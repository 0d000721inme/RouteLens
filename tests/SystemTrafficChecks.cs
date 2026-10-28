using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using RouteLens;

internal static class SystemTrafficChecks
{
    private static int checks;
    private static void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
    private static SystemAdapterSample Sample(string id, long down, long up, bool available, bool upState, bool? physical, NetworkInterfaceType kind)
    { return new SystemAdapterSample { Id = id, Name = "虚构 " + id, Type = kind, Physical = physical, Up = upState, Available = available, ReceivedBytes = down, SentBytes = up }; }
    private static SystemAdapterTraffic One(SystemTrafficSnapshot snapshot, string id) { return snapshot.Adapters.Single(x => x.Id == id); }
    private static int Main()
    {
        DateTime start = DateTime.Now;
        SystemTrafficMonitor monitor = new SystemTrafficMonitor();
        SystemAdapterSample physical = Sample("physical", 1000, 500, true, true, true, NetworkInterfaceType.Ethernet);
        SystemAdapterSample virt = Sample("virtual", 3000, 700, true, true, false, NetworkInterfaceType.Ethernet);
        SystemAdapterSample unknown = Sample("unknown", 0, 0, false, true, null, NetworkInterfaceType.Ethernet);
        SystemTrafficSnapshot first = monitor.Update(new[] { physical, virt, unknown }, start);
        Check(first.Adapters.Count == 3 && first.Adapters.All(x => !x.Measured), "First sample was calculated");
        Check(One(first, "physical").Kind == "物理" && One(first, "virtual").Kind == "虚拟" && One(first, "unknown").Kind == "类型未知", "WMI classifications incorrect");
        Check(One(first, "unknown").Status == "读取受限", "Unavailable counter mislabeled");
        physical.ReceivedBytes += 1000; physical.SentBytes += 400; virt.ReceivedBytes += 1000; virt.SentBytes += 400;
        SystemTrafficSnapshot second = monitor.Update(new[] { physical, virt }, start.AddSeconds(2));
        Check(One(second, "physical").Measured && Math.Abs(One(second, "physical").DownloadPerSecond - 500) < .01 && Math.Abs(One(second, "physical").UploadPerSecond - 200) < .01, "Physical delta or elapsed time incorrect");
        Check(One(second, "virtual").DownloadPerSecond == 500, "Virtual interface speed incorrect");
        physical.ReceivedBytes = 1; physical.SentBytes = 1;
        SystemTrafficSnapshot reset = monitor.Update(new[] { physical }, start.AddSeconds(3));
        Check(!One(reset, "physical").Measured, "Counter reset fabricated speed");
        physical.ReceivedBytes = 7; physical.SentBytes = 4;
        Check(One(monitor.Update(new[] { physical, virt }, start.AddSeconds(4)), "virtual").Measured == false, "Reappearing interface reused stale baseline");
        Check(!One(monitor.Update(new[] { physical }, start.AddSeconds(20)), "physical").Measured, "Long gap fabricated speed");
        Check(!One(monitor.Update(new[] { physical }, start.AddSeconds(20)), "physical").Measured, "Zero interval fabricated speed");
        monitor.Reset();
        Check(!One(monitor.Update(new[] { physical }, start.AddSeconds(21)), "physical").Measured, "Resume included paused interval");
        SystemTrafficSnapshot duplicate = monitor.Update(new[] { physical, physical }, start.AddSeconds(22));
        Check(duplicate.Adapters.Count == 0, "Ambiguous adapter duplicated");
        Check(!One(monitor.Update(new[] { physical }, start.AddSeconds(23)), "physical").Measured, "Duplicate identity preserved baseline");
        Check(SystemTrafficMonitor.Classify(NetworkInterfaceType.Tunnel, true) == "隧道" && SystemTrafficMonitor.Classify(NetworkInterfaceType.Loopback, true) == "回环", "Tunnel/loopback precedence incorrect");
        Check(monitor.Update(null, start.AddSeconds(24)).Error != null, "Unavailable collection presented as empty success");
        Console.WriteLine("PASS " + checks + " synthetic per-interface speed, classifications, reset and pause checks.");
        return 0;
    }
}
