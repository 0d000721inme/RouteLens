using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RouteLens
{
    internal sealed class ThemeTransition : Control
    {
        private const double DurationMilliseconds = 200;
        private const long MaximumCapturePixels = 2500000;
        private readonly Timer _timer = new Timer { Interval = 16 };
        private readonly Stopwatch _elapsed = new Stopwatch();
        private Bitmap _previousFrame, _currentFrame;
        private Control _owner;

        public ThemeTransition()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false; Visible = false; AccessibleRole = AccessibleRole.None;
            _timer.Tick += delegate
            {
                if (_elapsed.Elapsed.TotalMilliseconds >= DurationMilliseconds) Stop();
                else Invalidate();
            };
        }

        public bool IsRunning { get { return _elapsed.IsRunning && _previousFrame != null; } }

        public void Play(Control window)
        {
            Play(window, PageTransition.AnimationsEnabled);
        }

        internal void Play(Control window, bool animationsEnabled)
        {
            Stop();
            Rectangle bounds = window == null ? Rectangle.Empty : window.DisplayRectangle;
            if (IsDisposed || window == null || window.IsDisposed || !window.IsHandleCreated ||
                bounds.Width <= 0 || bounds.Height <= 0 ||
                (long)bounds.Width * bounds.Height > MaximumCapturePixels ||
                !animationsEnabled) return;
            Bitmap frame = CaptureFrame(window, bounds);
            if (frame == null) return;
            _owner = window;
            _owner.SizeChanged += OwnerSizeChanged;
            _previousFrame = frame;
            _elapsed.Restart(); Visible = true; BringToFront();
            _timer.Start(); Invalidate();
        }

        public void CaptureCurrent(Control window)
        {
            if (!IsRunning || window == null || window.IsDisposed) return;
            Visible = false;
            Bitmap next = CaptureFrame(window, window.DisplayRectangle);
            if (next == null) { Stop(); return; }
            if (_currentFrame != null) _currentFrame.Dispose();
            _currentFrame = next;
            Visible = true; BringToFront(); Invalidate();
        }

        private static Bitmap CaptureFrame(Control window, Rectangle bounds)
        {
            Bitmap frame = null;
            try
            {
                if (bounds.X < 0 || bounds.Y < 0 || bounds.Right > window.ClientSize.Width || bounds.Bottom > window.ClientSize.Height)
                    throw new ArgumentException("capture bounds outside client: " + bounds + " client " + window.ClientSize);
                using (Bitmap fullWindow = new Bitmap(window.ClientSize.Width, window.ClientSize.Height, PixelFormat.Format32bppPArgb))
                {
                    window.DrawToBitmap(fullWindow, new Rectangle(Point.Empty, fullWindow.Size));
                    frame = fullWindow.Clone(bounds, PixelFormat.Format32bppPArgb);
                }
                return frame;
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (ExternalException) { }
            catch (OutOfMemoryException) { }
            if (frame != null) frame.Dispose();
            return null;
        }

        public void Stop()
        {
            if (_owner != null) { _owner.SizeChanged -= OwnerSizeChanged; _owner = null; }
            _timer.Stop(); _elapsed.Reset();
            if (!IsDisposed) Visible = false;
            if (_previousFrame != null) { _previousFrame.Dispose(); _previousFrame = null; }
            if (_currentFrame != null) { _currentFrame.Dispose(); _currentFrame = null; }
        }

        private void OwnerSizeChanged(object sender, EventArgs e) { Stop(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_previousFrame == null) return;
            double progress = Math.Min(1, _elapsed.Elapsed.TotalMilliseconds / DurationMilliseconds);
            if (_currentFrame != null) e.Graphics.DrawImageUnscaled(_currentFrame, 0, 0);
            using (ImageAttributes attributes = new ImageAttributes())
            {
                ColorMatrix matrix = new ColorMatrix();
                matrix.Matrix33 = (float)Math.Pow(1 - progress, 2);
                attributes.SetColorMatrix(matrix);
                e.Graphics.DrawImage(_previousFrame, ClientRectangle, 0, 0,
                    _previousFrame.Width, _previousFrame.Height, GraphicsUnit.Pixel, attributes);
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x84) { m.Result = new IntPtr(-1); return; }
            base.WndProc(ref m);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !IsDisposed) { Stop(); _timer.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
