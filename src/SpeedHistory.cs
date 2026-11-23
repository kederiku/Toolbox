using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;

namespace AtelierPC
{
    [DataContract]
    internal sealed class SpeedResult
    {
        [DataMember(IsRequired = true)] internal long CompletedUtcTicks;
        [DataMember(IsRequired = true)] internal double Latency;
        [DataMember(IsRequired = true)] internal double Jitter;
        [DataMember(IsRequired = true)] internal double Download;
        [DataMember(IsRequired = true)] internal double Upload;
        [DataMember(IsRequired = true)] internal double Seconds;
        [DataMember(IsRequired = true)] internal long ReceivedBytes;
        [DataMember(IsRequired = true)] internal long SentBytes;
        [DataMember] internal SpeedConnection Connection;

        internal static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

        internal static string Number(double value)
        {
            return value > 0 && value < 0.01 ? "< 0,01" : value.ToString("0.##", French);
        }

        internal static string Rate(double megabits)
        {
            return BitRate(megabits) + " · " + ByteRate(megabits);
        }

        internal static string BitRate(double megabits) { return Number(megabits) + " Mbit/s"; }
        internal static string ByteRate(double megabits) { return Number(megabits / 8) + " Mo/s"; }

        internal string TransferDetail()
        {
            return "Durée des mesures : " + Number(Seconds) + " s   ·   Reçu : " +
                Number(ReceivedBytes / 1000000.0) + " Mo   ·   Envoyé : " + Number(SentBytes / 1000000.0) + " Mo";
        }

        internal string NetworkDetail()
        {
            var net = Connection ?? new SpeedConnection();
            return "IPv4 publique : " + (net.PublicIPv4 ?? "indisponible") + Environment.NewLine +
                "IPv6 publique : " + (net.PublicIPv6 ?? "indisponible") + Environment.NewLine +
                "FAI : " + (net.Provider ?? "indisponible");
        }
    }

    internal sealed class SpeedHistory
    {
        readonly string path;

        internal SpeedHistory(string path = null)
        {
            this.path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AtelierPC", "historique-debit.json");
        }

        internal List<SpeedResult> Load()
        {
            try
            {
                using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                {
                    if (input.Length > 128 * 1024) throw new InvalidDataException("Historique de débit trop volumineux.");
                    var items = (List<SpeedResult>)new DataContractJsonSerializer(typeof(List<SpeedResult>)).ReadObject(input);
                    return Recent(items ?? new List<SpeedResult>());
                }
            }
            catch (FileNotFoundException) { return new List<SpeedResult>(); }
            catch (DirectoryNotFoundException) { return new List<SpeedResult>(); }
            catch (SerializationException ex) { throw new InvalidDataException("Historique de débit illisible.", ex); }
        }

        static List<SpeedResult> Recent(IEnumerable<SpeedResult> items)
        {
            return items.Where(r => r != null && r.CompletedUtcTicks > 0 && r.CompletedUtcTicks <= DateTime.MaxValue.Ticks)
                .OrderByDescending(r => r.CompletedUtcTicks).Take(10).ToList();
        }

        internal List<SpeedResult> Add(SpeedResult result)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var guard = LockForWrite())
            {
                List<SpeedResult> items;
                string backup = null;
                try { items = Load(); }
                catch (InvalidDataException)
                {
                    items = new List<SpeedResult>();
                    backup = path + ".illisible";
                }
                items.Insert(0, result);
                items = Recent(items);
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        new DataContractJsonSerializer(typeof(List<SpeedResult>)).WriteObject(output, items);
                        output.Flush(true);
                    }
                    if (File.Exists(path)) File.Replace(temporary, path, backup);
                    else File.Move(temporary, path);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                return items;
            }
        }

        FileStream LockForWrite()
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    // Keep the empty lock file. Deleting it on close leaves a delete-pending
                    // window in which Windows rejects another writer with access denied.
                    return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
                }
                catch (IOException ex)
                {
                    int error = ex.HResult & 0xffff;
                    if ((error != 32 && error != 33) || attempt >= 40) throw;
                    Thread.Sleep(25);
                }
            }
        }
    }
}
