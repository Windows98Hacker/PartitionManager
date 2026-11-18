using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using WinParted.Interop;
using WinParted.Models;

namespace WinParted.Core
{
    /// <summary>
    /// Snapshot of progress, throughput, and status log details emitted during queued operation execution.
    /// </summary>
    public sealed class OperationProgressReport
    {
        public int OperationIndex { get; set; }
        public int TotalOperations { get; set; }
        public string OperationSummary { get; set; }
        public string CurrentStage { get; set; }
        public int OperationPercent { get; set; }
        public int OverallPercent { get; set; }
        public long BytesProcessed { get; set; }
        public long BytesTotal { get; set; }
        public double ThroughputMiBPerSec { get; set; }
        public bool IsIndeterminateStage { get; set; }
        public string LogLine { get; set; }
        public bool IsOperationComplete { get; set; }
        public bool IsOperationFailed { get; set; }
    }

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
        public List<string> ApplyAll(Action<int, string> progressCallback = null, Action<OperationProgressReport> detailedProgress = null)
        {
            var log = new List<string>();
            if (_queue.Count == 0)
                return log;

            int total = _queue.Count;
            var executed = new List<PendingOperation>();

            for (int i = 0; i < total; i++)
            {
                var op = _queue[i];
                ReportProgress(progressCallback, detailedProgress, i, total, op.Summary, 0,
                    "Preparing: " + op.Summary,
                    logLine: string.Format("[{0}/{1}] Starting: {2}", i + 1, total, op.Summary));

                try
                {
                    var disk = GetBaselineDisk(op.TargetDiskNumber);
                    if (disk == null)
                        throw new InvalidOperationException("Target disk #" + op.TargetDiskNumber + " not found.");

                    if (disk.IsVirtualImage)
                    {
                        ExecuteVirtualDiskOperation(disk, op, i, total, progressCallback, detailedProgress);
                        string okLine = "[OK] Virtual Disk: " + op.Summary;
                        log.Add(okLine);
                        ReportProgress(progressCallback, detailedProgress, i, total, op.Summary, 100,
                            "Completed: " + op.Summary,
                            logLine: okLine,
                            isComplete: true);
                    }
                    else
                    {
                        ExecutePhysicalDiskOperation(disk, op, i, total, progressCallback, detailedProgress);
                        ReportProgress(progressCallback, detailedProgress, i, total, op.Summary, 96,
                            "Refreshing physical disk layout...",
                            indeterminate: true);
                        var freshPhysical = Win32DiskInterop.EnumeratePhysicalDisks();
                        var virtuals = _baselineDisks.Where(d => d.IsVirtualImage).ToList();
                        SetBaselineDisks(freshPhysical, virtuals);

                        string okLine = "[OK] Physical Disk: " + op.Summary;
                        log.Add(okLine);
                        ReportProgress(progressCallback, detailedProgress, i, total, op.Summary, 100,
                            "Completed: " + op.Summary,
                            logLine: okLine,
                            isComplete: true);
                    }

                    executed.Add(op);
                }
                catch (Exception ex)
                {
                    ReportProgress(progressCallback, detailedProgress, i, total, op.Summary, 100,
                        "Failed: " + ex.Message,
                        logLine: "[ERR] " + op.Summary + ": " + ex.Message,
                        isFailed: true);
                    throw;
                }
            }

            foreach (var done in executed)
                _queue.Remove(done);

            if (progressCallback != null) progressCallback(100, "All operations completed and verified.");
            return log;
        }

        private static void ReportProgress(
            Action<int, string> progressCallback,
            Action<OperationProgressReport> detailedProgress,
            int opIndex,
            int totalOps,
            string opSummary,
            int opPct,
            string stage,
            long bytesProcessed = 0,
            long bytesTotal = 0,
            double speedMiB = 0.0,
            bool indeterminate = false,
            string logLine = null,
            bool isComplete = false,
            bool isFailed = false)
        {
            int clampedOp = Math.Max(0, Math.Min(100, opPct));
            int overall = totalOps > 0
                ? (int)Math.Round(((opIndex * 100.0) + clampedOp) / totalOps)
                : 100;
            overall = Math.Max(0, Math.Min(100, overall));

            if (progressCallback != null)
                progressCallback(overall, stage ?? opSummary);

            if (detailedProgress != null)
            {
                detailedProgress(new OperationProgressReport
                {
                    OperationIndex = opIndex,
                    TotalOperations = totalOps,
                    OperationSummary = opSummary,
                    CurrentStage = stage,
                    OperationPercent = clampedOp,
                    OverallPercent = overall,
                    BytesProcessed = bytesProcessed,
                    BytesTotal = bytesTotal,
                    ThroughputMiBPerSec = speedMiB,
                    IsIndeterminateStage = indeterminate,
                    LogLine = logLine,
                    IsOperationComplete = isComplete,
                    IsOperationFailed = isFailed
                });
            }
        }

        private static Action<long, long> CreateByteProgressCallback(
            Action<int, string> progressCallback,
            Action<OperationProgressReport> detailedProgress,
            int opIndex,
            int totalOps,
            string opSummary,
            int startPct,
            int endPct,
            string verb)
        {
            var sw = Stopwatch.StartNew();
            long lastEmitMs = -100;
            return (done, total) =>
            {
                long elapsedMs = sw.ElapsedMilliseconds;
                bool isBoundary = done <= 0 || (total > 0 && done >= total);
                if (!isBoundary && elapsedMs - lastEmitMs < 35)
                    return;
                lastEmitMs = elapsedMs;

                double ratio = (total > 0) ? Math.Max(0.0, Math.Min(1.0, (double)done / total)) : 1.0;
                int opPct = startPct + (int)Math.Round((endPct - startPct) * ratio);
                double secs = sw.Elapsed.TotalSeconds;
                double speedMiB = (done > 0 && secs > 0.01) ? (done / 1048576.0) / secs : 0.0;
                string stageText = total > 0
                    ? string.Format("{0}: {1} / {2} ({3:F1}%) — {4:F1} MiB/s",
                        verb,
                        ByteFormatter.FormatBytes(done),
                        ByteFormatter.FormatBytes(total),
                        ratio * 100.0,
                        speedMiB)
                    : verb;

                ReportProgress(
                    progressCallback,
                    detailedProgress,
                    opIndex,
                    totalOps,
                    opSummary,
                    opPct,
                    stageText,
                    bytesProcessed: done,
                    bytesTotal: total,
                    speedMiB: speedMiB);
            };
        }

        /// <summary>
        /// Executes an operation on a virtual <c>.img</c> file with primary/backup GPT+MBR snapshot rollback,
        /// raw payload preservation during Move/Resize/Clone, and post-write CRC32/MBR integrity verification.
        /// </summary>
        private void ExecuteVirtualDiskOperation(
            DiskInfo disk,
            PendingOperation op,
            int opIndex,
            int totalOps,
            Action<int, string> progressCallback,
            Action<OperationProgressReport> detailedProgress)
        {
            ReportProgress(progressCallback, detailedProgress, opIndex, totalOps, op.Summary, 5,
                "Snapshotting primary & backup GPT/MBR headers...",
                logLine: "  Snapshotting primary (34 sectors) & backup GPT headers...");

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
                        ReportProgress(progressCallback, detailedProgress, opIndex, totalOps, op.Summary, 25,
                            "Purging stale overlays & filesystem cache...",
                            logLine: "  Purging stale overlays & filesystem cache...");
                        ReadOnlyFileSystemMountEngine.PurgeDiskOverlaysAndCache(disk.DiskNumber);
                        ReportProgress(progressCallback, detailedProgress, opIndex, totalOps, op.Summary, 60,
                            string.Format("Writing new {0} partition table...", op.NewTableStyle));
                        ApplyOperationToModel(disk, op);
                        RawDiskEngine.WritePartitionTable(disk);
                        break;

                    case OperationType.CreatePartition:
                        ReportProgress(progressCallback, detailedProgress, opIndex, totalOps, op.Summary, 25,
                            "Updating partition table entries...");
                        ApplyOperationToModel(disk, op);
                        RenumberPartitions(disk);
                        var created = disk.Partitions.First(p => p.StartingOffset == op.StartOffset);
                        RawDiskEngine.WritePartitionTable(disk);
                        ReportProgress(progressCallback, detailedProgress, opIndex, totalOps, op.Summary, 55,
                            "Purging overlays & initializing filesystem header...",
                            logLine: string.Format("  Formatting {0} superblock at offset 0x{1:X}...", op.NewFileSystem, created.StartingOffset));
                        ReadOnlyFileSystemMountEngine.PurgePartitionOverlaysAndCache(disk, created.StartingOffset, created.PartitionLength, true);
                        RawDiskEngine.WriteFileSystemHeader(disk, created);
                        break;

                    case OperationType.DeletePartition:
                        ReportProgress(progressCallback, detailedProgress, opIndex, totalOps, op.Summary, 20,
                            "Purging overlays & clearing filesystem cache...");
                        ReadOnlyFileSystemMountEngine.PurgePartitionOverlaysAndCache(disk, op.OriginalStartOffset, op.OriginalLength, true);
                        ReportProgress(progressCallback, detailedProgress, opIndex, totalOps, op.Summary, 55,
                            "Removing entry from partition table...");
                        ApplyOperationToModel(disk, op);
                        RenumberPartitions(disk);
                        RawDiskEngine.WritePartitionTable(disk);
                        ReportProgress(progressCallback, detailedProgress, opIndex, totalOps, op.Summary, 75,
                            "Zeroing vacated partition header sectors...");
                        ZeroVacatedPartitionHeader(disk, op.OriginalStartOffset, op.OriginalLength);
                        break;

                    case OperationType.ResizeMovePartition:
                        if (op.StartOffset != op.OriginalStartOffset)
                        {
                            long bytesToMove = Math.Min(op.OriginalLength, op.Length);
                            string moveDir = op.StartOffset < op.OriginalStartOffset ? "forward" : "backward overlap-safe";
                            ReportProgress(progressCallback, detailedProgress, opIndex, totalOps, op.Summary, 15,
                                "Moving partition sectors (" + moveDir + ")...",
                                logLine: string.Format("  Moving {0} ({1})...", ByteFormatter.FormatBytes(bytesToMove), moveDir));
                            RawDiskEngine.MovePartitionBytes(disk, op.OriginalStartOffset, op.StartOffset, bytesToMove,
                                CreateByteProgressCallback(progressCallback, detailedProgress, opIndex, totalOps, op.Summary, 15, 70, "Moving partition data"));
                            ReadOnlyFileSystemMountEngine.PurgePartitionOverlaysAndCache(disk, op.OriginalStartOffset, op.OriginalLength, false);
                        }
                        ReadOnlyFileSystemMountEngine.Unmount(disk.DiskNumber, op.OriginalStartOffset);
                        ReadOnlyFileSystemMountEngine.Unmount(disk.DiskNumber, op.StartOffset);
                        ReportProgress(progressCallback, detailedProgress, opIndex, totalOps, op.Summary, 76,
                            "Updating partition table geometry...");
                        ApplyOperationToModel(disk, op);
                        RenumberPartitions(disk);
                        var resizedPart = disk.Partitions.First(p => p.StartingOffset == op.StartOffset);
                        RawDiskEngine.WritePartitionTable(disk);
                        ReportProgress(progressCallback, detailedProgress, opIndex, totalOps, op.Summary, 84,
                            "Resizing filesystem metadata in-place...");
                        RawDiskEngine.ResizeFileSystemInPlace(disk, resizedPart, op.OriginalLength);
                        break;

                    case OperationType.FormatPartition:
                        ReportProgress(progressCallback, detailedProgress, opIndex, totalOps, op.Summary, 20,
                            "Purging existing overlays & stale superblocks...");
                        ReadOnlyFileSystemMountEngine.PurgePartitionOverlaysAndCache(disk, op.OriginalStartOffset, op.OriginalLength, true);
                        ReportProgress(progressCallback, detailedProgress, opIndex, totalOps, op.Summary, 55,
                            "Updating partition table types...");
                        ApplyOperationToModel(disk, op);
                        var fmtPart = disk.Partitions.First(p => p.StartingOffset == op.OriginalStartOffset);
                        RawDiskEngine.WritePartitionTable(disk);
                        ReportProgress(progressCallback, detailedProgress, opIndex, totalOps, op.Summary, 78,
                            string.Format("Writing {0} filesystem structures...", op.NewFileSystem),
                            logLine: string.Format("  Writing {0} structures (label '{1}')...", op.NewFileSystem, op.NewLabel ?? string.Empty));
                        RawDiskEngine.WriteFileSystemHeader(disk, fmtPart);
                        break;

                    case OperationType.ChangeLabel:
                        ReportProgress(progressCallback, detailedProgress, opIndex, totalOps, op.Summary, 40,
                            "Updating partition table name...");
                        ApplyOperationToModel(disk, op);
                        var lblPart = disk.Partitions.First(p => p.StartingOffset == op.OriginalStartOffset);
                        RawDiskEngine.WritePartitionTable(disk);
                        ReportProgress(progressCallback, detailedProgress, opIndex, totalOps, op.Summary, 75,
                            "Writing in-place volume label...");
                        RawDiskEngine.UpdateFileSystemLabelInPlace(disk, lblPart);
                        break;

                    case OperationType.ChangeFlags:
                        ReportProgress(progressCallback, detailedProgress, opIndex, totalOps, op.Summary, 50,
                            "Updating GPT GUID / attributes or MBR flags...");
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
                        ReportProgress(progressCallback, detailedProgress, opIndex, totalOps, op.Summary, 15,
                            "Cloning partition sectors...",
                            logLine: string.Format("  Cloning {0} from Disk #{1} to offset 0x{2:X}...", ByteFormatter.FormatBytes(op.SourceLength), srcDisk.DiskNumber, op.StartOffset));
                        RawDiskEngine.CopyPartitionBytes(srcDisk, op.SourceStartOffset, disk, op.StartOffset, op.SourceLength,
                            CreateByteProgressCallback(progressCallback, detailedProgress, opIndex, totalOps, op.Summary, 15, 75, "Cloning partition sectors"));
                        ReportProgress(progressCallback, detailedProgress, opIndex, totalOps, op.Summary, 80,
                            "Updating destination partition table...");
                        ApplyOperationToModel(disk, op);
                        RenumberPartitions(disk);
                        var clonedPart = disk.Partitions.First(p => p.StartingOffset == op.StartOffset);
                        RawDiskEngine.WritePartitionTable(disk);
                        if (op.Length > op.SourceLength)
                        {
                            ReportProgress(progressCallback, detailedProgress, opIndex, totalOps, op.Summary, 86,
                                "Expanding cloned filesystem to fill target partition...");
                            RawDiskEngine.ResizeFileSystemInPlace(disk, clonedPart, op.SourceLength);
                        }
                        break;

                    case OperationType.WipePartition:
                        ReportProgress(progressCallback, detailedProgress, opIndex, totalOps, op.Summary, 15,
                            string.Format("Wiping partition ({0})...", op.WipePattern),
                            logLine: string.Format("  Wiping {0} using {1}...", ByteFormatter.FormatBytes(op.OriginalLength), op.WipePattern));
                        RawDiskEngine.WipeRange(disk, op.OriginalStartOffset, op.OriginalLength, op.WipePattern,
                            CreateByteProgressCallback(progressCallback, detailedProgress, opIndex, totalOps, op.Summary, 15, 80, "Wiping sectors (" + op.WipePattern + ")"));
                        ReportProgress(progressCallback, detailedProgress, opIndex, totalOps, op.Summary, 85,
                            "Purging cache & updating partition table...");
                        ReadOnlyFileSystemMountEngine.PurgePartitionOverlaysAndCache(disk, op.OriginalStartOffset, op.OriginalLength, false);
                        ApplyOperationToModel(disk, op);
                        RawDiskEngine.WritePartitionTable(disk);
                        break;

                    case OperationType.RestorePartitionTable:
                        ReportProgress(progressCallback, detailedProgress, opIndex, totalOps, op.Summary, 30,
                            "Purging stale overlays & cache...");
                        ReadOnlyFileSystemMountEngine.PurgeDiskOverlaysAndCache(disk.DiskNumber);
                        ReportProgress(progressCallback, detailedProgress, opIndex, totalOps, op.Summary, 65,
                            "Restoring primary & backup partition table sectors...");
                        RawDiskEngine.RestorePartitionTable(disk, op.BackupFilePath);
                        break;
                }

                ReportProgress(progressCallback, detailedProgress, opIndex, totalOps, op.Summary, 90,
                    "Flushing buffers & reloading virtual image...");
                // Reload from disk to verify CRC32, Primary+Backup GPT sync, and actual persisted bytes
                var reloaded = RawDiskEngine.LoadVirtualDisk(disk.BackingFilePath, disk.DiskNumber);
                ReportProgress(progressCallback, detailedProgress, opIndex, totalOps, op.Summary, 96,
                    "Verifying GPT CRC32 & partition table integrity...",
                    logLine: "  Verifying GPT CRC32 & partition table integrity...");
                string integrityReport;
                if (!RawDiskEngine.VerifyDiskPartitionTableIntegrity(reloaded, out integrityReport))
                    throw new InvalidDataException("Post-operation partition table integrity check failed: " + integrityReport);

                ReplaceOrAddVirtualDisk(reloaded);
            }
            catch
            {
                // Rollback partition table headers on failure
                ReportProgress(progressCallback, detailedProgress, opIndex, totalOps, op.Summary, 92,
                    "Rolling back partition table header snapshot...",
                    logLine: "  [Rollback] Restoring pre-operation GPT/MBR header snapshot...");
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
        private void ExecutePhysicalDiskOperation(
            DiskInfo disk,
            PendingOperation op,
            int opIndex,
            int totalOps,
            Action<int, string> progressCallback,
            Action<OperationProgressReport> detailedProgress)
        {
            ReportProgress(progressCallback, detailedProgress, opIndex, totalOps, op.Summary, 5,
                "Verifying administrator privileges & boot guardrails...");

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
                ReportProgress(progressCallback, detailedProgress, opIndex, totalOps, op.Summary, 25,
                    "Preparing volume for label update...");
                string targetFs = MapDiskPartFs(targetPart.FileSystem);
                if (!string.IsNullOrEmpty(targetFs) && !string.IsNullOrEmpty(targetPart.DriveLetter))
                {
                    ReportProgress(progressCallback, detailedProgress, opIndex, totalOps, op.Summary, 60,
                        "Calling Win32 SetVolumeLabel...");
                    if (!Win32DiskInterop.SetVolumeLabel(targetPart.DriveLetter.TrimEnd('\\') + "\\", op.NewLabel ?? string.Empty))
                        throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(), "SetVolumeLabel failed.");
                }
                else
                {
                    Win32DiskInterop.PreparePartitionForRawWrite(disk.DiskNumber, targetPart.StartingOffset, false);
                    ReportProgress(progressCallback, detailedProgress, opIndex, totalOps, op.Summary, 60,
                        "Updating raw filesystem superblock label...");
                    targetPart.Label = op.NewLabel ?? string.Empty;
                    RawDiskEngine.UpdateFileSystemLabelInPlace(disk, targetPart);
                }
                return;
            }

            if (op.Type == OperationType.WipePartition)
            {
                if (targetPart == null)
                    throw new InvalidOperationException("Target partition was not found for wiping.");
                ReportProgress(progressCallback, detailedProgress, opIndex, totalOps, op.Summary, 12,
                    "Locking & dismounting target partition volume...",
                    logLine: string.Format("  Locking Disk {0} @ 0x{1:X} ({2})...", disk.DiskNumber, targetPart.StartingOffset, ByteFormatter.FormatBytes(targetPart.PartitionLength)));
                Win32DiskInterop.PreparePartitionForRawWrite(disk.DiskNumber, targetPart.StartingOffset, true);
                RawDiskEngine.WipeRange(disk, targetPart.StartingOffset, targetPart.PartitionLength, op.WipePattern,
                    CreateByteProgressCallback(progressCallback, detailedProgress, opIndex, totalOps, op.Summary, 18, 86, "Wiping physical sectors (" + op.WipePattern + ")"));
                ReportProgress(progressCallback, detailedProgress, opIndex, totalOps, op.Summary, 90,
                    "Purging overlays & filesystem cache...");
                ReadOnlyFileSystemMountEngine.PurgePartitionOverlaysAndCache(disk, targetPart.StartingOffset, targetPart.PartitionLength, false);
                return;
            }

            if (op.Type == OperationType.RestorePartitionTable)
            {
                ReportProgress(progressCallback, detailedProgress, opIndex, totalOps, op.Summary, 20,
                    "Wiping stale boot & hybrid descriptors...");
                Win32DiskInterop.WipeDiskBootAndHybridHeaders(disk.DiskNumber, disk.DevicePath, disk.TotalSizeBytes, disk.LogicalSectorSize);
                ReadOnlyFileSystemMountEngine.PurgeDiskOverlaysAndCache(disk.DiskNumber);
                ReportProgress(progressCallback, detailedProgress, opIndex, totalOps, op.Summary, 60,
                    "Restoring primary & backup partition table sectors...");
                RawDiskEngine.RestorePartitionTable(disk, op.BackupFilePath);
                return;
            }

            string script = BuildDiskPartScript(op, disk.PartitionStyle);
            Action<string> diskPartLineHandler = line =>
            {
                ReportProgress(progressCallback, detailedProgress, opIndex, totalOps, op.Summary, 55,
                    "DiskPart: " + line,
                    indeterminate: true,
                    logLine: "  [diskpart] " + line);
            };

            if (op.Type == OperationType.CreatePartitionTable)
            {
                ReportProgress(progressCallback, detailedProgress, opIndex, totalOps, op.Summary, 15,
                    "Wiping LBA 0..255, isohybrid descriptors & Backup GPT...",
                    logLine: "  Wiping MBR/isohybrid/GPT sectors and purging overlays...");
                // Always wipe LBA 0..255 (+0x8000 isohybrid descriptors, MBR, Primary GPT) and Backup GPT first
                Win32DiskInterop.WipeDiskBootAndHybridHeaders(disk.DiskNumber, disk.DevicePath, disk.TotalSizeBytes, disk.LogicalSectorSize);
                ReadOnlyFileSystemMountEngine.PurgeDiskOverlaysAndCache(disk.DiskNumber);
                try
                {
                    ReportProgress(progressCallback, detailedProgress, opIndex, totalOps, op.Summary, 35,
                        "Running DiskPart to initialize partition table...",
                        indeterminate: true);
                    RunDiskPartScript("rescan\r\n" + script, diskPartLineHandler);
                }
                catch (InvalidOperationException)
                {
                    ReportProgress(progressCallback, detailedProgress, opIndex, totalOps, op.Summary, 70,
                        "DiskPart declined; writing clean partition table directly...",
                        logLine: "  [Fallback] Writing raw GPT/MBR headers directly...");
                    // Fallback: write clean GPT or MBR table directly when DiskPart refuses removable hybrid media
                    ApplyOperationToModel(disk, op);
                    RawDiskEngine.WritePartitionTable(disk);
                    Win32DiskInterop.NotifyDiskPropertiesUpdated(disk.DevicePath);
                }
                return;
            }

            if (op.Type == OperationType.DeletePartition)
            {
                ReportProgress(progressCallback, detailedProgress, opIndex, totalOps, op.Summary, 15,
                    "Locking volume & clearing partition superblock headers...");
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
                    ReportProgress(progressCallback, detailedProgress, opIndex, totalOps, op.Summary, 35,
                        "Running DiskPart to delete partition...",
                        indeterminate: true);
                    RunDiskPartScript("rescan\r\n" + script, diskPartLineHandler);
                }
                catch (InvalidOperationException)
                {
                    ReportProgress(progressCallback, detailedProgress, opIndex, totalOps, op.Summary, 70,
                        "DiskPart declined; removing partition via raw table write...",
                        logLine: "  [Fallback] Updating raw partition table directly...");
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

            long targetLenBytes = op.Length > 0 ? op.Length : op.OriginalLength;
            string mappedFs = MapDiskPartFs(op.NewFileSystem, targetLenBytes);
            bool isNonWindowsCreateOrFormat =
                (op.Type == OperationType.CreatePartition || op.Type == OperationType.FormatPartition) &&
                string.IsNullOrEmpty(mappedFs) &&
                op.NewFileSystem != FileSystemType.MicrosoftReserved;
            long expectedStart = op.Type == OperationType.CreatePartition
                ? op.StartOffset
                : (targetPart != null ? targetPart.StartingOffset : op.OriginalStartOffset);

            bool diskPartSucceeded = false;
            try
            {
                ReportProgress(progressCallback, detailedProgress, opIndex, totalOps, op.Summary, 35,
                    "Running DiskPart script...",
                    indeterminate: true);
                RunDiskPartScript("rescan\r\n" + script, diskPartLineHandler);
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
                if (op.Type != OperationType.CreatePartition && op.Type != OperationType.FormatPartition)
                    throw;

                bool needTableUpdate = true;
                if (op.Type == OperationType.FormatPartition && targetPart != null)
                {
                    if (disk.PartitionStyle == PartitionTableStyle.GPT)
                    {
                        Guid wantGuid = op.NewGptTypeGuid != Guid.Empty ? op.NewGptTypeGuid : GptPartitionTypeGuids.ForFileSystem(op.NewFileSystem);
                        needTableUpdate = (targetPart.GptTypeGuid != wantGuid);
                    }
                    else
                    {
                        byte wantMbr = op.NewMbrType != 0 ? op.NewMbrType : RawDiskEngine.GetDefaultMbrType(op.NewFileSystem);
                        needTableUpdate = (targetPart.MbrPartitionType != wantMbr);
                    }
                }

                if (needTableUpdate)
                {
                    ReportProgress(progressCallback, detailedProgress, opIndex, totalOps, op.Summary, 48,
                        "DiskPart unavailable/declined; updating partition table directly...",
                        logLine: "  [Fallback] Updating partition table via raw writer...");
                    ApplyOperationToModel(disk, op);
                    RenumberPartitions(disk);
                    RawDiskEngine.WritePartitionTable(disk);
                    Win32DiskInterop.NotifyDiskPropertiesUpdated(disk.DevicePath);
                }

                bool formattedNatively = false;
                if (!string.IsNullOrEmpty(MapWindowsFormatName(op.NewFileSystem)))
                {
                    formattedNatively = FormatPhysicalWindowsVolume(
                        disk,
                        expectedStart,
                        op.NewFileSystem,
                        op.NewLabel,
                        targetLenBytes,
                        opIndex,
                        totalOps,
                        op.Summary,
                        progressCallback,
                        detailedProgress);
                }

                if (!formattedNatively)
                {
                    ReportProgress(progressCallback, detailedProgress, opIndex, totalOps, op.Summary, 76,
                        string.Format("Writing {0} filesystem structures in userspace...", op.NewFileSystem),
                        logLine: string.Format("  [Fallback] Writing spec-compliant {0} structures directly...", op.NewFileSystem));

                    var rawTarget = disk.Partitions
                        .Where(p => !p.IsUnallocated)
                        .OrderBy(p => Math.Abs(p.StartingOffset - expectedStart))
                        .FirstOrDefault();
                    if (rawTarget == null)
                        throw new InvalidOperationException("Target partition not found after partition table update.");

                    Win32DiskInterop.PreparePartitionForRawWrite(disk.DiskNumber, rawTarget.StartingOffset, true);
                    disk.CanRawRead = true;
                    rawTarget.FileSystem = op.NewFileSystem;
                    rawTarget.Label = op.NewLabel ?? string.Empty;
                    rawTarget.UsedBytes = 0;
                    rawTarget.FreeBytes = rawTarget.PartitionLength;
                    RawDiskEngine.WriteFileSystemHeader(disk, rawTarget);
                    Win32DiskInterop.NotifyDiskPropertiesUpdated(disk.DevicePath);
                    RawDiskEngine.InspectPartitionSuperblock(disk, rawTarget);

                    if (op.NewFileSystem != FileSystemType.RAW &&
                        op.NewFileSystem != FileSystemType.MicrosoftReserved &&
                        rawTarget.FileSystem != op.NewFileSystem)
                    {
                        throw new InvalidDataException(string.Format(
                            "Post-format verification failed on Disk {0} Part #{1}: expected {2}, read back {3}.",
                            disk.DiskNumber, rawTarget.PartitionNumber, op.NewFileSystem, rawTarget.FileSystem));
                    }
                }
            }

            if (isNonWindowsCreateOrFormat && diskPartSucceeded)
            {
                ReportProgress(progressCallback, detailedProgress, opIndex, totalOps, op.Summary, 84,
                    string.Format("Verifying & stamping {0} filesystem structures...", op.NewFileSystem),
                    logLine: string.Format("  Re-scanning Disk {0} & verifying {1} superblock...", disk.DiskNumber, op.NewFileSystem));
                var liveDisks = Win32DiskInterop.EnumeratePhysicalDisks();
                var liveDisk = liveDisks.FirstOrDefault(d => d.DiskNumber == disk.DiskNumber) ?? disk;

                var livePart = liveDisk.Partitions
                    .Where(p => !p.IsUnallocated)
                    .OrderBy(p => Math.Abs(p.StartingOffset - expectedStart))
                    .FirstOrDefault();

                if (livePart == null)
                    throw new InvalidOperationException("Unable to locate target partition on Disk " + disk.DiskNumber + " after DiskPart layout update.");

                liveDisk.CanRawRead = true;

                if (op.Type == OperationType.CreatePartition || op.Type == OperationType.FormatPartition)
                {
                    Win32DiskInterop.PreparePartitionForRawWrite(liveDisk.DiskNumber, livePart.StartingOffset, true);
                    livePart.FileSystem = op.NewFileSystem;
                    livePart.Label = op.NewLabel ?? string.Empty;
                    livePart.UsedBytes = 0;
                    livePart.FreeBytes = livePart.PartitionLength;
                    RawDiskEngine.WriteFileSystemHeader(liveDisk, livePart);
                    Win32DiskInterop.NotifyDiskPropertiesUpdated(liveDisk.DevicePath);
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

        private static void RunDiskPartScript(string script, Action<string> onOutputLine = null)
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
                using (var proc = new Process { StartInfo = psi })
                using (var outDone = new ManualResetEvent(false))
                using (var errDone = new ManualResetEvent(false))
                {
                    var stdout = new StringBuilder();
                    var stderr = new StringBuilder();
                    object sync = new object();

                    proc.OutputDataReceived += (s, e) =>
                    {
                        if (e.Data == null)
                        {
                            outDone.Set();
                            return;
                        }
                        lock (sync) { stdout.AppendLine(e.Data); }
                        string trimmed = e.Data.Trim();
                        if (trimmed.Length > 0 && onOutputLine != null)
                            onOutputLine(trimmed);
                    };
                    proc.ErrorDataReceived += (s, e) =>
                    {
                        if (e.Data == null)
                        {
                            errDone.Set();
                            return;
                        }
                        lock (sync) { stderr.AppendLine(e.Data); }
                        string trimmed = e.Data.Trim();
                        if (trimmed.Length > 0 && onOutputLine != null)
                            onOutputLine(trimmed);
                    };

                    proc.Start();
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();

                    while (!proc.WaitForExit(80))
                    {
                    }
                    outDone.WaitOne(2000);
                    errDone.WaitOne(2000);

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

        private static string MapWindowsFormatName(FileSystemType fs)
        {
            switch (fs)
            {
                case FileSystemType.NTFS:
                case FileSystemType.WindowsRecovery:
                    return "NTFS";
                case FileSystemType.FAT32:
                case FileSystemType.EfiSystem:
                    return "FAT32";
                case FileSystemType.FAT16:
                    return "FAT";
                case FileSystemType.exFAT:
                    return "EXFAT";
                case FileSystemType.ReFS:
                    return "REFS";
                default:
                    return string.Empty;
            }
        }

        private static bool FormatPhysicalWindowsVolume(
            DiskInfo disk,
            long expectedStart,
            FileSystemType fs,
            string label,
            long targetLenBytes,
            int opIndex,
            int totalOps,
            string summary,
            Action<int, string> progressCallback,
            Action<OperationProgressReport> detailedProgress)
        {
            string fsName = MapWindowsFormatName(fs);
            if (string.IsNullOrEmpty(fsName))
                return false;

            if (string.IsNullOrEmpty(MapDiskPartFs(fs, targetLenBytes)))
                return false;

            ReportProgress(progressCallback, detailedProgress, opIndex, totalOps, summary, 55,
                "Waiting for Windows volume device & mount point...",
                indeterminate: true);

            string driveLetter, volGuid;
            bool hasVol = Win32DiskInterop.WaitForPartitionVolume(disk.DiskNumber, expectedStart, 3500, out driveLetter, out volGuid);
            if (!hasVol)
            {
                try { RunDiskPartScript("rescan\r\n"); } catch { }
                hasVol = Win32DiskInterop.WaitForPartitionVolume(disk.DiskNumber, expectedStart, 2000, out driveLetter, out volGuid);
            }

            string fmifsErr = string.Empty;
            bool formatted = false;
            if (hasVol)
            {
                string[] candidates = (!string.IsNullOrEmpty(driveLetter) && !string.IsNullOrEmpty(volGuid))
                    ? new string[] { driveLetter, volGuid }
                    : (!string.IsNullOrEmpty(driveLetter) ? new string[] { driveLetter } : new string[] { volGuid });

                for (int ci = 0; ci < candidates.Length; ci++)
                {
                    string target = candidates[ci];
                    if (string.IsNullOrEmpty(target)) continue;

                    ReportProgress(progressCallback, detailedProgress, opIndex, totalOps, summary, 62,
                        string.Format("Formatting {0} via Windows fmifs.dll ({1})...", fsName, target.TrimEnd('\\')),
                        logLine: string.Format("  [fmifs.dll] FormatEx({0}, {1})...", target.TrimEnd('\\'), fsName));

                    string err;
                    if (Win32DiskInterop.TryFormatVolumeWithFmifs(
                        target,
                        fsName,
                        label,
                        pct => ReportProgress(progressCallback, detailedProgress, opIndex, totalOps, summary,
                            62 + (int)Math.Round(pct * 0.28),
                            string.Format("Formatting {0}: {1}%...", fsName, pct)),
                        line => ReportProgress(progressCallback, detailedProgress, opIndex, totalOps, summary, 76,
                            "FormatEx: " + line,
                            logLine: "  [fmifs] " + line),
                        out err))
                    {
                        formatted = true;
                        break;
                    }
                    if (!string.IsNullOrEmpty(err))
                        fmifsErr = err;
                }

                if (!formatted)
                {
                    string comTarget = !string.IsNullOrEmpty(driveLetter) ? driveLetter : volGuid;
                    ReportProgress(progressCallback, detailedProgress, opIndex, totalOps, summary, 74,
                        string.Format("Formatting {0} via Windows format.com...", fsName),
                        indeterminate: true);
                    formatted = TryFormatWithWindowsFormatCom(
                        comTarget,
                        fs,
                        label,
                        line => ReportProgress(progressCallback, detailedProgress, opIndex, totalOps, summary, 80,
                            "format.com: " + line,
                            indeterminate: true,
                            logLine: "  [format.com] " + line));
                }
            }

            if (formatted)
            {
                Win32DiskInterop.NotifyDiskPropertiesUpdated(disk.DevicePath);
                return true;
            }

            if (fs == FileSystemType.NTFS || fs == FileSystemType.WindowsRecovery || fs == FileSystemType.ReFS)
            {
                string detail = !hasVol
                    ? "Windows did not expose a mounted volume object for this partition"
                    : (!string.IsNullOrEmpty(fmifsErr) ? fmifsErr : "fmifs.dll FormatEx and format.com both failed");
                throw new InvalidOperationException(string.Format("Native Windows formatting failed for {0}: {1}", fs, detail));
            }

            return false;
        }

        private static bool TryFormatWithWindowsFormatCom(string volumeTarget, FileSystemType fs, string label, Action<string> onOutputLine = null)
        {
            if (string.IsNullOrEmpty(volumeTarget))
                return false;

            string fsName = MapWindowsFormatName(fs);
            if (string.IsNullOrEmpty(fsName))
                return false;

            string cleanTarget = volumeTarget.TrimEnd('\\');
            string cleanLabel = (label ?? string.Empty).Replace("\"", string.Empty).Trim();
            int maxLabelLen = (fs == FileSystemType.FAT32 || fs == FileSystemType.EfiSystem || fs == FileSystemType.FAT16 || fs == FileSystemType.exFAT) ? 11 : 32;
            if (cleanLabel.Length > maxLabelLen)
                cleanLabel = cleanLabel.Substring(0, maxLabelLen);

            string volSwitch = string.IsNullOrEmpty(cleanLabel) ? string.Empty : (" /V:\"" + cleanLabel + "\"");
            string args = string.Format("{0} /FS:{1} /Q /X /Y{2}",
                cleanTarget, fsName, volSwitch);

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "format.com",
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (var proc = new Process { StartInfo = psi })
                using (var outDone = new ManualResetEvent(false))
                using (var errDone = new ManualResetEvent(false))
                {
                    proc.OutputDataReceived += (s, e) =>
                    {
                        if (e.Data == null) { outDone.Set(); return; }
                        string trimmed = e.Data.Trim();
                        if (trimmed.Length > 0 && onOutputLine != null) onOutputLine(trimmed);
                    };
                    proc.ErrorDataReceived += (s, e) =>
                    {
                        if (e.Data == null) { errDone.Set(); return; }
                        string trimmed = e.Data.Trim();
                        if (trimmed.Length > 0 && onOutputLine != null) onOutputLine(trimmed);
                    };

                    proc.Start();
                    try
                    {
                        proc.StandardInput.Write("\r\nY\r\n\r\n");
                        proc.StandardInput.Flush();
                        proc.StandardInput.Close();
                    }
                    catch { }
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();
                    while (!proc.WaitForExit(80)) { }
                    outDone.WaitOne(2000);
                    errDone.WaitOne(2000);
                    return proc.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Generates the equivalent Windows <c>diskpart.exe</c> script (for live execution or user export).
        /// </summary>
        public static string BuildDiskPartScript(PendingOperation op, PartitionTableStyle tableStyle = PartitionTableStyle.GPT)
        {
            var sb = new StringBuilder();
            sb.AppendLine("select disk " + op.TargetDiskNumber);
            long targetLenBytes = op.Length > 0 ? op.Length : op.OriginalLength;
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
                    if (tableStyle == PartitionTableStyle.GPT && op.NewFileSystem == FileSystemType.EfiSystem)
                        sb.AppendLine(string.Format("create partition efi size={0}", sizeMb));
                    else if (tableStyle == PartitionTableStyle.GPT && op.NewFileSystem == FileSystemType.MicrosoftReserved)
                        sb.AppendLine(string.Format("create partition msr size={0}", sizeMb));
                    else
                        sb.AppendLine(string.Format("create partition primary size={0} offset={1}", sizeMb, offsetKb));

                    string fsArg = MapDiskPartFs(op.NewFileSystem, targetLenBytes);
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
                    if (tableStyle == PartitionTableStyle.MBR && op.NewMbrBoot)
                        sb.AppendLine("active");
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

                    string fmtFs = MapDiskPartFs(op.NewFileSystem, targetLenBytes);
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

        private static string MapDiskPartFs(FileSystemType fs, long partitionLength = 0)
        {
            switch (fs)
            {
                case FileSystemType.NTFS:
                case FileSystemType.WindowsRecovery:
                    return "ntfs";
                case FileSystemType.FAT32:
                case FileSystemType.EfiSystem:
                    return (partitionLength <= 0 || partitionLength <= 32L * 1073741824L) ? "fat32" : string.Empty;
                case FileSystemType.FAT16:
                    return (partitionLength <= 0 || partitionLength <= 2L * 1073741824L) ? "fat" : string.Empty;
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
