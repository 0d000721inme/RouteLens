using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace RouteLens
{
    // Volatile session health, never persisted or sent elsewhere.
    public sealed class EvidenceEvent
    {
        public DateTime TimeUtc;
        public string Source, State, Detail;
    }

    public sealed class EvidenceMonitor
    {
        private readonly Queue<EvidenceEvent> _events = new Queue<EvidenceEvent>();
        private string _state, _source, _historyState;
        private long _lastSample;
        public EvidenceMonitor() { SetState("等待采样", "尚未取得连接快照", "连接采样"); }
        public string State { get { return _state; } }
        public string Source { get { return _source; } }

        public void SetState(string state, string detail, string source)
        {
            if (state == _state && source == _source) return;
            _state = state; _source = source;
            Add(source, state, detail);
            if (state != "采样中") _lastSample = 0;
        }
        public void RecordSample(NetworkSnapshot snapshot)
        {
            string source = snapshot == null || String.IsNullOrWhiteSpace(snapshot.SourceName) ? "连接采样" : snapshot.SourceName;
            if (snapshot == null || snapshot.Connections == null || !String.IsNullOrWhiteSpace(snapshot.Error) ||
                (source.IndexOf("Clash", StringComparison.OrdinalIgnoreCase) >= 0 && !snapshot.ClashConnected))
            { SetState("读取受限", "连接数据不可用；此时段不能判断网络活动。", source); return; }
            long now = Stopwatch.GetTimestamp();
            if (_lastSample != 0 && (now - _lastSample) / (double)Stopwatch.Frequency > 5)
                Add(source, "采样间隔过长", "采样间隔超过 5 秒；这段时间的连接可能遗漏。");
            SetState("采样中", "当前为活动连接快照，短连接可能遗漏。", source);
            _lastSample = now;
        }
        public void RecordAlert(string message)
        {
            if (!String.IsNullOrWhiteSpace(message)) Add("联网提醒", "观察触发", message.IndexOf("持续上传", StringComparison.Ordinal) >= 0 ? "持续上传观察已触发；这不代表恶意行为。" : "已有联网提醒触发；请在提醒页面查看设置。");
        }
        public void RecordUploadAlert(double bytesPerSecond, double seconds)
        {
            Add("联网提醒", "持续上传观察", "Clash 已计速连接连续达到 " + (bytesPerSecond / 1048576d).ToString("0.##") + " MiB/s，至少 " + seconds.ToString("0.#") + " 秒；不代表恶意行为。");
        }
        public void RecordHistory(bool enabled, bool error, bool dropped) { RecordHistory(enabled, error, dropped, 0, 0); }
        public void RecordHistory(bool enabled, bool error, bool dropped, int busySamplesSkipped, int baselineGaps)
        {
            busySamplesSkipped = Math.Max(0, busySamplesSkipped); baselineGaps = Math.Max(0, baselineGaps);
            string state = dropped ? "历史存在遗漏" : error ? "历史读写受限" : baselineGaps > 0 ? "历史计量有缺口" : busySamplesSkipped > 0 ? "历史采样曾跳过" : !enabled ? "记录关闭" : "记录已启用";
            string signature = state + "|" + enabled + "|" + busySamplesSkipped + "|" + baselineGaps;
            if (signature == _historyState) return;
            _historyState = signature;
            string detail = dropped ? "本次运行有部分历史未记录；请查看历史页面。" : error ? "历史读写或设置受限；请查看历史页面。" : baselineGaps > 0 ? "历史采样无法连续计量，无法判断该时段是否有流量遗漏。" : busySamplesSkipped > 0 ? "锁忙时跳过历史采样；后续样本可能补计，但短连接可能遗漏。" : !enabled ? "未开启历史记录；不会补录之前的流量。" : "仅记录可比较的 Clash 连接字节增量。";
            if (busySamplesSkipped > 0 || baselineGaps > 0) detail += " 本次锁忙跳过 " + busySamplesSkipped + " 次，连续计量中断 " + baselineGaps + " 次。";
            if (!enabled && (busySamplesSkipped > 0 || baselineGaps > 0)) detail += " 当前记录已关闭。";
            Add("历史记录", state, detail);
        }
        public void CheckStale()
        {
            if (_state == "采样中" && _lastSample != 0 && (Stopwatch.GetTimestamp() - _lastSample) / (double)Stopwatch.Frequency > 5)
                SetState("采样延迟", "超过 5 秒没有新快照；此时段的活动可能遗漏。", _source);
        }
        private void Add(string source, string state, string detail)
        {
            _events.Enqueue(new EvidenceEvent { TimeUtc = DateTime.UtcNow, Source = source ?? "", State = state ?? "", Detail = detail ?? "" });
            while (_events.Count > 200) _events.Dequeue();
        }
        public EvidenceEvent[] Snapshot() { return _events.ToArray(); }
    }
}
