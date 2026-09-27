using Skald.Core.Models;

namespace Skald.App.Pages;

public interface ITelemetryPage
{
    void Update(SystemMetricsSnapshot snapshot, IReadOnlyList<DiagnosticFinding> findings);
}
