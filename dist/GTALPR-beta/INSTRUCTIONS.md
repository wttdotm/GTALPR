# GTALPR Beta Installation and Testing

GTALPR is a single-player mod for **Grand Theft Auto V Legacy**. Do not use it
in GTA Online. GTA V Enhanced is not supported by this beta.

## INSTALLATION

### IF YOU HAVE SET UP GTA MODS before

1. Copy the files in this directory's `scripts` folder into your own GTA `scripts` folder
2. Put the `gtalpr` folder into your GTA `mods\update\x64\dlcpacks` folder
3. In OpenIV, add `<Item>dlcpacks:/gtalpr/</Item>` to the end of the list of items in `mods\update\update.rpf\common\data\dlclist.xml`

(NOTE: LemonUI 2.2.0 is included in this package. You can probably skip it if you already have it installed.)

### IF YOU HAVE NOT SET UP GTA MODS BEFORE

#### Modding Setup:

1. Install GTA V Legacy with Story Mode, however is easiest for you.
2. Find where your GTA folder actually lives (like, where GTAV.exe is). You are gonna copy some stuff into here. On steam this is the folder shown by Library -> Right click GTA V Legacy -> Browse Local Files.
2. Download Script Hook V and copy the files in the `bin` folder (not the folder itself, just the files) right into your GTA folder:
   <https://www.dev-c.com/gtav/scripthookv/>
3. Download ScriptHookVDotNet v3 (use the most recent nightly release here) and copy all the files that start with "ScriptHookVDotNet" into your GTA folder.
   <https://github.com/scripthookvdotnet/scripthookvdotnet-nightly/releases>
4. Download OpenIV and install it. Once installed, open it, go to Tools, click ASI Manager, and install the OpenIV ASI loader.
5. Download and install the .NET Framework 4.8. https://dotnet.microsoft.com/en-us/download/dotnet-framework/net48 and Use either of the "Run Apps  - Runtime" installers.
6. Download and the current Microsoft Visual C++ x64 runtime. [For Visual C++ x64runtime ]CHECK IF NECESSARY
7. Restart your computer.

*If you want, you can check if everything is working by loading GTA and pressing f4. If you see a command line pop up, you're in!*


#### GTALPR Install:

1. Close GTA V and go to your GTA V folder. We'll call that folder `<GTA V>`
2. In that folder, make a `mods` folder and a `scripts` folder.
3. Copy the files from the GTALPR `scripts` folder into the new `scripts` folder you just made.
4. Copy the folder from the GTALPR `mods` folder called `update` into the new `mods` folder you just made.
5. In your GTA folder, there is a folder called `<GTA V>\update\` that has two files in it called `update.rpf` and `update2.rpf`, copy them both into the `<GTA V>\mods\update` folder that you just pasted in.
6. Your GTAV folder should now look like this (aside from the many other files in there):
```
<GTA V>/
├── scripts/
│   ├── GTALPR.dll
│   ├── LemonUI.SHVDN3.dll
│   └── in_game_cameras.json
└── mods/
    └── update/
        ├── update.rpf/
        ├── update2.rpf/
        └── x64/
```

7. Open OpenIV, turn on edit mode, and go to `<GTA V>\mods\update\update.rpf\common\data\dlclist.xml`. Right click on it and select "edit."
8. Add `<Item>dlcpacks:/gtalpr/</Item>` as the last line before the closing `</Paths>` tag
9. Save and you're done!

*The mod should now be ready to play :)*

## PLAYING WITH THE MOD

On game startup, GTALPR should report that its camera definitions loaded.

*Controls:*
- Open/close the control panel: **F7**.
- Controller shortcut: hold **RB** and tap **D-pad Up** while stopped.
- Place a camera manually with the control panel, you can also save the cameras you place so they'll persist between sessions.
- Render and save the pictures the cameras have taken of you (and of you destroying cameras) in the PHOTOS section of the control panel.
- See stats in the STATS section of the control panel.

*Stuff you can do/toggle in settings:*
- Cameras on/off
- Picture-taking on-off (note that this does not turn off other functionality like cop calling and shutter sound, but cameras off does)
- Debug and line-of-sight graphics on-off
- Toggle/change the strength of the CCTV filter on the image
- Respawn all cameras
- Reset Wanted meter to 0

*Photo capture and Photo Lab output are stored under:*
```text
%USERPROFILE%\Pictures\FlockSurveillance\Captures
```

## Beta Testing

Have fun with it! Fuck around with the settings, drive around, see if you can get yourself accidentally targeted by a camera. Get into a police chase and try to get away without running into cameras. Render the pictures you've had taken and see what they look like. Go up to the mountains and see if you can get a pic in the air. Etc :)

## Troubleshooting

- **The script does not load (no popup above the minimap on load):** confirm `GTALPR.dll`,
  `LemonUI.SHVDN3.dll`, and `in_game_cameras.json` are together in the
  `scripts` folder. Check `ScriptHookVDotNet.log` in the GTA V folder.
- **No Flock camera models appear / "flockfragment is not valid":** confirm the `gtalpr` DLC path and
  `dlclist.xml` entry exactly match the paths above.
- **Photo Lab will not capture:** switch to borderless/windowed mode and keep
  GTA focused and fully visible on a monitor.