using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using RouteLens;

internal static class WindowChromeChecks
{
    private static int checks;
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly string[] Labels = { "连接", "实时流量", "历史流量", "世界地图", "隐私检测", "离线地区", "提醒", "证据", "行为调查" };
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    private static T Field<T>(object owner, string name) { return (T)owner.GetType().GetField(name, Private).GetValue(owner); }
    private static IEnumerable<Control> Children(Control parent) { return parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Children(c))); }
    private static void Pump(int milliseconds)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(5); }
        Application.DoEvents();
    }
    private static int Hit(MainForm main, Point clientPoint)
    {
        Point screen = main.PointToScreen(clientPoint);
        int packed = unchecked((screen.Y << 16) | (screen.X & 0xffff));
        return SendMessage(main.Handle, 0x84, IntPtr.Zero, new IntPtr(packed)).ToInt32();
    }
    private static KeyValuePair<Point, int>[] Targets(MainForm main)
    {
        int inset = Math.Max(3, main.Padding.Left / 2), right = main.ClientSize.Width - 1 - inset, bottom = main.ClientSize.Height - 1 - inset;
        return new[] {
            new KeyValuePair<Point, int>(new Point(inset, main.ClientSize.Height / 2), 10),
            new KeyValuePair<Point, int>(new Point(right, main.ClientSize.Height / 2), 11),
            new KeyValuePair<Point, int>(new Point(main.ClientSize.Width / 2, inset), 12),
            new KeyValuePair<Point, int>(new Point(main.ClientSize.Width / 2, bottom), 15),
            new KeyValuePair<Point, int>(new Point(inset, inset), 13),
            new KeyValuePair<Point, int>(new Point(right, inset), 14),
            new KeyValuePair<Point, int>(new Point(inset, bottom), 16),
            new KeyValuePair<Point, int>(new Point(right, bottom), 17)
        };
    }
    private static void CheckEdges(MainForm main, string context)
    {
        main.BringToFront(); main.Activate(); Pump(40);
        foreach (KeyValuePair<Point, int> target in Targets(main))
        {
            Check(Hit(main, target.Key) == target.Value, context + ": wrong resize direction at " + target.Key);
            // Sending directly to the parent alone would miss the original bug:
            // a docked child HWND covered the perimeter and received the real mouse.
            IntPtr actual = WindowFromPoint(main.PointToScreen(target.Key));
            Control grip = main.Controls.Cast<Control>().First(c => c is WindowResizeGrip);
            Check(actual == main.Handle || (target.Value == 17 && actual == grip.Handle),
                context + ": a child or another window covers resize target " + target.Key);
        }
        Point content = new Point(main.ClientSize.Width / 2, main.ClientSize.Height / 2);
        Check((GetWindowLong(main.Handle, -16) & 0x00040000) != 0, context + ": native resize style was lost");
        Control handle = main.Controls.Cast<Control>().First(c => c is WindowResizeGrip);
        Check(handle.Visible && handle.Cursor == Cursors.SizeNWSE, context + ": resize grip is unavailable");
        Check(handle.Right == main.ClientSize.Width - 2 && handle.Bottom == main.ClientSize.Height - 2, context + ": resize grip did not follow client bounds");
        Check(WindowFromPoint(handle.PointToScreen(new Point(handle.Width / 2, handle.Height / 2))) == handle.Handle, context + ": resize grip is covered by page content");
        Check(Hit(main, content) == 1, context + ": content was treated as a resize grip");
        Check(WindowFromPoint(main.PointToScreen(content)) != main.Handle, context + ": child content was unexpectedly removed");
    }
    private static void CheckGeometry()
    {
        Size size = new Size(1040, 720);
        Check(WindowChrome.HitTest(new Point(-1, 100), size, 8) == 0, "Outside left must not resize");
        Check(WindowChrome.HitTest(new Point(1040, 100), size, 8) == 0, "Outside right must not resize");
        Check(WindowChrome.HitTest(new Point(100, -1), size, 8) == 0, "Outside top must not resize");
        Check(WindowChrome.HitTest(new Point(100, 720), size, 8) == 0, "Outside bottom must not resize");
        Check(WindowChrome.HitTest(new Point(10, 10), size, 8) == 0, "Wider corner zone must not steal content clicks");
        Check(WindowChrome.HitTest(new Point(4, 15), size, 8) == 13, "Left corner extension must allow diagonal resize");
        Check(WindowChrome.HitTest(new Point(15, 4), size, 8) == 13, "Top corner extension must allow diagonal resize");
        Check(WindowChrome.HitTest(new Point(4, 16), size, 8) == 10, "Left side after corner zone must resize horizontally");
        Check(WindowChrome.HitTest(new Point(16, 4), size, 8) == 12, "Top side after corner zone must resize vertically");
        Check(WindowChrome.HitTest(new Point(0, 0), size, 0) == 0, "Zero border must not resize");
    }
    private static void CheckCorners(MainForm main)
    {
        Check(main.Region != null, "Normal window must have a rounded Region");
        Check(!main.Region.IsVisible(0, 0), "Top-left corner is still square");
        Check(!main.Region.IsVisible(main.Width - 1, 0), "Top-right corner is still square");
        Check(!main.Region.IsVisible(0, main.Height - 1), "Bottom-left corner is still square");
        Check(!main.Region.IsVisible(main.Width - 1, main.Height - 1), "Bottom-right corner is still square");
        Check(main.Region.IsVisible(main.Width / 2, main.Height / 2), "Rounded region removed content");
    }
    private static void Save(Form form, string name)
    {
        string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "previews"); Directory.CreateDirectory(directory);
        using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
        { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(directory, name), ImageFormat.Png); }
    }
    private static void DisableCollectors(MainForm main)
    {
        FieldInfo shownKey = typeof(Form).GetFields(BindingFlags.Static | BindingFlags.NonPublic).Single(f => f.Name.Equals("EVENT_SHOWN", StringComparison.OrdinalIgnoreCase));
        EventHandlerList events = (EventHandlerList)typeof(Component).GetProperty("Events", Private).GetValue(main, null);
        object eventKey = shownKey.GetValue(null); events.RemoveHandler(eventKey, events[eventKey]);
        Field<DnsLeakMonitor>(main, "_dns").Dispose(); Field<CheckBox>(main, "_online").Checked = false;
    }
    private static void SyntheticSnapshot(MainForm main)
    {
        main.SetSnapshotForTest(new NetworkSnapshot { CapturedAt = DateTime.UtcNow, ClashConnected = true,
            SourceName = "Clash / Mihomo", ControllerSummary = "窗口验证：虚构数据", SourceStatus = "窗口验证：真实采集已禁用",
            Connections = new List<ConnectionInfo> { new ConnectionInfo { Id = "window-fixture", Protocol = "TCP", ProcessName = "缩放示例.exe", ProcessPath = @"C:\Example\缩放示例.exe",
                Host = "resize.example.test", LocalAddress = "192.0.2.10", LocalPort = 50001, RemoteAddress = "198.51.100.20", RemotePort = 443,
                DataSource = "Clash / Mihomo", HasTrafficCounters = true, UploadBytes = 2000, DownloadBytes = 6000, ProxyChain = "示例策略 → DIRECT", ProcessAttribution = "虚构元数据" } } });
    }
    private static void ShowBounds(MainForm main)
    {
        string bounds = "X=" + main.Left + " Y=" + main.Top + " · " + main.Width + " × " + main.Height + " · " + main.WindowState;
        main.Text = "RouteLens · 窗口缩放验证 · " + bounds;
        Field<Label>(main, "_status").Text = "虚构数据 · " + bounds;
    }
    [STAThread] private static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            bool interactive = args.Contains("--interactive") || Path.GetFileNameWithoutExtension(Application.ExecutablePath).EndsWith("Interactive", StringComparison.OrdinalIgnoreCase);
            using (GeoService geo = new GeoService(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "window-fixture-cache")))
            {
                geo.Enabled = false;
                using (MainForm main = new MainForm(geo))
                {
                    DisableCollectors(main);
                    main.Text = "RouteLens · 窗口缩放验证"; main.StartPosition = FormStartPosition.Manual;
                    main.Bounds = new Rectangle(80, 80, 1180, 780);
                    if (interactive)
                    {
                        main.Shown += delegate { SyntheticSnapshot(main); ShowBounds(main); };
                        main.Resize += delegate { ShowBounds(main); }; main.LocationChanged += delegate { ShowBounds(main); };
                        Application.Run(main); return 0;
                    }
                    CheckGeometry(); main.Show(); Pump(80); SyntheticSnapshot(main);
                    Check(main.Bounds == new Rectangle(80, 80, 1180, 780), "First display changed the requested window bounds: " + main.Bounds);
                    Check(main.Padding.Left >= 8 && main.Padding.All == main.Padding.Left, "No exposed perimeter for resize messages");
                    Check(main.ClientSize == main.Size, "Custom frame unexpectedly reduced the client area");
                    main.Enabled = false; main.Enabled = true; Pump(40);
                    Check((GetWindowLong(main.Handle, -16) & 0x00040000) != 0, "Updating window styles removed native sizing");
                    CheckCorners(main);
                    foreach (string label in Labels)
                    {
                        Children(main).OfType<Button>().First(c => c.Text == label).PerformClick(); Pump(230);
                        CheckEdges(main, label);
                    }
                    Size original = main.Size;
                    main.Size = new Size(300, 200); Pump(50);
                    Check(main.Width >= main.MinimumSize.Width && main.Height >= main.MinimumSize.Height, "Resize ignored minimum readable size");
                    CheckEdges(main, "minimum"); CheckCorners(main); Save(main, "window-rounded-minimum.png");
                    main.Size = original; Pump(50);
                    Rectangle normalBounds = main.Bounds;
                    for (int repeat = 0; repeat < 3; repeat++)
                    {
                        main.WindowState = FormWindowState.Maximized; Pump(150);
                        Rectangle workArea = Screen.FromHandle(main.Handle).WorkingArea;
                        Check(main.Bounds == workArea, "Maximize must fill work area without covering taskbar: " + main.Bounds + " vs " + workArea);
                        Check(main.Padding == Padding.Empty && main.Region == null, "Maximized window retained border gaps or corner cutouts");
                        foreach (KeyValuePair<Point, int> target in Targets(main))
                        {
                            int hit = Hit(main, target.Key);
                            Check(hit < 10 || hit > 17, "Maximized window still offers edge resizing");
                        }
                        main.WindowState = FormWindowState.Normal; Pump(100);
                        Check(main.Bounds == normalBounds, "Restore lost the normal window bounds: " + main.Bounds + " vs " + normalBounds);
                    }
                    CheckEdges(main, "restore"); CheckCorners(main);
                    main.WindowState = FormWindowState.Minimized; Pump(60);
                    main.WindowState = FormWindowState.Normal; Pump(100);
                    Check(main.Bounds == normalBounds, "Minimize/restore lost normal window bounds");
                    CheckEdges(main, "minimize restore"); CheckCorners(main);
                    Save(main, "window-rounded-restored.png");
                    main.Close(); Check(main.IsDisposed, "Test window did not close");
                }
            }
            Console.WriteLine("PASS " + checks + " window chrome checks; all eight real HWND edge targets checked on all nine pages. Synthetic data only."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
