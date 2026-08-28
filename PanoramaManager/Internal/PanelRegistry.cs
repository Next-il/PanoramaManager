using System.Collections.Generic;

namespace PanoramaManager.Internal;

/// <summary>
/// Process-wide bookkeeping. The library is referenced as a plain DLL rather than loaded as a
/// plugin, so each consumer gets its own copy of these statics per assembly-load context. That is
/// fine for everything here: the entity-index map is only an optimisation to avoid stacking
/// duplicate entities within one consumer, and correctness never depends on two consumers sharing it.
/// </summary>
internal static class PanelRegistry
{
    private static readonly Dictionary<uint, string> Layouts = new();

    internal static void RegisterLayout(uint entityIndex, string layoutPath)
        => Layouts[entityIndex] = layoutPath;

    internal static bool IsOwnedLayout(uint entityIndex, string layoutPath)
        => Layouts.TryGetValue(entityIndex, out var path) && path == layoutPath;

    internal static void ClearLayouts() => Layouts.Clear();
}
