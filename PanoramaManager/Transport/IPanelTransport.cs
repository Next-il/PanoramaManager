using System;
using CounterStrikeSharp.API.Core;

namespace PanoramaManager.Transport;

/// <summary>A click as it arrives from the client, before it's resolved against a session.</summary>
/// <param name="Player">Who clicked, or null if the transport couldn't attribute it.</param>
/// <param name="ElementId">Raw element id from the layout, e.g. <c>row3_btn</c>.</param>
/// <param name="Args">Extra tokens. Empty on transports that can't carry them.</param>
/// <param name="Token">Session token, when the transport is spoofable and carries one.</param>
/// <param name="Layout">
/// The <c>CCSCustomHudLayout</c> that was clicked, when the transport knows it. This is what makes
/// several menus open at once unambiguous: every layout has a <c>row0_btn</c>, so the element id
/// alone cannot say which menu it came from. Zero means the transport could not attribute it, and
/// routing falls back to matching on the player's open sessions.
/// </param>
public sealed record RawInteraction(
    CCSPlayerController? Player,
    string               ElementId,
    string[]             Args,
    string?              Token,
    IntPtr               Layout = default);

/// <summary>
/// How a click gets from the client back to the server. Two exist because the answer depends on
/// something outside this library's control:
///
/// <list type="bullet">
/// <item><see cref="ClickHookTransport"/> - the engine's own click message. Works with no scripting
/// in the layout, which is the only thing allowed on the sanctioned path today. Unspoofable, since
/// the controller pointer comes from the engine.</item>
/// <item><see cref="ConsoleCommandTransport"/> - the layout's JS runs a console command. Only usable
/// if scripting is ever permitted, and spoofable, so it carries a session token.</item>
/// </list>
/// </summary>
public interface IPanelTransport
{
    /// <summary>True once the transport is live. A failed signature scan leaves this false and the
    /// menu renders but never reports clicks.</summary>
    bool IsInstalled { get; }

    event Action<RawInteraction>? OnInteraction;

    void Install();

    void Uninstall();
}
