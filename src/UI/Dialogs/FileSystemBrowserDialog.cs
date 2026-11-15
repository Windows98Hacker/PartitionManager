using System;
using System.Drawing;
using System.Windows.Forms;
using WinParted.Core;
using WinParted.Models;

namespace WinParted.UI.Dialogs
{
    /// <summary>
    /// Modal full-window host wrapping <see cref="IntegratedFileSystemExplorerControl"/> for dedicated
    /// browsing of a mounted foreign or developer partition.
    /// </summary>
    public class FileSystemBrowserDialog : Form
    {
        private readonly IntegratedFileSystemExplorerControl _explorer;

        public FileSystemBrowserDialog(DiskInfo disk, PartitionEntry partition, MountedReadOnlyVolume volume)
        {
            Text = string.Format("Foreign Filesystem Explorer — {0} ({1})",
                volume.MountPath, FileSystemColors.GetDisplayName(volume.FileSystem));
            Size = new Size(1240, 760);
            MinimumSize = new Size(980, 600);
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
                Height = 54,
                Padding = new Padding(12, 8, 12, 8),
                Tag = "ElevatedPanel"
            };
            var lblHint = new Label
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 8.8f),
                Text = "Read-Only mount by default. Files stream directly in 4 MiB chunks; use the red side panel only for experimental write testing.",
                TextAlign = ContentAlignment.MiddleLeft
            };
            var spacer = new Panel
            {
                Dock = DockStyle.Right,
                Width = 12,
                Tag = "ElevatedPanel"
            };
            var btnClose = new Button
            {
                Dock = DockStyle.Right,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(100, 32),
                Padding = new Padding(12, 0, 12, 0),
                Text = "Close",
                Tag = "TonalButton",
                DialogResult = DialogResult.Cancel
            };
            bottomBar.Controls.Add(lblHint);
            bottomBar.Controls.Add(spacer);
            bottomBar.Controls.Add(btnClose);

            Controls.Add(_explorer);
            Controls.Add(bottomBar);
            CancelButton = btnClose;

            EventHandler onThemeChanged = (s, e) =>
            {
                if (!IsDisposed)
                    ThemeManager.ApplyTheme(this);
            };
            ThemeManager.ThemeChanged += onThemeChanged;
            FormClosed += (s, e) => ThemeManager.ThemeChanged -= onThemeChanged;

            ThemeManager.ApplyTheme(this);
        }
    }
}
