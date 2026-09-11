using System.Collections.Generic;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Extensions;
using PanoramaManager.Internal;
using Microsoft.Extensions.Logging;

namespace PanoramaManager.Rendering;

/// <summary>
/// Drives a <c>custom_hud_layout</c> entity through CounterStrikeSharp's own
/// <c>CCSCustomHudLayout</c> setters.
///
/// <para>Everything is written through the <c>...ForPlayer</c> variants so two admins can have the
/// same menu open on different pages. Those write into <c>m_vecPlayerLayoutStates[slot]</c>, which
/// only exists for a connected player, so every call here is best-effort rather than throwing -
/// see <see cref="Target"/>.</para>
/// </summary>
public sealed class CustomHudLayoutRenderer : IPanelRenderer
{
    private readonly LayoutContract _contract;
    private readonly PanelEntity    _entity;

    public CustomHudLayoutRenderer(string layoutPath, LayoutContract contract, ILogger logger)
    {
        _contract = contract;
        _entity   = new PanelEntity(layoutPath, logger);
    }

    public int RowCapacity => _contract.RowCount;

    public uint? EntityIndexIfSpawned => _entity.IndexIfSpawned;

    /// <summary>What this renderer is actually bound to right now.</summary>
    public string DescribeState() => _entity.Describe();

    /// <summary>
    /// The entity plus the controller for <paramref name="slot"/>, or null if either is missing.
    ///
    /// <para>The setters are keyed on the controller, not the slot: the state a per-player write
    /// lands in is <c>m_vecPlayerLayoutStates[controller index - 1]</c>. So a write for a slot with
    /// nobody in it has no destination, and asking for one is an out-of-range element fetch rather
    /// than a no-op - which is what <see cref="PanelEntity.HasStateFor"/> is guarding.</para>
    ///
    /// <para>This is the one behaviour the old hand-rolled path had that this does not: it computed
    /// the state address from the raw slot and could therefore clear a departing player's state
    /// after their controller was gone. Clearing on the way IN covers it -
    /// <c>OnClientPutInServer</c> resets the slot - which the library already does, precisely
    /// because a disconnect is not a reliable place to do it.</para>
    /// </summary>
    private (CCSCustomHudLayout Layout, CCSPlayerController Player)? Target(int slot)
    {
        if (_entity.Resolve() is not { } layout || !PanelEntity.HasStateFor(layout, slot))
            return null;

        return Utilities.GetPlayerFromSlot(slot) is { IsValid: true } player
            ? (layout, player)
            : null;
    }

    /// <summary>
    /// Every dialog-variable write funnels through here so the global/per-player choice is made in
    /// one place.
    ///
    /// <para><b>There is no automatic fallback to global.</b> There used to be: when the per-player
    /// natives were unavailable this quietly used the global setter, on the reasoning that shared
    /// text beats no text. That is true of a layout only one person ever has open, and badly wrong
    /// of one several can - global variables are a single set of strings for the whole server, so
    /// each viewer's render overwrites the last. The result is not obviously broken, it is subtly
    /// wrong: someone else's name on your card, a header showing the footer's text, a panel going
    /// blank because another viewer closed theirs. Every one of those reads as a different bug.</para>
    ///
    /// <para>A layout that genuinely shows everyone the same thing opts in with
    /// <see cref="LayoutContract.SharedText"/>. Everything else fails the write instead, which the
    /// caller can see and report.</para>
    /// </summary>
    private bool WriteVariable(CCSCustomHudLayout layout, CCSPlayerController? player, string panelId, string name, string value)
    {
        if (TextIsShared)
        {
            layout.SetDialogVariableString(panelId, name, value);

            return true;
        }

        if (player is null)
            return false;

        layout.SetDialogVariableStringForPlayer(player, panelId, name, value);

        return true;
    }

    /// <summary>Is this layout's text written once for everyone rather than per viewer?</summary>
    private bool TextIsShared => _contract.SharedText || Panorama.UseGlobalDialogVariables;

    public void Invalidate() => _entity.Invalidate();

    /// <summary>Is the layout entity still live? Lets a world reset tell an entity that was
    /// deleted from one that the engine preserved.</summary>
    public bool IsEntityAlive() => _entity.IsAlive();

    /// <summary>Is there an entity to write into, adopting one rather than spawning? See
    /// <see cref="IPanelRenderer.IsEntityResolvable"/> for why this is not IsEntityAlive.</summary>
    public bool IsEntityResolvable() => _entity.ResolveWithoutSpawning() != null;

    public bool OwnsEntity(System.IntPtr entity)
        => entity != System.IntPtr.Zero && _entity.Resolve() is { } mine && mine.Handle == entity;

    public bool RenderRows(int slot, IReadOnlyList<MenuItem> rows)
    {
        if (Target(slot) is not ({ } layout, { } player))
            return false;

        // RowCount 0 means the layout has no rowN pool at all - every panel is addressed directly.
        // Without this the renderer still asks for row0 and the game logs "Unable to find panel with
        // id 'row0'" on every draw, which is noise that looks like a real fault.
        if (_contract.RowCount <= 0)
            return true;

        for (var i = 0; i < _contract.RowCount; i++)
        {
            var panelId = _contract.RowPanelId(i);

            if (i >= rows.Count)
            {
                // Collapse, don't just blank - an empty-but-visible row leaves a hole in the list.
                layout.SetHasClassForPlayer(player, panelId, _contract.HiddenClass, true);
                continue;
            }

            var row = rows[i];

            WriteVariable(layout, player, RootFor(panelId), _contract.RowTitleVar(i), row.Title);

            WriteVariable(layout, player, RootFor(panelId), _contract.RowSubtitleVar(i), row.Subtitle ?? string.Empty);

            layout.SetHasClassForPlayer(player, panelId, _contract.HiddenClass, false);
            layout.SetHasClassForPlayer(player, panelId, _contract.DisabledClass, !row.Enabled);
        }

        return true;
    }

    public bool SetVariable(int slot, string name, string value)
    {
        // Shared text lands in the entity's global state, which exists whether or not this slot has
        // a per-player state of its own. Going through Target would refuse the write for the absence
        // of something it never touches - and the caller reads a refused write as a failed draw.
        if (TextIsShared)
        {
            return _entity.Resolve() is { } shared
                && WriteVariable(shared, null, _contract.RootPanelId, name, value);
        }

        if (Target(slot) is not ({ } layout, { } player))
            return false;

        return WriteVariable(layout, player, _contract.RootPanelId, name, value);
    }

    public bool SetClass(int slot, string panelId, string className, bool enabled)
    {
        if (Target(slot) is not ({ } layout, { } player))
            return false;

        layout.SetHasClassForPlayer(player, panelId, className, enabled);

        return true;
    }

    /// <summary>
    /// Required for a player's HUD to take mouse input - without it there is no cursor and Buttons
    /// cannot be clicked.
    ///
    /// <para>Read back rather than assumed. The setter is void, and this is the one piece of
    /// per-player state the engine will also report, so "did the capture land" has a real answer
    /// instead of an inference - and a capture stranded on a slot with no state used to print
    /// capture=off in the diagnostic and look clean.</para>
    /// </summary>
    public bool SetInputCapture(int slot, bool enabled)
    {
        if (Target(slot) is not ({ } layout, { } player))
            return false;

        layout.SetInputCaptureEnabled(player, enabled);

        return layout.IsInputCaptureEnabled(player) == enabled;
    }

    /// <summary>Panorama scopes dialog variables to the panel they're set on and children inherit
    /// them, so row text is written on the root. If a layout turns out not to inherit, point this
    /// at the row panel instead by clearing <see cref="LayoutContract.RootPanelId"/>.</summary>
    private string RootFor(string rowPanelId)
        => string.IsNullOrEmpty(_contract.RootPanelId) ? rowPanelId : _contract.RootPanelId;
}
