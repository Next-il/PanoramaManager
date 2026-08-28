# workshop

The panorama files for the AdminMenuKit example.

```
panorama/layout/custom_game/admin_hud_kit.xml
panorama/styles/custom_game/admin_hud_kit.css
panorama/styles/custom_game/hud_shared.css    copy of the library's, so this example stands alone
```

Assemble every project's files into an addon and compile:

```
python3 ../../../tools/collect-panorama.py --out "X:/.../content/csgo_addons/hud_test1"
../../../tools/build-hud.cmd
```

Or check just this one without compiling anything:

```
python3 ../../../tools/validate.py .
python3 ../../../tools/preview.py panorama/layout/custom_game/admin_hud_kit.xml 5
```

Do not edit `hud_shared.css` here - edit the library's copy in `PanoramaManager/workshop` and let the
collector catch the drift.
