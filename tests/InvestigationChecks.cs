using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using RouteLens;

internal static class InvestigationChecks
{
    private static int checks;
    private static readonly DateTime At = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static void Check(bool condition, string description)
    {
        checks++;
        if (!condition) throw new Exception("FAILED: " + description);
    }
    private static void Near(double actual, double expected, string description)
    { Check(Math.Abs(actual - expected) < .001, description + ": " + actual); }

    private static ConnectionInfo Row(string id, long upload)
    {
        return new ConnectionInfo { Id = id, UploadBytes = upload, DownloadBytes = 100,
            DataSource = "Clash / Mihomo", HasTrafficCounters = true,
            ProcessName = "Example.exe", ProcessPath = @"C:\Users\PrivateUser\Apps\Example.exe",
            ProcessId = 111, Protocol = "TCP", LocalAddress = "192.0.2.1", LocalPort = 20000,
            Host = "private.example.test", RemoteAddress = "198.51.100.1", RemotePort = 443,
            ProxyChain = "Private node -> DIRECT", RouteType = "代理", ProcessAttribution = "Clash 元数据" };
    }
    private static NetworkSnapshot Snapshot(int second, params ConnectionInfo[] rows)
    {
        return new NetworkSnapshot { CapturedAt = At.AddSeconds(second), ClashConnected = true,
            SourceName = "Clash / Mihomo", ControllerSummary = "fictional controller",
            Connections = new List<ConnectionInfo>(rows) };
    }
    private static void Accept(InvestigationMonitor monitor, TrafficTracker tracker, NetworkSnapshot snapshot)
    {
        tracker.Update(snapshot);
        monitor.Observe(snapshot, tracker.Build(snapshot == null ? null : snapshot.Connections, false));
    }
    private static InvestigationMonitor Enabled()
    {
        InvestigationMonitor monitor = new InvestigationMonitor();
        monitor.Configure(true, 1024, 2);
        return monitor;
    }
    private static InvestigationCase Trigger(InvestigationMonitor monitor, TrafficTracker tracker)
    {
        Accept(monitor, tracker, Snapshot(0, Row("a", 0)));
        Accept(monitor, tracker, Snapshot(1, Row("a", 2048)));
        Accept(monitor, tracker, Snapshot(2, Row("a", 4096)));
        Check(monitor.Snapshot().Length == 1, "two measured seconds produce one episode");
        return monitor.Snapshot()[0];
    }

    private static void DefaultsAndGroupDetection()
    {
        InvestigationMonitor monitor = new InvestigationMonitor();
        TrafficTracker tracker = new TrafficTracker();
        Check(!monitor.Enabled, "automatic recording is disabled by default");
        for (int i = 0; i < 5; i++) Accept(monitor, tracker, Snapshot(i, Row("a", i * 4096)));
        Check(monitor.Snapshot().Length == 0, "disabled observer does not collect automatic cases");
        monitor = Enabled(); tracker = new TrafficTracker();
        InvestigationCase item = Trigger(monitor, tracker);
        Check(item.Kind == "sustained-upload" && item.Conclusion == "已观察到异常", "rule records an anomaly, not infection or attack");
        Check(item.MeasuredConnections == 1 && item.ObservedConnections == 1, "coverage keeps count and denominator");
        Check(item.ObservedPids.Length == 1 && item.ObservedPids[0] == 111, "provided PID retained without inventing an instance");
        Near(item.DurationSeconds, 2, "duration includes measured intervals only");
        Near(item.PeakUploadBytesPerSecond, 2048, "peak measured from real counter deltas");
        string id = item.Id;
        Accept(monitor, tracker, Snapshot(3, Row("a", 8192)));
        item = monitor.Find(id);
        Check(monitor.Snapshot().Length == 1 && item.IsActive, "continuing episode updates instead of duplicating");
        Near(item.DurationSeconds, 3, "continued duration"); Near(item.PeakUploadBytesPerSecond, 4096, "continued peak");
        Accept(monitor, tracker, Snapshot(4, Row("a", 8192)));
        Check(!monitor.Find(id).IsActive, "below-threshold sample closes episode");

        monitor = Enabled(); tracker = new TrafficTracker();
        for (int i = 0; i < 5; i++)
        {
            ConnectionInfo a = Row("a", i * 600), b = Row("b", i * 600);
            b.ProcessName = "Other.exe"; b.ProcessPath = @"C:\Apps\Other.exe";
            Accept(monitor, tracker, Snapshot(i, a, b));
        }
        Check(monitor.Snapshot().Length == 0, "two below-threshold programs do not become one aggregate alert");
    }

    private static void ManualSnapshotsAndPrivacy()
    {
        InvestigationMonitor monitor = new InvestigationMonitor();
        ConnectionInfo row = Row("private-connection-id", 2560);
        row.ProcessId = 0; row.HasTrafficCounters = false;
        InvestigationCase item = monitor.CaptureConnection(Snapshot(0, row), row);
        Check(item != null && monitor.Snapshot().Length == 1, "manual snapshot works with automatic observer disabled");
        Check(item.Kind == "connection-snapshot" && item.Conclusion == "证据不足", "connection snapshot is not classified as anomalous");
        Check(item.ObservedPids.Length == 0, "missing PID remains unknown");
        Near(item.DurationSeconds, 0, "a cumulative snapshot has no measured duration");
        Check(item.Samples.TrueForAll(delegate(InvestigationSample s) { return !s.UploadBytesPerSecond.HasValue; }), "manual snapshot never invents an upload rate");
        string note = "manual note secret 203.0.113.55 " + @"C:\Users\PrivateUser\notes.txt";
        monitor.SetReview(item.Id, "expected", note);
        item = monitor.Find(item.Id);
        Check(item.ReviewStatus == "expected" && item.ReviewNote == note, "review state and note retained");
        Check(item.Conclusion == "证据不足", "review state does not rewrite observed evidence");
        string json = monitor.BuildExportJson(item.Id, false, false, false);
        Check(new JavaScriptSerializer().DeserializeObject(json) != null, "export is valid JSON");
        Check(!json.Contains("PrivateUser") && !json.Contains("private.example.test") && !json.Contains("198.51.100.1") && !json.Contains("203.0.113.55"), "default export omits paths, targets and freeform notes");
        Check(!json.Contains("private-connection-id") && !json.Contains("Private node"), "default export does not leak raw connection or proxy identifiers");
        json = monitor.BuildExportJson(item.Id, true, true, true);
        Check(json.Contains("PrivateUser") && json.Contains("private.example.test") && json.Contains("198.51.100.1") && json.Contains("manual note secret"), "explicit export options include requested local evidence");
        json = monitor.BuildExportJson(item.Id, false, true, false);
        Check(json.Contains("private.example.test") && !json.Contains("PrivateUser") && !json.Contains("manual note secret"), "target export does not implicitly include paths or notes");
        Check(monitor.Find(item.Id).ProgramPath == row.ProcessPath && monitor.Find(item.Id).Targets[0].Host == row.Host, "export redaction does not mutate retained evidence");
    }

    private static void Discontinuities()
    {
        InvestigationMonitor monitor = Enabled(); TrafficTracker tracker = new TrafficTracker();
        Accept(monitor, tracker, Snapshot(0, Row("a", 0)));
        Accept(monitor, tracker, Snapshot(1, Row("a", 2048)));
        Accept(monitor, tracker, Snapshot(10, Row("a", 20480)));
        Accept(monitor, tracker, Snapshot(11, Row("a", 22528)));
        Check(monitor.Snapshot().Length == 0, "sampling gap cannot bridge two short runs");
        Accept(monitor, tracker, Snapshot(12, Row("a", 24576)));
        Check(monitor.Snapshot().Length == 1, "a new complete interval can trigger after a gap");

        monitor = Enabled(); tracker = new TrafficTracker();
        Accept(monitor, tracker, Snapshot(0, Row("a", 0)));
        Accept(monitor, tracker, Snapshot(1, Row("a", 2048)));
        NetworkSnapshot changed = Snapshot(2, Row("a", 4096)); changed.ControllerSummary = "new controller";
        Accept(monitor, tracker, changed);
        changed = Snapshot(3, Row("a", 6144)); changed.ControllerSummary = "new controller"; Accept(monitor, tracker, changed);
        Check(monitor.Snapshot().Length == 0, "controller change starts a new baseline");

        monitor = Enabled(); tracker = new TrafficTracker();
        Accept(monitor, tracker, Snapshot(0, Row("a", 0)));
        Accept(monitor, tracker, Snapshot(1, Row("a", 2048)));
        Accept(monitor, tracker, Snapshot(2, Row("a", 0)));
        Accept(monitor, tracker, Snapshot(3, Row("a", 2048)));
        Check(monitor.Snapshot().Length == 0, "counter reset interrupts a run instead of reusing previous evidence");

        monitor = Enabled(); tracker = new TrafficTracker();
        InvestigationCase active = Trigger(monitor, tracker);
        monitor.Interrupt("用户暂停采样"); tracker.Reset();
        Check(!monitor.Find(active.Id).IsActive && monitor.Find(active.Id).EndReason.Length > 0, "pause closes the active case with a reason");
        Accept(monitor, tracker, Snapshot(3, Row("a", 6144)));
        Accept(monitor, tracker, Snapshot(4, Row("a", 8192)));
        Check(monitor.Snapshot().Length == 1, "resume needs a complete new interval");
        Accept(monitor, tracker, Snapshot(5, Row("a", 10240)));
        Check(monitor.Snapshot().Length == 2, "new episode after resume has a separate record");

        monitor = Enabled(); tracker = new TrafficTracker();
        active = Trigger(monitor, tracker);
        NetworkSnapshot failed = Snapshot(3, Row("a", 6144)); failed.Error = "fictional read failure";
        Accept(monitor, tracker, failed);
        Check(!monitor.Find(active.Id).IsActive, "failed read closes active observation");
        Accept(monitor, tracker, Snapshot(4, Row("a", 8192)));
        Accept(monitor, tracker, Snapshot(5, Row("a", 10240)));
        Check(monitor.Snapshot().Length == 1, "recovery does not bridge an unavailable period");
    }

    private static void DuplicateMissingAndStale()
    {
        InvestigationMonitor monitor = Enabled(); TrafficTracker tracker = new TrafficTracker();
        InvestigationCase active = Trigger(monitor, tracker);
        double duration = active.DurationSeconds;
        Accept(monitor, tracker, Snapshot(2, Row("a", 4096)));
        Check(monitor.Snapshot().Length == 1, "repeated timestamp does not create another record");
        Near(monitor.Find(active.Id).DurationSeconds, duration, "repeated timestamp cannot add duration");

        monitor = Enabled(); tracker = new TrafficTracker();
        Accept(monitor, tracker, Snapshot(0, Row("a", 0)));
        Accept(monitor, tracker, Snapshot(1, Row("a", 2048)));
        ConnectionInfo missing = Row("a", 4096); missing.HasTrafficCounters = false;
        Accept(monitor, tracker, Snapshot(2, missing));
        Accept(monitor, tracker, Snapshot(3, Row("a", 6144)));
        Accept(monitor, tracker, Snapshot(4, Row("a", 8192)));
        Check(monitor.Snapshot().Length == 0, "missing counter interval cannot be treated as a measured zero or bridge a run");

        monitor = Enabled(); tracker = new TrafficTracker();
        Accept(monitor, tracker, Snapshot(0, Row("a", 0)));
        Accept(monitor, tracker, Snapshot(1, Row("a", 2048)));
        ConnectionInfo newlySeen = Row("new", 0);
        Accept(monitor, tracker, Snapshot(2, Row("a", 4096), newlySeen));
        Accept(monitor, tracker, Snapshot(3, Row("a", 6144), Row("new", 0)));
        Check(monitor.Snapshot().Length == 0, "partially measured program group cannot bridge continuity across a new connection");

        monitor = Enabled(); tracker = new TrafficTracker();
        Accept(monitor, tracker, Snapshot(0, Row("a", 0)));
        Accept(monitor, tracker, Snapshot(1, Row("a", 2048)));
        ConnectionInfo changedPid = Row("a", 4096); changedPid.ProcessId = 222;
        Accept(monitor, tracker, Snapshot(2, changedPid));
        changedPid = Row("a", 6144); changedPid.ProcessId = 222;
        Accept(monitor, tracker, Snapshot(3, changedPid));
        Check(monitor.Snapshot().Length == 0, "connection identity change cannot carry another PID's measurement into the run");

        monitor = Enabled(); tracker = new TrafficTracker(); active = Trigger(monitor, tracker);
        NetworkSnapshot stale = Snapshot(3, Row("a", 6144)); tracker.Update(stale);
        TrafficReport paused = tracker.Build(stale.Connections, true);
        monitor.Observe(stale, paused);
        Check(!monitor.Find(active.Id).IsActive, "unfresh report cannot extend an active case");

        monitor = Enabled(); tracker = new TrafficTracker();
        for (int i = 0; i < 4; i++)
        {
            ConnectionInfo a = Row("ambiguous", i * 2048), b = Row("ambiguous", i * 4096);
            Accept(monitor, tracker, Snapshot(i, a, b, null));
        }
        Check(monitor.Snapshot().Length == 0, "duplicate connection IDs and null rows cannot supply false evidence");
        Accept(monitor, tracker, null);
        Check(monitor.Snapshot().Length == 0, "null sample is handled as unavailable");
    }

    private static void BoundsAndReset()
    {
        InvestigationMonitor monitor = new InvestigationMonitor(); string first = null;
        for (int i = 0; i < 205; i++)
        {
            ConnectionInfo row = Row("manual-" + i, i);
            InvestigationCase item = monitor.CaptureConnection(Snapshot(i, row), row);
            if (i == 0) first = item.Id;
        }
        Check(monitor.Snapshot().Length == 200 && monitor.EvictedCount == 5, "case retention is bounded and eviction is visible");
        Check(monitor.Find(first) == null, "oldest evicted record is no longer reported as available");
        monitor.Clear(); Check(monitor.Snapshot().Length == 0, "clear removes session cases");

        monitor = Enabled(); TrafficTracker tracker = new TrafficTracker(); InvestigationCase active = Trigger(monitor, tracker);
        monitor.Configure(false, 1024, 2);
        Check(!monitor.Enabled && !monitor.Find(active.Id).IsActive, "disabling preserves completed evidence but stops the run");
        Accept(monitor, tracker, Snapshot(3, Row("a", 8192)));
        Check(monitor.Snapshot().Length == 1, "disabled rule does not extend evidence");
        monitor.Configure(true, 1024, 2); monitor.Clear(); tracker.Reset();
        Accept(monitor, tracker, Snapshot(10, Row("a", 20480)));
        Accept(monitor, tracker, Snapshot(11, Row("a", 22528)));
        Check(monitor.Snapshot().Length == 0, "clear does not carry a partially completed run into new evidence");

        monitor = Enabled(); tracker = new TrafficTracker();
        active = Trigger(monitor, tracker);
        for (int i = 3; i < 135; i++) Accept(monitor, tracker, Snapshot(i, Row("a", i * 2048)));
        active = monitor.Find(active.Id);
        Check(monitor.Snapshot().Length == 1 && active.Samples.Count <= 120 && active.SamplesTruncated, "long episode keeps a bounded sample window and discloses truncation");
        Near(active.DurationSeconds, 134, "sample retention does not reset episode duration");

        bool rejected = false;
        try { monitor.Configure(true, Double.NaN, 2); } catch (ArgumentOutOfRangeException) { rejected = true; }
        Check(rejected, "non-finite thresholds are rejected");
    }

    private static void TargetEvidenceScope()
    {
        InvestigationMonitor monitor = new InvestigationMonitor(); monitor.Configure(true, 1024, 1);
        TrafficTracker tracker = new TrafficTracker();
        for (int i = 0; i < 2; i++)
        {
            ConnectionInfo a = Row("a", i * 1024), b = Row("b", i * 1024);
            b.ProcessPath = b.ProcessPath.ToLowerInvariant(); b.Host = "other.example.test"; b.RemoteAddress = "198.51.100.2";
            Accept(monitor, tracker, Snapshot(i, a, b));
        }
        InvestigationCase item = monitor.Snapshot()[0];
        Check(item.Targets.Exists(delegate(InvestigationTarget t) { return t.PositiveUpload && t.Host == "other.example.test"; }), "case-insensitive program grouping preserves target-group upload evidence");
        Check(item.Targets.TrueForAll(delegate(InvestigationTarget t) { return !t.PositiveUpload || (t.EvidenceScope == "upload-target-group" && String.IsNullOrEmpty(t.Address) && t.Port == 0); }), "group upload is never presented as an exact IP-port attribution");
        Check(item.Targets.Exists(delegate(InvestigationTarget t) { return t.EvidenceScope == "recent-endpoint" && t.Address == "198.51.100.2" && !t.PositiveUpload; }), "recent endpoint remains distinct from target-group positive delta");
    }

    private static void Main()
    {
        DefaultsAndGroupDetection(); ManualSnapshotsAndPrivacy(); Discontinuities(); DuplicateMissingAndStale(); BoundsAndReset(); TargetEvidenceScope(); SnapshotCountersAndEndpoints();
        Console.WriteLine("PASS: " + checks + " behavior investigation checks (synthetic snapshots, no network traffic).");
    }

    private static void SnapshotCountersAndEndpoints()
    {
        InvestigationMonitor monitor = new InvestigationMonitor(); ConnectionInfo row = Row("manual", 2500);
        row.DownloadBytes = 6800; row.ProcessAttribution = "private attribution C:\\Private\\source";
        InvestigationCase item = monitor.CaptureConnection(Snapshot(0, row), row);
        Check(item.CapturedUploadBytes == 2500 && item.CapturedDownloadBytes == 6800, "manual snapshot preserves valid cumulative bytes");
        Check(item.Samples.TrueForAll(s => !s.UploadBytesPerSecond.HasValue), "cumulative counters do not imply rate");
        string json = monitor.BuildExportJson(item.Id, false, false, false);
        Check(!json.Contains("private attribution") && !json.Contains("192.0.2.1"), "default export hides raw attribution and local address");
        Check(monitor.BuildExportJson(item.Id, true, false, false).Contains("private attribution"), "explicit process export preserves reported attribution");
        row.HasTrafficCounters = false; item = monitor.CaptureConnection(Snapshot(1, row), row);
        Check(!item.CapturedUploadBytes.HasValue && !item.CapturedDownloadBytes.HasValue, "unavailable counters are unknown rather than zero");
        row.HasTrafficCounters = true; row.UploadBytes = -1; item = monitor.CaptureConnection(Snapshot(2, row), row);
        Check(!item.CapturedUploadBytes.HasValue && !item.CapturedDownloadBytes.HasValue, "invalid counter pair is rejected");
        row.Protocol = "UDP"; row.RemoteAddress = ""; row.RemotePort = 0; row.LocalAddress = "2001:db8::1"; row.LocalPort = 54321;
        item = monitor.CaptureConnection(new NetworkSnapshot { CapturedAt = At, Connections = new List<ConnectionInfo> { row } }, row);
        Check(item.Targets[0].LocalAddress == "2001:db8::1" && item.Targets[0].LocalPort == 54321 && item.Targets[0].Address == "", "Windows UDP retains its only known local endpoint without inventing remote");
        Check(monitor.BuildExportJson(item.Id, false, true, false).Contains("2001:db8::1"), "explicit endpoint export preserves local IPv6");
    }
}
