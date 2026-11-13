using System;
using System.Drawing;
using System.Windows.Forms;
using WinParted.Core;
using WinParted.Models;

namespace WinParted.UI.Dialogs
{
    public class FileSystemBrowserDialog : Form
    {
        private readonly IntegratedFileSystemExplorerControl _explorer;

        public FileSystemBrowserDialog(DiskInfo disk, PartitionEntry partition, MountedReadOnlyVolume volume)
        {
            Text = string.Format("Foreign Filesystem Explorer — {0} ({1})",
                volume.MountPath, FileSystemColors.GetDisplayName(volume.FileSystem));
            Size = new Size(1080, 680);
            MinimumSize = new Size(860, 540);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9f);

            _explorer = new IntegratedFileSystemExplorerControl
            {
                Dock = DockStyle.Fill
            };
            _explorer.BindPartition(disk, partition, volume, false);

            var bottomBar = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 42,
                Padding = new Padding(10, 7, 10, 7),
                Tag = "ElevatedPanel"
            };
            var lblHint = new Label
            {
                Dock = DockStyle.Fill,
                Text = "Read-Only by default. Use the red side panel only for experimental write testing.",
                TextAlign = ContentAlignment.MiddleLeft
            };
            var btnClose = new Button
            {
                Dock = DockStyle.Right,
                Width = 90,
                Text = "Close",
                DialogResult = DialogResult.Cancel
            };
            bottomBar.Controls.Add(lblHint);
            bottomBar.Controls.Add(btnClose);

            Controls.Add(_explorer);
            Controls.Add(bottomBar);
            CancelButton = btnClose;

            ThemeManager.ApplyTheme(this);
        }
    }
}
