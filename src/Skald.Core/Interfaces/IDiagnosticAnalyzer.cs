using Skald.Core.Models;

namespace Skald.Core.Interfaces;

public interface IDiagnosticAnalyzer
{
    IReadOnlyList<DiagnosticFinding> Analyze(SystemMetricsSnapshot snapshot);
}
