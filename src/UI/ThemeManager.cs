using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WinParted.UI
{
    /// <summary>
    /// Centralized Golden Ratio (\u03C6 = 1.61803398875) & Material Design 3 light/dark theme tokens,
    /// Fibonacci layout metrics, DWM window chrome integration, and custom WinForms renderer.
    /// Controls opt into semantic roles via <c>Control.Tag</c> (<c>PrimaryButton</c>,
    /// <c>TonalButton</c>, <c>DangerButton</c>, <c>ElevateButton</c>, <c>ExperimentalToggle</c>,
    /// <c>ExperimentalLabel</c>, <c>KeepForeColor</c>, <c>ExperimentalPanel</c>,
    /// <c>ExperimentalHeaderPanel</c>, <c>SurfacePanel</c>, <c>ElevatedPanel</c>,
    /// <c>ContainerHighPanel</c>, <c>StatusBar</c>, <c>SliderPanel</c>, <c>ConsoleColors</c>,
    /// <c>KeepButtonSize</c>).
    /// </summary>
    public static class ThemeManager
    {
        // Fundamental Golden Ratio constants (\u03C6)
        public const double Phi = 1.6180339887498949;
        public const double InvPhi = 0.6180339887498949;   // 1 / \u03C6 = \u03C6 - 1 (Major 61.8%)
        public const double MinorPhi = 0.3819660112501051; // 1 - 1 / \u03C6 = 1 / \u03C6^2 (Minor 38.2%)

        // Fibonacci pixel scale tokens
        public const int F3 = 3;
        public const int F5 = 5;
        public const int F8 = 8;
        public const int F13 = 13;
        public const int F21 = 21;
        public const int F34 = 34;
        public const int F55 = 55;
        public const int F89 = 89;
        public const int F144 = 144;
        public const int F233 = 233;
        public const int F377 = 377;
        public const int F610 = 610;
        public const int F987 = 987;
        public const int F1597 = 1597;

        public static bool IsDarkMode = true;
        public static event EventHandler ThemeChanged;

        private sealed class ButtonVisualState
        {
            public bool Attached;
            public bool Hovered;
            public bool Pressed;
        }

        private static readonly ConditionalWeakTable<Button, ButtonVisualState> s_buttonStates =
            new ConditionalWeakTable<Button, ButtonVisualState>();

        public static int GoldenWidth(int height)
        {
            return (int)Math.Round(height * Phi);
        }

        public static int GoldenHeight(int width)
        {
            return (int)Math.Round(width / Phi);
        }

        public static int GoldenMajor(int total)
        {
            if (total <= 0) return 0;
            return (int)Math.Round(total * InvPhi);
        }

        public static int GoldenMinor(int total)
        {
            if (total <= 0) return 0;
            return total - GoldenMajor(total);
        }

        public static Size GoldenWindowSize(int height)
        {
            return new Size(GoldenWidth(height), height);
        }

        /// <summary>
        /// Computes a \u03C6-proportioned button size based on Fibonacci/Golden steps while guaranteeing
        /// enough horizontal room so button labels never clip.
        /// </summary>
        public static Size GoldenButtonSize(int height, string text, Font font)
        {
            int targetH;
            if (height <= 23) targetH = F21;
            else if (height <= 30) targetH = 26;
            else if (height <= 38) targetH = F34;
            else targetH = 42;

            int phiBaseW = GoldenWidth(targetH); // 21->34, 26->42, 34->55, 42->68
            int textW = 0;
            if (!string.IsNullOrEmpty(text))
            {
                Font f = font ?? Control.DefaultFont;
                Size measured = TextRenderer.MeasureText(
                    text,
                    f,
                    new Size(int.MaxValue, targetH),
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                textW = measured.Width;
            }

            int neededW = Math.Max(phiBaseW, textW + F21);

            // Snap to harmonious Golden/Fibonacci button widths when targetH is standard 34px
            int[] steps = new int[] { 34, 42, 55, 68, 89, 110, 144, 178, 233, 288, 377 };
            int chosenW = neededW;
            for (int i = 0; i < steps.Length; i++)
            {
                if (steps[i] >= neededW)
                {
                    chosenW = steps[i];
                    break;
                }
            }
            if (chosenW < neededW)
                chosenW = (int)Math.Ceiling(neededW / 13.0) * 13;

            return new Size(chosenW, targetH);
        }

        /// <summary>
        /// Safely applies a 61.8% (major) / 38.2% (minor) Golden Ratio split to a SplitContainer.
        /// </summary>
        public static void ApplyGoldenSplitter(SplitContainer split, bool majorFirst, int minPanel1, int minPanel2)
        {
            if (split == null || split.IsDisposed) return;
            int total = split.Orientation == Orientation.Horizontal ? split.ClientSize.Height : split.ClientSize.Width;
            int available = total - split.SplitterWidth;
            if (available <= minPanel1 + minPanel2 + F5) return;

            int target = majorFirst ? GoldenMajor(available) : GoldenMinor(available);
            if (target < minPanel1) target = minPanel1;
            int maxDist = available - minPanel2;
            if (target > maxDist) target = maxDist;
            if (target <= 0) return;

            try
            {
                if (split.SplitterDistance != target)
                    split.SplitterDistance = target;
            }
            catch
            {
                // SplitContainer may reject early assignment before handle layout completes.
            }
        }

        /// <summary>
        /// Creates a smooth rounded-rectangle GraphicsPath with safe corner clamping.
        /// </summary>
        public static GraphicsPath CreateRoundedRectPath(RectangleF bounds, float radius)
        {
            var path = new GraphicsPath();
            if (bounds.Width <= 1f || bounds.Height <= 1f)
            {
                path.AddRectangle(bounds);
                return path;
            }
            float maxR = Math.Min(bounds.Width, bounds.Height) / 2f;
            float r = Math.Max(0f, Math.Min(radius, maxR));
            if (r <= 0.25f)
            {
                path.AddRectangle(bounds);
                return path;
            }
            float d = r * 2f;
            path.AddArc(bounds.X, bounds.Y, d, d, 180f, 90f);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270f, 90f);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0f, 90f);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90f, 90f);
            path.CloseFigure();
            return path;
        }

        /// <summary>
        /// Draws a Golden Rectangle subdivision + quarter-arc Fibonacci spiral motif inside <paramref name="bounds"/>.
        /// </summary>
        public static void DrawGoldenSpiralBadge(Graphics g, Rectangle bounds, Color strokeColor, Color fillAccent)
        {
            if (g == null || bounds.Width < 13 || bounds.Height < 8) return;
            SmoothingMode prevMode = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            float w = bounds.Width;
            float h = bounds.Height;
            // Fit exact 1.618:1 rectangle centered inside bounds
            float rectW = w;
            float rectH = (float)(rectW / Phi);
            if (rectH > h)
            {
                rectH = h;
                rectW = (float)(rectH * Phi);
            }
            float rx = bounds.X + (w - rectW) / 2f;
            float ry = bounds.Y + (h - rectH) / 2f;

            var outerRect = new RectangleF(rx, ry, rectW, rectH);
            using (var bgBrush = new SolidBrush(Color.FromArgb(32, fillAccent)))
            using (var boxPen = new Pen(Color.FromArgb(140, strokeColor), 1f))
            using (var spiralPen = new Pen(fillAccent, 1.5f))
            {
                g.FillRectangle(bgBrush, outerRect);
                g.DrawRectangle(boxPen, outerRect.X, outerRect.Y, outerRect.Width, outerRect.Height);

                // Iterative Golden Rectangle square subdivisions + quarter arcs
                float x = outerRect.X;
                float y = outerRect.Y;
                float cw = outerRect.Width;
                float ch = outerRect.Height;
                for (int step = 0; step < 5; step++)
                {
                    int dir = step % 4;
                    float sq = Math.Min(cw, ch);
                    if (sq < 2f) break;

                    if (dir == 0)
                    {
                        g.DrawLine(boxPen, x + sq, y, x + sq, y + ch);
                        g.DrawArc(spiralPen, x, y, sq * 2f, sq * 2f, 180f, 90f);
                        x += sq;
                        cw -= sq;
                    }
                    else if (dir == 1)
                    {
                        g.DrawLine(boxPen, x, y + sq, x + cw, y + sq);
                        g.DrawArc(spiralPen, x - sq, y, sq * 2f, sq * 2f, 270f, 90f);
                        y += sq;
                        ch -= sq;
                    }
                    else if (dir == 2)
                    {
                        float sx = x + cw - sq;
                        g.DrawLine(boxPen, sx, y, sx, y + ch);
                        g.DrawArc(spiralPen, sx - sq, y - sq, sq * 2f, sq * 2f, 0f, 90f);
                        cw -= sq;
                    }
                    else
                    {
                        float sy = y + ch - sq;
                        g.DrawLine(boxPen, x, sy, x + cw, sy);
                        g.DrawArc(spiralPen, x, sy - sq, sq * 2f, sq * 2f, 90f, 90f);
                        ch -= sq;
                    }
                }
            }

            g.SmoothingMode = prevMode;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private static void ApplyDwmWindowChrome(Form form)
        {
            if (form == null || form.IsDisposed || !form.IsHandleCreated) return;
            try
            {
                int darkMode = IsDarkMode ? 1 : 0;
                DwmSetWindowAttribute(form.Handle, 20, ref darkMode, sizeof(int));

                // COLORREF is 0x00BBGGRR
                Color caption = BackElevated;
                int captionRef = caption.R | (caption.G << 8) | (caption.B << 16);
                DwmSetWindowAttribute(form.Handle, 35, ref captionRef, sizeof(int));

                Color text = TextPrimary;
                int textRef = text.R | (text.G << 8) | (text.B << 16);
                DwmSetWindowAttribute(form.Handle, 36, ref textRef, sizeof(int));

                Color border = AccentGold;
                int borderRef = border.R | (border.G << 8) | (border.B << 16);
                DwmSetWindowAttribute(form.Handle, 34, ref borderRef, sizeof(int));
            }
            catch
            {
                // Older Windows builds without DWMWA_* caption attributes fall back safely.
            }
        }

        public static Color AccentGold
        {
            get { return IsDarkMode ? Color.FromArgb(219, 178, 72) : Color.FromArgb(176, 129, 18); }
        }

        public static Color AccentGoldSubtle
        {
            get { return IsDarkMode ? Color.FromArgb(44, 39, 26) : Color.FromArgb(246, 238, 214); }
        }

        public static Color BackPrimary
        {
            get { return IsDarkMode ? Color.FromArgb(13, 16, 23) : Color.FromArgb(250, 248, 242); }
        }

        public static Color BackSurface
        {
            get { return IsDarkMode ? Color.FromArgb(17, 21, 30) : Color.FromArgb(255, 254, 250); }
        }

        public static Color BackElevated
        {
            get { return IsDarkMode ? Color.FromArgb(21, 26, 36) : Color.FromArgb(242, 239, 230); }
        }

        public static Color BackContainerHigh
        {
            get { return IsDarkMode ? Color.FromArgb(30, 37, 51) : Color.FromArgb(231, 226, 214); }
        }

        public static Color BackAltRow
        {
            get { return IsDarkMode ? Color.FromArgb(20, 25, 35) : Color.FromArgb(246, 244, 236); }
        }

        public static Color BackInput
        {
            get { return IsDarkMode ? Color.FromArgb(27, 33, 46) : Color.FromArgb(255, 254, 250); }
        }

        public static Color BackSelection
        {
            get { return IsDarkMode ? Color.FromArgb(35, 58, 92) : Color.FromArgb(219, 231, 250); }
        }

        public static Color BackHover
        {
            get { return IsDarkMode ? Color.FromArgb(40, 48, 65) : Color.FromArgb(228, 223, 210); }
        }

        public static Color TonalButtonBack
        {
            get { return IsDarkMode ? Color.FromArgb(37, 52, 78) : Color.FromArgb(222, 232, 249); }
        }

        public static Color TonalButtonFore
        {
            get { return IsDarkMode ? Color.FromArgb(216, 231, 255) : Color.FromArgb(14, 42, 92); }
        }

        public static Color TextPrimary
        {
            get { return IsDarkMode ? Color.FromArgb(236, 238, 244) : Color.FromArgb(26, 25, 22); }
        }

        public static Color TextSecondary
        {
            get { return IsDarkMode ? Color.FromArgb(180, 188, 204) : Color.FromArgb(84, 80, 72); }
        }

        public static Color BorderColor
        {
            get { return IsDarkMode ? Color.FromArgb(56, 67, 89) : Color.FromArgb(204, 197, 182); }
        }

        public static Color AccentBlue
        {
            get { return IsDarkMode ? Color.FromArgb(56, 132, 255) : Color.FromArgb(24, 92, 204); }
        }

        public static Color AccentBlueSoft
        {
            get { return IsDarkMode ? Color.FromArgb(168, 204, 255) : Color.FromArgb(24, 92, 204); }
        }

        public static Color AccentGreen
        {
            get { return IsDarkMode ? Color.FromArgb(46, 184, 114) : Color.FromArgb(22, 128, 66); }
        }

        public static Color AccentAmber
        {
            get { return IsDarkMode ? Color.FromArgb(253, 214, 99) : Color.FromArgb(176, 96, 0); }
        }

        public static Color AccentRed
        {
            get { return IsDarkMode ? Color.FromArgb(242, 139, 130) : Color.FromArgb(179, 38, 30); }
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

        /// <summary>
        /// Flips between Golden Ratio Dark and Light palettes and notifies subscribed windows/controls.
        /// </summary>
        public static void ToggleTheme()
        {
            IsDarkMode = !IsDarkMode;
            EventHandler handler = ThemeChanged;
            if (handler != null)
                handler(null, EventArgs.Empty);
        }

        /// <summary>
        /// Recursively applies the active Golden Ratio palette, DWM chrome, and renderer to a Form or UserControl hierarchy.
        /// </summary>
        public static void ApplyTheme(Control root)
        {
            if (root == null) return;

            if (root is Form)
            {
                var form = (Form)root;
                form.BackColor = BackPrimary;
                form.ForeColor = TextPrimary;
                if (form.IsHandleCreated)
                    ApplyDwmWindowChrome(form);
                else
                    form.HandleCreated += OnFormHandleCreatedApplyChrome;
            }

            ApplyControlRecursive(root);
            root.Invalidate(true);
            root.Update();
        }

        private static void OnFormHandleCreatedApplyChrome(object sender, EventArgs e)
        {
            var form = sender as Form;
            if (form != null)
            {
                form.HandleCreated -= OnFormHandleCreatedApplyChrome;
                ApplyDwmWindowChrome(form);
            }
        }

        private static readonly DrawListViewColumnHeaderEventHandler s_lvDrawHeader = OnListViewDrawColumnHeader;
        private static readonly DrawListViewItemEventHandler s_lvDrawItem = OnListViewDrawItem;
        private static readonly DrawListViewSubItemEventHandler s_lvDrawSubItem = OnListViewDrawSubItem;

        public static void ApplyListViewHeaderTheme(ListView lv)
        {
            if (lv == null) return;
            lv.DrawColumnHeader -= s_lvDrawHeader;
            lv.DrawItem -= s_lvDrawItem;
            lv.DrawSubItem -= s_lvDrawSubItem;

            if (lv.View == View.Details)
            {
                lv.OwnerDraw = true;
                lv.DrawColumnHeader += s_lvDrawHeader;
                lv.DrawItem += s_lvDrawItem;
                lv.DrawSubItem += s_lvDrawSubItem;
            }
            else
            {
                lv.OwnerDraw = false;
            }
            lv.Invalidate();
        }

        private static void OnListViewDrawColumnHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            using (var bg = new SolidBrush(BackElevated))
                e.Graphics.FillRectangle(bg, e.Bounds);

            using (var pen = new Pen(BorderColor, 1f))
            using (var goldPen = new Pen(Color.FromArgb(90, AccentGold), 1f))
            {
                e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                e.Graphics.DrawLine(goldPen, e.Bounds.Left, e.Bounds.Top, e.Bounds.Right, e.Bounds.Top);
                e.Graphics.DrawLine(pen, e.Bounds.Right - 1, e.Bounds.Top + F5, e.Bounds.Right - 1, e.Bounds.Bottom - F5);
            }

            Font baseFont = e.Font ?? Control.DefaultFont;
            using (var headerFont = new Font(baseFont.FontFamily, 8.5f, FontStyle.Bold))
            {
                Rectangle textRect = new Rectangle(e.Bounds.X + F8, e.Bounds.Y, Math.Max(4, e.Bounds.Width - F13), e.Bounds.Height);
                TextRenderer.DrawText(
                    e.Graphics,
                    e.Header != null ? e.Header.Text : string.Empty,
                    headerFont,
                    textRect,
                    TextSecondary,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
        }

        private static void OnListViewDrawItem(object sender, DrawListViewItemEventArgs e)
        {
            e.DrawDefault = true;
        }

        private static void OnListViewDrawSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            e.DrawDefault = true;
        }

        private static void AttachGoldenButtonRenderer(Button btn)
        {
            ButtonVisualState state;
            if (!s_buttonStates.TryGetValue(btn, out state))
            {
                state = new ButtonVisualState();
                s_buttonStates.Add(btn, state);
            }
            if (state.Attached) return;
            state.Attached = true;

            btn.MouseEnter += delegate
            {
                state.Hovered = true;
                btn.Invalidate();
            };
            btn.MouseLeave += delegate
            {
                state.Hovered = false;
                state.Pressed = false;
                btn.Invalidate();
            };
            btn.MouseDown += delegate (object s, MouseEventArgs me)
            {
                if (me.Button == MouseButtons.Left)
                {
                    state.Pressed = true;
                    btn.Invalidate();
                }
            };
            btn.MouseUp += delegate
            {
                state.Pressed = false;
                btn.Invalidate();
            };
            btn.Paint += OnGoldenButtonPaint;
        }

        private static Color BlendColors(Color a, Color b, float amount)
        {
            float t = Math.Max(0f, Math.Min(1f, amount));
            int r = (int)Math.Round(a.R + (b.R - a.R) * t);
            int g = (int)Math.Round(a.G + (b.G - a.G) * t);
            int bl = (int)Math.Round(a.B + (b.B - a.B) * t);
            return Color.FromArgb(255, r, g, bl);
        }

        private static void OnGoldenButtonPaint(object sender, PaintEventArgs e)
        {
            var btn = sender as Button;
            if (btn == null || btn.Width < 4 || btn.Height < 4) return;

            ButtonVisualState state;
            bool hovered = false;
            bool pressed = false;
            if (s_buttonStates.TryGetValue(btn, out state))
            {
                hovered = state.Hovered;
                pressed = state.Pressed;
            }

            string tag = btn.Tag as string;
            bool isPrimary = string.Equals(tag, "PrimaryButton", StringComparison.OrdinalIgnoreCase);
            bool isTonal = string.Equals(tag, "TonalButton", StringComparison.OrdinalIgnoreCase);
            bool isDanger = string.Equals(tag, "DangerButton", StringComparison.OrdinalIgnoreCase);
            bool isElevate = string.Equals(tag, "ElevateButton", StringComparison.OrdinalIgnoreCase);

            Color parentBack = btn.Parent != null ? btn.Parent.BackColor : BackPrimary;
            if (parentBack == Color.Transparent) parentBack = BackPrimary;
            e.Graphics.Clear(parentBack);

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            Color fillColor = btn.BackColor;
            Color textColor = btn.ForeColor;
            Color borderColor = BorderColor;

            if (!btn.Enabled)
            {
                fillColor = BackElevated;
                textColor = TextSecondary;
                borderColor = Color.FromArgb(140, BorderColor);
            }
            else if (isPrimary)
            {
                fillColor = AccentBlue;
                if (pressed) fillColor = BlendColors(AccentBlue, Color.Black, 0.18f);
                else if (hovered) fillColor = BlendColors(AccentBlue, AccentGold, 0.18f);
                borderColor = hovered ? AccentGold : BlendColors(AccentBlue, AccentGold, 0.45f);
                textColor = Color.White;
            }
            else if (isDanger)
            {
                fillColor = Color.FromArgb(178, 34, 34);
                if (pressed) fillColor = Color.FromArgb(140, 22, 22);
                else if (hovered) fillColor = Color.FromArgb(208, 44, 44);
                borderColor = hovered ? AccentGold : Color.FromArgb(238, 108, 108);
                textColor = Color.White;
            }
            else if (isElevate)
            {
                fillColor = AccentGoldSubtle;
                if (hovered) fillColor = BlendColors(AccentGoldSubtle, AccentGold, 0.22f);
                borderColor = AccentGold;
                textColor = IsDarkMode ? Color.FromArgb(250, 232, 178) : Color.FromArgb(112, 74, 6);
            }
            else if (isTonal)
            {
                fillColor = TonalButtonBack;
                if (pressed) fillColor = BackSelection;
                else if (hovered) fillColor = BlendColors(TonalButtonBack, AccentGold, 0.14f);
                borderColor = hovered ? AccentGold : BlendColors(BorderColor, AccentGold, 0.25f);
                textColor = TonalButtonFore;
            }
            else
            {
                fillColor = BackContainerHigh;
                if (pressed) fillColor = BackSelection;
                else if (hovered) fillColor = BackHover;
                borderColor = hovered ? AccentGold : BorderColor;
                textColor = TextPrimary;
            }

            float radius = btn.Height >= 32 ? (float)F8 : (float)F5;
            var rect = new RectangleF(0.5f, 0.5f, btn.Width - 1.5f, btn.Height - 1.5f);
            using (GraphicsPath path = CreateRoundedRectPath(rect, radius))
            using (var fillBrush = new LinearGradientBrush(
                new RectangleF(0f, 0f, btn.Width, btn.Height),
                BlendColors(fillColor, Color.White, IsDarkMode ? 0.06f : 0.14f),
                BlendColors(fillColor, Color.Black, 0.06f),
                LinearGradientMode.Vertical))
            using (var borderPen = new Pen(borderColor, hovered ? 1.4f : 1f))
            {
                e.Graphics.FillPath(fillBrush, path);
                e.Graphics.DrawPath(borderPen, path);

                // Subtle specular top hairline inside the upper contour
                if (btn.Enabled && rect.Width > 16f)
                {
                    using (var glossPen = new Pen(Color.FromArgb(IsDarkMode ? 36 : 90, Color.White), 1f))
                    {
                        float inset = Math.Min(radius, rect.Width * 0.2f);
                        e.Graphics.DrawLine(glossPen, rect.X + inset, rect.Y + 1.2f, rect.Right - inset, rect.Y + 1.2f);
                    }
                }
            }

            Rectangle textRect = new Rectangle(F5, 0, Math.Max(4, btn.Width - F5 * 2), btn.Height);
            TextRenderer.DrawText(
                e.Graphics,
                btn.Text ?? string.Empty,
                btn.Font,
                textRect,
                textColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
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
                ts.Renderer = new M3ToolStripRenderer();

                ApplyToolStripItems(ts.Items);
            }
            else if (c is DataGridView)
            {
                var grid = (DataGridView)c;
                grid.EnableHeadersVisualStyles = false;
                grid.BackgroundColor = BackSurface;
                grid.GridColor = BorderColor;
                grid.ForeColor = TextPrimary;
                grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
                grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
                grid.ColumnHeadersDefaultCellStyle.BackColor = BackElevated;
                grid.ColumnHeadersDefaultCellStyle.ForeColor = TextSecondary;
                grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = BackElevated;
                grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = TextSecondary;
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
                ApplyListViewHeaderTheme(lv);
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
                cb.FlatStyle = FlatStyle.Flat;
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
                if (btn.Dock == DockStyle.None && !string.Equals(tag, "KeepButtonSize", StringComparison.OrdinalIgnoreCase))
                {
                    Size phiSize = GoldenButtonSize(btn.Height, btn.Text, btn.Font);
                    int targetW = Math.Max(btn.Width, phiSize.Width);
                    int targetH = phiSize.Height;
                    if (btn.Width != targetW || btn.Height != targetH)
                        btn.Size = new Size(targetW, targetH);
                }

                if (string.Equals(tag, "PrimaryButton", StringComparison.OrdinalIgnoreCase))
                {
                    btn.BackColor = AccentBlue;
                    btn.ForeColor = Color.White;
                    btn.FlatAppearance.BorderSize = 0;
                    btn.FlatAppearance.MouseOverBackColor = IsDarkMode ? Color.FromArgb(48, 128, 252) : Color.FromArgb(26, 102, 224);
                    btn.FlatAppearance.MouseDownBackColor = IsDarkMode ? Color.FromArgb(22, 92, 210) : Color.FromArgb(8, 70, 175);
                }
                else if (string.Equals(tag, "TonalButton", StringComparison.OrdinalIgnoreCase))
                {
                    btn.BackColor = TonalButtonBack;
                    btn.ForeColor = TonalButtonFore;
                    btn.FlatAppearance.BorderSize = 0;
                    btn.FlatAppearance.MouseOverBackColor = BackSelection;
                    btn.FlatAppearance.MouseDownBackColor = BackContainerHigh;
                }
                else if (string.Equals(tag, "DangerButton", StringComparison.OrdinalIgnoreCase))
                {
                    btn.BackColor = btn.Enabled ? Color.FromArgb(185, 28, 28) : Color.FromArgb(95, 25, 28);
                    btn.ForeColor = Color.White;
                    btn.FlatAppearance.BorderSize = 1;
                    btn.FlatAppearance.BorderColor = Color.FromArgb(248, 113, 113);
                    btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(220, 38, 38);
                }
                else if (string.Equals(tag, "ElevateButton", StringComparison.OrdinalIgnoreCase))
                {
                    btn.BackColor = AccentGoldSubtle;
                    btn.ForeColor = IsDarkMode ? Color.FromArgb(250, 232, 178) : Color.FromArgb(112, 74, 6);
                    btn.FlatAppearance.BorderSize = 1;
                    btn.FlatAppearance.BorderColor = AccentGold;
                }
                else
                {
                    btn.BackColor = BackContainerHigh;
                    btn.ForeColor = TextPrimary;
                    btn.FlatAppearance.BorderSize = 1;
                    btn.FlatAppearance.BorderColor = BorderColor;
                    btn.FlatAppearance.MouseOverBackColor = BackHover;
                    btn.FlatAppearance.MouseDownBackColor = BackSelection;
                }
                AttachGoldenButtonRenderer(btn);
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
            else if (c is Splitter)
            {
                c.BackColor = BorderColor;
                c.Invalidate();
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
                    else if (string.Equals(tag, "ContainerHighPanel", StringComparison.OrdinalIgnoreCase))
                        c.BackColor = BackContainerHigh;
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
            cms.Renderer = new M3ToolStripRenderer();
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

        private class M3ToolStripRenderer : ToolStripProfessionalRenderer
        {
            public M3ToolStripRenderer()
                : base(new M3ColorTable())
            {
                RoundedEdges = false;
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                if (e.ToolStrip is ToolStripDropDown)
                {
                    using (var pen = new Pen(AccentGold, 1f))
                        e.Graphics.DrawRectangle(pen, 0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
                    return;
                }

                using (var borderPen = new Pen(BorderColor, 1f))
                using (var goldPen = new Pen(Color.FromArgb(96, AccentGold), 1f))
                {
                    int y = e.AffectedBounds.Height - 1;
                    e.Graphics.DrawLine(borderPen, 0, y, e.AffectedBounds.Width, y);
                    e.Graphics.DrawLine(goldPen, F13, y, Math.Max(F13, e.AffectedBounds.Width - F13), y);
                }
            }

            protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
            {
                var btn = e.Item as ToolStripButton;
                bool isHighlighted = (btn != null && (btn.Selected || btn.Pressed || btn.Checked));
                if (!isHighlighted) return;

                var rect = new RectangleF(1.5f, 1.5f, e.Item.Width - 3.5f, e.Item.Height - 3.5f);
                Color fill = (btn.Pressed || btn.Checked) ? BackSelection : BackHover;
                if (string.Equals(e.Item.Tag as string, "ApplyHighlight", StringComparison.OrdinalIgnoreCase) && e.Item.Enabled)
                    fill = Color.FromArgb(60, AccentGreen);

                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath path = CreateRoundedRectPath(rect, F5))
                using (var bg = new SolidBrush(fill))
                using (var border = new Pen(btn.Selected || btn.Checked ? AccentGold : BorderColor, 1f))
                {
                    e.Graphics.FillPath(bg, path);
                    e.Graphics.DrawPath(border, path);
                }
            }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                if (!e.Item.Selected && !e.Item.Pressed)
                {
                    base.OnRenderMenuItemBackground(e);
                    return;
                }
                var rect = new RectangleF(2f, 1.5f, e.Item.Width - 5f, e.Item.Height - 3f);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath path = CreateRoundedRectPath(rect, F5))
                using (var bg = new SolidBrush(BackSelection))
                using (var border = new Pen(Color.FromArgb(170, AccentGold), 1f))
                {
                    e.Graphics.FillPath(bg, path);
                    e.Graphics.DrawPath(border, path);
                }
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                if (!e.Item.Enabled)
                    e.TextColor = TextSecondary;
                else if (string.Equals(e.Item.Tag as string, "ApplyHighlight", StringComparison.OrdinalIgnoreCase))
                    e.TextColor = AccentGreen;
                else
                    e.TextColor = TextPrimary;
                base.OnRenderItemText(e);
            }
        }

        private class M3ColorTable : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return BackElevated; } }
            public override Color ImageMarginGradientBegin { get { return BackElevated; } }
            public override Color ImageMarginGradientMiddle { get { return BackElevated; } }
            public override Color ImageMarginGradientEnd { get { return BackElevated; } }
            public override Color MenuBorder { get { return BorderColor; } }
            public override Color MenuItemBorder { get { return AccentGold; } }
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
