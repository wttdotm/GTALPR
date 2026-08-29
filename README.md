** Description


Grant Theft Automated License Plate Reader is a GTA V mod that installs 235 Flock Automated License Plate Reader cameras (ALPRs) around the Los Santos. This number is exactly 1/10th the amount of Flock ALPRS currently installed in LA County, which GTA V is heavily based on. As players drive through the map, they are subjected to seemingly inescapable surveillance, with pictures taken of them every time they enter a new camera's field of view. This makes the game much harder, as not only does police surveillance become nearly impossible to evade, but also Flock's own 5% inaccurate report rate means that every time a player passes through a camera's field of view, there's a 1 in 20 chance that the ALPR will trigger a police chase without any cause.




This not only makes the game's chase mechanics harder (it is much more difficult to escape the cops when they have eyes on every block), it also throws a wrench into daily life: 

A few notable features:
1. When destroyed, players can pick up the remnants of the camera for $_____, the market value of all components in a Flock camera based on a hardware teardown by _______.
2. Reflecting Flock's own accuracy numbers, all cameras have a _____% chance to report an innocent / unwanted player to the police, triggering a chase when the player was doing nothing wrong.
3. While Los Santos is a shrunken and fictionalized vrersion of LA, there are many points across the two where their maps and camera placements are near one-to-one. Some examples include [Muscle Beach / Muscle Sands, ]
4. 


 matches the exact amount of Automatic License Plate Reader cameras (or ALPRs for short) that exist in LA county today.



Install new YFT by
1 - Import the fragment XML into addonprops
2 - Add the archetype to props 


TODO:
- [] Persist cameras between script runs
- [] Results screen / complete!
- [] Remove debug f keys
- [] Add controller controls (triple click left to activate/deactivate mod, triple click right to place test camera)
- [] Remove props that intersect with cameras

Control Panel:
- Triple click to activate
- Turn mod on/off
- Reset cameras
- Toggle debug view (FOV lines basically)
- See stats
- Photo Section
- - Toggle Photos on/off
- - Show N Camera Captures, X processed / Y queued
- - Clear Photo Queue
- - Process Photo Queue (show N screenshots ready)

Image stuff:
- Clear out JSON captures on successful image generation
- Add GTALPR Overlay to image capture
- Add an on-collision hit image capture? or maybe the frame or two after destruction? This shouldnt need a script generation thing we can probably just scrape it from the in-game gamera 
- 


** CONTRIBUTING:
There's a lot that could be fixed / updated / optimized about this mod.