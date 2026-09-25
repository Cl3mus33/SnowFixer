using System.Text;
using Mutagen.Bethesda.Plugins.Binary.Streams;
using Mutagen.Bethesda.Strings;
using SnowFixer.Core.Configuration;

namespace SnowFixer.Core.Scanning;

/// <summary>
/// Reads the user's own configured game language ("sLanguage" in Skyrim.ini) so Snow Fixer's own
/// generated overrides carry the SAME localized text the player actually sees in-game, instead of
/// whatever Mutagen falls back to (English) when nothing tells it otherwise - reported directly on
/// Nexus: on a Russian install, an overridden record (Dawnguard's own "SEBench01", localized FULL
/// "Скамья") came back in plain English in SnowFixer.esp, because nothing ever told Mutagen which
/// language to resolve localized strings in. Confirmed directly against a real vanilla record: the
/// same FULL field resolves to "Bench" with no language specified, and to the Russian string when
/// Language.Russian is requested explicitly.
/// </summary>
public static class GameLanguageDetector
{
    // Every value Skyrim's own sLanguage= ini key uses that actually ships its own Strings/*.strings
    // files in the base game/DLC archives - confirmed directly (Skyrim - Interface.bsa's own
    // Strings/ folder: english, french, italian, german, spanish, polish, russian, japanese,
    // chinese). A language Skyrim doesn't localize into (sLanguage set to something else, or a typo)
    // falls through to English, matching what an unlocalized record already always looks like.
    private static readonly Dictionary<string, Language> IniValueToLanguage = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ENGLISH"] = Language.English,
        ["FRENCH"] = Language.French,
        ["ITALIAN"] = Language.Italian,
        ["GERMAN"] = Language.German,
        ["SPANISH"] = Language.Spanish,
        ["POLISH"] = Language.Polish,
        ["RUSSIAN"] = Language.Russian,
        ["JAPANESE"] = Language.Japanese,
        ["CHINESE"] = Language.Chinese,
    };

    // The ANSI code page Skyrim itself reads a NON-localized plugin's inline text with, per game
    // language. Only languages that are NOT plain Western European need an entry: Mutagen's default
    // write encoding is Windows-1252, which cannot hold these scripts at all - confirmed directly
    // against a real Russian install, where Dawnguard's own "SEBench01" FULL ("Скамья") was written
    // into SnowFixer.esp as six literal '?' bytes. Windows-1251 is what Russian plugins ship with, and
    // xEdit (UTF-8 first, then falling back to 1251 for Russian) reads those bytes back correctly.
    private static readonly Dictionary<Language, int> LegacyCodePages = new()
    {
        [Language.Polish] = 1250,
        [Language.Russian] = 1251,
        [Language.Japanese] = 932,
        [Language.Chinese] = 936,
    };

    /// <summary>The encoding to write SnowFixer.esp's inline (non-localized) text with, or null when
    /// Mutagen's own default already suits the language.</summary>
    public static EncodingBundle? GetPlainPluginEncodings(Language language)
    {
        if (!LegacyCodePages.TryGetValue(language, out var codePage))
        {
            return null;
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = new MutagenEncodingWrapper(Encoding.GetEncoding(codePage));
        return new EncodingBundle(encoding, encoding);
    }

    /// <summary>Tries the MO2 profile's own Skyrim.ini first (only present when the instance uses
    /// per-profile inis - MO2's own "profile_local_inis" setting), then the real game's Documents
    /// ini (the only one that exists at all outside MO2, and the fallback MO2 itself uses whenever
    /// per-profile inis are off). Defaults to English - matching the language every previous Snow
    /// Fixer release effectively always used - when neither can be read.</summary>
    public static Language Detect(string? mo2ProfilePath, GameType gameType)
    {
        if (mo2ProfilePath is not null && TryReadFromIni(Path.Combine(mo2ProfilePath, "Skyrim.ini"), out var mo2Language))
        {
            return mo2Language;
        }

        var documentsIniPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "My Games",
            gameType == GameType.SkyrimLE ? "Skyrim" : "Skyrim Special Edition",
            "Skyrim.ini");
        return TryReadFromIni(documentsIniPath, out var documentsLanguage) ? documentsLanguage : Language.English;
    }

    private static bool TryReadFromIni(string iniPath, out Language language)
    {
        language = Language.English;
        if (!File.Exists(iniPath))
        {
            return false;
        }

        foreach (var rawLine in File.ReadAllLines(iniPath))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("sLanguage=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return IniValueToLanguage.TryGetValue(line["sLanguage=".Length..].Trim(), out language);
        }

        return false;
    }
}
