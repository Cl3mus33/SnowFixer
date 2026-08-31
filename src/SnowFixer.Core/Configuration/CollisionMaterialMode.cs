namespace SnowFixer.Core.Configuration;

/// <summary>Controls whether landscape-folder MESH collision materials get remapped toward their
/// snow-equivalent (affects footstep sounds/impact effects) - entirely separate from
/// <see cref="MeshVertexColorMode"/>, which clears vertex color tinting instead of touching
/// collision. Unlike MeshVertexColorMode, there is no "All" option here - duplicating a non-snow
/// landscape mesh purely to fix up its collision material (with no other visible change on that
/// mesh) was judged not worth the extra mesh it would add to the output, so this only ever applies
/// to the snow-matched duplicates the tool already generates for other reasons. The remap itself
/// works per collision CHUNK, not per mesh - each chunk's own existing material is looked up
/// independently, so a mesh with e.g. a DIRT chunk next to a WOOD chunk only has the DIRT one
/// touched. Deliberately conservative to start: only DIRT and GRASS remap to SNOW (see
/// ExtractOrchestrator.CollisionMaterialRemap) - STONE (by far the most common landscape collision
/// material) is left alone, since a lot of rock stays visibly exposed under a "snow" mesh variant
/// rather than being fully buried, and blindly remapping it risked being wrong more often than
/// right.</summary>
public enum CollisionMaterialMode
{
    None,
    SnowOnly,
}
