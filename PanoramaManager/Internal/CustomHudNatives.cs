using System;
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

    private MemoryFunctionVoid<IntPtr, IntPtr, IntPtr, IntPtr>?         _setDialogVar;
    private MemoryFunctionVoid<IntPtr, IntPtr, IntPtr, bool>?           _setHasClass;
    private MemoryFunctionVoid<IntPtr, uint, IntPtr, IntPtr, bool>?     _setHasClassForPlayer;
    private MemoryFunctionVoid<IntPtr, uint, bool>?                     _setInputCapture;
    private MemoryFunctionWithReturn<IntPtr, IntPtr, IntPtr, bool>?     _internPanelId;
    private MemoryFunctionWithReturn<IntPtr, IntPtr, IntPtr, bool>?     _internVarName;
    private MemoryFunctionVoid<IntPtr, uint, uint, IntPtr>?             _writeDialogVar;

    private int  _countOffset;
    private int  _baseOffset;
    private int  _stride;
    private bool _resolved;
    private bool _strideVerifiedBad;

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
    /// A <c>CUtlString</c> the natives can take the address of. The engine's type is
    /// <c>Size=8 { char* }</c>, so this is two allocations: the char buffer, and the 8-byte struct
    /// that points at it. The natives deep-copy, so both are ours to free once the call returns.
    /// </summary>
    private readonly struct UtlString : IDisposable
    {
        private readonly IntPtr _chars;

        public IntPtr Ptr { get; }

        public UtlString(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);

            _chars = Marshal.AllocHGlobal(bytes.Length + 1);
            Marshal.Copy(bytes, 0, _chars, bytes.Length);
            Marshal.WriteByte(_chars, bytes.Length, 0);

            Ptr = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(Ptr, _chars);
        }

        public void Dispose()
        {
            Marshal.FreeHGlobal(Ptr);
            Marshal.FreeHGlobal(_chars);
        }
    }

    private void Resolve()
    {
        if (_resolved)
            return;

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

        _logger.LogInformation("[HudMenu] gamedata: {Source}", PanoramaGameData.Source);
        _logger.LogInformation(
            "[HudMenu] natives - DVar=0x{DVar:X} HCls=0x{HCls:X} HClsP=0x{HClsP:X} Input=0x{Input:X} "
            + "InternPanel=0x{IP:X} InternVar=0x{IV:X} WriteVar=0x{WV:X} | states +0x{Count:X}/+0x{Base:X} stride 0x{Stride:X}",
            _setDialogVar?.Handle         ?? IntPtr.Zero,
            _setHasClass?.Handle          ?? IntPtr.Zero,
            _setHasClassForPlayer?.Handle ?? IntPtr.Zero,
            _setInputCapture?.Handle      ?? IntPtr.Zero,
            _internPanelId?.Handle        ?? IntPtr.Zero,
            _internVarName?.Handle        ?? IntPtr.Zero,
            _writeDialogVar?.Handle       ?? IntPtr.Zero,
            _countOffset, _baseOffset, _stride);
    }

    /// <summary>Binds a signature from gamedata, or null if it is missing or didn't resolve.
    /// BaseMemoryFunction swallows a resolve failure and leaves Handle at 0, so check it here rather
    /// than at every call site.</summary>
    private T? Bind<T>(string key) where T : BaseMemoryFunction
    {
        if (PanoramaGameData.Signature(key) is not { } signature)
        {
            _logger.LogWarning("[HudMenu] no signature for {Key} on this platform.", key);

            return null;
        }

        try
        {
            var fn = (T) Activator.CreateInstance(typeof(T), signature)!;

            if (fn.Handle != IntPtr.Zero)
                return fn;

            _logger.LogWarning("[HudMenu] {Key} did not resolve.", key);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "[HudMenu] {Key} failed to bind.", key);
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
                _logger.LogInformation(
                    "[HudMenu] stride 0x{Stride:X} confirmed against schema {Class}", _stride, candidate);
            }
            else
            {
                _logger.LogError(
                    "[HudMenu] stride mismatch: gamedata says 0x{Configured:X}, schema {Class} says "
                    + "0x{Actual:X}. Per-player writes DISABLED - they would compute a wrong address. "
                    + "Update CCSCustomHudLayout_PlayerStateStride in gamedata/panoramamanager.json.",
                    _stride, candidate, actual);

                _strideVerifiedBad = true;
            }

            return;
        }

        _logger.LogInformation(
            "[HudMenu] stride 0x{Stride:X} could not be schema-verified - no known state class. "
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
    public bool SetDialogVariableString(IntPtr entity, string panelId, string variableName, string value)
    {
        Resolve();

        if (_setDialogVar is not { } fn)
            return false;

        using var pPanel = new UtlString(panelId);
        using var pName  = new UtlString(variableName);
        using var pValue = new UtlString(value);

        fn.Invoke(entity, pPanel.Ptr, pName.Ptr, pValue.Ptr);

        return true;
    }

    /// <summary>Global-state class toggle.</summary>
    public bool SetHasClass(IntPtr entity, string panelId, string className, bool hasClass)
    {
        Resolve();

        if (_setHasClass is not { } fn)
            return false;

        using var pPanel = new UtlString(panelId);
        using var pClass = new UtlString(className);

        fn.Invoke(entity, pPanel.Ptr, pClass.Ptr, hasClass);

        return true;
    }

    /// <summary>
    /// Per-player text injection, hand-driven. Interns the panel id and variable name into the
    /// entity's tables - the same tables the global setter fills - then writes the value straight
    /// into that slot's state. The engine's own <c>...ForPlayer</c> entry point is skipped because it
    /// never stores the value; see the class remarks.
    /// </summary>
    public bool SetDialogVariableStringForPlayer(IntPtr entity, uint slot, string panelId, string variableName, string value)
    {
        Resolve();

        if (_internPanelId is not { } internPanel
            || _internVarName is not { } internVar
            || _writeDialogVar is not { } write)
            return false;

        var state = PlayerState(entity, slot);

        if (state == IntPtr.Zero)
            return false;

        using var pPanel = new UtlString(panelId);
        using var pName  = new UtlString(variableName);
        using var pValue = new UtlString(value);

        var index = Marshal.AllocHGlobal(sizeof(ushort));

        try
        {
            if (!internPanel.Invoke(entity, pPanel.Ptr, index))
                return false;

            var panelIndex = (uint) (ushort) Marshal.ReadInt16(index);

            if (!internVar.Invoke(entity, pName.Ptr, index))
                return false;

            var variableIndex = (uint) (ushort) Marshal.ReadInt16(index);

            write.Invoke(state, panelIndex, variableIndex, pValue.Ptr);

            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(index);
        }
    }

    /// <summary>Per-player class toggle. This one's engine entry point works, so it is called
    /// directly.</summary>
    public bool SetHasClassForPlayer(IntPtr entity, uint slot, string panelId, string className, bool hasClass)
    {
        Resolve();

        if (_setHasClassForPlayer is not { } fn)
            return false;

        using var pPanel = new UtlString(panelId);
        using var pClass = new UtlString(className);

        fn.Invoke(entity, slot, pPanel.Ptr, pClass.Ptr, hasClass);

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
