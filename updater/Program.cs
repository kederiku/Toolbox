using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;

namespace AtelierPC
{
    internal static class UpdateWorker
    {
        [STAThread]
        static int Main()
        {
            string staged = null;
            bool committed = false;
            try
            {
                using (var identity = WindowsIdentity.GetCurrent())
                    if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                        throw new IOException("Relancez Toolbox sans les droits administrateur pour la mettre à jour.");

                int id = Int32.Parse(Console.ReadLine(), CultureInfo.InvariantCulture);
                string target = DecodePath(Console.ReadLine());
                staged = DecodePath(Console.ReadLine());
                string expectedDigest = Console.ReadLine();
                string installedDigest = Console.ReadLine();
                using (var parent = Process.GetProcessById(id))
                {
                    // Le handle reste ouvert même si Windows réutilise ce numéro de processus.
                    if (parent.Handle == IntPtr.Zero || !UpdateFiles.Same(parent.MainModule.FileName, target))
                        throw new IOException("La Toolbox à remplacer doit être le programme en cours d’exécution.");
                    UpdateFiles.StageDirectory(target, staged);
                    Console.WriteLine("READY");
                    Console.Out.Flush();
                    if (Console.ReadLine() != "INSTALL") return 0;
                    committed = true;
                    if (!parent.WaitForExit(120000))
                        throw new IOException("Toolbox est toujours ouverte. La mise à jour a été annulée.");
                }
                UpdateFiles.Install(target, staged, expectedDigest, installedDigest);
                return 0;
            }
            catch (Exception error)
            {
                if (committed)
                    MessageBox.Show(error.Message, "Mise à jour de Toolbox", MessageBoxButtons.OK, MessageBoxIcon.Error);
                else
                {
                    Console.WriteLine("ERROR " + error.Message.Replace('\r', ' ').Replace('\n', ' '));
                    Console.Out.Flush();
                }
                return 1;
            }
            finally
            {
                if (committed)
                {
                    try { UpdateFiles.RemoveStage(staged); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        static string DecodePath(string encoded)
        {
            return Path.GetFullPath(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
        }
    }
}
