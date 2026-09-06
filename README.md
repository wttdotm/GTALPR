# Grand Theft Automated License Plate Reader (GTALPR)

## Description: 

Grant Theft Automated License Plate Reader is a GTA V mod that installs 235 Flock Automated License Plate Reader cameras (ALPRs) around the in-game map of Los Santos. This number is 1/10th the amount of Flock ALPRS currently tracked by [DeFlock.org](https://deflock.org) in LA County, which GTA V is heavily based on. As players drive through the map, they are subjected to seemingly inescapable surveillance, with pictures taken of them every time they enter a new camera's field of view. This makes the game much harder, as not only does police surveillance become nearly impossible to evade, but representing Flock's own history of inaccurate reporting, every time a player passes through a camera's field of view, the mod creates a 1 in 20 chance that the ALPR will trigger a police chase without any cause.

A few other notable features:
1. When destroyed, players can pick up the remnants of the camera for $600, the estimated market value of all components in a Flock camera based on a list created in a [hardware teardown by EyesOffCR](https://eyesoffcr.org/blog/blog-8.html).
2. While Los Santos is a shrunken and fictionalized version of LA, there are many points across the two where their maps and camera placements are near one-to-one. Muscle Beach & Muscle Sands is a good example (see below).
3. Players can choose to have the cameras take actual pictures of them while they play and then render and save them later. The pictures that the mod's Flock cameras take mimic Flock's own watermarking/overlays and support widescreen as well as 4:5 and 9:16 vertical formats for easy sharing on socials. Pictures can also be taken any time a player destroys a camera, letting them compile an album of destruction.
4. The 3D model used for the camera in game is custom-built to mimic Flock's own cameras.
5. There is built-in speedrun functionality, with a stats screen tracking number of cameras destroyed, and fastest time to destroy 10, 50, or all cameras.

## Installation
You can download the latest version of the mod from the [releases page](https://github.com/wttdotm/gtalpr/releases). Instructions for installation are included in the description of the release, in the `INSTRUCTIONS.md` file in this repo, and  the `INSTRUCTIONS.md` file in the ZIP. All instructions are the same.

## Settings & Defaults
Most of the features of the mod are toggleable in settings. Here are some of the most important ones and their default states:

| Setting                  | Description                                                                                                    | Location  | Default        |
|--------------------------|----------------------------------------------------------------------------------------------------------------|-----------|----------------|
| Cameras Network Enabled  | Turns all cameras and related functionality on/off (basically a total on/off switch)                           | Main Menu | On             |
| Show FOV Debug Geometry  | Shows a basic wireframe FOV for every camera. Useful for seeing them coming and knowing their range.           | Main Menu | Off            |
| Show Line of Sight       | When within a camera's FOV, shows a line of sight from the camera to the player. Useful for seeing the camera. | Main Menu | On             |
| Capture Photos           | Captures photos for rendering whenever you drive into a camera's line of sight.                                | Photos    | On             |
| Camera Destruction Cam   | Captures photos for rendering whenever you destroy a camera (within a reasonable distance)                     | Photos    | On             |
| Aspect Ratios            | Different aspect ratios that the photo captures get cropped to (original, 4:5, 9:16)                           | Photos    | Original, 9:16 |
| CCTV Filter              | Two settings that affect whether a CCTV filter is applied to the photos and how strong it is                   | Photos    | On, 0.65       |                                                                                |           |         |


## Gallery

Some favorite images so far:

![Car escaping cops](assets/red_cops.jpg)
![Rocket launcher pov](assets/rocket_launcher.jpg)
![Destruction camera example](assets/rocket_launcher_2.jpg)
![Destruction camera car](assets/car_destruction.jpg)
![Tankkkkkkk](assets/tank_chillin.jpg)
![Plane!!!!!!](assets/plane_sunset.jpg)
![Car flying](assets/pink_flying.jpg)


## Contributing / Modifying
As you can see, this repo is a bit of a mess. I'm a JS and Python guy, so I do not know C# and I developed most of it by iterating and tweaking funcionality with an LLM. Most of the important logic of the mod (camera loading and placement, camera FOV, reporting flow, etc) lives in `SurveillanceScript.cs`, so start there. Stats logic lives in `SurveillanceStats.cs`, and then most of the other files are pretty much all dedicated to the photo capture and rendering workflow.

## Special Thanks
...to Sean Kennedy [@aie_sean](https://instagram.com/aie_sean) for creating the 3d model of the Flock camera used in the mod.

...and to [Albert Sellars LLP](https://www.albertsellars.law/) for their pro bono legal assistance in the release of this mod.

## Disclaimer
All trademarks are the property of their respective owners. GTALPR is in no way endorsed by or affiliated with Flock or Rockstar Games. Like Grand Theft Auto generally, the mechanics and gameplay of GTALPR are not an endorsement of the activities players may perform with the mod. Destroying Flock cameras in real life is illegal and will not, as far as we know, give you a loot box with $600.

The Flock logo in flock_logo_transparent.png is not subject to the license of GTALPR and is owned by Flock.

## Contact
For any questions or inquiries, please contact me at wttdotm [at] gmail [dot] com.</p>


--


** Description


Grant Theft Automated License Plate Reader is a GTA V mod that installs 235 Flock Automated License Plate Reader cameras (ALPRs) around the Los Santos. This number is exactly 1/10th the amount of Flock ALPRS currently installed in LA County, which GTA V is heavily based on. As players drive through the map, they are subjected to seemingly inescapable surveillance, with pictures taken of them every time they enter a new camera's field of view. This makes the game much harder, as not only does police surveillance become nearly impossible to evade, but also Flock's own history of inaccurate reporting means that every time a player passes through a camera's field of view, the mod creates a 1 in 20 chance that the ALPR will trigger a police chase without any cause.

A few notable features:
1. When destroyed, players can pick up the remnants of the camera for $600, the estimated market value of all components in a Flock camera based on a hardware teardown.
2. Reflecting Flock's own history of inaccurate captures and reporting, all cameras have a 5% chance to report an innocent / unwanted player to the police, triggering a chase when the player was doing nothing wrong.
3. While Los Santos is a shrunken and fictionalized version of LA, there are many points across the two where their maps and camera placements are near one-to-one. Muscle Beach & Muscle Sands is a good example.
4. Players can choose to have the cameras take actual pictures of them while they play and then render and save them later. The pictures that the mod's Flock cameras take mimic Flock's own watermarking/overlays. Pictures can also be taken any time a player destroys a camera, letting them compile an album.
5. The 3D model used for the camera in game is custom-built to mimic Flock's own cameras.
6. There is built-in speedrun functionality, with a stats screen tracking number of cameras destroyed, and fastest time to destroy 10, 50, or all cameras.


 matches the exact amount of Automatic License Plate Reader cameras (or ALPRs for short) that exist in LA county today.



Install new YFT by
1 - Import the fragment XML into addonprops
2 - Add the archetype to props 


TODO:
- [x] Check that cameras persist between script runs (check)
- [] Remove debug f keys
- [x] Make "remove wanted level" a menu item
- [x] Add controller controls (triple click left to activate/deactivate mod, triple click right to place test camera)
- [] Remove props that intersect with cameras [known bug, fix later]
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