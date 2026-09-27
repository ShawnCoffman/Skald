using System.Globalization;
using System.Management;
using Skald.Core.Models;

namespace Skald.Collectors;

// Consume an existing local provider. Skald never installs or starts its kernel driver.
internal sealed class HardwareSensorCollector
{
    private DateTimeOffset _retryAfter;
    private string? _provider;

    public IReadOnlyList<SensorMetric> Sample()
    {
        if (DateTimeOffset.UtcNow < _retryAfter) return [];
        foreach (var provider in _provider is null ? new[] { "LibreHardwareMonitor", "OpenHardwareMonitor" } : new[] { _provider })
        {
            try
            {
                using var searcher = new ManagementObjectSearcher($@"root\{provider}",
                    "SELECT Identifier, Parent, Name, SensorType, Value FROM Sensor WHERE SensorType = 'Power' OR SensorType = 'Temperature' OR SensorType = 'Clock'");
                searcher.Options.Timeout = TimeSpan.FromSeconds(1);
                using var collection = searcher.Get();
                var result = new List<SensorMetric>();
                foreach (ManagementObject item in collection)
                {
                    using (item)
                    {
                        var kind = item["SensorType"]?.ToString() ?? "";
                        var value = item["Value"] is { } raw ? Convert.ToDouble(raw, CultureInfo.InvariantCulture) : (double?)null;
                        if (value is { } number && (!double.IsFinite(number) || number < 0)) value = null;
                        var parent = item["Parent"]?.ToString() ?? "Unknown device";
                        var name = item["Name"]?.ToString() ?? "Sensor";
                        var scope = parent.StartsWith("/amdcpu/", StringComparison.Ordinal) || parent.StartsWith("/intelcpu/", StringComparison.Ordinal)
                            ? "CPU sensor" : "Device sensor";
                        if (scope == "CPU sensor" && kind == "Power" && name.Equals("CPU Package", StringComparison.OrdinalIgnoreCase)) scope = "CPU package";
                        result.Add(new SensorMetric($"{provider}/{item["Identifier"]}", parent, name, kind,
                            kind == "Power" ? "W" : kind == "Clock" ? "MHz" : "°C", value, provider, scope,
                            "Local WMI provider; provider measurement age is not exposed."));
                    }
                }
                _provider = provider;
                return result;
            }
            catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or TimeoutException) { }
        }
        _provider = null;
        _retryAfter = DateTimeOffset.UtcNow.AddMinutes(1);
        return [];
    }
}
