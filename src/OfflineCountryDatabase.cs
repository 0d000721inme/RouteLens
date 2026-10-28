using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RouteLens
{
    public sealed class OfflineCountryStatus
    {
        public bool Ready, Busy;
        public int RangeCount;
        public DateTime InstalledAt;
        public string Message;
    }

    // DB-IP Country Lite CSV: ip_start, ip_end, country. The snapshot is immutable
    // after validation, so lookups do not block on database imports or downloads.
    public sealed class OfflineCountryDatabase : IDisposable
    {
        private const long MaximumFileBytes = 128L * 1024 * 1024;
        private const long MaximumTextCharacters = 512L * 1024 * 1024;
        private const int MaximumRanges = 2000000;
        private const string BundledResource = "Geo.dbip-country-lite.csv.gz";
        private readonly string directory, databasePath;
        private readonly object gate = new object();
        private volatile Data current;
        private volatile bool disposed;
        private int busy;
        private string message = "未安装离线国家数据库";
        private DateTime installedAt;

        private struct AddressKey : IComparable<AddressKey>
        {
            public ulong High, Low;
            public int CompareTo(AddressKey other)
            {
                int comparison = High.CompareTo(other.High);
                return comparison != 0 ? comparison : Low.CompareTo(other.Low);
            }
        }
        private struct Range
        {
            public AddressKey Start, End;
            public ushort Country;
        }
        private sealed class Data
        {
            public Range[] V4, V6;
            public int Count { get { return V4.Length + V6.Length; } }
        }

        public OfflineCountryDatabase(string dataDirectory)
        {
            if (String.IsNullOrWhiteSpace(dataDirectory)) throw new ArgumentNullException("dataDirectory");
            directory = dataDirectory;
            databasePath = Path.Combine(directory, "offline-country.dbip");
            bool installed = File.Exists(databasePath);
            bool bundled = Assembly.GetExecutingAssembly().GetManifestResourceInfo(BundledResource) != null;
            if (installed || bundled)
            {
                busy = 1;
                message = "正在加载本地国家数据库";
                Task.Run(delegate { LoadInitial(installed, bundled); });
            }
        }

        public OfflineCountryStatus GetStatus()
        {
            lock (gate)
            {
                Data snapshot = current;
                return new OfflineCountryStatus { Ready = snapshot != null, Busy = Volatile.Read(ref busy) != 0,
                    RangeCount = snapshot == null ? 0 : snapshot.Count, InstalledAt = installedAt, Message = message };
            }
        }

        public GeoResult Find(string ip)
        {
            Data snapshot = current;
            IPAddress address;
            if (disposed || snapshot == null || !IPAddress.TryParse(ip, out address)) return null;
            bool v4;
            AddressKey key = Key(address, out v4);
            Range[] ranges = v4 ? snapshot.V4 : snapshot.V6;
            int low = 0, high = ranges.Length - 1;
            while (low <= high)
            {
                int middle = low + (high - low) / 2;
                Range value = ranges[middle];
                if (key.CompareTo(value.Start) < 0) high = middle - 1;
                else if (key.CompareTo(value.End) > 0) low = middle + 1;
                else
                {
                    string code = new String(new char[] { (char)(value.Country >> 8), (char)(value.Country & 255) });
                    if (code == "ZZ") return null;
                    return new GeoResult { Ip = address.ToString(), CountryCode = code, CountryName = CountryNames.Chinese(code, code),
                        Status = "ok", CheckedAt = DateTime.UtcNow };
                }
            }
            return null;
        }

        public Task ImportAsync(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentNullException("path");
            Begin("正在导入并验证本地国家数据库");
            return Task.Run(delegate
            {
                string temporary = null;
                try
                {
                    Directory.CreateDirectory(directory);
                    temporary = TemporaryPath();
                    CopyBounded(path, temporary);
                    Activate(temporary);
                    temporary = null;
                }
                catch (Exception ex) { Fail(ex); throw; }
                finally { DeleteTemporary(temporary); Interlocked.Exchange(ref busy, 0); }
            });
        }

        // Only called in response to the user selecting Download in the dialog.
        public Task DownloadAsync()
        {
            Begin("正在下载 DB-IP Lite 国家数据库");
            return Task.Run(delegate
            {
                string temporary = null;
                try
                {
                    Directory.CreateDirectory(directory);
                    temporary = TemporaryPath();
                    DownloadTo(temporary);
                    lock (gate) message = "下载完成，正在验证国家数据库";
                    Activate(temporary);
                    temporary = null;
                }
                catch (Exception ex) { Fail(ex); throw; }
                finally { DeleteTemporary(temporary); Interlocked.Exchange(ref busy, 0); }
            });
        }

        private void Begin(string status)
        {
            if (disposed) throw new ObjectDisposedException("OfflineCountryDatabase");
            if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) throw new InvalidOperationException("国家数据库正在处理，请稍候");
            lock (gate) message = status;
        }
        private string TemporaryPath() { return Path.Combine(directory, ".offline-country-" + Guid.NewGuid().ToString("N") + ".tmp"); }
        private void LoadInitial(bool installed, bool bundled)
        {
            try
            {
                Data data;
                bool useBundled = !installed;
                try
                {
                    if (installed) data = ReadDatabase(databasePath);
                    else using (Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(BundledResource)) data = ReadDatabase(resource);
                }
                catch (Exception)
                {
                    if (!installed || !bundled || disposed) throw;
                    using (Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(BundledResource)) data = ReadDatabase(resource);
                    useBundled = true;
                }
                if (disposed) return;
                lock (gate) { current = data; installedAt = useBundled ? DateTime.MinValue : File.GetLastWriteTime(databasePath); message = useBundled ? (installed ? "本地更新不可用，已使用内置数据库" : "内置 DB-IP Lite 国家数据库已就绪") : "本地国家数据库已就绪"; }
            }
            catch (Exception ex) { Fail(ex); }
            finally { Interlocked.Exchange(ref busy, 0); }
        }
        private void Activate(string temporary)
        {
            Data data = ReadDatabase(temporary);
            if (disposed) throw new ObjectDisposedException("OfflineCountryDatabase");
            // Validation precedes replacement; failure leaves the previous database intact.
            if (File.Exists(databasePath)) File.Replace(temporary, databasePath, null);
            else File.Move(temporary, databasePath);
            lock (gate) { current = data; installedAt = DateTime.Now; message = "本地国家数据库已就绪"; }
        }
        private void Fail(Exception exception)
        {
            string detail = exception is InvalidDataException ? exception.Message :
                exception is WebException ? "网络连接失败，请检查代理或稍后重试" :
                exception is UnauthorizedAccessException ? "没有文件读写权限" : "文件读取或保存失败";
            lock (gate) message = (current == null ? "离线数据库不可用：" : "更新失败，原数据库仍可用：") + detail;
        }
        private static void DeleteTemporary(string path) { if (path != null) try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

        private void CopyBounded(string source, string destination)
        {
            using (FileStream input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (FileStream output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                CopyBounded(input, output);
        }
        private void CopyBounded(Stream input, Stream output)
        {
            byte[] buffer = new byte[65536]; long total = 0; int count;
            while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (disposed) throw new ObjectDisposedException("OfflineCountryDatabase");
                total += count;
                if (total > MaximumFileBytes) throw new InvalidDataException("数据库文件超过大小限制");
                output.Write(buffer, 0, count);
            }
            if (total == 0) throw new InvalidDataException("数据库文件为空");
        }

        private void DownloadTo(string destination)
        {
            DateTime month = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                string url = "https://download.db-ip.com/free/dbip-country-lite-" + month.AddMonths(-attempt).ToString("yyyy-MM", CultureInfo.InvariantCulture) + ".csv.gz";
                try
                {
                    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                    request.AllowAutoRedirect = false;
                    request.Timeout = 20000; request.ReadWriteTimeout = 20000;
                    request.Proxy = WebRequest.GetSystemWebProxy();
                    request.UserAgent = "RouteLens/1.0 (offline country database)";
                    using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    {
                        if ((int)response.StatusCode != 200) throw new InvalidDataException("数据库下载地址未返回文件");
                        if (response.ContentLength > MaximumFileBytes) throw new InvalidDataException("数据库文件超过大小限制");
                        using (Stream input = response.GetResponseStream())
                        using (FileStream output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None)) CopyBounded(input, output);
                    }
                    return;
                }
                catch (WebException error)
                {
                    HttpWebResponse response = error.Response as HttpWebResponse;
                    bool notFound = response != null && response.StatusCode == HttpStatusCode.NotFound;
                    if (response != null) response.Close();
                    if (!notFound || attempt != 0) throw;
                }
            }
        }

        private Data ReadDatabase(string path)
        {
            using (FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                return ReadDatabase(file);
        }
        private Data ReadDatabase(Stream file)
        {
            if (file == null || !file.CanSeek) throw new InvalidDataException("数据库文件不可读取");
            List<Range> v4 = new List<Range>(), v6 = new List<Range>();
            {
                if (file.Length == 0 || file.Length > MaximumFileBytes) throw new InvalidDataException("数据库文件大小无效");
                int first = file.ReadByte(), second = file.ReadByte(); file.Position = 0;
                using (Stream source = first == 0x1f && second == 0x8b ? (Stream)new GZipStream(file, CompressionMode.Decompress) : file)
                using (StreamReader reader = new StreamReader(source, new UTF8Encoding(false, true), true, 65536))
                {
                    char[] chars = new char[16384]; StringBuilder line = new StringBuilder(); long total = 0; int read;
                    while ((read = reader.Read(chars, 0, chars.Length)) > 0)
                    {
                        total += read;
                        if (total > MaximumTextCharacters) throw new InvalidDataException("数据库解压后超过大小限制");
                        if (disposed) throw new ObjectDisposedException("OfflineCountryDatabase");
                        for (int i = 0; i < read; i++)
                        {
                            char ch = chars[i];
                            if (ch == '\n') { AddLine(line.ToString().TrimEnd('\r'), v4, v6); line.Length = 0; }
                            else { if (line.Length >= 1024) throw new InvalidDataException("数据库行格式无效"); line.Append(ch); }
                        }
                    }
                    if (line.Length > 0) AddLine(line.ToString().TrimEnd('\r'), v4, v6);
                }
            }
            if (v4.Count + v6.Count == 0) throw new InvalidDataException("数据库中没有有效网段");
            Comparison<Range> compare = delegate(Range a, Range b) { return a.Start.CompareTo(b.Start); };
            v4.Sort(compare); v6.Sort(compare); ValidateRanges(v4); ValidateRanges(v6);
            return new Data { V4 = v4.ToArray(), V6 = v6.ToArray() };
        }
        private static void AddLine(string line, List<Range> v4, List<Range> v6)
        {
            if (String.IsNullOrWhiteSpace(line)) return;
            string[] fields = line.Split(',');
            if (fields.Length != 3) throw new InvalidDataException("数据库格式不匹配，请使用 DB-IP 国家版 CSV 或 CSV.GZ");
            for (int i = 0; i < fields.Length; i++)
            {
                string value = fields[i].Trim();
                if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"') value = value.Substring(1, value.Length - 2);
                if (value.IndexOf('"') >= 0) throw new InvalidDataException("数据库字段格式无效");
                fields[i] = value;
            }
            IPAddress start, end;
            if (!IPAddress.TryParse(fields[0], out start) || !IPAddress.TryParse(fields[1], out end) || !Flags.ValidCode(fields[2])) throw new InvalidDataException("数据库包含无效的 IP 范围或国家代码");
            bool startV4, endV4; AddressKey first = Key(start, out startV4), last = Key(end, out endV4);
            if (startV4 != endV4 || first.CompareTo(last) > 0) throw new InvalidDataException("数据库包含无效的 IP 范围");
            string code = fields[2].ToUpperInvariant();
            Range valueRange = new Range { Start = first, End = last, Country = (ushort)((code[0] << 8) | code[1]) };
            (startV4 ? v4 : v6).Add(valueRange);
            if (v4.Count + v6.Count > MaximumRanges) throw new InvalidDataException("数据库网段超过数量限制");
        }
        private static void ValidateRanges(List<Range> values)
        {
            for (int i = 1; i < values.Count; i++)
                if (values[i].Start.CompareTo(values[i - 1].End) <= 0) throw new InvalidDataException("数据库包含重复或重叠的 IP 范围");
        }
        private static AddressKey Key(IPAddress address, out bool v4)
        {
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            byte[] bytes = address.GetAddressBytes(); v4 = address.AddressFamily == AddressFamily.InterNetwork;
            AddressKey key = new AddressKey();
            if (v4) for (int i = 0; i < bytes.Length; i++) key.Low = (key.Low << 8) | bytes[i];
            else for (int i = 0; i < bytes.Length; i++) { if (i < 8) key.High = (key.High << 8) | bytes[i]; else key.Low = (key.Low << 8) | bytes[i]; }
            return key;
        }
        public void Dispose() { disposed = true; }
    }
}
