using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using Microsoft.Extensions.Logging;

namespace PanoramaManager.Internal;

/// <summary>
/// Native bridge to <c>CCSCustomHudLayout</c>. These are the engine's own impls that intern
/// panel-id / class-name / variable-name into the entity's index tables and fire
/// NetworkStateChanged - doing that by hand from managed code would mean growing a nested
/// <c>CNetworkUtlVectorBase</c> and dispatching state changes, so we call the engine instead.
///
/// <para>Signatures and offsets come from <see cref="PanoramaGameData"/>: the server's
/// <c>gamedata/panoramamanager.json</c> if present, compiled-in defaults otherwise. Windows and Linux are
/// both covered.</para>
///
/// <para><b>Per-player dialog variables do not use the obvious function.</b>
/// <c>SetDialogVariableStringForPlayer</c> exists and resolves, but on this build it jumps to a
/// writer that never stores the value - confirmed live, a global write lands and a per-player write
/// with a different value does not overwrite it. The working path is the one the engine's own global
/// setter takes: intern the panel id and variable name into the entity's tables, then write the
/// value into the target slot's state. That is what <see cref="SetDialogVariableStringForPlayer"/>
/// below actually does.</para>
/// </summary>
internal sealed class CustomHudNatives
{
    private const string KeySetDialogVariableString  = "CCSCustomHudLayout_SetDialogVariableString";
    private const string KeySetHasClass              = "CCSCustomHudLayout_SetHasClass";
    private const string KeySetHasClassForPlayer     = "CCSCustomHudLayout_SetHasClassForPlayer";
    private const string KeySetInputCaptureEnabled   = "CCSCustomHudLayout_SetInputCaptureEnabled";
    private const string KeyInternPanelId            = "CCSCustomHudLayout_InternPanelId";
    private const string KeyInternDialogVarName      = "CCSCustomHudLayout_InternDialogVarName";
    private const string KeyWriteDialogVariable      = "CCSCustomHudLayout_WriteDialogVariableToState";

    private const string KeyStatesCount  = "CCSCustomHudLayout_PlayerStatesCount";
    private const string KeyStatesBase   = "CCSCustomHudLayout_PlayerStatesBase";
    private const string KeyStateStride  = "CCSCustomHudLayout_PlayerStateStride";

    /// <summary>A count read out of the entity that exceeds this is taken as a bad offset rather
    /// than a real player count, and the per-player write is abandoned. Cheap insurance: without it,
    /// a wrong offset turns into a wild pointer write.</summary>
    private const int MaxPlausibleSlots = 128;

    private readonly ILogger _logger;

    private static MemoryFunctionVoid<IntPtr, IntPtr, IntPtr, IntPtr>?         _setDialogVar;
    private static MemoryFunctionVoid<IntPtr, IntPtr, IntPtr, bool>?           _setHasClass;
    private static MemoryFunctionVoid<IntPtr, uint, IntPtr, IntPtr, bool>?     _setHasClassForPlayer;
    private static MemoryFunctionVoid<IntPtr, uint, bool>?                     _setInputCapture;
    private static MemoryFunctionWithReturn<IntPtr, IntPtr, IntPtr, bool>?     _internPanelId;
    private static MemoryFunctionWithReturn<IntPtr, IntPtr, IntPtr, bool>?     _internVarName;
    private static MemoryFunctionVoid<IntPtr, uint, uint, IntPtr>?             _writeDialogVar;

    private static int  _countOffset;
    private static int  _baseOffset;
    private static int  _stride;
    private static bool _resolved;
    private static bool _strideVerifiedBad;

    public CustomHudNatives(ILogger logger)
        => _logger = logger;

    /// <summary>True once every native needed for per-player dialog variables is bound.</summary>
    public bool CanWritePerPlayerText
    {
        get
        {
            Resolve();

            return _internPanelId is not null && _internVarName is not null && _writeDialogVar is not null;
        }
    }

    /// <summary>
    /// Longest string written without touching the heap. Panel ids, class names and weapon labels
    /// are all far short of this; a player name or a footer line is the realistic worst case.
    /// </summary>
    private const int InlineBytes = 256;

    /// <summary>
    /// Writes <paramref name="value"/> as a null-terminated UTF-8 string into <paramref name="inline"/>,
    /// falling back to the heap only if it does not fit.
    ///
    /// <para>The engine's <c>CUtlString</c> is <c>Size=8 { char* }</c> - verified against the
    /// disassembly, where the setter does <c>mov rcx, [rsi]</c> and then walks the string. It reads
    /// and hashes; it never stores the pointer or takes ownership, so the memory only has to outlive
    /// the call.</para>
    ///
    /// <para>That is what makes stack memory safe here, and it matters: a full grid render was doing
    /// roughly 2,800 <c>AllocHGlobal</c>/<c>FreeHGlobal</c> pairs, every one of them on the game
    /// thread. Now it does none in the common case.</para>
    /// </summary>
    private static unsafe byte* Encode(string? value, byte* inline, ref IntPtr heap)
    {
        value ??= string.Empty;

        // GetMaxByteCount rather than GetByteCount: cheaper, and being generous only costs stack.
        var needed = Encoding.UTF8.GetMaxByteCount(value.Length) + 1;

        if (needed <= InlineBytes)
        {
            var count = Encoding.UTF8.GetBytes(value, new Span<byte>(inline, InlineBytes - 1));
            inline[count] = 0;

            return inline;
        }

        heap = Marshal.AllocHGlobal(needed);

        var written = Encoding.UTF8.GetBytes(value, new Span<byte>((void*) heap, needed - 1));
        ((byte*) heap)[written] = 0;

        return (byte*) heap;
    }

    /// <summary>Frees whatever <see cref="Encode"/> put on the heap. Almost always a no-op.</summary>
    private static void Release(IntPtr heap)
    {
        if (heap != IntPtr.Zero)
            Marshal.FreeHGlobal(heap);
    }

    private static readonly object ResolveLock = new();

    /// <summary>
    /// Binds every native once per load context.
    ///
    /// <para>The state behind this is static on purpose. It describes the running server binary, not
    /// any one menu, and resolving it means seven pattern scans over the module - about 28ms on the
    /// game thread. It used to be per-instance, and a renderer is created per <c>Panorama.Spawn</c>,
    /// so every menu paid that scan again the first time it drew: a visible hitch on the first
    /// toast, and another on the first weapon menu. Now Init's resolve at plugin load is the only
    /// one, and opening a menu costs nothing.</para>
    /// </summary>
    private void Resolve()
    {
        if (_resolved)
            return;

        lock (ResolveLock)
        {
            if (_resolved)
                return;

            ResolveCore();
        }
    }

    private void ResolveCore()
    {
        _resolved = true;

        _setDialogVar         = Bind<MemoryFunctionVoid<IntPtr, IntPtr, IntPtr, IntPtr>>(KeySetDialogVariableString);
        _setHasClass          = Bind<MemoryFunctionVoid<IntPtr, IntPtr, IntPtr, bool>>(KeySetHasClass);
        _setHasClassForPlayer = Bind<MemoryFunctionVoid<IntPtr, uint, IntPtr, IntPtr, bool>>(KeySetHasClassForPlayer);
        _setInputCapture      = Bind<MemoryFunctionVoid<IntPtr, uint, bool>>(KeySetInputCaptureEnabled);
        _internPanelId        = Bind<MemoryFunctionWithReturn<IntPtr, IntPtr, IntPtr, bool>>(KeyInternPanelId);
        _internVarName        = Bind<MemoryFunctionWithReturn<IntPtr, IntPtr, IntPtr, bool>>(KeyInternDialogVarName);
        _writeDialogVar       = Bind<MemoryFunctionVoid<IntPtr, uint, uint, IntPtr>>(KeyWriteDialogVariable);

        _countOffset = PanoramaGameData.Offset(KeyStatesCount);
        _baseOffset  = PanoramaGameData.Offset(KeyStatesBase);
        _stride      = PanoramaGameData.Offset(KeyStateStride);

        VerifyStride();

// Nothing is logged on a healthy start. Every plugin referencing this library loads its
        // own copy in its own context, so anything printed here is printed once per plugin - which
        // is how a working server ends up with a screenful of identical startup noise.
        // `css_panorama_diag` prints the full table on demand instead.
        Announce();
    }

    /// <summary>The full native table, for <c>css_panorama_diag</c>. This used to be printed at
    /// every plugin load; on demand is the right place for it.</summary>
    internal IEnumerable<string> Describe()
    {
        Resolve();

        yield return $"gamedata: {PanoramaGameData.Source}";
        yield return $"natives:  DVar=0x{Addr(_setDialogVar):X} HCls=0x{Addr(_setHasClass):X} "
                   + $"HClsP=0x{Addr(_setHasClassForPlayer):X} Input=0x{Addr(_setInputCapture):X}";
        yield return $"          InternPanel=0x{Addr(_internPanelId):X} InternVar=0x{Addr(_internVarName):X} "
                   + $"WriteVar=0x{Addr(_writeDialogVar):X}";
        yield return $"states:   +0x{_countOffset:X}/+0x{_baseOffset:X} stride 0x{_stride:X}"
                   + (_strideVerifiedBad ? " (MISMATCH - per-player writes disabled)" : "");

        if (_unresolved.Count > 0)
            yield return $"unresolved: {string.Join(", ", _unresolved)}";
    }

    private static IntPtr Addr(BaseMemoryFunction? fn) => fn?.Handle ?? IntPtr.Zero;

    private static readonly List<string> _unresolved = [];

    /// <summary>Set once per plugin load context, so a plugin that opens menus repeatedly does not
    /// reprint the same complaint.</summary>
    private static bool _announced;

    /// <summary>
    /// The whole startup output. Silent when everything works, which is the normal case.
    ///
    /// <para>Two things are worth interrupting a server operator for, and neither is "it loaded".
    /// One is a missing gamedata file, because the compiled-in fallback is a snapshot that a CS2
    /// update will quietly invalidate. The other is a signature that did not resolve, because the
    /// menu will render and then do nothing.</para>
    /// </summary>
    private void Announce()
    {
        if (_announced)
            return;

        _announced = true;

        if (!PanoramaGameData.FileFound)
        {
            _logger.LogError(
                "[Panorama] no {File} in addons/counterstrikesharp/gamedata - running on compiled-in "
                + "signatures, which are a snapshot of one CS2 build and will stop working after an "
                + "update. Download it from {Url} and drop it in that folder.",
                "panoramamanager.json",
                "https://github.com/Next-il/PanoramaManager/releases/latest");
        }

        if (_unresolved.Count > 0)
        {
            _logger.LogError(
                "[Panorama] {Count} native(s) did not resolve ({Keys}) - menus will render but "
                + "misbehave. The signatures need re-deriving for this CS2 build; update {File} from "
                + "{Url}.",
                _unresolved.Count,
                string.Join(", ", _unresolved),
                "panoramamanager.json",
                "https://github.com/Next-il/PanoramaManager/releases/latest");
        }
    }

    /// <summary>Binds a signature from gamedata, or null if it is missing or didn't resolve.
    /// BaseMemoryFunction swallows a resolve failure and leaves Handle at 0, so check it here rather
    /// than at every call site.</summary>
    private T? Bind<T>(string key) where T : BaseMemoryFunction
    {
        if (PanoramaGameData.Signature(key) is not { } signature)
        {
            _unresolved.Add(key);

            return null;
        }

        try
        {
            var fn = (T) Activator.CreateInstance(typeof(T), signature)!;

            if (fn.Handle != IntPtr.Zero)
                return fn;

            _unresolved.Add(key);
        }
        catch (Exception e)
        {
            _unresolved.Add(key);
            _logger.LogDebug(e, "[Panorama] {Key} failed to bind.", key);
        }

        return null;
    }

    /// <summary>
    /// Cross-checks the configured stride against the schema, and refuses per-player writes if they
    /// disagree.
    ///
    /// <para>This is the one number that can corrupt memory. Everything else either resolves or does
    /// not; the stride is used to compute an address, so a wrong one writes real data to the wrong
    /// place - and a CS2 update that adds a field to the state struct changes it silently, with no
    /// signature failing to warn us. It already moved once, 0x1A0 to 0x198.</para>
    ///
    /// <para>The schema knows the element's size by name, which survives updates. If it disagrees we
    /// disable the per-player path rather than trust a stale constant: the menu falls back to shared
    /// text, which is a visible limitation instead of an invisible corruption.</para>
    /// </summary>
    private void VerifyStride()
    {
        foreach (var candidate in new[] { "CCSCustomHudLayoutState", "CustomHudLayoutState" })
        {
            int actual;

            try
            {
                actual = Schema.GetClassSize(candidate);
            }
            catch
            {
                continue;
            }

            if (actual <= 0)
                continue;

            if (actual == _stride)
            {
                _logger.LogDebug(
                    "[Panorama] stride 0x{Stride:X} confirmed against schema {Class}", _stride, candidate);
            }
            else
            {
                _logger.LogError(
                    "[Panorama] stride mismatch: gamedata says 0x{Configured:X}, schema {Class} says "
                    + "0x{Actual:X}. Per-player writes DISABLED - they would compute a wrong address. "
                    + "Update CCSCustomHudLayout_PlayerStateStride in gamedata/panoramamanager.json.",
                    _stride, candidate, actual);

                _strideVerifiedBad = true;
            }

            return;
        }

        _logger.LogDebug(
            "[Panorama] stride 0x{Stride:X} could not be schema-verified - no known state class. "
            + "Proceeding on the gamedata value.", _stride);
    }

    /// <summary>
    /// Address of <c>m_vecPlayerLayoutStates[slot]</c>, or zero if that slot has no allocated state.
    /// Replicates the engine's own bounds check, plus a sanity ceiling on the count so a wrong
    /// offset produces a refusal rather than a wild write.
    /// </summary>
    private IntPtr PlayerState(IntPtr entity, uint slot)
    {
        if (entity == IntPtr.Zero || _baseOffset == 0 || _stride == 0 || _strideVerifiedBad)
            return IntPtr.Zero;

        try
        {
            var count = Marshal.ReadInt32(entity + _countOffset);

            if (count <= 0 || count > MaxPlausibleSlots || slot >= (uint) count)
                return IntPtr.Zero;

            var basePtr = Marshal.ReadIntPtr(entity + _baseOffset);

            return basePtr == IntPtr.Zero ? IntPtr.Zero : basePtr + (int) (slot * (uint) _stride);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>Global-state text injection - every viewer of the layout sees it.</summary>
    // The stackalloc buffers below are fully written by Encode before anything reads them,
    // so the implicit zeroing is pure waste at ~1,400 calls a render.
    [SkipLocalsInit]
    public unsafe bool SetDialogVariableString(IntPtr entity, string panelId, string variableName, string value)
    {
        Resolve();

        if (_setDialogVar is not { } fn)
            return false;

        // Three char buffers and three 8-byte CUtlStrings, all on the stack.
        byte* c0 = stackalloc byte[InlineBytes];
        byte* c1 = stackalloc byte[InlineBytes];
        byte* c2 = stackalloc byte[InlineBytes];
        byte** utl = stackalloc byte*[3];

        IntPtr h0 = IntPtr.Zero, h1 = IntPtr.Zero, h2 = IntPtr.Zero;

        try
        {
            utl[0] = Encode(panelId, c0, ref h0);
            utl[1] = Encode(variableName, c1, ref h1);
            utl[2] = Encode(value, c2, ref h2);

            fn.Invoke(entity, (IntPtr) (utl + 0), (IntPtr) (utl + 1), (IntPtr) (utl + 2));
        }
        finally
        {
            Release(h0);
            Release(h1);
            Release(h2);
        }

        return true;
    }

    /// <summary>Global-state class toggle.</summary>
    // The stackalloc buffers below are fully written by Encode before anything reads them,
    // so the implicit zeroing is pure waste at ~1,400 calls a render.
    [SkipLocalsInit]
    public unsafe bool SetHasClass(IntPtr entity, string panelId, string className, bool hasClass)
    {
        Resolve();

        if (_setHasClass is not { } fn)
            return false;

        byte* c0 = stackalloc byte[InlineBytes];
        byte* c1 = stackalloc byte[InlineBytes];
        byte** utl = stackalloc byte*[2];

        IntPtr h0 = IntPtr.Zero, h1 = IntPtr.Zero;

        try
        {
            utl[0] = Encode(panelId, c0, ref h0);
            utl[1] = Encode(className, c1, ref h1);

            fn.Invoke(entity, (IntPtr) (utl + 0), (IntPtr) (utl + 1), hasClass);
        }
        finally
        {
            Release(h0);
            Release(h1);
        }

        return true;
    }

    /// <summary>
    /// Per-player text injection, hand-driven. Interns the panel id and variable name into the
    /// entity's tables - the same tables the global setter fills - then writes the value straight
    /// into that slot's state. The engine's own <c>...ForPlayer</c> entry point is skipped because it
    /// never stores the value; see the class remarks.
    /// </summary>
    // The stackalloc buffers below are fully written by Encode before anything reads them,
    // so the implicit zeroing is pure waste at ~1,400 calls a render.
    [SkipLocalsInit]
    public unsafe bool SetDialogVariableStringForPlayer(IntPtr entity, uint slot, string panelId, string variableName, string value)
    {
        Resolve();

        if (_internPanelId is not { } internPanel
            || _internVarName is not { } internVar
            || _writeDialogVar is not { } write)
            return false;

        var state = PlayerState(entity, slot);

        if (state == IntPtr.Zero)
            return false;

        // This is the hot one - every tile of a grid goes through here.
        byte* c0 = stackalloc byte[InlineBytes];
        byte* c1 = stackalloc byte[InlineBytes];
        byte* c2 = stackalloc byte[InlineBytes];
        byte** utl = stackalloc byte*[3];

        // The intern functions write a ushort back through this.
        ushort* index = stackalloc ushort[1];

        IntPtr h0 = IntPtr.Zero, h1 = IntPtr.Zero, h2 = IntPtr.Zero;

        try
        {
            utl[0] = Encode(panelId, c0, ref h0);
            utl[1] = Encode(variableName, c1, ref h1);
            utl[2] = Encode(value, c2, ref h2);

            if (!internPanel.Invoke(entity, (IntPtr) (utl + 0), (IntPtr) index))
                return false;

            var panelIndex = (uint) *index;

            if (!internVar.Invoke(entity, (IntPtr) (utl + 1), (IntPtr) index))
                return false;

            var variableIndex = (uint) *index;

            write.Invoke(state, panelIndex, variableIndex, (IntPtr) (utl + 2));

            return true;
        }
        finally
        {
            Release(h0);
            Release(h1);
            Release(h2);
        }
    }

    /// <summary>Per-player class toggle. This one's engine entry point works, so it is called
    /// directly.</summary>
    // The stackalloc buffers below are fully written by Encode before anything reads them,
    // so the implicit zeroing is pure waste at ~1,400 calls a render.
    [SkipLocalsInit]
    public unsafe bool SetHasClassForPlayer(IntPtr entity, uint slot, string panelId, string className, bool hasClass)
    {
        Resolve();

        if (_setHasClassForPlayer is not { } fn)
            return false;

        byte* c0 = stackalloc byte[InlineBytes];
        byte* c1 = stackalloc byte[InlineBytes];
        byte** utl = stackalloc byte*[2];

        IntPtr h0 = IntPtr.Zero, h1 = IntPtr.Zero;

        try
        {
            utl[0] = Encode(panelId, c0, ref h0);
            utl[1] = Encode(className, c1, ref h1);

            fn.Invoke(entity, slot, (IntPtr) (utl + 0), (IntPtr) (utl + 1), hasClass);
        }
        finally
        {
            Release(h0);
            Release(h1);
        }

        return true;
    }

    /// <summary>Enable/disable input capture for a slot - required for that player's HUD to receive
    /// mouse input, i.e. for Buttons to be clickable and for a cursor to appear.</summary>
    public bool SetInputCaptureEnabled(IntPtr entity, uint slot, bool enabled)
    {
        Resolve();

        if (_setInputCapture is not { } fn)
            return false;

        fn.Invoke(entity, slot, enabled);

        return true;
    }
}
