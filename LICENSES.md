# Licences and third-party references

## This repository

The source code in `Mods/` and `Installer/` is original work by **lyingsangl** and is published so that the released binaries can be reviewed and rebuilt.

You may read, build and modify it for personal use. Redistribution of compiled builds or modified versions requires permission — the released pack is distributed through its own project page and nowhere else.

## Referenced assemblies (not redistributed)

Every project references these DLLs from the player's own installation. **None of them are included in this repository or in the released package.**

| Assembly | Origin | Purpose of reference |
|---|---|---|
| `BepInEx.dll`, `0Harmony.dll` | BepInEx 5.4.x (LGPL-2.1) | plugin loading, Harmony patching |
| `Assembly-CSharp.dll` | Casualties: Unknown, © its developers | game types used by every mod |
| `UnityEngine*.dll` | Unity Technologies | engine types |
| `CUCoreLib.dll` | CUCoreLib 1.1 by **JimmyKing** | item / recipe / liquid registration and the in-game options menu |

No code from these projects is copied, decompiled or re-shipped here. Where a mod has to reach into game internals it does so at runtime through reflection, against the public API surface of the assembly the player already owns.

## RshLibCompat

`Mods/RshLibCompat` is a **reimplementation**, not a copy of RshLib. It exists because the original library is incompatible with game 1.0.2 and gun packs depend on it.

- It declares the same assembly name and plugin GUID so dependent mods resolve it, which is the documented way to supply a drop-in replacement.
- The implementation is written from scratch; no decompiled source from the original was copied into this repository.
- It does not attempt to replace, patch or circumvent any paid content, DRM or licensing check.

If the original author objects to a compatibility build existing, we will rename the assembly and coordinate with them instead — the goal is only to keep their mods working on the current game version.

## Third-party mods mentioned in documentation

The released pack does not bundle third-party mods. Names such as ContainerTweaks, Auto Sort, New Gun Loader, Re-Growth Serums and Enhanced Tools appear in the documentation only to tell players where to get them and to explain compatibility fixes. All rights remain with their respective authors.

## AI assistance disclosure

These mods were developed with AI assistance: the author drives the design, the gameplay values and the debugging, and uses an AI assistant to read the decompiled game API, work out formulas, and speed up implementation.

Concretely:

- design decisions, balance numbers and the final behaviour of every mod are the author's;
- the author can explain and modify any part of the code, and has done so repeatedly during debugging;
- game internals were studied by decompiling the locally installed `Assembly-CSharp.dll` for reference only — no game code is redistributed.

Development history is available in the repository's commit log.
