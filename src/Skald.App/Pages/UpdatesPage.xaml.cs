using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Skald.App.Controls;
using Skald.Collectors;
using Skald.Triage;
using Skald.Core.Models;

namespace Skald.App.Pages;

public sealed partial class UpdatesPage : Page, ITelemetryPage
{
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
    private async Task RefreshAsync()
    {
        if (_loading) return;
        _loading = true;
        RefreshButton.IsEnabled = false;
        StatusText.Text = "Reading Windows Update history and local update catalog…";
        try { _history = await WindowsUpdateCollector.CollectAsync(); Render(); }
        catch (Exception ex) { StatusText.Text = $"Update history failed: {ex.Message}"; }
        finally { _loading = false; RefreshButton.IsEnabled = true; }
    }
    private void Render()
    {
        if (_history is null || UpdatesList is null) return;
        using var scroll = ScrollPositionKeeper.Capture(this);
        var entries = _history.Entries.Where(item => FailuresOnly.IsChecked != true || item.Result is "Failed" or "Aborted" or "Succeeded with errors")
            .Select(item => new UpdateRow(item.Title, $"{item.Date:g} · {item.Operation} · {item.Result} · {item.HResult}")).ToArray();
        UpdatesList.ItemsSource = entries;
        StatusText.Text = $"Checked {_history.CollectedAt:g} · {entries.Length} of {_history.Entries.Count} recent entries";
        StateText.Text = $"Pending in local catalog: {(_history.PendingCount?.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? "Unknown")}\nLast successful update check: {(_history.LastSuccessfulSearch?.ToString("g", System.Globalization.CultureInfo.CurrentCulture) ?? "Unknown")}\nLatest history entry: {(_history.Entries.Count > 0 ? _history.Entries[0].Date.ToString("g", System.Globalization.CultureInfo.CurrentCulture) : "None available")}";
        SourcesText.Text = string.Join("\n", _history.SourceStatus);
    }
    private sealed record UpdateRow(string Title, string Detail);
}
