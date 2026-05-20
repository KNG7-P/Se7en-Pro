using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace PsiphonUI.Services;

[SupportedOSPlatform("windows")]
public sealed class ChildProcessGuard : IChildProcessGuard, IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob,
        int JobObjectInfoClass,
        IntPtr lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    private readonly ILogger<ChildProcessGuard> _logger;
    private readonly object _lock = new();
    private IntPtr _handle;
    private bool _initFailed;

    public ChildProcessGuard(ILogger<ChildProcessGuard> logger)
    {
        _logger = logger;
        TryCreateJob();
    }

    private void TryCreateJob()
    {
        try
        {
            var handle = CreateJobObjectW(IntPtr.Zero, null);
            if (handle == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObjectW");
            }

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
                },
            };
            var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var infoPtr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, infoPtr, fDeleteOld: false);
                if (!SetInformationJobObject(
                        handle, JobObjectExtendedLimitInformation, infoPtr, (uint)size))
                {
                    var err = Marshal.GetLastWin32Error();
                    CloseHandle(handle);
                    throw new Win32Exception(err, "SetInformationJobObject");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(infoPtr);
            }

            _handle = handle;
            _logger.LogInformation("Child-process job created (KILL_ON_JOB_CLOSE)");
        }
        catch (Exception ex)
        {
            _initFailed = true;
            _logger.LogWarning(ex, "Could not create child-process job; orphan-process protection disabled");
        }
    }

    public void Adopt(Process process)
    {
        if (process is null) return;

        lock (_lock)
        {
            if (_initFailed || _handle == IntPtr.Zero) return;

            try
            {
                if (!AssignProcessToJobObject(_handle, process.Handle))
                {
                    var err = Marshal.GetLastWin32Error();
                    _logger.LogWarning(
                        "AssignProcessToJobObject failed for pid {Pid}: Win32 error {Err}",
                        SafePid(process),
                        err);
                    return;
                }

                _logger.LogInformation(
                    "Adopted child pid {Pid} into kill-on-close job",
                    SafePid(process));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Adopting pid {Pid} into job threw", SafePid(process));
            }
        }
    }

    private static int SafePid(Process p)
    {
        try { return p.Id; }
        catch { return -1; }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_handle != IntPtr.Zero)
            {

                CloseHandle(_handle);
                _handle = IntPtr.Zero;
            }
        }
    }
}
