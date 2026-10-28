using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RouteLens;

internal static class HistoryChecks
{
    private static int checks;
    private static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    private static ConnectionInfo Row(string id, string host, long down, long up)
    {
        return new ConnectionInfo { Id = id, Protocol = "TCP", LocalAddress = "127.0.0.1", LocalPort = id == "a" ? 10001 : 10002,
            RemoteAddress = "192.0.2.4", RemotePort = 443, ProcessName = "示例.exe", ProcessPath = @"C:\Example\示例.exe",
            ProcessId = id == "a" ? 1 : 2, Host = host, ProxyChain = id == "b" ? "另一组 → 节点" : "组 → 节点", DataSource = "Clash / Mihomo", HasTrafficCounters = true,
            DownloadBytes = down, UploadBytes = up };
    }
    private static NetworkSnapshot Snapshot(DateTime at, params ConnectionInfo[] rows)
    { return new NetworkSnapshot { CapturedAt = at, ClashConnected = true, SourceName = "Clash 原始连接", ControllerSummary = "虚构控制器", Connections = new List<ConnectionInfo>(rows) }; }
    private static int Main()
    {
        string work = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
        string directory = Path.Combine(work, "history-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            DateTime time = DateTime.Now.AddSeconds(-3);
            using (HistoryStore store = new HistoryStore(directory))
            {
                Check(!store.Enabled && store.RetentionDays == 30, "History should be opt-in");
                store.Update(Snapshot(time, Row("a", "a.example.org", 100, 10)));
                Check(!Directory.Exists(directory), "Disabled history created files");
                store.Enabled = true;
                Check(store.LastError == null && File.Exists(store.DatabasePath), "SQLite opening failed: " + store.LastError);
                store.Update(Snapshot(time, Row("a", "a.example.org", 100, 10), Row("b", "b.example.org", 300, 20)));
                Check(store.GetReport(1, "program").DownloadBytes == 0, "First sample was counted");
                store.Update(Snapshot(time.AddSeconds(1), Row("a", "a.example.org", 800, 50), Row("b", "b.example.org", 600, 30)));
                HistoryReport program = store.GetReport(1, "program");
                Check(program.Available && program.DownloadBytes == 1000 && program.UploadBytes == 50, "Delta totals incorrect");
                Check(program.Entries.Count == 1 && program.Entries[0].DownloadBytes == 1000, "Same path did not aggregate processes");
                using (HistoryStore other = new HistoryStore(directory))
                {
                    other.Update(Snapshot(time, Row("a", "a.example.org", 100, 10)));
                    other.Update(Snapshot(time.AddSeconds(1), Row("a", "a.example.org", 9000, 9000)));
                    HistoryReport shared = other.GetReport(1, "program");
                    Check(shared.DownloadBytes == 1000 && shared.Message.Contains("其他 RouteLens"), "Second instance double-counted or failed to explain read-only state");
                }
                HistoryReport domain = store.GetReport(1, "domain");
                Check(domain.Entries.Count == 2 && domain.Entries.First(x => x.Name == "a.example.org").DownloadBytes == 700, "Domain grouping incorrect");
                Check(store.GetReport(1, "node").Entries.Count == 1 && store.GetReport(1, "chain").Entries.Count == 2, "Proxy chain / terminal-node grouping incorrect");
                store.ResetBaseline();
                store.Update(Snapshot(time.AddSeconds(2), Row("a", "a.example.org", 50000, 5000)));
                Check(store.GetReport(1, "program").DownloadBytes == 1000, "Resume included paused traffic");
                store.Update(Snapshot(time.AddSeconds(3), Row("a", "a.example.org", 1, 1)));
                Check(store.GetReport(1, "program").DownloadBytes == 1000, "Counter reset counted");
                store.Update(Snapshot(time.AddSeconds(4), Row("a", "a.example.org", 5, 3), Row("short", "short.example.org", 2000, 2000)));
                Check(store.GetReport(1, "program").DownloadBytes == 1004, "New connection or reset baseline counted");
                store.Update(Snapshot(time.AddSeconds(5), Row("a", "a.example.org", 9, 5), Row("a", "a.example.org", 50000, 50000)));
                store.Update(Snapshot(time.AddSeconds(6), Row("a", "a.example.org", 60000, 60000)));
                Check(store.GetReport(1, "program").DownloadBytes == 1004, "Ambiguous ID used as a baseline");
                store.Update(Snapshot(time.AddSeconds(7), Row("a", "192.0.2.4", 60010, 60002)));
                Check(store.GetReport(1, "domain").Entries.Any(x => x.Name == "未提供域名" && x.DownloadBytes == 10), "IP literal incorrectly presented as domain");
                store.Update(new NetworkSnapshot { CapturedAt = time.AddSeconds(8), ClashConnected = false, Connections = new List<ConnectionInfo>() });
                store.Update(Snapshot(time.AddSeconds(9), Row("a", "a.example.org", 100000, 90000)));
                Check(store.GetReport(1, "program").DownloadBytes == 1014, "Failed source was treated as continuous");
                store.Update(Snapshot(time.AddSeconds(20), Row("a", "a.example.org", 200000, 190000)));
                Check(store.GetReport(1, "program").DownloadBytes == 1014, "Stale time gap counted");
                store.Flush();
            }
            using (HistoryStore reopened = new HistoryStore(directory))
            {
                Check(reopened.Enabled && reopened.GetReport(1, "program").DownloadBytes == 1014, "History did not persist/reload");
                reopened.Enabled = false;
                Check(!reopened.Enabled && reopened.GetReport(1, "program").DownloadBytes == 1014, "Disabling should preserve history");
                reopened.RetentionDays = 7;
                reopened.Clear();
                Check(reopened.LastError == null && reopened.GetReport(1, "program").Entries.Count == 0, "Clear failed");
            }
            using (HistoryStore empty = new HistoryStore(directory)) Check(!empty.Enabled && empty.RetentionDays == 7 && empty.GetReport(1, "program").Entries.Count == 0, "Settings/clear did not persist");
            Console.WriteLine("PASS " + checks + " end-to-end synthetic history checks with Windows SQLite.");
            return 0;
        }
        finally
        {
            string full = Path.GetFullPath(directory), root = Path.GetFullPath(work).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe test directory");
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }
    }
}
