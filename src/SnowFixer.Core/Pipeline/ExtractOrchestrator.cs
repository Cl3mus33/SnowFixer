using Mutagen.Bethesda;
using Mutagen.Bethesda.Environments;
using Mutagen.Bethesda.Plugins.Exceptions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using nifly;
using SnowFixer.Core.Configuration;
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


    private readonly ExtractSettings _settings;
    private readonly string _dataFolder;
    private readonly string _outputFolder;
    private readonly BlacklistEvaluator _blacklist;

    private IGameEnvironment<ISkyrimMod, ISkyrimModGetter> _env = null!;
    private IGameFileProbe _fileProbe = null!;
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
            ? new Mo2InstanceReader(_settings.Mo2InstancePath, mo2ProfileName, gameRelease)
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

        var envDataFolder = materializedLoadOrder?.DataFolder ?? _dataFolder;
        var envBuilder = GameEnvironment.Typical.Builder<ISkyrimMod, ISkyrimModGetter>(gameRelease)
            .WithTargetDataFolder(envDataFolder);
        _env = materializedLoadOrder is not null
            ? envBuilder.WithLoadOrder(materializedLoadOrder.LoadOrder.ToArray()).Build()
            : envBuilder.Build();
        using var envDisposable = _env;

        _fileProbe = mo2Reader is not null
            ? new Mo2ModlistFileProbe(mo2Reader, _dataFolder, gameRelease)
            : new ArchiveAwareFileProbe(_dataFolder, gameRelease);
        using var fileProbeDisposable = _fileProbe;

        _outputMod = new SkyrimMod(new ModKey("SnowFixer", ModType.Plugin), skyrimRelease);

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

        if (_settings.LandscapeVertexColorMode != LandscapeVertexColorMode.None)
        {
            PatchLandscapes(Report);
        }

        var dirtCliffsSnowVariantGenerated = false;
        if (_settings.GenerateDirtCliffsSnowVariant)
        {
            Report("Generating DirtCliffsRoots snow variant texture...");
            var textureGenerator = new DirtCliffsSnowVariantGenerator(_fileProbe, _outputFolder);
            textureGenerator.Run();
            dirtCliffsSnowVariantGenerated = textureGenerator.Generated;
            _diagnostics.AddRange(textureGenerator.Diagnostics);
        }

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
            $"Decal-flagged shapes hidden: {result.DecalShapesHidden}",
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

    private static bool IsSnow(string? editorId, string? modelPath) =>
        (editorId is not null && (editorId.Contains("snow", StringComparison.OrdinalIgnoreCase) || EndsWithSnowSuffix(editorId)))
        || (modelPath is not null && modelPath.Contains("snow", StringComparison.OrdinalIgnoreCase));

    private static readonly string[] LandscapeMeshFolderPatterns = { @"*\landscape\*" };

    // Vertex color neutralization is restricted to meshes living somewhere under a "Landscape"
    // folder - matches vanilla's own Landscape\..., as well as DLC-specific variants like
    // DLC01\Landscape\... (Dawnguard icebergs/glaciers), DLC02\Landscape\... (Solstheim trees), and
    // _ResourcePack\Landscape\... - reported directly: non-landscape snow matches (e.g. DLC01's
    // Snow Elf ruins under DLC01\Architecture\SnowElfRuins\...) were also having their vertex colors
    // stripped, which wasn't wanted; only true landscape/terrain-type meshes (rocks, icebergs, snow
    // drifts, trees) should be affected. Relies on WildcardMatcher's leading-wildcard fix to also
    // catch the vanilla case where "Landscape" is the very first path segment.
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
        if (nifFile.Load(fullPath) != 0)
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
            if (nifFile.Save(fullPath, saveOptions) != 0)
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
        if (nifFile.Load(fullPath) != 0)
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
            if (nifFile.Save(fullPath, saveOptions) != 0)
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
    // (nexusmods.com/skyrimspecialedition/mods/131170), whose author moved away from these same
    // shapes because Skyrim's own dynamic snow shaders (Simplicity of Snow, BDS3, ...) render via the
    // decal pipeline and z-fight against them. That mod's own fix edits the plugin's Alternate
    // Textures to drop/repoint the shape - this instead just hides it in the mesh itself (NiAVObject
    // Hidden flag, not deleted), so block indices never move and nothing in the plugin needs to
    // change at all.
    //
    // Detection is purely by diffuse texture file name (DecalTextureFileNames), not by any shader
    // flag - confirmed empirically against real vanilla meshes that the NIF's own SLSF1_Decal/
    // Dynamic_Decal bits do NOT reliably mark these shapes (e.g. vanilla RockCliff08 has two shapes
    // both textured "Rocks01.dds", an opaque base pass and a decal-flagged pass on top of it - only
    // the second carries the flag, yet both need hiding; vanilla RockCliff01 has only the flagged
    // one and no Rocks01 twin). Every shape using one of these textures is hidden, flag or no flag.
    private bool HideDecalShapes(string duplicatedRelativePath, string label)
    {
        var fullPath = Path.Combine(_outputFolder, "meshes", duplicatedRelativePath);

        using var nifFile = new NifFile();
        if (nifFile.Load(fullPath) != 0)
        {
            _diagnostics.Add($"'{label}': failed to reload duplicated mesh for decal shape hiding.");
            return false;
        }

        var header = nifFile.GetHeader();
        var patched = false;

        foreach (var shape in nifFile.GetShapes())
        {
            if (!TryGetLightingShaderProperty(header, shape, out var shaderProperty)
                || !TryGetDiffuseTexture(header, shaderProperty!, out var diffuse)
                || Array.IndexOf(DecalTextureFileNames, Path.GetFileName(diffuse).ToLowerInvariant()) < 0)
            {
                continue;
            }

            if ((shape.flags & HiddenBit) == 0)
            {
                shape.flags |= HiddenBit;
                patched = true;
            }
        }

        if (patched)
        {
            var saveOptions = new NifSaveOptions { optimize = false, sortBlocks = false };
            if (nifFile.Save(fullPath, saveOptions) != 0)
            {
                _diagnostics.Add($"'{label}': failed to save the mesh after hiding decal shapes.");
                return false;
            }

            _decalShapesHidden++;
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
        if (nifFile.Load(fullPath) != 0)
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
            if (nifFile.Save(fullPath, saveOptions) != 0)
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
        if (nifFile.Load(fullPath) != 0)
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
            if (nifFile.Save(fullPath, saveOptions) != 0)
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
        if (nifFile.Load(fullPath) != 0)
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
        if (nifFile.Save(fullPath, saveOptions) != 0)
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
        if (nifFile.Load(fullPath) != 0)
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
        if (nifFile.Save(fullPath, saveOptions) != 0)
        {
            _diagnostics.Add($"'{label}': failed to save the mesh after MountainSlab mask swap.");
            return;
        }

        _mountainSlabMaskSwapped++;
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

            IReadOnlyList<IAlternateTextureGetter>? altTexs;
            try
            {
                altTexs = getAlternateTextures(record);
            }
            catch (AssetPathMisalignedException ex)
            {
                _malformedRecordsSkipped++;
                _diagnostics.Add(
                    $"{typeName} {record.FormKey} ({editorId ?? "<no EditorID>"}): invalid alternate texture path; " +
                    $"mesh was generated but alternate textures were left as-is. {ex.Message}");
                altTexs = null;
            }

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
