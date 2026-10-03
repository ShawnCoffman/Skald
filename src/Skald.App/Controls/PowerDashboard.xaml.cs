using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Skald.Core.Models;
using Windows.UI;

namespace Skald.App.Controls;

public sealed partial class PowerDashboard : UserControl
{
    private readonly TelemetryHistory _history = new();
    private readonly ObservableCollection<Headline> _headlines = [];
    private readonly ObservableCollection<SensorRow> _sensorRows = [];
    private string? _selectedPower;
    private bool _updating;
    public PowerDashboard()
    {
        InitializeComponent();
        HeadlineList.ItemsSource = _headlines;
        SensorList.ItemsSource = _sensorRows;
    }

    public void Update(SystemMetricsSnapshot snapshot) { _history.Add(snapshot); Render(); }
    public void ShowHistory(IEnumerable<SystemMetricsSnapshot> samples) { _history.Replace(samples); Render(); }

    private void Render()
    {
        if (_history.Samples.Count == 0) return;
        var snapshot = _history.Samples[^1];
        var sensors = SensorCatalog.GetSensors(snapshot);
        SensorMetric? Measured(string scope) => sensors.Where(sensor => sensor.Scope == scope && sensor.Kind == "Power").OrderByDescending(sensor => sensor.Value.HasValue).FirstOrDefault();
        var cpuPower = Measured("CPU package");
        var corePower = Measured("CPU cores");
        var integratedGraphicsPower = Measured("CPU integrated graphics");
        var dramPower = Measured("DRAM");
        var platformPower = Measured("Platform");
        var temperature = sensors.FirstOrDefault(sensor => sensor.Scope == "CPU sensor" && sensor.Kind == "Temperature");
        var gpuPower = sensors.FirstOrDefault(sensor => sensor.Scope == "GPU board" && sensor.Kind == "Power" && sensor.Value.HasValue);
        var gpuLimit = gpuPower is null ? null : sensors.FirstOrDefault(sensor => sensor.Scope == "GPU power limit" && sensor.Device == gpuPower.Device);
        var discharge = snapshot.Power.Source == PowerSource.Battery ? snapshot.Power.BatteryDischargeWatts : null;
        var available = new List<string>();
        var missing = new List<string>();
        if (discharge is { } batteryWatts) available.Add($"Battery discharge: {batteryWatts:F1} W");
        else missing.Add(snapshot.Power.Source == PowerSource.Battery ? "Battery discharge: no measured reading" : "Whole-system AC draw: no measured wall-power source");
        if (cpuPower?.Value is { } cpuWatts) available.Add($"CPU package: {cpuWatts:F1} W");
        else missing.Add("CPU package power: no Windows Energy Meter or hardware-sensor provider");
        if (corePower?.Value is { } coreWatts) available.Add($"CPU cores: {coreWatts:F1} W");
        if (integratedGraphicsPower?.Value is { } igpuWatts) available.Add($"Integrated graphics: {igpuWatts:F1} W");
        if (dramPower?.Value is { } dramWatts) available.Add($"DRAM: {dramWatts:F1} W");
        if (platformPower?.Value is { } platformWatts) available.Add($"Platform (PSys): {platformWatts:F1} W");
        if (gpuPower?.Value is { } gpuWatts) available.Add($"GPU board: {gpuWatts:F1} W" + (gpuLimit?.Value is { } limitWatts ? $" of {limitWatts:F0} W limit" : string.Empty));
        else missing.Add("GPU board power: no measured reading");
        foreach (var meter in sensors.Where(sensor => sensor.Id.StartsWith("windows/power-meter/", StringComparison.Ordinal) && sensor.Kind == "Power" && sensor.Value.HasValue))
            available.Add($"Power meter {meter.Device} (unidentified domain): {meter.Value:F1} W");
        if (snapshot.Power.DisplayBrightnessPercent is { } brightness) available.Add($"Display brightness: {brightness}%");
        else missing.Add("Display brightness: unavailable");
        var zoneLimits = sensors.Where(sensor => sensor.Id.EndsWith("/passive-limit", StringComparison.Ordinal)).ToArray();
        var limiting = sensors.Where(sensor => (sensor.Id.EndsWith("/passive-limit", StringComparison.Ordinal) && sensor.Value is < 100)
            || (sensor.Id.EndsWith("/throttle-reasons", StringComparison.Ordinal) && sensor.Value is > 0)).ToArray();
        if (limiting.Length > 0) available.Add("Platform thermal limit: ACTIVE (" + string.Join(", ", limiting.Select(sensor => $"{sensor.Device} {sensor.Name} {sensor.Value:F0}{(sensor.Unit == "%" ? "%" : string.Empty)}")) + ")");
        else if (zoneLimits.Any(sensor => sensor.Value.HasValue)) available.Add("Platform thermal limit: none reported");
        else missing.Add("Thermal limiting: no platform thermal zone reports a limit");
        var cpuLimit = sensors.FirstOrDefault(sensor => sensor.Id == "windows/cpu/performance-limit");
        var cpuLimitFlags = sensors.FirstOrDefault(sensor => sensor.Id == "windows/cpu/performance-limit-flags");
        if (cpuLimit?.Value is < 100 || cpuLimitFlags?.Value is > 0)
            available.Add($"CPU performance limit: ACTIVE ({Format(cpuLimit?.Value, "%")} of maximum frequency, flags {Format(cpuLimitFlags?.Value, "bitmask")})");
        else if (cpuLimit?.Value is not null) available.Add("CPU performance limit: none reported");
        else missing.Add("CPU performance limit: Windows does not report one");
        missing.Add("CPU power limit values (PL1/PL2): not exposed by Windows");
        foreach (var reasons in sensors.Where(sensor => sensor.Scope == "GPU clock limit" && sensor.Value.HasValue))
        {
            var caps = SensorCatalog.DescribeGpuCapReasons(reasons.Value);
            available.Add(caps.Count > 0 ? $"GPU clock limit: ACTIVE ({string.Join(", ", caps)}) · {reasons.Device}" : $"GPU clock limit: no power or thermal cap · {reasons.Device}");
        }
        SystemPowerText.Text = available.Count > 0 ? string.Join("  ·  ", available) : "No measured power readings are available on this machine.";
        MissingSourcesText.Text = string.Join("\n", missing);
        var active = snapshot.Processes.Where(process => process.CpuAvailable && process.CpuPercent > 0 || process.GpuPercent is > 0)
            .OrderByDescending(process => (process.CpuAvailable ? process.CpuPercent : 0) + process.GpuPercent.GetValueOrDefault()).FirstOrDefault();
        ActivityText.Text = active is null ? "Top process activity: Unavailable" : $"Top process activity: {active.Name} (PID {active.ProcessId}) · CPU {active.CpuPercent:F1}% · busiest GPU engine {(active.GpuPercent is { } gpu ? $"{gpu:F1}%" : "Unavailable")}";
        var headlines = new List<(string Name, string Value, string Detail)>();
        if (gpuPower?.Value is not null) headlines.Add(("GPU board power", Format(gpuPower.Value, "W"), gpuLimit?.Value is { } boardLimit ? $"{gpuPower.Device} · limit {boardLimit:F0} W" : gpuPower.Device));
        if (cpuPower?.Value is not null) headlines.Add(("CPU package", Format(cpuPower.Value, "W"), $"{cpuPower.Device} · {cpuPower.Source}"));
        if (corePower?.Value is not null) headlines.Add(("CPU cores", Format(corePower.Value, "W"), "Part of package power"));
        if (integratedGraphicsPower?.Value is not null) headlines.Add(("Integrated graphics", Format(integratedGraphicsPower.Value, "W"), "Part of package power"));
        if (dramPower?.Value is not null) headlines.Add(("DRAM power", Format(dramPower.Value, "W"), dramPower.Source));
        if (temperature?.Value is not null) headlines.Add(("CPU temperature", Format(temperature.Value, "°C"), temperature.Name));
        if (snapshot.Cpu.EffectiveMegahertz is > 0) headlines.Add(("Effective clock (estimate)", Format(snapshot.Cpu.EffectiveMegahertz / 1000, "GHz"), "Base frequency × % Processor Performance"));
        if (snapshot.Cpu.ReportedMegahertz is > 0) headlines.Add(("OS reported clock", Format(snapshot.Cpu.ReportedMegahertz / 1000, "GHz"), "Core average · often a nominal firmware value"));
        if (_headlines.Count != headlines.Count || _headlines.Where((headline, index) => headline.Name != headlines[index].Name).Any())
        {
            _headlines.Clear();
            foreach (var headline in headlines) _headlines.Add(new Headline(headline.Name));
        }
        for (var index = 0; index < headlines.Count; index++) _headlines[index].Set(headlines[index].Value, headlines[index].Detail);
        ContextText.Text = $"{snapshot.Power.Source} power · Sample {snapshot.Timestamp.ToLocalTime():HH:mm:ss}";
        BatteryPanel.Visibility = snapshot.Power.HasBattery ? Visibility.Visible : Visibility.Collapsed;
        var modeledRuntime = discharge is > 0 && snapshot.Power.BatteryRemainingCapacityWh is { } capacity
            ? TimeSpan.FromHours(capacity / discharge.Value) : (TimeSpan?)null;
        BatteryText.Text = $"Battery {snapshot.Power.BatteryPercent}% · Health {Format(snapshot.Power.BatteryHealthPercent, "%")} · Full charge {Format(snapshot.Power.BatteryFullChargeCapacityWh, "Wh")} / Design {Format(snapshot.Power.BatteryDesignCapacityWh, "Wh")}\nRuntime at current discharge: {(modeledRuntime is { } runtime && runtime.TotalHours < 168 ? $"{(int)runtime.TotalHours}h {runtime.Minutes}m" : "Unavailable")} · Windows estimate {(snapshot.Power.EstimatedRemaining is { } remaining ? $"{(int)remaining.TotalHours}h {remaining.Minutes}m" : "Unavailable")}";
        var powers = sensors.Where(sensor => sensor.Kind == "Power").Select(sensor => new PowerChoice(sensor.Id, $"{sensor.Device} · {sensor.Name}")).ToArray();
        if (_selectedPower is null || !powers.Any(choice => choice.Id == _selectedPower))
            _selectedPower = snapshot.Power.Source == PowerSource.Battery && snapshot.Power.BatteryDischargeWatts.HasValue ? "battery/discharge" : sensors.FirstOrDefault(sensor => sensor.Kind == "Power" && sensor.Value.HasValue)?.Id ?? powers.FirstOrDefault()?.Id;
        var oldChoices = (PowerSensorPicker.ItemsSource as PowerChoice[]) ?? [];
        if (!oldChoices.SequenceEqual(powers))
        {
            _updating = true;
            PowerSensorPicker.ItemsSource = powers;
            PowerSensorPicker.SelectedItem = powers.FirstOrDefault(choice => choice.Id == _selectedPower);
            _updating = false;
        }
        var historyBySensor = _history.Samples.SelectMany(SensorCatalog.GetSensors).Where(item => item.Value.HasValue).ToLookup(item => item.Id, item => item.Value!.Value);
        var rows = sensors.Select(sensor => {
            var values = historyBySensor[sensor.Id].ToArray();
            var statistics = values.Length == 0 ? "Unavailable" : $"Now {Format(sensor.Value, sensor.Unit)}  ·  Min {Format(values.Min(), sensor.Unit)}  ·  Avg {Format(values.Average(), sensor.Unit)}  ·  Max {Format(values.Max(), sensor.Unit)}";
            return new SensorRow(sensor.Id, $"{sensor.Device} / {sensor.Name}", statistics, $"{sensor.Source} · {sensor.Scope} · {sensor.Id}\n{sensor.Note}");
        }).ToArray();
        if (_sensorRows.Count != rows.Length || _sensorRows.Where((row, index) => row.Id != rows[index].Id).Any())
        {
            _sensorRows.Clear();
            foreach (var row in rows) _sensorRows.Add(row);
        }
        else for (var index = 0; index < rows.Length; index++) _sensorRows[index].Set(rows[index].Name, rows[index].Statistics, rows[index].Source);
        RenderCharts();
    }

    private void RenderCharts()
    {
        if (_history.Samples.Count == 0) return;
        var samples = _history.Samples;
        var end = samples[^1].Timestamp;
        CpuChart.SetTimedSeries(samples.Select(sample => (sample.Timestamp, sample.Cpu.Availability.IsSupported ? (double?)sample.Cpu.UtilizationPercent : null)), end, 100, Color.FromArgb(255, 95, 222, 185));
        var clockMax = Math.Max(1, samples.Select(sample => sample.Cpu.ReportedMegahertz.GetValueOrDefault() / 1000).Max() * 1.15);
        ClockChart.SetTimedSeries(samples.Select(sample => (sample.Timestamp, sample.Cpu.ReportedMegahertz / 1000)), end, clockMax, Color.FromArgb(255, 173, 150, 238));
        ClockScaleText.Text = "Windows reported core-average clock (often a nominal firmware value). The effective clock estimate is listed under sensors.";
        var power = samples.Select(sample => (sample.Timestamp, Value: SensorCatalog.GetSensors(sample).FirstOrDefault(sensor => sensor.Id == _selectedPower)?.Value)).ToArray();
        var max = Math.Max(10, power.Select(sample => sample.Value.GetValueOrDefault()).DefaultIfEmpty().Max() * 1.15);
        var hasPower = power.Any(sample => sample.Value.HasValue);
        PowerChart.SetTimedSeries(power, end, max, Color.FromArgb(255, 237, 180, 101));
        PowerScaleText.Text = hasPower ? "Axis range is based on observed samples, not rated capacity." : "No samples for this sensor. CPU package readings require Windows Energy Meter counters or an identified hardware provider.";
    }

    private void PowerSensorPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || PowerSensorPicker.SelectedItem is not PowerChoice choice) return;
        _selectedPower = choice.Id;
        RenderCharts();
    }
    private static string Format(double? value, string unit) => value switch
    {
        null => "Unavailable",
        { } number when unit == "bitmask" => $"0x{(ulong)Math.Max(0, number):X}",
        { } number when unit == "P-state" => $"P{number:F0}",
        { } number when unit == "%" => $"{number:F1}%",
        { } number => $"{number:F1} {unit}"
    };
    private sealed class Headline(string name) : INotifyPropertyChanged
    {
        public string Name { get; } = name;
        public string Value { get; private set; } = "Unavailable";
        public string Detail { get; private set; } = string.Empty;
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Set(string value, string detail)
        {
            if (Value != value) { Value = value; PropertyChanged?.Invoke(this, new(nameof(Value))); }
            if (Detail != detail) { Detail = detail; PropertyChanged?.Invoke(this, new(nameof(Detail))); }
        }
    }
    private sealed class SensorRow(string id, string name, string statistics, string source) : INotifyPropertyChanged
    {
        public string Id { get; } = id;
        public string Name { get; private set; } = name;
        public string Statistics { get; private set; } = statistics;
        public string Source { get; private set; } = source;
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Set(string name, string statistics, string source)
        {
            if (Name != name) { Name = name; PropertyChanged?.Invoke(this, new(nameof(Name))); }
            if (Statistics != statistics) { Statistics = statistics; PropertyChanged?.Invoke(this, new(nameof(Statistics))); }
            if (Source != source) { Source = source; PropertyChanged?.Invoke(this, new(nameof(Source))); }
        }
    }
    private sealed record PowerChoice(string Id, string Label);
}

