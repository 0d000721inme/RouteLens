using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using RouteLens;

// Test the shipped executable's real HWNDs and paint handlers. Window sizes
// come from SetWindowPos so this fixture never injects mouse or keyboard input.
internal static class ResizePerformanceChecks
{
    const int FrameCount = 48;
    const int NoSize = 1, NoMove = 2, NoZOrder = 4, NoActivate = 16;
    const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

    [StructLayout(LayoutKind.Sequential)]
    struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int width, int height, int flags);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int message, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool RedrawWindow(IntPtr h, IntPtr invalid, IntPtr region, uint flags);

    static T Field<T>(MainForm form, string name)
    { return (T)typeof(MainForm).GetField(name, Private).GetValue(form); }

    static void Check(bool ok, string message)
    { if (!ok) throw new InvalidOperationException(message); }

    static void DrainPaint(MainForm form)
    {
        // Deliver already-pending paints, including child HWNDs. Do not force a
        // full repaint. This measures UI work, not DWM presentation latency.
        RedrawWindow(form.Handle, IntPtr.Zero, IntPtr.Zero, 0x0100 | 0x0080);
        Application.DoEvents();
    }

    static void Settle(MainForm form)
    {
        var timer = Stopwatch.StartNew();
        do { DrainPaint(form); Thread.Sleep(8); } while (timer.ElapsedMilliseconds < 400);
    }

    static void WaitForWindowState(MainForm form, FormWindowState expected, string operation)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < 2000)
        {
            Application.DoEvents();
            if (form.WindowState == expected) return;
            Thread.Sleep(8);
        }

        throw new InvalidOperationException(operation + " timed out: state=" + form.WindowState +
            ", bounds=" + form.Bounds + ", moving=" + Field<bool>(form, "_movingOrSizing") +
            ", nativeSizing=" + Field<bool>(form, "_nativeSizing") +
            ", preview=" + Field<bool>(form, "_resizePreviewActive") +
            ", cachedChromeSize=" + Field<Size>(form, "_chromeSize"));
    }

    static void DisableCollectors(MainForm form)
    {
        var key = typeof(Form).GetFields(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(f => f.Name.Equals("EVENT_SHOWN", StringComparison.OrdinalIgnoreCase)).GetValue(null);
        var events = (EventHandlerList)typeof(Component).GetProperty("Events", Private).GetValue(form, null);
        events.RemoveHandler(key, events[key]);
        MethodInfo quotaProvider = typeof(MainForm).GetMethod("SetSubscriptionQuotaProviderForTest", Private);
        if (quotaProvider != null)
        {
            Type providerType = quotaProvider.GetParameters()[0].ParameterType;
            Type arrayType = providerType.GetMethod("Invoke").ReturnType;
            Array empty = Array.CreateInstance(arrayType.GetElementType(), 0);
            Delegate provider = Expression.Lambda(providerType, Expression.Constant(empty, arrayType)).Compile();
            quotaProvider.Invoke(form, new object[] { provider });
        }
        Field<DnsLeakMonitor>(form, "_dns").Dispose();
        Field<CheckBox>(form, "_online").Checked = false;
        Field<CheckBox>(form, "_publicOnly").Checked = false;
    }

    static NetworkSnapshot Snapshot()
    {
        var rows = new List<ConnectionInfo>();
        for (int i = 1; i <= 80; i++)
            rows.Add(new ConnectionInfo { Id = "synthetic-" + i, DataSource = "Clash / Mihomo", Protocol = "TCP",
                ProcessName = "fixture.exe", Host = "site" + i + ".example.test", RemoteAddress = "192.0.2." + i,
                RemotePort = 443, LocalPort = 50000 + i, HasTrafficCounters = true, UploadBytes = 1000, DownloadBytes = 2000,
                RouteType = "代理", ProxyChain = "Fixture node" });
        return new NetworkSnapshot { SourceName = "Clash / Mihomo", ControllerSummary = "fixture", SourceStatus = "synthetic",
            CapturedAt = DateTime.UtcNow, ClashConnected = true, Connections = rows };
    }

    static IEnumerable<Control> Children(Control owner)
    {
        foreach (Control child in owner.Controls)
        {
            yield return child;
            foreach (Control grandchild in Children(child)) yield return grandchild;
        }
    }

    static void CheckCorner(MainForm form)
    {
        var corner = form.PointToScreen(new Point(form.ClientSize.Width - 5, form.ClientSize.Height - 5));
        int packed = unchecked((corner.Y << 16) | (corner.X & 0xffff));
        Check(SendMessage(form.Handle, 0x84, IntPtr.Zero, new IntPtr(packed)).ToInt32() == 17, "Bottom-right hit test failed");
    }

    static void CheckCaptureIsAppOnly(MainForm form)
    {
        Point sample = new Point(40, 100);
        using (Form overlay = new Form { FormBorderStyle = FormBorderStyle.None, BackColor = Color.Magenta,
            ShowInTaskbar = false, TopMost = true, StartPosition = FormStartPosition.Manual, Size = new Size(24, 24) })
        {
            overlay.Location = form.PointToScreen(sample);
            overlay.Show(); overlay.BringToFront(); Application.DoEvents();
            MethodInfo capture = typeof(MainForm).GetMethod("CaptureResizePreview", Private);
            using (Bitmap frame = (Bitmap)capture.Invoke(form, null))
            {
                Check(frame != null, "Visible synthetic UI did not produce a resize preview frame");
                Check(frame.GetPixel(sample.X, sample.Y).ToArgb() != Color.Magenta.ToArgb(),
                    "Resize preview copied an overlapping window instead of RouteLens content");
            }
        }
        form.BringToFront(); form.Activate(); Application.DoEvents();
    }

    static void BeginNativeSizing(MainForm form)
    {
        BeginNativeSizing(form, form.Width + 1, form.Height + 1);
    }

    static void BeginNativeSizing(MainForm form, int width, int height)
    {
        NativeRect proposed = new NativeRect { Left = form.Left, Top = form.Top,
            Right = form.Left + width, Bottom = form.Top + height };
        IntPtr memory = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NativeRect)));
        try
        {
            Marshal.StructureToPtr(proposed, memory, false);
            SendMessage(form.Handle, 0x0214, new IntPtr(8), memory); // WM_SIZING / WMSZ_BOTTOMRIGHT
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    static void ResizeAt(MainForm form, int width, int height)
    {
        SendMessage(form.Handle, 0x0231, IntPtr.Zero, IntPtr.Zero); // WM_ENTERSIZEMOVE
        typeof(MainForm).GetMethod("OnResizeBegin", Private).Invoke(form, new object[] { EventArgs.Empty });
        BeginNativeSizing(form, width, height);
        Check(Field<bool>(form, "_resizePreviewActive"), "Breakpoint resize did not enter static preview mode");
        Check(SetWindowPos(form.Handle, IntPtr.Zero, form.Left, form.Top, width, height,
            NoZOrder | NoActivate), "Adaptive-layout native resize failed");
        DrainPaint(form);
        SendMessage(form.Handle, 0x0232, IntPtr.Zero, IntPtr.Zero); // WM_EXITSIZEMOVE
        typeof(MainForm).GetMethod("OnResizeEnd", Private).Invoke(form, new object[] { EventArgs.Empty });
        DrainPaint(form);
        Check(!Field<bool>(form, "_resizePreviewActive"), "Breakpoint resize preview did not finish");
    }

    static void CheckAdaptiveBreakpoints(MainForm form)
    {
        float scale = Field<float>(form, "_dpiScale");
        int aboveWidth = (int)Math.Ceiling(1320 * scale);
        int belowWidth = (int)Math.Floor(1270 * scale);
        int tallHeight = (int)Math.Ceiling(850 * scale);
        int middleHeight = (int)Math.Ceiling(780 * scale);
        int shortHeight = (int)Math.Ceiling(740 * scale);

        ResizeAt(form, aboveWidth, tallHeight);
        Check(Field<Control>(form, "_inspector").Visible, "Inspector did not appear above the 1280 logical-width breakpoint");
        Check(Math.Abs(Field<TableLayoutPanel>(form, "_rootLayout").RowStyles[2].Height - 300 * scale) < 0.2f,
            "Overview height did not adapt above the 840 logical-height breakpoint");

        ResizeAt(form, belowWidth, middleHeight);
        Check(!Field<Control>(form, "_inspector").Visible, "Inspector remained visible below the 1280 logical-width breakpoint");
        Check(Math.Abs(Field<TableLayoutPanel>(form, "_rootLayout").RowStyles[2].Height - 260 * scale) < 0.2f,
            "Overview height did not adapt across the 760 logical-height breakpoint");

        ResizeAt(form, aboveWidth, shortHeight);
        Check(Field<Control>(form, "_inspector").Visible, "Inspector did not reappear after crossing the width breakpoint again");
        Check(Math.Abs(Field<TableLayoutPanel>(form, "_rootLayout").RowStyles[2].Height - 216 * scale) < 0.2f,
            "Overview height did not return below the 760 logical-height breakpoint");

        ResizeAt(form, form.MinimumSize.Width + 40, shortHeight);
        Check(!Field<Control>(form, "_inspector").Visible, "Inspector remained visible after restoring a narrow window");
    }

    static void Measure(MainForm form, string page)
    {
        var view = Children(form).OfType<WorldMapControl>().First(c => c.Visible);
        Settle(form);
        Rectangle initial = form.Bounds;
        CheckCorner(form);
        int paints = 0;
        PaintEventHandler count = delegate { paints++; };
        view.Paint += count;
        try
        {
            var elapsed = new double[FrameCount];
            SendMessage(form.Handle, 0x0231, IntPtr.Zero, IntPtr.Zero); // WM_ENTERSIZEMOVE
            typeof(MainForm).GetMethod("OnResizeBegin", Private).Invoke(form, new object[] { EventArgs.Empty });
            int paintsBeforePreview = paints;
            BeginNativeSizing(form);
            Check(Field<bool>(form, "_resizePreviewActive"), "Native sizing did not enter static preview mode");
            Check(!Field<Control>(form, "_shell").Visible, "Application UI remained visible over the resize preview");
            Check(Field<Bitmap>(form, "_resizePreviewFrame") != null, "Resize preview did not retain its captured frame");
            CheckCorner(form);
            try
            {
                for (int i = 0; i < elapsed.Length; i++)
                {
                    int delta = (i < FrameCount / 2 ? i + 1 : FrameCount - 1 - i) * 6;
                    int w = initial.Width + delta, h = initial.Height + delta / 2;
                    var clock = Stopwatch.StartNew();
                    Check(SetWindowPos(form.Handle, IntPtr.Zero, 0, 0, w, h, NoMove | NoZOrder | NoActivate), "Native resize failed");
                    DrainPaint(form);
                    Check(Field<bool>(form, "_resizePreviewActive"), "Static preview ended before native sizing completed");
                    Check(paints == paintsBeforePreview, "Map repainted during the static resize preview");
                    elapsed[i] = clock.Elapsed.TotalMilliseconds;
                    Check(form.Width == w && form.Height == h && form.Location == initial.Location, "Bottom-right bounds changed unexpectedly");
                    if (i == 1) Check(form.Region == null, "Resize loop rebuilt the rounded region per frame. native=" +
                        Field<bool>(form, "_nativeSizing") + "; moving=" + Field<bool>(form, "_movingOrSizing") + "; size=" + form.Size +
                        "; cached=" + Field<Size>(form, "_chromeSize"));
                }
            }

            finally
            {
                var finish = Stopwatch.StartNew();
                SendMessage(form.Handle, 0x0232, IntPtr.Zero, IntPtr.Zero); // WM_EXITSIZEMOVE
                typeof(MainForm).GetMethod("OnResizeEnd", Private).Invoke(form, new object[] { EventArgs.Empty });
                DrainPaint(form);
                Check(form.Region != null, "Rounded region was not restored when native resizing ended");
                Check(!Field<bool>(form, "_resizePreviewActive"), "Static preview remained active after native sizing ended");
                Check(Field<Control>(form, "_shell").Visible, "Application UI was not restored after native sizing");
                Check(Field<Bitmap>(form, "_resizePreviewFrame") == null, "Captured resize preview was not released");
                Console.WriteLine("{0} resize release + pending paints: {1:F1} ms", page, finish.Elapsed.TotalMilliseconds);
            }
            Check(paints > 0, "Visible map was never painted");
            Array.Sort(elapsed);
            Console.WriteLine("{0}: {1} native size updates + pending paints, p50 {2:F1} ms, p95 {3:F1} ms, max {4:F1} ms, map paints {5}",
                page, FrameCount, elapsed[FrameCount / 2 - 1], elapsed[(int)Math.Ceiling(FrameCount * .95) - 1], elapsed.Last(), paints);
            Settle(form);
            var toggle = Stopwatch.StartNew();
            SendMessage(form.Handle, 0x0112, new IntPtr(0xf030), IntPtr.Zero); // SC_MAXIMIZE
            WaitForWindowState(form, FormWindowState.Maximized, "Maximize");
            DrainPaint(form);
            double maxMs = toggle.Elapsed.TotalMilliseconds;
            Check(form.WindowState == FormWindowState.Maximized && form.Bounds == Screen.FromHandle(form.Handle).WorkingArea, "Maximize bounds");
            toggle.Restart();
            SendMessage(form.Handle, 0x0112, new IntPtr(0xf120), IntPtr.Zero); // SC_RESTORE
            WaitForWindowState(form, FormWindowState.Normal, "Restore");
            DrainPaint(form);
            double restoreMs = toggle.Elapsed.TotalMilliseconds;
            Check(form.WindowState == FormWindowState.Normal && form.Bounds == initial,
                "Restore bounds: state=" + form.WindowState + ", actual=" + form.Bounds + ", expected=" + initial);
            CheckCorner(form);
            Console.WriteLine("{0} native maximize {1:F1} ms, restore {2:F1} ms", page, maxMs, restoreMs);
            Point moved = new Point(initial.X + 12, initial.Y + 8);
            Check(SetWindowPos(form.Handle, IntPtr.Zero, moved.X, moved.Y, 0, 0, NoSize | NoZOrder | NoActivate), "Move failed");
            DrainPaint(form);
            Check(form.Location == moved && form.Size == initial.Size, "Move must preserve size");
            Check(!Field<bool>(form, "_resizePreviewActive"), "Moving the window must not enter resize preview mode");
        }
        finally { view.Paint -= count; }
    }

    [STAThread] static int Main()
    {
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var geo = new GeoService(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fixture-geo")))
            using (var form = new MainForm(geo))
            {
                geo.Enabled = false;
                DisableCollectors(form);
                form.ShowInTaskbar = false; form.TopMost = true;
                form.StartPosition = FormStartPosition.Manual;
                var area = Screen.PrimaryScreen.WorkingArea;
                form.Bounds = new Rectangle(area.Left + 10, area.Top + 10, Math.Max(form.MinimumSize.Width, 1080), Math.Max(form.MinimumSize.Height, 740));
                form.Text = "RouteLens resize check (synthetic)";
                form.Show();
                form.SetSnapshotForTest(Snapshot());
                Check(Field<DataGridView>(form, "_grid").Rows.Count == 80, "Expected 80 synthetic connections");
                CheckCaptureIsAppOnly(form);
                Console.WriteLine("RouteLens {0}, starting bounds {1}, display {2}",
                    typeof(MainForm).Assembly.GetName().Version, form.Bounds, area);
                Measure(form, "connections");
                CheckAdaptiveBreakpoints(form);
                Field<Dictionary<string, ModernButton>>(form, "_navigation")["map"].PerformClick();
                Measure(form, "world-map");
                form.Close();
            }
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
