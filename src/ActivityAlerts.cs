using System;
using System.Collections.Generic;

namespace RouteLens
{
    // Session-only evidence: disabled by default, no disk writes or independent sampling.
    public sealed class ActivityAlerts
    {
        private const int MaxKnownPrograms = 4096;
        private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(5);
        private readonly HashSet<string> _programs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _countries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _lastAlerts = new Dictionary<string, DateTime>();
        private readonly TrafficTracker _traffic = new TrafficTracker();
        private readonly Func<DateTime> _clock;
        private bool _enabled, _newPrograms, _newCountries, _highTraffic, _baseline;
        private string _source;
        private DateTime _lastSnapshot;
        private double _threshold = 10 * 1024 * 1024;

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
        public double HighTrafficBytesPerSecond
        {
            get { return _threshold; }
            set { if (Double.IsNaN(value) || Double.IsInfinity(value) || value < 1024 || value > 10240d * 1024 * 1024) throw new ArgumentOutOfRangeException("value"); _threshold = value; }
        }

        // Keep previously seen evidence across pause/temporary disconnects while
        // forcing the first accepted sample to establish a new baseline silently.
        public void ResetBaseline() { _baseline = false; _source = null; _lastSnapshot = default(DateTime); _traffic.Reset(); }

        public void Update(NetworkSnapshot snapshot, Func<string, string> countryResolver)
        {
            if (!_enabled || (!_newPrograms && !_newCountries && !_highTraffic)) { ResetBaseline(); return; }
            if (snapshot == null || snapshot.Connections == null || !String.IsNullOrWhiteSpace(snapshot.Error) ||
                (Text(snapshot.SourceName).IndexOf("Clash", StringComparison.OrdinalIgnoreCase) >= 0 && !snapshot.ClashConnected))
            { ResetBaseline(); return; }
            string source = Text(snapshot.SourceName) + "\n" + Text(snapshot.ControllerSummary);
            double interval = (snapshot.CapturedAt - _lastSnapshot).TotalSeconds;
            bool establishing = !_baseline || _lastSnapshot == default(DateTime) || snapshot.CapturedAt == default(DateTime) ||
                interval <= 0 || interval > 5 || !String.Equals(source, _source, StringComparison.Ordinal);
            if (establishing) _traffic.Reset();
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
            if (_highTraffic)
            {
                TrafficReport report = _traffic.Build(snapshot.Connections, false);
                if (report.Available && report.Measuring && report.DownloadPerSecond + report.UploadPerSecond >= _threshold)
                    Notify("traffic", "Clash 已计速连接的总流量速度达到提醒阈值，打开 RouteLens 查看。");
            }
        }

        private void Notify(string category, string message)
        {
            DateTime now = _clock(); DateTime last;
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
