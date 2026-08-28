using System;
using System.Linq;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;

namespace PanoramaManager.Example;

/// <summary>
/// Worked example: a two-level admin menu. Player list, click a player, act on them.
/// This is the shape a real admin system would take.
/// </summary>
public sealed class AdminMenuPlugin : BasePlugin
{
    public override string ModuleName        => "PanoramaManager Example";
    public override string ModuleVersion     => "0.1.0";
    public override string ModuleAuthor      => "PanoramaManager";
    public override string ModuleDescription => "Admin menu built on PanoramaManager.";

    private const string Layout = "panorama/layout/custom_game/admin_hud.vxml_c";

    private PanelHandle? _menu;
    private Timer?      _clock;

    public override void Load(bool hotReload)
    {
        Panorama.Init(this);

        if (!Panorama.CanReceiveClicks)
            Logger.LogWarning("[Example] PanoramaManager has no click channel - the menu will render but not respond.");

        // One handle, reused. Content is rebuilt per open, so two admins browsing at once each keep
        // their own page while sharing the row set.
        _menu = Panorama.Spawn(Layout);
        _menu.Title = "Admin";
        _menu.OnEvent += OnMenuEvent;

        // Live values are just variables - push a new string and the layout updates in place.
        _clock = AddTimer(1.0f, PushUptime, TimerFlags.REPEAT);
    }

    public override void Unload(bool hotReload)
    {
        _clock?.Kill();
        _menu?.Dispose();
        Panorama.Shutdown();
    }

    [ConsoleCommand("css_admin", "Open the admin menu.")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    [RequiresPermissions("@css/generic")]
    public void OnAdminCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player is not { IsValid: true } || _menu is null)
            return;

        ShowPlayerList(player);
    }

    private void ShowPlayerList(CCSPlayerController viewer)
    {
        if (_menu is null)
            return;

        _menu.Title = "Admin - Players";

        // Bots are included deliberately: they are the only other bodies on a test server, and an
        // admin menu that cannot target a bot is useless for kick/slay testing.
        //
        // NOTE: the "(You)" marker is per-viewer text, so it is only correct while a single admin
        // has the menu open. Panorama.UseGlobalDialogVariables is on by default because the
        // per-player dialog-variable signature is broken on this build, which makes every viewer
        // share one set of strings - the last admin to open it decides whose row says "(You)".
        // That resolves itself the moment the signature is fixed and the flag goes false.
        _menu.SetItems(Utilities.GetPlayers()
            .Where(p => p is { IsValid: true, IsHLTV: false })
            .Select(p => new MenuItem(
                Id:       $"player:{p.Slot}",
                Title:    p.Slot == viewer.Slot ? $"{p.PlayerName} (You)" : p.PlayerName,
                Subtitle: p.IsBot ? "BOT" : $"{p.Ping} ms - {p.Score} score",
                OnSelect: e => ShowActionsFor(e.Player, p.Slot),
                Tag:      p.Slot)));

        _menu.Open(viewer);
    }

    private void ShowActionsFor(CCSPlayerController viewer, int targetSlot)
    {
        if (_menu is null)
            return;

        var target = Utilities.GetPlayerFromSlot(targetSlot);
        if (target is not { IsValid: true })
        {
            ShowPlayerList(viewer);
            return;
        }

        _menu.Title = $"Admin - {target.PlayerName}";
        // The action rides along with the row. No central switch to keep in sync with the ids, and
        // targetSlot is already in scope - the closure captures it instead of round-tripping it
        // through a string that has to be parsed back out.
        _menu.SetItems(
        [
            new MenuItem($"act:kick:{targetSlot}", "Kick", "Remove from the server", e => Act(e.Player, "kick", targetSlot)),
            new MenuItem($"act:slay:{targetSlot}", "Slay", "Kill immediately",       e => Act(e.Player, "slay", targetSlot)),
            new MenuItem("back",                   "Back", "Return to the player list", e => ShowPlayerList(e.Player)),
        ]);

        _menu.Refresh(viewer);
    }

    private void OnMenuEvent(PanelEvent e)
    {
        // One gate for the whole menu. OnEvent runs before any row's own callback, and cancelling
        // here stops it - so authorisation lives in exactly one place instead of being repeated in
        // every MenuItem, where forgetting it once is a privilege escalation.
        //
        // The transport is only as trustworthy as the channel underneath it. Do not assume the menu
        // could only have been opened by someone allowed to use it.
        if (e.Action == PanelAction.Click && !AdminManager.PlayerHasPermissions(e.Player, "@css/generic"))
        {
            e.Cancel = true;

            Logger.LogWarning(
                "[Example] blocked {Player} from '{Element}' - missing @css/generic",
                e.Player.PlayerName, e.ElementId);
        }
    }

    private void Act(CCSPlayerController admin, string verb, int targetSlot)
    {
        var target = Utilities.GetPlayerFromSlot(targetSlot);
        if (target is not { IsValid: true })
            return;

        switch (verb)
        {
            case "kick":
                Server.ExecuteCommand($"kickid {target.UserId}");
                break;

            case "slay":
                target.PlayerPawn.Value?.CommitSuicide(false, true);
                break;
        }

        admin.PrintToChat($" \u0004[Admin]\u0001 {verb} on {target.PlayerName}");
        ShowPlayerList(admin);
    }

    private void PushUptime()
        => _menu?.SetVariable("uptime", TimeSpan.FromSeconds(Server.CurrentTime).ToString(@"hh\:mm\:ss"));
}
