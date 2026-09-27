using System.Diagnostics;

namespace Skald.App;

// WPR uses a bounded memory-mode trace. Saving it is always an explicit user action.
internal sealed class WindowsTraceCapture
{
    private const string InstanceName = "SkaldIncident";
    private readonly string _wprPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "wpr.exe");
    private readonly string _statePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Skald", "wpr-active.txt");
    public bool IsRecording { get; private set; }

    public WindowsTraceCapture() => IsRecording = File.Exists(_statePath);

    public async Task<string> StartAsync()
    {
        if (IsRecording) return "Deep trace is already running.";
        if (!File.Exists(_wprPath)) return "Windows Performance Recorder (wpr.exe) is unavailable on this machine.";
        var result = await RunAsync("-start", "GeneralProfile.light", "-instancename", InstanceName);
        if (result.ExitCode != 0) return $"Could not start WPR: {result.Message}";
        IsRecording = true;
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        await File.WriteAllTextAsync(_statePath, DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        return "WPR General profile is recording in memory. Reproduce the issue, press Mark Problem, then save the trace here.";
    }

    public async Task<string> StopAsync()
    {
        if (!IsRecording) return "No Skald deep trace is running.";
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var directory = Path.Combine(string.IsNullOrWhiteSpace(documents) ? AppContext.BaseDirectory : documents, "Skald Traces");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"Skald_{DateTime.Now:yyyyMMdd_HHmmss_fff}.etl");
        var result = await RunAsync("-stop", path, "Skald marked performance incident", "-instancename", InstanceName);
        if (result.ExitCode != 0)
        {
            if (result.Message.Contains("no trace profiles running", StringComparison.OrdinalIgnoreCase)
                || result.Message.Contains("not recording", StringComparison.OrdinalIgnoreCase))
            {
                IsRecording = false;
                if (File.Exists(_statePath)) File.Delete(_statePath);
                return "No Skald WPR trace was running. Cleared the stale trace state.";
            }
            return $"Could not save WPR trace: {result.Message}. Recording may still be active.";
        }
        IsRecording = false;
        if (File.Exists(_statePath)) File.Delete(_statePath);
        return $"Saved {path}. Open this ETL in Windows Performance Analyzer for thread waits, disk I/O, DPCs, and hard faults.";
    }

    public async Task MarkAsync()
    {
        if (!IsRecording) return;
        try { await RunAsync("-marker", "Skald: problem marked", "-instancename", InstanceName); }
        catch { /* A WPR marker must not interrupt the Skald session marker. */ }
    }

    private async Task<(int ExitCode, string Message)> RunAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo(_wprPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("WPR did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var message = (await error).Trim();
        if (message.Length == 0) message = (await output).Trim();
        return (process.ExitCode, message.Length == 0 ? $"exit code {process.ExitCode}" : message);
    }
}
