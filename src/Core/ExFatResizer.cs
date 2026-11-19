using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using WinParted.Interop;
using WinParted.Models;

namespace WinParted.Core
{
    /// <summary>
    /// Abstraction over sector/byte storage for offline exFAT resize and move operations.
    /// Supports both virtual disk image files and locked physical drive sessions.
    /// </summary>
    public interface IRawDiskIo
    {
        byte[] ReadAt(long diskOffset, int length);
        void WriteAt(long diskOffset, byte[] data);
        void Flush();
    }

    /// <summary>
    /// Offline, Microsoft exFAT-spec-compliant resizer and partition mover (Shrink, Grow, and Overlap-Safe Move).
    /// Supports 512 B through 4096 B logical sectors, contiguous (<c>NoFatChain</c>) and FAT-chained files/directories,
    /// multi-cluster Allocation Bitmaps, and zero-shift FAT expansions (<c>ClusterHeapOffset += K * SecPerClus</c>).
    /// </summary>
    public static class ExFatResizer
    {
        private const uint FAT_BAD_CLUSTER = 0xFFFFFFF7u;
        private const uint FAT_EOC = 0xFFFFFFFFu;
        private const uint FAT_MEDIA = 0xFFFFFFF8u;
        private const int FAT_PAGE_ENTRIES = 65536; // 256 KiB per cached FAT page

        public sealed class VirtualFileIo : IRawDiskIo, IDisposable
        {
            private readonly FileStream _stream;

            public VirtualFileIo(string path)
            {
                _stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            }

            public byte[] ReadAt(long diskOffset, int length)
            {
                if (length <= 0) return new byte[0];
                if (diskOffset < 0 || diskOffset + length > _stream.Length)
                    throw new EndOfStreamException("Virtual disk read beyond EOF at offset 0x" + diskOffset.ToString("X"));

                byte[] buf = new byte[length];
                _stream.Seek(diskOffset, SeekOrigin.Begin);
                int read = 0;
                while (read < length)
                {
                    int n = _stream.Read(buf, read, length - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read < length)
                {
                    byte[] trimmed = new byte[read];
                    Array.Copy(buf, 0, trimmed, 0, read);
                    return trimmed;
                }
                return buf;
            }

            public void WriteAt(long diskOffset, byte[] data)
            {
                if (data == null || data.Length == 0) return;
                if (diskOffset < 0 || diskOffset + data.Length > _stream.Length)
                    throw new EndOfStreamException("Virtual disk write beyond EOF at offset 0x" + diskOffset.ToString("X"));

                _stream.Seek(diskOffset, SeekOrigin.Begin);
                _stream.Write(data, 0, data.Length);
            }

            public void Flush()
            {
                _stream.Flush();
            }

            public void Dispose()
            {
                _stream.Dispose();
            }
        }

        public sealed class PhysicalSessionIo : IRawDiskIo
        {
            private readonly Win32DiskInterop.PhysicalRawSession _session;

            public PhysicalSessionIo(Win32DiskInterop.PhysicalRawSession session)
            {
                if (session == null) throw new ArgumentNullException("session");
                _session = session;
            }

            public byte[] ReadAt(long diskOffset, int length)
            {
                return _session.ReadAt(diskOffset, length);
            }

            public void WriteAt(long diskOffset, byte[] data)
            {
                _session.WriteAt(diskOffset, data);
            }

            public void Flush()
            {
                _session.Flush();
            }
        }

        private sealed class ExFatBootGeometry
        {
            public long PartStartOffset;
            public byte[] MainBootRegion; // 12 * SectorSize
            public ulong PartitionOffsetSectors;
            public ulong VolumeLengthSectors;
            public uint FatOffsetSectors;
            public uint FatLengthSectors;
            public uint ClusterHeapOffsetSectors;
            public uint ClusterCount;
            public uint RootFirstCluster;
            public uint VolumeSerial;
            public ushort VolumeFlags;
            public int BytesPerSectorShift;
            public int SectorsPerClusterShift;
            public int SectorSize;
            public uint SectorsPerCluster;
            public int ClusterSize;
            public uint BootChecksum;

            public long FatStartByte { get { return PartStartOffset + (long)FatOffsetSectors * SectorSize; } }
            public long HeapStartByte { get { return PartStartOffset + (long)ClusterHeapOffsetSectors * SectorSize; } }

            public long ClusterToDiskByte(uint cluster)
            {
                return HeapStartByte + (long)(cluster - 2u) * SectorsPerCluster * SectorSize;
            }
        }

        private sealed class EvacOverride
        {
            public uint NewFirstCluster;
            public bool ForceFatChain;
        }

        private sealed class ExFatObjectInfo
        {
            public int DirIndex;           // -1 for Bitmap / UpCase
            public long ParentEntryOffset; // Byte offset of 0x85 (or 0x81/0x82) inside parent dir logical stream
            public int EntrySetBytes;
            public uint FirstCluster;
            public ulong DataLength;
            public bool NoFatChain;
            public bool IsDirectory;
            public bool DirtyMetadata;
            public bool DirtyFat;
            public List<uint> Clusters;
        }

        private sealed class ExFatDirNode
        {
            public int Index;
            public int ParentDirIndex;
            public ExFatObjectInfo ObjInfo;
            public bool HasDirtyChildren;
            public bool HasAnyNonZeroChild;
            public Dictionary<long, EvacOverride> ChildOverrides;
        }

        private sealed class PagedFatTable
        {
            private readonly IRawDiskIo _io;
            private readonly long _fatStartByte;
            private readonly long _fatByteLen;
            private readonly int _sectorSize;
            private readonly Dictionary<int, uint[]> _pages = new Dictionary<int, uint[]>();
            private readonly HashSet<int> _dirtyPages = new HashSet<int>();

            public PagedFatTable(IRawDiskIo io, long fatStartByte, uint fatLengthSectors, int sectorSize)
            {
                _io = io;
                _fatStartByte = fatStartByte;
                _fatByteLen = (long)fatLengthSectors * sectorSize;
                _sectorSize = sectorSize;
            }

            public uint MaxAddressableEntries
            {
                get { return (uint)Math.Min((long)uint.MaxValue, _fatByteLen / 4L); }
            }

            private uint[] LoadPage(int pageIdx)
            {
                uint[] page;
                if (_pages.TryGetValue(pageIdx, out page))
                    return page;

                long pageByteOffset = (long)pageIdx * FAT_PAGE_ENTRIES * 4L;
                int bytesToRead = (int)Math.Min(FAT_PAGE_ENTRIES * 4L, _fatByteLen - pageByteOffset);
                if (bytesToRead <= 0)
                    throw new ArgumentOutOfRangeException("pageIdx", "FAT page beyond FatLength.");

                byte[] raw = _io.ReadAt(_fatStartByte + pageByteOffset, bytesToRead);
                int count = (raw.Length + 3) / 4;
                page = new uint[count];
                for (int i = 0; i < raw.Length / 4; i++)
                    page[i] = BitConverter.ToUInt32(raw, i * 4);

                if (_pages.Count >= 16)
                    EvictCleanPage();
                _pages[pageIdx] = page;
                return page;
            }

            private void EvictCleanPage()
            {
                int victim = -1;
                foreach (int key in _pages.Keys)
                {
                    if (!_dirtyPages.Contains(key))
                    {
                        victim = key;
                        break;
                    }
                }
                if (victim >= 0)
                    _pages.Remove(victim);
            }

            public uint GetEntry(uint cluster)
            {
                int pageIdx = (int)(cluster / FAT_PAGE_ENTRIES);
                int idx = (int)(cluster % FAT_PAGE_ENTRIES);
                uint[] page = LoadPage(pageIdx);
                return idx < page.Length ? page[idx] : 0u;
            }

            public void SetEntry(uint cluster, uint value)
            {
                int pageIdx = (int)(cluster / FAT_PAGE_ENTRIES);
                int idx = (int)(cluster % FAT_PAGE_ENTRIES);
                uint[] page = LoadPage(pageIdx);
                if (idx >= page.Length)
                    throw new ArgumentOutOfRangeException("cluster", "Cluster exceeds FatLength.");
                page[idx] = value;
                _dirtyPages.Add(pageIdx);
            }

            public List<uint> ReadFatChain(uint firstCluster, uint maxClusterCount, string contextName)
            {
                if (firstCluster < 2u || firstCluster > maxClusterCount + 1u)
                    throw new InvalidDataException(string.Format("exFAT {0} FirstCluster {1} is out of valid cluster bounds [2..{2}]. Run 'chkdsk /f' first.",
                        contextName, firstCluster, maxClusterCount + 1u));

                var chain = new List<uint>();
                var seen = new HashSet<uint>();
                uint cur = firstCluster;
                while (true)
                {
                    if (!seen.Add(cur))
                        throw new InvalidDataException(string.Format("exFAT FAT cycle detected in {0} at cluster {1}. Run 'chkdsk /f' first.", contextName, cur));
                    if (chain.Count >= (int)Math.Min((long)int.MaxValue - 1L, maxClusterCount))
                        throw new InvalidDataException(string.Format("exFAT FAT chain length exceeded volume cluster count for {0}. Run 'chkdsk /f' first.", contextName));

                    chain.Add(cur);
                    uint next = GetEntry(cur);
                    if (next == FAT_EOC || next >= FAT_MEDIA)
                        break;
                    if (next == FAT_BAD_CLUSTER)
                        throw new InvalidDataException(string.Format("exFAT {0} references bad cluster {1}. Run 'chkdsk /f' first.", contextName, cur));
                    if (next < 2u || next > maxClusterCount + 1u)
                        throw new InvalidDataException(string.Format("exFAT FAT chain for {0} has invalid link {1} -> 0x{2:X}. Run 'chkdsk /f' first.",
                            contextName, cur, next));
                    cur = next;
                }
                return chain;
            }

            public void Flush()
            {
                if (_dirtyPages.Count == 0) return;
                var sorted = new List<int>(_dirtyPages);
                sorted.Sort();
                for (int i = 0; i < sorted.Count; i++)
                {
                    int pageIdx = sorted[i];
                    uint[] page = _pages[pageIdx];
                    long pageByteOffset = (long)pageIdx * FAT_PAGE_ENTRIES * 4L;
                    int rawBytes = (int)Math.Min((long)page.Length * 4L, _fatByteLen - pageByteOffset);
                    int alignedLen = ((rawBytes + _sectorSize - 1) / _sectorSize) * _sectorSize;
                    if (pageByteOffset + alignedLen > _fatByteLen)
                        alignedLen = (int)(_fatByteLen - pageByteOffset);
                    byte[] outBuf = new byte[alignedLen];
                    for (int k = 0; k < page.Length && k * 4 < outBuf.Length; k++)
                        BitConverter.GetBytes(page[k]).CopyTo(outBuf, k * 4);
                    _io.WriteAt(_fatStartByte + pageByteOffset, outBuf);
                }
                _dirtyPages.Clear();
            }
        }

        private sealed class PreFlightVolumeState
        {
            public ExFatBootGeometry Boot;
            public PagedFatTable Fat;
            public byte[] DiskBitmap;
            public byte[] OwnedBitmap;
            public ExFatObjectInfo BitmapObj;
            public ExFatObjectInfo UpCaseObj;
            public ExFatDirNode RootDir;
            public List<ExFatDirNode> AllDirs = new List<ExFatDirNode>();
            public List<ExFatObjectInfo> TrackedFiles = new List<ExFatObjectInfo>();
            public long EvacZoneMinCluster;
            public long EvacZoneMaxCluster;
        }

        /// <summary>
        /// Computes Microsoft exFAT Directory Entry Set Checksum (Spec Section 3.3.4):
        /// 16-bit right-rotate-by-1 additive sum across all bytes of the entry set except offsets 2 and 3.
        /// </summary>
        public static ushort ComputeExFatEntrySetChecksum(byte[] data, int offset, int totalBytes)
        {
            ushort checksum = 0;
            if (data == null || offset < 0 || totalBytes <= 0 || offset + totalBytes > data.Length)
                return 0;

            for (int i = 0; i < totalBytes; i++)
            {
                if (i == 2 || i == 3) continue;
                checksum = (ushort)(((checksum & 1) != 0 ? 0x8000 : 0) + (checksum >> 1) + data[offset + i]);
            }
            return checksum;
        }

        private static bool MatchesAscii(byte[] buf, int off, string text)
        {
            if (buf == null || off + text.Length > buf.Length) return false;
            for (int i = 0; i < text.Length; i++)
                if (buf[off + i] != (byte)text[i]) return false;
            return true;
        }

        private static bool GetBit(byte[] bmp, uint cluster)
        {
            if (cluster < 2u) return false;
            ulong idx = cluster - 2u;
            ulong byteIdx = idx >> 3;
            if (byteIdx >= (ulong)bmp.Length) return false;
            return (bmp[byteIdx] & (1 << (int)(idx & 7))) != 0;
        }

        private static void SetBit(byte[] bmp, uint cluster, bool val)
        {
            if (cluster < 2u) return;
            ulong idx = cluster - 2u;
            ulong byteIdx = idx >> 3;
            if (byteIdx >= (ulong)bmp.Length) return;
            byte mask = (byte)(1 << (int)(idx & 7));
            if (val) bmp[byteIdx] |= mask;
            else bmp[byteIdx] &= (byte)~mask;
        }

        private static ExFatBootGeometry ReadAndValidateBoot(IRawDiskIo io, long partStart, long partLenBytes)
        {
            if (partLenBytes < 24L * 512L)
                throw new InvalidDataException("Partition is too small to contain an exFAT boot region.");

            byte[] sec0 = io.ReadAt(partStart, 512);
            if (!MatchesAscii(sec0, 3, "EXFAT   "))
                throw new InvalidDataException("Partition does not contain a valid exFAT signature.");

            for (int z = 0x0B; z < 0x40; z++)
            {
                if (sec0[z] != 0)
                    throw new InvalidDataException(string.Format("exFAT MustBeZero boot field at +0x{0:X2} is non-zero.", z));
            }

            int bpsShift = sec0[0x6C];
            int spcShift = sec0[0x6D];
            if (bpsShift < 9 || bpsShift > 12)
                throw new InvalidDataException("Unsupported exFAT BytesPerSectorShift: " + bpsShift);
            if (spcShift < 0 || spcShift > 25 - bpsShift)
                throw new InvalidDataException("Invalid exFAT SectorsPerClusterShift: " + spcShift);

            int sectorSize = 1 << bpsShift;
            uint secPerClus = 1u << spcShift;
            long clusBytes64 = (long)sectorSize * secPerClus;
            if (clusBytes64 <= 0 || clusBytes64 > 33554432L)
                throw new InvalidDataException("Unsupported exFAT cluster size.");

            if (partLenBytes < 24L * sectorSize)
                throw new InvalidDataException("Partition length is smaller than 24 exFAT sectors.");

            byte[] mainBoot = io.ReadAt(partStart, 12 * sectorSize);
            if (mainBoot[510] != 0x55 || mainBoot[511] != 0xAA)
                throw new InvalidDataException("exFAT boot sector signature 0x55AA is missing.");

            for (int ext = 1; ext <= 8; ext++)
            {
                int extEnd = ext * sectorSize;
                bool okEnd = mainBoot[extEnd - 2] == 0x55 && mainBoot[extEnd - 1] == 0xAA;
                bool ok512 = mainBoot[ext * sectorSize + 510] == 0x55 && mainBoot[ext * sectorSize + 511] == 0xAA;
                if (!okEnd && !ok512)
                    throw new InvalidDataException("exFAT extended boot sector signature is invalid at sector " + ext + ".");
            }

            uint storedChk = BitConverter.ToUInt32(mainBoot, 11 * sectorSize);
            uint calcChk = RawDiskEngine.ComputeExFatChecksum(mainBoot, 11 * sectorSize, true);
            if (storedChk != calcChk)
                throw new InvalidDataException(string.Format(
                    "exFAT Main Boot Region checksum mismatch (stored 0x{0:X8}, expected 0x{1:X8}). Run 'chkdsk /f' before resizing.",
                    storedChk, calcChk));

            byte numFats = mainBoot[0x6E];
            if (numFats != 1)
                throw new NotSupportedException("TexFAT (NumberOfFats=" + numFats + ") is not supported for offline resize.");

            ushort fsRev = BitConverter.ToUInt16(mainBoot, 0x68);
            if (fsRev != 0x0100)
                throw new NotSupportedException(string.Format("Unsupported exFAT revision 0x{0:X4}.", fsRev));

            ushort volFlags = BitConverter.ToUInt16(mainBoot, 0x6A);
            if ((volFlags & 0x0002) != 0)
                throw new InvalidOperationException("exFAT VolumeDirty flag is set (volume was not cleanly dismounted). Run 'chkdsk <drive>: /f' first and retry.");
            if ((volFlags & 0x0004) != 0)
                throw new InvalidOperationException("exFAT MediaFailure flag is set on this volume. Run 'chkdsk <drive>: /f' first before resizing.");
            if ((volFlags & 0x0001) != 0)
                throw new NotSupportedException("exFAT ActiveFat flag is non-zero; run 'chkdsk <drive>: /f' before resizing.");

            ulong partOffSectors = BitConverter.ToUInt64(mainBoot, 0x40);
            ulong volLenSectors = BitConverter.ToUInt64(mainBoot, 0x48);
            uint fatOff = BitConverter.ToUInt32(mainBoot, 0x50);
            uint fatLen = BitConverter.ToUInt32(mainBoot, 0x54);
            uint heapOff = BitConverter.ToUInt32(mainBoot, 0x58);
            uint clusCount = BitConverter.ToUInt32(mainBoot, 0x5C);
            uint rootFirst = BitConverter.ToUInt32(mainBoot, 0x60);
            uint serial = BitConverter.ToUInt32(mainBoot, 0x64);

            if (fatOff < 24u || fatLen == 0u || (ulong)fatOff + fatLen > heapOff)
                throw new InvalidDataException("Invalid exFAT FAT or ClusterHeap offset.");
            if (clusCount < 8u || clusCount > uint.MaxValue - 10u)
                throw new InvalidDataException("Invalid exFAT ClusterCount: " + clusCount);
            ulong minVolSectors = (ulong)heapOff + (ulong)clusCount * secPerClus;
            if (minVolSectors > volLenSectors || volLenSectors * (ulong)sectorSize > (ulong)partLenBytes)
                throw new InvalidDataException("exFAT VolumeLength exceeds partition bounds or ClusterHeap geometry.");
            ulong fatCap = ((ulong)fatLen * (ulong)sectorSize) / 4UL;
            if (fatCap < (ulong)clusCount + 2UL)
                throw new InvalidDataException("exFAT FAT length is smaller than ClusterCount + 2.");
            if (rootFirst < 2u || rootFirst > clusCount + 1u)
                throw new InvalidDataException("exFAT FirstClusterOfRootDirectory is out of range.");

            return new ExFatBootGeometry
            {
                PartStartOffset = partStart,
                MainBootRegion = mainBoot,
                PartitionOffsetSectors = partOffSectors,
                VolumeLengthSectors = volLenSectors,
                FatOffsetSectors = fatOff,
                FatLengthSectors = fatLen,
                ClusterHeapOffsetSectors = heapOff,
                ClusterCount = clusCount,
                RootFirstCluster = rootFirst,
                VolumeSerial = serial,
                VolumeFlags = volFlags,
                BytesPerSectorShift = bpsShift,
                SectorsPerClusterShift = spcShift,
                SectorSize = sectorSize,
                SectorsPerCluster = secPerClus,
                ClusterSize = (int)clusBytes64,
                BootChecksum = storedChk
            };
        }

        private static byte[] ReadClusterChainBytes(IRawDiskIo io, ExFatBootGeometry boot, List<uint> clusters)
        {
            long totalLen = (long)clusters.Count * boot.ClusterSize;
            if (totalLen > 256L * 1048576L)
                throw new NotSupportedException("exFAT metadata stream exceeds 256 MiB limit.");

            byte[] result = new byte[totalLen];
            int pos = 0;
            int i = 0;
            while (i < clusters.Count)
            {
                int run = 1;
                while (i + run < clusters.Count && clusters[i + run] == clusters[i] + (uint)run && (long)(run + 1) * boot.ClusterSize <= 4L * 1048576L)
                    run++;

                byte[] chunk = io.ReadAt(boot.ClusterToDiskByte(clusters[i]), run * boot.ClusterSize);
                Array.Copy(chunk, 0, result, pos, Math.Min(chunk.Length, result.Length - pos));
                pos += run * boot.ClusterSize;
                i += run;
            }
            return result;
        }

        private static void WriteClusterChainBytes(IRawDiskIo io, ExFatBootGeometry boot, List<uint> clusters, byte[] data)
        {
            int pos = 0;
            int i = 0;
            while (i < clusters.Count && pos < data.Length)
            {
                int run = 1;
                while (i + run < clusters.Count && clusters[i + run] == clusters[i] + (uint)run && (long)(run + 1) * boot.ClusterSize <= 4L * 1048576L)
                    run++;

                int runBytes = run * boot.ClusterSize;
                byte[] chunk = new byte[runBytes];
                int copyLen = Math.Min(runBytes, data.Length - pos);
                Array.Copy(data, pos, chunk, 0, copyLen);
                io.WriteAt(boot.ClusterToDiskByte(clusters[i]), chunk);
                pos += runBytes;
                i += run;
            }
        }

        private static bool IsContiguous(List<uint> clusters)
        {
            if (clusters == null || clusters.Count <= 1) return true;
            for (int i = 1; i < clusters.Count; i++)
            {
                if (clusters[i] != clusters[i - 1] + 1u) return false;
            }
            return true;
        }

        private static List<uint> BuildContiguousRange(uint firstCluster, ulong dataLength, int clusterSize, uint maxClusterCount, string contextName)
        {
            ulong needed = dataLength == 0UL ? 1UL : (dataLength + (ulong)clusterSize - 1UL) / (ulong)clusterSize;
            if (needed > (ulong)maxClusterCount || firstCluster < 2u || (ulong)firstCluster + needed - 1UL > (ulong)maxClusterCount + 1UL)
                throw new InvalidDataException(string.Format("exFAT contiguous {0} cluster range [{1} + {2}] exceeds ClusterCount {3}. Run 'chkdsk /f' first.",
                    contextName, firstCluster, needed, maxClusterCount));

            var list = new List<uint>((int)needed);
            for (uint k = 0; k < (uint)needed; k++)
                list.Add(firstCluster + k);
            return list;
        }

        private static bool IntersectsRange(List<uint> clusters, long minC, long maxC)
        {
            if (clusters == null || minC > maxC) return false;
            for (int i = 0; i < clusters.Count; i++)
            {
                long c = clusters[i];
                if (c >= minC && c <= maxC) return true;
            }
            return false;
        }

        private static void ClaimClusters(PreFlightVolumeState st, List<uint> clusters, string name)
        {
            for (int i = 0; i < clusters.Count; i++)
            {
                uint c = clusters[i];
                if (GetBit(st.OwnedBitmap, c))
                    throw new InvalidDataException(string.Format("exFAT cross-linked cluster {0} detected on {1}. Run 'chkdsk /f' first.", c, name));
                if (!GetBit(st.DiskBitmap, c))
                    throw new InvalidDataException(string.Format("exFAT Allocation Bitmap mismatch: cluster {0} in {1} is marked free on disk. Run 'chkdsk /f' first.", c, name));
                SetBit(st.OwnedBitmap, c, true);
            }
        }

        private static PreFlightVolumeState RunPreFlightScan(IRawDiskIo io, long partStart, long oldLengthBytes, long evacMinCluster, long evacMaxCluster, bool trackAllFileObjects)
        {
            ExFatBootGeometry boot = ReadAndValidateBoot(io, partStart, oldLengthBytes);
            var fat = new PagedFatTable(io, boot.FatStartByte, boot.FatLengthSectors, boot.SectorSize);

            int bitmapByteLen = (int)((boot.ClusterCount + 7u) / 8u);
            var st = new PreFlightVolumeState
            {
                Boot = boot,
                Fat = fat,
                OwnedBitmap = new byte[bitmapByteLen],
                EvacZoneMinCluster = evacMinCluster,
                EvacZoneMaxCluster = evacMaxCluster
            };

            List<uint> rootClusters = fat.ReadFatChain(boot.RootFirstCluster, boot.ClusterCount, "RootDirectory");
            byte[] rootBytes = ReadClusterChainBytes(io, boot, rootClusters);

            // Locate Allocation Bitmap (0x81) & Up-Case Table (0x82) inside Root Directory first
            for (int pos = 0; pos + 32 <= rootBytes.Length; pos += 32)
            {
                byte eType = rootBytes[pos];
                if (eType == 0x00) break;
                if ((eType & 0x80) == 0) continue;

                if (eType == 0x81)
                {
                    if (st.BitmapObj != null)
                        throw new InvalidDataException("Multiple exFAT Allocation Bitmap (0x81) entries found.");
                    if ((rootBytes[pos + 1] & 1) != 0)
                        throw new NotSupportedException("Secondary TexFAT Allocation Bitmap is not supported.");

                    uint firstClus = BitConverter.ToUInt32(rootBytes, pos + 20);
                    ulong dataLen = BitConverter.ToUInt64(rootBytes, pos + 24);
                    ulong expectedBmpLen = (boot.ClusterCount + 7UL) / 8UL;
                    if (dataLen < expectedBmpLen)
                        throw new InvalidDataException("exFAT Allocation Bitmap DataLength is smaller than ClusterCount / 8.");

                    List<uint> bmpClusters = fat.ReadFatChain(firstClus, boot.ClusterCount, "AllocationBitmap");
                    if ((ulong)bmpClusters.Count * (ulong)boot.ClusterSize < expectedBmpLen)
                        throw new InvalidDataException("exFAT Allocation Bitmap cluster chain is truncated.");

                    byte[] rawBmp = ReadClusterChainBytes(io, boot, bmpClusters);
                    st.DiskBitmap = new byte[bitmapByteLen];
                    Array.Copy(rawBmp, 0, st.DiskBitmap, 0, bitmapByteLen);

                    st.BitmapObj = new ExFatObjectInfo
                    {
                        DirIndex = -1,
                        ParentEntryOffset = pos,
                        EntrySetBytes = 32,
                        FirstCluster = firstClus,
                        DataLength = dataLen,
                        NoFatChain = false,
                        IsDirectory = false,
                        Clusters = bmpClusters
                    };
                }
                else if (eType == 0x82)
                {
                    if (st.UpCaseObj != null)
                        throw new InvalidDataException("Multiple exFAT Up-Case Table (0x82) entries found.");

                    uint tableChk = BitConverter.ToUInt32(rootBytes, pos + 4);
                    uint firstClus = BitConverter.ToUInt32(rootBytes, pos + 20);
                    ulong dataLen = BitConverter.ToUInt64(rootBytes, pos + 24);
                    List<uint> upClusters = fat.ReadFatChain(firstClus, boot.ClusterCount, "UpCaseTable");
                    if (dataLen == 0UL || dataLen > (ulong)upClusters.Count * (ulong)boot.ClusterSize)
                        throw new InvalidDataException("exFAT Up-Case Table cluster chain is truncated.");

                    byte[] upRaw = ReadClusterChainBytes(io, boot, upClusters);
                    uint actualUpChk = RawDiskEngine.ComputeExFatChecksum(upRaw, (int)dataLen, false);
                    if (actualUpChk != tableChk)
                        throw new InvalidDataException("exFAT Up-Case Table checksum mismatch. Run 'chkdsk /f' first.");

                    st.UpCaseObj = new ExFatObjectInfo
                    {
                        DirIndex = -1,
                        ParentEntryOffset = pos,
                        EntrySetBytes = 32,
                        FirstCluster = firstClus,
                        DataLength = dataLen,
                        NoFatChain = false,
                        IsDirectory = false,
                        Clusters = upClusters
                    };
                }
            }

            if (st.BitmapObj == null || st.UpCaseObj == null)
                throw new InvalidDataException("exFAT Root Directory is missing required Allocation Bitmap (0x81) or Up-Case Table (0x82) entry.");

            // Claim system metadata objects against DiskBitmap & OwnedBitmap
            ClaimClusters(st, st.BitmapObj.Clusters, "AllocationBitmap");
            ClaimClusters(st, st.UpCaseObj.Clusters, "UpCaseTable");
            ClaimClusters(st, rootClusters, "RootDirectory");

            var rootObj = new ExFatObjectInfo
            {
                DirIndex = -1,
                FirstCluster = boot.RootFirstCluster,
                DataLength = (ulong)rootClusters.Count * (ulong)boot.ClusterSize,
                NoFatChain = false,
                IsDirectory = true,
                Clusters = rootClusters
            };
            st.RootDir = new ExFatDirNode
            {
                Index = 0,
                ParentDirIndex = -1,
                ObjInfo = rootObj
            };
            st.AllDirs.Add(st.RootDir);

            // BFS walk across Root + all Subdirectories
            var queue = new Queue<int>();
            queue.Enqueue(0);

            while (queue.Count > 0)
            {
                int dirIdx = queue.Dequeue();
                ExFatDirNode dirNode = st.AllDirs[dirIdx];
                byte[] dirBytes = dirIdx == 0 ? rootBytes : ReadClusterChainBytes(io, boot, dirNode.ObjInfo.Clusters);

                int pos = 0;
                while (pos + 32 <= dirBytes.Length)
                {
                    byte eType = dirBytes[pos];
                    if (eType == 0x00)
                        break;
                    if ((eType & 0x80) == 0)
                    {
                        pos += 32;
                        continue;
                    }

                    if (eType == 0x81 || eType == 0x82 || eType == 0x83 || eType == 0xA0)
                    {
                        if ((eType == 0x81 || eType == 0x82 || eType == 0x83) && dirIdx != 0)
                            throw new InvalidDataException(string.Format("Unexpected root metadata entry 0x{0:X2} in subdirectory.", eType));
                        if (eType == 0x81 || eType == 0x82)
                            dirNode.HasAnyNonZeroChild = true;
                        pos += 32;
                        continue;
                    }

                    if (eType == 0x85)
                    {
                        int secCount = dirBytes[pos + 1];
                        int setBytes = (1 + secCount) * 32;
                        if (secCount < 2 || pos + setBytes > dirBytes.Length)
                            throw new InvalidDataException("Malformed exFAT File Directory Entry set at offset " + pos + ". Run 'chkdsk /f' first.");
                        if (dirBytes[pos + 32] != 0xC0)
                            throw new InvalidDataException("exFAT Entry Set missing Stream Extension (0xC0) at offset " + pos + ". Run 'chkdsk /f' first.");

                        ushort storedSetChk = BitConverter.ToUInt16(dirBytes, pos + 2);
                        ushort calcSetChk = ComputeExFatEntrySetChecksum(dirBytes, pos, setBytes);
                        if (storedSetChk != calcSetChk)
                            throw new InvalidDataException(string.Format(
                                "exFAT Directory Entry SetChecksum mismatch (stored 0x{0:X4}, expected 0x{1:X4}). Run 'chkdsk /f' first.",
                                storedSetChk, calcSetChk));

                        ushort fileAttrs = BitConverter.ToUInt16(dirBytes, pos + 4);
                        bool isSubdir = (fileAttrs & 0x0010) != 0;
                        byte secFlags = dirBytes[pos + 32 + 1];
                        bool noFatChain = (secFlags & 0x02) != 0;
                        uint firstClus = BitConverter.ToUInt32(dirBytes, pos + 32 + 20);
                        ulong dataLen = BitConverter.ToUInt64(dirBytes, pos + 32 + 24);

                        if (firstClus == 0u)
                        {
                            if (dataLen != 0UL || isSubdir)
                                throw new InvalidDataException("exFAT entry has FirstCluster=0 with non-zero DataLength or directory flag. Run 'chkdsk /f' first.");
                            pos += setBytes;
                            continue;
                        }

                        dirNode.HasAnyNonZeroChild = true;

                        List<uint> itemClusters = noFatChain
                            ? BuildContiguousRange(firstClus, dataLen, boot.ClusterSize, boot.ClusterCount, isSubdir ? "Subdirectory" : "File")
                            : fat.ReadFatChain(firstClus, boot.ClusterCount, isSubdir ? "Subdirectory" : "File");

                        ClaimClusters(st, itemClusters, isSubdir ? "Subdirectory" : "File");

                        bool hitsEvac = evacMinCluster <= evacMaxCluster && IntersectsRange(itemClusters, evacMinCluster, evacMaxCluster);
                        if (isSubdir || hitsEvac || trackAllFileObjects)
                        {
                            var childObj = new ExFatObjectInfo
                            {
                                DirIndex = dirIdx,
                                ParentEntryOffset = pos,
                                EntrySetBytes = setBytes,
                                FirstCluster = firstClus,
                                DataLength = dataLen,
                                NoFatChain = noFatChain,
                                IsDirectory = isSubdir,
                                Clusters = itemClusters
                            };

                            if (isSubdir)
                            {
                                var childDir = new ExFatDirNode
                                {
                                    Index = st.AllDirs.Count,
                                    ParentDirIndex = dirIdx,
                                    ObjInfo = childObj
                                };
                                st.AllDirs.Add(childDir);
                                queue.Enqueue(childDir.Index);
                            }
                            else
                            {
                                st.TrackedFiles.Add(childObj);
                            }
                        }

                        pos += setBytes;
                        continue;
                    }

                    throw new NotSupportedException(string.Format(
                        "Unsupported or unknown exFAT active directory entry 0x{0:X2} encountered. Run 'chkdsk /f' or format instead of offline resize.", eType));
                }
            }

            // Check evacuation zone for orphaned allocated clusters and bad clusters
            if (evacMinCluster <= evacMaxCluster)
            {
                uint cMin = (uint)Math.Max(2L, evacMinCluster);
                uint cMax = (uint)Math.Min((long)boot.ClusterCount + 1L, evacMaxCluster);
                for (uint c = cMin; c <= cMax; c++)
                {
                    bool onDisk = GetBit(st.DiskBitmap, c);
                    bool owned = GetBit(st.OwnedBitmap, c);
                    if (onDisk != owned)
                        throw new InvalidDataException(string.Format(
                            "exFAT Allocation Bitmap inconsistency at cluster {0} (disk={1}, owned={2}). Run 'chkdsk /f' first.",
                            c, onDisk ? 1 : 0, owned ? 1 : 0));

                    if (evacMinCluster == 2L && fat.GetEntry(c) == FAT_BAD_CLUSTER)
                        throw new InvalidOperationException(string.Format(
                            "Cannot expand exFAT FAT into cluster {0} because it is marked BAD (0xFFFFFFF7).", c));
                }
            }

            return st;
        }

        private static List<uint> CollectFreeClusters(
            PreFlightVolumeState st,
            uint rangeMin,
            uint rangeMax,
            int neededCount,
            uint preferMin)
        {
            var freeList = new List<uint>(neededCount);
            if (neededCount <= 0 || rangeMin > rangeMax) return freeList;

            // Pass 1: Preferred range (e.g. appended tail [oldClusterCount + 2 .. newClusterCount + K + 1] during Grow K > 0)
            if (preferMin > rangeMin && preferMin <= rangeMax)
            {
                for (uint c = preferMin; c <= rangeMax && freeList.Count < neededCount; c++)
                {
                    if (c > st.Boot.ClusterCount + 1u || (!GetBit(st.DiskBitmap, c) && !GetBit(st.OwnedBitmap, c)))
                    {
                        if (c <= st.Boot.ClusterCount + 1u && st.Fat.GetEntry(c) == FAT_BAD_CLUSTER)
                            continue;
                        freeList.Add(c);
                    }
                }
            }

            // Pass 2: Remaining range [rangeMin .. min(rangeMax, preferMin - 1)]
            uint pass2Max = (preferMin > rangeMin && preferMin <= rangeMax) ? (preferMin - 1u) : rangeMax;
            for (uint c = rangeMin; c <= pass2Max && freeList.Count < neededCount; c++)
            {
                bool used = c <= st.Boot.ClusterCount + 1u && (GetBit(st.DiskBitmap, c) || GetBit(st.OwnedBitmap, c));
                if (used) continue;
                if (c <= st.Boot.ClusterCount + 1u && st.Fat.GetEntry(c) == FAT_BAD_CLUSTER) continue;
                freeList.Add(c);
            }

            return freeList;
        }

        private static void CopyClusterList(
            IRawDiskIo io,
            ExFatBootGeometry boot,
            List<uint> srcClusters,
            List<uint> dstClusters,
            Action<int, string> progressCallback,
            int progStart,
            int progEnd,
            string statusPrefix)
        {
            if (srcClusters.Count == 0) return;
            int total = srcClusters.Count;
            int i = 0;
            while (i < total)
            {
                int run = 1;
                while (i + run < total &&
                       srcClusters[i + run] == srcClusters[i] + (uint)run &&
                       dstClusters[i + run] == dstClusters[i] + (uint)run &&
                       (long)(run + 1) * boot.ClusterSize <= 1048576L)
                {
                    run++;
                }

                byte[] buf = io.ReadAt(boot.ClusterToDiskByte(srcClusters[i]), run * boot.ClusterSize);
                io.WriteAt(boot.ClusterToDiskByte(dstClusters[i]), buf);
                i += run;

                if (progressCallback != null)
                {
                    int pct = progStart + (int)((long)i * (progEnd - progStart) / total);
                    progressCallback(pct, string.Format("{0} ({1}/{2} clusters)", statusPrefix, i, total));
                }
            }
        }

        private static void MovePartitionRaw(IRawDiskIo io, long oldStart, long newStart, long length, Action<int, string> progressCallback, int progStart, int progEnd)
        {
            if (oldStart == newStart || length <= 0) return;
            const int chunkSize = 1048576;
            if (newStart < oldStart)
            {
                long moved = 0;
                while (moved < length)
                {
                    int n = (int)Math.Min(chunkSize, length - moved);
                    byte[] buf = io.ReadAt(oldStart + moved, n);
                    io.WriteAt(newStart + moved, buf);
                    moved += n;
                    if (progressCallback != null)
                    {
                        int pct = progStart + (int)(moved * (progEnd - progStart) / Math.Max(1L, length));
                        progressCallback(pct, string.Format("Moving exFAT sectors left ({0} / {1})...",
                            ByteFormatter.FormatBytes(moved), ByteFormatter.FormatBytes(length)));
                    }
                }
            }
            else
            {
                long remaining = length;
                while (remaining > 0)
                {
                    int n = (int)Math.Min(chunkSize, remaining);
                    long pos = remaining - n;
                    byte[] buf = io.ReadAt(oldStart + pos, n);
                    io.WriteAt(newStart + pos, buf);
                    remaining -= n;
                    if (progressCallback != null)
                    {
                        long moved = length - remaining;
                        int pct = progStart + (int)(moved * (progEnd - progStart) / Math.Max(1L, length));
                        progressCallback(pct, string.Format("Moving exFAT sectors right ({0} / {1})...",
                            ByteFormatter.FormatBytes(moved), ByteFormatter.FormatBytes(length)));
                    }
                }

                long clearLen = Math.Min(131072L, newStart - oldStart);
                if (clearLen > 0)
                    io.WriteAt(oldStart, new byte[(int)clearLen]);
            }
        }

        private static void WriteMainAndBackupBoot(
            IRawDiskIo io,
            long partStart,
            byte[] bootRegion,
            ulong newPartOffsetSectors,
            ulong newVolLengthSectors,
            uint newFatLength,
            uint newHeapOffset,
            uint newClusterCount,
            uint newRootFirst,
            byte percentInUse,
            bool updateVirtualUsedHint,
            long usedBytes)
        {
            int sectorSize = 1 << bootRegion[0x6C];
            BitConverter.GetBytes(newPartOffsetSectors).CopyTo(bootRegion, 0x40);
            BitConverter.GetBytes(newVolLengthSectors).CopyTo(bootRegion, 0x48);
            BitConverter.GetBytes(newFatLength).CopyTo(bootRegion, 0x54);
            BitConverter.GetBytes(newHeapOffset).CopyTo(bootRegion, 0x58);
            BitConverter.GetBytes(newClusterCount).CopyTo(bootRegion, 0x5C);
            BitConverter.GetBytes(newRootFirst).CopyTo(bootRegion, 0x60);
            bootRegion[0x70] = percentInUse;

            if (updateVirtualUsedHint && MatchesAscii(bootRegion, 0x1C0, "WPU:"))
                BitConverter.GetBytes(usedBytes).CopyTo(bootRegion, 0x1C4);

            uint chk = RawDiskEngine.ComputeExFatChecksum(bootRegion, 11 * sectorSize, true);
            byte[] chkBytes = BitConverter.GetBytes(chk);
            int slots = sectorSize / 4;
            int chkOff = 11 * sectorSize;
            for (int i = 0; i < slots; i++)
                Array.Copy(chkBytes, 0, bootRegion, chkOff + i * 4, 4);

            io.WriteAt(partStart, bootRegion);
            io.WriteAt(partStart + 12L * sectorSize, bootRegion);
        }

        private static void PatchAndWriteDirectory(
            IRawDiskIo io,
            ExFatBootGeometry readBoot,
            ExFatDirNode dirNode,
            uint bitmapFirstNew,
            ulong bitmapDataLenNew,
            uint upcaseFirstNew,
            uint clusterShiftK)
        {
            byte[] dirBuf = ReadClusterChainBytes(io, readBoot, dirNode.ObjInfo.Clusters);
            int pos = 0;
            while (pos + 32 <= dirBuf.Length)
            {
                byte eType = dirBuf[pos];
                if (eType == 0x00) break;
                if ((eType & 0x80) == 0)
                {
                    pos += 32;
                    continue;
                }

                if (eType == 0x81 && dirNode.Index == 0)
                {
                    BitConverter.GetBytes(bitmapFirstNew).CopyTo(dirBuf, pos + 20);
                    BitConverter.GetBytes(bitmapDataLenNew).CopyTo(dirBuf, pos + 24);
                    pos += 32;
                    continue;
                }
                if (eType == 0x82 && dirNode.Index == 0)
                {
                    BitConverter.GetBytes(upcaseFirstNew).CopyTo(dirBuf, pos + 20);
                    pos += 32;
                    continue;
                }
                if (eType == 0x83 || eType == 0xA0)
                {
                    pos += 32;
                    continue;
                }

                if (eType == 0x85)
                {
                    int secCount = dirBuf[pos + 1];
                    int setBytes = (1 + secCount) * 32;
                    if (secCount < 2 || pos + setBytes > dirBuf.Length || dirBuf[pos + 32] != 0xC0)
                        throw new InvalidDataException("Corrupt 0x85 entry encountered during directory update.");

                    bool touched = false;
                    EvacOverride ov;
                    if (dirNode.ChildOverrides != null && dirNode.ChildOverrides.TryGetValue(pos, out ov))
                    {
                        BitConverter.GetBytes(ov.NewFirstCluster).CopyTo(dirBuf, pos + 32 + 20);
                        if (ov.ForceFatChain)
                            dirBuf[pos + 32 + 1] &= 0xFD; // clear bit 1 (NoFatChain)
                        touched = true;
                    }
                    else if (clusterShiftK > 0u)
                    {
                        uint oldFirst = BitConverter.ToUInt32(dirBuf, pos + 32 + 20);
                        if (oldFirst != 0u)
                        {
                            BitConverter.GetBytes(oldFirst - clusterShiftK).CopyTo(dirBuf, pos + 32 + 20);
                            touched = true;
                        }
                    }

                    if (touched)
                    {
                        ushort newChk = ComputeExFatEntrySetChecksum(dirBuf, pos, setBytes);
                        BitConverter.GetBytes(newChk).CopyTo(dirBuf, pos + 2);
                    }

                    pos += setBytes;
                    continue;
                }

                pos += 32;
            }

            WriteClusterChainBytes(io, readBoot, dirNode.ObjInfo.Clusters, dirBuf);
        }

        private static void RecordDirOverride(ExFatDirNode parentDir, long entryOffset, uint newFirstCluster, bool forceFatChain)
        {
            if (parentDir.ChildOverrides == null)
                parentDir.ChildOverrides = new Dictionary<long, EvacOverride>();
            parentDir.ChildOverrides[entryOffset] = new EvacOverride
            {
                NewFirstCluster = newFirstCluster,
                ForceFatChain = forceFatChain
            };
            parentDir.HasDirtyChildren = true;
        }

        private static void ShrinkExFatInPlace(
            IRawDiskIo io,
            long partStart,
            long oldLengthBytes,
            long newLengthBytes,
            bool isVirtualImage,
            Action<int, string> progressCallback)
        {
            if (progressCallback != null) progressCallback(12, "Validating exFAT volume & scanning clusters to evacuate...");

            // First validate boot to know newClusterCount for evac range
            ExFatBootGeometry preBoot = ReadAndValidateBoot(io, partStart, oldLengthBytes);
            ulong newTotalSectors = (ulong)(newLengthBytes / preBoot.SectorSize);
            if (newTotalSectors <= preBoot.ClusterHeapOffsetSectors)
                throw new InvalidOperationException("Target size is smaller than the exFAT FAT + Cluster Heap header.");

            ulong newClus64 = (newTotalSectors - preBoot.ClusterHeapOffsetSectors) / preBoot.SectorsPerCluster;
            if (newClus64 < 8UL || newClus64 >= preBoot.ClusterCount)
            {
                if (newClus64 < 8UL)
                    throw new InvalidOperationException("Target size is too small for a valid exFAT cluster heap (minimum 8 clusters).");
            }

            uint newClusterCount = (uint)Math.Min((long)uint.MaxValue - 10L, (long)newClus64);
            if (newClusterCount >= preBoot.ClusterCount)
            {
                // Partition trimmed within trailing unused sector remainder (< 1 cluster)
                ulong newBmpBytesTrim = (newClusterCount + 7UL) / 8UL;
                PreFlightVolumeState stTrim = RunPreFlightScan(io, partStart, oldLengthBytes, long.MaxValue, 0L, false);
                long usedBytesTrim = CountAllocatedClusters(stTrim.OwnedBitmap, preBoot.ClusterCount) * (long)preBoot.ClusterSize;
                byte pctTrim = (byte)Math.Min(100UL, ((ulong)CountAllocatedClusters(stTrim.OwnedBitmap, newClusterCount) * 100UL) / newClusterCount);
                WriteMainAndBackupBoot(io, partStart, preBoot.MainBootRegion, (ulong)(partStart / preBoot.SectorSize),
                    newTotalSectors, preBoot.FatLengthSectors, preBoot.ClusterHeapOffsetSectors, newClusterCount,
                    preBoot.RootFirstCluster, pctTrim, isVirtualImage, usedBytesTrim);
                return;
            }

            uint maxValidNewCluster = newClusterCount + 1u;
            PreFlightVolumeState st = RunPreFlightScan(io, partStart, oldLengthBytes, (long)maxValidNewCluster + 1L, (long)preBoot.ClusterCount + 1L, false);
            ExFatBootGeometry boot = st.Boot;

            ulong newBitmapBytes = (newClusterCount + 7UL) / 8UL;
            int newBitmapClusters = (int)Math.Max(1UL, (newBitmapBytes + (ulong)boot.ClusterSize - 1UL) / (ulong)boot.ClusterSize);

            // Identify non-bitmap clusters >= newClusterCount + 2 that need evacuation
            var objectsToEvac = new List<ExFatObjectInfo>();
            int totalDataClustersToEvac = 0;

            if (IntersectsRange(st.UpCaseObj.Clusters, maxValidNewCluster + 1L, long.MaxValue))
            {
                objectsToEvac.Add(st.UpCaseObj);
                totalDataClustersToEvac += CountTailClusters(st.UpCaseObj.Clusters, maxValidNewCluster);
            }
            for (int i = 0; i < st.AllDirs.Count; i++)
            {
                var d = st.AllDirs[i].ObjInfo;
                if (IntersectsRange(d.Clusters, maxValidNewCluster + 1L, long.MaxValue))
                {
                    objectsToEvac.Add(d);
                    totalDataClustersToEvac += CountTailClusters(d.Clusters, maxValidNewCluster);
                }
            }
            for (int i = 0; i < st.TrackedFiles.Count; i++)
            {
                var f = st.TrackedFiles[i];
                if (IntersectsRange(f.Clusters, maxValidNewCluster + 1L, long.MaxValue))
                {
                    objectsToEvac.Add(f);
                    totalDataClustersToEvac += CountTailClusters(f.Clusters, maxValidNewCluster);
                }
            }

            // Determine how many clusters Bitmap currently occupies inside <= maxValidNewCluster
            var keptBitmapClusters = new List<uint>();
            var droppedBitmapClusters = new List<uint>();
            for (int i = 0; i < st.BitmapObj.Clusters.Count; i++)
            {
                uint c = st.BitmapObj.Clusters[i];
                if (keptBitmapClusters.Count < newBitmapClusters && c <= maxValidNewCluster)
                    keptBitmapClusters.Add(c);
                else
                    droppedBitmapClusters.Add(c);
            }
            int needExtraBitmapClusters = Math.Max(0, newBitmapClusters - keptBitmapClusters.Count);

            // Free any dropped bitmap clusters (still in <= maxValidNewCluster) from Owned/DiskBitmap BEFORE picking destinations (we never preserve old Bitmap data)
            for (int i = 0; i < droppedBitmapClusters.Count; i++)
            {
                uint c = droppedBitmapClusters[i];
                SetBit(st.OwnedBitmap, c, false);
                SetBit(st.DiskBitmap, c, false);
                st.Fat.SetEntry(c, 0u);
            }

            int totalNeededInLower = totalDataClustersToEvac + needExtraBitmapClusters;
            List<uint> freePool = CollectFreeClusters(st, 2u, maxValidNewCluster, totalNeededInLower, 0u);
            if (freePool.Count < totalNeededInLower)
                throw new InvalidOperationException(string.Format(
                    "Not enough free space below target size ({0} clusters needed for evacuation, only {1} available).",
                    totalNeededInLower, freePool.Count));

            // Mark all picked destinations as owned
            for (int i = 0; i < freePool.Count; i++)
                SetBit(st.OwnedBitmap, freePool[i], true);

            // Finalize Bitmap's new cluster list
            int poolCursor = 0;
            for (int i = 0; i < needExtraBitmapClusters; i++)
                keptBitmapClusters.Add(freePool[poolCursor++]);

            // Evacuate tail clusters for all affected data/directory/upcase objects
            var copySources = new List<uint>(totalDataClustersToEvac);
            var copyDests = new List<uint>(totalDataClustersToEvac);

            for (int oi = 0; oi < objectsToEvac.Count; oi++)
            {
                ExFatObjectInfo obj = objectsToEvac[oi];
                bool wasContiguous = obj.NoFatChain;
                for (int ci = 0; ci < obj.Clusters.Count; ci++)
                {
                    uint oldC = obj.Clusters[ci];
                    if (oldC <= maxValidNewCluster) continue;

                    uint newC = freePool[poolCursor++];
                    copySources.Add(oldC);
                    copyDests.Add(newC);
                    SetBit(st.OwnedBitmap, oldC, false);
                    obj.Clusters[ci] = newC;
                }

                if (wasContiguous && !IsContiguous(obj.Clusters))
                {
                    obj.NoFatChain = false;
                    obj.DirtyMetadata = true;
                    obj.DirtyFat = true;
                }
                else
                {
                    obj.DirtyFat = !obj.NoFatChain;
                    if (obj.FirstCluster != obj.Clusters[0])
                        obj.DirtyMetadata = true;
                }

                obj.FirstCluster = obj.Clusters[0];
            }

            if (copySources.Count > 0)
                CopyClusterList(io, boot, copySources, copyDests, progressCallback, 18, 52, "Evacuating tail clusters");

            if (progressCallback != null) progressCallback(56, "Updating exFAT cluster chains, directory entries & allocation bitmap...");

            // Update FAT: clear vacated slots, write full chains for flipped NoFatChain / dirty FAT objects & Bitmap
            for (int i = 0; i < copySources.Count; i++)
                st.Fat.SetEntry(copySources[i], 0u);

            // Update Bitmap chain in FAT
            st.BitmapObj.Clusters = keptBitmapClusters;
            st.BitmapObj.FirstCluster = keptBitmapClusters[0];
            st.BitmapObj.DataLength = newBitmapBytes;
            for (int i = 0; i < keptBitmapClusters.Count; i++)
                st.Fat.SetEntry(keptBitmapClusters[i], (i + 1 < keptBitmapClusters.Count) ? keptBitmapClusters[i + 1] : FAT_EOC);

            for (int oi = 0; oi < objectsToEvac.Count; oi++)
            {
                ExFatObjectInfo obj = objectsToEvac[oi];
                if (obj.DirtyFat)
                {
                    for (int i = 0; i < obj.Clusters.Count; i++)
                        st.Fat.SetEntry(obj.Clusters[i], (i + 1 < obj.Clusters.Count) ? obj.Clusters[i + 1] : FAT_EOC);
                }

                if (obj.DirIndex >= 0 && obj.DirtyMetadata)
                {
                    ExFatDirNode parentDir = st.AllDirs[obj.DirIndex];
                    RecordDirOverride(parentDir, obj.ParentEntryOffset, obj.FirstCluster, !obj.NoFatChain);
                }
            }

            // Truncate leftover FAT entries above maxValidNewCluster (best effort within old cluster count)
            for (uint c = maxValidNewCluster + 1u; c <= boot.ClusterCount + 1u; c++)
            {
                if (st.Fat.GetEntry(c) != 0u)
                    st.Fat.SetEntry(c, 0u);
            }
            st.Fat.Flush();

            // Write modified directories + Root (Root always written to update 0x81 DataLength and 0x82 if moved)
            for (int i = 0; i < st.AllDirs.Count; i++)
            {
                ExFatDirNode d = st.AllDirs[i];
                if (i == 0 || d.HasDirtyChildren)
                    PatchAndWriteDirectory(io, boot, d, st.BitmapObj.FirstCluster, newBitmapBytes, st.UpCaseObj.FirstCluster, 0u);
            }

            // Write new Allocation Bitmap containing ONLY clusters in [2 .. maxValidNewCluster] owned by OwnedBitmap
            byte[] newBmpRaw = new byte[keptBitmapClusters.Count * boot.ClusterSize];
            int finalCopyLen = (int)Math.Min((long)newBitmapBytes, (long)st.OwnedBitmap.Length);
            Array.Copy(st.OwnedBitmap, 0, newBmpRaw, 0, finalCopyLen);
            // Clear any trailing unused bits in the final byte of the Allocation Bitmap
            int remBits = (int)(newClusterCount & 7u);
            if (remBits > 0 && newBitmapBytes > 0)
                newBmpRaw[newBitmapBytes - 1] &= (byte)((1 << remBits) - 1);
            WriteClusterChainBytes(io, boot, st.BitmapObj.Clusters, newBmpRaw);

            // Preserve any original non-Bitmap/non-UpCase Root first cluster change
            uint finalRootFirst = st.RootDir.ObjInfo.FirstCluster;
            long allocCount = CountAllocatedClusters(newBmpRaw, newClusterCount);
            byte percentInUse = (byte)Math.Min(100UL, ((ulong)allocCount * 100UL) / newClusterCount);
            long usedBytes = allocCount * (long)boot.ClusterSize;

            WriteMainAndBackupBoot(io, partStart, boot.MainBootRegion, (ulong)(partStart / boot.SectorSize),
                newTotalSectors, boot.FatLengthSectors, boot.ClusterHeapOffsetSectors, newClusterCount,
                finalRootFirst, percentInUse, isVirtualImage, usedBytes);
        }

        private static int CountTailClusters(List<uint> clusters, uint maxValid)
        {
            int count = 0;
            for (int i = 0; i < clusters.Count; i++)
                if (clusters[i] > maxValid) count++;
            return count;
        }

        private static long CountAllocatedClusters(byte[] bmp, uint clusterCount)
        {
            long count = 0;
            int fullBytes = (int)(clusterCount >> 3);
            for (int i = 0; i < fullBytes && i < bmp.Length; i++)
            {
                byte b = bmp[i];
                while (b != 0)
                {
                    count += (b & 1);
                    b >>= 1;
                }
            }
            int tailBits = (int)(clusterCount & 7u);
            if (tailBits > 0 && fullBytes < bmp.Length)
            {
                byte b = (byte)(bmp[fullBytes] & ((1 << tailBits) - 1));
                while (b != 0)
                {
                    count += (b & 1);
                    b >>= 1;
                }
            }
            return count;
        }

        private static void SolveGrowGeometry(
            ExFatBootGeometry boot,
            ulong newTotalSectors,
            out uint newFatLength,
            out uint shiftK,
            out uint newHeapOffset,
            out uint newClusterCount)
        {
            uint fatOff = boot.FatOffsetSectors;
            uint oldFatLen = boot.FatLengthSectors;
            uint oldHeapOff = boot.ClusterHeapOffsetSectors;
            uint spc = boot.SectorsPerCluster;
            int secSize = boot.SectorSize;

            newHeapOffset = oldHeapOff;
            shiftK = 0u;
            newFatLength = oldFatLen;
            newClusterCount = boot.ClusterCount;

            for (int iter = 0; iter < 3; iter++)
            {
                if (newTotalSectors <= newHeapOffset)
                    throw new InvalidOperationException("Target size is too small for grown exFAT FAT geometry.");

                ulong c64 = (newTotalSectors - newHeapOffset) / spc;
                if (c64 > (ulong)uint.MaxValue - 10UL)
                    throw new NotSupportedException("Target partition exceeds maximum exFAT cluster count.");

                newClusterCount = (uint)c64;
                ulong reqFatEntries = (ulong)newClusterCount + 2UL;
                ulong reqFatBytes = reqFatEntries * 4UL;
                uint reqFatSectors = (uint)((reqFatBytes + (ulong)secSize - 1UL) / (ulong)secSize);
                newFatLength = Math.Max(oldFatLen, reqFatSectors);

                ulong fatEnd = (ulong)fatOff + newFatLength;
                if (fatEnd <= newHeapOffset)
                    break;

                ulong extraSec = fatEnd - oldHeapOff;
                shiftK = (uint)((extraSec + spc - 1UL) / spc);
                newHeapOffset = oldHeapOff + shiftK * spc;
            }
        }

        private static void GrowExFatInPlace(
            IRawDiskIo io,
            long partStart,
            long oldLengthBytes,
            long newLengthBytes,
            bool isVirtualImage,
            Action<int, string> progressCallback)
        {
            if (progressCallback != null) progressCallback(58, "Computing expanded exFAT FAT & Cluster Heap geometry...");

            ExFatBootGeometry preBoot = ReadAndValidateBoot(io, partStart, oldLengthBytes);
            ulong newTotalSectors = (ulong)(newLengthBytes / preBoot.SectorSize);

            uint newFatLen;
            uint shiftK;
            uint newHeapOff;
            uint newClusterCount;
            SolveGrowGeometry(preBoot, newTotalSectors, out newFatLen, out shiftK, out newHeapOff, out newClusterCount);

            if (shiftK == 0u)
            {
                GrowExFatZeroShift(io, partStart, oldLengthBytes, newTotalSectors, newFatLen, newClusterCount, isVirtualImage, progressCallback);
                return;
            }

            GrowExFatWithShiftK(io, partStart, oldLengthBytes, newTotalSectors, newFatLen, shiftK, newHeapOff, newClusterCount, isVirtualImage, progressCallback);
        }

        private static void GrowExFatZeroShift(
            IRawDiskIo io,
            long partStart,
            long oldLengthBytes,
            ulong newTotalSectors,
            uint newFatLen,
            uint newClusterCount,
            bool isVirtualImage,
            Action<int, string> progressCallback)
        {
            if (progressCallback != null) progressCallback(62, "Validating exFAT metadata before expansion...");
            PreFlightVolumeState st = RunPreFlightScan(io, partStart, oldLengthBytes, long.MaxValue, 0L, false);
            ExFatBootGeometry boot = st.Boot;

            // Zero newly added FAT sectors (if any; note newFatLen <= HeapOffset - FatOffset)
            if (newFatLen > boot.FatLengthSectors)
            {
                long addStart = boot.FatStartByte + (long)boot.FatLengthSectors * boot.SectorSize;
                long addBytes = (long)(newFatLen - boot.FatLengthSectors) * boot.SectorSize;
                WriteZeroChunks(io, addStart, addBytes);
            }

            ulong newBmpBytes = (newClusterCount + 7UL) / 8UL;
            int needBmpClusters = (int)Math.Max(1UL, (newBmpBytes + (ulong)boot.ClusterSize - 1UL) / (ulong)boot.ClusterSize);
            int curBmpClusters = st.BitmapObj.Clusters.Count;

            byte[] finalOwned = new byte[(int)newBmpBytes];
            Array.Copy(st.OwnedBitmap, 0, finalOwned, 0, Math.Min(st.OwnedBitmap.Length, finalOwned.Length));

            if (needBmpClusters > curBmpClusters)
            {
                int extra = needBmpClusters - curBmpClusters;
                // Prefer appended range [oldClusterCount + 2 .. newClusterCount + 1], then free holes in [2 .. oldClusterCount + 1]
                List<uint> extraPool = CollectFreeClusters(st, 2u, newClusterCount + 1u, extra, boot.ClusterCount + 2u);
                if (extraPool.Count < extra)
                    throw new InvalidOperationException("Not enough free clusters to expand exFAT Allocation Bitmap.");

                var fatNew = new PagedFatTable(io, boot.FatStartByte, newFatLen, boot.SectorSize);
                uint prev = st.BitmapObj.Clusters[st.BitmapObj.Clusters.Count - 1];
                for (int i = 0; i < extraPool.Count; i++)
                {
                    uint nextC = extraPool[i];
                    SetBit(finalOwned, nextC, true);
                    st.BitmapObj.Clusters.Add(nextC);
                    fatNew.SetEntry(prev, nextC);
                    prev = nextC;
                }
                fatNew.SetEntry(prev, FAT_EOC);
                fatNew.Flush();
            }

            // Write expanded Allocation Bitmap across its clusters
            byte[] bmpBuf = new byte[needBmpClusters * boot.ClusterSize];
            Array.Copy(finalOwned, 0, bmpBuf, 0, finalOwned.Length);
            WriteClusterChainBytes(io, boot, st.BitmapObj.Clusters, bmpBuf);

            // Patch Root Directory 0x81 DataLength
            PatchAndWriteDirectory(io, boot, st.RootDir, st.BitmapObj.FirstCluster, newBmpBytes, st.UpCaseObj.FirstCluster, 0u);

            long allocCount = CountAllocatedClusters(finalOwned, newClusterCount);
            byte percentInUse = (byte)Math.Min(100UL, ((ulong)allocCount * 100UL) / newClusterCount);
            long usedBytes = allocCount * (long)boot.ClusterSize;

            WriteMainAndBackupBoot(io, partStart, boot.MainBootRegion, (ulong)(partStart / boot.SectorSize),
                newTotalSectors, newFatLen, boot.ClusterHeapOffsetSectors, newClusterCount,
                boot.RootFirstCluster, percentInUse, isVirtualImage, usedBytes);
        }

        private static void GrowExFatWithShiftK(
            IRawDiskIo io,
            long partStart,
            long oldLengthBytes,
            ulong newTotalSectors,
            uint newFatLen,
            uint shiftK,
            uint newHeapOffset,
            uint newClusterCount,
            bool isVirtualImage,
            Action<int, string> progressCallback)
        {
            uint evacMax = 2u + shiftK - 1u;
            if (progressCallback != null) progressCallback(62, string.Format("Scanning exFAT cluster tree (relocating front {0} cluster(s) for expanded FAT)...", shiftK));
            PreFlightVolumeState st = RunPreFlightScan(io, partStart, oldLengthBytes, 2L, evacMax, false);
            ExFatBootGeometry boot = st.Boot;

            if (shiftK >= boot.ClusterCount && newClusterCount + shiftK < boot.ClusterCount + 1u)
                throw new InvalidOperationException("Target size cannot accommodate expanded FAT.");

            ulong newBmpBytes = (newClusterCount + 7UL) / 8UL;
            int needBmpClusters = (int)Math.Max(1UL, (newBmpBytes + (ulong)boot.ClusterSize - 1UL) / (ulong)boot.ClusterSize);

            // Partition bitmap's existing clusters into those staying >= 2 + K vs those inside [2 .. 2 + K - 1] (replaced, data rebuilt fresh)
            var keptBmpOld = new List<uint>();
            for (int i = 0; i < st.BitmapObj.Clusters.Count; i++)
            {
                uint c = st.BitmapObj.Clusters[i];
                if (c >= 2u + shiftK && keptBmpOld.Count < needBmpClusters)
                    keptBmpOld.Add(c);
            }
            int needNewBmpSlots = Math.Max(0, needBmpClusters - keptBmpOld.Count);

            // Identify non-bitmap objects touching [2 .. 2 + K - 1]
            var objectsToEvac = new List<ExFatObjectInfo>();
            int dataEvacCount = 0;
            if (IntersectsRange(st.UpCaseObj.Clusters, 2L, evacMax))
            {
                objectsToEvac.Add(st.UpCaseObj);
                dataEvacCount += CountFrontClusters(st.UpCaseObj.Clusters, evacMax);
            }
            for (int i = 0; i < st.AllDirs.Count; i++)
            {
                var d = st.AllDirs[i].ObjInfo;
                if (IntersectsRange(d.Clusters, 2L, evacMax))
                {
                    objectsToEvac.Add(d);
                    dataEvacCount += CountFrontClusters(d.Clusters, evacMax);
                }
            }
            for (int i = 0; i < st.TrackedFiles.Count; i++)
            {
                var f = st.TrackedFiles[i];
                if (IntersectsRange(f.Clusters, 2L, evacMax))
                {
                    objectsToEvac.Add(f);
                    dataEvacCount += CountFrontClusters(f.Clusters, evacMax);
                }
            }

            // Old coordinate upper bound corresponding to final new cluster (newClusterCount + 1): oldMax = newClusterCount + K + 1
            uint oldCoordMax = newClusterCount + shiftK + 1u;
            int totalFreeNeeded = dataEvacCount + needNewBmpSlots;
            List<uint> freePoolOld = CollectFreeClusters(st, 2u + shiftK, oldCoordMax, totalFreeNeeded, boot.ClusterCount + 2u);
            if (freePoolOld.Count < totalFreeNeeded)
                throw new InvalidOperationException(string.Format(
                    "Not enough free space to expand exFAT FAT (needed {0} cluster(s) >= {1}, found {2}).",
                    totalFreeNeeded, 2u + shiftK, freePoolOld.Count));

            // Prepare combined old-coordinate allocation tracking bitmap (size covers up to max(oldClusterCount, newClusterCount + K))
            uint maxBmpBitsOld = Math.Max(boot.ClusterCount, newClusterCount + shiftK);
            byte[] combinedOwnedOld = new byte[(int)((maxBmpBitsOld + 7u) / 8u)];
            Array.Copy(st.OwnedBitmap, 0, combinedOwnedOld, 0, Math.Min(st.OwnedBitmap.Length, combinedOwnedOld.Length));

            // Free any dropped Bitmap clusters outside keptBmpOld
            for (int i = 0; i < st.BitmapObj.Clusters.Count; i++)
            {
                uint c = st.BitmapObj.Clusters[i];
                if (!keptBmpOld.Contains(c))
                    SetBit(combinedOwnedOld, c, false);
            }

            // Assign destinations from freePoolOld
            int poolCursor = 0;
            for (int i = 0; i < needNewBmpSlots; i++)
            {
                uint destOld = freePoolOld[poolCursor++];
                keptBmpOld.Add(destOld);
                SetBit(combinedOwnedOld, destOld, true);
            }
            // Also make sure keptBmpOld stay marked allocated
            for (int i = 0; i < keptBmpOld.Count; i++)
                SetBit(combinedOwnedOld, keptBmpOld[i], true);

            var copySrcOld = new List<uint>(dataEvacCount);
            var copyDstOld = new List<uint>(dataEvacCount);

            for (int oi = 0; oi < objectsToEvac.Count; oi++)
            {
                ExFatObjectInfo obj = objectsToEvac[oi];
                bool wasContig = obj.NoFatChain;
                for (int ci = 0; ci < obj.Clusters.Count; ci++)
                {
                    uint oldC = obj.Clusters[ci];
                    if (oldC > evacMax) continue;

                    uint dstOld = freePoolOld[poolCursor++];
                    copySrcOld.Add(oldC);
                    copyDstOld.Add(dstOld);
                    SetBit(combinedOwnedOld, oldC, false);
                    SetBit(combinedOwnedOld, dstOld, true);
                    obj.Clusters[ci] = dstOld;
                }

                if (wasContig && !IsContiguous(obj.Clusters))
                {
                    obj.NoFatChain = false;
                    obj.DirtyFat = true;
                }
                else
                {
                    obj.DirtyFat = !obj.NoFatChain;
                }
            }

            // Copy evacuated cluster data FIRST while FAT/dirs/boot are still valid at old coordinates
            if (copySrcOld.Count > 0)
                CopyClusterList(io, boot, copySrcOld, copyDstOld, progressCallback, 68, 78, "Relocating front clusters for FAT");

            if (progressCallback != null) progressCallback(80, "Streaming FAT shift & rewriting directory cluster references...");

            // Shift Bitmap cluster list -> new coordinates and build shifted FAT overrides
            var newFatOverrides = new Dictionary<uint, uint>();
            var newBitmapClusters = new List<uint>(keptBmpOld.Count);
            for (int i = 0; i < keptBmpOld.Count; i++)
                newBitmapClusters.Add(keptBmpOld[i] - shiftK);
            for (int i = 0; i < newBitmapClusters.Count; i++)
                newFatOverrides[newBitmapClusters[i]] = (i + 1 < newBitmapClusters.Count) ? newBitmapClusters[i + 1] : FAT_EOC;

            st.BitmapObj.Clusters = newBitmapClusters;
            st.BitmapObj.FirstCluster = newBitmapClusters[0];
            st.BitmapObj.DataLength = newBmpBytes;

            // Shift UpCase & Root & all evacuated objects -> build shifted FAT overrides & Dir child overrides
            if (!objectsToEvac.Contains(st.UpCaseObj))
            {
                // UpCase was not evacuated, its clusters just shift by -K; FAT stream handles non-evacuated FAT chains automatically
                st.UpCaseObj.FirstCluster -= shiftK;
                for (int i = 0; i < st.UpCaseObj.Clusters.Count; i++)
                    st.UpCaseObj.Clusters[i] -= shiftK;
            }

            if (!objectsToEvac.Contains(st.RootDir.ObjInfo))
            {
                st.RootDir.ObjInfo.FirstCluster -= shiftK;
                for (int i = 0; i < st.RootDir.ObjInfo.Clusters.Count; i++)
                    st.RootDir.ObjInfo.Clusters[i] -= shiftK;
            }

            for (int oi = 0; oi < objectsToEvac.Count; oi++)
            {
                ExFatObjectInfo obj = objectsToEvac[oi];
                for (int i = 0; i < obj.Clusters.Count; i++)
                    obj.Clusters[i] -= shiftK;
                obj.FirstCluster = obj.Clusters[0];

                if (obj.DirtyFat)
                {
                    for (int i = 0; i < obj.Clusters.Count; i++)
                        newFatOverrides[obj.Clusters[i]] = (i + 1 < obj.Clusters.Count) ? obj.Clusters[i + 1] : FAT_EOC;
                }

                if (obj.DirIndex >= 0)
                {
                    ExFatDirNode pDir = st.AllDirs[obj.DirIndex];
                    RecordDirOverride(pDir, obj.ParentEntryOffset, obj.FirstCluster, !obj.NoFatChain);
                }
            }

            // Stream-shift FAT in place (readOffset is strictly K*4 bytes ahead of writeOffset)
            uint oldValidUpper = Math.Min(boot.ClusterCount + 1u, (uint)(((long)boot.FatLengthSectors * boot.SectorSize) / 4L - 1L));
            StreamShiftFatForGrow(io, boot, shiftK, oldValidUpper, newFatLen, st.DiskBitmap, newFatOverrides);

            // Construct a view geometry using newHeapOffset for writing Directories and Bitmap at final shifted cluster IDs!
            // Note: For every cluster >= 2 + K (now c_new = c_old - K), newHeap + (c_new - 2)*spc == oldHeap + (c_old - 2)*spc (exact same bytes!).
            var shiftedViewBoot = new ExFatBootGeometry
            {
                PartStartOffset = boot.PartStartOffset,
                MainBootRegion = boot.MainBootRegion,
                PartitionOffsetSectors = boot.PartitionOffsetSectors,
                VolumeLengthSectors = newTotalSectors,
                FatOffsetSectors = boot.FatOffsetSectors,
                FatLengthSectors = newFatLen,
                ClusterHeapOffsetSectors = newHeapOffset,
                ClusterCount = newClusterCount,
                RootFirstCluster = st.RootDir.ObjInfo.FirstCluster,
                VolumeSerial = boot.VolumeSerial,
                VolumeFlags = boot.VolumeFlags,
                BytesPerSectorShift = boot.BytesPerSectorShift,
                SectorsPerClusterShift = boot.SectorsPerClusterShift,
                SectorSize = boot.SectorSize,
                SectorsPerCluster = boot.SectorsPerCluster,
                ClusterSize = boot.ClusterSize
            };

            // Shift non-evacuated subdirectories' Clusters arrays in memory so PatchAndWriteDirectory addresses shiftedViewBoot cleanly
            for (int i = 0; i < st.AllDirs.Count; i++)
            {
                ExFatDirNode d = st.AllDirs[i];
                if (i == 0) continue; // Root already shifted above
                if (!objectsToEvac.Contains(d.ObjInfo))
                {
                    d.ObjInfo.FirstCluster -= shiftK;
                    for (int ci = 0; ci < d.ObjInfo.Clusters.Count; ci++)
                        d.ObjInfo.Clusters[ci] -= shiftK;
                }
            }

            // Update Root + every subdirectory with non-zero children or overrides
            for (int i = 0; i < st.AllDirs.Count; i++)
            {
                ExFatDirNode d = st.AllDirs[i];
                if (i == 0 || d.HasDirtyChildren || d.HasAnyNonZeroChild)
                    PatchAndWriteDirectory(io, shiftedViewBoot, d, st.BitmapObj.FirstCluster, newBmpBytes, st.UpCaseObj.FirstCluster, shiftK);
            }

            // Build new shifted Allocation Bitmap: bit(c_new - 2) = bit(c_new + K - 2) from combinedOwnedOld
            byte[] finalBmpRaw = new byte[newBitmapClusters.Count * boot.ClusterSize];
            for (uint cNew = 2u; cNew <= newClusterCount + 1u; cNew++)
            {
                uint cOld = cNew + shiftK;
                if (GetBit(combinedOwnedOld, cOld))
                    SetBit(finalBmpRaw, cNew, true);
            }
            WriteClusterChainBytes(io, shiftedViewBoot, st.BitmapObj.Clusters, finalBmpRaw);

            long allocCount = CountAllocatedClusters(finalBmpRaw, newClusterCount);
            byte percentInUse = (byte)Math.Min(100UL, ((ulong)allocCount * 100UL) / newClusterCount);
            long usedBytes = allocCount * (long)boot.ClusterSize;

            WriteMainAndBackupBoot(io, partStart, boot.MainBootRegion, (ulong)(partStart / boot.SectorSize),
                newTotalSectors, newFatLen, newHeapOffset, newClusterCount,
                st.RootDir.ObjInfo.FirstCluster, percentInUse, isVirtualImage, usedBytes);
        }

        private static int CountFrontClusters(List<uint> clusters, uint evacMax)
        {
            int count = 0;
            for (int i = 0; i < clusters.Count; i++)
                if (clusters[i] <= evacMax) count++;
            return count;
        }

        private static void StreamShiftFatForGrow(
            IRawDiskIo io,
            ExFatBootGeometry oldBoot,
            uint shiftK,
            uint oldValidUpper,
            uint newFatLen,
            byte[] oldDiskBitmap,
            Dictionary<uint, uint> newFatOverrides)
        {
            long fatBase = oldBoot.FatStartByte;
            int secSize = oldBoot.SectorSize;
            const int chunkEntries = FAT_PAGE_ENTRIES; // 65536 uints = 256 KiB

            uint totalShiftedCount = oldValidUpper >= 2u + shiftK ? (oldValidUpper - (2u + shiftK) + 1u) : 0u;
            uint processed = 0u;

            var appliedOverrides = new HashSet<uint>();
            while (processed < totalShiftedCount)
            {
                uint batchEntries = (processed == 0u)
                    ? Math.Min((uint)(chunkEntries - 2), totalShiftedCount)
                    : Math.Min((uint)chunkEntries, totalShiftedCount - processed);
                uint readFirstCluster = 2u + shiftK + processed;
                byte[] srcRaw = io.ReadAt(fatBase + (long)readFirstCluster * 4L, (int)(batchEntries * 4u));

                // Careful if batch starts at c_new = 2 (+8 bytes inside sector 0): only prepend 8 bytes on first batch!
                uint writeFirstCluster = 2u + processed;
                if (processed == 0u)
                {
                    int totalOutBytes = (int)(8u + batchEntries * 4u);
                    int alignedBytes = ((totalOutBytes + secSize - 1) / secSize) * secSize;
                    byte[] out0 = new byte[alignedBytes];
                    BitConverter.GetBytes(FAT_MEDIA).CopyTo(out0, 0);
                    BitConverter.GetBytes(FAT_EOC).CopyTo(out0, 4);

                    for (uint idx = 0; idx < batchEntries; idx++)
                    {
                        uint cOld = readFirstCluster + idx;
                        uint cNew = writeFirstCluster + idx;
                        uint val;
                        if (newFatOverrides.TryGetValue(cNew, out val))
                        {
                            appliedOverrides.Add(cNew);
                        }
                        else
                        {
                            uint rawVal = idx * 4u + 4u <= (uint)srcRaw.Length ? BitConverter.ToUInt32(srcRaw, (int)(idx * 4u)) : 0u;
                            val = RemapOldFatEntry(rawVal, shiftK, oldValidUpper, oldDiskBitmap, cOld);
                        }
                        BitConverter.GetBytes(val).CopyTo(out0, 8 + (int)(idx * 4u));
                    }

                    // Keep zero tail past processed + 2 in sector-aligned write
                    io.WriteAt(fatBase, out0);
                }
                else
                {
                    byte[] outN = new byte[batchEntries * 4u];
                    for (uint idx = 0; idx < batchEntries; idx++)
                    {
                        uint cOld = readFirstCluster + idx;
                        uint cNew = writeFirstCluster + idx;
                        uint val;
                        if (newFatOverrides.TryGetValue(cNew, out val))
                        {
                            appliedOverrides.Add(cNew);
                        }
                        else
                        {
                            uint rawVal = idx * 4u + 4u <= (uint)srcRaw.Length ? BitConverter.ToUInt32(srcRaw, (int)(idx * 4u)) : 0u;
                            val = RemapOldFatEntry(rawVal, shiftK, oldValidUpper, oldDiskBitmap, cOld);
                        }
                        BitConverter.GetBytes(val).CopyTo(outN, (int)(idx * 4u));
                    }
                    // Align write offset down to sector boundary by reading sector head once
                    long writeStartByte = fatBase + (long)writeFirstCluster * 4L;
                    long alignedByte = (writeStartByte / secSize) * secSize;
                    int headKeep = (int)(writeStartByte - alignedByte);
                    int fullWriteLen = ((headKeep + outN.Length + secSize - 1) / secSize) * secSize;
                    byte[] secAlignedBuf = new byte[fullWriteLen];
                    if (headKeep > 0)
                    {
                        byte[] head = io.ReadAt(alignedByte, headKeep);
                        Array.Copy(head, 0, secAlignedBuf, 0, headKeep);
                    }
                    Array.Copy(outN, 0, secAlignedBuf, headKeep, outN.Length);
                    io.WriteAt(alignedByte, secAlignedBuf);
                }

                processed += batchEntries;
            }

            if (totalShiftedCount == 0u)
            {
                byte[] headSec = new byte[secSize];
                BitConverter.GetBytes(FAT_MEDIA).CopyTo(headSec, 0);
                BitConverter.GetBytes(FAT_EOC).CopyTo(headSec, 4);
                io.WriteAt(fatBase, headSec);
            }

            // Zero remaining FAT bytes from (2 + totalShiftedCount) entries to fatLen * secSize
            long newFatTotalBytes = (long)newFatLen * secSize;
            long tailStartByte = fatBase + (long)(2u + totalShiftedCount) * 4L;
            long fatEndByte = fatBase + newFatTotalBytes;
            if (tailStartByte < fatEndByte)
            {
                long alignedTailStart = ((tailStartByte + secSize - 1L) / secSize) * secSize;
                if (alignedTailStart > tailStartByte)
                {
                    long partialSecStart = alignedTailStart - secSize;
                    int keepBytes = (int)(tailStartByte - partialSecStart);
                    byte[] partial = new byte[secSize];
                    byte[] keep = io.ReadAt(partialSecStart, keepBytes);
                    Array.Copy(keep, 0, partial, 0, keepBytes);
                    io.WriteAt(partialSecStart, partial);
                }
                if (alignedTailStart < fatEndByte)
                    WriteZeroChunks(io, alignedTailStart, fatEndByte - alignedTailStart);
            }

            // Write remaining newFatOverrides (>= 2 + totalShiftedCount, or anything unapplied) via PagedFatTable
            var remaining = new List<uint>();
            foreach (uint k in newFatOverrides.Keys)
            {
                if (!appliedOverrides.Contains(k))
                    remaining.Add(k);
            }
            if (remaining.Count > 0)
            {
                var paged = new PagedFatTable(io, fatBase, newFatLen, secSize);
                for (int i = 0; i < remaining.Count; i++)
                    paged.SetEntry(remaining[i], newFatOverrides[remaining[i]]);
                paged.Flush();
            }
        }

        private static uint RemapOldFatEntry(uint rawVal, uint shiftK, uint oldValidUpper, byte[] oldDiskBitmap, uint cOld)
        {
            if (rawVal == FAT_BAD_CLUSTER)
                return FAT_BAD_CLUSTER;
            if (!GetBit(oldDiskBitmap, cOld))
                return 0u;
            if (rawVal >= FAT_MEDIA)
                return rawVal;
            if (rawVal >= 2u + shiftK && rawVal <= oldValidUpper)
                return rawVal - shiftK;
            return 0u;
        }

        private static void WriteZeroChunks(IRawDiskIo io, long startOffset, long length)
        {
            const int maxChunk = 1048576;
            byte[] zero = new byte[(int)Math.Min(maxChunk, length)];
            long written = 0;
            while (written < length)
            {
                int n = (int)Math.Min(maxChunk, length - written);
                if (n != zero.Length) zero = new byte[n];
                io.WriteAt(startOffset + written, zero);
                written += n;
            }
        }

        /// <summary>
        /// Executes an offline, non-destructive exFAT resize and/or overlap-safe move.
        /// Validates the entire directory tree and allocation bitmap before modifying any sectors.
        /// </summary>
        public static void ResizeOrMoveExFat(
            IRawDiskIo io,
            PartitionEntry part,
            long oldStartOffset,
            long oldLengthBytes,
            long newStartOffset,
            long newLengthBytes,
            bool isVirtualImage,
            Action commitPartitionTable,
            Action<int, string> progressCallback)
        {
            if (io == null) throw new ArgumentNullException("io");
            if (part == null) throw new ArgumentNullException("part");
            if (oldLengthBytes <= 0 || newLengthBytes <= 0)
                throw new ArgumentException("Partition lengths must be positive.");

            // Phase 0: Pre-flight validation BEFORE any disk sector write
            if (progressCallback != null) progressCallback(5, "Pre-flight validating exFAT boot region & filesystem tree...");
            PreFlightVolumeState initState = RunPreFlightScan(io, oldStartOffset, oldLengthBytes, long.MaxValue, 0L, false);
            ExFatBootGeometry initBoot = initState.Boot;

            long currentStart = oldStartOffset;
            long currentLength = oldLengthBytes;

            // Phase 1: Shrink FIRST (at oldStartOffset) so shrunk volume fits cleanly into smaller new slot before Move
            if (newLengthBytes < oldLengthBytes)
            {
                ShrinkExFatInPlace(io, currentStart, oldLengthBytes, newLengthBytes, isVirtualImage, progressCallback);
                currentLength = newLengthBytes;
                io.Flush();
            }

            // Phase 2: Move (if StartOffset changed) and update PartitionOffset (+0x40) in Main + Backup Boot Regions
            if (newStartOffset != oldStartOffset)
            {
                long moveBytes = Math.Min(currentLength, newLengthBytes);
                MovePartitionRaw(io, currentStart, newStartOffset, moveBytes, progressCallback, 45, 75);
                currentStart = newStartOffset;

                ExFatBootGeometry movedBoot = ReadAndValidateBoot(io, currentStart, moveBytes);
                ulong newPartOffSectors = (ulong)(newStartOffset / movedBoot.SectorSize);
                if (movedBoot.PartitionOffsetSectors != newPartOffSectors)
                {
                    BitConverter.GetBytes(newPartOffSectors).CopyTo(movedBoot.MainBootRegion, 0x40);
                    uint chk = RawDiskEngine.ComputeExFatChecksum(movedBoot.MainBootRegion, 11 * movedBoot.SectorSize, true);
                    byte[] chkBytes = BitConverter.GetBytes(chk);
                    int slots = movedBoot.SectorSize / 4;
                    int chkOff = 11 * movedBoot.SectorSize;
                    for (int i = 0; i < slots; i++)
                        Array.Copy(chkBytes, 0, movedBoot.MainBootRegion, chkOff + i * 4, 4);

                    io.WriteAt(currentStart, movedBoot.MainBootRegion);
                    io.WriteAt(currentStart + 12L * movedBoot.SectorSize, movedBoot.MainBootRegion);
                }
                io.Flush();
            }

            // Phase 3: Grow (at newStartOffset) after partition bytes are in their final destination slot
            if (newLengthBytes > oldLengthBytes)
            {
                GrowExFatInPlace(io, currentStart, currentLength, newLengthBytes, isVirtualImage, progressCallback);
                currentLength = newLengthBytes;
                io.Flush();
            }

            // Phase 4: Commit updated MBR/GPT partition table inside the same locked/dismounted raw I/O session
            if (commitPartitionTable != null)
            {
                if (progressCallback != null) progressCallback(92, "Updating partition table entries...");
                commitPartitionTable();
            }
            io.Flush();

            // Refresh PartitionEntry stats from the finalized volume
            ExFatBootGeometry finalBoot = ReadAndValidateBoot(io, newStartOffset, newLengthBytes);
            PreFlightVolumeState finalState = RunPreFlightScan(io, newStartOffset, newLengthBytes, long.MaxValue, 0L, false);
            long finalAlloc = CountAllocatedClusters(finalState.OwnedBitmap, finalBoot.ClusterCount);
            part.UsedBytes = finalAlloc * (long)finalBoot.ClusterSize;
            part.FreeBytes = Math.Max(0L, newLengthBytes - part.UsedBytes.Value);

            if (progressCallback != null) progressCallback(96, "exFAT resize/move complete.");
        }

        /// <summary>
        /// Strict Microsoft-spec integrity verifier used by stress tests and post-resize diagnostics:
        /// checks Main == Backup Boot Regions, Boot Checksum, PartitionOffset/VolumeLength/FatLength/ClusterHeapOffset,
        /// bit-for-bit Allocation Bitmap consistency against the full directory tree walk, Up-Case checksum, and all EntrySetChecksums.
        /// </summary>
        public static void VerifyExFatVolumeIntegrity(IRawDiskIo io, long partStart, long partLenBytes)
        {
            PreFlightVolumeState st = RunPreFlightScan(io, partStart, partLenBytes, long.MaxValue, 0L, false);
            ExFatBootGeometry boot = st.Boot;

            byte[] backupBoot = io.ReadAt(partStart + 12L * boot.SectorSize, 12 * boot.SectorSize);
            if (backupBoot.Length != boot.MainBootRegion.Length)
                throw new InvalidDataException("exFAT Backup Boot Region truncated.");
            for (int i = 0; i < boot.MainBootRegion.Length; i++)
            {
                if (boot.MainBootRegion[i] != backupBoot[i])
                    throw new InvalidDataException("exFAT Backup Boot Region does not match Main Boot Region at byte 0x" + i.ToString("X"));
            }

            if (boot.PartitionOffsetSectors != (ulong)(partStart / boot.SectorSize))
                throw new InvalidDataException(string.Format("exFAT PartitionOffset mismatch: {0} != {1}",
                    boot.PartitionOffsetSectors, partStart / boot.SectorSize));

            // Check exact Allocation Bitmap bits across all clusters [2 .. ClusterCount + 1] + tail padding bits == 0
            for (uint c = 2u; c <= boot.ClusterCount + 1u; c++)
            {
                bool onDisk = GetBit(st.DiskBitmap, c);
                bool owned = GetBit(st.OwnedBitmap, c);
                if (onDisk != owned)
                    throw new InvalidDataException(string.Format(
                        "VerifyExFatVolumeIntegrity: bitmap mismatch at cluster {0} (disk={1}, owned={2}).", c, onDisk ? 1 : 0, owned ? 1 : 0));
            }
            int tailBits = (int)(boot.ClusterCount & 7u);
            if (tailBits > 0 && st.DiskBitmap.Length > 0)
            {
                byte lastByte = st.DiskBitmap[st.DiskBitmap.Length - 1];
                if ((lastByte & ~((1 << tailBits) - 1)) != 0)
                    throw new InvalidDataException("VerifyExFatVolumeIntegrity: Allocation Bitmap contains non-zero bits past ClusterCount.");
            }
        }
    }
}
