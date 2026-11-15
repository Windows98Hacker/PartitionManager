# WinParted

A GParted-inspired disk and partition manager for **32-bit (x86)** and **64-bit (x64)** Windows, featuring a Google Material Design 3 light/dark UI, non-destructive operation queuing, raw sector inspection, and an integrated userspace file explorer for non-Microsoft and developer filesystems.

Entirely experimental and vibe-coded. I don't know a thing about programming, but I do know that I dislike the windows partition manager, and have had great success with gparted on linux. I wanted this to be freely available for windows, but found that my only options were rather terrible and pay to use. I think that sucks. So I told AI to make this in hopes that this creates a better alternative to what is already available.

USE AT YOUR OWN RISK!!! Always test on virtual `.img` disk images before running operations against live physical drives.

## Features

- **Multi-Disk Management & Isolation**: Safely switch across physical drives and virtual disk images (`*.img`) with per-disk partition selection memory, queued operation scoping, and visual Smart Scale (`bytes^0.30`) partition ribbon isolation.
- **GPT & MBR Partition Tables**:
  - Pre-flight and pre-write layout validation (non-overlapping ranges, GPT LBA `34 .. TotalSectors-34` bounds, max 128 GPT entries, unique partition GUIDs, MBR 4-slot primary limit).
  - Primary & Backup GPT Header/Array CRC32 verification and Backup GPT recovery.
  - Partition table backup (`.ptbak`), raw LBA Hex Sector Inspector, alignment/health audit, and transactional rollback on virtual-disk failure.
- **Windows, Linux, Apple & Developer Filesystem Support**:
  - **Microsoft**: NTFS, FAT32, FAT16, exFAT, ReFS, BitLocker detection, EFI System, MSR, and Windows Recovery.
  - **Linux / Unix / Storage**: Ext2, Ext3, Ext4, Btrfs, XFS, F2FS, OpenZFS, SquashFS, Linux Swap, and LVM2.
  - **Optical & Apple**: ISO9660 (ECMA-119 / Joliet / Rock Ridge `NM`), UDF, APFS, HFS+ / HFSX (with Extents Overflow B-Tree), and Classic HFS.
  - Cross-disk partition cloning and expansion, secure wiping, in-place move/resize/relabel, and queued batch execution with undo/preview.
- **Integrated M3 File Explorer (Read-Only + Experimental Write Overlay)**:
  - Native userspace browsing, recursive search, text/hex preview, and **4 MiB chunked streaming export to host** (supporting multi-gigabyte files such as macOS `.dmg` images) with progress bar & cancellation.
  - Opt-in red **Experimental Write Mode** backed by the `WPRWOVL1` persistent overlay for testing directory creation, file/folder upload, and deletion on virtual images.
- **Automated Stress & Data-Safety Suite**:
  - Built-in CLI and GUI (`Tools -> Run Stress & Data-Safety Test Suite...`) covering 8 test suites strictly on temporary `.img` files (multi-disk switching, Windows/Linux/Apple resize & overlapping moves, cross-disk clones, guardrails, Backup GPT recovery, dev filesystem formatting/mounting, and >16 MiB HFS+ Extents Overflow export).

## Architecture & Developer Docs

See [**ARCHITECTURE.md**](ARCHITECTURE.md) for the full code layout, subsystem diagrams, on-disk superblock/offset reference table, Material Design 3 theme conventions, and developer cookbook.

## Build

Requires `.NET Framework 4.8` (`csc.exe`, built via PowerShell with C# 5.0 syntax):

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

Outputs `bin\WinParted-x86.exe`, `bin\WinParted-x64.exe`, and `bin\WinParted.exe`.

## Self-Test & Stress Test

```powershell
.\bin\WinParted-x64.exe --self-test
.\bin\WinParted-x86.exe --self-test
```

Or run only the stress suite with `--stress-test`.
