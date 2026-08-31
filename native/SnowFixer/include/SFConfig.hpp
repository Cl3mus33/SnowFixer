#pragma once

#include <nlohmann/json_fwd.hpp>

#include <filesystem>
#include <string>
#include <vector>

/**
 * @brief Landscape (LAND record) vertex color handling mode. Values (and their on-wire order) must
 * stay in lockstep with SnowFixer.Core.Configuration.LandscapeVertexColorMode - both sides
 * serialize this as a plain integer.
 */
enum class SFLandscapeVertexColorMode : int { None = 0, All = 1, SnowOnly = 2 };

/**
 * @brief Landscape-folder MESH vertex color handling mode - entirely separate from
 * SFLandscapeVertexColorMode, which clears LAND (terrain) records instead of mesh geometry. Values
 * (and their on-wire order) must stay in lockstep with
 * SnowFixer.Core.Configuration.MeshVertexColorMode - both sides serialize this as a plain integer.
 */
enum class SFMeshVertexColorMode : int { None = 0, SnowOnly = 1, All = 2 };

/**
 * @brief Landscape-folder MESH collision material handling mode (footstep sounds/impact effects) -
 * entirely separate from SFMeshVertexColorMode, which clears vertex color tinting instead of
 * touching collision. No "All" option - duplicating a non-snow mesh purely to fix its collision
 * material wasn't judged worth the extra mesh, so this only ever applies to the snow-matched
 * duplicates the tool already generates. Values (and their on-wire order) must stay in lockstep with
 * SnowFixer.Core.Configuration.CollisionMaterialMode - both sides serialize this as a plain integer.
 */
enum class SFCollisionMaterialMode : int { None = 0, SnowOnly = 1 };

/// @brief Mirrors SnowFixer.Core.Configuration.ModManagerType (same integer values).
enum class SFModManagerType : int { None = 0, ModOrganizer2 = 1 };

/**
 * @brief User-configurable parameters for SnowFixer, persisted as JSON. Field set and JSON
 * key names mirror SnowFixer.Core.Configuration.ExtractSettings exactly (PascalCase keys,
 * default System.Text.Json serialization - no naming policy) so both this native shell and a
 * future shell read/write the very same %APPDATA%\SnowFixer\settings.json without any
 * translation layer. Mirrors AutoBlend's own ABParams, trimmed to this tool's much smaller settings
 * surface - no auto-generate allowlist, no PBR toggle.
 */
struct SFParams {
    /// @brief GUI language code (SnowFixer_translations/&lt;code&gt;.json); English if missing.
    std::string uiLanguage = "en";
    /// @brief GUI color theme: "system", "light", or "dark".
    std::string uiTheme = "system";

    /// @brief Skyrim Special Edition install root (the folder containing "Data").
    std::wstring gameLocation;
    /// @brief Folder the extraction writes its generated mod (meshes + SnowFixer.esp + log) into.
    std::wstring outputLocation;

    SFLandscapeVertexColorMode landscapeVertexColorMode = SFLandscapeVertexColorMode::None;
    /// @brief Defaults to SnowOnly, matching this tool's behavior before this setting existed.
    SFMeshVertexColorMode meshVertexColorMode = SFMeshVertexColorMode::SnowOnly;
    /// @brief Defaults to None - a brand new capability with no established prior behavior to
    /// preserve, and it affects gameplay-audible footstep sounds, so it starts opt-in.
    SFCollisionMaterialMode collisionMaterialMode = SFCollisionMaterialMode::None;

    /// @brief None/Vortex (scan the raw Data folder directly - Vortex deploys mods straight into
    /// it via hardlinks, so nothing extra is needed there) or ModOrganizer2 (requires
    /// mo2InstancePath to reconstruct MO2's own virtual file system).
    SFModManagerType modManager = SFModManagerType::None;
    std::wstring mo2InstancePath;
    /// @brief MO2 profile whose modlist.txt/plugins.txt should be layered. The launcher populates
    /// this from the instance's valid profiles; an empty value lets the managed backend fall back
    /// to MO2's selected_profile setting for older callers/config files.
    std::wstring mo2ProfileName;

    /// @brief Meshes matching any wildcard rule here are never duplicated/patched. Defaults to the
    /// single rule Snow Fixer used to hardcode (tree debris meshes living under the same
    /// Landscape\Trees\ folder as real Tree records, which are excluded by record type already).
    std::vector<std::wstring> meshBlacklist { LR"(*\trees\*)" };
    /// @brief Records whose EditorID contains one of these words (case-insensitive) are skipped
    /// entirely. Empty by default - unlike AutoBlend's own equivalent setting, no evidence-based
    /// default rule exists yet for Snow Fixer.
    std::vector<std::wstring> editorIdBlacklistKeywords;

    /// @brief Generates a snow variant of Vanaheimr's own "landscape\dirtcliffs\dirtcliffsroots01"
    /// texture - one specific, hardcoded texture pair rather than a general engine. Off by default.
    bool generateDirtCliffsSnowVariant = false;
};

/**
 * @class SFConfig
 * @brief Loads/saves SFParams to the same settings.json SnowFixer.Core's Exports.cs
 * (de)serializes into an ExtractSettings, so settings persist between runs. Mirrors AutoBlend's own
 * ABConfig (same file I/O pattern) under this project's own name.
 */
class SFConfig {
public:
    static auto load() -> SFParams;
    static void save(const SFParams& params);

    static auto loadFrom(const std::filesystem::path& configFilePath) -> SFParams;
    static void saveTo(const std::filesystem::path& configFilePath, const SFParams& params);

    /// @brief Serializes params to the exact JSON shape
    /// SnowFixer.Core.Configuration.ExtractSettings (de)serializes - used both by
    /// save()/saveTo() and to build the payload start_extract_run expects.
    static auto toJson(const SFParams& params) -> nlohmann::json;

private:
    static auto getConfigPath() -> std::filesystem::path;
};
