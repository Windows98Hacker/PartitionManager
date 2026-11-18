using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using WinParted.Core;
using WinParted.Interop;
using WinParted.Models;

namespace WinParted.UI.Dialogs
{
    /// <summary>
    /// Modal dialog for creating a new partition in unallocated space or resizing/moving an existing
    /// partition with an interactive GParted-style drag slider, MiB/Cylinder alignment, and FS selector.
    /// </summary>
    public class CreateOrResizePartitionDialog : Form
    {
        private readonly bool _isCreateMode;
        private readonly long _regionStartBytes;
        private readonly long _regionTotalBytes;
        private readonly long _minSizeBytes;
        private readonly DiskInfo _disk;
        private readonly PartitionEntry _existingPartition;

        private readonly SliderPreviewPanel _sliderBar;
        private readonly NumericUpDown _numBeforeMiB;
        private readonly NumericUpDown _numSizeMiB;
        private readonly NumericUpDown _numAfterMiB;
        private readonly ComboBox _cmbAlign;
        private readonly ComboBox _cmbFs;
        private readonly TextBox _txtLabel;
        private readonly TextBox _txtPartName;
        private readonly Label _lblRangeInfo;

        private bool _updating;

        public long ResultStartOffset { get; private set; }
        public long ResultLengthBytes { get; private set; }
        public FileSystemType ResultFileSystem { get; private set; }
        public string ResultLabel { get; private set; }
        public string ResultPartitionName { get; private set; }

        public CreateOrResizePartitionDialog(
            DiskInfo disk,
            PartitionEntry targetSlice,
            long contiguousStartBytes,
            long contiguousTotalBytes,
            bool isCreateMode)
        {
            _disk = disk;
            _existingPartition = targetSlice;
            _isCreateMode = isCreateMode;
            _regionStartBytes = contiguousStartBytes;
            _regionTotalBytes = contiguousTotalBytes;

            long usedBytes = (!isCreateMode && targetSlice.UsedBytes.HasValue) ? targetSlice.UsedBytes.Value : 0;
            _minSizeBytes = Math.Max(1048576L, ByteFormatter.AlignUp(Math.Max(1048576L, usedBytes), 1048576L));
            if (_minSizeBytes > _regionTotalBytes) _minSizeBytes = _regionTotalBytes;

            Text = isCreateMode ? "Create New Partition" : "Resize / Move Partition — " + targetSlice.ShortDeviceName;
            Size = new Size(890, 560);
            MinimumSize = new Size(760, 480);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9f);

            long initialBefore = isCreateMode ? 0 : Math.Max(0, targetSlice.StartingOffset - _regionStartBytes);
            long initialSize = isCreateMode ? _regionTotalBytes : targetSlice.PartitionLength;
            if (initialBefore + initialSize > _regionTotalBytes)
                initialSize = _regionTotalBytes - initialBefore;

            var rootPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(ThemeManager.F21) };
            Controls.Add(rootPanel);

            var bottomButtons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = ThemeManager.F55,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(0, ThemeManager.F8, 0, ThemeManager.F8)
            };
            var btnCancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Size = new Size(ThemeManager.F89, ThemeManager.F34),
                Margin = new Padding(ThemeManager.F8, 0, 0, 0)
            };
            var btnOk = new Button
            {
                Text = isCreateMode ? "Add Partition" : "Resize / Move",
                DialogResult = DialogResult.OK,
                Size = new Size(ThemeManager.F144, ThemeManager.F34),
                Margin = new Padding(0),
                Tag = "PrimaryButton"
            };
            btnOk.Click += (s, e) => CommitValues();
            bottomButtons.Controls.Add(btnCancel);
            bottomButtons.Controls.Add(btnOk);
            AcceptButton = btnOk;
            CancelButton = btnCancel;

            var formTable = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 8,
                Padding = new Padding(0, 8, 0, 0)
            };
            formTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            formTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            long maxMiB = Math.Max(1, _regionTotalBytes / 1048576L);
            long minMiB = Math.Max(1, _minSizeBytes / 1048576L);

            _numBeforeMiB = AddNumericRow(formTable, 0, "Free space preceding (MiB):", 0, maxMiB - minMiB);
            _numSizeMiB = AddNumericRow(formTable, 1, "New size (MiB):", minMiB, maxMiB);
            _numAfterMiB = AddNumericRow(formTable, 2, "Free space following (MiB):", 0, maxMiB - minMiB);

            _numBeforeMiB.ValueChanged += (s, e) => OnNumericBeforeChanged();
            _numSizeMiB.ValueChanged += (s, e) => OnNumericSizeChanged();
            _numAfterMiB.ValueChanged += (s, e) => OnNumericAfterChanged();

            _cmbAlign = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Margin = new Padding(8, 6, 0, 6)
            };
            _cmbAlign.Items.AddRange(new object[] { "MiB (1024 KiB - Recommended)", "Cylinder (8 MiB)", "None (Sector 512 B)" });
            _cmbAlign.SelectedIndex = 0;
            AddRow(formTable, 3, "Align to:", _cmbAlign);

            _cmbFs = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Enabled = isCreateMode,
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Margin = new Padding(8, 6, 0, 6)
            };
            var fsOptions = new[]
            {
                FileSystemType.NTFS, FileSystemType.FAT32, FileSystemType.FAT16, FileSystemType.exFAT, FileSystemType.ReFS,
                FileSystemType.Ext4, FileSystemType.Ext3, FileSystemType.Ext2, FileSystemType.Btrfs, FileSystemType.XFS,
                FileSystemType.F2FS, FileSystemType.ZFS, FileSystemType.SquashFS, FileSystemType.ISO9660, FileSystemType.UDF,
                FileSystemType.LinuxSwap, FileSystemType.LVM2, FileSystemType.APFS, FileSystemType.HFSPlus, FileSystemType.HFS,
                FileSystemType.EfiSystem, FileSystemType.MicrosoftReserved, FileSystemType.RAW
            };
            foreach (var fs in fsOptions) _cmbFs.Items.Add(fs);
            _cmbFs.SelectedItem = isCreateMode ? FileSystemType.NTFS : (fsOptions.Contains(targetSlice.FileSystem) ? targetSlice.FileSystem : FileSystemType.NTFS);
            _cmbFs.SelectedIndexChanged += (s, e) =>
            {
                if (_cmbFs.SelectedItem is FileSystemType)
                    _sliderBar.SetFileSystem((FileSystemType)_cmbFs.SelectedItem);
            };
            AddRow(formTable, 4, "File system:", _cmbFs);

            _txtPartName = new TextBox
            {
                Text = isCreateMode ? "Basic data partition" : (targetSlice.PartitionName ?? string.Empty),
                Enabled = isCreateMode && disk.PartitionStyle == PartitionTableStyle.GPT,
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Margin = new Padding(8, 6, 0, 6)
            };
            AddRow(formTable, 5, "Partition name (GPT):", _txtPartName);

            _txtLabel = new TextBox
            {
                Text = isCreateMode ? "New Volume" : (targetSlice.Label ?? string.Empty),
                Enabled = isCreateMode,
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Margin = new Padding(8, 6, 0, 6)
            };
            AddRow(formTable, 6, "Volume label:", _txtLabel);

            rootPanel.Controls.Add(formTable);

            _lblRangeInfo = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = false,
                Height = 30,
                Font = new Font("Segoe UI", 8.5f),
                TextAlign = ContentAlignment.MiddleLeft
            };
            rootPanel.Controls.Add(_lblRangeInfo);

            _sliderBar = new SliderPreviewPanel(_regionTotalBytes, _minSizeBytes, initialBefore, initialSize,
                isCreateMode ? FileSystemType.NTFS : targetSlice.FileSystem, usedBytes);
            _sliderBar.Dock = DockStyle.Top;
            _sliderBar.Height = ThemeManager.F89;
            _sliderBar.Tag = "SliderPanel";
            _sliderBar.RangeChanged += (s, e) => SyncFromBytes(_sliderBar.BeforeBytes, _sliderBar.SizeBytes);
            rootPanel.Controls.Add(_sliderBar);

            // Add bottomButtons last so Dock=Bottom reserves space first before Top/Fill controls
            rootPanel.Controls.Add(bottomButtons);

            SyncFromBytes(initialBefore, initialSize);
            ThemeManager.ApplyTheme(this);
        }

        private void AddRow(TableLayoutPanel table, int rowIdx, string labelText, Control input)
        {
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var lbl = new Label
            {
                Text = labelText,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 9, 12, 6)
            };
            table.Controls.Add(lbl, 0, rowIdx);
            table.Controls.Add(input, 1, rowIdx);
        }

        private NumericUpDown AddNumericRow(TableLayoutPanel table, int rowIdx, string label, long min, long max)
        {
            var nud = new NumericUpDown
            {
                Minimum = Math.Max(0, min),
                Maximum = Math.Max(Math.Max(0, min), max),
                ThousandsSeparator = true,
                Width = 180,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(8, 6, 0, 6)
            };
            AddRow(table, rowIdx, label, nud);
            return nud;
        }

        private void SyncFromBytes(long beforeBytes, long sizeBytes)
        {
            _updating = true;
            try
            {
                long beforeMiB = Math.Max(0, beforeBytes / 1048576L);
                long sizeMiB = Math.Max((long)_numSizeMiB.Minimum, sizeBytes / 1048576L);
                long totalMiB = Math.Max(1, _regionTotalBytes / 1048576L);
                if (beforeMiB + sizeMiB > totalMiB)
                    sizeMiB = Math.Max((long)_numSizeMiB.Minimum, totalMiB - beforeMiB);
                long afterMiB = Math.Max(0, totalMiB - beforeMiB - sizeMiB);

                _numBeforeMiB.Value = Math.Min(_numBeforeMiB.Maximum, Math.Max(_numBeforeMiB.Minimum, beforeMiB));
                _numSizeMiB.Value = Math.Min(_numSizeMiB.Maximum, Math.Max(_numSizeMiB.Minimum, sizeMiB));
                _numAfterMiB.Value = Math.Min(_numAfterMiB.Maximum, Math.Max(_numAfterMiB.Minimum, afterMiB));

                _sliderBar.SetRange(beforeMiB * 1048576L, sizeMiB * 1048576L);
                long startOff = _regionStartBytes + beforeMiB * 1048576L;
                long endOff = startOff + sizeMiB * 1048576L;
                _lblRangeInfo.Text = string.Format("Start: 0x{0:X} (LBA {1:N0})  ->  End: 0x{2:X}  |  Total span: {3}",
                    startOff, startOff / Math.Max(512, _disk.LogicalSectorSize), endOff, ByteFormatter.FormatBytes(_regionTotalBytes));
            }
            finally
            {
                _updating = false;
            }
        }

        private void OnNumericBeforeChanged()
        {
            if (_updating) return;
            long before = (long)_numBeforeMiB.Value * 1048576L;
            long size = (long)_numSizeMiB.Value * 1048576L;
            if (before + size > _regionTotalBytes)
                size = Math.Max(_minSizeBytes, _regionTotalBytes - before);
            SyncFromBytes(before, size);
        }

        private void OnNumericSizeChanged()
        {
            if (_updating) return;
            long before = (long)_numBeforeMiB.Value * 1048576L;
            long size = (long)_numSizeMiB.Value * 1048576L;
            if (before + size > _regionTotalBytes)
                before = Math.Max(0, _regionTotalBytes - size);
            SyncFromBytes(before, size);
        }

        private void OnNumericAfterChanged()
        {
            if (_updating) return;
            long after = (long)_numAfterMiB.Value * 1048576L;
            long size = (long)_numSizeMiB.Value * 1048576L;
            long before = Math.Max(0, _regionTotalBytes - after - size);
            SyncFromBytes(before, size);
        }

        private void CommitValues()
        {
            long alignUnit = 1048576L;
            if (_cmbAlign.SelectedIndex == 1) alignUnit = 8L * 1048576L;
            else if (_cmbAlign.SelectedIndex == 2) alignUnit = Math.Max(512, _disk.LogicalSectorSize);

            long rawStart = _regionStartBytes + (long)_numBeforeMiB.Value * 1048576L;
            long rawLen = (long)_numSizeMiB.Value * 1048576L;

            long alignedStart = ByteFormatter.AlignUp(rawStart, alignUnit);
            if (alignedStart < _regionStartBytes) alignedStart = _regionStartBytes;
            if (alignedStart > _regionStartBytes + _regionTotalBytes - _minSizeBytes)
                alignedStart = rawStart;

            long maxLen = (_regionStartBytes + _regionTotalBytes) - alignedStart;
            long alignedLen = Math.Min(maxLen, ByteFormatter.AlignDown(rawLen, Math.Min(alignUnit, maxLen)));
            if (alignedLen < _minSizeBytes) alignedLen = Math.Min(maxLen, _minSizeBytes);

            ResultStartOffset = alignedStart;
            ResultLengthBytes = alignedLen;
            ResultFileSystem = _cmbFs.SelectedItem is FileSystemType ? (FileSystemType)_cmbFs.SelectedItem : FileSystemType.NTFS;
            ResultLabel = _txtLabel.Text.Trim();
            ResultPartitionName = _txtPartName.Text.Trim();
        }

        private class SliderPreviewPanel : Panel
        {
            private readonly long _totalBytes;
            private readonly long _minBytes;
            private readonly long _usedBytes;
            private FileSystemType _fs;
            private long _beforeBytes;
            private long _sizeBytes;
            private int _dragMode; // 0=none, 1=left, 2=right, 3=move
            private int _dragStartX;
            private long _dragOrigBefore;
            private long _dragOrigSize;

            public long BeforeBytes { get { return _beforeBytes; } }
            public long SizeBytes { get { return _sizeBytes; } }
            public event EventHandler RangeChanged;

            public SliderPreviewPanel(long totalBytes, long minBytes, long beforeBytes, long sizeBytes, FileSystemType fs, long usedBytes)
            {
                DoubleBuffered = true;
                _totalBytes = Math.Max(1, totalBytes);
                _minBytes = minBytes;
                _beforeBytes = beforeBytes;
                _sizeBytes = sizeBytes;
                _fs = fs;
                _usedBytes = usedBytes;
                BackColor = Color.White;
                BorderStyle = BorderStyle.FixedSingle;
            }

            public void SetFileSystem(FileSystemType fs) { _fs = fs; Invalidate(); }

            public void SetRange(long before, long size)
            {
                _beforeBytes = before;
                _sizeBytes = size;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                Graphics g = e.Graphics;
                g.Clear(ThemeManager.BackElevated);

                float pad = 6f;
                float trackW = Width - pad * 2f;
                float x = pad + (float)(trackW * ((double)_beforeBytes / _totalBytes));
                float w = Math.Max(16f, (float)(trackW * ((double)_sizeBytes / _totalBytes)));
                if (x + w > Width - pad) w = Math.Max(10f, Width - pad - x);

                var rect = new RectangleF(x, 8f, w, Height - 16f);
                using (var fillBr = new SolidBrush(FileSystemColors.GetFillColor(_fs)))
                    g.FillRectangle(fillBr, rect);

                if (_usedBytes > 0 && _sizeBytes > 0)
                {
                    float uw = (float)(rect.Width * Math.Min(1.0, (double)_usedBytes / _sizeBytes));
                    using (var usedBr = new SolidBrush(FileSystemColors.GetUsedColor(_fs)))
                        g.FillRectangle(usedBr, rect.X, rect.Y, uw, rect.Height);
                }

                using (var pen = new Pen(FileSystemColors.GetBorderColor(_fs), 2.5f))
                    g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);

                // Drag handles on left and right edges
                using (var handleBr = new SolidBrush(ThemeManager.TextPrimary))
                {
                    g.FillRectangle(handleBr, rect.X - 3, rect.Y + 12, 6, rect.Height - 24);
                    g.FillRectangle(handleBr, rect.Right - 3, rect.Y + 12, 6, rect.Height - 24);
                }
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                float pad = 6f;
                float trackW = Width - pad * 2f;
                float x = pad + (float)(trackW * ((double)_beforeBytes / _totalBytes));
                float w = Math.Max(16f, (float)(trackW * ((double)_sizeBytes / _totalBytes)));

                _dragStartX = e.X;
                _dragOrigBefore = _beforeBytes;
                _dragOrigSize = _sizeBytes;

                if (Math.Abs(e.X - x) <= 8) _dragMode = 1;
                else if (Math.Abs(e.X - (x + w)) <= 8) _dragMode = 2;
                else if (e.X > x && e.X < x + w) _dragMode = 3;
                else _dragMode = 0;
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                float pad = 6f;
                float trackW = Math.Max(1f, Width - pad * 2f);

                if (_dragMode == 0)
                {
                    float x = pad + (float)(trackW * ((double)_beforeBytes / _totalBytes));
                    float w = Math.Max(16f, (float)(trackW * ((double)_sizeBytes / _totalBytes)));
                    if (Math.Abs(e.X - x) <= 8 || Math.Abs(e.X - (x + w)) <= 8)
                        Cursor = Cursors.SizeWE;
                    else if (e.X > x && e.X < x + w)
                        Cursor = Cursors.SizeAll;
                    else
                        Cursor = Cursors.Default;
                    return;
                }

                long deltaBytes = (long)(((e.X - _dragStartX) / trackW) * _totalBytes);
                if (_dragMode == 1)
                {
                    long maxShift = _dragOrigSize - _minBytes;
                    long newBefore = Math.Max(0, Math.Min(_dragOrigBefore + maxShift, _dragOrigBefore + deltaBytes));
                    newBefore = ByteFormatter.AlignDown(newBefore, 1048576L);
                    _beforeBytes = newBefore;
                    _sizeBytes = (_dragOrigBefore + _dragOrigSize) - newBefore;
                }
                else if (_dragMode == 2)
                {
                    long newSize = Math.Max(_minBytes, Math.Min(_totalBytes - _dragOrigBefore, _dragOrigSize + deltaBytes));
                    _sizeBytes = Math.Max(_minBytes, ByteFormatter.AlignDown(newSize, 1048576L));
                }
                else if (_dragMode == 3)
                {
                    long newBefore = Math.Max(0, Math.Min(_totalBytes - _dragOrigSize, _dragOrigBefore + deltaBytes));
                    _beforeBytes = ByteFormatter.AlignDown(newBefore, 1048576L);
                }
                Invalidate();
                if (RangeChanged != null) RangeChanged(this, EventArgs.Empty);
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                _dragMode = 0;
            }
        }
    }

    /// <summary>
    /// Modal dialog for editing GPT partition type GUIDs, GPT attribute flags, or MBR partition IDs and active flags.
    /// </summary>
    public class ManageFlagsDialog : Form
    {
        private readonly DiskInfo _disk;
        private readonly ComboBox _cmbType;
        private readonly CheckBox _chkBootEsp;
        private readonly CheckBox _chkLegacyBoot;
        private readonly CheckBox _chkReadOnly;
        private readonly CheckBox _chkHidden;
        private readonly CheckBox _chkNoAutomount;
        private readonly CheckBox _chkMbrBoot;

        public Guid ResultGptTypeGuid { get; private set; }
        public ulong ResultGptAttributes { get; private set; }
        public byte ResultMbrType { get; private set; }
        public bool ResultMbrBoot { get; private set; }

        public ManageFlagsDialog(DiskInfo disk, PartitionEntry part)
        {
            _disk = disk;
            Text = "Manage Partition Flags — " + part.ShortDeviceName;
            Size = new Size(744, 460);
            MinimumSize = new Size(640, 430);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9f);

            var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(ThemeManager.F21) };
            Controls.Add(root);

            var btnPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = ThemeManager.F55,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(0, ThemeManager.F8, 0, ThemeManager.F8)
            };
            var btnCancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Size = new Size(ThemeManager.F89, ThemeManager.F34),
                Margin = new Padding(ThemeManager.F8, 0, 0, 0)
            };
            var btnOk = new Button
            {
                Text = "Apply Flags",
                DialogResult = DialogResult.OK,
                Size = new Size(ThemeManager.F144, ThemeManager.F34),
                Margin = new Padding(0),
                Tag = "PrimaryButton"
            };
            btnPanel.Controls.Add(btnCancel);
            btnPanel.Controls.Add(btnOk);

            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true
            };
            root.Controls.Add(flow);
            // Add bottom button bar after Fill panel so Dock=Bottom reserves space first
            root.Controls.Add(btnPanel);

            flow.Controls.Add(new Label
            {
                Text = disk.PartitionStyle == PartitionTableStyle.GPT ? "GPT Partition Type GUID:" : "MBR Partition Type ID:",
                AutoSize = true,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Margin = new Padding(0, 0, 0, 6)
            });

            _cmbType = new ComboBox
            {
                Width = ThemeManager.F610,
                DropDownWidth = 650,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Margin = new Padding(0, 0, 0, 12)
            };
            if (disk.PartitionStyle == PartitionTableStyle.GPT)
            {
                _cmbType.Items.Add(new TypeOption("Basic Data (Windows NTFS / FAT / exFAT)", GptPartitionTypeGuids.BasicData, 0x07));
                _cmbType.Items.Add(new TypeOption("EFI System Partition (boot, esp)", GptPartitionTypeGuids.EfiSystem, 0xEF));
                _cmbType.Items.Add(new TypeOption("Microsoft Reserved (msftres)", GptPartitionTypeGuids.MicrosoftReserved, 0x0C));
                _cmbType.Items.Add(new TypeOption("Windows Recovery Environment (diag)", GptPartitionTypeGuids.WindowsRecovery, 0x27));
                _cmbType.Items.Add(new TypeOption("Linux Filesystem Data (ext2/3/4, btrfs, xfs, f2fs, squashfs)", GptPartitionTypeGuids.LinuxFilesystem, 0x83));
                _cmbType.Items.Add(new TypeOption("Linux Swap", GptPartitionTypeGuids.LinuxSwap, 0x82));
                _cmbType.Items.Add(new TypeOption("Linux LVM", GptPartitionTypeGuids.LinuxLvm, 0x8E));
                _cmbType.Items.Add(new TypeOption("OpenZFS / Solaris Pool Member", GptPartitionTypeGuids.ZfsMember, 0xBF));
                _cmbType.Items.Add(new TypeOption("BIOS Boot Partition (bios_grub)", GptPartitionTypeGuids.BiosBoot, 0x00));
                _cmbType.Items.Add(new TypeOption("Apple APFS Container", GptPartitionTypeGuids.AppleApfs, 0xAF));
                _cmbType.Items.Add(new TypeOption("Apple HFS+ / HFSX", GptPartitionTypeGuids.AppleHfs, 0xAF));
                _cmbType.Items.Add(new TypeOption("Apple Boot / Recovery HD", GptPartitionTypeGuids.AppleBoot, 0xAB));

                int sel = 0;
                for (int i = 0; i < _cmbType.Items.Count; i++)
                {
                    if (((TypeOption)_cmbType.Items[i]).GptGuid == part.GptTypeGuid) { sel = i; break; }
                }
                _cmbType.SelectedIndex = sel;
            }
            else
            {
                _cmbType.Items.Add(new TypeOption("0x07 — NTFS / exFAT / HPFS", Guid.Empty, 0x07));
                _cmbType.Items.Add(new TypeOption("0x0C — FAT32 (LBA)", Guid.Empty, 0x0C));
                _cmbType.Items.Add(new TypeOption("0x0E — FAT16 (LBA)", Guid.Empty, 0x0E));
                _cmbType.Items.Add(new TypeOption("0x83 — Linux Native (ext2/3/4, XFS, Btrfs, F2FS, SquashFS)", Guid.Empty, 0x83));
                _cmbType.Items.Add(new TypeOption("0x82 — Linux Swap", Guid.Empty, 0x82));
                _cmbType.Items.Add(new TypeOption("0x8E — Linux LVM", Guid.Empty, 0x8E));
                _cmbType.Items.Add(new TypeOption("0xBF — OpenZFS / Solaris", Guid.Empty, 0xBF));
                _cmbType.Items.Add(new TypeOption("0xCD — ISO9660 / UDF Optical Image", Guid.Empty, 0xCD));
                _cmbType.Items.Add(new TypeOption("0xAF — Apple HFS / HFS+ / APFS", Guid.Empty, 0xAF));
                _cmbType.Items.Add(new TypeOption("0xAB — Apple Boot", Guid.Empty, 0xAB));
                _cmbType.Items.Add(new TypeOption("0xEF — EFI System Partition", Guid.Empty, 0xEF));
                _cmbType.Items.Add(new TypeOption("0x27 — Windows Recovery", Guid.Empty, 0x27));

                int sel = 0;
                for (int i = 0; i < _cmbType.Items.Count; i++)
                {
                    if (((TypeOption)_cmbType.Items[i]).MbrId == part.MbrPartitionType) { sel = i; break; }
                }
                _cmbType.SelectedIndex = sel;
            }
            flow.Controls.Add(_cmbType);

            bool isGpt = disk.PartitionStyle == PartitionTableStyle.GPT;
            _chkBootEsp = new CheckBox { Text = "boot / esp (Mark as EFI System Partition)", AutoSize = true, Margin = new Padding(2, 4, 2, 4), Checked = part.GptTypeGuid == GptPartitionTypeGuids.EfiSystem, Enabled = isGpt };
            _chkLegacyBoot = new CheckBox { Text = "legacy_boot (GPT BIOS bootable attribute)", AutoSize = true, Margin = new Padding(2, 4, 2, 4), Checked = (part.GptAttributes & 0x4UL) != 0, Enabled = isGpt };
            _chkReadOnly = new CheckBox { Text = "readonly (Bit 60)", AutoSize = true, Margin = new Padding(2, 4, 2, 4), Checked = (part.GptAttributes & (1UL << 60)) != 0, Enabled = isGpt };
            _chkHidden = new CheckBox { Text = "hidden (Bit 62)", AutoSize = true, Margin = new Padding(2, 4, 2, 4), Checked = (part.GptAttributes & (1UL << 62)) != 0, Enabled = isGpt };
            _chkNoAutomount = new CheckBox { Text = "no_automount (Bit 63)", AutoSize = true, Margin = new Padding(2, 4, 2, 4), Checked = (part.GptAttributes & (1UL << 63)) != 0, Enabled = isGpt };
            _chkMbrBoot = new CheckBox { Text = "Active / Bootable Flag (0x80 MBR)", AutoSize = true, Margin = new Padding(2, 4, 2, 4), Checked = part.MbrBootable, Enabled = !isGpt };

            flow.Controls.AddRange(new Control[] { _chkBootEsp, _chkLegacyBoot, _chkReadOnly, _chkHidden, _chkNoAutomount, _chkMbrBoot });

            btnOk.Click += (s, e) =>
            {
                var opt = (TypeOption)_cmbType.SelectedItem;
                ResultGptTypeGuid = _chkBootEsp.Checked ? GptPartitionTypeGuids.EfiSystem : opt.GptGuid;
                ulong attrs = 0;
                if (_chkLegacyBoot.Checked) attrs |= 0x4UL;
                if (_chkReadOnly.Checked) attrs |= (1UL << 60);
                if (_chkHidden.Checked) attrs |= (1UL << 62);
                if (_chkNoAutomount.Checked) attrs |= (1UL << 63);
                ResultGptAttributes = attrs;
                ResultMbrType = opt.MbrId;
                ResultMbrBoot = _chkMbrBoot.Checked;
            };
            AcceptButton = btnOk;
            CancelButton = btnCancel;
            ThemeManager.ApplyTheme(this);
        }

        private class TypeOption
        {
            public string Text;
            public Guid GptGuid;
            public byte MbrId;
            public TypeOption(string t, Guid g, byte m) { Text = t; GptGuid = g; MbrId = m; }
            public override string ToString() { return Text; }
        }
    }

    /// <summary>
    /// Modal dialog allowing the user to select a destructive wipe pattern (Zero, Ones, or PRNG Random) before queuing.
    /// </summary>
    public class WipePartitionDialog : Form
    {
        private readonly RadioButton _rbZero;
        private readonly RadioButton _rbOnes;
        private readonly RadioButton _rbRandom;

        public WipePattern SelectedPattern
        {
            get
            {
                if (_rbOnes.Checked) return WipePattern.OnesFill;
                if (_rbRandom.Checked) return WipePattern.RandomDoD;
                return WipePattern.ZeroFill;
            }
        }

        public bool ExecuteImmediately { get; private set; }

        public WipePartitionDialog(PartitionEntry part)
        {
            Text = "Secure Wipe Partition — " + part.ShortDeviceName;
            Size = new Size(550, 340);
            MinimumSize = new Size(518, 320);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9f);

            var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(ThemeManager.F21) };
            Controls.Add(root);

            var btnPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = ThemeManager.F55,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(0, ThemeManager.F8, 0, ThemeManager.F8)
            };
            var btnCancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Size = new Size(ThemeManager.F89, ThemeManager.F34),
                Margin = new Padding(ThemeManager.F8, 0, 0, 0)
            };
            var btnOk = new Button
            {
                Text = "Queue Wipe",
                DialogResult = DialogResult.OK,
                Size = new Size(110, ThemeManager.F34),
                Margin = new Padding(ThemeManager.F8, 0, 0, 0),
                Tag = "TonalButton"
            };
            btnOk.Click += delegate { ExecuteImmediately = false; };
            var btnWipeNow = new Button
            {
                Text = "Wipe Now",
                DialogResult = DialogResult.OK,
                Size = new Size(110, ThemeManager.F34),
                Margin = new Padding(0),
                Tag = "DangerButton"
            };
            btnWipeNow.Click += delegate { ExecuteImmediately = true; };
            btnPanel.Controls.Add(btnCancel);
            btnPanel.Controls.Add(btnOk);
            btnPanel.Controls.Add(btnWipeNow);

            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false
            };
            root.Controls.Add(flow);
            root.Controls.Add(btnPanel);

            flow.Controls.Add(new Label
            {
                Text = string.Format("Overwrite all {0} in {1} ({2}):",
                    ByteFormatter.FormatBytes(part.PartitionLength), part.ShortDeviceName,
                    string.IsNullOrEmpty(part.Label) ? "no label" : part.Label),
                AutoSize = true,
                MaximumSize = new Size(490, 0),
                Margin = new Padding(0, 0, 0, 12)
            });

            _rbZero = new RadioButton { Text = "Zero-Fill (0x00) — Fast clean wipe", AutoSize = true, Margin = new Padding(4, 6, 0, 6), Checked = true };
            _rbOnes = new RadioButton { Text = "Ones-Fill (0xFF) — Flash/SSD pattern", AutoSize = true, Margin = new Padding(4, 6, 0, 6) };
            _rbRandom = new RadioButton { Text = "Cryptographic / PRNG Random Data — Secure erase", AutoSize = true, Margin = new Padding(4, 6, 0, 6) };
            flow.Controls.AddRange(new Control[] { _rbZero, _rbOnes, _rbRandom });
            AcceptButton = btnOk;
            CancelButton = btnCancel;
            ThemeManager.ApplyTheme(this);
        }
    }

    /// <summary>
    /// Live raw 512-byte sector hex dump and superblock/MBR/GPT header decoder for any physical or virtual disk LBA.
    /// </summary>
    public class HexSectorInspectorDialog : Form
    {
        private readonly DiskInfo _disk;
        private readonly NumericUpDown _numLba;
        private readonly TextBox _txtHex;
        private readonly TextBox _txtDecode;

        public HexSectorInspectorDialog(DiskInfo disk, long initialLba = 0)
        {
            _disk = disk;
            Text = "Low-Level Sector & Hex Inspector — " + disk.DisplayName;
            Size = new Size(1068, 660);
            MinimumSize = new Size(890, 550);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9f);

            var topBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = ThemeManager.F89,
                Padding = new Padding(ThemeManager.F13, ThemeManager.F8, ThemeManager.F13, ThemeManager.F8),
                Tag = "ElevatedPanel"
            };

            var row2 = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Padding = new Padding(0, ThemeManager.F5, 0, 0)
            };
            topBar.Controls.Add(row2);

            var row1 = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = ThemeManager.F34,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false
            };
            topBar.Controls.Add(row1);

            row1.Controls.Add(new Label { Text = "LBA Sector:", AutoSize = true, Margin = new Padding(0, ThemeManager.F5, ThemeManager.F8, 0) });
            long maxLba = Math.Max(0, disk.TotalSizeBytes / Math.Max(512, disk.LogicalSectorSize) - 1);
            _numLba = new NumericUpDown
            {
                Width = ThemeManager.F144,
                Minimum = 0,
                Maximum = maxLba,
                Value = Math.Min(maxLba, Math.Max(0, initialLba)),
                ThousandsSeparator = true,
                Margin = new Padding(0, 2, ThemeManager.F8, 0)
            };
            row1.Controls.Add(_numLba);

            var btnGo = new Button
            {
                Text = "Read Sector",
                Size = new Size(110, 26),
                Margin = new Padding(0, 1, 0, 0),
                Tag = "PrimaryButton"
            };
            btnGo.Click += (s, e) => LoadSector((long)_numLba.Value);
            row1.Controls.Add(btnGo);

            AddQuickBtn(row2, "LBA 0 (MBR)", 0);
            AddQuickBtn(row2, "LBA 1 (GPT Hdr)", 1);
            AddQuickBtn(row2, "LBA 2 (GPT Entries)", 2);
            foreach (var p in disk.Partitions.Where(x => !x.IsUnallocated).Take(4))
            {
                AddQuickBtn(row2, string.Format("Part #{0} Boot", p.PartitionNumber), p.StartLba);
            }

            Action syncTopBar = () =>
            {
                if (topBar.IsDisposed || row2.IsDisposed) return;
                int availW = Math.Max(ThemeManager.F233, topBar.ClientSize.Width - topBar.Padding.Horizontal);
                int rows = 1;
                int curX = row2.Padding.Left;
                int rowH = ThemeManager.F34;
                foreach (Control c in row2.Controls)
                {
                    if (!c.Visible) continue;
                    int w = c.Width > 0 ? c.Width : c.GetPreferredSize(Size.Empty).Width;
                    int span = w + c.Margin.Horizontal;
                    if (c.Height + c.Margin.Vertical > rowH) rowH = c.Height + c.Margin.Vertical;
                    if (curX > row2.Padding.Left && curX + span > availW)
                    {
                        rows++;
                        curX = row2.Padding.Left;
                    }
                    curX += span;
                }
                int desiredH = Math.Max(ThemeManager.F89, topBar.Padding.Vertical + row1.Height + row2.Padding.Vertical + Math.Max(ThemeManager.F34, rows * rowH + ThemeManager.F5));
                if (topBar.Height != desiredH)
                    topBar.Height = desiredH;
            };
            topBar.Resize += (s, e) => syncTopBar();

            var split = new SplitContainer
            {
                Size = new Size(890, 550),
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                Panel1MinSize = ThemeManager.F144,
                Panel2MinSize = ThemeManager.F89,
                SplitterWidth = ThemeManager.F8
            };
            ThemeManager.ApplyGoldenSplitter(split, true, ThemeManager.F144, ThemeManager.F89);
            split.Resize += (s, e) => ThemeManager.ApplyGoldenSplitter(split, true, ThemeManager.F144, ThemeManager.F89);

            var bottomBar = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = ThemeManager.F55,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(ThemeManager.F21, ThemeManager.F8, ThemeManager.F21, ThemeManager.F8),
                Tag = "ElevatedPanel"
            };
            var btnCloseHex = new Button
            {
                Text = "Close",
                DialogResult = DialogResult.OK,
                Size = new Size(ThemeManager.F89, ThemeManager.F34),
                Margin = new Padding(0),
                Tag = "TonalButton"
            };
            bottomBar.Controls.Add(btnCloseHex);
            CancelButton = btnCloseHex;

            Controls.Add(split);
            Controls.Add(topBar);
            Controls.Add(bottomBar);
            Load += (s, e) =>
            {
                syncTopBar();
                ThemeManager.ApplyGoldenSplitter(split, true, ThemeManager.F144, ThemeManager.F89);
            };

            _txtHex = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                WordWrap = false,
                ScrollBars = ScrollBars.Both,
                Font = new Font("Consolas", 9.5f),
                BackColor = Color.FromArgb(15, 23, 42),
                ForeColor = Color.FromArgb(226, 232, 240),
                Tag = "ConsoleColors"
            };
            split.Panel1.Controls.Add(_txtHex);

            _txtDecode = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                WordWrap = false,
                ScrollBars = ScrollBars.Both,
                Font = new Font("Consolas", 9f)
            };
            split.Panel2.Controls.Add(_txtDecode);

            LoadSector((long)_numLba.Value);
            Shown += (s, e) =>
            {
                _txtHex.SelectionLength = 0;
                _txtDecode.SelectionLength = 0;
                _numLba.Focus();
            };
            ThemeManager.ApplyTheme(this);
        }

        private void AddQuickBtn(Control parent, string text, long lba)
        {
            Size phi = ThemeManager.GoldenButtonSize(26, text, Font);
            var btn = new Button
            {
                Text = text,
                Size = phi,
                Margin = new Padding(0, ThemeManager.F3, ThemeManager.F5, ThemeManager.F3)
            };
            btn.Click += (s, e) =>
            {
                _numLba.Value = Math.Min(_numLba.Maximum, Math.Max(0, lba));
                LoadSector(lba);
            };
            parent.Controls.Add(btn);
        }

        private void LoadSector(long lba)
        {
            int secSize = Math.Max(512, _disk.LogicalSectorSize);
            try
            {
                byte[] bytes = RawDiskEngine.ReadBytes(_disk, lba * secSize, secSize);
                _txtHex.Text = FormatHexDump(bytes, lba * secSize);
                _txtDecode.Text = DecodeSectorStructure(lba, bytes);
            }
            catch (Exception ex)
            {
                _txtHex.Text = "Unable to read raw sector LBA " + lba + ":\r\n" + ex.Message +
                               "\r\n\r\nNote: Reading raw physical drive sectors on Windows requires Administrator elevation. Virtual Disk (.img) files can always be inspected without elevation.";
                _txtDecode.Text = "Raw read unavailable.";
            }
        }

        private string FormatHexDump(byte[] data, long baseOffset)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < data.Length; i += 16)
            {
                sb.AppendFormat("{0:X8}  ", baseOffset + i);
                for (int j = 0; j < 16; j++)
                {
                    if (i + j < data.Length) sb.AppendFormat("{0:X2} ", data[i + j]);
                    else sb.Append("   ");
                    if (j == 7) sb.Append(" ");
                }
                sb.Append(" |");
                for (int j = 0; j < 16 && i + j < data.Length; j++)
                {
                    byte b = data[i + j];
                    sb.Append(b >= 32 && b < 127 ? (char)b : '.');
                }
                sb.AppendLine("|");
            }
            return sb.ToString();
        }

        private string DecodeSectorStructure(long lba, byte[] b)
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Format("=== Structural Decoder for LBA {0:N0} (Offset 0x{1:X}) ===", lba, lba * b.Length));

            if (lba == 0 && b.Length >= 512)
            {
                sb.AppendLine(string.Format("Boot Signature: 0x{0:X2}{1:X2} ({2})", b[510], b[511], (b[510] == 0x55 && b[511] == 0xAA) ? "VALID 0x55AA" : "MISSING"));
                sb.AppendLine(string.Format("MBR Disk Signature: 0x{0:X8}", BitConverter.ToUInt32(b, 440)));
                for (int i = 0; i < 4; i++)
                {
                    int off = 446 + i * 16;
                    byte type = b[off + 4];
                    uint start = BitConverter.ToUInt32(b, off + 8);
                    uint count = BitConverter.ToUInt32(b, off + 12);
                    sb.AppendLine(string.Format("  MBR Slot {0}: Boot=0x{1:X2}, Type=0x{2:X2} ({3}), StartLBA={4}, Sectors={5}",
                        i + 1, b[off], type, type == 0xEE ? "GPT Protective" : Win32DiskInterop.GuessFsFromMbrType(type).ToString(), start, count));
                }
            }
            else if (lba == 1 && b.Length >= 92 && BitConverter.ToUInt64(b, 0) == 0x5452415020494645UL)
            {
                uint hdrCrc = BitConverter.ToUInt32(b, 16);
                byte[] copy = (byte[])b.Clone();
                Array.Clear(copy, 16, 4);
                uint calcCrc = Crc32.Compute(copy, 0, Math.Min(92, copy.Length));
                byte[] g = new byte[16];
                Array.Copy(b, 56, g, 0, 16);
                sb.AppendLine("Signature: \"EFI PART\" (Valid GPT Header)");
                sb.AppendLine(string.Format("Header CRC32: 0x{0:X8} (Computed: 0x{1:X8} -> {2})", hdrCrc, calcCrc, hdrCrc == calcCrc ? "VALID" : "MISMATCH"));
                sb.AppendLine(string.Format("Disk GUID: {0}", new Guid(g)));
                sb.AppendLine(string.Format("First Usable LBA: {0:N0} | Last Usable LBA: {1:N0}", BitConverter.ToInt64(b, 40), BitConverter.ToInt64(b, 48)));
                sb.AppendLine(string.Format("Partition Entries: {0} entries x {1} bytes @ LBA {2} | Array CRC32: 0x{3:X8}",
                    BitConverter.ToInt32(b, 80), BitConverter.ToInt32(b, 84), BitConverter.ToInt64(b, 72), BitConverter.ToUInt32(b, 88)));
            }
            else if (b.Length >= 512)
            {
                string oem = Encoding.ASCII.GetString(b, 3, 8);
                sb.AppendLine("OEM / Boot ID (+3): \"" + oem.Replace("\0", ".") + "\"");
                if (b.Length >= 0x60 && Encoding.ASCII.GetString(b, 0x52, 8) == "FAT32   ")
                    sb.AppendLine("Detected FAT32 Boot Sector | Label: " + Encoding.ASCII.GetString(b, 0x47, 11).Trim());
                if (b.Length >= 0x40 && Encoding.ASCII.GetString(b, 0x36, 8) == "FAT16   ")
                    sb.AppendLine("Detected FAT16 Boot Sector | Label: " + Encoding.ASCII.GetString(b, 0x2B, 11).Trim());
                if (b.Length >= 11 && Encoding.ASCII.GetString(b, 3, 4) == "ReFS")
                    sb.AppendLine("Detected Windows ReFS Volume Header");
                if (BitConverter.ToUInt32(b, 0) == 0x73717368u)
                    sb.AppendLine(string.Format("Detected SquashFS 4.0 Superblock (hsqs) | Inodes={0} | BlockSize={1} B",
                        BitConverter.ToUInt32(b, 4), BitConverter.ToUInt32(b, 12)));
                if (Encoding.ASCII.GetString(b, 0, 4) == "XFSB")
                {
                    uint beBlk = ((uint)b[4] << 24) | ((uint)b[5] << 16) | ((uint)b[6] << 8) | b[7];
                    uint leBlk = BitConverter.ToUInt32(b, 4);
                    sb.AppendLine(string.Format("Detected XFS Superblock (XFSB) | BlockSize={0} B | Label={1}",
                        (beBlk >= 512 && beBlk <= 65536) ? beBlk : leBlk,
                        Encoding.UTF8.GetString(b, 108, 12).TrimEnd('\0', ' ')));
                }
                if (b.Length >= 0x48 && Encoding.ASCII.GetString(b, 0x40, 8) == "_BHRfS_M")
                    sb.AppendLine("Detected Btrfs Superblock (_BHRfS_M)");
                if (BitConverter.ToUInt32(b, 0) == 0xF2F52010u)
                    sb.AppendLine("Detected F2FS Superblock (0xF2F52010 @ +1024 B)");
                if (b.Length >= 10 && Encoding.ASCII.GetString(b, 1, 5) == "CD001")
                    sb.AppendLine("Detected ISO9660 Descriptor (CD001) | Type=" + b[0]);
                if (b.Length >= 10 && (Encoding.ASCII.GetString(b, 1, 5) == "BEA01" || Encoding.ASCII.GetString(b, 1, 5) == "NSR02" || Encoding.ASCII.GetString(b, 1, 5) == "NSR03"))
                    sb.AppendLine("Detected UDF Descriptor (" + Encoding.ASCII.GetString(b, 1, 5) + ")");
                if (BitConverter.ToUInt64(b, 0) == 0x00BAB10CUL)
                    sb.AppendLine("Detected OpenZFS Uberblock (0x00BAB10C)");
                if (b.Length >= 0x440 && BitConverter.ToUInt16(b, 0x438) == 0xEF53)
                    sb.AppendLine("Detected Ext2/Ext3/Ext4 Superblock (0xEF53)");
                if (b.Length >= 36 && (Encoding.ASCII.GetString(b, 32, 4) == "NXSB" || Encoding.ASCII.GetString(b, 32, 4) == "APSB"))
                    sb.AppendLine("Detected Apple APFS Container/Volume Superblock (" + Encoding.ASCII.GetString(b, 32, 4) + ")");
                if (b.Length >= 4 && Encoding.ASCII.GetString(b, 0, 2) == "H+")
                    sb.AppendLine("Detected Apple HFS+ Volume Header");
                else if (b.Length >= 4 && Encoding.ASCII.GetString(b, 0, 2) == "HX")
                    sb.AppendLine("Detected Apple HFSX Volume Header");
                else if (b.Length >= 4 && Encoding.ASCII.GetString(b, 0, 2) == "BD")
                    sb.AppendLine("Detected Macintosh Classic HFS Master Directory Block");
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Audits 1 MiB / 4 KiB physical alignment and GPT/MBR structural health for every partition on the selected disk.
    /// </summary>
    public class AlignmentAndHealthDialog : Form
    {
        public AlignmentAndHealthDialog(DiskInfo disk)
        {
            Text = "Alignment & Disk Health Analyzer — " + disk.DisplayName;
            Size = new Size(890, 550);
            MinimumSize = new Size(760, 470);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9f);

            var sb = new StringBuilder();
            sb.AppendLine("DEVICE PROPERTIES");
            sb.AppendLine("  Model / Path      : " + disk.Model + " (" + disk.DevicePath + ")");
            sb.AppendLine("  Bus / Interface   : " + disk.BusTypeName);
            sb.AppendLine("  Partition Table   : " + disk.PartitionStyle + (disk.PartitionStyle == PartitionTableStyle.GPT ? (disk.GptCrcValid ? " (CRC32 VALID)" : " (CRC32 MISMATCH)") : ""));
            sb.AppendLine("  Logical / Physical: " + disk.LogicalSectorSize + " B / " + disk.PhysicalSectorSize + " B");
            sb.AppendLine("  Total Capacity    : " + ByteFormatter.FormatBytes(disk.TotalSizeBytes));
            sb.AppendLine();
            sb.AppendLine("PARTITION ALIGNMENT AUDIT (4K Physical Sector & 1 MiB SSD Boundary)");
            sb.AppendLine(new string('-', 95));
            sb.AppendLine(string.Format("{0,-6} {1,-16} {2,-14} {3,-14} {4,-10} {5,-12}",
                "Part#", "Filesystem", "Start LBA", "Offset", "4K Align", "1 MiB Align"));
            sb.AppendLine(new string('-', 95));

            foreach (var p in disk.Partitions.Where(x => !x.IsUnallocated))
            {
                sb.AppendLine(string.Format("{0,-6} {1,-16} {2,-14:N0} {3,-14} {4,-10} {5,-12}",
                    "#" + p.PartitionNumber,
                    FileSystemColors.GetDisplayName(p.FileSystem),
                    p.StartLba,
                    ByteFormatter.FormatBytes(p.StartingOffset),
                    p.IsAligned4K ? "PASS" : "MISALIGNED",
                    p.IsAligned1MiB ? "OPTIMAL" : "Suboptimal"));
            }

            var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(ThemeManager.F21) };
            Controls.Add(root);

            var bottomBar = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = ThemeManager.F55,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(0, ThemeManager.F8, 0, ThemeManager.F8)
            };
            var btnClose = new Button
            {
                Text = "Close",
                DialogResult = DialogResult.OK,
                Size = new Size(ThemeManager.F89, ThemeManager.F34),
                Margin = new Padding(0),
                Tag = "PrimaryButton"
            };
            bottomBar.Controls.Add(btnClose);

            var txt = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                WordWrap = false,
                BorderStyle = BorderStyle.FixedSingle,
                ScrollBars = ScrollBars.Both,
                Font = new Font("Consolas", 9.5f),
                Text = sb.ToString()
            };
            root.Controls.Add(txt);
            root.Controls.Add(bottomBar);
            AcceptButton = btnClose;
            CancelButton = btnClose;
            Shown += (s, e) =>
            {
                txt.SelectionStart = 0;
                txt.SelectionLength = 0;
                btnClose.Focus();
            };
            ThemeManager.ApplyTheme(this);
        }
    }

    /// <summary>
    /// Flicker-free custom progress bar supporting deterministic percentage fills and animated indeterminate
    /// pulses in both Light and Dark themes.
    /// </summary>
    internal sealed class SmoothProgressBar : Control
    {
        private int _value;
        private bool _isIndeterminate;
        private float _pulsePhase;
        private Color _barColor = Color.Empty;
        private string _overlayText = string.Empty;

        public SmoothProgressBar()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.UserPaint,
                true);
            DoubleBuffered = true;
            Height = 24;
        }

        public int Value
        {
            get { return _value; }
            set
            {
                int clamped = Math.Max(0, Math.Min(100, value));
                if (_value != clamped)
                {
                    _value = clamped;
                    Invalidate();
                }
            }
        }

        public bool IsIndeterminate
        {
            get { return _isIndeterminate; }
            set
            {
                if (_isIndeterminate != value)
                {
                    _isIndeterminate = value;
                    Invalidate();
                }
            }
        }

        public Color BarColor
        {
            get { return _barColor; }
            set
            {
                if (_barColor != value)
                {
                    _barColor = value;
                    Invalidate();
                }
            }
        }

        public string OverlayText
        {
            get { return _overlayText; }
            set
            {
                string next = value ?? string.Empty;
                if (!string.Equals(_overlayText, next, StringComparison.Ordinal))
                {
                    _overlayText = next;
                    Invalidate();
                }
            }
        }

        public void AdvancePulse()
        {
            if (!_isIndeterminate) return;
            _pulsePhase += 0.065f;
            if (_pulsePhase > 1.35f)
                _pulsePhase = -0.35f;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (ClientSize.Width < 4 || ClientSize.Height < 4) return;

            Graphics g = e.Graphics;
            SmoothingMode prevMode = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var outerRect = new RectangleF(0.5f, 0.5f, ClientSize.Width - 1f, ClientSize.Height - 1f);
            Color fillBase = _barColor.IsEmpty ? ThemeManager.AccentBlue : _barColor;
            RectangleF fillClipRect = RectangleF.Empty;

            using (GraphicsPath trackPath = ThemeManager.CreateRoundedRectPath(outerRect, 6f))
            using (var trackBrush = new SolidBrush(ThemeManager.BackContainerHigh))
            using (var borderPen = new Pen(ThemeManager.BorderColor, 1f))
            {
                g.FillPath(trackBrush, trackPath);

                Region prevClip = g.Clip;
                g.SetClip(trackPath);

                if (_isIndeterminate)
                {
                    float segW = Math.Max(48f, ClientSize.Width * 0.28f);
                    float segX = _pulsePhase * ClientSize.Width - segW * 0.3f;
                    var pulseRect = new RectangleF(segX, 1f, segW, ClientSize.Height - 2f);
                    using (var pulseBrush = new LinearGradientBrush(
                        pulseRect,
                        Color.FromArgb(40, fillBase),
                        fillBase,
                        LinearGradientMode.Horizontal))
                    {
                        var cb = new ColorBlend(3);
                        cb.Positions = new float[] { 0f, 0.5f, 1f };
                        cb.Colors = new Color[]
                        {
                            Color.FromArgb(30, fillBase),
                            fillBase,
                            Color.FromArgb(30, fillBase)
                        };
                        pulseBrush.InterpolationColors = cb;
                        g.FillRectangle(pulseBrush, pulseRect);
                    }
                    fillClipRect = pulseRect;
                }
                else if (_value > 0)
                {
                    float fillW = Math.Max(4f, (ClientSize.Width - 2f) * (_value / 100f));
                    fillClipRect = new RectangleF(1f, 1f, fillW, ClientSize.Height - 2f);
                    Color lighter = ControlPaint.Light(fillBase, 0.16f);
                    using (var fillBrush = new LinearGradientBrush(
                        fillClipRect,
                        lighter,
                        fillBase,
                        LinearGradientMode.Vertical))
                    {
                        g.FillRectangle(fillBrush, fillClipRect);
                    }
                }

                string text = !string.IsNullOrEmpty(_overlayText)
                    ? _overlayText
                    : (_isIndeterminate ? "Working..." : string.Format("{0}%", _value));

                Rectangle textBounds = ClientRectangle;
                using (var textFont = new Font("Segoe UI", 8.5f, FontStyle.Bold))
                {
                    TextFormatFlags tff = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
                    TextRenderer.DrawText(g, text, textFont, textBounds, ThemeManager.TextPrimary, tff);

                    if (!fillClipRect.IsEmpty && fillClipRect.Width > 2f)
                    {
                        g.SetClip(trackPath);
                        g.IntersectClip(fillClipRect);
                        TextRenderer.DrawText(g, text, textFont, textBounds, Color.White, tff);
                    }
                }

                g.Clip = prevClip;
                g.DrawPath(borderPen, trackPath);
            }

            g.SmoothingMode = prevMode;
        }
    }

    /// <summary>
    /// Non-blocking modal progress monitor that runs queued disk operations on a background thread
    /// and displays overall progress, active sub-stage, MiB/s throughput, per-operation status,
    /// and a live execution log without freezing the UI thread.
    /// </summary>
    public sealed class ApplyOperationsProgressDialog : Form
    {
        private sealed class OperationRowState
        {
            public string StatusText;
            public string PercentText;
            public string ElapsedText;
            public DateTime? StartedUtc;
            public DateTime? FinishedUtc;
            public bool IsActive;
            public bool IsDone;
            public bool IsFailed;
        }

        private readonly OperationQueueManager _queueManager;
        private readonly object _sync = new object();
        private readonly OperationRowState[] _rowStates;
        private readonly Queue<string> _pendingLogs = new Queue<string>();
        private readonly Stopwatch _totalSw = new Stopwatch();
        private readonly System.Windows.Forms.Timer _uiTimer;

        private readonly Label _lblOpHeader;
        private readonly Label _lblElapsed;
        private readonly Label _lblStage;
        private readonly Label _lblByteMetric;
        private readonly Label _lblBottomHint;
        private readonly SmoothProgressBar _barOverall;
        private readonly SmoothProgressBar _barCurrent;
        private readonly ListView _lvOps;
        private readonly TextBox _txtLog;
        private readonly TableLayoutPanel _bottomBar;
        private readonly Button _btnCopyLog;
        private readonly Button _btnClose;

        private OperationProgressReport _latestReport;
        private volatile bool _isRunning;
        private volatile bool _workerFinished;

        public List<string> AppliedResults { get; private set; }
        public Exception ExecutionError { get; private set; }
        public bool CompletedSuccessfully
        {
            get { return !_isRunning && _workerFinished && ExecutionError == null && AppliedResults != null; }
        }

        public ApplyOperationsProgressDialog(OperationQueueManager queueManager)
        {
            if (queueManager == null) throw new ArgumentNullException("queueManager");
            _queueManager = queueManager;

            int count = _queueManager.Queue.Count;
            _rowStates = new OperationRowState[count];
            for (int i = 0; i < count; i++)
            {
                _rowStates[i] = new OperationRowState
                {
                    StatusText = "Queued",
                    PercentText = "0%",
                    ElapsedText = "-"
                };
            }

            Text = count == 1
                ? "Applying 1 Partition Operation"
                : string.Format("Applying {0} Pending Partition Operations", count);
            Rectangle initWa = Screen.FromPoint(Cursor.Position).WorkingArea;
            int initMaxW = Math.Max(480, (int)(initWa.Width * 0.92));
            int initMaxH = Math.Max(340, (int)(initWa.Height * 0.92));
            Size = new Size(Math.Min(820, initMaxW), Math.Min(540, initMaxH));
            MinimumSize = new Size(Math.Min(560, initMaxW), Math.Min(380, initMaxH));
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = true;
            MinimizeBox = false;
            Font = new Font("Segoe UI", 9f);

            var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(ThemeManager.F13) };
            Controls.Add(root);

            var headerPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = Math.Max(192, Font.Height * 12 + 24),
                Padding = new Padding(ThemeManager.F13),
                Tag = "SurfacePanel"
            };

            var titleRow = new Panel { Dock = DockStyle.Top, Height = 28 };
            _lblElapsed = new Label
            {
                Dock = DockStyle.Right,
                AutoSize = true,
                Text = "Elapsed: 00:00.0",
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                ForeColor = ThemeManager.AccentGold,
                TextAlign = ContentAlignment.MiddleRight,
                Tag = "KeepForeColor"
            };
            _lblOpHeader = new Label
            {
                Dock = DockStyle.Fill,
                Text = count > 0
                    ? string.Format("Preparing {0} pending operation{1}...", count, count == 1 ? "" : "s")
                    : "No operations queued.",
                Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                UseMnemonic = false
            };
            titleRow.Controls.Add(_lblOpHeader);
            titleRow.Controls.Add(_lblElapsed);

            _lblStage = new Label
            {
                Dock = DockStyle.Top,
                Height = 24,
                Text = "Initializing partition execution worker...",
                Font = new Font("Segoe UI", 9f),
                ForeColor = ThemeManager.TextSecondary,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                UseMnemonic = false,
                Tag = "KeepForeColor"
            };

            var lblOverall = new Label
            {
                Dock = DockStyle.Top,
                Height = 20,
                Text = "Overall Queue Progress:",
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                TextAlign = ContentAlignment.BottomLeft
            };
            _barOverall = new SmoothProgressBar { Dock = DockStyle.Top, Height = 26 };

            var currentHeaderRow = new Panel { Dock = DockStyle.Top, Height = 24 };
            var lblCurrent = new Label
            {
                Dock = DockStyle.Left,
                AutoSize = true,
                Text = "Current Operation Progress:",
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                TextAlign = ContentAlignment.BottomLeft
            };
            _lblByteMetric = new Label
            {
                Dock = DockStyle.Right,
                AutoSize = true,
                Text = string.Empty,
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = ThemeManager.TextSecondary,
                TextAlign = ContentAlignment.BottomRight,
                UseMnemonic = false,
                Tag = "KeepForeColor"
            };
            currentHeaderRow.Controls.Add(lblCurrent);
            currentHeaderRow.Controls.Add(_lblByteMetric);

            _barCurrent = new SmoothProgressBar { Dock = DockStyle.Top, Height = 26 };

            // Dock=Top controls stack in reverse Add order cleanly
            headerPanel.Controls.Add(_barCurrent);
            headerPanel.Controls.Add(currentHeaderRow);
            headerPanel.Controls.Add(_barOverall);
            headerPanel.Controls.Add(lblOverall);
            headerPanel.Controls.Add(_lblStage);
            headerPanel.Controls.Add(titleRow);

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterWidth = 6,
                Panel1MinSize = 52,
                Panel2MinSize = 52
            };

            _lvOps = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = false,
                HideSelection = false,
                BorderStyle = BorderStyle.FixedSingle
            };
            _lvOps.Columns.Add("#", 32);
            _lvOps.Columns.Add("Disk", 78);
            _lvOps.Columns.Add("Operation", 300);
            _lvOps.Columns.Add("Status", 96);
            _lvOps.Columns.Add("Progress", 66);
            _lvOps.Columns.Add("Time", 58);

            for (int i = 0; i < count; i++)
            {
                var op = _queueManager.Queue[i];
                var d = _queueManager.GetBaselineDisk(op.TargetDiskNumber);
                string diskTag = (d != null && d.IsVirtualImage)
                    ? "Virtual #" + op.TargetDiskNumber
                    : "Disk " + op.TargetDiskNumber;

                var item = new ListViewItem((i + 1).ToString());
                item.UseItemStyleForSubItems = false;
                item.SubItems.Add(diskTag);
                item.SubItems.Add(op.Summary ?? op.Type.ToString());
                var stSub = item.SubItems.Add("Queued");
                stSub.ForeColor = ThemeManager.TextSecondary;
                item.SubItems.Add("0%");
                item.SubItems.Add("-");
                _lvOps.Items.Add(item);
            }
            split.Panel1.Controls.Add(_lvOps);

            _txtLog = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                WordWrap = false,
                ScrollBars = ScrollBars.Both,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Consolas", 9f),
                BackColor = Color.FromArgb(15, 23, 42),
                ForeColor = Color.FromArgb(203, 213, 225),
                Tag = "ConsoleColors"
            };
            split.Panel2.Controls.Add(_txtLog);

            _bottomBar = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(0),
                Padding = new Padding(0, ThemeManager.F8, 0, 2)
            };
            _bottomBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            _bottomBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _bottomBar.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            var btnFlow = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                FlowDirection = FlowDirection.LeftToRight,
                Anchor = AnchorStyles.Right | AnchorStyles.Top | AnchorStyles.Bottom,
                Margin = new Padding(ThemeManager.F8, 0, 0, 0),
                Padding = new Padding(0, 2, 0, 2)
            };
            _btnClose = new Button
            {
                Text = "Running...",
                Enabled = false,
                DialogResult = DialogResult.OK,
                Size = new Size(105, ThemeManager.F34),
                Margin = new Padding(ThemeManager.F8, 0, 0, 0),
                Tag = "PrimaryButton"
            };
            _btnCopyLog = new Button
            {
                Text = "Copy Log",
                Size = new Size(105, ThemeManager.F34),
                Margin = new Padding(0),
                Tag = "TonalButton"
            };
            _btnCopyLog.Click += (s, e) =>
            {
                try
                {
                    if (!string.IsNullOrEmpty(_txtLog.Text))
                        Clipboard.SetText(_txtLog.Text);
                }
                catch { }
            };
            btnFlow.Controls.Add(_btnCopyLog);
            btnFlow.Controls.Add(_btnClose);

            _lblBottomHint = new Label
            {
                Dock = DockStyle.Fill,
                Text = "Applying changes in background — please wait for all disk operations to finish.",
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = ThemeManager.TextSecondary,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                UseMnemonic = false,
                Margin = new Padding(0),
                Tag = "KeepForeColor"
            };
            _bottomBar.Controls.Add(_lblBottomHint, 0, 0);
            _bottomBar.Controls.Add(btnFlow, 1, 0);

            root.Controls.Add(split);
            root.Controls.Add(headerPanel);
            root.Controls.Add(_bottomBar);

            AcceptButton = _btnClose;
            CancelButton = _btnClose;
            ThemeManager.ApplyTheme(this);
            _bottomBar.Height = Math.Max(ThemeManager.F55, Math.Max(_btnCopyLog.Height, _btnClose.Height) + 20);
            _bottomBar.PerformLayout();

            Action autoSizeOpsColumns = () =>
            {
                if (_lvOps == null || _lvOps.Columns.Count < 6) return;
                int fixedW = _lvOps.Columns[0].Width + _lvOps.Columns[1].Width +
                             _lvOps.Columns[3].Width + _lvOps.Columns[4].Width + _lvOps.Columns[5].Width + 4;
                int opW = Math.Max(120, _lvOps.ClientSize.Width - fixedW);
                if (_lvOps.Columns[2].Width != opW)
                    _lvOps.Columns[2].Width = opW;
            };
            _lvOps.SizeChanged += (s, e) => autoSizeOpsColumns();

            _uiTimer = new System.Windows.Forms.Timer { Interval = 75 };
            _uiTimer.Tick += (s, e) => FlushUiState();

            Action clampToWorkingArea = () =>
            {
                Rectangle wa = Screen.FromControl(this).WorkingArea;
                int maxW = Math.Max(480, (int)(wa.Width * 0.92));
                int maxH = Math.Max(340, (int)(wa.Height * 0.92));
                MinimumSize = new Size(Math.Min(560, maxW), Math.Min(380, maxH));
                int w = Math.Min(Width, maxW);
                int h = Math.Min(Height, maxH);
                if (w != Width || h != Height)
                    Size = new Size(w, h);
            };
            Load += (s, e) => clampToWorkingArea();

            Shown += (s, e) =>
            {
                clampToWorkingArea();
                ThemeManager.ApplyGoldenSplitter(split, false, 52, 52);
                autoSizeOpsColumns();
                StartExecutionWorker();
            };

            FormClosing += (s, e) =>
            {
                if (_isRunning && e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    System.Media.SystemSounds.Beep.Play();
                    return;
                }
                _uiTimer.Stop();
            };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _uiTimer != null)
            {
                _uiTimer.Stop();
                _uiTimer.Dispose();
            }
            base.Dispose(disposing);
        }

        private void StartExecutionWorker()
        {
            if (_isRunning || _workerFinished) return;
            _isRunning = true;
            _totalSw.Reset();
            _totalSw.Start();
            _uiTimer.Start();

            var worker = new Thread(() =>
            {
                try
                {
                    AppliedResults = _queueManager.ApplyAll(null, OnWorkerProgress);
                }
                catch (Exception ex)
                {
                    ExecutionError = ex;
                    lock (_sync)
                    {
                        _pendingLogs.Enqueue("[FATAL] " + ex.Message);
                    }
                }
                finally
                {
                    _totalSw.Stop();
                    _isRunning = false;
                    _workerFinished = true;
                    try
                    {
                        if (IsHandleCreated && !IsDisposed)
                            BeginInvoke(new MethodInvoker(FlushUiState));
                    }
                    catch { }
                }
            });
            worker.IsBackground = true;
            worker.Name = "WinParted-OperationWorker";
            worker.Start();
        }

        private void OnWorkerProgress(OperationProgressReport report)
        {
            if (report == null) return;
            lock (_sync)
            {
                _latestReport = report;
                if (!string.IsNullOrEmpty(report.LogLine))
                    _pendingLogs.Enqueue(report.LogLine);

                int idx = report.OperationIndex;
                if (idx >= 0 && idx < _rowStates.Length)
                {
                    OperationRowState rs = _rowStates[idx];
                    if (!rs.StartedUtc.HasValue)
                        rs.StartedUtc = DateTime.UtcNow;
                    rs.IsActive = !report.IsOperationComplete && !report.IsOperationFailed;
                    rs.PercentText = report.IsIndeterminateStage && !report.IsOperationComplete && !report.IsOperationFailed
                        ? "..."
                        : string.Format("{0}%", report.OperationPercent);

                    if (report.IsOperationComplete)
                    {
                        rs.IsDone = true;
                        rs.IsFailed = false;
                        rs.StatusText = "Completed";
                        rs.PercentText = "100%";
                        rs.FinishedUtc = DateTime.UtcNow;
                    }
                    else if (report.IsOperationFailed)
                    {
                        rs.IsFailed = true;
                        rs.IsDone = false;
                        rs.StatusText = "Failed";
                        rs.FinishedUtc = DateTime.UtcNow;
                    }
                    else
                    {
                        rs.StatusText = "Running";
                    }
                }
            }
        }

        private void FlushUiState()
        {
            OperationProgressReport report;
            var newLogs = new List<string>();
            bool finished;
            Exception err;

            lock (_sync)
            {
                report = _latestReport;
                while (_pendingLogs.Count > 0)
                    newLogs.Add(_pendingLogs.Dequeue());
                finished = _workerFinished;
                err = ExecutionError;
            }

            TimeSpan totalElapsed = _totalSw.Elapsed;
            _lblElapsed.Text = string.Format("Elapsed: {0:00}:{1:00}.{2}",
                (int)totalElapsed.TotalMinutes, totalElapsed.Seconds, totalElapsed.Milliseconds / 100);

            if (report != null)
            {
                _lblOpHeader.Text = string.Format("Operation {0} of {1}: {2}",
                    report.OperationIndex + 1,
                    Math.Max(1, report.TotalOperations),
                    report.OperationSummary ?? string.Empty);
                _lblStage.Text = report.CurrentStage ?? string.Empty;
                _barOverall.Value = report.OverallPercent;
                _barCurrent.IsIndeterminate = report.IsIndeterminateStage && !finished;
                _barCurrent.Value = report.OperationPercent;

                if (report.BytesTotal > 0)
                {
                    _lblByteMetric.Text = string.Format("{0} / {1} ({2:F1} MiB/s)",
                        ByteFormatter.FormatBytes(report.BytesProcessed),
                        ByteFormatter.FormatBytes(report.BytesTotal),
                        report.ThroughputMiBPerSec);
                }
                else
                {
                    _lblByteMetric.Text = report.IsIndeterminateStage ? "External tool active..." : string.Empty;
                }
            }

            if (_barCurrent.IsIndeterminate)
                _barCurrent.AdvancePulse();

            for (int i = 0; i < _rowStates.Length && i < _lvOps.Items.Count; i++)
            {
                string statusText;
                string percentText;
                string baseElapsedText;
                DateTime? startedUtc;
                DateTime? finishedUtc;
                bool isDone;
                bool isFailed;
                bool isActive;

                lock (_sync)
                {
                    OperationRowState rs = _rowStates[i];
                    statusText = rs.StatusText;
                    percentText = rs.PercentText;
                    baseElapsedText = rs.ElapsedText;
                    startedUtc = rs.StartedUtc;
                    finishedUtc = rs.FinishedUtc;
                    isDone = rs.IsDone;
                    isFailed = rs.IsFailed;
                    isActive = rs.IsActive;
                }

                string elapsedStr = baseElapsedText;
                if (startedUtc.HasValue)
                {
                    DateTime endT = finishedUtc.HasValue ? finishedUtc.Value : DateTime.UtcNow;
                    double sec = Math.Max(0.0, (endT - startedUtc.Value).TotalSeconds);
                    elapsedStr = string.Format("{0:F1}s", sec);
                }

                ListViewItem item = _lvOps.Items[i];
                if (item.SubItems[3].Text != statusText)
                    item.SubItems[3].Text = statusText;
                if (item.SubItems[4].Text != percentText)
                    item.SubItems[4].Text = percentText;
                if (item.SubItems[5].Text != elapsedStr)
                    item.SubItems[5].Text = elapsedStr;

                Color stColor = isDone
                    ? ThemeManager.AccentGreen
                    : (isFailed ? ThemeManager.AccentRed : (isActive ? ThemeManager.AccentGold : ThemeManager.TextSecondary));
                if (item.SubItems[3].ForeColor != stColor)
                    item.SubItems[3].ForeColor = stColor;
            }

            if (newLogs.Count > 0)
            {
                var sb = new StringBuilder();
                foreach (string line in newLogs)
                    sb.AppendLine(line);
                _txtLog.AppendText(sb.ToString());
                _txtLog.SelectionStart = _txtLog.TextLength;
                _txtLog.ScrollToCaret();
            }

            if (finished)
            {
                _uiTimer.Stop();
                _barCurrent.IsIndeterminate = false;

                if (err == null)
                {
                    _barOverall.BarColor = ThemeManager.AccentGreen;
                    _barCurrent.BarColor = ThemeManager.AccentGreen;
                    _barOverall.Value = 100;
                    _barCurrent.Value = 100;
                    _barOverall.OverlayText = "100% — Complete";
                    _barCurrent.OverlayText = "100% — Verified";
                    _lblOpHeader.Text = "All queued partition operations completed and verified.";
                    _lblStage.Text = string.Format("Finished {0} operation{1} in {2:F2}s.",
                        _rowStates.Length, _rowStates.Length == 1 ? "" : "s", totalElapsed.TotalSeconds);
                    _lblBottomHint.Text = "All operations succeeded. Click Done to return to WinParted.";
                    _lblBottomHint.ForeColor = ThemeManager.AccentGreen;
                }
                else
                {
                    _barOverall.BarColor = ThemeManager.AccentRed;
                    _barCurrent.BarColor = ThemeManager.AccentRed;
                    _lblOpHeader.Text = "Partition operation halted due to an error.";
                    _lblStage.Text = err.Message;
                    _lblBottomHint.Text = "Execution failed — inspect the log output before closing.";
                    _lblBottomHint.ForeColor = ThemeManager.AccentRed;
                }

                _btnClose.Text = "Done";
                _btnClose.Enabled = true;
                _bottomBar.PerformLayout();
                _btnClose.Invalidate();
                _btnClose.Focus();
            }
        }
    }
}
