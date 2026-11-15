# WinParted

A GParted-inspired disk and partition manager for **32-bit (x86)** and **64-bit (x64)** Windows, featuring a Google Material Design 3 light/dark UI, non-destructive operation queuing, raw sector inspection, full native Apple/Linux/optical read-only mounting, and an experimental write overlay.

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

## Build from Source

Requires Windows with `.NET Framework 4.8` / `v4.0.30319` (`csc.exe`, using C# 5.0 syntax):

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

Outputs local (git-ignored) binaries into `bin\WinParted-x86.exe`, `bin\WinParted-x64.exe`, and `bin\WinParted.exe`.

## Self-Test & Stress Test

```powershell
.\bin\WinParted-x64.exe --self-test
.\bin\WinParted-x86.exe --self-test
```

Or run only the 10-suite stress harness with `--stress-test`:

```powershell
.\bin\WinParted-x64.exe --stress-test
```
