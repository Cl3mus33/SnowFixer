using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Archives.Exceptions;

namespace SnowFixer.Core.Scanning;

/// <summary>
/// Parses a Mod Organizer 2 profile's modlist.txt and resolves a relative Data-folder path
/// against its virtual file system: the "overwrite" folder first, then each enabled mod folder's
/// own loose files in priority order, then (only if no loose file anywhere provides it) each
/// enabled mod's own BSA/BA2 archives, also in priority order. MO2 writes modlist.txt in the
/// OPPOSITE order from its own mod list panel (file top = panel bottom) - verified directly
/// against a real instance's modlist.txt alongside a screenshot of its panel, four mods deep. The
/// panel's own bottom wins conflicts, so the file's own top is highest priority - the first
/// enabled line in the file is checked first here. Loose always beats archived, matching Skyrim's
/// own engine behavior - a mod that packs assets into its own BSA rather than shipping them loose
/// (e.g. Beyond Skyrim's BSAssets.bsa/BSHeartland.bsa) was previously invisible to this reader
/// entirely, since only loose files were ever checked.
/// </summary>
public sealed class Mo2InstanceReader : IDisposable
{
    /// <summary>Profiles that MO2 can actually use for a load order. A directory is listed only
    /// when it contains modlist.txt, since an arbitrary folder under profiles is not a runnable
    /// MO2 profile.</summary>
    public sealed record ProfileDiscovery(IReadOnlyList<string> Profiles, string? SelectedProfile);

    /// <summary>The path the user gave us — MO2's own notion of "the instance" (where
    /// ModOrganizer.ini lives). For a "global" instance this is under %LOCALAPPDATA%\ModOrganizer\
    /// and does NOT necessarily contain mods/profiles/overwrite itself — see <see cref="DataRoot"/>.</summary>
    public string InstancePath { get; }

    /// <summary>Where mods/profiles/overwrite/downloads actually live. Equal to
    /// <see cref="InstancePath"/> unless ModOrganizer.ini sets a custom base_directory (common
    /// when mods are stored on a different drive than the instance's AppData metadata).</summary>
    public string DataRoot { get; }

    public string ModsRoot { get; }
    public string? OverwriteFolder { get; }
    public IReadOnlyList<string> EnabledModFoldersHighToLowPriority { get; }

    private readonly GameRelease _gameRelease;
    private IReadOnlyList<IArchiveReader>? _modArchiveReaders;
    private readonly Dictionary<IArchiveReader, Dictionary<string, IArchiveFile>> _archiveIndexes = new();

    // Needed only for ManualArchiveExtractor's own workaround (see TryResolveLooseOrArchived) - it
    // has to reopen the physical archive file itself, and IArchiveReader has no public property
    // exposing that back.
    private readonly Dictionary<IArchiveReader, string> _readerArchivePaths = new();

    // Reported straight to the caller rather than swallowed - see ArchiveAwareFileProbe's own
    // identical field for the real report this closes ("every static reverts to vanilla" with no
    // error anywhere, traced to a mod's own archive silently failing to open). Optional (defaults
    // to a no-op) so existing callers that don't care still compile unchanged.
    private readonly Action<string> _onDiagnostic;

    public Mo2InstanceReader(string instancePath, string profileName, GameRelease gameRelease, Action<string>? onDiagnostic = null)
    {
        _onDiagnostic = onDiagnostic ?? (_ => { });
        instancePath = ResolveInstancePath(instancePath);
        InstancePath = instancePath;
        _gameRelease = gameRelease;
        DataRoot = ResolveDataRoot(instancePath);
        ModsRoot = Path.Combine(DataRoot, "mods");

        var overwrite = Path.Combine(DataRoot, "overwrite");
        OverwriteFolder = Directory.Exists(overwrite) ? overwrite : null;

        var modlistPath = Path.Combine(DataRoot, "profiles", profileName, "modlist.txt");
        if (!File.Exists(modlistPath))
        {
            throw new FileNotFoundException(DescribeMissingModlist(instancePath, profileName, modlistPath), modlistPath);
        }

        // ParseEnabledMods reads the file top-to-bottom, exactly the order it's written in. Verified
        // directly against a real instance's own modlist.txt alongside a screenshot of its MO2 mod
        // panel: the file is written in the OPPOSITE order from the panel (file top = panel bottom),
        // not the same order as an earlier version of this comment assumed. Since the panel's own
        // bottom wins conflicts, that makes the file's own TOP the highest-priority mod - so the
        // parsed top-to-bottom list already IS high-to-low priority, with no reversal needed.
        EnabledModFoldersHighToLowPriority = ParseEnabledMods(modlistPath)
            .Select(name => Path.Combine(ModsRoot, name))
            .Where(Directory.Exists)
            .ToList();
    }

    public string ProfilePath(string profileName) => Path.Combine(DataRoot, "profiles", profileName);

    /// <summary>Active plugin filenames (esp/esm/esl) from plugins.txt, in load-order — top to bottom of the file.</summary>
    public IReadOnlyList<string> ReadActivePlugins(string profileName)
    {
        var pluginsPath = Path.Combine(ProfilePath(profileName), "plugins.txt");
        var result = new List<string>();
        if (!File.Exists(pluginsPath))
        {
            return result;
        }

        // The "*"-prefix-marks-active plugins.txt format only exists for games with light-plugin
        // (ESL) support - introduced alongside it. Skyrim LE predates that entirely: verified
        // directly against a real MO2 LE profile's own plugins.txt, every line is a plain filename
        // with no prefix at all, and simply being listed means active (MO2 never writes a disabled
        // plugin into this file for LE the way it can for SE). Using the SE-only "*" check here
        // silently produced an empty active list for LE - every mod-added plugin invisible, only
        // the hardcoded implicit base masters remaining.
        var requiresActiveMarker = _gameRelease == GameRelease.SkyrimSE;

        foreach (var rawLine in File.ReadAllLines(pluginsPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (requiresActiveMarker)
            {
                if (!line.StartsWith('*'))
                {
                    continue;
                }

                result.Add(line[1..].Trim());
            }
            else
            {
                result.Add(line);
            }
        }
        return result;
    }

    /// <summary>Loose-file resolution only (overwrite, then each enabled mod folder in priority
    /// order) - does NOT consult any mod's own BSA/BA2 archives. Used for plugins (.esp/.esm/.esl),
    /// which are never packed into archives, so archive-awareness would only add cost there. For
    /// meshes/textures/anything that CAN legitimately live in a mod's own archive, use
    /// <see cref="TryResolveLooseOrArchived"/> instead.</summary>
    public bool TryResolve(string relativeDataPath, out string fullPath)
    {
        if (OverwriteFolder is not null)
        {
            var overwritePath = Path.Combine(OverwriteFolder, relativeDataPath);
            if (File.Exists(overwritePath))
            {
                fullPath = overwritePath;
                return true;
            }
        }

        foreach (var modFolder in EnabledModFoldersHighToLowPriority)
        {
            var candidate = Path.Combine(modFolder, relativeDataPath);
            if (File.Exists(candidate))
            {
                fullPath = candidate;
                return true;
            }
        }

        fullPath = string.Empty;
        return false;
    }

    /// <summary>Lists every loose file matching <paramref name="extension"/> under
    /// <paramref name="relativeFolder"/> (recursive) across the whole modlist - the overwrite
    /// folder, then every enabled mod folder in priority order - deduplicated by relative path so a
    /// path multiple mods provide is only reported once, for whichever copy wins (same priority
    /// order <see cref="TryResolve"/> itself uses). Archives are not searched: content this is used
    /// for (PBRNifPatcher json configs) is never packed into BSA/BA2 in practice.</summary>
    public IEnumerable<string> EnumerateLooseFiles(string relativeFolder, string extension)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (OverwriteFolder is not null)
        {
            foreach (var relativePath in EnumerateUnder(OverwriteFolder, relativeFolder, extension))
            {
                if (seen.Add(relativePath))
                {
                    yield return relativePath;
                }
            }
        }

        foreach (var modFolder in EnabledModFoldersHighToLowPriority)
        {
            foreach (var relativePath in EnumerateUnder(modFolder, relativeFolder, extension))
            {
                if (seen.Add(relativePath))
                {
                    yield return relativePath;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateUnder(string root, string relativeFolder, string extension)
    {
        var full = Path.Combine(root, relativeFolder);
        if (!Directory.Exists(full))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(full, "*" + extension, SearchOption.AllDirectories))
        {
            yield return Path.GetRelativePath(root, file);
        }
    }

    /// <summary>Same loose-file resolution as <see cref="TryResolve"/>, then - only if no mod
    /// provides a loose override - checks every enabled mod's own BSA/BA2 archives, also in
    /// priority order (matching Skyrim's own engine rule: loose always beats archived, regardless
    /// of which mod either comes from). A mod that ships assets packed into its own archive rather
    /// than loose (e.g. Beyond Skyrim's BSAssets.bsa) resolves through this path.</summary>
    public bool TryResolveLooseOrArchived(string relativeDataPath, out Stream stream)
    {
        if (TryResolve(relativeDataPath, out var loosePath))
        {
            stream = File.OpenRead(loosePath);
            return true;
        }

        foreach (var reader in GetOrBuildModArchiveReaders())
        {
            var index = GetOrBuildArchiveIndex(reader);
            if (index.TryGetValue(relativeDataPath, out var archiveFile))
            {
                try
                {
                    stream = archiveFile.AsStream();
                    return true;
                }
                catch (ArchiveException) when (_readerArchivePaths.TryGetValue(reader, out var archivePath)
                    && ManualArchiveExtractor.TryExtract(archivePath, archiveFile, out var bytes))
                {
                    stream = new MemoryStream(bytes!);
                    return true;
                }
            }
        }

        stream = Stream.Null;
        return false;
    }

    public bool ExistsLooseOrArchived(string relativeDataPath)
    {
        if (TryResolve(relativeDataPath, out _))
        {
            return true;
        }

        foreach (var reader in GetOrBuildModArchiveReaders())
        {
            if (GetOrBuildArchiveIndex(reader).ContainsKey(relativeDataPath))
            {
                return true;
            }
        }

        return false;
    }

    // Lazy and built once: a modlist can carry hundreds of mods, most without their own archives,
    // and each IArchiveReader.Files enumeration is a real cost - only pay it for instances that
    // actually construct this reader, and only once regardless of how many paths get queried.
    private IReadOnlyList<IArchiveReader> GetOrBuildModArchiveReaders()
    {
        if (_modArchiveReaders is not null)
        {
            return _modArchiveReaders;
        }

        var readers = new List<IArchiveReader>();
        foreach (var modFolder in EnabledModFoldersHighToLowPriority)
        {
            IEnumerable<string> archivePaths;
            try
            {
                archivePaths = Directory.EnumerateFiles(modFolder, "*.bsa")
                    .Concat(Directory.EnumerateFiles(modFolder, "*.ba2"));
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var archivePath in archivePaths)
            {
                try
                {
                    var reader = Archive.CreateReader(_gameRelease, archivePath);
                    readers.Add(reader);
                    _readerArchivePaths[reader] = archivePath;
                }
                catch (Exception ex)
                {
                    // Skip archives Mutagen can't parse (corrupt/unsupported format) rather than
                    // failing the whole run over one bad file.
                    _onDiagnostic($"Archive '{archivePath}' could not be opened and was skipped entirely - "
                        + $"every file it would have provided falls back to a lower-priority source instead. "
                        + $"{ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        _modArchiveReaders = readers;
        return readers;
    }

    private Dictionary<string, IArchiveFile> GetOrBuildArchiveIndex(IArchiveReader reader)
    {
        if (_archiveIndexes.TryGetValue(reader, out var existing))
        {
            return existing;
        }

        // Case-insensitive: real-world archives frequently mix casing between what a plugin's own
        // Model.File path uses and what got packed into the archive - see ArchiveAwareFileProbe's
        // own identical indexing for the same fix applied to the vanilla game's own BSAs.
        var index = new Dictionary<string, IArchiveFile>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // reader.Files is lazy - the first access is what actually parses the archive's own
            // folder/file record tables, so a malformed one (reported directly: "Arithmetic
            // operation resulted in an overflow" from deep inside Mutagen's own BsaReader) throws
            // here, not at Archive.CreateReader time (already guarded in
            // GetOrBuildModArchiveReaders). Same resilience as ArchiveAwareFileProbe's own
            // identical indexing: whatever this archive already indexed before hitting the bad
            // part stays usable, but one bad mod archive must not abort the whole run.
            foreach (var archiveFile in reader.Files)
            {
                index.TryAdd(archiveFile.Path, archiveFile);
            }
        }
        catch (Exception ex)
        {
            // Leave the index as whatever was collected before the failure (possibly empty).
            _readerArchivePaths.TryGetValue(reader, out var archivePath);
            _onDiagnostic($"Archive '{archivePath ?? reader.ToString()}' stopped indexing partway through "
                + $"(its own file listing is malformed past that point) - only what was already found in "
                + $"it before this is usable. {ex.GetType().Name}: {ex.Message}");
        }

        _archiveIndexes[reader] = index;
        return index;
    }

    public void Dispose()
    {
        if (_modArchiveReaders is null)
        {
            return;
        }

        foreach (var reader in _modArchiveReaders)
        {
            (reader as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// MO2 stores each "global" instance's ModOrganizer.ini under %LOCALAPPDATA%\ModOrganizer\{name}\,
    /// which is what MO2's own UI calls "the instance" — but mods/profiles/overwrite only live
    /// directly inside that folder if the instance never set a custom base_directory (common when
    /// someone wants mods on a bigger/faster drive than the OS one). Read the ini's base_directory
    /// key when present and follow it; otherwise assume a portable instance where everything is
    /// co-located with ModOrganizer.ini.
    /// </summary>
    /// <summary>
    /// A user pointing "MO2 Instance Path" at a folder that merely CONTAINS their instance (the MO2
    /// install folder, or the parent of a custom base directory) used to end in an empty profile
    /// list and a bare "No modlist.txt found for profile 'Default'" - reported directly on Nexus.
    /// If the given folder already is an instance (has ModOrganizer.ini or a profiles folder) it is
    /// returned unchanged; otherwise, when exactly one instance can be found inside it (a direct
    /// subfolder that looks like one, or a global instance under %LOCALAPPDATA%\ModOrganizer whose
    /// base_directory lives inside it), that one is used. Anything ambiguous or unfound is returned
    /// unchanged, so the caller's own error explains the situation.
    /// </summary>
    public static string ResolveInstancePath(string instancePath)
    {
        if (string.IsNullOrWhiteSpace(instancePath) || IsInstanceFolder(instancePath))
        {
            return instancePath;
        }

        var candidates = FindInstanceCandidates(instancePath);
        return candidates.Count == 1 ? candidates[0] : instancePath;
    }

    private static bool IsInstanceFolder(string path) =>
        File.Exists(Path.Combine(path, "ModOrganizer.ini")) || Directory.Exists(Path.Combine(path, "profiles"));

    /// <summary>Every MO2 instance that can be found inside <paramref name="path"/> - see
    /// <see cref="ResolveInstancePath"/>. Candidates that resolve to the same data root are merged,
    /// preferring the one with its own ModOrganizer.ini (it also carries selected_profile).</summary>
    public static IReadOnlyList<string> FindInstanceCandidates(string path)
    {
        var byDataRoot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Add(string candidate)
        {
            var dataRoot = Path.GetFullPath(ResolveDataRoot(candidate)).TrimEnd('\\', '/');
            if (!byDataRoot.TryGetValue(dataRoot, out var existing)
                || (!File.Exists(Path.Combine(existing, "ModOrganizer.ini")) && File.Exists(Path.Combine(candidate, "ModOrganizer.ini"))))
            {
                byDataRoot[dataRoot] = candidate;
            }
        }

        try
        {
            if (Directory.Exists(path))
            {
                foreach (var child in Directory.EnumerateDirectories(path))
                {
                    if (IsInstanceFolder(child))
                    {
                        Add(child);
                    }
                }
            }

            var globalRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModOrganizer");
            var normalizedPath = Path.GetFullPath(path).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            if (Directory.Exists(globalRoot))
            {
                foreach (var instance in Directory.EnumerateDirectories(globalRoot))
                {
                    if (!File.Exists(Path.Combine(instance, "ModOrganizer.ini")))
                    {
                        continue;
                    }

                    var dataRoot = Path.GetFullPath(ResolveDataRoot(instance)).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                    if (dataRoot.StartsWith(normalizedPath, StringComparison.OrdinalIgnoreCase))
                    {
                        Add(instance);
                    }
                }
            }
        }
        catch (Exception)
        {
            // Unreadable folder / bad path: report whatever was found so far.
        }

        return byDataRoot.Values.ToList();
    }

    private static string DescribeMissingModlist(string instancePath, string profileName, string modlistPath)
    {
        var message = $"No modlist.txt found for MO2 profile '{profileName}' (looked for '{modlistPath}'). ";
        var profiles = DiscoverProfiles(instancePath).Profiles;
        if (profiles.Count > 0)
        {
            return message + $"Profiles found in this instance: {string.Join(", ", profiles)}. Pick one of them as the MO2 Profile.";
        }

        message += $"'{instancePath}' doesn't look like an MO2 instance (no profiles folder found). "
            + "MO2 Instance Path should be the folder MO2 itself calls the instance - the one containing ModOrganizer.ini "
            + "(for a global instance that's under %LOCALAPPDATA%\\ModOrganizer\\<instance name>; for a portable one, the MO2 folder itself). "
            + "If that instance uses a custom base directory, it's read automatically from ModOrganizer.ini.";
        var candidates = FindInstanceCandidates(instancePath);
        if (candidates.Count > 1)
        {
            message += $" Several instances were found inside that folder, pick one: {string.Join("; ", candidates)}.";
        }

        return message;
    }

    private static string ResolveDataRoot(string instancePath)
    {
        var iniPath = Path.Combine(instancePath, "ModOrganizer.ini");
        if (!File.Exists(iniPath))
        {
            return instancePath;
        }

        foreach (var rawLine in File.ReadAllLines(iniPath))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("base_directory=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = line["base_directory=".Length..].Trim();
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        return instancePath;
    }

    /// <summary>
    /// Reads ModOrganizer.ini's selected_profile (the profile MO2 itself currently has active,
    /// stored as "selected_profile=@ByteArray(Name)") so the UI can default to it instead of
    /// making every user type "Default" by hand. Returns false if the ini or key isn't found.
    /// </summary>
    public static bool TryDetectSelectedProfile(string instancePath, out string profileName)
    {
        instancePath = ResolveInstancePath(instancePath);
        var iniPath = Path.Combine(instancePath, "ModOrganizer.ini");
        if (File.Exists(iniPath))
        {
            foreach (var rawLine in File.ReadAllLines(iniPath))
            {
                var line = rawLine.Trim();
                if (!line.StartsWith("selected_profile=", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = line["selected_profile=".Length..].Trim();
                var start = value.IndexOf('(');
                var end = value.IndexOf(')');
                if (start >= 0 && end > start)
                {
                    value = value[(start + 1)..end];
                }

                if (!string.IsNullOrEmpty(value))
                {
                    profileName = value;
                    return true;
                }
            }
        }

        profileName = string.Empty;
        return false;
    }

    /// <summary>Discovers the usable profiles for an instance and the profile MO2 currently has
    /// selected. The selected value is returned only when it corresponds to a profile containing
    /// modlist.txt; this keeps the native picker from offering a stale/incomplete profile name.</summary>
    public static ProfileDiscovery DiscoverProfiles(string instancePath)
    {
        instancePath = ResolveInstancePath(instancePath);
        var dataRoot = ResolveDataRoot(instancePath);
        var profilesRoot = Path.Combine(dataRoot, "profiles");
        if (!Directory.Exists(profilesRoot))
        {
            return new ProfileDiscovery(Array.Empty<string>(), null);
        }

        var profiles = Directory.EnumerateDirectories(profilesRoot)
            .Where(path => File.Exists(Path.Combine(path, "modlist.txt")))
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var selectedProfile = TryDetectSelectedProfile(instancePath, out var selected)
            && profiles.Contains(selected, StringComparer.OrdinalIgnoreCase)
            ? profiles.First(name => string.Equals(name, selected, StringComparison.OrdinalIgnoreCase))
            : null;

        return new ProfileDiscovery(profiles, selectedProfile);
    }

    private static List<string> ParseEnabledMods(string modlistPath)
    {
        var result = new List<string>();
        foreach (var rawLine in File.ReadAllLines(modlistPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || !line.StartsWith('+'))
            {
                continue;
            }

            result.Add(line[1..].Trim());
        }
        return result;
    }
}
