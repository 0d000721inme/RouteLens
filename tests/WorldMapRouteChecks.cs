using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using RouteLens;

internal static class WorldMapRouteChecks
{
    private static int _checks;

    [STAThread]
    public static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            using (WorldMapControl map = new WorldMapControl { Size = new Size(820, 400) })
            using (Form host = new Form { ClientSize = map.Size, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
                Location = new Point(-2000, -2000) })
            {
                map.Dock = DockStyle.Fill;
                host.Controls.Add(map);
                host.Show();
                Application.DoEvents();
                map.ZoomIn();
                float originalZoom = map.ZoomFactor;
                map.SetRoute(null, null);
                Check(Field<bool>(map, "_routeMode"), "An empty route must remain in route mode.");
                Render(map);
                Check(Field<object>(map, "_routeCurve") == null, "Unlocated endpoints must not connect.");
                WorldMapRoutePoint source = new WorldMapRoutePoint("南通", "CN", new PointF(120.89f, 31.98f));
                WorldMapRoutePoint destination = new WorldMapRoutePoint("香港", "HK", new PointF(114.17f, 22.32f));
                map.SetRoute(source, null);
                Render(map);
                Check(Field<object>(map, "_routeCurve") == null, "A single endpoint must not connect.");
                map.SetRoute(source, destination);
                Check(map.ZoomFactor > 5, "Regional routes should receive a closer initial view.");
                Render(map);
                Check(Field<object>(map, "_routeCurve") != null, "Two distinct endpoints should connect.");
                Bitmap baseLayer = Field<Bitmap>(map, "_baseLayer");
                map.SetRouteTraffic(true, true);
                Check(Field<Timer>(map, "_routeTimer").Enabled == PageTransition.AnimationsEnabled, "Traffic must respect system animation preferences.");
                Render(map, args.Length > 0 ? args[0] : null);
                Check(Object.ReferenceEquals(baseLayer, Field<Bitmap>(map, "_baseLayer")), "A traffic frame must reuse cached geography.");
                map.Hide();
                Check(!Field<Timer>(map, "_routeTimer").Enabled, "Hiding the route must suspend animation.");
                map.Show();
                Check(Field<Timer>(map, "_routeTimer").Enabled == PageTransition.AnimationsEnabled, "Showing active traffic must restore permitted animation.");
                map.SetRouteTraffic(false, false);
                Check(!Field<Timer>(map, "_routeTimer").Enabled, "Idle traffic must stop animation.");
                map.SetRouteHops(new[] { source, null, destination, new WorldMapRoutePoint("Japan", "JP") });
                Render(map);
                List<PointF?> projected = Field<List<PointF?>>(map, "_projectedHops");
                Check(projected.Count == 4 && projected[0].HasValue && !projected[1].HasValue && projected[2].HasValue && projected[3].HasValue,
                    "Missing hops must be preserved in position between known country markers.");
                Check(Field<PointF?>(map, "_routeSourcePosition") == source.LongitudeLatitude, "An explicit endpoint must retain its supplied coordinates.");
                Check(Field<List<PointF?>>(map, "_routeHops")[0] != source.LongitudeLatitude, "A hop uses its country representative point even if a caller supplies a city.");
                map.SetRoute(new WorldMapRoutePoint("Tokyo", "JP"), destination);
                Check(Field<List<PointF?>>(map, "_routeHops").Count == 0, "Changing the route must discard previous hop observations.");
                map.ClearRoute();
                Check(!Field<bool>(map, "_routeMode") && Math.Abs(map.ZoomFactor - originalZoom) < .001f,
                    "Clearing route mode must restore the previous distribution viewport.");
                map.SetRoute(source, destination);
                map.SetRouteTraffic(true, true);
            }
            CheckPinnedPage();
            Console.WriteLine("PASS " + _checks + " route map checks");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object instance, string name)
    {
        return (T)instance.GetType().GetField(name, Private).GetValue(instance);
    }

    private static void CheckPinnedPage()
    {
        using (GeoService geo = new GeoService(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "route-test-cache")))
        using (MainForm main = new MainForm(geo))
        {
            // Drive the normal selection/navigation handlers without starting live network sampling.
            FieldInfo shown = typeof(Form).GetFields(BindingFlags.Static | BindingFlags.NonPublic).Single(f => f.Name.Equals("EVENT_SHOWN", StringComparison.OrdinalIgnoreCase));
            EventHandlerList events = (EventHandlerList)typeof(Component).GetProperty("Events", Private).GetValue(main, null);
            object key = shown.GetValue(null); events.RemoveHandler(key, events[key]);
            geo.Enabled = false; Field<DnsLeakMonitor>(main, "_dns").Dispose(); Field<CheckBox>(main, "_online").Checked = false;
            main.StartPosition = FormStartPosition.Manual; main.Location = new Point(-2400, -2000); main.Show();
            ConnectionInfo row = new ConnectionInfo { Id = "fixture", Host = "fixture.example", ProcessName = "fixture.exe",
                Protocol = "TCP", Scope = "公网", RemoteAddress = "203.0.113.10", RemotePort = 443, RouteType = "代理",
                OutboundAddress = "127.0.0.1", ProxyChain = "Fixture node", DataSource = "Clash / Mihomo", HasTrafficCounters = true };
            NetworkSnapshot snapshot = new NetworkSnapshot { ClashConnected = true, CapturedAt = DateTime.Now, SourceName = "fixture",
                Connections = new List<ConnectionInfo> { row } };
            Field<CheckBox>(main, "_publicOnly").Checked = false;
            main.SetSnapshotForTest(snapshot); Application.DoEvents();
            var grid = Field<DataGridView>(main, "_grid");
            Check(grid.Rows.Count == 1, "Selected connection available in actual grid");
            grid.CurrentCell = grid.Rows[0].Cells["process"];
            typeof(MainForm).GetMethod("ShowSelectedPath", Private).Invoke(main, null); Application.DoEvents();
            MapExplorerDialog page = (MapExplorerDialog)Field<Dictionary<string, Form>>(main, "_pages")["map"];
            ConnectionPathView view = Field<ConnectionPathView>(page, "_pathView");
            Button measure = Field<ModernButton>(view, "measure"); ListView hops = Field<ListView>(view, "hops");
            Check(view.Visible && measure.Enabled && measure.AccessibleDescription.Contains("127.0.0.1"), "Uses reported proxy endpoint");
            measure.PerformClick();
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < 4000 && !Field<Label>(view, "status").Text.Contains("已到达"))
            { Application.DoEvents(); System.Threading.Thread.Sleep(8); }
            Application.DoEvents();
            Check(hops.Items.Count == 1 && hops.Items[0].SubItems[1].Text == "127.0.0.1" && Field<Label>(view, "status").Text.Contains("已到达"),
                "Real loopback via pinned UI: rows=" + hops.Items.Count + ", status=" + Field<Label>(view, "status").Text + ", selectable=" + measure.CanSelect);
            typeof(MainForm).GetField("_paused", Private).SetValue(main, true);
            typeof(MainForm).GetMethod("RenderConnections", Private).Invoke(main, null);
            Check(!measure.Enabled && Field<Label>(view, "status").Text.Contains("暂停"), "Pause stops active path observation");
            typeof(MainForm).GetField("_paused", Private).SetValue(main, false);
            snapshot.Connections.Clear(); main.SetSnapshotForTest(snapshot);
            Check(!measure.Enabled && Field<Label>(view, "status").Text.Contains("固定快照"), "Disappeared connection has only snapshot state");
            snapshot.Connections.Add(row); main.SetSnapshotForTest(snapshot); measure.PerformClick();
            typeof(MainForm).GetMethod("ShowMapExplorer", Private).Invoke(main, null); Application.DoEvents();
            Check(!view.Visible && hops.Items.Count == 0 && Field<ConnectionInfo>(view, "pinned") == null &&
                !Field<bool>(Field<WorldMapControl>(view, "map"), "_routeMode"), "Distribution clears pinned connection and measured hops");
            main.Close();
        }
    }

    private static void Render(WorldMapControl map, string file = null)
    {
        using (Bitmap bitmap = new Bitmap(map.Width, map.Height))
        {
            map.DrawToBitmap(bitmap, new Rectangle(Point.Empty, map.Size));
            if (file != null) bitmap.Save(file, System.Drawing.Imaging.ImageFormat.Png);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks++;
    }
}
