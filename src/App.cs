using System;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Threading;
using System.Windows;

[assembly: AssemblyTitle("Toolbox")]
[assembly: AssemblyProduct("Toolbox")]
[assembly: AssemblyDescription("Dépannage guidé de Windows, du matériel et du réseau")]
[assembly: AssemblyVersion("1.7.0.0")]
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace AtelierPC
{
    internal static class App
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == PdfWatermarkPreview.Command) return PdfWatermarkPreview.RunWorker();
            Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            if (args.Length == 2 && args[0] == UsbCleanup.Command) return UsbCleanup.Run(args[1]);
            if (args.Length > 0 && args[0] == UsbCleanup.Command) return (int)CleanupResult.Failed;
            var app = new Application();
            var shell = new Shell(DeviceReader.Scan, toolPreferencesPath: ToolPreferences.LocalPath);
            AcknowledgeUpdateOnFirstRender(shell.Window);
            new UpdateNotice(shell.Window).Start();
            return app.Run(shell.Window);
        }

        internal static void AcknowledgeUpdateOnFirstRender(Window window)
        {
            string name = Environment.GetEnvironmentVariable(UpdateFiles.ReadyVariable);
            if (name == null) return;
            Environment.SetEnvironmentVariable(UpdateFiles.ReadyVariable, null);
            Guid id;
            if (!name.StartsWith(UpdateFiles.ReadyPrefix, StringComparison.Ordinal) ||
                !Guid.TryParseExact(name.Substring(UpdateFiles.ReadyPrefix.Length), "N", out id)) return;
            EventHandler rendered = null;
            rendered = delegate {
                window.ContentRendered -= rendered;
                try { using (var ready = EventWaitHandle.OpenExisting(name, EventWaitHandleRights.Modify)) ready.Set(); }
                catch (WaitHandleCannotBeOpenedException) { }
                catch (UnauthorizedAccessException) { }
            };
            window.ContentRendered += rendered;
        }
    }
}
