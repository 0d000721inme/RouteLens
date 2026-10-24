using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;
using System.Threading;
using Microsoft.Win32;

namespace RouteLens
{
    // Passive, memory-only capture. No DNS queries, proxy changes, elevation or drivers.
    public sealed class DnsLeakMonitor : IDisposable
    {
        private const int MaximumCaptures = 16;
        private const string CoverageText = "仅被动观察物理网卡的 IPv4 UDP/TCP 53 明文 DNS。IPv6、DoH/DoT、IP 分片及跨包 TCP 重组未覆盖；未捕获到请求不能保证没有 DNS 泄露。";
        private readonly object _gate = new object();
        private readonly Dictionary<string, Capture> _captures = new Dictionary<string, Capture>(StringComparer.OrdinalIgnoreCase);
        private readonly List<DnsLeakEvent> _events = new List<DnsLeakEvent>();
        private List<DnsAdapterStatus> _adapters = new List<DnsAdapterStatus>();
        private List<ConnectionInfo> _windows = new List<ConnectionInfo>();
        private HashSet<string> _localAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private DateTime _windowsAt;
        private DateTime _lastProxyTraffic;
        private DateTime _startedAt;
        private DateTime _lastUpdated;
        private bool _systemProxy;
        private bool _initializing;
        private bool _disposed;
        private bool _administrator;
        private int _desiredCaptures;
        private string _discoveryError;
        private Run _run;

        private sealed class Run
        {
            internal readonly ManualResetEvent Stop = new ManualResetEvent(false);
            internal volatile bool Stopped;
        }

        private sealed class Target
        {
            internal string Key;
            internal string AdapterId;
            internal string AdapterName;
            internal IPAddress Address;
        }

        private sealed class Capture
        {
            internal readonly DnsLeakMonitor Owner;
            internal readonly Run Run;
            internal readonly Target Target;
            private readonly object _socketGate = new object();
            private Socket _socket;
            internal volatile bool Stopped;
            internal volatile bool Active;
            internal volatile bool Failed;
            internal string Error;
            internal DateTime RetryAt;

            internal Capture(DnsLeakMonitor owner, Run run, Target target)
            {
                Owner = owner;
                Run = run;
                Target = target;
            }

            internal void Start()
            {
                Thread thread = new Thread(ReceiveLoop);
                thread.IsBackground = true;
                thread.Name = "RouteLens DNS IPv4";
                thread.Start();
            }

            internal void Stop()
            {
                Stopped = true;
                Active = false;
                Socket socket;
                lock (_socketGate) { socket = _socket; _socket = null; }
                if (socket != null) { try { socket.Close(0); } catch { } }
            }

            private void ReceiveLoop()
            {
                try
                {
                    if (Stopped || Run.Stopped) return;
                    Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.IP);
                    lock (_socketGate)
                    {
                        if (Stopped || Run.Stopped) { socket.Close(0); return; }
                        _socket = socket;
                    }
                    socket.ReceiveTimeout = 1000;
                    socket.ReceiveBufferSize = 262144;
                    socket.Bind(new IPEndPoint(Target.Address, 0));
                    // RCVALL_IPLEVEL = 3 observes IP traffic without requesting promiscuous mode.
                    socket.IOControl(IOControlCode.ReceiveAll, BitConverter.GetBytes(3), null);
                    Active = true;
                    byte[] buffer = new byte[65535];
                    while (!Stopped && !Run.Stopped)
                    {
                        int count;
                        try { count = socket.Receive(buffer, 0, buffer.Length, SocketFlags.None); }
                        catch (SocketException error)
                        {
                            if (error.SocketErrorCode == SocketError.TimedOut) continue;
                            throw;
                        }
                        DnsPacketEvidence evidence;
                        if (count > 0 && DnsPacketParser.TryParseIPv4(buffer, count, Target.Address, out evidence))
                            Owner.Observe(this, evidence);
                    }
                }
                catch (Exception error)
                {
                    if (!Stopped && !Run.Stopped)
                    {
                        SocketException socketError = error as SocketException;
                        Error = error is UnauthorizedAccessException || error is System.Security.SecurityException
                            || (socketError != null && socketError.SocketErrorCode == SocketError.AccessDenied)
                            ? "当前权限无法捕获此接口"
                            : "捕获不可用" + (socketError == null ? "" : "（" + socketError.ErrorCode + "）");
                        RetryAt = DateTime.Now.AddSeconds(12);
                        Failed = true;
                    }
                }
                finally { Stop(); }
            }
        }

        public DnsLeakMonitor()
        {
            _administrator = IsAdministrator();
            _lastUpdated = DateTime.Now;
        }

        public void Start()
        {
            Run run;
            lock (_gate)
            {
                if (_disposed || _run != null) return;
                run = new Run();
                _run = run;
                _initializing = true;
                _startedAt = DateTime.Now;
                _lastUpdated = _startedAt;
            }
            Thread thread = new Thread(delegate() { Manage(run); });
            thread.IsBackground = true;
            thread.Name = "RouteLens DNS adapters";
            thread.Start();
        }

        public void Stop()
        {
            List<Capture> captures;
            lock (_gate)
            {
                Run run = _run;
                if (run == null) return;
                run.Stopped = true;
                try { run.Stop.Set(); } catch (ObjectDisposedException) { }
                _run = null;
                _initializing = false;
                captures = _captures.Values.ToList();
                _captures.Clear();
                _lastUpdated = DateTime.Now;
            }
            // Socket closure and worker exit never block the WinForms thread.
            ThreadPool.QueueUserWorkItem(delegate { foreach (Capture capture in captures) capture.Stop(); });
        }

        public void Dispose()
        {
            lock (_gate) { if (_disposed) return; _disposed = true; }
            Stop();
        }

        public void UpdateContext(NetworkSnapshot windows, bool clashProxyTraffic)
        {
            lock (_gate)
            {
                if (_disposed) return;
                DateTime now = DateTime.Now;
                if (clashProxyTraffic) _lastProxyTraffic = now;
                _windowsAt = windows == null ? DateTime.MinValue : windows.CapturedAt;
                _windows = windows == null || windows.Connections == null ? new List<ConnectionInfo>()
                    : windows.Connections.Where(c => c != null && c.ProcessId > 0).Select(c => new ConnectionInfo
                    {
                        Protocol = c.Protocol, LocalAddress = c.LocalAddress, LocalPort = c.LocalPort,
                        RemoteAddress = c.RemoteAddress, RemotePort = c.RemotePort,
                        ProcessId = c.ProcessId, ProcessName = c.ProcessName
                    }).ToList();
            }
        }

        public void ClearEvents()
        {
            lock (_gate) { _events.Clear(); _lastUpdated = DateTime.Now; }
        }

        public DnsLeakStatus GetStatus()
        {
            lock (_gate)
            {
                DateTime now = DateTime.Now;
                Prune(now);
                int active = _captures.Values.Count(c => c.Active && !c.Stopped);
                bool running = _run != null;
                bool expected = _systemProxy || Recent(_lastProxyTraffic, now, 120);
                bool recentEvent = _events.Any(e => Recent(e.LastSeen, now, 120));
                bool recentRisk = _events.Any(e => e.ProxyExpectedAtObservation && Recent(e.LastSeen, now, 120));
                string captureError = _captures.Values.Where(c => !String.IsNullOrEmpty(c.Error)).Select(c => c.Error).FirstOrDefault();
                bool limited = !_administrator || _desiredCaptures == 0 || active < _desiredCaptures || !String.IsNullOrEmpty(_discoveryError);
                DnsLeakStatus status = new DnsLeakStatus
                {
                    State = !running ? "stopped" : recentRisk ? "warning" : recentEvent ? "observed" : _initializing ? "starting" : limited ? "limited" : "monitoring",
                    IsAdministrator = _administrator, IsMonitoring = running && active > 0, ProxyExpected = expected,
                    ActiveCaptureCount = active, StartedAt = _startedAt, LastUpdated = _lastUpdated,
                    Coverage = CoverageText, Error = _discoveryError ?? captureError ?? "",
                    Events = _events.OrderByDescending(e => e.LastSeen).Select(CloneEvent).ToList(),
                    Adapters = _adapters.Select(CloneAdapter).ToList()
                };
                foreach (DnsAdapterStatus adapter in status.Adapters)
                {
                    List<Capture> own = _captures.Values.Where(c => c.Target.AdapterId == adapter.Id).ToList();
                    if (own.Count > 0) adapter.CaptureState = own.All(c => c.Active && !c.Stopped) ? "监测中" : own.Any(c => c.Active && !c.Stopped) ? "部分地址监测中" : own.Any(c => c.Failed) ? "捕获失败，稍后重试" : "正在绑定";
                }
                if (status.State == "stopped") { status.Title = "DNS 监测已停止"; status.Summary = "未进行实时捕获"; }
                else if (status.State == "starting") { status.Title = "正在准备 DNS 监测"; status.Summary = "正在读取网卡和捕获权限"; }
                else if (status.State == "warning") { status.Title = "疑似 DNS 泄露"; status.Summary = "曾在有代理依据时观察到物理网卡向外发送明文 DNS"; }
                else if (status.State == "observed") { status.Title = "发现明文 DNS 外发"; status.Summary = "请求捕获时未记录到代理依据，仅作为 DNS 外发观察"; }
                else if (status.State == "limited")
                {
                    status.Title = "DNS 检测受限";
                    status.Summary = !_administrator ? "当前为普通权限，仅展示 DNS 设置，无法判断是否泄露" : _desiredCaptures == 0 ? "没有可捕获的已确认物理 IPv4 接口" : "部分物理接口尚未成功捕获，无法完整判断";
                }
                else { status.Title = "正在监测 DNS 外发"; status.Summary = "暂未捕获到明文 DNS；这不代表没有泄露"; }
                status.Detail = "提醒依据是本机物理接口上的实际 DNS 请求，不以 DNS 服务器所在国家判定。代理依据仅指请求捕获时系统代理已配置或近期存在 Clash 代理活动，不代表所有 DNS 都应走代理。Clash 的 bootstrap DNS、规则直连或局域网解析可能是有意行为；请结合域名、解析器及代理规则判断。"
                    + (running && limited && recentEvent ? " 当前捕获覆盖仍受限。" : "")
                    + " 事件仅保留在内存中 10 分钟；提醒关注最近 2 分钟。";
                return status;
            }
        }

        private void Manage(Run run)
        {
            try
            {
                while (!run.Stopped)
                {
                    try { Refresh(run); }
                    catch
                    {
                        lock (_gate)
                        {
                            if (_run == run) { _initializing = false; _discoveryError = "无法读取网卡信息，稍后重试"; _lastUpdated = DateTime.Now; }
                        }
                    }
                    if (run.Stop.WaitOne(6000)) break;
                }
            }
            finally { run.Stop.Dispose(); }
        }

        private void Refresh(Run run)
        {
            string classificationError;
            HashSet<string> physicalIds = GetPhysicalAdapterIds(out classificationError);
            NetworkInterface[] networks = NetworkInterface.GetAllNetworkInterfaces();
            List<DnsAdapterStatus> adapters = new List<DnsAdapterStatus>();
            List<Target> targets = new List<Target>();
            HashSet<string> localAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (NetworkInterface network in networks)
            {
                if (run.Stopped) return;
                DnsAdapterStatus adapter = new DnsAdapterStatus
                {
                    Id = NormalizeId(network.Id), Name = network.Name,
                    IsPhysical = physicalIds.Contains(NormalizeId(network.Id))
                };
                try
                {
                    IPInterfaceProperties properties = network.GetIPProperties();
                    adapter.Addresses = properties.UnicastAddresses.Select(a => a.Address.ToString()).Distinct().ToList();
                    foreach (string address in adapter.Addresses) localAddresses.Add(address);
                    adapter.DnsServers = properties.DnsAddresses.Select(a => a.ToString()).Distinct().ToList();
                    bool up = network.OperationalStatus == OperationalStatus.Up;
                    adapter.CaptureState = !up ? "离线" : !adapter.IsPhysical ? "非物理或未确认接口（不捕获）" : !_administrator ? "需要管理员权限" : "无 IPv4 地址";
                    if (up && adapter.IsPhysical)
                    {
                        foreach (UnicastIPAddressInformation unicast in properties.UnicastAddresses)
                        {
                            IPAddress address = unicast.Address;
                            if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any)) continue;
                            targets.Add(new Target { Key = adapter.Id + "|" + address, AdapterId = adapter.Id, AdapterName = adapter.Name, Address = address });
                            if (_administrator) adapter.CaptureState = "正在绑定";
                        }
                    }
                }
                catch { adapter.CaptureState = "无法读取接口设置"; }
                if (network.NetworkInterfaceType != NetworkInterfaceType.Loopback) adapters.Add(adapter);
            }
            bool proxy = ReadSystemProxy();
            List<Capture> removed = new List<Capture>();
            List<Capture> added = new List<Capture>();
            lock (_gate)
            {
                if (_run != run || run.Stopped) return;
                _adapters = adapters;
                _localAddresses = localAddresses;
                _systemProxy = proxy;
                _initializing = false;
                _desiredCaptures = targets.Count;
                _discoveryError = classificationError;
                if (targets.Count > MaximumCaptures) _discoveryError = "物理 IPv4 地址超过捕获上限，部分接口未覆盖";
                Dictionary<string, Target> desired = targets.Take(MaximumCaptures).GroupBy(t => t.Key).ToDictionary(g => g.Key, g => g.First());
                foreach (KeyValuePair<string, Capture> pair in _captures.ToList())
                {
                    if (!desired.ContainsKey(pair.Key) || (pair.Value.Failed && DateTime.Now >= pair.Value.RetryAt))
                    { removed.Add(pair.Value); _captures.Remove(pair.Key); }
                }
                if (_administrator)
                {
                    foreach (Target target in desired.Values)
                    {
                        if (_captures.ContainsKey(target.Key)) continue;
                        Capture capture = new Capture(this, run, target);
                        _captures.Add(target.Key, capture);
                        added.Add(capture);
                    }
                }
                Prune(DateTime.Now);
                _lastUpdated = DateTime.Now;
            }
            foreach (Capture capture in removed) capture.Stop();
            foreach (Capture capture in added) capture.Start();
        }

        private void Observe(Capture capture, DnsPacketEvidence evidence)
        {
            lock (_gate)
            {
                Capture current;
                if (_run != capture.Run || capture.Stopped || !_captures.TryGetValue(capture.Target.Key, out current) || current != capture) return;
                if (_localAddresses.Contains(evidence.ResolverAddress)) return;
                DateTime now = DateTime.Now;
                Prune(now);
                bool expected = _systemProxy || Recent(_lastProxyTraffic, now, 120);
                string key = capture.Target.Key + "|" + evidence.ResolverAddress + "|" + evidence.Transport + "|" + evidence.QueryName
                    + "|proxy=" + (expected ? "1" : "0");
                DnsLeakEvent entry = _events.FirstOrDefault(e => e.Id == key);
                ConnectionInfo process = FindProcess(evidence, now);
                if (entry == null)
                {
                    if (_events.Count >= 100) _events.Remove(_events.OrderBy(e => e.LastSeen).First());
                    entry = new DnsLeakEvent
                    {
                        Id = key, AdapterName = capture.Target.AdapterName, SourceAddress = evidence.SourceAddress,
                        SourcePort = evidence.SourcePort, ResolverAddress = evidence.ResolverAddress,
                        ResolverPort = evidence.ResolverPort, Transport = evidence.Transport, QueryName = evidence.QueryName,
                        ProcessId = process == null ? 0 : process.ProcessId, ProcessName = process == null ? "未归因" : process.ProcessName,
                        ProxyExpectedAtObservation = expected, FirstSeen = now, LastSeen = now, Count = 1
                    };
                    _events.Add(entry);
                }
                else
                {
                    entry.Count = entry.Count == Int32.MaxValue ? Int32.MaxValue : entry.Count + 1;
                    entry.LastSeen = now;
                    entry.SourcePort = evidence.SourcePort;
                    // Aggregated queries may come from different processes. Do not retain a stale attribution.
                    if (process == null || entry.ProcessId != process.ProcessId)
                    { entry.ProcessId = 0; entry.ProcessName = "未归因（聚合事件）"; }
                }
                _lastUpdated = now;
            }
        }

        private ConnectionInfo FindProcess(DnsPacketEvidence evidence, DateTime now)
        {
            if (!Recent(_windowsAt, now, 3)) return null;
            bool udp = String.Equals(evidence.Transport, "UDP", StringComparison.OrdinalIgnoreCase);
            List<ConnectionInfo> matches = _windows.Where(c => String.Equals(c.Protocol, evidence.Transport, StringComparison.OrdinalIgnoreCase)
                && c.LocalPort == evidence.SourcePort
                && (SameAddress(c.LocalAddress, evidence.SourceAddress) || (udp && c.LocalAddress == "0.0.0.0"))
                && (udp || (c.RemotePort == evidence.ResolverPort && SameAddress(c.RemoteAddress, evidence.ResolverAddress)))).ToList();
            if (matches.Count == 0 || matches.Select(c => c.ProcessId).Distinct().Count() != 1) return null;
            return matches[0];
        }

        private void Prune(DateTime now) { _events.RemoveAll(e => !Recent(e.LastSeen, now, 600)); }
        private static bool Recent(DateTime time, DateTime now, int seconds)
        { double age = (now - time).TotalSeconds; return time != DateTime.MinValue && age >= -1 && age <= seconds; }
        private static bool SameAddress(string a, string b)
        { IPAddress left, right; return IPAddress.TryParse(a, out left) && IPAddress.TryParse(b, out right) && left.Equals(right); }
        private static string NormalizeId(string id) { return (id ?? "").Trim().Trim('{', '}').ToUpperInvariant(); }
        private static bool IsAdministrator()
        {
            try { using (WindowsIdentity identity = WindowsIdentity.GetCurrent()) return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator); }
            catch { return false; }
        }
        private static bool ReadSystemProxy()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", false))
                    return key != null && Convert.ToInt32(key.GetValue("ProxyEnable", 0)) != 0;
            }
            catch { return false; }
        }

        private static HashSet<string> GetPhysicalAdapterIds(out string error)
        {
            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            error = null;
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(@"root\StandardCimv2", "SELECT InterfaceGuid, HardwareInterface, Virtual, EndPointInterface FROM MSFT_NetAdapter"))
                {
                    searcher.Options.Timeout = TimeSpan.FromSeconds(3);
                    using (ManagementObjectCollection rows = searcher.Get())
                        foreach (ManagementObject row in rows)
                        {
                            using (row)
                            {
                                if (row["HardwareInterface"] is bool && (bool)row["HardwareInterface"]
                                    && row["Virtual"] is bool && !(bool)row["Virtual"]
                                    && row["EndPointInterface"] is bool && !(bool)row["EndPointInterface"])
                                    ids.Add(NormalizeId(Convert.ToString(row["InterfaceGuid"])));
                            }
                        }
                }
                ids.Remove("");
                return ids;
            }
            catch { ids.Clear(); }
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT GUID, PhysicalAdapter FROM Win32_NetworkAdapter"))
                {
                    searcher.Options.Timeout = TimeSpan.FromSeconds(3);
                    using (ManagementObjectCollection rows = searcher.Get())
                        foreach (ManagementObject row in rows)
                        {
                            using (row)
                            {
                                if (row["PhysicalAdapter"] is bool && (bool)row["PhysicalAdapter"])
                                    ids.Add(NormalizeId(Convert.ToString(row["GUID"])));
                            }
                        }
                }
                ids.Remove("");
                return ids;
            }
            catch { ids.Clear(); error = "无法可靠确认物理网卡，未猜测接口类型"; return ids; }
        }

        private static DnsLeakEvent CloneEvent(DnsLeakEvent e)
        {
            return new DnsLeakEvent { Id = e.Id, AdapterName = e.AdapterName, SourceAddress = e.SourceAddress,
                SourcePort = e.SourcePort, ResolverAddress = e.ResolverAddress, ResolverPort = e.ResolverPort,
                Transport = e.Transport, QueryName = e.QueryName, ProcessName = e.ProcessName, ProcessId = e.ProcessId,
                ProxyExpectedAtObservation = e.ProxyExpectedAtObservation,
                Count = e.Count, FirstSeen = e.FirstSeen, LastSeen = e.LastSeen };
        }
        private static DnsAdapterStatus CloneAdapter(DnsAdapterStatus a)
        {
            return new DnsAdapterStatus { Id = a.Id, Name = a.Name, IsPhysical = a.IsPhysical, CaptureState = a.CaptureState,
                Addresses = new List<string>(a.Addresses), DnsServers = new List<string>(a.DnsServers) };
        }
    }
}
