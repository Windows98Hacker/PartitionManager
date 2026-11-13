using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
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
            Size = new Size(650, 540);
            MinimumSize = new Size(620, 510);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9f);

            long initialBefore = isCreateMode ? 0 : Math.Max(0, targetSlice.StartingOffset - _regionStartBytes);
            long initialSize = isCreateMode ? _regionTotalBytes : targetSlice.PartitionLength;
            if (initialBefore + initialSize > _regionTotalBytes)
                initialSize = _regionTotalBytes - initialBefore;

            var rootPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16) };
            Controls.Add(rootPanel);

            var bottomButtons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 44,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(0, 6, 0, 0)
            };
            var btnCancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Size = new Size(96, 32),
                Margin = new Padding(8, 0, 0, 0)
            };
            var btnOk = new Button
            {
                Text = isCreateMode ? "Add Partition" : "Resize / Move",
                DialogResult = DialogResult.OK,
                Size = new Size(118, 32),
                Tag = "PrimaryButton"
            };
            btnOk.Click += (s, e) => CommitValues();
            bottomButtons.Controls.Add(btnCancel);
            bottomButtons.Controls.Add(btnOk);
            rootPanel.Controls.Add(bottomButtons);
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
                FileSystemType.LinuxSwap, FileSystemType.APFS, FileSystemType.HFSPlus, FileSystemType.HFS,
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
                Height = 28,
                Font = new Font("Segoe UI", 8.5f),
                TextAlign = ContentAlignment.MiddleLeft
            };
            rootPanel.Controls.Add(_lblRangeInfo);

            _sliderBar = new SliderPreviewPanel(_regionTotalBytes, _minSizeBytes, initialBefore, initialSize,
                isCreateMode ? FileSystemType.NTFS : targetSlice.FileSystem, usedBytes);
            _sliderBar.Dock = DockStyle.Top;
            _sliderBar.Height = 72;
            _sliderBar.Tag = "SliderPanel";
            _sliderBar.RangeChanged += (s, e) => SyncFromBytes(_sliderBar.BeforeBytes, _sliderBar.SizeBytes);
            rootPanel.Controls.Add(_sliderBar);

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
            Size = new Size(500, 420);
            MinimumSize = new Size(460, 390);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9f);

            var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16) };
            Controls.Add(root);

            var btnPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 44,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(0, 6, 0, 0)
            };
            var btnCancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(94, 32), Margin = new Padding(8, 0, 0, 0) };
            var btnOk = new Button { Text = "Apply Flags", DialogResult = DialogResult.OK, Size = new Size(106, 32), Tag = "PrimaryButton" };
            btnPanel.Controls.Add(btnCancel);
            btnPanel.Controls.Add(btnOk);
            root.Controls.Add(btnPanel);

            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true
            };
            root.Controls.Add(flow);

            flow.Controls.Add(new Label
            {
                Text = disk.PartitionStyle == PartitionTableStyle.GPT ? "GPT Partition Type GUID:" : "MBR Partition Type ID:",
                AutoSize = true,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Margin = new Padding(0, 0, 0, 6)
            });

            _cmbType = new ComboBox { Width = 440, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 0, 0, 12) };
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

        public WipePartitionDialog(PartitionEntry part)
        {
            Text = "Secure Wipe Partition — " + part.ShortDeviceName;
            Size = new Size(480, 280);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9f);

            var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16) };
            Controls.Add(root);

            var btnPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 44,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(0, 6, 0, 0)
            };
            var btnCancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(92, 32), Margin = new Padding(8, 0, 0, 0) };
            var btnOk = new Button { Text = "Queue Wipe", DialogResult = DialogResult.OK, Size = new Size(106, 32), Tag = "DangerButton" };
            btnPanel.Controls.Add(btnCancel);
            btnPanel.Controls.Add(btnOk);
            root.Controls.Add(btnPanel);

            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false
            };
            root.Controls.Add(flow);

            flow.Controls.Add(new Label
            {
                Text = string.Format("Overwrite all {0} in {1} ({2}):",
                    ByteFormatter.FormatBytes(part.PartitionLength), part.ShortDeviceName,
                    string.IsNullOrEmpty(part.Label) ? "no label" : part.Label),
                AutoSize = true,
                MaximumSize = new Size(430, 0),
                Margin = new Padding(0, 0, 0, 12)
            });

            _rbZero = new RadioButton { Text = "Zero-Fill (0x00) — Fast clean wipe", AutoSize = true, Margin = new Padding(4, 4, 0, 4), Checked = true };
            _rbOnes = new RadioButton { Text = "Ones-Fill (0xFF) — Flash/SSD pattern", AutoSize = true, Margin = new Padding(4, 4, 0, 4) };
            _rbRandom = new RadioButton { Text = "Cryptographic / PRNG Random Data — Secure erase", AutoSize = true, Margin = new Padding(4, 4, 0, 4) };
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
            Size = new Size(940, 640);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9f);

            var topBar = new Panel { Dock = DockStyle.Top, Height = 88, Padding = new Padding(10, 8, 10, 6), Tag = "ElevatedPanel" };
            Controls.Add(topBar);

            var row2 = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 36,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoScroll = false
            };
            topBar.Controls.Add(row2);

            var row1 = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 34,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false
            };
            topBar.Controls.Add(row1);

            row1.Controls.Add(new Label { Text = "LBA Sector:", AutoSize = true, Margin = new Padding(0, 6, 8, 0) });
            long maxLba = Math.Max(0, disk.TotalSizeBytes / Math.Max(512, disk.LogicalSectorSize) - 1);
            _numLba = new NumericUpDown
            {
                Width = 150,
                Minimum = 0,
                Maximum = maxLba,
                Value = Math.Min(maxLba, Math.Max(0, initialLba)),
                ThousandsSeparator = true,
                Margin = new Padding(0, 2, 8, 0)
            };
            row1.Controls.Add(_numLba);

            var btnGo = new Button { Text = "Read Sector", AutoSize = true, MinimumSize = new Size(100, 26), Padding = new Padding(8, 2, 8, 2), Margin = new Padding(0, 1, 0, 0), Tag = "PrimaryButton" };
            btnGo.Click += (s, e) => LoadSector((long)_numLba.Value);
            row1.Controls.Add(btnGo);

            AddQuickBtn(row2, "LBA 0 (MBR)", 0);
            AddQuickBtn(row2, "LBA 1 (GPT Hdr)", 1);
            AddQuickBtn(row2, "LBA 2 (GPT Entries)", 2);
            foreach (var p in disk.Partitions.Where(x => !x.IsUnallocated).Take(4))
            {
                AddQuickBtn(row2, string.Format("Part #{0} Boot", p.PartitionNumber), p.StartLba);
            }

            var split = new SplitContainer
            {
                Size = new Size(760, 500),
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                Panel1MinSize = 25,
                Panel2MinSize = 25
            };
            IntegratedFileSystemExplorerControl.SetSplitterDistanceSafe(split, 300);
            Controls.Add(split);
            split.BringToFront();
            Load += (s, e) => IntegratedFileSystemExplorerControl.SetSplitterDistanceSafe(split, 300);

            _txtHex = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
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
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Consolas", 9f)
            };
            split.Panel2.Controls.Add(_txtDecode);

            LoadSector((long)_numLba.Value);
            ThemeManager.ApplyTheme(this);
        }

        private void AddQuickBtn(Control parent, string text, long lba)
        {
            var btn = new Button { Text = text, AutoSize = true, MinimumSize = new Size(92, 26), Padding = new Padding(8, 2, 8, 2), Margin = new Padding(0, 4, 6, 0) };
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
            Size = new Size(820, 500);
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

            var txt = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                Font = new Font("Consolas", 9.5f),
                Text = sb.ToString()
            };
            Controls.Add(txt);
            ThemeManager.ApplyTheme(this);
        }
    }

    /// <summary>
    /// Modal dialog to completely wipe, reinitialize (GPT or MBR), and format an entire non-OS drive
    /// with a single partition of any supported Windows, Linux, macOS, or developer filesystem.
    /// </summary>
    public class FormatEntireDriveDialog : Form
    {
        private readonly ComboBox _cmbTableStyle;
        private readonly ComboBox _cmbFileSystem;
        private readonly TextBox _txtLabel;
        private readonly CheckBox _chkWipeBootSectors;

        public PartitionTableStyle ResultTableStyle { get; private set; }
        public FileSystemType ResultFileSystem { get; private set; }
        public string ResultLabel { get; private set; }
        public bool ResultWipeBootSectors { get; private set; }

        public FormatEntireDriveDialog(DiskInfo disk)
        {
            Text = "Format Entire Drive — " + disk.DisplayName;
            Size = new Size(540, 390);
            MinimumSize = new Size(500, 360);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9f);

            var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16) };
            Controls.Add(root);

            var btnPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 44,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(0, 6, 0, 0)
            };
            var btnCancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(96, 32), Margin = new Padding(8, 0, 0, 0) };
            var btnOk = new Button { Text = "Queue Full Drive Format", DialogResult = DialogResult.OK, Size = new Size(168, 32), Tag = "DangerButton" };
            btnPanel.Controls.Add(btnCancel);
            btnPanel.Controls.Add(btnOk);
            root.Controls.Add(btnPanel);

            var warnPanel = new Panel { Dock = DockStyle.Top, Height = 64, Padding = new Padding(10, 8, 10, 8), Tag = "ElevatedPanel" };
            var lblWarn = new Label
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 8.8f, FontStyle.Bold),
                ForeColor = ThemeManager.AccentRed,
                Tag = "KeepForeColor",
                Text = string.Format(
                    "TARGET: {0}\nCapacity: {1}  |  Bus: {2}  |  Current Partitions: {3}\nALL partitions and boot sectors on this entire drive will be erased and replaced.",
                    disk.DisplayName,
                    ByteFormatter.FormatBytes(disk.TotalSizeBytes),
                    disk.BusTypeName,
                    disk.Partitions.Count(p => !p.IsUnallocated))
            };
            warnPanel.Controls.Add(lblWarn);

            var tbl = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 4,
                Padding = new Padding(0, 12, 0, 0)
            };
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            tbl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tbl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tbl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tbl.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            tbl.Controls.Add(new Label { Text = "Partition Table:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 9, 12, 8) }, 0, 0);
            _cmbTableStyle = new ComboBox { Anchor = AnchorStyles.Left | AnchorStyles.Right, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 6, 0, 8) };
            _cmbTableStyle.Items.Add("GPT (UEFI Modern — Recommended)");
            _cmbTableStyle.Items.Add("MBR (Legacy BIOS / Maximum Compatibility)");
            _cmbTableStyle.SelectedIndex = 0;
            tbl.Controls.Add(_cmbTableStyle, 1, 0);

            tbl.Controls.Add(new Label { Text = "File System:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 9, 12, 8) }, 0, 1);
            _cmbFileSystem = new ComboBox { Anchor = AnchorStyles.Left | AnchorStyles.Right, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 6, 0, 8) };
            _cmbFileSystem.Items.AddRange(new object[]
            {
                FileSystemType.exFAT, FileSystemType.NTFS, FileSystemType.FAT32, FileSystemType.FAT16, FileSystemType.ReFS,
                FileSystemType.Ext4, FileSystemType.Ext3, FileSystemType.Ext2, FileSystemType.Btrfs, FileSystemType.XFS,
                FileSystemType.F2FS, FileSystemType.ZFS, FileSystemType.SquashFS, FileSystemType.ISO9660, FileSystemType.UDF,
                FileSystemType.APFS, FileSystemType.HFSPlus, FileSystemType.HFS, FileSystemType.LinuxSwap
            });
            _cmbFileSystem.SelectedItem = (disk.TotalSizeBytes > 32L * 1073741824L) ? FileSystemType.exFAT : FileSystemType.FAT32;
            tbl.Controls.Add(_cmbFileSystem, 1, 1);

            tbl.Controls.Add(new Label { Text = "Volume Label:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 9, 12, 8) }, 0, 2);
            _txtLabel = new TextBox { Anchor = AnchorStyles.Left | AnchorStyles.Right, Text = "USB_DRIVE", Margin = new Padding(0, 6, 0, 8) };
            tbl.Controls.Add(_txtLabel, 1, 2);

            _chkWipeBootSectors = new CheckBox
            {
                Text = "Wipe active boot sectors & hybrid ISO headers (LBA 0..255 + backup GPT)",
                AutoSize = true,
                Checked = true,
                Margin = new Padding(0, 10, 0, 6)
            };
            tbl.Controls.Add(_chkWipeBootSectors, 0, 3);
            tbl.SetColumnSpan(_chkWipeBootSectors, 2);

            root.Controls.Add(tbl);
            root.Controls.Add(warnPanel);

            btnOk.Click += (s, e) =>
            {
                ResultTableStyle = _cmbTableStyle.SelectedIndex == 1 ? PartitionTableStyle.MBR : PartitionTableStyle.GPT;
                ResultFileSystem = _cmbFileSystem.SelectedItem is FileSystemType ? (FileSystemType)_cmbFileSystem.SelectedItem : FileSystemType.exFAT;
                ResultLabel = _txtLabel.Text.Trim();
                ResultWipeBootSectors = _chkWipeBootSectors.Checked;
            };

            AcceptButton = btnOk;
            CancelButton = btnCancel;
            ThemeManager.ApplyTheme(this);
        }
    }
}
