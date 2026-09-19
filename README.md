# Snow Fixer

*Make the snow in your Skyrim look the way it was meant to.*

Rocks with weird dark tinting in the snow? Footsteps that sound like dirt on a snowy path?
Glaciers wearing a second coat of snow? Snow Fixer goes through your load order, finds every
snow-related mesh and record, and fixes them in a single plugin. Your original files are never
touched, and you can remove the plugin any time.

Works with **Skyrim Special Edition and Legendary Edition**, with Mod Organizer 2, Vortex or a plain
Data folder.

## What it does

- **Finds the snow** — scans every record whose EditorID or mesh path mentions snow and makes a
  patched copy of the winning mesh, right next to the original.
- **Cleans up the tinting** — removes baked-in vertex color tinting on terrain (LAND) records and on
  landscape-folder meshes (rocks, cliffs, icebergs, ...), either only where a snow-classified
  texture is involved or across the board.
- **Fixes distant snow** — corrects ZBuffer-write / no-fade shader flags on the generated meshes so
  they render correctly at a distance.
- **Makes footsteps match** — remaps Dirt/Grass collision materials on the generated snow meshes to
  Snow, per collision chunk, so unrelated materials in the same mesh are left alone.
- **Snowy dirt cliff roots** — generates a snow-covered DirtCliffsRoots texture from the roots
  texture's alpha and the game's own snow texture (Vanilla / Complex Material / True PBR aware, with
  a matching PBRNifPatcher json when needed) and applies it to exactly the right part ("Skirt") of
  the generated DirtCliffs meshes.

### Optional extras (off by default)

- **Hide Decal Shapes** — stops decal-based dynamic snow shaders (Simplicity of Snow, BDS3, ...)
  from z-fighting with small rock overlays on mountain/rock/tundra meshes. The true decal shape
  ("Rocks01"/"SnowRocks01" with an alpha property) is hidden rather than deleted, and its non-alpha
  companion shape is retextured to the vanilla snow texture, so no hole is left behind. No plugin
  changes are needed.
- **Ice Snow Material** — clears the `SnowMaterialGlacier` / `SnowMaterialGlacierSlab` Material
  Object link on every static that uses it, so no projected snow covers glaciers and ice. Only
  `SnowFixer.esp` changes; the ice's own shader material is left alone.
- **MountainSlab Mask Swap** — for a record whose EditorID ends in "Snow"/"SN", uses the
  "...Mask" version of MountainSlab01/02 when a texture pack ships one.

Also included: mesh and EditorID keyword blacklists (wildcards supported, with sensible defaults for
new installs) and config profiles to save/load the whole launcher setup as a JSON file — handy if
several modlists share one install.

### Made to be safe

- Only **active** plugins are scanned, so a disabled mod never ends up as a master of the output.
- Output Location can't be the game's own Data folder, and an existing output folder is only wiped
  when it's provably Snow Fixer's own previous output.
- Malformed asset paths are reported and skipped per record; paths with non-ASCII characters
  (e.g. Cyrillic) are supported.
- Runs fully offline against your files — it never touches the running game.

## Two native tools, one patch engine

The actual scanning/patching logic lives in **`src/SnowFixer.Core`** (C#, built on
[Mutagen](https://github.com/Mutagen-Modding/Mutagen) and
[niflysharp](https://github.com/Aetherinox/niflysharp)). It's driven by:

- **`native/SnowFixer`** — a native wxWidgets shell (CMake + vcpkg + wxWidgets), calling into
  `SnowFixer.Core` through a [DNNE](https://github.com/dotnet/dnne)-exported
  `src/SnowFixer.NativeExport` assembly.
- **`native/SnowFixerTexTools`** — a small native library on top of
  [DirectXTex](https://github.com/microsoft/DirectXTex) that composites and recompresses the
  DirtCliffsRoots snow variant texture (GPU-accelerated with a CPU fallback).

## Installation

1. Install it like any other mod (MO2: as a regular mod; Vortex: extract into a mod folder).
2. Launch it from your mod manager's tool list.
3. Pick your game (SE or LE), point it at your game folder (and your MO2 instance/profile if you use
   MO2), and choose an **empty, dedicated** output folder.
4. Hit Start, then enable the generated output plugin.

Changed your load order? Just run it again — it always starts fresh from what's installed.

Like other patchers (AutoSeasons, AutoBlend, PGPatcher, DynDOLOD, ...), Snow Fixer only looks at what
exists when you run it: if you change something, regenerate everything that comes after it. Suggested
order: Snow Fixer → AutoSeasons → AutoBlend → PGPatcher → DynDOLOD (skip the ones you don't use).

## Building from source

Requirements:
- Windows, Visual Studio 2022 (MSVC toolchain) or the standalone Build Tools
- [vcpkg](https://github.com/microsoft/vcpkg) (manifest mode; dependencies are pulled automatically)
- .NET 9 SDK (for the Mutagen/niflysharp-based patch backend)
- CMake 3.31+, Ninja

```bash
git clone https://github.com/Cl3mus33/SnowFixer.git
cd SnowFixer/native
cmake -B buildRelease -S . -G Ninja -DCMAKE_TOOLCHAIN_FILE=<path-to-vcpkg>/scripts/buildsystems/vcpkg.cmake -DCMAKE_BUILD_TYPE=Release
cmake --build buildRelease --config Release
```

`src/SnowFixer.Core` and `src/SnowFixer.NativeExport` are built and published automatically as
part of this same CMake build (via DNNE) — no separate `dotnet build` step is needed. The built
executable and its runtime dependencies (`SnowFixer_dotnetlib/`, the self-contained .NET runtime)
end up in `native/buildRelease/bin/`.

**Deploying as an MO2 tool**: the executable must be copied into a mod folder MO2 already knows
about (a plain, unregistered folder under `mods/` won't participate in MO2's virtual filesystem
merge, which `SnowFixer_dotnetlib` resolution depends on).

## Credits

- Native shell architecture (wxWidgets + DNNE-bridged .NET patch backend) shares its lineage with
  [AutoBlend](https://github.com/Cl3mus33/AutoBlend), itself adapted from
  [AutoSeasons](https://github.com/Kesta-Dev/AutoSeasons), which is built on
  [PGPatcher](https://github.com/hakasapl/PGPatcher) by hakasapl.
- [nifly](https://github.com/ousnius/nifly) by ousnius, and its C# binding
  [niflysharp](https://github.com/Aetherinox/niflysharp), for NIF file handling.
- [Mutagen](https://github.com/Mutagen-Modding/Mutagen) for reading and writing Bethesda plugin
  files.
- [DirectXTex](https://github.com/microsoft/DirectXTex) for texture compositing/compression.
- The alpha-blending concept this whole family of tools (and AutoBlend before it) is built around
  traces back to the Majestic Landscapes modding standard; the "Hide Decal Shapes" feature is
  modeled on [Enhanced Rocks and Mountains - Blending Patch And Other
  Fixes](https://www.nexusmods.com/skyrimspecialedition/mods/131170), whose own "no decal" approach
  (needed to avoid z-fighting with decal-based dynamic snow shaders like Simplicity of Snow/BDS3)
  inspired this simpler, mesh-only variant.
- Thanks to [ra2phoenix](https://www.nexusmods.com/profile/ra2phoenix) for their contributions and
  ideas toward fixing snow-related visual issues.

## License

GPLv3 — see [LICENSE](LICENSE). `native/` shares its architecture with AutoBlend/AutoSeasons,
themselves derived from PGPatcher, all GPLv3-licensed.
