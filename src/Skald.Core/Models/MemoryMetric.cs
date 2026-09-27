namespace Skald.Core.Models;

public sealed record MemoryMetric(
    ulong TotalBytes,
    ulong AvailableBytes,
    double UsedPercent,
    MetricAvailability Availability)
{
    public ulong UsedBytes => TotalBytes >= AvailableBytes ? TotalBytes - AvailableBytes : 0;
    public ulong? CommitBytes { get; init; }
    public ulong? CommitLimitBytes { get; init; }
    public double? PageReadsPerSecond { get; init; }
}
