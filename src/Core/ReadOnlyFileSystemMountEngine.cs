using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using WinParted.Models;

namespace WinParted.Core
{
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
    }

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
        internal long OverlayNextDataRelOffset { get; set; }
        internal List<OverlayRecord> OverlayRecords { get; set; }
    }

    internal class OverlayRecord
    {
        public byte Op { get; set; } // 1 = upsert, 2 = delete
        public bool IsDirectory { get; set; }
        public long RelDataOffset { get; set; }
        public long DataLength { get; set; }
        public long ModifiedUtcTicks { get; set; }
        public string FullPath { get; set; }
    }

    public static class ReadOnlyFileSystemMountEngine
    {
        private static readonly object SyncLock = new object();
        private static readonly Dictionary<string, MountedReadOnlyVolume> ActiveMounts = new Dictionary<string, MountedReadOnlyVolume>();

        public static bool IsSupportedNonMicrosoftFs(FileSystemType fs)
        {
            switch (fs)
            {
                case FileSystemType.Ext2:
                case FileSystemType.Ext3:
                case FileSystemType.Ext4:
                case FileSystemType.Btrfs:
                case FileSystemType.XFS:
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

        public static void UnmountAll()
        {
            lock (SyncLock)
            {
                ActiveMounts.Clear();
            }
        }

        public static MountedReadOnlyVolume MountReadOnly(DiskInfo disk, PartitionEntry part, bool requireVirtualDisk = false)
        {
            if (disk == null) throw new ArgumentNullException("disk");
            if (part == null) throw new ArgumentNullException("part");
            if (part.IsUnallocated)
                throw new InvalidOperationException("Cannot mount unallocated space.");
            if (requireVirtualDisk && !disk.IsVirtualImage)
                throw new InvalidOperationException("Test safety restriction: Read-only mount tests are restricted to virtual disk images.");
            if (!IsSupportedNonMicrosoftFs(part.FileSystem))
                throw new NotSupportedException("Read-only explorer mount is intended for non-Microsoft filesystems (ext2/3/4, Btrfs, XFS, APFS, HFS+, HFS, Linux Swap/LVM2). Selected: " + FileSystemColors.GetDisplayName(part.FileSystem));

            string mountId = string.Format("ro_disk{0}_p{1}", disk.DiskNumber, part.PartitionNumber);
            string mountPath = string.Format("/mnt/ro/disk{0}p{1}", disk.DiskNumber, part.PartitionNumber);

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
                OverlayNextDataRelOffset = 0x24000L,
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

        public static void ExportNodeToHost(
            DiskInfo disk, PartitionEntry part, VirtualFsNode node, string hostDestinationPath,
            out int filesCopied, out int foldersCreated, out long bytesCopied)
        {
            ExportNodeToHost(disk, part, node, hostDestinationPath, null, null, out filesCopied, out foldersCreated, out bytesCopied);
        }

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

        public static void SetExperimentalWriteMode(MountedReadOnlyVolume vol, bool enabled)
        {
            if (vol == null) throw new ArgumentNullException("vol");
            vol.ExperimentalWriteEnabled = enabled;
        }

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

        public static VirtualFsNode WriteFileToVolume(
            DiskInfo disk, PartitionEntry part, MountedReadOnlyVolume vol, VirtualFsNode targetDir, string fileName, byte[] data)
        {
            EnsureExperimentalWriteAllowed(disk, part, vol);
            var node = WriteFileInternal(disk, part, vol, ResolveTargetDir(vol, targetDir), fileName, data ?? new byte[0]);
            SaveExperimentalOverlay(disk, part, vol);
            return node;
        }

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
                if (vol.OverlayNextDataRelOffset < 0x24000L)
                    vol.OverlayNextDataRelOffset = 0x24000L;

                relDataOffset = ((vol.OverlayNextDataRelOffset + 4095L) / 4096L) * 4096L;
                long alignedWriteLen = ((data.Length + 4095L) / 4096L) * 4096L;
                if (part.StartingOffset + relDataOffset + alignedWriteLen > part.EndOffset)
                    throw new IOException("Partition does not have enough overlay space remaining to write '" + cleanName + "'.");

                byte[] sectorBuf = new byte[(int)alignedWriteLen];
                Array.Copy(data, sectorBuf, data.Length);
                RawDiskEngine.WriteBytes(disk, part.StartingOffset + relDataOffset, sectorBuf);
                vol.OverlayNextDataRelOffset = relDataOffset + alignedWriteLen;
            }

            DateTime now = DateTime.UtcNow;
            VirtualFsNode existing = FindChildByName(parentDir, cleanName);
            if (existing != null)
                parentDir.Children.Remove(existing);

            var fileNode = new VirtualFsNode
            {
                Name = cleanName,
                FullPath = fullPath,
                IsDirectory = false,
                SizeBytes = data.Length,
                InodeOrObjectId = NextOverlayInodeId(vol),
                PermissionsString = "-rw-r--r--",
                ModifiedUtc = now,
                ContentOffset = data.Length > 0 ? (part.StartingOffset + relDataOffset) : 0,
                ContentLength = data.Length,
                InlineContent = data.Length == 0 ? new byte[0] : null
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

        private static void SaveExperimentalOverlay(DiskInfo disk, PartitionEntry part, MountedReadOnlyVolume vol)
        {
            const int tableSize = 16384; // 0x20000..0x23FFF
            byte[] buf = new byte[tableSize];
            Encoding.ASCII.GetBytes("WPRWOVL1").CopyTo(buf, 0);
            BitConverter.GetBytes((ulong)Math.Max(0x24000L, vol.OverlayNextDataRelOffset)).CopyTo(buf, 8);

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
            RawDiskEngine.WriteBytes(disk, part.StartingOffset + 0x20000L, buf);
        }

        private static void LoadAndMergeExperimentalOverlay(DiskInfo disk, PartitionEntry part, MountedReadOnlyVolume vol)
        {
            if (part.PartitionLength < 0x30000L || vol.RootNode == null) return;
            try
            {
                byte[] probe = RawDiskEngine.ReadBytes(disk, part.StartingOffset + 0x20000L, 4096);
                if (probe.Length < 20 || !MatchesAscii(probe, 0, "WPRWOVL1"))
                    return;

                byte[] table = RawDiskEngine.ReadBytes(disk, part.StartingOffset + 0x20000L, 16384);
                if (table.Length < 20 || !MatchesAscii(table, 0, "WPRWOVL1"))
                    return;

                ulong nextData = BitConverter.ToUInt64(table, 8);
                vol.OverlayNextDataRelOffset = (long)Math.Max(0x24000UL, nextData);
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
                        ApplyOverlayUpsertToTree(part, vol, rec);
                    }
                }

                SortAndAssignPaths(vol.RootNode, "/");
            }
            catch
            {
            }
        }

        private static void ApplyOverlayUpsertToTree(PartitionEntry part, MountedReadOnlyVolume vol, OverlayRecord rec)
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
                parent.Children.Add(new VirtualFsNode
                {
                    Name = leafName,
                    FullPath = rec.FullPath,
                    IsDirectory = false,
                    SizeBytes = rec.DataLength,
                    InodeOrObjectId = NextOverlayInodeId(vol),
                    PermissionsString = "-rw-r--r--",
                    ModifiedUtc = mtime,
                    ContentOffset = (rec.DataLength > 0 && rec.RelDataOffset > 0) ? (part.StartingOffset + rec.RelDataOffset) : 0,
                    ContentLength = rec.DataLength,
                    InlineContent = rec.DataLength == 0 ? new byte[0] : null
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

        private static void ParseCatalogOrMetadataVolume(DiskInfo disk, PartitionEntry part, MountedReadOnlyVolume vol)
        {
            int headerReadLen = (int)Math.Min(131072L, part.PartitionLength);
            byte[] header = RawDiskEngine.ReadBytes(disk, part.StartingOffset, headerReadLen);

            if (part.FileSystem == FileSystemType.Btrfs)
                vol.SuperblockSummary = "Btrfs Superblock (_BHRfS_M @ +64 KiB) | Read-Only";
            else if (part.FileSystem == FileSystemType.XFS)
                vol.SuperblockSummary = "XFS Superblock (XFSB @ +0 B) | AG BlockSize=" + (header.Length >= 8 ? BitConverter.ToUInt32(header, 4) : 4096) + " B";
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
