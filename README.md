** Description


Grant Theft Automated License Plate Reader is a GTA V mod that installs 235 Flock Automated License Plate Reader cameras (ALPRs) around the Los Santos. This number is exactly 1/10th the amount of Flock ALPRS currently installed in LA County, which GTA V is heavily based on. As players drive through the map, they are subjected to seemingly inescapable surveillance, with pictures taken of them every time they enter a new camera's field of view. This makes the game much harder, as not only does police surveillance become nearly impossible to evade, but also Flock's own 5% inaccurate report rate means that every time a player passes through a camera's field of view, there's a 1 in 20 chance that the ALPR will trigger a police chase without any cause.




This not only makes the game's chase mechanics harder (it is much more difficult to escape the cops when they have eyes on every block), it also throws a wrench into daily life: 

A few notable features:
1. When destroyed, players can pick up the remnants of the camera for $_____, the market value of all components in a Flock camera based on a hardware teardown by _______.
2. Reflecting Flock's own history of inaccurate captures and reporting, all cameras have a _____% chance to report an innocent / unwanted player to the police, triggering a chase when the player was doing nothing wrong.
3. While Los Santos is a shrunken and fictionalized vrersion of LA, there are many points across the two where their maps and camera placements are near one-to-one. Some examples include [Muscle Beach / Muscle Sands, ]
4. Players can choose to have the cameras take actual pictures of them while they play and then render and save them later. The pictures that the mod's Flock cameras take mimic Flock's own watermarking/overlays.
5. The 3D model used for the camera in game is custom-built to mimic Flock's own cameras.


 matches the exact amount of Automatic License Plate Reader cameras (or ALPRs for short) that exist in LA county today.



Install new YFT by
1 - Import the fragment XML into addonprops
2 - Add the archetype to props 


TODO:
- [x] Check that cameras persist between script runs (check)
- [] Remove debug f keys
- [x] Make "remove wanted level" a menu item
- [x] Add controller controls (triple click left to activate/deactivate mod, triple click right to place test camera)
- [] Remove props that intersect with cameras
- [x] Add cooldown for camera photos so that it doesnt take like 3 in 2s


Control Panel:
- [x] Change to F7 or RB + D-Up when still to activate
- [x] Turn camera network on/off
- [x] Reset cameras
- [x] Toggle debug view (FOV lines basically)
- [x] See stats
- Photo Section
- - [x] Toggle Photos on/off
- - [x] Show N Camera Captures, X processed / Y queued
- - Clear Photo Queue
- - [x] Process Photo Queue (show N screenshots ready)
- - [x] Make background process update the Photos stats live when done and the menu is open 

Image stuff:
- [x] Maybe move captures + pictures to one big folder
- [x] Add GTALPR Overlay to image capture
- [] Add an on-collision hit image capture? or maybe the frame or two after destruction? This shouldnt need a script generation thing we can probably just scrape it from the in-game gamera 
- [] Re-implement the slight delay before press B or esc to cancel
- 


Assumptions to test:
- [x] Need 300f distance from place to take picture (could we just teleport someone?)
- [x] No faster way to render the images
- [x] Weapons can damage the poles

To fix:
- [x] Debounce cameras by 2s 
- [x] Menu title should be in font 7 (centered, white on black)
- [x] Allow multiple manual cameras. Menu item to save manual cameras to permanent list.

To add:
- [x] Place camera like menyoo (this should also show camera FOV while placing)
- [] Make "Learn more" info not look like ass





LEARN MORE POPUP (Esc or B on controller to close):
What is Flock?
Flock is an $8.3 billion company that makes and sells Automatic License Plate Readers (ALPRs or LPRs) to local governments. ALPRs are AI-powered cameras that capture and store information about all passing vehicles without a warrant. Your car's make, model, color, license plate, location, heading, bumper stickers, dents, and more are all stored and made searachable by the cops even if you haven't done anything wrong. With over 100,000 cameras currently deployed in the US, it is very likely that you, yourself, your movements and life, are in their database.

Isn't it good to catch criminals though?
These cameras don't monitor criminals. They monitor everyone. There are many tools cops have to monitor criminals that are more targeted and require a warrant or have other oversight that are already extremely powerful. These cameras don't have those limitations, they treat everyone as a criminal waiting to be caught.

But I'm not a criminal?
That doens't mean you can't get caught! Flock has a 5% misreport rate, and across hundreds of thousands of cameras, that means there are countless incidents of cops chasing, detaining, and falsely accusing innocent civilians of crimes that they never actually committed. You can experience this yourself as part of the mod, as every time you pass by a camera without a wanted level, there is a 5% chance you get the cops called on you regardless.

That's fucked, what can I do to help?
You can find Anti-Flock advocacy groups near you and learn more at DeFlock.org, as well as dive deeper into your local surveillance policies around Flock and other technoligies at the Electronic Frontier Foundation's project AtlasOfSurveillance.org.

CREDITS:
Mod Creation & Development - Morry Kolman @WTTDOTM
Flock Camera 3D Model - Sean Kennedy @aie_sean 



This mod:
1. Installs _____ Flock cameras in Los Santos, exactly 1/10th the amount currently invading Los Angeles County.
2. Uses a custom camera model that closely mimics Flock's own cameras. 
3. Drops a box of the components in a Flock camera whenever destroy one. Picking it up gives you money equal to the estimated market value of those components.
4. Takes pictures of you every time you pass by

    "Automated License Plate Readers (ALPRs or LPRs) are AI-powered cameras that capture and analyze images of all passing vehicles, storing details like your car's location, date, and time. They also capture your car's make, model, color, and identifying features such as dents, roof racks, and bumper stickers, often turning these into searchable data points.

These cameras collect data on millions of vehicles regardless of whether the driver is suspected of a crime. These systems are marketed as indispensable tools to fight crime, but they ignore the powerful tools police already have to track criminals, such as cell phone location data, creating a loophole that doesn't require a warrant."

This Mod:
Th

Learn more and donate at:
- DeFlock.org
- EFF.org
- 


- [] Credit to me in banner
- [] Circular saw animation

Package stuff:
- [] Get prop into own droppable folder
- [] Audit script for relative filesystem stuff that oculd be unique to me
- [] ??



Mod info:
RB + D-Up to bring up menu
Y to place a new camera

Contest:


** CONTRIBUTING:
There's a lot that could be fixed / updated / optimized about this mod.