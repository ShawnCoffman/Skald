using System.Globalization;
using System.Runtime.InteropServices;
using Skald.Core.Models;

namespace Skald.Collectors;

// One wildcard PDH counter reads all process/engine instances in a single query.
// The query is kept across samples because utilization counters need two readings.
internal sealed class GpuEngineCollector : IDisposable
{
    internal static IReadOnlyList<GpuEngineMetric> Aggregate(IReadOnlyDictionary<int, IReadOnlyDictionary<string, double>>? readings)
    {
        if (readings is null) return [];
        return readings.Values.SelectMany(engines => engines)
            .Where(pair => pair.Key.StartsWith("luid_", StringComparison.OrdinalIgnoreCase))
            .GroupBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new GpuEngineMetric(group.Key,
                group.Key.Contains("_engtype_", StringComparison.OrdinalIgnoreCase)
                    ? group.Key[(group.Key.LastIndexOf("_engtype_", StringComparison.OrdinalIgnoreCase) + 9)..] : "Other",
                Math.Clamp(group.Sum(pair => pair.Value), 0, 100)))
            .OrderByDescending(engine => engine.UtilizationPercent).ToArray();
    }
    private const string CounterPath = @"\GPU Engine(*)\Utilization Percentage";
    private const uint PdhMoreData = 0x800007D2;
    private const uint PdhFormatDouble = 0x00000200;
    private const uint ValidData = 0;
    private const uint NewData = 1;
    private const uint MaximumBufferBytes = 16 * 1024 * 1024;
    private readonly object _gate = new();
    private IntPtr _query;
    private IntPtr _counter;
    private DateTimeOffset _retryAfter;
    private bool _primed;
    private bool _disposed;

    public IReadOnlyDictionary<int, IReadOnlyDictionary<string, double>>? Sample()
    {
        if (!OperatingSystem.IsWindows()) return null;

        lock (_gate)
        {
            if (_disposed || !EnsureQuery()) return null;
            if (PdhCollectQueryData(_query) != 0)
            {
                Reset();
                return null;
            }

            if (!_primed)
            {
                _primed = true;
                return null;
            }

            return ReadInstances();
        }
    }

    private bool EnsureQuery()
    {
        if (_query != IntPtr.Zero) return true;
        if (DateTimeOffset.UtcNow < _retryAfter) return false;

        if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0 || _query == IntPtr.Zero)
        {
            _retryAfter = DateTimeOffset.UtcNow.AddSeconds(30);
            return false;
        }

        if (PdhAddEnglishCounterW(_query, CounterPath, IntPtr.Zero, out _counter) != 0 || _counter == IntPtr.Zero)
        {
            Reset();
            return false;
        }

        return true;
    }

    private Dictionary<int, IReadOnlyDictionary<string, double>>? ReadInstances()
    {
        uint size = 0;
        var status = PdhGetFormattedCounterArrayW(_counter, PdhFormatDouble, ref size, out _, IntPtr.Zero);
        if (status != PdhMoreData || size == 0 || size > MaximumBufferBytes)
        {
            if (status != 0) Reset();
            return status == 0 ? new Dictionary<int, IReadOnlyDictionary<string, double>>() : null;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(checked((int)size));
            try
            {
                var availableSize = size;
                status = PdhGetFormattedCounterArrayW(_counter, PdhFormatDouble, ref availableSize, out var count, buffer);
                if (status == PdhMoreData && availableSize > size && availableSize <= MaximumBufferBytes)
                {
                    size = availableSize;
                    continue;
                }
                if (status != 0)
                {
                    Reset();
                    return null;
                }

                var readings = new Dictionary<int, Dictionary<string, double>>();
                var itemSize = Marshal.SizeOf<FormattedCounterValueItem>();
                for (uint index = 0; index < count; index++)
                {
                    var item = Marshal.PtrToStructure<FormattedCounterValueItem>(IntPtr.Add(buffer, checked((int)index * itemSize)));
                    if (item.Status is not (ValidData or NewData) || !double.IsFinite(item.Value) || item.Value <= 0) continue;
                    var name = Marshal.PtrToStringUni(item.Name);
                    if (!TryParseInstance(name, out var processId, out var engine)) continue;
                    if (!readings.TryGetValue(processId, out var engines)) readings[processId] = engines = new(StringComparer.OrdinalIgnoreCase);
                    engines[engine] = Math.Clamp(item.Value, 0, 100);
                }

                return readings.ToDictionary(pair => pair.Key, pair => (IReadOnlyDictionary<string, double>)pair.Value);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        Reset();
        return null;
    }

    internal static bool TryParseInstance(string? instance, out int processId, out string engine)
    {
        processId = 0;
        engine = string.Empty;
        if (instance is null || !instance.StartsWith("pid_", StringComparison.OrdinalIgnoreCase)) return false;
        var luid = instance.IndexOf("_luid_", 4, StringComparison.OrdinalIgnoreCase);
        if (luid <= 4 || !int.TryParse(instance.AsSpan(4, luid - 4), NumberStyles.None, CultureInfo.InvariantCulture, out processId)) return false;
        engine = instance[(luid + 1)..];
        return engine.Length > 0;
    }

    private void Reset()
    {
        if (_query != IntPtr.Zero) _ = PdhCloseQuery(_query);
        _query = IntPtr.Zero;
        _counter = IntPtr.Zero;
        _primed = false;
        _retryAfter = DateTimeOffset.UtcNow.AddSeconds(30);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Reset();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FormattedCounterValueItem
    {
        public IntPtr Name;
        public uint Status;
        public double Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCloseQuery(IntPtr query);
}
