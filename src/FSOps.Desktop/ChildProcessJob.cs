using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FSOps.Desktop;

/// <summary>
/// A Windows Job Object configured with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>, used to
/// guarantee that FSOps.Server never outlives the window that started it.
///
/// <para>
/// Killing the child in a form-closing handler covers the tidy case and nothing else. If the shell
/// is killed from Task Manager, hits a stack overflow, or is torn down at Windows shutdown, no
/// managed code of ours runs at all - and the user is left with an orphaned FSOps.Server holding
/// port 5977 and their SQLite database open, with no window to close and no obvious way to get rid
/// of it. A job object moves that guarantee into the kernel: every handle to the job is closed when
/// the process dies, however it dies, and the kernel then terminates everything still assigned to
/// it. This is the only mechanism on Windows that survives an abnormal parent exit, which is why
/// the child-process design is acceptable at all.
/// </para>
///
/// <para>
/// The handle is deliberately created non-inheritable (null security attributes) so the server
/// cannot itself hold the job open and defeat the kill.
/// </para>
/// </summary>
internal sealed class ChildProcessJob : IDisposable
{
    private readonly SafeJobHandle _handle;
    private bool _disposed;

    private ChildProcessJob(SafeJobHandle handle) => _handle = handle;

    /// <summary>
    /// Creates the job, or returns <c>null</c> if the OS refuses. A missing job object is not fatal
    /// - the shell still kills the child explicitly on close - so callers degrade to the tidy-exit
    /// guarantee only rather than failing to start.
    /// </summary>
    public static ChildProcessJob? Create()
    {
        var handle = NativeMethods.CreateJobObjectW(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            return null;
        }

        var info = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new NativeMethods.JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
            },
        };

        var size = Marshal.SizeOf<NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, fDeleteOld: false);
            if (!NativeMethods.SetInformationJobObject(
                    handle,
                    NativeMethods.JobObjectExtendedLimitInformation,
                    buffer,
                    (uint)size))
            {
                handle.Dispose();
                return null;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return new ChildProcessJob(handle);
    }

    /// <summary>
    /// Assigns an already-started process to the job. There is an unavoidable window between
    /// <see cref="Process.Start()"/> and this call in which a grandchild could escape, because
    /// System.Diagnostics.Process cannot create a process suspended. It does not matter here:
    /// FSOps.Server spawns no child processes of its own, and the process being assigned is the one
    /// we are about to hold the only handle to.
    /// </summary>
    public bool Assign(Process process)
    {
        try
        {
            return NativeMethods.AssignProcessToJobObject(_handle, process.Handle);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // The process exited between Start() and here - there is nothing to assign, and the
            // caller's own "did it die immediately?" check reports that far more usefully.
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Closing the last handle is what kills the job's processes. Nothing else to do.
        _handle.Dispose();
    }

    private sealed class SafeJobHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
    }

    private static class NativeMethods
    {
        public const int JobObjectExtendedLimitInformation = 9;
        public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeJobHandle CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetInformationJobObject(
            SafeJobHandle hJob,
            int jobObjectInformationClass,
            IntPtr lpJobObjectInformation,
            uint cbJobObjectInformationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AssignProcessToJobObject(SafeJobHandle hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
        public struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }
    }
}
