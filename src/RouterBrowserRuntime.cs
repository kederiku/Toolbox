using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Web.WebView2.Core;

namespace AtelierPC
{
    internal static class RouterBrowserRuntime
    {
        const string DirectoryPrefix = "AtelierPC-WebView2Loader-";
        const string LoaderName = "WebView2Loader.dll";
        static readonly object Sync = new object();
        static Assembly core;
        static Assembly wpf;
        static bool prepared;

        // Called by the viewer's SDK-free entry point, before its typed WPF code is JIT-compiled.
        internal static void Prepare()
        {
            lock (Sync)
            {
                if (prepared) return;
                if (core == null)
                {
                    var coreAssembly = ReadAssembly("Core");
                    wpf = ReadAssembly("Wpf");
                    core = coreAssembly;
                    AppDomain.CurrentDomain.AssemblyResolve += ResolveSdkAssembly;
                }
                if (Assembly.Load(core.GetName()) != core || Assembly.Load(wpf.GetName()) != wpf)
                    throw new InvalidOperationException("Une autre copie du SDK WebView2 est déjà chargée.");

                var temp = new DirectoryInfo(Path.GetTempPath());
                using (var identity = WindowsIdentity.GetCurrent())
                {
                    var owner = identity.User;
                    RemoveAbandonedLoaders(temp, owner);
                    var security = new DirectorySecurity();
                    security.SetOwner(owner);
                    security.SetAccessRuleProtection(true, false);
                    security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None,
                        AccessControlType.Allow));
                    var directory = temp.CreateSubdirectory(DirectoryPrefix + Guid.NewGuid().ToString("N"), security);
                    string loaderPath = Path.Combine(directory.FullName, LoaderName);
                    FileStream guard = null;
                    try
                    {
                        using (var source = OpenResource(ProcessArchitecture()))
                        using (var output = new FileStream(loaderPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                            source.CopyTo(output);
                        guard = new FileStream(loaderPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        SetLoaderPath(directory.FullName);
                        AppDomain.CurrentDomain.ProcessExit += delegate {
                            guard.Dispose();
                            RemoveLoader(directory);
                        };
                        prepared = true;
                    }
                    catch
                    {
                        if (guard != null) guard.Dispose();
                        RemoveLoader(directory);
                        throw;
                    }
                }
            }
        }

        static Assembly ResolveSdkAssembly(object sender, ResolveEventArgs args)
        {
            if (args.Name == core.FullName) return core;
            if (args.Name == wpf.FullName) return wpf;
            return null;
        }

        static Assembly ReadAssembly(string name)
        {
            using (var resource = OpenResource(name))
            using (var bytes = new MemoryStream())
            {
                resource.CopyTo(bytes);
                return Assembly.Load(bytes.ToArray());
            }
        }

        static Stream OpenResource(string name)
        {
            var stream = typeof(RouterBrowserRuntime).Assembly.GetManifestResourceStream("AtelierPC.WebView2." + name + ".dll");
            if (stream == null) throw new InvalidDataException("Le SDK WebView2 intégré est incomplet.");
            return stream;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void SetLoaderPath(string directory)
        {
            CoreWebView2Environment.SetLoaderDllFolderPath(directory);
        }

        // GetSystemInfo reports the process architecture, including under Windows emulation.
        [StructLayout(LayoutKind.Explicit, Size = 48)]
        struct SystemInfo
        {
            [FieldOffset(0)] public ushort ProcessorArchitecture;
        }

        [DllImport("kernel32.dll")]
        static extern void GetSystemInfo(out SystemInfo info);

        static string ProcessArchitecture()
        {
            SystemInfo info;
            GetSystemInfo(out info);
            switch (info.ProcessorArchitecture)
            {
                case 0: return "x86";
                case 9: return "x64";
                case 12: return "arm64";
                default: throw new PlatformNotSupportedException("WebView2 nécessite un processus x86, x64 ou ARM64.");
            }
        }

        // Windows can keep a mapped DLL locked until after ProcessExit. A later viewer
        // can then remove that single file. Active sessions also keep a read-only handle.
        static void RemoveAbandonedLoaders(DirectoryInfo temp, SecurityIdentifier owner)
        {
            try
            {
                foreach (var directory in temp.EnumerateDirectories(DirectoryPrefix + "*"))
                {
                    Guid id;
                    if (!Guid.TryParseExact(directory.Name.Substring(DirectoryPrefix.Length), "N", out id)
                        || (directory.Attributes & FileAttributes.ReparsePoint) != 0
                        || directory.CreationTimeUtc > DateTime.UtcNow.AddHours(-1)) continue;
                    if (owner.Equals(directory.GetAccessControl().GetOwner(typeof(SecurityIdentifier))))
                        RemoveLoader(directory);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        static void RemoveLoader(DirectoryInfo directory)
        {
            try
            {
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) return;
                File.Delete(Path.Combine(directory.FullName, LoaderName));
                directory.Delete(false);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
