using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using RouteLens;

internal static class RenderInvalidationChecks
{
    private static int checks;
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    private static List<CountryStat> Countries()
    {
        return new List<CountryStat> { new CountryStat { Code = "ZZ", Name = "示例地区", Count = 4 }, new CountryStat { Code = "XY", Name = "另一个地区", Count = 2 } };
    }
    private static Bitmap Capture(Control control)
    {
        Bitmap bitmap = new Bitmap(control.Width, control.Height);
        control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        return bitmap;
    }
    [STAThread] private static int Main()
    {
        try
        {
            Application.EnableVisualStyles();
            Flags.NetworkEnabled = false;
            using (CountryBreakdown countries = new CountryBreakdown())
            {
                IntPtr handle = countries.Handle;
                int paints = 0;
                countries.Invalidated += delegate { paints++; };
                countries.SetData(Countries(), 8, 2); paints = 0;
                for (int i = 0; i < 20; i++) countries.SetData(Countries(), 8, 2);
                Check(paints == 0, "Unchanged country samples still invalidate the chart");
                Check(countries.AccessibleDescription.Contains("6 / 8"), "Coverage description did not initialize");
                countries.SetData(Countries(), 8, 1);
                Check(paints > 0, "Changed unknown count was not repainted"); paints = 0;
                List<CountryStat> renamed = Countries(); renamed[0].Name = "新名称";
                countries.SetData(renamed, 8, 1);
                Check(paints > 0, "Changed name was not repainted"); paints = 0;
                List<CountryStat> reordered = Countries(); reordered[1].Count = 5;
                countries.SetData(reordered, 9, 0);
                Check(paints > 0, "Changed counts/order were not repainted");
                countries.SetData(Countries(), 8, 2); paints = 0;
                countries.SelectedCode = "ZZ";
                Check(paints > 0, "Selection was not repainted"); paints = 0;
                countries.SetData(Countries(), 8, 2);
                Check(paints == 0 && countries.SelectedCode == "ZZ", "Same data lost selection or repainted it");
                countries.SelectedCode = null;
                using (Bitmap before = Capture(countries))
                using (Bitmap flag = new Bitmap(20, 12))
                {
                    using (Graphics graphics = Graphics.FromImage(flag)) graphics.Clear(Color.Magenta);
                    Dictionary<string, Image> images = (Dictionary<string, Image>)typeof(Flags).GetField("Images", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                    // Simulate completion of a flag load entirely in memory.
                    images.Add("zz", flag);
                    try
                    {
                        paints = 0; countries.SetData(Countries(), 8, 2);
                        Check(paints > 0, "A newly available flag must repaint otherwise identical data");
                        using (Bitmap after = Capture(countries))
                        {
                            Check(before.GetPixel(15, 14) != after.GetPixel(15, 14) && after.GetPixel(15, 14).ToArgb() == Color.Magenta.ToArgb(), "New flag pixels were not rendered");
                        }
                        paints = 0; countries.SetData(Countries(), 8, 2);
                        Check(paints == 0, "Unchanged cached flags triggered a repaint");
                    }
                    finally { images.Remove("zz"); countries.SetData(null, 0, 0); }
                }
                paints = 0; countries.SetData(null, 0, 0);
                Check(paints == 0, "Repeated empty samples trigger repaint");
            }
            using (RoundedPanel panel = new RoundedPanel())
            {
                IntPtr handle = panel.Handle; int paints = 0;
                panel.Invalidated += delegate { paints++; };
                for (int i = 0; i < 20; i++) { panel.Radius = panel.Radius; panel.SurfaceColor = panel.SurfaceColor; panel.BorderColor = panel.BorderColor; }
                Check(paints == 0, "Identical panel styling triggered repaint");
                panel.SurfaceColor = Color.Beige;
                Check(paints > 0, "Changed panel fill did not repaint"); paints = 0;
                panel.BorderColor = Color.Orange;
                Check(paints > 0, "Changed panel outline did not repaint"); paints = 0;
                panel.Radius = 0;
                Check(paints > 0, "Changed radius did not repaint"); paints = 0;
                panel.Radius = -5;
                Check(paints == 0 && panel.Radius == 0, "Equivalent normalized radius triggered repaint");
            }
            using (ModernButton button = new ModernButton())
            {
                IntPtr handle = button.Handle; int paints = 0;
                button.Invalidated += delegate { paints++; };
                for (int i = 0; i < 20; i++) { button.IconName = button.IconName; button.Primary = button.Primary; button.Quiet = button.Quiet; button.CornerRadius = button.CornerRadius; }
                Check(paints == 0, "Identical navigation styling triggered repaint");
                using (Bitmap before = Capture(button))
                {
                    button.Primary = true;
                    Check(paints > 0, "New active-page styling did not repaint");
                    using (Bitmap after = Capture(button)) Check(before.GetPixel(10, 20) != after.GetPixel(10, 20), "Primary button pixels did not change");
                }
                paints = 0; button.Quiet = true;
                Check(paints > 0, "Changed quiet state did not repaint"); paints = 0;
                button.IconName = "globe";
                Check(paints > 0, "Changed icon did not repaint"); paints = 0;
                button.CornerRadius = 0;
                Check(paints > 0, "Changed corner radius did not repaint"); paints = 0;
                button.CornerRadius = -1;
                Check(paints == 0 && button.CornerRadius == 0, "Equivalent normalized corner radius triggered repaint");
            }
            Console.WriteLine("PASS " + checks + " rendering invalidation checks; 20 identical country samples and 20 repeated style assignments cause zero invalidations; changed data, selection and newly available flags still repaint. No network or live sampling.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
