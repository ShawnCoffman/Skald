namespace Skald.Core.Models;

public sealed record CpuMetric(
    double UtilizationPercent,
    int LogicalProcessorCount,
    MetricAvailability Availability)
{
    public double? ReportedMegahertz { get; init; }
    public IReadOnlyList<LogicalProcessorMetric> LogicalProcessors { get; init; } = [];
    public double? ProcessorQueueLength { get; init; }
    public double? InterruptPercent { get; init; }
    public double? DpcPercent { get; init; }
}
