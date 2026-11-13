# WinParted

A GParted-inspired disk and partition manager for **32-bit (x86)** and **64-bit (x64)** Windows.

## Features

- **Multi-Disk Management & Isolation**: Safely switch across physical drives and virtual disk images (`*.img`) with per-disk partition selection memory, queued operation scoping, and visual partition ribbon isolation.
- **GPT & MBR Partition Tables**:
  - Pre-flight and pre-write layout validation (non-overlapping ranges, GPT LBA `34 .. TotalSectors-34` bounds, max 128 GPT entries, unique partition GUIDs, MBR 4-slot primary limit).
  - Primary & Backup GPT Header/Array CRC32 verification and Backup GPT recovery.
  - Partition table backup (`.ptbak`) and transactional rollback on failure.
- **Windows & Linux Filesystem Support**:
  - Inspect, format, relabel, and resize/move in-place without losing data payloads or UUIDs across **NTFS**, **FAT32**, **exFAT**, **ext2 / ext3 / ext4**, **Btrfs**, **XFS**, and **Linux Swap**.
  - Cross-disk partition cloning and expansion, secure wiping, and queued batch execution with undo/preview.
- **Automated Stress & Data-Safety Suite**:
  - Built-in CLI and GUI (`Tools -> Run Stress & Data-Safety Test Suite...`) covering multi-disk switching, Windows/Linux resize & overlapping moves, cross-disk clones, partition table guardrails, and Backup GPT recovery.

## Build

Requires `.NET Framework 4.8` (`csc.exe`, built via PowerShell):

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
