using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Skald.Triage;

public static class TriageReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // The JSON is a file, not embedded markup: keep <, >, and non-ASCII punctuation readable.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    // Escapes every non-ASCII character (\uXXXX), for consoles and shells that do not decode UTF-8.
    private static readonly JsonSerializerOptions AsciiJsonOptions = new(JsonOptions) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default };

    public static string ToJson(TriageReport report, bool asciiOnly = false) => JsonSerializer.Serialize(report, asciiOnly ? AsciiJsonOptions : JsonOptions);

    public static string ToJson(DriverInventory inventory) => JsonSerializer.Serialize(inventory, JsonOptions);

    public static string ToText(TriageReport report)
    {
        var text = new StringBuilder();
        text.AppendLine("SKALD HARDWARE CHECK");
        text.AppendLine(FormattableString.Invariant($"Scanned {report.GeneratedAt.ToLocalTime():yyyy-MM-dd HH:mm} · window {report.WindowDays} days · {(report.Elevated ? "administrator" : "standard user")} · Skald {report.ToolVersion}"));
        text.AppendLine();
        text.AppendLine(FormattableString.Invariant($"VERDICT: {report.Verdict.Headline.ToUpperInvariant()}"));
        text.AppendLine(Wrap(report.Verdict.Detail, 0));
        if (report.UpdateEvidence.Count > 0)
        {
            text.AppendLine();
            text.AppendLine(TriageLabels.UpdateSection.ToUpperInvariant());
            foreach (var note in report.UpdateEvidence)
            {
                text.AppendLine(FormattableString.Invariant($"  [{TriageLabels.Update(note.Lean)}] {note.Subject}"));
                text.AppendLine(Wrap(note.Text, 6));
            }
        }
        foreach (var domain in TriageLabels.DomainOrder)
        {
            var checks = report.In(domain).ToArray();
            if (checks.Length == 0) continue;
            text.AppendLine();
            text.AppendLine(TriageLabels.Domain(domain).ToUpperInvariant());
            foreach (var check in checks)
            {
                text.AppendLine(FormattableString.Invariant($"  [{TriageLabels.Status(check)}] {check.Title}"));
                text.AppendLine(Wrap(check.Summary, 6));
                foreach (var detail in check.Details) text.AppendLine(Wrap("- " + detail, 8));
                if (check.Limit is not null) text.AppendLine(Wrap("Note: " + check.Limit, 6));
            }
        }
        text.AppendLine();
        text.AppendLine("NOT COVERED BY THIS SCAN");
        foreach (var item in report.NotCovered) text.AppendLine(Wrap("- " + item, 2));
        text.AppendLine();
        text.AppendLine("MACHINE");
        foreach (var item in report.Machine) text.AppendLine(FormattableString.Invariant($"  {item.Label}: {item.Value}"));
        text.AppendLine();
        text.AppendLine("SOURCES");
        foreach (var item in report.SourceStatus) text.AppendLine("  " + item);
        return text.ToString();
    }

    public static string ToHtml(TriageReport report)
    {
        static string E(string value) => WebUtility.HtmlEncode(value);
        static string Css(TriageCheck check) => check.Status switch
        {
            CheckStatus.Found => check.Domain == CheckDomain.HardwareDriverFirmware && !check.Minor ? "found" : "noted",
            CheckStatus.CouldNotCheck => "unable",
            CheckStatus.NotApplicable => "na",
            _ => "clear"
        };
        var html = new StringBuilder();
        html.AppendLine("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        html.AppendLine("<title>Skald hardware check</title><style>");
        html.AppendLine(":root{--bg:#f7f9fa;--fg:#14232b;--card:#fff;--line:#d5dfe3;--muted:#5b6f78;--found:#b3261e;--noted:#8a5a00;--clear:#1b6e3c;--unable:#555;--na:#777}");
        html.AppendLine("@media (prefers-color-scheme:dark){:root{--bg:#0b151d;--fg:#e6f0f2;--card:#111f29;--line:#27404c;--muted:#9bb3bc;--found:#ff8a80;--noted:#ffcc66;--clear:#6fd69a;--unable:#b9c4c9;--na:#8a9aa2}}");
        html.AppendLine("body{font:15px/1.5 Segoe UI,system-ui,sans-serif;background:var(--bg);color:var(--fg);margin:0;padding:24px;max-width:960px;margin-inline:auto}");
        html.AppendLine("h1{margin:0 0 4px;font-size:24px}h2{font-size:15px;letter-spacing:.06em;text-transform:uppercase;color:var(--muted);margin:28px 0 8px}");
        html.AppendLine(".meta{color:var(--muted);font-size:13px}.verdict{border:1px solid var(--line);border-left:6px solid var(--clear);background:var(--card);padding:14px 18px;border-radius:6px;margin-top:16px}");
        html.AppendLine(".verdict.found{border-left-color:var(--found)}.verdict.minor{border-left-color:var(--noted)}.verdict.incomplete{border-left-color:var(--unable)}.verdict h2{margin:0 0 6px;color:var(--fg);text-transform:none;letter-spacing:0;font-size:19px}");
        html.AppendLine(".check{border:1px solid var(--line);background:var(--card);border-radius:6px;padding:10px 14px;margin:8px 0}.check h3{margin:0;font-size:15px}");
        html.AppendLine(".badge{display:inline-block;font-size:11px;font-weight:600;padding:1px 8px;border-radius:10px;border:1px solid currentColor;margin-right:8px;vertical-align:1px}");
        html.AppendLine(".found{color:var(--found)}.noted{color:var(--noted)}.clear{color:var(--clear)}.unable{color:var(--unable)}.na{color:var(--na)}");
        html.AppendLine("ul{margin:6px 0 0;padding-left:20px}.limit{color:var(--muted);font-size:13px;margin-top:6px}table{border-collapse:collapse}td{padding:2px 14px 2px 0;vertical-align:top}pre{white-space:pre-wrap;color:var(--muted);font-size:12px}");
        html.AppendLine("</style></head><body>");
        html.AppendLine(FormattableString.Invariant($"<h1>Skald hardware check</h1><div class=\"meta\">Scanned {E(report.GeneratedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))} · last {report.WindowDays} days · {(report.Elevated ? "administrator" : "standard user")} · Skald {E(report.ToolVersion)}</div>"));
        var verdictClass = report.Verdict.Outcome switch { TriageOutcome.HardwareEvidenceFound => "found", TriageOutcome.MinorFindings => "minor", TriageOutcome.Incomplete => "incomplete", _ => "" };
        html.AppendLine(FormattableString.Invariant($"<div class=\"verdict {verdictClass}\"><h2>{E(report.Verdict.Headline)}</h2><div>{E(report.Verdict.Detail)}</div></div>"));
        if (report.UpdateEvidence.Count > 0)
        {
            html.AppendLine(FormattableString.Invariant($"<h2>{E(TriageLabels.UpdateSection)}</h2>"));
            foreach (var note in report.UpdateEvidence)
            {
                var css = note.Lean switch { UpdateLean.PointsHere => "found", UpdateLean.CouldNotAssess => "unable", _ => "clear" };
                html.AppendLine(FormattableString.Invariant($"<div class=\"check\"><h3><span class=\"badge {css}\">{E(TriageLabels.Update(note.Lean))}</span>{E(note.Subject)}</h3><div>{E(note.Text)}</div></div>"));
            }
        }
        foreach (var domain in TriageLabels.DomainOrder)
        {
            var checks = report.In(domain).ToArray();
            if (checks.Length == 0) continue;
            html.AppendLine(FormattableString.Invariant($"<h2>{E(TriageLabels.Domain(domain))}</h2>"));
            foreach (var check in checks)
            {
                html.AppendLine(FormattableString.Invariant($"<div class=\"check\"><h3><span class=\"badge {Css(check)}\">{E(TriageLabels.Status(check))}</span>{E(check.Title)}</h3><div>{E(check.Summary)}</div>"));
                if (check.Details.Count > 0) html.AppendLine("<ul>" + string.Concat(check.Details.Select(detail => $"<li>{E(detail)}</li>")) + "</ul>");
                if (check.Limit is not null) html.AppendLine(FormattableString.Invariant($"<div class=\"limit\">{E(check.Limit)}</div>"));
                html.AppendLine("</div>");
            }
        }
        html.AppendLine("<h2>Not covered by this scan</h2><ul>" + string.Concat(report.NotCovered.Select(item => $"<li>{E(item)}</li>")) + "</ul>");
        html.AppendLine("<h2>Machine</h2><table>" + string.Concat(report.Machine.Select(item => $"<tr><td>{E(item.Label)}</td><td>{E(item.Value)}</td></tr>")) + "</table>");
        html.AppendLine("<h2>Sources</h2><pre>" + E(string.Join("\n", report.SourceStatus)) + "</pre>");
        html.AppendLine("</body></html>");
        return html.ToString();
    }

    private static string Wrap(string text, int indent, int width = 100)
    {
        var padding = new string(' ', indent);
        var builder = new StringBuilder();
        var line = new StringBuilder(padding);
        foreach (var word in text.Replace("\r", string.Empty, StringComparison.Ordinal).Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length + word.Length + 1 > width && line.Length > indent) { builder.AppendLine(line.ToString()); line.Clear().Append(padding).Append("  "); }
            if (line.Length > indent && line[^1] != ' ') line.Append(' ');
            line.Append(word);
        }
        builder.Append(line);
        return builder.ToString();
    }
}
