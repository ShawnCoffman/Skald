using System.Globalization;
using Skald.Core.Models;

namespace Skald.Triage;

public sealed record HandoffStatement(string Headline, string Detail);

// Joins the two questions: what the hardware check found and what the machine is doing right now. It states a lead, never a verdict on the application.
public static class Handoff
{
    // Live findings that describe a device, driver, platform or network path rather than an application's workload.
    private static readonly string[] PlatformFindings =
        ["Platform thermal limit", "High read latency", "High write latency", "Elevated DPC time", "Interface errors", "Sustained TCP retransmissions"];

    public static bool IsPlatformFinding(string title) => PlatformFindings.Any(prefix => title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    public static HandoffStatement Compose(TriageReport? report, IReadOnlyList<DiagnosticFinding> live, string? topProcess)
    {
        var current = live.Where(finding => finding.Severity >= PressureState.Elevated).Select(finding => finding.Title).Distinct().ToArray();
        var now = current.Length > 0 ? string.Join(", ", current) : null;

        if (report is null)
            return new("Hardware has not been checked yet",
                (now is null ? "Nothing is flagged right now. " : $"Flagged right now: {now}. ") + "Run Check hardware to rule hardware, drivers and firmware in or out before pointing at an application.");

        var age = DateTimeOffset.Now - report.GeneratedAt;
        var when = age.TotalMinutes < 2 ? "just now" : age.TotalHours < 24 ? $"{Math.Max(1, (int)age.TotalHours)} h ago" : $"{(int)age.TotalDays} d ago";
        var checkedText = $"Hardware check run {when}." + (report.Verdict.Coverage is { } coverage ? " " + coverage : string.Empty);
        var lookedAt = report.Verdict.Coverage is null ? $"in the last {report.WindowDays.ToString(CultureInfo.InvariantCulture)} days" : "in the part of the logs that was read";
        var software = report.Checks.FirstOrDefault(check => check.Id == "app.faults" && check.Status == CheckStatus.Found);

        switch (report.Verdict.Outcome)
        {
            case TriageOutcome.HardwareEvidenceFound:
                return new("Hardware, driver or firmware evidence exists",
                    $"{checkedText} Resolve or rule out {string.Join("; ", report.Verdict.Leads)} before attributing this to an application."
                    + (now is null ? string.Empty : $" The machine is also flagged for {now}, which may be a symptom of the same fault."));
            case TriageOutcome.Incomplete:
                return new("Hardware check incomplete", $"{checkedText} {report.Verdict.Detail}");
            default:
                if (report.Verdict.Outcome == TriageOutcome.MinorFindings)
                    checkedText += $" Minor findings noted ({string.Join("; ", report.Verdict.Leads)}); none is strong evidence on its own.";
                var platform = current.Where(IsPlatformFinding).ToArray();
                if (platform.Length > 0)
                    return new("The current constraint points at the platform, not an application",
                        $"{checkedText} Windows recorded no significant hardware evidence, but right now the machine shows {string.Join(", ", platform)}. "
                        + "These describe a device, driver, thermal limit or network path, so do not attribute them to the busiest process. Capture it with Record and Mark Problem, then check the disk, driver or cooling named.");
                if (now is null)
                    return new("No hardware evidence, and nothing is constrained right now",
                        $"{checkedText} If the problem is intermittent, press Record, reproduce it, then Mark Problem so the evidence is captured."
                        + (software is null ? string.Empty : $" Application crashes are on record: {software.Summary}"));
                return new("The current constraint looks software-side",
                    $"{checkedText} Windows recorded no significant hardware, driver or firmware evidence {lookedAt}, and the machine is flagged for {now}."
                    + (topProcess is null ? string.Empty : $" The largest contributor in the live view is {topProcess}.")
                    + " Treat that as a lead: the timing correlates, it does not prove cause."
                    + (software is null ? string.Empty : $" Application crashes are also on record: {software.Summary}"));
        }
    }
}
