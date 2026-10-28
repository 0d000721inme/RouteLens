using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace RouteLens
{
    // Each point is an accepted snapshot's measured portion; nulls are gaps,
    // so an unavailable or paused sample never appears as a real zero.
    public sealed class TrafficTrend : Control
    {
        private sealed class PointValue { public double Down, Up; public bool Valid; }
        private readonly Queue<PointValue> _values = new Queue<PointValue>();
        private readonly Font _label = UiPalette.Body(8.4f);
        private DateTime _lastRecord = DateTime.MinValue;
        private static readonly Color Upload = Color.FromArgb(38, 139, 137);

        public TrafficTrend()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = UiPalette.Surface; Dock = DockStyle.Fill; Margin = new Padding(0);
            AccessibleRole = AccessibleRole.Graphic; AccessibleName = "实时下载和上传速度趋势";
        }
        public void Record(TrafficReport report)
        {
            DateTime now = DateTime.UtcNow;
            if (now - _lastRecord < TimeSpan.FromMilliseconds(500)) return;
            _lastRecord = now;
            bool valid = report != null && report.Available && report.Measuring && Finite(report.DownloadPerSecond) && Finite(report.UploadPerSecond);
            _values.Enqueue(new PointValue { Valid = valid, Down = valid ? Math.Max(0, report.DownloadPerSecond) : 0, Up = valid ? Math.Max(0, report.UploadPerSecond) : 0 });
            while (_values.Count > 60) _values.Dequeue();
            AccessibleDescription = valid ? "最近采样的下载和上传速度变化；无法计速时留空。" : "等待有效采样，无法计速时不显示为零。";
            Invalidate();
        }
        private static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); Graphics g = e.Graphics;
            using (Brush surface = new SolidBrush(BackColor)) g.FillRectangle(surface, ClientRectangle);
            float scale = g.DpiX / 96f; int left = (int)(13 * scale), top = (int)(6 * scale);
            TextRenderer.DrawText(g, "速度趋势", _label, new Rectangle(left, top, (int)(100 * scale), (int)(20 * scale)), UiPalette.Ink,
                UiPaint.TextFlags | TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            string legend = "下载 — 蓝色   上传 — 青色";
            TextRenderer.DrawText(g, legend, _label, new Rectangle(Math.Max(left, Width - (int)(200 * scale)), top, (int)(185 * scale), (int)(20 * scale)), UiPalette.Muted,
                UiPaint.TextFlags | TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            Rectangle plot = new Rectangle(left, top + (int)(24 * scale), Math.Max(1, Width - left * 2), Math.Max(1, Height - top - (int)(30 * scale)));
            PointValue[] points = _values.ToArray();
            if (!points.Any(x => x.Valid))
            {
                TextRenderer.DrawText(g, "等待有效采样", _label, plot, UiPalette.Muted, UiPaint.TextFlags | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix); return;
            }
            GraphicsState state = g.Save();
            g.SetClip(plot, CombineMode.Intersect); g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen grid = new Pen(UiPalette.Border, Math.Max(.5f, scale)))
                for (int y = 0; y < 3; y++) g.DrawLine(grid, plot.Left, plot.Top + y * (plot.Height - 1) / 2, plot.Right, plot.Top + y * (plot.Height - 1) / 2);
            double maximum = Math.Max(1, points.Where(x => x.Valid).Select(x => Math.Max(x.Down, x.Up)).Max());
            DrawLine(g, plot, points, maximum, false, UiPalette.Accent, scale);
            DrawLine(g, plot, points, maximum, true, Upload, scale);
            g.Restore(state);
        }
        private static void DrawLine(Graphics g, Rectangle plot, PointValue[] points, double maximum, bool upload, Color color, float scale)
        {
            PointF? previous = null;
            using (Pen pen = new Pen(color, Math.Max(1.2f, 1.7f * scale)))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
                for (int i = 0; i < points.Length; i++)
                {
                    if (!points[i].Valid) { previous = null; continue; }
                    float x = plot.Right - 1 - (points.Length - 1 - i) * Math.Max(2 * scale, (plot.Width - 1) / 59f);
                    float y = plot.Bottom - 1 - (float)((upload ? points[i].Up : points[i].Down) / maximum * Math.Max(1, plot.Height - 3));
                    PointF next = new PointF(x, y);
                    if (previous.HasValue) g.DrawLine(pen, previous.Value, next);
                    else using (Brush brush = new SolidBrush(color)) g.FillEllipse(brush, x - scale, y - scale, scale * 2, scale * 2);
                    previous = next;
                }
            }
        }
        protected override void Dispose(bool disposing) { if (disposing) _label.Dispose(); base.Dispose(disposing); }
    }
}
