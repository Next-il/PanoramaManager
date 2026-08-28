# panoramamanager.json

Copy it to your server:

```
game/csgo/addons/counterstrikesharp/gamedata/panoramamanager.json
```

That is all it needs. The plugin has compiled-in copies of these values, so it runs without the file
too - but the file wins when present, which is the point: after a CS2 update you fix signatures by
editing this rather than rebuilding.

If a menu renders but does nothing, run `css_panorama_diag` in the console. It names which entry
failed to resolve.

Derived from [cs2-customhud](https://gitlab.com/cs2-server-plugins/cs2-customhud) (MIT), with the
Linux column re-derived against a live `libserver.so`. Windows is untested.
