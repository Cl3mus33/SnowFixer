namespace SnowFixer.Core.Configuration;

/// <summary>Controls whether/how LAND (terrain) records' own vertex colors get cleared - entirely
/// separate from the mesh-duplication pipeline, since LAND records have no NIF/Model.File at all.
/// "SnowOnly" decides per-record (not per-vertex, this is a hard clear, not a gradual/blended
/// transform): a cell counts as "snow" if any of its texture layers (base or alpha-blended) is
/// snow-classified via the LandscapeTexture record's own IsSnow flag, the same authoritative source
/// Bethesda's own engine uses, rather than guessing from a texture's file name.</summary>
public enum LandscapeVertexColorMode
{
    None,
    All,
    SnowOnly,
}
