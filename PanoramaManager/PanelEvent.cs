using System;
using CounterStrikeSharp.API.Core;

namespace PanoramaManager;

/// <summary>What the player did.</summary>
public enum PanelAction
{
    /// <summary>A row in the item list was clicked. <see cref="PanelEvent.Item"/> is set.</summary>
    Click,

    /// <summary>A tab button was clicked. <see cref="PanelEvent.ElementId"/> is the tab id.</summary>
    Tab,

    /// <summary>Page changed via the prev/next buttons. <see cref="PanelEvent.Page"/> is the new page.</summary>
    Page,

    /// <summary>The close button was clicked, or the menu was closed for this player.</summary>
    Close,

    /// <summary>A button that isn't a row, tab or nav control. <see cref="PanelEvent.ElementId"/> is its id.</summary>
    Button,

    /// <summary>
    /// The menu was rebuilt after the layout entity was destroyed - a round restart, typically - and
    /// is interactive again on the same page.
    ///
    /// <para>Rows, title and variables set through the handle are restored automatically. Anything
    /// written with <see cref="PanelHandle.SetVariableFor"/> or
    /// <see cref="PanelHandle.SetClassFor"/> is not: the library never saw what it meant, so only the
    /// consumer can redraw it. Handle this if your menu is anything other than a plain list.</para>
    /// </summary>
    Restored,
}

/// <summary>A single interaction, delivered to <see cref="PanelHandle.OnEvent"/>.</summary>
public sealed class PanelEvent
{
    /// <summary>Who interacted. Always the real controller as reported by the engine
    /// (or, on the console-command transport, the verified command caller).</summary>
    public required CCSPlayerController Player { get; init; }

    /// <summary>The menu that was interacted with.</summary>
    public required PanelHandle Menu { get; init; }

    public required PanelAction Action { get; init; }

    /// <summary>Raw element id from the layout. For <see cref="PanelAction.Click"/> this is the
    /// clicked <see cref="MenuItem.Id"/>, not the physical row slot.</summary>
    public required string ElementId { get; init; }

    /// <summary>The resolved row, when <see cref="Action"/> is <see cref="PanelAction.Click"/>.</summary>
    public MenuItem? Item { get; init; }

    /// <summary>Current page after the interaction.</summary>
    public int Page { get; init; }

    /// <summary>Extra tokens, only populated by the console-command transport.</summary>
    public string[] Args { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Set from <see cref="PanelHandle.OnEvent"/> to stop the clicked row's own
    /// <see cref="MenuItem.OnSelect"/> from running.
    ///
    /// <para>This is what makes a single authorisation check possible. <c>OnEvent</c> runs first
    /// precisely so it can veto: without that, a per-row callback would fire before any central
    /// gate had a chance to reject the click, and every row would need its own permission check -
    /// which is a rule you only have to forget once.</para>
    /// </summary>
    public bool Cancel { get; set; }
}
