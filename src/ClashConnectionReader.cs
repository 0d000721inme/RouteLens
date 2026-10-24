using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace RouteLens
{
    // Read-only local controller client. No controller settings or VPN rules are changed.
    public sealed class ClashConnectionReader : IDisposable
    {
        private const int MaximumBody = 8 * 1024 * 1024;
        private const int IoTimeout = 2200;
        private readonly object _gate = new object();
        private List<Endpoint> _configured = new List<Endpoint>();
        private Endpoint _active;
        private DateTime _discoverAfter = DateTime.MinValue;
        private volatile bool _disposed;

        private sealed class Endpoint
        {
            public string PipeName;
            public Uri HttpUri;
            public string Secret;
            public string Version;
            public bool Runtime;
            public DateTime Deadline;
            public List<int> InboundPorts = new List<int>();
            public string Summary
            {
                get
                {
                    string source = PipeName != null ? "Windows 命名管道" : "本机 HTTP · " + HttpUri.Authority;
                    return source + (Runtime ? " · 已核验运行中 Mihomo" : " · Clash/Mihomo") +
                        (String.IsNullOrEmpty(Version) ? "" : " · " + Version);
                }
            }
        }

        public NetworkSnapshot Read(NetworkSnapshot windows)
        {
            lock (_gate)
            {
                if (_disposed) return Failure(windows, "Clash 读取器已关闭", "");
                try { return ReadCore(windows); }
                finally { if (_disposed) ClearState(); }
            }
        }

        private NetworkSnapshot ReadCore(NetworkSnapshot windows)
        {
                DateTime deadline = DateTime.UtcNow.AddSeconds(5);
                if (DateTime.UtcNow >= _discoverAfter)
                {
                    List<Endpoint> updated = DiscoverConfigured();
                    bool same = ConfigurationEquals(_configured, updated);
                    if (!same) _active = null;
                    foreach (Endpoint previous in _configured)
                        if (!Object.ReferenceEquals(previous, _active)) previous.Secret = null;
                    _configured = updated;
                    _discoverAfter = DateTime.UtcNow.AddSeconds(15);
                }
                List<Endpoint> attempts = new List<Endpoint>();
                if (_active != null) attempts.Add(_active);
                foreach (Endpoint endpoint in _configured)
                    if (!SameEndpoint(endpoint, _active)) attempts.Add(endpoint);
                Exception last = null;
                string summary = "";
                foreach (Endpoint endpoint in attempts)
                {
                    if (_disposed || DateTime.UtcNow >= deadline) break;
                    endpoint.Deadline = deadline;
                    summary = endpoint.Summary;
                    try { return ReadEndpoint(endpoint, windows); }
                    catch (Exception ex) { last = ex; }
                }

                // Verge can replace its sidecar pipe while leaving an older generated
                // config on disk. Only probe known local Mihomo pipes and require a
                // unique API-version match, never the Clash Verge service-control pipe.
                try
                {
                    List<Endpoint> runtime = DiscoverRuntimePipes();
                    if (runtime.Count > 3) return Failure(windows, "发现过多 Mihomo 候选接口，无法唯一确定当前实例", "多个本机命名管道");
                    Endpoint live = null;
                    foreach (Endpoint endpoint in runtime)
                    {
                        if (ContainsEndpoint(attempts, endpoint)) continue;
                        if (_disposed || DateTime.UtcNow >= deadline) throw new TimeoutException();
                        endpoint.Deadline = deadline;
                        try
                        {
                            endpoint.Version = ReadVersion(endpoint);
                            if (String.IsNullOrEmpty(endpoint.Version)) continue;
                            if (live != null)
                            {
                                _discoverAfter = DateTime.MinValue;
                                return Failure(windows, "发现多个运行中的 Mihomo 内核，无法确定当前实例", "多个本机命名管道");
                            }
                            live = endpoint;
                        }
                        catch (Exception ex) { last = ex; }
                    }
                    if (live != null)
                    {
                        foreach (Endpoint configured in _configured)
                            foreach (int port in configured.InboundPorts)
                                if (!live.InboundPorts.Contains(port)) live.InboundPorts.Add(port);
                        if (_disposed) return Failure(windows, "Clash 读取器已关闭", "");
                        return ReadEndpoint(live, windows);
                    }
                }
                catch (Exception ex) { last = ex; }
                _discoverAfter = DateTime.MinValue;
                string reason = last == null ? "未发现可读取的 Clash/Mihomo 本机控制接口" : SafeFailure(last);
                return Failure(windows, reason + "；可切换到 Windows 连接查看系统端点", summary);
        }

        private NetworkSnapshot ReadEndpoint(Endpoint endpoint, NetworkSnapshot windows)
        {
            string json = Get(endpoint, "/connections");
            if (_disposed) return Failure(windows, "Clash 读取器已关闭", "");
            List<ConnectionInfo> connections = ParseConnections(json, windows, endpoint);
            _active = endpoint;
            int processes = 0, hosts = 0;
            foreach (ConnectionInfo row in connections)
            {
                if (row.ProcessName != "未识别程序") processes++;
                if (!String.IsNullOrEmpty(row.Host)) hosts++;
            }
            return new NetworkSnapshot
            {
                Connections = connections,
                CapturedAt = DateTime.Now,
                ProxySummary = windows == null ? "" : windows.ProxySummary,
                Error = "",
                ClashConnected = true,
                SourceName = "Clash / Mihomo",
                SourceStatus = "已读取内核连接 · 程序 " + processes + "/" + connections.Count + " · 域名 " + hosts + "/" + connections.Count,
                ControllerSummary = endpoint.Summary
            };
        }

        private static NetworkSnapshot Failure(NetworkSnapshot windows, string status, string controller)
        {
            return new NetworkSnapshot
            {
                Connections = new List<ConnectionInfo>(), CapturedAt = DateTime.Now,
                ProxySummary = windows == null ? "" : windows.ProxySummary,
                Error = status, ClashConnected = false, SourceName = "Clash / Mihomo",
                SourceStatus = status, ControllerSummary = controller
            };
        }

        private static List<Endpoint> DiscoverConfigured()
        {
            List<Endpoint> result = new List<Endpoint>();
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string[] directories = new string[]
            {
                Path.Combine(appData, "io.github.clash-verge-rev.clash-verge-rev"),
                Path.Combine(appData, "io.github.clash-verge.clash-verge"),
                Path.Combine(appData, "clash-verge"),
                Path.Combine(profile, @".config\mihomo"),
                Path.Combine(profile, @".config\clash")
            };
            foreach (string directory in directories)
            {
                string active = Path.Combine(directory, "clash-verge.yaml");
                string fallback = Path.Combine(directory, "config.yaml");
                // An explicitly empty HTTP controller in active config must not be
                // overridden with a disabled port from an older config.yaml.
                string path = File.Exists(active) ? active : fallback;
                if (!File.Exists(path)) continue;
                Dictionary<string, string> fields;
                try { fields = ReadTopLevelFields(path); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                List<int> ports = new List<int>();
                foreach (string key in new string[] { "mixed-port", "port", "socks-port", "redir-port", "tproxy-port" })
                {
                    int port;
                    if (Int32.TryParse(Field(fields, key), out port) && port > 0 && port <= 65535 && !ports.Contains(port)) ports.Add(port);
                }
                string pipe = NormalizePipeName(Field(fields, "external-controller-pipe"));
                if (pipe != null) result.Add(new Endpoint { PipeName = pipe, InboundPorts = ports });
                Uri uri = LocalControllerUri(Field(fields, "external-controller"));
                if (uri != null)
                {
                    string secret = Field(fields, "secret");
                    if (secret.Length <= 4096 && secret.IndexOfAny(new char[] { '\r', '\n' }) < 0)
                        result.Add(new Endpoint { HttpUri = uri, Secret = secret, InboundPorts = ports });
                }
                // The first readable known configuration is authoritative even if
                // controllers are explicitly disabled; do not select an older core.
                break;
            }
            return result;
        }

        private static Dictionary<string, string> ReadTopLevelFields(string path)
        {
            Dictionary<string, string> fields = new Dictionary<string, string>(StringComparer.Ordinal);
            using (StreamReader reader = new StreamReader(path, Encoding.UTF8, true))
            {
                int consumed = 0;
                for (int i = 0; i < 512 && consumed < 128 * 1024; i++)
                {
                    string line = reader.ReadLine();
                    if (line == null) break;
                    consumed += line.Length;
                    if (line.Length == 0 || Char.IsWhiteSpace(line[0]) || line[0] == '#') continue;
                    int colon = line.IndexOf(':');
                    if (colon <= 0) continue;
                    string key = line.Substring(0, colon).Trim();
                    // Do not traverse subscription URLs, proxy definitions, rule lists,
                    // provider credentials, or other unrelated configuration sections.
                    if (key == "proxies" || key == "proxy-groups" || key == "proxy-providers" || key == "rules" || key == "rule-providers") break;
                    if (key != "external-controller-pipe" && key != "external-controller" && key != "secret" &&
                        key != "mixed-port" && key != "port" && key != "socks-port" && key != "redir-port" && key != "tproxy-port") continue;
                    string scalar = YamlScalar(line.Substring(colon + 1));
                    if (scalar != null) fields[key] = scalar;
                }
            }
            return fields;
        }

        private static string YamlScalar(string input)
        {
            string value = input.Trim();
            bool single = false, quoted = false, escaped = false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (quoted && escaped) { escaped = false; continue; }
                if (quoted && c == '\\') { escaped = true; continue; }
                if (!quoted && c == '\'') { if (single && i + 1 < value.Length && value[i + 1] == '\'') { i++; continue; } single = !single; }
                else if (!single && c == '"') quoted = !quoted;
                else if (!single && !quoted && c == '#' && (i == 0 || Char.IsWhiteSpace(value[i - 1]))) { value = value.Substring(0, i).TrimEnd(); break; }
            }
            if (value.Length == 0 || value == "~" || value == "null") return "";
            if (value[0] == '\'' && value.Length >= 2 && value[value.Length - 1] == '\'') return value.Substring(1, value.Length - 2).Replace("''", "'");
            if (value[0] == '"')
            {
                try { return new JavaScriptSerializer().Deserialize<string>(value); }
                catch (Exception) { return null; }
            }
            if (value[0] == '|' || value[0] == '>' || value[0] == '{' || value[0] == '[') return null;
            return value;
        }

        private static string Field(Dictionary<string, string> fields, string key)
        { string value; return fields.TryGetValue(key, out value) ? value : ""; }

        private static Uri LocalControllerUri(string address)
        {
            if (String.IsNullOrWhiteSpace(address)) return null;
            Uri uri;
            if (!Uri.TryCreate(address.IndexOf("://", StringComparison.Ordinal) >= 0 ? address : "http://" + address, UriKind.Absolute, out uri)) return null;
            if ((uri.Scheme != "http" && uri.Scheme != "https") || !String.IsNullOrEmpty(uri.UserInfo) ||
                !String.IsNullOrEmpty(uri.Query) || !String.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != "/" || uri.Port <= 0) return null;
            string host = uri.Host.Trim('[', ']');
            if (String.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) host = "127.0.0.1";
            else
            {
                IPAddress ip;
                if (!IPAddress.TryParse(host, out ip) || !IPAddress.IsLoopback(ip)) return null;
                host = ip.ToString();
            }
            return new UriBuilder(uri.Scheme, host, uri.Port).Uri;
        }

        private static string NormalizePipeName(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return null;
            string name = value.Trim();
            if (name.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase)) name = name.Substring(9);
            if (name.Length == 0 || name.Length > 240 || name.IndexOfAny(new char[] { '\\', '/', ':', '\r', '\n' }) >= 0) return null;
            return name;
        }

        private static List<Endpoint> DiscoverRuntimePipes()
        {
            List<Endpoint> result = new List<Endpoint>();
            string[] paths = Directory.GetFiles(@"\\.\pipe\");
            foreach (string path in paths)
            {
                string name = NormalizePipeName(path);
                if (name == null || (!name.StartsWith("verge-mihomo-", StringComparison.OrdinalIgnoreCase) &&
                    !String.Equals(name, "mihomo", StringComparison.OrdinalIgnoreCase))) continue;
                bool valid = true;
                foreach (char c in name) if (!Char.IsLetterOrDigit(c) && c != '-' && c != '_' && c != '.') { valid = false; break; }
                if (valid) result.Add(new Endpoint { PipeName = name, Runtime = true });
                if (result.Count > 8) throw new InvalidDataException("Too many local core candidates");
            }
            result.Sort(delegate(Endpoint a, Endpoint b) { return String.CompareOrdinal(a.PipeName, b.PipeName); });
            return result;
        }

        private static string ReadVersion(Endpoint endpoint)
        {
            Dictionary<string, object> root = JsonObject(Get(endpoint, "/version"));
            string version = Text(root, "version", 96);
            object meta;
            if (!root.TryGetValue("meta", out meta) || !(meta is bool) || !(bool)meta || version.Length == 0) return null;
            foreach (char c in version) if (!Char.IsLetterOrDigit(c) && c != '.' && c != '-' && c != '_' && c != '+') return "Mihomo";
            return version;
        }

        private static string Get(Endpoint endpoint, string path)
        {
            if (path != "/connections" && path != "/version") throw new InvalidOperationException("Read-only endpoint");
            if (endpoint.PipeName != null)
            {
                using (NamedPipeClientStream pipe = new NamedPipeClientStream(".", endpoint.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                {
                    pipe.Connect(Remaining(endpoint, 1000));
                    byte[] request = Encoding.ASCII.GetBytes("GET " + path + " HTTP/1.1\r\nHost: localhost\r\nAccept: application/json\r\nConnection: close\r\n\r\n");
                    IAsyncResult write = pipe.BeginWrite(request, 0, request.Length, null, null);
                    using (WaitHandle wait = write.AsyncWaitHandle)
                    {
                        if (!wait.WaitOne(Remaining(endpoint, IoTimeout))) { pipe.Dispose(); throw new TimeoutException(); }
                        pipe.EndWrite(write);
                    }
                    return ReadHttpResponse(pipe, Remaining(endpoint, IoTimeout));
                }
            }
            HttpWebRequest http = (HttpWebRequest)WebRequest.Create(new Uri(endpoint.HttpUri, path));
            http.Method = "GET";
            http.Proxy = null;
            http.AllowAutoRedirect = false;
            http.Timeout = Remaining(endpoint, 1500);
            http.ReadWriteTimeout = Remaining(endpoint, IoTimeout);
            http.KeepAlive = false;
            http.UseDefaultCredentials = false;
            http.Accept = "application/json";
            if (!String.IsNullOrEmpty(endpoint.Secret)) http.Headers[HttpRequestHeader.Authorization] = "Bearer " + endpoint.Secret;
            using (HttpWebResponse response = (HttpWebResponse)http.GetResponse())
            {
                if (response.StatusCode != HttpStatusCode.OK) throw new InvalidDataException("Unexpected controller HTTP status");
                if (response.ContentLength > MaximumBody) throw new InvalidDataException("Controller response too large");
                using (Stream body = response.GetResponseStream())
                    return Encoding.UTF8.GetString(new HttpInput(body, Remaining(endpoint, IoTimeout)).ReadToEnd(MaximumBody));
            }
        }

        private static int Remaining(Endpoint endpoint, int limit)
        {
            if (endpoint.Deadline == DateTime.MinValue) return limit;
            int remaining = (int)Math.Min(limit, (endpoint.Deadline - DateTime.UtcNow).TotalMilliseconds);
            if (remaining <= 0) throw new TimeoutException();
            return remaining;
        }

        // Pure stream parser is also exercised with fictional, fragmented HTTP fixtures.
        private static string ReadHttpResponse(Stream stream, int timeoutMilliseconds)
        {
            HttpInput input = new HttpInput(stream, timeoutMilliseconds);
            int headerBytes = 0;
            string status = input.ReadLine(ref headerBytes);
            string[] parts = status.Split(' ');
            if (parts.Length < 2 || !parts[0].StartsWith("HTTP/1.", StringComparison.Ordinal) || parts[1] != "200")
                throw new InvalidDataException("Unexpected controller HTTP status");
            long length = -1;
            bool chunked = false;
            while (true)
            {
                string header = input.ReadLine(ref headerBytes);
                if (header.Length == 0) break;
                int colon = header.IndexOf(':');
                if (colon <= 0) throw new InvalidDataException("Malformed HTTP header");
                string name = header.Substring(0, colon).Trim();
                string value = header.Substring(colon + 1).Trim();
                if (String.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    long parsed;
                    if (!Int64.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed) || parsed < 0 || parsed > MaximumBody || (length >= 0 && length != parsed))
                        throw new InvalidDataException("Invalid content length");
                    length = parsed;
                }
                if (String.Equals(name, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                {
                    if (!String.Equals(value, "chunked", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsupported transfer encoding");
                    chunked = true;
                }
                if (String.Equals(name, "Content-Encoding", StringComparison.OrdinalIgnoreCase) && !String.Equals(value, "identity", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Unsupported content encoding");
            }
            byte[] bytes;
            if (chunked)
            {
                using (MemoryStream body = new MemoryStream())
                {
                    while (true)
                    {
                        string line = input.ReadLine(ref headerBytes);
                        int extension = line.IndexOf(';');
                        if (extension >= 0) line = line.Substring(0, extension);
                        long chunk;
                        if (!Int64.TryParse(line.Trim(), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out chunk) || chunk < 0 || chunk > MaximumBody - body.Length)
                            throw new InvalidDataException("Invalid HTTP chunk size");
                        if (chunk == 0)
                        {
                            while (input.ReadLine(ref headerBytes).Length != 0) { }
                            break;
                        }
                        input.CopyExact(body, (int)chunk);
                        if (input.ReadByte() != 13 || input.ReadByte() != 10) throw new InvalidDataException("Missing HTTP chunk terminator");
                    }
                    bytes = body.ToArray();
                }
            }
            else if (length >= 0)
            {
                using (MemoryStream body = new MemoryStream()) { input.CopyExact(body, (int)length); bytes = body.ToArray(); }
            }
            else bytes = input.ReadToEnd(MaximumBody);
            return new UTF8Encoding(false, true).GetString(bytes);
        }

        private sealed class HttpInput
        {
            private readonly Stream _stream;
            private readonly DateTime _deadline;
            private readonly byte[] _buffer = new byte[8192];
            private int _offset, _count;
            public HttpInput(Stream stream, int timeout) { _stream = stream; _deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(1, timeout)); }
            private bool Fill()
            {
                int remaining = (int)Math.Max(0, Math.Min(Int32.MaxValue, (_deadline - DateTime.UtcNow).TotalMilliseconds));
                if (remaining <= 0) { _stream.Dispose(); throw new TimeoutException(); }
                IAsyncResult read = _stream.BeginRead(_buffer, 0, _buffer.Length, null, null);
                using (WaitHandle wait = read.AsyncWaitHandle)
                {
                    if (!wait.WaitOne(remaining)) { _stream.Dispose(); throw new TimeoutException(); }
                    _count = _stream.EndRead(read);
                }
                _offset = 0;
                return _count > 0;
            }
            public int ReadByte() { return _offset < _count || Fill() ? _buffer[_offset++] : -1; }
            public string ReadLine(ref int consumed)
            {
                StringBuilder line = new StringBuilder();
                while (true)
                {
                    int next = ReadByte();
                    if (next < 0) throw new EndOfStreamException("Truncated HTTP response");
                    consumed++;
                    if (consumed > 32768 || line.Length > 8192) throw new InvalidDataException("HTTP headers too large");
                    if (next == 13)
                    {
                        if (ReadByte() != 10) throw new InvalidDataException("Malformed HTTP line ending");
                        consumed++;
                        return line.ToString();
                    }
                    if (next == 10) throw new InvalidDataException("Malformed HTTP line ending");
                    line.Append((char)next);
                }
            }
            public void CopyExact(Stream output, int length)
            {
                while (length > 0)
                {
                    if (_offset >= _count && !Fill()) throw new EndOfStreamException("Truncated HTTP body");
                    int size = Math.Min(length, _count - _offset);
                    output.Write(_buffer, _offset, size); _offset += size; length -= size;
                }
            }
            public byte[] ReadToEnd(int limit)
            {
                using (MemoryStream output = new MemoryStream())
                {
                    while (_offset < _count || Fill())
                    {
                        int size = _count - _offset;
                        if (size > limit - output.Length) throw new InvalidDataException("Controller response too large");
                        output.Write(_buffer, _offset, size); _offset += size;
                    }
                    return output.ToArray();
                }
            }
        }

        private static List<ConnectionInfo> ParseConnections(string json, NetworkSnapshot windows, Endpoint endpoint)
        {
            Dictionary<string, object> root = JsonObject(json);
            object rawConnections;
            if (!root.TryGetValue("connections", out rawConnections)) throw new InvalidDataException("Missing connections array");
            List<ConnectionInfo> result = new List<ConnectionInfo>();
            if (rawConnections == null) return result;
            IEnumerable array = rawConnections as IEnumerable;
            if (array == null || rawConnections is string || rawConnections is IDictionary) throw new InvalidDataException("Invalid connections array");
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (object raw in array)
            {
                Dictionary<string, object> item = raw as Dictionary<string, object>;
                if (item == null) continue;
                object rawMetadata;
                Dictionary<string, object> metadata = item.TryGetValue("metadata", out rawMetadata) ? rawMetadata as Dictionary<string, object> : null;
                string id = Text(item, "id", 160);
                if (metadata == null || id.Length == 0 || !ids.Add(id)) continue;
                string protocol = Text(metadata, "network", 12).ToUpperInvariant();
                if (protocol != "TCP" && protocol != "UDP") protocol = "未知";
                string source = IpText(Text(metadata, "sourceIP", 100));
                string destination = IpText(Text(metadata, "destinationIP", 100));
                List<string> chains = StringArray(item, "chains");
                string terminal = chains.Count == 0 ? "" : chains[0];
                chains.Reverse();
                string processPath = Text(metadata, "processPath", 2048);
                bool systemMarker = processPath == ":System" || processPath == ":System Idle Process";
                string process = systemMarker ? processPath.Substring(1) : BaseName(processPath);
                if (process.Length == 0) process = BaseName(Text(metadata, "process", 512));
                string host = Text(metadata, "host", 512);
                if (host.Length == 0) host = Text(metadata, "sniffHost", 512);
                ConnectionInfo row = new ConnectionInfo
                {
                    Id = "clash:" + id, Protocol = protocol,
                    LocalAddress = source, LocalPort = Port(metadata, "sourcePort"),
                    RemoteAddress = destination, RemotePort = Port(metadata, "destinationPort"),
                    ProcessName = process.Length == 0 ? "未识别程序" : process,
                    ProcessPath = processPath, ProcessId = 0,
                    ProcessAttribution = systemMarker ? "Mihomo 系统进程标记（PID 未提供）" : (process.Length == 0 ? "内核未提供进程信息" : "Mihomo 进程元数据（PID 未提供）"),
                    Host = host, ProxyChain = String.Join(" → ", chains.ToArray()),
                    RouteType = terminal == "DIRECT" ? "直连" : (terminal == "REJECT" || terminal == "REJECT-DROP" ? "拒绝" : (terminal.Length == 0 ? "未知" : "代理")),
                    Rule = Text(item, "rule", 160), RulePayload = Text(item, "rulePayload", 2048),
                    DataSource = "Clash / Mihomo", Inbound = InboundText(metadata),
                    UploadBytes = Bytes(item, "upload"), DownloadBytes = Bytes(item, "download"),
                    HasTrafficCounters = HasBytes(item, "upload") && HasBytes(item, "download"),
                    State = "ACTIVE", Scope = IpScope.Classify(destination)
                };
                CorrelateProcess(row, metadata, windows, endpoint);
                result.Add(row);
            }
            return result;
        }

        private static void CorrelateProcess(ConnectionInfo row, Dictionary<string, object> metadata, NetworkSnapshot windows, Endpoint endpoint)
        {
            if (row.Protocol != "TCP" || row.LocalPort == 0 || row.LocalAddress.Length == 0 || windows == null || windows.Connections == null) return;
            if (row.ProcessPath == ":System" || row.ProcessPath == ":System Idle Process") return;
            string type = Text(metadata, "type", 64);
            if (String.Equals(type, "Tun", StringComparison.OrdinalIgnoreCase) || String.Equals(type, "Inner", StringComparison.OrdinalIgnoreCase)) return;
            int inboundPort = Port(metadata, "inboundPort");
            string inboundIp = IpText(Text(metadata, "inboundIP", 100));
            ConnectionInfo match = null;
            foreach (ConnectionInfo native in windows.Connections)
            {
                if (native == null || native.Protocol != "TCP" || native.LocalPort != row.LocalPort ||
                    IpText(native.LocalAddress) != row.LocalAddress || native.ProcessId <= 4 || IsProxyProcess(native.ProcessName)) continue;
                bool destinationMatches = inboundPort > 0
                    ? native.RemotePort == inboundPort && (IsUnspecified(inboundIp) ? IsLoopback(native.RemoteAddress) : IpText(native.RemoteAddress) == inboundIp)
                    : endpoint.InboundPorts.Contains(native.RemotePort) && IsLoopback(native.RemoteAddress);
                if (!destinationMatches) continue;
                if (row.ProcessName != "未识别程序" && !String.Equals(ProcessKey(row.ProcessName), ProcessKey(native.ProcessName), StringComparison.OrdinalIgnoreCase)) continue;
                if (match != null) return; // Endpoint reuse or multiple candidates: keep PID unknown.
                match = native;
            }
            if (match == null) return;
            row.ProcessId = match.ProcessId;
            if (row.ProcessName == "未识别程序")
            {
                row.ProcessName = String.IsNullOrWhiteSpace(match.ProcessName) ? "未识别程序" : match.ProcessName;
                row.ProcessAttribution = "Windows TCP 入站端点唯一关联";
            }
            else row.ProcessAttribution = "Mihomo 进程元数据 + Windows 入站端点";
        }

        private static bool IsProxyProcess(string name)
        {
            string key = ProcessKey(name).ToLowerInvariant();
            return key.Contains("mihomo") || key.StartsWith("clash", StringComparison.Ordinal) || key.StartsWith("verge-mihomo", StringComparison.Ordinal);
        }
        private static string ProcessKey(string value)
        { string name = BaseName(value); return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 4) : name; }
        private static string BaseName(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return "";
            string clean = value.Trim();
            int last = Math.Max(clean.LastIndexOf('/'), clean.LastIndexOf('\\'));
            return last >= 0 ? clean.Substring(last + 1) : clean;
        }
        private static string InboundText(Dictionary<string, object> metadata)
        {
            string type = Text(metadata, "type", 80);
            string name = Text(metadata, "inboundName", 120);
            return type + (name.Length > 0 && name != type ? " · " + name : "");
        }
        private static bool IsUnspecified(string ip) { return ip.Length == 0 || ip == "0.0.0.0" || ip == "::"; }
        private static bool IsLoopback(string ip) { IPAddress address; return IPAddress.TryParse(ip, out address) && IPAddress.IsLoopback(address); }
        private static string IpText(string value)
        {
            IPAddress ip;
            if (!IPAddress.TryParse(value, out ip)) return "";
            byte[] bytes = ip.GetAddressBytes();
            if (bytes.Length == 16)
            {
                bool mapped = bytes[10] == 255 && bytes[11] == 255;
                for (int i = 0; i < 10 && mapped; i++) if (bytes[i] != 0) mapped = false;
                if (mapped) return new IPAddress(new byte[] { bytes[12], bytes[13], bytes[14], bytes[15] }).ToString();
            }
            return ip.ToString();
        }
        private static int Port(Dictionary<string, object> source, string key)
        { int port; return Int32.TryParse(Text(source, key, 12), NumberStyles.None, CultureInfo.InvariantCulture, out port) && port > 0 && port <= 65535 ? port : 0; }
        private static long Bytes(Dictionary<string, object> source, string key)
        { long bytes; return Int64.TryParse(Text(source, key, 32), NumberStyles.Integer, CultureInfo.InvariantCulture, out bytes) ? Math.Max(0, bytes) : 0; }
        private static bool HasBytes(Dictionary<string, object> source, string key)
        { long bytes; return Int64.TryParse(Text(source, key, 32), NumberStyles.Integer, CultureInfo.InvariantCulture, out bytes) && bytes >= 0; }
        private static string Text(Dictionary<string, object> source, string key, int maximum)
        {
            object value;
            if (!source.TryGetValue(key, out value) || value == null || value is IDictionary || value is ArrayList || value is object[]) return "";
            string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            text = text.Replace('\r', ' ').Replace('\n', ' ').Replace('\0', ' ');
            return text.Length <= maximum ? text : text.Substring(0, maximum);
        }
        private static List<string> StringArray(Dictionary<string, object> source, string key)
        {
            List<string> result = new List<string>();
            object raw;
            if (!source.TryGetValue(key, out raw) || raw == null || raw is string || raw is IDictionary) return result;
            IEnumerable values = raw as IEnumerable;
            if (values != null)
                foreach (object value in values)
                {
                    string text = value as string;
                    if (!String.IsNullOrEmpty(text)) result.Add(text.Length <= 256 ? text.Replace('\r', ' ').Replace('\n', ' ') : text.Substring(0, 256));
                    if (result.Count >= 64) break;
                }
            return result;
        }
        private static Dictionary<string, object> JsonObject(string json)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = MaximumBody, RecursionLimit = 64 };
            Dictionary<string, object> root = serializer.DeserializeObject(json) as Dictionary<string, object>;
            if (root == null) throw new InvalidDataException("Expected JSON object");
            return root;
        }
        private static bool SameEndpoint(Endpoint left, Endpoint right)
        {
            return left != null && right != null && String.Equals(left.PipeName, right.PipeName, StringComparison.OrdinalIgnoreCase) &&
                Object.Equals(left.HttpUri, right.HttpUri);
        }
        private static bool ContainsEndpoint(List<Endpoint> endpoints, Endpoint endpoint)
        { foreach (Endpoint existing in endpoints) if (SameEndpoint(existing, endpoint)) return true; return false; }
        private static bool ConfigurationEquals(List<Endpoint> left, List<Endpoint> right)
        {
            if (left.Count != right.Count) return false;
            for (int i = 0; i < left.Count; i++)
            {
                if (!SameEndpoint(left[i], right[i]) || left[i].Secret != right[i].Secret || left[i].InboundPorts.Count != right[i].InboundPorts.Count) return false;
                for (int j = 0; j < left[i].InboundPorts.Count; j++) if (left[i].InboundPorts[j] != right[i].InboundPorts[j]) return false;
            }
            return true;
        }
        private static string SafeFailure(Exception exception)
        {
            if (exception is UnauthorizedAccessException) return "当前用户无权读取本机控制接口";
            if (exception is TimeoutException) return "本机 Clash/Mihomo 控制接口读取超时";
            WebException web = exception as WebException;
            if (web != null)
            {
                HttpWebResponse response = web.Response as HttpWebResponse;
                if (response != null)
                {
                    int status = (int)response.StatusCode;
                    response.Dispose();
                    return status == 401 || status == 403 ? "本机控制接口鉴权失败" : "本机控制接口返回 HTTP " + status;
                }
                return "本机控制接口未监听或已重启";
            }
            if (exception is InvalidDataException || exception is ArgumentException || exception is InvalidOperationException)
                return "本机控制接口响应格式不受支持或数据超出限制";
            return "暂时无法读取 Clash/Mihomo 本机连接";
        }

        public void Dispose()
        {
            _disposed = true;
            // MainForm closes on the UI thread. A pending read owns its own streams
            // and bounded deadline, so disposal must never wait for network IO.
            if (Monitor.TryEnter(_gate))
            {
                try { ClearState(); }
                finally { Monitor.Exit(_gate); }
            }
        }

        private void ClearState()
        {
            foreach (Endpoint endpoint in _configured) endpoint.Secret = null;
            if (_active != null) _active.Secret = null;
            _configured.Clear();
            _active = null;
        }
    }
}
