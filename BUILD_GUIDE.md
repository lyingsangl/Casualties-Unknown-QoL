# Build Guide

How to rebuild every DLL in this repository from source, so that a reviewer can verify the published binaries match the code.

---

## 1. Requirements

| Tool | Version | Notes |
|---|---|---|
| .NET SDK | 8.0 or newer | projects target `netstandard2.1` |
| Casualties: Unknown | 1.0.2 | Steam build; needed only for reference assemblies |
| BepInEx | 5.4.x **x64** | must be installed in the game folder |
| CUCoreLib | 1.1 | dependency of 5 mods; install into the game |
| (optional) RshLib | any | **not** required — `RshLibCompat` replaces it |

There is no NuGet restore step. All references are resolved from the game installation, so nothing is downloaded during build.

---

## 2. Point the build at your game folder

Every `.csproj` reads one MSBuild property, `GameDir`, defined in `Directory.Build.props`.

**Option A — environment variable (recommended)**

```bat
setx CASUALTIES_DIR "D:\Steam\steamapps\common\Casualties Unknown"
```

Open a new terminal afterwards so the variable is picked up.

```bash
# Linux / macOS, for reference
export CASUALTIES_DIR="$HOME/.steam/steam/steamapps/common/Casualties Unknown"
```

**Option B — per-command**

```bat
dotnet build Mods\HealthBarLite\HealthBarLite.csproj -c Release -p:GameDir="D:\Steam\steamapps\common\Casualties Unknown"
```

If `GameDir` is missing or `BepInEx.dll` cannot be found under it, the build stops with an explicit message instead of failing later with a confusing reference error.

---

## 3. Build everything

From the repository root:

```bat
for /d %d in (Mods\*) do dotnet build "Mods\%~nxd\%~nxd.csproj" -c Release
```

Or build a single mod:

```bat
dotnet build Mods\DietDrug\DietDrug.csproj -c Release
```

Output:

```
Mods/<Mod>/bin/Release/<Mod>.dll
```

Deploy by copying that DLL into:

```
<GameDir>\BepInEx\plugins\<Mod>\<Mod>.dll
```

---

## 4. Reference assemblies used

All references come from the player's installation. None of these files are redistributed by this repository.

| Reference | Path |
|---|---|
| `BepInEx.dll` | `$(GameDir)/BepInEx/core/BepInEx.dll` |
| `0Harmony.dll` | `$(GameDir)/BepInEx/core/0Harmony.dll` |
| `Assembly-CSharp.dll` | `$(GameDir)/CasualtiesUnknown_Data/Managed/Assembly-CSharp.dll` |
| `UnityEngine*.dll` | `$(GameDir)/CasualtiesUnknown_Data/Managed/` |
| `CUCoreLib.dll` | `$(GameDir)/BepInEx/plugins/CUCoreLib/CUCoreLib.dll` |

Every reference is marked `<Private>false</Private>`, so no game or dependency DLL is ever copied into the output folder.

### Which mods need CUCoreLib

| Mod | Uses CUCoreLib |
|---|---|
| HealthBarLite | yes (options menu) |
| MiniMapLite | no |
| CrosshairLite | yes (options menu) |
| SerumCN | yes (item registry access) |
| RshLibCompat | no |
| CuGuard | no |
| TraderTweak | no |
| DietDrug | yes (recipe / liquid / locale registry) |
| IlmeniteExtraction | yes (recipe registry) |

---

## 5. Verification

`DebugType` is set to `none` for release builds, so DLLs are small and contain no debug symbols. To confirm a published DLL came from this source:

1. Build with the same SDK major version.
2. Compare file size and a hash:

```bat
certutil -hashfile Mods\DietDrug\bin\Release\DietDrug.dll SHA256
```

Byte-for-byte reproduction is not guaranteed across SDK patch versions, but the public API surface and behaviour are identical. If a published hash does not match your local build, rebuild from the corresponding git tag and diff the IL with `ildasm` or a decompiler.

---

## 6. Common problems

| Symptom | Cause |
|---|---|
| `GameDir is not set` | environment variable missing, or terminal not restarted |
| `BepInEx.dll was not found` | game path wrong, or BepInEx not installed in that folder |
| `未能找到类型或命名空间名 CUCoreLib` | CUCoreLib 1.1 not installed in the game folder |
| Mod loads but nothing happens in game | check `BepInEx/LogOutput.log`; some mods wait for the world to load before registering content |

---

## 7. Installer scripts

`Installer/Source/*.py` are plain Python 3 helpers used on the author's machine. They only touch the game's `BepInEx` folder:

- `mods_toggle.py` — renames `*.dll` to `*.dll.off` (and back) to enable or disable plugins;
- `install_mods.py` — copies plugin folders from a staging directory into `BepInEx/plugins`, with a backup/restore step.

Neither script makes network requests, collects data, or writes outside `BepInEx`. A third helper that automated downloading from Nexus Mods is intentionally **not** published, since automated downloading is not something we want to distribute and players are expected to download third-party mods manually. See `Installer/README.md`.
