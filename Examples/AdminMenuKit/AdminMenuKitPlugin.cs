using System;
using System.Linq;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;

namespace PanoramaManager.ExampleKit;

/// <summary>
/// The same admin menu as <c>AdminMenu</c>, on the kit-styled layout.
///
/// <para>The point of having both is that the C# is nearly identical. Same contract, same rows,
/// same callbacks - the skin is a layout path and a <see cref="LayoutContract"/> with a
/// <see cref="LayoutContract.RevealClass"/> set. What this one adds is the two things the kit design
/// makes possible: an animated entry, and an accent colour the server drives.</para>
/// </summary>
public sealed class AdminMenuKitPlugin : BasePlugin
{
    public override string ModuleName        => "PanoramaManager Example (Kit)";
    public override string ModuleVersion     => "0.1.0";
    public override string ModuleAuthor      => "PanoramaManager";
    public override string ModuleDescription => "Admin menu on the kit-styled layout.";

    private const string Layout = "panorama/layout/custom_game/admin_hud_kit.vxml_c";

    /// <summary>
    /// Identical to the default except for the reveal. <c>admin_hud_kit.xml</c> keeps its root in
    /// layout at opacity 0 rather than collapsing it, so the library toggles <c>show</c> instead of
    /// <c>hidden</c> and the CSS transition plays on the way in and the way out.
    /// </summary>
    private static readonly LayoutContract Contract = new() { RevealClass = "show" };

    private PanelHandle? _menu;
    private Timer?      _clock;

    public override void Load(bool hotReload)
    {
        Panorama.Init(this);

        if (!Panorama.CanReceiveClicks)
            Logger.LogWarning("[ExampleKit] no click channel - the menu will render but not respond.");

        _menu = Panorama.Spawn(Layout, Contract);
        _menu.OnEvent += OnMenuEvent;

        _clock = AddTimer(1.0f, PushUptime, TimerFlags.REPEAT);
    }

    public override void Unload(bool hotReload)
    {
        _clock?.Kill();
        _menu?.Dispose();
        Panorama.Shutdown();
    }

    [ConsoleCommand("css_adminkit", "Open the kit-styled admin menu.")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    [RequiresPermissions("@css/generic")]
    public void OnAdminCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player is { IsValid: true } && _menu is not null)
            ShowPlayerList(player);
    }

    private void ShowPlayerList(CCSPlayerController viewer)
    {
        if (_menu is null)
            return;

        _menu.Title    = "Admin";
        _menu.Subtitle = "Players";

        // Neutral accent for a list - nothing here is destructive yet.
        _menu.SetVariant("accent", "gold");

        _menu.SetItems(Utilities.GetPlayers()
            .Where(p => p is { IsValid: true, IsHLTV: false })
            .Select(p => new MenuItem(
                Id:       $"player:{p.Slot}",
                Title:    p.Slot == viewer.Slot ? $"{p.PlayerName} (You)" : p.PlayerName,
                Subtitle: p.IsBot ? "BOT" : $"{p.Ping}ms",
                OnSelect: e => ShowActionsFor(e.Player, p.Slot),
                Tag:      p.Slot)));

        _menu.Open(viewer);
    }

    private void ShowActionsFor(CCSPlayerController viewer, int targetSlot)
    {
        if (_menu is null)
            return;

        if (Utilities.GetPlayerFromSlot(targetSlot) is not { IsValid: true } target)
        {
            ShowPlayerList(viewer);
            return;
        }

        _menu.Title    = target.PlayerName;
        _menu.Subtitle = "Actions";

        // The colour is the point of this screen: red says "what you pick next is destructive".
        // The server cannot send #e0432f - it names a class the stylesheet already defines.
        _menu.SetVariant("accent", "red");

        _menu.SetItems(
        [
            new MenuItem($"act:kick:{targetSlot}", "Kick", "Remove", e => Act(e.Player, "kick", targetSlot)),
            new MenuItem($"act:slay:{targetSlot}", "Slay", "Kill",   e => Act(e.Player, "slay", targetSlot)),
            new MenuItem("back", "Back", null, e => ShowPlayerList(e.Player)),
        ]);

        _menu.Refresh(viewer);
    }

    private void OnMenuEvent(PanelEvent e)
    {
        // One gate for every row - see the note in AdminMenuPlugin. OnEvent runs before any row's
        // own callback, so cancelling here stops the action.
        if (e.Action == PanelAction.Click && !AdminManager.PlayerHasPermissions(e.Player, "@css/generic"))
        {
            e.Cancel = true;

            Logger.LogWarning(
                "[ExampleKit] blocked {Player} from '{Element}' - missing @css/generic",
                e.Player.PlayerName, e.ElementId);
        }
    }

    private void Act(CCSPlayerController admin, string verb, int targetSlot)
    {
        if (Utilities.GetPlayerFromSlot(targetSlot) is not { IsValid: true } target)
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

        Logger.LogDebug("[ExampleKit] {Admin} {Verb} {Target}", admin.PlayerName, verb, target.PlayerName);

        _menu?.Close(admin);
    }

    private void PushUptime()
        => _menu?.SetVariable("uptime", TimeSpan.FromSeconds(Server.CurrentTime).ToString(@"hh\:mm\:ss"));
}
