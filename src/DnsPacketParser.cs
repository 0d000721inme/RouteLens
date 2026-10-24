using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RouteLens
{
    // Parses IP packets returned by an IPv4 raw socket; Ethernet headers are not accepted.
    // Only complete, unfragmented plaintext DNS queries are evidence. TCP stream
    // reassembly, IPv6, DoH and DoT are deliberately outside this parser's scope.
    public static class DnsPacketParser
    {
        public static bool TryParseIPv4(byte[] buffer, int count, IPAddress localSource,
            out DnsPacketEvidence evidence)
        {
            evidence = null;
            if (buffer == null || count < 20 || count > buffer.Length || localSource == null ||
                localSource.AddressFamily != AddressFamily.InterNetwork) return false;

            int headerLength = (buffer[0] & 15) * 4;
            if ((buffer[0] >> 4) != 4 || headerLength < 20 || headerLength > count) return false;
            int packetLength = U16(buffer, 2);
            if (packetLength < headerLength || packetLength > count) return false;
            // Permit DF, but reject the reserved flag, MF, and every fragment offset.
            if ((U16(buffer, 6) & 0xbfff) != 0) return false;

            byte[] local = localSource.GetAddressBytes();
            if (!IsUnicast(local, 0) || !SameAddress(buffer, 12, local, 0) ||
                !IsUnicast(buffer, 16) || SameAddress(buffer, 12, buffer, 16)) return false;

            int payloadLength = packetLength - headerLength;
            int sourcePort;
            int resolverPort;
            int dnsOffset;
            int dnsLength;
            string transport;
            if (buffer[9] == 17)
            {
                if (payloadLength < 20) return false;
                sourcePort = U16(buffer, headerLength);
                resolverPort = U16(buffer, headerLength + 2);
                int udpLength = U16(buffer, headerLength + 4);
                if (udpLength != payloadLength || udpLength < 20) return false;
                dnsOffset = headerLength + 8;
                dnsLength = udpLength - 8;
                transport = "UDP";
            }
            else if (buffer[9] == 6)
            {
                if (payloadLength < 34) return false;
                sourcePort = U16(buffer, headerLength);
                resolverPort = U16(buffer, headerLength + 2);
                int tcpLength = (buffer[headerLength + 12] >> 4) * 4;
                if (tcpLength < 20 || tcpLength > payloadLength - 14) return false;
                // A reset segment cannot carry an actionable DNS request.
                if ((buffer[headerLength + 13] & 4) != 0) return false;
                dnsOffset = headerLength + tcpLength + 2;
                dnsLength = U16(buffer, dnsOffset - 2);
                // Accept exactly one whole length-prefixed message in this segment.
                if (dnsLength < 12 || dnsLength != payloadLength - tcpLength - 2) return false;
                transport = "TCP";
            }
            else return false;

            if (sourcePort == 0 || resolverPort != 53) return false;
            string queryName;
            if (!TryReadQuery(buffer, dnsOffset, dnsLength, out queryName)) return false;
            evidence = new DnsPacketEvidence
            {
                SourceAddress = localSource.ToString(),
                ResolverAddress = new IPAddress(new byte[]
                    { buffer[16], buffer[17], buffer[18], buffer[19] }).ToString(),
                SourcePort = sourcePort,
                ResolverPort = resolverPort,
                Transport = transport,
                QueryName = queryName
            };
            return true;
        }

        private static bool TryReadQuery(byte[] bytes, int start, int length, out string queryName)
        {
            queryName = null;
            if (length < 12) return false;
            int flags = U16(bytes, start + 2);
            // QR=0, OPCODE=QUERY, and the reserved Z bit must be clear.
            if ((flags & 0xf840) != 0) return false;
            int questions = U16(bytes, start + 4);
            if (questions == 0 || questions > (length - 12) / 5) return false;
            int offset = 12;
            for (int i = 0; i < questions; i++)
            {
                string name;
                if (!TryReadName(bytes, start, length, ref offset, out name) || offset > length - 4)
                    return false;
                if (U16(bytes, start + offset) == 0 || U16(bytes, start + offset + 2) == 0)
                    return false;
                offset += 4;
                if (i == 0) queryName = name;
            }

            // Validate record envelopes as well, including the common EDNS OPT record.
            // RDATA stays opaque: its schema depends on its record type.
            int records = U16(bytes, start + 6) + U16(bytes, start + 8) + U16(bytes, start + 10);
            if (records > (length - offset) / 11) return false;
            for (int i = 0; i < records; i++)
            {
                string ignored;
                if (!TryReadName(bytes, start, length, ref offset, out ignored) || offset > length - 10)
                    return false;
                int dataLength = U16(bytes, start + offset + 8);
                offset += 10;
                if (dataLength > length - offset) return false;
                offset += dataLength;
            }
            return offset == length;
        }

        private static bool TryReadName(byte[] bytes, int start, int length, ref int offset,
            out string name)
        {
            name = null;
            int cursor = offset;
            int wireLength = 1; // Include the terminating root label.
            bool jumped = false;
            var visited = new HashSet<int>();
            var text = new StringBuilder();
            for (int steps = 0; steps < 128; steps++)
            {
                if (cursor < 12 || cursor >= length || !visited.Add(cursor)) return false;
                int labelLength = bytes[start + cursor];
                if (labelLength == 0)
                {
                    if (!jumped) offset = cursor + 1;
                    name = text.Length == 0 ? "." : text.ToString();
                    return true;
                }
                if ((labelLength & 0xc0) == 0xc0)
                {
                    if (cursor >= length - 1) return false;
                    int target = ((labelLength & 0x3f) << 8) | bytes[start + cursor + 1];
                    // DNS compression refers backward to an earlier name, never the header.
                    if (target < 12 || target >= cursor) return false;
                    if (!jumped) offset = cursor + 2;
                    jumped = true;
                    cursor = target;
                    continue;
                }
                // Extended/reserved label encodings are not accepted as ordinary DNS names.
                if ((labelLength & 0xc0) != 0 || labelLength > length - cursor - 1) return false;
                wireLength += labelLength + 1;
                if (wireLength > 255) return false;
                if (text.Length != 0) text.Append('.');
                for (int i = 0; i < labelLength; i++)
                {
                    byte c = bytes[start + cursor + 1 + i];
                    // Keep UI output inert. Unusual binary/escaped DNS labels are unsupported.
                    if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                        (c >= '0' && c <= '9') || c == '-' || c == '_')) return false;
                    text.Append((char)c);
                }
                cursor += labelLength + 1;
                if (!jumped) offset = cursor;
            }
            return false;
        }

        private static int U16(byte[] bytes, int offset)
        {
            return (bytes[offset] << 8) | bytes[offset + 1];
        }

        private static bool IsUnicast(byte[] address, int offset)
        {
            // Also excludes unspecified, loopback, multicast, and limited broadcast.
            return address[offset] != 0 && address[offset] != 127 && address[offset] < 224;
        }

        private static bool SameAddress(byte[] left, int leftOffset, byte[] right, int rightOffset)
        {
            for (int i = 0; i < 4; i++)
                if (left[leftOffset + i] != right[rightOffset + i]) return false;
            return true;
        }
    }
}
