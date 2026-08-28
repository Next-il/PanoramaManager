# workshop

`hud_shared.css` - the canonical copy.

The library toggles `.hidden`, `.disabled` and `.anchor-*` by name, so the stylesheet that implements
them is part of its contract, not decoration. It also carries the card, header, footer and reveal
that layouts build on.

Every project keeps its own copy so it can be split into its own repo and still build. **This is the
one to edit**; the others follow it. `collect-panorama.py` compares them and refuses to assemble
mismatched copies, so drift is caught at build time rather than in game.
