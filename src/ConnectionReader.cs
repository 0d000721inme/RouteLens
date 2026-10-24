using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace RouteLens
{
    public static class IpScope
    {
        public static bool IsPublic(string ip) { return Classify(ip) == "公网"; }

        public static string Classify(string ip)
        {
            if (String.IsNullOrWhiteSpace(ip)) return "无远端";
            IPAddress address;
            if (!IPAddress.TryParse(ip, out address)) return "保留地址";
            byte[] bytes = address.GetAddressBytes();
            if (bytes.Length == 16 && IsMapped(bytes))
            {
                bytes = new byte[] { bytes[12], bytes[13], bytes[14], bytes[15] };
                address = new IPAddress(bytes);
            }
            if (IPAddress.IsLoopback(address)) return "本机";
            if (bytes.Length == 4)
            {
                int a = bytes[0], b = bytes[1], c = bytes[2];
                if (a == 0 && b == 0 && c == 0 && bytes[3] == 0) return "无远端";
                if (a == 198 && (b == 18 || b == 19)) return "虚拟地址";
                if (a == 10 || (a == 172 && b >= 16 && b <= 31) ||
                    (a == 192 && b == 168) || (a == 169 && b == 254) ||
                    (a == 100 && b >= 64 && b <= 127)) return "局域网";
                if (a == 0 || a >= 224 ||
                    (a == 192 && b == 0 && (c == 0 || c == 2)) ||
                    (a == 192 && b == 88 && c == 99) ||
                    (a == 198 && b == 51 && c == 100) ||
                    (a == 203 && b == 0 && c == 113)) return "保留地址";
                return "公网";
            }
            bool allZero = true;
            for (int i = 0; i < bytes.Length; i++) if (bytes[i] != 0) { allZero = false; break; }
            if (allZero) return "无远端";
            if ((bytes[0] & 0xfe) == 0xfc ||
                (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80) ||
                (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0xc0)) return "局域网";
            // Geolocation is meaningful only for global unicast addresses. Exclude
            // documentation, benchmarking, ORCHID, and newer documentation prefixes.
            if ((bytes[0] & 0xe0) != 0x20 ||
                (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8) ||
                (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0 && bytes[3] == 2 && bytes[4] == 0 && bytes[5] == 0) ||
                (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0 && ((bytes[3] & 0xf0) == 0x10 || (bytes[3] & 0xf0) == 0x20)) ||
                (bytes[0] == 0x3f && bytes[1] == 0xff && (bytes[2] & 0xf0) == 0)) return "保留地址";
            return "公网";
        }

        private static bool IsMapped(byte[] bytes)
        {
            for (int i = 0; i < 10; i++) if (bytes[i] != 0) return false;
            return bytes[10] == 255 && bytes[11] == 255;
        }
    }

    public static class ConnectionReader
    {
        private const int AfInet = 2;
        private const int AfInet6 = 23;
        private const uint InsufficientBuffer = 122;

        [DllImport("iphlpapi.dll", SetLastError = false)]
        private static extern uint GetExtendedTcpTable(IntPtr table, ref int size,
            [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, uint reserved);

        [DllImport("iphlpapi.dll", SetLastError = false)]
        private static extern uint GetExtendedUdpTable(IntPtr table, ref int size,
            [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, uint reserved);

        [StructLayout(LayoutKind.Sequential)]
        private struct Tcp4
        {
            public uint State, LocalAddress, LocalPort, RemoteAddress, RemotePort, Pid;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct Tcp6
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] LocalAddress;
            public uint LocalScope, LocalPort;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] RemoteAddress;
            public uint RemoteScope, RemotePort, State, Pid;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct Udp4 { public uint LocalAddress, LocalPort, Pid; }
        [StructLayout(LayoutKind.Sequential)]
        private struct Udp6
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] LocalAddress;
            public uint LocalScope, LocalPort, Pid;
        }

        public static NetworkSnapshot Read()
        {
            List<ConnectionInfo> connections = new List<ConnectionInfo>();
            List<string> errors = new List<string>();
            ReadFamily(true, AfInet, connections, errors);
            ReadFamily(true, AfInet6, connections, errors);
            ReadFamily(false, AfInet, connections, errors);
            ReadFamily(false, AfInet6, connections, errors);
            Dictionary<int, string> names = ReadProcessNames();
            foreach (ConnectionInfo connection in connections)
            {
                string name;
                connection.ProcessName = connection.ProcessId == 0 ? "已结束 / 无归属" :
                    (names.TryGetValue(connection.ProcessId, out name) ? name : "PID " + connection.ProcessId);
            }
            return new NetworkSnapshot
            {
                Connections = connections,
                CapturedAt = DateTime.Now,
                Error = String.Join("；", errors.ToArray()),
                ProxySummary = GetProxySummary()
            };
        }

        private static void ReadFamily(bool tcp, int family, List<ConnectionInfo> result, List<string> errors)
        {
            string label = (tcp ? "TCP" : "UDP") + (family == AfInet ? " IPv4" : " IPv6");
            IntPtr buffer = IntPtr.Zero;
            try
            {
                int size = 0;
                uint status = GetTable(tcp, IntPtr.Zero, ref size, family);
                if (status != 0 && status != InsufficientBuffer)
                {
                    errors.Add(label + "：" + NativeError(status));
                    return;
                }
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    if (size < 4) size = 4;
                    if (size > 128 * 1024 * 1024) throw new InvalidOperationException("连接表大小超出安全范围");
                    int allocated = size;
                    buffer = Marshal.AllocHGlobal(allocated);
                    status = GetTable(tcp, buffer, ref size, family);
                    if (status == InsufficientBuffer)
                    {
                        Marshal.FreeHGlobal(buffer);
                        buffer = IntPtr.Zero;
                        continue;
                    }
                    if (status != 0)
                    {
                        errors.Add(label + "：" + NativeError(status));
                        return;
                    }
                    int rowSize = tcp ? (family == AfInet ? Marshal.SizeOf(typeof(Tcp4)) : Marshal.SizeOf(typeof(Tcp6)))
                        : (family == AfInet ? Marshal.SizeOf(typeof(Udp4)) : Marshal.SizeOf(typeof(Udp6)));
                    int count = Marshal.ReadInt32(buffer);
                    if (count < 0 || count > (allocated - 4) / rowSize)
                        throw new InvalidOperationException("连接表在读取期间变化，请等待下一次刷新");
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr row = IntPtr.Add(buffer, 4 + i * rowSize);
                        ConnectionInfo connection = ParseRow(tcp, family, row);
                        if (connection != null) result.Add(connection);
                    }
                    return;
                }
                errors.Add(label + "：连接变化频繁，本轮未能读取；下次刷新会重试");
            }
            catch (Exception ex)
            {
                errors.Add(label + "：" + ex.Message);
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            }
        }

        private static uint GetTable(bool tcp, IntPtr buffer, ref int size, int family)
        {
            // TCP_TABLE_OWNER_PID_ALL = 5; UDP_TABLE_OWNER_PID = 1.
            return tcp ? GetExtendedTcpTable(buffer, ref size, false, family, 5, 0)
                : GetExtendedUdpTable(buffer, ref size, false, family, 1, 0);
        }

        private static ConnectionInfo ParseRow(bool tcp, int family, IntPtr pointer)
        {
            ConnectionInfo row = new ConnectionInfo();
            row.Protocol = tcp ? "TCP" : "UDP";
            uint state = 0;
            if (tcp && family == AfInet)
            {
                Tcp4 native = (Tcp4)Marshal.PtrToStructure(pointer, typeof(Tcp4));
                state = native.State;
                if (state == 2) return null;
                row.LocalAddress = new IPAddress(BitConverter.GetBytes(native.LocalAddress)).ToString();
                row.LocalPort = Port(native.LocalPort);
                row.RemoteAddress = new IPAddress(BitConverter.GetBytes(native.RemoteAddress)).ToString();
                row.RemotePort = Port(native.RemotePort);
                row.ProcessId = unchecked((int)native.Pid);
            }
            else if (tcp)
            {
                Tcp6 native = (Tcp6)Marshal.PtrToStructure(pointer, typeof(Tcp6));
                state = native.State;
                if (state == 2) return null;
                // Match Microsoft's SystemTcpConnectionInformation implementation:
                // owner-PID table scope IDs are passed directly to IPAddress.
                row.LocalAddress = new IPAddress(native.LocalAddress, native.LocalScope).ToString();
                row.LocalPort = Port(native.LocalPort);
                row.RemoteAddress = new IPAddress(native.RemoteAddress, native.RemoteScope).ToString();
                row.RemotePort = Port(native.RemotePort);
                row.ProcessId = unchecked((int)native.Pid);
            }
            else if (family == AfInet)
            {
                Udp4 native = (Udp4)Marshal.PtrToStructure(pointer, typeof(Udp4));
                row.LocalAddress = new IPAddress(BitConverter.GetBytes(native.LocalAddress)).ToString();
                row.LocalPort = Port(native.LocalPort);
                row.ProcessId = unchecked((int)native.Pid);
            }
            else
            {
                Udp6 native = (Udp6)Marshal.PtrToStructure(pointer, typeof(Udp6));
                row.LocalAddress = new IPAddress(native.LocalAddress, native.LocalScope).ToString();
                row.LocalPort = Port(native.LocalPort);
                row.ProcessId = unchecked((int)native.Pid);
            }
            if (tcp && IpScope.Classify(row.RemoteAddress) == "无远端") return null;
            row.RemoteAddress = row.RemoteAddress ?? "";
            row.State = tcp ? StateName(state) : "本地端点";
            row.Scope = tcp ? IpScope.Classify(row.RemoteAddress) : "无远端";
            row.Id = row.Protocol + "|" + row.ProcessId + "|" + row.LocalAddress + "|" + row.LocalPort
                + "|" + row.RemoteAddress + "|" + row.RemotePort;
            return row;
        }

        private static int Port(uint value) { return (int)(((value & 255) << 8) | ((value >> 8) & 255)); }

        private static string StateName(uint state)
        {
            switch (state)
            {
                case 1: return "CLOSED";
                case 2: return "LISTEN";
                case 3: return "SYN_SENT";
                case 4: return "SYN_RECEIVED";
                case 5: return "ESTABLISHED";
                case 6: return "FIN_WAIT_1";
                case 7: return "FIN_WAIT_2";
                case 8: return "CLOSE_WAIT";
                case 9: return "CLOSING";
                case 10: return "LAST_ACK";
                case 11: return "TIME_WAIT";
                case 12: return "DELETE_TCB";
                default: return "STATE_" + state;
            }
        }

        private static Dictionary<int, string> ReadProcessNames()
        {
            Dictionary<int, string> result = new Dictionary<int, string>();
            try
            {
                Process[] processes = Process.GetProcesses();
                foreach (Process process in processes)
                {
                    try { result[process.Id] = process.ProcessName; }
                    catch (InvalidOperationException) { }
                    catch (Win32Exception) { }
                    finally { process.Dispose(); }
                }
            }
            catch (Win32Exception) { }
            catch (InvalidOperationException) { }
            return result;
        }

        private static string NativeError(uint status)
        {
            return new Win32Exception(unchecked((int)status)).Message + "（" + status + "）";
        }

        public static string GetProxySummary()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
                {
                    if (key == null) return "系统代理：未设置";
                    bool enabled = Convert.ToInt32(key.GetValue("ProxyEnable", 0)) != 0;
                    string server = Convert.ToString(key.GetValue("ProxyServer", ""));
                    string pac = Convert.ToString(key.GetValue("AutoConfigURL", ""));
                    string summary = enabled ? "系统代理：已开启" : "系统代理：未开启手动代理";
                    if (enabled && !String.IsNullOrWhiteSpace(server)) summary += " · " + SafeProxyAddress(server);
                    if (!String.IsNullOrWhiteSpace(pac)) summary += " · PAC 已配置";
                    return summary;
                }
            }
            catch (Exception) { return "系统代理：无法读取"; }
        }

        private static string SafeProxyAddress(string value)
        {
            // Parse addresses instead of displaying raw registry values: credentials,
            // paths, query strings, and fragments must never reach the UI.
            List<string> hosts = new List<string>();
            foreach (string part in value.Split(';'))
            {
                string address = part.Trim();
                string protocol = "";
                int equals = address.IndexOf('=');
                if (equals >= 0)
                {
                    string label = address.Substring(0, equals).ToLowerInvariant();
                    if (label == "http" || label == "https" || label == "ftp" || label == "socks")
                    {
                        protocol = label + "=";
                        address = address.Substring(equals + 1);
                    }
                }
                int scheme = address.IndexOf("://", StringComparison.Ordinal);
                if (scheme >= 0) address = address.Substring(scheme + 3);
                int userInfo = address.LastIndexOf('@');
                int suffix = address.IndexOfAny(new char[] { '/', '?', '#' });
                if (userInfo >= 0 && suffix >= 0 && userInfo > suffix) continue;
                Uri parsed;
                if (Uri.TryCreate("http://" + address, UriKind.Absolute, out parsed) && !String.IsNullOrEmpty(parsed.Host))
                    hosts.Add(protocol + parsed.Host + (parsed.IsDefaultPort ? "" : ":" + parsed.Port));
                if (hosts.Count >= 6) break;
            }
            string safe = String.Join("; ", hosts.ToArray());
            if (safe.Length == 0) return "地址已隐藏";
            return safe.Length > 180 ? safe.Substring(0, 180) + "…" : safe;
        }
    }
}
