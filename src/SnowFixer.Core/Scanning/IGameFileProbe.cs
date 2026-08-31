namespace SnowFixer.Core.Scanning;

public interface IGameFileProbe : IDisposable
{
    bool Exists(string relativeDataPath);
    Stream OpenRead(string relativeDataPath);

    /// <summary>Lists every file matching <paramref name="extension"/> anywhere under
    /// <paramref name="relativeFolder"/> (recursive), across every source this probe knows about -
    /// used to search content (e.g. PBRNifPatcher json configs) rather than probe one specific
    /// path. Paths are relative to the game's Data folder, same convention as <see cref="Exists"/>.</summary>
    IEnumerable<string> EnumerateFiles(string relativeFolder, string extension);
}
