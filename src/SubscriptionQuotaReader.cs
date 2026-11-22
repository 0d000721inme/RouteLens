using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace RouteLens
{
    public sealed partial class ClashConnectionReader
    {
        private const int MaximumQuotaItems = 256;
        private readonly object _quotaGate = new object();
        private SubscriptionQuota[] _quotaCache = new SubscriptionQuota[0];
        private DateTime _quotaRefreshAfter;
        private bool _quotaLoading;

        // The UI can read this cache on either connection-source tab. The worker has its
        // own controller deadline and never takes the lock used for /connections.
        public SubscriptionQuota[] GetSubscriptionQuotas()
        {
            lock (_quotaGate)
            {
                if (_disposed) return new SubscriptionQuota[0];
                if (!_quotaLoading && DateTime.UtcNow >= _quotaRefreshAfter)
                {
                    _quotaLoading = true;
                    ThreadPool.QueueUserWorkItem(delegate { RefreshQuotas(); });
                }
                return (SubscriptionQuota[])_quotaCache.Clone();
            }
        }

        internal static List<SubscriptionQuota> ReadVergeQuotas()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            foreach (string dir in new[] { "io.github.clash-verge-rev.clash-verge-rev", "io.github.clash-verge.clash-verge", "clash-verge" })
            {
                string path = Path.Combine(appData, dir, "profiles.yaml");
                if (!File.Exists(path)) continue;
                using (FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader reader = new StreamReader(file, Encoding.UTF8, true))
                {
                    if (file.Length > 2 * 1024 * 1024) throw new InvalidDataException("Oversized local profile index");
                    return ParseVergeQuotas(reader);
                }
            }
            return new List<SubscriptionQuota>();
        }

        private void RefreshQuotas()
        {
            List<SubscriptionQuota> result = new List<SubscriptionQuota>();
            try
            {
                try { result.AddRange(ReadVergeQuotas()); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                if (!_disposed)
                {
                    Endpoint active = _active;
                    List<Endpoint> candidates = active == null ? DiscoverConfigured() : new List<Endpoint> { active };
                    if (candidates.Count == 0)
                    {
                        List<Endpoint> pipes = DiscoverRuntimePipes();
                        if (pipes.Count == 1) candidates.Add(pipes[0]);
                    }
                    DateTime deadline = DateTime.UtcNow.AddSeconds(4);
                    foreach (Endpoint candidate in candidates)
                    {
                        if (_disposed || DateTime.UtcNow >= deadline) break;
                        Endpoint request = new Endpoint { PipeName = candidate.PipeName, HttpUri = candidate.HttpUri,
                            Secret = candidate.Secret, Deadline = deadline };
                        try
                        {
                            if (candidate.Runtime && active == null && String.IsNullOrEmpty(ReadVersion(request))) continue;
                            result.AddRange(ParseProviderQuotas(Get(request, "/providers/proxies")));
                            break;
                        }
                        catch (WebException ex) { if (ex.Response != null) ex.Response.Close(); }
                        catch (Exception) { /* Try another configured local transport. */ }
                        finally { request.Secret = null; }
                    }
                }
            }
            catch (Exception) { /* An unavailable optional source cannot invalidate the other source. */ }
            finally
            {
                lock (_quotaGate)
                {
                    _quotaCache = _disposed ? new SubscriptionQuota[0] : result.ToArray();
                    _quotaRefreshAfter = DateTime.UtcNow.AddMinutes(1);
                    _quotaLoading = false;
                }
            }
        }

        internal static List<SubscriptionQuota> ParseProviderQuotas(string json)
        {
            List<SubscriptionQuota> result = new List<SubscriptionQuota>();
            Dictionary<string, object> root = JsonObject(json);
            object raw;
            Dictionary<string, object> providers = root.TryGetValue("providers", out raw) ? raw as Dictionary<string, object> : null;
            if (providers == null) return result;
            List<string> names = new List<string>(providers.Keys);
            names.Sort(StringComparer.Ordinal);
            foreach (string name in names)
            {
                Dictionary<string, object> provider = providers[name] as Dictionary<string, object>;
                if (provider == null || !provider.TryGetValue("subscriptionInfo", out raw)) continue;
                Dictionary<string, object> info = raw as Dictionary<string, object>;
                if (info == null) continue;
                Dictionary<string, object> fields = new Dictionary<string, object>(info, StringComparer.OrdinalIgnoreCase);
                SubscriptionQuota quota = CreateQuota(name, name, "Mihomo", fields, result.Count + 1);
                if (quota == null) continue;
                DateTime updated;
                if (DateTime.TryParse(Text(provider, "updatedAt", 64), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out updated) && updated.Year >= 1970)
                    quota.UpdatedAtUtc = updated;
                result.Add(quota);
                if (result.Count >= MaximumQuotaItems) break;
            }
            return result;
        }

        // Only parse the scalar fields emitted by Verge's own profiles.yaml serializer.
        // Nested proxy selections, options, URL, and credentials are never stored.
        internal static List<SubscriptionQuota> ParseVergeQuotas(TextReader reader)
        {
            List<SubscriptionQuota> result = new List<SubscriptionQuota>();
            Dictionary<string, string> item = null;
            Dictionary<string, object> extra = null;
            string current = "", line;
            bool items = false, inExtra = false;
            int itemIndent = -1, consumed = 0;
            while ((line = reader.ReadLine()) != null)
            {
                consumed += line.Length + 1;
                if (consumed > 2 * 1024 * 1024) throw new InvalidDataException("Oversized local profile index");
                string body = line.TrimStart(' ');
                int indent = line.Length - body.Length;
                if (body.Length == 0 || body[0] == '#') continue;
                if (items && body.StartsWith("- ", StringComparison.Ordinal) && (itemIndent < 0 || indent == itemIndent))
                {
                    AddVergeQuota(result, item, extra);
                    item = new Dictionary<string, string>();
                    extra = new Dictionary<string, object>();
                    itemIndent = indent;
                    inExtra = false;
                    body = body.Substring(2);
                    indent += 2;
                }
                else if (indent == 0)
                {
                    AddVergeQuota(result, item, extra);
                    item = null;
                    itemIndent = -1;
                    items = body.TrimEnd() == "items:";
                    if (body.StartsWith("current:", StringComparison.Ordinal))
                        current = YamlScalar(body.Substring(8)) ?? "";
                    continue;
                }
                if (!items || item == null) continue;
                int colon = body.IndexOf(':');
                if (colon < 1) continue;
                string key = body.Substring(0, colon);
                if (indent == itemIndent + 2)
                {
                    inExtra = key == "extra" && body.Substring(colon + 1).Trim().Length == 0;
                    if (key == "uid" || key == "name" || key == "type" || key == "updated")
                        item[key] = YamlScalar(body.Substring(colon + 1)) ?? "";
                }
                else if (inExtra && indent == itemIndent + 4 && (key == "upload" || key == "download" || key == "total" || key == "expire"))
                    extra[key] = YamlScalar(body.Substring(colon + 1));
            }
            AddVergeQuota(result, item, extra);
            if (current.Length != 0)
            {
                string currentId = QuotaId("Clash Verge", current);
                foreach (SubscriptionQuota quota in result) quota.IsCurrent = quota.Id == currentId;
            }
            return result;
        }

        private static void AddVergeQuota(List<SubscriptionQuota> result, Dictionary<string, string> item, Dictionary<string, object> extra)
        {
            if (item == null || result.Count >= MaximumQuotaItems || Field(item, "type") != "remote") return;
            SubscriptionQuota quota = CreateQuota(Field(item, "uid"), Field(item, "name"), "Clash Verge", extra, result.Count + 1);
            if (quota == null) return;
            quota.UpdatedAtUtc = UnixSeconds(Field(item, "updated"));
            result.Add(quota);
        }

        private static SubscriptionQuota CreateQuota(string key, string name, string source, Dictionary<string, object> info, int ordinal)
        {
            long upload, download, total;
            if (String.IsNullOrEmpty(key) || !Int64.TryParse(Text(info, "upload", 32), NumberStyles.None, CultureInfo.InvariantCulture, out upload) ||
                !Int64.TryParse(Text(info, "download", 32), NumberStyles.None, CultureInfo.InvariantCulture, out download) ||
                !Int64.TryParse(Text(info, "total", 32), NumberStyles.None, CultureInfo.InvariantCulture, out total) ||
                total <= 0 || upload > Int64.MaxValue - download) return null;
            string label = (name ?? "").Trim();
            if (label.Length == 0 || label.Length > 64 || label.IndexOfAny(new char[] { ':', '/', '\\', '=', '?', '&', '@', '#', '\r', '\n', '\t' }) >= 0)
                label = "订阅 " + ordinal;
            return new SubscriptionQuota { Id = QuotaId(source, key), Name = label, Source = source,
                UploadBytes = upload, DownloadBytes = download, TotalBytes = total, ExpiresAtUtc = UnixSeconds(Text(info, "expire", 32)) };
        }

        private static string QuotaId(string source, string key)
        {
            using (SHA256 sha = SHA256.Create())
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(source + "\n" + key)));
        }

        private static DateTime? UnixSeconds(string text)
        {
            long value;
            if (!Int64.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) || value <= 0 || value > 253402300799L) return null;
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(value);
        }
    }
}
