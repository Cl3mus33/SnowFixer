namespace SnowFixer.Core.Pipeline;

public sealed record ExtractResult(
    int RecordsMatched,
    int MeshesDuplicated,
    int MeshesFailed,
    int MalformedRecordsSkipped,
    int ShaderFlagsPatched,
    int VertexColorsNeutralized,
    int CollisionMaterialsRemapped,
    int DecalShapesHidden,
    int DecalCompanionShapesRetextured,
    int IceSnowMaterialsRemoved,
    int NonSnowLandscapeMeshesIncluded,
    int LandscapesPatched,
    IReadOnlyList<string> Diagnostics,
    string? OutputEspPath);
