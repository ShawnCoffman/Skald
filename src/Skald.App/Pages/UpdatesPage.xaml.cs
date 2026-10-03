using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Skald.App.Controls;
using Skald.Triage;
using Skald.Core.Models;

namespace Skald.App.Pages;

public sealed partial class UpdatesPage : Page, ITelemetryPage
{
    // Matches the RangePicker items; null is all history. The default (14 days) is the hardware check's update window.
    private static readonly int?[] RangeDays = [1, 3, 7, 14, 30, null];
    private WindowsUpdateHistory? _history;
    private bool _loading;

    public UpdatesPage() => InitializeComponent();
    public void Update(SystemMetricsSnapshot snapshot, IReadOnlyList<DiagnosticFinding> findings) { }
    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (_history is null || DateTimeOffset.Now - _history.CollectedAt > TimeSpan.FromMinutes(5)) await RefreshAsync();
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void Filter_Changed(object sender, RoutedEventArgs e) => Render();

    // A shorter range is filtered from what is loaded; a longer one is read again.
    private async void Range_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_history is null) return;
        var since = Since();
        var loaded = _history.RequestedSince;
        if (loaded is null || since is not null && since >= loaded) Render();
        else await RefreshAsync();
    }

    private DateTimeOffset? Since()
    {
        var index = RangePicker is null ? 3 : Math.Clamp(RangePicker.SelectedIndex, 0, RangeDays.Length - 1);
        return RangeDays[index] is { } days ? DateTimeOffset.Now.AddDays(-days) : null;
    }

    private async Task RefreshAsync()
    {
        if (_loading) return;
        _loading = true;
        RefreshButton.IsEnabled = false;
        RangePicker.IsEnabled = false;
        StatusText.Text = "Reading Windows Update history and local update catalog…";
        try { _history = await WindowsUpdateCollector.CollectAsync(Since()); Render(); }
        catch (Exception ex) { StatusText.Text = $"Update history failed: {ex.Message}"; }
        finally { _loading = false; RefreshButton.IsEnabled = true; RangePicker.IsEnabled = true; }
    }

    private void Render()
    {
        if (_history is null || UpdatesList is null) return;
        using var scroll = ScrollPositionKeeper.Capture(this);
        var since = Since();
        var rows = WindowsUpdateHistoryView.Rows(_history, since, FailuresOnly.IsChecked == true);
        UpdatesList.ItemsSource = rows.Select(row => new UpdateRow(row.Title, row.Detail)).ToArray();
        var range = RangePicker.SelectedItem as string ?? "Last 14 days";
        var entries = rows.Sum(row => row.Count);
        var failures = rows.Where(row => row.IsFailure).Sum(row => row.Count);
        StatusText.Text = $"Checked {_history.CollectedAt:g} · {range}: {entries} entr{(entries == 1 ? "y" : "ies")} in {rows.Count} row{(rows.Count == 1 ? string.Empty : "s")}"
            + (FailuresOnly.IsChecked == true ? " (failures only)" : $" · {failures} failure{(failures == 1 ? string.Empty : "s")}");
        var coverage = WindowsUpdateHistoryView.Coverage(_history, since);
        CoverageText.Text = coverage ?? string.Empty;
        CoverageText.Visibility = coverage is null ? Visibility.Collapsed : Visibility.Visible;
        StateText.Text = $"Pending in local catalog: {(_history.PendingCount?.ToString(CultureInfo.CurrentCulture) ?? "Unknown")}\nLast successful update check: {(_history.LastSuccessfulSearch?.ToString("g", CultureInfo.CurrentCulture) ?? "Unknown")}\nLatest history entry: {(_history.Entries.Count > 0 ? _history.Entries[0].Date.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : "None available")}";
        SourcesText.Text = string.Join("\n", _history.SourceStatus);
    }

    private sealed record UpdateRow(string Title, string Detail);
}
