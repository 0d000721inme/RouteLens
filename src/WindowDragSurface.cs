using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RouteLens
{
    // Only plain, non-interactive surfaces inside this main window move it.
    // Filtering queued client clicks also covers lazily created embedded pages.
    internal sealed class WindowDragSurface : IMessageFilter, IDisposable
    {
        private readonly Form owner;
        private bool disposed;
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

        internal WindowDragSurface(Form window)
        {
            owner = window;
            Application.AddMessageFilter(this);
            owner.Disposed += OwnerDisposed;
        }

        internal bool CanDrag(Control source, Point point)
        {
            if (disposed || source == null || !owner.Enabled || !source.Visible || !source.Enabled) return false;
            Type type = source.GetType();
            bool plain = type == typeof(Panel) || type == typeof(TableLayoutPanel) || type == typeof(FlowLayoutPanel) ||
                type == typeof(RoundedPanel) || type == typeof(Label) || source is Form;
            if (!plain || source.Cursor == Cursors.Hand || !source.ClientRectangle.Contains(point)) return false;
            // Disabled controls still occupy their space; clicking one is not blank space.
            if (source.GetChildAtPoint(point, GetChildAtPointSkip.Invisible) != null) return false;
            for (Control current = source; current != null; current = current.Parent)
            {
                if (current.Name == "windowBrand") return false; // Existing brand drag/double-click handlers.
                if (current is ButtonBase || current is TextBoxBase || current is ComboBox || current is UpDownBase ||
                    current is DataGridView || current is ListControl || current is ListView || current is TreeView ||
                    current is ScrollBar || current is TabControl || current is WorldMapControl || current is CountryBreakdown)
                    return false;
                if (current == owner) return true;
            }
            return false; // Other top-level windows and modal dialogs keep their own input.
        }

        public bool PreFilterMessage(ref Message message)
        {
            if (message.Msg != 0x201 || disposed || owner.IsDisposed) return false; // WM_LBUTTONDOWN only.
            Control source = Control.FromHandle(message.HWnd);
            long packed = message.LParam.ToInt64();
            Point point = new Point((short)(packed & 0xffff), (short)((packed >> 16) & 0xffff));
            if (!CanDrag(source, point)) return false;
            Point screen = source.PointToScreen(point);
            ReleaseCapture();
            SendMessage(owner.Handle, 0xA1, new IntPtr(2), new IntPtr(unchecked((screen.Y << 16) | (screen.X & 0xffff))));
            return true;
        }

        private void OwnerDisposed(object sender, EventArgs e) { Dispose(); }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true; Application.RemoveMessageFilter(this); owner.Disposed -= OwnerDisposed;
        }
    }
}
