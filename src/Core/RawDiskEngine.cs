using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using WinParted.Interop;
using WinParted.Models;

namespace WinParted.Core
{
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

        public static uint Compute(byte[] data, int offset, int length)
        {
            uint crc = 0xFFFFFFFFu;
            for (int i = 0; i < length; i++)
                crc = Table[(crc ^ data[offset + i]) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }
    }

    public static class RawDiskEngine
    {
        private const ulong GPT_SIGNATURE = 0x5452415020494645UL; // "EFI PART"

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

            return Win32DiskInterop.ReadRawDiskSectors(disk.DevicePath, offset, length);
        }

        public static void WriteBytes(DiskInfo disk, long offset, byte[] data)
        {
            if (!disk.IsVirtualImage || string.IsNullOrEmpty(disk.BackingFilePath))
                throw new InvalidOperationException("Direct raw byte writing is restricted to Virtual Disk images unless executed through elevated disk operations.");

            using (var fs = new FileStream(disk.BackingFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                fs.Seek(offset, SeekOrigin.Begin);
                fs.Write(data, 0, data.Length);
                fs.Flush();
            }
        }

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

        public static DiskInfo CreateBlankVirtualDisk(string filePath, long sizeBytes, PartitionTableStyle style)
        {
            long alignedSize = Math.Max(4L * 1048576L, ByteFormatter.AlignUp(sizeBytes, 512));
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

        public static DiskInfo CreateDemoVirtualDisk(string filePath, int diskNumber = 90)
        {
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

        public static void WritePartitionTable(DiskInfo disk)
        {
            string valErr;
            if (!ValidatePartitionTable(disk, out valErr))
                throw new InvalidOperationException("Partition table safety check failed: " + valErr);

            int sec = disk.LogicalSectorSize > 0 ? disk.LogicalSectorSize : 512;
            long totalSectors = disk.TotalSizeBytes / sec;

            if (disk.PartitionStyle == PartitionTableStyle.MBR)
            {
                byte[] mbr = new byte[sec];
                uint sig = disk.MbrSignature != 0 ? disk.MbrSignature : 0x12345678u;
                BitConverter.GetBytes(sig).CopyTo(mbr, 440);

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

                WriteBytes(disk, 0, mbr);
                // Clear GPT LBA 1 signature if switching from GPT to MBR
                WriteBytes(disk, sec, new byte[sec]);
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

            WriteBytes(disk, 0, pmbr);
            WriteBytes(disk, sec, primaryHdr);
            WriteBytes(disk, 2L * sec, entries);
            if (backupEntriesLba > 34)
            {
                WriteBytes(disk, backupEntriesLba * sec, entries);
                WriteBytes(disk, backupHeaderLba * sec, backupHdr);
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

        public static void InspectPartitionSuperblock(DiskInfo disk, PartitionEntry part)
        {
            if (part.IsUnallocated || part.PartitionLength < 4096) return;

            int probeSize = (int)Math.Min(131072, part.PartitionLength);
            byte[] data;
            try
            {
                data = ReadBytes(disk, part.StartingOffset, probeSize);
            }
            catch
            {
                return;
            }
            if (data.Length < 512) return;

            // Check NTFS ("NTFS    " at +3)
            if (MatchesAscii(data, 3, "NTFS    "))
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

            // Check FAT16 ("FAT16   " at +0x36)
            if (data.Length >= 64 && MatchesAscii(data, 0x36, "FAT16   "))
            {
                part.FileSystem = FileSystemType.FAT16;
                string lbl = Encoding.ASCII.GetString(data, 0x2B, 11).Trim();
                if (!string.IsNullOrEmpty(lbl) && lbl != "NO NAME")
                    part.Label = lbl;
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

            // Check XFS ("XFSB" at +0)
            if (MatchesAscii(data, 0, "XFSB"))
            {
                part.FileSystem = FileSystemType.XFS;
                if (data.Length >= 120)
                {
                    uint blockSize = BitConverter.ToUInt32(data, 4);
                    if (blockSize == 0) blockSize = 4096;
                    ulong dblocks = BitConverter.ToUInt64(data, 8);
                    ulong fdblocks = BitConverter.ToUInt64(data, 16);
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

            // Check LVM2 ("LABELONE" at +512)
            if (data.Length >= 1024 && MatchesAscii(data, 512, "LABELONE"))
            {
                part.FileSystem = FileSystemType.LVM2;
                return;
            }

            // Check APFS ("NXSB" at +32)
            if (MatchesAscii(data, 32, "NXSB"))
            {
                part.FileSystem = FileSystemType.APFS;
                return;
            }

            // No recognized filesystem superblock found (e.g. unformatted or wiped)
            if (part.FileSystem != FileSystemType.MicrosoftReserved)
            {
                part.FileSystem = FileSystemType.RAW;
            }
        }

        public static void WriteFileSystemHeader(DiskInfo disk, PartitionEntry part)
        {
            if (part.IsUnallocated || part.PartitionLength < 65536) return;

            int headerClearLen = (int)Math.Min(131072, part.PartitionLength);
            byte[] buf = new byte[headerClearLen];
            long totalSectors = part.PartitionLength / 512;

            switch (part.FileSystem)
            {
                case FileSystemType.NTFS:
                case FileSystemType.WindowsRecovery:
                    buf[0] = 0xEB; buf[1] = 0x52; buf[2] = 0x90;
                    Encoding.ASCII.GetBytes("NTFS    ").CopyTo(buf, 3);
                    BitConverter.GetBytes((ushort)512).CopyTo(buf, 0x0B);
                    buf[0x0D] = 8; // 4K clusters
                    buf[0x15] = 0xF8;
                    BitConverter.GetBytes((ulong)totalSectors).CopyTo(buf, 0x28);
                    BitConverter.GetBytes((ulong)(totalSectors / 16)).CopyTo(buf, 0x30); // MFT LCN
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
                    buf[0] = 0xEB; buf[1] = 0x58; buf[2] = 0x90;
                    Encoding.ASCII.GetBytes("MSDOS5.0").CopyTo(buf, 3);
                    BitConverter.GetBytes((ushort)512).CopyTo(buf, 0x0B);
                    buf[0x0D] = 8;
                    BitConverter.GetBytes((ushort)32).CopyTo(buf, 0x0E);
                    buf[0x10] = 2;
                    buf[0x15] = 0xF8;
                    BitConverter.GetBytes((uint)totalSectors).CopyTo(buf, 0x20);
                    uint fatId = (uint)(0x46415400 ^ (int)part.StartingOffset);
                    BitConverter.GetBytes(fatId).CopyTo(buf, 0x43);
                    string fatLbl = (string.IsNullOrEmpty(part.Label) ? "NO NAME" : part.Label.ToUpperInvariant()).PadRight(11).Substring(0, 11);
                    Encoding.ASCII.GetBytes(fatLbl).CopyTo(buf, 0x47);
                    Encoding.ASCII.GetBytes("FAT32   ").CopyTo(buf, 0x52);
                    long fatUsed = part.UsedBytes ?? (part.FileSystem == FileSystemType.EfiSystem ? (long)(part.PartitionLength * 0.18) : (long)(part.PartitionLength * 0.10));
                    WriteCustomUsedHint(buf, 0x1C0, fatUsed);
                    buf[510] = 0x55; buf[511] = 0xAA;
                    part.UsedBytes = fatUsed;
                    part.FreeBytes = part.PartitionLength - fatUsed;
                    part.UUID = string.Format("{0:X4}-{1:X4}", (fatId >> 16) & 0xFFFF, fatId & 0xFFFF);
                    break;

                case FileSystemType.exFAT:
                    buf[0] = 0xEB; buf[1] = 0x76; buf[2] = 0x90;
                    Encoding.ASCII.GetBytes("EXFAT   ").CopyTo(buf, 3);
                    BitConverter.GetBytes((ulong)totalSectors).CopyTo(buf, 0x48);
                    uint exId = (uint)(0x45584600 ^ (int)part.StartingOffset);
                    BitConverter.GetBytes(exId).CopyTo(buf, 0x64);
                    WriteCustomLabelHint(buf, 0x1A0, part.Label);
                    long exUsed = part.UsedBytes ?? Math.Min(part.PartitionLength, 1048576L);
                    WriteCustomUsedHint(buf, 0x1C0, exUsed);
                    buf[510] = 0x55; buf[511] = 0xAA;
                    part.UUID = string.Format("{0:X4}-{1:X4}", (exId >> 16) & 0xFFFF, exId & 0xFFFF);
                    part.UsedBytes = exUsed;
                    part.FreeBytes = Math.Max(0, part.PartitionLength - exUsed);
                    break;

                case FileSystemType.Ext2:
                case FileSystemType.Ext3:
                case FileSystemType.Ext4:
                    uint blockSize = 4096;
                    uint blocksCount = (uint)Math.Max(1, part.PartitionLength / blockSize);
                    long extUsed = part.UsedBytes ?? (long)(part.PartitionLength * 0.30);
                    uint usedBlocks = (uint)(extUsed / blockSize);
                    uint freeBlocks = blocksCount > usedBlocks ? blocksCount - usedBlocks : 0;

                    BitConverter.GetBytes(blocksCount * 4).CopyTo(buf, 0x400);  // s_inodes_count
                    BitConverter.GetBytes(blocksCount).CopyTo(buf, 0x404);      // s_blocks_count_lo
                    BitConverter.GetBytes(freeBlocks).CopyTo(buf, 0x40C);       // s_free_blocks_count_lo
                    BitConverter.GetBytes(2u).CopyTo(buf, 0x418);               // s_log_block_size = 2 => 4096
                    BitConverter.GetBytes((ushort)0xEF53).CopyTo(buf, 0x438);   // s_magic
                    uint compat = (part.FileSystem == FileSystemType.Ext3 || part.FileSystem == FileSystemType.Ext4) ? 0x0004u : 0x0000u;
                    uint incompat = (part.FileSystem == FileSystemType.Ext4) ? 0x0242u : 0x0002u;
                    BitConverter.GetBytes(compat).CopyTo(buf, 0x45C);
                    BitConverter.GetBytes(incompat).CopyTo(buf, 0x460);

                    Guid extUuid = Guid.NewGuid();
                    extUuid.ToByteArray().CopyTo(buf, 0x468);
                    string extLbl = part.Label ?? string.Empty;
                    if (extLbl.Length > 16) extLbl = extLbl.Substring(0, 16);
                    Encoding.UTF8.GetBytes(extLbl).CopyTo(buf, 0x478);

                    part.UUID = extUuid.ToString("D");
                    part.UsedBytes = (blocksCount - freeBlocks) * (long)blockSize;
                    part.FreeBytes = freeBlocks * (long)blockSize;
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
                    if (buf.Length >= 0x10200)
                    {
                        Encoding.ASCII.GetBytes("_BHRfS_M").CopyTo(buf, 0x10040);
                        Guid bUuid = Guid.NewGuid();
                        bUuid.ToByteArray().CopyTo(buf, 0x10020);
                        long bUsed = part.UsedBytes ?? Math.Min(part.PartitionLength / 4, 4L * 1048576L);
                        BitConverter.GetBytes(part.PartitionLength).CopyTo(buf, 0x10070);
                        BitConverter.GetBytes(bUsed).CopyTo(buf, 0x10078);
                        string bLbl = part.Label ?? string.Empty;
                        if (bLbl.Length > 64) bLbl = bLbl.Substring(0, 64);
                        Encoding.UTF8.GetBytes(bLbl).CopyTo(buf, 0x1012B);
                        part.UUID = bUuid.ToString("D");
                        part.UsedBytes = bUsed;
                        part.FreeBytes = Math.Max(0, part.PartitionLength - bUsed);
                    }
                    break;

                case FileSystemType.XFS:
                    uint xfsBlockSize = 4096;
                    ulong xfsTotalBlocks = (ulong)(part.PartitionLength / xfsBlockSize);
                    long xUsed = part.UsedBytes ?? Math.Min(part.PartitionLength / 4, 4L * 1048576L);
                    ulong xfsUsedBlocks = (ulong)(xUsed / xfsBlockSize);
                    ulong xfsFreeBlocks = xfsTotalBlocks > xfsUsedBlocks ? xfsTotalBlocks - xfsUsedBlocks : 0;

                    Encoding.ASCII.GetBytes("XFSB").CopyTo(buf, 0);
                    BitConverter.GetBytes(xfsBlockSize).CopyTo(buf, 4);
                    BitConverter.GetBytes(xfsTotalBlocks).CopyTo(buf, 8);
                    BitConverter.GetBytes(xfsFreeBlocks).CopyTo(buf, 16);
                    Guid xUuid = Guid.NewGuid();
                    xUuid.ToByteArray().CopyTo(buf, 32);
                    string xLbl = part.Label ?? string.Empty;
                    if (xLbl.Length > 12) xLbl = xLbl.Substring(0, 12);
                    Encoding.UTF8.GetBytes(xLbl).CopyTo(buf, 108);
                    part.UUID = xUuid.ToString("D");
                    part.UsedBytes = (long)(xfsUsedBlocks * xfsBlockSize);
                    part.FreeBytes = (long)(xfsFreeBlocks * xfsBlockSize);
                    break;

                case FileSystemType.RAW:
                    // buf is zeroed, clearing any stale filesystem signatures
                    part.UUID = string.Empty;
                    part.UsedBytes = null;
                    part.FreeBytes = null;
                    break;
            }

            WriteBytes(disk, part.StartingOffset, buf);
        }

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
                BitConverter.GetBytes((ulong)totalSectors).CopyTo(buf, 0x48);
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

                BitConverter.GetBytes(newBlocks * 4).CopyTo(buf, 0x400);
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
                uint blockSize = BitConverter.ToUInt32(buf, 4);
                if (blockSize == 0) blockSize = 4096;
                ulong oldDblocks = BitConverter.ToUInt64(buf, 8);
                ulong oldFdblocks = BitConverter.ToUInt64(buf, 16);
                ulong usedBlocks = oldDblocks > oldFdblocks ? oldDblocks - oldFdblocks : 0;

                ulong newDblocks = (ulong)(part.PartitionLength / blockSize);
                if (usedBlocks > newDblocks) usedBlocks = newDblocks;
                ulong newFdblocks = newDblocks - usedBlocks;

                BitConverter.GetBytes(newDblocks).CopyTo(buf, 8);
                BitConverter.GetBytes(newFdblocks).CopyTo(buf, 16);
                part.UsedBytes = (long)(usedBlocks * blockSize);
                part.FreeBytes = (long)(newFdblocks * blockSize);
                WriteBytes(disk, part.StartingOffset, buf);
                return;
            }
        }

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
        }

        public static void CopyPartitionBytes(DiskInfo sourceDisk, long sourceOffset, DiskInfo destDisk, long destOffset, long length)
        {
            if (!destDisk.IsVirtualImage)
                throw new InvalidOperationException("Direct partition byte copy requires a virtual disk target unless running elevated raw disk mode.");

            const int chunkSize = 1048576; // 1 MiB
            long copied = 0;

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
                }
                destFs.Flush();
            }
        }

        public static void MovePartitionBytes(DiskInfo disk, long oldOffset, long newOffset, long length)
        {
            if (oldOffset == newOffset || length <= 0) return;
            if (!disk.IsVirtualImage)
                throw new InvalidOperationException("Moving partition data on live hardware requires unmounted/offline disk locking.");

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
        }

        public static void WipeRange(DiskInfo disk, long startOffset, long length, WipePattern pattern)
        {
            if (!disk.IsVirtualImage)
                throw new InvalidOperationException("Direct wiping is enabled for Virtual Disk images.");

            const int chunkSize = 1048576;
            byte[] buf = new byte[chunkSize];
            var rng = new Random();

            if (pattern == WipePattern.OnesFill)
            {
                for (int i = 0; i < buf.Length; i++) buf[i] = 0xFF;
            }

            using (var fs = new FileStream(disk.BackingFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                long written = 0;
                while (written < length)
                {
                    int n = (int)Math.Min(chunkSize, length - written);
                    if (pattern == WipePattern.RandomDoD)
                        rng.NextBytes(buf);

                    fs.Seek(startOffset + written, SeekOrigin.Begin);
                    fs.Write(buf, 0, n);
                    written += n;
                }
                fs.Flush();
            }
        }

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

        private static byte GetDefaultMbrType(FileSystemType fs)
        {
            switch (fs)
            {
                case FileSystemType.FAT32:
                case FileSystemType.EfiSystem:
                    return 0x0C;
                case FileSystemType.FAT16:
                    return 0x0E;
                case FileSystemType.LinuxSwap:
                    return 0x82;
                case FileSystemType.Ext2:
                case FileSystemType.Ext3:
                case FileSystemType.Ext4:
                case FileSystemType.Btrfs:
                case FileSystemType.XFS:
                    return 0x83;
                case FileSystemType.LVM2:
                    return 0x8E;
                default:
                    return 0x07;
            }
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
