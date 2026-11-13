using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using WinParted.Core;
using WinParted.Interop;
using WinParted.Models;

namespace WinParted.UI
{
    public class IntegratedFileSystemExplorerControl : UserControl
    {
        private const string ParentSentinel = "..PARENT..";

        private DiskInfo _disk;
        private PartitionEntry _partition;
        private MountedReadOnlyVolume _volume;
        private VirtualFsNode _selectedFileNode;
        private bool _isSearchActive;
        private bool _suppressToggleEvent;
        private bool _suppressTreeEvent;
        private bool _navigatingHistory;

        private readonly List<string> _navHistory = new List<string>();
        private int _navIndex = -1;
        private int _sortColumn = 0;
        private bool _sortAscending = true;
        private volatile bool _copyInProgress;
        private volatile bool _copyCancelRequested;

        private ImageList _smallIcons;
        private ImageList _largeIcons;

        private readonly Panel _topBar;
        private readonly Label _lblVolumeBanner;
        private readonly Button _btnMountNow;
        private readonly Button _btnTogglePreview;

        private readonly Button _btnBack;
        private readonly Button _btnForward;
        private readonly Button _btnUp;
        private readonly TextBox _txtPath;
        private readonly TextBox _txtSearch;
        private readonly Button _btnSearch;
        private readonly Button _btnClearSearch;
        private readonly ComboBox _cmbViewMode;
        private readonly Button _btnCopySelectedToHost;
        private readonly Button _btnCopyFolderToHost;

        private readonly Panel _experimentalPanel;
        private readonly CheckBox _chkExperimentalWrite;
        private readonly Button _btnExpWriteFile;
        private readonly Button _btnExpWriteFolder;
        private readonly Button _btnExpNewFolder;
        private readonly Button _btnExpDelete;

        private readonly SplitContainer _mainSplit;
        private readonly SplitContainer _rightSplit;
        private readonly TreeView _treeDirs;
        private readonly ListView _lvEntries;
        private readonly TextBox _txtPreview;
        private readonly CheckBox _chkHexView;
        private readonly Label _lblSelectedFile;

        private readonly Panel _statusBar;
        private readonly Label _lblExplorerStatus;
        private readonly ProgressBar _copyProgressBar;
        private readonly Button _btnCancelCopy;

        private readonly ContextMenuStrip _entryContextMenu;
        private readonly ToolTip _toolTip = new ToolTip();

        public event EventHandler VolumeStateChanged;

        public IntegratedFileSystemExplorerControl()
        {
            Size = new Size(920, 520);
            Dock = DockStyle.Fill;
            Font = new Font("Segoe UI", 9.0f);

            BuildIconLists();

            // 1. Top M3 Navigation & Command Header
            _topBar = new Panel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(10, 6, 10, 6),
                Tag = "ElevatedPanel"
            };

            // Row 1: Volume Status + Mount/Refresh + Preview Pane toggle
            var row1 = new Panel { Dock = DockStyle.Top, Height = 28 };
            _btnTogglePreview = new Button
            {
                Dock = DockStyle.Right,
                Width = 104,
                Height = 26,
                Text = "Preview Pane",
                Tag = "TonalButton",
                Margin = new Padding(6, 0, 0, 0)
            };
            _btnTogglePreview.Click += (s, e) => TogglePreviewPane();

            var spacerR1 = new Panel { Dock = DockStyle.Right, Width = 6, Tag = "ElevatedPanel" };

            _btnMountNow = new Button
            {
                Dock = DockStyle.Right,
                Width = 138,
                Height = 26,
                Text = "Mount (Read-Only)",
                Tag = "PrimaryButton",
                Visible = false
            };
            _btnMountNow.Click += (s, e) => MountCurrentPartitionInteractive();

            _lblVolumeBanner = new Label
            {
                Dock = DockStyle.Fill,
                Text = "Foreign Filesystem Explorer — select a non-Microsoft/dev partition (ext2/3/4, Btrfs, XFS, F2FS, ZFS, SquashFS, ISO9660, UDF, APFS, HFS+)",
                Font = new Font("Segoe UI", 9.0f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                Tag = "KeepForeColor"
            };

            row1.Controls.Add(_lblVolumeBanner);
            row1.Controls.Add(spacerR1);
            row1.Controls.Add(_btnMountNow);
            row1.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 6, Tag = "ElevatedPanel" });
            row1.Controls.Add(_btnTogglePreview);

            // Row 2: Explorer Navigation (Back / Forward / Up / Address Bar / Search / View Mode)
            var row2 = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 32,
                ColumnCount = 10,
                RowCount = 1,
                Margin = new Padding(0, 4, 0, 2),
                Padding = new Padding(0, 4, 0, 0)
            };
            row2.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); // Back
            row2.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); // Forward
            row2.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); // Up
            row2.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); // "Path:"
            row2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58f)); // Address Bar
            row2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42f)); // Search Box
            row2.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); // Search btn
            row2.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); // Clear btn
            row2.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); // "View:"
            row2.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); // View Mode Combo

            _btnBack = MakeNavButton("\u2190", "Back to previous directory");
            _btnBack.Enabled = false;
            _btnBack.Click += (s, e) => NavigateBack();

            _btnForward = MakeNavButton("\u2192", "Forward to next directory");
            _btnForward.Enabled = false;
            _btnForward.Click += (s, e) => NavigateForward();

            _btnUp = MakeNavButton("\u2191", "Up to parent folder");
            _btnUp.Enabled = false;
            _btnUp.Click += (s, e) => NavigateUp();

            var lblPath = new Label
            {
                Text = "Path:",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(4, 0, 4, 0)
            };

            _txtPath = new TextBox
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 2, 8, 2),
                Enabled = false
            };
            _txtPath.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    NavigateToTypedPath();
                }
            };

            _txtSearch = new TextBox
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 2, 4, 2),
                Enabled = false
            };
            _txtSearch.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    ExecuteSearch();
                }
            };

            _btnSearch = new Button
            {
                Text = "Search",
                AutoSize = true,
                MinimumSize = new Size(64, 25),
                Margin = new Padding(0, 1, 4, 1),
                Tag = "TonalButton",
                Enabled = false
            };
            _btnSearch.Click += (s, e) => ExecuteSearch();

            _btnClearSearch = new Button
            {
                Text = "Clear",
                AutoSize = true,
                MinimumSize = new Size(52, 25),
                Margin = new Padding(0, 1, 8, 1),
                Enabled = false
            };
            _btnClearSearch.Click += (s, e) => ClearSearch();

            var lblView = new Label
            {
                Text = "View:",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 0, 4, 0)
            };

            _cmbViewMode = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 104,
                Margin = new Padding(0, 2, 0, 2)
            };
            _cmbViewMode.Items.AddRange(new object[] { "Details", "Tiles", "Large Icons", "List" });
            _cmbViewMode.SelectedIndex = 0;
            _cmbViewMode.SelectedIndexChanged += (s, e) => ApplySelectedViewMode();

            row2.Controls.Add(_btnBack, 0, 0);
            row2.Controls.Add(_btnForward, 1, 0);
            row2.Controls.Add(_btnUp, 2, 0);
            row2.Controls.Add(lblPath, 3, 0);
            row2.Controls.Add(_txtPath, 4, 0);
            row2.Controls.Add(_txtSearch, 5, 0);
            row2.Controls.Add(_btnSearch, 6, 0);
            row2.Controls.Add(_btnClearSearch, 7, 0);
            row2.Controls.Add(lblView, 8, 0);
            row2.Controls.Add(_cmbViewMode, 9, 0);

            // Row 3: Export Actions
            var row3 = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Padding = new Padding(0, 4, 0, 0)
            };

            _btnCopySelectedToHost = new Button
            {
                Text = "Copy Selected to Host...",
                AutoSize = true,
                MinimumSize = new Size(156, 26),
                Margin = new Padding(0, 1, 6, 1),
                Tag = "PrimaryButton",
                Enabled = false
            };
            _btnCopySelectedToHost.Click += (s, e) => ExportSelectedNodeToHost();

            _btnCopyFolderToHost = new Button
            {
                Text = "Export Current Folder...",
                AutoSize = true,
                MinimumSize = new Size(156, 26),
                Margin = new Padding(0, 1, 6, 1),
                Tag = "TonalButton",
                Enabled = false
            };
            _btnCopyFolderToHost.Click += (s, e) => ExportCurrentDirectoryToHost();

            row3.Controls.Add(_btnCopySelectedToHost);
            row3.Controls.Add(_btnCopyFolderToHost);

            _topBar.Controls.Add(row3);
            _topBar.Controls.Add(row2);
            _topBar.Controls.Add(row1);

            // 2. Right Side Panel — Vivid Red Experimental Write Mode
            _experimentalPanel = new Panel
            {
                Dock = DockStyle.Right,
                Width = 216,
                Padding = new Padding(10),
                Tag = "ExperimentalPanel"
            };
            _experimentalPanel.Paint += OnExperimentalPanelPaint;

            var expHeader = new Panel
            {
                Dock = DockStyle.Top,
                Height = 30,
                Padding = new Padding(6, 4, 6, 4),
                Tag = "ExperimentalHeaderPanel"
            };
            expHeader.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = "EXPERIMENTAL WRITE",
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColor = Color.White,
                TextAlign = ContentAlignment.MiddleCenter,
                Tag = "KeepForeColor"
            });

            var lblExpWarn = new Label
            {
                Dock = DockStyle.Top,
                Height = 64,
                Padding = new Padding(0, 6, 0, 4),
                Text = "EXPERIMENTAL: Writing files or folders to non-Microsoft partitions may risk data loss. Off by default.",
                Font = new Font("Segoe UI", 8.0f, FontStyle.Bold),
                Tag = "ExperimentalLabel"
            };

            _chkExperimentalWrite = new CheckBox
            {
                Dock = DockStyle.Top,
                Height = 36,
                Appearance = Appearance.Button,
                TextAlign = ContentAlignment.MiddleCenter,
                Text = "Experimental Write: OFF",
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                Tag = "ExperimentalToggle",
                Enabled = false
            };
            _chkExperimentalWrite.CheckedChanged += OnExperimentalToggleChanged;

            var expButtonsStack = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(0, 8, 0, 0),
                AutoScroll = true
            };

            _btnExpWriteFile = MakeExperimentalButton("+ Write File from Host...");
            _btnExpWriteFile.Click += (s, e) => WriteHostFileInteractive();

            _btnExpWriteFolder = MakeExperimentalButton("+ Write Folder from Host...");
            _btnExpWriteFolder.Click += (s, e) => WriteHostFolderInteractive();

            _btnExpNewFolder = MakeExperimentalButton("+ New Folder...");
            _btnExpNewFolder.Click += (s, e) => CreateDirectoryInteractive();

            _btnExpDelete = MakeExperimentalButton("Delete Selected Entry");
            _btnExpDelete.Click += (s, e) => DeleteSelectedInteractive();

            expButtonsStack.Controls.Add(_btnExpWriteFile);
            expButtonsStack.Controls.Add(_btnExpWriteFolder);
            expButtonsStack.Controls.Add(_btnExpNewFolder);
            expButtonsStack.Controls.Add(_btnExpDelete);

            _experimentalPanel.Controls.Add(expButtonsStack);
            _experimentalPanel.Controls.Add(_chkExperimentalWrite);
            _experimentalPanel.Controls.Add(lblExpWarn);
            _experimentalPanel.Controls.Add(expHeader);

            // 3. Bottom Explorer Status & Live Multi-GB Copy Progress Bar
            _statusBar = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 28,
                Padding = new Padding(10, 3, 10, 3),
                Tag = "StatusBar"
            };
            _btnCancelCopy = new Button
            {
                Dock = DockStyle.Right,
                Width = 72,
                Height = 22,
                Text = "Cancel",
                Visible = false,
                Tag = "DangerButton"
            };
            _btnCancelCopy.Click += (s, e) => { _copyCancelRequested = true; _btnCancelCopy.Enabled = false; };

            var copySpacer = new Panel { Dock = DockStyle.Right, Width = 8, Tag = "StatusBar" };

            _copyProgressBar = new ProgressBar
            {
                Dock = DockStyle.Right,
                Width = 170,
                Height = 16,
                Minimum = 0,
                Maximum = 1000,
                Visible = false
            };

            _lblExplorerStatus = new Label
            {
                Dock = DockStyle.Fill,
                Text = "Ready",
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                Font = new Font("Segoe UI", 8.4f)
            };

            _statusBar.Controls.Add(_lblExplorerStatus);
            _statusBar.Controls.Add(copySpacer);
            _statusBar.Controls.Add(_copyProgressBar);
            _statusBar.Controls.Add(_btnCancelCopy);

            // 4. Center Directory Tree + File Explorer View + Collapsible Preview Pane
            _mainSplit = new SplitContainer
            {
                Size = new Size(700, 420),
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                Panel1MinSize = 25,
                Panel2MinSize = 25,
                SplitterWidth = 5
            };
            SetSplitterDistanceSafe(_mainSplit, 210);

            _treeDirs = new TreeView
            {
                Dock = DockStyle.Fill,
                HideSelection = false,
                BorderStyle = BorderStyle.None,
                ShowLines = true,
                FullRowSelect = true,
                ImageList = _smallIcons,
                ImageIndex = 0,
                SelectedImageIndex = 1,
                ItemHeight = 22
            };
            _treeDirs.AfterSelect += (s, e) => OnDirectorySelected();
            _mainSplit.Panel1.Controls.Add(_treeDirs);

            _rightSplit = new SplitContainer
            {
                Size = new Size(700, 420),
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                Panel1MinSize = 25,
                Panel2MinSize = 25,
                SplitterWidth = 5
            };
            SetSplitterDistanceSafe(_rightSplit, 240);
            _mainSplit.Panel2.Controls.Add(_rightSplit);

            _entryContextMenu = BuildEntryContextMenu();

            _lvEntries = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = false,
                BorderStyle = BorderStyle.None,
                SmallImageList = _smallIcons,
                LargeImageList = _largeIcons,
                MultiSelect = false,
                ContextMenuStrip = _entryContextMenu
            };
            _lvEntries.Columns.Add("Name", 240);
            _lvEntries.Columns.Add("Type", 96);
            _lvEntries.Columns.Add("Size", 90, HorizontalAlignment.Right);
            _lvEntries.Columns.Add("Modified (UTC)", 132);
            _lvEntries.Columns.Add("Permissions", 88);
            _lvEntries.Columns.Add("Path", 190);
            _lvEntries.Columns.Add("ID", 64);
            _lvEntries.ListViewItemSorter = new EntryListComparer(this);
            _lvEntries.ColumnClick += OnListViewColumnClick;
            _lvEntries.SelectedIndexChanged += (s, e) => OnEntrySelected();
            _lvEntries.DoubleClick += (s, e) => OnEntryDoubleClick();
            _lvEntries.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    OnEntryDoubleClick();
                }
                else if (e.KeyCode == Keys.Back)
                {
                    e.SuppressKeyPress = true;
                    NavigateUp();
                }
            };
            _rightSplit.Panel1.Controls.Add(_lvEntries);

            var previewContainer = new Panel { Dock = DockStyle.Fill };
            _rightSplit.Panel2.Controls.Add(previewContainer);

            var previewBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 28,
                Padding = new Padding(8, 3, 8, 3),
                Tag = "ElevatedPanel"
            };
            _chkHexView = new CheckBox
            {
                Dock = DockStyle.Right,
                Width = 85,
                Text = "Hex View",
                Checked = false
            };
            _chkHexView.CheckedChanged += (s, e) => RenderSelectedFilePreview();

            _lblSelectedFile = new Label
            {
                Dock = DockStyle.Fill,
                Text = "Select a file to inspect (Read-Only)",
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
            };
            previewBar.Controls.Add(_lblSelectedFile);
            previewBar.Controls.Add(_chkHexView);

            _txtPreview = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Font = new Font("Consolas", 9.0f),
                Text = "Select a file above to preview or copy its contents to the host system."
            };

            previewContainer.Controls.Add(_txtPreview);
            previewContainer.Controls.Add(previewBar);

            // Control assembly order
            Controls.Add(_mainSplit);
            Controls.Add(_experimentalPanel);
            Controls.Add(_statusBar);
            Controls.Add(_topBar);

            Load += (s, e) =>
            {
                SetSplitterDistanceSafe(_mainSplit, 210);
                SetSplitterDistanceSafe(_rightSplit, 240);
            };

            ThemeManager.ApplyTheme(this);
        }

        private Button MakeNavButton(string symbol, string tip)
        {
            var btn = new Button
            {
                Text = symbol,
                Width = 30,
                Height = 25,
                Margin = new Padding(0, 1, 3, 1),
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
            };
            _toolTip.SetToolTip(btn, tip);
            return btn;
        }

        private ContextMenuStrip BuildEntryContextMenu()
        {
            var cms = new ContextMenuStrip();
            var mOpen = new ToolStripMenuItem("Open");
            mOpen.Click += (s, e) => OnEntryDoubleClick();

            var mCopy = new ToolStripMenuItem("Copy to Host...");
            mCopy.Click += (s, e) => ExportSelectedNodeToHost();

            var mUp = new ToolStripMenuItem("Go Up One Folder");
            mUp.Click += (s, e) => NavigateUp();

            var mRefresh = new ToolStripMenuItem("Refresh");
            mRefresh.Click += (s, e) => { if (_volume != null) MountCurrentPartitionInteractive(); };

            cms.Items.AddRange(new ToolStripItem[] { mOpen, mCopy, new ToolStripSeparator(), mUp, mRefresh });
            cms.Opening += (s, e) =>
            {
                bool hasSel = _lvEntries.SelectedItems.Count > 0;
                bool isNode = hasSel && (_lvEntries.SelectedItems[0].Tag is VirtualFsNode);
                mOpen.Enabled = hasSel;
                mCopy.Enabled = isNode && !_copyInProgress;
                mUp.Enabled = CanNavigateUp();
                mRefresh.Enabled = _volume != null;
            };
            ThemeManager.ApplyContextMenu(cms);
            return cms;
        }

        private void BuildIconLists()
        {
            _smallIcons = new ImageList { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
            _largeIcons = new ImageList { ImageSize = new Size(32, 32), ColorDepth = ColorDepth.Depth32Bit };

            string[] keys = new string[] { "folder", "folder_open", "up", "file", "disk_image", "config", "bundle" };
            for (int i = 0; i < keys.Length; i++)
            {
                _smallIcons.Images.Add(keys[i], DrawM3Icon(keys[i], 16));
                _largeIcons.Images.Add(keys[i], DrawM3Icon(keys[i], 32));
            }
        }

        private static Bitmap DrawM3Icon(string kind, int sz)
        {
            var bmp = new Bitmap(sz, sz);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                float u = sz / 16.0f;

                if (kind == "folder" || kind == "folder_open" || kind == "up")
                {
                    Color fill = kind == "folder_open"
                        ? Color.FromArgb(251, 191, 36)
                        : (kind == "up" ? Color.FromArgb(147, 197, 253) : Color.FromArgb(245, 158, 11));
                    Color edge = kind == "up" ? Color.FromArgb(37, 99, 235) : Color.FromArgb(180, 108, 0);

                    using (var b = new SolidBrush(fill))
                    using (var p = new Pen(edge, Math.Max(1f, u * 0.9f)))
                    {
                        g.FillRectangle(b, 1.5f * u, 2.5f * u, 5.5f * u, 2.5f * u);
                        g.FillRectangle(b, 1.5f * u, 4.2f * u, 13f * u, 9.2f * u);
                        g.DrawRectangle(p, 1.5f * u, 4.2f * u, 13f * u, 9.2f * u);
                    }
                    if (kind == "up")
                    {
                        using (var ap = new Pen(Color.FromArgb(20, 45, 105), Math.Max(1.4f, u * 1.4f)))
                        {
                            g.DrawLine(ap, 8f * u, 11.8f * u, 8f * u, 6.2f * u);
                            g.DrawLine(ap, 5.5f * u, 8.3f * u, 8f * u, 5.8f * u);
                            g.DrawLine(ap, 10.5f * u, 8.3f * u, 8f * u, 5.8f * u);
                        }
                    }
                    return bmp;
                }

                Color pageFill = Color.FromArgb(236, 240, 248);
                Color badgeColor = Color.FromArgb(100, 116, 139);
                if (kind == "disk_image") badgeColor = Color.FromArgb(27, 110, 243);
                else if (kind == "config") badgeColor = Color.FromArgb(20, 148, 136);
                else if (kind == "bundle") badgeColor = Color.FromArgb(139, 92, 246);

                using (var pb = new SolidBrush(pageFill))
                using (var pp = new Pen(Color.FromArgb(120, 132, 152), Math.Max(1f, u * 0.9f)))
                using (var ab = new SolidBrush(badgeColor))
                {
                    g.FillRectangle(pb, 2.5f * u, 1.5f * u, 11f * u, 13f * u);
                    g.DrawRectangle(pp, 2.5f * u, 1.5f * u, 11f * u, 13f * u);
                    g.FillRectangle(ab, 4.0f * u, 8.2f * u, 8.0f * u, 4.6f * u);
                    g.FillRectangle(ab, 4.0f * u, 4.2f * u, 5.5f * u, 1.5f * u);
                }
            }
            return bmp;
        }

        private static int GetNodeIconIndex(VirtualFsNode node)
        {
            if (node == null) return 3;
            string name = (node.Name ?? string.Empty).ToLowerInvariant();
            if (node.IsDirectory)
            {
                if (name.EndsWith(".app") || name.EndsWith(".pkg") || name.EndsWith(".kext") ||
                    name.EndsWith(".bundle") || name.EndsWith(".framework"))
                    return 6;
                return 0;
            }
            if (name.EndsWith(".dmg") || name.EndsWith(".iso") || name.EndsWith(".img") ||
                name.EndsWith(".cdr") || name.EndsWith(".sparseimage") || name.EndsWith(".pkg"))
                return 4;
            if (name.EndsWith(".plist") || name.EndsWith(".xml") || name.EndsWith(".json") ||
                name.EndsWith(".conf") || name.EndsWith(".txt") || name.EndsWith(".log") || name.EndsWith(".md"))
                return 5;
            return 3;
        }

        private static string GetNodeTypeLabel(VirtualFsNode node)
        {
            if (node == null) return string.Empty;
            if (node.IsDirectory)
            {
                string low = (node.Name ?? string.Empty).ToLowerInvariant();
                if (low.EndsWith(".app")) return "Application";
                if (low.EndsWith(".framework") || low.EndsWith(".bundle")) return "Bundle";
                return "File folder";
            }
            string ext = Path.GetExtension(node.Name ?? string.Empty);
            if (!string.IsNullOrEmpty(ext))
                return ext.TrimStart('.').ToUpperInvariant() + " File";
            return "File";
        }

        internal static void SetSplitterDistanceSafe(SplitContainer sc, int desired)
        {
            if (sc == null || sc.IsDisposed) return;
            int total = (sc.Orientation == Orientation.Vertical) ? sc.Width : sc.Height;
            int min = sc.Panel1MinSize;
            int max = total - sc.Panel2MinSize - sc.SplitterWidth;
            if (max < min) return;
            sc.SplitterDistance = Math.Max(min, Math.Min(max, desired));
        }

        private Button MakeExperimentalButton(string text)
        {
            return new Button
            {
                Text = text,
                Width = 192,
                Height = 28,
                Margin = new Padding(0, 0, 0, 6),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(6, 0, 6, 0),
                Tag = "DangerButton",
                Enabled = false
            };
        }

        private void OnExperimentalPanelPaint(object sender, PaintEventArgs e)
        {
            var r = _experimentalPanel.ClientRectangle;
            using (var pen = new Pen(ThemeManager.AccentRed, 2))
            {
                e.Graphics.DrawRectangle(pen, 1, 1, Math.Max(1, r.Width - 2), Math.Max(1, r.Height - 2));
            }
        }

        public void BindPartition(DiskInfo disk, PartitionEntry partition, MountedReadOnlyVolume mountedVol, bool autoMount)
        {
            _disk = disk;
            _partition = partition;
            _selectedFileNode = null;
            _isSearchActive = false;
            _txtSearch.Text = string.Empty;
            _navHistory.Clear();
            _navIndex = -1;

            if (disk == null || partition == null || partition.IsUnallocated ||
                !ReadOnlyFileSystemMountEngine.IsSupportedNonMicrosoftFs(partition.FileSystem))
            {
                _volume = null;
                RenderEmptyState(partition);
                return;
            }

            MountedReadOnlyVolume existing = mountedVol ?? ReadOnlyFileSystemMountEngine.GetMount(disk.DiskNumber, partition.StartingOffset);
            if (existing == null && autoMount)
            {
                try
                {
                    existing = ReadOnlyFileSystemMountEngine.MountReadOnly(disk, partition, false);
                }
                catch (Exception ex)
                {
                    _volume = null;
                    RenderUnmountedState(partition, ex.Message);
                    return;
                }
            }

            if (existing == null)
            {
                _volume = null;
                RenderUnmountedState(partition, null);
                return;
            }

            _volume = existing;
            RenderMountedVolume();
        }

        private void RenderEmptyState(PartitionEntry part)
        {
            _treeDirs.Nodes.Clear();
            _lvEntries.Items.Clear();
            _btnMountNow.Visible = false;
            _btnSearch.Enabled = false;
            _btnClearSearch.Enabled = false;
            _txtSearch.Enabled = false;
            _txtPath.Enabled = false;
            _txtPath.Text = string.Empty;
            _btnBack.Enabled = false;
            _btnForward.Enabled = false;
            _btnUp.Enabled = false;
            _btnCopySelectedToHost.Enabled = false;
            _btnCopyFolderToHost.Enabled = false;
            _chkExperimentalWrite.Enabled = false;
            SetExperimentalWriteUiChecked(false);

            if (part == null || part.IsUnallocated)
            {
                _lblVolumeBanner.ForeColor = ThemeManager.TextSecondary;
                _lblVolumeBanner.Text = "Foreign Filesystem Explorer — select an ext2/3/4, Btrfs, XFS, F2FS, ZFS, SquashFS, ISO9660, UDF, APFS, HFS+, or HFS partition.";
                _txtPreview.Text = "No foreign filesystem partition selected.";
                _lblExplorerStatus.Text = "Ready";
            }
            else
            {
                _lblVolumeBanner.ForeColor = ThemeManager.TextSecondary;
                _lblVolumeBanner.Text = string.Format("Native Windows FS ({0}) — browse directly in Windows Explorer.", FileSystemColors.GetDisplayName(part.FileSystem));
                _txtPreview.Text = string.Format("Selected partition uses {0} ({1}), which is natively supported by Windows.",
                    FileSystemColors.GetDisplayName(part.FileSystem), part.ShortDeviceName);
                _lblExplorerStatus.Text = part.ShortDeviceName + " (Native Windows Filesystem)";
            }
            UpdateExperimentalActionStates();
        }

        private void RenderUnmountedState(PartitionEntry part, string errorMessage)
        {
            _treeDirs.Nodes.Clear();
            _lvEntries.Items.Clear();
            _btnMountNow.Visible = true;
            _btnMountNow.Text = "Mount (Read-Only)";
            _btnSearch.Enabled = false;
            _btnClearSearch.Enabled = false;
            _txtSearch.Enabled = false;
            _txtPath.Enabled = false;
            _txtPath.Text = string.Empty;
            _btnBack.Enabled = false;
            _btnForward.Enabled = false;
            _btnUp.Enabled = false;
            _btnCopySelectedToHost.Enabled = false;
            _btnCopyFolderToHost.Enabled = false;
            _chkExperimentalWrite.Enabled = false;
            SetExperimentalWriteUiChecked(false);

            _lblVolumeBanner.ForeColor = ThemeManager.AccentAmber;
            _lblVolumeBanner.Text = string.Format("{0} ({1}) is not mounted yet — click 'Mount (Read-Only)' to browse or copy files.",
                part.ShortDeviceName, FileSystemColors.GetDisplayName(part.FileSystem));
            _txtPreview.Text = string.IsNullOrEmpty(errorMessage)
                ? "Click 'Mount (Read-Only)' in the top-right corner of this explorer or double-click the partition to load its directory tree."
                : ("Unable to mount automatically: " + errorMessage);
            _lblExplorerStatus.Text = "Not mounted";
            UpdateExperimentalActionStates();
        }

        private void RenderMountedVolume()
        {
            _btnMountNow.Visible = true;
            _btnMountNow.Text = "Refresh";
            _txtSearch.Enabled = true;
            _txtPath.Enabled = true;
            _btnSearch.Enabled = true;
            _btnClearSearch.Enabled = _isSearchActive;
            _btnCopyFolderToHost.Enabled = (_volume != null && _volume.RootNode != null && !_copyInProgress);
            _chkExperimentalWrite.Enabled = (_volume != null);

            SetExperimentalWriteUiChecked(_volume != null && _volume.ExperimentalWriteEnabled);
            UpdateBannerText();
            PopulateDirectoryTree();
            UpdateExperimentalActionStates();
        }

        private void UpdateBannerText()
        {
            if (_volume == null) return;
            if (_volume.ExperimentalWriteEnabled)
            {
                _lblVolumeBanner.ForeColor = ThemeManager.AccentRed;
                _lblVolumeBanner.Text = string.Format("EXPERIMENTAL RW ACTIVE  |  {0} ({1})  |  Label: {2}",
                    _volume.MountPath, FileSystemColors.GetDisplayName(_volume.FileSystem),
                    string.IsNullOrEmpty(_volume.Label) ? "(none)" : _volume.Label);
            }
            else
            {
                _lblVolumeBanner.ForeColor = ThemeManager.AccentGreen;
                _lblVolumeBanner.Text = string.Format("READ-ONLY MOUNT  |  {0}  |  {1}",
                    _volume.MountPath, _volume.SuperblockSummary);
            }
        }

        private void TogglePreviewPane()
        {
            _rightSplit.Panel2Collapsed = !_rightSplit.Panel2Collapsed;
            _btnTogglePreview.Text = _rightSplit.Panel2Collapsed ? "Show Preview" : "Hide Preview";
        }

        private void ApplySelectedViewMode()
        {
            string mode = _cmbViewMode.SelectedItem as string;
            if (mode == "Tiles")
            {
                _lvEntries.View = View.Tile;
                _lvEntries.TileSize = new Size(210, 44);
            }
            else if (mode == "Large Icons")
            {
                _lvEntries.View = View.LargeIcon;
            }
            else if (mode == "List")
            {
                _lvEntries.View = View.List;
            }
            else
            {
                _lvEntries.View = View.Details;
            }
        }

        private void MountCurrentPartitionInteractive()
        {
            if (_disk == null || _partition == null || _partition.IsUnallocated) return;
            try
            {
                _volume = ReadOnlyFileSystemMountEngine.MountReadOnly(_disk, _partition, false);
                _isSearchActive = false;
                RenderMountedVolume();
                NotifyVolumeStateChanged();
            }
            catch (Exception ex)
            {
                var win32 = ex as Win32Exception;
                bool accessDenied = (ex is UnauthorizedAccessException) || (win32 != null && win32.NativeErrorCode == 5);
                if (!_disk.IsVirtualImage && !Win32DiskInterop.IsAdministrator() && (accessDenied || !_disk.CanRawRead))
                {
                    var dr = MessageBox.Show(this,
                        string.Format("Administrator privileges are required to read raw {0} sectors on {1}.\n\nDetails: {2}\n\nRestart WinParted as Administrator?",
                            FileSystemColors.GetDisplayName(_partition.FileSystem), _disk.DevicePath, ex.Message),
                        "Administrator Required", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (dr == DialogResult.Yes && Win32DiskInterop.RelaunchAsAdministrator())
                        Application.Exit();
                    return;
                }
                MessageBox.Show(this, ex.Message, "Mount Foreign Filesystem", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void PopulateDirectoryTree()
        {
            string prevSelectedPath = (_treeDirs.SelectedNode != null && _treeDirs.SelectedNode.Tag is VirtualFsNode)
                ? ((VirtualFsNode)_treeDirs.SelectedNode.Tag).FullPath
                : "/";

            _suppressTreeEvent = true;
            _treeDirs.BeginUpdate();
            try
            {
                _treeDirs.Nodes.Clear();
                if (_volume == null || _volume.RootNode == null) return;

                var rootTn = BuildDirTreeNode(_volume.RootNode);
                _treeDirs.Nodes.Add(rootTn);
                rootTn.Expand();

                TreeNode target = FindTreeNodeByPath(_treeDirs.Nodes, prevSelectedPath) ?? rootTn;
                _treeDirs.SelectedNode = target;
            }
            finally
            {
                _treeDirs.EndUpdate();
                _suppressTreeEvent = false;
            }

            OnDirectorySelected();
        }

        private TreeNode BuildDirTreeNode(VirtualFsNode dir)
        {
            int ico = GetNodeIconIndex(dir);
            var tn = new TreeNode(dir.Name, ico, ico == 0 ? 1 : ico) { Tag = dir };
            foreach (var child in dir.Children)
            {
                if (child.IsDirectory)
                    tn.Nodes.Add(BuildDirTreeNode(child));
            }
            return tn;
        }

        private void OnDirectorySelected()
        {
            if (_suppressTreeEvent) return;
            _isSearchActive = false;
            _btnClearSearch.Enabled = false;

            var dir = GetCurrentDirectoryNode();
            if (dir == null) return;

            if (!_navigatingHistory)
                PushNavHistory(dir.FullPath);

            _txtPath.Text = dir.FullPath;
            UpdateNavigationButtons();
            PopulateEntriesList(dir.Children, false, dir.FullPath);
        }

        private void PushNavHistory(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (_navIndex >= 0 && _navIndex < _navHistory.Count &&
                string.Equals(_navHistory[_navIndex], path, StringComparison.OrdinalIgnoreCase))
                return;

            if (_navIndex + 1 < _navHistory.Count)
                _navHistory.RemoveRange(_navIndex + 1, _navHistory.Count - _navIndex - 1);

            _navHistory.Add(path);
            _navIndex = _navHistory.Count - 1;
        }

        private void UpdateNavigationButtons()
        {
            _btnBack.Enabled = (_navIndex > 0);
            _btnForward.Enabled = (_navIndex >= 0 && _navIndex + 1 < _navHistory.Count);
            _btnUp.Enabled = CanNavigateUp();
        }

        private bool CanNavigateUp()
        {
            if (_isSearchActive) return true;
            var dir = GetCurrentDirectoryNode();
            return dir != null && dir.FullPath != "/";
        }

        private void NavigateBack()
        {
            if (_navIndex <= 0) return;
            _navIndex--;
            NavigateToPathInternal(_navHistory[_navIndex], true);
        }

        private void NavigateForward()
        {
            if (_navIndex + 1 >= _navHistory.Count) return;
            _navIndex++;
            NavigateToPathInternal(_navHistory[_navIndex], true);
        }

        private void NavigateUp()
        {
            if (_isSearchActive)
            {
                ClearSearch();
                return;
            }
            var dir = GetCurrentDirectoryNode();
            if (dir == null || dir.FullPath == "/") return;

            int lastSlash = dir.FullPath.LastIndexOf('/');
            string parentPath = lastSlash <= 0 ? "/" : dir.FullPath.Substring(0, lastSlash);
            NavigateToPathInternal(parentPath, false);
        }

        private void NavigateToTypedPath()
        {
            string target = (_txtPath.Text ?? string.Empty).Trim();
            if (target.Length == 0) target = "/";
            if (!target.StartsWith("/")) target = "/" + target;
            if (!NavigateToPathInternal(target, false))
                _lblExplorerStatus.Text = "Folder not found: " + target;
        }

        private bool NavigateToPathInternal(string path, bool fromHistory)
        {
            TreeNode tn = FindTreeNodeByPath(_treeDirs.Nodes, path);
            if (tn == null) return false;

            _isSearchActive = false;
            _btnClearSearch.Enabled = false;
            _navigatingHistory = fromHistory;
            try
            {
                if (tn.Parent != null) tn.Parent.Expand();
                _treeDirs.SelectedNode = tn;
            }
            finally
            {
                _navigatingHistory = false;
            }
            UpdateNavigationButtons();
            return true;
        }

        private VirtualFsNode GetCurrentDirectoryNode()
        {
            if (_treeDirs.SelectedNode != null && _treeDirs.SelectedNode.Tag is VirtualFsNode)
                return (VirtualFsNode)_treeDirs.SelectedNode.Tag;
            return _volume != null ? _volume.RootNode : null;
        }

        private void PopulateEntriesList(IList<VirtualFsNode> nodes, bool fromSearch, string currentDirPath)
        {
            _lvEntries.BeginUpdate();
            try
            {
                _lvEntries.Items.Clear();

                if (!fromSearch && !string.IsNullOrEmpty(currentDirPath) && currentDirPath != "/")
                {
                    var upItem = new ListViewItem("..", 2);
                    upItem.SubItems.Add("Parent Folder");
                    upItem.SubItems.Add(string.Empty);
                    upItem.SubItems.Add(string.Empty);
                    upItem.SubItems.Add(string.Empty);
                    upItem.SubItems.Add(currentDirPath);
                    upItem.SubItems.Add(string.Empty);
                    upItem.Tag = ParentSentinel;
                    _lvEntries.Items.Add(upItem);
                }

                long dirBytes = 0;
                int fileCount = 0;
                int folderCount = 0;

                foreach (var child in nodes)
                {
                    int iconIdx = GetNodeIconIndex(child);
                    var item = new ListViewItem(child.Name, iconIdx);
                    item.SubItems.Add(GetNodeTypeLabel(child));
                    item.SubItems.Add(child.IsDirectory ? string.Empty : ByteFormatter.FormatBytes(child.SizeBytes));
                    item.SubItems.Add(child.ModifiedUtc.ToString("yyyy-MM-dd HH:mm"));
                    item.SubItems.Add(child.PermissionsString);
                    item.SubItems.Add(child.FullPath);
                    item.SubItems.Add(child.InodeOrObjectId.ToString());
                    item.Tag = child;
                    _lvEntries.Items.Add(item);

                    if (child.IsDirectory) folderCount++;
                    else { fileCount++; dirBytes += child.SizeBytes; }
                }

                _lvEntries.Sort();

                if (!fromSearch)
                {
                    _lblExplorerStatus.Text = string.Format("{0}  |  {1} folder(s), {2} file(s)  ({3})",
                        currentDirPath, folderCount, fileCount, ByteFormatter.FormatBytes(dirBytes));
                }
                else
                {
                    _lblExplorerStatus.Text = string.Format("Search '{0}': {1} match(es)", _txtSearch.Text.Trim(), nodes.Count);
                }
            }
            finally
            {
                _lvEntries.EndUpdate();
            }

            _selectedFileNode = null;
            _btnCopySelectedToHost.Enabled = false;
            if (fromSearch)
            {
                _lblSelectedFile.Text = string.Format("Search results for '{0}': {1} match(es)", _txtSearch.Text.Trim(), nodes.Count);
                _txtPreview.Text = nodes.Count == 0
                    ? "No matching files or directories found."
                    : "Select a search hit to preview or copy to host, or double-click a directory to open it.";
            }
            UpdateExperimentalActionStates();
        }

        private void OnListViewColumnClick(object sender, ColumnClickEventArgs e)
        {
            if (_sortColumn == e.Column)
                _sortAscending = !_sortAscending;
            else
            {
                _sortColumn = e.Column;
                _sortAscending = true;
            }
            _lvEntries.Sort();
        }

        private class EntryListComparer : IComparer
        {
            private readonly IntegratedFileSystemExplorerControl _owner;

            public EntryListComparer(IntegratedFileSystemExplorerControl owner)
            {
                _owner = owner;
            }

            public int Compare(object x, object y)
            {
                var lx = x as ListViewItem;
                var ly = y as ListViewItem;
                if (lx == null || ly == null) return 0;

                bool xUp = Equals(lx.Tag, ParentSentinel);
                bool yUp = Equals(ly.Tag, ParentSentinel);
                if (xUp != yUp) return xUp ? -1 : 1;

                var nx = lx.Tag as VirtualFsNode;
                var ny = ly.Tag as VirtualFsNode;
                if (nx == null || ny == null) return 0;

                if (nx.IsDirectory != ny.IsDirectory)
                    return nx.IsDirectory ? -1 : 1;

                int cmp = 0;
                switch (_owner._sortColumn)
                {
                    case 1:
                        cmp = string.Compare(lx.SubItems[1].Text, ly.SubItems[1].Text, StringComparison.OrdinalIgnoreCase);
                        break;
                    case 2:
                        cmp = nx.SizeBytes.CompareTo(ny.SizeBytes);
                        break;
                    case 3:
                        cmp = nx.ModifiedUtc.CompareTo(ny.ModifiedUtc);
                        break;
                    case 4:
                        cmp = string.Compare(nx.PermissionsString, ny.PermissionsString, StringComparison.OrdinalIgnoreCase);
                        break;
                    case 5:
                        cmp = string.Compare(nx.FullPath, ny.FullPath, StringComparison.OrdinalIgnoreCase);
                        break;
                    case 6:
                        cmp = nx.InodeOrObjectId.CompareTo(ny.InodeOrObjectId);
                        break;
                    default:
                        cmp = string.Compare(nx.Name, ny.Name, StringComparison.OrdinalIgnoreCase);
                        break;
                }

                if (cmp == 0)
                    cmp = string.Compare(nx.Name, ny.Name, StringComparison.OrdinalIgnoreCase);

                return _owner._sortAscending ? cmp : -cmp;
            }
        }

        private void ExecuteSearch()
        {
            if (_volume == null) return;
            string q = _txtSearch.Text.Trim();
            if (q.Length == 0)
            {
                ClearSearch();
                return;
            }

            var matches = ReadOnlyFileSystemMountEngine.SearchVolume(_volume, q);
            _isSearchActive = true;
            _btnClearSearch.Enabled = true;
            _txtPath.Text = "Search: " + q;
            UpdateNavigationButtons();
            PopulateEntriesList(matches, true, null);
        }

        private void ClearSearch()
        {
            _txtSearch.Text = string.Empty;
            _isSearchActive = false;
            _btnClearSearch.Enabled = false;
            OnDirectorySelected();
        }

        private void OnEntrySelected()
        {
            if (_lvEntries.SelectedItems.Count == 0)
            {
                _selectedFileNode = null;
                _btnCopySelectedToHost.Enabled = false;
                UpdateExperimentalActionStates();
                return;
            }

            if (Equals(_lvEntries.SelectedItems[0].Tag, ParentSentinel))
            {
                _selectedFileNode = null;
                _btnCopySelectedToHost.Enabled = false;
                _lblSelectedFile.Text = "Parent Directory (..)";
                UpdateExperimentalActionStates();
                return;
            }

            var node = _lvEntries.SelectedItems[0].Tag as VirtualFsNode;
            if (node == null) return;

            _btnCopySelectedToHost.Enabled = !_copyInProgress;
            if (node.IsDirectory)
            {
                _selectedFileNode = null;
                _lblSelectedFile.Text = string.Format("Folder: {0} ({1} item(s))", node.FullPath, node.Children.Count);
                _txtPreview.Text = "Folder selected. Double-click to open, or click 'Copy Selected to Host...' to copy this folder and its contents to Windows.";
                UpdateExperimentalActionStates();
                return;
            }

            _selectedFileNode = node;
            _lblSelectedFile.Text = string.Format("{0} ({1})", node.FullPath, ByteFormatter.FormatBytes(node.SizeBytes));
            RenderSelectedFilePreview();
            UpdateExperimentalActionStates();
        }

        private void OnEntryDoubleClick()
        {
            if (_lvEntries.SelectedItems.Count == 0) return;
            object tag = _lvEntries.SelectedItems[0].Tag;
            if (Equals(tag, ParentSentinel))
            {
                NavigateUp();
                return;
            }

            var node = tag as VirtualFsNode;
            if (node != null && node.IsDirectory)
            {
                _isSearchActive = false;
                _btnClearSearch.Enabled = false;
                TreeNode found = FindTreeNodeByTag(_treeDirs.Nodes, node) ?? FindTreeNodeByPath(_treeDirs.Nodes, node.FullPath);
                if (found != null)
                {
                    if (found.Parent != null) found.Parent.Expand();
                    _treeDirs.SelectedNode = found;
                }
            }
        }

        private void RenderSelectedFilePreview()
        {
            if (_selectedFileNode == null || _disk == null || _partition == null) return;
            try
            {
                const int PreviewCap = 131072; // 128 KB fast preview cap (full copy streams multi-GB!)
                byte[] data = ReadOnlyFileSystemMountEngine.ReadFileBytes(_disk, _partition, _selectedFileNode, PreviewCap);
                string notice = _selectedFileNode.SizeBytes > PreviewCap
                    ? string.Format("[Showing first {0} of {1} — use 'Copy Selected to Host...' to extract full file]\r\n\r\n",
                        ByteFormatter.FormatBytes(data.Length), ByteFormatter.FormatBytes(_selectedFileNode.SizeBytes))
                    : string.Empty;

                if (_chkHexView.Checked || ContainsBinaryControlBytes(data))
                    _txtPreview.Text = notice + FormatHexDump(data);
                else
                    _txtPreview.Text = notice + Encoding.UTF8.GetString(data).Replace("\r\n", "\n").Replace("\n", "\r\n");
            }
            catch (Exception ex)
            {
                _txtPreview.Text = "Unable to read file bytes: " + ex.Message;
            }
        }

        private void ExportSelectedNodeToHost()
        {
            if (_copyInProgress || _disk == null || _partition == null || _lvEntries.SelectedItems.Count == 0) return;
            var node = _lvEntries.SelectedItems[0].Tag as VirtualFsNode;
            if (node == null) return;
            ExportNodeInteractive(node);
        }

        private void ExportCurrentDirectoryToHost()
        {
            if (_copyInProgress) return;
            var dir = GetCurrentDirectoryNode();
            if (dir == null) return;
            ExportNodeInteractive(dir);
        }

        private void ExportNodeInteractive(VirtualFsNode node)
        {
            if (_copyInProgress || node == null) return;

            string destinationPath = null;
            bool isDirExport = node.IsDirectory;

            if (!isDirExport)
            {
                using (var sfd = new SaveFileDialog { FileName = node.Name, Filter = "All files (*.*)|*.*" })
                {
                    if (sfd.ShowDialog(this) != DialogResult.OK) return;
                    destinationPath = sfd.FileName;
                }
            }
            else
            {
                using (var fbd = new FolderBrowserDialog { Description = string.Format("Select Windows destination folder to copy '{0}' (with subfolders & files):", node.FullPath) })
                {
                    if (fbd.ShowDialog(this) != DialogResult.OK) return;
                    destinationPath = fbd.SelectedPath;
                }
            }

            _copyInProgress = true;
            _copyCancelRequested = false;
            _btnCopySelectedToHost.Enabled = false;
            _btnCopyFolderToHost.Enabled = false;
            _copyProgressBar.Value = 0;
            _copyProgressBar.Visible = true;
            _btnCancelCopy.Enabled = true;
            _btnCancelCopy.Visible = true;
            _lblExplorerStatus.Text = string.Format("Starting copy of {0}...", node.Name);

            DiskInfo diskSnap = _disk;
            PartitionEntry partSnap = _partition;
            DateTime lastUiTick = DateTime.MinValue;

            var worker = new Thread(() =>
            {
                int filesCopied = 0;
                int foldersCreated = 0;
                long bytesCopied = 0;
                Exception copyEx = null;
                bool cancelled = false;

                try
                {
                    ReadOnlyFileSystemMountEngine.ExportNodeToHost(
                        diskSnap,
                        partSnap,
                        node,
                        destinationPath,
                        (done, total, currentName) =>
                        {
                            DateTime now = DateTime.UtcNow;
                            if ((now - lastUiTick).TotalMilliseconds < 100 && done < total) return;
                            lastUiTick = now;

                            if (!IsDisposed && IsHandleCreated)
                            {
                                try
                                {
                                    BeginInvoke((MethodInvoker)(() =>
                                    {
                                        if (IsDisposed) return;
                                        int pct = total > 0 ? (int)Math.Min(1000L, Math.Max(0L, (done * 1000L) / total)) : 0;
                                        _copyProgressBar.Value = pct;
                                        _lblExplorerStatus.Text = string.Format("Copying {0}... {1} / {2} ({3:F1}%)",
                                            currentName,
                                            ByteFormatter.FormatBytes(done),
                                            ByteFormatter.FormatBytes(total),
                                            pct / 10.0);
                                    }));
                                }
                                catch
                                {
                                }
                            }
                        },
                        () => _copyCancelRequested,
                        out filesCopied,
                        out foldersCreated,
                        out bytesCopied);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }
                catch (Exception ex)
                {
                    copyEx = ex;
                }

                if (!IsDisposed && IsHandleCreated)
                {
                    try
                    {
                        BeginInvoke((MethodInvoker)(() =>
                        {
                            if (IsDisposed) return;
                            _copyInProgress = false;
                            _copyProgressBar.Visible = false;
                            _btnCancelCopy.Visible = false;
                            _btnCopyFolderToHost.Enabled = (_volume != null && _volume.RootNode != null);
                            _btnCopySelectedToHost.Enabled = (_lvEntries.SelectedItems.Count > 0 && (_lvEntries.SelectedItems[0].Tag is VirtualFsNode));

                            if (cancelled)
                            {
                                _lblExplorerStatus.Text = "Copy to host cancelled.";
                                MessageBox.Show(this, "Copy to host was cancelled.", "Copy Cancelled", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            }
                            else if (copyEx != null)
                            {
                                _lblExplorerStatus.Text = "Copy error: " + copyEx.Message;
                                MessageBox.Show(this, "Copy to host failed:\n" + copyEx.Message, "Copy Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            }
                            else
                            {
                                _lblExplorerStatus.Text = string.Format("Copied {0} ({1}) to host", node.Name, ByteFormatter.FormatBytes(bytesCopied));
                                if (!isDirExport)
                                {
                                    MessageBox.Show(this,
                                        string.Format("Copied file to host:\n{0}\n({1})", destinationPath, ByteFormatter.FormatBytes(bytesCopied)),
                                        "Copy to Host Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                                }
                                else
                                {
                                    MessageBox.Show(this,
                                        string.Format("Copied directory tree '{0}' to:\n{1}\n\nFolders created: {2}\nFiles copied: {3}\nTotal data: {4}",
                                            node.FullPath, destinationPath, foldersCreated, filesCopied, ByteFormatter.FormatBytes(bytesCopied)),
                                        "Folder Copy Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                                }
                            }
                        }));
                    }
                    catch
                    {
                    }
                }
            });
            worker.IsBackground = true;
            worker.Start();
        }

        private void SetExperimentalWriteUiChecked(bool enabled)
        {
            _suppressToggleEvent = true;
            try
            {
                _chkExperimentalWrite.Checked = enabled;
                _chkExperimentalWrite.Text = enabled ? "EXPERIMENTAL WRITE: ON" : "Experimental Write: OFF";
                ThemeManager.ApplyTheme(_experimentalPanel);
            }
            finally
            {
                _suppressToggleEvent = false;
            }
        }

        private void OnExperimentalToggleChanged(object sender, EventArgs e)
        {
            if (_suppressToggleEvent || _volume == null) return;

            bool wantEnable = _chkExperimentalWrite.Checked;
            if (wantEnable)
            {
                var confirm = MessageBox.Show(this,
                    "WARNING — EXPERIMENTAL FEATURE:\n\n" +
                    "Enabling Experimental Write Mode unlocks direct file & folder writes onto this non-Microsoft partition via WinParted's overlay table (WPRWOVL1).\n\n" +
                    "Writing to foreign partitions can risk data corruption. Only proceed if you understand the risk or are using a test disk image.\n\n" +
                    "Enable Experimental Write Mode?",
                    "Enable Experimental Foreign Filesystem Writes?",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);

                if (confirm != DialogResult.Yes)
                {
                    SetExperimentalWriteUiChecked(false);
                    return;
                }
            }

            ReadOnlyFileSystemMountEngine.SetExperimentalWriteMode(_volume, wantEnable);
            SetExperimentalWriteUiChecked(wantEnable);
            UpdateBannerText();
            UpdateExperimentalActionStates();
            NotifyVolumeStateChanged();
        }

        private void UpdateExperimentalActionStates()
        {
            bool canWrite = (_volume != null && _volume.ExperimentalWriteEnabled);
            bool hasSelection = (_lvEntries.SelectedItems.Count > 0 && (_lvEntries.SelectedItems[0].Tag is VirtualFsNode));
            _btnExpWriteFile.Enabled = canWrite;
            _btnExpWriteFolder.Enabled = canWrite;
            _btnExpNewFolder.Enabled = canWrite;
            _btnExpDelete.Enabled = canWrite && hasSelection;
            ThemeManager.ApplyTheme(_experimentalPanel);
        }

        private void WriteHostFileInteractive()
        {
            if (_volume == null || !_volume.ExperimentalWriteEnabled) return;
            using (var ofd = new OpenFileDialog { Title = "Select Host File to Write to Foreign Partition", Filter = "All files (*.*)|*.*" })
            {
                if (ofd.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    VirtualFsNode targetDir = GetCurrentDirectoryNode();
                    var created = ReadOnlyFileSystemMountEngine.WriteHostFileToVolume(_disk, _partition, _volume, targetDir, ofd.FileName);
                    RefreshTreeKeepingPath(targetDir.FullPath);
                    MessageBox.Show(this, "Experimental write succeeded:\n" + created.FullPath, "Experimental Write", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    HandleWriteError(ex);
                }
            }
        }

        private void WriteHostFolderInteractive()
        {
            if (_volume == null || !_volume.ExperimentalWriteEnabled) return;
            using (var fbd = new FolderBrowserDialog { Description = "Select Host Folder to Write to Foreign Partition (Experimental)" })
            {
                if (fbd.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    VirtualFsNode targetDir = GetCurrentDirectoryNode();
                    int filesWritten;
                    int foldersWritten;
                    var created = ReadOnlyFileSystemMountEngine.WriteHostDirectoryToVolume(_disk, _partition, _volume, targetDir, fbd.SelectedPath, out filesWritten, out foldersWritten);
                    RefreshTreeKeepingPath(targetDir.FullPath);
                    MessageBox.Show(this,
                        string.Format("Experimental folder write succeeded:\n{0}\n\nFolders: {1}, Files: {2}", created.FullPath, foldersWritten, filesWritten),
                        "Experimental Write", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    HandleWriteError(ex);
                }
            }
        }

        private void CreateDirectoryInteractive()
        {
            if (_volume == null || !_volume.ExperimentalWriteEnabled) return;
            string name = PromptSimpleInput("Create Folder (Experimental RW)", "Folder name:");
            if (string.IsNullOrEmpty(name)) return;
            try
            {
                VirtualFsNode targetDir = GetCurrentDirectoryNode();
                var created = ReadOnlyFileSystemMountEngine.CreateDirectoryOnVolume(_disk, _partition, _volume, targetDir, name);
                RefreshTreeKeepingPath(targetDir.FullPath);
                _lblSelectedFile.Text = "Created folder: " + created.FullPath;
            }
            catch (Exception ex)
            {
                HandleWriteError(ex);
            }
        }

        private void DeleteSelectedInteractive()
        {
            if (_volume == null || !_volume.ExperimentalWriteEnabled || _lvEntries.SelectedItems.Count == 0) return;
            var node = _lvEntries.SelectedItems[0].Tag as VirtualFsNode;
            if (node == null || node.FullPath == "/") return;

            var dr = MessageBox.Show(this,
                string.Format("Permanently record deletion of '{0}' in Experimental Write overlay?", node.FullPath),
                "Delete Entry (Experimental RW)", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (dr != DialogResult.Yes) return;

            try
            {
                string curDirPath = GetCurrentDirectoryNode().FullPath;
                ReadOnlyFileSystemMountEngine.DeleteNodeFromVolume(_disk, _partition, _volume, node);
                RefreshTreeKeepingPath(curDirPath);
            }
            catch (Exception ex)
            {
                HandleWriteError(ex);
            }
        }

        private void RefreshTreeKeepingPath(string targetDirPath)
        {
            _isSearchActive = false;
            _btnClearSearch.Enabled = false;
            PopulateDirectoryTree();
            TreeNode tn = FindTreeNodeByPath(_treeDirs.Nodes, targetDirPath);
            if (tn != null) _treeDirs.SelectedNode = tn;
            OnDirectorySelected();
        }

        private void HandleWriteError(Exception ex)
        {
            if (ex is UnauthorizedAccessException && _disk != null && !_disk.IsVirtualImage && !Win32DiskInterop.IsAdministrator())
            {
                var dr = MessageBox.Show(this,
                    "Writing to a physical disk partition requires Administrator privileges.\n\nRestart WinParted as Administrator?",
                    "Administrator Required", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (dr == DialogResult.Yes && Win32DiskInterop.RelaunchAsAdministrator())
                    Application.Exit();
                return;
            }
            MessageBox.Show(this, "Experimental write failed:\n" + ex.Message, "Experimental Write Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private string PromptSimpleInput(string title, string prompt)
        {
            using (var dlg = new Form
            {
                Text = title,
                Size = new Size(360, 150),
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MaximizeBox = false,
                MinimizeBox = false
            })
            {
                var lbl = new Label { Left = 12, Top = 12, Width = 320, Text = prompt };
                var txt = new TextBox { Left = 12, Top = 36, Width = 320 };
                var btnOk = new Button { Text = "Create", Left = 172, Top = 72, Width = 75, DialogResult = DialogResult.OK, Tag = "DangerButton" };
                var btnCancel = new Button { Text = "Cancel", Left = 257, Top = 72, Width = 75, DialogResult = DialogResult.Cancel };
                dlg.Controls.AddRange(new Control[] { lbl, txt, btnOk, btnCancel });
                dlg.AcceptButton = btnOk;
                dlg.CancelButton = btnCancel;
                ThemeManager.ApplyTheme(dlg);
                return dlg.ShowDialog(this) == DialogResult.OK ? txt.Text.Trim() : null;
            }
        }

        private TreeNode FindTreeNodeByTag(TreeNodeCollection nodes, VirtualFsNode target)
        {
            foreach (TreeNode tn in nodes)
            {
                if (ReferenceEquals(tn.Tag, target)) return tn;
                TreeNode sub = FindTreeNodeByTag(tn.Nodes, target);
                if (sub != null) return sub;
            }
            return null;
        }

        private TreeNode FindTreeNodeByPath(TreeNodeCollection nodes, string fullPath)
        {
            foreach (TreeNode tn in nodes)
            {
                var vn = tn.Tag as VirtualFsNode;
                if (vn != null && string.Equals(vn.FullPath, fullPath, StringComparison.OrdinalIgnoreCase))
                    return tn;
                TreeNode sub = FindTreeNodeByPath(tn.Nodes, fullPath);
                if (sub != null) return sub;
            }
            return null;
        }

        private void NotifyVolumeStateChanged()
        {
            EventHandler handler = VolumeStateChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private static bool ContainsBinaryControlBytes(byte[] data)
        {
            int limit = Math.Min(data.Length, 512);
            for (int i = 0; i < limit; i++)
            {
                byte b = data[i];
                if (b == 0 || (b < 32 && b != 9 && b != 10 && b != 13))
                    return true;
            }
            return false;
        }

        private static string FormatHexDump(byte[] data)
        {
            var sb = new StringBuilder();
            int limit = Math.Min(data.Length, 16384);
            for (int i = 0; i < limit; i += 16)
            {
                sb.AppendFormat("{0:X8}  ", i);
                for (int j = 0; j < 16; j++)
                {
                    if (i + j < limit) sb.AppendFormat("{0:X2} ", data[i + j]);
                    else sb.Append("   ");
                    if (j == 7) sb.Append(" ");
                }
                sb.Append(" |");
                for (int j = 0; j < 16 && i + j < limit; j++)
                {
                    byte b = data[i + j];
                    sb.Append(b >= 32 && b < 127 ? (char)b : '.');
                }
                sb.AppendLine("|");
            }
            return sb.ToString();
        }
    }
}
