using System;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using RouteLens.EtwExperiment;

internal static class EtwExperimentChecks
{
    private static int checks;
    private static void Check(bool condition, string name) { checks++; if (!condition) throw new Exception(name); }
    private static byte[] Payload(string src, string dst, int sport, int dport, uint pid, uint size)
    {
        byte[] a = IPAddress.Parse(dst).GetAddressBytes(), b = IPAddress.Parse(src).GetAddressBytes();
        byte[] data = new byte[8 + a.Length + b.Length + 4];
        Buffer.BlockCopy(BitConverter.GetBytes(pid), 0, data, 0, 4); Buffer.BlockCopy(BitConverter.GetBytes(size), 0, data, 4, 4);
        Buffer.BlockCopy(a, 0, data, 8, a.Length); Buffer.BlockCopy(b, 0, data, 8 + a.Length, b.Length);
        int at = 8 + a.Length + b.Length;
        data[at] = (byte)(dport >> 8); data[at + 1] = (byte)dport; data[at + 2] = (byte)(sport >> 8); data[at + 3] = (byte)sport;
        return data;
    }
    public static int Main()
    {
        NetworkObservation x; DateTime time = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        byte[] v4 = Payload("192.0.2.1", "198.51.100.8", 50000, 443, 4242, 1200);
        byte[] v6 = Payload("2001:db8::1", "2001:db8::2", 50001, 53, 71, 512);
        Check(NetworkEventDecoder.TryDecode(NetworkEventDecoder.Tcp, 2, 10, time, v4, out x), "TCP4 send accepted");
        Check(x.Protocol == "TCP" && x.IsSend && x.ProcessId == 4242 && x.EventSize == 1200 && x.TimeUtc == time, "TCP4 fields");
        Check(x.SourceAddress.ToString() == "192.0.2.1" && x.DestinationAddress.ToString() == "198.51.100.8", "IPv4 address order");
        Check(x.SourcePort == 50000 && x.DestinationPort == 443, "network-order IPv4 ports");
        Check(NetworkEventDecoder.TryDecode(NetworkEventDecoder.Udp, 2, 11, time, v4, out x) && !x.IsSend && x.Protocol == "UDP", "UDP4 receive");
        Check(NetworkEventDecoder.TryDecode(NetworkEventDecoder.Tcp, 2, 26, time, v6, out x) && x.IsSend, "TCP6 send");
        Check(x.SourceAddress.ToString() == "2001:db8::1" && x.DestinationAddress.ToString() == "2001:db8::2", "IPv6 address order");
        Check(x.SourcePort == 50001 && x.DestinationPort == 53 && x.ProcessId == 71 && x.EventSize == 512, "IPv6 fields");
        Check(NetworkEventDecoder.TryDecode(NetworkEventDecoder.Udp, 2, 27, time, v6, out x) && !x.IsSend, "UDP6 receive");
        Check(!NetworkEventDecoder.TryDecode(Guid.Empty, 2, 10, time, v4, out x) && x == null, "unknown provider rejected");
        Check(!NetworkEventDecoder.TryDecode(NetworkEventDecoder.Tcp, 1, 10, time, v4, out x), "unknown version rejected");
        Check(!NetworkEventDecoder.TryDecode(NetworkEventDecoder.Udp, 2, 12, time, v4, out x), "other opcode rejected");
        Check(!NetworkEventDecoder.TryDecode(NetworkEventDecoder.Tcp, 2, 10, time, null, out x), "null rejected");
        Check(!NetworkEventDecoder.TryDecode(NetworkEventDecoder.Tcp, 2, 10, time, new byte[19], out x), "short IPv4 rejected");
        Check(!NetworkEventDecoder.TryDecode(NetworkEventDecoder.Tcp, 2, 26, time, new byte[43], out x), "short IPv6 rejected");
        Type monitor = typeof(EtwExperiment);
        Type props = monitor.GetNestedType("TraceProperties", BindingFlags.NonPublic);
        Type header = monitor.GetNestedType("EventHeader", BindingFlags.NonPublic);
        Type record = monitor.GetNestedType("EventRecord", BindingFlags.NonPublic);
        Check(Marshal.SizeOf(header) == 80, "event header ABI");
        Check(Marshal.SizeOf(record) == (IntPtr.Size == 8 ? 112 : 100), "event record ABI");
        Check(Marshal.SizeOf(props) == (IntPtr.Size == 8 ? 120 : 112), "trace properties ABI");
        using (EtwExperiment trace = new EtwExperiment())
        {
            Check(!trace.IsRunning && trace.Snapshot().Length == 0 && !trace.Health().Running, "construction does not start capture");
        }
        Console.WriteLine("PASS " + checks + " ETW experiment decoder/ABI checks; synthetic data, no capture started.");
        return 0;
    }
}
