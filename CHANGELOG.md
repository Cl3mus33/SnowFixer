# Changelog

All notable changes to this project are documented here. Format loosely follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

## [1.0.5] - 2026-09-15

### Added
- The output plugin is now automatically flagged as ESL (Light) whenever it fits that format's own
  new-record range - requested directly on Nexus (StrayHALOMAN). Falls back to a regular ESP with a
  diagnostic message if a run's own new records exceed the ESL limit.

## [1.0.4] - 2026-09-14

### Fixed
- **Fixed a crash when one of the user's installed mods ships a corrupt/malformed BSA** - reported
  directly on Nexus (Carlotii): `System.OverflowException` from deep inside Mutagen's own
  `BsaReader.LoadFolderRecords`, aborting the whole run. `Mo2InstanceReader.GetOrBuildArchiveIndex`
  accessed each mod archive's file list with no exception handling, unlike `ArchiveAwareFileProbe`'s
  already-guarded equivalent for the vanilla Data folder's own archives (v1.0.1). Same fix: keep
  whatever was indexed before the failure rather than losing the whole run over one bad archive.

## [1.0.3] - 2026-09-13

### Added
- Turkish (`tr`) GUI translation, contributed by burako54 via Nexus.

## [1.0.2] - 2026-09-13

### Changed
- New installs now ship with a much more complete default Mesh Blacklist (`*\trees\*`, `*\ice\*`,
  `*\effects\*`, `*\weapons\*`, `*\trophy\*`, `*\snowelfruins\*`, `*\wet\*`, `*\lod\*`, `*\clutter\*`,
  `*\architecture\*`, `*\dungeons\*`) and default EditorID Blacklist Keywords (`marker`, `glacier`,
  `lod`), refined through real use against a real modlist instead of starting from an almost-empty
  list. Existing `settings.json` files with your own rules are untouched - this only changes what a
  fresh install starts from.

## [1.0.1] - 2026-09-13

### Fixed
- **Fixed a crash on startup under Mod Organizer 2** when the game's Data folder contains archives
  whose names collide in a way Mutagen's own internal sort doesn't handle - reported directly on
  Nexus (AzEx1t), full stack trace pointed at
  `Mutagen.Bethesda.Archives.DI.CachedArchiveListingDetailsProvider.Comparer.Compare` throwing
  `NotImplementedException`. Confirmed this is the same bug AutoBlend had already hit and fixed:
  `Archive.GetApplicableArchivePaths` sorts every matching archive by a priority comparer that
  can't handle two archives whose names collapse to the same base+suffix pair after stripping a
  " - Suffix" segment (commonly Creation Club content). `ArchiveAwareFileProbe` doesn't actually
  need that sort order - it only needs to know which archives exist at all - so it now falls back
  to a plain, unsorted directory listing when the sorted call throws, ported directly from
  AutoBlend's own fix. Also hardened the same code path against a single corrupt/unreadable
  archive aborting the whole run (try/catch around both `Archive.CreateReader` and each archive's
  own file-list enumeration), matching AutoBlend's existing resilience.

## [1.0.0] - 2026-09-13

First stable release - no longer WIP.

### Added
- **MountainSlab Mask Swap** - for a record whose EditorID ends in "Snow"/"SN", repoints any shape
  using the MountainSlab01/02 texture to its "...Mask" sibling when a texture pack ships one on
  disk, so rock/mountain meshes read correctly under a snow overlay. Opt-in, off by default.

### Fixed
- **Fixed orphaned `BSShaderTextureSet` blocks left behind in generated meshes** - reported
  directly via a NifSkope screenshot showing a dimmed, disconnected block. Both the DirtCliffs
  Skirt retexture and the new MountainSlab Mask Swap clone a shape's shared texture set into a
  private copy before editing it (so sibling shapes aren't affected), but neither ever deleted the
  original block once nothing referenced it anymore. Both now call
  `NiHeader.DeleteBlockByType("BSShaderTextureSet", true)` before saving, which only removes
  texture sets no shape still points to.

## [0.2.0] - 2026-09-05

### Added
- Mod Organizer 2 support (instance + profile aware), alongside plain Data-folder scanning for
  Vortex/no manager - contributed via community pull request (theBigOunce/mkerv).
- Malformed/misaligned asset paths are now reported and skipped per record instead of aborting the
  whole run; fatal run errors include detailed exception information in the progress window.

## [0.1.0] - 2026-08-31

Initial WIP release.

### Added
- Scans a load order for every base record with a NIF model whose EditorID or mesh path mentions
  snow, duplicates each winning mesh, and writes a plugin pointing at the duplicates.
- Landscape and mesh vertex color clearing (all / snow-only / off).
- Shader flag fixes for correct distance rendering.
- Collision material remapping (Dirt/Grass → Snow) so footstep sounds match the snowy visual.
- DirtCliffsRoots snow variant texture generation, aware of Vanilla / Complex Material / True PBR
  conventions, applied to the "Skirt" shape of generated DirtCliffs meshes.
- Mesh blacklist and EditorID keyword blacklist, both with wildcard support.
- 6 languages: English, French, German, Spanish, Italian, Portuguese (Brazil).
