using System.Xml.Linq;
using Skald.Core.Models;

namespace Skald.Triage;

public enum ModuleOrigin { Application, WindowsComponent, GraphicsDriver, Unknown }

public sealed record AppFault(DateTimeOffset Time, bool IsHang, string App, string? AppVersion, string? Module,
    string? ExceptionCode, ModuleOrigin Origin)
{
    public string ExceptionText => ExceptionCodes.Describe(ExceptionCode);
}

public static class ExceptionCodes
{
    public static string Describe(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "no exception code";
        var normalized = code.Trim().TrimStart('0', 'x', 'X').ToUpperInvariant();
        var label = normalized switch
        {
            "C0000005" => "access violation",
            "C0000006" => "in-page I/O error",
            "C0000094" => "integer divide by zero",
            "C000001D" => "illegal instruction",
            "C00000FD" => "stack overflow",
            "C0000374" => "heap corruption",
            "C0000409" => "fail-fast / stack buffer overrun",
            "E0434352" => ".NET unhandled exception",
            "E06D7363" => "C++ unhandled exception",
            "80000003" => "breakpoint",
            _ => null
        };
        return label is null ? $"0x{normalized}" : $"0x{normalized} ({label})";
    }
}

public static class AppFaultParser
{
    private static readonly string[] GraphicsPrefixes =
    [
        "nvwgf2", "nvd3d", "nvoglv", "nvcuda", "nvldumd", "nvlddmkm", "nvapi", "atiumd", "atidxx", "atiu9p", "amdxx", "amdvlk",
        "amdihk", "ig9icd", "igd10", "igd11", "igd12", "igdumd", "igdrcl", "igc64", "igxelpicd", "igvk", "iigd", "ialmdd"
    ];

    private static readonly string[] WindowsModules =
    [
        "ntdll.dll", "kernel32.dll", "kernelbase.dll", "ucrtbase.dll", "msvcrt.dll", "combase.dll", "user32.dll", "win32u.dll",
        "rpcrt4.dll", "ole32.dll", "clr.dll", "coreclr.dll", "clrjit.dll", "msvcp140.dll", "vcruntime140.dll", "wow64.dll"
    ];

    public static AppFault? Parse(ReliabilityEvent report)
    {
        var isCrash = report.Provider.Equals("Application Error", StringComparison.OrdinalIgnoreCase) && report.EventId == 1000;
        var isHang = report.Provider.Equals("Application Hang", StringComparison.OrdinalIgnoreCase) && report.EventId == 1002;
        if (!isCrash && !isHang) return null;
        var data = Data(report.Raw);
        var app = Get(data, "AppName", 0) ?? (report.Component == report.Provider ? null : report.Component) ?? "Unknown application";
        var module = isCrash ? Get(data, "ModuleName", 3) : null;
        var path = isCrash ? Get(data, "ModulePath", 11) : null;
        var exception = isCrash ? Get(data, "ExceptionCode", 6) : null;
        return new AppFault(report.Timestamp, isHang, app, Get(data, "AppVersion", 1), module, exception,
            isHang ? ModuleOrigin.Unknown : Classify(app, module, path));
    }

    public static ModuleOrigin Classify(string app, string? module, string? modulePath)
    {
        if (string.IsNullOrWhiteSpace(module)) return ModuleOrigin.Unknown;
        var name = module.Trim();
        if (GraphicsPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) return ModuleOrigin.GraphicsDriver;
        if (name.Equals(app, StringComparison.OrdinalIgnoreCase)) return ModuleOrigin.Application;
        if (WindowsModules.Contains(name, StringComparer.OrdinalIgnoreCase)) return ModuleOrigin.WindowsComponent;
        if (modulePath is null) return ModuleOrigin.Unknown;
        var path = modulePath.Replace('/', '\\');
        return path.Contains(@"\windows\system32\", StringComparison.OrdinalIgnoreCase)
            || path.Contains(@"\windows\syswow64\", StringComparison.OrdinalIgnoreCase)
            || path.Contains(@"\windows\winsxs\", StringComparison.OrdinalIgnoreCase)
            ? ModuleOrigin.WindowsComponent : ModuleOrigin.Application;
    }

    // Windows 10+ names the fields; earlier builds only order them, so fall back to the position.
    private static List<(string? Name, string Value)> Data(string raw)
    {
        try
        {
            return XElement.Parse(raw).Descendants().Where(element => element.Name.LocalName == "Data")
                .Select(element => ((string?)element.Attribute("Name"), element.Value.Trim())).ToList();
        }
        catch (System.Xml.XmlException) { return []; }
    }

    private static string? Get(List<(string? Name, string Value)> data, string name, int position)
    {
        var named = data.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        if (named.Name is not null) return named.Value.Length > 0 ? named.Value : null;
        var unnamed = data.Count > position && data[position].Name is null ? data[position].Value : null;
        return string.IsNullOrWhiteSpace(unnamed) ? null : unnamed;
    }
}
