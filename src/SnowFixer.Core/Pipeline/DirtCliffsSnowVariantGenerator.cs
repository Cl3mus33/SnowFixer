using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using SnowFixer.Core.Scanning;

namespace SnowFixer.Core.Pipeline;

/// <summary>Which texture convention a given generated variant follows - determines both its
/// output compression and which sibling files get copied alongside it. Not mutually exclusive at
/// the load-order level: see <see cref="DirtCliffsSnowVariantGenerator.Run"/> for why the vanilla/
/// Complex-Material variant and the True PBR variant are both generated (independently) whenever
/// their own respective source texture exists, rather than one convention "winning".</summary>
internal enum SnowTextureConvention
{
    /// <summary>Plain diffuse + "_n" normal map only, BC7 compression.</summary>
    Vanilla,

    /// <summary>"Complex Material" shader (env map bounced off a "_m" mask, sometimes a "_p"
    /// height map) - lives in the SAME folder as the vanilla diffuse, which itself stays BC7 (only
    /// True PBR's own diffuse needs BC1 sRGB).</summary>
    ComplexMaterial,

    /// <summary>"True PBR" (Community Shaders / PG Patcher) - separate "textures\pbr\..." folder,
    /// BC1 sRGB diffuse, "_n"/"_p"/"_rmaos" siblings, and a PBRNifPatcher json describing material
    /// parameters.</summary>
    TruePbr,
}

/// <summary>
/// Generates a snow variant of exactly one hardcoded texture: Vanaheimr's own
/// "landscape\dirtcliffs\dirtcliffsroots01" diffuse. That texture is a shared atlas - roots,
/// a "dirt" blend-mask band, and an unrelated bottom band all live in the same file, sampled by
/// different parts of the mesh - so this doesn't just swap the whole diffuse: it composites the
/// RGB of "landscape\snow01" (a plain, uniformly-tiled snow texture) with a hand-painted alpha mask
/// (bundled as an embedded resource, see Assets\DirtCliffsRootsAlpha.png) that keeps only the
/// "dirt" band's own alpha shape and zeroes everything else - so the roots and the other band stay
/// fully transparent in the new texture, exactly as they are for the original. Which meshes
/// actually get pointed at this new texture is a separate, later concern (not implemented here);
/// this only ever produces the texture file(s) themselves.
///
/// Deliberately hardcoded to this one texture pair rather than a general "composite any two
/// textures" engine - discussed directly, not worth the extra configuration surface for a single
/// known case.
///
/// Convention-aware (see <see cref="SnowTextureConvention"/>): Vanilla and Complex Material both
/// stay BC7, only True PBR needs BC1 sRGB - recompressing a True PBR diffuse to BC7 (or a vanilla
/// one to BC1) is exactly the mistake AutoBlend's own MissingTextureGenerator already had to fix
/// for its own generated textures. True PBR additionally gets its "PBRNifPatcher\...json" entry
/// mirrored (cloned from snow01's own entry, if any pack in the load order ships one) - same
/// convention AutoBlend.Core.Scanning.MissingTextureGenerator already established. PBRTextureSets
/// json mirroring (keyed by TextureSet EditorID) is NOT done here - there's no EditorID to key it
/// by until a mesh/TextureSet is actually wired to this new texture, which is that later,
/// not-yet-implemented concern above.
/// </summary>
public sealed class DirtCliffsSnowVariantGenerator
{
    [DllImport("SnowFixerTexTools.dll", CharSet = CharSet.Unicode)]
    private static extern int sf_composite_alpha_diffuse(string colorSourcePath, string alphaSourcePath, string dstPath, int isPbr, int isLe);

    private const string VanillaSnowDiffuse = @"textures\landscape\snow01.dds";
    private const string PbrSnowDiffuse = @"textures\pbr\landscape\snow01.dds";
    private const string VanillaOutputDiffuse = @"textures\landscape\dirtcliffs\dirtcliffsroots01_snow.dds";
    private const string PbrOutputDiffuse = @"textures\pbr\landscape\dirtcliffs\dirtcliffsroots01_snow.dds";
    private const string OutputMatchKey = @"landscape\dirtcliffs\dirtcliffsroots01_snow";
    private const string SnowMatchKey = @"landscape\snow01";
    private const string EmbeddedAlphaResourceName = "SnowFixer.Core.Assets.DirtCliffsRootsAlpha.png";

    /// <summary>The vanilla-convention diffuse path always written into a DirtCliffs mesh's own
    /// "Skirt" shape (see ExtractOrchestrator.RetextureDirtCliffsSkirt) - the plain vanilla-style
    /// path is correct even for PBR users, since PG Patcher/Community Shaders intercept by matching
    /// this exact string against their own PBRNifPatcher config (see MirrorPbrNifPatcherJson)
    /// rather than the mesh embedding a different path per convention.</summary>
    public static string OutputDiffuseRelativePath => VanillaOutputDiffuse;

    /// <summary>The "Skirt" shape's normal map sibling, same naming convention CopySiblingMaps
    /// itself uses.</summary>
    public static string OutputNormalRelativePath => VanillaOutputDiffuse[..^".dds".Length] + "_n.dds";

    private readonly IGameFileProbe _fileProbe;
    private readonly string _outputLocation;
    private readonly bool _isLe;
    private readonly List<string> _diagnostics = new();

    /// <summary>True once the diffuse itself was successfully written - siblings/json mirroring are
    /// best-effort on top of that and don't affect this flag.</summary>
    public bool Generated { get; private set; }

    public IReadOnlyList<string> Diagnostics => _diagnostics;

    public DirtCliffsSnowVariantGenerator(IGameFileProbe fileProbe, string outputLocation, bool isLe = false)
    {
        _fileProbe = fileProbe;
        _outputLocation = outputLocation;
        _isLe = isLe;
    }

    /// <summary>Generates the vanilla/Complex-Material variant whenever "landscape\snow01" exists
    /// at all, and SEPARATELY the True PBR variant whenever "textures\pbr\landscape\snow01" also
    /// exists - not an either/or choice keyed on which convention wins. PG Patcher (and Community
    /// Shaders generally) only replaces a shape's materials with its own PBR ones at runtime when
    /// active; the mesh's own base Model.File/TextureSet still needs a working non-PBR texture
    /// underneath for that to degrade gracefully without it - same reason AutoBlend's own generated
    /// "blend" diffuse always keeps a plain vanilla-format companion alongside any PBR one (see
    /// LandscapeFolderDetector.EnsureVanillaCompanion).</summary>
    public void Run()
    {
        string? alphaTempPath = null;
        try
        {
            alphaTempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".png");
            using (var resourceStream = typeof(DirtCliffsSnowVariantGenerator).Assembly.GetManifestResourceStream(EmbeddedAlphaResourceName)
                ?? throw new InvalidOperationException($"Embedded resource '{EmbeddedAlphaResourceName}' not found."))
            using (var dest = File.Create(alphaTempPath))
            {
                resourceStream.CopyTo(dest);
            }

            if (_fileProbe.Exists(VanillaSnowDiffuse))
            {
                var convention = _fileProbe.Exists(@"textures\landscape\snow01_m.dds")
                    ? SnowTextureConvention.ComplexMaterial
                    : SnowTextureConvention.Vanilla;
                GenerateOne(VanillaSnowDiffuse, VanillaOutputDiffuse, isPbr: false, convention, alphaTempPath);
            }
            else
            {
                _diagnostics.Add($"DirtCliffsRoots snow variant: '{VanillaSnowDiffuse}' not found in the load order - vanilla/Complex Material variant skipped.");
            }

            if (_fileProbe.Exists(PbrSnowDiffuse))
            {
                GenerateOne(PbrSnowDiffuse, PbrOutputDiffuse, isPbr: true, SnowTextureConvention.TruePbr, alphaTempPath);
            }
        }
        catch (Exception ex)
        {
            _diagnostics.Add($"DirtCliffsRoots snow variant: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            TryDelete(alphaTempPath);
        }
    }

    private void GenerateOne(string colorSourceRelative, string outputRelative, bool isPbr, SnowTextureConvention convention, string alphaTempPath)
    {
        string? extractedColorTempPath = null;
        try
        {
            using (var source = _fileProbe.OpenRead(colorSourceRelative))
            {
                extractedColorTempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dds");
                using var dest = File.Create(extractedColorTempPath);
                source.CopyTo(dest);
            }

            var outputFullPath = Path.Combine(_outputLocation, outputRelative);
            Directory.CreateDirectory(Path.GetDirectoryName(outputFullPath)!);

            var resultCode = sf_composite_alpha_diffuse(extractedColorTempPath, alphaTempPath, outputFullPath, isPbr ? 1 : 0, _isLe ? 1 : 0);
            if (resultCode != 0)
            {
                _diagnostics.Add($"DirtCliffsRoots snow variant: SnowFixerTexTools failed for '{colorSourceRelative}' (code {resultCode}).");
                return;
            }

            Generated = true;
            CopySiblingMaps(colorSourceRelative, outputFullPath, convention);

            if (isPbr)
            {
                MirrorPbrNifPatcherJson();
            }
        }
        catch (DllNotFoundException ex)
        {
            _diagnostics.Add($"DirtCliffsRoots snow variant disabled: SnowFixerTexTools.dll not found ({ex.Message}).");
        }
        catch (Exception ex)
        {
            _diagnostics.Add($"DirtCliffsRoots snow variant: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            TryDelete(extractedColorTempPath);
        }
    }

    private void CopySiblingMaps(string colorSourceRelative, string outputDiffuseFullPath, SnowTextureConvention convention)
    {
        var colorBase = colorSourceRelative[..^".dds".Length];
        var outputBase = Path.Combine(Path.GetDirectoryName(outputDiffuseFullPath)!, Path.GetFileNameWithoutExtension(outputDiffuseFullPath));

        CopySiblingIfExists(colorBase + "_n.dds", outputBase + "_n.dds");

        switch (convention)
        {
            case SnowTextureConvention.ComplexMaterial:
                CopySiblingIfExists(colorBase + "_m.dds", outputBase + "_m.dds");
                CopySiblingIfExists(colorBase + "_m.json", outputBase + "_m.json");
                CopySiblingIfExists(colorBase + "_p.dds", outputBase + "_p.dds");
                break;
            case SnowTextureConvention.TruePbr:
                CopySiblingIfExists(colorBase + "_p.dds", outputBase + "_p.dds");
                CopySiblingIfExists(colorBase + "_rmaos.dds", outputBase + "_rmaos.dds");
                break;
        }
    }

    private void CopySiblingIfExists(string relativeSourcePath, string destFullPath)
    {
        if (!_fileProbe.Exists(relativeSourcePath))
        {
            return;
        }

        try
        {
            using var source = _fileProbe.OpenRead(relativeSourcePath);
            using var dest = File.Create(destFullPath);
            source.CopyTo(dest);
        }
        catch (Exception ex)
        {
            _diagnostics.Add($"DirtCliffsRoots snow variant: failed to copy sibling '{relativeSourcePath}' ({ex.GetType().Name}: {ex.Message}).");
        }
    }

    /// <summary>Searches every json under "PBRNifPatcher\" anywhere in the load order for snow01's
    /// own entry (packs commonly bundle many entries into one arbitrarily-named combined file
    /// rather than one file per texture - see AutoBlend's own equivalent for the same search) and
    /// clones it onto our new identity, same material parameters, only the match key updated. Any
    /// explicit slot2/slot4/slot6 override on the donor entry is dropped, not carried over - those
    /// would otherwise still point at snow01's OWN sibling files; PG Patcher's own default
    /// resolution ("{matchedPath}_n/_p/_rmaos.dds") already finds the real siblings this generator
    /// just copied right next to the new diffuse, at the exact conventional path it expects them
    /// at. Falls back to a generic default entry if nothing describes snow01 either.</summary>
    private void MirrorPbrNifPatcherJson()
    {
        if (TryFindExistingPbrEntry(OutputMatchKey) is not null)
        {
            // Some pack already ships a dedicated entry for our own new identity - nothing to add.
            return;
        }

        var donorEntry = TryFindExistingPbrEntry(SnowMatchKey);
        var entry = donorEntry is not null ? CloneEntryWithNewMatchKey(donorEntry, OutputMatchKey) : BuildDefaultEntry(OutputMatchKey);

        try
        {
            var destPath = Path.Combine(_outputLocation, "PBRNifPatcher", "SnowFixer_DirtCliffsRootsSnow.json");
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            var array = new JsonArray { entry };
            File.WriteAllText(destPath, array.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _diagnostics.Add($"DirtCliffsRoots snow variant: could not write PBRNifPatcher json ({ex.GetType().Name}: {ex.Message}).");
        }
    }

    private JsonObject? TryFindExistingPbrEntry(string matchValue)
    {
        var normalized = NormalizeMatchValue(matchValue);
        var normalizedBareName = NormalizeMatchValue(Path.GetFileName(matchValue));

        foreach (var jsonPath in _fileProbe.EnumerateFiles("PBRNifPatcher", ".json"))
        {
            try
            {
                using var stream = _fileProbe.OpenRead(jsonPath);
                var json = JsonNode.Parse(stream);
                var entries = json as JsonArray ?? json?["entries"] as JsonArray;
                if (entries is null)
                {
                    continue;
                }

                foreach (var candidate in entries)
                {
                    if (candidate is not JsonObject obj)
                    {
                        continue;
                    }

                    var value = (obj["texture"] ?? obj["match_diffuse"])?.GetValue<string>();
                    if (string.IsNullOrEmpty(value))
                    {
                        continue;
                    }

                    var normalizedValue = NormalizeMatchValue(value);
                    if (normalizedValue == normalized || normalizedValue == normalizedBareName)
                    {
                        return obj;
                    }
                }
            }
            catch
            {
                // Best-effort: one unparsable json (malformed, not actually PG Patcher's schema)
                // shouldn't stop the search across the rest of the load order.
            }
        }

        return null;
    }

    private static string NormalizeMatchValue(string value)
    {
        var noExt = Path.HasExtension(value) ? value[..^Path.GetExtension(value).Length] : value;
        return noExt.Replace('/', '\\').Trim('\\');
    }

    private static JsonObject CloneEntryWithNewMatchKey(JsonObject sourceEntry, string newMatchKey)
    {
        var clone = (JsonNode.Parse(sourceEntry.ToJsonString()) as JsonObject)!;
        clone.Remove("rename");
        clone.Remove("slot2");
        clone.Remove("slot4");
        clone.Remove("slot6");
        clone.Remove("lock_normal");
        clone.Remove("lock_parallax");
        clone.Remove("lock_rmaos");
        if (clone.ContainsKey("texture"))
        {
            clone["texture"] = newMatchKey;
        }
        else
        {
            clone["match_diffuse"] = newMatchKey;
        }

        return clone;
    }

    /// <summary>Same generic TruePBR default AutoBlend's own MissingTextureGenerator falls back to
    /// when nothing anywhere describes the source texture either - verified directly against a real
    /// PBR pack's own "dirtcliffsroots01.json", which uses these exact values.</summary>
    private static JsonObject BuildDefaultEntry(string matchValue) => new()
    {
        ["match_diffuse"] = matchValue,
        ["emissive"] = false,
        ["parallax"] = true,
        ["subsurface_foliage"] = false,
        ["subsurface"] = false,
        ["specular_level"] = 0.04,
        ["subsurface_color"] = new JsonArray(1, 1, 1),
        ["roughness_scale"] = 1,
        ["subsurface_opacity"] = 1,
        ["smooth_angle"] = 75,
        ["displacement_scale"] = 1,
    };

    private static void TryDelete(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch
        {
            // best-effort cleanup of the temp extraction
        }
    }
}
