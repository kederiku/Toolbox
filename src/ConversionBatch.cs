using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace AtelierPC
{
    internal sealed class ConversionBatch
    {
        readonly BatchFile[] files;
        readonly string folder, extension;
        readonly HashSet<string> occupied;
        readonly Action<int, double> report;
        internal readonly CancellationToken Token;
        internal int Saved { get; private set; }
        internal int Failed { get; private set; }

        internal ConversionBatch(BatchFile[] files, string folder, string extension, CancellationToken token, Action<int, double> report)
        {
            this.files = files;
            this.folder = folder;
            this.extension = extension;
            this.report = report;
            Token = token;
            occupied = new HashSet<string>(files.Select(f => f.Source), StringComparer.OrdinalIgnoreCase);
        }

        internal string Summary
        {
            get
            {
                string text = string.Format("{0} sur {1} enregistrés", Saved, files.Length);
                if (Failed > 0) text += string.Format(" · {0} échec{1}", Failed, Failed > 1 ? "s" : "");
                int remaining = files.Length - Saved - Failed;
                if (remaining > 0) text += string.Format(" · {0} non traité{1}", remaining, remaining > 1 ? "s" : "");
                return text;
            }
        }

        string NextDestination(string source)
        {
            string stem = Path.GetFileNameWithoutExtension(source);
            if (string.IsNullOrEmpty(stem)) stem = "Conversion";
            for (int suffix = 1; ; suffix++)
            {
                Token.ThrowIfCancellationRequested();
                string name = suffix == 1 ? stem : stem + " (" + suffix + ")";
                string path = Path.Combine(folder, name + "." + extension);
                if (!occupied.Contains(path) && !File.Exists(path) && !Directory.Exists(path))
                    return path;
            }
        }

        internal async Task ConvertAsync<TEngine>(Func<TEngine> createEngine, Action<TEngine, string, string, IProgress<FileProgress>> convert)
            where TEngine : IDisposable
        {
            int current = 0;
            try
            {
                using (var engine = await Task.Run(createEngine, Token))
                {
                    for (; current < files.Length; current++)
                    {
                        Token.ThrowIfCancellationRequested();
                        int index = current;
                        var file = files[index];
                        file.Set(BatchFileState.Running, "Lecture", "");
                        report(index, 0);
                        IProgress<FileProgress> progress = new Progress<FileProgress>(update => {
                            if (index != current || file.State != BatchFileState.Running || Token.IsCancellationRequested) return;
                            file.Report(update);
                            if (update.Value > 0) report(index, file.Progress);
                        });
                        try
                        {
                            string destination = await Task.Run(() => {
                                string path = NextDestination(file.Source);
                                convert(engine, file.Source, path, progress);
                                return path;
                            }, Token);
                            occupied.Add(destination);
                            Saved++;
                            file.Set(BatchFileState.Saved, "Enregistré", "Copie enregistrée · " + Path.GetFileName(destination));
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            Failed++;
                            file.Set(BatchFileState.Failed, "Échec", ex.Message);
                        }
                        report(index, 100);
                    }
                }
            }
            finally
            {
                for (; current < files.Length; current++)
                {
                    var file = files[current];
                    if (file.State == BatchFileState.Running && Token.IsCancellationRequested)
                        file.Set(BatchFileState.Canceled, "Annulé", "");
                    else if (file.State == BatchFileState.Waiting || file.State == BatchFileState.Running)
                        file.Set(BatchFileState.Skipped, "Non traité", "");
                }
            }
        }

        sealed class DialogOwner : Forms.IWin32Window
        {
            public IntPtr Handle { get; private set; }
            internal DialogOwner(Window window) { Handle = new WindowInteropHelper(window).EnsureHandle(); }
        }

        internal static string ChooseFolder(Window owner, string initial, string description = "Choisissez le dossier où enregistrer les fichiers convertis.")
        {
            using (var dialog = new Forms.FolderBrowserDialog {
                Description = description,
                SelectedPath = initial, ShowNewFolderButton = true
            })
                return dialog.ShowDialog(new DialogOwner(owner)) == Forms.DialogResult.OK ? dialog.SelectedPath : null;
        }
    }
}
