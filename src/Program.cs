using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using WinParted.Core;
using WinParted.Interop;
using WinParted.Models;
using WinParted.UI;

namespace WinParted
{
    /// <summary>
    /// Application entrypoint supporting interactive WinForms GUI execution as well as
    /// headless CLI verification modes (<c>--self-test</c> and <c>--stress-test</c>).
    /// </summary>
    internal static class Program
    {
        // Ensures crisp text/ribbon rendering on high-DPI Windows displays (Vista+)
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void TryEnableDpiAwareness()
        {
            SetProcessDPIAware();
        }

        /// <summary>
        /// Program entry point. Dispatches CLI self-test/stress modes or starts the DPI-aware WinForms UI.
        /// </summary>
        [STAThread]
        private static int Main(string[] args)
        {
            if (args != null && args.Any(a => string.Equals(a, "--self-test", StringComparison.OrdinalIgnoreCase) ||
                                              string.Equals(a, "--stress-test", StringComparison.OrdinalIgnoreCase)))
            {
                bool stressOnly = args.Any(a => string.Equals(a, "--stress-test", StringComparison.OrdinalIgnoreCase)) &&
                                  !args.Any(a => string.Equals(a, "--self-test", StringComparison.OrdinalIgnoreCase));
                return stressOnly ? RunStressTestsOnly() : RunSelfTest();
            }

            if (Win32DiskInterop.IsWindowsVistaOrNewer)
            {
                try { TryEnableDpiAwareness(); } catch { }
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }

        /// <summary>
        /// Executes end-to-end smoke checks on physical disk enumeration, GPT/MBR virtual image creation,
        /// queued partition operations, backup/restore, and the full <see cref="StressTestRunner"/> suite.
        /// </summary>
        private static int RunSelfTest()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "WinPartedSelfTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                Console.WriteLine("=== WinParted Self-Test ===");
                Console.WriteLine("Process Architecture: {0}-bit (IntPtr.Size={1})", IntPtr.Size * 8, IntPtr.Size);
                Console.WriteLine("OS 64-bit: {0} | Badge: {1}", Win32DiskInterop.Is64BitOperatingSystem(), Win32DiskInterop.GetArchitectureBadge());

                // 1. Live Physical Drive Discovery
                var physical = Win32DiskInterop.EnumeratePhysicalDisks();
                Console.WriteLine("Discovered {0} physical disk(s).", physical.Count);
                if (physical.Count == 0)
                    throw new Exception("Expected at least 1 physical disk to be enumerated.");

                foreach (var d in physical)
                {
                    Console.WriteLine("  Disk {0}: {1} | {2} | {3} | {4} partitions | Logical={5}B Physical={6}B",
                        d.DiskNumber, d.Model, d.PartitionStyle, ByteFormatter.FormatBytes(d.TotalSizeBytes),
                        d.Partitions.Count, d.LogicalSectorSize, d.PhysicalSectorSize);
                }

                // 2. Create Demo Multi-OS GPT Virtual Disk
                string demoImg = Path.Combine(tempDir, "test_gpt.img");
                var vdisk = RawDiskEngine.CreateDemoVirtualDisk(demoImg, 90);
                if (!vdisk.GptCrcValid)
                    throw new Exception("Demo GPT disk failed CRC32 validation.");
                if (vdisk.Partitions.Count != 4)
                    throw new Exception("Expected 4 partitions in demo disk, found " + vdisk.Partitions.Count);

                AssertFs(vdisk.Partitions[0], FileSystemType.EfiSystem);
                AssertFs(vdisk.Partitions[1], FileSystemType.NTFS);
                AssertFs(vdisk.Partitions[2], FileSystemType.Ext4);
                AssertFs(vdisk.Partitions[3], FileSystemType.LinuxSwap);
                Console.WriteLine("Demo GPT disk verified: EFI(FAT32), NTFS, Ext4, LinuxSwap + CRC32 OK.");

                // 3. Backup Partition Table
                string bakPath = Path.Combine(tempDir, "test_backup.gptbak");
                RawDiskEngine.BackupPartitionTable(vdisk, bakPath);
                if (!File.Exists(bakPath) || new FileInfo(bakPath).Length < 16384)
                    throw new Exception("Backup file missing or too small.");

                // 4. Operation Queue Preview & Apply Test
                var qm = new OperationQueueManager();
                qm.SetBaselineDisks(physical, new[] { vdisk });

                var layout = vdisk.GetFullLayoutWithUnallocated();
                var unalloc = layout.Where(p => p.IsUnallocated).OrderByDescending(p => p.PartitionLength).FirstOrDefault();
                if (unalloc == null || unalloc.PartitionLength < 16L * 1048576L)
                    throw new Exception("Expected trailing unallocated space >= 16 MiB.");

                long createStart = ByteFormatter.AlignUp(unalloc.StartingOffset, 1048576L);
                long createLen = 16L * 1048576L;

                // Enqueue Create Btrfs
                qm.Enqueue(new PendingOperation
                {
                    Type = OperationType.CreatePartition,
                    TargetDiskNumber = 90,
                    StartOffset = createStart,
                    Length = createLen,
                    NewFileSystem = FileSystemType.Btrfs,
                    NewLabel = "btrfs-data",
                    NewPartitionName = "Btrfs Data",
                    Summary = "Create Btrfs 16 MiB"
                });

                // Preview check before Apply
                var preview = qm.GetPreviewDisk(90);
                if (preview.Partitions.Count != 5)
                    throw new Exception("Preview expected 5 partitions, got " + preview.Partitions.Count);

                // Enqueue Resize/Move on Swap (Part #4: 73 MiB, 8 MiB -> shrink to 4 MiB, move +2 MiB)
                var swapPart = vdisk.Partitions[3];
                qm.Enqueue(new PendingOperation
                {
                    Type = OperationType.ResizeMovePartition,
                    TargetDiskNumber = 90,
                    TargetPartitionNumber = swapPart.PartitionNumber,
                    OriginalStartOffset = swapPart.StartingOffset,
                    OriginalLength = swapPart.PartitionLength,
                    StartOffset = swapPart.StartingOffset + 2L * 1048576L,
                    Length = 4L * 1048576L,
                    Summary = "Resize/Move Swap"
                });

                // Enqueue Flags change on NTFS (Part #2 -> mark hidden + readonly)
                var ntfsPart = vdisk.Partitions[1];
                qm.Enqueue(new PendingOperation
                {
                    Type = OperationType.ChangeFlags,
                    TargetDiskNumber = 90,
                    TargetPartitionNumber = ntfsPart.PartitionNumber,
                    OriginalStartOffset = ntfsPart.StartingOffset,
                    OriginalLength = ntfsPart.PartitionLength,
                    NewGptTypeGuid = ntfsPart.GptTypeGuid,
                    NewGptAttributes = (1UL << 60) | (1UL << 62),
                    Summary = "Set flags on NTFS"
                });

                // Enqueue Copy/Paste of Swap into trailing unallocated space
                long pasteStart = createStart + createLen;
                qm.Enqueue(new PendingOperation
                {
                    Type = OperationType.CopyPartition,
                    TargetDiskNumber = 90,
                    SourceDiskNumber = 90,
                    SourceStartOffset = swapPart.StartingOffset + 2L * 1048576L,
                    SourceLength = 4L * 1048576L,
                    StartOffset = pasteStart,
                    Length = 4L * 1048576L,
                    NewFileSystem = FileSystemType.LinuxSwap,
                    NewLabel = "swap-clone",
                    Summary = "Clone Swap"
                });

                // Apply all operations
                var logs = qm.ApplyAll();
                Console.WriteLine("Applied {0} queued operations.", logs.Count);

                var reloaded = RawDiskEngine.LoadVirtualDisk(demoImg, 90);
                if (!reloaded.GptCrcValid)
                    throw new Exception("Reloaded GPT CRC32 invalid after operations!");
                if (reloaded.Partitions.Count != 6)
                    throw new Exception("Expected 6 partitions after create+copy, got " + reloaded.Partitions.Count);

                // Verify Btrfs persisted
                if (!reloaded.Partitions.Any(p => p.FileSystem == FileSystemType.Btrfs && p.Label == "btrfs-data"))
                    throw new Exception("Btrfs partition superblock/label did not persist.");

                // Verify NTFS flags persisted
                var reloadedNtfs = reloaded.Partitions.First(p => p.FileSystem == FileSystemType.NTFS);
                if ((reloadedNtfs.GptAttributes & (1UL << 62)) == 0)
                    throw new Exception("GPT hidden flag did not persist.");

                // 5. Test Wipe + Restore Backup
                qm.SetBaselineDisks(physical, new[] { reloaded });
                var btrfsPart = reloaded.Partitions.First(p => p.FileSystem == FileSystemType.Btrfs);
                qm.Enqueue(new PendingOperation
                {
                    Type = OperationType.WipePartition,
                    TargetDiskNumber = 90,
                    OriginalStartOffset = btrfsPart.StartingOffset,
                    OriginalLength = btrfsPart.PartitionLength,
                    WipePattern = WipePattern.ZeroFill,
                    Summary = "Zero-fill Btrfs"
                });
                qm.ApplyAll();
                var afterWipe = RawDiskEngine.LoadVirtualDisk(demoImg, 90);
                var wipedPart = afterWipe.Partitions.First(p => p.StartingOffset == btrfsPart.StartingOffset);
                if (wipedPart.FileSystem != FileSystemType.RAW)
                    throw new Exception("Wiped partition should read as RAW, got " + wipedPart.FileSystem);

                // Restore backup
                RawDiskEngine.RestorePartitionTable(afterWipe, bakPath);
                var restored = RawDiskEngine.LoadVirtualDisk(demoImg, 90);
                if (restored.Partitions.Count != 4 || !restored.GptCrcValid)
                    throw new Exception("Restored GPT partition table failed verification.");

                // 6. Test MBR creation & parsing
                string mbrImg = Path.Combine(tempDir, "test_mbr.img");
                var mbrDisk = RawDiskEngine.CreateBlankVirtualDisk(mbrImg, 32L * 1048576L, PartitionTableStyle.MBR);
                qm.SetBaselineDisks(physical, new[] { mbrDisk });
                qm.Enqueue(new PendingOperation
                {
                    Type = OperationType.CreatePartition,
                    TargetDiskNumber = 99,
                    StartOffset = 1048576L,
                    Length = 16L * 1048576L,
                    NewFileSystem = FileSystemType.FAT32,
                    NewLabel = "MBRFAT32",
                    NewMbrType = 0x0C,
                    NewMbrBoot = true,
                    Summary = "Create MBR FAT32"
                });
                qm.ApplyAll();
                var reloadedMbr = RawDiskEngine.LoadVirtualDisk(mbrImg, 99);
                if (reloadedMbr.PartitionStyle != PartitionTableStyle.MBR || reloadedMbr.Partitions.Count != 1 || !reloadedMbr.Partitions[0].MbrBootable)
                    throw new Exception("MBR creation/reload verification failed.");

                Console.WriteLine("All GPT, MBR, Multi-OS Superblock, CRC32, Queue, Wipe, Copy, Backup/Restore tests passed!");
                Console.WriteLine("");
                if (!StressTestRunner.RunAll(Console.WriteLine))
                    throw new Exception("StressTestRunner reported failure.");
                Console.WriteLine("SELF_TEST_PASSED");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("SELF_TEST_FAILED: " + ex);
                return 1;
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        private static int RunStressTestsOnly()
        {
            try
            {
                Console.WriteLine("Process Architecture: {0}-bit (IntPtr.Size={1})", IntPtr.Size * 8, IntPtr.Size);
                bool ok = StressTestRunner.RunAll(Console.WriteLine);
                return ok ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("STRESS_TEST_FAILED: " + ex);
                return 1;
            }
        }

        private static void AssertFs(PartitionEntry part, FileSystemType expected)
        {
            if (part.FileSystem != expected)
                throw new Exception(string.Format("Partition #{0} expected {1}, got {2}", part.PartitionNumber, expected, part.FileSystem));
        }
    }
}
