using System.Runtime.InteropServices;
using System.Text;
using Skald.Core.Models;

namespace Skald.Collectors;

// NVML is installed with the NVIDIA driver; it is never redistributed by Skald.
// Load only from System32 to avoid picking up an untrusted DLL next to the app.
internal sealed class NvidiaGpuCollector : IDisposable
{
    private const double BytesPerMegabyte = 1024d * 1024d;
    private readonly object _gate = new();
    private IntPtr _library;
    private NvmlInit? _initialize;
    private NvmlShutdown? _shutdown;
    private NvmlDeviceGetCount? _getCount;
    private NvmlDeviceGetHandleByIndex? _getHandle;
    private NvmlDeviceGetName? _getName;
    private NvmlDeviceGetTemperature? _getTemperature;
    private NvmlDeviceGetPowerUsage? _getPower;
    private NvmlDeviceGetMemoryInfo? _getMemory;
    private NvmlDeviceGetClockInfo? _getClock;
    private NvmlDeviceGetName? _getUuid;
    private DateTimeOffset _retryAfter;
    private bool _initialized;
    private bool _disposed;

    public IReadOnlyList<GpuDeviceMetric> Sample()
    {
        if (!OperatingSystem.IsWindows()) return [];
        lock (_gate)
        {
            if (_disposed || !EnsureInitialized() || _getCount is null || _getHandle is null || _getName is null) return [];
            if (_getCount(out var count) != 0) return [];

            var devices = new List<GpuDeviceMetric>();
            for (uint index = 0; index < Math.Min(count, 16); index++)
            {
                if (_getHandle(index, out var handle) != 0 || handle == IntPtr.Zero) continue;
                var nameBuffer = new byte[96];
                var name = _getName(handle, nameBuffer, (uint)nameBuffer.Length) == 0
                    ? DecodeName(nameBuffer)
                    : $"NVIDIA GPU {index}";
                double? temperature = _getTemperature is not null && _getTemperature(handle, 0, out var celsius) == 0 ? celsius : null;
                double? power = _getPower is not null && _getPower(handle, out var milliwatts) == 0 ? milliwatts / 1000d : null;
                double? used = null;
                double? total = null;
                if (_getMemory is not null && _getMemory(handle, out var memory) == 0)
                {
                    used = memory.Used / BytesPerMegabyte;
                    total = memory.Total / BytesPerMegabyte;
                }
                var uuid = new byte[96];
                var id = _getUuid is not null && _getUuid(handle, uuid, (uint)uuid.Length) == 0 ? DecodeName(uuid) : $"nvml-index-{index}";
                devices.Add(new GpuDeviceMetric(name, "NVIDIA NVML", temperature, power, used, total)
                {
                    DeviceId = id,
                    GraphicsClockMegahertz = _getClock is not null && _getClock(handle, 0, out var graphicsClock) == 0 ? graphicsClock : null,
                    MemoryClockMegahertz = _getClock is not null && _getClock(handle, 2, out var memoryClock) == 0 ? memoryClock : null
                });
            }
            return devices;
        }
    }

    private bool EnsureInitialized()
    {
        if (_initialized) return true;
        if (DateTimeOffset.UtcNow < _retryAfter) return false;
        var path = Path.Combine(Environment.SystemDirectory, "nvml.dll");
        if (!File.Exists(path) || !NativeLibrary.TryLoad(path, out _library))
        {
            _retryAfter = DateTimeOffset.UtcNow.AddMinutes(1);
            return false;
        }

        _initialize = GetExport<NvmlInit>("nvmlInit_v2");
        _shutdown = GetExport<NvmlShutdown>("nvmlShutdown");
        _getCount = GetExport<NvmlDeviceGetCount>("nvmlDeviceGetCount_v2");
        _getHandle = GetExport<NvmlDeviceGetHandleByIndex>("nvmlDeviceGetHandleByIndex_v2");
        _getName = GetExport<NvmlDeviceGetName>("nvmlDeviceGetName");
        _getTemperature = GetExport<NvmlDeviceGetTemperature>("nvmlDeviceGetTemperature");
        _getPower = GetExport<NvmlDeviceGetPowerUsage>("nvmlDeviceGetPowerUsage");
        _getMemory = GetExport<NvmlDeviceGetMemoryInfo>("nvmlDeviceGetMemoryInfo");
        _getClock = GetExport<NvmlDeviceGetClockInfo>("nvmlDeviceGetClockInfo");
        _getUuid = GetExport<NvmlDeviceGetName>("nvmlDeviceGetUUID");
        if (_initialize is null || _shutdown is null || _getCount is null || _getHandle is null || _getName is null || _initialize() != 0)
        {
            ReleaseLibrary();
            _retryAfter = DateTimeOffset.UtcNow.AddMinutes(1);
            return false;
        }
        _initialized = true;
        return true;
    }

    private T? GetExport<T>(string name) where T : Delegate
        => NativeLibrary.TryGetExport(_library, name, out var address)
            ? Marshal.GetDelegateForFunctionPointer<T>(address)
            : null;

    private static string DecodeName(byte[] bytes)
    {
        var length = Array.IndexOf(bytes, (byte)0);
        return Encoding.UTF8.GetString(bytes, 0, length >= 0 ? length : bytes.Length);
    }

    private void ReleaseLibrary()
    {
        if (_initialized) _ = _shutdown?.Invoke();
        _initialized = false;
        if (_library != IntPtr.Zero) NativeLibrary.Free(_library);
        _library = IntPtr.Zero;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseLibrary();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlMemoryInfo
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlInit();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlShutdown();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlDeviceGetCount(out uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlDeviceGetHandleByIndex(uint index, out IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlDeviceGetName(IntPtr device, [Out] byte[] name, uint length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlDeviceGetTemperature(IntPtr device, uint sensor, out uint temperature);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMemoryInfo memory);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlDeviceGetClockInfo(IntPtr device, uint clockType, out uint megahertz);
}
