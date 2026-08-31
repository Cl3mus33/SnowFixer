namespace SnowFixer.Core.Pipeline;

/// <summary>A progress update from an extraction run. Total &lt;= 0 means indeterminate (no known
/// count yet) - matches AutoBlend.Core.Pipeline.PatchProgress's own shape.</summary>
public sealed record ExtractProgress(string Message, int Current, int Total)
{
    public bool IsIndeterminate => Total <= 0;
}
