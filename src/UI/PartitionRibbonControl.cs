using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using WinParted.Models;

namespace WinParted.UI
{
    /// <summary>
    /// GParted-style graphical partition layout bar supporting Smart Scale (<c>bytes^0.30</c>)
    /// or linear proportional slice widths, unallocated hatched regions, mount badges, and selection state.
    /// </summary>
    public class PartitionRibbonControl : UserControl
    {
        private List<PartitionEntry> _layout = new List<PartitionEntry>();
        private PartitionEntry _selectedPartition;
        private PartitionEntry _hoveredPartition;
        private readonly List<RectangleF> _sliceRects = new List<RectangleF>();
        private bool _smartScale = true;
        private readonly ToolTip _toolTip = new ToolTip();

        public event EventHandler<PartitionEntry> SelectedPartitionChanged;
        public event EventHandler<PartitionEntry> PartitionDoubleClicked;
        public event Action<PartitionEntry, Point> PartitionContextMenuRequested;

        public bool SmartScale
        {
            get { return _smartScale; }
            set { _smartScale = value; Invalidate(); }
        }

        public PartitionEntry SelectedPartition { get { return _selectedPartition; } }

        public PartitionRibbonControl()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            MinimumSize = new Size(300, 136);
            Height = 142;
            BackColor = ThemeManager.BackPrimary;
            _toolTip.AutoPopDelay = 8000;
            _toolTip.InitialDelay = 300;
        }

        public void SetLayout(List<PartitionEntry> layout, PartitionEntry selected = null)
        {
            _layout = layout ?? new List<PartitionEntry>();
            _selectedPartition = null;
            _hoveredPartition = null;
            if (selected != null)
            {
                _selectedPartition = _layout.FirstOrDefault(p =>
                    p.DiskNumber == selected.DiskNumber &&
                    p.StartingOffset == selected.StartingOffset &&
                    p.PartitionLength == selected.PartitionLength);
            }
            if (_selectedPartition == null && _layout.Count > 0)
            {
                _selectedPartition = _layout.FirstOrDefault(p => !p.IsUnallocated) ?? _layout[0];
            }
            Invalidate();
        }

        public void SelectPartition(PartitionEntry entry)
        {
            _selectedPartition = entry;
            Invalidate();
            if (SelectedPartitionChanged != null) SelectedPartitionChanged(this, _selectedPartition);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            _sliceRects.Clear();

            Rectangle outer = new Rectangle(8, 8, Width - 16, Math.Max(44, Height - 36));
            if (outer.Width <= 20 || outer.Height <= 20) return;

            using (var bgBrush = new SolidBrush(ThemeManager.BackSurface))
            using (var bgPen = new Pen(ThemeManager.BorderColor, 1))
            {
                g.FillRectangle(bgBrush, outer);
                g.DrawRectangle(bgPen, outer);
            }

            if (_layout.Count == 0)
            {
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                using (var br = new SolidBrush(ThemeManager.TextSecondary))
                    g.DrawString("No partitions or disk selected", Font, br, outer, sf);
                return;
            }

            double[] weights = new double[_layout.Count];
            double totalWeight = 0;
            for (int i = 0; i < _layout.Count; i++)
            {
                double bytes = Math.Max(1.0, _layout[i].PartitionLength);
                double w = _smartScale ? Math.Pow(bytes, 0.30) : bytes;
                weights[i] = w;
                totalWeight += w;
            }

            float innerPad = 6f;
            float availWidth = outer.Width - innerPad * 2f;
            float minSliceWidth = Math.Min(96f, availWidth / Math.Max(1, _layout.Count));

            float[] widths = new float[_layout.Count];
            float sumWidths = 0;
            for (int i = 0; i < _layout.Count; i++)
            {
                widths[i] = (float)(availWidth * (weights[i] / totalWeight));
                if (widths[i] < minSliceWidth) widths[i] = minSliceWidth;
                sumWidths += widths[i];
            }

            if (sumWidths > availWidth && sumWidths > 0)
            {
                float ratio = availWidth / sumWidths;
                for (int i = 0; i < _layout.Count; i++)
                    widths[i] *= ratio;
            }

            float curX = outer.Left + innerPad;
            float sliceTop = outer.Top + innerPad;
            float sliceH = outer.Height - innerPad * 2f;

            for (int i = 0; i < _layout.Count; i++)
            {
                PartitionEntry part = _layout[i];
                float w = (i == _layout.Count - 1) ? Math.Max(10f, (outer.Right - innerPad) - curX) : widths[i];
                RectangleF rect = new RectangleF(curX + 1.5f, sliceTop, Math.Max(6f, w - 3f), sliceH);
                _sliceRects.Add(rect);

                bool isSelected = _selectedPartition != null &&
                                  part.StartingOffset == _selectedPartition.StartingOffset &&
                                  part.PartitionLength == _selectedPartition.PartitionLength;
                bool isHovered = _hoveredPartition != null &&
                                 part.StartingOffset == _hoveredPartition.StartingOffset &&
                                 part.PartitionLength == _hoveredPartition.PartitionLength;

                DrawPartitionSlice(g, rect, part, isSelected, isHovered);
                curX += w;
            }

            DrawLegendStrip(g, new Rectangle(outer.Left, outer.Bottom + 4, outer.Width, Math.Max(16, Height - outer.Bottom - 6)));
        }

        private void DrawLegendStrip(Graphics g, Rectangle bounds)
        {
            if (bounds.Height < 14 || bounds.Width < 60 || _layout.Count == 0) return;

            var seen = new List<FileSystemType>();
            for (int i = 0; i < _layout.Count; i++)
            {
                FileSystemType fs = _layout[i].FileSystem;
                if (!seen.Contains(fs))
                    seen.Add(fs);
            }

            using (var legendFont = new Font("Segoe UI", 8.2f, FontStyle.Regular))
            using (var textBr = new SolidBrush(ThemeManager.TextSecondary))
            using (var borderPen = new Pen(ThemeManager.BorderColor, 1f))
            {
                int x = bounds.Left + 2;
                int yMid = bounds.Top + bounds.Height / 2;
                int swatchSize = 10;

                foreach (FileSystemType fs in seen)
                {
                    string label = FileSystemColors.GetDisplayName(fs);
                    Size textSize = TextRenderer.MeasureText(label, legendFont);
                    int itemWidth = swatchSize + 5 + textSize.Width + 14;
                    if (x + swatchSize + 5 + textSize.Width > bounds.Right)
                        break;

                    Rectangle swatch = new Rectangle(x, yMid - swatchSize / 2, swatchSize, swatchSize);
                    using (var fill = new SolidBrush(FileSystemColors.GetBorderColor(fs)))
                        g.FillRectangle(fill, swatch);
                    g.DrawRectangle(borderPen, swatch);

                    Rectangle textRect = new Rectangle(x + swatchSize + 5, bounds.Top, textSize.Width + 2, bounds.Height);
                    TextRenderer.DrawText(g, label, legendFont, textRect, ThemeManager.TextSecondary,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);

                    x += itemWidth;
                }
            }
        }

        private void DrawPartitionSlice(Graphics g, RectangleF rect, PartitionEntry part, bool isSelected, bool isHovered)
        {
            Color borderColor = FileSystemColors.GetBorderColor(part.FileSystem);
            Color fillColor = FileSystemColors.GetFillColor(part.FileSystem);
            Color usedColor = FileSystemColors.GetUsedColor(part.FileSystem);

            using (GraphicsPath path = CreateRoundedRect(rect, 5f))
            {
                if (part.IsUnallocated)
                {
                    Color hatchFore = ThemeManager.IsDarkMode ? Color.FromArgb(62, 71, 89) : Color.FromArgb(203, 213, 225);
                    Color hatchBack = ThemeManager.IsDarkMode ? Color.FromArgb(36, 41, 52) : Color.FromArgb(241, 245, 249);
                    using (var hatch = new HatchBrush(HatchStyle.BackwardDiagonal, hatchFore, hatchBack))
                        g.FillPath(hatch, path);
                }
                else
                {
                    using (var fillBr = new SolidBrush(fillColor))
                        g.FillPath(fillBr, path);

                    // Used-space fill inside block (classic GParted style)
                    double usedFrac = part.UsedFraction;
                    if (usedFrac > 0.005)
                    {
                        float usedW = (float)(rect.Width * usedFrac);
                        Region prevClip = g.Clip;
                        g.SetClip(path);
                        using (var usedBr = new SolidBrush(usedColor))
                            g.FillRectangle(usedBr, rect.X, rect.Y, usedW, rect.Height);
                        g.Clip = prevClip;
                    }
                }

                if (isHovered && !isSelected)
                {
                    Color hoverColor = ThemeManager.IsDarkMode ? Color.FromArgb(28, 255, 255, 255) : Color.FromArgb(24, 15, 23, 42);
                    using (var hoverBr = new SolidBrush(hoverColor))
                        g.FillPath(hoverBr, path);
                }

                float penWidth = isSelected ? 2.8f : 1.6f;
                Color selectedPenCol = ThemeManager.IsDarkMode ? Color.FromArgb(248, 250, 252) : Color.FromArgb(15, 23, 42);
                Color penCol = isSelected ? selectedPenCol : borderColor;
                using (var pen = new Pen(penCol, penWidth))
                {
                    if (part.IsUnallocated)
                        pen.DashStyle = DashStyle.Dash;
                    g.DrawPath(pen, path);
                }

                if (isSelected)
                {
                    RectangleF innerGlow = RectangleF.Inflate(rect, -2.5f, -2.5f);
                    if (innerGlow.Width > 4 && innerGlow.Height > 4)
                    {
                        Color glowCol = ThemeManager.IsDarkMode ? ThemeManager.AccentBlue : Color.White;
                        using (var innerPen = new Pen(glowCol, 1.5f))
                        using (var innerPath = CreateRoundedRect(innerGlow, 3f))
                            g.DrawPath(innerPen, innerPath);
                    }
                }

                // Draw labels if wide enough using dynamic font height measurement
                if (rect.Width >= 52)
                {
                    using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                    using (var titleFont = new Font("Segoe UI", 9f, FontStyle.Bold))
                    using (var subFont = new Font("Segoe UI", 8.2f, FontStyle.Regular))
                    using (var darkBr = new SolidBrush(ThemeManager.TextPrimary))
                    using (var mutedBr = new SolidBrush(ThemeManager.TextSecondary))
                    {
                        float maxTextAreaW = rect.Width - 10f;
                        string botText = ByteFormatter.FormatBytes(part.PartitionLength);
                        float h1 = Math.Max(18f, titleFont.GetHeight(g) + 2f);
                        float h2 = Math.Max(16f, subFont.GetHeight(g) + 2f);
                        float h3 = h2;

                        if (part.IsUnallocated)
                        {
                            string topText = "Unallocated";
                            if (g.MeasureString(topText, titleFont).Width > maxTextAreaW)
                                topText = "Free";

                            float totalH = h1 + h3 + 4f;
                            float startY = rect.Y + Math.Max(4f, (rect.Height - totalH) / 2f);
                            RectangleF r1 = new RectangleF(rect.X + 5f, startY, maxTextAreaW, h1);
                            RectangleF r3 = new RectangleF(rect.X + 5f, startY + h1 + 3f, maxTextAreaW, h3);
                            g.DrawString(topText, titleFont, darkBr, r1, sf);
                            g.DrawString(botText, subFont, mutedBr, r3, sf);
                        }
                        else
                        {
                            string topText = !string.IsNullOrEmpty(part.DriveLetter)
                                ? (string.IsNullOrEmpty(part.Label) ? part.DriveLetter : part.DriveLetter + " " + part.Label)
                                : (!string.IsNullOrEmpty(part.Label) ? part.Label : "Part #" + part.PartitionNumber);
                            topText = topText.Trim();

                            string midText = FileSystemColors.GetDisplayName(part.FileSystem);
                            if (g.MeasureString(midText, subFont).Width > maxTextAreaW)
                            {
                                int parenIdx = midText.IndexOf(" (");
                                if (parenIdx > 0)
                                    midText = midText.Substring(0, parenIdx);
                            }

                            bool showMid = rect.Width >= 64 && rect.Height >= (h1 + h2 + h3 + 12f);
                            float totalH = showMid ? (h1 + h2 + h3 + 4f) : (h1 + h3 + 3f);
                            float startY = rect.Y + Math.Max(4f, (rect.Height - totalH) / 2f);

                            float w1 = g.MeasureString(topText, titleFont).Width;
                            float w2 = showMid ? g.MeasureString(midText, subFont).Width : 0f;
                            float w3 = g.MeasureString(botText, subFont).Width;
                            float widest = Math.Min(maxTextAreaW, Math.Max(w1, Math.Max(w2, w3)) + 12f);
                            float pillH = Math.Min(rect.Height - 8f, totalH + 6f);
                            RectangleF pillRect = new RectangleF(rect.X + (rect.Width - widest) / 2f, Math.Max(rect.Y + 4f, startY - 3f), widest, pillH);
                            if (pillRect.Width > 18f && pillRect.Height > 14f)
                            {
                                using (var pillPath = CreateRoundedRect(pillRect, 4f))
                                using (var pillBr = new SolidBrush(Color.FromArgb(ThemeManager.IsDarkMode ? 155 : 205, fillColor)))
                                    g.FillPath(pillBr, pillPath);
                            }

                            RectangleF r1 = new RectangleF(rect.X + 5f, startY, maxTextAreaW, h1);
                            g.DrawString(topText, titleFont, darkBr, r1, sf);

                            if (showMid)
                            {
                                RectangleF r2 = new RectangleF(rect.X + 5f, startY + h1 + 2f, maxTextAreaW, h2);
                                RectangleF r3 = new RectangleF(rect.X + 5f, startY + h1 + h2 + 4f, maxTextAreaW, h3);
                                g.DrawString(midText, subFont, mutedBr, r2, sf);
                                g.DrawString(botText, subFont, darkBr, r3, sf);
                            }
                            else
                            {
                                RectangleF r3 = new RectangleF(rect.X + 5f, startY + h1 + 3f, maxTextAreaW, h3);
                                g.DrawString(botText, subFont, darkBr, r3, sf);
                            }
                        }
                    }
                }
            }
        }

        private static GraphicsPath CreateRoundedRect(RectangleF r, float radius)
        {
            float d = radius * 2f;
            var path = new GraphicsPath();
            if (r.Width < d) d = r.Width;
            if (r.Height < d) d = r.Height;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private PartitionEntry HitTest(Point pt)
        {
            for (int i = 0; i < _sliceRects.Count && i < _layout.Count; i++)
            {
                if (_sliceRects[i].Contains(pt))
                    return _layout[i];
            }
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var hit = HitTest(e.Location);
            if (!ReferenceEquals(hit, _hoveredPartition))
            {
                _hoveredPartition = hit;
                Cursor = hit != null ? Cursors.Hand : Cursors.Default;
                Invalidate();

                if (hit != null)
                {
                    string tip = hit.IsUnallocated
                        ? string.Format("Unallocated Space\nSize: {0} ({1})\nOffset: 0x{2:X} (LBA {3:N0}..{4:N0})",
                            ByteFormatter.FormatBytes(hit.PartitionLength), ByteFormatter.FormatMiB(hit.PartitionLength),
                            hit.StartingOffset, hit.StartLba, hit.EndLba)
                        : string.Format("{0}\nFS: {1} | Label: {2}\nSize: {3} | Used: {4}\nLBA: {5:N0}..{6:N0} | 4K Aligned: {7} | 1 MiB Aligned: {8}",
                            hit.LinuxStyleDeviceName,
                            FileSystemColors.GetDisplayName(hit.FileSystem),
                            string.IsNullOrEmpty(hit.Label) ? "(none)" : hit.Label,
                            ByteFormatter.FormatBytes(hit.PartitionLength),
                            hit.UsedBytes.HasValue ? ByteFormatter.FormatBytes(hit.UsedBytes.Value) : "N/A",
                            hit.StartLba, hit.EndLba,
                            hit.IsAligned4K ? "Yes" : "No",
                            hit.IsAligned1MiB ? "Yes" : "No");
                    _toolTip.SetToolTip(this, tip);
                }
                else
                {
                    _toolTip.SetToolTip(this, string.Empty);
                }
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoveredPartition != null)
            {
                _hoveredPartition = null;
                Cursor = Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            var hit = HitTest(e.Location);
            if (hit != null)
            {
                SelectPartition(hit);
                if (e.Button == MouseButtons.Right && PartitionContextMenuRequested != null)
                    PartitionContextMenuRequested(hit, e.Location);
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            var hit = HitTest(e.Location);
            if (hit != null)
            {
                SelectPartition(hit);
                if (PartitionDoubleClicked != null)
                    PartitionDoubleClicked(this, hit);
            }
        }
    }
}
