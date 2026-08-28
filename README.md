# PanoramaManager

Server-driven Panorama UI for CounterStrikeSharp, built on the `custom_hud_layout` entity.

You write the Panorama layout; this drives it from C#. Any layout - a menu, a notification, a
progress bar, a scoreboard, a weapon grid. Write text into it, toggle classes on it, per player, and
get clicks back.

## The bridge

A Panorama layout and a CounterStrikeSharp plugin have no idea the other exists. The layout is XML
and CSS sitting on the client; the plugin is C# on the server. Between them is an entity whose
setters have to be found by scanning `libserver.so`, whose strings have to be marshalled into
`CUtlString`, whose per-player state lives at `m_vecPlayerLayoutStates[slot]` computed from raw
offsets, and whose clicks arrive as a user message you have to detour.

This is that bridge, behind typed C#:

```csharp
menu.SetVariableFor(player, "row0_title", p.PlayerName);   // fills {s:row0_title}
menu.SetClassFor(player, "row0", "selected", true);        // toggles .selected
menu.OnEvent += e => Kick(e.Item.Tag);                     // a click, resolved to your object
```

No signatures, no marshalling, no offsets. `LayoutContract` is where you state what your panels are
called, and everything after that is ids and strings.

## What you can build

Anything that is a panel the server drives. The library does not know what a menu is - that is one
layer on top of it, and two of the three things below use none of it.


|                       |                                                                                                                                       |
| --------------------- | ------------------------------------------------------------------------------------------------------------------------------------- |
| **Menus**             | Player lists, admin panels, vote prompts, shops. Row pool with pagination, per-row callbacks, one authorisation gate                  |
| **Notifications**     | Toasts, kill feeds, round announcements. Stacked, coloured, animated, auto-dismissing                                                 |
| **Live readouts**     | Timers, bomb clocks, scoreboards, HP bars, objective progress. Push a string every tick; that is what the entity is genuinely good at |
| **Pickers and grids** | Weapon selection, class select, inventories, map votes - anything a list does not fit                                                 |
| **Prompts**           | Confirm dialogs, and text input through chat, since a layout cannot take a keystroke                                                  |


The retakes weapon grid and the toast service are both built on this and neither touches the menu
layer - they drive their panels directly through the same two calls.

## Writing the layouts

The C# side is the easy half. Panorama looks enough like web CSS that you will write `display: flex`,
`rgba()` or `background-size: contain` on reflex, and Panorama **drops what it does not recognise
without a word** - no error, no warning, the rule simply does nothing.

[PanoramaHUD-Skills](https://github.com/Next-il/PanoramaHUD-Skills) is the answer to that: agent
skills that teach Claude Code, Cursor or Copilot the real vocabulary, read out of `libpanorama.so`,
plus a scaffold that generates a layout and the C# to drive it from one row count, a validator that
fails a build on an unregistered property, and a browser preview so you are not compiling a VPK to
check a padding value.

With those in place, "add a panel with three buttons and a progress bar" is a sentence you can hand
to an agent.

## Features

Any layout:

- **Write text and toggle classes, per player.** `SetVariableFor` and `SetClassFor` are the whole
surface. Everything else is built on those two.
- **Clicks**, routed to the layout that was clicked - so several panels can be live at once without
stealing each other's input.
- `SetVariant` for anything the server cannot send directly. Colours, placements and progress
widths are class swaps underneath, and this manages the group so only one is applied.
- `HideHudFlags` to hide the crosshair, radar or anything else while a panel is open, restored on
close - including closes you did not trigger.
- `CaptureInput = false` for anything the player only reads, so a notification cannot pull up a
cursor and stop them aiming.
- **Cleanup that is easy to get wrong**: round restarts, disconnects, entity recycling, and panels
left on screen when their entity is destroyed.
- **Signatures in gamedata**, so a CS2 update that shifts them is a text edit on the server rather
than a rebuild.

If it is a menu, also:

- **Row pool with pagination.** Declare a fixed number of rows in the layout, hand it any number of
items, it pages them.
- **Clicks resolved to your object.** A click comes back as the `MenuItem` you created, not a panel
id to decode.
- **Per-item callbacks**, plus one `OnEvent` that runs first and can veto them - so authorisation
lives in one place instead of on every row.
- **Tabs**, and per-viewer page state, so two admins can sit on different pages of the same menu.
- `TextPrompt`, which borrows the chat box to ask for a line of text, because a Panorama layout
cannot take a keystroke.



## Installation

### In your plugin

```
dotnet add package PanoramaManager
```

The package brings `gamedata/panoramamanager.json` with it, and the DLL copies next to your plugin
on build - which is what CounterStrikeSharp needs, since each plugin loads through a context that
probes its own directory.

Then, on the server:

1. Copy `gamedata/panoramamanager.json` to `addons/counterstrikesharp/gamedata/`. It ships in the
   package under `contentFiles/any/any/gamedata/`, and in every [release](../../releases).
2. Add `workshop/panorama/` to a workshop addon, build it, and mount it. The paths must stay as
   `panorama/layout/custom_game` and `panorama/styles/custom_game` - that is the search path CS2
   registers for custom HUD layouts.

### Just the examples

Download the latest [release](../../releases), do steps 1 and 2 above, and copy the plugins you want
from `plugins/` to `addons/counterstrikesharp/plugins/`.

Either way, check it came up:

```
css_panorama_diag
```

A healthy start logs nothing. An error means the gamedata file is missing or a signature stopped
resolving after a CS2 update, and it says what to do about it.



## Commands


| Command             |                                                                    |
| ------------------- | ------------------------------------------------------------------ |
| `css_panorama_diag` | gamedata source, which natives resolved, click channel, live menus |
| `css_admin`         | example admin menu                                                 |
| `css_adminkit`      | the same menu on a different skin                                  |
| `css_textinput`     | example text prompt                                                |




## API

### Interface

```csharp
// Setup
Panorama.Init(this);                                  // once, in Load
Panorama.Shutdown();                                  // in Unload
bool Panorama.CanReceiveClicks { get; }
bool Panorama.CanWritePerPlayerText { get; }
bool Panorama.SetHideHud(CCSPlayerController player, HideHudFlags flags, bool hide);

PanelHandle Panorama.Spawn(string layoutPath, LayoutContract? contract = null);

// Any panel
event Action<PanelEvent> OnEvent;
int OpenCount { get; }

PanelHandle SetVariableFor(CCSPlayerController player, string name, string value);
PanelHandle SetClassFor(CCSPlayerController player, string panelId, string cls, bool on);
PanelHandle SetVariant(string group, string? value);
PanelHandle SetVariable(string name, string value);          // every viewer

// Menus, on top of the same handle
string          Title, Subtitle { get; set; }
IList<string>   Tabs { get; }
int             PageSize, PageCount { get; }

PanelHandle SetItems(IEnumerable<MenuItem> items);

void Open(CCSPlayerController player);
void Close(CCSPlayerController player);
void Refresh(CCSPlayerController? player = null);
bool IsOpenFor(CCSPlayerController player);

// A row
record MenuItem(string Id, string Title, string? Subtitle = null,
                Action<PanelEvent>? OnSelect = null, bool Enabled = true, object? Tag = null);

// What the layout is called
class LayoutContract
{
    string  RootPanelId;      // default "PanoramaRoot"
    int     RowCount;         // physical rows in the layout - this is the page size
    string? RevealClass;      // set for an animated layout instead of collapse-to-hide
    bool    CaptureInput;     // false for anything the player only reads
    HideHudFlags HideHud;     // hidden while open, restored on close
    // plus the id and class names the library drives
}
```



### Example usage

```csharp
public class AdminMenu : BasePlugin
{
    private PanelHandle? _menu;

    public override void Load(bool hotReload)
    {
        Panorama.Init(this);

        _menu = Panorama.Spawn("panorama/layout/custom_game/admin_hud.vxml_c");
        _menu.OnEvent += OnMenuEvent;
    }

    public override void Unload(bool hotReload)
    {
        _menu?.Dispose();
        Panorama.Shutdown();
    }

    [ConsoleCommand("css_admin")]
    [RequiresPermissions("@css/generic")]
    public void OnAdmin(CCSPlayerController? player, CommandInfo command)
    {
        if (player is not { IsValid: true } || _menu is null) return;

        _menu.Title = "Admin";
        _menu.SetItems(Utilities.GetPlayers()
            .Where(p => p is { IsValid: true, IsHLTV: false })
            .Select(p => new MenuItem(
                Id:       $"player:{p.Slot}",
                Title:    p.PlayerName,
                Subtitle: $"{p.Ping}ms",
                OnSelect: e => Kick(p))));

        _menu.Open(player);
    }

    private void OnMenuEvent(PanelEvent e)
    {
        // Runs before any row's OnSelect. Cancel here and the row callback never fires, so
        // authorisation lives in one place instead of on every item.
        if (e.Action == PanelAction.Click && !AdminManager.PlayerHasPermissions(e.Player, "@css/generic"))
            e.Cancel = true;
    }
}
```



### Layouts

A layout is Panorama XML plus CSS that the client already has. The library drives it by id, so the
two have to agree - `LayoutContract` is where you say what yours is called.

Each example under `[Examples/](Examples)` ships its layout in a `workshop/` folder, and
`PanoramaManager/workshop/` carries `hud_shared.css`, which they all include. `tools/validate.py` checks a layout before
you compile it, and `tools/preview.py` renders one in a browser so you are not doing a
compile-pack-copy-restart-join cycle for a padding value.

See [Writing the layouts](#writing-the-layouts) above for the tooling.

## Built with this


|                                                                     |                                                                                                                                                                                                                                                                              |
| ------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| [Toasts](https://github.com/Next-il/Toasts)                         | Shared notification system. Any plugin on the server can send a toast through one service - stacked, coloured, animated, with a progress bar. Uses none of the menu layer; it drives its panels directly.                                                                    |
| [PanoramaHUD-Skills](https://github.com/Next-il/PanoramaHUD-Skills) | Agent skills for Claude Code / Cursor / Copilot that teach them to write Panorama layouts. Panorama looks like web CSS and silently drops what it does not recognise; this carries the full vocabulary read out of `libpanorama.so`, plus a validator and a browser preview. |




## Credits

- [cs2-customhud](https://gitlab.com/cs2-server-plugins/cs2-customhud) - the engine signatures in
`gamedata/panoramamanager.json` derive from its reverse engineering
- [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) - the framework this runs on



## Notes

- **Linux tested.** Windows signatures are in the gamedata but have not been run.
- Four things the server genuinely cannot do, no matter the API: create panels, send a colour, send a
coordinate, or take a keystroke. Everything here is strings into dialog variables and class
toggles, which is why colours and widths are class palettes.



## Need help?

Open an [issue](../../issues). Include the output of `css_panorama_diag` - it says which natives
resolved, which is the first thing worth knowing when a menu renders but does nothing.