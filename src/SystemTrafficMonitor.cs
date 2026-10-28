using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;

namespace RouteLens
{
    // A system interface counter is not a process counter. The same traffic may
    // traverse both a tunnel and its physical NIC, so it must never be summed.
    public sealed class SystemAdapterSample
    {
        public string Id, Name, Description;
        public NetworkInterfaceType Type;
        public bool? Physical;
        public bool Up, Available;
        public long ReceivedBytes, SentBytes;
    }
    public sealed class SystemAdapterTraffic
    {
        public string Id, Name, Description, Kind, Status;
        public bool Available, Measured;
        public double DownloadPerSecond, UploadPerSecond;
        public long ReceivedBytes, SentBytes;
    }
    public sealed class SystemTrafficSnapshot
    {
        public readonly List<SystemAdapterTraffic> Adapters = new List<SystemAdapterTraffic>();
        public DateTime CapturedAt;
        public string Error;
        public bool Stale;
    }
    public sealed class SystemTrafficMonitor
    {
        private sealed class Before { public long Down, Up; public bool Available; }
        private readonly object _gate = new object();
        private Dictionary<string, Before> _previous = new Dictionary<string, Before>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> _physical = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private double _previousAt;
        private DateTime _classificationAt;
        private int _generation;

        public void Reset() { lock (_gate) { _previous.Clear(); _previousAt = 0; _generation++; } }
        public SystemTrafficSnapshot Capture()
        {
            int generation; lock (_gate) generation = _generation;
            try
            {
                List<SystemAdapterSample> samples = ReadAdapters();
                double when = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
                lock (_gate)
                {
                    if (generation != _generation) return new SystemTrafficSnapshot { CapturedAt = DateTime.Now, Stale = true };
                    return ApplyLocked(samples, when, DateTime.Now);
                }
            }
            catch (Exception ex) { Reset(); return new SystemTrafficSnapshot { CapturedAt = DateTime.Now, Error = "读取系统网卡失败：" + ex.Message }; }
        }
        public SystemTrafficSnapshot Update(IEnumerable<SystemAdapterSample> samples, DateTime timestamp)
        {
            lock (_gate) return ApplyLocked(samples, timestamp.ToUniversalTime().Ticks / (double)TimeSpan.TicksPerSecond, timestamp);
        }
        private SystemTrafficSnapshot ApplyLocked(IEnumerable<SystemAdapterSample> samples, double when, DateTime captured)
        {
            SystemTrafficSnapshot result = new SystemTrafficSnapshot { CapturedAt = captured };
            if (samples == null) { _previous.Clear(); _previousAt = 0; result.Error = "系统网卡数据未提供"; return result; }
            double seconds = when - _previousAt;
            bool interval = _previousAt != 0 && seconds > 0 && seconds <= 5;
            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            List<SystemAdapterSample> all = new List<SystemAdapterSample>();
            foreach (SystemAdapterSample sample in samples)
            {
                if (sample == null || String.IsNullOrWhiteSpace(sample.Id)) continue;
                all.Add(sample); int count; counts.TryGetValue(sample.Id, out count); counts[sample.Id] = count + 1;
            }
            Dictionary<string, Before> next = new Dictionary<string, Before>(StringComparer.OrdinalIgnoreCase);
            foreach (SystemAdapterSample sample in all)
            {
                if (counts[sample.Id] != 1) continue;
                bool available = sample.Up && sample.Available && sample.ReceivedBytes >= 0 && sample.SentBytes >= 0;
                SystemAdapterTraffic item = new SystemAdapterTraffic { Id = sample.Id, Name = sample.Name ?? "未命名网卡", Description = sample.Description ?? "", Kind = Classify(sample.Type, sample.Physical),
                    Status = !sample.Up ? "未连接" : available ? "已连接" : "读取受限", Available = available,
                    ReceivedBytes = Math.Max(0, sample.ReceivedBytes), SentBytes = Math.Max(0, sample.SentBytes) };
                Before old;
                if (interval && available && _previous.TryGetValue(sample.Id, out old) && old.Available && item.ReceivedBytes >= old.Down && item.SentBytes >= old.Up)
                {
                    item.Measured = true; item.DownloadPerSecond = (item.ReceivedBytes - old.Down) / seconds; item.UploadPerSecond = (item.SentBytes - old.Up) / seconds;
                }
                next.Add(sample.Id, new Before { Down = item.ReceivedBytes, Up = item.SentBytes, Available = available });
                result.Adapters.Add(item);
            }
            _previous = next; _previousAt = when;
            result.Adapters.Sort(delegate(SystemAdapterTraffic a, SystemAdapterTraffic b)
            {
                int active = b.Available.CompareTo(a.Available); return active != 0 ? active : StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name);
            });
            return result;
        }
        public static string Classify(NetworkInterfaceType type, bool? physical)
        {
            if (type == NetworkInterfaceType.Loopback) return "回环";
            if (type == NetworkInterfaceType.Tunnel || type == NetworkInterfaceType.Ppp) return "隧道";
            return !physical.HasValue ? "类型未知" : physical.Value ? "物理" : "虚拟";
        }
        private List<SystemAdapterSample> ReadAdapters()
        {
            if (DateTime.UtcNow - _classificationAt > TimeSpan.FromSeconds(30)) RefreshClassification();
            List<SystemAdapterSample> result = new List<SystemAdapterSample>();
            foreach (NetworkInterface network in NetworkInterface.GetAllNetworkInterfaces())
            {
                string id = network.Id ?? "";
                bool physical; bool known = _physical.TryGetValue(id.Trim('{', '}'), out physical);
                SystemAdapterSample sample = new SystemAdapterSample { Id = id, Name = network.Name, Description = network.Description, Type = network.NetworkInterfaceType,
                    Up = network.OperationalStatus == OperationalStatus.Up, Physical = known ? (bool?)physical : null };
                if (sample.Up)
                {
                    try { IPInterfaceStatistics statistics = network.GetIPStatistics(); sample.ReceivedBytes = statistics.BytesReceived; sample.SentBytes = statistics.BytesSent; sample.Available = sample.ReceivedBytes >= 0 && sample.SentBytes >= 0; }
                    catch (Exception) { sample.Available = false; }
                }
                result.Add(sample);
            }
            return result;
        }
        private void RefreshClassification()
        {
            Dictionary<string, bool> current = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT GUID,PhysicalAdapter FROM Win32_NetworkAdapter"))
                using (ManagementObjectCollection rows = searcher.Get())
                    foreach (ManagementObject row in rows)
                    using (row)
                    {
                        string guid = Convert.ToString(row["GUID"]); object physical = row["PhysicalAdapter"];
                        if (!String.IsNullOrWhiteSpace(guid) && physical is bool) current[guid.Trim('{', '}')] = (bool)physical;
                    }
            }
            catch (Exception) { current.Clear(); }
            _physical.Clear(); foreach (KeyValuePair<string, bool> entry in current) _physical.Add(entry.Key, entry.Value);
            _classificationAt = DateTime.UtcNow;
        }
    }
}
