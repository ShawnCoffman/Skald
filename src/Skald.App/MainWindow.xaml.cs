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
    private readonly FlightRecorder _recorder = new();
    private readonly WindowsTraceCapture _trace = new();
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
        _recordingsPage.DeepTraceRequested += (_, _) => DeepTrace_Click();
        if (_trace.IsRecording) _recordingsPage.SetDeepTraceStatus(true, "A Skald WPR trace may still be running from a previous app session. Stop and save it here.");

        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(2);
        _timer.Tick += Timer_Tick;
        _timer.Start();
        Closed += MainWindow_Closed;
        _ = RecoverInterruptedRecordingsAsync();
    }

    private static string SessionsDirectory()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(string.IsNullOrWhiteSpace(documents) ? AppContext.BaseDirectory : documents, "Skald Sessions");
    }

    // A recording that was running when the app, Windows or the power died leaves a journal behind; turn it back into a session.
    private async Task RecoverInterruptedRecordingsAsync()
    {
        try
        {
            var recovered = 0;
            var failed = 0;
            foreach (var journal in FlightRecorder.FindInterruptedJournals(SessionsDirectory()))
            {
                // One damaged journal must not stop the others from being recovered.
                try { if (await FlightRecorder.RecoverAsync(journal) is not null) recovered++; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException) { failed++; }
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
            if (_powerRecordingStopAt is { } stopAt && DateTimeOffset.UtcNow >= stopAt)
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
        if (_recorder.IsRecording)
        {
            RecorderStatusText.Text = $"Recording · {_recorder.SampleCount} samples";
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
            "recorder" => new RecorderPage(),
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

    private async void RecordButton_Click(object sender, RoutedEventArgs e)
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
        var directory = SessionsDirectory();
        var fileName = $"Skald_{DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture)}.perfsession";
        var path = Path.Combine(directory, fileName);
        var history = _rollingBuffer.GetSnapshot();
        _recorder.Start(path, 2, history);
        _recordingsPage.SetActiveRecordingPath(path);
        RecordButton.Content = "■ Stop";
        _recordingsPage.SetRecording(true);
        RecorderStatusText.Text = $"Recording · {history.Count} buffered samples";
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
        RecordButton_Click(RecordButton, new RoutedEventArgs());
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
            string? savedPath;
            try { savedPath = await _recorder.StopAsync(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RecorderStatusText.Text = $"Could not save recording: {ex.Message}. Press Stop to retry.";
                return false;
            }
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
        if (!_recorder.IsRecording) StartRecording();
        _recorder.AddMarker();
        RecorderStatusText.Text = "Saving marked incident…";
        try
        {
            await _trace.MarkAsync();
            await _recorder.CheckpointAsync();
            await _recordingsPage.RefreshSessionsAsync();
            RecorderStatusText.Text = $"Problem marked · {_recorder.SampleCount} samples · saved checkpoint";
        }
        catch (Exception ex) { RecorderStatusText.Text = $"Problem marked, but checkpoint failed: {ex.Message}"; }
    }

    private async void DeepTrace_Click()
    {
        _recordingsPage.SetDeepTraceBusy();
        try
        {
            var message = _trace.IsRecording ? await _trace.StopAsync() : await _trace.StartAsync();
            _recordingsPage.SetDeepTraceStatus(_trace.IsRecording, message);
        }
        catch (Exception ex)
        {
            _recordingsPage.SetDeepTraceStatus(_trace.IsRecording, $"Deep trace failed: {ex.Message}");
        }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args) => Dispose();

    public void Dispose()
    {
        _timer.Stop();
        if (_recorder.IsRecording) _recorder.StopAsync().GetAwaiter().GetResult();
        _recorder.Dispose();
        _collector.Dispose();
    }
}


