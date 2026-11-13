using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using WinParted.Core;
using WinParted.Models;

namespace WinParted.UI.Dialogs
{
    public class FileSystemBrowserDialog : Form
    {
        private readonly DiskInfo _disk;
        private readonly PartitionEntry _partition;
        private readonly MountedReadOnlyVolume _volume;

        private readonly TreeView _treeDirs;
        private readonly ListView _lvEntries;
        private readonly TextBox _txtPreview;
        private readonly CheckBox _chkHexView;
        private readonly Button _btnExport;
        private readonly Label _lblSelectedFile;

        private VirtualFsNode _selectedFileNode;

        public FileSystemBrowserDialog(DiskInfo disk, PartitionEntry partition, MountedReadOnlyVolume volume)
        {
            _disk = disk;
            _partition = partition;
            _volume = volume;

            Text = string.Format("Read-Only Filesystem Viewer — {0} ({1})",
                volume.MountPath, FileSystemColors.GetDisplayName(volume.FileSystem));
            Size = new Size(980, 660);
            MinimumSize = new Size(780, 520);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9f);

            var topBanner = new Panel
            {
                Dock = DockStyle.Top,
                Height = 66,
                Padding = new Padding(12, 8, 12, 8),
                Tag = "ElevatedPanel"
            };
            Controls.Add(topBanner);

            var lblBanner = new Label
            {
                Dock = DockStyle.Top,
                Height = 22,
                Text = string.Format("READ-ONLY MOUNT  |  Mount Point: {0}  |  FS: {1}  |  Label: {2}  |  UUID: {3}",
                    volume.MountPath,
                    FileSystemColors.GetDisplayName(volume.FileSystem),
                    string.IsNullOrEmpty(volume.Label) ? "(none)" : volume.Label,
                    string.IsNullOrEmpty(volume.UUID) ? "(none)" : volume.UUID),
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColor = ThemeManager.AccentGreen,
                Tag = "KeepForeColor"
            };
            var lblSub = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 22,
                Text = volume.SuperblockSummary,
                Font = new Font("Segoe UI", 8.5f)
            };
            topBanner.Controls.Add(lblBanner);
            topBanner.Controls.Add(lblSub);

            var mainSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterDistance = 260
            };
            Controls.Add(mainSplit);
            mainSplit.BringToFront();

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
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterDistance = 270
            };
            mainSplit.Panel2.Controls.Add(rightSplit);

            _lvEntries = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                BorderStyle = BorderStyle.None
            };
            _lvEntries.Columns.Add("Name", 200);
            _lvEntries.Columns.Add("Type", 90);
            _lvEntries.Columns.Add("Size", 95);
            _lvEntries.Columns.Add("Inode / ID", 85);
            _lvEntries.Columns.Add("Permissions", 105);
            _lvEntries.Columns.Add("Modified (UTC)", 140);
            _lvEntries.SelectedIndexChanged += (s, e) => OnEntrySelected();
            _lvEntries.DoubleClick += (s, e) => OnEntryDoubleClick();
            rightSplit.Panel1.Controls.Add(_lvEntries);

            var previewContainer = new Panel { Dock = DockStyle.Fill };
            rightSplit.Panel2.Controls.Add(previewContainer);

            var previewBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 38,
                Padding = new Padding(8, 5, 8, 5),
                Tag = "ElevatedPanel"
            };
            previewContainer.Controls.Add(previewBar);

            var barRight = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false
            };
            previewBar.Controls.Add(barRight);

            _chkHexView = new CheckBox
            {
                Text = "Hex View",
                AutoSize = true,
                Margin = new Padding(0, 5, 10, 0)
            };
            _chkHexView.CheckedChanged += (s, e) => RenderSelectedFilePreview();
            barRight.Controls.Add(_chkHexView);

            _btnExport = new Button
            {
                Text = "Export Copy (RO)...",
                AutoSize = true,
                MinimumSize = new Size(132, 26),
                Padding = new Padding(8, 2, 8, 2),
                Enabled = false,
                Tag = "PrimaryButton"
            };
            _btnExport.Click += (s, e) => ExportSelectedFile();
            barRight.Controls.Add(_btnExport);

            _lblSelectedFile = new Label
            {
                Dock = DockStyle.Fill,
                Text = "Select a file to inspect (Read-Only)",
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 8.8f, FontStyle.Bold)
            };
            previewBar.Controls.Add(_lblSelectedFile);

            _txtPreview = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Font = new Font("Consolas", 9.5f),
                Text = "Select a file above to read its contents safely in read-only mode."
            };
            previewContainer.Controls.Add(_txtPreview);
            _txtPreview.BringToFront();

            PopulateDirectoryTree();
            ThemeManager.ApplyTheme(this);
        }

        private void PopulateDirectoryTree()
        {
            _treeDirs.Nodes.Clear();
            if (_volume == null || _volume.RootNode == null) return;

            var rootTn = BuildDirTreeNode(_volume.RootNode);
            _treeDirs.Nodes.Add(rootTn);
            rootTn.ExpandAll();
            _treeDirs.SelectedNode = rootTn;
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
            var tn = _treeDirs.SelectedNode;
            if (tn == null) return;
            var dir = tn.Tag as VirtualFsNode;
            if (dir == null) return;

            _lvEntries.Items.Clear();
            foreach (var child in dir.Children)
            {
                var item = new ListViewItem(child.Name);
                item.SubItems.Add(child.IsDirectory ? "Directory" : "File");
                item.SubItems.Add(child.IsDirectory ? "-" : ByteFormatter.FormatBytes(child.SizeBytes));
                item.SubItems.Add(child.InodeOrObjectId.ToString());
                item.SubItems.Add(child.PermissionsString);
                item.SubItems.Add(child.ModifiedUtc.ToString("yyyy-MM-dd HH:mm"));
                item.Tag = child;
                _lvEntries.Items.Add(item);
            }
        }

        private void OnEntrySelected()
        {
            if (_lvEntries.SelectedItems.Count == 0)
            {
                _selectedFileNode = null;
                _btnExport.Enabled = false;
                return;
            }

            var node = _lvEntries.SelectedItems[0].Tag as VirtualFsNode;
            if (node == null || node.IsDirectory)
            {
                _selectedFileNode = null;
                _btnExport.Enabled = false;
                _lblSelectedFile.Text = node != null ? "Directory: " + node.FullPath : "Select a file to inspect";
                _txtPreview.Text = node != null ? "Double-click directory '" + node.Name + "' to open it." : string.Empty;
                return;
            }

            _selectedFileNode = node;
            _btnExport.Enabled = true;
            _lblSelectedFile.Text = string.Format("{0} ({1})", node.FullPath, ByteFormatter.FormatBytes(node.SizeBytes));
            RenderSelectedFilePreview();
        }

        private void OnEntryDoubleClick()
        {
            if (_lvEntries.SelectedItems.Count == 0) return;
            var node = _lvEntries.SelectedItems[0].Tag as VirtualFsNode;
            if (node != null && node.IsDirectory)
            {
                TreeNode found = FindTreeNodeByTag(_treeDirs.Nodes, node);
                if (found != null)
                    _treeDirs.SelectedNode = found;
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

        private void RenderSelectedFilePreview()
        {
            if (_selectedFileNode == null) return;
            try
            {
                byte[] data = ReadOnlyFileSystemMountEngine.ReadFileBytes(_disk, _partition, _selectedFileNode);
                if (_chkHexView.Checked || ContainsBinaryControlBytes(data))
                {
                    _txtPreview.Text = FormatHexDump(data);
                }
                else
                {
                    _txtPreview.Text = Encoding.UTF8.GetString(data).Replace("\r\n", "\n").Replace("\n", "\r\n");
                }
            }
            catch (Exception ex)
            {
                _txtPreview.Text = "Unable to read file bytes (Read-Only): " + ex.Message;
            }
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

        private void ExportSelectedFile()
        {
            if (_selectedFileNode == null || _selectedFileNode.IsDirectory) return;
            using (var sfd = new SaveFileDialog { FileName = _selectedFileNode.Name, Filter = "All files (*.*)|*.*" })
            {
                if (sfd.ShowDialog(this) == DialogResult.OK)
                {
                    byte[] data = ReadOnlyFileSystemMountEngine.ReadFileBytes(_disk, _partition, _selectedFileNode);
                    File.WriteAllBytes(sfd.FileName, data);
                    MessageBox.Show(this, "Saved read-only copy to:\n" + sfd.FileName, "Export Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
        }
    }
}
