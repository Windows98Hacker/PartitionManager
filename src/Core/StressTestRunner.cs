using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using WinParted.Models;
using WinParted.UI;

namespace WinParted.Core
{
    public static class StressTestRunner
    {
        private const long MiB = 1048576L;

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

                log("STRESS_TEST_ALL_PASSED");
                return true;
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

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
