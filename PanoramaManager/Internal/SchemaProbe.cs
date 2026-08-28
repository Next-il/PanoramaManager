using System;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using Microsoft.Extensions.Logging;

namespace PanoramaManager.Internal;

/// <summary>
/// Asks the engine's schema system where <c>CCSCustomHudLayout</c>'s fields live, instead of
/// hardcoding byte offsets.
///
/// <para><b>Why this matters.</b> Everything the client renders is networked state, and networked
/// state is schema. A schema offset is resolved by <i>name</i> at runtime, so it survives a CS2
/// update that shifts every byte in the binary - which is exactly what just invalidated three of our
/// signatures. Signatures are only genuinely needed for behaviour that is code rather than state:
/// the string interning, and the inbound click message.</para>
///
/// <para>This probe reports what resolves so the hardcoded offsets in <c>gamedata/panoramamanager.json</c>
/// can be retired for the ones that do.</para>
/// </summary>
internal static class SchemaProbe
{
    private const string ClassName = "CCSCustomHudLayout";

    /// <summary>Field names worth asking about. Names are guesses drawn from the observed layout -
    /// the log tells us which exist, and a zero simply means "not that name".</summary>
    private static readonly string[] Candidates =
    [
        "m_vecPlayerLayoutStates",
        "m_globalLayoutState",
        "m_strLayout",
        "m_bInputCaptureEnabled",
        "m_vecPanelIds",
        "m_vecDialogVarNames",
    ];

    internal static void Report(ILogger logger)
    {
        foreach (var field in Candidates)
        {
            try
            {
                var offset = Schema.GetSchemaOffset(ClassName, field);

                if (offset > 0)
                    logger.LogInformation("[Panorama] schema {Class}::{Field} = +0x{Offset:X}", ClassName, field, offset);
            }
            catch (Exception e)
            {
                logger.LogDebug("[Panorama] schema {Class}::{Field} unavailable: {Message}", ClassName, field, e.Message);
            }
        }
    }
}
