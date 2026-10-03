using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Skald.App.Controls;
using Skald.Collectors;
using Skald.Triage;
using Skald.Core.Models;
using Windows.ApplicationModel.DataTransfer;

namespace Skald.App.Pages;

public sealed partial class ReliabilityPage : Page, ITelemetryPage
{
    private ReliabilityHistory? _history;
    private bool _loading;
    private DateTime? _day;
    private bool _includeOtherProfiles;
    private CrashDumpInventory? _dumps;
    private HardwareInventory? _hardware;
    private string? _selectedDumpPath;

    public ReliabilityPage() => InitializeComponent();
    public void Update(SystemMetricsSnapshot snapshot, IReadOnlyList<DiagnosticFinding> findings) { }
    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (_history is null || DateTimeOffset.Now - _history.CollectedAt > TimeSpan.FromMinutes(5)) await RefreshAsync();
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void OtherProfiles_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _includeOtherProfiles = true;
        if (sender is Button button) button.IsEnabled = false;
        await RefreshAsync();
    }
    private async Task RefreshAsync()
    {
        if (_loading) return;
        _loading = true;
        RefreshButton.IsEnabled = false;
        StatusText.Text = "Reading Windows history in the background…";
        try
        {
            var hardwareTask = HardwareInventoryCollector.CollectAsync();
            _history = await WindowsReliabilityCollector.CollectAsync();
            _dumps = await CrashDumpCollector.CollectAsync(_includeOtherProfiles);
            using (var scroll = ScrollPositionKeeper.Capture(this))
            {
                DumpExpander.Header = $"Crash dumps · {_dumps.Files.Count} available";
                DumpDetails.Text = _dumps.Files.Count == 0 ? "No dump files found in the scanned locations." : string.Join("\n\n", _dumps.Files.Select(file => $"{file.Kind} · {file.ModifiedAt:g} · {file.SizeBytes / 1048576d:F1} MB\n{file.Path}"));
                if (_dumps.SourceStatus.Count > 0) DumpDetails.Text += "\n\n" + string.Join("\n", _dumps.SourceStatus);
                SourcesText.Text = string.Join("\n", _history.SourceStatus);
                Render();
            }
            try
            {
                _hardware = await hardwareTask;
                if (ReportsList.SelectedItem is ReportRow) ShowSelectedReport();
            }
            catch (Exception ex) { SourcesText.Text += $"\nHardware version context: unavailable ({ex.GetType().Name})"; }
        }
        catch (Exception ex) { StatusText.Text = $"History refresh failed: {ex.Message}. Previous results, if any, remain visible."; }
        finally { _loading = false; RefreshButton.IsEnabled = true; }
    }
    private void Filter_Changed(object sender, SelectionChangedEventArgs e) { _day = null; Render(); }
    private void Search_Changed(object sender, TextChangedEventArgs e) => Render();
    private void Group_Toggled(object sender, RoutedEventArgs e) => Render();
    private void ClearDay_Click(object sender, RoutedEventArgs e) { _day = null; Render(); }

    private void Render()
    {
        if (_history is null || ReportsList is null) return;
        using var scroll = ScrollPositionKeeper.Capture(this);
        var days = RangePicker.SelectedIndex switch { 0 => 1, 2 => 30, _ => 7 };
        var end = _history.CollectedAt;
        var start = end.AddDays(-days);
        var category = (CategoryPicker.SelectedItem as ComboBoxItem)?.Content?.ToString();
        var search = SearchBox.Text.Trim();
        var events = _history.Events.Where(item => item.Timestamp >= start && item.Timestamp <= end)
            .Where(item => category == "All categories" || (category == "Problems" ? item.Category != "Changes" || item.Severity is "Error" or "Critical" : item.Category == category))
            .Where(item => search.Length == 0 || $"{item.Component} {item.Provider} {item.EventId} {item.Details}".Contains(search, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        Timeline.Children.Clear();
        var maximum = Math.Max(1, events.GroupBy(item => item.Timestamp.LocalDateTime.Date).Select(group => group.Count()).DefaultIfEmpty(0).Max());
        for (var day = start.LocalDateTime.Date; day <= end.LocalDateTime.Date; day = day.AddDays(1))
        {
            var date = day;
            var count = events.Count(item => item.Timestamp.LocalDateTime.Date == date);
            var content = new StackPanel { Spacing = 5, MinWidth = 48 };
            content.Children.Add(new TextBlock { Text = date.ToString("MMM d", System.Globalization.CultureInfo.CurrentCulture), FontSize = 11 });
            var bar = new Border { Height = count == 0 ? 2 : 36d * count / maximum, VerticalAlignment = VerticalAlignment.Bottom, Background = new SolidColorBrush(count > 0 ? Colors.CadetBlue : Colors.SlateGray), CornerRadius = new CornerRadius(2) };
            var track = new Grid { Height = 36 };
            track.Children.Add(bar);
            content.Children.Add(track);
            content.Children.Add(new TextBlock { Text = count.ToString(System.Globalization.CultureInfo.CurrentCulture), FontSize = 14 });
            var button = new Button { Content = content, BorderThickness = new Thickness(_day == date ? 2 : 0), BorderBrush = new SolidColorBrush(Colors.Aquamarine) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"{date:D}: {count} reports");
            button.Click += (_, _) => { _day = date; Render(); };
            Timeline.Children.Add(button);
        }
        var filtered = events.Where(item => _day is null || item.Timestamp.LocalDateTime.Date == _day).ToArray();
        var rows = GroupToggle.IsOn
            ? filtered.GroupBy(item => item.Signature, StringComparer.OrdinalIgnoreCase).Select(group => new ReportRow(group.OrderByDescending(item => item.Timestamp).ToArray())).OrderByDescending(row => row.Events.Count).ThenByDescending(row => row.Events[0].Timestamp).ToArray()
            : filtered.Select(item => new ReportRow([item])).ToArray();
        ReportsList.ItemsSource = rows;
        ResultCount.Text = $"{filtered.Length} reports · {rows.Length} {(GroupToggle.IsOn ? "groups" : "rows")}{(_day is { } selected ? $" · {selected:MMM d}" : string.Empty)}";
        EmptyText.Text = filtered.Length == 0 ? "No matching reports in the available history. Check source coverage below." : "Select a row for source details. Related reports may describe one incident.";
        StatusText.Text = $"Checked {end:g} · Last {days} {(days == 1 ? "day" : "days")} · {_history.SourceStatus.Count(source => source.Contains("unavailable", StringComparison.OrdinalIgnoreCase) || source.Contains("limit reached", StringComparison.OrdinalIgnoreCase))} limited sources";
        if (rows.Length > 0) ReportsList.SelectedIndex = 0;
        else { DetailHeading.Text = "No report selected."; InterpretationText.Text = TriageText.Text = DetailText.Text = RawText.Text = OccurrenceText.Text = string.Empty; _selectedDumpPath = null; CopyDumpCommandButton.IsEnabled = false; }
    }

    private void Report_Selected(object sender, SelectionChangedEventArgs e) => ShowSelectedReport();

    private void ShowSelectedReport()
    {
        if (ReportsList.SelectedItem is not ReportRow row) return;
        var item = row.Events[0];
        DetailHeading.Text = $"{item.Category} · {item.Severity}\n{item.Component}";
        InterpretationText.Text = ReliabilityAnalysis.Interpretation(item);
        TriageText.Text = item.Category is "Hardware" or "Crashes & restarts"
            ? CrashTriage.Describe(item, _dumps?.Files ?? []) : string.Empty;
        if (item.Category is "Hardware" or "Crashes & restarts")
        {
            var related = _history?.Events.Where(report => report.Signature.Equals(item.Signature, StringComparison.OrdinalIgnoreCase)).ToArray() ?? [];
            if (related.Length > 0) TriageText.Text += $"\n\nSame report signature in retained history: {related.Length}; first {related[^1].DisplayTime}, latest {related[0].DisplayTime}. Reports can repeat for one incident.";
            var bios = _hardware?.Devices.FirstOrDefault(device => device.Category == "Firmware")?.Facts
                .FirstOrDefault(fact => fact.Label == "BIOS version");
            if (bios is not null) TriageText.Text += $"\nPlatform context: BIOS {bios.Value}. This version is not an attribution.";
            var deviceId = CrashTriage.StructuredData(item.Raw).GetValueOrDefault("DeviceInstanceId") ?? item.Component;
            var matching = _hardware?.Devices.FirstOrDefault(device => HardwareIdentity.SamePnpDevice(device.Identity, deviceId));
            if (matching is not null)
            {
                var versions = matching.Facts.Where(fact => fact.Label is "Driver" or "Driver version" or "Firmware").ToArray();
                TriageText.Text += $"\nVerified device identity: {matching.Name}" +
                    (versions.Length > 0 ? $" · {string.Join(" · ", versions.Select(fact => $"{fact.Label}: {fact.Value}"))}" : " · no version was exposed");
            }
            else if (item.Category == "Hardware") TriageText.Text += "\nNo exact PnP identity match; a driver or replaceable device cannot be assigned from this report.";
        }
        _selectedDumpPath = item.Category is "Hardware" or "Crashes & restarts"
            ? CrashTriage.CandidateDump(item, _dumps?.Files ?? [])?.Path : null;
        CopyDumpCommandButton.IsEnabled = _selectedDumpPath is not null;
        CopyDumpCommandButton.Content = "Copy WinDbg command";
        if (_selectedDumpPath is not null) TriageText.Text += "\n\nOpen the candidate in WinDbg, then run !analyze -v. The debugger and symbols must be available separately.";
        DetailText.Text = $"{item.DisplayTime}\n{item.Log} / {item.Provider} / Event {item.EventId}\nRecord {item.RecordId}\n\n{item.Details}";
        RawText.Text = item.Raw;
        OccurrenceText.Text = string.Join("\n", row.Events.Select(report => $"{report.DisplayTime} · {report.Severity} · Record {report.RecordId}"));
    }
    private void CopyDumpCommand_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDumpPath is null) return;
        var package = new DataPackage();
        package.SetText($"windbg -z \"{_selectedDumpPath}\"");
        Clipboard.SetContent(package);
        CopyDumpCommandButton.Content = "WinDbg command copied";
    }
    private void Results_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < 850;
        ResultsGrid.ColumnDefinitions[1].Width = new GridLength(narrow ? 0 : 1, GridUnitType.Star);
        Grid.SetColumn(DetailPanel, narrow ? 0 : 1); Grid.SetRow(DetailPanel, narrow ? 1 : 0);
    }
    private sealed record ReportRow(IReadOnlyList<ReliabilityEvent> Events)
    {
        public string Title => Events.Count > 1 ? $"{Events[0].Component} · Event {Events[0].EventId}" : Events[0].Summary;
        public string Subtitle => $"{Events[0].Category} · {Events[0].Provider}\n{(Events.Count > 1 ? "Latest: " : string.Empty)}{Events[0].Summary}";
        public string Dates => Events.Count == 1 ? Events[0].DisplayTime : $"{Events.Count} reports · First {Events[^1].DisplayTime} · Last {Events[0].DisplayTime}";
    }
}
