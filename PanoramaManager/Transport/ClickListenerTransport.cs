using System;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;

namespace PanoramaManager.Transport;

/// <summary>
/// Receives HUD button clicks through CounterStrikeSharp's <c>OnCustomHudClicked</c> listener.
///
/// <para>CounterStrikeSharp hooks <c>ClientSvcUserMessage</c> itself and dispatches
/// <c>CS_UM_CustomHudClicked</c> to managed code with the clicker, the layout entity and the button
/// id already decoded. This used to be a signature-scanned detour on the engine's own click
/// receiver, plus a hand-written <c>std::string</c> reader with a separate layout for each
/// toolchain; none of that survives a CS2 update, and none of it is needed any more.</para>
///
/// <para>Unspoofable - the controller comes from the engine, not from anything the client can
/// name.</para>
/// </summary>
public sealed class ClickListenerTransport : IPanelTransport
{
    private readonly BasePlugin _plugin;
    private readonly ILogger    _logger;

    private Listeners.OnCustomHudClicked? _handler;

    public ClickListenerTransport(BasePlugin plugin, ILogger logger)
    {
        _plugin = plugin;
        _logger = logger;
    }

    public bool IsInstalled => _handler is not null;

    public event Action<RawInteraction>? OnInteraction;

    public void Install()
    {
        if (_handler is not null)
            return;

        _handler = OnClicked;
        _plugin.RegisterListener(_handler);

        _logger.LogDebug("[Panorama] click transport installed (OnCustomHudClicked)");
    }

    public void Uninstall()
    {
        if (_handler is not { } handler)
            return;

        _plugin.RemoveListener(handler);
        _handler = null;
    }

    private void OnClicked(CCSPlayerController player, CCSCustomHudLayout layout, string buttonId)
    {
        try
        {
            if (string.IsNullOrEmpty(buttonId))
                return;

            OnInteraction?.Invoke(new RawInteraction(
                player is { IsValid: true } ? player : null,
                buttonId,
                Array.Empty<string>(),
                Token: null,
                Layout: layout?.Handle ?? IntPtr.Zero));
        }
        catch (Exception e)
        {
            _logger.LogError(e, "[Panorama] click transport handler threw");
        }
    }
}
