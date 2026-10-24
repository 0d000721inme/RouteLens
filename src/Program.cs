using System;
using System.IO;
using System.Windows.Forms;

namespace RouteLens
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e)
            {
                MessageBox.Show("界面遇到错误，请关闭后重新打开。\r\n" + e.Exception.Message,
                    "RouteLens · 网络去向", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            using (GeoService geo = new GeoService(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data")))
                Application.Run(new MainForm(geo));
        }
    }
}
