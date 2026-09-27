namespace Skald.Core.Models;

public sealed record NetworkMetric(
    double ReceiveBytesPerSecond,
    double SendBytesPerSecond,
    int ActiveInterfaceCount,
    MetricAvailability Availability)
{
    public IReadOnlyList<NetworkInterfaceMetric> Interfaces { get; init; } = [];
    public double? TcpRetransmitsPerSecond { get; init; }
}

public sealed record NetworkInterfaceMetric(string Id, string Name, string Kind, long LinkSpeedBitsPerSecond,
    double ReceiveBytesPerSecond, double SendBytesPerSecond, long? ReceiveErrors, long? SendErrors,
    long? ReceiveDiscards, long? SendDiscards)
{
    public long? NewReceiveErrors { get; init; }
    public long? NewSendErrors { get; init; }
    public long? NewReceiveDiscards { get; init; }
    public long? NewSendDiscards { get; init; }
}
