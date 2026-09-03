using System;
using System.Collections.Generic;
using System.Linq;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using PanoramaManager.Internal;
using PanoramaManager.Rendering;
using PanoramaManager.Transport;
using Microsoft.Extensions.Logging;

namespace PanoramaManager;

/// <summary>
/// One menu. Create with <see cref="Panorama.Spawn"/>, fill it, subscribe to <see cref="OnEvent"/>,
/// then <see cref="Open"/> it for a player.
///
/// <para>A handle can be open for several players at once and each keeps their own page and tab,
/// because state is written through the engine's per-player slots. Content is shared though - if
/// two admins need different rows, give each their own handle.</para>
/// </summary>
public sealed class PanelHandle : IDisposable
{
    private readonly IPanelRenderer                 _renderer;
    private readonly LayoutContract                _contract;
    private readonly ILogger                       _logger;
    private readonly List<MenuItem>                _items    = new();
    private readonly Dictionary<int, PanelSession>  _sessions = new();
    private readonly Dictionary<string, string>    _vars     = new();
    private readonly Dictionary<string, string>    _variants = new();
    private readonly HashSet<int>                 _promptSlots = new();

    /// <summary>
    /// Every class this handle has turned ON for one viewer through <see cref="SetClassFor"/>, so
    /// closing can take them off again.
    ///
    /// <para>Without this a per-viewer class is set once and never removed by anything: the
    /// consumer's own bookkeeping is discarded when its menu state goes, and the class lives on the
    /// entity, which the engine preserves across round restarts. The progress bar frozen at 60% for
    /// the rest of the map is this - one w-class left on, and the stylesheet takes the widest.</para>
    /// </summary>
    private readonly Dictionary<int, HashSet<(string PanelId, string ClassName)>> _classesBySlot = new();

    private bool _disposed;

    /// <summary>
    /// Slots this handle believes are holding its input capture. Diagnostic only - nothing branches
    /// on it, because the netvar is the truth and a stale belief here must never stop a release.
    ///
    /// <para>A refused release deliberately LEAVES the slot recorded. The failure being hunted is a
    /// capture held with no panel behind it, and a release that did not reach the client is one of
    /// the ways to get there; dropping the record on a failed write would hide exactly that.</para>
    /// </summary>
    private readonly HashSet<int> _captureHeld = new();

    /// <summary>
    /// Slots whose scrub could not be written because there was no entity to write into, and
    /// whether that scrub still owes the tracked classes a removal.
    ///
    /// <para>A scrub that cannot run is not a scrub that is not needed. Close drops the session
    /// first and then hides the panel, so a scrub that bails leaves the exact state the user
    /// reports as "stuck": the panel drawn, the reveal class still on, no session behind it, so the
    /// close button is ignored and no later close retries - <c>Close</c> returns at its
    /// <c>!_sessions.Remove</c> guard from then on. Remembering the slot is what makes it
    /// retryable.</para>
    /// </summary>
    private readonly Dictionary<int, bool> _pendingScrub = new();

    /// <summary>
    /// Slots that were closed a moment ago, and the instant their layout entity may stop being sent
    /// to them.
    ///
    /// <para>The hide a close writes is per-player state INSIDE the layout entity, and
    /// <see cref="EntityToHideFrom"/> stops that entity being transmitted to any slot with no
    /// session - which a closing slot loses in the same tick. The end-of-frame snapshot for that
    /// client then excludes the entity, so the hide is never shipped: the client keeps drawing the
    /// last state it was told about - reveal class on, fully opaque - until it tears the entity down
    /// on its own, seconds later. That is the "click Close, panel goes inert, disappears a few
    /// seconds afterwards" report, on every interactive panel of every plugin, because
    /// <c>HideFromSpectators</c> defaults on and nothing overrides it.</para>
    ///
    /// <para>So keep sending the entity to the closing viewer for a moment longer. Their own state
    /// is already scrubbed, so they see the exit animation and then nothing; other viewers are
    /// unaffected, since this is per slot. Entries expire where they are read and are dropped
    /// outright by the next Open.</para>
    /// </summary>
    private readonly Dictionary<int, DateTime> _closingUntil = new();

    /// <summary>
    /// How long a closed slot keeps receiving the layout entity. Long enough for the hide to reach
    /// the client and its exit animation to play, short enough that a player who closes a menu and
    /// starts spectating is not shown the spectated player's panel for any noticeable time.
    /// </summary>
    private static readonly TimeSpan CloseTransmitGrace = TimeSpan.FromSeconds(1);

    /// <summary>Wraps <c>IPanelRenderer.SetInputCapture</c> so the capture state is recorded. The
    /// engine call and its return value are unchanged.</summary>
    private bool SetCapture(int slot, bool enabled)
    {
        var written = _renderer.SetInputCapture(slot, enabled);

        if (written)
        {
            if (enabled) _captureHeld.Add(slot);
            else         _captureHeld.Remove(slot);
        }

        return written;
    }

    internal PanelHandle(string id, string layoutPath, IPanelRenderer renderer, LayoutContract contract, ILogger logger)
    {
        Id         = id;
        LayoutPath = layoutPath;
        _renderer  = renderer;
        _contract  = contract;
        _logger    = logger;
    }

    /// <summary>Short opaque id, generated for you. Useful for logging; you never need to pass it back.</summary>
    public string Id { get; }

    public string LayoutPath { get; }

    /// <summary>Shown in the layout's title slot. Set before <see cref="Open"/>, or call
    /// <see cref="Refresh"/> after changing it.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Second line of the header. Both bundled layouts have a slot for it. Set before
    /// <see cref="Open"/>, or call <see cref="Refresh"/> after changing it.</summary>
    public string Subtitle { get; set; } = string.Empty;

    /// <summary>Tab ids in display order. Leave empty for a menu with no tab bar.</summary>
    public IList<string> Tabs { get; } = new List<string>();

    /// <summary>Rows, across every page. Pagination is handled for you.</summary>
    public IReadOnlyList<MenuItem> Items => _items;

    /// <summary>Rows per page, taken from the layout's physical row pool.</summary>
    public int PageSize => Math.Max(1, _renderer.RowCapacity);

    public int PageCount => Math.Max(1, (int) Math.Ceiling(_items.Count / (double) PageSize));

    /// <summary>Every interaction from every player with this menu open.</summary>
    public event Action<PanelEvent>? OnEvent;

    public PanelHandle AddItem(MenuItem item)
    {
        _items.Add(item);
        return this;
    }

    public PanelHandle AddItem(string id, string title, string? subtitle = null)
        => AddItem(new MenuItem(id, title, subtitle));

    public PanelHandle SetItems(IEnumerable<MenuItem> items)
    {
        _items.Clear();
        _items.AddRange(items);
        return this;
    }

    public PanelHandle ClearItems()
    {
        _items.Clear();
        return this;
    }

    /// <summary>Sets a free-form dialog variable, e.g. a live timer. Applies to every player with
    /// the menu open and is replayed for anyone who opens it later.</summary>
    /// <summary>
    /// Picks one class out of a mutually exclusive group on the root panel - the way to drive
    /// anything the server cannot send directly.
    ///
    /// <para>A colour, a width, a placement: none of them can travel over the wire, because the only
    /// things that can are strings into dialog variables and class toggles. So the layout bakes in a
    /// palette and the plugin names one of them. <c>SetVariant("accent", "red")</c> adds
    /// <c>accent-red</c> and removes whatever <c>accent-*</c> was set before, so the group only ever
    /// has one member applied.</para>
    ///
    /// <code>
    /// menu.SetVariant("accent", alive ? "green" : "red");
    /// menu.SetVariant("anchor", "bottom");
    /// </code>
    ///
    /// <para>Pass null to clear the group. Values are replayed for anyone who opens the menu later,
    /// and the class has to exist in the layout's stylesheet - an unknown one is silently ignored by
    /// the client, which is the usual reason a variant "does nothing".</para>
    /// </summary>
    public PanelHandle SetVariant(string group, string? value)
    {
        if (_variants.TryGetValue(group, out var previous))
        {
            if (previous == value)
                return this;

            foreach (var slot in _sessions.Keys)
                _renderer.SetClass(slot, _contract.RootPanelId, $"{group}-{previous}", false);

            _variants.Remove(group);
        }

        if (!string.IsNullOrEmpty(value))
        {
            _variants[group] = value;

            foreach (var slot in _sessions.Keys)
                _renderer.SetClass(slot, _contract.RootPanelId, $"{group}-{value}", true);
        }

        return this;
    }

    /// <summary>
    /// Sets a dialog variable for <b>one viewer only</b>, bypassing the shared
    /// <see cref="SetVariable(string,string)"/>.
    ///
    /// <para>The row pool never needed this - every viewer sees the same list, differing only in
    /// which page they are on. A menu whose content is per-player from the start (a weapon picker,
    /// an inventory) does, and writing it through the shared setter would show every player the last
    /// one's data. Not replayed on reopen: the caller owns the state, so the caller redraws it.</para>
    /// </summary>
    public PanelHandle SetVariableFor(CCSPlayerController player, string name, string value)
    {
        if (player is { IsValid: true } && _sessions.ContainsKey(player.Slot))
            _renderer.SetVariable(player.Slot, name, value);

        return this;
    }

    /// <summary>Toggles a class on a panel for one viewer only. Same reasoning as
    /// <see cref="SetVariableFor"/>.</summary>
    public PanelHandle SetClassFor(CCSPlayerController player, string panelId, string className, bool enabled)
    {
        TrySetClassFor(player, panelId, className, enabled);

        return this;
    }

    /// <summary>
    /// <see cref="SetClassFor"/> with the answer. Returns false when the write did not reach the
    /// client - no session, no entity, or an unresolved native.
    ///
    /// <para>The chaining overload cannot report that, so a caller commits its own state ("the bar
    /// is at 60% now") on the strength of a write that never landed, and every later toggle is
    /// computed against a lie. Use this where the class is part of a state machine the caller is
    /// tracking; the void form is fine for a class it recomputes from scratch each render.</para>
    /// </summary>
    public bool TrySetClassFor(CCSPlayerController player, string panelId, string className, bool enabled)
    {
        if (player is not { IsValid: true } || !_sessions.ContainsKey(player.Slot))
            return false;

        var slot = player.Slot;

        // Recorded on intent, not on success. A write that failed leaves nothing to clear, and
        // clearing it anyway is one refused native call - whereas a write that succeeded and went
        // unrecorded is a class stuck on for the rest of the map.
        if (enabled)
        {
            if (!_classesBySlot.TryGetValue(slot, out var set))
                _classesBySlot[slot] = set = new HashSet<(string, string)>();

            set.Add((panelId, className));
        }
        else if (_classesBySlot.TryGetValue(slot, out var set))
        {
            set.Remove((panelId, className));
        }

        return _renderer.SetClass(slot, panelId, className, enabled);
    }

    public PanelHandle SetVariable(string name, string value)
    {
        _vars[name] = value;

        foreach (var slot in _sessions.Keys)
            _renderer.SetVariable(slot, name, value);

        return this;
    }

    /// <summary>Shows the menu to a player. Safe to call again to reset them to page 0.</summary>
    public void Open(CCSPlayerController player)
    {
        if (player is not { IsValid: true })
            return;

        // Before anything is drawn: a scrub owed on this slot from an earlier close would otherwise
        // land on top of the panel we are about to open and hide it again.
        DrainPendingScrubs();

        var session = new PanelSession
        {
            Slot      = player.Slot,
            SteamId   = player.SteamID,
            Token     = Guid.NewGuid().ToString("N")[..12],
            ActiveTab = Tabs.FirstOrDefault(),
        };

        _sessions[player.Slot] = session;

        // This slot no longer owes a hide - it is being opened. The drain above runs BEFORE the
        // session exists and before Render creates the entity, so on the first open of a layout on
        // a new map it is guaranteed to have found nothing resolvable and left the entry queued.
        // Dropping it here is what stops that entry firing later, against this very panel.
        _pendingScrub.Remove(player.Slot);

        // And it is not closing either - the session above already keeps the entity transmitting,
        // so the grace has nothing left to do and an expiry left behind would only be read again
        // after the next close sets a fresh one.
        _closingUntil.Remove(player.Slot);

        // Take off every per-viewer class this handle previously turned on for this slot, BEFORE the
        // first draw. Close deliberately leaves them so the exit animation still has the panel's
        // geometry, which means a reopen would otherwise inherit them - and several are "highest
        // value wins" state, most visibly the now-playing bar's w0..w20 steps, where a leftover w12
        // beats a fresh w2 and the bar reads 60% forever. Doing it here rather than at close also
        // means the writes land on a panel nobody is looking at yet.
        //
        // The liveness check comes FIRST, and the record is only dropped once the removals below
        // have actually been written. The other order dropped it either way: Remove is the left
        // operand, so a false IsEntityAlive forgot every class this handle had turned on for the
        // slot without taking a single one off. IsEntityAlive is false whenever our cached index is
        // unresolved, which is not the same as the entity being gone - Render is the very next
        // statement here, and it resolves by ADOPTING a live entity for this layout when there is
        // one. The classes come straight back, now untracked, and nothing in the process knows they
        // exist: no later scrub, close or reset can reach them for the rest of the map.
        // Resolvable rather than alive, for the reason the comment above gives: Render is the very
        // next statement and it resolves by adopting, so an entity our index had merely forgotten is
        // one we are about to write into anyway. IsEntityAlive here skipped the removals and then
        // dropped the record on the next open, leaving classes nothing in the process can name.
        if (_renderer.IsEntityResolvable() && _classesBySlot.Remove(player.Slot, out var stale))
        {
            foreach (var (panelId, className) in stale)
            {
                // Never the root. Turning a class OFF is indistinguishable from never setting it,
                // so scrubbing the root strips whatever the LAYOUT declared statically on it too -
                // nowplaying_hud.xml ships class="hud-root hud-card accent-gold np-root pos-top",
                // and taking pos-top off leaves the card with no pos- class at all, which the
                // stylesheet renders dead centre. Root classes are structural and every draw
                // rewrites them anyway; the transient state that actually needed scrubbing lives on
                // child panels (np_bar's w0..w20 being the case this was built for).
                if (panelId == _contract.RootPanelId) continue;

                _renderer.SetClass(player.Slot, panelId, className, false);
            }
        }

        bool drawn;

        try
        {
            drawn = Render(session);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Panorama] first draw of menu {MenuId} threw for {Player}; closing it",
                Id, player.PlayerName);

            Close(session.Slot);
            return;
        }

        // A render that wrote nothing is the same failure as one that threw, and it is the more
        // common one: the renderer returns false rather than throwing on every path - no entity,
        // an unresolved native, a refused per-player write - so dropping those return values made a
        // dead render indistinguishable from a live one. The player then held a cursor with nothing
        // on screen and no close button to escape with, and reopening only repeated it.
        if (!drawn)
        {
            _logger.LogError(
                "[Panorama] first draw of menu {MenuId} for {Player} wrote nothing - no entity, or "
                + "the natives are unresolved. Closing it rather than leaving a cursor over an empty "
                + "screen; run css_panorama_diag.", Id, player.PlayerName);

            Close(session.Slot);
            return;
        }

        // Taken AFTER the first draw succeeds, not before. Without input capture the layout's
        // buttons never receive the mouse, so an interactive menu renders correctly and is
        // completely inert - but taking it first means every failure above strands the player in
        // cursor mode. A read-only layout opts out entirely; see LayoutContract.CaptureInput.
        if (_contract.CaptureInput)
            SetCapture(session.Slot, true);

        ApplyHudFlags(player, hide: true);
    }

    /// <summary>Hides the menu for a player and drops their session.</summary>
    public void Close(CCSPlayerController player)
    {
        if (player is { IsValid: true })
            Close(player.Slot);
    }

    /// <summary>Redraws for one player, or for everyone when <paramref name="player"/> is null.
    /// Call after mutating <see cref="Items"/> or <see cref="Title"/>.</summary>
    public void Refresh(CCSPlayerController? player = null)
    {
        if (player is { IsValid: true })
        {
            if (_sessions.TryGetValue(player.Slot, out var one))
                Render(one);

            return;
        }

        foreach (var session in _sessions.Values.ToList())
            Render(session);
    }

    /// <summary>
    /// Asks a player for a line of text, using chat because a <c>custom_hud_layout</c> cannot accept
    /// one. See <see cref="TextPrompt"/> for why that is a constraint rather than a choice.
    ///
    /// <para>The answer is echoed into <see cref="TextPrompt.Variable"/> for this viewer, so they can
    /// see what the server received before acting on it, and the chat message is swallowed.</para>
    ///
    /// <para>Only one prompt per player at a time; starting a second abandons the first. A prompt is
    /// also abandoned if the menu closes underneath it.</para>
    /// </summary>
    /// <summary>
    /// Two-argument overload, kept for binary compatibility.
    ///
    /// <para>Adding the optional <c>replace</c> parameter to the three-arg method is source
    /// compatible but NOT binary compatible: a plugin compiled against an older PanoramaManager
    /// emits a MemberRef to the two-arg signature, and only this dll is redeployed, so without this
    /// it throws MissingMethodException at the first prompt. Shared/Examples/TextInput does exactly
    /// that.</para>
    /// </summary>
    public PanelHandle PromptText(CCSPlayerController player, TextPrompt prompt)
        => PromptText(player, prompt, false);

    /// <param name="player">Who to ask.</param>
    /// <param name="prompt">What to ask, and what to do with the answer.</param>
    /// <param name="replace">
    /// Replace a prompt this player already has pending on this menu. Default false, and that
    /// default is load-bearing: a prompt is naturally armed from the code that draws the view, and
    /// a view redraws on every click. Re-arming there reprints the hint and discards the pending
    /// capture, so the player watches the instruction repeat and their typing fall through to
    /// public chat. Ignoring a duplicate arm makes the obvious way to write it the correct one.
    /// Pass true only to deliberately swap the question being asked.
    /// </param>
    public PanelHandle PromptText(CCSPlayerController player, TextPrompt prompt, bool replace = false)
    {
        TryPromptText(player, prompt, replace);
        return this;
    }

    /// <summary>
    /// <see cref="PromptText(CCSPlayerController, TextPrompt, bool)"/>, but says whether a prompt is
    /// now actually pending.
    ///
    /// <para>PromptText declines silently - no session on this slot (a world reset between the drop
    /// and Restore), or one already pending without <paramref name="replace"/> - and returns the
    /// handle either way, so a caller that sets its own "asked already" flag up front can leave it
    /// stuck true with nothing behind it and never ask again.</para>
    /// </summary>
    public bool TryPromptText(CCSPlayerController player, TextPrompt prompt, bool replace = false)
    {
        if (player is not { IsValid: true } || !_sessions.ContainsKey(player.Slot))
            return false;

        // Asked of Panorama, not of _promptSlots. That set is this menu's own bookkeeping and
        // can outlive the prompt it describes; Prompts is what OnSay reads. Guarding on the
        // wrong one turns a stale entry into a menu that never asks again and never explains
        // why - the hint stops appearing and typing falls through to public chat.
        if (!replace && Panorama.HasPendingPrompt(player.Slot))
            return false;

        _promptSlots.Add(player.Slot);
        Panorama.BeginPrompt(this, player, prompt);

        // If BeginPrompt declined for any reason, do not leave this menu believing it asked.
        if (Panorama.HasPendingPrompt(player.Slot))
            return true;

        _promptSlots.Remove(player.Slot);
        return false;
    }

    /// <summary>
    /// Drops a pending prompt for this player without closing the menu.
    ///
    /// <para>Leaving the view that asked is not the same as answering it. Without this the prompt
    /// stayed pending: the player's next chat message was swallowed by OnSay, delivered to a
    /// handler whose view is gone, and never appeared anywhere - for the rest of the timeout. Call
    /// it AFTER clearing the view state, so the Abandoned delivery re-enters the consumer with the
    /// view already closed and cannot draw it back.</para>
    /// </summary>
    public PanelHandle CancelPrompt(CCSPlayerController player)
    {
        if (player is { IsValid: true } && _promptSlots.Contains(player.Slot))
            Panorama.CancelPrompt(player.Slot, TextPromptOutcome.Abandoned);

        return this;
    }

    /// <summary>Echoes a finished prompt into the layout. Called by <see cref="Panorama"/>.</summary>
    internal void OnPromptResult(int slot, TextPrompt prompt, TextPromptResult result)
    {
        // One slot, not every slot. Sweeping all of _promptSlots meant one player finishing - or
        // now, disconnecting, since ResetSlot cancels prompts - wiped every other viewer's pending
        // entry and echoed an empty answer into their variable. Two admins typing an announcement
        // at once is enough to hit it: their own answer still arrives, but the on-screen echo dies.
        if (!_promptSlots.Remove(slot)) return;

        if (_sessions.ContainsKey(slot))
            _renderer.SetVariable(slot, prompt.Variable, result.Text);
    }

    /// <summary>How many players currently have this menu open.</summary>
    public int OpenCount => _sessions.Count;

    /// <summary>
    /// This renderer's live state - the entity it writes into and the native table it writes
    /// through, read from the objects that actually render.
    /// </summary>
    internal string DescribeRenderer() => _renderer.DescribeState();

    /// <summary>
    /// Two lines per slot this handle has state for, for css_panorama_diag.
    ///
    /// <para>Reports the things that come apart and cause the visible bugs: a slot with tracked
    /// classes but NO session is state nothing will ever take off by itself, and the set of classes
    /// shows directly whether a structural one (a pos-, an accent) is missing or whether two
    /// mutually exclusive ones are on at once - which no amount of reading the source can tell you
    /// about a particular player on a running server.</para>
    ///
    /// <para>The classes alone were not enough, and the now-playing bar is why: six cards all
    /// reading <c>np_bar.w7</c> with a session are either six listeners a third of the way through
    /// the same song or six cards frozen where the song ended, and the dump was identical either
    /// way. The state line dates the last draw and counts them, so the two separate at a glance.</para>
    /// </summary>
    internal IEnumerable<string> DescribeSlots()
    {
        // _captureHeld is in the union, not just consulted per slot. Without it the one state this
        // whole line of enquiry is about - capture held, session gone, no classes left - visited no
        // slot at all and printed nothing, which is exactly how a handle reporting "0 viewer(s)"
        // can be the thing holding a player's cursor.
        var slots = _sessions.Keys.Concat(_classesBySlot.Keys).Concat(_captureHeld).Distinct().OrderBy(s => s);
        var now   = DateTime.UtcNow;

        foreach (var slot in slots)
        {
            var name = Utilities.GetPlayerFromSlot(slot) is { IsValid: true } p ? p.PlayerName : "<gone>";
            var classes = _classesBySlot.TryGetValue(slot, out var set) && set.Count > 0
                ? string.Join(" ", set.OrderBy(c => c.PanelId).ThenBy(c => c.ClassName)
                                      .Select(c => $"{c.PanelId}.{c.ClassName}"))
                : "-";

            // The state line is the half that says whether this is a panel or a corpse. The class
            // dump can only ever repeat what was last written; these say when it was last written,
            // whether the write that makes the panel visible landed, and whether the player's input
            // is still being held.
            string state;

            if (_sessions.TryGetValue(slot, out var session))
            {
                var draw = session.LastRenderAt is { } at
                    ? $"draw {(now - at).TotalSeconds,6:0.0}s ago #{session.RenderCount}"
                    : "NEVER DRAWN";

                // The reveal is the write the class dump cannot show: it goes through the renderer
                // directly rather than the tracked SetClassFor path. A root without it sits at
                // opacity 0 - invisible, still laid out, still taking clicks - which is precisely
                // the "cursor with nothing on screen" report.
                var reveal = _contract.RevealClass is { } r
                    ? (session.Revealed ? $"reveal={r}" : $"reveal={r} FAILED")
                    : (session.Revealed ? "reveal=unhidden" : "reveal=unhidden FAILED");

                state = $"session age {(now - session.OpenedAt).TotalSeconds,6:0.0}s {draw} {reveal}";
            }
            else
            {
                state = "NO SESSION";
            }

            // Reported for a slot with no session too, because that pairing - capture held, session
            // gone - is the one that leaves a player stuck.
            var capture = !_contract.CaptureInput ? "capture=n/a"
                        : _captureHeld.Contains(slot) ? "capture=HELD"
                        : "capture=off";

            yield return $"slot {slot,-2} {name,-20} {state} {capture}";
            yield return $"       classes: {classes}";
        }
    }

    /// <summary>True if this player currently has the menu open.</summary>
    public bool IsOpenFor(CCSPlayerController player)
        => player is { IsValid: true } && _sessions.ContainsKey(player.Slot);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // Slots with no session are closed too, for their tracked classes. A normal close leaves
        // those on so the exit animation keeps the panel's geometry, on the promise that the next
        // Open scrubs them - a promise a disposed handle cannot keep, while the entity and its
        // classes outlive it and the next handle for this layout adopts both. Close with no session
        // is the no-op-plus-scrub path, so this costs a few refused writes.
        foreach (var slot in _sessions.Keys.Concat(_classesBySlot.Keys).Distinct().ToList())
            Close(slot);

        Panorama.Forget(this);
    }

    /// <summary>
    /// <see cref="Close(CCSPlayerController)"/> for a slot whose controller is gone or not worth
    /// resolving.
    ///
    /// <para>Public because per-player panel state is keyed by slot on both sides of this library,
    /// and a consumer that drops its own state for a slot has to be able to drop the session with
    /// it. Without this the consumer could only forget - leaving the library holding a session with
    /// the reveal class still on and nobody left who will ever redraw or close it, which is a panel
    /// frozen mid-state for the rest of the map.</para>
    /// </summary>
    public void Close(int slot)
    {
        // Before the session goes, and on both branches below. Everything this method does to hide
        // the panel is written into the layout entity's per-player state, and the transmit hook
        // stops sending that entity to a slot the instant it has no session - so without this the
        // writes below never reach the client and the panel stays on screen. See _closingUntil.
        _closingUntil[slot] = DateTime.UtcNow + CloseTransmitGrace;

        if (!_sessions.Remove(slot))
        {
            // No session is NOT nothing to do. The session is this library's bookkeeping; the panel
            // is the client's, and the two come apart - a world reset drops sessions, a failed first
            // draw closes one that never drew, a consumer closes twice. Returning here left whatever
            // was last written still on screen with nothing that would ever take it off: a reveal
            // class holding an empty card up, per-viewer classes stuck at their last value for the
            // rest of the map. Scrub unconditionally instead; every write below is a no-op when
            // there was nothing set.
            ScrubPanel(slot, clearTrackedClasses: _disposed);

            // The player may also still be holding a capture from before a world reset. Releasing
            // one that was never taken is a no-op - the netvar is already false - so this costs
            // nothing and is the only thing standing between a stale capture and a player who
            // cannot move.
            if (_contract.CaptureInput)
                SetCapture(slot, false);

            return;
        }

        // A prompt outliving its menu would keep swallowing this player's chat with nothing on
        // screen to answer.
        if (_promptSlots.Remove(slot))
            Panorama.CancelPrompt(slot, TextPromptOutcome.Abandoned);

        // clearTrackedClasses follows _disposed, which Dispose sets before it closes anything. A
        // normal close leaves the per-viewer classes on so the CSS exit animation still has the
        // panel's geometry, and relies on the next Open to scrub them. A Dispose has no next Open:
        // the record goes with the handle while the classes stay on an entity that outlives it -
        // custom_hud_layout survives a round restart and the next handle for this layout ADOPTS it
        // - so a reloaded plugin inherits classes nothing can name any more. There is no exit
        // animation to protect when the handle is going away.
        ScrubPanel(slot, clearTrackedClasses: _disposed);

        // Released unconditionally, unlike Open which is guarded. Turning capture off for a layout
        // that never turned it on is a no-op - the netvar is already false, so nothing is even sent
        // - whereas skipping the release when it somehow IS on strands the player in cursor mode
        // with no way out. Dispose and Shutdown both come through here, so this covers them too.
        SetCapture(slot, false);

        // And again next frame, across EVERY menu rather than just this one.
        //
        // Capture is held per layout entity. A player with nothing open should have it on none of
        // them, but a leaked handle - a menu rebuilt without disposing the old one, a plugin
        // reloaded - keeps an entity nobody closes, and its capture is enough to hold the cursor
        // no matter how thoroughly this menu releases its own. Releasing where it was never taken
        // is a no-op, so the sweep costs nothing and removes the whole class of failure.
        Server.NextFrame(() => Panorama.ReleaseInputIfIdle(slot));

        // Raise on EVERY close, not just a click on the X. A consumer that changed something on open
        // - hid the crosshair, paused a timer - has to be able to undo it, and the closes it cannot
        // see coming are exactly the ones that matter: a round restart, a Dispose, a Save. Without
        // this the player keeps whatever the menu did to them until they notice.
        if (Utilities.GetPlayerFromSlot(slot) is { IsValid: true } player)
        {
            ApplyHudFlags(player, hide: false);
            Raise(player, PanelAction.Close, _contract.CloseButtonId, null, 0, Array.Empty<string>());
        }
    }

    /// <summary>
    /// Puts the layout back the way it was before this handle touched it, for one viewer.
    ///
    /// <para>Hiding the root is not enough on its own. Classes are per (slot, panel, name) on an
    /// entity that survives round restarts, so anything still set is simply inherited - by this
    /// player on their next open, or by the next player to take the slot. Tell the layout it is
    /// closed, empty the rows, and take back every class this handle turned on.</para>
    /// </summary>
    /// <summary>
    /// Hides one viewer's panel.
    ///
    /// <para><paramref name="clearTrackedClasses"/> is false for a normal close and true only when
    /// the slot is being reset. The classes a consumer sets are not all transient - a position, an
    /// accent, a size modifier are structural, and the CSS exit animation is written against them.
    /// Stripping them at close pulled the panel's own geometry out from under the animation: the
    /// now-playing card lost pos-top and snapped to the middle, the admin panel lost its height
    /// constraint and grew. Only the reveal class comes off here; the rest is left alone so the
    /// panel exits looking like itself.</para>
    ///
    /// <para>Leftovers are still dealt with, just not here - Open scrubs before the first draw, so
    /// a reopened panel never inherits a stale class (a w-step bar being the case that bit), and
    /// ResetSlot scrubs on the way out so the next occupant of the slot inherits nothing.</para>
    /// </summary>
    private void ScrubPanel(int slot, bool clearTrackedClasses, bool fromDrain = false)
    {
        // Resolvable, not alive. IsEntityAlive only consults the cached index, and a world reset
        // invalidates that index while the engine keeps the entity - so it reported "dead" for an
        // entity that is right there and that every write path would have found, because they all
        // resolve by ADOPTING. Bailing on that answer is the frozen-panel bug: the session is
        // already gone, the reveal class stays on, and nothing retries, so the panel is drawn and
        // deaf for the rest of the map. Resolvable adopts and still never spawns - building a
        // layout entity for the sole purpose of telling it to hide would be a leak with no upside.
        //
        // Checked BEFORE the tracked set is taken, not after. Taking it first forgot every class
        // this handle had turned on for the slot and then returned without removing any of them,
        // which is what made a leftover permanent: this record is the only thing in the process that
        // knows which classes were set.
        if (!_renderer.IsEntityResolvable())
        {
            // Genuinely nothing in the world to write into. That is usually also nothing to undo -
            // a fresh entity carries no per-player class state - but "usually" is what left panels
            // stuck before, so the slot is queued instead of dropped. The tracked classes are
            // untouched above, so the retry can still name them.
            _pendingScrub[slot] = clearTrackedClasses
                               || (_pendingScrub.TryGetValue(slot, out var owed) && owed);

            // One retry next frame, which is when the common cause clears: a world reset drops the
            // index and the replacement entity exists a frame later. If that fails too the slot
            // stays queued and the next Open, spawn or world reset drains it - no timer, and no
            // rescheduling from the drain itself, so this cannot spin.
            Server.NextFrame(DrainPendingScrubs);
            return;
        }

        // Tell the layout it is closed. Clearing the rows on their own leaves an empty card sitting
        // on screen - the layout has no idea what "closed" means, it only knows what classes it was
        // last told to wear.
        //
        // Issued BEFORE the tracked set is taken, and its answer is kept. Both matter: a refusal
        // below has to leave the tracked classes where they are so the retry can still name them.
        var hidden = _contract.RevealClass is { } reveal
            ? _renderer.SetClass(slot, _contract.RootPanelId, reveal, false)
            : _renderer.SetClass(slot, _contract.RootPanelId, _contract.HiddenClass, true);

        // A resolvable entity is not a landed write, and this return was being thrown away. SetClass
        // answers HasPlayerState - the same answer Render judges its first draw on - so the hide can
        // be refused for a slot whose per-player state is not allocated, and that lands in exactly
        // the state the unresolvable branch above exists to prevent: panel drawn, reveal class on,
        // no session behind it, nothing retrying. It was also unobservable, because the diagnostic
        // only prints Revealed while a session exists. Queue it the same way.
        if (!hidden)
        {
            _pendingScrub[slot] = clearTrackedClasses
                               || (_pendingScrub.TryGetValue(slot, out var stillOwed) && stillOwed);

            // Not rescheduled from the drain itself. The drain terminates because ScrubPanel only
            // re-queues when the entity is unresolvable, which it checks once up front; this branch
            // can re-queue with a live entity, so a slot whose state is never allocated - a
            // disconnected one - would retry every frame for the rest of the map. Left queued
            // instead, for the next Open, spawn or world reset to drain.
            if (!fromDrain)
                Server.NextFrame(DrainPendingScrubs);

            return;
        }

        _pendingScrub.Remove(slot);

        // Taken only now that the removals at the bottom will actually be written.
        var tracked = clearTrackedClasses && _classesBySlot.Remove(slot, out var set) ? set : null;

        _renderer.RenderRows(slot, Array.Empty<MenuItem>());

        if (tracked is null)
            return;

        // Every per-viewer class this handle turned on, taken back off. The consumer cannot do this
        // itself once its own state is gone, and the entity survives round restarts - so anything
        // left here is worn for the rest of the map, by this player and then by the next one to
        // take the slot.
        foreach (var (panelId, className) in tracked)
        {
            // Same reasoning as Open's scrub: the root's classes are the panel's own geometry and
            // are partly declared by the layout, so removing them is not undoing our own work. The
            // reveal class is the one root class this method does take off, deliberately, above.
            if (panelId == _contract.RootPanelId) continue;

            _renderer.SetClass(slot, panelId, className, false);
        }
    }

    /// <summary>
    /// Runs the scrubs that had no entity to write into when they were asked for.
    ///
    /// <para>Cheap to call from anywhere: the queue is empty in every normal life of a menu, and the
    /// entity check is a dictionary lookup once the index resolves. It deliberately does NOT
    /// reschedule itself - it returns while there is still nothing to write into, and is driven
    /// instead by the moments that create an entity or prove one exists.</para>
    /// </summary>
    private void DrainPendingScrubs()
    {
        if (_pendingScrub.Count == 0)
            return;

        // Asked once here rather than once per slot, and it is also what makes the loop terminate:
        // this is the only re-queue path ScrubPanel reschedules from, and it cannot be reached from
        // inside the loop. Its other re-queue - a refused write on a live entity - is passed
        // fromDrain below precisely so it does not schedule another frame from here.
        if (!_renderer.IsEntityResolvable())
            return;

        foreach (var (slot, clearTrackedClasses) in _pendingScrub.ToList())
        {
            // A slot that has a session does not owe a hide. The queue is keyed by slot with no
            // identity, and entries outlive the close that made them: ResetSlot queues on every
            // connect and disconnect, so on a fresh map - where no entity exists yet and even Adopt
            // cannot succeed - every player who joins leaves one behind. Draining it later, against
            // a panel somebody has since opened, blanks that panel while the session stays live:
            // cursor held, IsOpenFor still true, and no consumer told anything happened. Worse, the
            // entry may have been queued for the PREVIOUS occupant of the slot entirely.
            if (_sessions.ContainsKey(slot))
            {
                _pendingScrub.Remove(slot);
                continue;
            }

            ScrubPanel(slot, clearTrackedClasses, fromDrain: true);
        }
    }

    /// <summary>
    /// Draws one viewer's page. Returns false if the draw did not reach the client.
    ///
    /// <para>Only the two writes every layout must have are judged: the reveal that makes the panel
    /// visible, and the title. Both fail for the reasons that matter - no entity, unresolved
    /// natives, per-player writes refused - and neither is optional in any layout the contract
    /// describes. The rest is left best-effort on purpose: a missing tab panel or a variable a
    /// layout does not declare is a layout detail, not a dead render, and failing the draw on one
    /// would close working menus.</para>
    /// </summary>
    private bool Render(PanelSession session)
    {
        var page  = Math.Clamp(session.Page, 0, PageCount - 1);
        session.Page = page;

        var rows = _items.Skip(page * PageSize).Take(PageSize).ToList();

        session.RowMap.Clear();
        for (var i = 0; i < rows.Count; i++)
            session.RowMap[i] = rows[i];

        // Undo whatever Close did. Open on a fresh session hits this too, harmlessly.
        var revealed = _contract.RevealClass is { } reveal
            ? _renderer.SetClass(session.Slot, _contract.RootPanelId, reveal, true)
            : _renderer.SetClass(session.Slot, _contract.RootPanelId, _contract.HiddenClass, false);

        foreach (var (group, value) in _variants)
            _renderer.SetClass(session.Slot, _contract.RootPanelId, $"{group}-{value}", true);

        _renderer.RenderRows(session.Slot, rows);

        // Deliberately not short-circuited - the draw finishes either way, and a half-written panel
        // that reports failure is closed, not left half-drawn.
        var titled = _renderer.SetVariable(session.Slot, _contract.TitleVar, Title);

        _renderer.SetVariable(session.Slot, _contract.SubtitleVar, Subtitle);
        _renderer.SetVariable(session.Slot, _contract.PageVar, $"{page + 1} / {PageCount}");
        _renderer.SetClass(session.Slot, _contract.RootPanelId, _contract.PagedClass, PageCount > 1);

        foreach (var (name, value) in _vars)
            _renderer.SetVariable(session.Slot, name, value);

        foreach (var tab in Tabs)
        {
            var active = tab == session.ActiveTab;
            _renderer.SetClass(session.Slot, _contract.TabButtonId(tab), _contract.ActiveClass, active);
            _renderer.SetClass(session.Slot, _contract.TabPageId(tab), _contract.ActiveClass, active);
            _renderer.SetClass(session.Slot, _contract.TabPageId(tab), _contract.HiddenClass, !active);
        }

        // Bookkeeping for css_panorama_diag, and nothing else reads it. Both halves are invisible
        // from outside: the reveal is written here rather than through the tracked SetClassFor path,
        // so the class dump never shows it, and a session that stopped being redrawn looks exactly
        // like one redrawn a moment ago.
        session.LastRenderAt = DateTime.UtcNow;
        session.RenderCount++;
        session.Revealed = revealed;

        return revealed && titled;
    }

    /// <summary>Resolves a raw click against this handle. Returns false if it wasn't ours, so the
    /// dispatcher can try the next open handle.</summary>
    internal bool TryHandle(RawInteraction raw)
    {
        if (raw.Player is not { IsValid: true } player)
            return false;

        // The session is asked FIRST, and the order is load-bearing: OwnsEntity resolves through
        // the renderer, which spawns when nothing is cached, so asking it first meant every click
        // anywhere spawned a layout entity for every menu the clicker does not have open. Duplicate
        // entities for one layout are how a click gets routed to entity A while the client draws
        // entity B, which reads as "clicks stopped landing" and is invisible from the server.
        if (!_sessions.TryGetValue(player.Slot, out var session))
            return false;

        // When the transport knows which layout was clicked, that decides it. Every layout declares
        // its own row0_btn, so with two menus open for one player the element id is ambiguous and
        // session matching alone would hand the click to whichever handle was created first.
        if (raw.Layout != IntPtr.Zero && !_renderer.OwnsEntity(raw.Layout))
            return false;

        // Spoofable transports carry a token; an unspoofable one (the engine click hook) doesn't.
        if (raw.Token is not null && raw.Token != session.Token)
        {
            _logger.LogWarning(
                "[Panorama] rejected interaction with a stale or forged token from {Player} (menu {MenuId})",
                player.PlayerName, Id);

            return true;
        }

        Dispatch(player, session, raw);

        return true;
    }

    private void Dispatch(CCSPlayerController player, PanelSession session, RawInteraction raw)
    {
        var element = raw.ElementId;

        if (element == _contract.CloseButtonId)
        {
            // Close raises the event itself, so every close reports the same way.
            Close(session.Slot);
            return;
        }

        if (element == _contract.PrevButtonId || element == _contract.NextButtonId)
        {
            var delta   = element == _contract.NextButtonId ? 1 : -1;
            var wrapped = (session.Page + delta + PageCount) % PageCount;

            session.Page = wrapped;
            Render(session);
            Raise(player, PanelAction.Page, element, null, wrapped, raw.Args);
            return;
        }

        if (TryMatchTab(element) is { } tabId)
        {
            session.ActiveTab = tabId;
            Render(session);
            Raise(player, PanelAction.Tab, tabId, null, session.Page, raw.Args);
            return;
        }

        if (TryMatchRow(element) is { } rowIndex)
        {
            // The physical row is meaningless on its own - it only means something paired with
            // what this player had drawn there at the time.
            if (!session.RowMap.TryGetValue(rowIndex, out var item))
                return;

            if (!item.Enabled)
                return;

            Raise(player, PanelAction.Click, item.Id, item, session.Page, raw.Args);
            return;
        }

        Raise(player, PanelAction.Button, element, null, session.Page, raw.Args);
    }

    private string? TryMatchTab(string element)
        => Tabs.FirstOrDefault(tab => _contract.TabButtonId(tab) == element);

    private int? TryMatchRow(string element)
    {
        for (var i = 0; i < _contract.RowCount; i++)
        {
            if (_contract.RowButtonId(i) == element)
                return i;
        }

        return null;
    }

    private void Raise(
        CCSPlayerController player,
        PanelAction          action,
        string              elementId,
        MenuItem?           item,
        int                 page,
        string[]            args)
    {
        try
        {
            var raised = new PanelEvent
            {
                Player    = player,
                Menu      = this,
                Action    = action,
                ElementId = elementId,
                Item      = item,
                Page      = page,
                Args      = args,
            };

            // OnEvent first, so a central handler can authorise and veto before any row-specific
            // code runs. See PanelEvent.Cancel.
            OnEvent?.Invoke(raised);

            if (!raised.Cancel)
                item?.OnSelect?.Invoke(raised);
        }
        catch (Exception e)
        {
            // A consumer's handler throwing must not take down the click hook.
            _logger.LogError(e, "[Panorama] consumer handler threw for menu {MenuId} element {Element}", Id, elementId);
        }
    }

    /// <summary>Drops sessions and cached entity handles after a world reset. The layout entity is
    /// gone, so anyone who had the menu open no longer does.</summary>
    /// <summary>
    /// Rebuilds every open menu after the world is reset.
    ///
    /// <para>A round restart bulk-deletes non-player entities, taking the layout entity with it. The
    /// sessions then point at nothing: the panel stays on screen because the client was never told
    /// otherwise, and every click is dropped because there is no entity to route it to. Closing them
    /// was a way to stop that being confusing, not a fix - the player still lost their menu every
    /// round.</para>
    ///
    /// <para>So: remember who had it open and on what page, drop the dead entity handle, and put it
    /// back a frame later once the reset has finished. A fresh entity means fresh intern tables, so
    /// everything is re-sent rather than diffed - which a full render does anyway.</para>
    /// </summary>
    internal void OnWorldReset()
    {
        // A round restart does NOT delete this entity - Valve put custom_hud_layout on the engine's
        // preserved-classname list, so it survives the wipe that takes ordinary non-player entities.
        // A map change still takes it.
        //
        // Assuming death unconditionally was actively harmful: Invalidate only forgets our index, so
        // a live entity was orphaned rather than replaced, a duplicate was spawned a frame later,
        // and every consumer was told to redraw a panel that had never actually gone away. Once per
        // round, for the life of the map.
        //
        // Still tell consumers to redraw. The entity survived, but that is not a promise the client
        // kept the panel it was drawing, and a redraw is cheap next to a blank card.
        // Owed scrubs first: this is the one moment that knows whether the entity came back, and a
        // scrub queued by the previous reset is for a slot nobody is going to close again.
        DrainPendingScrubs();

        if (_renderer.IsEntityAlive())
        {
            foreach (var session in _sessions.Values.ToList())
            {
                if (Utilities.GetPlayerFromSlot(session.Slot) is not { IsValid: true } player)
                    continue;

                // The slot is occupied - but by whom? A session outlives the player it belongs to
                // whenever the disconnect was missed, and the next occupant inherits it. Restored
                // would then hand a consumer somebody else's menu state under a live controller,
                // which is how a stranger's admin panel ends up rendered for whoever took the slot.
                if (player.SteamID != session.SteamId)
                {
                    _logger.LogWarning(
                        "[Panorama] menu {MenuId} had a session on slot {Slot} belonging to another "
                        + "player; discarding it rather than restoring it for {Player}.",
                        Id, session.Slot, player.PlayerName);

                    ResetSlot(session.Slot);
                    continue;
                }

                Raise(player, PanelAction.Restored, _contract.RootPanelId, null, session.Page, Array.Empty<string>());
            }

            return;
        }

        var reopen = _sessions.Values
            .Select(session => (session.Slot, session.SteamId, session.Page, session.ActiveTab))
            .ToList();

        // Released BEFORE the sessions are dropped. Restore runs a frame later and skips any player
        // who is not valid right then, which during a map change is most of them - and once the
        // session is gone, Close returns at its !_sessions.Remove guard and never reaches the
        // release. That strands the player in cursor mode with nothing left that knows to undo it.
        if (_contract.CaptureInput)
        {
            foreach (var (slot, _, _, _) in reopen)
                SetCapture(slot, false);
        }

        _sessions.Clear();

        // Cancelled, not just forgotten. Clearing the set alone left Panorama still holding the
        // prompt: it kept swallowing that player's chat for the rest of its timeout with nothing on
        // screen to answer, and the eventual result bailed at OnPromptResult's !_promptSlots.Remove
        // guard so it never echoed anywhere. Sessions are already gone above, so delivery cannot
        // draw into the entity being deleted.
        foreach (var slot in _promptSlots.ToList())
            Panorama.CancelPrompt(slot, TextPromptOutcome.Abandoned);

        _promptSlots.Clear();
        _renderer.Invalidate();

        if (reopen.Count == 0)
            return;

        // Next frame, not now: the entity is being deleted as part of this reset, and spawning a
        // replacement into a world that is still tearing down gets it deleted too.
        Server.NextFrame(() => Restore(reopen));
    }

    private void Restore(List<(int Slot, ulong SteamId, int Page, string? ActiveTab)> reopen)
    {
        foreach (var (slot, steamId, page, tab) in reopen)
        {
            if (Utilities.GetPlayerFromSlot(slot) is not { IsValid: true } player)
            {
                // Close, not just a capture release. OnWorldReset took the dead branch on the
                // strength of IsEntityAlive, which reads only the cached index - so when that answer
                // was wrong (the entity was preserved, the index merely forgotten) the reveal class
                // was never taken off anywhere: OnWorldReset does not scrub, and returning here left
                // a panel drawn with its session already cleared and nothing that would ever retry.
                // Close with no session is exactly the scrub-and-release path this needs, and it is
                // a no-op when there was genuinely nothing there.
                Close(slot);
                continue;
            }

            // Same check as the alive branch above, for the same reason: a map change is exactly
            // when a slot changes hands, and reopening on the slot number alone hands the new
            // occupant the previous one's menu.
            if (player.SteamID != steamId)
            {
                ResetSlot(slot);
                continue;
            }

            var session = new PanelSession
            {
                Slot      = slot,
                SteamId   = steamId,
                Token     = Guid.NewGuid().ToString("N")[..12],
                Page      = page,
                ActiveTab = tab,
            };

            _sessions[slot] = session;

            // Same failure as a first draw: if the redraw wrote nothing there is no panel to hold a
            // cursor over, so close instead of capturing input over an empty screen.
            if (!Render(session))
            {
                _logger.LogError(
                    "[Panorama] menu {MenuId} could not be redrawn for {Player} after a world reset; "
                    + "closing it.", Id, player.PlayerName);

                Close(slot);
                continue;
            }

            // Guarded exactly as Open is. Capturing input for a layout that opted out strands the
            // player in cursor mode: Close only releases the capture when CaptureInput is set, so
            // nothing ever turns it back off. That is the toast case - non-interactive, hittest
            // false, and no close button to escape with.
            if (_contract.CaptureInput)
                SetCapture(slot, true);

            ApplyHudFlags(player, hide: true);

            // The library restored what it knows: rows, title, variables set through the handle.
            // Per-viewer writes it never saw the meaning of are the consumer's to redraw.
            Raise(player, PanelAction.Restored, _contract.RootPanelId, null, page, Array.Empty<string>());
        }

        _logger.LogDebug("[Panorama] restored {Count} viewer(s) of menu {MenuId} after world reset",
            reopen.Count, Id);
    }

    /// <summary>
    /// Applies or restores this menu's <see cref="LayoutContract.HideHud"/> flags for a viewer. See
    /// <see cref="Panorama.SetHideHud"/> for why this is a pawn field and not a client command.
    ///
    /// <para>Restoring is driven from <see cref="Close(int)"/>, which runs for a round restart and a
    /// Dispose as well as a click on the X - otherwise a menu that vanished on its own would leave
    /// the player's HUD altered until they noticed.</para>
    ///
    /// <para>Logged when it fails, because the failure is otherwise silent and has an obvious cause:
    /// the flags live on the <b>pawn</b>, so a dead or spectating player has nothing to carry them,
    /// and a respawn hands out a fresh pawn without them.</para>
    /// </summary>
    private void ApplyHudFlags(CCSPlayerController player, bool hide)
    {
        if (_contract.HideHud == HideHudFlags.None)
            return;

        if (!Panorama.SetHideHud(player, _contract.HideHud, hide))
        {
            _logger.LogWarning(
                "[Panorama] {Action} of HUD flags {Flags} for {Player} did nothing - no valid pawn. "
                + "Dead or spectating players have no pawn to carry them, and respawning drops them.",
                hide ? "hiding" : "restoring", _contract.HideHud, player.PlayerName);
        }
    }

    /// <summary>
    /// Drops a disconnecting player's session and clears the per-player state they leave behind.
    ///
    /// <para>Slots are recycled. Per-player state is keyed by slot and outlives the player who owned
    /// it, so a leftover input capture drops the next person to take that slot straight into cursor
    /// mode with no menu on screen to close. Clearing on the way out is cheaper than detecting it on
    /// the way in.</para>
    /// </summary>
    /// <summary>
    /// Re-applies this panel's HUD flags for a player who still has it open.
    ///
    /// <para>The flags live in <c>m_iHideHUD</c> on the player's PAWN, and respawning gives them a
    /// new pawn with the field back at its default. So a menu that hid the crosshair loses that the
    /// moment its owner respawns, and the crosshair reappears on top of the panel. Called from the
    /// spawn hook rather than reapplied on a timer, because spawning is the only thing that drops
    /// them.</para>
    /// </summary>
    /// <summary>Drops this menu's input capture for a slot, whatever the session state. The
    /// last resort behind css_cursor.</summary>
    internal void ForceReleaseInput(int slot)
    {
        // Guarded on the entity for the same reason ResetSlot is: the renderer resolves by
        // SPAWNING, so an unguarded release builds a custom_hud_layout for a menu nobody has ever
        // opened in order to tell it to stop capturing input it was never capturing. This runs on
        // every close and every spawn, across every handle, so that was a steady drip of entities.
        if (_contract.CaptureInput && _renderer.IsEntityAlive())
            SetCapture(slot, false);
    }

    /// <summary>True when this menu is showing for a slot.</summary>
    internal bool HasSession(int slot) => _sessions.ContainsKey(slot);

    /// <summary>
    /// Gets a player out, whatever state this menu is in. Returns true if it had one open.
    ///
    /// <para>Behind <c>css_cursor</c>, and the reason that command exists: every automatic release
    /// is driven from something the stuck player cannot make happen. A capture held over a panel
    /// the client never drew is not released by respawning (the spawn sweep skips a slot with a
    /// live session), not by a round restart (the entity is preserved, so sessions and capture are
    /// deliberately kept), and not by the close button (there is nothing on screen to click). Only
    /// a reconnect cleared it. Closing is preferred over a bare release so consumers hear about it
    /// and HUD flags go back.</para>
    /// </summary>
    internal bool ReleaseCursor(int slot)
    {
        // Close either way, rather than a bare release for a slot with no session. No session is the
        // state this command exists FOR: a panel left drawn with its reveal class on and its session
        // already dropped ignores the close button, so the player has nothing to click and the one
        // escape hatch was taking the branch that scrubs nothing and answering "released anyway".
        // Close(int) is a no-op-plus-scrub in that case and releases capture on the way through, so
        // it does everything ForceReleaseInput did and the thing it did not.
        var had = _sessions.ContainsKey(slot);

        Close(slot);
        return had;
    }

    /// <summary>
    /// The entity to hide from this slot, or null when it should be sent as normal.
    ///
    /// <para>Called once per player per tick, so it does no work beyond a dictionary lookup and
    /// never spawns anything.</para>
    /// </summary>
    internal uint? EntityToHideFrom(int slot)
    {
        if (!_contract.HideFromSpectators) return null;
        if (_sessions.ContainsKey(slot)) return null;

        // Recently closed: keep sending the entity so the hide that close wrote actually ships.
        // Expired here rather than on a timer - this is the only reader, and it runs every tick.
        if (_closingUntil.TryGetValue(slot, out var until))
        {
            if (DateTime.UtcNow < until) return null;

            _closingUntil.Remove(slot);
        }

        return _renderer.EntityIndexIfSpawned;
    }

    internal void OnPlayerSpawn(CCSPlayerController player)
    {
        if (player is not { IsValid: true }) return;

        // A spawn is the cheapest proof that the world is up and an entity can be resolved, which is
        // what a scrub queued during a map change was waiting for.
        DrainPendingScrubs();

        var open = _sessions.ContainsKey(player.Slot);

        // Spawning with no menu open means nothing should be holding the cursor. A capture that
        // survived a world reset - the entity it was set on is gone, so nothing else will ever turn
        // it off - leaves the player unable to move and unable to close anything, since a panel
        // without a session ignores its own close button. Releasing one that was never taken is a
        // no-op, so this is a safety net rather than a cost.
        if (!open)
        {
            // Swept across every menu, not just this one: an orphaned handle elsewhere is enough
            // to hold the cursor, and spawning with nothing open is the clearest moment to be sure
            // the player has none of it.
            Panorama.ReleaseInputIfIdle(player.Slot);
            return;
        }

        if (_contract.HideHud == HideHudFlags.None) return;

        ApplyHudFlags(player, hide: true);
    }

    /// <summary>
    /// Wipes this menu's state for one slot, open or not. Called at both ends of a slot's life -
    /// see <c>Panorama.ResetSlot</c>.
    ///
    /// <para>The early return this replaced is what made the leftovers permanent: no session meant
    /// nothing to clean up, which is exactly backwards - a slot with no session is precisely the one
    /// carrying state nobody owns. Every write here is idempotent and silently refused when there is
    /// nothing set, so running it on every join and every leave costs a handful of native calls.</para>
    ///
    /// <para>No Close event and no HUD-flag restore: the controller is gone or belongs to someone
    /// else, so there is nobody to hand either to. Close covers the cases where there is.</para>
    /// </summary>
    internal void ResetSlot(int slot)
    {
        // Same reason as Close: the scrub at the bottom is a write into the entity, and it only
        // reaches the client while the entity is still being transmitted to this slot.
        _closingUntil[slot] = DateTime.UtcNow + CloseTransmitGrace;

        _sessions.Remove(slot);

        // Before the renderer work, so the chat handler stops routing to a dead menu even if every
        // write below is refused.
        if (_promptSlots.Remove(slot))
            Panorama.CancelPrompt(slot, TextPromptOutcome.Abandoned);

        // Unconditional, unlike Open's guarded take: a capture left on strands the next occupant of
        // this slot in cursor mode with no panel to close, and releasing one never taken is a no-op.
        // Guarded on the entity only so that a player joining a server where this menu has never
        // opened is not what spawns its layout entity - a fresh entity captures nobody's input.
        // Adoption satisfies that guard without spawning, and a capture held on an entity whose
        // index we forgot is exactly the one nothing else will ever release.
        if (_renderer.IsEntityResolvable())
            SetCapture(slot, false);

        // The slot is changing hands, so nothing here is worth preserving and everything left is
        // inherited by whoever lands on it next.
        ScrubPanel(slot, clearTrackedClasses: true);
    }
}
