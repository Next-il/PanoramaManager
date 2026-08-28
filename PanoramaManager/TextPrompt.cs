using System;

namespace PanoramaManager;

/// <summary>How a text prompt ended.</summary>
public enum TextPromptOutcome
{
    /// <summary>The player typed something and it was accepted.</summary>
    Submitted,

    /// <summary>The player typed the cancel word.</summary>
    Cancelled,

    /// <summary>Nothing arrived before the timeout.</summary>
    TimedOut,

    /// <summary>The menu closed, the player disconnected, or the round restarted.</summary>
    Abandoned,
}

/// <summary>The result handed to <see cref="TextPrompt.OnResult"/>.</summary>
/// <param name="Outcome">Why the prompt ended. Check this before using <paramref name="Text"/>.</param>
/// <param name="Text">What the player typed, trimmed and truncated. Empty unless submitted.</param>
public readonly record struct TextPromptResult(TextPromptOutcome Outcome, string Text)
{
    public bool Submitted => Outcome == TextPromptOutcome.Submitted;
}

/// <summary>
/// Asks a player for a line of text by borrowing the chat box.
///
/// <para><b>Why chat.</b> A <c>custom_hud_layout</c> may only contain Panel, Label, Image and Button,
/// and may not carry scripts - so a layout physically cannot accept a keystroke. There is no
/// TextEntry to allow. Chat is the only thing a player types that reaches the server, which makes
/// this the only mechanism available rather than a preference.</para>
///
/// <para>The message is swallowed, so it never reaches public chat: a kick reason or a ban duration
/// is not something the server should broadcast on the way in.</para>
///
/// <code>
/// menu.PromptText(player, new TextPrompt
/// {
///     Variable = "input_preview",
///     Hint     = "Type the kick reason in chat, or 'cancel' to abort.",
///     OnResult = r => { if (r.Submitted) Kick(target, r.Text); },
/// });
/// </code>
/// </summary>
public sealed class TextPrompt
{
    /// <summary>
    /// Dialog variable the answer is echoed into, so the player sees what the server received. The
    /// layout needs a <c>text="{s:name}"</c> slot with this name; without one the prompt still works
    /// and the player simply gets no feedback.
    /// </summary>
    public required string Variable { get; init; }

    /// <summary>Shown to the player in chat when the prompt opens. Null sends nothing.</summary>
    public string? Hint { get; init; }

    /// <summary>Runs on the game thread when the prompt ends, for any reason.</summary>
    public Action<TextPromptResult>? OnResult { get; init; }

    /// <summary>
    /// Typing this cancels instead of submitting. Case-insensitive. Set null to disable, though a
    /// player who has opened a prompt by accident then has only the timeout to get out of it.
    /// </summary>
    public string? CancelWord { get; init; } = "cancel";

    /// <summary>
    /// Longest accepted answer; anything past this is cut. Guards the layout as much as the
    /// consumer - a Label handed a few thousand characters is a rendering problem, and the text
    /// arrives from a client that decides its own length.
    /// </summary>
    public int MaxLength { get; init; } = 128;

    /// <summary>
    /// Seconds before giving up. A prompt that never resolves leaves the player's chat swallowed
    /// indefinitely, so this is a backstop rather than a nicety.
    /// </summary>
    public float TimeoutSeconds { get; init; } = 60f;
}
