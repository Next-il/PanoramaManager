using System;
using System.Linq;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace PanoramaManager.Internal;

/// <summary>
/// Owns the <c>custom_hud_layout</c> entity for one layout path. Entities are shared per layout
/// across every consumer of the library - two plugins opening the same layout drive one entity,
/// because the per-player state slots are what separate their viewers, not the entity.
/// </summary>
internal sealed class PanelEntity
{
    internal const string ClassName = "custom_hud_layout";

    private readonly string  _layoutPath;
    private readonly ILogger _logger;

    private uint? _index;

    internal PanelEntity(string layoutPath, ILogger logger)
    {
        _layoutPath = layoutPath;
        _logger     = logger;
    }

    /// <summary>Non-player entities are bulk-deleted on round restart and map change. Drop the
    /// cached index so the next resolve re-spawns instead of binding a recycled slot.</summary>
    internal void Invalidate() => _index = null;

    /// <summary>Resolves the live entity, spawning it on first use or after a world reset.
    /// Returns null if the entity could not be created.</summary>
    internal CBaseEntity? Resolve()
    {
        if (_index is { } index)
        {
            var existing = Utilities.GetEntityFromIndex<CBaseEntity>((int) index);
            if (existing is { IsValid: true })
                return existing;

            _index = null;
        }

        return Spawn();
    }

    private CBaseEntity? Spawn()
    {
        // Adopt an entity another consumer already spawned for this layout rather than stacking
        // duplicates. DesignerName is the only thing we can match on; the layout keyvalue interns
        // into m_strLayout and isn't reachable without a schema class for CCSCustomHudLayout.
        // Filter on the layout, don't check it afterwards. Taking the first entity of any kind and
        // then asking whether it happens to be ours means that with several menus live - each one
        // its own entity, on its own layout - we look at exactly one candidate and spawn a duplicate
        // whenever it isn't the right one.
        var adopted = Utilities.FindAllEntitiesByDesignerName<CBaseEntity>(ClassName)
            .FirstOrDefault(e => e.IsValid && PanelRegistry.IsOwnedLayout(e.Index, _layoutPath));

        if (adopted is { IsValid: true })
        {
            _index = adopted.Index;
            return adopted;
        }

        // Raw factory rather than Utilities.CreateEntityByName: an unknown classname comes back as
        // a null pointer here, where the wrapper would hand back a CBaseEntity over address 0 whose
        // IsValid dereferences 0x10.
        var pointer = VirtualFunctions.UTIL_CreateEntityByName(ClassName, -1);
        if (pointer == IntPtr.Zero)
        {
            _logger.LogWarning("[HudMenu] UTIL_CreateEntityByName({ClassName}) returned null.", ClassName);
            return null;
        }

        var entity = new CBaseEntity(pointer);

        // The layout MUST be set as a spawn keyvalue, not written to m_strLayout afterwards.
        // The field write networks fine and reads back correctly, so it looks like it worked, but
        // the client never loads the layout and you get "[custom_hud] Failed to load layout" with
        // no violation named - which then reads like an XML problem and sends you rewriting a
        // layout that was never at fault. Do not "simplify" this into a schema write.
        using (var kv = new CEntityKeyValues())
        {
            kv.SetVector("origin", 0f, 0f, 0f); // HUD manager entity, position is irrelevant.
            kv.SetString("layout", _layoutPath);

            entity.DispatchSpawn(kv);
        }

        if (!entity.IsValid)
        {
            _logger.LogWarning("[HudMenu] DispatchSpawn({ClassName}) left an invalid entity.", ClassName);
            return null;
        }

        _index = entity.Index;
        PanelRegistry.RegisterLayout(entity.Index, _layoutPath);

        _logger.LogInformation(
            "[HudMenu] Spawned {ClassName} index={Index} layout='{Layout}'", ClassName, entity.Index, _layoutPath);

        return entity;
    }

    /// <summary>Kills every <c>custom_hud_layout</c> in the world. Collects indices first -
    /// FindAllEntitiesByDesignerName is lazy and killing mid-enumeration invalidates its cursor.</summary>
    internal static int DespawnAll()
    {
        var indices = Utilities.FindAllEntitiesByDesignerName<CBaseEntity>(ClassName)
            .Where(e => e.IsValid)
            .Select(e => e.Index)
            .ToList();

        var removed = 0;
        foreach (var index in indices)
        {
            if (Utilities.GetEntityFromIndex<CBaseEntity>((int) index) is not { IsValid: true } entity)
                continue;

            entity.AcceptInput("Kill");
            removed++;
        }

        PanelRegistry.ClearLayouts();

        return removed;
    }
}
