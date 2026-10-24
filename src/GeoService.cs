using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace RouteLens
{
    // Only public IP literals are sent to the lookup service; process names stay local.
    public sealed class GeoService : IDisposable
    {
        private readonly object gate = new object();
        private readonly Dictionary<string, GeoResult> cache = new Dictionary<string, GeoResult>();
        private readonly HashSet<string> queued = new HashSet<string>();
        private readonly Queue<string> normal = new Queue<string>();
        private readonly Queue<string> urgent = new Queue<string>();
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private readonly ManualResetEvent stop = new ManualResetEvent(false);
        private readonly Thread worker;
        private readonly Timer egressTimer;
        private readonly string cacheFile;
        private readonly string budgetFile;
        private volatile bool enabled = true;
        private volatile bool disposed;
        private int checking;
        private string budgetDay = "";
        private int usedToday;
        private DateTime blockedUntil = DateTime.MinValue;
        private string statusText = "国家查询就绪 · 结果本机缓存";
        private EgressResult egress = new EgressResult { IsChecking = true };

        public GeoService(string dataDirectory)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            cacheFile = Path.Combine(dataDirectory, "countries.json");
            budgetFile = Path.Combine(dataDirectory, "lookup-budget.json");
            try { Directory.CreateDirectory(dataDirectory); Load(); }
            catch (Exception)
            { statusText = "缓存不可写，本次使用内存缓存"; }
            worker = new Thread(Work) { IsBackground = true, Name = "RouteLens country lookup" };
            worker.Start();
            egressTimer = new Timer(delegate { RefreshEgress(); }, null, 100, 30000);
        }

        public bool Enabled
        {
            get { return enabled; }
            set
            {
                enabled = value;
                Flags.NetworkEnabled = value;
                if (value) { wake.Set(); RefreshEgress(); }
                else
                {
                    lock (gate)
                    {
                        egress.IsChecking = false;
                        foreach (string ip in queued) cache.Remove(ip);
                        queued.Clear(); normal.Clear(); urgent.Clear();
                        statusText = "联网查询已关闭 · 保留已缓存国家";
                    }
                }
            }
        }
        public int PendingCount { get { lock (gate) return queued.Count; } }
        public string StatusText
        {
            get { lock (gate) return !enabled ? "联网查询已关闭 · 保留已缓存国家" : statusText; }
        }
        public EgressResult Egress
        {
            get
            {
                lock (gate)
                {
                    return new EgressResult { Ip = egress.Ip, Location = PeekLocked(egress.Ip),
                        CheckedAt = egress.CheckedAt, Error = egress.Error, IsChecking = egress.IsChecking };
                }
            }
        }

        public GeoResult Peek(string ip)
        {
            lock (gate) return PeekLocked(ip);
        }
        private GeoResult PeekLocked(string ip)
        {
            GeoResult result;
            if (ip != null && cache.TryGetValue(ip, out result)) return result;
            return new GeoResult { Ip = ip, Status = enabled ? "unknown" : "disabled" };
        }
        public GeoResult GetOrQueue(string ip) { return Enqueue(ip, false); }
        private GeoResult Enqueue(string ip, bool priority)
        {
            if (!IpScope.IsPublic(ip))
                return new GeoResult { Ip = ip, Status = "local", CountryName = IpScope.Classify(ip) };
            ip = IPAddress.Parse(ip).ToString();
            lock (gate)
            {
                GeoResult found;
                if (cache.TryGetValue(ip, out found))
                {
                    if (found.Success && DateTime.UtcNow - found.CheckedAt < TimeSpan.FromDays(7)) return found;
                    if (!found.Success && found.Status != "queued" && DateTime.UtcNow - found.CheckedAt < TimeSpan.FromMinutes(5)) return found;
                    if (found.Status == "queued")
                    {
                        if (priority) { urgent.Enqueue(ip); wake.Set(); }
                        return found;
                    }
                }
                if (!enabled || disposed) return found ?? new GeoResult { Ip = ip, Status = "disabled" };
                if (queued.Count >= 1000) return new GeoResult { Ip = ip, Status = "error", Error = "查询队列已满，稍后重试" };
                GeoResult pending = new GeoResult { Ip = ip, Status = "queued" };
                cache[ip] = pending;
                queued.Add(ip);
                if (priority) urgent.Enqueue(ip); else normal.Enqueue(ip);
                wake.Set();
                return pending;
            }
        }

        public void RefreshEgress()
        {
            if (!enabled || disposed || Interlocked.CompareExchange(ref checking, 1, 0) != 0) return;
            lock (gate) egress.IsChecking = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string raw = NetFetch.Text("https://api64.ipify.org?format=json");
                    Dictionary<string, object> data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(raw);
                    string ip = Field(data, "ip");
                    if (!IpScope.IsPublic(ip)) throw new InvalidDataException("探测服务未返回有效公网 IP");
                    ip = IPAddress.Parse(ip).ToString();
                    Enqueue(ip, true);
                    lock (gate) egress = new EgressResult { Ip = ip, CheckedAt = DateTime.UtcNow, IsChecking = false };
                }
                catch (Exception ex)
                {
                    lock (gate)
                    {
                        egress.IsChecking = false;
                        egress.Error = FriendlyError(ex);
                    }
                }
                finally { Interlocked.Exchange(ref checking, 0); }
            });
        }

        private void Work()
        {
            WaitHandle[] waits = { stop, wake };
            while (!disposed)
            {
                string ip = null;
                lock (gate)
                {
                    if (enabled)
                    {
                        while (urgent.Count > 0 || normal.Count > 0)
                        {
                            string candidate = urgent.Count > 0 ? urgent.Dequeue() : normal.Dequeue();
                            if (queued.Contains(candidate)) { ip = candidate; break; }
                        }
                    }
                }
                if (ip == null) { if (WaitHandle.WaitAny(waits) == 0) return; continue; }
                GeoResult result;
                DateTime now = DateTime.UtcNow;
                bool mayQuery;
                lock (gate)
                {
                    string day = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    if (day != budgetDay) { budgetDay = day; usedToday = 0; }
                    mayQuery = now >= blockedUntil && usedToday < 800 && enabled;
                    if (mayQuery) usedToday++;
                }
                if (!mayQuery)
                {
                    result = new GeoResult { Ip = ip, Status = "error", CheckedAt = now,
                        Error = "查询暂缓：服务限额或每日保护额度，稍后自动重试" };
                    lock (gate) statusText = "国家查询暂缓 · 已缓存结果仍可用";
                }
                else
                {
                    SaveBudget();
                    try
                    {
                        result = Lookup(ip);
                        lock (gate) statusText = "国家查询在线 · 未知 IP 排队查询并缓存";
                    }
                    catch (Exception ex)
                    {
                        result = new GeoResult { Ip = ip, Status = "error", CheckedAt = now, Error = FriendlyError(ex) };
                        WebException web = ex as WebException;
                        HttpWebResponse response = web == null ? null : web.Response as HttpWebResponse;
                        if (response != null)
                        {
                            if ((int)response.StatusCode == 429)
                            {
                                int seconds;
                                if (!Int32.TryParse(response.Headers["Retry-After"], out seconds)) seconds = 3600;
                                lock (gate) blockedUntil = now.AddSeconds(Math.Max(60, Math.Min(86400, seconds)));
                            }
                            response.Close();
                        }
                        SaveBudget();
                        lock (gate) statusText = "国家查询暂不可用 · " + result.Error;
                    }
                }
                lock (gate) { cache[ip] = result; queued.Remove(ip); }
                if (result.Success) SaveCache();
                if (stop.WaitOne(2000)) return;
            }
        }

        internal static GeoResult Lookup(string ip)
        {
            if (!IpScope.IsPublic(ip)) throw new ArgumentException("只允许查询公网 IP");
            string url = "https://ipwho.is/" + Uri.EscapeDataString(IPAddress.Parse(ip).ToString()) +
                "?fields=ip,success,message,country,country_code,region,city,connection&lang=zh-CN";
            Dictionary<string, object> data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(NetFetch.Text(url));
            if (!data.ContainsKey("success") || !(data["success"] is bool) || !(bool)data["success"])
                throw new InvalidDataException("国家服务未能识别该 IP：" + Field(data, "message"));
            string code = Field(data, "country_code").ToUpperInvariant();
            if (!Flags.ValidCode(code)) throw new InvalidDataException("国家服务没有返回国家代码");
            object nested;
            Dictionary<string, object> connection = data.TryGetValue("connection", out nested) ? nested as Dictionary<string, object> : null;
            return new GeoResult { Ip = IPAddress.Parse(ip).ToString(), CountryCode = code,
                CountryName = CountryNames.Chinese(code, Field(data, "country")), Region = Field(data, "region"),
                City = Field(data, "city"), Organization = connection == null ? "" : Field(connection, "isp"),
                Status = "ok", CheckedAt = DateTime.UtcNow };
        }

        private static string Field(Dictionary<string, object> data, string key)
        { object value; return data != null && data.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : ""; }

        internal static string FriendlyError(Exception ex)
        {
            WebException web = ex as WebException;
            if (web != null)
            {
                HttpWebResponse response = web.Response as HttpWebResponse;
                if (response != null && (int)response.StatusCode == 429) return "服务查询限额已用完，等待恢复";
                if (web.Status == WebExceptionStatus.Timeout) return "请求超时，稍后可重试";
                return "网络请求失败，请检查 VPN 或系统代理";
            }
            if (ex is InvalidDataException) return ex.Message;
            return "查询失败，稍后可重试";
        }

        private void Load()
        {
            JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 4000000 };
            try
            {
                if (File.Exists(cacheFile))
                {
                    List<GeoResult> saved = json.Deserialize<List<GeoResult>>(File.ReadAllText(cacheFile, Encoding.UTF8));
                    if (saved != null) foreach (GeoResult item in saved)
                        if (item != null && item.Success && IpScope.IsPublic(item.Ip) && Flags.ValidCode(item.CountryCode) &&
                            DateTime.UtcNow - item.CheckedAt < TimeSpan.FromDays(7) && item.CheckedAt <= DateTime.UtcNow.AddMinutes(1))
                            cache[IPAddress.Parse(item.Ip).ToString()] = item;
                }
            }
            catch (Exception) { }
            try
            {
                if (File.Exists(budgetFile))
                {
                    Dictionary<string, object> data = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(budgetFile));
                    budgetDay = Field(data, "day");
                    int parsed;
                    if (Int32.TryParse(Field(data, "used"), out parsed)) usedToday = Math.Max(0, parsed);
                    DateTime block;
                    if (DateTime.TryParse(Field(data, "blockedUntil"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out block)) blockedUntil = block.ToUniversalTime();
                }
            }
            catch (Exception) { }
        }
        private void SaveBudget()
        {
            try
            {
                string data;
                lock (gate) data = new JavaScriptSerializer().Serialize(new { day = budgetDay, used = usedToday,
                    blockedUntil = blockedUntil.ToString("o", CultureInfo.InvariantCulture) });
                AtomicSave(budgetFile, data);
            }
            catch (Exception) { }
        }
        private void SaveCache()
        {
            try
            {
                List<GeoResult> saved = new List<GeoResult>();
                lock (gate) foreach (GeoResult result in cache.Values)
                    if (result.Success && DateTime.UtcNow - result.CheckedAt < TimeSpan.FromDays(7)) saved.Add(result);
                AtomicSave(cacheFile, new JavaScriptSerializer { MaxJsonLength = 4000000 }.Serialize(saved));
            }
            catch (Exception) { }
        }
        private static void AtomicSave(string path, string data)
        {
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, data, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            egressTimer.Dispose();
            stop.Set(); wake.Set();
            // Requests are bounded by a timeout; background threads never hold the app open.
        }
    }

    internal static class NetFetch
    {
        public static byte[] Bytes(string url)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Proxy = WebRequest.GetSystemWebProxy();
            request.Timeout = 8000;
            request.ReadWriteTimeout = 8000;
            request.UserAgent = "RouteLens/1.0 (Windows personal network monitor)";
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            using (WebResponse response = request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            using (MemoryStream memory = new MemoryStream())
            {
                byte[] buffer = new byte[4096];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    memory.Write(buffer, 0, read);
                    if (memory.Length > 1024 * 1024) throw new InvalidDataException("服务响应过大");
                }
                return memory.ToArray();
            }
        }
        public static string Text(string url) { return Encoding.UTF8.GetString(Bytes(url)); }
    }

    internal static class CountryNames
    {
        private static readonly Dictionary<string, string> Names = new Dictionary<string, string>
        {
            {"CN","中国"},{"JP","日本"},{"US","美国"},{"GB","英国"},{"HK","中国香港"},{"MO","中国澳门"},{"TW","中国台湾"},
            {"SG","新加坡"},{"KR","韩国"},{"DE","德国"},{"FR","法国"},{"CA","加拿大"},{"AU","澳大利亚"},{"NZ","新西兰"},
            {"NL","荷兰"},{"RU","俄罗斯"},{"IN","印度"},{"TH","泰国"},{"VN","越南"},{"MY","马来西亚"},{"PH","菲律宾"},
            {"ID","印度尼西亚"},{"IT","意大利"},{"ES","西班牙"},{"BR","巴西"},{"TR","土耳其"},{"CH","瑞士"},{"SE","瑞典"},
            {"FI","芬兰"},{"NO","挪威"},{"DK","丹麦"},{"IE","爱尔兰"},{"PL","波兰"},{"AE","阿联酋"},{"ZA","南非"},
            {"MX","墨西哥"},{"AR","阿根廷"},{"IL","以色列"},{"UA","乌克兰"},{"PT","葡萄牙"},{"AT","奥地利"}
        };
        public static string Chinese(string code, string fallback)
        { string name; return Names.TryGetValue(code, out name) ? name : fallback; }
    }
}
