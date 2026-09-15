namespace SnowFixer.Core.Configuration;

/// <summary>
/// Full configuration for an extraction run - mirrors AutoBlend's own PatcherSettings shape
/// (PascalCase keys, default System.Text.Json serialization) so both this native shell and any
/// future shell read/write the same %APPDATA%\SnowFixer\settings.json without a translation
/// layer.
/// </summary>
public sealed class ExtractSettings
{
    /// <summary>GUI language code (SnowFixer_translations/&lt;code&gt;.json, native shell
    /// only); English if missing. Core itself never reads this - it rides along in the shared
    /// settings file purely so the native shell can persist it.</summary>
    public string UiLanguage { get; set; } = "en";

    /// <summary>GUI color theme (native shell only): "system", "light", or "dark".</summary>
    public string UiTheme { get; set; } = "system";

    /// <summary>Skyrim install root (the folder containing "Data", not the Data folder itself) -
    /// matches AutoBlend.Core.Pipeline.PatchOrchestrator's own GameLocation convention.</summary>
    public string GameLocation { get; set; } = string.Empty;

    /// <summary>Skyrim Special Edition or Legendary Edition - same GameType/conversion pattern as
    /// AutoBlend.Core.Configuration.GameType. LE support is new and far less battle-tested than SE
    /// (the vast majority of this tool's own verification has been against real SE modlists).</summary>
    public GameType GameType { get; set; } = GameType.SkyrimSE;

    /// <summary>Folder the extraction writes its generated mod (meshes + SnowFixer.esp +
    /// log) into.</summary>
    public string OutputLocation { get; set; } = string.Empty;

    /// <summary>Whether/how LAND (terrain) records' own vertex colors get cleared - see
    /// <see cref="LandscapeVertexColorMode"/>.</summary>
    public LandscapeVertexColorMode LandscapeVertexColorMode { get; set; } = LandscapeVertexColorMode.None;

    /// <summary>Whether/how landscape-folder MESH vertex colors get cleared - see
    /// <see cref="MeshVertexColorMode"/>. Defaults to SnowOnly, matching this tool's behavior before
    /// this setting existed.</summary>
    public MeshVertexColorMode MeshVertexColorMode { get; set; } = MeshVertexColorMode.SnowOnly;

    /// <summary>Whether/how landscape-folder MESH collision materials get remapped toward their
    /// snow-equivalent (footstep sounds) - see <see cref="CollisionMaterialMode"/>. Defaults to
    /// None - unlike vertex colors, this is a brand new capability with no established prior
    /// behavior to preserve, and it affects gameplay-audible footstep sounds, so it starts opt-in.</summary>
    public CollisionMaterialMode CollisionMaterialMode { get; set; } = CollisionMaterialMode.None;

    /// <summary>Generates a snow variant of Vanaheimr's "landscape\dirtcliffs\dirtcliffsroots01"
    /// texture - see <see cref="Pipeline.DirtCliffsSnowVariantGenerator"/> for the full mechanics.
    /// One specific, hardcoded texture pair rather than a general engine - opt-in, off by
    /// default.</summary>
    public bool GenerateDirtCliffsSnowVariant { get; set; }

    /// <summary>For a record whose EditorID ends in "Snow" or "SN" (case-insensitive), swaps any
    /// shape's diffuse texture ending in "mountainslab01.dds"/"mountainslab02.dds" (case-insensitive)
    /// for its "...Mask.dds" sibling in the same folder, when that sibling actually exists on disk -
    /// see <see cref="Pipeline.ExtractOrchestrator.SwapMountainSlabToMaskVariant"/>. One specific,
    /// hardcoded texture pair rather than a general engine - opt-in, off by default, same reasoning
    /// as <see cref="GenerateDirtCliffsSnowVariant"/>.</summary>
    public bool SwapMountainSlabMask { get; set; }

    /// <summary>None (scan the raw Data folder directly - nothing extra to configure) or
    /// ModOrganizer2 (requires <see cref="Mo2InstancePath"/> to reconstruct its virtual file
    /// system, layering every enabled mod on top of the vanilla Data folder).</summary>
    public ModManagerType ModManager { get; set; } = ModManagerType.None;

    /// <summary>The MO2 instance folder (containing ModOrganizer.ini) - only used when
    /// <see cref="ModManager"/> is ModOrganizer2.</summary>
    public string Mo2InstancePath { get; set; } = string.Empty;

    /// <summary>The MO2 profile to read modlist.txt/plugins.txt from - only used when
    /// <see cref="ModManager"/> is ModOrganizer2. An empty value means use MO2's currently
    /// selected profile (the native launcher normally writes the explicit picker value).</summary>
    public string Mo2ProfileName { get; set; } = string.Empty;

    /// <summary>Wildcard path patterns (e.g. "*\effects\*"). A record whose mesh path matches one
    /// of these is skipped entirely, even if it would otherwise match the snow-detection criteria -
    /// same convention as AutoBlend.Core.Configuration.PatcherSettings.MeshBlacklist. Defaults
    /// refined through real use against a real modlist: "*\trees\*" covers tree DEBRIS (fallen
    /// logs, cut stumps) - real Tree records are never scanned at all (see
    /// ExtractOrchestrator.Run's own record-type list), but debris is authored as plain Static
    /// records living in the same "Landscape\Trees\..." folder and would otherwise slip through
    /// IsSnow's own substring check. The rest (ice, effects, weapons, trophy, snowelfruins, wet,
    /// lod, clutter, architecture, dungeons) rule out categories that matched "snow" in their
    /// EditorID/path but weren't actually candidates worth duplicating/patching - visual effects,
    /// weapon models, trophies, wet-surface variants, LOD meshes, and non-landscape clutter or
    /// architecture/dungeon assets.</summary>
    public List<string> MeshBlacklist { get; set; } = new()
    {
        @"*\trees\*",
        @"*\ice\*",
        @"*\effects\*",
        @"*\weapons\*",
        @"*\trophy\*",
        @"*\snowelfruins\*",
        @"*\wet\*",
        @"*\lod\*",
        @"*\clutter\*",
        @"*\architecture\*",
        @"*\dungeons\*",
    };

    /// <summary>Case-insensitive substrings. A record whose EditorID contains one of these is
    /// skipped entirely - same convention as
    /// AutoBlend.Core.Configuration.PatcherSettings.EditorIdBlacklistKeywords. Defaults refined
    /// through real use: "marker" and "lod" rule out non-visual/LOD records that match "snow" in
    /// name only, "glacier" rules out glacier-specific records that aren't real candidates for
    /// this tool's own snow-mesh fixes.</summary>
    public List<string> EditorIdBlacklistKeywords { get; set; } = new() { "marker", "glacier", "lod" };
}
