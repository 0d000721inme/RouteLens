using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using RouteLens;

internal static class PrivacyDnsChecks
{
    private static int checks;
    [STAThread] private static int Main()
    {
        try { Run().GetAwaiter().GetResult(); Console.WriteLine("PASS " + checks + " DNS test checks; all service responses fictional, no network."); return 0; }
        catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex.Message); return 1; }
    }

    private static async Task Run()
    {
        FakeTransport fixture = new FakeTransport { Id = "test123", Result = "[{\"type\":\"ip\",\"ip\":\"192.0.2.10\",\"country_name\":\"示例地区\"},{\"type\":\"dns\",\"ip\":\"198.51.100.20\",\"asn\":\"测试网络\"},{\"type\":\"dns\",\"ip\":\"198.51.100.20\"},{\"type\":\"dns\",\"ip\":\"2001:db8::123\"},{\"type\":\"conclusion\",\"ip\":\"No leak\"}]" };
        List<string> steps = new List<string>();
        PrivacyDnsReport report = await PrivacyDnsTester.RunAsync(fixture, CancellationToken.None, steps.Add);
        Check(fixture.Requests.Count == 10 && fixture.Probes == 8, "Expected ID, 8 probes and a single result request");
        Check(fixture.Requests[0] == "https://bash.ws/id" && fixture.Requests[9].Contains("/test/test123?json=2"), "Unexpected endpoints");
        Check(report.Error == null && !report.Cancelled && report.Resolvers.Count == 2 && report.Exits.Count == 1, "Duplicate handling or parsed endpoints incorrect");
        Check(report.Resolvers[0].Address == "198.51.100.20" && report.Resolvers[0].Organization == "测试网络", "Resolver evidence lost");
        Check(steps.Count == 3 && report.CheckedAt != default(DateTime), "Progress/time missing");
        string message = PrivacyDnsDialog.FormatReport(report);
        Check(message.Contains("198.51.100.20") && message.Contains("192.0.2.10") && message.Contains("不能保证安全") && !message.Contains("No leak"), "Unverified service verdict was exposed as trusted");

        fixture = new FakeTransport { Id = "invalid.example/path", Result = "[]" };
        report = await PrivacyDnsTester.RunAsync(fixture, CancellationToken.None, null);
        Check(report.Error != null && fixture.Probes == 0 && fixture.Requests.Count == 1, "Untrusted ID was used as DNS target");
        fixture = new FakeTransport { Id = "ok", Result = "[]" };
        report = await PrivacyDnsTester.RunAsync(fixture, CancellationToken.None, null);
        Check(report.Error.Contains("无法判断") && report.Resolvers.Count == 0, "Empty data mistaken for safety");
        fixture = new FakeTransport { Id = "ok", Result = "[{\"type\":\"error\",\"text\":\"server secret detail\"}]" };
        report = await PrivacyDnsTester.RunAsync(fixture, CancellationToken.None, null);
        Check(report.Error != null && !report.Error.Contains("secret"), "Service error was trusted directly");

        report = new PrivacyDnsReport(); PrivacyDnsTester.ParseResult("[invalid]", report);
        Check(report.Error != null, "Invalid JSON accepted");
        report = new PrivacyDnsReport(); PrivacyDnsTester.ParseResult("{\"type\":\"dns\"}", report);
        Check(report.Error != null, "Non-array JSON accepted");
        report = new PrivacyDnsReport(); PrivacyDnsTester.ParseResult("[{\"type\":\"dns\",\"ip\":\"not an IP\"},{\"type\":\"dns\",\"ip\":\"203.0.113.4\",\"city_name\":\"A\\nB\"}]", report);
        Check(report.Resolvers.Count == 1 && report.Resolvers[0].City == "AB", "Invalid addresses/control characters accepted");
        report = new PrivacyDnsReport(); PrivacyDnsTester.ParseResult(new string('a', 65537), report);
        Check(report.Error != null, "Oversized result accepted");

        using (CancellationTokenSource cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel(); fixture = new FakeTransport { Id = "ok", Result = "[]" };
            report = await PrivacyDnsTester.RunAsync(fixture, cancellation.Token, null);
            Check(report.Cancelled && fixture.Requests.Count == 0, "Pre-cancelled test sent requests");
        }
        using (CancellationTokenSource cancellation = new CancellationTokenSource())
        {
            cancellation.CancelAfter(30); fixture = new FakeTransport { Delay = true, Id = "ok", Result = "[]" };
            report = await PrivacyDnsTester.RunAsync(fixture, cancellation.Token, null);
            Check(report.Cancelled, "In-flight cancellation not reported");
        }
        bool rejected = false;
        try { await new PrivacyDnsNetworkTransport().GetAsync("https://example.invalid/not-bash", CancellationToken.None); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Foreign destination not rejected");

        using (PrivacyDnsDialog dialog = new PrivacyDnsDialog())
        {
            Label status = (Label)typeof(PrivacyDnsDialog).GetField("_status", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(dialog);
            Check(status.Text.Contains("尚未开始") && status.Text.Contains("点击"), "Dialog must be idle until explicit click");
        }
    }
    private static void Check(bool condition, string detail) { checks++; if (!condition) throw new Exception(detail); }

    private sealed class FakeTransport : IPrivacyDnsTransport
    {
        public readonly List<string> Requests = new List<string>();
        public string Id, Result; public int Probes; public bool Delay;
        public async Task<string> GetAsync(string url, CancellationToken cancellation)
        {
            Requests.Add(url);
            if (Delay) await Task.Delay(1000, cancellation);
            return url.EndsWith("/id") ? Id : Result;
        }
        public Task ProbeAsync(string url, CancellationToken cancellation) { cancellation.ThrowIfCancellationRequested(); Requests.Add(url); Probes++; return Task.FromResult(0); }
    }
}
