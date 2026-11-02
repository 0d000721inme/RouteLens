using System;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

namespace RouteLens
{
    public sealed class InvestigationSample
    {
        public DateTime TimeUtc;
        public double? UploadBytesPerSecond;
        public int MeasuredConnections, ObservedConnections;
    }

    public sealed class InvestigationTarget
    {
        public string Host, Address, LocalAddress, Protocol, ProxyChain, EvidenceScope;
        public int Port, LocalPort;
        public bool PositiveUpload;
    }

    public sealed class InvestigationCase
    {
        public string Id, Kind, ProgramName, ProgramPath, Source, Conclusion, Attribution, ReportedAttribution, Coverage, EndReason, ReviewStatus, ReviewNote;
        public DateTime OpenedAtUtc, LastSeenAtUtc;
        public double DurationSeconds, PeakUploadBytesPerSecond, ThresholdBytesPerSecond, RequiredSeconds;
        public long? CapturedUploadBytes, CapturedDownloadBytes;
        public int MeasuredConnections, ObservedConnections;
        public bool IsActive, SamplesTruncated, TargetsTruncated, PidsTruncated;
        public int[] ObservedPids = new int[0];
        public List<InvestigationTarget> Targets = new List<InvestigationTarget>();
        public List<InvestigationSample> Samples = new List<InvestigationSample>();
    }

    // This class consumes the existing unfiltered Clash traffic report. It neither
    // samples the machine independently nor persists any connection or review data.
    public sealed class InvestigationMonitor
    {
        private const int MaximumCases = 200, MaximumRuns = 512, MaximumSamples = 120, MaximumTargets = 40, MaximumPids = 32;
        private readonly List<InvestigationCase> _cases = new List<InvestigationCase>();
        private readonly Dictionary<string, Run> _runs = new Dictionary<string, Run>(StringComparer.OrdinalIgnoreCase);
        private bool _enabled;
        private double _threshold = 1048576, _requiredSeconds = 10;
        private DateTime _lastSnapshot;
        private string _source;
        private string _state = "观察关闭；可以手动保存连接快照。";
        private long _revision;
        private int _evicted;

        private sealed class Run
        {
            public InvestigationCase Item;
            public bool Published;
        }

        public bool Enabled { get { return _enabled; } }
        public double ThresholdBytesPerSecond { get { return _threshold; } }
        public double RequiredSeconds { get { return _requiredSeconds; } }
        public string State { get { return _state; } }
        public long Revision { get { return _revision; } }
        public int EvictedCount { get { return _evicted; } }
        public event Action<InvestigationCase> CaseCreated;

        public void Configure(bool enabled, double thresholdBytesPerSecond, double requiredSeconds)
        {
            if (!Finite(thresholdBytesPerSecond) || thresholdBytesPerSecond < 1024 || thresholdBytesPerSecond > 10737418240d)
                throw new ArgumentOutOfRangeException("thresholdBytesPerSecond");
            if (!Finite(requiredSeconds) || requiredSeconds < 1 || requiredSeconds > 3600)
                throw new ArgumentOutOfRangeException("requiredSeconds");
            if (_enabled == enabled && _threshold == thresholdBytesPerSecond && _requiredSeconds == requiredSeconds) return;
            Interrupt(enabled ? "观察设置已改变，重新建立基线。" : "用户关闭了行为观察。");
            _enabled = enabled; _threshold = thresholdBytesPerSecond; _requiredSeconds = requiredSeconds;
            SetState(enabled ? "等待连续采样；仅观察 Clash 已计速的程序组。" : "观察关闭；现有记录仅保留在本次运行内存中。");
        }

        public void Observe(NetworkSnapshot snapshot, TrafficReport report)
        {
            if (!_enabled) return;
            if (snapshot == null || snapshot.Connections == null || !String.IsNullOrWhiteSpace(snapshot.Error) ||
                !snapshot.ClashConnected || report == null || !report.Available || !report.Fresh || report.SnapshotAt != snapshot.CapturedAt ||
                snapshot.CapturedAt == default(DateTime))
            {
                Interrupt("当前来源不可计速或数据已过期；此时段没有连续行为观察。");
                return;
            }
            string source = Text(snapshot.SourceName) + "\n" + Text(snapshot.ControllerSummary);
            double interval = (snapshot.CapturedAt - _lastSnapshot).TotalSeconds;
            if (_lastSnapshot == default(DateTime) || interval <= 0 || interval > 5 || !String.Equals(_source, source, StringComparison.Ordinal))
            {
                Interrupt("采样时间、间隔或数据源改变，连续观察已中断。");
                _source = source; _lastSnapshot = snapshot.CapturedAt;
                SetState("基线已建立；等待下一次有效计速采样。");
                return;
            }
            _source = source; _lastSnapshot = snapshot.CapturedAt;
            Dictionary<string, List<ConnectionInfo>> groupedRows = new Dictionary<string, List<ConnectionInfo>>(StringComparer.OrdinalIgnoreCase);
            foreach (ConnectionInfo row in snapshot.Connections)
            {
                if (row == null) continue;
                string key = TrafficTracker.GetProgramKey(row); List<ConnectionInfo> rows;
                if (!groupedRows.TryGetValue(key, out rows)) { rows = new List<ConnectionInfo>(); groupedRows.Add(key, rows); }
                rows.Add(row);
            }
            Dictionary<string, List<TargetTraffic>> groupedTargets = new Dictionary<string, List<TargetTraffic>>(StringComparer.OrdinalIgnoreCase);
            foreach (TargetTraffic target in report.Targets)
            {
                if (target == null || !target.Measured || !target.PositiveUpload || target.Name == null || target.Key == null ||
                    !target.Key.EndsWith("\n" + target.Name, StringComparison.OrdinalIgnoreCase)) continue;
                string key = target.Key.Substring(0, target.Key.Length - target.Name.Length - 1); List<TargetTraffic> targets;
                if (!groupedTargets.TryGetValue(key, out targets)) { targets = new List<TargetTraffic>(); groupedTargets.Add(key, targets); }
                targets.Add(target);
            }
            HashSet<string> continued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool capacityLimited = false;
            foreach (ProgramTraffic program in report.Programs)
            {
                if (program == null || String.IsNullOrWhiteSpace(program.Key)) continue;
                // A counter reset, new connection or missing measurement must not
                // be silently joined to a previous continuous interval.
                bool qualifying = program.Connections > 0 && program.MeasuredConnections == program.Connections &&
                    Finite(program.UploadPerSecond) && program.UploadPerSecond >= _threshold;
                if (!qualifying) continue;
                if (!continued.Add(program.Key)) continue;
                Run run;
                if (!_runs.TryGetValue(program.Key, out run))
                {
                    if (_runs.Count >= MaximumRuns) { capacityLimited = true; continue; }
                    run = new Run { Item = NewCase("sustained-upload", program.Name, program.Path, snapshot, program.Key) };
                    run.Item.OpenedAtUtc = Utc(snapshot.CapturedAt.AddSeconds(-interval));
                    run.Item.IsActive = true; _runs.Add(program.Key, run);
                }
                InvestigationCase item = run.Item;
                item.LastSeenAtUtc = Utc(snapshot.CapturedAt);
                item.DurationSeconds += interval;
                item.PeakUploadBytesPerSecond = Math.Max(item.PeakUploadBytesPerSecond, program.UploadPerSecond);
                item.MeasuredConnections = program.MeasuredConnections; item.ObservedConnections = program.Connections;
                item.Coverage = Coverage(program.MeasuredConnections, program.Connections);
                AddSample(item, snapshot.CapturedAt, program.UploadPerSecond, program.MeasuredConnections, program.Connections);
                List<ConnectionInfo> rows; List<TargetTraffic> targets;
                if (groupedRows.TryGetValue(program.Key, out rows))
                {
                    groupedTargets.TryGetValue(program.Key, out targets);
                    ObserveTargets(item, program.Key, rows, targets);
                }
                if (!run.Published && item.DurationSeconds >= _requiredSeconds)
                {
                    run.Published = true; Publish(item);
                }
                else if (run.Published) _revision++;
            }
            List<string> ended = new List<string>();
            foreach (KeyValuePair<string, Run> pair in _runs)
                if (!continued.Contains(pair.Key)) ended.Add(pair.Key);
            foreach (string key in ended)
            {
                End(_runs[key], "速度低于条件、程序组不再可见，或有连接无法连续计速。", snapshot.CapturedAt);
                _runs.Remove(key);
            }
            SetState(capacityLimited ? "观察中；同时跟踪程序组达到上限，部分程序组未纳入。" :
                "观察中；仅 Clash 可见程序组。已计速 " + report.MeasuredConnectionCount + " / " + report.ConnectionCount + " 条连接，全机覆盖率未知。");
        }

        public void Interrupt(string reason)
        {
            foreach (Run run in _runs.Values) End(run, reason, default(DateTime));
            _runs.Clear(); _source = null; _lastSnapshot = default(DateTime);
            if (_enabled) SetState(Text(reason));
        }

        public InvestigationCase CaptureConnection(NetworkSnapshot snapshot, ConnectionInfo row)
        {
            if (snapshot == null || row == null) return null;
            string key = TrafficTracker.GetProgramKey(row);
            InvestigationCase item = NewCase("connection-snapshot", row.ProcessName, row.ProcessPath, snapshot, key);
            item.ReportedAttribution = Limit(row.ProcessAttribution, 2048);
            if (HasCounters(row)) { item.CapturedUploadBytes = row.UploadBytes; item.CapturedDownloadBytes = row.DownloadBytes; }
            item.ObservedConnections = 1; item.MeasuredConnections = 0;
            item.Coverage = "手动保存的一条连接快照；未计算速度，不代表全机或持续活动。";
            item.EndReason = "手动快照不自动续采。";
            ObserveTargets(item, key, new[] { row }, null);
            AddSample(item, snapshot.CapturedAt, null, 0, 1);
            Publish(item);
            return item;
        }

        public InvestigationCase[] Snapshot()
        {
            InvestigationCase[] result = _cases.ToArray();
            Array.Reverse(result); return result;
        }

        public InvestigationCase Find(string id)
        {
            foreach (InvestigationCase item in _cases) if (item.Id == id) return item;
            return null;
        }

        public void SetReview(string id, string status, string note)
        {
            if (status != "unreviewed" && status != "investigating" && status != "expected" && status != "needs-more-evidence")
                throw new ArgumentOutOfRangeException("status");
            InvestigationCase item = Find(id); if (item == null) return;
            item.ReviewStatus = status; item.ReviewNote = Limit(note, 4000); _revision++;
        }

        public void Clear()
        {
            _runs.Clear(); _cases.Clear(); _source = null; _lastSnapshot = default(DateTime); _evicted = 0; _revision++;
            SetState(_enabled ? "记录已清空；等待重新建立采样基线。" : "观察关闭；本次调查记录已清空。");
        }

        public string BuildExportJson(string id, bool includePaths, bool includeDestinations, bool includeReviewNote)
        {
            InvestigationCase item = Find(id); if (item == null) throw new ArgumentException("调查记录不存在。", "id");
            List<object> targets = new List<object>();
            foreach (InvestigationTarget target in item.Targets)
                targets.Add(new Dictionary<string, object> {
                    { "evidenceScope", target.EvidenceScope }, { "positiveUpload", target.PositiveUpload },
                    { "host", includeDestinations ? target.Host : null }, { "address", includeDestinations ? target.Address : null },
                    { "port", includeDestinations && target.Port > 0 ? (object)target.Port : null },
                    { "localAddress", includeDestinations ? target.LocalAddress : null },
                    { "localPort", includeDestinations && target.LocalPort > 0 ? (object)target.LocalPort : null },
                    { "protocol", target.Protocol }, { "proxyChain", includeDestinations ? target.ProxyChain : null }
                });
            List<object> samples = new List<object>();
            foreach (InvestigationSample sample in item.Samples)
                samples.Add(new Dictionary<string, object> { { "timeUtc", Iso(sample.TimeUtc) }, { "uploadBytesPerSecond", sample.UploadBytesPerSecond },
                    { "measuredConnections", sample.MeasuredConnections }, { "observedConnections", sample.ObservedConnections } });
            Dictionary<string, object> document = new Dictionary<string, object> {
                { "schemaVersion", 1 }, { "application", "RouteLens" }, { "exportedAtUtc", Iso(DateTime.UtcNow) },
                { "id", item.Id }, { "kind", item.Kind }, { "source", item.Source }, { "conclusion", item.Conclusion },
                { "hostSafetyConclusion", null }, { "confirmedAttack", false }, { "independentVerification", false },
                { "programName", includePaths ? item.ProgramName : "程序组（已脱敏）" }, { "programPath", includePaths ? item.ProgramPath : null },
                { "reportedAttribution", includePaths ? item.ReportedAttribution : null },
                { "observedPids", includePaths ? (object)item.ObservedPids : null }, { "processInstanceVerified", false },
                { "openedAtUtc", Iso(item.OpenedAtUtc) }, { "lastSeenAtUtc", Iso(item.LastSeenAtUtc) },
                { "durationSeconds", item.Kind == "sustained-upload" ? (object)item.DurationSeconds : null },
                { "peakUploadBytesPerSecond", item.Kind == "sustained-upload" ? (object)item.PeakUploadBytesPerSecond : null },
                { "capturedUploadBytes", item.CapturedUploadBytes }, { "capturedDownloadBytes", item.CapturedDownloadBytes },
                { "thresholdBytesPerSecond", item.Kind == "sustained-upload" ? (object)item.ThresholdBytesPerSecond : null },
                { "requiredSeconds", item.Kind == "sustained-upload" ? (object)item.RequiredSeconds : null },
                { "isActive", item.IsActive }, { "attribution", item.Attribution }, { "coverage", item.Coverage },
                { "measuredConnections", item.MeasuredConnections }, { "observedConnections", item.ObservedConnections },
                { "endReason", item.EndReason }, { "reviewStatus", item.ReviewStatus }, { "reviewNote", includeReviewNote ? item.ReviewNote : null },
                { "targets", targets }, { "samples", samples }, { "targetsTruncated", item.TargetsTruncated },
                { "samplesTruncated", item.SamplesTruncated }, { "pidsTruncated", item.PidsTruncated }, { "evictedCaseCount", _evicted },
                { "privacy", new Dictionary<string, object> { { "pathsIncluded", includePaths }, { "destinationsIncluded", includeDestinations },
                    { "reviewNoteIncluded", includeReviewNote }, { "reviewNoteAutomaticallyRedacted", false } } },
                { "limits", "仅本次运行内存记录。程序组不是进程实例；域名组上传增量不等于具体连接归因。未合并网卡或隧道外层字节；未观察到异常不能排除感染。" }
            };
            return new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 }.Serialize(document);
        }

        private InvestigationCase NewCase(string kind, string name, string path, NetworkSnapshot snapshot, string key)
        {
            bool hasPath = !String.IsNullOrWhiteSpace(path), known = key != "unknown";
            string attribution = hasPath ? "按来源提供的程序路径分组；未验证文件签名、启动时间或真实发起者。" :
                known ? "仅按程序名分组；可能合并不同文件和多个进程实例。" : "来源未提供足够的程序身份；无法确认哪个进程发起。";
            attribution += " PID 只表示快照中报告的编号，不能证明同一进程实例或排除注入。";
            return new InvestigationCase {
                Id = Guid.NewGuid().ToString("N"), Kind = kind, ProgramName = Limit(String.IsNullOrWhiteSpace(name) ? "未识别程序" : name, 256),
                ProgramPath = Limit(path, 4096), Source = SourceName(snapshot),
                Conclusion = kind == "sustained-upload" ? "已观察到异常" : "证据不足", Attribution = attribution,
                OpenedAtUtc = Utc(snapshot.CapturedAt), LastSeenAtUtc = Utc(snapshot.CapturedAt), ThresholdBytesPerSecond = _threshold,
                RequiredSeconds = _requiredSeconds, ReviewStatus = "unreviewed", ReviewNote = "", EndReason = "", Coverage = "覆盖范围未知。"
            };
        }

        private void Publish(InvestigationCase item)
        {
            if (_cases.Count >= MaximumCases)
            {
                InvestigationCase removed = _cases[0]; _cases.RemoveAt(0); _evicted++;
                // The evicted active run still has a bounded lifetime, but must not
                // be published repeatedly while the same high-upload run continues.
                removed.IsActive = false; removed.EndReason = "记录容量已满，较早记录已移出列表。";
            }
            _cases.Add(item); _revision++;
            Action<InvestigationCase> created = CaseCreated; if (created != null) created(item);
        }

        private void End(Run run, string reason, DateTime gapAt)
        {
            run.Item.IsActive = false; run.Item.EndReason = Limit(reason, 512);
            if (gapAt != default(DateTime)) AddSample(run.Item, gapAt, null, 0, 0);
            if (run.Published) _revision++;
        }

        private static void ObserveTargets(InvestigationCase item, string key, IEnumerable<ConnectionInfo> rows, IEnumerable<TargetTraffic> targets)
        {
            // Report target keys aggregate by program + target name, not endpoint.
            // Preserve this level rather than assigning bytes to every IP:port.
            if (targets != null) foreach (TargetTraffic target in targets)
            {
                if (target == null || !target.Measured || !target.PositiveUpload ||
                    !String.Equals(target.Key, key + "\n" + target.Name, StringComparison.OrdinalIgnoreCase)) continue;
                AddTarget(item, new InvestigationTarget { Host = Limit(target.Name, 512), Address = "", Port = 0, Protocol = "", ProxyChain = "",
                    PositiveUpload = true, EvidenceScope = "upload-target-group" });
            }
            foreach (ConnectionInfo row in rows)
            {
                if (row == null || !String.Equals(TrafficTracker.GetProgramKey(row), key, StringComparison.OrdinalIgnoreCase)) continue;
                if (row.ProcessId > 0 && Array.IndexOf(item.ObservedPids, row.ProcessId) < 0)
                {
                    if (item.ObservedPids.Length >= MaximumPids) item.PidsTruncated = true;
                    else { int[] pids = new int[item.ObservedPids.Length + 1]; Array.Copy(item.ObservedPids, pids, item.ObservedPids.Length); pids[pids.Length - 1] = row.ProcessId; item.ObservedPids = pids; }
                }
                AddTarget(item, new InvestigationTarget { Host = Limit(row.Host, 512), Address = Limit(row.RemoteAddress, 128),
                    Port = row.RemotePort, LocalAddress = Limit(row.LocalAddress, 128), LocalPort = row.LocalPort,
                    Protocol = Protocol(row.Protocol), ProxyChain = Limit(row.ProxyChain, 1024),
                    PositiveUpload = false, EvidenceScope = "recent-endpoint" });
            }
        }

        private static void AddTarget(InvestigationCase item, InvestigationTarget target)
        {
            foreach (InvestigationTarget existing in item.Targets)
                if (existing.EvidenceScope == target.EvidenceScope && String.Equals(existing.Host, target.Host, StringComparison.OrdinalIgnoreCase) &&
                    String.Equals(existing.Address, target.Address, StringComparison.OrdinalIgnoreCase) && existing.Port == target.Port &&
                    existing.LocalAddress == target.LocalAddress && existing.LocalPort == target.LocalPort &&
                    existing.Protocol == target.Protocol && existing.ProxyChain == target.ProxyChain) return;
            if (item.Targets.Count == MaximumTargets) { item.Targets.RemoveAt(0); item.TargetsTruncated = true; }
            item.Targets.Add(target);
        }

        private static void AddSample(InvestigationCase item, DateTime time, double? rate, int measured, int observed)
        {
            if (item.Samples.Count == MaximumSamples) { item.Samples.RemoveAt(0); item.SamplesTruncated = true; }
            item.Samples.Add(new InvestigationSample { TimeUtc = Utc(time), UploadBytesPerSecond = rate, MeasuredConnections = measured, ObservedConnections = observed });
        }

        private void SetState(string state) { if (_state != state) { _state = state; _revision++; } }
        private static string SourceName(NetworkSnapshot snapshot)
        {
            string source = Text(snapshot.SourceName);
            if (snapshot.ClashConnected || source.IndexOf("Clash", StringComparison.OrdinalIgnoreCase) >= 0 || source.IndexOf("Mihomo", StringComparison.OrdinalIgnoreCase) >= 0) return "Clash / Mihomo";
            return source.IndexOf("Windows", StringComparison.OrdinalIgnoreCase) >= 0 ? "Windows 系统连接" : "来源未确认";
        }
        private static bool HasCounters(ConnectionInfo row)
        {
            if (row.HasTrafficCounters == false || row.UploadBytes < 0 || row.DownloadBytes < 0) return false;
            string source = Text(row.DataSource);
            return row.HasTrafficCounters == true || source.IndexOf("Clash", StringComparison.OrdinalIgnoreCase) >= 0 || source.IndexOf("Mihomo", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        private static string Coverage(int measured, int observed) { return "最近有效区间已计速 " + measured + " / " + observed + " 条本程序组可见连接；短连接和绕过 Clash 的活动可能遗漏，全机覆盖率未知。"; }
        private static string Protocol(string value) { string protocol = Text(value).ToUpperInvariant(); return protocol == "TCP" || protocol == "UDP" || protocol == "TCP6" || protocol == "UDP6" ? protocol : "未知"; }
        private static string Text(string value) { return value ?? String.Empty; }
        private static string Limit(string value, int maximum) { value = Text(value); return value.Length <= maximum ? value : value.Substring(0, maximum); }
        private static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
        private static DateTime Utc(DateTime value) { return value == default(DateTime) ? value : value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime(); }
        private static string Iso(DateTime value) { return value == default(DateTime) ? null : Utc(value).ToString("o", CultureInfo.InvariantCulture); }
    }
}
