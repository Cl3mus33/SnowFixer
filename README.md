# Snow Fixer

A standalone tool for Skyrim Special Edition that scans your load order for every snow-related mesh
and record, duplicates and patches the winning ones, and writes a single plugin pointing at the
fixes — without ever touching an original asset.

## What it does

Point Snow Fixer at your load order (via Mod Organizer 2, or a plain Data folder) and it will:

- Scan every base record with a NIF model whose EditorID or mesh path mentions snow, and duplicate
  each winning mesh right alongside the original.
- **Vertex colors** — clear baked-in vertex color tinting on terrain (LAND) records and on
  landscape-folder meshes (rocks, cliffs, icebergs, ...), either only where a snow-classified
  texture is involved or across the board.
- **Shader flags** — fix ZBuffer-write / no-fade flags on the generated meshes so they render
  correctly at a distance.
- **Collision materials** — remap collision materials on the generated snow meshes toward their
  snow equivalent (currently Dirt/Grass → Snow), so footstep sounds match the snowy visual.
- **DirtCliffsRoots snow variant** — generate a snow-covered version of the dirt cliff roots
  texture from the alpha channel of the roots texture and the diffuse/normal/etc. of the game's
  own snow texture, aware of the Vanilla / Complex Material / True PBR conventions (correct
  compression format + a matching PBRNifPatcher json when needed), and apply it to exactly the
  right part ("Skirt") of the generated DirtCliffs meshes.
- **MountainSlab Mask Swap** — for a record whose EditorID ends in "Snow"/"SN", repoint any shape
  using the MountainSlab01/02 texture to its "...Mask" sibling when a texture pack ships one, so
  rock/mountain meshes read correctly under a snow overlay.
- **Mesh blacklist / EditorID keyword blacklist** — exclude specific meshes (wildcards supported)
  or any record whose EditorID contains a given keyword, to rule out false-positive matches.
- **Error handling** — malformed asset paths are reported and skipped per record where possible;
  fatal run errors include detailed exception information in the progress window.
- Run fully offline against your files — it never touches the running game.

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

1. Download the latest release and install it like any other mod (MO2: as a regular mod).
2. Point it at your game install, your mod manager (if any), and an output folder. When using MO2,
   choose the instance and the profile whose `modlist.txt`/`plugins.txt` should be scanned.
3. Run it, then enable the generated output plugin in your mod manager.

Supports Skyrim Special Edition.

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

## License

GPLv3 — see [LICENSE](LICENSE). `native/` shares its architecture with AutoBlend/AutoSeasons,
themselves derived from PGPatcher, all GPLv3-licensed.
