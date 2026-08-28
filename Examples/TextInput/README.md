# TextInput

How to get a line of typed text out of a player.

```
!textinput      (or css_textinput)
```

Click **Set message**, type in chat. The message is swallowed rather than broadcast, and appears in
the panel's subtitle.

## Why this needs a demo

A `custom_hud_layout` may only contain `Panel`, `Label`, `Image` and `Button`, and carries no
scripts. A layout therefore **cannot accept a keystroke** - there is no `TextEntry` to enable and no
script to read one. Chat is the only text a player produces that reaches the server, so
`PromptText` borrows it.

That is a constraint of the entity, not a design preference. There is no better mechanism to switch
to later.

## What to try

| | |
|---|---|
| Type something | Saved, echoed into the subtitle, footer confirms |
| Type `cancel` | Ends without saving |
| Wait 20 seconds | Times out (the library default is 60; this demo shortens it) |
| Open the menu, then close it while it is waiting | Abandoned - the prompt does not outlive its menu |

All four reach `OnResult`. A handler that only checks `Submitted` leaves the menu stuck on
"Waiting for chat..." in the other three, which is why the example switches on every outcome.

## Its layout

```
workshop/panorama/layout/custom_game/text_input.xml
workshop/panorama/styles/custom_game/text_input.css
```

Every example's layout lives in the repo's `workshop/` folder, which is what you add to a workshop
addon. To see it without compiling:

```bash
python3 tools/preview.py workshop/panorama/layout/custom_game/text_input.xml
```

The readout is the point. A layout cannot accept a keystroke, so the text comes through chat and the
server writes it into `{s:input_value}` - showing it back prominently is what makes that indirection
read as an input box rather than a trick. It has a fixed height so an empty value looks like an empty
box rather than a missing panel.

The text comes from a client. The library trims it, strips control characters and truncates to
`MaxLength` before it reaches a Label, because a Label handed a few thousand characters is a
rendering problem. Validate it for your own purposes on top of that.
