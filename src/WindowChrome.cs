using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RouteLens
{
    internal static class WindowChrome
    {
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
}
