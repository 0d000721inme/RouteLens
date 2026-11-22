using System;
using System.Drawing;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using RouteLens;

internal static class ThemeChecks
{
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    private static int checks;
    private static void Check(bool condition, string message)
    {
        checks++;
        if (!condition) throw new Exception(message);
    }

    private static int Distance(Color left, Color right)
    {
        return Math.Abs(left.R - right.R) + Math.Abs(left.G - right.G) + Math.Abs(left.B - right.B);
    }

    private static void Pump(int milliseconds)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (elapsed.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); System.Threading.Thread.Sleep(5); }
    }

    [STAThread]
    private static int Main()
    {
        string preference = Path.Combine(Path.GetTempPath(), "RouteLens-theme-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            Application.EnableVisualStyles();
            using (Form form = new Form())
            using (RoundedPanel panel = new RoundedPanel())
            using (TextBox input = new TextBox())
            using (DataGridView grid = new DataGridView())
            using (WorldMapControl map = new WorldMapControl())
            using (ThemeToggle toggle = new ThemeToggle())
            using (ThemeTransition transition = new ThemeTransition { Dock = DockStyle.Fill })
            {
                UiPalette.SetMode(false, null);
                form.BackColor = UiPalette.Canvas; form.ForeColor = UiPalette.Ink;
                panel.SurfaceColor = UiPalette.Surface; panel.BorderColor = UiPalette.Border;
                panel.Controls.Add(input);
                grid.BackgroundColor = UiPalette.Surface; grid.GridColor = UiPalette.Border;
                grid.DefaultCellStyle = new DataGridViewCellStyle {
                    BackColor = UiPalette.Surface, ForeColor = UiPalette.Ink,
                    SelectionBackColor = UiPalette.AccentSoft, SelectionForeColor = UiPalette.Ink
                };
                ContextMenuStrip menu = new ContextMenuStrip { BackColor = UiPalette.Surface, ForeColor = UiPalette.Ink };
                menu.Items.Add("Test");
                grid.ContextMenuStrip = menu;
                panel.Bounds = new Rectangle(10, 10, 120, 40);
                grid.Bounds = new Rectangle(10, 60, 220, 100);
                map.Bounds = new Rectangle(250, 10, 300, 180);
                toggle.Bounds = new Rectangle(10, 180, 60, 36);
                form.Controls.Add(panel); form.Controls.Add(grid); form.Controls.Add(map); form.Controls.Add(toggle); form.Controls.Add(transition);
                form.ClientSize = new Size(580, 240); form.Show(); Application.DoEvents();

                using (Bitmap previous = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
                {
                    form.DrawToBitmap(previous, new Rectangle(Point.Empty, previous.Size));
                    transition.Play(form);
                UiPalette.SetMode(true, form);
                Check(transition.IsRunning == PageTransition.AnimationsEnabled, "Window transition should follow Windows animation preference");
                if (transition.IsRunning)
                {
                    transition.CaptureCurrent(form);
                    using (Bitmap frame = new Bitmap(transition.Width, transition.Height))
                    {
                        transition.DrawToBitmap(frame, new Rectangle(Point.Empty, frame.Size));
                        Color initial = frame.GetPixel(200, 230);
                        Color before = previous.GetPixel(200, 230);
                        Check(Distance(initial, before) > 0 && Distance(initial, UiPalette.Canvas) < Distance(before, UiPalette.Canvas),
                            "Transition should start between the old and current palette");
                        Pump(55);
                        transition.DrawToBitmap(frame, new Rectangle(Point.Empty, frame.Size));
                        Color middle = frame.GetPixel(200, 230);
                        Check(Distance(middle, initial) > 0 && Distance(middle, UiPalette.Canvas) < Distance(initial, UiPalette.Canvas),
                            "Transition should keep fading the previous frame");
                    }
                    IntPtr hitTest = SendMessage(transition.Handle, 0x84, IntPtr.Zero, new IntPtr(1 | (1 << 16)));
                    Check(hitTest.ToInt64() == -1, "Window transition overlay should pass pointer input through");
                    Pump(230);
                    Check(!transition.IsRunning && !transition.Visible, "Window transition should release its frame after 200 ms");
                }
                }
                Check(UiPalette.IsDark && form.BackColor == UiPalette.Canvas, "Form surface should use dark palette");
                Check(panel.SurfaceColor == UiPalette.Surface && panel.BorderColor == UiPalette.Border, "Rounded panels should change theme");
                Check(input.BackColor == UiPalette.Surface && input.ForeColor == UiPalette.Ink, "Native inputs should change theme");
                Check(grid.BackgroundColor == UiPalette.Surface && grid.GridColor == UiPalette.Border, "Grid surface should change theme");
                Check(grid.DefaultCellStyle.SelectionBackColor.ToArgb() == UiPalette.AccentSoft.ToArgb(),
                    "Grid selection should change theme " + grid.DefaultCellStyle.SelectionBackColor + " expected " + UiPalette.AccentSoft);
                Check(menu.BackColor == UiPalette.Surface && menu.Items[0].ForeColor == UiPalette.Ink,
                    "Grid context menu should change theme");
                Check(toggle.AccessibleName == "切换到深色主题", "Theme control should expose current mode");

                toggle.SetTheme(false);
                transition.Play(form);
                UiPalette.SetMode(false, form);
                Check(transition.IsRunning == PageTransition.AnimationsEnabled, "Reverse transition should follow Windows animation preference");
                toggle.SetTheme(false);
                Check(!UiPalette.IsDark && form.BackColor == UiPalette.Canvas, "Switching back should restore light palette");
                Check(grid.DefaultCellStyle.SelectionBackColor == UiPalette.AccentSoft, "Grid selection should restore light palette");
                menu.Dispose();
                ThemePreference.Write(preference, true);
                Check(ThemePreference.Read(preference), "Dark preference should persist");
                ThemePreference.Write(preference, false);
                Check(!ThemePreference.Read(preference), "Light preference should persist");
                transition.Play(form, false);
                Check(!transition.IsRunning && !transition.Visible, "Disabled motion should skip the window transition");
                transition.Play(form); transition.Stop(); transition.Stop();
                Check(!transition.IsRunning && !transition.Visible, "Repeated stop should release the previous frame");
                transition.Play(form); form.ClientSize = new Size(600, 240);
                Check(!transition.IsRunning, "Resize should cancel a stale transition frame");
                transition.Play(form); transition.Dispose(); transition.Play(form);
                Check(!transition.IsRunning, "Disposed transition should not restart");
            }
            Console.WriteLine("PASS " + checks + " theme checks.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            if (File.Exists(preference)) File.Delete(preference);
        }
    }
}
