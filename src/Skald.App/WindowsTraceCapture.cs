using System.Diagnostics;
using Skald.Triage;

namespace Skald.App;

// Windows Performance Recorder in memory mode: a rolling buffer of roughly the last minute, written to an ETL only when a problem is
// marked or the recording stops. WPR needs administrator rights. The state file notices a trace left running by a closed or crashed app.
internal sealed class WindowsTraceCapture : IDisposable
{
    private const string InstanceName = "SkaldIncident";
    private readonly string _wprPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "wpr.exe");
    private readonly string _statePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Skald", "wpr-active.txt");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WindowsTraceCapture() => IsRecording = File.Exists(_statePath);

    public bool IsRecording { get; private set; }
    public bool IsSaving { get; private set; }
    public static bool IsElevated => TriageScanner.IsElevated();
    public bool IsAvailable => File.Exists(_wprPath);

    // Null on success, otherwise why the trace could not start.
    public async Task<string?> StartAsync()
    {
        if (!IsAvailable) return "Windows Performance Recorder (wpr.exe) is not available on this machine.";
        if (!IsElevated) return "Deep trace needs Skald running as administrator.";
        await _gate.WaitAsync().ConfigureAwait(false);
        try { return await StartCoreAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<string?> StartCoreAsync()
    {
        if (IsRecording) return null;
        // Memory mode is WPR's default: nothing is written to disk until a save.
        var result = await RunAsync("-start", "GeneralProfile.light", "-instancename", InstanceName).ConfigureAwait(false);
        if (result.ExitCode != 0) return $"Could not start the deep trace: {result.Message}";
        IsRecording = true;
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        await File.WriteAllTextAsync(_statePath, DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture)).ConfigureAwait(false);
        return null;
    }

    // Writes the buffer to path. With restart, a new buffer starts at once so a later problem is covered too.
    public async Task<(string? Path, string Message)> SaveAsync(string path, string description, bool restart)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        IsSaving = true;
        try
        {
            if (!IsRecording) return (null, "No deep trace is running.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // -skipPdbGen keeps the trace one file and the save quick; kernel and driver stacks still resolve from symbol servers.
            var result = await RunAsync("-stop", path, description, "-skipPdbGen", "-compress", "-instancename", InstanceName).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                if (result.Message.Contains("no trace profiles running", StringComparison.OrdinalIgnoreCase)
                    || result.Message.Contains("not recording", StringComparison.OrdinalIgnoreCase))
                {
                    ClearState();
                    return (null, "The deep trace was no longer running.");
                }
                return (null, $"Could not save the deep trace: {result.Message}");
            }
            ClearState();
            var restartError = restart ? await StartCoreAsync().ConfigureAwait(false) : null;
            return (File.Exists(path) ? path : null, restartError is null ? "Deep trace saved." : $"Deep trace saved; it could not restart: {restartError}");
        }
        finally
        {
            IsSaving = false;
            _gate.Release();
        }
    }

    public async Task MarkAsync(string label)
    {
        if (!IsRecording) return;
        try { await RunAsync("-marker", label, "-instancename", InstanceName).ConfigureAwait(false); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { /* A WPR marker must not interrupt the Skald marker. */ }
    }

    // Discards the buffer. Used when the app closes, so a kernel trace is not left running with nobody to save it.
    public void Cancel()
    {
        if (!IsRecording || !IsElevated) return;
        try
        {
            using var process = Process.Start(StartInfo("-cancel", "-instancename", InstanceName));
            process?.WaitForExit(10000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        ClearState();
    }

    public void Dispose() => _gate.Dispose();

    private void ClearState()
    {
        IsRecording = false;
        try { if (File.Exists(_statePath)) File.Delete(_statePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private ProcessStartInfo StartInfo(params string[] arguments)
    {
        var start = new ProcessStartInfo(_wprPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    private async Task<(int ExitCode, string Message)> RunAsync(params string[] arguments)
    {
        using var process = Process.Start(StartInfo(arguments)) ?? throw new InvalidOperationException("WPR did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        var message = (await error.ConfigureAwait(false)).Trim();
        if (message.Length == 0) message = (await output.ConfigureAwait(false)).Trim();
        return (process.ExitCode, message.Length == 0 ? $"exit code {process.ExitCode}" : message);
    }
}
