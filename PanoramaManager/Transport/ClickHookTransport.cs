using System;
using System.Runtime.InteropServices;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using PanoramaManager.Internal;
using Microsoft.Extensions.Logging;

namespace PanoramaManager.Transport;

/// <summary>
/// Catches HUD button clicks by detouring the receiver that the inbound <c>CS_UM_CustomHudClicked</c>
/// (net msg 390) dispatch always calls, with the entity handle already <c>dynamic_cast</c>-verified
/// and the button id already decoded. Chain: usermsg recv -> id switch -> here.
///
/// <para><b>Not the Pulse output.</b> An earlier version hooked the function that fires the
/// <c>OnCustomHudClicked</c> Pulse output. That is a <c>CCSScript_EntityScript</c> vtable slot only
/// reached when the level contains a <c>point_script</c>-family entity, so on an ordinary map it is
/// never called and no click is ever seen. The receiver below runs on every click regardless of what
/// the map contains.</para>
///
/// <para>Argument positions are the same on both platforms - SysV puts them in rdi/rsi/rdx/rcx and
/// Win64 in rcx/rdx/r8/r9, but either way it is args 0..3:</para>
/// <list type="bullet">
/// <item>arg 0 - singleton, ignored</item>
/// <item>arg 1 - <c>CBasePlayerController*</c> of the clicker, or null</item>
/// <item>arg 2 - <c>CCSCustomHudLayout*</c> that was clicked</item>
/// <item>arg 3 - <c>std::string*</c> button id</item>
/// </list>
/// </summary>
public sealed class ClickHookTransport : IPanelTransport
{
    private const string SignatureKey = "CCSCustomHudLayout_CustomHudClickedReceiver";

    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private readonly ILogger _logger;

    private MemoryFunctionVoid<IntPtr, IntPtr, IntPtr, IntPtr>? _function;

    public ClickHookTransport(ILogger logger)
        => _logger = logger;

    public bool IsInstalled => _function is not null;

    public event Action<RawInteraction>? OnInteraction;

    public void Install()
    {
        if (_function is not null)
            return;

        if (PanoramaGameData.Signature(SignatureKey) is not { } signature)
        {
            _logger.LogDebug("[Panorama] no click-receiver signature for this platform - clicks won't be reported.");

            return;
        }

        var function = new MemoryFunctionVoid<IntPtr, IntPtr, IntPtr, IntPtr>(signature);

        if (function.Handle == IntPtr.Zero)
        {
            _logger.LogWarning(
                "[Panorama] click receiver did not resolve - menus will render but clicks won't be "
                + "reported. Re-derive the signature after a CS2 update; see gamedata/panoramamanager.json.");

            return;
        }

        function.Hook(OnClicked, HookMode.Pre);
        _function = function;

        _logger.LogDebug("[Panorama] click transport installed @ 0x{Target:X}", function.Handle);
    }

    public void Uninstall()
    {
        if (_function is not { } function)
            return;

        function.Unhook(OnClicked, HookMode.Pre);
        _function = null;
    }

    private HookResult OnClicked(DynamicHook hook)
    {
        try
        {
            var controller = hook.GetParam<IntPtr>(1);
            var layout     = hook.GetParam<IntPtr>(2);
            var elementId  = ReadStdString(hook.GetParam<IntPtr>(3));

            if (elementId is { Length: > 0 })
            {
                var player = controller != IntPtr.Zero ? new CCSPlayerController(controller) : null;

                OnInteraction?.Invoke(new RawInteraction(
                    player is { IsValid: true } ? player : null,
                    elementId,
                    Array.Empty<string>(),
                    Token: null,
                    Layout: layout));
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "[Panorama] click transport handler threw");
        }

        return HookResult.Continue;
    }

    /// <summary>
    /// Reads a <c>std::string</c>, which is laid out differently per toolchain.
    ///
    /// <para>libstdc++ keeps the <c>char*</c> in the first 8 bytes for both the small-string and
    /// heap representations, so one read covers both. MSVC does not: its first 16 bytes are a union
    /// of an inline buffer and a pointer, and which one is live depends on the capacity at +0x18.
    /// A button id like <c>row0_btn</c> is short enough to live inline, so reading those 8 bytes as
    /// a pointer on Windows would dereference the text itself.</para>
    /// </summary>
    private static string? ReadStdString(IntPtr stdString)
    {
        if (stdString == IntPtr.Zero)
            return null;

        if (!IsWindows)
        {
            var dataPtr = Marshal.ReadIntPtr(stdString);

            return dataPtr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(dataPtr);
        }

        const int msvcCapacityOffset = 0x18;
        const int msvcInlineCapacity = 16;

        var capacity = Marshal.ReadInt64(stdString + msvcCapacityOffset);

        if (capacity < msvcInlineCapacity)
            return Marshal.PtrToStringUTF8(stdString);

        var heapPtr = Marshal.ReadIntPtr(stdString);

        return heapPtr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(heapPtr);
    }
}
