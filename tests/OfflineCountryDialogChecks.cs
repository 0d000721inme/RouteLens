using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using RouteLens;

internal static class OfflineCountryDialogChecks
{
    [STAThread] private static int Main()
    {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        string root = Path.Combine(Path.GetTempPath(), "RouteLensOfflineDialog-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        using (OfflineCountryDatabase database = new OfflineCountryDatabase(root))
        using (OfflineCountryDialog dialog = new OfflineCountryDialog(database))
        {
            dialog.StartPosition = FormStartPosition.Manual; dialog.Location = new Point(-12000, -12000); dialog.ShowInTaskbar = false; dialog.Show(); Application.DoEvents();
            Check(Field<Label>(dialog, "state").Text == "尚未安装", "empty database incorrectly enabled");
            Check(Field<Button>(dialog, "import").Enabled && Field<Button>(dialog, "download").Enabled, "empty state actions disabled");
            string sample = Path.Combine(root, "fixture.csv"); File.WriteAllText(sample, "8.8.8.0,8.8.8.255,US\n1.1.1.0,1.1.1.255,TW\n");
            database.ImportAsync(sample).GetAwaiter().GetResult(); typeof(OfflineCountryDialog).GetMethod("Render", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(dialog, null); Application.DoEvents();
            Check(Field<Label>(dialog, "state").Text == "已启用" && Field<Label>(dialog, "detail").Text.Contains("2 个网段"), "ready status missing");
            foreach (Size size in new[] { new Size(780, 520), new Size(700, 480) })
            {
                dialog.Size = size; Application.DoEvents();
                foreach (Control child in Descendants(dialog)) if (child.Visible && (child is Button || child is LinkLabel)) Check(child.Parent.ClientRectangle.Contains(child.Bounds), "dialog action outside parent: " + child.Text);
                using (Bitmap image = new Bitmap(dialog.Width, dialog.Height)) { dialog.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); image.Save(Path.Combine("work", "offline-dialog-" + size.Width + ".png"), ImageFormat.Png); }
            }
            dialog.Close();
        }
        Console.WriteLine("PASS offline dialog missing/ready states and 780x520 / 700x480 action layout; synthetic data only.");
        return 0;
    }
    private static T Field<T>(object target, string name) { return (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static System.Collections.Generic.IEnumerable<Control> Descendants(Control parent) { foreach (Control child in parent.Controls) { yield return child; foreach (Control nested in Descendants(child)) yield return nested; } }
}
