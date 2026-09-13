using System.Collections.Generic;
using OpenTECHub.Services.Persistence;

namespace OpenTECHub.Services.Calibration;

/// <summary>
/// Persistence contract for managing external pump hose calibration profiles.
/// </summary>
public interface IPumpCalibrationProfileStore
{
    /// <summary>Root folder where profiles are stored.</summary>
    string ProfilesDirectory { get; }

    /// <summary>
    /// Lists summaries of all filed hose profiles without loading their full sample arrays.
    /// Corrupt files are omitted from the listing and logged without breaking access to healthy profiles.
    /// Files with future schema versions are reported with IsCompatible = false.
    /// </summary>
    IReadOnlyList<PumpCalibrationProfileSummary> ListProfiles();

    /// <summary>
    /// Loads a complete profile by its hose name. Returns null if absent.
    /// Throws InvalidOperationException if the file was written by a newer incompatible application version.
    /// </summary>
    PumpCalibrationProfile? LoadProfile(string name);

    /// <summary>
    /// Saves a profile atomically. If the file already exists, overwrite must be true or an InvalidOperationException is thrown.
    /// Overwriting files with future schema versions is rejected unconditionally.
    /// </summary>
    void SaveProfile(PumpCalibrationProfile profile, bool overwrite = false);

    /// <summary>
    /// Removes a profile from disk. Returns true if removed, false if not found.
    /// </summary>
    bool DeleteProfile(string name);

    /// <summary>
    /// Checks whether a profile exists on disk with the specified name (case-insensitive on Windows).
    /// </summary>
    bool ProfileExists(string name);

    /// <summary>
    /// Idempotently ensures an initial profile ("Padrão") exists by migrating legacy linear settings
    /// when the store is completely empty. Returns the active profile name.
    /// </summary>
    string EnsureDefaultProfileMigrated(AppSettings settings);
}
