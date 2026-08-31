# GTALPR Beta Installation and Testing

GTALPR is a single-player mod for **Grand Theft Auto V Legacy**. Do not use it
in GTA Online. GTA V Enhanced is not supported by this beta.

## Package contents

```text
GTALPR-beta/
├── INSTRUCTIONS.md
├── scripts/
│   ├── GTALPR.dll
│   ├── LemonUI.SHVDN3.dll
│   └── in_game_cameras.json
└── mods/
    └── update/
        └── x64/
            └── dlcpacks/
                └── gtalpr/
                    └── dlc.rpf
```

## Requirements

Install these before installing GTALPR:

1. GTA V Legacy with Story Mode.
2. The current Script Hook V version matching your installed GTA V build:
   <https://www.dev-c.com/gtav/scripthookv/>
3. ScriptHookVDotNet v3. For current GTA V builds, use a compatible recent
   nightly release and install its ASI and API DLL files together:
   <https://github.com/scripthookvdotnet/scripthookvdotnet-nightly/releases>
4. OpenIV's ASI loader/mods-folder support so GTA can load add-on DLC packs.
5. .NET Framework 4.8 and the current Microsoft Visual C++ x64 runtime.

LemonUI 2.2.0 is included in this package. If another mod already installed a
newer `LemonUI.SHVDN3.dll`, back it up before replacing it; multiple mods can
share that file.

## Install

1. Find your GTA V Legacy folder. This is the folder containing `GTA5.exe`.
   Its location differs between Steam, Epic, and Rockstar installations.
2. Close GTA V.
3. Copy this package's `scripts` folder into the GTA V folder. Merge it with
   the existing `scripts` folder if one exists.
4. Copy this package's `mods` folder into the GTA V folder. The resulting DLC
   path must be:

   ```text
   <GTA V>\mods\update\x64\dlcpacks\gtalpr\dlc.rpf
   ```

5. In OpenIV, enable Edit Mode and open:

   ```text
   mods\update\update.rpf\common\data\dlclist.xml
   ```

6. Back up `dlclist.xml`, then add this line before the closing `</Paths>`
   element:

   ```xml
   <Item>dlcpacks:/gtalpr/</Item>
   ```

7. Save the file and launch GTA V Story Mode.

On startup, GTALPR should report that its camera definitions loaded. If an
older startup message mentions F6, ignore it and use the control-panel camera
placement described below.

## Controls

- Open/close the control panel: **F7**.
- Controller shortcut: hold **RB** and tap **D-pad Up** while stopped.
- Place a manual camera: open the control panel and choose
  **Place Manual Camera**.
- In manual placement:
  - Move: left stick or WASD.
  - Look: right stick or mouse.
  - Raise/lower: RT/LT or Space/Ctrl.
  - Move faster: left-stick click or Shift.
  - Rotate camera: LB/RB or Q/E.
  - Confirm: A or Enter.
  - Cancel: B or Escape.

The control panel also lets you enable/disable the camera network, restore
destroyed cameras, save manually placed cameras, inspect statistics, configure
photo capture, and render queued photos.

## Photos and saved data

Photo capture and Photo Lab output are stored under:

```text
%USERPROFILE%\Pictures\FlockSurveillance\Captures
```

Windows may redirect Pictures to OneDrive. In that case, Windows can sync the
captures according to your OneDrive settings. GTALPR itself does not upload
photos.

Photo Lab requires GTA to remain in the foreground. Use windowed or
borderless-windowed mode; exclusive fullscreen is not supported for screenshot
capture.

Persistent mod data is stored under:

```text
%LOCALAPPDATA%\FlockSurveillance
```

This includes statistics, saved manual cameras, and camera destruction state.
The installed `scripts\in_game_cameras.json` catalog is read-only player
content and is not rewritten.

## Troubleshooting

- **The script does not load:** confirm `GTALPR.dll`,
  `LemonUI.SHVDN3.dll`, and `in_game_cameras.json` are together in the
  `scripts` folder. Check `ScriptHookVDotNet.log` in the GTA V folder.
- **No Flock camera models appear:** confirm the `gtalpr` DLC path and
  `dlclist.xml` entry exactly match the paths above.
- **GTA updated recently:** install the matching current Script Hook V and
  compatible ScriptHookVDotNet files before reporting a GTALPR problem.
- **Photo Lab will not capture:** switch to borderless/windowed mode and keep
  GTA focused and fully visible on a monitor.

When reporting a beta issue, include the GTA V build number, Windows version,
install platform, `ScriptHookVDotNet.log`, and exact reproduction steps.

## Uninstall

1. Close GTA V.
2. Remove `GTALPR.dll` and `in_game_cameras.json` from `scripts`.
3. Remove `mods\update\x64\dlcpacks\gtalpr`.
4. Remove `<Item>dlcpacks:/gtalpr/</Item>` from your modded
   `dlclist.xml`.
5. Remove `LemonUI.SHVDN3.dll` only if no other installed mod uses it.

To remove saved data and captures as well, delete
`%LOCALAPPDATA%\FlockSurveillance` and
`%USERPROFILE%\Pictures\FlockSurveillance`. Those deletions are optional.
