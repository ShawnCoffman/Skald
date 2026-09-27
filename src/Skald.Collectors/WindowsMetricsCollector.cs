using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Net.NetworkInformation;
using System.Security.Principal;
using Skald.Core.Interfaces;
using Skald.Core.Models;

namespace Skald.Collectors;

public sealed class WindowsMetricsCollector : ISystemMetricsCollector, IDisposable
{
    private static readonly IReadOnlyDictionary<string, double> EmptyGpuEngines = new Dictionary<string, double>();
    private readonly object _gate = new();
    private readonly GpuEngineCollector _gpuCollector = new();
    private readonly GpuMemoryCollector _gpuMemoryCollector = new();
    private readonly NvidiaGpuCollector _nvidiaGpuCollector = new();
    private readonly WindowsPowerTelemetryCollector _powerTelemetryCollector = new();
    private readonly BatteryTelemetryCollector _batteryTelemetry = new();
    private readonly WindowsCpuDetailCollector _cpuDetailCollector = new();
    private readonly HardwareSensorCollector _hardwareSensors = new();
    private readonly PhysicalDiskCollector _physicalDisks = new();
    private readonly Dictionary<ProcessIdentity, ProcessState> _previousProcesses = [];
    private readonly Dictionary<string, NetworkState> _previousNetwork = [];
    private readonly PerformanceCounter? _diskActiveTime;
    private readonly PerformanceCounter? _diskTransfers;
    private readonly PerformanceCounter? _cpuQueue;
    private readonly PerformanceCounter? _cpuInterrupt;
    private readonly PerformanceCounter? _cpuDpc;
    private readonly PerformanceCounter? _pageReads;
    private readonly PerformanceCounter? _tcpV4Retransmits;
    private readonly PerformanceCounter? _tcpV6Retransmits;
    private CpuTimes? _previousCpuTimes;
    private DateTimeOffset? _previousCpuTimestamp;

    public WindowsMetricsCollector()
    {
        (_diskActiveTime, _diskTransfers) = CreateDiskCounters();
        _cpuQueue = CreateCounter("System", "Processor Queue Length", string.Empty);
        _cpuInterrupt = CreateCounter("Processor", "% Interrupt Time", "_Total");
        _cpuDpc = CreateCounter("Processor", "% DPC Time", "_Total");
        _pageReads = CreateCounter("Memory", "Page Reads/sec", string.Empty);
        _tcpV4Retransmits = CreateCounter("TCPv4", "Segments Retransmitted/sec", string.Empty);
        _tcpV6Retransmits = CreateCounter("TCPv6", "Segments Retransmitted/sec", string.Empty);
    }

    public string Name => "Windows system telemetry";

    public bool IsSupported => OperatingSystem.IsWindows();

    public Task<SystemMetricsSnapshot> SampleAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsSupported)
        {
            throw new PlatformNotSupportedException("Skald requires Windows.");
        }

        return Task.Run(() => Sample(cancellationToken), cancellationToken);
    }

    private SystemMetricsSnapshot Sample(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var timestamp = DateTimeOffset.UtcNow;
        var cpu = _cpuDetailCollector.Enrich(SampleCpu(timestamp));
        var memory = SampleMemory();
        var disk = SampleDisk();
        var gpuEngines = _gpuCollector.Sample();
        var gpuMemory = _gpuMemoryCollector.Sample();
        var gpuDevices = _nvidiaGpuCollector.Sample();
        var processes = SampleProcesses(timestamp, gpuEngines, gpuMemory, cancellationToken);

        return new SystemMetricsSnapshot(timestamp, cpu, memory, disk, processes)
        {
            Power = SamplePower(),
            Network = SampleNetwork(timestamp),
            GpuAdapters = gpuMemory?.Adapters ?? [],
            GpuDevices = gpuDevices,
            GpuEngines = GpuEngineCollector.Aggregate(gpuEngines),
            GpuEnginesAvailable = gpuEngines is not null,
            Sensors = _powerTelemetryCollector.Sample().Concat(_hardwareSensors.Sample()).ToArray()
        };
    }

    public void Dispose()
    {
        _diskActiveTime?.Dispose();
        _diskTransfers?.Dispose();
        _physicalDisks.Dispose();
        _cpuQueue?.Dispose();
        _cpuInterrupt?.Dispose();
        _cpuDpc?.Dispose();
        _pageReads?.Dispose();
        _tcpV4Retransmits?.Dispose();
        _tcpV6Retransmits?.Dispose();
        _gpuCollector.Dispose();
        _gpuMemoryCollector.Dispose();
        _nvidiaGpuCollector.Dispose();
        _powerTelemetryCollector.Dispose();
        _cpuDetailCollector.Dispose();
    }

    private CpuMetric SampleCpu(DateTimeOffset timestamp)
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return new CpuMetric(0, Environment.ProcessorCount, MetricAvailability.Unsupported("GetSystemTimes failed."));
        }

        var current = new CpuTimes(FileTimeToUInt64(idle), FileTimeToUInt64(kernel), FileTimeToUInt64(user));
        double utilization = 0;

        lock (_gate)
        {
            if (_previousCpuTimes is { } previous && _previousCpuTimestamp is { } previousTimestamp)
            {
                var totalDelta = current.Total - previous.Total;
                var idleDelta = current.Idle - previous.Idle;
                if (totalDelta > 0)
                {
                    utilization = Math.Clamp((totalDelta - idleDelta) * 100d / totalDelta, 0, 100);
                }
            }

            _previousCpuTimes = current;
            _previousCpuTimestamp = timestamp;
        }

        return new CpuMetric(utilization, Environment.ProcessorCount, MetricAvailability.Supported)
        {
            ProcessorQueueLength = ReadCounter(_cpuQueue),
            InterruptPercent = ReadCounter(_cpuInterrupt),
            DpcPercent = ReadCounter(_cpuDpc)
        };
    }

    private MemoryMetric SampleMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            return new MemoryMetric(0, 0, 0, MetricAvailability.Unsupported("GlobalMemoryStatusEx failed."));
        }

        var usedPercent = status.TotalPhysicalMemory == 0
            ? 0
            : (status.TotalPhysicalMemory - status.AvailablePhysicalMemory) * 100d / status.TotalPhysicalMemory;

        return new MemoryMetric(
            status.TotalPhysicalMemory,
            status.AvailablePhysicalMemory,
            Math.Clamp(usedPercent, 0, 100),
            MetricAvailability.Supported)
        {
            CommitBytes = status.TotalPageFile >= status.AvailablePageFile ? status.TotalPageFile - status.AvailablePageFile : null,
            CommitLimitBytes = status.TotalPageFile,
            PageReadsPerSecond = ReadCounter(_pageReads)
        };
    }

    private DiskMetric SampleDisk()
    {
        try
        {
            var activeTime = _diskActiveTime?.NextValue() ?? 0;
            var transfers = _diskTransfers?.NextValue() ?? 0;
            var supported = _diskActiveTime is not null || _diskTransfers is not null;
            return new DiskMetric(activeTime, transfers, supported
                ? MetricAvailability.Supported
                : MetricAvailability.Unsupported("Physical disk performance counters are unavailable."))
            { PhysicalDisks = _physicalDisks.Sample() };
        }
        catch (InvalidOperationException ex)
        {
            return new DiskMetric(0, 0, MetricAvailability.Unsupported(ex.Message));
        }
    }

    private PowerMetric SamplePower()
    {
        if (!GetSystemPowerStatus(out var status))
        {
            return new PowerMetric(PowerSource.Unknown, null, null, MetricAvailability.Unsupported("GetSystemPowerStatus failed."));
        }

        var source = status.ACLineStatus switch
        {
            1 => PowerSource.AC,
            0 => PowerSource.Battery,
            _ => PowerSource.Unknown
        };
        int? batteryPercent = status.BatteryFlag == 128 || status.BatteryLifePercent == 255 ? null : status.BatteryLifePercent;
        TimeSpan? remaining = status.BatteryLifeTime == uint.MaxValue ? null : TimeSpan.FromSeconds(status.BatteryLifeTime);
        var battery = batteryPercent.HasValue ? _batteryTelemetry.Sample() : null;
        return new PowerMetric(source, batteryPercent, remaining, MetricAvailability.Supported,
            BatteryDischargeWatts: source == PowerSource.Battery ? battery?.DischargeWatts : null,
            CpuPackagePowerAvailability: MetricAvailability.Unsupported("No identified CPU package sensor is available."))
        {
            BatteryRemainingCapacityWh = battery?.RemainingWh,
            BatteryFullChargeCapacityWh = battery?.FullChargeWh,
            BatteryDesignCapacityWh = battery?.DesignWh,
            DisplayBrightnessPercent = battery?.BrightnessPercent,
            DisplayActive = battery?.DisplayActive
        };
    }

    private NetworkMetric SampleNetwork(DateTimeOffset timestamp)
    {
        try
        {
            var current = new Dictionary<string, NetworkState>(StringComparer.OrdinalIgnoreCase);
            var receivedPerSecond = 0d;
            var sentPerSecond = 0d;
            var activeInterfaces = 0;
            var interfaces = new List<NetworkInterfaceMetric>();

            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback || networkInterface.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                IPInterfaceStatistics statistics;
                try { statistics = networkInterface.GetIPStatistics(); }
                catch (NetworkInformationException) { continue; }
                var state = new NetworkState(statistics.BytesReceived, statistics.BytesSent, timestamp,
                    statistics.IncomingPacketsWithErrors, statistics.OutgoingPacketsWithErrors,
                    statistics.IncomingPacketsDiscarded, statistics.OutgoingPacketsDiscarded);
                current[networkInterface.Id] = state;
                activeInterfaces++;

                var receiveRate = 0d;
                var sendRate = 0d;
                NetworkState? previousState = null;
                lock (_gate)
                {
                    if (_previousNetwork.TryGetValue(networkInterface.Id, out var previous))
                    {
                        previousState = previous;
                        var seconds = (timestamp - previous.Timestamp).TotalSeconds;
                        if (seconds > 0)
                        {
                            receiveRate = Math.Max(0, statistics.BytesReceived - previous.BytesReceived) / seconds;
                            sendRate = Math.Max(0, statistics.BytesSent - previous.BytesSent) / seconds;
                        }
                    }
                }
                receivedPerSecond += receiveRate;
                sentPerSecond += sendRate;
                interfaces.Add(new(networkInterface.Id, networkInterface.Name, networkInterface.NetworkInterfaceType.ToString(),
                    networkInterface.Speed, receiveRate, sendRate, statistics.IncomingPacketsWithErrors,
                    statistics.OutgoingPacketsWithErrors, statistics.IncomingPacketsDiscarded, statistics.OutgoingPacketsDiscarded)
                {
                    NewReceiveErrors = previousState is { } prior ? Math.Max(0, state.ReceiveErrors - prior.ReceiveErrors) : null,
                    NewSendErrors = previousState is { } priorSend ? Math.Max(0, state.SendErrors - priorSend.SendErrors) : null,
                    NewReceiveDiscards = previousState is { } priorReceiveDiscard ? Math.Max(0, state.ReceiveDiscards - priorReceiveDiscard.ReceiveDiscards) : null,
                    NewSendDiscards = previousState is { } priorSendDiscard ? Math.Max(0, state.SendDiscards - priorSendDiscard.SendDiscards) : null
                });
            }

            lock (_gate)
            {
                _previousNetwork.Clear();
                foreach (var pair in current) _previousNetwork[pair.Key] = pair.Value;
            }

            return new NetworkMetric(receivedPerSecond, sentPerSecond, activeInterfaces, MetricAvailability.Supported)
            { Interfaces = interfaces, TcpRetransmitsPerSecond = SumAvailable(ReadCounter(_tcpV4Retransmits), ReadCounter(_tcpV6Retransmits)) };
        }
        catch (NetworkInformationException ex)
        {
            return new NetworkMetric(0, 0, 0, MetricAvailability.Unsupported(ex.Message));
        }
    }

    private ProcessMetric[] SampleProcesses(
        DateTimeOffset timestamp,
        IReadOnlyDictionary<int, IReadOnlyDictionary<string, double>>? gpuEngines,
        GpuMemorySample? gpuMemory,
        CancellationToken cancellationToken)
    {
        var currentProcesses = new Dictionary<ProcessIdentity, ProcessState>();
        var metrics = new List<ProcessMetric>();
        var windowedProcessIds = GetWindowedProcessIds();

        foreach (var process in Process.GetProcesses())
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (process)
            {
                try
                {
                    var processId = process.Id;
                    var startUtc = TryReadValue(() => process.StartTime.ToUniversalTime());
                    DateTimeOffset? startTime = startUtc is { } value ? new DateTimeOffset(value) : null;
                    var identity = startTime is { } started ? new ProcessIdentity(processId, started) : (ProcessIdentity?)null;
                    ProcessState? previous = null;
                    if (identity is { } key)
                    {
                        lock (_gate)
                        {
                            _previousProcesses.TryGetValue(key, out previous);
                        }
                    }

                    var processorTime = TryReadValue(() => process.TotalProcessorTime);
                    var workingSet = TryReadValue(() => process.WorkingSet64);
                    var privateBytes = TryReadValue(() => process.PrivateMemorySize64);
                    var peakWorkingSet = TryReadValue(() => process.PeakWorkingSet64);
                    var threadCount = TryReadValue(() => process.Threads.Count);
                    var handleCount = TryReadValue(() => process.HandleCount);
                    var executablePath = previous is null ? TryGetExecutablePath(process) : previous.ExecutablePath;
                    IoCounters? io = null;
                    uint? pageFaults = null;
                    var owner = previous?.Owner;
                    var ownerResolved = previous?.OwnerResolved ?? false;

                    var handle = OpenProcess(0x1000, false, processId); // PROCESS_QUERY_LIMITED_INFORMATION
                    if (handle != IntPtr.Zero)
                    {
                        try
                        {
                            if (GetProcessIoCounters(handle, out var counters)) io = counters;
                            var memoryCounters = new ProcessMemoryCounters { cb = (uint)Marshal.SizeOf<ProcessMemoryCounters>() };
                            if (GetProcessMemoryInfo(handle, ref memoryCounters, memoryCounters.cb))
                            {
                                pageFaults = memoryCounters.PageFaultCount;
                                peakWorkingSet ??= (long)memoryCounters.PeakWorkingSetSize;
                            }
                            if (!ownerResolved)
                            {
                                owner = TryGetOwner(handle);
                                ownerResolved = true;
                            }
                        }
                        finally
                        {
                            CloseHandle(handle);
                        }
                    }

                    double? cpuPercent = null;
                    double? readRate = null;
                    double? writeRate = null;
                    if (previous is { } prior)
                    {
                        var elapsed = (timestamp - prior.Timestamp).TotalSeconds;
                        if (elapsed > 0)
                        {
                            if (processorTime is { } currentTime && prior.ProcessorTime is { } oldTime)
                                cpuPercent = Math.Clamp((currentTime - oldTime).TotalSeconds / (elapsed * Environment.ProcessorCount) * 100d, 0, 100);
                            if (io is { } currentIo && prior.Io is { } oldIo)
                            {
                                if (currentIo.ReadTransferCount >= oldIo.ReadTransferCount)
                                    readRate = (currentIo.ReadTransferCount - oldIo.ReadTransferCount) / elapsed;
                                if (currentIo.WriteTransferCount >= oldIo.WriteTransferCount)
                                    writeRate = (currentIo.WriteTransferCount - oldIo.WriteTransferCount) / elapsed;
                            }
                        }
                    }

                    if (identity is { } currentIdentity)
                        currentProcesses[currentIdentity] = new ProcessState(processorTime, io, timestamp, owner, ownerResolved, executablePath);

                    metrics.Add(new ProcessMetric(
                        processId,
                        startTime,
                        TryReadText(() => process.ProcessName) ?? $"PID {processId}",
                        executablePath,
                        cpuPercent ?? 0,
                        (workingSet ?? 0) / 1024d / 1024d,
                        (privateBytes ?? 0) / 1024d / 1024d)
                    {
                        UserName = owner,
                        ReadBytesPerSecond = readRate,
                        WriteBytesPerSecond = writeRate,
                        ThreadCount = threadCount,
                        HandleCount = handleCount,
                        PeakWorkingSetMegabytes = peakWorkingSet / 1024d / 1024d,
                        TotalCpuTime = processorTime,
                        PageFaultCount = pageFaults,
                        CpuAvailable = cpuPercent is not null,
                        WorkingSetAvailable = workingSet is not null,
                        PrivateMemoryAvailable = privateBytes is not null,
                        HasVisibleWindow = windowedProcessIds.Contains(processId),
                        GpuEngines = gpuEngines is null ? null : gpuEngines.TryGetValue(processId, out var engines) ? engines : EmptyGpuEngines,
                        GpuDedicatedMegabytes = gpuMemory?.DedicatedMegabytes(processId),
                        GpuSharedMegabytes = gpuMemory?.SharedMegabytes(processId)
                    });
                }
                catch (InvalidOperationException)
                {
                    // The process exited between enumeration and inspection.
                }
            }
        }

        lock (_gate)
        {
            _previousProcesses.Clear();
            foreach (var pair in currentProcesses)
            {
                _previousProcesses[pair.Key] = pair.Value;
            }
        }

        return metrics
            .OrderByDescending(process => process.CpuAvailable ? process.CpuPercent : -1)
            .ThenByDescending(process => process.WorkingSetMegabytes)
            .ToArray();
    }

    private static T? TryReadValue<T>(Func<T> read) where T : struct
    {
        try { return read(); }
        catch (Win32Exception) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    private static string? TryReadText(Func<string> read)
    {
        try { return read(); }
        catch (Win32Exception) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    private static string? TryGetOwner(IntPtr processHandle)
    {
        if (!OpenProcessToken(processHandle, 0x0008, out var token)) return null; // TOKEN_QUERY
        try
        {
            using var identity = new WindowsIdentity(token);
            return identity.Name;
        }
        catch (System.Security.SecurityException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        finally { CloseHandle(token); }
    }

    private static HashSet<int> GetWindowedProcessIds()
    {
        var processIds = new HashSet<int>();
        EnumWindows((window, _) =>
        {
            if (IsWindowVisible(window) && GetWindow(window, 4) == IntPtr.Zero) // GW_OWNER
            {
                if (GetWindowThreadProcessId(window, out var processId) != 0 && processId != 0)
                    processIds.Add((int)processId);
            }
            return true;
        }, IntPtr.Zero);
        return processIds;
    }

    private static string? TryGetExecutablePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Win32Exception)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static (PerformanceCounter? ActiveTime, PerformanceCounter? Transfers) CreateDiskCounters()
    {
        try
        {
            return (
                new PerformanceCounter("PhysicalDisk", "% Disk Time", "_Total", readOnly: true),
                new PerformanceCounter("PhysicalDisk", "Disk Transfers/sec", "_Total", readOnly: true));
        }
        catch (InvalidOperationException)
        {
            return (null, null);
        }
    }

    private static PerformanceCounter? CreateCounter(string category, string counter, string instance)
    {
        try { return new PerformanceCounter(category, counter, instance, readOnly: true); }
        catch (InvalidOperationException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static double? ReadCounter(PerformanceCounter? counter)
    {
        if (counter is null) return null;
        try { var value = counter.NextValue(); return double.IsFinite(value) && value >= 0 ? value : null; }
        catch (InvalidOperationException) { return null; }
    }

    private static double? SumAvailable(double? first, double? second) => first is null && second is null ? null : (first ?? 0) + (second ?? 0);

    private static ulong FileTimeToUInt64(System.Runtime.InteropServices.ComTypes.FILETIME fileTime)
        => ((ulong)(uint)fileTime.dwHighDateTime << 32) | (uint)fileTime.dwLowDateTime;

    private readonly record struct CpuTimes(ulong Idle, ulong Kernel, ulong User)
    {
        public ulong Total => Kernel + User;
    }

    private readonly record struct ProcessIdentity(int ProcessId, DateTimeOffset StartTime);

    private sealed record ProcessState(TimeSpan? ProcessorTime, IoCounters? Io, DateTimeOffset Timestamp, string? Owner, bool OwnerResolved, string? ExecutablePath);

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public uint cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
    }

    private readonly record struct NetworkState(long BytesReceived, long BytesSent, DateTimeOffset Timestamp,
        long ReceiveErrors, long SendErrors, long ReceiveDiscards, long SendDiscards);

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters ioCounters);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(IntPtr process, ref ProcessMemoryCounters counters, uint size);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(
        out System.Runtime.InteropServices.ComTypes.FILETIME idleTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME kernelTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysicalMemory;
        public ulong AvailablePhysicalMemory;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte Reserved;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }
}

