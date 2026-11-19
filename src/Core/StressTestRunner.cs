using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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
                RunStress9_BootUnlockFullFormatAndHybridOverlay(tempDir, log);
                RunStress10_SecureWipeFormatAndDemoResetOverlayPurge(tempDir, log);
                RunStress11_ExFatOfflineResizeMoveSpec(tempDir, log);

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

            // Strip WinParted synthetic hints (WPL: and WPROFS01) from APFS and HFS+ partitions so labels + trees are proven to come purely from on-disk B-Trees
            byte[] zeroSlot = new byte[32];
            var apfsPartRaw = disk.Partitions[3];
            apfsPartRaw.Label = string.Empty;
            RawDiskEngine.WriteBytes(disk, apfsPartRaw.StartingOffset + 0x1A0, zeroSlot);
            RawDiskEngine.WriteBytes(disk, apfsPartRaw.StartingOffset + 0x3000, new byte[4096]);

            var hfsPartRaw = disk.Partitions[4];
            hfsPartRaw.Label = string.Empty;
            RawDiskEngine.WriteBytes(disk, hfsPartRaw.StartingOffset + 0x1A0, zeroSlot);
            RawDiskEngine.WriteBytes(disk, hfsPartRaw.StartingOffset + 0x3000, new byte[4096]);

            disk = RawDiskEngine.LoadVirtualDisk(imgPath, 98);
            if (disk.Partitions[3].Label != "ro-apfs")
                throw new Exception("APFS OMAP/APSB volume label extraction failed: got '" + disk.Partitions[3].Label + "'");
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

                // 3. Pure Apple APFS Container/OMAP/FS-Tree, Big-Endian HFS+ Catalog B-Tree, Classic HFS
                VerifyRoMountAndFile(disk, disk.Partitions[3], "/System/Library/CoreServices/SystemVersion.plist", "<string>APFS</string>");
                VerifyRoMountAndFile(disk, disk.Partitions[3], "/Users/Shared/ReadMe.txt", "ro-apfs");
                VerifyRoMountAndFile(disk, disk.Partitions[3], "/.VolumeLabel", "ro-apfs");
                VerifyRoMountAndFile(disk, disk.Partitions[4], "/.hfsplus_volume_info.txt", "Catalog B-Tree Summary");
                VerifyRoMountAndFile(disk, disk.Partitions[4], "/System/Library/CoreServices/SystemVersion.plist", "<string>hfs+</string>");
                VerifyRoMountAndFile(disk, disk.Partitions[4], "/Users/Shared/ReadMe.txt", "hfs+");
                VerifyRoMountAndFile(disk, disk.Partitions[5], "/.VolumeLabel", "ro-hfs");

                // 4. Verify Search & Host Copy (File + Recursive Folder) in strictly Read-Only mode
                var extVol = ReadOnlyFileSystemMountEngine.GetMount(disk.DiskNumber, disk.Partitions[0].StartingOffset);
                var apfsVol = ReadOnlyFileSystemMountEngine.GetMount(disk.DiskNumber, disk.Partitions[3].StartingOffset);
                var hfsVol = ReadOnlyFileSystemMountEngine.GetMount(disk.DiskNumber, disk.Partitions[4].StartingOffset);

                var apfsHits = ReadOnlyFileSystemMountEngine.SearchVolume(apfsVol, "SystemVersion.plist");
                if (apfsHits.Count != 1 || apfsHits[0].FullPath != "/System/Library/CoreServices/SystemVersion.plist")
                    throw new Exception("SearchVolume failed on APFS: expected /System/Library/CoreServices/SystemVersion.plist.");

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
                string apfsExportRoot = Path.Combine(tempDir, "host_export_apfs");
                var apfsSystemDir = FindFsNodeByPath(apfsVol.RootNode, "/System");
                ReadOnlyFileSystemMountEngine.ExportNodeToHost(disk, disk.Partitions[3], apfsSystemDir, apfsExportRoot, out fCount, out dCount, out bCount);
                string exportedApfsFile = Path.Combine(apfsExportRoot, "System", "Library", "CoreServices", "SystemVersion.plist");
                if (!File.Exists(exportedApfsFile) || File.ReadAllText(exportedApfsFile).IndexOf("ro-apfs", StringComparison.OrdinalIgnoreCase) < 0 || fCount < 1 || dCount < 3)
                    throw new Exception("Recursive APFS folder copy to host failed.");

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

            // 4b2. Verify Multi-Volume / Multi-Level APFS Container B-Tree, Sparse Multi-Extent Export, & decmpfs
            VerifyAdvancedApfsContainerExport(tempDir, log);

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

            // Verify Fedora isohybrid-style ISO9660 partition at +0x8000 (where overlay hits offset 0x28000)
            string isoImg = Path.Combine(tempDir, "stress8_isohybrid_0x8000.img");
            var isoDisk = RawDiskEngine.CreateBlankVirtualDisk(isoImg, 32 * MiB, PartitionTableStyle.MBR);
            isoDisk.DiskNumber = 94;
            var isoPart = new PartitionEntry
            {
                DiskNumber = 94,
                PartitionNumber = 1,
                StartingOffset = 0x8000L,
                PartitionLength = 24 * MiB,
                MbrPartitionType = 0x17,
                FileSystem = FileSystemType.ISO9660,
                Label = "FEDORA-WS-LIVE",
                LogicalSectorSize = 512,
                PhysicalSectorSize = 4096
            };
            isoDisk.Partitions.Add(isoPart);
            RawDiskEngine.WritePartitionTable(isoDisk);
            RawDiskEngine.WriteFileSystemHeader(isoDisk, isoPart);

            isoDisk = RawDiskEngine.LoadVirtualDisk(isoImg, 94);
            AssertIntegrity(isoDisk);
            if (isoDisk.Partitions.Count != 1 || isoDisk.Partitions[0].FileSystem != FileSystemType.ISO9660 || isoDisk.Partitions[0].StartingOffset != 0x8000L)
                throw new Exception("isohybrid ISO9660 +0x8000 partition layout detection failed.");

            try
            {
                var isoVol = ReadOnlyFileSystemMountEngine.MountReadOnly(isoDisk, isoDisk.Partitions[0], true);
                ReadOnlyFileSystemMountEngine.SetExperimentalWriteMode(isoVol, true);
                var dir = ReadOnlyFileSystemMountEngine.CreateDirectoryOnVolume(isoDisk, isoDisk.Partitions[0], isoVol, isoVol.RootNode, "fedora_ovl");
                byte[] expectedIsoBytes = Encoding.UTF8.GetBytes("Fedora isohybrid overlay payload @ 0x28000 check\n");
                ReadOnlyFileSystemMountEngine.WriteFileToVolume(isoDisk, isoDisk.Partitions[0], isoVol, dir, "kickstart.cfg", expectedIsoBytes);
                var tmpDel = ReadOnlyFileSystemMountEngine.WriteFileToVolume(isoDisk, isoDisk.Partitions[0], isoVol, dir, "temp.txt", Encoding.UTF8.GetBytes("del"));
                ReadOnlyFileSystemMountEngine.DeleteNodeFromVolume(isoDisk, isoDisk.Partitions[0], isoVol, tmpDel);

                ReadOnlyFileSystemMountEngine.Unmount(isoDisk.DiskNumber, isoDisk.Partitions[0].StartingOffset);
                var remountedIso = ReadOnlyFileSystemMountEngine.MountReadOnly(isoDisk, isoDisk.Partitions[0], true);
                var ksNode = FindFsNodeByPath(remountedIso.RootNode, "/fedora_ovl/kickstart.cfg");
                if (ksNode == null || FindFsNodeByPath(remountedIso.RootNode, "/fedora_ovl/temp.txt") != null)
                    throw new Exception("ISO9660 +0x8000 experimental overlay persistence failed after remount.");
                byte[] gotIsoBytes = ReadOnlyFileSystemMountEngine.ReadFileBytes(isoDisk, isoDisk.Partitions[0], ksNode);
                if (Encoding.UTF8.GetString(gotIsoBytes) != Encoding.UTF8.GetString(expectedIsoBytes))
                    throw new Exception("ISO9660 +0x8000 experimental overlay file payload mismatch.");

                // Verify Host Shadow Overlay fallback when a physical drive rejects raw sector writes
                ReadOnlyFileSystemMountEngine.ShadowOverlayRootOverride = Path.Combine(tempDir, "host_shadow_ovl");
                var fakePhysDisk = new DiskInfo
                {
                    DiskNumber = 999,
                    DevicePath = @"\\.\PhysicalDrive999",
                    IsVirtualImage = false,
                    Model = "TestFedoraUSB",
                    SerialNumber = "FEDORA-TEST-USB",
                    TotalSizeBytes = 32 * MiB,
                    LogicalSectorSize = 512,
                    PhysicalSectorSize = 4096
                };
                var fakePhysPart = new PartitionEntry
                {
                    DiskNumber = 999,
                    PartitionNumber = 1,
                    StartingOffset = 0x8000L,
                    PartitionLength = 24 * MiB,
                    FileSystem = FileSystemType.ISO9660,
                    Label = "FEDORA-LIVE",
                    UUID = "2026-11-13-00"
                };
                var shadowVol = new MountedReadOnlyVolume
                {
                    MountId = "ro_disk999_p1",
                    MountPath = "/mnt/ro/disk999p1",
                    DiskNumber = 999,
                    PartitionNumber = 1,
                    StartingOffset = fakePhysPart.StartingOffset,
                    PartitionLength = fakePhysPart.PartitionLength,
                    FileSystem = FileSystemType.ISO9660,
                    Label = fakePhysPart.Label,
                    UUID = fakePhysPart.UUID,
                    BlockSize = 2048,
                    OverlayNextDataRelOffset = 0x24000L,
                    OverlayRecords = new List<OverlayRecord>(),
                    RootNode = new VirtualFsNode { Name = "/", FullPath = "/", IsDirectory = true, PermissionsString = "dr-xr-xr-x" }
                };
                ReadOnlyFileSystemMountEngine.SetExperimentalWriteMode(shadowVol, true);
                var sDir = ReadOnlyFileSystemMountEngine.CreateDirectoryOnVolume(fakePhysDisk, fakePhysPart, shadowVol, shadowVol.RootNode, "shadow_dir");
                byte[] shadowPayload = Encoding.UTF8.GetBytes("Host shadow fallback verified for locked physical USB!");
                var sFile = ReadOnlyFileSystemMountEngine.WriteFileToVolume(fakePhysDisk, fakePhysPart, shadowVol, sDir, "ks.cfg", shadowPayload);
                if (!shadowVol.OverlayUsingHostShadow)
                    throw new Exception("Physical write rejection did not activate host shadow overlay.");

                byte[] gotShadowImmediate = ReadOnlyFileSystemMountEngine.ReadFileBytes(fakePhysDisk, fakePhysPart, sFile);
                if (Encoding.UTF8.GetString(gotShadowImmediate) != Encoding.UTF8.GetString(shadowPayload))
                    throw new Exception("Host shadow overlay immediate read mismatch.");

                var reloadShadowVol = new MountedReadOnlyVolume
                {
                    MountId = "ro_disk999_p1",
                    MountPath = "/mnt/ro/disk999p1",
                    DiskNumber = 999,
                    PartitionNumber = 1,
                    StartingOffset = fakePhysPart.StartingOffset,
                    PartitionLength = fakePhysPart.PartitionLength,
                    FileSystem = FileSystemType.ISO9660,
                    BlockSize = 2048,
                    OverlayNextDataRelOffset = 0x24000L,
                    OverlayRecords = new List<OverlayRecord>(),
                    RootNode = new VirtualFsNode { Name = "/", FullPath = "/", IsDirectory = true, PermissionsString = "dr-xr-xr-x" }
                };
                ReadOnlyFileSystemMountEngine.LoadAndMergeExperimentalOverlay(fakePhysDisk, fakePhysPart, reloadShadowVol);
                var reloadedNode = FindFsNodeByPath(reloadShadowVol.RootNode, "/shadow_dir/ks.cfg");
                if (!reloadShadowVol.OverlayUsingHostShadow || reloadedNode == null)
                    throw new Exception("Host shadow overlay reload failed to restore /shadow_dir/ks.cfg.");
                byte[] gotShadowReload = ReadOnlyFileSystemMountEngine.ReadFileBytes(fakePhysDisk, fakePhysPart, reloadedNode);
                if (Encoding.UTF8.GetString(gotShadowReload) != Encoding.UTF8.GetString(shadowPayload))
                    throw new Exception("Host shadow overlay reloaded payload mismatch.");
            }
            finally
            {
                ReadOnlyFileSystemMountEngine.ShadowOverlayRootOverride = null;
                ReadOnlyFileSystemMountEngine.UnmountAll();
            }

            log("  -> Dev Filesystems Verified (native Btrfs B-tree, BE XFS IN-inodes, Ext2 direct blocks, ECMA-119 ISO9660, F2FS, ZFS, SquashFS, UDF + 0x28000/shadow RW overlay).");
        }

        /// <summary>
        /// Stress 9: Verifies that active OS boot disks reject destructive edits at queue time while
        /// non-system/USB boot sectors remain editable, validates full-drive reformatting, and tests
        /// isohybrid tail <c>WPRWOVL1</c> overlay offset selection.
        /// </summary>
        private static void RunStress9_BootUnlockFullFormatAndHybridOverlay(string tempDir, Action<string> log)
        {
            log("[Stress 9] OS Boot Protection vs USB/Non-System Boot Editing, Full-Drive Reformat, and Isohybrid Overlay Selection...");

            ReadOnlyFileSystemMountEngine.ShadowOverlayRootOverride = Path.Combine(tempDir, "stress9_shadow");
            try
            {
                // 9a. Active running OS disk (IsVirtualImage = false, IsSystemDisk = true) MUST reject destructive edits at Enqueue time.
                // Queue validation ONLY — never call ApplyAll on a mock non-virtual disk.
                var mockOsDisk = new DiskInfo
                {
                    DiskNumber = 99,
                    Model = "Mock Windows OS NVMe",
                    DevicePath = @"\\.\PhysicalDrive99",
                    IsVirtualImage = false,
                    IsSystemDisk = true,
                    TotalSizeBytes = 64L * 1048576L,
                    LogicalSectorSize = 512,
                    PhysicalSectorSize = 4096,
                    PartitionStyle = PartitionTableStyle.GPT,
                    GptCrcValid = true
                };
                mockOsDisk.Partitions.Add(new PartitionEntry
                {
                    PartitionNumber = 1,
                    StartingOffset = 1048576L,
                    PartitionLength = 16L * 1048576L,
                    FileSystem = FileSystemType.EfiSystem,
                    GptTypeGuid = GptPartitionTypeGuids.EfiSystem,
                    IsSystemOrBoot = true,
                    PartitionName = "EFI System Partition"
                });

                var qmOs = new OperationQueueManager();
                qmOs.SetBaselineDisks(new DiskInfo[] { mockOsDisk }, new DiskInfo[0]);

                ExpectRejected(() => qmOs.Enqueue(new PendingOperation
                {
                    Type = OperationType.DeletePartition,
                    TargetDiskNumber = 99,
                    TargetDiskPath = mockOsDisk.DevicePath,
                    TargetPartitionNumber = 1,
                    OriginalStartOffset = 1048576L,
                    OriginalLength = 16L * 1048576L,
                    Summary = "Should reject OS ESP delete"
                }), "DeletePartition on active OS boot disk");

                ExpectRejected(() => qmOs.Enqueue(new PendingOperation
                {
                    Type = OperationType.FormatPartition,
                    TargetDiskNumber = 99,
                    TargetDiskPath = mockOsDisk.DevicePath,
                    TargetPartitionNumber = 1,
                    OriginalStartOffset = 1048576L,
                    OriginalLength = 16L * 1048576L,
                    NewFileSystem = FileSystemType.FAT32,
                    Summary = "Should reject OS ESP format"
                }), "FormatPartition on active OS boot disk");

                ExpectRejected(() => qmOs.Enqueue(new PendingOperation
                {
                    Type = OperationType.WipePartition,
                    TargetDiskNumber = 99,
                    TargetDiskPath = mockOsDisk.DevicePath,
                    TargetPartitionNumber = 1,
                    OriginalStartOffset = 1048576L,
                    OriginalLength = 16L * 1048576L,
                    WipePattern = WipePattern.ZeroFill,
                    Summary = "Should reject OS ESP wipe"
                }), "WipePartition on active OS boot disk");

                ExpectRejected(() => qmOs.Enqueue(new PendingOperation
                {
                    Type = OperationType.ResizeMovePartition,
                    TargetDiskNumber = 99,
                    TargetDiskPath = mockOsDisk.DevicePath,
                    TargetPartitionNumber = 1,
                    OriginalStartOffset = 1048576L,
                    OriginalLength = 16L * 1048576L,
                    StartOffset = 1048576L,
                    Length = 8L * 1048576L,
                    Summary = "Should reject OS ESP resize"
                }), "ResizeMovePartition on active OS boot disk");

                ExpectRejected(() => qmOs.Enqueue(new PendingOperation
                {
                    Type = OperationType.CreatePartitionTable,
                    TargetDiskNumber = 99,
                    TargetDiskPath = mockOsDisk.DevicePath,
                    NewTableStyle = PartitionTableStyle.GPT,
                    Summary = "Should reject OS GPT reinit"
                }), "CreatePartitionTable on active OS boot disk");

                // 9b. Non-system disk EFI System Partition + Active MBR boot partition can be formatted, wiped, and deleted.
                string efiImgPath = Path.Combine(tempDir, "stress9_nonos_efi.img");
                var efiDisk = RawDiskEngine.CreateBlankVirtualDisk(efiImgPath, 32L * 1048576L, PartitionTableStyle.GPT);
                efiDisk.DiskNumber = 901;
                efiDisk.IsSystemDisk = false;

                var qmEfi = new OperationQueueManager();
                qmEfi.SetBaselineDisks(new DiskInfo[0], new DiskInfo[] { efiDisk });
                qmEfi.Enqueue(new PendingOperation
                {
                    Type = OperationType.CreatePartition,
                    TargetDiskNumber = 901,
                    TargetDiskPath = efiDisk.DevicePath,
                    StartOffset = 1048576L,
                    Length = 16L * 1048576L,
                    NewFileSystem = FileSystemType.EfiSystem,
                    NewLabel = "USB_ESP",
                    NewPartitionName = "EFI System Partition",
                    NewGptTypeGuid = GptPartitionTypeGuids.EfiSystem,
                    NewMbrType = 0xEF,
                    Summary = "Create USB ESP"
                });
                qmEfi.ApplyAll(null);

                efiDisk = RawDiskEngine.LoadVirtualDisk(efiImgPath, 901);
                if (efiDisk.Partitions.Count != 1 || efiDisk.Partitions[0].FileSystem != FileSystemType.EfiSystem)
                    throw new Exception("Expected non-system EfiSystem partition to be created cleanly.");

                qmEfi.SetBaselineDisks(new DiskInfo[0], new DiskInfo[] { efiDisk });
                qmEfi.Enqueue(new PendingOperation
                {
                    Type = OperationType.FormatPartition,
                    TargetDiskNumber = 901,
                    TargetDiskPath = efiDisk.DevicePath,
                    TargetPartitionNumber = efiDisk.Partitions[0].PartitionNumber,
                    OriginalStartOffset = efiDisk.Partitions[0].StartingOffset,
                    OriginalLength = efiDisk.Partitions[0].PartitionLength,
                    NewFileSystem = FileSystemType.FAT32,
                    NewLabel = "USB_FAT",
                    NewGptTypeGuid = GptPartitionTypeGuids.ForFileSystem(FileSystemType.FAT32),
                    NewMbrType = 0x0C,
                    Summary = "Reformat non-system ESP to FAT32"
                });
                qmEfi.ApplyAll(null);

                efiDisk = RawDiskEngine.LoadVirtualDisk(efiImgPath, 901);
                qmEfi.SetBaselineDisks(new DiskInfo[0], new DiskInfo[] { efiDisk });
                qmEfi.Enqueue(new PendingOperation
                {
                    Type = OperationType.WipePartition,
                    TargetDiskNumber = 901,
                    TargetDiskPath = efiDisk.DevicePath,
                    TargetPartitionNumber = efiDisk.Partitions[0].PartitionNumber,
                    OriginalStartOffset = efiDisk.Partitions[0].StartingOffset,
                    OriginalLength = efiDisk.Partitions[0].PartitionLength,
                    WipePattern = WipePattern.ZeroFill,
                    Summary = "Wipe non-system ESP"
                });
                qmEfi.Enqueue(new PendingOperation
                {
                    Type = OperationType.DeletePartition,
                    TargetDiskNumber = 901,
                    TargetDiskPath = efiDisk.DevicePath,
                    TargetPartitionNumber = efiDisk.Partitions[0].PartitionNumber,
                    OriginalStartOffset = efiDisk.Partitions[0].StartingOffset,
                    OriginalLength = efiDisk.Partitions[0].PartitionLength,
                    Summary = "Delete non-system ESP"
                });
                qmEfi.ApplyAll(null);

                efiDisk = RawDiskEngine.LoadVirtualDisk(efiImgPath, 901);
                if (efiDisk.Partitions.Count != 0)
                    throw new Exception("Expected non-system ESP deletion to remove partition.");

                string mbrImgPath = Path.Combine(tempDir, "stress9_nonos_mbrboot.img");
                var mbrDisk = RawDiskEngine.CreateBlankVirtualDisk(mbrImgPath, 32L * 1048576L, PartitionTableStyle.MBR);
                mbrDisk.DiskNumber = 902;
                mbrDisk.IsSystemDisk = false;

                var qmMbr = new OperationQueueManager();
                qmMbr.SetBaselineDisks(new DiskInfo[0], new DiskInfo[] { mbrDisk });
                qmMbr.Enqueue(new PendingOperation
                {
                    Type = OperationType.CreatePartition,
                    TargetDiskNumber = 902,
                    TargetDiskPath = mbrDisk.DevicePath,
                    StartOffset = 1048576L,
                    Length = 16L * 1048576L,
                    NewFileSystem = FileSystemType.FAT32,
                    NewLabel = "BOOT_USB",
                    NewMbrType = 0x0C,
                    Summary = "Create MBR partition"
                });
                qmMbr.ApplyAll(null);

                mbrDisk = RawDiskEngine.LoadVirtualDisk(mbrImgPath, 902);
                qmMbr.SetBaselineDisks(new DiskInfo[0], new DiskInfo[] { mbrDisk });
                qmMbr.Enqueue(new PendingOperation
                {
                    Type = OperationType.ChangeFlags,
                    TargetDiskNumber = 902,
                    TargetDiskPath = mbrDisk.DevicePath,
                    TargetPartitionNumber = mbrDisk.Partitions[0].PartitionNumber,
                    OriginalStartOffset = mbrDisk.Partitions[0].StartingOffset,
                    OriginalLength = mbrDisk.Partitions[0].PartitionLength,
                    NewMbrType = 0x0C,
                    NewMbrBoot = true,
                    Summary = "Mark MBR bootable"
                });
                qmMbr.ApplyAll(null);

                mbrDisk = RawDiskEngine.LoadVirtualDisk(mbrImgPath, 902);
                if (!mbrDisk.Partitions[0].MbrBootable)
                    throw new Exception("Expected MBR partition to be marked active bootable.");

                qmMbr.SetBaselineDisks(new DiskInfo[0], new DiskInfo[] { mbrDisk });
                qmMbr.Enqueue(new PendingOperation
                {
                    Type = OperationType.DeletePartition,
                    TargetDiskNumber = 902,
                    TargetDiskPath = mbrDisk.DevicePath,
                    TargetPartitionNumber = mbrDisk.Partitions[0].PartitionNumber,
                    OriginalStartOffset = mbrDisk.Partitions[0].StartingOffset,
                    OriginalLength = mbrDisk.Partitions[0].PartitionLength,
                    Summary = "Delete active MBR boot partition"
                });
                qmMbr.ApplyAll(null);

                mbrDisk = RawDiskEngine.LoadVirtualDisk(mbrImgPath, 902);
                if (mbrDisk.Partitions.Count != 0)
                    throw new Exception("Expected active MBR boot partition on non-system disk to be deleted.");

                // 9c. Full-drive reinitialization + single-partition formatting across non-OS developer filesystems
                string fmtImgPath = Path.Combine(tempDir, "stress9_fullfmt.img");
                RawDiskEngine.CreateBlankVirtualDisk(fmtImgPath, 32L * 1048576L, PartitionTableStyle.MBR);
                FileSystemType[] devFormats = new FileSystemType[]
                {
                    FileSystemType.Ext4,
                    FileSystemType.Btrfs,
                    FileSystemType.XFS,
                    FileSystemType.HFSPlus,
                    FileSystemType.APFS
                };

                for (int i = 0; i < devFormats.Length; i++)
                {
                    FileSystemType fs = devFormats[i];
                    var fmtDisk = RawDiskEngine.LoadVirtualDisk(fmtImgPath, 904);
                    var qmFmt = new OperationQueueManager();
                    qmFmt.SetBaselineDisks(new DiskInfo[0], new DiskInfo[] { fmtDisk });
                    qmFmt.Enqueue(new PendingOperation
                    {
                        Type = OperationType.CreatePartitionTable,
                        TargetDiskNumber = 904,
                        TargetDiskPath = fmtDisk.DevicePath,
                        NewTableStyle = PartitionTableStyle.GPT,
                        Summary = "Reinit GPT for " + fs
                    });
                    qmFmt.Enqueue(new PendingOperation
                    {
                        Type = OperationType.CreatePartition,
                        TargetDiskNumber = 904,
                        TargetDiskPath = fmtDisk.DevicePath,
                        StartOffset = 1048576L,
                        Length = 28L * 1048576L,
                        NewFileSystem = fs,
                        NewLabel = "DEV_" + fs.ToString().ToUpperInvariant(),
                        NewPartitionName = fs.ToString(),
                        NewGptTypeGuid = GptPartitionTypeGuids.ForFileSystem(fs),
                        NewMbrType = RawDiskEngine.GetDefaultMbrType(fs),
                        Summary = "Full-drive create " + fs
                    });
                    qmFmt.ApplyAll(null);

                    fmtDisk = RawDiskEngine.LoadVirtualDisk(fmtImgPath, 904);
                    if (fmtDisk.PartitionStyle != PartitionTableStyle.GPT || fmtDisk.Partitions.Count != 1 || fmtDisk.Partitions[0].FileSystem != fs)
                        throw new Exception("Full-drive reformat verification failed for " + fs);

                    var part = fmtDisk.Partitions[0];
                    var vol = ReadOnlyFileSystemMountEngine.MountReadOnly(fmtDisk, part, true);
                    if (FindFsNodeByPath(vol.RootNode, "/probe.txt") != null)
                        throw new Exception("Stale overlay file /probe.txt survived full-drive reformat to " + fs);
                    ReadOnlyFileSystemMountEngine.SetExperimentalWriteMode(vol, true);
                    string payloadText = "rw-probe-" + fs;
                    var node = ReadOnlyFileSystemMountEngine.WriteFileToVolume(fmtDisk, part, vol, null, "probe.txt", Encoding.UTF8.GetBytes(payloadText));
                    byte[] gotBytes = ReadOnlyFileSystemMountEngine.ReadFileBytes(fmtDisk, part, node);
                    if (Encoding.UTF8.GetString(gotBytes) != payloadText)
                        throw new Exception("Experimental write roundtrip mismatch for " + fs);
                }

                // 9d. Simulated isohybrid disk: Partition 1 (ISO9660 at +0x8000, 24 MiB) overlaps nested Partition 2 (ESP at +0x20000, 2 MiB)
                string hybridImgPath = Path.Combine(tempDir, "stress9_isohybrid.img");
                var hybridDisk = RawDiskEngine.CreateBlankVirtualDisk(hybridImgPath, 32L * 1048576L, PartitionTableStyle.GPT);
                hybridDisk.DiskNumber = 903;

                var pIso = new PartitionEntry
                {
                    PartitionNumber = 1,
                    StartingOffset = 0x8000L,
                    PartitionLength = 24L * 1048576L,
                    FileSystem = FileSystemType.ISO9660,
                    Label = "FEDORA_LIVE",
                    PartitionName = "FEDORA_LIVE"
                };
                var pEsp = new PartitionEntry
                {
                    PartitionNumber = 2,
                    StartingOffset = 0x20000L,
                    PartitionLength = 2L * 1048576L,
                    FileSystem = FileSystemType.EfiSystem,
                    Label = "ANACONDA",
                    PartitionName = "ANACONDA",
                    IsSystemOrBoot = false
                };
                hybridDisk.Partitions.Add(pIso);
                RawDiskEngine.WriteFileSystemHeader(hybridDisk, pIso);
                hybridDisk.Partitions.Add(pEsp);

                long sentinelOffset = pIso.StartingOffset + 0x28000L; // 0x30000, inside nested pEsp [0x20000..0x220000)
                byte[] sentinel = Encoding.ASCII.GetBytes("ANACONDA_ESP_SENTINEL_UNTOUCHED_0x30000");
                RawDiskEngine.WriteBytes(hybridDisk, sentinelOffset, sentinel);

                var volIso = ReadOnlyFileSystemMountEngine.MountReadOnly(hybridDisk, pIso, true);
                ReadOnlyFileSystemMountEngine.SetExperimentalWriteMode(volIso, true);
                var liveOsDir = ReadOnlyFileSystemMountEngine.CreateDirectoryOnVolume(hybridDisk, pIso, volIso, null, "LiveOS");
                ReadOnlyFileSystemMountEngine.CreateDirectoryOnVolume(hybridDisk, pIso, volIso, liveOsDir, "overlay");
                const string ksText = "lang en_US.UTF-8\n";
                ReadOnlyFileSystemMountEngine.WriteFileToVolume(hybridDisk, pIso, volIso, liveOsDir, "ks.cfg", Encoding.UTF8.GetBytes(ksText));

                if (volIso.OverlayTableRelOffset == 0x20000L || volIso.OverlayTableRelOffset == 0x28000L || volIso.OverlayTableRelOffset <= pEsp.EndOffset - pIso.StartingOffset)
                    throw new Exception(string.Format("Expected non-overlapping tail overlay offset past nested ESP, got 0x{0:X}", volIso.OverlayTableRelOffset));

                byte[] sentinelAfter = RawDiskEngine.ReadBytes(hybridDisk, sentinelOffset, sentinel.Length);
                if (Encoding.ASCII.GetString(sentinelAfter) != Encoding.ASCII.GetString(sentinel))
                    throw new Exception("Nested isohybrid ESP sectors at 0x30000 were overwritten by ISO overlay!");

                ReadOnlyFileSystemMountEngine.Unmount(hybridDisk.DiskNumber, pIso.StartingOffset);
                var volIsoRemount = ReadOnlyFileSystemMountEngine.MountReadOnly(hybridDisk, pIso, false);
                var ksHits = ReadOnlyFileSystemMountEngine.SearchVolume(volIsoRemount, "ks.cfg");
                if (ksHits.Count == 0)
                    throw new Exception("Expected /LiveOS/ks.cfg in remounted isohybrid overlay!");
                byte[] ksGot = ReadOnlyFileSystemMountEngine.ReadFileBytes(hybridDisk, pIso, ksHits[0]);
                if (Encoding.UTF8.GetString(ksGot) != ksText)
                    throw new Exception("Remounted /LiveOS/ks.cfg content mismatch: " + Encoding.UTF8.GetString(ksGot));
            }
            finally
            {
                ReadOnlyFileSystemMountEngine.ShadowOverlayRootOverride = null;
                ReadOnlyFileSystemMountEngine.UnmountAll();
            }

            log("  -> Boot protection, non-system EFI/MBR editing, full-drive formatting, and isohybrid tail overlay verified.");
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

        private static byte[] BuildZlibDecmpfsValue(byte[] plainBytes)
        {
            byte[] deflatePayload;
            using (var ms = new MemoryStream())
            {
                ms.WriteByte(0x78);
                ms.WriteByte(0x9C);
                using (var def = new DeflateStream(ms, CompressionMode.Compress, true))
                {
                    def.Write(plainBytes, 0, plainBytes.Length);
                }
                deflatePayload = ms.ToArray();
            }

            // j_xattr_val_t: flags(2)=2 (XATTR_DATA_EMBEDDED) | xdata_len(2) | decmpfs hdr(16) + payload
            int xdataLen = 16 + deflatePayload.Length;
            byte[] val = new byte[4 + xdataLen];
            BitConverter.GetBytes((ushort)2).CopyTo(val, 0);
            BitConverter.GetBytes((ushort)xdataLen).CopyTo(val, 2);
            BitConverter.GetBytes(0x636D7066U).CopyTo(val, 4 + 0); // 'fpmc'
            BitConverter.GetBytes(3U).CopyTo(val, 4 + 4);          // type 3: inline zlib
            BitConverter.GetBytes((ulong)plainBytes.Length).CopyTo(val, 4 + 8);
            Array.Copy(deflatePayload, 0, val, 4 + 16, deflatePayload.Length);
            return val;
        }

        /// <summary>
        /// Verifies real-world APFS container structures with zero synthetic WinParted hints:
        /// checkpoint descriptor ring superblock discovery (stale block 0 vs newer o_xid checkpoint),
        /// 2-level Container OMAP + Volume OMAP + 2-level FS-Tree, clone private_id multi-extent + sparse hole export,
        /// inline zlib <c>com.apple.decmpfs</c> decompression, and dual-volume Macintosh HD + Data merging.
        /// </summary>
        private static void VerifyAdvancedApfsContainerExport(string tempDir, Action<string> log)
        {
            const long DiskBytes = 32L * 1048576L;
            const int BlockSize = 4096;
            const ulong TotalBlocks = 7168UL; // 28 MiB partition

            string apfsImg = Path.Combine(tempDir, "stress8_apfs_advanced.img");
            var disk = RawDiskEngine.CreateBlankVirtualDisk(apfsImg, DiskBytes, PartitionTableStyle.GPT);
            disk.DiskNumber = 96;

            long partStart = 1 * MiB;
            long partLen = 28 * MiB;
            var qm = new OperationQueueManager();
            qm.SetBaselineDisks(null, new[] { disk });
            qm.Enqueue(MakeCreateOp(96, apfsImg, partStart, partLen, FileSystemType.APFS, "InitApfs"));
            qm.ApplyAll();

            disk = RawDiskEngine.LoadVirtualDisk(apfsImg, 96);
            var apfsPart = disk.Partitions[0];

            // Wipe the entire partition header completely clean so zero synthetic WPROFS01 / WPL: / WPU: hints remain
            byte[] zeroHdr = new byte[128 * 1024];
            RawDiskEngine.WriteBytes(disk, apfsPart.StartingOffset, zeroHdr);

            // Prepare container metadata blocks 0..49
            byte[] meta = new byte[50 * BlockSize];
            byte[] fsUuidBytes = Guid.NewGuid().ToByteArray();

            // Block 0: Stale NXSB (o_xid = 1, empty fs_oid[]) pointing to checkpoint descriptor ring blocks 2..5
            BitConverter.GetBytes(1UL).CopyTo(meta, 0x08);
            BitConverter.GetBytes(1UL).CopyTo(meta, 0x10);
            BitConverter.GetBytes(0x80000001U).CopyTo(meta, 0x18);
            Encoding.ASCII.GetBytes("NXSB").CopyTo(meta, 0x20);
            BitConverter.GetBytes((uint)BlockSize).CopyTo(meta, 0x24);
            BitConverter.GetBytes(TotalBlocks).CopyTo(meta, 0x28);
            fsUuidBytes.CopyTo(meta, 0x48);
            BitConverter.GetBytes(4U).CopyTo(meta, 0x68);   // nx_xp_desc_blocks = 4 (blocks 2..5)
            BitConverter.GetBytes(2L).CopyTo(meta, 0x70);   // nx_xp_desc_base = block 2
            BitConverter.GetBytes(100U).CopyTo(meta, 0xB4); // nx_max_file_systems
            ReadOnlyFileSystemMountEngine.WriteApfsFletcher64(meta, 0, BlockSize);

            // Block 4 (in checkpoint ring): Live latest NXSB (o_xid = 5) pointing to Container OMAP at block 10 and two volumes (0x402, 0x403)
            int ckOff = 4 * BlockSize;
            Array.Copy(meta, 0, meta, ckOff, BlockSize);
            BitConverter.GetBytes(5UL).CopyTo(meta, ckOff + 0x10);      // o_xid = 5
            BitConverter.GetBytes(10UL).CopyTo(meta, ckOff + 0xA0);     // nx_omap_oid = 10
            BitConverter.GetBytes(0x402UL).CopyTo(meta, ckOff + 0xB8);  // nx_fs_oid[0] = Macintosh HD
            BitConverter.GetBytes(0x403UL).CopyTo(meta, ckOff + 0xC0);  // nx_fs_oid[1] = Macintosh HD - Data
            ReadOnlyFileSystemMountEngine.WriteApfsFletcher64(meta, ckOff, BlockSize);

            // Block 10: Container OMAP phys header -> 2-level OMAP root internal node at block 11
            RawDiskEngine.WriteApfsOmapHeaderBlock(meta, 10 * BlockSize, 10UL, 5UL, 11UL);

            // Block 11: Container OMAP internal node (btn_level = 1) -> leaf1 (block 12) for <=0x402, leaf2 (block 13) for >=0x403
            var contOmapIdx = new List<RawDiskEngine.ApfsBTreeRecord>
            {
                new RawDiskEngine.ApfsBTreeRecord { Key = RawDiskEngine.BuildApfsOmapKey(0x402UL, 1UL), Val = BitConverter.GetBytes(12UL) },
                new RawDiskEngine.ApfsBTreeRecord { Key = RawDiskEngine.BuildApfsOmapKey(0x403UL, 1UL), Val = BitConverter.GetBytes(13UL) }
            };
            RawDiskEngine.WriteApfsBTreeNodeBlock(meta, 11 * BlockSize, BlockSize, 11UL, 5UL, 0x40000002U, 0x000BU, 0x0005, 1, 16U, 16U, 3UL, contOmapIdx);

            // Block 12: Container OMAP leaf1 (old deleted tombstone xid=4 for 0x402? no, xid=4 -> block 999, newer xid=5 -> block 20!)
            var contOmapLeaf1 = new List<RawDiskEngine.ApfsBTreeRecord>
            {
                new RawDiskEngine.ApfsBTreeRecord { Key = RawDiskEngine.BuildApfsOmapKey(0x402UL, 4UL), Val = RawDiskEngine.BuildApfsOmapVal(0U, 4096U, 999UL) },
                new RawDiskEngine.ApfsBTreeRecord { Key = RawDiskEngine.BuildApfsOmapKey(0x402UL, 5UL), Val = RawDiskEngine.BuildApfsOmapVal(0U, 4096U, 20UL) }
            };
            RawDiskEngine.WriteApfsBTreeNodeBlock(meta, 12 * BlockSize, BlockSize, 12UL, 5UL, 0x40000003U, 0x000BU, 0x0006, 0, 16U, 16U, 0UL, contOmapLeaf1);

            // Block 13: Container OMAP leaf2 -> 0x403 @ xid 5 -> block 30
            var contOmapLeaf2 = new List<RawDiskEngine.ApfsBTreeRecord>
            {
                new RawDiskEngine.ApfsBTreeRecord { Key = RawDiskEngine.BuildApfsOmapKey(0x403UL, 5UL), Val = RawDiskEngine.BuildApfsOmapVal(0U, 4096U, 30UL) }
            };
            RawDiskEngine.WriteApfsBTreeNodeBlock(meta, 13 * BlockSize, BlockSize, 13UL, 5UL, 0x40000003U, 0x000BU, 0x0006, 0, 16U, 16U, 0UL, contOmapLeaf2);

            // ---------------- Volume 0: "Macintosh HD" at block 20 (with 2-level FS-Tree) ----------------
            int apsb0 = 20 * BlockSize;
            BitConverter.GetBytes(0x402UL).CopyTo(meta, apsb0 + 0x08);
            BitConverter.GetBytes(5UL).CopyTo(meta, apsb0 + 0x10);
            BitConverter.GetBytes(0x8000000DU).CopyTo(meta, apsb0 + 0x18);
            Encoding.ASCII.GetBytes("APSB").CopyTo(meta, apsb0 + 0x20);
            BitConverter.GetBytes(40UL).CopyTo(meta, apsb0 + 0x58);       // alloc count
            BitConverter.GetBytes(21UL).CopyTo(meta, apsb0 + 0x80);       // apfs_omap_oid = 21
            BitConverter.GetBytes(0x500UL).CopyTo(meta, apsb0 + 0x88);    // apfs_root_tree_oid = 0x500 (virtual internal root)
            Guid.NewGuid().ToByteArray().CopyTo(meta, apsb0 + 0xF0);
            BitConverter.GetBytes(1UL).CopyTo(meta, apsb0 + 0x108);       // APFS_FS_UNENCRYPTED
            Encoding.UTF8.GetBytes("Macintosh HD").CopyTo(meta, apsb0 + 0x2C0);
            ReadOnlyFileSystemMountEngine.WriteApfsFletcher64(meta, apsb0, BlockSize);

            // Volume 0 OMAP @ block 21 -> leaf @ block 22 mapping FS-Tree nodes 0x500->23, 0x501->24, 0x502->25
            RawDiskEngine.WriteApfsOmapHeaderBlock(meta, 21 * BlockSize, 21UL, 5UL, 22UL);
            var vol0OmapRecs = new List<RawDiskEngine.ApfsBTreeRecord>
            {
                new RawDiskEngine.ApfsBTreeRecord { Key = RawDiskEngine.BuildApfsOmapKey(0x500UL, 5UL), Val = RawDiskEngine.BuildApfsOmapVal(0U, 4096U, 23UL) },
                new RawDiskEngine.ApfsBTreeRecord { Key = RawDiskEngine.BuildApfsOmapKey(0x501UL, 5UL), Val = RawDiskEngine.BuildApfsOmapVal(0U, 4096U, 24UL) },
                new RawDiskEngine.ApfsBTreeRecord { Key = RawDiskEngine.BuildApfsOmapKey(0x502UL, 5UL), Val = RawDiskEngine.BuildApfsOmapVal(0U, 4096U, 25UL) }
            };
            RawDiskEngine.WriteApfsBTreeNodeBlock(meta, 22 * BlockSize, BlockSize, 22UL, 5UL, 0x40000002U, 0x000BU, 0x0007, 0, 16U, 16U, 1UL, vol0OmapRecs);

            // Volume 0 FS-Tree Internal Root @ block 23 (0x500, btn_level=1) -> 0x501 (leaf 1) and 0x502 (leaf 2)
            var fs0IdxRecs = new List<RawDiskEngine.ApfsBTreeRecord>
            {
                new RawDiskEngine.ApfsBTreeRecord { Key = BitConverter.GetBytes(2UL | (3UL << 60)), Val = BitConverter.GetBytes(0x501UL) },
                new RawDiskEngine.ApfsBTreeRecord { Key = BitConverter.GetBytes(30UL | (3UL << 60)), Val = BitConverter.GetBytes(0x502UL) }
            };
            RawDiskEngine.WriteApfsBTreeNodeBlock(meta, 23 * BlockSize, BlockSize, 0x500UL, 5UL, 0x80000002U, 0x000EU, 0x0009, 1, 0U, 0U, 3UL, fs0IdxRecs);

            // Prepare zlib decmpfs content for /CompressedNotes.txt (inode 20)
            string compressedPlainText = "APFS Transparent zlib com.apple.decmpfs test payload: " + new string('Z', 240) + "-END-APFS-DECMPFS!";
            byte[] compressedPlainBytes = Encoding.UTF8.GetBytes(compressedPlainText);

            // Volume 0 FS-Tree Leaf 1 @ block 24 (0x501): root(2), Users(16 empty stub), CompressedNotes.txt(20), DirRecs
            var fs0Leaf1 = new List<RawDiskEngine.ApfsBTreeRecord>();
            RawDiskEngine.AddApfsDirInode(fs0Leaf1, 2UL, 1UL, "Macintosh HD", 0x41ED);
            RawDiskEngine.AddApfsDirInode(fs0Leaf1, 16UL, 2UL, "Users", 0x41ED);

            // Inode 20: CompressedNotes.txt with bsd_flags=0x20 (UF_COMPRESSED) & xattr com.apple.decmpfs
            RawDiskEngine.AddApfsFileRecords(fs0Leaf1, 20UL, 2UL, "CompressedNotes.txt", 0UL, 0UL, 0L);
            byte[] xattrKey = new byte[10 + "com.apple.decmpfs\0".Length];
            BitConverter.GetBytes(20UL | (4UL << 60)).CopyTo(xattrKey, 0);
            byte[] xNameBytes = Encoding.UTF8.GetBytes("com.apple.decmpfs\0");
            BitConverter.GetBytes((ushort)xNameBytes.Length).CopyTo(xattrKey, 8);
            Array.Copy(xNameBytes, 0, xattrKey, 10, xNameBytes.Length);
            fs0Leaf1.Add(new RawDiskEngine.ApfsBTreeRecord
            {
                SortObjId = 20UL,
                SortRecType = 4,
                Key = xattrKey,
                Val = BuildZlibDecmpfsValue(compressedPlainBytes)
            });

            RawDiskEngine.AddApfsDirRec(fs0Leaf1, 2UL, "Users", 16UL, 4);
            RawDiskEngine.AddApfsDirRec(fs0Leaf1, 2UL, "CompressedNotes.txt", 20UL, 8);
            RawDiskEngine.AddApfsDirRec(fs0Leaf1, 2UL, "LargeSparseMultiExtent.bin", 30UL, 8);
            fs0Leaf1.Sort(delegate(RawDiskEngine.ApfsBTreeRecord a, RawDiskEngine.ApfsBTreeRecord b)
            {
                int c = a.SortObjId.CompareTo(b.SortObjId);
                return (c != 0) ? c : a.SortRecType.CompareTo(b.SortRecType);
            });
            RawDiskEngine.WriteApfsBTreeNodeBlock(meta, 24 * BlockSize, BlockSize, 0x501UL, 5UL, 0x80000003U, 0x000EU, 0x000A, 0, 0U, 0U, 0UL, fs0Leaf1);

            // Volume 0 FS-Tree Leaf 2 @ block 25 (0x502): Inode 30 with clone private_id = 9001 and non-contiguous extents + sparse hole
            const ulong clonePrivId = 9001UL;
            const long ext1Bytes = 65536L;  // phys blocks 100..115
            const long holeBytes = 8192L;   // sparse zero hole [65536..73727]
            const long ext2Bytes = 65536L;  // phys blocks 200..215
            const long largeFileTotal = ext1Bytes + holeBytes + ext2Bytes; // 139,264 bytes

            var fs0Leaf2 = new List<RawDiskEngine.ApfsBTreeRecord>();
            RawDiskEngine.AddApfsFileRecords(fs0Leaf2, 30UL, 2UL, "LargeSparseMultiExtent.bin", (ulong)largeFileTotal, 0UL, 0L);
            // Patch private_id of inode 30 record to clonePrivId (9001) to prove clone private_id extent lookup
            BitConverter.GetBytes(clonePrivId).CopyTo(fs0Leaf2[0].Val, 0x08);
            RawDiskEngine.AddApfsFileExtent(fs0Leaf2, clonePrivId, 0UL, ext1Bytes, 100UL);
            RawDiskEngine.AddApfsFileExtent(fs0Leaf2, clonePrivId, (ulong)(ext1Bytes + holeBytes), ext2Bytes, 200UL);
            fs0Leaf2.Sort(delegate(RawDiskEngine.ApfsBTreeRecord a, RawDiskEngine.ApfsBTreeRecord b)
            {
                int c = a.SortObjId.CompareTo(b.SortObjId);
                return (c != 0) ? c : a.SortRecType.CompareTo(b.SortRecType);
            });
            RawDiskEngine.WriteApfsBTreeNodeBlock(meta, 25 * BlockSize, BlockSize, 0x502UL, 5UL, 0x80000003U, 0x000EU, 0x000A, 0, 0U, 0U, 0UL, fs0Leaf2);

            // ---------------- Volume 1: "Macintosh HD - Data" at block 30 ----------------
            int apsb1 = 30 * BlockSize;
            BitConverter.GetBytes(0x403UL).CopyTo(meta, apsb1 + 0x08);
            BitConverter.GetBytes(5UL).CopyTo(meta, apsb1 + 0x10);
            BitConverter.GetBytes(0x8000000DU).CopyTo(meta, apsb1 + 0x18);
            Encoding.ASCII.GetBytes("APSB").CopyTo(meta, apsb1 + 0x20);
            BitConverter.GetBytes(10UL).CopyTo(meta, apsb1 + 0x58);
            BitConverter.GetBytes(31UL).CopyTo(meta, apsb1 + 0x80);
            BitConverter.GetBytes(0x600UL).CopyTo(meta, apsb1 + 0x88);
            Guid.NewGuid().ToByteArray().CopyTo(meta, apsb1 + 0xF0);
            BitConverter.GetBytes(1UL).CopyTo(meta, apsb1 + 0x108);
            Encoding.UTF8.GetBytes("Macintosh HD - Data").CopyTo(meta, apsb1 + 0x2C0);
            ReadOnlyFileSystemMountEngine.WriteApfsFletcher64(meta, apsb1, BlockSize);

            RawDiskEngine.WriteApfsOmapHeaderBlock(meta, 31 * BlockSize, 31UL, 5UL, 32UL);
            var vol1OmapRecs = new List<RawDiskEngine.ApfsBTreeRecord>
            {
                new RawDiskEngine.ApfsBTreeRecord { Key = RawDiskEngine.BuildApfsOmapKey(0x600UL, 5UL), Val = RawDiskEngine.BuildApfsOmapVal(0U, 4096U, 33UL) }
            };
            RawDiskEngine.WriteApfsBTreeNodeBlock(meta, 32 * BlockSize, BlockSize, 32UL, 5UL, 0x40000002U, 0x000BU, 0x0007, 0, 16U, 16U, 1UL, vol1OmapRecs);

            string dataVolTxt = "APFS Data Volume User Document (Merged Firmlink /Users/tester/notes.md)\n";
            byte[] dataVolBytes = Encoding.UTF8.GetBytes(dataVolTxt);
            var fs1Leaf = new List<RawDiskEngine.ApfsBTreeRecord>();
            RawDiskEngine.AddApfsDirInode(fs1Leaf, 2UL, 1UL, "Macintosh HD - Data", 0x41ED);
            RawDiskEngine.AddApfsDirInode(fs1Leaf, 40UL, 2UL, "Users", 0x41ED);
            RawDiskEngine.AddApfsDirInode(fs1Leaf, 41UL, 40UL, "tester", 0x41ED);
            RawDiskEngine.AddApfsFileRecords(fs1Leaf, 42UL, 41UL, "notes.md", (ulong)dataVolBytes.Length, 45UL, 4096L);
            RawDiskEngine.AddApfsDirRec(fs1Leaf, 2UL, "Users", 40UL, 4);
            RawDiskEngine.AddApfsDirRec(fs1Leaf, 40UL, "tester", 41UL, 4);
            RawDiskEngine.AddApfsDirRec(fs1Leaf, 41UL, "notes.md", 42UL, 8);
            fs1Leaf.Sort(delegate(RawDiskEngine.ApfsBTreeRecord a, RawDiskEngine.ApfsBTreeRecord b)
            {
                int c = a.SortObjId.CompareTo(b.SortObjId);
                return (c != 0) ? c : a.SortRecType.CompareTo(b.SortRecType);
            });
            RawDiskEngine.WriteApfsBTreeNodeBlock(meta, 33 * BlockSize, BlockSize, 0x600UL, 5UL, 0x80000002U, 0x000EU, 0x000B, 0, 0U, 0U, 1UL, fs1Leaf);
            Array.Copy(dataVolBytes, 0, meta, 45 * BlockSize, dataVolBytes.Length);

            // Write metadata blocks 0..49
            RawDiskEngine.WriteBytes(disk, apfsPart.StartingOffset, meta);

            // Write LargeSparseMultiExtent.bin extent1 (blocks 100..115) & extent2 (blocks 200..215)
            byte[] ext1Buf = new byte[ext1Bytes];
            for (int i = 0; i < ext1Buf.Length; i++) ext1Buf[i] = (byte)(0xA1 ^ (i & 0x3F));
            Encoding.ASCII.GetBytes("APFS-EXTENT-1-START").CopyTo(ext1Buf, 0);

            byte[] ext2Buf = new byte[ext2Bytes];
            for (int i = 0; i < ext2Buf.Length; i++) ext2Buf[i] = (byte)(0x5B ^ (i & 0x1F));
            Encoding.ASCII.GetBytes("APFS-EXTENT-2-TAIL!").CopyTo(ext2Buf, ext2Buf.Length - 19);

            RawDiskEngine.WriteBytes(disk, apfsPart.StartingOffset + 100L * BlockSize, ext1Buf);
            RawDiskEngine.WriteBytes(disk, apfsPart.StartingOffset + 200L * BlockSize, ext2Buf);

            // Reload virtual disk from scratch and assert pure metadata inspection
            disk = RawDiskEngine.LoadVirtualDisk(apfsImg, 96);
            apfsPart = disk.Partitions[0];
            if (apfsPart.FileSystem != FileSystemType.APFS)
                throw new Exception("Advanced APFS image was not detected as APFS.");
            if (apfsPart.Label != "Macintosh HD")
                throw new Exception("Advanced APFS checkpoint/OMAP label extraction failed: got '" + apfsPart.Label + "'");

            var vol = ReadOnlyFileSystemMountEngine.MountReadOnly(disk, apfsPart, true);
            try
            {
                // Verify decmpfs zlib-compressed file
                VerifyRoMountAndFile(disk, apfsPart, "/CompressedNotes.txt", "-END-APFS-DECMPFS!");
                var decNode = FindFsNodeByPath(vol.RootNode, "/CompressedNotes.txt");
                byte[] gotDec = ReadOnlyFileSystemMountEngine.ReadFileBytes(disk, apfsPart, decNode, compressedPlainBytes.Length + 64);
                if (gotDec.Length != compressedPlainBytes.Length || Encoding.UTF8.GetString(gotDec) != compressedPlainText)
                    throw new Exception("APFS com.apple.decmpfs zlib decompression payload mismatch.");

                // Verify second volume merged firmlink + /Volumes/<VolName> browsing
                VerifyRoMountAndFile(disk, apfsPart, "/Users/tester/notes.md", "Merged Firmlink");
                VerifyRoMountAndFile(disk, apfsPart, "/Volumes/Macintosh HD - Data/Users/tester/notes.md", "Merged Firmlink");

                // Export file to host folder and verify multi-extent + sparse zero hole byte-for-byte
                string exportRoot = Path.Combine(tempDir, "host_export_apfs_advanced");
                Directory.CreateDirectory(exportRoot);
                int fc, dc;
                long bc;
                var largeNode = FindFsNodeByPath(vol.RootNode, "/LargeSparseMultiExtent.bin");
                if (largeNode == null || largeNode.SizeBytes != largeFileTotal)
                    throw new Exception("Advanced APFS /LargeSparseMultiExtent.bin size mismatch.");
                ReadOnlyFileSystemMountEngine.ExportNodeToHost(disk, apfsPart, largeNode, exportRoot, out fc, out dc, out bc);

                string exportedPath = Path.Combine(exportRoot, "LargeSparseMultiExtent.bin");
                byte[] exported = File.ReadAllBytes(exportedPath);
                if (exported.Length != largeFileTotal)
                    throw new Exception("APFS sparse/multi-extent exported size mismatch: " + exported.Length);
                for (int i = 0; i < ext1Buf.Length; i++)
                    if (exported[i] != ext1Buf[i])
                        throw new Exception("APFS extent 1 mismatch at offset " + i);
                for (int i = 0; i < holeBytes; i++)
                    if (exported[ext1Buf.Length + i] != 0)
                        throw new Exception("APFS sparse zero hole corrupted at offset " + i);
                for (int i = 0; i < ext2Buf.Length; i++)
                    if (exported[ext1Buf.Length + (int)holeBytes + i] != ext2Buf[i])
                        throw new Exception("APFS extent 2 mismatch at offset " + i);
            }
            finally
            {
                ReadOnlyFileSystemMountEngine.Unmount(disk.DiskNumber, apfsPart.StartingOffset);
            }

            log("  -> Advanced APFS Verified (checkpoint xid=5, 2-level OMAP & FS-Tree, clone private_id sparse multi-extent, zlib decmpfs, multi-volume).");
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

        private static void AssertBytesUniform(byte[] data, byte expectedValue, string context)
        {
            for (int i = 0; i < data.Length; i++)
            {
                if (data[i] != expectedValue)
                    throw new Exception(string.Format("Secure wipe byte mismatch in {0} at +0x{1:X}: expected 0x{2:X2}, got 0x{3:X2}.",
                        context, i, expectedValue, data[i]));
            }
        }

        private static void AssertHighEntropyRandom(byte[] sample, string context)
        {
            int[] counts = new int[256];
            for (int i = 0; i < sample.Length; i++)
                counts[sample[i]]++;

            int distinct = 0;
            int maxFreq = 0;
            for (int b = 0; b < 256; b++)
            {
                if (counts[b] > 0) distinct++;
                if (counts[b] > maxFreq) maxFreq = counts[b];
            }

            if (distinct < 240 || maxFreq >= 512)
                throw new Exception(string.Format("Cryptographic random wipe failed entropy check in {0}: distinct={1}/256, maxFreq={2}/65536.",
                    context, distinct, maxFreq));
        }

        /// <summary>
        /// Stress 10: Verifies that Secure Wipe (Zero, Ones, CryptoRandom with entropy checks),
        /// partition Reformat, and Demo Disk Reset completely purge on-disk data, <c>WPRWOVL1</c>
        /// overlays, host shadow files, and in-memory cached mounts.
        /// </summary>
        private static void RunStress10_SecureWipeFormatAndDemoResetOverlayPurge(string tempDir, Action<string> log)
        {
            log("[Stress 10] Secure Wipe (Zero/Ones/CryptoRandom), Reformat Overlay Purge, and Demo Disk Reset Completeness...");

            string shadowDir = Path.Combine(tempDir, "stress10_shadow");
            Directory.CreateDirectory(shadowDir);
            ReadOnlyFileSystemMountEngine.ShadowOverlayRootOverride = shadowDir;
            try
            {
                // 10a. Secure Wipe (ZeroFill, OnesFill, RandomDoD) must clear superblocks, WPRWOVL1 overlays, host shadow overlays, and RAM ActiveMounts
                WipePattern[] patterns = new WipePattern[]
                {
                    WipePattern.ZeroFill,
                    WipePattern.OnesFill,
                    WipePattern.RandomDoD
                };

                for (int i = 0; i < patterns.Length; i++)
                {
                    WipePattern pat = patterns[i];
                    int diskNum = 905 + i;
                    string imgPath = Path.Combine(tempDir, "stress10_wipe_" + pat + ".img");
                    var disk = RawDiskEngine.CreateBlankVirtualDisk(imgPath, 36L * MiB, PartitionTableStyle.GPT);
                    disk.DiskNumber = diskNum;

                    var qm = new OperationQueueManager();
                    qm.SetBaselineDisks(new DiskInfo[0], new DiskInfo[] { disk });
                    qm.Enqueue(MakeCreateOp(diskNum, imgPath, 1L * MiB, 16L * MiB, FileSystemType.Ext4, "WIPE_EXT4"));
                    qm.Enqueue(MakeCreateOp(diskNum, imgPath, 17L * MiB, 16L * MiB, FileSystemType.HFSPlus, "WIPE_HFS"));
                    qm.ApplyAll(null);

                    disk = RawDiskEngine.LoadVirtualDisk(imgPath, diskNum);
                    var pExt = disk.Partitions[0];
                    var pHfs = disk.Partitions[1];

                    // Populate both partitions and keep BOTH mounted in ActiveMounts
                    var volExt = ReadOnlyFileSystemMountEngine.MountReadOnly(disk, pExt, true);
                    ReadOnlyFileSystemMountEngine.SetExperimentalWriteMode(volExt, true);
                    var secDir = ReadOnlyFileSystemMountEngine.CreateDirectoryOnVolume(disk, pExt, volExt, volExt.RootNode, "secret_dir");
                    ReadOnlyFileSystemMountEngine.WriteFileToVolume(disk, pExt, volExt, secDir, "classified.txt", Encoding.UTF8.GetBytes("TOP_SECRET_EXT4_PAYLOAD_" + pat));

                    string extShadowPath = Path.Combine(shadowDir, "ovl_d" + diskNum + "_" + pExt.StartingOffset.ToString("X") + "_deadbeef.bin");
                    File.WriteAllBytes(extShadowPath, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });

                    var volHfs = ReadOnlyFileSystemMountEngine.MountReadOnly(disk, pHfs, true);
                    ReadOnlyFileSystemMountEngine.SetExperimentalWriteMode(volHfs, true);
                    ReadOnlyFileSystemMountEngine.WriteFileToVolume(disk, pHfs, volHfs, volHfs.RootNode, "hfs_keep.txt", Encoding.UTF8.GetBytes("HFS_INTACT_PAYLOAD_" + pat));
                    ReadOnlyFileSystemMountEngine.Unmount(diskNum, pHfs.StartingOffset);

                    // Enqueue WipePartition ONLY on Partition 1 (Ext4) while still mounted
                    qm = new OperationQueueManager();
                    qm.SetBaselineDisks(new DiskInfo[0], new DiskInfo[] { disk });
                    qm.Enqueue(new PendingOperation
                    {
                        Type = OperationType.WipePartition,
                        TargetDiskNumber = diskNum,
                        TargetDiskPath = disk.DevicePath,
                        TargetPartitionNumber = pExt.PartitionNumber,
                        OriginalStartOffset = pExt.StartingOffset,
                        OriginalLength = pExt.PartitionLength,
                        WipePattern = pat,
                        Summary = "Wipe Ext4 " + pat
                    });
                    qm.ApplyAll(null);

                    if (ReadOnlyFileSystemMountEngine.GetMount(diskNum, pExt.StartingOffset) != null)
                        throw new Exception("ActiveMounts cache was NOT evicted after WipePartition (" + pat + ").");
                    if (File.Exists(extShadowPath))
                        throw new Exception("Host shadow overlay file survived WipePartition (" + pat + ").");

                    disk = RawDiskEngine.LoadVirtualDisk(imgPath, diskNum);
                    if (disk.Partitions.Count != 2)
                        throw new Exception("Unexpected partition count after WipePartition (" + pat + "): " + disk.Partitions.Count);

                    pExt = disk.Partitions[0];
                    pHfs = disk.Partitions[1];
                    if (pExt.FileSystem != FileSystemType.RAW || !string.IsNullOrEmpty(pExt.Label) || !string.IsNullOrEmpty(pExt.PartitionName))
                        throw new Exception("Wiped partition metadata not reset to clean RAW (" + pat + "): FS=" + pExt.FileSystem + ", Label=" + pExt.Label);

                    long[] sampleOffsets = new long[]
                    {
                        pExt.StartingOffset,
                        pExt.StartingOffset + 0x20000L,
                        pExt.StartingOffset + 8L * MiB,
                        pExt.StartingOffset + pExt.PartitionLength - 65536L
                    };
                    for (int s = 0; s < sampleOffsets.Length; s++)
                    {
                        byte[] raw = RawDiskEngine.ReadBytes(disk, sampleOffsets[s], 65536);
                        string ctx = pat + " @ +0x" + (sampleOffsets[s] - pExt.StartingOffset).ToString("X");
                        if (pat == WipePattern.ZeroFill)
                            AssertBytesUniform(raw, 0x00, ctx);
                        else if (pat == WipePattern.OnesFill)
                            AssertBytesUniform(raw, 0xFF, ctx);
                        else
                            AssertHighEntropyRandom(raw, ctx);
                    }

                    // Neighbor HFS+ partition must remain completely untouched
                    VerifyRoMountAndFile(disk, pHfs, "/hfs_keep.txt", "HFS_INTACT_PAYLOAD_" + pat);

                    // Even if metadata is spoofed back to Ext4, MountReadOnly MUST reject a wiped partition lacking a superblock
                    var spoofedExt = new PartitionEntry
                    {
                        DiskNumber = pExt.DiskNumber,
                        PartitionNumber = pExt.PartitionNumber,
                        StartingOffset = pExt.StartingOffset,
                        PartitionLength = pExt.PartitionLength,
                        FileSystem = FileSystemType.Ext4
                    };
                    ExpectRejected(() => ReadOnlyFileSystemMountEngine.MountReadOnly(disk, spoofedExt, true), "MountReadOnly on wiped " + pat + " partition");

                    // Reformat the wiped partition back to Ext4 and verify old written files NEVER reappear
                    qm = new OperationQueueManager();
                    qm.SetBaselineDisks(new DiskInfo[0], new DiskInfo[] { disk });
                    qm.Enqueue(new PendingOperation
                    {
                        Type = OperationType.FormatPartition,
                        TargetDiskNumber = diskNum,
                        TargetDiskPath = disk.DevicePath,
                        TargetPartitionNumber = pExt.PartitionNumber,
                        OriginalStartOffset = pExt.StartingOffset,
                        OriginalLength = pExt.PartitionLength,
                        NewFileSystem = FileSystemType.Ext4,
                        NewLabel = "CLEAN_EXT4",
                        NewGptTypeGuid = GptPartitionTypeGuids.ForFileSystem(FileSystemType.Ext4),
                        NewMbrType = RawDiskEngine.GetDefaultMbrType(FileSystemType.Ext4),
                        Summary = "Reformat wiped partition to Ext4"
                    });
                    qm.ApplyAll(null);

                    disk = RawDiskEngine.LoadVirtualDisk(imgPath, diskNum);
                    var cleanVol = ReadOnlyFileSystemMountEngine.MountReadOnly(disk, disk.Partitions[0], true);
                    if (FindFsNodeByPath(cleanVol.RootNode, "/secret_dir/classified.txt") != null ||
                        (cleanVol.OverlayRecords != null && cleanVol.OverlayRecords.Count != 0))
                        throw new Exception("Old experimental files or overlay records reappeared after wipe + reformat (" + pat + ").");

                    // Now wipe the HFS+ partition with RandomDoD while still mounted and verify full erasure too
                    if (pat == WipePattern.RandomDoD)
                    {
                        qm = new OperationQueueManager();
                        qm.SetBaselineDisks(new DiskInfo[0], new DiskInfo[] { disk });
                        qm.Enqueue(new PendingOperation
                        {
                            Type = OperationType.WipePartition,
                            TargetDiskNumber = diskNum,
                            TargetDiskPath = disk.DevicePath,
                            TargetPartitionNumber = disk.Partitions[1].PartitionNumber,
                            OriginalStartOffset = disk.Partitions[1].StartingOffset,
                            OriginalLength = disk.Partitions[1].PartitionLength,
                            WipePattern = WipePattern.RandomDoD,
                            Summary = "Wipe HFS+ RandomDoD"
                        });
                        qm.ApplyAll(null);

                        if (ReadOnlyFileSystemMountEngine.GetMount(diskNum, disk.Partitions[1].StartingOffset) != null)
                            throw new Exception("HFS+ ActiveMounts cache survived WipePartition.");
                        disk = RawDiskEngine.LoadVirtualDisk(imgPath, diskNum);
                        if (disk.Partitions[1].FileSystem != FileSystemType.RAW)
                            throw new Exception("HFS+ partition did not become RAW after RandomDoD wipe.");
                        var spoofedHfs = new PartitionEntry
                        {
                            DiskNumber = disk.Partitions[1].DiskNumber,
                            PartitionNumber = disk.Partitions[1].PartitionNumber,
                            StartingOffset = disk.Partitions[1].StartingOffset,
                            PartitionLength = disk.Partitions[1].PartitionLength,
                            FileSystem = FileSystemType.HFSPlus
                        };
                        ExpectRejected(() => ReadOnlyFileSystemMountEngine.MountReadOnly(disk, spoofedHfs, true), "MountReadOnly on wiped HFS+ partition");
                    }
                }

                // 10b. Format Partition without prior Wipe (Ext4 -> Btrfs -> Ext4) must completely purge overlay & cache
                string fmtImgPath = Path.Combine(tempDir, "stress10_fmt_only.img");
                var fmtDisk = RawDiskEngine.CreateBlankVirtualDisk(fmtImgPath, 32L * MiB, PartitionTableStyle.GPT);
                fmtDisk.DiskNumber = 909;

                var qmFmt = new OperationQueueManager();
                qmFmt.SetBaselineDisks(new DiskInfo[0], new DiskInfo[] { fmtDisk });
                qmFmt.Enqueue(MakeCreateOp(909, fmtImgPath, 1L * MiB, 24L * MiB, FileSystemType.Ext4, "FMT_SRC"));
                qmFmt.ApplyAll(null);

                fmtDisk = RawDiskEngine.LoadVirtualDisk(fmtImgPath, 909);
                var fmtPart = fmtDisk.Partitions[0];
                var fmtVol1 = ReadOnlyFileSystemMountEngine.MountReadOnly(fmtDisk, fmtPart, true);
                ReadOnlyFileSystemMountEngine.SetExperimentalWriteMode(fmtVol1, true);
                var oldDir = ReadOnlyFileSystemMountEngine.CreateDirectoryOnVolume(fmtDisk, fmtPart, fmtVol1, fmtVol1.RootNode, "old_format_dir");
                ReadOnlyFileSystemMountEngine.WriteFileToVolume(fmtDisk, fmtPart, fmtVol1, oldDir, "old_secret.txt", Encoding.UTF8.GetBytes("OLD_FORMAT_SECRET"));
                ReadOnlyFileSystemMountEngine.WriteFileToVolume(fmtDisk, fmtPart, fmtVol1, fmtVol1.RootNode, "top_secret.bin", new byte[4096]);
                string fmtShadowPath = Path.Combine(shadowDir, "ovl_d909_" + fmtPart.StartingOffset.ToString("X") + "_fmtmarker.bin");
                File.WriteAllBytes(fmtShadowPath, new byte[] { 0xCA, 0xFE, 0xBA, 0xBE });

                qmFmt = new OperationQueueManager();
                qmFmt.SetBaselineDisks(new DiskInfo[0], new DiskInfo[] { fmtDisk });
                qmFmt.Enqueue(new PendingOperation
                {
                    Type = OperationType.FormatPartition,
                    TargetDiskNumber = 909,
                    TargetDiskPath = fmtDisk.DevicePath,
                    TargetPartitionNumber = fmtPart.PartitionNumber,
                    OriginalStartOffset = fmtPart.StartingOffset,
                    OriginalLength = fmtPart.PartitionLength,
                    NewFileSystem = FileSystemType.Btrfs,
                    NewLabel = "FMT_BTRFS",
                    NewGptTypeGuid = GptPartitionTypeGuids.ForFileSystem(FileSystemType.Btrfs),
                    NewMbrType = RawDiskEngine.GetDefaultMbrType(FileSystemType.Btrfs),
                    Summary = "Format Ext4 -> Btrfs"
                });
                qmFmt.ApplyAll(null);

                if (ReadOnlyFileSystemMountEngine.GetMount(909, fmtPart.StartingOffset) != null)
                    throw new Exception("ActiveMounts cache survived FormatPartition (Ext4 -> Btrfs).");
                if (File.Exists(fmtShadowPath))
                    throw new Exception("Shadow overlay file survived FormatPartition (Ext4 -> Btrfs).");

                fmtDisk = RawDiskEngine.LoadVirtualDisk(fmtImgPath, 909);
                var btrfsVol = ReadOnlyFileSystemMountEngine.MountReadOnly(fmtDisk, fmtDisk.Partitions[0], true);
                if (FindFsNodeByPath(btrfsVol.RootNode, "/old_format_dir/old_secret.txt") != null ||
                    FindFsNodeByPath(btrfsVol.RootNode, "/top_secret.bin") != null ||
                    (btrfsVol.OverlayRecords != null && btrfsVol.OverlayRecords.Count != 0))
                    throw new Exception("Stale overlay files survived FormatPartition (Ext4 -> Btrfs).");

                ReadOnlyFileSystemMountEngine.SetExperimentalWriteMode(btrfsVol, true);
                ReadOnlyFileSystemMountEngine.WriteFileToVolume(fmtDisk, fmtDisk.Partitions[0], btrfsVol, btrfsVol.RootNode, "btrfs_secret.txt", Encoding.UTF8.GetBytes("BTRFS_SECRET"));

                qmFmt = new OperationQueueManager();
                qmFmt.SetBaselineDisks(new DiskInfo[0], new DiskInfo[] { fmtDisk });
                qmFmt.Enqueue(new PendingOperation
                {
                    Type = OperationType.FormatPartition,
                    TargetDiskNumber = 909,
                    TargetDiskPath = fmtDisk.DevicePath,
                    TargetPartitionNumber = fmtDisk.Partitions[0].PartitionNumber,
                    OriginalStartOffset = fmtDisk.Partitions[0].StartingOffset,
                    OriginalLength = fmtDisk.Partitions[0].PartitionLength,
                    NewFileSystem = FileSystemType.Ext4,
                    NewLabel = "FMT_EXT4_2",
                    NewGptTypeGuid = GptPartitionTypeGuids.ForFileSystem(FileSystemType.Ext4),
                    NewMbrType = RawDiskEngine.GetDefaultMbrType(FileSystemType.Ext4),
                    Summary = "Format Btrfs -> Ext4"
                });
                qmFmt.ApplyAll(null);

                fmtDisk = RawDiskEngine.LoadVirtualDisk(fmtImgPath, 909);
                var ext2Vol = ReadOnlyFileSystemMountEngine.MountReadOnly(fmtDisk, fmtDisk.Partitions[0], true);
                if (FindFsNodeByPath(ext2Vol.RootNode, "/btrfs_secret.txt") != null ||
                    FindFsNodeByPath(ext2Vol.RootNode, "/old_format_dir/old_secret.txt") != null ||
                    (ext2Vol.OverlayRecords != null && ext2Vol.OverlayRecords.Count != 0))
                    throw new Exception("Stale overlay files survived FormatPartition (Btrfs -> Ext4).");

                // 10c. Demo Virtual Disk Reset must clear written experimental files, shadow overlays, and RAM cache
                string demoPath = Path.Combine(tempDir, "stress10_demo.img");
                var demo1 = RawDiskEngine.CreateDemoVirtualDisk(demoPath, 90);
                var demoExt1 = demo1.Partitions.FirstOrDefault(p => p.FileSystem == FileSystemType.Ext4);
                var demoSwap1 = demo1.Partitions.FirstOrDefault(p => p.FileSystem == FileSystemType.LinuxSwap);
                if (demoExt1 == null || demoSwap1 == null)
                    throw new Exception("Demo virtual disk missing expected Ext4 or LinuxSwap partition.");

                var demoExtVol1 = ReadOnlyFileSystemMountEngine.MountReadOnly(demo1, demoExt1, true);
                ReadOnlyFileSystemMountEngine.SetExperimentalWriteMode(demoExtVol1, true);
                ReadOnlyFileSystemMountEngine.WriteFileToVolume(demo1, demoExt1, demoExtVol1, demoExtVol1.RootNode, "secret_after_demo.txt", Encoding.UTF8.GetBytes("DEMO_RESET_MUST_DELETE"));

                var qmDemo = new OperationQueueManager();
                qmDemo.SetBaselineDisks(new DiskInfo[0], new DiskInfo[] { demo1 });
                qmDemo.Enqueue(MakeResizeOp(90, demoPath, demoExt1, demoExt1.StartingOffset, 16L * MiB));
                qmDemo.ApplyAll(null);

                demo1 = RawDiskEngine.LoadVirtualDisk(demoPath, 90);
                demoExt1 = demo1.Partitions.FirstOrDefault(p => p.FileSystem == FileSystemType.Ext4);
                demoSwap1 = demo1.Partitions.FirstOrDefault(p => p.FileSystem == FileSystemType.LinuxSwap);
                if (demoExt1 == null || demoExt1.PartitionLength != 16L * MiB || demoSwap1 == null)
                    throw new Exception("Expected resized demo Ext4 partition (16 MiB) before reset.");

                // Re-mount both (now at 16 MiB Ext4 and 8 MiB Swap) and write overlay + shadow files, leaving BOTH mounted in ActiveMounts
                demoExtVol1 = ReadOnlyFileSystemMountEngine.MountReadOnly(demo1, demoExt1, true);
                ReadOnlyFileSystemMountEngine.SetExperimentalWriteMode(demoExtVol1, true);
                ReadOnlyFileSystemMountEngine.WriteFileToVolume(demo1, demoExt1, demoExtVol1, demoExtVol1.RootNode, "secret_after_demo.txt", Encoding.UTF8.GetBytes("DEMO_RESET_MUST_DELETE"));

                var demoSwapVol1 = ReadOnlyFileSystemMountEngine.MountReadOnly(demo1, demoSwap1, true);
                ReadOnlyFileSystemMountEngine.SetExperimentalWriteMode(demoSwapVol1, true);
                ReadOnlyFileSystemMountEngine.WriteFileToVolume(demo1, demoSwap1, demoSwapVol1, demoSwapVol1.RootNode, "secret_after_demo.txt", Encoding.UTF8.GetBytes("DEMO_RESET_MUST_DELETE"));

                string demoShadowPath = Path.Combine(shadowDir, "ovl_d90_" + demoExt1.StartingOffset.ToString("X") + "_demomarker.bin");
                File.WriteAllBytes(demoShadowPath, new byte[] { 1, 2, 3, 4 });

                // Reset demo virtual disk while caches & overlays exist
                var demo2 = RawDiskEngine.CreateDemoVirtualDisk(demoPath, 90);
                if (ReadOnlyFileSystemMountEngine.GetMount(90, demoExt1.StartingOffset) != null ||
                    ReadOnlyFileSystemMountEngine.GetMount(90, demoSwap1.StartingOffset) != null)
                    throw new Exception("Demo reset did not evict ActiveMounts cache for disk 90.");
                if (File.Exists(demoShadowPath))
                    throw new Exception("Demo reset did not delete shadow overlay file for disk 90.");

                var demoExt2 = demo2.Partitions.FirstOrDefault(p => p.FileSystem == FileSystemType.Ext4);
                var demoSwap2 = demo2.Partitions.FirstOrDefault(p => p.FileSystem == FileSystemType.LinuxSwap);
                if (demoExt2 == null || demoSwap2 == null ||
                    demoExt2.PartitionLength != 24L * MiB ||
                    demoSwap2.PartitionLength != 8L * MiB)
                    throw new Exception("Reset demo disk did not restore original partition sizes (expected 24 MiB Ext4 and 8 MiB Swap).");

                var demoExtVol2 = ReadOnlyFileSystemMountEngine.MountReadOnly(demo2, demoExt2, true);
                var demoSwapVol2 = ReadOnlyFileSystemMountEngine.MountReadOnly(demo2, demoSwap2, true);
                if (FindFsNodeByPath(demoExtVol2.RootNode, "/secret_after_demo.txt") != null ||
                    FindFsNodeByPath(demoSwapVol2.RootNode, "/secret_after_demo.txt") != null ||
                    (demoExtVol2.OverlayRecords != null && demoExtVol2.OverlayRecords.Count != 0) ||
                    (demoSwapVol2.OverlayRecords != null && demoSwapVol2.OverlayRecords.Count != 0))
                    throw new Exception("Experimental files or overlay records survived CreateDemoVirtualDisk reset!");

                VerifyAllFileSystemsFormatMatrix(tempDir, log);

                log("  -> Stress 10 PASSED (Zero/Ones/CryptoRandom wipe, Format overlay purge, Demo reset & all-23 FS format matrix verified).");
            }
            finally
            {
                ReadOnlyFileSystemMountEngine.ShadowOverlayRootOverride = null;
                ReadOnlyFileSystemMountEngine.UnmountAll();
            }
        }

        private static void VerifyAllFileSystemsFormatMatrix(string tempDir, Action<string> log)
        {
            string matrixImg = Path.Combine(tempDir, "stress10_all_fs_matrix.img");
            var disk = RawDiskEngine.CreateBlankVirtualDisk(matrixImg, 24L * MiB, PartitionTableStyle.GPT);
            disk.DiskNumber = 911;

            var qmCreate = new OperationQueueManager();
            qmCreate.SetBaselineDisks(null, new[] { disk });
            qmCreate.Enqueue(MakeCreateOp(911, matrixImg, 1L * MiB, 20L * MiB, FileSystemType.NTFS, "MATRIX_INIT"));
            qmCreate.ApplyAll();

            FileSystemType[] allFormats = new FileSystemType[]
            {
                FileSystemType.NTFS, FileSystemType.FAT32, FileSystemType.FAT16, FileSystemType.exFAT, FileSystemType.ReFS,
                FileSystemType.Ext2, FileSystemType.Ext3, FileSystemType.Ext4, FileSystemType.Btrfs, FileSystemType.XFS,
                FileSystemType.F2FS, FileSystemType.ZFS, FileSystemType.SquashFS, FileSystemType.ISO9660, FileSystemType.UDF,
                FileSystemType.LinuxSwap, FileSystemType.LVM2, FileSystemType.APFS, FileSystemType.HFSPlus, FileSystemType.HFS,
                FileSystemType.EfiSystem, FileSystemType.WindowsRecovery, FileSystemType.RAW
            };

            for (int i = 0; i < allFormats.Length; i++)
            {
                FileSystemType targetFs = allFormats[i];
                disk = RawDiskEngine.LoadVirtualDisk(matrixImg, 911);
                var p = disk.Partitions[0];
                string lbl = (targetFs == FileSystemType.RAW) ? string.Empty : ("M_" + targetFs.ToString().ToUpperInvariant());
                if (lbl.Length > 11) lbl = lbl.Substring(0, 11);

                var qm = new OperationQueueManager();
                qm.SetBaselineDisks(null, new[] { disk });
                qm.Enqueue(new PendingOperation
                {
                    Type = OperationType.FormatPartition,
                    TargetDiskNumber = 911,
                    TargetDiskPath = matrixImg,
                    TargetPartitionNumber = p.PartitionNumber,
                    OriginalStartOffset = p.StartingOffset,
                    OriginalLength = p.PartitionLength,
                    NewFileSystem = targetFs,
                    NewLabel = lbl,
                    NewGptTypeGuid = GptPartitionTypeGuids.ForFileSystem(targetFs),
                    NewMbrType = RawDiskEngine.GetDefaultMbrType(targetFs),
                    Summary = "Matrix Format -> " + targetFs
                });
                qm.ApplyAll();

                disk = RawDiskEngine.LoadVirtualDisk(matrixImg, 911);
                var reloadedPart = disk.Partitions[0];
                if (reloadedPart.FileSystem != targetFs)
                {
                    throw new Exception(string.Format(
                        "Format matrix mismatch for {0}: reloaded as {1}.", targetFs, reloadedPart.FileSystem));
                }

                if (targetFs == FileSystemType.FAT32 || targetFs == FileSystemType.EfiSystem)
                {
                    byte[] hdr = RawDiskEngine.ReadBytes(disk, reloadedPart.StartingOffset, 4096);
                    uint fatSz32 = BitConverter.ToUInt32(hdr, 0x24);
                    uint rootClus = BitConverter.ToUInt32(hdr, 0x2C);
                    ushort fsInfo = BitConverter.ToUInt16(hdr, 0x30);
                    ushort bkBoot = BitConverter.ToUInt16(hdr, 0x32);
                    if (fatSz32 == 0 || rootClus != 2u || fsInfo != 1 || bkBoot != 6)
                        throw new Exception("Invalid FAT32 BPB geometry fields on formatted partition.");
                    if (BitConverter.ToUInt32(hdr, 512) != 0x41615252u ||
                        BitConverter.ToUInt32(hdr, 512 + 484) != 0x61417272u ||
                        BitConverter.ToUInt32(hdr, 512 + 508) != 0xAA550000u)
                        throw new Exception("Invalid FAT32 FSInfo signatures at LBA 1.");
                    if (hdr[6 * 512 + 510] != 0x55 || hdr[6 * 512 + 511] != 0xAA)
                        throw new Exception("Missing FAT32 backup boot sector signature at LBA 6.");

                    byte[] fat1 = RawDiskEngine.ReadBytes(disk, reloadedPart.StartingOffset + 32L * 512L, 512);
                    byte[] fat2 = RawDiskEngine.ReadBytes(disk, reloadedPart.StartingOffset + (32L + fatSz32) * 512L, 512);
                    if (BitConverter.ToUInt32(fat1, 0) != 0x0FFFFFF8u || BitConverter.ToUInt32(fat2, 0) != 0x0FFFFFF8u)
                        throw new Exception("Invalid FAT32 FAT1/FAT2 cluster 0 signature.");
                }
                else if (targetFs == FileSystemType.FAT16)
                {
                    byte[] boot16 = RawDiskEngine.ReadBytes(disk, reloadedPart.StartingOffset, 512);
                    ushort fat16Sz = BitConverter.ToUInt16(boot16, 0x16);
                    if (fat16Sz == 0)
                        throw new Exception("Invalid FAT16 BPB_FATSz16 = 0.");
                    byte[] fat1 = RawDiskEngine.ReadBytes(disk, reloadedPart.StartingOffset + 512L, 512);
                    byte[] fat2 = RawDiskEngine.ReadBytes(disk, reloadedPart.StartingOffset + (1L + fat16Sz) * 512L, 512);
                    if (fat1[0] != 0xF8 || fat1[1] != 0xFF || fat1[2] != 0xFF || fat1[3] != 0xFF ||
                        fat2[0] != 0xF8 || fat2[1] != 0xFF || fat2[2] != 0xFF || fat2[3] != 0xFF)
                        throw new Exception("Invalid FAT16 FAT1/FAT2 media descriptor entries.");
                }
                else if (targetFs == FileSystemType.exFAT)
                {
                    byte[] bootRegion = RawDiskEngine.ReadBytes(disk, reloadedPart.StartingOffset, 24 * 512);
                    for (int z = 0x0B; z <= 0x3F; z++)
                    {
                        if (bootRegion[z] != 0)
                            throw new Exception("exFAT MustBeZero region contains non-zero byte.");
                    }
                    if (bootRegion[0x6C] != 9 || bootRegion[0x6D] == 0 || bootRegion[0x6E] != 1)
                        throw new Exception("Invalid exFAT sector/cluster/FAT shifts.");
                    uint fatOffset = BitConverter.ToUInt32(bootRegion, 0x50);
                    uint heapOffset = BitConverter.ToUInt32(bootRegion, 0x58);
                    uint clusCount = BitConverter.ToUInt32(bootRegion, 0x5C);
                    uint rootClus = BitConverter.ToUInt32(bootRegion, 0x60);
                    if (fatOffset != 24u || heapOffset <= fatOffset || clusCount == 0 || rootClus < 4u)
                        throw new Exception("Invalid exFAT volume geometry offsets.");

                    uint expectedBootChk = RawDiskEngine.ComputeExFatChecksum(bootRegion, 11 * 512, true);
                    for (int ci = 0; ci < 128; ci++)
                    {
                        if (BitConverter.ToUInt32(bootRegion, 11 * 512 + ci * 4) != expectedBootChk)
                            throw new Exception("exFAT BootChecksum mismatch at LBA 11.");
                    }
                    for (int bi = 0; bi < 12 * 512; bi++)
                    {
                        if (bootRegion[bi] != bootRegion[12 * 512 + bi])
                            throw new Exception("exFAT Backup Boot Region does not match Main Boot Region.");
                    }

                    byte[] exFat = RawDiskEngine.ReadBytes(disk, reloadedPart.StartingOffset + (long)fatOffset * 512L, 512);
                    if (BitConverter.ToUInt32(exFat, 0) != 0xFFFFFFF8u || BitConverter.ToUInt32(exFat, 4) != 0xFFFFFFFFu)
                        throw new Exception("Invalid exFAT FAT media/EOC entries.");

                    long spc = 1L << bootRegion[0x6D];
                    long clusBytes = spc * 512L;
                    long heapByteOff = (long)heapOffset * 512L;
                    byte[] bmp = RawDiskEngine.ReadBytes(disk, reloadedPart.StartingOffset + heapByteOff, 512);
                    if ((bmp[0] & 1) == 0)
                        throw new Exception("exFAT Allocation Bitmap cluster 2 bit not marked allocated.");

                    long rootByteOff = heapByteOff + (long)(rootClus - 2u) * clusBytes;
                    byte[] rootSec = RawDiskEngine.ReadBytes(disk, reloadedPart.StartingOffset + rootByteOff, 512);
                    bool foundBitmap = false, foundUpCase = false;
                    uint upClus = 0, upChk = 0;
                    ulong upLen = 0;
                    for (int rOff = 0; rOff + 32 <= rootSec.Length; rOff += 32)
                    {
                        if (rootSec[rOff] == 0x81) foundBitmap = true;
                        if (rootSec[rOff] == 0x82)
                        {
                            foundUpCase = true;
                            upChk = BitConverter.ToUInt32(rootSec, rOff + 4);
                            upClus = BitConverter.ToUInt32(rootSec, rOff + 20);
                            upLen = BitConverter.ToUInt64(rootSec, rOff + 24);
                        }
                    }
                    if (!foundBitmap || !foundUpCase || upClus < 3u || upLen == 0)
                        throw new Exception("exFAT Root Directory missing Allocation Bitmap (0x81) or Up-Case Table (0x82).");
                    byte[] upBytes = RawDiskEngine.ReadBytes(disk, reloadedPart.StartingOffset + heapByteOff + (long)(upClus - 2u) * clusBytes, (int)upLen);
                    if (RawDiskEngine.ComputeExFatChecksum(upBytes, upBytes.Length, false) != upChk)
                        throw new Exception("exFAT Up-Case Table checksum mismatch.");
                }
            }

            // Verify 72 MiB FAT32 (inside 64..260 MiB range) reaches >= 65525 clusters with secPerClus == 1
            string smallFatImg = Path.Combine(tempDir, "stress10_fat32_72m.img");
            var smallFatDisk = RawDiskEngine.CreateBlankVirtualDisk(smallFatImg, 74L * MiB, PartitionTableStyle.MBR);
            smallFatDisk.DiskNumber = 912;
            var qmFat72 = new OperationQueueManager();
            qmFat72.SetBaselineDisks(null, new[] { smallFatDisk });
            qmFat72.Enqueue(MakeCreateOp(912, smallFatImg, 1L * MiB, 72L * MiB, FileSystemType.FAT32, "FAT72M"));
            qmFat72.ApplyAll();
            smallFatDisk = RawDiskEngine.LoadVirtualDisk(smallFatImg, 912);
            var p72 = smallFatDisk.Partitions[0];
            if (p72.FileSystem != FileSystemType.FAT32)
                throw new Exception("72 MiB FAT32 partition misidentified as " + p72.FileSystem);
            byte[] hdr72 = RawDiskEngine.ReadBytes(smallFatDisk, p72.StartingOffset, 4096);
            if (hdr72[0x0D] != 1)
                throw new Exception("72 MiB FAT32 expected BPB_SecPerClus = 1, got " + hdr72[0x0D]);
            uint fatSz72 = BitConverter.ToUInt32(hdr72, 0x24);
            long totSec72 = BitConverter.ToUInt32(hdr72, 0x20);
            long dataClus72 = (totSec72 - 32L - 2L * fatSz72) / hdr72[0x0D];
            if (dataClus72 < 65525L)
                throw new Exception("72 MiB FAT32 has fewer than 65525 data clusters: " + dataClus72);

            // Verify real multi-group Linux-mountable Ext2/Ext3/Ext4 formatter across 2 block groups (140 MiB)
            string extImg = Path.Combine(tempDir, "stress10_real_ext_2grp.img");
            FileSystemType[] extFses = new FileSystemType[] { FileSystemType.Ext2, FileSystemType.Ext3, FileSystemType.Ext4 };
            for (int ei = 0; ei < extFses.Length; ei++)
            {
                FileSystemType extFs = extFses[ei];
                var extDisk = RawDiskEngine.CreateBlankVirtualDisk(extImg, 144L * MiB, PartitionTableStyle.MBR);
                extDisk.DiskNumber = 913;
                var qmExt = new OperationQueueManager();
                qmExt.SetBaselineDisks(null, new[] { extDisk });
                qmExt.Enqueue(MakeCreateOp(913, extImg, 1L * MiB, 140L * MiB, extFs, "REAL_" + extFs.ToString().ToUpperInvariant()));
                qmExt.ApplyAll();

                extDisk = RawDiskEngine.LoadVirtualDisk(extImg, 913);
                var ep = extDisk.Partitions[0];
                ep.Label = "REAL_" + extFs.ToString().ToUpperInvariant();
                ep.FileSystem = extFs;
                RawDiskEngine.WriteRealExtFileSystem(extDisk, ep);
                extDisk.CanRawRead = true;
                RawDiskEngine.InspectPartitionSuperblock(extDisk, ep);
                if (ep.FileSystem != extFs)
                    throw new Exception("WriteRealExtFileSystem readback mismatch for " + extFs + ": got " + ep.FileSystem);

                byte[] sbBytes = RawDiskEngine.ReadBytes(extDisk, ep.StartingOffset + 1024L, 1024);
                uint freeBlocksSb = BitConverter.ToUInt32(sbBytes, 0x0C);
                uint freeInodesSb = BitConverter.ToUInt32(sbBytes, 0x10);
                uint featIncompat = BitConverter.ToUInt32(sbBytes, 0x60);
                uint journalInum = BitConverter.ToUInt32(sbBytes, 0xE0);
                byte[] gdtBytes = RawDiskEngine.ReadBytes(extDisk, ep.StartingOffset + 4096L, 64);
                uint bgFreeSum = BitConverter.ToUInt16(gdtBytes, 0x0C) + (uint)BitConverter.ToUInt16(gdtBytes, 32 + 0x0C);
                uint bgInoSum = BitConverter.ToUInt16(gdtBytes, 0x0E) + (uint)BitConverter.ToUInt16(gdtBytes, 32 + 0x0E);
                if (freeBlocksSb != bgFreeSum || freeInodesSb != bgInoSum)
                    throw new Exception("Real " + extFs + " superblock free counts do not match Group 0 + Group 1 sums.");

                byte[] backupSb = RawDiskEngine.ReadBytes(extDisk, ep.StartingOffset + 32768L * 4096L, 1024);
                if (BitConverter.ToUInt16(backupSb, 0x38) != 0xEF53 || BitConverter.ToUInt16(backupSb, 0x5A) != 1)
                    throw new Exception("Real " + extFs + " Group 1 backup superblock invalid.");

                if (extFs == FileSystemType.Ext3 || extFs == FileSystemType.Ext4)
                {
                    if (journalInum != 8u)
                        throw new Exception("Real " + extFs + " missing s_journal_inum=8.");
                    uint g0ItableBlk = BitConverter.ToUInt32(gdtBytes, 0x08);
                    byte[] ino8 = RawDiskEngine.ReadBytes(extDisk, ep.StartingOffset + (long)g0ItableBlk * 4096L + 7L * 256L, 256);
                    uint jFirstBlk = (extFs == FileSystemType.Ext4)
                        ? BitConverter.ToUInt32(ino8, 0x3C)
                        : BitConverter.ToUInt32(ino8, 0x28);
                    if (jFirstBlk == 0u)
                        throw new Exception("Real " + extFs + " Inode 8 has zero journal block.");
                    byte[] jHdr = RawDiskEngine.ReadBytes(extDisk, ep.StartingOffset + (long)jFirstBlk * 4096L, 64);
                    if (jHdr[0] != 0xC0 || jHdr[1] != 0x3B || jHdr[2] != 0x39 || jHdr[3] != 0x98 ||
                        jHdr[4] != 0 || jHdr[5] != 0 || jHdr[6] != 0 || jHdr[7] != 4)
                        throw new Exception("Real " + extFs + " Inode 8 missing big-endian JBD2_SUPERBLOCK_V2 header.");
                }
                if (extFs == FileSystemType.Ext4)
                {
                    if ((featIncompat & 0x0200u) != 0 || (featIncompat & 0x0040u) == 0)
                        throw new Exception("Real Ext4 incompat flags invalid (expected EXTENTS without FLEX_BG).");
                }
            }

            // Verify >32 GiB FAT32 and MBR vs GPT DiskPart scripts
            var bigFat32 = new PendingOperation
            {
                Type = OperationType.FormatPartition,
                TargetDiskNumber = 2,
                TargetPartitionNumber = 1,
                OriginalStartOffset = 1L * MiB,
                OriginalLength = 64L * 1073741824L,
                NewFileSystem = FileSystemType.FAT32,
                NewLabel = "BIGFAT32"
            };
            string bigScript = OperationQueueManager.BuildDiskPartScript(bigFat32, PartitionTableStyle.GPT);
            if (bigScript.IndexOf("format fs=fat32", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new Exception("FAT32 > 32 GiB should bypass DiskPart format fs=fat32.");

            var smallFat32 = new PendingOperation
            {
                Type = OperationType.FormatPartition,
                TargetDiskNumber = 2,
                TargetPartitionNumber = 1,
                OriginalStartOffset = 1L * MiB,
                OriginalLength = 8L * 1073741824L,
                NewFileSystem = FileSystemType.FAT32,
                NewLabel = "SMALLFAT"
            };
            string modernSmall = OperationQueueManager.BuildDiskPartScript(smallFat32, PartitionTableStyle.GPT);
            if (modernSmall.IndexOf("format fs=fat32", StringComparison.OrdinalIgnoreCase) < 0)
                throw new Exception("FAT32 <= 32 GiB should emit DiskPart format on modern Windows.");

            var mbrEfiCreate = new PendingOperation
            {
                Type = OperationType.CreatePartition,
                TargetDiskNumber = 2,
                StartOffset = 1L * MiB,
                Length = 100L * MiB,
                NewFileSystem = FileSystemType.EfiSystem,
                NewMbrBoot = true
            };
            string mbrCreateScript = OperationQueueManager.BuildDiskPartScript(mbrEfiCreate, PartitionTableStyle.MBR);
            if (mbrCreateScript.IndexOf("create partition efi", StringComparison.OrdinalIgnoreCase) >= 0 ||
                mbrCreateScript.IndexOf("create partition primary", StringComparison.OrdinalIgnoreCase) < 0 ||
                mbrCreateScript.IndexOf("active", StringComparison.OrdinalIgnoreCase) < 0)
                throw new Exception("MBR CreatePartition script must emit 'create partition primary' + 'active', never 'create partition efi'.");

            var extFmtOp = new PendingOperation
            {
                Type = OperationType.FormatPartition,
                TargetDiskNumber = 2,
                TargetPartitionNumber = 1,
                OriginalStartOffset = 1L * MiB,
                OriginalLength = 100L * MiB,
                NewFileSystem = FileSystemType.Ext4
            };
            string mbrExtScript = OperationQueueManager.BuildDiskPartScript(extFmtOp, PartitionTableStyle.MBR);
            string gptExtScript = OperationQueueManager.BuildDiskPartScript(extFmtOp, PartitionTableStyle.GPT);
            if (mbrExtScript.IndexOf("set id=83 override", StringComparison.OrdinalIgnoreCase) < 0 ||
                gptExtScript.IndexOf("set id=" + GptPartitionTypeGuids.LinuxFilesystem.ToString("D"), StringComparison.OrdinalIgnoreCase) < 0)
                throw new Exception("Ext4 FormatPartition script must emit 0x83 on MBR and LinuxFilesystem GUID on GPT.");

            log("  -> Verified all " + allFormats.Length + " formattable FileSystemTypes + 2-group Ext2/3/4 + FAT32 (incl. 72 MiB)/FAT16/exFAT + MBR/GPT scripts.");
        }

        private sealed class SeededExFatFile
        {
            public string RelativePath;
            public byte[] ExpectedContent;
        }

        private static byte[] MakeDeterministicBytes(int seed, int length)
        {
            byte[] buf = new byte[length];
            uint x = (uint)seed ^ 0x9E3779B9u;
            for (int i = 0; i < length; i++)
            {
                x ^= x << 13;
                x ^= x >> 17;
                x ^= x << 5;
                buf[i] = (byte)(x & 0xFF);
            }
            return buf;
        }

        private static void WriteExFatFileEntrySet(byte[] dirBuf, int pos, string fileName, ushort fileAttrs, bool noFatChain, uint firstCluster, ulong dataLen)
        {
            int nameChars = fileName.Length;
            int nameEntries = (nameChars + 14) / 15;
            int totalEntries = 2 + nameEntries;
            int totalBytes = totalEntries * 32;
            Array.Clear(dirBuf, pos, totalBytes);

            // 0x85 File Entry
            dirBuf[pos] = 0x85;
            dirBuf[pos + 1] = (byte)(totalEntries - 1);
            BitConverter.GetBytes(fileAttrs).CopyTo(dirBuf, pos + 4);

            // 0xC0 Stream Extension
            int c0 = pos + 32;
            dirBuf[c0] = 0xC0;
            dirBuf[c0 + 1] = (byte)(0x01 | (noFatChain ? 0x02 : 0x00)); // AllocationPossible | NoFatChain
            dirBuf[c0 + 3] = (byte)nameChars;
            BitConverter.GetBytes((ushort)0x1234).CopyTo(dirBuf, c0 + 4); // NameHash (non-zero placeholder)
            BitConverter.GetBytes(dataLen).CopyTo(dirBuf, c0 + 8);        // ValidDataLength
            BitConverter.GetBytes(firstCluster).CopyTo(dirBuf, c0 + 20);  // FirstCluster
            BitConverter.GetBytes(dataLen).CopyTo(dirBuf, c0 + 24);       // DataLength

            // 0xC1 File Name entries (15 UTF-16LE chars per entry)
            for (int ne = 0; ne < nameEntries; ne++)
            {
                int c1 = pos + (2 + ne) * 32;
                dirBuf[c1] = 0xC1;
                int startCh = ne * 15;
                int take = Math.Min(15, nameChars - startCh);
                for (int ci = 0; ci < take; ci++)
                    BitConverter.GetBytes((ushort)fileName[startCh + ci]).CopyTo(dirBuf, c1 + 2 + ci * 2);
            }

            ushort chk = ExFatResizer.ComputeExFatEntrySetChecksum(dirBuf, pos, totalBytes);
            BitConverter.GetBytes(chk).CopyTo(dirBuf, pos + 2);
        }

        private static byte[] ReadSeededExFatFile(DiskInfo disk, PartitionEntry part, string relPath)
        {
            byte[] boot = RawDiskEngine.ReadBytes(disk, part.StartingOffset, 512);
            int secSize = 1 << boot[0x6C];
            uint secPerClus = 1u << boot[0x6D];
            int clusSize = secSize * (int)secPerClus;
            uint fatOff = BitConverter.ToUInt32(boot, 0x50);
            uint fatLen = BitConverter.ToUInt32(boot, 0x54);
            uint heapOff = BitConverter.ToUInt32(boot, 0x58);
            uint rootFirst = BitConverter.ToUInt32(boot, 0x60);

            byte[] fatRaw = RawDiskEngine.ReadBytes(disk, part.StartingOffset + (long)fatOff * secSize, (int)(fatLen * secSize));

            Func<uint, bool, ulong, byte[]> readObj = delegate(uint firstClus, bool noFatChain, ulong dataLen)
            {
                if (firstClus == 0u) return new byte[0];
                var clusList = new List<uint>();
                if (noFatChain)
                {
                    ulong cnt = dataLen == 0UL ? 1UL : (dataLen + (ulong)clusSize - 1UL) / (ulong)clusSize;
                    for (uint i = 0; i < (uint)cnt; i++) clusList.Add(firstClus + i);
                }
                else
                {
                    uint cur = firstClus;
                    var seen = new HashSet<uint>();
                    while (true)
                    {
                        if (!seen.Add(cur)) throw new Exception("FAT loop reading " + relPath);
                        clusList.Add(cur);
                        uint next = BitConverter.ToUInt32(fatRaw, (int)(cur * 4u));
                        if (next >= 0xFFFFFFF8u) break;
                        cur = next;
                    }
                }
                byte[] res = new byte[clusList.Count * clusSize];
                for (int i = 0; i < clusList.Count; i++)
                {
                    long off = part.StartingOffset + ((long)heapOff + (long)(clusList[i] - 2u) * secPerClus) * secSize;
                    byte[] blk = RawDiskEngine.ReadBytes(disk, off, clusSize);
                    Array.Copy(blk, 0, res, i * clusSize, clusSize);
                }
                if (dataLen < (ulong)res.Length)
                {
                    byte[] exact = new byte[dataLen];
                    Array.Copy(res, 0, exact, 0, (int)dataLen);
                    return exact;
                }
                return res;
            };

            string[] segments = relPath.Trim('/').Split('/');
            uint curDirClus = rootFirst;
            for (int si = 0; si < segments.Length; si++)
            {
                byte[] dirBytes = readObj(curDirClus, false, ulong.MaxValue);
                bool found = false;
                int pos = 0;
                while (pos + 32 <= dirBytes.Length)
                {
                    byte eType = dirBytes[pos];
                    if (eType == 0x00) break;
                    if ((eType & 0x80) == 0 || eType == 0x81 || eType == 0x82 || eType == 0x83 || eType == 0xA0)
                    {
                        pos += 32;
                        continue;
                    }
                    if (eType == 0x85)
                    {
                        int secCnt = dirBytes[pos + 1];
                        int setBytes = (1 + secCnt) * 32;
                        int nameLen = dirBytes[pos + 32 + 3];
                        var sbName = new StringBuilder();
                        for (int n = 2; n <= secCnt; n++)
                        {
                            int nOff = pos + n * 32;
                            for (int ci = 0; ci < 15 && sbName.Length < nameLen; ci++)
                                sbName.Append((char)BitConverter.ToUInt16(dirBytes, nOff + 2 + ci * 2));
                        }
                        if (string.Equals(sbName.ToString(), segments[si], StringComparison.Ordinal))
                        {
                            bool nfc = (dirBytes[pos + 32 + 1] & 0x02) != 0;
                            uint fClus = BitConverter.ToUInt32(dirBytes, pos + 32 + 20);
                            ulong dLen = BitConverter.ToUInt64(dirBytes, pos + 32 + 24);
                            if (si == segments.Length - 1)
                                return readObj(fClus, nfc, dLen);
                            if (nfc)
                                throw new Exception("Expected FAT chain on directory segment " + segments[si]);
                            curDirClus = fClus;
                            found = true;
                            break;
                        }
                        pos += setBytes;
                        continue;
                    }
                    pos += 32;
                }
                if (!found)
                    throw new Exception("Seeded exFAT path not found after resize/move: " + relPath);
            }
            throw new Exception("Unreachable path search failure for " + relPath);
        }

        private static List<SeededExFatFile> SeedRealExFatVolumeTree(string imgPath, long partStart, long shrinkBoundaryBytes)
        {
            byte[] sec0;
            using (var fs = new FileStream(imgPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                fs.Seek(partStart, SeekOrigin.Begin);
                sec0 = new byte[512];
                fs.Read(sec0, 0, 512);
            }

            int secSize = 1 << sec0[0x6C];
            uint secPerClus = 1u << sec0[0x6D];
            int clusSize = secSize * (int)secPerClus;
            uint fatOff = BitConverter.ToUInt32(sec0, 0x50);
            uint fatLen = BitConverter.ToUInt32(sec0, 0x54);
            uint heapOff = BitConverter.ToUInt32(sec0, 0x58);
            uint clusCount = BitConverter.ToUInt32(sec0, 0x5C);
            uint rootFirst = BitConverter.ToUInt32(sec0, 0x60);

            byte[] fat = new byte[fatLen * secSize];
            byte[] rootBuf = new byte[clusSize];
            byte[] bmpBuf;

            using (var fs = new FileStream(imgPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                fs.Seek(partStart + (long)fatOff * secSize, SeekOrigin.Begin);
                fs.Read(fat, 0, fat.Length);
                fs.Seek(partStart + ((long)heapOff + (long)(rootFirst - 2u) * secPerClus) * secSize, SeekOrigin.Begin);
                fs.Read(rootBuf, 0, rootBuf.Length);

                // Locate 0x81 Allocation Bitmap cluster 2
                bmpBuf = new byte[clusSize];
                fs.Seek(partStart + (long)heapOff * secSize, SeekOrigin.Begin);
                fs.Read(bmpBuf, 0, clusSize);
            }

            // Free cursor right after initial metadata clusters
            uint nextSeq = rootFirst + 1u;
            Action<uint> markAlloc = delegate(uint c)
            {
                uint idx = c - 2u;
                bmpBuf[idx >> 3] |= (byte)(1 << (int)(idx & 7));
            };
            Action<uint, uint> setFat = delegate(uint c, uint val)
            {
                BitConverter.GetBytes(val).CopyTo(fat, (int)(c * 4u));
            };

            var files = new List<SeededExFatFile>();
            byte[] frontContig = MakeDeterministicBytes(101, clusSize * 2 - 123);
            byte[] tailContigFull = MakeDeterministicBytes(202, clusSize * 2 + 77);
            byte[] tailContigSplit = MakeDeterministicBytes(303, clusSize * 4 - 41);
            byte[] fragBytes = MakeDeterministicBytes(404, clusSize * 2 + 512);
            byte[] nestedBytes = MakeDeterministicBytes(505, clusSize + 999);
            byte[] deepTargetBytes = MakeDeterministicBytes(606, 2500);

            // Allocate frontContig right at cluster [nextSeq, nextSeq + 1] (NoFatChain = 1, hits K=5 grow zone if nextSeq == 6!)
            uint cFront = nextSeq;
            nextSeq += 2u;
            markAlloc(cFront); markAlloc(cFront + 1u);

            // Allocate fragmented file spanning low and near-shrink clusters (NoFatChain = 0)
            uint boundaryClus = (uint)Math.Max(nextSeq + 10u, (uint)((shrinkBoundaryBytes / secSize - heapOff) / secPerClus + 1L));
            uint frag0 = nextSeq++;
            uint frag1 = nextSeq + 3u;
            uint frag2 = Math.Min(clusCount + 1u, boundaryClus + 8u);
            markAlloc(frag0); markAlloc(frag1); markAlloc(frag2);
            setFat(frag0, frag1); setFat(frag1, frag2); setFat(frag2, 0xFFFFFFFFu);

            // Allocate split contiguous file straddling shrink boundary [boundaryClus - 1 .. boundaryClus + 2] (4 clusters, NoFatChain = 1)
            uint splitStart = boundaryClus - 1u;
            for (uint s = 0; s < 4u; s++) markAlloc(splitStart + s);

            // Allocate full tail contiguous file cleanly above shrink boundary [boundaryClus + 10 .. +12] (3 clusters, NoFatChain = 1)
            uint tailFullStart = Math.Min(clusCount - 2u, boundaryClus + 10u);
            for (uint s = 0; s < 3u; s++) markAlloc(tailFullStart + s);

            // Allocate 2-cluster /Docs directory (padded with deleted 0x05 placeholders so a 0x85+0xC0+0xC1 EntrySet crosses cluster 0 -> cluster 1!)
            uint docs0 = boundaryClus - 8u;
            uint docs1 = boundaryClus + 4u; // non-contiguous 2nd dir cluster in tail!
            markAlloc(docs0); markAlloc(docs1);
            setFat(docs0, docs1); setFat(docs1, 0xFFFFFFFFu);

            // Allocate /Docs/Deep subdir in tail
            uint deepDirClus = Math.Min(clusCount, boundaryClus + 15u);
            markAlloc(deepDirClus);
            setFat(deepDirClus, 0xFFFFFFFFu);

            uint nested0 = boundaryClus + 6u;
            uint nested1 = nextSeq + 5u;
            markAlloc(nested0); markAlloc(nested1);
            setFat(nested0, nested1); setFat(nested1, 0xFFFFFFFFu);

            uint deepTargetClus = nextSeq + 6u;
            markAlloc(deepTargetClus);

            // Build /Docs 2-cluster buffer with Deep + cross-cluster deep_target.bin (starts at offset 4096 - 32 = 4064!)
            byte[] docsBuf = new byte[clusSize * 2];
            WriteExFatFileEntrySet(docsBuf, 0, "Deep", 0x0010, false, deepDirClus, (ulong)clusSize);
            for (int off = 96; off < clusSize - 32; off += 32)
                docsBuf[off] = 0x05; // deleted file slot padding
            WriteExFatFileEntrySet(docsBuf, clusSize - 32, "deep_target.bin", 0x0020, true, deepTargetClus, (ulong)deepTargetBytes.Length);

            // Build /Docs/Deep directory buffer
            byte[] deepDirBuf = new byte[clusSize];
            WriteExFatFileEntrySet(deepDirBuf, 0, "nested.bin", 0x0020, false, nested0, (ulong)nestedBytes.Length);

            // Append entries into Root Directory after existing 0x81 / 0x82 / 0x83 slots
            int rootSlot = 0;
            while (rootSlot + 32 <= rootBuf.Length && rootBuf[rootSlot] != 0x00)
                rootSlot += 32;
            WriteExFatFileEntrySet(rootBuf, rootSlot, "empty.txt", 0x0020, false, 0u, 0UL);
            rootSlot += 96;
            WriteExFatFileEntrySet(rootBuf, rootSlot, "front_contig.bin", 0x0020, true, cFront, (ulong)frontContig.Length);
            rootSlot += 128; // "front_contig.bin" = 16 chars -> 1 + 1 + 2 = 4 entries = 128B
            WriteExFatFileEntrySet(rootBuf, rootSlot, "frag.bin", 0x0020, false, frag0, (ulong)fragBytes.Length);
            rootSlot += 96;
            WriteExFatFileEntrySet(rootBuf, rootSlot, "tail_contig_split.bin", 0x0020, true, splitStart, (ulong)tailContigSplit.Length);
            rootSlot += 128; // 21 chars -> 2 name entries -> 4 * 32 = 128B
            WriteExFatFileEntrySet(rootBuf, rootSlot, "tail_contig_full.bin", 0x0020, true, tailFullStart, (ulong)tailContigFull.Length);
            rootSlot += 128;
            WriteExFatFileEntrySet(rootBuf, rootSlot, "Docs", 0x0010, false, docs0, (ulong)(clusSize * 2));

            // Commit FAT, Bitmap, Root, Dirs, and file payloads to image
            using (var fs = new FileStream(imgPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                Action<uint, byte[], int, int> writeClus = delegate(uint c, byte[] src, int sOff, int sLen)
                {
                    byte[] cBuf = new byte[clusSize];
                    Array.Copy(src, sOff, cBuf, 0, Math.Min(sLen, clusSize));
                    fs.Seek(partStart + ((long)heapOff + (long)(c - 2u) * secPerClus) * secSize, SeekOrigin.Begin);
                    fs.Write(cBuf, 0, clusSize);
                };

                fs.Seek(partStart + (long)fatOff * secSize, SeekOrigin.Begin);
                fs.Write(fat, 0, fat.Length);
                fs.Seek(partStart + (long)heapOff * secSize, SeekOrigin.Begin);
                fs.Write(bmpBuf, 0, bmpBuf.Length);
                writeClus(rootFirst, rootBuf, 0, rootBuf.Length);

                writeClus(cFront, frontContig, 0, clusSize);
                writeClus(cFront + 1u, frontContig, clusSize, frontContig.Length - clusSize);

                writeClus(frag0, fragBytes, 0, clusSize);
                writeClus(frag1, fragBytes, clusSize, clusSize);
                writeClus(frag2, fragBytes, clusSize * 2, fragBytes.Length - clusSize * 2);

                for (int s = 0; s < 4; s++)
                    writeClus(splitStart + (uint)s, tailContigSplit, s * clusSize, Math.Min(clusSize, tailContigSplit.Length - s * clusSize));
                for (int s = 0; s < 3; s++)
                    writeClus(tailFullStart + (uint)s, tailContigFull, s * clusSize, Math.Min(clusSize, tailContigFull.Length - s * clusSize));

                writeClus(docs0, docsBuf, 0, clusSize);
                writeClus(docs1, docsBuf, clusSize, clusSize);
                writeClus(deepDirClus, deepDirBuf, 0, clusSize);

                writeClus(nested0, nestedBytes, 0, clusSize);
                writeClus(nested1, nestedBytes, clusSize, nestedBytes.Length - clusSize);
                writeClus(deepTargetClus, deepTargetBytes, 0, deepTargetBytes.Length);
                fs.Flush();
            }

            files.Add(new SeededExFatFile { RelativePath = "/empty.txt", ExpectedContent = new byte[0] });
            files.Add(new SeededExFatFile { RelativePath = "/front_contig.bin", ExpectedContent = frontContig });
            files.Add(new SeededExFatFile { RelativePath = "/frag.bin", ExpectedContent = fragBytes });
            files.Add(new SeededExFatFile { RelativePath = "/tail_contig_split.bin", ExpectedContent = tailContigSplit });
            files.Add(new SeededExFatFile { RelativePath = "/tail_contig_full.bin", ExpectedContent = tailContigFull });
            files.Add(new SeededExFatFile { RelativePath = "/Docs/deep_target.bin", ExpectedContent = deepTargetBytes });
            files.Add(new SeededExFatFile { RelativePath = "/Docs/Deep/nested.bin", ExpectedContent = nestedBytes });
            return files;
        }

        private static void VerifySeededExFatFiles(string imgPath, int diskNum, long partStart, long partLen, List<SeededExFatFile> files, string stage)
        {
            using (var vIo = new ExFatResizer.VirtualFileIo(imgPath))
                ExFatResizer.VerifyExFatVolumeIntegrity(vIo, partStart, partLen);

            var disk = RawDiskEngine.LoadVirtualDisk(imgPath, diskNum);
            var part = disk.Partitions.FirstOrDefault(p => Math.Abs(p.StartingOffset - partStart) < 512L);
            if (part == null || part.FileSystem != FileSystemType.exFAT)
                throw new Exception("exFAT partition missing or misidentified after " + stage);

            for (int i = 0; i < files.Count; i++)
            {
                byte[] actual = ReadSeededExFatFile(disk, part, files[i].RelativePath);
                byte[] exp = files[i].ExpectedContent;
                if (actual.Length != exp.Length)
                    throw new Exception(string.Format("exFAT file {0} size mismatch after {1}: {2} != {3}",
                        files[i].RelativePath, stage, actual.Length, exp.Length));
                for (int b = 0; b < exp.Length; b++)
                {
                    if (actual[b] != exp[b])
                        throw new Exception(string.Format("exFAT file {0} byte 0x{1:X} corrupted after {2}.",
                            files[i].RelativePath, b, stage));
                }
            }
        }

        /// <summary>
        /// Stress 11: Real spec-compliant offline exFAT Grow (K &gt; 0 + Bitmap 1->2 clusters), Shrink (tail evac +
        /// NoFatChain split + Bitmap 2->1 clusters), Overlap Moves, MBR &amp; GPT support, and Pre-Flight Rejections.
        /// </summary>
        private static void RunStress11_ExFatOfflineResizeMoveSpec(string tempDir, Action<string> log)
        {
            log("[Stress 11] Spec-Compliant Offline exFAT Resize (Grow K>0, Shrink+NoFatChain Split, Bitmap Multi-Cluster, Overlap Move, Rejections)...");

            // 1. Small MBR test: 8 MiB -> 24 MiB (K > 0 FAT expansion evacuates System clusters [2..5])
            string mbrImg = Path.Combine(tempDir, "stress11_exfat_mbr.img");
            var mbrDisk = RawDiskEngine.CreateBlankVirtualDisk(mbrImg, 32L * MiB, PartitionTableStyle.MBR);
            mbrDisk.DiskNumber = 915;
            var qmMbr = new OperationQueueManager();
            qmMbr.SetBaselineDisks(null, new[] { mbrDisk });
            qmMbr.Enqueue(MakeCreateOp(915, mbrImg, 1L * MiB, 8L * MiB, FileSystemType.exFAT, "MBR_EXFAT"));
            qmMbr.ApplyAll();

            var mbrFiles = SeedRealExFatVolumeTree(mbrImg, 1L * MiB, 6L * MiB);
            VerifySeededExFatFiles(mbrImg, 915, 1L * MiB, 8L * MiB, mbrFiles, "MBR Initial Seed");

            mbrDisk = RawDiskEngine.LoadVirtualDisk(mbrImg, 915);
            qmMbr.SetBaselineDisks(null, new[] { mbrDisk });
            qmMbr.Enqueue(MakeResizeOp(915, mbrImg, mbrDisk.Partitions[0], 1L * MiB, 24L * MiB));
            qmMbr.ApplyAll();
            VerifySeededExFatFiles(mbrImg, 915, 1L * MiB, 24L * MiB, mbrFiles, "MBR Grow 8M->24M (K>0)");

            // 2. Multi-cluster Bitmap & Tree Evac on GPT: 120 MiB -> 140 MiB (1->2 bitmap clusters + K>0) -> 16 MiB (2->1 + split) -> Move R/L
            string gptImg = Path.Combine(tempDir, "stress11_exfat_gpt.img");
            var gptDisk = RawDiskEngine.CreateBlankVirtualDisk(gptImg, 160L * MiB, PartitionTableStyle.GPT);
            gptDisk.DiskNumber = 916;
            var qmGpt = new OperationQueueManager();
            qmGpt.SetBaselineDisks(null, new[] { gptDisk });
            qmGpt.Enqueue(MakeCreateOp(916, gptImg, 1L * MiB, 120L * MiB, FileSystemType.exFAT, "GPT_EXFAT"));
            qmGpt.ApplyAll();

            // Shrink target later is 16 MiB, so pass 16 MiB as shrink boundary so split file straddles 16 MiB!
            var gptFiles = SeedRealExFatVolumeTree(gptImg, 1L * MiB, 16L * MiB);
            VerifySeededExFatFiles(gptImg, 916, 1L * MiB, 120L * MiB, gptFiles, "GPT Initial Seed 120M");

            // 2a. Grow 120 MiB -> 140 MiB: forces Allocation Bitmap 1->2 clusters AND FAT shift K > 0
            gptDisk = RawDiskEngine.LoadVirtualDisk(gptImg, 916);
            qmGpt.SetBaselineDisks(null, new[] { gptDisk });
            qmGpt.Enqueue(MakeResizeOp(916, gptImg, gptDisk.Partitions[0], 1L * MiB, 140L * MiB));
            qmGpt.ApplyAll();
            VerifySeededExFatFiles(gptImg, 916, 1L * MiB, 140L * MiB, gptFiles, "GPT Grow 120M->140M (Bitmap 1->2 + K>0)");

            // Confirm Allocation Bitmap DataLength really grew >= 4097 bytes (2 clusters)
            byte[] root140 = RawDiskEngine.ReadBytes(gptDisk, 1L * MiB, 512);
            int sec140 = 1 << root140[0x6C];
            uint heap140 = BitConverter.ToUInt32(root140, 0x58);
            uint rootC140 = BitConverter.ToUInt32(root140, 0x60);
            byte[] rDir140 = RawDiskEngine.ReadBytes(gptDisk, 1L * MiB + ((long)heap140 + (long)(rootC140 - 2u) * (1L << root140[0x6D])) * sec140, 4096);
            ulong bmpLen140 = 0;
            for (int off = 0; off < 4096 && rDir140[off] != 0; off += 32)
            {
                if (rDir140[off] == 0x81) { bmpLen140 = BitConverter.ToUInt64(rDir140, off + 24); break; }
            }
            if (bmpLen140 <= 4096UL)
                throw new Exception("Expected 140 MiB Allocation Bitmap DataLength > 4096 (2 clusters), got " + bmpLen140);

            // 2b. Shrink 140 MiB -> 16 MiB: evacuates tail contiguous & fragmented files + Docs cluster1 + /Docs/Deep + Bitmap 2->1
            gptDisk = RawDiskEngine.LoadVirtualDisk(gptImg, 916);
            qmGpt.SetBaselineDisks(null, new[] { gptDisk });
            qmGpt.Enqueue(MakeResizeOp(916, gptImg, gptDisk.Partitions[0], 1L * MiB, 16L * MiB));
            qmGpt.ApplyAll();
            VerifySeededExFatFiles(gptImg, 916, 1L * MiB, 16L * MiB, gptFiles, "GPT Shrink 140M->16M");

            // 2c. Overlap Move Right (1M -> 5M) + Overlap Move Left (5M -> 2M)
            gptDisk = RawDiskEngine.LoadVirtualDisk(gptImg, 916);
            qmGpt.SetBaselineDisks(null, new[] { gptDisk });
            qmGpt.Enqueue(MakeResizeOp(916, gptImg, gptDisk.Partitions[0], 5L * MiB, 16L * MiB));
            var previewMovedRight = qmGpt.GetPreviewDisk(916).Partitions[0];
            qmGpt.Enqueue(MakeResizeOp(916, gptImg, previewMovedRight, 2L * MiB, 16L * MiB));
            qmGpt.ApplyAll();
            VerifySeededExFatFiles(gptImg, 916, 2L * MiB, 16L * MiB, gptFiles, "GPT Overlap Move R+L");

            // 3. Pre-flight Safety Rejections on dirty/corrupt/orphan/tiny-shrink volumes (zero sectors modified!)
            string negImg = Path.Combine(tempDir, "stress11_exfat_rejects.img");
            File.Copy(mbrImg, negImg, true);
            byte[] preBootSnap;
            using (var fs = new FileStream(negImg, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                preBootSnap = new byte[12 * 512];
                fs.Seek(1L * MiB, SeekOrigin.Begin);
                fs.Read(preBootSnap, 0, preBootSnap.Length);
            }

            // 3a. Target shrink smaller than header/minimum clusters
            var negDisk = RawDiskEngine.LoadVirtualDisk(negImg, 917);
            var qmNeg = new OperationQueueManager();
            qmNeg.SetBaselineDisks(null, new[] { negDisk });
            ExpectRejected(delegate
            {
                qmNeg.Enqueue(MakeResizeOp(917, negImg, negDisk.Partitions[0], 1L * MiB, 65536L));
                qmNeg.ApplyAll();
            }, "Shrink exFAT below minimum clusters");

            // 3b. VolumeDirty flag (0x0002) set at +0x6A must reject immediately without modifying boot region
            byte[] dirtyBoot = new byte[12 * 512];
            Array.Copy(preBootSnap, dirtyBoot, preBootSnap.Length);
            dirtyBoot[0x6A] |= 0x02;
            using (var fs = new FileStream(negImg, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                fs.Seek(1L * MiB, SeekOrigin.Begin);
                fs.Write(dirtyBoot, 0, dirtyBoot.Length);
                fs.Flush();
            }
            bool dirtyRejected = false;
            try
            {
                using (var vIo = new ExFatResizer.VirtualFileIo(negImg))
                    ExFatResizer.ResizeOrMoveExFat(vIo, negDisk.Partitions[0], 1L * MiB, 24L * MiB, 1L * MiB, 16L * MiB, true, null, null);
            }
            catch (InvalidOperationException)
            {
                dirtyRejected = true;
            }
            if (!dirtyRejected)
                throw new Exception("Dirty exFAT VolumeFlags (0x0002) should have been rejected.");

            // 3c. Corrupted Boot Checksum (sector 11) must reject cleanly
            byte[] badChkBoot = new byte[12 * 512];
            Array.Copy(preBootSnap, badChkBoot, preBootSnap.Length);
            badChkBoot[11 * 512] ^= 0x5A;
            using (var fs = new FileStream(negImg, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                fs.Seek(1L * MiB, SeekOrigin.Begin);
                fs.Write(badChkBoot, 0, badChkBoot.Length);
                fs.Flush();
            }
            bool chkRejected = false;
            try
            {
                using (var vIo = new ExFatResizer.VirtualFileIo(negImg))
                    ExFatResizer.ResizeOrMoveExFat(vIo, negDisk.Partitions[0], 1L * MiB, 24L * MiB, 1L * MiB, 16L * MiB, true, null, null);
            }
            catch (InvalidDataException)
            {
                chkRejected = true;
            }
            if (!chkRejected)
                throw new Exception("Corrupt exFAT Boot Checksum should have been rejected.");

            // 3d. Orphaned cluster bit inside shrink zone (bitmap mismatch) must restore valid boot first & reject
            using (var fs = new FileStream(negImg, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                fs.Seek(1L * MiB, SeekOrigin.Begin);
                fs.Write(preBootSnap, 0, preBootSnap.Length);
                uint heapMbr = BitConverter.ToUInt32(preBootSnap, 0x58);
                uint rootClusMbr = BitConverter.ToUInt32(preBootSnap, 0x60);
                uint spcMbr = 1u << preBootSnap[0x6D];
                int secMbr = 1 << preBootSnap[0x6C];
                byte[] rMbr = new byte[spcMbr * secMbr];
                fs.Seek(1L * MiB + ((long)heapMbr + (long)(rootClusMbr - 2u) * spcMbr) * secMbr, SeekOrigin.Begin);
                fs.Read(rMbr, 0, rMbr.Length);
                uint bmpClusMbr = 2u;
                for (int off = 0; off < rMbr.Length && rMbr[off] != 0; off += 32)
                {
                    if (rMbr[off] == 0x81) { bmpClusMbr = BitConverter.ToUInt32(rMbr, off + 20); break; }
                }
                long bmpByteOff = 1L * MiB + ((long)heapMbr + (long)(bmpClusMbr - 2u) * spcMbr) * secMbr + 520L; // cluster ~4162 (in tail > 16 MiB)
                fs.Seek(bmpByteOff, SeekOrigin.Begin);
                fs.WriteByte(0x80);
                fs.Flush();
            }
            bool orphanRejected = false;
            try
            {
                using (var vIo = new ExFatResizer.VirtualFileIo(negImg))
                    ExFatResizer.ResizeOrMoveExFat(vIo, negDisk.Partitions[0], 1L * MiB, 24L * MiB, 1L * MiB, 16L * MiB, true, null, null);
            }
            catch (InvalidDataException)
            {
                orphanRejected = true;
            }
            if (!orphanRejected)
                throw new Exception("Orphaned allocated cluster in exFAT shrink zone should have been rejected.");

            log("  -> Stress 11 PASSED (exFAT MBR/GPT Grow K>0, Multi-cluster Bitmap 1<->2, Tail Evac, NoFatChain Split, Overlap Move, Rejections verified).");
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
