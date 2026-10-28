using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace RouteLens
{
    public sealed class HistoryEntry
    {
        public string Key, Name, Detail;
        public long DownloadBytes, UploadBytes;
    }

    public sealed class HistoryReport
    {
        public readonly List<HistoryEntry> Entries = new List<HistoryEntry>();
        public long DownloadBytes, UploadBytes;
        public bool Enabled, Available;
        public int RetentionDays;
        public string Message;
    }

    // Records only measured deltas of consecutive Clash snapshots. The database is
    // local and recording is opt-in; Windows' built-in SQLite is used without a DLL
    // download or an additional runtime. This implementation is independent code.
    public sealed class HistoryStore : IDisposable
    {
        private const double MaximumIntervalSeconds = 5;
        private readonly object _gate = new object();
        private readonly string _directory, _settingsPath, _databasePath;
        private FileStream _writerLease;
        private readonly Dictionary<string, Sample> _samples = new Dictionary<string, Sample>(StringComparer.Ordinal);
        private readonly Dictionary<string, Pending> _pending = new Dictionary<string, Pending>(StringComparer.Ordinal);
        private IntPtr _database;
        private bool _enabled, _disposed, _flushQueued;
        private int _dropped;
        private int _retentionDays = 30;
        private string _source, _error;
        private DateTime _capturedAt, _lastPruned;
        private long _receivedAt, _lastFlush;

        private sealed class Sample { public string Identity; public long Download, Upload; public bool Counters; }
        private sealed class Pending
        {
            public long Minute, Download, Upload;
            public string ProgramKey, Program, Path, Host, Node;
        }
        private sealed class Settings { public bool Enabled { get; set; } public int RetentionDays { get; set; } }

        public HistoryStore(string directory)
        {
            if (String.IsNullOrWhiteSpace(directory)) throw new ArgumentException("需要本地数据目录", "directory");
            _directory = directory; _settingsPath = Path.Combine(directory, "history-settings.json"); _databasePath = Path.Combine(directory, "traffic-history.db");
            LoadSettings();
            if (_enabled) OpenDatabase();
        }
        public bool Enabled
        {
            get { lock (_gate) return _enabled; }
            set
            {
                lock (_gate)
                {
                    if (_disposed || _enabled == value) return;
                    if (!value) FlushLocked();
                    _enabled = value; ResetBaselineLocked();
                    if (_enabled) { OpenDatabaseLocked(); AcquireWriterLocked(); }
                    else ReleaseWriterLocked();
                    SaveSettingsLocked();
                }
            }
        }
        public int RetentionDays
        {
            get { lock (_gate) return _retentionDays; }
            set
            {
                if (value != 7 && value != 30 && value != 90) throw new ArgumentOutOfRangeException("value");
                lock (_gate)
                {
                    if (_disposed || _retentionDays == value) return;
                    _retentionDays = value; SaveSettingsLocked();
                    if (_database != IntPtr.Zero && _writerLease != null) { FlushLocked(); PruneLocked(); }
                }
            }
        }
        public string LastError { get { lock (_gate) return _error; } }
        public string DatabasePath { get { return _databasePath; } }
        public void ResetBaseline() { lock (_gate) ResetBaselineLocked(); }
        private void ResetBaselineLocked() { _samples.Clear(); _capturedAt = default(DateTime); _receivedAt = 0; _source = null; }

        public void Update(NetworkSnapshot snapshot)
        {
            // A large report or a busy database must not stall the live UI. The next
            // accepted snapshot can still account for the interval if it remains fresh.
            if (!Monitor.TryEnter(_gate)) return;
            try
            {
                if (_disposed || !_enabled) return;
                if (snapshot == null || !snapshot.ClashConnected || !String.IsNullOrWhiteSpace(snapshot.Error) || snapshot.Connections == null)
                { ResetBaselineLocked(); return; }
                if (_database == IntPtr.Zero && !OpenDatabaseLocked()) return;
                if (!AcquireWriterLocked()) { ResetBaselineLocked(); return; }
                string source = Text(snapshot.SourceName) + "\n" + Text(snapshot.ControllerSummary);
                double seconds = (snapshot.CapturedAt - _capturedAt).TotalSeconds;
                bool interval = _capturedAt != default(DateTime) && snapshot.CapturedAt != default(DateTime) && _receivedAt != 0 &&
                    String.Equals(_source, source, StringComparison.Ordinal) && seconds > 0 && seconds <= MaximumIntervalSeconds &&
                    (Stopwatch.GetTimestamp() - _receivedAt) / (double)Stopwatch.Frequency <= MaximumIntervalSeconds;
                Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (ConnectionInfo row in snapshot.Connections)
                {
                    if (row == null || String.IsNullOrWhiteSpace(row.Id)) continue;
                    int count; counts.TryGetValue(row.Id, out count); counts[row.Id] = count + 1;
                }
                Dictionary<string, Sample> next = new Dictionary<string, Sample>(StringComparer.Ordinal);
                long minute = snapshot.CapturedAt.ToUniversalTime().Ticks / TimeSpan.TicksPerMinute;
                foreach (ConnectionInfo row in snapshot.Connections)
                {
                    if (row == null || String.IsNullOrWhiteSpace(row.Id) || counts[row.Id] != 1) continue;
                    string program, path, programKey = ProgramKey(row, out program, out path);
                    Sample sample = new Sample { Identity = Identity(row, programKey), Download = Math.Max(0, row.DownloadBytes), Upload = Math.Max(0, row.UploadBytes), Counters = HasCounters(row) };
                    Sample previous;
                    if (interval && sample.Counters && _samples.TryGetValue(row.Id, out previous) && previous.Counters &&
                        String.Equals(sample.Identity, previous.Identity, StringComparison.OrdinalIgnoreCase) && sample.Download >= previous.Download && sample.Upload >= previous.Upload)
                    {
                        long down = sample.Download - previous.Download, up = sample.Upload - previous.Upload;
                        if (down != 0 || up != 0)
                        {
                            string host = Text(row.Host).Trim(); IPAddress literal;
                            if (IPAddress.TryParse(host.Trim('[', ']'), out literal)) host = "";
                            AddPending(minute, programKey, program, path, host, Text(row.ProxyChain).Trim(), down, up);
                        }
                    }
                    next.Add(row.Id, sample);
                }
                _samples.Clear(); foreach (KeyValuePair<string, Sample> item in next) _samples.Add(item.Key, item.Value);
                _source = source; _capturedAt = snapshot.CapturedAt; _receivedAt = Stopwatch.GetTimestamp();
                if (_lastFlush == 0) _lastFlush = _receivedAt;
                if ((_receivedAt - _lastFlush) / (double)Stopwatch.Frequency >= 10 || _pending.Count >= 3000 || DateTime.UtcNow - _lastPruned > TimeSpan.FromHours(1)) QueueFlushLocked();
            }
            finally { Monitor.Exit(_gate); }
        }
        private void QueueFlushLocked()
        {
            if (_flushQueued) return;
            _flushQueued = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                lock (_gate)
                {
                    try { if (!_disposed) { FlushLocked(); if (DateTime.UtcNow - _lastPruned > TimeSpan.FromHours(1)) PruneLocked(); } }
                    finally { _flushQueued = false; }
                }
            });
        }
        private void AddPending(long minute, string key, string program, string path, string host, string node, long down, long up)
        {
            string id = minute.ToString() + "\n" + key + "\n" + host.ToLowerInvariant() + "\n" + node.ToLowerInvariant();
            Pending p;
            if (!_pending.TryGetValue(id, out p))
            {
                if (_pending.Count >= 20000) { _dropped++; _error = "历史写入受限，内存缓存已满，部分数据未记录"; return; }
                p = new Pending { Minute = minute, ProgramKey = key, Program = program, Path = path, Host = host, Node = node }; _pending.Add(id, p);
            }
            p.Download = Add(p.Download, down); p.Upload = Add(p.Upload, up);
        }
        public void Flush() { lock (_gate) { if (!_disposed) FlushLocked(); } }
        private void FlushLocked()
        {
            if (_pending.Count == 0 || _database == IntPtr.Zero) return;
            bool temporaryLease = _writerLease == null && !_enabled;
            if (!AcquireWriterLocked()) return;
            IntPtr insert = IntPtr.Zero, update = IntPtr.Zero;
            try
            {
                Execute("BEGIN IMMEDIATE");
                insert = Prepare("INSERT OR IGNORE INTO buckets(minute,program_key,program,path,host,node,download,upload) VALUES(?1,?2,?3,?4,?5,?6,0,0)");
                update = Prepare("UPDATE buckets SET download=CASE WHEN download>9223372036854775807-?1 THEN 9223372036854775807 ELSE download+?1 END,upload=CASE WHEN upload>9223372036854775807-?2 THEN 9223372036854775807 ELSE upload+?2 END WHERE minute=?3 AND program_key=?4 AND host=?5 AND node=?6");
                foreach (Pending p in _pending.Values)
                {
                    BindInt64(insert, 1, p.Minute); BindText(insert, 2, p.ProgramKey); BindText(insert, 3, p.Program); BindText(insert, 4, p.Path); BindText(insert, 5, p.Host); BindText(insert, 6, p.Node);
                    StepDone(insert); ResetStatement(insert);
                    BindInt64(update, 1, p.Download); BindInt64(update, 2, p.Upload); BindInt64(update, 3, p.Minute); BindText(update, 4, p.ProgramKey); BindText(update, 5, p.Host); BindText(update, 6, p.Node);
                    StepDone(update); ResetStatement(update);
                }
                FinalizeStatement(ref insert); FinalizeStatement(ref update); Execute("COMMIT");
                _pending.Clear(); _lastFlush = Stopwatch.GetTimestamp(); _error = null;
            }
            catch (Exception ex)
            {
                FinalizeStatement(ref insert); FinalizeStatement(ref update);
                try { Execute("ROLLBACK"); } catch (Exception) { }
                _error = "历史记录保存失败：" + ex.Message;
            }
            finally { FinalizeStatement(ref insert); FinalizeStatement(ref update); if (temporaryLease) ReleaseWriterLocked(); }
        }

        public HistoryReport GetReport(int days, string dimension)
        {
            if (days != 1 && days != 7 && days != 30 && days != 90) throw new ArgumentOutOfRangeException("days");
            lock (_gate)
            {
                HistoryReport report = new HistoryReport { Enabled = _enabled, RetentionDays = _retentionDays, Available = _database != IntPtr.Zero };
                if (_disposed) { report.Message = "历史服务已关闭"; return report; }
                if (_database == IntPtr.Zero && File.Exists(_databasePath)) OpenDatabaseLocked();
                if (_database == IntPtr.Zero)
                {
                    report.Message = _error ?? (_enabled ? "历史数据库不可用" : "历史记录已关闭；开启后才会记录。所有数据保存在本机。");
                    return report;
                }
                FlushLocked();
                IntPtr statement = IntPtr.Zero;
                try
                {
                    long earliest = DateTime.Today.AddDays(1 - Math.Min(days, _retentionDays)).ToUniversalTime().Ticks / TimeSpan.TicksPerMinute;
                    string keyColumn = dimension == "domain" ? "host" : dimension == "node" || dimension == "chain" ? "node" : "program_key";
                    string nameColumn = dimension == "program" ? "MAX(program)" : keyColumn;
                    string pathColumn = dimension == "program" ? "MAX(path)" : "''";
                    statement = Prepare("SELECT " + keyColumn + "," + nameColumn + "," + pathColumn + ",SUM(CAST(download AS REAL)),SUM(CAST(upload AS REAL)) FROM buckets WHERE minute>=?1 GROUP BY " + keyColumn + " COLLATE NOCASE");
                    BindInt64(statement, 1, earliest);
                    Dictionary<string, HistoryEntry> entries = new Dictionary<string, HistoryEntry>(StringComparer.OrdinalIgnoreCase);
                    int result;
                    while ((result = Native.sqlite3_step(statement)) == 100)
                    {
                        string rawKey = ColumnText(statement, 0), program = ColumnText(statement, 1), path = ColumnText(statement, 2);
                        long down = FromDouble(Native.sqlite3_column_double(statement, 3)), up = FromDouble(Native.sqlite3_column_double(statement, 4));
                        if (dimension == "node" && !String.IsNullOrWhiteSpace(rawKey)) { int arrow = rawKey.LastIndexOf('→'); if (arrow >= 0) rawKey = rawKey.Substring(arrow + 1).Trim(); }
                        string key = dimension == "domain" ? (String.IsNullOrWhiteSpace(rawKey) ? "未提供域名" : rawKey) : dimension == "node" || dimension == "chain" ? (String.IsNullOrWhiteSpace(rawKey) ? "未提供代理链" : rawKey) : rawKey;
                        string name = dimension == "domain" || dimension == "node" || dimension == "chain" ? key : (String.IsNullOrWhiteSpace(program) ? "未识别程序" : program);
                        HistoryEntry entry;
                        if (!entries.TryGetValue(key, out entry)) { entry = new HistoryEntry { Key = key, Name = name, Detail = dimension == "program" ? path : "" }; entries.Add(key, entry); }
                        entry.DownloadBytes = Add(entry.DownloadBytes, down); entry.UploadBytes = Add(entry.UploadBytes, up);
                        report.DownloadBytes = Add(report.DownloadBytes, down); report.UploadBytes = Add(report.UploadBytes, up);
                    }
                    if (result != 101) ThrowSqlite("读取历史", result);
                    report.Entries.AddRange(entries.Values.OrderByDescending(x => (double)x.DownloadBytes + x.UploadBytes).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase));
                    report.Available = true;
                    report.Message = _error ?? (_enabled ? (_writerLease == null ? "其他实例正在记录 · 此窗口只查看历史" : "正在记录 · 仅统计经过 Clash 且两次采样可比较的连接") : "记录已关闭 · 仍可查看此前保存在本机的历史");
                    if (_dropped > 0) report.Message += " · 本次运行有部分流量因写入失败未记录";
                }
                catch (Exception ex) { _error = "读取历史失败：" + ex.Message; report.Message = _error; report.Available = false; }
                finally { FinalizeStatement(ref statement); }
                return report;
            }
        }
        public void Clear()
        {
            lock (_gate)
            {
                if (_disposed) return;
                if (_database == IntPtr.Zero && File.Exists(_databasePath) && !OpenDatabaseLocked()) return;
                if (_database == IntPtr.Zero) return;
                bool temporaryLease = _writerLease == null && !_enabled;
                if (!AcquireWriterLocked()) return;
                _pending.Clear(); ResetBaselineLocked();
                try { Execute("DELETE FROM buckets"); Execute("PRAGMA wal_checkpoint(TRUNCATE)"); Execute("VACUUM"); _error = null; }
                catch (Exception ex) { _error = "清除历史失败：" + ex.Message; }
                finally { if (temporaryLease) ReleaseWriterLocked(); }
            }
        }

        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(_settingsPath)) return;
                Settings settings = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(_settingsPath, Encoding.UTF8));
                if (settings == null) return;
                _enabled = settings.Enabled;
                if (settings.RetentionDays == 7 || settings.RetentionDays == 30 || settings.RetentionDays == 90) _retentionDays = settings.RetentionDays;
            }
            catch (Exception ex) { _enabled = false; _error = "历史设置读取失败：" + ex.Message; }
        }
        private void SaveSettingsLocked()
        {
            string temporary = _settingsPath + ".tmp";
            try
            {
                Directory.CreateDirectory(_directory);
                string json = new JavaScriptSerializer().Serialize(new Settings { Enabled = _enabled, RetentionDays = _retentionDays });
                File.WriteAllText(temporary, json, new UTF8Encoding(false));
                if (File.Exists(_settingsPath)) File.Replace(temporary, _settingsPath, null);
                else File.Move(temporary, _settingsPath);
            }
            catch (Exception ex) { _error = "历史设置保存失败：" + ex.Message; }
        }
        private void OpenDatabase() { lock (_gate) OpenDatabaseLocked(); }
        private bool OpenDatabaseLocked()
        {
            if (_database != IntPtr.Zero) return true;
            try
            {
                Directory.CreateDirectory(_directory);
                int result = Native.sqlite3_open16(_databasePath, out _database);
                if (result != 0) ThrowSqlite("打开历史数据库", result);
                result = Native.sqlite3_busy_timeout(_database, 250);
                if (result != 0) ThrowSqlite("设置数据库等待时间", result);
                Execute("PRAGMA journal_mode=WAL"); Execute("PRAGMA secure_delete=ON");
                Execute("CREATE TABLE IF NOT EXISTS buckets(minute INTEGER NOT NULL,program_key TEXT NOT NULL,program TEXT NOT NULL,path TEXT NOT NULL,host TEXT NOT NULL,node TEXT NOT NULL,download INTEGER NOT NULL,upload INTEGER NOT NULL,PRIMARY KEY(minute,program_key,host,node)) WITHOUT ROWID");
                Execute("CREATE INDEX IF NOT EXISTS buckets_minute ON buckets(minute)");
                _error = null; _lastFlush = Stopwatch.GetTimestamp();
                if (_enabled && AcquireWriterLocked()) PruneLocked();
                return true;
            }
            catch (Exception ex)
            {
                if (_database != IntPtr.Zero) { try { Native.sqlite3_close(_database); } catch (Exception) { } _database = IntPtr.Zero; }
                _error = "历史数据库不可用：" + ex.Message; return false;
            }
        }
        private void PruneLocked()
        {
            if (_database == IntPtr.Zero || _writerLease == null) return;
            IntPtr statement = IntPtr.Zero;
            try
            {
                long earliest = DateTime.Today.AddDays(1 - _retentionDays).ToUniversalTime().Ticks / TimeSpan.TicksPerMinute;
                statement = Prepare("DELETE FROM buckets WHERE minute<?1"); BindInt64(statement, 1, earliest); StepDone(statement);
                _lastPruned = DateTime.UtcNow;
            }
            catch (Exception ex) { _error = "历史过期清理失败：" + ex.Message; }
            finally { FinalizeStatement(ref statement); }
        }
        private bool AcquireWriterLocked()
        {
            if (_writerLease != null) return true;
            try
            {
                Directory.CreateDirectory(_directory);
                _writerLease = new FileStream(Path.Combine(_directory, "traffic-history.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                if (_error == "其他 RouteLens 实例正在记录历史，此实例仅可查看") _error = null;
                return true;
            }
            catch (IOException ex)
            {
                int code = ex.HResult & 0xffff;
                _error = code == 32 || code == 33 ? "其他 RouteLens 实例正在记录历史，此实例仅可查看" : "历史记录锁不可用：" + ex.Message;
                return false;
            }
            catch (Exception ex) { _error = "历史记录锁不可用：" + ex.Message; return false; }
        }
        private void ReleaseWriterLocked()
        { if (_writerLease != null) { _writerLease.Dispose(); _writerLease = null; } }
        private IntPtr Prepare(string sql)
        {
            IntPtr statement; int result = Native.sqlite3_prepare16_v2(_database, sql, -1, out statement, IntPtr.Zero);
            if (result != 0) ThrowSqlite("准备数据库查询", result);
            return statement;
        }
        private void Execute(string sql)
        {
            IntPtr statement = IntPtr.Zero;
            try
            {
                statement = Prepare(sql); int result;
                do { result = Native.sqlite3_step(statement); } while (result == 100);
                if (result != 101) ThrowSqlite("执行数据库操作", result);
            }
            finally { FinalizeStatement(ref statement); }
        }
        private void StepDone(IntPtr statement) { int result = Native.sqlite3_step(statement); if (result != 101) ThrowSqlite("保存历史", result); }
        private void ResetStatement(IntPtr statement)
        {
            int result = Native.sqlite3_reset(statement); if (result != 0) ThrowSqlite("重置数据库操作", result);
            result = Native.sqlite3_clear_bindings(statement); if (result != 0) ThrowSqlite("清除数据库参数", result);
        }
        private void BindText(IntPtr statement, int index, string value)
        {
            int result = Native.sqlite3_bind_text16(statement, index, Text(value), -1, new IntPtr(-1));
            if (result != 0) ThrowSqlite("设置数据库文本参数", result);
        }
        private void BindInt64(IntPtr statement, int index, long value)
        {
            int result = Native.sqlite3_bind_int64(statement, index, value);
            if (result != 0) ThrowSqlite("设置数据库数值参数", result);
        }
        private static string ColumnText(IntPtr statement, int index)
        { IntPtr value = Native.sqlite3_column_text16(statement, index); return value == IntPtr.Zero ? "" : Marshal.PtrToStringUni(value); }
        private static void FinalizeStatement(ref IntPtr statement)
        { if (statement != IntPtr.Zero) { Native.sqlite3_finalize(statement); statement = IntPtr.Zero; } }
        private void ThrowSqlite(string operation, int result)
        {
            string detail = _database == IntPtr.Zero ? "" : Marshal.PtrToStringUni(Native.sqlite3_errmsg16(_database));
            throw new IOException(operation + "失败 (" + result + ")" + (String.IsNullOrWhiteSpace(detail) ? "" : "：" + detail));
        }
        private static long Add(long current, long amount) { return amount > Int64.MaxValue - current ? Int64.MaxValue : current + Math.Max(0, amount); }
        private static long FromDouble(double value) { return Double.IsNaN(value) || value <= 0 ? 0 : value >= Int64.MaxValue ? Int64.MaxValue : (long)Math.Round(value); }
        private static string Text(string value) { return value ?? ""; }
        private static bool HasCounters(ConnectionInfo row)
        {
            if (row.DownloadBytes < 0 || row.UploadBytes < 0 || row.HasTrafficCounters == false) return false;
            return row.HasTrafficCounters == true || Text(row.DataSource).IndexOf("Clash", StringComparison.OrdinalIgnoreCase) >= 0 || Text(row.DataSource).IndexOf("Mihomo", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        private static string ProgramKey(ConnectionInfo row, out string name, out string path)
        {
            path = Text(row.ProcessPath).Trim().Replace('/', '\\'); name = Text(row.ProcessName).Trim();
            bool known = name.Length > 0 && name != "未识别程序" && name != "已结束 / 无归属" && name != "未知" && !name.StartsWith("PID ", StringComparison.OrdinalIgnoreCase);
            if (path.Length > 0) { if (!known) { int slash = path.LastIndexOf('\\'); name = slash >= 0 ? path.Substring(slash + 1) : path; } return "path:" + path; }
            if (known) return "name:" + name;
            name = "未识别程序"; return "unknown";
        }
        private static string Identity(ConnectionInfo row, string programKey)
        {
            return Text(row.Protocol) + "\n" + Text(row.LocalAddress) + ":" + row.LocalPort + "\n" + Text(row.RemoteAddress) + ":" + row.RemotePort + "\n" + programKey + "\n" + row.ProcessId;
        }
        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                FlushLocked(); _disposed = true; ResetBaselineLocked(); ReleaseWriterLocked();
                if (_database != IntPtr.Zero) { Native.sqlite3_close(_database); _database = IntPtr.Zero; }
            }
        }
        private static class Native
        {
            private const string Sqlite = "winsqlite3.dll";
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport(Sqlite, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] internal static extern int sqlite3_open16(string filename, out IntPtr database);
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport(Sqlite, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_close(IntPtr database);
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport(Sqlite, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_busy_timeout(IntPtr database, int milliseconds);
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport(Sqlite, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] internal static extern int sqlite3_prepare16_v2(IntPtr database, string sql, int bytes, out IntPtr statement, IntPtr tail);
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport(Sqlite, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] internal static extern int sqlite3_bind_text16(IntPtr statement, int index, string value, int bytes, IntPtr destructor);
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport(Sqlite, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_int64(IntPtr statement, int index, long value);
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport(Sqlite, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_step(IntPtr statement);
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport(Sqlite, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_reset(IntPtr statement);
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport(Sqlite, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_clear_bindings(IntPtr statement);
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport(Sqlite, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_finalize(IntPtr statement);
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport(Sqlite, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_column_text16(IntPtr statement, int index);
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport(Sqlite, CallingConvention = CallingConvention.Cdecl)] internal static extern long sqlite3_column_int64(IntPtr statement, int index);
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport(Sqlite, CallingConvention = CallingConvention.Cdecl)] internal static extern double sqlite3_column_double(IntPtr statement, int index);
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport(Sqlite, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_errmsg16(IntPtr database);
        }
    }
}
