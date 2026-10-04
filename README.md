# Skald

Skald helps a support or hardware team answer two questions about a Windows laptop or desktop, and keep the two answers apart:

1. **Check hardware.** Is it the hardware, a driver, or the BIOS? A one-minute, read-only scan of what Windows has already recorded (crashes, WHEA, storage, memory, graphics resets, firmware limits, device and driver problems per device class, what changed), ending in a verdict, an evidence list, and a plain statement of whether the evidence supports a driver or BIOS update.
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
- Sound, privacy, display and accessibility settings are the signed-in user's, so they are read only when `skald` runs as that user in their own session; under SYSTEM, a remote tool in session 0, or another account (an admin account via "Run as administrator") those items say **Could not check** and why.
- Ctrl+C stops the scan at the next step; a second Ctrl+C quits immediately. Run as administrator for the most complete result (kernel dump headers and some storage counters need it); every affected check says so rather than reporting a false "clear".

### How the verdict works

Each check is **Found**, **Nothing found**, **Could not check**, or **Not applicable**. Only hardware, driver and firmware checks decide the verdict: **evidence found**, **minor findings only** (small signals that also appear on healthy machines, such as one unexplained restart or a single USB device error), **no evidence found**, or **incomplete** (the System event log could not be read). Application crashes and hangs (with faulting module and exception code, so a crash inside the application's own code can be told apart from one inside a driver) and context (pending restart, low disk space, recent updates, driver and BIOS changes since the last scan or a baseline, BIOS age) are shown beside the verdict but never change it. "No evidence found" means Windows recorded nothing; it is not a hardware test, and the report says what was not covered.

Two more sections sit beside the verdict and never change it:

- **Driver and BIOS updates: what the evidence says.** One line per checked driver and the BIOS: *evidence points here*, *nothing points here*, or *could not assess*. "Nothing points here" is only said when the checks behind it ran (for example: no Device Manager problem, no driver errors or resets, the driver matches the baseline), and it says so in words a ticket can quote ("This evidence does not support a driver update"). BIOS age alone is never treated as evidence.
- **Windows settings that can make a working device look broken.** Airplane mode, a radio switched off, a muted or disabled sound device, camera or microphone privacy, "PC screen only", Filter Keys, a touchpad turned off, a device disabled in Device Manager. When hardware looks clean and a setting blocks a device, the Home hand-off says so ("Hardware and drivers look clean; a Windows setting is blocking the camera").

The scan keeps a driver, BIOS and OS-build snapshot at `%LOCALAPPDATA%\Skald\driver-snapshot.json` and compares the next scan against it. Use `--save-baseline` / `--baseline` to compare against a known-good snapshot instead.

### Recording

One **Record** button in the toolbar, available from every page. Its menu holds:

- **Include deep trace.** Also runs Windows Performance Recorder (WPR) in memory mode. Each **Mark Problem** saves the last minute or so of system activity as an ETL beside the recording (`<recording>.marker1.etl`, …), then a fresh buffer starts; Stop saves `<recording>.end.etl`. Requires Skald running as administrator: the item is disabled with the reason otherwise, and **Restart Skald as administrator** is offered. The choice is remembered. A trace left running by an app that closed or crashed is saved as `*.leftover.etl` on the next elevated launch.
- **Record power for 30 minutes** and **Open recordings folder**.

Mark Problem without a running recording starts one with the five minutes of pre-roll and saves it automatically two minutes after the last marker. Every recording stops and saves at two hours, because samples are held in memory until Stop (about 110 MB per hour).

Each recording saves, from the recorded machine:

- **Machine identity and drivers:** make/model, processor, graphics, memory, BIOS, Windows build and the full driver list.
- **Windows reports for the recording's own time window:** crashes, hangs, WHEA, driver/storage and restart reports, plus dump files written during it. Replay and the HTML report use these, so a recording opened on another PC never shows the viewer's events. Recordings from earlier versions without them fall back to the local logs only when opened on the PC that made them.
- **Events:** problem markers, CPU/memory/disk threshold conditions (logged once when they start and once when they end), process starts and exits (with name, PID and whether the program had a window), power-source and network-adapter changes, and saved deep traces. Replay pairs a program exit with any Windows crash or hang report for it within 30 seconds.

Recordings are Brotli-compressed JSON (`.perfsession`), about 3–4 KB per two-second sample, roughly 7× smaller than the gzip format of earlier versions, which still loads. When Documents is synced by OneDrive, recordings are kept in `%LOCALAPPDATA%\Skald\Sessions` instead of `Documents\Skald Sessions` so process paths and user names are not uploaded as they are written. ZIP exports can remove computer, user, domain and profile names, SIDs and device serials; deep traces cannot be scrubbed and are never included in a ZIP with names removed. The HTML report uses the hardware check's layout and Found / Nothing found / Could not check wording, per marker.

### Recording survives crashes

While recording, each sample is also appended to a journal next to the session file (`*.perfsession.journal`), flushed after every entry. If the app, Windows or the power dies mid-recording, the next launch rebuilds the session from the journal, reads the Windows reports through to the following boot (so the unclean-shutdown or bugcheck report is included), and says so ("Recovered 1 interrupted recording"). A clean Stop removes the journal.

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

The app is currently configured as an unpackaged WinUI 3 application and requests no administrator privileges; deep trace offers a restart as administrator. The executable is `Skald.exe`, and new recordings are saved under `Documents\Skald Sessions`, or `%LOCALAPPDATA%\Skald\Sessions` when Documents is synced by OneDrive. The Recordings page lists both.

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
- Recordings combines saved sessions, event navigation, and historical activity/power charts, including CPU package and GPU board power with per-domain average, peak and energy for the run. New sensor fields are saved in portable sessions. Old recordings load with missing fields unavailable; legacy generic power readings are explicitly unverified.
- Recordings can ZIP selected sessions or all saved sessions into `Documents\Skald Exports`, and can permanently delete selected sessions after confirmation. The current active recording is excluded from these actions until stopped.
- The Power & Thermals dashboard updates its bound headline and sensor rows in place so live telemetry does not rebuild the list while the user scrolls.
- Analyze Performance examines the last 60 seconds of collected history. A qualifying condition must span at least 30 seconds; unavailable samples or gaps over six seconds break a run. Rules include total CPU, individual logical processors, low available memory, and disk activity. They do not infer thermal limits or paging pressure.
- Mark Problem now starts a recording if needed, includes up to five minutes of buffered samples, and immediately checkpoints a replayable session file. A recording started this way saves itself two minutes after the last marker. Starting a normal recording also includes the same pre-roll.
- Recordings has an incident review centered on each user marker. It aligns sampled threshold transitions, nearby top processes, retained Windows reports, and dump candidates by timestamp. The review distinguishes timing correlation from cause and shows sampling gaps.
- Analyze Performance also inspects processor queue and DPC time, commit with hard-page reads, per-physical-disk read/write latency, TCP retransmissions, and interface errors or discards when those counters are available. These are screening observations, not root-cause determinations.
- Deep trace (WPR General profile, light, memory mode) is an option on the Record button rather than a separate control. Mark Problem places a WPR marker and saves the buffer; ETLs are saved with `-compress -skipPdbGen`, so they are one file each and quick to save, and kernel and driver stacks still resolve from symbol servers. Tracing needs administrator rights and can affect system performance.
- Reliability & Events surfaces structured WHEA/bugcheck fields when exposed, recurrence by report signature, BIOS context, driver or device firmware versions only on an exact PnP identity match, nearby dump candidates, and a copyable WinDbg launch command. A nearby dump is only a candidate; WinDbg and symbols are separate tools.

### Sensor sources and limitations

Windows `CallNtPowerInformation` supplies **OS-reported** processor MHz. This can be a nominal firmware value; it is not effective clock or a guarantee of boost frequency. Per-logical-processor load comes from Windows Processor Information counters.

NVIDIA NVML supplies GPU board power, enforced power limit, clock limit reasons, performance state, fan speed, temperature, graphics clock, and memory clock when supported by the installed driver. GPU sensor identity uses NVML UUID where available. GPU board power is never labeled whole-system power or added to CPU package power.

CPU power comes first from the in-box Windows **Energy Meter** counters, which expose the processor's RAPL domains without any added driver: CPU package, CPU cores, integrated graphics, DRAM and (on some laptops) platform PSys. These are processor-modeled running averages, not wall measurements. Cores and integrated graphics are part of package power and are shown separately, never summed; the counter set's `_Total` instance is ignored for that reason. Domains whose cumulative energy is zero (not implemented by the processor) are hidden. The `Processor Information` *% Performance Limit* and *Performance Limit Flags* counters show when firmware is capping CPU frequency; Windows does not say which PL1/PL2 limit applies or expose the limit values.

Skald can also read already-running LibreHardwareMonitor/OpenHardwareMonitor WMI providers for power, temperature, and clock sensors. Only a CPU-parent `CPU Package` power sensor is attributed to CPU package power. Skald does not install or start a hardware-access driver. If neither the Energy Meter counters nor a provider is exposed, CPU package power and CPU temperature can remain unavailable. Provider sample age is not exposed by that interface and is labeled accordingly.

Optional Windows Power Meter instances (and their power budget) are enumerated and displayed with their actual instance identity and **unidentified hardware domain**. They report milliwatts and are converted to watts. Their watts are never assumed to be CPU package power.

Analyze Performance adds *CPU performance limit* (firmware capping CPU frequency), *GPU thermal or hardware slowdown* (NVML thermal, hardware slowdown or power-brake reasons) and *GPU power cap* (the board held at its enforced limit, which is normal under full load). The first two are handed off as platform leads; the power cap is not.

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

Windows updates shows Windows Update Agent history by date range (last 1, 3, 7, 14 or 30 days, or all history; 14 days by default, the hardware check's update window), including installations, removals, failures and Defender definition updates. Repeats of the same update and outcome are one row with a count, the first and latest time, and the latest error code. History is read newest first until the range is covered, up to 2,000 entries; when the read limit or Windows' own retained history stops short of the range, the page says how far back it reaches. Drivers installed outside Windows Update and BIOS updates are not in this history; the hardware check's driver/BIOS comparison covers them. Its pending count comes from an offline search of the local update catalog; it can be stale until Windows checks online. The overview shows a compact update state and links to the history page. The collector does not initiate an online update search or install updates.

### Performance subtab diagnostics

CPU adds three-minute average/peak usage, the busiest logical processor, processor queue length, interrupt/DPC time, and current top CPU processes. The reported clock remains an OS-reported value, not effective frequency. Memory adds commit used/limit, Page Reads/sec, available-memory history, and top processes by private memory. Page Reads/sec is a hard-fault disk-read operation rate; it does not by itself prove a RAM shortage.

GPU shows the busiest physical engine counter over the same three-minute window and lists engine types and top GPU processes. Process engine readings are grouped by Windows LUID/physical-engine identity and capped at 100%; the chart is the busiest engine, not a sum of all engines or a match to vendor sensors. Disks shows per-physical-disk rates, average read/write latency, queue length, a selected-disk throughput history, top processes by total I/O, fixed-volume capacity, and optional storage reliability counters. Process I/O is not attributed to a physical disk. Network lists active adapters, per-adapter rates, reported link speed, receive/send utilization, error/discard changes between samples, a selected-adapter receive history, and system TCP retransmitted-segment rate. Unsupported counters display as unavailable.

Source coverage lists access failures, empty reliability history, and limits (5,000 scanned records per source). Windows retention/provider settings determine available history. The collector does not enable logs, change policy, inspect Security logs, or infer unrecorded telemetry. Event 41/6008 is never labeled proof of power-supply failure. Raw bugcheck messages can contain stop codes and dump locations when Windows recorded them. Automatic dump analysis and recording correlation are not implemented in this first version.

### Hardware identity & health

The Hardware page inventories Windows disks, graphics adapters, processor sockets, memory modules, physical network adapters, battery packs, and system firmware on a background refresh. Each device shows its Windows identity, configuration facts, source-specific health evidence, and whether a live reading is verified, probable, or unavailable. A device without direct health evidence stays **Unknown**; a normal Device Manager code is only evidence that Windows has no configuration problem.

Storage joins Windows disk numbers to partitions and their access paths, and shows provider health, operational state, model, serial, firmware, sector size, temperature, wear used, power-on hours, uncorrected errors, and provider-reported latency when exposed. Reliability counters are assigned only through the provider's explicit `MSFT_DiskToStorageReliabilityCounter` association, matched by WMI object path; unmatched counters remain separate. PhysicalDisk performance counters matched by disk-number prefix are labeled **probable**. When a disk has a stable provider ID or serial/model pair, Skald stores the latest read/write error counts under `%LOCALAPPDATA%\Skald\storage-health.json` using a hash of that identity and flags increases on later refreshes. This is a local comparison, not a persistent sampling service or a full SMART test.

The per-disk incident timeline lists recent high-latency samples from the rolling telemetry window and retained storage driver reports. It links events only when the message contains an explicit disk number and that number identifies one current disk during the present boot; the link is still labeled **probable**. Port-only controller resets, conflicting identifiers, and reports from earlier boots remain unassigned. A report near sampled latency is shown as a timing correlation, not proof of a failed drive or controller. Recent telemetry exists only while Skald is running; Windows event retention controls older report coverage.

Graphics identity uses Windows PnP IDs, but NVIDIA vendor sensors remain unmapped until an adapter identity can be verified. Network live rates use an exact interface GUID. Battery capacity is per pack where Windows exposes it; discharge rate is system aggregate. WHEA events remain unassigned because an error source alone does not identify the replaceable failing part. Source coverage shows when a WMI source is empty, unavailable, or partial.

## Device checks (2026-10-03)

Each device class gets its own hardware check, read the same way: is the device present and enumerating, what Windows reports for its health, how its driver compares with the baseline, and which of its driver records in the scan window are errors or resets. The filtering is done for the tech: the check lists grouped lines ("adapter reset ×3, latest …, reason given: …") so nobody has to open Event Viewer. Identical lines are collapsed with a count.

The same four steps apply to every class:

- *Present:* a built-in device that was in the baseline (or the previous scan) and is no longer present is evidence, worded as "stopped enumerating (failed, disconnected inside the machine, or turned off in BIOS setup)". The same part listed under a new instance ID after a firmware update is not. A built-in device that Windows remembers but cannot see, with no baseline, is a minor finding. An unplugged external device (USB adapter, monitor, mouse) is not counted. "Built-in" is Windows' own container ID for the computer, so an internal USB Bluetooth radio or camera counts as built in.
- *Health:* any Device Manager problem code except 22. Code 22 means disabled, which is a setting and is shown under Windows settings.
- *Driver:* provider, version, date, signer, and "matches / changed X → Y since / not compared". A changed driver is a detail, never evidence on its own. An unsigned driver is evidence.
- *Records:* the device's own driver service in the System log (errors are evidence at three or more, warnings are minor, information is ignored), failed device starts (Kernel-PnP/Configuration 411), driver installs (400, listed as detail), and driver load failures (Kernel-PnP 219, without the harmless WUDFRd reflector ones). Records known to appear on healthy machines are ignored and counted in the note: Intel Wi-Fi 6062 ("Lso was triggered", dozens a month) and BTHUSB 17 (an optional Bluetooth LE feature is missing).

Per class:

| Check | Devices | Added evidence | Settings check |
|---|---|---|---|
| `devices.network` Network adapters | Net class on PCI, USB, SDIO; virtual adapters and WAN miniports ignored | NDIS adapter resets (10400, with the reason Windows gives) and fatal errors (10317), matched to a hardware adapter by name; resets of virtual adapters are not counted | `settings.network`: airplane mode (unless Wi-Fi was turned back on inside it, as for Bluetooth), each Wi-Fi interface's software and hardware radio switch (Native Wifi), WLAN AutoConfig state and start type, disabled adapters |
| `devices.bluetooth` Bluetooth radio | Bluetooth class radio on USB, PCI, SDIO, ACPI; paired devices (BTHENUM, BTHLE) as accessories | A paired device's problem code is a minor finding about that device, not the radio. BTHPORT errors count; its warnings, mostly about remote devices, do not | `settings.bluetooth`: radio on, off or held off by hardware (Windows radio manager), MDM policy `Connectivity/AllowBluetooth = 0`, Bluetooth Support Service disabled (stopped is normal; it starts on demand) |
| `devices.audio` Audio devices | MEDIA class on HDAUDIO, INTELAUDIO, USB, PCI, ACPI, plus the HD Audio and Intel Smart Sound controllers from the System class | Windows audio engine (`audiodg.exe`) crashes inside a module that is not Windows' own: usually a sound enhancement (APO) installed with the audio driver. Evidence at three or more | `settings.audio`: Windows Audio and Endpoint Builder services, no active playback device (all disabled, or nothing plugged in), default playback or recording device muted or at 0%, microphone privacy |
| `devices.camera` Cameras | Camera class and USB video devices (Image class, `usbvideo` only), any bus except software cameras (virtual cameras, Windows Studio Effects) | The camera is never opened | `settings.camera`: Group Policy `Allow use of camera = 0`, device-wide and per-user camera access, desktop apps (Zoom, Google Meet in a browser, classic Teams), the new Teams and the Camera app, Store-app policy, Frame Server service disabled, and which app Windows records as using the camera right now (app or file name only) |
| `devices.display` Monitors and the built-in screen | Monitor class | The graphics adapter's driver is listed with its baseline comparison for reference; its evidence stays under *Graphics driver and GPU*, so it is never counted twice | `settings.display`: "PC screen only" (Win+P) with another monitor connected, "Second screen only" hiding the built-in screen, connected monitors Windows is not drawing on, disabled monitors |
| `devices.input` Keyboards, mice and touchpads | Keyboard and Mouse classes on HID, ACPI, USB, plus I2C HID devices (`hidi2c`); Remote Desktop input devices ignored | none | `settings.input`: Filter Keys, Mouse Keys, touchpad turned off, touchpad set to turn off while a mouse is connected (only when one is), disabled keyboards and mice |

- **Could not check** whenever the device list, the System log or the driver history needed for a clean answer could not be read. A log that starts after the window began (the device setup log is capped at 1 MB) is disclosed on the check and in the update line ("in the part of the logs that was read").
- **The signed-in user's settings.** Sound devices, mute and volume, privacy switches, display layout and accessibility keys belong to the user signed in at that session. When Skald runs as SYSTEM, in session 0, or under a different account (for example a tech's admin account via "Run as administrator"), those items are **Could not check** with that reason, never read from the wrong account. In a Remote Desktop session, sound devices and display layout are the remote session's, so they are not read either. Machine-wide items (airplane mode, services, Group Policy, device-wide privacy) are still read. Run `skald triage` as the signed-in user for the complete settings picture; run it elevated as that same user for the most complete hardware picture.
- Class checks own their devices: problem codes, unsigned drivers and load failures for their hardware are reported only there; a paired accessory's load failure stays under *Drivers*. The generic checks become *Device Manager problems (other devices)* and *Drivers* for everything else, so nothing is counted twice.
- The **Driver and BIOS updates** section takes its graphics line from *Graphics driver and GPU* plus the adapter's baseline comparison, and its BIOS line from the WHEA, firmware-limit and crash checks (hardware-leaning stop codes). BIOS age and a newer driver being available are never treated as evidence.
- Sources: device presence comes from SetupAPI and the configuration manager rather than `Win32_PnPEntity`, because only SetupAPI lists devices Windows remembers but cannot see, and gives the container ID that says a device is built in. Radio state comes from Native Wifi and `Windows.Devices.Radios`, sound devices from Core Audio (read-only: only the getters are called), display layout from `QueryDisplayConfig`, and accessibility keys from `SystemParametersInfo`. Driver records now include the signer (`Win32_PnPSignedDriver.Signer`); snapshots saved before this load with the signer blank. MyService's camera probe, which opens the camera, was deliberately not brought over.
- Connectivity, Wi-Fi signal, SSIDs, DNS, VPN, browser site permissions, and settings inside Teams or Zoom are not read.

## Hardware check update (2026-10-02)

- New `Skald.Triage` project holds the one-shot evidence collectors (event logs, hardware inventory, system inventory, drivers, dumps, Windows Update) that used to sit in `Skald.Collectors`, plus the rules engine, report writers, redaction and driver snapshots. `Skald.Cli` and the app both call it, so the console, HTML and in-app results use the same rules.
- Checks: crashes and unexpected restarts (stop code, lean toward hardware/driver/memory/storage/graphics, long power-button press = hung machine forced off, kernel dump header stop codes), WHEA, storage, memory (including Windows Memory Diagnostic results), graphics driver resets and GPU hangs, firmware throttling (Kernel-Processor-Power 37), Device Manager problems, driver signing and load failures, battery capacity. Application crashes and hangs are parsed for faulting module, exception code and whether the module is the application's own, a Windows component or a graphics driver.
- Generic user-mode driver reflector warnings (`\Driver\WUDFRd`) are ignored for the driver check because they appear on healthy machines whenever a device is unplugged.
- Kernel dump parsing reads only the fixed header of 64-bit kernel dumps (stop code and parameters). It does not name the faulting driver; use WinDbg for that. Reading `%SystemRoot%\Minidump` and `MEMORY.DMP` normally needs administrator rights.
- Windows Update history times were previously treated as local time; WUA reports UTC, so they were shifted by the UTC offset. Fixed.
- Live telemetry now includes an effective CPU clock estimate (base frequency x `% Processor Performance`) and ACPI thermal-zone temperature, passive limit and throttle reasons. Analyze Performance adds a *Platform thermal limit* rule when a zone's passive limit stays below 100% or a throttle reason is set for 30 seconds. Thermal zones are platform sensors, not necessarily the CPU die.
- New event categories in Reliability & Events: *Firmware & diagnostics*.
- Known limits: the scan runs no stress test; unsigned-driver detection depends on `Win32_PnPSignedDriver`; thresholds for "minor" versus "evidence" are conservative defaults in `TriageEngine` and should be tuned against real fleet data.
