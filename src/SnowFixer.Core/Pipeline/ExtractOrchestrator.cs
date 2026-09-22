using Mutagen.Bethesda;
using Mutagen.Bethesda.Environments;
using Mutagen.Bethesda.Plugins.Exceptions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Strings;
using nifly;
using SnowFixer.Core.Configuration;
using SnowFixer.Core.Nif;
using SnowFixer.Core.Scanning;

namespace SnowFixer.Core.Pipeline;

/// <summary>
/// Standalone from AutoBlend. Scans the vanilla Skyrim Data folder (base game + official DLCs +
/// every installed Creation Club addon, on SE) or, when MO2 is selected, the active plugin load order
/// layered over that folder for every base record with a NIF model whose EditorID or mesh path
/// mentions "snow". It duplicates each winning mesh right alongside the original (its own
/// EditorID- or "_snow"-suffixed name is what keeps it from colliding with the original, not a
/// separate folder - see DuplicateMesh), and writes a single
/// ESP overriding those records' Model.File to point at the duplicates. Since each matched record
/// gets its own dedicated physical duplicate (never shared with another record), any pre-existing
/// Alternate Texture on that record is baked directly into the duplicate's own embedded texture
/// slot and then dropped from the override - unlike AutoBlend's own shared-mesh scenario, nothing
/// else could ever want a different texture on this exact file, so baking is always safe here.
/// Optionally also clears vertex colors on LAND (terrain) records - see
/// <see cref="Configuration.LandscapeVertexColorMode"/> - an entirely separate pass, since LAND
/// records have no NIF/Model.File at all.
/// </summary>
public sealed class ExtractOrchestrator
{
    // BSShaderTextureSet slot order - fixed across SE/AE, matches the TXST record's TX00-TX07
    // subrecord order 1:1 (same mapping AutoBlend.Core.Plugin.SourceTexturePaths uses).
    private static readonly Func<ITextureSetGetter, IEnumerable<(uint Slot, string? Path)>> TextureSetSlots = txst => new (uint, string?)[]
    {
        (0, txst.Diffuse?.GivenPath),
        (1, txst.NormalOrGloss?.GivenPath),
        (2, txst.GlowOrDetailMap?.GivenPath),
        (3, txst.Height?.GivenPath),
        (4, txst.Environment?.GivenPath),
        (5, txst.EnvironmentMaskOrSubsurfaceTint?.GivenPath),
        (6, txst.Multilayer?.GivenPath),
        (7, txst.BacklightMaskOrSpecular?.GivenPath),
    };

    // BSShaderProperty.shaderFlags2 (SLSF2) bits - matches AutoBlend.Core.Nif.BSShaderFlags2.
    private const uint ZBufferWriteBit = 0x1; // value 1, bit 0
    private const uint NoFadeBit = 0x8; // value 8, bit 3 - disables distance-based dithered fading

    // Diffuse texture file names HideDecalShapes hides shapes for - the generic small-rock detail
    // overlay Bethesda scatters across mountain/rock/tundra meshes (both plain and snow-region
    // variants), matched by file name only (case-insensitive) regardless of folder.
    private static readonly string[] DecalTextureFileNames = { "rocks01.dds", "snowrocks01.dds" };

    // Retexture target for HideDecalShapes' own non-alpha "companion" shapes (see that method) -
    // matches the exact fix Vanaheimr's own "_snow" mesh variants use for the identical shape.
    private const string SnowRetextureDiffuse = @"textures\landscape\snow01.dds";
    private const string SnowRetextureNormal = @"textures\landscape\snow01_n.dds";

    // Folders HideDecalShapes is scoped to - the same three asset categories the reference mod
    // (nexusmods.com/skyrimspecialedition/mods/131170) ships its own fixed meshes under. DirtCliffs
    // meshes are deliberately NOT included: their own "Skirt" shape must be kept (it's what
    // GenerateDirtCliffsSnowVariant/RetextureDirtCliffsSkirt retextures for snow), and DirtCliffs
    // shapes never use the Rocks01/SnowRocks01 textures this feature targets anyway.
    private static readonly string[] HideDecalShapesFolderPatterns =
        { @"*\landscape\mountains\*", @"*\landscape\rocks\*", @"*\landscape\tundra\*" };

    // NiAVObject.flags bit - hides the node/shape from rendering without removing it from the file,
    // so block indices (and therefore any plugin-side AltTexture index) never shift.
    private const uint HiddenBit = 0x1;

    // The snow Material Objects (MATO EditorIDs) RemoveIceSnowMaterial strips from every static that
    // uses them. NOT IceShader01 and friends - those carry the ice's own look, only snow projected on
    // top of it is being removed.
    private static readonly string[] IceSnowMaterialEditorIds = { "SnowMaterialGlacier", "SnowMaterialGlacierSlab" };


    private readonly ExtractSettings _settings;
    private readonly string _dataFolder;
    private readonly string _outputFolder;
    private readonly BlacklistEvaluator _blacklist;

    private IGameEnvironment<ISkyrimMod, ISkyrimModGetter> _env = null!;
    private IGameFileProbe _fileProbe = null!;
    private Mo2InstanceReader? _mo2Reader;
    private static readonly ModKey _outputModKey = new("SnowFixer", ModType.Plugin);
    private SkyrimMod _outputMod = null!;
    private readonly HashSet<string> _usedOutputPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _diagnostics = new();
    private readonly char[] _invalidFileNameChars = Path.GetInvalidFileNameChars();

    private int _matched;
    private int _meshesCopied;
    private int _meshesFailed;
    private int _malformedRecordsSkipped;
    private int _altTexBaked;
    private int _altTexFailed;
    private int _shaderFlagsPatched;
    private int _vertexColorsNeutralized;
    private int _collisionMaterialsRemapped;
    private int _dirtCliffsSkirtShapesRetextured;
    private int _mountainSlabMaskSwapped;
    private int _decalShapesHidden;
    private int _decalCompanionShapesRetextured;
    private int _iceSnowMaterialsRemoved;
    private int _nonSnowLandscapeMeshesIncluded;
    private int _landscapesPatched;

    public ExtractOrchestrator(ExtractSettings settings)
    {
        _settings = settings;
        _dataFolder = Path.Combine(settings.GameLocation, "Data");
        _outputFolder = settings.OutputLocation;
        _blacklist = new BlacklistEvaluator(settings);
    }

    public ExtractResult Run(Action<ExtractProgress>? progress = null)
    {
        void Report(string message, int current = 0, int total = 0) => progress?.Invoke(new ExtractProgress(message, current, total));

        if (!Directory.Exists(_dataFolder))
        {
            throw new DirectoryNotFoundException($"Data folder not found: {_dataFolder}");
        }

        // Refuse to run if Output Location is - or contains - the real game Data folder. Reported
        // directly (Nexus): a user pointed Output Location at their game's own Data folder (with no
        // dedicated output folder set up), and the wipe step below deleted their entire real
        // meshes\ folder - every mod's own meshes (skeletons, animation replacers, ...) gone. Checked
        // via full-path comparison (case-insensitive, trailing separators trimmed) so this catches
        // the exact-match case; the broader "does this even look like our own prior output" check
        // right below catches every other unrelated folder Output Location could point at.
        var normalizedOutput = Path.GetFullPath(_outputFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedDataFolder = Path.GetFullPath(_dataFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(normalizedOutput, normalizedDataFolder, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Output Location is set to your game's own Data folder ('{_outputFolder}') - Snow Fixer "
                + "wipes and regenerates everything under Output Location on every run, which would delete "
                + "your entire real Data folder. Point Output Location at an empty, dedicated folder instead "
                + "(e.g. your mod manager's own \"Snow Fixer Output\" mod folder).");
        }

        // Wipe any previous run's own meshes/plugin before regenerating - this folder is entirely
        // owned by this tool (never hand-edited), so a stale leftover from an earlier run (e.g. a
        // record type that used to be scanned but no longer is) would otherwise sit on disk
        // forever, unreferenced by the fresh ESP but still shipped to players as dead weight.
        var outputMeshesParent = Path.Combine(_outputFolder, "meshes");
        var previousEspPath = Path.Combine(_outputFolder, "SnowFixer.esp");
        var previousLogPath = Path.Combine(_outputFolder, "SnowFixer-log.txt");

        if (Directory.Exists(outputMeshesParent))
        {
            // "Entirely owned by this tool" only holds if a previous run's own marker is actually
            // there - otherwise Output Location was pointed at some other folder that just happens
            // to already have a meshes\ subfolder (any existing mod, or - the exact real report
            // above - the game's own Data folder), and blindly deleting it would destroy content
            // this tool never created. Refuse instead of guessing.
            if (!File.Exists(previousEspPath) && !File.Exists(previousLogPath))
            {
                throw new InvalidOperationException(
                    $"Output Location ('{_outputFolder}') already has a 'meshes' folder, but no "
                    + "SnowFixer.esp or SnowFixer-log.txt from a previous run - this doesn't look like "
                    + "Snow Fixer's own output, so refusing to delete it. Point Output Location at an "
                    + "empty, dedicated folder instead.");
            }

            Directory.Delete(outputMeshesParent, recursive: true);
        }

        if (File.Exists(previousEspPath))
        {
            File.Delete(previousEspPath);
        }

        Directory.CreateDirectory(_outputFolder);

        Report("Loading game environment...");

        var gameRelease = ToGameRelease(_settings.GameType);
        var skyrimRelease = ToSkyrimRelease(_settings.GameType);

        string? mo2ProfileName = null;
        if (_settings.ModManager == ModManagerType.ModOrganizer2)
        {
            mo2ProfileName = _settings.Mo2ProfileName;
            if (string.IsNullOrWhiteSpace(mo2ProfileName))
            {
                if (!Mo2InstanceReader.TryDetectSelectedProfile(_settings.Mo2InstancePath, out var detectedProfile))
                {
                    throw new InvalidOperationException(
                        $"No MO2 profile was selected and ModOrganizer.ini did not identify one. " +
                        $"Select a profile in the launcher or set selected_profile in '{Path.Combine(_settings.Mo2InstancePath, "ModOrganizer.ini")}'.");
                }

                mo2ProfileName = detectedProfile;
            }
        }

        using var mo2Reader = mo2ProfileName is not null
            ? new Mo2InstanceReader(_settings.Mo2InstancePath, mo2ProfileName, gameRelease, _diagnostics.Add)
            : null;

        // Mutagen loads every plugin from one physical Data folder — it has no notion of MO2's
        // per-mod folders. For MO2 we materialize just the active plugins.txt entries (resolved
        // through the same overwrite/priority order as the file probe below) into a throwaway
        // folder in plugins.txt's order, so the winning-override resolution matches what MO2
        // actually shows in-game. Meshes/textures are NOT materialized — those stay served live
        // through Mo2ModlistFileProbe.
        using var materializedLoadOrder = mo2Reader is not null
            ? Mo2LoadOrderMaterializer.Materialize(mo2Reader, mo2ProfileName!, _dataFolder, gameRelease, _diagnostics)
            : null;

        // Without an explicit load order, Mutagen's own auto-detection (the branch below this used
        // to always take for a non-MO2/Vortex setup) imports EVERY plugin it finds physically
        // present in the Data folder, regardless of whether plugins.txt actually marks it active -
        // confirmed directly against Mutagen's own source (LoadOrderImporter.Import calls
        // Importer.Import for every listing unconditionally, only ever carrying the listing's own
        // Enabled flag through as inert metadata on the result). Reported directly on Nexus: a
        // disabled plugin still got treated as a winning-override source and ended up as a master
        // of SnowFixer's own output. Reading the real plugins.txt ourselves first and passing only
        // the actually-enabled entries closes this the same way Mo2LoadOrderMaterializer already
        // does for MO2 - falling back to Mutagen's own auto-detection only if this can't be read at
        // all, rather than ever silently including disabled plugins' own records. Same fix as
        // AutoBlend's own identical one.
        ModKey[]? activeLoadOrder = null;
        if (materializedLoadOrder is null)
        {
            try
            {
                // PluginListings.LoadOrderListings itself has no notion of the game's own implicit
                // base masters (Skyrim.esm and the official DLCs are never actually written to
                // plugins.txt by the game itself, since they can't be disabled) - without adding
                // them explicitly here too (same list, same reasoning as
                // Mo2LoadOrderMaterializer's own identical one), an active-only list would scan zero
                // records at all, since nothing in plugins.txt itself names Skyrim.esm.
                var activePluginNames = PluginListings.LoadOrderListings(gameRelease, _dataFolder, throwOnMissingMods: false)
                    .Where(l => l.Enabled)
                    .Select(l => l.ModKey.FileName.String)
                    .ToList();
                var alreadyListed = new HashSet<string>(activePluginNames, StringComparer.OrdinalIgnoreCase);
                activeLoadOrder = Mo2LoadOrderMaterializer.ImplicitBaseMasterFileNames(gameRelease)
                    .Where(name => !alreadyListed.Contains(name))
                    .Concat(activePluginNames)
                    .Select(name => ModKey.FromNameAndExtension(name))
                    .ToArray();
            }
            catch (Exception ex)
            {
                _diagnostics.Add("Could not read the game's own plugins.txt to filter to only active plugins "
                    + $"({ex.Message}) - falling back to Mutagen's own auto-detection, which may incorrectly "
                    + "include disabled plugins' own records.");
            }
        }

        var envDataFolder = materializedLoadOrder?.DataFolder ?? _dataFolder;
        var gameLanguage = GameLanguageDetector.Detect(
            mo2Reader is not null ? mo2Reader.ProfilePath(mo2ProfileName!) : null, _settings.GameType);

        // Materialize copies each active plugin's own .esm/.esp/.esl into a bare throwaway folder -
        // no Strings/ subfolder, no BSAs - so, unqualified, Mutagen can never find ANY localized
        // plugin's own STRINGS/DLSTRINGS/ILSTRINGS content from there at all. Vanilla/DLC/CC strings
        // are always packed into the real Data folder's own BSAs (Skyrim - Interface.bsa) - pointing
        // Mutagen's own BSA lookup at the real Data folder instead of the materialized one closes
        // that gap. A mod's own LOOSE translation patch (rare) living only inside its own MO2 mod
        // folder is not covered by this - that would need the same priority-aware resolution meshes
        // already get, which is a separate, larger piece of work.
        var looseStringsFolder = Path.Combine(_dataFolder, "Strings");
        var stringsParameters = new StringsReadParameters
        {
            TargetLanguage = gameLanguage,
            BsaFolderOverride = materializedLoadOrder is not null ? _dataFolder : null,
            StringsFolderOverride = materializedLoadOrder is not null && Directory.Exists(looseStringsFolder) ? looseStringsFolder : null,
        };

        var sourceLoadOrder = materializedLoadOrder is not null ? materializedLoadOrder.LoadOrder.ToArray() : activeLoadOrder;

        // A previous run's own SnowFixer.esp, left enabled in the user's own load order, is an
        // OUTPUT this tool wrote, not a real source to scan winning overrides from - every run
        // already wipes and regenerates its own output from scratch, so treating a stale prior copy
        // as a source would perpetuate outdated data even before considering anything else. Reported
        // directly on Nexus: SnowFixer.esp overrides some records with English text instead of the
        // user's own localized (Russian) strings. Root-caused directly against the real modlist: the
        // FULL text itself was correct with no language specified at all (see GameLanguageDetector's
        // own reasoning for that half of the fix) - what actually broke it was SnowFixer.esp's own
        // prior copy sitting at the END of the load order Mutagen was asked to build: with it
        // present, EVERY OTHER plugin's own localized strings (not just SnowFixer.esp's own records)
        // came back blank, regardless of target language; with it excluded, the exact same load order
        // resolved correctly. Bisected directly against the user's real ~65-plugin load order to
        // confirm SnowFixer.esp specifically (not merely "the last-loaded plugin" in general) is what
        // breaks it.
        var filteredLoadOrder = sourceLoadOrder?.Where(k => k != _outputModKey).ToArray();
        if (filteredLoadOrder is not null && sourceLoadOrder is not null && filteredLoadOrder.Length != sourceLoadOrder.Length)
        {
            _diagnostics.Add("A previous run's own SnowFixer.esp was left active in the load order - "
                + "excluded it from this run's own source scan (Snow Fixer always regenerates its own "
                + "output from scratch, so a stale prior copy is never a real source, and its presence "
                + "was also breaking every other localized (non-English) string in this run).");
        }

        _env = BuildEnvironment(gameRelease, envDataFolder, filteredLoadOrder, stringsParameters);
        using var envDisposable = _env;

        _mo2Reader = mo2Reader;
        _fileProbe = mo2Reader is not null
            ? new Mo2ModlistFileProbe(mo2Reader, _dataFolder, gameRelease, _diagnostics.Add)
            : new ArchiveAwareFileProbe(_dataFolder, gameRelease, _diagnostics.Add);
        using var fileProbeDisposable = _fileProbe;

        _outputMod = new SkyrimMod(_outputModKey, skyrimRelease);

        Report("Scanning records...");

        ProcessType<IStaticGetter>(
            _env.LoadOrder.PriorityOrder.WinningOverrides<IStaticGetter>(),
            r => r.EditorID,
            r => r.Model?.File.GivenPath,
            r => r.Model?.AlternateTextures,
            r => _outputMod.Statics.GetOrAddAsOverride(r).Model!,
            "Static", Report);

        ProcessType<IMoveableStaticGetter>(
            _env.LoadOrder.PriorityOrder.WinningOverrides<IMoveableStaticGetter>(),
            r => r.EditorID,
            r => r.Model?.File.GivenPath,
            r => r.Model?.AlternateTextures,
            r => _outputMod.MoveableStatics.GetOrAddAsOverride(r).Model!,
            "MoveableStatic", Report);

        ProcessType<IFloraGetter>(
            _env.LoadOrder.PriorityOrder.WinningOverrides<IFloraGetter>(),
            r => r.EditorID,
            r => r.Model?.File.GivenPath,
            r => r.Model?.AlternateTextures,
            r => _outputMod.Florae.GetOrAddAsOverride(r).Model!,
            "Flora", Report);

        ProcessType<IFurnitureGetter>(
            _env.LoadOrder.PriorityOrder.WinningOverrides<IFurnitureGetter>(),
            r => r.EditorID,
            r => r.Model?.File.GivenPath,
            r => r.Model?.AlternateTextures,
            r => _outputMod.Furniture.GetOrAddAsOverride(r).Model!,
            "Furniture", Report);

        ProcessType<IDoorGetter>(
            _env.LoadOrder.PriorityOrder.WinningOverrides<IDoorGetter>(),
            r => r.EditorID,
            r => r.Model?.File.GivenPath,
            r => r.Model?.AlternateTextures,
            r => _outputMod.Doors.GetOrAddAsOverride(r).Model!,
            "Door", Report);

        ProcessType<IContainerGetter>(
            _env.LoadOrder.PriorityOrder.WinningOverrides<IContainerGetter>(),
            r => r.EditorID,
            r => r.Model?.File.GivenPath,
            r => r.Model?.AlternateTextures,
            r => _outputMod.Containers.GetOrAddAsOverride(r).Model!,
            "Container", Report);

        if (_settings.RemoveIceSnowMaterial)
        {
            Report("Removing snow material from ice statics...");
            RemoveIceSnowMaterials();
        }

        if (_settings.LandscapeVertexColorMode != LandscapeVertexColorMode.None)
        {
            try
            {
                PatchLandscapes(Report);
            }
            catch (Exception ex)
            {
                // Finding any LAND record needs Mutagen to open every mod's own Cells/Worldspace
                // group tree (Landscape().WinningContextOverrides()), even mods that never touch a
                // single landscape record - so one plugin with a malformed group header anywhere in
                // that tree aborts landscape scanning across the ENTIRE load order, not just its own
                // records. Reported directly on Nexus: an OverflowException surfaced this way from a
                // single corrupted plugin, taking down the whole run (nothing was scanned at all,
                // not even the unrelated Static/Flora/etc. passes already completed above by this
                // point). Landscape vertex color clearing is skipped instead - everything already
                // scanned above is unaffected and still gets written normally.
                var reason = UnreadablePluginFinder.TryFind(ex, out var badPlugin, out var innerReason)
                    ? $"plugin '{badPlugin.FileName}' could not be read ({innerReason})"
                    : $"{ex.GetType().Name}: {ex.Message}";
                _diagnostics.Add($"Landscape vertex color clearing was skipped: {reason}. It is probably "
                    + "corrupted - consider reinstalling or removing that mod. Everything else in this run "
                    + "completed normally.");
            }
        }

        var dirtCliffsSnowVariantGenerated = false;
        if (_settings.GenerateDirtCliffsSnowVariant)
        {
            Report("Generating DirtCliffsRoots snow variant texture...");
            var textureGenerator = new DirtCliffsSnowVariantGenerator(_fileProbe, _outputFolder, _settings.GameType == GameType.SkyrimLE);
            textureGenerator.Run();
            dirtCliffsSnowVariantGenerated = textureGenerator.Generated;
            _diagnostics.AddRange(textureGenerator.Diagnostics);
        }

        // Guarantee SnowFixer.esp never depends on a plugin that isn't actually part of the loaded
        // setup - see RemoveLinksToUnavailableMasters.
        RemoveLinksToUnavailableMasters(IsPluginAvailable);

        // Flag the output as ESL (Light) whenever it actually fits that format's own new-record
        // range, matching AutoBlend's own identical logic - opt-in rather than default is the
        // wrong framing here, since there's no downside to a smaller plugin that still works
        // exactly the same, only an upside (frees a real load-order slot). CanBeSmallMaster is
        // Mutagen's own check against that ceiling - only flip the flag when it actually holds, so
        // an unusually large run still writes a normal ESP instead of a corrupt one.
        //
        // ESL/light plugins are a Skyrim SE-only engine feature - Mutagen's own
        // SkyrimMod.CanBeSmallMaster returns true purely from the FormID/record-count ceiling, with
        // no awareness that Legendary Edition's engine has no concept of the ESL flag at all.
        // Flagging a plugin ESL for an LE run would produce something LE can't actually load
        // correctly - confirmed directly while adding Legendary Edition support - so this only ever
        // applies on SE.
        if (_settings.GameType == GameType.SkyrimSE && _outputMod.CanBeSmallMaster)
        {
            _outputMod.IsSmallMaster = true;
        }
        else if (_settings.GameType == GameType.SkyrimSE)
        {
            _diagnostics.Add("This run's own new records exceed the ESL limit - plugin written as a "
                + "regular (non-ESL) ESP instead.");
        }

        Report("Writing plugin...");
        var espPath = Path.Combine(_outputFolder, "SnowFixer.esp");
        _outputMod.BeginWrite.ToPath(espPath).WithNoLoadOrder().Write();

        var result = new ExtractResult(
            _matched,
            _meshesCopied,
            _meshesFailed,
            _malformedRecordsSkipped,
            _altTexBaked,
            _altTexFailed,
            _shaderFlagsPatched,
            _vertexColorsNeutralized,
            _collisionMaterialsRemapped,
            _dirtCliffsSkirtShapesRetextured,
            _mountainSlabMaskSwapped,
            _decalShapesHidden,
            _decalCompanionShapesRetextured,
            _iceSnowMaterialsRemoved,
            _nonSnowLandscapeMeshesIncluded,
            _landscapesPatched,
            dirtCliffsSnowVariantGenerated,
            _diagnostics,
            espPath);

        WriteLog(result, espPath);

        return result;
    }

    private void WriteLog(ExtractResult result, string espPath)
    {
        var logPath = Path.Combine(_outputFolder, "SnowFixer-log.txt");
        var log = new List<string>
        {
            "SnowFixer run",
            "",
            "=== Result ===",
            $"Records matched: {result.RecordsMatched}",
            $"Meshes duplicated: {result.MeshesDuplicated}",
            $"Meshes failed to resolve: {result.MeshesFailed}",
            $"Malformed records skipped: {result.MalformedRecordsSkipped}",
            $"Alternate Textures baked: {result.AlternateTexturesBaked}",
            $"Alternate Textures that couldn't be baked: {result.AlternateTexturesFailed}",
            $"Meshes with ZBuffer_Write/No_Fade shader flag fixups: {result.ShaderFlagsPatched}",
            $"Meshes with vertex colors neutralized: {result.VertexColorsNeutralized}",
            $"Meshes with collision materials remapped to snow: {result.CollisionMaterialsRemapped}",
            $"DirtCliffs 'Skirt' shapes retextured to the snow variant: {result.DirtCliffsSkirtShapesRetextured}",
            $"MountainSlab shapes swapped to their Mask variant: {result.MountainSlabMaskSwapped}",
            $"Decal shapes hidden: {result.DecalShapesHidden}",
            $"Decal companion shapes retextured to snow: {result.DecalCompanionShapesRetextured}",
            $"Ice statics with their snow Material Object removed: {result.IceSnowMaterialsRemoved}",
            $"Non-snow landscape meshes also included for vertex color/collision fixups: {result.NonSnowLandscapeMeshesIncluded}",
            $"Landscape records with vertex colors cleared: {result.LandscapesPatched}",
            $"DirtCliffsRoots snow variant texture generated: {result.DirtCliffsSnowVariantGenerated}",
            $"Plugin written: {espPath}",
            "",
            $"{result.Diagnostics.Count} diagnostic(s):",
        };
        log.AddRange(result.Diagnostics.Select(d => $" - {d}"));
        File.WriteAllLines(logPath, log);
    }

    private bool TryResolveMesh(string relativeMeshPath, out Stream? stream)
    {
        var archiveRelative = Path.Combine("meshes", relativeMeshPath);
        if (_fileProbe.Exists(archiveRelative))
        {
            stream = _fileProbe.OpenRead(archiveRelative);
            return true;
        }

        stream = null;
        return false;
    }

    // Beyond the literal "snow" word, Bethesda's own EditorID convention for regional snow-coverage
    // variants of an otherwise-shared mesh (mountains, glaciers, ice floes, rubble, ...) is a bare
    // "SN" suffix - "_LightSN"/"_HeavySN", or occasionally just "_SN"/"_Sn" - never spelling "snow"
    // out in full. Verified against a real load order: 247 real records (MountainTrim01_HeavySN,
    // FrozenMarshIceFloeL01_SN, DLC2IcebergLarge_LightSN, ...) match this suffix without containing
    // "snow" anywhere else in their EditorID - all sharing a base (non-snow) mesh with a snow-region
    // counterpart differentiated only by texture/EditorID, not a distinct shader type (checked
    // directly against a sample of these meshes' own BSLightingShaderProperty - none use the "Snow"
    // or "Multi Index Snow" shader type; that type appears to be landscape-terrain-only, never used
    // on object meshes in the base game/DLC/CC content). "_NoSN" is the explicit opposite ("No
    // Snow") and must never match, even though it also ends in "SN".
    private static bool EndsWithSnowSuffix(string editorId) =>
        editorId.EndsWith("SN", StringComparison.OrdinalIgnoreCase)
        && !editorId.EndsWith("NoSN", StringComparison.OrdinalIgnoreCase);

    private static bool EditorIdSaysSnow(string? editorId) =>
        editorId is not null && (editorId.Contains("snow", StringComparison.OrdinalIgnoreCase) || EndsWithSnowSuffix(editorId));

    private static bool IsSnow(string? editorId, string? modelPath) =>
        EditorIdSaysSnow(editorId)
        || (modelPath is not null && modelPath.Contains("snow", StringComparison.OrdinalIgnoreCase));

    // A shared mesh whose own FILE NAME happens to say "snow" (e.g. Landscape\SnowDrifts\...) is
    // reused across unrelated contexts via Alternate Textures - confirmed directly against real
    // vanilla data: DLC01AshDriftL01/L02/L03/L04 and DLC02AshDuneVolcAsh01L01/L02 (Solstheim ash
    // piles/dunes, EditorIDs say nothing about snow) both reuse Landscape\SnowDrifts\SnowDriftL0*.nif
    // with an Alternate Texture repointing the diffuse to ash. Matching purely on the base mesh path
    // (the only signal IsSnow had before) treated these as snow records - reported directly on
    // Nexus: Snow Fixer duplicating/patching them left the wrong texture applied to snow-adjacent
    // but non-snow shapes on other meshes reusing the same trick (Lordbound's own ash/dirt piles were
    // named as an example). Only demotes when EditorID gives no snow signal of its own AND every
    // Alternate Texture's own resolved diffuse also says nothing about snow - a record with even one
    // legitimately snow-targeted Alternate Texture (e.g. SnowDriftL02_GlacierBlend) is left alone.
    private bool IsSnowDemotedByAlternateTextures(string? editorId, IReadOnlyList<IAlternateTextureGetter>? altTexs)
    {
        if (EditorIdSaysSnow(editorId) || altTexs is not { Count: > 0 })
        {
            return false;
        }

        foreach (var altTex in altTexs)
        {
            if (!_env.LinkCache.TryResolve<ITextureSetGetter>(altTex.NewTexture.FormKey, out var txst))
            {
                // Unresolvable - can't prove it's NOT snow, so don't demote on its account.
                return false;
            }

            if ((txst.Diffuse?.GivenPath ?? string.Empty).Contains("snow", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    // Clears STAT.DNAM's Material link (the projected snow/ash overlay - see the Direction Material
    // notes on ExtractSettings.RemoveIceSnowMaterial) on every static whose winning Material points at
    // one of IceSnowMaterialEditorIds. Matched purely by Material Object, not by mesh or EditorID
    // keywords: an earlier keyword-restricted version (glacier/icicle/iceberg/icepile/\ice\) missed
    // real users of these two glacier-specific Material Objects (e.g. vanilla's
    // SERuinsWaygatePlatform01SnowLight). Deliberately ignores the mesh/EditorID blacklists: the
    // default blacklist excludes glacier/ice content from snow duplication, which is the opposite of
    // what this needs.
    private void RemoveIceSnowMaterials()
    {
        foreach (var record in _env.LoadOrder.PriorityOrder.WinningOverrides<IStaticGetter>())
        {
            if (record.Material.IsNull
                || !record.Material.TryResolve(_env.LinkCache, out var material)
                || Array.FindIndex(IceSnowMaterialEditorIds, id => string.Equals(id, material.EditorID, StringComparison.OrdinalIgnoreCase)) < 0)
            {
                continue;
            }

            _outputMod.Statics.GetOrAddAsOverride(record).Material.SetToNull();
            _iceSnowMaterialsRemoved++;
        }
    }

    // A plugin counts as available when it's in the environment's own load order, or physically
    // present in the game's Data folder (base game/DLC/Creation Club plugins the engine loads on its
    // own, never necessarily listed in plugins.txt), or shipped by an ENABLED MO2 mod. A plugin from
    // a disabled mod is none of those.
    private bool IsPluginAvailable(ModKey modKey)
    {
        var fileName = modKey.FileName.String;
        return _env.LoadOrder.ContainsKey(modKey)
            || File.Exists(Path.Combine(_dataFolder, fileName))
            || (_mo2Reader?.TryResolve(fileName, out _) ?? false);
    }

    // Some records SnowFixer.esp carries along aren't the ones a run set out to patch - e.g. a LAND
    // override drags in its parent Cell/Worldspace records, copied whole with every link they hold.
    // If any such link points into a plugin that isn't part of the loaded setup (e.g. a mod the user
    // disabled), the output would list that plugin as a master - reported directly on Nexus as a
    // "missing master" (NOTWL - Lanterns.esp, a patch from a disabled True Light). Links to
    // unavailable plugins are already dead in-game, so they're simply nulled here; Mutagen then
    // drops the no-longer-referenced master when the plugin is written.
    private void RemoveLinksToUnavailableMasters(Func<ModKey, bool> isAvailable)
    {
        var remap = new Dictionary<FormKey, FormKey>();
        var unavailable = new HashSet<ModKey>();
        foreach (var record in _outputMod.EnumerateMajorRecords())
        {
            foreach (var link in record.EnumerateFormLinks())
            {
                var formKey = link.FormKey;
                if (formKey.IsNull || formKey.ModKey == _outputMod.ModKey || isAvailable(formKey.ModKey))
                {
                    continue;
                }

                remap[formKey] = FormKey.Null;
                unavailable.Add(formKey.ModKey);
            }
        }

        if (remap.Count == 0)
        {
            return;
        }

        _outputMod.RemapLinks(remap);
        _diagnostics.Add($"Cleared {remap.Count} reference(s) to plugin(s) that aren't part of the loaded setup "
            + $"(e.g. from a disabled mod), so SnowFixer.esp doesn't list them as masters: "
            + string.Join(", ", unavailable.Select(m => m.FileName.String)));
    }

    private static readonly string[] LandscapeMeshFolderPatterns = { @"*\landscape\*" };

    // Vertex color neutralization is restricted to meshes living somewhere under a "Landscape"
    // folder - matches vanilla's own Landscape\..., as well as DLC-specific variants like
    // DLC01\Landscape\... (Dawnguard icebergs/glaciers), DLC02\Landscape\... (Solstheim trees), and
    // _ResourcePack\Landscape\... - reported directly: non-landscape snow matches (e.g. DLC01's
    // Snow Elf ruins under DLC01\Architecture\SnowElfRuins\...) were also having their vertex colors
    // stripped, which wasn't wanted; only true landscape/terrain-type meshes (rocks, icebergs, snow
    // drifts, trees) should be affected. Relies on WildcardMatcher's leading-wildcard fix to also
    // catch the vanilla case where "Landscape" is the very first path segment.
    // Builds the Mutagen environment; when an explicit load order was given, a plugin Mutagen can't
    // even open (typically empty/corrupted - see UnreadablePluginFinder) is dropped with a diagnostic
    // and the build retried, instead of one bad file aborting the whole run.
    private IGameEnvironment<ISkyrimMod, ISkyrimModGetter> BuildEnvironment(GameRelease gameRelease, string dataFolder, ModKey[]? loadOrder, StringsReadParameters stringsParameters)
    {
        var remaining = loadOrder?.ToList();
        while (true)
        {
            var builder = GameEnvironment.Typical.Builder<ISkyrimMod, ISkyrimModGetter>(gameRelease)
                .WithTargetDataFolder(dataFolder)
                .WithStringParameters(stringsParameters);
            try
            {
                return remaining is null ? builder.Build() : builder.WithLoadOrder(remaining.ToArray()).Build();
            }
            catch (Exception ex) when (remaining is not null
                && UnreadablePluginFinder.TryFind(ex, out var unreadable, out var reason)
                && remaining.Remove(unreadable))
            {
                _diagnostics.Add($"Plugin '{unreadable.FileName}' could not be read and was skipped ({reason}). "
                    + "It is probably empty or corrupted - consider reinstalling or removing that mod.");
            }
        }
    }

    private static bool IsLandscapeMesh(string modelPath) => WildcardMatcher.MatchesAny(modelPath, LandscapeMeshFolderPatterns);

    private string SanitizeFileName(string name)
    {
        var chars = name.Select(c => _invalidFileNameChars.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }

    // Duplicated meshes are named per RECORD, not per source mesh - one physical output file per
    // matched record - since the user wants each duplicate identifiable by the EditorID it belongs
    // to in the ESP (e.g. so editing "Farmhouse01Snow.nif" obviously only affects that record,
    // never an unrelated one that happens to share the same vanilla source mesh). When a record has
    // no usable EditorID (only matched via its mesh path containing "snow"), falls back to the
    // original filename with the given suffix instead ("_snow" for an actual snow match, or
    // "_novc" for a non-snow landscape mesh only duplicated to strip its vertex colors under
    // MeshVertexColorMode.All - see ProcessType).
    private string? DuplicateMesh(string originalRelativePath, string? editorId, string fallbackSuffix = "_snow")
    {
        if (!TryResolveMesh(originalRelativePath, out var stream) || stream is null)
        {
            _diagnostics.Add($"Mesh not found (loose or archived): meshes\\{originalRelativePath}");
            _meshesFailed++;
            return null;
        }

        var extension = Path.GetExtension(originalRelativePath);
        var baseFileName = !string.IsNullOrEmpty(editorId)
            ? SanitizeFileName(editorId) + extension
            : Path.GetFileNameWithoutExtension(originalRelativePath) + fallbackSuffix + extension;

        // Sits directly alongside the original file, no "snow" sibling subfolder - the filename
        // itself (EditorID, which itself commonly already says "Snow", or the "_snow" suffix
        // fallback) already disambiguates it from the original for the game engine, which needs
        // nothing more than a unique path. A separate subfolder per original directory was
        // reconsidered as pure organizational overhead once the naming already does the actual
        // disambiguation job - and DynDOLOD in particular is simpler to configure against a flat
        // layout that mirrors vanilla's own tree exactly, rather than one with an extra nesting
        // level to account for. E.g. "Architecture\Farmhouse\Farmhouse01.nif" with EditorID
        // "Farmhouse01Snow" -> "Architecture\Farmhouse\Farmhouse01Snow.nif".
        var originalDir = Path.GetDirectoryName(originalRelativePath) ?? string.Empty;
        var newRelativePath = Path.Combine(originalDir, baseFileName);

        // EditorIDs are unique within a game, but two different records could still land on the
        // same filename after sanitization in principle - guard rather than silently overwrite.
        var suffix = 2;
        while (!_usedOutputPaths.Add(newRelativePath))
        {
            var dedupedName = Path.GetFileNameWithoutExtension(baseFileName) + $"_{suffix}" + extension;
            newRelativePath = Path.Combine(originalDir, dedupedName);
            suffix++;
        }

        var destFullPath = Path.Combine(_outputFolder, "meshes", newRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destFullPath)!);

        using (stream)
        using (var dest = File.Create(destFullPath))
        {
            stream.CopyTo(dest);
        }

        _meshesCopied++;
        return newRelativePath;
    }

    // Bakes every resolvable Alternate Texture's own TextureSet straight into the matching shape
    // (by name, falling back to the shape's index among the NIF's own shape list when the name
    // doesn't match anything - the CK-authored name can go stale after a mesh's shapes are
    // renamed/reordered, but the engine itself resolves by index) of the just-duplicated mesh - all
    // 8 slots (diffuse, normal, glow, height, environment, mask, multilayer, backlight/specular),
    // not just the diffuse, since baking only the diffuse left a shape's other slots (most visibly
    // the normal map) mismatched against whatever the mesh's own original default happened to
    // embed. Returns true if at least one shape was baked, meaning the caller should drop
    // AlternateTextures from the override so the now-redundant ESP-level data doesn't linger.
    private bool BakeAlternateTextures(string duplicatedRelativePath, IReadOnlyList<IAlternateTextureGetter> altTexs, string label)
    {
        var fullPath = Path.Combine(_outputFolder, "meshes", duplicatedRelativePath);

        using var nifFile = new NifFile();
        if (NifIo.Load(nifFile, fullPath) != 0)
        {
            _diagnostics.Add($"'{label}': failed to reload duplicated mesh for Alternate Texture baking.");
            return false;
        }

        var shapes = nifFile.GetShapes().ToList();
        var baked = false;
        var failed = false;

        foreach (var altTex in altTexs)
        {
            try
            {
                if (!_env.LinkCache.TryResolve<ITextureSetGetter>(altTex.NewTexture.FormKey, out var txst)
                    || string.IsNullOrEmpty(txst.Diffuse?.GivenPath))
                {
                    _diagnostics.Add($"'{label}': could not resolve Alternate Texture's TextureSet for shape '{altTex.Name}' - left as-is.");
                    _altTexFailed++;
                    failed = true;
                    continue;
                }

                var shape = shapes.FirstOrDefault(s => s.name.get() == altTex.Name)
                    ?? (altTex.Index >= 0 && altTex.Index < shapes.Count ? shapes[altTex.Index] : null);
                if (shape is null)
                {
                    _diagnostics.Add($"'{label}': Alternate Texture's shape '{altTex.Name}' (index {altTex.Index}) not found in the duplicated mesh - left as-is.");
                    _altTexFailed++;
                    failed = true;
                    continue;
                }

                // Materialize all slots before changing the NIF so a malformed path cannot leave a
                // partially baked Alternate Texture behind.
                var slots = TextureSetSlots(txst).ToArray();
                foreach (var (slot, path) in slots)
                {
                    if (!string.IsNullOrEmpty(path))
                    {
                        nifFile.SetTextureSlot(shape, path, slot);
                    }
                }

                baked = true;
                _altTexBaked++;
            }
            catch (AssetPathMisalignedException ex)
            {
                _altTexFailed++;
                failed = true;
                _diagnostics.Add(
                    $"'{label}': invalid Alternate Texture asset path for shape '{altTex.Name}' - left as-is. {ex.Message}");
            }
        }

        if (baked)
        {
            var saveOptions = new NifSaveOptions { optimize = false, sortBlocks = false };
            if (NifIo.Save(nifFile, fullPath, saveOptions) != 0)
            {
                _diagnostics.Add($"'{label}': failed to save the mesh after baking Alternate Textures.");
                return false;
            }
        }

        // Keep the ESP-level list when any entry failed. Dropping the whole list after baking only
        // a subset would silently discard the failed Alternate Texture, while retaining it is a
        // safe fallback for both the malformed and successfully baked entries.
        return baked && !failed;
    }

    // Same fix as AutoBlend's own NiAlphaBlendPatcher: every shape carrying a NiAlphaProperty
    // (alpha test or alpha blend, whatever the source mesh already had) gets ZBuffer_Write and
    // No_Fade set on its shader flags. Many source meshes ship with these disabled, which causes
    // visible render artifacts and unwanted distance dithering once a shape is actually
    // alpha-blended - since these duplicated snow meshes carry their own NiAlphaProperty straight
    // from the vanilla source (nothing here converts test->blend the way AutoBlend does), this only
    // ever touches shapes that already have one, unconditionally, rather than being scoped to a
    // separately-detected blend target list.
    private bool ApplyAlphaShaderFixups(string duplicatedRelativePath, string label)
    {
        var fullPath = Path.Combine(_outputFolder, "meshes", duplicatedRelativePath);

        using var nifFile = new NifFile();
        if (NifIo.Load(nifFile, fullPath) != 0)
        {
            _diagnostics.Add($"'{label}': failed to reload duplicated mesh for shader flag fixups.");
            return false;
        }

        var patched = false;

        foreach (var shape in nifFile.GetShapes())
        {
            if (!shape.HasAlphaProperty() || !shape.HasShaderProperty())
            {
                continue;
            }

            var shaderBlockIndex = shape.ShaderPropertyRef().index;
            if (nifFile.GetHeader().GetBlockById(shaderBlockIndex) is not BSShaderProperty shaderProperty)
            {
                continue;
            }

            var flags2 = shaderProperty.shaderFlags2;
            var changed = false;

            if ((flags2 & ZBufferWriteBit) == 0)
            {
                flags2 |= ZBufferWriteBit;
                changed = true;
            }

            if ((flags2 & NoFadeBit) == 0)
            {
                flags2 |= NoFadeBit;
                changed = true;
            }

            if (changed)
            {
                shaderProperty.shaderFlags2 = flags2;
                patched = true;
            }
        }

        if (patched)
        {
            var saveOptions = new NifSaveOptions { optimize = false, sortBlocks = false };
            if (NifIo.Save(nifFile, fullPath, saveOptions) != 0)
            {
                _diagnostics.Add($"'{label}': failed to save the mesh after shader flag fixups.");
                return false;
            }

            _shaderFlagsPatched++;
        }

        return patched;
    }

    private static bool IsHideDecalShapesEligibleFolder(string modelPath) =>
        WildcardMatcher.MatchesAny(modelPath, HideDecalShapesFolderPatterns);

    // Modeled on "Enhanced Rocks and Mountains - Blending Patch And Other Fixes"
    // (nexusmods.com/skyrimspecialedition/mods/131170), whose own "_snow" mesh variants (e.g.
    // rockcliff08_snow.nif) do exactly this pairing directly - confirmed via nifly against the real
    // file: the actual decal shape (alpha-blended "Rocks01"/"SnowRocks01") is gone entirely, and its
    // non-alpha companion (same texture, no NiAlphaProperty - e.g. vanilla RockCliff08's own ":9")
    // is RETEXTURED to "landscape\snow01", not removed. Deleting the decal block outright needs more
    // block-graph surgery than hiding it does for the same visual result, so this hides it instead
    // (NiAVObject Hidden flag - block indices, and any plugin-side AltTexture index, never move).
    //
    // Detection is by diffuse texture name (DecalTextureFileNames); which of the two treatments a
    // matching shape gets is decided by HasAlphaProperty(), not the NIF's own SLSF1_Decal shader
    // flag - texture name plus the decal flag alone is NOT safe: reported directly on Nexus
    // (visible holes) after shipping that way. Vanilla RockCliff08's own ":9" shares "Rocks01.dds"
    // with the real decal shape ":8" but was never decal-flagged, and turned out to be real, load-
    // bearing surface geometry once simply hidden alongside ":8" - hiding it left a gaping hole,
    // exactly the failure mode Vanaheimr's own fix avoids by retexturing it instead. HasAlphaProperty
    // reliably tells the two apart (true for the actual decal shape, false for its companion) -
    // confirmed against both RockCliff01 (no companion at all - only its own decal has alpha) and
    // RockCliff08 (companion present, no alpha) in the real vanilla meshes.
    private bool HideDecalShapes(string duplicatedRelativePath, string label)
    {
        var fullPath = Path.Combine(_outputFolder, "meshes", duplicatedRelativePath);

        using var nifFile = new NifFile();
        if (NifIo.Load(nifFile, fullPath) != 0)
        {
            _diagnostics.Add($"'{label}': failed to reload duplicated mesh for decal shape hiding.");
            return false;
        }

        var header = nifFile.GetHeader();
        var hidAny = false;
        var retexturedAny = false;

        foreach (var shape in nifFile.GetShapes())
        {
            if (!TryGetLightingShaderProperty(header, shape, out var shaderProperty)
                || !TryGetDiffuseTexture(header, shaderProperty!, out var diffuse)
                || Array.IndexOf(DecalTextureFileNames, Path.GetFileName(diffuse).ToLowerInvariant()) < 0)
            {
                continue;
            }

            if (shape.HasAlphaProperty())
            {
                if ((shape.flags & HiddenBit) == 0)
                {
                    shape.flags |= HiddenBit;
                    hidAny = true;
                }
            }
            else if (RetextureCompanionToSnow(nifFile, shape, shaderProperty!))
            {
                retexturedAny = true;
            }
        }

        var patched = hidAny || retexturedAny;
        if (patched)
        {
            // Same cleanup as RetextureDirtCliffsSkirt/SwapMountainSlabToMaskVariant - cloning a
            // shared texture set per matching shape leaves each original block behind once nothing
            // references it anymore.
            header.DeleteBlockByType("BSShaderTextureSet", true);

            var saveOptions = new NifSaveOptions { optimize = false, sortBlocks = false };
            if (NifIo.Save(nifFile, fullPath, saveOptions) != 0)
            {
                _diagnostics.Add($"'{label}': failed to save the mesh after hiding decal shapes.");
                return false;
            }

            if (hidAny)
            {
                _decalShapesHidden++;
            }

            if (retexturedAny)
            {
                _decalCompanionShapesRetextured++;
            }
        }

        return patched;
    }

    private static bool TryGetLightingShaderProperty(NiHeader header, NiShape shape, out BSLightingShaderProperty? shaderProperty)
    {
        shaderProperty = shape.HasShaderProperty()
            ? header.GetBlockById(shape.ShaderPropertyRef().index) as BSLightingShaderProperty
            : null;
        return shaderProperty is not null;
    }

    private static bool TryGetDiffuseTexture(NiHeader header, BSLightingShaderProperty shaderProperty, out string diffuse)
    {
        diffuse = string.Empty;
        if (header.GetBlockById(shaderProperty.TextureSetRef().index) is not BSShaderTextureSet textureSet)
        {
            return false;
        }

        var items = textureSet.textures.items();
        if (items.Count == 0)
        {
            return false;
        }

        diffuse = items[0].get();
        return !string.IsNullOrEmpty(diffuse);
    }

    // Retextures a HideDecalShapes "companion" shape (matches Rocks01/SnowRocks01 but has no
    // NiAlphaProperty, so it's real geometry rather than the decal itself - see HideDecalShapes'
    // own comment) to the vanilla snow ground texture, exactly as Vanaheimr's own "_snow" mesh
    // variants do for the identical shape. Same private-texture-set-clone pattern as
    // SwapMountainSlabToMaskVariant, since the texture set may be shared with sibling shapes.
    private static bool RetextureCompanionToSnow(NifFile nifFile, NiShape shape, BSLightingShaderProperty shaderProperty)
    {
        if (nifFile.GetHeader().GetBlockById(shaderProperty.TextureSetRef().index) is not BSShaderTextureSet textureSet)
        {
            return false;
        }

        var items = textureSet.textures.items();
        if (items.Count == 0)
        {
            return false;
        }

        var privateItems = new vectorNiString();
        foreach (var item in items)
        {
            privateItems.Add(new NiString(item.get()));
        }

        var privateTextureSet = new BSShaderTextureSet();
        var privateVector = new NiStringVector();
        privateVector.SetItems(privateItems);
        privateTextureSet.textures = privateVector;

        var privateTextureSetIndex = nifFile.GetHeader().AddBlock(privateTextureSet);
        GC.SuppressFinalize(privateTextureSet);
        shaderProperty.SetTextureSetRef(privateTextureSetIndex);

        nifFile.SetTextureSlot(shape, SnowRetextureDiffuse, 0);
        nifFile.SetTextureSlot(shape, SnowRetextureNormal, 1);
        return true;
    }

    // Neutralizes any pre-existing vertex-color painting on the duplicated mesh's shapes: forces
    // R/G/B to white (1,1,1) on every vertex while leaving Alpha untouched. Vertex colors multiply
    // against the diffuse texture wherever SLSF2_Vertex_Colors is set, so white is the true no-op
    // value - the source mesh's own baked-in tinting (wear, AO, region-specific color grading) was
    // authored against its ORIGINAL texture, not the snow diffuse now baked into this duplicate, so
    // leaving it in place would tint the snow texture in ways nobody intended. Alpha is deliberately
    // left alone since it commonly carries an unrelated meaning (e.g. modulating snow coverage
    // itself). Shapes with no vertex colors at all are skipped outright - absence already behaves
    // like white, nothing to fix. Verified empirically against a real "Sniff" (S'Lanter's NIF
    // Helper) run with matching settings: on 3815 real shapes, every single one converged to exact
    // RGB=(1,1,1) with alpha preserved bit-for-bit. Only called by ProcessType for meshes under a
    // "Landscape" folder (see IsLandscapeMesh) - non-landscape snow matches (e.g. DLC01's Snow Elf
    // ruins architecture) keep their original vertex colors.
    private bool NeutralizeVertexColors(string duplicatedRelativePath, string label)
    {
        var fullPath = Path.Combine(_outputFolder, "meshes", duplicatedRelativePath);

        using var nifFile = new NifFile();
        if (NifIo.Load(nifFile, fullPath) != 0)
        {
            _diagnostics.Add($"'{label}': failed to reload duplicated mesh for vertex color neutralization.");
            return false;
        }

        var neutralized = false;

        foreach (var shape in nifFile.GetShapes())
        {
            if (!shape.HasVertexColors())
            {
                continue;
            }

            var colors = new vectorColor4();
            if (!nifFile.GetColorsForShape(shape, colors) || colors.Count == 0)
            {
                continue;
            }

            for (var i = 0; i < colors.Count; i++)
            {
                var c = colors[i];
                colors[i] = new Color4(1.0f, 1.0f, 1.0f, c.a);
            }

            nifFile.SetColorsForShape(shape, colors);
            neutralized = true;
        }

        if (neutralized)
        {
            var saveOptions = new NifSaveOptions { optimize = false, sortBlocks = false };
            if (NifIo.Save(nifFile, fullPath, saveOptions) != 0)
            {
                _diagnostics.Add($"'{label}': failed to save the mesh after neutralizing vertex colors.");
                return false;
            }

            _vertexColorsNeutralized++;
        }

        return neutralized;
    }

    // Skyrim's own Havok material IDs are CRC32 hashes of the Creation Kit material name, not small
    // sequential integers - values sourced from niftools/nifxml's SkyrimHavokMaterial enum
    // definition. Deliberately conservative: only DIRT and GRASS remap to SNOW. STONE (by far the
    // most common landscape collision material) is left alone - a lot of rock stays visibly exposed
    // under a "snow" mesh variant rather than being fully buried, so blindly remapping it risked
    // being wrong more often than right; discussed directly and scoped down to just these two.
    private const uint DirtMaterial = 3106094762;
    private const uint GrassMaterial = 1848600814;
    private const uint SnowMaterial = 398949039;

    private static readonly Dictionary<uint, uint> CollisionMaterialRemap = new()
    {
        [DirtMaterial] = SnowMaterial,
        [GrassMaterial] = SnowMaterial,
    };

    // "DirtCliffs" meshes are steep, largely-vertical cliff faces - their DIRT chunk represents the
    // exposed cliff face itself, which doesn't realistically accumulate snow on a near-vertical
    // surface, while their GRASS chunk is the flatter, walkable ledge on top, which does. Same
    // ambiguity that already disqualified STONE from the remap table entirely, but here it's
    // narrow enough (one specific mesh family) to exclude by path instead of dropping DIRT
    // everywhere - flat dirt patches/paths elsewhere should still become snow. Discussed directly
    // after a real DirtCliffs mesh showed both its DIRT and GRASS chunks turned to snow.
    private static bool ShouldSkipDirtRemap(string relativeMeshPath) =>
        relativeMeshPath.Contains("dirtcliff", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetSnowEquivalentMaterial(uint material, string relativeMeshPath, out uint newMaterial)
    {
        if (material == DirtMaterial && ShouldSkipDirtRemap(relativeMeshPath))
        {
            newMaterial = 0;
            return false;
        }
        return CollisionMaterialRemap.TryGetValue(material, out newMaterial);
    }

    // Remaps collision materials on the duplicated mesh's collision shapes toward their snow
    // equivalent (see CollisionMaterialRemap), so footstep sounds/impact effects match the now-snowy
    // visual. Works per collision CHUNK, not per mesh: bhkCompressedMeshShapeData (used by nearly
    // all SSE static collision) assigns each chunk its own material via an index into its own
    // materials list, and hkPackedNiTriStripsData (the older tri-strip collision format) assigns
    // material per sub-part the same way - each entry is looked up and remapped independently, so a
    // mesh with e.g. a DIRT chunk next to a WOOD chunk only has the DIRT one touched. Simpler
    // collision shapes (bhkConvexVerticesShape, bhkListShape, bhkNiTriStripsShape, ...) carry a
    // single material directly on the shape itself via GetMaterial()/SetMaterial(), so those are
    // remapped as a whole. Verified empirically via a round-trip test against a real generated mesh:
    // mutating bhkCMSDMaterial.material through the NiVector's items()/SetItems() pair persists
    // correctly across a Save+reload.
    private bool RemapCollisionMaterials(string duplicatedRelativePath, string label)
    {
        var fullPath = Path.Combine(_outputFolder, "meshes", duplicatedRelativePath);

        using var nifFile = new NifFile();
        if (NifIo.Load(nifFile, fullPath) != 0)
        {
            _diagnostics.Add($"'{label}': failed to reload duplicated mesh for collision material remapping.");
            return false;
        }

        var header = nifFile.GetHeader();
        var remapped = false;

        for (uint i = 0; i < header.GetNumBlocks(); i++)
        {
            switch (header.GetBlockById(i))
            {
                case bhkCompressedMeshShapeData cmsd:
                {
                    var items = cmsd.materials.items();
                    var mutated = false;
                    for (var j = 0; j < items.Count; j++)
                    {
                        if (TryGetSnowEquivalentMaterial(items[j].material, duplicatedRelativePath, out var newMat))
                        {
                            items[j].material = newMat;
                            mutated = true;
                        }
                    }
                    if (mutated)
                    {
                        cmsd.materials.SetItems(items);
                        remapped = true;
                    }
                    break;
                }
                case hkPackedNiTriStripsData packed:
                {
                    var items = packed.subPartData.items();
                    var mutated = false;
                    for (var j = 0; j < items.Count; j++)
                    {
                        if (TryGetSnowEquivalentMaterial(items[j].material, duplicatedRelativePath, out var newMat))
                        {
                            items[j].material = newMat;
                            mutated = true;
                        }
                    }
                    if (mutated)
                    {
                        packed.subPartData.SetItems(items);
                        remapped = true;
                    }
                    break;
                }
                case bhkConvexVerticesShape or bhkListShape or bhkNiTriStripsShape or bhkBoxShape
                    or bhkCapsuleShape or bhkMultiSphereShape or bhkConvexTransformShape or bhkConvexListShape:
                {
                    var shape = (bhkShape)header.GetBlockById(i);
                    if (TryGetSnowEquivalentMaterial(shape.GetMaterial(), duplicatedRelativePath, out var newMat))
                    {
                        shape.SetMaterial(newMat);
                        remapped = true;
                    }
                    break;
                }
            }
        }

        if (remapped)
        {
            var saveOptions = new NifSaveOptions { optimize = false, sortBlocks = false };
            if (NifIo.Save(nifFile, fullPath, saveOptions) != 0)
            {
                _diagnostics.Add($"'{label}': failed to save the mesh after remapping collision materials.");
                return false;
            }

            _collisionMaterialsRemapped++;
        }

        return remapped;
    }

    // DirtCliffsRoots-family meshes (DirtCliffs01, DirtCliffs03, DirtCliffsCornerIn01,
    // DirtCliffsCornerOut01, DirtCliffsIsland01, ...) share one texture atlas across several
    // differently-named shapes - roots, an unrelated bottom band, and a "Skirt" shape that samples
    // the "dirt" blend-mask band DirtCliffsSnowVariantGenerator's own composite targets. Only the
    // Skirt shape's own texture gets repointed at the generated snow variant; every other shape in
    // the same mesh (roots, the cliff rock geometry itself) keeps whatever texture it already had -
    // confirmed directly against a real generated mesh: every DirtCliffs-family mesh carries exactly
    // one shape named "Skirt". Always writes the plain vanilla-convention path (see
    // DirtCliffsSnowVariantGenerator.OutputDiffuseRelativePath's own comment for why that's correct
    // for PBR users too).
    private void RetextureDirtCliffsSkirt(string duplicatedRelativePath, string label)
    {
        var fullPath = Path.Combine(_outputFolder, "meshes", duplicatedRelativePath);

        using var nifFile = new NifFile();
        if (NifIo.Load(nifFile, fullPath) != 0)
        {
            _diagnostics.Add($"'{label}': failed to reload duplicated mesh for DirtCliffs skirt retexturing.");
            return;
        }

        var skirtShape = nifFile.GetShapes().FirstOrDefault(s => string.Equals(s.name.get(), "Skirt", StringComparison.OrdinalIgnoreCase));
        if (skirtShape is null)
        {
            _diagnostics.Add($"'{label}': no 'Skirt' shape found for DirtCliffs snow-variant retexturing - left as-is.");
            return;
        }

        if (skirtShape.HasShaderProperty()
            && nifFile.GetHeader().GetBlockById(skirtShape.ShaderPropertyRef().index) is BSLightingShaderProperty skirtShaderProperty
            && nifFile.GetHeader().GetBlockById(skirtShaderProperty.TextureSetRef().index) is BSShaderTextureSet sharedTextureSet)
        {
            // Skirt shares its BSShaderTextureSet block with sibling shapes (e.g. the root pieces) in
            // these meshes - editing it in place would retexture those siblings too. Give Skirt its own
            // private copy of the texture set before touching any slot.
            var privateItems = new vectorNiString();
            foreach (var item in sharedTextureSet.textures.items())
            {
                privateItems.Add(new NiString(item.get()));
            }

            var privateTextureSet = new BSShaderTextureSet();
            var privateVector = new NiStringVector();
            privateVector.SetItems(privateItems);
            privateTextureSet.textures = privateVector;

            var privateTextureSetIndex = nifFile.GetHeader().AddBlock(privateTextureSet);
            // AddBlock hands ownership of the native object to the header's own block vector, but the
            // SWIG wrapper still thinks it owns it too - without this, its finalizer double-frees the
            // native pointer once the header (and its own copy) is torn down, crashing the process.
            GC.SuppressFinalize(privateTextureSet);
            skirtShaderProperty.SetTextureSetRef(privateTextureSetIndex);
        }

        nifFile.SetTextureSlot(skirtShape, DirtCliffsSnowVariantGenerator.OutputDiffuseRelativePath, 0);
        nifFile.SetTextureSlot(skirtShape, DirtCliffsSnowVariantGenerator.OutputNormalRelativePath, 1);

        // Cloning Skirt's texture set above (when it was shared) leaves the original block behind -
        // still present in the file but referenced by nothing, which NifSkope shows as a dimmed,
        // disconnected block. orphanedOnly=true only removes texture sets no shape still points to, so
        // this is a no-op whenever Skirt's texture set wasn't shared to begin with.
        nifFile.GetHeader().DeleteBlockByType("BSShaderTextureSet", true);

        var saveOptions = new NifSaveOptions { optimize = false, sortBlocks = false };
        if (NifIo.Save(nifFile, fullPath, saveOptions) != 0)
        {
            _diagnostics.Add($"'{label}': failed to save the mesh after DirtCliffs skirt retexturing.");
            return;
        }

        _dirtCliffsSkirtShapesRetextured++;
    }

    // For records whose EditorID ends in "Snow"/"SN" - a naming convention some texture packs use
    // for their own hand-authored snow variant of a record - the mesh may still embed the plain
    // (non-snow) "mountainslab01"/"mountainslab02" diffuse rather than that pack's own "...Mask"
    // sibling, which several rock/mountain texture packs ship specifically for use under a snow
    // overlay. Every shape whose diffuse matches gets repointed, not just one named shape - unlike
    // DirtCliffs' single "Skirt" shape, there's no established single-shape convention here. Only
    // ever swaps to a sibling that actually exists on disk - never invents a path a texture pack
    // might not ship.
    private void SwapMountainSlabToMaskVariant(string duplicatedRelativePath, string label)
    {
        var fullPath = Path.Combine(_outputFolder, "meshes", duplicatedRelativePath);

        using var nifFile = new NifFile();
        if (NifIo.Load(nifFile, fullPath) != 0)
        {
            _diagnostics.Add($"'{label}': failed to reload duplicated mesh for MountainSlab mask swap.");
            return;
        }

        var swapped = false;
        foreach (var shape in nifFile.GetShapes())
        {
            if (!shape.HasShaderProperty()
                || nifFile.GetHeader().GetBlockById(shape.ShaderPropertyRef().index) is not BSLightingShaderProperty shaderProperty
                || nifFile.GetHeader().GetBlockById(shaderProperty.TextureSetRef().index) is not BSShaderTextureSet textureSet)
            {
                continue;
            }

            var items = textureSet.textures.items();
            if (items.Count == 0)
            {
                continue;
            }

            var diffuse = items[0].get();
            if (string.IsNullOrEmpty(diffuse)
                || !(diffuse.EndsWith("mountainslab01.dds", StringComparison.OrdinalIgnoreCase)
                    || diffuse.EndsWith("mountainslab02.dds", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var maskPath = diffuse[..^".dds".Length] + "Mask.dds";
            if (!_fileProbe.Exists(maskPath))
            {
                _diagnostics.Add($"'{label}': shape '{shape.name.get()}' uses '{diffuse}' but no '{maskPath}' sibling exists - left as-is.");
                continue;
            }

            // The texture set may be shared with sibling shapes (same pitfall as DirtCliffs' own
            // Skirt shape above) - give this shape its own private copy before touching any slot.
            var privateItems = new vectorNiString();
            foreach (var item in items)
            {
                privateItems.Add(new NiString(item.get()));
            }

            var privateTextureSet = new BSShaderTextureSet();
            var privateVector = new NiStringVector();
            privateVector.SetItems(privateItems);
            privateTextureSet.textures = privateVector;

            var privateTextureSetIndex = nifFile.GetHeader().AddBlock(privateTextureSet);
            GC.SuppressFinalize(privateTextureSet);
            shaderProperty.SetTextureSetRef(privateTextureSetIndex);

            nifFile.SetTextureSlot(shape, maskPath, 0);
            swapped = true;
        }

        if (!swapped)
        {
            return;
        }

        // Same cleanup as RetextureDirtCliffsSkirt above - cloning a shared texture set per matching
        // shape leaves each original block behind once nothing references it anymore.
        nifFile.GetHeader().DeleteBlockByType("BSShaderTextureSet", true);

        var saveOptions = new NifSaveOptions { optimize = false, sortBlocks = false };
        if (NifIo.Save(nifFile, fullPath, saveOptions) != 0)
        {
            _diagnostics.Add($"'{label}': failed to save the mesh after MountainSlab mask swap.");
            return;
        }

        _mountainSlabMaskSwapped++;
    }

    // Shared by ProcessType's early demotion check (reportFailure: false - the record may not even
    // end up in scope, so a malformed path there shouldn't count as a real failure) and its main,
    // in-scope fetch (reportFailure: true - matches the original diagnostic/counter behavior).
    private IReadOnlyList<IAlternateTextureGetter>? TryGetAlternateTextures<TGetter>(
        TGetter record, Func<TGetter, IReadOnlyList<IAlternateTextureGetter>?> getAlternateTextures,
        string typeName, string? editorId, bool reportFailure)
        where TGetter : class, ISkyrimMajorRecordGetter
    {
        try
        {
            return getAlternateTextures(record);
        }
        catch (AssetPathMisalignedException ex)
        {
            if (reportFailure)
            {
                _malformedRecordsSkipped++;
                _diagnostics.Add(
                    $"{typeName} {record.FormKey} ({editorId ?? "<no EditorID>"}): invalid alternate texture path; " +
                    $"mesh was generated but alternate textures were left as-is. {ex.Message}");
            }

            return null;
        }
    }

    private void ProcessType<TGetter>(
        IEnumerable<TGetter> winningOverrides,
        Func<TGetter, string?> getEditorId,
        Func<TGetter, string?> getModelPath,
        Func<TGetter, IReadOnlyList<IAlternateTextureGetter>?> getAlternateTextures,
        Func<TGetter, Model> getOrAddOverrideModel,
        string typeName,
        Action<string, int, int> report)
        where TGetter : class, ISkyrimMajorRecordGetter
    {
        foreach (var record in winningOverrides)
        {
            var editorId = getEditorId(record);
            string? modelPath;
            try
            {
                modelPath = getModelPath(record);
            }
            catch (AssetPathMisalignedException ex)
            {
                _diagnostics.Add(
                    $"{typeName} {record.FormKey} ({editorId ?? "<no EditorID>"}): invalid model path; record skipped. {ex.Message}");
                continue;
            }

            if (modelPath is null
                || _blacklist.IsMeshBlacklisted(modelPath)
                || _blacklist.IsEditorIdBlacklisted(editorId ?? string.Empty))
            {
                continue;
            }

            var isSnowMatch = IsSnow(editorId, modelPath);
            var isLandscape = IsLandscapeMesh(modelPath);

            // Only a path-only match (EditorID itself gives no snow signal) is ever a demotion
            // candidate - fetching every record's own Alternate Textures just to check this would
            // both cost a lookup per record most of which are never touched otherwise, and surface
            // AssetPathMisalignedException diagnostics for records that were never in scope to
            // begin with. Re-fetched (cheaply, from the already-parsed record) below once a record
            // is confirmed in scope, rather than threaded through as a local.
            if (isSnowMatch && !EditorIdSaysSnow(editorId)
                && IsSnowDemotedByAlternateTextures(editorId, TryGetAlternateTextures(record, getAlternateTextures, typeName, editorId, reportFailure: false)))
            {
                isSnowMatch = false;
            }

            // MeshVertexColorMode.All additionally pulls in every OTHER landscape-folder mesh
            // (rocks, cliffs, ... that never matched "snow" on their own) purely so its vertex
            // colors can be cleared too - see MeshVertexColorMode's own doc comment for why.
            // CollisionMaterialMode has no equivalent "All" option - duplicating a non-snow mesh
            // purely to fix its collision material (with no other visible change) wasn't judged
            // worth the extra mesh, so collision remapping only ever happens below, for meshes
            // already being duplicated for the snow match itself.
            var landscapeOnlyInclusion = !isSnowMatch
                && isLandscape
                && _settings.MeshVertexColorMode == MeshVertexColorMode.All;

            if (!isSnowMatch && !landscapeOnlyInclusion)
            {
                continue;
            }

            if (landscapeOnlyInclusion)
            {
                var lsDuplicatedPath = DuplicateMesh(modelPath, editorId, fallbackSuffix: "_novc");
                if (lsDuplicatedPath is null)
                {
                    continue;
                }

                getOrAddOverrideModel(record).File = lsDuplicatedPath;
                NeutralizeVertexColors(lsDuplicatedPath, editorId ?? modelPath);
                _nonSnowLandscapeMeshesIncluded++;
                continue;
            }

            var duplicatedPath = DuplicateMesh(modelPath, editorId);
            if (duplicatedPath is null)
            {
                continue;
            }

            var overrideModel = getOrAddOverrideModel(record);
            overrideModel.File = duplicatedPath;

            var altTexs = TryGetAlternateTextures(record, getAlternateTextures, typeName, editorId, reportFailure: true);
            if (altTexs is { Count: > 0 } && BakeAlternateTextures(duplicatedPath, altTexs, editorId ?? modelPath))
            {
                overrideModel.AlternateTextures = null;
            }

            ApplyAlphaShaderFixups(duplicatedPath, editorId ?? modelPath);
            if (isLandscape && _settings.MeshVertexColorMode != MeshVertexColorMode.None)
            {
                NeutralizeVertexColors(duplicatedPath, editorId ?? modelPath);
            }
            if (isLandscape && _settings.CollisionMaterialMode != CollisionMaterialMode.None)
            {
                RemapCollisionMaterials(duplicatedPath, editorId ?? modelPath);
            }
            if (_settings.GenerateDirtCliffsSnowVariant && modelPath.Contains("dirtcliffs", StringComparison.OrdinalIgnoreCase))
            {
                RetextureDirtCliffsSkirt(duplicatedPath, editorId ?? modelPath);
            }
            if (_settings.SwapMountainSlabMask && editorId is not null
                && (editorId.EndsWith("Snow", StringComparison.OrdinalIgnoreCase) || editorId.EndsWith("SN", StringComparison.OrdinalIgnoreCase)))
            {
                SwapMountainSlabToMaskVariant(duplicatedPath, editorId);
            }
            if (_settings.HideDecalShapes && IsHideDecalShapesEligibleFolder(modelPath))
            {
                HideDecalShapes(duplicatedPath, editorId ?? modelPath);
            }

            _matched++;
        }

        report($"Scanning records... ({typeName} done)", 0, 0);
    }

    private bool IsSnowTexture(IFormLinkGetter<ILandscapeTextureGetter> textureLink)
    {
        if (textureLink.IsNull)
        {
            return false;
        }

        return _env.LinkCache.TryResolve<ILandscapeTextureGetter>(textureLink.FormKey, out var ltex)
            && ltex.Flags.HasValue
            && ltex.Flags.Value.HasFlag(LandscapeTexture.Flag.IsSnow);
    }

    private void PatchLandscapes(Action<string, int, int> report)
    {
        report("Scanning landscape (terrain) records...", 0, 0);

        foreach (var context in _env.LoadOrder.PriorityOrder.Landscape().WinningContextOverrides(_env.LinkCache))
        {
            var record = context.Record;
            if (record.Flags is not { } flags || !flags.HasFlag(Landscape.Flag.VertexColors))
            {
                continue;
            }

            if (_settings.LandscapeVertexColorMode == LandscapeVertexColorMode.SnowOnly)
            {
                // Whole-cell decision, not per-vertex like a gradual/blended tool would do (this
                // mode is a hard clear, not a gamma curve) - a cell counts as "snow" if ANY of its
                // texture layers (base or alpha-blended) is snow-classified via the
                // LandscapeTexture record's own IsSnow flag, the same authoritative source
                // Bethesda's own engine uses, rather than guessing from a texture's file name.
                var isSnowCell = record.Layers.Any(layer => IsSnowTexture(layer.Header!.Texture));
                if (!isSnowCell)
                {
                    continue;
                }
            }

            var overrideRecord = context.GetOrAddAsOverride(_outputMod);
            overrideRecord.Flags = overrideRecord.Flags!.Value & ~Landscape.Flag.VertexColors;
            overrideRecord.VertexColors = null;
            _landscapesPatched++;
        }

        report($"Landscape: {_landscapesPatched} record(s) had their vertex colors cleared.", 0, 0);
    }

    private static GameRelease ToGameRelease(GameType gameType) => gameType switch
    {
        GameType.SkyrimSE => GameRelease.SkyrimSE,
        GameType.SkyrimLE => GameRelease.SkyrimLE,
        _ => throw new ArgumentOutOfRangeException(nameof(gameType), gameType, null),
    };

    private static SkyrimRelease ToSkyrimRelease(GameType gameType) => gameType switch
    {
        GameType.SkyrimSE => SkyrimRelease.SkyrimSE,
        GameType.SkyrimLE => SkyrimRelease.SkyrimLE,
        _ => throw new ArgumentOutOfRangeException(nameof(gameType), gameType, null),
    };
}
