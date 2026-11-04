using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using RouteLens;

internal static class RenderSchedulingChecks
{
    private static readonly BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static int checks;
    private static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    private static T Field<T>(object value, string name) { return (T)value.GetType().GetField(name, Private).GetValue(value); }
    private static void Call(MainForm main, string method, params object[] args) { typeof(MainForm).GetMethod(method, Private).Invoke(main, args); }
    private static NetworkSnapshot Snapshot(string host)
    {
        return new NetworkSnapshot { CapturedAt = DateTime.UtcNow, ClashConnected = true, SourceName = "Clash / Mihomo",
            Connections = new List<ConnectionInfo> { new ConnectionInfo { Id = "render", ProcessName = "render-test.exe", Protocol = "TCP",
                Host = host, RemoteAddress = "198.51.100.20", RemotePort = 443, DataSource = "Clash / Mihomo", HasTrafficCounters = true, UploadBytes = 1000 } } };
    }
    [STAThread] private static int Main()
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            using (GeoService geo = new GeoService(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "render-fixture-cache")))
            {
                geo.Enabled = false;
                using (MainForm main = new MainForm(geo))
                {
                    FieldInfo shown = typeof(Form).GetFields(BindingFlags.Static | BindingFlags.NonPublic).Single(f => f.Name.Equals("EVENT_SHOWN", StringComparison.OrdinalIgnoreCase));
                    EventHandlerList events = (EventHandlerList)typeof(Component).GetProperty("Events", Private).GetValue(main, null);
                    object key = shown.GetValue(null); events.RemoveHandler(key, events[key]);
                    Field<DnsLeakMonitor>(main, "_dns").Dispose(); Field<CheckBox>(main, "_online").Checked = false;
                    main.StartPosition = FormStartPosition.Manual; main.Bounds = new Rectangle(60, 60, 1180, 780); main.Show();
                    main.SetSnapshotForTest(Snapshot("before.example.test")); Application.DoEvents();
                    DataGridView grid = Field<DataGridView>(main, "_grid");
                    int regions = 0, countryPaints = 0;
                    main.RegionChanged += delegate { regions++; };
                    grid.CellPainting += delegate(object sender, DataGridViewCellPaintingEventArgs e) { if (e.RowIndex >= 0 && e.ColumnIndex == grid.Columns["country"].Index) countryPaints++; };
                    for (int i = 0; i < 20; i++) { Call(main, "UpdateWindowChrome"); Call(main, "RenderView"); Application.DoEvents(); }
                    Check(regions == 0, "Unchanged chrome recreated the window Region");
                    Check(countryPaints == 0, "Unchanged data repainted the entire country column: " + countryPaints);
                    main.Width += 20; Application.DoEvents();
                    Check(regions == 1, "One resize should update the Region once, got " + regions);
                    Call(main, "OnResizeBegin", EventArgs.Empty);
                    main.SetSnapshotForTest(Snapshot("after.example.test"));
                    Check(Field<NetworkSnapshot>(main, "_snapshot").Connections[0].Host == "after.example.test", "Moving stopped snapshot acceptance");
                    Check(Convert.ToString(grid.Rows[0].Cells["host"].Value) == "before.example.test", "Moving still refreshed the connection grid");
                    Call(main, "OnResizeEnd", EventArgs.Empty); Application.DoEvents();
                    Check(Convert.ToString(grid.Rows[0].Cells["host"].Value) == "after.example.test", "Move end did not render the latest snapshot");
                    main.Close();
                }
            }
            Console.WriteLine("PASS " + checks + " render scheduling checks: 20 unchanged refreshes caused zero Region replacements and zero country-cell paints."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
