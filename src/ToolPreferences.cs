using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace AtelierPC
{
    internal enum ToolPreference { HiddenFromMenu, Favorite }

    internal sealed class ToolPreferences
    {
        internal static readonly string LocalPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AtelierPC", "outils.ini");
        readonly string path;

        internal ToolPreferences(string path) { this.path = path; }

        // Each option has its own INI key, so two open windows can save unrelated choices independently.
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern uint GetPrivateProfileInt(string section, string key, int fallback, string file);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool WritePrivateProfileString(string section, string key, string value, string file);

        internal bool Read(string tool, ToolPreference option)
        {
            return path != null && GetPrivateProfileInt(tool, option.ToString(), 0, path) == 1;
        }

        internal void Write(string tool, ToolPreference option, bool value)
        {
            if (path == null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (!WritePrivateProfileString(tool, option.ToString(), value ? "1" : "0", path))
                throw new IOException("Écriture des préférences impossible.", new Win32Exception(Marshal.GetLastWin32Error()));
        }
    }
}
