using System;
using System.Collections.Generic;
using System.Linq;
using RouteLens;

internal static class ActivityAlertsChecks
{
    private static int checks;
    [STAThread] private static int Main()
    {
        try { Run(); Console.WriteLine("PASS " + checks + " session alert checks; fictional data only, no network or disk history."); return 0; }
        catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex.Message); return 1; }
    }
    private static void Run()
    {
        DateTime clock = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        DateTime sample = clock;
        ActivityAlerts alerts = new ActivityAlerts(delegate { return clock; });
        List<string> events = new List<string>(); alerts.Alert += events.Add;
        int lookups = 0;
        Func<string, string> resolver = delegate(string ip) { lookups++; return ip == "192.0.2.1" ? "US" : ip == "192.0.2.2" ? "JP" : ip == "192.0.2.3" ? "DE" : "FR"; };
        ConnectionInfo a = Row("a", "C:\\Example\\a.exe", "a.exe", 20, "192.0.2.1", 0);
        alerts.Update(Snapshot(sample, "Clash", a), resolver);
        Check(!alerts.Enabled && lookups == 0 && events.Count == 0, "Default-disabled alerts scanned data");
        alerts.NewProgramsEnabled = true; alerts.NewCountriesEnabled = true; alerts.HighTrafficEnabled = true; alerts.HighTrafficBytesPerSecond = 1024; alerts.Enabled = true;
        alerts.Update(Snapshot(sample, "Clash", a), resolver);
        Check(lookups == 1 && events.Count == 0, "First sample emitted alerts");
        sample = sample.AddSeconds(1);
        ConnectionInfo pidChanged = Row("a", "C:\\Example\\a.exe", "a.exe", 200, "192.0.2.1", 1000);
        alerts.Update(Snapshot(sample, "Clash", pidChanged), resolver);
        Check(events.Count == 0, "PID change treated as a new program or usable speed");
        sample = sample.AddSeconds(1);
        ConnectionInfo faster = Row("a", "C:\\Example\\a.exe", "a.exe", 200, "192.0.2.1", 5000);
        ConnectionInfo b = Row("b", "C:\\Example\\b.exe", "b.exe", 200, "192.0.2.2", 0);
        alerts.Update(Snapshot(sample, "Clash", faster, b), resolver);
        Check(events.Count == 3 && events.Any(x => x.Contains("程序")) && events.Any(x => x.Contains("地区")) && events.Any(x => x.Contains("流量")), "Expected one new program, country and traffic alert");
        Check(events.All(x => !x.Contains("192.0.2") && !x.Contains("Example") && !x.Contains(".exe") && !x.Contains("example.test")), "Sensitive data escaped into notification");

        clock = clock.AddMinutes(1); sample = sample.AddSeconds(1);
        ConnectionInfo c = Row("c", "C:\\Example\\c.exe", "c.exe", 300, "192.0.2.3", 0);
        alerts.Update(Snapshot(sample, "Clash", faster, b, c), resolver);
        Check(events.Count == 3, "Per-category throttle failed");
        clock = clock.AddMinutes(5); sample = sample.AddSeconds(1);
        ConnectionInfo d = Row("d", "C:\\Example\\d.exe", "d.exe", 400, "192.0.2.4", 0);
        alerts.Update(Snapshot(sample, "Clash", faster, b, c, d), resolver);
        Check(events.Count == 5, "New programs/countries not alerted after cooldown");
        alerts.ResetBaseline(); sample = sample.AddSeconds(20);
        ConnectionInfo e = Row("e", "C:\\Example\\e.exe", "e.exe", 500, "192.0.2.5", 0);
        alerts.Update(Snapshot(sample, "Clash", faster, b, c, d, e), resolver);
        Check(events.Count == 5, "Pause/resume emitted retrospective new-activity alert");
        sample = sample.AddSeconds(1); alerts.Update(Snapshot(sample, "Clash", faster, b, c, d, e), resolver);
        Check(events.Count == 5, "Existing programs re-alerted after resume");
        sample = sample.AddSeconds(1);
        NetworkSnapshot failed = Snapshot(sample, "Clash", faster); failed.ClashConnected = false;
        alerts.Update(failed, resolver);
        sample = sample.AddSeconds(1); alerts.Update(Snapshot(sample, "Clash", faster, b, c, d, e), resolver);
        Check(events.Count == 5, "Disconnect/reconnect emitted retrospective alerts");
        sample = sample.AddSeconds(1); alerts.Update(Snapshot(sample, "Windows 系统连接", faster, b), resolver);
        Check(events.Count == 5, "Source switch emitted alerts");
        sample = sample.AddSeconds(20); alerts.Update(Snapshot(sample, "Windows 系统连接", faster, b, c), resolver);
        Check(events.Count == 5, "Stale interval emitted alerts");
        int before = lookups; alerts.Enabled = false; sample = sample.AddSeconds(1); alerts.Update(Snapshot(sample, "Clash", d), resolver);
        Check(lookups == before && events.Count == 5, "Disabled mode continued querying countries");
        alerts.Enabled = true; sample = sample.AddSeconds(1); alerts.Update(Snapshot(sample, "Clash", faster, b, c, d, e), resolver);
        Check(events.Count == 5, "Re-enable failed to establish baseline");
        bool rejected = false; try { alerts.HighTrafficBytesPerSecond = Double.NaN; } catch (ArgumentOutOfRangeException) { rejected = true; }
        Check(rejected, "Invalid threshold accepted");
        sample = sample.AddSeconds(1); alerts.Update(Snapshot(sample, "Clash", faster, b), delegate(string ip) { throw new InvalidOperationException(); });
        Check(events.Count == 5, "Unavailable country cache caused a spurious alert");
    }
    private static void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
    private static NetworkSnapshot Snapshot(DateTime at, string source, params ConnectionInfo[] rows)
    { return new NetworkSnapshot { CapturedAt = at, SourceName = source, ControllerSummary = source, ClashConnected = source == "Clash", Connections = new List<ConnectionInfo>(rows) }; }
    private static ConnectionInfo Row(string id, string path, string name, int pid, string ip, long download)
    { return new ConnectionInfo { Id = id, Protocol = "TCP", LocalAddress = "127.0.0.1", LocalPort = 5000, RemoteAddress = ip, RemotePort = 443, ProcessId = pid, ProcessName = name, ProcessPath = path, Host = "example.test", DataSource = "Clash / Mihomo", HasTrafficCounters = true, DownloadBytes = download, UploadBytes = 0 }; }
}
