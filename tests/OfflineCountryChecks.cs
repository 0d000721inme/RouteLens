using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RouteLens;

internal static class OfflineCountryChecks
{
    private static int assertions;
    private static void Require(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
    private static void ExpectBad(OfflineCountryDatabase database, string path)
    {
        bool failed = false; try { database.ImportAsync(path).GetAwaiter().GetResult(); } catch (Exception) { failed = true; }
        Require(failed, "invalid import should fail");
    }
    private static string Country(OfflineCountryDatabase database, string ip) { GeoResult result = database.Find(ip); return result == null ? null : result.CountryCode; }
    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "RouteLensOfflineTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string csv = Path.Combine(root, "fixture.csv"), gz = Path.Combine(root, "fixture.csv.gz"), bad = Path.Combine(root, "invalid.csv");
        File.WriteAllText(csv, "\"8.8.8.0\",\"8.8.8.255\",\"US\"\r\n1.1.1.0,1.1.1.255,TW\r\n2001:4860:4860::,2001:4860:4860::ffff,JP\n", new UTF8Encoding(false));
        using (FileStream output = File.Create(gz)) using (GZipStream gzip = new GZipStream(output, CompressionMode.Compress)) using (FileStream input = File.OpenRead(csv)) input.CopyTo(gzip);
        string dataDirectory = Path.Combine(root, "data");
        using (OfflineCountryDatabase database = new OfflineCountryDatabase(dataDirectory))
        {
            Require(!database.GetStatus().Ready && !database.GetStatus().Busy && database.Find("8.8.8.8") == null, "missing database state");
            database.ImportAsync(gz).GetAwaiter().GetResult();
            Require(database.GetStatus().Ready && database.GetStatus().RangeCount == 3, "gzip import state");
            Require(Country(database, "8.8.8.0") == "US" && Country(database, "8.8.8.255") == "US", "IPv4 inclusive boundaries");
            Require(Country(database, "8.8.7.255") == null && Country(database, "8.8.9.0") == null, "IPv4 outside range");
            Require(Country(database, "1.1.1.1") == "TW" && database.Find("1.1.1.1").CountryName == "中国台湾", "TW code and display name");
            Require(Country(database, "::ffff:8.8.8.8") == "US", "mapped IPv4 lookup");
            Require(Country(database, "2001:4860:4860::") == "JP" && Country(database, "2001:4860:4860::ffff") == "JP", "IPv6 boundaries");
            Require(Country(database, "2001:4860:4861::") == null && database.Find("invalid") == null, "IPv6 outside and invalid");
            string installed = Path.Combine(dataDirectory, "offline-country.dbip"); byte[] original = File.ReadAllBytes(installed);
            File.WriteAllText(bad, "8.8.8.0,8.8.8.255,US\n8.8.8.128,8.8.9.0,CN\n", Encoding.UTF8);
            ExpectBad(database, bad);
            Require(Country(database, "8.8.8.8") == "US" && Equal(original, File.ReadAllBytes(installed)), "overlapping update preserves old data");
            Require(database.GetStatus().Ready && database.GetStatus().Message.Contains("原数据库仍可用"), "failure visible while old database is active");
            File.WriteAllText(bad, "8.8.8.0,8.8.8.1,US,extra\n", Encoding.UTF8);
            ExpectBad(database, bad);
            Require(Equal(original, File.ReadAllBytes(installed)), "malformed columns preserve old file");
            File.WriteAllText(bad, new String('a', 1025), Encoding.UTF8); ExpectBad(database, bad);
            Require(Equal(original, File.ReadAllBytes(installed)), "long line rejected without replacing database");
            File.WriteAllText(bad, "8.8.8.0,8.8.8.255,CN\n1.1.1.0,1.1.1.255,tw\n", Encoding.UTF8);
            Task update = database.ImportAsync(bad);
            while (!update.IsCompleted) { string oldOrNew = Country(database, "8.8.8.8"); Require(oldOrNew == "US" || oldOrNew == "CN", "concurrent query saw partial update"); Thread.Sleep(1); }
            update.GetAwaiter().GetResult();
            Require(Country(database, "8.8.8.8") == "CN" && Country(database, "1.1.1.1") == "TW", "CSV replacement and case normalization");
        }
        using (OfflineCountryDatabase reopened = new OfflineCountryDatabase(dataDirectory))
        {
            Require(SpinWait.SpinUntil(delegate { return !reopened.GetStatus().Busy; }, 10000), "restart load timed out");
            Require(reopened.GetStatus().Ready && Country(reopened, "8.8.8.8") == "CN", "database survives restart");
        }
        using (GeoService service = new GeoService(dataDirectory))
        {
            service.Enabled = false;
            Require(SpinWait.SpinUntil(delegate { return !service.Offline.GetStatus().Busy; }, 10000), "service offline load timed out");
            FieldInfo cacheField = typeof(GeoService).GetField("cache", BindingFlags.Instance | BindingFlags.NonPublic);
            Dictionary<string, GeoResult> cache = (Dictionary<string, GeoResult>)cacheField.GetValue(service);
            cache["8.8.8.8"] = new GeoResult { Ip = "8.8.8.8", Status = "error", Error = "fixture only", CheckedAt = DateTime.UtcNow };
            Require(service.Peek("8.8.8.8").CountryCode == "CN" && service.GetOrQueue("8.8.8.8").CountryCode == "CN", "offline result beats old cache error when internet disabled");
            Require(service.GetOrQueue("9.9.9.9").Status == "disabled", "missing offline result does not use internet when disabled");
            Require(service.PendingCount == 0, "no live IP lookup queued in offline-only test");
            Require(service.StatusText.Contains("离线"), "offline ready visible in service status");
        }
        Console.WriteLine("PASS " + assertions + " offline GeoIP checks: CSV/GZ, IPv4/IPv6 boundaries, import integrity, atomic replacement, restart, priority and offline-only behavior.");
        return 0;
    }
    private static bool Equal(byte[] a, byte[] b) { if (a.Length != b.Length) return false; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false; return true; }
}
