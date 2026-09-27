namespace Skald.Core.Models;

public sealed record DiagnosticFinding(
    string Title,
    PressureState Severity,
    string Explanation,
    IReadOnlyList<string> Evidence);
