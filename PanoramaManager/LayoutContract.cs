namespace PanoramaManager;

/// <summary>
/// The naming convention a layout must follow for <see cref="PanelHandle"/> to drive it. The
/// bundled Workshop layout implements this exactly; a custom layout only has to match the names.
///
/// <para>Everything is a plain string so a consumer shipping their own addon layout can override
/// the prefixes on the <see cref="PanelHandle"/> without recompiling this library.</para>
/// </summary>
public sealed class LayoutContract
{
    public static LayoutContract Default { get; } = new();

    /// <summary>Panel whose dialog variables carry the whole menu's state. Panorama scopes dialog
    /// variables to the panel they're set on, so a single root panel keeps every write in one place.
    /// A layout that sets variables per-row should set this to null and rely on <see cref="RowPanelId"/>.</summary>
    public string RootPanelId { get; init; } = "HudMenuRoot";

    /// <summary>Number of physical row panels declared in the layout. This is the page size -
    /// the renderer never shows more rows than this and paginates past it.</summary>
    public int RowCount { get; init; } = 10;

    /// <summary>Dialog variable holding the menu title.</summary>
    public string TitleVar { get; init; } = "menu_title";

    /// <summary>Dialog variable holding the second line of the header.</summary>
    public string SubtitleVar { get; init; } = "menu_subtitle";

    /// <summary>Dialog variable holding the "Page 2 / 7" indicator.</summary>
    public string PageVar { get; init; } = "menu_page";

    /// <summary>Class toggled on the root when the menu has more than one page, so the layout can
    /// hide the nav bar on short lists.</summary>
    public string PagedClass { get; init; } = "has-pages";

    /// <summary>
    /// Take the mouse while this layout is open.
    ///
    /// <para>On by default, because a menu whose buttons cannot be clicked is inert. Turn it off for
    /// anything the player only reads - a toast, a bar, a timer. Input capture pulls up a cursor and
    /// stops them aiming, so a notification that grabs it is worse than no notification.</para>
    /// </summary>
    public bool CaptureInput { get; init; } = true;

    /// <summary>
    /// Parts of the base HUD to hide while this menu is open, restored on close.
    ///
    /// <para><b>Defaults to none, and the crosshair does not need to be here.</b> A layout with
    /// <c>z-index: 99999</c> on its outermost panel draws above the crosshair on its own - confirmed
    /// live, with no flags set. Taking a player's crosshair away to solve a stacking problem is worse
    /// than fixing the stacking, and it has to be given back on every close, round restart and
    /// disconnect to avoid stranding them without one.</para>
    ///
    /// <para>Reach for this when a menu genuinely wants part of the HUD gone - a cutscene hiding the
    /// radar, a full-screen overlay hiding everything - not to work around draw order.</para>
    ///
    /// <para>Combine freely for a menu that wants more of the HUD out of the way:</para>
    ///
    /// <code>
    /// new LayoutContract { HideHud = HideHudFlags.Crosshair | HideHudFlags.Radar }
    /// new LayoutContract { HideHud = HideHudFlags.All }
    /// new LayoutContract { HideHud = HideHudFlags.None }     // leave the HUD alone
    /// </code>
    ///
    /// <para>Only the named bits are touched, so this composes with anything else setting HUD flags
    /// rather than clobbering it. The flags live on the player's <b>pawn</b>, so a dead or
    /// spectating viewer has nothing to set them on and a respawn drops them.</para>
    /// </summary>
    public HideHudFlags HideHud { get; init; } = HideHudFlags.None;

    /// <summary>Class that hides a panel. Must collapse it out of layout (not just make it
    /// transparent) so unused rows leave no gap.</summary>
    public string HiddenClass { get; init; } = "hidden";

    /// <summary>
    /// Class the layout wears while the menu is open, for a layout that animates its entry. Set it
    /// and the root gains the class on open and loses it on close, so a CSS transition plays both
    /// ways; leave it null and the root is collapsed with <see cref="HiddenClass"/> instead.
    ///
    /// <para>The two are alternatives, not additions. A collapsed panel is out of layout and has
    /// nothing to animate from, so a layout that wants a reveal must stay in layout at opacity 0 -
    /// which is what <c>admin_hud_kit.xml</c> does and <c>admin_hud.xml</c> does not.</para>
    /// </summary>
    public string? RevealClass { get; init; }

    /// <summary>Class marking the active tab button / tab page.</summary>
    public string ActiveClass { get; init; } = "active";

    /// <summary>Class applied to a row whose <see cref="MenuItem.Enabled"/> is false.</summary>
    public string DisabledClass { get; init; } = "disabled";

    /// <summary>Panel id of physical row <c>i</c>. Toggled with <see cref="HiddenClass"/>.</summary>
    public string RowPanelId(int index) => $"row{index}";

    /// <summary>Button id inside physical row <c>i</c>. This is what the client reports on click.</summary>
    public string RowButtonId(int index) => $"row{index}_btn";

    /// <summary>Dialog variable for row <c>i</c>'s first line.</summary>
    public string RowTitleVar(int index) => $"row{index}_title";

    /// <summary>Dialog variable for row <c>i</c>'s second line.</summary>
    public string RowSubtitleVar(int index) => $"row{index}_sub";

    /// <summary>Tab button id for logical tab <paramref name="tabId"/>.</summary>
    public string TabButtonId(string tabId) => $"tab_{tabId}";

    /// <summary>Tab page panel id for logical tab <paramref name="tabId"/>.</summary>
    public string TabPageId(string tabId) => $"page_{tabId}";

    public string PrevButtonId { get; init; } = "nav_prev";
    public string NextButtonId { get; init; } = "nav_next";
    public string CloseButtonId { get; init; } = "menu_close";
}
