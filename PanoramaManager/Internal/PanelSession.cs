using System;
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

    /// <summary>
    /// Who this session belongs to. Sessions are keyed by SLOT, and slots are recycled the moment
    /// one player leaves and the next connects - so the slot alone cannot say whether the person
    /// standing there is the one who opened the menu. Checked before anything is redrawn for a
    /// session that survived a world reset, which is the one place a stale session and a live
    /// player meet.
    ///
    /// <para>Zero for a bot, and bots therefore compare equal to each other. That is the correct
    /// trade: it costs a bot an unnecessary redraw, and guessing would cost a human their menu.</para>
    /// </summary>
    internal ulong SteamId { get; init; }

    /// <summary>Random per-open value. The console-command transport requires a match, so a player
    /// can't run the command by hand for a menu they don't have open.</summary>
    internal required string Token { get; init; }

    internal int Page { get; set; }

    internal string? ActiveTab { get; set; }

    /// <summary>Physical row index to the item currently drawn there. Rebuilt on every render.</summary>
    internal Dictionary<int, MenuItem> RowMap { get; } = new();

    // ------------------------------------------------------------------ diagnostics
    //
    // A class dump alone cannot tell a panel that is being redrawn from one frozen at whatever it
    // last wrote: six now-playing cards all reading "np_bar.w7 with a session" are either six
    // listeners forty seconds into the same song or six cards nothing has touched since the song
    // ended, and those look identical. These four fields are the difference.

    /// <summary>When this session was opened. Wall-clock age of the panel on screen.</summary>
    internal DateTime OpenedAt { get; } = DateTime.UtcNow;

    /// <summary>When the last render for this session ran. Null if the session has never drawn.</summary>
    internal DateTime? LastRenderAt { get; set; }

    /// <summary>How many renders this session has had. A live card climbs; a stuck one stops.</summary>
    internal int RenderCount { get; set; }

    /// <summary>
    /// Whether the last render's reveal write landed - the one that puts the root's reveal class on.
    ///
    /// <para>Worth its own field because that write is invisible to the class dump: the tracked-class
    /// record only holds what a CONSUMER turned on through SetClassFor, and the reveal is written by
    /// the library's own render. So a root stuck at <c>.hud-root</c> with no <c>.show</c> - opacity 0,
    /// still in layout, still capturing input - dumps exactly like a healthy panel.</para>
    /// </summary>
    internal bool Revealed { get; set; }
}
