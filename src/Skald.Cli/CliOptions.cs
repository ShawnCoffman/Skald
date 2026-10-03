using System.Globalization;

namespace Skald.Cli;

public enum CliCommand { Help, Version, Triage }

public sealed record CliOptions
{
    public CliCommand Command { get; init; } = CliCommand.Help;
    public int Days { get; init; } = 30;
    public string? OutDirectory { get; init; }
    public bool Zip { get; init; }
    public bool Redact { get; init; }
    public string? Baseline { get; init; }
    public string? SaveBaseline { get; init; }
    public string Console { get; init; } = "text";
    public bool Quiet { get; init; }
    public string? Error { get; init; }

    public const string Usage = """
        Skald command line

        Usage:
          skald triage [options]    Check hardware, driver and firmware evidence (about a minute)
          skald version
          skald help

        triage options:
          --days <1-30>           Event window in days (default 30)
          --out <folder>          Write .txt, .json and .html reports to this folder
          --zip                   Write the reports as one .zip bundle (uses the current folder without --out)
          --redact                Remove computer name, serial number, user names and SIDs from console output and files
          --baseline <file>       Compare drivers, BIOS and OS build against this snapshot instead of the previous scan
                                  (the previous-scan snapshot is then left unchanged)
          --save-baseline <file>  Save this machine's driver, BIOS and OS snapshot (for a known-good baseline)
          --console text|json|none  What to print to standard output (default text)
          --quiet                 No progress messages on standard error

        Exit codes: 0 nothing significant (clear or minor findings only), 1 hardware evidence found, 2 check incomplete, 3 error, 64 bad arguments.
        Run as administrator for the most complete result (kernel dump headers, some storage counters).
        Console output is plain ASCII; saved files are UTF-8. Ctrl+C stops the scan; press it twice to quit at once.
        """;

    public static CliOptions Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0] is "help" or "--help" or "-h" or "/?") return new();
        if (args[0] is "version" or "--version") return new() { Command = CliCommand.Version };
        if (!args[0].Equals("triage", StringComparison.OrdinalIgnoreCase)) return new() { Error = $"Unknown command '{args[0]}'." };

        var options = new CliOptions { Command = CliCommand.Triage };
        for (var i = 1; i < args.Count; i++)
        {
            string? Value(string name)
            {
                if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) return args[++i];
                options = options with { Error = $"{name} needs a value." };
                return null;
            }
            switch (args[i].ToLowerInvariant())
            {
                case "--days":
                    if (Value("--days") is { } days)
                        options = int.TryParse(days, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed is >= 1 and <= 30
                            ? options with { Days = parsed } : options with { Error = "--days must be a whole number from 1 to 30." };
                    break;
                case "--out": options = Value("--out") is { } folder ? options with { OutDirectory = folder } : options; break;
                case "--baseline": options = Value("--baseline") is { } file ? options with { Baseline = file } : options; break;
                case "--save-baseline": options = Value("--save-baseline") is { } save ? options with { SaveBaseline = save } : options; break;
                case "--console":
                    if (Value("--console") is { } mode)
                        options = mode is "text" or "json" or "none" ? options with { Console = mode } : options with { Error = "--console must be text, json or none." };
                    break;
                case "--zip": options = options with { Zip = true }; break;
                case "--redact": options = options with { Redact = true }; break;
                case "--quiet": options = options with { Quiet = true }; break;
                default: options = options with { Error = $"Unknown option '{args[i]}'." }; break;
            }
            if (options.Error is not null) break;
        }
        if (options.Error is null && options.Baseline is not null && options.SaveBaseline is not null
            && string.Equals(Path.GetFullPath(options.Baseline), Path.GetFullPath(options.SaveBaseline), StringComparison.OrdinalIgnoreCase))
            options = options with { Error = "--save-baseline would overwrite the --baseline file this scan compares against. Use a different file." };
        return options;
    }
}
