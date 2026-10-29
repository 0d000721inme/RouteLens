using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Security;
using System.Text;
using System.Threading;
using System.Xml;

namespace RouteLens
{
    public sealed class SystemTimelineEntry
    {
        public DateTime? TimeUtc;
        public string Source, Provider, Description;
        public int EventId;
        public Guid? ProcessGuid;
    }

    public sealed class SystemTimelineChannel
    {
        public string Name, Path, Filter;
        public SystemTimelineChannel(string name, string path, string filter) { Name = name; Path = path; Filter = filter; }
    }

    public sealed class SystemTimelineChannelResult
    {
        public string Source, Status;
        public bool Limited;
        public readonly List<SystemTimelineEntry> Entries = new List<SystemTimelineEntry>();
    }

    public sealed class SystemTimelineReport
    {
        public readonly List<SystemTimelineChannelResult> Sources = new List<SystemTimelineChannelResult>();
        public readonly List<SystemTimelineEntry> Entries = new List<SystemTimelineEntry>();
        public bool Cancelled;
    }

    // Explicit on-demand, local read-only snapshots. No subscription, installation,
    // policy change, disk writes or background reads occur here.
    public static class SystemTimeline
    {
        public const int LimitPerChannel = 100;
        private const long DayMilliseconds = 24L * 60 * 60 * 1000;
        private static readonly SystemTimelineChannel[] Channels = {
            new SystemTimelineChannel("Sysmon", "Microsoft-Windows-Sysmon/Operational", "(EventID=1 or EventID=3 or EventID=5 or EventID=6 or EventID=8 or EventID=10 or EventID=25)"),
            new SystemTimelineChannel("系统服务", "System", "(EventID=7045 and Provider[@Name='Service Control Manager'])"),
            new SystemTimelineChannel("计划任务", "Microsoft-Windows-TaskScheduler/Operational", "(EventID=106 or EventID=140 or EventID=141 or EventID=200 or EventID=201)"),
            new SystemTimelineChannel("任务安全审计", "Security", "(EventID=4698 or EventID=4702)"),
        };

        public static SystemTimelineReport Read(CancellationToken token) { return Collect(ReadChannel, token); }

        internal static SystemTimelineReport Collect(Func<SystemTimelineChannel, CancellationToken, SystemTimelineChannelResult> reader, CancellationToken token)
        {
            if (reader == null) throw new ArgumentNullException("reader");
            SystemTimelineReport report = new SystemTimelineReport();
            foreach (SystemTimelineChannel channel in Channels)
            {
                if (token.IsCancellationRequested) { report.Cancelled = true; break; }
                SystemTimelineChannelResult item;
                try { item = reader(channel, token); }
                catch (OperationCanceledException) { report.Cancelled = true; break; }
                catch (UnauthorizedAccessException) { item = Failure(channel, "无读取权限；请检查该日志访问权限。"); }
                catch (SecurityException) { item = Failure(channel, "无读取权限；请检查该日志访问权限。"); }
                catch (EventLogNotFoundException) { item = Failure(channel, "日志不存在或未安装；无法判断该时段是否发生事件。"); }
                catch (EventLogException) { item = Failure(channel, "无法读取；日志可能未启用、无权限或暂不可用。"); }
                catch (Exception) { item = Failure(channel, "读取失败；无法判断该时段是否发生事件。"); }
                if (item == null) item = Failure(channel, "读取失败；无法判断该时段是否发生事件。");
                report.Sources.Add(item);
                report.Entries.AddRange(item.Entries);
            }
            if (token.IsCancellationRequested) report.Cancelled = true;
            report.Entries.Sort(delegate(SystemTimelineEntry a, SystemTimelineEntry b)
            {
                if (!a.TimeUtc.HasValue && !b.TimeUtc.HasValue) return 0;
                if (!a.TimeUtc.HasValue) return 1;
                if (!b.TimeUtc.HasValue) return -1;
                return b.TimeUtc.Value.CompareTo(a.TimeUtc.Value);
            });
            return report;
        }

        private static SystemTimelineChannelResult Failure(SystemTimelineChannel channel, string status)
        { return new SystemTimelineChannelResult { Source = channel.Name, Status = status }; }

        private static SystemTimelineChannelResult ReadChannel(SystemTimelineChannel channel, CancellationToken token)
        {
            SystemTimelineChannelResult result = new SystemTimelineChannelResult { Source = channel.Name };
            string query = "*[System[" + channel.Filter + " and TimeCreated[timediff(@SystemTime) <= " + DayMilliseconds + "]]]";
            try
            {
                EventLogQuery source = new EventLogQuery(channel.Path, PathType.LogName, query) { ReverseDirection = true };
                using (EventLogReader reader = new EventLogReader(source))
                {
                    while (!token.IsCancellationRequested)
                    {
                        using (EventRecord record = reader.ReadEvent())
                        {
                            if (record == null) break;
                            if (result.Entries.Count >= LimitPerChannel) { result.Limited = true; break; }
                            string provider = record.ProviderName;
                            string xml = null;
                            if (String.Equals(channel.Name, "Sysmon", StringComparison.Ordinal) && String.Equals(provider, "Microsoft-Windows-Sysmon", StringComparison.OrdinalIgnoreCase))
                                try { xml = record.ToXml(); } catch (Exception) { }
                            string description;
                            try { description = record.FormatDescription(); }
                            catch (Exception) { description = null; }
                            if (String.IsNullOrWhiteSpace(description))
                            {
                                try { if (xml == null) xml = record.ToXml(); description = "事件描述不可用，以下是事件 XML：\r\n" + xml; }
                                catch (Exception) { description = "事件描述不可用。"; }
                            }
                            DateTime? created = record.TimeCreated;
                            result.Entries.Add(new SystemTimelineEntry { Source = channel.Name, Provider = Clean(provider, 160), EventId = record.Id,
                                TimeUtc = created.HasValue ? created.Value.ToUniversalTime() : (DateTime?)null, Description = Clean(description, 12000), ProcessGuid = SysmonProcessGuid(channel.Name, provider, xml) });
                        }
                    }
                }
                result.Status = token.IsCancellationRequested ? "读取已取消；结果不完整。" : result.Limited ? "结果超过每个来源上限，仅保留最近 100 条。" :
                    result.Entries.Count == 0 ? "已读取，近 24 小时没有匹配记录；不代表对应事件已启用或不存在。" : "已读取 " + result.Entries.Count + " 条；日志策略、保留范围或其他缺口仍可能限制覆盖。";
            }
            catch (EventLogNotFoundException) { result.Status = "日志不存在或未安装；无法判断该时段是否发生事件。"; }
            catch (UnauthorizedAccessException) { result.Status = "无读取权限；请检查该日志访问权限。"; }
            catch (SecurityException) { result.Status = "无读取权限；请检查该日志访问权限。"; }
            catch (EventLogException) { result.Status = "读取受限；日志可能未启用、权限不足或暂不可用。"; }
            catch (Exception) { result.Status = "读取失败；无法判断该时段是否发生事件。"; }
            return result;
        }

        // Only the exact Sysmon EventData/ProcessGuid field identifies the event's
        // own process instance. SourceProcessGuid, TargetProcessGuid and PID are
        // deliberately not inferred as equivalent.
        internal static Guid? SysmonProcessGuid(string source, string provider, string xml)
        {
            if (!String.Equals(source, "Sysmon", StringComparison.Ordinal) || !String.Equals(provider, "Microsoft-Windows-Sysmon", StringComparison.OrdinalIgnoreCase) ||
                String.IsNullOrWhiteSpace(xml) || xml.Length > 131072) return null;
            const string ns = "http://schemas.microsoft.com/win/2004/08/events/event";
            try
            {
                XmlReaderSettings settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 131072 };
                XmlDocument document = new XmlDocument(); document.XmlResolver = null;
                using (StringReader text = new StringReader(xml)) using (XmlReader reader = XmlReader.Create(text, settings)) document.Load(reader);
                XmlElement root = document.DocumentElement;
                if (root == null || root.LocalName != "Event" || root.NamespaceURI != ns) return null;
                Guid? found = null;
                foreach (XmlNode child in root.ChildNodes)
                {
                    XmlElement eventData = child as XmlElement;
                    if (eventData == null || eventData.LocalName != "EventData" || eventData.NamespaceURI != ns) continue;
                    foreach (XmlNode fieldNode in eventData.ChildNodes)
                    {
                        XmlElement field = fieldNode as XmlElement;
                        if (field == null || field.LocalName != "Data" || field.NamespaceURI != ns || !String.Equals(field.GetAttribute("Name"), "ProcessGuid", StringComparison.Ordinal)) continue;
                        Guid guid;
                        if (found.HasValue || !Guid.TryParse(field.InnerText.Trim(), out guid) || guid == Guid.Empty) return null;
                        found = guid;
                    }
                }
                return found;
            }
            catch (XmlException) { return null; }
            catch (InvalidOperationException) { return null; }
            catch (ArgumentException) { return null; }
        }

        internal static List<SystemTimelineEntry> RelatedEntries(SystemTimelineEntry selected, IEnumerable<SystemTimelineEntry> entries)
        {
            List<SystemTimelineEntry> related = new List<SystemTimelineEntry>();
            if (!HasSysmonIdentity(selected) || entries == null) return related;
            foreach (SystemTimelineEntry candidate in entries)
                if (!Object.ReferenceEquals(selected, candidate) && HasSysmonIdentity(candidate) && candidate.ProcessGuid.Value == selected.ProcessGuid.Value)
                    related.Add(candidate);
            return related;
        }

        private static bool HasSysmonIdentity(SystemTimelineEntry entry)
        {
            return entry != null && entry.ProcessGuid.HasValue && entry.ProcessGuid.Value != Guid.Empty &&
                String.Equals(entry.Source, "Sysmon", StringComparison.Ordinal) && String.Equals(entry.Provider, "Microsoft-Windows-Sysmon", StringComparison.OrdinalIgnoreCase);
        }

        internal static string Clean(string value, int maxLength)
        {
            if (value == null) return "";
            StringBuilder clean = new StringBuilder(Math.Min(value.Length, maxLength));
            bool truncated = false;
            foreach (char c in value)
            {
                if (Char.IsControl(c) && c != '\r' && c != '\n' && c != '\t') continue;
                if (clean.Length >= maxLength) { truncated = true; break; }
                clean.Append(c);
            }
            if (truncated) clean.Append("\r\n…（内容过长，已截断）");
            return clean.ToString();
        }
    }
}
