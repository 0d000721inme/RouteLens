using System;
using System.Collections.Generic;

namespace RouteLens
{
    public sealed class DnsPacketEvidence
    {
        public string SourceAddress { get; set; }
        public int SourcePort { get; set; }
        public string ResolverAddress { get; set; }
        public int ResolverPort { get; set; }
        public string Transport { get; set; }
        public string QueryName { get; set; }
    }

    public sealed class DnsLeakEvent
    {
        public string Id { get; set; }
        public string AdapterName { get; set; }
        public string SourceAddress { get; set; }
        public int SourcePort { get; set; }
        public string ResolverAddress { get; set; }
        public int ResolverPort { get; set; }
        public string Transport { get; set; }
        public string QueryName { get; set; }
        public string ProcessName { get; set; }
        public int ProcessId { get; set; }
        public bool ProxyExpectedAtObservation { get; set; }
        public int Count { get; set; }
        public DateTime FirstSeen { get; set; }
        public DateTime LastSeen { get; set; }
    }

    public sealed class DnsAdapterStatus
    {
        public DnsAdapterStatus()
        {
            Addresses = new List<string>();
            DnsServers = new List<string>();
        }
        public string Id { get; set; }
        public string Name { get; set; }
        public string CaptureState { get; set; }
        public bool IsPhysical { get; set; }
        public List<string> Addresses { get; set; }
        public List<string> DnsServers { get; set; }
    }

    public sealed class DnsLeakStatus
    {
        public DnsLeakStatus()
        {
            Events = new List<DnsLeakEvent>();
            Adapters = new List<DnsAdapterStatus>();
        }
        public string State { get; set; }
        public string Title { get; set; }
        public string Summary { get; set; }
        public string Detail { get; set; }
        public string Coverage { get; set; }
        public string Error { get; set; }
        public bool IsAdministrator { get; set; }
        public bool IsMonitoring { get; set; }
        public bool ProxyExpected { get; set; }
        public int ActiveCaptureCount { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime LastUpdated { get; set; }
        public List<DnsLeakEvent> Events { get; set; }
        public List<DnsAdapterStatus> Adapters { get; set; }
    }
}
