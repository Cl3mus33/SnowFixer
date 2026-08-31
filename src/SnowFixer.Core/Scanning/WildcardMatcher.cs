using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace SnowFixer.Core.Scanning;

/// <summary>
/// Matches paths against simple "*" wildcard patterns (e.g. "*\glass\*"), case-insensitive,
/// treating "/" and "\" as equivalent - ported from AutoBlend.Core.Scanning.WildcardMatcher, same
/// convention.
/// </summary>
public static class WildcardMatcher
{
    // Every pattern passed through here comes from one of a handful of small, fixed
    // ExtractSettings lists (mesh blacklist, EditorID keywords) that never change during a run -
    // but MatchesAny is called once per record scanned, which on a real load order is thousands of
    // calls. RegexOptions.Compiled does real IL-generation/JIT work per construction, so building a
    // fresh Regex on every call would mean redoing that work for the exact same pattern string over
    // and over. Caching the compiled Regex per normalized pattern means each unique pattern is only
    // ever compiled once per process; behavior is identical since BuildRegex is a pure function of
    // the normalized pattern.
    private static readonly ConcurrentDictionary<string, Regex> RegexCache = new();

    public static bool MatchesAny(string path, IEnumerable<string> patterns)
    {
        var normalizedPath = Normalize(path);
        foreach (var pattern in patterns)
        {
            if (GetOrBuildRegex(pattern).IsMatch(normalizedPath))
            {
                return true;
            }
        }
        return false;
    }

    private static string Normalize(string value) => value.Replace('\\', '/').Trim();

    private static Regex GetOrBuildRegex(string pattern)
    {
        var normalizedPattern = Normalize(pattern);
        return RegexCache.GetOrAdd(normalizedPattern, static p =>
        {
            var escaped = Regex.Escape(p).Replace(@"\*", ".*");

            // A leading "*/" must also match when the following segment is the very first path
            // component, with nothing (not even a separator) before it - Bethesda's own relative
            // mesh paths never start with a separator, so a rule like "*\architecture\*" needs to
            // match "architecture\whiterun\house01.nif" (top-level folder), not just something like
            // "dungeons\architecture\house01.nif" (nested). Without this, a rule targeting any
            // top-level folder would silently never exclude anything - reported directly: files
            // kept generating in a folder the user had explicitly blacklisted. Only the leading
            // wildcard needs this treatment; a trailing "*" already matches zero-or-more with no
            // separator required.
            if (escaped.StartsWith(".*/", StringComparison.Ordinal))
            {
                escaped = "(?:.*/)?" + escaped[3..];
            }

            return new Regex($"^{escaped}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        });
    }
}
