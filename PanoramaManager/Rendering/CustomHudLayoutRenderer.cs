using System;
using System.Collections.Generic;
using CounterStrikeSharp.API.Core;
using PanoramaManager.Internal;
using Microsoft.Extensions.Logging;

namespace PanoramaManager.Rendering;

/// <summary>
/// Drives a <c>custom_hud_layout</c> entity through the engine's own per-player state setters.
///
/// <para>Everything is written through the <c>...ForPlayer</c> variants so two admins can have the
/// same menu open on different pages. Those write into <c>m_vecPlayerLayoutStates[slot]</c> and
/// silently no-op if that slot's state isn't allocated, which is also why every call here is
/// best-effort rather than throwing.</para>
/// </summary>
public sealed class CustomHudLayoutRenderer : IPanelRenderer
{
    private readonly LayoutContract    _contract;
    private readonly PanelEntity        _entity;
    private readonly CustomHudNatives  _natives;
    private readonly ILogger           _logger;

    public CustomHudLayoutRenderer(string layoutPath, LayoutContract contract, ILogger logger)
    {
        _contract = contract;
        _logger   = logger;
        _entity   = new PanelEntity(layoutPath, logger);
        _natives  = new CustomHudNatives(logger);
    }

    public int RowCapacity => _contract.RowCount;

    public uint? EntityIndexIfSpawned => _entity.IndexIfSpawned;

    /// <summary>
    /// What this renderer is actually bound to right now.
    ///
    /// <para>The diagnostic used to build its own <c>CustomHudNatives</c> and describe that, which
    /// answers a question nobody asked: the object it described had never rendered anything. This
    /// reads the instance the menu draws through. The identity hash is printed because most of the
    /// table is static - if two renderers ever disagree, the hashes are what shows it.</para>
    /// </summary>
    public string DescribeState()
        => $"{_entity.Describe()}  natives#{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_natives):x4} "
         + $"per-player text: {(_natives.CanWritePerPlayerText ? "available" : "UNAVAILABLE")}";

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
    private bool WriteVariable(IntPtr entity, int slot, string panelId, string name, string value)
    {
        if (_contract.SharedText || Panorama.UseGlobalDialogVariables)
            return _natives.SetDialogVariableString(entity, panelId, name, value);

        if (!_natives.CanWritePerPlayerText)
        {
            WarnOncePerPlayerTextMissing();
            return false;
        }

        return _natives.SetDialogVariableStringForPlayer(entity, (uint) slot, panelId, name, value);
    }

    private bool _warnedPerPlayerText;

    /// <summary>Said once, not once per write - a render is dozens of writes and this would bury
    /// the log otherwise.</summary>
    private void WarnOncePerPlayerTextMissing()
    {
        if (_warnedPerPlayerText) return;
        _warnedPerPlayerText = true;

        _logger.LogError(
            "[Panorama] per-player text is unavailable, so this layout cannot render. Run "
            + "css_panorama_diag: the intern/write signatures did not resolve, which usually means "
            + "the gamedata needs updating after a CS2 patch.");
    }

    public void Invalidate() => _entity.Invalidate();

    /// <summary>Is the layout entity still live? Lets a world reset tell an entity that was
    /// deleted from one that the engine preserved.</summary>
    public bool IsEntityAlive() => _entity.IsAlive();

    /// <summary>Is there an entity to write into, adopting one rather than spawning? See
    /// <see cref="IPanelRenderer.IsEntityResolvable"/> for why this is not IsEntityAlive.</summary>
    public bool IsEntityResolvable() => _entity.ResolveWithoutSpawning() != null;

    public bool OwnsEntity(IntPtr entity)
        => entity != IntPtr.Zero && _entity.Resolve() is { } mine && mine.Handle == entity;

    public bool RenderRows(int slot, IReadOnlyList<MenuItem> rows)
    {
        if (_entity.Resolve() is not { } entity)
            return false;

        // RowCount 0 means the layout has no rowN pool at all - every panel is addressed directly.
        // Without this the renderer still asks for row0 and the game logs "Unable to find panel with
        // id 'row0'" on every draw, which is noise that looks like a real fault.
        if (_contract.RowCount <= 0)
            return true;

        var handle = entity.Handle;

        for (var i = 0; i < _contract.RowCount; i++)
        {
            var panelId = _contract.RowPanelId(i);

            if (i >= rows.Count)
            {
                // Collapse, don't just blank - an empty-but-visible row leaves a hole in the list.
                _natives.SetHasClassForPlayer(handle, (uint) slot, panelId, _contract.HiddenClass, true);
                continue;
            }

            var row = rows[i];

            WriteVariable(handle, slot, RootFor(panelId), _contract.RowTitleVar(i), row.Title);

            WriteVariable(handle, slot, RootFor(panelId), _contract.RowSubtitleVar(i), row.Subtitle ?? string.Empty);

            _natives.SetHasClassForPlayer(handle, (uint) slot, panelId, _contract.HiddenClass, false);
            _natives.SetHasClassForPlayer(handle, (uint) slot, panelId, _contract.DisabledClass, !row.Enabled);
        }

        return true;
    }

    public bool SetVariable(int slot, string name, string value)
    {
        if (_entity.Resolve() is not { } entity)
            return false;

        return WriteVariable(entity.Handle, slot, _contract.RootPanelId, name, value);
    }

    public bool SetClass(int slot, string panelId, string className, bool enabled)
    {
        if (_entity.Resolve() is not { } entity)
            return false;

        return _natives.SetHasClassForPlayer(entity.Handle, (uint) slot, panelId, className, enabled);
    }

    /// <summary>
    /// Required for a player's HUD to take mouse input - without it there is no cursor and Buttons
    /// cannot be clicked.
    ///
    /// <para>The signature is byte-identical to the upstream ModSharp plugin's, which reports input
    /// capture as confirmed working, so a failure here is not a stale pattern. In our first test no
    /// cursor appeared, but the click receiver was also hooked on the wrong function at the time, so
    /// that result is not clean. Retest now that the receiver is correct.</para>
    /// </summary>
    public bool SetInputCapture(int slot, bool enabled)
    {
        if (_entity.Resolve() is not { } entity)
            return false;

        return _natives.SetInputCaptureEnabled(entity.Handle, (uint) slot, enabled);
    }

    /// <summary>Panorama scopes dialog variables to the panel they're set on and children inherit
    /// them, so row text is written on the root. If a layout turns out not to inherit, point this
    /// at the row panel instead by clearing <see cref="LayoutContract.RootPanelId"/>.</summary>
    private string RootFor(string rowPanelId)
        => string.IsNullOrEmpty(_contract.RootPanelId) ? rowPanelId : _contract.RootPanelId;
}
