using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RouteLens
{
    // Temporary page images never leave RAM. Navigation remains independent of
    // animation: capture failure, reduced motion or a resize displays the page directly.
    public sealed class PageTransition : Control
    {
        private const double DurationMilliseconds = 170;
        private const long MaximumCapturePixels = 2000000;
        private readonly Timer _timer = new Timer { Interval = 16 };
        private readonly Stopwatch _elapsed = new Stopwatch();
        private Bitmap _outgoing, _incoming;

        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SystemParametersInfo(uint action, uint parameter,
            [MarshalAs(UnmanagedType.Bool)] out bool value, uint flags);

        public PageTransition()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false; Visible = false; BackColor = UiPalette.Canvas;
            AccessibleRole = AccessibleRole.None;
            _timer.Tick += delegate
            {
                if (_elapsed.Elapsed.TotalMilliseconds >= DurationMilliseconds) Stop();
                else Invalidate();
            };
        }

        public bool IsRunning { get { return _timer.Enabled && _outgoing != null && _incoming != null; } }

        public static bool AnimationsEnabled
        {
            get
            {
                if (SystemInformation.HighContrast || SystemInformation.TerminalServerSession) return false;
                bool enabled;
                // SPI_GETCLIENTAREAANIMATION: respect the user's Windows animation setting.
                return SystemParametersInfo(0x1042, 0, out enabled, 0) && enabled;
            }
        }

        public new static Bitmap Capture(Control page)
        {
            if (page == null || page.IsDisposed || !page.IsHandleCreated || !page.Visible || !AnimationsEnabled) return null;
            Size size = page.ClientSize;
            if (size.Width <= 0 || size.Height <= 0 || (long)size.Width * size.Height > MaximumCapturePixels) return null;
            Bitmap image = null;
            try
            {
                image = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
                page.DrawToBitmap(image, new Rectangle(Point.Empty, size));
                return image;
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (ExternalException) { }
            catch (OutOfMemoryException) { }
            if (image != null) image.Dispose();
            return null;
        }

        // Ownership of outgoing is transferred even when the animation is skipped.
        public void Play(Bitmap outgoing, Control incoming)
        {
            Stop();
            if (outgoing == null) return;
            if (IsDisposed || Parent == null || incoming == null || incoming.IsDisposed || incoming.Parent != Parent || !AnimationsEnabled)
            { outgoing.Dispose(); return; }
            // Hidden docked controls do not receive their final layout bounds.
            // Size the overlay before acquiring the images it will own.
            Bounds = incoming.Bounds;
            Bitmap next = Capture(incoming);
            if (next == null || next.Size != outgoing.Size || next.Size != ClientSize)
            { outgoing.Dispose(); if (next != null) next.Dispose(); return; }
            _outgoing = outgoing; _incoming = next;
            _elapsed.Restart(); Visible = true; BringToFront();
            _timer.Start(); Invalidate();
        }

        public void Stop()
        {
            _timer.Stop(); _elapsed.Reset();
            if (!IsDisposed) Visible = false;
            if (_outgoing != null) { _outgoing.Dispose(); _outgoing = null; }
            if (_incoming != null) { _incoming.Dispose(); _incoming = null; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_outgoing == null || _incoming == null) return;
            double progress = Math.Min(1, _elapsed.Elapsed.TotalMilliseconds / DurationMilliseconds);
            // Ease out smoothly, without translating text or changing layout.
            float oldOpacity = (float)Math.Pow(1 - progress, 3);
            e.Graphics.DrawImageUnscaled(_incoming, 0, 0);
            using (ImageAttributes attributes = new ImageAttributes())
            {
                ColorMatrix matrix = new ColorMatrix(); matrix.Matrix33 = oldOpacity;
                attributes.SetColorMatrix(matrix);
                e.Graphics.DrawImage(_outgoing, ClientRectangle, 0, 0, _outgoing.Width, _outgoing.Height, GraphicsUnit.Pixel, attributes);
            }
        }

        protected override void OnSizeChanged(EventArgs e) { Stop(); base.OnSizeChanged(e); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { Stop(); _timer.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
