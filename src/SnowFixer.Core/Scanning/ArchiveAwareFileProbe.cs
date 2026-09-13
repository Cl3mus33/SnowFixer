using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Noggog;

namespace SnowFixer.Core.Scanning;

/// <summary>
/// Loose files first (matching Skyrim's real VFS priority — loose always wins), falling back to
/// every BSA/BA2 applicable to the data folder for this game release. Each archive's full file
/// list is indexed case-insensitively on first use (not up front in the constructor) so a modlist
/// carrying many large archives only pays the indexing cost for archives we actually end up
/// querying.
/// </summary>
public sealed class ArchiveAwareFileProbe : IGameFileProbe
{
    private readonly LooseFileProbe _looseProbe;
    private readonly IReadOnlyList<IArchiveReader> _archiveReaders;

    // IArchiveReader.TryGetFolder(folderPath) does a case-SENSITIVE lookup internally - real-world
    // BSA/BA2 archives frequently mix casing between what a plugin's own Model.File path uses
    // (e.g. "BSCyrodiil\...") and what got packed into the archive (e.g. "bscyrodiil\..."), or even
    // between different records referencing the very same folder. Indexing every archive's full
    // file list once (case-insensitively, lazily on first use) avoids the folder-lookup step's case
    // sensitivity entirely; built once per reader rather than per query since IArchiveReader.Files
    // enumerates the whole archive.
    private readonly Dictionary<IArchiveReader, Dictionary<string, IArchiveFile>> _archiveIndexes = new();

    public ArchiveAwareFileProbe(string dataRoot, GameRelease gameRelease)
    {
        _looseProbe = new LooseFileProbe(dataRoot);

        var archivePaths = GetApplicableArchivePathsSafe(gameRelease, dataRoot);

        var readers = new List<IArchiveReader>();
        foreach (var archivePath in archivePaths)
        {
            // A corrupt or unsupported-format archive can throw here - one bad archive in the real
            // Data folder must not abort the whole run.
            try
            {
                readers.Add(Archive.CreateReader(gameRelease, archivePath));
            }
            catch (Exception)
            {
                // Skip archives Mutagen can't parse (corrupt/unsupported format).
            }
        }
        _archiveReaders = readers;
    }

    public bool Exists(string relativeDataPath)
    {
        if (_looseProbe.Exists(relativeDataPath))
        {
            return true;
        }

        return TryFindArchiveFile(relativeDataPath, out _);
    }

    public Stream OpenRead(string relativeDataPath)
    {
        if (_looseProbe.Exists(relativeDataPath))
        {
            return _looseProbe.OpenRead(relativeDataPath);
        }

        if (TryFindArchiveFile(relativeDataPath, out var file))
        {
            return file!.AsStream();
        }

        throw new FileNotFoundException($"'{relativeDataPath}' was not found loose or in any applicable archive.");
    }

    public IEnumerable<string> EnumerateFiles(string relativeFolder, string extension) =>
        _looseProbe.EnumerateFiles(relativeFolder, extension);

    private bool TryFindArchiveFile(string relativeDataPath, out IArchiveFile? file)
    {
        foreach (var reader in _archiveReaders)
        {
            var index = GetOrBuildIndex(reader);
            if (index.TryGetValue(relativeDataPath, out var match))
            {
                file = match;
                return true;
            }
        }

        file = null;
        return false;
    }

    private Dictionary<string, IArchiveFile> GetOrBuildIndex(IArchiveReader reader)
    {
        if (_archiveIndexes.TryGetValue(reader, out var existing))
        {
            return existing;
        }

        var index = new Dictionary<string, IArchiveFile>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var archiveFile in reader.Files)
            {
                // A handful of archives ship the same path under two different cases as distinct
                // entries - first one wins, matching how the game itself only ever sees one at a time.
                index.TryAdd(archiveFile.Path, archiveFile);
            }
        }
        catch (Exception)
        {
            // A malformed/corrupt archive's own internal filename table can throw while being
            // enumerated here. Whatever this archive already indexed before hitting the bad part
            // stays usable - better than discarding it entirely - but nothing more from it will
            // ever be found.
        }

        _archiveIndexes[reader] = index;
        return index;
    }

    /// <summary>
    /// Mutagen's own Archive.GetApplicableArchivePaths sorts every matching archive by a priority
    /// comparer that can throw NotImplementedException from deep inside Mutagen itself (reported
    /// directly against a real MO2 modlist): two archives whose names collapse to the same
    /// base+suffix pair after stripping a " - Suffix" segment reach a branch Mutagen never
    /// implemented. TryFindArchiveFile only cares about which archives exist at all (first match
    /// wins in whatever order they're returned) - it doesn't need Mutagen's own priority ordering -
    /// so falling back to a plain, unsorted directory listing on failure keeps the whole run from
    /// crashing over a dependency bug that has nothing to do with which files are actually being
    /// looked up.
    /// </summary>
    private static IEnumerable<FilePath> GetApplicableArchivePathsSafe(GameRelease release, string dataRoot)
    {
        try
        {
            return Archive.GetApplicableArchivePaths(release, dataRoot).ToList();
        }
        catch (Exception)
        {
            var extension = Archive.GetExtension(release);
            return Directory.Exists(dataRoot)
                ? Directory.EnumerateFiles(dataRoot, "*" + extension).Select(path => (FilePath)path).ToList()
                : Enumerable.Empty<FilePath>();
        }
    }

    public void Dispose()
    {
        foreach (var reader in _archiveReaders)
        {
            (reader as IDisposable)?.Dispose();
        }
    }
}
