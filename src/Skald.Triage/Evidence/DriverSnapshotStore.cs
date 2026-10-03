using System.Text.Json;
using System.Text.Json.Serialization;

namespace Skald.Triage;

public static class DriverSnapshotStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // The snapshot taken at the end of the previous scan on this machine; the next scan compares against it.
    public static string LastScanPath
    {
        get
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(string.IsNullOrWhiteSpace(root) ? AppContext.BaseDirectory : root, "Skald", "driver-snapshot.json");
        }
    }

    public static DriverSnapshot? Load(string path, ICollection<string> status)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var snapshot = JsonSerializer.Deserialize<DriverSnapshot>(File.ReadAllText(path), Options);
            if (snapshot is null) return null;
            // A hand-edited or older baseline can miss fields; drop unusable rows instead of failing the scan.
            return snapshot with
            {
                Computer = snapshot.Computer ?? "Unknown",
                Drivers = (snapshot.Drivers ?? []).Where(driver => driver is { DeviceId: not null, Version: not null })
                    .Select(driver => driver with { Name = driver.Name ?? driver.DeviceId, Class = driver.Class ?? "Other", Provider = driver.Provider ?? "Unknown", Inf = driver.Inf ?? string.Empty })
                    .ToArray()
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            status.Add($"Snapshot {Path.GetFileName(path)}: unreadable ({ex.GetType().Name})");
            return null;
        }
    }

    public static void Save(string path, DriverSnapshot snapshot, ICollection<string> status)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, Options));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            status.Add($"Snapshot {Path.GetFileName(path)}: could not save ({ex.GetType().Name})");
        }
    }
}
