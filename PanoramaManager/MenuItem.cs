using System;

namespace PanoramaManager;

/// <summary>One row in a menu. <paramref name="Id"/> is what comes back on
/// <see cref="PanelEvent.ElementId"/> when the row is clicked, so make it meaningful
/// (e.g. <c>player_3</c>) rather than positional.</summary>
/// <param name="Id">Stable identifier for this row, echoed back on click.</param>
/// <param name="Title">Main line of text.</param>
/// <param name="Subtitle">Optional second line.</param>
/// <param name="OnSelect">
/// Runs when this row is clicked, after the menu's own <see cref="PanelHandle.OnEvent"/> and only if
/// that handler did not set <see cref="PanelEvent.Cancel"/>. Handling a row where you build it beats
/// switching on an id in a central handler - the action lives next to the thing it acts on, and the
/// closure already has the target:
/// <code>
/// new MenuItem($"act:kick:{p.Slot}", "Kick", "Remove from the server", e => Kick(p))
/// </code>
/// Leave it null and handle the row in <see cref="PanelHandle.OnEvent"/> instead. Both fire, so a
/// menu can mix the two - per-row callbacks for the actions, one <c>OnEvent</c> for logging or for
/// the authorisation that has to apply to every row.
/// </param>
/// <param name="Enabled">When false the row renders dimmed and clicks are ignored.</param>
/// <param name="Tag">Arbitrary payload the consumer can hang off the row. Never sent to the client.</param>
public sealed record MenuItem(
    string             Id,
    string             Title,
    string?            Subtitle = null,
    Action<PanelEvent>? OnSelect = null,
    bool               Enabled  = true,
    object?            Tag      = null)
{
    public static MenuItem Of(string id, string title, string? subtitle = null)
        => new(id, title, subtitle);

    /// <summary>A row that runs <paramref name="onSelect"/> when clicked.</summary>
    public static MenuItem Of(string id, string title, string? subtitle, Action<PanelEvent> onSelect)
        => new(id, title, subtitle, onSelect);
}
