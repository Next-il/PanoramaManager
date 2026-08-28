using System.Collections.Generic;

namespace PanoramaManager.Rendering;

/// <summary>
/// How a menu's state reaches the client. Swappable on purpose: today the only shippable path is
/// <see cref="CustomHudLayoutRenderer"/>, which pushes strings and class toggles into a layout the
/// client already has. If Valve later allows addon-supplied layouts with scripting, a second
/// renderer can drive those without any consumer changing a line.
/// </summary>
public interface IPanelRenderer
{
    /// <summary>Page size - how many rows the layout can physically show at once.</summary>
    int RowCapacity { get; }

    /// <summary>Draws <paramref name="rows"/> into the row pool for one player and blanks the rest.
    /// Returns false if the underlying transport isn't available (bad signature, no entity).</summary>
    bool RenderRows(int slot, IReadOnlyList<MenuItem> rows);

    /// <summary>Sets a free-form dialog variable for one player, e.g. a live timer.</summary>
    bool SetVariable(int slot, string name, string value);

    /// <summary>Toggles a class on a panel for one player.</summary>
    bool SetClass(int slot, string panelId, string className, bool enabled);

    /// <summary>Enables mouse input for one player so buttons in the layout become clickable.</summary>
    bool SetInputCapture(int slot, bool enabled);

    /// <summary>Drops any cached entity handle after a round restart or map change.</summary>
    void Invalidate();

    /// <summary>True if <paramref name="entity"/> is the layout entity this renderer drives. Used to
    /// route a click to the right menu when more than one is open.</summary>
    bool OwnsEntity(System.IntPtr entity);
}
