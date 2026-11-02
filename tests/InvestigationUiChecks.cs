using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using RouteLens;

internal static class InvestigationUiChecks
{
    private static int checks;
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    private static T Field<T>(object owner, string name) { return (T)owner.GetType().GetField(name, Private).GetValue(owner); }
    private static void Call(object owner, string name, params object[] args) { owner.GetType().GetMethod(name, Private).Invoke(owner, args); }
    private static IEnumerable<Control> Children(Control root) { return root.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Children(c))); }
    private static Button Button(Form form, string name) { return Children(form).OfType<Button>().First(c => c.Text == name); }
    private static void Refresh(InvestigationDialog dialog) { Call(dialog, "RefreshData", true); Application.DoEvents(); }
    private static ConnectionInfo Row(long upload)
    {
        return new ConnectionInfo { Id = "fixture-connection", Protocol = "TCP", DataSource = "Clash / Mihomo", HasTrafficCounters = true,
            ProcessName = "示例同步.exe", ProcessPath = @"C:\Example\示例同步.exe", Host = "upload.example.test", RemoteAddress = "192.0.2.42", RemotePort = 443,
            LocalAddress = "192.0.2.1", LocalPort = 50000, UploadBytes = upload, DownloadBytes = 4096, ProxyChain = "示例策略 → DIRECT", ProcessAttribution = "虚构接口元数据" };
    }
    private static NetworkSnapshot Snapshot(int second)
    {
        return new NetworkSnapshot { CapturedAt = new DateTime(2020, 1, 1, 12, 0, 0, DateTimeKind.Utc).AddSeconds(second), ClashConnected = true,
            SourceName = "Clash / Mihomo", ControllerSummary = "虚构控制器", SourceStatus = "界面检查：全部为虚构数据", Connections = new List<ConnectionInfo> { Row(second * 2097152L) } };
    }
    private static void Layout(Form form)
    {
        foreach (Control c in Children(form).Where(c => c.Visible && (c is Button || c is NumericUpDown || c is CheckBox || c is ComboBox)))
            Check(c.Parent.ClientRectangle.Contains(c.Bounds), "Clipped control: " + c.Text + " / " + c.AccessibleName);
    }
    private static void Save(Form form, string name)
    {
        string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "previews"); Directory.CreateDirectory(directory);
        using (Bitmap bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(directory, name), ImageFormat.Png); }
    }
    [STAThread] private static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            using (GeoService geo = new GeoService(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fixture-cache")))
            {
                geo.Enabled = false;
                using (MainForm main = new MainForm(geo))
                {
                    // Disable startup sampling in this test executable only. The shipping
                    // application has no fixture data and retains its normal startup path.
                    FieldInfo shownKey = typeof(Form).GetFields(BindingFlags.Static | BindingFlags.NonPublic).Single(f => f.Name.Equals("EVENT_SHOWN", StringComparison.OrdinalIgnoreCase));
                    EventHandlerList events = (EventHandlerList)typeof(Component).GetProperty("Events", Private).GetValue(main, null);
                    object key = shownKey.GetValue(null); events.RemoveHandler(key, events[key]);
                    Field<DnsLeakMonitor>(main, "_dns").Dispose(); Field<CheckBox>(main, "_online").Checked = false;
                    main.Text = "RouteLens 集成检查 · 虚构数据"; main.StartPosition = FormStartPosition.Manual; main.Location = new Point(20, 20);
                    main.Show(); Application.DoEvents();
                    InvestigationMonitor monitor = Field<InvestigationMonitor>(main, "_investigation");
                    Check(!monitor.Enabled, "Investigation should default off");
                    Check(Button(main, "行为调查").Visible, "Main navigation missing");
                    monitor.Configure(true, 1048576, 2);
                    Field<TextBox>(main, "_search").Text = "not-a-match";
                    for (int i = 0; i <= 4; i++) main.SetSnapshotForTest(Snapshot(i));
                    Check(monitor.Snapshot().Length == 1, "MainForm did not feed unfiltered samples into investigation");
                    Check(Field<EvidenceMonitor>(main, "_evidence").Snapshot().Any(e => e.Source == "行为调查"), "Evidence monitor missed investigation");
                    Field<TextBox>(main, "_search").Clear(); main.Size = new Size(1040, 720); Application.DoEvents();
                    Check(Button(main, "行为调查").Parent.ClientRectangle.Contains(Button(main, "行为调查").Bounds), "Navigation clipped at minimum size");
                    Save(main, "main-minimum.png");
                    Button(main, "行为调查").PerformClick(); Application.DoEvents();
                    Form investigationPage = Field<Dictionary<string, Form>>(main, "_pages")["investigation"];
                    Check(investigationPage is InvestigationDialog && investigationPage.Visible && !investigationPage.TopLevel && main.Enabled,
                        "Native navigation did not switch to an embedded investigation page");
                    Button(main, "连接").PerformClick(); Application.DoEvents();
                    monitor.Interrupt("测试暂停；不是实际网络异常。");
                    int callbacks = 0;
                    using (InvestigationDialog dialog = new InvestigationDialog(monitor, delegate { callbacks++; }, delegate { callbacks++; }, delegate { callbacks++; }))
                    {
                        dialog.Text += " · 虚构数据界面检查";
                        dialog.Show(main); Refresh(dialog);
                        Check(!Field<CheckBox>(dialog, "_includePaths").Checked && !Field<CheckBox>(dialog, "_includeTargets").Checked && !Field<CheckBox>(dialog, "_includeNote").Checked, "Export must be private by default");
                        Check(Field<DataGridView>(dialog, "_grid").Rows.Count == 1, "Record list missing case");
                        string id = monitor.Snapshot()[0].Id;
                        Field<TextBox>(dialog, "_note").Text = "虚构核查备注"; Field<ComboBox>(dialog, "_review").SelectedIndex = 2;
                        Refresh(dialog); Check(Field<TextBox>(dialog, "_note").Text == "虚构核查备注", "Refresh discarded draft");
                        Call(dialog, "SaveReview"); Check(monitor.Find(id).ReviewNote == "虚构核查备注" && monitor.Find(id).ReviewStatus == "expected", "Review did not save");
                        Check(monitor.Find(id).Conclusion == "已观察到异常", "Review overwrote evidence conclusion");
                        Field<TextBox>(dialog, "_search").Text = "no-results"; Check(Field<DataGridView>(dialog, "_grid").Rows.Count == 0, "Search filter did not work");
                        Check(!Field<Button>(dialog, "_export").Enabled, "Export enabled without selected record");
                        Field<TextBox>(dialog, "_search").Clear();
                        Field<ComboBox>(dialog, "_filter").SelectedIndex = 1; Check(Field<DataGridView>(dialog, "_grid").Rows.Count == 0, "Review filter did not work");
                        Field<ComboBox>(dialog, "_filter").SelectedIndex = 0;
                        Button(dialog, "采集状态").PerformClick(); Button(dialog, "系统变更").PerformClick(); Button(dialog, "外部记录").PerformClick(); Check(callbacks == 3, "Existing tool callbacks not connected");
                        Layout(dialog); Save(dialog, "investigation-default.png");
                        dialog.Size = dialog.MinimumSize; Application.DoEvents(); Layout(dialog); Save(dialog, "investigation-minimum.png");
                        Field<CheckBox>(dialog, "_enabled").Checked = false; Call(dialog, "ApplySettings"); Check(!monitor.Enabled, "Apply observation setting failed");
                        dialog.Close();
                    }
                    Check(monitor.Find(id: monitor.Snapshot()[0].Id) != null, "Closing dialog erased records");
                    ConnectionInfo manual = Row(2500); manual.HasTrafficCounters = true; manual.DownloadBytes = 6800;
                    InvestigationCase snapshot = monitor.CaptureConnection(Snapshot(4), manual);
                    using (InvestigationDialog dialog = new InvestigationDialog(monitor, null, null, null))
                    {
                        dialog.Text += " · 虚构数据界面检查"; dialog.Show(main); Refresh(dialog);
                        Check(Field<TextBox>(dialog, "_facts").Text.Contains("2,500") && Field<TextBox>(dialog, "_facts").Text.Contains("未提供"), "Manual cumulative bytes or unknown PID omitted");
                        Check(snapshot.Samples.All(s => !s.UploadBytesPerSecond.HasValue), "Manual snapshot invented rate");
                        Save(dialog, "investigation-snapshot.png");
                        Field<TextBox>(dialog, "_facts").Focus();
                        for (int i = 0; i < 200; i++) { ConnectionInfo replacement = Row(123); replacement.ProcessName = "替代记录.exe"; monitor.CaptureConnection(Snapshot(4), replacement); }
                        Refresh(dialog);
                        Check(Field<TextBox>(dialog, "_facts").Text.Contains("替代记录.exe"), "Focused details remained stale after selected record eviction");
                        if (args.Contains("--preview")) { Application.Run(dialog); }
                        else dialog.Close();
                    }
                    main.Close();
                }
            }
            Console.WriteLine("PASS " + checks + " native integration/UI checks; fictional snapshots only, no network sampling or external tests."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
