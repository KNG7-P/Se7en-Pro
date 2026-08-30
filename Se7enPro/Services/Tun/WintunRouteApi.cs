using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Se7enPro.Services;

[SupportedOSPlatform("windows")]
internal static class WintunRouteApi
{
    private const ushort AfInet = 2;
    private const ushort AfInet6 = 23;
    private const uint NoError = 0;
    private const uint ErrorObjectAlreadyExists = 5010;
    private const uint ErrorNotFound = 1168;
    private const byte IpDadStatePreferred = 4;
    private const uint Infinite32 = 0xFFFFFFFF;
    private const long Infinite64 = unchecked((long)0xFFFFFFFFFFFFFFFF);

    [StructLayout(LayoutKind.Sequential, Pack = 4, Size = 28)]
    internal struct SockaddrInet
    {
        public ushort Family;
        public ushort Port;
        public ulong Low;
        public ulong High;
        public uint ScopeId;

        public static SockaddrInet FromIp(IPAddress ip)
        {
            var s = new SockaddrInet();
            var b = ip.GetAddressBytes();
            if (b.Length == 4)
            {
                s.Family = AfInet;
                s.Low = (ulong)b[0] | (ulong)b[1] << 8 | (ulong)b[2] << 16 | (ulong)b[3] << 24;
            }
            else
            {
                s.Family = AfInet6;
                for (var i = 0; i < 8; i++) s.Low |= (ulong)b[i] << (8 * i);
                for (var i = 8; i < 16; i++) s.High |= (ulong)b[i] << (8 * (i - 8));
            }
            return s;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IpAddressPrefix
    {
        public SockaddrInet Prefix;
        public byte PrefixLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MibUnicastIpAddressRow
    {
        public SockaddrInet Address;
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public long ValidLifetime;
        public long PreferredLifetime;
        public byte OnLinkPrefixLength;
        public byte SkipAsSource;
        public byte DadState;
        public uint ZoneScope;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MibIpforwardRow2
    {
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public IpAddressPrefix DestinationPrefix;
        public SockaddrInet NextHop;
        public uint SitePrefixLength;
        public uint ValidLifetime;
        public uint PreferredLifetime;
        public uint Metric;
        public uint Protocol;
        public byte Loopback;
        public byte AutoconfigureStarted;
        public byte Publish;
        public byte Immortal;
        public uint Age;
        public uint Origin;
        public ulong Reserved;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint CreateUnicastIpAddressEntry(ref MibUnicastIpAddressRow row);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint DeleteUnicastIpAddressEntry(ref MibUnicastIpAddressRow row);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint CreateIpForwardEntry2(ref MibIpforwardRow2 row);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint DeleteIpForwardEntry2(ref MibIpforwardRow2 row);

    [DllImport("dnsapi.dll", SetLastError = false)]
    private static extern bool DnsFlushResolverCache();

    public static NetworkInterface? FindAdapter(string name)
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase));
        }
        catch { return null; }
    }

    public static bool IsAdapterUp(string name) =>
        FindAdapter(name) is { OperationalStatus: OperationalStatus.Up };

    public static int GetAdapterIndex(NetworkInterface nic) =>
        nic.GetIPProperties().GetIPv4Properties()?.Index
        ?? nic.GetIPProperties().GetIPv6Properties()?.Index
        ?? 0;

    public static void SetAdapterIpAddress(int ifIndex, IPAddress ip, byte prefixLen)
    {
        var row = new MibUnicastIpAddressRow
        {
            Address = SockaddrInet.FromIp(ip),
            InterfaceLuid = 0,
            InterfaceIndex = (uint)ifIndex,
            ValidLifetime = Infinite64,
            PreferredLifetime = Infinite64,
            OnLinkPrefixLength = prefixLen,
            SkipAsSource = 0,
            DadState = IpDadStatePreferred,
            ZoneScope = 0,
        };
        var rc = CreateUnicastIpAddressEntry(ref row);
        if (rc != NoError && rc != ErrorObjectAlreadyExists)
        {
            throw new InvalidOperationException(
                $"CreateUnicastIpAddressEntry({ip}/{prefixLen}) failed with Win32 error {rc}");
        }
    }

    public sealed record RouteEntry(int IfIndex, IPAddress Destination, byte Prefix, IPAddress NextHop, uint Metric);

    public static RouteEntry AddRoute(
        int ifIndex, IPAddress destination, byte prefix, IPAddress nextHop, uint metric = 0)
    {
        var row = BuildRouteRow(ifIndex, destination, prefix, nextHop, metric);
        var rc = CreateIpForwardEntry2(ref row);
        if (rc != NoError && rc != ErrorObjectAlreadyExists)
        {
            throw new InvalidOperationException(
                $"CreateIpForwardEntry2({destination}/{prefix} via {nextHop}) failed with Win32 error {rc}");
        }
        return new RouteEntry(ifIndex, destination, prefix, nextHop, metric);
    }

    public static void DeleteRoute(RouteEntry entry)
    {
        var row = BuildRouteRow(entry.IfIndex, entry.Destination, entry.Prefix, entry.NextHop, entry.Metric);
        var rc = DeleteIpForwardEntry2(ref row);
        if (rc != NoError && rc != ErrorNotFound)
        {
            throw new InvalidOperationException(
                $"DeleteIpForwardEntry2({entry.Destination}/{entry.Prefix}) failed with Win32 error {rc}");
        }
    }

    private static MibIpforwardRow2 BuildRouteRow(
        int ifIndex, IPAddress destination, byte prefix, IPAddress nextHop, uint metric)
    {
        return new MibIpforwardRow2
        {
            InterfaceLuid = 0,
            InterfaceIndex = (uint)ifIndex,
            DestinationPrefix = new IpAddressPrefix
            {
                Prefix = SockaddrInet.FromIp(NormalizePrefix(destination, prefix)),
                PrefixLength = prefix,
            },
            NextHop = SockaddrInet.FromIp(nextHop),
            SitePrefixLength = 0,
            ValidLifetime = Infinite32,
            PreferredLifetime = Infinite32,
            Metric = metric,
            Protocol = 3,
        };
    }

    public static IPAddress NormalizePrefix(IPAddress destination, byte prefix)
    {
        var bytes = destination.GetAddressBytes();
        if (bytes.Length is not (4 or 16) || prefix > bytes.Length * 8) return destination;

        var full = prefix / 8;
        var rem = prefix % 8;
        for (var i = 0; i < bytes.Length; i++)
        {
            if (i < full) continue;
            var mask = i == full && rem != 0 ? (byte)(0xFF << (8 - rem)) : (byte)0;
            bytes[i] &= mask;
        }
        return new IPAddress(bytes);
    }

    public static void FlushDnsCache()
    {
        try { DnsFlushResolverCache(); } catch { }
    }

    public static bool IsOwnTunAdapter(NetworkInterface nic)
    {
        var desc = nic.Description ?? "";
        return nic.Name.IndexOf(TunInterfaceNameConst, StringComparison.OrdinalIgnoreCase) >= 0
               || desc.IndexOf(TunInterfaceNameConst, StringComparison.OrdinalIgnoreCase) >= 0
               || desc.IndexOf("wintun", StringComparison.OrdinalIgnoreCase) >= 0
               || desc.IndexOf("tun2socks", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private const string TunInterfaceNameConst = "se7en_tun";

    [StructLayout(LayoutKind.Sequential)]
    internal struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    internal enum TcpTableClass
    {
        TcpTableBasicListener,
        TcpTableBasicConnections,
        TcpTableBasicAll,
        TcpTableOwnerPidListener,
        TcpTableOwnerPidConnections,
        TcpTableOwnerPidAll,
        TcpTableOwnerModuleListener,
        TcpTableOwnerModuleConnections,
        TcpTableOwnerModuleAll
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int pdwSize,
        bool bOrder,
        int ulAf,
        TcpTableClass tableClass,
        uint reserved = 0);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, int flags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MibTcpRow
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
    }

    private const uint MibTcpStateDeleteTcb = 12;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint SetTcpEntry(ref MibTcpRow row);

    public static bool TryCloseTcpConnection(MibTcpRowOwnerPid conn)
    {
        var row = new MibTcpRow
        {
            State = MibTcpStateDeleteTcb,
            LocalAddr = conn.LocalAddr,
            LocalPort = conn.LocalPort,
            RemoteAddr = conn.RemoteAddr,
            RemotePort = conn.RemotePort,
        };
        return SetTcpEntry(ref row) == 0;
    }

    public sealed record ActiveTcpConn(IPAddress RemoteIp, int Pid, MibTcpRowOwnerPid Raw);

    public static List<ActiveTcpConn> GetActiveTcpConnections()
    {
        var list = new List<ActiveTcpConn>();
        int bufferSize = 0;

        uint res = GetExtendedTcpTable(IntPtr.Zero, ref bufferSize, true, 2, TcpTableClass.TcpTableOwnerPidAll, 0);
        if (bufferSize <= 0) return list;

        IntPtr pTable = Marshal.AllocHGlobal(bufferSize);
        try
        {
            res = GetExtendedTcpTable(pTable, ref bufferSize, true, 2, TcpTableClass.TcpTableOwnerPidAll, 0);
            if (res != 0) return list;

            int numEntries = Marshal.ReadInt32(pTable);
            IntPtr rowPtr = IntPtr.Add(pTable, 4);
            int rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();

            for (int i = 0; i < numEntries; i++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPtr);
                if (row.RemoteAddr != 0 && row.OwningPid > 4)
                {
                    var ip = new IPAddress(BitConverter.GetBytes(row.RemoteAddr));
                    list.Add(new ActiveTcpConn(ip, (int)row.OwningPid, row));
                }
                rowPtr = IntPtr.Add(rowPtr, rowSize);
            }
        }
        catch { }
        finally
        {
            Marshal.FreeHGlobal(pTable);
        }
        return list;
    }

    public static string? TryGetProcessPath(int pid)
    {
        if (pid <= 4) return null;
        IntPtr hProc = OpenProcess(0x1000 , false, pid);
        if (hProc == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            if (QueryFullProcessImageName(hProc, 0, sb, ref size))
            {
                return sb.ToString();
            }
            return null;
        }
        catch { return null; }
        finally
        {
            CloseHandle(hProc);
        }
    }

    public static void ResetLocalLoopbackConnections(params int[] ports)
    {
        if (ports is null || ports.Length == 0) return;
        try
        {
            var conns = GetActiveTcpConnections();
            foreach (var conn in conns)
            {
                var remotePort = (ushort)IPAddress.NetworkToHostOrder((short)conn.Raw.RemotePort);
                var localPort = (ushort)IPAddress.NetworkToHostOrder((short)conn.Raw.LocalPort);
                if (ports.Contains((int)remotePort) || ports.Contains((int)localPort))
                {
                    TryCloseTcpConnection(conn.Raw);
                }
            }
        }
        catch { }
    }
}
