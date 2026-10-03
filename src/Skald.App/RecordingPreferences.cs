using System.Text.Json;

namespace Skald.App;

// Per-user recording choices, kept across launches. A missing or unreadable file means the defaults.
internal sealed class RecordingPreferences
{
    private static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Skald", "recording.json");

    public bool IncludeDeepTrace { get; set; }

    public static RecordingPreferences Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<RecordingPreferences>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
