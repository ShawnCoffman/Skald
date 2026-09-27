using System.Globalization;
using System.Runtime.InteropServices;
using Skald.Core.Models;

namespace Skald.Collectors;

// Windows VidMm memory counters are gauges, so unlike GPU engine utilization
// they do not need a second sample before they can be displayed.
internal sealed class GpuMemoryCollector : IDisposable
{
    private const uint PdhMoreData = 0x800007D2;
    private const uint PdhFormatDouble = 0x00000200;
    private const uint MaximumBufferBytes = 16 * 1024 * 1024;
    private const double BytesPerMegabyte = 1024d * 1024d;
    private readonly object _gate = new();
    private IntPtr _query;
    private IntPtr _processDedicated;
    private IntPtr _processShared;
    private IntPtr _adapterDedicated;
    private IntPtr _adapterShared;
    private DateTimeOffset _retryAfter;
    private bool _disposed;

    public GpuMemorySample? Sample()
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

            var processDedicated = SumByProcess(ReadCounter(_processDedicated));
            var processShared = SumByProcess(ReadCounter(_processShared));
            var adapterDedicated = ReadCounter(_adapterDedicated);
            var adapterShared = ReadCounter(_adapterShared);
            var adapterKeys = (adapterDedicated?.Keys.AsEnumerable() ?? Enumerable.Empty<string>())
                .Concat(adapterShared?.Keys.AsEnumerable() ?? Enumerable.Empty<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var adapters = adapterKeys.Select(key => new GpuAdapterMemoryMetric(
                key,
                GetMegabytes(adapterDedicated, key),
                GetMegabytes(adapterShared, key))).ToArray();

            if (processDedicated is null && processShared is null && adapters.Length == 0) return null;
            return new GpuMemorySample(processDedicated, processShared, adapters);
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

        _processDedicated = Add(@"\GPU Process Memory(*)\Dedicated Usage");
        _processShared = Add(@"\GPU Process Memory(*)\Shared Usage");
        _adapterDedicated = Add(@"\GPU Adapter Memory(*)\Dedicated Usage");
        _adapterShared = Add(@"\GPU Adapter Memory(*)\Shared Usage");
        if (_processDedicated == IntPtr.Zero && _processShared == IntPtr.Zero &&
            _adapterDedicated == IntPtr.Zero && _adapterShared == IntPtr.Zero)
        {
            Reset();
            return false;
        }
        return true;
    }

    private IntPtr Add(string path)
        => PdhAddEnglishCounterW(_query, path, IntPtr.Zero, out var counter) == 0 ? counter : IntPtr.Zero;

    private static Dictionary<int, double>? SumByProcess(Dictionary<string, double>? instances)
    {
        if (instances is null) return null;
        var result = new Dictionary<int, double>();
        foreach (var (name, bytes) in instances)
        {
            if (!TryParseProcessInstance(name, out var processId)) continue;
            result[processId] = result.GetValueOrDefault(processId) + bytes;
        }
        return result;
    }

    internal static bool TryParseProcessInstance(string? instance, out int processId)
    {
        processId = 0;
        if (instance is null || !instance.StartsWith("pid_", StringComparison.OrdinalIgnoreCase)) return false;
        var luid = instance.IndexOf("_luid_", 4, StringComparison.OrdinalIgnoreCase);
        return luid > 4 && int.TryParse(instance.AsSpan(4, luid - 4), NumberStyles.None, CultureInfo.InvariantCulture, out processId);
    }

    private static double? GetMegabytes(Dictionary<string, double>? values, string key)
        => values is null ? null : values.TryGetValue(key, out var bytes) ? bytes / BytesPerMegabyte : 0;

    private static Dictionary<string, double>? ReadCounter(IntPtr counter)
    {
        if (counter == IntPtr.Zero) return null;
        uint size = 0;
        var status = PdhGetFormattedCounterArrayW(counter, PdhFormatDouble, ref size, out _, IntPtr.Zero);
        if (status != PdhMoreData || size == 0 || size > MaximumBufferBytes) return null;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(checked((int)size));
            try
            {
                var availableSize = size;
                status = PdhGetFormattedCounterArrayW(counter, PdhFormatDouble, ref availableSize, out var count, buffer);
                if (status == PdhMoreData && availableSize > size && availableSize <= MaximumBufferBytes)
                {
                    size = availableSize;
                    continue;
                }
                if (status != 0) return null;

                var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                var itemSize = Marshal.SizeOf<FormattedCounterValueItem>();
                for (uint index = 0; index < count; index++)
                {
                    var item = Marshal.PtrToStructure<FormattedCounterValueItem>(IntPtr.Add(buffer, checked((int)index * itemSize)));
                    if (item.Status is not (0 or 1) || !double.IsFinite(item.Value) || item.Value < 0) continue;
                    var name = Marshal.PtrToStringUni(item.Name);
                    if (!string.IsNullOrWhiteSpace(name)) result[name] = item.Value;
                }
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return null;
    }

    private void Reset()
    {
        if (_query != IntPtr.Zero) _ = PdhCloseQuery(_query);
        _query = IntPtr.Zero;
        _processDedicated = IntPtr.Zero;
        _processShared = IntPtr.Zero;
        _adapterDedicated = IntPtr.Zero;
        _adapterShared = IntPtr.Zero;
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

internal sealed record GpuMemorySample(
    IReadOnlyDictionary<int, double>? ProcessDedicatedBytes,
    IReadOnlyDictionary<int, double>? ProcessSharedBytes,
    IReadOnlyList<GpuAdapterMemoryMetric> Adapters)
{
    private const double BytesPerMegabyte = 1024d * 1024d;

    public double? DedicatedMegabytes(int processId)
        => ProcessDedicatedBytes is null ? null : ProcessDedicatedBytes.GetValueOrDefault(processId) / BytesPerMegabyte;

    public double? SharedMegabytes(int processId)
        => ProcessSharedBytes is null ? null : ProcessSharedBytes.GetValueOrDefault(processId) / BytesPerMegabyte;
}
