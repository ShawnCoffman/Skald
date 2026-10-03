using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using Skald.App.Pages;
using Skald.App.Controls;
using Skald.Collectors;
using Skald.Core.Models;
using Skald.Diagnostics;
using Skald.Recorder;

namespace Skald.App;

public sealed partial class MainWindow : Window, IDisposable
{
    private readonly WindowsMetricsCollector _collector = new();
    private readonly InitialDiagnosticAnalyzer _analyzer = new();
    private readonly RollingTelemetryBuffer _rollingBuffer = new(TimeSpan.FromMinutes(5));
    // A recording longer than this is saved and stopped: every sample stays in memory until Stop (about 110 MB per hour).
    private static readonly TimeSpan MaximumRecording = TimeSpan.FromHours(2);
    // A recording started by Mark Problem saves itself this long after the last marker.
    private static readonly TimeSpan MarkerTail = TimeSpan.FromMinutes(2);
    private readonly FlightRecorder _recorder = new();
    private readonly WindowsTraceCapture _trace = new();
    private readonly RecordingPreferences _preferences = RecordingPreferences.Load();
    private readonly DispatcherQueueTimer _timer;
    private readonly HomePage _homePage = new();
    private readonly TriagePage _triagePage = new();
    private readonly SummaryPage _summaryPage = new();
    private readonly PerformancePage _performancePage = new();
    private readonly PowerPage _powerPage = new();
    private readonly HardwarePage _hardwarePage = new();
    private readonly GpuPage _gpuPage = new();
    private readonly ProcessesPage _processesPage = new();
    private readonly CpuPage _cpuPage = new();
    private readonly MemoryPage _memoryPage = new();
    private readonly DisksPage _disksPage = new();
    private readonly NetworkPage _networkPage = new();
    private readonly ReliabilityPage _reliabilityPage = new();
    private readonly UpdatesPage _updatesPage = new();
    private readonly SessionReplayPage _recordingsPage = new();
    private SystemMetricsSnapshot? _latestSnapshot;
    private ITelemetryPage? _activePage;
    private bool _isSampling;
    private bool _isStoppingRecording;
    private DateTimeOffset? _powerRecordingStopAt;
    private DateTimeOffset? _markerStopAt;
    private Task<SessionMachine>? _machineTask;
    private Task? _traceSave;
    private int _traceCount;

    public MainWindow()
    {
        InitializeComponent();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Skald.ico"));
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Resize(new SizeInt32(Math.Min(1380, workArea.Width), Math.Min(900, workArea.Height)));
        ContentFrame.Content = _homePage;
        _activePage = _homePage;
        RootNavigation.SelectedItem = RootNavigation.MenuItems[0];
        void Navigate(string tag)
        {
            if (FindNavigationItem(RootNavigation.MenuItems, tag) is { } item) RootNavigation.SelectedItem = item;
        }
        _summaryPage.NavigateRequested += Navigate;
        _homePage.NavigateRequested += Navigate;
        _triagePage.NavigateRequested += Navigate;
        _homePage.RunHardwareCheckRequested += async () => await _triagePage.RunAsync();
        _homePage.AnalyzeRequested += AnalyzePerformance;
        void OpenProcesses() => Navigate("processes");
        _cpuPage.ProcessesRequested += OpenProcesses;
        _memoryPage.ProcessesRequested += OpenProcesses;
        _gpuPage.ProcessesRequested += OpenProcesses;
        _disksPage.ProcessesRequested += OpenProcesses;
        _powerPage.TimedRecordingRequested += StartTimedPowerRecording;

        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(2);
        _timer.Tick += Timer_Tick;
        _timer.Start();
        Closed += MainWindow_Closed;
        _ = RecoverInterruptedRecordingsAsync();
        _ = SaveLeftoverTraceAsync();
    }

    // Windows reports for the recording's own window, read on this machine while the session is saved.
    // ConfigureAwait(false) matters: Dispose blocks the UI thread on StopAsync, and a continuation posted back to it would deadlock.
    private static async Task<SessionWindowsEvidence?> CollectWindowsEvidence(SessionMetadata metadata, CancellationToken cancellationToken)
        => await SessionEvidenceCollector.CollectWindowsAsync(metadata.StartedAt, metadata.EndedAt).WaitAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);

    // A recovered recording ended in a crash, freeze or power loss; the boot that followed logs the unclean shutdown, so the window
    // runs on past the last sample. Journals from another machine (a copied folder) get no local evidence.
    private static async Task<SessionWindowsEvidence?> CollectRecoveryEvidence(SessionMetadata metadata, CancellationToken cancellationToken)
    {
        if (!metadata.MachineName.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return null;
        var until = DateTimeOffset.UtcNow < metadata.EndedAt.AddHours(24) ? DateTimeOffset.UtcNow : metadata.EndedAt.AddHours(24);
        return await SessionEvidenceCollector.CollectWindowsAsync(metadata.StartedAt, until).WaitAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
    }

    // A recording that was running when the app, Windows or the power died leaves a journal behind; turn it back into a session.
    private async Task RecoverInterruptedRecordingsAsync()
    {
        try
        {
            var recovered = 0;
            var failed = 0;
            foreach (var journal in SessionLocations.SearchDirectories.SelectMany(FlightRecorder.FindInterruptedJournals))
            {
                // One damaged journal must not stop the others from being recovered.
                try { if (await FlightRecorder.RecoverAsync(journal, CollectRecoveryEvidence) is not null) recovered++; }
                // InvalidOperationException is how Brotli reports corrupt data.
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException or InvalidOperationException) { failed++; }
            }
            if (recovered == 0 && failed == 0) return;
            RecorderStatusText.Text = (recovered > 0 ? $"Recovered {recovered} interrupted recording{(recovered == 1 ? string.Empty : "s")}" : string.Empty)
                + (failed > 0 ? $"{(recovered > 0 ? " · " : string.Empty)}{failed} interrupted recording(s) could not be recovered" : string.Empty);
            if (recovered > 0) await _recordingsPage.RefreshSessionsAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RecorderStatusText.Text = $"Could not look for interrupted recordings: {ex.Message}";
        }
    }

    private async void Timer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_isSampling) return;
        _isSampling = true;
        try
        {
            var snapshot = await _collector.SampleAsync();
            _latestSnapshot = snapshot;
            _rollingBuffer.Add(snapshot);
            _recorder.Append(snapshot);
            UpdateDashboard(snapshot);
            if (_recorder.IsRecording && !_isStoppingRecording && _powerRecordingStopAt is null
                && (_markerStopAt is { } markerStop && DateTimeOffset.UtcNow >= markerStop
                    || _recorder.StartedAt is { } started && DateTimeOffset.UtcNow - started >= MaximumRecording))
            {
                var limit = _markerStopAt is { } due && DateTimeOffset.UtcNow >= due ? "Saved 2 minutes after the last marker" : "Saved at the 2-hour recording limit";
                if (await StopRecordingAsync()) RecorderStatusText.Text = $"{limit} · {RecorderStatusText.Text}";
            }
            else if (_powerRecordingStopAt is { } stopAt && DateTimeOffset.UtcNow >= stopAt)
            {
                _powerRecordingStopAt = null;
                if (await StopRecordingAsync())
                    _powerPage.SetRecordingStatus("30-minute power recording saved. Open Recordings to compare it with a baseline.");
                else
                    _powerPage.SetRecordingStatus("Could not save the power recording. Use the main Stop button to retry.");
            }
            else if (_powerRecordingStopAt is { } futureStop)
            {
                var remaining = futureStop - DateTimeOffset.UtcNow;
                _powerPage.SetRecordingStatus($"Recording power and workload data · {remaining.Minutes:00}:{remaining.Seconds:00} until automatic save.");
            }
        }
        catch (Exception ex)
        {
            RecorderStatusText.Text = "Telemetry error";
            LastUpdatedText.Text = ex.Message;
        }
        finally
        {
            _isSampling = false;
        }
    }

    private void UpdateDashboard(SystemMetricsSnapshot snapshot)
    {
        var findings = _analyzer.Analyze(snapshot);
        // Sample globally; render only the visible page. Navigation rebuilds charts from the shared history.
        var history = _rollingBuffer.GetWindow(snapshot.Timestamp, TimeSpan.FromMinutes(3), TimeSpan.Zero);
        using var scroll = _activePage is Page visiblePage ? ScrollPositionKeeper.Capture(visiblePage) : null;
        switch (_activePage)
        {
            case HomePage page: page.ShowHistory(history); break;
            case SummaryPage page: page.ShowHistory(history, findings); break;
            case PerformancePage page: page.ShowHistory(history); break;
            case PowerPage page: page.ShowHistory(history); break;
            case CpuPage page: page.ShowHistory(history); break;
            case MemoryPage page: page.ShowHistory(history); break;
            case GpuPage page: page.ShowHistory(history); break;
            case DisksPage page: page.ShowHistory(history); break;
            case NetworkPage page: page.ShowHistory(history); break;
            case HardwarePage page: page.ShowHistory(history); break;
            default: _activePage?.Update(snapshot, findings); break;
        }
        LastUpdatedText.Text = $"Updated {snapshot.Timestamp.ToLocalTime():HH:mm:ss}";
        if (_recorder.IsRecording && !_isStoppingRecording && !_trace.IsSaving)
        {
            RecorderStatusText.Text = $"Recording · {_recorder.SampleCount} samples"
                + (_trace.IsRecording ? " · deep trace on" : string.Empty)
                + (_markerStopAt is { } stop ? $" · saves in {Math.Max(0, (stop - DateTimeOffset.UtcNow).TotalSeconds):F0} s" : string.Empty);
        }
    }


    private static NavigationViewItem? FindNavigationItem(IEnumerable<object> items, string tag)
    {
        foreach (var item in items.OfType<NavigationViewItem>())
        {
            if (item.Tag as string == tag) return item;
            if (FindNavigationItem(item.MenuItems, tag) is { } nested) return nested;
        }
        return null;
    }

    private void RootNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag) return;

        _activePage = tag switch
        {
            "home" => _homePage,
            "triage" => _triagePage,
            "summary" => _summaryPage,
            "performance" => _performancePage,
            "cpu" => _cpuPage,
            "memory" => _memoryPage,
            "reliability" => _reliabilityPage,
            "updates" => _updatesPage,
            "gpu" => _gpuPage,
            "processes" => _processesPage,
            "disks" => _disksPage,
            "network" => _networkPage,
            "power" => _powerPage,
            "hardware" => _hardwarePage,
            "sessions" => _recordingsPage,
            _ => _homePage
        };

        ContentFrame.Content = _activePage;
        if (_latestSnapshot is not null)
        {
            UpdateDashboard(_latestSnapshot);
        }
    }

    private async void AnalyzePerformance()
    {
        var samples = _latestSnapshot is null ? [] : _rollingBuffer.GetWindow(_latestSnapshot.Timestamp, TimeSpan.FromSeconds(60), TimeSpan.Zero);
        var findings = WindowDiagnosticAnalyzer.Analyze(samples);
        var duration = samples.Count > 1 ? (samples[^1].Timestamp - samples[0].Timestamp).TotalSeconds : 0;
        var expectedSamples = Math.Max(1, (int)Math.Floor(duration / 2) + 1);
        var coverage = Math.Min(100, samples.Count * 100d / expectedSamples);
        var content = $"Last {duration:F0} seconds · {samples.Count} samples · about {coverage:F0}% cadence coverage\n\n" + (duration < 30
            ? "Gathering history. At least 30 seconds are needed for sustained-condition analysis."
            : findings.Count == 0 ? "No configured sustained condition was detected in the available samples. This does not rule out brief stalls, driver waits, thermal limits, or unavailable counters."
            : string.Join("\n\n", findings.Select(finding => $"{finding.Title} — {finding.Severity}\n{finding.Explanation}\n{string.Join("\n", finding.Evidence)}")));

        var dialog = new ContentDialog
        {
            Title = "Analyze Performance",
            Content = new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "Close",
            XamlRoot = Content.XamlRoot
        };
        await dialog.ShowAsync();
    }

    private void AnalyzePerformance_Click(object sender, RoutedEventArgs e) => AnalyzePerformance();

    private async void RecordButton_Click(SplitButton sender, SplitButtonClickEventArgs args)
    {
        if (_isStoppingRecording) return;
        if (!_recorder.IsRecording)
        {
            StartRecording();
            return;
        }

        var wasTimedPowerRecording = _powerRecordingStopAt.HasValue;
        _powerRecordingStopAt = null;
        if (await StopRecordingAsync() && wasTimedPowerRecording)
            _powerPage.SetRecordingStatus("Power recording saved early. Open Recordings to analyze it.");
    }

    private void StartRecording()
    {
        var directory = SessionLocations.RecordingsDirectory;
        var fileName = $"Skald_{DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture)}.perfsession";
        var path = Path.Combine(directory, fileName);
        var history = _rollingBuffer.GetSnapshot();
        _recorder.Start(path, 2, history);
        _markerStopAt = null;
        _traceCount = 0;
        _traceSave = null;
        _machineTask = SessionEvidenceCollector.CollectMachineAsync();
        _ = AttachMachineAsync(_machineTask, path);
        _recordingsPage.SetActiveRecordingPath(path);
        RecordButton.Content = "■ Stop";
        _recordingsPage.SetRecording(true);
        RecorderStatusText.Text = $"Recording · {history.Count} buffered samples";
        if (_preferences.IncludeDeepTrace && WindowsTraceCapture.IsElevated && _trace.IsAvailable) _ = StartTraceAsync();
    }

    // Machine identity and drivers take several seconds to read; they join the recording when ready.
    private async Task AttachMachineAsync(Task<SessionMachine> machine, string path)
    {
        try
        {
            var result = await machine;
            if (string.Equals(_recorder.FilePath, path, StringComparison.OrdinalIgnoreCase)) _recorder.SetMachine(result);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* The recording is still useful without machine identity. */ }
    }

    private async Task StartTraceAsync()
    {
        try
        {
            var error = await _trace.StartAsync();
            if (error is not null) RecorderStatusText.Text = error;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or IOException or UnauthorizedAccessException)
        { RecorderStatusText.Text = $"Deep trace failed to start: {ex.Message}"; }
    }

    // Saves the deep-trace buffer beside the recording and attaches it. With restart a new buffer covers the next problem.
    private async Task SaveTraceAsync(string suffix, string reason, bool restart)
    {
        if (_recorder.FilePath is not { } session || !_trace.IsRecording) return;
        var path = Path.ChangeExtension(session, null) + $".{suffix}.etl";
        RecorderStatusText.Text = "Saving deep trace…";
        try
        {
            var (saved, message) = await _trace.SaveAsync(path, $"Skald: {reason}", restart);
            if (saved is not null) _recorder.AddTrace(new SessionTrace(Path.GetFileName(saved), DateTimeOffset.UtcNow, reason, new FileInfo(saved).Length));
            RecorderStatusText.Text = saved is null ? message : $"{message} {Path.GetFileName(saved)}";
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or IOException or UnauthorizedAccessException)
        { RecorderStatusText.Text = $"Deep trace could not be saved: {ex.Message}"; }
    }

    // A trace still running from an earlier run that closed or crashed holds whatever led up to that; save it rather than lose it.
    private async Task SaveLeftoverTraceAsync()
    {
        if (!_trace.IsRecording) return;
        if (!WindowsTraceCapture.IsElevated)
        {
            RecorderStatusText.Text = "A deep trace from an earlier Skald run may still be running. Restart Skald as administrator to save it.";
            return;
        }
        var path = Path.Combine(SessionLocations.RecordingsDirectory, $"Skald_{DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture)}.leftover.etl");
        try
        {
            var (saved, message) = await _trace.SaveAsync(path, "Skald: trace left running by an earlier run", restart: false);
            RecorderStatusText.Text = saved is null ? message : $"Saved a deep trace left running by an earlier Skald run: {Path.GetFileName(saved)}";
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or IOException or UnauthorizedAccessException)
        { RecorderStatusText.Text = $"Could not save a deep trace left by an earlier run: {ex.Message}"; }
    }

    private void StartTimedPowerRecording()
    {
        if (_isStoppingRecording)
        {
            _powerPage.SetRecordingStatus("The previous recording is still being saved. Try again in a moment.");
            return;
        }
        if (_recorder.IsRecording)
        {
            _powerPage.SetRecordingStatus(_powerRecordingStopAt is null
                ? "A recording is already active. Stop it before starting a dedicated 30-minute power recording."
                : "The 30-minute power recording is already in progress.");
            return;
        }
        StartRecording();
        _powerRecordingStopAt = DateTimeOffset.UtcNow.AddMinutes(30);
        _powerPage.SetRecordingStatus("Recording scheduled to save automatically after 30 minutes. You can stop it earlier with the main Record button.");
    }

    private async Task<bool> StopRecordingAsync()
    {
        if (_isStoppingRecording) return false;
        _isStoppingRecording = true;
        RecordButton.IsEnabled = false;
        MarkerButton.IsEnabled = false;
        try
        {
            RecorderStatusText.Text = "Saving recording…";
            if (_traceSave is { } pending) await pending;
            if (_trace.IsRecording) await SaveTraceAsync("end", "End of recording", restart: false);
            if (_machineTask is { IsCompleted: false } machine) await Task.WhenAny(machine, Task.Delay(TimeSpan.FromSeconds(15)));
            if (_machineTask is { IsCompletedSuccessfully: true } done) _recorder.SetMachine(done.Result);
            RecorderStatusText.Text = "Saving recording and the Windows reports from its time window…";
            string? savedPath;
            try { savedPath = await _recorder.StopAsync(CollectWindowsEvidence); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RecorderStatusText.Text = $"Could not save recording: {ex.Message}. Press Stop to retry.";
                return false;
            }
            _markerStopAt = null;
            _machineTask = null;
            _traceSave = null;
            _recordingsPage.SetActiveRecordingPath(null);
            RecordButton.Content = "● Record";
            _recordingsPage.SetRecording(false);
            await _recordingsPage.RefreshSessionsAsync();
            RecorderStatusText.Text = savedPath is null ? "Rolling history: 5 min" : $"Saved · {Path.GetFileName(savedPath)}";
            return true;
        }
        finally
        {
            RecordButton.IsEnabled = true;
            MarkerButton.IsEnabled = true;
            _isStoppingRecording = false;
        }
    }

    private async void MarkerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isStoppingRecording) return;
        var started = !_recorder.IsRecording;
        if (started) StartRecording();
        _recorder.AddMarker();
        // A recording that only exists because of Mark Problem keeps 5 minutes before (pre-roll) and 2 minutes after the last marker.
        if (started || _markerStopAt is not null) _markerStopAt = DateTimeOffset.UtcNow + MarkerTail;
        RecorderStatusText.Text = "Saving marked incident…";
        try
        {
            await _trace.MarkAsync("Skald: problem marked");
            if (_trace.IsRecording && _traceSave is null or { IsCompleted: true })
                _traceSave = SaveTraceAsync($"marker{++_traceCount}", $"Problem marker {_recorder.MarkerCount}", restart: true);
            await _recorder.CheckpointAsync();
            await _recordingsPage.RefreshSessionsAsync();
            if (!_trace.IsSaving)
                RecorderStatusText.Text = $"Problem marked · {_recorder.SampleCount} samples · saved checkpoint" + (started ? " · saves 2 minutes after the last marker" : string.Empty);
        }
        catch (Exception ex) { RecorderStatusText.Text = $"Problem marked, but checkpoint failed: {ex.Message}"; }
    }

    private void RecordMenu_Opening(object sender, object e)
    {
        var elevated = WindowsTraceCapture.IsElevated;
        var recording = _recorder.IsRecording;
        DeepTraceToggle.IsChecked = _preferences.IncludeDeepTrace;
        DeepTraceToggle.IsEnabled = elevated && _trace.IsAvailable && !recording;
        DeepTraceNote.Text = !_trace.IsAvailable ? "Windows Performance Recorder (wpr.exe) is not installed"
            : !elevated ? "Deep trace needs Skald running as administrator"
            : recording ? (_trace.IsRecording ? "Deep trace is running for this recording" : "Deep trace applies to the next recording")
            : "Saves the last minute of system activity at each marker (ETL)";
        RestartElevatedItem.Visibility = elevated ? Visibility.Collapsed : Visibility.Visible;
        TimedPowerItem.IsEnabled = !recording;
    }

    private void DeepTraceToggle_Click(object sender, RoutedEventArgs e)
    {
        _preferences.IncludeDeepTrace = DeepTraceToggle.IsChecked;
        _preferences.Save();
    }

    private void RestartElevated_Click(object sender, RoutedEventArgs e)
    {
        if (_recorder.IsRecording)
        {
            RecorderStatusText.Text = "Stop the recording before restarting Skald as administrator.";
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" })?.Dispose();
            Close();
        }
        catch (Win32Exception) { RecorderStatusText.Text = "Skald was not restarted as administrator."; }
    }

    private void TimedPower_Click(object sender, RoutedEventArgs e) => StartTimedPowerRecording();

    private void OpenRecordingsFolder_Click(object sender, RoutedEventArgs e) => _recordingsPage.OpenRecordingsFolder();

    private void MainWindow_Closed(object sender, WindowEventArgs args) => Dispose();

    public void Dispose()
    {
        _timer.Stop();
        // The deep-trace buffer is discarded rather than left running in the kernel with nobody to save it.
        _trace.Cancel();
        if (_recorder.IsRecording)
        {
            if (_machineTask is { IsCompletedSuccessfully: true } machine) _recorder.SetMachine(machine.Result);
            _recorder.StopAsync(CollectWindowsEvidence).GetAwaiter().GetResult();
        }
        _recorder.Dispose();
        _trace.Dispose();
        _collector.Dispose();
    }
}


