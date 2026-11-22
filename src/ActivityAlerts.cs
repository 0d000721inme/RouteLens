using System;
using System.Collections.Generic;
using System.Globalization;

namespace RouteLens
{
    // Session-only evidence: disabled by default, no disk writes or independent sampling.
    public sealed class ActivityAlerts
    {
        private const int MaxKnownPrograms = 4096;
        private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(5);
        private readonly HashSet<string> _programs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _countries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _lastAlerts = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private DateTime _lastCooldownCleanup;
        private Dictionary<string, DownloadRun> _downloadRuns = new Dictionary<string, DownloadRun>(StringComparer.OrdinalIgnoreCase);
        private readonly TrafficTracker _traffic = new TrafficTracker();
        private readonly Func<DateTime> _clock;
        private bool _enabled, _newPrograms, _newCountries, _highTraffic, _sustainedUpload, _baseline;
        private string _source;
        private DateTime _lastSnapshot;
        private double _threshold = 10 * 1024 * 1024;
        private double _uploadThreshold = 1024 * 1024, _uploadSeconds = 10, _uploadRun;
        private bool _programDownload;
        private double _downloadThreshold = 10 * 1024 * 1024, _downloadSeconds = 5;

        private struct DownloadRun
        {
            public double Seconds, Bytes;
        }

        public ActivityAlerts() : this(delegate { return DateTime.UtcNow; }) { }
        internal ActivityAlerts(Func<DateTime> clock) { if (clock == null) throw new ArgumentNullException("clock"); _clock = clock; }
        public event Action<string> Alert;

        public bool Enabled
        {
            get { return _enabled; }
            set
            {
                if (_enabled == value) return;
                _enabled = value; ResetBaseline();
                if (!value) { _programs.Clear(); _countries.Clear(); _lastAlerts.Clear(); }
            }
        }
        public bool NewProgramsEnabled { get { return _newPrograms; } set { if (_newPrograms != value) { _newPrograms = value; ResetBaseline(); } } }
        public bool NewCountriesEnabled { get { return _newCountries; } set { if (_newCountries != value) { _newCountries = value; ResetBaseline(); } } }
        public bool HighTrafficEnabled { get { return _highTraffic; } set { if (_highTraffic != value) { _highTraffic = value; ResetBaseline(); } } }
        public bool SustainedUploadEnabled { get { return _sustainedUpload; } set { if (_sustainedUpload != value) { _sustainedUpload = value; ResetBaseline(); } } }
        public bool ProgramDownloadEnabled { get { return _programDownload; } set { if (_programDownload != value) { _programDownload = value; ResetBaseline(); } } }
        public double ProgramDownloadBytesPerSecond
        {
            get { return _downloadThreshold; }
            set
            {
                if (!(value >= 1024 && value <= 10240d * 1024 * 1024)) throw new ArgumentOutOfRangeException("value");
                if (_downloadThreshold != value) { _downloadThreshold = value; _downloadRuns.Clear(); }
            }
        }
        public double ProgramDownloadSeconds
        {
            get { return _downloadSeconds; }
            set
            {
                if (!(value >= 1 && value <= 3600)) throw new ArgumentOutOfRangeException("value");
                if (_downloadSeconds != value) { _downloadSeconds = value; _downloadRuns.Clear(); }
            }
        }
        public double SustainedUploadBytesPerSecond
        {
            get { return _uploadThreshold; }
            set { if (Double.IsNaN(value) || Double.IsInfinity(value) || value < 1024 || value > 10240d * 1024 * 1024) throw new ArgumentOutOfRangeException("value"); if (_uploadThreshold != value) { _uploadThreshold = value; _uploadRun = 0; } }
        }
        public double SustainedUploadSeconds
        {
            get { return _uploadSeconds; }
            set { if (Double.IsNaN(value) || Double.IsInfinity(value) || value < 1 || value > 3600) throw new ArgumentOutOfRangeException("value"); if (_uploadSeconds != value) { _uploadSeconds = value; _uploadRun = 0; } }
        }
        public double HighTrafficBytesPerSecond
        {
            get { return _threshold; }
            set { if (Double.IsNaN(value) || Double.IsInfinity(value) || value < 1024 || value > 10240d * 1024 * 1024) throw new ArgumentOutOfRangeException("value"); _threshold = value; }
        }

        // Keep previously seen evidence across pause/temporary disconnects while
        // forcing the first accepted sample to establish a new baseline silently.
        public void ResetBaseline() { _baseline = false; _source = null; _lastSnapshot = default(DateTime); _uploadRun = 0; _downloadRuns.Clear(); _traffic.Reset(); }

        public void Update(NetworkSnapshot snapshot, Func<string, string> countryResolver)
        {
            if (!_enabled || (!_newPrograms && !_newCountries && !_highTraffic && !_sustainedUpload && !_programDownload)) { ResetBaseline(); return; }
            if (snapshot == null || snapshot.Connections == null || !String.IsNullOrWhiteSpace(snapshot.Error) ||
                (Text(snapshot.SourceName).IndexOf("Clash", StringComparison.OrdinalIgnoreCase) >= 0 && !snapshot.ClashConnected))
            { ResetBaseline(); return; }
            string source = Text(snapshot.SourceName) + "\n" + Text(snapshot.ControllerSummary);
            double interval = (snapshot.CapturedAt - _lastSnapshot).TotalSeconds;
            bool establishing = !_baseline || _lastSnapshot == default(DateTime) || snapshot.CapturedAt == default(DateTime) ||
                interval <= 0 || interval > 5 || !String.Equals(source, _source, StringComparison.Ordinal);
            if (establishing) { _traffic.Reset(); _uploadRun = 0; _downloadRuns.Clear(); }
            _source = source; _lastSnapshot = snapshot.CapturedAt; _baseline = true;
            _traffic.Update(snapshot);

            int newPrograms = 0, newCountries = 0;
            Dictionary<string, string> resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (ConnectionInfo row in snapshot.Connections)
            {
                if (row == null) continue;
                if (_newPrograms)
                {
                    string key = ProgramKey(row);
                    if (key != null && !_programs.Contains(key) && _programs.Count < MaxKnownPrograms)
                    { _programs.Add(key); if (!establishing) newPrograms++; }
                }
                if (_newCountries && countryResolver != null && !String.IsNullOrWhiteSpace(row.RemoteAddress))
                {
                    string code;
                    if (!resolved.TryGetValue(row.RemoteAddress, out code))
                    {
                        try { code = NormalizeCountry(countryResolver(row.RemoteAddress)); }
                        catch (Exception) { code = null; }
                        resolved[row.RemoteAddress] = code;
                    }
                    if (code != null && _countries.Add(code) && !establishing) newCountries++;
                }
            }
            if (establishing) return;
            if (newPrograms > 0) Notify("program", "发现 " + newPrograms + " 个新联网程序组，打开 RouteLens 查看。");
            if (newCountries > 0) Notify("country", "发现 " + newCountries + " 个新目标地区，打开 RouteLens 查看。");
            if (_highTraffic || _sustainedUpload || _programDownload)
            {
                TrafficReport report = _traffic.Build(snapshot.Connections, false);
                if (_programDownload) ObserveDownloads(report, interval);
                if (_highTraffic && report.Available && report.Measuring && report.DownloadPerSecond + report.UploadPerSecond >= _threshold)
                    Notify("traffic", "Clash 已计速连接的总流量速度达到提醒阈值，打开 RouteLens 查看。");
                // Require every currently observed connection to be comparable: a
                // new/reset/missing counter or gap breaks the continuous interval.
                if (_sustainedUpload && report.Available && report.Measuring && report.ConnectionCount > 0 &&
                    report.MeasuredConnectionCount == report.ConnectionCount && report.UploadPerSecond >= _uploadThreshold)
                {
                    _uploadRun += interval;
                    if (_uploadRun >= _uploadSeconds)
                        Notify("upload", String.Format(CultureInfo.InvariantCulture, "观察到 Clash 已计速连接合计上传速度持续达到 {0:0.##} MiB/s，至少 {1:0.##} 秒；这不代表恶意行为。", _uploadThreshold / (1024d * 1024), _uploadSeconds));
                }
                else _uploadRun = 0;
            }
        }

        private void ObserveDownloads(TrafficReport report, double interval)
        {
            if (!report.Available || !report.Fresh || !report.Measuring) { _downloadRuns.Clear(); return; }
            Dictionary<string, DownloadRun> next = new Dictionary<string, DownloadRun>(StringComparer.OrdinalIgnoreCase);
            foreach (ProgramTraffic program in report.Programs)
            {
                if (program.Key == "unknown" || program.MeasuredConnections != program.Connections ||
                    program.DownloadPerSecond < _downloadThreshold) continue;

                DownloadRun run;
                _downloadRuns.TryGetValue(program.Key, out run);
                run.Seconds += interval;
                run.Bytes += program.DownloadPerSecond * interval;
                next.Add(program.Key, run);
                if (run.Seconds < _downloadSeconds) continue;

                string name = program.Name.Replace('/', '\\');
                name = name.Substring(name.LastIndexOf('\\') + 1);
                if (name.Length > 60) name = name.Substring(0, 59) + "…";
                Notify("download:" + program.Key, String.Format(CultureInfo.InvariantCulture,
                    "{0} 高速下载持续 {1:0.##} 秒，平均 {2}/s，共 {3}（Clash 已计速连接）。",
                    name, run.Seconds, FormatDownloadSize(run.Bytes / run.Seconds), FormatDownloadSize(run.Bytes)));
            }
            _downloadRuns = next;
        }

        private static string FormatDownloadSize(double bytes)
        {
            bool mib = bytes >= 1024d * 1024;
            return (bytes / (mib ? 1024d * 1024 : 1024d)).ToString("0.##", CultureInfo.InvariantCulture) + (mib ? " MiB" : " KiB");
        }

        private void Notify(string category, string message)
        {
            DateTime now = _clock(); DateTime last;
            if (now < _lastCooldownCleanup || now - _lastCooldownCleanup >= Cooldown)
            {
                foreach (string key in new List<string>(_lastAlerts.Keys))
                    if (now < _lastAlerts[key] || now - _lastAlerts[key] >= Cooldown) _lastAlerts.Remove(key);
                _lastCooldownCleanup = now;
            }
            if (_lastAlerts.TryGetValue(category, out last) && now >= last && now - last < Cooldown) return;
            _lastAlerts[category] = now;
            Action<string> handler = Alert;
            if (handler != null) try { handler(message); } catch (Exception) { }
        }

        private static string ProgramKey(ConnectionInfo row)
        {
            string path = Text(row.ProcessPath).Trim().Replace('/', '\\');
            if (path.Length > 0) return "path:" + path;
            string name = Text(row.ProcessName).Trim();
            if (name.Length == 0 || name == "未识别程序" || name == "已结束 / 无归属" || name == "未知" || name.StartsWith("PID ", StringComparison.OrdinalIgnoreCase)) return null;
            return "name:" + name;
        }

        private static string NormalizeCountry(string country)
        {
            if (String.IsNullOrWhiteSpace(country)) return null;
            string code = country.Trim().ToUpperInvariant();
            if (code.Length != 2 || code[0] < 'A' || code[0] > 'Z' || code[1] < 'A' || code[1] > 'Z') return null;
            return code;
        }
        private static string Text(string value) { return value ?? String.Empty; }
    }
}
