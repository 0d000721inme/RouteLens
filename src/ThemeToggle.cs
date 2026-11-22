using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace RouteLens
{
    internal sealed class ThemeToggle : Button
    {
        private const double Duration = 160;
        private readonly Timer _timer = new Timer { Interval = 16 };
        private readonly Stopwatch _elapsed = new Stopwatch();
        private float _from, _to;

        public ThemeToggle()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Opaque, true);
            FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; UseVisualStyleBackColor = false;
            Cursor = Cursors.Hand; Width = 52; Height = 36; Margin = new Padding(0);
            Name = "themeToggle"; AccessibleRole = AccessibleRole.PushButton;
            SetTheme(false);
            _timer.Tick += delegate
            {
                if (_elapsed.Elapsed.TotalMilliseconds >= Duration) { _elapsed.Reset(); _timer.Stop(); }
                Invalidate();
            };
        }

        private float Position
        {
            get
            {
                if (!_elapsed.IsRunning) return _to;
                double t = Math.Min(1, _elapsed.Elapsed.TotalMilliseconds / Duration);
                return _from + (_to - _from) * (float)(1 - Math.Pow(1 - t, 3));
            }
        }

        public void SetTheme(bool animate)
        {
            _from = Position; _to = UiPalette.IsDark ? 1 : 0;
            AccessibleName = UiPalette.IsDark ? "切换到浅色主题" : "切换到深色主题";
            AccessibleDescription = UiPalette.IsDark ? "当前为深色主题" : "当前为浅色主题";
            if (animate && _from != _to && PageTransition.AnimationsEnabled) { _elapsed.Restart(); _timer.Start(); }
            else { _elapsed.Reset(); _timer.Stop(); }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            UiPaint.PaintControlSurface(graphics, this);
            float scale = graphics.DpiX / 96f;
            float height = Math.Min(24 * scale, Height - 2 * scale);
            float width = Math.Min(46 * scale, Width - 2 * scale);
            if (height <= 0 || width <= height) return;
            RectangleF track = new RectangleF((Width - width) / 2, (Height - height) / 2, width, height);
            UiPaint.RoundRect(graphics, track, height / 2, UiPalette.Raised,
                Focused && ShowFocusCues ? UiPalette.Accent : UiPalette.Border);
            float inset = 2 * scale;
            float side = height - 2 * inset;
            RectangleF thumb = new RectangleF(track.X + inset + Position * (width - height), track.Y + inset, side, side);
            UiPaint.RoundRect(graphics, thumb, side / 2, UiPalette.Accent);
            UiPaint.DrawIcon(graphics, Position >= .5f ? "moon" : "sun",
                Rectangle.Round(RectangleF.Inflate(thumb, -2 * scale, -2 * scale)), UiPalette.AccentInk, 1.15f);
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !IsDisposed) _timer.Dispose();
            base.Dispose(disposing);
        }
    }
}
