using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Skald.Core.Models;
namespace Skald.App.Pages;
public sealed partial class PowerPage : Page, ITelemetryPage
{
    public event Action? TimedRecordingRequested;
    public PowerPage() => InitializeComponent();
    private void Record30_Click(object sender, RoutedEventArgs e) => TimedRecordingRequested?.Invoke();
    public void SetRecordingStatus(string status)
    {
        RecordingText.Text = status;
        RecordingText.Visibility = string.IsNullOrWhiteSpace(status) ? Visibility.Collapsed : Visibility.Visible;
    }
    public void ShowHistory(IReadOnlyList<SystemMetricsSnapshot> samples) => Dashboard.ShowHistory(samples);
    public void Update(SystemMetricsSnapshot snapshot, IReadOnlyList<DiagnosticFinding> findings) => Dashboard.Update(snapshot);
}
