using Skald.Core.Models;

namespace Skald.Core.Interfaces;

public interface ISystemMetricsCollector
{
    string Name { get; }

    bool IsSupported { get; }

    Task<SystemMetricsSnapshot> SampleAsync(CancellationToken cancellationToken = default);
}
