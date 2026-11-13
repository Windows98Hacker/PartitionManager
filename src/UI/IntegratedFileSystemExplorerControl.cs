using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using WinParted.Core;
using WinParted.Interop;
using WinParted.Models;

namespace WinParted.UI
{
    public class IntegratedFileSystemExplorerControl : UserControl
    {
        private DiskInfo _disk;
        private PartitionEntry _partition;
        private MountedReadOnlyVolume _volume;
        private VirtualFsNode _selectedFileNode;
        private bool _isSearchActive;
        private bool _suppressToggleEvent;

        private readonly Panel _topBar;
        private readonly Label _lblVolumeBanner;
        private readonly Button _btnMountNow;
        private readonly TextBox _txtSearch;
        private readonly Button _btnSearch;
        private readonly Button _btnClearSearch;
        private readonly Button _btnCopySelectedToHost;
        private readonly Button _btnCopyFolderToHost;

        private readonly Panel _experimentalPanel;
        private readonly CheckBox _chkExperimentalWrite;
        private readonly Button _btnExpWriteFile;
        private readonly Button _btnExpWriteFolder;
        private readonly Button _btnExpNewFolder;
        private readonly Button _btnExpDelete;

        private readonly TreeView _treeDirs;
        private readonly ListView _lvEntries;
        private readonly TextBox _txtPreview;
        private readonly CheckBox _chkHexView;
        private readonly Label _lblSelectedFile;

        public event EventHandler VolumeStateChanged;

        public IntegratedFileSystemExplorerControl()
        {
            Size = new Size(920, 520);
            Dock = DockStyle.Fill;
            Font = new Font("Segoe UI", 8.8f);

            // 1. Top Search & Host Export Bar
            _topBar = new Panel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(8, 5, 8, 5),
                Tag = "ElevatedPanel"
            };

            var row1 = new Panel { Dock = DockStyle.Top, Height = 26 };
            _btnMountNow = new Button
            {
                Dock = DockStyle.Right,
                Width = 135,
                Text = "Mount (Read-Only)",
                Tag = "PrimaryButton",
                Visible = false
            };
            _btnMountNow.Click += (s, e) => MountCurrentPartitionInteractive();

            _lblVolumeBanner = new Label
            {
                Dock = DockStyle.Fill,
                Text = "Foreign Filesystem Explorer — select a non-Microsoft partition (ext4, HFS+, APFS, Btrfs, XFS)",
                Font = new Font("Segoe UI", 8.8f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                Tag = "KeepForeColor"
            };
            row1.Controls.Add(_lblVolumeBanner);
            row1.Controls.Add(_btnMountNow);

            var row2 = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Padding = new Padding(0, 4, 0, 2)
            };

            var lblSearch = new Label
            {
                Text = "Search:",
                AutoSize = true,
                Margin = new Padding(0, 5, 4, 2)
            };
            _txtSearch = new TextBox
            {
                Width = 135,
                Margin = new Padding(0, 2, 4, 2)
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
                MinimumSize = new Size(60, 24),
                Margin = new Padding(0, 1, 4, 2)
            };
            _btnSearch.Click += (s, e) => ExecuteSearch();

            _btnClearSearch = new Button
            {
                Text = "Clear",
                AutoSize = true,
                MinimumSize = new Size(52, 24),
                Margin = new Padding(0, 1, 8, 2),
                Enabled = false
            };
            _btnClearSearch.Click += (s, e) => ClearSearch();

            _btnCopySelectedToHost = new Button
            {
                Text = "Copy to Host...",
                AutoSize = true,
                MinimumSize = new Size(112, 24),
                Margin = new Padding(0, 1, 4, 2),
                Tag = "PrimaryButton",
                Enabled = false
            };
            _btnCopySelectedToHost.Click += (s, e) => ExportSelectedNodeToHost();

            _btnCopyFolderToHost = new Button
            {
                Text = "Export Folder...",
                AutoSize = true,
                MinimumSize = new Size(112, 24),
                Margin = new Padding(0, 1, 0, 2),
                Enabled = false
            };
            _btnCopyFolderToHost.Click += (s, e) => ExportCurrentDirectoryToHost();

            row2.Controls.Add(lblSearch);
            row2.Controls.Add(_txtSearch);
            row2.Controls.Add(_btnSearch);
            row2.Controls.Add(_btnClearSearch);
            row2.Controls.Add(_btnCopySelectedToHost);
            row2.Controls.Add(_btnCopyFolderToHost);

            _topBar.Controls.Add(row2);
            _topBar.Controls.Add(row1);

            // 2. Right Side Panel — Red Experimental Write Mode
            _experimentalPanel = new Panel
            {
                Dock = DockStyle.Right,
                Width = 212,
                Padding = new Padding(8),
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
                Height = 62,
                Padding = new Padding(0, 6, 0, 4),
                Text = "EXPERIMENTAL: Writing files or folders to non-Microsoft partitions may cause data loss. Disabled by default.",
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

            // 3. Center Tree + Entries List + File Preview
            var mainSplit = new SplitContainer
            {
                Size = new Size(700, 420),
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                Panel1MinSize = 25,
                Panel2MinSize = 25
            };
            SetSplitterDistanceSafe(mainSplit, 165);

            _treeDirs = new TreeView
            {
                Dock = DockStyle.Fill,
                HideSelection = false,
                BorderStyle = BorderStyle.None
            };
            _treeDirs.AfterSelect += (s, e) => OnDirectorySelected();
            mainSplit.Panel1.Controls.Add(_treeDirs);

            var rightSplit = new SplitContainer
            {
                Size = new Size(700, 420),
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                Panel1MinSize = 25,
                Panel2MinSize = 25
            };
            SetSplitterDistanceSafe(rightSplit, 115);
            mainSplit.Panel2.Controls.Add(rightSplit);

            _lvEntries = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                BorderStyle = BorderStyle.None
            };
            _lvEntries.Columns.Add("Name", 170);
            _lvEntries.Columns.Add("Type", 78);
            _lvEntries.Columns.Add("Size", 84);
            _lvEntries.Columns.Add("Path", 180);
            _lvEntries.Columns.Add("ID", 68);
            _lvEntries.Columns.Add("Permissions", 92);
            _lvEntries.Columns.Add("Modified (UTC)", 125);
            _lvEntries.SelectedIndexChanged += (s, e) => OnEntrySelected();
            _lvEntries.DoubleClick += (s, e) => OnEntryDoubleClick();
            rightSplit.Panel1.Controls.Add(_lvEntries);

            var previewContainer = new Panel { Dock = DockStyle.Fill };
            rightSplit.Panel2.Controls.Add(previewContainer);

            var previewBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 28,
                Padding = new Padding(6, 3, 6, 3),
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
                Font = new Font("Segoe UI", 8.4f, FontStyle.Bold)
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
            Controls.Add(mainSplit);
            Controls.Add(_experimentalPanel);
            Controls.Add(_topBar);

            Load += (s, e) =>
            {
                SetSplitterDistanceSafe(mainSplit, 165);
                SetSplitterDistanceSafe(rightSplit, 115);
            };

            ThemeManager.ApplyTheme(this);
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
                Width = 190,
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
            _btnCopySelectedToHost.Enabled = false;
            _btnCopyFolderToHost.Enabled = false;
            _chkExperimentalWrite.Enabled = false;
            SetExperimentalWriteUiChecked(false);

            if (part == null || part.IsUnallocated)
            {
                _lblVolumeBanner.ForeColor = ThemeManager.TextSecondary;
                _lblVolumeBanner.Text = "Foreign Filesystem Explorer — select an ext2/3/4, Btrfs, XFS, APFS, HFS+, or HFS partition.";
                _txtPreview.Text = "No foreign filesystem partition selected.";
            }
            else
            {
                _lblVolumeBanner.ForeColor = ThemeManager.TextSecondary;
                _lblVolumeBanner.Text = string.Format("Native Windows FS ({0}) — browse directly in Windows Explorer.", FileSystemColors.GetDisplayName(part.FileSystem));
                _txtPreview.Text = string.Format("Selected partition uses {0} ({1}), which is natively supported by Windows.",
                    FileSystemColors.GetDisplayName(part.FileSystem), part.ShortDeviceName);
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
            _btnCopySelectedToHost.Enabled = false;
            _btnCopyFolderToHost.Enabled = false;
            _chkExperimentalWrite.Enabled = false;
            SetExperimentalWriteUiChecked(false);

            _lblVolumeBanner.ForeColor = ThemeManager.AccentAmber;
            _lblVolumeBanner.Text = string.Format("{0} ({1}) is not mounted yet — click 'Mount (Read-Only)' to search or copy files.",
                part.ShortDeviceName, FileSystemColors.GetDisplayName(part.FileSystem));
            _txtPreview.Text = string.IsNullOrEmpty(errorMessage)
                ? "Click 'Mount (Read-Only)' in the top-right corner of this explorer or double-click the partition to load its directory tree."
                : ("Unable to mount automatically: " + errorMessage);
            UpdateExperimentalActionStates();
        }

        private void RenderMountedVolume()
        {
            _btnMountNow.Visible = true;
            _btnMountNow.Text = "Refresh View";
            _txtSearch.Enabled = true;
            _btnSearch.Enabled = true;
            _btnClearSearch.Enabled = _isSearchActive;
            _btnCopyFolderToHost.Enabled = (_volume != null && _volume.RootNode != null);
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
            }
        }

        private TreeNode BuildDirTreeNode(VirtualFsNode dir)
        {
            var tn = new TreeNode(dir.Name) { Tag = dir };
            foreach (var child in dir.Children)
            {
                if (child.IsDirectory)
                    tn.Nodes.Add(BuildDirTreeNode(child));
            }
            return tn;
        }

        private void OnDirectorySelected()
        {
            if (_isSearchActive) return;
            var dir = GetCurrentDirectoryNode();
            if (dir == null) return;
            PopulateEntriesList(dir.Children, false);
        }

        private VirtualFsNode GetCurrentDirectoryNode()
        {
            if (_treeDirs.SelectedNode != null && _treeDirs.SelectedNode.Tag is VirtualFsNode)
                return (VirtualFsNode)_treeDirs.SelectedNode.Tag;
            return _volume != null ? _volume.RootNode : null;
        }

        private void PopulateEntriesList(IList<VirtualFsNode> nodes, bool fromSearch)
        {
            _lvEntries.BeginUpdate();
            try
            {
                _lvEntries.Items.Clear();
                foreach (var child in nodes)
                {
                    var item = new ListViewItem(child.Name);
                    item.SubItems.Add(child.IsDirectory ? "Directory" : "File");
                    item.SubItems.Add(child.IsDirectory ? "-" : ByteFormatter.FormatBytes(child.SizeBytes));
                    item.SubItems.Add(child.FullPath);
                    item.SubItems.Add(child.InodeOrObjectId.ToString());
                    item.SubItems.Add(child.PermissionsString);
                    item.SubItems.Add(child.ModifiedUtc.ToString("yyyy-MM-dd HH:mm"));
                    item.Tag = child;
                    _lvEntries.Items.Add(item);
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
            PopulateEntriesList(matches, true);
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

            var node = _lvEntries.SelectedItems[0].Tag as VirtualFsNode;
            if (node == null) return;

            _btnCopySelectedToHost.Enabled = true;
            if (node.IsDirectory)
            {
                _selectedFileNode = null;
                _lblSelectedFile.Text = string.Format("Directory: {0} ({1} item(s))", node.FullPath, node.Children.Count);
                _txtPreview.Text = "Directory selected. Double-click to open it, or click 'Copy Selected to Host...' to copy this folder and its contents to Windows.";
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
            var node = _lvEntries.SelectedItems[0].Tag as VirtualFsNode;
            if (node != null && node.IsDirectory)
            {
                _isSearchActive = false;
                _btnClearSearch.Enabled = false;
                TreeNode found = FindTreeNodeByTag(_treeDirs.Nodes, node) ?? FindTreeNodeByPath(_treeDirs.Nodes, node.FullPath);
                if (found != null)
                    _treeDirs.SelectedNode = found;
            }
        }

        private void RenderSelectedFilePreview()
        {
            if (_selectedFileNode == null || _disk == null || _partition == null) return;
            try
            {
                byte[] data = ReadOnlyFileSystemMountEngine.ReadFileBytes(_disk, _partition, _selectedFileNode);
                if (_chkHexView.Checked || ContainsBinaryControlBytes(data))
                    _txtPreview.Text = FormatHexDump(data);
                else
                    _txtPreview.Text = Encoding.UTF8.GetString(data).Replace("\r\n", "\n").Replace("\n", "\r\n");
            }
            catch (Exception ex)
            {
                _txtPreview.Text = "Unable to read file bytes: " + ex.Message;
            }
        }

        private void ExportSelectedNodeToHost()
        {
            if (_disk == null || _partition == null || _lvEntries.SelectedItems.Count == 0) return;
            var node = _lvEntries.SelectedItems[0].Tag as VirtualFsNode;
            if (node == null) return;
            ExportNodeInteractive(node);
        }

        private void ExportCurrentDirectoryToHost()
        {
            var dir = GetCurrentDirectoryNode();
            if (dir == null) return;
            ExportNodeInteractive(dir);
        }

        private void ExportNodeInteractive(VirtualFsNode node)
        {
            try
            {
                int filesCopied;
                int foldersCreated;
                long bytesCopied;

                if (!node.IsDirectory)
                {
                    using (var sfd = new SaveFileDialog { FileName = node.Name, Filter = "All files (*.*)|*.*" })
                    {
                        if (sfd.ShowDialog(this) != DialogResult.OK) return;
                        ReadOnlyFileSystemMountEngine.ExportNodeToHost(_disk, _partition, node, sfd.FileName, out filesCopied, out foldersCreated, out bytesCopied);
                        MessageBox.Show(this,
                            string.Format("Copied file to host:\n{0}\n({1})", sfd.FileName, ByteFormatter.FormatBytes(bytesCopied)),
                            "Copy to Host Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
                else
                {
                    using (var fbd = new FolderBrowserDialog { Description = string.Format("Select Windows destination folder to copy '{0}' (with subfolders & files):", node.FullPath) })
                    {
                        if (fbd.ShowDialog(this) != DialogResult.OK) return;
                        ReadOnlyFileSystemMountEngine.ExportNodeToHost(_disk, _partition, node, fbd.SelectedPath, out filesCopied, out foldersCreated, out bytesCopied);
                        MessageBox.Show(this,
                            string.Format("Copied directory tree '{0}' to:\n{1}\n\nFolders created: {2}\nFiles copied: {3}\nTotal data: {4}",
                                node.FullPath, fbd.SelectedPath, foldersCreated, filesCopied, ByteFormatter.FormatBytes(bytesCopied)),
                            "Folder Copy Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Copy to host failed:\n" + ex.Message, "Copy Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
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
            bool hasSelection = (_lvEntries.SelectedItems.Count > 0);
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
