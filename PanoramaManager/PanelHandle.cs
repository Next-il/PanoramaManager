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

    private bool _disposed;

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
        if (player is { IsValid: true } && _sessions.ContainsKey(player.Slot))
            _renderer.SetClass(player.Slot, panelId, className, enabled);

        return this;
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

        var session = new PanelSession
        {
            Slot      = player.Slot,
            Token     = Guid.NewGuid().ToString("N")[..12],
            ActiveTab = Tabs.FirstOrDefault(),
        };

        _sessions[player.Slot] = session;

        // Without input capture the layout's buttons never receive the mouse, so an interactive
        // menu would render correctly and be completely inert. A read-only layout opts out - see
        // LayoutContract.CaptureInput.
        if (_contract.CaptureInput)
            _renderer.SetInputCapture(session.Slot, true);
        ApplyHudFlags(player, hide: true);
        Render(session);
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
    public PanelHandle PromptText(CCSPlayerController player, TextPrompt prompt)
    {
        if (player is { IsValid: true } && _sessions.ContainsKey(player.Slot))
        {
            _promptSlots.Add(player.Slot);
            Panorama.BeginPrompt(this, player, prompt);
        }

        return this;
    }

    /// <summary>Echoes a finished prompt into the layout. Called by <see cref="Panorama"/>.</summary>
    internal void OnPromptResult(TextPrompt prompt, TextPromptResult result)
    {
        foreach (var slot in _promptSlots.ToList())
        {
            _promptSlots.Remove(slot);

            if (_sessions.ContainsKey(slot))
                _renderer.SetVariable(slot, prompt.Variable, result.Text);
        }
    }

    /// <summary>How many players currently have this menu open.</summary>
    public int OpenCount => _sessions.Count;

    /// <summary>True if this player currently has the menu open.</summary>
    public bool IsOpenFor(CCSPlayerController player)
        => player is { IsValid: true } && _sessions.ContainsKey(player.Slot);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        foreach (var slot in _sessions.Keys.ToList())
            Close(slot);

        Panorama.Forget(this);
    }

    private void Close(int slot)
    {
        if (!_sessions.Remove(slot))
            return;

        // A prompt outliving its menu would keep swallowing this player's chat with nothing on
        // screen to answer.
        if (_promptSlots.Remove(slot))
            Panorama.CancelPrompt(slot, TextPromptOutcome.Abandoned);

        // Tell the layout it is closed. Clearing the rows on their own leaves an empty card sitting
        // on screen - the layout has no idea what "closed" means, it only knows what classes it was
        // last told to wear.
        if (_contract.RevealClass is { } reveal)
            _renderer.SetClass(slot, _contract.RootPanelId, reveal, false);
        else
            _renderer.SetClass(slot, _contract.RootPanelId, _contract.HiddenClass, true);
        _renderer.RenderRows(slot, Array.Empty<MenuItem>());
        if (_contract.CaptureInput)
            _renderer.SetInputCapture(slot, false);

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

    private void Render(PanelSession session)
    {
        var page  = Math.Clamp(session.Page, 0, PageCount - 1);
        session.Page = page;

        var rows = _items.Skip(page * PageSize).Take(PageSize).ToList();

        session.RowMap.Clear();
        for (var i = 0; i < rows.Count; i++)
            session.RowMap[i] = rows[i];

        // Undo whatever Close did. Open on a fresh session hits this too, harmlessly.
        if (_contract.RevealClass is { } reveal)
            _renderer.SetClass(session.Slot, _contract.RootPanelId, reveal, true);
        else
            _renderer.SetClass(session.Slot, _contract.RootPanelId, _contract.HiddenClass, false);

        foreach (var (group, value) in _variants)
            _renderer.SetClass(session.Slot, _contract.RootPanelId, $"{group}-{value}", true);

        _renderer.RenderRows(session.Slot, rows);
        _renderer.SetVariable(session.Slot, _contract.TitleVar, Title);
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
    }

    /// <summary>Resolves a raw click against this handle. Returns false if it wasn't ours, so the
    /// dispatcher can try the next open handle.</summary>
    internal bool TryHandle(RawInteraction raw)
    {
        if (raw.Player is not { IsValid: true } player)
            return false;

        // When the transport knows which layout was clicked, that decides it. Every layout declares
        // its own row0_btn, so with two menus open for one player the element id is ambiguous and
        // session matching alone would hand the click to whichever handle was created first.
        if (raw.Layout != IntPtr.Zero && !_renderer.OwnsEntity(raw.Layout))
            return false;

        if (!_sessions.TryGetValue(player.Slot, out var session))
            return false;

        // Spoofable transports carry a token; an unspoofable one (the engine click hook) doesn't.
        if (raw.Token is not null && raw.Token != session.Token)
        {
            _logger.LogWarning(
                "[HudMenu] rejected interaction with a stale or forged token from {Player} (menu {MenuId})",
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
            _logger.LogError(e, "[HudMenu] consumer handler threw for menu {MenuId} element {Element}", Id, elementId);
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
        var reopen = _sessions.Values
            .Select(session => (session.Slot, session.Page, session.ActiveTab))
            .ToList();

        _sessions.Clear();
        _promptSlots.Clear();
        _renderer.Invalidate();

        if (reopen.Count == 0)
            return;

        // Next frame, not now: the entity is being deleted as part of this reset, and spawning a
        // replacement into a world that is still tearing down gets it deleted too.
        Server.NextFrame(() => Restore(reopen));
    }

    private void Restore(List<(int Slot, int Page, string? ActiveTab)> reopen)
    {
        foreach (var (slot, page, tab) in reopen)
        {
            if (Utilities.GetPlayerFromSlot(slot) is not { IsValid: true } player)
                continue;

            var session = new PanelSession
            {
                Slot      = slot,
                Token     = Guid.NewGuid().ToString("N")[..12],
                Page      = page,
                ActiveTab = tab,
            };

            _sessions[slot] = session;

            _renderer.SetInputCapture(slot, true);
            ApplyHudFlags(player, hide: true);
            Render(session);

            // The library restored what it knows: rows, title, variables set through the handle.
            // Per-viewer writes it never saw the meaning of are the consumer's to redraw.
            Raise(player, PanelAction.Restored, _contract.RootPanelId, null, page, Array.Empty<string>());
        }

        _logger.LogInformation("[HudMenu] restored {Count} viewer(s) of menu {MenuId} after world reset",
            _sessions.Count, Id);
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
                "[HudMenu] {Action} of HUD flags {Flags} for {Player} did nothing - no valid pawn. "
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
    internal void OnPlayerDisconnect(int slot)
    {
        if (!_sessions.Remove(slot))
            return;

        _promptSlots.Remove(slot);

        // Best-effort: the entity may already be gone, in which case these no-op.
        _renderer.SetInputCapture(slot, false);
        _renderer.RenderRows(slot, Array.Empty<MenuItem>());

        if (_contract.RevealClass is { } reveal)
            _renderer.SetClass(slot, _contract.RootPanelId, reveal, false);
        else
            _renderer.SetClass(slot, _contract.RootPanelId, _contract.HiddenClass, true);
    }
}
