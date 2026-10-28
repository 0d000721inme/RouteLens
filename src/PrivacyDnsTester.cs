using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace RouteLens
{
    // Probe protocol informed by macvk/dnsleaktest, Copyright (c) 2018 macvk,
    // MIT License. The service is contacted only when the user starts a test.
    public sealed class PrivacyDnsEntry
    {
        public string Address, Country, Region, City, Organization;
    }

    public sealed class PrivacyDnsReport
    {
        public readonly List<PrivacyDnsEntry> Resolvers = new List<PrivacyDnsEntry>();
        public readonly List<PrivacyDnsEntry> Exits = new List<PrivacyDnsEntry>();
        public DateTime CheckedAt;
        public string Error;
        public bool Cancelled;
    }

    internal interface IPrivacyDnsTransport
    {
        Task<string> GetAsync(string url, CancellationToken cancellation);
        Task ProbeAsync(string url, CancellationToken cancellation);
    }

    public static class PrivacyDnsTester
    {
        private const int Probes = 8;

        public static Task<PrivacyDnsReport> RunAsync(CancellationToken cancellation, Action<string> progress)
        { return RunAsync(new PrivacyDnsNetworkTransport(), cancellation, progress); }

        internal static async Task<PrivacyDnsReport> RunAsync(IPrivacyDnsTransport transport, CancellationToken cancellation, Action<string> progress)
        {
            PrivacyDnsReport report = new PrivacyDnsReport { CheckedAt = DateTime.UtcNow };
            try
            {
                cancellation.ThrowIfCancellationRequested();
                Report(progress, "正在连接检测服务…");
                string id = (await transport.GetAsync("https://bash.ws/id", cancellation)).Trim();
                if (!ValidId(id)) { report.Error = "检测服务返回了无效的测试编号。"; return report; }
                Report(progress, "正在发起测试域名查询…");
                Task[] probes = new Task[Probes];
                for (int i = 0; i < probes.Length; i++)
                    probes[i] = transport.ProbeAsync("https://" + (i + 1).ToString(CultureInfo.InvariantCulture) + "." + id + ".bash.ws/", cancellation);
                await Task.WhenAll(probes);
                cancellation.ThrowIfCancellationRequested();
                // Give the remote DNS observation service a moment to record the probes.
                await Task.Delay(1000, cancellation);
                Report(progress, "正在读取检测结果…");
                string json = await transport.GetAsync("https://bash.ws/dnsleak/test/" + id + "?json=2", cancellation);
                ParseResult(json, report);
                if (report.Resolvers.Count == 0 && String.IsNullOrEmpty(report.Error))
                    report.Error = "未观察到解析服务器；检测结果不足，无法判断。";
            }
            catch (OperationCanceledException) { report.Cancelled = true; report.Error = "测试已取消。"; }
            catch (Exception) { report.Error = "检测未完成，请检查网络或稍后重试。"; }
            report.CheckedAt = DateTime.UtcNow;
            return report;
        }

        private static void Report(Action<string> progress, string text) { if (progress != null) progress(text); }
        private static bool ValidId(string value)
        {
            if (String.IsNullOrEmpty(value) || value.Length > 64) return false;
            foreach (char c in value)
                if (!(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') && !(c >= '0' && c <= '9') && c != '-') return false;
            return true;
        }

        internal static void ParseResult(string json, PrivacyDnsReport report)
        {
            if (String.IsNullOrEmpty(json) || json.Length > 65536) { report.Error = "检测服务返回空结果或结果过大。"; return; }
            object[] rows;
            try { rows = new JavaScriptSerializer { MaxJsonLength = 65536 }.DeserializeObject(json) as object[]; }
            catch (Exception) { report.Error = "检测服务返回的数据无法解析。"; return; }
            if (rows == null || rows.Length > 1000) { report.Error = "检测服务返回的数据格式无效。"; return; }
            HashSet<string> resolvers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> exits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (object item in rows)
            {
                Dictionary<string, object> row = item as Dictionary<string, object>;
                if (row == null) continue;
                string type = Field(row, "type");
                if (String.Equals(type, "error", StringComparison.OrdinalIgnoreCase)) { report.Error = "检测服务未能返回结果。"; return; }
                if (type != "dns" && type != "ip") continue;
                IPAddress address;
                if (!IPAddress.TryParse(Field(row, "ip"), out address)) continue;
                string key = address.ToString();
                HashSet<string> seen = type == "dns" ? resolvers : exits;
                if (!seen.Add(key)) continue;
                PrivacyDnsEntry entry = new PrivacyDnsEntry { Address = key, Country = Field(row, "country_name"), Region = Field(row, "region_name"), City = Field(row, "city_name"), Organization = Field(row, "asn") };
                (type == "dns" ? report.Resolvers : report.Exits).Add(entry);
            }
        }

        private static string Field(Dictionary<string, object> row, string key)
        {
            object value;
            if (!row.TryGetValue(key, out value) || value == null || value is System.Collections.IEnumerable && !(value is string)) return "";
            string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            StringBuilder clean = new StringBuilder(Math.Min(120, text.Length));
            foreach (char c in text) { if (clean.Length >= 120) break; if (!Char.IsControl(c)) clean.Append(c); }
            return clean.ToString().Trim();
        }
    }

    internal sealed class PrivacyDnsNetworkTransport : IPrivacyDnsTransport
    {
        public Task<string> GetAsync(string url, CancellationToken cancellation)
        { return Task.Run(delegate { return Request(url, cancellation); }, cancellation); }

        public Task ProbeAsync(string url, CancellationToken cancellation)
        {
            return Task.Run(delegate
            {
                try { Request(url, cancellation); }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { cancellation.ThrowIfCancellationRequested(); /* DNS may have been observed even if HTTPS failed. */ }
            }, cancellation);
        }

        private static string Request(string url, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            Uri uri = new Uri(url);
            if (uri.Scheme != Uri.UriSchemeHttps || !(uri.Host == "bash.ws" || uri.Host.EndsWith(".bash.ws", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Unexpected DNS test endpoint");
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(uri);
            request.Proxy = WebRequest.GetSystemWebProxy();
            request.Timeout = 6000; request.ReadWriteTimeout = 6000; request.AllowAutoRedirect = false;
            request.UserAgent = "RouteLens-DNS-test";
            using (cancellation.Register(delegate { request.Abort(); }))
            {
                try
                {
                    using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    using (Stream input = response.GetResponseStream())
                    using (MemoryStream output = new MemoryStream())
                    {
                        byte[] buffer = new byte[4096]; int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            if (output.Length + read > 65536) throw new InvalidDataException("Response exceeds limit");
                            output.Write(buffer, 0, read);
                        }
                        return Encoding.UTF8.GetString(output.ToArray());
                    }
                }
                catch (WebException) { cancellation.ThrowIfCancellationRequested(); throw; }
            }
        }
    }
}
