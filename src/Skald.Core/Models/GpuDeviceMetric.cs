namespace Skald.Core.Models;

public sealed record GpuDeviceMetric(
    string Name,
    string Source,
    double? TemperatureCelsius,
    double? PowerWatts,
    double? DedicatedUsedMegabytes,
    double? DedicatedTotalMegabytes)
{
    public double? GraphicsClockMegahertz { get; init; }
    public double? MemoryClockMegahertz { get; init; }
    public string? DeviceId { get; init; }
    public double? PowerLimitWatts { get; init; }
    public int? PerformanceState { get; init; }
    public double? FanSpeedPercent { get; init; }
    public ulong? ClockLimitReasons { get; init; }
}
