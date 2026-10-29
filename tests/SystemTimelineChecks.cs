using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using RouteLens;

internal static class SystemTimelineChecks
{
    private static int _checks;
    private static void Check(bool value, string name) { _checks++; if (!value) throw new Exception(name); }
    private static T Field<T>(object target, string name) where T : class
    { return (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target); }

    [STAThread]
    private static int Main()
    {
        try
        {
            DateTime now = DateTime.UtcNow;
            int calls = 0;
            SystemTimelineReport report = SystemTimeline.Collect(delegate(SystemTimelineChannel channel, CancellationToken token)
            {
                calls++;
                if (channel.Name == "系统服务") throw new UnauthorizedAccessException();
                if (channel.Name == "计划任务") throw new System.Diagnostics.Eventing.Reader.EventLogNotFoundException();
                SystemTimelineChannelResult item = new SystemTimelineChannelResult { Source = channel.Name, Status = "虚构来源" };
                item.Entries.Add(new SystemTimelineEntry { Source = channel.Name, TimeUtc = calls == 1 ? now.AddMinutes(-1) : now, EventId = calls, Provider = "fictional", Description = "虚构事件" });
                return item;
            }, CancellationToken.None);
            Check(calls == 4 && report.Sources.Count == 4, "all expected source statuses");
            Check(report.Entries.Count == 2 && report.Entries[0].TimeUtc == now, "chronological sort");
            Check(report.Sources[1].Status.Contains("权限"), "permission failure shown");
            Check(report.Sources[2].Status.Contains("不存在"), "missing channel shown");
            Check(SystemTimeline.Clean("first\u0000\u001b\r\nsecond", 100) == "first\r\nsecond", "unsafe controls removed");
            Check(SystemTimeline.Clean("123456", 3).StartsWith("123\r\n") && SystemTimeline.Clean("123456", 3).Contains("截断"), "bounded detail reported");
            Guid firstGuid = new Guid("A1111111-1111-4111-8111-111111111111"), secondGuid = new Guid("B2222222-2222-4222-8222-222222222222");
            string field = "<Data Name='ProcessGuid'>{" + firstGuid + "}</Data>";
            string firstXml = EventXml(field + "<Data Name='ProcessId'>1234</Data>");
            Check(SystemTimeline.SysmonProcessGuid("Sysmon", "Microsoft-Windows-Sysmon", firstXml) == firstGuid, "valid Sysmon ProcessGuid parsed");
            Check(SystemTimeline.SysmonProcessGuid("Sysmon", "Microsoft-Windows-Sysmon", EventXml("<Data Name='ProcessId'>1234</Data>")) == null, "missing ProcessGuid remains unknown");
            Check(SystemTimeline.SysmonProcessGuid("Sysmon", "Microsoft-Windows-Sysmon", EventXml("<Data Name='SourceProcessGuid'>{" + firstGuid + "}</Data>")) == null, "source process field is not event process");
            Check(SystemTimeline.SysmonProcessGuid("Sysmon", "Microsoft-Windows-Sysmon", EventXml(field + field)) == null, "duplicate ProcessGuid rejected");
            Check(SystemTimeline.SysmonProcessGuid("Sysmon", "Microsoft-Windows-Sysmon", EventXml("<Data Name='ProcessGuid'>" + Guid.Empty + "</Data>")) == null, "empty GUID rejected");
            Check(SystemTimeline.SysmonProcessGuid("Sysmon", "Microsoft-Windows-Sysmon", EventXml("<Data Name='ProcessGuid'>broken</Data>")) == null, "malformed GUID rejected");
            Check(SystemTimeline.SysmonProcessGuid("系统服务", "Microsoft-Windows-Sysmon", firstXml) == null && SystemTimeline.SysmonProcessGuid("Sysmon", "other", firstXml) == null, "only Sysmon source and provider accepted");
            Check(SystemTimeline.SysmonProcessGuid("Sysmon", "Microsoft-Windows-Sysmon", "<!DOCTYPE Event [<!ENTITY a '" + firstGuid + "'>]>" + EventXml("<Data Name='ProcessGuid'>&a;</Data>")) == null, "DTD rejected");
            Check(SystemTimeline.SysmonProcessGuid("Sysmon", "Microsoft-Windows-Sysmon", new string('x', 131073)) == null, "oversized XML rejected");
            SystemTimelineEntry original = SysmonEvent(now, 1, SystemTimeline.SysmonProcessGuid("Sysmon", "Microsoft-Windows-Sysmon", firstXml));
            SystemTimelineEntry sameInstance = SysmonEvent(now.AddSeconds(1), 3, SystemTimeline.SysmonProcessGuid("Sysmon", "Microsoft-Windows-Sysmon", EventXml(field + "<Data Name='ProcessId'>9999</Data>")));
            SystemTimelineEntry reusedPid = SysmonEvent(now.AddSeconds(2), 5, SystemTimeline.SysmonProcessGuid("Sysmon", "Microsoft-Windows-Sysmon", EventXml("<Data Name='ProcessGuid'>{" + secondGuid + "}</Data><Data Name='ProcessId'>1234</Data>")));
            SystemTimelineEntry noGuid = SysmonEvent(now.AddSeconds(3), 8, null);
            SystemTimelineEntry otherSource = new SystemTimelineEntry { Source = "系统服务", Provider = "Microsoft-Windows-Sysmon", ProcessGuid = firstGuid, EventId = 7045 };
            SystemTimelineEntry[] candidates = { original, sameInstance, reusedPid, noGuid, otherSource };
            Check(SystemTimeline.RelatedEntries(original, candidates).Count == 1 && Object.ReferenceEquals(SystemTimeline.RelatedEntries(original, candidates)[0], sameInstance), "identity joins GUID, never same PID or foreign source");
            Check(SystemTimeline.RelatedEntries(noGuid, candidates).Count == 0, "missing identity never links");
            CancellationTokenSource cancelled = new CancellationTokenSource(); cancelled.Cancel();
            int cancelledCalls = 0;
            SystemTimelineReport stopped = SystemTimeline.Collect(delegate(SystemTimelineChannel c, CancellationToken t) { cancelledCalls++; return null; }, cancelled.Token);
            Check(stopped.Cancelled && cancelledCalls == 0 && stopped.Sources.Count == 0, "cancelled before sources");
            cancelled.Dispose();

            StartupSnapshot before = new StartupSnapshot { CapturedUtc = now.AddHours(-1) }, after = new StartupSnapshot { CapturedUtc = now };
            StartupSource oldRun = new StartupSource { Name = "虚构用户", Complete = true }, newRun = new StartupSource { Name = "虚构用户", Complete = true };
            oldRun.Items.Add("change", new StartupItem { Name = "变化项", Digest = "old" }); oldRun.Items.Add("remove", new StartupItem { Name = "移除项", Digest = "a" });
            oldRun.Items.Add("same", new StartupItem { Name = "相同项", Digest = "s" }); newRun.Items.Add("CHANGE", new StartupItem { Name = "变化项", Digest = "new" });
            newRun.Items.Add("add", new StartupItem { Name = "新增项", Digest = "b" }); newRun.Items.Add("same", new StartupItem { Name = "相同项", Digest = "s" });
            before.Sources.Add(oldRun.Name, oldRun); after.Sources.Add(newRun.Name, newRun);
            before.Sources.Add("无权限", new StartupSource { Name = "无权限", Complete = true }); after.Sources.Add("无权限", new StartupSource { Name = "无权限", Complete = false });
            StartupComparison comparison = StartupSnapshots.Compare(before, after);
            Check(comparison.Changes.Count == 3 && comparison.Changes.Any(c => c.Change == "新增") && comparison.Changes.Any(c => c.Change == "已移除") && comparison.Changes.Any(c => c.Change == "内容已变化"), "startup added, removed, changed and case insensitive names");
            Check(comparison.Unknown.Count == 1 && !comparison.Changes.Any(c => c.Source == "无权限"), "incomplete source is unknown rather than removed");

            Application.EnableVisualStyles();
            int reads = 0;
            using (SystemTimelineDialog dialog = new SystemTimelineDialog(delegate(CancellationToken token) { reads++; return report; }))
            {
                dialog.Show(); Application.DoEvents();
                Check(reads == 0, "no automatic log read");
                Check(Field<Label>(dialog, "_status").Text.Contains("尚未读取"), "initial state");
                dialog.Size = dialog.MinimumSize; Application.DoEvents();
                Button start = Field<Button>(dialog, "_start"), cancel = Field<Button>(dialog, "_cancel");
                Check(start.Visible && start.Parent.ClientRectangle.Contains(start.Bounds) && cancel.Parent.ClientRectangle.Contains(cancel.Bounds), "minimum action layout");
                start.PerformClick();
                WaitFor(delegate { return start.Enabled && reads == 1 && Field<DataGridView>(dialog, "_grid").Rows.Count == 2 && Field<TextBox>(dialog, "_details").Text.Contains("虚构事件"); });
                Check(reads == 1 && start.Enabled, "manual read completes");
                Check(Field<DataGridView>(dialog, "_grid").Rows.Count == 2, "synthetic event rows");
                Check(Field<TextBox>(dialog, "_sources").Text.Contains("权限") && Field<TextBox>(dialog, "_sources").Text.Contains("不存在"), "source limitations visible");
                Check(Field<TextBox>(dialog, "_details").Text.Contains("虚构事件"), "event details visible");
                dialog.Size = new Size(970, 730); Application.DoEvents();
                string preview = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SystemTimeline-preview.png");
                using (Bitmap bitmap = new Bitmap(dialog.Width, dialog.Height)) { dialog.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height)); bitmap.Save(preview); }
                dialog.Close();
            }
            SystemTimelineReport linkedReport = new SystemTimelineReport(); linkedReport.Entries.AddRange(candidates);
            using (SystemTimelineDialog linkedDialog = new SystemTimelineDialog(delegate(CancellationToken token) { return linkedReport; }))
            {
                linkedDialog.Show(); Application.DoEvents();
                Button start = Field<Button>(linkedDialog, "_start"); start.PerformClick();
                WaitFor(delegate { return start.Enabled && Field<DataGridView>(linkedDialog, "_grid").Rows.Count == candidates.Length; });
                string detail = Field<TextBox>(linkedDialog, "_details").Text;
                Check(detail.Contains("另有 1 条事件") && detail.Contains("事件 3") && !detail.Contains("事件 5"), "related Sysmon event displayed without reused PID");
                Check(detail.Contains("不证明因果关系或恶意"), "correlation disclaimer shown");
                string linkedPreview = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SysmonCorrelation-preview.png");
                using (Bitmap bitmap = new Bitmap(linkedDialog.Width, linkedDialog.Height)) { linkedDialog.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height)); bitmap.Save(linkedPreview); }
                linkedDialog.Close();
            }
            int snapshotReads = 0;
            using (StartupSnapshotDialog snapshotDialog = new StartupSnapshotDialog(delegate(CancellationToken token) { snapshotReads++; return snapshotReads == 1 ? before : after; }))
            {
                snapshotDialog.Show(); Application.DoEvents();
                Button baseline = Field<Button>(snapshotDialog, "_baselineButton"), compare = Field<Button>(snapshotDialog, "_compareButton");
                Check(snapshotReads == 0 && !compare.Enabled, "snapshot dialog does not read without explicit action");
                snapshotDialog.Size = snapshotDialog.MinimumSize; Application.DoEvents();
                Check(baseline.Parent.ClientRectangle.Contains(baseline.Bounds) && compare.Parent.ClientRectangle.Contains(compare.Bounds), "snapshot minimum action layout");
                baseline.PerformClick(); WaitFor(delegate { return compare.Enabled && snapshotReads == 1 && Field<Label>(snapshotDialog, "_status").Text.Contains("基线已建立"); });
                Check(compare.Enabled && Field<Label>(snapshotDialog, "_status").Text.Contains("基线已建立"), "explicit baseline read");
                compare.PerformClick(); WaitFor(delegate { return compare.Enabled && snapshotReads == 2 && Field<TextBox>(snapshotDialog, "_results").Text.Contains("新增项"); });
                Check(Field<TextBox>(snapshotDialog, "_results").Text.Contains("新增项") && Field<TextBox>(snapshotDialog, "_results").Text.Contains("无法判断"), "snapshot comparison and unknown shown");
                snapshotDialog.Size = new Size(830, 620); Application.DoEvents();
                string preview = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "StartupSnapshot-preview.png");
                using (Bitmap bitmap = new Bitmap(snapshotDialog.Width, snapshotDialog.Height)) { snapshotDialog.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height)); bitmap.Save(preview); }
                snapshotDialog.Close();
            }
            Console.WriteLine("PASS " + _checks + " synthetic timeline and UI checks; no real logs accessed.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void WaitFor(Func<bool> condition)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(5)) { Application.DoEvents(); Thread.Sleep(10); }
        Application.DoEvents();
        if (!condition()) throw new Exception("timed out waiting for synthetic snapshot");
    }
    private static string EventXml(string fields)
    { return "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><EventData>" + fields + "</EventData></Event>"; }
    private static SystemTimelineEntry SysmonEvent(DateTime time, int id, Guid? guid)
    { return new SystemTimelineEntry { Source = "Sysmon", Provider = "Microsoft-Windows-Sysmon", TimeUtc = time, EventId = id, Description = "虚构事件", ProcessGuid = guid }; }
}
