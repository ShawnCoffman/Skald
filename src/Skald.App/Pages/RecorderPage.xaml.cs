using Microsoft.UI.Xaml.Controls;
using Skald.Core.Models;

namespace Skald.App.Pages;

public sealed partial class RecorderPage : Page, ITelemetryPage
{
    public RecorderPage() => InitializeComponent();

    public void Update(SystemMetricsSnapshot snapshot, IReadOnlyList<DiagnosticFinding> findings)
    {
    }
}
