# Foxide Helper

*[Русская версия](README.ru.md)*

A portable utility for Windows 10/11: disks (health, speed, wipe, partitioning), memory (modules, testing) and CPU / GPU stress tests.
It can fully wipe a disk, removing **all** partitions including EFI, MSR, OEM and recovery partitions that Disk Management refuses to delete.

> **AI-assisted development.** Foxide Helper is developed by Foxide Studio with the help of AI coding tools (Claude by Anthropic);
> the design, testing and every release are done and reviewed by people.

[![Support on Patreon](https://img.shields.io/badge/Patreon-support-f96854?logo=patreon)](https://www.patreon.com/cw/FoxideStudio)

## Getting started
Download `FoxideHelper.exe` from [Releases](https://github.com/Foxide-Studio/foxide-helper/releases) and run it — no installation needed
(it uses the .NET Framework 4.x built into Windows). The program asks for administrator rights: without them Windows does not expose SMART
data and does not allow repartitioning. Settings and pending reports are kept in `Documents\Foxide Helper` (or `%AppData%\Foxide Helper` if Documents is not writable) —
nothing is written next to the exe. Files of older versions are moved there automatically.

The interface is available in **English** and **Russian** (Settings → Appearance → Language).

## Features
### Home
The start page shows every module as a widget with its current state — disks (health, hottest disk), memory load, CPU and GPU
temperature, battery, cooling, background mode, updates and statistics. Click a widget to open the module.

### Disks
- **Grouped by type:** all disks, SSD (incl. NVMe), HDD, USB drives and memory cards, other / virtual.
- **Overview:** model, serial number, firmware, interface, partition style, sectors, partition map and used space.
- **Health:** Windows health status, SMART failure prediction, all SMART / NVMe attributes with explanations, live temperature chart,
  SSD wear, power-on hours, read/write errors, latency.
- **Speed:** sequential read/write (1 MB) and random 4K, bypassing the cache. The temporary file is deleted after the test.
- **Wipe:** `diskpart clean` (or `clean all`, writing zeros) → GPT/MBR → a single NTFS / exFAT / FAT32 partition (FAT32 also on disks over 32 GB).
  The system disk is locked. You must type the disk number to start, and the program re-checks that it is still the same disk.
- Safe removal of USB drives, changing drive letters.

### System
- **Memory:** usage, modules (manufacturer, size, speed, slots), a quick in-Windows test (MemTest-style patterns over the memory available
  to the process) and scheduling the full Windows Memory Diagnostic on reboot.
- **Processor:** CPU stress test on all cores with load, clock, temperature, GFLOPS and computation-error checks.
- **Graphics card:** every adapter is detected — model, video memory, driver version and date, display mode, PCIe link,
  power limit, max clocks and VBIOS (NVIDIA); OpenGL stress test with load, temperature, clock and power.
- **Battery** (laptops): health and wear (full charge vs. design capacity), charge cycles, power draw, voltage, temperature,
  and a discharge test under CPU / CPU+GPU load that measures average and peak power, runtime and real capacity.
- **Cooling:** CPU, GPU and memory temperatures and fans that Windows and the graphics drivers expose; NVIDIA GPU fan speed
  control where the driver allows it (fans return to automatic on exit). No drivers are installed — motherboard and case fans
  and memory temperature usually need the motherboard vendor's software.

### Background and notifications
- Start with Windows (a Task Scheduler task, no UAC prompt), tray icon. Its menu shows disk, CPU, GPU and memory temperatures
  and battery charge / health (each can be turned off in Settings).
- Disk overheating notifications with separate thresholds for SSD and HDD.
- Light, dark and system theme; the disk list refreshes when USB devices are plugged in; high-DPI support.

## Updates
The program checks [GitHub Releases](https://github.com/Foxide-Studio/foxide-helper/releases) at start and shows a notice with
**Update**, **Later** and **Don't check for updates** (the check can be turned back on in Settings → Updates).
A release is offered when its tag `vX.Y.Z` is newer than the running version and it has a `FoxideHelper.exe` asset. Before installing,
the download is checked against the asset size and its SHA-256 (the `digest` GitHub reports for the asset, or a `FoxideHelper.exe.sha256` asset).
The running exe is renamed to `.old`, the new one takes its place and starts; leftovers are removed on the next start.

## Privacy: anonymous statistics and crash reports
Unless turned off in Settings → Statistics and crash reports, once a day the program sends to Foxide Studio
(`hub.foxidestudio.com/api/foxidehelper/`): a random id created on first start (not tied to the hardware), program and Windows version,
interface language and Windows locale, number of launches, which sections, tabs and features were used (e.g. "speed test", "CPU stress test"), which options are
switched on (theme, temperature alerts, autostart, tray items…) and a general hardware summary (how many SSD / HDD / USB disks,
GPU vendor, amount of RAM, whether there is a battery). After a crash it sends the error type,
message and stack trace, with the user profile path, user name and PC name masked.
**No PC name, user name, device models, serial numbers or disk contents are sent, and the server does not store IP addresses.**
The code is in [`src/Telemetry.cs`](src/Telemetry.cs).

## Building
```powershell
.\build.ps1
```
Only `csc.exe` from `C:\Windows\Microsoft.NET\Framework64\v4.0.30319` is needed. `-Test` additionally builds `obj\FoxideDisk.test.exe`
without the administrator prompt for diagnostics (`--dump file.txt`, `--shot file.png <section> <tab> <theme> [disk]`).

### Translations
Source strings are Russian and are wrapped in `L.T("…")` / `L.F("… {0}", x)`; English pairs live in `src/lang/En.*.cs`.
`python tools/check_lang.py` lists strings without a translation and Russian literals that are not wrapped.

## Releasing a new version
1. Bump the version in `src/Program.cs` (`AssemblyVersion` and `AssemblyFileVersion`, `X.Y.Z.0`).
2. `.\build.ps1 -Out dist` — builds `dist\FoxideHelper.exe` and `dist\FoxideHelper.exe.sha256`.
3. Create a GitHub release with the tag `vX.Y.Z`, describe the changes (the text is shown in the update dialog) and attach both files.

## Contributing
Issues and pull requests are welcome — see [CONTRIBUTING.md](CONTRIBUTING.md). Security issues: [SECURITY.md](SECURITY.md).

## Support
Foxide Helper is free. If it helped you, please support the development on [Patreon](https://www.patreon.com/cw/FoxideStudio).

## License
[MIT](LICENSE) © 2026 Foxide Studio. The software is provided "as is", without warranty of any kind — the disk wipe deletes data
irreversibly, double-check the selected disk.
