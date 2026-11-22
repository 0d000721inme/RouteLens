using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace RouteLens
{
    public sealed class BrandMark : Control
    {
        private const double DurationMilliseconds = 280;
        private readonly Timer _timer = new Timer { Interval = 16 };
        private readonly Stopwatch _elapsed = new Stopwatch();
        private bool _hasStarted;

        public BrandMark()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                ControlStyles.SupportsTransparentBackColor, true);
            SetStyle(ControlStyles.Selectable, false);
            BackColor = Color.Transparent; Size = new Size(38, 38); TabStop = false; AccessibleName = "RouteLens";
            _timer.Tick += delegate
            {
                if (_elapsed.Elapsed.TotalMilliseconds >= DurationMilliseconds)
                {
                    _timer.Stop(); _elapsed.Reset();
                }
                Invalidate();
            };
        }

        public void PlayEntrance()
        {
            if (IsDisposed || _hasStarted) return;
            _hasStarted = true;
            if (PageTransition.AnimationsEnabled) { _elapsed.Start(); _timer.Start(); }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            int side = Math.Min(Width, Height);
            if (side <= 0) return;
            float progress = _elapsed.IsRunning
                ? (float)Math.Min(1, _elapsed.Elapsed.TotalMilliseconds / DurationMilliseconds)
                : !_hasStarted && PageTransition.AnimationsEnabled ? 0f : 1f;
            GraphicsState state = e.Graphics.Save();
            e.Graphics.TranslateTransform((Width - side) / 2f, (Height - side) / 2f);
            BrandIcon.Draw(e.Graphics, side, progress);
            e.Graphics.Restore(state);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !IsDisposed) { _timer.Dispose(); _elapsed.Reset(); }
            base.Dispose(disposing);
        }
    }
}
