using System;
using System.IO;
using System.Security;

namespace RouteLens
{
    internal static class ThemePreference
    {
        internal static string PathInApp { get; set; }

        static ThemePreference()
        {
            PathInApp = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RouteLens", "theme.txt");
        }

        internal static bool Read(string path)
        {
            try { return String.Equals(File.ReadAllText(path).Trim(), "dark", StringComparison.OrdinalIgnoreCase); }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (SecurityException) { return false; }
        }

        internal static void Write(string path, bool isDark)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, isDark ? "dark" : "light");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (SecurityException) { }
        }
    }
}
