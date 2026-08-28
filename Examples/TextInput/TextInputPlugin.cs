using System;
using System.Collections.Generic;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using Microsoft.Extensions.Logging;

namespace PanoramaManager.Example.TextInput;

/// <summary>
/// Worked example: getting a line of text out of a player.
///
/// <para><b>Why this needs an example at all.</b> A <c>custom_hud_layout</c> may only contain Panel,
/// Label, Image and Button, and carries no scripts - so a layout physically cannot accept a
/// keystroke. There is no TextEntry to enable. Chat is the only text a player produces that reaches
/// the server, so <see cref="PanelHandle.PromptText"/> borrows it: the message is swallowed rather
/// than broadcast, and echoed back into the layout so the player can see what the server received.
/// </para>
///
/// <para>Run <c>!textinput</c>, click "Set message", type in chat.</para>
/// </summary>
public sealed class TextInputPlugin : BasePlugin
{
    public override string ModuleName        => "HudMenu Example (Text Input)";
    public override string ModuleVersion     => "0.1.0";
    public override string ModuleAuthor      => "HudMenu";
    public override string ModuleDescription => "Shows how PromptText gets typed text out of a player.";

    /// <summary>
    /// Its own layout, in the repo's <c>workshop/</c> folder. The readout it adds is the point of the
    /// example: the text arrives through chat because a layout cannot take a keystroke, and showing
    /// it back prominently is what makes that indirection read as an input box.
    /// </summary>
    private const string Layout = "panorama/layout/custom_game/text_input.vxml_c";

    private static readonly LayoutContract Contract = new()
    {
        RevealClass = "show",
        RowCount    = 4,   // matches the row pool the layout declares
    };

    private PanelHandle? _menu;

    /// <summary>Last answer per player. The library does not keep it - it hands you the text once and
    /// forgets, because it cannot know what the answer means.</summary>
    private readonly Dictionary<ulong, string> _messages = new();

    public override void Load(bool hotReload)
    {
        Panorama.Init(this);

        _menu = Panorama.Spawn(Layout, Contract);
        _menu.Title = "Text Input";
        _menu.OnEvent += OnMenuEvent;
    }

    public override void Unload(bool hotReload)
    {
        _menu?.Dispose();
        Panorama.Shutdown();
    }

    [ConsoleCommand("css_textinput", "Demonstrate typed input through chat.")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnTextInputCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player is { IsValid: true })
            Show(player);
    }

    private void Show(CCSPlayerController player)
    {
        if (_menu is null)
            return;

        var current = _messages.GetValueOrDefault(player.SteamID, "");

        _menu.Subtitle = "PromptText demo";

        _menu.SetItems(
        [
            new MenuItem("set", "Set message", "Type it in chat", e => Ask(e.Player)),
            new MenuItem("clear", "Clear message", current, e => Clear(e.Player), Enabled: current.Length > 0),
        ]);

        _menu.Open(player);
        _menu.SetVariableFor(player, "input_value", current);
        _menu.SetVariableFor(player, "menu_footer", "Pick an option");
    }

    private void Ask(CCSPlayerController player)
    {
        if (_menu is null)
            return;

        _menu.SetVariableFor(player, "menu_footer", "Waiting for chat...");

        _menu.PromptText(player, new TextPrompt
        {
            // Echoed here by the library the moment the answer arrives, so the player sees what the
            // server actually received - including the trimming and truncation it applied.
            Variable = "input_value",

            // \u0004 not \x04: C#'s \x escape is variable-length and would swallow the following
            // letters as hex digits - "\x04cancel" reads 04ca as one code point and prints a Cyrillic
            // letter where "cancel" should be. These are CS2 chat colour codes.
            Hint = " \u0004[TextInput]\u0001 Type your message in chat, or \u0004cancel\u0001 to abort.",

            // Deliberately short so the timeout path is easy to see. The default is 60.
            TimeoutSeconds = 20f,
            MaxLength      = 64,

            OnResult = result => OnAnswer(player, result),
        });
    }

    /// <summary>
    /// Runs for every outcome, not just success. The prompt ends when the player answers, cancels,
    /// runs out of time, or the menu closes underneath them - and a handler that only considers the
    /// happy path leaves the menu sitting on "Waiting for chat..." forever in the other three.
    /// </summary>
    /// <summary>
    /// Redraws after a round restart. The library rebuilds the menu and restores what it knows -
    /// rows and title - but the readout is a per-viewer write it never saw the meaning of.
    /// </summary>
    private void OnMenuEvent(PanelEvent e)
    {
        if (e.Action != PanelAction.Restored || _menu is null)
            return;

        _menu.SetVariableFor(e.Player, "input_value", _messages.GetValueOrDefault(e.Player.SteamID, ""));
        _menu.SetVariableFor(e.Player, "menu_footer", "Pick an option");
    }

    private void OnAnswer(CCSPlayerController player, TextPromptResult result)
    {
        if (_menu is null || player is not { IsValid: true })
            return;

        if (result.Submitted)
        {
            _messages[player.SteamID] = result.Text;

            Logger.LogInformation("[TextInput] {Player} set: {Text}", player.PlayerName, result.Text);
        }

        var footer = result.Outcome switch
        {
            TextPromptOutcome.Submitted => $"Saved: {result.Text}",
            TextPromptOutcome.Cancelled => "Cancelled",
            TextPromptOutcome.TimedOut  => "Timed out - nothing saved",
            _                           => "Abandoned",
        };

        // Redraw so the rows reflect the new value, then say what happened. Order matters: Show
        // rewrites the footer, so setting it first would be overwritten immediately.
        if (_menu.IsOpenFor(player))
        {
            Show(player);
            _menu.SetVariableFor(player, "menu_footer", footer);
        }
    }

    private void Clear(CCSPlayerController player)
    {
        _messages.Remove(player.SteamID);
        Show(player);
        _menu?.SetVariableFor(player, "menu_footer", "Cleared");
    }
}
