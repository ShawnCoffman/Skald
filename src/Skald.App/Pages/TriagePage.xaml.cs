using System.Diagnostics;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Skald.Core.Models;
using Skald.Triage;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace Skald.App.Pages;

public sealed partial class TriagePage : Page, ITelemetryPage
{
    private bool _running;
    private string? _lastFolder;

    public TriagePage() => InitializeComponent();

    public event Action<string>? NavigateRequested;

    public void Update(SystemMetricsSnapshot snapshot, IReadOnlyList<DiagnosticFinding> findings) { }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (TriageSession.Last is { } last && ResultPanel.Visibility == Visibility.Collapsed) Render(last.Report);
    }

    private async void Run_Click(object sender, RoutedEventArgs e) => await RunAsync();

    public async Task RunAsync()
    {
        if (_running) return;
        _running = true;
        RunButton.IsEnabled = false;
        Spinner.IsActive = true;
        StatusText.Text = "Starting…";
        try
        {
            var days = WindowPicker.SelectedIndex switch { 0 => 7, 1 => 14, _ => 30 };
            var progress = new Progress<string>(message => StatusText.Text = message);
            var options = new TriageOptions { WindowDays = days };
            var result = await TriageScanner.RunAsync(options, progress);
            TriageSession.Set(result);
            Render(result.Report);
            var problems = await Task.Run(() => TriageScanner.CommitSnapshots(result, options));
            if (problems.Count > 0) StatusText.Text += " · " + string.Join("; ", problems);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"The hardware check failed: {ex.Message}";
        }
        finally
        {
            _running = false;
            RunButton.IsEnabled = true;
            Spinner.IsActive = false;
        }
    }

    private void Render(TriageReport report)
    {
        ResultPanel.Visibility = Visibility.Visible;
        StatusText.Text = $"Checked {report.GeneratedAt.ToLocalTime():g} · last {report.WindowDays} days · {(report.Elevated ? "administrator" : "standard user")}";
        var (background, foreground) = report.Verdict.Outcome switch
        {
            TriageOutcome.HardwareEvidenceFound => (Hex("#3A2226"), Hex("#FFB4AB")),
            TriageOutcome.MinorFindings => (Hex("#33291A"), Hex("#F1D39C")),
            TriageOutcome.NoHardwareEvidence => (Hex("#1B3226"), Hex("#9FE0B8")),
            _ => (Hex("#27323A"), Hex("#C5D3DA"))
        };
        VerdictCard.Background = new SolidColorBrush(background);
        VerdictHeadline.Text = report.Verdict.Headline;
        VerdictHeadline.Foreground = new SolidColorBrush(foreground);
        VerdictDetail.Text = report.Verdict.Detail;

        ChecksPanel.Children.Clear();
        foreach (var domain in new[] { CheckDomain.HardwareDriverFirmware, CheckDomain.Software, CheckDomain.Context })
        {
            var checks = report.In(domain).ToArray();
            if (checks.Length == 0) continue;
            ChecksPanel.Children.Add(new TextBlock
            {
                Text = TriageLabels.Domain(domain), FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 0)
            });
            foreach (var check in checks) ChecksPanel.Children.Add(CheckCard(check));
        }
        NotCoveredText.Text = string.Join("\n\n", report.NotCovered);
        MachineText.Text = string.Join("\n", report.Machine.Select(item => $"{item.Label}: {item.Value}"));
        SourcesText.Text = string.Join("\n", report.SourceStatus);
    }

    private static Expander CheckCard(TriageCheck check)
    {
        var accent = check.Status switch
        {
            CheckStatus.Found when check.Domain == CheckDomain.HardwareDriverFirmware && !check.Minor => Hex("#FFB4AB"),
            CheckStatus.Found => Hex("#F1D39C"),
            CheckStatus.NotFound => Hex("#9FE0B8"),
            _ => Hex("#9FB3BC")
        };
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(new TextBlock { Text = TriageLabels.Status(check), Foreground = new SolidColorBrush(accent), FontWeight = FontWeights.SemiBold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock { Text = check.Title, FontWeight = FontWeights.SemiBold });
        text.Children.Add(new TextBlock { Text = check.Summary, Foreground = new SolidColorBrush(Hex("#A8B8C0")), FontSize = 12, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(text, 1);
        header.Children.Add(text);

        var body = new StackPanel { Spacing = 6 };
        foreach (var detail in check.Details)
            body.Children.Add(new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 13 });
        if (check.Limit is not null)
            body.Children.Add(new TextBlock { Text = check.Limit, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Hex("#D9B27C")), FontSize = 12, IsTextSelectionEnabled = true });
        if (body.Children.Count == 0) body.Children.Add(new TextBlock { Text = "Nothing further to show.", Foreground = new SolidColorBrush(Hex("#8DA0AA")), FontSize = 12 });
        return new Expander
        {
            Header = header, Content = body, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            IsExpanded = check.Status == CheckStatus.Found && !check.Minor
        };
    }

    private static TriageResult? Current => TriageSession.Last;

    private void Save_Click(object sender, RoutedEventArgs e) => Save(zip: false);
    private void SaveZip_Click(object sender, RoutedEventArgs e) => Save(zip: true);

    private void Save(bool zip)
    {
        if (Current is not { } result) return;
        try
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var folder = Path.Combine(string.IsNullOrWhiteSpace(documents) ? AppContext.BaseDirectory : documents, "Skald Reports");
            var paths = TriageBundle.Write(result, folder, RedactToggle.IsOn, zip);
            _lastFolder = folder;
            OpenFolderButton.Visibility = Visibility.Visible;
            SaveStatus.Text = $"Saved {string.Join(", ", paths.Select(Path.GetFileName))} to {folder}" + (RedactToggle.IsOn ? " (names, serial number and user names removed)." : ".");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveStatus.Text = $"Could not save the report: {ex.Message}";
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } result) return;
        var report = RedactToggle.IsOn ? TriageRedactor.Redact(result.Report) : result.Report;
        try
        {
            var data = new DataPackage();
            data.SetText(TriageReportWriter.ToText(report));
            Clipboard.SetContent(data);
            SaveStatus.Text = "Summary copied to the clipboard.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            SaveStatus.Text = "Clipboard is unavailable. Try copying again.";
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_lastFolder is null || !Directory.Exists(_lastFolder)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_lastFolder}\"") { UseShellExecute = true });
    }

    private void Hardware_Click(object sender, RoutedEventArgs e) => NavigateRequested?.Invoke("hardware");
    private void Reliability_Click(object sender, RoutedEventArgs e) => NavigateRequested?.Invoke("reliability");
    private void Updates_Click(object sender, RoutedEventArgs e) => NavigateRequested?.Invoke("updates");

    private static Color Hex(string value)
        => Color.FromArgb(0xFF, Convert.ToByte(value.Substring(1, 2), 16), Convert.ToByte(value.Substring(3, 2), 16), Convert.ToByte(value.Substring(5, 2), 16));
}
