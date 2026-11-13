using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace WinParted.Models
{
    public enum PartitionTableStyle
    {
        Unknown = 0,
        MBR = 1,
        GPT = 2,
        RAW = 3
    }

    public enum FileSystemType
    {
        Unallocated,
        NTFS,
        FAT32,
        FAT16,
        exFAT,
        ReFS,
        Ext2,
        Ext3,
        Ext4,
        Btrfs,
        XFS,
        F2FS,
        ZFS,
        SquashFS,
        ISO9660,
        UDF,
        LinuxSwap,
        LVM2,
        BitLocker,
        APFS,
        HFSPlus,
        HFS,
        EfiSystem,
        MicrosoftReserved,
        WindowsRecovery,
        Extended,
        RAW
    }

    public enum OperationType
    {
        CreatePartition,
        DeletePartition,
        ResizeMovePartition,
        FormatPartition,
        ChangeLabel,
        ChangeFlags,
        CreatePartitionTable,
        CopyPartition,
        WipePartition,
        RestorePartitionTable
    }

    public enum AlignmentMode
    {
        MiB = 0,
        Cylinder = 1,
        None = 2
    }

    public enum WipePattern
    {
        ZeroFill = 0,
        OnesFill = 1,
        RandomDoD = 2
    }

    public static class GptPartitionTypeGuids
    {
        public static readonly Guid Unused = Guid.Empty;
        public static readonly Guid EfiSystem = new Guid("c12a7328-f81f-11d2-ba4b-00a0c93ec93b");
        public static readonly Guid MicrosoftReserved = new Guid("e3c9e316-0b5c-4db8-817d-f92df00215ae");
        public static readonly Guid BasicData = new Guid("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7");
        public static readonly Guid WindowsRecovery = new Guid("de94bba4-06d1-4d40-a16a-bfd50179d6ac");
        public static readonly Guid LinuxFilesystem = new Guid("0fc63daf-8483-4772-8e79-3d69d8477de4");
        public static readonly Guid LinuxSwap = new Guid("0657fd6d-a4ab-43c4-84e5-0933c84b4f4f");
        public static readonly Guid LinuxLvm = new Guid("e6d6d379-f507-44c2-a23c-238f2a3df928");
        public static readonly Guid ZfsMember = new Guid("6a898cc3-1dd2-11b2-99a6-080020736631");
        public static readonly Guid BiosBoot = new Guid("21686148-6449-6e6f-744e-656564454649");
        public static readonly Guid AppleApfs = new Guid("7c3457ef-0000-11aa-aa11-00306543ecac");
        public static readonly Guid AppleHfs = new Guid("48465300-0000-11aa-aa11-00306543ecac");
        public static readonly Guid AppleBoot = new Guid("426f6f74-0000-11aa-aa11-00306543ecac");
        public static readonly Guid AppleUfs = new Guid("55465300-0000-11aa-aa11-00306543ecac");
        public static readonly Guid AppleCoreStorage = new Guid("53746f72-6167-11aa-aa11-00306543ecac");
        public static readonly Guid AppleRaid = new Guid("52414944-0000-11aa-aa11-00306543ecac");

        public static string Describe(Guid g)
        {
            if (g == Guid.Empty) return "Unused";
            if (g == EfiSystem) return "EFI System Partition";
            if (g == MicrosoftReserved) return "Microsoft Reserved (MSR)";
            if (g == BasicData) return "Basic Data (Windows/FAT/NTFS)";
            if (g == WindowsRecovery) return "Windows Recovery Environment";
            if (g == LinuxFilesystem) return "Linux Filesystem Data";
            if (g == LinuxSwap) return "Linux Swap";
            if (g == LinuxLvm) return "Linux LVM";
            if (g == ZfsMember) return "OpenZFS / Solaris Pool";
            if (g == BiosBoot) return "BIOS Boot Partition";
            if (g == AppleApfs) return "Apple APFS";
            if (g == AppleHfs) return "Apple HFS+ / HFSX";
            if (g == AppleBoot) return "Apple Boot / Recovery HD";
            if (g == AppleUfs) return "Apple UFS";
            if (g == AppleCoreStorage) return "Apple CoreStorage";
            if (g == AppleRaid) return "Apple RAID";
            return g.ToString("B");
        }

        public static Guid ForFileSystem(FileSystemType fs)
        {
            switch (fs)
            {
                case FileSystemType.EfiSystem:
                    return EfiSystem;
                case FileSystemType.MicrosoftReserved:
                    return MicrosoftReserved;
                case FileSystemType.WindowsRecovery:
                    return WindowsRecovery;
                case FileSystemType.Ext2:
                case FileSystemType.Ext3:
                case FileSystemType.Ext4:
                case FileSystemType.Btrfs:
                case FileSystemType.XFS:
                case FileSystemType.F2FS:
                case FileSystemType.SquashFS:
                    return LinuxFilesystem;
                case FileSystemType.ZFS:
                    return ZfsMember;
                case FileSystemType.ISO9660:
                case FileSystemType.UDF:
                    return BasicData;
                case FileSystemType.LinuxSwap:
                    return LinuxSwap;
                case FileSystemType.LVM2:
                    return LinuxLvm;
                case FileSystemType.APFS:
                    return AppleApfs;
                case FileSystemType.HFSPlus:
                case FileSystemType.HFS:
                    return AppleHfs;
                default:
                    return BasicData;
            }
        }
    }

    public static class FileSystemColors
    {
        public static Color GetBorderColor(FileSystemType fs)
        {
            switch (fs)
            {
                case FileSystemType.Unallocated: return Color.FromArgb(148, 163, 184);
                case FileSystemType.NTFS: return Color.FromArgb(22, 163, 74);
                case FileSystemType.FAT32: return Color.FromArgb(22, 101, 52);
                case FileSystemType.FAT16: return Color.FromArgb(34, 197, 94);
                case FileSystemType.exFAT: return Color.FromArgb(16, 185, 129);
                case FileSystemType.ReFS: return Color.FromArgb(13, 148, 136);
                case FileSystemType.Ext2: return Color.FromArgb(59, 130, 246);
                case FileSystemType.Ext3: return Color.FromArgb(37, 99, 235);
                case FileSystemType.Ext4: return Color.FromArgb(29, 78, 216);
                case FileSystemType.Btrfs: return Color.FromArgb(234, 88, 12);
                case FileSystemType.XFS: return Color.FromArgb(202, 138, 4);
                case FileSystemType.F2FS: return Color.FromArgb(236, 72, 153);
                case FileSystemType.ZFS: return Color.FromArgb(6, 182, 212);
                case FileSystemType.SquashFS: return Color.FromArgb(101, 163, 13);
                case FileSystemType.ISO9660: return Color.FromArgb(148, 82, 255);
                case FileSystemType.UDF: return Color.FromArgb(192, 132, 252);
                case FileSystemType.LinuxSwap: return Color.FromArgb(190, 18, 60);
                case FileSystemType.LVM2: return Color.FromArgb(124, 58, 237);
                case FileSystemType.BitLocker: return Color.FromArgb(217, 119, 6);
                case FileSystemType.APFS: return Color.FromArgb(14, 165, 233);
                case FileSystemType.HFSPlus: return Color.FromArgb(99, 102, 241);
                case FileSystemType.HFS: return Color.FromArgb(139, 92, 246);
                case FileSystemType.EfiSystem: return Color.FromArgb(168, 85, 247);
                case FileSystemType.MicrosoftReserved: return Color.FromArgb(100, 116, 139);
                case FileSystemType.WindowsRecovery: return Color.FromArgb(220, 38, 38);
                case FileSystemType.Extended: return Color.FromArgb(8, 145, 178);
                default: return Color.FromArgb(71, 85, 105);
            }
        }

        public static Color GetFillColor(FileSystemType fs)
        {
            Color border = GetBorderColor(fs);
            if (WinParted.UI.ThemeManager.IsDarkMode)
            {
                if (fs == FileSystemType.Unallocated)
                    return Color.FromArgb(36, 41, 52);
                const int dr = 32, dg = 37, db = 47;
                return Color.FromArgb(
                    dr + (border.R - dr) * 35 / 100,
                    dg + (border.G - dg) * 35 / 100,
                    db + (border.B - db) * 35 / 100);
            }

            if (fs == FileSystemType.Unallocated)
                return Color.FromArgb(241, 245, 249);
            return Color.FromArgb(
                Math.Min(255, border.R + (255 - border.R) * 3 / 4),
                Math.Min(255, border.G + (255 - border.G) * 3 / 4),
                Math.Min(255, border.B + (255 - border.B) * 3 / 4));
        }

        public static Color GetUsedColor(FileSystemType fs)
        {
            Color border = GetBorderColor(fs);
            if (WinParted.UI.ThemeManager.IsDarkMode)
            {
                const int dr = 32, dg = 37, db = 47;
                return Color.FromArgb(
                    dr + (border.R - dr) * 62 / 100,
                    dg + (border.G - dg) * 62 / 100,
                    db + (border.B - db) * 62 / 100);
            }

            return Color.FromArgb(
                Math.Min(255, border.R + (255 - border.R) * 2 / 5),
                Math.Min(255, border.G + (255 - border.G) * 2 / 5),
                Math.Min(255, border.B + (255 - border.B) * 2 / 5));
        }

        public static string GetDisplayName(FileSystemType fs)
        {
            switch (fs)
            {
                case FileSystemType.Unallocated: return "unallocated";
                case FileSystemType.NTFS: return "ntfs";
                case FileSystemType.FAT32: return "fat32";
                case FileSystemType.FAT16: return "fat16";
                case FileSystemType.exFAT: return "exfat";
                case FileSystemType.ReFS: return "refs";
                case FileSystemType.Ext2: return "ext2";
                case FileSystemType.Ext3: return "ext3";
                case FileSystemType.Ext4: return "ext4";
                case FileSystemType.Btrfs: return "btrfs";
                case FileSystemType.XFS: return "xfs";
                case FileSystemType.F2FS: return "f2fs";
                case FileSystemType.ZFS: return "zfs";
                case FileSystemType.SquashFS: return "squashfs";
                case FileSystemType.ISO9660: return "iso9660";
                case FileSystemType.UDF: return "udf";
                case FileSystemType.LinuxSwap: return "linux-swap";
                case FileSystemType.LVM2: return "lvm2 pv";
                case FileSystemType.BitLocker: return "bitlocker";
                case FileSystemType.APFS: return "apfs";
                case FileSystemType.HFSPlus: return "hfs+";
                case FileSystemType.HFS: return "hfs";
                case FileSystemType.EfiSystem: return "fat32 (ESP)";
                case FileSystemType.MicrosoftReserved: return "msftres";
                case FileSystemType.WindowsRecovery: return "ntfs (WinRE)";
                case FileSystemType.Extended: return "extended";
                default: return "unknown / raw";
            }
        }
    }

    public class PartitionEntry
    {
        public PartitionEntry()
        {
            LogicalSectorSize = 512;
            PhysicalSectorSize = 4096;
            FileSystem = FileSystemType.RAW;
            DriveLetter = string.Empty;
            VolumeGuidPath = string.Empty;
            Label = string.Empty;
            PartitionName = string.Empty;
            GptTypeGuid = Guid.Empty;
            GptPartitionGuid = Guid.Empty;
            UUID = string.Empty;
            FlagsSummary = string.Empty;
        }

        public int Index { get; set; }
        public int PartitionNumber { get; set; }
        public long StartingOffset { get; set; }
        public long PartitionLength { get; set; }
        public int LogicalSectorSize { get; set; }
        public int PhysicalSectorSize { get; set; }

        public FileSystemType FileSystem { get; set; }
        public string DriveLetter { get; set; }
        public string VolumeGuidPath { get; set; }
        public string Label { get; set; }
        public string PartitionName { get; set; }

        public Guid GptTypeGuid { get; set; }
        public Guid GptPartitionGuid { get; set; }
        public ulong GptAttributes { get; set; }

        public byte MbrPartitionType { get; set; }
        public bool MbrBootable { get; set; }

        public long? UsedBytes { get; set; }
        public long? FreeBytes { get; set; }
        public string UUID { get; set; }
        public string FlagsSummary { get; set; }
        public bool IsSystemOrBoot { get; set; }
        public bool IsMounted { get; set; }
        public int DiskNumber { get; set; }

        public long EndOffset { get { return StartingOffset + PartitionLength; } }
        public long StartLba { get { return LogicalSectorSize > 0 ? StartingOffset / LogicalSectorSize : 0; } }
        public long EndLba { get { return LogicalSectorSize > 0 ? Math.Max(StartLba, (StartingOffset + PartitionLength) / LogicalSectorSize - 1) : 0; } }
        public long SectorCount { get { return LogicalSectorSize > 0 ? PartitionLength / LogicalSectorSize : 0; } }

        public bool IsUnallocated { get { return FileSystem == FileSystemType.Unallocated; } }
        public bool IsAligned4K { get { return PhysicalSectorSize <= 0 || (StartingOffset % PhysicalSectorSize == 0); } }
        public bool IsAligned1MiB { get { return StartingOffset % 1048576L == 0; } }

        public string LinuxStyleDeviceName
        {
            get
            {
                char diskChar = (char)('a' + Math.Max(0, Math.Min(25, DiskNumber)));
                if (IsUnallocated)
                    return "unallocated";
                return string.Format("/dev/sd{0}{1} (Disk {2}, Part {3})", diskChar, PartitionNumber, DiskNumber, PartitionNumber);
            }
        }

        public string ShortDeviceName
        {
            get
            {
                if (IsUnallocated) return "unallocated";
                if (!string.IsNullOrEmpty(DriveLetter))
                    return string.Format("Part #{0} ({1})", PartitionNumber, DriveLetter);
                return string.Format("Part #{0}", PartitionNumber);
            }
        }

        public double UsedFraction
        {
            get
            {
                if (IsUnallocated || PartitionLength <= 0 || !UsedBytes.HasValue) return 0.0;
                double frac = (double)UsedBytes.Value / PartitionLength;
                if (frac < 0.0) return 0.0;
                if (frac > 1.0) return 1.0;
                return frac;
            }
        }

        public PartitionEntry Clone()
        {
            return (PartitionEntry)MemberwiseClone();
        }
    }

    public class DiskInfo
    {
        public DiskInfo()
        {
            DevicePath = string.Empty;
            BackingFilePath = string.Empty;
            Model = "Unknown Disk";
            Vendor = string.Empty;
            SerialNumber = string.Empty;
            FirmwareRevision = string.Empty;
            BusTypeName = "Unknown";
            PartitionStyle = PartitionTableStyle.Unknown;
            LogicalSectorSize = 512;
            PhysicalSectorSize = 4096;
            GptDiskGuid = Guid.Empty;
            MaxPartitions = 128;
            GptCrcValid = true;
            Partitions = new List<PartitionEntry>();
        }

        public int DiskNumber { get; set; }
        public string DevicePath { get; set; }
        public bool IsVirtualImage { get; set; }
        public string BackingFilePath { get; set; }

        public string Model { get; set; }
        public string Vendor { get; set; }
        public string SerialNumber { get; set; }
        public string FirmwareRevision { get; set; }
        public string BusTypeName { get; set; }

        public PartitionTableStyle PartitionStyle { get; set; }
        public long TotalSizeBytes { get; set; }
        public int LogicalSectorSize { get; set; }
        public int PhysicalSectorSize { get; set; }

        public uint MbrSignature { get; set; }
        public Guid GptDiskGuid { get; set; }
        public int MaxPartitions { get; set; }
        public bool IsReadOnly { get; set; }
        public bool IsSystemDisk { get; set; }
        public bool GptCrcValid { get; set; }
        public bool CanRawRead { get; set; }

        public List<PartitionEntry> Partitions { get; set; }

        public string DisplayName
        {
            get
            {
                string kind = IsVirtualImage ? "[Virtual Disk]" : string.Format("Disk {0}", DiskNumber);
                return string.Format("{0} — {1} ({2})", kind, Model, ByteFormatter.FormatBytes(TotalSizeBytes));
            }
        }

        public override string ToString()
        {
            return DisplayName;
        }

        public long UsableFirstOffset
        {
            get
            {
                if (PartitionStyle == PartitionTableStyle.GPT)
                    return 34L * LogicalSectorSize;
                if (PartitionStyle == PartitionTableStyle.MBR)
                    return Math.Max(LogicalSectorSize, 1048576L);
                return LogicalSectorSize;
            }
        }

        public long UsableLastOffset
        {
            get
            {
                if (PartitionStyle == PartitionTableStyle.GPT)
                    return Math.Max(0, TotalSizeBytes - 33L * LogicalSectorSize);
                return TotalSizeBytes;
            }
        }

        public List<PartitionEntry> GetFullLayoutWithUnallocated()
        {
            var result = new List<PartitionEntry>();
            long startBoundary = PartitionStyle == PartitionTableStyle.GPT
                ? 34L * LogicalSectorSize
                : (PartitionStyle == PartitionTableStyle.MBR ? (long)LogicalSectorSize : 0L);

            long endBoundary = PartitionStyle == PartitionTableStyle.GPT
                ? Math.Max(startBoundary, TotalSizeBytes - 33L * LogicalSectorSize)
                : TotalSizeBytes;

            var sorted = Partitions
                .Where(p => !p.IsUnallocated && p.PartitionLength > 0)
                .OrderBy(p => p.StartingOffset)
                .ToList();

            long cursor = startBoundary;
            int idx = 0;

            foreach (var p in sorted)
            {
                if (p.StartingOffset > cursor)
                {
                    long gap = p.StartingOffset - cursor;
                    if (gap >= 1048576)
                    {
                        result.Add(new PartitionEntry
                        {
                            Index = idx++,
                            PartitionNumber = 0,
                            DiskNumber = DiskNumber,
                            StartingOffset = cursor,
                            PartitionLength = gap,
                            LogicalSectorSize = LogicalSectorSize,
                            PhysicalSectorSize = PhysicalSectorSize,
                            FileSystem = FileSystemType.Unallocated,
                            Label = "Unallocated"
                        });
                    }
                }

                var copy = p.Clone();
                copy.Index = idx++;
                copy.DiskNumber = DiskNumber;
                copy.LogicalSectorSize = LogicalSectorSize;
                copy.PhysicalSectorSize = PhysicalSectorSize;
                result.Add(copy);

                if (p.EndOffset > cursor)
                    cursor = p.EndOffset;
            }

            if (endBoundary > cursor)
            {
                long trailingGap = endBoundary - cursor;
                if (trailingGap >= 1048576 || result.Count == 0)
                {
                    result.Add(new PartitionEntry
                    {
                        Index = idx++,
                        PartitionNumber = 0,
                        DiskNumber = DiskNumber,
                        StartingOffset = cursor,
                        PartitionLength = Math.Max(0, trailingGap),
                        LogicalSectorSize = LogicalSectorSize,
                        PhysicalSectorSize = PhysicalSectorSize,
                        FileSystem = FileSystemType.Unallocated,
                        Label = "Unallocated"
                    });
                }
            }

            return result;
        }

        public DiskInfo Clone()
        {
            var copy = (DiskInfo)MemberwiseClone();
            copy.Partitions = Partitions.Select(p => p.Clone()).ToList();
            return copy;
        }
    }

    public class PendingOperation
    {
        public PendingOperation()
        {
            Id = Guid.NewGuid();
            TargetDiskPath = string.Empty;
            Summary = string.Empty;
            DetailDescription = string.Empty;
            DiskPartScript = string.Empty;
            RiskLevel = "Medium";
            NewLabel = string.Empty;
            NewPartitionName = string.Empty;
            NewGptTypeGuid = Guid.Empty;
            SourceDiskPath = string.Empty;
            BackupFilePath = string.Empty;
        }

        public Guid Id { get; set; }
        public OperationType Type { get; set; }
        public int TargetDiskNumber { get; set; }
        public string TargetDiskPath { get; set; }
        public string Summary { get; set; }
        public string DetailDescription { get; set; }
        public string DiskPartScript { get; set; }
        public string RiskLevel { get; set; }

        public int TargetPartitionNumber { get; set; }
        public long StartOffset { get; set; }
        public long Length { get; set; }
        public long OriginalStartOffset { get; set; }
        public long OriginalLength { get; set; }

        public PartitionTableStyle NewTableStyle { get; set; }
        public FileSystemType NewFileSystem { get; set; }
        public string NewLabel { get; set; }
        public string NewPartitionName { get; set; }
        public Guid NewGptTypeGuid { get; set; }
        public ulong NewGptAttributes { get; set; }
        public byte NewMbrType { get; set; }
        public bool NewMbrBoot { get; set; }
        public WipePattern WipePattern { get; set; }

        public string SourceDiskPath { get; set; }
        public int SourceDiskNumber { get; set; }
        public long SourceStartOffset { get; set; }
        public long SourceLength { get; set; }
        public string BackupFilePath { get; set; }

        public Func<bool> ExecuteCallback { get; set; }
    }

    public static class ByteFormatter
    {
        public static string FormatBytes(long bytes)
        {
            if (bytes < 0) return "0 B";
            string[] units = { "B", "KiB", "MiB", "GiB", "TiB", "PiB" };
            double val = bytes;
            int u = 0;
            while (val >= 1024.0 && u < units.Length - 1)
            {
                val /= 1024.0;
                u++;
            }
            if (u == 0) return string.Format("{0} B", bytes);
            return string.Format("{0:0.##} {1}", val, units[u]);
        }

        public static string FormatMiB(long bytes)
        {
            return string.Format("{0:N2} MiB", bytes / 1048576.0);
        }

        public static long AlignDown(long value, long alignment)
        {
            if (alignment <= 1) return value;
            return (value / alignment) * alignment;
        }

        public static long AlignUp(long value, long alignment)
        {
            if (alignment <= 1) return value;
            long rem = value % alignment;
            return rem == 0 ? value : value + (alignment - rem);
        }
    }
}
