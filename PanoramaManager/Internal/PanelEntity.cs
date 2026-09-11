using System;
using System.Collections.Generic;
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
    /// Is <paramref name="layout"/> the entity for OUR layout path?
    ///
    /// <para><c>m_strLayout</c> is the entity's own record of the layout keyvalue it was spawned
    /// with, reachable now that CounterStrikeSharp ships a schema class for
    /// <c>CCSCustomHudLayout</c>. That makes this true for any matching entity in the world -
    /// including one orphaned by a plugin reload, which the process-wide index map this replaced
    /// could never recognise, because only the load context that spawned an entity had it on
    /// record.</para>
    /// </summary>
    private bool IsOurs(CCSCustomHudLayout layout)
    {
        try
        {
            return layout.StrLayout == _layoutPath;
        }
        catch
        {
            return false; // Schema unavailable - treat it as somebody else's.
        }
    }

    /// <summary>
    /// The cached entity if it is still ours, else null. Never spawns.
    ///
    /// <para>The designer-name check is not redundant with IsValid: entity indices are recycled, so
    /// a dead slot can come back valid while holding something else entirely - at which point we
    /// would be writing dialog variables into a stranger's entity.</para>
    /// </summary>
    private CCSCustomHudLayout? ResolveCached()
    {
        if (_index is not { } index) return null;

        var existing = Utilities.GetEntityFromIndex<CCSCustomHudLayout>((int) index);
        if (existing is { IsValid: true } && existing.DesignerName == ClassName && IsOurs(existing))
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
    /// preserved and Dispose never kills it, so the previous load leaves its entity standing.
    /// Counted off m_strLayout, so a reload orphan is included - the index map this replaced could
    /// not see one. Diagnostic only, and only asked by css_panorama_diag - it walks the entity list.</para>
    /// </summary>
    private string DescribeDuplicates()
    {
        var live = All().ToList();
        var ours = live.Count(IsOurs);

        return ours > 1
            ? $"  DUPLICATE: {ours} entities for this layout ({live.Count} {ClassName} in world)"
            : $"  ({live.Count} {ClassName} in world, {ours} for this layout)";
    }

    /// <summary>Resolves the live entity, spawning it on first use or after a world reset.
    /// Returns null if the entity could not be created.</summary>
    internal CCSCustomHudLayout? Resolve() => ResolveWithoutSpawning() ?? Create();

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
    internal CCSCustomHudLayout? ResolveWithoutSpawning() => ResolveCached() ?? Adopt();

    /// <summary>
    /// Takes over an entity that is already in the world for this layout, or null if there is none.
    ///
    /// <para>Filter on the layout, do not check it afterwards. Taking the first entity of any kind
    /// and then asking whether it happens to be ours means that with several menus live - each one
    /// its own entity, on its own layout - we look at exactly one candidate and spawn a duplicate
    /// whenever it is not the right one.</para>
    /// </summary>
    private CCSCustomHudLayout? Adopt()
    {
        if (All().FirstOrDefault(IsOurs) is not { } adopted)
            return null;

        _index = adopted.Index;
        return adopted;
    }

    private static IEnumerable<CCSCustomHudLayout> All()
        => Utilities.FindAllEntitiesByDesignerName<CCSCustomHudLayout>(ClassName).Where(e => e.IsValid);

    private CCSCustomHudLayout? Create()
    {
        // Raw factory rather than Utilities.CreateEntityByName: an unknown classname comes back as
        // a null pointer here, where the wrapper would hand back an entity over address 0 whose
        // IsValid dereferences 0x10.
        var pointer = VirtualFunctions.UTIL_CreateEntityByName(ClassName, -1);
        if (pointer == IntPtr.Zero)
        {
            _logger.LogWarning("[Panorama] UTIL_CreateEntityByName({ClassName}) returned null.", ClassName);
            return null;
        }

        var entity = new CCSCustomHudLayout(pointer);

        // The layout MUST be set as a spawn keyvalue, not written to m_strLayout afterwards.
        // The field write networks fine and reads back correctly, so it looks like it worked, but
        // the client never loads the layout and you get "[custom_hud] Failed to load layout" with
        // no violation named - which then reads like an XML problem and sends you rewriting a
        // layout that was never at fault. Do not "simplify" this into a StrLayout write.
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

        _logger.LogDebug(
            "[Panorama] Spawned {ClassName} index={Index} layout='{Layout}'", ClassName, entity.Index, _layoutPath);

        return entity;
    }

    /// <summary>
    /// Does <c>m_vecPlayerLayoutStates</c> have a state for this slot?
    ///
    /// <para>A guard, not a diagnostic. Every per-player setter resolves the slot's state by
    /// indexing that vector, and a slot past the end is an out-of-range element fetch.</para>
    ///
    /// <para>It is also the honest answer to "did that write land". The setters are void and a slot
    /// with no state silently receives nothing, so reporting "we called it" as "it worked" is what
    /// left a panel capturing input at opacity 0 with no way to close it.</para>
    /// </summary>
    internal static bool HasStateFor(CCSCustomHudLayout layout, int slot)
    {
        if (slot < 0)
            return false;

        try
        {
            return slot < layout.PlayerLayoutStates.Count;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Kills every <c>custom_hud_layout</c> in the world. Collects indices first -
    /// FindAllEntitiesByDesignerName is lazy and killing mid-enumeration invalidates its cursor.</summary>
    internal static int DespawnAll()
    {
        var indices = All().Select(e => e.Index).ToList();

        var removed = 0;
        foreach (var index in indices)
        {
            if (Utilities.GetEntityFromIndex<CBaseEntity>((int) index) is not { IsValid: true } entity)
                continue;

            entity.AcceptInput("Kill");
            removed++;
        }

        return removed;
    }
}
