using Skald.Core.Models;

namespace Skald.Collectors;

public static class CrashDumpCollector
{
    public static Task<CrashDumpInventory> CollectAsync(bool includeOtherProfiles = false) => Task.Run(() => Collect(includeOtherProfiles));

    private static CrashDumpInventory Collect(bool includeOtherProfiles)
    {
        var files = new List<CrashDumpFile>();
        var status = new List<string>();
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        AddFile(Path.Combine(windows, "MEMORY.DMP"), "System memory dump", files, status);
        AddDirectory(Path.Combine(windows, "Minidump"), "System minidump", files, status);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        AddDirectory(Path.Combine(local, "CrashDumps"), "Current-user app dump", files, status);
        if (includeOtherProfiles)
        {
            try
            {
                var profileRoot = Directory.GetParent(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                if (profileRoot is not null)
                foreach (var profile in profileRoot.EnumerateDirectories())
                {
                    var dumpPath = Path.Combine(profile.FullName, "AppData", "Local", "CrashDumps");
                    if (dumpPath.Equals(Path.Combine(local, "CrashDumps"), StringComparison.OrdinalIgnoreCase)) continue;
                    AddDirectory(dumpPath, $"App dump · {profile.Name}", files, status);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { status.Add($"Other profiles: partial ({ex.GetType().Name})"); }
        }
        return new(DateTimeOffset.Now, files.OrderByDescending(file => file.ModifiedAt).ToArray(), status);
    }

    private static void AddFile(string path, string kind, List<CrashDumpFile> files, List<string> status)
    {
        try
        {
            var file = new FileInfo(path);
            if (file.Exists) files.Add(new(kind, file.FullName, file.LastWriteTime, file.Length));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { status.Add($"{kind}: unavailable ({ex.GetType().Name})"); }
    }

    private static void AddDirectory(string path, string kind, List<CrashDumpFile> files, List<string> status)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            foreach (var pathToFile in Directory.EnumerateFiles(path, "*.dmp", SearchOption.TopDirectoryOnly))
                AddFile(pathToFile, kind, files, status);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { status.Add($"{kind}: unavailable ({ex.GetType().Name})"); }
    }
}
