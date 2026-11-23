using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace AtelierPC
{
    internal static class DuplicateRecycleBin
    {
        internal static bool ContainsPath(string path)
        {
            string full = Path.GetFullPath(path);
            string root = Path.GetPathRoot(full);
            string bin = Path.Combine(root, "$Recycle.Bin");
            return full.Equals(bin, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(bin + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            void BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, out ITransferSource transfer);
            void GetParent(out IShellItem parent);
        }

        // Ordre des méthodes de ShObjIdl_core.h jusqu’à RecycleItem.
        [ComImport, Guid("00ADB003-BDE9-45C6-8E29-D09F9353E108"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ITransferSource
        {
            void Advise(IntPtr sink, out uint cookie);
            void Unadvise(uint cookie);
            void SetProperties(IntPtr changes);
            void OpenItem(IShellItem item, uint flags, ref Guid iid, out IntPtr opened);
            void MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, uint flags, out IShellItem moved);
            [PreserveSig] int RecycleItem(IShellItem item, IShellItem bin, uint flags, out IShellItem recycled);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, out IShellItem item);

        [DllImport("shell32.dll", PreserveSig = false)]
        static extern void SHGetKnownFolderItem(ref Guid folder, uint flags, IntPtr token, ref Guid iid, out IShellItem item);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool GetVolumeNameForVolumeMountPoint(string root, StringBuilder name, uint size);

        static long Setting(RegistryKey key, string path, string name, int defaultValue)
        {
            using (var settings = key.OpenSubKey(path))
            {
                object value = settings == null ? null : settings.GetValue(name);
                if (value == null) return defaultValue;
                if (!(value is int)) throw new IOException("Le réglage de la Corbeille est illisible.");
                return (uint)(int)value;
            }
        }

        static void CheckVolume(string path)
        {
            string root = Path.GetPathRoot(path);
            if (root == null || root.Length != 3)
                throw new IOException("La Corbeille est indisponible pour cet emplacement.");
            var drive = new DriveInfo(root);
            if (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable)
                throw new IOException("La Corbeille est indisponible pour ce type de volume.");

            const string policies = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";
            const string bins = @"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket";
            var volume = new StringBuilder(64);
            if (!GetVolumeNameForVolumeMountPoint(root, volume, (uint)volume.Capacity))
                throw new IOException("Windows ne peut pas identifier le volume de la Corbeille.");
            string volumeId = volume.ToString().Substring(@"\\?\Volume".Length).TrimEnd('\\');
            string settings = bins + @"\Volume\" + volumeId;
            long size = new FileInfo(path).Length;
            using (var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default))
            using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default))
            {
                if (Setting(user, policies, "NoRecycleFiles", 0) != 0 || Setting(machine, policies, "NoRecycleFiles", 0) != 0 ||
                    Setting(user, settings, "NukeOnDelete", 0) != 0 || Setting(user, bins, "NukeOnDelete", 0) != 0)
                    throw new IOException("La Corbeille est désactivée sur ce volume.");
                long maxMb = Setting(user, settings, "MaxCapacity", -1);
                long percent = Setting(user, policies, "RecycleBinSize", -1);
                if (percent > 100) throw new IOException("La limite de la Corbeille est invalide.");
                if (maxMb == 0 || (maxMb > 0 && size > maxMb * 1048576L) || percent == 0 ||
                    (percent > 0 && size > drive.TotalSize / 100 * percent))
                    throw new IOException("La taille du fichier dépasse la capacité autorisée de la Corbeille.");
            }
        }

        internal static void Move(string path, Action verify)
        {
            CheckVolume(path);
            Guid shellId = typeof(IShellItem).GUID, transferId = typeof(ITransferSource).GUID;
            var transferHandler = new Guid("D5E346A1-F753-4932-B403-4574800E2498");
            var recycleBin = new Guid("B7534046-3ECB-4C18-BE4E-64CD4CB7D6AC");
            IShellItem source = null, parent = null, bin = null, recycled = null;
            ITransferSource transfer = null;
            try
            {
                SHCreateItemFromParsingName(path, IntPtr.Zero, ref shellId, out source);
                source.GetParent(out parent);
                parent.BindToHandler(IntPtr.Zero, ref transferHandler, ref transferId, out transfer);
                SHGetKnownFolderItem(ref recycleBin, 0, IntPtr.Zero, ref shellId, out bin);
                verify();
                // Cette méthode ne propose que le recyclage et renvoie l’élément dans la Corbeille.
                int status = transfer.RecycleItem(source, bin, 0, out recycled);
                Marshal.ThrowExceptionForHR(status);
                if (recycled == null) throw new IOException("Windows n’a pas confirmé le déplacement dans la Corbeille.");
            }
            finally
            {
                if (recycled != null) Marshal.ReleaseComObject(recycled);
                if (transfer != null) Marshal.ReleaseComObject(transfer);
                if (bin != null) Marshal.ReleaseComObject(bin);
                if (parent != null) Marshal.ReleaseComObject(parent);
                if (source != null) Marshal.ReleaseComObject(source);
            }
        }
    }
}
