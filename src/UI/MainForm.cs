using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using WinParted.Core;
using WinParted.Interop;
using WinParted.Models;
using WinParted.UI.Dialogs;

namespace WinParted.UI
{
    /// <summary>
    /// Primary WinParted desktop window: hosts the drive selector, graphical <see cref="PartitionRibbonControl"/>,
    /// partition detail grid, queued operation list, and collapsible M3 Integrated File Explorer.
    /// </summary>
    public class MainForm : Form
    {
        private readonly OperationQueueManager _queueManager = new OperationQueueManager();
        private readonly List<DiskInfo> _mountedVirtualDisks = new List<DiskInfo>();
        private readonly Dictionary<int, long> _selectedOffsetByDisk = new Dictionary<int, long>();
        private DiskInfo _currentPreviewDisk;
        private PartitionEntry _clipboardPartition;
        private int _clipboardSourceDiskNumber = -1;
        private string _clipboardSourceDiskPath = string.Empty;
        private int _nextVirtualDiskNumber = 91;
        private bool _suppressUiEvents;
        private bool _isSyncingToolbarLayout;
        private bool _toolbarLayoutQueued;

        private Panel _toolbarBar;
        private ToolStrip _mainToolStrip;
        private Panel _driveRightPanel;
        private Label _lblDrive;
        private ComboBox _cmbDrives;
        private Label _lblArchBadge;
        private Button _btnElevate;
        private ToolStripMenuItem _miDarkMode;
        private PartitionRibbonControl _ribbon;
        private SplitContainer _mainSplit;
        private Splitter _deviceSplitter;
        private Panel _deviceInfoPanel;
        private Label _lblDeviceInfo;
        private DataGridView _gridPartitions;
        private FlowLayoutPanel _opsQuickFlow;
        private ListView _lvOperations;
        private Label _lblStatus;
        private ContextMenuStrip _partContextMenu;

        private Button _btnOpsApplyQuick;
        private Button _btnOpsUndoQuick;
        private Button _btnOpsClearQuick;

        private ToolStripButton _tbNew, _tbDelete, _tbResize, _tbCopy, _tbPaste, _tbFlags, _tbWipe, _tbMountRo, _tbBrowseFs, _tbUndo, _tbApply;
        private ToolStripSplitButton _tbFormat;

        public MainForm()
        {
            Text = "WinParted — Disk & Partition Manager";
            Size = new Size(1437, 888);
            MinimumSize = new Size(1094, 676);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);
            BackColor = ThemeManager.BackPrimary;
            DoubleBuffered = true;
            ResizeRedraw = true;

            BuildUi();
            LoadInitialDisks();
        }

        private static void EnableDoubleBuffering(Control control)
        {
            if (control == null) return;
            try
            {
                var dbProp = typeof(Control).GetProperty(
                    "DoubleBuffered",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (dbProp != null) dbProp.SetValue(control, true, null);

                var rrProp = typeof(Control).GetProperty(
                    "ResizeRedraw",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (rrProp != null) rrProp.SetValue(control, true, null);
            }
            catch { }
            control.Resize += (s, e) => { if (!control.IsDisposed) control.Invalidate(true); };
            control.SizeChanged += (s, e) => { if (!control.IsDisposed) control.Invalidate(true); };
        }

        private void BuildUi()
        {
            // 1. GParted-Style MenuStrip
            var menu = new MenuStrip();

            var mApp = new ToolStripMenuItem("GParted");
            mApp.DropDownItems.Add("Refresh Devices (F5)", null, (s, e) => RefreshAllDisks());
            mApp.DropDownItems.Add("Create / Reset Demo Virtual Disk (.img)", null, (s, e) => CreateAndMountDemoDisk());
            mApp.DropDownItems.Add("Open Virtual Disk Image (.img / .vhd)...", null, (s, e) => OpenVirtualDiskDialog());
            mApp.DropDownItems.Add("New Blank Virtual Disk...", null, (s, e) => CreateBlankVirtualDiskDialog());
            mApp.DropDownItems.Add(new ToolStripSeparator());
            mApp.DropDownItems.Add("Exit", null, (s, e) => Close());

            var mEdit = new ToolStripMenuItem("Edit");
            mEdit.DropDownItems.Add("Undo Last Operation", null, (s, e) => UndoOperation());
            mEdit.DropDownItems.Add("Clear All Operations", null, (s, e) => ClearOperations());
            mEdit.DropDownItems.Add(new ToolStripSeparator());
            mEdit.DropDownItems.Add("Apply All Operations", null, (s, e) => ApplyOperations());

            var mView = new ToolStripMenuItem("View");
            var miDeviceInfo = new ToolStripMenuItem("Device Information") { Checked = true, CheckOnClick = true };
            miDeviceInfo.CheckedChanged += (s, e) =>
            {
                _deviceInfoPanel.Visible = miDeviceInfo.Checked;
                if (_deviceSplitter != null) _deviceSplitter.Visible = miDeviceInfo.Checked;
            };
            var miPendingOps = new ToolStripMenuItem("Pending Operations") { Checked = true, CheckOnClick = true };
            miPendingOps.CheckedChanged += (s, e) =>
            {
                if (_mainSplit != null)
                    _mainSplit.Panel2Collapsed = !miPendingOps.Checked;
            };
            var miSmartScale = new ToolStripMenuItem("Smart Partition Scale") { Checked = true, CheckOnClick = true };
            miSmartScale.CheckedChanged += (s, e) => { if (_ribbon != null) _ribbon.SmartScale = miSmartScale.Checked; };
            _miDarkMode = new ToolStripMenuItem("Dark Mode (Easy on Eyes)") { Checked = ThemeManager.IsDarkMode, CheckOnClick = true };
            _miDarkMode.CheckedChanged += (s, e) =>
            {
                if (_miDarkMode.Checked != ThemeManager.IsDarkMode)
                    ThemeManager.ToggleTheme();
            };
            mView.DropDownItems.Add(miDeviceInfo);
            mView.DropDownItems.Add(miPendingOps);
            mView.DropDownItems.Add(miSmartScale);
            mView.DropDownItems.Add(new ToolStripSeparator());
            mView.DropDownItems.Add(_miDarkMode);

            var mDevice = new ToolStripMenuItem("Device");
            mDevice.DropDownItems.Add("Create Partition Table (GPT)...", null, (s, e) => QueueCreatePartitionTable(PartitionTableStyle.GPT));
            mDevice.DropDownItems.Add("Create Partition Table (MBR)...", null, (s, e) => QueueCreatePartitionTable(PartitionTableStyle.MBR));
            mDevice.DropDownItems.Add(new ToolStripSeparator());
            mDevice.DropDownItems.Add("Backup Partition Table (.gptbak)...", null, (s, e) => BackupPartitionTable());
            mDevice.DropDownItems.Add("Restore Partition Table (.gptbak)...", null, (s, e) => RestorePartitionTable());

            var mPart = new ToolStripMenuItem("Partition");
            mPart.DropDownItems.Add("New...", null, (s, e) => QueueNewPartition());
            mPart.DropDownItems.Add("Delete", null, (s, e) => QueueDeletePartition());
            mPart.DropDownItems.Add("Resize / Move...", null, (s, e) => QueueResizeMovePartition());
            mPart.DropDownItems.Add(new ToolStripSeparator());
            mPart.DropDownItems.Add("Copy", null, (s, e) => CopySelectedPartition());
            mPart.DropDownItems.Add("Paste", null, (s, e) => PastePartitionToUnallocated());
            mPart.DropDownItems.Add(new ToolStripSeparator());
            var mPartFormatTo = new ToolStripMenuItem("Format to");
            mPartFormatTo.DropDownItems.AddRange(BuildFormatToMenuItems());
            mPart.DropDownItems.Add(mPartFormatTo);
            mPart.DropDownItems.Add("Label / Name...", null, (s, e) => QueueLabelPartition());
            mPart.DropDownItems.Add("Manage Flags...", null, (s, e) => QueueManageFlags());
            mPart.DropDownItems.Add("Secure Wipe...", null, (s, e) => QueueWipePartition());
            mPart.DropDownItems.Add(new ToolStripSeparator());
            mPart.DropDownItems.Add("Mount / Unmount Read-Only", null, (s, e) => ToggleReadOnlyMount());
            mPart.DropDownItems.Add("Browse Filesystem (File Explorer)...", null, (s, e) => BrowseNonMicrosoftFileSystem());
            mPart.DropDownItems.Add(new ToolStripSeparator());
            mPart.DropDownItems.Add("Information / Hex Sector Inspector...", null, (s, e) => OpenHexInspectorAtSelected());

            var mTools = new ToolStripMenuItem("Tools");
            mTools.DropDownItems.Add("Hex & Sector Inspector...", null, (s, e) => OpenHexInspector());
            mTools.DropDownItems.Add("4K / 1MiB Alignment & Health Audit...", null, (s, e) => OpenAlignmentAudit());
            mTools.DropDownItems.Add("Export Pending Operations as DiskPart Script...", null, (s, e) => ExportDiskPartScript());
            mTools.DropDownItems.Add(new ToolStripSeparator());
            mTools.DropDownItems.Add("Run Stress & Data-Safety Test Suite...", null, (s, e) => RunStressTestsDialog());

            var mHelp = new ToolStripMenuItem("Help");
            mHelp.DropDownItems.Add("About WinParted", null, (s, e) =>
                MessageBox.Show(
                    "WinParted — GParted-Style Partition Manager for 32-bit & 64-bit Windows\n\n" +
                    "Runtime: " + Win32DiskInterop.GetArchitectureBadge() + "\n" +
                    "Pointer Width: " + (IntPtr.Size * 8) + "-bit\n\n" +
                    "Supports MBR & GPT partition tables, multi-OS filesystem inspection (NTFS, FAT16/FAT32, exFAT, ReFS, ext2/3/4, Btrfs, XFS, F2FS, ZFS, SquashFS, ISO9660, UDF, Linux Swap, LVM2, BitLocker, APFS, HFS+/HFS), 4K/1MiB alignment auditing, sector hex inspection, and a non-destructive Pending Operations Queue.",
                    "About WinParted", MessageBoxButtons.OK, MessageBoxIcon.Information));

            menu.Items.AddRange(new ToolStripItem[] { mApp, mEdit, mView, mDevice, mPart, mTools, mHelp });
            MainMenuStrip = menu;

            // 2. Unified Toolbar + Right-Aligned Drive Selector
            _toolbarBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = ThemeManager.F55,
                Padding = new Padding(ThemeManager.F8, ThemeManager.F5, ThemeManager.F8, ThemeManager.F5),
                Tag = "ElevatedPanel"
            };
            EnableDoubleBuffering(_toolbarBar);

            _mainToolStrip = new ToolStrip
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                Stretch = true,
                GripStyle = ToolStripGripStyle.Hidden,
                ImageScalingSize = new Size(18, 18),
                Font = new Font("Segoe UI", 8.5f),
                CanOverflow = false,
                LayoutStyle = ToolStripLayoutStyle.Flow,
                Padding = new Padding(ThemeManager.F5, ThemeManager.F8, ThemeManager.F5, ThemeManager.F5)
            };
            var ts = _mainToolStrip;
            var toolbarBar = _toolbarBar;
            _tbNew = new ToolStripButton("New", DrawGPartedToolbarIcon("new"), (s, e) => QueueNewPartition())
            {
                DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
                ToolTipText = "Create a new partition in the selected unallocated space"
            };
            _tbDelete = new ToolStripButton("Delete", DrawGPartedToolbarIcon("delete"), (s, e) => QueueDeletePartition())
            {
                DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
                ToolTipText = "Delete the selected partition"
            };
            _tbResize = new ToolStripButton("Resize/Move", DrawGPartedToolbarIcon("resize"), (s, e) => QueueResizeMovePartition())
            {
                DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
                ToolTipText = "Resize or move the selected partition"
            };
            _tbCopy = new ToolStripButton("Copy", DrawGPartedToolbarIcon("copy"), (s, e) => CopySelectedPartition())
            {
                DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
                ToolTipText = "Copy the selected partition to the clipboard"
            };
            _tbPaste = new ToolStripButton("Paste", DrawGPartedToolbarIcon("paste"), (s, e) => PastePartitionToUnallocated())
            {
                DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
                ToolTipText = "Paste copied partition into unallocated space"
            };
            _tbFormat = new ToolStripSplitButton("Format to", DrawGPartedToolbarIcon("format"))
            {
                DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
                ToolTipText = "Format the selected partition to a chosen filesystem"
            };
            _tbFormat.ButtonClick += (s, e) => QueueFormatPartition();
            _tbFormat.DropDownItems.AddRange(BuildFormatToMenuItems());

            _tbUndo = new ToolStripButton("Undo", DrawGPartedToolbarIcon("undo"), (s, e) => UndoOperation())
            {
                DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
                ToolTipText = "Undo Last Operation"
            };
            _tbApply = new ToolStripButton("Apply", DrawGPartedToolbarIcon("apply"), (s, e) => ApplyOperations())
            {
                DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                ForeColor = ThemeManager.AccentGreen,
                Tag = "ApplyHighlight",
                ToolTipText = "Apply All Operations"
            };
            _tbFlags = new ToolStripButton("Flags", null, (s, e) => QueueManageFlags())
            {
                ToolTipText = "Manage partition flags and GPT/MBR type IDs"
            };
            _tbWipe = new ToolStripButton("Wipe", null, (s, e) => QueueWipePartition())
            {
                ToolTipText = "Securely wipe the selected partition"
            };
            _tbMountRo = new ToolStripButton("Mount RO", null, (s, e) => ToggleReadOnlyMount())
            {
                ToolTipText = "Mount/unmount unsupported filesystem in read-only mode"
            };
            _tbBrowseFs = new ToolStripButton("Explore", DrawGPartedToolbarIcon("explore"), (s, e) => BrowseNonMicrosoftFileSystem())
            {
                DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
                ToolTipText = "Open File Explorer for unsupported filesystem partitions (ext, Btrfs, XFS, APFS, HFS+, ISO9660, etc.)"
            };
            var tbRefresh = new ToolStripButton("Refresh", DrawGPartedToolbarIcon("refresh"), (s, e) => RefreshAllDisks())
            {
                DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
                ToolTipText = "Rescan all physical and virtual drives"
            };

            ts.Items.AddRange(new ToolStripItem[]
            {
                _tbNew, _tbDelete, new ToolStripSeparator(),
                _tbResize, _tbCopy, _tbPaste, new ToolStripSeparator(),
                _tbFormat, _tbFlags, _tbWipe, new ToolStripSeparator(),
                _tbMountRo, _tbBrowseFs, new ToolStripSeparator(),
                _tbUndo, _tbApply, new ToolStripSeparator(), tbRefresh
            });

            foreach (ToolStripItem tsItem in ts.Items)
            {
                tsItem.Padding = new Padding(2, 2, 2, 2);
                tsItem.Margin = new Padding(1, 1, 1, 1);
            }

            _driveRightPanel = new Panel
            {
                Dock = DockStyle.Right,
                Width = Win32DiskInterop.IsAdministrator() ? 288 : ThemeManager.F377,
                Padding = new Padding(ThemeManager.F8, ThemeManager.F3, ThemeManager.F5, ThemeManager.F3),
                Tag = "ElevatedPanel"
            };
            var driveRightPanel = _driveRightPanel;
            EnableDoubleBuffering(driveRightPanel);

            ts.LayoutCompleted += (s, e) => QueueSyncToolbarLayout();
            toolbarBar.Resize += (s, e) => SyncToolbarLayout();

            var driveTable = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = Win32DiskInterop.IsAdministrator() ? 2 : 3,
                RowCount = 1,
                Tag = "ElevatedPanel"
            };
            driveTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            driveTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            if (!Win32DiskInterop.IsAdministrator())
                driveTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            driveTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            _lblDrive = new Label
            {
                Text = "Drive:",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 0, ThemeManager.F8, 0),
                Font = new Font("Segoe UI", 9.0f, FontStyle.Bold)
            };
            var lblDrive = _lblDrive;

            _cmbDrives = new ComboBox
            {
                Dock = DockStyle.Fill,
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Margin = new Padding(0, ThemeManager.F5, ThemeManager.F8, 0),
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            _cmbDrives.SelectedIndexChanged += (s, e) => OnDriveSelected();

            _btnElevate = new Button
            {
                Text = "Run as Admin",
                Size = new Size(110, ThemeManager.F34),
                Anchor = AnchorStyles.Right,
                Visible = !Win32DiskInterop.IsAdministrator(),
                Margin = new Padding(ThemeManager.F5, 0, 0, 0),
                Tag = "ElevateButton"
            };
            _btnElevate.Click += (s, e) =>
            {
                if (Win32DiskInterop.RelaunchAsAdministrator())
                    Close();
            };

            driveTable.Controls.Add(lblDrive, 0, 0);
            driveTable.Controls.Add(_cmbDrives, 1, 0);
            if (!Win32DiskInterop.IsAdministrator())
                driveTable.Controls.Add(_btnElevate, 2, 0);
            driveRightPanel.Controls.Add(driveTable);

            toolbarBar.Controls.Add(ts);
            toolbarBar.Controls.Add(driveRightPanel);

            // 3. Full-Width Visual Golden Ratio Partition Ribbon (Fibonacci 144px height)
            _ribbon = new PartitionRibbonControl { Dock = DockStyle.Top, Height = ThemeManager.F144 };
            _ribbon.SelectedPartitionChanged += (s, part) => OnRibbonSelectionChanged(part);
            _ribbon.PartitionDoubleClicked += (s, part) => OnPartitionDoubleClicked(part);
            _ribbon.PartitionContextMenuRequested += (part, pt) =>
            {
                SyncGridSelectionToPartition(part);
                UpdateActionStates();
                _partContextMenu.Show(_ribbon, pt);
            };

            // 4. Main Golden Ratio Split (61.8% Partition Workspace / 38.2% Pending Operations Queue)
            _mainSplit = new SplitContainer
            {
                Size = new Size(1094, 610),
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                Panel1MinSize = ThemeManager.F233,
                Panel2MinSize = ThemeManager.F144,
                SplitterWidth = ThemeManager.F8
            };
            EnableDoubleBuffering(_mainSplit);
            EnableDoubleBuffering(_mainSplit.Panel1);
            EnableDoubleBuffering(_mainSplit.Panel2);
            ThemeManager.ApplyGoldenSplitter(_mainSplit, true, ThemeManager.F233, ThemeManager.F144);
            _mainSplit.Resize += (s, e) => ThemeManager.ApplyGoldenSplitter(_mainSplit, true, ThemeManager.F233, ThemeManager.F144);

            _deviceInfoPanel = new Panel
            {
                Dock = DockStyle.Left,
                Width = ThemeManager.F233,
                AutoScroll = true,
                Padding = new Padding(ThemeManager.F13),
                BorderStyle = BorderStyle.None,
                Tag = "SurfacePanel"
            };
            EnableDoubleBuffering(_deviceInfoPanel);
            _deviceInfoPanel.Paint += (s, e) =>
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var hdrFont = new Font("Segoe UI", 8.5f, FontStyle.Bold))
                {
                    TextRenderer.DrawText(
                        g,
                        "DEVICE OVERVIEW",
                        hdrFont,
                        new Rectangle(ThemeManager.F13, ThemeManager.F8, Math.Max(20, _deviceInfoPanel.ClientSize.Width - ThemeManager.F21 - ThemeManager.F5), ThemeManager.F21 + ThemeManager.F3),
                        ThemeManager.AccentGold,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                }
                using (var rulePen = new Pen(Color.FromArgb(140, ThemeManager.AccentGold), 1f))
                    g.DrawLine(rulePen, ThemeManager.F13, ThemeManager.F34 + ThemeManager.F3, Math.Max(ThemeManager.F13, _deviceInfoPanel.ClientSize.Width - ThemeManager.F13), ThemeManager.F34 + ThemeManager.F3);
            };
            _lblDeviceInfo = new Label
            {
                Location = new Point(ThemeManager.F13, ThemeManager.F34 + ThemeManager.F8),
                AutoSize = true,
                MaximumSize = new Size(ThemeManager.F233 - ThemeManager.F21 - ThemeManager.F5, 0),
                Font = new Font("Segoe UI", 8.4f)
            };
            _deviceInfoPanel.Controls.Add(_lblDeviceInfo);
            _deviceInfoPanel.ClientSizeChanged += (s, e) =>
            {
                if (_deviceInfoPanel != null && _lblDeviceInfo != null)
                {
                    _lblDeviceInfo.MaximumSize = new Size(Math.Max(110, _deviceInfoPanel.ClientSize.Width - ThemeManager.F21 - ThemeManager.F5), 0);
                    _deviceInfoPanel.Invalidate();
                }
            };

            _deviceSplitter = new Splitter
            {
                Dock = DockStyle.Left,
                Width = 6,
                MinSize = 150,
                MinExtra = 320,
                BackColor = ThemeManager.BorderColor,
                Cursor = Cursors.VSplit
            };
            EnableDoubleBuffering(_deviceSplitter);
            _deviceSplitter.Paint += (s, e) =>
            {
                int midX = _deviceSplitter.Width / 2;
                int midY = _deviceSplitter.Height / 2;
                using (var bgBr = new SolidBrush(ThemeManager.BackPrimary))
                    e.Graphics.FillRectangle(bgBr, _deviceSplitter.ClientRectangle);
                using (var linePen = new Pen(ThemeManager.BorderColor, 1f))
                    e.Graphics.DrawLine(linePen, midX, 0, midX, _deviceSplitter.Height);
                using (var gripPen = new Pen(ThemeManager.TextSecondary, 1.5f))
                {
                    if (midY > 24)
                        e.Graphics.DrawLine(gripPen, midX, midY - 14, midX, midY + 14);
                }
            };

            _gridPartitions = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                RowHeadersVisible = false,
                BorderStyle = BorderStyle.None,
                ShowCellToolTips = true,
                Font = new Font("Segoe UI", 8.8f),
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                ColumnHeadersHeight = ThemeManager.F34
            };
            _gridPartitions.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
            _gridPartitions.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 8.6f, FontStyle.Bold);
            _gridPartitions.ColumnHeadersDefaultCellStyle.Padding = new Padding(ThemeManager.F8, ThemeManager.F5, ThemeManager.F8, ThemeManager.F5);
            _gridPartitions.RowTemplate.Height = ThemeManager.F34;
            _gridPartitions.DefaultCellStyle.Padding = new Padding(ThemeManager.F8, ThemeManager.F5, ThemeManager.F8, ThemeManager.F5);
            _gridPartitions.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
            _gridPartitions.Columns.Add("Partition", "Partition");
            _gridPartitions.Columns.Add("Name", "Name / Mount");
            _gridPartitions.Columns.Add("FS", "File System");
            _gridPartitions.Columns.Add("Label", "Label");
            _gridPartitions.Columns.Add("Size", "Size");
            _gridPartitions.Columns.Add("Used", "Used");
            _gridPartitions.Columns.Add("Unused", "Unused");
            _gridPartitions.Columns.Add("Flags", "Flags");
            _gridPartitions.Columns.Add("Align", "Alignment");

            SetGridColumnSizing("Partition", 84, 89f);
            SetGridColumnSizing("Name", 118, 144f);
            SetGridColumnSizing("FS", 102, 89f);
            SetGridColumnSizing("Label", 84, 89f);
            SetGridColumnSizing("Size", 68, 55f);
            SetGridColumnSizing("Used", 68, 55f);
            SetGridColumnSizing("Unused", 68, 55f);
            SetGridColumnSizing("Flags", 110, 89f);
            SetGridColumnSizing("Align", 96, 89f);

            _gridPartitions.SelectionChanged += (s, e) => OnGridSelectionChanged();
            _gridPartitions.CellDoubleClick += OnGridCellDoubleClick;
            _gridPartitions.CellPainting += OnGridCellPainting;
            _gridPartitions.MouseDown += OnGridMouseDown;
            _gridPartitions.CellToolTipTextNeeded += (s, e) =>
            {
                if (e.RowIndex >= 0 && e.RowIndex < _gridPartitions.Rows.Count)
                {
                    var p = _gridPartitions.Rows[e.RowIndex].Tag as PartitionEntry;
                    if (p != null)
                    {
                        e.ToolTipText = p.IsUnallocated
                            ? string.Format("Unallocated Space | Size: {0} | LBA: {1:N0}..{2:N0} (Offset 0x{3:X})",
                                ByteFormatter.FormatBytes(p.PartitionLength), p.StartLba, p.EndLba, p.StartingOffset)
                            : string.Format("{0} ({1}) | Label: {2}\nLBA: {3:N0}..{4:N0} (Offset 0x{5:X})\nFlags: {6}",
                                p.ShortDeviceName, FileSystemColors.GetDisplayName(p.FileSystem),
                                string.IsNullOrEmpty(p.Label) ? "(none)" : p.Label,
                                p.StartLba, p.EndLba, p.StartingOffset,
                                string.IsNullOrEmpty(p.FlagsSummary) ? "(none)" : p.FlagsSummary);
                    }
                }
            };
            EnableDoubleBuffering(_gridPartitions);

            _mainSplit.Panel1.Controls.Add(_gridPartitions);
            _mainSplit.Panel1.Controls.Add(_deviceSplitter);
            _mainSplit.Panel1.Controls.Add(_deviceInfoPanel);

            var opsHeader = new Panel
            {
                Dock = DockStyle.Top,
                Height = 36,
                Padding = new Padding(ThemeManager.F13, 4, ThemeManager.F13, 4),
                Tag = "ElevatedPanel"
            };
            EnableDoubleBuffering(opsHeader);
            opsHeader.Controls.Add(new Label
            {
                Text = "Pending Operations Queue",
                Dock = DockStyle.Left,
                AutoSize = true,
                Padding = new Padding(0, ThemeManager.F5, 0, 0),
                Font = new Font("Segoe UI", 8.8f, FontStyle.Bold)
            });

            _opsQuickFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0),
                Padding = new Padding(0, 1, 0, 1),
                Tag = "ElevatedPanel"
            };
            _btnOpsApplyQuick = new Button { Text = "Apply", Size = new Size(ThemeManager.F89, 26), Margin = new Padding(0, 0, ThemeManager.F5, 0), Tag = "PrimaryButton" };
            _btnOpsApplyQuick.Click += (s, e) => ApplyOperations();
            _btnOpsUndoQuick = new Button { Text = "Undo", Size = new Size(ThemeManager.F55, 26), Margin = new Padding(0, 0, ThemeManager.F5, 0), Tag = "TonalButton" };
            _btnOpsUndoQuick.Click += (s, e) => UndoOperation();
            _btnOpsClearQuick = new Button { Text = "Clear", Size = new Size(ThemeManager.F55, 26), Margin = new Padding(0) };
            _btnOpsClearQuick.Click += (s, e) => ClearOperations();
            _opsQuickFlow.Controls.Add(_btnOpsApplyQuick);
            _opsQuickFlow.Controls.Add(_btnOpsUndoQuick);
            _opsQuickFlow.Controls.Add(_btnOpsClearQuick);
            opsHeader.Controls.Add(_opsQuickFlow);
            opsHeader.Resize += (s, e) => SyncOpsQuickFlowWidth();

            _lvOperations = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = false,
                BorderStyle = BorderStyle.None
            };
            _lvOperations.Columns.Add("#", ThemeManager.F55);
            _lvOperations.Columns.Add("Disk", ThemeManager.F144);
            _lvOperations.Columns.Add("Operation Summary", 340);
            _lvOperations.Columns.Add("Details", 550);
            _lvOperations.Columns.Add("Risk", ThemeManager.F89);
            _lvOperations.Resize += (s, e) => AutoSizeOperationsColumns();
            _lvOperations.DoubleClick += (s, e) =>
            {
                if (_lvOperations.SelectedItems.Count > 0 && _lvOperations.SelectedItems[0].Tag is PendingOperation)
                {
                    var op = (PendingOperation)_lvOperations.SelectedItems[0].Tag;
                    SelectDiskByNumber(op.TargetDiskNumber);
                }
            };
            EnableDoubleBuffering(_lvOperations);

            _mainSplit.Panel2.Controls.Add(_lvOperations);
            _mainSplit.Panel2.Controls.Add(opsHeader);

            // 5. Status Bar
            var statusPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = ThemeManager.F34,
                Padding = new Padding(ThemeManager.F13, ThemeManager.F5, ThemeManager.F13, ThemeManager.F5),
                Tag = "StatusBar"
            };
            EnableDoubleBuffering(statusPanel);
            _lblStatus = new Label { Dock = DockStyle.Fill, Text = "0 operations pending", Font = new Font("Segoe UI", 8.8f), TextAlign = ContentAlignment.MiddleLeft };
            _lblArchBadge = new Label
            {
                Text = Win32DiskInterop.GetArchitectureBadge(),
                Dock = DockStyle.Right,
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = Win32DiskInterop.IsAdministrator() ? ThemeManager.AccentGreen : ThemeManager.AccentAmber,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                Tag = "KeepForeColor"
            };
            statusPanel.Controls.Add(_lblStatus);
            statusPanel.Controls.Add(_lblArchBadge);

            // Context Menu
            _partContextMenu = new ContextMenuStrip();
            _partContextMenu.Items.Add("New...", null, (s, e) => QueueNewPartition());
            _partContextMenu.Items.Add("Resize / Move...", null, (s, e) => QueueResizeMovePartition());
            _partContextMenu.Items.Add("Delete", null, (s, e) => QueueDeletePartition());
            _partContextMenu.Items.Add(new ToolStripSeparator());
            _partContextMenu.Items.Add("Copy", null, (s, e) => CopySelectedPartition());
            _partContextMenu.Items.Add("Paste", null, (s, e) => PastePartitionToUnallocated());
            _partContextMenu.Items.Add(new ToolStripSeparator());
            var ctxFormatTo = new ToolStripMenuItem("Format to");
            ctxFormatTo.DropDownItems.AddRange(BuildFormatToMenuItems());
            _partContextMenu.Items.Add(ctxFormatTo);
            _partContextMenu.Items.Add("Label / Name...", null, (s, e) => QueueLabelPartition());
            _partContextMenu.Items.Add("Manage Flags...", null, (s, e) => QueueManageFlags());
            _partContextMenu.Items.Add("Secure Wipe...", null, (s, e) => QueueWipePartition());
            _partContextMenu.Items.Add(new ToolStripSeparator());
            _partContextMenu.Items.Add("Mount / Unmount Read-Only", null, (s, e) => ToggleReadOnlyMount());
            _partContextMenu.Items.Add("Browse Filesystem (File Explorer)...", null, (s, e) => BrowseNonMicrosoftFileSystem());
            _partContextMenu.Items.Add(new ToolStripSeparator());
            _partContextMenu.Items.Add("Information / Hex Sector Inspector...", null, (s, e) => OpenHexInspectorAtSelected());

            // Assemble form docking order carefully (last added docks outermost)
            Controls.Add(_mainSplit);
            Controls.Add(_ribbon);
            Controls.Add(toolbarBar);
            Controls.Add(menu);
            Controls.Add(statusPanel);
            EnableDoubleBuffering(this);

            ThemeManager.ThemeChanged += OnThemeChangedHandler;
            Load += (s, e) =>
            {
                ThemeManager.ApplyGoldenSplitter(_mainSplit, true, ThemeManager.F233, ThemeManager.F144);
                SyncOpsQuickFlowWidth();
                SyncToolbarLayout();
                AutoSizeOperationsColumns();
            };
            Shown += (s, e) =>
            {
                SyncOpsQuickFlowWidth();
                SyncToolbarLayout();
            };
            FormClosed += (s, e) => ThemeManager.ThemeChanged -= OnThemeChangedHandler;
            ApplyMainFormTheme();
        }

        private ToolStripItem[] BuildFormatToMenuItems()
        {
            FileSystemType[] formats = new FileSystemType[]
            {
                FileSystemType.NTFS, FileSystemType.FAT32, FileSystemType.FAT16, FileSystemType.exFAT, FileSystemType.ReFS,
                FileSystemType.Ext4, FileSystemType.Ext3, FileSystemType.Ext2, FileSystemType.Btrfs, FileSystemType.XFS,
                FileSystemType.F2FS, FileSystemType.ZFS, FileSystemType.SquashFS, FileSystemType.ISO9660, FileSystemType.UDF,
                FileSystemType.LinuxSwap, FileSystemType.LVM2, FileSystemType.APFS, FileSystemType.HFSPlus, FileSystemType.HFS,
                FileSystemType.RAW
            };

            var items = new List<ToolStripItem>();
            for (int i = 0; i < formats.Length; i++)
            {
                FileSystemType targetFs = formats[i];
                var mi = new ToolStripMenuItem(FileSystemColors.GetDisplayName(targetFs));
                mi.Click += (s, e) => QueueFormatPartitionTo(targetFs);
                items.Add(mi);
                if (targetFs == FileSystemType.ReFS || targetFs == FileSystemType.LinuxSwap)
                    items.Add(new ToolStripSeparator());
            }
            items.Add(new ToolStripSeparator());
            var customItem = new ToolStripMenuItem("Custom Format Dialog (with Label)...");
            customItem.Click += (s, e) => QueueFormatPartition();
            items.Add(customItem);
            return items.ToArray();
        }

        private static Bitmap DrawGPartedToolbarIcon(string kind)
        {
            var bmp = new Bitmap(18, 18);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                if (kind == "new")
                {
                    using (var fill = new SolidBrush(Color.FromArgb(22, 163, 74)))
                    using (var pen = new Pen(Color.White, 2.2f))
                    {
                        g.FillEllipse(fill, 2, 2, 14, 14);
                        g.DrawLine(pen, 9, 5, 9, 13);
                        g.DrawLine(pen, 5, 9, 13, 9);
                    }
                }
                else if (kind == "delete")
                {
                    using (var fill = new SolidBrush(Color.FromArgb(220, 38, 38)))
                    using (var pen = new Pen(Color.White, 2.2f))
                    {
                        g.FillEllipse(fill, 2, 2, 14, 14);
                        g.DrawLine(pen, 6, 6, 12, 12);
                        g.DrawLine(pen, 12, 6, 6, 12);
                    }
                }
                else if (kind == "resize")
                {
                    using (var box = new Pen(Color.FromArgb(37, 99, 235), 1.8f))
                    using (var arrow = new Pen(Color.FromArgb(59, 130, 246), 1.8f))
                    {
                        g.DrawRectangle(box, 3, 4, 12, 10);
                        g.DrawLine(arrow, 5, 9, 13, 9);
                        g.DrawLine(arrow, 7, 7, 5, 9);
                        g.DrawLine(arrow, 7, 11, 5, 9);
                        g.DrawLine(arrow, 11, 7, 13, 9);
                        g.DrawLine(arrow, 11, 11, 13, 9);
                    }
                }
                else if (kind == "copy")
                {
                    using (var fill = new SolidBrush(Color.FromArgb(226, 232, 240)))
                    using (var pen = new Pen(Color.FromArgb(71, 85, 105), 1.5f))
                    {
                        g.FillRectangle(fill, 2, 2, 9, 11);
                        g.DrawRectangle(pen, 2, 2, 9, 11);
                        g.FillRectangle(fill, 6, 5, 9, 11);
                        g.DrawRectangle(pen, 6, 5, 9, 11);
                    }
                }
                else if (kind == "paste")
                {
                    using (var clip = new SolidBrush(Color.FromArgb(148, 163, 184)))
                    using (var page = new SolidBrush(Color.FromArgb(241, 245, 249)))
                    using (var pen = new Pen(Color.FromArgb(71, 85, 105), 1.5f))
                    {
                        g.FillRectangle(clip, 6, 1, 6, 3);
                        g.FillRectangle(page, 3, 3, 12, 13);
                        g.DrawRectangle(pen, 3, 3, 12, 13);
                    }
                }
                else if (kind == "format")
                {
                    using (var fill = new SolidBrush(Color.FromArgb(245, 158, 11)))
                    using (var pen = new Pen(Color.FromArgb(180, 83, 9), 1.5f))
                    {
                        g.FillRectangle(fill, 2, 6, 14, 7);
                        g.DrawRectangle(pen, 2, 6, 14, 7);
                        g.DrawLine(pen, 6, 6, 6, 13);
                        g.DrawLine(pen, 11, 6, 11, 13);
                    }
                }
                else if (kind == "undo")
                {
                    using (var pen = new Pen(Color.FromArgb(217, 119, 6), 2.2f))
                    {
                        g.DrawArc(pen, 4, 5, 10, 9, 210, 180);
                        g.DrawLine(pen, 3, 7, 7, 4);
                        g.DrawLine(pen, 3, 7, 7, 9);
                    }
                }
                else if (kind == "apply")
                {
                    using (var fill = new SolidBrush(Color.FromArgb(22, 163, 74)))
                    using (var pen = new Pen(Color.White, 2.2f))
                    {
                        g.FillEllipse(fill, 2, 2, 14, 14);
                        g.DrawLine(pen, 5, 9, 8, 12);
                        g.DrawLine(pen, 8, 12, 13, 6);
                    }
                }
                else if (kind == "explore")
                {
                    using (var folder = new SolidBrush(Color.FromArgb(245, 158, 11)))
                    using (var edge = new Pen(Color.FromArgb(180, 108, 0), 1.3f))
                    using (var lens = new Pen(Color.FromArgb(37, 99, 235), 1.8f))
                    {
                        g.FillRectangle(folder, 2, 4, 6, 3);
                        g.FillRectangle(folder, 2, 6, 12, 9);
                        g.DrawRectangle(edge, 2, 6, 12, 9);
                        g.DrawEllipse(lens, 8, 8, 6, 6);
                        g.DrawLine(lens, 13, 13, 16, 16);
                    }
                }
                else if (kind == "refresh")
                {
                    using (var pen = new Pen(Color.FromArgb(37, 99, 235), 2.0f))
                    {
                        g.DrawArc(pen, 3, 3, 12, 12, 30, 290);
                        g.DrawLine(pen, 11, 3, 15, 5);
                        g.DrawLine(pen, 15, 5, 11, 7);
                    }
                }
            }
            return bmp;
        }

        private void OnThemeChangedHandler(object sender, EventArgs e)
        {
            ApplyMainFormTheme();
        }

        private void SetGridColumnSizing(string colName, int minWidth, float fillWeight)
        {
            var col = _gridPartitions.Columns[colName];
            if (col != null)
            {
                col.MinimumWidth = minWidth;
                col.FillWeight = fillWeight;
            }
        }

        private void AutoSizeOperationsColumns()
        {
            if (_lvOperations == null || _lvOperations.IsDisposed || _lvOperations.Columns.Count < 5) return;
            int avail = _lvOperations.ClientSize.Width;
            if (avail < 300) return;

            int c0 = ThemeManager.F55;
            int c1 = ThemeManager.F144;
            int c4 = ThemeManager.F89;
            int remaining = Math.Max(ThemeManager.F233, avail - c0 - c1 - c4 - 2);
            int c2 = Math.Max(ThemeManager.F144, ThemeManager.GoldenMinor(remaining)); // 38.2% Operation Summary
            int c3 = Math.Max(ThemeManager.F144, remaining - c2);                    // 61.8% Details

            _lvOperations.Columns[0].Width = c0;
            _lvOperations.Columns[1].Width = c1;
            _lvOperations.Columns[2].Width = c2;
            _lvOperations.Columns[3].Width = c3;
            _lvOperations.Columns[4].Width = c4;
        }

        /// <summary>
        /// Ensures the right-aligned Quick Actions flow panel in the Pending Operations header
        /// is always wide enough to render Apply, Undo, and Clear without horizontal clipping.
        /// </summary>
        private void SyncOpsQuickFlowWidth()
        {
            if (_opsQuickFlow == null || _opsQuickFlow.IsDisposed || _btnOpsApplyQuick == null || _btnOpsUndoQuick == null || _btnOpsClearQuick == null)
                return;
            int neededW = _btnOpsApplyQuick.Width + _btnOpsApplyQuick.Margin.Horizontal
                        + _btnOpsUndoQuick.Width + _btnOpsUndoQuick.Margin.Horizontal
                        + _btnOpsClearQuick.Width + _btnOpsClearQuick.Margin.Horizontal
                        + _opsQuickFlow.Padding.Horizontal + ThemeManager.F5;
            if (_opsQuickFlow.MinimumSize.Width != neededW)
                _opsQuickFlow.MinimumSize = new Size(neededW, 0);
            if (_opsQuickFlow.Width < neededW)
                _opsQuickFlow.Width = neededW;
        }

        /// <summary>
        /// Schedules a single deferred toolbar layout pass via <see cref="Control.BeginInvoke"/> so
        /// <see cref="UpdateDriveSelectorLayout"/> never modifies docked control widths inside an active
        /// WinForms layout transaction.
        /// </summary>
        private void QueueSyncToolbarLayout()
        {
            if (_toolbarLayoutQueued || IsDisposed || !IsHandleCreated)
                return;
            _toolbarLayoutQueued = true;
            BeginInvoke(new Action(delegate
            {
                _toolbarLayoutQueued = false;
                if (!IsDisposed)
                    SyncToolbarLayout();
            }));
        }

        /// <summary>
        /// Sums the total horizontal pixel width required to render all available toolbar items on one row.
        /// </summary>
        private int MeasureToolStripSingleRowItemsWidth()
        {
            if (_mainToolStrip == null || _mainToolStrip.IsDisposed)
                return 0;
            int sum = 0;
            foreach (ToolStripItem tsItem in _mainToolStrip.Items)
            {
                if (!tsItem.Available) continue;
                int w = tsItem.Width > 0 ? tsItem.Width : tsItem.GetPreferredSize(Size.Empty).Width;
                sum += w + tsItem.Margin.Horizontal;
            }
            return sum;
        }

        /// <summary>
        /// Computes the minimum width needed so the toolbar wraps into at most two horizontal rows
        /// instead of shrinking the Drive selector prematurely on narrower windows.
        /// </summary>
        private int MeasureToolStripTwoRowMinWidth()
        {
            if (_mainToolStrip == null || _mainToolStrip.IsDisposed)
                return 240;
            int totalW = 0;
            int maxItemW = 0;
            foreach (ToolStripItem tsItem in _mainToolStrip.Items)
            {
                if (!tsItem.Available) continue;
                int w = (tsItem.Width > 0 ? tsItem.Width : tsItem.GetPreferredSize(Size.Empty).Width) + tsItem.Margin.Horizontal;
                totalW += w;
                if (w > maxItemW) maxItemW = w;
            }
            // Half the total width plus the widest item guarantees greedy row 1 leaves < half for row 2
            return (totalW + 1) / 2 + maxItemW + _mainToolStrip.Padding.Horizontal + ThemeManager.F8;
        }

        /// <summary>
        /// Dynamically sizes the Drive selector container up to the exact length of the longest device
        /// name and capacity (never wider), allowing the action toolbar to compress to two horizontal
        /// rows when the window becomes narrower.
        /// </summary>
        private void UpdateDriveSelectorLayout()
        {
            if (_toolbarBar == null || _toolbarBar.IsDisposed || _driveRightPanel == null || _driveRightPanel.IsDisposed || _cmbDrives == null || _cmbDrives.IsDisposed)
                return;
            if (_toolbarBar.ClientSize.Width < 300)
                return;

            int maxTextW = 140;
            Font driveFont = _cmbDrives.Font ?? Font;
            Size probeBounds = new Size(int.MaxValue, 32);
            TextFormatFlags measureFlags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            for (int i = 0; i < _cmbDrives.Items.Count; i++)
            {
                object item = _cmbDrives.Items[i];
                string text = item != null ? item.ToString() : string.Empty;
                if (!string.IsNullOrEmpty(text))
                {
                    int w = TextRenderer.MeasureText(text, driveFont, probeBounds, measureFlags).Width;
                    if (w > maxTextW) maxTextW = w;
                }
            }
            if (_cmbDrives.Items.Count == 0 && _currentPreviewDisk != null && !string.IsNullOrEmpty(_currentPreviewDisk.DisplayName))
            {
                int w = TextRenderer.MeasureText(_currentPreviewDisk.DisplayName, driveFont, probeBounds, measureFlags).Width;
                if (w > maxTextW) maxTextW = w;
            }

            // Expand up to the exact length of the longest device name + size (plus combo chrome), never wider
            int desiredComboW = maxTextW + 38;
            int desiredDropDownW = Math.Min(1000, Math.Max(desiredComboW, _cmbDrives.Width));
            if (_cmbDrives.DropDownWidth != desiredDropDownW)
                _cmbDrives.DropDownWidth = desiredDropDownW;

            int labelW = (_lblDrive != null ? Math.Max(_lblDrive.Width, _lblDrive.PreferredWidth) + _lblDrive.Margin.Horizontal : 52);
            int elevateW = (_btnElevate != null && _btnElevate.Visible) ? (Math.Max(_btnElevate.Width, _btnElevate.PreferredSize.Width) + _btnElevate.Margin.Horizontal) : 0;
            int chromePad = _driveRightPanel.Padding.Horizontal + _cmbDrives.Margin.Horizontal + 6;
            int desiredPanelW = labelW + desiredComboW + elevateW + chromePad;

            int twoRowToolbarMinW = _toolbarBar.Padding.Horizontal + MeasureToolStripTwoRowMinWidth();
            int availWithTwoRowCompression = _toolbarBar.ClientSize.Width - twoRowToolbarMinW;

            int minPanelW = (_btnElevate != null && _btnElevate.Visible) ? 300 : 210;
            int maxAllowedW = Math.Max(minPanelW, availWithTwoRowCompression);
            int targetPanelW = Math.Max(minPanelW, Math.Min(desiredPanelW, maxAllowedW));

            if (_driveRightPanel.Width != targetPanelW)
            {
                _driveRightPanel.Width = targetPanelW;
                _toolbarBar.PerformLayout();
            }
        }

        /// <summary>
        /// Synchronizes <see cref="_driveRightPanel"/> width and compresses <see cref="_mainToolStrip"/>
        /// between one horizontal row (height 55px) and two horizontal rows (height 74px) as needed.
        /// </summary>
        private void SyncToolbarLayout()
        {
            if (_isSyncingToolbarLayout) return;
            if (_toolbarBar == null || _toolbarBar.IsDisposed || _mainToolStrip == null || _mainToolStrip.IsDisposed || _driveRightPanel == null || _driveRightPanel.IsDisposed)
                return;
            if (_toolbarBar.ClientSize.Width < 300)
                return;

            _isSyncingToolbarLayout = true;
            try
            {
                UpdateDriveSelectorLayout();

                int availW = Math.Max(ThemeManager.F144,
                    _toolbarBar.ClientSize.Width - _toolbarBar.Padding.Horizontal - _driveRightPanel.Width - _mainToolStrip.Padding.Horizontal);
                int rows = 1;
                int curX = 0;
                foreach (ToolStripItem tsItem in _mainToolStrip.Items)
                {
                    if (!tsItem.Available) continue;
                    int w = tsItem.Width > 0 ? tsItem.Width : tsItem.GetPreferredSize(Size.Empty).Width;
                    int itemSpan = w + tsItem.Margin.Horizontal;
                    if (curX > 0 && curX + itemSpan > availW)
                    {
                        rows++;
                        curX = 0;
                    }
                    curX += itemSpan;
                }

                Padding targetTsPad = rows <= 1
                    ? new Padding(ThemeManager.F5, ThemeManager.F8, ThemeManager.F5, ThemeManager.F5)
                    : new Padding(ThemeManager.F5, 3, ThemeManager.F5, 3);
                if (_mainToolStrip.Padding != targetTsPad)
                    _mainToolStrip.Padding = targetTsPad;

                int effectiveRows = Math.Min(2, rows);
                int desiredH = effectiveRows <= 1 ? ThemeManager.F55 : 74;
                if (_toolbarBar.Height != desiredH)
                {
                    _toolbarBar.Height = desiredH;
                    _toolbarBar.PerformLayout();
                    _mainToolStrip.PerformLayout();
                }
            }
            finally
            {
                _isSyncingToolbarLayout = false;
            }
        }

        private void ApplyMainFormTheme()
        {
            ThemeManager.ApplyTheme(this);
            ThemeManager.ApplyContextMenu(_partContextMenu);
            if (_miDarkMode != null && _miDarkMode.Checked != ThemeManager.IsDarkMode)
                _miDarkMode.Checked = ThemeManager.IsDarkMode;
            if (_lblArchBadge != null)
                _lblArchBadge.ForeColor = Win32DiskInterop.IsAdministrator() ? ThemeManager.AccentGreen : ThemeManager.AccentAmber;
            if (_tbApply != null)
                _tbApply.ForeColor = ThemeManager.AccentGreen;

            SyncOpsQuickFlowWidth();
            SyncToolbarLayout();

            if (_gridPartitions != null)
            {
                foreach (DataGridViewRow row in _gridPartitions.Rows)
                {
                    var p = row.Tag as PartitionEntry;
                    if (p != null)
                        row.DefaultCellStyle.ForeColor = p.IsUnallocated ? ThemeManager.TextSecondary : ThemeManager.TextPrimary;
                }
            }
            if (_ribbon != null)
                _ribbon.Invalidate();
        }

        private static string GetWritableDemoDiskPath()
        {
            string[] candidates = new string[]
            {
                Path.Combine(Path.GetTempPath(), "WinParted_sandbox_demo_disk.img"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sandbox_demo_disk.img"),
                Path.Combine(Environment.CurrentDirectory, "sandbox_demo_disk.img")
            };
            foreach (string path in candidates)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                            return path;
                    }
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    string probe = Path.Combine(dir, ".wp_write_probe_" + Guid.NewGuid().ToString("N"));
                    File.WriteAllBytes(probe, new byte[1]);
                    File.Delete(probe);
                    return path;
                }
                catch { }
            }
            return candidates[0];
        }

        private void LoadInitialDisks()
        {
            RefreshAllDisks();
        }

        private void CreateAndMountDemoDisk()
        {
            string demoPath = GetWritableDemoDiskPath();
            ReadOnlyFileSystemMountEngine.PurgeDiskOverlaysAndCache(90);
            var demo = RawDiskEngine.CreateDemoVirtualDisk(demoPath, 90);
            _mountedVirtualDisks.RemoveAll(d => string.Equals(d.BackingFilePath, demoPath, StringComparison.OrdinalIgnoreCase));
            _mountedVirtualDisks.Add(demo);
            _queueManager.ClearAll();
            RefreshAllDisks();
            SelectDiskByNumber(90);
            _lblStatus.Text = "Created fresh 128 MiB multi-OS sandbox virtual disk: " + demoPath;
        }

        private void OpenVirtualDiskDialog()
        {
            using (var ofd = new OpenFileDialog { Filter = "Disk Images (*.img;*.vhd;*.bin)|*.img;*.vhd;*.bin|All files (*.*)|*.*" })
            {
                if (ofd.ShowDialog(this) == DialogResult.OK)
                {
                    var existing = _mountedVirtualDisks.FirstOrDefault(d => string.Equals(d.BackingFilePath, ofd.FileName, StringComparison.OrdinalIgnoreCase));
                    int diskNum = existing != null ? existing.DiskNumber : _nextVirtualDiskNumber++;
                    ReadOnlyFileSystemMountEngine.UnmountDisk(diskNum);
                    var vdisk = RawDiskEngine.LoadVirtualDisk(ofd.FileName, diskNum);
                    _mountedVirtualDisks.RemoveAll(d => string.Equals(d.BackingFilePath, ofd.FileName, StringComparison.OrdinalIgnoreCase));
                    _mountedVirtualDisks.Add(vdisk);
                    RefreshAllDisks();
                    SelectDiskByNumber(vdisk.DiskNumber);
                }
            }
        }

        private void CreateBlankVirtualDiskDialog()
        {
            using (var sfd = new SaveFileDialog { Filter = "Raw Disk Image (*.img)|*.img", FileName = "blank_64mb.img" })
            {
                if (sfd.ShowDialog(this) == DialogResult.OK)
                {
                    var existing = _mountedVirtualDisks.FirstOrDefault(d => string.Equals(d.BackingFilePath, sfd.FileName, StringComparison.OrdinalIgnoreCase));
                    int diskNum = existing != null ? existing.DiskNumber : _nextVirtualDiskNumber++;
                    ReadOnlyFileSystemMountEngine.PurgeDiskOverlaysAndCache(diskNum);
                    var vdisk = RawDiskEngine.CreateBlankVirtualDisk(sfd.FileName, 64L * 1048576L, PartitionTableStyle.GPT);
                    vdisk.DiskNumber = diskNum;
                    _mountedVirtualDisks.RemoveAll(d => string.Equals(d.BackingFilePath, sfd.FileName, StringComparison.OrdinalIgnoreCase));
                    _mountedVirtualDisks.Add(vdisk);
                    RefreshAllDisks();
                    SelectDiskByNumber(vdisk.DiskNumber);
                }
            }
        }

        private void RefreshAllDisks()
        {
            int prevDiskNum = _currentPreviewDisk != null ? _currentPreviewDisk.DiskNumber : -1;
            var physical = Win32DiskInterop.EnumeratePhysicalDisks();
            for (int i = 0; i < _mountedVirtualDisks.Count; i++)
            {
                if (File.Exists(_mountedVirtualDisks[i].BackingFilePath))
                    _mountedVirtualDisks[i] = RawDiskEngine.LoadVirtualDisk(_mountedVirtualDisks[i].BackingFilePath, _mountedVirtualDisks[i].DiskNumber);
            }

            _queueManager.SetBaselineDisks(physical, _mountedVirtualDisks);
            var previewDisks = _queueManager.GetAllPreviewDisks();

            _suppressUiEvents = true;
            try
            {
                _cmbDrives.Items.Clear();
                int maxItemW = _cmbDrives.Width;
                foreach (var d in previewDisks)
                {
                    _cmbDrives.Items.Add(d);
                    int measured = TextRenderer.MeasureText(d.DisplayName, _cmbDrives.Font).Width + 28;
                    if (measured > maxItemW) maxItemW = measured;
                }
                _cmbDrives.DropDownWidth = Math.Min(900, Math.Max(_cmbDrives.Width, maxItemW));

                if (_cmbDrives.Items.Count > 0)
                {
                    int idx = previewDisks.FindIndex(d => d.DiskNumber == prevDiskNum);
                    _cmbDrives.SelectedIndex = idx >= 0 ? idx : 0;
                }
            }
            finally
            {
                _suppressUiEvents = false;
            }

            OnDriveSelected();
            RefreshOperationsList();
            SyncToolbarLayout();
        }

        private void SelectDiskByNumber(int diskNum)
        {
            for (int i = 0; i < _cmbDrives.Items.Count; i++)
            {
                var d = _cmbDrives.Items[i] as DiskInfo;
                if (d != null && d.DiskNumber == diskNum)
                {
                    if (_cmbDrives.SelectedIndex != i)
                        _cmbDrives.SelectedIndex = i;
                    else
                        OnDriveSelected();
                    return;
                }
            }
        }

        private void OnDriveSelected()
        {
            if (_suppressUiEvents) return;
            var sel = _cmbDrives.SelectedItem as DiskInfo;
            if (sel != null)
            {
                _currentPreviewDisk = _queueManager.GetPreviewDisk(sel.DiskNumber) ?? sel;
                RenderCurrentDisk();
            }
        }

        private void RenderCurrentDisk()
        {
            if (_currentPreviewDisk == null) return;

            var layout = _currentPreviewDisk.GetFullLayoutWithUnallocated();

            // Determine previously remembered partition offset for this disk
            PartitionEntry remembered = null;
            long savedOffset;
            if (_selectedOffsetByDisk.TryGetValue(_currentPreviewDisk.DiskNumber, out savedOffset))
            {
                remembered = layout.FirstOrDefault(p => p.StartingOffset == savedOffset);
            }
            if (remembered == null && layout.Count > 0)
            {
                remembered = layout.FirstOrDefault(p => !p.IsUnallocated) ?? layout[0];
            }

            bool isProtectedSystemDisk = _currentPreviewDisk.IsSystemDisk && !_currentPreviewDisk.IsVirtualImage;

            // Populate Device Information panel
            _lblDeviceInfo.Text = string.Format(
                "Model:\r\n  {0}\r\n" +
                "Path:\r\n  {1}\r\n" +
                "Capacity:\r\n  {2}\r\n" +
                "Interface / Bus:\r\n  {3}\r\n" +
                "Partition Table:\r\n  {4}{5}\r\n" +
                "Logical / Physical:\r\n  {6} / {7} bytes\r\n" +
                "Partitions:\r\n  {8} active (max {9})\r\n" +
                "Disk GUID / Sig:\r\n  {10}\r\n" +
                "OS Boot Disk:\r\n  {11}",
                _currentPreviewDisk.Model,
                _currentPreviewDisk.DevicePath,
                ByteFormatter.FormatBytes(_currentPreviewDisk.TotalSizeBytes),
                _currentPreviewDisk.BusTypeName,
                _currentPreviewDisk.PartitionStyle,
                _currentPreviewDisk.PartitionStyle == PartitionTableStyle.GPT ? (_currentPreviewDisk.GptCrcValid ? " (CRC Valid)" : " (CRC Error)") : "",
                _currentPreviewDisk.LogicalSectorSize,
                _currentPreviewDisk.PhysicalSectorSize,
                _currentPreviewDisk.Partitions.Count(p => !p.IsUnallocated),
                _currentPreviewDisk.MaxPartitions,
                _currentPreviewDisk.PartitionStyle == PartitionTableStyle.GPT ? _currentPreviewDisk.GptDiskGuid.ToString() : string.Format("0x{0:X8}", _currentPreviewDisk.MbrSignature),
                isProtectedSystemDisk ? "YES (Windows OS — Protected)" : "No (Fully Editable)"
            );

            _suppressUiEvents = true;
            _gridPartitions.SuspendLayout();
            try
            {
                _ribbon.SetLayout(layout, remembered);

                // Populate Grid cleanly with Tags assigned before firing selection sync
                _gridPartitions.Rows.Clear();
                DataGridViewRow rowToSelect = null;
                foreach (var p in layout)
                {
                    var roMount = (!p.IsUnallocated && _currentPreviewDisk != null)
                        ? ReadOnlyFileSystemMountEngine.GetMount(_currentPreviewDisk.DiskNumber, p.StartingOffset)
                        : null;
                    string nameOrLetter = p.IsUnallocated
                        ? "-"
                        : (roMount != null
                            ? (roMount.ExperimentalWriteEnabled ? "[EXP-RW: " : "[RO: ") + roMount.MountPath + "]"
                            : (string.IsNullOrEmpty(p.DriveLetter) ? p.PartitionName : p.DriveLetter));

                    string flagsText = p.FlagsSummary ?? "";
                    if (!p.IsUnallocated)
                    {
                        bool isBootOrEsp = p.FileSystem == FileSystemType.EfiSystem ||
                            p.MbrBootable ||
                            flagsText.IndexOf("esp", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (p.IsSystemOrBoot && isProtectedSystemDisk)
                            flagsText = string.IsNullOrEmpty(flagsText) ? "[OS Boot]" : (flagsText + " [OS Boot]");
                        else if (isBootOrEsp)
                            flagsText = string.IsNullOrEmpty(flagsText) ? "[ESP/Boot]" : (flagsText + " [ESP/Boot]");
                    }

                    int rowIdx = _gridPartitions.Rows.Add(
                        p.IsUnallocated ? "unallocated" : p.ShortDeviceName,
                        nameOrLetter,
                        FileSystemColors.GetDisplayName(p.FileSystem),
                        p.IsUnallocated ? "" : p.Label,
                        ByteFormatter.FormatBytes(p.PartitionLength),
                        p.UsedBytes.HasValue ? ByteFormatter.FormatBytes(p.UsedBytes.Value) : "-",
                        p.FreeBytes.HasValue ? ByteFormatter.FormatBytes(p.FreeBytes.Value) : "-",
                        flagsText,
                        p.IsUnallocated ? "-" : (p.IsAligned1MiB ? "1 MiB (OK)" : (p.IsAligned4K ? "4K (OK)" : "Misaligned"))
                    );
                    var row = _gridPartitions.Rows[rowIdx];
                    row.Height = 32;
                    row.Tag = p;
                    row.DefaultCellStyle.ForeColor = p.IsUnallocated ? ThemeManager.TextSecondary : ThemeManager.TextPrimary;
                    if (remembered != null && p.StartingOffset == remembered.StartingOffset && p.PartitionLength == remembered.PartitionLength)
                        rowToSelect = row;
                }

                if (rowToSelect != null)
                    rowToSelect.Selected = true;
                else if (_gridPartitions.Rows.Count > 0)
                    _gridPartitions.Rows[0].Selected = true;
            }
            finally
            {
                _gridPartitions.ResumeLayout();
                _suppressUiEvents = false;
            }

            if (remembered != null)
                _selectedOffsetByDisk[_currentPreviewDisk.DiskNumber] = remembered.StartingOffset;

            UpdateActionStates();
            int diskPending = _queueManager.Queue.Count(o => o.TargetDiskNumber == _currentPreviewDisk.DiskNumber);
            _lblStatus.Text = string.Format("{0} operation{1} pending ({2} on {3})",
                _queueManager.PendingCount,
                _queueManager.PendingCount == 1 ? "" : "s",
                diskPending,
                _currentPreviewDisk.DisplayName);
        }

        private void OnGridCellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex >= 0 && e.ColumnIndex == 2)
            {
                var p = _gridPartitions.Rows[e.RowIndex].Tag as PartitionEntry;
                if (p == null) return;
                e.Paint(e.CellBounds, DataGridViewPaintParts.All & ~DataGridViewPaintParts.ContentForeground);
                int swatchSize = 12;
                int swatchY = e.CellBounds.Y + Math.Max(4, (e.CellBounds.Height - swatchSize) / 2);
                Rectangle swatch = new Rectangle(e.CellBounds.X + 6, swatchY, swatchSize, swatchSize);
                using (var br = new SolidBrush(FileSystemColors.GetBorderColor(p.FileSystem)))
                    e.Graphics.FillRectangle(br, swatch);
                using (var pen = new Pen(ThemeManager.BorderColor))
                    e.Graphics.DrawRectangle(pen, swatch);

                bool isSelected = (e.State & DataGridViewElementStates.Selected) != 0;
                Color textColor = isSelected ? e.CellStyle.SelectionForeColor : e.CellStyle.ForeColor;
                Rectangle textRect = new Rectangle(e.CellBounds.X + 22, e.CellBounds.Y, Math.Max(10, e.CellBounds.Width - 24), e.CellBounds.Height);
                TextRenderer.DrawText(e.Graphics, Convert.ToString(e.FormattedValue), e.CellStyle.Font, textRect, textColor,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                e.Handled = true;
            }
        }

        private void OnGridMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                var hit = _gridPartitions.HitTest(e.X, e.Y);
                if (hit.RowIndex >= 0)
                {
                    _gridPartitions.Rows[hit.RowIndex].Selected = true;
                    _partContextMenu.Show(_gridPartitions, e.Location);
                }
            }
        }

        private PartitionEntry GetSelectedPartition()
        {
            if (_gridPartitions.SelectedRows.Count > 0)
            {
                var p = _gridPartitions.SelectedRows[0].Tag as PartitionEntry;
                if (p != null) return p;
            }
            return _ribbon.SelectedPartition;
        }

        private void OnRibbonSelectionChanged(PartitionEntry part)
        {
            if (_suppressUiEvents) return;
            _suppressUiEvents = true;
            try
            {
                SyncGridSelectionToPartition(part);
            }
            finally
            {
                _suppressUiEvents = false;
            }
            if (_currentPreviewDisk != null && part != null)
                _selectedOffsetByDisk[_currentPreviewDisk.DiskNumber] = part.StartingOffset;
            UpdateActionStates();
        }

        private void SyncGridSelectionToPartition(PartitionEntry part)
        {
            if (part == null) return;
            foreach (DataGridViewRow row in _gridPartitions.Rows)
            {
                var rp = row.Tag as PartitionEntry;
                if (rp != null && rp.StartingOffset == part.StartingOffset && rp.PartitionLength == part.PartitionLength)
                {
                    row.Selected = true;
                    break;
                }
            }
        }

        private void OnGridSelectionChanged()
        {
            if (_suppressUiEvents) return;
            if (_gridPartitions.SelectedRows.Count > 0)
            {
                var p = _gridPartitions.SelectedRows[0].Tag as PartitionEntry;
                if (p != null)
                {
                    if (_currentPreviewDisk != null)
                        _selectedOffsetByDisk[_currentPreviewDisk.DiskNumber] = p.StartingOffset;
                    if (!ReferenceEquals(_ribbon.SelectedPartition, p))
                    {
                        _suppressUiEvents = true;
                        try
                        {
                            _ribbon.SelectPartition(p);
                        }
                        finally
                        {
                            _suppressUiEvents = false;
                        }
                    }
                    UpdateActionStates();
                }
            }
        }

        private void RefreshMountBadgesAndActions()
        {
            if (_currentPreviewDisk != null && _gridPartitions != null)
            {
                foreach (DataGridViewRow row in _gridPartitions.Rows)
                {
                    var p = row.Tag as PartitionEntry;
                    if (p == null || p.IsUnallocated) continue;
                    var roMount = ReadOnlyFileSystemMountEngine.GetMount(_currentPreviewDisk.DiskNumber, p.StartingOffset);
                    row.Cells["Name"].Value = roMount != null
                        ? (roMount.ExperimentalWriteEnabled ? "[EXP-RW: " : "[RO: ") + roMount.MountPath + "]"
                        : (string.IsNullOrEmpty(p.DriveLetter) ? p.PartitionName : p.DriveLetter);
                }
            }
            UpdateActionStates();
        }

        private void UpdateActionStates()
        {
            var sel = GetSelectedPartition();
            bool hasSel = sel != null;
            bool isUnalloc = hasSel && sel.IsUnallocated;
            bool isPart = hasSel && !sel.IsUnallocated;
            bool isOsProtectedPart = isPart &&
                _currentPreviewDisk != null &&
                !_currentPreviewDisk.IsVirtualImage &&
                _currentPreviewDisk.IsSystemDisk &&
                sel.IsSystemOrBoot;

            _tbNew.Enabled = isUnalloc;
            _tbDelete.Enabled = isPart && !isOsProtectedPart;
            _tbResize.Enabled = isPart && !isOsProtectedPart;
            _tbCopy.Enabled = isPart;
            _tbPaste.Enabled = isUnalloc && _clipboardPartition != null;
            _tbFormat.Enabled = isPart && !isOsProtectedPart;
            _tbFlags.Enabled = isPart;
            _tbWipe.Enabled = isPart && !isOsProtectedPart;

            bool canRoMount = isPart && ReadOnlyFileSystemMountEngine.IsSupportedNonMicrosoftFs(sel.FileSystem);
            bool isRoMounted = canRoMount && _currentPreviewDisk != null &&
                ReadOnlyFileSystemMountEngine.GetMount(_currentPreviewDisk.DiskNumber, sel.StartingOffset) != null;
            if (_tbMountRo != null)
            {
                _tbMountRo.Enabled = canRoMount;
                _tbMountRo.Text = isRoMounted ? "Unmount" : "Mount RO";
            }
            if (_tbBrowseFs != null)
                _tbBrowseFs.Enabled = canRoMount;

            bool hasPending = _queueManager.PendingCount > 0;
            _tbUndo.Enabled = hasPending;
            _tbApply.Enabled = hasPending;
            if (_btnOpsApplyQuick != null) _btnOpsApplyQuick.Enabled = hasPending;
            if (_btnOpsUndoQuick != null) _btnOpsUndoQuick.Enabled = hasPending;
            if (_btnOpsClearQuick != null) _btnOpsClearQuick.Enabled = hasPending;
        }

        private void OnGridCellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;
            var p = _gridPartitions.Rows[e.RowIndex].Tag as PartitionEntry;
            if (p != null)
                OnPartitionDoubleClicked(p);
        }

        private void OnPartitionDoubleClicked(PartitionEntry part)
        {
            if (part == null) return;
            SyncGridSelectionToPartition(part);
            UpdateActionStates();
            if (part.IsUnallocated)
                QueueNewPartition();
            else if (ReadOnlyFileSystemMountEngine.IsSupportedNonMicrosoftFs(part.FileSystem))
                BrowseNonMicrosoftFileSystem();
            else
                QueueResizeMovePartition();
        }

        /// <summary>
        /// Toggles read-only userspace mounting for the selected foreign/developer partition and opens
        /// the standalone browser dialog when mounting succeeds.
        /// </summary>
        private void ToggleReadOnlyMount()
        {
            var sel = GetSelectedPartition();
            if (_currentPreviewDisk == null || sel == null || sel.IsUnallocated) return;

            var existing = ReadOnlyFileSystemMountEngine.GetMount(_currentPreviewDisk.DiskNumber, sel.StartingOffset);
            if (existing != null)
            {
                ReadOnlyFileSystemMountEngine.Unmount(_currentPreviewDisk.DiskNumber, sel.StartingOffset);
                sel.IsMounted = false;
                RenderCurrentDisk();
                _lblStatus.Text = "Unmounted read-only volume: " + existing.MountPath;
                return;
            }

            try
            {
                var vol = ReadOnlyFileSystemMountEngine.MountReadOnly(_currentPreviewDisk, sel, false);
                RenderCurrentDisk();
                _lblStatus.Text = string.Format("Mounted {0} read-only at {1} ({2})",
                    sel.ShortDeviceName, vol.MountPath, FileSystemColors.GetDisplayName(vol.FileSystem));
                using (var dlg = new FileSystemBrowserDialog(_currentPreviewDisk, sel, vol))
                    dlg.ShowDialog(this);
                RenderCurrentDisk();
            }
            catch (Exception ex)
            {
                HandleReadOnlyMountException(ex, sel, "Read-Only Mount");
            }
        }

        private void BrowseNonMicrosoftFileSystem()
        {
            var sel = GetSelectedPartition();
            if (_currentPreviewDisk == null || sel == null || sel.IsUnallocated) return;

            try
            {
                var vol = ReadOnlyFileSystemMountEngine.MountReadOnly(_currentPreviewDisk, sel, false);
                RenderCurrentDisk();
                using (var dlg = new FileSystemBrowserDialog(_currentPreviewDisk, sel, vol))
                    dlg.ShowDialog(this);
                RenderCurrentDisk();
            }
            catch (Exception ex)
            {
                HandleReadOnlyMountException(ex, sel, "Read-Only Filesystem Viewer");
            }
        }

        private void HandleReadOnlyMountException(Exception ex, PartitionEntry sel, string title)
        {
            var win32 = ex as Win32Exception;
            bool accessDenied = (ex is UnauthorizedAccessException) ||
                                (win32 != null && win32.NativeErrorCode == 5);
            if (_currentPreviewDisk != null && !_currentPreviewDisk.IsVirtualImage &&
                !Win32DiskInterop.IsAdministrator() && (accessDenied || !_currentPreviewDisk.CanRawRead))
            {
                var dr = MessageBox.Show(this,
                    string.Format(
                        "Windows requires Administrator privileges to read raw {0} sectors from a physical drive ({1}).\n\n" +
                        "Details: {2}\n\nRestart WinParted as Administrator now?",
                        FileSystemColors.GetDisplayName(sel.FileSystem),
                        _currentPreviewDisk.DevicePath,
                        ex.Message),
                    title + " — Administrator Required",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);
                if (dr == DialogResult.Yes && Win32DiskInterop.RelaunchAsAdministrator())
                    Close();
                return;
            }

            MessageBox.Show(this, ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void RefreshOperationsList()
        {
            _lvOperations.BeginUpdate();
            try
            {
                _lvOperations.Items.Clear();
                int idx = 1;
                foreach (var op in _queueManager.Queue)
                {
                    var targetDisk = _queueManager.GetBaselineDisk(op.TargetDiskNumber);
                    string diskTag = targetDisk != null && targetDisk.IsVirtualImage
                        ? string.Format("Virtual #{0}", op.TargetDiskNumber)
                        : string.Format("Disk {0}", op.TargetDiskNumber);

                    var item = new ListViewItem(idx++.ToString());
                    item.SubItems.Add(diskTag);
                    item.SubItems.Add(op.Summary);
                    item.SubItems.Add(op.DetailDescription);
                    item.SubItems.Add(op.RiskLevel);
                    item.Tag = op;
                    _lvOperations.Items.Add(item);
                }
            }
            finally
            {
                _lvOperations.EndUpdate();
            }
            AutoSizeOperationsColumns();
            UpdateActionStates();
        }

        /// <summary>
        /// Validates and queues a <see cref="PendingOperation"/> in <see cref="OperationQueueManager"/>,
        /// updating the simulated ribbon and partition table without mutating disk bytes yet.
        /// </summary>
        private bool TryEnqueue(PendingOperation op)
        {
            try
            {
                _queueManager.Enqueue(op);
                OnDriveSelected();
                RefreshOperationsList();
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Operation Rejected (Safety Check)", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }

        private void QueueNewPartition()
        {
            var sel = GetSelectedPartition();
            if (_currentPreviewDisk == null || sel == null || !sel.IsUnallocated) return;

            using (var dlg = new CreateOrResizePartitionDialog(_currentPreviewDisk, sel, sel.StartingOffset, sel.PartitionLength, true))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    TryEnqueue(new PendingOperation
                    {
                        Type = OperationType.CreatePartition,
                        TargetDiskNumber = _currentPreviewDisk.DiskNumber,
                        TargetDiskPath = _currentPreviewDisk.DevicePath,
                        StartOffset = dlg.ResultStartOffset,
                        Length = dlg.ResultLengthBytes,
                        NewFileSystem = dlg.ResultFileSystem,
                        NewLabel = dlg.ResultLabel,
                        NewPartitionName = dlg.ResultPartitionName,
                        NewGptTypeGuid = GptPartitionTypeGuids.ForFileSystem(dlg.ResultFileSystem),
                        NewMbrType = RawDiskEngine.GetDefaultMbrType(dlg.ResultFileSystem),
                        Summary = string.Format("Create {0} partition ({1}) on {2}",
                            FileSystemColors.GetDisplayName(dlg.ResultFileSystem), ByteFormatter.FormatBytes(dlg.ResultLengthBytes),
                            _currentPreviewDisk.IsVirtualImage ? "Virtual Disk" : "Disk " + _currentPreviewDisk.DiskNumber),
                        DetailDescription = string.Format("Start 0x{0:X}, Length {1}, Label '{2}'",
                            dlg.ResultStartOffset, ByteFormatter.FormatBytes(dlg.ResultLengthBytes), dlg.ResultLabel),
                        RiskLevel = _currentPreviewDisk.IsVirtualImage ? "Safe (Img)" : "Medium"
                    });
                }
            }
        }

        private void QueueDeletePartition()
        {
            var sel = GetSelectedPartition();
            if (_currentPreviewDisk == null || sel == null || sel.IsUnallocated) return;
            if (sel.IsSystemOrBoot && _currentPreviewDisk.IsSystemDisk && !_currentPreviewDisk.IsVirtualImage)
            {
                MessageBox.Show(this, "Cannot delete a partition required by the running Windows OS on the boot disk.", "Safety Guardrail", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            TryEnqueue(new PendingOperation
            {
                Type = OperationType.DeletePartition,
                TargetDiskNumber = _currentPreviewDisk.DiskNumber,
                TargetDiskPath = _currentPreviewDisk.DevicePath,
                TargetPartitionNumber = sel.PartitionNumber,
                OriginalStartOffset = sel.StartingOffset,
                OriginalLength = sel.PartitionLength,
                Summary = string.Format("Delete {0} ({1}, {2})", sel.ShortDeviceName, FileSystemColors.GetDisplayName(sel.FileSystem), ByteFormatter.FormatBytes(sel.PartitionLength)),
                DetailDescription = string.Format("Offset 0x{0:X}, Size {1}", sel.StartingOffset, ByteFormatter.FormatBytes(sel.PartitionLength)),
                RiskLevel = _currentPreviewDisk.IsVirtualImage ? "Safe (Img)" : "High"
            });
        }

        private void QueueResizeMovePartition()
        {
            var sel = GetSelectedPartition();
            if (_currentPreviewDisk == null || sel == null || sel.IsUnallocated) return;
            if (sel.IsSystemOrBoot && _currentPreviewDisk.IsSystemDisk && !_currentPreviewDisk.IsVirtualImage)
            {
                MessageBox.Show(this, "Safety Guardrail: Cannot resize or move an active Windows OS boot partition.", "Safety Guardrail", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var layout = _currentPreviewDisk.GetFullLayoutWithUnallocated();
            int idx = layout.FindIndex(p => p.StartingOffset == sel.StartingOffset && p.PartitionLength == sel.PartitionLength);
            long spanStart = sel.StartingOffset;
            long spanEnd = sel.EndOffset;
            if (idx > 0 && layout[idx - 1].IsUnallocated) spanStart = layout[idx - 1].StartingOffset;
            if (idx >= 0 && idx + 1 < layout.Count && layout[idx + 1].IsUnallocated) spanEnd = layout[idx + 1].EndOffset;

            using (var dlg = new CreateOrResizePartitionDialog(_currentPreviewDisk, sel, spanStart, spanEnd - spanStart, false))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    if (dlg.ResultStartOffset == sel.StartingOffset && dlg.ResultLengthBytes == sel.PartitionLength)
                        return;

                    TryEnqueue(new PendingOperation
                    {
                        Type = OperationType.ResizeMovePartition,
                        TargetDiskNumber = _currentPreviewDisk.DiskNumber,
                        TargetDiskPath = _currentPreviewDisk.DevicePath,
                        TargetPartitionNumber = sel.PartitionNumber,
                        OriginalStartOffset = sel.StartingOffset,
                        OriginalLength = sel.PartitionLength,
                        StartOffset = dlg.ResultStartOffset,
                        Length = dlg.ResultLengthBytes,
                        Summary = string.Format("Resize/Move {0} from {1} to {2}", sel.ShortDeviceName, ByteFormatter.FormatBytes(sel.PartitionLength), ByteFormatter.FormatBytes(dlg.ResultLengthBytes)),
                        DetailDescription = string.Format("New Start 0x{0:X}, New Size {1}", dlg.ResultStartOffset, ByteFormatter.FormatBytes(dlg.ResultLengthBytes)),
                        RiskLevel = _currentPreviewDisk.IsVirtualImage ? "Safe (Img)" : "High"
                    });
                }
            }
        }

        private void QueueFormatPartition()
        {
            var sel = GetSelectedPartition();
            if (_currentPreviewDisk == null || sel == null || sel.IsUnallocated) return;
            if (sel.IsSystemOrBoot && _currentPreviewDisk.IsSystemDisk && !_currentPreviewDisk.IsVirtualImage)
            {
                MessageBox.Show(this, "Safety Guardrail: Cannot format an active Windows OS boot partition.", "Safety Guardrail", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using (var dlg = new Form { Text = "Format Partition — " + sel.ShortDeviceName, Size = new Size(448, 277), FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false, Font = new Font("Segoe UI", 9f) })
            {
                var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(ThemeManager.F21) };
                dlg.Controls.Add(root);

                var btnFlow = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = ThemeManager.F55, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, ThemeManager.F8, 0, ThemeManager.F8) };
                var btnCancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(ThemeManager.F89, ThemeManager.F34), Margin = new Padding(ThemeManager.F8, 0, 0, 0) };
                var btnOk = new Button { Text = "Queue Format", DialogResult = DialogResult.OK, Size = new Size(110, ThemeManager.F34), Margin = new Padding(ThemeManager.F8, 0, 0, 0), Tag = "TonalButton" };
                var btnFormatNow = new Button { Text = "Format Now", DialogResult = DialogResult.OK, Size = new Size(110, ThemeManager.F34), Margin = new Padding(0), Tag = "PrimaryButton" };
                bool executeNow = false;
                btnOk.Click += delegate { executeNow = false; };
                btnFormatNow.Click += delegate { executeNow = true; };
                btnFlow.Controls.Add(btnCancel);
                btnFlow.Controls.Add(btnOk);
                btnFlow.Controls.Add(btnFormatNow);

                var tbl = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
                tbl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
                tbl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                tbl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.Controls.Add(tbl);
                root.Controls.Add(btnFlow);

                tbl.Controls.Add(new Label { Text = "File system:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 9, 12, 8) }, 0, 0);
                var cmbFs = new ComboBox { Anchor = AnchorStyles.Left | AnchorStyles.Right, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 6, 0, 8) };
                cmbFs.Items.AddRange(new object[]
                {
                    FileSystemType.NTFS, FileSystemType.FAT32, FileSystemType.FAT16, FileSystemType.exFAT, FileSystemType.ReFS,
                    FileSystemType.Ext4, FileSystemType.Ext3, FileSystemType.Ext2, FileSystemType.Btrfs, FileSystemType.XFS,
                    FileSystemType.F2FS, FileSystemType.ZFS, FileSystemType.SquashFS, FileSystemType.ISO9660, FileSystemType.UDF,
                    FileSystemType.LinuxSwap, FileSystemType.LVM2, FileSystemType.APFS, FileSystemType.HFSPlus, FileSystemType.HFS,
                    FileSystemType.RAW
                });
                cmbFs.SelectedIndex = 0;
                tbl.Controls.Add(cmbFs, 1, 0);

                tbl.Controls.Add(new Label { Text = "Volume label:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 9, 12, 8) }, 0, 1);
                var txtLbl = new TextBox { Anchor = AnchorStyles.Left | AnchorStyles.Right, Text = sel.Label, Margin = new Padding(0, 6, 0, 8) };
                tbl.Controls.Add(txtLbl, 1, 1);

                dlg.AcceptButton = btnOk;
                dlg.CancelButton = btnCancel;
                ThemeManager.ApplyTheme(dlg);
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    var fs = (FileSystemType)cmbFs.SelectedItem;
                    int beforeCount = _queueManager.PendingCount;
                    TryEnqueue(new PendingOperation
                    {
                        Type = OperationType.FormatPartition,
                        TargetDiskNumber = _currentPreviewDisk.DiskNumber,
                        TargetDiskPath = _currentPreviewDisk.DevicePath,
                        TargetPartitionNumber = sel.PartitionNumber,
                        OriginalStartOffset = sel.StartingOffset,
                        OriginalLength = sel.PartitionLength,
                        NewFileSystem = fs,
                        NewLabel = txtLbl.Text.Trim(),
                        NewGptTypeGuid = GptPartitionTypeGuids.ForFileSystem(fs),
                        NewMbrType = RawDiskEngine.GetDefaultMbrType(fs),
                        Summary = string.Format("Format {0} as {1}", sel.ShortDeviceName, FileSystemColors.GetDisplayName(fs)),
                        DetailDescription = string.Format("Label: '{0}'", txtLbl.Text.Trim()),
                        RiskLevel = _currentPreviewDisk.IsVirtualImage ? "Safe (Img)" : "High"
                    });

                    if (executeNow && _queueManager.PendingCount > beforeCount)
                    {
                        string msg = string.Format("Format {0} as {1} immediately?", sel.ShortDeviceName, FileSystemColors.GetDisplayName(fs));
                        if (_queueManager.PendingCount > 1)
                            msg += string.Format("\n\nAll {0} pending operation(s) in the queue will be applied together so offsets remain consistent.", _queueManager.PendingCount);
                        if (MessageBox.Show(this, msg, "Confirm Immediate Format", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                            ApplyOperationsInternal(true);
                    }
                }
            }
        }

        private void QueueFormatPartitionTo(FileSystemType fs)
        {
            var sel = GetSelectedPartition();
            if (_currentPreviewDisk == null || sel == null || sel.IsUnallocated) return;
            if (sel.IsSystemOrBoot && _currentPreviewDisk.IsSystemDisk && !_currentPreviewDisk.IsVirtualImage)
            {
                MessageBox.Show(this, "Safety Guardrail: Cannot format an active Windows OS boot partition.", "Safety Guardrail", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string label = sel.Label ?? string.Empty;
            TryEnqueue(new PendingOperation
            {
                Type = OperationType.FormatPartition,
                TargetDiskNumber = _currentPreviewDisk.DiskNumber,
                TargetDiskPath = _currentPreviewDisk.DevicePath,
                TargetPartitionNumber = sel.PartitionNumber,
                OriginalStartOffset = sel.StartingOffset,
                OriginalLength = sel.PartitionLength,
                NewFileSystem = fs,
                NewLabel = label,
                NewGptTypeGuid = GptPartitionTypeGuids.ForFileSystem(fs),
                NewMbrType = RawDiskEngine.GetDefaultMbrType(fs),
                Summary = string.Format("Format {0} as {1}", sel.ShortDeviceName, FileSystemColors.GetDisplayName(fs)),
                DetailDescription = string.Format("Label: '{0}'", label),
                RiskLevel = _currentPreviewDisk.IsVirtualImage ? "Safe (Img)" : "High"
            });
        }

        private void QueueLabelPartition()
        {
            var sel = GetSelectedPartition();
            if (_currentPreviewDisk == null || sel == null || sel.IsUnallocated) return;

            using (var dlg = new Form { Text = "Label Partition — " + sel.ShortDeviceName, Size = new Size(424, 262), FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false, Font = new Font("Segoe UI", 9f) })
            {
                var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(ThemeManager.F21) };
                dlg.Controls.Add(root);

                var btnFlow = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = ThemeManager.F55, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, ThemeManager.F8, 0, ThemeManager.F8) };
                var btnCancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(ThemeManager.F89, ThemeManager.F34), Margin = new Padding(ThemeManager.F8, 0, 0, 0) };
                var btnOk = new Button { Text = "Apply Label", DialogResult = DialogResult.OK, Size = new Size(110, ThemeManager.F34), Margin = new Padding(0), Tag = "PrimaryButton" };
                btnFlow.Controls.Add(btnCancel);
                btnFlow.Controls.Add(btnOk);

                var tbl = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
                tbl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
                tbl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                tbl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.Controls.Add(tbl);
                root.Controls.Add(btnFlow);

                tbl.Controls.Add(new Label { Text = "Volume Label:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 9, 12, 8) }, 0, 0);
                var txtLbl = new TextBox { Anchor = AnchorStyles.Left | AnchorStyles.Right, Text = sel.Label, Margin = new Padding(0, 6, 0, 8) };
                tbl.Controls.Add(txtLbl, 1, 0);

                tbl.Controls.Add(new Label { Text = "GPT Name:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 9, 12, 8) }, 0, 1);
                var txtName = new TextBox { Anchor = AnchorStyles.Left | AnchorStyles.Right, Text = sel.PartitionName, Enabled = _currentPreviewDisk.PartitionStyle == PartitionTableStyle.GPT, Margin = new Padding(0, 6, 0, 8) };
                tbl.Controls.Add(txtName, 1, 1);

                dlg.AcceptButton = btnOk;
                dlg.CancelButton = btnCancel;
                ThemeManager.ApplyTheme(dlg);
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    TryEnqueue(new PendingOperation
                    {
                        Type = OperationType.ChangeLabel,
                        TargetDiskNumber = _currentPreviewDisk.DiskNumber,
                        TargetDiskPath = _currentPreviewDisk.DevicePath,
                        TargetPartitionNumber = sel.PartitionNumber,
                        OriginalStartOffset = sel.StartingOffset,
                        OriginalLength = sel.PartitionLength,
                        NewLabel = txtLbl.Text.Trim(),
                        NewPartitionName = txtName.Text.Trim(),
                        Summary = string.Format("Set label of {0} to '{1}'", sel.ShortDeviceName, txtLbl.Text.Trim()),
                        DetailDescription = "GPT Name: " + txtName.Text.Trim(),
                        RiskLevel = "Low"
                    });
                }
            }
        }

        private void QueueManageFlags()
        {
            var sel = GetSelectedPartition();
            if (_currentPreviewDisk == null || sel == null || sel.IsUnallocated) return;

            using (var dlg = new ManageFlagsDialog(_currentPreviewDisk, sel))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    TryEnqueue(new PendingOperation
                    {
                        Type = OperationType.ChangeFlags,
                        TargetDiskNumber = _currentPreviewDisk.DiskNumber,
                        TargetDiskPath = _currentPreviewDisk.DevicePath,
                        TargetPartitionNumber = sel.PartitionNumber,
                        OriginalStartOffset = sel.StartingOffset,
                        OriginalLength = sel.PartitionLength,
                        NewGptTypeGuid = dlg.ResultGptTypeGuid,
                        NewGptAttributes = dlg.ResultGptAttributes,
                        NewMbrType = dlg.ResultMbrType,
                        NewMbrBoot = dlg.ResultMbrBoot,
                        Summary = string.Format("Change flags/type on {0}", sel.ShortDeviceName),
                        DetailDescription = Win32DiskInterop.FormatGptFlags(dlg.ResultGptTypeGuid, dlg.ResultGptAttributes),
                        RiskLevel = "Medium"
                    });
                }
            }
        }

        private void QueueWipePartition()
        {
            var sel = GetSelectedPartition();
            if (_currentPreviewDisk == null || sel == null || sel.IsUnallocated) return;
            if (sel.IsSystemOrBoot && _currentPreviewDisk.IsSystemDisk && !_currentPreviewDisk.IsVirtualImage)
            {
                MessageBox.Show(this, "Safety Guardrail: Cannot wipe an active Windows OS boot partition.", "Safety Guardrail", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using (var dlg = new WipePartitionDialog(sel))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    int beforeCount = _queueManager.PendingCount;
                    TryEnqueue(new PendingOperation
                    {
                        Type = OperationType.WipePartition,
                        TargetDiskNumber = _currentPreviewDisk.DiskNumber,
                        TargetDiskPath = _currentPreviewDisk.DevicePath,
                        TargetPartitionNumber = sel.PartitionNumber,
                        OriginalStartOffset = sel.StartingOffset,
                        OriginalLength = sel.PartitionLength,
                        WipePattern = dlg.SelectedPattern,
                        Summary = string.Format("Wipe {0} ({1})", sel.ShortDeviceName, dlg.SelectedPattern),
                        DetailDescription = string.Format("Overwrite {0}", ByteFormatter.FormatBytes(sel.PartitionLength)),
                        RiskLevel = _currentPreviewDisk.IsVirtualImage ? "Safe (Img)" : "Critical"
                    });

                    if (dlg.ExecuteImmediately && _queueManager.PendingCount > beforeCount)
                    {
                        string msg = string.Format("Secure wipe {0} ({1}) immediately? This will permanently erase all data in the partition.", sel.ShortDeviceName, dlg.SelectedPattern);
                        if (_queueManager.PendingCount > 1)
                            msg += string.Format("\n\nAll {0} pending operation(s) in the queue will be applied together so offsets remain consistent.", _queueManager.PendingCount);
                        if (MessageBox.Show(this, msg, "Confirm Immediate Secure Wipe", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                            ApplyOperationsInternal(true);
                    }
                }
            }
        }

        private void CopySelectedPartition()
        {
            var sel = GetSelectedPartition();
            if (_currentPreviewDisk == null || sel == null || sel.IsUnallocated) return;
            _clipboardPartition = sel.Clone();
            _clipboardSourceDiskNumber = _currentPreviewDisk.DiskNumber;
            _clipboardSourceDiskPath = _currentPreviewDisk.DevicePath;
            UpdateActionStates();
            _lblStatus.Text = string.Format("Copied {0} ({1}) from Disk {2} to WinParted clipboard. Select Unallocated space and click Paste.", sel.ShortDeviceName, ByteFormatter.FormatBytes(sel.PartitionLength), _clipboardSourceDiskNumber);
        }

        private void PastePartitionToUnallocated()
        {
            var sel = GetSelectedPartition();
            if (_currentPreviewDisk == null || sel == null || !sel.IsUnallocated || _clipboardPartition == null) return;
            if (sel.PartitionLength < _clipboardPartition.PartitionLength)
            {
                MessageBox.Show(this, string.Format("Selected unallocated space ({0}) is smaller than the copied partition ({1}).",
                    ByteFormatter.FormatBytes(sel.PartitionLength), ByteFormatter.FormatBytes(_clipboardPartition.PartitionLength)),
                    "Insufficient Space", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            long alignedStart = ByteFormatter.AlignUp(sel.StartingOffset, 1048576L);
            if (alignedStart + _clipboardPartition.PartitionLength > sel.EndOffset)
                alignedStart = sel.StartingOffset;

            TryEnqueue(new PendingOperation
            {
                Type = OperationType.CopyPartition,
                TargetDiskNumber = _currentPreviewDisk.DiskNumber,
                TargetDiskPath = _currentPreviewDisk.DevicePath,
                SourceDiskNumber = _clipboardSourceDiskNumber,
                SourceDiskPath = _clipboardSourceDiskPath,
                SourceStartOffset = _clipboardPartition.StartingOffset,
                SourceLength = _clipboardPartition.PartitionLength,
                StartOffset = alignedStart,
                Length = _clipboardPartition.PartitionLength,
                NewFileSystem = _clipboardPartition.FileSystem,
                NewLabel = _clipboardPartition.Label,
                NewPartitionName = _clipboardPartition.PartitionName,
                NewGptTypeGuid = _clipboardPartition.GptTypeGuid,
                Summary = string.Format("Paste/Clone {0} ({1}) into unallocated space", _clipboardPartition.ShortDeviceName, ByteFormatter.FormatBytes(_clipboardPartition.PartitionLength)),
                DetailDescription = string.Format("Source Disk #{0} -> Destination Offset 0x{1:X}", _clipboardSourceDiskNumber, alignedStart),
                RiskLevel = _currentPreviewDisk.IsVirtualImage ? "Safe (Img)" : "Medium"
            });
        }

        private void QueueCreatePartitionTable(PartitionTableStyle style)
        {
            if (_currentPreviewDisk == null) return;
            if (_currentPreviewDisk.IsSystemDisk && !_currentPreviewDisk.IsVirtualImage)
            {
                MessageBox.Show(this, "Safety Guardrail: Cannot erase partition table on the active Windows boot disk.", "Blocked", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (MessageBox.Show(this,
                string.Format("Create a new {0} partition table on {1}?\nThis will queue erasing all existing partition entries on this drive.", style, _currentPreviewDisk.DisplayName),
                "Confirm New Partition Table", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            {
                TryEnqueue(new PendingOperation
                {
                    Type = OperationType.CreatePartitionTable,
                    TargetDiskNumber = _currentPreviewDisk.DiskNumber,
                    TargetDiskPath = _currentPreviewDisk.DevicePath,
                    NewTableStyle = style,
                    Summary = string.Format("Create new {0} partition table on {1}", style, _currentPreviewDisk.IsVirtualImage ? "Virtual Disk" : "Disk " + _currentPreviewDisk.DiskNumber),
                    DetailDescription = "Initializes protective MBR + GPT or classic MBR header",
                    RiskLevel = _currentPreviewDisk.IsVirtualImage ? "Safe (Img)" : "Critical"
                });
            }
        }

        private void BackupPartitionTable()
        {
            if (_currentPreviewDisk == null) return;
            using (var sfd = new SaveFileDialog { Filter = "WinParted Partition Table Backup (*.gptbak)|*.gptbak", FileName = "disk_" + _currentPreviewDisk.DiskNumber + ".gptbak" })
            {
                if (sfd.ShowDialog(this) == DialogResult.OK)
                {
                    try
                    {
                        RawDiskEngine.BackupPartitionTable(_currentPreviewDisk, sfd.FileName);
                        MessageBox.Show(this, "Partition table backed up successfully to:\n" + sfd.FileName, "Backup Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, "Backup failed:\n" + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void RestorePartitionTable()
        {
            if (_currentPreviewDisk == null) return;
            using (var ofd = new OpenFileDialog { Filter = "WinParted Partition Table Backup (*.gptbak)|*.gptbak" })
            {
                if (ofd.ShowDialog(this) == DialogResult.OK)
                {
                    TryEnqueue(new PendingOperation
                    {
                        Type = OperationType.RestorePartitionTable,
                        TargetDiskNumber = _currentPreviewDisk.DiskNumber,
                        TargetDiskPath = _currentPreviewDisk.DevicePath,
                        BackupFilePath = ofd.FileName,
                        Summary = "Restore Partition Table from " + Path.GetFileName(ofd.FileName),
                        DetailDescription = ofd.FileName,
                        RiskLevel = "High"
                    });
                }
            }
        }

        private void UndoOperation()
        {
            if (_queueManager.UndoLast())
            {
                OnDriveSelected();
                RefreshOperationsList();
            }
        }

        private void ClearOperations()
        {
            _queueManager.ClearAll();
            OnDriveSelected();
            RefreshOperationsList();
        }

        /// <summary>
        /// Prompts for confirmation and commits all pending operations to virtual images or live disks.
        /// </summary>
        private void ApplyOperations()
        {
            ApplyOperationsInternal(false);
        }

        private void ApplyOperationsInternal(bool skipConfirmPrompt)
        {
            if (_queueManager.PendingCount == 0) return;

            if (!skipConfirmPrompt && MessageBox.Show(this,
                string.Format("Are you sure you want to apply {0} pending operation(s)?\nEditing partitions can cause data loss if interrupted.", _queueManager.PendingCount),
                "Apply Pending Operations", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            using (var dlg = new ApplyOperationsProgressDialog(_queueManager))
            {
                dlg.ShowDialog(this);
                RefreshAllDisks();

                if (dlg.ExecutionError != null)
                {
                    _lblStatus.Text = "Operation failed: " + dlg.ExecutionError.Message;
                    MessageBox.Show(this, "Operation failed:\n" + dlg.ExecutionError.Message, "Apply Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                else if (dlg.AppliedResults != null && dlg.AppliedResults.Count > 0)
                {
                    _lblStatus.Text = string.Format("Applied and verified {0} operation{1}.",
                        dlg.AppliedResults.Count, dlg.AppliedResults.Count == 1 ? "" : "s");
                }
            }
        }

        private void OpenHexInspector()
        {
            if (_currentPreviewDisk == null) return;
            using (var dlg = new HexSectorInspectorDialog(_currentPreviewDisk, 0))
                dlg.ShowDialog(this);
        }

        private void OpenHexInspectorAtSelected()
        {
            if (_currentPreviewDisk == null) return;
            var sel = GetSelectedPartition();
            long lba = sel != null ? sel.StartLba : 0;
            using (var dlg = new HexSectorInspectorDialog(_currentPreviewDisk, lba))
                dlg.ShowDialog(this);
        }

        private void OpenAlignmentAudit()
        {
            if (_currentPreviewDisk == null) return;
            using (var dlg = new AlignmentAndHealthDialog(_currentPreviewDisk))
                dlg.ShowDialog(this);
        }

        private void ExportDiskPartScript()
        {
            string script = _queueManager.ExportFullDiskPartScript();
            using (var dlg = new Form { Text = "Generated Windows DiskPart Script", Size = new Size(660, 440), StartPosition = FormStartPosition.CenterParent })
            {
                var txt = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Font = new Font("Consolas", 9.5f), Text = script };
                dlg.Controls.Add(txt);
                ThemeManager.ApplyTheme(dlg);
                dlg.ShowDialog(this);
            }
        }

        private void RunStressTestsDialog()
        {
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            int maxW = Math.Max(480, (int)(wa.Width * 0.92));
            int maxH = Math.Max(340, (int)(wa.Height * 0.92));

            using (var dlg = new Form
            {
                Text = "Stress & Data-Safety Test Suite — Running...",
                Size = new Size(Math.Min(840, maxW), Math.Min(560, maxH)),
                MinimumSize = new Size(Math.Min(560, maxW), Math.Min(380, maxH)),
                StartPosition = FormStartPosition.CenterParent,
                Font = new Font("Segoe UI", 9f)
            })
            {
                var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(ThemeManager.F13) };
                dlg.Controls.Add(root);

                var lblHeader = new Label
                {
                    Dock = DockStyle.Top,
                    Height = 28,
                    Text = "Running stress & data-safety test suites in background...",
                    Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                    TextAlign = ContentAlignment.MiddleLeft,
                    UseMnemonic = false
                };

                var txt = new TextBox
                {
                    Dock = DockStyle.Fill,
                    Multiline = true,
                    ReadOnly = true,
                    ScrollBars = ScrollBars.Both,
                    BorderStyle = BorderStyle.FixedSingle,
                    Font = new Font("Consolas", 9f),
                    BackColor = Color.FromArgb(15, 23, 42),
                    ForeColor = Color.FromArgb(203, 213, 225),
                    Tag = "ConsoleColors"
                };

                var bottomFlow = new FlowLayoutPanel
                {
                    Dock = DockStyle.Bottom,
                    Height = ThemeManager.F55,
                    WrapContents = false,
                    FlowDirection = FlowDirection.RightToLeft,
                    Padding = new Padding(0, ThemeManager.F8, 0, ThemeManager.F8)
                };
                var btnClose = new Button
                {
                    Text = "Running...",
                    Enabled = false,
                    DialogResult = DialogResult.OK,
                    Size = new Size(110, ThemeManager.F34),
                    Tag = "PrimaryButton"
                };
                bottomFlow.Controls.Add(btnClose);

                root.Controls.Add(txt);
                root.Controls.Add(lblHeader);
                root.Controls.Add(bottomFlow);
                dlg.AcceptButton = btnClose;
                dlg.CancelButton = btnClose;
                ThemeManager.ApplyTheme(dlg);
                bottomFlow.Height = Math.Max(ThemeManager.F55, btnClose.Height + 18);
                bottomFlow.PerformLayout();

                object sync = new object();
                var pendingLines = new Queue<string>();
                bool isRunning = true;
                bool finished = false;
                bool ok = false;

                var timer = new System.Windows.Forms.Timer { Interval = 75 };
                Action flush = () =>
                {
                    var lines = new List<string>();
                    bool doneNow;
                    bool passedNow;
                    lock (sync)
                    {
                        while (pendingLines.Count > 0)
                            lines.Add(pendingLines.Dequeue());
                        doneNow = finished;
                        passedNow = ok;
                    }

                    if (lines.Count > 0)
                    {
                        var batch = new System.Text.StringBuilder();
                        foreach (string line in lines)
                            batch.AppendLine(line);
                        txt.AppendText(batch.ToString());
                        txt.SelectionStart = txt.TextLength;
                        txt.ScrollToCaret();
                    }

                    if (doneNow)
                    {
                        timer.Stop();
                        isRunning = false;
                        dlg.Text = passedNow
                            ? "Stress & Data-Safety Test Suite — ALL PASSED"
                            : "Stress & Data-Safety Test Suite — FAILED";
                        lblHeader.Text = passedNow
                            ? "All stress & data-safety test suites PASSED."
                            : "Stress & data-safety test suite encountered failures.";
                        lblHeader.ForeColor = passedNow ? ThemeManager.AccentGreen : ThemeManager.AccentRed;
                        lblHeader.Tag = "KeepForeColor";
                        txt.ForeColor = passedNow ? Color.FromArgb(134, 239, 172) : Color.FromArgb(252, 165, 165);
                        btnClose.Text = "Close";
                        btnClose.Enabled = true;
                        btnClose.Focus();
                    }
                };

                timer.Tick += (s, e) => flush();
                dlg.FormClosing += (s, e) =>
                {
                    if (isRunning && e.CloseReason == CloseReason.UserClosing)
                    {
                        e.Cancel = true;
                        System.Media.SystemSounds.Beep.Play();
                        return;
                    }
                    timer.Stop();
                };

                dlg.Shown += (s, e) =>
                {
                    timer.Start();
                    var t = new Thread(() =>
                    {
                        bool result = false;
                        try
                        {
                            result = StressTestRunner.RunAll(line =>
                            {
                                lock (sync) { pendingLines.Enqueue(line); }
                            });
                        }
                        catch (Exception ex)
                        {
                            lock (sync) { pendingLines.Enqueue("EXCEPTION: " + ex); }
                            result = false;
                        }
                        finally
                        {
                            lock (sync)
                            {
                                ok = result;
                                finished = true;
                            }
                            try
                            {
                                if (dlg.IsHandleCreated && !dlg.IsDisposed)
                                    dlg.BeginInvoke(new MethodInvoker(delegate { flush(); }));
                            }
                            catch { }
                        }
                    });
                    t.IsBackground = true;
                    t.Name = "WinParted-StressTestWorker";
                    t.Start();
                };

                try
                {
                    dlg.ShowDialog(this);
                }
                finally
                {
                    timer.Dispose();
                }
            }
        }
    }
}
