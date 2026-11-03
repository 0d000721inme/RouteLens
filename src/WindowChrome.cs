using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RouteLens
{
    internal static class WindowChrome
    {
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr window, int index, int value);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

        internal static void EnableNativeSizing(IntPtr window)
        {
            int style = GetWindowLong(window, -16);
            if ((style & 0x00040000) != 0) return;
            SetWindowLong(window, -16, style | 0x00040000);
            SetWindowPos(window, IntPtr.Zero, 0, 0, 0, 0, 0x0037); // FRAMECHANGED, NOMOVE, NOSIZE, NOZORDER, NOACTIVATE.
        }

        // Only the exposed perimeter belongs to the form. Wider corner zones
        // along that perimeter make diagonal resizing easier without eating clicks.
        internal static int HitTest(Point point, Size size, int edge)
        {
            if (edge <= 0 || point.X < 0 || point.Y < 0 || point.X >= size.Width || point.Y >= size.Height) return 0;
            bool left = point.X < edge, right = point.X >= size.Width - edge;
            bool top = point.Y < edge, bottom = point.Y >= size.Height - edge;
            if (!left && !right && !top && !bottom) return 0;
            int corner = edge * 2;
            if (point.Y < corner && point.X < corner) return 13;
            if (point.Y < corner && point.X >= size.Width - corner) return 14;
            if (point.Y >= size.Height - corner && point.X < corner) return 16;
            if (point.Y >= size.Height - corner && point.X >= size.Width - corner) return 17;
            return left ? 10 : right ? 11 : top ? 12 : 15;
        }

        internal static void ApplyCorners(Form form, float scale)
        {
            bool normal = form.WindowState == FormWindowState.Normal;
            Region replacement = null;
            // A real window region rounds the custom frame on Windows 10 and 11.
            // Maximized windows remain rectangular and flush with the work area.
            if (normal && form.Width > 0 && form.Height > 0)
                using (GraphicsPath path = UiPaint.RoundedPath(new RectangleF(0, 0, form.Width, form.Height), 12 * scale))
                    replacement = new Region(path);
            Region previous = form.Region;
            form.Region = replacement;
            if (previous != null) previous.Dispose();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MinMaxInfo
        {
            public Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
        }

        internal static void SetMaximizedWorkArea(IntPtr window, IntPtr data)
        {
            if (data == IntPtr.Zero) return;
            Screen screen = Screen.FromHandle(window);
            Rectangle area = screen.WorkingArea, monitor = screen.Bounds;
            MinMaxInfo info = (MinMaxInfo)Marshal.PtrToStructure(data, typeof(MinMaxInfo));
            info.MaxPosition = new Point(area.Left - monitor.Left, area.Top - monitor.Top);
            info.MaxSize = new Point(area.Width, area.Height);
            Marshal.StructureToPtr(info, data, false);
        }
    }

    // A visible, larger target inside the rounded corner. Mouse-down hands the
    // gesture to the owner's native resize loop, which retains capture off-window.
    internal sealed class WindowResizeGrip : Control
    {
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

        internal WindowResizeGrip()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = UiPalette.Canvas; Cursor = Cursors.SizeNWSE; TabStop = false;
            AccessibleName = "拖动右下角调整窗口大小";
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Form owner = FindForm();
            if (e.Button != MouseButtons.Left || owner == null || owner.WindowState != FormWindowState.Normal) return;
            Point screen = Cursor.Position;
            ReleaseCapture();
            SendMessage(owner.Handle, 0xA1, new IntPtr(17), new IntPtr(unchecked((screen.Y << 16) | (screen.X & 0xffff))));
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            float scale = Width / 24f;
            using (Pen pen = new Pen(UiPalette.Muted, Math.Max(1, scale)))
                for (int inset = 5; inset <= 13; inset += 4)
                    e.Graphics.DrawLine(pen, (24 - inset) * scale, 20 * scale, 20 * scale, (24 - inset) * scale);
        }
    }
}
