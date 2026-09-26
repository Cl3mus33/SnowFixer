# Changelog

All notable changes to this project are documented here. Format loosely follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

## [1.2.10] - 2026-09-26

### Fixed
- **Non-English names in `SnowFixer.esp` were still unreadable in xEdit on Skyrim SE** - reported on Nexus
  right after v1.2.9 (Russian install, Vortex): the `?` characters were gone but xEdit showed the raw
  bytes with "No mapping for the Unicode character exists in the target multi-byte code page".
  v1.2.9 wrote Russian/Polish/Japanese/Chinese text in the language's legacy ANSI code page (cp1251
  for Russian), which is what Legendary Edition uses - but Skyrim SE stores every non-English language
  as UTF-8 (confirmed directly against vanilla: `dawnguard_russian.strings` and
  `dawnguard_french.strings` are 100% valid UTF-8, zero entries in a legacy code page; xEdit documents
  the same rule for SSE). Snow Fixer now writes the plugin with the encoding the game itself uses for
  the selected game version and language (UTF-8 on SE for every non-English language, the language's
  ANSI code page on LE). English is unchanged. Verified byte-for-byte on the real Russian "SEBench01"
  record: SE `D0 A1 D0 BA D0 B0 D0 BC D1 8C D1 8F`, LE `D1 EA E0 EC FC FF`.

## [1.2.9] - 2026-09-25

### Fixed
- **Non-Western text (Russian, Polish, Japanese, Chinese) came out as literal `?` characters in
  `SnowFixer.esp`** - the v1.2.7 language fix resolved the right string ("Скамья") but the plugin was
  still written in Mutagen's default Western (Windows-1252) encoding, which can't hold those
  scripts. The plugin is now written in the language's own ANSI code page (1251 for Russian, 1250
  Polish, 932 Japanese, 936 Chinese), the same one Russian/Polish plugins normally ship with and that
  xEdit reads back correctly. Western languages are unchanged.
- **`SnowFixer.esp` no longer contains ITM (Identical To Master) records** - when a record's
  EditorID is its mesh's own filename (e.g. `DirtCliffs01` -> `DirtCliffs01.nif`), the duplicate
  mesh lands on the original path and the record's Model.File comes out unchanged. The mesh is still
  updated in place; the pointless override record is now dropped (179 of them on a real modlist).

## [1.2.8] - 2026-09-22

### Changed
- **A previous run's own `SnowFixer.esp` still being active now refuses the run outright instead of
  silently excluding it and continuing** - matches AutoBlend/AutoSeasons' own identical guard for the
  same underlying problem (their own output plugin compounding on itself run after run). The v1.2.7
  fix already excluded it from the source scan automatically; this is stricter - it stops the run
  before anything is touched (so a stale prior output is never even at risk of being wiped while
  refusing) and tells you to disable "Snow Fixer Output" in your mod manager first, then re-enable it
  once the new run has finished. The v1.2.7 exclusion still applies underneath as a fallback for the
  rarer case where the output folder was moved/cleared without disabling the plugin.


## [1.2.7] - 2026-09-22

### Fixed
- **`SnowFixer.esp` overrode some records with plain English text instead of the user's own
  localized strings** - reported on Nexus (Russian install, Dawnguard's own "SEBench01" bench).
  Root-caused directly against the real modlist to three separate, compounding causes:
  - Nothing ever told Mutagen which language to resolve localized text in, so it silently defaulted
    to English regardless of the player's actual game language. Snow Fixer now reads `sLanguage` from
    the MO2 profile's own Skyrim.ini (falling back to the real game's Documents one) and resolves
    strings in that language.
  - Under Mod Organizer 2, the load order Mutagen builds from is materialized into a bare temp folder
    holding only the active plugins themselves - no Strings/ folder, no BSAs - so vanilla/DLC/CC's own
    localized text (packed in `Skyrim - Interface.bsa`) could never be found at all. Mutagen's own BSA
    lookup is now pointed at the real Data folder instead.
  - **The actual trigger**: a previous run's own `SnowFixer.esp`, left active in the load order (the
    normal state after using the tool once), broke localized-string resolution for the ENTIRE
    environment the moment it was present - not just its own records, every other plugin's localized
    text came back blank too, confirmed by bisecting a real ~65-plugin load order down to that one
    plugin. Snow Fixer already wipes and regenerates its own output from scratch every run, so a stale
    prior copy was never a real source to scan to begin with - it's now excluded from the load order
    Mutagen builds, with a diagnostic when this happens.


## [1.2.6] - 2026-09-22

### Fixed
- **A mod's own BSA/BA2 archive silently failing to open no longer falls back to a lower-priority
  source (or vanilla) with zero indication anything went wrong** - reported on Nexus: with Skyrim 3D
  Rocks installed (ships `S3DRocks.bsa`/`S3DRocks - Textures.bsa`), every static reverted to its
  vanilla mesh in Snow Fixer's own output, with no error shown anywhere. Archive open/index failures
  were being caught and silently skipped in three places (`ArchiveAwareFileProbe` and
  `Mo2InstanceReader`, both the vanilla-Data-folder and the per-mod-archive paths) - a mod whose
  entire archive can't be read used to mean every file it would have provided fell through to
  whatever lower-priority source (often vanilla) provides instead, completely invisibly. Every one of
  those now adds a clear diagnostic naming the archive and the underlying error, instead of staying
  silent. Verified directly: a deliberately corrupted archive now surfaces "Archive '...' could not be
  opened and was skipped entirely..." in the run's own diagnostics.


## [1.2.5] - 2026-09-22

### Fixed
- **One plugin with a malformed Cell/Worldspace group no longer aborts an entire run** - reported on
  Nexus (`DeepSeek.esp`, `OverflowException`): finding any LAND record needs Mutagen to open every
  mod's own Cells/Worldspace group tree, even mods that never touch a single landscape record, so one
  plugin with a corrupted group header there took down landscape scanning across the whole load order
  - nothing was written at all, not even everything already scanned before that point. Landscape
  vertex color clearing is now skipped (with a diagnostic naming the plugin) instead, while every
  other pass already completed is unaffected.
- **Fixed a mesh shared between snow and non-snow uses via Alternate Textures being treated as snow
  purely because of its file name** - reported on Nexus: Snow Fixer applying a mismatched/wrong
  texture to meshes that should be ash or dirt. Confirmed directly against vanilla data:
  `DLC01AshDriftL01`-`L04` and `DLC02AshDuneVolcAsh01L01`/`L02` (Solstheim ash piles/dunes) reuse
  `Landscape\SnowDrifts\SnowDriftL0*.nif` with an Alternate Texture redirecting the diffuse to ash -
  matching "snow" purely by mesh path (with no EditorID signal) treated these as snow records, baking
  a duplicate and clearing/patching things a genuinely-ash record was never meant to have touched. A
  record whose EditorID gives no snow signal is no longer treated as snow when every one of its own
  Alternate Textures also resolves to a non-snow diffuse; a record with even one legitimately
  snow-targeted Alternate Texture (e.g. `SnowDriftL02_GlacierBlend`) is unaffected.


## [1.2.4] - 2026-09-21

### Fixed
- **Fixed the mouse wheel changing dropdown values instead of scrolling the launcher** - reported on Nexus
  ("Generate PBR slots" greyed out after updating; "the mouse wheel on this interface is so hard to use").
  Since the General tab became scrollable, a dropdown under the cursor swallowed the wheel and changed
  its own selection - reproduced directly: one notch over Game Type flipped Special Edition to Legendary
  Edition, which greys out and unchecks "Generate PBR slots".
  Dropdowns in the General tab now hand the wheel to the panel (changing a value still works by
  clicking the dropdown), and each notch scrolls further.


## [1.2.3] - 2026-09-20

### Fixed
- **One empty or corrupted plugin in the load order no longer aborts the whole run** - reported on Nexus
  (Snow Fixer, `Merethic Grass Catche.esp`): Mutagen threw "Could not read enough data to parse a Mod
  Header from stream. Position: 0. 0 remaining < 24 expected." while loading the game environment, so
  nothing was scanned at all. Reproduced directly with a 0-byte plugin in a synthetic MO2 instance.
  Snow Fixer now skips the unreadable plugin, notes which one in the run's diagnostics/warnings, and
  carries on with the rest of the load order.

## [1.2.2] - 2026-09-19

### Fixed
- **MO2 Instance Path pointing at a folder that merely contains the instance no longer fails with a bare error** - reported on Nexus: with
  `MO2 Instance Path` set to the MO2 folder (parent of the actual instance/base directory), the
  MO2 Profile dropdown stayed empty, the run silently fell back to a profile named "Default", and
  ended with "No modlist.txt found for profile...". If the given folder isn't an instance but exactly
  one instance can be found inside it (a subfolder with profiles, or a global instance whose
  base_directory lives there), it's now used automatically (and the profile list fills in); otherwise
  the error explains what an instance folder is, lists the profiles/instances found, and the launcher
  now asks you to pick an MO2 profile instead of running with none. The message is translated in all
  languages.

## [1.2.1] - 2026-09-19

### Fixed
- **`SnowFixer.esp` can no longer end up with a master that isn't part of the loaded setup** - reported
  on Nexus: with vertex colors set to "All" on landscapes, the plugin listed `NOTWL - Lanterns.esp` (a
  True Light patch from a mod the user had disabled) as a master. Reproduced the mechanism directly:
  each patched LAND record drags in its parent Cell/Worldspace records, copied whole with every link
  they hold, so a link into a plugin that isn't actually loaded turned that plugin into a master. Any
  reference to a plugin that is neither in the load order, nor in the game's Data folder (base game/CC),
  nor shipped by an enabled MO2 mod is now cleared before writing (those links are already dead
  in-game), and a diagnostic lists which plugins were involved. Verified against a real load order
  with two plugins declared unavailable: they disappear from the masters, and a normal run is
  unchanged.

## [1.2.0] - 2026-09-19

### Added
- **Ice Snow Material** - new opt-in option that removes the projected snow from glaciers and ice.
  For every static whose Direction Material (STAT.DNAM) points at `SnowMaterialGlacier` or
  `SnowMaterialGlacierSlab`, the override written to `SnowFixer.esp` clears that Material link. Only
  those two Material Objects are touched - the ice's own look (`IceShader01` and similar) is
  deliberately left alone, and no mesh is modified. Matched by Material Object only (not by
  mesh/EditorID keywords), so it also catches uses outside obvious ice names. Verified against real
  load orders: it clears every unique winning static using them (27 Slab + 19 Glacier in vanilla).

### Changed
- New installs now also start with `*\glaciers\*` in the default Mesh Blacklist and `ice`, `frozen`
  and `icicle` in the default EditorID Blacklist Keywords, so glacier/ice content is not duplicated
  and snow-fixed. Existing `settings.json` files keep their own lists - this only changes what a
  fresh install starts from.

## [1.0.11] - 2026-09-19

### Fixed
- **Fixed Snow Fixer failing on any path containing non-ASCII characters (e.g. Cyrillic)** - found via a Nexus report on the sibling tool AutoBlend.
  Reproduced directly: nifly can't open or write a NIF whose path has any non-ASCII character (its
  Windows path handling goes through a narrow-string API), so every mesh failed to load whenever the
  temp folder (a Cyrillic/accented Windows user name puts `%TEMP%` itself out of reach) or the output
  folder was non-ASCII. All NIF reads/writes now go through `NifIo`, which copies through a
  guaranteed-ASCII temp file when needed; where a temp folder is used, it falls back to
  `C:\ProgramData\SnowFixer\tmp` if the normal one isn't ASCII-only.

## [1.0.10] - 2026-09-18

### Fixed
- **Fixed Hide Decal Shapes creating holes in generated meshes** - found via own testing (NifSkope):
  a shape sharing the Rocks01/SnowRocks01 texture with a real decal shape isn't
  always a redundant duplicate - on meshes like vanilla RockCliff08, the second shape (":9") is real,
  load-bearing surface geometry, and hiding it alongside the actual decal (":8") left a visible gap.
  Confirmed by inspecting the reference mod's (Vanaheimr, ra2phoenix) own fixed mesh: it keeps that
  shape but retextures it to `landscape\snow01`, rather than hiding it. Detection is now split by
  `NiAlphaProperty`, which reliably tells the two apart - the actual decal shape (has the property) is
  still hidden exactly as before, while its non-alpha companion is retextured to the vanilla snow
  ground texture instead of touched at all. Verified directly against both vanilla RockCliff01 (no
  companion shape) and RockCliff08 (companion present) - only the real decal shape is ever hidden now.

## [1.0.9] - 2026-09-17

### Fixed
- **Fixed the launcher window being taller than the screen on smaller displays** - reported
  directly on Nexus: with every setting added since the "General" tab was first laid out (Config
  Profile, Game Type, MountainSlab Mask, DirtCliffsRoots Snow Variant, Hide Decal Shapes, ...), the
  dialog auto-sized itself to fit all of it, and on a smaller display that meant a window taller
  than the screen itself - with no way to reach the controls (or even the Start button) below the
  fold, since dragging the window's own edges can't make it bigger than the screen. The General tab
  now scrolls its own content instead of growing the whole dialog to fit it.

## [1.0.8] - 2026-09-17

### Fixed
- **Fixed disabled plugins being included as masters when Mod Manager is None/Vortex** - reported
  directly on Nexus: a plugin disabled in the user's own plugins.txt still got treated as a
  winning-override source and ended up as a master of `SnowFixer.esp` (also the root cause of
  xEdit's own "Modules with extended FormID range should always have the Game Master as their
  first master" warning on the generated ESL). Without an explicit load order, Mutagen's own
  auto-detection imports every plugin physically present in the Data folder regardless of its real
  enabled state - the real plugins.txt is now read first and only its actually-enabled entries (plus
  the game's own implicit base masters) are passed through explicitly. Same fix AutoBlend already
  had for this identical gap.
- **Fixed textures generated by the DirtCliffsRoots Snow Variant option being unreadable on Skyrim
  Legendary Edition** - `DirtCliffsSnowVariantGenerator`'s native texture compositing always
  recompressed to BC7, which needs a DDS header format LE's engine can't read at all (see
  AutoBlend's own identical fix for the full write-up); now falls back to BC3 (LE's own native
  format) when Game Type is Legendary Edition.

## [1.0.7] - 2026-09-17

### Added
- **Hide Decal Shapes** - new opt-in option that hides (rather than deletes) any shape textured
  with "Rocks01" or "SnowRocks01" on a landscape\mountains\, landscape\rocks\, or
  landscape\tundra\ mesh, to avoid z-fighting with Skyrim's own decal-based dynamic snow shaders
  (Simplicity of Snow, BDS3, ...). Detection is by diffuse texture name, not the NIF's own
  SLSF1_Decal shader flag - that flag doesn't reliably mark every shape that needs hiding
  (confirmed empirically: vanilla RockCliff08 has two shapes both textured "Rocks01" - an opaque
  base pass and a decal-flagged pass on top of it - and only the second carries the flag, yet both
  need hiding to avoid leaving the base pass behind as pointless duplicate geometry). DirtCliffs
  meshes are NOT touched by this - their own "Skirt" shape must stay, since that's what
  DirtCliffsRoots Snow Variant retextures for snow instead. Verified directly against vanilla
  meshes extracted from the base game's own archives. Modeled on [Enhanced Rocks and Mountains -
  Blending Patch And Other Fixes](https://www.nexusmods.com/skyrimspecialedition/mods/131170),
  whose own fix for this
  edits the plugin's Alternate Textures instead - this needs no plugin changes at all, since every
  shape stays in the mesh (just hidden) and block indices never move.

### Fixed
- **Fixed reading meshes/textures from compressed Skyrim Legendary Edition archives** - a real bug
  in the pinned Mutagen.Bethesda 0.54.4 itself: opening a compressed file entry from an LE-format
  (BSA v103) archive threw `ArchiveException: "InflaterInputStream Length is not supported"` from
  deep inside Mutagen's own BSA reader, silently skipping every affected file. Both `Skyrim -
  Textures.bsa` and `Skyrim - Meshes.bsa` (vanilla LE) are compressed this way, so almost nothing
  from the base game was ever actually readable on LE until now - reads now fall back to manually
  decompressing the entry (see `ManualArchiveExtractor`) when this specific error occurs. Special
  Edition's own archives are unaffected (confirmed empirically) and take the normal path exactly
  as before.
- **Fixed a real risk of Snow Fixer deleting a user's actual mod files** - reported directly on
  Nexus: pointing Output Location at the game's own Data folder (instead of an empty, dedicated
  output folder) caused the "wipe previous run's own output before regenerating" step to delete the
  user's entire real `meshes\` folder - every mod's own meshes (skeletons, animation replacers, ...)
  gone. Two guards now: Output Location can no longer be set to the game's own Data folder at all
  (immediate, specific error), and more generally, an existing `meshes\` folder under Output
  Location is only ever wiped if a `SnowFixer.esp`/`SnowFixer-log.txt` from a previous run is
  already there to prove it's actually this tool's own prior output - any other folder is left
  alone, with a clear error explaining why, instead of being silently deleted.

## [1.0.6] - 2026-09-15

### Added
- **Skyrim Legendary Edition support** - new "Game Type" setting in the launcher, alongside SE.
  Verified end to end against a real LE MO2 modlist.
- **Config Profile** - "Load Config..."/"Save Config As..." buttons let you save/load the whole
  launcher configuration as a standalone JSON file, instead of the single shared
  `%APPDATA%\SnowFixer\settings.json`. Requested directly after noticing that several modlists
  sharing one install all fight over that one file - now each can keep its own saved config
  instead. Mirrors AutoBlend's own identical feature.

### Fixed
- Fixed `plugins.txt` parsing for LE under MO2: LE's `plugins.txt` has no `*` active-marker prefix
  at all (every listed plugin is simply active) - Skyrim SE marks active plugins with a leading
  `*`. Using the SE-only check unconditionally meant every mod-added plugin was invisible to Snow
  Fixer on a real LE profile, only the hardcoded implicit base masters remaining. Found and
  verified directly against a real Skyrim LE MO2 instance.
- Fixed the ESL auto-flag (added in 1.0.5) being applied on Skyrim LE runs - ESL/light plugins are
  an SE-only engine feature, and Mutagen's own `CanBeSmallMaster` check has no awareness of that,
  so an LE run would have produced a plugin LE can't actually load correctly. Only applies on SE
  now.

### Changed
- The MountainSlab Mask Swap option is now disabled (and its label/help text greyed out) whenever
  Game Type is Legendary Edition: both `MountainSlab01`/`02` and their `...Mask` sibling are
  Skyrim SE's own vanilla landscape assets, never shipped under any esm/esp on LE.

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
