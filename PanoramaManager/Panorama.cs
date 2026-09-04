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
    ///
    /// <para>Asked of the natives on every read rather than snapshotted at <see cref="Init"/>. The
    /// answer is not fixed at load: the stride check can disable the per-player path, and a consumer
    /// that cached this once printed "available" for the rest of the map while every text write was
    /// being refused.</para>
    /// </summary>
    public static bool CanWritePerPlayerText
        => _logger is { } log && (_natives ??= new CustomHudNatives(log)).CanWritePerPlayerText;

    /// <summary>Kept for the property above so the check is a field read rather than an allocation
    /// per call - the natives themselves are static, this instance is just the accessor.</summary>
    private static CustomHudNatives? _natives;

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
        // The result is deliberately not stored - CanWritePerPlayerText re-asks, see the property.
        _natives = new CustomHudNatives(_logger);
        _ = _natives.CanWritePerPlayerText;

        _transport = transport ?? new ClickHookTransport(_logger);
        _transport.OnInteraction += Dispatch;
        _transport.Install();

        // Non-player entities are bulk-deleted on both, taking every open menu with them.
        plugin.RegisterEventHandler<EventRoundStart>(OnRoundStart);
        plugin.RegisterListener<Listeners.OnMapStart>(OnMapStart);
        plugin.RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);

        // Keeps a layout off the screens of players spectating whoever it belongs to. See
        // LayoutContract.HideFromSpectators for why the entity has to be withheld rather than
        // hidden with a class.
        plugin.RegisterListener<Listeners.CheckTransmit>(OnCheckTransmit);

        // Both ends of a slot's life, because neither one alone is reliable.
        //
        // Every scrap of per-player state - reveal class, dialog variables, input capture - lives in
        // m_vecPlayerLayoutStates[SLOT] on an entity the engine preserves across round restarts, so
        // it is inherited by whoever takes the slot next and lasts the whole map. The disconnect
        // EVENT is not enough to clear it: CS2 routinely delivers it with a null or already-invalid
        // Userid, and the handler cannot then name a slot. OnClientDisconnect is handed the slot as
        // an int and always fires. OnClientPutInServer is the other half of belt-and-braces - it
        // clears on the way IN, which also covers state left behind by a crash, a map change or a
        // plugin reload, none of which produce a disconnect this library ever sees.
        plugin.RegisterListener<Listeners.OnClientDisconnect>(ResetSlot);
        plugin.RegisterListener<Listeners.OnClientPutInServer>(ResetSlot);

        // HUD flags live on the pawn, and respawning hands the player a fresh one with the field
        // reset. Without this, a menu that hides the crosshair loses it the first time its owner
        // respawns and the crosshair draws over the panel.
        plugin.RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);

        // Chat is the only text channel a player has, so it is where prompts are answered. Hooking
        // the commands rather than EventPlayerChat is deliberate: a command listener can return
        // Handled and actually suppress the message, which an event handler cannot.
        plugin.AddCommandListener("say", OnSay, HookMode.Pre);
        plugin.AddCommandListener("say_team", OnSay, HookMode.Pre);

        // Diagnostic, and a second chance at the same message.
        //
        // Not a fallback - the wildcard chain runs to completion BEFORE the named chain, inside the
        // same dispatch, so this always fires and always fires first. It is worth keeping anyway:
        // the named chain returns at the first listener answering Handled, so a "say" listener from
        // another plugin registered ahead of ours means ours is never reached, and this is the only
        // thing that still sees the message. The double fire is absorbed by _consumedSay.
        plugin.AddCommandListener(null, OnAnyCommand, HookMode.Pre);

        // One command that answers "is this working", because the alternative is reading five
        // startup lines that have scrolled away. Distilled from the Poc1 probe plugin, which existed
        // only to poke the entity by hand - this is the part of it worth keeping.
        plugin.AddCommand("css_panorama_diag", "Report Panorama native and transport status.", Diagnose);

        // The way out of a stuck cursor, and until now it did not exist - the only thing that
        // cleared one was reconnecting to the server. Every automatic release is driven by
        // something the stuck player cannot cause: respawning skips a slot that still has a live
        // session, a round restart deliberately keeps entity, sessions and capture, and the close
        // button needs a panel on screen to be clicked.
        //
        // Registered by every plugin referencing the library, exactly like css_panorama_diag, and
        // that is the point: each has its own Handles list in its own load context, so one command
        // registered six times is what reaches all six. A capture stranded by another plugin is
        // otherwise unreachable from this one.
        plugin.AddCommand("css_cursor", "Release a stuck menu cursor.", ReleaseCursor);

        if (!_transport.IsInstalled)
        {
            _logger.LogWarning(
                "[Panorama] no click channel - menus will render but won't respond. Expected on "
                + "Windows servers; on Linux, run css_panorama_diag.");
        }
    }

    private sealed record PendingPrompt(int Slot, PanelHandle Menu, TextPrompt Prompt, Timer? Timeout);

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

        Prompts[slot] = new PendingPrompt(slot, menu, prompt, timeout);

        // After the guards, not before them: logged where an arm has actually happened. Paired with
        // the line in OnSay. Together they say which half is broken - an arm with no matching say
        // means the listener is not being reached; neither means it never armed.
        _logger?.LogInformation("[Panorama] prompt armed for {Player} (slot {Slot})",
            player.PlayerName, slot);

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

    /// <summary>
    /// Whether this slot has a prompt actually waiting for an answer.
    ///
    /// <para>This dictionary is what <see cref="OnSay"/> consults, so it is the only honest answer
    /// to "am I already asking?". A menu's own bookkeeping can drift out of step with it, and a
    /// guard built on that instead will either ask twice or - far worse - refuse to ask ever again
    /// while nothing is listening.</para>
    /// </summary>
    internal static bool HasPendingPrompt(int slot) => Prompts.ContainsKey(slot);

    /// <summary>
    /// The last say this library consumed, as (slot, tick).
    ///
    /// <para>One typed line reaches this listener more than once. The wildcard chain and the named
    /// chain both run inside a single ExecuteCommandCallbacks call, wildcard first, so registering
    /// both is a guaranteed double fire rather than a fallback; and the engine dispatches a client
    /// command through two hooked entry points, so the whole call can happen twice in the frame.
    /// Without a memo the second pass finds a prompt again - the consumer re-arms from OnResult, as
    /// the announce view does - and eats the same line a second time.</para>
    ///
    /// <para>Checked at the top of <see cref="OnSay"/>, before the pending lookup, so a repeat is
    /// neither consumed nor let through: it returns Handled, which is what keeps the answer out of
    /// public chat on whichever dispatch is the one that would have reached Host_Say.</para>
    ///
    /// <para>Keyed on the tick as well as the slot so it expires on its own. Two says from one
    /// player in the same tick is not a thing a human does, and if it ever happened the cost is one
    /// swallowed message rather than a leaked one.</para>
    /// </summary>
    private static (int Slot, int Tick) _consumedSay = (-1, -1);

    /// <summary>
    /// Catches chat through the wildcard listener when the named one was pre-empted.
    ///
    /// <para>Only looks at say/say_team, and only while a prompt is actually pending - so on a
    /// server where the named listener works this never does anything, and the message is consumed
    /// exactly once either way.</para>
    /// </summary>
    private static HookResult OnAnyCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (Prompts.Count == 0) return HookResult.Continue;

        var name = command.GetArg(0);
        if (!name.Equals("say", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("say_team", StringComparison.OrdinalIgnoreCase))
            return HookResult.Continue;

        // Only for the player who is actually being asked. Gated on Prompts.Count it named every
        // player who spoke while any prompt anywhere was pending.
        if (player is { IsValid: true } && Panorama.HasPendingPrompt(player.Slot))
            _logger?.LogInformation("[Panorama] say seen by the wildcard listener from {Player}",
                player.PlayerName);

        return OnSay(player, command);
    }

    private static HookResult OnSay(CCSPlayerController? player, CommandInfo command)
    {
        if (player is not { IsValid: true })
            return HookResult.Continue;

        // Before the lookup, not after it. A consumer that re-arms from OnResult - which is the
        // only way a view can stay armed while the admin retypes - puts a fresh prompt back in
        // Prompts before the next listener in this same dispatch reads it, and a check placed on
        // the miss branch would let that listener consume the line all over again.
        if (_consumedSay == (player.Slot, Server.TickCount))
            return HookResult.Stop;

        if (!Prompts.TryGetValue(player.Slot, out var pending))
            return HookResult.Continue;

        _consumedSay = (player.Slot, Server.TickCount);

        // Logged AFTER the lookup, so it is this player's own answer being logged and not every
        // other player's chat. Gated on Prompts.Count it copied the whole server's conversation
        // into the log for as long as one admin sat in a 120s prompt, and buried the one line it
        // exists to provide: no line means the listener is not being reached - another plugin
        // handled the message first - and a line means the text arrived and the fault is further in.
        _logger?.LogInformation(
            "[Panorama] say reached the prompt listener: player={Player} pending={Pending} text='{Text}'",
            player.PlayerName, Prompts.Count, command.GetArg(1));

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

        // Stop, not Handled, and the difference is the whole bug.
        //
        // ExecuteCommandCallbacks (con_command_manager.cpp) treats them differently in the WILDCARD
        // chain, which is where this runs:
        //     if (hookResult >= Stop)    { if (mode == Pre) return Stop; }   // chain ends here
        //     if (hookResult >= Handled) { result = hookResult; }            // loop continues
        // Handled supercedes the engine's own broadcast, which is why this looked correct - but it
        // leaves every later listener running. A chat-tag plugin (Ranks_Tag here) blocks the
        // original and REPRINTS the line itself, so the answer went out anyway with a rank tag on
        // it. Stop ends the chain, so nothing downstream ever sees the message.
        //
        // Scoped to exactly the message this library just consumed as a prompt answer: one player,
        // one tick, already being swallowed deliberately. Gag and tag processing have no business
        // running on a ban reason typed into a menu, so short-circuiting them here costs nothing.
        return HookResult.Stop;
    }

    private static void Deliver(PendingPrompt pending, TextPromptResult result)
    {
        try
        {
            pending.Menu.OnPromptResult(pending.Slot, pending.Prompt, result);
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
        // The instance Init built and every CanWritePerPlayerText call since has gone through -
        // NOT a fresh one. A new CustomHudNatives describes an object that has never rendered
        // anything, and worse, it reports its own untouched fields: Resolve short-circuits on a
        // static flag, so the second instance never runs the stride check and prints "not checked"
        // on a server where the stride was checked and confirmed at load. A diagnostic that
        // disagrees with the code it is diagnosing is worse than no diagnostic.
        var natives = _natives ??= new CustomHudNatives(_logger!);

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

            // The renderer's OWN view, not the block above. Most of the native table is static and
            // the two normally agree - but "normally agree" is an assumption, and this is where a
            // handle whose entity died or whose renderer sees a different table becomes visible
            // instead of being averaged away into one summary line.
            command.ReplyToCommand($"[Panorama/{owner}]     {handle.DescribeRenderer()}");

            // Per-slot, because the failures that reach a player are per-slot: a class left on with
            // no session behind it, or a structural class missing so the panel falls back to the
            // stylesheet's default position.
            foreach (var line in handle.DescribeSlots())
                command.ReplyToCommand($"[Panorama/{owner}]     {line}");
        }

        SchemaProbe.Report(_logger!);
        command.ReplyToCommand($"[Panorama/{owner}] schema offsets written to the server log.");
    }

    /// <summary>
    /// Closes every menu this plugin has open for the caller and drops its input capture.
    ///
    /// <para>No permission check: it only ever acts on the caller's own slot, and the player it is
    /// for is by definition unable to open a menu to prove anything. Closing rather than releasing
    /// blind means the consumer hears a Close and puts the HUD flags back, and a menu that was
    /// legitimately open is simply closed - which is what someone typing this is asking for.</para>
    /// </summary>
    private static void ReleaseCursor(CCSPlayerController? player, CommandInfo command)
    {
        if (player is not { IsValid: true })
        {
            command.ReplyToCommand("[Panorama] css_cursor is for players - it acts on your own slot.");
            return;
        }

        var closed = 0;

        foreach (var handle in Handles.ToList())
        {
            if (handle.ReleaseCursor(player.Slot))
                closed++;
        }

        _logger?.LogInformation(
            "[Panorama] css_cursor from {Player} (slot {Slot}) closed {Closed} of {Total} menu(s)",
            player.PlayerName, player.Slot, closed, Handles.Count);

        command.ReplyToCommand(closed > 0
            ? $"[{_plugin?.ModuleName ?? "Panorama"}] closed {closed} menu(s) and released the cursor."
            : $"[{_plugin?.ModuleName ?? "Panorama"}] no menu was open here; released the cursor anyway.");
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

        _plugin  = null;
        _logger  = null;
        _natives = null;
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

        ReportUnclaimed(raw);
    }

    private static DateTime _lastUnclaimedLog = DateTime.MinValue;

    /// <summary>
    /// Says something when a click reaches the server and no menu takes it.
    ///
    /// <para>"Clicks stopped landing while the panel was still up" had no server-side evidence of
    /// any kind: the click path drops silently at the controller, at the layout match and at the
    /// session lookup, and not one of them logged. This is the whole class of report, on one line.</para>
    ///
    /// <para>Two shapes only, because every plugin referencing the library hooks the same click
    /// receiver and therefore sees every click on the server - reporting all of them would be six
    /// identical lines per click of pure noise. Worth a line: a click whose clicker did not resolve
    /// at all (no handle anywhere could have claimed it), and a click nobody took while THIS
    /// context has a menu open for that player. The second is the failure being hunted.</para>
    /// </summary>
    private static void ReportUnclaimed(RawInteraction raw)
    {
        if (_logger is null)
            return;

        var open = raw.Player is { IsValid: true } clicker
                   && Handles.Any(handle => handle.HasSession(clicker.Slot));

        if (raw.Player is not null && !open)
            return;

        // A stuck panel is clicked repeatedly and angrily; one line every few seconds is enough to
        // establish it is happening without burying the rest of the log.
        if ((DateTime.UtcNow - _lastUnclaimedLog).TotalSeconds < 5)
            return;

        _lastUnclaimedLog = DateTime.UtcNow;

        _logger.LogWarning(
            "[Panorama] click on '{Element}' from {Player} was claimed by no menu (layout {Layout}, "
            + "menu open here: {Open}). A null player means the click hook could not resolve the "
            + "controller; an open menu with no claim means the layout or the session did not match.",
            raw.ElementId,
            raw.Player is { IsValid: true } p ? p.PlayerName : "<unresolved>",
            raw.Layout == IntPtr.Zero ? "unknown" : "supplied",
            open);
    }

    private static HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        WorldReset();

        return HookResult.Continue;
    }

    private static void OnMapStart(string mapName)
    {
        // Entity indices from the previous map mean nothing on this one, and the registry is what
        // Spawn consults before adopting an entity instead of creating one. Left standing, a
        // recycled index can match an entry from the old map and hand a handle a custom_hud_layout
        // belonging to another layout entirely - which then quietly receives every write meant for
        // ours. Cleared before any handle resolves anything on the new map.
        PanelRegistry.ClearLayouts();

        WorldReset();
    }

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

    /// <summary>
    /// Kept as a third chance at the same slot. The event fires earlier than the listener, while the
    /// controller is still valid, so it gets the panel off screen sooner - but it is guarded on a
    /// Userid CS2 often does not supply, which is exactly why it is no longer the only path.
    /// </summary>
    private static HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        if (@event.Userid is { IsValid: true } player)
            ResetSlot(player.Slot);

        return HookResult.Continue;
    }

    /// <summary>
    /// Wipes everything the library holds for one slot, whether or not anything thinks it is open.
    ///
    /// <para>Slots are recycled and the layout entity outlives their occupants, so anything left
    /// behind here is inherited by the next player to take the slot: a stuck cursor with no panel to
    /// close, someone else's rows, a progress bar frozen mid-track, or - through the prompt table -
    /// their chat being swallowed into the previous occupant's text handler. None of it is
    /// recoverable by the player, and none of it expires before the map does.</para>
    ///
    /// <para>Called from both ends of the slot's life and safe to call repeatedly: every write here
    /// is idempotent, and clearing state that was never set costs one refused native call.</para>
    /// </summary>
    private static void ResetSlot(int slot)
    {
        foreach (var handle in Handles.ToList())
            handle.ResetSlot(slot);

        // After the handles, not before: a handle cancels the prompt it knows about, and this
        // catches one whose menu was disposed out from under it - the table Panorama.OnSay actually
        // reads is this one, so a stale entry here is the one that eats the next player's chat.
        CancelPrompt(slot, TextPromptOutcome.Abandoned);
    }

    /// <summary>
    /// Drops input capture on every menu for a slot, provided none of them is actually open.
    ///
    /// <para>Capture lives on the layout entity, so a player is only free of the cursor once every
    /// entity agrees. One orphaned handle is enough to keep it, and the player has no way to clear
    /// that themselves.</para>
    /// </summary>
    internal static void ReleaseInputIfIdle(int slot)
    {
        var handles = Handles.ToList();

        if (handles.Any(handle => handle.HasSession(slot)))
            return;

        foreach (var handle in handles)
            handle.ForceReleaseInput(slot);
    }

    /// <summary>
    /// Decides each menu's entity per player: sent to whoever has it open, dropped for whoever has
    /// nothing open on it.
    ///
    /// <para>Runs every tick for every player, so it does the least possible: a slot lookup per
    /// menu, and nothing at all when no menu has spawned yet.</para>
    ///
    /// <para>The add is not redundant with the engine's own list. The layout entity is a logical
    /// entity sitting at the world origin, so whether it lands in a given client's snapshot is the
    /// engine's business, not ours - and a player whose viewpoint moves (dying and going in-eye of
    /// somebody else, then respawning at a spawn point) can have it drop out from under an open
    /// menu. The client then keeps drawing the last state it was told about until it tears the
    /// entity down on its own, which is the "my menu vanished a few seconds after I respawned"
    /// report. Anyone holding a session is told about the entity unconditionally.</para>
    /// </summary>
    private static void OnCheckTransmit(CCheckTransmitInfoList infoList)
    {
        if (Handles.Count == 0) return;

        foreach (var (info, player) in infoList)
        {
            if (player is not { IsValid: true }) continue;

            var slot = player.Slot;

            foreach (var handle in Handles)
            {
                // Show wins over hide, and the two are mutually exclusive anyway - both are keyed
                // on the same session lookup, from opposite sides.
                if (handle.EntityToShowTo(slot) is { } shown)
                    info.TransmitEntities.Add((int) shown);
                else if (handle.EntityToHideFrom(slot) is { } index)
                    info.TransmitEntities.Remove((int) index);
            }
        }
    }

    private static void WorldReset()
    {
        foreach (var handle in Handles.ToList())
            handle.OnWorldReset();
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..6];
}
