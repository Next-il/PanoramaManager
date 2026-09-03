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

    /// <summary>The spawned entity's index, or null. Must not spawn.</summary>
    uint? EntityIndexIfSpawned { get; }

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

    /// <summary>Is the layout entity still live? Lets a world reset tell an entity the engine
    /// deleted from one it preserved, so a live entity is not orphaned and replaced.</summary>
    bool IsEntityAlive();

    /// <summary>
    /// Is there a layout entity this renderer can write into right now? Adopts a live entity for
    /// this layout when the cached index was forgotten, and never creates one.
    ///
    /// <para>The question <see cref="IsEntityAlive"/> answers is narrower and wrong for anyone
    /// asking "will my write land": an index invalidated by a world reset reads as dead while the
    /// entity is still there, and every write path resolves by adopting, so the write WOULD have
    /// landed. Guarding cleanup on the narrow answer is what leaves a panel revealed with nothing
    /// behind it. A false here means there is genuinely nothing in the world to write into.</para>
    /// </summary>
    bool IsEntityResolvable();

    /// <summary>True if <paramref name="entity"/> is the layout entity this renderer drives. Used to
    /// route a click to the right menu when more than one is open.</summary>
    bool OwnsEntity(System.IntPtr entity);

    /// <summary>
    /// One line of this renderer's own live state for <c>css_panorama_diag</c>: the entity it is
    /// writing into and the native table it is writing through, read from the instance that
    /// actually renders rather than a fresh one made for the report.
    /// </summary>
    string DescribeState();
}
