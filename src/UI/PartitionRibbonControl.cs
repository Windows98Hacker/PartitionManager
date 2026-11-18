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

        public event Action<object, PartitionEntry> SelectedPartitionChanged;
        public event Action<object, PartitionEntry> PartitionDoubleClicked;
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
            MinimumSize = new Size(ThemeManager.F377, ThemeManager.F144);
            Size = new Size(890, ThemeManager.F144);
            Height = ThemeManager.F144;
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

        /// <summary>
        /// Collects distinct <see cref="FileSystemType"/> values in layout order for the ribbon legend.
        /// </summary>
        private List<FileSystemType> CollectLegendTypes()
        {
            var seen = new List<FileSystemType>();
            for (int i = 0; i < _layout.Count; i++)
            {
                FileSystemType fs = _layout[i].FileSystem;
                if (!seen.Contains(fs))
                    seen.Add(fs);
            }
            return seen;
        }

        /// <summary>
        /// Renders the proportional partition slices and the adaptive 1-to-2 row filesystem legend strip.
        /// </summary>
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            _sliceRects.Clear();

            var seen = CollectLegendTypes();
            int innerOuterW = Width - ThemeManager.F13 * 2;
            int legendRows = ComputeLegendRowCount(seen, innerOuterW);
            int legendH = legendRows > 1 ? 38 : ThemeManager.F34;
            int outerH = Math.Max(ThemeManager.F55, Height - ThemeManager.F8 - ThemeManager.F5 - legendH - ThemeManager.F8);
            Rectangle outer = new Rectangle(ThemeManager.F13, ThemeManager.F8, innerOuterW, outerH);
            if (outer.Width <= ThemeManager.F21 || outer.Height <= ThemeManager.F21) return;

            RectangleF outerF = new RectangleF(outer.X, outer.Y, outer.Width, outer.Height);
            using (GraphicsPath outerPath = ThemeManager.CreateRoundedRectPath(outerF, ThemeManager.F8))
            using (var bgBrush = new SolidBrush(ThemeManager.BackSurface))
            using (var bgPen = new Pen(ThemeManager.BorderColor, 1f))
            {
                g.FillPath(bgBrush, outerPath);
                g.DrawPath(bgPen, outerPath);
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

            float innerPad = ThemeManager.F8;
            float gap = ThemeManager.F5;
            float availWidth = outer.Width - innerPad * 2f;
            float minSliceWidth = Math.Min(89f, availWidth / Math.Max(1, _layout.Count));

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
            float halfGap = gap / 2f;

            for (int i = 0; i < _layout.Count; i++)
            {
                PartitionEntry part = _layout[i];
                float w = (i == _layout.Count - 1) ? Math.Max(10f, (outer.Right - innerPad) - curX) : widths[i];
                RectangleF rect = new RectangleF(curX + halfGap, sliceTop, Math.Max(6f, w - gap), sliceH);
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

            int legendBottomPad = legendRows > 1 ? ThemeManager.F8 : ThemeManager.F13;
            DrawLegendStrip(g, new Rectangle(outer.Left, outer.Bottom + ThemeManager.F5, outer.Width, Math.Max(ThemeManager.F21, Height - outer.Bottom - legendBottomPad)), seen, legendRows);
        }

        /// <summary>
        /// Determines whether the filesystem legend fits on a single row or should compress into two rows.
        /// </summary>
        private int ComputeLegendRowCount(List<FileSystemType> seen, int width)
        {
            if (seen == null || seen.Count == 0 || width < 60)
                return 1;

            using (var legendFont = new Font("Segoe UI", 8.2f, FontStyle.Regular))
            using (var scaleFont = new Font("Segoe UI", 8.0f, FontStyle.Bold))
            {
                int badgeReserve = 0;
                if (width >= ThemeManager.F377)
                {
                    string scaleLabel = _smartScale ? "Smart Scale" : "Linear Scale";
                    Size scaleSize = TextRenderer.MeasureText(scaleLabel, scaleFont);
                    badgeReserve = scaleSize.Width + ThemeManager.F8 + ThemeManager.F13;
                }

                int x = ThemeManager.F3;
                int maxRight = width - badgeReserve;
                int swatchSize = ThemeManager.F13;
                foreach (FileSystemType fs in seen)
                {
                    string label = FileSystemColors.GetDisplayName(fs);
                    Size textSize = TextRenderer.MeasureText(label, legendFont);
                    if (x + swatchSize + ThemeManager.F5 + textSize.Width > maxRight)
                        return 2;
                    x += swatchSize + ThemeManager.F5 + textSize.Width + ThemeManager.F21;
                }
            }
            return 1;
        }

        /// <summary>
        /// Draws the filesystem color swatches and labels across one or two horizontal rows.
        /// </summary>
        private void DrawLegendStrip(Graphics g, Rectangle bounds, List<FileSystemType> seen, int legendRows)
        {
            if (bounds.Height < 14 || bounds.Width < 60 || seen == null || seen.Count == 0) return;

            using (var legendFont = new Font("Segoe UI", 8.2f, FontStyle.Regular))
            using (var scaleFont = new Font("Segoe UI", 8.0f, FontStyle.Bold))
            using (var borderPen = new Pen(ThemeManager.BorderColor, 1f))
            {
                int rowH = legendRows > 1 ? Math.Max(14, bounds.Height / 2) : bounds.Height;

                // Reserve right side of row 0 for scale mode label when width suffices
                int badgeReserve = 0;
                if (bounds.Width >= ThemeManager.F377)
                {
                    string scaleLabel = _smartScale ? "Smart Scale" : "Linear Scale";
                    Size scaleSize = TextRenderer.MeasureText(scaleLabel, scaleFont);
                    int totalBadgeW = scaleSize.Width + ThemeManager.F8;
                    int bx = bounds.Right - totalBadgeW;

                    Rectangle scaleRect = new Rectangle(bx, bounds.Top, scaleSize.Width + 2, rowH);
                    TextRenderer.DrawText(g, scaleLabel, scaleFont, scaleRect, ThemeManager.TextSecondary,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
                    badgeReserve = totalBadgeW + ThemeManager.F13;
                }

                int row = 0;
                int x = bounds.Left + ThemeManager.F3;
                int maxRight = bounds.Right - badgeReserve;
                int swatchSize = legendRows > 1 ? Math.Max(10, ThemeManager.F13 - 2) : ThemeManager.F13;

                foreach (FileSystemType fs in seen)
                {
                    string label = FileSystemColors.GetDisplayName(fs);
                    Size textSize = TextRenderer.MeasureText(label, legendFont);
                    int itemWidth = swatchSize + ThemeManager.F5 + textSize.Width + (legendRows > 1 ? ThemeManager.F13 : ThemeManager.F21);

                    if (x + swatchSize + ThemeManager.F5 + textSize.Width > maxRight)
                    {
                        if (row == 0 && legendRows > 1)
                        {
                            row = 1;
                            x = bounds.Left + ThemeManager.F3;
                            maxRight = bounds.Right - ThemeManager.F3;
                            if (x + swatchSize + ThemeManager.F5 + textSize.Width > maxRight)
                                break;
                        }
                        else
                        {
                            break;
                        }
                    }

                    int rowTop = bounds.Top + row * rowH;
                    int yMid = rowTop + rowH / 2;

                    RectangleF swatchF = new RectangleF(x, yMid - swatchSize / 2f, swatchSize, swatchSize);
                    using (GraphicsPath sp = ThemeManager.CreateRoundedRectPath(swatchF, ThemeManager.F3))
                    using (var fill = new SolidBrush(FileSystemColors.GetBorderColor(fs)))
                    {
                        g.FillPath(fill, sp);
                        g.DrawPath(borderPen, sp);
                    }

                    Rectangle textRect = new Rectangle(x + swatchSize + ThemeManager.F5, rowTop, textSize.Width + 2, rowH);
                    TextRenderer.DrawText(g, label, legendFont, textRect, ThemeManager.TextSecondary,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);

                    x += itemWidth;
                }
            }
        }

        /// <summary>
        /// Draws a single partition slice (or hatched unallocated block), its used-space bar, selection
        /// glow, and center label pill fitted to the slice width.
        /// </summary>
        private void DrawPartitionSlice(Graphics g, RectangleF rect, PartitionEntry part, bool isSelected, bool isHovered)
        {
            Color borderColor = FileSystemColors.GetBorderColor(part.FileSystem);
            Color fillColor = FileSystemColors.GetFillColor(part.FileSystem);
            Color usedColor = FileSystemColors.GetUsedColor(part.FileSystem);

            using (GraphicsPath path = ThemeManager.CreateRoundedRectPath(rect, ThemeManager.F8))
            {
                if (part.IsUnallocated)
                {
                    Color hatchFore = ThemeManager.IsDarkMode ? Color.FromArgb(62, 71, 89) : Color.FromArgb(203, 213, 225);
                    Color hatchBack = ThemeManager.IsDarkMode ? Color.FromArgb(30, 35, 45) : Color.FromArgb(241, 245, 249);
                    using (var hatch = new HatchBrush(HatchStyle.BackwardDiagonal, hatchFore, hatchBack))
                        g.FillPath(hatch, path);
                }
                else
                {
                    using (var fillBr = new SolidBrush(fillColor))
                        g.FillPath(fillBr, path);

                    // Used-space fill inside block + bottom F5 accent bar
                    double usedFrac = part.UsedFraction;
                    if (usedFrac > 0.005)
                    {
                        float usedW = (float)(rect.Width * usedFrac);
                        Region prevClip = g.Clip;
                        g.SetClip(path);
                        using (var usedBr = new SolidBrush(usedColor))
                            g.FillRectangle(usedBr, rect.X, rect.Y, usedW, rect.Height);
                        using (var goldUsedBr = new SolidBrush(Color.FromArgb(190, ThemeManager.AccentGold)))
                            g.FillRectangle(goldUsedBr, rect.X, rect.Bottom - ThemeManager.F5, usedW, ThemeManager.F5);
                        g.Clip = prevClip;
                    }

                    // Subtle top-to-bottom luxury sheen
                    using (var sheen = new LinearGradientBrush(
                        new RectangleF(rect.X, rect.Y, rect.Width, rect.Height),
                        Color.FromArgb(ThemeManager.IsDarkMode ? 24 : 50, Color.White),
                        Color.FromArgb(ThemeManager.IsDarkMode ? 18 : 8, Color.Black),
                        LinearGradientMode.Vertical))
                    {
                        g.FillPath(sheen, path);
                    }
                }

                if (isHovered && !isSelected)
                {
                    Color hoverColor = ThemeManager.IsDarkMode ? Color.FromArgb(32, 255, 255, 255) : Color.FromArgb(24, 15, 23, 42);
                    using (var hoverBr = new SolidBrush(hoverColor))
                        g.FillPath(hoverBr, path);
                }

                float penWidth = isSelected ? 2.6f : 1.5f;
                Color penCol = isSelected ? ThemeManager.AccentGold : borderColor;
                using (var pen = new Pen(penCol, penWidth))
                {
                    if (part.IsUnallocated)
                        pen.DashStyle = DashStyle.Dash;
                    g.DrawPath(pen, path);
                }

                if (isSelected)
                {
                    RectangleF innerGlow = RectangleF.Inflate(rect, -2.5f, -2.5f);
                    if (innerGlow.Width > 6f && innerGlow.Height > 6f)
                    {
                        Color glowCol = ThemeManager.IsDarkMode ? ThemeManager.AccentBlue : Color.White;
                        using (var innerPen = new Pen(glowCol, 1.4f))
                        using (var innerPath = ThemeManager.CreateRoundedRectPath(innerGlow, ThemeManager.F5))
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

                            bool showMid = rect.Width >= 64 && rect.Height >= (h1 + h2 + h3 + 10f);
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
                                using (var pillPath = ThemeManager.CreateRoundedRectPath(pillRect, ThemeManager.F5))
                                using (var pillBr = new SolidBrush(Color.FromArgb(ThemeManager.IsDarkMode ? 165 : 210, fillColor)))
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
