using System.IO.Compression;
using System.Globalization;

namespace Skald.Triage;

public static class TriageBundle
{
    // Writes report.txt/json/html (and the driver inventory) for one scan; returns the paths created.
    public static IReadOnlyList<string> Write(TriageResult result, string directory, bool redact = false, bool zip = false)
    {
        var report = redact ? TriageRedactor.Redact(result.Report) : result.Report;
        var computer = redact ? "host" : Sanitize(Environment.MachineName);
        var stem = $"SKALD-{computer}-{report.GeneratedAt.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";
        Directory.CreateDirectory(directory);
        var files = new List<(string Name, string Content)>
        {
            ($"{stem}.txt", TriageReportWriter.ToText(report)),
            ($"{stem}.json", TriageReportWriter.ToJson(report)),
            ($"{stem}.html", TriageReportWriter.ToHtml(report))
        };
        if (result.Drivers is { } drivers) files.Add(($"{stem}-drivers.json", TriageReportWriter.ToJson(redact ? TriageRedactor.Redact(drivers) : drivers)));

        if (!zip)
        {
            // All or nothing: write every file to a temporary name first, then rename, so a failure leaves no half set behind.
            var staged = new List<(string Temporary, string Final)>();
            try
            {
                foreach (var (name, content) in files)
                {
                    var final = Path.Combine(directory, name);
                    staged.Add((final + ".writing", final));
                    File.WriteAllText(final + ".writing", content);
                }
                foreach (var (stagedPath, final) in staged) File.Move(stagedPath, final, overwrite: true);
                return staged.Select(item => item.Final).ToArray();
            }
            finally
            {
                foreach (var (stagedPath, _) in staged) if (File.Exists(stagedPath)) File.Delete(stagedPath);
            }
        }

        var archive = Path.Combine(directory, stem + ".zip");
        var temporary = archive + ".writing";
        try
        {
            using (var output = File.Create(temporary))
            using (var package = new ZipArchive(output, ZipArchiveMode.Create))
                foreach (var (name, content) in files)
                {
                    var entry = package.CreateEntry(name, CompressionLevel.Optimal);
                    using var writer = new StreamWriter(entry.Open());
                    writer.Write(content);
                }
            File.Move(temporary, archive, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return [archive];
    }

    private static string Sanitize(string value) => string.Concat(value.Where(character => char.IsLetterOrDigit(character) || character is '-' or '_'));
}
