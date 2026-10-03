using Skald.Triage;

namespace Skald.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var options = CliOptions.Parse(args);
        if (options.Error is not null)
        {
            await Console.Error.WriteLineAsync(options.Error);
            await Console.Error.WriteLineAsync(CliOptions.Usage);
            return 64;
        }
        switch (options.Command)
        {
            case CliCommand.Help:
                Console.WriteLine(CliOptions.Usage);
                return 0;
            case CliCommand.Version:
                Console.WriteLine($"skald {TriageInfo.Version}");
                return 0;
        }
        if (options.Baseline is not null && !File.Exists(options.Baseline))
        {
            await Console.Error.WriteLineAsync($"Baseline file not found: {Scrub(options.Baseline)}");
            return 64;
        }

        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            // The first Ctrl+C stops waiting for the scan; a second one ends the process at once.
            if (cancel.IsCancellationRequested) return;
            eventArgs.Cancel = true;
            cancel.Cancel();
            Console.Error.WriteLine("Stopping... press Ctrl+C again to quit immediately.");
        };

        var scanOptions = new TriageOptions { WindowDays = options.Days, BaselinePath = options.Baseline, SaveBaselinePath = options.SaveBaseline };
        TriageResult result;
        try
        {
            var progress = options.Quiet ? null : new Progress<string>(message => Console.Error.WriteLine(ConsoleText.Ascii(message)));
            result = await TriageScanner.RunAsync(scanOptions, progress, cancel.Token);
            cancel.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            await Console.Error.WriteLineAsync("Cancelled.");
            return 3;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Scan failed: {Scrub(ex.Message)}");
            return 3;
        }

        // Print first: a failed file write must not throw away a finished scan.
        var report = options.Redact ? TriageRedactor.Redact(result.Report) : result.Report;
        switch (options.Console)
        {
            case "text": Console.Out.Write(ConsoleText.Ascii(TriageReportWriter.ToText(report))); break;
            case "json": Console.Out.WriteLine(TriageReportWriter.ToJson(report, asciiOnly: true)); break;
        }

        if (options.OutDirectory is not null || options.Zip)
        {
            try
            {
                foreach (var path in TriageBundle.Write(result, options.OutDirectory ?? Directory.GetCurrentDirectory(), options.Redact, options.Zip))
                    await Console.Error.WriteLineAsync($"Wrote {Scrub(path)}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // The snapshots are not committed, so the next scan still compares against the same point.
                await Console.Error.WriteLineAsync($"Could not write the report files: {Scrub(ex.Message)}");
                return 3;
            }
        }
        foreach (var problem in TriageScanner.CommitSnapshots(result, scanOptions))
            await Console.Error.WriteLineAsync(Scrub(problem));
        return TriageLabels.ExitCode(report.Verdict.Outcome);

        string Scrub(string text) => ConsoleText.Ascii(options.Redact
            ? TriageRedactor.Redact(text, Environment.MachineName, Environment.UserName, Environment.UserDomainName) : text);
    }
}
