using System.Globalization;
using System.Net;
using System.Text;
using Skald.Core.Models;
using Skald.Diagnostics;

namespace Skald.Recorder;

// A standalone page for a ticket: what was happening around each problem marker, what Windows recorded in the same window, and
// what the recording could not see. It uses the hardware check's layout and Found / Nothing found / Could not check wording.
public static class SessionReportWriter
{
    private static readonly TimeSpan Before = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan After = TimeSpan.FromMinutes(2);

    public static async Task WriteHtmlAsync(SessionDocument document, string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(filePath, ToHtml(document), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
    }

    public static string ToHtml(SessionDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var samples = document.Samples.OrderBy(sample => sample.Timestamp).ToArray();
        var metadata = document.Metadata;
        var duration = metadata.EndedAt - metadata.StartedAt;
        var markers = document.Events.Where(item => item.Type == SessionEventType.UserMarker).OrderBy(item => item.Timestamp).ToArray();
        var windows = document.WindowsEvidence;
        var exits = document.Events.Where(item => item.Type == SessionEventType.ProcessExit).ToArray();
        var appExits = exits.Where(item => item.HadWindow == true).ToArray();
        var power = PowerSessionSummary.From(document);

        var html = new StringBuilder();
        html.AppendLine("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        html.AppendLine("<title>Skald recording report</title><style>");
        html.AppendLine(":root{--bg:#f7f9fa;--fg:#14232b;--card:#fff;--line:#d5dfe3;--muted:#5b6f78;--found:#b3261e;--noted:#8a5a00;--clear:#1b6e3c;--unable:#555}");
        html.AppendLine("@media (prefers-color-scheme:dark){:root{--bg:#0b151d;--fg:#e6f0f2;--card:#111f29;--line:#27404c;--muted:#9bb3bc;--found:#ff8a80;--noted:#ffcc66;--clear:#6fd69a;--unable:#b9c4c9}}");
        html.AppendLine("body{font:15px/1.5 Segoe UI,system-ui,sans-serif;background:var(--bg);color:var(--fg);margin:0;padding:24px;max-width:960px;margin-inline:auto}");
        html.AppendLine("h1{margin:0 0 4px;font-size:24px}h2{font-size:15px;letter-spacing:.06em;text-transform:uppercase;color:var(--muted);margin:28px 0 8px}");
        html.AppendLine(".meta{color:var(--muted);font-size:13px}.summary{border:1px solid var(--line);border-left:6px solid var(--clear);background:var(--card);padding:14px 18px;border-radius:6px;margin-top:16px}.summary.found{border-left-color:var(--found)}");
        html.AppendLine(".check{border:1px solid var(--line);background:var(--card);border-radius:6px;padding:10px 14px;margin:8px 0}.check h3{margin:0;font-size:15px}");
        html.AppendLine(".badge{display:inline-block;font-size:11px;font-weight:600;padding:1px 8px;border-radius:10px;border:1px solid currentColor;margin-right:8px;vertical-align:1px}");
        html.AppendLine(".found{color:var(--found)}.noted{color:var(--noted)}.clear{color:var(--clear)}.unable{color:var(--unable)}");
        html.AppendLine("ul{margin:6px 0 0;padding-left:20px}.limit{color:var(--muted);font-size:13px;margin-top:6px}table{border-collapse:collapse;width:100%}td,th{padding:3px 12px 3px 0;vertical-align:top;text-align:left;border-bottom:1px solid var(--line)}th{color:var(--muted);font-weight:600;font-size:13px}");
        html.AppendLine(".grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(170px,1fr));gap:8px}.card{border:1px solid var(--line);background:var(--card);border-radius:6px;padding:10px 14px}.card div{color:var(--muted);font-size:12px}pre{white-space:pre-wrap;color:var(--muted);font-size:12px}");
        html.AppendLine("</style></head><body>");
        html.AppendLine(Invariant($"<h1>Skald recording report</h1><div class=\"meta\">{E(metadata.MachineName)} · {Local(metadata.StartedAt, "yyyy-MM-dd HH:mm:ss")} to {Local(metadata.EndedAt, "HH:mm:ss")} · {duration.TotalMinutes:F1} min · {samples.Length} samples every {metadata.SampleIntervalSeconds} s · Skald {E(metadata.ProductVersion)}{(metadata.Recovered ? " · recovered after an interruption" : string.Empty)}{(document.Redacted ? " · names removed" : string.Empty)}</div>"));

        var crashEvents = windows?.Events.Where(item => item.Category is "Crashes & restarts" or "Hardware" or "Applications" or "Drivers & storage").ToArray() ?? [];
        var hasEvidence = crashEvents.Length > 0 || (windows?.Dumps.Count ?? 0) > 0;
        var headline = $"{markers.Length} problem marker{(markers.Length == 1 ? string.Empty : "s")} · " + (windows is null
            ? "Windows reports were not saved with this recording"
            : $"{windows.Events.Count} Windows report{(windows.Events.Count == 1 ? string.Empty : "s")} and {windows.Dumps.Count} dump file{(windows.Dumps.Count == 1 ? string.Empty : "s")} during the recording")
            + $" · {appExits.Length} program{(appExits.Length == 1 ? string.Empty : "s")} with a window exited" + (document.Traces.Count > 0 ? $" · {document.Traces.Count} deep trace{(document.Traces.Count == 1 ? string.Empty : "s")}" : string.Empty);
        html.AppendLine(Invariant($"<div class=\"summary {(hasEvidence ? "found" : string.Empty)}\"><strong>{E(headline)}</strong><div class=\"limit\">Timing near a marker is a correlation, not proof of cause. Process exits are inferred from {metadata.SampleIntervalSeconds}-second samples; whether an exit was a crash comes only from Windows reports.</div></div>"));

        html.AppendLine("<h2>Problem markers</h2>");
        if (markers.Length == 0) html.AppendLine("<div class=\"check\"><h3><span class=\"badge unable\">No marker</span>Mark Problem was not pressed</h3><div>Review the whole recording below.</div></div>");
        foreach (var marker in markers)
        {
            var from = marker.Timestamp - Before;
            var to = marker.Timestamp + After;
            var window = samples.Where(sample => sample.Timestamp >= from && sample.Timestamp <= to).ToArray();
            var findings = WindowDiagnosticAnalyzer.Analyze(window, Before + After);
            var nearbyReports = windows?.Events.Where(item => item.Timestamp >= from && item.Timestamp <= marker.Timestamp.AddMinutes(10)).ToArray() ?? [];
            var nearbyExits = exits.Where(item => item.Timestamp >= from && item.Timestamp <= to && item.HadWindow == true).ToArray();
            var status = findings.Count > 0 || nearbyReports.Length > 0 ? ("found", "Found") : window.Length == 0 ? ("unable", "Could not check") : ("clear", "Nothing found");
            html.AppendLine(Invariant($"<div class=\"check\"><h3><span class=\"badge {status.Item1}\">{status.Item2}</span>{Local(marker.Timestamp, "HH:mm:ss")} · {E(marker.Note ?? "Problem marked")}</h3>"));
            html.AppendLine(Invariant($"<div>{window.Length} samples from 5 minutes before to 2 minutes after.</div><ul>"));
            foreach (var finding in findings) html.AppendLine(Invariant($"<li><strong>{E(finding.Title)}</strong> — {E(finding.Explanation)} {E(string.Join(" ", finding.Evidence))}</li>"));
            foreach (var report in nearbyReports) html.AppendLine(Invariant($"<li>Windows · {E(report.Category)} at {Local(report.Timestamp, "HH:mm:ss")}: {E(report.Provider)} / Event {report.EventId} / {E(report.Component)} — {E(report.Summary)}</li>"));
            foreach (var exit in nearbyExits) html.AppendLine(Invariant($"<li>Program exited at {Local(exit.Timestamp, "HH:mm:ss")}: {E(exit.Note ?? string.Empty)}</li>"));
            var near = window.MinBy(sample => Math.Abs((sample.Timestamp - marker.Timestamp).TotalMilliseconds));
            if (near is not null)
                html.AppendLine(Invariant($"<li>Top CPU at the marker: {E(string.Join(", ", near.Processes.Where(p => p.CpuAvailable).OrderByDescending(p => p.CpuPercent).Take(3).Select(p => Invariant($"{p.Name} ({p.ProcessId}) {p.CpuPercent:F0}%"))))}</li>"));
            if (findings.Count == 0 && nearbyReports.Length == 0 && window.Length > 0) html.AppendLine("<li>No configured sustained condition and no Windows report. Brief stalls and unmeasured sources remain possible.</li>");
            html.AppendLine("</ul></div>");
        }

        html.AppendLine("<h2>Windows reports during the recording</h2>");
        if (windows is null)
            html.AppendLine("<div class=\"check\"><h3><span class=\"badge unable\">Could not check</span>Not saved with this recording</h3><div>Recordings from earlier Skald versions do not include Windows reports. Run Check hardware on the recorded machine instead.</div></div>");
        else
        {
            html.AppendLine(Invariant($"<div class=\"meta\">Read on the recorded machine for {Local(windows.From, "yyyy-MM-dd HH:mm:ss")} to {Local(windows.To, "yyyy-MM-dd HH:mm:ss")}.</div>"));
            if (windows.Events.Count == 0 && windows.Dumps.Count == 0) html.AppendLine("<div class=\"check\"><h3><span class=\"badge clear\">Nothing found</span>No crash, hardware, driver or application report in this window</h3></div>");
            else
            {
                html.AppendLine("<table><thead><tr><th>Time</th><th>Category</th><th>Source</th><th>Summary</th></tr></thead><tbody>");
                foreach (var item in windows.Events)
                    html.AppendLine(Invariant($"<tr><td>{Local(item.Timestamp, "HH:mm:ss")}</td><td>{E(item.Category)}</td><td>{E(item.Provider)} / {item.EventId}<br>{E(item.Component)}</td><td>{E(item.Summary)}</td></tr>"));
                foreach (var dump in windows.Dumps)
                    html.AppendLine(Invariant($"<tr><td>{Local(dump.ModifiedAt, "HH:mm:ss")}</td><td>Dump file</td><td>{E(dump.Kind)}</td><td>{E(dump.Path)} ({dump.SizeBytes / 1048576d:F1} MB)</td></tr>"));
                html.AppendLine("</tbody></table>");
            }
        }

        html.AppendLine("<h2>Programs that exited</h2>");
        if (appExits.Length == 0) html.AppendLine("<div class=\"meta\">No program with a visible window exited during the recording.</div>");
        else
        {
            html.AppendLine("<table><thead><tr><th>Time</th><th>Program</th><th>Windows report within 30 s</th></tr></thead><tbody>");
            foreach (var exit in appExits.Take(100))
            {
                var report = windows?.Events.FirstOrDefault(item => item.Category == "Applications" && exit.ProcessName is { } name
                    && item.Component.Contains(Path.GetFileNameWithoutExtension(name), StringComparison.OrdinalIgnoreCase)
                    && Math.Abs((item.Timestamp - exit.Timestamp).TotalSeconds) <= 30);
                html.AppendLine(Invariant($"<tr><td>{Local(exit.Timestamp, "HH:mm:ss")}</td><td>{E(exit.Note ?? string.Empty)}</td><td>{(report is null ? "None" : E($"{report.Provider} / Event {report.EventId}: {report.Summary}"))}</td></tr>"));
            }
            html.AppendLine("</tbody></table>");
        }
        html.AppendLine(Invariant($"<div class=\"limit\">{exits.Length} process exits and {document.Events.Count(item => item.Type == SessionEventType.ProcessLaunch)} launches in total, including background processes.</div>"));

        html.AppendLine("<h2>Activity and power</h2><div class=\"grid\">");
        Card(html, "Peak CPU", Peak(samples.Where(s => s.Cpu.Availability.IsSupported).Select(s => s.Cpu.UtilizationPercent), "%"));
        Card(html, "Peak memory occupied", Peak(samples.Where(s => s.Memory.Availability.IsSupported).Select(s => s.Memory.UsedPercent), "%"));
        Card(html, "Peak disk active", Peak(samples.Where(s => s.Disk.Availability.IsSupported).Select(s => s.Disk.ActiveTimePercent), "%"));
        Card(html, "Average battery draw", power.AverageDischargeWatts is { } draw ? Invariant($"{draw:F1} W") : "Unavailable");
        foreach (var domain in power.Domains)
            Card(html, domain.Label, Invariant($"avg {domain.AverageWatts:F1} W · peak {domain.PeakWatts:F1} W · {domain.EnergyWh:F2} Wh ({domain.CoveragePercent:F0}% of run)"));
        html.AppendLine("</div><div class=\"limit\">CPU package and GPU board power are separate measured domains and are never added together or treated as whole-system power.</div>");

        html.AppendLine("<h2>Other recorded changes</h2>");
        var changes = document.Events.Where(item => item.Type is SessionEventType.CpuSaturation or SessionEventType.MemoryPressure or SessionEventType.DiskActivity
            or SessionEventType.PowerSourceChanged or SessionEventType.NetworkChanged or SessionEventType.DeepTraceSaved).OrderBy(item => item.Timestamp).ToArray();
        if (changes.Length == 0) html.AppendLine("<div class=\"meta\">None.</div>");
        else html.AppendLine("<table><tbody>" + string.Concat(changes.Take(300).Select(item => Invariant($"<tr><td>{Local(item.Timestamp, "HH:mm:ss")}</td><td>{E(item.Note ?? item.Type.ToString())}</td></tr>"))) + "</tbody></table>");

        if (document.Traces.Count > 0)
            html.AppendLine("<h2>Deep traces</h2><ul>" + string.Concat(document.Traces.Select(trace => Invariant($"<li>{E(trace.FileName)} · {Local(trace.SavedAt, "HH:mm:ss")} · {E(trace.Reason)} · {trace.SizeBytes / 1048576d:F0} MB</li>"))) + "</ul><div class=\"limit\">Open with Windows Performance Analyzer for DPC/ISR time by driver, disk I/O, hard faults and thread waits.</div>");

        html.AppendLine("<h2>Not covered by this recording</h2><ul>");
        html.AppendLine(Invariant($"<li>Events shorter than the {metadata.SampleIntervalSeconds}-second sample interval, unless a deep trace was saved.</li>"));
        html.AppendLine("<li>Why a process exited (exit code, crash or user close); only Windows reports can say it crashed.</li>");
        html.AppendLine("<li>Per-process power. Watts are measured per CPU package or GPU board, never per process.</li>");
        if (windows is null) html.AppendLine("<li>Windows reliability reports and dump files: not saved with this recording.</li>");
        html.AppendLine("</ul>");

        html.AppendLine("<h2>Machine</h2>");
        if (document.Machine is not { } machine) html.AppendLine(Invariant($"<div class=\"meta\">Only the computer name and Windows version were saved: {E(metadata.MachineName)} · {E(metadata.OperatingSystem)}.</div>"));
        else
        {
            html.AppendLine("<table>" + string.Concat(machine.Details.Select(item => $"<tr><td>{E(item.Label)}</td><td>{E(item.Value)}</td></tr>")) + "</table>");
            var key = machine.Drivers.Where(driver => driver.Class is "Display" or "Net" or "HDC" or "SCSIAdapter" or "MEDIA" or "Bluetooth").ToArray();
            if (key.Length > 0)
                html.AppendLine("<h2>Display, network, storage and audio drivers</h2><table><thead><tr><th>Class</th><th>Device</th><th>Provider</th><th>Version</th><th>Date</th></tr></thead><tbody>"
                    + string.Concat(key.Select(driver => $"<tr><td>{E(driver.Class)}</td><td>{E(driver.Name)}</td><td>{E(driver.Provider)}</td><td>{E(driver.Version)}</td><td>{(driver.Date is { } date ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "Unknown")}</td></tr>"))
                    + Invariant($"</tbody></table><div class=\"limit\">{machine.Drivers.Count} drivers in total are saved in the recording.</div>"));
        }

        var sources = (document.Machine?.SourceStatus ?? []).Concat(windows?.SourceStatus ?? []).ToArray();
        if (sources.Length > 0) html.AppendLine("<h2>Sources</h2><pre>" + E(string.Join("\n", sources)) + "</pre>");
        html.AppendLine("</body></html>");
        return html.ToString();
    }

    private static void Card(StringBuilder html, string label, string value) => html.AppendLine(Invariant($"<div class=\"card\"><div>{E(label)}</div><strong>{E(value)}</strong></div>"));

    private static string Peak(IEnumerable<double> values, string unit)
    {
        var data = values.ToArray();
        return data.Length == 0 ? "Unavailable" : Invariant($"{data.Max():F1}{unit}");
    }

    private static string Local(DateTimeOffset time, string format) => time.ToLocalTime().ToString(format, CultureInfo.InvariantCulture);

    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);

    private static string E(string value) => WebUtility.HtmlEncode(value);
}
