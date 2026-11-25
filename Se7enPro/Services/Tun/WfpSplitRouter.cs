using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace Se7enPro.Services;

// Runtime WFP enforcement for the split-tunnel routing modes on TUN back-ends that cannot
// match processes themselves (tun2socks / zeptun). In mihomo TUN mode the same modes are
// emitted as native process rules instead (see MihomoConfigBuilder).
//
//   Block        -> per-app BLOCK filter on FWPM_LAYER_ALE_AUTH_CONNECT_V4/V6 (new TCP
//                   connects and UDP flows) plus FWPM_LAYER_DATAGRAM_DATA_V4/V6 (every
//                   datagram of an already-open UDP socket). The process can reach neither
//                   the physical interface nor the proxy.
//   Bypass       -> per-app BLOCK filter on FWPM_LAYER_ALE_CONNECT_REDIRECT_V4/V6.
//                   That layer only decides whether an outbound connect is redirected;
//                   a block there cancels the redirect and lets the connect continue
//                   over the physical interface, below the TUN. Nothing is dropped.
//   Through Tunnel -> no filter: the TUN catch-all route owns the process.
//
// A bypassed process is deliberately *not* filtered at ALE_AUTH_CONNECT, so it can still
// reach the local SOCKS listener when the user runs it in system-proxy mode as well.
//
// Layout constants, sublayer conventions and the interop structs mirror WfpKillSwitch,
// which already ships and is layout-verified at runtime.
internal sealed class WfpSplitRouter : IDisposable
{
    private const uint RpcCAuthnWinnt = 10;
    private const uint SessionFlagDynamic = 1;

    private const uint FwpActionBlock = 0x1001;

    private const int FwpByteBlobType = 12;
    private const int FwpUint64 = 4;

    // FWPM_LAYER_ALE_AUTH_CONNECT_V4 = c38d57d1-05a7-4c33-904f-7fbceee60e82
    private static readonly Guid LayerAleAuthConnectV4 =
        new("c38d57d1-05a7-4c33-904f-7fbceee60e82");

    // FWPM_LAYER_ALE_AUTH_CONNECT_V6 = 4a72393b-319f-44bc-84c3-ba54dcb3b6b4
    private static readonly Guid LayerAleAuthConnectV6 =
        new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4");

    // FWPM_LAYER_ALE_CONNECT_REDIRECT_V4 = c6e63c8c-b784-4562-aa7d-0a67cfcaf9a3
    private static readonly Guid LayerAleConnectRedirectV4 =
        new("c6e63c8c-b784-4562-aa7d-0a67cfcaf9a3");

    // FWPM_LAYER_ALE_CONNECT_REDIRECT_V6 = 587e54a7-8046-42ba-a0aa-b716250fc7fd
    private static readonly Guid LayerAleConnectRedirectV6 =
        new("587e54a7-8046-42ba-a0aa-b716250fc7fd");

    // FWPM_LAYER_DATAGRAM_DATA_V4 = 3d08bf4e-45f6-4930-a922-417098e20027
    private static readonly Guid LayerDatagramDataV4 =
        new("3d08bf4e-45f6-4930-a922-417098e20027");

    // FWPM_LAYER_DATAGRAM_DATA_V6 = fa45fe2f-3cba-4427-87fc-57b9a4b10d00
    private static readonly Guid LayerDatagramDataV6 =
        new("fa45fe2f-3cba-4427-87fc-57b9a4b10d00");

    // FWPM_CONDITION_ALE_APP_ID = d78e1e87-8644-4ea5-9437-d809ecefc971
    private static readonly Guid ConditionAleAppId =
        new("d78e1e87-8644-4ea5-9437-d809ecefc971");

    // FWPM_CONDITION_IP_REMOTE_ADDRESS = b235ae9a-1d64-49b8-a44c-5ff3d9095045
    private static readonly Guid ConditionIpRemoteAddress =
        new("b235ae9a-1d64-49b8-a44c-5ff3d9095045");

    private const int FwpV4AddrMaskType = 0x100;
    private const int FwpV6AddrMaskType = 0x101;

    // 1f3b6a72-6a1e-4e8f-9a30-2c5d9b7e4a55 — private to this feature.
    private static readonly Guid SublayerKey =
        new(BuildIdentity.SplitSublayerGuid);

    // Higher than the kill-switch weights, so a blocked app cannot be re-permitted by the
    // kill switch's engine permits, and a bypass redirect always wins.
    private const ulong AppBlockWeight = 0xFFFFFFFA;

    // Below every app-specific filter, so an explicitly allowed app still connects while
    // the catch-all denies the rest. Used only when the profile default is Block.
    private const ulong DefaultBlockWeight = 0x00000001;
    private const ulong BypassRedirectWeight = 0xFFFFFFFB;

    private readonly object _sync = new();
    private readonly List<long> _filterIds = new();
    private readonly List<IntPtr> _appIds = new();
    private IntPtr _engine;
    private volatile bool _armed;
    private string _signature = "";

    public bool IsArmed => _armed;

    // True when the redirect layer refused the filters, i.e. bypass falls back to the
    // per-connection route pinning monitor.
    public bool BypassNeedsRouteFallback { get; private set; }

    public string? LastError { get; private set; }

    // When the profile's default outbound is Block, everything that is not explicitly
    // allowed must be denied. A per-app list cannot express that, so this overload takes
    // the allowed apps and installs a low-weight catch-all deny behind them: the allowed
    // apps' filters win on weight, every other process is denied.
    public bool Apply(
        IReadOnlyList<string> blockedAppPaths,
        IReadOnlyList<string> bypassAppPaths,
        string signature,
        IReadOnlyList<string>? allowedWhenDefaultBlock = null,
        IReadOnlyList<string>? blockedCidrs = null)
    {
        lock (_sync)
        {
            if (_armed && string.Equals(_signature, signature, StringComparison.Ordinal))
            {
                return true;
            }

            try
            {
                if (!_armed)
                {
                    VerifyLayout();
                    CloseEngine();
                    OpenEngine();
                    AddSublayer();
                }

                RemoveFilters();

                foreach (var exe in blockedAppPaths)
                {
                    if (!TryGetAppId(exe, out var appId)) continue;
                    AddAppFilter(LayerAleAuthConnectV4, FwpActionBlock, AppBlockWeight, appId,
                        "Se7en split block " + FileName(exe));
                    AddAppFilter(LayerAleAuthConnectV6, FwpActionBlock, AppBlockWeight, appId,
                        "Se7en split block v6 " + FileName(exe));

                    // An open UDP socket is already past ALE_AUTH_CONNECT; the datagram
                    // layer is where its remaining traffic can still be dropped.
                    try
                    {
                        AddAppFilter(LayerDatagramDataV4, FwpActionBlock, AppBlockWeight, appId,
                            "Se7en split block udp " + FileName(exe));
                        AddAppFilter(LayerDatagramDataV6, FwpActionBlock, AppBlockWeight, appId,
                            "Se7en split block udp v6 " + FileName(exe));
                    }
                    catch (Exception datagramEx)
                    {
                        // Older builds of Windows can refuse the datagram layer; the
                        // connect-layer block still stops every new flow.
                        LastError = datagramEx.Message;
                    }
                }

                if (blockedCidrs is not null)
                {
                    foreach (var cidr in blockedCidrs)
                    {
                        if (SplitRules.ParseIpCidr(cidr) is not { } range) continue;
                        if (System.Net.IPAddress.IsLoopback(range.Addr)) continue;

                        try
                        {
                            using var rangeConditions = new ConditionBuilder();
                            if (range.Addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            {
                                rangeConditions.AddRemoteV4(range.Addr, range.Prefix);
                                AddFilter(LayerAleAuthConnectV4, "Se7en split block range " + cidr,
                                    FwpActionBlock, AppBlockWeight, rangeConditions.Conditions);
                            }
                            else
                            {
                                rangeConditions.AddRemoteV6(range.Addr, range.Prefix);
                                AddFilter(LayerAleAuthConnectV6, "Se7en split block range v6 " + cidr,
                                    FwpActionBlock, AppBlockWeight, rangeConditions.Conditions);
                            }
                        }
                        catch (Exception rangeEx)
                        {
                            LastError = rangeEx.Message;
                        }
                    }
                }

                if (allowedWhenDefaultBlock is not null)
                {
                    // A deny with no app condition, placed under the allowed apps' weights,
                    // so an explicitly-allowed app still connects while everything else is
                    // refused at the connect layer and at the datagram layer.
                    AddFilter(LayerAleAuthConnectV4, "Se7en split default block",
                        FwpActionBlock, DefaultBlockWeight, Array.Empty<FwpmFilterCondition0>());
                    AddFilter(LayerAleAuthConnectV6, "Se7en split default block v6",
                        FwpActionBlock, DefaultBlockWeight, Array.Empty<FwpmFilterCondition0>());
                    try
                    {
                        AddFilter(LayerDatagramDataV4, "Se7en split default block udp",
                            FwpActionBlock, DefaultBlockWeight, Array.Empty<FwpmFilterCondition0>());
                        AddFilter(LayerDatagramDataV6, "Se7en split default block udp v6",
                            FwpActionBlock, DefaultBlockWeight, Array.Empty<FwpmFilterCondition0>());
                    }
                    catch
                    {
                        // Datagram layer is optional on older builds.
                    }
                }

                BypassNeedsRouteFallback = false;
                foreach (var exe in bypassAppPaths)
                {
                    if (!TryGetAppId(exe, out var appId)) continue;
                    try
                    {
                        AddAppFilter(LayerAleConnectRedirectV4, FwpActionBlock, BypassRedirectWeight, appId,
                            "Se7en split bypass " + FileName(exe));
                        AddAppFilter(LayerAleConnectRedirectV6, FwpActionBlock, BypassRedirectWeight, appId,
                            "Se7en split bypass v6 " + FileName(exe));
                    }
                    catch (Exception redirectEx)
                    {
                        BypassNeedsRouteFallback = true;
                        LastError = redirectEx.Message;
                    }
                }

                _armed = true;
                _signature = signature;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                try { RemoveFilters(); } catch { }
                CloseEngine();
                _armed = false;
                _signature = "";
                return false;
            }
        }
    }

    public void Disarm()
    {
        lock (_sync)
        {
            try { RemoveFilters(); } catch { }
            CloseEngine();
            _armed = false;
            _signature = "";
            BypassNeedsRouteFallback = false;
        }
    }

    public void Dispose() => Disarm();

    private static string FileName(string path)
    {
        try { return Path.GetFileName(path); } catch { return path; }
    }

    private bool TryGetAppId(string exePath, out IntPtr appId)
    {
        appId = IntPtr.Zero;
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return false;

        var rc = FwpmGetAppIdFromFileName0(exePath, out appId);
        if (rc != 0 || appId == IntPtr.Zero)
        {
            appId = IntPtr.Zero;
            LastError = $"Could not resolve an app id for {exePath} (error {rc}).";
            return false;
        }

        _appIds.Add(appId);
        return true;
    }

    private void AddAppFilter(Guid layer, uint action, ulong weight, IntPtr appId, string name)
    {
        using var conditions = new ConditionBuilder();
        conditions.AddAppId(appId);
        AddFilter(layer, name, action, weight, conditions.Conditions);
    }

    private void AddFilter(
        Guid layer,
        string name,
        uint action,
        ulong weight,
        IReadOnlyList<FwpmFilterCondition0> conditions)
    {
        var namePtr = Marshal.StringToHGlobalUni(name);
        var weightPtr = Marshal.AllocHGlobal(sizeof(ulong));
        var conditionsPtr = IntPtr.Zero;

        try
        {
            Marshal.WriteInt64(weightPtr, unchecked((long)weight));

            var filter = new FwpmFilter0
            {
                FilterKey = Guid.Empty,
                DisplayData = new FwpmDisplayData0 { Name = namePtr, Description = IntPtr.Zero },
                Flags = 0,
                ProviderKey = IntPtr.Zero,
                LayerKey = layer,
                SubLayerKey = SublayerKey,
                Weight = new FwpValue0 { Type = FwpUint64, Value = weightPtr },
                NumFilterConditions = (uint)conditions.Count,
                Action = new FwpmAction0 { Type = action, FilterType = Guid.Empty },
            };

            if (conditions.Count > 0)
            {
                var size = Marshal.SizeOf<FwpmFilterCondition0>();
                conditionsPtr = Marshal.AllocHGlobal(size * conditions.Count);
                for (var i = 0; i < conditions.Count; i++)
                {
                    Marshal.StructureToPtr(conditions[i], conditionsPtr + (i * size), false);
                }
                filter.FilterCondition = conditionsPtr;
            }

            var rc = FwpmFilterAdd0(_engine, ref filter, IntPtr.Zero, out var id);
            if (rc != 0)
            {
                throw new Win32Exception((int)rc, $"FwpmFilterAdd0({name}) failed");
            }

            _filterIds.Add((long)id);
        }
        finally
        {
            if (conditionsPtr != IntPtr.Zero) Marshal.FreeHGlobal(conditionsPtr);
            Marshal.FreeHGlobal(weightPtr);
            Marshal.FreeHGlobal(namePtr);
        }
    }

    private void RemoveFilters()
    {
        if (_engine != IntPtr.Zero)
        {
            foreach (var id in _filterIds)
            {
                try { FwpmFilterDeleteById0(_engine, (ulong)id); } catch { }
            }
        }

        _filterIds.Clear();

        foreach (var appId in _appIds)
        {
            var ptr = appId;
            try { FwpmFreeMemory0(ref ptr); } catch { }
        }

        _appIds.Clear();
    }

    private void OpenEngine()
    {
        var session = new FwpmSession0
        {
            SessionKey = Guid.Empty,
            DisplayData = new FwpmDisplayData0
            {
                Name = Marshal.StringToHGlobalUni("Se7en split routing"),
                Description = IntPtr.Zero,
            },
            Flags = SessionFlagDynamic,
            TxnWaitTimeoutInMSec = 5000,
        };

        try
        {
            var rc = FwpmEngineOpen0(null, RpcCAuthnWinnt, IntPtr.Zero, ref session, out _engine);
            if (rc != 0)
            {
                _engine = IntPtr.Zero;
                throw new Win32Exception((int)rc,
                    "Could not open the Windows Filtering Platform engine (administrator rights are required)");
            }
        }
        finally
        {
            if (session.DisplayData.Name != IntPtr.Zero) Marshal.FreeHGlobal(session.DisplayData.Name);
        }
    }

    private void CloseEngine()
    {
        if (_engine == IntPtr.Zero) return;
        try { FwpmEngineClose0(_engine); } catch { }
        _engine = IntPtr.Zero;
    }

    private void AddSublayer()
    {
        var name = Marshal.StringToHGlobalUni("Se7en Pro split routing sublayer");
        try
        {
            var sublayer = new FwpmSubLayer0
            {
                SubLayerKey = SublayerKey,
                DisplayData = new FwpmDisplayData0 { Name = name, Description = IntPtr.Zero },
                Flags = 0,
                Weight = ushort.MaxValue,
            };

            var rc = FwpmSubLayerAdd0(_engine, ref sublayer, IntPtr.Zero);
            if (rc != 0 && rc != 183)
            {
                throw new Win32Exception((int)rc, "FwpmSubLayerAdd0 failed");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(name);
        }
    }

    private static void VerifyLayout()
    {
        static void Check(int actual, int expected, string what)
        {
            if (actual != expected)
            {
                throw new InvalidOperationException(
                    $"WFP interop layout mismatch: {what} is {actual}, expected {expected}.");
            }
        }

        Check(Marshal.SizeOf<FwpmFilter0>(), 200, "FWPM_FILTER0");
        Check(Marshal.SizeOf<FwpmAction0>(), 24, "FWPM_ACTION0");
        Check(Marshal.SizeOf<FwpmFilterCondition0>(), 40, "FWPM_FILTER_CONDITION0");
        Check(Marshal.SizeOf<FwpmSession0>(), 72, "FWPM_SESSION0");
        Check(Marshal.SizeOf<FwpmSubLayer0>(), 72, "FWPM_SUBLAYER0");
        Check(Marshal.SizeOf<FwpValue0>(), 16, "FWP_VALUE0");
        Check(Marshal.SizeOf<FwpConditionValue0>(), 16, "FWP_CONDITION_VALUE0");
        Check(Marshal.SizeOf<FwpByteBlob>(), 16, "FWP_BYTE_BLOB");
    }

    private sealed class ConditionBuilder : IDisposable
    {
        private readonly List<FwpmFilterCondition0> _conditions = new();

        public IReadOnlyList<FwpmFilterCondition0> Conditions => _conditions;

        public void AddAppId(IntPtr appIdBlob)
        {
            _conditions.Add(new FwpmFilterCondition0
            {
                FieldKey = ConditionAleAppId,
                MatchType = 0,
                ConditionValue = new FwpConditionValue0 { Type = FwpByteBlobType, Value = appIdBlob },
            });
        }

        public void AddRemoteV4(System.Net.IPAddress address, byte prefix)
        {
            var value = new FwpV4AddrAndMask
            {
                Address = HostOrderV4(address),
                Mask = HostOrderV4(PrefixToMask(prefix)),
            };
            var ptr = Alloc(value);
            _conditions.Add(new FwpmFilterCondition0
            {
                FieldKey = ConditionIpRemoteAddress,
                MatchType = 0,
                ConditionValue = new FwpConditionValue0 { Type = FwpV4AddrMaskType, Value = ptr },
            });
        }

        public void AddRemoteV6(System.Net.IPAddress address, byte prefix)
        {
            var bytes = address.GetAddressBytes();
            var value = new FwpV6AddrAndMask { Addr = bytes, PrefixLength = prefix };
            var ptr = Alloc(value);
            _conditions.Add(new FwpmFilterCondition0
            {
                FieldKey = ConditionIpRemoteAddress,
                MatchType = 0,
                ConditionValue = new FwpConditionValue0 { Type = FwpV6AddrMaskType, Value = ptr },
            });
        }

        private readonly List<IntPtr> _allocated = new();

        private IntPtr Alloc<T>(T value) where T : struct
        {
            var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
            Marshal.StructureToPtr(value, ptr, false);
            _allocated.Add(ptr);
            return ptr;
        }

        private static System.Net.IPAddress PrefixToMask(byte prefix)
        {
            var bytes = new byte[4];
            for (var i = 0; i < prefix && i < 32; i++)
            {
                bytes[i / 8] |= (byte)(0x80 >> (i % 8));
            }
            return new System.Net.IPAddress(bytes);
        }

        private static uint HostOrderV4(System.Net.IPAddress address)
        {
            var b = address.GetAddressBytes();
            return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        }

        public void Dispose()
        {
            foreach (var ptr in _allocated)
            {
                try { Marshal.FreeHGlobal(ptr); } catch { }
            }
            _allocated.Clear();
            _conditions.Clear();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FwpV4AddrAndMask
    {
        public uint Address;
        public uint Mask;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FwpV6AddrAndMask
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] Addr;
        public byte PrefixLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FwpByteBlob
    {
        public uint Size;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FwpValue0
    {
        public int Type;
        public IntPtr Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FwpConditionValue0
    {
        public int Type;
        public IntPtr Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FwpmDisplayData0
    {
        public IntPtr Name;
        public IntPtr Description;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct FwpmAction0
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public Guid FilterType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FwpmFilterCondition0
    {
        public Guid FieldKey;
        public int MatchType;
        public FwpConditionValue0 ConditionValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FwpmFilter0
    {
        public Guid FilterKey;
        public FwpmDisplayData0 DisplayData;
        public uint Flags;
        public IntPtr ProviderKey;
        public FwpByteBlob ProviderData;
        public Guid LayerKey;
        public Guid SubLayerKey;
        public FwpValue0 Weight;
        public uint NumFilterConditions;
        public IntPtr FilterCondition;
        public FwpmAction0 Action;
        public Guid ProviderContextKey;
        public IntPtr Reserved;
        public ulong FilterId;
        public FwpValue0 EffectiveWeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FwpmSession0
    {
        public Guid SessionKey;
        public FwpmDisplayData0 DisplayData;
        public uint Flags;
        public uint TxnWaitTimeoutInMSec;
        public uint ProcessId;
        public IntPtr Sid;
        public IntPtr Username;
        public int KernelMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FwpmSubLayer0
    {
        public Guid SubLayerKey;
        public FwpmDisplayData0 DisplayData;
        public ushort Flags;
        public IntPtr ProviderKey;
        public FwpByteBlob ProviderData;
        public ushort Weight;
    }

    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint FwpmEngineOpen0(
        string? serverName,
        uint authnService,
        IntPtr authIdentity,
        ref FwpmSession0 session,
        out IntPtr engineHandle);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    private static extern uint FwpmEngineClose0(IntPtr engineHandle);

    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint FwpmSubLayerAdd0(
        IntPtr engineHandle,
        ref FwpmSubLayer0 subLayer,
        IntPtr sd);

    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint FwpmFilterAdd0(
        IntPtr engineHandle,
        ref FwpmFilter0 filter,
        IntPtr sd,
        out ulong id);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    private static extern uint FwpmFilterDeleteById0(IntPtr engineHandle, ulong id);

    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint FwpmGetAppIdFromFileName0(string fileName, out IntPtr appId);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    private static extern uint FwpmFreeMemory0(ref IntPtr p);
}
