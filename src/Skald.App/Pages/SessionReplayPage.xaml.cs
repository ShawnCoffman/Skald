using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Skald.App.Controls;
using Skald.Collectors;
using Skald.Triage;
using Skald.Core.Models;
using Skald.Diagnostics;
using Skald.Recorder;
using Windows.UI;

namespace Skald.App.Pages;

public sealed partial class SessionReplayPage : Page, ITelemetryPage
{
    private readonly ObservableCollection<SessionFile> _sessions = [];
    private readonly ObservableCollection<ProcessMetric> _processes = [];
    private readonly ObservableCollection<EventRow> _events = [];
    private readonly ObservableCollection<IncidentRow> _incidentRows = [];
    private readonly ObservableCollection<MarkerRow> _markers = [];
    private ReliabilityHistory? _localReliability;
    private CrashDumpInventory? _localDumps;
    private IReadOnlyList<ReliabilityEvent> _reports = [];
    private IReadOnlyList<CrashDumpFile> _dumps = [];
    private SessionDocument? _document;
    private PowerSessionSummary? _powerSummary;
    private PowerSessionSummary? _baseline;
    private string? _baselineName;
    private string? _baselinePath;
    private string? _baselineMachine;
    private string? _loadedFilePath;
    private string? _loadedDisplayName;
    private string? _activeRecordingPath;
    private bool _archiveBusy;
    private bool _hasLoadedDirectory;
    private int _selectionVersion;
    private readonly PerformancePage _replayPerformance = new();

    public SessionReplayPage()
    {
        InitializeComponent();
        SessionList.ItemsSource = _sessions;
        ReplayProcessList.ItemsSource = _processes;
        EventList.ItemsSource = _events;
        IncidentList.ItemsSource = _incidentRows;
        MarkerPicker.ItemsSource = _markers;
        ActivityFrame.Content = _replayPerformance;
    }

    public void Update(SystemMetricsSnapshot snapshot, IReadOnlyList<DiagnosticFinding> findings)
    {
    }

    public void SetRecording(bool recording)
    {
        RecordingStateText.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
    }
    public void SetActiveRecordingPath(string? path)
    {
        _activeRecordingPath = path;
        UpdateArchiveActions();
    }
    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshSessionsAsync();
    private void OpenFolder_Click(object sender, RoutedEventArgs e) => OpenRecordingsFolder();

    public void OpenRecordingsFolder()
    {
        try
        {
            Directory.CreateDirectory(SessionLocations.RecordingsDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { SessionLocations.RecordingsDirectory } })?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { SessionStatusText.Text = $"Could not open the recordings folder: {ex.Message}"; }
    }

    private void ShowTraces_Click(object sender, RoutedEventArgs e)
    {
        if (_loadedFilePath is null || _document is not { Traces.Count: > 0 } document) return;
        var first = Path.Combine(Path.GetDirectoryName(_loadedFilePath)!, Path.GetFileName(document.Traces[0].FileName));
        // Explorer expects /select,"path" as one token with only the path quoted; ArgumentList would quote the whole token.
        var arguments = File.Exists(first) ? $"/select,\"{first}\"" : $"\"{Path.GetDirectoryName(_loadedFilePath)!}\"";
        try { Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = arguments })?.Dispose(); }
        catch (System.ComponentModel.Win32Exception ex) { SessionStatusText.Text = $"Could not open the folder: {ex.Message}"; }
    }

    private void ZipOptions_Click(object sender, RoutedEventArgs e)
    {
        // A trace cannot have names removed, so the two choices exclude each other.
        if (ReferenceEquals(sender, RedactCheck) && RedactCheck.IsChecked == true) IncludeTracesCheck.IsChecked = false;
        if (ReferenceEquals(sender, IncludeTracesCheck) && IncludeTracesCheck.IsChecked == true) RedactCheck.IsChecked = false;
    }

    private void BackgroundEvents_Click(object sender, RoutedEventArgs e) => RenderEvents();

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (_hasLoadedDirectory) return;
        _hasLoadedDirectory = true;
        await RefreshSessionsAsync();
    }

    public async Task RefreshSessionsAsync()
    {
        var files = SessionLocations.SearchDirectories.Where(Directory.Exists)
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.perfsession"))
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Select(file => new SessionFile(file.FullName, file.Name, FormatBytes(file.Length)))
            .ToArray();

        using var scroll = ScrollPositionKeeper.Capture(this);
        _sessions.Clear();
        foreach (var file in files) _sessions.Add(file);
        SessionStatusText.Text = (files.Length == 0 ? "No recordings yet. Press Record to create one." : $"{files.Length} recording(s)")
            + (SessionLocations.IsCloudSynced(SessionLocations.DocumentsDirectory) ? " · kept in local app data because Documents is synced by OneDrive" : string.Empty);
        UpdateArchiveActions();
        await Task.CompletedTask;
    }

    private bool IsActive(string path) => _activeRecordingPath is not null &&
        path.Equals(_activeRecordingPath, StringComparison.OrdinalIgnoreCase);

    private void UpdateArchiveActions()
    {
        if (SessionList is null || ZipSelectedButton is null) return;
        var selected = SessionList.SelectedItems.OfType<SessionFile>().ToArray();
        ZipSelectedButton.IsEnabled = !_archiveBusy && selected.Length > 0 && selected.All(file => !IsActive(file.Path));
        DeleteSelectedButton.IsEnabled = !_archiveBusy && selected.Length > 0 && selected.All(file => !IsActive(file.Path));
        ZipAllButton.IsEnabled = !_archiveBusy && _sessions.Any(file => !IsActive(file.Path));
    }

    private async void ZipSelected_Click(object sender, RoutedEventArgs e)
        => await CreateArchiveAsync(SessionList.SelectedItems.OfType<SessionFile>().Where(file => !IsActive(file.Path)).ToArray());

    private async void ZipAll_Click(object sender, RoutedEventArgs e)
        => await CreateArchiveAsync(_sessions.Where(file => !IsActive(file.Path)).ToArray());

    private async Task CreateArchiveAsync(IReadOnlyList<SessionFile> files)
    {
        if (_archiveBusy || files.Count == 0) return;
        _archiveBusy = true;
        UpdateArchiveActions();
        var options = new SessionArchiveOptions { Redact = RedactCheck.IsChecked == true, IncludeTraces = IncludeTracesCheck.IsChecked == true };
        SessionStatusText.Text = $"Creating ZIP from {files.Count} saved session(s){(options.Redact ? " with names removed" : string.Empty)}…";
        try
        {
            var path = Path.Combine(SessionLocations.ExportsDirectory, $"Skald_Sessions_{DateTime.Now:yyyyMMdd_HHmmss_fff}{(options.Redact ? "_redacted" : string.Empty)}.zip");
            await SessionArchive.CreateAsync(files.Select(file => file.Path).ToArray(), path, options);
            SessionStatusText.Text = $"ZIP saved: {path} ({files.Count} session(s){(options.Redact ? ", names removed" : options.IncludeTraces ? ", with deep traces" : string.Empty)}).";
        }
        catch (Exception ex) { SessionStatusText.Text = $"Could not create ZIP: {ex.Message}"; }
        finally { _archiveBusy = false; UpdateArchiveActions(); }
    }

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_archiveBusy) return;
        var files = SessionList.SelectedItems.OfType<SessionFile>().ToArray();
        if (files.Length == 0 || files.Any(file => IsActive(file.Path))) return;
        var dialog = new ContentDialog
        {
            Title = files.Length == 1 ? "Delete this session?" : $"Delete {files.Length} sessions?",
            Content = new TextBlock { Text = "This permanently deletes the selected .perfsession files and their deep traces. Export a ZIP first if you need a copy.", TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "Delete permanently",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        _archiveBusy = true;
        UpdateArchiveActions();
        var removedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        foreach (var file in files)
        {
            try
            {
                foreach (var trace in Directory.EnumerateFiles(Path.GetDirectoryName(file.Path)!, Path.GetFileNameWithoutExtension(file.Path) + ".*.etl"))
                    File.Delete(trace);
                File.Delete(file.Path);
                removedPaths.Add(file.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { errors.Add($"{file.DisplayName}: {ex.Message}"); }
        }
        if (_baselinePath is not null && removedPaths.Contains(_baselinePath))
            ClearBaseline_Click(this, new RoutedEventArgs());
        if (_loadedFilePath is not null && removedPaths.Contains(_loadedFilePath))
            ClearLoadedSession();
        try { await RefreshSessionsAsync(); }
        catch (Exception ex) { errors.Add($"Refresh failed: {ex.Message}"); }
        _archiveBusy = false;
        UpdateArchiveActions();
        SessionStatusText.Text = errors.Count == 0 ? $"Deleted {removedPaths.Count} session(s)." : $"Deleted {removedPaths.Count}; {string.Join("; ", errors)}";
    }

    private void ClearLoadedSession()
    {
        _document = null;
        ReplayScroll.Visibility = Visibility.Collapsed;
        EmptyReplayText.Visibility = Visibility.Visible;
        _powerSummary = null;
        _loadedFilePath = _loadedDisplayName = null;
        SessionNameText.Text = "Select a session";
        MachineText.Text = string.Empty;
        TracePanel.Visibility = Visibility.Collapsed;
        _reports = [];
        _dumps = [];
        ExportButton.IsEnabled = BaselineButton.IsEnabled = false;
        ReplaySlider.Value = ReplaySlider.Maximum = 0;
        ReplayTimeText.Text = "No session loaded";
        ReplayCpuText.Text = ReplayMemoryText.Text = ReplayDiskText.Text = "—";
        IncidentCoverageText.Text = "Choose a recording to review an incident.";
        IncidentFindingsText.Text = IncidentStatusText.Text = string.Empty;
        _markers.Clear(); _incidentRows.Clear(); _events.Clear(); _processes.Clear();
    }

    private async void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateArchiveActions();
        var selectionVersion = ++_selectionVersion;
        if (e.AddedItems.OfType<SessionFile>().LastOrDefault() is not { } file) return;
        try
        {
            var document = await FlightRecorder.LoadAsync(file.Path);
            if (selectionVersion != _selectionVersion) return;
            var powerSummary = await Task.Run(() => PowerSessionSummary.From(document));
            if (selectionVersion != _selectionVersion) return;
            _document = document;
            _powerSummary = powerSummary;
            ReplayScroll.Visibility = Visibility.Visible;
            EmptyReplayText.Visibility = Visibility.Collapsed;
            _loadedFilePath = file.Path;
            _loadedDisplayName = file.DisplayName;
            BaselineButton.IsEnabled = true;
            RenderPowerAnalysis(file.DisplayName);
            RenderPowerTimeline();
            SessionNameText.Text = $"{file.DisplayName} · {_document.Samples.Count} samples" + (_document.Metadata.Recovered ? " · recovered after an interruption" : string.Empty) + (_document.Redacted ? " · names removed" : string.Empty);
            MachineText.Text = DescribeMachine(_document);
            RenderTraces();
            ExportButton.IsEnabled = true;
            ReplaySlider.Maximum = Math.Max(0, _document.Samples.Count - 1);
            ReplaySlider.Value = 0;
            RenderEvents();

            _markers.Clear();
            foreach (var marker in _document.Events.Where(item => item.Type == SessionEventType.UserMarker).OrderByDescending(item => item.Timestamp))
                _markers.Add(new MarkerRow(marker.Timestamp, $"{marker.Timestamp.ToLocalTime():g} · {marker.Note}"));
            if (_markers.Count == 0 && _document.Samples.Count > 0)
                _markers.Add(new MarkerRow(_document.Samples[^1].Timestamp, $"{_document.Samples[^1].Timestamp.ToLocalTime():g} · end of recording (no marker)"));
            MarkerPicker.SelectedIndex = _markers.Count > 0 ? 0 : -1;
            UpdateReplaySample();
            await LoadIncidentSourcesAsync(_document);
        }
        catch (Exception ex)
        {
            if (selectionVersion == _selectionVersion)
                SessionStatusText.Text = $"Could not open session: {ex.Message}";
        }
    }

    private void Baseline_Click(object sender, RoutedEventArgs e)
    {
        if (_powerSummary is null || _document is null || _loadedFilePath is null || _loadedDisplayName is null) return;
        _baseline = _powerSummary;
        _baselineName = _loadedDisplayName;
        _baselinePath = _loadedFilePath;
        _baselineMachine = _document.Metadata.MachineName;
        RenderPowerAnalysis(_loadedDisplayName);
    }

    private void ClearBaseline_Click(object sender, RoutedEventArgs e)
    {
        _baseline = null;
        _baselineName = _baselineMachine = _baselinePath = null;
        if (_loadedDisplayName is not null) RenderPowerAnalysis(_loadedDisplayName);
        else ComparisonText.Text = "Choose a baseline, then select another recording to compare.";
    }

    // Windows reports come from the recording itself. Only a recording from before Skald saved them, made on this same PC, falls
    // back to reading this PC's logs now; a recording from another PC never shows the viewer's reports.
    private async Task LoadIncidentSourcesAsync(SessionDocument selectedDocument)
    {
        if (selectedDocument.WindowsEvidence is { } saved)
        {
            _reports = saved.Events;
            _dumps = saved.Dumps;
            IncidentStatusText.Text = $"Windows reports saved with this recording, read on {selectedDocument.Metadata.MachineName}: {saved.Events.Count} report(s) and {saved.Dumps.Count} dump file(s) " +
                $"from {saved.From.ToLocalTime():g} to {saved.To.ToLocalTime():g}. Events near a marker may be related, but timing alone does not prove cause.";
            RenderIncident();
            return;
        }
        _reports = [];
        _dumps = [];
        if (selectedDocument.Redacted || !selectedDocument.Metadata.MachineName.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            IncidentStatusText.Text = selectedDocument.Redacted
                ? "This recording has no saved Windows reports and its computer name was removed, so this PC's reports cannot be matched to it and none are shown."
                : $"This recording has no saved Windows reports (it predates that feature) and was made on {selectedDocument.Metadata.MachineName}, not this PC, so none are shown. Run Check hardware on that machine for its reports.";
            RenderIncident();
            return;
        }
        IncidentStatusText.Text = "This recording has no saved Windows reports. Reading this PC's retained reports instead…";
        try
        {
            if (_localReliability is null || DateTimeOffset.Now - _localReliability.CollectedAt > TimeSpan.FromMinutes(5))
            {
                var reports = WindowsReliabilityCollector.CollectAsync();
                var dumps = CrashDumpCollector.CollectAsync();
                await Task.WhenAll(reports, dumps);
                _localReliability = await reports;
                _localDumps = await dumps;
            }
            if (!ReferenceEquals(_document, selectedDocument)) return;
            _reports = _localReliability?.Events ?? [];
            _dumps = _localDumps?.Files ?? [];
            IncidentStatusText.Text = $"Read from this PC now, because the recording predates saved reports: {_reports.Count} retained report(s); dumps: {_dumps.Count}. " +
                "Events near a marker may be related, but timing alone does not prove cause.";
            RenderIncident();
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_document, selectedDocument)) IncidentStatusText.Text = $"Windows evidence unavailable: {ex.Message}. Recorded telemetry remains available.";
        }
    }

    private void Marker_Changed(object sender, SelectionChangedEventArgs e) => RenderIncident();

    private void RenderIncident()
    {
        if (_document is null || MarkerPicker.SelectedItem is not MarkerRow marker) return;
        var start = marker.Timestamp.AddMinutes(-5);
        var end = marker.Timestamp.AddMinutes(2);
        var samples = _document.Samples.Where(sample => sample.Timestamp >= start && sample.Timestamp <= end)
            .OrderBy(sample => sample.Timestamp).ToArray();
        _incidentRows.Clear();
        var gaps = samples.Zip(samples.Skip(1)).Count(pair => (pair.Second.Timestamp - pair.First.Timestamp).TotalSeconds > 6);
        IncidentCoverageText.Text = samples.Length == 0
            ? "No telemetry samples were saved around this marker. Windows reports may still be available."
            : $"Marker {marker.Timestamp.ToLocalTime():g} · {samples.Length} samples from {samples[0].Timestamp.ToLocalTime():HH:mm:ss} to {samples[^1].Timestamp.ToLocalTime():HH:mm:ss} · {gaps} gaps over 6 seconds. Review window: 5 min before, 2 min after.";
        var findings = WindowDiagnosticAnalyzer.Analyze(samples, TimeSpan.FromMinutes(7));
        IncidentFindingsText.Text = findings.Count == 0
            ? "No configured sustained condition found in available samples. Brief stalls and missing sources remain possible."
            : string.Join("\n\n", findings.Select(finding => $"{finding.Title}: {finding.Explanation} {string.Join(" ", finding.Evidence)}"));

        foreach (var item in _document.Events.Where(item => item.Type == SessionEventType.UserMarker && item.Timestamp >= start && item.Timestamp <= end))
            Add(item.Timestamp, "User marker", item.Note ?? "Problem marked");
        AddTransitions("CPU above 90%", s => s.Cpu.Availability.IsSupported && s.Cpu.UtilizationPercent >= 90,
            s => $"Total CPU {s.Cpu.UtilizationPercent:F1}%");
        AddTransitions("Low available memory", s => s.Memory.Availability.IsSupported && s.Memory.UsedPercent >= 90 && s.Memory.AvailableBytes <= 1024UL * 1024 * 1024,
            s => $"Available {s.Memory.AvailableBytes / 1048576d:F0} MB; physical occupancy {s.Memory.UsedPercent:F1}%");
        AddTransitions("Disk active above 90%", s => s.Disk.Availability.IsSupported && s.Disk.ActiveTimePercent >= 90,
            s => $"Aggregate disk active {s.Disk.ActiveTimePercent:F1}%");
        foreach (var name in samples.SelectMany(s => s.Disk.PhysicalDisks).Select(d => d.Name).Distinct(StringComparer.OrdinalIgnoreCase))
            AddTransitions($"High disk latency · {name}",
                s => s.Disk.PhysicalDisks.Any(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                    && (d.ReadLatencyMilliseconds is >= 50 && d.ReadsPerSecond is >= 1 || d.WriteLatencyMilliseconds is >= 50 && d.WritesPerSecond is >= 1)),
                s => { var d = s.Disk.PhysicalDisks.First(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    return $"Read {d.ReadLatencyMilliseconds?.ToString("F0", CultureInfo.CurrentCulture) ?? "?"} ms; write {d.WriteLatencyMilliseconds?.ToString("F0", CultureInfo.CurrentCulture) ?? "?"} ms average. Disk identity is a counter name."; });

        if (samples.Length > 0)
        {
            var near = samples.MinBy(sample => Math.Abs((sample.Timestamp - marker.Timestamp).TotalMilliseconds))!;
            var cpu = near.Processes.Where(p => p.CpuAvailable).OrderByDescending(p => p.CpuPercent).Take(3)
                .Select(p => $"{p.Name} ({p.ProcessId}) {p.CpuPercent:F0}%");
            var io = near.Processes.OrderByDescending(p => p.ReadBytesPerSecond.GetValueOrDefault() + p.WriteBytesPerSecond.GetValueOrDefault()).Take(3)
                .Select(p => $"{p.Name} ({p.ProcessId}) {(p.ReadBytesPerSecond.GetValueOrDefault() + p.WriteBytesPerSecond.GetValueOrDefault()) / 1048576d:F1} MB/s");
            Add(near.Timestamp, "Processes near marker", $"Top CPU: {string.Join(", ", cpu)}. Top process I/O: {string.Join(", ", io)}. Process I/O is not disk attribution.");
        }
        foreach (var exit in _document.Events.Where(item => item.Type == SessionEventType.ProcessExit && item.HadWindow == true && item.Timestamp >= start && item.Timestamp <= end))
        {
            var crash = _reports.FirstOrDefault(report => report.Category == "Applications" && exit.ProcessName is { } name
                && report.Component.Contains(Path.GetFileNameWithoutExtension(name), StringComparison.OrdinalIgnoreCase)
                && Math.Abs((report.Timestamp - exit.Timestamp).TotalSeconds) <= 30);
            Add(exit.Timestamp, "Program exited", (exit.Note ?? exit.ProcessName ?? "Process exited")
                + (crash is null ? ". No Windows crash or hang report within 30 s; it may have been closed normally." : $". Windows reported: {crash.Provider} / Event {crash.EventId}: {crash.Summary}"));
        }
        var reportsUntil = marker.Timestamp.AddMinutes(10);
        foreach (var report in _reports.Where(item => item.Timestamp >= start && item.Timestamp <= reportsUntil))
            Add(report.Timestamp, $"Windows · {report.Category}", $"{report.Provider} / Event {report.EventId} / {report.Component}: {report.Summary}");
        // A recovered recording stopped because the app, Windows or power died; the next boot's crash and restart reports explain how.
        if (_document.Metadata.Recovered)
            foreach (var report in _reports.Where(item => item.Timestamp > reportsUntil && item.Category == "Crashes & restarts"))
                Add(report.Timestamp, "After the recording stopped · Windows", $"{report.Provider} / Event {report.EventId}: {report.Summary}");
        foreach (var dump in _dumps.Where(item => item.ModifiedAt >= start && item.ModifiedAt <= reportsUntil))
            Add(dump.ModifiedAt, "Dump candidate", $"{dump.Kind}: {dump.Path}. File time is a correlation, not proof of this incident.");

        var ordered = _incidentRows.OrderBy(row => row.Timestamp).ToArray();
        _incidentRows.Clear();
        foreach (var row in ordered) _incidentRows.Add(row);

        void AddTransitions(string title, Func<SystemMetricsSnapshot, bool> condition, Func<SystemMetricsSnapshot, string> detail)
        {
            bool active = false;
            DateTimeOffset? previous = null;
            foreach (var sample in samples)
            {
                if (previous is { } prior && (sample.Timestamp - prior).TotalSeconds > 6) active = false;
                var observed = condition(sample);
                if (observed && !active) Add(sample.Timestamp, title, detail(sample) + ". Sampled observation; inspect duration and nearby evidence.");
                active = observed;
                previous = sample.Timestamp;
            }
        }
        void Add(DateTimeOffset time, string source, string detail)
        {
            var delta = time - marker.Timestamp;
            var relative = (delta < TimeSpan.Zero ? "−" : "+") + delta.Duration().ToString(@"mm\:ss", CultureInfo.InvariantCulture);
            _incidentRows.Add(new IncidentRow(time, $"{time.ToLocalTime():HH:mm:ss} ({relative}) · {source}", detail));
        }
    }

    private void IncidentList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_document is null || _document.Samples.Count == 0 || IncidentList.SelectedItem is not IncidentRow row) return;
        ReplaySlider.Value = Enumerable.Range(0, _document.Samples.Count).MinBy(index => Math.Abs((_document.Samples[index].Timestamp - row.Timestamp).TotalMilliseconds));
    }

    private void RenderPowerAnalysis(string selectedName)
    {
        if (_powerSummary is null || _document is null) return;
        var summary = _powerSummary;
        RunPowerText.Text = summary.AverageDischargeWatts is { } average
            ? $"Average battery draw: {average:F1} W · Peak sampled draw: {Watts(summary.PeakDischargeWatts)} · Estimated battery energy used: {summary.EstimatedBatteryEnergyWh:F2} Wh\n" +
              $"Coverage: {summary.BatteryDuration.TotalMinutes:F1} of {summary.Duration.TotalMinutes:F1} minutes ({summary.RunCoveragePercent:F0}% of run). Source: battery discharge; average and energy are integrated across adjacent valid samples."
            : "Run average system draw unavailable. No continuous battery-discharge readings were captured; AC and desktop power require a verified whole-system source.";
        if (summary.Domains.Count > 0)
            RunPowerText.Text += "\n" + string.Join("\n", summary.Domains.Select(domain =>
                $"{domain.Label}: average {domain.AverageWatts:F1} W · peak {domain.PeakWatts:F1} W · {domain.EnergyWh:F2} Wh over {domain.CoveragePercent:F0}% of the run")) +
                "\nCPU package and GPU board are separate measured domains; they are not added together or treated as whole-system power.";
        PowerAnalysisText.Text = $"Duration {summary.Duration.TotalMinutes:F1} min · battery draw span {summary.BatteryDuration.TotalMinutes:F1} min · {summary.BatterySamples} / {_document.Samples.Count} samples with measured battery draw\n" +
            $"Estimated runtime at average draw: {(summary.RemainingRuntimeHours is { } remaining ? $"{remaining:F1} h remaining" : "Unavailable")} / {(summary.FullChargeRuntimeHours is { } full ? $"{full:F1} h from full" : "Unavailable")}\n" +
            $"Battery health: {(summary.BatteryHealthPercent is { } health ? $"{health:F1}%" : "Unavailable")} · Average brightness: {(summary.AverageBrightnessPercent is { } brightness ? $"{brightness:F1}%" : "Unavailable")}\n" +
            $"Top sampled activity: {(summary.Processes.Count > 0 ? string.Join(", ", summary.Processes.Take(3).Select(process => process.Name)) : "Unavailable")}. Process watts are not measured.";
        ComparisonText.Text = _baseline is null ? "Choose a baseline, then select another recording to compare."
            : _baselineName == selectedName ? $"Baseline set: {_baselineName}. Select another recording to compare."
            : !string.Equals(_baselineMachine, _document.Metadata.MachineName, StringComparison.OrdinalIgnoreCase)
                ? "These recordings are from different machines and cannot be compared as one battery baseline."
                : $"Baseline: {_baselineName}\nCurrent: {selectedName}\n{PowerSessionComparison.Describe(_baseline, summary)}";
    }

    private static string Watts(double? value) => value is { } watts ? $"{watts:F1} W" : "Unavailable";

    private void RenderPowerTimeline()
    {
        if (_document is null || _document.Samples.Count == 0) return;
        var samples = _document.Samples.OrderBy(sample => sample.Timestamp).ToArray();
        var end = samples[^1].Timestamp;
        var window = TimeSpan.FromSeconds(Math.Max(1, (end - samples[0].Timestamp).TotalSeconds));
        var discharge = samples.Select(sample => (sample.Timestamp, sample.Power.Source == PowerSource.Battery ? sample.Power.BatteryDischargeWatts : null)).ToArray();
        var drawMax = Math.Max(10, discharge.Select(item => item.Item2.GetValueOrDefault()).Max() * 1.1);
        DischargeRecordingChart.SetTimedSeries(discharge, end, drawMax, Color.FromArgb(255, 237, 180, 101), window);
        CpuRecordingChart.SetTimedSeries(samples.Select(sample => (sample.Timestamp, sample.Cpu.Availability.IsSupported ? (double?)sample.Cpu.UtilizationPercent : null)), end, 100, Color.FromArgb(255, 95, 222, 185), window);
        GpuRecordingChart.SetTimedSeries(samples.Select(sample => (sample.Timestamp, sample.GpuEnginesAvailable ? (double?)sample.GpuEngines.Select(engine => engine.UtilizationPercent).DefaultIfEmpty().Max() : null)), end, 100, Color.FromArgb(255, 102, 202, 232), window);
        DiskRecordingChart.SetTimedSeries(samples.Select(sample => (sample.Timestamp, sample.Disk.Availability.IsSupported ? (double?)sample.Disk.ActiveTimePercent : null)), end, 100, Color.FromArgb(255, 218, 162, 101), window);
        var network = samples.Select(sample => (sample.Timestamp, sample.Network.Availability.IsSupported ? (double?)(sample.Network.ReceiveBytesPerSecond + sample.Network.SendBytesPerSecond) : null)).ToArray();
        NetworkRecordingChart.SetTimedSeries(network, end, Math.Max(1024, network.Select(item => item.Item2.GetValueOrDefault()).Max() * 1.1), Color.FromArgb(255, 130, 194, 236), window);
        BrightnessRecordingChart.SetTimedSeries(samples.Select(sample => (sample.Timestamp, sample.Power.DisplayBrightnessPercent is { } brightness ? (double?)brightness : null)), end, 100, Color.FromArgb(255, 224, 208, 140), window);
        var temperature = samples.Select(sample => (sample.Timestamp, SensorCatalog.GetSensors(sample).FirstOrDefault(sensor => sensor.Scope == "CPU sensor" && sensor.Kind == "Temperature")?.Value)).ToArray();
        TemperatureRecordingChart.SetTimedSeries(temperature, end, Math.Max(50, temperature.Select(item => item.Item2.GetValueOrDefault()).Max() * 1.1), Color.FromArgb(255, 237, 132, 113), window);
        var sensors = samples.Select(sample => (sample.Timestamp, Sensors: SensorCatalog.GetSensors(sample))).ToArray();
        var cpuPower = sensors.Select(item => (item.Timestamp, item.Sensors.Where(sensor => sensor.Scope == "CPU package" && sensor.Kind == "Power").Select(sensor => sensor.Value).FirstOrDefault(value => value.HasValue))).ToArray();
        CpuPowerRecordingChart.SetTimedSeries(cpuPower, end, Math.Max(10, cpuPower.Select(item => item.Item2.GetValueOrDefault()).Max() * 1.1), Color.FromArgb(255, 237, 180, 101), window);
        var gpuPower = sensors.Select(item => (item.Timestamp, item.Sensors.Where(sensor => sensor.Scope == "GPU board" && sensor.Kind == "Power").Select(sensor => sensor.Value).FirstOrDefault(value => value.HasValue))).ToArray();
        GpuPowerRecordingChart.SetTimedSeries(gpuPower, end, Math.Max(10, gpuPower.Select(item => item.Item2.GetValueOrDefault()).Max() * 1.1), Color.FromArgb(255, 102, 202, 232), window);
        RecordingTimelineScale.Text = $"{samples[0].Timestamp.ToLocalTime():g} – {end.ToLocalTime():g} · Each chart has its own units; missing readings leave gaps.";
    }

    private void RenderEvents()
    {
        _events.Clear();
        if (_document is null) return;
        var background = BackgroundEventsCheck.IsChecked == true;
        foreach (var sessionEvent in _document.Events.OrderByDescending(item => item.Timestamp))
        {
            // Starts and exits of background processes are frequent; programs with a window are always shown.
            if (!background && sessionEvent.Type is SessionEventType.ProcessLaunch or SessionEventType.ProcessExit && sessionEvent.HadWindow != true) continue;
            _events.Add(new EventRow(sessionEvent.Timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture), Label(sessionEvent.Type), sessionEvent.Note ?? string.Empty, sessionEvent.Timestamp));
        }

        static string Label(SessionEventType type) => type switch
        {
            SessionEventType.UserMarker => "Problem marker",
            SessionEventType.CpuSaturation => "CPU",
            SessionEventType.MemoryPressure => "Memory",
            SessionEventType.DiskActivity => "Disk",
            SessionEventType.ProcessLaunch => "Process started",
            SessionEventType.ProcessExit => "Process exited",
            SessionEventType.PowerSourceChanged => "Power source",
            SessionEventType.NetworkChanged => "Network",
            SessionEventType.DeepTraceSaved => "Deep trace saved",
            _ => type.ToString()
        };
    }

    private void RenderTraces()
    {
        if (_document is not { Traces.Count: > 0 } document || _loadedFilePath is null)
        {
            TracePanel.Visibility = Visibility.Collapsed;
            return;
        }
        var folder = Path.GetDirectoryName(_loadedFilePath)!;
        TraceText.Text = string.Join("\n", document.Traces.Select(trace =>
            $"{trace.SavedAt.ToLocalTime():HH:mm:ss} · {trace.Reason} · {trace.FileName} · {trace.SizeBytes / 1048576d:F0} MB"
            + (File.Exists(Path.Combine(folder, Path.GetFileName(trace.FileName))) ? string.Empty : " · file not found beside the recording")));
        TracePanel.Visibility = Visibility.Visible;
    }

    private static string DescribeMachine(SessionDocument document)
    {
        if (document.Machine is not { } machine)
            return $"Recorded on {document.Metadata.MachineName} · {document.Metadata.OperatingSystem}. This recording predates saved machine identity and drivers.";
        string? Detail(string label) => machine.Details.FirstOrDefault(item => item.Label == label)?.Value;
        var parts = new[] { Detail("Make / model"), Detail("Processor"), Detail("BIOS version"), Detail("Windows release") }.Where(part => !string.IsNullOrWhiteSpace(part));
        return $"Recorded on {document.Metadata.MachineName} · {string.Join(" · ", parts)} · {machine.Drivers.Count} drivers saved";
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null || _loadedFilePath is null) return;
        var htmlPath = Path.ChangeExtension(_loadedFilePath, ".html");
        try
        {
            await SessionReportWriter.WriteHtmlAsync(_document, htmlPath);
            SessionStatusText.Text = $"Exported {htmlPath}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { SessionStatusText.Text = $"Could not export the report: {ex.Message}"; }
    }

    private void ReplaySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_document is not null) UpdateReplaySample();
    }

    private void UpdateReplaySample()
    {
        if (_document is null || _document.Samples.Count == 0) return;
        var index = Math.Clamp((int)Math.Round(ReplaySlider.Value), 0, _document.Samples.Count - 1);
        var snapshot = _document.Samples[index];
        ReplayTimeText.Text = snapshot.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        ReplayPositionText.Text = $"{index + 1} / {_document.Samples.Count}";
        ReplayCpuText.Text = snapshot.Cpu.Availability.IsSupported ? $"{snapshot.Cpu.UtilizationPercent:F1}%" : "Unavailable";
        ReplayMemoryText.Text = snapshot.Memory.Availability.IsSupported ? $"{snapshot.Memory.UsedPercent:F1}%" : "Unavailable";
        ReplayDiskText.Text = snapshot.Disk.Availability.IsSupported ? $"{snapshot.Disk.ActiveTimePercent:F1}%" : "Unavailable";
        _processes.Clear();
        foreach (var process in snapshot.Processes) _processes.Add(process);
        var window = _document.Samples.Take(index + 1).Where(sample => sample.Timestamp >= snapshot.Timestamp.AddMinutes(-3)).ToArray();
        PowerReplay.ShowHistory(window);
        _replayPerformance.ShowHistory(window);
    }

    private void EventList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_document is null || _document.Samples.Count == 0 || EventList.SelectedItem is not EventRow row) return;
        ReplaySlider.Value = Enumerable.Range(0, _document.Samples.Count).MinBy(index => Math.Abs((_document.Samples[index].Timestamp - row.Timestamp).TotalMilliseconds));
    }

    private static string FormatBytes(long bytes)
    {
        var value = (double)bytes;
        var suffix = "B";
        if (value >= 1024) { value /= 1024; suffix = "KB"; }
        if (value >= 1024) { value /= 1024; suffix = "MB"; }
        if (value >= 1024) { value /= 1024; suffix = "GB"; }
        return $"{value:F1} {suffix}";
    }

    private sealed record SessionFile(string Path, string DisplayName, string FileSize);

    private sealed record EventRow(string Time, string Type, string Detail, DateTimeOffset Timestamp);
    private sealed record MarkerRow(DateTimeOffset Timestamp, string Label)
    {
        public override string ToString() => Label;
    }
    private sealed record IncidentRow(DateTimeOffset Timestamp, string Heading, string Detail);
}
