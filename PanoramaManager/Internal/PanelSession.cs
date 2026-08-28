using System.Collections.Generic;

namespace PanoramaManager.Internal;

/// <summary>
/// What one player is currently looking at. The row pool in the layout is fixed, so the physical
/// row a click reports is meaningless on its own - this is what turns "row 3 was clicked" back
/// into "the item at index 23 of the current list".
/// </summary>
internal sealed class PanelSession
{
    internal required int Slot { get; init; }

    /// <summary>Random per-open value. The console-command transport requires a match, so a player
    /// can't run the command by hand for a menu they don't have open.</summary>
    internal required string Token { get; init; }

    internal int Page { get; set; }

    internal string? ActiveTab { get; set; }

    /// <summary>Physical row index to the item currently drawn there. Rebuilt on every render.</summary>
    internal Dictionary<int, MenuItem> RowMap { get; } = new();
}
