namespace SnowFixer.Core.Configuration;

/// <summary>
/// Values (and their on-wire order) must stay in lockstep with the native shell's
/// SFModManagerType - both sides serialize this as a plain integer.
/// </summary>
public enum ModManagerType
{
    None,
    ModOrganizer2,
}
