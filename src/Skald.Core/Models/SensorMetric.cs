namespace Skald.Core.Models;

// Different power domains must never be added implicitly.
public sealed record SensorMetric(string Id, string Device, string Name, string Kind,
    string Unit, double? Value, string Source, string Scope, string? Note = null);

public sealed record LogicalProcessorMetric(string Id, double? UtilizationPercent, double? ReportedMegahertz);
