using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace Se7enPro.Services;

internal sealed class WfpDnsLeakProtector : IDisposable
{
    private const uint RpcCAuthnWinnt = 10;
    private const uint SessionFlagDynamic = 1;

    private const uint FwpActionBlock = 0x1001;
    private const uint FwpActionPermit = 0x1002;

    private const int FwpUint16 = 2;
    private const int FwpUint64 = 4;
    private const int FwpByteBlobType = 12;
    private const int FwpV4AddrMask = 0x100;
    private const int FwpV6AddrMask = 0x101;
    private const int FwpMatchEqual = 0;

    private const ulong DnsBlockWeight = 0xFFFFFFE0;
    private const ulong DnsPermitWeight = 0xFFFFFFF0;

    private static readonly Guid LayerAleAuthConnectV4 =
        new("c38d57d1-05a7-4c33-904f-7fbceee60e82");

    private static readonly Guid LayerAleAuthConnectV6 =
        new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4");

    private static readonly Guid ConditionAleAppId =
        new("d78e1e87-8644-4ea5-9437-d809ecefc971");

    private static readonly Guid ConditionIpRemoteAddress =
        new("b235ae9a-1d64-49b8-a44c-5ff3d9095045");

    private static readonly Guid ConditionIpRemotePort =
        new("c35a604d-d22b-4e1a-91b4-68f674ee674b");

    private static readonly Guid SublayerKey =
        new("7b1e4c2a-8d39-4a7f-9b2c-4d6e8f0a1b3c");

    private readonly object _sync = new();
    private IntPtr _engine;
    private volatile bool _armed;

    public bool IsArmed => _armed;
    public string? LastError { get; private set; }

    public bool Arm()
    {
        lock (_sync)
        {
            if (_armed) return true;
            LastError = null;
            try
            {
                CloseEngine();
                OpenEngine();
                AddSublayer();

                using (var condBlockV4 = new ConditionBuilder())
                {
                    condBlockV4.AddPort53();
                    AddFilter(LayerAleAuthConnectV4, "Se7en DNS Leak block port 53 v4", FwpActionBlock,
                              DnsBlockWeight, condBlockV4.Conditions);
                }
                using (var condBlockV6 = new ConditionBuilder())
                {
                    condBlockV6.AddPort53();
                    AddFilter(LayerAleAuthConnectV6, "Se7en DNS Leak block port 53 v6", FwpActionBlock,
                              DnsBlockWeight, condBlockV6.Conditions);
                }

                using (var condLoopV4 = new ConditionBuilder())
                {
                    condLoopV4.AddV4("127.0.0.0", "255.0.0.0");
                    AddFilter(LayerAleAuthConnectV4, "Se7en DNS Leak loopback permit v4", FwpActionPermit,
                              DnsPermitWeight, condLoopV4.Conditions);
                }
                using (var condLoopV6 = new ConditionBuilder())
                {
                    condLoopV6.AddV6("::1", 128);
                    AddFilter(LayerAleAuthConnectV6, "Se7en DNS Leak loopback permit v6", FwpActionPermit,
                              DnsPermitWeight, condLoopV6.Conditions);
                }

                var permittedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var ownExe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(ownExe)) permittedPaths.Add(ownExe);
                foreach (var enginePath in EngineProcessNames.ResolveEngineExecutablePaths())
                {
                    if (!string.IsNullOrEmpty(enginePath)) permittedPaths.Add(enginePath);
                }

                foreach (var path in permittedPaths)
                {
                    if (File.Exists(path))
                    {
                        AddAppPermit(LayerAleAuthConnectV4, path);
                        AddAppPermit(LayerAleAuthConnectV6, path);
                    }
                }

                _armed = true;
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
                Name = Marshal.StringToHGlobalUni("Se7en Pro DNS Leak Protector Session"),
                Description = IntPtr.Zero,
            },
            Flags = SessionFlagDynamic,
            TxnWaitTimeoutInMSec = 0,
            ProcessId = 0,
            Sid = IntPtr.Zero,
            Username = IntPtr.Zero,
            KernelMode = false,
        };

        try
        {
            var rc = FwpmEngineOpen0(IntPtr.Zero, RpcCAuthnWinnt, IntPtr.Zero, ref session, out _engine);
            if (rc != 0)
            {
                throw new Win32Exception((int)rc, "FwpmEngineOpen0 failed");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(session.DisplayData.Name);
        }
    }

    private void CloseEngine()
    {
        if (_engine != IntPtr.Zero)
        {
            try { FwpmEngineClose0(_engine); } catch { }
            _engine = IntPtr.Zero;
        }
    }

    private void AddSublayer()
    {
        var sublayer = new FwpmSubLayer0
        {
            SubLayerKey = SublayerKey,
            DisplayData = new FwpmDisplayData0
            {
                Name = Marshal.StringToHGlobalUni("Se7en Pro DNS Leak Sublayer"),
                Description = IntPtr.Zero,
            },
            Flags = 0,
            ProviderKey = IntPtr.Zero,
            ProviderData = default,
            Weight = 0x8100,
        };

        try
        {
            var rc = FwpmSubLayerAdd0(_engine, ref sublayer, IntPtr.Zero);
            if (rc != 0 && rc != 0x80320002)
            {
                throw new Win32Exception((int)rc, "FwpmSubLayerAdd0 failed");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(sublayer.DisplayData.Name);
        }
    }

    private bool AddAppPermit(Guid layer, string exePath)
    {
        var rc = FwpmGetAppIdFromFileName0(exePath, out var appId);
        if (rc != 0 || appId == IntPtr.Zero) return false;

        try
        {
            using var conditions = new ConditionBuilder();
            conditions.AddAppId(appId);
            AddFilter(layer, "Se7en DNS Leak allow " + Path.GetFileName(exePath),
                      FwpActionPermit, DnsPermitWeight, conditions.Conditions);
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

    private sealed class ConditionBuilder : IDisposable
    {
        private readonly List<FwpmFilterCondition0> _conditions = new();
        private readonly List<IntPtr> _allocations = new();

        public IReadOnlyList<FwpmFilterCondition0> Conditions => _conditions;

        public void AddPort53()
        {
            _conditions.Add(new FwpmFilterCondition0
            {
                FieldKey = ConditionIpRemotePort,
                MatchType = FwpMatchEqual,
                ConditionValue = new FwpConditionValue0 { Type = FwpUint16, Value = (IntPtr)53 },
            });
        }

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

        public void AddV6(string address, byte prefix)
        {
            var bytes = System.Net.IPAddress.Parse(address).GetAddressBytes();
            var value = new FwpV6AddrAndMask
            {
                Addr = bytes,
                PrefixLength = prefix,
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
        public ulong Context;
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
        [MarshalAs(UnmanagedType.Bool)] public bool KernelMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FwpmSubLayer0
    {
        public Guid SubLayerKey;
        public FwpmDisplayData0 DisplayData;
        public uint Flags;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct FwpV6AddrAndMask
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] Addr;
        public byte PrefixLength;
    }

    [DllImport("fwpuclnt.dll", SetLastError = true)]
    private static extern uint FwpmEngineOpen0(
        IntPtr serverName,
        uint authnService,
        IntPtr authIdentity,
        ref FwpmSession0 session,
        out IntPtr engineHandle);

    [DllImport("fwpuclnt.dll", SetLastError = true)]
    private static extern uint FwpmEngineClose0(IntPtr engineHandle);

    [DllImport("fwpuclnt.dll", SetLastError = true)]
    private static extern uint FwpmSubLayerAdd0(
        IntPtr engineHandle,
        ref FwpmSubLayer0 subLayer,
        IntPtr sd);

    [DllImport("fwpuclnt.dll", SetLastError = true)]
    private static extern uint FwpmFilterAdd0(
        IntPtr engineHandle,
        ref FwpmFilter0 filter,
        IntPtr sd,
        out ulong id);

    [DllImport("fwpuclnt.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint FwpmGetAppIdFromFileName0(
        string fileName,
        out IntPtr appId);

    [DllImport("fwpuclnt.dll", SetLastError = true)]
    private static extern void FwpmFreeMemory0(ref IntPtr p);
}
