using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using RouteLens;

internal static class UsageStripChecks
{
    private const long MiB = 1048576, GiB = 1024 * MiB;
    private static int _checks;

    [STAThread] private static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();
            foreach (bool dark in new[] { false, true }) Run(args[0], dark);
            Console.WriteLine("Usage strip passed " + _checks + " checks with fictional traffic and plans.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void Run(string folder, bool dark)
    {
        UiPalette.SetMode(dark, null);
        string suffix = dark ? "-dark.png" : "-light.png";
        DailySiteTraffic daily = new DailySiteTraffic();
        TrafficTracker tracker = new TrafficTracker();
        DateTime start = DateTime.Today.AddHours(2);
        string[] hosts = { "bilibili.com", "youtube.com", "github.com" };
        string[] routes = { "直连", "代理", "未知" };
        long[] amounts = { 480, 240, 80 };
        for (int i = 0; i < 2; i++)
        {
            List<ConnectionInfo> rows = new List<ConnectionInfo>();
            for (int k = 0; k < hosts.Length; k++) rows.Add(new ConnectionInfo { Id = "fictional-" + k,
                Host = hosts[k], RouteType = routes[k], HasTrafficCounters = true, DownloadBytes = i * amounts[k] * MiB });
            tracker.Update(new NetworkSnapshot { CapturedAt = start.AddSeconds(i), SourceName = "Clash", ClashConnected = true, Connections = rows }, daily);
        }
        SubscriptionQuota first = new SubscriptionQuota { Id = "one", Name = "示例套餐", IsCurrent = true, Source = "Clash Verge",
            UploadBytes = 5 * GiB, DownloadBytes = 32 * GiB + (GiB / 2), TotalBytes = 150 * GiB };
        SubscriptionQuota second = new SubscriptionQuota { Id = "two", Name = "备用套餐", Source = "Mihomo", TotalBytes = 50 * GiB };
        using (Panel host = new Panel { Size = new Size(1040, 64) })
        using (UsageStrip strip = new UsageStrip(daily))
        {
            host.Controls.Add(strip); host.CreateControl(); strip.UpdateUsage(new[] { first, second }, false, true);
            Button plan = (Button)strip.GetControlFromPosition(0, 0), sites = (Button)strip.GetControlFromPosition(1, 0);
            Check(plan.AccessibleName.Contains("示例套餐") && plan.AccessibleName.Contains("112.5 GiB") &&
                plan.AccessibleName.Contains("37.5 GiB") && plan.AccessibleName.Contains("150 GiB"), "plan amount, used and remaining");
            Check(sites.AccessibleName.Contains("800 MiB") && sites.AccessibleName.Contains("代理 240 MiB") &&
                sites.AccessibleName.Contains("本次运行"), "visible scope and proxy subset");
            using (Bitmap image = new Bitmap(host.Width, host.Height))
            {
                host.DrawToBitmap(image, host.ClientRectangle);
                image.Save(Path.Combine(folder, "usage-strip" + suffix), ImageFormat.Png);
                Color[] brands = hosts.Select(UsageStrip.BrandColor).ToArray();
                int[] widths = MeasureStripe(image, sites.Bounds, brands);
                for (int i = 0; i < widths.Length; i++)
                    Check(widths[i] > 0 && Math.Abs(widths[i] - widths.Sum() * amounts[i] / 800d) <= 1.1, "rendered brand widths match measured usage " + hosts[i]);
                Check(brands[0].R > 190 && brands[0].B > 115 && brands[0].G < brands[0].B, "Bilibili uses pink");
                int[] planWidths = MeasureStripe(image, plan.Bounds, new[] { UiPalette.Accent, UiPalette.Border });
                Check(planWidths[0] > 0 && Math.Abs(planWidths[0] - planWidths.Sum() * .25) <= 1.1, "plan shows one quarter used");
            }
            using (ContextMenuStrip menu = strip.CreatePlanMenu())
            {
                Check(menu.Items.Count == 2 && ((ToolStripMenuItem)menu.Items[0]).Checked, "menu identifies current subscription");
                menu.Items[1].PerformClick();
                Check(plan.AccessibleName.Contains("备用套餐") && plan.AccessibleName.Contains("50 GiB"), "selectable second subscription");
            }
            using (ContextMenuStrip menu = strip.CreatePlanMenu())
            {
                menu.Items[0].PerformClick();
                Check(plan.AccessibleName.Contains("示例套餐"), "follow current subscription again");
            }
            InspectDetail(sites, folder, suffix);
            strip.UpdateUsage(new SubscriptionQuota[0], true, true);
            Check(sites.AccessibleName.Contains("已暂停") && sites.AccessibleName.Contains("800 MiB") &&
                plan.AccessibleName.Contains("暂无额度"), "pause keeps observed bytes and missing plan stays unavailable");
        }
    }

    private static int[] MeasureStripe(Bitmap image, Rectangle bounds, Color[] colors)
    {
        int[] best = new int[colors.Length];
        for (int y = bounds.Top; y < bounds.Bottom; y++)
        {
            int[] line = new int[colors.Length];
            for (int x = bounds.Left; x < bounds.Right; x++)
            {
                int pixel = image.GetPixel(x, y).ToArgb();
                for (int c = 0; c < colors.Length; c++) if (colors[c].ToArgb() == pixel) line[c]++;
            }
            if (line.Sum() > best.Sum()) best = line;
        }
        return best;
    }

    private static void InspectDetail(Button sites, string folder, string suffix)
    {
        Exception failure = null; bool captured = false;
        using (Timer timer = new Timer { Interval = 80 })
        {
            timer.Tick += delegate
            {
                Form dialog = Application.OpenForms.Cast<Form>().FirstOrDefault();
                if (dialog == null) return;
                timer.Stop();
                try
                {
                    Check(dialog.Text == "今日监测 · 站点流量", "site action opened the correct detail");
                    var grid = Descendants(dialog).OfType<DataGridView>().Single();
                    Check(grid.Rows.Count == 3 && grid.Columns.Count == 8 && (string)grid[0, 0].Value == "bilibili.com" &&
                        (string)grid[1, 0].Value == "480 MiB" && (string)grid[3, 0].Value == "480 MiB" &&
                        (string)grid[4, 1].Value == "240 MiB" && (string)grid[5, 2].Value == "80 MiB", "drilldown ranks with observed route split");
                    dialog.Size = dialog.MinimumSize;
                    using (Bitmap image = new Bitmap(dialog.Width, dialog.Height))
                    {
                        dialog.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
                        image.Save(Path.Combine(folder, "usage-detail" + suffix), ImageFormat.Png);
                    }
                    captured = true;
                }
                catch (Exception ex) { failure = ex; }
                finally { dialog.Close(); }
            };
            timer.Start(); sites.PerformClick();
        }
        if (failure != null) throw failure;
        Check(captured, "normal site button opens detail");
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control c in parent.Controls) { yield return c; foreach (Control child in Descendants(c)) yield return child; }
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); _checks++; }
}
