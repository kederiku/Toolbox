using System;
using System.IO;
using System.Runtime.InteropServices;

namespace AtelierPC
{
    internal sealed class PerformanceSample
    {
        internal double? Cpu, DiskActive;
        internal PerformanceCapacity Memory;
    }

    internal sealed class PerformanceSampler : IDisposable
    {
        IntPtr query;
        readonly IntPtr cpu, diskIdle;
        bool primed;
        internal static readonly string WindowsVolume = Path.GetPathRoot(Environment.SystemDirectory).TrimEnd('\\');

        internal PerformanceSampler(bool includeDisk)
        {
            IntPtr handle;
            if (PdhOpenQuery(null, UIntPtr.Zero, out handle) != 0) return;
            query = handle;
            // The total includes all Windows processor groups.
            cpu = Add(@"\Processor Information(_Total)\% Processor Time");
            if (includeDisk) diskIdle = Add(@"\LogicalDisk(" + WindowsVolume + @")\% Idle Time");
        }

        IntPtr Add(string path)
        {
            IntPtr counter;
            return PdhAddEnglishCounter(query, path, UIntPtr.Zero, out counter) == 0 ? counter : IntPtr.Zero;
        }

        internal PerformanceSample Read()
        {
            bool collected = query != IntPtr.Zero && PdhCollectQueryData(query) == 0;
            bool ready = collected && primed;
            primed = collected;
            var memory = new MemoryStatus { Size = (uint)Marshal.SizeOf(typeof(MemoryStatus)) };
            return new PerformanceSample {
                Cpu = ready ? Percent(cpu) : null,
                DiskActive = ready ? 100 - Percent(diskIdle) : null,
                Memory = GlobalMemoryStatusEx(ref memory)
                    ? PerformanceCapacity.FromBytes((long)memory.AvailablePhysical, (long)memory.TotalPhysical) : null
            };
        }

        static double? Percent(IntPtr counter)
        {
            CounterValue value;
            const uint pdhFormatDouble = 0x200;
            if (counter == IntPtr.Zero || PdhGetFormattedCounterValue(counter, pdhFormatDouble, IntPtr.Zero, out value) != 0
                || value.Status > 1 || !(value.Number >= 0 && value.Number <= 100)) return null;
            return value.Number;
        }

        public void Dispose()
        {
            if (query != IntPtr.Zero) PdhCloseQuery(query);
            query = IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct CounterValue { internal uint Status; internal double Number; }
        [StructLayout(LayoutKind.Sequential)]
        struct MemoryStatus
        {
            internal uint Size, Load;
            internal ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile,
                TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        static extern uint PdhOpenQuery(string source, UIntPtr userData, out IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        static extern uint PdhAddEnglishCounter(IntPtr query, string path, UIntPtr userData, out IntPtr counter);
        [DllImport("pdh.dll", ExactSpelling = true)]
        static extern uint PdhCollectQueryData(IntPtr query);
        [DllImport("pdh.dll", ExactSpelling = true)]
        static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type, out CounterValue value);
        [DllImport("pdh.dll", ExactSpelling = true)]
        static extern uint PdhCloseQuery(IntPtr query);
        [DllImport("kernel32.dll")]
        static extern bool GlobalMemoryStatusEx(ref MemoryStatus memory);
    }
}
