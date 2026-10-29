using System;
using System.IO;
using System.Text;
using RouteLens;

internal static class ExternalEvidenceChecks
{
    private static int checks;
    private static void Check(bool condition, string name) { checks++; if (!condition) throw new Exception(name); }
    public static int Main()
    {
        const string valid = "{\"ts\":1591367999.430166,\"id.orig_h\":\"192.0.2.1\",\"id.orig_p\":46378,\"id.resp_h\":\"198.51.100.2\",\"id.resp_p\":443,\"proto\":\"tcp\",\"conn_state\":\"SF\"}";
        const string ipv6 = "{\"ts\":\"2020-09-23T00:24:31.210053Z\",\"id.orig_h\":\"2001:db8::1\",\"id.orig_p\":12345,\"id.resp_h\":\"2001:db8::2\",\"id.resp_p\":53,\"proto\":\"udp\",\"conn_state\":\"S0\"}";
        ExternalEvidenceReport report = ExternalEvidence.ParseText(valid + "\n" + ipv6 + "\n");
        Check(report.Connections.Count == 2 && report.InvalidLines == 0 && report.IncompleteRows == 0, "valid JSON lines");
        Check(report.Connections[0].Time == "2020-06-05 14:39:59.430 UTC", "numeric epoch time UTC");
        Check(report.Connections[0].Origin == "192.0.2.1:46378" && report.Connections[0].Responder == "198.51.100.2:443", "origin and responder");
        Check(report.Connections[0].Protocol == "TCP" && report.Connections[0].State == "SF", "protocol and state");
        Check(report.Connections[1].Time == "2020-09-23 00:24:31.210 UTC" && report.Connections[1].Origin == "[2001:db8::1]:12345", "ISO time and IPv6");
        report = ExternalEvidence.ParseText("{\"ts\":\"2020-09-23T08:24:31.210+08:00\",\"id.orig_h\":\"192.0.2.1\",\"id.orig_p\":53,\"id.resp_h\":\"198.51.100.2\",\"id.resp_p\":443,\"proto\":\"tcp\",\"conn_state\":\"SF\"}");
        Check(report.Connections[0].Time == "2020-09-23 00:24:31.210 UTC", "ISO time zone conversion");
        report = ExternalEvidence.ParseText("{\"id.orig_h\":\"bad\",\"id.orig_p\":-1,\"id.resp_h\":null,\"id.resp_p\":65536,\"ts\":\"2020-09-23\",\"proto\":true}\nnot-json\n[]\n{}\n");
        Check(report.Connections.Count == 1 && report.InvalidLines == 3 && report.IncompleteRows == 1, "malformed and missing fields");
        Check(report.Connections[0].Origin.Contains("无效地址") && report.Connections[0].Origin.Contains("无效端口"), "invalid endpoint labelled");
        Check(report.Connections[0].Responder.Contains("未提供") && report.Connections[0].Responder.Contains("无效端口"), "missing endpoint labelled");
        Check(report.Connections[0].Time == "无效时间" && report.Connections[0].Protocol == "无效", "invalid time and protocol labelled");
        report = ExternalEvidence.ParseText("{\"id.orig_h\":\"192.0.2.1\",\"id.orig_p\":1.5,\"id.resp_h\":\"198.51.100.2\",\"id.resp_p\":true,\"ts\":1e100,\"proto\":\"bad\\u0000\"}");
        Check(report.IncompleteRows == 1 && report.Connections[0].Time == "无效时间" && report.Connections[0].Protocol == "无效", "untrusted numeric and control fields");
        Check(report.Connections[0].Origin.Contains("无效端口") && report.Connections[0].Responder.Contains("无效端口"), "fractional and boolean ports rejected");
        report = ExternalEvidence.ParseText(new string('a', ExternalEvidence.MaximumLineChars + 1) + "\n" + valid);
        Check(report.LongLines == 1 && report.Connections.Count == 1, "long line rejected, following record retained");
        StringBuilder records = new StringBuilder();
        for (int i = 0; i < ExternalEvidence.MaximumRecords + 1; i++) records.AppendLine(valid);
        report = ExternalEvidence.ParseText(records.ToString());
        Check(report.Connections.Count == ExternalEvidence.MaximumRecords && report.Truncated, "record limit indicated");
        report = ExternalEvidence.ParseText(new string('\n', ExternalEvidence.MaximumLines) + valid);
        Check(report.LinesRead == ExternalEvidence.MaximumLines && report.Truncated && report.Connections.Count == 0, "line limit indicated");
        Check(ExternalEvidence.LoadLocalFile("\\\\server\\share\\conn.log").Error != null, "UNC path rejected");
        string root = Path.Combine(Path.GetTempPath(), "RouteLens-Zeek-" + Guid.NewGuid().ToString("N"));
        string log = root + ".log", malformed = root + "-bad.log", huge = root + "-large.log";
        try
        {
            File.WriteAllText(log, valid, new UTF8Encoding(true));
            report = ExternalEvidence.LoadLocalFile(log);
            Check(report.Error == null && report.Connections.Count == 1, "UTF-8 BOM local file");
            File.WriteAllBytes(malformed, new byte[] { 0xc3, 0x28 });
            Check(ExternalEvidence.LoadLocalFile(malformed).Error != null, "invalid UTF-8 refused");
            using (FileStream stream = new FileStream(huge, FileMode.CreateNew, FileAccess.Write)) stream.SetLength(ExternalEvidence.MaximumBytes + 1);
            Check(ExternalEvidence.LoadLocalFile(huge).Error != null, "oversize refused");
            Check(ExternalEvidence.LoadLocalFile(root + "-missing.log").Error != null, "missing file reports failure");
        }
        finally { if (File.Exists(log)) File.Delete(log); if (File.Exists(malformed)) File.Delete(malformed); if (File.Exists(huge)) File.Delete(huge); }
        Console.WriteLine("PASS " + checks + " external Zeek JSON checks; synthetic files only, no network or persistence.");
        return 0;
    }
}
