using System;
using System.Linq;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using Microsoft.Extensions.Logging;

namespace PanoramaManager.Transport;

/// <summary>
/// Receives clicks as a console command the layout's script runs, e.g.
/// <c>hudmenu_myplugin &lt;token&gt; row3_btn [args...]</c>.
///
/// <para>Only usable if layout scripting is ever permitted on the sanctioned path - today it isn't,
/// so this exists so consumers don't have to be rewritten when it is.</para>
///
/// <para><b>This transport is spoofable.</b> Any player can type the command. The token is checked
/// against the caller's open session upstream in <see cref="PanelHandle"/>; consumers must still
/// authorise the action itself against the caller's admin flags.</para>
/// </summary>
public sealed class ConsoleCommandTransport : IPanelTransport
{
    private readonly BasePlugin _plugin;
    private readonly string     _command;
    private readonly ILogger    _logger;

    private bool _installed;

    public ConsoleCommandTransport(BasePlugin plugin, string command, ILogger logger)
    {
        _plugin  = plugin;
        _command = command;
        _logger  = logger;
    }

    public bool IsInstalled => _installed;

    public event Action<RawInteraction>? OnInteraction;

    public void Install()
    {
        if (_installed)
            return;

        _plugin.AddCommand(_command, "HudMenu interaction channel (internal).", Handle);
        _installed = true;

        _logger.LogInformation("[HudMenu] command transport installed as '{Command}'", _command);
    }

    public void Uninstall()
    {
        if (!_installed)
            return;

        _plugin.RemoveCommand(_command, Handle);
        _installed = false;
    }

    private void Handle(CCSPlayerController? player, CommandInfo command)
    {
        // Server console has no session to match, and this channel is only meaningful for a player.
        if (player is not { IsValid: true })
            return;

        if (command.ArgCount < 3)
            return;

        var token     = command.GetArg(1);
        var elementId = command.GetArg(2);

        var args = Enumerable.Range(3, Math.Max(0, command.ArgCount - 3))
            .Select(command.GetArg)
            .ToArray();

        OnInteraction?.Invoke(new RawInteraction(player, elementId, args, token));
    }
}
