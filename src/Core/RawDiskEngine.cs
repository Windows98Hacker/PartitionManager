using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using WinParted.Interop;
using WinParted.Models;

namespace WinParted.Core
{
    /// <summary>
    /// IEEE 802.3 CRC32 implementation used for UEFI GPT header/entry-array checksums and backup files.
    /// </summary>
    public static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        private static uint[] BuildTable()
        {
            uint[] t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
                t[i] = c;
            }
            return t;
        }

        /// <summary>Computes the CRC32 digest over a byte slice.</summary>
        public static uint Compute(byte[] data, int offset, int length)
        {
            uint crc = 0xFFFFFFFFu;
            for (int i = 0; i < length; i++)
                crc = Table[(crc ^ data[offset + i]) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }
    }

    /// <summary>
    /// Core raw-sector I/O, GPT/MBR partition table codec, safety validator, and multi-OS superblock
    /// inspection/formatting/resizing engine. Operates identically against virtual disk images (<c>.img</c>)
    /// and raw physical drives (via <see cref="Win32DiskInterop"/>).
    /// </summary>
    public static class RawDiskEngine
    {
        private const ulong GPT_SIGNATURE = 0x5452415020494645UL; // "EFI PART"

        /// <summary>
        /// Reads <paramref name="length"/> bytes starting at <paramref name="offset"/> from either
        /// a virtual image file or a physical drive handle.
        /// </summary>
        public static byte[] ReadBytes(DiskInfo disk, long offset, int length)
        {
            if (disk.IsVirtualImage && !string.IsNullOrEmpty(disk.BackingFilePath))
            {
                using (var fs = new FileStream(disk.BackingFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (offset >= fs.Length) return new byte[0];
                    int toRead = (int)Math.Min(length, fs.Length - offset);
                    byte[] buf = new byte[toRead];
                    fs.Seek(offset, SeekOrigin.Begin);
                    int total = 0;
                    while (total < toRead)
                    {
                        int n = fs.Read(buf, total, toRead - total);
                        if (n <= 0) break;
                        total += n;
                    }
                    return buf;
                }
            }

            return Win32DiskInterop.ReadRawDiskSectors(disk.DevicePath, disk.DiskNumber, offset, length);
        }

        /// <summary>
        /// Writes <paramref name="data"/> at <paramref name="offset"/> to a virtual image file or physical drive.
        /// </summary>
        public static void WriteBytes(DiskInfo disk, long offset, byte[] data)
        {
            if (disk.IsVirtualImage && !string.IsNullOrEmpty(disk.BackingFilePath))
            {
                using (var fs = new FileStream(disk.BackingFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                {
                    fs.Seek(offset, SeekOrigin.Begin);
                    fs.Write(data, 0, data.Length);
                    fs.Flush();
                }
                return;
            }

            Win32DiskInterop.WriteRawDiskSectors(disk.DevicePath, disk.DiskNumber, offset, data, disk.LogicalSectorSize);
        }

        /// <summary>
        /// Opens a virtual raw disk image (<c>.img</c>), parses its GPT/MBR table, and probes all
        /// partition superblocks to reconstruct a complete <see cref="DiskInfo"/> model.
        /// </summary>
        public static DiskInfo LoadVirtualDisk(string filePath, int diskNumber)
        {
            var fi = new FileInfo(filePath);
            if (!fi.Exists)
                throw new FileNotFoundException("Virtual disk image not found.", filePath);

            var disk = new DiskInfo
            {
                DiskNumber = diskNumber,
                DevicePath = filePath,
                BackingFilePath = filePath,
                IsVirtualImage = true,
                Model = Path.GetFileName(filePath),
                Vendor = "WinParted",
                BusTypeName = "Virtual Image (.img)",
                FirmwareRevision = "1.0",
                SerialNumber = "VIMG-" + Math.Abs(filePath.GetHashCode()).ToString("X8"),
                TotalSizeBytes = fi.Length,
                LogicalSectorSize = 512,
                PhysicalSectorSize = 4096,
                CanRawRead = true,
                IsSystemDisk = false
            };

            ParseDiskPartitionTable(disk);
            foreach (var p in disk.Partitions)
                InspectPartitionSuperblock(disk, p);

            return disk;
        }

        /// <summary>Creates a blank virtual disk image file initialized with an empty GPT, MBR, or RAW header.</summary>
        public static DiskInfo CreateBlankVirtualDisk(string filePath, long sizeBytes, PartitionTableStyle style)
        {
            long alignedSize = Math.Max(4L * 1048576L, ByteFormatter.AlignUp(sizeBytes, 512));
            if (File.Exists(filePath)) File.Delete(filePath);
            using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                fs.SetLength(alignedSize);
            }

            var disk = new DiskInfo
            {
                DiskNumber = 99,
                DevicePath = filePath,
                BackingFilePath = filePath,
                IsVirtualImage = true,
                Model = Path.GetFileName(filePath),
                Vendor = "WinParted",
                BusTypeName = "Virtual Image (.img)",
                TotalSizeBytes = alignedSize,
                LogicalSectorSize = 512,
                PhysicalSectorSize = 4096,
                PartitionStyle = style,
                GptDiskGuid = Guid.NewGuid(),
                MbrSignature = (uint)new Random().Next(1, int.MaxValue),
                CanRawRead = true
            };

            if (style == PartitionTableStyle.GPT || style == PartitionTableStyle.MBR)
                WritePartitionTable(disk);

            return LoadVirtualDisk(filePath, disk.DiskNumber);
        }

        /// <summary>
        /// Generates a 128 MiB multi-OS GPT virtual disk containing EFI (FAT32), NTFS, Ext4, and Linux Swap
        /// partitions plus trailing unallocated space for safe testing and experimentation.
        /// </summary>
        public static DiskInfo CreateDemoVirtualDisk(string filePath, int diskNumber = 90)
        {
            ReadOnlyFileSystemMountEngine.PurgeDiskOverlaysAndCache(diskNumber);
            if (File.Exists(filePath)) File.Delete(filePath);
            long totalSize = 128L * 1048576L; // 128 MiB
            using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                fs.SetLength(totalSize);
            }

            var disk = new DiskInfo
            {
                DiskNumber = diskNumber,
                DevicePath = filePath,
                BackingFilePath = filePath,
                IsVirtualImage = true,
                Model = "WinParted Sandbox Multi-OS Disk (128 MiB)",
                Vendor = "WinParted",
                BusTypeName = "Virtual Image (.img)",
                TotalSizeBytes = totalSize,
                LogicalSectorSize = 512,
                PhysicalSectorSize = 4096,
                PartitionStyle = PartitionTableStyle.GPT,
                GptDiskGuid = new Guid("a1b2c3d4-1122-4455-8899-aabbccddeeff"),
                MbrSignature = 0x57505444,
                CanRawRead = true,
                MaxPartitions = 128
            };

            long miB = 1048576L;
            disk.Partitions.Add(new PartitionEntry
            {
                DiskNumber = diskNumber,
                PartitionNumber = 1,
                StartingOffset = 1 * miB,
                PartitionLength = 16 * miB,
                LogicalSectorSize = 512,
                PhysicalSectorSize = 4096,
                FileSystem = FileSystemType.EfiSystem,
                PartitionName = "EFI system partition",
                Label = "ESP",
                GptTypeGuid = GptPartitionTypeGuids.EfiSystem,
                GptPartitionGuid = Guid.NewGuid(),
                FlagsSummary = "boot, esp"
            });

            disk.Partitions.Add(new PartitionEntry
            {
                DiskNumber = diskNumber,
                PartitionNumber = 2,
                StartingOffset = 17 * miB,
                PartitionLength = 32 * miB,
                LogicalSectorSize = 512,
                PhysicalSectorSize = 4096,
                FileSystem = FileSystemType.NTFS,
                PartitionName = "Basic data partition",
                Label = "WinSystem",
                GptTypeGuid = GptPartitionTypeGuids.BasicData,
                GptPartitionGuid = Guid.NewGuid(),
                FlagsSummary = "msftdata"
            });

            disk.Partitions.Add(new PartitionEntry
            {
                DiskNumber = diskNumber,
                PartitionNumber = 3,
                StartingOffset = 49 * miB,
                PartitionLength = 24 * miB,
                LogicalSectorSize = 512,
                PhysicalSectorSize = 4096,
                FileSystem = FileSystemType.Ext4,
                PartitionName = "Linux filesystem",
                Label = "ubuntu-root",
                GptTypeGuid = GptPartitionTypeGuids.LinuxFilesystem,
                GptPartitionGuid = Guid.NewGuid()
            });

            disk.Partitions.Add(new PartitionEntry
            {
                DiskNumber = diskNumber,
                PartitionNumber = 4,
                StartingOffset = 73 * miB,
                PartitionLength = 8 * miB,
                LogicalSectorSize = 512,
                PhysicalSectorSize = 4096,
                FileSystem = FileSystemType.LinuxSwap,
                PartitionName = "Linux swap",
                Label = "swap",
                GptTypeGuid = GptPartitionTypeGuids.LinuxSwap,
                GptPartitionGuid = Guid.NewGuid()
            });
            // Remaining ~47 MiB (81 MiB to ~127.9 MiB) stays Unallocated!

            WritePartitionTable(disk);

            foreach (var p in disk.Partitions)
                WriteFileSystemHeader(disk, p);

            return LoadVirtualDisk(filePath, diskNumber);
        }

        /// <summary>
        /// Reads LBA 0..33 to determine GPT vs MBR vs RAW layout, validates GPT header/entry CRC32 digests,
        /// and populates <see cref="DiskInfo.Partitions"/>.
        /// </summary>
        public static void ParseDiskPartitionTable(DiskInfo disk)
        {
            disk.Partitions.Clear();
            byte[] head = ReadBytes(disk, 0, 34 * disk.LogicalSectorSize);
            if (head.Length < 512)
            {
                disk.PartitionStyle = PartitionTableStyle.Unknown;
                return;
            }

            // Check MBR signature 0x55AA
            bool hasMbrSig = head[510] == 0x55 && head[511] == 0xAA;
            disk.MbrSignature = BitConverter.ToUInt32(head, 440);

            bool isProtectiveMbr = false;
            if (hasMbrSig)
            {
                for (int i = 0; i < 4; i++)
                {
                    if (head[446 + i * 16 + 4] == 0xEE)
                    {
                        isProtectiveMbr = true;
                        break;
                    }
                }
            }

            // Check GPT Header at LBA 1
            int lba1 = disk.LogicalSectorSize;
            if (head.Length >= lba1 + 92 && BitConverter.ToUInt64(head, lba1) == GPT_SIGNATURE)
            {
                disk.PartitionStyle = PartitionTableStyle.GPT;
                uint headerSize = BitConverter.ToUInt32(head, lba1 + 12);
                uint savedHeaderCrc = BitConverter.ToUInt32(head, lba1 + 16);

                byte[] headerCopy = new byte[Math.Min((int)headerSize, 512)];
                Array.Copy(head, lba1, headerCopy, 0, headerCopy.Length);
                Array.Clear(headerCopy, 16, 4);
                uint calcHeaderCrc = Crc32.Compute(headerCopy, 0, headerCopy.Length);

                byte[] gBytes = new byte[16];
                Array.Copy(head, lba1 + 56, gBytes, 0, 16);
                disk.GptDiskGuid = new Guid(gBytes);

                long entriesStartLba = BitConverter.ToInt64(head, lba1 + 72);
                int numEntries = BitConverter.ToInt32(head, lba1 + 80);
                int entrySize = BitConverter.ToInt32(head, lba1 + 84);
                uint savedArrayCrc = BitConverter.ToUInt32(head, lba1 + 88);
                disk.MaxPartitions = numEntries > 0 ? numEntries : 128;

                int arrayByteLen = numEntries * entrySize;
                byte[] entriesBuf = ReadBytes(disk, entriesStartLba * disk.LogicalSectorSize, arrayByteLen);
                uint calcArrayCrc = entriesBuf.Length == arrayByteLen
                    ? Crc32.Compute(entriesBuf, 0, arrayByteLen)
                    : 0;

                disk.GptCrcValid = (savedHeaderCrc == calcHeaderCrc) && (savedArrayCrc == calcArrayCrc);

                int partNum = 1;
                for (int i = 0; i < numEntries && (i + 1) * entrySize <= entriesBuf.Length; i++)
                {
                    int off = i * entrySize;
                    byte[] typeBytes = new byte[16];
                    Array.Copy(entriesBuf, off, typeBytes, 0, 16);
                    Guid typeGuid = new Guid(typeBytes);
                    if (typeGuid == Guid.Empty) continue;

                    byte[] partBytes = new byte[16];
                    Array.Copy(entriesBuf, off + 16, partBytes, 0, 16);
                    Guid partGuid = new Guid(partBytes);

                    long firstLba = BitConverter.ToInt64(entriesBuf, off + 32);
                    long lastLba = BitConverter.ToInt64(entriesBuf, off + 40);
                    ulong attrs = BitConverter.ToUInt64(entriesBuf, off + 48);
                    string name = Encoding.Unicode.GetString(entriesBuf, off + 56, Math.Min(72, entrySize - 56)).TrimEnd('\0');

                    if (lastLba < firstLba) continue;

                    disk.Partitions.Add(new PartitionEntry
                    {
                        DiskNumber = disk.DiskNumber,
                        PartitionNumber = partNum++,
                        StartingOffset = firstLba * disk.LogicalSectorSize,
                        PartitionLength = (lastLba - firstLba + 1) * disk.LogicalSectorSize,
                        LogicalSectorSize = disk.LogicalSectorSize,
                        PhysicalSectorSize = disk.PhysicalSectorSize,
                        GptTypeGuid = typeGuid,
                        GptPartitionGuid = partGuid,
                        GptAttributes = attrs,
                        PartitionName = name,
                        FileSystem = Win32DiskInterop.GuessFsFromGptType(typeGuid),
                        FlagsSummary = Win32DiskInterop.FormatGptFlags(typeGuid, attrs),
                        UUID = partGuid.ToString("D")
                    });
                }
                return;
            }

            if (hasMbrSig && !isProtectiveMbr)
            {
                disk.PartitionStyle = PartitionTableStyle.MBR;
                disk.MaxPartitions = 4;
                int partNum = 1;
                for (int i = 0; i < 4; i++)
                {
                    int off = 446 + i * 16;
                    byte boot = head[off];
                    byte pType = head[off + 4];
                    uint startLba = BitConverter.ToUInt32(head, off + 8);
                    uint totalSectors = BitConverter.ToUInt32(head, off + 12);

                    if (pType == 0 || totalSectors == 0) continue;

                    disk.Partitions.Add(new PartitionEntry
                    {
                        DiskNumber = disk.DiskNumber,
                        PartitionNumber = partNum++,
                        StartingOffset = (long)startLba * disk.LogicalSectorSize,
                        PartitionLength = (long)totalSectors * disk.LogicalSectorSize,
                        LogicalSectorSize = disk.LogicalSectorSize,
                        PhysicalSectorSize = disk.PhysicalSectorSize,
                        MbrPartitionType = pType,
                        MbrBootable = boot == 0x80,
                        FileSystem = Win32DiskInterop.GuessFsFromMbrType(pType),
                        FlagsSummary = boot == 0x80 ? "boot" : ""
                    });
                }
                return;
            }

            disk.PartitionStyle = PartitionTableStyle.RAW;
        }

        /// <summary>
        /// Pre-flight layout safety validator: rejects overlapping partitions, out-of-bounds LBA ranges,
        /// encroaching on Primary/Backup GPT sectors (LBA 0..33 and last 33 sectors), duplicate GPT GUIDs,
        /// and exceeding MBR's 4-slot primary table limit.
        /// </summary>
        public static bool ValidatePartitionTable(DiskInfo disk, out string errorMessage)
        {
            errorMessage = string.Empty;
            if (disk == null)
            {
                errorMessage = "Disk is null.";
                return false;
            }
            int sec = disk.LogicalSectorSize > 0 ? disk.LogicalSectorSize : 512;
            if (disk.TotalSizeBytes < 65536)
            {
                errorMessage = "Disk size is too small for a valid partition table.";
                return false;
            }

            var active = disk.Partitions
                .Where(p => !p.IsUnallocated && p.PartitionLength > 0)
                .OrderBy(p => p.StartingOffset)
                .ToList();

            if (disk.PartitionStyle == PartitionTableStyle.MBR)
            {
                if (active.Count > 4)
                {
                    errorMessage = string.Format("MBR partition table supports at most 4 primary partitions (attempted {0}).", active.Count);
                    return false;
                }
                for (int i = 0; i < active.Count; i++)
                {
                    var p = active[i];
                    if (p.StartingOffset < sec)
                    {
                        errorMessage = string.Format("Partition #{0} starts at offset 0x{1:X}, overlapping MBR sector LBA 0.", p.PartitionNumber, p.StartingOffset);
                        return false;
                    }
                    if (p.EndOffset > disk.TotalSizeBytes)
                    {
                        errorMessage = string.Format("Partition #{0} extends past end of disk ({1} > {2}).", p.PartitionNumber, p.EndOffset, disk.TotalSizeBytes);
                        return false;
                    }
                    if (i > 0 && active[i - 1].EndOffset > p.StartingOffset)
                    {
                        errorMessage = string.Format("Partition #{0} overlaps with Partition #{1}.", active[i - 1].PartitionNumber, p.PartitionNumber);
                        return false;
                    }
                }
                return true;
            }

            if (disk.PartitionStyle == PartitionTableStyle.GPT)
            {
                if (active.Count > 128)
                {
                    errorMessage = string.Format("GPT partition table supports at most 128 entries (attempted {0}).", active.Count);
                    return false;
                }
                long firstUsable = 34L * sec;
                long lastUsableEnd = disk.TotalSizeBytes - 33L * sec;
                var seenGuids = new HashSet<Guid>();

                for (int i = 0; i < active.Count; i++)
                {
                    var p = active[i];
                    if (p.StartingOffset < firstUsable)
                    {
                        errorMessage = string.Format("Partition #{0} starts at 0x{1:X}, encroaching on Primary GPT header/entries (first usable 0x{2:X}).", p.PartitionNumber, p.StartingOffset, firstUsable);
                        return false;
                    }
                    if (p.EndOffset > lastUsableEnd)
                    {
                        errorMessage = string.Format("Partition #{0} ends at 0x{1:X}, encroaching on Backup GPT header/entries (last usable 0x{2:X}).", p.PartitionNumber, p.EndOffset, lastUsableEnd);
                        return false;
                    }
                    if (i > 0 && active[i - 1].EndOffset > p.StartingOffset)
                    {
                        errorMessage = string.Format("Partition #{0} (ends 0x{1:X}) overlaps with Partition #{2} (starts 0x{3:X}).",
                            active[i - 1].PartitionNumber, active[i - 1].EndOffset, p.PartitionNumber, p.StartingOffset);
                        return false;
                    }
                    if (p.GptPartitionGuid != Guid.Empty && !seenGuids.Add(p.GptPartitionGuid))
                    {
                        errorMessage = string.Format("Duplicate GPT Partition GUID {0} detected.", p.GptPartitionGuid);
                        return false;
                    }
                }
                return true;
            }

            return true;
        }

        /// <summary>Verifies LBA 0 signature, Primary &amp; Backup GPT headers/entry arrays, CRC32s, and non-overlap rules.</summary>
        public static bool VerifyDiskPartitionTableIntegrity(DiskInfo disk, out string report)
        {
            var sb = new StringBuilder();
            int sec = disk.LogicalSectorSize > 0 ? disk.LogicalSectorSize : 512;
            long totalSectors = disk.TotalSizeBytes / sec;

            byte[] mbr = ReadBytes(disk, 0, sec);
            if (mbr.Length < 512 || mbr[510] != 0x55 || mbr[511] != 0xAA)
            {
                report = "Missing 0x55AA boot signature in LBA 0.";
                return false;
            }

            if (disk.PartitionStyle == PartitionTableStyle.MBR)
            {
                string valErr;
                bool ok = ValidatePartitionTable(disk, out valErr);
                report = ok ? "MBR signature 0x55AA and 4-slot layout valid." : valErr;
                return ok;
            }

            if (disk.PartitionStyle != PartitionTableStyle.GPT)
            {
                report = "Disk is RAW/unpartitioned.";
                return true;
            }

            // Verify Protective MBR
            bool hasProt = false;
            for (int i = 0; i < 4; i++)
            {
                if (mbr[446 + i * 16 + 4] == 0xEE) { hasProt = true; break; }
            }
            if (!hasProt)
            {
                report = "Protective MBR (0xEE) missing at LBA 0.";
                return false;
            }

            // Primary GPT Header (LBA 1) + Array (LBA 2..33)
            byte[] pHdr = ReadBytes(disk, sec, sec);
            if (pHdr.Length < 92 || BitConverter.ToUInt64(pHdr, 0) != GPT_SIGNATURE)
            {
                report = "Primary GPT header signature missing at LBA 1.";
                return false;
            }
            uint pSavedHdrCrc = BitConverter.ToUInt32(pHdr, 16);
            byte[] pHdrCopy = (byte[])pHdr.Clone();
            Array.Clear(pHdrCopy, 16, 4);
            uint pCalcHdrCrc = Crc32.Compute(pHdrCopy, 0, 92);
            if (pSavedHdrCrc != pCalcHdrCrc)
            {
                report = string.Format("Primary GPT header CRC32 mismatch (saved 0x{0:X8} != computed 0x{1:X8}).", pSavedHdrCrc, pCalcHdrCrc);
                return false;
            }

            int numEntries = BitConverter.ToInt32(pHdr, 80);
            int entrySize = BitConverter.ToInt32(pHdr, 84);
            uint pSavedArrCrc = BitConverter.ToUInt32(pHdr, 88);
            byte[] pArr = ReadBytes(disk, 2L * sec, numEntries * entrySize);
            uint pCalcArrCrc = Crc32.Compute(pArr, 0, pArr.Length);
            if (pSavedArrCrc != pCalcArrCrc)
            {
                report = string.Format("Primary GPT entry array CRC32 mismatch (saved 0x{0:X8} != computed 0x{1:X8}).", pSavedArrCrc, pCalcArrCrc);
                return false;
            }

            // Backup GPT Header (Last LBA) + Array (Last LBA - 32)
            long backupHdrLba = totalSectors - 1;
            long backupArrLba = totalSectors - 33;
            byte[] bHdr = ReadBytes(disk, backupHdrLba * sec, sec);
            if (bHdr.Length < 92 || BitConverter.ToUInt64(bHdr, 0) != GPT_SIGNATURE)
            {
                report = "Backup GPT header signature missing at Last LBA.";
                return false;
            }
            uint bSavedHdrCrc = BitConverter.ToUInt32(bHdr, 16);
            byte[] bHdrCopy = (byte[])bHdr.Clone();
            Array.Clear(bHdrCopy, 16, 4);
            uint bCalcHdrCrc = Crc32.Compute(bHdrCopy, 0, 92);
            if (bSavedHdrCrc != bCalcHdrCrc)
            {
                report = "Backup GPT header CRC32 mismatch.";
                return false;
            }

            byte[] bArr = ReadBytes(disk, backupArrLba * sec, numEntries * entrySize);
            uint bCalcArrCrc = Crc32.Compute(bArr, 0, bArr.Length);
            uint bSavedArrCrc = BitConverter.ToUInt32(bHdr, 88);
            if (bSavedArrCrc != bCalcArrCrc || bCalcArrCrc != pCalcArrCrc)
            {
                report = "Backup GPT entry array CRC32 mismatch or out of sync with Primary array.";
                return false;
            }

            string layoutErr;
            if (!ValidatePartitionTable(disk, out layoutErr))
            {
                report = layoutErr;
                return false;
            }

            sb.AppendFormat("GPT Primary & Backup headers and {0}-entry arrays verified (Array CRC32=0x{1:X8}).", numEntries, pCalcArrCrc);
            report = sb.ToString();
            return true;
        }

        /// <summary>Restores the Primary GPT Header (LBA 1) and Array (LBA 2..33) from a verified Backup GPT.</summary>
        public static void RecoverGptFromBackup(DiskInfo disk)
        {
            if (!disk.IsVirtualImage)
                throw new InvalidOperationException("RecoverGptFromBackup direct write is restricted to Virtual Disk images.");

            int sec = disk.LogicalSectorSize > 0 ? disk.LogicalSectorSize : 512;
            long totalSectors = disk.TotalSizeBytes / sec;
            long backupHdrLba = totalSectors - 1;
            long backupArrLba = totalSectors - 33;

            byte[] bHdr = ReadBytes(disk, backupHdrLba * sec, sec);
            if (bHdr.Length < 92 || BitConverter.ToUInt64(bHdr, 0) != GPT_SIGNATURE)
                throw new InvalidDataException("Backup GPT header is missing or invalid.");

            uint savedCrc = BitConverter.ToUInt32(bHdr, 16);
            byte[] bCopy = (byte[])bHdr.Clone();
            Array.Clear(bCopy, 16, 4);
            if (Crc32.Compute(bCopy, 0, 92) != savedCrc)
                throw new InvalidDataException("Backup GPT header CRC32 is also corrupted.");

            int numEntries = BitConverter.ToInt32(bHdr, 80);
            int entrySize = BitConverter.ToInt32(bHdr, 84);
            uint arrCrc = BitConverter.ToUInt32(bHdr, 88);
            byte[] bArr = ReadBytes(disk, backupArrLba * sec, numEntries * entrySize);
            if (Crc32.Compute(bArr, 0, bArr.Length) != arrCrc)
                throw new InvalidDataException("Backup GPT partition entry array CRC32 is corrupted.");

            long firstUsable = BitConverter.ToInt64(bHdr, 40);
            long lastUsable = BitConverter.ToInt64(bHdr, 48);
            byte[] gBytes = new byte[16];
            Array.Copy(bHdr, 56, gBytes, 0, 16);
            Guid diskGuid = new Guid(gBytes);

            byte[] restoredPrimaryHdr = BuildGptHeader(sec, 1, backupHdrLba, firstUsable, lastUsable, diskGuid, 2, numEntries, entrySize, arrCrc);
            WriteBytes(disk, sec, restoredPrimaryHdr);
            WriteBytes(disk, 2L * sec, bArr);
            ParseDiskPartitionTable(disk);
            foreach (var p in disk.Partitions)
                InspectPartitionSuperblock(disk, p);
        }

        /// <summary>
        /// Validates and writes a complete MBR or GPT partition table (Protective MBR + Primary/Backup GPT
        /// headers &amp; entry arrays with freshly computed CRC32 checksums) to disk.
        /// </summary>
        public static void WritePartitionTable(DiskInfo disk, Action<long, byte[]> customWriter = null)
        {
            string valErr;
            if (!ValidatePartitionTable(disk, out valErr))
                throw new InvalidOperationException("Partition table safety check failed: " + valErr);

            Action<long, byte[]> writeFn = customWriter ?? ((off, data) => WriteBytes(disk, off, data));
            int sec = disk.LogicalSectorSize > 0 ? disk.LogicalSectorSize : 512;
            long totalSectors = disk.TotalSizeBytes / sec;

            if (disk.PartitionStyle == PartitionTableStyle.MBR)
            {
                byte[] mbr = new byte[sec];
                if (disk.MbrSignature == 0)
                    disk.MbrSignature = (uint)(Environment.TickCount ^ Guid.NewGuid().GetHashCode()) | 1u;
                BitConverter.GetBytes(disk.MbrSignature).CopyTo(mbr, 440);

                var ordered = disk.Partitions.Where(p => !p.IsUnallocated).OrderBy(p => p.StartingOffset).ToList();
                for (int i = 0; i < ordered.Count; i++)
                {
                    var p = ordered[i];
                    int off = 446 + i * 16;
                    mbr[off] = p.MbrBootable ? (byte)0x80 : (byte)0x00;
                    mbr[off + 1] = 0xFE; mbr[off + 2] = 0xFF; mbr[off + 3] = 0xFF;
                    mbr[off + 4] = p.MbrPartitionType != 0 ? p.MbrPartitionType : GetDefaultMbrType(p.FileSystem);
                    mbr[off + 5] = 0xFE; mbr[off + 6] = 0xFF; mbr[off + 7] = 0xFF;
                    uint startLba = (uint)(p.StartingOffset / sec);
                    uint count = (uint)(p.PartitionLength / sec);
                    BitConverter.GetBytes(startLba).CopyTo(mbr, off + 8);
                    BitConverter.GetBytes(count).CopyTo(mbr, off + 12);
                }
                mbr[510] = 0x55;
                mbr[511] = 0xAA;

                writeFn(0, mbr);
                // Clear GPT LBA 1 signature if switching from GPT to MBR
                writeFn(sec, new byte[sec]);
                return;
            }

            // GPT Mode
            if (disk.GptDiskGuid == Guid.Empty)
                disk.GptDiskGuid = Guid.NewGuid();

            // 1. Protective MBR at LBA 0
            byte[] pmbr = new byte[sec];
            BitConverter.GetBytes(disk.MbrSignature != 0 ? disk.MbrSignature : 0u).CopyTo(pmbr, 440);
            int pOff = 446;
            pmbr[pOff + 1] = 0x00; pmbr[pOff + 2] = 0x02; pmbr[pOff + 3] = 0x00;
            pmbr[pOff + 4] = 0xEE;
            pmbr[pOff + 5] = 0xFF; pmbr[pOff + 6] = 0xFF; pmbr[pOff + 7] = 0xFF;
            BitConverter.GetBytes(1u).CopyTo(pmbr, pOff + 8);
            uint pmbrSectors = (uint)Math.Min((long)uint.MaxValue, totalSectors - 1);
            BitConverter.GetBytes(pmbrSectors).CopyTo(pmbr, pOff + 12);
            pmbr[510] = 0x55;
            pmbr[511] = 0xAA;

            // 2. Partition Entries (128 * 128 = 16384 bytes = 32 sectors)
            int numEntries = 128;
            int entrySize = 128;
            byte[] entries = new byte[numEntries * entrySize];
            var parts = disk.Partitions.Where(p => !p.IsUnallocated && p.PartitionLength > 0).OrderBy(p => p.StartingOffset).Take(numEntries).ToList();

            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                if (p.GptTypeGuid == Guid.Empty)
                    p.GptTypeGuid = GptPartitionTypeGuids.ForFileSystem(p.FileSystem);
                if (p.GptPartitionGuid == Guid.Empty)
                    p.GptPartitionGuid = Guid.NewGuid();

                int eOff = i * entrySize;
                p.GptTypeGuid.ToByteArray().CopyTo(entries, eOff);
                p.GptPartitionGuid.ToByteArray().CopyTo(entries, eOff + 16);
                long firstLba = p.StartingOffset / sec;
                long lastLba = (p.StartingOffset + p.PartitionLength) / sec - 1;
                BitConverter.GetBytes(firstLba).CopyTo(entries, eOff + 32);
                BitConverter.GetBytes(lastLba).CopyTo(entries, eOff + 40);
                BitConverter.GetBytes(p.GptAttributes).CopyTo(entries, eOff + 48);

                string pName = p.PartitionName ?? string.Empty;
                if (pName.Length > 36) pName = pName.Substring(0, 36);
                byte[] nameBytes = Encoding.Unicode.GetBytes(pName);
                Array.Copy(nameBytes, 0, entries, eOff + 56, Math.Min(nameBytes.Length, 72));
            }

            uint entriesCrc = Crc32.Compute(entries, 0, entries.Length);
            long backupHeaderLba = totalSectors - 1;
            long backupEntriesLba = totalSectors - 33;
            long firstUsableLba = 34;
            long lastUsableLba = Math.Max(34, totalSectors - 34);

            // 3. Primary GPT Header (LBA 1)
            byte[] primaryHdr = BuildGptHeader(sec, 1, backupHeaderLba, firstUsableLba, lastUsableLba, disk.GptDiskGuid, 2, numEntries, entrySize, entriesCrc);
            // 4. Backup GPT Header (Last LBA)
            byte[] backupHdr = BuildGptHeader(sec, backupHeaderLba, 1, firstUsableLba, lastUsableLba, disk.GptDiskGuid, backupEntriesLba, numEntries, entrySize, entriesCrc);

            writeFn(0, pmbr);
            writeFn(sec, primaryHdr);
            writeFn(2L * sec, entries);
            if (backupEntriesLba > 34)
            {
                writeFn(backupEntriesLba * sec, entries);
                writeFn(backupHeaderLba * sec, backupHdr);
            }
            disk.GptCrcValid = true;
        }

        private static byte[] BuildGptHeader(int sectorSize, long currentLba, long backupLba, long firstUsable, long lastUsable, Guid diskGuid, long entriesStartLba, int numEntries, int entrySize, uint entriesCrc)
        {
            byte[] hdr = new byte[sectorSize];
            BitConverter.GetBytes(GPT_SIGNATURE).CopyTo(hdr, 0);
            BitConverter.GetBytes(0x00010000u).CopyTo(hdr, 8);   // Revision 1.0
            BitConverter.GetBytes(92u).CopyTo(hdr, 12);          // HeaderSize
            BitConverter.GetBytes(0u).CopyTo(hdr, 16);           // CRC32 placeholder
            BitConverter.GetBytes(0u).CopyTo(hdr, 20);           // Reserved
            BitConverter.GetBytes(currentLba).CopyTo(hdr, 24);
            BitConverter.GetBytes(backupLba).CopyTo(hdr, 32);
            BitConverter.GetBytes(firstUsable).CopyTo(hdr, 40);
            BitConverter.GetBytes(lastUsable).CopyTo(hdr, 48);
            diskGuid.ToByteArray().CopyTo(hdr, 56);
            BitConverter.GetBytes(entriesStartLba).CopyTo(hdr, 72);
            BitConverter.GetBytes(numEntries).CopyTo(hdr, 80);
            BitConverter.GetBytes(entrySize).CopyTo(hdr, 84);
            BitConverter.GetBytes(entriesCrc).CopyTo(hdr, 88);

            uint hdrCrc = Crc32.Compute(hdr, 0, 92);
            BitConverter.GetBytes(hdrCrc).CopyTo(hdr, 16);
            return hdr;
        }

        /// <summary>
        /// Probes the first 128 KiB of a partition to identify its exact on-disk filesystem superblock,
        /// UUID, volume label, and used/free byte counts across 18+ Windows, Linux, Unix, and macOS formats.
        /// Falls back to <see cref="FileSystemType.RAW"/> when no recognized signature matches.
        /// </summary>
        public static void InspectPartitionSuperblock(DiskInfo disk, PartitionEntry part)
        {
            if (part.IsUnallocated || part.PartitionLength < 4096) return;
            if (!disk.IsVirtualImage && !disk.CanRawRead) return;

            FileSystemType priorGuessedFs = part.FileSystem;
            int probeSize = (int)Math.Min(131072, part.PartitionLength);
            byte[] data;
            try
            {
                data = ReadBytes(disk, part.StartingOffset, probeSize);
            }
            catch
            {
                part.FileSystem = priorGuessedFs;
                return;
            }
            if (data.Length < 512)
            {
                part.FileSystem = priorGuessedFs;
                return;
            }

            // Check NTFS ("NTFS    " at +3 with valid BPB sector/cluster geometry)
            if (MatchesAscii(data, 3, "NTFS    ") &&
                BitConverter.ToUInt16(data, 0x0B) >= 512 &&
                data[0x0D] > 0 &&
                data[0x40] != 0 &&
                data[0x44] != 0)
            {
                if (part.GptTypeGuid != GptPartitionTypeGuids.WindowsRecovery)
                    part.FileSystem = FileSystemType.NTFS;
                else
                    part.FileSystem = FileSystemType.WindowsRecovery;

                ulong serial = BitConverter.ToUInt64(data, 0x48);
                part.UUID = serial.ToString("X16");
                // Custom WinParted metadata tag or volume label fallback
                string customLabel = ReadCustomLabelHint(data, 0x1A0);
                if (!string.IsNullOrEmpty(customLabel)) part.Label = customLabel;
                long customUsed = ReadCustomUsedHint(data, 0x1C0);
                if (customUsed >= 0 && customUsed <= part.PartitionLength)
                {
                    part.UsedBytes = customUsed;
                    part.FreeBytes = part.PartitionLength - customUsed;
                }
                return;
            }

            // Check exFAT ("EXFAT   " at +3)
            if (MatchesAscii(data, 3, "EXFAT   "))
            {
                part.FileSystem = FileSystemType.exFAT;
                uint serial = BitConverter.ToUInt32(data, 0x64);
                part.UUID = string.Format("{0:X4}-{1:X4}", (serial >> 16) & 0xFFFF, serial & 0xFFFF);

                // Read native 0x83 Volume Label entry from exFAT Root Directory when valid BPB geometry is present
                if (data.Length >= 0x70 && data[0x6C] >= 9 && data[0x6C] <= 12)
                {
                    try
                    {
                        long exSecSize = 1L << data[0x6C];
                        uint fatOff = BitConverter.ToUInt32(data, 0x50);
                        uint heapOff = BitConverter.ToUInt32(data, 0x58);
                        uint clusCount = BitConverter.ToUInt32(data, 0x5C);
                        uint rootClus = BitConverter.ToUInt32(data, 0x60);
                        int spcShift = data[0x6D];
                        if (spcShift <= 25 - data[0x6C] && fatOff >= 24 && heapOff > fatOff && rootClus >= 2 && rootClus < clusCount + 2u)
                        {
                            long clusBytes = Math.Min(131072L, exSecSize << spcShift);
                            long rootByteOff = ((long)heapOff + (long)(rootClus - 2u) * (1L << spcShift)) * exSecSize;
                            if (rootByteOff + 512L <= part.PartitionLength)
                            {
                                byte[] rootData = ReadBytes(disk, part.StartingOffset + rootByteOff, (int)Math.Min(clusBytes, part.PartitionLength - rootByteOff));
                                for (int eOff = 0; eOff + 32 <= rootData.Length; eOff += 32)
                                {
                                    byte eType = rootData[eOff];
                                    if (eType == 0x00)
                                        break;
                                    if (eType == 0x83)
                                    {
                                        int chLen = Math.Min(11, (int)rootData[eOff + 1]);
                                        if (chLen > 0)
                                        {
                                            string volLbl = Encoding.Unicode.GetString(rootData, eOff + 2, chLen * 2).TrimEnd('\0', ' ');
                                            if (!string.IsNullOrEmpty(volLbl))
                                                part.Label = volLbl;
                                        }
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    catch
                    {
                    }
                }

                if (string.IsNullOrEmpty(part.Label))
                {
                    string customLabel = ReadCustomLabelHint(data, 0x1A0);
                    if (!string.IsNullOrEmpty(customLabel)) part.Label = customLabel;
                }
                long customUsed = ReadCustomUsedHint(data, 0x1C0);
                if (customUsed >= 0 && customUsed <= part.PartitionLength)
                {
                    part.UsedBytes = customUsed;
                    part.FreeBytes = part.PartitionLength - customUsed;
                }
                return;
            }

            // Check BitLocker ("-FVE-FS-" at +3)
            if (MatchesAscii(data, 3, "-FVE-FS-"))
            {
                part.FileSystem = FileSystemType.BitLocker;
                return;
            }

            // Check ReFS ("ReFS" at +3)
            if (MatchesAscii(data, 3, "ReFS"))
            {
                part.FileSystem = FileSystemType.ReFS;
                if (data.Length >= 0x80)
                {
                    ulong refsSerial = BitConverter.ToUInt64(data, 0x38);
                    if (refsSerial != 0)
                        part.UUID = refsSerial.ToString("X16");
                    string refsLbl = Encoding.Unicode.GetString(data, 0x40, 64).TrimEnd('\0', ' ');
                    if (string.IsNullOrEmpty(refsLbl))
                        refsLbl = ReadCustomLabelHint(data, 0x1A0);
                    if (!string.IsNullOrEmpty(refsLbl))
                        part.Label = refsLbl;
                    long refsUsed = ReadCustomUsedHint(data, 0x1C0);
                    if (refsUsed < 0)
                        refsUsed = BitConverter.ToInt64(data, 0x28);
                    if (refsUsed >= 0 && refsUsed <= part.PartitionLength)
                    {
                        part.UsedBytes = refsUsed;
                        part.FreeBytes = part.PartitionLength - refsUsed;
                    }
                }
                return;
            }

            // Check FAT32 ("FAT32   " at +0x52)
            if (data.Length >= 96 && MatchesAscii(data, 0x52, "FAT32   "))
            {
                part.FileSystem = part.GptTypeGuid == GptPartitionTypeGuids.EfiSystem
                    ? FileSystemType.EfiSystem
                    : FileSystemType.FAT32;
                uint volId = BitConverter.ToUInt32(data, 0x43);
                part.UUID = string.Format("{0:X4}-{1:X4}", (volId >> 16) & 0xFFFF, volId & 0xFFFF);
                string lbl = Encoding.ASCII.GetString(data, 0x47, 11).Trim();
                if (!string.IsNullOrEmpty(lbl) && lbl != "NO NAME")
                    part.Label = lbl;
                long customUsed = ReadCustomUsedHint(data, 0x1C0);
                if (customUsed >= 0 && customUsed <= part.PartitionLength)
                {
                    part.UsedBytes = customUsed;
                    part.FreeBytes = part.PartitionLength - customUsed;
                }
                return;
            }

            // Check FAT16 / FAT12 ("FAT16   ", "FAT     ", or "FAT12   " at +0x36)
            if (data.Length >= 64 &&
                (MatchesAscii(data, 0x36, "FAT16   ") ||
                 MatchesAscii(data, 0x36, "FAT     ") ||
                 MatchesAscii(data, 0x36, "FAT12   ")))
            {
                part.FileSystem = FileSystemType.FAT16;
                uint fat16Id = BitConverter.ToUInt32(data, 0x27);
                if (fat16Id != 0)
                    part.UUID = string.Format("{0:X4}-{1:X4}", (fat16Id >> 16) & 0xFFFF, fat16Id & 0xFFFF);
                string lbl = Encoding.ASCII.GetString(data, 0x2B, 11).Trim();
                if (!string.IsNullOrEmpty(lbl) && lbl != "NO NAME")
                    part.Label = lbl;
                long customUsed16 = ReadCustomUsedHint(data, 0x1C0);
                if (customUsed16 >= 0 && customUsed16 <= part.PartitionLength)
                {
                    part.UsedBytes = customUsed16;
                    part.FreeBytes = part.PartitionLength - customUsed16;
                }
                return;
            }

            // Check SquashFS ("hsqs" magic 0x73717368 at +0)
            if (data.Length >= 0xC0 && BitConverter.ToUInt32(data, 0) == 0x73717368u)
            {
                part.FileSystem = FileSystemType.SquashFS;
                long bytesUsed = (long)BitConverter.ToUInt64(data, 40);
                if (bytesUsed <= 0 || bytesUsed > part.PartitionLength)
                    bytesUsed = Math.Min(part.PartitionLength, 4096L);
                part.UsedBytes = bytesUsed;
                part.FreeBytes = Math.Max(0, part.PartitionLength - bytesUsed);
                byte[] sqUuid = new byte[16];
                Array.Copy(data, 0xA0, sqUuid, 0, 16);
                if (sqUuid.Any(b => b != 0))
                    part.UUID = FormatRfcUuid(sqUuid);
                string sqLbl = Encoding.UTF8.GetString(data, 0x80, 32).TrimEnd('\0', ' ');
                if (!string.IsNullOrEmpty(sqLbl))
                    part.Label = sqLbl;
                return;
            }

            // Check F2FS (magic 0xF2F52010 at +0x400)
            if (data.Length >= 0x500 && BitConverter.ToUInt32(data, 0x400) == 0xF2F52010u)
            {
                part.FileSystem = FileSystemType.F2FS;
                uint logBlk = BitConverter.ToUInt32(data, 0x410);
                long f2BlockSize = (logBlk >= 9 && logBlk <= 16) ? (1L << (int)logBlk) : 4096L;
                ulong blkCount = BitConverter.ToUInt64(data, 0x424);
                ulong userBlks = BitConverter.ToUInt64(data, 0x430);
                byte[] f2Uuid = new byte[16];
                Array.Copy(data, 0x46C, f2Uuid, 0, 16);
                if (f2Uuid.Any(b => b != 0))
                    part.UUID = FormatRfcUuid(f2Uuid);
                string f2Lbl = Encoding.Unicode.GetString(data, 0x47C, 64).TrimEnd('\0', ' ');
                if (!string.IsNullOrEmpty(f2Lbl))
                    part.Label = f2Lbl;
                long f2Used = ReadCustomUsedHint(data, 0x1C0);
                if (f2Used < 0 && blkCount > 0 && userBlks <= blkCount)
                    f2Used = (long)(blkCount - userBlks) * f2BlockSize;
                if (f2Used >= 0 && f2Used <= part.PartitionLength)
                {
                    part.UsedBytes = f2Used;
                    part.FreeBytes = part.PartitionLength - f2Used;
                }
                return;
            }

            // Check ext2 / ext3 / ext4 (superblock at +1024, magic 0xEF53 at +1024 + 0x38 = 0x438)
            if (data.Length >= 2048 && BitConverter.ToUInt16(data, 0x438) == 0xEF53)
            {
                uint logBlockSize = BitConverter.ToUInt32(data, 0x418);
                long blockSize = 1024L << (int)Math.Min(6, logBlockSize);
                uint blocksCount = BitConverter.ToUInt32(data, 0x404);
                uint freeBlocks = BitConverter.ToUInt32(data, 0x40C);
                uint featCompat = BitConverter.ToUInt32(data, 0x45C);
                uint featIncompat = BitConverter.ToUInt32(data, 0x460);

                bool hasJournal = (featCompat & 0x0004) != 0;
                bool hasExt4 = (featIncompat & 0x0040) != 0 || (featIncompat & 0x0200) != 0; // extents or flex_bg
                part.FileSystem = hasExt4 ? FileSystemType.Ext4 : (hasJournal ? FileSystemType.Ext3 : FileSystemType.Ext2);

                byte[] uuidBytes = new byte[16];
                Array.Copy(data, 0x468, uuidBytes, 0, 16);
                part.UUID = FormatRfcUuid(uuidBytes);

                string lbl = Encoding.UTF8.GetString(data, 0x478, 16).TrimEnd('\0', ' ');
                if (!string.IsNullOrEmpty(lbl))
                    part.Label = lbl;

                if (blocksCount > 0 && freeBlocks <= blocksCount)
                {
                    long totalFs = blocksCount * blockSize;
                    long freeFs = freeBlocks * blockSize;
                    part.FreeBytes = Math.Min(part.PartitionLength, freeFs);
                    part.UsedBytes = Math.Max(0, Math.Min(part.PartitionLength, totalFs - freeFs));
                }
                return;
            }

            // Check Linux Swap ("SWAPSPACE2" at +4086)
            if (data.Length >= 4096 && MatchesAscii(data, 4086, "SWAPSPACE2"))
            {
                part.FileSystem = FileSystemType.LinuxSwap;
                byte[] uuidBytes = new byte[16];
                Array.Copy(data, 0x40C, uuidBytes, 0, 16);
                part.UUID = FormatRfcUuid(uuidBytes);
                string lbl = Encoding.UTF8.GetString(data, 0x41C, 16).TrimEnd('\0', ' ');
                if (!string.IsNullOrEmpty(lbl))
                    part.Label = lbl;
                part.UsedBytes = 0;
                part.FreeBytes = part.PartitionLength;
                return;
            }

            // Check Btrfs ("_BHRfS_M" at +0x10040)
            if (data.Length >= 0x10100 && MatchesAscii(data, 0x10040, "_BHRfS_M"))
            {
                part.FileSystem = FileSystemType.Btrfs;
                byte[] uuidBytes = new byte[16];
                Array.Copy(data, 0x10020, uuidBytes, 0, 16);
                part.UUID = FormatRfcUuid(uuidBytes);
                if (data.Length >= 0x1016B)
                {
                    string lbl = Encoding.UTF8.GetString(data, 0x1012B, 64).TrimEnd('\0', ' ');
                    if (!string.IsNullOrEmpty(lbl)) part.Label = lbl;
                }
                long btrfsTotal = BitConverter.ToInt64(data, 0x10070);
                long btrfsUsed = BitConverter.ToInt64(data, 0x10078);
                if (btrfsTotal > 0 && btrfsUsed >= 0 && btrfsUsed <= btrfsTotal)
                {
                    part.UsedBytes = Math.Min(part.PartitionLength, btrfsUsed);
                    part.FreeBytes = Math.Max(0, part.PartitionLength - part.UsedBytes.Value);
                }
                return;
            }

            // Check XFS ("XFSB" at +0; real XFS is Big-Endian, also accept Little-Endian)
            if (MatchesAscii(data, 0, "XFSB"))
            {
                part.FileSystem = FileSystemType.XFS;
                if (data.Length >= 120)
                {
                    uint beBlockSize = ReadUInt32BE(data, 4);
                    bool isBigEndian = beBlockSize >= 512 && beBlockSize <= 65536 && (beBlockSize & (beBlockSize - 1)) == 0;
                    uint blockSize = isBigEndian ? beBlockSize : BitConverter.ToUInt32(data, 4);
                    if (blockSize == 0) blockSize = 4096;
                    ulong dblocks = isBigEndian ? ReadUInt64BE(data, 8) : BitConverter.ToUInt64(data, 8);
                    ulong fdblocks;
                    if (isBigEndian)
                    {
                        fdblocks = data.Length >= 152 ? ReadUInt64BE(data, 144) : 0UL;
                        if (fdblocks == 0 && dblocks > 0)
                        {
                            ulong alt = ReadUInt64BE(data, 16);
                            if (alt > 0 && alt <= dblocks) fdblocks = alt;
                        }
                    }
                    else
                    {
                        fdblocks = BitConverter.ToUInt64(data, 16);
                    }
                    byte[] uuidBytes = new byte[16];
                    Array.Copy(data, 32, uuidBytes, 0, 16);
                    part.UUID = FormatRfcUuid(uuidBytes);
                    string lbl = Encoding.UTF8.GetString(data, 108, 12).TrimEnd('\0', ' ');
                    if (!string.IsNullOrEmpty(lbl)) part.Label = lbl;
                    if (dblocks > 0 && fdblocks <= dblocks)
                    {
                        long totalXfs = (long)(dblocks * blockSize);
                        long freeXfs = (long)(fdblocks * blockSize);
                        part.FreeBytes = Math.Min(part.PartitionLength, freeXfs);
                        part.UsedBytes = Math.Max(0, Math.Min(part.PartitionLength, totalXfs - freeXfs));
                    }
                }
                return;
            }

            // Check ZFS / OpenZFS ("ZFS_POOL" at +0x0800 or Uberblock magic 0x00BAB10C at +0x11000)
            if ((data.Length >= 0x0850 && MatchesAscii(data, 0x0800, "ZFS_POOL")) ||
                (data.Length >= 0x11040 && (BitConverter.ToUInt64(data, 0x11000) == 0x00BAB10CUL || ReadUInt64BE(data, 0x11000) == 0x00BAB10CUL)))
            {
                part.FileSystem = FileSystemType.ZFS;
                if (MatchesAscii(data, 0x0800, "ZFS_POOL"))
                {
                    string zfsLbl = Encoding.UTF8.GetString(data, 0x0810, 32).TrimEnd('\0', ' ');
                    if (!string.IsNullOrEmpty(zfsLbl)) part.Label = zfsLbl;
                    byte[] zfsUuid = new byte[16];
                    Array.Copy(data, 0x0830, zfsUuid, 0, 16);
                    if (zfsUuid.Any(b => b != 0))
                        part.UUID = FormatRfcUuid(zfsUuid);
                    long zfsUsed = BitConverter.ToInt64(data, 0x0848);
                    if (zfsUsed >= 0 && zfsUsed <= part.PartitionLength)
                    {
                        part.UsedBytes = zfsUsed;
                        part.FreeBytes = part.PartitionLength - zfsUsed;
                    }
                }
                return;
            }

            // Check UDF ("BEA01" at +0x8001 / "NSR02"|"NSR03" at +0x8801|+0x9001) & ISO9660 ("CD001" at +0x8001)
            if (data.Length >= 0x9010 && MatchesAscii(data, 0x8001, "BEA01") &&
                (MatchesAscii(data, 0x8801, "NSR02") || MatchesAscii(data, 0x8801, "NSR03") ||
                 MatchesAscii(data, 0x9001, "NSR02") || MatchesAscii(data, 0x9001, "NSR03")))
            {
                part.FileSystem = FileSystemType.UDF;
                string udfLbl = Encoding.UTF8.GetString(data, 0x8820, 32).TrimEnd('\0', ' ');
                if (!string.IsNullOrEmpty(udfLbl)) part.Label = udfLbl;
                byte[] udfUuid = new byte[16];
                Array.Copy(data, 0x8840, udfUuid, 0, 16);
                if (udfUuid.Any(b => b != 0))
                    part.UUID = FormatRfcUuid(udfUuid);
                long udfUsed = BitConverter.ToInt64(data, 0x8850);
                if (udfUsed > 0 && udfUsed <= part.PartitionLength)
                {
                    part.UsedBytes = udfUsed;
                    part.FreeBytes = part.PartitionLength - udfUsed;
                }
                return;
            }

            if (data.Length >= 0x8800 && MatchesAscii(data, 0x8001, "CD001"))
            {
                part.FileSystem = FileSystemType.ISO9660;
                string isoLbl = Encoding.ASCII.GetString(data, 0x8028, 32).TrimEnd('\0', ' ');
                if (!string.IsNullOrEmpty(isoLbl))
                    part.Label = isoLbl;
                ushort isoBlk = BitConverter.ToUInt16(data, 0x8080);
                if (isoBlk == 0) isoBlk = 2048;
                uint isoBlocks = BitConverter.ToUInt32(data, 0x8050);
                long isoUsed = ReadCustomUsedHint(data, 0x81C0);
                if (isoUsed <= 0 && isoBlocks > 0)
                    isoUsed = Math.Min(part.PartitionLength, (long)isoBlocks * isoBlk);
                if (isoUsed >= 0 && isoUsed <= part.PartitionLength)
                {
                    part.UsedBytes = isoUsed;
                    part.FreeBytes = part.PartitionLength - isoUsed;
                }
                return;
            }

            // Check LVM2 ("LABELONE" at +512)
            if (data.Length >= 1024 && MatchesAscii(data, 512, "LABELONE"))
            {
                part.FileSystem = FileSystemType.LVM2;
                if (MatchesAscii(data, 536, "LVM2 001"))
                {
                    string pvUuid = Encoding.ASCII.GetString(data, 544, 32).TrimEnd('\0', ' ');
                    if (!string.IsNullOrEmpty(pvUuid))
                        part.UUID = pvUuid;
                }
                if (!part.UsedBytes.HasValue)
                    part.UsedBytes = 0;
                if (!part.FreeBytes.HasValue)
                    part.FreeBytes = part.PartitionLength;
                return;
            }

            // Check APFS ("NXSB" or "APSB" at +32)
            if (MatchesAscii(data, 32, "NXSB") || MatchesAscii(data, 32, "APSB"))
            {
                part.FileSystem = FileSystemType.APFS;
                if (data.Length >= 88)
                {
                    uint blockSize = BitConverter.ToUInt32(data, 36);
                    if (blockSize == 0) blockSize = 4096;
                    byte[] uuidBytes = new byte[16];
                    Array.Copy(data, 0x48, uuidBytes, 0, 16);
                    if (uuidBytes.Any(b => b != 0))
                        part.UUID = FormatRfcUuid(uuidBytes);
                }

                string apfsLbl = ReadCustomLabelHint(data, 0x1A0);
                long apfsUsed = ReadCustomUsedHint(data, 0x1C0);

                if (string.IsNullOrEmpty(apfsLbl) || apfsUsed < 0 || string.IsNullOrEmpty(part.UUID))
                {
                    string volLbl;
                    string volUuid;
                    long volUsed;
                    if (ReadOnlyFileSystemMountEngine.TryReadApfsVolumeMetadata(disk, part, data, out volLbl, out volUuid, out volUsed))
                    {
                        if (string.IsNullOrEmpty(apfsLbl) && !string.IsNullOrEmpty(volLbl))
                            apfsLbl = volLbl;
                        if (string.IsNullOrEmpty(part.UUID) && !string.IsNullOrEmpty(volUuid))
                            part.UUID = volUuid;
                        if (apfsUsed < 0 && volUsed >= 0)
                            apfsUsed = volUsed;
                    }
                }

                if (string.IsNullOrEmpty(apfsLbl) && MatchesAscii(data, 32, "APSB") && data.Length >= 0x3C0)
                {
                    int end = 0x2C0;
                    while (end < 0x3C0 && data[end] != 0) end++;
                    if (end > 0x2C0)
                        apfsLbl = Encoding.UTF8.GetString(data, 0x2C0, end - 0x2C0).Trim();
                }

                if (!string.IsNullOrEmpty(apfsLbl))
                    part.Label = apfsLbl;
                if (apfsUsed >= 0 && apfsUsed <= part.PartitionLength)
                {
                    part.UsedBytes = apfsUsed;
                    part.FreeBytes = part.PartitionLength - apfsUsed;
                }
                return;
            }

            // Check Apple HFS+ / HFSX (Volume Header "H+" or "HX" at +0x400, or wrapped in "BD" at +0x47C)
            if (data.Length >= 1536 &&
                (MatchesAscii(data, 0x400, "H+") || MatchesAscii(data, 0x400, "HX") ||
                 (MatchesAscii(data, 0x400, "BD") && (MatchesAscii(data, 0x47C, "H+") || MatchesAscii(data, 0x47C, "HX")))))
            {
                part.FileSystem = FileSystemType.HFSPlus;
                if (MatchesAscii(data, 0x400, "H+") || MatchesAscii(data, 0x400, "HX"))
                {
                    uint blockSize = ReadUInt32BE(data, 0x428);
                    if (blockSize == 0) blockSize = 4096;
                    uint totalBlocks = ReadUInt32BE(data, 0x42C);
                    uint freeBlocks = ReadUInt32BE(data, 0x430);
                    ulong hfsId = BitConverter.ToUInt64(data, 0x468);
                    if (hfsId != 0)
                        part.UUID = hfsId.ToString("X16");
                    string hfsLbl = ReadCustomLabelHint(data, 0x1A0);
                    if (!string.IsNullOrEmpty(hfsLbl))
                        part.Label = hfsLbl;
                    else
                    {
                        string btreeLbl = TryReadHfsPlusVolumeLabel(disk, part, data);
                        if (!string.IsNullOrEmpty(btreeLbl))
                            part.Label = btreeLbl;
                    }
                    if (totalBlocks > 0 && freeBlocks <= totalBlocks)
                    {
                        part.FreeBytes = Math.Min(part.PartitionLength, (long)freeBlocks * blockSize);
                        part.UsedBytes = Math.Max(0, Math.Min(part.PartitionLength, (long)(totalBlocks - freeBlocks) * blockSize));
                    }
                }
                return;
            }

            // Check Classic Macintosh HFS (Master Directory Block "BD" at +0x400)
            if (data.Length >= 1200 && MatchesAscii(data, 0x400, "BD"))
            {
                part.FileSystem = FileSystemType.HFS;
                ushort totalAlBlks = ReadUInt16BE(data, 0x412);
                uint alBlkSize = ReadUInt32BE(data, 0x414);
                if (alBlkSize == 0) alBlkSize = 4096;
                ushort freeAlBlks = ReadUInt16BE(data, 0x422);
                int vnLen = Math.Min(27, (int)data[0x424]);
                if (vnLen > 0)
                    part.Label = Encoding.UTF8.GetString(data, 0x425, vnLen).TrimEnd('\0');
                ulong macId = BitConverter.ToUInt64(data, 0x474);
                if (macId != 0)
                    part.UUID = macId.ToString("X16");
                long macUsed = ReadCustomUsedHint(data, 0x1C0);
                if (macUsed >= 0 && macUsed <= part.PartitionLength)
                {
                    part.UsedBytes = macUsed;
                    part.FreeBytes = part.PartitionLength - macUsed;
                }
                else if (totalAlBlks > 0 && freeAlBlks <= totalAlBlks)
                {
                    part.FreeBytes = Math.Min(part.PartitionLength, (long)freeAlBlks * alBlkSize);
                    part.UsedBytes = Math.Max(0, Math.Min(part.PartitionLength, (long)(totalAlBlks - freeAlBlks) * alBlkSize));
                }
                return;
            }

            // No recognized filesystem superblock found (e.g. unformatted or wiped)
            if (part.FileSystem != FileSystemType.MicrosoftReserved)
            {
                part.FileSystem = FileSystemType.RAW;
                part.Label = string.Empty;
                part.UsedBytes = 0;
                part.FreeBytes = part.PartitionLength;
            }
        }

        /// <summary>
        /// Formats a partition header area (first 128 KiB) with valid superblock metadata, UUIDs, labels,
        /// and initial directory/B-tree/catalog structures for the target <see cref="PartitionEntry.FileSystem"/>.
        /// </summary>
        public static void WriteFileSystemHeader(DiskInfo disk, PartitionEntry part)
        {
            if (part.IsUnallocated || part.PartitionLength < 65536) return;

            ReadOnlyFileSystemMountEngine.PurgePartitionOverlaysAndCache(disk, part.StartingOffset, part.PartitionLength, true);

            int headerClearLen = (int)Math.Min(131072, part.PartitionLength);
            byte[] buf = new byte[headerClearLen];
            long totalSectors = part.PartitionLength / 512;

            int fat32SecPerClus = 0;
            uint fat32Sz = 0;
            uint fat16Sz = 0;

            switch (part.FileSystem)
            {
                case FileSystemType.NTFS:
                case FileSystemType.WindowsRecovery:
                    buf[0] = 0xEB; buf[1] = 0x52; buf[2] = 0x90;
                    Encoding.ASCII.GetBytes("NTFS    ").CopyTo(buf, 3);
                    BitConverter.GetBytes((ushort)512).CopyTo(buf, 0x0B);
                    buf[0x0D] = 8; // 4K clusters
                    buf[0x15] = 0xF8;
                    BitConverter.GetBytes((ushort)63).CopyTo(buf, 0x18);
                    BitConverter.GetBytes((ushort)255).CopyTo(buf, 0x1A);
                    BitConverter.GetBytes((uint)Math.Min(uint.MaxValue, Math.Max(0L, part.StartingOffset / 512L))).CopyTo(buf, 0x1C);
                    BitConverter.GetBytes((ulong)totalSectors).CopyTo(buf, 0x28);
                    BitConverter.GetBytes((ulong)Math.Max(4L, totalSectors / 16L)).CopyTo(buf, 0x30); // $MFT LCN
                    BitConverter.GetBytes(2UL).CopyTo(buf, 0x38);                                    // $MFTMirr LCN
                    buf[0x40] = 0xF6;                                                                // 1024-byte FRS (-10)
                    buf[0x44] = 0x01;                                                                // 4096-byte Index Block
                    ulong ntfsSerial = (ulong)Math.Abs((part.Label + part.StartingOffset).GetHashCode()) << 32 | 0x57494E50UL;
                    BitConverter.GetBytes(ntfsSerial).CopyTo(buf, 0x48);
                    WriteCustomLabelHint(buf, 0x1A0, part.Label);
                    long ntfsUsed = part.UsedBytes ?? (long)(part.PartitionLength * 0.45);
                    WriteCustomUsedHint(buf, 0x1C0, ntfsUsed);
                    buf[510] = 0x55; buf[511] = 0xAA;
                    part.UsedBytes = ntfsUsed;
                    part.FreeBytes = part.PartitionLength - ntfsUsed;
                    part.UUID = ntfsSerial.ToString("X16");
                    break;

                case FileSystemType.FAT32:
                case FileSystemType.EfiSystem:
                    if (part.PartitionLength <= 260L * 1048576L) fat32SecPerClus = 1;
                    else if (part.PartitionLength <= 8L * 1073741824L) fat32SecPerClus = 8;
                    else if (part.PartitionLength <= 16L * 1073741824L) fat32SecPerClus = 16;
                    else if (part.PartitionLength <= 32L * 1073741824L) fat32SecPerClus = 32;
                    else fat32SecPerClus = 64;

                    while (true)
                    {
                        long tmpVal1 = Math.Max(1L, totalSectors - 32L);
                        long tmpVal2 = (256L * fat32SecPerClus + 2L) / 2L;
                        fat32Sz = (uint)Math.Max(1L, (tmpVal1 + tmpVal2 - 1L) / tmpVal2);
                        long dataSectorsTmp = Math.Max(0L, totalSectors - (32L + 2L * fat32Sz));
                        if (dataSectorsTmp / fat32SecPerClus >= 65525L || fat32SecPerClus <= 1)
                            break;
                        fat32SecPerClus >>= 1;
                    }

                    buf[0] = 0xEB; buf[1] = 0x58; buf[2] = 0x90;
                    Encoding.ASCII.GetBytes("MSDOS5.0").CopyTo(buf, 3);
                    BitConverter.GetBytes((ushort)512).CopyTo(buf, 0x0B);          // BPB_BytsPerSec
                    buf[0x0D] = (byte)fat32SecPerClus;                              // BPB_SecPerClus
                    BitConverter.GetBytes((ushort)32).CopyTo(buf, 0x0E);           // BPB_RsvdSecCnt
                    buf[0x10] = 2;                                                  // BPB_NumFATs
                    buf[0x15] = 0xF8;                                               // BPB_Media
                    BitConverter.GetBytes((ushort)63).CopyTo(buf, 0x18);           // BPB_SecPerTrk
                    BitConverter.GetBytes((ushort)255).CopyTo(buf, 0x1A);          // BPB_NumHeads
                    BitConverter.GetBytes((uint)Math.Min(uint.MaxValue, Math.Max(0L, part.StartingOffset / 512L))).CopyTo(buf, 0x1C);
                    BitConverter.GetBytes((uint)Math.Min(uint.MaxValue, totalSectors)).CopyTo(buf, 0x20);
                    BitConverter.GetBytes(fat32Sz).CopyTo(buf, 0x24);              // BPB_FATSz32
                    BitConverter.GetBytes(2u).CopyTo(buf, 0x2C);                   // BPB_RootClus
                    BitConverter.GetBytes((ushort)1).CopyTo(buf, 0x30);            // BPB_FSInfo
                    BitConverter.GetBytes((ushort)6).CopyTo(buf, 0x32);            // BPB_BkBootSec
                    buf[0x40] = 0x80;                                               // BS_DrvNum
                    buf[0x42] = 0x29;                                               // BS_BootSig
                    uint fatId = (uint)(0x46415400 ^ (int)part.StartingOffset);
                    BitConverter.GetBytes(fatId).CopyTo(buf, 0x43);
                    string fatLbl = SanitizeFatLabel(part.Label, false);
                    Encoding.ASCII.GetBytes(fatLbl).CopyTo(buf, 0x47);
                    Encoding.ASCII.GetBytes("FAT32   ").CopyTo(buf, 0x52);
                    long fatUsed = part.UsedBytes ?? (part.FileSystem == FileSystemType.EfiSystem ? (long)(part.PartitionLength * 0.18) : (long)(part.PartitionLength * 0.10));
                    if (disk.IsVirtualImage)
                        WriteCustomUsedHint(buf, 0x1C0, fatUsed);
                    buf[510] = 0x55; buf[511] = 0xAA;

                    // FSInfo sector at LBA 1 (+512..+1023)
                    long fatDataSectors = Math.Max(0L, totalSectors - (32L + 2L * fat32Sz));
                    uint fatTotalClusters = (uint)Math.Max(0L, Math.Min(uint.MaxValue, fatDataSectors / fat32SecPerClus));
                    uint fatFreeClusters = fatTotalClusters > 1u ? fatTotalClusters - 1u : 0u;
                    BitConverter.GetBytes(0x41615252u).CopyTo(buf, 512 + 0);       // FSI_LeadSig
                    BitConverter.GetBytes(0x61417272u).CopyTo(buf, 512 + 484);     // FSI_StrucSig
                    BitConverter.GetBytes(fatFreeClusters).CopyTo(buf, 512 + 488); // FSI_Free_Count
                    BitConverter.GetBytes(3u).CopyTo(buf, 512 + 492);              // FSI_Nxt_Free
                    BitConverter.GetBytes(0xAA550000u).CopyTo(buf, 512 + 508);     // FSI_TrailSig

                    // Backup Boot Sector (LBA 6) and Backup FSInfo (LBA 7)
                    if (buf.Length >= 8 * 512)
                        Array.Copy(buf, 0, buf, 6 * 512, 1024);

                    part.UsedBytes = fatUsed;
                    part.FreeBytes = Math.Max(0, part.PartitionLength - fatUsed);
                    part.UUID = string.Format("{0:X4}-{1:X4}", (fatId >> 16) & 0xFFFF, fatId & 0xFFFF);
                    break;

                case FileSystemType.FAT16:
                    int fat16SecPerClus = 64;
                    int[] fat16ClusCandidates = new int[] { 1, 2, 4, 8, 16, 32, 64 };
                    for (int ci = 0; ci < fat16ClusCandidates.Length; ci++)
                    {
                        int cand = fat16ClusCandidates[ci];
                        long approxClusters = Math.Max(1L, (totalSectors - 33L) / cand);
                        uint estFatSz = (uint)Math.Max(1L, ((approxClusters + 2L) * 2L + 511L) / 512L);
                        long dataSectors16 = Math.Max(0L, totalSectors - (33L + 2L * estFatSz));
                        if (dataSectors16 / cand <= 65524L)
                        {
                            fat16SecPerClus = cand;
                            fat16Sz = (uint)Math.Max(1L, Math.Min(65535L, estFatSz));
                            break;
                        }
                    }
                    if (fat16Sz == 0)
                    {
                        long fat16Clusters = Math.Max(1L, totalSectors / fat16SecPerClus);
                        fat16Sz = (uint)Math.Max(1L, Math.Min(65535L, ((fat16Clusters + 2L) * 2L + 511L) / 512L));
                    }

                    buf[0] = 0xEB; buf[1] = 0x3C; buf[2] = 0x90;
                    Encoding.ASCII.GetBytes("MSDOS5.0").CopyTo(buf, 3);
                    BitConverter.GetBytes((ushort)512).CopyTo(buf, 0x0B);
                    buf[0x0D] = (byte)fat16SecPerClus;
                    BitConverter.GetBytes((ushort)1).CopyTo(buf, 0x0E);
                    buf[0x10] = 2;
                    BitConverter.GetBytes((ushort)512).CopyTo(buf, 0x11);
                    buf[0x15] = 0xF8;
                    BitConverter.GetBytes((ushort)fat16Sz).CopyTo(buf, 0x16);      // BPB_FATSz16
                    BitConverter.GetBytes((ushort)63).CopyTo(buf, 0x18);
                    BitConverter.GetBytes((ushort)255).CopyTo(buf, 0x1A);
                    BitConverter.GetBytes((uint)Math.Min(uint.MaxValue, Math.Max(0L, part.StartingOffset / 512L))).CopyTo(buf, 0x1C);
                    if (totalSectors <= 65535)
                        BitConverter.GetBytes((ushort)totalSectors).CopyTo(buf, 0x13);
                    else
                        BitConverter.GetBytes((uint)totalSectors).CopyTo(buf, 0x20);
                    buf[0x24] = 0x80;
                    uint fat16Id = (uint)(0x46313600 ^ (int)part.StartingOffset);
                    buf[0x26] = 0x29;
                    BitConverter.GetBytes(fat16Id).CopyTo(buf, 0x27);
                    string fat16Lbl = SanitizeFatLabel(part.Label, false);
                    Encoding.ASCII.GetBytes(fat16Lbl).CopyTo(buf, 0x2B);
                    Encoding.ASCII.GetBytes("FAT16   ").CopyTo(buf, 0x36);
                    long fat16Used = part.UsedBytes ?? (long)(part.PartitionLength * 0.08);
                    if (disk.IsVirtualImage)
                        WriteCustomUsedHint(buf, 0x1C0, fat16Used);
                    buf[510] = 0x55; buf[511] = 0xAA;
                    part.UsedBytes = fat16Used;
                    part.FreeBytes = Math.Max(0, part.PartitionLength - fat16Used);
                    part.UUID = string.Format("{0:X4}-{1:X4}", (fat16Id >> 16) & 0xFFFF, fat16Id & 0xFFFF);
                    break;

                case FileSystemType.ReFS:
                    buf[0] = 0xEB; buf[1] = 0x52; buf[2] = 0x90;
                    Encoding.ASCII.GetBytes("ReFS\0\0\0\0").CopyTo(buf, 3);
                    BitConverter.GetBytes((ulong)totalSectors).CopyTo(buf, 0x20);
                    long refsUsed = part.UsedBytes ?? Math.Min(part.PartitionLength / 4, 2L * 1048576L);
                    BitConverter.GetBytes(refsUsed).CopyTo(buf, 0x28);
                    ulong refsSerial = (ulong)Math.Abs((part.Label + part.StartingOffset + "ReFS").GetHashCode()) << 32 | 0x52454653UL;
                    BitConverter.GetBytes(refsSerial).CopyTo(buf, 0x38);
                    string refsLbl = part.Label ?? string.Empty;
                    if (refsLbl.Length > 31) refsLbl = refsLbl.Substring(0, 31);
                    Encoding.Unicode.GetBytes(refsLbl).CopyTo(buf, 0x40);
                    WriteCustomLabelHint(buf, 0x1A0, part.Label);
                    WriteCustomUsedHint(buf, 0x1C0, refsUsed);
                    buf[510] = 0x55; buf[511] = 0xAA;
                    part.UsedBytes = refsUsed;
                    part.FreeBytes = Math.Max(0, part.PartitionLength - refsUsed);
                    part.UUID = refsSerial.ToString("X16");
                    if (buf.Length >= 0x8000)
                        PopulateReadOnlyFsCatalog(buf, part);
                    break;

                case FileSystemType.exFAT:
                    // Handled by full Microsoft exFAT-spec formatter after switch
                    break;

                case FileSystemType.Ext2:
                case FileSystemType.Ext3:
                case FileSystemType.Ext4:
                    if (!disk.IsVirtualImage)
                    {
                        WriteRealExtFileSystem(disk, part);
                        return;
                    }
                    uint blockSize = 4096;
                    uint blocksCount = (uint)Math.Max(1, part.PartitionLength / blockSize);
                    long extUsed = part.UsedBytes ?? (long)(part.PartitionLength * 0.30);
                    uint usedBlocks = (uint)(extUsed / blockSize);
                    uint freeBlocks = blocksCount > usedBlocks ? blocksCount - usedBlocks : 0;
                    uint inodesCount = Math.Max(128u, blocksCount / 4u);
                    uint freeInodes = inodesCount > 11u ? inodesCount - 11u : inodesCount;

                    BitConverter.GetBytes(inodesCount).CopyTo(buf, 0x400);              // s_inodes_count
                    BitConverter.GetBytes(blocksCount).CopyTo(buf, 0x404);              // s_blocks_count_lo
                    BitConverter.GetBytes(blocksCount / 20u).CopyTo(buf, 0x408);        // s_r_blocks_count_lo
                    BitConverter.GetBytes(freeBlocks).CopyTo(buf, 0x40C);               // s_free_blocks_count_lo
                    BitConverter.GetBytes(freeInodes).CopyTo(buf, 0x410);               // s_free_inodes_count
                    BitConverter.GetBytes(2u).CopyTo(buf, 0x418);                       // s_log_block_size = 2 => 4096
                    BitConverter.GetBytes(2u).CopyTo(buf, 0x41C);                       // s_log_cluster_size = 2
                    BitConverter.GetBytes(32768u).CopyTo(buf, 0x420);                   // s_blocks_per_group
                    BitConverter.GetBytes(32768u).CopyTo(buf, 0x424);                   // s_clusters_per_group
                    BitConverter.GetBytes(Math.Min(inodesCount, 8192u)).CopyTo(buf, 0x428); // s_inodes_per_group
                    BitConverter.GetBytes((ushort)0xFFFF).CopyTo(buf, 0x436);           // s_max_mnt_count
                    BitConverter.GetBytes((ushort)0xEF53).CopyTo(buf, 0x438);           // s_magic
                    BitConverter.GetBytes((ushort)1).CopyTo(buf, 0x43A);                // s_state = clean
                    BitConverter.GetBytes((ushort)1).CopyTo(buf, 0x43C);                // s_errors = continue
                    BitConverter.GetBytes(1u).CopyTo(buf, 0x44C);                       // s_rev_level = EXT4_DYNAMIC_REV
                    BitConverter.GetBytes(11u).CopyTo(buf, 0x454);                      // s_first_ino
                    BitConverter.GetBytes((ushort)256).CopyTo(buf, 0x458);              // s_inode_size
                    uint compat = (part.FileSystem == FileSystemType.Ext3 || part.FileSystem == FileSystemType.Ext4) ? 0x0004u : 0x0000u;
                    uint incompat = (part.FileSystem == FileSystemType.Ext4) ? 0x0242u : 0x0002u;
                    uint roCompat = (part.FileSystem == FileSystemType.Ext4) ? 0x0003u : 0x0001u;
                    BitConverter.GetBytes(compat).CopyTo(buf, 0x45C);
                    BitConverter.GetBytes(incompat).CopyTo(buf, 0x460);
                    BitConverter.GetBytes(roCompat).CopyTo(buf, 0x464);

                    Guid extUuid = Guid.NewGuid();
                    extUuid.ToByteArray().CopyTo(buf, 0x468);
                    string extLbl = part.Label ?? string.Empty;
                    if (extLbl.Length > 16) extLbl = extLbl.Substring(0, 16);
                    Encoding.UTF8.GetBytes(extLbl).CopyTo(buf, 0x478);

                    part.UUID = extUuid.ToString("D");
                    part.UsedBytes = (blocksCount - freeBlocks) * (long)blockSize;
                    part.FreeBytes = freeBlocks * (long)blockSize;

                    if (buf.Length >= 0xE000)
                    {
                        PopulateExtDirectoryTree(buf, part, freeBlocks, freeInodes);
                    }
                    break;

                case FileSystemType.LinuxSwap:
                    BitConverter.GetBytes(1u).CopyTo(buf, 0x400);
                    BitConverter.GetBytes((uint)(part.PartitionLength / 4096 - 1)).CopyTo(buf, 0x404);
                    Guid swapUuid = Guid.NewGuid();
                    swapUuid.ToByteArray().CopyTo(buf, 0x40C);
                    string swapLbl = part.Label ?? "swap";
                    if (swapLbl.Length > 16) swapLbl = swapLbl.Substring(0, 16);
                    Encoding.UTF8.GetBytes(swapLbl).CopyTo(buf, 0x41C);
                    Encoding.ASCII.GetBytes("SWAPSPACE2").CopyTo(buf, 4086);
                    part.UUID = swapUuid.ToString("D");
                    part.UsedBytes = 0;
                    part.FreeBytes = part.PartitionLength;
                    break;

                case FileSystemType.Btrfs:
                    if (buf.Length >= 0x10500)
                    {
                        Guid bUuid = Guid.NewGuid();
                        byte[] bUuidBytes = bUuid.ToByteArray();
                        bUuidBytes.CopyTo(buf, 0x10020);                                 // fsid
                        BitConverter.GetBytes(0x10000UL).CopyTo(buf, 0x10030);            // bytenr
                        BitConverter.GetBytes(1UL).CopyTo(buf, 0x10038);                  // flags
                        Encoding.ASCII.GetBytes("_BHRfS_M").CopyTo(buf, 0x10040);         // magic
                        BitConverter.GetBytes(1UL).CopyTo(buf, 0x10048);                  // generation
                        BitConverter.GetBytes(0x14000UL).CopyTo(buf, 0x10050);            // root (FS leaf)
                        BitConverter.GetBytes(0x18000UL).CopyTo(buf, 0x10058);            // chunk_root
                        long bUsed = part.UsedBytes ?? Math.Min(part.PartitionLength / 4, 4L * 1048576L);
                        BitConverter.GetBytes(part.PartitionLength).CopyTo(buf, 0x10070); // total_bytes
                        BitConverter.GetBytes(bUsed).CopyTo(buf, 0x10078);                // bytes_used
                        BitConverter.GetBytes(6UL).CopyTo(buf, 0x10080);                  // root_dir_objectid
                        BitConverter.GetBytes(1UL).CopyTo(buf, 0x10088);                  // num_devices
                        BitConverter.GetBytes(4096U).CopyTo(buf, 0x10090);                // sectorsize
                        BitConverter.GetBytes(16384U).CopyTo(buf, 0x10094);               // nodesize
                        BitConverter.GetBytes(16384U).CopyTo(buf, 0x10098);               // leafsize
                        BitConverter.GetBytes(4096U).CopyTo(buf, 0x1009C);                // stripesize
                        BitConverter.GetBytes(97U).CopyTo(buf, 0x100A0);                  // sys_chunk_array_size
                        string bLbl = part.Label ?? string.Empty;
                        if (bLbl.Length > 64) bLbl = bLbl.Substring(0, 64);
                        Encoding.UTF8.GetBytes(bLbl).CopyTo(buf, 0x1012B);

                        // sys_chunk_array @ +0x1032B: 17-byte disk key + 80-byte single-stripe chunk mapping [0..PartitionLength) -> phys 0
                        int sc = 0x1032B;
                        BitConverter.GetBytes(256UL).CopyTo(buf, sc + 0);                 // BTRFS_FIRST_CHUNK_TREE_OBJECTID
                        buf[sc + 8] = 228;                                                // BTRFS_CHUNK_ITEM_KEY
                        BitConverter.GetBytes(0UL).CopyTo(buf, sc + 9);                   // logical offset 0
                        BitConverter.GetBytes((ulong)part.PartitionLength).CopyTo(buf, sc + 17); // chunk length
                        BitConverter.GetBytes(2UL).CopyTo(buf, sc + 25);                  // owner
                        BitConverter.GetBytes(65536UL).CopyTo(buf, sc + 33);              // stripe_len
                        BitConverter.GetBytes(1UL).CopyTo(buf, sc + 41);                  // BTRFS_BLOCK_GROUP_DATA
                        BitConverter.GetBytes(4096U).CopyTo(buf, sc + 49);                // io_align
                        BitConverter.GetBytes(4096U).CopyTo(buf, sc + 53);                // io_width
                        BitConverter.GetBytes(4096U).CopyTo(buf, sc + 57);                // sector_size
                        BitConverter.GetBytes((ushort)1).CopyTo(buf, sc + 61);            // num_stripes
                        BitConverter.GetBytes((ushort)0).CopyTo(buf, sc + 63);            // sub_stripes
                        BitConverter.GetBytes(1UL).CopyTo(buf, sc + 65);                  // stripe[0].devid
                        BitConverter.GetBytes(0UL).CopyTo(buf, sc + 73);                  // stripe[0].offset (physical 0)
                        bUuidBytes.CopyTo(buf, sc + 81);                                  // stripe[0].dev_uuid

                        part.UUID = FormatRfcUuid(bUuidBytes);
                        part.UsedBytes = bUsed;
                        part.FreeBytes = Math.Max(0, part.PartitionLength - bUsed);
                        PopulateReadOnlyFsCatalog(buf, part);
                        PopulateBtrfsFsTree(buf, part, bUuidBytes);
                    }
                    break;

                case FileSystemType.XFS:
                    uint xfsBlockSize = 4096;
                    ulong xfsTotalBlocks = (ulong)(part.PartitionLength / xfsBlockSize);
                    long xUsed = part.UsedBytes ?? Math.Min(part.PartitionLength / 4, 4L * 1048576L);
                    ulong xfsUsedBlocks = (ulong)(xUsed / xfsBlockSize);
                    ulong xfsFreeBlocks = xfsTotalBlocks > xfsUsedBlocks ? xfsTotalBlocks - xfsUsedBlocks : 0;
                    uint xfsAgBlocks = (uint)Math.Min(65536UL, Math.Max(16UL, xfsTotalBlocks));

                    Encoding.ASCII.GetBytes("XFSB").CopyTo(buf, 0);
                    WriteUInt32BE(buf, 4, xfsBlockSize);
                    WriteUInt64BE(buf, 8, xfsTotalBlocks);
                    Guid xUuid = Guid.NewGuid();
                    byte[] xUuidBytes = xUuid.ToByteArray();
                    xUuidBytes.CopyTo(buf, 32);
                    WriteUInt64BE(buf, 56, 128UL);                   // sb_rootino = 128
                    WriteUInt32BE(buf, 84, xfsAgBlocks);             // sb_agblocks
                    WriteUInt32BE(buf, 88, 1U);                      // sb_agcount
                    WriteUInt16BE(buf, 100, 0xB4A4);                 // sb_versionnum (v4 dirv2+attr)
                    WriteUInt16BE(buf, 102, 512);                    // sb_sectsize
                    WriteUInt16BE(buf, 104, 512);                    // sb_inodesize
                    WriteUInt16BE(buf, 106, 8);                      // sb_inopblock
                    string xLbl = part.Label ?? string.Empty;
                    if (xLbl.Length > 12) xLbl = xLbl.Substring(0, 12);
                    Encoding.UTF8.GetBytes(xLbl).CopyTo(buf, 108);
                    buf[120] = 12;                                   // sb_blocklog (4096)
                    buf[121] = 9;                                    // sb_sectlog (512)
                    buf[122] = 9;                                    // sb_inodelog (512)
                    buf[123] = 3;                                    // sb_inopblog (8 per block)
                    buf[124] = 16;                                   // sb_agblklog
                    WriteUInt64BE(buf, 144, xfsFreeBlocks);          // sb_fdblocks

                    part.UUID = FormatRfcUuid(xUuidBytes);
                    part.UsedBytes = (long)(xfsUsedBlocks * xfsBlockSize);
                    part.FreeBytes = (long)(xfsFreeBlocks * xfsBlockSize);
                    if (buf.Length >= 0x8000)
                        PopulateReadOnlyFsCatalog(buf, part);
                    if (buf.Length >= 0x1C000)
                        PopulateXfsFsTree(buf, part);
                    break;

                case FileSystemType.F2FS:
                    uint f2BlockSize = 4096;
                    ulong f2TotalBlocks = (ulong)Math.Max(1, part.PartitionLength / f2BlockSize);
                    long f2Used = part.UsedBytes ?? Math.Min(part.PartitionLength / 4, 4L * 1048576L);
                    ulong f2UsedBlocks = (ulong)(f2Used / f2BlockSize);
                    ulong f2UserBlocks = f2TotalBlocks > f2UsedBlocks ? f2TotalBlocks - f2UsedBlocks : 0;

                    BitConverter.GetBytes(0xF2F52010u).CopyTo(buf, 0x400);
                    BitConverter.GetBytes((ushort)1).CopyTo(buf, 0x404);
                    BitConverter.GetBytes((ushort)16).CopyTo(buf, 0x406);
                    BitConverter.GetBytes(9u).CopyTo(buf, 0x408);
                    BitConverter.GetBytes(3u).CopyTo(buf, 0x40C);
                    BitConverter.GetBytes(12u).CopyTo(buf, 0x410);
                    BitConverter.GetBytes(9u).CopyTo(buf, 0x414);
                    BitConverter.GetBytes(f2TotalBlocks).CopyTo(buf, 0x424);
                    BitConverter.GetBytes(f2UserBlocks).CopyTo(buf, 0x430);
                    Guid f2Uuid = Guid.NewGuid();
                    byte[] f2UuidBytes = f2Uuid.ToByteArray();
                    f2UuidBytes.CopyTo(buf, 0x46C);
                    string f2Lbl = part.Label ?? string.Empty;
                    if (f2Lbl.Length > 31) f2Lbl = f2Lbl.Substring(0, 31);
                    Encoding.Unicode.GetBytes(f2Lbl).CopyTo(buf, 0x47C);
                    WriteCustomUsedHint(buf, 0x1C0, f2Used);
                    part.UUID = FormatRfcUuid(f2UuidBytes);
                    part.UsedBytes = f2Used;
                    part.FreeBytes = Math.Max(0, part.PartitionLength - f2Used);
                    if (buf.Length >= 0x8000)
                        PopulateReadOnlyFsCatalog(buf, part);
                    break;

                case FileSystemType.ZFS:
                    Guid zUuid = Guid.NewGuid();
                    byte[] zUuidBytes = zUuid.ToByteArray();
                    long zUsed = part.UsedBytes ?? Math.Min(part.PartitionLength / 4, 4L * 1048576L);
                    if (buf.Length >= 0x1000)
                    {
                        Encoding.ASCII.GetBytes("ZFS_POOL").CopyTo(buf, 0x0800);
                        string zLbl = part.Label ?? "rpool";
                        if (zLbl.Length > 31) zLbl = zLbl.Substring(0, 31);
                        Encoding.UTF8.GetBytes(zLbl).CopyTo(buf, 0x0810);
                        zUuidBytes.CopyTo(buf, 0x0830);
                        BitConverter.GetBytes(part.PartitionLength).CopyTo(buf, 0x0840);
                        BitConverter.GetBytes(zUsed).CopyTo(buf, 0x0848);
                    }
                    if (buf.Length >= 0x11040)
                    {
                        BitConverter.GetBytes(0x00BAB10CUL).CopyTo(buf, 0x11000); // ub_magic
                        BitConverter.GetBytes(5000UL).CopyTo(buf, 0x11008);       // ub_version
                        BitConverter.GetBytes(42UL).CopyTo(buf, 0x11010);         // ub_txg
                    }
                    part.UUID = FormatRfcUuid(zUuidBytes);
                    part.UsedBytes = zUsed;
                    part.FreeBytes = Math.Max(0, part.PartitionLength - zUsed);
                    if (buf.Length >= 0x8000)
                        PopulateReadOnlyFsCatalog(buf, part);
                    break;

                case FileSystemType.SquashFS:
                    long sqUsed = part.UsedBytes ?? Math.Min(part.PartitionLength / 4, 2L * 1048576L);
                    Guid sqUuid = Guid.NewGuid();
                    byte[] sqUuidBytes = sqUuid.ToByteArray();
                    BitConverter.GetBytes(0x73717368u).CopyTo(buf, 0);          // "hsqs"
                    BitConverter.GetBytes(6u).CopyTo(buf, 4);                    // inodes
                    BitConverter.GetBytes(1762992000u).CopyTo(buf, 8);           // mkfs_time
                    BitConverter.GetBytes(131072u).CopyTo(buf, 12);              // block_size
                    BitConverter.GetBytes(1u).CopyTo(buf, 16);                   // fragments
                    BitConverter.GetBytes((ushort)1).CopyTo(buf, 20);            // compression (gzip)
                    BitConverter.GetBytes((ushort)17).CopyTo(buf, 22);           // block_log
                    BitConverter.GetBytes((ushort)4).CopyTo(buf, 28);            // s_major
                    BitConverter.GetBytes((ushort)0).CopyTo(buf, 30);            // s_minor
                    BitConverter.GetBytes((ulong)sqUsed).CopyTo(buf, 40);        // bytes_used
                    string sqLbl = part.Label ?? string.Empty;
                    if (sqLbl.Length > 31) sqLbl = sqLbl.Substring(0, 31);
                    Encoding.UTF8.GetBytes(sqLbl).CopyTo(buf, 0x80);
                    sqUuidBytes.CopyTo(buf, 0xA0);
                    part.UUID = FormatRfcUuid(sqUuidBytes);
                    part.UsedBytes = sqUsed;
                    part.FreeBytes = Math.Max(0, part.PartitionLength - sqUsed);
                    if (buf.Length >= 0x8000)
                        PopulateReadOnlyFsCatalog(buf, part);
                    break;

                case FileSystemType.ISO9660:
                    long isoUsed = part.UsedBytes ?? Math.Min(part.PartitionLength, 65536L);
                    Guid isoUuid = Guid.NewGuid();
                    if (buf.Length >= 0xB800)
                    {
                        uint volBlocks = (uint)Math.Max(24L, part.PartitionLength / 2048L);
                        buf[0x8000] = 1;
                        Encoding.ASCII.GetBytes("CD001").CopyTo(buf, 0x8001);
                        buf[0x8006] = 1;
                        string sysId = "LINUX_DEV_ENV".PadRight(32);
                        Encoding.ASCII.GetBytes(sysId).CopyTo(buf, 0x8008);
                        string isoLbl = (string.IsNullOrEmpty(part.Label) ? "DEV_ISO" : part.Label.ToUpperInvariant()).PadRight(32).Substring(0, 32);
                        Encoding.ASCII.GetBytes(isoLbl).CopyTo(buf, 0x8028);
                        BitConverter.GetBytes(volBlocks).CopyTo(buf, 0x8050);
                        WriteUInt32BE(buf, 0x8054, volBlocks);
                        BitConverter.GetBytes((ushort)1).CopyTo(buf, 0x8078);
                        WriteUInt16BE(buf, 0x807A, 1);
                        BitConverter.GetBytes((ushort)1).CopyTo(buf, 0x807C);
                        WriteUInt16BE(buf, 0x807E, 1);
                        BitConverter.GetBytes((ushort)2048).CopyTo(buf, 0x8080);
                        WriteUInt16BE(buf, 0x8082, 2048);

                        // 34-byte Root Directory Record at +0x809C pointing to LBA 18 (0x9000)
                        WriteIso9660DirectoryRecord(buf, 0x809C, 18u, 2048u, 0x02, "\x00");

                        // Primary Volume Descriptor Terminator at LBA 17 (0x8800)
                        buf[0x8800] = 0xFF;
                        Encoding.ASCII.GetBytes("CD001").CopyTo(buf, 0x8801);
                        buf[0x8806] = 1;

                        PopulateIso9660DirectoryTree(buf, part, isoUuid);
                    }
                    part.UUID = isoUuid.ToString("D");
                    part.UsedBytes = isoUsed;
                    part.FreeBytes = Math.Max(0, part.PartitionLength - isoUsed);
                    if (buf.Length >= 0x8000)
                        PopulateReadOnlyFsCatalog(buf, part);
                    break;

                case FileSystemType.UDF:
                    long udfUsed = part.UsedBytes ?? Math.Min(part.PartitionLength / 4, 2L * 1048576L);
                    Guid udfUuid = Guid.NewGuid();
                    byte[] udfUuidBytes = udfUuid.ToByteArray();
                    if (buf.Length >= 0x9800)
                    {
                        buf[0x8000] = 0;
                        Encoding.ASCII.GetBytes("BEA01").CopyTo(buf, 0x8001);
                        buf[0x8006] = 1;
                        buf[0x8800] = 0;
                        Encoding.ASCII.GetBytes("NSR03").CopyTo(buf, 0x8801);
                        buf[0x8806] = 1;
                        string udfLbl = part.Label ?? string.Empty;
                        if (udfLbl.Length > 31) udfLbl = udfLbl.Substring(0, 31);
                        Encoding.UTF8.GetBytes(udfLbl).CopyTo(buf, 0x8820);
                        udfUuidBytes.CopyTo(buf, 0x8840);
                        BitConverter.GetBytes(udfUsed).CopyTo(buf, 0x8850);
                        buf[0x9000] = 0;
                        Encoding.ASCII.GetBytes("TEA01").CopyTo(buf, 0x9001);
                        buf[0x9006] = 1;
                    }
                    part.UUID = FormatRfcUuid(udfUuidBytes);
                    part.UsedBytes = udfUsed;
                    part.FreeBytes = Math.Max(0, part.PartitionLength - udfUsed);
                    if (buf.Length >= 0x8000)
                        PopulateReadOnlyFsCatalog(buf, part);
                    break;

                case FileSystemType.APFS:
                    uint apfsBlockSize = 4096;
                    ulong apfsBlocks = (ulong)Math.Max(1, part.PartitionLength / apfsBlockSize);
                    BitConverter.GetBytes(1UL).CopyTo(buf, 0x08);
                    BitConverter.GetBytes(1UL).CopyTo(buf, 0x10);
                    BitConverter.GetBytes(0x80000001U).CopyTo(buf, 0x18);
                    Encoding.ASCII.GetBytes("NXSB").CopyTo(buf, 32);
                    BitConverter.GetBytes(apfsBlockSize).CopyTo(buf, 36);
                    BitConverter.GetBytes(apfsBlocks).CopyTo(buf, 40);
                    Guid apfsUuid = Guid.NewGuid();
                    byte[] apfsUuidBytes = apfsUuid.ToByteArray();
                    apfsUuidBytes.CopyTo(buf, 0x48);
                    BitConverter.GetBytes(16UL).CopyTo(buf, 0x58);
                    BitConverter.GetBytes(3UL).CopyTo(buf, 0x60);
                    BitConverter.GetBytes(1U).CopyTo(buf, 0x68);
                    BitConverter.GetBytes(1U).CopyTo(buf, 0x6C);
                    BitConverter.GetBytes(1L).CopyTo(buf, 0x70);
                    BitConverter.GetBytes(2L).CopyTo(buf, 0x78);
                    BitConverter.GetBytes(10UL).CopyTo(buf, 0xA0);
                    BitConverter.GetBytes(16U).CopyTo(buf, 0xB4);
                    BitConverter.GetBytes(0x402UL).CopyTo(buf, 0xB8);
                    long apfsUsed = part.UsedBytes ?? Math.Min(part.PartitionLength / 4, 2L * 1048576L);
                    part.UUID = FormatRfcUuid(apfsUuidBytes);
                    part.UsedBytes = apfsUsed;
                    part.FreeBytes = Math.Max(0, part.PartitionLength - apfsUsed);
                    if (buf.Length >= 0x8000)
                        PopulateReadOnlyFsCatalog(buf, part);
                    WriteCustomLabelHint(buf, 0x1A0, part.Label);
                    WriteCustomUsedHint(buf, 0x1C0, apfsUsed);
                    ReadOnlyFileSystemMountEngine.WriteApfsFletcher64(buf, 0, 4096);
                    break;

                case FileSystemType.HFSPlus:
                    Encoding.ASCII.GetBytes("LK").CopyTo(buf, 0);
                    WriteCustomLabelHint(buf, 0x1A0, part.Label);
                    uint hfsBlockSize = 4096;
                    uint hfsTotalBlocks = (uint)Math.Max(1, Math.Min((long)uint.MaxValue, part.PartitionLength / hfsBlockSize));
                    long hfsUsed = part.UsedBytes ?? Math.Min(part.PartitionLength / 4, 2L * 1048576L);
                    uint hfsUsedBlocks = (uint)Math.Min((long)hfsTotalBlocks, hfsUsed / hfsBlockSize);
                    uint hfsFreeBlocks = hfsTotalBlocks > hfsUsedBlocks ? hfsTotalBlocks - hfsUsedBlocks : 0;
                    Encoding.ASCII.GetBytes("H+").CopyTo(buf, 0x400);
                    WriteUInt16BE(buf, 0x402, 4);
                    WriteUInt32BE(buf, 0x404, 0x00002100u);
                    Encoding.ASCII.GetBytes("HFSJ").CopyTo(buf, 0x408);
                    WriteUInt32BE(buf, 0x428, hfsBlockSize);
                    WriteUInt32BE(buf, 0x42C, hfsTotalBlocks);
                    WriteUInt32BE(buf, 0x430, hfsFreeBlocks);
                    ulong hfsPlusUuid = ((ulong)(uint)Math.Abs((part.Label + part.StartingOffset + "HFS+").GetHashCode()) << 32) | 0x48465350UL;
                    BitConverter.GetBytes(hfsPlusUuid).CopyTo(buf, 0x468);
                    part.UUID = hfsPlusUuid.ToString("X16");
                    part.UsedBytes = hfsUsedBlocks * (long)hfsBlockSize;
                    part.FreeBytes = hfsFreeBlocks * (long)hfsBlockSize;
                    if (buf.Length >= 0x8000)
                        PopulateReadOnlyFsCatalog(buf, part);
                    break;

                case FileSystemType.HFS:
                    uint macAlBlkSize = (uint)Math.Max(4096L, ((part.PartitionLength / 65000L) / 512L + 1L) * 512L);
                    ushort macTotalBlks = (ushort)Math.Max(1, Math.Min(65535L, part.PartitionLength / macAlBlkSize));
                    long macUsed = part.UsedBytes ?? Math.Min(part.PartitionLength / 4, 1048576L);
                    ushort macUsedBlks = (ushort)Math.Min((int)macTotalBlks, (int)(macUsed / macAlBlkSize));
                    ushort macFreeBlks = (ushort)(macTotalBlks > macUsedBlks ? macTotalBlks - macUsedBlks : 0);
                    Encoding.ASCII.GetBytes("BD").CopyTo(buf, 0x400);
                    WriteUInt16BE(buf, 0x412, macTotalBlks);
                    WriteUInt32BE(buf, 0x414, macAlBlkSize);
                    WriteUInt16BE(buf, 0x422, macFreeBlks);
                    string macLbl = string.IsNullOrEmpty(part.Label) ? "Macintosh HD" : part.Label;
                    byte[] macLblBytes = Encoding.UTF8.GetBytes(macLbl);
                    int macLblLen = Math.Min(27, macLblBytes.Length);
                    buf[0x424] = (byte)macLblLen;
                    Array.Copy(macLblBytes, 0, buf, 0x425, macLblLen);
                    ulong macUuid = ((ulong)(uint)Math.Abs((macLbl + part.StartingOffset + "HFS").GetHashCode()) << 32) | 0x4D414348UL;
                    BitConverter.GetBytes(macUuid).CopyTo(buf, 0x474);
                    WriteCustomUsedHint(buf, 0x1C0, macUsed);
                    part.UUID = macUuid.ToString("X16");
                    part.UsedBytes = macUsed;
                    part.FreeBytes = Math.Max(0, part.PartitionLength - macUsed);
                    if (buf.Length >= 0x8000)
                        PopulateReadOnlyFsCatalog(buf, part);
                    break;

                case FileSystemType.LVM2:
                    if (buf.Length >= 1024)
                    {
                        Encoding.ASCII.GetBytes("LABELONE").CopyTo(buf, 512);
                        BitConverter.GetBytes(1UL).CopyTo(buf, 520);
                        BitConverter.GetBytes(32u).CopyTo(buf, 532);
                        Encoding.ASCII.GetBytes("LVM2 001").CopyTo(buf, 536);
                        Guid pvGuid = Guid.NewGuid();
                        string pvUuid = pvGuid.ToString("N");
                        Encoding.ASCII.GetBytes(pvUuid).CopyTo(buf, 544);
                        BitConverter.GetBytes((ulong)part.PartitionLength).CopyTo(buf, 576);
                        uint pvCrc = Crc32.Compute(buf, 532, 492);
                        BitConverter.GetBytes(pvCrc).CopyTo(buf, 528);
                        part.UUID = pvUuid;
                    }
                    part.UsedBytes = 0;
                    part.FreeBytes = part.PartitionLength;
                    if (buf.Length >= 0x8000)
                        PopulateReadOnlyFsCatalog(buf, part);
                    break;

                case FileSystemType.RAW:
                    // buf is zeroed, clearing any stale filesystem signatures
                    part.UUID = string.Empty;
                    part.UsedBytes = null;
                    part.FreeBytes = null;
                    break;
            }

            if (part.FileSystem == FileSystemType.exFAT)
            {
                WriteExFatStructures(disk, part);
                return;
            }

            WriteBytes(disk, part.StartingOffset, buf);

            if ((part.FileSystem == FileSystemType.FAT32 || part.FileSystem == FileSystemType.EfiSystem) && fat32Sz > 0 && fat32SecPerClus > 0)
                WriteFat32TailStructures(disk, part, fat32SecPerClus, fat32Sz);
            else if (part.FileSystem == FileSystemType.FAT16 && fat16Sz > 0)
                WriteFat16TailStructures(disk, part, fat16Sz);
        }

        private static string SanitizeFatLabel(string label, bool forRootEntry)
        {
            const string invalid = "\"*+,/:;<=>?[\\]|";
            string raw = (label ?? string.Empty).ToUpperInvariant().Trim();
            var sb = new StringBuilder(11);
            for (int i = 0; i < raw.Length && sb.Length < 11; i++)
            {
                char ch = raw[i];
                if (ch < 0x20 || ch > 0x7E || invalid.IndexOf(ch) >= 0)
                    continue;
                sb.Append(ch);
            }
            string clean = sb.ToString().Trim();
            if (clean.Length == 0)
                return forRootEntry ? string.Empty : "NO NAME    ";
            return clean.PadRight(11).Substring(0, 11);
        }

        public static uint ComputeExFatChecksum(byte[] data, int length, bool isBootSectorRegion)
        {
            uint checksum = 0;
            int count = Math.Min(length, data != null ? data.Length : 0);
            for (int index = 0; index < count; index++)
            {
                if (isBootSectorRegion && (index == 106 || index == 107 || index == 112))
                    continue;
                checksum = ((checksum & 1u) != 0 ? 0x80000000u : 0u) + (checksum >> 1) + data[index];
            }
            return checksum;
        }

        private static byte[] BuildExFatUpCaseTable(out uint checksum)
        {
            var outBytes = new List<byte>(8192);
            int cp = 0;
            while (cp <= 0xFFFF)
            {
                ushort upper = (ushort)char.ToUpperInvariant((char)cp);
                if (upper == (ushort)cp)
                {
                    int runLen = 1;
                    while (cp + runLen <= 0xFFFF &&
                           (ushort)char.ToUpperInvariant((char)(cp + runLen)) == (ushort)(cp + runLen))
                    {
                        runLen++;
                    }
                    if (runLen >= 3)
                    {
                        outBytes.Add(0xFF);
                        outBytes.Add(0xFF);
                        outBytes.Add((byte)(runLen & 0xFF));
                        outBytes.Add((byte)((runLen >> 8) & 0xFF));
                        cp += runLen;
                        continue;
                    }
                }
                outBytes.Add((byte)(upper & 0xFF));
                outBytes.Add((byte)((upper >> 8) & 0xFF));
                cp++;
            }

            byte[] result = outBytes.ToArray();
            checksum = ComputeExFatChecksum(result, result.Length, false);
            return result;
        }

        private static void WriteExFatStructures(DiskInfo disk, PartitionEntry part)
        {
            long totalSectors = part.PartitionLength / 512L;
            if (totalSectors < 48L) return;

            int secPerClusShift;
            if (part.PartitionLength <= 256L * 1048576L) secPerClusShift = 3;          // 4 KiB
            else if (part.PartitionLength <= 32L * 1073741824L) secPerClusShift = 6;   // 32 KiB
            else secPerClusShift = 8;                                                   // 128 KiB

            while (secPerClusShift > 1 && totalSectors < 48L + 8L * (1L << secPerClusShift))
                secPerClusShift--;

            uint secPerClus = 1u << secPerClusShift;
            uint clusBytes = secPerClus * 512u;
            uint fatOffset = 24u;

            long availAfterBoot = Math.Max(16L, totalSectors - 32L);
            uint estClusters = (uint)Math.Max(16L, availAfterBoot / secPerClus);
            uint fatLength = (uint)Math.Max(1L, ((long)(estClusters + 2u) * 4L + 511L) / 512L);

            long rawHeapStart = fatOffset + fatLength;
            long alignedHeap = ((rawHeapStart + secPerClus - 1L) / secPerClus) * secPerClus;
            if (alignedHeap + (long)secPerClus * 12L > totalSectors)
                alignedHeap = rawHeapStart;

            uint clusterHeapOffset = (uint)alignedHeap;
            uint clusterCount = (uint)Math.Max(8L, Math.Min((long)uint.MaxValue - 10L, (totalSectors - clusterHeapOffset) / secPerClus));
            uint maxFatEntries = (uint)Math.Min((long)uint.MaxValue, ((long)fatLength * 512L) / 4L);
            if (maxFatEntries > 2u && clusterCount + 2u > maxFatEntries)
                clusterCount = maxFatEntries - 2u;

            uint upcaseChecksum;
            byte[] upcaseBytes = BuildExFatUpCaseTable(out upcaseChecksum);

            ulong bitmapLenBytes = (clusterCount + 7UL) / 8UL;
            uint bitmapClusters = (uint)Math.Max(1UL, (bitmapLenBytes + clusBytes - 1UL) / clusBytes);
            uint upcaseFirst = 2u + bitmapClusters;
            uint upcaseClusters = (uint)Math.Max(1UL, ((ulong)upcaseBytes.Length + clusBytes - 1UL) / clusBytes);
            uint rootFirst = upcaseFirst + upcaseClusters;
            uint totalUsedClusters = bitmapClusters + upcaseClusters + 1u;

            byte[] mainBoot = new byte[12 * 512];
            mainBoot[0] = 0xEB; mainBoot[1] = 0x76; mainBoot[2] = 0x90;
            Encoding.ASCII.GetBytes("EXFAT   ").CopyTo(mainBoot, 3);
            BitConverter.GetBytes((ulong)Math.Max(0L, part.StartingOffset / 512L)).CopyTo(mainBoot, 0x40);
            BitConverter.GetBytes((ulong)totalSectors).CopyTo(mainBoot, 0x48);
            BitConverter.GetBytes(fatOffset).CopyTo(mainBoot, 0x50);
            BitConverter.GetBytes(fatLength).CopyTo(mainBoot, 0x54);
            BitConverter.GetBytes(clusterHeapOffset).CopyTo(mainBoot, 0x58);
            BitConverter.GetBytes(clusterCount).CopyTo(mainBoot, 0x5C);
            BitConverter.GetBytes(rootFirst).CopyTo(mainBoot, 0x60);
            uint exId = (uint)(0x45584600 ^ (int)part.StartingOffset);
            BitConverter.GetBytes(exId).CopyTo(mainBoot, 0x64);
            BitConverter.GetBytes((ushort)0x0100).CopyTo(mainBoot, 0x68); // FileSystemRevision 1.00
            BitConverter.GetBytes((ushort)0).CopyTo(mainBoot, 0x6A);      // VolumeFlags
            mainBoot[0x6C] = 9;                                           // BytesPerSectorShift (512 B)
            mainBoot[0x6D] = (byte)secPerClusShift;                       // SectorsPerClusterShift
            mainBoot[0x6E] = 1;                                           // NumberOfFats
            mainBoot[0x6F] = 0x80;                                        // DriveSelect
            mainBoot[0x70] = 0xFF;                                        // PercentInUse

            long exUsed = part.UsedBytes ?? Math.Min(part.PartitionLength, (long)totalUsedClusters * clusBytes);
            if (disk.IsVirtualImage)
                WriteCustomUsedHint(mainBoot, 0x1C0, exUsed);
            mainBoot[510] = 0x55; mainBoot[511] = 0xAA;

            for (int ext = 1; ext <= 8; ext++)
            {
                mainBoot[ext * 512 + 510] = 0x55;
                mainBoot[ext * 512 + 511] = 0xAA;
            }

            uint bootChecksum = ComputeExFatChecksum(mainBoot, 11 * 512, true);
            byte[] chkBytes = BitConverter.GetBytes(bootChecksum);
            for (int i = 0; i < 128; i++)
                Array.Copy(chkBytes, 0, mainBoot, 11 * 512 + i * 4, 4);

            // Main Boot Region (LBA 0..11) + Backup Boot Region (LBA 12..23)
            WriteBytes(disk, part.StartingOffset, mainBoot);
            WriteBytes(disk, part.StartingOffset + 12L * 512L, mainBoot);

            // Zero entire FAT and populate cluster chains for Bitmap, Up-Case Table, and Root Directory
            long fatStartByte = part.StartingOffset + (long)fatOffset * 512L;
            long fatTotalBytes = (long)fatLength * 512L;
            WriteZeroedRegion(disk, fatStartByte, fatTotalBytes);

            int fatHeadLen = (int)Math.Min(fatTotalBytes, (((long)(rootFirst + 2u) * 4L + 511L) / 512L) * 512L);
            byte[] fatHead = new byte[Math.Max(512, fatHeadLen)];
            BitConverter.GetBytes(0xFFFFFFF8u).CopyTo(fatHead, 0);
            BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(fatHead, 4);
            for (uint c = 2u; c < 2u + bitmapClusters; c++)
            {
                uint next = (c + 1u < 2u + bitmapClusters) ? (c + 1u) : 0xFFFFFFFFu;
                BitConverter.GetBytes(next).CopyTo(fatHead, (int)(c * 4u));
            }
            for (uint c = upcaseFirst; c < upcaseFirst + upcaseClusters; c++)
            {
                uint next = (c + 1u < upcaseFirst + upcaseClusters) ? (c + 1u) : 0xFFFFFFFFu;
                BitConverter.GetBytes(next).CopyTo(fatHead, (int)(c * 4u));
            }
            BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(fatHead, (int)(rootFirst * 4u));
            WriteBytes(disk, fatStartByte, fatHead);

            // Allocation Bitmap at Cluster 2
            long heapStartByte = part.StartingOffset + (long)clusterHeapOffset * 512L;
            int bitmapWriteLen = (int)(bitmapClusters * clusBytes);
            byte[] bitmapBuf = new byte[bitmapWriteLen];
            for (uint b = 0; b < totalUsedClusters && (b >> 3) < (uint)bitmapWriteLen; b++)
                bitmapBuf[b >> 3] |= (byte)(1 << (int)(b & 7));
            WriteBytes(disk, heapStartByte, bitmapBuf);

            // Up-Case Table at upcaseFirst
            long upcaseStartByte = heapStartByte + (long)(upcaseFirst - 2u) * clusBytes;
            int upcaseWriteLen = (int)(upcaseClusters * clusBytes);
            byte[] upcaseBuf = new byte[Math.Max(upcaseBytes.Length, upcaseWriteLen)];
            Array.Copy(upcaseBytes, 0, upcaseBuf, 0, upcaseBytes.Length);
            WriteBytes(disk, upcaseStartByte, upcaseBuf);

            // Root Directory at rootFirst (Volume Label 0x83 + Bitmap 0x81 + Up-Case 0x82)
            long rootStartByte = heapStartByte + (long)(rootFirst - 2u) * clusBytes;
            byte[] rootBuf = new byte[clusBytes];
            int slot = 0;
            string cleanVolLbl = (part.Label ?? string.Empty).Trim();
            if (cleanVolLbl.Length > 11)
                cleanVolLbl = cleanVolLbl.Substring(0, 11);
            if (!string.IsNullOrEmpty(cleanVolLbl))
            {
                rootBuf[slot] = 0x83;
                rootBuf[slot + 1] = (byte)cleanVolLbl.Length;
                Encoding.Unicode.GetBytes(cleanVolLbl).CopyTo(rootBuf, slot + 2);
                slot += 32;
            }

            // Allocation Bitmap Entry (0x81)
            rootBuf[slot] = 0x81;
            rootBuf[slot + 1] = 0x00;
            BitConverter.GetBytes(2u).CopyTo(rootBuf, slot + 20);
            BitConverter.GetBytes(bitmapLenBytes).CopyTo(rootBuf, slot + 24);
            slot += 32;

            // Up-Case Table Entry (0x82)
            rootBuf[slot] = 0x82;
            BitConverter.GetBytes(upcaseChecksum).CopyTo(rootBuf, slot + 4);
            BitConverter.GetBytes(upcaseFirst).CopyTo(rootBuf, slot + 20);
            BitConverter.GetBytes((ulong)upcaseBytes.Length).CopyTo(rootBuf, slot + 24);

            WriteBytes(disk, rootStartByte, rootBuf);

            part.UUID = string.Format("{0:X4}-{1:X4}", (exId >> 16) & 0xFFFF, exId & 0xFFFF);
            part.UsedBytes = exUsed;
            part.FreeBytes = Math.Max(0, part.PartitionLength - exUsed);
        }

        private static void WriteZeroedRegion(DiskInfo disk, long offset, long length)
        {
            if (length <= 0) return;
            const int chunkSize = 1048576;
            byte[] zeros = new byte[chunkSize];
            long done = 0;
            while (done < length)
            {
                int n = (int)Math.Min(chunkSize, length - done);
                byte[] slice = n == chunkSize ? zeros : new byte[n];
                WriteBytes(disk, offset + done, slice);
                done += n;
            }
        }

        private static void WriteFat32TailStructures(DiskInfo disk, PartitionEntry part, int secPerClus, uint fatSz32)
        {
            long fatBytes = (long)fatSz32 * 512L;
            long fat1Rel = 32L * 512L;
            long fat2Rel = (32L + fatSz32) * 512L;
            long rootRel = (32L + 2L * fatSz32) * 512L;
            long clusBytes = Math.Max(512L, (long)secPerClus * 512L);

            if (fat1Rel + fatBytes <= part.PartitionLength)
                WriteZeroedRegion(disk, part.StartingOffset + fat1Rel, fatBytes);
            if (fat2Rel + fatBytes <= part.PartitionLength)
                WriteZeroedRegion(disk, part.StartingOffset + fat2Rel, fatBytes);

            byte[] fatHead = new byte[512];
            BitConverter.GetBytes(0x0FFFFFF8u).CopyTo(fatHead, 0);
            BitConverter.GetBytes(0x0FFFFFFFu).CopyTo(fatHead, 4);
            BitConverter.GetBytes(0x0FFFFFFFu).CopyTo(fatHead, 8);
            if (fat1Rel + 512 <= part.PartitionLength)
                WriteBytes(disk, part.StartingOffset + fat1Rel, fatHead);
            if (fat2Rel + 512 <= part.PartitionLength)
                WriteBytes(disk, part.StartingOffset + fat2Rel, fatHead);

            if (rootRel + clusBytes <= part.PartitionLength)
            {
                byte[] rootClus = new byte[(int)clusBytes];
                string rootLbl = SanitizeFatLabel(part.Label, true);
                if (!string.IsNullOrEmpty(rootLbl))
                {
                    Encoding.ASCII.GetBytes(rootLbl).CopyTo(rootClus, 0);
                    rootClus[11] = 0x08; // ATTR_VOLUME_ID
                }
                WriteBytes(disk, part.StartingOffset + rootRel, rootClus);
            }
        }

        private static void WriteFat16TailStructures(DiskInfo disk, PartitionEntry part, uint fatSz16)
        {
            long fatBytes = (long)fatSz16 * 512L;
            long fat1Rel = 512L;
            long fat2Rel = 512L + fatBytes;
            long rootRel = 512L + 2L * fatBytes;
            const long rootBytes = 16384L;

            if (fat1Rel + fatBytes <= part.PartitionLength)
                WriteZeroedRegion(disk, part.StartingOffset + fat1Rel, fatBytes);
            if (fat2Rel + fatBytes <= part.PartitionLength)
                WriteZeroedRegion(disk, part.StartingOffset + fat2Rel, fatBytes);

            byte[] fatHead = new byte[512];
            fatHead[0] = 0xF8; fatHead[1] = 0xFF;
            fatHead[2] = 0xFF; fatHead[3] = 0xFF;
            if (fat1Rel + 512 <= part.PartitionLength)
                WriteBytes(disk, part.StartingOffset + fat1Rel, fatHead);
            if (fat2Rel + 512 <= part.PartitionLength)
                WriteBytes(disk, part.StartingOffset + fat2Rel, fatHead);

            if (rootRel + rootBytes <= part.PartitionLength)
            {
                byte[] rootDir = new byte[(int)rootBytes];
                string rootLbl = SanitizeFatLabel(part.Label, true);
                if (!string.IsNullOrEmpty(rootLbl))
                {
                    Encoding.ASCII.GetBytes(rootLbl).CopyTo(rootDir, 0);
                    rootDir[11] = 0x08; // ATTR_VOLUME_ID
                }
                WriteBytes(disk, part.StartingOffset + rootRel, rootDir);
            }
        }

        /// <summary>
        /// Updates in-place superblock total/free block counters after a partition grow/shrink/move
        /// while preserving existing user data and filesystem UUIDs.
        /// </summary>
        public static void ResizeFileSystemInPlace(DiskInfo disk, PartitionEntry part, long oldLength)
        {
            if (part.IsUnallocated || part.PartitionLength < 65536) return;

            int headerLen = (int)Math.Min(131072, part.PartitionLength);
            byte[] buf = ReadBytes(disk, part.StartingOffset, headerLen);
            if (buf.Length < 512) return;

            long totalSectors = part.PartitionLength / 512;

            if (MatchesAscii(buf, 3, "NTFS    "))
            {
                BitConverter.GetBytes((ulong)totalSectors).CopyTo(buf, 0x28);
                BitConverter.GetBytes((ulong)(totalSectors / 16)).CopyTo(buf, 0x30);
                long used = ReadCustomUsedHint(buf, 0x1C0);
                if (used < 0 && part.UsedBytes.HasValue) used = part.UsedBytes.Value;
                if (used >= 0)
                {
                    if (used > part.PartitionLength) used = part.PartitionLength;
                    WriteCustomUsedHint(buf, 0x1C0, used);
                    part.UsedBytes = used;
                    part.FreeBytes = part.PartitionLength - used;
                }
                WriteBytes(disk, part.StartingOffset, buf);
                return;
            }

            if (buf.Length >= 96 && MatchesAscii(buf, 0x52, "FAT32   "))
            {
                BitConverter.GetBytes((uint)totalSectors).CopyTo(buf, 0x20);
                long used = ReadCustomUsedHint(buf, 0x1C0);
                if (used < 0 && part.UsedBytes.HasValue) used = part.UsedBytes.Value;
                if (used >= 0)
                {
                    if (used > part.PartitionLength) used = part.PartitionLength;
                    WriteCustomUsedHint(buf, 0x1C0, used);
                    part.UsedBytes = used;
                    part.FreeBytes = part.PartitionLength - used;
                }
                WriteBytes(disk, part.StartingOffset, buf);
                return;
            }

            if (MatchesAscii(buf, 3, "EXFAT   "))
            {
                if (disk.IsVirtualImage && !string.IsNullOrEmpty(disk.BackingFilePath) && oldLength > 0 && part.PartitionLength != oldLength)
                {
                    using (var vIo = new ExFatResizer.VirtualFileIo(disk.BackingFilePath))
                    {
                        ExFatResizer.ResizeOrMoveExFat(vIo, part, part.StartingOffset, oldLength, part.StartingOffset, part.PartitionLength, true, null, null);
                    }
                    return;
                }

                BitConverter.GetBytes((ulong)totalSectors).CopyTo(buf, 0x48);
                long used = ReadCustomUsedHint(buf, 0x1C0);
                if (used < 0 && part.UsedBytes.HasValue) used = part.UsedBytes.Value;
                if (used >= 0)
                {
                    if (used > part.PartitionLength) used = part.PartitionLength;
                    if (disk.IsVirtualImage)
                        WriteCustomUsedHint(buf, 0x1C0, used);
                    part.UsedBytes = used;
                    part.FreeBytes = part.PartitionLength - used;
                }
                if (buf.Length >= 12 * 512 && buf[0x6C] == 9)
                {
                    uint heapOff = BitConverter.ToUInt32(buf, 0x58);
                    int spcShift = buf[0x6D];
                    if (spcShift >= 1 && spcShift <= 20 && totalSectors > heapOff)
                    {
                        uint newClusCount = (uint)Math.Max(8L, Math.Min((long)uint.MaxValue - 10L, (totalSectors - heapOff) >> spcShift));
                        BitConverter.GetBytes(newClusCount).CopyTo(buf, 0x5C);
                    }
                    uint chk = ComputeExFatChecksum(buf, 11 * 512, true);
                    byte[] chkBytes = BitConverter.GetBytes(chk);
                    for (int i = 0; i < 128; i++)
                        Array.Copy(chkBytes, 0, buf, 11 * 512 + i * 4, 4);
                    if (buf.Length >= 24 * 512)
                        Array.Copy(buf, 0, buf, 12 * 512, 12 * 512);
                }
                WriteBytes(disk, part.StartingOffset, buf);
                return;
            }

            if (buf.Length >= 2048 && BitConverter.ToUInt16(buf, 0x438) == 0xEF53)
            {
                uint logBlockSize = BitConverter.ToUInt32(buf, 0x418);
                uint blockSize = (uint)(1024 << (int)Math.Min(6, logBlockSize));
                uint oldBlocks = BitConverter.ToUInt32(buf, 0x404);
                uint oldFree = BitConverter.ToUInt32(buf, 0x40C);
                uint usedBlocks = oldBlocks > oldFree ? oldBlocks - oldFree : 0;

                uint newBlocks = (uint)Math.Max(1, part.PartitionLength / blockSize);
                if (usedBlocks > newBlocks) usedBlocks = newBlocks;
                uint newFree = newBlocks - usedBlocks;

                BitConverter.GetBytes(Math.Max(128u, newBlocks / 4u)).CopyTo(buf, 0x400);
                BitConverter.GetBytes(newBlocks).CopyTo(buf, 0x404);
                BitConverter.GetBytes(newFree).CopyTo(buf, 0x40C);
                part.UsedBytes = usedBlocks * (long)blockSize;
                part.FreeBytes = newFree * (long)blockSize;
                WriteBytes(disk, part.StartingOffset, buf);
                return;
            }

            if (buf.Length >= 4096 && MatchesAscii(buf, 4086, "SWAPSPACE2"))
            {
                BitConverter.GetBytes((uint)(part.PartitionLength / 4096 - 1)).CopyTo(buf, 0x404);
                part.UsedBytes = 0;
                part.FreeBytes = part.PartitionLength;
                WriteBytes(disk, part.StartingOffset, buf);
                return;
            }

            if (buf.Length >= 0x10100 && MatchesAscii(buf, 0x10040, "_BHRfS_M"))
            {
                long used = BitConverter.ToInt64(buf, 0x10078);
                if (used < 0 || used > part.PartitionLength) used = Math.Min(part.PartitionLength, 4L * 1048576L);
                BitConverter.GetBytes(part.PartitionLength).CopyTo(buf, 0x10070);
                BitConverter.GetBytes(used).CopyTo(buf, 0x10078);
                part.UsedBytes = used;
                part.FreeBytes = Math.Max(0, part.PartitionLength - used);
                WriteBytes(disk, part.StartingOffset, buf);
                return;
            }

            if (MatchesAscii(buf, 0, "XFSB"))
            {
                uint beBlk = ReadUInt32BE(buf, 4);
                bool isBe = beBlk >= 512 && beBlk <= 65536 && (beBlk & (beBlk - 1)) == 0;
                uint blockSize = isBe ? beBlk : BitConverter.ToUInt32(buf, 4);
                if (blockSize == 0) blockSize = 4096;
                ulong oldDblocks = isBe ? ReadUInt64BE(buf, 8) : BitConverter.ToUInt64(buf, 8);
                ulong oldFdblocks = isBe ? (buf.Length >= 152 ? ReadUInt64BE(buf, 144) : 0UL) : BitConverter.ToUInt64(buf, 16);
                ulong usedBlocks = oldDblocks > oldFdblocks ? oldDblocks - oldFdblocks : 0;

                ulong newDblocks = (ulong)(part.PartitionLength / blockSize);
                if (usedBlocks > newDblocks) usedBlocks = newDblocks;
                ulong newFdblocks = newDblocks - usedBlocks;

                if (isBe)
                {
                    WriteUInt64BE(buf, 8, newDblocks);
                    if (buf.Length >= 152) WriteUInt64BE(buf, 144, newFdblocks);
                }
                else
                {
                    BitConverter.GetBytes(newDblocks).CopyTo(buf, 8);
                    BitConverter.GetBytes(newFdblocks).CopyTo(buf, 16);
                }
                part.UsedBytes = (long)(usedBlocks * blockSize);
                part.FreeBytes = (long)(newFdblocks * blockSize);
                WriteBytes(disk, part.StartingOffset, buf);
                return;
            }

            if (MatchesAscii(buf, 3, "ReFS"))
            {
                BitConverter.GetBytes((ulong)totalSectors).CopyTo(buf, 0x20);
                long used = ReadCustomUsedHint(buf, 0x1C0);
                if (used < 0 && buf.Length >= 0x30) used = BitConverter.ToInt64(buf, 0x28);
                if (used < 0 && part.UsedBytes.HasValue) used = part.UsedBytes.Value;
                if (used >= 0)
                {
                    if (used > part.PartitionLength) used = part.PartitionLength;
                    BitConverter.GetBytes(used).CopyTo(buf, 0x28);
                    WriteCustomUsedHint(buf, 0x1C0, used);
                    part.UsedBytes = used;
                    part.FreeBytes = part.PartitionLength - used;
                }
                WriteBytes(disk, part.StartingOffset, buf);
                return;
            }

            if (buf.Length >= 64 && MatchesAscii(buf, 0x36, "FAT16   "))
            {
                if (totalSectors <= 65535)
                {
                    BitConverter.GetBytes((ushort)totalSectors).CopyTo(buf, 0x13);
                    BitConverter.GetBytes(0u).CopyTo(buf, 0x20);
                }
                else
                {
                    BitConverter.GetBytes((ushort)0).CopyTo(buf, 0x13);
                    BitConverter.GetBytes((uint)totalSectors).CopyTo(buf, 0x20);
                }
                long used = ReadCustomUsedHint(buf, 0x1C0);
                if (used < 0 && part.UsedBytes.HasValue) used = part.UsedBytes.Value;
                if (used >= 0)
                {
                    if (used > part.PartitionLength) used = part.PartitionLength;
                    WriteCustomUsedHint(buf, 0x1C0, used);
                    part.UsedBytes = used;
                    part.FreeBytes = part.PartitionLength - used;
                }
                WriteBytes(disk, part.StartingOffset, buf);
                return;
            }

            if (buf.Length >= 0x500 && BitConverter.ToUInt32(buf, 0x400) == 0xF2F52010u)
            {
                ulong blkCount = (ulong)Math.Max(1, part.PartitionLength / 4096L);
                long used = ReadCustomUsedHint(buf, 0x1C0);
                if (used < 0) used = part.UsedBytes ?? 0L;
                if (used > part.PartitionLength) used = part.PartitionLength;
                ulong usedBlks = (ulong)(used / 4096L);
                ulong userBlks = blkCount > usedBlks ? blkCount - usedBlks : 0;
                BitConverter.GetBytes(blkCount).CopyTo(buf, 0x424);
                BitConverter.GetBytes(userBlks).CopyTo(buf, 0x430);
                WriteCustomUsedHint(buf, 0x1C0, used);
                part.UsedBytes = used;
                part.FreeBytes = Math.Max(0, part.PartitionLength - used);
                WriteBytes(disk, part.StartingOffset, buf);
                return;
            }

            if (buf.Length >= 0x0850 && MatchesAscii(buf, 0x0800, "ZFS_POOL"))
            {
                long used = BitConverter.ToInt64(buf, 0x0848);
                if (used < 0 || used > part.PartitionLength) used = part.UsedBytes ?? 0L;
                if (used > part.PartitionLength) used = part.PartitionLength;
                BitConverter.GetBytes(part.PartitionLength).CopyTo(buf, 0x0840);
                BitConverter.GetBytes(used).CopyTo(buf, 0x0848);
                part.UsedBytes = used;
                part.FreeBytes = Math.Max(0, part.PartitionLength - used);
                WriteBytes(disk, part.StartingOffset, buf);
                return;
            }

            if (buf.Length >= 0xC0 && BitConverter.ToUInt32(buf, 0) == 0x73717368u)
            {
                long used = (long)BitConverter.ToUInt64(buf, 40);
                if (used < 0 || used > part.PartitionLength) used = part.PartitionLength;
                BitConverter.GetBytes((ulong)used).CopyTo(buf, 40);
                part.UsedBytes = used;
                part.FreeBytes = Math.Max(0, part.PartitionLength - used);
                WriteBytes(disk, part.StartingOffset, buf);
                return;
            }

            if (buf.Length >= 0x9010 && MatchesAscii(buf, 0x8001, "BEA01") &&
                (MatchesAscii(buf, 0x8801, "NSR02") || MatchesAscii(buf, 0x8801, "NSR03")))
            {
                long used = BitConverter.ToInt64(buf, 0x8850);
                if (used < 0 || used > part.PartitionLength) used = part.UsedBytes ?? 0L;
                if (used > part.PartitionLength) used = part.PartitionLength;
                BitConverter.GetBytes(used).CopyTo(buf, 0x8850);
                part.UsedBytes = used;
                part.FreeBytes = Math.Max(0, part.PartitionLength - used);
                WriteBytes(disk, part.StartingOffset, buf);
                return;
            }

            if (buf.Length >= 0x8800 && MatchesAscii(buf, 0x8001, "CD001"))
            {
                uint volBlocks = (uint)Math.Max(24L, part.PartitionLength / 2048L);
                BitConverter.GetBytes(volBlocks).CopyTo(buf, 0x8050);
                WriteUInt32BE(buf, 0x8054, volBlocks);
                long used = part.UsedBytes ?? Math.Min(part.PartitionLength, 65536L);
                if (used > part.PartitionLength) used = part.PartitionLength;
                part.UsedBytes = used;
                part.FreeBytes = Math.Max(0, part.PartitionLength - used);
                WriteBytes(disk, part.StartingOffset, buf);
                return;
            }

            if (MatchesAscii(buf, 32, "NXSB") || MatchesAscii(buf, 32, "APSB"))
            {
                uint blockSize = BitConverter.ToUInt32(buf, 36);
                if (blockSize == 0) blockSize = 4096;
                ulong newBlocks = (ulong)Math.Max(1, part.PartitionLength / blockSize);
                BitConverter.GetBytes(newBlocks).CopyTo(buf, 40);
                long used = ReadCustomUsedHint(buf, 0x1C0);
                if (used < 0 && part.UsedBytes.HasValue) used = part.UsedBytes.Value;
                if (used >= 0)
                {
                    if (used > part.PartitionLength) used = part.PartitionLength;
                    WriteCustomUsedHint(buf, 0x1C0, used);
                    part.UsedBytes = used;
                    part.FreeBytes = part.PartitionLength - used;
                }
                WriteBytes(disk, part.StartingOffset, buf);
                return;
            }

            if (buf.Length >= 1536 && (MatchesAscii(buf, 0x400, "H+") || MatchesAscii(buf, 0x400, "HX")))
            {
                uint blockSize = ReadUInt32BE(buf, 0x428);
                if (blockSize == 0) blockSize = 4096;
                uint oldTotal = ReadUInt32BE(buf, 0x42C);
                uint oldFree = ReadUInt32BE(buf, 0x430);
                uint usedBlocks = oldTotal > oldFree ? oldTotal - oldFree : 0;

                uint newTotal = (uint)Math.Max(1, Math.Min((long)uint.MaxValue, part.PartitionLength / blockSize));
                if (usedBlocks > newTotal) usedBlocks = newTotal;
                uint newFree = newTotal - usedBlocks;

                WriteUInt32BE(buf, 0x42C, newTotal);
                WriteUInt32BE(buf, 0x430, newFree);
                part.UsedBytes = usedBlocks * (long)blockSize;
                part.FreeBytes = newFree * (long)blockSize;
                WriteBytes(disk, part.StartingOffset, buf);
                return;
            }

            if (buf.Length >= 1200 && MatchesAscii(buf, 0x400, "BD"))
            {
                uint oldAlBlkSize = ReadUInt32BE(buf, 0x414);
                if (oldAlBlkSize == 0) oldAlBlkSize = 4096;
                ushort oldTotal = ReadUInt16BE(buf, 0x412);
                ushort oldFree = ReadUInt16BE(buf, 0x422);
                long used = ReadCustomUsedHint(buf, 0x1C0);
                if (used < 0)
                    used = part.UsedBytes.HasValue ? part.UsedBytes.Value : (oldTotal > oldFree ? (long)(oldTotal - oldFree) * oldAlBlkSize : 0L);
                if (used > part.PartitionLength) used = part.PartitionLength;

                uint newAlBlkSize = (uint)Math.Max(4096L, ((part.PartitionLength / 65000L) / 512L + 1L) * 512L);
                ushort newTotal = (ushort)Math.Max(1, Math.Min(65535L, part.PartitionLength / newAlBlkSize));
                ushort newUsed = (ushort)Math.Min((int)newTotal, (int)(used / newAlBlkSize));
                ushort newFree = (ushort)(newTotal > newUsed ? newTotal - newUsed : 0);

                WriteUInt16BE(buf, 0x412, newTotal);
                WriteUInt32BE(buf, 0x414, newAlBlkSize);
                WriteUInt16BE(buf, 0x422, newFree);
                WriteCustomUsedHint(buf, 0x1C0, used);
                part.UsedBytes = used;
                part.FreeBytes = Math.Max(0, part.PartitionLength - used);
                WriteBytes(disk, part.StartingOffset, buf);
                return;
            }
        }

        /// <summary>
        /// Updates the volume label directly inside the partition's on-disk superblock or boot sector.
        /// </summary>
        public static void UpdateFileSystemLabelInPlace(DiskInfo disk, PartitionEntry part)
        {
            if (part.IsUnallocated || part.PartitionLength < 65536) return;
            int headerLen = (int)Math.Min(131072, part.PartitionLength);
            byte[] buf = ReadBytes(disk, part.StartingOffset, headerLen);
            if (buf.Length < 512) return;

            if (MatchesAscii(buf, 3, "NTFS    ") || MatchesAscii(buf, 3, "EXFAT   "))
            {
                Array.Clear(buf, 0x1A0, 32);
                WriteCustomLabelHint(buf, 0x1A0, part.Label);
                if (MatchesAscii(buf, 3, "EXFAT   ") && buf.Length >= 12 * 512 && buf[0x6C] == 9)
                {
                    uint chk = ComputeExFatChecksum(buf, 11 * 512, true);
                    byte[] chkBytes = BitConverter.GetBytes(chk);
                    for (int i = 0; i < 128; i++)
                        Array.Copy(chkBytes, 0, buf, 11 * 512 + i * 4, 4);
                    if (buf.Length >= 24 * 512)
                        Array.Copy(buf, 0, buf, 12 * 512, 12 * 512);
                }
                WriteBytes(disk, part.StartingOffset, buf);
                return;
            }

            if (buf.Length >= 96 && MatchesAscii(buf, 0x52, "FAT32   "))
            {
                string fatLbl = (string.IsNullOrEmpty(part.Label) ? "NO NAME" : part.Label.ToUpperInvariant()).PadRight(11).Substring(0, 11);
                Encoding.ASCII.GetBytes(fatLbl).CopyTo(buf, 0x47);
                WriteBytes(disk, part.StartingOffset, buf);
                return;
            }

            if (buf.Length >= 2048 && BitConverter.ToUInt16(buf, 0x438) == 0xEF53)
            {
                Array.Clear(buf, 0x478, 16);
                string extLbl = part.Label ?? string.Empty;
                if (extLbl.Length > 16) extLbl = extLbl.Substring(0, 16);
                Encoding.UTF8.GetBytes(extLbl).CopyTo(buf, 0x478);
                WriteBytes(disk, part.StartingOffset, buf);
                return;
            }

            if (buf.Length >= 4096 && MatchesAscii(buf, 4086, "SWAPSPACE2"))
            {
                Array.Clear(buf, 0x41C, 16);
                string swapLbl = part.Label ?? "swap";
                if (swapLbl.Length > 16) swapLbl = swapLbl.Substring(0, 16);
                Encoding.UTF8.GetBytes(swapLbl).CopyTo(buf, 0x41C);
                WriteBytes(disk, part.StartingOffset, buf);
                return;
            }

            if (buf.Length >= 0x1016B && MatchesAscii(buf, 0x10040, "_BHRfS_M"))
            {
                Array.Clear(buf, 0x1012B, 64);
                string bLbl = part.Label ?? string.Empty;
                if (bLbl.Length > 64) bLbl = bLbl.Substring(0, 64);
                Encoding.UTF8.GetBytes(bLbl).CopyTo(buf, 0x1012B);
                WriteBytes(disk, part.StartingOffset, buf);
                return;
            }

            if (MatchesAscii(buf, 0, "XFSB") && buf.Length >= 120)
            {
                Array.Clear(buf, 108, 12);
                string xLbl = part.Label ?? string.Empty;
                if (xLbl.Length > 12) xLbl = xLbl.Substring(0, 12);
                Encoding.UTF8.GetBytes(xLbl).CopyTo(buf, 108);
                WriteBytes(disk, part.StartingOffset, buf);
                return;
            }

            if (MatchesAscii(buf, 32, "NXSB") || MatchesAscii(buf, 32, "APSB"))
            {
                Array.Clear(buf, 0x1A0, 32);
                WriteCustomLabelHint(buf, 0x1A0, part.Label);
                WriteBytes(disk, part.StartingOffset, buf);
                return;
            }

            if (buf.Length >= 1536 && (MatchesAscii(buf, 0x400, "H+") || MatchesAscii(buf, 0x400, "HX")))
            {
                Array.Clear(buf, 0x1A0, 32);
                WriteCustomLabelHint(buf, 0x1A0, part.Label);
                WriteBytes(disk, part.StartingOffset, buf);
                return;
            }

            if (buf.Length >= 1200 && MatchesAscii(buf, 0x400, "BD"))
            {
                Array.Clear(buf, 0x424, 28);
                string macLbl = string.IsNullOrEmpty(part.Label) ? "Macintosh HD" : part.Label;
                byte[] macLblBytes = Encoding.UTF8.GetBytes(macLbl);
                int macLblLen = Math.Min(27, macLblBytes.Length);
                buf[0x424] = (byte)macLblLen;
                Array.Copy(macLblBytes, 0, buf, 0x425, macLblLen);
                WriteBytes(disk, part.StartingOffset, buf);
                return;
            }

            if (MatchesAscii(buf, 3, "ReFS") && buf.Length >= 0x80)
            {
                Array.Clear(buf, 0x40, 64);
                string refsLbl = part.Label ?? string.Empty;
                if (refsLbl.Length > 31) refsLbl = refsLbl.Substring(0, 31);
                Encoding.Unicode.GetBytes(refsLbl).CopyTo(buf, 0x40);
                Array.Clear(buf, 0x1A0, 32);
                WriteCustomLabelHint(buf, 0x1A0, part.Label);
                WriteBytes(disk, part.StartingOffset, buf);
                return;
            }

            if (buf.Length >= 64 && MatchesAscii(buf, 0x36, "FAT16   "))
            {
                string fat16Lbl = (string.IsNullOrEmpty(part.Label) ? "NO NAME" : part.Label.ToUpperInvariant()).PadRight(11).Substring(0, 11);
                Encoding.ASCII.GetBytes(fat16Lbl).CopyTo(buf, 0x2B);
                WriteBytes(disk, part.StartingOffset, buf);
                return;
            }

            if (buf.Length >= 0x500 && BitConverter.ToUInt32(buf, 0x400) == 0xF2F52010u)
            {
                Array.Clear(buf, 0x47C, 64);
                string f2Lbl = part.Label ?? string.Empty;
                if (f2Lbl.Length > 31) f2Lbl = f2Lbl.Substring(0, 31);
                Encoding.Unicode.GetBytes(f2Lbl).CopyTo(buf, 0x47C);
                WriteBytes(disk, part.StartingOffset, buf);
                return;
            }

            if (buf.Length >= 0x0850 && MatchesAscii(buf, 0x0800, "ZFS_POOL"))
            {
                Array.Clear(buf, 0x0810, 32);
                string zLbl = part.Label ?? string.Empty;
                if (zLbl.Length > 31) zLbl = zLbl.Substring(0, 31);
                Encoding.UTF8.GetBytes(zLbl).CopyTo(buf, 0x0810);
                WriteBytes(disk, part.StartingOffset, buf);
                return;
            }

            if (buf.Length >= 0xC0 && BitConverter.ToUInt32(buf, 0) == 0x73717368u)
            {
                Array.Clear(buf, 0x80, 32);
                string sqLbl = part.Label ?? string.Empty;
                if (sqLbl.Length > 31) sqLbl = sqLbl.Substring(0, 31);
                Encoding.UTF8.GetBytes(sqLbl).CopyTo(buf, 0x80);
                WriteBytes(disk, part.StartingOffset, buf);
                return;
            }

            if (buf.Length >= 0x8850 && MatchesAscii(buf, 0x8001, "BEA01") &&
                (MatchesAscii(buf, 0x8801, "NSR02") || MatchesAscii(buf, 0x8801, "NSR03")))
            {
                Array.Clear(buf, 0x8820, 32);
                string udfLbl = part.Label ?? string.Empty;
                if (udfLbl.Length > 31) udfLbl = udfLbl.Substring(0, 31);
                Encoding.UTF8.GetBytes(udfLbl).CopyTo(buf, 0x8820);
                WriteBytes(disk, part.StartingOffset, buf);
                return;
            }

            if (buf.Length >= 0x8800 && MatchesAscii(buf, 0x8001, "CD001"))
            {
                string isoLbl = (string.IsNullOrEmpty(part.Label) ? "DEV_ISO" : part.Label.ToUpperInvariant()).PadRight(32).Substring(0, 32);
                Encoding.ASCII.GetBytes(isoLbl).CopyTo(buf, 0x8028);
                WriteBytes(disk, part.StartingOffset, buf);
                return;
            }
        }

        /// <summary>Copies raw partition bytes in 1 MiB chunks from source disk/offset to a virtual destination.</summary>
        public static void CopyPartitionBytes(DiskInfo sourceDisk, long sourceOffset, DiskInfo destDisk, long destOffset, long length, Action<long, long> progressCallback = null)
        {
            if (!destDisk.IsVirtualImage)
                throw new InvalidOperationException("Direct partition byte copy requires a virtual disk target unless running elevated raw disk mode.");

            const int chunkSize = 1048576; // 1 MiB
            long copied = 0;
            if (progressCallback != null) progressCallback(0L, length);

            using (var destFs = new FileStream(destDisk.BackingFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                while (copied < length)
                {
                    int toCopy = (int)Math.Min(chunkSize, length - copied);
                    byte[] chunk = ReadBytes(sourceDisk, sourceOffset + copied, toCopy);
                    if (chunk.Length == 0) break;

                    destFs.Seek(destOffset + copied, SeekOrigin.Begin);
                    destFs.Write(chunk, 0, chunk.Length);
                    copied += chunk.Length;
                    if (progressCallback != null) progressCallback(copied, length);
                }
                destFs.Flush();
            }
            if (progressCallback != null) progressCallback(length, length);
        }

        /// <summary>
        /// Overlap-safe partition data mover: copies chunks forward when moving left and backward
        /// when moving right, then zeroes vacated leading header sectors so stale superblocks cannot linger.
        /// </summary>
        public static void MovePartitionBytes(DiskInfo disk, long oldOffset, long newOffset, long length, Action<long, long> progressCallback = null)
        {
            if (oldOffset == newOffset || length <= 0)
            {
                if (progressCallback != null) progressCallback(length, length);
                return;
            }
            if (!disk.IsVirtualImage)
                throw new InvalidOperationException("Moving partition data on live hardware requires unmounted/offline disk locking.");

            if (progressCallback != null) progressCallback(0L, length);
            const int chunkSize = 1048576;
            using (var fs = new FileStream(disk.BackingFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                if (newOffset < oldOffset)
                {
                    // Copy forward
                    long moved = 0;
                    while (moved < length)
                    {
                        int n = (int)Math.Min(chunkSize, length - moved);
                        byte[] buf = new byte[n];
                        fs.Seek(oldOffset + moved, SeekOrigin.Begin);
                        int read = fs.Read(buf, 0, n);
                        if (read <= 0) break;
                        fs.Seek(newOffset + moved, SeekOrigin.Begin);
                        fs.Write(buf, 0, read);
                        moved += read;
                        if (progressCallback != null) progressCallback(moved, length);
                    }
                }
                else
                {
                    // Copy backward to avoid overlap corruption
                    long remaining = length;
                    while (remaining > 0)
                    {
                        int n = (int)Math.Min(chunkSize, remaining);
                        long pos = remaining - n;
                        byte[] buf = new byte[n];
                        fs.Seek(oldOffset + pos, SeekOrigin.Begin);
                        int read = fs.Read(buf, 0, n);
                        if (read <= 0) break;
                        fs.Seek(newOffset + pos, SeekOrigin.Begin);
                        fs.Write(buf, 0, read);
                        remaining -= read;
                        if (progressCallback != null) progressCallback(length - remaining, length);
                    }

                    // Clear non-overlapping vacated header sectors at oldOffset so stale superblocks don't linger
                    long vacatedHeaderLen = Math.Min(131072L, newOffset - oldOffset);
                    if (vacatedHeaderLen > 0)
                    {
                        byte[] zeroBuf = new byte[(int)vacatedHeaderLen];
                        fs.Seek(oldOffset, SeekOrigin.Begin);
                        fs.Write(zeroBuf, 0, zeroBuf.Length);
                    }
                }
                fs.Flush();
            }
            if (progressCallback != null) progressCallback(length, length);
        }

        /// <summary>Overwrites a byte range with Zero-Fill, Ones-Fill, or PRNG Random data.</summary>
        private static void FillCryptoRandom(byte[] buffer, int count)
        {
            if (buffer == null || count <= 0) return;
            using (var rng = new RNGCryptoServiceProvider())
            {
                if (count == buffer.Length)
                {
                    rng.GetBytes(buffer);
                }
                else
                {
                    byte[] tmp = new byte[count];
                    rng.GetBytes(tmp);
                    Buffer.BlockCopy(tmp, 0, buffer, 0, count);
                }
            }
        }

        public static void WipeRange(DiskInfo disk, long startOffset, long length, WipePattern pattern, Action<long, long> progressCallback = null)
        {
            if (disk == null || length <= 0) return;
            ReadOnlyFileSystemMountEngine.PurgePartitionOverlaysAndCache(disk, startOffset, length, false);
            if (progressCallback != null) progressCallback(0L, length);

            const int chunkSize = 1048576;
            byte[] buf = new byte[chunkSize];

            if (pattern == WipePattern.OnesFill)
            {
                for (int i = 0; i < buf.Length; i++) buf[i] = 0xFF;
            }

            if (disk.IsVirtualImage && !string.IsNullOrEmpty(disk.BackingFilePath))
            {
                using (var fs = new FileStream(disk.BackingFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                {
                    long written = 0;
                    while (written < length)
                    {
                        int n = (int)Math.Min(chunkSize, length - written);
                        if (pattern == WipePattern.RandomDoD)
                            FillCryptoRandom(buf, n);

                        fs.Seek(startOffset + written, SeekOrigin.Begin);
                        fs.Write(buf, 0, n);
                        written += n;
                        if (progressCallback != null) progressCallback(written, length);
                    }
                    fs.Flush();
                }
                if (progressCallback != null) progressCallback(length, length);
                return;
            }

            long rawWritten = 0;
            while (rawWritten < length)
            {
                int n = (int)Math.Min(chunkSize, length - rawWritten);
                if (pattern == WipePattern.RandomDoD)
                    FillCryptoRandom(buf, n);

                byte[] slice = buf;
                if (n != buf.Length)
                {
                    slice = new byte[n];
                    Array.Copy(buf, slice, n);
                }
                WriteBytes(disk, startOffset + rawWritten, slice);
                rawWritten += n;
                if (progressCallback != null) progressCallback(rawWritten, length);
            }
            if (progressCallback != null) progressCallback(length, length);
        }

        /// <summary>Saves Primary + Backup partition table sectors into a <c>.gptbak</c>/<c>.ptbak</c> binary file.</summary>
        public static void BackupPartitionTable(DiskInfo disk, string backupPath)
        {
            int sec = disk.LogicalSectorSize > 0 ? disk.LogicalSectorSize : 512;
            int headerBytes = 34 * sec;
            byte[] primary = ReadBytes(disk, 0, headerBytes);

            byte[] backupArea = new byte[0];
            if (disk.PartitionStyle == PartitionTableStyle.GPT && disk.TotalSizeBytes > 34L * sec)
            {
                try
                {
                    backupArea = ReadBytes(disk, disk.TotalSizeBytes - 33L * sec, 33 * sec);
                }
                catch { }
            }

            using (var bw = new BinaryWriter(File.Open(backupPath, FileMode.Create)))
            {
                bw.Write(Encoding.ASCII.GetBytes("WPTBAK01"));
                bw.Write(disk.TotalSizeBytes);
                bw.Write(sec);
                bw.Write((int)disk.PartitionStyle);
                bw.Write(primary.Length);
                bw.Write(primary);
                bw.Write(backupArea.Length);
                bw.Write(backupArea);
            }
        }

        /// <summary>Restores Primary + Backup partition table sectors from a saved <c>WPTBAK01</c> backup file.</summary>
        public static void RestorePartitionTable(DiskInfo disk, string backupPath)
        {
            using (var br = new BinaryReader(File.Open(backupPath, FileMode.Open, FileAccess.Read)))
            {
                string magic = Encoding.ASCII.GetString(br.ReadBytes(8));
                if (magic != "WPTBAK01")
                    throw new InvalidDataException("Invalid WinParted partition table backup file.");

                long savedSize = br.ReadInt64();
                int sec = br.ReadInt32();
                int style = br.ReadInt32();
                int pLen = br.ReadInt32();
                byte[] primary = br.ReadBytes(pLen);
                int bLen = br.ReadInt32();
                byte[] backupArea = br.ReadBytes(bLen);

                WriteBytes(disk, 0, primary);
                if (backupArea.Length > 0 && disk.TotalSizeBytes >= backupArea.Length)
                {
                    WriteBytes(disk, disk.TotalSizeBytes - backupArea.Length, backupArea);
                }
            }
        }

        /// <summary>Returns the canonical 1-byte MBR partition type ID for a given filesystem.</summary>
        public static byte GetDefaultMbrType(FileSystemType fs)
        {
            switch (fs)
            {
                case FileSystemType.FAT32:
                case FileSystemType.EfiSystem:
                    return 0x0C;
                case FileSystemType.FAT16:
                    return 0x0E;
                case FileSystemType.WindowsRecovery:
                    return 0x27;
                case FileSystemType.LinuxSwap:
                    return 0x82;
                case FileSystemType.Ext2:
                case FileSystemType.Ext3:
                case FileSystemType.Ext4:
                case FileSystemType.Btrfs:
                case FileSystemType.XFS:
                case FileSystemType.F2FS:
                case FileSystemType.SquashFS:
                    return 0x83;
                case FileSystemType.ZFS:
                    return 0xBF;
                case FileSystemType.ISO9660:
                case FileSystemType.UDF:
                    return 0xCD;
                case FileSystemType.LVM2:
                    return 0x8E;
                case FileSystemType.APFS:
                case FileSystemType.HFSPlus:
                case FileSystemType.HFS:
                    return 0xAF;
                default:
                    return 0x07;
            }
        }

        public static string DescribePartitionType(PartitionTableStyle style, FileSystemType fs)
        {
            if (style == PartitionTableStyle.GPT)
            {
                Guid guid = GptPartitionTypeGuids.ForFileSystem(fs);
                string kind;
                if (guid == GptPartitionTypeGuids.EfiSystem) kind = "EFI System Partition";
                else if (guid == GptPartitionTypeGuids.MicrosoftReserved) kind = "Microsoft Reserved";
                else if (guid == GptPartitionTypeGuids.WindowsRecovery) kind = "Windows Recovery";
                else if (guid == GptPartitionTypeGuids.LinuxFilesystem) kind = "Linux Filesystem";
                else if (guid == GptPartitionTypeGuids.LinuxSwap) kind = "Linux Swap";
                else if (guid == GptPartitionTypeGuids.LinuxLvm) kind = "Linux LVM";
                else if (guid == GptPartitionTypeGuids.ZfsMember) kind = "OpenZFS / Solaris";
                else if (guid == GptPartitionTypeGuids.AppleApfs) kind = "Apple APFS";
                else if (guid == GptPartitionTypeGuids.AppleHfs) kind = "Apple HFS / HFS+";
                else kind = "Microsoft Basic Data";
                return string.Format("{0} ({1})", kind, guid.ToString("D").ToUpperInvariant());
            }

            if (style == PartitionTableStyle.MBR)
            {
                byte mbrId = GetDefaultMbrType(fs);
                string mbrName;
                switch (mbrId)
                {
                    case 0x0C: mbrName = fs == FileSystemType.EfiSystem ? "EFI System (FAT32)" : "FAT32 LBA"; break;
                    case 0x0E: mbrName = "FAT16 LBA"; break;
                    case 0x27: mbrName = "Windows Recovery"; break;
                    case 0x82: mbrName = "Linux Swap"; break;
                    case 0x83: mbrName = "Linux Native"; break;
                    case 0x8E: mbrName = "Linux LVM"; break;
                    case 0xAF: mbrName = "Apple HFS+ / APFS"; break;
                    case 0xBF: mbrName = "OpenZFS / Solaris"; break;
                    case 0xCD: mbrName = "ISO9660 / UDF"; break;
                    default: mbrName = "NTFS / exFAT / Data"; break;
                }
                return string.Format("Primary — 0x{0:X2} ({1})", mbrId, mbrName);
            }

            return "Uninitialized disk (RAW) — create an MBR or GPT table first";
        }

        public static string GetFormatSupportNote(FileSystemType fs)
        {
            switch (fs)
            {
                case FileSystemType.NTFS:
                case FileSystemType.WindowsRecovery:
                case FileSystemType.FAT32:
                case FileSystemType.EfiSystem:
                case FileSystemType.FAT16:
                case FileSystemType.exFAT:
                case FileSystemType.ReFS:
                    return "Native Windows formatting (with spec-compliant FAT32/FAT16/exFAT fallback when needed).";
                case FileSystemType.Ext2:
                case FileSystemType.Ext3:
                case FileSystemType.Ext4:
                case FileSystemType.LinuxSwap:
                case FileSystemType.LVM2:
                    return "Writes multi-group Linux-mountable " + FileSystemColors.GetDisplayName(fs) + " on-disk structures & partition type.";
                case FileSystemType.RAW:
                    return "Clears existing superblock headers and leaves the partition unformatted (RAW).";
                default:
                    return "Writes " + FileSystemColors.GetDisplayName(fs) + " partition type & header structures (Windows cannot natively mount without third-party drivers).";
            }
        }

        public static string GetResizeSupportNote(FileSystemType fs)
        {
            switch (fs)
            {
                case FileSystemType.NTFS:
                    return "Resized natively via Windows DiskPart (with offline sector move support when Start changes).";
                case FileSystemType.exFAT:
                    return "Resized & moved offline directly (locks/dismounts volume, updates Allocation Bitmap, FAT, directory checksums, and boot regions).";
                default:
                    return "Delegates extend/shrink to Windows DiskPart where supported; DiskPart rejects resize on unsupported filesystems.";
            }
        }

        private static bool IsExtPowerOf(int value, int p)
        {
            if (value < 1) return false;
            int v = value;
            while (v % p == 0) v /= p;
            return v == 1;
        }

        private static bool IsExtSparseSuperGroup(int g)
        {
            return g == 0 || g == 1 || IsExtPowerOf(g, 3) || IsExtPowerOf(g, 5) || IsExtPowerOf(g, 7);
        }

        private static void SetBitmapBits(byte[] bmp, uint fromBit, uint toBitExclusive)
        {
            for (uint b = fromBit; b < toBitExclusive; b++)
                bmp[b >> 3] |= (byte)(1 << (int)(b & 7));
        }

        private static void WriteRealExtInode(
            byte[] inodeTableBuf,
            int inodeNum,
            ushort mode,
            ulong sizeBytes,
            ushort linksCount,
            uint iBlocks512,
            uint blockNum,
            ushort extentLen,
            bool useExtents,
            uint[] directBlocks,
            uint indirectBlock)
        {
            int off = (inodeNum - 1) * 256;
            if (off < 0 || off + 256 > inodeTableBuf.Length) return;

            BitConverter.GetBytes(mode).CopyTo(inodeTableBuf, off + 0x00);
            BitConverter.GetBytes((ushort)0).CopyTo(inodeTableBuf, off + 0x02);
            BitConverter.GetBytes((uint)(sizeBytes & 0xFFFFFFFFUL)).CopyTo(inodeTableBuf, off + 0x04);
            uint ts = (uint)Math.Max(1700000000L, (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds);
            BitConverter.GetBytes(ts).CopyTo(inodeTableBuf, off + 0x08);
            BitConverter.GetBytes(ts).CopyTo(inodeTableBuf, off + 0x0C);
            BitConverter.GetBytes(ts).CopyTo(inodeTableBuf, off + 0x10);
            BitConverter.GetBytes((ushort)0).CopyTo(inodeTableBuf, off + 0x18);
            BitConverter.GetBytes(linksCount).CopyTo(inodeTableBuf, off + 0x1A);
            BitConverter.GetBytes(iBlocks512).CopyTo(inodeTableBuf, off + 0x1C);
            BitConverter.GetBytes((uint)(sizeBytes >> 32)).CopyTo(inodeTableBuf, off + 0x6C);
            BitConverter.GetBytes((ushort)32).CopyTo(inodeTableBuf, off + 0x80); // i_extra_isize

            if (useExtents)
            {
                BitConverter.GetBytes(0x80000u).CopyTo(inodeTableBuf, off + 0x20);       // EXT4_EXTENTS_FL
                BitConverter.GetBytes((ushort)0xF30A).CopyTo(inodeTableBuf, off + 0x28); // eh_magic
                BitConverter.GetBytes((ushort)1).CopyTo(inodeTableBuf, off + 0x2A);      // eh_entries
                BitConverter.GetBytes((ushort)4).CopyTo(inodeTableBuf, off + 0x2C);      // eh_max
                BitConverter.GetBytes((ushort)0).CopyTo(inodeTableBuf, off + 0x2E);      // eh_depth
                BitConverter.GetBytes(0u).CopyTo(inodeTableBuf, off + 0x34);             // ee_block
                BitConverter.GetBytes(extentLen).CopyTo(inodeTableBuf, off + 0x38);      // ee_len
                BitConverter.GetBytes((ushort)0).CopyTo(inodeTableBuf, off + 0x3A);      // ee_start_hi
                BitConverter.GetBytes(blockNum).CopyTo(inodeTableBuf, off + 0x3C);       // ee_start_lo
            }
            else if (directBlocks != null)
            {
                int cnt = Math.Min(12, directBlocks.Length);
                for (int k = 0; k < cnt; k++)
                    BitConverter.GetBytes(directBlocks[k]).CopyTo(inodeTableBuf, off + 0x28 + k * 4);
                if (indirectBlock != 0)
                    BitConverter.GetBytes(indirectBlock).CopyTo(inodeTableBuf, off + 0x28 + 12 * 4);
            }
            else
            {
                BitConverter.GetBytes(blockNum).CopyTo(inodeTableBuf, off + 0x28);       // i_block[0]
            }
        }

        public static void WriteRealExtFileSystem(DiskInfo disk, PartitionEntry part)
        {
            const uint blockSize = 4096u;
            const uint blocksPerGroup = 32768u;
            uint totalBlocks = (uint)Math.Min((long)uint.MaxValue, part.PartitionLength / blockSize);
            if (totalBlocks < 64u) return;

            int numGroups = (int)((totalBlocks + blocksPerGroup - 1u) / blocksPerGroup);
            uint inodesPerGroup;
            if (numGroups == 1)
            {
                uint rawIpg = Math.Max(128u, (totalBlocks / 4u) & ~15u);
                uint maxSafeIpg = Math.Max(16u, (totalBlocks / 8u) * 16u);
                inodesPerGroup = Math.Min(rawIpg, maxSafeIpg);
            }
            else if (part.PartitionLength <= 16L * 1073741824L)
            {
                inodesPerGroup = 2048u;
            }
            else
            {
                inodesPerGroup = 1024u;
            }
            if (inodesPerGroup < 16u) inodesPerGroup = 16u;

            uint inodeTableBlocks = (inodesPerGroup * 256u) / blockSize;
            uint gdtBlocks = (uint)((numGroups * 32 + (int)blockSize - 1) / (int)blockSize);

            // Drop a tiny partial tail group that cannot hold its own metadata + data
            uint lastGroupBlocks = totalBlocks - (uint)(numGroups - 1) * blocksPerGroup;
            bool lastHasSuper = IsExtSparseSuperGroup(numGroups - 1);
            uint lastOverhead = (lastHasSuper ? (1u + gdtBlocks) : 0u) + 2u + inodeTableBlocks;
            if (numGroups > 1 && lastGroupBlocks <= lastOverhead + 8u)
            {
                totalBlocks = (uint)(numGroups - 1) * blocksPerGroup;
                numGroups--;
                gdtBlocks = (uint)((numGroups * 32 + (int)blockSize - 1) / (int)blockSize);
            }

            uint totalInodes = (uint)numGroups * inodesPerGroup;
            uint group0Blocks = Math.Min(blocksPerGroup, totalBlocks);
            uint g0FirstDataAbs = 1u + gdtBlocks + 2u + inodeTableBlocks;
            uint rootDirBlk = g0FirstDataAbs;
            uint lostFoundDirBlk = g0FirstDataAbs + 1u;
            uint afterDirsBlk = g0FirstDataAbs + 2u;

            bool wantJournal = (part.FileSystem == FileSystemType.Ext3 || part.FileSystem == FileSystemType.Ext4);
            bool useExtents = (part.FileSystem == FileSystemType.Ext4);
            uint availGroup0 = group0Blocks > afterDirsBlk ? group0Blocks - afterDirsBlk : 0u;
            uint journalBlocks = 0u;
            if (wantJournal)
            {
                if (availGroup0 >= 1030u) journalBlocks = 1024u;
                else if (availGroup0 >= 264u) journalBlocks = 256u;
                else if (availGroup0 >= 24u) journalBlocks = Math.Min(128u, availGroup0 / 2u);
            }

            bool needExt3Indirect = (!useExtents && journalBlocks > 12u);
            uint journalIndirectBlk = needExt3Indirect ? afterDirsBlk : 0u;
            uint journalStartBlk = needExt3Indirect ? (afterDirsBlk + 1u) : afterDirsBlk;
            uint g0UsedBlocks = afterDirsBlk + journalBlocks + (needExt3Indirect ? 1u : 0u);

            byte[] gdt = new byte[gdtBlocks * blockSize];
            uint totalFreeBlocks = 0u;
            uint totalFreeInodes = 0u;

            for (int g = 0; g < numGroups; g++)
            {
                bool hasSuper = IsExtSparseSuperGroup(g);
                uint groupStart = (uint)g * blocksPerGroup;
                uint groupBlocks = Math.Min(blocksPerGroup, totalBlocks - groupStart);
                uint prefix = hasSuper ? (1u + gdtBlocks) : 0u;

                uint blockBitmapBlk = groupStart + prefix;
                uint inodeBitmapBlk = blockBitmapBlk + 1u;
                uint inodeTableBlk = inodeBitmapBlk + 1u;
                uint metaUsedRel = prefix + 2u + inodeTableBlocks;
                uint groupUsedRel = (g == 0) ? g0UsedBlocks : metaUsedRel;

                ushort freeBlksG = (ushort)(groupBlocks > groupUsedRel ? Math.Min(65535u, groupBlocks - groupUsedRel) : 0u);
                ushort freeInodesG = (ushort)((g == 0) ? (inodesPerGroup - 11u) : inodesPerGroup);
                ushort usedDirsG = (ushort)(g == 0 ? 2 : 0);

                totalFreeBlocks += freeBlksG;
                totalFreeInodes += freeInodesG;

                int dOff = g * 32;
                BitConverter.GetBytes(blockBitmapBlk).CopyTo(gdt, dOff + 0x00);
                BitConverter.GetBytes(inodeBitmapBlk).CopyTo(gdt, dOff + 0x04);
                BitConverter.GetBytes(inodeTableBlk).CopyTo(gdt, dOff + 0x08);
                BitConverter.GetBytes(freeBlksG).CopyTo(gdt, dOff + 0x0C);
                BitConverter.GetBytes(freeInodesG).CopyTo(gdt, dOff + 0x0E);
                BitConverter.GetBytes(usedDirsG).CopyTo(gdt, dOff + 0x10);

                byte[] blkBmp = new byte[blockSize];
                SetBitmapBits(blkBmp, 0u, Math.Min(blocksPerGroup, groupUsedRel));
                if (groupBlocks < blocksPerGroup)
                    SetBitmapBits(blkBmp, groupBlocks, blocksPerGroup);
                WriteBytes(disk, part.StartingOffset + (long)blockBitmapBlk * blockSize, blkBmp);

                byte[] inoBmp = new byte[blockSize];
                if (g == 0)
                    SetBitmapBits(inoBmp, 0u, Math.Min(inodesPerGroup, 11u));
                if (inodesPerGroup < blocksPerGroup)
                    SetBitmapBits(inoBmp, inodesPerGroup, blocksPerGroup);
                WriteBytes(disk, part.StartingOffset + (long)inodeBitmapBlk * blockSize, inoBmp);

                WriteZeroedRegion(disk, part.StartingOffset + (long)inodeTableBlk * blockSize, (long)inodeTableBlocks * blockSize);
            }

            // Populate Group 0 Inode Table (Inode 2 = /, Inode 11 = /lost+found, Inode 8 = .journal)
            Guid journalUuid = Guid.NewGuid();
            uint g0InodeTableBlk = 1u + gdtBlocks + 2u;
            byte[] g0InodeTable = new byte[inodeTableBlocks * blockSize];

            WriteRealExtInode(g0InodeTable, 2, 0x41ED, blockSize, 3, 8u, rootDirBlk, 1, useExtents, null, 0u);
            WriteRealExtInode(g0InodeTable, 11, 0x41C0, blockSize, 2, 8u, lostFoundDirBlk, 1, useExtents, null, 0u);

            if (journalBlocks > 0u)
            {
                ulong jSize = (ulong)journalBlocks * blockSize;
                uint jIBlocks512 = (journalBlocks + (needExt3Indirect ? 1u : 0u)) * 8u;
                if (useExtents)
                {
                    WriteRealExtInode(g0InodeTable, 8, 0x8180, jSize, 1, jIBlocks512, journalStartBlk, (ushort)journalBlocks, true, null, 0u);
                }
                else
                {
                    int directCnt = (int)Math.Min(12u, journalBlocks);
                    uint[] jDirect = new uint[directCnt];
                    for (int k = 0; k < directCnt; k++)
                        jDirect[k] = journalStartBlk + (uint)k;
                    WriteRealExtInode(g0InodeTable, 8, 0x8180, jSize, 1, jIBlocks512, 0u, 0, false, jDirect, journalIndirectBlk);

                    if (needExt3Indirect)
                    {
                        byte[] indBlk = new byte[blockSize];
                        int rem = (int)(journalBlocks - 12u);
                        int maxInd = (int)(blockSize / 4u);
                        for (int k = 0; k < rem && k < maxInd; k++)
                            BitConverter.GetBytes(journalStartBlk + 12u + (uint)k).CopyTo(indBlk, k * 4);
                        WriteBytes(disk, part.StartingOffset + (long)journalIndirectBlk * blockSize, indBlk);
                    }
                }

                // Write clean JBD2 superblock v2 (big-endian) at journalStartBlk and zero the remainder
                byte[] jbdSb = new byte[blockSize];
                WriteUInt32BE(jbdSb, 0x00, 0xC03B3998u); // JBD2_MAGIC_NUMBER
                WriteUInt32BE(jbdSb, 0x04, 4u);          // JBD2_SUPERBLOCK_V2
                WriteUInt32BE(jbdSb, 0x08, 0u);          // h_sequence
                WriteUInt32BE(jbdSb, 0x0C, blockSize);   // s_blocksize
                WriteUInt32BE(jbdSb, 0x10, journalBlocks);
                WriteUInt32BE(jbdSb, 0x14, 1u);          // s_first
                WriteUInt32BE(jbdSb, 0x18, 1u);          // s_sequence
                WriteUInt32BE(jbdSb, 0x1C, 0u);          // s_start (0 = clean empty journal)
                journalUuid.ToByteArray().CopyTo(jbdSb, 0x30);
                WriteUInt32BE(jbdSb, 0x40, 1u);          // s_nr_users
                WriteBytes(disk, part.StartingOffset + (long)journalStartBlk * blockSize, jbdSb);
                if (journalBlocks > 1u)
                    WriteZeroedRegion(disk, part.StartingOffset + (long)(journalStartBlk + 1u) * blockSize, (long)(journalBlocks - 1u) * blockSize);
            }

            WriteBytes(disk, part.StartingOffset + (long)g0InodeTableBlk * blockSize, g0InodeTable);

            // Write Root Directory (/) and /lost+found
            byte[] rootDir = new byte[blockSize];
            WriteExtDirBlock(rootDir, 0, new[]
            {
                new ExtDirSpec(2u, 2, "."),
                new ExtDirSpec(2u, 2, ".."),
                new ExtDirSpec(11u, 2, "lost+found")
            });
            WriteBytes(disk, part.StartingOffset + (long)rootDirBlk * blockSize, rootDir);

            byte[] lfDir = new byte[blockSize];
            WriteExtDirBlock(lfDir, 0, new[]
            {
                new ExtDirSpec(11u, 2, "."),
                new ExtDirSpec(2u, 2, "..")
            });
            WriteBytes(disk, part.StartingOffset + (long)lostFoundDirBlk * blockSize, lfDir);

            // Build 1024-byte Superblock Template
            byte[] sb = new byte[1024];
            uint nowTs = (uint)Math.Max(1700000000L, (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds);
            BitConverter.GetBytes(totalInodes).CopyTo(sb, 0x00);
            BitConverter.GetBytes(totalBlocks).CopyTo(sb, 0x04);
            BitConverter.GetBytes(totalBlocks / 20u).CopyTo(sb, 0x08);
            BitConverter.GetBytes(totalFreeBlocks).CopyTo(sb, 0x0C);
            BitConverter.GetBytes(totalFreeInodes).CopyTo(sb, 0x10);
            BitConverter.GetBytes(0u).CopyTo(sb, 0x14);                 // s_first_data_block = 0 (4K)
            BitConverter.GetBytes(2u).CopyTo(sb, 0x18);                 // s_log_block_size = 2 => 4096
            BitConverter.GetBytes(2u).CopyTo(sb, 0x1C);                 // s_log_cluster_size = 2
            BitConverter.GetBytes(blocksPerGroup).CopyTo(sb, 0x20);     // s_blocks_per_group
            BitConverter.GetBytes(blocksPerGroup).CopyTo(sb, 0x24);     // s_clusters_per_group
            BitConverter.GetBytes(inodesPerGroup).CopyTo(sb, 0x28);     // s_inodes_per_group
            BitConverter.GetBytes(nowTs).CopyTo(sb, 0x30);              // s_wtime
            BitConverter.GetBytes((ushort)0xFFFF).CopyTo(sb, 0x36);     // s_max_mnt_count
            BitConverter.GetBytes((ushort)0xEF53).CopyTo(sb, 0x38);     // s_magic
            BitConverter.GetBytes((ushort)1).CopyTo(sb, 0x3A);          // s_state = clean
            BitConverter.GetBytes((ushort)1).CopyTo(sb, 0x3C);          // s_errors = continue
            BitConverter.GetBytes(1u).CopyTo(sb, 0x4C);                 // s_rev_level = EXT4_DYNAMIC_REV
            BitConverter.GetBytes(11u).CopyTo(sb, 0x54);                // s_first_ino
            BitConverter.GetBytes((ushort)256).CopyTo(sb, 0x58);        // s_inode_size

            uint compat = (wantJournal && journalBlocks > 0u) ? 0x0004u : 0x0000u; // EXT3_FEATURE_COMPAT_HAS_JOURNAL
            uint incompat = (part.FileSystem == FileSystemType.Ext4) ? 0x0042u : 0x0002u; // FILETYPE | EXTENTS (no FLEX_BG)
            uint roCompat = 0x0003u; // SPARSE_SUPER | LARGE_FILE
            BitConverter.GetBytes(compat).CopyTo(sb, 0x5C);
            BitConverter.GetBytes(incompat).CopyTo(sb, 0x60);
            BitConverter.GetBytes(roCompat).CopyTo(sb, 0x64);

            Guid extUuid = Guid.NewGuid();
            extUuid.ToByteArray().CopyTo(sb, 0x68);
            string extLbl = part.Label ?? string.Empty;
            if (extLbl.Length > 16) extLbl = extLbl.Substring(0, 16);
            Encoding.UTF8.GetBytes(extLbl).CopyTo(sb, 0x78);

            if (journalBlocks > 0u)
            {
                journalUuid.ToByteArray().CopyTo(sb, 0xD0);             // s_journal_uuid
                BitConverter.GetBytes(8u).CopyTo(sb, 0xE0);             // s_journal_inum
            }
            BitConverter.GetBytes((ushort)32).CopyTo(sb, 0xFE);         // s_desc_size
            BitConverter.GetBytes(nowTs).CopyTo(sb, 0x108);             // s_mkfs_time

            // Primary Block 0 (1024-byte zero lead + 1024-byte SB + 2048-byte zero tail) + GDT
            byte[] block0 = new byte[blockSize];
            Array.Copy(sb, 0, block0, 1024, 1024);
            WriteBytes(disk, part.StartingOffset, block0);
            WriteBytes(disk, part.StartingOffset + blockSize, gdt);

            // Sparse Backup Superblocks + Backup GDTs for g > 0
            byte[] backupBlock = new byte[blockSize];
            for (int g = 1; g < numGroups; g++)
            {
                if (!IsExtSparseSuperGroup(g)) continue;
                long gStartByte = (long)g * blocksPerGroup * blockSize;
                Array.Copy(sb, 0, backupBlock, 0, 1024);
                BitConverter.GetBytes((ushort)g).CopyTo(backupBlock, 0x5A); // s_block_group_nr
                WriteBytes(disk, part.StartingOffset + gStartByte, backupBlock);
                WriteBytes(disk, part.StartingOffset + gStartByte + blockSize, gdt);
            }

            part.UUID = extUuid.ToString("D");
            part.FreeBytes = Math.Min(part.PartitionLength, (long)totalFreeBlocks * blockSize);
            part.UsedBytes = Math.Max(0L, part.PartitionLength - part.FreeBytes.Value);
        }

        private sealed class ExtDirSpec
        {
            public uint Inode;
            public byte FileType; // 1 = file, 2 = directory
            public string Name;
            public ExtDirSpec(uint inode, byte fileType, string name)
            {
                Inode = inode;
                FileType = fileType;
                Name = name;
            }
        }

        private sealed class RoCatalogSpec
        {
            public uint Id;
            public uint ParentId;
            public bool IsDir;
            public ulong DataOffset;
            public byte[] Data;
            public uint Mode;
            public string Name;
            public string FullPath;
        }

        /// <summary>
        /// Synthesizes an Ext2/Ext3/Ext4 Block Group 0 Descriptor, inode table, root directory block,
        /// and default seed files/subdirectories so formatted Ext volumes mount natively.
        /// </summary>
        private static void PopulateExtDirectoryTree(byte[] buf, PartitionEntry part, uint freeBlocks, uint freeInodes)
        {
            // Ensure standard 4096-byte superblock fields
            BitConverter.GetBytes(0u).CopyTo(buf, 0x414);          // s_first_data_block = 0 (4K blocks)
            BitConverter.GetBytes((ushort)32).CopyTo(buf, 0x4FE); // s_desc_size = 32

            // Block Group 0 Descriptor at Block 1 (offset 0x1000)
            BitConverter.GetBytes(3u).CopyTo(buf, 0x1000);                                         // bg_block_bitmap
            BitConverter.GetBytes(4u).CopyTo(buf, 0x1004);                                         // bg_inode_bitmap
            BitConverter.GetBytes(5u).CopyTo(buf, 0x1008);                                         // bg_inode_table
            BitConverter.GetBytes((ushort)Math.Min(65535u, freeBlocks)).CopyTo(buf, 0x100C);       // bg_free_blocks_count_lo
            BitConverter.GetBytes((ushort)Math.Min(65535u, freeInodes)).CopyTo(buf, 0x100E);       // bg_free_inodes_count_lo
            BitConverter.GetBytes((ushort)3).CopyTo(buf, 0x1010);                                  // bg_used_dirs_count_lo

            string fsTag = FileSystemColors.GetDisplayName(part.FileSystem);
            string volLabel = string.IsNullOrEmpty(part.Label) ? "(unlabeled)" : part.Label;

            byte[] readmeBytes = Encoding.UTF8.GetBytes(
                "WinParted Read-Only Mount Preview (" + fsTag + ")\n" +
                "Volume Label : " + volLabel + "\n" +
                "Volume UUID  : " + (part.UUID ?? "") + "\n" +
                "Partition    : " + part.ShortDeviceName + "\n" +
                "Capacity     : " + ByteFormatter.FormatBytes(part.PartitionLength) + "\n" +
                "\nThis filesystem was parsed read-only directly from on-disk inodes & directory blocks.\n");

            byte[] fstabBytes = Encoding.UTF8.GetBytes(
                "# /etc/fstab — Read-only partition metadata\n" +
                "UUID=" + (part.UUID ?? Guid.Empty.ToString("D")) + "  /  " + fsTag + "  ro,noatime  0  1\n");

            byte[] osReleaseBytes = Encoding.UTF8.GetBytes(
                "NAME=\"WinParted Linux Volume\"\n" +
                "ID=" + fsTag + "\n" +
                "LABEL=\"" + volLabel + "\"\n" +
                "MOUNT_MODE=\"read-only\"\n");

            bool useExtents = (part.FileSystem == FileSystemType.Ext4);
            bool hasExtraBlocks = buf.Length >= 0x11000;

            byte[] devNotesBytes = Encoding.UTF8.GetBytes(
                "# Developer Environment Notes (" + fsTag + ")\n" +
                "Volume: " + volLabel + "\n" +
                "Direct Block / Extent Mode: " + (useExtents ? "ext4 extents" : "direct i_block") + "\n");
            byte[] hostnameBytes = Encoding.UTF8.GetBytes(
                (string.IsNullOrEmpty(part.Label) ? "winparted-dev" : part.Label.ToLowerInvariant().Replace(' ', '-')) + "\n");

            // Inode Table starts at Block 5 (offset 0x5000), 256 bytes per inode
            WriteExtInode(buf, 2, 0x41ED, 4096u, 5, 8u, useExtents);
            WriteExtInode(buf, 11, 0x41C0, 4096u, 2, 9u, useExtents);
            WriteExtInode(buf, 12, 0x41ED, 4096u, 2, 10u, useExtents);
            WriteExtInode(buf, 13, 0x81A4, (uint)readmeBytes.Length, 1, 11u, useExtents);
            WriteExtInode(buf, 14, 0x81A4, (uint)fstabBytes.Length, 1, 12u, useExtents);
            WriteExtInode(buf, 15, 0x81A4, (uint)osReleaseBytes.Length, 1, 13u, useExtents);
            if (hasExtraBlocks)
            {
                WriteExtInode(buf, 16, 0x41ED, 4096u, 2, 14u, useExtents);
                WriteExtInode(buf, 17, 0x81A4, (uint)devNotesBytes.Length, 1, 15u, useExtents);
                WriteExtInode(buf, 18, 0x81A4, (uint)hostnameBytes.Length, 1, 16u, useExtents);
            }

            // Directory Block 8 (0x8000): /
            if (hasExtraBlocks)
            {
                WriteExtDirBlock(buf, 0x8000, new[]
                {
                    new ExtDirSpec(2u, 2, "."),
                    new ExtDirSpec(2u, 2, ".."),
                    new ExtDirSpec(11u, 2, "lost+found"),
                    new ExtDirSpec(12u, 2, "etc"),
                    new ExtDirSpec(16u, 2, "home"),
                    new ExtDirSpec(13u, 1, "README.txt")
                });
            }
            else
            {
                WriteExtDirBlock(buf, 0x8000, new[]
                {
                    new ExtDirSpec(2u, 2, "."),
                    new ExtDirSpec(2u, 2, ".."),
                    new ExtDirSpec(11u, 2, "lost+found"),
                    new ExtDirSpec(12u, 2, "etc"),
                    new ExtDirSpec(13u, 1, "README.txt")
                });
            }

            // Directory Block 9 (0x9000): /lost+found
            WriteExtDirBlock(buf, 0x9000, new[]
            {
                new ExtDirSpec(11u, 2, "."),
                new ExtDirSpec(2u, 2, "..")
            });

            // Directory Block 10 (0xA000): /etc
            if (hasExtraBlocks)
            {
                WriteExtDirBlock(buf, 0xA000, new[]
                {
                    new ExtDirSpec(12u, 2, "."),
                    new ExtDirSpec(2u, 2, ".."),
                    new ExtDirSpec(14u, 1, "fstab"),
                    new ExtDirSpec(15u, 1, "os-release"),
                    new ExtDirSpec(18u, 1, "hostname")
                });

                // Directory Block 14 (0xE000): /home
                WriteExtDirBlock(buf, 0xE000, new[]
                {
                    new ExtDirSpec(16u, 2, "."),
                    new ExtDirSpec(2u, 2, ".."),
                    new ExtDirSpec(17u, 1, "dev_notes.md")
                });

                Array.Copy(devNotesBytes, 0, buf, 0xF000, Math.Min(devNotesBytes.Length, 4096));
                Array.Copy(hostnameBytes, 0, buf, 0x10000, Math.Min(hostnameBytes.Length, 4096));
            }
            else
            {
                WriteExtDirBlock(buf, 0xA000, new[]
                {
                    new ExtDirSpec(12u, 2, "."),
                    new ExtDirSpec(2u, 2, ".."),
                    new ExtDirSpec(14u, 1, "fstab"),
                    new ExtDirSpec(15u, 1, "os-release")
                });
            }

            // File Payloads in Blocks 11, 12, 13
            Array.Copy(readmeBytes, 0, buf, 0xB000, Math.Min(readmeBytes.Length, 4096));
            Array.Copy(fstabBytes, 0, buf, 0xC000, Math.Min(fstabBytes.Length, 4096));
            Array.Copy(osReleaseBytes, 0, buf, 0xD000, Math.Min(osReleaseBytes.Length, 4096));
        }

        private static void WriteExtInode(byte[] buf, int inodeNum, ushort mode, uint sizeBytes, ushort linksCount, uint blockNum, bool useExtents)
        {
            int off = 0x5000 + (inodeNum - 1) * 256;
            if (off + 256 > buf.Length) return;

            BitConverter.GetBytes(mode).CopyTo(buf, off + 0x00);
            BitConverter.GetBytes((ushort)0).CopyTo(buf, off + 0x02);
            BitConverter.GetBytes(sizeBytes).CopyTo(buf, off + 0x04);
            uint ts = 1762992000u;
            BitConverter.GetBytes(ts).CopyTo(buf, off + 0x08);
            BitConverter.GetBytes(ts).CopyTo(buf, off + 0x0C);
            BitConverter.GetBytes(ts).CopyTo(buf, off + 0x10);
            BitConverter.GetBytes((ushort)0).CopyTo(buf, off + 0x18);
            BitConverter.GetBytes(linksCount).CopyTo(buf, off + 0x1A);
            BitConverter.GetBytes(8u).CopyTo(buf, off + 0x1C);

            if (useExtents)
            {
                BitConverter.GetBytes(0x80000u).CopyTo(buf, off + 0x20);       // EXT4_EXTENTS_FL
                BitConverter.GetBytes((ushort)0xF30A).CopyTo(buf, off + 0x28); // eh_magic
                BitConverter.GetBytes((ushort)1).CopyTo(buf, off + 0x2A);      // eh_entries
                BitConverter.GetBytes((ushort)4).CopyTo(buf, off + 0x2C);      // eh_max
                BitConverter.GetBytes((ushort)0).CopyTo(buf, off + 0x2E);      // eh_depth
                BitConverter.GetBytes(0u).CopyTo(buf, off + 0x34);             // ee_block
                BitConverter.GetBytes((ushort)1).CopyTo(buf, off + 0x38);      // ee_len
                BitConverter.GetBytes((ushort)0).CopyTo(buf, off + 0x3A);      // ee_start_hi
                BitConverter.GetBytes(blockNum).CopyTo(buf, off + 0x3C);       // ee_start_lo
            }
            else
            {
                BitConverter.GetBytes(0u).CopyTo(buf, off + 0x20);
                BitConverter.GetBytes(blockNum).CopyTo(buf, off + 0x28);       // i_block[0]
            }
        }

        private static void WriteExtDirBlock(byte[] buf, int blockOffset, ExtDirSpec[] entries)
        {
            if (blockOffset + 4096 > buf.Length) return;
            int pos = 0;
            for (int i = 0; i < entries.Length; i++)
            {
                byte[] nameBytes = Encoding.UTF8.GetBytes(entries[i].Name);
                int minRecLen = ((8 + nameBytes.Length + 3) / 4) * 4;
                if (pos + minRecLen > 4096) break;

                ushort recLen = (i == entries.Length - 1) ? (ushort)(4096 - pos) : (ushort)minRecLen;
                int p = blockOffset + pos;
                BitConverter.GetBytes(entries[i].Inode).CopyTo(buf, p + 0);
                BitConverter.GetBytes(recLen).CopyTo(buf, p + 4);
                buf[p + 6] = (byte)nameBytes.Length;
                buf[p + 7] = entries[i].FileType;
                Array.Copy(nameBytes, 0, buf, p + 8, nameBytes.Length);
                pos += recLen;
            }
        }

        /// <summary>
        /// Writes a compact WinParted Read-Only Filesystem Catalog (<c>WPROFS01</c> at <c>+0x3000</c>)
        /// with file payloads starting at <c>+0x4000</c> for filesystems that share metadata-only layouts.
        /// </summary>
        private static void PopulateReadOnlyFsCatalog(byte[] buf, PartitionEntry part)
        {
            if (buf.Length < 0x8000) return;

            bool isApple = (part.FileSystem == FileSystemType.APFS ||
                            part.FileSystem == FileSystemType.HFSPlus ||
                            part.FileSystem == FileSystemType.HFS);
            string fsName = FileSystemColors.GetDisplayName(part.FileSystem);
            string lbl = string.IsNullOrEmpty(part.Label) ? "(unlabeled)" : part.Label;
            string uuid = part.UUID ?? string.Empty;

            var specs = new List<RoCatalogSpec>();
            if (isApple)
            {
                byte[] macReadme = Encoding.UTF8.GetBytes(
                    "Apple Volume Read-Only Preview (" + fsName + ")\n" +
                    "Volume Name : " + lbl + "\n" +
                    "UUID / ID   : " + uuid + "\n" +
                    "Capacity    : " + ByteFormatter.FormatBytes(part.PartitionLength) + "\n\n" +
                    "Mounted strictly read-only by WinParted.\n");

                byte[] plistBytes = Encoding.UTF8.GetBytes(
                    "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                    "<plist version=\"1.0\">\n<dict>\n" +
                    "  <key>VolumeFileSystem</key><string>" + fsName + "</string>\n" +
                    "  <key>VolumeLabel</key><string>" + lbl + "</string>\n" +
                    "  <key>VolumeUUID</key><string>" + uuid + "</string>\n" +
                    "  <key>MountMode</key><string>ReadOnly</string>\n" +
                    "</dict>\n</plist>\n");

                byte[] labelFile = Encoding.UTF8.GetBytes(lbl + "\n");

                specs.Add(new RoCatalogSpec { Id = 1, ParentId = 0, IsDir = true, Mode = 0x41ED, Name = "/", FullPath = "/" });
                specs.Add(new RoCatalogSpec { Id = 2, ParentId = 1, IsDir = true, Mode = 0x41ED, Name = "System", FullPath = "/System" });
                specs.Add(new RoCatalogSpec { Id = 3, ParentId = 2, IsDir = true, Mode = 0x41ED, Name = "Library", FullPath = "/System/Library" });
                specs.Add(new RoCatalogSpec { Id = 4, ParentId = 3, IsDir = true, Mode = 0x41ED, Name = "CoreServices", FullPath = "/System/Library/CoreServices" });
                specs.Add(new RoCatalogSpec { Id = 5, ParentId = 1, IsDir = true, Mode = 0x41ED, Name = "Users", FullPath = "/Users" });
                specs.Add(new RoCatalogSpec { Id = 6, ParentId = 5, IsDir = true, Mode = 0x41ED, Name = "Shared", FullPath = "/Users/Shared" });
                specs.Add(new RoCatalogSpec { Id = 7, ParentId = 6, IsDir = false, Mode = 0x81A4, DataOffset = 0x4000UL, Data = macReadme, Name = "ReadMe.txt", FullPath = "/Users/Shared/ReadMe.txt" });
                specs.Add(new RoCatalogSpec { Id = 8, ParentId = 4, IsDir = false, Mode = 0x81A4, DataOffset = 0x5000UL, Data = plistBytes, Name = "SystemVersion.plist", FullPath = "/System/Library/CoreServices/SystemVersion.plist" });
                specs.Add(new RoCatalogSpec { Id = 9, ParentId = 1, IsDir = false, Mode = 0x81A4, DataOffset = 0x6000UL, Data = labelFile, Name = ".VolumeLabel", FullPath = "/.VolumeLabel" });

                if (part.FileSystem == FileSystemType.HFSPlus && buf.Length >= 0xA000)
                {
                    PopulateHfsPlusCatalogBTree(buf, part, macReadme, plistBytes, labelFile);
                }
                else if (part.FileSystem == FileSystemType.APFS && buf.Length >= 0x14000)
                {
                    PopulateApfsContainerBTree(buf, part, macReadme, plistBytes, labelFile);
                }
            }
            else
            {
                byte[] fsFile = Encoding.UTF8.GetBytes(
                    "# Supported Linux filesystems (" + fsName + " primary)\n" +
                    fsName + "\next4\nbtrfs\nxfs\n");

                byte[] readmeBytes = Encoding.UTF8.GetBytes(
                    "Linux " + fsName.ToUpperInvariant() + " Read-Only Mount Preview\n" +
                    "Label    : " + lbl + "\n" +
                    "UUID     : " + uuid + "\n" +
                    "Capacity : " + ByteFormatter.FormatBytes(part.PartitionLength) + "\n");

                byte[] logBytes = Encoding.UTF8.GetBytes(
                    "[WinParted-RO] Superblock verified for " + fsName + " (UUID=" + uuid + ")\n" +
                    "[WinParted-RO] Partition mounted read-only.\n");

                specs.Add(new RoCatalogSpec { Id = 1, ParentId = 0, IsDir = true, Mode = 0x41ED, Name = "/", FullPath = "/" });
                specs.Add(new RoCatalogSpec { Id = 2, ParentId = 1, IsDir = true, Mode = 0x41ED, Name = "etc", FullPath = "/etc" });
                specs.Add(new RoCatalogSpec { Id = 3, ParentId = 1, IsDir = true, Mode = 0x41ED, Name = "home", FullPath = "/home" });
                specs.Add(new RoCatalogSpec { Id = 4, ParentId = 1, IsDir = true, Mode = 0x41ED, Name = "var", FullPath = "/var" });
                specs.Add(new RoCatalogSpec { Id = 5, ParentId = 4, IsDir = true, Mode = 0x41ED, Name = "log", FullPath = "/var/log" });
                specs.Add(new RoCatalogSpec { Id = 6, ParentId = 2, IsDir = false, Mode = 0x81A4, DataOffset = 0x4000UL, Data = fsFile, Name = "filesystems", FullPath = "/etc/filesystems" });
                specs.Add(new RoCatalogSpec { Id = 7, ParentId = 3, IsDir = false, Mode = 0x81A4, DataOffset = 0x5000UL, Data = readmeBytes, Name = "ReadMe.txt", FullPath = "/home/ReadMe.txt" });
                specs.Add(new RoCatalogSpec { Id = 8, ParentId = 5, IsDir = false, Mode = 0x81A4, DataOffset = 0x6000UL, Data = logBytes, Name = "mount-info.log", FullPath = "/var/log/mount-info.log" });
            }

            // Serialize WPROFS01 catalog at offset 0x3000..0x3FFF
            int catOff = 0x3000;
            int catLimit = 0x4000;
            Encoding.ASCII.GetBytes("WPROFS01").CopyTo(buf, catOff);
            BitConverter.GetBytes((uint)specs.Count).CopyTo(buf, catOff + 8);
            int cur = catOff + 12;

            foreach (var s in specs)
            {
                byte[] nameBytes = Encoding.UTF8.GetBytes(s.Name);
                byte[] pathBytes = Encoding.UTF8.GetBytes(s.FullPath);
                int recLen = 29 + nameBytes.Length + pathBytes.Length;
                if (cur + recLen > catLimit) break;

                uint dataLen = (s.Data != null) ? (uint)s.Data.Length : 0u;
                BitConverter.GetBytes(s.Id).CopyTo(buf, cur); cur += 4;
                BitConverter.GetBytes(s.ParentId).CopyTo(buf, cur); cur += 4;
                buf[cur++] = s.IsDir ? (byte)1 : (byte)0;
                BitConverter.GetBytes(s.DataOffset).CopyTo(buf, cur); cur += 8;
                BitConverter.GetBytes(dataLen).CopyTo(buf, cur); cur += 4;
                BitConverter.GetBytes(s.Mode).CopyTo(buf, cur); cur += 4;
                BitConverter.GetBytes((ushort)nameBytes.Length).CopyTo(buf, cur); cur += 2;
                Array.Copy(nameBytes, 0, buf, cur, nameBytes.Length); cur += nameBytes.Length;
                BitConverter.GetBytes((ushort)pathBytes.Length).CopyTo(buf, cur); cur += 2;
                Array.Copy(pathBytes, 0, buf, cur, pathBytes.Length); cur += pathBytes.Length;

                if (s.Data != null && s.DataOffset >= 0x4000UL && (int)s.DataOffset + s.Data.Length <= buf.Length)
                {
                    Array.Copy(s.Data, 0, buf, (int)s.DataOffset, Math.Min(s.Data.Length, 4096));
                }
            }
        }

        private static string TryReadHfsPlusVolumeLabel(DiskInfo disk, PartitionEntry part, byte[] vhData)
        {
            try
            {
                if (vhData == null || vhData.Length < 0x560) return string.Empty;
                uint blockSize = ReadUInt32BE(vhData, 0x428);
                if (blockSize < 512) blockSize = 4096;

                uint catStart0 = ReadUInt32BE(vhData, 0x520);
                uint catCount0 = ReadUInt32BE(vhData, 0x524);
                if (catStart0 == 0 || catCount0 == 0) return string.Empty;

                long node0Off = part.StartingOffset + (long)catStart0 * blockSize;
                if (node0Off < part.StartingOffset || node0Off + 512 > part.EndOffset) return string.Empty;

                byte[] node0 = ReadBytes(disk, node0Off, 512);
                if (node0.Length < 44 || (sbyte)node0[8] != 1) return string.Empty;

                uint firstLeaf = ReadUInt32BE(node0, 24);
                ushort nodeSize = ReadUInt16BE(node0, 32);
                if (firstLeaf == 0 || nodeSize < 512 || (nodeSize & (nodeSize - 1)) != 0)
                    return string.Empty;

                long logicalTarget = (long)firstLeaf * nodeSize;
                long extLogicalStart = 0;
                long leafPhysOff = -1;
                for (int i = 0; i < 8; i++)
                {
                    uint st = ReadUInt32BE(vhData, 0x520 + i * 8);
                    uint cnt = ReadUInt32BE(vhData, 0x520 + i * 8 + 4);
                    if (cnt == 0) break;
                    long extBytes = (long)cnt * blockSize;
                    if (logicalTarget >= extLogicalStart && logicalTarget + nodeSize <= extLogicalStart + extBytes)
                    {
                        leafPhysOff = part.StartingOffset + (long)st * blockSize + (logicalTarget - extLogicalStart);
                        break;
                    }
                    extLogicalStart += extBytes;
                }

                if (leafPhysOff < part.StartingOffset || leafPhysOff + nodeSize > part.EndOffset)
                    return string.Empty;

                byte[] leaf = ReadBytes(disk, leafPhysOff, nodeSize);
                if (leaf.Length < nodeSize || (sbyte)leaf[8] != -1) return string.Empty;

                ushort recCount = ReadUInt16BE(leaf, 10);
                if (recCount == 0) return string.Empty;

                int recOff = ReadUInt16BE(leaf, nodeSize - 2);
                if (recOff < 14 || recOff + 10 > nodeSize) return string.Empty;

                ushort keyLen = ReadUInt16BE(leaf, recOff);
                uint parentId = ReadUInt32BE(leaf, recOff + 2);
                ushort nameChars = ReadUInt16BE(leaf, recOff + 6);
                if (parentId != 1 || keyLen < 6 || 6 + nameChars * 2 > keyLen || recOff + 8 + nameChars * 2 > nodeSize)
                    return string.Empty;

                int dataOff = recOff + 2 + keyLen;
                if ((dataOff & 1) != 0) dataOff++;
                if (dataOff + 12 > nodeSize) return string.Empty;

                ushort recType = ReadUInt16BE(leaf, dataOff);
                uint folderId = ReadUInt32BE(leaf, dataOff + 8);
                if (recType == 1 && folderId == 2 && nameChars > 0)
                    return Encoding.BigEndianUnicode.GetString(leaf, recOff + 8, nameChars * 2).TrimEnd('\0');
            }
            catch
            {
            }
            return string.Empty;
        }

        /// <summary>
        /// Populates an HFS+ Big-Endian Catalog B-Tree header node (<c>+0x8000</c>) and leaf node (<c>+0x9000</c>)
        /// containing default macOS folder/file records readable by the native HFS+ catalog parser.
        /// </summary>
        private static void PopulateHfsPlusCatalogBTree(byte[] buf, PartitionEntry part, byte[] macReadme, byte[] plistBytes, byte[] labelFile)
        {
            const int nodeSize = 4096;
            const int node0Off = 0x8000;
            const int node1Off = 0x9000;
            if (buf.Length < node1Off + nodeSize) return;

            // Volume Header catalogFile fork (offset 0x510 in unwrapped VH at 0x400)
            WriteUInt64BE(buf, 0x510, (ulong)(2 * nodeSize)); // logicalSize = 8192
            WriteUInt32BE(buf, 0x518, nodeSize);              // clumpSize
            WriteUInt32BE(buf, 0x51C, 2);                     // totalBlocks
            WriteUInt32BE(buf, 0x520, 8);                     // extents[0].startBlock (8 * 4096 = 0x8000)
            WriteUInt32BE(buf, 0x524, 2);                     // extents[0].blockCount

            // Node 0: B-Tree Header Node
            buf[node0Off + 8] = 1;                            // kind = kBTHeaderNode
            WriteUInt16BE(buf, node0Off + 10, 3);             // numRecords = 3
            WriteUInt16BE(buf, node0Off + 14, 1);             // treeDepth = 1
            WriteUInt32BE(buf, node0Off + 16, 1);             // rootNode = 1
            WriteUInt32BE(buf, node0Off + 20, 9);             // leafRecords = 9
            WriteUInt32BE(buf, node0Off + 24, 1);             // firstLeafNode = 1
            WriteUInt32BE(buf, node0Off + 28, 1);             // lastLeafNode = 1
            WriteUInt16BE(buf, node0Off + 32, nodeSize);      // nodeSize = 4096
            WriteUInt16BE(buf, node0Off + 34, 516);           // maxKeyLength
            WriteUInt32BE(buf, node0Off + 36, 2);             // totalNodes = 2
            WriteUInt32BE(buf, node0Off + 40, 0);             // freeNodes = 0
            WriteUInt16BE(buf, node0Off + nodeSize - 2, 14);
            WriteUInt16BE(buf, node0Off + nodeSize - 4, 120);
            WriteUInt16BE(buf, node0Off + nodeSize - 6, 248);
            WriteUInt16BE(buf, node0Off + nodeSize - 8, 512);

            // Node 1: B-Tree Leaf Node
            buf[node1Off + 8] = 0xFF;                         // kind = kBTLeafNode (-1)
            buf[node1Off + 9] = 1;                            // height = 1

            string volLabel = string.IsNullOrEmpty(part.Label) ? "Macintosh HD" : part.Label;
            int cursor = 14;
            var recOffsets = new List<int>();

            cursor = WriteHfsFolderRecord(buf, node1Off, cursor, recOffsets, 1, 2, volLabel);
            cursor = WriteHfsFolderRecord(buf, node1Off, cursor, recOffsets, 2, 16, "System");
            cursor = WriteHfsFolderRecord(buf, node1Off, cursor, recOffsets, 16, 17, "Library");
            cursor = WriteHfsFolderRecord(buf, node1Off, cursor, recOffsets, 17, 18, "CoreServices");
            cursor = WriteHfsFolderRecord(buf, node1Off, cursor, recOffsets, 2, 19, "Users");
            cursor = WriteHfsFolderRecord(buf, node1Off, cursor, recOffsets, 19, 20, "Shared");
            cursor = WriteHfsFileRecord(buf, node1Off, cursor, recOffsets, 2, 21, ".VolumeLabel", (ulong)labelFile.Length, 6, 1);
            cursor = WriteHfsFileRecord(buf, node1Off, cursor, recOffsets, 18, 22, "SystemVersion.plist", (ulong)plistBytes.Length, 5, 1);
            cursor = WriteHfsFileRecord(buf, node1Off, cursor, recOffsets, 20, 23, "ReadMe.txt", (ulong)macReadme.Length, 4, 1);

            WriteUInt16BE(buf, node1Off + 10, (ushort)recOffsets.Count);
            for (int i = 0; i < recOffsets.Count; i++)
                WriteUInt16BE(buf, node1Off + nodeSize - 2 * (i + 1), (ushort)recOffsets[i]);
            WriteUInt16BE(buf, node1Off + nodeSize - 2 * (recOffsets.Count + 1), (ushort)cursor);
        }

        private static int WriteHfsCatalogKey(byte[] buf, int nodeOff, int cursor, uint parentId, string name)
        {
            byte[] utf16 = Encoding.BigEndianUnicode.GetBytes(name);
            ushort nameChars = (ushort)(utf16.Length / 2);
            ushort keyLen = (ushort)(6 + utf16.Length);
            int p = nodeOff + cursor;
            WriteUInt16BE(buf, p, keyLen);
            WriteUInt32BE(buf, p + 2, parentId);
            WriteUInt16BE(buf, p + 6, nameChars);
            Array.Copy(utf16, 0, buf, p + 8, utf16.Length);
            int next = cursor + 2 + keyLen;
            if ((next & 1) != 0) next++;
            return next;
        }

        private static int WriteHfsFolderRecord(byte[] buf, int nodeOff, int cursor, List<int> recOffsets, uint parentId, uint folderId, string name)
        {
            recOffsets.Add(cursor);
            int dataRel = WriteHfsCatalogKey(buf, nodeOff, cursor, parentId, name);
            int d = nodeOff + dataRel;
            WriteUInt16BE(buf, d, 1);              // kHFSPlusFolderRecord
            WriteUInt32BE(buf, d + 4, 1);          // valence
            WriteUInt32BE(buf, d + 8, folderId);
            WriteUInt16BE(buf, d + 42, 0x41ED);    // drwxr-xr-x
            return dataRel + 88;
        }

        private static int WriteHfsFileRecord(byte[] buf, int nodeOff, int cursor, List<int> recOffsets, uint parentId, uint fileId, string name, ulong logicalSize, uint startBlock, uint blockCount)
        {
            recOffsets.Add(cursor);
            int dataRel = WriteHfsCatalogKey(buf, nodeOff, cursor, parentId, name);
            int d = nodeOff + dataRel;
            WriteUInt16BE(buf, d, 2);              // kHFSPlusFileRecord
            WriteUInt32BE(buf, d + 8, fileId);
            WriteUInt16BE(buf, d + 42, 0x81A4);    // -rw-r--r--
            WriteUInt64BE(buf, d + 88, logicalSize);
            WriteUInt32BE(buf, d + 100, blockCount);
            WriteUInt32BE(buf, d + 104, startBlock);
            WriteUInt32BE(buf, d + 108, blockCount);
            return dataRel + 248;
        }

        internal sealed class ApfsBTreeRecord
        {
            public ulong SortObjId;
            public int SortRecType;
            public byte[] Key;
            public byte[] Val;
        }

        /// <summary>
        /// Populates an APFS partition buffer with a valid Container (<c>NXSB</c>) + checkpoint descriptor,
        /// Container OMAP, Volume (<c>APSB</c>) + Volume OMAP, and FS-Tree B-tree containing starter files.
        /// </summary>
        private static void PopulateApfsContainerBTree(byte[] buf, PartitionEntry part, byte[] macReadme, byte[] plistBytes, byte[] labelFile)
        {
            const int blockSize = 4096;
            if (buf.Length < 19 * blockSize) return;

            // Block 1: Clean checkpoint NXSB descriptor copy (o_xid = 2)
            Array.Copy(buf, 0, buf, 0x1000, blockSize);
            BitConverter.GetBytes(2UL).CopyTo(buf, 0x1010);
            ReadOnlyFileSystemMountEngine.WriteApfsFletcher64(buf, 0x1000, blockSize);

            // Block 10: Container OMAP phys header (om_tree_oid = 11)
            WriteApfsOmapHeaderBlock(buf, 10 * blockSize, 10UL, 1UL, 11UL);

            // Block 11: Container OMAP B-Tree root leaf (oid 0x402, xid 1 -> paddr 12)
            var containerOmapRecs = new List<ApfsBTreeRecord>
            {
                new ApfsBTreeRecord
                {
                    SortObjId = 0x402UL,
                    Key = BuildApfsOmapKey(0x402UL, 1UL),
                    Val = BuildApfsOmapVal(0U, 4096U, 12UL)
                }
            };
            WriteApfsBTreeNodeBlock(buf, 11 * blockSize, blockSize, 11UL, 1UL, 0x40000002U, 0x000BU, 0x0007, 0, 16U, 16U, 1UL, containerOmapRecs);

            // Block 12: Volume 0 APSB superblock
            int apsbOff = 12 * blockSize;
            BitConverter.GetBytes(0x402UL).CopyTo(buf, apsbOff + 0x08);
            BitConverter.GetBytes(1UL).CopyTo(buf, apsbOff + 0x10);
            BitConverter.GetBytes(0x8000000DU).CopyTo(buf, apsbOff + 0x18);
            BitConverter.GetBytes(0x00000000U).CopyTo(buf, apsbOff + 0x1C);
            Encoding.ASCII.GetBytes("APSB").CopyTo(buf, apsbOff + 0x20);
            ulong allocBlocks = (ulong)Math.Max(20L, (part.UsedBytes ?? (20L * blockSize)) / blockSize);
            BitConverter.GetBytes(allocBlocks).CopyTo(buf, apsbOff + 0x58);
            BitConverter.GetBytes(0x00000002U).CopyTo(buf, apsbOff + 0x74); // apfs_root_tree_type
            BitConverter.GetBytes(13UL).CopyTo(buf, apsbOff + 0x80);        // apfs_omap_oid = 13
            BitConverter.GetBytes(0x404UL).CopyTo(buf, apsbOff + 0x88);     // apfs_root_tree_oid = 0x404
            BitConverter.GetBytes(100UL).CopyTo(buf, apsbOff + 0xB0);       // apfs_next_obj_id
            BitConverter.GetBytes(3UL).CopyTo(buf, apsbOff + 0xB8);         // apfs_num_files
            BitConverter.GetBytes(5UL).CopyTo(buf, apsbOff + 0xC0);         // apfs_num_directories
            if (buf.Length >= 0x58)
                Array.Copy(buf, 0x48, buf, apsbOff + 0xF0, 16);             // apfs_vol_uuid
            BitConverter.GetBytes(1UL).CopyTo(buf, apsbOff + 0x108);        // APFS_FS_UNENCRYPTED
            string volLabel = string.IsNullOrEmpty(part.Label) ? "Macintosh HD" : part.Label;
            byte[] volNameBytes = Encoding.UTF8.GetBytes(volLabel);
            Array.Copy(volNameBytes, 0, buf, apsbOff + 0x2C0, Math.Min(volNameBytes.Length, 255));
            ReadOnlyFileSystemMountEngine.WriteApfsFletcher64(buf, apsbOff, blockSize);

            // Block 13: Volume OMAP phys header (om_tree_oid = 14)
            WriteApfsOmapHeaderBlock(buf, 13 * blockSize, 13UL, 1UL, 14UL);

            // Block 14: Volume OMAP B-Tree root leaf (oid 0x404, xid 1 -> paddr 15)
            var volOmapRecs = new List<ApfsBTreeRecord>
            {
                new ApfsBTreeRecord
                {
                    SortObjId = 0x404UL,
                    Key = BuildApfsOmapKey(0x404UL, 1UL),
                    Val = BuildApfsOmapVal(0U, 4096U, 15UL)
                }
            };
            WriteApfsBTreeNodeBlock(buf, 14 * blockSize, blockSize, 14UL, 1UL, 0x40000002U, 0x000BU, 0x0007, 0, 16U, 16U, 1UL, volOmapRecs);

            // Blocks 16..18: File extent payloads
            Array.Copy(macReadme, 0, buf, 16 * blockSize, Math.Min(macReadme.Length, blockSize));
            Array.Copy(plistBytes, 0, buf, 17 * blockSize, Math.Min(plistBytes.Length, blockSize));
            Array.Copy(labelFile, 0, buf, 18 * blockSize, Math.Min(labelFile.Length, blockSize));

            // Block 15: Volume FS-Tree B-Tree root leaf
            var fsRecs = new List<ApfsBTreeRecord>();
            AddApfsDirInode(fsRecs, 2UL, 1UL, volLabel, 0x41ED);
            AddApfsDirInode(fsRecs, 16UL, 2UL, "System", 0x41ED);
            AddApfsDirInode(fsRecs, 17UL, 16UL, "Library", 0x41ED);
            AddApfsDirInode(fsRecs, 18UL, 17UL, "CoreServices", 0x41ED);
            AddApfsDirInode(fsRecs, 19UL, 2UL, "Users", 0x41ED);
            AddApfsDirInode(fsRecs, 20UL, 19UL, "Shared", 0x41ED);

            AddApfsFileRecords(fsRecs, 21UL, 2UL, ".VolumeLabel", (ulong)labelFile.Length, 18UL, 4096L);
            AddApfsFileRecords(fsRecs, 22UL, 18UL, "SystemVersion.plist", (ulong)plistBytes.Length, 17UL, 4096L);
            AddApfsFileRecords(fsRecs, 23UL, 20UL, "ReadMe.txt", (ulong)macReadme.Length, 16UL, 4096L);

            AddApfsDirRec(fsRecs, 2UL, "System", 16UL, 4);
            AddApfsDirRec(fsRecs, 16UL, "Library", 17UL, 4);
            AddApfsDirRec(fsRecs, 17UL, "CoreServices", 18UL, 4);
            AddApfsDirRec(fsRecs, 2UL, "Users", 19UL, 4);
            AddApfsDirRec(fsRecs, 19UL, "Shared", 20UL, 4);
            AddApfsDirRec(fsRecs, 2UL, ".VolumeLabel", 21UL, 8);
            AddApfsDirRec(fsRecs, 18UL, "SystemVersion.plist", 22UL, 8);
            AddApfsDirRec(fsRecs, 20UL, "ReadMe.txt", 23UL, 8);

            fsRecs.Sort(delegate(ApfsBTreeRecord a, ApfsBTreeRecord b)
            {
                int c = a.SortObjId.CompareTo(b.SortObjId);
                return (c != 0) ? c : a.SortRecType.CompareTo(b.SortRecType);
            });

            WriteApfsBTreeNodeBlock(buf, 15 * blockSize, blockSize, 0x404UL, 1UL, 0x80000002U, 0x000EU, 0x000B, 0, 0U, 0U, 1UL, fsRecs);
        }

        internal static void WriteApfsOmapHeaderBlock(byte[] buf, int off, ulong oOid, ulong oXid, ulong omTreeOid)
        {
            BitConverter.GetBytes(oOid).CopyTo(buf, off + 0x08);
            BitConverter.GetBytes(oXid).CopyTo(buf, off + 0x10);
            BitConverter.GetBytes(0x0000000BU).CopyTo(buf, off + 0x18); // OBJECT_TYPE_OMAP
            BitConverter.GetBytes(0x00000000U).CopyTo(buf, off + 0x1C);
            BitConverter.GetBytes(0x00000002U).CopyTo(buf, off + 0x28); // om_tree_type = BTREE
            BitConverter.GetBytes(omTreeOid).CopyTo(buf, off + 0x30);
            ReadOnlyFileSystemMountEngine.WriteApfsFletcher64(buf, off, 4096);
        }

        internal static byte[] BuildApfsOmapKey(ulong oid, ulong xid)
        {
            byte[] k = new byte[16];
            BitConverter.GetBytes(oid).CopyTo(k, 0);
            BitConverter.GetBytes(xid).CopyTo(k, 8);
            return k;
        }

        internal static byte[] BuildApfsOmapVal(uint flags, uint size, ulong paddr)
        {
            byte[] v = new byte[16];
            BitConverter.GetBytes(flags).CopyTo(v, 0);
            BitConverter.GetBytes(size).CopyTo(v, 4);
            BitConverter.GetBytes(paddr).CopyTo(v, 8);
            return v;
        }

        internal static void AddApfsDirInode(List<ApfsBTreeRecord> recs, ulong ino, ulong parentId, string name, ushort mode)
        {
            byte[] nameBytes = Encoding.UTF8.GetBytes((name ?? "dir") + "\0");
            int extPadLen = (nameBytes.Length + 7) & ~7;
            byte[] val = new byte[92 + 8 + extPadLen];
            BitConverter.GetBytes(parentId).CopyTo(val, 0x00);
            BitConverter.GetBytes(ino).CopyTo(val, 0x08);
            ulong ts = 1762992000000000000UL;
            BitConverter.GetBytes(ts).CopyTo(val, 0x10);
            BitConverter.GetBytes(ts).CopyTo(val, 0x18);
            BitConverter.GetBytes(ts).CopyTo(val, 0x20);
            BitConverter.GetBytes(ts).CopyTo(val, 0x28);
            BitConverter.GetBytes(1).CopyTo(val, 0x38);
            BitConverter.GetBytes(mode).CopyTo(val, 0x50);
            BitConverter.GetBytes((ushort)1).CopyTo(val, 92);
            BitConverter.GetBytes((ushort)extPadLen).CopyTo(val, 94);
            val[96] = 4; // INO_EXT_TYPE_NAME
            BitConverter.GetBytes((ushort)nameBytes.Length).CopyTo(val, 98);
            Array.Copy(nameBytes, 0, val, 100, nameBytes.Length);

            recs.Add(new ApfsBTreeRecord
            {
                SortObjId = ino,
                SortRecType = 3,
                Key = BitConverter.GetBytes(ino | (3UL << 60)),
                Val = val
            });
        }

        internal static void AddApfsFileRecords(List<ApfsBTreeRecord> recs, ulong ino, ulong parentId, string name, ulong fileSize, ulong physBlock, long extentLenBytes)
        {
            byte[] nameBytes = Encoding.UTF8.GetBytes((name ?? "file") + "\0");
            int namePad = (nameBytes.Length + 7) & ~7;
            int dstreamLen = 40;
            byte[] val = new byte[92 + 12 + namePad + dstreamLen];
            BitConverter.GetBytes(parentId).CopyTo(val, 0x00);
            BitConverter.GetBytes(ino).CopyTo(val, 0x08);
            ulong ts = 1762992000000000000UL;
            BitConverter.GetBytes(ts).CopyTo(val, 0x10);
            BitConverter.GetBytes(ts).CopyTo(val, 0x18);
            BitConverter.GetBytes(ts).CopyTo(val, 0x20);
            BitConverter.GetBytes(ts).CopyTo(val, 0x28);
            BitConverter.GetBytes(1).CopyTo(val, 0x38);
            BitConverter.GetBytes((ushort)0x81A4).CopyTo(val, 0x50);
            BitConverter.GetBytes((ushort)2).CopyTo(val, 92);
            BitConverter.GetBytes((ushort)(namePad + dstreamLen)).CopyTo(val, 94);
            val[96] = 4; // INO_EXT_TYPE_NAME
            BitConverter.GetBytes((ushort)nameBytes.Length).CopyTo(val, 98);
            val[100] = 8; // INO_EXT_TYPE_DSTREAM
            BitConverter.GetBytes((ushort)dstreamLen).CopyTo(val, 102);
            Array.Copy(nameBytes, 0, val, 104, nameBytes.Length);
            int dsOff = 104 + namePad;
            BitConverter.GetBytes(fileSize).CopyTo(val, dsOff + 0);
            BitConverter.GetBytes((ulong)extentLenBytes).CopyTo(val, dsOff + 8);

            recs.Add(new ApfsBTreeRecord
            {
                SortObjId = ino,
                SortRecType = 3,
                Key = BitConverter.GetBytes(ino | (3UL << 60)),
                Val = val
            });

            if (physBlock > 0 && extentLenBytes > 0)
                AddApfsFileExtent(recs, ino, 0UL, extentLenBytes, physBlock);
        }

        internal static void AddApfsFileExtent(List<ApfsBTreeRecord> recs, ulong privId, ulong logicalAddr, long lenBytes, ulong physBlock)
        {
            byte[] extKey = new byte[16];
            BitConverter.GetBytes(privId | (8UL << 60)).CopyTo(extKey, 0);
            BitConverter.GetBytes(logicalAddr).CopyTo(extKey, 8);

            byte[] extVal = new byte[24];
            BitConverter.GetBytes((ulong)lenBytes & 0x00FFFFFFFFFFFFFFUL).CopyTo(extVal, 0);
            BitConverter.GetBytes(physBlock).CopyTo(extVal, 8);

            recs.Add(new ApfsBTreeRecord
            {
                SortObjId = privId,
                SortRecType = 8,
                Key = extKey,
                Val = extVal
            });
        }

        internal static void AddApfsDirRec(List<ApfsBTreeRecord> recs, ulong parentId, string name, ulong childId, byte dtType)
        {
            byte[] nameBytes = Encoding.UTF8.GetBytes((name ?? "entry") + "\0");
            uint crc = Crc32.Compute(nameBytes, 0, nameBytes.Length);
            uint lenAndHash = ((crc & 0x3FFFFFU) << 10) | (uint)(nameBytes.Length & 0x3FF);
            byte[] key = new byte[12 + nameBytes.Length];
            BitConverter.GetBytes(parentId | (9UL << 60)).CopyTo(key, 0);
            BitConverter.GetBytes(lenAndHash).CopyTo(key, 8);
            Array.Copy(nameBytes, 0, key, 12, nameBytes.Length);

            byte[] val = new byte[18];
            BitConverter.GetBytes(childId).CopyTo(val, 0);
            BitConverter.GetBytes(1762992000000000000UL).CopyTo(val, 8);
            BitConverter.GetBytes((ushort)dtType).CopyTo(val, 16);

            recs.Add(new ApfsBTreeRecord
            {
                SortObjId = parentId,
                SortRecType = 9,
                Key = key,
                Val = val
            });
        }

        internal static void WriteApfsBTreeNodeBlock(
            byte[] buf, int off, int blockSize,
            ulong oOid, ulong oXid, uint oType, uint oSubtype,
            ushort btnFlags, ushort btnLevel, uint btKeySize, uint btValSize, ulong btNodeCount,
            List<ApfsBTreeRecord> recs)
        {
            if (off < 0 || off + blockSize > buf.Length) return;

            bool isRoot = (btnFlags & 0x0001) != 0;
            bool isFixed = (btnFlags & 0x0004) != 0;
            bool isHashed = (btnFlags & 0x0008) != 0;
            int tocEntryLen = isFixed ? 4 : 8;
            int tocLen = recs.Count * tocEntryLen;

            BitConverter.GetBytes(oOid).CopyTo(buf, off + 0x08);
            BitConverter.GetBytes(oXid).CopyTo(buf, off + 0x10);
            BitConverter.GetBytes(oType).CopyTo(buf, off + 0x18);
            BitConverter.GetBytes(oSubtype).CopyTo(buf, off + 0x1C);
            BitConverter.GetBytes(btnFlags).CopyTo(buf, off + 0x20);
            BitConverter.GetBytes(btnLevel).CopyTo(buf, off + 0x22);
            BitConverter.GetBytes((uint)recs.Count).CopyTo(buf, off + 0x24);
            BitConverter.GetBytes((ushort)0).CopyTo(buf, off + 0x28);
            BitConverter.GetBytes((ushort)tocLen).CopyTo(buf, off + 0x2A);

            int tocStart = off + 0x38;
            int keyCursor = tocStart + tocLen;
            int keyAreaStart = keyCursor;
            int valAreaEnd = off + (isRoot ? (blockSize - 40) : blockSize);
            int valCursor = valAreaEnd;

            uint maxKeyLen = 0;
            uint maxValLen = 0;
            for (int i = 0; i < recs.Count; i++)
            {
                var r = recs[i];
                if (keyCursor + r.Key.Length > valCursor - r.Val.Length) break;

                int kRel = keyCursor - keyAreaStart;
                Array.Copy(r.Key, 0, buf, keyCursor, r.Key.Length);
                keyCursor += r.Key.Length;

                valCursor -= r.Val.Length;
                Array.Copy(r.Val, 0, buf, valCursor, r.Val.Length);
                int vRel = valAreaEnd - valCursor;

                int tOff = tocStart + i * tocEntryLen;
                if (isFixed)
                {
                    BitConverter.GetBytes((ushort)kRel).CopyTo(buf, tOff);
                    BitConverter.GetBytes((ushort)vRel).CopyTo(buf, tOff + 2);
                }
                else
                {
                    BitConverter.GetBytes((ushort)kRel).CopyTo(buf, tOff);
                    BitConverter.GetBytes((ushort)r.Key.Length).CopyTo(buf, tOff + 2);
                    BitConverter.GetBytes((ushort)vRel).CopyTo(buf, tOff + 4);
                    BitConverter.GetBytes((ushort)r.Val.Length).CopyTo(buf, tOff + 6);
                }

                if ((uint)r.Key.Length > maxKeyLen) maxKeyLen = (uint)r.Key.Length;
                if ((uint)r.Val.Length > maxValLen) maxValLen = (uint)r.Val.Length;
            }

            if (isRoot)
            {
                int infoOff = off + blockSize - 40;
                uint btFlags = (isFixed ? 1U : 0U) | (isHashed ? 0x80U : 0U);
                BitConverter.GetBytes(btFlags).CopyTo(buf, infoOff + 0);
                BitConverter.GetBytes((uint)blockSize).CopyTo(buf, infoOff + 4);
                BitConverter.GetBytes(btKeySize).CopyTo(buf, infoOff + 8);
                BitConverter.GetBytes(btValSize).CopyTo(buf, infoOff + 12);
                BitConverter.GetBytes(maxKeyLen).CopyTo(buf, infoOff + 16);
                BitConverter.GetBytes(maxValLen).CopyTo(buf, infoOff + 20);
                BitConverter.GetBytes((ulong)recs.Count).CopyTo(buf, infoOff + 24);
                BitConverter.GetBytes(btNodeCount).CopyTo(buf, infoOff + 32);
            }

            ReadOnlyFileSystemMountEngine.WriteApfsFletcher64(buf, off, blockSize);
        }

        private sealed class BtrfsLeafSpec
        {
            public ulong ObjectId;
            public byte KeyType;
            public ulong KeyOffset;
            public byte[] Payload;
        }

        /// <summary>
        /// Populates a Btrfs filesystem tree leaf node (<c>+0x14000</c>) plus CRC32C leaf checksums,
        /// chunk tree mappings, and inline/regular <c>EXTENT_DATA</c> items for native mounting.
        /// </summary>
        private static void PopulateBtrfsFsTree(byte[] buf, PartitionEntry part, byte[] fsid)
        {
            const int nodeOff = 0x14000;
            const int nodeSize = 16384;
            if (buf.Length < nodeOff + nodeSize + 0x2000) return;

            string lbl = string.IsNullOrEmpty(part.Label) ? "(unlabeled)" : part.Label;
            string uuid = part.UUID ?? string.Empty;
            byte[] fsBytes = Encoding.UTF8.GetBytes("# Supported Linux filesystems (btrfs primary)\nbtrfs\next4\nxfs\nf2fs\nzfs\n");
            byte[] readmeBytes = Encoding.UTF8.GetBytes(
                "Btrfs Copy-on-Write Volume Preview\n" +
                "Label    : " + lbl + "\n" +
                "UUID     : " + uuid + "\n" +
                "Capacity : " + ByteFormatter.FormatBytes(part.PartitionLength) + "\n" +
                "Subvols  : /etc, /home, /var/log, /@dev\n");
            byte[] logBytes = Encoding.UTF8.GetBytes(
                "[WinParted-RO] Btrfs B-Tree Leaf parsed at 0x14000\n" +
                "[WinParted-RO] UUID=" + uuid + "\n");
            byte[] subvolConf = Encoding.UTF8.GetBytes(
                "# Btrfs developer subvolume config\nsubvol=@dev\ncompress=zstd:3\nmount=read-only\n");

            // Write regular extents at 0x18000 (/etc/filesystems) and 0x19000 (/home/ReadMe.txt)
            Array.Copy(fsBytes, 0, buf, 0x18000, Math.Min(fsBytes.Length, 4096));
            Array.Copy(readmeBytes, 0, buf, 0x19000, Math.Min(readmeBytes.Length, 4096));

            var items = new List<BtrfsLeafSpec>();

            // Root inode (256) and subdirs
            items.Add(new BtrfsLeafSpec { ObjectId = 256, KeyType = 1, KeyOffset = 0, Payload = BuildBtrfsInodeItem(0, 0x41ED) });
            items.Add(new BtrfsLeafSpec { ObjectId = 256, KeyType = 84, KeyOffset = 1001, Payload = BuildBtrfsDirItem(257, 1, 2, "etc") });
            items.Add(new BtrfsLeafSpec { ObjectId = 256, KeyType = 84, KeyOffset = 1002, Payload = BuildBtrfsDirItem(258, 1, 2, "home") });
            items.Add(new BtrfsLeafSpec { ObjectId = 256, KeyType = 84, KeyOffset = 1003, Payload = BuildBtrfsDirItem(259, 1, 2, "var") });
            items.Add(new BtrfsLeafSpec { ObjectId = 256, KeyType = 84, KeyOffset = 1004, Payload = BuildBtrfsDirItem(260, 1, 2, "@dev") });

            items.Add(new BtrfsLeafSpec { ObjectId = 257, KeyType = 1, KeyOffset = 0, Payload = BuildBtrfsInodeItem(0, 0x41ED) });
            items.Add(new BtrfsLeafSpec { ObjectId = 257, KeyType = 84, KeyOffset = 2001, Payload = BuildBtrfsDirItem(261, 1, 1, "filesystems") });

            items.Add(new BtrfsLeafSpec { ObjectId = 258, KeyType = 1, KeyOffset = 0, Payload = BuildBtrfsInodeItem(0, 0x41ED) });
            items.Add(new BtrfsLeafSpec { ObjectId = 258, KeyType = 84, KeyOffset = 3001, Payload = BuildBtrfsDirItem(262, 1, 1, "ReadMe.txt") });

            items.Add(new BtrfsLeafSpec { ObjectId = 259, KeyType = 1, KeyOffset = 0, Payload = BuildBtrfsInodeItem(0, 0x41ED) });
            items.Add(new BtrfsLeafSpec { ObjectId = 259, KeyType = 84, KeyOffset = 4001, Payload = BuildBtrfsDirItem(263, 1, 2, "log") });

            items.Add(new BtrfsLeafSpec { ObjectId = 260, KeyType = 1, KeyOffset = 0, Payload = BuildBtrfsInodeItem(0, 0x41ED) });
            items.Add(new BtrfsLeafSpec { ObjectId = 260, KeyType = 84, KeyOffset = 5001, Payload = BuildBtrfsDirItem(265, 1, 1, "btrfs-subvol.conf") });

            // Regular files: 261 (/etc/filesystems) & 262 (/home/ReadMe.txt) use regular extents at 0x18000 / 0x19000
            items.Add(new BtrfsLeafSpec { ObjectId = 261, KeyType = 1, KeyOffset = 0, Payload = BuildBtrfsInodeItem((ulong)fsBytes.Length, 0x81A4) });
            items.Add(new BtrfsLeafSpec { ObjectId = 261, KeyType = 108, KeyOffset = 0, Payload = BuildBtrfsRegularExtent(0x18000UL, 4096UL, (ulong)fsBytes.Length) });

            items.Add(new BtrfsLeafSpec { ObjectId = 262, KeyType = 1, KeyOffset = 0, Payload = BuildBtrfsInodeItem((ulong)readmeBytes.Length, 0x81A4) });
            items.Add(new BtrfsLeafSpec { ObjectId = 262, KeyType = 108, KeyOffset = 0, Payload = BuildBtrfsRegularExtent(0x19000UL, 4096UL, (ulong)readmeBytes.Length) });

            items.Add(new BtrfsLeafSpec { ObjectId = 263, KeyType = 1, KeyOffset = 0, Payload = BuildBtrfsInodeItem(0, 0x41ED) });
            items.Add(new BtrfsLeafSpec { ObjectId = 263, KeyType = 84, KeyOffset = 6001, Payload = BuildBtrfsDirItem(264, 1, 1, "mount-info.log") });

            // Inline files: 264 (/var/log/mount-info.log) & 265 (/@dev/btrfs-subvol.conf)
            items.Add(new BtrfsLeafSpec { ObjectId = 264, KeyType = 1, KeyOffset = 0, Payload = BuildBtrfsInodeItem((ulong)logBytes.Length, 0x81A4) });
            items.Add(new BtrfsLeafSpec { ObjectId = 264, KeyType = 108, KeyOffset = 0, Payload = BuildBtrfsInlineExtent(logBytes) });

            items.Add(new BtrfsLeafSpec { ObjectId = 265, KeyType = 1, KeyOffset = 0, Payload = BuildBtrfsInodeItem((ulong)subvolConf.Length, 0x81A4) });
            items.Add(new BtrfsLeafSpec { ObjectId = 265, KeyType = 108, KeyOffset = 0, Payload = BuildBtrfsInlineExtent(subvolConf) });

            items.Sort(delegate(BtrfsLeafSpec a, BtrfsLeafSpec b)
            {
                int c = a.ObjectId.CompareTo(b.ObjectId);
                if (c != 0) return c;
                c = a.KeyType.CompareTo(b.KeyType);
                if (c != 0) return c;
                return a.KeyOffset.CompareTo(b.KeyOffset);
            });

            Array.Copy(fsid, 0, buf, nodeOff + 0x20, 16);
            BitConverter.GetBytes((ulong)nodeOff).CopyTo(buf, nodeOff + 0x30);
            BitConverter.GetBytes(1UL).CopyTo(buf, nodeOff + 0x38);
            BitConverter.GetBytes(1UL).CopyTo(buf, nodeOff + 0x50);
            BitConverter.GetBytes(5UL).CopyTo(buf, nodeOff + 0x58); // BTRFS_FS_TREE_OBJECTID
            BitConverter.GetBytes((uint)items.Count).CopyTo(buf, nodeOff + 0x60);
            buf[nodeOff + 0x64] = 0; // leaf level

            int dataEnd = nodeSize;
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                dataEnd -= it.Payload.Length;
                if (dataEnd < 101 + items.Count * 25) break;
                Array.Copy(it.Payload, 0, buf, nodeOff + dataEnd, it.Payload.Length);

                int hdrOff = nodeOff + 101 + i * 25;
                BitConverter.GetBytes(it.ObjectId).CopyTo(buf, hdrOff + 0);
                buf[hdrOff + 8] = it.KeyType;
                BitConverter.GetBytes(it.KeyOffset).CopyTo(buf, hdrOff + 9);
                BitConverter.GetBytes((uint)(dataEnd - 101)).CopyTo(buf, hdrOff + 17);
                BitConverter.GetBytes((uint)it.Payload.Length).CopyTo(buf, hdrOff + 21);
            }
        }

        private static byte[] BuildBtrfsInodeItem(ulong size, uint mode)
        {
            byte[] d = new byte[160];
            BitConverter.GetBytes(1UL).CopyTo(d, 0);
            BitConverter.GetBytes(1UL).CopyTo(d, 8);
            BitConverter.GetBytes(size).CopyTo(d, 16);
            BitConverter.GetBytes(size).CopyTo(d, 24);
            BitConverter.GetBytes(1U).CopyTo(d, 40);
            BitConverter.GetBytes(mode).CopyTo(d, 52);
            BitConverter.GetBytes(1762992000UL).CopyTo(d, 136);
            return d;
        }

        private static byte[] BuildBtrfsDirItem(ulong childObjId, byte childKeyType, byte ftType, string name)
        {
            byte[] nameBytes = Encoding.UTF8.GetBytes(name);
            byte[] d = new byte[30 + nameBytes.Length];
            BitConverter.GetBytes(childObjId).CopyTo(d, 0);
            d[8] = childKeyType;
            BitConverter.GetBytes(0UL).CopyTo(d, 9);
            BitConverter.GetBytes(1UL).CopyTo(d, 17);
            BitConverter.GetBytes((ushort)0).CopyTo(d, 25);
            BitConverter.GetBytes((ushort)nameBytes.Length).CopyTo(d, 27);
            d[29] = ftType;
            Array.Copy(nameBytes, 0, d, 30, nameBytes.Length);
            return d;
        }

        private static byte[] BuildBtrfsInlineExtent(byte[] data)
        {
            byte[] d = new byte[21 + data.Length];
            BitConverter.GetBytes(1UL).CopyTo(d, 0);
            BitConverter.GetBytes((ulong)data.Length).CopyTo(d, 8);
            d[20] = 0; // BTRFS_FILE_EXTENT_INLINE
            Array.Copy(data, 0, d, 21, data.Length);
            return d;
        }

        private static byte[] BuildBtrfsRegularExtent(ulong diskBytenr, ulong diskNumBytes, ulong numBytes)
        {
            byte[] d = new byte[53];
            BitConverter.GetBytes(1UL).CopyTo(d, 0);
            BitConverter.GetBytes(numBytes).CopyTo(d, 8);
            d[20] = 1; // BTRFS_FILE_EXTENT_REG
            BitConverter.GetBytes(diskBytenr).CopyTo(d, 21);
            BitConverter.GetBytes(diskNumBytes).CopyTo(d, 29);
            BitConverter.GetBytes(0UL).CopyTo(d, 37);
            BitConverter.GetBytes(numBytes).CopyTo(d, 45);
            return d;
        }

        /// <summary>
        /// Populates XFS v4/v5 Big-Endian inodes (<c>IN</c>, root inode 128 at <c>+0x10000</c>), shortform
        /// directories, and 128-bit BMBT extent records so formatted XFS images mount natively.
        /// </summary>
        private static void PopulateXfsFsTree(byte[] buf, PartitionEntry part)
        {
            // Root inode = 128 at 128 * 512 = 0x10000; data blocks 24..27 at 0x18000..0x1B000
            if (buf.Length < 0x1C000) return;

            string lbl = string.IsNullOrEmpty(part.Label) ? "(unlabeled)" : part.Label;
            string uuid = part.UUID ?? string.Empty;

            byte[] fsBytes = Encoding.UTF8.GetBytes("# Supported Linux filesystems (xfs primary)\nxfs\next4\nbtrfs\nf2fs\n");
            byte[] readmeBytes = Encoding.UTF8.GetBytes(
                "Linux XFS Read-Only Mount Preview\n" +
                "Label    : " + lbl + "\n" +
                "UUID     : " + uuid + "\n" +
                "Capacity : " + ByteFormatter.FormatBytes(part.PartitionLength) + "\n" +
                "Parsed natively from XFS Big-Endian superblock & IN inodes.\n");
            byte[] logBytes = Encoding.UTF8.GetBytes(
                "[WinParted-RO] XFS IN inodes & BMBT extents verified (UUID=" + uuid + ")\n");
            byte[] projJson = Encoding.UTF8.GetBytes(
                "{\n  \"filesystem\": \"XFS\",\n  \"agcount\": 1,\n  \"blocksize\": 4096,\n  \"inodesize\": 512,\n  \"readonly\": true\n}\n");

            Array.Copy(fsBytes, 0, buf, 24 * 4096, Math.Min(fsBytes.Length, 4096));
            Array.Copy(readmeBytes, 0, buf, 25 * 4096, Math.Min(readmeBytes.Length, 4096));
            Array.Copy(logBytes, 0, buf, 26 * 4096, Math.Min(logBytes.Length, 4096));
            Array.Copy(projJson, 0, buf, 27 * 4096, Math.Min(projJson.Length, 4096));

            // Directory inodes: 128 (/), 129 (/etc), 130 (/home), 131 (/var), 132 (/projects), 135 (/var/log)
            WriteXfsShortformDirInode(buf, 128, 128, new[]
            {
                new ExtDirSpec(129u, 2, "etc"),
                new ExtDirSpec(130u, 2, "home"),
                new ExtDirSpec(131u, 2, "var"),
                new ExtDirSpec(132u, 2, "projects")
            });
            WriteXfsShortformDirInode(buf, 129, 128, new[] { new ExtDirSpec(133u, 1, "filesystems") });
            WriteXfsShortformDirInode(buf, 130, 128, new[] { new ExtDirSpec(134u, 1, "ReadMe.txt") });
            WriteXfsShortformDirInode(buf, 131, 128, new[] { new ExtDirSpec(135u, 2, "log") });
            WriteXfsShortformDirInode(buf, 135, 131, new[] { new ExtDirSpec(136u, 1, "mount-info.log") });
            WriteXfsShortformDirInode(buf, 132, 128, new[] { new ExtDirSpec(137u, 1, "xfs_project.json") });

            // File inodes (format = 2 XFS_DINODE_FMT_EXTENTS -> blocks 24..27)
            WriteXfsExtentFileInode(buf, 133, (ulong)fsBytes.Length, 24UL);
            WriteXfsExtentFileInode(buf, 134, (ulong)readmeBytes.Length, 25UL);
            WriteXfsExtentFileInode(buf, 136, (ulong)logBytes.Length, 26UL);
            WriteXfsExtentFileInode(buf, 137, (ulong)projJson.Length, 27UL);
        }

        private static void WriteXfsInodeHeader(byte[] buf, uint ino, ushort mode, byte format, ulong size, ulong nblocks, uint nextents)
        {
            int off = (int)ino * 512;
            if (off + 512 > buf.Length) return;

            WriteUInt16BE(buf, off + 0, 0x494E); // "IN"
            WriteUInt16BE(buf, off + 2, mode);
            buf[off + 4] = 2;                      // di_version = 2
            buf[off + 5] = format;                 // 1 = local, 2 = extents
            WriteUInt32BE(buf, off + 16, 1U);      // di_nlink
            WriteUInt32BE(buf, off + 40, 1762992000U); // di_mtime sec
            WriteUInt64BE(buf, off + 56, size);
            WriteUInt64BE(buf, off + 64, nblocks);
            WriteUInt32BE(buf, off + 76, nextents);
        }

        private static void WriteXfsShortformDirInode(byte[] buf, uint ino, uint parentIno, ExtDirSpec[] entries)
        {
            int off = (int)ino * 512;
            if (off + 512 > buf.Length) return;

            int df = off + 100;
            buf[df + 0] = (byte)entries.Length; // count
            buf[df + 1] = 0;                    // i8count = 0 (32-bit inos)
            WriteUInt32BE(buf, df + 2, parentIno);

            int cur = df + 6;
            ushort fakeDirOffset = 0x10;
            for (int i = 0; i < entries.Length; i++)
            {
                byte[] nameBytes = Encoding.UTF8.GetBytes(entries[i].Name);
                if (cur + 7 + nameBytes.Length > off + 512) break;
                buf[cur++] = (byte)nameBytes.Length;
                WriteUInt16BE(buf, cur, fakeDirOffset); cur += 2;
                Array.Copy(nameBytes, 0, buf, cur, nameBytes.Length); cur += nameBytes.Length;
                WriteUInt32BE(buf, cur, entries[i].Inode); cur += 4;
                fakeDirOffset += 0x20;
            }

            WriteXfsInodeHeader(buf, ino, 0x41ED, 1, (ulong)(cur - (off + 100)), 0UL, 0U);
        }

        private static void WriteXfsExtentFileInode(byte[] buf, uint ino, ulong sizeBytes, ulong startBlock)
        {
            int off = (int)ino * 512;
            if (off + 116 > buf.Length) return;

            WriteXfsInodeHeader(buf, ino, 0x81A4, 2, sizeBytes, 1UL, 1U);
            ulong startOff = 0UL;
            ulong blockCount = 1UL;
            ulong hi = ((startOff & 0x003FFFFFFFFFFFFFUL) << 9) | ((startBlock >> 43) & 0x1FFUL);
            ulong lo = ((startBlock & 0x000007FFFFFFFFFFUL) << 21) | (blockCount & 0x1FFFFFUL);
            WriteUInt64BE(buf, off + 100, hi);
            WriteUInt64BE(buf, off + 108, lo);
        }

        /// <summary>
        /// Builds ECMA-119 ISO9660 directory records starting at LBA 17 (<c>+0x8800</c>) and links them
        /// into the Primary Volume Descriptor root directory record at <c>+0x809C</c>.
        /// </summary>
        private static void PopulateIso9660DirectoryTree(byte[] buf, PartitionEntry part, Guid uuid)
        {
            if (buf.Length < 0xB800) return;

            string lbl = string.IsNullOrEmpty(part.Label) ? "DEV_ISO" : part.Label;
            byte[] readmeBytes = Encoding.UTF8.GetBytes(
                "ISO9660 (ECMA-119) Developer Image\n" +
                "Volume ID : " + lbl + "\n" +
                "UUID      : " + uuid.ToString("D") + "\n" +
                "Capacity  : " + ByteFormatter.FormatBytes(part.PartitionLength) + "\n");
            byte[] manifestBytes = Encoding.UTF8.GetBytes(
                "{\n  \"standard\": \"ECMA-119\",\n  \"volume\": \"" + lbl + "\",\n  \"logicalBlockSize\": 2048\n}\n");
            byte[] envBytes = Encoding.UTF8.GetBytes(
                "ARCH=x86_64\nFS=iso9660\nREADONLY=1\n");

            Array.Copy(readmeBytes, 0, buf, 20 * 2048, Math.Min(readmeBytes.Length, 2048));
            Array.Copy(manifestBytes, 0, buf, 21 * 2048, Math.Min(manifestBytes.Length, 2048));
            Array.Copy(envBytes, 0, buf, 22 * 2048, Math.Min(envBytes.Length, 2048));

            // LBA 18 (0x9000): Root directory
            int p = 18 * 2048;
            p += WriteIso9660DirectoryRecord(buf, p, 18u, 2048u, 0x02, "\x00");
            p += WriteIso9660DirectoryRecord(buf, p, 18u, 2048u, 0x02, "\x01");
            p += WriteIso9660DirectoryRecord(buf, p, 19u, 2048u, 0x02, "DEV");
            p += WriteIso9660DirectoryRecord(buf, p, 20u, (uint)readmeBytes.Length, 0x00, "README.TXT;1");
            WriteIso9660DirectoryRecord(buf, p, 21u, (uint)manifestBytes.Length, 0x00, "ISO_MANIFEST.JSON;1");

            // LBA 19 (0x9800): /DEV directory
            int d = 19 * 2048;
            d += WriteIso9660DirectoryRecord(buf, d, 19u, 2048u, 0x02, "\x00");
            d += WriteIso9660DirectoryRecord(buf, d, 18u, 2048u, 0x02, "\x01");
            WriteIso9660DirectoryRecord(buf, d, 22u, (uint)envBytes.Length, 0x00, "TOOLCHAIN.ENV;1");
        }

        private static int WriteIso9660DirectoryRecord(byte[] buf, int offset, uint lba, uint dataLen, byte flags, string name)
        {
            byte[] nameBytes = Encoding.ASCII.GetBytes(name);
            int recLen = 33 + nameBytes.Length;
            if ((recLen & 1) != 0) recLen++;
            if (offset + recLen > buf.Length) return 0;

            buf[offset + 0] = (byte)recLen;
            buf[offset + 1] = 0;
            BitConverter.GetBytes(lba).CopyTo(buf, offset + 2);
            WriteUInt32BE(buf, offset + 6, lba);
            BitConverter.GetBytes(dataLen).CopyTo(buf, offset + 10);
            WriteUInt32BE(buf, offset + 14, dataLen);
            buf[offset + 18] = 126; // years since 1900 (2026)
            buf[offset + 19] = 11;
            buf[offset + 20] = 13;
            buf[offset + 25] = flags;
            BitConverter.GetBytes((ushort)1).CopyTo(buf, offset + 28);
            WriteUInt16BE(buf, offset + 30, 1);
            buf[offset + 32] = (byte)nameBytes.Length;
            Array.Copy(nameBytes, 0, buf, offset + 33, nameBytes.Length);
            return recLen;
        }

        private static ulong ReadUInt64BE(byte[] data, int offset)
        {
            return ((ulong)ReadUInt32BE(data, offset) << 32) | ReadUInt32BE(data, offset + 4);
        }

        private static ushort ReadUInt16BE(byte[] data, int offset)
        {
            return (ushort)((data[offset] << 8) | data[offset + 1]);
        }

        private static uint ReadUInt32BE(byte[] data, int offset)
        {
            return ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];
        }

        private static void WriteUInt16BE(byte[] data, int offset, ushort val)
        {
            data[offset] = (byte)(val >> 8);
            data[offset + 1] = (byte)(val & 0xFF);
        }

        private static void WriteUInt32BE(byte[] data, int offset, uint val)
        {
            data[offset] = (byte)(val >> 24);
            data[offset + 1] = (byte)((val >> 16) & 0xFF);
            data[offset + 2] = (byte)((val >> 8) & 0xFF);
            data[offset + 3] = (byte)(val & 0xFF);
        }

        private static void WriteUInt64BE(byte[] data, int offset, ulong val)
        {
            WriteUInt32BE(data, offset, (uint)(val >> 32));
            WriteUInt32BE(data, offset + 4, (uint)(val & 0xFFFFFFFFUL));
        }

        private static bool MatchesAscii(byte[] data, int offset, string text)
        {
            if (offset < 0 || offset + text.Length > data.Length) return false;
            for (int i = 0; i < text.Length; i++)
            {
                if (data[offset + i] != (byte)text[i]) return false;
            }
            return true;
        }

        private static string FormatRfcUuid(byte[] b)
        {
            if (b.Length < 16) return string.Empty;
            return string.Format("{0:x2}{1:x2}{2:x2}{3:x2}-{4:x2}{5:x2}-{6:x2}{7:x2}-{8:x2}{9:x2}-{10:x2}{11:x2}{12:x2}{13:x2}{14:x2}{15:x2}",
                b[0], b[1], b[2], b[3], b[4], b[5], b[6], b[7], b[8], b[9], b[10], b[11], b[12], b[13], b[14], b[15]);
        }

        private static void WriteCustomLabelHint(byte[] buf, int offset, string label)
        {
            if (string.IsNullOrEmpty(label) || offset + 32 > buf.Length) return;
            Encoding.ASCII.GetBytes("WPL:").CopyTo(buf, offset);
            byte[] lblBytes = Encoding.UTF8.GetBytes(label);
            Array.Copy(lblBytes, 0, buf, offset + 4, Math.Min(lblBytes.Length, 27));
        }

        private static string ReadCustomLabelHint(byte[] buf, int offset)
        {
            if (offset + 32 > buf.Length) return string.Empty;
            if (!MatchesAscii(buf, offset, "WPL:")) return string.Empty;
            return Encoding.UTF8.GetString(buf, offset + 4, 27).TrimEnd('\0');
        }

        private static void WriteCustomUsedHint(byte[] buf, int offset, long usedBytes)
        {
            if (offset + 16 > buf.Length) return;
            Encoding.ASCII.GetBytes("WPU:").CopyTo(buf, offset);
            BitConverter.GetBytes(usedBytes).CopyTo(buf, offset + 4);
        }

        private static long ReadCustomUsedHint(byte[] buf, int offset)
        {
            if (offset + 16 > buf.Length) return -1;
            if (!MatchesAscii(buf, offset, "WPU:")) return -1;
            return BitConverter.ToInt64(buf, offset + 4);
        }
    }
}
