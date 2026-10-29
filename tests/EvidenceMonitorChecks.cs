using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using RouteLens;

internal static class EvidenceMonitorChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static int Main()
    {
        EvidenceMonitor monitor = new EvidenceMonitor();
        Check(monitor.State == "等待采样", "initial status");
        NetworkSnapshot ok = new NetworkSnapshot { SourceName = "Clash 原始连接", ClashConnected = true, Connections = new List<ConnectionInfo>(), CapturedAt = DateTime.Now };
        monitor.RecordSample(ok);
        Check(monitor.State == "采样中", "sample state");
        int count = monitor.Snapshot().Length;
        monitor.RecordSample(ok); Check(monitor.Snapshot().Length == count, "repeated samples do not flood timeline");
        typeof(EvidenceMonitor).GetField("_lastSample", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(monitor, Stopwatch.GetTimestamp() - 6 * Stopwatch.Frequency);
        monitor.CheckStale(); Check(monitor.State == "采样延迟", "stale sampling is not presented as healthy");
        monitor.RecordSample(ok); Check(monitor.State == "采样中", "sampling recovers after gap");
        monitor.SetState("已暂停", "暂停", "Clash 原始连接"); Check(monitor.State == "已暂停", "pause state");
        monitor.RecordSample(ok); Check(monitor.State == "采样中", "resume state");
        monitor.RecordSample(new NetworkSnapshot { SourceName = "Clash 原始连接", ClashConnected = false, Connections = new List<ConnectionInfo>() });
        Check(monitor.State == "读取受限", "disconnected state");
        monitor.RecordHistory(true, false, false, 0, 0);
        monitor.RecordHistory(true, false, false, 1, 0);
        Check(monitor.Snapshot()[monitor.Snapshot().Length - 1].State == "历史采样曾跳过" && monitor.Snapshot()[monitor.Snapshot().Length - 1].Detail.Contains("可能补计"), "skipped history is uncertain");
        monitor.RecordHistory(true, false, false, 1, 1);
        Check(monitor.Snapshot()[monitor.Snapshot().Length - 1].State == "历史计量有缺口" && monitor.Snapshot()[monitor.Snapshot().Length - 1].Detail.Contains("无法判断"), "baseline gap is uncertain");
        monitor.RecordHistory(true, true, false, 1, 1); monitor.RecordHistory(true, false, true, 1, 1);
        Check(monitor.Snapshot()[monitor.Snapshot().Length - 1].State == "历史存在遗漏", "history dropped state");
        monitor.RecordAlert("持续上传 secret.example 192.0.2.1");
        Check(monitor.Snapshot()[monitor.Snapshot().Length - 1].Detail.IndexOf("secret.example", StringComparison.Ordinal) < 0, "alerts are redacted");
        for (int i = 0; i < 250; i++) monitor.RecordAlert("test");
        Check(monitor.Snapshot().Length == 200, "bounded timeline");
        Console.WriteLine("PASS evidence monitoring: source state, pause, errors, history loss, redaction and bounded session history.");
        return 0;
    }
}
