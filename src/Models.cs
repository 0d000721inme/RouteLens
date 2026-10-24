using System;
using System.Collections.Generic;

namespace RouteLens
{
    public sealed class ConnectionInfo
    {
        public string Id { get; set; }
        public string Protocol { get; set; }
        public string LocalAddress { get; set; }
        public int LocalPort { get; set; }
        public string RemoteAddress { get; set; }
        public int RemotePort { get; set; }
        public int ProcessId { get; set; }
        public string ProcessName { get; set; }
        public string State { get; set; }
        public string Scope { get; set; }
        public string Host { get; set; }
        public string ProcessPath { get; set; }
        public string ProcessAttribution { get; set; }
        public string ProxyChain { get; set; }
        public string RouteType { get; set; }
        public string Rule { get; set; }
        public string RulePayload { get; set; }
        public string DataSource { get; set; }
        public string Inbound { get; set; }
        public long UploadBytes { get; set; }
        public long DownloadBytes { get; set; }
        public bool? HasTrafficCounters { get; set; }
    }

    public sealed class NetworkSnapshot
    {
        public List<ConnectionInfo> Connections { get; set; }
        public DateTime CapturedAt { get; set; }
        public string Error { get; set; }
        public string ProxySummary { get; set; }
        public bool ClashConnected { get; set; }
        public string SourceName { get; set; }
        public string SourceStatus { get; set; }
        public string ControllerSummary { get; set; }
    }

    public sealed class GeoResult
    {
        public string Ip { get; set; }
        public string CountryCode { get; set; }
        public string CountryName { get; set; }
        public string Region { get; set; }
        public string City { get; set; }
        public string Organization { get; set; }
        public string Status { get; set; }
        public string Error { get; set; }
        public DateTime CheckedAt { get; set; }
        public bool Success { get { return Status == "ok"; } }
    }

    public sealed class EgressResult
    {
        public string Ip { get; set; }
        public GeoResult Location { get; set; }
        public DateTime CheckedAt { get; set; }
        public string Error { get; set; }
        public bool IsChecking { get; set; }
    }
}
