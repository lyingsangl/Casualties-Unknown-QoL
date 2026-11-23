# Installer / tooling scripts

Small Python 3 helpers used while building and testing the pack. They run locally and are included here for transparency, not because players need them.

## `mods_toggle.py`

Enables or disables plugins by renaming files inside `BepInEx/plugins`:

- `SomeMod.dll` → `SomeMod.dll.off` disables it
- `SomeMod.dll.off` → `SomeMod.dll` enables it

Keeps a `mods.txt` list so the same set can be restored later. Pure filesystem operations — no network access, no registry changes, nothing written outside the game's `BepInEx` folder.

## `install_mods.py`

Copies plugin folders from a staging directory into `BepInEx/plugins`, and can restore a backup of the previous state. Local file operations only.

## What is deliberately **not** included

The author's own convenience script that automated browsing and downloading mod archives from Nexus Mods is not published here. Automatic downloading from the site is not something we want to encourage or distribute, and every third-party mod in the pack is meant to be downloaded manually by the player from its original page.

This is also why the released package contains no third-party binaries: players obtain those themselves, from the authors.

## Requirements

Python 3.9+, no third-party packages.
