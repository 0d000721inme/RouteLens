using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using RouteLens;

internal static class WindowDragChecks
{
    private static int checks;
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly string[] Pages = { "连接", "实时流量", "历史流量", "世界地图", "隐私检测", "离线地区", "提醒", "证据", "行为调查" };
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    private static T Field<T>(object owner, string name) { return (T)owner.GetType().GetField(name, Private).GetValue(owner); }
    private static IEnumerable<Control> Children(Control parent) { return parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Children(c))); }
    private static void Pump(int milliseconds)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(5); }
        Application.DoEvents();
    }
    private static void DisableCollectors(MainForm main)
    {
        FieldInfo shownKey = typeof(Form).GetFields(BindingFlags.Static | BindingFlags.NonPublic).Single(f => f.Name.Equals("EVENT_SHOWN", StringComparison.OrdinalIgnoreCase));
        EventHandlerList events = (EventHandlerList)typeof(Component).GetProperty("Events", Private).GetValue(main, null);
        object eventKey = shownKey.GetValue(null); events.RemoveHandler(eventKey, events[eventKey]);
        Field<DnsLeakMonitor>(main, "_dns").Dispose(); Field<CheckBox>(main, "_online").Checked = false;
    }
    private static Control Deepest(Control parent, Point screen)
    {
        Control next = parent.GetChildAtPoint(parent.PointToClient(screen), GetChildAtPointSkip.Invisible);
        return next == null ? parent : Deepest(next, screen);
    }
    private static void CheckPage(MainForm main, WindowDragSurface drag, string page)
    {
        Children(main).OfType<Button>().First(c => c.Text == page).PerformClick(); Pump(230);
        Point screen = main.PointToScreen(new Point(18, 200));
        Control blank = Deepest(main, screen);
        Check(blank != main, page + ": blank margin must actually be on the embedded content");
        Check(WindowFromPoint(screen) == blank.Handle, page + ": a different HWND covers the blank margin");
        Check(drag.CanDrag(blank, blank.PointToClient(screen)), page + ": visible blank margin cannot move the main window");
        Button navigation = Children(main).OfType<Button>().First(c => c.Text == page);
        Check(!drag.CanDrag(navigation, new Point(navigation.Width / 2, navigation.Height / 2)), page + ": navigation button was treated as blank space");
    }
    private static void CheckControlBoundaries(MainForm main, WindowDragSurface drag)
    {
        Panel harness = new Panel { Bounds = new Rectangle(120, 130, 380, 260), BackColor = Color.White };
        main.Controls.Add(harness); harness.BringToFront(); Pump(20);
        Check(drag.CanDrag(harness, new Point(350, 240)), "A plain panel must permit window movement");
        Control[] interactive = {
            new Button(), new CheckBox(), new TextBox(), new RichTextBox(), new ComboBox(), new NumericUpDown(),
            new LinkLabel(), new DataGridView(), new ListBox(), new ListView(), new TreeView(),
            new HScrollBar(), new VScrollBar(), new WorldMapControl(), new CountryBreakdown(), new WindowResizeGrip(), new PageTransition()
        };
        foreach (Control control in interactive)
        {
            control.Bounds = new Rectangle(10, 10, 140, 80); harness.Controls.Add(control); control.Show(); control.BringToFront(); Pump(5);
            Check(!drag.CanDrag(control, new Point(4, 4)), control.GetType().Name + " must keep its own mouse interaction");
            Message click = Message.Create(control.Handle, 0x201, new IntPtr(1), new IntPtr((4 << 16) | 4));
            Check(!drag.PreFilterMessage(ref click), control.GetType().Name + " click was intercepted by the move filter");
            harness.Controls.Remove(control); control.Dispose();
        }
        Label label = new Label { Bounds = new Rectangle(10, 10, 140, 24), Text = "Static display" }; harness.Controls.Add(label);
        Check(drag.CanDrag(label, new Point(4, 4)), "Static labels should permit window movement");
        Check(!drag.CanDrag(label, new Point(-1, 4)), "Outside-label coordinates must be rejected");
        label.Cursor = Cursors.Hand;
        Check(!drag.CanDrag(label, new Point(4, 4)), "A hand-cursor label must retain its custom interaction");
        label.Dispose();
        Button disabled = new Button { Bounds = new Rectangle(10, 10, 140, 24), Enabled = false }; harness.Controls.Add(disabled);
        Check(!drag.CanDrag(harness, new Point(20, 20)), "Disabled child controls must not become window drag targets");
        disabled.Dispose();
        Button parentButton = new Button { Bounds = new Rectangle(10, 10, 160, 100) }; harness.Controls.Add(parentButton);
        Panel buttonContent = new Panel { Bounds = new Rectangle(2, 2, 100, 60) }; parentButton.Controls.Add(buttonContent);
        Check(!drag.CanDrag(buttonContent, new Point(20, 20)), "A plain panel inside an interactive ancestor must not move the window");
        parentButton.Dispose();
        Panel brand = new Panel { Name = "windowBrand", Bounds = new Rectangle(10, 10, 160, 100) }; harness.Controls.Add(brand);
        Label brandLabel = new Label { Bounds = new Rectangle(2, 2, 100, 30) }; brand.Controls.Add(brandLabel);
        Check(!drag.CanDrag(brandLabel, new Point(20, 20)), "Existing brand drag/double-click handlers must remain independent");
        brand.Dispose();
        foreach (int messageId in new[] { 0xA1, 0xA3, 0x114, 0x115, 0x204, 0x20A })
        {
            Message other = Message.Create(harness.Handle, messageId, IntPtr.Zero, new IntPtr((200 << 16) | 200));
            Check(!drag.PreFilterMessage(ref other), "Non-client, scroll, right-click and wheel messages must not be intercepted");
        }
        Message unknown = Message.Create(new IntPtr(1), 0x201, new IntPtr(1), IntPtr.Zero);
        Check(!drag.PreFilterMessage(ref unknown), "Unknown native HWND must not fall back to a parent drag surface");
        harness.Dispose();
    }
    [STAThread] private static int Main()
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            using (GeoService geo = new GeoService(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "drag-fixture-cache")))
            {
                geo.Enabled = false;
                using (MainForm main = new MainForm(geo))
                {
                    DisableCollectors(main); main.StartPosition = FormStartPosition.Manual; main.Bounds = new Rectangle(80, 80, 1180, 780);
                    main.Show(); Pump(50);
                    WindowDragSurface drag = Field<WindowDragSurface>(main, "_dragSurface");
                    foreach (string page in Pages) CheckPage(main, drag, page);
                    CheckControlBoundaries(main, drag);
                    using (Form other = new Form { StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(520, 240, 300, 200) })
                    {
                        Panel blank = new Panel { Dock = DockStyle.Fill }; other.Controls.Add(blank); other.Show(); Pump(20);
                        Check(!drag.CanDrag(blank, new Point(20, 20)), "Another top-level window must not move the RouteLens main window");
                        Check(!drag.CanDrag(other, new Point(20, 20)), "Other forms must retain their own input");
                    }
                    main.Activate(); Pump(20);
                    Point screen = main.PointToScreen(new Point(18, 200)); Control finalBlank = Deepest(main, screen);
                    Check(drag.CanDrag(finalBlank, finalBlank.PointToClient(screen)), "Main window drag filter unexpectedly stopped");
                    drag.Dispose();
                    Check(!drag.CanDrag(finalBlank, finalBlank.PointToClient(screen)), "Disposed move filter is still active");
                    Message disposedClick = Message.Create(finalBlank.Handle, 0x201, new IntPtr(1), IntPtr.Zero);
                    Check(!drag.PreFilterMessage(ref disposedClick), "Disposed move filter still intercepts client clicks");
                    main.Close();
                }
            }
            using (Form owner = new Form())
            {
                WindowDragSurface owned = new WindowDragSurface(owner);
                owner.Dispose();
                Check(Field<bool>(owned, "disposed"), "Owner disposal must detach its application message filter");
            }
            Console.WriteLine("PASS " + checks + " blank-window drag boundary checks; nine pages, interactive exclusions and disposal. No mouse gesture was simulated by these checks.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
