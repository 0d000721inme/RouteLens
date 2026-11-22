using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace RouteLens
{
    public sealed class ProgramTraffic
    {
        public string Key, Name, Path;
        public int Connections, CounterConnections, MeasuredConnections;
        public double DownloadPerSecond, UploadPerSecond;
        public long DownloadBytes, UploadBytes;
    }

    public sealed class TrafficReport
    {
        public List<ProgramTraffic> Programs = new List<ProgramTraffic>();
        public List<TargetTraffic> Targets = new List<TargetTraffic>();
        public bool Available, Measuring;
        public bool Fresh;
        public DateTime SnapshotAt;
        public int ConnectionCount, CounterConnectionCount, MeasuredConnectionCount;
        public double DownloadPerSecond, UploadPerSecond;
        public long DownloadBytes, UploadBytes;
        public string Status;
    }

    public sealed class TargetTraffic
    {
        public string Key, Name, Program;
        public bool Measured, PositiveUpload;
    }

    public struct DailySiteTrafficEntry
    {
        public string Host;
        public long DownloadBytes, UploadBytes, DirectBytes, ProxyBytes, UnknownBytes;
        public long TotalBytes { get { return TrafficTracker.AddBytes(DownloadBytes, UploadBytes); } }

        internal void Add(long download, long upload, string route)
        {
            DownloadBytes = TrafficTracker.AddBytes(DownloadBytes, download);
            UploadBytes = TrafficTracker.AddBytes(UploadBytes, upload);
            long bytes = TrafficTracker.AddBytes(download, upload);
            if (route == "直连") DirectBytes = TrafficTracker.AddBytes(DirectBytes, bytes);
            else if (route == "代理") ProxyBytes = TrafficTracker.AddBytes(ProxyBytes, bytes);
            else UnknownBytes = TrafficTracker.AddBytes(UnknownBytes, bytes);
        }
    }

    public sealed class DailySiteTrafficReport
    {
        public DateTime Day;
        public long DownloadBytes, UploadBytes, DirectBytes, ProxyBytes, UnknownBytes, OverflowBytes;
        public long TotalBytes { get { return TrafficTracker.AddBytes(DownloadBytes, UploadBytes); } }
        public List<DailySiteTrafficEntry> Sites = new List<DailySiteTrafficEntry>();
    }

    // Current local day, in memory. Overflow is included in all overall totals.
    public sealed class DailySiteTraffic
    {
        internal const int MaximumSites = 2048;
        private readonly Dictionary<string, DailySiteTrafficEntry> _sites = new Dictionary<string, DailySiteTrafficEntry>(StringComparer.OrdinalIgnoreCase);
        private DailySiteTrafficEntry _total;
        private long _overflowBytes;
        private DateTime _day, _lastCaptureUtc;
        private bool _hasBaseline;

        public void Clear()
        {
            _sites.Clear(); _total = new DailySiteTrafficEntry(); _overflowBytes = 0; _hasBaseline = false;
        }

        private void AdvanceDay(DateTime day)
        {
            if (day > _day) { Clear(); _day = day; }
        }

        private static DateTime LocalDay(DateTime time)
        { return (time.Kind == DateTimeKind.Utc ? time.ToLocalTime() : time).Date; }

        internal bool BeginSnapshot(DateTime previous, DateTime current, out bool countInterval)
        {
            countInterval = false;
            if (current == default(DateTime)) { _hasBaseline = false; return true; }
            DateTime day = LocalDay(current), utc = current.ToUniversalTime();
            // Keep the newest accepted baseline when callers replay an older capture.
            if (day < _day || utc <= _lastCaptureUtc) return false;
            AdvanceDay(day);
            countInterval = _hasBaseline && previous != default(DateTime) && previous.ToUniversalTime() == _lastCaptureUtc;
            _hasBaseline = true; _lastCaptureUtc = utc;
            return true;
        }

        internal void Add(ConnectionInfo row, long download, long upload, string route)
        {
            if (download == 0 && upload == 0) return;
            _total.Add(download, upload, route);
            string host = (row.Host ?? "").Trim().TrimEnd('.');
            if (host.Length == 0) host = (row.RemoteAddress ?? "").Trim();
            if (host.Length == 0) host = "目标未提供";
            DailySiteTrafficEntry entry;
            if (!_sites.TryGetValue(host, out entry))
            {
                if (_sites.Count >= MaximumSites)
                {
                    _overflowBytes = TrafficTracker.AddBytes(_overflowBytes, TrafficTracker.AddBytes(download, upload));
                    return;
                }
                entry.Host = host;
            }
            entry.Add(download, upload, route); _sites[host] = entry;
        }

        public DailySiteTrafficReport Read(DateTime now, bool includeSites = true)
        {
            DateTime day = LocalDay(now); AdvanceDay(day);
            if (day != _day) return new DailySiteTrafficReport { Day = day };
            DailySiteTrafficReport report = new DailySiteTrafficReport { Day = day, DownloadBytes = _total.DownloadBytes,
                UploadBytes = _total.UploadBytes, DirectBytes = _total.DirectBytes, ProxyBytes = _total.ProxyBytes,
                UnknownBytes = _total.UnknownBytes, OverflowBytes = _overflowBytes };
            if (includeSites)
            {
                report.Sites.AddRange(_sites.Values);
                report.Sites.Sort(delegate(DailySiteTrafficEntry a, DailySiteTrafficEntry b)
                {
                    int order = b.TotalBytes.CompareTo(a.TotalBytes);
                    return order != 0 ? order : StringComparer.OrdinalIgnoreCase.Compare(a.Host, b.Host);
                });
            }
            return report;
        }
    }

    // Retains only the last accepted snapshot. Closing connections leave these totals;
    // this is neither a history store nor an estimate of whole-machine usage.
    public sealed class TrafficTracker
    {
        private const double MaximumIntervalSeconds = 5;
        private Dictionary<string, Sample> _samples = new Dictionary<string, Sample>(StringComparer.Ordinal);
        private DateTime _capturedAt;
        private long _receivedAt;
        private string _source;
        private bool _available;

        private sealed class Sample
        {
            public string Identity, Route;
            public long Download, Upload;
            public bool Counters, Measured;
            public double DownloadRate, UploadRate;
        }

        public void Reset()
        {
            _samples.Clear();
            _capturedAt = default(DateTime);
            _receivedAt = 0;
            _source = null;
            _available = false;
        }

        public void Update(NetworkSnapshot snapshot)
        { Update(snapshot, null); }

        public void Update(NetworkSnapshot snapshot, DailySiteTraffic daily)
        {
            if (snapshot == null || !snapshot.ClashConnected || !String.IsNullOrWhiteSpace(snapshot.Error) || snapshot.Connections == null)
            {
                Reset();
                return;
            }
            bool countDaily = false;
            if (daily != null && !daily.BeginSnapshot(_capturedAt, snapshot.CapturedAt, out countDaily) &&
                _capturedAt != default(DateTime) && snapshot.CapturedAt <= _capturedAt) return;
            string source = Text(snapshot.SourceName) + "\n" + Text(snapshot.ControllerSummary);
            double seconds = (snapshot.CapturedAt - _capturedAt).TotalSeconds;
            bool intervalValid = _available && String.Equals(_source, source, StringComparison.Ordinal) &&
                _capturedAt != default(DateTime) && snapshot.CapturedAt != default(DateTime) &&
                seconds > 0 && seconds <= MaximumIntervalSeconds && _receivedAt != 0 &&
                (Stopwatch.GetTimestamp() - _receivedAt) / (double)Stopwatch.Frequency <= MaximumIntervalSeconds;
            Dictionary<string, Sample> next = new Dictionary<string, Sample>(StringComparer.Ordinal);
            HashSet<string> duplicates = new HashSet<string>(StringComparer.Ordinal);
            foreach (ConnectionInfo row in snapshot.Connections)
            {
                if (row == null || String.IsNullOrWhiteSpace(row.Id)) continue;
                if (next.ContainsKey(row.Id))
                {
                    duplicates.Add(row.Id);
                    next[row.Id].Measured = false;
                    continue;
                }
                Sample sample = new Sample
                {
                    Identity = Identity(row), Route = row.RouteType, Counters = HasCounters(row),
                    Download = Math.Max(0, row.DownloadBytes), Upload = Math.Max(0, row.UploadBytes)
                };
                Sample before;
                if (intervalValid && sample.Counters && _samples.TryGetValue(row.Id, out before) && before.Counters &&
                    String.Equals(before.Identity, sample.Identity, StringComparison.OrdinalIgnoreCase) &&
                    sample.Download >= before.Download && sample.Upload >= before.Upload)
                {
                    sample.Measured = true;
                    sample.DownloadRate = (sample.Download - before.Download) / seconds;
                    sample.UploadRate = (sample.Upload - before.Upload) / seconds;
                }
                next.Add(row.Id, sample);
            }
            // An ambiguous identifier cannot supply a trustworthy future baseline.
            foreach (string id in duplicates) next.Remove(id);
            if (countDaily && intervalValid)
            {
                foreach (ConnectionInfo row in snapshot.Connections)
                {
                    Sample current;
                    if (row == null || row.Id == null || !next.TryGetValue(row.Id, out current) || !current.Measured) continue;
                    Sample previous = _samples[row.Id];
                    string route = previous.Route == current.Route ? current.Route : null;
                    daily.Add(row, current.Download - previous.Download, current.Upload - previous.Upload, route);
                }
            }
            _samples = next;
            _capturedAt = snapshot.CapturedAt;
            _receivedAt = Stopwatch.GetTimestamp();
            _source = source;
            _available = true;
        }

        public TrafficReport Build(IEnumerable<ConnectionInfo> connections, bool paused)
        {
            TrafficReport report = new TrafficReport { Available = _available, SnapshotAt = _capturedAt };
            bool fresh = _receivedAt != 0 && (Stopwatch.GetTimestamp() - _receivedAt) / (double)Stopwatch.Frequency <= MaximumIntervalSeconds;
            bool showRates = _available && !paused && fresh;
            report.Fresh = showRates;
            Dictionary<string, ProgramTraffic> programs = new Dictionary<string, ProgramTraffic>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, TargetTraffic> targets = new Dictionary<string, TargetTraffic>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            if (connections != null) foreach (ConnectionInfo row in connections)
            {
                if (row == null) continue;
                // Caller filters can enumerate the same connection more than once.
                if (!String.IsNullOrWhiteSpace(row.Id) && !seen.Add(row.Id)) continue;
                string path, name;
                string key = ProgramKey(row, out name, out path);
                ProgramTraffic program;
                if (!programs.TryGetValue(key, out program))
                {
                    program = new ProgramTraffic { Key = key, Name = name, Path = path };
                    programs.Add(key, program);
                }
                program.Connections++;
                report.ConnectionCount++;
                string targetName = Text(row.Host).Trim();
                if (targetName.Length == 0) targetName = Text(row.RemoteAddress).Trim();
                if (targetName.Length == 0) targetName = "目标未提供";
                TargetTraffic target;
                string targetKey = key + "\n" + targetName;
                if (!targets.TryGetValue(targetKey, out target)) { target = new TargetTraffic { Key = targetKey, Name = targetName, Program = name }; targets.Add(targetKey, target); }
                if (!_available || !HasCounters(row)) continue;
                program.CounterConnections++;
                report.CounterConnectionCount++;
                program.DownloadBytes = AddBytes(program.DownloadBytes, row.DownloadBytes);
                program.UploadBytes = AddBytes(program.UploadBytes, row.UploadBytes);
                Sample sample;
                if (showRates && !String.IsNullOrWhiteSpace(row.Id) && _samples.TryGetValue(row.Id, out sample) && sample.Measured &&
                    String.Equals(sample.Identity, Identity(row), StringComparison.OrdinalIgnoreCase) &&
                    sample.Download == row.DownloadBytes && sample.Upload == row.UploadBytes)
                {
                    program.MeasuredConnections++;
                    program.DownloadPerSecond += sample.DownloadRate;
                    program.UploadPerSecond += sample.UploadRate;
                    report.MeasuredConnectionCount++;
                    target.Measured = true;
                    if (sample.UploadRate > 0) target.PositiveUpload = true;
                }
            }
            report.Targets.AddRange(targets.Values);
            foreach (ProgramTraffic program in programs.Values)
            {
                report.Programs.Add(program);
                report.DownloadBytes = AddBytes(report.DownloadBytes, program.DownloadBytes);
                report.UploadBytes = AddBytes(report.UploadBytes, program.UploadBytes);
                report.DownloadPerSecond += program.DownloadPerSecond;
                report.UploadPerSecond += program.UploadPerSecond;
            }
            report.Programs.Sort(delegate(ProgramTraffic a, ProgramTraffic b)
            {
                int order = (b.DownloadPerSecond + b.UploadPerSecond).CompareTo(a.DownloadPerSecond + a.UploadPerSecond);
                return order == 0 ? StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name) : order;
            });
            report.Measuring = report.MeasuredConnectionCount > 0;
            if (!_available) report.Status = "当前来源未提供流量计数，请连接 Clash / Mihomo。";
            else if (paused) report.Status = "采样已暂停；累计为当前存活连接的数据。";
            else if (!fresh) report.Status = "流量数据已过期，等待新的采样。";
            else if (report.ConnectionCount == 0) report.Status = "当前筛选下没有连接。";
            else if (report.CounterConnectionCount == 0) report.Status = "当前连接未提供流量计数。";
            else if (!report.Measuring) report.Status = "等待下一次有效采样；累计仅统计当前存活连接。";
            else report.Status = "当前筛选中观察到的 Clash 活动连接：已计速 " + report.MeasuredConnectionCount + " / " + report.ConnectionCount + "；累计仅统计存活连接。";
            if (_available && report.CounterConnectionCount < report.ConnectionCount)
                report.Status += " " + (report.ConnectionCount - report.CounterConnectionCount) + " 条连接未提供计数。";
            return report;
        }

        private static bool HasCounters(ConnectionInfo row)
        {
            if (row.DownloadBytes < 0 || row.UploadBytes < 0 || row.HasTrafficCounters == false) return false;
            return row.HasTrafficCounters == true ||
                Text(row.DataSource).IndexOf("Clash", StringComparison.OrdinalIgnoreCase) >= 0 ||
                Text(row.DataSource).IndexOf("Mihomo", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static long AddBytes(long total, long amount)
        {
            return amount > Int64.MaxValue - total ? Int64.MaxValue : total + Math.Max(0, amount);
        }

        private static string Identity(ConnectionInfo row)
        {
            string name, path;
            return Text(row.Protocol) + "\n" + Text(row.LocalAddress) + ":" + row.LocalPort + "\n" +
                Text(row.RemoteAddress) + ":" + row.RemotePort + "\n" + Text(row.Host) + "\n" + ProgramKey(row, out name, out path) + "\n" + row.ProcessId;
        }

        public static string GetProgramKey(ConnectionInfo row)
        {
            if (row == null) throw new ArgumentNullException("row");
            string name, path;
            return ProgramKey(row, out name, out path);
        }

        private static string ProgramKey(ConnectionInfo row, out string name, out string path)
        {
            path = Text(row.ProcessPath).Trim().Replace('/', '\\');
            name = Text(row.ProcessName).Trim();
            bool knownName = name.Length > 0 && name != "未识别程序" && name != "已结束 / 无归属" && name != "未知" &&
                !name.StartsWith("PID ", StringComparison.OrdinalIgnoreCase);
            if (path.Length > 0)
            {
                if (!knownName)
                {
                    int slash = path.LastIndexOf('\\');
                    name = slash >= 0 ? path.Substring(slash + 1) : path;
                }
                return "path:" + path;
            }
            if (knownName) return "name:" + name;
            name = "未识别程序";
            return "unknown";
        }

        private static string Text(string value) { return value ?? String.Empty; }
    }
}
