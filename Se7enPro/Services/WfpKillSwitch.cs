using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Se7enPro.Services;

internal sealed class WfpKillSwitch : IDisposable
{
    private const uint RpcCAuthnWinnt = 10;
    private const uint SessionFlagDynamic = 1;

    private const uint FwpActionBlock = 0x1001;
    private const uint FwpActionPermit = 0x1002;

    private const int FwpUint64 = 4;
    private const int FwpByteBlobType = 12;
    private const int FwpV4AddrMask = 0x100;
    private const int FwpV6AddrMask = 0x101;
    private const int FwpMatchEqual = 0;

    private const ulong BlockWeight = 0xFFFFFFF0;
    private const ulong LanPermitWeight = 0xFFFFFFF1;
    private const ulong SinkholeBlockWeight = 0xFFFFFFF2;
    private const ulong AppPermitWeight = 0xFFFFFFF3;

    private static readonly Guid LayerAleAuthConnectV4 =
        new("c38d57d1-05a7-4c33-904f-7fbceee60e82");

    private static readonly Guid LayerAleAuthConnectV6 =
        new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4");

    private static readonly Guid ConditionAleAppId =
        new("d78e1e87-8644-4ea5-9437-d809ecefc971");

    private static readonly Guid ConditionIpRemoteAddress =
        new("b235ae9a-1d64-49b8-a44c-5ff3d9095045");

    private static readonly Guid SublayerKey =
        new("6f2c4b1a-9d38-4f6e-8a1b-3c5d7e9f0a2b");

    private static readonly (string Address, string Mask)[] V4Permits =
    {
        ("127.0.0.0", "255.0.0.0"),
        ("10.0.0.0", "255.0.0.0"),
        ("172.16.0.0", "255.240.0.0"),
        ("192.168.0.0", "255.255.0.0"),
        ("169.254.0.0", "255.255.0.0"),
    };

    private static readonly (string Address, byte Prefix)[] V6Permits =
    {
        ("::1", 128),
        ("fc00::", 7),
        ("fe80::", 10),
    };

    private readonly object _sync = new();
    private IntPtr _engine;
    private volatile bool _armed;

    public bool IsArmed => _armed;
    public string? LastError { get; private set; }

    public bool Arm(IReadOnlyCollection<string> allowedPrograms)
    {
        lock (_sync)
        {
            if (_armed) return true;
            LastError = null;
            try
            {
                VerifyLayout();
                CloseEngine();

                OpenEngine();
                AddSublayer();

                AddFilter(LayerAleAuthConnectV4, "Se7en Kill Switch block v4", FwpActionBlock,
                          BlockWeight, Array.Empty<FwpmFilterCondition0>());
                AddFilter(LayerAleAuthConnectV6, "Se7en Kill Switch block v6", FwpActionBlock,
                          BlockWeight, Array.Empty<FwpmFilterCondition0>());

                foreach (var (address, mask) in V4Permits)
                {
                    using var conditions = new ConditionBuilder();
                    conditions.AddV4(address, mask);
                    AddFilter(LayerAleAuthConnectV4, "Se7en Kill Switch LAN permit " + address,
                              FwpActionPermit, LanPermitWeight, conditions.Conditions);
                }

                {
                    using var sinkholeCond = new ConditionBuilder();
                    sinkholeCond.AddV4("10.10.34.0", "255.255.255.0");
                    AddFilter(LayerAleAuthConnectV4, "Se7en Kill Switch sinkhole block 10.10.34.0/24",
                              FwpActionBlock, SinkholeBlockWeight, sinkholeCond.Conditions);
                }

                foreach (var (address, prefix) in V6Permits)
                {
                    using var conditions = new ConditionBuilder();
                    conditions.AddV6(address, prefix);
                    AddFilter(LayerAleAuthConnectV6, "Se7en Kill Switch LAN permit " + address,
                              FwpActionPermit, LanPermitWeight, conditions.Conditions);
                }

                var permittedCount = 0;
                foreach (var exe in allowedPrograms)
                {
                    if (!System.IO.File.Exists(exe)) continue;
                    var v4Ok = AddAppPermit(LayerAleAuthConnectV4, exe);
                    var v6Ok = AddAppPermit(LayerAleAuthConnectV6, exe);
                    if (v4Ok && v6Ok) permittedCount++;
                }

                if (permittedCount == 0 && allowedPrograms.Count > 0)
                {
                    LastError ??= "Could not register WFP permits for any engine executable.";
                    CloseEngine();
                    _armed = false;
                    return false;
                }

                _armed = true;
                LastError = null;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                CloseEngine();
                _armed = false;
                return false;
            }
        }
    }

    public void Disarm()
    {
        lock (_sync)
        {
            CloseEngine();
            _armed = false;
        }
    }

    private void OpenEngine()
    {
        var session = new FwpmSession0
        {
            SessionKey = Guid.Empty,
            DisplayData = new FwpmDisplayData0
            {
                Name = Marshal.StringToHGlobalUni("Se7en Pro kill switch"),
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
        var name = Marshal.StringToHGlobalUni("Se7en Pro kill switch sublayer");
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

    private bool AddAppPermit(Guid layer, string exePath)
    {
        var rc = FwpmGetAppIdFromFileName0(exePath, out var appId);
        if (rc != 0 || appId == IntPtr.Zero)
        {
            LastError = $"Could not resolve the app id for {exePath} (error {rc}).";
            return false;
        }

        try
        {
            using var conditions = new ConditionBuilder();
            conditions.AddAppId(appId);
            AddFilter(layer, "Se7en Kill Switch allow " + System.IO.Path.GetFileName(exePath),
                      FwpActionPermit, AppPermitWeight, conditions.Conditions);
            return true;
        }
        finally
        {
            FwpmFreeMemory0(ref appId);
        }
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

            var rc = FwpmFilterAdd0(_engine, ref filter, IntPtr.Zero, out _);
            if (rc != 0)
            {
                throw new Win32Exception((int)rc, $"FwpmFilterAdd0({name}) failed");
            }
        }
        finally
        {
            if (conditionsPtr != IntPtr.Zero) Marshal.FreeHGlobal(conditionsPtr);
            Marshal.FreeHGlobal(weightPtr);
            Marshal.FreeHGlobal(namePtr);
        }
    }

    public void Dispose() => Disarm();

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
        Check(Marshal.SizeOf<FwpV4AddrAndMask>(), 8, "FWP_V4_ADDR_AND_MASK");
        Check(Marshal.SizeOf<FwpV6AddrAndMask>(), 17, "FWP_V6_ADDR_AND_MASK");
    }

    private sealed class ConditionBuilder : IDisposable
    {
        private readonly List<FwpmFilterCondition0> _conditions = new();
        private readonly List<IntPtr> _allocations = new();

        public IReadOnlyList<FwpmFilterCondition0> Conditions => _conditions;

        public void AddAppId(IntPtr appIdBlob)
        {
            _conditions.Add(new FwpmFilterCondition0
            {
                FieldKey = ConditionAleAppId,
                MatchType = FwpMatchEqual,
                ConditionValue = new FwpConditionValue0 { Type = FwpByteBlobType, Value = appIdBlob },
            });
        }

        public void AddV4(string address, string mask)
        {
            var value = new FwpV4AddrAndMask
            {
                Address = HostOrderV4(address),
                Mask = HostOrderV4(mask),
            };
            var ptr = Alloc(value);
            _conditions.Add(new FwpmFilterCondition0
            {
                FieldKey = ConditionIpRemoteAddress,
                MatchType = FwpMatchEqual,
                ConditionValue = new FwpConditionValue0 { Type = FwpV4AddrMask, Value = ptr },
            });
        }

        public void AddV6(string address, byte prefixLength)
        {
            var ip = System.Net.IPAddress.Parse(address).GetAddressBytes();
            var value = new FwpV6AddrAndMask
            {
                Low = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(ip),
                High = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(ip.AsSpan(8)),
                PrefixLength = prefixLength,
            };
            var ptr = Alloc(value);
            _conditions.Add(new FwpmFilterCondition0
            {
                FieldKey = ConditionIpRemoteAddress,
                MatchType = FwpMatchEqual,
                ConditionValue = new FwpConditionValue0 { Type = FwpV6AddrMask, Value = ptr },
            });
        }

        private IntPtr Alloc<T>(T value) where T : struct
        {
            var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
            Marshal.StructureToPtr(value, ptr, false);
            _allocations.Add(ptr);
            return ptr;
        }

        private static uint HostOrderV4(string dotted)
        {
            var b = System.Net.IPAddress.Parse(dotted).GetAddressBytes();
            return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        }

        public void Dispose()
        {
            foreach (var ptr in _allocations) Marshal.FreeHGlobal(ptr);
            _allocations.Clear();
        }
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

    [StructLayout(LayoutKind.Sequential)]
    private struct FwpV4AddrAndMask
    {
        public uint Address;
        public uint Mask;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct FwpV6AddrAndMask
    {
        public ulong Low;
        public ulong High;
        public byte PrefixLength;
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

    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint FwpmGetAppIdFromFileName0(string fileName, out IntPtr appId);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    private static extern uint FwpmFreeMemory0(ref IntPtr p);
}
