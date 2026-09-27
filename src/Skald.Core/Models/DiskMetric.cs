namespace Skald.Core.Models;

public sealed record DiskMetric(
    double ActiveTimePercent,
    double TransfersPerSecond,
    MetricAvailability Availability)
{
    public IReadOnlyList<PhysicalDiskMetric> PhysicalDisks { get; init; } = [];
}

public sealed record PhysicalDiskMetric(string Name, double? ActivePercent, double? ReadBytesPerSecond,
    double? WriteBytesPerSecond, double? ReadsPerSecond, double? WritesPerSecond,
    double? ReadLatencyMilliseconds, double? WriteLatencyMilliseconds, double? QueueLength);
