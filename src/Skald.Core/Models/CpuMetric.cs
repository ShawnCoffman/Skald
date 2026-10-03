namespace Skald.Core.Models;

public sealed record CpuMetric(
    double UtilizationPercent,
    int LogicalProcessorCount,
    MetricAvailability Availability)
{
    public double? ReportedMegahertz { get; init; }
    // Base frequency x '% Processor Performance' from Windows counters; reflects boost and throttling, unlike the OS-reported value.
    public double? EffectiveMegahertz { get; init; }
    public IReadOnlyList<LogicalProcessorMetric> LogicalProcessors { get; init; } = [];
    public double? ProcessorQueueLength { get; init; }
    public double? InterruptPercent { get; init; }
    public double? DpcPercent { get; init; }
}
