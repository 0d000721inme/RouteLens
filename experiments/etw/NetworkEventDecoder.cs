using System;
using System.Net;

namespace RouteLens.EtwExperiment
{
    // An ETW network event, not a packet or a physical-wire byte counter.
    public sealed class NetworkObservation
    {
        public DateTime TimeUtc;
        public string Protocol;
        public bool IsSend;
        public uint ProcessId;
        public uint EventSize;
        public IPAddress SourceAddress, DestinationAddress;
        public int SourcePort, DestinationPort;
    }

    // Kernel classic TCP/IP and UDP/IP version 2 send/receive payload prefix.
    // Explicitly reject unfamiliar versions/opcodes, never guess fields.
    public static class NetworkEventDecoder
    {
        public static readonly Guid Tcp = new Guid("9a280ac0-c8e0-11d1-84e2-00c04fb998a2");
        public static readonly Guid Udp = new Guid("bf3a50c5-a9c9-4988-a005-2df0b7c80f80");

        public static bool TryDecode(Guid provider, byte version, byte opcode, DateTime timeUtc, byte[] data, out NetworkObservation result)
        {
            result = null;
            if ((provider != Tcp && provider != Udp) || version != 2 || data == null) return false;
            bool v6 = opcode == 26 || opcode == 27;
            if (!v6 && opcode != 10 && opcode != 11) return false;
            int addressLength = v6 ? 16 : 4;
            int portOffset = 8 + 2 * addressLength;
            if (data.Length < portOffset + 4) return false;
            byte[] destination = new byte[addressLength], source = new byte[addressLength];
            Buffer.BlockCopy(data, 8, destination, 0, addressLength);
            Buffer.BlockCopy(data, 8 + addressLength, source, 0, addressLength);
            result = new NetworkObservation {
                TimeUtc = timeUtc, Protocol = provider == Tcp ? "TCP" : "UDP", IsSend = opcode == 10 || opcode == 26,
                ProcessId = ReadUInt32(data, 0), EventSize = ReadUInt32(data, 4),
                SourceAddress = new IPAddress(source), DestinationAddress = new IPAddress(destination),
                DestinationPort = (data[portOffset] << 8) | data[portOffset + 1],
                SourcePort = (data[portOffset + 2] << 8) | data[portOffset + 3]
            };
            return true;
        }
        private static uint ReadUInt32(byte[] value, int offset)
        { return (uint)(value[offset] | value[offset + 1] << 8 | value[offset + 2] << 16 | value[offset + 3] << 24); }
    }
}
