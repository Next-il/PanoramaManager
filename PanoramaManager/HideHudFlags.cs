using System;

namespace PanoramaManager;

/// <summary>
/// Bits of <c>CBasePlayerPawn::m_iHideHUD</c>, which suppresses parts of the base HUD server-side.
///
/// <para>This is the only way to hide HUD elements from a plugin. The obvious alternative -
/// <c>ExecuteClientCommand("crosshair 0")</c> - does nothing: a server may only execute convars
/// flagged <c>FCVAR_SERVER_CAN_EXECUTE</c> on a client, and the HUD convars are client archive
/// convars, so the command is accepted and discarded. <c>m_iHideHUD</c> is networked state the
/// server genuinely owns.</para>
/// </summary>
[Flags]
public enum HideHudFlags : uint
{
    None             = 0,

    /// <summary>Ammo count and weapon selection.</summary>
    WeaponSelection  = 1u << 0,

    Flashlight       = 1u << 1,

    /// <summary>Everything except money.</summary>
    All              = 1u << 2,

    /// <summary>Health and armour.</summary>
    Health           = 1u << 3,

    /// <summary>Applied while the player is dead.</summary>
    PlayerDead       = 1u << 4,

    /// <summary>Applied while the player has no HEV suit.</summary>
    NeedSuit         = 1u << 5,

    /// <summary>Pickup history, death notices and similar.</summary>
    MiscStatus       = 1u << 6,

    /// <summary>Chat, voice icon and the rest of the communication elements.</summary>
    Chat             = 1u << 7,

    Crosshair        = 1u << 8,
    VehicleCrosshair = 1u << 9,
    InVehicle        = 1u << 10,
    BonusProgress    = 1u << 11,

    /// <summary>Radar. CS-specific.</summary>
    Radar            = 1u << 12,
}
