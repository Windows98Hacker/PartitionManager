using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Text;
using WinParted.Models;

namespace WinParted.Core
{
    /// <summary>
    /// In-memory representation of a file or directory node inside a mounted foreign filesystem,
    /// supporting single-extent (<see cref="ContentOffset"/>), multi-extent (<see cref="ContentExtents"/>),
    /// and inline small-file payloads (<see cref="InlineContent"/>).
    /// </summary>
    public class VirtualFsNode
    {
        public VirtualFsNode()
        {
            Children = new List<VirtualFsNode>();
            PermissionsString = "r--r--r--";
            ModifiedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        }

        public string Name { get; set; }
        public string FullPath { get; set; }
        public bool IsDirectory { get; set; }
        public long SizeBytes { get; set; }
        public ulong InodeOrObjectId { get; set; }
        public string PermissionsString { get; set; }
        public DateTime ModifiedUtc { get; set; }
        public long ContentOffset { get; set; }
        public long ContentLength { get; set; }
        public byte[] InlineContent { get; set; }
        public List<KeyValuePair<long, long>> ContentExtents { get; set; }
        public List<VirtualFsNode> Children { get; set; }
        internal string HostShadowFilePath { get; set; }
        internal long HostShadowOffset { get; set; }
    }

    /// <summary>
    /// State and parsed directory tree for a mounted non-Microsoft (or ReFS) partition,
    /// plus optional persistent experimental write-overlay state (<c>WPRWOVL1</c>).
    /// </summary>
    public class MountedReadOnlyVolume
    {
        public string MountId { get; set; }
        public string MountPath { get; set; }
        public int DiskNumber { get; set; }
        public int PartitionNumber { get; set; }
        public long StartingOffset { get; set; }
        public long PartitionLength { get; set; }
        public FileSystemType FileSystem { get; set; }
        public string Label { get; set; }
        public string UUID { get; set; }
        public long BlockSize { get; set; }
        public long TotalBlocks { get; set; }
        public long FreeBlocks { get; set; }
        public string SuperblockSummary { get; set; }
        public bool ExperimentalWriteEnabled { get; set; }
        public bool IsReadOnly { get { return !ExperimentalWriteEnabled; } }
        public VirtualFsNode RootNode { get; set; }
        public bool OverlayUsingHostShadow { get; set; }
        public string OverlayShadowMessage { get; set; }
        internal long OverlayTableRelOffset { get; set; }
        internal long OverlayNextDataRelOffset { get; set; }
        internal bool OverlayDiskOffsetCommitted { get; set; }
        internal List<OverlayRecord> OverlayRecords { get; set; }
    }

    /// <summary>
    /// Single append/delete entry stored inside the <c>WPRWOVL1</c> experimental write overlay table.
    /// </summary>
    internal class OverlayRecord
    {
        public byte Op { get; set; } // 1 = upsert, 2 = delete
        public bool IsDirectory { get; set; }
        public long RelDataOffset { get; set; }
        public long DataLength { get; set; }
        public long ModifiedUtcTicks { get; set; }
        public string FullPath { get; set; }
    }

    /// <summary>
    /// Native userspace filesystem reader, streaming exporter (4 MiB chunks), search indexer,
    /// and opt-in experimental write overlay engine for foreign and developer filesystems.
    /// </summary>
    public static class ReadOnlyFileSystemMountEngine
    {
        private static readonly object SyncLock = new object();
        private static readonly Dictionary<string, MountedReadOnlyVolume> ActiveMounts = new Dictionary<string, MountedReadOnlyVolume>();

        /// <summary>
        /// Returns true if the given filesystem can be opened in WinParted's integrated file explorer.
        /// </summary>
        public static bool IsSupportedNonMicrosoftFs(FileSystemType fs)
        {
            switch (fs)
            {
                case FileSystemType.Ext2:
                case FileSystemType.Ext3:
                case FileSystemType.Ext4:
                case FileSystemType.Btrfs:
                case FileSystemType.XFS:
                case FileSystemType.F2FS:
                case FileSystemType.ZFS:
                case FileSystemType.SquashFS:
                case FileSystemType.ISO9660:
                case FileSystemType.UDF:
                case FileSystemType.ReFS:
                case FileSystemType.APFS:
                case FileSystemType.HFSPlus:
                case FileSystemType.HFS:
                case FileSystemType.LinuxSwap:
                case FileSystemType.LVM2:
                    return true;
                default:
                    return false;
            }
        }

        private static string MakeMountKey(int diskNumber, long startingOffset)
        {
            return diskNumber + ":" + startingOffset;
        }

        public static MountedReadOnlyVolume GetMount(int diskNumber, long startingOffset)
        {
            lock (SyncLock)
            {
                MountedReadOnlyVolume vol;
                if (ActiveMounts.TryGetValue(MakeMountKey(diskNumber, startingOffset), out vol))
                    return vol;
                return null;
            }
        }

        public static bool Unmount(int diskNumber, long startingOffset)
        {
            lock (SyncLock)
            {
                return ActiveMounts.Remove(MakeMountKey(diskNumber, startingOffset));
            }
        }

        /// <summary>
        /// Unmounts and evicts all cached read-only volumes belonging to the specified disk number.
        /// </summary>
        public static int UnmountDisk(int diskNumber)
        {
            string prefix = diskNumber + ":";
            lock (SyncLock)
            {
                var keys = new List<string>();
                foreach (var k in ActiveMounts.Keys)
                {
                    if (k.StartsWith(prefix, StringComparison.Ordinal))
                        keys.Add(k);
                }
                for (int i = 0; i < keys.Count; i++)
                    ActiveMounts.Remove(keys[i]);
                return keys.Count;
            }
        }

        public static void UnmountAll()
        {
            lock (SyncLock)
            {
                ActiveMounts.Clear();
            }
        }

        private static bool IsSameFsFamily(FileSystemType a, FileSystemType b)
        {
            if (a == b) return true;
            bool aExt = (a == FileSystemType.Ext2 || a == FileSystemType.Ext3 || a == FileSystemType.Ext4);
            bool bExt = (b == FileSystemType.Ext2 || b == FileSystemType.Ext3 || b == FileSystemType.Ext4);
            if (aExt && bExt) return true;
            bool aHfs = (a == FileSystemType.HFS || a == FileSystemType.HFSPlus);
            bool bHfs = (b == FileSystemType.HFS || b == FileSystemType.HFSPlus);
            if (aHfs && bHfs) return true;
            bool aOpt = (a == FileSystemType.ISO9660 || a == FileSystemType.UDF);
            bool bOpt = (b == FileSystemType.ISO9660 || b == FileSystemType.UDF);
            if (aOpt && bOpt) return true;
            return false;
        }

        /// <summary>
        /// Verifies that the partition currently holds a valid on-disk superblock signature matching its metadata
        /// (preventing wiped or reformatted volumes from falling back to permissive directory trees or overlays).
        /// </summary>
        public static bool HasValidSuperblockSignature(DiskInfo disk, PartitionEntry part)
        {
            if (disk == null || part == null || part.IsUnallocated || part.PartitionLength < 1024)
                return false;

            int headLen = (int)Math.Min(16384, part.PartitionLength);
            byte[] head = RawDiskEngine.ReadBytes(disk, part.StartingOffset, headLen);
            if (head == null || head.Length < headLen)
                return false;

            if ((head.Length >= 8 && Encoding.ASCII.GetString(head, 0, 8) == "WPROFS01") ||
                (head.Length >= 0x3008 && Encoding.ASCII.GetString(head, 0x3000, 8) == "WPROFS01"))
                return true;

            var probe = new PartitionEntry
            {
                PartitionNumber = part.PartitionNumber,
                StartingOffset = part.StartingOffset,
                PartitionLength = part.PartitionLength,
                FileSystem = FileSystemType.RAW
            };
            bool prevCanRaw = disk.CanRawRead;
            disk.CanRawRead = true;
            try
            {
                RawDiskEngine.InspectPartitionSuperblock(disk, probe);
            }
            finally
            {
                disk.CanRawRead = prevCanRaw;
            }
            if (probe.FileSystem == FileSystemType.RAW || probe.FileSystem == FileSystemType.Unallocated)
                return false;

            return IsSameFsFamily(probe.FileSystem, part.FileSystem);
        }

        private static string GetDefaultShadowOverlayDirectory()
        {
            if (!string.IsNullOrEmpty(ShadowOverlayRootOverride))
                return ShadowOverlayRootOverride;
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinParted",
                "ExperimentalOverlays");
        }

        private static void DeleteShadowOverlayFilesInRange(int diskNumber, long rangeStart, long rangeEnd)
        {
            string prefix = "ovl_d" + diskNumber + "_";
            var dirs = new List<string>();
            if (!string.IsNullOrEmpty(ShadowOverlayRootOverride))
                dirs.Add(ShadowOverlayRootOverride);
            string appDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinParted",
                "ExperimentalOverlays");
            if (!dirs.Contains(appDataDir))
                dirs.Add(appDataDir);

            for (int d = 0; d < dirs.Count; d++)
            {
                string dir = dirs[d];
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                string[] files = Directory.GetFiles(dir, prefix + "*.bin");
                for (int i = 0; i < files.Length; i++)
                {
                    string name = Path.GetFileName(files[i]);
                    if (rangeEnd == long.MaxValue)
                    {
                        File.Delete(files[i]);
                        continue;
                    }
                    int startIdx = prefix.Length;
                    int underIdx = name.IndexOf('_', startIdx);
                    if (underIdx <= startIdx) continue;
                    long fileOff;
                    if (long.TryParse(name.Substring(startIdx, underIdx - startIdx), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out fileOff))
                    {
                        if (fileOff >= rangeStart && fileOff < rangeEnd)
                            File.Delete(files[i]);
                    }
                }
            }
        }

        private static bool RangeOverlapsOtherPartition(DiskInfo disk, long targetStart, long absStart, long absEnd)
        {
            if (disk == null || disk.Partitions == null) return false;
            for (int i = 0; i < disk.Partitions.Count; i++)
            {
                var other = disk.Partitions[i];
                if (other == null || other.IsUnallocated || other.PartitionLength <= 0) continue;
                if (other.StartingOffset == targetStart) continue;
                long oStart = other.StartingOffset;
                long oEnd = other.StartingOffset + other.PartitionLength;
                if (absStart < oEnd && absEnd > oStart)
                    return true;
            }
            return false;
        }

        private static void ZeroDiskRangeChunked(DiskInfo disk, long offset, long length)
        {
            if (disk == null || length <= 0) return;
            const int chunkSize = 1048576;
            byte[] zeros = new byte[(int)Math.Min(chunkSize, length)];
            long written = 0;
            while (written < length)
            {
                int n = (int)Math.Min(zeros.Length, length - written);
                if (n == zeros.Length)
                    RawDiskEngine.WriteBytes(disk, offset + written, zeros);
                else
                {
                    byte[] tail = new byte[n];
                    RawDiskEngine.WriteBytes(disk, offset + written, tail);
                }
                written += n;
            }
        }

        /// <summary>
        /// Evicts in-memory mount cache entries, removes persistent host shadow overlay files,
        /// and optionally zeros on-disk <c>WPRWOVL1</c> overlay tables and data for a partition range.
        /// </summary>
        public static void PurgePartitionOverlaysAndCache(DiskInfo disk, long startingOffset, long partitionLength, bool wipeOnDiskOverlays)
        {
            if (disk == null) return;

            Unmount(disk.DiskNumber, startingOffset);

            // Also evict any cached mounts falling inside a wiped or replaced range.
            if (partitionLength > 0)
            {
                string prefix = disk.DiskNumber + ":";
                long rangeEnd = startingOffset + partitionLength;
                lock (SyncLock)
                {
                    var toRemove = new List<string>();
                    foreach (var k in ActiveMounts.Keys)
                    {
                        if (!k.StartsWith(prefix, StringComparison.Ordinal)) continue;
                        long keyOff;
                        if (long.TryParse(k.Substring(prefix.Length), out keyOff))
                        {
                            if (keyOff >= startingOffset && keyOff < rangeEnd)
                                toRemove.Add(k);
                        }
                    }
                    for (int i = 0; i < toRemove.Count; i++)
                        ActiveMounts.Remove(toRemove[i]);
                }
            }

            DeleteShadowOverlayFilesInRange(disk.DiskNumber, startingOffset, startingOffset + Math.Max(1L, partitionLength));

            if (!wipeOnDiskOverlays || partitionLength < 65536) return;

            var candidates = new List<long>();
            if (partitionLength >= 0x24000L)
                candidates.Add(0x20000L);
            if (partitionLength > 8388608L + 0x4000L)
                candidates.Add(((partitionLength - 8388608L) / 4096L) * 4096L);
            if (partitionLength > 524288L + 0x4000L)
                candidates.Add(((partitionLength - 524288L) / 4096L) * 4096L);

            for (int i = 0; i < candidates.Count; i++)
            {
                long cand = candidates[i];
                if (cand < 0 || cand + 0x4000L > partitionLength) continue;
                long absStart = startingOffset + cand;
                if (RangeOverlapsOtherPartition(disk, startingOffset, absStart, absStart + 0x4000L))
                    continue;

                byte[] hdr = RawDiskEngine.ReadBytes(disk, absStart, 24);
                long zeroLen = 0;
                if (hdr != null && hdr.Length >= 20 && Encoding.ASCII.GetString(hdr, 0, 8) == "WPRWOVL1")
                {
                    ulong nextData = BitConverter.ToUInt64(hdr, 8);
                    long minClear = Math.Min(partitionLength - cand, 0x10000L);
                    zeroLen = minClear;
                    if (nextData > (ulong)(cand + 0x4000L) && nextData <= (ulong)partitionLength)
                    {
                        long dataSpan = (long)nextData - cand;
                        if (dataSpan > zeroLen)
                            zeroLen = Math.Min(dataSpan, 64L * 1048576L);
                    }
                }
                else if (cand == 0x20000L)
                {
                    // Always clear the primary WPRWOVL1 slot during format even if header was partially overwritten.
                    zeroLen = Math.Min(partitionLength - cand, 0x4000L);
                }

                if (zeroLen > 0)
                {
                    if (RangeOverlapsOtherPartition(disk, startingOffset, absStart, absStart + zeroLen))
                        zeroLen = Math.Min(zeroLen, 0x4000L);
                    if (!RangeOverlapsOtherPartition(disk, startingOffset, absStart, absStart + zeroLen))
                        ZeroDiskRangeChunked(disk, absStart, zeroLen);
                }
            }
        }

        /// <summary>
        /// Evicts all cached mounts and deletes all host shadow overlay files associated with a disk number.
        /// </summary>
        public static void PurgeDiskOverlaysAndCache(int diskNumber)
        {
            UnmountDisk(diskNumber);
            DeleteShadowOverlayFilesInRange(diskNumber, 0, long.MaxValue);
        }

        /// <summary>
        /// Mounts a partition read-only in userspace by dispatching to the native filesystem parser
        /// (Ext2/3/4, HFS+, Btrfs, XFS, ISO9660, or WPROFS01/superblock fallback) and merging any <c>WPRWOVL1</c> overlay.
        /// </summary>
        public static MountedReadOnlyVolume MountReadOnly(DiskInfo disk, PartitionEntry part, bool requireVirtualDisk = false)
        {
            if (disk == null) throw new ArgumentNullException("disk");
            if (part == null) throw new ArgumentNullException("part");
            if (part.IsUnallocated)
                throw new InvalidOperationException("Cannot mount unallocated space.");
            if (requireVirtualDisk && !disk.IsVirtualImage)
                throw new InvalidOperationException("Test safety restriction: Read-only mount tests are restricted to virtual disk images.");
            if (!IsSupportedNonMicrosoftFs(part.FileSystem))
                throw new NotSupportedException("Read-only explorer mount is intended for supported volumes (ext2/3/4, Btrfs, XFS, F2FS, ZFS, SquashFS, ISO9660, UDF, APFS, HFS+, HFS, ReFS, Swap/LVM2). Selected: " + FileSystemColors.GetDisplayName(part.FileSystem));
            if (!HasValidSuperblockSignature(disk, part))
            {
                Unmount(disk.DiskNumber, part.StartingOffset);
                throw new InvalidOperationException(
                    "No valid " + FileSystemColors.GetDisplayName(part.FileSystem) +
                    " superblock signature was found on disk (the volume may have been wiped, reformatted, or corrupted).");
            }

            string mountId = string.Format("ro_disk{0}_p{1}", disk.DiskNumber, part.PartitionNumber);
            string mountPath = string.Format("/mnt/ro/disk{0}p{1}", disk.DiskNumber, part.PartitionNumber);

            long[] candidates = GetCandidateOverlayTableRelOffsets(disk, part);
            long initialTableRel = candidates.Length > 0 ? candidates[0] : 0x20000L;

            var vol = new MountedReadOnlyVolume
            {
                MountId = mountId,
                MountPath = mountPath,
                DiskNumber = disk.DiskNumber,
                PartitionNumber = part.PartitionNumber,
                StartingOffset = part.StartingOffset,
                PartitionLength = part.PartitionLength,
                FileSystem = part.FileSystem,
                Label = part.Label ?? string.Empty,
                UUID = part.UUID ?? string.Empty,
                BlockSize = 4096,
                OverlayTableRelOffset = initialTableRel,
                OverlayNextDataRelOffset = initialTableRel + 0x4000L,
                OverlayRecords = new List<OverlayRecord>()
            };

            if (part.FileSystem == FileSystemType.Ext2 ||
                part.FileSystem == FileSystemType.Ext3 ||
                part.FileSystem == FileSystemType.Ext4)
            {
                ParseExtVolume(disk, part, vol);
            }
            else if (part.FileSystem == FileSystemType.HFSPlus)
            {
                ParseHfsPlusVolume(disk, part, vol);
            }
            else if (part.FileSystem == FileSystemType.APFS)
            {
                ParseApfsVolume(disk, part, vol);
            }
            else if (part.FileSystem == FileSystemType.Btrfs)
            {
                ParseBtrfsVolume(disk, part, vol);
            }
            else if (part.FileSystem == FileSystemType.XFS)
            {
                ParseXfsVolume(disk, part, vol);
            }
            else if (part.FileSystem == FileSystemType.ISO9660)
            {
                ParseIso9660Volume(disk, part, vol);
            }
            else
            {
                ParseCatalogOrMetadataVolume(disk, part, vol);
            }

            LoadAndMergeExperimentalOverlay(disk, part, vol);

            part.IsMounted = true;
            lock (SyncLock)
            {
                ActiveMounts[MakeMountKey(disk.DiskNumber, part.StartingOffset)] = vol;
            }
            return vol;
        }

        private const long CopyStreamChunkBytes = 4L * 1048576L; // 4 MiB chunked transfer for multi-GB exports

        /// <summary>
        /// Reads up to <paramref name="maxBytes"/> from a virtual file node for preview rendering.
        /// </summary>
        public static byte[] ReadFileBytes(DiskInfo disk, PartitionEntry part, VirtualFsNode fileNode, int maxBytes = 262144)
        {
            if (fileNode == null || fileNode.IsDirectory || maxBytes <= 0)
                return new byte[0];

            using (var ms = new MemoryStream())
            {
                CopyFileNodeToStream(disk, part, fileNode, ms, maxBytes, null);
                return ms.ToArray();
            }
        }

        /// <summary>
        /// Streams a file node's payload in 4 MiB chunks across inline data, multi-extent lists,
        /// or a contiguous disk offset so multi-gigabyte files export safely with constant RAM usage.
        /// </summary>
        public static long CopyFileNodeToStream(
            DiskInfo disk,
            PartitionEntry part,
            VirtualFsNode fileNode,
            Stream destination,
            long maxBytesToCopy = long.MaxValue,
            Action<long, long> onProgress = null)
        {
            if (fileNode == null || fileNode.IsDirectory || destination == null || maxBytesToCopy <= 0)
                return 0;

            if (fileNode.InlineContent != null)
            {
                int toWrite = (int)Math.Min((long)fileNode.InlineContent.Length, maxBytesToCopy);
                if (toWrite > 0)
                {
                    destination.Write(fileNode.InlineContent, 0, toWrite);
                    if (onProgress != null) onProgress(toWrite, toWrite);
                }
                return toWrite;
            }

            if (!string.IsNullOrEmpty(fileNode.HostShadowFilePath) && fileNode.ContentLength > 0 && File.Exists(fileNode.HostShadowFilePath))
            {
                using (var sfs = new FileStream(fileNode.HostShadowFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (fileNode.HostShadowOffset >= 0 && fileNode.HostShadowOffset < sfs.Length)
                    {
                        long avail = sfs.Length - fileNode.HostShadowOffset;
                        long shadowTotal = Math.Min(Math.Min(fileNode.ContentLength, maxBytesToCopy), avail);
                        long shadowWritten = 0;
                        sfs.Seek(fileNode.HostShadowOffset, SeekOrigin.Begin);
                        byte[] buf = new byte[(int)Math.Min(CopyStreamChunkBytes, Math.Max(4096L, shadowTotal))];

                        while (shadowWritten < shadowTotal)
                        {
                            int toRead = (int)Math.Min((long)buf.Length, shadowTotal - shadowWritten);
                            int actual = sfs.Read(buf, 0, toRead);
                            if (actual <= 0) break;
                            destination.Write(buf, 0, actual);
                            shadowWritten += actual;
                            if (onProgress != null) onProgress(shadowWritten, shadowTotal);
                        }
                        return shadowWritten;
                    }
                }
            }

            if (fileNode.ContentExtents != null && fileNode.ContentExtents.Count > 0 && fileNode.ContentLength > 0)
            {
                long targetTotal = Math.Min(fileNode.ContentLength, maxBytesToCopy);
                long written = 0;

                for (int i = 0; i < fileNode.ContentExtents.Count && written < targetTotal; i++)
                {
                    long extOff = fileNode.ContentExtents[i].Key;
                    long extLen = fileNode.ContentExtents[i].Value;
                    if (extOff == -1L && extLen > 0)
                    {
                        long zeroRem = Math.Min(targetTotal - written, extLen);
                        if (zeroRem > 0)
                        {
                            byte[] zeroBuf = new byte[(int)Math.Min(65536L, zeroRem)];
                            while (zeroRem > 0)
                            {
                                int zChunk = (int)Math.Min((long)zeroBuf.Length, zeroRem);
                                destination.Write(zeroBuf, 0, zChunk);
                                written += zChunk;
                                zeroRem -= zChunk;
                                if (onProgress != null) onProgress(written, targetTotal);
                            }
                        }
                        continue;
                    }
                    if (extOff < part.StartingOffset || extOff >= part.EndOffset || extLen <= 0) continue;

                    long extAvail = Math.Min(extLen, part.EndOffset - extOff);
                    long remInExt = Math.Min(targetTotal - written, extAvail);
                    long cursor = extOff;

                    while (remInExt > 0)
                    {
                        int chunkLen = (int)Math.Min(CopyStreamChunkBytes, remInExt);
                        byte[] chunk = RawDiskEngine.ReadBytes(disk, cursor, chunkLen);
                        int actual = Math.Min(chunkLen, chunk.Length);
                        if (actual <= 0) return written;

                        destination.Write(chunk, 0, actual);
                        written += actual;
                        cursor += actual;
                        remInExt -= actual;

                        if (onProgress != null) onProgress(written, targetTotal);
                        if (actual < chunkLen) return written;
                    }
                }
                return written;
            }

            if (fileNode.ContentLength <= 0 || fileNode.ContentOffset < part.StartingOffset || fileNode.ContentOffset >= part.EndOffset)
                return 0;

            long partAvail = part.EndOffset - fileNode.ContentOffset;
            long contiguousTotal = Math.Min(Math.Min(fileNode.ContentLength, maxBytesToCopy), partAvail);
            long totalWritten = 0;
            long physCursor = fileNode.ContentOffset;

            while (totalWritten < contiguousTotal)
            {
                int chunkLen = (int)Math.Min(CopyStreamChunkBytes, contiguousTotal - totalWritten);
                byte[] chunk = RawDiskEngine.ReadBytes(disk, physCursor, chunkLen);
                int actual = Math.Min(chunkLen, chunk.Length);
                if (actual <= 0) break;

                destination.Write(chunk, 0, actual);
                totalWritten += actual;
                physCursor += actual;

                if (onProgress != null) onProgress(totalWritten, contiguousTotal);
                if (actual < chunkLen) break;
            }

            return totalWritten;
        }

        /// <summary>
        /// Searches a mounted volume recursively by case-insensitive filename or full-path substring.
        /// </summary>
        public static List<VirtualFsNode> SearchVolume(MountedReadOnlyVolume vol, string query)
        {
            var results = new List<VirtualFsNode>();
            if (vol == null || vol.RootNode == null || string.IsNullOrEmpty(query))
                return results;

            string needle = query.Trim();
            if (needle.Length == 0) return results;

            SearchRecursive(vol.RootNode, needle, results, 0);
            results.Sort((a, b) =>
            {
                if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;
                return string.Compare(a.FullPath, b.FullPath, StringComparison.OrdinalIgnoreCase);
            });
            return results;
        }

        private static void SearchRecursive(VirtualFsNode current, string needle, List<VirtualFsNode> results, int depth)
        {
            if (current == null || depth > 48) return;
            foreach (var child in current.Children)
            {
                if ((!string.IsNullOrEmpty(child.Name) && child.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (!string.IsNullOrEmpty(child.FullPath) && child.FullPath.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    results.Add(child);
                }
                if (child.IsDirectory)
                    SearchRecursive(child, needle, results, depth + 1);
            }
        }

        /// <summary>
        /// Exports a file or entire directory subtree to the host Windows filesystem.
        /// </summary>
        public static void ExportNodeToHost(
            DiskInfo disk, PartitionEntry part, VirtualFsNode node, string hostDestinationPath,
            out int filesCopied, out int foldersCreated, out long bytesCopied)
        {
            ExportNodeToHost(disk, part, node, hostDestinationPath, null, null, out filesCopied, out foldersCreated, out bytesCopied);
        }

        /// <summary>
        /// Exports a file or directory tree to the host with byte-level progress callbacks and cancellation support.
        /// </summary>
        public static void ExportNodeToHost(
            DiskInfo disk,
            PartitionEntry part,
            VirtualFsNode node,
            string hostDestinationPath,
            Action<long, long, string> onProgress,
            Func<bool> isCancelled,
            out int filesCopied,
            out int foldersCreated,
            out long bytesCopied)
        {
            filesCopied = 0;
            foldersCreated = 0;
            bytesCopied = 0;

            if (disk == null) throw new ArgumentNullException("disk");
            if (part == null) throw new ArgumentNullException("part");
            if (node == null) throw new ArgumentNullException("node");
            if (string.IsNullOrEmpty(hostDestinationPath)) throw new ArgumentNullException("hostDestinationPath");

            long totalEstimatedBytes = ComputeEstimatedBytes(node, 0);

            if (!node.IsDirectory)
            {
                string safeName = SanitizeHostName(node.Name);
                string targetFile = Directory.Exists(hostDestinationPath)
                    ? Path.Combine(hostDestinationPath, safeName)
                    : hostDestinationPath;
                string parent = Path.GetDirectoryName(targetFile);
                if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                    Directory.CreateDirectory(parent);

                long written = StreamNodeToHostFile(disk, part, node, targetFile, 0, totalEstimatedBytes, onProgress);
                filesCopied = 1;
                bytesCopied = written;
                return;
            }

            string rootFolderName = (node.FullPath == "/")
                ? SanitizeHostName(string.IsNullOrEmpty(part.Label) ? "VolumeRoot" : part.Label)
                : SanitizeHostName(node.Name);
            string destRoot = Path.Combine(hostDestinationPath, rootFolderName);
            ExportDirectoryRecursive(disk, part, node, destRoot, totalEstimatedBytes, onProgress, isCancelled, ref filesCopied, ref foldersCreated, ref bytesCopied, 0);
        }

        private static long ComputeEstimatedBytes(VirtualFsNode node, int depth)
        {
            if (node == null || depth > 48) return 0;
            if (!node.IsDirectory)
                return node.SizeBytes > 0 ? node.SizeBytes : Math.Max(0, node.ContentLength);

            long sum = 0;
            foreach (var child in node.Children)
                sum += ComputeEstimatedBytes(child, depth + 1);
            return sum;
        }

        private static long StreamNodeToHostFile(
            DiskInfo disk,
            PartitionEntry part,
            VirtualFsNode fileNode,
            string targetFile,
            long completedBeforeFile,
            long totalEstimatedBytes,
            Action<long, long, string> onProgress)
        {
            if (onProgress != null)
                onProgress(completedBeforeFile, totalEstimatedBytes, fileNode.Name);

            using (var fs = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
            {
                long copied = CopyFileNodeToStream(disk, part, fileNode, fs, long.MaxValue, (doneInFile, fileTotal) =>
                {
                    if (onProgress != null)
                        onProgress(completedBeforeFile + doneInFile, Math.Max(totalEstimatedBytes, completedBeforeFile + fileTotal), fileNode.Name);
                });
                fs.Flush();
                return copied;
            }
        }

        private static void ExportDirectoryRecursive(
            DiskInfo disk,
            PartitionEntry part,
            VirtualFsNode dirNode,
            string targetDir,
            long totalEstimatedBytes,
            Action<long, long, string> onProgress,
            Func<bool> isCancelled,
            ref int filesCopied,
            ref int foldersCreated,
            ref long bytesCopied,
            int depth)
        {
            if (dirNode == null || depth > 48) return;
            if (isCancelled != null && isCancelled())
                throw new OperationCanceledException("Export to host was cancelled.");

            Directory.CreateDirectory(targetDir);
            foldersCreated++;

            foreach (var child in dirNode.Children)
            {
                if (isCancelled != null && isCancelled())
                    throw new OperationCanceledException("Export to host was cancelled.");

                string safeChild = SanitizeHostName(child.Name);
                if (string.IsNullOrEmpty(safeChild) || safeChild == "." || safeChild == "..")
                    continue;

                if (child.IsDirectory)
                {
                    ExportDirectoryRecursive(disk, part, child, Path.Combine(targetDir, safeChild), totalEstimatedBytes, onProgress, isCancelled, ref filesCopied, ref foldersCreated, ref bytesCopied, depth + 1);
                }
                else
                {
                    long written = StreamNodeToHostFile(disk, part, child, Path.Combine(targetDir, safeChild), bytesCopied, totalEstimatedBytes, onProgress);
                    filesCopied++;
                    bytesCopied += written;
                }
            }
        }

        private static string SanitizeHostName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "unnamed";
            string cleaned = name.Trim().Replace("\r", string.Empty).Replace("\n", string.Empty);
            char[] invalid = Path.GetInvalidFileNameChars();
            for (int i = 0; i < invalid.Length; i++)
                cleaned = cleaned.Replace(invalid[i], '_');
            cleaned = cleaned.TrimEnd('.');
            return string.IsNullOrEmpty(cleaned) ? "unnamed" : cleaned;
        }

        // =========================================================================
        // Experimental Read-Write Overlay API (WPRWOVL1 @ +0x20000, Payloads @ +0x24000+)
        // =========================================================================

        /// <summary>
        /// Enables or disables the red-banner experimental write toggle on a mounted volume.
        /// </summary>
        public static void SetExperimentalWriteMode(MountedReadOnlyVolume vol, bool enabled)
        {
            if (vol == null) throw new ArgumentNullException("vol");
            vol.ExperimentalWriteEnabled = enabled;
        }

        /// <summary>
        /// Creates a directory inside the volume's persistent <c>WPRWOVL1</c> experimental overlay.
        /// </summary>
        public static VirtualFsNode CreateDirectoryOnVolume(
            DiskInfo disk, PartitionEntry part, MountedReadOnlyVolume vol, VirtualFsNode parentDir, string folderName)
        {
            EnsureExperimentalWriteAllowed(disk, part, vol);
            string cleanName = SanitizeVolumeEntryName(folderName);
            VirtualFsNode parent = ResolveTargetDir(vol, parentDir);
            string fullPath = BuildChildPath(parent.FullPath, cleanName);

            VirtualFsNode existing = FindChildByName(parent, cleanName);
            if (existing != null)
            {
                if (!existing.IsDirectory)
                    throw new IOException("A file with that name already exists: " + fullPath);
                return existing;
            }

            DateTime now = DateTime.UtcNow;
            var dirNode = new VirtualFsNode
            {
                Name = cleanName,
                FullPath = fullPath,
                IsDirectory = true,
                SizeBytes = 0,
                InodeOrObjectId = NextOverlayInodeId(vol),
                PermissionsString = "drwxr-xr-x",
                ModifiedUtc = now
            };
            parent.Children.Add(dirNode);
            SortAndAssignPaths(parent, parent.FullPath);

            UpsertOverlayRecord(vol, new OverlayRecord
            {
                Op = 1,
                IsDirectory = true,
                RelDataOffset = 0,
                DataLength = 0,
                ModifiedUtcTicks = now.Ticks,
                FullPath = fullPath
            });
            SaveExperimentalOverlay(disk, part, vol);
            return dirNode;
        }

        /// <summary>
        /// Writes raw bytes as a new or overwritten file into the experimental overlay.
        /// </summary>
        public static VirtualFsNode WriteFileToVolume(
            DiskInfo disk, PartitionEntry part, MountedReadOnlyVolume vol, VirtualFsNode targetDir, string fileName, byte[] data)
        {
            EnsureExperimentalWriteAllowed(disk, part, vol);
            var node = WriteFileInternal(disk, part, vol, ResolveTargetDir(vol, targetDir), fileName, data ?? new byte[0]);
            SaveExperimentalOverlay(disk, part, vol);
            return node;
        }

        /// <summary>
        /// Copies a single file from the host OS into the mounted partition's experimental overlay.
        /// </summary>
        public static VirtualFsNode WriteHostFileToVolume(
            DiskInfo disk, PartitionEntry part, MountedReadOnlyVolume vol, VirtualFsNode targetDir, string hostFilePath)
        {
            if (string.IsNullOrEmpty(hostFilePath) || !File.Exists(hostFilePath))
                throw new FileNotFoundException("Host file not found.", hostFilePath);

            EnsureExperimentalWriteAllowed(disk, part, vol);
            byte[] data = File.ReadAllBytes(hostFilePath);
            var node = WriteFileInternal(disk, part, vol, ResolveTargetDir(vol, targetDir), Path.GetFileName(hostFilePath), data);
            SaveExperimentalOverlay(disk, part, vol);
            return node;
        }

        /// <summary>
        /// Recursively copies a host directory and its files into the mounted partition's experimental overlay.
        /// </summary>
        public static VirtualFsNode WriteHostDirectoryToVolume(
            DiskInfo disk, PartitionEntry part, MountedReadOnlyVolume vol, VirtualFsNode targetDir, string hostDirPath,
            out int filesWritten, out int foldersWritten)
        {
            filesWritten = 0;
            foldersWritten = 0;
            if (string.IsNullOrEmpty(hostDirPath) || !Directory.Exists(hostDirPath))
                throw new DirectoryNotFoundException("Host folder not found: " + hostDirPath);

            EnsureExperimentalWriteAllowed(disk, part, vol);
            VirtualFsNode parent = ResolveTargetDir(vol, targetDir);
            VirtualFsNode rootCreated = WriteHostDirectoryRecursive(disk, part, vol, parent, hostDirPath, ref filesWritten, ref foldersWritten, 0);
            SaveExperimentalOverlay(disk, part, vol);
            return rootCreated;
        }

        private static VirtualFsNode WriteHostDirectoryRecursive(
            DiskInfo disk, PartitionEntry part, MountedReadOnlyVolume vol, VirtualFsNode parentDir, string hostDirPath,
            ref int filesWritten, ref int foldersWritten, int depth)
        {
            if (depth > 32) return parentDir;
            string dirName = SanitizeVolumeEntryName(new DirectoryInfo(hostDirPath).Name);
            VirtualFsNode dirNode = EnsureDirectoryChild(vol, parentDir, dirName);
            foldersWritten++;

            string[] subDirs = Directory.GetDirectories(hostDirPath);
            for (int i = 0; i < subDirs.Length; i++)
                WriteHostDirectoryRecursive(disk, part, vol, dirNode, subDirs[i], ref filesWritten, ref foldersWritten, depth + 1);

            string[] files = Directory.GetFiles(hostDirPath);
            for (int i = 0; i < files.Length; i++)
            {
                byte[] bytes = File.ReadAllBytes(files[i]);
                WriteFileInternal(disk, part, vol, dirNode, Path.GetFileName(files[i]), bytes);
                filesWritten++;
            }

            SortAndAssignPaths(dirNode, dirNode.FullPath);
            return dirNode;
        }

        /// <summary>
        /// Records a tombstone deletion (<c>Op = 2</c>) for a node in the experimental overlay.
        /// </summary>
        public static bool DeleteNodeFromVolume(
            DiskInfo disk, PartitionEntry part, MountedReadOnlyVolume vol, VirtualFsNode targetNode)
        {
            EnsureExperimentalWriteAllowed(disk, part, vol);
            if (targetNode == null || vol.RootNode == null || ReferenceEquals(targetNode, vol.RootNode) || targetNode.FullPath == "/")
                throw new InvalidOperationException("Cannot delete root directory.");

            if (!RemoveNodeRecursive(vol.RootNode, targetNode))
                return false;

            UpsertOverlayRecord(vol, new OverlayRecord
            {
                Op = 2,
                IsDirectory = targetNode.IsDirectory,
                RelDataOffset = 0,
                DataLength = 0,
                ModifiedUtcTicks = DateTime.UtcNow.Ticks,
                FullPath = targetNode.FullPath
            });
            SaveExperimentalOverlay(disk, part, vol);
            return true;
        }

        private static VirtualFsNode WriteFileInternal(
            DiskInfo disk, PartitionEntry part, MountedReadOnlyVolume vol, VirtualFsNode parentDir, string fileName, byte[] data)
        {
            string cleanName = SanitizeVolumeEntryName(fileName);
            string fullPath = BuildChildPath(parentDir.FullPath, cleanName);

            long relDataOffset = 0;
            if (data.Length > 0)
            {
                long tableRel = vol.OverlayTableRelOffset > 0 ? vol.OverlayTableRelOffset : 0x20000L;
                long minDataRel = tableRel + 0x4000L;
                if (vol.OverlayNextDataRelOffset < minDataRel)
                    vol.OverlayNextDataRelOffset = minDataRel;

                relDataOffset = ((vol.OverlayNextDataRelOffset + 4095L) / 4096L) * 4096L;
                long alignedWriteLen = ((data.Length + 4095L) / 4096L) * 4096L;
                if (part.StartingOffset + relDataOffset + alignedWriteLen > part.EndOffset)
                    throw new IOException("Partition does not have enough overlay space remaining to write '" + cleanName + "'.");

                byte[] sectorBuf = new byte[(int)alignedWriteLen];
                Array.Copy(data, sectorBuf, data.Length);
                WriteOverlayRegionBytes(disk, part, vol, ref relDataOffset, sectorBuf);
                vol.OverlayNextDataRelOffset = relDataOffset + alignedWriteLen;
            }

            DateTime now = DateTime.UtcNow;
            VirtualFsNode existing = FindChildByName(parentDir, cleanName);
            if (existing != null)
                parentDir.Children.Remove(existing);

            string activeShadowPath = vol.OverlayUsingHostShadow ? GetHostShadowOverlayFilePath(disk, part) : null;
            var fileNode = new VirtualFsNode
            {
                Name = cleanName,
                FullPath = fullPath,
                IsDirectory = false,
                SizeBytes = data.Length,
                InodeOrObjectId = NextOverlayInodeId(vol),
                PermissionsString = "-rw-r--r--",
                ModifiedUtc = now,
                ContentOffset = (data.Length > 0 && !vol.OverlayUsingHostShadow) ? (part.StartingOffset + relDataOffset) : 0,
                ContentLength = data.Length,
                InlineContent = data.Length == 0 ? new byte[0] : null,
                HostShadowFilePath = (data.Length > 0 && !string.IsNullOrEmpty(activeShadowPath)) ? activeShadowPath : null,
                HostShadowOffset = relDataOffset
            };
            parentDir.Children.Add(fileNode);
            SortAndAssignPaths(parentDir, parentDir.FullPath);

            UpsertOverlayRecord(vol, new OverlayRecord
            {
                Op = 1,
                IsDirectory = false,
                RelDataOffset = relDataOffset,
                DataLength = data.Length,
                ModifiedUtcTicks = now.Ticks,
                FullPath = fullPath
            });
            return fileNode;
        }

        private static void EnsureExperimentalWriteAllowed(DiskInfo disk, PartitionEntry part, MountedReadOnlyVolume vol)
        {
            if (disk == null) throw new ArgumentNullException("disk");
            if (part == null) throw new ArgumentNullException("part");
            if (vol == null) throw new ArgumentNullException("vol");
            if (!vol.ExperimentalWriteEnabled)
                throw new InvalidOperationException("Volume is mounted strictly READ-ONLY. Enable the red 'Experimental Write' toggle first.");
            if (part.PartitionLength < 0x40000L)
                throw new IOException("Partition is too small (< 256 KiB) for experimental overlay writes.");
        }

        private static VirtualFsNode ResolveTargetDir(MountedReadOnlyVolume vol, VirtualFsNode targetDir)
        {
            if (targetDir != null && targetDir.IsDirectory) return targetDir;
            return vol.RootNode;
        }

        private static string SanitizeVolumeEntryName(string name)
        {
            string trimmed = (name ?? string.Empty).Replace('\\', '_').Replace('/', '_').Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed == "." || trimmed == "..")
                throw new ArgumentException("Invalid file or folder name.");
            return trimmed;
        }

        private static string BuildChildPath(string parentPath, string childName)
        {
            return (string.IsNullOrEmpty(parentPath) || parentPath == "/")
                ? ("/" + childName)
                : (parentPath.TrimEnd('/') + "/" + childName);
        }

        private static VirtualFsNode FindChildByName(VirtualFsNode parent, string name)
        {
            if (parent == null) return null;
            foreach (var c in parent.Children)
            {
                if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                    return c;
            }
            return null;
        }

        private static bool RemoveNodeRecursive(VirtualFsNode current, VirtualFsNode target)
        {
            if (current == null) return false;
            if (current.Children.Remove(target))
                return true;
            foreach (var c in current.Children)
            {
                if (c.IsDirectory && RemoveNodeRecursive(c, target))
                    return true;
            }
            return false;
        }

        private static ulong NextOverlayInodeId(MountedReadOnlyVolume vol)
        {
            return 900000UL + (ulong)(vol.OverlayRecords != null ? vol.OverlayRecords.Count + 1 : 1);
        }

        private static VirtualFsNode EnsureDirectoryChild(MountedReadOnlyVolume vol, VirtualFsNode parentDir, string dirName)
        {
            VirtualFsNode existing = FindChildByName(parentDir, dirName);
            if (existing != null)
            {
                if (!existing.IsDirectory)
                    throw new IOException("Path conflict: file already exists at " + existing.FullPath);
                return existing;
            }

            DateTime now = DateTime.UtcNow;
            string fullPath = BuildChildPath(parentDir.FullPath, dirName);
            var dirNode = new VirtualFsNode
            {
                Name = dirName,
                FullPath = fullPath,
                IsDirectory = true,
                InodeOrObjectId = NextOverlayInodeId(vol),
                PermissionsString = "drwxr-xr-x",
                ModifiedUtc = now
            };
            parentDir.Children.Add(dirNode);
            UpsertOverlayRecord(vol, new OverlayRecord
            {
                Op = 1,
                IsDirectory = true,
                RelDataOffset = 0,
                DataLength = 0,
                ModifiedUtcTicks = now.Ticks,
                FullPath = fullPath
            });
            return dirNode;
        }

        private static void UpsertOverlayRecord(MountedReadOnlyVolume vol, OverlayRecord rec)
        {
            if (vol.OverlayRecords == null)
                vol.OverlayRecords = new List<OverlayRecord>();
            for (int i = vol.OverlayRecords.Count - 1; i >= 0; i--)
            {
                if (string.Equals(vol.OverlayRecords[i].FullPath, rec.FullPath, StringComparison.OrdinalIgnoreCase))
                    vol.OverlayRecords.RemoveAt(i);
            }
            vol.OverlayRecords.Add(rec);
        }

        internal static string ShadowOverlayRootOverride;

        internal static string GetHostShadowOverlayFilePath(DiskInfo disk, PartitionEntry part)
        {
            string baseDir = !string.IsNullOrEmpty(ShadowOverlayRootOverride)
                ? ShadowOverlayRootOverride
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinParted", "ExperimentalOverlays");
            string identity = string.Format("{0}|{1}|{2}|{3}|{4}",
                disk != null ? (disk.SerialNumber ?? string.Empty) : string.Empty,
                disk != null ? (disk.Model ?? string.Empty) : string.Empty,
                part != null ? part.StartingOffset : 0,
                part != null ? part.PartitionLength : 0,
                part != null ? (part.UUID ?? string.Empty) : string.Empty);
            string hashHex = ComputeFnv1a64Hex(identity);
            long startOff = part != null ? part.StartingOffset : 0;
            int diskNum = disk != null ? disk.DiskNumber : 0;
            return Path.Combine(baseDir, string.Format("ovl_d{0}_{1:X}_{2}.bin", diskNum, startOff, hashHex));
        }

        private static string ComputeFnv1a64Hex(string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text ?? string.Empty);
            ulong hash = 14695981039346656037UL;
            for (int i = 0; i < bytes.Length; i++)
            {
                hash ^= bytes[i];
                hash *= 1099511628211UL;
            }
            return hash.ToString("X16");
        }

        internal static void WriteShadowRegionBytes(string shadowPath, long relOffset, byte[] data)
        {
            string dir = Path.GetDirectoryName(shadowPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            using (var fs = new FileStream(shadowPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                long requiredLen = Math.Max(0x24000L, relOffset + data.Length);
                if (fs.Length < requiredLen)
                    fs.SetLength(requiredLen);
                fs.Seek(relOffset, SeekOrigin.Begin);
                fs.Write(data, 0, data.Length);
                fs.Flush();
            }
        }

        private static bool OverlapsOtherPartition(DiskInfo disk, PartitionEntry part, long relStart, long regionLength)
        {
            if (disk == null || disk.Partitions == null || part == null) return false;
            long absStart = part.StartingOffset + relStart;
            long absEnd = absStart + regionLength;
            foreach (var other in disk.Partitions)
            {
                if (other == null || other.IsUnallocated || other.PartitionLength <= 0) continue;
                if (other.StartingOffset == part.StartingOffset && other.PartitionLength == part.PartitionLength) continue;
                if (other.StartingOffset < absEnd && other.EndOffset > absStart)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Returns ordered candidate relative offsets for the <c>WPRWOVL1</c> experimental overlay table,
        /// preferring a non-overlapping tail region behind ISO9660/isohybrid images when <c>+0x20000</c>
        /// would overlap an embedded sub-partition (e.g., ESP).
        /// </summary>
        private static long[] GetCandidateOverlayTableRelOffsets(DiskInfo disk, PartitionEntry part)
        {
            if (part == null) return new long[] { 0x20000L };
            var list = new List<long>();
            long headCand = 0x20000L;
            long tailCand = -1;
            if (part.PartitionLength >= 16L * 1048576L)
                tailCand = ((part.PartitionLength - 8L * 1048576L) / 4096L) * 4096L;
            else if (part.PartitionLength >= 2L * 1048576L)
                tailCand = ((part.PartitionLength - 512L * 1024L) / 4096L) * 4096L;

            bool headOverlaps = OverlapsOtherPartition(disk, part, headCand, 0xE0000L);
            if (headOverlaps)
            {
                if (tailCand > headCand && !OverlapsOtherPartition(disk, part, tailCand, 0x40000L))
                    list.Add(tailCand);
                list.Add(headCand);
                if (tailCand > headCand && !list.Contains(tailCand))
                    list.Add(tailCand);
            }
            else
            {
                list.Add(headCand);
                if (tailCand > headCand)
                    list.Add(tailCand);
            }
            return list.ToArray();
        }

        private static void WriteOverlayRegionBytes(DiskInfo disk, PartitionEntry part, MountedReadOnlyVolume vol, ref long relOffset, byte[] data)
        {
            if (disk.IsVirtualImage)
            {
                RawDiskEngine.WriteBytes(disk, part.StartingOffset + relOffset, data);
                vol.OverlayDiskOffsetCommitted = true;
                return;
            }

            long currentTableRel = vol.OverlayTableRelOffset > 0 ? vol.OverlayTableRelOffset : 0x20000L;
            string shadowPath = GetHostShadowOverlayFilePath(disk, part);
            if (!vol.OverlayUsingHostShadow)
            {
                try
                {
                    RawDiskEngine.WriteBytes(disk, part.StartingOffset + relOffset, data);
                    vol.OverlayDiskOffsetCommitted = true;
                    return;
                }
                catch (Exception ex)
                {
                    if (!(ex is Win32Exception) && !(ex is UnauthorizedAccessException) && !(ex is IOException))
                        throw;

                    // Before falling back to a local host shadow file, try alternate non-overlapping on-disk offsets
                    // when no previous on-disk overlay write has been committed yet (critical on isohybrid partitions).
                    if (!vol.OverlayDiskOffsetCommitted)
                    {
                        long[] candidates = GetCandidateOverlayTableRelOffsets(disk, part);
                        long delta = relOffset - currentTableRel;
                        for (int i = 0; i < candidates.Length; i++)
                        {
                            long altTableRel = candidates[i];
                            if (altTableRel == currentTableRel) continue;
                            long altRelOffset = altTableRel + delta;
                            if (altRelOffset < 0 || part.StartingOffset + altRelOffset + data.Length > part.EndOffset)
                                continue;
                            try
                            {
                                RawDiskEngine.WriteBytes(disk, part.StartingOffset + altRelOffset, data);
                                vol.OverlayTableRelOffset = altTableRel;
                                vol.OverlayDiskOffsetCommitted = true;
                                relOffset = altRelOffset;
                                return;
                            }
                            catch
                            {
                            }
                        }
                    }

                    vol.OverlayUsingHostShadow = true;
                    vol.OverlayShadowMessage = "Host shadow overlay active (physical medium blocked raw write; stored locally on host).";
                }
            }

            WriteShadowRegionBytes(shadowPath, relOffset, data);
        }

        /// <summary>
        /// Serializes up to 16 KiB of <c>WPRWOVL1</c> overlay directory/file/tombstone records at the volume's
        /// selected <c>OverlayTableRelOffset</c> (default <c>+0x20000</c> or non-overlapping tail region),
        /// falling back to the host shadow overlay file on physical disks only if all on-disk offsets fail.
        /// </summary>
        private static void SaveExperimentalOverlay(DiskInfo disk, PartitionEntry part, MountedReadOnlyVolume vol)
        {
            long tableRel = vol.OverlayTableRelOffset > 0 ? vol.OverlayTableRelOffset : 0x20000L;
            const int tableSize = 16384; // 16 KiB table header
            byte[] buf = new byte[tableSize];
            Encoding.ASCII.GetBytes("WPRWOVL1").CopyTo(buf, 0);
            BitConverter.GetBytes((ulong)Math.Max(tableRel + 0x4000L, vol.OverlayNextDataRelOffset)).CopyTo(buf, 8);

            var records = vol.OverlayRecords ?? new List<OverlayRecord>();
            int cursor = 20;
            uint writtenCount = 0;

            for (int i = 0; i < records.Count; i++)
            {
                var r = records[i];
                byte[] pathBytes = Encoding.UTF8.GetBytes(r.FullPath ?? string.Empty);
                int recLen = 28 + pathBytes.Length;
                if (cursor + recLen > tableSize) break;

                buf[cursor++] = r.Op;
                buf[cursor++] = r.IsDirectory ? (byte)1 : (byte)0;
                BitConverter.GetBytes(r.RelDataOffset).CopyTo(buf, cursor); cursor += 8;
                BitConverter.GetBytes(r.DataLength).CopyTo(buf, cursor); cursor += 8;
                BitConverter.GetBytes(r.ModifiedUtcTicks).CopyTo(buf, cursor); cursor += 8;
                BitConverter.GetBytes((ushort)pathBytes.Length).CopyTo(buf, cursor); cursor += 2;
                Array.Copy(pathBytes, 0, buf, cursor, pathBytes.Length); cursor += pathBytes.Length;
                writtenCount++;
            }

            BitConverter.GetBytes(writtenCount).CopyTo(buf, 16);
            WriteOverlayRegionBytes(disk, part, vol, ref tableRel, buf);
        }

        /// <summary>
        /// Loads any persisted <c>WPRWOVL1</c> experimental write overlay from the partition on disk
        /// (checking candidate table offsets, or from a physical partition's host shadow file if present)
        /// and merges it onto the volume tree.
        /// </summary>
        internal static void LoadAndMergeExperimentalOverlay(DiskInfo disk, PartitionEntry part, MountedReadOnlyVolume vol)
        {
            if (part.PartitionLength < 0x30000L || vol.RootNode == null) return;
            try
            {
                byte[] table = null;
                string activeShadowPath = null;

                // 1. Prefer direct on-disk WPRWOVL1 table if present at any candidate offset
                long[] candidates = GetCandidateOverlayTableRelOffsets(disk, part);
                for (int c = 0; c < candidates.Length; c++)
                {
                    long cand = candidates[c];
                    if (cand < 0 || part.StartingOffset + cand + 4096L > part.EndOffset)
                        continue;
                    try
                    {
                        byte[] probe = RawDiskEngine.ReadBytes(disk, part.StartingOffset + cand, 4096);
                        if (probe.Length >= 20 && MatchesAscii(probe, 0, "WPRWOVL1"))
                        {
                            byte[] fullTable = RawDiskEngine.ReadBytes(disk, part.StartingOffset + cand, 16384);
                            if (fullTable.Length >= 20 && MatchesAscii(fullTable, 0, "WPRWOVL1"))
                            {
                                table = fullTable;
                                vol.OverlayTableRelOffset = cand;
                                vol.OverlayDiskOffsetCommitted = true;
                                break;
                            }
                        }
                    }
                    catch
                    {
                    }
                }

                // 2. Fall back to local host shadow overlay if no direct on-disk WPRWOVL1 table exists
                if (table == null && !disk.IsVirtualImage)
                {
                    string shadowPath = GetHostShadowOverlayFilePath(disk, part);
                    if (File.Exists(shadowPath))
                    {
                        using (var sfs = new FileStream(shadowPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            long[] shadowCandidates = candidates.Length > 0 ? candidates : new long[] { 0x20000L };
                            for (int c = 0; c < shadowCandidates.Length; c++)
                            {
                                long sCand = shadowCandidates[c];
                                if (sfs.Length >= sCand + 20)
                                {
                                    int toRead = (int)Math.Min(16384L, sfs.Length - sCand);
                                    byte[] sTable = new byte[toRead];
                                    sfs.Seek(sCand, SeekOrigin.Begin);
                                    int n = sfs.Read(sTable, 0, toRead);
                                    if (n >= 20 && MatchesAscii(sTable, 0, "WPRWOVL1"))
                                    {
                                        table = sTable;
                                        activeShadowPath = shadowPath;
                                        vol.OverlayTableRelOffset = sCand;
                                        vol.OverlayUsingHostShadow = true;
                                        vol.OverlayShadowMessage = "Host shadow overlay active (loaded local host overlay for physical partition).";
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }

                if (table == null)
                    return;

                ulong nextData = BitConverter.ToUInt64(table, 8);
                long minDataRel = (vol.OverlayTableRelOffset > 0 ? vol.OverlayTableRelOffset : 0x20000L) + 0x4000L;
                vol.OverlayNextDataRelOffset = (long)Math.Max((ulong)minDataRel, nextData);
                uint recCount = BitConverter.ToUInt32(table, 16);

                int cursor = 20;
                for (uint i = 0; i < recCount && cursor + 28 <= table.Length; i++)
                {
                    byte op = table[cursor++];
                    bool isDir = table[cursor++] != 0;
                    long relOff = BitConverter.ToInt64(table, cursor); cursor += 8;
                    long dataLen = BitConverter.ToInt64(table, cursor); cursor += 8;
                    long ticks = BitConverter.ToInt64(table, cursor); cursor += 8;
                    ushort pathLen = BitConverter.ToUInt16(table, cursor); cursor += 2;
                    if (cursor + pathLen > table.Length) break;
                    string fullPath = Encoding.UTF8.GetString(table, cursor, pathLen); cursor += pathLen;

                    var rec = new OverlayRecord
                    {
                        Op = op,
                        IsDirectory = isDir,
                        RelDataOffset = relOff,
                        DataLength = dataLen,
                        ModifiedUtcTicks = ticks,
                        FullPath = fullPath
                    };
                    UpsertOverlayRecord(vol, rec);

                    if (op == 2)
                    {
                        var doomed = FindNodeByPath(vol.RootNode, fullPath);
                        if (doomed != null && !ReferenceEquals(doomed, vol.RootNode))
                            RemoveNodeRecursive(vol.RootNode, doomed);
                    }
                    else if (op == 1)
                    {
                        ApplyOverlayUpsertToTree(part, vol, rec, activeShadowPath);
                    }
                }

                SortAndAssignPaths(vol.RootNode, "/");
            }
            catch
            {
            }
        }

        private static void ApplyOverlayUpsertToTree(PartitionEntry part, MountedReadOnlyVolume vol, OverlayRecord rec, string shadowFilePath)
        {
            if (string.IsNullOrEmpty(rec.FullPath) || rec.FullPath == "/") return;
            string[] parts = rec.FullPath.Trim('/').Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return;

            VirtualFsNode parent = vol.RootNode;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                VirtualFsNode sub = FindChildByName(parent, parts[i]);
                if (sub == null)
                {
                    sub = new VirtualFsNode
                    {
                        Name = parts[i],
                        FullPath = BuildChildPath(parent.FullPath, parts[i]),
                        IsDirectory = true,
                        PermissionsString = "drwxr-xr-x"
                    };
                    parent.Children.Add(sub);
                }
                if (!sub.IsDirectory) return;
                parent = sub;
            }

            string leafName = parts[parts.Length - 1];
            VirtualFsNode existing = FindChildByName(parent, leafName);
            DateTime mtime = (rec.ModifiedUtcTicks > 0) ? new DateTime(rec.ModifiedUtcTicks, DateTimeKind.Utc) : DateTime.UtcNow;

            if (rec.IsDirectory)
            {
                if (existing == null)
                {
                    parent.Children.Add(new VirtualFsNode
                    {
                        Name = leafName,
                        FullPath = rec.FullPath,
                        IsDirectory = true,
                        InodeOrObjectId = NextOverlayInodeId(vol),
                        PermissionsString = "drwxr-xr-x",
                        ModifiedUtc = mtime
                    });
                }
            }
            else
            {
                if (existing != null)
                    parent.Children.Remove(existing);
                bool hasShadow = !string.IsNullOrEmpty(shadowFilePath) && rec.DataLength > 0 && rec.RelDataOffset > 0;
                parent.Children.Add(new VirtualFsNode
                {
                    Name = leafName,
                    FullPath = rec.FullPath,
                    IsDirectory = false,
                    SizeBytes = rec.DataLength,
                    InodeOrObjectId = NextOverlayInodeId(vol),
                    PermissionsString = "-rw-r--r--",
                    ModifiedUtc = mtime,
                    ContentOffset = (!hasShadow && rec.DataLength > 0 && rec.RelDataOffset > 0) ? (part.StartingOffset + rec.RelDataOffset) : 0,
                    ContentLength = rec.DataLength,
                    InlineContent = rec.DataLength == 0 ? new byte[0] : null,
                    HostShadowFilePath = hasShadow ? shadowFilePath : null,
                    HostShadowOffset = rec.RelDataOffset
                });
            }
        }

        private static VirtualFsNode FindNodeByPath(VirtualFsNode current, string fullPath)
        {
            if (current == null) return null;
            if (string.Equals(current.FullPath, fullPath, StringComparison.OrdinalIgnoreCase))
                return current;
            foreach (var c in current.Children)
            {
                var found = FindNodeByPath(c, fullPath);
                if (found != null) return found;
            }
            return null;
        }

        // =========================================================================
        // Native Filesystem Parsers
        // =========================================================================

        /// <summary>
        /// Parses an Ext2/Ext3/Ext4 superblock (<c>0xEF53</c>), Block Group 0 Descriptor, and root inode 2,
        /// supporting both legacy direct/indirect blocks and Ext4 extent trees (<c>0xF30A</c>).
        /// </summary>
        private static void ParseExtVolume(DiskInfo disk, PartitionEntry part, MountedReadOnlyVolume vol)
        {
            byte[] sb = RawDiskEngine.ReadBytes(disk, part.StartingOffset + 1024, 1024);
            if (sb.Length < 1024 || BitConverter.ToUInt16(sb, 0x38) != 0xEF53)
                throw new InvalidOperationException("Valid ext2/3/4 superblock signature (0xEF53) not found.");

            uint sLogBlock = BitConverter.ToUInt32(sb, 0x18);
            long blockSize = 1024L << (int)Math.Min(6, sLogBlock);
            uint sFirstDataBlock = BitConverter.ToUInt32(sb, 0x14);
            uint blocksCount = BitConverter.ToUInt32(sb, 0x04);
            uint freeBlocks = BitConverter.ToUInt32(sb, 0x0C);
            uint revLevel = BitConverter.ToUInt32(sb, 0x4C);
            int inodeSize = (revLevel >= 1) ? Math.Max(128, (int)BitConverter.ToUInt16(sb, 0x58)) : 128;

            vol.BlockSize = blockSize;
            vol.SuperblockSummary = string.Format(
                "{0} | BlockSize={1} B | Blocks={2:N0} (Free={3:N0}) | InodeSize={4} B | Rev={5}",
                FileSystemColors.GetDisplayName(part.FileSystem), blockSize, blocksCount, freeBlocks, inodeSize, revLevel);

            long bgdtOffset = part.StartingOffset + (long)(sFirstDataBlock + 1) * blockSize;
            byte[] bgd = RawDiskEngine.ReadBytes(disk, bgdtOffset, 64);
            uint inodeTableBlock = bgd.Length >= 12 ? BitConverter.ToUInt32(bgd, 8) : 0;

            if (inodeTableBlock > 0 && inodeTableBlock < blocksCount)
            {
                var visited = new HashSet<uint>();
                var root = ReadExtDirectoryNode(disk, part, vol, blockSize, inodeTableBlock, inodeSize, 2u, "/", "/", visited, 0);
                if (root != null && root.Children.Count > 0)
                {
                    vol.RootNode = root;
                    return;
                }
            }

            // Fallback metadata-only tree if partition was created without populated inode table
            vol.RootNode = BuildSuperblockOnlyTree(part, vol, RawDiskEngine.ReadBytes(disk, part.StartingOffset, (int)Math.Min(4096, part.PartitionLength)));
        }

        private static VirtualFsNode ReadExtDirectoryNode(
            DiskInfo disk, PartitionEntry part, MountedReadOnlyVolume vol,
            long blockSize, uint inodeTableBlock, int inodeSize,
            uint inodeNum, string name, string fullPath, HashSet<uint> visited, int depth)
        {
            if (depth > 16 || !visited.Add(inodeNum))
                return null;

            byte[] inode = ReadExtInodeBytes(disk, part, blockSize, inodeTableBlock, inodeSize, inodeNum);
            if (inode.Length < 128) return null;

            ushort mode = BitConverter.ToUInt16(inode, 0x00);
            uint mtime = BitConverter.ToUInt32(inode, 0x10);
            bool isDir = (mode & 0xF000) == 0x4000;
            if (!isDir)
                return BuildExtFileNode(disk, part, blockSize, inode, inodeNum, name, fullPath);

            var node = new VirtualFsNode
            {
                Name = name,
                FullPath = fullPath,
                IsDirectory = true,
                SizeBytes = BitConverter.ToUInt32(inode, 0x04),
                InodeOrObjectId = inodeNum,
                PermissionsString = FormatUnixMode(mode),
                ModifiedUtc = FromUnixSeconds(mtime)
            };

            var dirExtents = ResolveExtExtents(disk, part, blockSize, inode);
            long dirBytesRead = 0;
            const long MaxDirScanBytes = 2L * 1048576L;

            for (int ex = 0; ex < dirExtents.Count && dirBytesRead < MaxDirScanBytes; ex++)
            {
                long extOff = dirExtents[ex].Key;
                long extLen = dirExtents[ex].Value;
                long cursor = extOff;
                long endOff = Math.Min(part.EndOffset, extOff + extLen);

                while (cursor < endOff && dirBytesRead < MaxDirScanBytes)
                {
                    int blkLen = (int)Math.Min(blockSize, endOff - cursor);
                    if (blkLen < 12) break;
                    byte[] dirBlock = RawDiskEngine.ReadBytes(disk, cursor, blkLen);
                    dirBytesRead += blkLen;
                    cursor += blkLen;

                    int pos = 0;
                    while (pos + 8 <= dirBlock.Length)
                    {
                        uint childInode = BitConverter.ToUInt32(dirBlock, pos + 0);
                        ushort recLen = BitConverter.ToUInt16(dirBlock, pos + 4);
                        byte nameLen = dirBlock[pos + 6];
                        byte fileType = dirBlock[pos + 7];
                        if (recLen < 8 || pos + recLen > dirBlock.Length) break;

                        if (childInode != 0 && nameLen > 0 && pos + 8 + nameLen <= dirBlock.Length)
                        {
                            string childName = Encoding.UTF8.GetString(dirBlock, pos + 8, nameLen);
                            if (childName != "." && childName != "..")
                            {
                                string childPath = fullPath == "/" ? "/" + childName : fullPath + "/" + childName;
                                byte[] childInodeBytes = ReadExtInodeBytes(disk, part, blockSize, inodeTableBlock, inodeSize, childInode);
                                bool childIsDir = childInodeBytes.Length >= 2
                                    ? ((BitConverter.ToUInt16(childInodeBytes, 0) & 0xF000) == 0x4000)
                                    : (fileType == 2);

                                if (childIsDir)
                                {
                                    var sub = ReadExtDirectoryNode(disk, part, vol, blockSize, inodeTableBlock, inodeSize, childInode, childName, childPath, visited, depth + 1);
                                    if (sub != null) node.Children.Add(sub);
                                }
                                else if (childInodeBytes.Length >= 128)
                                {
                                    var fileNode = BuildExtFileNode(disk, part, blockSize, childInodeBytes, childInode, childName, childPath);
                                    if (fileNode != null) node.Children.Add(fileNode);
                                }
                            }
                        }
                        pos += recLen;
                    }
                }
            }

            return node;
        }

        private static VirtualFsNode BuildExtFileNode(
            DiskInfo disk, PartitionEntry part, long blockSize,
            byte[] inode, uint inodeNum, string name, string fullPath)
        {
            ushort mode = BitConverter.ToUInt16(inode, 0x00);
            uint sizeLo = BitConverter.ToUInt32(inode, 0x04);
            uint sizeHi = (inode.Length >= 0x70 && (mode & 0xF000) == 0x8000)
                ? BitConverter.ToUInt32(inode, 0x6C)
                : 0u;
            long fullSize = ((long)sizeHi << 32) | sizeLo;
            uint mtime = BitConverter.ToUInt32(inode, 0x10);
            var extents = ResolveExtExtents(disk, part, blockSize, inode);

            return new VirtualFsNode
            {
                Name = name,
                FullPath = fullPath,
                IsDirectory = false,
                SizeBytes = fullSize,
                InodeOrObjectId = inodeNum,
                PermissionsString = FormatUnixMode(mode),
                ModifiedUtc = FromUnixSeconds(mtime),
                ContentOffset = extents.Count > 0 ? extents[0].Key : 0,
                ContentLength = extents.Count > 0 ? fullSize : 0,
                ContentExtents = extents.Count > 0 ? extents : null
            };
        }

        private static byte[] ReadExtInodeBytes(DiskInfo disk, PartitionEntry part, long blockSize, uint inodeTableBlock, int inodeSize, uint inodeNum)
        {
            if (inodeNum == 0) return new byte[0];
            long offset = part.StartingOffset + (long)inodeTableBlock * blockSize + (long)(inodeNum - 1) * inodeSize;
            if (offset < part.StartingOffset || offset + inodeSize > part.EndOffset)
                return new byte[0];
            return RawDiskEngine.ReadBytes(disk, offset, inodeSize);
        }

        private static List<KeyValuePair<long, long>> ResolveExtExtents(DiskInfo disk, PartitionEntry part, long blockSize, byte[] inode)
        {
            var result = new List<KeyValuePair<long, long>>();
            if (inode == null || inode.Length < 100 || blockSize <= 0) return result;

            uint flags = BitConverter.ToUInt32(inode, 0x20);
            ushort magic = BitConverter.ToUInt16(inode, 0x28);
            bool usesExtents = (flags & 0x80000u) != 0 || magic == 0xF30A;

            if (usesExtents && magic == 0xF30A)
            {
                ParseExt4ExtentNode(disk, part, blockSize, inode, 0x28, Math.Min(inode.Length, 0x64), result, 0);
                return CoalesceExtents(result);
            }

            // Legacy direct blocks i_block[0..11]
            for (int i = 0; i < 12 && 0x28 + (i + 1) * 4 <= inode.Length; i++)
            {
                uint blk = BitConverter.ToUInt32(inode, 0x28 + i * 4);
                if (blk == 0) continue;
                long extOff = part.StartingOffset + (long)blk * blockSize;
                if (extOff >= part.StartingOffset && extOff < part.EndOffset)
                    result.Add(new KeyValuePair<long, long>(extOff, Math.Min(blockSize, part.EndOffset - extOff)));
            }

            // Single indirect block i_block[12]
            if (0x28 + 13 * 4 <= inode.Length)
            {
                uint indBlk = BitConverter.ToUInt32(inode, 0x28 + 12 * 4);
                if (indBlk > 0)
                {
                    long indOff = part.StartingOffset + (long)indBlk * blockSize;
                    if (indOff >= part.StartingOffset && indOff + 4 <= part.EndOffset)
                    {
                        byte[] indBuf = RawDiskEngine.ReadBytes(disk, indOff, (int)Math.Min(blockSize, part.EndOffset - indOff));
                        for (int p = 0; p + 4 <= indBuf.Length; p += 4)
                        {
                            uint blk = BitConverter.ToUInt32(indBuf, p);
                            if (blk == 0) continue;
                            long extOff = part.StartingOffset + (long)blk * blockSize;
                            if (extOff >= part.StartingOffset && extOff < part.EndOffset)
                                result.Add(new KeyValuePair<long, long>(extOff, Math.Min(blockSize, part.EndOffset - extOff)));
                        }
                    }
                }
            }

            return CoalesceExtents(result);
        }

        private static void ParseExt4ExtentNode(
            DiskInfo disk, PartitionEntry part, long blockSize,
            byte[] buf, int offset, int maxLen,
            List<KeyValuePair<long, long>> result, int depth)
        {
            if (depth > 5 || offset + 12 > maxLen) return;
            if (BitConverter.ToUInt16(buf, offset) != 0xF30A) return;

            ushort entries = BitConverter.ToUInt16(buf, offset + 2);
            ushort ehDepth = BitConverter.ToUInt16(buf, offset + 6);
            int maxEntries = Math.Min((int)entries, (maxLen - offset - 12) / 12);

            for (int i = 0; i < maxEntries; i++)
            {
                int rec = offset + 12 + i * 12;
                if (ehDepth == 0)
                {
                    ushort rawLen = BitConverter.ToUInt16(buf, rec + 4);
                    uint extBlocks = rawLen > 32768 ? (uint)(rawLen - 32768) : rawLen;
                    ushort startHi = BitConverter.ToUInt16(buf, rec + 6);
                    uint startLo = BitConverter.ToUInt32(buf, rec + 8);
                    long physBlock = ((long)startHi << 32) | startLo;
                    if (physBlock <= 0 || extBlocks == 0) continue;

                    long extOff = part.StartingOffset + physBlock * blockSize;
                    long extLen = (long)extBlocks * blockSize;
                    if (extOff >= part.StartingOffset && extOff < part.EndOffset)
                        result.Add(new KeyValuePair<long, long>(extOff, Math.Min(extLen, part.EndOffset - extOff)));
                }
                else
                {
                    uint leafLo = BitConverter.ToUInt32(buf, rec + 4);
                    ushort leafHi = BitConverter.ToUInt16(buf, rec + 8);
                    long childBlock = ((long)leafHi << 32) | leafLo;
                    if (childBlock <= 0) continue;

                    long childOff = part.StartingOffset + childBlock * blockSize;
                    if (childOff < part.StartingOffset || childOff + 12 > part.EndOffset) continue;

                    byte[] childBuf = RawDiskEngine.ReadBytes(disk, childOff, (int)Math.Min(blockSize, part.EndOffset - childOff));
                    ParseExt4ExtentNode(disk, part, blockSize, childBuf, 0, childBuf.Length, result, depth + 1);
                }
            }
        }

        private static List<KeyValuePair<long, long>> CoalesceExtents(List<KeyValuePair<long, long>> raw)
        {
            if (raw == null || raw.Count <= 1) return raw;
            var merged = new List<KeyValuePair<long, long>>(raw.Count);
            KeyValuePair<long, long> cur = raw[0];
            for (int i = 1; i < raw.Count; i++)
            {
                if ((cur.Key == -1L && raw[i].Key == -1L) || (cur.Key >= 0 && cur.Key + cur.Value == raw[i].Key))
                    cur = new KeyValuePair<long, long>(cur.Key, cur.Value + raw[i].Value);
                else
                {
                    merged.Add(cur);
                    cur = raw[i];
                }
            }
            merged.Add(cur);
            return merged;
        }

        private struct HfsExtent
        {
            public uint StartBlock;
            public uint BlockCount;
        }

        private class HfsCatalogItem
        {
            public uint CnId;
            public uint ParentId;
            public string Name;
            public bool IsDir;
            public ulong SizeBytes;
            public ushort Mode;
            public DateTime ModifiedUtc;
            public List<KeyValuePair<long, long>> Extents = new List<KeyValuePair<long, long>>();
        }

        private struct HfsOverflowRecord
        {
            public uint StartBlock;
            public HfsExtent[] Extents;
        }

        /// <summary>
        /// Parses an Apple HFS+ or HFSX Volume Header (including HFS wrapper embedding), Extents Overflow
        /// B-Tree, and Catalog B-Tree to reconstruct files, folders, fork extents, and resource paths.
        /// </summary>
        private static void ParseHfsPlusVolume(DiskInfo disk, PartitionEntry part, MountedReadOnlyVolume vol)
        {
            int headerReadLen = (int)Math.Min(131072L, part.PartitionLength);
            byte[] header = RawDiskEngine.ReadBytes(disk, part.StartingOffset, headerReadLen);
            if (header.Length < 1536)
                throw new InvalidOperationException("Partition too small or unreadable for HFS+ volume header.");

            long embedBase = 0;
            byte[] vh = null;

            if (MatchesAscii(header, 0x400, "H+") || MatchesAscii(header, 0x400, "HX"))
            {
                vh = new byte[Math.Min(512, header.Length - 0x400)];
                Array.Copy(header, 0x400, vh, 0, vh.Length);
            }
            else if (MatchesAscii(header, 0x400, "BD") &&
                     (MatchesAscii(header, 0x47C, "H+") || MatchesAscii(header, 0x47C, "HX")))
            {
                uint alBlkSize = ReadBE32(header, 0x414);
                ushort alBlSt = ReadBE16(header, 0x41C);
                ushort embedStart = ReadBE16(header, 0x47E);
                if (alBlkSize > 0)
                {
                    embedBase = (long)alBlSt * 512L + (long)embedStart * alBlkSize;
                    vh = RawDiskEngine.ReadBytes(disk, part.StartingOffset + embedBase + 0x400, 512);
                }
            }

            if (vh != null && vh.Length >= 512 && (MatchesAscii(vh, 0, "H+") || MatchesAscii(vh, 0, "HX")))
            {
                bool isHfsx = MatchesAscii(vh, 0, "HX");
                uint blockSize = ReadBE32(vh, 40);
                if (blockSize == 0) blockSize = 4096;
                uint totalBlocks = ReadBE32(vh, 44);
                uint freeBlocks = ReadBE32(vh, 48);

                vol.BlockSize = blockSize;
                vol.TotalBlocks = totalBlocks;
                vol.FreeBlocks = freeBlocks;

                ulong extLogicalSize = ReadBE64(vh, 192);
                var extExtents = ParseHfsExtents(vh, 208);

                ulong catLogicalSize = ReadBE64(vh, 272);
                var catExtents = ParseHfsExtents(vh, 288);

                vol.SuperblockSummary = string.Format(
                    "Apple {0} Volume Header | BlockSize={1} B | Blocks={2:N0} (Free={3:N0}) | Catalog={4} | Read-Only",
                    isHfsx ? "HFSX" : "HFS+", blockSize, totalBlocks, freeBlocks, ByteFormatter.FormatBytes((long)catLogicalSize));

                if (blockSize >= 512 && catLogicalSize >= 512 && catExtents[0].BlockCount > 0 && catExtents[0].StartBlock > 0)
                {
                    if (TryParseHfsPlusCatalogBTree(disk, part, vol, embedBase, blockSize, catLogicalSize, catExtents, extLogicalSize, extExtents, isHfsx))
                        return;
                }
            }
            else
            {
                vol.SuperblockSummary = "Apple HFS+ / HFSX Volume Header (@ +1024 B) | Read-Only";
            }

            // Fallback to synthetic WPROFS01 catalog if present on test images
            if (header.Length >= 0x3010 && MatchesAscii(header, 0x3000, "WPROFS01"))
            {
                var tree = ParseRoCatalogTree(header, 0x3000, part.StartingOffset);
                if (tree != null)
                {
                    vol.RootNode = tree;
                    return;
                }
            }

            vol.RootNode = BuildSuperblockOnlyTree(part, vol, header);
        }

        private static HfsExtent[] ParseHfsExtents(byte[] buf, int offset)
        {
            var extents = new HfsExtent[8];
            for (int i = 0; i < 8; i++)
            {
                int pos = offset + i * 8;
                if (pos + 8 > buf.Length) break;
                extents[i] = new HfsExtent
                {
                    StartBlock = ReadBE32(buf, pos),
                    BlockCount = ReadBE32(buf, pos + 4)
                };
            }
            return extents;
        }

        private static byte[] ReadHfsForkBytes(
            DiskInfo disk, long partStart, long partEnd, long embedBase, uint blockSize,
            IList<HfsExtent> extents, long forkOffset, int length)
        {
            if (length <= 0 || forkOffset < 0 || extents == null) return new byte[0];

            byte[] output = new byte[length];
            int copied = 0;
            long logicalCursor = forkOffset;

            long extLogicalStart = 0;
            for (int i = 0; i < extents.Count && copied < length; i++)
            {
                if (extents[i].BlockCount == 0) break;
                long extByteLen = (long)extents[i].BlockCount * blockSize;
                long extLogicalEnd = extLogicalStart + extByteLen;

                if (logicalCursor >= extLogicalStart && logicalCursor < extLogicalEnd)
                {
                    long inExtOffset = logicalCursor - extLogicalStart;
                    long physOffset = partStart + embedBase + (long)extents[i].StartBlock * blockSize + inExtOffset;
                    long availInExt = extByteLen - inExtOffset;
                    long availInPart = partEnd - physOffset;
                    int toRead = (int)Math.Min(length - copied, Math.Min(availInExt, availInPart));
                    if (toRead <= 0) break;

                    byte[] chunk = RawDiskEngine.ReadBytes(disk, physOffset, toRead);
                    int actual = Math.Min(toRead, chunk.Length);
                    if (actual <= 0) break;

                    Array.Copy(chunk, 0, output, copied, actual);
                    copied += actual;
                    logicalCursor += actual;
                    if (actual < toRead) break;
                }

                extLogicalStart = extLogicalEnd;
            }

            if (copied == length) return output;
            byte[] trimmed = new byte[copied];
            Array.Copy(output, trimmed, copied);
            return trimmed;
        }

        private static Dictionary<uint, List<HfsOverflowRecord>> ParseHfsExtentsOverflowBTree(
            DiskInfo disk, PartitionEntry part, long embedBase,
            uint blockSize, ulong extLogicalSize, HfsExtent[] extExtents)
        {
            var map = new Dictionary<uint, List<HfsOverflowRecord>>();
            if (extLogicalSize < 512 || extExtents == null || extExtents.Length == 0 || extExtents[0].BlockCount == 0)
                return map;

            byte[] node0 = ReadHfsForkBytes(disk, part.StartingOffset, part.EndOffset, embedBase, blockSize, extExtents, 0, 512);
            if (node0.Length < 44 || (sbyte)node0[8] != 1)
                return map;

            uint leafRecords = ReadBE32(node0, 20);
            uint firstLeafNode = ReadBE32(node0, 24);
            ushort nodeSize = ReadBE16(node0, 32);

            if (leafRecords == 0 || firstLeafNode == 0 || nodeSize < 512 || (nodeSize & (nodeSize - 1)) != 0)
                return map;

            var visited = new HashSet<uint>();
            uint curNode = firstLeafNode;
            int nodesWalked = 0;
            const int MaxExtLeafNodes = 4096;

            while (curNode != 0 && nodesWalked < MaxExtLeafNodes)
            {
                if (!visited.Add(curNode)) break;
                long nodeOffset = (long)curNode * nodeSize;
                if ((ulong)nodeOffset + nodeSize > extLogicalSize) break;

                byte[] nodeBuf = ReadHfsForkBytes(disk, part.StartingOffset, part.EndOffset, embedBase, blockSize, extExtents, nodeOffset, nodeSize);
                if (nodeBuf.Length < 14) break;

                uint fLink = ReadBE32(nodeBuf, 0);
                sbyte kind = (sbyte)nodeBuf[8];
                ushort numRecords = ReadBE16(nodeBuf, 10);
                nodesWalked++;

                if (kind == -1) // kBTLeafNode
                {
                    int offTableStart = nodeSize - 2 * (numRecords + 1);
                    for (int r = 0; r < numRecords; r++)
                    {
                        int offPos = nodeSize - 2 * (r + 1);
                        if (offPos < 14 || offPos + 2 > nodeBuf.Length) break;

                        int recOff = ReadBE16(nodeBuf, offPos);
                        if (recOff < 14 || recOff + 12 > nodeBuf.Length || (offTableStart > 14 && recOff >= offTableStart + 2))
                            continue;

                        ushort keyLen = ReadBE16(nodeBuf, recOff);
                        if (keyLen < 10 || recOff + 2 + keyLen > nodeBuf.Length) continue;

                        byte forkType = nodeBuf[recOff + 2];
                        if (forkType != 0) continue; // data fork only

                        uint fileId = ReadBE32(nodeBuf, recOff + 4);
                        uint startBlock = ReadBE32(nodeBuf, recOff + 8);

                        int dataOff = recOff + 2 + keyLen;
                        if ((dataOff & 1) != 0) dataOff++;
                        if (dataOff + 64 > nodeBuf.Length) continue;

                        List<HfsOverflowRecord> recList;
                        if (!map.TryGetValue(fileId, out recList))
                        {
                            recList = new List<HfsOverflowRecord>();
                            map[fileId] = recList;
                        }
                        recList.Add(new HfsOverflowRecord
                        {
                            StartBlock = startBlock,
                            Extents = ParseHfsExtents(nodeBuf, dataOff)
                        });
                    }
                }

                curNode = fLink;
            }

            return map;
        }

        private static List<HfsExtent> CombineHfsDataExtents(
            HfsExtent[] inlineExtents, uint fileId, Dictionary<uint, List<HfsOverflowRecord>> overflowMap)
        {
            var combined = new List<HfsExtent>();
            ulong coveredBlocks = 0;

            if (inlineExtents != null)
            {
                for (int i = 0; i < inlineExtents.Length; i++)
                {
                    if (inlineExtents[i].BlockCount == 0) break;
                    combined.Add(inlineExtents[i]);
                    coveredBlocks += inlineExtents[i].BlockCount;
                }
            }

            List<HfsOverflowRecord> overflowRecs;
            if (overflowMap != null && overflowMap.TryGetValue(fileId, out overflowRecs) && overflowRecs.Count > 0)
            {
                overflowRecs.Sort((a, b) => a.StartBlock.CompareTo(b.StartBlock));
                for (int r = 0; r < overflowRecs.Count; r++)
                {
                    var rec = overflowRecs[r];
                    if ((ulong)rec.StartBlock < coveredBlocks) continue;
                    ulong cursor = rec.StartBlock;
                    for (int e = 0; e < rec.Extents.Length; e++)
                    {
                        if (rec.Extents[e].BlockCount == 0) break;
                        combined.Add(rec.Extents[e]);
                        cursor += rec.Extents[e].BlockCount;
                    }
                    coveredBlocks = cursor;
                }
            }

            return combined;
        }

        private static bool TryParseHfsPlusCatalogBTree(
            DiskInfo disk, PartitionEntry part, MountedReadOnlyVolume vol, long embedBase,
            uint blockSize, ulong catLogicalSize, HfsExtent[] catInlineExtents,
            ulong extLogicalSize, HfsExtent[] extExtents, bool isHfsx)
        {
            var overflowMap = ParseHfsExtentsOverflowBTree(disk, part, embedBase, blockSize, extLogicalSize, extExtents);
            var catExtents = CombineHfsDataExtents(catInlineExtents, 4u, overflowMap); // kHFSCatalogFileID = 4

            byte[] node0 = ReadHfsForkBytes(disk, part.StartingOffset, part.EndOffset, embedBase, blockSize, catExtents, 0, 512);
            if (node0.Length < 44) return false;

            sbyte hdrKind = (sbyte)node0[8];
            if (hdrKind != 1) return false;

            ushort treeDepth = ReadBE16(node0, 14);
            uint rootNode = ReadBE32(node0, 16);
            uint leafRecords = ReadBE32(node0, 20);
            uint firstLeafNode = ReadBE32(node0, 24);
            ushort nodeSize = ReadBE16(node0, 32);

            if (nodeSize < 512 || (nodeSize & (nodeSize - 1)) != 0 || firstLeafNode == 0)
                return false;

            var items = new List<HfsCatalogItem>();
            var visitedNodes = new HashSet<uint>();
            uint curNode = firstLeafNode;
            int nodesWalked = 0;
            bool truncated = false;
            string rootVolumeName = null;

            const int MaxLeafNodes = 4096;
            const int MaxCatalogEntries = 32768;

            while (curNode != 0 && nodesWalked < MaxLeafNodes && items.Count < MaxCatalogEntries)
            {
                if (!visitedNodes.Add(curNode)) break;
                long nodeOffset = (long)curNode * nodeSize;
                if ((ulong)nodeOffset + nodeSize > catLogicalSize) break;

                byte[] nodeBuf = ReadHfsForkBytes(disk, part.StartingOffset, part.EndOffset, embedBase, blockSize, catExtents, nodeOffset, nodeSize);
                if (nodeBuf.Length < 14) break;

                uint fLink = ReadBE32(nodeBuf, 0);
                sbyte kind = (sbyte)nodeBuf[8];
                ushort numRecords = ReadBE16(nodeBuf, 10);
                nodesWalked++;

                if (kind == -1) // kBTLeafNode
                {
                    int offTableStart = nodeSize - 2 * (numRecords + 1);
                    for (int r = 0; r < numRecords; r++)
                    {
                        if (items.Count >= MaxCatalogEntries)
                        {
                            truncated = true;
                            break;
                        }

                        int offPos = nodeSize - 2 * (r + 1);
                        if (offPos < 14 || offPos + 2 > nodeBuf.Length) break;

                        int recOff = ReadBE16(nodeBuf, offPos);
                        if (recOff < 14 || recOff + 10 > nodeBuf.Length || (offTableStart > 14 && recOff >= offTableStart + 2))
                            continue;

                        ushort keyLen = ReadBE16(nodeBuf, recOff);
                        if (keyLen < 6 || recOff + 2 + keyLen > nodeBuf.Length) continue;

                        uint parentId = ReadBE32(nodeBuf, recOff + 2);
                        ushort nameChars = ReadBE16(nodeBuf, recOff + 6);
                        if (6 + nameChars * 2 > keyLen || recOff + 8 + nameChars * 2 > nodeBuf.Length) continue;

                        string name = nameChars > 0
                            ? Encoding.BigEndianUnicode.GetString(nodeBuf, recOff + 8, nameChars * 2).Replace('\0', '_')
                            : string.Empty;

                        int dataOff = recOff + 2 + keyLen;
                        if ((dataOff & 1) != 0) dataOff++;
                        if (dataOff + 2 > nodeBuf.Length) continue;

                        short recType = ReadBES16(nodeBuf, dataOff);
                        if (recType == 1 && dataOff + 88 <= nodeBuf.Length) // kHFSPlusFolderRecord
                        {
                            uint folderId = ReadBE32(nodeBuf, dataOff + 8);
                            uint modDate = ReadBE32(nodeBuf, dataOff + 16);
                            ushort mode = ReadBE16(nodeBuf, dataOff + 42);
                            if (mode == 0) mode = 0x41ED;

                            if (parentId == 1 && folderId == 2)
                            {
                                if (!string.IsNullOrEmpty(name))
                                    rootVolumeName = name;
                            }
                            else if (!string.IsNullOrEmpty(name))
                            {
                                items.Add(new HfsCatalogItem
                                {
                                    CnId = folderId,
                                    ParentId = parentId,
                                    Name = name,
                                    IsDir = true,
                                    SizeBytes = 0,
                                    Mode = (ushort)((mode & 0x0FFF) | 0x4000),
                                    ModifiedUtc = FromHfsPlusSeconds(modDate)
                                });
                            }
                        }
                        else if (recType == 2 && dataOff + 168 <= nodeBuf.Length) // kHFSPlusFileRecord
                        {
                            if (string.IsNullOrEmpty(name)) continue;
                            uint fileId = ReadBE32(nodeBuf, dataOff + 8);
                            uint modDate = ReadBE32(nodeBuf, dataOff + 16);
                            ushort mode = ReadBE16(nodeBuf, dataOff + 42);
                            if (mode == 0) mode = 0x81A4;

                            ulong logicalSize = ReadBE64(nodeBuf, dataOff + 88);
                            var inlineExtents = ParseHfsExtents(nodeBuf, dataOff + 104);
                            var allExtents = CombineHfsDataExtents(inlineExtents, fileId, overflowMap);

                            var item = new HfsCatalogItem
                            {
                                CnId = fileId,
                                ParentId = parentId,
                                Name = name,
                                IsDir = false,
                                SizeBytes = logicalSize,
                                Mode = (ushort)((mode & 0x0FFF) | 0x8000),
                                ModifiedUtc = FromHfsPlusSeconds(modDate)
                            };

                            for (int e = 0; e < allExtents.Count; e++)
                            {
                                long extAbsOff = part.StartingOffset + embedBase + (long)allExtents[e].StartBlock * blockSize;
                                long extByteLen = (long)allExtents[e].BlockCount * blockSize;
                                if (extAbsOff >= part.StartingOffset && extAbsOff < part.EndOffset && extByteLen > 0)
                                    item.Extents.Add(new KeyValuePair<long, long>(extAbsOff, Math.Min(extByteLen, part.EndOffset - extAbsOff)));
                            }
                            item.Extents = CoalesceExtents(item.Extents);

                            items.Add(item);
                        }
                    }
                }

                if (nodesWalked >= MaxLeafNodes || items.Count >= MaxCatalogEntries)
                {
                    if (fLink != 0) truncated = true;
                    break;
                }
                curNode = fLink;
            }

            if (string.IsNullOrEmpty(rootVolumeName) && items.Count == 0)
                return false;

            if (!string.IsNullOrEmpty(rootVolumeName))
            {
                vol.Label = rootVolumeName;
                if (string.IsNullOrEmpty(part.Label))
                    part.Label = rootVolumeName;
            }

            var root = new VirtualFsNode
            {
                Name = "/",
                FullPath = "/",
                IsDirectory = true,
                InodeOrObjectId = 2,
                PermissionsString = "drwxr-xr-x"
            };

            var dirNodes = new Dictionary<uint, VirtualFsNode>();
            dirNodes[2u] = root;

            foreach (var it in items)
            {
                if (!it.IsDir) continue;
                dirNodes[it.CnId] = new VirtualFsNode
                {
                    Name = it.Name,
                    FullPath = it.Name,
                    IsDirectory = true,
                    InodeOrObjectId = it.CnId,
                    PermissionsString = FormatUnixMode(it.Mode),
                    ModifiedUtc = it.ModifiedUtc
                };
            }

            foreach (var it in items)
            {
                VirtualFsNode parentNode;
                if (!dirNodes.TryGetValue(it.ParentId, out parentNode))
                {
                    if (it.ParentId == 2)
                        parentNode = root;
                    else
                        continue;
                }

                if (it.IsDir)
                {
                    VirtualFsNode dirNode;
                    if (dirNodes.TryGetValue(it.CnId, out dirNode))
                        parentNode.Children.Add(dirNode);
                }
                else
                {
                    var fileNode = new VirtualFsNode
                    {
                        Name = it.Name,
                        FullPath = it.Name,
                        IsDirectory = false,
                        SizeBytes = (long)Math.Min((ulong)long.MaxValue, it.SizeBytes),
                        InodeOrObjectId = it.CnId,
                        PermissionsString = FormatUnixMode(it.Mode),
                        ModifiedUtc = it.ModifiedUtc,
                        ContentOffset = it.Extents.Count > 0 ? it.Extents[0].Key : 0,
                        ContentLength = (long)Math.Min((ulong)long.MaxValue, it.SizeBytes),
                        ContentExtents = it.Extents.Count > 0 ? it.Extents : null
                    };
                    parentNode.Children.Add(fileNode);
                }
            }

            SortAndAssignPaths(root, "/");

            string summaryInfo = string.Format(
                "Apple {0} Catalog B-Tree Summary (Read-Only)\n" +
                "Volume Name     : {1}\n" +
                "Volume UUID     : {2}\n" +
                "Partition       : {3} ({4})\n" +
                "Block Size      : {5} bytes\n" +
                "B-Tree Depth    : {6}\n" +
                "Root / FirstLeaf: node {7} / node {8}\n" +
                "Node Size       : {9} bytes\n" +
                "Header Leaf Recs: {10:N0}\n" +
                "Loaded Entries  : {11:N0}{12}\n",
                isHfsx ? "HFSX" : "HFS+",
                string.IsNullOrEmpty(vol.Label) ? "(unnamed)" : vol.Label,
                string.IsNullOrEmpty(vol.UUID) ? "(none)" : vol.UUID,
                part.ShortDeviceName,
                ByteFormatter.FormatBytes(part.PartitionLength),
                blockSize,
                treeDepth,
                rootNode,
                firstLeafNode,
                nodeSize,
                leafRecords,
                items.Count,
                truncated ? " (capped at browse preview limit)" : "");
            byte[] summaryBytes = Encoding.UTF8.GetBytes(summaryInfo);
            root.Children.Add(new VirtualFsNode
            {
                Name = ".hfsplus_volume_info.txt",
                FullPath = "/.hfsplus_volume_info.txt",
                IsDirectory = false,
                SizeBytes = summaryBytes.Length,
                InodeOrObjectId = 0,
                PermissionsString = "r--r--r--",
                InlineContent = summaryBytes
            });

            vol.SuperblockSummary = string.Format(
                "Apple {0} ('{1}') | BlockSize={2} B | Catalog NodeSize={3} B | Loaded={4:N0} entries | Read-Only",
                isHfsx ? "HFSX" : "HFS+",
                string.IsNullOrEmpty(vol.Label) ? "Untitled" : vol.Label,
                blockSize, nodeSize, items.Count);
            vol.RootNode = root;
            return true;
        }

        private static void SortAndAssignPaths(VirtualFsNode dir, string parentPath, int depth = 0)
        {
            if (depth > 48) return;
            dir.Children.Sort((a, b) =>
            {
                if (a.IsDirectory != b.IsDirectory)
                    return a.IsDirectory ? -1 : 1;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            foreach (var child in dir.Children)
            {
                child.FullPath = (parentPath == "/") ? ("/" + child.Name) : (parentPath + "/" + child.Name);
                if (child.IsDirectory)
                    SortAndAssignPaths(child, child.FullPath, depth + 1);
            }
        }

        private sealed class BtrfsChunkMap
        {
            public ulong LogicalStart;
            public ulong Length;
            public ulong PhysicalStart;
        }

        private sealed class BtrfsInodeMeta
        {
            public long Size;
            public uint Mode;
            public uint MtimeSec;
        }

        /// <summary>
        /// Parses a Btrfs superblock (<c>_BHRfS_M</c> at <c>+0x10040</c>), decodes <c>sys_chunk_array</c>,
        /// walks root/FS B-trees to collect inodes, directory items, and inline/regular <c>EXTENT_DATA</c>.
        /// </summary>
        private static void ParseBtrfsVolume(DiskInfo disk, PartitionEntry part, MountedReadOnlyVolume vol)
        {
            if (part.PartitionLength < 0x15000L)
            {
                ParseCatalogOrMetadataVolume(disk, part, vol);
                return;
            }

            byte[] sb = RawDiskEngine.ReadBytes(disk, part.StartingOffset + 0x10000L, 4096);
            if (sb.Length < 0x350 || !MatchesAscii(sb, 0x40, "_BHRfS_M"))
            {
                ParseCatalogOrMetadataVolume(disk, part, vol);
                return;
            }

            ulong rootLogical = BitConverter.ToUInt64(sb, 0x50);
            uint sectorSize = BitConverter.ToUInt32(sb, 0x90);
            uint nodeSize = BitConverter.ToUInt32(sb, 0x94);
            uint sysChunkSize = BitConverter.ToUInt32(sb, 0xA0);
            if (sectorSize < 512 || sectorSize > 65536) sectorSize = 4096;
            if (nodeSize < 4096 || nodeSize > 65536) nodeSize = 16384;

            vol.BlockSize = sectorSize;
            long totalBytes = BitConverter.ToInt64(sb, 0x70);
            long bytesUsed = BitConverter.ToInt64(sb, 0x78);
            if (totalBytes > 0)
            {
                vol.TotalBlocks = totalBytes / sectorSize;
                vol.FreeBlocks = Math.Max(0, (totalBytes - bytesUsed) / sectorSize);
            }

            // Parse sys_chunk_array at +0x32B
            var chunks = new List<BtrfsChunkMap>();
            int scEnd = Math.Min(sb.Length, 0x32B + (int)Math.Min(2048U, sysChunkSize));
            int p = 0x32B;
            while (p + 17 + 48 <= scEnd)
            {
                byte keyType = sb[p + 8];
                ulong chunkLog = BitConverter.ToUInt64(sb, p + 9);
                p += 17;
                if (keyType != 228 || p + 48 > scEnd) break;
                ulong chunkLen = BitConverter.ToUInt64(sb, p + 0);
                ushort numStripes = BitConverter.ToUInt16(sb, p + 44);
                int recBytes = 48 + numStripes * 32;
                if (numStripes >= 1 && p + 48 + 32 <= scEnd)
                {
                    ulong physOff = BitConverter.ToUInt64(sb, p + 48 + 8);
                    chunks.Add(new BtrfsChunkMap { LogicalStart = chunkLog, Length = chunkLen, PhysicalStart = physOff });
                }
                p += recBytes;
            }

            // Locate FS tree root (owner 5) directly or via ROOT_TREE (owner 1) -> ROOT_ITEM (objectid 5, type 132)
            long startNodePhys = ResolveBtrfsLogical(chunks, rootLogical, part.PartitionLength);
            if (startNodePhys < 0 || startNodePhys + nodeSize > part.PartitionLength)
            {
                ParseCatalogOrMetadataVolume(disk, part, vol);
                return;
            }

            byte[] firstNode = RawDiskEngine.ReadBytes(disk, part.StartingOffset + startNodePhys, (int)nodeSize);
            if (firstNode.Length < 101)
            {
                ParseCatalogOrMetadataVolume(disk, part, vol);
                return;
            }

            ulong treeOwner = BitConverter.ToUInt64(firstNode, 0x58);
            ulong fsRootLogical = rootLogical;
            if (treeOwner == 1UL)
            {
                ulong foundFs = FindBtrfsRootItemLogical(disk, part, chunks, firstNode, (int)nodeSize, 5UL, 0);
                if (foundFs == 0)
                {
                    ParseCatalogOrMetadataVolume(disk, part, vol);
                    return;
                }
                fsRootLogical = foundFs;
            }

            var inodes = new Dictionary<ulong, BtrfsInodeMeta>();
            var dirLinks = new List<KeyValuePair<ulong, KeyValuePair<string, ulong>>>();
            var dirTypes = new Dictionary<string, byte>(StringComparer.Ordinal);
            var inlineData = new Dictionary<ulong, byte[]>();
            var regExtents = new Dictionary<ulong, List<KeyValuePair<long, long>>>();
            int leavesRead = 0;

            CollectBtrfsTreeLeaves(disk, part, chunks, fsRootLogical, (int)nodeSize, inodes, dirLinks, dirTypes, inlineData, regExtents, ref leavesRead, 0);

            if (inodes.Count == 0 && dirLinks.Count == 0)
            {
                ParseCatalogOrMetadataVolume(disk, part, vol);
                return;
            }

            var nodesByObjId = new Dictionary<ulong, VirtualFsNode>();
            foreach (var kv in inodes)
            {
                uint mode = kv.Value.Mode;
                bool isDir = (mode & 0xF000U) == 0x4000U;
                var n = new VirtualFsNode
                {
                    InodeOrObjectId = kv.Key,
                    IsDirectory = isDir,
                    SizeBytes = isDir ? 0 : kv.Value.Size,
                    ContentLength = isDir ? 0 : kv.Value.Size,
                    PermissionsString = FormatUnixMode((ushort)(mode & 0xFFFFU)),
                    ModifiedUtc = FromUnixSeconds(kv.Value.MtimeSec)
                };

                byte[] inl;
                List<KeyValuePair<long, long>> exts;
                if (!isDir && inlineData.TryGetValue(kv.Key, out inl))
                {
                    n.InlineContent = inl;
                    n.ContentLength = 0;
                }
                else if (!isDir && regExtents.TryGetValue(kv.Key, out exts) && exts.Count > 0)
                {
                    n.ContentExtents = exts;
                    n.ContentOffset = exts[0].Key;
                }

                nodesByObjId[kv.Key] = n;
            }

            VirtualFsNode root;
            if (!nodesByObjId.TryGetValue(256UL, out root))
            {
                root = new VirtualFsNode
                {
                    Name = "/",
                    FullPath = "/",
                    IsDirectory = true,
                    InodeOrObjectId = 256UL,
                    PermissionsString = "drwxr-xr-x"
                };
                nodesByObjId[256UL] = root;
            }
            else
            {
                root.Name = "/";
                root.FullPath = "/";
                root.IsDirectory = true;
            }

            var attached = new HashSet<string>(StringComparer.Ordinal);
            foreach (var link in dirLinks)
            {
                ulong parentId = link.Key;
                string name = link.Value.Key;
                ulong childId = link.Value.Value;
                if (string.IsNullOrEmpty(name) || name == "." || name == "..") continue;
                string dedupKey = parentId + ":" + name;
                if (!attached.Add(dedupKey)) continue;

                VirtualFsNode parentNode;
                VirtualFsNode childNode;
                if (!nodesByObjId.TryGetValue(parentId, out parentNode) || !parentNode.IsDirectory) continue;
                if (!nodesByObjId.TryGetValue(childId, out childNode))
                {
                    byte ftype;
                    dirTypes.TryGetValue(dedupKey, out ftype);
                    childNode = new VirtualFsNode
                    {
                        InodeOrObjectId = childId,
                        IsDirectory = (ftype == 2),
                        PermissionsString = (ftype == 2) ? "drwxr-xr-x" : "-rw-r--r--"
                    };
                    nodesByObjId[childId] = childNode;
                }

                childNode.Name = name;
                parentNode.Children.Add(childNode);
            }

            SortAndAssignPaths(root, "/");
            vol.SuperblockSummary = string.Format(
                "Btrfs CoW ('{0}') | Sector={1} B | Node={2} B | Items={3} | Read-Only",
                string.IsNullOrEmpty(vol.Label) ? "btrfs" : vol.Label,
                sectorSize, nodeSize, inodes.Count);
            vol.RootNode = root;
        }

        private static long ResolveBtrfsLogical(List<BtrfsChunkMap> chunks, ulong logical, long partLength)
        {
            for (int i = 0; i < chunks.Count; i++)
            {
                var c = chunks[i];
                if (logical >= c.LogicalStart && logical < c.LogicalStart + c.Length)
                {
                    ulong phys = c.PhysicalStart + (logical - c.LogicalStart);
                    if (phys < (ulong)partLength) return (long)phys;
                }
            }
            if (logical < (ulong)partLength) return (long)logical;
            return -1;
        }

        private static ulong FindBtrfsRootItemLogical(DiskInfo disk, PartitionEntry part, List<BtrfsChunkMap> chunks, byte[] node, int nodeSize, ulong targetRootObj, int depth)
        {
            if (depth > 6 || node.Length < 101) return 0;
            uint nritems = BitConverter.ToUInt32(node, 0x60);
            byte level = node[0x64];
            if (level == 0)
            {
                for (uint i = 0; i < nritems; i++)
                {
                    int hOff = 101 + (int)i * 25;
                    if (hOff + 25 > node.Length) break;
                    ulong obj = BitConverter.ToUInt64(node, hOff);
                    byte kType = node[hOff + 8];
                    uint dataRel = BitConverter.ToUInt32(node, hOff + 17);
                    uint dataSize = BitConverter.ToUInt32(node, hOff + 21);
                    int pOff = 101 + (int)dataRel;
                    if (obj == targetRootObj && kType == 132 && dataSize >= 184 && pOff + 184 <= node.Length)
                        return BitConverter.ToUInt64(node, pOff + 176);
                }
                return 0;
            }

            // Internal node: btrfs_key_ptr entries are 33 bytes each (17B key + 8B blockptr + 8B gen)
            int childIdx = -1;
            for (uint i = 0; i < nritems; i++)
            {
                int kOff = 101 + (int)i * 33;
                if (kOff + 33 > node.Length) break;
                ulong obj = BitConverter.ToUInt64(node, kOff);
                if (obj <= targetRootObj) childIdx = (int)i;
                else break;
            }
            if (childIdx < 0 && nritems > 0) childIdx = 0;
            if (childIdx >= 0)
            {
                ulong ptrLogical = BitConverter.ToUInt64(node, 101 + childIdx * 33 + 17);
                long ptrPhys = ResolveBtrfsLogical(chunks, ptrLogical, part.PartitionLength);
                if (ptrPhys >= 0 && ptrPhys + nodeSize <= part.PartitionLength)
                {
                    byte[] child = RawDiskEngine.ReadBytes(disk, part.StartingOffset + ptrPhys, nodeSize);
                    return FindBtrfsRootItemLogical(disk, part, chunks, child, nodeSize, targetRootObj, depth + 1);
                }
            }
            return 0;
        }

        private static void CollectBtrfsTreeLeaves(
            DiskInfo disk, PartitionEntry part, List<BtrfsChunkMap> chunks,
            ulong nodeLogical, int nodeSize,
            Dictionary<ulong, BtrfsInodeMeta> inodes,
            List<KeyValuePair<ulong, KeyValuePair<string, ulong>>> dirLinks,
            Dictionary<string, byte> dirTypes,
            Dictionary<ulong, byte[]> inlineData,
            Dictionary<ulong, List<KeyValuePair<long, long>>> regExtents,
            ref int leavesRead, int depth)
        {
            if (depth > 6 || leavesRead >= 64) return;
            long phys = ResolveBtrfsLogical(chunks, nodeLogical, part.PartitionLength);
            if (phys < 0 || phys + nodeSize > part.PartitionLength) return;

            byte[] node = RawDiskEngine.ReadBytes(disk, part.StartingOffset + phys, nodeSize);
            if (node.Length < 101) return;
            uint nritems = BitConverter.ToUInt32(node, 0x60);
            byte level = node[0x64];

            if (level > 0)
            {
                for (uint i = 0; i < nritems && leavesRead < 64; i++)
                {
                    int kOff = 101 + (int)i * 33;
                    if (kOff + 33 > node.Length) break;
                    ulong childLogical = BitConverter.ToUInt64(node, kOff + 17);
                    CollectBtrfsTreeLeaves(disk, part, chunks, childLogical, nodeSize, inodes, dirLinks, dirTypes, inlineData, regExtents, ref leavesRead, depth + 1);
                }
                return;
            }

            leavesRead++;
            for (uint i = 0; i < nritems; i++)
            {
                int hOff = 101 + (int)i * 25;
                if (hOff + 25 > node.Length) break;
                ulong objId = BitConverter.ToUInt64(node, hOff);
                byte kType = node[hOff + 8];
                uint dataRel = BitConverter.ToUInt32(node, hOff + 17);
                uint dataSize = BitConverter.ToUInt32(node, hOff + 21);
                int pOff = 101 + (int)dataRel;
                if (pOff < 101 || pOff + dataSize > node.Length) continue;

                if (kType == 1 && dataSize >= 148)
                {
                    inodes[objId] = new BtrfsInodeMeta
                    {
                        Size = (long)BitConverter.ToUInt64(node, pOff + 16),
                        Mode = BitConverter.ToUInt32(node, pOff + 52),
                        MtimeSec = (uint)BitConverter.ToUInt64(node, pOff + 136)
                    };
                }
                else if ((kType == 84 || kType == 96) && dataSize >= 30)
                {
                    int cur = pOff;
                    int end = pOff + (int)dataSize;
                    while (cur + 30 <= end)
                    {
                        ulong childObj = BitConverter.ToUInt64(node, cur);
                        ushort dLen = BitConverter.ToUInt16(node, cur + 25);
                        ushort nLen = BitConverter.ToUInt16(node, cur + 27);
                        byte ft = node[cur + 29];
                        cur += 30;
                        if (cur + nLen + dLen > end) break;
                        string name = Encoding.UTF8.GetString(node, cur, nLen);
                        cur += nLen + dLen;
                        if (childObj != 0 && !string.IsNullOrEmpty(name))
                        {
                            dirLinks.Add(new KeyValuePair<ulong, KeyValuePair<string, ulong>>(objId, new KeyValuePair<string, ulong>(name, childObj)));
                            dirTypes[objId + ":" + name] = ft;
                        }
                    }
                }
                else if (kType == 108 && dataSize >= 21)
                {
                    byte compression = node[pOff + 16];
                    byte extType = node[pOff + 20];
                    if (extType == 0 && compression == 0)
                    {
                        int inlLen = (int)dataSize - 21;
                        if (inlLen >= 0)
                        {
                            byte[] raw = new byte[inlLen];
                            Array.Copy(node, pOff + 21, raw, 0, inlLen);
                            inlineData[objId] = raw;
                        }
                    }
                    else if (extType == 1 && compression == 0 && dataSize >= 53)
                    {
                        ulong diskBytenr = BitConverter.ToUInt64(node, pOff + 21);
                        ulong extOffInDisk = BitConverter.ToUInt64(node, pOff + 37);
                        ulong numBytes = BitConverter.ToUInt64(node, pOff + 45);
                        if (diskBytenr > 0 && numBytes > 0)
                        {
                            long extPhys = ResolveBtrfsLogical(chunks, diskBytenr + extOffInDisk, part.PartitionLength);
                            if (extPhys >= 0)
                            {
                                List<KeyValuePair<long, long>> list;
                                if (!regExtents.TryGetValue(objId, out list))
                                {
                                    list = new List<KeyValuePair<long, long>>();
                                    regExtents[objId] = list;
                                }
                                list.Add(new KeyValuePair<long, long>(part.StartingOffset + extPhys, (long)numBytes));
                            }
                        }
                    }
                }
            }
        }

        private sealed class XfsGeom
        {
            public uint BlockSize;
            public ushort InodeSize;
            public uint AgBlocks;
            public byte InopbLog;
            public byte AgBlkLog;
            public bool HasFtype;
        }

        /// <summary>
        /// Parses a Big-Endian XFS superblock (<c>XFSB</c>), converts AG/block-relative 64-bit inode numbers
        /// to partition offsets, and reads <c>IN</c> shortform directories plus <c>XD2B</c>/<c>XDB3</c> extent blocks.
        /// </summary>
        private static void ParseXfsVolume(DiskInfo disk, PartitionEntry part, MountedReadOnlyVolume vol)
        {
            if (part.PartitionLength < 4096)
            {
                ParseCatalogOrMetadataVolume(disk, part, vol);
                return;
            }

            byte[] sb = RawDiskEngine.ReadBytes(disk, part.StartingOffset, 4096);
            if (sb.Length < 152 || !MatchesAscii(sb, 0, "XFSB"))
            {
                ParseCatalogOrMetadataVolume(disk, part, vol);
                return;
            }

            uint blockSize = ReadBE32(sb, 4);
            bool isValidBE = blockSize >= 512 && blockSize <= 65536 && (blockSize & (blockSize - 1)) == 0;
            if (!isValidBE)
            {
                ParseCatalogOrMetadataVolume(disk, part, vol);
                return;
            }

            ulong dblocks = ReadBE64(sb, 8);
            ulong fdblocks = ReadBE64(sb, 144);
            ulong rootIno = ReadBE64(sb, 56);
            uint agBlocks = ReadBE32(sb, 84);
            ushort versionNum = ReadBE16(sb, 100);
            ushort inodeSize = ReadBE16(sb, 104);
            if (inodeSize < 256 || inodeSize > 2048) inodeSize = 512;
            byte inopbLog = sb[123];
            byte agBlkLog = sb[124];
            uint featIncompat = sb.Length >= 220 ? ReadBE32(sb, 216) : 0U;
            bool hasFtype = ((versionNum & 0x000F) == 5 && (featIncompat & 0x04U) != 0) || ((versionNum & 0x000F) != 5 && (versionNum & 0x0200) != 0 && sb.Length >= 220 && (featIncompat & 0x04U) != 0);

            vol.BlockSize = blockSize;
            vol.TotalBlocks = (long)dblocks;
            vol.FreeBlocks = (long)Math.Min(dblocks, fdblocks);

            var geom = new XfsGeom
            {
                BlockSize = blockSize,
                InodeSize = inodeSize,
                AgBlocks = agBlocks,
                InopbLog = inopbLog,
                AgBlkLog = agBlkLog,
                HasFtype = hasFtype
            };

            var visited = new HashSet<ulong>();
            var root = BuildXfsNodeRecursive(disk, part, geom, rootIno, "/", visited, 0);
            if (root == null)
            {
                ParseCatalogOrMetadataVolume(disk, part, vol);
                return;
            }

            root.Name = "/";
            root.FullPath = "/";
            root.IsDirectory = true;
            SortAndAssignPaths(root, "/");
            vol.SuperblockSummary = string.Format(
                "XFS v{0} ('{1}') | BlockSize={2} B | InodeSize={3} B | RootIno={4} | Read-Only",
                (versionNum & 0x000F) == 5 ? 5 : 4,
                string.IsNullOrEmpty(vol.Label) ? "xfs" : vol.Label,
                blockSize, inodeSize, rootIno);
            vol.RootNode = root;
        }

        private static long ConvertXfsInoToRelOffset(XfsGeom g, ulong ino)
        {
            if (g.AgBlkLog == 0 || g.InopbLog == 0 || g.AgBlocks == 0)
                return (long)ino * g.InodeSize;
            int totalLog = g.AgBlkLog + g.InopbLog;
            ulong ag = (totalLog < 63) ? (ino >> totalLog) : 0UL;
            ulong agbno = (ino >> g.InopbLog) & ((1UL << g.AgBlkLog) - 1UL);
            ulong idx = ino & ((1UL << g.InopbLog) - 1UL);
            ulong block = ag * g.AgBlocks + agbno;
            return (long)(block * g.BlockSize + idx * g.InodeSize);
        }

        private static List<KeyValuePair<long, long>> ParseXfsBmbtExtents(PartitionEntry part, XfsGeom g, byte[] inodeBuf, int dataForkOff, uint nextents, long maxFileSize)
        {
            var runs = new List<KeyValuePair<long, long>>();
            long remaining = maxFileSize;
            for (uint i = 0; i < nextents && remaining > 0; i++)
            {
                int rOff = dataForkOff + (int)i * 16;
                if (rOff + 16 > inodeBuf.Length) break;
                ulong hi = ReadBE64(inodeBuf, rOff);
                ulong lo = ReadBE64(inodeBuf, rOff + 8);
                ulong startBlock = ((hi & 0x1FFUL) << 43) | (lo >> 21);
                ulong blockCount = lo & 0x1FFFFFUL;
                ulong fsbMask = (1UL << g.AgBlkLog) - 1UL;
                ulong physBlock = (g.AgBlkLog > 0 && g.AgBlocks > 0)
                    ? ((startBlock >> g.AgBlkLog) * g.AgBlocks + (startBlock & fsbMask))
                    : startBlock;
                long physOff = part.StartingOffset + (long)(physBlock * g.BlockSize);
                long extBytes = (long)(blockCount * g.BlockSize);
                long used = Math.Min(remaining, extBytes);
                if (physOff >= part.StartingOffset && physOff < part.EndOffset && used > 0)
                {
                    runs.Add(new KeyValuePair<long, long>(physOff, used));
                    remaining -= used;
                }
            }
            return runs;
        }

        private static VirtualFsNode BuildXfsNodeRecursive(DiskInfo disk, PartitionEntry part, XfsGeom g, ulong ino, string name, HashSet<ulong> visited, int depth)
        {
            if (depth > 24 || !visited.Add(ino)) return null;
            long relOff = ConvertXfsInoToRelOffset(g, ino);
            if (relOff < 0 || relOff + g.InodeSize > part.PartitionLength) return null;

            byte[] buf = RawDiskEngine.ReadBytes(disk, part.StartingOffset + relOff, g.InodeSize);
            if (buf.Length < 100 || ReadBE16(buf, 0) != 0x494E) return null;

            ushort mode = ReadBE16(buf, 2);
            byte version = buf[4];
            byte format = buf[5];
            uint mtimeSec = ReadBE32(buf, 40);
            long size = (long)ReadBE64(buf, 56);
            uint nextents = ReadBE32(buf, 76);
            int dataForkOff = (version >= 3) ? 176 : 100;
            if (dataForkOff > buf.Length) return null;

            bool isDir = (mode & 0xF000U) == 0x4000U;
            var node = new VirtualFsNode
            {
                Name = name,
                IsDirectory = isDir,
                SizeBytes = isDir ? 0 : size,
                ContentLength = isDir ? 0 : size,
                InodeOrObjectId = ino,
                PermissionsString = FormatUnixMode(mode),
                ModifiedUtc = FromUnixSeconds(mtimeSec)
            };

            if (!isDir)
            {
                if (format == 1 && size >= 0)
                {
                    int len = (int)Math.Min(size, buf.Length - dataForkOff);
                    if (len > 0)
                    {
                        byte[] inlineBytes = new byte[len];
                        Array.Copy(buf, dataForkOff, inlineBytes, 0, len);
                        node.InlineContent = inlineBytes;
                        node.ContentLength = 0;
                    }
                }
                else if (format == 2 && nextents > 0 && size > 0)
                {
                    var exts = ParseXfsBmbtExtents(part, g, buf, dataForkOff, nextents, size);
                    if (exts.Count > 0)
                    {
                        node.ContentExtents = exts;
                        node.ContentOffset = exts[0].Key;
                    }
                }
                return node;
            }

            if (format == 1 && dataForkOff + 6 <= buf.Length)
            {
                byte count = buf[dataForkOff];
                byte i8Count = buf[dataForkOff + 1];
                bool use64 = i8Count > 0;
                int cur = dataForkOff + (use64 ? 10 : 6);
                for (int i = 0; i < count && cur < buf.Length; i++)
                {
                    int namelen = buf[cur++];
                    if (cur + 2 + namelen > buf.Length) break;
                    cur += 2; // 2-byte shortform offset
                    string childName = Encoding.UTF8.GetString(buf, cur, namelen);
                    cur += namelen;
                    if (g.HasFtype)
                    {
                        if (cur >= buf.Length) break;
                        cur++;
                    }
                    int inoBytes = use64 ? 8 : 4;
                    if (cur + inoBytes > buf.Length) break;
                    ulong childIno = use64 ? ReadBE64(buf, cur) : (ulong)ReadBE32(buf, cur);
                    cur += inoBytes;
                    if (!string.IsNullOrEmpty(childName) && childName != "." && childName != "..")
                    {
                        var childNode = BuildXfsNodeRecursive(disk, part, g, childIno, childName, visited, depth + 1);
                        if (childNode != null) node.Children.Add(childNode);
                    }
                }
            }
            else if (format == 2 && nextents > 0)
            {
                var dirRuns = ParseXfsBmbtExtents(part, g, buf, dataForkOff, nextents, Math.Max(g.BlockSize, size));
                foreach (var run in dirRuns)
                {
                    byte[] blk = RawDiskEngine.ReadBytes(disk, run.Key, (int)Math.Min(65536L, run.Value));
                    if (blk.Length < 16) continue;
                    uint magic = ReadBE32(blk, 0);
                    int hdrSize = (magic == 0x58444233U || magic == 0x58444433U) ? 64 : 16; // XDB3/XDD3 vs XD2B/XD2D
                    int pos = hdrSize;
                    while (pos + 12 <= blk.Length)
                    {
                        ushort tag = ReadBE16(blk, pos);
                        if (tag == 0xFFFF)
                        {
                            ushort freeLen = ReadBE16(blk, pos + 2);
                            if (freeLen < 4) break;
                            pos += ((freeLen + 7) / 8) * 8;
                            continue;
                        }
                        if (pos + 9 > blk.Length) break;
                        ulong childIno = ReadBE64(blk, pos);
                        byte nLen = blk[pos + 8];
                        if (nLen == 0 || pos + 9 + nLen > blk.Length) break;
                        string childName = Encoding.UTF8.GetString(blk, pos + 9, nLen);
                        int step = 9 + nLen + (g.HasFtype ? 1 : 0) + 2; // + tag(2)
                        step = ((step + 7) / 8) * 8;
                        pos += Math.Max(8, step);
                        if (!string.IsNullOrEmpty(childName) && childName != "." && childName != "..")
                        {
                            var childNode = BuildXfsNodeRecursive(disk, part, g, childIno, childName, visited, depth + 1);
                            if (childNode != null) node.Children.Add(childNode);
                        }
                    }
                }
            }

            return node;
        }

        /// <summary>
        /// Parses an ISO9660 Primary Volume Descriptor (and optional Joliet SVD) at <c>+0x8000</c>
        /// to recursively build the optical/live ISO directory tree with optional Rock Ridge <c>NM</c> names.
        /// </summary>
        private static void ParseIso9660Volume(DiskInfo disk, PartitionEntry part, MountedReadOnlyVolume vol)
        {
            if (part.PartitionLength < 0x9800L)
            {
                ParseCatalogOrMetadataVolume(disk, part, vol);
                return;
            }

            byte[] pvd = RawDiskEngine.ReadBytes(disk, part.StartingOffset + 0x8000L, 4096);
            if (pvd.Length < 2048 || pvd[0] != 1 || !MatchesAscii(pvd, 1, "CD001"))
            {
                ParseCatalogOrMetadataVolume(disk, part, vol);
                return;
            }

            ushort logicalBlockSize = BitConverter.ToUInt16(pvd, 0x80);
            if (logicalBlockSize < 512 || logicalBlockSize > 4096) logicalBlockSize = 2048;
            uint volSpaceBlocks = BitConverter.ToUInt32(pvd, 0x50);
            uint rootLba = BitConverter.ToUInt32(pvd, 0x9C + 2);
            uint rootLen = BitConverter.ToUInt32(pvd, 0x9C + 10);

            // Prefer Joliet SVD at +0x8800 if present and non-empty
            bool isJoliet = false;
            if (pvd.Length >= 2048 + 190 && pvd[2048] == 2 && MatchesAscii(pvd, 2049, "CD001") && pvd[2048 + 88] == (byte)'%')
            {
                uint jLba = BitConverter.ToUInt32(pvd, 2048 + 0x9C + 2);
                uint jLen = BitConverter.ToUInt32(pvd, 2048 + 0x9C + 10);
                if (jLba >= 16 && jLen >= 34 && (long)jLba * logicalBlockSize < part.PartitionLength)
                {
                    rootLba = jLba;
                    rootLen = jLen;
                    isJoliet = true;
                }
            }

            vol.BlockSize = logicalBlockSize;
            vol.TotalBlocks = volSpaceBlocks;
            vol.FreeBlocks = 0;

            var root = new VirtualFsNode
            {
                Name = "/",
                FullPath = "/",
                IsDirectory = true,
                InodeOrObjectId = rootLba,
                PermissionsString = "dr-xr-xr-x"
            };

            var visited = new HashSet<uint>();
            ParseIsoDirectoryExtent(disk, part, root, rootLba, rootLen, logicalBlockSize, isJoliet, visited, 0);
            if (root.Children.Count == 0)
            {
                ParseCatalogOrMetadataVolume(disk, part, vol);
                return;
            }

            SortAndAssignPaths(root, "/");
            vol.SuperblockSummary = string.Format(
                "ISO9660 {0} ('{1}') | LogicalBlock={2} B | Sectors={3:N0} | Read-Only",
                isJoliet ? "Joliet" : "ECMA-119",
                string.IsNullOrEmpty(vol.Label) ? "CDROM" : vol.Label,
                logicalBlockSize, volSpaceBlocks);
            vol.RootNode = root;
        }

        private static void ParseIsoDirectoryExtent(
            DiskInfo disk, PartitionEntry part, VirtualFsNode parent,
            uint dirLba, uint dirLen, ushort blockSize, bool isJoliet,
            HashSet<uint> visited, int depth)
        {
            if (depth > 24 || dirLen == 0 || !visited.Add(dirLba)) return;
            long relOff = (long)dirLba * blockSize;
            int readLen = (int)Math.Min(1048576L, Math.Min((long)dirLen, part.PartitionLength - relOff));
            if (relOff < 0 || readLen < 34) return;

            byte[] dir = RawDiskEngine.ReadBytes(disk, part.StartingOffset + relOff, readLen);
            int pos = 0;
            while (pos < dir.Length)
            {
                byte recLen = dir[pos];
                if (recLen == 0)
                {
                    int nextSec = ((pos / blockSize) + 1) * blockSize;
                    if (nextSec <= pos || nextSec >= dir.Length) break;
                    pos = nextSec;
                    continue;
                }
                if (pos + recLen > dir.Length || recLen < 34) break;

                uint extLba = BitConverter.ToUInt32(dir, pos + 2);
                uint dataLen = BitConverter.ToUInt32(dir, pos + 10);
                byte flags = dir[pos + 25];
                int nameLen = dir[pos + 32];
                if (33 + nameLen > recLen) { pos += recLen; continue; }

                if (nameLen == 1 && (dir[pos + 33] == 0 || dir[pos + 33] == 1))
                {
                    pos += recLen;
                    continue;
                }

                string name = isJoliet && nameLen >= 2
                    ? Encoding.BigEndianUnicode.GetString(dir, pos + 33, nameLen & ~1).TrimEnd('\0')
                    : Encoding.ASCII.GetString(dir, pos + 33, nameLen).TrimEnd('\0');

                // Check optional Rock Ridge NM entry in System Use area
                int suStart = 33 + nameLen + ((nameLen & 1) == 0 ? 1 : 0);
                if (!isJoliet && suStart + 5 <= recLen)
                {
                    int su = pos + suStart;
                    int recEnd = pos + recLen;
                    var rrName = new StringBuilder();
                    while (su + 4 <= recEnd)
                    {
                        byte suLen = dir[su + 2];
                        if (suLen < 4 || su + suLen > recEnd) break;
                        if (dir[su] == (byte)'N' && dir[su + 1] == (byte)'M' && suLen > 5)
                            rrName.Append(Encoding.UTF8.GetString(dir, su + 5, suLen - 5));
                        su += suLen;
                    }
                    if (rrName.Length > 0)
                        name = rrName.ToString();
                }

                int semiIdx = name.IndexOf(';');
                if (semiIdx > 0) name = name.Substring(0, semiIdx);

                bool isDir = (flags & 0x02) != 0;
                var child = new VirtualFsNode
                {
                    Name = name,
                    IsDirectory = isDir,
                    SizeBytes = isDir ? 0 : (long)dataLen,
                    InodeOrObjectId = extLba,
                    PermissionsString = isDir ? "dr-xr-xr-x" : "-r--r--r--",
                    ContentOffset = isDir ? 0 : (part.StartingOffset + (long)extLba * blockSize),
                    ContentLength = isDir ? 0 : (long)dataLen
                };
                parent.Children.Add(child);

                if (isDir)
                    ParseIsoDirectoryExtent(disk, part, child, extLba, dataLen, blockSize, isJoliet, visited, depth + 1);

                pos += recLen;
            }
        }

        private sealed class ApfsInodeInfo
        {
            public ulong Ino;
            public ulong ParentId;
            public ulong PrivateId;
            public DateTime ModTime;
            public uint BsdFlags;
            public ushort Mode;
            public ulong UncompressedSize;
            public ulong DstreamSize;
            public bool HasDstream;
            public string Name;
        }

        private sealed class ApfsDirLink
        {
            public ulong ParentId;
            public string Name;
            public ulong ChildId;
            public byte DtType;
            public DateTime DateAdded;
        }

        private sealed class ApfsExtentEntry
        {
            public ulong LogicalAddr;
            public long LengthBytes;
            public ulong PhysBlock;
        }

        private sealed class ApfsParsedVolume
        {
            public string VolumeName;
            public string VolumeUuid;
            public ulong AllocBlocks;
            public bool IsEncrypted;
            public int ParsedInodeCount;
            public int ParsedDirLinkCount;
            public VirtualFsNode RootNode;
        }

        /// <summary>
        /// Navigates multi-level APFS Object Map (<c>OMAP</c>) B-trees to translate virtual object IDs
        /// (at or below a checkpoint transaction <c>xid</c>) into physical block addresses.
        /// </summary>
        private sealed class ApfsOmapReader
        {
            private readonly DiskInfo _disk;
            private readonly PartitionEntry _part;
            private readonly int _blockSize;
            private readonly ulong _totalBlocks;
            private readonly ulong _rootTreePhys;
            private readonly Dictionary<ulong, ulong> _bestPaddr = new Dictionary<ulong, ulong>();
            private readonly Dictionary<ulong, ulong> _bestXid = new Dictionary<ulong, ulong>();
            private readonly HashSet<ulong> _visitedNodes = new HashSet<ulong>();

            public ApfsOmapReader(DiskInfo disk, PartitionEntry part, int blockSize, ulong totalBlocks, ulong omapPhysBlock)
            {
                _disk = disk;
                _part = part;
                _blockSize = blockSize;
                _totalBlocks = totalBlocks;
                _rootTreePhys = ResolveOmapTreeRoot(omapPhysBlock);
            }

            public bool HasRoot { get { return _rootTreePhys > 0 && _rootTreePhys < _totalBlocks; } }

            private ulong ResolveOmapTreeRoot(ulong omapPhysBlock)
            {
                if (omapPhysBlock == 0 || omapPhysBlock >= _totalBlocks) return 0;
                byte[] blk = ReadApfsBlock(_disk, _part, omapPhysBlock, _blockSize);
                if (blk == null || blk.Length < 0x38) return 0;
                uint objType = BitConverter.ToUInt32(blk, 0x18) & 0xFFFFU;
                if (objType == 0x000BU)
                {
                    ulong treeOid = BitConverter.ToUInt64(blk, 0x30);
                    return (treeOid > 0 && treeOid < _totalBlocks) ? treeOid : 0;
                }
                if (objType == 0x0002U || objType == 0x0003U)
                    return omapPhysBlock;
                return 0;
            }

            public ulong Lookup(ulong targetOid)
            {
                if (targetOid == 0) return 0;
                ulong cached;
                if (_bestPaddr.TryGetValue(targetOid, out cached))
                    return cached;
                if (!HasRoot) return 0;
                SearchNode(_rootTreePhys, targetOid, 0);
                return _bestPaddr.TryGetValue(targetOid, out cached) ? cached : 0;
            }

            private void SearchNode(ulong nodePhys, ulong targetOid, int depth)
            {
                if (depth > 10 || nodePhys == 0 || nodePhys >= _totalBlocks) return;
                byte[] node = ReadApfsBlock(_disk, _part, nodePhys, _blockSize);
                if (node == null || node.Length < 0x38) return;

                ushort btnFlags = BitConverter.ToUInt16(node, 0x20);
                ushort btnLevel = BitConverter.ToUInt16(node, 0x22);
                uint btnNkeys = BitConverter.ToUInt32(node, 0x24);
                ushort tocOff = BitConverter.ToUInt16(node, 0x28);
                ushort tocLen = BitConverter.ToUInt16(node, 0x2A);

                bool isRoot = (btnFlags & 0x0001) != 0;
                bool isFixed = (btnFlags & 0x0004) != 0;
                int hdrLen = ((btnFlags & 0x0010) != 0) ? 24 : 56;
                int tocStart = hdrLen + tocOff;
                int keyAreaStart = tocStart + tocLen;
                int valAreaEnd = isRoot ? (_blockSize - 40) : _blockSize;
                if (tocStart < 24 || keyAreaStart > valAreaEnd || valAreaEnd > node.Length) return;

                int tocEntryLen = isFixed ? 4 : 8;
                int count = (int)Math.Min((long)btnNkeys, (keyAreaStart - tocStart) / tocEntryLen);
                if (count <= 0) return;

                if (btnLevel == 0)
                {
                    if (!_visitedNodes.Add(nodePhys)) return;
                    for (int i = 0; i < count; i++)
                    {
                        int kPos, kLen, vPos, vLen;
                        if (!ReadApfsTocEntry(node, tocStart + i * tocEntryLen, isFixed, 16, 16, keyAreaStart, valAreaEnd, out kPos, out kLen, out vPos, out vLen))
                            continue;
                        if (kLen < 16 || vLen < 16) continue;
                        ulong okOid = BitConverter.ToUInt64(node, kPos);
                        ulong okXid = BitConverter.ToUInt64(node, kPos + 8);
                        uint ovFlags = BitConverter.ToUInt32(node, vPos);
                        ulong ovPaddr = BitConverter.ToUInt64(node, vPos + 8);

                        ulong prevXid;
                        bool hasPrev = _bestXid.TryGetValue(okOid, out prevXid);
                        if (!hasPrev || okXid >= prevXid)
                        {
                            _bestXid[okOid] = okXid;
                            if ((ovFlags & 1U) == 0 && ovPaddr > 0 && ovPaddr < _totalBlocks)
                                _bestPaddr[okOid] = ovPaddr;
                            else
                                _bestPaddr.Remove(okOid);
                        }
                    }
                    return;
                }

                // Internal OMAP node: select last child whose key.ok_oid <= targetOid
                int chosen = -1;
                ulong chosenKeyOid = 0;
                for (int i = 0; i < count; i++)
                {
                    int kPos, kLen, vPos, vLen;
                    if (!ReadApfsTocEntry(node, tocStart + i * tocEntryLen, isFixed, 16, 8, keyAreaStart, valAreaEnd, out kPos, out kLen, out vPos, out vLen))
                        continue;
                    if (kLen < 8 || vLen < 8) continue;
                    ulong kOid = BitConverter.ToUInt64(node, kPos);
                    if (kOid <= targetOid)
                    {
                        chosen = i;
                        chosenKeyOid = kOid;
                    }
                    else
                    {
                        break;
                    }
                }
                if (chosen < 0) return;

                ulong childPhys = ReadChildBlockFromToc(node, tocStart + chosen * tocEntryLen, isFixed, keyAreaStart, valAreaEnd);
                if (childPhys > 0 && childPhys < _totalBlocks)
                    SearchNode(childPhys, targetOid, depth + 1);

                if (!_bestPaddr.ContainsKey(targetOid) && chosen > 0 && chosenKeyOid == targetOid)
                {
                    ulong altPhys = ReadChildBlockFromToc(node, tocStart + (chosen - 1) * tocEntryLen, isFixed, keyAreaStart, valAreaEnd);
                    if (altPhys > 0 && altPhys < _totalBlocks)
                        SearchNode(altPhys, targetOid, depth + 1);
                }
            }

            private static ulong ReadChildBlockFromToc(byte[] node, int tOff, bool isFixed, int keyAreaStart, int valAreaEnd)
            {
                int kPos, kLen, vPos, vLen;
                if (!ReadApfsTocEntry(node, tOff, isFixed, 16, 8, keyAreaStart, valAreaEnd, out kPos, out kLen, out vPos, out vLen))
                    return 0;
                if (vLen < 8 || vPos + 8 > valAreaEnd) return 0;
                return BitConverter.ToUInt64(node, vPos);
            }
        }

        private static byte[] ReadApfsBlock(DiskInfo disk, PartitionEntry part, ulong blockNum, int blockSize)
        {
            long relOff = (long)blockNum * blockSize;
            if (relOff < 0 || relOff + blockSize > part.PartitionLength)
                return null;
            byte[] buf = RawDiskEngine.ReadBytes(disk, part.StartingOffset + relOff, blockSize);
            return (buf != null && buf.Length == blockSize) ? buf : null;
        }

        private static void AccumulateApfsFletcher64(byte[] buf, int offset, int len, out ulong sum1, out ulong sum2)
        {
            sum1 = 0;
            sum2 = 0;
            if (buf == null || offset < 0 || len < 16 || offset + len > buf.Length) return;
            int end = offset + (len & ~3);
            for (int i = offset + 8; i < end; i += 4)
            {
                sum1 = (sum1 + BitConverter.ToUInt32(buf, i)) % 0xFFFFFFFFUL;
                sum2 = (sum2 + sum1) % 0xFFFFFFFFUL;
            }
        }

        internal static ulong ComputeApfsFletcher64(byte[] buf, int offset, int len)
        {
            if (buf == null || offset < 0 || len < 16 || offset + len > buf.Length) return 0;
            ulong sum1, sum2;
            AccumulateApfsFletcher64(buf, offset, len, out sum1, out sum2);
            ulong ck0 = 0xFFFFFFFFUL - ((sum1 + sum2) % 0xFFFFFFFFUL);
            ulong ck1 = 0xFFFFFFFFUL - ((sum1 + ck0) % 0xFFFFFFFFUL);
            return (ck1 << 32) | ck0;
        }

        internal static void WriteApfsFletcher64(byte[] buf, int offset, int len)
        {
            ulong cksum = ComputeApfsFletcher64(buf, offset, len);
            BitConverter.GetBytes(cksum).CopyTo(buf, offset);
        }

        internal static bool VerifyApfsFletcher64(byte[] buf, int len)
        {
            if (buf == null || len < 16 || len > buf.Length) return false;
            ulong stored = BitConverter.ToUInt64(buf, 0);
            ulong sum1, sum2;
            AccumulateApfsFletcher64(buf, 0, len, out sum1, out sum2);
            if (stored == 0 && (sum1 != 0 || sum2 != 0)) return false;
            ulong ck0 = stored & 0xFFFFFFFFUL;
            ulong ck1 = stored >> 32;
            return ((sum1 + ck0 + ck1) % 0xFFFFFFFFUL) == 0 &&
                   ((sum2 + sum1 + ck0) % 0xFFFFFFFFUL) == 0;
        }

        private static bool ReadApfsTocEntry(
            byte[] node, int tOff, bool isFixed, int fixedKeyLen, int fixedValLen,
            int keyAreaStart, int valAreaEnd,
            out int kPos, out int kLen, out int vPos, out int vLen)
        {
            kPos = kLen = vPos = vLen = 0;
            if (tOff < 0 || tOff + (isFixed ? 4 : 8) > keyAreaStart) return false;

            ushort kRel = BitConverter.ToUInt16(node, tOff);
            ushort vRel;
            if (isFixed)
            {
                vRel = BitConverter.ToUInt16(node, tOff + 2);
                kLen = fixedKeyLen;
                vLen = fixedValLen;
            }
            else
            {
                kLen = BitConverter.ToUInt16(node, tOff + 2);
                vRel = BitConverter.ToUInt16(node, tOff + 4);
                vLen = BitConverter.ToUInt16(node, tOff + 6);
            }

            if (kRel == 0xFFFF || vRel == 0xFFFF || vRel == 0) return false;
            kPos = keyAreaStart + kRel;
            vPos = valAreaEnd - vRel;
            if (kPos < keyAreaStart || kPos + kLen > valAreaEnd) return false;
            if (vPos < keyAreaStart || vPos + vLen > valAreaEnd) return false;
            return true;
        }

        private static DateTime FromUnixNanoseconds(ulong ns)
        {
            if (ns == 0)
                return new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            try
            {
                return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks((long)(ns / 100UL));
            }
            catch
            {
                return new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            }
        }

        /// <summary>
        /// Parses an Apple APFS Container (<c>NXSB</c>) or Volume (<c>APSB</c>), resolving checkpoints,
        /// Container/Volume OMAP B-trees, File-System B-trees, multi-extent file runs, and inline <c>decmpfs</c>.
        /// </summary>
        private static void ParseApfsVolume(DiskInfo disk, PartitionEntry part, MountedReadOnlyVolume vol)
        {
            if (part.PartitionLength < 4096L)
            {
                ParseCatalogOrMetadataVolume(disk, part, vol);
                return;
            }

            byte[] blk0 = RawDiskEngine.ReadBytes(disk, part.StartingOffset, 4096);
            if (blk0.Length < 0x100)
            {
                ParseCatalogOrMetadataVolume(disk, part, vol);
                return;
            }

            bool isContainer = MatchesAscii(blk0, 0x20, "NXSB");
            bool isDirectVol = MatchesAscii(blk0, 0x20, "APSB");
            if (!isContainer && !isDirectVol)
            {
                ParseCatalogOrMetadataVolume(disk, part, vol);
                return;
            }

            int blockSize = 4096;
            if (isContainer)
            {
                uint rawBs = BitConverter.ToUInt32(blk0, 0x24);
                if (rawBs >= 4096U && rawBs <= 65536U && (rawBs & (rawBs - 1U)) == 0U)
                    blockSize = (int)rawBs;
                if (blockSize > blk0.Length && part.PartitionLength >= blockSize)
                    blk0 = RawDiskEngine.ReadBytes(disk, part.StartingOffset, blockSize);
            }

            vol.BlockSize = blockSize;
            ulong totalBlocks = (ulong)(part.PartitionLength / blockSize);
            if (totalBlocks == 0)
            {
                ParseCatalogOrMetadataVolume(disk, part, vol);
                return;
            }

            if (isDirectVol)
            {
                byte[] directBlk = (blk0.Length >= blockSize) ? blk0 : ReadApfsBlock(disk, part, 0, blockSize);
                if (directBlk == null)
                {
                    ParseCatalogOrMetadataVolume(disk, part, vol);
                    return;
                }
                var parsedDirect = ParseSingleApfsVolume(disk, part, directBlk, blockSize, totalBlocks);
                if (parsedDirect == null || parsedDirect.RootNode == null)
                {
                    ParseCatalogOrMetadataVolume(disk, part, vol);
                    return;
                }
                FinalizeMountedApfsTree(vol, part, blockSize, totalBlocks, new List<ApfsParsedVolume> { parsedDirect });
                return;
            }

            // Collect NXSB checkpoint superblocks sorted by descending o_xid, falling back to blk0
            var nxsbCandidates = CollectApfsNxsbCandidates(disk, part, blk0, blockSize, totalBlocks);
            List<ApfsParsedVolume> parsedVolumes = null;

            for (int c = 0; c < nxsbCandidates.Count; c++)
            {
                byte[] nxsb = nxsbCandidates[c];
                ulong nxBlockCount = BitConverter.ToUInt64(nxsb, 0x28);
                if (nxBlockCount > 0 && nxBlockCount < totalBlocks)
                    vol.TotalBlocks = (long)nxBlockCount;
                else
                    vol.TotalBlocks = (long)totalBlocks;

                var candidateVols = ResolveApfsVolumesFromContainer(disk, part, nxsb, blockSize, totalBlocks);
                if (candidateVols.Count == 0) continue;

                bool anyTree = false;
                for (int v = 0; v < candidateVols.Count; v++)
                {
                    if (candidateVols[v].ParsedInodeCount > 0 || candidateVols[v].ParsedDirLinkCount > 0)
                    {
                        anyTree = true;
                        break;
                    }
                }
                if (anyTree || parsedVolumes == null)
                {
                    parsedVolumes = candidateVols;
                    if (anyTree) break;
                }
            }

            if (parsedVolumes == null || parsedVolumes.Count == 0)
            {
                ParseCatalogOrMetadataVolume(disk, part, vol);
                return;
            }

            bool hasAnyContent = false;
            bool anyEncrypted = false;
            for (int v = 0; v < parsedVolumes.Count; v++)
            {
                if (parsedVolumes[v].RootNode != null && (parsedVolumes[v].ParsedInodeCount > 0 || parsedVolumes[v].ParsedDirLinkCount > 0))
                    hasAnyContent = true;
                if (parsedVolumes[v].IsEncrypted)
                    anyEncrypted = true;
            }

            if (!hasAnyContent)
            {
                ParseCatalogOrMetadataVolume(disk, part, vol);
                if (anyEncrypted)
                    vol.SuperblockSummary = "Apple APFS Encrypted Container (FileVault / Hardware Crypto) | Read-Only Metadata";
                return;
            }

            FinalizeMountedApfsTree(vol, part, blockSize, totalBlocks, parsedVolumes);
        }

        private static List<byte[]> CollectApfsNxsbCandidates(DiskInfo disk, PartitionEntry part, byte[] blk0, int blockSize, ulong totalBlocks)
        {
            var candidates = new List<byte[]>();
            var seenXid = new HashSet<ulong>();

            uint descBlocksRaw = BitConverter.ToUInt32(blk0, 0x68);
            ulong descBase = (ulong)BitConverter.ToInt64(blk0, 0x70);
            uint descCount = descBlocksRaw & 0x7FFFFFFFU;

            if ((descBlocksRaw & 0x80000000U) == 0 && descBase > 0 && descBase < totalBlocks && descCount > 0)
            {
                ulong scanBlocks = Math.Min((ulong)descCount, Math.Min(512UL, totalBlocks - descBase));
                const int batchBlocks = 64;
                for (ulong offset = 0; offset < scanBlocks; offset += batchBlocks)
                {
                    int n = (int)Math.Min(batchBlocks, scanBlocks - offset);
                    long diskOff = part.StartingOffset + (long)((descBase + offset) * (ulong)blockSize);
                    byte[] batch = RawDiskEngine.ReadBytes(disk, diskOff, n * blockSize);
                    for (int b = 0; b < n && (b + 1) * blockSize <= batch.Length; b++)
                    {
                        int off = b * blockSize;
                        if (!MatchesAscii(batch, off + 0x20, "NXSB")) continue;
                        byte[] single = new byte[blockSize];
                        Array.Copy(batch, off, single, 0, blockSize);
                        if (!VerifyApfsFletcher64(single, blockSize)) continue;
                        ulong xid = BitConverter.ToUInt64(single, 0x10);
                        if (seenXid.Add(xid))
                            candidates.Add(single);
                    }
                }
            }

            ulong blk0Xid = BitConverter.ToUInt64(blk0, 0x10);
            if (seenXid.Add(blk0Xid))
                candidates.Add(blk0);

            candidates.Sort(delegate(byte[] a, byte[] b)
            {
                ulong xa = BitConverter.ToUInt64(a, 0x10);
                ulong xb = BitConverter.ToUInt64(b, 0x10);
                return xb.CompareTo(xa);
            });
            return candidates;
        }

        /// <summary>
        /// Inspects an APFS container (<c>NXSB</c>) or volume (<c>APSB</c>) to extract the preferred
        /// non-System-internal volume label, UUID, and allocated container used bytes for UI display.
        /// </summary>
        internal static bool TryReadApfsVolumeMetadata(
            DiskInfo disk, PartitionEntry part, byte[] header0,
            out string volLabel, out string volUuid, out long volUsedBytes)
        {
            volLabel = string.Empty;
            volUuid = string.Empty;
            volUsedBytes = -1;
            if (disk == null || part == null || header0 == null || header0.Length < 0x100) return false;

            try
            {
                bool isContainer = MatchesAscii(header0, 0x20, "NXSB");
                bool isDirectVol = MatchesAscii(header0, 0x20, "APSB");
                if (!isContainer && !isDirectVol) return false;

                int blockSize = 4096;
                if (isContainer)
                {
                    uint rawBs = BitConverter.ToUInt32(header0, 0x24);
                    if (rawBs >= 4096U && rawBs <= 65536U && (rawBs & (rawBs - 1U)) == 0U)
                        blockSize = (int)rawBs;
                }

                ulong totalBlocks = (ulong)(part.PartitionLength / blockSize);
                if (totalBlocks == 0) return false;

                List<byte[]> apsbBlocks;
                if (isDirectVol)
                {
                    byte[] direct = (header0.Length >= blockSize) ? header0 : ReadApfsBlock(disk, part, 0, blockSize);
                    apsbBlocks = (direct != null) ? new List<byte[]> { direct } : new List<byte[]>();
                }
                else
                {
                    byte[] blk0 = (header0.Length >= blockSize) ? header0 : RawDiskEngine.ReadBytes(disk, part.StartingOffset, (int)Math.Min((long)blockSize, part.PartitionLength));
                    if (blk0.Length < blockSize) return false;
                    var nxsbCandidates = CollectApfsNxsbCandidates(disk, part, blk0, blockSize, totalBlocks);
                    apsbBlocks = new List<byte[]>();
                    for (int c = 0; c < nxsbCandidates.Count; c++)
                    {
                        apsbBlocks = FindApfsVolumeSuperblocks(disk, part, nxsbCandidates[c], blockSize, totalBlocks);
                        if (apsbBlocks.Count > 0) break;
                    }
                }

                if (apsbBlocks.Count == 0) return false;

                ulong sumAlloc = 0;
                string preferredLabel = string.Empty;
                string preferredUuid = string.Empty;
                bool preferredIsSysVol = true;
                for (int i = 0; i < apsbBlocks.Count; i++)
                {
                    byte[] apsb = apsbBlocks[i];
                    if (apsb == null || apsb.Length < 0x3C0) continue;
                    sumAlloc += BitConverter.ToUInt64(apsb, 0x58);

                    int end = 0x2C0;
                    while (end < 0x3C0 && apsb[end] != 0) end++;
                    string name = (end > 0x2C0) ? Encoding.UTF8.GetString(apsb, 0x2C0, end - 0x2C0).Trim() : string.Empty;

                    byte[] uBytes = new byte[16];
                    Array.Copy(apsb, 0xF0, uBytes, 0, 16);
                    bool nonZeroUuid = false;
                    for (int k = 0; k < 16; k++)
                    {
                        if (uBytes[k] != 0) { nonZeroUuid = true; break; }
                    }
                    string uuid = nonZeroUuid ? FormatRfcUuidFromBytes(uBytes) : string.Empty;

                    bool isSysVol =
                        string.Equals(name, "Preboot", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, "Recovery", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, "VM", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, "Update", StringComparison.OrdinalIgnoreCase);

                    if (!string.IsNullOrEmpty(name) && (string.IsNullOrEmpty(preferredLabel) || (preferredIsSysVol && !isSysVol)))
                    {
                        preferredLabel = name;
                        preferredIsSysVol = isSysVol;
                        if (!string.IsNullOrEmpty(uuid)) preferredUuid = uuid;
                    }
                    if (string.IsNullOrEmpty(preferredUuid) && !string.IsNullOrEmpty(uuid))
                        preferredUuid = uuid;
                }

                volLabel = preferredLabel;
                volUuid = preferredUuid;
                if (sumAlloc > 0)
                {
                    long used = (long)(sumAlloc * (ulong)blockSize);
                    if (used > 0 && used <= part.PartitionLength)
                        volUsedBytes = used;
                }
                return !string.IsNullOrEmpty(volLabel) || !string.IsNullOrEmpty(volUuid) || volUsedBytes >= 0;
            }
            catch
            {
                return false;
            }
        }

        private static List<byte[]> FindApfsVolumeSuperblocks(
            DiskInfo disk, PartitionEntry part, byte[] nxsb, int blockSize, ulong totalBlocks)
        {
            var apsbBlocks = new List<byte[]>();
            var seenVolOid = new HashSet<ulong>();

            ulong nxOmapOid = BitConverter.ToUInt64(nxsb, 0xA0);
            var containerOmap = new ApfsOmapReader(disk, part, blockSize, totalBlocks, nxOmapOid);

            uint maxFs = BitConverter.ToUInt32(nxsb, 0xB4);
            if (maxFs == 0 || maxFs > 100U) maxFs = 100U;
            int maxSlot = Math.Min((int)maxFs, (nxsb.Length - 0xB8) / 8);

            for (int i = 0; i < maxSlot; i++)
            {
                ulong fsOid = BitConverter.ToUInt64(nxsb, 0xB8 + i * 8);
                if (fsOid == 0) continue;

                ulong fsPaddr = containerOmap.Lookup(fsOid);
                if (fsPaddr == 0 && fsOid < totalBlocks)
                    fsPaddr = fsOid;
                if (fsPaddr == 0 || fsPaddr >= totalBlocks) continue;

                byte[] apsb = ReadApfsBlock(disk, part, fsPaddr, blockSize);
                if (apsb != null && MatchesAscii(apsb, 0x20, "APSB") && seenVolOid.Add(fsOid))
                    apsbBlocks.Add(apsb);
            }

            // Fallback scan of checkpoint data area / early container blocks if container OMAP yielded nothing
            if (apsbBlocks.Count == 0)
            {
                ulong dataBase = (ulong)BitConverter.ToInt64(nxsb, 0x78);
                uint dataBlocksRaw = BitConverter.ToUInt32(nxsb, 0x6C);
                var bestByOid = new Dictionary<ulong, byte[]>();
                var ranges = new List<KeyValuePair<ulong, ulong>>();
                if ((dataBlocksRaw & 0x80000000U) == 0 && dataBase > 0 && dataBase < totalBlocks)
                    ranges.Add(new KeyValuePair<ulong, ulong>(dataBase, Math.Min(256UL, totalBlocks - dataBase)));
                ranges.Add(new KeyValuePair<ulong, ulong>(1UL, Math.Min(128UL, totalBlocks - 1UL)));

                foreach (var rng in ranges)
                {
                    const int batchBlocks = 64;
                    for (ulong offset = 0; offset < rng.Value; offset += batchBlocks)
                    {
                        int n = (int)Math.Min(batchBlocks, rng.Value - offset);
                        long diskOff = part.StartingOffset + (long)((rng.Key + offset) * (ulong)blockSize);
                        byte[] batch = RawDiskEngine.ReadBytes(disk, diskOff, n * blockSize);
                        for (int b = 0; b < n && (b + 1) * blockSize <= batch.Length; b++)
                        {
                            int off = b * blockSize;
                            if (!MatchesAscii(batch, off + 0x20, "APSB")) continue;
                            byte[] single = new byte[blockSize];
                            Array.Copy(batch, off, single, 0, blockSize);
                            if (!VerifyApfsFletcher64(single, blockSize)) continue;
                            ulong oOid = BitConverter.ToUInt64(single, 0x08);
                            ulong oXid = BitConverter.ToUInt64(single, 0x10);
                            byte[] existing;
                            if (!bestByOid.TryGetValue(oOid, out existing) || oXid >= BitConverter.ToUInt64(existing, 0x10))
                                bestByOid[oOid] = single;
                        }
                    }
                    if (bestByOid.Count > 0) break;
                }

                foreach (var kv in bestByOid)
                    apsbBlocks.Add(kv.Value);
            }

            return apsbBlocks;
        }

        private static List<ApfsParsedVolume> ResolveApfsVolumesFromContainer(
            DiskInfo disk, PartitionEntry part, byte[] nxsb, int blockSize, ulong totalBlocks)
        {
            var volumes = new List<ApfsParsedVolume>();
            var apsbBlocks = FindApfsVolumeSuperblocks(disk, part, nxsb, blockSize, totalBlocks);
            for (int i = 0; i < apsbBlocks.Count; i++)
            {
                var parsed = ParseSingleApfsVolume(disk, part, apsbBlocks[i], blockSize, totalBlocks);
                if (parsed != null)
                    volumes.Add(parsed);
            }
            return volumes;
        }

        private static ApfsParsedVolume ParseSingleApfsVolume(
            DiskInfo disk, PartitionEntry part, byte[] apsb, int blockSize, ulong totalBlocks)
        {
            if (apsb == null || apsb.Length < 0x3C0 || !MatchesAscii(apsb, 0x20, "APSB"))
                return null;

            int nameEnd = 0x2C0;
            while (nameEnd < 0x3C0 && nameEnd < apsb.Length && apsb[nameEnd] != 0)
                nameEnd++;
            string volName = (nameEnd > 0x2C0)
                ? Encoding.UTF8.GetString(apsb, 0x2C0, nameEnd - 0x2C0).Trim()
                : string.Empty;

            byte[] uuidBytes = new byte[16];
            Array.Copy(apsb, 0xF0, uuidBytes, 0, 16);
            string volUuid = FormatRfcUuidFromBytes(uuidBytes);

            ulong allocBlocks = BitConverter.ToUInt64(apsb, 0x58);
            ulong apfsOmapOid = BitConverter.ToUInt64(apsb, 0x80);
            ulong rootTreeOid = BitConverter.ToUInt64(apsb, 0x88);
            ulong fsFlags = BitConverter.ToUInt64(apsb, 0x108);
            bool isEncrypted = (fsFlags & 1UL) == 0;

            var parsed = new ApfsParsedVolume
            {
                VolumeName = volName,
                VolumeUuid = volUuid,
                AllocBlocks = allocBlocks,
                IsEncrypted = isEncrypted
            };

            var volOmap = new ApfsOmapReader(disk, part, blockSize, totalBlocks, apfsOmapOid);
            ulong rootTreePhys = volOmap.Lookup(rootTreeOid);
            if (rootTreePhys == 0 && rootTreeOid > 0 && rootTreeOid < totalBlocks)
                rootTreePhys = rootTreeOid;
            if (rootTreePhys == 0 || rootTreePhys >= totalBlocks)
                return parsed;

            var inodes = new Dictionary<ulong, ApfsInodeInfo>();
            var dirLinks = new List<ApfsDirLink>();
            var extentsByPrivId = new Dictionary<ulong, List<ApfsExtentEntry>>();
            var symlinksByIno = new Dictionary<ulong, string>();
            var inlineByIno = new Dictionary<ulong, byte[]>();
            var decmpfsSizeByIno = new Dictionary<ulong, ulong>();
            var visitedTreeNodes = new HashSet<ulong>();

            CollectApfsFsTreeNodes(
                disk, part, volOmap, rootTreePhys, blockSize, totalBlocks, false,
                inodes, dirLinks, extentsByPrivId, symlinksByIno, inlineByIno, decmpfsSizeByIno,
                visitedTreeNodes, 0);

            parsed.ParsedInodeCount = inodes.Count;
            parsed.ParsedDirLinkCount = dirLinks.Count;
            if (inodes.Count == 0 && dirLinks.Count == 0)
                return parsed;

            parsed.RootNode = AssembleApfsVolumeTree(
                part, blockSize, inodes, dirLinks, extentsByPrivId, symlinksByIno, inlineByIno, decmpfsSizeByIno);
            return parsed;
        }

        private static string FormatRfcUuidFromBytes(byte[] b)
        {
            if (b == null || b.Length < 16) return string.Empty;
            return string.Format("{0:x2}{1:x2}{2:x2}{3:x2}-{4:x2}{5:x2}-{6:x2}{7:x2}-{8:x2}{9:x2}-{10:x2}{11:x2}{12:x2}{13:x2}{14:x2}{15:x2}",
                b[0], b[1], b[2], b[3], b[4], b[5], b[6], b[7], b[8], b[9], b[10], b[11], b[12], b[13], b[14], b[15]);
        }

        private static void CollectApfsFsTreeNodes(
            DiskInfo disk, PartitionEntry part, ApfsOmapReader volOmap,
            ulong nodePhys, int blockSize, ulong totalBlocks, bool inheritedHashed,
            Dictionary<ulong, ApfsInodeInfo> inodes,
            List<ApfsDirLink> dirLinks,
            Dictionary<ulong, List<ApfsExtentEntry>> extentsByPrivId,
            Dictionary<ulong, string> symlinksByIno,
            Dictionary<ulong, byte[]> inlineByIno,
            Dictionary<ulong, ulong> decmpfsSizeByIno,
            HashSet<ulong> visitedNodes, int depth)
        {
            if (depth > 12 || visitedNodes.Count >= 16384 || nodePhys == 0 || nodePhys >= totalBlocks) return;
            if (!visitedNodes.Add(nodePhys)) return;

            byte[] node = ReadApfsBlock(disk, part, nodePhys, blockSize);
            if (node == null || node.Length < 0x38) return;

            ushort btnFlags = BitConverter.ToUInt16(node, 0x20);
            ushort btnLevel = BitConverter.ToUInt16(node, 0x22);
            uint btnNkeys = BitConverter.ToUInt32(node, 0x24);
            ushort tocOff = BitConverter.ToUInt16(node, 0x28);
            ushort tocLen = BitConverter.ToUInt16(node, 0x2A);

            bool isRoot = (btnFlags & 0x0001) != 0;
            bool isFixed = (btnFlags & 0x0004) != 0;
            bool isHashed = inheritedHashed || ((btnFlags & 0x0008) != 0);
            int hdrLen = ((btnFlags & 0x0010) != 0) ? 24 : 56;
            int tocStart = hdrLen + tocOff;
            int keyAreaStart = tocStart + tocLen;
            int valAreaEnd = isRoot ? (blockSize - 40) : blockSize;
            if (tocStart < 24 || keyAreaStart > valAreaEnd || valAreaEnd > node.Length) return;

            int tocEntryLen = isFixed ? 4 : 8;
            int count = (int)Math.Min((long)btnNkeys, (keyAreaStart - tocStart) / tocEntryLen);
            if (count <= 0) return;

            if (btnLevel > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    int kPos, kLen, vPos, vLen;
                    if (!ReadApfsTocEntry(node, tocStart + i * tocEntryLen, isFixed, 8, 8, keyAreaStart, valAreaEnd, out kPos, out kLen, out vPos, out vLen))
                        continue;
                    if (vLen < 8) continue;
                    ulong childOid = BitConverter.ToUInt64(node, vPos);
                    ulong childPhys = (volOmap != null) ? volOmap.Lookup(childOid) : 0;
                    if (childPhys == 0 && childOid > 0 && childOid < totalBlocks)
                    {
                        byte[] probe = ReadApfsBlock(disk, part, childOid, blockSize);
                        if (probe != null && VerifyApfsFletcher64(probe, blockSize))
                        {
                            uint ot = BitConverter.ToUInt32(probe, 0x18) & 0xFFFFU;
                            if (ot == 2U || ot == 3U) childPhys = childOid;
                        }
                    }
                    if (childPhys > 0 && childPhys < totalBlocks)
                    {
                        CollectApfsFsTreeNodes(
                            disk, part, volOmap, childPhys, blockSize, totalBlocks, isHashed,
                            inodes, dirLinks, extentsByPrivId, symlinksByIno, inlineByIno, decmpfsSizeByIno,
                            visitedNodes, depth + 1);
                    }
                }
                return;
            }

            for (int i = 0; i < count; i++)
            {
                int kPos, kLen, vPos, vLen;
                if (!ReadApfsTocEntry(node, tocStart + i * tocEntryLen, isFixed, 8, 8, keyAreaStart, valAreaEnd, out kPos, out kLen, out vPos, out vLen))
                    continue;
                if (kLen < 8) continue;

                ulong hdr = BitConverter.ToUInt64(node, kPos);
                ulong objId = hdr & 0x0FFFFFFFFFFFFFFFUL;
                int recType = (int)(hdr >> 60);

                if (recType == 3 && vLen >= 92)
                {
                    ParseApfsInodeRecord(node, objId, vPos, vLen, inodes);
                }
                else if (recType == 9 && kLen >= 11 && vLen >= 18)
                {
                    ParseApfsDirRecord(node, objId, kPos, kLen, vPos, isHashed, dirLinks);
                }
                else if (recType == 8 && kLen >= 16 && vLen >= 24)
                {
                    ParseApfsFileExtentRecord(node, objId, kPos, vPos, extentsByPrivId);
                }
                else if (recType == 4 && kLen >= 11 && vLen >= 4)
                {
                    ParseApfsXattrRecord(node, objId, kPos, kLen, vPos, vLen, symlinksByIno, inlineByIno, decmpfsSizeByIno);
                }
            }
        }

        private static void ParseApfsInodeRecord(
            byte[] node, ulong objId, int vPos, int vLen, Dictionary<ulong, ApfsInodeInfo> inodes)
        {
            ulong parentId = BitConverter.ToUInt64(node, vPos + 0x00);
            ulong privateId = BitConverter.ToUInt64(node, vPos + 0x08);
            ulong modNs = BitConverter.ToUInt64(node, vPos + 0x18);
            uint bsdFlags = BitConverter.ToUInt32(node, vPos + 0x44);
            ushort mode = BitConverter.ToUInt16(node, vPos + 0x50);
            ulong uncompSize = BitConverter.ToUInt64(node, vPos + 0x54);

            var info = new ApfsInodeInfo
            {
                Ino = objId,
                ParentId = parentId,
                PrivateId = privateId != 0 ? privateId : objId,
                ModTime = FromUnixNanoseconds(modNs),
                BsdFlags = bsdFlags,
                Mode = mode,
                UncompressedSize = uncompSize
            };

            if (vLen >= 96)
            {
                ushort numExts = BitConverter.ToUInt16(node, vPos + 92);
                int descBase = vPos + 96;
                int dataCursor = descBase + numExts * 4;
                if (numExts <= 64 && dataCursor <= vPos + vLen)
                {
                    for (int e = 0; e < numExts; e++)
                    {
                        byte xType = node[descBase + e * 4];
                        ushort xSize = BitConverter.ToUInt16(node, descBase + e * 4 + 2);
                        if (dataCursor + xSize > vPos + vLen) break;

                        if (xType == 4 && xSize > 0)
                        {
                            info.Name = Encoding.UTF8.GetString(node, dataCursor, xSize).TrimEnd('\0');
                        }
                        else if (xType == 8 && xSize >= 8)
                        {
                            info.DstreamSize = BitConverter.ToUInt64(node, dataCursor);
                            info.HasDstream = true;
                        }

                        dataCursor += (xSize + 7) & ~7;
                    }
                }
            }

            inodes[objId] = info;
        }

        private static void ParseApfsDirRecord(
            byte[] node, ulong parentId, int kPos, int kLen, int vPos, bool isHashed, List<ApfsDirLink> dirLinks)
        {
            int nameOff;
            int nameLen;

            int hashedLen = (int)(BitConverter.ToUInt32(node, kPos + 8) & 0x3FFU);
            int unhashedLen = BitConverter.ToUInt16(node, kPos + 8);
            if (kLen >= 12 && 12 + hashedLen == kLen)
            {
                nameOff = kPos + 12;
                nameLen = hashedLen;
            }
            else if (kLen >= 10 && 10 + unhashedLen == kLen)
            {
                nameOff = kPos + 10;
                nameLen = unhashedLen;
            }
            else if (isHashed && kLen >= 12)
            {
                nameOff = kPos + 12;
                nameLen = Math.Min(kLen - 12, Math.Max(0, hashedLen));
            }
            else
            {
                nameOff = kPos + 10;
                nameLen = Math.Min(kLen - 10, Math.Max(0, unhashedLen));
            }

            if (nameLen <= 0 || nameOff + nameLen > node.Length) return;
            string name = Encoding.UTF8.GetString(node, nameOff, nameLen).TrimEnd('\0');
            if (string.IsNullOrEmpty(name)) return;

            ulong fileId = BitConverter.ToUInt64(node, vPos + 0);
            ulong dateAdded = BitConverter.ToUInt64(node, vPos + 8);
            ushort drecFlags = BitConverter.ToUInt16(node, vPos + 16);
            byte dtType = (byte)(drecFlags & 0x0F);

            dirLinks.Add(new ApfsDirLink
            {
                ParentId = parentId,
                Name = name,
                ChildId = fileId,
                DtType = dtType,
                DateAdded = FromUnixNanoseconds(dateAdded)
            });
        }

        private static void ParseApfsFileExtentRecord(
            byte[] node, ulong privId, int kPos, int vPos, Dictionary<ulong, List<ApfsExtentEntry>> extentsByPrivId)
        {
            ulong logicalAddr = BitConverter.ToUInt64(node, kPos + 8);
            ulong lenAndFlags = BitConverter.ToUInt64(node, vPos + 0);
            long lenBytes = (long)(lenAndFlags & 0x00FFFFFFFFFFFFFFUL);
            ulong physBlock = BitConverter.ToUInt64(node, vPos + 8);
            if (lenBytes <= 0) return;

            List<ApfsExtentEntry> list;
            if (!extentsByPrivId.TryGetValue(privId, out list))
            {
                list = new List<ApfsExtentEntry>();
                extentsByPrivId[privId] = list;
            }
            list.Add(new ApfsExtentEntry
            {
                LogicalAddr = logicalAddr,
                LengthBytes = lenBytes,
                PhysBlock = physBlock
            });
        }

        private static void ParseApfsXattrRecord(
            byte[] node, ulong ino, int kPos, int kLen, int vPos, int vLen,
            Dictionary<ulong, string> symlinksByIno,
            Dictionary<ulong, byte[]> inlineByIno,
            Dictionary<ulong, ulong> decmpfsSizeByIno)
        {
            ushort nameLen = BitConverter.ToUInt16(node, kPos + 8);
            if (10 + nameLen > kLen) return;
            string xName = Encoding.UTF8.GetString(node, kPos + 10, nameLen).TrimEnd('\0');
            ushort flags = BitConverter.ToUInt16(node, vPos + 0);
            ushort dataLen = BitConverter.ToUInt16(node, vPos + 2);
            if ((flags & 2) == 0 || vPos + 4 + dataLen > node.Length) return;

            if (xName == "com.apple.fs.symlink" && dataLen > 0)
            {
                symlinksByIno[ino] = Encoding.UTF8.GetString(node, vPos + 4, dataLen).TrimEnd('\0');
            }
            else if (xName == "com.apple.decmpfs" && dataLen >= 16)
            {
                int dOff = vPos + 4;
                uint magic = BitConverter.ToUInt32(node, dOff);
                if (magic != 0x636D7066U) return; // 'fpmc'
                uint compType = BitConverter.ToUInt32(node, dOff + 4);
                ulong uncompSize = BitConverter.ToUInt64(node, dOff + 8);
                decmpfsSizeByIno[ino] = uncompSize;

                int payloadOff = dOff + 16;
                int payloadLen = dataLen - 16;
                if (compType == 3 && payloadLen >= 1)
                {
                    if (node[payloadOff] == 0xFF)
                    {
                        int rawLen = (int)Math.Min((ulong)(payloadLen - 1), uncompSize);
                        byte[] literal = new byte[rawLen];
                        Array.Copy(node, payloadOff + 1, literal, 0, rawLen);
                        inlineByIno[ino] = literal;
                    }
                    else if (payloadLen >= 3 && node[payloadOff] == 0x78)
                    {
                        byte[] decompressed = TryDecompressZlibSlice(node, payloadOff + 2, payloadLen - 2, (int)Math.Min(uncompSize, 16L * 1048576L));
                        if (decompressed != null)
                            inlineByIno[ino] = decompressed;
                    }
                }
            }
        }

        private static byte[] TryDecompressZlibSlice(byte[] buf, int offset, int count, int expectedHint)
        {
            if (buf == null || count <= 0 || offset + count > buf.Length) return null;
            try
            {
                using (var src = new MemoryStream(buf, offset, count, false))
                using (var def = new DeflateStream(src, CompressionMode.Decompress))
                using (var outMs = new MemoryStream(Math.Max(256, expectedHint)))
                {
                    byte[] tmp = new byte[8192];
                    int r;
                    while ((r = def.Read(tmp, 0, tmp.Length)) > 0)
                    {
                        outMs.Write(tmp, 0, r);
                        if (outMs.Length > 32L * 1048576L) break;
                    }
                    return outMs.ToArray();
                }
            }
            catch
            {
                return null;
            }
        }

        private static VirtualFsNode BuildApfsNodeFromInode(
            PartitionEntry part, int blockSize, ulong ino, string name, byte dtType, DateTime fallbackDate,
            Dictionary<ulong, ApfsInodeInfo> inodes,
            Dictionary<ulong, List<ApfsExtentEntry>> extentsByPrivId,
            Dictionary<ulong, string> symlinksByIno,
            Dictionary<ulong, byte[]> inlineByIno,
            Dictionary<ulong, ulong> decmpfsSizeByIno)
        {
            ApfsInodeInfo meta;
            inodes.TryGetValue(ino, out meta);

            bool isDir = (meta != null && meta.Mode != 0)
                ? ((meta.Mode & 0xF000U) == 0x4000U)
                : (dtType == 4);
            bool isSymlink = (meta != null && meta.Mode != 0)
                ? ((meta.Mode & 0xF000U) == 0xA000U)
                : (dtType == 10);

            ushort mode = (meta != null && meta.Mode != 0)
                ? meta.Mode
                : (isDir ? (ushort)0x41ED : (ushort)0x81A4);

            var node = new VirtualFsNode
            {
                Name = name,
                IsDirectory = isDir,
                InodeOrObjectId = ino,
                PermissionsString = isSymlink ? ("l" + FormatUnixMode(mode).Substring(1)) : FormatUnixMode(mode),
                ModifiedUtc = (meta != null && meta.ModTime.Year > 1971) ? meta.ModTime : fallbackDate
            };

            if (isDir)
                return node;

            ulong privId = (meta != null && meta.PrivateId != 0) ? meta.PrivateId : ino;
            byte[] inlineData;
            string symTarget;
            if (inlineByIno.TryGetValue(ino, out inlineData))
            {
                node.InlineContent = inlineData;
                node.SizeBytes = inlineData.Length;
                node.ContentLength = 0;
                return node;
            }
            if (isSymlink && symlinksByIno.TryGetValue(ino, out symTarget))
            {
                byte[] symBytes = Encoding.UTF8.GetBytes(symTarget);
                node.InlineContent = symBytes;
                node.SizeBytes = symBytes.Length;
                node.ContentLength = 0;
                return node;
            }

            List<ApfsExtentEntry> exts;
            if (!extentsByPrivId.TryGetValue(privId, out exts))
                extentsByPrivId.TryGetValue(ino, out exts);

            long extSum = 0;
            if (exts != null)
            {
                for (int i = 0; i < exts.Count; i++)
                {
                    long end = (long)exts[i].LogicalAddr + exts[i].LengthBytes;
                    if (end > extSum) extSum = end;
                }
            }

            ulong decSize = 0;
            decmpfsSizeByIno.TryGetValue(ino, out decSize);

            long fileSize = 0;
            if (meta != null && meta.HasDstream)
                fileSize = (long)meta.DstreamSize;
            else if (meta != null && meta.UncompressedSize > 0)
                fileSize = (long)meta.UncompressedSize;
            else if (decSize > 0)
                fileSize = (long)decSize;
            else
                fileSize = extSum;

            node.SizeBytes = fileSize;
            node.ContentLength = fileSize;

            if (exts != null && exts.Count > 0 && fileSize > 0)
            {
                var sorted = new List<ApfsExtentEntry>(exts);
                sorted.Sort(delegate(ApfsExtentEntry a, ApfsExtentEntry b)
                {
                    return a.LogicalAddr.CompareTo(b.LogicalAddr);
                });

                var runs = new List<KeyValuePair<long, long>>();
                long logicalCursor = 0;
                for (int i = 0; i < sorted.Count && logicalCursor < fileSize; i++)
                {
                    long start = (long)sorted[i].LogicalAddr;
                    long len = sorted[i].LengthBytes;
                    if (start > logicalCursor)
                    {
                        long holeLen = Math.Min(start - logicalCursor, fileSize - logicalCursor);
                        if (holeLen > 0)
                        {
                            runs.Add(new KeyValuePair<long, long>(-1L, holeLen));
                            logicalCursor += holeLen;
                        }
                    }

                    long skip = (start < logicalCursor) ? (logicalCursor - start) : 0;
                    long avail = Math.Min(len - skip, fileSize - logicalCursor);
                    if (avail <= 0) continue;

                    if (sorted[i].PhysBlock == 0)
                    {
                        runs.Add(new KeyValuePair<long, long>(-1L, avail));
                    }
                    else
                    {
                        long physOff = part.StartingOffset + (long)(sorted[i].PhysBlock * (ulong)blockSize) + skip;
                        if (physOff >= part.StartingOffset && physOff < part.EndOffset)
                        {
                            long clamped = Math.Min(avail, part.EndOffset - physOff);
                            if (clamped > 0)
                                runs.Add(new KeyValuePair<long, long>(physOff, clamped));
                        }
                    }
                    logicalCursor += avail;
                }

                if (runs.Count > 0)
                {
                    var coalesced = CoalesceExtents(runs);
                    node.ContentExtents = coalesced;
                    node.ContentOffset = coalesced[0].Key >= 0 ? coalesced[0].Key : 0;
                }
            }

            return node;
        }

        private static VirtualFsNode AssembleApfsVolumeTree(
            PartitionEntry part, int blockSize,
            Dictionary<ulong, ApfsInodeInfo> inodes,
            List<ApfsDirLink> dirLinks,
            Dictionary<ulong, List<ApfsExtentEntry>> extentsByPrivId,
            Dictionary<ulong, string> symlinksByIno,
            Dictionary<ulong, byte[]> inlineByIno,
            Dictionary<ulong, ulong> decmpfsSizeByIno)
        {
            var dirNodes = new Dictionary<ulong, VirtualFsNode>();
            var root = BuildApfsNodeFromInode(
                part, blockSize, 2UL, "/", 4, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                inodes, extentsByPrivId, symlinksByIno, inlineByIno, decmpfsSizeByIno);
            root.Name = "/";
            root.FullPath = "/";
            root.IsDirectory = true;
            dirNodes[2UL] = root;

            // Pre-create directory nodes from inodes (excluding private-dir 3 and snap-dir 4)
            foreach (var kv in inodes)
            {
                if (kv.Key <= 4UL) continue;
                if ((kv.Value.Mode & 0xF000U) == 0x4000U)
                {
                    string dName = string.IsNullOrEmpty(kv.Value.Name) ? ("dir_" + kv.Key) : kv.Value.Name;
                    dirNodes[kv.Key] = BuildApfsNodeFromInode(
                        part, blockSize, kv.Key, dName, 4, kv.Value.ModTime,
                        inodes, extentsByPrivId, symlinksByIno, inlineByIno, decmpfsSizeByIno);
                }
            }

            var attachedDirs = new HashSet<ulong>();
            attachedDirs.Add(2UL);
            var attachedNames = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < dirLinks.Count; i++)
            {
                var link = dirLinks[i];
                if (link.ParentId == 1UL || link.ParentId == 3UL || link.ChildId <= 4UL) continue;
                if (string.IsNullOrEmpty(link.Name) || link.Name == "." || link.Name == "..") continue;

                VirtualFsNode parentNode;
                if (!dirNodes.TryGetValue(link.ParentId, out parentNode)) continue;

                string dedup = link.ParentId + ":" + link.Name;
                if (!attachedNames.Add(dedup)) continue;

                VirtualFsNode childDir;
                if (dirNodes.TryGetValue(link.ChildId, out childDir))
                {
                    if (!attachedDirs.Add(link.ChildId)) continue;
                    childDir.Name = link.Name;
                    parentNode.Children.Add(childDir);
                }
                else
                {
                    var childNode = BuildApfsNodeFromInode(
                        part, blockSize, link.ChildId, link.Name, link.DtType, link.DateAdded,
                        inodes, extentsByPrivId, symlinksByIno, inlineByIno, decmpfsSizeByIno);
                    if (childNode.IsDirectory)
                    {
                        dirNodes[link.ChildId] = childNode;
                        attachedDirs.Add(link.ChildId);
                    }
                    parentNode.Children.Add(childNode);
                }
            }

            // Fallback: attach any orphan inodes that carry ParentId and INO_EXT_TYPE_NAME
            foreach (var kv in inodes)
            {
                if (kv.Key <= 4UL || kv.Value.ParentId == 1UL || kv.Value.ParentId == 3UL) continue;
                if (string.IsNullOrEmpty(kv.Value.Name)) continue;

                VirtualFsNode parentNode;
                if (!dirNodes.TryGetValue(kv.Value.ParentId, out parentNode)) continue;

                string dedup = kv.Value.ParentId + ":" + kv.Value.Name;
                if (!attachedNames.Add(dedup)) continue;

                VirtualFsNode orphanDir;
                if (dirNodes.TryGetValue(kv.Key, out orphanDir))
                {
                    if (!attachedDirs.Add(kv.Key)) continue;
                    orphanDir.Name = kv.Value.Name;
                    parentNode.Children.Add(orphanDir);
                }
                else
                {
                    var orphanFile = BuildApfsNodeFromInode(
                        part, blockSize, kv.Key, kv.Value.Name, 8, kv.Value.ModTime,
                        inodes, extentsByPrivId, symlinksByIno, inlineByIno, decmpfsSizeByIno);
                    parentNode.Children.Add(orphanFile);
                }
            }

            return root;
        }

        private static VirtualFsNode CloneApfsNodeShallowTree(VirtualFsNode src)
        {
            var copy = new VirtualFsNode
            {
                Name = src.Name,
                FullPath = src.FullPath,
                IsDirectory = src.IsDirectory,
                SizeBytes = src.SizeBytes,
                InodeOrObjectId = src.InodeOrObjectId,
                PermissionsString = src.PermissionsString,
                ModifiedUtc = src.ModifiedUtc,
                ContentOffset = src.ContentOffset,
                ContentLength = src.ContentLength,
                InlineContent = src.InlineContent,
                ContentExtents = src.ContentExtents
            };
            for (int i = 0; i < src.Children.Count; i++)
                copy.Children.Add(CloneApfsNodeShallowTree(src.Children[i]));
            return copy;
        }

        private static void MergeApfsEmptyDirectories(VirtualFsNode targetDir, VirtualFsNode sourceDir)
        {
            if (targetDir == null || sourceDir == null || !targetDir.IsDirectory || !sourceDir.IsDirectory) return;
            var targetByName = new Dictionary<string, VirtualFsNode>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < targetDir.Children.Count; i++)
                targetByName[targetDir.Children[i].Name] = targetDir.Children[i];

            for (int i = 0; i < sourceDir.Children.Count; i++)
            {
                var srcChild = sourceDir.Children[i];
                if (srcChild.IsDirectory && (srcChild.Name == "Volumes" || srcChild.Name == ".TempItems")) continue;
                VirtualFsNode existing;
                if (!targetByName.TryGetValue(srcChild.Name, out existing))
                {
                    targetDir.Children.Add(CloneApfsNodeShallowTree(srcChild));
                }
                else if (existing.IsDirectory && srcChild.IsDirectory && existing.Children.Count == 0 && srcChild.Children.Count > 0)
                {
                    for (int k = 0; k < srcChild.Children.Count; k++)
                        existing.Children.Add(CloneApfsNodeShallowTree(srcChild.Children[k]));
                }
            }
        }

        private static void FinalizeMountedApfsTree(
            MountedReadOnlyVolume vol, PartitionEntry part, int blockSize, ulong totalBlocks, List<ApfsParsedVolume> parsedVolumes)
        {
            int primaryIdx = 0;
            int bestScore = -1;
            ulong totalAllocBlocks = 0;
            var volNames = new List<string>();

            for (int i = 0; i < parsedVolumes.Count; i++)
            {
                var pv = parsedVolumes[i];
                totalAllocBlocks += pv.AllocBlocks;
                if (!string.IsNullOrEmpty(pv.VolumeName))
                    volNames.Add(pv.VolumeName);

                if (pv.RootNode == null) continue;
                int score = pv.ParsedInodeCount + pv.ParsedDirLinkCount;
                bool isSysMetaVol =
                    string.Equals(pv.VolumeName, "Preboot", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(pv.VolumeName, "Recovery", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(pv.VolumeName, "VM", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(pv.VolumeName, "Update", StringComparison.OrdinalIgnoreCase);
                if (!isSysMetaVol) score += 100000;
                if (score > bestScore)
                {
                    bestScore = score;
                    primaryIdx = i;
                }
            }

            var primary = parsedVolumes[primaryIdx];
            if (string.IsNullOrEmpty(vol.Label) && !string.IsNullOrEmpty(primary.VolumeName))
                vol.Label = primary.VolumeName;
            if (string.IsNullOrEmpty(vol.UUID) && !string.IsNullOrEmpty(primary.VolumeUuid))
                vol.UUID = primary.VolumeUuid;
            if (vol.TotalBlocks <= 0)
                vol.TotalBlocks = (long)totalBlocks;
            if (totalAllocBlocks > 0 && (ulong)vol.TotalBlocks > totalAllocBlocks)
                vol.FreeBlocks = vol.TotalBlocks - (long)totalAllocBlocks;

            var root = primary.RootNode;
            if (parsedVolumes.Count > 1)
            {
                var volumesDir = new VirtualFsNode
                {
                    Name = "Volumes",
                    IsDirectory = true,
                    PermissionsString = "drwxr-xr-x",
                    InodeOrObjectId = 0xFFFFFF00UL
                };
                var usedVolDirNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < parsedVolumes.Count; i++)
                {
                    var pv = parsedVolumes[i];
                    if (pv.RootNode == null) continue;
                    if (i != primaryIdx)
                        MergeApfsEmptyDirectories(root, pv.RootNode);

                    string baseName = string.IsNullOrEmpty(pv.VolumeName) ? ("APFS_Volume_" + (i + 1)) : pv.VolumeName;
                    string cleanName = baseName.Replace('/', '_').Replace('\\', '_');
                    string uniqueName = cleanName;
                    int suffix = 2;
                    while (!usedVolDirNames.Add(uniqueName))
                        uniqueName = cleanName + "_" + (suffix++);

                    var volMountNode = CloneApfsNodeShallowTree(pv.RootNode);
                    volMountNode.Name = uniqueName;
                    volMountNode.IsDirectory = true;
                    volumesDir.Children.Add(volMountNode);
                }

                if (volumesDir.Children.Count > 0)
                {
                    bool hasVolumesEntry = false;
                    for (int i = 0; i < root.Children.Count; i++)
                    {
                        if (string.Equals(root.Children[i].Name, "Volumes", StringComparison.OrdinalIgnoreCase))
                        {
                            hasVolumesEntry = true;
                            if (root.Children[i].IsDirectory)
                            {
                                for (int k = 0; k < volumesDir.Children.Count; k++)
                                    root.Children[i].Children.Add(volumesDir.Children[k]);
                            }
                            break;
                        }
                    }
                    if (!hasVolumesEntry)
                        root.Children.Add(volumesDir);
                }
            }

            SortAndAssignPaths(root, "/");
            vol.SuperblockSummary = string.Format(
                "Apple APFS ('{0}') | BlockSize={1} B | Volumes={2} | Inodes={3} | Read-Only",
                string.IsNullOrEmpty(vol.Label) ? (volNames.Count > 0 ? volNames[0] : "APFS") : vol.Label,
                blockSize,
                parsedVolumes.Count,
                primary.ParsedInodeCount);
            vol.RootNode = root;
        }

        /// <summary>
        /// Summarizes F2FS, ZFS, SquashFS, UDF, ReFS, APFS, Classic HFS, Linux Swap, and LVM2 superblocks
        /// and loads any embedded <c>WPROFS01</c> catalog (falling back to a read-only superblock metadata view).
        /// </summary>
        private static void ParseCatalogOrMetadataVolume(DiskInfo disk, PartitionEntry part, MountedReadOnlyVolume vol)
        {
            int headerReadLen = (int)Math.Min(131072L, part.PartitionLength);
            byte[] header = RawDiskEngine.ReadBytes(disk, part.StartingOffset, headerReadLen);

            if (part.FileSystem == FileSystemType.Btrfs)
                vol.SuperblockSummary = "Btrfs Superblock (_BHRfS_M @ +64 KiB) | Read-Only";
            else if (part.FileSystem == FileSystemType.XFS)
                vol.SuperblockSummary = "XFS Superblock (XFSB @ +0 B) | AG BlockSize=" + (header.Length >= 8 ? BitConverter.ToUInt32(header, 4) : 4096) + " B";
            else if (part.FileSystem == FileSystemType.F2FS)
                vol.SuperblockSummary = "F2FS Flash-Friendly Superblock (0xF2F52010 @ +1024 B) | BlockSize=4096 B | Read-Only";
            else if (part.FileSystem == FileSystemType.ZFS)
                vol.SuperblockSummary = "OpenZFS Storage Pool Vdev (Uberblock 0x00BAB10C) | Read-Only";
            else if (part.FileSystem == FileSystemType.SquashFS)
                vol.SuperblockSummary = "SquashFS 4.0 Read-Only Compressed RootFS (hsqs) | BlockSize=128 KiB | Read-Only";
            else if (part.FileSystem == FileSystemType.ISO9660)
                vol.SuperblockSummary = "ISO9660 (ECMA-119 CD001) Optical/Live Volume | LogicalBlock=2048 B | Read-Only";
            else if (part.FileSystem == FileSystemType.UDF)
                vol.SuperblockSummary = "UDF (ECMA-167 BEA01/NSR03) Universal Volume | Read-Only";
            else if (part.FileSystem == FileSystemType.ReFS)
                vol.SuperblockSummary = "Resilient File System (ReFS) Volume Header | Read-Only";
            else if (part.FileSystem == FileSystemType.APFS)
                vol.SuperblockSummary = "Apple APFS Container Superblock (NXSB/APSB) | Read-Only";
            else if (part.FileSystem == FileSystemType.HFSPlus)
                vol.SuperblockSummary = "Apple HFS+ / HFSX Volume Header (@ +1024 B) | Read-Only";
            else if (part.FileSystem == FileSystemType.HFS)
                vol.SuperblockSummary = "Macintosh Classic HFS Master Directory Block (BD) | Read-Only";
            else
                vol.SuperblockSummary = FileSystemColors.GetDisplayName(part.FileSystem) + " Volume | Read-Only Metadata";

            // Parse structured on-disk WPROFS01 catalog at offset 0x3000 if present
            if (header.Length >= 0x3010 && MatchesAscii(header, 0x3000, "WPROFS01"))
            {
                var tree = ParseRoCatalogTree(header, 0x3000, part.StartingOffset);
                if (tree != null)
                {
                    vol.RootNode = tree;
                    return;
                }
            }

            vol.RootNode = BuildSuperblockOnlyTree(part, vol, header);
        }

        /// <summary>
        /// Parses a WinParted Read-Only Catalog (<c>WPROFS01</c>) serialized by test/image formatting.
        /// </summary>
        private static VirtualFsNode ParseRoCatalogTree(byte[] buf, int catOffset, long partStartOffset)
        {
            uint count = BitConverter.ToUInt32(buf, catOffset + 8);
            int cur = catOffset + 12;
            int limit = Math.Min(buf.Length, catOffset + 4096);

            var byId = new Dictionary<uint, VirtualFsNode>();
            var parentLinks = new List<KeyValuePair<uint, VirtualFsNode>>();
            VirtualFsNode root = null;

            for (uint i = 0; i < count && cur + 29 <= limit; i++)
            {
                uint id = BitConverter.ToUInt32(buf, cur); cur += 4;
                uint parentId = BitConverter.ToUInt32(buf, cur); cur += 4;
                bool isDir = buf[cur++] != 0;
                ulong relOffset = BitConverter.ToUInt64(buf, cur); cur += 8;
                uint dataLen = BitConverter.ToUInt32(buf, cur); cur += 4;
                uint mode = BitConverter.ToUInt32(buf, cur); cur += 4;

                if (cur + 2 > limit) break;
                ushort nameLen = BitConverter.ToUInt16(buf, cur); cur += 2;
                if (cur + nameLen + 2 > limit) break;
                string name = Encoding.UTF8.GetString(buf, cur, nameLen); cur += nameLen;

                ushort pathLen = BitConverter.ToUInt16(buf, cur); cur += 2;
                if (cur + pathLen > limit) break;
                string fullPath = Encoding.UTF8.GetString(buf, cur, pathLen); cur += pathLen;

                var node = new VirtualFsNode
                {
                    Name = name,
                    FullPath = fullPath,
                    IsDirectory = isDir,
                    SizeBytes = isDir ? 0 : (long)dataLen,
                    InodeOrObjectId = id,
                    PermissionsString = FormatUnixMode((ushort)(mode & 0xFFFF)),
                    ContentOffset = isDir ? 0 : (partStartOffset + (long)relOffset),
                    ContentLength = isDir ? 0 : (long)dataLen
                };

                byId[id] = node;
                if (parentId == 0 || fullPath == "/")
                    root = node;
                else
                    parentLinks.Add(new KeyValuePair<uint, VirtualFsNode>(parentId, node));
            }

            foreach (var link in parentLinks)
            {
                VirtualFsNode parent;
                if (byId.TryGetValue(link.Key, out parent))
                    parent.Children.Add(link.Value);
            }

            return root;
        }

        /// <summary>
        /// Builds a synthetic single-directory tree exposing raw superblock/header info when a filesystem
        /// lacks a native on-disk directory tree (e.g., Swap, LVM2, or minimal metadata volumes).
        /// </summary>
        private static VirtualFsNode BuildSuperblockOnlyTree(PartitionEntry part, MountedReadOnlyVolume vol, byte[] headerBytes)
        {
            var root = new VirtualFsNode
            {
                Name = "/",
                FullPath = "/",
                IsDirectory = true,
                InodeOrObjectId = 1,
                PermissionsString = "r-xr-xr-x"
            };

            string infoText = string.Format(
                "WinParted Read-Only Filesystem Metadata Inspector\n" +
                "Mount Point  : {0}\n" +
                "File System  : {1}\n" +
                "Volume Label : {2}\n" +
                "Volume UUID  : {3}\n" +
                "Partition    : {4} ({5})\n" +
                "LBA Range    : {6:N0}..{7:N0}\n" +
                "Summary      : {8}\n\n" +
                "Note: Partition contains no user catalog directory tree at this header offset; raw superblock inspection is active in Read-Only mode.\n",
                vol.MountPath,
                FileSystemColors.GetDisplayName(part.FileSystem),
                string.IsNullOrEmpty(part.Label) ? "(none)" : part.Label,
                string.IsNullOrEmpty(part.UUID) ? "(none)" : part.UUID,
                part.ShortDeviceName,
                ByteFormatter.FormatBytes(part.PartitionLength),
                part.StartLba,
                part.EndLba,
                vol.SuperblockSummary);

            byte[] infoBytes = Encoding.UTF8.GetBytes(infoText);
            root.Children.Add(new VirtualFsNode
            {
                Name = "VOLUME_INFO.txt",
                FullPath = "/VOLUME_INFO.txt",
                IsDirectory = false,
                SizeBytes = infoBytes.Length,
                InodeOrObjectId = 2,
                PermissionsString = "r--r--r--",
                InlineContent = infoBytes
            });

            int sbLen = Math.Min(4096, headerBytes.Length);
            root.Children.Add(new VirtualFsNode
            {
                Name = ".superblock.bin",
                FullPath = "/.superblock.bin",
                IsDirectory = false,
                SizeBytes = sbLen,
                InodeOrObjectId = 3,
                PermissionsString = "r--------",
                ContentOffset = part.StartingOffset,
                ContentLength = sbLen
            });

            return root;
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

        private static string FormatUnixMode(ushort mode)
        {
            char kind = ((mode & 0xF000) == 0x4000) ? 'd' : '-';
            char[] perms = new char[10];
            perms[0] = kind;
            int[] bits = new int[] { 0x100, 0x080, 0x040, 0x020, 0x010, 0x008, 0x004, 0x002, 0x001 };
            char[] sym = new char[] { 'r', 'w', 'x', 'r', 'w', 'x', 'r', 'w', 'x' };
            for (int i = 0; i < 9; i++)
                perms[i + 1] = ((mode & bits[i]) != 0) ? sym[i] : '-';
            return new string(perms);
        }

        private static DateTime FromUnixSeconds(uint seconds)
        {
            if (seconds == 0)
                return new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds);
        }

        private static DateTime FromHfsPlusSeconds(uint hfsSeconds)
        {
            if (hfsSeconds == 0)
                return new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(hfsSeconds);
        }

        private static ushort ReadBE16(byte[] data, int offset)
        {
            return (ushort)((data[offset] << 8) | data[offset + 1]);
        }

        private static short ReadBES16(byte[] data, int offset)
        {
            return unchecked((short)((data[offset] << 8) | data[offset + 1]));
        }

        private static uint ReadBE32(byte[] data, int offset)
        {
            return ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];
        }

        private static ulong ReadBE64(byte[] data, int offset)
        {
            return ((ulong)ReadBE32(data, offset) << 32) | ReadBE32(data, offset + 4);
        }
    }
}
