using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using System.Threading;
using RouteLens.EtwExperiment;

internal static class LiveLoopbackCheck
{
    private static void Transfer(IPAddress address, bool udp)
    {
        AddressFamily family = address.AddressFamily;
        using (Socket server = new Socket(family, udp ? SocketType.Dgram : SocketType.Stream, udp ? ProtocolType.Udp : ProtocolType.Tcp))
        using (Socket client = new Socket(family, udp ? SocketType.Dgram : SocketType.Stream, udp ? ProtocolType.Udp : ProtocolType.Tcp))
        {
            server.ReceiveTimeout = 3000; client.SendTimeout = 3000;
            server.Bind(new IPEndPoint(address, 0));
            if (!udp) server.Listen(1);
            client.Connect(server.LocalEndPoint);
            using (Socket peer = udp ? null : server.Accept())
            {
                Socket receiver = udp ? server : peer; receiver.ReceiveTimeout = 3000;
                byte[] send = new byte[256], receive = new byte[512];
                client.Send(send); if (receiver.Receive(receive) != send.Length) throw new Exception("Loopback transfer incomplete.");
            }
        }
    }
    public static int Main(string[] args)
    {
        if (args.Length != 1 || args[0] != "--execute") { Console.Error.WriteLine("Explicit --execute required; starts a system ETW session for loopback test."); return 2; }
        using (WindowsIdentity current = WindowsIdentity.GetCurrent())
            if (!new WindowsPrincipal(current).IsInRole(WindowsBuiltInRole.Administrator)) { Console.Error.WriteLine("Administrator token required for this experiment."); return 2; }
        int pid = Process.GetCurrentProcess().Id;
        using (EtwExperiment trace = new EtwExperiment())
        {
            uint status = trace.Start();
            if (status != 0) { Console.Error.WriteLine("Trace could not start; Windows error " + status + ". Existing sessions are untouched."); return 1; }
            Transfer(IPAddress.Loopback, false); Transfer(IPAddress.Loopback, true);
            if (Socket.OSSupportsIPv6) { Transfer(IPAddress.IPv6Loopback, false); Transfer(IPAddress.IPv6Loopback, true); }
            Thread.Sleep(3000);
            NetworkObservation[] own = trace.Snapshot().Where(x => x.ProcessId == (uint)pid).ToArray();
            bool tcp4 = own.Any(x => x.Protocol == "TCP" && x.SourceAddress.AddressFamily == AddressFamily.InterNetwork);
            bool udp4 = own.Any(x => x.Protocol == "UDP" && x.SourceAddress.AddressFamily == AddressFamily.InterNetwork);
            bool tcp6 = own.Any(x => x.Protocol == "TCP" && x.SourceAddress.AddressFamily == AddressFamily.InterNetworkV6);
            bool udp6 = own.Any(x => x.Protocol == "UDP" && x.SourceAddress.AddressFamily == AddressFamily.InterNetworkV6);
            ObservationHealth health = trace.Health();
            Console.WriteLine("Own loopback event coverage: TCP4={0}, UDP4={1}, TCP6={2}, UDP6={3}; IPv6 available={4}.", tcp4, udp4, tcp6, udp6, Socket.OSSupportsIPv6);
            Console.WriteLine("Health: ETW events lost={0}, realtime buffers lost={1}, rejected={2}, evicted={3}.", health.EventsLost, health.RealTimeBuffersLost, health.RejectedEvents, health.EvictedObservations);
            if (!tcp4 || !udp4 || (Socket.OSSupportsIPv6 && (!tcp6 || !udp6)) || health.EventsLost != 0 || health.RealTimeBuffersLost != 0 || health.RejectedEvents != 0 || health.EvictedObservations != 0) return 1;
        }
        Console.WriteLine("PASS controlled loopback protocol smoke test; no external traffic generated. This does not validate byte accuracy or PID reuse.");
        return 0;
    }
}
