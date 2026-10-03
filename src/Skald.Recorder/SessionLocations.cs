namespace Skald.Recorder;

// Where recordings live. Recordings hold process paths and user names, so when Documents is synced by OneDrive (Known Folder Move)
// they are kept in local app data instead of being uploaded as they are written. Exports remain an explicit user choice.
public static class SessionLocations
{
    public static string DocumentsDirectory
    {
        get
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return string.IsNullOrWhiteSpace(documents) ? AppContext.BaseDirectory : documents;
        }
    }

    // Recordings made before this release, and on machines whose Documents folder is not synced.
    public static string LegacyDirectory => Path.Combine(DocumentsDirectory, "Skald Sessions");

    public static string LocalDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Skald", "Sessions");

    public static string RecordingsDirectory => IsCloudSynced(DocumentsDirectory) ? LocalDirectory : LegacyDirectory;

    public static string ExportsDirectory => Path.Combine(DocumentsDirectory, "Skald Exports");

    // Every folder that may hold recordings, newest location first.
    public static IReadOnlyList<string> SearchDirectories
        => new[] { RecordingsDirectory, LegacyDirectory, LocalDirectory }.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static bool IsCloudSynced(string path) => IsCloudSynced(path,
        [Environment.GetEnvironmentVariable("OneDrive"), Environment.GetEnvironmentVariable("OneDriveCommercial"), Environment.GetEnvironmentVariable("OneDriveConsumer")]);

    public static bool IsCloudSynced(string path, IEnumerable<string?> syncRoots)
    {
        ArgumentNullException.ThrowIfNull(syncRoots);
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return syncRoots.Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => Path.GetFullPath(root!).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar)
            .Any(root => full.StartsWith(root, StringComparison.OrdinalIgnoreCase));
    }
}
