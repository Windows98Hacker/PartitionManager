using System;
using System.Drawing;
using System.Windows.Forms;

namespace WinParted.UI
{
    public static class ThemeManager
    {
        public static bool IsDarkMode = true;
        public static event EventHandler ThemeChanged;

        public static Color BackPrimary
        {
            get { return IsDarkMode ? Color.FromArgb(24, 27, 34) : Color.FromArgb(248, 250, 252); }
        }

        public static Color BackSurface
        {
            get { return IsDarkMode ? Color.FromArgb(31, 35, 44) : Color.White; }
        }

        public static Color BackElevated
        {
            get { return IsDarkMode ? Color.FromArgb(38, 43, 54) : Color.FromArgb(241, 245, 249); }
        }

        public static Color BackAltRow
        {
            get { return IsDarkMode ? Color.FromArgb(28, 33, 43) : Color.FromArgb(248, 250, 252); }
        }

        public static Color BackInput
        {
            get { return IsDarkMode ? Color.FromArgb(42, 48, 60) : Color.White; }
        }

        public static Color BackSelection
        {
            get { return IsDarkMode ? Color.FromArgb(49, 78, 124) : Color.FromArgb(219, 234, 254); }
        }

        public static Color TextPrimary
        {
            get { return IsDarkMode ? Color.FromArgb(226, 232, 240) : Color.FromArgb(15, 23, 42); }
        }

        public static Color TextSecondary
        {
            get { return IsDarkMode ? Color.FromArgb(148, 163, 184) : Color.FromArgb(71, 85, 105); }
        }

        public static Color BorderColor
        {
            get { return IsDarkMode ? Color.FromArgb(56, 65, 82) : Color.FromArgb(203, 213, 225); }
        }

        public static Color AccentBlue
        {
            get { return IsDarkMode ? Color.FromArgb(59, 130, 246) : Color.FromArgb(37, 99, 235); }
        }

        public static Color AccentGreen
        {
            get { return IsDarkMode ? Color.FromArgb(74, 222, 128) : Color.FromArgb(22, 163, 74); }
        }

        public static Color AccentAmber
        {
            get { return IsDarkMode ? Color.FromArgb(251, 191, 36) : Color.FromArgb(180, 83, 9); }
        }

        public static Color AccentRed
        {
            get { return IsDarkMode ? Color.FromArgb(239, 68, 68) : Color.FromArgb(220, 38, 38); }
        }

        public static Color ExperimentalSurface
        {
            get { return IsDarkMode ? Color.FromArgb(56, 14, 18) : Color.FromArgb(254, 226, 226); }
        }

        public static Color ExperimentalHeader
        {
            get { return IsDarkMode ? Color.FromArgb(127, 29, 29) : Color.FromArgb(185, 28, 28); }
        }

        public static Color ExperimentalText
        {
            get { return IsDarkMode ? Color.FromArgb(254, 202, 202) : Color.FromArgb(153, 27, 27); }
        }

        public static void ToggleTheme()
        {
            IsDarkMode = !IsDarkMode;
            EventHandler handler = ThemeChanged;
            if (handler != null)
                handler(null, EventArgs.Empty);
        }

        public static void ApplyTheme(Control root)
        {
            if (root == null) return;

            if (root is Form)
            {
                root.BackColor = BackPrimary;
                root.ForeColor = TextPrimary;
            }

            ApplyControlRecursive(root);
        }

        private static void ApplyControlRecursive(Control c)
        {
            if (c == null) return;

            string tag = c.Tag as string;
            bool preserveConsole = string.Equals(tag, "ConsoleColors", StringComparison.OrdinalIgnoreCase);

            if (c is MenuStrip || c is ToolStrip)
            {
                var ts = (ToolStrip)c;
                ts.BackColor = c is MenuStrip ? BackPrimary : BackElevated;
                ts.ForeColor = TextPrimary;
                if (IsDarkMode)
                    ts.Renderer = new DarkToolStripRenderer();
                else
                    ts.RenderMode = ToolStripRenderMode.System;

                ApplyToolStripItems(ts.Items);
            }
            else if (c is DataGridView)
            {
                var grid = (DataGridView)c;
                grid.EnableHeadersVisualStyles = false;
                grid.BackgroundColor = BackSurface;
                grid.GridColor = BorderColor;
                grid.ForeColor = TextPrimary;
                grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
                grid.ColumnHeadersDefaultCellStyle.BackColor = BackElevated;
                grid.ColumnHeadersDefaultCellStyle.ForeColor = TextPrimary;
                grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = BackElevated;
                grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = TextPrimary;
                grid.DefaultCellStyle.BackColor = BackSurface;
                grid.DefaultCellStyle.ForeColor = TextPrimary;
                grid.DefaultCellStyle.SelectionBackColor = BackSelection;
                grid.DefaultCellStyle.SelectionForeColor = IsDarkMode ? Color.White : TextPrimary;
                grid.AlternatingRowsDefaultCellStyle.BackColor = BackAltRow;
                grid.AlternatingRowsDefaultCellStyle.ForeColor = TextPrimary;
                grid.AlternatingRowsDefaultCellStyle.SelectionBackColor = BackSelection;
                grid.AlternatingRowsDefaultCellStyle.SelectionForeColor = IsDarkMode ? Color.White : TextPrimary;
            }
            else if (c is ListView)
            {
                var lv = (ListView)c;
                lv.BackColor = BackSurface;
                lv.ForeColor = TextPrimary;
            }
            else if (c is TreeView)
            {
                var tv = (TreeView)c;
                tv.BackColor = BackSurface;
                tv.ForeColor = TextPrimary;
                tv.LineColor = BorderColor;
            }
            else if (c is TextBox)
            {
                if (!preserveConsole)
                {
                    var tb = (TextBox)c;
                    tb.BackColor = tb.ReadOnly ? BackSurface : BackInput;
                    tb.ForeColor = TextPrimary;
                    tb.BorderStyle = BorderStyle.FixedSingle;
                }
            }
            else if (c is ComboBox)
            {
                var cb = (ComboBox)c;
                cb.BackColor = BackInput;
                cb.ForeColor = TextPrimary;
                cb.FlatStyle = IsDarkMode ? FlatStyle.Flat : FlatStyle.Standard;
            }
            else if (c is NumericUpDown)
            {
                var nud = (NumericUpDown)c;
                nud.BackColor = BackInput;
                nud.ForeColor = TextPrimary;
            }
            else if (c is Button)
            {
                var btn = (Button)c;
                btn.FlatStyle = FlatStyle.Flat;
                btn.FlatAppearance.BorderColor = BorderColor;
                btn.FlatAppearance.BorderSize = 1;
                if (string.Equals(tag, "PrimaryButton", StringComparison.OrdinalIgnoreCase))
                {
                    btn.BackColor = AccentBlue;
                    btn.ForeColor = Color.White;
                }
                else if (string.Equals(tag, "DangerButton", StringComparison.OrdinalIgnoreCase))
                {
                    btn.BackColor = btn.Enabled ? Color.FromArgb(185, 28, 28) : Color.FromArgb(95, 25, 28);
                    btn.ForeColor = Color.White;
                    btn.FlatAppearance.BorderColor = Color.FromArgb(248, 113, 113);
                }
                else if (string.Equals(tag, "ElevateButton", StringComparison.OrdinalIgnoreCase))
                {
                    btn.BackColor = IsDarkMode ? Color.FromArgb(120, 53, 15) : Color.FromArgb(254, 243, 199);
                    btn.ForeColor = IsDarkMode ? Color.FromArgb(254, 243, 199) : Color.FromArgb(146, 64, 14);
                }
                else
                {
                    btn.BackColor = BackElevated;
                    btn.ForeColor = TextPrimary;
                }
            }
            else if (c is CheckBox && string.Equals(tag, "ExperimentalToggle", StringComparison.OrdinalIgnoreCase))
            {
                var cb = (CheckBox)c;
                cb.FlatStyle = FlatStyle.Flat;
                cb.BackColor = cb.Checked ? Color.FromArgb(220, 38, 38) : Color.FromArgb(127, 29, 29);
                cb.ForeColor = Color.White;
                cb.FlatAppearance.BorderColor = Color.FromArgb(252, 165, 165);
                cb.FlatAppearance.BorderSize = 2;
                cb.FlatAppearance.CheckedBackColor = Color.FromArgb(220, 38, 38);
            }
            else if (c is CheckBox || c is RadioButton)
            {
                c.ForeColor = TextPrimary;
            }
            else if (c is Label)
            {
                if (string.Equals(tag, "ExperimentalLabel", StringComparison.OrdinalIgnoreCase))
                    c.ForeColor = ExperimentalText;
                else if (!string.Equals(tag, "KeepForeColor", StringComparison.OrdinalIgnoreCase))
                    c.ForeColor = TextPrimary;
            }
            else if (c is SplitContainer)
            {
                var sc = (SplitContainer)c;
                sc.BackColor = BorderColor;
                sc.Panel1.BackColor = BackPrimary;
                sc.Panel2.BackColor = BackPrimary;
            }
            else if (c is PartitionRibbonControl)
            {
                c.BackColor = BackPrimary;
                c.Invalidate();
            }
            else if (c is TableLayoutPanel || c is FlowLayoutPanel)
            {
                c.ForeColor = TextPrimary;
            }
            else if (c is Panel)
            {
                if (string.Equals(tag, "ExperimentalPanel", StringComparison.OrdinalIgnoreCase))
                {
                    c.BackColor = ExperimentalSurface;
                    c.ForeColor = ExperimentalText;
                }
                else if (string.Equals(tag, "ExperimentalHeaderPanel", StringComparison.OrdinalIgnoreCase))
                {
                    c.BackColor = ExperimentalHeader;
                    c.ForeColor = Color.White;
                }
                else
                {
                    if (string.Equals(tag, "SurfacePanel", StringComparison.OrdinalIgnoreCase))
                        c.BackColor = BackSurface;
                    else if (string.Equals(tag, "ElevatedPanel", StringComparison.OrdinalIgnoreCase))
                        c.BackColor = BackElevated;
                    else if (string.Equals(tag, "StatusBar", StringComparison.OrdinalIgnoreCase))
                        c.BackColor = BackElevated;
                    else if (!string.Equals(tag, "SliderPanel", StringComparison.OrdinalIgnoreCase))
                        c.BackColor = BackPrimary;
                    c.ForeColor = TextPrimary;
                }
                c.Invalidate();
            }

            if (c.ContextMenuStrip != null)
                ApplyContextMenu(c.ContextMenuStrip);

            foreach (Control child in c.Controls)
                ApplyControlRecursive(child);
        }

        public static void ApplyContextMenu(ContextMenuStrip cms)
        {
            if (cms == null) return;
            cms.BackColor = BackElevated;
            cms.ForeColor = TextPrimary;
            if (IsDarkMode)
                cms.Renderer = new DarkToolStripRenderer();
            else
                cms.RenderMode = ToolStripRenderMode.System;
            ApplyToolStripItems(cms.Items);
        }

        private static void ApplyToolStripItems(ToolStripItemCollection items)
        {
            if (items == null) return;
            foreach (ToolStripItem item in items)
            {
                string itemTag = item.Tag as string;
                if (!string.Equals(itemTag, "KeepForeColor", StringComparison.OrdinalIgnoreCase))
                    item.ForeColor = TextPrimary;

                var dd = item as ToolStripDropDownItem;
                if (dd != null && dd.HasDropDownItems)
                {
                    dd.DropDown.BackColor = BackElevated;
                    dd.DropDown.ForeColor = TextPrimary;
                    ApplyToolStripItems(dd.DropDownItems);
                }
            }
        }

        private class DarkToolStripRenderer : ToolStripProfessionalRenderer
        {
            public DarkToolStripRenderer()
                : base(new DarkColorTable())
            {
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                if (!e.Item.Enabled)
                    e.TextColor = Color.FromArgb(100, 116, 139);
                else if (string.Equals(e.Item.Tag as string, "ApplyHighlight", StringComparison.OrdinalIgnoreCase))
                    e.TextColor = AccentGreen;
                else
                    e.TextColor = TextPrimary;
                base.OnRenderItemText(e);
            }
        }

        private class DarkColorTable : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return BackElevated; } }
            public override Color ImageMarginGradientBegin { get { return BackElevated; } }
            public override Color ImageMarginGradientMiddle { get { return BackElevated; } }
            public override Color ImageMarginGradientEnd { get { return BackElevated; } }
            public override Color MenuBorder { get { return BorderColor; } }
            public override Color MenuItemBorder { get { return AccentBlue; } }
            public override Color MenuItemSelected { get { return BackSelection; } }
            public override Color MenuStripGradientBegin { get { return BackPrimary; } }
            public override Color MenuStripGradientEnd { get { return BackPrimary; } }
            public override Color MenuItemSelectedGradientBegin { get { return BackSelection; } }
            public override Color MenuItemSelectedGradientEnd { get { return BackSelection; } }
            public override Color MenuItemPressedGradientBegin { get { return BackSurface; } }
            public override Color MenuItemPressedGradientEnd { get { return BackSurface; } }
            public override Color ToolStripBorder { get { return BorderColor; } }
            public override Color ToolStripGradientBegin { get { return BackElevated; } }
            public override Color ToolStripGradientMiddle { get { return BackElevated; } }
            public override Color ToolStripGradientEnd { get { return BackElevated; } }
            public override Color ButtonSelectedHighlight { get { return BackSelection; } }
            public override Color ButtonSelectedGradientBegin { get { return BackSelection; } }
            public override Color ButtonSelectedGradientEnd { get { return BackSelection; } }
            public override Color ButtonPressedGradientBegin { get { return BackSurface; } }
            public override Color ButtonPressedGradientEnd { get { return BackSurface; } }
            public override Color SeparatorDark { get { return BorderColor; } }
            public override Color SeparatorLight { get { return BackSurface; } }
            public override Color CheckBackground { get { return BackSelection; } }
            public override Color CheckSelectedBackground { get { return AccentBlue; } }
        }
    }
}
