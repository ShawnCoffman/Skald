namespace Skald.Triage;

// Which Windows device classes make up each checked device class, and which of their devices are real hardware rather than
// software adapters (WAN miniports, Wi-Fi Direct virtual adapters, Bluetooth protocol enumerators and the like).
// NotProven is the check's standing limit: what reading Windows' records cannot tell about this kind of device.
public sealed record DeviceClassSpec(DeviceClass Class, string Id, string Title, string Noun, IReadOnlyList<Guid> ClassGuids,
    IReadOnlyList<string> SnapshotClasses, IReadOnlyList<string> HardwareEnumerators, string NotProven)
{
    // Accessories hang off the class's hardware (paired Bluetooth devices): a problem code on one is a minor finding about that
    // accessory, and they are never expected to stay present. ExtraProviders are port or class drivers that log for the hardware
    // under their own name; only their errors count, because their warnings are mostly about remote devices.
    public IReadOnlyList<string> AccessoryEnumerators { get; init; } = [];
    public IReadOnlyList<string> ExtraProviders { get; init; } = [];
    // Broad Windows classes (System, HIDClass, Image) contribute only devices run by these driver services, e.g. the HD Audio controller.
    public IReadOnlyDictionary<Guid, IReadOnlyList<string>> ServiceFilters { get; init; } = new Dictionary<Guid, IReadOnlyList<string>>();

    // Driver classes (snapshot names) shown in this check for reference only; their evidence belongs to another check (the
    // graphics adapter's to Graphics driver and GPU), so they are compared with the baseline but never counted here.
    public IReadOnlyList<string> ReferenceClasses { get; init; } = [];

    public bool IsHardware(string instanceId)
    {
        var enumerator = instanceId.Split('\\')[0];
        return HardwareEnumerators.Contains(DeviceCatalog.AnyHardware)
            ? !DeviceCatalog.SoftwareEnumerators.Contains(enumerator, StringComparer.OrdinalIgnoreCase) && !enumerator.StartsWith('{')
            : HardwareEnumerators.Contains(enumerator, StringComparer.OrdinalIgnoreCase);
    }
    public bool IsAccessory(string instanceId) => AccessoryEnumerators.Contains(instanceId.Split('\\')[0], StringComparer.OrdinalIgnoreCase);
}

public static class DeviceCatalog
{
    // Buses inside the computer. A device on one of these that is in the baseline but no longer listed at all has left the machine's
    // own device tree (removed, failed, or turned off in BIOS setup); a USB device that is gone may simply be unplugged.
    public static readonly IReadOnlyList<string> InternalEnumerators = ["PCI", "ACPI", "HDAUDIO", "INTELAUDIO", "SD"];

    // Devices built into the computer share this container ID; external devices get their own.
    public static readonly Guid LocalMachineContainer = new("00000000-0000-0000-ffff-ffffffffffff");

    private static readonly Guid SystemClass = new("4d36e97d-e325-11ce-bfc1-08002be10318");
    private static readonly Guid ImageClass = new("6bdd1fc6-810f-11d0-bec7-08002be2092f");
    private static readonly Guid HidClass = new("745a17a0-74d3-11d0-b6fe-00a0c90f57da");

    // In HardwareEnumerators: any enumerator except the software ones (virtual cameras, Windows Studio Effects, remote-session devices).
    internal const string AnyHardware = "*";
    internal static readonly IReadOnlyList<string> SoftwareEnumerators = ["SWD", "SW", "ROOT", "TERMINPUT_BUS", "HTREE"];

    public static readonly IReadOnlyList<DeviceClassSpec> Classes =
    [
        new(DeviceClass.Network, "devices.network", "Network adapters (Wi-Fi and Ethernet hardware)", "network adapter",
            [new("4d36e972-e325-11ce-bfc1-08002be10318")], ["NET"], ["PCI", "USB", "SD"],
            "Reads Windows' device status, driver and event records. It does not test the radio, antenna or cable, and does not check the network itself: connectivity, Wi-Fi signal, DNS and VPN are not covered."),
        new(DeviceClass.Bluetooth, "devices.bluetooth", "Bluetooth radio", "Bluetooth radio",
            [new("e0cbf06c-cd8b-4647-bb8a-263b43f0f974")], ["BLUETOOTH"], ["USB", "PCI", "SD", "ACPI"],
            "Reads Windows' device status, driver and event records for the Bluetooth radio and paired devices. It does not test pairing, range or audio quality, and a paired device's own battery and firmware are not checked.")
        {
            AccessoryEnumerators = ["BTHENUM", "BTHLEDEVICE", "BTHLE"],
            ExtraProviders = ["BTHPORT"]
        },
        new(DeviceClass.Audio, "devices.audio", "Audio devices (sound hardware and USB audio)", "audio device",
            [new("4d36e96c-e325-11ce-bfc1-08002be10318"), SystemClass], ["MEDIA"], ["HDAUDIO", "INTELAUDIO", "USB", "PCI", "ACPI"],
            "Reads Windows' device status, driver and event records for audio hardware. It does not play or record sound, and cannot tell a failed speaker, jack, headset or cable from a working one.")
        {
            ServiceFilters = new Dictionary<Guid, IReadOnlyList<string>> { [SystemClass] = ["HDAudBus", "IntcAudioBus", "IntcOED"] }
        },
        // Built-in cameras sit on several buses (USB, Intel IPU/MIPI, ACPI sensors), so anything but a software camera counts.
        new(DeviceClass.Camera, "devices.camera", "Cameras", "camera",
            [new("ca3e7ab9-b4c3-4ae6-8251-579ef933890f"), ImageClass], ["CAMERA"], [AnyHardware],
            "Reads Windows' device status, driver and event records for cameras. It never opens the camera, so it cannot tell whether the camera delivers a picture, and a lens cover or privacy shutter is invisible to Windows.")
        {
            ServiceFilters = new Dictionary<Guid, IReadOnlyList<string>> { [ImageClass] = ["usbvideo"] }
        },
        new(DeviceClass.Display, "devices.display", "Monitors and the built-in screen", "monitor",
            [new("4d36e96e-e325-11ce-bfc1-08002be10318")], ["MONITOR"], ["DISPLAY"],
            "Reads Windows' device status and driver records for monitors. It cannot see a monitor's power, cable, input selection or panel damage; graphics adapter evidence is under Graphics driver and GPU.")
        {
            ReferenceClasses = ["DISPLAY"]
        },
        new(DeviceClass.Input, "devices.input", "Keyboards, mice and touchpads", "input device",
            [new("4d36e96b-e325-11ce-bfc1-08002be10318"), new("4d36e96f-e325-11ce-bfc1-08002be10318"), HidClass], ["KEYBOARD", "MOUSE"], ["HID", "ACPI", "USB"],
            "Reads Windows' device status, driver and event records for keyboards, mice and touchpads. It does not test keys or tracking; a worn key, a flat battery or a dirty sensor leaves no Windows record.")
        {
            ServiceFilters = new Dictionary<Guid, IReadOnlyList<string>> { [HidClass] = ["hidi2c"] }
        }
    ];

    public static DeviceClassSpec Spec(DeviceClass deviceClass) => Classes.Single(spec => spec.Class == deviceClass);

    // For the Sources list when a class returned no devices to take the name from.
    public static string ClassLabel(Guid classGuid)
        => classGuid == SystemClass ? "System, audio controllers only" : classGuid == ImageClass ? "Image, USB video only" : classGuid == HidClass ? "HIDClass, I2C touchpads only" : classGuid.ToString();

    public static bool IsWireless(string name) => name.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) || name.Contains("WiFi", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Wireless", StringComparison.OrdinalIgnoreCase) || name.Contains("WLAN", StringComparison.OrdinalIgnoreCase) || name.Contains("802.11", StringComparison.Ordinal);
}
