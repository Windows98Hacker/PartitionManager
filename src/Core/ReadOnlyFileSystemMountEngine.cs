using System;
using System.Collections.Generic;
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
        public bool IsReadOnly { get { return true; } }
        public VirtualFsNode RootNode { get; set; }
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
                BlockSize = 4096
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

            part.IsMounted = true;
            lock (SyncLock)
            {
                ActiveMounts[MakeMountKey(disk.DiskNumber, part.StartingOffset)] = vol;
            }
            return vol;
        }

        public static byte[] ReadFileBytes(DiskInfo disk, PartitionEntry part, VirtualFsNode fileNode)
        {
            if (fileNode == null || fileNode.IsDirectory)
                return new byte[0];

            if (fileNode.InlineContent != null)
            {
                byte[] copy = new byte[fileNode.InlineContent.Length];
                Array.Copy(fileNode.InlineContent, copy, copy.Length);
                return copy;
            }

            if (fileNode.ContentExtents != null && fileNode.ContentExtents.Count > 0 && fileNode.ContentLength > 0)
            {
                int targetLen = (int)Math.Min(fileNode.ContentLength, 16L * 1048576L);
                byte[] outBuf = new byte[targetLen];
                int written = 0;
                for (int i = 0; i < fileNode.ContentExtents.Count && written < targetLen; i++)
                {
                    long extOff = fileNode.ContentExtents[i].Key;
                    long extLen = fileNode.ContentExtents[i].Value;
                    if (extOff < part.StartingOffset || extOff >= part.EndOffset || extLen <= 0) continue;
                    int chunkLen = (int)Math.Min(targetLen - written, Math.Min(extLen, part.EndOffset - extOff));
                    if (chunkLen <= 0) continue;
                    byte[] chunk = RawDiskEngine.ReadBytes(disk, extOff, chunkLen);
                    int copyLen = Math.Min(chunkLen, chunk.Length);
                    if (copyLen > 0)
                    {
                        Array.Copy(chunk, 0, outBuf, written, copyLen);
                        written += copyLen;
                    }
                    if (copyLen < chunkLen) break;
                }
                if (written == targetLen) return outBuf;
                byte[] trimmed = new byte[written];
                Array.Copy(outBuf, trimmed, written);
                return trimmed;
            }

            if (fileNode.ContentLength <= 0 || fileNode.ContentOffset < part.StartingOffset)
                return new byte[0];

            int toRead = (int)Math.Min(fileNode.ContentLength, 16L * 1048576L);
            if (fileNode.ContentOffset + toRead > part.EndOffset)
                toRead = (int)Math.Max(0, part.EndOffset - fileNode.ContentOffset);
            if (toRead <= 0) return new byte[0];

            return RawDiskEngine.ReadBytes(disk, fileNode.ContentOffset, toRead);
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
            if (depth > 8 || !visited.Add(inodeNum))
                return null;

            byte[] inode = ReadExtInodeBytes(disk, part, blockSize, inodeTableBlock, inodeSize, inodeNum);
            if (inode.Length < 128) return null;

            ushort mode = BitConverter.ToUInt16(inode, 0x00);
            uint sizeLo = BitConverter.ToUInt32(inode, 0x04);
            uint mtime = BitConverter.ToUInt32(inode, 0x10);
            bool isDir = (mode & 0xF000) == 0x4000;

            var node = new VirtualFsNode
            {
                Name = name,
                FullPath = fullPath,
                IsDirectory = isDir,
                SizeBytes = sizeLo,
                InodeOrObjectId = inodeNum,
                PermissionsString = FormatUnixMode(mode),
                ModifiedUtc = FromUnixSeconds(mtime)
            };

            uint firstBlock = ResolveExtFirstBlock(inode);
            if (!isDir)
            {
                if (firstBlock > 0)
                {
                    node.ContentOffset = part.StartingOffset + (long)firstBlock * blockSize;
                    node.ContentLength = sizeLo;
                }
                return node;
            }

            if (firstBlock == 0) return node;
            long dirOffset = part.StartingOffset + (long)firstBlock * blockSize;
            byte[] dirBlock = RawDiskEngine.ReadBytes(disk, dirOffset, (int)Math.Min(blockSize, part.EndOffset - dirOffset));

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
                            ushort cMode = BitConverter.ToUInt16(childInodeBytes, 0x00);
                            uint cSize = BitConverter.ToUInt32(childInodeBytes, 0x04);
                            uint cMtime = BitConverter.ToUInt32(childInodeBytes, 0x10);
                            uint cBlock = ResolveExtFirstBlock(childInodeBytes);
                            node.Children.Add(new VirtualFsNode
                            {
                                Name = childName,
                                FullPath = childPath,
                                IsDirectory = false,
                                SizeBytes = cSize,
                                InodeOrObjectId = childInode,
                                PermissionsString = FormatUnixMode(cMode),
                                ModifiedUtc = FromUnixSeconds(cMtime),
                                ContentOffset = cBlock > 0 ? part.StartingOffset + (long)cBlock * blockSize : 0,
                                ContentLength = cBlock > 0 ? cSize : 0
                            });
                        }
                    }
                }
                pos += recLen;
            }

            return node;
        }

        private static byte[] ReadExtInodeBytes(DiskInfo disk, PartitionEntry part, long blockSize, uint inodeTableBlock, int inodeSize, uint inodeNum)
        {
            if (inodeNum == 0) return new byte[0];
            long offset = part.StartingOffset + (long)inodeTableBlock * blockSize + (long)(inodeNum - 1) * inodeSize;
            if (offset < part.StartingOffset || offset + inodeSize > part.EndOffset)
                return new byte[0];
            return RawDiskEngine.ReadBytes(disk, offset, inodeSize);
        }

        private static uint ResolveExtFirstBlock(byte[] inode)
        {
            if (inode.Length < 100) return 0;
            uint flags = BitConverter.ToUInt32(inode, 0x20);
            bool usesExtents = (flags & 0x80000u) != 0 || BitConverter.ToUInt16(inode, 0x28) == 0xF30A;
            if (usesExtents && BitConverter.ToUInt16(inode, 0x28) == 0xF30A)
            {
                ushort entries = BitConverter.ToUInt16(inode, 0x2A);
                ushort depth = BitConverter.ToUInt16(inode, 0x2E);
                if (entries >= 1 && depth == 0)
                    return BitConverter.ToUInt32(inode, 0x3C);
            }
            return BitConverter.ToUInt32(inode, 0x28);
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

                ulong catLogicalSize = ReadBE64(vh, 272);
                var catExtents = ParseHfsExtents(vh, 288);

                vol.SuperblockSummary = string.Format(
                    "Apple {0} Volume Header | BlockSize={1} B | Blocks={2:N0} (Free={3:N0}) | Catalog={4} | Read-Only",
                    isHfsx ? "HFSX" : "HFS+", blockSize, totalBlocks, freeBlocks, ByteFormatter.FormatBytes((long)catLogicalSize));

                if (blockSize >= 512 && catLogicalSize >= 512 && catExtents[0].BlockCount > 0 && catExtents[0].StartBlock > 0)
                {
                    if (TryParseHfsPlusCatalogBTree(disk, part, vol, embedBase, blockSize, catLogicalSize, catExtents, isHfsx))
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
            HfsExtent[] extents, long forkOffset, int length)
        {
            if (length <= 0 || forkOffset < 0) return new byte[0];

            byte[] output = new byte[length];
            int copied = 0;
            long logicalCursor = forkOffset;

            long extLogicalStart = 0;
            for (int i = 0; i < extents.Length && copied < length; i++)
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

        private static bool TryParseHfsPlusCatalogBTree(
            DiskInfo disk, PartitionEntry part, MountedReadOnlyVolume vol, long embedBase,
            uint blockSize, ulong catLogicalSize, HfsExtent[] catExtents, bool isHfsx)
        {
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

            const int MaxLeafNodes = 512;
            const int MaxCatalogEntries = 4000;

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
                            var fileExtents = ParseHfsExtents(nodeBuf, dataOff + 104);

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

                            for (int e = 0; e < fileExtents.Length; e++)
                            {
                                if (fileExtents[e].BlockCount == 0) break;
                                long extAbsOff = part.StartingOffset + embedBase + (long)fileExtents[e].StartBlock * blockSize;
                                long extByteLen = (long)fileExtents[e].BlockCount * blockSize;
                                if (extAbsOff >= part.StartingOffset && extAbsOff < part.EndOffset)
                                    item.Extents.Add(new KeyValuePair<long, long>(extAbsOff, extByteLen));
                            }

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
