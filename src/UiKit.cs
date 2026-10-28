using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace RouteLens
{
    public static class UiPalette
    {
        public static readonly Color Canvas = Color.FromArgb(243, 246, 251);
        public static readonly Color Surface = Color.White;
        public static readonly Color Raised = Color.FromArgb(237, 242, 248);
        public static readonly Color Border = Color.FromArgb(219, 229, 240);
        public static readonly Color Ink = Color.FromArgb(30, 42, 58);
        public static readonly Color Muted = Color.FromArgb(91, 106, 127);
        public static readonly Color Accent = Color.FromArgb(28, 103, 193);
        public static readonly Color AccentSoft = Color.FromArgb(229, 240, 252);
        public static readonly Color Warn = Color.FromArgb(154, 106, 37);

        public static Font Body(float size) { return new Font("Microsoft YaHei UI", size, FontStyle.Regular, GraphicsUnit.Point); }
        public static Font Mono(float size) { return new Font("Consolas", size, FontStyle.Regular, GraphicsUnit.Point); }
        public static Font Heading(float size) { return new Font("Segoe UI Semibold", size, FontStyle.Regular, GraphicsUnit.Point); }
    }

    public static class UiPaint
    {
        public const TextFormatFlags TextFlags = TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform;

        // ButtonBase is opaque and skips OnPaintBackground. Initialize every pixel
        // before drawing so a reused double buffer cannot retain another control.
        public static void PaintControlSurface(Graphics graphics, Control control)
        {
            Color surface = UiPalette.Canvas;
            for (Control parent = control.Parent; parent != null; parent = parent.Parent)
            {
                RoundedPanel rounded = parent as RoundedPanel;
                if (rounded != null) { surface = rounded.SurfaceColor; break; }
                if (parent.BackColor.A == 255) { surface = parent.BackColor; break; }
            }
            using (Brush brush = new SolidBrush(surface)) graphics.FillRectangle(brush, control.ClientRectangle);
        }

        public static GraphicsPath RoundedPath(RectangleF bounds, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            if (bounds.Width <= 0 || bounds.Height <= 0) return path;
            radius = Math.Max(0, Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2));
            if (radius < 0.5f) { path.AddRectangle(bounds); path.CloseFigure(); return path; }
            float diameter = radius * 2;
            path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void RoundRect(Graphics graphics, RectangleF bounds, float radius, Color fill, Color? outline = null)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            GraphicsState state = graphics.Save();
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = RoundedPath(bounds, radius))
            {
                if (fill.A > 0) using (Brush brush = new SolidBrush(fill)) graphics.FillPath(brush, path);
                if (outline.HasValue && outline.Value.A > 0)
                    using (Pen pen = new Pen(outline.Value, graphics.DpiX / 96f)) graphics.DrawPath(pen, path);
            }
            graphics.Restore(state);
        }

        public static void DrawIcon(Graphics graphics, string name, Rectangle bounds, Color color, float stroke = 1.7f)
        {
            if (String.IsNullOrEmpty(name) || bounds.Width <= 0 || bounds.Height <= 0) return;
            GraphicsState state = graphics.Save();
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float side = Math.Min(bounds.Width, bounds.Height);
            float scale = side / 24f;
            graphics.TranslateTransform(bounds.X + (bounds.Width - side) / 2, bounds.Y + (bounds.Height - side) / 2);
            graphics.ScaleTransform(scale, scale);
            using (Pen pen = new Pen(color, Math.Max(0.5f, stroke) * graphics.DpiX / 96f / scale))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
                switch (name.ToLowerInvariant())
                {
                    case "search":
                        graphics.DrawEllipse(pen, 4, 4, 11, 11); graphics.DrawLine(pen, 14, 14, 20, 20); break;
                    case "pause":
                        graphics.DrawLine(pen, 8.5f, 5.5f, 8.5f, 18.5f); graphics.DrawLine(pen, 15.5f, 5.5f, 15.5f, 18.5f); break;
                    case "play":
                        graphics.DrawPolygon(pen, new PointF[] { new PointF(8, 5), new PointF(19, 12), new PointF(8, 19) }); break;
                    case "refresh":
                        graphics.DrawArc(pen, 4, 4, 16, 16, 45, 280);
                        graphics.DrawLines(pen, new PointF[] { new PointF(14.2f, 5.4f), new PointF(18.6f, 5.4f), new PointF(18.6f, 1.2f) }); break;
                    case "globe":
                        graphics.DrawEllipse(pen, 3.5f, 3.5f, 17, 17);
                        graphics.DrawEllipse(pen, 8, 3.5f, 8, 17);
                        graphics.DrawLine(pen, 4, 12, 20, 12); break;
                    case "network":
                        graphics.DrawLines(pen, new PointF[] { new PointF(5, 13), new PointF(5, 10), new PointF(19, 10), new PointF(19, 13) });
                        graphics.DrawLine(pen, 12, 6, 12, 13);
                        SmallNode(graphics, pen, 9, 2.5f, 6, 4.5f); SmallNode(graphics, pen, 2, 14, 6, 6); SmallNode(graphics, pen, 9, 14, 6, 6); SmallNode(graphics, pen, 16, 14, 6, 6); break;
                    case "arrow":
                        graphics.DrawLine(pen, 4, 12, 20, 12); graphics.DrawLines(pen, new PointF[] { new PointF(14, 6), new PointF(20, 12), new PointF(14, 18) }); break;
                    case "copy":
                        SmallNode(graphics, pen, 8, 8, 12, 12);
                        using (GraphicsPath copy = new GraphicsPath()) { copy.AddLines(new PointF[] { new PointF(4, 15), new PointF(4, 4), new PointF(15, 4) }); graphics.DrawPath(pen, copy); } break;
                    case "minus": graphics.DrawLine(pen, 5, 12, 19, 12); break;
                    case "maximize": graphics.DrawRectangle(pen, 5, 5, 14, 14); break;
                    case "close": graphics.DrawLine(pen, 6, 6, 18, 18); graphics.DrawLine(pen, 6, 18, 18, 6); break;
                    case "chevron": graphics.DrawLines(pen, new PointF[] { new PointF(7, 9), new PointF(12, 14), new PointF(17, 9) }); break;
                    case "link":
                        graphics.DrawArc(pen, 2.5f, 8, 12, 8, 90, 180); graphics.DrawArc(pen, 9.5f, 8, 12, 8, -90, 180); graphics.DrawLine(pen, 8, 12, 16, 12); break;
                    case "help":
                        graphics.DrawEllipse(pen, 3.5f, 3.5f, 17, 17);
                        graphics.DrawArc(pen, 9, 7, 6, 5, 185, 295); graphics.DrawLine(pen, 12, 12, 12, 13.5f);
                        using (Brush dot = new SolidBrush(color)) graphics.FillEllipse(dot, 11.2f, 16, 1.6f, 1.6f); break;
                    case "filter":
                        graphics.DrawLine(pen, 4, 6, 20, 6); graphics.DrawLine(pen, 7, 12, 17, 12); graphics.DrawLine(pen, 10, 18, 14, 18); break;
                }
            }
            graphics.Restore(state);
        }

        private static void SmallNode(Graphics graphics, Pen pen, float x, float y, float width, float height)
        {
            using (GraphicsPath path = RoundedPath(new RectangleF(x, y, width, height), 1.5f)) graphics.DrawPath(pen, path);
        }
    }

    public class RoundedPanel : Panel
    {
        private int radius = 20;
        private Color surface = UiPalette.Surface;
        private Color border = UiPalette.Border;

        public RoundedPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Padding = new Padding(16);
        }

        public int Radius { get { return radius; } set { radius = Math.Max(0, value); Invalidate(); } }
        public Color SurfaceColor { get { return surface; } set { surface = value; Invalidate(); } }
        public Color BorderColor { get { return border; } set { border = value; Invalidate(); } }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            base.OnPaintBackground(e);
            float scale = e.Graphics.DpiX / 96f;
            UiPaint.RoundRect(e.Graphics, new RectangleF(0.5f * scale, 0.5f * scale, Math.Max(0, Width - scale), Math.Max(0, Height - scale)), Radius * scale, surface, border);
        }
    }

    public class ModernButton : Button
    {
        private bool hovered;
        private bool pressed;
        private bool primary;
        private bool quiet;
        private string iconName;
        private int cornerRadius = 12;

        public ModernButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            SetStyle(ControlStyles.Opaque, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            BackColor = Color.Transparent;
            ForeColor = UiPalette.Ink;
            Font = UiPalette.Body(9);
            Cursor = Cursors.Hand;
            Size = new Size(116, 38);
            Padding = new Padding(12, 0, 12, 0);
            AccessibleRole = AccessibleRole.PushButton;
        }

        public string IconName { get { return iconName; } set { iconName = value; Invalidate(); } }
        public bool Primary { get { return primary; } set { primary = value; Invalidate(); } }
        public bool Quiet { get { return quiet; } set { quiet = value; Invalidate(); } }
        public int CornerRadius { get { return cornerRadius; } set { cornerRadius = Math.Max(0, value); Invalidate(); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            UiPaint.PaintControlSurface(graphics, this);
            float scale = graphics.DpiX / 96f;
            Color fill;
            Color outline;
            Color text;
            if (!Enabled)
            {
                fill = Quiet ? Color.Transparent : Color.FromArgb(239, 242, 244);
                outline = Quiet ? Color.Transparent : UiPalette.Border;
                text = Color.FromArgb(142, 151, 159);
            }
            else if (Primary)
            {
                fill = pressed ? Color.FromArgb(18, 76, 151) : (hovered ? Color.FromArgb(24, 91, 175) : UiPalette.Accent);
                outline = fill; text = Color.White;
            }
            else
            {
                fill = pressed ? Color.FromArgb(226, 233, 236) : (hovered ? Color.FromArgb(237, 242, 244) : (Quiet ? Color.Transparent : UiPalette.Surface));
                outline = Quiet ? Color.Transparent : (hovered ? Color.FromArgb(198, 209, 216) : UiPalette.Border);
                text = ForeColor;
            }
            UiPaint.RoundRect(graphics, new RectangleF(0.5f * scale, 0.5f * scale, Math.Max(0, Width - scale), Math.Max(0, Height - scale)), CornerRadius * scale, fill, outline);
            int offset = pressed ? Math.Max(1, (int)Math.Round(scale)) : 0;
            Rectangle content = new Rectangle(Padding.Left, Padding.Top + offset, Math.Max(0, Width - Padding.Horizontal), Math.Max(0, Height - Padding.Vertical));
            bool hasIcon = !String.IsNullOrEmpty(IconName);
            int iconSize = Math.Min((int)Math.Round(18 * scale), Math.Max((int)Math.Round(12 * scale), content.Height - (int)Math.Round(10 * scale)));
            int gap = hasIcon && !String.IsNullOrEmpty(Text) ? (int)Math.Round(7 * scale) : 0;
            int measured = String.IsNullOrEmpty(Text) ? 0 : TextRenderer.MeasureText(graphics, Text, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
            int textWidth = Math.Min(measured, Math.Max(0, content.Width - (hasIcon ? iconSize + gap : 0)));
            int combined = textWidth + (hasIcon ? iconSize + gap : 0);
            int x = content.Left + (content.Width - combined) / 2;
            if (hasIcon)
            {
                UiPaint.DrawIcon(graphics, IconName, new Rectangle(x, content.Top + (content.Height - iconSize) / 2, iconSize, iconSize), text, 1.55f);
                x += iconSize + gap;
            }
            if (textWidth > 0)
                TextRenderer.DrawText(graphics, Text, Font, new Rectangle(x, content.Top, textWidth + 1, content.Height), text, UiPaint.TextFlags | TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            if (Focused && ShowFocusCues && Enabled)
            {
                Color focus = Primary ? Color.FromArgb(218, 234, 255) : UiPalette.Accent;
                using (Pen pen = new Pen(focus, 1.2f * scale))
                using (GraphicsPath path = UiPaint.RoundedPath(new RectangleF(3 * scale, 3 * scale, Math.Max(0, Width - 6 * scale), Math.Max(0, Height - 6 * scale)), Math.Max(1, CornerRadius - 2) * scale))
                {
                    GraphicsState state = graphics.Save(); graphics.SmoothingMode = SmoothingMode.AntiAlias; graphics.DrawPath(pen, path); graphics.Restore(state);
                }
            }
        }

        protected override void OnMouseEnter(EventArgs e) { hovered = true; base.OnMouseEnter(e); Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { hovered = false; pressed = false; base.OnMouseLeave(e); Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) pressed = true; base.OnMouseDown(e); Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; base.OnMouseUp(e); Invalidate(); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) pressed = true; base.OnKeyDown(e); Invalidate(); }
        protected override void OnKeyUp(KeyEventArgs e) { pressed = false; base.OnKeyUp(e); Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { pressed = false; base.OnLostFocus(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    }

    public class ModernCheck : CheckBox
    {
        private bool hovered;
        public ModernCheck()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            SetStyle(ControlStyles.Opaque, true);
            AutoSize = false;
            BackColor = Color.Transparent;
            ForeColor = UiPalette.Ink;
            Font = UiPalette.Body(9);
            Size = new Size(125, 34);
            Cursor = Cursors.Hand;
            TextAlign = ContentAlignment.MiddleLeft;
            AccessibleRole = AccessibleRole.CheckButton;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            UiPaint.PaintControlSurface(graphics, this);
            float scale = graphics.DpiX / 96f;
            RectangleF box = new RectangleF(3 * scale, (Height - 16 * scale) / 2, 16 * scale, 16 * scale);
            bool marked = CheckState != CheckState.Unchecked;
            Color fill = !Enabled ? Color.FromArgb(236, 240, 242) : (marked ? UiPalette.Accent : UiPalette.Surface);
            Color outline = !Enabled ? UiPalette.Border : (marked || hovered ? UiPalette.Accent : Color.FromArgb(126, 140, 150));
            UiPaint.RoundRect(graphics, box, 4 * scale, fill, outline);
            if (marked)
            {
                GraphicsState state = graphics.Save(); graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (Pen pen = new Pen(Enabled ? Color.White : Color.FromArgb(157, 169, 177), 1.75f * scale))
                {
                    pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
                    if (CheckState == CheckState.Indeterminate) graphics.DrawLine(pen, box.X + 4 * scale, box.Y + 8 * scale, box.X + 12 * scale, box.Y + 8 * scale);
                    else graphics.DrawLines(pen, new PointF[] { new PointF(box.X + 3.5f * scale, box.Y + 8 * scale), new PointF(box.X + 6.8f * scale, box.Y + 11.2f * scale), new PointF(box.X + 12.5f * scale, box.Y + 4.8f * scale) });
                }
                graphics.Restore(state);
            }
            int textLeft = (int)Math.Round(27 * scale);
            Rectangle text = new Rectangle(textLeft, 0, Math.Max(0, Width - textLeft - (int)Math.Round(2 * scale)), Height);
            TextRenderer.DrawText(graphics, Text, Font, text, Enabled ? ForeColor : UiPalette.Muted, UiPaint.TextFlags | TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            if (Focused && ShowFocusCues && Enabled)
            {
                using (Pen pen = new Pen(UiPalette.Accent, scale))
                using (GraphicsPath path = UiPaint.RoundedPath(new RectangleF(0.5f * scale, 1.5f * scale, Math.Max(0, Width - scale), Math.Max(0, Height - 3 * scale)), 6 * scale))
                {
                    GraphicsState state = graphics.Save(); graphics.SmoothingMode = SmoothingMode.AntiAlias; graphics.DrawPath(pen, path); graphics.Restore(state);
                }
            }
        }

        protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }
        protected override void OnCheckStateChanged(EventArgs e) { base.OnCheckStateChanged(e); Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { hovered = true; base.OnMouseEnter(e); Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { hovered = false; base.OnMouseLeave(e); Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    }

    public sealed class BrandMark : Control
    {
        public BrandMark()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent; Size = new Size(38, 38); TabStop = false; AccessibleName = "RouteLens";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            GraphicsState state = graphics.Save();
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float side = Math.Min(Width, Height);
            graphics.TranslateTransform((Width - side) / 2, (Height - side) / 2);
            graphics.ScaleTransform(side / 40f, side / 40f);
            UiPaint.RoundRect(graphics, new RectangleF(0.5f, 0.5f, 39, 39), 11, UiPalette.AccentSoft);
            using (Pen pen = new Pen(UiPalette.Accent, 2.4f))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
                graphics.DrawArc(pen, 8, 8, 24, 24, 7, 303);
                graphics.DrawLines(pen, new PointF[] { new PointF(13, 26), new PointF(18, 26), new PointF(25, 15), new PointF(30, 15) });
            }
            using (Brush brush = new SolidBrush(UiPalette.Accent)) { graphics.FillEllipse(brush, 10.7f, 23.7f, 4.6f, 4.6f); graphics.FillEllipse(brush, 27.7f, 12.7f, 4.6f, 4.6f); }
            graphics.Restore(state);
        }
    }
}
