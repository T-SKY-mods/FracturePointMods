Fracture Point fan MOD set 1.1.2
=====================================

Game : Steam build 25787540 / Mod loader: MelonLoader 0.7.3
Japanese: README_JA.txt

  Install.bat                         installs all mods at once
  Uninstall.bat                       removes all mods (keeps MelonLoader)
  Uninstall (also MelonLoader).bat    removes all mods and MelonLoader (fully vanilla)
  Mods\ UserData\                     the mods and their models (used by Install.bat)
  Docs\Weapon_Stats.xlsx              stats of every weapon and rarity
  Source\                             source code of the mods


0. How it works
---------------
  1. "Install.bat" puts ALL mods into the game once (you cannot pick mods at install time)
  2. When you start the game, every mod is ON
  3. In the title screen, open Settings > MOD and turn off the mods you don't want. Play the way you like
     - ON/OFF is applied after you restart the game
     - The rarity mod can also switch its features one by one: rarity, weapon mastery, boss floors
     - Every mod works on its own (e.g. only the survival mode, or only the optimization)
     - Turning a mod off keeps its data (weapon rarities, mastery, attachments, survival record).
       Turn it on again and everything is back


1. The mods (overview)
----------------------
  [Rarity mod] (FPRarity)
    - Every weapon you find gets a rarity (Common to Legendary) and its own stats (damage, fire rate,
      control, ergonomics). Deeper floors drop higher rarities more often
      Every weapon type is equally likely to drop, on every floor
      Control +1 = recoil about -3%, ergonomics +1 = aiming speed +1%
      Ergonomics and control are shown from the real aiming speed and recoil (same number = same performance)
    - Weapon mastery (Lv1-100): kills level it up; every 10 levels gives an upgrade point (free respec)
    - Endless mode floors sometimes become a boss room (stronger every 10 floors)
    - Stats are shown on the game's own stat cards and tooltips. Stat table: Docs\Weapon_Stats.xlsx

  [Survival mode mod] (FPSurvival)
    - Endgame content: fight endless enemy waves on the rooftop
    - Start it from the endless-mode terminal in your room ("SURVIVAL ▶"). Escape through the emergency
      exit during a break to keep the money, the crate weapons and the Broken Weapon Parts enemies dropped.
      You never lose what you brought in (if you go down, that run's pickups and money are lost)
    - With the rarity mod ON, crate weapons get a rarity based on the wave you reached
    - The lighter your loadout, the better the crate rewards tend to be

  [Weapon customization mod] (FPCustom)
    - Add and remove attachments on the "CUSTOMIZE" page of the stash
      Rifles / SMGs / shotguns: 20 attachments (muzzle 5, grip 5, sight 5, laser 3, magazine 2)
      Handguns: 12 attachments (muzzle 5, stock 1, sight 1, laser 3, magazine 2)
    - Unlocking needs "Broken Weapon Parts" (per weapon type)
    - Each attachment changes stats such as recoil, ergonomics and reload
    - Slide changes
        All weapons : while sliding, 70% of enemy bullets miss you
        Main weapons: no aiming while sliding (hip fire works)
        Handguns    : slide about 5m and keep running with no stiffness; aim and fire while sliding or sprinting; move speed +5%
      An angled foregrip gives a main weapon the handgun movement; an M93R stock gives a handgun the main-weapon movement
    - With the rarity mod ON, you can switch between the mastery page and the customize page

  [Optimization mod] (FPOptimize)
    - Cuts wasted CPU work without changing visuals or controls: fewer stutters and less FPS drop with many enemies

  [MOD settings] (FPModOption)
    - Shared parts used by every mod, and the "MOD" tab in the settings. Always on
    - While the mods are installed, nothing is sent to the leaderboard (online rankings are not affected)


2. Install
----------
  1. Quit the game from the menu
  2. Extract the whole zip and double-click "Install.bat". It will
     - find the game folder in your Steam libraries. If it is not found, it asks for it:
       enter the folder that has "Fracture Point Release.exe"
     - back up your save (LocalLow folder + registry) and the mod data to Documents\FPMods_backup\<date>
     - install MelonLoader 0.7.3 from the official GitHub release if needed (SHA256 checked, needs an internet connection)
     - install all mods
  3. Start the game from Steam (the first start with MelonLoader takes a few minutes)
  4. Turn off the mods you don't want in Settings > MOD, then restart the game

  If "Windows protected your PC" appears, click "More info" > "Run anyway".
  You can install over an older version (also when FPRarity / FPSurvival / FPOptimize were installed separately).
  Your mod data and the switches in Settings > MOD are kept.
  A first install (also after removing the mods with Uninstall.bat) starts with all mods ON.


3. Uninstall
------------
  To stop using only some mods
    Turn them off in Settings > MOD and restart the game (no files need to be deleted)

  Uninstall.bat
    Removes all mods. Keeps the mod data and MelonLoader. "Install.bat" brings everything back
    The switches in Settings > MOD are reset, so installing again starts with all mods ON

  Uninstall (also MelonLoader).bat
    Also removes MelonLoader = fully vanilla game.
    MelonLoader is kept (so other mods keep working) when:
      - other mods or plugins are still in the Mods or Plugins folder
      - MelonLoader was there before these mods (it was not installed by Install.bat)
    MelonLoader lives only in this game's folder, so other games are never affected

  Neither batch changes your game save. The mod data (in UserData) is kept too, so installing again
  with "Install.bat" brings back your rarities, mastery, attachments and so on.

  While the mods are removed
    - Weapons with a rarity can still be used, with vanilla stats and rarity shown
      (the rarity is kept in the mod data and comes back when you install again)
    - Extended magazines go back to their normal size; the extra rounds can't be used while removed
    - Attachments are not shown and do nothing (they come back when you install again)
    - Broken Weapon Parts are a normal game item, so you keep them
  If you quit during a survival run and your stored items are not returned yet, the batch stops.
  Start the game with the survival mode ON, enter your room to get your items back, then uninstall.

  Manual removal
    Delete FPModOption.dll, FPRarity.dll, FPSurvival.dll, FPCustom.dll and FPOptimize.dll from <game folder>\Mods\.
    (Optional) also delete the FPModOption, FPRarity, FPSurvival, FPCustom and FPOptimize folders in <game folder>\UserData\.
    This also deletes the mod data (rarities, mastery, attachments, survival record, settings)


4. When the game is updated
---------------------------
  - The mods check the game build at every start
    - Untested build: the mods run, and "MOD: running on an untested game version" is shown in your room
    - Some features no longer fit: only those features stop, with a notice in your room
    - A critical part (e.g. blocking leaderboard submission) no longer fits: ALL mods stop automatically and
      the game runs as vanilla ("MOD: turned off - not compatible with this game update"). The mod data is kept
  - When the game build changes, the mod data files are backed up automatically
  - If the game does not start or crashes right away
    1. Run "Uninstall (also MelonLoader).bat" (the mod data is kept)
    2. If it still fails, use Steam's "Verify integrity of game files"
    3. When a mod version for the new game build is out, install it with its "Install.bat" (your data comes back)
  - Right after a game update, it is safest to play without the mods or wait for an updated version


5. Notes
--------
  - Quit the game before installing, uninstalling, or after switching mods ON/OFF (applied on restart)
  - Always quit the game from the menu
  - Do not report bugs to the game developer while the mods are installed.
    Remove the mods and check that the bug still happens first
  - Weapons obtained while the rarity mod was OFF keep the game's own stats when you turn it on again
    (weapons that were only moved between stash and inventory get their rarity back)
  - Turning the customization mod OFF removes the attachment visuals and effects and the slide changes
    (unlocks and loadouts are kept). Extra rounds from extended mags are deleted
  - Turning the survival mode mod OFF keeps the items stored by an unfinished run. Turn it on and enter your room to get them back


6. Troubleshooting
------------------
  - Save backups: Documents\FPMods_backup\<date>. Restore the LocalLow folder AND PlayerPrefs.reg together
  - Log: <game folder>\UserData\FPModOption\fpmod.log (optimization mod: UserData\FPOptimize\fpoptimize.log)
  - Steam's "Verify integrity of game files" restores the game files


7. Credits
----------
  - MOD: fan project (made with the help of an AI, Claude)
  - 3D models (free Sketchfab models, CC BY 4.0, modified). Authors and links:
      UserData\FPRarity\models\CREDITS.txt (sight rails and mounts)
      UserData\FPCustom\models\CREDITS.txt (handguards, muzzles, grips, bipod)
  - Some models are edited versions of the game's own models
  - All other sights, attachments and weapons use the game's own assets
