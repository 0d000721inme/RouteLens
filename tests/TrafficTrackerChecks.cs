using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using RouteLens;

internal static class TrafficTrackerChecksV6
{
    private static int checks;
    private static readonly DateTime At = new DateTime(2020, 1, 1, 0, 0, 0);

    private static void Check(bool condition, string description)
    {
        checks++;
        if (!condition) throw new Exception("FAILED: " + description);
    }
    private static void Near(double actual, double expected, string description)
    {
        Check(Math.Abs(actual - expected) < .001, description + ": " + actual);
    }
    private static ConnectionInfo Row(string id, long down, long up)
    {
        return new ConnectionInfo { Id = id, DownloadBytes = down, UploadBytes = up,
            DataSource = "Clash / Mihomo", ProcessName = "Example.exe", ProcessPath = @"C:\Apps\Example.exe",
            ProcessId = 111, Protocol = "TCP", LocalAddress = "192.0.2.1", LocalPort = 20000,
            RemoteAddress = "198.51.100.1", RemotePort = 443 };
    }
    private static NetworkSnapshot Snapshot(int second, params ConnectionInfo[] rows)
    {
        return new NetworkSnapshot { CapturedAt = At.AddSeconds(second), ClashConnected = true,
            SourceName = "Clash / Mihomo", ControllerSummary = "fictional controller",
            Connections = new List<ConnectionInfo>(rows) };
    }
    private static TrafficReport Accept(TrafficTracker tracker, NetworkSnapshot snapshot)
    {
        tracker.Update(snapshot);
        return tracker.Build(snapshot.Connections, false);
    }
    private static void Main()
    {
        TrafficTracker tracker = new TrafficTracker();
        ConnectionInfo first = Row("a", 1000, 200);
        TrafficReport report = Accept(tracker, Snapshot(0, first));
        Check(report.Available && !report.Measuring, "first sample supports cumulative but no invented rate");
        Check(report.DownloadBytes == 1000 && report.CounterConnectionCount == 1, "first cumulative retained");
        ConnectionInfo a = Row("a", 1600, 300), b = Row("b", 900, 90);
        b.ProcessId = 222;
        report = Accept(tracker, Snapshot(2, a, b));
        Check(report.Programs.Count == 1 && report.Programs[0].Connections == 2, "same path across instances grouped");
        Check(report.Measuring && report.MeasuredConnectionCount == 1, "new connection is not retroactively timed");
        Near(report.DownloadPerSecond, 300, "download delta divided by seconds");
        Near(report.UploadPerSecond, 50, "upload delta divided by seconds");
        Check(report.DownloadBytes == 2500 && report.UploadBytes == 390, "active cumulative sum");
        report = tracker.Build(new[] { b }, false);
        Check(!report.Measuring && report.DownloadBytes == 900, "filtered subset excludes other program connection rates");
        report = tracker.Build(new[] { a }, false);
        Near(report.DownloadPerSecond, 300, "Build does not consume or modify rate");
        report = tracker.Build(new[] { a, a }, false);
        Check(report.ConnectionCount == 1 && report.DownloadBytes == 1600, "duplicate enumeration not double counted");
        report = tracker.Build(new[] { a }, true);
        Check(report.Available && !report.Measuring && report.DownloadPerSecond == 0 && report.DownloadBytes == 1600,
            "pause keeps cumulative and hides speed");
        a = Row("a", 1800, 340); b = Row("b", 900, 90); b.ProcessId = 222;
        report = Accept(tracker, Snapshot(3, a, b));
        Check(report.MeasuredConnectionCount == 2, "second new-connection sample measured");
        Near(report.DownloadPerSecond, 200, "idle measured connection adds genuine zero");
        report = Accept(tracker, Snapshot(4, Row("a", 100, 350)));
        Check(!report.Measuring && report.DownloadPerSecond == 0, "counter decrease invalidates rate");
        report = Accept(tracker, Snapshot(5, Row("a", 200, 360)));
        Near(report.DownloadPerSecond, 100, "counter reset recovers from fresh baseline");
        Check(report.ConnectionCount == 1 && report.DownloadBytes == 200, "closed connections leave cumulative totals");

        ConnectionInfo changed = Row("a", 400, 380); changed.RemoteAddress = "203.0.113.2";
        report = Accept(tracker, Snapshot(6, changed));
        Check(!report.Measuring, "reused identifier with different endpoint resets");
        changed = Row("a", 600, 400); changed.ProcessPath = @"C:\Other\Example.exe";
        report = Accept(tracker, Snapshot(7, changed));
        Check(!report.Measuring, "reused identifier with different program resets");
        NetworkSnapshot otherController = Snapshot(8, changed); otherController.ControllerSummary = "other fictional controller";
        report = Accept(tracker, otherController);
        Check(!report.Measuring, "controller change resets");
        report = Accept(tracker, Snapshot(9, Row("a", 800, 410)));
        Check(!report.Measuring, "controller changed back still needs baseline");
        report = Accept(tracker, Snapshot(15, Row("a", 1000, 420)));
        Check(!report.Measuring, "long sample gap is not shown as live speed");
        report = Accept(tracker, Snapshot(15, Row("a", 1200, 430)));
        Check(!report.Measuring, "zero time gap has no division or fabricated speed");
        report = Accept(tracker, Snapshot(14, Row("a", 1400, 440)));
        Check(!report.Measuring, "backward clock jump has no negative speed");
        report = Accept(tracker, Snapshot(15, Row("a", 1500, 450)));
        Near(report.DownloadPerSecond, 100, "clock jump recovers next interval");
        typeof(TrafficTracker).GetField("_receivedAt", BindingFlags.NonPublic | BindingFlags.Instance)
            .SetValue(tracker, Stopwatch.GetTimestamp() - 6 * Stopwatch.Frequency);
        report = tracker.Build(new[] { Row("a", 1500, 450) }, false);
        Check(report.Available && !report.Measuring && report.Status.Contains("过期"), "stale wall age hides speed independent of historical fixture date");
        report = Accept(tracker, Snapshot(16, Row("a", 1800, 460)));
        Check(!report.Measuring, "stale reception gap resets baseline even if capture timestamps are close");
        report = Accept(tracker, Snapshot(17, Row("a", 1900, 470)));
        Near(report.DownloadPerSecond, 100, "fresh after stale resumes normally");
        NetworkSnapshot error = Snapshot(18, Row("a", 2000, 480)); error.Error = "fictional error";
        report = Accept(tracker, error);
        Check(!report.Available && !report.Measuring && report.DownloadBytes == 0, "failed read never displays stale cumulative");
        report = Accept(tracker, Snapshot(19, Row("a", 2200, 490)));
        Check(report.Available && !report.Measuring, "recovery starts new baseline");
        NetworkSnapshot native = Snapshot(20, Row("a", 2400, 500)); native.ClashConnected = false;
        report = Accept(tracker, native);
        Check(!report.Available && !report.Measuring, "Windows source has no guessed traffic");
        tracker.Reset();
        report = Accept(tracker, Snapshot(21, Row("a", 2600, 510)));
        Check(!report.Measuring, "explicit reset prevents paused interval averaging");
        Check(first.DownloadBytes == 1000 && first.UploadBytes == 200 && first.ProcessName == "Example.exe", "external connection data unchanged");

        ConnectionInfo missing = Row("m", 100, 10); missing.HasTrafficCounters = false;
        ConnectionInfo inferred = Row("i", 200, 20); inferred.HasTrafficCounters = null;
        ConnectionInfo noSource = Row("n", 300, 30); noSource.HasTrafficCounters = null; noSource.DataSource = "Windows";
        ConnectionInfo negative = Row("x", -1, 40); negative.HasTrafficCounters = true;
        report = Accept(tracker, Snapshot(22, missing, inferred, noSource, negative));
        Check(report.Available && report.ConnectionCount == 4 && report.CounterConnectionCount == 1, "counter presence enforced, legacy Clash null supported");
        Check(report.DownloadBytes == 200 && report.Status.Contains("3 条连接未提供计数"), "missing counters not silently summed as zero");
        missing = Row("m", 10000, 500); missing.HasTrafficCounters = true;
        report = Accept(tracker, Snapshot(23, missing));
        Check(!report.Measuring, "missing counters do not supply future rate baseline");
        report = Accept(tracker, Snapshot(24, Row("m", 10100, 600)));
        Near(report.DownloadPerSecond, 100, "restored counters recover on second sample");
        ConnectionInfo idle = Row("zero", 0, 0); idle.HasTrafficCounters = true;
        Accept(tracker, Snapshot(25, idle));
        report = Accept(tracker, Snapshot(26, Row("zero", 0, 0)));
        Check(report.Measuring && report.MeasuredConnectionCount == 1 && report.DownloadPerSecond == 0, "valid zero rate differs from waiting");

        ConnectionInfo unknownA = Row("ua", 2, 1), unknownB = Row("ub", 3, 1), namedNoPid = Row("np", 4, 1);
        unknownA.ProcessPath = unknownB.ProcessPath = namedNoPid.ProcessPath = "";
        unknownA.ProcessName = "PID 111"; unknownB.ProcessName = "已结束 / 无归属"; namedNoPid.ProcessName = "Known.exe";
        unknownA.ProcessId = 111; unknownB.ProcessId = namedNoPid.ProcessId = 0;
        report = Accept(tracker, Snapshot(27, unknownA, unknownB, namedNoPid));
        Check(report.Programs.Count == 2, "unknown PIDs are not represented as identified programs");
        ProgramTraffic unknown = report.Programs.Find(delegate(ProgramTraffic p) { return p.Name == "未识别程序"; });
        Check(unknown != null && unknown.Connections == 2 && unknown.DownloadBytes == 5, "unknown attribution is explicit bucket");
        Check(report.Programs.Exists(delegate(ProgramTraffic p) { return p.Name == "Known.exe"; }), "known Clash name valid without native PID");

        ConnectionInfo p1 = Row("p1", 2, 1), p2 = Row("p2", 3, 1), p3 = Row("p3", 4, 1);
        p1.ProcessPath = @"C:\Apps\Example.exe"; p2.ProcessPath = @"c:/apps/EXAMPLE.exe"; p3.ProcessPath = @"D:\Apps\Example.exe";
        report = Accept(tracker, Snapshot(28, p1, p2, p3));
        Check(report.Programs.Count == 2, "paths normalized case-insensitively without merging different installs");
        ConnectionInfo huge1 = Row("h1", Int64.MaxValue, Int64.MaxValue), huge2 = Row("h2", Int64.MaxValue, Int64.MaxValue);
        report = Accept(tracker, Snapshot(29, huge1, huge2));
        Check(report.DownloadBytes == Int64.MaxValue && report.UploadBytes == Int64.MaxValue, "cumulative arithmetic saturates safely");
        Check(report.Programs[0].DownloadBytes == Int64.MaxValue, "program cumulative saturates safely");
        report = Accept(tracker, Snapshot(30, Row("dup", 100, 10), Row("dup", 900, 90)));
        Check(!report.Measuring && report.ConnectionCount == 1, "duplicate id cannot generate a speed or double count");
        report = Accept(tracker, Snapshot(31, Row("dup", 1000, 100)));
        Check(!report.Measuring, "ambiguous duplicate id cannot form next baseline");
        report = Accept(tracker, Snapshot(32, Row("", 2, 1)));
        Check(report.Available && !report.Measuring && report.DownloadBytes == 2, "missing id permits cumulative only");
        report = tracker.Build(new ConnectionInfo[0], false);
        Check(report.Available && !report.Measuring && report.ConnectionCount == 0 && report.Status.Contains("没有连接"), "empty filter accurate");
        tracker.Update(null);
        Check(!tracker.Build(null, false).Available, "null snapshot clears state safely");
        Console.WriteLine("Traffic tracker passed " + checks + " checks.");
    }
}
