using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace RouteLens
{
    public sealed class StartupItem
    {
        public string Name, Digest;
    }
    public sealed class StartupSource
    {
        public string Name, Status;
        public bool Complete;
        public readonly Dictionary<string, StartupItem> Items = new Dictionary<string, StartupItem>(StringComparer.OrdinalIgnoreCase);
    }
    public sealed class StartupSnapshot
    {
        public DateTime CapturedUtc;
        public bool Cancelled;
        public readonly Dictionary<string, StartupSource> Sources = new Dictionary<string, StartupSource>(StringComparer.Ordinal);
    }
    public sealed class StartupChange
    {
        public string Source, Name, Change;
    }
    public sealed class StartupComparison
    {
        public readonly List<StartupChange> Changes = new List<StartupChange>();
        public readonly List<string> Unknown = new List<string>();
    }
    public static class StartupSnapshots
    {
        private const int MaximumItems = 512;
        private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        // A read is only started by an explicit user action; no values are uploaded or saved.
        public static StartupSnapshot Read(CancellationToken token)
        {
            StartupSnapshot snapshot = new StartupSnapshot { CapturedUtc = DateTime.UtcNow };
            ReadSource(snapshot, RegistryHive.CurrentUser, Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default, Environment.Is64BitOperatingSystem ? "当前用户 · 64 位" : "当前用户 · 默认视图", token);
            if (Environment.Is64BitOperatingSystem) ReadSource(snapshot, RegistryHive.CurrentUser, RegistryView.Registry32, "当前用户 · 32 位", token);
            ReadSource(snapshot, RegistryHive.LocalMachine, Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default, Environment.Is64BitOperatingSystem ? "所有用户 · 64 位" : "所有用户 · 默认视图", token);
            if (Environment.Is64BitOperatingSystem) ReadSource(snapshot, RegistryHive.LocalMachine, RegistryView.Registry32, "所有用户 · 32 位", token);
            snapshot.Cancelled = token.IsCancellationRequested;
            return snapshot;
        }
        private static void ReadSource(StartupSnapshot snapshot, RegistryHive hive, RegistryView view, string label, CancellationToken token)
        {
            if (token.IsCancellationRequested) return;
            StartupSource source = new StartupSource { Name = label, Status = "未知" }; snapshot.Sources.Add(label, source);
            try
            {
                using (RegistryKey root = RegistryKey.OpenBaseKey(hive, view))
                using (RegistryKey key = root.OpenSubKey(RunPath, false))
                {
                    if (key == null) { source.Complete = true; source.Status = "该 Run 项不存在。"; return; }
                    string[] names = key.GetValueNames();
                    if (names.Length > MaximumItems) { source.Status = "条目过多，超过 512 条；此来源不参与比较。"; return; }
                    foreach (string name in names)
                    {
                        if (token.IsCancellationRequested) { source.Status = "读取已取消；此来源不参与比较。"; return; }
                        RegistryValueKind kind = key.GetValueKind(name);
                        object value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                        if (value == null) { source.Status = "读取时条目发生变化，未能完整读取。"; return; }
                        source.Items.Add(name, new StartupItem { Name = SystemTimeline.Clean(String.IsNullOrEmpty(name) ? "(默认值)" : name, 256), Digest = Digest(kind, value) });
                    }
                    source.Complete = true; source.Status = "读取完成，" + names.Length + " 条。";
                }
            }
            catch (UnauthorizedAccessException) { source.Status = "无读取权限；此来源不参与比较。"; }
            catch (SecurityException) { source.Status = "无读取权限；此来源不参与比较。"; }
            catch (Exception) { source.Status = "读取失败或条目已变化；此来源不参与比较。"; }
        }
        private static string Digest(RegistryValueKind kind, object value)
        {
            string data;
            byte[] bytes = value as byte[];
            string[] strings = value as string[];
            if (bytes != null) data = Convert.ToBase64String(bytes);
            else if (strings != null) data = String.Join("\0", strings);
            else data = Convert.ToString(value, CultureInfo.InvariantCulture);
            using (SHA256 hash = SHA256.Create()) return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(((int)kind).ToString(CultureInfo.InvariantCulture) + "\0" + data)));
        }
        public static StartupComparison Compare(StartupSnapshot before, StartupSnapshot after)
        {
            if (before == null || after == null) throw new ArgumentNullException("快照不能为空");
            StartupComparison result = new StartupComparison();
            HashSet<string> sources = new HashSet<string>(before.Sources.Keys, StringComparer.Ordinal);
            sources.UnionWith(after.Sources.Keys);
            foreach (string name in sources)
            {
                StartupSource oldSource, newSource;
                if (!before.Sources.TryGetValue(name, out oldSource) || !after.Sources.TryGetValue(name, out newSource) || !oldSource.Complete || !newSource.Complete)
                { result.Unknown.Add(name + "：至少一次快照未完整读取，无法判断变化。"); continue; }
                foreach (KeyValuePair<string, StartupItem> oldItem in oldSource.Items)
                {
                    StartupItem current;
                    if (!newSource.Items.TryGetValue(oldItem.Key, out current)) result.Changes.Add(new StartupChange { Source = name, Name = oldItem.Value.Name, Change = "已移除" });
                    else if (!String.Equals(oldItem.Value.Digest, current.Digest, StringComparison.Ordinal)) result.Changes.Add(new StartupChange { Source = name, Name = current.Name, Change = "内容已变化" });
                }
                foreach (KeyValuePair<string, StartupItem> newItem in newSource.Items)
                    if (!oldSource.Items.ContainsKey(newItem.Key)) result.Changes.Add(new StartupChange { Source = name, Name = newItem.Value.Name, Change = "新增" });
            }
            return result;
        }
    }
}
