using Skald.Core.Models;

namespace Skald.Triage;

public enum DeviceClass { Network, Bluetooth, Audio, Camera, Display, Input }

// One Plug and Play device as Windows' device database lists it, including devices it remembers but that are not present now.
// ProblemCode is null when the device's status could not be read, which is not the same as "no problem" (0).
public sealed record DeviceRecord(string InstanceId, string Name, DeviceClass Class, string ClassName, bool? Present, uint? ProblemCode,
    string? Service, bool BuiltIn)
{
    public bool Disabled => Present == true && ProblemCode == 22;
    public bool HasProblem => Present == true && ProblemCode is > 0 and not 22;
    public string Enumerator => InstanceId.Split('\\')[0].ToUpperInvariant();

    // Enumerator plus vendor and device (PCI\VEN_8086&DEV_2725, USB\VID_8087&PID_0026). The same part keeps it when a firmware
    // or BIOS update changes its revision or subsystem and Windows lists it under a new instance ID.
    public string HardwareKey => HardwareKeyOf(InstanceId);

    public static string HardwareKeyOf(string instanceId)
    {
        var segments = instanceId.Split('\\');
        if (segments.Length < 2) return instanceId.ToUpperInvariant();
        var parts = segments[1].Split('&').Where(part => part.StartsWith("VEN_", StringComparison.OrdinalIgnoreCase) || part.StartsWith("DEV_", StringComparison.OrdinalIgnoreCase)
            || part.StartsWith("VID_", StringComparison.OrdinalIgnoreCase) || part.StartsWith("PID_", StringComparison.OrdinalIgnoreCase)).ToArray();
        return (segments[0] + "\\" + (parts.Length > 0 ? string.Join('&', parts) : segments[1])).ToUpperInvariant();
    }
}

// Coverage of one event log: whether it could be read and how far back it holds records. A log that starts after the scan
// window began cannot show older events, so a clean result only covers the part it holds.
public sealed record LogCoverage(string Log, bool Readable, DateTimeOffset? OldestRecord, bool CapReached = false);

public sealed record DeviceEvidence(DateTimeOffset CollectedAt, IReadOnlyList<DeviceRecord> Devices, IReadOnlyList<ReliabilityEvent> Events,
    IReadOnlyList<string> SourceStatus)
{
    // Classes whose device list could not be read (or was read only in part). Their checks say "could not check".
    public IReadOnlySet<DeviceClass> UnreadableClasses { get; init; } = new HashSet<DeviceClass>();
    public IReadOnlyList<LogCoverage> Logs { get; init; } = [];
    public LogCoverage? Log(string name) => Logs.FirstOrDefault(log => log.Log.Equals(name, StringComparison.OrdinalIgnoreCase));
}

public sealed record ServiceReading(string Name, bool Installed, string? State, string? StartMode)
{
    public bool Running => State?.Equals("Running", StringComparison.OrdinalIgnoreCase) == true;
}

public enum RadioSwitch { Unknown, On, Off }

public sealed record RadioReading(string Adapter, RadioSwitch Software, RadioSwitch Hardware);

// Windows settings that can make a working device look broken. A null reading means it could not be read; the reason is in SourceStatus.
public sealed record DeviceSettings(DateTimeOffset CollectedAt, IReadOnlyList<string> SourceStatus)
{
    public bool? AirplaneMode { get; init; }
    public IReadOnlyList<ServiceReading>? Services { get; init; }
    public IReadOnlyList<RadioReading>? WifiRadios { get; init; }
    public IReadOnlyList<RadioReading>? BluetoothRadios { get; init; }
    // Set when this process is not the signed-in user in their own session (SYSTEM, session 0, another account), so per-user
    // settings (privacy, sound, accessibility) would be someone else's. Those readings are then null and say why.
    public string? SessionProblem { get; init; }
    // Set in a Remote Desktop session, where sound devices and display layout are the remote session's, not the computer's own.
    public string? RemoteSession { get; init; }
    public AudioReading? Audio { get; init; }
    public PrivacyReading? Microphone { get; init; }
    public PrivacyReading? Camera { get; init; }
    public DisplayReading? Display { get; init; }
    public InputReading? Input { get; init; }
    // True when device-management policy (Policy CSP Connectivity/AllowBluetooth = 0) disallows Bluetooth; false when not set or allowed.
    public bool? BluetoothDisallowedByPolicy { get; init; }
    public ServiceReading? Service(string name) => Services?.FirstOrDefault(service => service.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

// A default sound device: its name and, where Windows exposes them, mute and volume (0 to 1). Null values could not be read.
public sealed record AudioEndpointReading(string Name, bool? Muted, double? Volume);

// Sound devices as Windows' audio service lists them. A null default means Windows has no default device of that kind.
public sealed record AudioReading(AudioEndpointReading? DefaultPlayback, AudioEndpointReading? DefaultRecording,
    int ActivePlayback, int DisabledPlayback, int UnpluggedPlayback, int ActiveRecording, int DisabledRecording);

// Windows privacy access for one capability (webcam, microphone). Null means the value could not be read.
public sealed record PrivacyReading(string Capability)
{
    public bool? DeviceDenied { get; init; }        // Settings > Privacy: "<Camera> access" for this device (all users)
    public bool? UserDenied { get; init; }          // "Let apps access your <camera>"
    public bool? DesktopAppsDenied { get; init; }   // "Let desktop apps access your <camera>": Zoom, browsers (Google Meet), classic Teams
    public IReadOnlyList<string>? DeniedApps { get; init; }  // Watched Store apps the user turned off (new Teams, Camera, Sound Recorder)
    public int? AppPolicy { get; init; }            // Group Policy LetAppsAccess<Capability>: 0 user in control, 1 force allow, 2 force deny
    public IReadOnlyList<string> PolicyDeniedApps { get; init; } = [];
    public bool DisabledByPolicy { get; init; }     // Camera only: Group Policy "Allow use of camera" = 0
    public IReadOnlyList<string> InUseBy { get; init; } = [];  // Apps Windows records as using it right now (file or app name only)
}

public enum DisplayTopology { Unknown, PcScreenOnly, Duplicate, Extend, SecondScreenOnly }

// The signed-in session's display layout (the Win+P choice) and how many displays Windows is drawing on.
public sealed record DisplayReading(DisplayTopology Topology, int ActiveDisplays);

// Accessibility and touchpad switches that make a keyboard, mouse or touchpad look dead. Null values could not be read.
public sealed record InputReading(bool? FilterKeys, bool? MouseKeys, bool? TouchpadEnabled, bool? TouchpadOffWithMouse);

public enum UpdateLean { NothingPointsHere, PointsHere, CouldNotAssess }

// What the evidence says about updating one driver or the BIOS. Descriptive only: it states what was and was not found.
public sealed record UpdateNote(string Subject, UpdateLean Lean, string Text);
