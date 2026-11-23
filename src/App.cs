using System;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

[assembly: AssemblyTitle("Toolbox")]
[assembly: AssemblyProduct("Toolbox")]
[assembly: AssemblyDescription("Dépannage guidé de Windows, du matériel et du réseau")]
[assembly: AssemblyVersion("1.6.0.0")]
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
            return app.Run(shell.Window);
        }
    }
}
