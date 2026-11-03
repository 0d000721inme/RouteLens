using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using RouteLens;

// Interactive fixture: the operator must perform the drag. This program only
// observes messages and bounds; it never injects input or simulates a successful
// resize by assigning Size/Bounds after startup.
internal static class WindowResizeInteractionChecks
{
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object owner, string name) { return (T)owner.GetType().GetField(name, Private).GetValue(owner); }

    private static void DisableCollectors(MainForm main)
    {
        FieldInfo shownKey = typeof(Form).GetFields(BindingFlags.Static | BindingFlags.NonPublic).Single(f => f.Name.Equals("EVENT_SHOWN", StringComparison.OrdinalIgnoreCase));
        EventHandlerList events = (EventHandlerList)typeof(Component).GetProperty("Events", Private).GetValue(main, null);
        object eventKey = shownKey.GetValue(null); events.RemoveHandler(eventKey, events[eventKey]);
        Field<DnsLeakMonitor>(main, "_dns").Dispose(); Field<CheckBox>(main, "_online").Checked = false;
    }

    private sealed class ResizeObserver : NativeWindow, IDisposable
    {
        private readonly MainForm form;
        private bool updating;
        private readonly Timer debugTimer = new Timer();
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
        private int nonClientDown, lastHit, sizeCommands, enters, exits, sizing, changes;

        internal ResizeObserver(MainForm target)
        {
            form = target;
            AssignHandle(form.Handle);
            form.Resize += Resized;
            form.LocationChanged += Moved;
            UpdateTitle();
            debugTimer.Interval = 200; debugTimer.Tick += delegate { UpdateTitle(); }; debugTimer.Start();
        }

        private void Resized(object sender, EventArgs e) { changes++; UpdateTitle(); }
        private void Moved(object sender, EventArgs e) { UpdateTitle(); }

        internal void Reset()
        {
            nonClientDown = lastHit = sizeCommands = enters = exits = sizing = changes = 0;
            UpdateTitle();
        }

        private void UpdateTitle()
        {
            if (updating || form.IsDisposed) return;
            updating = true;
            try
            {
                string status = form.Width + "x" + form.Height + " @" + form.Left + "," + form.Top + " " + form.WindowState +
                    " | Down=" + nonClientDown + "/HT" + lastHit + " Size=" + sizeCommands + " Enter=" + enters +
                    " Exit=" + exits + " Sizing=" + sizing + " Changes=" + changes;
                Control grip = form.Controls.Cast<Control>().FirstOrDefault(c => c is WindowResizeGrip);
                if (grip != null) status += " Grip=" + grip.Bounds + " Client=" + form.ClientSize;
                status += " Cursor=" + form.PointToClient(Cursor.Position) + " Style=" + GetWindowLong(form.Handle, -16).ToString("X");
                form.Text = "RouteLens · 拖拽验证 | " + status;
                Field<Label>(form, "_status").Text = "仅虚构数据 · " + status;
            }
            finally { updating = false; }
        }

        protected override void WndProc(ref Message message)
        {
            bool changed = true;
            switch (message.Msg)
            {
                case 0xA1: nonClientDown++; lastHit = message.WParam.ToInt32(); break; // WM_NCLBUTTONDOWN
                case 0x112:
                    if ((message.WParam.ToInt64() & 0xfff0) == 0xf000) sizeCommands++;
                    else changed = false;
                    break;
                case 0x231: enters++; break; // WM_ENTERSIZEMOVE
                case 0x232: exits++; break; // WM_EXITSIZEMOVE
                case 0x214: sizing++; break; // WM_SIZING
                default: changed = false; break;
            }
            if (changed) UpdateTitle();
            base.WndProc(ref message);
            if (changed) UpdateTitle();
        }

        public void Dispose()
        {
            debugTimer.Stop(); debugTimer.Dispose();
            form.Resize -= Resized;
            form.LocationChanged -= Moved;
            ReleaseHandle();
        }
    }

    [STAThread] private static void Main()
    {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        using (GeoService geo = new GeoService(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "resize-interaction-cache")))
        {
            geo.Enabled = false;
            using (MainForm main = new MainForm(geo))
            {
                DisableCollectors(main);
                main.StartPosition = FormStartPosition.Manual;
                main.Bounds = new Rectangle(80, 80, 1180, 780);
                using (ResizeObserver observer = new ResizeObserver(main))
                {
                    main.Shown += delegate
                    {
                        main.SetSnapshotForTest(new NetworkSnapshot { CapturedAt = DateTime.UtcNow, ClashConnected = true,
                            SourceName = "Clash / Mihomo", ControllerSummary = "交互验证：虚构数据", SourceStatus = "真实采集已禁用",
                            Connections = new List<ConnectionInfo> { new ConnectionInfo { Id = "resize-interaction", Protocol = "TCP",
                                ProcessName = "拖拽示例.exe", Host = "resize.example.test", RemoteAddress = "198.51.100.20", RemotePort = 443,
                                DataSource = "Clash / Mihomo", HasTrafficCounters = true, UploadBytes = 2000, DownloadBytes = 6000,
                                ProxyChain = "示例策略 → DIRECT", ProcessAttribution = "虚构元数据" } } });
                        observer.Reset();
                    };
                    main.KeyDown += delegate(object sender, KeyEventArgs e)
                    {
                        if (e.Control && e.KeyCode == Keys.R) { observer.Reset(); e.SuppressKeyPress = true; }
                    };
                    Application.Run(main);
                }
            }
        }
    }
}
