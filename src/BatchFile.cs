using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;

namespace AtelierPC
{
    internal enum BatchFileState { Waiting, Running, Saved, Failed, Canceled, Skipped }

    internal struct FileProgress
    {
        internal readonly string Status, Caption;
        internal readonly double Value;

        FileProgress(string status, string caption, double value)
        {
            Status = status;
            Caption = caption;
            Value = value;
        }

        internal static FileProgress Step(string name, int current, int total)
        {
            return new FileProgress(name, "Étape " + current + "/" + total,
                current == 1 ? -1 : (current - 1) * 100.0 / total);
        }

        internal static FileProgress Conversion(double percent)
        {
            int value = (int)Math.Max(0, Math.Min(99, Math.Floor(percent)));
            return new FileProgress("Conversion", value + " %", value);
        }

        internal static FileProgress Activity(string name)
        {
            return new FileProgress(name, "", -1);
        }
    }

    internal class BatchFile : INotifyPropertyChanged
    {
        public string Source { get; protected set; }
        public string Name { get; protected set; }
        public string FileType { get; private set; }
        public BatchFileState State { get; private set; }
        public string Status { get; private set; }
        public string Details { get; private set; }
        public double Progress { get; private set; }
        public string ProgressLabel { get; private set; }
        public bool IsIndeterminate { get; private set; }
        public event PropertyChangedEventHandler PropertyChanged;

        internal BatchFile(string source)
        {
            Source = Name = source;
            FileType = "Fichier";
            try
            {
                Name = Path.GetFileName(source);
                string extension = Path.GetExtension(Name);
                if (!string.IsNullOrEmpty(extension)) FileType = extension.Substring(1).ToUpperInvariant();
            }
            catch (ArgumentException) { } // Les sélections invalides doivent aussi pouvoir afficher leur motif de refus.
            Set(BatchFileState.Waiting, "En attente", "");
        }

        internal void Set(BatchFileState state, string status, string details)
        {
            State = state;
            Status = status;
            Details = details;
            IsIndeterminate = state == BatchFileState.Running;
            if (state == BatchFileState.Saved) { Progress = 100; ProgressLabel = "100 %"; }
            else if (state == BatchFileState.Waiting || state == BatchFileState.Skipped) { Progress = 0; ProgressLabel = ""; }
            Notify();
        }

        internal static void DistinguishNames(IEnumerable<BatchFile> files)
        {
            foreach (var group in files.GroupBy(file => file.Name, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
                foreach (var file in group) file.Name = file.Source;
        }

        internal void Report(FileProgress update)
        {
            if (State != BatchFileState.Running || (update.Value >= 0 && update.Value < Progress)) return;
            Status = update.Status;
            ProgressLabel = update.Caption;
            IsIndeterminate = update.Value < 0;
            Progress = Math.Max(Progress, update.Value);
            Notify();
        }

        void Notify()
        {
            var changed = PropertyChanged;
            if (changed != null) changed(this, new PropertyChangedEventArgs(""));
        }
    }
}
