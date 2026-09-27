using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace Skald.App.Controls;

public sealed partial class HistoryChart : UserControl
{
    private double?[] _samples = [];
    private double _maximum = 100;
    private Color _color = Color.FromArgb(255, 89, 220, 185);
    private DateTimeOffset[]? _timestamps;
    private DateTimeOffset _windowEnd;
    private double _windowSeconds = 180;
    public string MetricName { get; set; } = "Value";
    public string Unit { get; set; } = string.Empty;

    public HistoryChart() => InitializeComponent();

    public void SetSeries(IEnumerable<double?> samples, double maximum, Color color)
    {
        _timestamps = null;
        _samples = samples.ToArray();
        _maximum = Math.Max(1, maximum);
        _color = color;
        Redraw();
    }

    public void SetTimedSeries(IEnumerable<(DateTimeOffset Timestamp, double? Value)> samples, DateTimeOffset end, double maximum, Color color, TimeSpan? window = null)
    {
        var values = samples.ToArray();
        _timestamps = values.Select(sample => sample.Timestamp).ToArray();
        _samples = values.Select(sample => sample.Value).ToArray();
        _windowEnd = end;
        _windowSeconds = Math.Max(1, (window ?? TimeSpan.FromMinutes(3)).TotalSeconds);
        _maximum = Math.Max(1, maximum);
        _color = color;
        Redraw();
    }

    private void Chart_SizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    private void Redraw()
    {
        RefreshLabels();
        var width = Plot.ActualWidth;
        var height = Plot.ActualHeight;
        if (width < 2 || height < 2) return;

        Plot.Children.Clear();
        for (var step = 1; step <= 3; step++)
        {
            var line = new Line
            {
                X1 = 0,
                X2 = width,
                Y1 = height * step / 4,
                Y2 = height * step / 4,
                Stroke = new SolidColorBrush(Color.FromArgb(255, 37, 57, 67)),
                StrokeThickness = 1
            };
            Plot.Children.Add(line);
        }

        if (_samples.Length == 0) return;

        var geometry = new PathGeometry();
        PathFigure? figure = null;
        for (var index = 0; index < _samples.Length; index++)
        {
            var value = _samples[index];
            if (value is null)
            {
                figure = null;
                continue;
            }

            if (_timestamps is not null && index > 0 && (_timestamps[index] - _timestamps[index - 1]).TotalSeconds > 6) figure = null;
            var x = _timestamps is null
                ? (_samples.Length == 1 ? width : index * width / (_samples.Length - 1))
                : Math.Clamp(1 - (_windowEnd - _timestamps[index]).TotalSeconds / _windowSeconds, 0, 1) * width;
            var y = height - Math.Clamp(value.Value / _maximum, 0, 1) * (height - 8) - 4;
            if (figure is null)
            {
                figure = new PathFigure { StartPoint = new Point(x, y), IsClosed = false, IsFilled = false };
                geometry.Figures.Add(figure);
            }
            else
            {
                figure.Segments.Add(new LineSegment { Point = new Point(x, y) });
            }
        }

        Plot.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = geometry,
            Stroke = new SolidColorBrush(_color),
            StrokeThickness = 2.5,
            StrokeLineJoin = PenLineJoin.Round
        });
    }

    private void RefreshLabels()
    {
        AxisTitleText.Text = string.IsNullOrWhiteSpace(Unit) ? $"Y · {MetricName}" : $"Y · {MetricName} ({Unit})";
        MaxTickText.Text = FormatValue(_maximum, false);
        MidTickText.Text = FormatValue(_maximum / 2, false);
        ZeroTickText.Text = FormatValue(0, false);

        double? observed = null;
        var start = _windowEnd.AddSeconds(-_windowSeconds);
        for (var index = 0; index < _samples.Length; index++)
        {
            if (_samples[index] is not { } value || !double.IsFinite(value)) continue;
            if (_timestamps is not null && (_timestamps[index] < start || _timestamps[index] > _windowEnd)) continue;
            observed = Math.Max(observed ?? value, value);
        }
        MaxObservedText.Text = observed is { } peak ? $"Max observed {FormatValue(peak, true)}" : "Max observed —";

        if (_timestamps is null)
        {
            StartTimeText.Text = "First sample";
            EndTimeText.Text = "Latest sample";
        }
        else
        {
            var localStart = start.ToLocalTime();
            var localEnd = _windowEnd.ToLocalTime();
            var format = localStart.Date == localEnd.Date && _windowSeconds <= 3600 ? "HH:mm:ss" : "M/d HH:mm";
            StartTimeText.Text = localStart.ToString(format, System.Globalization.CultureInfo.CurrentCulture);
            EndTimeText.Text = localEnd.ToString(format, System.Globalization.CultureInfo.CurrentCulture);
        }
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this,
            $"{MetricName} over time. Maximum observed: {(observed is { } maximum ? FormatValue(maximum, true) : "unavailable")}. Y axis {FormatValue(0, false)} to {FormatValue(_maximum, false)}. X axis {StartTimeText.Text} to {EndTimeText.Text}.");
    }

    private string FormatValue(double value, bool observed)
    {
        if (Unit == "B/s")
        {
            var magnitude = Math.Abs(value);
            if (magnitude >= 1048576) return $"{value / 1048576:0.#} MB/s";
            if (magnitude >= 1024) return $"{value / 1024:0.#} KB/s";
            return $"{value:0.#} B/s";
        }
        var pattern = Unit switch
        {
            "GHz" or "GB" or "MB/s" => observed ? "0.##" : "0.#",
            "%" or "W" or "°C" or "transfers/s" => observed ? "0.#" : "0",
            _ => observed ? "0.##" : "0.#"
        };
        return string.IsNullOrWhiteSpace(Unit) ? value.ToString(pattern, System.Globalization.CultureInfo.CurrentCulture)
            : $"{value.ToString(pattern, System.Globalization.CultureInfo.CurrentCulture)}{(Unit == "%" ? string.Empty : " ")}{Unit}";
    }
}
