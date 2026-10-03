using System.Buffers.Binary;
using Skald.Cli;
using Skald.Core.Models;
using Skald.Triage;
using Xunit;

namespace Skald.Core.Tests;

public sealed class TriageAnalysisTests
{
    [Fact]
    public void BugcheckCatalogLeansTowardAreasWithoutClaimingCause()
    {
        Assert.Equal(BugcheckLean.Hardware, Bugchecks.Describe(0x124).Lean);
        Assert.Equal(BugcheckLean.Driver, Bugchecks.Describe(0xD1).Lean);
        Assert.Equal(BugcheckLean.Graphics, Bugchecks.Describe(0x116).Lean);
        Assert.Equal(BugcheckLean.General, Bugchecks.Describe(0xDEAD).Lean);
        Assert.Contains("commonly associated", Bugchecks.Describe(0x124).LeanText, StringComparison.Ordinal);
        Assert.True(Bugchecks.TryParse("0x00000133", out var hex));
        Assert.Equal(0x133u, hex);
        Assert.True(Bugchecks.TryParse("292", out var decimalCode));
        Assert.Equal(0x124u, decimalCode);
        Assert.False(Bugchecks.TryParse("nonsense", out _));
    }

    [Fact]
    public void ReadsKernelDumpHeaderAndRejectsOtherFiles()
    {
        var header = new byte[0x2000];
        "PAGEDU64"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x34), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x38), 0x124);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x40), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x48), 0xFFFFA00012345678);
        var info = DumpHeaderReader.TryParse(header);
        Assert.NotNull(info);
        Assert.Equal(0x124u, info.BugcheckCode);
        Assert.Equal(16u, info.ProcessorCount);
        Assert.Equal(0xFFFFA00012345678UL, info.Parameters[1]);
        Assert.Equal("WHEA_UNCORRECTABLE_ERROR", info.Bugcheck.Name);

        Assert.Null(DumpHeaderReader.TryParse("MDMP"u8.ToArray().Concat(new byte[0x100]).ToArray()));
        Assert.Null(DumpHeaderReader.TryParse(new byte[8]));
    }

    [Fact]
    public void ReadsDumpHeaderFromDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"skald-dump-{Guid.NewGuid():N}.dmp");
        try
        {
            var header = new byte[0x1000];
            "PAGEDU64"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x38), 0x9F);
            File.WriteAllBytes(path, header);
            Assert.Equal(0x9Fu, DumpHeaderReader.TryRead(path)?.BugcheckCode);
            Assert.Null(DumpHeaderReader.TryRead(path + ".missing"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ParsesNamedAndUnnamedApplicationErrorFields()
    {
        var named = new ReliabilityEvent(DateTimeOffset.UtcNow, "Application", 1, "Application Error", 1000, "Applications", "Error", "x", "s", "",
            "<Event><EventData><Data Name='AppName'>app.exe</Data><Data Name='ModuleName'>KERNELBASE.dll</Data><Data Name='ExceptionCode'>e0434352</Data><Data Name='ModulePath'>C:\\WINDOWS\\System32\\KERNELBASE.dll</Data></EventData></Event>");
        var fault = AppFaultParser.Parse(named);
        Assert.NotNull(fault);
        Assert.Equal("app.exe", fault.App);
        Assert.Equal(ModuleOrigin.WindowsComponent, fault.Origin);
        Assert.Contains(".NET unhandled exception", fault.ExceptionText, StringComparison.Ordinal);

        var positional = named with { Raw = "<Event><EventData><Data>old.exe</Data><Data>1.0</Data><Data>t</Data><Data>old.exe</Data><Data>1.0</Data><Data>t</Data><Data>c0000005</Data></EventData></Event>" };
        var legacy = AppFaultParser.Parse(positional);
        Assert.NotNull(legacy);
        Assert.Equal("old.exe", legacy.App);
        Assert.Equal(ModuleOrigin.Application, legacy.Origin);
        Assert.Contains("access violation", legacy.ExceptionText, StringComparison.Ordinal);

        Assert.Null(AppFaultParser.Parse(named with { Provider = "Other" }));
        Assert.True(AppFaultParser.Parse(named with { Provider = "Application Hang", EventId = 1002 })?.IsHang);
    }

    [Theory]
    [InlineData("app.exe", "nvwgf2umx.dll", null, ModuleOrigin.GraphicsDriver)]
    [InlineData("app.exe", "app.exe", null, ModuleOrigin.Application)]
    [InlineData("app.exe", "ntdll.dll", null, ModuleOrigin.WindowsComponent)]
    [InlineData("app.exe", "plugin.dll", @"C:\Program Files\App\plugin.dll", ModuleOrigin.Application)]
    [InlineData("app.exe", "comdlg32.dll", @"C:\Windows\System32\comdlg32.dll", ModuleOrigin.WindowsComponent)]
    [InlineData("app.exe", "mystery.dll", null, ModuleOrigin.Unknown)]
    [InlineData("app.exe", null, null, ModuleOrigin.Unknown)]
    public void ClassifiesFaultingModules(string app, string? module, string? path, ModuleOrigin expected)
        => Assert.Equal(expected, AppFaultParser.Classify(app, module, path));

    [Fact]
    public void DriverComparisonFindsEveryKindOfChangeAndListsVendorsFirst()
    {
        DriverRecord Driver(string id, string version, string provider = "Vendor") => new(id, id, "Net", version, null, provider, "a.inf", true);
        var before = new[] { Driver("same", "1.0"), Driver("up", "1.0"), Driver("down", "2.0"), Driver("gone", "1.0"), Driver("inbox", "1.0", "Microsoft Windows") };
        var after = new[] { Driver("same", "1.0"), Driver("up", "1.1"), Driver("down", "1.0"), Driver("new", "3.0"), Driver("inbox", "1.1", "Microsoft Windows") };
        var changes = DriverComparison.Compare(before, after);
        Assert.Equal(5, changes.Count);
        Assert.Contains(changes, change => change.Kind == DriverChangeKind.Updated && change.Subject.DeviceId == "up");
        Assert.Contains(changes, change => change.Kind == DriverChangeKind.Downgraded && change.Subject.DeviceId == "down");
        Assert.Contains(changes, change => change.Kind == DriverChangeKind.Added && change.Subject.DeviceId == "new");
        Assert.Contains(changes, change => change.Kind == DriverChangeKind.Removed && change.Subject.DeviceId == "gone");
        Assert.Equal("inbox", changes[^1].Subject.DeviceId);
        Assert.Equal(new DateTimeOffset(2023, 6, 15, 0, 0, 0, TimeSpan.Zero), DriverComparison.ParseCimDate("20230615000000.******+***"));
        Assert.Null(DriverComparison.ParseCimDate("garbage"));
    }

    [Fact]
    public void BundleWritesAllFormatsAndZip()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"skald-bundle-{Guid.NewGuid():N}");
        try
        {
            var machine = Environment.MachineName;
            var report = TriageEngine.Evaluate(new TriageInputs(DateTimeOffset.Now, 30, false)
            {
                Reliability = new(DateTimeOffset.Now, [], ["System: 1 records scanned", "Application: 1 records scanned"]),
                Machine = [new("System name", machine), new("Serial number", "SN-998877")],
                SourceStatus = [$"Note from {machine} about BTHENUM\\DEV_542A4316F033\\7&1&0"]
            }, "test");
            var drivers = new DriverInventory(DateTimeOffset.Now, [new DriverRecord(@"BTHENUM\DEV_542A4316F033\7&1C2D&0&BLUETOOTHDEVICE_542A4316F033", $"{machine} headset", "Bluetooth", "1.0", null, "Microsoft", "bth.inf", true)], []);
            var result = new TriageResult(report, drivers, null);

            var loose = TriageBundle.Write(result, folder);
            Assert.Equal(4, loose.Count);
            Assert.All(loose, path => Assert.True(File.Exists(path)));
            Assert.Empty(Directory.GetFiles(folder, "*.writing"));

            var zip = TriageBundle.Write(result, folder, redact: true, zip: true);
            Assert.Single(zip);
            Assert.Contains("SKALD-host-", zip[0], StringComparison.Ordinal);
            using var archive = System.IO.Compression.ZipFile.OpenRead(zip[0]);
            Assert.Equal(4, archive.Entries.Count);
            foreach (var entry in archive.Entries)
            {
                using var reader = new StreamReader(entry.Open());
                var content = reader.ReadToEnd();
                Assert.DoesNotContain(machine, content, StringComparison.Ordinal);
                Assert.DoesNotContain("SN-998877", content, StringComparison.Ordinal);
                Assert.DoesNotContain("542A4316F033", content, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void IncompleteBaselineLoadsWithoutCrashing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"skald-baseline-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{"createdAt":"2026-01-01T00:00:00Z","drivers":[{"deviceId":"A","version":"1.0"},{"name":"no id"}]}""");
            var snapshot = DriverSnapshotStore.Load(path, new List<string>());
            Assert.NotNull(snapshot);
            var record = Assert.Single(snapshot.Drivers);
            Assert.Equal("Unknown", record.Provider);
            Assert.Single(DriverComparison.Compare(snapshot.Drivers, [record with { Version = "2.0" }]));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CliParsesTriageOptions()
    {
        var options = CliOptions.Parse(["triage", "--days", "7", "--out", "C:\\r", "--zip", "--redact", "--baseline", "b.json", "--save-baseline", "g.json", "--console", "json", "--quiet"]);
        Assert.Null(options.Error);
        Assert.Equal(CliCommand.Triage, options.Command);
        Assert.Equal(7, options.Days);
        Assert.Equal("C:\\r", options.OutDirectory);
        Assert.True(options.Zip && options.Redact && options.Quiet);
        Assert.Equal("b.json", options.Baseline);
        Assert.Equal("g.json", options.SaveBaseline);
        Assert.Equal("json", options.Console);
    }

    [Theory]
    [InlineData("triage", "--days", "0")]
    [InlineData("triage", "--days", "31")]
    [InlineData("triage", "--days", "abc")]
    [InlineData("triage", "--days")]
    [InlineData("triage", "--console", "xml")]
    [InlineData("triage", "--nope")]
    [InlineData("scan")]
    public void CliRejectsBadArguments(params string[] args) => Assert.NotNull(CliOptions.Parse(args).Error);

    [Fact]
    public void CliRefusesToOverwriteTheBaselineItComparesAgainst()
        => Assert.Contains("overwrite", CliOptions.Parse(["triage", "--baseline", "golden.json", "--save-baseline", ".\\golden.json"]).Error, StringComparison.Ordinal);

    [Theory]
    [InlineData("Scanned · 30 days — A → B ×2 … 45.0 °C", "Scanned - 30 days - A -> B x2 ... 45.0 C")]
    [InlineData("Intel® Core™ · Café", "Intel(R) Core(TM) - Cafe")]
    [InlineData("日本", "??")]
    public void ConsoleTextIsPlainAscii(string input, string expected) => Assert.Equal(expected, ConsoleText.Ascii(input));

    [Fact]
    public void CliDefaultsToHelpAndVersion()
    {
        Assert.Equal(CliCommand.Help, CliOptions.Parse([]).Command);
        Assert.Equal(CliCommand.Help, CliOptions.Parse(["--help"]).Command);
        Assert.Equal(CliCommand.Version, CliOptions.Parse(["version"]).Command);
        Assert.Equal(30, CliOptions.Parse(["triage"]).Days);
    }
}
