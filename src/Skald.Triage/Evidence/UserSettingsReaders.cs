using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace Skald.Triage;

// Whether this process sees the signed-in user's own settings. A tech who elevates with a separate admin account, or a remote
// tool running as SYSTEM, would otherwise read that account's sound, privacy and accessibility settings and report them as the user's.
internal static class SessionContext
{
    public static (string? SessionProblem, string? RemoteSession) Read()
    {
        string? remote = null;
        try
        {
            if (GetSystemMetrics(SmRemoteSession) != 0)
                remote = "Skald is running in a Remote Desktop session, so sound devices and display layout are the remote session's, not the computer's own.";
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.IsSystem) return ("Skald is running as SYSTEM, so the signed-in user's settings are not visible to it.", remote);
            var session = Process.GetCurrentProcess().SessionId;
            if (session == 0) return ("Skald is running in session 0 (a service or remote tool), not in the signed-in user's session.", remote);
            var signedIn = SessionUser(session);
            var account = identity.Name.Split('\\')[^1];
            if (signedIn is null) return ("The user signed in to this session could not be identified, so per-user settings may not be theirs.", remote);
            if (!signedIn.Equals(account, StringComparison.OrdinalIgnoreCase))
                return ("Skald is running under a different account than the user signed in to this session (for example \"Run as administrator\" with an admin account), so per-user settings would be that account's. Run it as the signed-in user to read them.", remote);
            return (null, remote);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {
            return ($"The signed-in session could not be identified ({ex.GetType().Name}), so per-user settings were not read.", remote);
        }
    }

    private static string? SessionUser(int session)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, session, WtsUserName, out var buffer, out _) || buffer == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(buffer) is { Length: > 0 } name ? name : null; }
        finally { WTSFreeMemory(buffer); }
    }

    private const int SmRemoteSession = 0x1000;
    private const int WtsUserName = 5;

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetSystemMetrics(int index);

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll", ExactSpelling = true)]
    private static extern void WTSFreeMemory(IntPtr memory);
}

// Windows' privacy switches for a capability (webcam, microphone), read from the same keys the Settings app writes.
internal static class PrivacyReader
{
    private const string Store = @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";

    // The Store apps this team supports. Zoom, Google Meet (in a browser) and classic Teams are desktop apps, governed by the desktop-apps switch.
    internal static readonly IReadOnlyDictionary<string, string> WatchedApps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["MSTeams_8wekyb3d8bbwe"] = "Microsoft Teams",
        ["Microsoft.WindowsCamera_8wekyb3d8bbwe"] = "Windows Camera app",
        ["Microsoft.WindowsSoundRecorder_8wekyb3d8bbwe"] = "Sound Recorder"
    };

    public static PrivacyReading? Read(string capability, string policyName, string? sessionProblem, List<string> status)
    {
        try
        {
            var reading = new PrivacyReading(capability) { DeviceDenied = Denied(Registry.LocalMachine, Store + capability) };
            using (var policy = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy"))
            {
                var denyList = policy?.GetValue(policyName + "_ForceDenyTheseApps") switch
                {
                    string[] many => many,
                    string one => one.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    _ => []
                };
                reading = reading with
                {
                    AppPolicy = policy?.GetValue(policyName) is int value ? value : null,
                    PolicyDeniedApps = denyList.Where(WatchedApps.ContainsKey).Select(app => WatchedApps[app]).ToArray()
                };
            }
            if (capability == "webcam")
                using (var camera = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Camera"))
                    reading = reading with { DisabledByPolicy = camera?.GetValue("AllowCamera") is int allowed && allowed == 0 };
            if (sessionProblem is not null)
            {
                status.Add($"{capability} privacy: per-user switches not read ({sessionProblem})");
                return reading;
            }
            var apps = WatchedApps.Keys.Where(app => Denied(Registry.CurrentUser, Store + capability + "\\" + app) == true).Select(app => WatchedApps[app]).ToArray();
            return reading with
            {
                UserDenied = Denied(Registry.CurrentUser, Store + capability) ?? false,
                DesktopAppsDenied = Denied(Registry.CurrentUser, Store + capability + @"\NonPackaged") ?? false,
                DeniedApps = apps,
                InUseBy = InUse(Store + capability)
            };
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            status.Add($"{capability} privacy: unavailable ({ex.GetType().Name})");
            return null;
        }
    }

    // Windows records when each app started and stopped using the capability; a start with no stop means it is in use now. Only the
    // app's name is kept: desktop apps are keyed by their full path (which holds the user name), so just the file name is reported.
    private static string[] InUse(string path)
    {
        var names = new List<string>();
        using var root = Registry.CurrentUser.OpenSubKey(path);
        if (root is null) return [];
        using var desktop = root.OpenSubKey("NonPackaged");
        foreach (var parent in new[] { root, desktop }.OfType<RegistryKey>())
            foreach (var name in parent.GetSubKeyNames().Where(name => !name.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase)).Take(512))
            {
                using var app = parent.OpenSubKey(name);
                if (app?.GetValue("LastUsedTimeStart") is long start && start > 0 && app.GetValue("LastUsedTimeStop") is long stop && stop == 0)
                    names.Add(WatchedApps.TryGetValue(name, out var known) ? known : parent == root ? name.Split('_')[0] : name.Split('#')[^1]);
            }
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // Only an explicit "Deny" blocks. "Prompt" or a missing value is not a block.
    private static bool? Denied(RegistryKey hive, string path)
    {
        using var key = hive.OpenSubKey(path);
        return key?.GetValue("Value") is string value ? value.Equals("Deny", StringComparison.OrdinalIgnoreCase) : null;
    }
}

// Default sound devices through Windows' Core Audio API, read-only: the volume interface is declared only as far as GetMute, and
// its setters are placeholders that are never called.
internal static class AudioEndpointReader
{
    public static AudioReading? Read(List<string> status)
    {
        IMMDeviceEnumerator? enumerator = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            var playback = Count(enumerator, DataFlowRender);
            var recording = Count(enumerator, DataFlowCapture);
            var reading = new AudioReading(Default(enumerator, DataFlowRender, status), Default(enumerator, DataFlowCapture, status),
                playback.Active, playback.Disabled, playback.Unplugged, recording.Active, recording.Disabled);
            status.Add($"Sound devices: {reading.ActivePlayback} active playback ({reading.DisabledPlayback} disabled, {reading.UnpluggedPlayback} unplugged), {reading.ActiveRecording} active recording");
            return reading;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException)
        {
            status.Add($"Sound devices: unavailable ({ex.GetType().Name} 0x{ex.HResult:X8})");
            return null;
        }
        finally { if (enumerator is not null) Marshal.ReleaseComObject(enumerator); }
    }

    private static (int Active, int Disabled, int Unplugged) Count(IMMDeviceEnumerator enumerator, int flow)
    {
        Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(flow, StateActive | StateDisabled | StateUnplugged, out var collection));
        try
        {
            Marshal.ThrowExceptionForHR(collection.GetCount(out var count));
            int active = 0, disabled = 0, unplugged = 0;
            for (uint i = 0; i < count; i++)
            {
                if (collection.Item(i, out var device) != 0) continue;
                try
                {
                    if (device.GetState(out var state) != 0) continue;
                    if (state == StateActive) active++;
                    else if (state == StateDisabled) disabled++;
                    else if (state == StateUnplugged) unplugged++;
                }
                finally { Marshal.ReleaseComObject(device); }
            }
            return (active, disabled, unplugged);
        }
        finally { Marshal.ReleaseComObject(collection); }
    }

    // Null when Windows has no default device of this kind (E_NOTFOUND); any other failure is thrown so the reading becomes "could not read".
    private static AudioEndpointReading? Default(IMMDeviceEnumerator enumerator, int flow, List<string> status)
    {
        var result = enumerator.GetDefaultAudioEndpoint(flow, RoleConsole, out var device);
        if (result == ENotFound) return null;
        Marshal.ThrowExceptionForHR(result);
        try
        {
            var name = FriendlyName(device) ?? (flow == DataFlowRender ? "Default playback device" : "Default recording device");
            bool? muted = null;
            double? volume = null;
            var iid = typeof(IAudioEndpointVolume).GUID;
            if (device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out var activated) == 0 && activated is IAudioEndpointVolume control)
            {
                try
                {
                    if (control.GetMute(out var mute) == 0) muted = mute;
                    if (control.GetMasterVolumeLevelScalar(out var level) == 0) volume = level;
                }
                finally { Marshal.ReleaseComObject(control); }
            }
            else status.Add($"Sound devices: volume and mute of {name} could not be read");
            return new(name, muted, volume);
        }
        finally { Marshal.ReleaseComObject(device); }
    }

    private static string? FriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(StgmRead, out var store) != 0) return null;
        try
        {
            var key = new PropertyKey { FormatId = new("a45c254e-df1c-4efd-8020-67d146a850e0"), PropertyId = 14 };
            if (store.GetValue(ref key, out var value) != 0) return null;
            try { return value.Type == VtLpwstr ? Marshal.PtrToStringUni(value.Value) : null; }
            finally { _ = PropVariantClear(ref value); }
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    private const int DataFlowRender = 0, DataFlowCapture = 1, RoleConsole = 0;
    private const uint StateActive = 1, StateDisabled = 2, StateUnplugged = 8, ClsctxAll = 23, StgmRead = 0;
    private const int ENotFound = unchecked((int)0x80070490);
    private const ushort VtLpwstr = 31;

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    // PROPVARIANT is 24 bytes on x64 (16 on x86): a type, three reserved words, then a 16-byte (8-byte) union.
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort Type;
        public ushort Reserved1, Reserved2, Reserved3;
        public IntPtr Value;
        public IntPtr Value2;
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int PropVariantClear(ref PropVariant value);

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object activated);
        [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }

    // Vtable order matters, so the methods before GetMute are declared; the setters are placeholders and are never called.
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int NotUsedRegisterControlChangeNotify();
        [PreserveSig] int NotUsedUnregisterControlChangeNotify();
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int NotUsedSetMasterVolumeLevel();
        [PreserveSig] int NotUsedSetMasterVolumeLevelScalar();
        [PreserveSig] int GetMasterVolumeLevel(out float decibels);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int NotUsedSetChannelVolumeLevel();
        [PreserveSig] int NotUsedSetChannelVolumeLevelScalar();
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float decibels);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int NotUsedSetMute();
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
    }
}

// The session's display layout through QueryDisplayConfig, read-only: the Win+P choice and how many displays are in use.
internal static class DisplayLayoutReader
{
    public static DisplayReading? Read(List<string> status)
    {
        try
        {
            var active = Paths(QdcOnlyActivePaths, out _);
            _ = Paths(QdcDatabaseCurrent, out var topology);
            var reading = new DisplayReading(topology switch
            {
                1 => DisplayTopology.PcScreenOnly, 2 => DisplayTopology.Duplicate, 4 => DisplayTopology.Extend, 8 => DisplayTopology.SecondScreenOnly, _ => DisplayTopology.Unknown
            }, active);
            status.Add($"Display layout: {reading.Topology}, {reading.ActiveDisplays} display(s) in use");
            return reading;
        }
        catch (Win32Exception ex)
        {
            status.Add($"Display layout: unavailable (error {ex.NativeErrorCode})");
            return null;
        }
    }

    private static int Paths(uint flags, out uint topology)
    {
        topology = 0;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var result = GetDisplayConfigBufferSizes(flags, out var pathCount, out var modeCount);
            if (result != 0) throw new Win32Exception(result);
            // DISPLAYCONFIG_PATH_INFO is 72 bytes and DISPLAYCONFIG_MODE_INFO 64; the contents are not needed, only the count and topology.
            var paths = Marshal.AllocHGlobal((int)Math.Max(1, pathCount) * 72);
            var modes = Marshal.AllocHGlobal((int)Math.Max(1, modeCount) * 64);
            try
            {
                result = (flags & QdcDatabaseCurrent) != 0
                    ? QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, out topology)
                    : QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
                if (result == ErrorInsufficientBuffer) continue;   // the layout changed between the two calls
                if (result != 0) throw new Win32Exception(result);
                return (int)pathCount;
            }
            finally
            {
                Marshal.FreeHGlobal(paths);
                Marshal.FreeHGlobal(modes);
            }
        }
        throw new Win32Exception(ErrorInsufficientBuffer);
    }

    private const uint QdcOnlyActivePaths = 0x2, QdcDatabaseCurrent = 0x4;
    private const int ErrorInsufficientBuffer = 122;

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, IntPtr paths, ref uint modeCount, IntPtr modes, out uint topology);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, IntPtr paths, ref uint modeCount, IntPtr modes, IntPtr topology);
}

// Accessibility switches (through SystemParametersInfo, the session's live values) and the touchpad switches the Settings app writes.
internal static class InputSettingsReader
{
    public static InputReading Read(List<string> status)
    {
        var filter = new FilterKeys { Size = (uint)Marshal.SizeOf<FilterKeys>() };
        var mouse = new MouseKeys { Size = (uint)Marshal.SizeOf<MouseKeys>() };
        bool? filterOn = SystemParametersInfoW(SpiGetFilterKeys, filter.Size, ref filter, 0) ? (filter.Flags & 1) != 0 : null;
        bool? mouseOn = SystemParametersInfoW(SpiGetMouseKeys, mouse.Size, ref mouse, 0) ? (mouse.Flags & 1) != 0 : null;
        bool? enabled = null, offWithMouse = null;
        try
        {
            using var touchpad = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad");
            using var state = touchpad?.OpenSubKey("Status");
            // Missing values are Windows' defaults: the touchpad on, and left on when a mouse is connected.
            enabled = state?.GetValue("Enabled") is int on ? on != 0 : true;
            offWithMouse = touchpad?.GetValue("LeaveOnWithMouse") is int leave ? leave == 0 : false;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        { status.Add($"Touchpad settings: unavailable ({ex.GetType().Name})"); }
        if (filterOn is null || mouseOn is null) status.Add("Accessibility keys: could not be read");
        return new(filterOn, mouseOn, enabled, offWithMouse);
    }

    private const uint SpiGetFilterKeys = 0x0032, SpiGetMouseKeys = 0x0036;

    [StructLayout(LayoutKind.Sequential)]
    private struct FilterKeys
    {
        public uint Size, Flags, WaitMilliseconds, DelayMilliseconds, RepeatMilliseconds, BounceMilliseconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseKeys
    {
        public uint Size, Flags, MaxSpeed, TimeToMaxSpeed, CtrlSpeed, Reserved1, Reserved2;
    }

    [DllImport("user32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint action, uint size, ref FilterKeys value, uint flags);

    [DllImport("user32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint action, uint size, ref MouseKeys value, uint flags);
}
