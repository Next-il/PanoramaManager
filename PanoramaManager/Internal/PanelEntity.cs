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

    /// <summary>Forgets the cached index so the next resolve re-spawns instead of binding a
    /// recycled slot. Call this only when the entity is genuinely gone - see <see cref="IsAlive"/>,
    /// because forgetting a live entity orphans it rather than replacing it.</summary>
    internal void Invalidate() => _index = null;

    /// <summary>The spawned entity's index, or null when nothing is spawned. Read without spawning:
    /// the transmit check runs every tick for every player and must not create anything.</summary>
    internal uint? IndexIfSpawned => _index;

    /// <summary>
    /// Is the entity we spawned still there? Answers without spawning, so a caller can tell
    /// "the world reset took it" from "the world reset left it alone".
    ///
    /// <para>This matters because Valve added <c>custom_hud_layout</c> to the engine's preserved
    /// classname list, so it is NOT bulk-deleted on a round restart the way an ordinary non-player
    /// entity is. Treating every round start as a death meant abandoning a live entity, leaking it,
    /// and spawning a duplicate every round.</para>
    /// </summary>
    internal bool IsAlive() => ResolveCached() != null;

    /// <summary>
    /// The cached entity if it is still ours, else null. Never spawns.
    ///
    /// <para>The designer-name check is not redundant with IsValid: entity indices are recycled, so
    /// a dead slot can come back valid while holding something else entirely - at which point we
    /// would be writing dialog variables into a stranger's entity.</para>
    /// </summary>
    private CBaseEntity? ResolveCached()
    {
        if (_index is not { } index) return null;

        var existing = Utilities.GetEntityFromIndex<CBaseEntity>((int) index);
        if (existing is { IsValid: true } && existing.DesignerName == ClassName)
            return existing;

        _index = null;
        return null;
    }

    /// <summary>
    /// One line of live entity state for <c>css_panorama_diag</c>. Never spawns, so asking does not
    /// change the answer.
    ///
    /// <para>"unresolved" and "stale" both render as a dead panel and are told apart nowhere else:
    /// the first means nothing was ever drawn, the second that the entity we drew into is gone and
    /// every write since has been silently dropped.</para>
    /// </summary>
    internal string Describe()
    {
        // Read the index BEFORE IsAlive, which clears it when the cached entity turns out to be
        // gone - otherwise the interesting case prints no index at all.
        if (_index is not { } cached)
            return "entity unresolved" + DescribeDuplicates();

        return (IsAlive() ? $"entity live idx {cached}" : $"entity STALE (idx {cached} gone)")
             + DescribeDuplicates();
    }

    /// <summary>
    /// Says so when the world holds more than one entity for this layout.
    ///
    /// <para>The handle reports the one index it happens to hold, which reads as healthy while the
    /// client draws a different entity nobody writes into any more - "the library says closed, the
    /// panel is still up" with no other symptom. A plugin reload is the way in: custom_hud_layout is
    /// preserved, Dispose never kills it, and the new load context's registry cannot recognise the
    /// old one, so Adopt misses it and Create spawns a second. Diagnostic only, and only asked by
    /// css_panorama_diag - it walks the entity list.</para>
    /// </summary>
    private string DescribeDuplicates()
    {
        var live = Utilities.FindAllEntitiesByDesignerName<CBaseEntity>(ClassName)
                            .Where(e => e.IsValid)
                            .ToList();

        var owned = live.Count(e => PanelRegistry.IsOwnedLayout(e.Index, _layoutPath));

        // Both numbers, because the owned count alone cannot see the duplicate this method was
        // written for. PanelRegistry is per load context and only Create writes to it, so the
        // orphan a reload leaves behind is in nobody's registry: owned reads 1 and the line stays
        // silent on exactly the failure it exists to catch. The world total is the honest signal -
        // compare it against the number of distinct layouts the server actually uses.
        return owned > 1
            ? $"  DUPLICATE: {owned} entities for this layout ({live.Count} {ClassName} in world)"
            : $"  ({live.Count} {ClassName} in world, {owned} owned here)";
    }

    /// <summary>Resolves the live entity, spawning it on first use or after a world reset.
    /// Returns null if the entity could not be created.</summary>
    internal CBaseEntity? Resolve()
    {
        return ResolveWithoutSpawning() ?? Create();
    }

    /// <summary>
    /// The live entity for this layout if there is one, without creating anything.
    ///
    /// <para>Stronger than <see cref="IsAlive"/>, weaker than <see cref="Resolve"/>, and the gap
    /// between those two is where panels get stuck. IsAlive only reads our cached index, so it
    /// answers "no" for an entity that is alive in the world but whose index we forgot - a world
    /// reset invalidates the index and the engine preserves the entity - and a caller that reads
    /// that "no" as "nothing to write into" skips work that the very next Resolve then makes
    /// visible again, because Resolve adopts. Resolve is not the answer either: a caller that only
    /// wants to UNDO something must not build a layout entity for the sole purpose of telling it to
    /// hide. Adoption is the middle ground - one entity walk, and it finds anything this process
    /// could have written into.</para>
    /// </summary>
    internal CBaseEntity? ResolveWithoutSpawning() => ResolveCached() ?? Adopt();

    /// <summary>
    /// Takes over an entity that is already in the world for this layout, or null if there is none.
    ///
    /// <para>Adopting rather than stacking duplicates. DesignerName is the only thing we can match
    /// on; the layout keyvalue interns into m_strLayout and isn't reachable without a schema class
    /// for CCSCustomHudLayout. Filter on the layout, don't check it afterwards. Taking the first
    /// entity of any kind and then asking whether it happens to be ours means that with several
    /// menus live - each one its own entity, on its own layout - we look at exactly one candidate
    /// and spawn a duplicate whenever it isn't the right one.</para>
    /// </summary>
    private CBaseEntity? Adopt()
    {
        var adopted = Utilities.FindAllEntitiesByDesignerName<CBaseEntity>(ClassName)
            .FirstOrDefault(e => e.IsValid && PanelRegistry.IsOwnedLayout(e.Index, _layoutPath));

        if (adopted is not { IsValid: true })
            return null;

        _index = adopted.Index;
        return adopted;
    }

    private CBaseEntity? Create()
    {
        // Raw factory rather than Utilities.CreateEntityByName: an unknown classname comes back as
        // a null pointer here, where the wrapper would hand back a CBaseEntity over address 0 whose
        // IsValid dereferences 0x10.
        var pointer = VirtualFunctions.UTIL_CreateEntityByName(ClassName, -1);
        if (pointer == IntPtr.Zero)
        {
            _logger.LogWarning("[Panorama] UTIL_CreateEntityByName({ClassName}) returned null.", ClassName);
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
            _logger.LogWarning("[Panorama] DispatchSpawn({ClassName}) left an invalid entity.", ClassName);
            return null;
        }

        _index = entity.Index;
        PanelRegistry.RegisterLayout(entity.Index, _layoutPath);

        _logger.LogDebug(
            "[Panorama] Spawned {ClassName} index={Index} layout='{Layout}'", ClassName, entity.Index, _layoutPath);

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
