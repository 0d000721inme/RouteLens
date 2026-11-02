using System;
using System.Drawing;
using System.Diagnostics;
using System.Windows.Forms;
using RouteLens;

internal static class PageTransitionChecks
{
    private static int checks;
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    private static void Pump(int ms) { Stopwatch watch = Stopwatch.StartNew(); while (watch.ElapsedMilliseconds < ms) { Application.DoEvents(); System.Threading.Thread.Sleep(5); } }
    [STAThread] private static int Main()
    {
        try
        {
            Application.EnableVisualStyles();
            using (Form host = new Form { ClientSize = new Size(400, 200), StartPosition = FormStartPosition.Manual, Location = new Point(-10000, -10000) })
            using (Panel first = new Panel { Dock = DockStyle.Fill, BackColor = Color.White })
            using (Panel second = new Panel { Dock = DockStyle.Fill, BackColor = Color.LightBlue })
            using (PageTransition fade = new PageTransition { Dock = DockStyle.Fill })
            {
                host.Controls.Add(first); host.Controls.Add(second); host.Controls.Add(fade); second.Hide();
                host.Show(); Application.DoEvents();
                bool enabled = PageTransition.AnimationsEnabled;
                Check(PageTransition.Capture(null) == null, "Null capture should skip");
                Bitmap before = PageTransition.Capture(first); first.Hide(); second.Show(); second.BringToFront();
                fade.Play(before, second);
                Check(fade.IsRunning == enabled, "Windows animation setting must control transition");
                Check(!fade.CanSelect && !fade.TabStop, "Animation must not steal keyboard focus");
                if (enabled)
                {
                    Pump(35);
                    using (Bitmap frame = new Bitmap(fade.Width, fade.Height))
                    {
                        fade.DrawToBitmap(frame, new Rectangle(Point.Empty, frame.Size));
                        Color pixel = frame.GetPixel(100, 100);
                        Check(pixel.R > Color.LightBlue.R && pixel.R < Color.White.R, "Intermediate frame should blend old and new content");
                    }
                }
                Pump(240); Check(!fade.IsRunning && !fade.Visible, "Completed animation must release overlay");
                fade.Play(PageTransition.Capture(second), second); fade.Stop(); fade.Stop();
                Check(!fade.IsRunning && !fade.Visible, "Repeated stop should be safe");
                fade.Play(PageTransition.Capture(second), second); host.ClientSize = new Size(420, 200); Application.DoEvents();
                Check(!fade.IsRunning, "Resize must cancel stale captured frames");
                for (int i = 0; i < 30; i++) fade.Play(PageTransition.Capture(second), second);
                Pump(240); Check(!fade.IsRunning, "Rapid replacement must settle");
                fade.Play(new Bitmap(2, 2), second); Check(!fade.IsRunning, "Mismatched capture must skip");
                fade.Stop(); Check(PageTransition.Capture(first) == null, "Hidden pages should not be captured");
                Console.WriteLine("PASS " + checks + " transition checks; Windows animation enabled=" + enabled + "; frames remain in memory.");
                host.Close();
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
