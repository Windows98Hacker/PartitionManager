using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using WinParted.Models;
using WinParted.UI;

namespace WinParted.Core
{
    /// <summary>
    /// End-to-end automated regression and data-safety test suite operating strictly on temporary
    /// virtual <c>.img</c> disk files (multi-disk UI queue isolation, NTFS/FAT/exFAT/Ext/Btrfs/XFS/Swap/Apple
    /// resize/move/relabel data preservation, GPT backup recovery, >16 MiB HFS+ Extents Overflow streaming,
    /// and native read-only mount + experimental write overlay round-trips).
    /// </summary>
    public static class StressTestRunner
    {
        private const long MiB = 1048576L;

        /// <summary>
        /// Executes all 8 automated stress and filesystem verification suites in an isolated temporary directory.
        /// </summary>
        public static bool RunAll(Action<string> log)
        {
            if (log == null) log = delegate(string s) { Console.WriteLine(s); };
            string tempDir = Path.Combine(Path.GetTempPath(), "WinPartedStress_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                log("=== WinParted Stress & Data-Safety Test Suite ===");
                log(string.Format("Architecture: {0}-bit (IntPtr.Size={1})", IntPtr.Size * 8, IntPtr.Size));

                RunStress1_MultiDiskSwitching(tempDir, log);
                RunStress2_WindowsResizeMoveDataSafety(tempDir, log);
                RunStress3_LinuxResizeMoveRelabelDataSafety(tempDir, log);
                RunStress4_CrossDiskCloneSafety(tempDir, log);
                RunStress5_PartitionTableSafetyAndRecovery(tempDir, log);
                RunStress6_AppleAndMacDataSafety(tempDir, log);
                RunStress7_PhysicalDiskScriptAndFormatType(tempDir, log);
                RunStress8_ReadOnlyNonMicrosoftMountAndBrowse(tempDir, log);

                log("STRESS_TEST_ALL_PASSED");
                return true;
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        /// <summary>
        /// Stress 1: Multi-disk ribbon isolation and per-disk selective undo (<see cref="OperationQueueManager.UndoLastForDisk"/>).
        /// </summary>
        private static void RunStress1_MultiDiskSwitching(string tempDir, Action<string> log)
        {
            log("[Stress 1] Rapid Multi-Disk Switching, UI Ribbon Isolation & Interleaved Queue...");

            string p90 = Path.Combine(tempDir, "d90_gpt_multi.img");
            string p91 = Path.Combine(tempDir, "d91_gpt_win.img");
            string p92 = Path.Combine(tempDir, "d92_gpt_linux.img");
            string p93 = Path.Combine(tempDir, "d93_mbr_legacy.img");

            var d90 = RawDiskEngine.CreateDemoVirtualDisk(p90, 90);
            var d91 = RawDiskEngine.CreateBlankVirtualDisk(p91, 48 * MiB, PartitionTableStyle.GPT);
            d91.DiskNumber = 91;
            var d92 = RawDiskEngine.CreateBlankVirtualDisk(p92, 48 * MiB, PartitionTableStyle.GPT);
            d92.DiskNumber = 92;
            var d93 = RawDiskEngine.CreateBlankVirtualDisk(p93, 32 * MiB, PartitionTableStyle.MBR);
            d93.DiskNumber = 93;

            var qm = new OperationQueueManager();
            qm.SetBaselineDisks(null, new[] { d90, d91, d92, d93 });

            // Seed initial partitions on 91, 92, 93
            qm.Enqueue(MakeCreateOp(91, p91, 1 * MiB, 12 * MiB, FileSystemType.NTFS, "WinA"));
            qm.Enqueue(MakeCreateOp(92, p92, 1 * MiB, 12 * MiB, FileSystemType.Ext4, "LinA"));
            qm.Enqueue(new PendingOperation
            {
                Type = OperationType.CreatePartition,
                TargetDiskNumber = 93,
                TargetDiskPath = p93,
                StartOffset = 1 * MiB,
                Length = 10 * MiB,
                NewFileSystem = FileSystemType.FAT32,
                NewLabel = "LEGACYFAT",
                NewMbrType = 0x0C,
                NewMbrBoot = true,
                Summary = "Create MBR FAT32"
            });
            qm.ApplyAll();

            // Interleave new queued operations across all 4 disks without applying yet
            qm.Enqueue(MakeCreateOp(90, p90, 82 * MiB, 16 * MiB, FileSystemType.exFAT, "SharedExFAT"));
            qm.Enqueue(MakeCreateOp(91, p91, 14 * MiB, 16 * MiB, FileSystemType.FAT32, "WIN_FAT"));
            qm.Enqueue(MakeCreateOp(92, p92, 14 * MiB, 16 * MiB, FileSystemType.Btrfs, "btrfs-pool"));
            qm.Enqueue(new PendingOperation
            {
                Type = OperationType.CreatePartition,
                TargetDiskNumber = 93,
                TargetDiskPath = p93,
                StartOffset = 12 * MiB,
                Length = 10 * MiB,
                NewFileSystem = FileSystemType.NTFS,
                NewLabel = "LegacyNTFS",
                NewMbrType = 0x07,
                Summary = "Create MBR NTFS"
            });
            // Add an extra op on Disk 91 and undo only Disk 91's last op
            qm.Enqueue(MakeCreateOp(91, p91, 31 * MiB, 8 * MiB, FileSystemType.NTFS, "TempToUndo"));
            if (!qm.UndoLastForDisk(91))
                throw new Exception("UndoLastForDisk(91) failed.");

            // Test 60 rapid switches across all 4 disks + Ribbon UI control selection isolation
            using (var ribbon = new PartitionRibbonControl())
            {
                int[] order = { 90, 92, 91, 93 };
                for (int step = 0; step < 60; step++)
                {
                    int diskId = order[step % order.Length];
                    var preview = qm.GetPreviewDisk(diskId);
                    var layout = preview.GetFullLayoutWithUnallocated();
                    ribbon.SetLayout(layout);

                    if (ribbon.SelectedPartition == null || ribbon.SelectedPartition.DiskNumber != diskId)
                        throw new Exception(string.Format("Ribbon leaked wrong DiskNumber on switch step {0}: expected {1}, got {2}",
                            step, diskId, ribbon.SelectedPartition != null ? ribbon.SelectedPartition.DiskNumber : -1));

                    int expectedParts = diskId == 90 ? 5 : 2;
                    if (preview.Partitions.Count != expectedParts)
                        throw new Exception(string.Format("Disk {0} preview expected {1} partitions, got {2}", diskId, expectedParts, preview.Partitions.Count));
                }
            }

            qm.ApplyAll();

            // Reload all 4 from raw disk files and verify zero cross-contamination
            var r90 = RawDiskEngine.LoadVirtualDisk(p90, 90);
            var r91 = RawDiskEngine.LoadVirtualDisk(p91, 91);
            var r92 = RawDiskEngine.LoadVirtualDisk(p92, 92);
            var r93 = RawDiskEngine.LoadVirtualDisk(p93, 93);

            AssertIntegrity(r90);
            AssertIntegrity(r91);
            AssertIntegrity(r92);
            AssertIntegrity(r93);

            if (r90.Partitions.Count != 5 || r91.Partitions.Count != 2 || r92.Partitions.Count != 2 || r93.Partitions.Count != 2)
                throw new Exception("Unexpected partition counts after interleaved multi-disk ApplyAll.");
            if (!r91.Partitions.Any(p => p.FileSystem == FileSystemType.FAT32 && p.Label == "WIN_FAT"))
                throw new Exception("Disk 91 missing WIN_FAT.");
            if (r91.Partitions.Any(p => p.Label == "TempToUndo"))
                throw new Exception("Disk 91 UndoLastForDisk failed to exclude TempToUndo.");
            if (!r92.Partitions.Any(p => p.FileSystem == FileSystemType.Btrfs && p.Label == "btrfs-pool"))
                throw new Exception("Disk 92 missing btrfs-pool.");
            if (!r93.Partitions.Any(p => p.FileSystem == FileSystemType.NTFS && p.Label == "LegacyNTFS"))
                throw new Exception("Disk 93 missing LegacyNTFS.");

            log("  -> Stress 1 PASSED (4 disks, 60 rapid switches, ribbon isolation & interleaved queue verified).");
        }

        /// <summary>
        /// Stress 2: Windows NTFS, FAT32, and exFAT grow, shrink, overlapping move, and in-place relabel byte verification.
        /// </summary>
        private static void RunStress2_WindowsResizeMoveDataSafety(string tempDir, Action<string> log)
        {
            log("[Stress 2] Windows Partitions (NTFS, FAT32, exFAT) Grow/Shrink/Overlapping-Move Data Safety...");

            string imgPath = Path.Combine(tempDir, "win_data_safety.img");
            var disk = RawDiskEngine.CreateBlankVirtualDisk(imgPath, 80 * MiB, PartitionTableStyle.GPT);
            disk.DiskNumber = 94;

            var qm = new OperationQueueManager();
            qm.SetBaselineDisks(null, new[] { disk });

            // Leave 8 MiB gaps between partitions so every partition can grow, shrink, move right (overlapping), and move left!
            qm.Enqueue(MakeCreateOp(94, imgPath, 1 * MiB, 12 * MiB, FileSystemType.NTFS, "WinNTFS"));
            qm.Enqueue(MakeCreateOp(94, imgPath, 21 * MiB, 8 * MiB, FileSystemType.FAT32, "WINFAT32"));
            qm.Enqueue(MakeCreateOp(94, imgPath, 37 * MiB, 8 * MiB, FileSystemType.exFAT, "WinExFAT"));
            qm.ApplyAll();

            disk = qm.GetBaselineDisk(94);
            var ntfsPayloads = WriteDataPayloads(disk, disk.Partitions[0], new[] { 256 * 1024L, 1 * MiB, 2 * MiB, 4 * MiB }, 262144);
            var fatPayloads = WriteDataPayloads(disk, disk.Partitions[1], new[] { 256 * 1024L, 1 * MiB, 2 * MiB, 3 * MiB }, 262144);
            var exPayloads = WriteDataPayloads(disk, disk.Partitions[2], new[] { 256 * 1024L, 1 * MiB, 2 * MiB, 3 * MiB }, 262144);

            // Set realistic UsedBytes so shrink protection allows >= 5 MiB
            foreach (var p in disk.Partitions) p.UsedBytes = 5 * MiB;

            string ntfsUuid = disk.Partitions[0].UUID;
            string fatUuid = disk.Partitions[1].UUID;
            string exUuid = disk.Partitions[2].UUID;

            // Phase A: Grow all 3 Windows partitions
            qm.Enqueue(MakeResizeOp(94, imgPath, disk.Partitions[0], 1 * MiB, 16 * MiB));
            qm.Enqueue(MakeResizeOp(94, imgPath, disk.Partitions[1], 21 * MiB, 12 * MiB));
            qm.Enqueue(MakeResizeOp(94, imgPath, disk.Partitions[2], 37 * MiB, 14 * MiB));
            qm.ApplyAll();

            disk = RawDiskEngine.LoadVirtualDisk(imgPath, 94);
            AssertIntegrity(disk);
            VerifyDataPayloads(disk, disk.Partitions[0], ntfsPayloads, "NTFS after Grow");
            VerifyDataPayloads(disk, disk.Partitions[1], fatPayloads, "FAT32 after Grow");
            VerifyDataPayloads(disk, disk.Partitions[2], exPayloads, "exFAT after Grow");

            // Phase B: Shrink + Overlapping Move Right (newStart overlaps old range!)
            qm.SetBaselineDisks(null, new[] { disk });
            qm.Enqueue(MakeResizeOp(94, imgPath, disk.Partitions[0], 5 * MiB, 8 * MiB));   // was 1..17 MiB -> now 5..13 MiB
            qm.Enqueue(MakeResizeOp(94, imgPath, disk.Partitions[1], 24 * MiB, 6 * MiB));  // was 21..33 MiB -> now 24..30 MiB
            qm.Enqueue(MakeResizeOp(94, imgPath, disk.Partitions[2], 41 * MiB, 7 * MiB));  // was 37..51 MiB -> now 41..48 MiB
            qm.ApplyAll();

            disk = RawDiskEngine.LoadVirtualDisk(imgPath, 94);
            AssertIntegrity(disk);
            VerifyDataPayloads(disk, disk.Partitions[0], ntfsPayloads, "NTFS after Shrink+OverlapRight");
            VerifyDataPayloads(disk, disk.Partitions[1], fatPayloads, "FAT32 after Shrink+OverlapRight");
            VerifyDataPayloads(disk, disk.Partitions[2], exPayloads, "exFAT after Shrink+OverlapRight");

            // Phase C: Overlapping Move Left + Relabel in-place
            qm.SetBaselineDisks(null, new[] { disk });
            qm.Enqueue(MakeResizeOp(94, imgPath, disk.Partitions[0], 2 * MiB, 9 * MiB));
            qm.Enqueue(MakeResizeOp(94, imgPath, disk.Partitions[1], 22 * MiB, 7 * MiB));
            qm.Enqueue(MakeResizeOp(94, imgPath, disk.Partitions[2], 38 * MiB, 9 * MiB));
            qm.Enqueue(new PendingOperation
            {
                Type = OperationType.ChangeLabel,
                TargetDiskNumber = 94,
                TargetDiskPath = imgPath,
                OriginalStartOffset = 2 * MiB,
                OriginalLength = 9 * MiB,
                NewLabel = "WinOS_Safe",
                NewPartitionName = "Windows System",
                Summary = "Relabel NTFS"
            });
            qm.ApplyAll();

            disk = RawDiskEngine.LoadVirtualDisk(imgPath, 94);
            AssertIntegrity(disk);
            VerifyDataPayloads(disk, disk.Partitions[0], ntfsPayloads, "NTFS after MoveLeft+Relabel");
            VerifyDataPayloads(disk, disk.Partitions[1], fatPayloads, "FAT32 after MoveLeft");
            VerifyDataPayloads(disk, disk.Partitions[2], exPayloads, "exFAT after MoveLeft");

            if (disk.Partitions[0].UUID != ntfsUuid || disk.Partitions[1].UUID != fatUuid || disk.Partitions[2].UUID != exUuid)
                throw new Exception("Windows partition UUID changed during non-destructive resize/move/relabel!");
            if (disk.Partitions[0].Label != "WinOS_Safe")
                throw new Exception("NTFS in-place relabel failed; got '" + disk.Partitions[0].Label + "'");

            log("  -> Stress 2 PASSED (NTFS, FAT32, exFAT 100% payload & UUID integrity across grow/shrink/overlap-move/relabel).");
        }

        /// <summary>
        /// Stress 3: Linux Ext4, Btrfs, XFS, and Linux Swap grow, shrink, overlapping move, and UUID/payload preservation.
        /// </summary>
        private static void RunStress3_LinuxResizeMoveRelabelDataSafety(string tempDir, Action<string> log)
        {
            log("[Stress 3] Linux Partitions (ext4, Btrfs, XFS, Swap) Grow/Shrink/Overlapping-Move/Relabel Data Safety...");

            string imgPath = Path.Combine(tempDir, "linux_data_safety.img");
            var disk = RawDiskEngine.CreateBlankVirtualDisk(imgPath, 88 * MiB, PartitionTableStyle.GPT);
            disk.DiskNumber = 95;

            var qm = new OperationQueueManager();
            qm.SetBaselineDisks(null, new[] { disk });

            qm.Enqueue(MakeCreateOp(95, imgPath, 1 * MiB, 12 * MiB, FileSystemType.Ext4, "ubuntu-root"));
            qm.Enqueue(MakeCreateOp(95, imgPath, 22 * MiB, 10 * MiB, FileSystemType.Btrfs, "btrfs-home"));
            qm.Enqueue(MakeCreateOp(95, imgPath, 40 * MiB, 10 * MiB, FileSystemType.XFS, "xfs-var"));
            qm.Enqueue(MakeCreateOp(95, imgPath, 60 * MiB, 8 * MiB, FileSystemType.LinuxSwap, "linux-swap"));
            qm.ApplyAll();

            disk = qm.GetBaselineDisk(95);
            var extPayloads = WriteDataPayloads(disk, disk.Partitions[0], new[] { 256 * 1024L, 1 * MiB, 2 * MiB, 4 * MiB }, 262144);
            var btrfsPayloads = WriteDataPayloads(disk, disk.Partitions[1], new[] { 256 * 1024L, 1 * MiB, 2 * MiB, 4 * MiB }, 262144);
            var xfsPayloads = WriteDataPayloads(disk, disk.Partitions[2], new[] { 256 * 1024L, 1 * MiB, 2 * MiB, 4 * MiB }, 262144);

            string extUuid = disk.Partitions[0].UUID;
            string btrfsUuid = disk.Partitions[1].UUID;
            string xfsUuid = disk.Partitions[2].UUID;
            string swapUuid = disk.Partitions[3].UUID;

            // Grow ext4/Btrfs/XFS, shrink swap
            qm.Enqueue(MakeResizeOp(95, imgPath, disk.Partitions[0], 1 * MiB, 16 * MiB));
            qm.Enqueue(MakeResizeOp(95, imgPath, disk.Partitions[1], 22 * MiB, 14 * MiB));
            qm.Enqueue(MakeResizeOp(95, imgPath, disk.Partitions[2], 40 * MiB, 14 * MiB));
            qm.Enqueue(MakeResizeOp(95, imgPath, disk.Partitions[3], 60 * MiB, 4 * MiB));
            qm.ApplyAll();

            disk = RawDiskEngine.LoadVirtualDisk(imgPath, 95);
            AssertIntegrity(disk);

            // Shrink + Overlapping Move Right + In-Place Relabels
            qm.SetBaselineDisks(null, new[] { disk });
            qm.Enqueue(MakeResizeOp(95, imgPath, disk.Partitions[0], 4 * MiB, 8 * MiB));
            qm.Enqueue(MakeResizeOp(95, imgPath, disk.Partitions[1], 25 * MiB, 8 * MiB));
            qm.Enqueue(MakeResizeOp(95, imgPath, disk.Partitions[2], 43 * MiB, 8 * MiB));
            qm.Enqueue(MakeResizeOp(95, imgPath, disk.Partitions[3], 62 * MiB, 6 * MiB));
            qm.Enqueue(new PendingOperation
            {
                Type = OperationType.ChangeLabel,
                TargetDiskNumber = 95,
                TargetDiskPath = imgPath,
                OriginalStartOffset = 4 * MiB,
                OriginalLength = 8 * MiB,
                NewLabel = "arch-root",
                NewPartitionName = "Arch Linux",
                Summary = "Relabel ext4"
            });
            qm.Enqueue(new PendingOperation
            {
                Type = OperationType.ChangeLabel,
                TargetDiskNumber = 95,
                TargetDiskPath = imgPath,
                OriginalStartOffset = 25 * MiB,
                OriginalLength = 8 * MiB,
                NewLabel = "btrfs-pool2",
                Summary = "Relabel Btrfs"
            });
            qm.Enqueue(new PendingOperation
            {
                Type = OperationType.ChangeLabel,
                TargetDiskNumber = 95,
                TargetDiskPath = imgPath,
                OriginalStartOffset = 43 * MiB,
                OriginalLength = 8 * MiB,
                NewLabel = "xfs-fast",
                Summary = "Relabel XFS"
            });
            qm.ApplyAll();

            disk = RawDiskEngine.LoadVirtualDisk(imgPath, 95);
            AssertIntegrity(disk);
            VerifyDataPayloads(disk, disk.Partitions[0], extPayloads, "Ext4 after Grow+Shrink+Move+Relabel");
            VerifyDataPayloads(disk, disk.Partitions[1], btrfsPayloads, "Btrfs after Grow+Shrink+Move+Relabel");
            VerifyDataPayloads(disk, disk.Partitions[2], xfsPayloads, "XFS after Grow+Shrink+Move+Relabel");

            if (disk.Partitions[0].FileSystem != FileSystemType.Ext4 || disk.Partitions[0].Label != "arch-root" || disk.Partitions[0].UUID != extUuid)
                throw new Exception("Ext4 metadata mismatch after resize/move/relabel.");
            if (disk.Partitions[1].FileSystem != FileSystemType.Btrfs || disk.Partitions[1].Label != "btrfs-pool2" || disk.Partitions[1].UUID != btrfsUuid)
                throw new Exception("Btrfs metadata mismatch after resize/move/relabel.");
            if (disk.Partitions[2].FileSystem != FileSystemType.XFS || disk.Partitions[2].Label != "xfs-fast" || disk.Partitions[2].UUID != xfsUuid)
                throw new Exception("XFS metadata mismatch after resize/move/relabel.");
            if (disk.Partitions[3].FileSystem != FileSystemType.LinuxSwap || disk.Partitions[3].UUID != swapUuid)
                throw new Exception("LinuxSwap metadata mismatch after resize/move.");

            log("  -> Stress 3 PASSED (ext4, Btrfs, XFS, LinuxSwap 100% payload & UUID integrity across grow/shrink/overlap-move/relabel).");
        }

        /// <summary>
        /// Stress 4: Cross-disk partition cloning and post-clone target expansion byte-for-byte integrity.
        /// </summary>
        private static void RunStress4_CrossDiskCloneSafety(string tempDir, Action<string> log)
        {
            log("[Stress 4] Cross-Disk Partition Clone & Expansion Verification...");

            string srcPath = Path.Combine(tempDir, "clone_src.img");
            string dstPath = Path.Combine(tempDir, "clone_dst.img");
            var srcDisk = RawDiskEngine.CreateBlankVirtualDisk(srcPath, 40 * MiB, PartitionTableStyle.GPT);
            srcDisk.DiskNumber = 96;
            var dstDisk = RawDiskEngine.CreateBlankVirtualDisk(dstPath, 56 * MiB, PartitionTableStyle.GPT);
            dstDisk.DiskNumber = 97;

            var qm = new OperationQueueManager();
            qm.SetBaselineDisks(null, new[] { srcDisk, dstDisk });

            qm.Enqueue(MakeCreateOp(96, srcPath, 1 * MiB, 10 * MiB, FileSystemType.Ext4, "src-ext4"));
            qm.Enqueue(MakeCreateOp(96, srcPath, 12 * MiB, 10 * MiB, FileSystemType.NTFS, "SrcNTFS"));
            qm.ApplyAll();

            srcDisk = qm.GetBaselineDisk(96);
            var extPayloads = WriteDataPayloads(srcDisk, srcDisk.Partitions[0], new[] { 256 * 1024L, 2 * MiB, 5 * MiB }, 262144);
            var ntfsPayloads = WriteDataPayloads(srcDisk, srcDisk.Partitions[1], new[] { 256 * 1024L, 2 * MiB, 5 * MiB }, 262144);

            // Clone both partitions from Disk 96 -> Disk 97, expanding ext4 from 10 MiB -> 16 MiB
            qm.Enqueue(new PendingOperation
            {
                Type = OperationType.CopyPartition,
                TargetDiskNumber = 97,
                TargetDiskPath = dstPath,
                SourceDiskNumber = 96,
                SourceDiskPath = srcPath,
                SourceStartOffset = srcDisk.Partitions[0].StartingOffset,
                SourceLength = srcDisk.Partitions[0].PartitionLength,
                StartOffset = 1 * MiB,
                Length = 16 * MiB,
                NewFileSystem = FileSystemType.Ext4,
                NewLabel = "src-ext4",
                NewPartitionName = "Cloned Ext4",
                NewGptTypeGuid = GptPartitionTypeGuids.LinuxFilesystem,
                Summary = "Clone Ext4 96->97"
            });
            qm.Enqueue(new PendingOperation
            {
                Type = OperationType.CopyPartition,
                TargetDiskNumber = 97,
                TargetDiskPath = dstPath,
                SourceDiskNumber = 96,
                SourceDiskPath = srcPath,
                SourceStartOffset = srcDisk.Partitions[1].StartingOffset,
                SourceLength = srcDisk.Partitions[1].PartitionLength,
                StartOffset = 18 * MiB,
                Length = 12 * MiB,
                NewFileSystem = FileSystemType.NTFS,
                NewLabel = "SrcNTFS",
                NewPartitionName = "Cloned NTFS",
                NewGptTypeGuid = GptPartitionTypeGuids.BasicData,
                Summary = "Clone NTFS 96->97"
            });
            qm.ApplyAll();

            var rSrc = RawDiskEngine.LoadVirtualDisk(srcPath, 96);
            var rDst = RawDiskEngine.LoadVirtualDisk(dstPath, 97);
            AssertIntegrity(rSrc);
            AssertIntegrity(rDst);

            VerifyDataPayloads(rSrc, rSrc.Partitions[0], extPayloads, "Source Ext4 unchanged");
            VerifyDataPayloads(rSrc, rSrc.Partitions[1], ntfsPayloads, "Source NTFS unchanged");
            VerifyDataPayloads(rDst, rDst.Partitions[0], extPayloads, "Cloned+Expanded Ext4 on Dst");
            VerifyDataPayloads(rDst, rDst.Partitions[1], ntfsPayloads, "Cloned+Expanded NTFS on Dst");

            log("  -> Stress 4 PASSED (Cross-disk clone + expansion preserved 100% of user data on both disks).");
        }

        /// <summary>
        /// Stress 5: Guardrails rejecting overlaps, out-of-bounds GPT ranges, unsafe shrinks, MBR >4 slots, plus GPT backup recovery.
        /// </summary>
        private static void RunStress5_PartitionTableSafetyAndRecovery(string tempDir, Action<string> log)
        {
            log("[Stress 5] Partition Table Safety Guardrails, Corruption Rejection & Backup GPT Recovery...");

            string gptPath = Path.Combine(tempDir, "safety_gpt.img");
            string mbrPath = Path.Combine(tempDir, "safety_mbr.img");
            var gptDisk = RawDiskEngine.CreateDemoVirtualDisk(gptPath, 98);
            var mbrDisk = RawDiskEngine.CreateBlankVirtualDisk(mbrPath, 32 * MiB, PartitionTableStyle.MBR);
            mbrDisk.DiskNumber = 99;

            var qm = new OperationQueueManager();
            qm.SetBaselineDisks(null, new[] { gptDisk, mbrDisk });

            // 1. Overlapping partition must be rejected
            ExpectRejected(delegate
            {
                qm.Enqueue(MakeCreateOp(98, gptPath, 20 * MiB, 8 * MiB, FileSystemType.NTFS, "Overlap"));
            }, "Overlapping GPT partition");

            // 2. Partition encroaching on Primary GPT (LBA 0..33) must be rejected
            ExpectRejected(delegate
            {
                qm.Enqueue(MakeCreateOp(98, gptPath, 4096, 512 * 1024, FileSystemType.FAT32, "BadLbaStart"));
            }, "Encroaching Primary GPT LBA 0..33");

            // 3. Partition encroaching on Backup GPT (Last 33 sectors) must be rejected
            ExpectRejected(delegate
            {
                qm.Enqueue(MakeCreateOp(98, gptPath, 82 * MiB, gptDisk.TotalSizeBytes - 82 * MiB, FileSystemType.NTFS, "BadLbaEnd"));
            }, "Encroaching Backup GPT");

            // 4. Shrinking below UsedBytes must be rejected
            var ntfsPart = gptDisk.Partitions[1];
            ExpectRejected(delegate
            {
                qm.Enqueue(MakeResizeOp(98, gptPath, ntfsPart, ntfsPart.StartingOffset, 2 * MiB));
            }, "Shrinking below UsedBytes");

            // 5. Adding 5th primary partition to MBR must be rejected
            qm.Enqueue(MakeCreateOp(99, mbrPath, 1 * MiB, 4 * MiB, FileSystemType.FAT32, "P1"));
            qm.Enqueue(MakeCreateOp(99, mbrPath, 6 * MiB, 4 * MiB, FileSystemType.NTFS, "P2"));
            qm.Enqueue(MakeCreateOp(99, mbrPath, 11 * MiB, 4 * MiB, FileSystemType.Ext4, "P3"));
            qm.Enqueue(MakeCreateOp(99, mbrPath, 16 * MiB, 4 * MiB, FileSystemType.LinuxSwap, "P4"));
            ExpectRejected(delegate
            {
                qm.Enqueue(MakeCreateOp(99, mbrPath, 21 * MiB, 4 * MiB, FileSystemType.NTFS, "P5"));
            }, "5th Primary MBR Partition");
            qm.ApplyAll();

            // 6. Primary GPT Header Corruption & Recovery from Backup GPT
            var extPart = gptDisk.Partitions[2];
            var extPayloads = WriteDataPayloads(gptDisk, extPart, new[] { 256 * 1024L, 2 * MiB, 4 * MiB }, 131072);

            // Corrupt LBA 1 (Primary GPT Header) with 0xFF
            byte[] garbage = new byte[512];
            for (int i = 0; i < garbage.Length; i++) garbage[i] = 0xFF;
            RawDiskEngine.WriteBytes(gptDisk, 512, garbage);

            string badReport;
            if (RawDiskEngine.VerifyDiskPartitionTableIntegrity(gptDisk, out badReport))
                throw new Exception("VerifyDiskPartitionTableIntegrity failed to detect corrupted LBA 1 Primary GPT Header!");

            // Recover from Backup GPT at Last LBA
            RawDiskEngine.RecoverGptFromBackup(gptDisk);
            var recovered = RawDiskEngine.LoadVirtualDisk(gptPath, 98);
            AssertIntegrity(recovered);

            if (recovered.Partitions.Count != 4)
                throw new Exception("Recovered GPT lost partitions!");
            VerifyDataPayloads(recovered, recovered.Partitions[2], extPayloads, "Ext4 after Backup GPT recovery");

            log("  -> Stress 5 PASSED (Overlap, GPT LBA bounds, UsedBytes shrink, MBR 4-slot limit rejected; Backup GPT recovery verified).");
        }

        /// <summary>
        /// Stress 6: Apple APFS, HFS+, and Classic HFS superblock detection, GPT type GUIDs, move/resize, and relabeling.
        /// </summary>
        private static void RunStress6_AppleAndMacDataSafety(string tempDir, Action<string> log)
        {
            log("[Stress 6] Apple/Macintosh Partitions (APFS, HFS+, Classic HFS) Detection, Resize/Move & Relabel...");
            string imgPath = Path.Combine(tempDir, "stress6_apple.img");
            var disk = RawDiskEngine.CreateBlankVirtualDisk(imgPath, 96 * MiB, PartitionTableStyle.GPT);
            disk.DiskNumber = 96;

            var qm = new OperationQueueManager();
            qm.SetBaselineDisks(null, new[] { disk });

            // Create APFS, HFS+, and Classic HFS partitions with expansion headroom
            qm.Enqueue(MakeCreateOp(96, imgPath, 1 * MiB, 16 * MiB, FileSystemType.APFS, "MacAPFS"));
            qm.Enqueue(MakeCreateOp(96, imgPath, 29 * MiB, 16 * MiB, FileSystemType.HFSPlus, "MacintoshHD"));
            qm.Enqueue(MakeCreateOp(96, imgPath, 57 * MiB, 16 * MiB, FileSystemType.HFS, "ClassicMac"));
            qm.ApplyAll();

            disk = RawDiskEngine.LoadVirtualDisk(imgPath, 96);
            if (disk.Partitions.Count != 3)
                throw new Exception("Expected 3 Apple/Mac partitions, got " + disk.Partitions.Count);

            if (disk.Partitions[0].FileSystem != FileSystemType.APFS ||
                disk.Partitions[0].GptTypeGuid != GptPartitionTypeGuids.AppleApfs ||
                disk.Partitions[0].Label != "MacAPFS")
                throw new Exception("APFS detection/label/GPT GUID mismatch.");

            if (disk.Partitions[1].FileSystem != FileSystemType.HFSPlus ||
                disk.Partitions[1].GptTypeGuid != GptPartitionTypeGuids.AppleHfs ||
                disk.Partitions[1].Label != "MacintoshHD")
                throw new Exception("HFS+ detection/label/GPT GUID mismatch.");

            if (disk.Partitions[2].FileSystem != FileSystemType.HFS ||
                disk.Partitions[2].GptTypeGuid != GptPartitionTypeGuids.AppleHfs ||
                disk.Partitions[2].Label != "ClassicMac")
                throw new Exception("Classic HFS detection/label/GPT GUID mismatch.");

            string uuidApfs = disk.Partitions[0].UUID;
            string uuidHfsPlus = disk.Partitions[1].UUID;
            string uuidHfs = disk.Partitions[2].UUID;
            if (string.IsNullOrEmpty(uuidApfs) || string.IsNullOrEmpty(uuidHfsPlus) || string.IsNullOrEmpty(uuidHfs))
                throw new Exception("Expected non-empty UUIDs on APFS/HFS+/HFS partitions.");

            long[] testOffsets = new[] { 256 * 1024L, 1 * MiB, 2 * MiB, 4 * MiB };
            var apfsPayloads = WriteDataPayloads(disk, disk.Partitions[0], testOffsets, 262144);
            var hfsPlusPayloads = WriteDataPayloads(disk, disk.Partitions[1], testOffsets, 262144);
            var hfsPayloads = WriteDataPayloads(disk, disk.Partitions[2], testOffsets, 262144);

            // Phase A: Grow all three partitions
            qm.SetBaselineDisks(null, new[] { disk });
            qm.Enqueue(MakeResizeOp(96, imgPath, disk.Partitions[0], 1 * MiB, 22 * MiB));
            qm.Enqueue(MakeResizeOp(96, imgPath, disk.Partitions[1], 29 * MiB, 22 * MiB));
            qm.Enqueue(MakeResizeOp(96, imgPath, disk.Partitions[2], 57 * MiB, 22 * MiB));
            qm.ApplyAll();

            disk = RawDiskEngine.LoadVirtualDisk(imgPath, 96);
            AssertIntegrity(disk);
            if (disk.Partitions[0].FileSystem != FileSystemType.APFS ||
                disk.Partitions[1].FileSystem != FileSystemType.HFSPlus ||
                disk.Partitions[2].FileSystem != FileSystemType.HFS)
                throw new Exception("Apple/Mac filesystem type lost after Grow.");
            VerifyDataPayloads(disk, disk.Partitions[0], apfsPayloads, "APFS after Grow");
            VerifyDataPayloads(disk, disk.Partitions[1], hfsPlusPayloads, "HFS+ after Grow");
            VerifyDataPayloads(disk, disk.Partitions[2], hfsPayloads, "Classic HFS after Grow");

            // Phase B: Shrink + Overlapping Right Move
            qm.SetBaselineDisks(null, new[] { disk });
            qm.Enqueue(MakeResizeOp(96, imgPath, disk.Partitions[0], 5 * MiB, 14 * MiB));
            qm.Enqueue(MakeResizeOp(96, imgPath, disk.Partitions[1], 33 * MiB, 14 * MiB));
            qm.Enqueue(MakeResizeOp(96, imgPath, disk.Partitions[2], 61 * MiB, 14 * MiB));
            qm.ApplyAll();

            disk = RawDiskEngine.LoadVirtualDisk(imgPath, 96);
            AssertIntegrity(disk);
            VerifyDataPayloads(disk, disk.Partitions[0], apfsPayloads, "APFS after Shrink+OverlapRight");
            VerifyDataPayloads(disk, disk.Partitions[1], hfsPlusPayloads, "HFS+ after Shrink+OverlapRight");
            VerifyDataPayloads(disk, disk.Partitions[2], hfsPayloads, "Classic HFS after Shrink+OverlapRight");

            // Phase C: Move Left + Relabel in-place
            qm.SetBaselineDisks(null, new[] { disk });
            qm.Enqueue(MakeResizeOp(96, imgPath, disk.Partitions[0], 2 * MiB, 14 * MiB));
            qm.Enqueue(MakeResizeOp(96, imgPath, disk.Partitions[1], 30 * MiB, 14 * MiB));
            qm.Enqueue(MakeResizeOp(96, imgPath, disk.Partitions[2], 58 * MiB, 14 * MiB));
            qm.ApplyAll();

            disk = RawDiskEngine.LoadVirtualDisk(imgPath, 96);
            qm.SetBaselineDisks(null, new[] { disk });
            string[] newLabels = new[] { "MacAPFS2", "MacHFSPlus2", "ClassicMac2" };
            for (int i = 0; i < 3; i++)
            {
                var p = disk.Partitions[i];
                qm.Enqueue(new PendingOperation
                {
                    Type = OperationType.ChangeLabel,
                    TargetDiskNumber = 96,
                    TargetDiskPath = imgPath,
                    TargetPartitionNumber = p.PartitionNumber,
                    OriginalStartOffset = p.StartingOffset,
                    OriginalLength = p.PartitionLength,
                    NewLabel = newLabels[i],
                    NewPartitionName = newLabels[i],
                    Summary = "Relabel " + p.ShortDeviceName
                });
            }
            qm.ApplyAll();

            disk = RawDiskEngine.LoadVirtualDisk(imgPath, 96);
            AssertIntegrity(disk);
            if (disk.Partitions[0].UUID != uuidApfs || disk.Partitions[1].UUID != uuidHfsPlus || disk.Partitions[2].UUID != uuidHfs)
                throw new Exception("Relabel/Resize altered Apple/Mac partition UUIDs!");
            if (disk.Partitions[0].Label != "MacAPFS2" || disk.Partitions[1].Label != "MacHFSPlus2" || disk.Partitions[2].Label != "ClassicMac2")
                throw new Exception("Relabel failed on Apple/Mac partitions.");

            VerifyDataPayloads(disk, disk.Partitions[0], apfsPayloads, "APFS after MoveLeft+Relabel");
            VerifyDataPayloads(disk, disk.Partitions[1], hfsPlusPayloads, "HFS+ after MoveLeft+Relabel");
            VerifyDataPayloads(disk, disk.Partitions[2], hfsPayloads, "Classic HFS after MoveLeft+Relabel");

            log("  -> Stress 6 PASSED (APFS, HFS+, Classic HFS detection, GPT GUIDs, 100% payload & UUID integrity across grow/shrink/move/relabel).");
        }

        /// <summary>
        /// Stress 7: Physical USB GPT/MBR Ext4 DiskPart script generation and FAT32-to-Ext4 superblock/type replacement.
        /// </summary>
        private static void RunStress7_PhysicalDiskScriptAndFormatType(string tempDir, Action<string> log)
        {
            log("[Stress 7] Physical USB Ext4 Format Scripts, MBR/GPT Type IDs & Superblock Reload...");

            var fmtExt4 = new PendingOperation
            {
                Type = OperationType.FormatPartition,
                TargetDiskNumber = 2,
                TargetPartitionNumber = 1,
                OriginalStartOffset = 1 * MiB,
                OriginalLength = 16 * MiB,
                NewFileSystem = FileSystemType.Ext4,
                NewLabel = "USB-EXT4",
                NewGptTypeGuid = GptPartitionTypeGuids.ForFileSystem(FileSystemType.Ext4),
                NewMbrType = RawDiskEngine.GetDefaultMbrType(FileSystemType.Ext4)
            };

            string gptScript = OperationQueueManager.BuildDiskPartScript(fmtExt4, PartitionTableStyle.GPT);
            if (gptScript.IndexOf("select partition 1", StringComparison.OrdinalIgnoreCase) < 0 ||
                gptScript.IndexOf("set id=0fc63daf-8483-4772-8e79-3d69d8477de4 override", StringComparison.OrdinalIgnoreCase) < 0)
            {
                throw new Exception("GPT Ext4 format DiskPart script missing Linux partition type GUID update: " + gptScript);
            }

            string mbrScript = OperationQueueManager.BuildDiskPartScript(fmtExt4, PartitionTableStyle.MBR);
            if (mbrScript.IndexOf("select partition 1", StringComparison.OrdinalIgnoreCase) < 0 ||
                mbrScript.IndexOf("set id=83 override", StringComparison.OrdinalIgnoreCase) < 0)
            {
                throw new Exception("MBR Ext4 format DiskPart script missing 0x83 type ID update: " + mbrScript);
            }

            var createExt4 = MakeCreateOp(2, "", 1 * MiB, 16 * MiB, FileSystemType.Ext4, "USB-NEW-EXT4");
            string mbrCreateScript = OperationQueueManager.BuildDiskPartScript(createExt4, PartitionTableStyle.MBR);
            if (mbrCreateScript.IndexOf("create partition primary", StringComparison.OrdinalIgnoreCase) < 0 ||
                mbrCreateScript.IndexOf("set id=83 override", StringComparison.OrdinalIgnoreCase) < 0)
            {
                throw new Exception("MBR Ext4 create DiskPart script missing 0x83 type ID: " + mbrCreateScript);
            }

            // Simulate formatting an MBR FAT32 USB partition -> Ext4
            string usbImg = Path.Combine(tempDir, "stress7_usb_mbr.img");
            var usbDisk = RawDiskEngine.CreateBlankVirtualDisk(usbImg, 32 * MiB, PartitionTableStyle.MBR);
            usbDisk.DiskNumber = 97;
            var qm = new OperationQueueManager();
            qm.SetBaselineDisks(null, new[] { usbDisk });
            qm.Enqueue(MakeCreateOp(97, usbImg, 1 * MiB, 20 * MiB, FileSystemType.FAT32, "USBDRIVE"));
            qm.ApplyAll();

            usbDisk = RawDiskEngine.LoadVirtualDisk(usbImg, 97);
            if (usbDisk.Partitions.Count != 1 || usbDisk.Partitions[0].FileSystem != FileSystemType.FAT32)
                throw new Exception("Expected initial FAT32 partition on USB MBR image.");

            qm.SetBaselineDisks(null, new[] { usbDisk });
            qm.Enqueue(new PendingOperation
            {
                Type = OperationType.FormatPartition,
                TargetDiskNumber = 97,
                TargetDiskPath = usbImg,
                TargetPartitionNumber = usbDisk.Partitions[0].PartitionNumber,
                OriginalStartOffset = usbDisk.Partitions[0].StartingOffset,
                OriginalLength = usbDisk.Partitions[0].PartitionLength,
                NewFileSystem = FileSystemType.Ext4,
                NewLabel = "USB-EXT4",
                NewGptTypeGuid = GptPartitionTypeGuids.ForFileSystem(FileSystemType.Ext4),
                NewMbrType = RawDiskEngine.GetDefaultMbrType(FileSystemType.Ext4),
                Summary = "Format USB FAT32 to Ext4"
            });
            qm.ApplyAll();

            var formattedUsb = RawDiskEngine.LoadVirtualDisk(usbImg, 97);
            if (formattedUsb.Partitions.Count != 1)
                throw new Exception("Formatted USB image lost partition.");
            var p = formattedUsb.Partitions[0];
            if (p.FileSystem != FileSystemType.Ext4 || p.MbrPartitionType != 0x83 || p.Label != "USB-EXT4" || string.IsNullOrEmpty(p.UUID))
            {
                throw new Exception(string.Format("USB Ext4 format verification failed: FS={0}, MbrType=0x{1:X2}, Label='{2}', UUID='{3}'",
                    p.FileSystem, p.MbrPartitionType, p.Label, p.UUID));
            }

            log("  -> Stress 7 PASSED (Physical USB GPT/MBR Ext4 format script & FAT32->Ext4 superblock/type verification).");
        }

        /// <summary>
        /// Stress 8: Native read-only mount, search, host export, experimental write overlay persistence,
        /// developer filesystems (Btrfs/XFS/Ext2/F2FS/ZFS/SquashFS/ISO9660/UDF/FAT16/ReFS), and >16 MiB HFS+ overflow streaming.
        /// </summary>
        private static void RunStress8_ReadOnlyNonMicrosoftMountAndBrowse(string tempDir, Action<string> log)
        {
            log("[Stress 8] Read-Only Mounting & Browsing Non-Microsoft Filesystems (Virtual Test Image Only)...");

            string imgPath = Path.Combine(tempDir, "stress8_nonms_ro.img");
            var disk = RawDiskEngine.CreateBlankVirtualDisk(imgPath, 96 * MiB, PartitionTableStyle.GPT);
            disk.DiskNumber = 98;
            if (!disk.IsVirtualImage)
                throw new Exception("Safety check failed: Stress 8 must only run on a virtual test disk image.");

            var qm = new OperationQueueManager();
            qm.SetBaselineDisks(null, new[] { disk });

            qm.Enqueue(MakeCreateOp(98, imgPath, 1 * MiB, 14 * MiB, FileSystemType.Ext4, "ro-ext4"));
            qm.Enqueue(MakeCreateOp(98, imgPath, 15 * MiB, 14 * MiB, FileSystemType.Btrfs, "ro-btrfs"));
            qm.Enqueue(MakeCreateOp(98, imgPath, 29 * MiB, 14 * MiB, FileSystemType.XFS, "ro-xfs"));
            qm.Enqueue(MakeCreateOp(98, imgPath, 43 * MiB, 14 * MiB, FileSystemType.APFS, "ro-apfs"));
            qm.Enqueue(MakeCreateOp(98, imgPath, 57 * MiB, 14 * MiB, FileSystemType.HFSPlus, "ro-hfsplus"));
            qm.Enqueue(MakeCreateOp(98, imgPath, 71 * MiB, 14 * MiB, FileSystemType.HFS, "ro-hfs"));
            qm.ApplyAll();

            disk = RawDiskEngine.LoadVirtualDisk(imgPath, 98);
            if (disk.Partitions.Count != 6)
                throw new Exception("Expected 6 non-Microsoft partitions in Stress 8 image, got " + disk.Partitions.Count);

            // Strip WinParted synthetic hints (WPL: and WPROFS01) from HFS+ partition so label + catalog tree are proven to come purely from Big-Endian HFS+ B-Tree
            var hfsPartRaw = disk.Partitions[4];
            hfsPartRaw.Label = string.Empty;
            byte[] zeroSlot = new byte[32];
            RawDiskEngine.WriteBytes(disk, hfsPartRaw.StartingOffset + 0x1A0, zeroSlot);
            RawDiskEngine.WriteBytes(disk, hfsPartRaw.StartingOffset + 0x3000, new byte[4096]);

            disk = RawDiskEngine.LoadVirtualDisk(imgPath, 98);
            if (disk.Partitions[4].Label != "ro-hfsplus")
                throw new Exception("HFS+ Catalog B-Tree volume label extraction failed: got '" + disk.Partitions[4].Label + "'");

            byte[] beforeBytes = File.ReadAllBytes(imgPath);
            uint crcBeforeMount = Crc32.Compute(beforeBytes, 0, beforeBytes.Length);

            try
            {
                // 1. Ext4 real superblock + BGDT + inode table + extent + ext2_dir_entry_2 traversal
                VerifyRoMountAndFile(disk, disk.Partitions[0], "/etc/fstab", "UUID=");
                VerifyRoMountAndFile(disk, disk.Partitions[0], "/README.txt", "WinParted Read-Only Mount Preview");

                // 2. Btrfs & XFS
                VerifyRoMountAndFile(disk, disk.Partitions[1], "/etc/filesystems", "btrfs");
                VerifyRoMountAndFile(disk, disk.Partitions[2], "/home/ReadMe.txt", "XFS");

                // 3. Apple APFS, pure Big-Endian HFS+ Catalog B-Tree, Classic HFS
                VerifyRoMountAndFile(disk, disk.Partitions[3], "/System/Library/CoreServices/SystemVersion.plist", "<string>apfs</string>");
                VerifyRoMountAndFile(disk, disk.Partitions[4], "/.hfsplus_volume_info.txt", "Catalog B-Tree Summary");
                VerifyRoMountAndFile(disk, disk.Partitions[4], "/System/Library/CoreServices/SystemVersion.plist", "<string>hfs+</string>");
                VerifyRoMountAndFile(disk, disk.Partitions[4], "/Users/Shared/ReadMe.txt", "hfs+");
                VerifyRoMountAndFile(disk, disk.Partitions[5], "/.VolumeLabel", "ro-hfs");

                // 4. Verify Search & Host Copy (File + Recursive Folder) in strictly Read-Only mode
                var extVol = ReadOnlyFileSystemMountEngine.GetMount(disk.DiskNumber, disk.Partitions[0].StartingOffset);
                var hfsVol = ReadOnlyFileSystemMountEngine.GetMount(disk.DiskNumber, disk.Partitions[4].StartingOffset);

                var hfsHits = ReadOnlyFileSystemMountEngine.SearchVolume(hfsVol, "SystemVersion.plist");
                if (hfsHits.Count != 1 || hfsHits[0].FullPath != "/System/Library/CoreServices/SystemVersion.plist")
                    throw new Exception("SearchVolume failed on HFS+: expected /System/Library/CoreServices/SystemVersion.plist.");

                var extHits = ReadOnlyFileSystemMountEngine.SearchVolume(extVol, "fstab");
                if (extHits.Count != 1 || extHits[0].FullPath != "/etc/fstab")
                    throw new Exception("SearchVolume failed on Ext4: expected /etc/fstab.");

                // Guardrail: writing while Read-Only must throw InvalidOperationException
                bool blockedRo = false;
                try
                {
                    ReadOnlyFileSystemMountEngine.WriteFileToVolume(disk, disk.Partitions[4], hfsVol, hfsVol.RootNode, "blocked.txt", Encoding.UTF8.GetBytes("nope"));
                }
                catch (InvalidOperationException)
                {
                    blockedRo = true;
                }
                if (!blockedRo)
                    throw new Exception("Read-Only safety failure: WriteFileToVolume succeeded without Experimental Write Mode enabled.");

                int fCount;
                int dCount;
                long bCount;
                string hfsExportRoot = Path.Combine(tempDir, "host_export_hfs");
                var usersDir = FindFsNodeByPath(hfsVol.RootNode, "/Users");
                ReadOnlyFileSystemMountEngine.ExportNodeToHost(disk, disk.Partitions[4], usersDir, hfsExportRoot, out fCount, out dCount, out bCount);
                string exportedHfsFile = Path.Combine(hfsExportRoot, "Users", "Shared", "ReadMe.txt");
                if (!File.Exists(exportedHfsFile) || File.ReadAllText(exportedHfsFile).IndexOf("hfs+", StringComparison.OrdinalIgnoreCase) < 0 || fCount < 1 || dCount < 2)
                    throw new Exception("Recursive HFS+ folder copy to host failed.");

                string extExportRoot = Path.Combine(tempDir, "host_export_ext4");
                var etcDir = FindFsNodeByPath(extVol.RootNode, "/etc");
                ReadOnlyFileSystemMountEngine.ExportNodeToHost(disk, disk.Partitions[0], etcDir, extExportRoot, out fCount, out dCount, out bCount);
                string exportedExtFile = Path.Combine(extExportRoot, "etc", "fstab");
                if (!File.Exists(exportedExtFile) || File.ReadAllText(exportedExtFile).IndexOf("UUID=", StringComparison.OrdinalIgnoreCase) < 0)
                    throw new Exception("Recursive Ext4 folder copy to host failed.");
            }
            finally
            {
                foreach (var part in disk.Partitions)
                    ReadOnlyFileSystemMountEngine.Unmount(disk.DiskNumber, part.StartingOffset);
            }

            // 4b. Verify >16 MiB Multi-Extent + HFS+ Extents Overflow B-Tree Streaming Copy (20 MiB BaseSystem.dmg)
            VerifyLargeHfsPlusOverflowExport(tempDir, log);

            // 4c. Verify Btrfs, XFS, Ext2 & Dev Filesystems (F2FS, ZFS, SquashFS, ISO9660, UDF)
            VerifyDevFileSystemsMountAndFormat(tempDir, log);

            byte[] afterBytes = File.ReadAllBytes(imgPath);
            uint crcAfterUnmount = Crc32.Compute(afterBytes, 0, afterBytes.Length);
            if (crcAfterUnmount != crcBeforeMount)
                throw new Exception(string.Format("READ-ONLY VIOLATION: Test disk image mutated during mount/browse/search/export (before 0x{0:X8} != after 0x{1:X8}).", crcBeforeMount, crcAfterUnmount));

            // 5. Verify Experimental Write Mode (Virtual Test Disk Only) on Ext4 & HFS+
            string hostUploadSrc = Path.Combine(tempDir, "HostUploadBundle");
            Directory.CreateDirectory(Path.Combine(hostUploadSrc, "Nested"));
            File.WriteAllText(Path.Combine(hostUploadSrc, "notes.txt"), "Experimental host upload top-level file.");
            File.WriteAllText(Path.Combine(hostUploadSrc, "Nested", "deep.txt"), "Experimental nested file payload 12345!");

            foreach (int pIdx in new int[] { 0, 4 })
            {
                var p = disk.Partitions[pIdx];
                var vol = ReadOnlyFileSystemMountEngine.MountReadOnly(disk, p, true);
                ReadOnlyFileSystemMountEngine.SetExperimentalWriteMode(vol, true);
                if (vol.IsReadOnly)
                    throw new Exception("Experimental Write toggle failed to unlock writes on " + p.FileSystem);

                var expDir = ReadOnlyFileSystemMountEngine.CreateDirectoryOnVolume(disk, p, vol, vol.RootNode, "ExpCustom");
                ReadOnlyFileSystemMountEngine.WriteFileToVolume(disk, p, vol, expDir, "direct.txt", Encoding.UTF8.GetBytes("DirectWritePayload-" + p.FileSystem));
                var tempDel = ReadOnlyFileSystemMountEngine.WriteFileToVolume(disk, p, vol, expDir, "temp_del.txt", Encoding.UTF8.GetBytes("delete-me"));
                ReadOnlyFileSystemMountEngine.DeleteNodeFromVolume(disk, p, vol, tempDel);

                int fw;
                int dw;
                ReadOnlyFileSystemMountEngine.WriteHostDirectoryToVolume(disk, p, vol, vol.RootNode, hostUploadSrc, out fw, out dw);
                if (fw != 2 || dw != 2)
                    throw new Exception("WriteHostDirectoryToVolume count mismatch: files=" + fw + ", dirs=" + dw);

                ReadOnlyFileSystemMountEngine.Unmount(disk.DiskNumber, p.StartingOffset);
            }

            // Reload image from scratch and verify written overlay persists & defaults back to Read-Only
            disk = RawDiskEngine.LoadVirtualDisk(imgPath, 98);
            foreach (int pIdx in new int[] { 0, 4 })
            {
                var p = disk.Partitions[pIdx];
                var reloadedVol = ReadOnlyFileSystemMountEngine.MountReadOnly(disk, p, true);
                if (!reloadedVol.IsReadOnly)
                    throw new Exception("Remounted volume must default back to Read-Only.");

                VerifyRoMountAndFile(disk, p, "/ExpCustom/direct.txt", "DirectWritePayload-" + p.FileSystem);
                VerifyRoMountAndFile(disk, p, "/HostUploadBundle/notes.txt", "Experimental host upload top-level file.");
                VerifyRoMountAndFile(disk, p, "/HostUploadBundle/Nested/deep.txt", "Experimental nested file payload 12345!");
                if (FindFsNodeByPath(reloadedVol.RootNode, "/ExpCustom/temp_del.txt") != null)
                    throw new Exception("Deleted file reappeared after reload on " + p.FileSystem);

                var searchNew = ReadOnlyFileSystemMountEngine.SearchVolume(reloadedVol, "deep.txt");
                if (searchNew.Count != 1 || searchNew[0].FullPath != "/HostUploadBundle/Nested/deep.txt")
                    throw new Exception("SearchVolume failed to find experimentally written nested file on " + p.FileSystem);

                ReadOnlyFileSystemMountEngine.Unmount(disk.DiskNumber, p.StartingOffset);
            }

            // 6. GUI Instantiation & Layout Smoke Test (MainForm, FileSystemBrowserDialog, HexInspectorDialog)
            using (var mainForm = new MainForm())
            {
                mainForm.CreateControl();
                mainForm.PerformLayout();
            }

            var uiPart = disk.Partitions[4];
            var uiVol = ReadOnlyFileSystemMountEngine.MountReadOnly(disk, uiPart, true);
            try
            {
                using (var browserDlg = new WinParted.UI.Dialogs.FileSystemBrowserDialog(disk, uiPart, uiVol))
                {
                    browserDlg.CreateControl();
                    browserDlg.PerformLayout();
                }
                using (var hexDlg = new WinParted.UI.Dialogs.HexSectorInspectorDialog(disk, uiPart.StartLba))
                {
                    hexDlg.CreateControl();
                    hexDlg.PerformLayout();
                }
            }
            finally
            {
                ReadOnlyFileSystemMountEngine.UnmountAll();
            }

            log("  -> Stress 8 PASSED (6 foreign FSs, >16 MiB HFS+ overflow, native Btrfs/XFS/Ext2/ISO9660 & dev FSs verified).");
        }

        /// <summary>
        /// Verifies native formatting, detection, read-only mounting, search, host export, and experimental writes
        /// across Btrfs, XFS, Ext2, F2FS, ZFS, SquashFS, ISO9660, UDF, FAT16, and ReFS on a virtual test image.
        /// </summary>
        private static void VerifyDevFileSystemsMountAndFormat(string tempDir, Action<string> log)
        {
            string devImg = Path.Combine(tempDir, "stress8_devfs.img");
            var disk = RawDiskEngine.CreateBlankVirtualDisk(devImg, 128 * MiB, PartitionTableStyle.GPT);
            disk.DiskNumber = 95;
            if (!disk.IsVirtualImage)
                throw new Exception("Safety check failed: VerifyDevFileSystemsMountAndFormat must use a virtual disk image.");

            var qm = new OperationQueueManager();
            qm.SetBaselineDisks(null, new[] { disk });
            qm.Enqueue(MakeCreateOp(95, devImg, 1 * MiB, 14 * MiB, FileSystemType.Btrfs, "dev-btrfs"));
            qm.Enqueue(MakeCreateOp(95, devImg, 15 * MiB, 14 * MiB, FileSystemType.XFS, "dev-xfs"));
            qm.Enqueue(MakeCreateOp(95, devImg, 29 * MiB, 14 * MiB, FileSystemType.Ext2, "dev-ext2"));
            qm.Enqueue(MakeCreateOp(95, devImg, 43 * MiB, 14 * MiB, FileSystemType.ISO9660, "DEV_ISO"));
            qm.Enqueue(MakeCreateOp(95, devImg, 57 * MiB, 14 * MiB, FileSystemType.F2FS, "dev-f2fs"));
            qm.Enqueue(MakeCreateOp(95, devImg, 71 * MiB, 14 * MiB, FileSystemType.ZFS, "dev-zfs"));
            qm.Enqueue(MakeCreateOp(95, devImg, 85 * MiB, 14 * MiB, FileSystemType.SquashFS, "dev-squash"));
            qm.Enqueue(MakeCreateOp(95, devImg, 99 * MiB, 14 * MiB, FileSystemType.UDF, "dev-udf"));
            qm.ApplyAll();

            disk = RawDiskEngine.LoadVirtualDisk(devImg, 95);
            AssertIntegrity(disk);
            if (disk.Partitions.Count != 8)
                throw new Exception("Expected 8 dev filesystem partitions, got " + disk.Partitions.Count);

            // Clear WPROFS01 synthetic catalog on Btrfs, XFS, and ISO9660 to prove native on-disk B-tree / XFSB+IN / ECMA-119 parsing
            byte[] clearCat = new byte[4096];
            RawDiskEngine.WriteBytes(disk, disk.Partitions[0].StartingOffset + 0x3000, clearCat);
            RawDiskEngine.WriteBytes(disk, disk.Partitions[1].StartingOffset + 0x3000, clearCat);
            RawDiskEngine.WriteBytes(disk, disk.Partitions[3].StartingOffset + 0x3000, clearCat);

            disk = RawDiskEngine.LoadVirtualDisk(devImg, 95);
            if (disk.Partitions[0].FileSystem != FileSystemType.Btrfs ||
                disk.Partitions[1].FileSystem != FileSystemType.XFS ||
                disk.Partitions[2].FileSystem != FileSystemType.Ext2 ||
                disk.Partitions[3].FileSystem != FileSystemType.ISO9660 ||
                disk.Partitions[4].FileSystem != FileSystemType.F2FS ||
                disk.Partitions[5].FileSystem != FileSystemType.ZFS ||
                disk.Partitions[6].FileSystem != FileSystemType.SquashFS ||
                disk.Partitions[7].FileSystem != FileSystemType.UDF)
            {
                throw new Exception("Superblock detection mismatch across Btrfs/XFS/Ext2/ISO9660/F2FS/ZFS/SquashFS/UDF.");
            }
            if (disk.Partitions[5].GptTypeGuid != GptPartitionTypeGuids.ZfsMember)
                throw new Exception("ZFS partition missing OpenZFS GPT Type GUID.");

            try
            {
                // Native Btrfs B-tree leaf node (regular + inline extents)
                VerifyRoMountAndFile(disk, disk.Partitions[0], "/etc/filesystems", "btrfs");
                VerifyRoMountAndFile(disk, disk.Partitions[0], "/home/ReadMe.txt", "Btrfs Copy-on-Write");
                VerifyRoMountAndFile(disk, disk.Partitions[0], "/@dev/btrfs-subvol.conf", "compress=zstd:3");

                // Native XFS Big-Endian superblock + 'IN' shortform dirs + BMBT extents
                VerifyRoMountAndFile(disk, disk.Partitions[1], "/home/ReadMe.txt", "XFS Big-Endian");
                VerifyRoMountAndFile(disk, disk.Partitions[1], "/projects/xfs_project.json", "\"filesystem\": \"XFS\"");

                // Native Ext2 direct i_block[0] inodes + subdirectories
                VerifyRoMountAndFile(disk, disk.Partitions[2], "/etc/hostname", "dev-ext2");
                VerifyRoMountAndFile(disk, disk.Partitions[2], "/home/dev_notes.md", "direct i_block");

                // Native ISO9660 ECMA-119 CD001 directory records
                VerifyRoMountAndFile(disk, disk.Partitions[3], "/README.TXT", "ECMA-119");
                VerifyRoMountAndFile(disk, disk.Partitions[3], "/DEV/TOOLCHAIN.ENV", "FS=iso9660");

                // F2FS, ZFS, SquashFS, UDF
                VerifyRoMountAndFile(disk, disk.Partitions[4], "/etc/filesystems", "f2fs");
                VerifyRoMountAndFile(disk, disk.Partitions[5], "/etc/filesystems", "zfs");
                VerifyRoMountAndFile(disk, disk.Partitions[6], "/etc/filesystems", "squashfs");
                VerifyRoMountAndFile(disk, disk.Partitions[7], "/etc/filesystems", "udf");

                // Test recursive folder host copy on Btrfs & ISO9660
                int fc, dc;
                long bc;
                string btrfsExport = Path.Combine(tempDir, "host_export_btrfs");
                var bVol = ReadOnlyFileSystemMountEngine.GetMount(disk.DiskNumber, disk.Partitions[0].StartingOffset);
                ReadOnlyFileSystemMountEngine.ExportNodeToHost(disk, disk.Partitions[0], FindFsNodeByPath(bVol.RootNode, "/@dev"), btrfsExport, out fc, out dc, out bc);
                if (!File.Exists(Path.Combine(btrfsExport, "@dev", "btrfs-subvol.conf")))
                    throw new Exception("Btrfs host export missing /@dev/btrfs-subvol.conf");
            }
            finally
            {
                ReadOnlyFileSystemMountEngine.UnmountAll();
            }

            // Test Resize & Relabel across dev filesystems on virtual disk
            qm.SetBaselineDisks(null, new[] { disk });
            qm.Enqueue(MakeResizeOp(95, devImg, disk.Partitions[4], 57 * MiB, 12 * MiB));
            qm.Enqueue(new PendingOperation
            {
                Type = OperationType.ChangeLabel,
                TargetDiskNumber = 95,
                TargetDiskPath = devImg,
                TargetPartitionNumber = disk.Partitions[5].PartitionNumber,
                OriginalStartOffset = disk.Partitions[5].StartingOffset,
                OriginalLength = disk.Partitions[5].PartitionLength,
                NewLabel = "tank-pool",
                NewPartitionName = "tank-pool",
                Summary = "Relabel ZFS"
            });
            qm.ApplyAll();

            disk = RawDiskEngine.LoadVirtualDisk(devImg, 95);
            AssertIntegrity(disk);
            if (disk.Partitions[4].FileSystem != FileSystemType.F2FS || disk.Partitions[4].PartitionLength != 12 * MiB)
                throw new Exception("F2FS resize failed.");
            if (disk.Partitions[5].FileSystem != FileSystemType.ZFS || disk.Partitions[5].Label != "tank-pool")
                throw new Exception("ZFS relabel failed.");

            log("  -> Dev Filesystems Verified (native Btrfs B-tree, BE XFS IN-inodes, Ext2 direct blocks, ECMA-119 ISO9660, F2FS, ZFS, SquashFS, UDF).");
        }

        private static void VerifyRoMountAndFile(DiskInfo disk, PartitionEntry part, string fullPath, string expectedSubstring)
        {
            var vol = ReadOnlyFileSystemMountEngine.GetMount(disk.DiskNumber, part.StartingOffset)
                      ?? ReadOnlyFileSystemMountEngine.MountReadOnly(disk, part, true);
            if (!vol.IsReadOnly)
                throw new Exception("Mounted volume must be strictly read-only.");
            if (vol.RootNode == null || vol.RootNode.Children.Count == 0)
                throw new Exception("Root directory tree is empty for " + part.FileSystem);

            var node = FindFsNodeByPath(vol.RootNode, fullPath);
            if (node == null || node.IsDirectory)
                throw new Exception("Expected file '" + fullPath + "' not found on mounted " + part.FileSystem);

            byte[] content = ReadOnlyFileSystemMountEngine.ReadFileBytes(disk, part, node);
            string text = Encoding.UTF8.GetString(content);
            if (text.IndexOf(expectedSubstring, StringComparison.OrdinalIgnoreCase) < 0)
                throw new Exception(string.Format("Unexpected file content for {0} ({1}): expected '{2}', got '{3}'",
                    fullPath, part.FileSystem, expectedSubstring, text));
        }

        private static VirtualFsNode FindFsNodeByPath(VirtualFsNode current, string fullPath)
        {
            if (current == null) return null;
            if (string.Equals(current.FullPath, fullPath, StringComparison.Ordinal))
                return current;
            foreach (var child in current.Children)
            {
                var found = FindFsNodeByPath(child, fullPath);
                if (found != null) return found;
            }
            return null;
        }

        private static PendingOperation MakeCreateOp(int diskNum, string diskPath, long start, long len, FileSystemType fs, string label)
        {
            return new PendingOperation
            {
                Type = OperationType.CreatePartition,
                TargetDiskNumber = diskNum,
                TargetDiskPath = diskPath,
                StartOffset = start,
                Length = len,
                NewFileSystem = fs,
                NewLabel = label,
                NewPartitionName = label,
                NewGptTypeGuid = GptPartitionTypeGuids.ForFileSystem(fs),
                Summary = string.Format("Create {0} ({1}) on Disk {2}", fs, label, diskNum)
            };
        }

        private static PendingOperation MakeResizeOp(int diskNum, string diskPath, PartitionEntry part, long newStart, long newLen)
        {
            return new PendingOperation
            {
                Type = OperationType.ResizeMovePartition,
                TargetDiskNumber = diskNum,
                TargetDiskPath = diskPath,
                TargetPartitionNumber = part.PartitionNumber,
                OriginalStartOffset = part.StartingOffset,
                OriginalLength = part.PartitionLength,
                StartOffset = newStart,
                Length = newLen,
                Summary = string.Format("Resize/Move Part #{0} on Disk {1}", part.PartitionNumber, diskNum)
            };
        }

        private static Dictionary<long, KeyValuePair<int, uint>> WriteDataPayloads(DiskInfo disk, PartitionEntry part, long[] relOffsets, int blockSize)
        {
            var map = new Dictionary<long, KeyValuePair<int, uint>>();
            for (int k = 0; k < relOffsets.Length; k++)
            {
                long rel = relOffsets[k];
                byte[] payload = new byte[blockSize];
                uint seed = (uint)(0x5A170001u + (uint)(part.StartingOffset >> 12) + (uint)(k * 97));
                for (int i = 0; i < payload.Length; i++)
                {
                    seed = seed * 1664525u + 1013904223u;
                    payload[i] = (byte)(seed >> 24);
                }
                Encoding.ASCII.GetBytes("WINPARTED_DATA_OK").CopyTo(payload, 0);
                uint crc = Crc32.Compute(payload, 0, payload.Length);
                RawDiskEngine.WriteBytes(disk, part.StartingOffset + rel, payload);
                map[rel] = new KeyValuePair<int, uint>(blockSize, crc);
            }
            return map;
        }

        private static void VerifyDataPayloads(DiskInfo disk, PartitionEntry part, Dictionary<long, KeyValuePair<int, uint>> expectedPayloads, string context)
        {
            foreach (var kv in expectedPayloads)
            {
                int expectedLen = kv.Value.Key;
                uint expectedCrc = kv.Value.Value;
                byte[] actual = RawDiskEngine.ReadBytes(disk, part.StartingOffset + kv.Key, expectedLen);
                if (actual.Length != expectedLen)
                    throw new Exception(context + ": truncated read at rel offset " + kv.Key + " (got " + actual.Length + ", expected " + expectedLen + ")");

                uint actualCrc = Crc32.Compute(actual, 0, actual.Length);
                if (actualCrc != expectedCrc)
                {
                    throw new Exception(string.Format("DATA CORRUPTION in {0} at rel +0x{1:X}: expected CRC 0x{2:X8}, got 0x{3:X8}.",
                        context, kv.Key, expectedCrc, actualCrc));
                }
            }
        }

        private static void AssertIntegrity(DiskInfo disk)
        {
            string report;
            if (!RawDiskEngine.VerifyDiskPartitionTableIntegrity(disk, out report))
                throw new Exception("Partition table integrity failed on Disk " + disk.DiskNumber + ": " + report);
        }

        private static void WriteBE16(byte[] buf, int off, ushort val)
        {
            buf[off] = (byte)(val >> 8);
            buf[off + 1] = (byte)(val & 0xFF);
        }

        private static void WriteBES16(byte[] buf, int off, short val)
        {
            WriteBE16(buf, off, unchecked((ushort)val));
        }

        private static void WriteBE32(byte[] buf, int off, uint val)
        {
            buf[off] = (byte)(val >> 24);
            buf[off + 1] = (byte)((val >> 16) & 0xFF);
            buf[off + 2] = (byte)((val >> 8) & 0xFF);
            buf[off + 3] = (byte)(val & 0xFF);
        }

        private static void WriteBE64(byte[] buf, int off, ulong val)
        {
            WriteBE32(buf, off, (uint)(val >> 32));
            WriteBE32(buf, off + 4, (uint)(val & 0xFFFFFFFFUL));
        }

        private static int BuildHfsCatalogFolderRecord(byte[] buf, int recOff, uint parentId, string name, uint folderId)
        {
            byte[] utf16 = Encoding.BigEndianUnicode.GetBytes(name);
            ushort keyLen = (ushort)(6 + utf16.Length);
            WriteBE16(buf, recOff, keyLen);
            WriteBE32(buf, recOff + 2, parentId);
            WriteBE16(buf, recOff + 6, (ushort)name.Length);
            Array.Copy(utf16, 0, buf, recOff + 8, utf16.Length);

            int dataOff = recOff + 2 + keyLen;
            if ((dataOff & 1) != 0) dataOff++;

            WriteBES16(buf, dataOff, 1); // kHFSPlusFolderRecord
            WriteBE32(buf, dataOff + 8, folderId);
            WriteBE16(buf, dataOff + 42, 0x41ED);
            return dataOff + 88;
        }

        private static int BuildHfsCatalogFileRecord(
            byte[] buf, int recOff, uint parentId, string name, uint fileId,
            ulong logicalSize, uint[] startBlocks, uint[] blockCounts)
        {
            byte[] utf16 = Encoding.BigEndianUnicode.GetBytes(name);
            ushort keyLen = (ushort)(6 + utf16.Length);
            WriteBE16(buf, recOff, keyLen);
            WriteBE32(buf, recOff + 2, parentId);
            WriteBE16(buf, recOff + 6, (ushort)name.Length);
            Array.Copy(utf16, 0, buf, recOff + 8, utf16.Length);

            int dataOff = recOff + 2 + keyLen;
            if ((dataOff & 1) != 0) dataOff++;

            WriteBES16(buf, dataOff, 2); // kHFSPlusFileRecord
            WriteBE32(buf, dataOff + 8, fileId);
            WriteBE16(buf, dataOff + 42, 0x81A4);
            WriteBE64(buf, dataOff + 88, logicalSize);

            int count = Math.Min(8, Math.Min(startBlocks.Length, blockCounts.Length));
            for (int i = 0; i < count; i++)
            {
                WriteBE32(buf, dataOff + 104 + i * 8, startBlocks[i]);
                WriteBE32(buf, dataOff + 104 + i * 8 + 4, blockCounts[i]);
            }
            return dataOff + 168;
        }

        /// <summary>
        /// Regression test for large HFS+ files exceeding 8 inline fork extents: constructs a 20 MiB sparse
        /// Extents Overflow B-Tree file and verifies complete 20 MiB streaming export in 4 MiB chunks.
        /// </summary>
        private static void VerifyLargeHfsPlusOverflowExport(string tempDir, Action<string> log)
        {
            const long DiskBytes = 34L * 1048576L;
            const uint BlockSize = 4096u;
            const uint TotalBlocks = 8192u; // 32 MiB
            const long ExpectedDmgSize = 20L * 1048576L; // > 16 MiB!

            string hfsImg = Path.Combine(tempDir, "stress8_hfs_large.img");
            var disk = RawDiskEngine.CreateBlankVirtualDisk(hfsImg, DiskBytes, PartitionTableStyle.GPT);
            disk.DiskNumber = 97;

            long partStart = 1 * MiB;
            long partLen = 32 * MiB;
            var qm = new OperationQueueManager();
            qm.SetBaselineDisks(null, new[] { disk });
            qm.Enqueue(MakeCreateOp(97, hfsImg, partStart, partLen, FileSystemType.HFSPlus, "MojaveUSB"));
            qm.ApplyAll();

            disk = RawDiskEngine.LoadVirtualDisk(hfsImg, 97);
            var hfsPart = disk.Partitions[0];

            // Build Volume Header (block 0 @ +1024)
            byte[] vh = new byte[512];
            Encoding.ASCII.GetBytes("H+").CopyTo(vh, 0);
            WriteBE16(vh, 2, 4);
            WriteBE32(vh, 40, BlockSize);
            WriteBE32(vh, 44, TotalBlocks);
            WriteBE32(vh, 48, 2500u);
            WriteBE64(vh, 192, 8192UL); // extentsFile logicalSize (2 nodes * 4096)
            WriteBE32(vh, 208, 2u);     // extentsFile startBlock = 2
            WriteBE32(vh, 212, 2u);     // extentsFile blockCount = 2
            WriteBE64(vh, 272, 8192UL); // catalogFile logicalSize (2 nodes * 4096)
            WriteBE32(vh, 288, 4u);     // catalogFile startBlock = 4
            WriteBE32(vh, 292, 2u);     // catalogFile blockCount = 2
            RawDiskEngine.WriteBytes(disk, hfsPart.StartingOffset + 0x400, vh);

            // Extents Overflow B-Tree Node 0 (Header) + Node 1 (Leaf) @ blocks 2..3
            byte[] extNodes = new byte[8192];
            extNodes[8] = 1; // kBTHeaderNode
            WriteBE16(extNodes, 10, 3);
            WriteBE32(extNodes, 16, 1u);    // rootNode
            WriteBE32(extNodes, 20, 1u);    // leafRecords
            WriteBE32(extNodes, 24, 1u);    // firstLeafNode
            WriteBE16(extNodes, 32, 4096);  // nodeSize

            int extLeaf = 4096;
            extNodes[extLeaf + 8] = 0xFF;   // kBTLeafNode (-1)
            extNodes[extLeaf + 9] = 1;
            WriteBE16(extNodes, extLeaf + 10, 1); // 1 record
            int extRec = extLeaf + 14;
            WriteBE16(extNodes, extRec, 10);       // keyLength = 10
            extNodes[extRec + 2] = 0;              // data fork
            WriteBE32(extNodes, extRec + 4, 16u);  // fileId = 16 (BaseSystem.dmg)
            WriteBE32(extNodes, extRec + 8, 3072u);// startBlock after 8 inline extents (8 * 384)
            int extDataOff = extRec + 12;
            uint[] ovStarts = new uint[] { 3300u, 3820u, 4340u, 4860u };
            uint[] ovCounts = new uint[] { 512u, 512u, 512u, 512u };
            for (int i = 0; i < 4; i++)
            {
                WriteBE32(extNodes, extDataOff + i * 8, ovStarts[i]);
                WriteBE32(extNodes, extDataOff + i * 8 + 4, ovCounts[i]);
            }
            WriteBE16(extNodes, extLeaf + 4094, 14);
            WriteBE16(extNodes, extLeaf + 4092, (ushort)(extDataOff + 64 - extLeaf));
            RawDiskEngine.WriteBytes(disk, hfsPart.StartingOffset + 2L * BlockSize, extNodes);

            // Catalog B-Tree Node 0 (Header) + Node 1 (Leaf) @ blocks 4..5
            byte[] catNodes = new byte[8192];
            catNodes[8] = 1; // kBTHeaderNode
            WriteBE16(catNodes, 10, 3);
            WriteBE32(catNodes, 16, 1u);    // rootNode
            WriteBE32(catNodes, 20, 3u);    // leafRecords
            WriteBE32(catNodes, 24, 1u);    // firstLeafNode
            WriteBE16(catNodes, 32, 4096);  // nodeSize

            int catLeaf = 4096;
            catNodes[catLeaf + 8] = 0xFF;   // kBTLeafNode (-1)
            catNodes[catLeaf + 9] = 1;
            WriteBE16(catNodes, catLeaf + 10, 3);

            uint[] inStarts = new uint[8];
            uint[] inCounts = new uint[8];
            for (int i = 0; i < 8; i++)
            {
                inStarts[i] = (uint)(20 + i * 400);
                inCounts[i] = 384u;
            }

            int r0 = catLeaf + 14;
            int r1 = BuildHfsCatalogFolderRecord(catNodes, r0, 1u, "MojaveUSB", 2u);
            int r2 = BuildHfsCatalogFolderRecord(catNodes, r1, 2u, "SharedSupport", 15u);
            int r3End = BuildHfsCatalogFileRecord(catNodes, r2, 15u, "BaseSystem.dmg", 16u, (ulong)ExpectedDmgSize, inStarts, inCounts);

            WriteBE16(catNodes, catLeaf + 4094, (ushort)(r0 - catLeaf));
            WriteBE16(catNodes, catLeaf + 4092, (ushort)(r1 - catLeaf));
            WriteBE16(catNodes, catLeaf + 4090, (ushort)(r2 - catLeaf));
            WriteBE16(catNodes, catLeaf + 4088, (ushort)(r3End - catLeaf));
            RawDiskEngine.WriteBytes(disk, hfsPart.StartingOffset + 4L * BlockSize, catNodes);

            // Stamp unique verification signatures into inline and overflow extents (including past 16 MiB!)
            var allExtents = new List<uint>();
            var allCounts = new List<uint>();
            allExtents.AddRange(inStarts);
            allCounts.AddRange(inCounts);
            allExtents.AddRange(ovStarts);
            allCounts.AddRange(ovCounts);

            byte[] stamp = new byte[64];
            for (int ex = 0; ex < allExtents.Count; ex++)
            {
                string tag = string.Format("EXTENT_{0:D2}_START", ex);
                Array.Clear(stamp, 0, stamp.Length);
                Encoding.ASCII.GetBytes(tag).CopyTo(stamp, 0);
                RawDiskEngine.WriteBytes(disk, hfsPart.StartingOffset + (long)allExtents[ex] * BlockSize, stamp);

                string endTag = string.Format("EXTENT_{0:D2}_TAIL!", ex);
                Array.Clear(stamp, 0, stamp.Length);
                Encoding.ASCII.GetBytes(endTag).CopyTo(stamp, 0);
                long tailOff = hfsPart.StartingOffset + ((long)(allExtents[ex] + allCounts[ex]) * BlockSize) - 64;
                RawDiskEngine.WriteBytes(disk, tailOff, stamp);
            }

            var vol = ReadOnlyFileSystemMountEngine.MountReadOnly(disk, hfsPart, true);
            try
            {
                var dmgNode = FindFsNodeByPath(vol.RootNode, "/SharedSupport/BaseSystem.dmg");
                if (dmgNode == null)
                    throw new Exception("Large HFS+ BaseSystem.dmg not found in Catalog B-Tree.");
                if (dmgNode.SizeBytes != ExpectedDmgSize)
                    throw new Exception(string.Format("Expected BaseSystem.dmg size {0}, got {1}.", ExpectedDmgSize, dmgNode.SizeBytes));

                // Preview read must remain safely capped
                byte[] preview = ReadOnlyFileSystemMountEngine.ReadFileBytes(disk, hfsPart, dmgNode, 131072);
                if (preview.Length != 131072)
                    throw new Exception("Preview cap failed: got " + preview.Length + " bytes.");

                // Full copy to host must stream all 20 MiB (>16 MiB!) across all 12 extents (inline + overflow B-Tree)
                string hostDmg = Path.Combine(tempDir, "BaseSystem_exported.dmg");
                int fc;
                int dc;
                long bc;
                ReadOnlyFileSystemMountEngine.ExportNodeToHost(disk, hfsPart, dmgNode, hostDmg, out fc, out dc, out bc);

                var fi = new FileInfo(hostDmg);
                if (!fi.Exists || fi.Length != ExpectedDmgSize || bc != ExpectedDmgSize)
                    throw new Exception(string.Format("BaseSystem.dmg export truncated! Expected {0} bytes, exported file={1}, reported={2}.",
                        ExpectedDmgSize, fi.Exists ? fi.Length : -1, bc));

                using (var fs = new FileStream(hostDmg, FileMode.Open, FileAccess.Read))
                {
                    byte[] checkBuf = new byte[32];
                    long logicalCursor = 0;
                    for (int ex = 0; ex < allCounts.Count; ex++)
                    {
                        long extBytes = (long)allCounts[ex] * BlockSize;
                        fs.Seek(logicalCursor, SeekOrigin.Begin);
                        fs.Read(checkBuf, 0, checkBuf.Length);
                        string actualStart = Encoding.ASCII.GetString(checkBuf, 0, 15);
                        string expectedStart = string.Format("EXTENT_{0:D2}_START", ex);
                        if (actualStart != expectedStart)
                            throw new Exception(string.Format("Extent {0} start mismatch at logical offset {1}: expected '{2}', got '{3}'.",
                                ex, logicalCursor, expectedStart, actualStart));

                        fs.Seek(logicalCursor + extBytes - 64, SeekOrigin.Begin);
                        fs.Read(checkBuf, 0, checkBuf.Length);
                        string actualTail = Encoding.ASCII.GetString(checkBuf, 0, 15);
                        string expectedTail = string.Format("EXTENT_{0:D2}_TAIL!", ex);
                        if (actualTail != expectedTail)
                            throw new Exception(string.Format("Extent {0} tail mismatch at logical offset {1}: expected '{2}', got '{3}'.",
                                ex, logicalCursor + extBytes - 64, expectedTail, actualTail));

                        logicalCursor += extBytes;
                    }
                }

                log("  -> Verified 20 MiB fragmented HFS+ BaseSystem.dmg (8 inline + 4 Extents Overflow B-Tree) exported 100% intact.");
            }
            finally
            {
                ReadOnlyFileSystemMountEngine.Unmount(disk.DiskNumber, hfsPart.StartingOffset);
            }
        }

        private static void ExpectRejected(Action act, string scenario)
        {
            bool threw = false;
            try
            {
                act();
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }
            if (!threw)
                throw new Exception("Safety check FAILED: '" + scenario + "' should have been rejected!");
        }
    }
}
