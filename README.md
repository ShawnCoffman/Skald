# Skald

Skald helps a support or hardware team answer two questions about a Windows laptop or desktop, and keep the two answers apart:

1. **Check hardware.** Is it the hardware, a driver, or the BIOS? A one-minute, read-only scan of what Windows has already recorded (crashes, WHEA, storage, memory, graphics resets, firmware limits, device and driver problems, what changed), ending in a verdict and an evidence list.
2. **Investigate slowness.** Why is it slow, hot or freezing? Live CPU, memory, disk, GPU, network, power and thermals, the processes behind them, and a recorder you can replay after the problem has passed.

After either one, the Home page gives a one-line hand-off: hardware evidence exists, or the current constraint looks software-side (with the top contributor as a lead, never as proof).

Both modes use the same words (**Found / Nothing found / Could not check**) and the same kind of finding: a statement, its evidence, and what it does *not* prove. Neither mode changes the machine.

## Quick start

The app (`Skald.exe`) opens on Home with two buttons. **Run hardware check** takes about a minute and can be saved as `.txt`, `.json`, `.html` or a `.zip` bundle for a ticket (with an option to remove computer name, serial number and user names). **Open live view**, **Record**, and **Mark Problem** cover the slowness side.

### Command line

The same hardware check runs headless, for remote sessions and scripts:

```powershell
.\tools\publish-cli.ps1                      # builds artifacts\cli\skald.exe (about 42 MB, no .NET install needed)
skald triage                                 # print the report
skald triage --out C:\Temp\case --zip        # also write txt/json/html reports as one zip
skald triage --days 7 --redact               # shorter window; strip computer name, serial, user names, SIDs
skald triage --save-baseline golden.json     # snapshot this machine's drivers, BIOS and OS build
skald triage --baseline golden.json          # later: what changed against that snapshot
skald triage --console json --quiet          # machine-readable output only
```

Exit codes: `0` nothing significant (clear, or minor findings only), `1` hardware evidence found, `2` check incomplete, `3` error (including report files that could not be written), `64` bad arguments or a `--baseline` file that does not exist.

- Reports are named `SKALD-<computer>-<yyyyMMdd-HHmmss>` (`SKALD-host-…` with `--redact`): `.txt`, `.json`, `.html`, plus `-drivers.json` with the driver inventory. With `--redact`, device instance IDs keep their type (for example `USB\VID_…&PID_…`) but lose serial numbers and Bluetooth MAC addresses.
- Console output is plain ASCII so Windows PowerShell 5.1 captures, redirects and `ConvertFrom-Json` read it correctly; saved files are UTF-8.
- The report is printed before files are written. The driver snapshot only moves forward after the output succeeded, so a failed export does not hide driver changes from the next scan. `--baseline` leaves the previous-scan snapshot unchanged, and `--save-baseline` refuses to overwrite the file named by `--baseline`.
- Ctrl+C stops the scan at the next step; a second Ctrl+C quits immediately. Run as administrator for the most complete result (kernel dump headers and some storage counters need it); every affected check says so rather than reporting a false "clear".

### How the verdict works

Each check is **Found**, **Nothing found**, **Could not check**, or **Not applicable**. Only hardware, driver and firmware checks decide the verdict: **evidence found**, **minor findings only** (small signals that also appear on healthy machines, such as one unexplained restart or a single USB device error), **no evidence found**, or **incomplete** (the System event log could not be read). Application crashes and hangs (with faulting module and exception code, so a crash inside the application's own code can be told apart from one inside a driver) and context (pending restart, low disk space, recent updates, driver and BIOS changes since the last scan or a baseline, BIOS age) are shown beside the verdict but never change it. "No evidence found" means Windows recorded nothing; it is not a hardware test, and the report says what was not covered.

The scan keeps a driver, BIOS and OS-build snapshot at `%LOCALAPPDATA%\Skald\driver-snapshot.json` and compares the next scan against it. Use `--save-baseline` / `--baseline` to compare against a known-good snapshot instead.

### Recording survives crashes

While recording, each sample is also appended to a journal next to the session file (`*.perfsession.journal`). If the app, Windows or the power dies mid-recording, the next launch rebuilds the session from the journal and says so ("Recovered 1 interrupted recording"). A clean Stop removes the journal.

### Layout of the code

| Project | Role |
|---|---|
| `Skald.Core` | Models shared by everything |
| `Skald.Collectors` | Live sampling every two seconds (counters, processes, GPU, network, power, thermal zones) |
| `Skald.Triage` | One-shot evidence collectors, the rules and verdict, reports, redaction, driver snapshots |
| `Skald.Diagnostics` | Rules over live samples (Analyze Performance) |
| `Skald.Recorder` | Flight recorder, journal, replay documents |
| `Skald.App` | WinUI 3 desktop app |
| `Skald.Cli` | Command-line front end for the hardware check |

## Current milestone

*(The sections below are the running engineering log. Where they describe navigation, Reliability & Events, Hardware, and Windows Updates now live under Check hardware, and the performance pages under Investigate slowness.)*


The repository now contains the first live-telemetry slice:

- .NET 10 solution
- WinUI 3 desktop shell using Windows App SDK 1.8
- read-only CPU, memory, physical-disk, and full process-list sampling
- sortable and filterable process table with expandable executable-based app groups, aggregate totals, and individual child processes; columns include owner, CPU, per-process GPU engine utilization, dedicated/shared GPU memory, working set, private and peak memory, process I/O rates, threads, handles, CPU time, and cumulative page faults where available
- GPU page with Windows per-adapter dedicated/shared memory usage and NVIDIA NVML temperature, power draw, and dedicated memory used/capacity when a compatible driver exposes them
- Windows power-source/battery state and aggregate network throughput sampling
- explainable initial findings for CPU and disk activity and low available physical memory (without claiming paging pressure)
- five-minute rolling telemetry buffer
- Flight Recorder sessions saved as compressed portable `.perfsession` files
- user problem markers and automatic CPU/memory/disk event markers
- original finding-first Summary and four synchronized live performance charts, with navigation for GPU, Processes, Disks, Network, Power, Recorder, Sessions, and System Info
- session browser with timeline scrubbing and historical process/event views
- standalone HTML report export from a saved session
- unit tests for the diagnostic rules

The first sample from a new process reports CPU and I/O rates as unavailable because a delta is required before rates can be calculated.

Process I/O rates come from Windows process I/O counters; they are not physical-disk-only attribution. A dash in the Processes table means access was denied or a counter was unavailable. GPU percentages come from Windows GPU Engine performance counters and represent the busiest engine for each process or app group, not a sum across all GPU engines. They require a driver that exposes these counters; the first sample is unavailable while the rate counter is primed.

The Processes table does not show estimated watts per PID. Windows' Energy Estimation Engine can provide estimated per-process energy in a recorded trace, and SRUM can export historical app energy data, but neither is a supported live two-second per-PID watt counter. Skald does not convert CPU/GPU utilization or whole-system battery draw into fictional process watts.

GPU memory readings come from Windows GPU Process Memory and GPU Adapter Memory counters. Per-process dedicated and shared memory can include cross-process shared allocations, so summing process values can exceed system-wide adapter usage. The GPU page keeps Windows adapter memory separate from NVIDIA sensors because their device identities are not yet correlated. Temperature and power currently use the NVIDIA Management Library (NVML) only if it is installed with the driver; unsupported readings remain unavailable. NVIDIA power draw is a GPU/board reading, not an estimate of whole-computer power or a per-process attribution. AMD and Intel temperature/power collectors and reliable adapter-to-sensor matching remain future work.

The Apps section is best-effort: it identifies groups containing a process with a visible top-level window. Groups are keyed by executable path when available (falling back to process name) so unrelated programs with the same process name are not merged. Aggregate totals sum available child readings.

## Build

Open `Skald.sln` in Visual Studio with the Windows App SDK prerequisites installed, or run:

```powershell
dotnet restore .\Skald.sln
dotnet build .\Skald.sln -c Debug -p:Platform=x64
dotnet test .\Skald.sln -c Debug -p:Platform=x64
```

The app is currently configured as an unpackaged WinUI 3 application and requests no administrator privileges. The executable is `Skald.exe`, and new recordings are saved under `Documents\Skald Sessions`. The Recordings page also discovers earlier `.perfsession` files in other `Documents\* Sessions` folders.

## Planned next milestones

1. SQLite-backed session indexing and a buffered high-volume writer.
2. More diagnostic rules: single-core saturation, paging, disk latency, and queue pressure.
3. Laptop telemetry: battery and power detail, AMD/Intel GPU sensors, adapter identity matching, and thermal capabilities.
4. Richer synchronized replay charts and session comparison.

## Engineering dashboard update (2026-09-26)

- Overview keeps four live meters (CPU total, physical memory, disk active time, network receive with send beneath) and focuses on machine identity, OS/firmware, and expandable inventory. Detailed activity and power remain on Performance and Power & Thermals.
- Navigation follows investigation flow: Overview; Performance (CPU, Memory, GPU, Disks, Network); Processes; Power & Thermals; Reliability & Events; Hardware; Windows Updates; and Recordings. Analyze Performance is available in the main toolbar beside Mark Problem. Overview and Hardware provide machine identity and capabilities. The CPU page shows logical-processor utilization and Windows-reported clock readings.
- Power & Thermals combines synchronized utilization, reported clock, and selectable power histories with per-sensor current/minimum/average/maximum statistics. Battery details appear only when a battery is present.
- Processes has General, CPU, Memory, GPU, and I/O column presets. Name and PID remain pinned; the metric header scrollbar moves the numeric columns.
- The Processes table redraws every five seconds while visible, and filters/sorts redraw immediately. The underlying telemetry and recording cadence remains two seconds, so this change reduces row rebuilding rather than the cost of collecting process data.
- Recordings combines start/stop, problem markers, saved sessions, event navigation, and historical activity/power charts. New sensor fields are saved in portable sessions. Old recordings load with missing fields unavailable; legacy generic power readings are explicitly unverified.
- Recordings can ZIP selected sessions or all saved sessions into `Documents\Skald Exports`, and can permanently delete selected sessions after confirmation. The current active recording is excluded from these actions until stopped.
- The Power & Thermals dashboard updates its bound headline and sensor rows in place so live telemetry does not rebuild the list while the user scrolls.
- Analyze Performance examines the last 60 seconds of collected history. A qualifying condition must span at least 30 seconds; unavailable samples or gaps over six seconds break a run. Rules include total CPU, individual logical processors, low available memory, and disk activity. They do not infer thermal limits or paging pressure.
- Mark Problem now starts a recording if needed, includes up to five minutes of buffered samples, and immediately checkpoints a replayable session file. The recording continues for post-marker context until Stop. Starting a normal recording also includes the same pre-roll.
- Recordings has an incident review centered on each user marker. It aligns sampled threshold transitions, nearby top processes, retained Windows reports, and dump candidates by timestamp. The review distinguishes timing correlation from cause and shows sampling gaps.
- Analyze Performance also inspects processor queue and DPC time, commit with hard-page reads, per-physical-disk read/write latency, TCP retransmissions, and interface errors or discards when those counters are available. These are screening observations, not root-cause determinations.
- The Recordings page can start and save an optional WPR General-profile memory trace. Mark Problem also places a WPR marker while that trace is running. The saved ETL is intended for Windows Performance Analyzer; starting a trace may require Windows permissions and can affect system performance.
- Reliability & Events surfaces structured WHEA/bugcheck fields when exposed, recurrence by report signature, BIOS context, driver or device firmware versions only on an exact PnP identity match, nearby dump candidates, and a copyable WinDbg launch command. A nearby dump is only a candidate; WinDbg and symbols are separate tools.

### Sensor sources and limitations

Windows `CallNtPowerInformation` supplies **OS-reported** processor MHz. This can be a nominal firmware value; it is not effective clock or a guarantee of boost frequency. Per-logical-processor load comes from Windows Processor Information counters.

NVIDIA NVML supplies GPU board power, temperature, graphics clock, and memory clock when supported by the installed driver. GPU sensor identity uses NVML UUID where available. GPU board power is never labeled whole-system power or added to CPU package power.

Skald can read already-running LibreHardwareMonitor/OpenHardwareMonitor WMI providers for power, temperature, and clock sensors. Only a CPU-parent `CPU Package` power sensor is attributed to CPU package power. Skald does not install or start a hardware-access driver. If neither provider is exposed, CPU package power and CPU temperature can remain unavailable. Provider sample age is not exposed by that interface and is labeled accordingly.

Optional Windows Power Meter instances are enumerated and displayed with their actual instance identity and **unidentified hardware domain**. Their watts are never assumed to be CPU package power. Effective CPU clock and thermal/power limiting indicators currently remain unavailable/unknown.

History uses timestamps, retains a three-minute window, ignores duplicate timestamps, and leaves gaps for missing readings. Power charts do not substitute another device's readings for missing samples of the selected sensor. Statistics describe available samples in the selected window, not rated hardware limits.

On battery-powered systems, Skald reads Windows battery WMI status, capacity, and built-in monitor brightness when exposed. It reports discharge watts only when static battery capabilities confirm absolute power units; batteries that report relative units remain unavailable. Battery health is full-charge capacity divided by design capacity. Runtime at current draw uses remaining Wh divided by measured discharge W. Desktop and AC operation show whole-system draw as unavailable because CPU/GPU sensors and unidentified power meters cannot establish wall power.

The Power page has a 30-minute recording preset. Recordings retain battery draw, CPU/GPU/disk/network activity, processes, temperature sensors, display brightness, and power source in each sample. Replay shows a full-session timeline and a run-level power summary: time-weighted average battery draw, sampled peak, estimated battery energy used in Wh, and the fraction of the recording covered by adjacent valid battery samples. Long gaps and AC intervals do not contribute to the average or energy. Desktop/AC whole-system draw remains unavailable without a verified meter; this release does not capture E3 trace energy. Replay also shows projected runtime and battery health. A recording can be selected as a baseline for comparison with another recording from the same machine. Increased process activity, GPU use, brightness, and other metrics are presented as correlations, never as measured per-process watts or a claim that their sum explains the draw change. Estimated remainder is displayed only when measured battery discharge, identified CPU package power, and GPU board power are all available and the difference is nonnegative; it remains a rough cross-domain estimate.


### Overview inventory

Machine context includes system name, manufacturer/model, processor topology, installed and usable memory, graphics, uptime, process count, and power source. OS & firmware includes Windows edition, release/full build, last boot, BIOS version/date, and Secure Boot state.

Inventory runs on a separate background task, refreshes on request or on returning after five minutes, and is not duplicated into every telemetry sample or recording. Device details show present Plug and Play devices with nonzero ConfigManagerErrorCode, distinguish disabled devices (22), and include status descriptions and instance IDs. Storage lists fixed volumes and flags less than 10% free. Restart indicators distinguish Windows servicing and Windows Update reboot keys from Session Manager file operations queued for the next boot. Only the former raise a restart attention signal; these checks are not a comprehensive reboot assessment.

Installed apps count desktop uninstall registrations in both registry views for the machine/current user and current-user packaged apps. Frameworks, resource packages, and marked system components are excluded; exact repeated desktop registrations are deduplicated within scope. The list is searchable, and its count is not a census of other users or portable apps. Failed sources are explicitly marked unknown or partial. Copy system summary copies identity and health summaries locally to the clipboard.

### Reliability & Events

Reliability & Events is a top-level section with rolling 24-hour, 7-day, and 30-day filters, a selectable daily report timeline, category/text filters, recurring-report groups, occurrence timestamps, and native message/XML details. Collection runs off the telemetry sampling path and is cached for five minutes between visits; Refresh explicitly rescans.

The collector combines relevant retained Application/System events with Win32_ReliabilityRecords. Categories cover application failures, bugcheck/unclean-shutdown reports, WHEA, common graphics/storage/device-driver sources, and installation/update context. Native event records take precedence over duplicate WMI copies. Counts are reports, not unique failures: separate Windows events may describe one incident. Grouping uses provider, event ID, and available component identity; it does not establish a shared root cause.

The overview adds seven-day counts for Application Error 1000 crashes, Application Hang 1002 hangs, unexpected shutdowns, confirmed bugcheck reports, and WHEA hardware reports. Kernel-Power 41 and EventLog 6008 within ten minutes count as one unexpected shutdown; this does not imply a bugcheck or hardware cause. Counts may be partial if event sources are unavailable or the 5,000-record-per-log scan cap is reached. Reliability & Events lists existing `%SystemRoot%\MEMORY.DMP`, `%SystemRoot%\Minidump\*.dmp`, and current-user `CrashDumps` files separately from incidents. Its button can include other profiles' `CrashDumps` folders in a shallow, read-only scan; inaccessible locations remain partial.

Windows updates shows the latest 50 Windows Update Agent history entries, including installations, removals, and failures. Its pending count comes from an offline search of the local update catalog; it can be stale until Windows checks online. The overview shows a compact update state and links to the history page. The collector does not initiate an online update search or install updates.

### Performance subtab diagnostics

CPU adds three-minute average/peak usage, the busiest logical processor, processor queue length, interrupt/DPC time, and current top CPU processes. The reported clock remains an OS-reported value, not effective frequency. Memory adds commit used/limit, Page Reads/sec, available-memory history, and top processes by private memory. Page Reads/sec is a hard-fault disk-read operation rate; it does not by itself prove a RAM shortage.

GPU shows the busiest physical engine counter over the same three-minute window and lists engine types and top GPU processes. Process engine readings are grouped by Windows LUID/physical-engine identity and capped at 100%; the chart is the busiest engine, not a sum of all engines or a match to vendor sensors. Disks shows per-physical-disk rates, average read/write latency, queue length, a selected-disk throughput history, top processes by total I/O, fixed-volume capacity, and optional storage reliability counters. Process I/O is not attributed to a physical disk. Network lists active adapters, per-adapter rates, reported link speed, receive/send utilization, error/discard changes between samples, a selected-adapter receive history, and system TCP retransmitted-segment rate. Unsupported counters display as unavailable.

Source coverage lists access failures, empty reliability history, and limits (5,000 scanned records per source). Windows retention/provider settings determine available history. The collector does not enable logs, change policy, inspect Security logs, or infer unrecorded telemetry. Event 41/6008 is never labeled proof of power-supply failure. Raw bugcheck messages can contain stop codes and dump locations when Windows recorded them. Automatic dump analysis and recording correlation are not implemented in this first version.

### Hardware identity & health

The Hardware page inventories Windows disks, graphics adapters, processor sockets, memory modules, physical network adapters, battery packs, and system firmware on a background refresh. Each device shows its Windows identity, configuration facts, source-specific health evidence, and whether a live reading is verified, probable, or unavailable. A device without direct health evidence stays **Unknown**; a normal Device Manager code is only evidence that Windows has no configuration problem.

Storage joins Windows disk numbers to partitions and their access paths, and shows provider health, operational state, model, serial, firmware, sector size, temperature, wear used, power-on hours, uncorrected errors, and provider-reported latency when exposed. Reliability counters are assigned only through the provider's explicit `MSFT_DiskToStorageReliabilityCounter` association, matched by WMI object path; unmatched counters remain separate. PhysicalDisk performance counters matched by disk-number prefix are labeled **probable**. When a disk has a stable provider ID or serial/model pair, Skald stores the latest read/write error counts under `%LOCALAPPDATA%\Skald\storage-health.json` using a hash of that identity and flags increases on later refreshes. This is a local comparison, not a persistent sampling service or a full SMART test.

The per-disk incident timeline lists recent high-latency samples from the rolling telemetry window and retained storage driver reports. It links events only when the message contains an explicit disk number and that number identifies one current disk during the present boot; the link is still labeled **probable**. Port-only controller resets, conflicting identifiers, and reports from earlier boots remain unassigned. A report near sampled latency is shown as a timing correlation, not proof of a failed drive or controller. Recent telemetry exists only while Skald is running; Windows event retention controls older report coverage.

Graphics identity uses Windows PnP IDs, but NVIDIA vendor sensors remain unmapped until an adapter identity can be verified. Network live rates use an exact interface GUID. Battery capacity is per pack where Windows exposes it; discharge rate is system aggregate. WHEA events remain unassigned because an error source alone does not identify the replaceable failing part. Source coverage shows when a WMI source is empty, unavailable, or partial.

## Hardware check update (2026-10-02)

- New `Skald.Triage` project holds the one-shot evidence collectors (event logs, hardware inventory, system inventory, drivers, dumps, Windows Update) that used to sit in `Skald.Collectors`, plus the rules engine, report writers, redaction and driver snapshots. `Skald.Cli` and the app both call it, so the console, HTML and in-app results use the same rules.
- Checks: crashes and unexpected restarts (stop code, lean toward hardware/driver/memory/storage/graphics, long power-button press = hung machine forced off, kernel dump header stop codes), WHEA, storage, memory (including Windows Memory Diagnostic results), graphics driver resets and GPU hangs, firmware throttling (Kernel-Processor-Power 37), Device Manager problems, driver signing and load failures, battery capacity. Application crashes and hangs are parsed for faulting module, exception code and whether the module is the application's own, a Windows component or a graphics driver.
- Generic user-mode driver reflector warnings (`\Driver\WUDFRd`) are ignored for the driver check because they appear on healthy machines whenever a device is unplugged.
- Kernel dump parsing reads only the fixed header of 64-bit kernel dumps (stop code and parameters). It does not name the faulting driver; use WinDbg for that. Reading `%SystemRoot%\Minidump` and `MEMORY.DMP` normally needs administrator rights.
- Windows Update history times were previously treated as local time; WUA reports UTC, so they were shifted by the UTC offset. Fixed.
- Live telemetry now includes an effective CPU clock estimate (base frequency x `% Processor Performance`) and ACPI thermal-zone temperature, passive limit and throttle reasons. Analyze Performance adds a *Platform thermal limit* rule when a zone's passive limit stays below 100% or a throttle reason is set for 30 seconds. Thermal zones are platform sensors, not necessarily the CPU die.
- New event categories in Reliability & Events: *Firmware & diagnostics*.
- Known limits: the scan runs no stress test; unsigned-driver detection depends on `Win32_PnPSignedDriver`; thresholds for "minor" versus "evidence" are conservative defaults in `TriageEngine` and should be tuned against real fleet data.
