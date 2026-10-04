using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Skald.Triage;

// Windows settings that can make a working device look broken. Read-only: nothing here turns a radio, service or device on or off.
public static class DeviceSettingsCollector
{
    private static readonly string[] ServiceNames = ["WlanSvc", "bthserv", "Audiosrv", "AudioEndpointBuilder", "FrameServer"];

    public static Task<DeviceSettings> CollectAsync() => Task.Run(Collect);

    private static DeviceSettings Collect()
    {
        var status = new List<string>();
        var (sessionProblem, remote) = SessionContext.Read();
        if (sessionProblem is not null) status.Add("Per-user settings: not read. " + sessionProblem);
        if (remote is not null) status.Add("Sound devices and display layout: not read. " + remote);
        return new(DateTimeOffset.Now, status)
        {
            SessionProblem = sessionProblem,
            RemoteSession = remote,
            Audio = sessionProblem is null && remote is null ? AudioEndpointReader.Read(status) : null,
            Microphone = PrivacyReader.Read("microphone", "LetAppsAccessMicrophone", sessionProblem, status),
            Camera = PrivacyReader.Read("webcam", "LetAppsAccessCamera", sessionProblem, status),
            Display = sessionProblem is null && remote is null ? DisplayLayoutReader.Read(status) : null,
            Input = sessionProblem is null ? InputSettingsReader.Read(status) : null,
            AirplaneMode = ReadAirplaneMode(status),
            Services = ReadServices(status),
            WifiRadios = WlanRadioReader.Read(status),
            BluetoothRadios = ReadBluetoothRadios(status),
            BluetoothDisallowedByPolicy = ReadBluetoothPolicy(status)
        };
    }

    // Windows' radio manager: the same on/off state as the Bluetooth toggle in Settings. "Disabled" means the radio is held off
    // by firmware or a hardware switch and cannot be turned on from Windows.
    private static List<RadioReading>? ReadBluetoothRadios(List<string> status)
    {
        try
        {
            var task = Windows.Devices.Radios.Radio.GetRadiosAsync().AsTask();
            if (!task.Wait(TimeSpan.FromSeconds(8))) { status.Add("Bluetooth radio state: timed out"); return null; }
            var radios = task.Result.Where(radio => radio.Kind == Windows.Devices.Radios.RadioKind.Bluetooth).Select(radio => radio.State switch
            {
                Windows.Devices.Radios.RadioState.On => new RadioReading(radio.Name, RadioSwitch.On, RadioSwitch.On),
                Windows.Devices.Radios.RadioState.Off => new RadioReading(radio.Name, RadioSwitch.Off, RadioSwitch.Unknown),
                Windows.Devices.Radios.RadioState.Disabled => new RadioReading(radio.Name, RadioSwitch.Unknown, RadioSwitch.Off),
                _ => new RadioReading(radio.Name, RadioSwitch.Unknown, RadioSwitch.Unknown)
            }).ToList();
            status.Add($"Bluetooth radio state: {radios.Count} radio(s)" + string.Concat(radios.Select(radio => $" · {radio.Adapter} {(radio.Hardware == RadioSwitch.Off ? "held off" : radio.Software.ToString().ToLowerInvariant())}")));
            return radios;
        }
        catch (Exception ex) when (ex is AggregateException or COMException or UnauthorizedAccessException or InvalidOperationException or TypeLoadException)
        {
            status.Add($"Bluetooth radio state: unavailable ({(ex is AggregateException aggregate ? aggregate.InnerException?.GetType().Name : ex.GetType().Name)})");
            return null;
        }
    }

    // Device-management policy (Intune or another MDM): Connectivity/AllowBluetooth, 0 = disallow, 2 = allow. Absent means not set.
    private static bool? ReadBluetoothPolicy(List<string> status)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\PolicyManager\current\device\Connectivity");
            return key?.GetValue("AllowBluetooth") is int value && value == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            status.Add($"Bluetooth policy: unavailable ({ex.GetType().Name})");
            return null;
        }
    }

    // Windows records the airplane-mode switch here (1 = on). It is machine-wide, so it is the same for every user.
    private static bool? ReadAirplaneMode(List<string> status)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\RadioManagement\SystemRadioState");
            if (key?.GetValue(null) is int value) return value != 0;
            status.Add("Airplane mode: not recorded by Windows on this machine");
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        { status.Add($"Airplane mode: unavailable ({ex.GetType().Name})"); }
        return null;
    }

    private static List<ServiceReading>? ReadServices(List<string> status)
    {
        var found = new Dictionary<string, ServiceReading>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var filter = string.Join(" OR ", ServiceNames.Select(name => $"Name='{name}'"));
            using var searcher = new ManagementObjectSearcher(HardwareInventoryCollector.CimNamespace, $"SELECT Name,State,StartMode FROM Win32_Service WHERE {filter}",
                new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(8) });
            using var rows = searcher.Get();
            foreach (ManagementBaseObject row in rows)
                using (row)
                    if (HardwareInventoryCollector.Value(row, "Name") is { } name)
                        found[name] = new(name, true, HardwareInventoryCollector.Value(row, "State"), HardwareInventoryCollector.Value(row, "StartMode"));
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException or InvalidOperationException)
        {
            status.Add($"Services: unavailable ({ex.GetType().Name})");
            return null;
        }
        // A service that the query did not return is not installed (for example WLAN AutoConfig on a server without the wireless feature).
        var services = ServiceNames.Select(name => found.TryGetValue(name, out var reading) ? reading : new ServiceReading(name, false, null, null)).ToList();
        status.Add("Services: " + string.Join(", ", services.Select(service => service.Installed ? $"{service.Name} {service.State} ({service.StartMode})" : $"{service.Name} not installed")));
        return services;
    }
}

// Wi-Fi radio switches through the Native Wifi API: software (Windows' Wi-Fi toggle) and hardware (a physical switch or function key).
internal static class WlanRadioReader
{
    public static List<RadioReading>? Read(List<string> status)
    {
        IntPtr handle = IntPtr.Zero, list = IntPtr.Zero;
        try
        {
            var result = WlanOpenHandle(2, IntPtr.Zero, out _, out handle);
            if (result != 0)
            {
                status.Add(result == ErrorServiceNotActive ? "Wi-Fi radio state: unavailable (WLAN AutoConfig is not running)" : $"Wi-Fi radio state: unavailable (error {result})");
                return null;
            }
            result = WlanEnumInterfaces(handle, IntPtr.Zero, out list);
            if (result != 0) { status.Add($"Wi-Fi radio state: interfaces unavailable (error {result})"); return null; }
            var radios = new List<RadioReading>();
            var count = Marshal.ReadInt32(list);
            for (var i = 0; i < count; i++)
            {
                // WLAN_INTERFACE_INFO_LIST: two DWORDs, then WLAN_INTERFACE_INFO entries of GUID + WCHAR[256] + state (532 bytes).
                var item = list + 8 + i * 532;
                var id = Marshal.PtrToStructure<Guid>(item);
                var description = Marshal.PtrToStringUni(item + 16, 256).Split('\0')[0];
                radios.Add(Radio(handle, id, description));
            }
            status.Add($"Wi-Fi radio state: {radios.Count} wireless interface(s)");
            return radios;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            status.Add("Wi-Fi radio state: unavailable (Native Wifi is not installed)");
            return null;
        }
        finally
        {
            if (list != IntPtr.Zero) WlanFreeMemory(list);
            if (handle != IntPtr.Zero) _ = WlanCloseHandle(handle, IntPtr.Zero);
        }
    }

    private static RadioReading Radio(IntPtr handle, Guid id, string description)
    {
        if (WlanQueryInterface(handle, ref id, RadioStateOpcode, IntPtr.Zero, out _, out var data, out _) != 0 || data == IntPtr.Zero)
            return new(description, RadioSwitch.Unknown, RadioSwitch.Unknown);
        try
        {
            // WLAN_RADIO_STATE: a count, then WLAN_PHY_RADIO_STATE entries of phy index, software state, hardware state (DWORDs; 1 on, 2 off).
            var phys = Math.Min(Marshal.ReadInt32(data), 64);
            var software = new List<int>();
            var hardware = new List<int>();
            for (var i = 0; i < phys; i++)
            {
                software.Add(Marshal.ReadInt32(data, 4 + i * 12 + 4));
                hardware.Add(Marshal.ReadInt32(data, 4 + i * 12 + 8));
            }
            return new(description, Combine(software), Combine(hardware));
        }
        finally { WlanFreeMemory(data); }
    }

    // A radio is on when any of its PHYs is on, off only when every PHY reports off.
    private static RadioSwitch Combine(List<int> states)
        => states.Contains(1) ? RadioSwitch.On : states.Count > 0 && states.All(state => state == 2) ? RadioSwitch.Off : RadioSwitch.Unknown;

    private const int RadioStateOpcode = 4;
    private const uint ErrorServiceNotActive = 1062;

    [DllImport("wlanapi.dll", ExactSpelling = true)]
    private static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr handle);

    [DllImport("wlanapi.dll", ExactSpelling = true)]
    private static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);

    [DllImport("wlanapi.dll", ExactSpelling = true)]
    private static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr interfaceList);

    [DllImport("wlanapi.dll", ExactSpelling = true)]
    private static extern uint WlanQueryInterface(IntPtr handle, ref Guid interfaceGuid, int opcode, IntPtr reserved, out uint dataSize, out IntPtr data, out int valueType);

    [DllImport("wlanapi.dll", ExactSpelling = true)]
    private static extern void WlanFreeMemory(IntPtr memory);
}
