namespace SnowFixer.Core.Pipeline;

public sealed record ExtractResult(
    int RecordsMatched,
    int MeshesDuplicated,
    int MeshesFailed,
    int MalformedRecordsSkipped,
    int AlternateTexturesBaked,
    int AlternateTexturesFailed,
    int ShaderFlagsPatched,
    int VertexColorsNeutralized,
    int CollisionMaterialsRemapped,
    int DirtCliffsSkirtShapesRetextured,
    int MountainSlabMaskSwapped,
    int NonSnowLandscapeMeshesIncluded,
    int LandscapesPatched,
    bool DirtCliffsSnowVariantGenerated,
    IReadOnlyList<string> Diagnostics,
    string? OutputEspPath);
