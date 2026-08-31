using SnowFixer.Core.Configuration;

namespace SnowFixer.Core.Scanning;

/// <summary>
/// Applies the two exclusion rules from <see cref="ExtractSettings"/>: mesh path wildcards and
/// EditorID substring keywords. Either match is enough to skip a candidate record entirely -
/// ported from AutoBlend.Core.Scanning.BlacklistEvaluator, same convention.
/// </summary>
public sealed class BlacklistEvaluator
{
    private readonly IReadOnlyList<string> _meshPatterns;
    private readonly IReadOnlyList<string> _editorIdKeywords;

    public BlacklistEvaluator(ExtractSettings settings)
    {
        _meshPatterns = settings.MeshBlacklist;
        _editorIdKeywords = settings.EditorIdBlacklistKeywords;
    }

    public bool IsMeshBlacklisted(string meshPath) => WildcardMatcher.MatchesAny(meshPath, _meshPatterns);

    public bool IsEditorIdBlacklisted(string editorId)
    {
        foreach (var keyword in _editorIdKeywords)
        {
            if (editorId.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
