using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using WinParted.Core;
using WinParted.Interop;
using WinParted.Models;
using WinParted.UI.Dialogs;

namespace WinParted.UI
{
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

        private ComboBox _cmbDrives;
        private Label _lblArchBadge;
        private Button _btnElevate;
        private CheckBox _chkSmartScale;
        private CheckBox _chkDarkMode;
        private ToolStripMenuItem _miDarkMode;
        private PartitionRibbonControl _ribbon;
        private Panel _deviceInfoPanel;
        private Label _lblDeviceInfo;
        private DataGridView _gridPartitions;
        private ListView _lvOperations;
        private IntegratedFileSystemExplorerControl _integratedExplorer;
        private int _boundExplorerDisk = -1;
        private long _boundExplorerOffset = -1;
        private MountedReadOnlyVolume _boundExplorerVolume;
        private Label _lblStatus;
        private ContextMenuStrip _partContextMenu;

        private ToolStripButton _tbNew, _tbDelete, _tbResize, _tbCopy, _tbPaste, _tbFormat, _tbFlags, _tbWipe, _tbMountRo, _tbBrowseFs, _tbUndo, _tbApply;

        public MainForm()
        {
            Text = "WinParted — Windows Partition Manager (GParted Edition)";
            Size = new Size(1360, 860);
            MinimumSize = new Size(1060, 700);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);
            BackColor = ThemeManager.BackPrimary;

            BuildUi();
            LoadInitialDisks();
        }

        private void BuildUi()
        {
            // 1. MenuStrip
            var menu = new MenuStrip();

            var mApp = new ToolStripMenuItem("WinParted");
            mApp.DropDownItems.Add("Refresh Devices (F5)", null, (s, e) => RefreshAllDisks());
            mApp.DropDownItems.Add("Create / Load Demo Virtual Disk (.img)", null, (s, e) => CreateAndMountDemoDisk());
            mApp.DropDownItems.Add("Open Virtual Disk Image (.img / .vhd)...", null, (s, e) => OpenVirtualDiskDialog());
            mApp.DropDownItems.Add("New Blank Virtual Disk...", null, (s, e) => CreateBlankVirtualDiskDialog());
            mApp.DropDownItems.Add(new ToolStripSeparator());
            mApp.DropDownItems.Add("Exit", null, (s, e) => Close());

            var mEdit = new ToolStripMenuItem("Edit");
            mEdit.DropDownItems.Add("Undo Last Operation", null, (s, e) => UndoOperation());
            mEdit.DropDownItems.Add("Clear All Pending Operations", null, (s, e) => ClearOperations());
            mEdit.DropDownItems.Add(new ToolStripSeparator());
            mEdit.DropDownItems.Add("Apply All Operations", null, (s, e) => ApplyOperations());

            var mView = new ToolStripMenuItem("View");
            var miDeviceInfo = new ToolStripMenuItem("Device Information") { Checked = true, CheckOnClick = true };
            miDeviceInfo.CheckedChanged += (s, e) => _deviceInfoPanel.Visible = miDeviceInfo.Checked;
            var miSmartScale = new ToolStripMenuItem("Smart Partition Scale") { Checked = true, CheckOnClick = true };
            miSmartScale.CheckedChanged += (s, e) => { _chkSmartScale.Checked = miSmartScale.Checked; _ribbon.SmartScale = miSmartScale.Checked; };
            _miDarkMode = new ToolStripMenuItem("Dark Mode (Easy on Eyes)") { Checked = ThemeManager.IsDarkMode, CheckOnClick = true };
            _miDarkMode.CheckedChanged += (s, e) =>
            {
                if (_miDarkMode.Checked != ThemeManager.IsDarkMode)
                    ThemeManager.ToggleTheme();
            };
            mView.DropDownItems.Add(miDeviceInfo);
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
            mPart.DropDownItems.Add("Format To...", null, (s, e) => QueueFormatPartition());
            mPart.DropDownItems.Add("Label / Name...", null, (s, e) => QueueLabelPartition());
            mPart.DropDownItems.Add("Manage Flags...", null, (s, e) => QueueManageFlags());
            mPart.DropDownItems.Add("Secure Wipe...", null, (s, e) => QueueWipePartition());
            mPart.DropDownItems.Add(new ToolStripSeparator());
            mPart.DropDownItems.Add("Mount / Unmount Read-Only", null, (s, e) => ToggleReadOnlyMount());
            mPart.DropDownItems.Add("Browse Filesystem (Read-Only)...", null, (s, e) => BrowseNonMicrosoftFileSystem());

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
                    "Supports MBR & GPT partition tables, multi-OS filesystem inspection (NTFS, FAT32, exFAT, ReFS, ext2/3/4, Btrfs, XFS, Linux Swap, LVM2, BitLocker), 4K/1MiB alignment auditing, sector hex inspection, and a non-destructive Pending Operations Queue.",
                    "About WinParted", MessageBoxButtons.OK, MessageBoxIcon.Information));

            menu.Items.AddRange(new ToolStripItem[] { mApp, mEdit, mView, mDevice, mPart, mTools, mHelp });
            MainMenuStrip = menu;

            // 2. Top Drive Bar
            var topPanel = new Panel { Dock = DockStyle.Top, Height = 48, Tag = "SurfacePanel" };
            var topFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(8, 7, 8, 5)
            };
            topPanel.Controls.Add(topFlow);

            topFlow.Controls.Add(new Label { Text = "Drive:", AutoSize = true, Margin = new Padding(2, 7, 6, 0), Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) });

            _cmbDrives = new ComboBox { Width = 380, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 4, 12, 0) };
            _cmbDrives.SelectedIndexChanged += (s, e) => OnDriveSelected();
            topFlow.Controls.Add(_cmbDrives);

            _chkSmartScale = new CheckBox { Text = "Smart Scale", AutoSize = true, Checked = true, Margin = new Padding(0, 6, 12, 0) };
            _chkSmartScale.CheckedChanged += (s, e) => { if (_ribbon != null) _ribbon.SmartScale = _chkSmartScale.Checked; };
            topFlow.Controls.Add(_chkSmartScale);

            _chkDarkMode = new CheckBox { Text = "Dark Mode", AutoSize = true, Checked = ThemeManager.IsDarkMode, Margin = new Padding(0, 6, 14, 0) };
            _chkDarkMode.CheckedChanged += (s, e) =>
            {
                if (_chkDarkMode.Checked != ThemeManager.IsDarkMode)
                    ThemeManager.ToggleTheme();
            };
            topFlow.Controls.Add(_chkDarkMode);

            _lblArchBadge = new Label
            {
                Text = Win32DiskInterop.GetArchitectureBadge(),
                AutoSize = true,
                Margin = new Padding(0, 7, 14, 0),
                ForeColor = Win32DiskInterop.IsAdministrator() ? ThemeManager.AccentGreen : ThemeManager.AccentAmber,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                Tag = "KeepForeColor"
            };
            topFlow.Controls.Add(_lblArchBadge);

            _btnElevate = new Button
            {
                Text = "Run as Admin",
                AutoSize = true,
                MinimumSize = new Size(108, 28),
                Padding = new Padding(8, 2, 8, 2),
                Margin = new Padding(0, 2, 8, 0),
                Visible = !Win32DiskInterop.IsAdministrator(),
                Tag = "ElevateButton"
            };
            _btnElevate.Click += (s, e) =>
            {
                if (Win32DiskInterop.RelaunchAsAdministrator())
                    Close();
            };
            topFlow.Controls.Add(_btnElevate);

            var btnNewDemo = new Button
            {
                Text = "Reset Demo Disk",
                AutoSize = true,
                MinimumSize = new Size(128, 28),
                Padding = new Padding(8, 2, 8, 2),
                Margin = new Padding(0, 2, 0, 0),
                Tag = "TonalButton"
            };
            btnNewDemo.Click += (s, e) => CreateAndMountDemoDisk();
            topFlow.Controls.Add(btnNewDemo);

            // 3. Action ToolStrip
            var ts = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(6, 4, 6, 4) };
            _tbNew = new ToolStripButton("New", null, (s, e) => QueueNewPartition()) { ToolTipText = "Create new partition in unallocated space" };
            _tbDelete = new ToolStripButton("Delete", null, (s, e) => QueueDeletePartition()) { ToolTipText = "Delete selected partition" };
            _tbResize = new ToolStripButton("Resize/Move", null, (s, e) => QueueResizeMovePartition()) { ToolTipText = "Resize or move selected partition" };
            _tbCopy = new ToolStripButton("Copy", null, (s, e) => CopySelectedPartition());
            _tbPaste = new ToolStripButton("Paste", null, (s, e) => PastePartitionToUnallocated());
            _tbFormat = new ToolStripButton("Format", null, (s, e) => QueueFormatPartition());
            _tbFlags = new ToolStripButton("Flags", null, (s, e) => QueueManageFlags());
            _tbWipe = new ToolStripButton("Wipe", null, (s, e) => QueueWipePartition());
            _tbMountRo = new ToolStripButton("Mount (RO)", null, (s, e) => ToggleReadOnlyMount()) { ToolTipText = "Mount/unmount non-Microsoft partition in read-only mode" };
            _tbBrowseFs = new ToolStripButton("Browse FS", null, (s, e) => BrowseNonMicrosoftFileSystem()) { ToolTipText = "Browse files inside non-Microsoft partition (read-only)" };
            _tbUndo = new ToolStripButton("Undo", null, (s, e) => UndoOperation());
            _tbApply = new ToolStripButton("Apply All", null, (s, e) => ApplyOperations()) { Font = new Font("Segoe UI", 9f, FontStyle.Bold), ForeColor = ThemeManager.AccentGreen, Tag = "ApplyHighlight" };
            var tbHex = new ToolStripButton("Hex/LBA", null, (s, e) => OpenHexInspector());
            var tbAlign = new ToolStripButton("4K Audit", null, (s, e) => OpenAlignmentAudit());
            var tbRefresh = new ToolStripButton("Refresh", null, (s, e) => RefreshAllDisks());

            ts.Items.AddRange(new ToolStripItem[]
            {
                _tbNew, _tbDelete, new ToolStripSeparator(),
                _tbResize, _tbCopy, _tbPaste, new ToolStripSeparator(),
                _tbFormat, _tbFlags, _tbWipe, new ToolStripSeparator(),
                _tbMountRo, _tbBrowseFs, new ToolStripSeparator(),
                tbHex, tbAlign, new ToolStripSeparator(),
                _tbUndo, _tbApply, new ToolStripSeparator(), tbRefresh
            });

            // 4. Visual Ribbon
            _ribbon = new PartitionRibbonControl { Dock = DockStyle.Top, Height = 128 };
            _ribbon.SelectedPartitionChanged += (s, part) => OnRibbonSelectionChanged(part);
            _ribbon.PartitionDoubleClicked += (s, part) => OnPartitionDoubleClicked(part);
            _ribbon.PartitionContextMenuRequested += (part, pt) =>
            {
                SyncGridSelectionToPartition(part);
                UpdateActionStates();
                _partContextMenu.Show(_ribbon, pt);
            };

            // 5. Device Info Side Panel + Main Split
            var centerContainer = new Panel { Dock = DockStyle.Fill };

            _deviceInfoPanel = new Panel { Dock = DockStyle.Left, Width = 248, AutoScroll = true, Padding = new Padding(12), BorderStyle = BorderStyle.None, Tag = "SurfacePanel" };
            _lblDeviceInfo = new Label { Location = new Point(12, 12), AutoSize = true, MaximumSize = new Size(220, 0), Font = new Font("Segoe UI", 8.8f) };
            _deviceInfoPanel.Controls.Add(_lblDeviceInfo);

            var mainSplit = new SplitContainer
            {
                Size = new Size(1060, 620),
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                Panel1MinSize = 25,
                Panel2MinSize = 25,
                SplitterWidth = 5
            };
            IntegratedFileSystemExplorerControl.SetSplitterDistanceSafe(mainSplit, 235);

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
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                ColumnHeadersHeight = 40
            };
            _gridPartitions.RowTemplate.Height = 36;
            _gridPartitions.DefaultCellStyle.Padding = new Padding(6, 4, 6, 4);
            _gridPartitions.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
            _gridPartitions.Columns.Add("Partition", "Partition");
            _gridPartitions.Columns.Add("Name", "Name / Letter");
            _gridPartitions.Columns.Add("FS", "File System");
            _gridPartitions.Columns.Add("Label", "Label");
            _gridPartitions.Columns.Add("Size", "Size");
            _gridPartitions.Columns.Add("Used", "Used");
            _gridPartitions.Columns.Add("Unused", "Unused");
            _gridPartitions.Columns.Add("Flags", "Flags");
            _gridPartitions.Columns.Add("Align", "Alignment");
            _gridPartitions.Columns.Add("LBA", "LBA Range");

            SetGridColumnSizing("Partition", 105, 95);
            SetGridColumnSizing("Name", 125, 115);
            SetGridColumnSizing("FS", 120, 105);
            SetGridColumnSizing("Label", 115, 110);
            SetGridColumnSizing("Size", 85, 80);
            SetGridColumnSizing("Used", 85, 80);
            SetGridColumnSizing("Unused", 85, 80);
            SetGridColumnSizing("Flags", 110, 100);
            SetGridColumnSizing("Align", 95, 90);
            SetGridColumnSizing("LBA", 135, 125);

            _gridPartitions.SelectionChanged += (s, e) => OnGridSelectionChanged();
            _gridPartitions.CellDoubleClick += OnGridCellDoubleClick;
            _gridPartitions.CellPainting += OnGridCellPainting;
            _gridPartitions.MouseDown += OnGridMouseDown;

            mainSplit.Panel1.Controls.Add(_gridPartitions);

            // Bottom Split: Left = Pending Operations Queue | Right = Integrated Foreign Filesystem Explorer
            var bottomSplit = new SplitContainer
            {
                Size = new Size(1060, 360),
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                Panel1MinSize = 25,
                Panel2MinSize = 25,
                SplitterWidth = 5
            };
            IntegratedFileSystemExplorerControl.SetSplitterDistanceSafe(bottomSplit, 275);

            var opsHeader = new Panel { Dock = DockStyle.Top, Height = 32, Padding = new Padding(10, 6, 10, 4), Tag = "ElevatedPanel" };
            opsHeader.Controls.Add(new Label { Text = "Pending Operations Queue (Safe Until 'Apply All')", Dock = DockStyle.Left, AutoSize = true, Font = new Font("Segoe UI", 8.8f, FontStyle.Bold) });

            _lvOperations = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = false,
                BorderStyle = BorderStyle.None
            };
            _lvOperations.Columns.Add("#", 38);
            _lvOperations.Columns.Add("Disk", 90);
            _lvOperations.Columns.Add("Operation Summary", 240);
            _lvOperations.Columns.Add("Details", 260);
            _lvOperations.Columns.Add("Risk", 75);
            _lvOperations.DoubleClick += (s, e) =>
            {
                if (_lvOperations.SelectedItems.Count > 0 && _lvOperations.SelectedItems[0].Tag is PendingOperation)
                {
                    var op = (PendingOperation)_lvOperations.SelectedItems[0].Tag;
                    SelectDiskByNumber(op.TargetDiskNumber);
                }
            };

            bottomSplit.Panel1.Controls.Add(_lvOperations);
            bottomSplit.Panel1.Controls.Add(opsHeader);

            _integratedExplorer = new IntegratedFileSystemExplorerControl { Dock = DockStyle.Fill };
            _integratedExplorer.VolumeStateChanged += (s, e) => RefreshMountBadgesAndActions();
            bottomSplit.Panel2.Controls.Add(_integratedExplorer);

            mainSplit.Panel2.Controls.Add(bottomSplit);

            centerContainer.Controls.Add(mainSplit);
            centerContainer.Controls.Add(_deviceInfoPanel);

            // 6. Status Bar
            var statusPanel = new Panel { Dock = DockStyle.Bottom, Height = 28, Padding = new Padding(8, 4, 8, 4), Tag = "StatusBar" };
            _lblStatus = new Label { Dock = DockStyle.Fill, Text = "Ready", Font = new Font("Segoe UI", 8.8f), TextAlign = ContentAlignment.MiddleLeft };
            statusPanel.Controls.Add(_lblStatus);

            // Context Menu
            _partContextMenu = new ContextMenuStrip();
            _partContextMenu.Items.Add("New Partition...", null, (s, e) => QueueNewPartition());
            _partContextMenu.Items.Add("Resize / Move...", null, (s, e) => QueueResizeMovePartition());
            _partContextMenu.Items.Add("Delete", null, (s, e) => QueueDeletePartition());
            _partContextMenu.Items.Add(new ToolStripSeparator());
            _partContextMenu.Items.Add("Copy", null, (s, e) => CopySelectedPartition());
            _partContextMenu.Items.Add("Paste", null, (s, e) => PastePartitionToUnallocated());
            _partContextMenu.Items.Add(new ToolStripSeparator());
            _partContextMenu.Items.Add("Format To...", null, (s, e) => QueueFormatPartition());
            _partContextMenu.Items.Add("Label / Name...", null, (s, e) => QueueLabelPartition());
            _partContextMenu.Items.Add("Manage Flags...", null, (s, e) => QueueManageFlags());
            _partContextMenu.Items.Add("Secure Wipe...", null, (s, e) => QueueWipePartition());
            _partContextMenu.Items.Add(new ToolStripSeparator());
            _partContextMenu.Items.Add("Mount / Unmount Read-Only", null, (s, e) => ToggleReadOnlyMount());
            _partContextMenu.Items.Add("Browse Filesystem (Read-Only)...", null, (s, e) => BrowseNonMicrosoftFileSystem());
            _partContextMenu.Items.Add(new ToolStripSeparator());
            _partContextMenu.Items.Add("Inspect Boot Sector / LBA in Hex...", null, (s, e) => OpenHexInspectorAtSelected());

            // Assemble form docking order carefully
            Controls.Add(centerContainer);
            Controls.Add(_ribbon);
            Controls.Add(ts);
            Controls.Add(topPanel);
            Controls.Add(menu);
            Controls.Add(statusPanel);

            ThemeManager.ThemeChanged += OnThemeChangedHandler;
            Load += (s, e) =>
            {
                IntegratedFileSystemExplorerControl.SetSplitterDistanceSafe(mainSplit, 235);
                IntegratedFileSystemExplorerControl.SetSplitterDistanceSafe(bottomSplit, 275);
            };
            FormClosed += (s, e) => ThemeManager.ThemeChanged -= OnThemeChangedHandler;
            ApplyMainFormTheme();
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

        private void ApplyMainFormTheme()
        {
            ThemeManager.ApplyTheme(this);
            ThemeManager.ApplyContextMenu(_partContextMenu);
            if (_miDarkMode != null && _miDarkMode.Checked != ThemeManager.IsDarkMode)
                _miDarkMode.Checked = ThemeManager.IsDarkMode;
            if (_chkDarkMode != null && _chkDarkMode.Checked != ThemeManager.IsDarkMode)
                _chkDarkMode.Checked = ThemeManager.IsDarkMode;
            if (_lblArchBadge != null)
                _lblArchBadge.ForeColor = Win32DiskInterop.IsAdministrator() ? ThemeManager.AccentGreen : ThemeManager.AccentAmber;
            if (_tbApply != null)
                _tbApply.ForeColor = ThemeManager.AccentGreen;

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

        private void LoadInitialDisks()
        {
            string demoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sandbox_demo_disk.img");
            if (!File.Exists(demoPath))
                _mountedVirtualDisks.Add(RawDiskEngine.CreateDemoVirtualDisk(demoPath, 90));
            else
                _mountedVirtualDisks.Add(RawDiskEngine.LoadVirtualDisk(demoPath, 90));

            RefreshAllDisks();
        }

        private void CreateAndMountDemoDisk()
        {
            string demoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sandbox_demo_disk.img");
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
                foreach (var d in previewDisks)
                    _cmbDrives.Items.Add(d);

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

            // Populate Device Information panel
            _lblDeviceInfo.Text = string.Format(
                "DEVICE INFORMATION\r\n" +
                "──────────────────────\r\n" +
                "Model:\r\n  {0}\r\n\r\n" +
                "Path:\r\n  {1}\r\n\r\n" +
                "Capacity:\r\n  {2}\r\n\r\n" +
                "Interface / Bus:\r\n  {3}\r\n\r\n" +
                "Partition Table:\r\n  {4}{5}\r\n\r\n" +
                "Logical Sector:\r\n  {6} bytes\r\n" +
                "Physical Sector:\r\n  {7} bytes\r\n\r\n" +
                "Partitions:\r\n  {8} active (max {9})\r\n\r\n" +
                "Disk GUID / Sig:\r\n  {10}",
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
                _currentPreviewDisk.PartitionStyle == PartitionTableStyle.GPT ? _currentPreviewDisk.GptDiskGuid.ToString() : string.Format("0x{0:X8}", _currentPreviewDisk.MbrSignature)
            );

            _suppressUiEvents = true;
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

                    int rowIdx = _gridPartitions.Rows.Add(
                        p.IsUnallocated ? "unallocated" : p.ShortDeviceName,
                        nameOrLetter,
                        FileSystemColors.GetDisplayName(p.FileSystem),
                        p.IsUnallocated ? "" : p.Label,
                        ByteFormatter.FormatBytes(p.PartitionLength),
                        p.UsedBytes.HasValue ? ByteFormatter.FormatBytes(p.UsedBytes.Value) : "-",
                        p.FreeBytes.HasValue ? ByteFormatter.FormatBytes(p.FreeBytes.Value) : "-",
                        p.FlagsSummary,
                        p.IsUnallocated ? "-" : (p.IsAligned1MiB ? "1 MiB (OK)" : (p.IsAligned4K ? "4K (OK)" : "Misaligned")),
                        string.Format("{0:N0}..{1:N0}", p.StartLba, p.EndLba)
                    );
                    var row = _gridPartitions.Rows[rowIdx];
                    row.Height = 36;
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
                _suppressUiEvents = false;
            }

            if (remembered != null)
                _selectedOffsetByDisk[_currentPreviewDisk.DiskNumber] = remembered.StartingOffset;

            UpdateActionStates();
            UpdateIntegratedExplorerSelection();
            int diskPending = _queueManager.Queue.Count(o => o.TargetDiskNumber == _currentPreviewDisk.DiskNumber);
            _lblStatus.Text = string.Format("{0} | {1} pending on this disk ({2} total)", _currentPreviewDisk.DisplayName, diskPending, _queueManager.PendingCount);
        }

        private void OnGridCellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex >= 0 && e.ColumnIndex == 2)
            {
                var p = _gridPartitions.Rows[e.RowIndex].Tag as PartitionEntry;
                if (p == null) return;
                e.Paint(e.CellBounds, DataGridViewPaintParts.All & ~DataGridViewPaintParts.ContentForeground);
                int swatchSize = 14;
                int swatchY = e.CellBounds.Y + Math.Max(4, (e.CellBounds.Height - swatchSize) / 2);
                Rectangle swatch = new Rectangle(e.CellBounds.X + 8, swatchY, swatchSize, swatchSize);
                using (var br = new SolidBrush(FileSystemColors.GetBorderColor(p.FileSystem)))
                    e.Graphics.FillRectangle(br, swatch);
                using (var pen = new Pen(ThemeManager.BorderColor))
                    e.Graphics.DrawRectangle(pen, swatch);

                bool isSelected = (e.State & DataGridViewElementStates.Selected) != 0;
                Color textColor = isSelected ? e.CellStyle.SelectionForeColor : e.CellStyle.ForeColor;
                Rectangle textRect = new Rectangle(e.CellBounds.X + 28, e.CellBounds.Y, Math.Max(10, e.CellBounds.Width - 32), e.CellBounds.Height);
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
            UpdateIntegratedExplorerSelection();
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
                    UpdateIntegratedExplorerSelection();
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

        private void UpdateIntegratedExplorerSelection()
        {
            if (_integratedExplorer == null) return;
            var sel = GetSelectedPartition();
            int dNum = _currentPreviewDisk != null ? _currentPreviewDisk.DiskNumber : -1;
            long off = sel != null ? sel.StartingOffset : -1;
            var existingVol = (_currentPreviewDisk != null && sel != null && !sel.IsUnallocated)
                ? ReadOnlyFileSystemMountEngine.GetMount(dNum, off)
                : null;

            if (_boundExplorerDisk == dNum && _boundExplorerOffset == off && ReferenceEquals(_boundExplorerVolume, existingVol))
                return;

            _boundExplorerDisk = dNum;
            _boundExplorerOffset = off;
            bool autoMount = _currentPreviewDisk != null && _currentPreviewDisk.IsVirtualImage;
            _integratedExplorer.BindPartition(_currentPreviewDisk, sel, existingVol, autoMount);
            _boundExplorerVolume = (_currentPreviewDisk != null && sel != null && !sel.IsUnallocated)
                ? ReadOnlyFileSystemMountEngine.GetMount(dNum, off)
                : null;
        }

        private void UpdateActionStates()
        {
            var sel = GetSelectedPartition();
            bool hasSel = sel != null;
            bool isUnalloc = hasSel && sel.IsUnallocated;
            bool isPart = hasSel && !sel.IsUnallocated;

            _tbNew.Enabled = isUnalloc;
            _tbDelete.Enabled = isPart;
            _tbResize.Enabled = isPart;
            _tbCopy.Enabled = isPart;
            _tbPaste.Enabled = isUnalloc && _clipboardPartition != null;
            _tbFormat.Enabled = isPart;
            _tbFlags.Enabled = isPart;
            _tbWipe.Enabled = isPart;

            bool canRoMount = isPart && ReadOnlyFileSystemMountEngine.IsSupportedNonMicrosoftFs(sel.FileSystem);
            bool isRoMounted = canRoMount && _currentPreviewDisk != null &&
                ReadOnlyFileSystemMountEngine.GetMount(_currentPreviewDisk.DiskNumber, sel.StartingOffset) != null;
            if (_tbMountRo != null)
            {
                _tbMountRo.Enabled = canRoMount;
                _tbMountRo.Text = isRoMounted ? "Unmount (RO)" : "Mount (RO)";
            }
            if (_tbBrowseFs != null)
                _tbBrowseFs.Enabled = canRoMount;

            _tbUndo.Enabled = _queueManager.PendingCount > 0;
            _tbApply.Enabled = _queueManager.PendingCount > 0;
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
                _boundExplorerDisk = -1;
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
                var vol = ReadOnlyFileSystemMountEngine.GetMount(_currentPreviewDisk.DiskNumber, sel.StartingOffset)
                          ?? ReadOnlyFileSystemMountEngine.MountReadOnly(_currentPreviewDisk, sel, false);
                RenderCurrentDisk();
                using (var dlg = new FileSystemBrowserDialog(_currentPreviewDisk, sel, vol))
                    dlg.ShowDialog(this);
                _boundExplorerDisk = -1;
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
            UpdateActionStates();
        }

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
            if (sel.IsSystemOrBoot && !_currentPreviewDisk.IsVirtualImage)
            {
                MessageBox.Show(this, "Cannot delete an active Windows system/boot partition.", "Safety Guardrail", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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

            using (var dlg = new Form { Text = "Format Partition — " + sel.ShortDeviceName, Size = new Size(440, 230), FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false, Font = new Font("Segoe UI", 9f) })
            {
                var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16) };
                dlg.Controls.Add(root);

                var btnFlow = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 0) };
                var btnCancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(92, 30), Margin = new Padding(8, 0, 0, 0) };
                var btnOk = new Button { Text = "Queue Format", DialogResult = DialogResult.OK, Size = new Size(110, 30), Tag = "PrimaryButton" };
                btnFlow.Controls.Add(btnCancel);
                btnFlow.Controls.Add(btnOk);
                root.Controls.Add(btnFlow);

                var tbl = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
                tbl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
                tbl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                tbl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.Controls.Add(tbl);

                tbl.Controls.Add(new Label { Text = "File system:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 9, 12, 8) }, 0, 0);
                var cmbFs = new ComboBox { Anchor = AnchorStyles.Left | AnchorStyles.Right, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 6, 0, 8) };
                cmbFs.Items.AddRange(new object[] { FileSystemType.NTFS, FileSystemType.FAT32, FileSystemType.exFAT, FileSystemType.Ext4, FileSystemType.Ext3, FileSystemType.Btrfs, FileSystemType.XFS, FileSystemType.LinuxSwap, FileSystemType.APFS, FileSystemType.HFSPlus, FileSystemType.HFS });
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
                }
            }
        }

        private void QueueLabelPartition()
        {
            var sel = GetSelectedPartition();
            if (_currentPreviewDisk == null || sel == null || sel.IsUnallocated) return;

            using (var dlg = new Form { Text = "Label Partition — " + sel.ShortDeviceName, Size = new Size(440, 220), FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false, Font = new Font("Segoe UI", 9f) })
            {
                var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16) };
                dlg.Controls.Add(root);

                var btnFlow = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 0) };
                var btnCancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(90, 30), Margin = new Padding(8, 0, 0, 0) };
                var btnOk = new Button { Text = "OK", DialogResult = DialogResult.OK, Size = new Size(90, 30), Tag = "PrimaryButton" };
                btnFlow.Controls.Add(btnCancel);
                btnFlow.Controls.Add(btnOk);
                root.Controls.Add(btnFlow);

                var tbl = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
                tbl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
                tbl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                tbl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.Controls.Add(tbl);

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

            using (var dlg = new WipePartitionDialog(sel))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
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

        private void ApplyOperations()
        {
            if (_queueManager.PendingCount == 0) return;

            if (MessageBox.Show(this,
                string.Format("Are you sure you want to apply {0} pending operation(s)?\nEditing partitions can cause data loss if interrupted.", _queueManager.PendingCount),
                "Apply Pending Operations", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            try
            {
                Cursor = Cursors.WaitCursor;
                var results = _queueManager.ApplyAll((pct, msg) =>
                {
                    _lblStatus.Text = string.Format("[{0}%] {1}", pct, msg);
                    Application.DoEvents();
                });

                RefreshAllDisks();
                MessageBox.Show(this, string.Join("\n", results), "Operations Applied Successfully", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Operation failed:\n" + ex.Message, "Apply Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
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
            var sb = new System.Text.StringBuilder();
            Cursor = Cursors.WaitCursor;
            bool ok = false;
            try
            {
                ok = StressTestRunner.RunAll(line => sb.AppendLine(line));
            }
            catch (Exception ex)
            {
                sb.AppendLine("EXCEPTION: " + ex);
            }
            finally
            {
                Cursor = Cursors.Default;
            }

            using (var dlg = new Form
            {
                Text = ok ? "Stress & Data-Safety Test Suite — ALL PASSED" : "Stress & Data-Safety Test Suite — FAILED",
                Size = new Size(820, 560),
                StartPosition = FormStartPosition.CenterParent
            })
            {
                var txt = new TextBox
                {
                    Dock = DockStyle.Fill,
                    Multiline = true,
                    ReadOnly = true,
                    ScrollBars = ScrollBars.Both,
                    Font = new Font("Consolas", 9f),
                    BackColor = Color.FromArgb(15, 23, 42),
                    ForeColor = ok ? Color.FromArgb(134, 239, 172) : Color.FromArgb(252, 165, 165),
                    Text = sb.ToString()
                };
                dlg.Controls.Add(txt);
                dlg.ShowDialog(this);
            }
        }
    }
}
