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
            using (TextBox input = new TextBox { Location = new Point(20, 30) })
            using (PageTransition strip = new PageTransition())
            {
                strip.Play(); Check(!strip.IsRunning, "Detached strip should skip");
                host.Controls.Add(input); host.Controls.Add(strip);
                host.Show(); Application.DoEvents();
                bool enabled = PageTransition.AnimationsEnabled;
                input.Focus(); strip.Play();
                Check(strip.IsRunning == enabled && strip.Visible == enabled, "Windows animation setting must control transition");
                Check(input.Focused && !strip.CanSelect && !strip.TabStop, "Animation must leave page input active");
                if (enabled)
                {
                    Check(strip.Bounds == new Rectangle(0, 0, host.ClientSize.Width, 3), "Only the top strip should repaint");
                    using (Bitmap frame = new Bitmap(strip.Width, strip.Height))
                    {
                        strip.DrawToBitmap(frame, new Rectangle(Point.Empty, frame.Size));
                        Check(frame.GetPixel(1, 1).ToArgb() != strip.BackColor.ToArgb(), "Strip should paint the accent");
                    }
                }
                Pump(260); Check(!strip.IsRunning && !strip.Visible, "Completed animation must hide the strip");
                strip.Play(); strip.Stop(); strip.Stop();
                Check(!strip.IsRunning && !strip.Visible, "Repeated stop should be safe");
                strip.Play(); host.ClientSize = new Size(420, 200); Application.DoEvents();
                Check(!strip.IsRunning, "Width changes should cancel the transition");
                for (int i = 0; i < 30; i++) strip.Play();
                Pump(260); Check(!strip.IsRunning && !strip.Visible, "Rapid replacement must settle");
                strip.Play(); strip.Dispose(); strip.Stop(); strip.Play();
                Check(!strip.IsRunning, "Disposal should cancel animation");
                Console.WriteLine("PASS " + checks + " transition checks; Windows animation enabled=" + enabled + ".");
                host.Close();
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
