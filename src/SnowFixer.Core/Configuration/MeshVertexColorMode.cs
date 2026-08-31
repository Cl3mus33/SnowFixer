namespace SnowFixer.Core.Configuration;

/// <summary>Controls whether/how the per-mesh vertex-color neutralization (see
/// ExtractOrchestrator.NeutralizeVertexColors) gets applied to landscape-folder meshes - entirely
/// separate from <see cref="LandscapeVertexColorMode"/>, which clears LAND (terrain) records
/// instead, not mesh geometry. "SnowOnly" touches only the snow-matched duplicates this tool
/// already generates (rocks, icebergs, snow drifts, ...) - those meshes get a fresh copy either way,
/// so neutralizing their vertex colors costs nothing extra. "All" additionally duplicates every
/// OTHER landscape-folder mesh referenced anywhere in the load order (the non-snow originals - plain
/// rocks, cliffs, ...) purely to clear their vertex colors too, so a landscape whose terrain has had
/// its own vertex colors cleared (see <see cref="LandscapeVertexColorMode"/>) doesn't end up sitting
/// underneath object meshes that still carry the old baked-in tinting.</summary>
public enum MeshVertexColorMode
{
    None,
    SnowOnly,
    All,
}
