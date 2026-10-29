using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace RouteLens
{
    public sealed class ExternalConnection
    {
        public string Time, Origin, Responder, Protocol, State;
    }

    public sealed class ExternalEvidenceReport
    {
        public readonly List<ExternalConnection> Connections = new List<ExternalConnection>();
        public int LinesRead, InvalidLines, LongLines, IncompleteRows;
        public bool Truncated;
        public string Error;
    }

    // Accepts only bounded local Zeek conn.log JSON-lines files. Parses data;
    // never executes instructions found in the file, uploads, or persists it.
    public static class ExternalEvidence
    {
        public const int MaximumBytes = 8 * 1024 * 1024;
        public const int MaximumLines = 20000;
        public const int MaximumRecords = 5000;
        public const int MaximumLineChars = 16384;

        public static ExternalEvidenceReport LoadLocalFile(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return Failed("未选择文件。");
            try
            {
                string full = Path.GetFullPath(path);
                string root = Path.GetPathRoot(full);
                if (String.IsNullOrEmpty(root) || root.StartsWith("\\\\", StringComparison.Ordinal) || new DriveInfo(root).DriveType == DriveType.Network)
                    return Failed("仅支持选择本地文件，不读取网络路径。");
                Stack<string> pathParts = new Stack<string>();
                for (string part = full; !String.Equals(part, root, StringComparison.OrdinalIgnoreCase); part = Path.GetDirectoryName(part))
                {
                    if (String.IsNullOrEmpty(part)) return Failed("文件路径无效。");
                    pathParts.Push(part);
                }
                while (pathParts.Count > 0)
                    if ((File.GetAttributes(pathParts.Pop()) & FileAttributes.ReparsePoint) != 0)
                        return Failed("不支持符号链接或重解析目录，请选择实际本地文件。");
                byte[] data;
                using (FileStream stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    long size = stream.Length;
                    if (size > MaximumBytes) return Failed("文件超过 8 MiB，未导入。请先选取较小的日志文件。");
                    data = new byte[(int)size]; int position = 0;
                    while (position < data.Length)
                    {
                        int count = stream.Read(data, position, data.Length - position);
                        if (count == 0) return Failed("读取时文件发生变化，请重试。");
                        position += count;
                    }
                    if (stream.Length != size) return Failed("读取时文件发生变化，请重试。");
                }
                string text = new UTF8Encoding(false, true).GetString(data);
                if (text.Length > 0 && text[0] == '\ufeff') text = text.Substring(1);
                return ParseText(text);
            }
            catch (DecoderFallbackException) { return Failed("文件不是有效的 UTF-8 文本，未导入。"); }
            catch (UnauthorizedAccessException) { return Failed("没有权限读取该文件。"); }
            catch (IOException) { return Failed("文件读取失败或正在使用，请重试。"); }
            catch (ArgumentException) { return Failed("文件路径无效。"); }
            catch (NotSupportedException) { return Failed("不支持该文件路径。"); }
            catch (System.Security.SecurityException) { return Failed("当前权限不允许读取该文件。"); }
        }

        internal static ExternalEvidenceReport ParseText(string text)
        {
            ExternalEvidenceReport report = new ExternalEvidenceReport();
            if (text == null) { report.Error = "没有可读取的内容。"; return report; }
            JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = MaximumLineChars, RecursionLimit = 16 };
            using (StringReader reader = new StringReader(text))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (report.LinesRead >= MaximumLines || report.Connections.Count >= MaximumRecords) { report.Truncated = true; break; }
                    report.LinesRead++;
                    if (String.IsNullOrWhiteSpace(line)) continue;
                    if (line.Length > MaximumLineChars) { report.LongLines++; continue; }
                    try
                    {
                        Dictionary<string, object> values = json.DeserializeObject(line) as Dictionary<string, object>;
                        if (values == null || (!values.ContainsKey("id.orig_h") && !values.ContainsKey("id.resp_h"))) { report.InvalidLines++; continue; }
                        bool incomplete = false;
                        ExternalConnection entry = new ExternalConnection();
                        entry.Time = ReadTime(values, ref incomplete);
                        entry.Origin = Endpoint(values, "id.orig_h", "id.orig_p", ref incomplete);
                        entry.Responder = Endpoint(values, "id.resp_h", "id.resp_p", ref incomplete);
                        entry.Protocol = ReadText(values, "proto", 16, ref incomplete).ToUpperInvariant();
                        entry.State = ReadText(values, "conn_state", 32, ref incomplete);
                        report.Connections.Add(entry);
                        if (incomplete) report.IncompleteRows++;
                    }
                    catch (ArgumentException) { report.InvalidLines++; }
                    catch (InvalidOperationException) { report.InvalidLines++; }
                }
            }
            return report;
        }

        private static string Endpoint(Dictionary<string, object> values, string hostKey, string portKey, ref bool incomplete)
        {
            object raw; IPAddress ip; string host;
            if (!values.TryGetValue(hostKey, out raw) || raw == null) { incomplete = true; host = "未提供"; }
            else if (!(raw is string) || !IPAddress.TryParse((string)raw, out ip)) { incomplete = true; host = "无效地址"; }
            else host = ip.ToString();
            if (!values.TryGetValue(portKey, out raw) || raw == null) { incomplete = true; return host + " : 未提供"; }
            double port;
            if (!TryNumber(raw, out port) || port < 0 || port > 65535 || Math.Floor(port) != port) { incomplete = true; return host + " : 无效端口"; }
            return (host.IndexOf(':') >= 0 ? "[" + host + "]" : host) + ":" + ((int)port).ToString(CultureInfo.InvariantCulture);
        }
        private static string ReadTime(Dictionary<string, object> values, ref bool incomplete)
        {
            object raw;
            if (!values.TryGetValue("ts", out raw) || raw == null) { incomplete = true; return "未提供"; }
            DateTimeOffset stamp;
            double seconds;
            if (TryNumber(raw, out seconds))
            {
                try { stamp = DateTimeOffset.FromUnixTimeMilliseconds(checked((long)Math.Floor(seconds * 1000))); return stamp.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " UTC"; }
                catch (ArgumentOutOfRangeException) { }
                catch (OverflowException) { }
            }
            else if (raw is string && Regex.IsMatch((string)raw, @"(?:[zZ]|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant) &&
                DateTimeOffset.TryParse((string)raw, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AdjustToUniversal, out stamp))
                return stamp.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " UTC";
            incomplete = true; return "无效时间";
        }
        private static bool TryNumber(object value, out double result)
        {
            result = 0;
            if (!(value is int) && !(value is long) && !(value is decimal) && !(value is double) && !(value is float)) return false;
            try { result = Convert.ToDouble(value, CultureInfo.InvariantCulture); return !Double.IsNaN(result) && !Double.IsInfinity(result); }
            catch (OverflowException) { return false; }
        }
        private static string ReadText(Dictionary<string, object> values, string key, int maximum, ref bool incomplete)
        {
            object raw;
            if (!values.TryGetValue(key, out raw) || raw == null) { incomplete = true; return "未提供"; }
            string text = raw as string;
            if (String.IsNullOrWhiteSpace(text) || text.Length > maximum) { incomplete = true; return "无效"; }
            foreach (char character in text) if (Char.IsControl(character)) { incomplete = true; return "无效"; }
            return text;
        }
        private static ExternalEvidenceReport Failed(string message) { return new ExternalEvidenceReport { Error = message }; }
    }
}
