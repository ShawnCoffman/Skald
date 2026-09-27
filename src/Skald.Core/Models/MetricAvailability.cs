namespace Skald.Core.Models;

public sealed record MetricAvailability(
    bool IsSupported,
    string? Reason = null)
{
    public static MetricAvailability Supported { get; } = new(true);

    public static MetricAvailability Unsupported(string reason) => new(false, reason);
}
