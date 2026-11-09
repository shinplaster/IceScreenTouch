// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using static IceScreenTouch.Platform.Windows.NativeMethods;

namespace IceScreenTouch.Platform.Windows;

/// <summary>
/// A job object that kills its processes when the last handle to it closes, including when this process
/// dies without cleaning up (crash, killed from Task Manager). Without it, a uxplay.exe started by the
/// bridge kept running after the bridge was killed (seen in PoC 3 and M3).
/// </summary>
public sealed class ChildProcessJob : IDisposable
{
    private nint _handle;

    /// <summary>Creates the job. Throws <see cref="Win32Exception"/> if Windows refuses.</summary>
    public ChildProcessJob()
    {
        _handle = CreateJobObject(0, null);
        if (_handle == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateJobObject failed");
        }

        var info = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose },
        };
        if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformationClass, ref info, (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            var error = Marshal.GetLastPInvokeError();
            Dispose();
            throw new Win32Exception(error, "SetInformationJobObject(KILL_ON_JOB_CLOSE) failed");
        }
    }

    /// <summary>
    /// Puts <paramref name="process"/> (and the processes it starts from now on) into the job. Throws
    /// <see cref="Win32Exception"/> if Windows refuses.
    /// </summary>
    public void Add(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        if (!AssignProcessToJobObject(_handle, process.SafeHandle))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "AssignProcessToJobObject failed");
        }
    }

    /// <summary>Closes the job, which kills every process still in it.</summary>
    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0)
        {
            _ = CloseHandle(handle);
        }
    }
}
