using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
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
                if (cur.Key + cur.Value == raw[i].Key)
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
