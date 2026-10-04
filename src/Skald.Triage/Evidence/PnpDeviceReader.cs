using System.Runtime.InteropServices;

namespace Skald.Triage;

// Reads one device class from Windows' device database through SetupAPI and the configuration manager, read-only. Unlike
// Win32_PnPEntity this also lists devices Windows remembers but that are not present now, and the container ID that says
// whether a device is built into the computer.
internal static class PnpDeviceReader
{
    internal sealed record RawDevice(string InstanceId, string Name, string ClassName, bool? Present, uint? ProblemCode, string? Service, Guid? Container);

    // Returns null when the class could not be opened at all. Problem is set when the list is incomplete; listed devices are still returned.
    public static IReadOnlyList<RawDevice>? Read(Guid classGuid, out string? problem)
    {
        problem = null;
        var set = SetupDiGetClassDevsW(ref classGuid, null, IntPtr.Zero, 0);
        if (set == InvalidHandle)
        {
            problem = $"device list unavailable (error {Marshal.GetLastWin32Error()})";
            return null;
        }
        var devices = new List<RawDevice>();
        var unreadable = 0;
        try
        {
            var data = new SpDevinfoData { Size = (uint)Marshal.SizeOf<SpDevinfoData>() };
            for (uint index = 0; ; index++)
            {
                if (!SetupDiEnumDeviceInfo(set, index, ref data))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != ErrorNoMoreItems) problem = $"device list stopped after {devices.Count} device(s) (error {error})";
                    break;
                }
                // A device whose ID cannot be read is counted, never merged with another or silently dropped.
                if (InstanceId(set, ref data) is not { } id) { unreadable++; continue; }
                var result = CM_Get_DevNode_Status(out var status, out var problemNumber, data.DevInst, 0);
                // The IsPresent property is authoritative; a remembered device has no live devnode, so the status call reports "no such device".
                var present = Boolean(set, ref data, IsPresentKey) ?? (result == CrSuccess ? true : result == CrNoSuchDevinst ? false : null);
                uint? code = present == true && result == CrSuccess ? (status & DnHasProblem) != 0 ? problemNumber : 0 : null;
                devices.Add(new(id, Text(set, ref data, SpdrpFriendlyName) ?? Text(set, ref data, SpdrpDeviceDesc) ?? id,
                    Text(set, ref data, SpdrpClass) ?? "Unknown", present, code, Text(set, ref data, SpdrpService), GuidProperty(set, ref data, ContainerKey)));
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        if (unreadable > 0) problem = (problem is null ? string.Empty : problem + "; ") + $"{unreadable} device(s) whose ID could not be read";
        return devices;
    }

    private static string? InstanceId(IntPtr set, ref SpDevinfoData data)
    {
        var buffer = new char[400];
        if (!SetupDiGetDeviceInstanceIdW(set, ref data, buffer, buffer.Length, out var required))
        {
            if (required <= buffer.Length) return null;
            buffer = new char[required];
            if (!SetupDiGetDeviceInstanceIdW(set, ref data, buffer, buffer.Length, out _)) return null;
        }
        var id = new string(buffer).TrimEnd('\0');
        return id.Length == 0 ? null : id;
    }

    private static string? Text(IntPtr set, ref SpDevinfoData data, uint property)
    {
        SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out _, null, 0, out var required);
        if (required == 0) return null;
        var buffer = new byte[required];
        if (!SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out _, buffer, required, out _)) return null;
        var value = System.Text.Encoding.Unicode.GetString(buffer).Split('\0')[0].Trim();
        return value.Length == 0 ? null : value;
    }

    private static bool? Boolean(IntPtr set, ref SpDevinfoData data, DevPropKey key)
    {
        var buffer = new byte[1];
        return SetupDiGetDevicePropertyW(set, ref data, ref key, out var type, buffer, 1, out _, 0) && type == DevpropTypeBoolean ? buffer[0] != 0 : null;
    }

    private static Guid? GuidProperty(IntPtr set, ref SpDevinfoData data, DevPropKey key)
    {
        var buffer = new byte[16];
        return SetupDiGetDevicePropertyW(set, ref data, ref key, out var type, buffer, 16, out _, 0) && type == DevpropTypeGuid ? new Guid(buffer) : null;
    }

    private static readonly IntPtr InvalidHandle = new(-1);
    private const int ErrorNoMoreItems = 259;
    private const uint CrSuccess = 0, CrNoSuchDevinst = 0x0D, DnHasProblem = 0x400;
    private const uint SpdrpDeviceDesc = 0x0, SpdrpService = 0x4, SpdrpClass = 0x7, SpdrpFriendlyName = 0xC;
    private const uint DevpropTypeGuid = 0x0D, DevpropTypeBoolean = 0x11;
    private static readonly DevPropKey IsPresentKey = new() { FormatId = new("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), PropertyId = 5 };
    private static readonly DevPropKey ContainerKey = new() { FormatId = new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), PropertyId = 2 };

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevinfoData
    {
        public uint Size;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SpDevinfoData data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref SpDevinfoData data, [Out] char[] buffer, int size, out int required);

    [DllImport("setupapi.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref SpDevinfoData data, uint property, out uint type, [Out] byte[]? buffer, uint size, out uint required);

    [DllImport("setupapi.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref SpDevinfoData data, ref DevPropKey key, out uint type, [Out] byte[] buffer, uint size, out uint required, uint flags);

    [DllImport("setupapi.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);
}
