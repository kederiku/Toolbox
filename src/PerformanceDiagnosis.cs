using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace AtelierPC
{
    internal sealed class PerformanceCapacity
    {
        readonly long available, total;
        const long Gibibyte = 1073741824;
        double AvailablePercent { get { return available * 100.0 / total; } }
        internal double UsedPercent { get { return 100 - AvailablePercent; } }

        internal string DescribeUsage()
        {
            return string.Format(CultureInfo.GetCultureInfo("fr-FR"), "{0:0.#} / {1:0.#} Gio utilisés",
                (double)(total - available) / Gibibyte, (double)total / Gibibyte);
        }

        PerformanceCapacity(long available, long total)
        {
            this.available = available;
            this.total = total;
        }

        internal static PerformanceCapacity FromBytes(long available, long total)
        {
            return total > 0 && available >= 0 && available <= total ? new PerformanceCapacity(available, total) : null;
        }

        internal string Describe()
        {
            return string.Format(CultureInfo.GetCultureInfo("fr-FR"), "{0:0.##} Gio disponibles sur {1:0.##} Gio ({2:0.#} %)",
                (double)available / Gibibyte, (double)total / Gibibyte, AvailablePercent);
        }

        internal bool IsLow(int percentThreshold, int gibThreshold)
        {
            return AvailablePercent <= percentThreshold && available <= Gibibyte * gibThreshold;
        }
    }

    internal enum FindingState { Normal, Unavailable, Warning }

    internal sealed class PerformanceFinding
    {
        public string Label { get; private set; }
        public string Value { get; private set; }
        public string Threshold { get; private set; }
        public string Advice { get; private set; }
        public FindingState State { get; private set; }
        public string Status { get { return State == FindingState.Warning ? "À examiner" :
            State == FindingState.Unavailable ? "Indisponible" : "Sans alerte"; } }
        public string AccessibleName { get { return Label + " · " + (State == FindingState.Unavailable ? Status : Value + " · " + Status); } }

        internal PerformanceFinding(string label, string value, bool warning, string threshold, string advice)
        {
            Label = label;
            State = value == null ? FindingState.Unavailable : warning ? FindingState.Warning : FindingState.Normal;
            Value = value ?? "indisponible";
            Threshold = threshold;
            Advice = State == FindingState.Unavailable ? "Relancez la mesure pour obtenir cette lecture." :
                warning ? advice : null;
        }
    }

    internal sealed class PerformanceDiagnosis
    {
        const int CpuThreshold = 85, AvailableThreshold = 10, MemoryThresholdGib = 2, DiskThresholdGib = 20;
        internal const int DurationSeconds = 3;
        internal readonly string Title, Advice, Details, Stamp, NextStep;
        internal readonly PerformanceFinding[] Findings;
        internal readonly FindingState State;

        internal PerformanceDiagnosis(DateTime start, TimeSpan elapsed, double? cpu, PerformanceCapacity memory, PerformanceCapacity disk)
        {
            var french = CultureInfo.GetCultureInfo("fr-FR");
            if (!(cpu >= 0 && cpu <= 100)) cpu = null;
            Findings = new[] {
                new PerformanceFinding("Processeur moyen", cpu.HasValue ? cpu.Value.ToString("0.#", french) + " %" : null,
                    cpu >= CpuThreshold, "Alerte à partir de " + CpuThreshold + " % d’utilisation moyenne.",
                    "Processeur très sollicité. Ouvrez le Gestionnaire des tâches (Ctrl + Maj + Échap) et triez les processus par utilisation du processeur."),
                new PerformanceFinding("Mémoire physique", memory == null ? null : memory.Describe(),
                    memory != null && memory.IsLow(AvailableThreshold, MemoryThresholdGib), CapacityThreshold(MemoryThresholdGib),
                    "Mémoire disponible faible. Dans le Gestionnaire des tâches, triez les processus par mémoire utilisée. Enregistrez votre travail avant de fermer une application."),
                new PerformanceFinding("Volume de Windows", disk == null ? null : disk.Describe(),
                    disk != null && disk.IsLow(AvailableThreshold, DiskThresholdGib), CapacityThreshold(DiskThresholdGib),
                    "Stockage presque plein. Dans Paramètres > Système > Stockage, repérez les fichiers ou applications qui occupent le plus d’espace.")
            };
            var advice = Findings.Where(f => f.State == FindingState.Warning).Select(f => f.Advice).ToList();
            int missing = Findings.Count(f => f.State == FindingState.Unavailable);
            State = advice.Count > 0 ? FindingState.Warning : missing > 0 ? FindingState.Unavailable : FindingState.Normal;
            if (missing == Findings.Length)
            {
                Title = "Mesures indisponibles";
                NextStep = "Relancez la mesure. Si les lectures restent indisponibles, consultez l’onglet Performances du Gestionnaire des tâches.";
                advice.Add(NextStep);
            }
            else if (advice.Count > 0)
            {
                Title = (missing > 0 ? "Bilan partiel · " : "") + (advice.Count == 1 ? "1 ressource" : advice.Count + " ressources") + " à examiner";
                NextStep = "";
            }
            else
            {
                Title = missing > 0 ? "Bilan partiel" : "Aucun seuil d’alerte atteint";
                NextStep = missing > 0 ? "Relancez la mesure pour compléter les lectures manquantes."
                    : "Si le PC ralentit de nouveau, relancez la mesure.";
                advice.Add("Sur les ressources mesurées, aucun seuil d’alerte n’a été atteint. Pour vérifier une lenteur intermittente, reproduisez-la puis relancez la mesure.");
            }

            Advice = string.Join(Environment.NewLine, advice);
            Stamp = string.Format(french, "Relevé du {0:dd/MM/yyyy} à {0:HH:mm:ss}  ·  {1:0.##} s", start, elapsed.TotalSeconds);
            Details = string.Format(french, "Début du relevé · {0:dd/MM/yyyy HH:mm:ss}   |   Durée · {1:0.##} s", start, elapsed.TotalSeconds)
                + Environment.NewLine + string.Join(Environment.NewLine, Findings.Select(f => f.Label + " · " + f.Value))
                + Environment.NewLine + string.Format(french, "Seuils indicatifs · processeur ≥ {0} % en moyenne. Mémoire disponible ≤ {1} % et ≤ {2} Gio. Espace disque disponible ≤ {1} % et ≤ {3} Gio.",
                    CpuThreshold, AvailableThreshold, MemoryThresholdGib, DiskThresholdGib);
        }

        static string CapacityThreshold(int gib)
        {
            return "Alerte lorsque la réserve est de " + AvailableThreshold + " % ou moins et qu’il reste au plus " + gib + " Gio.";
        }

        internal string Format()
        {
            return Title + Environment.NewLine + Advice + Environment.NewLine + Details;
        }

        internal static PerformanceDiagnosis Measure(CancellationToken token, IProgress<int> progress = null)
        {
            token.ThrowIfCancellationRequested();
            using (var sampler = new PerformanceSampler(false))
            {
                sampler.Read();
                var start = DateTime.Now;
                var elapsed = Stopwatch.StartNew();
                for (int second = 0; second < DurationSeconds; second++)
                {
                    if (progress != null) progress.Report(second);
                    var remaining = TimeSpan.FromSeconds(second + 1) - elapsed.Elapsed;
                    if (remaining > TimeSpan.Zero) token.WaitHandle.WaitOne(remaining);
                    token.ThrowIfCancellationRequested();
                }
                if (progress != null) progress.Report(DurationSeconds);
                var reading = sampler.Read();
                elapsed.Stop();
                var disk = ReadWindowsVolume();
                token.ThrowIfCancellationRequested();
                return new PerformanceDiagnosis(start, elapsed.Elapsed, reading.Cpu, reading.Memory, disk);
            }
        }

        static PerformanceCapacity ReadWindowsVolume()
        {
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory));
                return PerformanceCapacity.FromBytes(drive.AvailableFreeSpace, drive.TotalSize);
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (System.Security.SecurityException) { return null; }
        }
    }
}
