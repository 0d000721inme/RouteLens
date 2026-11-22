using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using RouteLens;

internal static class MapResizePerformanceChecks
{
    private static int checks;

    private static void Check(bool condition, string message)
    {
        checks++;
        if (!condition) throw new Exception(message);
    }

    private static void Pump(int milliseconds)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < milliseconds)
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
        Application.DoEvents();
    }

    private static void Paint(WorldMapControl map)
    {
        using (Bitmap image = new Bitmap(Math.Max(1, map.Width), Math.Max(1, map.Height)))
            map.DrawToBitmap(image, map.ClientRectangle);
    }

    private static void ResizeThrough(Form host, WorldMapControl map, System.Windows.Forms.Timer settleTimer, ref double totalMilliseconds, ref double maximumMilliseconds)
    {
        int startingBuilds = map.BaseLayerBuildCount;
        int startingPreviewBuilds = map.ResizePreviewBuildCount;
        int normalInterval = settleTimer.Interval;
        settleTimer.Stop();
        settleTimer.Interval = 30000;
        const int frames = 24;
        double paintTotalMilliseconds = 0;
        maximumMilliseconds = 0;
        for (int i = 0; i < frames; i++)
        {
            int width = 870 + (i * 23 % 500);
            int height = 460 + (i * 17 % 230);
            host.ClientSize = new Size(width, height);
            Application.DoEvents();
            Stopwatch paintClock = Stopwatch.StartNew();
            Paint(map);
            paintClock.Stop();
            paintTotalMilliseconds += paintClock.Elapsed.TotalMilliseconds;
            maximumMilliseconds = Math.Max(maximumMilliseconds, paintClock.Elapsed.TotalMilliseconds);
            Check(map.IsResizePreview, "Intermediate size did not use the cached resize preview.");
            Check(map.BaseLayerBuildCount == startingBuilds, "An intermediate size rebuilt the detailed map base layer.");
            Check(map.ResizePreviewBuildCount == startingPreviewBuilds,
                "An intermediate resize frame rebuilt the map-only preview instead of reusing its stable snapshot.");
        }
        totalMilliseconds = paintTotalMilliseconds / frames;
        settleTimer.Interval = normalInterval;
        settleTimer.Start();
        Pump(normalInterval + 80);
        Check(!map.IsResizePreview, "Resize preview did not settle after the resize stopped.");
        Check(map.BaseLayerBuildCount == startingBuilds + 1, "Settled resize should rebuild the detailed map exactly once.");
        Check(map.ResizePreviewBuildCount == startingPreviewBuilds + 1,
            "Settled resize should refresh the map-only snapshot exactly once for the new size.");
        Check(map.MapBoundsForChecks.Left >= 0 && map.MapBoundsForChecks.Top >= 0 &&
            map.MapBoundsForChecks.Right <= map.Width + 1 && map.MapBoundsForChecks.Bottom <= map.Height + 1,
            "Settled map projection is outside the resized control.");
        Paint(map);
    }

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();
            UiPalette.SetMode(false, null);
            using (Form host = new Form { ClientSize = new Size(1100, 700), StartPosition = FormStartPosition.Manual,
                Location = new Point(30, 30), ShowInTaskbar = false })
            using (WorldMapControl map = new WorldMapControl { Dock = DockStyle.Fill })
            {
                host.Controls.Add(map);
                map.SetData(new System.Collections.Generic.List<CountryStat>
                {
                    new CountryStat { Code = "US", Name = "United States", Count = 11 },
                    new CountryStat { Code = "CN", Name = "China", Count = 7 },
                    new CountryStat { Code = "HK", Name = "Hong Kong", Count = 4 },
                    new CountryStat { Code = "GB", Name = "United Kingdom", Count = 2 }
                }, 24, 0, null);
                host.Show();
                Pump(40);
                Paint(map);
                int initialBuilds = map.BaseLayerBuildCount;
                long fullBuildMilliseconds = map.LastBaseLayerBuildMilliseconds;
                Check(initialBuilds == 1, "Initial map paint did not build exactly one detailed base layer.");

                FieldInfo timerField = typeof(WorldMapControl).GetField("_resizeSettleTimer", BindingFlags.NonPublic | BindingFlags.Instance);
                System.Windows.Forms.Timer settleTimer = (System.Windows.Forms.Timer)timerField.GetValue(map);
                double previewAverage = 0, previewMax = 0;
                ResizeThrough(host, map, settleTimer, ref previewAverage, ref previewMax);
                long finalBuildMilliseconds = map.LastBaseLayerBuildMilliseconds;
                Check(map.BaseLayerBuildCount == initialBuilds + 1, "Distribution view cache count is wrong after settling.");

                map.SetRoute(new WorldMapRoutePoint("Origin", "CN", new PointF(120.9f, 31.9f)),
                    new WorldMapRoutePoint("Destination", "HK"));
                map.SetRouteTraffic(true, false);
                Paint(map);
                int routeStartingBuilds = map.BaseLayerBuildCount;
                ResizeThrough(host, map, settleTimer, ref previewAverage, ref previewMax);
                Check(map.BaseLayerBuildCount == routeStartingBuilds + 1, "Route view did not rebuild once after settled resize.");
                map.ClearRoute();
                map.SetData(new System.Collections.Generic.List<CountryStat>(), 0, 0, null);
                Paint(map);
                Check(map.MapBoundsForChecks.Right <= map.Width + 1, "Map hit-test bounds are stale after returning from route view.");

                if (args.Length > 0)
                {
                    using (Bitmap image = new Bitmap(map.Width, map.Height))
                    {
                        map.DrawToBitmap(image, map.ClientRectangle);
                        image.Save(args[0], ImageFormat.Png);
                    }
                }
                Console.WriteLine("PASS " + checks + " map resize cache checks.");
                Console.WriteLine("Detailed base layer builds: initial 1, distribution resize +1 after settle, route resize +1 after settle.");
                Console.WriteLine("Full detail build times: initial " + fullBuildMilliseconds + " ms, final " + finalBuildMilliseconds + " ms.");
                Console.WriteLine("Last 24-frame preview batch averaged " + previewAverage.ToString("0.##") +
                    " ms per paint (max " + previewMax.ToString("0.##") + " ms) and rebuilt no detailed layer between frames.");
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
