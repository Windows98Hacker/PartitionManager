using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using WinParted.Interop;
using WinParted.Models;

namespace WinParted.Core
{
    /// <summary>
    /// Coordinates non-destructive queued partition operations, simulated UI layout previews
    /// (<see cref="GetPreviewDisk"/>), overlap/bounds validation, and transactional execution against
    /// both virtual <c>.img</c> files and live physical drives (hybrid DiskPart + raw sector I/O).
    /// </summary>
    public class OperationQueueManager
    {
        private readonly List<DiskInfo> _baselineDisks = new List<DiskInfo>();
        private readonly List<PendingOperation> _queue = new List<PendingOperation>();

        public IReadOnlyList<DiskInfo> BaselineDisks { get { return _baselineDisks; } }
        public IReadOnlyList<PendingOperation> Queue { get { return _queue; } }
        public int PendingCount { get { return _queue.Count; } }

        /// <summary>
        /// Replaces the baseline snapshot of physical and virtual disks against which previews are simulated.
        /// </summary>
        public void SetBaselineDisks(IEnumerable<DiskInfo> physicalDisks, IEnumerable<DiskInfo> virtualDisks)
        {
            _baselineDisks.Clear();
            if (physicalDisks != null) _baselineDisks.AddRange(physicalDisks);
            if (virtualDisks != null) _baselineDisks.AddRange(virtualDisks);
        }

        public void ReplaceOrAddVirtualDisk(DiskInfo vdisk)
        {
            int idx = _baselineDisks.FindIndex(d => d.IsVirtualImage && string.Equals(d.BackingFilePath, vdisk.BackingFilePath, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
                _baselineDisks[idx] = vdisk;
            else
                _baselineDisks.Add(vdisk);
        }

        public DiskInfo GetBaselineDisk(int diskNumber)
        {
            return _baselineDisks.FirstOrDefault(d => d.DiskNumber == diskNumber);
        }

        public List<DiskInfo> GetAllPreviewDisks()
        {
            var result = new List<DiskInfo>();
            foreach (var d in _baselineDisks)
                result.Add(GetPreviewDisk(d.DiskNumber));
            return result;
        }

        /// <summary>
        /// Clones the baseline disk model and replays all pending operations for <paramref name="diskNumber"/>
        /// so the UI and ribbon reflect the post-queue layout without mutating disk bytes yet.
        /// </summary>
        public DiskInfo GetPreviewDisk(int diskNumber)
        {
            var baseline = GetBaselineDisk(diskNumber);
            if (baseline == null) return null;

            var preview = baseline.Clone();
            foreach (var op in _queue.Where(o => o.TargetDiskNumber == diskNumber))
                ApplyOperationToModel(preview, op);

            RenumberPartitions(preview);
            return preview;
        }

        /// <summary>
        /// Validates an operation (bounds, MiB alignment, collisions against simulated state, active OS boot
        /// protection, and generated DiskPart script) before appending it to the pending queue.
        /// </summary>
        public void Enqueue(PendingOperation op)
        {
            if (op == null) throw new ArgumentNullException("op");
            var baseline = GetBaselineDisk(op.TargetDiskNumber);
            if (baseline == null)
                throw new InvalidOperationException("Target disk #" + op.TargetDiskNumber + " is not mounted.");
            if (string.IsNullOrEmpty(op.TargetDiskPath))
                op.TargetDiskPath = baseline.DevicePath;

            // Validate against the simulated pre-operation layout first (for active Windows OS boot protection)
            var simulated = GetPreviewDisk(op.TargetDiskNumber);
            var targetPart = simulated.Partitions.FirstOrDefault(p => Math.Abs(p.StartingOffset - op.OriginalStartOffset) < 2L * 1048576L)
                             ?? simulated.Partitions.FirstOrDefault(p => p.PartitionNumber == op.TargetPartitionNumber);

            if (!baseline.IsVirtualImage && baseline.IsSystemDisk)
            {
                if (op.Type == OperationType.CreatePartitionTable || op.Type == OperationType.RestorePartitionTable)
                    throw new InvalidOperationException("Safety Guardrail: Cannot erase or replace the partition table on the active Windows OS boot disk.");

                if (targetPart != null && targetPart.IsSystemOrBoot &&
                    (op.Type == OperationType.DeletePartition ||
                     op.Type == OperationType.FormatPartition ||
                     op.Type == OperationType.WipePartition ||
                     op.Type == OperationType.ResizeMovePartition))
                {
                    throw new InvalidOperationException("Safety Guardrail: Cannot delete, format, wipe, or resize an active Windows OS/boot partition while Windows is running on it.");
                }
            }

            // Validate the resulting layout on a simulated preview before accepting into queue
            ApplyOperationToModel(simulated, op);
            RenumberPartitions(simulated);

            if (op.Type != OperationType.RestorePartitionTable)
            {
                string err;
                if (!RawDiskEngine.ValidatePartitionTable(simulated, out err))
                    throw new InvalidOperationException("Unsafe partition operation rejected: " + err);
            }

            if (string.IsNullOrEmpty(op.DiskPartScript))
                op.DiskPartScript = BuildDiskPartScript(op, simulated.PartitionStyle);
            _queue.Add(op);
        }

        public bool UndoLast()
        {
            if (_queue.Count == 0) return false;
            _queue.RemoveAt(_queue.Count - 1);
            return true;
        }

        public bool UndoLastForDisk(int diskNumber)
        {
            for (int i = _queue.Count - 1; i >= 0; i--)
            {
                if (_queue[i].TargetDiskNumber == diskNumber)
                {
                    _queue.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        public void ClearForDisk(int diskNumber)
        {
            _queue.RemoveAll(o => o.TargetDiskNumber == diskNumber);
        }

        public void ClearAll()
        {
            _queue.Clear();
        }

        public string ExportFullDiskPartScript()
        {
            var sb = new StringBuilder();
            sb.AppendLine("REM WinParted Generated DiskPart Script");
            foreach (var op in _queue)
            {
                sb.AppendLine("REM Operation: " + op.Summary);
                sb.AppendLine(op.DiskPartScript);
                sb.AppendLine();
            }
            return sb.ToString();
        }

        /// <summary>
        /// Pure in-memory simulation of a single <see cref="PendingOperation"/> on a cloned <see cref="DiskInfo"/>.
        /// </summary>
        public static void ApplyOperationToModel(DiskInfo disk, PendingOperation op)
        {
            switch (op.Type)
            {
                case OperationType.CreatePartitionTable:
                    ReadOnlyFileSystemMountEngine.UnmountDisk(disk.DiskNumber);
                    disk.PartitionStyle = op.NewTableStyle;
                    disk.Partitions.Clear();
                    if (op.NewTableStyle == PartitionTableStyle.GPT)
                    {
                        disk.GptDiskGuid = Guid.NewGuid();
                        disk.MaxPartitions = 128;
                    }
                    else if (op.NewTableStyle == PartitionTableStyle.MBR)
                    {
                        disk.MbrSignature = (uint)new Random().Next(1, int.MaxValue);
                        disk.MaxPartitions = 4;
                    }
                    break;

                case OperationType.CreatePartition:
                    var created = new PartitionEntry
                    {
                        DiskNumber = disk.DiskNumber,
                        PartitionNumber = disk.Partitions.Count + 1,
                        StartingOffset = op.StartOffset,
                        PartitionLength = op.Length,
                        LogicalSectorSize = disk.LogicalSectorSize,
                        PhysicalSectorSize = disk.PhysicalSectorSize,
                        FileSystem = op.NewFileSystem,
                        Label = op.NewLabel ?? string.Empty,
                        PartitionName = string.IsNullOrEmpty(op.NewPartitionName) ? (op.NewLabel ?? "Data") : op.NewPartitionName,
                        GptTypeGuid = op.NewGptTypeGuid != Guid.Empty ? op.NewGptTypeGuid : GptPartitionTypeGuids.ForFileSystem(op.NewFileSystem),
                        GptPartitionGuid = Guid.NewGuid(),
                        GptAttributes = op.NewGptAttributes,
                        MbrPartitionType = op.NewMbrType != 0 ? op.NewMbrType : RawDiskEngine.GetDefaultMbrType(op.NewFileSystem),
                        MbrBootable = op.NewMbrBoot,
                        UsedBytes = 0,
                        FreeBytes = op.Length
                    };
                    created.FlagsSummary = Win32DiskInterop.FormatGptFlags(created.GptTypeGuid, created.GptAttributes);
                    disk.Partitions.Add(created);
                    break;

                case OperationType.DeletePartition:
                    ReadOnlyFileSystemMountEngine.Unmount(disk.DiskNumber, op.OriginalStartOffset);
                    disk.Partitions.RemoveAll(p => p.StartingOffset == op.OriginalStartOffset && p.PartitionLength == op.OriginalLength);
                    break;

                case OperationType.ResizeMovePartition:
                    ReadOnlyFileSystemMountEngine.Unmount(disk.DiskNumber, op.OriginalStartOffset);
                    ReadOnlyFileSystemMountEngine.Unmount(disk.DiskNumber, op.StartOffset);
                    var targetResize = disk.Partitions.FirstOrDefault(p => p.StartingOffset == op.OriginalStartOffset);
                    if (targetResize == null)
                        throw new InvalidOperationException("Target partition at 0x" + op.OriginalStartOffset.ToString("X") + " was not found for Resize/Move.");
                    if (targetResize.UsedBytes.HasValue && op.Length < targetResize.UsedBytes.Value)
                        throw new InvalidOperationException(string.Format("Cannot shrink partition ({0}) below its used data ({1}).",
                            ByteFormatter.FormatBytes(op.Length), ByteFormatter.FormatBytes(targetResize.UsedBytes.Value)));

                    targetResize.StartingOffset = op.StartOffset;
                    targetResize.PartitionLength = op.Length;
                    if (targetResize.UsedBytes.HasValue)
                        targetResize.FreeBytes = Math.Max(0, op.Length - targetResize.UsedBytes.Value);
                    break;

                case OperationType.FormatPartition:
                    ReadOnlyFileSystemMountEngine.Unmount(disk.DiskNumber, op.OriginalStartOffset);
                    var targetFmt = disk.Partitions.FirstOrDefault(p => p.StartingOffset == op.OriginalStartOffset);
                    if (targetFmt != null)
                    {
                        targetFmt.FileSystem = op.NewFileSystem;
                        targetFmt.Label = op.NewLabel ?? string.Empty;
                        if (op.NewGptTypeGuid != Guid.Empty)
                            targetFmt.GptTypeGuid = op.NewGptTypeGuid;
                        else
                            targetFmt.GptTypeGuid = GptPartitionTypeGuids.ForFileSystem(op.NewFileSystem);
                        targetFmt.FlagsSummary = Win32DiskInterop.FormatGptFlags(targetFmt.GptTypeGuid, targetFmt.GptAttributes);
                        targetFmt.MbrPartitionType = op.NewMbrType != 0 ? op.NewMbrType : RawDiskEngine.GetDefaultMbrType(op.NewFileSystem);
                        targetFmt.UsedBytes = 0;
                        targetFmt.FreeBytes = targetFmt.PartitionLength;
                    }
                    break;

                case OperationType.ChangeLabel:
                    var targetLbl = disk.Partitions.FirstOrDefault(p => p.StartingOffset == op.OriginalStartOffset);
                    if (targetLbl != null)
                    {
                        targetLbl.Label = op.NewLabel ?? string.Empty;
                        if (!string.IsNullOrEmpty(op.NewPartitionName))
                            targetLbl.PartitionName = op.NewPartitionName;
                    }
                    break;

                case OperationType.ChangeFlags:
                    var targetFlag = disk.Partitions.FirstOrDefault(p => p.StartingOffset == op.OriginalStartOffset);
                    if (targetFlag != null)
                    {
                        if (disk.PartitionStyle == PartitionTableStyle.GPT)
                        {
                            targetFlag.GptTypeGuid = op.NewGptTypeGuid;
                            targetFlag.GptAttributes = op.NewGptAttributes;
                            targetFlag.FlagsSummary = Win32DiskInterop.FormatGptFlags(op.NewGptTypeGuid, op.NewGptAttributes);
                        }
                        else
                        {
                            if (op.NewMbrBoot)
                            {
                                foreach (var other in disk.Partitions)
                                {
                                    other.MbrBootable = false;
                                    other.FlagsSummary = string.Empty;
                                }
                            }
                            targetFlag.MbrPartitionType = op.NewMbrType;
                            targetFlag.MbrBootable = op.NewMbrBoot;
                            targetFlag.FlagsSummary = op.NewMbrBoot ? "boot" : "";
                        }
                    }
                    break;

                case OperationType.CopyPartition:
                    var cloned = new PartitionEntry
                    {
                        DiskNumber = disk.DiskNumber,
                        PartitionNumber = disk.Partitions.Count + 1,
                        StartingOffset = op.StartOffset,
                        PartitionLength = op.Length,
                        LogicalSectorSize = disk.LogicalSectorSize,
                        PhysicalSectorSize = disk.PhysicalSectorSize,
                        FileSystem = op.NewFileSystem,
                        Label = op.NewLabel ?? "Clone",
                        PartitionName = string.IsNullOrEmpty(op.NewPartitionName) ? "Cloned Partition" : op.NewPartitionName,
                        GptTypeGuid = op.NewGptTypeGuid != Guid.Empty ? op.NewGptTypeGuid : GptPartitionTypeGuids.ForFileSystem(op.NewFileSystem),
                        GptPartitionGuid = Guid.NewGuid(),
                        UsedBytes = 0,
                        FreeBytes = op.Length
                    };
                    disk.Partitions.Add(cloned);
                    break;

                case OperationType.WipePartition:
                    ReadOnlyFileSystemMountEngine.Unmount(disk.DiskNumber, op.OriginalStartOffset);
                    var targetWipe = disk.Partitions.FirstOrDefault(p => p.StartingOffset == op.OriginalStartOffset);
                    if (targetWipe != null)
                    {
                        targetWipe.FileSystem = FileSystemType.RAW;
                        targetWipe.Label = string.Empty;
                        targetWipe.PartitionName = string.Empty;
                        targetWipe.UUID = string.Empty;
                        targetWipe.GptTypeGuid = Guid.Empty;
                        targetWipe.MbrPartitionType = 0;
                        targetWipe.IsMounted = false;
                        targetWipe.UsedBytes = 0;
                        targetWipe.FreeBytes = targetWipe.PartitionLength;
                    }
                    break;
            }
        }

        private static void RenumberPartitions(DiskInfo disk)
        {
            var sorted = disk.Partitions.OrderBy(p => p.StartingOffset).ToList();
            for (int i = 0; i < sorted.Count; i++)
                sorted[i].PartitionNumber = i + 1;
            disk.Partitions = sorted;
        }

        private static void ZeroVacatedPartitionHeader(DiskInfo disk, long startOffset, long length)
        {
            if (disk == null || length <= 0) return;
            long minSafeStart = disk.PartitionStyle == PartitionTableStyle.GPT ? 0x4400L : 512L;
            long safeStart = Math.Max(startOffset, minSafeStart);
            long requestedEnd = Math.Min(startOffset + length, safeStart + 0x30000L);
            if (disk.TotalSizeBytes > 0)
            {
                long maxSafeEnd = disk.PartitionStyle == PartitionTableStyle.GPT
                    ? Math.Max(minSafeStart, disk.TotalSizeBytes - 0x4400L)
                    : disk.TotalSizeBytes;
                requestedEnd = Math.Min(requestedEnd, maxSafeEnd);
            }
            if (disk.Partitions != null)
            {
                for (int i = 0; i < disk.Partitions.Count; i++)
                {
                    var p = disk.Partitions[i];
                    if (p == null || p.IsUnallocated || p.PartitionLength <= 0) continue;
                    long pStart = p.StartingOffset;
                    long pEnd = p.StartingOffset + p.PartitionLength;
                    if (safeStart >= pStart && safeStart < pEnd)
                        return;
                    if (pStart > safeStart && pStart < requestedEnd)
                        requestedEnd = pStart;
                }
            }
            long clearLen = requestedEnd - safeStart;
            if (clearLen <= 0) return;
            RawDiskEngine.WriteBytes(disk, safeStart, new byte[(int)clearLen]);
        }

        /// <summary>
        /// Executes all queued operations sequentially across virtual and physical disks, updating
        /// baseline models as each operation succeeds.
        /// </summary>
        public List<string> ApplyAll(Action<int, string> progressCallback = null)
        {
            var log = new List<string>();
            if (_queue.Count == 0)
                return log;

            int total = _queue.Count;
            var executed = new List<PendingOperation>();

            for (int i = 0; i < total; i++)
            {
                var op = _queue[i];
                int pct = (int)((i * 100.0) / total);
                if (progressCallback != null) progressCallback(pct, "Executing: " + op.Summary);

                var disk = GetBaselineDisk(op.TargetDiskNumber);
                if (disk == null)
                    throw new InvalidOperationException("Target disk #" + op.TargetDiskNumber + " not found.");

                if (disk.IsVirtualImage)
                {
                    ExecuteVirtualDiskOperation(disk, op);
                    log.Add("[OK] Virtual Disk: " + op.Summary);
                }
                else
                {
                    ExecutePhysicalDiskOperation(disk, op);
                    log.Add("[OK] Physical Disk: " + op.Summary);
                    var freshPhysical = Win32DiskInterop.EnumeratePhysicalDisks();
                    var virtuals = _baselineDisks.Where(d => d.IsVirtualImage).ToList();
                    SetBaselineDisks(freshPhysical, virtuals);
                }

                executed.Add(op);
            }

            foreach (var done in executed)
                _queue.Remove(done);

            if (progressCallback != null) progressCallback(100, "All operations completed and verified.");
            return log;
        }

        /// <summary>
        /// Executes an operation on a virtual <c>.img</c> file with primary/backup GPT+MBR snapshot rollback,
        /// raw payload preservation during Move/Resize/Clone, and post-write CRC32/MBR integrity verification.
        /// </summary>
        private void ExecuteVirtualDiskOperation(DiskInfo disk, PendingOperation op)
        {
            int sec = disk.LogicalSectorSize > 0 ? disk.LogicalSectorSize : 512;
            byte[] primarySnapshot = RawDiskEngine.ReadBytes(disk, 0, 34 * sec);
            byte[] backupSnapshot = (disk.TotalSizeBytes > 34L * sec)
                ? RawDiskEngine.ReadBytes(disk, disk.TotalSizeBytes - 33L * sec, 33 * sec)
                : new byte[0];

            try
            {
                switch (op.Type)
                {
                    case OperationType.CreatePartitionTable:
                        ReadOnlyFileSystemMountEngine.PurgeDiskOverlaysAndCache(disk.DiskNumber);
                        ApplyOperationToModel(disk, op);
                        RawDiskEngine.WritePartitionTable(disk);
                        break;

                    case OperationType.CreatePartition:
                        ApplyOperationToModel(disk, op);
                        RenumberPartitions(disk);
                        var created = disk.Partitions.First(p => p.StartingOffset == op.StartOffset);
                        RawDiskEngine.WritePartitionTable(disk);
                        ReadOnlyFileSystemMountEngine.PurgePartitionOverlaysAndCache(disk, created.StartingOffset, created.PartitionLength, true);
                        RawDiskEngine.WriteFileSystemHeader(disk, created);
                        break;

                    case OperationType.DeletePartition:
                        ReadOnlyFileSystemMountEngine.PurgePartitionOverlaysAndCache(disk, op.OriginalStartOffset, op.OriginalLength, true);
                        ApplyOperationToModel(disk, op);
                        RenumberPartitions(disk);
                        RawDiskEngine.WritePartitionTable(disk);
                        ZeroVacatedPartitionHeader(disk, op.OriginalStartOffset, op.OriginalLength);
                        break;

                    case OperationType.ResizeMovePartition:
                        if (op.StartOffset != op.OriginalStartOffset)
                        {
                            long bytesToMove = Math.Min(op.OriginalLength, op.Length);
                            RawDiskEngine.MovePartitionBytes(disk, op.OriginalStartOffset, op.StartOffset, bytesToMove);
                            ReadOnlyFileSystemMountEngine.PurgePartitionOverlaysAndCache(disk, op.OriginalStartOffset, op.OriginalLength, false);
                        }
                        ReadOnlyFileSystemMountEngine.Unmount(disk.DiskNumber, op.OriginalStartOffset);
                        ReadOnlyFileSystemMountEngine.Unmount(disk.DiskNumber, op.StartOffset);
                        ApplyOperationToModel(disk, op);
                        RenumberPartitions(disk);
                        var resizedPart = disk.Partitions.First(p => p.StartingOffset == op.StartOffset);
                        RawDiskEngine.WritePartitionTable(disk);
                        RawDiskEngine.ResizeFileSystemInPlace(disk, resizedPart, op.OriginalLength);
                        break;

                    case OperationType.FormatPartition:
                        ReadOnlyFileSystemMountEngine.PurgePartitionOverlaysAndCache(disk, op.OriginalStartOffset, op.OriginalLength, true);
                        ApplyOperationToModel(disk, op);
                        var fmtPart = disk.Partitions.First(p => p.StartingOffset == op.OriginalStartOffset);
                        RawDiskEngine.WritePartitionTable(disk);
                        RawDiskEngine.WriteFileSystemHeader(disk, fmtPart);
                        break;

                    case OperationType.ChangeLabel:
                        ApplyOperationToModel(disk, op);
                        var lblPart = disk.Partitions.First(p => p.StartingOffset == op.OriginalStartOffset);
                        RawDiskEngine.WritePartitionTable(disk);
                        RawDiskEngine.UpdateFileSystemLabelInPlace(disk, lblPart);
                        break;

                    case OperationType.ChangeFlags:
                        ApplyOperationToModel(disk, op);
                        RawDiskEngine.WritePartitionTable(disk);
                        break;

                    case OperationType.CopyPartition:
                        DiskInfo srcDisk = null;
                        if (!string.IsNullOrEmpty(op.SourceDiskPath))
                            srcDisk = _baselineDisks.FirstOrDefault(d => string.Equals(d.DevicePath, op.SourceDiskPath, StringComparison.OrdinalIgnoreCase));
                        if (srcDisk == null)
                            srcDisk = GetBaselineDisk(op.SourceDiskNumber) ?? disk;

                        ReadOnlyFileSystemMountEngine.PurgePartitionOverlaysAndCache(disk, op.StartOffset, op.Length, false);
                        RawDiskEngine.CopyPartitionBytes(srcDisk, op.SourceStartOffset, disk, op.StartOffset, op.SourceLength);
                        ApplyOperationToModel(disk, op);
                        RenumberPartitions(disk);
                        var clonedPart = disk.Partitions.First(p => p.StartingOffset == op.StartOffset);
                        RawDiskEngine.WritePartitionTable(disk);
                        if (op.Length > op.SourceLength)
                            RawDiskEngine.ResizeFileSystemInPlace(disk, clonedPart, op.SourceLength);
                        break;

                    case OperationType.WipePartition:
                        RawDiskEngine.WipeRange(disk, op.OriginalStartOffset, op.OriginalLength, op.WipePattern);
                        ReadOnlyFileSystemMountEngine.PurgePartitionOverlaysAndCache(disk, op.OriginalStartOffset, op.OriginalLength, false);
                        ApplyOperationToModel(disk, op);
                        RawDiskEngine.WritePartitionTable(disk);
                        break;

                    case OperationType.RestorePartitionTable:
                        ReadOnlyFileSystemMountEngine.PurgeDiskOverlaysAndCache(disk.DiskNumber);
                        RawDiskEngine.RestorePartitionTable(disk, op.BackupFilePath);
                        break;
                }

                // Reload from disk to verify CRC32, Primary+Backup GPT sync, and actual persisted bytes
                var reloaded = RawDiskEngine.LoadVirtualDisk(disk.BackingFilePath, disk.DiskNumber);
                string integrityReport;
                if (!RawDiskEngine.VerifyDiskPartitionTableIntegrity(reloaded, out integrityReport))
                    throw new InvalidDataException("Post-operation partition table integrity check failed: " + integrityReport);

                ReplaceOrAddVirtualDisk(reloaded);
            }
            catch
            {
                // Rollback partition table headers on failure
                try
                {
                    if (primarySnapshot.Length > 0)
                        RawDiskEngine.WriteBytes(disk, 0, primarySnapshot);
                    if (backupSnapshot.Length > 0 && disk.TotalSizeBytes >= backupSnapshot.Length)
                        RawDiskEngine.WriteBytes(disk, disk.TotalSizeBytes - backupSnapshot.Length, backupSnapshot);
                    var restored = RawDiskEngine.LoadVirtualDisk(disk.BackingFilePath, disk.DiskNumber);
                    ReplaceOrAddVirtualDisk(restored);
                }
                catch { }
                throw;
            }
        }

        /// <summary>
        /// Executes an operation on a live Windows physical disk (requires Administrator elevation):
        /// uses Win32 volume locking + raw sector writes for non-Microsoft filesystems and <c>diskpart.exe</c>
        /// for native Windows partitioning/formatting.
        /// </summary>
        private void ExecutePhysicalDiskOperation(DiskInfo disk, PendingOperation op)
        {
            if (!Win32DiskInterop.IsAdministrator())
                throw new UnauthorizedAccessException("Administrator privileges are required to modify live physical disks. Relaunch as Administrator or test safely using a Virtual Disk (.img).");

            var targetPart = disk.Partitions.FirstOrDefault(p => Math.Abs(p.StartingOffset - op.OriginalStartOffset) < 2L * 1048576L)
                             ?? disk.Partitions.FirstOrDefault(p => p.PartitionNumber == op.TargetPartitionNumber);
            if (targetPart != null)
                op.TargetPartitionNumber = targetPart.PartitionNumber;

            // Protect ONLY partitions belonging to the active running Windows OS boot disk.
            // Partitions on USB drives or secondary disks (including ESP, MSR, Recovery, active MBR boot) remain editable.
            if (disk.IsSystemDisk && (op.Type == OperationType.CreatePartitionTable || op.Type == OperationType.RestorePartitionTable))
                throw new InvalidOperationException("Safety Guardrail: Cannot erase or replace the partition table on the active Windows boot/system disk.");

            if (disk.IsSystemDisk && targetPart != null && targetPart.IsSystemOrBoot &&
                (op.Type == OperationType.DeletePartition || op.Type == OperationType.FormatPartition || op.Type == OperationType.WipePartition || op.Type == OperationType.ResizeMovePartition))
            {
                throw new InvalidOperationException("Safety Guardrail: Cannot delete, format, wipe, or resize the active Windows boot/system partition while Windows is running on it.");
            }

            if (op.Type == OperationType.ChangeLabel)
            {
                if (targetPart == null)
                    throw new InvalidOperationException("Target partition was not found for relabeling.");
                string targetFs = MapDiskPartFs(targetPart.FileSystem);
                if (!string.IsNullOrEmpty(targetFs) && !string.IsNullOrEmpty(targetPart.DriveLetter))
                {
                    if (!Win32DiskInterop.SetVolumeLabel(targetPart.DriveLetter.TrimEnd('\\') + "\\", op.NewLabel ?? string.Empty))
                        throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(), "SetVolumeLabel failed.");
                }
                else
                {
                    Win32DiskInterop.PreparePartitionForRawWrite(disk.DiskNumber, targetPart.StartingOffset, false);
                    targetPart.Label = op.NewLabel ?? string.Empty;
                    RawDiskEngine.UpdateFileSystemLabelInPlace(disk, targetPart);
                }
                return;
            }

            if (op.Type == OperationType.WipePartition)
            {
                if (targetPart == null)
                    throw new InvalidOperationException("Target partition was not found for wiping.");
                Win32DiskInterop.PreparePartitionForRawWrite(disk.DiskNumber, targetPart.StartingOffset, true);
                RawDiskEngine.WipeRange(disk, targetPart.StartingOffset, targetPart.PartitionLength, op.WipePattern);
                ReadOnlyFileSystemMountEngine.PurgePartitionOverlaysAndCache(disk, targetPart.StartingOffset, targetPart.PartitionLength, false);
                return;
            }

            if (op.Type == OperationType.RestorePartitionTable)
            {
                Win32DiskInterop.WipeDiskBootAndHybridHeaders(disk.DiskNumber, disk.DevicePath, disk.TotalSizeBytes, disk.LogicalSectorSize);
                ReadOnlyFileSystemMountEngine.PurgeDiskOverlaysAndCache(disk.DiskNumber);
                RawDiskEngine.RestorePartitionTable(disk, op.BackupFilePath);
                return;
            }

            string script = BuildDiskPartScript(op, disk.PartitionStyle);

            if (op.Type == OperationType.CreatePartitionTable)
            {
                // Always wipe LBA 0..255 (+0x8000 isohybrid descriptors, MBR, Primary GPT) and Backup GPT first
                Win32DiskInterop.WipeDiskBootAndHybridHeaders(disk.DiskNumber, disk.DevicePath, disk.TotalSizeBytes, disk.LogicalSectorSize);
                ReadOnlyFileSystemMountEngine.PurgeDiskOverlaysAndCache(disk.DiskNumber);
                try
                {
                    RunDiskPartScript("rescan\r\n" + script);
                }
                catch (InvalidOperationException)
                {
                    // Fallback: write clean GPT or MBR table directly when DiskPart refuses removable hybrid media
                    ApplyOperationToModel(disk, op);
                    RawDiskEngine.WritePartitionTable(disk);
                }
                return;
            }

            if (op.Type == OperationType.DeletePartition)
            {
                if (targetPart != null && targetPart.StartingOffset >= 65536L && targetPart.PartitionLength >= 4096L)
                {
                    try
                    {
                        Win32DiskInterop.PreparePartitionForRawWrite(disk.DiskNumber, targetPart.StartingOffset, true);
                        ReadOnlyFileSystemMountEngine.PurgePartitionOverlaysAndCache(disk, targetPart.StartingOffset, targetPart.PartitionLength, true);
                        int wipeLen = (int)Math.Min(65536L, (targetPart.PartitionLength / 4096L) * 4096L);
                        if (wipeLen > 0)
                            RawDiskEngine.WriteBytes(disk, targetPart.StartingOffset, new byte[wipeLen]);
                    }
                    catch
                    {
                    }
                }

                ReadOnlyFileSystemMountEngine.PurgePartitionOverlaysAndCache(disk, op.OriginalStartOffset, op.OriginalLength, false);

                try
                {
                    RunDiskPartScript("rescan\r\n" + script);
                }
                catch (InvalidOperationException)
                {
                    // Fallback for stubborn EFI / MSR / Recovery / isohybrid overlapping partitions
                    if (targetPart != null)
                    {
                        try
                        {
                            Win32DiskInterop.PreparePartitionForRawWrite(disk.DiskNumber, targetPart.StartingOffset, true);
                            ReadOnlyFileSystemMountEngine.PurgePartitionOverlaysAndCache(disk, targetPart.StartingOffset, targetPart.PartitionLength, true);
                            if (targetPart.StartingOffset <= 0x8000L && targetPart.EndOffset > 0x20000L)
                            {
                                // Wipe ISO9660 volume descriptors (+0x8000..+0x20000) without prematurely clobbering LBA 0
                                RawDiskEngine.WriteBytes(disk, 0x8000L, new byte[0x18000]);
                            }
                            else
                            {
                                int wipeLen = (int)Math.Min(65536L, Math.Max(4096L, (targetPart.PartitionLength / 4096L) * 4096L));
                                if (targetPart.StartingOffset + wipeLen <= disk.TotalSizeBytes)
                                    RawDiskEngine.WriteBytes(disk, targetPart.StartingOffset, new byte[wipeLen]);
                            }
                        }
                        catch
                        {
                        }
                    }

                    ApplyOperationToModel(disk, op);
                    RenumberPartitions(disk);
                    RawDiskEngine.WritePartitionTable(disk);
                }
                return;
            }

            string mappedFs = MapDiskPartFs(op.NewFileSystem);
            bool isNonWindowsCreateOrFormat =
                (op.Type == OperationType.CreatePartition || op.Type == OperationType.FormatPartition) &&
                string.IsNullOrEmpty(mappedFs) &&
                op.NewFileSystem != FileSystemType.MicrosoftReserved;

            if (op.Type == OperationType.FormatPartition && isNonWindowsCreateOrFormat && targetPart != null)
            {
                Win32DiskInterop.PreparePartitionForRawWrite(disk.DiskNumber, targetPart.StartingOffset, true);
                targetPart.FileSystem = op.NewFileSystem;
                targetPart.Label = op.NewLabel ?? string.Empty;
                targetPart.UsedBytes = 0;
                targetPart.FreeBytes = targetPart.PartitionLength;
                RawDiskEngine.WriteFileSystemHeader(disk, targetPart);
            }

            bool diskPartSucceeded = false;
            try
            {
                RunDiskPartScript("rescan\r\n" + script);
                diskPartSucceeded = true;
                long purgeStart = op.Type == OperationType.CreatePartition
                    ? op.StartOffset
                    : (targetPart != null ? targetPart.StartingOffset : op.OriginalStartOffset);
                long purgeLen = op.Type == OperationType.CreatePartition
                    ? op.Length
                    : (targetPart != null ? targetPart.PartitionLength : op.OriginalLength);
                ReadOnlyFileSystemMountEngine.PurgePartitionOverlaysAndCache(disk, purgeStart, purgeLen, false);
            }
            catch (InvalidOperationException)
            {
                if (!isNonWindowsCreateOrFormat || (op.Type != OperationType.CreatePartition && op.Type != OperationType.FormatPartition))
                    throw;

                // Fallback for non-Windows filesystems when DiskPart cannot update layout directly
                ApplyOperationToModel(disk, op);
                RenumberPartitions(disk);
                RawDiskEngine.WritePartitionTable(disk);
                var rawTarget = disk.Partitions
                    .Where(p => !p.IsUnallocated)
                    .OrderBy(p => Math.Abs(p.StartingOffset - (op.Type == OperationType.CreatePartition ? op.StartOffset : op.OriginalStartOffset)))
                    .FirstOrDefault();
                if (rawTarget == null)
                    throw;

                Win32DiskInterop.PreparePartitionForRawWrite(disk.DiskNumber, rawTarget.StartingOffset, true);
                rawTarget.FileSystem = op.NewFileSystem;
                rawTarget.Label = op.NewLabel ?? string.Empty;
                rawTarget.UsedBytes = 0;
                rawTarget.FreeBytes = rawTarget.PartitionLength;
                RawDiskEngine.WriteFileSystemHeader(disk, rawTarget);
                RawDiskEngine.InspectPartitionSuperblock(disk, rawTarget);
            }

            if (isNonWindowsCreateOrFormat && diskPartSucceeded)
            {
                var liveDisks = Win32DiskInterop.EnumeratePhysicalDisks();
                var liveDisk = liveDisks.FirstOrDefault(d => d.DiskNumber == disk.DiskNumber) ?? disk;
                long expectedStart = op.Type == OperationType.CreatePartition
                    ? op.StartOffset
                    : (targetPart != null ? targetPart.StartingOffset : op.OriginalStartOffset);

                var livePart = liveDisk.Partitions
                    .Where(p => !p.IsUnallocated)
                    .OrderBy(p => Math.Abs(p.StartingOffset - expectedStart))
                    .FirstOrDefault();

                if (livePart == null)
                    throw new InvalidOperationException("Unable to locate target partition on Disk " + disk.DiskNumber + " after DiskPart layout update.");

                liveDisk.CanRawRead = true;
                RawDiskEngine.InspectPartitionSuperblock(liveDisk, livePart);

                if (op.Type == OperationType.CreatePartition ||
                    (op.NewFileSystem != FileSystemType.RAW && livePart.FileSystem != op.NewFileSystem))
                {
                    Win32DiskInterop.PreparePartitionForRawWrite(liveDisk.DiskNumber, livePart.StartingOffset, true);
                    livePart.FileSystem = op.NewFileSystem;
                    livePart.Label = op.NewLabel ?? string.Empty;
                    livePart.UsedBytes = 0;
                    livePart.FreeBytes = livePart.PartitionLength;
                    RawDiskEngine.WriteFileSystemHeader(liveDisk, livePart);
                    RawDiskEngine.InspectPartitionSuperblock(liveDisk, livePart);
                }

                if (op.NewFileSystem != FileSystemType.RAW && livePart.FileSystem != op.NewFileSystem)
                {
                    throw new InvalidDataException(string.Format(
                        "Post-format verification failed on Disk {0} Part #{1}: expected {2}, read back {3}.",
                        liveDisk.DiskNumber, livePart.PartitionNumber, op.NewFileSystem, livePart.FileSystem));
                }
            }
        }

        private static void RunDiskPartScript(string script)
        {
            string tempScript = Path.Combine(Path.GetTempPath(), "winparted_" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                File.WriteAllText(tempScript, script, Encoding.ASCII);
                var psi = new ProcessStartInfo
                {
                    FileName = "diskpart.exe",
                    Arguments = "/s \"" + tempScript + "\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (var proc = Process.Start(psi))
                {
                    string stdout = proc.StandardOutput.ReadToEnd();
                    string stderr = proc.StandardError.ReadToEnd();
                    proc.WaitForExit();
                    if (proc.ExitCode != 0)
                        throw new InvalidOperationException("DiskPart failed (exit " + proc.ExitCode + "): " + stdout + " " + stderr);
                }
            }
            finally
            {
                if (File.Exists(tempScript))
                {
                    try { File.Delete(tempScript); } catch { }
                }
            }
        }

        /// <summary>
        /// Generates the equivalent Windows <c>diskpart.exe</c> script (for live execution or user export).
        /// </summary>
        public static string BuildDiskPartScript(PendingOperation op, PartitionTableStyle tableStyle = PartitionTableStyle.GPT)
        {
            var sb = new StringBuilder();
            sb.AppendLine("select disk " + op.TargetDiskNumber);
            long sizeMb = Math.Max(1, op.Length / 1048576L);
            long offsetKb = op.StartOffset / 1024L;

            Guid resolvedGptGuid = op.NewGptTypeGuid != Guid.Empty
                ? op.NewGptTypeGuid
                : GptPartitionTypeGuids.ForFileSystem(op.NewFileSystem);
            byte resolvedMbrId = op.NewMbrType != 0
                ? op.NewMbrType
                : RawDiskEngine.GetDefaultMbrType(op.NewFileSystem);

            switch (op.Type)
            {
                case OperationType.CreatePartitionTable:
                    sb.AppendLine("clean");
                    sb.AppendLine(op.NewTableStyle == PartitionTableStyle.GPT ? "convert gpt" : "convert mbr");
                    break;

                case OperationType.CreatePartition:
                    if (op.NewFileSystem == FileSystemType.EfiSystem)
                        sb.AppendLine(string.Format("create partition efi size={0}", sizeMb));
                    else if (op.NewFileSystem == FileSystemType.MicrosoftReserved)
                        sb.AppendLine(string.Format("create partition msr size={0}", sizeMb));
                    else
                        sb.AppendLine(string.Format("create partition primary size={0} offset={1}", sizeMb, offsetKb));

                    string fsArg = MapDiskPartFs(op.NewFileSystem);
                    if (!string.IsNullOrEmpty(fsArg))
                    {
                        string lbl = string.IsNullOrEmpty(op.NewLabel) ? "" : " label=\"" + op.NewLabel + "\"";
                        sb.AppendLine(string.Format("format fs={0} quick{1}", fsArg, lbl));
                        sb.AppendLine("assign noerr");
                    }
                    else if (op.NewFileSystem != FileSystemType.MicrosoftReserved)
                    {
                        if (tableStyle == PartitionTableStyle.GPT && resolvedGptGuid != Guid.Empty)
                            sb.AppendLine("set id=" + resolvedGptGuid.ToString("D") + " override");
                        else if (tableStyle == PartitionTableStyle.MBR && resolvedMbrId != 0)
                            sb.AppendLine(string.Format("set id={0:X2} override", resolvedMbrId));
                    }
                    break;

                case OperationType.DeletePartition:
                    sb.AppendLine("select partition " + op.TargetPartitionNumber);
                    sb.AppendLine("delete partition override");
                    break;

                case OperationType.FormatPartition:
                    sb.AppendLine("select partition " + op.TargetPartitionNumber);
                    if (tableStyle == PartitionTableStyle.GPT && resolvedGptGuid != Guid.Empty)
                        sb.AppendLine("set id=" + resolvedGptGuid.ToString("D") + " override");
                    else if (tableStyle == PartitionTableStyle.MBR && resolvedMbrId != 0)
                        sb.AppendLine(string.Format("set id={0:X2} override", resolvedMbrId));

                    string fmtFs = MapDiskPartFs(op.NewFileSystem);
                    if (!string.IsNullOrEmpty(fmtFs))
                    {
                        string lbl = string.IsNullOrEmpty(op.NewLabel) ? "" : " label=\"" + op.NewLabel + "\"";
                        sb.AppendLine(string.Format("format fs={0} quick{1}", fmtFs, lbl));
                        sb.AppendLine("assign noerr");
                    }
                    break;

                case OperationType.ResizeMovePartition:
                    sb.AppendLine("select partition " + op.TargetPartitionNumber);
                    if (op.Length > op.OriginalLength)
                    {
                        long extendMb = Math.Max(1, (op.Length - op.OriginalLength) / 1048576L);
                        sb.AppendLine("extend size=" + extendMb);
                    }
                    else if (op.Length < op.OriginalLength)
                    {
                        long shrinkMb = Math.Max(1, (op.OriginalLength - op.Length) / 1048576L);
                        sb.AppendLine("shrink desired=" + shrinkMb);
                    }
                    break;

                case OperationType.ChangeFlags:
                    sb.AppendLine("select partition " + op.TargetPartitionNumber);
                    if (op.NewGptTypeGuid != Guid.Empty)
                        sb.AppendLine("set id=" + op.NewGptTypeGuid.ToString("D") + " override");
                    else if (op.NewMbrType != 0)
                        sb.AppendLine(string.Format("set id={0:X2} override", op.NewMbrType));
                    if (op.NewMbrBoot)
                        sb.AppendLine("active");
                    break;
            }
            return sb.ToString();
        }

        private static string MapDiskPartFs(FileSystemType fs)
        {
            switch (fs)
            {
                case FileSystemType.NTFS:
                case FileSystemType.WindowsRecovery:
                    return "ntfs";
                case FileSystemType.FAT32:
                case FileSystemType.EfiSystem:
                    return "fat32";
                case FileSystemType.exFAT:
                    return "exfat";
                case FileSystemType.ReFS:
                    return "refs";
                default:
                    return string.Empty;
            }
        }
    }
}
