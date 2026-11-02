using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using RouteLens;

internal static class PageNavigationChecks
{
    private static int checks;
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly string[] Labels = { "连接", "实时流量", "历史流量", "世界地图", "隐私检测", "离线地区", "提醒", "证据", "行为调查" };
    private static readonly string[] Keys = { "connections", "traffic", "history", "map", "privacy", "offline", "alerts", "evidence", "investigation" };
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    private static T Field<T>(object owner, string name) { return (T)owner.GetType().GetField(name, Private).GetValue(owner); }
    private static void Call(object owner, string name, params object[] args) { owner.GetType().GetMethod(name, Private).Invoke(owner, args); }
    private static IEnumerable<Control> Children(Control parent) { return parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Children(c))); }
    private static Button Button(Form main, string name) { return Children(main).OfType<Button>().First(c => c.Text == name); }
    private static void Pump(int milliseconds)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(5); }
        Application.DoEvents();
    }
    private static void Navigate(MainForm main, string label)
    {
        Button(main, label).PerformClick(); Pump(230);
    }
    private static void SendCommand(MainForm main, System.Windows.Forms.Keys keys)
    {
        object[] parameters = { Message.Create(main.Handle, 0x100, IntPtr.Zero, IntPtr.Zero), keys };
        bool handled = (bool)typeof(MainForm).GetMethod("ProcessCmdKey", Private).Invoke(main, parameters);
        Check(handled, "Navigation shortcut was not handled"); Pump(230);
    }
    private static void Save(Form form, string name)
    {
        string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "previews"); Directory.CreateDirectory(directory);
        using (Bitmap bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(directory, name), ImageFormat.Png); }
    }
    private static void CheckLayout(Form form, string page)
    {
        foreach (Control control in Children(form).Where(c => c.Visible && (c is Button || c is NumericUpDown || c is ComboBox)))
        {
            // Scrollable content may extend past its viewport intentionally.
            if (control.Parent is ScrollableControl && ((ScrollableControl)control.Parent).AutoScroll) continue;
            Check(control.Parent.ClientRectangle.Contains(control.Bounds), page + ": clipped action " + control.Text + " / " + control.AccessibleName + " bounds " + control.Bounds + " parent " + control.Parent.ClientRectangle);
        }
    }
    private static NetworkSnapshot Snapshot(int second)
    {
        return new NetworkSnapshot { CapturedAt = new DateTime(2020, 1, 1, 12, 0, 0, DateTimeKind.Utc).AddSeconds(second), ClashConnected = true,
            SourceName = "Clash / Mihomo", ControllerSummary = "虚构控制器", SourceStatus = "页面检查：虚构数据",
            Connections = new List<ConnectionInfo> { new ConnectionInfo { Id = "page-fixture", Protocol = "TCP", ProcessName = "页面示例.exe", ProcessPath = @"C:\Example\页面示例.exe", Host = "pages.example.test",
                LocalAddress = "192.0.2.10", LocalPort = 50001, RemoteAddress = "198.51.100.20", RemotePort = 443, DataSource = "Clash / Mihomo", HasTrafficCounters = true,
                UploadBytes = second * 2097152L, DownloadBytes = 2000, ProxyChain = "示例策略 → DIRECT", ProcessAttribution = "虚构元数据" } } };
    }
    [STAThread] private static int Main()
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            using (GeoService geo = new GeoService(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "page-fixture-cache")))
            {
                geo.Enabled = false;
                using (MainForm main = new MainForm(geo))
                {
                    // Suppress only this fixture's live collectors and startup work.
                    FieldInfo shownKey = typeof(Form).GetFields(BindingFlags.Static | BindingFlags.NonPublic).Single(f => f.Name.Equals("EVENT_SHOWN", StringComparison.OrdinalIgnoreCase));
                    EventHandlerList events = (EventHandlerList)typeof(Component).GetProperty("Events", Private).GetValue(main, null);
                    object eventKey = shownKey.GetValue(null); events.RemoveHandler(eventKey, events[eventKey]);
                    Field<DnsLeakMonitor>(main, "_dns").Dispose(); Field<CheckBox>(main, "_online").Checked = false;
                    main.Text = "RouteLens 页面检查 · 虚构数据"; main.StartPosition = FormStartPosition.Manual; main.Location = new Point(20, 20);
                    main.Show(); Pump(30);
                    Check(Field<string>(main, "_activePage") == "connections", "Initial page must remain connections");
                    InvestigationMonitor monitor = Field<InvestigationMonitor>(main, "_investigation"); monitor.Configure(true, 1048576, 2);
                    for (int i = 0; i <= 4; i++) main.SetSnapshotForTest(Snapshot(i));
                    monitor.Interrupt("页面测试停采；数据均为虚构。");
                    for (int i = 0; i < Labels.Length; i++)
                    {
                        Navigate(main, Labels[i]);
                        Check(Field<string>(main, "_activePage") == Keys[i], "Wrong active page: " + Labels[i]);
                        Check(main.Enabled, "Navigation disabled the main window: " + Labels[i]);
                        Check(Application.OpenForms.Cast<Form>().Count(f => f.TopLevel && f.Visible) == 1, "Navigation opened another top-level window: " + Labels[i]);
                        Check(Labels.Count(label => ((ModernButton)Button(main, label)).Primary) == 1 && ((ModernButton)Button(main, Labels[i])).Primary,
                            "Navigation selection is not exclusive: " + Labels[i]);
                        if (i > 0)
                        {
                            Form page = Field<Dictionary<string, Form>>(main, "_pages")[Keys[i]];
                            Check(!page.TopLevel && page.Visible && page.Parent == Field<Panel>(main, "_pageHost") && page.Dock == DockStyle.Fill,
                                "Page is not embedded: " + Labels[i]);
                            Check(!page.Modal && page.FormBorderStyle == FormBorderStyle.None, "Embedded page still has dialog chrome: " + Labels[i]);
                        }
                    }
                    Dictionary<string, Form> pages = Field<Dictionary<string, Form>>(main, "_pages");
                    Check(pages.Count == 8, "Each feature page should be cached once");
                    InvestigationDialog investigation = (InvestigationDialog)pages["investigation"];
                    string caseId = monitor.Snapshot()[0].Id;
                    Field<TextBox>(investigation, "_note").Text = "未保存的页面切换备注";
                    Field<ComboBox>(investigation, "_review").SelectedIndex = 1;
                    Field<TextBox>(investigation, "_search").Text = "页面示例";
                    Navigate(main, "实时流量"); Navigate(main, "行为调查");
                    Check(Object.ReferenceEquals(pages["investigation"], investigation), "Page switching recreated investigation");
                    Check(Field<TextBox>(investigation, "_note").Text == "未保存的页面切换备注" && Field<ComboBox>(investigation, "_review").SelectedIndex == 1,
                        "Page switching discarded review draft");
                    Check(Field<TextBox>(investigation, "_search").Text == "页面示例", "Page switching reset search");
                    Check(monitor.Find(caseId).ReviewNote == "", "Navigation implicitly saved draft");
                    Call(investigation, "SaveReview");
                    Check(monitor.Find(caseId).ReviewNote == "未保存的页面切换备注", "Review save failed after page switch");
                    Button(investigation, "采集状态").PerformClick(); Pump(230);
                    Check(Field<string>(main, "_activePage") == "evidence", "Investigation evidence link did not navigate within shell");
                    Navigate(main, "提醒");
                    ActivityAlertsDialog alertsPage = (ActivityAlertsDialog)pages["alerts"];
                    ActivityAlerts alerts = Field<ActivityAlerts>(main, "_alerts");
                    Field<CheckBox>(alertsPage, "_enabled").Checked = true;
                    Navigate(main, "连接"); Navigate(main, "提醒");
                    Check(!alerts.Enabled && Field<CheckBox>(alertsPage, "_enabled").Checked, "Navigation implicitly applied or lost alert settings");
                    Button(alertsPage, "保存设置").PerformClick(); Pump(30);
                    Check(alerts.Enabled && !alertsPage.IsDisposed && Field<string>(main, "_activePage") == "alerts", "Saving alerts closed embedded page");
                    Button(alertsPage, "返回连接").PerformClick(); Pump(230);
                    Check(Field<string>(main, "_activePage") == "connections" && !alertsPage.IsDisposed, "Return action did not retain cached page");
                    Navigate(main, "隐私检测");
                    Check(Field<Label>(pages["privacy"], "_status").Text.Contains("尚未开始"), "Merely opening privacy page started an external test");
                    Navigate(main, "行为调查"); Save(main, "pages-investigation-default.png");
                    SendCommand(main, System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.F);
                    Check(Field<TextBox>(investigation, "_search").Focused, "Ctrl+F did not focus current page search");
                    SendCommand(main, System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.Tab);
                    Check(Field<string>(main, "_activePage") == "connections", "Ctrl+Tab did not wrap to connections");
                    SendCommand(main, System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.Shift | System.Windows.Forms.Keys.Tab);
                    Check(Field<string>(main, "_activePage") == "investigation", "Ctrl+Shift+Tab did not return to investigation");
                    Navigate(main, "实时流量"); Save(main, "pages-traffic-default.png");
                    main.Size = new Size(1040, 720); Pump(30);
                    for (int i = 0; i < Labels.Length; i++) { Navigate(main, Labels[i]); CheckLayout(main, Labels[i]); }
                    Save(main, "pages-investigation-minimum.png");
                    for (int repeat = 0; repeat < 4; repeat++) foreach (string label in Labels) Button(main, label).PerformClick();
                    main.Size = new Size(1100, 760); Pump(400);
                    Check(Field<string>(main, "_activePage") == "investigation" && main.Enabled, "Rapid navigation or resize left wrong page");
                    Control transition = Field<Control>(main, "_pageTransition");
                    Check(!transition.Visible, "Transition overlay remained visible and blocked input");
                    Check(pages.Count == 8, "Repeated navigation leaked additional cached pages");
                    Form[] cached = pages.Values.ToArray(); main.Close();
                    Check(main.IsDisposed && cached.All(page => page.IsDisposed), "Closing main window did not dispose pages");
                }
            }
            Console.WriteLine("PASS " + checks + " embedded-page navigation checks; synthetic data only, live collectors disabled."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
