using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AtelierPC
{
    // yt-dlp peut démarrer Deno ou FFmpeg. Windows arrête aussi ces enfants à la fermeture.
    internal sealed class YoutubeProcessGroup : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        struct BasicLimits
        {
            public long ProcessTime, JobTime;
            public uint Flags;
            public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
            public uint ActiveProcesses;
            public UIntPtr Affinity;
            public uint Priority, Scheduling;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct ExtendedLimits
        {
            public BasicLimits Basic;
            public ulong ReadCount, WriteCount, OtherCount, ReadBytes, WriteBytes, OtherBytes;
            public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref ExtendedLimits limits, int size);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
        [DllImport("kernel32.dll")]
        static extern bool TerminateJobObject(SafeFileHandle job, uint code);

        readonly SafeFileHandle handle;

        internal YoutubeProcessGroup()
        {
            handle = CreateJobObject(IntPtr.Zero, null);
            if (handle.IsInvalid) throw new Win32Exception();
            var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } }; // KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(handle, 9, ref limits, Marshal.SizeOf(typeof(ExtendedLimits))))
            {
                var error = new Win32Exception();
                handle.Dispose();
                throw error;
            }
        }

        internal void Attach(Process process)
        {
            if (!AssignProcessToJobObject(handle, process.Handle) && !process.HasExited) throw new Win32Exception();
        }

        internal void Stop() { TerminateJobObject(handle, 1); }
        public void Dispose() { handle.Dispose(); }
    }
}
