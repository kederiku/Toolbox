using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace AtelierPC
{
    internal static class DuplicateStreams
    {
        const int NoMoreStreams = 38, StreamsUnsupported = 87;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct StreamInfo
        {
            internal long Length;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)]
            internal string Name;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr FindFirstStreamW(string file, int level, out StreamInfo info, int flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool FindNextStreamW(IntPtr search, out StreamInfo info);

        [DllImport("kernel32.dll")]
        static extern bool FindClose(IntPtr search);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFileW(string path, uint access, FileShare share, IntPtr security,
            FileMode mode, uint flags, IntPtr template);

        internal static string Fingerprint(string path, CancellationToken token)
        {
            string contentStreamHash;
            bool hasDownloadInfo;
            return Fingerprint(path, token, out contentStreamHash, out hasDownloadInfo);
        }

        internal static string Fingerprint(string path, CancellationToken token, out string contentStreamHash, out bool hasDownloadInfo)
        {
            contentStreamHash = "";
            hasDownloadInfo = false;
            token.ThrowIfCancellationRequested();
            StreamInfo info;
            IntPtr search = FindFirstStreamW(path, 0, out info, 0);
            if (search == new IntPtr(-1))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == NoMoreStreams || error == StreamsUnsupported) return "";
                throw new Win32Exception(error);
            }
            try
            {
                SortedDictionary<string, string> streams = null;
                do
                {
                    token.ThrowIfCancellationRequested();
                    if (info.Name == "::$DATA") continue;
                    using (var handle = CreateFileW(path + info.Name, 0x80000000, FileShare.Read, IntPtr.Zero, FileMode.Open, 0, IntPtr.Zero))
                    {
                        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                        using (var file = new FileStream(handle, FileAccess.Read))
                        {
                            if (streams == null) streams = new SortedDictionary<string, string>(StringComparer.Ordinal);
                            streams.Add(info.Name, DuplicateScanner.Hash(file, token));
                        }
                    }
                }
                while (FindNextStreamW(search, out info));
                int error = Marshal.GetLastWin32Error();
                if (error != NoMoreStreams) throw new Win32Exception(error);
                if (streams == null) return "";
                string fullHash = HashStreams(streams, token);
                hasDownloadInfo = streams.Keys.Any(IsDownloadStream);
                contentStreamHash = hasDownloadInfo ? HashStreams(streams.Where(p => !IsDownloadStream(p.Key)), token) : fullHash;
                return fullHash;
            }
            finally { FindClose(search); }
        }

        static bool IsDownloadStream(string name)
        {
            return string.Equals(name, ":Zone.Identifier:$DATA", StringComparison.OrdinalIgnoreCase);
        }

        static string HashStreams(IEnumerable<KeyValuePair<string, string>> streams, CancellationToken token)
        {
            // Préfixer les noms par leur longueur évite toute ambiguïté entre plusieurs flux.
            using (var data = new MemoryStream())
            using (var writer = new BinaryWriter(data, Encoding.Unicode, true))
            {
                foreach (var pair in streams) { writer.Write(pair.Key); writer.Write(pair.Value); }
                writer.Flush();
                if (data.Length == 0) return "";
                data.Position = 0;
                return DuplicateScanner.Hash(data, token);
            }
        }
    }
}
