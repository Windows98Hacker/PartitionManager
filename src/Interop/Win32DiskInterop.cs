using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using WinParted.Models;

namespace WinParted.Interop
{
    public static class Win32DiskInterop
    {
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 0x00000001;
        public const uint FILE_SHARE_WRITE = 0x00000002;
        public const uint FILE_SHARE_DELETE = 0x00000004;
        public const uint OPEN_EXISTING = 3;
        public const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
        public const uint FILE_FLAG_WRITE_THROUGH = 0x80000000;

        public const uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = 0x000700A0;
        public const uint IOCTL_DISK_GET_DRIVE_LAYOUT_EX = 0x00070050;
        public const uint IOCTL_DISK_SET_DRIVE_LAYOUT_EX = 0x0007C054;
        public const uint IOCTL_DISK_GET_PARTITION_INFO_EX = 0x00070048;
        public const uint IOCTL_DISK_UPDATE_PROPERTIES = 0x00070140;
        public const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;
        public const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
        public const uint FSCTL_LOCK_VOLUME = 0x00090018;
        public const uint FSCTL_UNLOCK_VOLUME = 0x0009001C;
        public const uint FSCTL_DISMOUNT_VOLUME = 0x00090020;

        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            byte[] lpInBuffer,
            uint nInBufferSize,
            byte[] lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadFile(
            SafeFileHandle hFile,
            byte[] lpBuffer,
            uint nNumberOfBytesToRead,
            out uint lpNumberOfBytesRead,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteFile(
            SafeFileHandle hFile,
            byte[] lpBuffer,
            uint nNumberOfBytesToWrite,
            out uint lpNumberOfBytesWritten,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetFilePointerEx(
            SafeFileHandle hFile,
            long liDistanceToMove,
            out long lpNewFilePointer,
            uint dwMoveMethod);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool FlushFileBuffers(SafeFileHandle hFile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr FindFirstVolume(StringBuilder lpszVolumeName, uint cchBufferLength);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool FindNextVolume(IntPtr hFindVolume, StringBuilder lpszVolumeName, uint cchBufferLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool FindVolumeClose(IntPtr hFindVolume);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool GetVolumePathNamesForVolumeName(
            string lpszVolumeName,
            char[] lpszVolumePathNames,
            uint cchBufferLength,
            out uint lpcchReturnLength);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool GetVolumeInformation(
            string lpRootPathName,
            StringBuilder lpVolumeNameBuffer,
            int nVolumeNameSize,
            out uint lpVolumeSerialNumber,
            out uint lpMaximumComponentLength,
            out uint lpFileSystemFlags,
            StringBuilder lpFileSystemNameBuffer,
            int nFileSystemNameSize);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool GetDiskFreeSpaceEx(
            string lpDirectoryName,
            out ulong lpFreeBytesAvailable,
            out ulong lpTotalNumberOfBytes,
            out ulong lpTotalNumberOfFreeBytes);

        public static bool IsAdministrator()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    WindowsPrincipal principal = new WindowsPrincipal(identity);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }

        public static string GetArchitectureBadge()
        {
            string proc = IntPtr.Size == 8 ? "64-bit x64" : "32-bit x86";
            string os = Environment.Is64BitOperatingSystem ? "Win64" : "Win32";
            string priv = IsAdministrator() ? "Admin" : "Standard User";
            return string.Format("{0} ({1}) | {2}", proc, os, priv);
        }

        public static bool RelaunchAsAdministrator(string extraArgs = "")
        {
            try
            {
                string exePath = Process.GetCurrentProcess().MainModule.FileName;
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = extraArgs,
                    UseShellExecute = true,
                    Verb = "runas"
                };
                Process.Start(psi);
                return true;
            }
            catch (Win32Exception)
            {
                return false;
            }
            catch
            {
                return false;
            }
        }

        private class VolumeMapping
        {
            public int DiskNumber;
            public long StartOffset;
            public long ExtentLength;
            public string VolumeGuidPath = string.Empty;
            public string DriveLetter = string.Empty;
            public string Label = string.Empty;
            public string FileSystemName = string.Empty;
            public long? TotalBytes;
            public long? FreeBytes;
            public uint VolumeSerial;
        }

        public static List<DiskInfo> EnumeratePhysicalDisks()
        {
            var disksByNumber = new Dictionary<int, DiskInfo>();
            var volumeMappings = DiscoverVolumeMappings();

            // 1. Try direct PhysicalDrive0..15 handles (succeeds with full access when elevated, or metadata on permissive systems)
            for (int i = 0; i < 16; i++)
            {
                string path = string.Format(@"\\.\PhysicalDrive{0}", i);
                using (SafeFileHandle h = CreateFile(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
                {
                    if (!h.IsInvalid)
                    {
                        var disk = QueryDiskFromHandle(h, i, path, canRawRead: true);
                        if (disk != null)
                            disksByNumber[i] = disk;
                        continue;
                    }
                }

                // Try metadata-only handle (access = 0)
                using (SafeFileHandle hMeta = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
                {
                    if (!hMeta.IsInvalid)
                    {
                        var disk = QueryDiskFromHandle(hMeta, i, path, canRawRead: false);
                        if (disk != null)
                            disksByNumber[i] = disk;
                    }
                }
            }

            // 2. Fallback/Augment via Volume handles opened with dwDesiredAccess = 0 (works without Admin on Windows!)
            foreach (var vm in volumeMappings)
            {
                if (!disksByNumber.ContainsKey(vm.DiskNumber))
                {
                    string volTarget = !string.IsNullOrEmpty(vm.DriveLetter)
                        ? string.Format(@"\\.\{0}", vm.DriveLetter.TrimEnd('\\'))
                        : vm.VolumeGuidPath.TrimEnd('\\');

                    using (SafeFileHandle hVol = CreateFile(volTarget, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
                    {
                        if (!hVol.IsInvalid)
                        {
                            var disk = QueryDiskFromHandle(hVol, vm.DiskNumber, string.Format(@"\\.\PhysicalDrive{0}", vm.DiskNumber), canRawRead: false);
                            if (disk != null)
                                disksByNumber[vm.DiskNumber] = disk;
                        }
                    }
                }
            }

            // 3. Merge Windows Drive Letters, Volume Labels, Usage Stats, and System flags onto discovered partitions
            string systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";

            foreach (var disk in disksByNumber.Values)
            {
                foreach (var part in disk.Partitions)
                {
                    var match = volumeMappings.FirstOrDefault(v =>
                        v.DiskNumber == disk.DiskNumber &&
                        v.StartOffset == part.StartingOffset);

                    if (match != null)
                    {
                        part.VolumeGuidPath = match.VolumeGuidPath;
                        if (!string.IsNullOrEmpty(match.DriveLetter))
                        {
                            part.DriveLetter = match.DriveLetter;
                            if (string.Equals(match.DriveLetter.TrimEnd('\\') + "\\", systemDrive, StringComparison.OrdinalIgnoreCase))
                            {
                                part.IsSystemOrBoot = true;
                                disk.IsSystemDisk = true;
                            }
                        }
                        if (!string.IsNullOrEmpty(match.Label))
                            part.Label = match.Label;
                        if (!string.IsNullOrEmpty(match.FileSystemName))
                            part.FileSystem = ParseWindowsFsName(match.FileSystemName, part.FileSystem);
                        if (match.TotalBytes.HasValue && match.FreeBytes.HasValue)
                        {
                            part.FreeBytes = match.FreeBytes.Value;
                            part.UsedBytes = Math.Max(0, match.TotalBytes.Value - match.FreeBytes.Value);
                        }
                        if (match.VolumeSerial != 0 && string.IsNullOrEmpty(part.UUID))
                        {
                            part.UUID = string.Format("{0:X4}-{1:X4}", (match.VolumeSerial >> 16) & 0xFFFF, match.VolumeSerial & 0xFFFF);
                        }
                        part.IsMounted = !string.IsNullOrEmpty(match.DriveLetter) || !string.IsNullOrEmpty(match.VolumeGuidPath);
                    }
                }
            }

            return disksByNumber.Values.OrderBy(d => d.DiskNumber).ToList();
        }

        private static List<VolumeMapping> DiscoverVolumeMappings()
        {
            var list = new List<VolumeMapping>();
            var seenGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // First enumerate via FindFirstVolume/FindNextVolume
            StringBuilder volBuf = new StringBuilder(1024);
            IntPtr hFind = FindFirstVolume(volBuf, (uint)volBuf.Capacity);
            if (hFind != INVALID_HANDLE_VALUE && hFind != IntPtr.Zero)
            {
                try
                {
                    do
                    {
                        string volGuid = volBuf.ToString();
                        if (seenGuids.Add(volGuid))
                        {
                            var vm = ProbeVolume(volGuid);
                            if (vm != null) list.Add(vm);
                        }
                    } while (FindNextVolume(hFind, volBuf, (uint)volBuf.Capacity));
                }
                finally
                {
                    FindVolumeClose(hFind);
                }
            }

            // Also ensure all DriveInfo roots are checked
            try
            {
                foreach (var di in DriveInfo.GetDrives())
                {
                    string root = di.Name; // e.g., C:\
                    string letterOnly = root.TrimEnd('\\');
                    if (list.Any(x => string.Equals(x.DriveLetter, letterOnly, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    var vm = ProbeVolume(root);
                    if (vm != null)
                    {
                        vm.DriveLetter = letterOnly;
                        list.Add(vm);
                    }
                }
            }
            catch { }

            return list;
        }

        private static VolumeMapping ProbeVolume(string volumeRootWithSlash)
        {
            string openPath = volumeRootWithSlash.TrimEnd('\\');
            if (!openPath.StartsWith(@"\\", StringComparison.Ordinal))
                openPath = @"\\.\" + openPath;

            using (SafeFileHandle hVol = CreateFile(openPath, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
            {
                if (hVol.IsInvalid)
                    return null;

                byte[] extBuf = new byte[256];
                uint ret;
                if (!DeviceIoControl(hVol, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, null, 0, extBuf, (uint)extBuf.Length, out ret, IntPtr.Zero))
                    return null;

                if (ret < 32)
                    return null;

                int numExtents = BitConverter.ToInt32(extBuf, 0);
                if (numExtents <= 0)
                    return null;

                var vm = new VolumeMapping
                {
                    VolumeGuidPath = volumeRootWithSlash.StartsWith(@"\\?\", StringComparison.Ordinal) ? volumeRootWithSlash : string.Empty,
                    DiskNumber = BitConverter.ToInt32(extBuf, 8),
                    StartOffset = BitConverter.ToInt64(extBuf, 16),
                    ExtentLength = BitConverter.ToInt64(extBuf, 24)
                };

                // Get mount points / drive letter
                if (volumeRootWithSlash.StartsWith(@"\\?\", StringComparison.Ordinal))
                {
                    char[] paths = new char[1024];
                    uint len;
                    if (GetVolumePathNamesForVolumeName(volumeRootWithSlash, paths, (uint)paths.Length, out len) && len > 0)
                    {
                        string multi = new string(paths, 0, (int)len);
                        foreach (string p in multi.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (p.Length >= 2 && p[1] == ':')
                            {
                                vm.DriveLetter = p.Substring(0, 2);
                                break;
                            }
                        }
                    }
                }
                else if (volumeRootWithSlash.Length >= 2 && volumeRootWithSlash[1] == ':')
                {
                    vm.DriveLetter = volumeRootWithSlash.Substring(0, 2);
                }

                string queryRoot = !string.IsNullOrEmpty(vm.DriveLetter)
                    ? vm.DriveLetter + "\\"
                    : (volumeRootWithSlash.EndsWith("\\") ? volumeRootWithSlash : volumeRootWithSlash + "\\");

                StringBuilder labelSb = new StringBuilder(260);
                StringBuilder fsSb = new StringBuilder(64);
                uint serial, maxComp, flags;
                if (GetVolumeInformation(queryRoot, labelSb, labelSb.Capacity, out serial, out maxComp, out flags, fsSb, fsSb.Capacity))
                {
                    vm.Label = labelSb.ToString();
                    vm.FileSystemName = fsSb.ToString();
                    vm.VolumeSerial = serial;
                }

                ulong avail, total, free;
                if (GetDiskFreeSpaceEx(queryRoot, out avail, out total, out free))
                {
                    vm.TotalBytes = (long)total;
                    vm.FreeBytes = (long)free;
                }

                return vm;
            }
        }

        private static DiskInfo QueryDiskFromHandle(SafeFileHandle handle, int diskNumber, string devicePath, bool canRawRead)
        {
            byte[] geomBuf = new byte[256];
            uint ret;
            if (!DeviceIoControl(handle, IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, null, 0, geomBuf, (uint)geomBuf.Length, out ret, IntPtr.Zero))
                return null;

            if (ret < 32)
                return null;

            int bytesPerSector = BitConverter.ToInt32(geomBuf, 20);
            long diskSize = BitConverter.ToInt64(geomBuf, 24);
            if (bytesPerSector <= 0) bytesPerSector = 512;

            var disk = new DiskInfo
            {
                DiskNumber = diskNumber,
                DevicePath = devicePath,
                IsVirtualImage = false,
                TotalSizeBytes = diskSize,
                LogicalSectorSize = bytesPerSector,
                PhysicalSectorSize = bytesPerSector,
                CanRawRead = canRawRead
            };

            // Query StorageDeviceProperty (PropertyId = 0)
            byte[] propQuery = new byte[12];
            byte[] propOut = new byte[1024];
            if (DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, propQuery, 12, propOut, (uint)propOut.Length, out ret, IntPtr.Zero) && ret >= 36)
            {
                int vendorOff = BitConverter.ToInt32(propOut, 12);
                int prodOff = BitConverter.ToInt32(propOut, 16);
                int revOff = BitConverter.ToInt32(propOut, 20);
                int serOff = BitConverter.ToInt32(propOut, 24);
                byte busType = propOut[28];

                disk.Vendor = ReadAsciiZ(propOut, vendorOff);
                string product = ReadAsciiZ(propOut, prodOff);
                disk.Model = string.IsNullOrEmpty(disk.Vendor)
                    ? (string.IsNullOrEmpty(product) ? "Physical Drive " + diskNumber : product)
                    : (disk.Vendor + " " + product).Trim();
                disk.FirmwareRevision = ReadAsciiZ(propOut, revOff);
                disk.SerialNumber = ReadAsciiZ(propOut, serOff);
                disk.BusTypeName = MapBusType(busType);
            }

            // Query StorageAccessAlignmentProperty (PropertyId = 6)
            BitConverter.GetBytes(6).CopyTo(propQuery, 0);
            if (DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, propQuery, 12, propOut, (uint)propOut.Length, out ret, IntPtr.Zero) && ret >= 24)
            {
                int logSec = BitConverter.ToInt32(propOut, 16);
                int physSec = BitConverter.ToInt32(propOut, 20);
                if (logSec >= 512) disk.LogicalSectorSize = logSec;
                if (physSec >= disk.LogicalSectorSize) disk.PhysicalSectorSize = physSec;
            }

            // Query Drive Layout (IOCTL_DISK_GET_DRIVE_LAYOUT_EX)
            byte[] layoutBuf = new byte[32768];
            if (DeviceIoControl(handle, IOCTL_DISK_GET_DRIVE_LAYOUT_EX, null, 0, layoutBuf, (uint)layoutBuf.Length, out ret, IntPtr.Zero) && ret >= 48)
            {
                int style = BitConverter.ToInt32(layoutBuf, 0);
                int partCount = BitConverter.ToInt32(layoutBuf, 4);

                if (style == 0)
                {
                    disk.PartitionStyle = PartitionTableStyle.MBR;
                    disk.MbrSignature = BitConverter.ToUInt32(layoutBuf, 8);
                    disk.MaxPartitions = 4;
                }
                else if (style == 1)
                {
                    disk.PartitionStyle = PartitionTableStyle.GPT;
                    byte[] gBytes = new byte[16];
                    Array.Copy(layoutBuf, 8, gBytes, 0, 16);
                    disk.GptDiskGuid = new Guid(gBytes);
                    disk.MaxPartitions = BitConverter.ToInt32(layoutBuf, 40);
                    if (disk.MaxPartitions <= 0) disk.MaxPartitions = 128;
                }
                else
                {
                    disk.PartitionStyle = PartitionTableStyle.RAW;
                }

                for (int i = 0; i < partCount; i++)
                {
                    int off = 48 + i * 144;
                    if (off + 144 > ret) break;

                    int pStyle = BitConverter.ToInt32(layoutBuf, off);
                    long startOffset = BitConverter.ToInt64(layoutBuf, off + 8);
                    long partLen = BitConverter.ToInt64(layoutBuf, off + 16);
                    int pNum = BitConverter.ToInt32(layoutBuf, off + 24);

                    if (partLen <= 0 || pNum <= 0)
                        continue;

                    var entry = new PartitionEntry
                    {
                        DiskNumber = diskNumber,
                        PartitionNumber = pNum,
                        StartingOffset = startOffset,
                        PartitionLength = partLen,
                        LogicalSectorSize = disk.LogicalSectorSize,
                        PhysicalSectorSize = disk.PhysicalSectorSize
                    };

                    if (pStyle == 0)
                    {
                        byte mbrType = layoutBuf[off + 32];
                        bool boot = layoutBuf[off + 33] != 0;
                        if (mbrType == 0) continue;
                        entry.MbrPartitionType = mbrType;
                        entry.MbrBootable = boot;
                        entry.FileSystem = GuessFsFromMbrType(mbrType);
                        entry.FlagsSummary = boot ? "boot" : "";
                    }
                    else if (pStyle == 1)
                    {
                        byte[] typeBytes = new byte[16];
                        byte[] idBytes = new byte[16];
                        Array.Copy(layoutBuf, off + 32, typeBytes, 0, 16);
                        Array.Copy(layoutBuf, off + 48, idBytes, 0, 16);
                        ulong attrs = BitConverter.ToUInt64(layoutBuf, off + 64);
                        string name = Encoding.Unicode.GetString(layoutBuf, off + 72, 72).TrimEnd('\0');

                        entry.GptTypeGuid = new Guid(typeBytes);
                        entry.GptPartitionGuid = new Guid(idBytes);
                        entry.GptAttributes = attrs;
                        entry.PartitionName = name;
                        entry.FileSystem = GuessFsFromGptType(entry.GptTypeGuid);
                        entry.FlagsSummary = FormatGptFlags(entry.GptTypeGuid, attrs);

                        if (entry.GptTypeGuid == GptPartitionTypeGuids.EfiSystem ||
                            entry.GptTypeGuid == GptPartitionTypeGuids.MicrosoftReserved ||
                            entry.GptTypeGuid == GptPartitionTypeGuids.WindowsRecovery)
                        {
                            entry.IsSystemOrBoot = true;
                        }
                    }

                    disk.Partitions.Add(entry);
                }
            }

            return disk;
        }

        public static byte[] ReadRawDiskSectors(string devicePath, long byteOffset, int length)
        {
            using (SafeFileHandle h = CreateFile(devicePath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
            {
                if (h.IsInvalid)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot open " + devicePath + " for raw read.");

                long newPos;
                if (!SetFilePointerEx(h, byteOffset, out newPos, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Seek failed.");

                byte[] buf = new byte[length];
                uint read;
                if (!ReadFile(h, buf, (uint)length, out read, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "ReadFile failed.");

                if (read < length)
                {
                    byte[] trimmed = new byte[read];
                    Array.Copy(buf, trimmed, (int)read);
                    return trimmed;
                }
                return buf;
            }
        }

        public static FileSystemType ParseWindowsFsName(string fsName, FileSystemType fallback)
        {
            if (string.IsNullOrEmpty(fsName)) return fallback;
            string upper = fsName.Trim().ToUpperInvariant();
            if (upper == "NTFS") return fallback == FileSystemType.WindowsRecovery ? FileSystemType.WindowsRecovery : FileSystemType.NTFS;
            if (upper == "FAT32") return fallback == FileSystemType.EfiSystem ? FileSystemType.EfiSystem : FileSystemType.FAT32;
            if (upper == "FAT") return FileSystemType.FAT16;
            if (upper == "EXFAT") return FileSystemType.exFAT;
            if (upper == "REFS") return FileSystemType.ReFS;
            if (upper == "APFS") return FileSystemType.APFS;
            if (upper == "HFS+" || upper == "HFSPLUS" || upper == "HFSX") return FileSystemType.HFSPlus;
            if (upper == "HFS") return FileSystemType.HFS;
            return fallback;
        }

        public static FileSystemType GuessFsFromMbrType(byte mbrType)
        {
            switch (mbrType)
            {
                case 0x07: return FileSystemType.NTFS;
                case 0x0B:
                case 0x0C: return FileSystemType.FAT32;
                case 0x04:
                case 0x06:
                case 0x0E: return FileSystemType.FAT16;
                case 0x05:
                case 0x0F: return FileSystemType.Extended;
                case 0x82: return FileSystemType.LinuxSwap;
                case 0x83: return FileSystemType.Ext4;
                case 0x8E: return FileSystemType.LVM2;
                case 0xA8: return FileSystemType.HFS;
                case 0xAB:
                case 0xAF: return FileSystemType.HFSPlus;
                case 0xEF: return FileSystemType.EfiSystem;
                case 0x27: return FileSystemType.WindowsRecovery;
                default: return FileSystemType.RAW;
            }
        }

        public static FileSystemType GuessFsFromGptType(Guid typeGuid)
        {
            if (typeGuid == GptPartitionTypeGuids.EfiSystem) return FileSystemType.EfiSystem;
            if (typeGuid == GptPartitionTypeGuids.MicrosoftReserved) return FileSystemType.MicrosoftReserved;
            if (typeGuid == GptPartitionTypeGuids.WindowsRecovery) return FileSystemType.WindowsRecovery;
            if (typeGuid == GptPartitionTypeGuids.LinuxSwap) return FileSystemType.LinuxSwap;
            if (typeGuid == GptPartitionTypeGuids.LinuxLvm) return FileSystemType.LVM2;
            if (typeGuid == GptPartitionTypeGuids.LinuxFilesystem) return FileSystemType.Ext4;
            if (typeGuid == GptPartitionTypeGuids.AppleApfs) return FileSystemType.APFS;
            if (typeGuid == GptPartitionTypeGuids.AppleHfs ||
                typeGuid == GptPartitionTypeGuids.AppleBoot ||
                typeGuid == GptPartitionTypeGuids.AppleCoreStorage ||
                typeGuid == GptPartitionTypeGuids.AppleRaid) return FileSystemType.HFSPlus;
            if (typeGuid == GptPartitionTypeGuids.AppleUfs) return FileSystemType.HFS;
            return FileSystemType.RAW;
        }

        public static string FormatGptFlags(Guid typeGuid, ulong attrs)
        {
            var flags = new List<string>();
            if (typeGuid == GptPartitionTypeGuids.EfiSystem)
            {
                flags.Add("boot");
                flags.Add("esp");
            }
            if (typeGuid == GptPartitionTypeGuids.MicrosoftReserved)
                flags.Add("msftres");
            if (typeGuid == GptPartitionTypeGuids.WindowsRecovery)
                flags.Add("diag");
            if (typeGuid == GptPartitionTypeGuids.BasicData)
                flags.Add("msftdata");
            if (typeGuid == GptPartitionTypeGuids.AppleApfs)
                flags.Add("apfs");
            if (typeGuid == GptPartitionTypeGuids.AppleHfs || typeGuid == GptPartitionTypeGuids.AppleCoreStorage)
                flags.Add("apple_hfs");
            if (typeGuid == GptPartitionTypeGuids.AppleBoot)
                flags.Add("apple_boot");
            if ((attrs & 0x1UL) != 0) flags.Add("required");
            if ((attrs & 0x4UL) != 0) flags.Add("legacy_boot");
            if ((attrs & (1UL << 60)) != 0) flags.Add("readonly");
            if ((attrs & (1UL << 62)) != 0) flags.Add("hidden");
            if ((attrs & (1UL << 63)) != 0) flags.Add("no_automount");
            return string.Join(", ", flags);
        }

        private static string ReadAsciiZ(byte[] buf, int offset)
        {
            if (offset <= 0 || offset >= buf.Length) return string.Empty;
            int end = offset;
            while (end < buf.Length && buf[end] != 0) end++;
            return Encoding.ASCII.GetString(buf, offset, end - offset).Trim();
        }

        private static string MapBusType(byte bus)
        {
            switch (bus)
            {
                case 1: return "SCSI";
                case 2: return "ATAPI";
                case 3: return "ATA";
                case 7: return "USB";
                case 8: return "RAID";
                case 10: return "SAS";
                case 11: return "SATA";
                case 12: return "SD";
                case 13: return "MMC";
                case 14: return "Virtual";
                case 15: return "File-Backed Virtual";
                case 17: return "NVMe";
                default: return string.Format("BusType({0})", bus);
            }
        }
    }
}
