# WinParted (Windows XP 32-bit Legacy Edition)

A GParted-inspired disk and partition manager for **Windows XP 32-bit (SP2/SP3, NT 5.1)** through modern **32-bit (x86)** and **64-bit (x64)** Windows, featuring a light/dark UI, non-destructive operation queuing with a non-blocking live progress dialog, raw sector inspection, full native Apple/Linux/optical read-only mounting, and an experimental write overlay.

Entirely experimental and vibe-coded. I don't know a thing about programming, but I do know that I dislike the windows partition manager, and have had great success with gparted on linux. I wanted this to be freely available for windows, but found that my only options were rather terrible and pay to use. I think that sucks. So I told AI to make this in hopes that this creates a better alternative to what is already available.

USE AT YOUR OWN RISK!!! Always test on virtual `.img` disk images (via `GParted -> Create / Reset Demo Virtual Disk (.img)`) before running operations against live physical drives.

## Features

- **Multi-Disk Management & Isolation**: Safely switch across physical drives and virtual disk images (`*.img`) with per-disk partition selection memory, queued operation scoping, an auto-expanding Drive selector, a width-resizable Device Overview pane, and a Smart Scale (`bytes^0.30`) partition ribbon.
- **OS Boot Safety & Non-System Boot Editing**: Protects the running Windows OS/boot partitions while allowing full-drive MBR/GPT reformatting and boot sector (EFI/MBR) editing on removable USB installers and secondary disks.
- **GPT & MBR Partition Tables**:
  - Pre-flight and pre-write layout validation (non-overlapping ranges, GPT LBA `34 .. TotalSectors-34` bounds, max 128 GPT entries, unique partition GUIDs, MBR 4-slot primary limit).
  - Primary & Backup GPT Header/Array CRC32 verification and Backup GPT recovery.
  - Partition table backup (`.gptbak`), raw LBA Hex Sector Inspector, 4K/1MiB alignment & health audit, and transactional rollback on virtual-disk failure.
- **Windows, Linux, Apple & Developer Filesystem Support**:
  - **Microsoft**: NTFS, FAT32, FAT16, exFAT, ReFS, BitLocker detection, EFI System, MSR, and Windows Recovery.
  - **Linux / Unix / Storage**: Ext2, Ext3, Ext4, Btrfs, XFS, F2FS, OpenZFS, SquashFS, Linux Swap, and LVM2.
  - **Optical & Apple**: ISO9660 (ECMA-119 / Joliet / Rock Ridge `NM`), UDF, **native Apple APFS** (`NXSB`/`APSB`, checkpoint descriptor ring, 2-level OMAP & FS-Tree B-Trees, `com.apple.decmpfs` zlib/inline decompression, sparse holes, and dual-volume firmlink merging), **HFS+ / HFSX** (with Extents Overflow B-Tree), and Classic HFS.
  - Cross-disk partition cloning and expansion, secure wiping (Zero / Ones / Cryptographic Random + automatic overlay/cache purge), in-place move/resize/relabel, and queued batch execution with undo/preview.
- **Integrated M3 File Explorer (Read-Only + Experimental Write Overlay)**:
  - Native userspace browsing, recursive search, text/hex preview, and **4 MiB chunked streaming export to host** (supporting multi-gigabyte files such as macOS `.dmg` images) with progress bar & cancellation.
  - Opt-in **Experimental Write Mode** backed by the `WPRWOVL1` persistent overlay (with automatic isohybrid tail placement and physical host-shadow fallback) for testing directory creation, file/folder upload, and deletion on unsupported filesystems.
- **Automated 10-Suite Stress & Data-Safety Suite**:
  - Built-in CLI and GUI (`Tools -> Run Stress & Data-Safety Test Suite...`) covering 10 test suites strictly on temporary virtual disks in `%TEMP%` (multi-disk switching, Windows/Linux/Apple resize & overlapping moves, cross-disk clones, guardrails & Backup GPT recovery, physical script verification, multi-OS native FS + APFS + >16 MiB HFS+ overflow exports, boot protection vs. USB/hybrid overlay tests, and cryptographic wipe / cache purge).

## Architecture & Developer Docs

See [**ARCHITECTURE.md**](ARCHITECTURE.md) for the full code layout, subsystem diagrams, on-disk superblock/offset reference table, Material Design 3 theme conventions, and developer cookbook.

## Downloads & Releases

Compiled executables are **not** stored in the Git source tree. Pre-built release binaries (`WinParted-x64.exe`, `WinParted-x86.exe`, `WinParted.exe`) are distributed exclusively on the GitHub **Releases** page, or you can compile them locally from source using `build.ps1`.

## Dependencies & System Requirements

### Windows 11 / Modern Windows (10 / 8.1 / 7 / Server)
- **Runtime**: `.NET Framework 4.x` (`4.0`–`4.8`, built into Windows 10 & Windows 11 — zero extra downloads required). Launch `WinParted-x64.exe` (recommended for 64-bit systems), `WinParted.exe`, or `WinParted-x86.exe`. Do **not** place a CLR 2.0 `app.config` alongside `WinParted-x64.exe` or `WinParted.exe`.
- **Architecture**: x64 (`WinParted-x64.exe`), AnyCPU (`WinParted.exe`), or x86 (`WinParted-x86.exe`).
- **Built-in OS Utilities & APIs** (included with Windows): `diskpart.exe` and `format.com` (native NTFS / FAT32 <= 32 GiB / FAT16 <= 2 GiB / exFAT / ReFS partitioning & formatting), `kernel32.dll` (`DeviceIoControl`, volume locking/dismount), `user32.dll` (`SetProcessDPIAware`), and `dwmapi.dll` (themed window chrome).
- **Privileges**: Standard user works for read-only inspection and all virtual `.img` / `.vhd` testing; **Administrator** elevation (`UAC`) is required to write to physical disks or format physical volumes.
- **Formatting Notes**: `ReFS` formatting via `diskpart`/`format.com` is restricted by Microsoft on some Windows 11 Home/Pro builds, and Windows `diskpart` caps `FAT32` at `32 GiB`; WinParted automatically falls back to its built-in userspace formatter when DiskPart declines or exceeds 32 GiB.
- **Build from Source (Windows 11 / Modern)**: Uses Windows' built-in compiler (`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe` and PowerShell). No Visual Studio or NuGet packages required; to build the XP x86 targets (`WinParted-XP-x86.exe`, `WinParted-x86.exe`), ensure the **.NET Framework 3.5** Windows feature is enabled so `.NET 2.0/3.5` reference assemblies exist.

### Windows XP 32-bit (SP2 / SP3, NT 5.1)
- **Target Branch & Binary**: Use **`WinParted-XP-x86.exe`** (or `WinParted-x86.exe`) built from the **`Windows-XP-32bit`** branch, kept together with **`WinParted-XP-x86.exe.config`** (from [src/app.config](src/app.config)).
- **Runtime**: **.NET Framework 3.5 SP1** (provides CLR `v2.0.50727` and `System.Core.dll` 3.5; `.NET Framework 4.0` Client Profile/Extended on XP SP3 is also supported by `app.config`). Windows Installer 3.1 + XP SP2 or SP3 is required for `.NET 3.5 SP1`.
- **Built-in OS Utilities**: `diskpart.exe` (legacy MBR layout creation/assignment) and `format.com` (NTFS / FAT32 <= 32 GiB / FAT16). When XP's `diskpart.exe` lacks `format`, `shrink`, `convert gpt`, `set id=... override`, or removable USB support, WinParted invokes `format.com` or falls back automatically to direct raw sector & userspace GPT/filesystem writes.
- **Optional Windows XP Updates**: Installing **KB955704** (Microsoft exFAT file system driver update for Windows XP) enables native OS mounting and `format.com /FS:EXFAT` for exFAT volumes; without KB955704, WinParted still inspects and formats exFAT headers in userspace.
- **Privileges**: Run from a Windows XP **Administrator** account for raw physical disk access and partitioning.
- **Hardware / Kernel Limitations Handled Automatically**: Windows XP 32-bit lacks native kernel GPT partitioning (`disk.sys` exposes GPT drives as a single `0xEE` protective MBR slice) and lacks Vista+ `SetProcessDPIAware` / `dwmapi.dll` APIs — WinParted isolates those APIs and parses/writes GPT tables in userspace when raw read access is available.

## Windows XP 32-bit Support

The `Windows-XP-32bit` branch adds first-class legacy support for Windows XP (x86 SP2/SP3):

- **Dual CLR Runtime Target**: `WinParted-XP-x86.exe` and `WinParted-x86.exe` compile strictly against `.NET Framework 3.5` (`CLR v2.0.50727`, `x86`, PE subsystem version `4.0`) with [src/app.config](src/app.config) enabling execution on either **.NET 3.5 SP1** (Windows XP) or **.NET 4.x** (Windows 7/10/11).
- **Vista+ API Guards**: High-DPI (`SetProcessDPIAware`), DWM window chrome (`dwmapi.dll`), and WoW64 detection (`IsWow64Process`) are isolated behind `IsWindowsVistaOrNewer` and `MethodImplOptions.NoInlining` wrappers so missing entry points on Windows XP SP2/SP3 `user32.dll` / `dwmapi.dll` never fail to load.
- **Userspace GPT & Legacy IOCTL Fallbacks**: Because 32-bit Windows XP lacks kernel GPT support (reporting GPT drives as a single `0xEE` protective MBR slot), `Win32DiskInterop` automatically falls back to `IOCTL_DISK_GET_DRIVE_GEOMETRY` + `IOCTL_DISK_GET_LENGTH_INFO`, 512-byte aligned reads on 63-sector/track geometry, and userspace GPT parsing via `RawDiskEngine.ParseDiskPartitionTable`.
- **Legacy `diskpart.exe` + `format.com` Raw Fallbacks**: When Windows XP's legacy `diskpart.exe` rejects GPT disks, `format fs=... quick`, `shrink`, `set id=... override`, or removable USB media, `OperationQueueManager` formats MBR volumes via `format.com` or falls back to direct raw sector partition table and filesystem header operations (including spec-compliant FAT32/FAT16 and >32 GiB FAT32).

## Build from Source

Requires Windows with `.NET Framework 3.5 SP1` / `4.x` (`csc.exe` under `v4.0.30319` + `.NET 2.0/3.5` reference assemblies):

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

Outputs local (git-ignored) binaries into `bin\WinParted-XP-x86.exe`, `bin\WinParted-x86.exe`, `bin\WinParted-x64.exe`, and `bin\WinParted.exe`, with `.exe.config` dual-runtime manifests paired alongside `WinParted-XP-x86.exe` and `WinParted-x86.exe`.

## Self-Test & Stress Test

```powershell
.\bin\WinParted-XP-x86.exe --self-test
.\bin\WinParted-x86.exe --self-test
.\bin\WinParted-x64.exe --self-test
```

Or run only the 10-suite stress harness with `--stress-test`:

```powershell
.\bin\WinParted-XP-x86.exe --stress-test
```
