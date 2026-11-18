using System;
using System.Collections.Generic;
using System.IO;
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
    private const int IpPrefixOriginManual = 1;
    private const int IpSuffixOriginManual = 1;
    private const uint Infinite32 = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Explicit, Size = 28, Pack = 4)]
    internal struct SockaddrInet
    {
        [FieldOffset(0)] public ushort Family;
        [FieldOffset(2)] public ushort Port;

        [FieldOffset(4)] public uint V4Address;

        [FieldOffset(4)] public uint FlowInfo;
        [FieldOffset(8)] public ulong V6Low;
        [FieldOffset(16)] public ulong V6High;
        [FieldOffset(24)] public uint ScopeId;

        public static SockaddrInet FromIp(IPAddress ip)
        {
            var s = new SockaddrInet();
            var b = ip.GetAddressBytes();
            if (b.Length == 4)
            {
                s.Family = AfInet;
                s.V4Address = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(b);
            }
            else
            {
                s.Family = AfInet6;
                s.V6Low = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(b);
                s.V6High = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(8));
                s.ScopeId = (uint)ip.ScopeId;
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
        public int PrefixOrigin;
        public int SuffixOrigin;
        public uint ValidLifetime;
        public uint PreferredLifetime;
        public byte OnLinkPrefixLength;
        public byte SkipAsSource;
        public int DadState;
        public uint ZoneScope;
        public long CreationTimeStamp;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MibIpforwardRow2
    {
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public IpAddressPrefix DestinationPrefix;
        public SockaddrInet NextHop;
        public byte SitePrefixLength;
        public uint ValidLifetime;
        public uint PreferredLifetime;
        public uint Metric;
        public uint Protocol;
        public byte Loopback;
        public byte AutoconfigureAddress;
        public byte Publish;
        public byte Immortal;
        public uint Age;
        public uint Origin;
    }

    internal static void VerifyLayout()
    {
        static void Check(int actual, int expected, string what)
        {
            if (actual != expected)
            {
                throw new InvalidOperationException(
                    $"TUN P/Invoke layout mismatch: {what} is {actual}, expected {expected}.");
            }
        }

        Check(Marshal.SizeOf<SockaddrInet>(), 28, "SOCKADDR_INET size");
        Check((int)Marshal.OffsetOf<SockaddrInet>(nameof(SockaddrInet.V4Address)), 4, "SOCKADDR_IN.sin_addr");
        Check((int)Marshal.OffsetOf<SockaddrInet>(nameof(SockaddrInet.FlowInfo)), 4, "SOCKADDR_IN6.sin6_flowinfo");
        Check((int)Marshal.OffsetOf<SockaddrInet>(nameof(SockaddrInet.V6Low)), 8, "SOCKADDR_IN6.sin6_addr");
        Check((int)Marshal.OffsetOf<SockaddrInet>(nameof(SockaddrInet.ScopeId)), 24, "SOCKADDR_IN6.sin6_scope_id");

        Check(Marshal.SizeOf<IpAddressPrefix>(), 32, "IP_ADDRESS_PREFIX size");

        Check(Marshal.SizeOf<MibUnicastIpAddressRow>(), 80, "MIB_UNICASTIPADDRESS_ROW size");
        Check((int)Marshal.OffsetOf<MibUnicastIpAddressRow>(nameof(MibUnicastIpAddressRow.InterfaceLuid)), 32, "row.InterfaceLuid");
        Check((int)Marshal.OffsetOf<MibUnicastIpAddressRow>(nameof(MibUnicastIpAddressRow.InterfaceIndex)), 40, "row.InterfaceIndex");
        Check((int)Marshal.OffsetOf<MibUnicastIpAddressRow>(nameof(MibUnicastIpAddressRow.PrefixOrigin)), 44, "row.PrefixOrigin");
        Check((int)Marshal.OffsetOf<MibUnicastIpAddressRow>(nameof(MibUnicastIpAddressRow.SuffixOrigin)), 48, "row.SuffixOrigin");
        Check((int)Marshal.OffsetOf<MibUnicastIpAddressRow>(nameof(MibUnicastIpAddressRow.ValidLifetime)), 52, "row.ValidLifetime");
        Check((int)Marshal.OffsetOf<MibUnicastIpAddressRow>(nameof(MibUnicastIpAddressRow.PreferredLifetime)), 56, "row.PreferredLifetime");
        Check((int)Marshal.OffsetOf<MibUnicastIpAddressRow>(nameof(MibUnicastIpAddressRow.OnLinkPrefixLength)), 60, "row.OnLinkPrefixLength");
        Check((int)Marshal.OffsetOf<MibUnicastIpAddressRow>(nameof(MibUnicastIpAddressRow.SkipAsSource)), 61, "row.SkipAsSource");
        Check((int)Marshal.OffsetOf<MibUnicastIpAddressRow>(nameof(MibUnicastIpAddressRow.DadState)), 64, "row.DadState");
        Check((int)Marshal.OffsetOf<MibUnicastIpAddressRow>(nameof(MibUnicastIpAddressRow.ZoneScope)), 68, "row.ScopeId");
        Check((int)Marshal.OffsetOf<MibUnicastIpAddressRow>(nameof(MibUnicastIpAddressRow.CreationTimeStamp)), 72, "row.CreationTimeStamp");

        Check(Marshal.SizeOf<MibIpforwardRow2>(), 104, "MIB_IPFORWARD_ROW2 size");
        Check((int)Marshal.OffsetOf<MibIpforwardRow2>(nameof(MibIpforwardRow2.InterfaceIndex)), 8, "fwd.InterfaceIndex");
        Check((int)Marshal.OffsetOf<MibIpforwardRow2>(nameof(MibIpforwardRow2.DestinationPrefix)), 12, "fwd.DestinationPrefix");
        Check((int)Marshal.OffsetOf<MibIpforwardRow2>(nameof(MibIpforwardRow2.NextHop)), 44, "fwd.NextHop");
        Check((int)Marshal.OffsetOf<MibIpforwardRow2>(nameof(MibIpforwardRow2.SitePrefixLength)), 72, "fwd.SitePrefixLength");
        Check((int)Marshal.OffsetOf<MibIpforwardRow2>(nameof(MibIpforwardRow2.ValidLifetime)), 76, "fwd.ValidLifetime");
        Check((int)Marshal.OffsetOf<MibIpforwardRow2>(nameof(MibIpforwardRow2.Origin)), 100, "fwd.Origin");
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

    [DllImport("netioapi.dll", CharSet = CharSet.Unicode)]
    private static extern uint ConvertInterfaceAliasToLuid(string interfaceAlias, out ulong interfaceLuid);

    [DllImport("netioapi.dll")]
    private static extern uint ConvertInterfaceLuidToIndex(ref ulong interfaceLuid, out uint interfaceIndex);

    

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

    public static int GetAdapterIndex(NetworkInterface nic)
    {
        try
        {
            var props = nic.GetIPProperties();
            try
            {
                var v4 = props.GetIPv4Properties();
                if (v4 is not null && v4.Index > 0) return v4.Index;
            }
            catch { }

            try
            {
                var v6 = props.GetIPv6Properties();
                if (v6 is not null && v6.Index > 0) return v6.Index;
            }
            catch { }
        }
        catch { }
        return 0;
    }

    public static bool TryResolveAdapterIdentity(string alias, out uint ifIndex, out ulong luid)
    {
        ifIndex = 0;
        luid = 0;
        try
        {
            var candidateLuid = 0UL;
            if (ConvertInterfaceAliasToLuid(alias, out candidateLuid) == 0 && candidateLuid != 0)
            {
                var index = 0U;
                if (ConvertInterfaceLuidToIndex(ref candidateLuid, out index) == 0 && index != 0)
                {
                    ifIndex = index;
                    luid = candidateLuid;
                    return true;
                }
            }
        }
        catch { }

        try
        {
            var nic = FindAdapter(alias);
            var index = nic is null ? 0 : GetAdapterIndex(nic);
            if (index > 0)
            {
                ifIndex = (uint)index;
                return true;
            }
        }
        catch { }

        return false;
    }

    public static ulong TryGetAdapterLuid(string alias)
    {
        try
        {
            if (ConvertInterfaceAliasToLuid(alias, out var luid) == 0) return luid;
        }
        catch { }
        return 0;
    }

    

        public static void SetAdapterIpAddress(int ifIndex, IPAddress ip, byte prefixLen, ulong interfaceLuid = 0)
    {
        if (ifIndex <= 0 && interfaceLuid == 0)
        {
            throw new InvalidOperationException(
                $"Cannot assign {ip}/{prefixLen}: the TUN adapter has no usable interface index.");
        }

        var row = new MibUnicastIpAddressRow
        {
            Address = SockaddrInet.FromIp(ip),
            InterfaceLuid = interfaceLuid,
            InterfaceIndex = ifIndex > 0 ? (uint)ifIndex : 0,
            PrefixOrigin = IpPrefixOriginManual,
            SuffixOrigin = IpSuffixOriginManual,
            ValidLifetime = Infinite32,
            PreferredLifetime = Infinite32,
            OnLinkPrefixLength = prefixLen,
            SkipAsSource = 0,
            DadState = IpDadStatePreferred,
            ZoneScope = 0,
            CreationTimeStamp = 0,
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
               || desc.IndexOf("tun2socks", StringComparison.OrdinalIgnoreCase) >= 0
               || desc.IndexOf("zeptun", StringComparison.OrdinalIgnoreCase) >= 0
               || desc.IndexOf("mihomo", StringComparison.OrdinalIgnoreCase) >= 0;
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
        
        GetExtendedTcpTable(IntPtr.Zero, ref bufferSize, true, 2, TcpTableClass.TcpTableOwnerPidAll, 0);
        if (bufferSize <= 0) return list;

        
        
        
        
        
        const int errorInsufficientBuffer = 122;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            IntPtr pTable = Marshal.AllocHGlobal(bufferSize);
            try
            {
                var needed = bufferSize;
                var res = GetExtendedTcpTable(pTable, ref needed, true, 2, TcpTableClass.TcpTableOwnerPidAll, 0);
                if (res == errorInsufficientBuffer && needed > bufferSize)
                {
                    bufferSize = needed;
                    continue;   
                }
                if (res != 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[wintun] GetExtendedTcpTable failed: {res}");
                    return list;
                }

                int numEntries = Marshal.ReadInt32(pTable);

                int rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();

                
                
                
                
                int usable = Math.Min(numEntries, Math.Max(0, (bufferSize - sizeof(int)) / rowSize));

                for (int i = 0; i < usable; i++)
                {
                    IntPtr rowPtr = IntPtr.Add(pTable, sizeof(int) + (i * rowSize));
                    var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPtr);
                    if (row.RemoteAddr != 0 && row.OwningPid > 4)
                    {
                        
                        
                        
                        
                        
                        
                        
                        
                        
                        var ip = new IPAddress(BitConverter.GetBytes(row.RemoteAddr));
                        list.Add(new ActiveTcpConn(ip, (int)row.OwningPid, row));
                    }
                }
                break;
            }
            finally
            {
                Marshal.FreeHGlobal(pTable);
            }
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

        public static void ResetStaleLoopbackConnections(int activeHttpPort, int activeSocksPort, params int[] candidatePorts)
    {
        var stalePorts = new HashSet<int>(candidatePorts ?? Array.Empty<int>());
        stalePorts.Remove(activeHttpPort);
        stalePorts.Remove(activeSocksPort);
        if (stalePorts.Count == 0) return;

        try
        {
            foreach (var conn in GetActiveTcpConnections())
            {
                if (!IPAddress.IsLoopback(new IPAddress(BitConverter.GetBytes(conn.Raw.LocalAddr)))
                    || !IPAddress.IsLoopback(new IPAddress(BitConverter.GetBytes(conn.Raw.RemoteAddr))))
                {
                    continue;
                }

                var remotePort = (ushort)IPAddress.NetworkToHostOrder((short)conn.Raw.RemotePort);
                var localPort = (ushort)IPAddress.NetworkToHostOrder((short)conn.Raw.LocalPort);
                if (stalePorts.Contains(remotePort) || stalePorts.Contains(localPort))
                {
                    TryCloseTcpConnection(conn.Raw);
                }
            }
        }
        catch { }
    }

    private static bool IsPublicInternetIpv4(IPAddress ip)
    {
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        if (b[0] is 0 or 10 or 127 || ip.Equals(IPAddress.Broadcast)) return false;
        if (b[0] == 172 && b[1] is >= 16 and <= 31) return false;
        if (b[0] == 192 && b[1] == 168) return false;
        if (b[0] == 169 && b[1] == 254) return false;
        if (b[0] == 100 && (b[1] & 0xC0) == 64) return false;
        if ((b[0] & 0xF0) == 224) return false;
        return true;
    }

    public static void ResetActiveNonEngineWebConnections()
    {
        try
        {
            var currentPid = Environment.ProcessId;
            var engineNames = EngineProcessNames.All;
            var pidCache = new Dictionary<int, string?>();

            foreach (var conn in GetActiveTcpConnections())
            {
                var remoteIp = conn.RemoteIp;
                if (IPAddress.IsLoopback(remoteIp) || !IsPublicInternetIpv4(remoteIp))
                    continue;

                var remotePort = (ushort)IPAddress.NetworkToHostOrder((short)conn.Raw.RemotePort);
                if (remotePort != 80 && remotePort != 443 && remotePort != 8080 && remotePort != 8443)
                    continue;

                var pid = conn.Pid;
                if (pid == currentPid || pid <= 4) continue;

                if (!pidCache.TryGetValue(pid, out var name))
                {
                    var path = TryGetProcessPath(pid);
                    name = string.IsNullOrEmpty(path) ? null : Path.GetFileName(path);
                    pidCache[pid] = name;
                }

                if (string.IsNullOrEmpty(name)) continue;
                if (engineNames.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;

                TryCloseTcpConnection(conn.Raw);
            }
        }
        catch { }
    }

    public static void TryCloseTcpConnectionsTo(IPAddress targetIp)
    {
        try
        {
            var currentPid = Environment.ProcessId;
            var engineNames = EngineProcessNames.All;
            var pidCache = new Dictionary<int, string?>();

            foreach (var conn in GetActiveTcpConnections())
            {
                if (!conn.RemoteIp.Equals(targetIp)) continue;

                var pid = conn.Pid;
                if (pid == currentPid || pid <= 4) continue;

                if (!pidCache.TryGetValue(pid, out var name))
                {
                    var path = TryGetProcessPath(pid);
                    name = string.IsNullOrEmpty(path) ? null : Path.GetFileName(path);
                    pidCache[pid] = name;
                }

                if (!string.IsNullOrEmpty(name) && engineNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                    continue;

                TryCloseTcpConnection(conn.Raw);
            }
        }
        catch { }
    }
}

