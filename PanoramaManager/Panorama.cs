using System;
using CounterStrikeSharp.API.Modules.Timers;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Admin;
using PanoramaManager.Internal;
using PanoramaManager.Rendering;
using PanoramaManager.Transport;
using Microsoft.Extensions.Logging;

namespace PanoramaManager;

/// <summary>
/// Entry point. Call <see cref="Init"/> once from your plugin's <c>Load</c>, then
/// <see cref="Spawn"/> menus wherever you need them.
///
/// <code>
/// public override void Load(bool hotReload)
/// {
///     Panorama.Init(this);
///
///     var menu = Panorama.Spawn("panorama/layout/custom_game/admin_hud.vxml_c");
///     menu.Title = "Admin";
///     menu.SetItems(Utilities.GetPlayers().Select(p =&gt; MenuItem.Of($"player_{p.Slot}", p.PlayerName)));
///     menu.OnEvent += e =&gt; { if (e.Action == PanelAction.Click) ShowActions(e.Player, e.Item!); };
///     menu.Open(admin);
/// }
/// </code>
///
/// <para>This is a plain library, not a plugin - nothing is registered globally and two consumers
/// referencing it don't interfere. The one thing it needs from you is the plugin instance, because
/// CounterStrikeSharp scopes command and listener registration to a plugin.</para>
/// </summary>
public static class Panorama
{
    private static readonly List<PanelHandle> Handles = new();

    private static BasePlugin?      _plugin;
    private static ILogger?         _logger;
    private static IPanelTransport?  _transport;

    /// <summary>True once <see cref="Init"/> has run.</summary>
    public static bool IsInitialised => _plugin is not null;

    /// <summary>
    /// Write dialog variables through the engine's <b>global</b> setter rather than the per-player
    /// path. Defaults to <b>false</b>: per-player text works, it just does not go through the
    /// engine's <c>SetDialogVariableStringForPlayer</c> entry point, which never stores the value.
    /// It interns the names and writes the slot's state directly instead - see
    /// <c>CustomHudNatives.SetDialogVariableStringForPlayer</c>.
    ///
    /// <para>Set this to <b>true</b> as a safety valve: if the per-player write misbehaves after a
    /// CS2 update shifts the state offsets, global writes still work and the menu still renders.</para>
    ///
    /// <para><b>What this costs.</b> Dialog variables become shared: every viewer of a given menu
    /// sees the same title, row text and footer. Class toggles stay per-player (that signature is
    /// good), so row <i>visibility</i> is still per viewer while row <i>text</i> is not. With one
    /// viewer this is invisible; with two admins browsing at once, the second one's text wins.</para>
    ///
    /// <para>Set to false once <c>SetDialogVariableStringForPlayer</c> has a correct signature, and
    /// per-viewer text comes back with no other change.</para>
    /// </summary>
    public static bool UseGlobalDialogVariables { get; set; }

    /// <summary>
    /// True if clicks can actually reach the server. False means menus still render but nothing
    /// comes back - check your logs for a signature-resolution warning. Worth surfacing in your own
    /// plugin's startup output so a broken build is obvious.
    /// </summary>
    public static bool CanReceiveClicks => _transport?.IsInstalled == true;

    /// <summary>
    /// True if per-viewer dialog variables are available. False means every viewer of a menu shares
    /// one set of strings - see <see cref="UseGlobalDialogVariables"/>.
    /// </summary>
    public static bool CanWritePerPlayerText { get; private set; }

    /// <summary>
    /// Wires the library to your plugin. Call once from <c>Load</c>.
    /// </summary>
    /// <param name="plugin">Your plugin instance.</param>
    /// <param name="transport">
    /// Override the click channel. Defaults to <see cref="ClickHookTransport"/>, which is the only
    /// one that works without layout scripting. Pass a <see cref="ConsoleCommandTransport"/> if you
    /// are on a build where layouts may run scripts.
    /// </param>
    public static void Init(BasePlugin plugin, IPanelTransport? transport = null)
    {
        ArgumentNullException.ThrowIfNull(plugin);

        if (_plugin is not null)
            return;

        _plugin = plugin;
        _logger = plugin.Logger;

        // Force signature resolution now rather than on the first menu open, so a broken gamedata
        // file is reported at load instead of the first time somebody opens a menu. This is silent
        // unless something is actually wrong; `css_panorama_diag` prints the full table on demand.
        CanWritePerPlayerText = new CustomHudNatives(_logger).CanWritePerPlayerText;

        _transport = transport ?? new ClickHookTransport(_logger);
        _transport.OnInteraction += Dispatch;
        _transport.Install();

        // Non-player entities are bulk-deleted on both, taking every open menu with them.
        plugin.RegisterEventHandler<EventRoundStart>(OnRoundStart);
        plugin.RegisterListener<Listeners.OnMapStart>(OnMapStart);
        plugin.RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);

        // HUD flags live on the pawn, and respawning hands the player a fresh one with the field
        // reset. Without this, a menu that hides the crosshair loses it the first time its owner
        // respawns and the crosshair draws over the panel.
        plugin.RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);

        // Chat is the only text channel a player has, so it is where prompts are answered. Hooking
        // the commands rather than EventPlayerChat is deliberate: a command listener can return
        // Handled and actually suppress the message, which an event handler cannot.
        plugin.AddCommandListener("say", OnSay, HookMode.Pre);
        plugin.AddCommandListener("say_team", OnSay, HookMode.Pre);

        // One command that answers "is this working", because the alternative is reading five
        // startup lines that have scrolled away. Distilled from the Poc1 probe plugin, which existed
        // only to poke the entity by hand - this is the part of it worth keeping.
        plugin.AddCommand("css_panorama_diag", "Report Panorama native and transport status.", Diagnose);

        if (!_transport.IsInstalled)
        {
            _logger.LogWarning(
                "[Panorama] no click channel - menus will render but won't respond. Expected on "
                + "Windows servers; on Linux, run css_panorama_diag.");
        }
    }

    private sealed record PendingPrompt(PanelHandle Menu, TextPrompt Prompt, Timer? Timeout);

    private static readonly Dictionary<int, PendingPrompt> Prompts = new();

    /// <summary>
    /// Starts waiting for <paramref name="player"/>'s next chat message. Replaces any prompt they
    /// already had, which is abandoned rather than left dangling.
    /// </summary>
    internal static void BeginPrompt(PanelHandle menu, CCSPlayerController player, TextPrompt prompt)
    {
        if (_plugin is null || player is not { IsValid: true })
            return;

        CancelPrompt(player.Slot, TextPromptOutcome.Abandoned);

        var slot = player.Slot;

        // A prompt that never resolves would swallow this player's chat for the rest of the map.
        var timeout = prompt.TimeoutSeconds > 0
            ? _plugin.AddTimer(prompt.TimeoutSeconds, () => CancelPrompt(slot, TextPromptOutcome.TimedOut))
            : null;

        Prompts[slot] = new PendingPrompt(menu, prompt, timeout);

        if (!string.IsNullOrEmpty(prompt.Hint))
            player.PrintToChat(prompt.Hint);
    }

    /// <summary>Ends a pending prompt without an answer. Safe to call when there is none.</summary>
    internal static void CancelPrompt(int slot, TextPromptOutcome outcome)
    {
        if (!Prompts.Remove(slot, out var pending))
            return;

        pending.Timeout?.Kill();
        Deliver(pending, new TextPromptResult(outcome, string.Empty));
    }

    private static HookResult OnSay(CCSPlayerController? player, CommandInfo command)
    {
        if (player is not { IsValid: true } || !Prompts.TryGetValue(player.Slot, out var pending))
            return HookResult.Continue;

        Prompts.Remove(player.Slot);
        pending.Timeout?.Kill();

        // Arg 1 is the message; the client sends it quoted.
        var text = command.GetArg(1).Trim().Trim('"').Trim();

        // Control characters would travel straight into a Label. Strip rather than reject, so a
        // player pasting something odd still gets their answer through.
        text = new string(text.Where(c => !char.IsControl(c)).ToArray());

        if (text.Length > pending.Prompt.MaxLength)
            text = text[..pending.Prompt.MaxLength];

        var cancelled = string.IsNullOrWhiteSpace(text)
                        || (pending.Prompt.CancelWord is { } word
                            && text.Equals(word, StringComparison.OrdinalIgnoreCase));

        Deliver(pending, cancelled
            ? new TextPromptResult(TextPromptOutcome.Cancelled, string.Empty)
            : new TextPromptResult(TextPromptOutcome.Submitted, text));

        // Swallow it. A kick reason or a ban note is not something to broadcast on the way in, and
        // the player is answering a menu rather than talking to the server.
        return HookResult.Handled;
    }

    private static void Deliver(PendingPrompt pending, TextPromptResult result)
    {
        try
        {
            pending.Menu.OnPromptResult(pending.Prompt, result);
            pending.Prompt.OnResult?.Invoke(result);
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "[Panorama] text prompt handler threw");
        }
    }

    /// <summary>
    /// Prints everything needed to tell a broken install from a broken layout.
    ///
    /// <para>The three failures this library has actually had all looked identical from the outside
    /// - a menu that renders but does nothing. They are told apart by which native resolved, whether
    /// the click transport installed, and whether the schema agrees with the hardcoded offsets. All
    /// of that is printed once at startup and then gone, so this reprints it on demand.</para>
    /// </summary>
    [RequiresPermissions("@css/generic")]
    private static void Diagnose(CCSPlayerController? player, CommandInfo command)
    {
        var natives = new CustomHudNatives(_logger!);

        // Every plugin referencing the library has its own copy of these statics and its own
        // registration of this command, so all of them answer. That is worth seeing - their state
        // genuinely differs - but only if each block says whose it is.
        var owner = _plugin?.ModuleName ?? "?";

        foreach (var line in natives.Describe())
            command.ReplyToCommand($"[Panorama/{owner}] {line}");

        command.ReplyToCommand($"[Panorama/{owner}] per-player text: {(natives.CanWritePerPlayerText ? "available" : "UNAVAILABLE - text will be shared")}");
        command.ReplyToCommand($"[Panorama/{owner}] click channel:   {(CanReceiveClicks ? "installed" : "NOT INSTALLED - clicks will not arrive")}");
        command.ReplyToCommand($"[Panorama/{owner}] live menus:      {Handles.Count}");

        foreach (var handle in Handles)
        {
            command.ReplyToCommand($"[Panorama/{owner}]   {handle.Id} {handle.LayoutPath} ({handle.OpenCount} viewer(s))");
        }

        SchemaProbe.Report(_logger!);
        command.ReplyToCommand($"[Panorama/{owner}] schema offsets written to the server log.");
    }

    /// <summary>Tears everything down. Call from your plugin's <c>Unload</c>.</summary>
    public static void Shutdown()
    {
        foreach (var handle in Handles.ToList())
            handle.Dispose();

        Handles.Clear();

        if (_transport is not null)
        {
            _transport.OnInteraction -= Dispatch;
            _transport.Uninstall();
            _transport = null;
        }

        _plugin = null;
        _logger = null;
    }

    /// <summary>
    /// Creates a menu backed by <paramref name="layoutPath"/>.
    /// </summary>
    /// <param name="layoutPath">
    /// Full compiled layout path, e.g. <c>panorama/layout/custom_game/admin_hud.vxml_c</c>. Note the
    /// <c>_c</c> - this is the compiled resource, not the authored <c>.xml</c>.
    /// </param>
    /// <param name="contract">
    /// Panel and variable naming the layout follows. Defaults to the convention the bundled
    /// Workshop layout implements; override it to drive a layout with different ids.
    /// </param>
    public static PanelHandle Spawn(string layoutPath, LayoutContract? contract = null)
    {
        if (_plugin is null || _logger is null)
            throw new InvalidOperationException("Call Panorama.Init(plugin) from your plugin's Load before spawning menus.");

        if (string.IsNullOrWhiteSpace(layoutPath))
            throw new ArgumentException("A layout path is required.", nameof(layoutPath));

        var resolved = contract ?? LayoutContract.Default;
        var renderer = new CustomHudLayoutRenderer(layoutPath, resolved, _logger);
        var handle   = new PanelHandle(NewId(), layoutPath, renderer, resolved, _logger);

        Handles.Add(handle);

        return handle;
    }

    /// <summary>
    /// Shows or hides parts of a player's base HUD.
    ///
    /// <para>Public because it is generally useful and has nothing to do with menus - a plugin that
    /// wants the radar gone during a cutscene needs exactly this. The library uses it internally to
    /// hide the crosshair while a menu is open, since the crosshair is drawn on a HUD layer above
    /// anything a Panorama stylesheet can reach.</para>
    ///
    /// <para>Read-modify-write on the named bits only, so it composes with anything else touching
    /// HUD flags instead of clobbering them. <c>SetStateChanged</c> is what actually networks the
    /// change - without it the field moves server-side and the client never hears about it.</para>
    ///
    /// <code>
    /// Panorama.SetHideHud(player, HideHudFlags.Crosshair | HideHudFlags.Radar, hide: true);
    /// </code>
    ///
    /// <para>Returns false if the player has no valid pawn - which is the usual reason a call
    /// appears to do nothing, since a dead or spectating player has nothing to hide.</para>
    /// </summary>
    public static bool SetHideHud(CCSPlayerController player, HideHudFlags flags, bool hide)
    {
        if (player is not { IsValid: true } || flags == HideHudFlags.None)
            return false;

        try
        {
            if (player.PlayerPawn?.Value is not { IsValid: true } pawn)
                return false;

            var updated = hide ? pawn.HideHUD | (uint) flags : pawn.HideHUD & ~(uint) flags;

            if (updated == pawn.HideHUD)
                return true;

            pawn.HideHUD = updated;
            Utilities.SetStateChanged(pawn, "CBasePlayerPawn", "m_iHideHUD");

            return true;
        }
        catch (Exception e)
        {
            _logger?.LogWarning(e, "[Panorama] could not set HUD flags for {Player}", player.PlayerName);

            return false;
        }
    }

    /// <summary>Kills every <c>custom_hud_layout</c> entity in the world. A blunt instrument for
    /// development; normal teardown goes through <see cref="Shutdown"/>.</summary>
    public static int DespawnAllEntities() => PanelEntity.DespawnAll();

    /// <summary>Suggests a transport command name scoped to a plugin, so two consumers using the
    /// console-command channel can't collide on one name.</summary>
    public static string CommandNameFor(BasePlugin plugin)
        => "hudmenu_" + Regex.Replace(plugin.ModuleName.ToLowerInvariant(), "[^a-z0-9]", "");

    internal static void Forget(PanelHandle handle) => Handles.Remove(handle);

    private static void Dispatch(RawInteraction raw)
    {
        // Handles are asked in creation order and the first that claims the click wins. A handle
        // claims it only if the clicked layout entity is its own, so several menus can be open at
        // once - one entity per layout path - without stealing each other's clicks.
        foreach (var handle in Handles.ToList())
        {
            if (handle.TryHandle(raw))
                return;
        }
    }

    private static HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        WorldReset();

        return HookResult.Continue;
    }

    private static void OnMapStart(string mapName) => WorldReset();

    private static HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        if (@event.Userid is { IsValid: true } player)
        {
            // Deferred a frame: at the moment the event fires the new pawn is not reliably
            // attached yet, and writing the flags then silently does nothing.
            Server.NextFrame(() =>
            {
                if (player is not { IsValid: true }) return;

                foreach (var handle in Handles.ToList())
                    handle.OnPlayerSpawn(player);
            });
        }

        return HookResult.Continue;
    }

    private static HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        if (@event.Userid is { IsValid: true } player)
        {
            foreach (var handle in Handles.ToList())
                handle.OnPlayerDisconnect(player.Slot);
        }

        return HookResult.Continue;
    }

    private static void WorldReset()
    {
        foreach (var handle in Handles.ToList())
            handle.OnWorldReset();
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..6];
}
