using System;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace PanoramaManager.Internal;

/// <summary>
/// Answers one question: is this player looking at the world through somebody ELSE's eyes?
///
/// <para><b>Why the library cared.</b> Until CS2 build 2000908 (2026-09-09) the client resolved a
/// <c>custom_hud_layout</c>'s per-player panel state through the player being OBSERVED rather than
/// the local client. A dead viewer in-eye of a team-mate had their panel drawn from the TARGET's
/// slot, which has nothing on it, so nothing appeared - while input capture, read from their own
/// slot, still applied: a cursor over an empty screen with a server-side render that looked
/// perfect. Confirmed in game at the time, a dead player spectating someone else saw THAT player's
/// menu and toast, which is why <see cref="LayoutContract.HideFromSpectators"/> exists.</para>
///
/// <para><b>What changed.</b> Build 2000908 added the <c>observable</c> keyvalue - "controls whether
/// an observer sees the UI as the spectated player sees it. Defaults to false" - and
/// <c>PanelEntity</c> spawns with it false, so an observer is drawn their own state. This is now a
/// diagnostic (<c>css_panorama_diag</c> prints WATCHING slot N) and the input to the opt-in
/// <see cref="LayoutContract.RefuseWhileSpectating"/>, not something the library acts on by
/// itself.</para>
///
/// <para>Not a transmit problem. Forcing the entity into the viewer's transmit list was tried and
/// changed nothing; the entity is on their client, its state is complete, and the client reads the
/// wrong slot out of it.</para>
/// </summary>
internal static class ObserverTargets
{
    /// <summary>
    /// The slot this player is watching another player through, or null when they are watching
    /// nobody - alive, in free look, or on a target that is not a player.
    ///
    /// <para>Cheap but not free (it walks the player list to turn a pawn back into a slot), so this
    /// is called on open and by the diagnostic, never per tick. Every read is best-effort: a schema
    /// miss must not stop a panel from opening, so any throw answers "watching nobody".</para>
    /// </summary>
    internal static int? ObservedSlot(CCSPlayerController? viewer)
    {
        if (viewer is not { IsValid: true })
            return null;

        try
        {
            // A live player always observes themselves, so this is the whole answer for most
            // callers and it costs one networked bool. It is also the stable half: the observer
            // target changes every time a dead player clicks to cycle spectate targets.
            if (viewer.PawnIsAlive)
                return null;

            CBasePlayerPawn? pawn = viewer.ObserverPawn.Value;

            if (pawn is not { IsValid: true })
                pawn = viewer.PlayerPawn.Value;

            if (pawn?.ObserverServices is not { } services)
                return null;

            // Only the two modes that render the world from another player. ROAMING is free look
            // and has no target; NONE and FIXED are the pre-deathcam and fixed-camera states, and
            // a fixed camera is not a player pawn.
            var mode = (ObserverMode_t) services.ObserverMode;

            if (mode != ObserverMode_t.OBS_MODE_IN_EYE && mode != ObserverMode_t.OBS_MODE_CHASE)
                return null;

            if (services.ObserverTarget.Value is not { IsValid: true } target)
                return null;

            // Matched against the live player list rather than read straight off the target: an
            // observer target is not guaranteed to be a player pawn, and reading m_hController out
            // of something that is not one is a schema read at an offset that does not belong to it.
            foreach (var other in Utilities.GetPlayers())
            {
                if (other.Slot == viewer.Slot)
                    continue;

                if (other.PlayerPawn.Value is { IsValid: true } theirs && theirs.Index == target.Index)
                    return other.Slot;
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
