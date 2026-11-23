using System;
using System.CodeDom.Compiler;
using System.IO;

namespace AtelierPC
{
    internal static partial class Tests
    {
        static void CheckUpdateInstaller()
        {
            string root = Path.Combine(Path.GetTempPath(), "Toolbox mise à jour & (test) '" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string target = Path.Combine(root, "Ma boîte [portable] $.exe");
                string old = InstallerFixture(root, "1.0.0.0", 0, false);
                string newer = InstallerFixture(root, "2.0.0.0", 0, true);
                string broken = InstallerFixture(root, "3.0.0.0", 4, false);
                string silent = InstallerFixture(root, "4.0.0.0", 0, false);
                File.Copy(old, target);
                string originalHash = UpdateFiles.HashFile(target);
                File.SetAttributes(target, FileAttributes.ReadOnly);
                bool rejected = false;
                try { UpdateInstaller.CreateStage(target); }
                catch (IOException) { rejected = true; }
                finally { File.SetAttributes(target, FileAttributes.Normal); }
                Check(rejected, "La mise à jour manuelle est proposée avant de télécharger pour un exécutable en lecture seule");
                string stage = UpdateInstaller.CreateStage(target);
                Check(!File.Exists(stage) && Directory.GetParent(Path.GetDirectoryName(stage)).FullName == root,
                    "Le fichier temporaire sera créé sur le volume de la Toolbox portable");
                File.Copy(newer, stage);
                UpdateInstaller.CleanStage(target);
                Check(File.Exists(target), "Le nettoyage ignore les chemins externes au téléchargement");
                string nextHash = UpdateFiles.HashFile(stage);
                Check(InstallerFailure(target, stage, originalHash, originalHash) && UpdateFiles.HashFile(target) == originalHash,
                    "Une empreinte de téléchargement incorrecte conserve la version installée");
                Check(InstallerFailure(target, stage, nextHash, nextHash) && UpdateFiles.HashFile(target) == originalHash,
                    "Un changement concurrent de la version installée annule le remplacement");
                UpdateFiles.Install(target, stage, nextHash, originalHash);
                Check(File.Exists(target + ".started") && UpdateFiles.HashFile(target) == nextHash, "La mise à jour redémarre depuis le chemin d'origine");
                UpdateInstaller.CleanStage(stage);
                Check(!Directory.Exists(Path.GetDirectoryName(stage)), "Le répertoire temporaire vide est supprimé");
                stage = UpdateInstaller.CreateStage(target);
                File.Copy(old, stage);
                Check(InstallerFailure(target, stage, originalHash, nextHash) && UpdateFiles.HashFile(target) == nextHash,
                    "Une version antérieure ne peut pas remplacer la Toolbox");
                File.Copy(broken, stage, true);
                Check(InstallerFailure(target, stage, UpdateFiles.HashFile(stage), nextHash) && UpdateFiles.HashFile(target) == nextHash,
                    "Un échec de redémarrage restaure exactement l'exécutable précédent");
                stage = FreshInstallerStage(target, stage, silent);
                Check(InstallerFailure(target, stage, UpdateFiles.HashFile(stage), nextHash) && UpdateFiles.HashFile(target) == nextHash,
                    "Une fermeture sans signal de disponibilité restaure la version précédente");
                UpdateInstaller.CleanStage(stage);
            }
            finally { Directory.Delete(root, true); }
        }

        static bool InstallerFailure(string target, string staged, string digest, string previous)
        {
            try { UpdateFiles.Install(target, staged, digest, previous); return false; }
            catch (IOException) { return true; }
        }

        static string FreshInstallerStage(string target, string used, string image)
        {
            UpdateInstaller.CleanStage(used);
            string staged = UpdateInstaller.CreateStage(target);
            File.Copy(image, staged);
            return staged;
        }

        static string InstallerFixture(string root, string version, int code, bool ready)
        {
            string path = Path.Combine(root, version, "Toolbox.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string ack = ready ? "System.Threading.Thread.Sleep(2500); using(var e=System.Threading.EventWaitHandle.OpenExisting(System.Environment.GetEnvironmentVariable(\"TOOLBOX_UPDATE_READY\"))){e.Set();}" : "";
            string source = "using System.IO; using System.Reflection; [assembly:AssemblyVersion(\"" + version +
                "\")] class Fixture { static int Main(){ File.WriteAllText(Assembly.GetExecutingAssembly().Location + \".started\", \"ok\"); " + ack + "return " + code + "; } }";
            using (var provider = CodeDomProvider.CreateProvider("CSharp"))
            {
                var options = new CompilerParameters { GenerateExecutable = true, OutputAssembly = path, CompilerOptions = "/target:winexe" };
                CompilerResults result = provider.CompileAssemblyFromSource(options, source);
                if (result.Errors.HasErrors) throw new Exception(result.Errors[0].ToString());
            }
            return path;
        }
    }
}
