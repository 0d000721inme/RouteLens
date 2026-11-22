using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using RouteLens;

internal static class ConnectionTraceChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Main()
    {
        IPAddress target;
        foreach (string invalid in new[] { "", "unresolved.example", "1", "0.0.0.0", "::", "224.0.0.1", "ff02::1", "255.255.255.255", "::ffff:224.0.0.1" })
            Require(!ConnectionTrace.TryGetTarget(invalid, out target), "Invalid target accepted: " + invalid);
        Require(ConnectionTrace.TryGetTarget("fe80::1%9", out target) && target.ScopeId == 9, "Lost IPv6 scope");
        Require(ConnectionTrace.TryGetTarget("::ffff:127.0.0.1", out target) && target.Equals(IPAddress.Loopback), "Mapped address");
        List<TraceHop> hops = new List<TraceHop>();
        TraceOutcome reached = ConnectionTrace.Run(delegate(int ttl)
        {
            if (ttl == 1) return ConnectionTrace.Classify(ttl, target, IPStatus.TimedOut, IPAddress.Any, 1000);
            if (ttl == 2) return ConnectionTrace.Classify(ttl, target, IPStatus.TtlExpired, IPAddress.Parse("192.0.2.1"), 17);
            return ConnectionTrace.Classify(ttl, target, IPStatus.Success, target, 20);
        }, hops.Add, CancellationToken.None);
        Require(reached == TraceOutcome.Reached && hops.Count == 3 && hops[2].Number == 3, "Target termination");
        Require(hops[0].Address == "" && !hops[0].RoundtripMs.HasValue, "Timeout attributed to an address or time");
        Require(hops[1].Address == "192.0.2.1" && hops[1].RoundtripMs == 17, "Intermediate hop address or RTT");
        hops.Clear();
        TraceOutcome limit = ConnectionTrace.Run(ttl => ConnectionTrace.Classify(ttl, target, IPStatus.TimedOut, null, 1000),
            hops.Add, CancellationToken.None);
        Require(limit == TraceOutcome.HopLimit && hops.Count == ConnectionTrace.MaxHops, "Unbounded trace");
        hops.Clear();
        TraceOutcome unreachable = ConnectionTrace.Run(ttl => ConnectionTrace.Classify(ttl, target, IPStatus.DestinationHostUnreachable, null, 0),
            hops.Add, CancellationToken.None);
        Require(unreachable == TraceOutcome.Unreachable && hops.Count == 1 && !hops[0].RoundtripMs.HasValue, "Unreachable handling");
        using (CancellationTokenSource cancellation = new CancellationTokenSource())
        {
            int calls = 0;
            bool canceled = false;
            hops.Clear();
            try
            {
                ConnectionTrace.Run(delegate(int ttl)
                {
                    calls++;
                    cancellation.Cancel();
                    return ConnectionTrace.Classify(ttl, target, IPStatus.TimedOut, null, 1000);
                }, hops.Add, cancellation.Token);
            }
            catch (OperationCanceledException) { canceled = true; }
            Require(canceled && calls == 1 && hops.Count == 0, "Callback after cancellation");
        }
        foreach (IPAddress loopback in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
        {
            if (loopback.Equals(IPAddress.IPv6Loopback) && !System.Net.Sockets.Socket.OSSupportsIPv6) continue;
            hops.Clear();
            reached = ConnectionTrace.RunAsync(loopback, hops.Add, CancellationToken.None).GetAwaiter().GetResult();
            Require(reached == TraceOutcome.Reached && hops.Count == 1 && hops[0].Address == loopback.ToString(), "Real loopback " + loopback);
            Console.WriteLine("ICMP loopback OK: " + loopback);
        }
        Console.WriteLine("Connection trace checks passed.");
    }
}
