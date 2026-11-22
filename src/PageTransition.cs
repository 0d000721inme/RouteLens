using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RouteLens
{
    public sealed class PageTransition : Control
    {
        private const double DurationMilliseconds = 180;
        private readonly Timer _timer = new Timer { Interval = 16 };
        private readonly Stopwatch _elapsed = new Stopwatch();

        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SystemParametersInfo(uint action, uint parameter,
            [MarshalAs(UnmanagedType.Bool)] out bool value, uint flags);

        public PageTransition()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer, true);
            SetStyle(ControlStyles.Selectable, false);
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            TabStop = false; Visible = false; BackColor = UiPalette.Canvas;
            AccessibleRole = AccessibleRole.None;
            _timer.Tick += delegate
            {
                if (_elapsed.Elapsed.TotalMilliseconds >= DurationMilliseconds) Stop();
                else Invalidate();
            };
        }

        public bool IsRunning { get { return _elapsed.IsRunning; } }

        public static bool AnimationsEnabled
        {
            get
            {
                if (SystemInformation.HighContrast || SystemInformation.TerminalServerSession) return false;
                bool enabled;
                // SPI_GETCLIENTAREAANIMATION
                return SystemParametersInfo(0x1042, 0, out enabled, 0) && enabled;
            }
        }

        public void Play()
        {
            Stop();
            if (IsDisposed || Parent == null || !Parent.Visible || Parent.ClientSize.Width <= 0 || !AnimationsEnabled) return;
            SetBounds(0, 0, Parent.ClientSize.Width, 3);
            _elapsed.Start();
            Visible = true; BringToFront();
            _timer.Start(); Invalidate();
        }

        public void Stop()
        {
            if (IsDisposed) return;
            _timer.Stop(); _elapsed.Reset(); Visible = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (!IsRunning) return;
            double progress = Math.Min(1, _elapsed.Elapsed.TotalMilliseconds / DurationMilliseconds);
            double distance = 1 - Math.Pow(1 - progress, 3);
            int width = Math.Max(1, (int)(Width * (.12 + .88 * distance)));
            using (Brush brush = new SolidBrush(Color.FromArgb((int)(210 * (1 - progress)), UiPalette.Accent)))
                e.Graphics.FillRectangle(brush, 0, 0, width, Height);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x84) { m.Result = new IntPtr(-1); return; } // HTTRANSPARENT
            base.WndProc(ref m);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            Stop(); base.OnSizeChanged(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !IsDisposed)
            {
                Stop(); _timer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
