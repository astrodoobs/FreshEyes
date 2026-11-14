<p align="center"><img src="images/icon.png" width="128" alt="Fresh Set of Eyes icon"></p>

# Fresh Set of Eyes

**See the world through your character's eyes.** A Dalamud plugin that gives FFXIV a true first-person view.

Formerly *StopTheClip*, a maintained fork of the plugin by [ProjectMimer](https://github.com/ProjectMimer), built on XIVR code.

## What it does

- **True first person.** Zoom all the way in and the camera sits at your character's eyes. It follows every emote and animation, and you can look down and see your own body.
- **Your face stays out of the way.** Your own face, lashes and hair are hidden automatically; your body, hands and weapon stay visible.
- **Natural head movement.** The view is limited to the range a head can turn, your eyes pivot around your neck, and head motion can be followed, smoothed or steadied while you move.
- **Looks at what you select.** Selecting a target turns your view to it. If the target leaves your range, it stays selected and you look ahead again.
- **No more vanishing models.** Characters and bosses no longer disappear when the camera gets close, and the near clip is lowered for close-up screenshots (the original StopTheClip behaviour).

## Install

1. In game, open `/xlsettings` → **Experimental** → **Custom Plugin Repositories**.
2. Add this URL and save:

   `https://raw.githubusercontent.com/astrodoobs/FreshEyes/main/repo.json`

3. Open `/xlplugins`, search for **Fresh Set of Eyes** and install it.

You can also download `latest.zip` from the [releases page](https://github.com/astrodoobs/FreshEyes/releases).

## Use

- `/fresheyes` opens the settings (`/stoptheclip` still works).
- `/fresheyes enable` and `/fresheyes disable` switch the plugin on and off.
- Zoom all the way in to enter first person.

| Tab | Settings |
|---|---|
| **General** | Enable the plugin; enable true first person |
| **First Person** | Hide my face; move eyes with head tilt; fine tuning of the eye position |
| **Head & Targeting** | Follow, smooth and limit head motion; look at what you select |
| **Debug** | Live diagnostics for troubleshooting |

Settings from StopTheClip are carried over the first time the plugin loads.

## Combat and PvP

Fresh Set of Eyes pauses itself in combat and in PvP areas: the game's normal camera and clipping come back until you're out. Both can be switched back on under **General → Experimental**.

## Build

Requires the .NET 10 SDK and a Dalamud install (API 15).

```
DALAMUD_HOME=/path/to/dalamud/Hooks/dev dotnet build -c Release StopTheClip/StopTheClip.csproj
```

The packaged plugin is written to `StopTheClip/bin/Release/FreshSetOfEyes/latest.zip`.

## How it was made

AI use: **Copilot** level, in the terms of Dalamud's AI usage policy. I forked this plugin from ProjectMimer's StopTheClip, which appears to be discontinued, and later stopped maintaining my fork as well. To bring it up to date and add a true first-person camera, I used Claude (through Claude Code) to write the code. I directed the work and tested every change in game myself before moving on. No automated or unattended workflow was used at any point.

The icon is AI-generated. I'm not a graphic designer, so it will do for now. If you'd like to make a proper one, DM me!

## Licence

My changes are released under the [MIT licence](LICENSE). The original StopTheClip code by ProjectMimer was published without a licence file; it remains theirs.

## Credits

- [ProjectMimer](https://github.com/ProjectMimer) (Marulu, StreetRat) for StopTheClip and XIVR, which this is based on.
