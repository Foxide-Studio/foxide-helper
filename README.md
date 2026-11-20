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
data and does not allow repartitioning. Settings are stored in `FoxideHelper.ini` next to the exe (or in `%AppData%\FoxideDisk` if that folder is read-only).

The interface is available in **English** and **Russian** (Settings → Appearance → Language).

## Features
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
- **Stress test:** CPU (all cores, load, clock, temperature) and GPU (OpenGL load; load of any GPU from Windows counters,
  temperature / clock / power for NVIDIA via NVML).

### Background and notifications
- Start with Windows (a Task Scheduler task, no UAC prompt), tray icon.
- Disk overheating notifications with separate thresholds for SSD and HDD.
- Light, dark and system theme; the disk list refreshes when USB devices are plugged in; high-DPI support.

## Updates
The program checks [GitHub Releases](https://github.com/Foxide-Studio/foxide-helper/releases) at start (can be turned off in Settings → Updates).
A release is offered when its tag `vX.Y.Z` is newer than the running version and it has a `FoxideHelper.exe` asset. Before installing,
the download is checked against the asset size and its SHA-256 (the `digest` GitHub reports for the asset, or a `FoxideHelper.exe.sha256` asset).
The running exe is renamed to `.old`, the new one takes its place and starts; leftovers are removed on the next start.

## Privacy: anonymous statistics and crash reports
Unless turned off in Settings → Statistics and crash reports, once a day the program sends to Foxide Studio
(`hub.foxidestudio.com/api/foxidehelper/`): a random id created on first start (not tied to the hardware), program and Windows version,
interface language and Windows locale, number of launches and which sections were opened. After a crash it sends the error type,
message and stack trace, with the user profile path, user name and PC name masked.
**No PC name, user name, serial numbers or disk data are sent, and the server does not store IP addresses.**
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
