using System.Globalization;
using System.Text.Json.Serialization;

namespace Skald.Core.Models;

public sealed record ProcessMetric(
    int ProcessId,
    DateTimeOffset? StartTime,
    string Name,
    string? ExecutablePath,
    double CpuPercent,
    double WorkingSetMegabytes,
    double PrivateMemoryMegabytes)
{
    public string? UserName { get; init; }
    public double? ReadBytesPerSecond { get; init; }
    public double? WriteBytesPerSecond { get; init; }
    public int? ThreadCount { get; init; }
    public int? HandleCount { get; init; }
    public double? PeakWorkingSetMegabytes { get; init; }
    public TimeSpan? TotalCpuTime { get; init; }
    public uint? PageFaultCount { get; init; }
    public bool CpuAvailable { get; init; } = true;
    public bool WorkingSetAvailable { get; init; } = true;
    public bool PrivateMemoryAvailable { get; init; } = true;
    public bool HasVisibleWindow { get; init; }
    // Null means GPU telemetry is unavailable; an empty map means a measured 0%.
    public IReadOnlyDictionary<string, double>? GpuEngines { get; init; }
    public double? GpuDedicatedMegabytes { get; init; }
    public double? GpuSharedMegabytes { get; init; }

    [JsonIgnore] public double? GpuPercent => GpuEngines is null ? null : GpuEngines.Count == 0 ? 0 : GpuEngines.Values.Max();

    [JsonIgnore] public string UserDisplay => UserName ?? "—";
    [JsonIgnore] public string CpuDisplay => CpuAvailable ? $"{CpuPercent:F1}% CPU" : "—";
    [JsonIgnore] public string CpuTableDisplay => CpuAvailable ? $"{CpuPercent:F1}%" : "—";
    [JsonIgnore] public string GpuDisplay => GpuPercent is { } value ? $"{value:F1}%" : "—";
    [JsonIgnore] public string GpuDedicatedDisplay => GpuDedicatedMegabytes is { } value ? $"{value:F0} MB" : "—";
    [JsonIgnore] public string GpuSharedDisplay => GpuSharedMegabytes is { } value ? $"{value:F0} MB" : "—";
    [JsonIgnore] public string WorkingSetDisplay => WorkingSetAvailable ? $"{WorkingSetMegabytes:F0} MB" : "—";
    [JsonIgnore] public string PrivateMemoryDisplay => PrivateMemoryAvailable ? $"{PrivateMemoryMegabytes:F0} MB" : "—";
    [JsonIgnore] public string ReadDisplay => FormatRate(ReadBytesPerSecond);
    [JsonIgnore] public string WriteDisplay => FormatRate(WriteBytesPerSecond);
    [JsonIgnore] public string ThreadDisplay => ThreadCount?.ToString("N0", CultureInfo.CurrentCulture) ?? "—";
    [JsonIgnore] public string HandleDisplay => HandleCount?.ToString("N0", CultureInfo.CurrentCulture) ?? "—";
    [JsonIgnore] public string PeakMemoryDisplay => PeakWorkingSetMegabytes is { } peak ? $"{peak:F0} MB" : "—";
    [JsonIgnore] public string CpuTimeDisplay => TotalCpuTime is { } time ? $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}" : "—";
    [JsonIgnore] public string PageFaultDisplay => PageFaultCount?.ToString("N0", CultureInfo.CurrentCulture) ?? "—";

    private static string FormatRate(double? bytesPerSecond)
    {
        if (bytesPerSecond is null) return "—";
        var value = bytesPerSecond.Value;
        var suffix = "B/s";
        if (value >= 1024) { value /= 1024; suffix = "KB/s"; }
        if (value >= 1024) { value /= 1024; suffix = "MB/s"; }
        return $"{value:F1} {suffix}";
    }
}
