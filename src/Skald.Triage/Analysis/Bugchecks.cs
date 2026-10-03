using System.Globalization;

namespace Skald.Triage;

public enum BugcheckLean { General, Hardware, Memory, Storage, Graphics, Driver, Manual }

public sealed record BugcheckInfo(uint Code, string Name, BugcheckLean Lean)
{
    public string Display => $"0x{Code:X8} {Name}";
    public string LeanText => Lean switch
    {
        BugcheckLean.Hardware => "commonly associated with hardware or firmware faults",
        BugcheckLean.Memory => "commonly associated with memory corruption (RAM or a driver)",
        BugcheckLean.Storage => "commonly associated with storage I/O or file system faults",
        BugcheckLean.Graphics => "commonly associated with graphics driver or GPU faults",
        BugcheckLean.Driver => "commonly associated with a faulty kernel-mode driver",
        BugcheckLean.Manual => "was initiated manually or by a test tool",
        _ => "has no single typical cause"
    };
}

// A stop code narrows where to look; it is never proof. Only a dump analysis can name the faulting module.
public static class Bugchecks
{
    private static readonly Dictionary<uint, BugcheckInfo> Catalog = new BugcheckInfo[]
    {
        new(0x0A, "IRQL_NOT_LESS_OR_EQUAL", BugcheckLean.Driver),
        new(0x19, "BAD_POOL_HEADER", BugcheckLean.Memory),
        new(0x1A, "MEMORY_MANAGEMENT", BugcheckLean.Memory),
        new(0x1E, "KMODE_EXCEPTION_NOT_HANDLED", BugcheckLean.Driver),
        new(0x24, "NTFS_FILE_SYSTEM", BugcheckLean.Storage),
        new(0x3B, "SYSTEM_SERVICE_EXCEPTION", BugcheckLean.Driver),
        new(0x4E, "PFN_LIST_CORRUPT", BugcheckLean.Memory),
        new(0x50, "PAGE_FAULT_IN_NONPAGED_AREA", BugcheckLean.Memory),
        new(0x51, "REGISTRY_ERROR", BugcheckLean.Storage),
        new(0x77, "KERNEL_STACK_INPAGE_ERROR", BugcheckLean.Storage),
        new(0x7A, "KERNEL_DATA_INPAGE_ERROR", BugcheckLean.Storage),
        new(0x7B, "INACCESSIBLE_BOOT_DEVICE", BugcheckLean.Storage),
        new(0x7E, "SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", BugcheckLean.Driver),
        new(0x7F, "UNEXPECTED_KERNEL_MODE_TRAP", BugcheckLean.Hardware),
        new(0x80, "NMI_HARDWARE_FAILURE", BugcheckLean.Hardware),
        new(0x8E, "KERNEL_MODE_EXCEPTION_NOT_HANDLED", BugcheckLean.Driver),
        new(0x9C, "MACHINE_CHECK_EXCEPTION", BugcheckLean.Hardware),
        new(0x9F, "DRIVER_POWER_STATE_FAILURE", BugcheckLean.Driver),
        new(0xA0, "INTERNAL_POWER_ERROR", BugcheckLean.Driver),
        new(0xBE, "ATTEMPTED_WRITE_TO_READONLY_MEMORY", BugcheckLean.Driver),
        new(0xC2, "BAD_POOL_CALLER", BugcheckLean.Driver),
        new(0xC4, "DRIVER_VERIFIER_DETECTED_VIOLATION", BugcheckLean.Driver),
        new(0xC5, "DRIVER_CORRUPTED_EXPOOL", BugcheckLean.Driver),
        new(0xCE, "DRIVER_UNLOADED_WITHOUT_CANCELLING_PENDING_OPERATIONS", BugcheckLean.Driver),
        new(0xD1, "DRIVER_IRQL_NOT_LESS_OR_EQUAL", BugcheckLean.Driver),
        new(0xD5, "DRIVER_PAGE_FAULT_BEYOND_END_OF_ALLOCATION", BugcheckLean.Driver),
        new(0xE2, "MANUALLY_INITIATED_CRASH", BugcheckLean.Manual),
        new(0xEA, "THREAD_STUCK_IN_DEVICE_DRIVER", BugcheckLean.Graphics),
        new(0xEF, "CRITICAL_PROCESS_DIED", BugcheckLean.General),
        new(0xF4, "CRITICAL_OBJECT_TERMINATION", BugcheckLean.Storage),
        new(0xFC, "ATTEMPTED_EXECUTE_OF_NOEXECUTE_MEMORY", BugcheckLean.Driver),
        new(0x101, "CLOCK_WATCHDOG_TIMEOUT", BugcheckLean.Hardware),
        new(0x109, "CRITICAL_STRUCTURE_CORRUPTION", BugcheckLean.Memory),
        new(0x116, "VIDEO_TDR_FAILURE", BugcheckLean.Graphics),
        new(0x117, "VIDEO_TDR_TIMEOUT_DETECTED", BugcheckLean.Graphics),
        new(0x119, "VIDEO_SCHEDULER_INTERNAL_ERROR", BugcheckLean.Graphics),
        new(0x124, "WHEA_UNCORRECTABLE_ERROR", BugcheckLean.Hardware),
        new(0x133, "DPC_WATCHDOG_VIOLATION", BugcheckLean.Driver),
        new(0x139, "KERNEL_SECURITY_CHECK_FAILURE", BugcheckLean.Driver),
        new(0x13A, "KERNEL_MODE_HEAP_CORRUPTION", BugcheckLean.Memory),
        new(0x141, "VIDEO_ENGINE_TIMEOUT_DETECTED", BugcheckLean.Graphics),
        new(0x154, "UNEXPECTED_STORE_EXCEPTION", BugcheckLean.Storage),
    }.ToDictionary(item => item.Code);

    public static BugcheckInfo Describe(uint code)
        => Catalog.TryGetValue(code, out var info) ? info : new BugcheckInfo(code, "Unrecognized stop code", BugcheckLean.General);

    public static bool TryParse(string? text, out uint code)
    {
        code = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var value = text.Trim();
        return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.TryParse(value.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out code)
            : uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out code);
    }
}
