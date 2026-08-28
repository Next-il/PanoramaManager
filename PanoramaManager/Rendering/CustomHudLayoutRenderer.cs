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

    /// <summary>Every dialog-variable write funnels through here so the global/per-player choice is
    /// made in one place. See <see cref="Panorama.UseGlobalDialogVariables"/> for why the global
    /// path is the default.</summary>
    private bool WriteVariable(IntPtr entity, int slot, string panelId, string name, string value)
        // Fall back to the global setter whenever the per-player path is unavailable, not just when
        // it is switched off. Without the second test, a server whose intern signatures did not
        // resolve renders a menu with no text at all - every write silently returning false is worse
        // than shared text, and much harder to recognise.
        => Panorama.UseGlobalDialogVariables || !_natives.CanWritePerPlayerText
            ? _natives.SetDialogVariableString(entity, panelId, name, value)
            : _natives.SetDialogVariableStringForPlayer(entity, (uint) slot, panelId, name, value);

    public void Invalidate() => _entity.Invalidate();

    public bool OwnsEntity(IntPtr entity)
        => entity != IntPtr.Zero && _entity.Resolve() is { } mine && mine.Handle == entity;

    public bool RenderRows(int slot, IReadOnlyList<MenuItem> rows)
    {
        if (_entity.Resolve() is not { } entity)
            return false;

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
