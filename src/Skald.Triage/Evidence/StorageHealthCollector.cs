using System.Management;

namespace Skald.Triage;

public static class StorageHealthCollector
{
    public static Task<string> CollectAsync() => Task.Run(Collect);

    private static string Collect()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
                "SELECT DeviceId,Temperature,Wear,ReadErrorsUncorrected,WriteErrorsUncorrected,PowerOnHours FROM MSFT_StorageReliabilityCounter",
                new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(8) });
            using var rows = searcher.Get();
            var details = new List<string>();
            foreach (ManagementBaseObject row in rows)
            {
                using (row)
                {
                    var id = row["DeviceId"]?.ToString() ?? "Unknown device";
                    var temperature = Value(row, "Temperature", "°C", zeroUnavailable: true);
                    var wear = Value(row, "Wear", "% wear used");
                    var readErrors = Value(row, "ReadErrorsUncorrected", " uncorrected reads");
                    var writeErrors = Value(row, "WriteErrorsUncorrected", " uncorrected writes");
                    var hours = Value(row, "PowerOnHours", " power-on hours", zeroUnavailable: true);
                    details.Add($"Device {id} · {temperature} · {wear} · {readErrors} · {writeErrors} · {hours}");
                }
            }
            return details.Count == 0 ? "No storage reliability counters exposed by this system." : string.Join("\n", details);
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        { return $"Storage reliability counters unavailable ({ex.GetType().Name})."; }
    }

    private static string Value(ManagementBaseObject row, string name, string suffix, bool zeroUnavailable = false)
    {
        var value = row[name];
        return value is null || zeroUnavailable && Convert.ToUInt64(value, System.Globalization.CultureInfo.InvariantCulture) == 0
            ? $"{name} unavailable" : $"{value}{suffix}";
    }
}
