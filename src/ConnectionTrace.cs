using System;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace RouteLens
{
    internal enum TraceOutcome { Reached, Unreachable, HopLimit }

    internal sealed class TraceHop
    {
        public readonly int Number;
        public readonly string Address;
        public readonly long? RoundtripMs;
        public readonly string StatusText;
        internal readonly TraceOutcome? Outcome;

        internal TraceHop(int number, string address, long? roundtripMs, string status, TraceOutcome? outcome)
        {
            Number = number;
            Address = address;
            RoundtripMs = roundtripMs;
            StatusText = status;
            Outcome = outcome;
        }
    }

    internal static class ConnectionTrace
    {
        public const int MaxHops = 30;
        public const int TimeoutMs = 1000;

        public static bool TryGetTarget(string remoteAddress, out IPAddress target)
        {
            target = null;
            IPAddress parsed;
            string value = (remoteAddress ?? "").Trim();
            if (!IPAddress.TryParse(value, out parsed)) return false;
            // The connection readers emit dotted IPv4. Reject alternate numeric forms such as "1".
            if (parsed.AddressFamily == AddressFamily.InterNetwork && value != parsed.ToString()) return false;
            if (parsed.IsIPv4MappedToIPv6) parsed = parsed.MapToIPv4();
            if (!IsUnicast(parsed)) return false;
            target = parsed;
            return true;
        }

        private static bool IsUnicast(IPAddress address)
        {
            if (address == null) return false;
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
                return !address.IsIPv6Multicast && !address.Equals(IPAddress.IPv6Any);
            byte firstOctet = address.GetAddressBytes()[0];
            return address.AddressFamily == AddressFamily.InterNetwork && firstOctet != 0 && firstOctet < 224;
        }

        public static Task<TraceOutcome> RunAsync(IPAddress target, Action<TraceHop> onHop, CancellationToken token)
        {
            if (target != null && target.IsIPv4MappedToIPv6) target = target.MapToIPv4();
            if (!IsUnicast(target)) throw new ArgumentException("请选择有效的单播 IP 地址。", "target");
            if (onHop == null) throw new ArgumentNullException("onHop");
            return Task.Factory.StartNew(delegate
            {
                using (Ping ping = new Ping())
                {
                    byte[] payload = new byte[32];
                    return Run(delegate(int ttl)
                    {
                        // Adapted from TraceMe IcmpProber.Send (MIT). Windows does not populate
                        // PingReply.RoundtripTime for TTL-expired replies on .NET Framework.
                        PingOptions options = new PingOptions(ttl, false);
                        Stopwatch timer = Stopwatch.StartNew();
                        PingReply reply = ping.Send(target, TimeoutMs, payload, options);
                        timer.Stop();
                        return Classify(ttl, target, reply.Status, reply.Address, timer.ElapsedMilliseconds);
                    }, onHop, token);
                }
            }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        internal static TraceOutcome Run(Func<int, TraceHop> probe, Action<TraceHop> onHop, CancellationToken token)
        {
            for (int ttl = 1; ttl <= MaxHops; ttl++)
            {
                token.ThrowIfCancellationRequested();
                TraceHop hop = probe(ttl);
                token.ThrowIfCancellationRequested();
                onHop(hop);
                if (hop.Outcome.HasValue) return hop.Outcome.Value;
            }
            return TraceOutcome.HopLimit;
        }

        internal static TraceHop Classify(int ttl, IPAddress target, IPStatus status, IPAddress responder, long elapsedMs)
        {
            string address = responder == null || responder.Equals(IPAddress.Any) || responder.Equals(IPAddress.IPv6Any)
                ? "" : responder.ToString();
            switch (status)
            {
                case IPStatus.Success:
                    return new TraceHop(ttl, address.Length == 0 ? target.ToString() : address, elapsedMs, "目标已响应", TraceOutcome.Reached);
                case IPStatus.TimeExceeded:
                case IPStatus.TtlExpired:
                case IPStatus.TtlReassemblyTimeExceeded:
                    return new TraceHop(ttl, address, elapsedMs, "生存时间已到", null);
                case IPStatus.TimedOut:
                    return new TraceHop(ttl, "", null, "等待超时", null);
                case IPStatus.DestinationUnreachable:
                case IPStatus.DestinationHostUnreachable:
                case IPStatus.DestinationNetworkUnreachable:
                case IPStatus.DestinationPortUnreachable:
                case IPStatus.DestinationProtocolUnreachable: // Also covers the DestinationProhibited alias.
                case IPStatus.BadRoute:
                case IPStatus.PacketTooBig:
                    return new TraceHop(ttl, address, address.Length == 0 ? (long?)null : elapsedMs,
                        "无法到达目标（" + status + "）", TraceOutcome.Unreachable);
                default:
                    throw new InvalidOperationException("系统返回 ICMP 探测错误（" + status + "）。");
            }
        }
    }
}
