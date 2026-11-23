using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;

namespace AtelierPC
{
    internal static partial class Tests
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr change, IntPtr data);

        static int count;
        static void Check(bool value, string expectation)
        {
            if (!value) throw new Exception(expectation);
            count++;
        }

        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == PdfWatermarkPreview.Command) return PdfWatermarkPreview.RunWorker();
            try
            {
                CheckUpdateRelease();
                CheckUpdateInstaller();
                CheckUpdateNotice();
                CheckToolPreferences();
                CheckYoutubeVideo();
                CheckYoutubeDownload();
                CheckYoutubePage();
                RunWatermarkTests();
                RunPdfWatermarkTests();
                RunWordWatermarkTests();
                RunImageWatermarkTests();
                CheckCatalogNavigation();
                CheckDuplicateScanner();
                CheckSimilarPhotos();
                CheckDuplicateRecycler();
                CheckDuplicatePage();
                CheckConverterLayouts();
                CheckConversionBatches();
                CheckMetadataNavigation();
                CheckMetadataPublication();
                CheckMetadataBatch();
                CheckPhotoMetadata();
                CheckOfficeMetadata();
                CheckPdfMetadata();
                CheckNetworkDevices();
                const string id = @"USB\VID_1234&PID_5678\01";
                var disabled = new Device(id, "Caméra", 22);
                Check(Repair.CanEnable(disabled, 19041), "Réactivation sur Windows 10 2004");
                Check(!Repair.CanEnable(disabled, 18363), "Guide manuel sur Windows 10 plus ancien");
                foreach (int code in new[] { -1, 0, 10, 14, 28, 43, 52, 999 })
                    Check(!Repair.CanEnable(new Device(id, "Caméra", code), 22621), "Action ciblée " + code);
                foreach (string invalid in new[] { "", "/class USB", "USB\\*\\1", "USB\\VID\\x\"", "USB\\VID\\", "USB\\VID\\x\r\n" })
                    Check(!Repair.CanEnable(new Device(invalid, "Caméra", 22), 22621), "Identifiant refusé");
                Check(Advice.For(28).Title.Contains("pilote"), "Pilote manquant");
                Check(Advice.For(43).Cause.Length > 20, "Cause accessible pour le code 43");
                Check(Advice.For(999).Steps.Length > 0, "Démarche pour un code inconnu");
                int[] documented = { 1, 3, 9, 10, 12, 14, 16, 18, 19, 21, 22, 24, 28, 29, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56, 57 };
                foreach (int code in documented)
                {
                    var advice = Advice.For(code);
                    Check(advice.Title != Advice.For(999).Title && advice.Cause.Length > 20 && advice.Steps.Length > 0, "Explication spécifique du code " + code);
                }
                Check(DeviceReader.InterpretStatus(0, 0, 28) == 0, "Ignorer le code résiduel");
                Check(DeviceReader.InterpretStatus(1, 0, 0) == Device.Unreadable, "Statut illisible");
                Check(DeviceReader.InterpretStatus(0, 0x400, 28) == 28, "Signalement Windows");
                Check(DeviceReader.InterpretStatus(0, 0x400, 0) == Device.UnspecifiedProblem, "Signalement sans code");
                Check(Repair.Verify(id, new Device[0]) != RepairState.Healthy, "Disparition après correction");
                Check(Repair.Verify(id, new[] { disabled }) == RepairState.Problem, "Problème persistant");
                Check(Repair.Verify(id, new[] { new Device(id.ToLowerInvariant(), "Caméra", 0) }) == RepairState.Healthy, "Même appareil vérifié");
                Check(Repair.Verify(id, new[] { new Device(id, "Caméra", -1) }) == RepairState.Unverified, "Contrôle incomplet");
                var actual = DeviceReader.Scan();
                Check(actual.Count > 0, "Lecture réelle de l’inventaire Windows");
                Check(actual.All(d => !string.IsNullOrWhiteSpace(d.Name)), "Noms affichables");
                CheckImageConversionNavigation();
                CheckImageConversions();
                CheckAudioNavigation();
                CheckAudioConversions();
                CheckVideoEngine();
                CheckVideoConversions();
                CheckVideoPage();
                CheckReport();
                CheckUsb();
                CheckTopology();
                CheckPortChanges();
                CheckInternetSpeed();
                CheckNetworkDiagnosis();
                CheckInterface();
                Console.WriteLine("{0} contrôles réussis. Inventaire réel : {1} périphériques, {2} signalements, {3} états illisibles.", count, actual.Count, actual.Count(d => d.HasProblem), actual.Count(d => d.Code == Device.Unreadable));
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }

        static void CheckNetworkDiagnosis()
        {
            var wifi = new NetworkLink { Id = "adapter-a", Name = "Wi-Fi maison", Dhcp = true };
            var other = new NetworkLink { Id = "adapter-b", Name = "Ethernet", Dhcp = true };
            var alone = new[] { wifi };
            var issue = NetworkDiagnosis.Assess(alone, WebProbe.Unavailable);
            Check(issue.Fix == NetworkFix.RenewAddress && issue.Target == wifi, "Renouvellement ciblé si DHCP sans adresse utilisable");
            Check(NetworkTroubleshooter.Arguments(issue, alone) == "/renew \"Wi-Fi maison\"", "La commande DHCP cible une seule carte");
            Check(NetworkTroubleshooter.Arguments(issue, new[] { other }) == null, "Une carte disparue ne peut pas être réparée");
            Check(NetworkDiagnosis.Assess(new[] { wifi, other }, WebProbe.Unavailable).Fix == NetworkFix.None,
                "Plusieurs cartes candidates ne déclenchent pas un renouvellement arbitraire");
            foreach (var unsafeName in new[] { "*", "Wi?Fi", "LAN\" /all", "/all", "Carte\r\n" })
            {
                var link = new NetworkLink { Id = "a", Name = unsafeName, Dhcp = true };
                Check(NetworkDiagnosis.Assess(new[] { link }, WebProbe.Unavailable).Fix == NetworkFix.None,
                    "Le nom de carte doit désigner une seule interface");
            }
            wifi.Dhcp = false;
            Check(NetworkTroubleshooter.Arguments(issue, alone) == null, "Pas de renouvellement après passage en IP fixe");
            wifi.HasIPv6 = true;
            wifi.HasGateway = true;
            Check(NetworkDiagnosis.Assess(alone, WebProbe.Healthy).Fix == NetworkFix.None, "IPv6 seul avec connexion fonctionnelle");
            Check(NetworkDiagnosis.Assess(alone, WebProbe.DnsFailure).Fix == NetworkFix.FlushDns, "Cache DNS proposé pour un échec de résolution confirmé");
            wifi.HasGateway = false;
            var noRoute = NetworkDiagnosis.Assess(alone, WebProbe.DnsFailure);
            Check(noRoute.Fix == NetworkFix.None && noRoute.Detail.Contains("passerelle"),
                "Une interface locale sans passerelle oriente d’abord vers la connexion au réseau");
            wifi.HasGateway = true;
            Check(NetworkDiagnosis.Assess(alone, WebProbe.Unavailable).Fix == NetworkFix.None, "Un serveur inaccessible ne prouve pas une panne DNS");
            Check(NetworkDiagnosis.Assess(alone, WebProbe.Intercepted).Fix == NetworkFix.None, "Un portail captif appelle une indication de connexion");
            Check(NetworkDiagnosis.Assess(new NetworkLink[0], WebProbe.Unavailable).Fix == NetworkFix.None,
                "Aucun changement système pour un câble débranché");
            Check(NetworkTroubleshooter.InterpretResponse(511, null) == WebProbe.Intercepted &&
                NetworkTroubleshooter.InterpretResponse(302, null) == WebProbe.Intercepted &&
                NetworkTroubleshooter.InterpretResponse(403, null) == WebProbe.Unavailable,
                "Un portail demandant une authentification se distingue d’un blocage générique");
            Check(NetworkTroubleshooter.InterpretResponse(200, "Microsoft Connect Test") == WebProbe.Healthy &&
                NetworkTroubleshooter.InterpretResponse(200, "<html>Login") == WebProbe.Intercepted,
                "Seul le contenu de contrôle confirme la réponse attendue");
            foreach (string local in new[] { "169.254.10.2", "127.0.0.1", "::1", "fe80::1" })
                Check(!NetworkTroubleshooter.Usable(System.Net.IPAddress.Parse(local)), "Adresse locale au lien ou de bouclage exclue");
            foreach (string usable in new[] { "192.168.1.15", "2001:db8::11" })
                Check(NetworkTroubleshooter.Usable(System.Net.IPAddress.Parse(usable)), "Adresse IP utilisable pour le diagnostic");
        }

        static void CheckInternetSpeed()
        {
            CheckSpeedDetails();
            var token = CancellationToken.None;
            var delays = new Queue<int>(new[] { 950, 30, 5, 10, 90, 20 });
            var engine = new InternetSpeedTest((phase, size, ct) => TimeSpan.FromMilliseconds(delays.Dequeue()));
            Check(engine.Measure(SpeedPhase.Latency, token) == 20, "Latence médiane après établissement de la connexion");

            var sizes = new List<int>();
            engine = new InternetSpeedTest((phase, size, ct) => {
                sizes.Add(size);
                return TimeSpan.FromMilliseconds(size < 1000000 ? 100 : 2000);
            });
            Check(engine.Measure(SpeedPhase.Download, token) == 4 && sizes.Sum() == 1064000,
                "1 Mo reçu en deux secondes représente 4 Mbit/s et termine la mesure");
            sizes.Clear();
            engine = new InternetSpeedTest((phase, size, ct) => {
                sizes.Add(size);
                return TimeSpan.FromMilliseconds(100);
            });
            engine.Measure(SpeedPhase.Download, token);
            engine.Measure(SpeedPhase.Upload, token);
            Check(sizes.Sum() <= 100000000 && sizes.Max() >= 8000000, "Ligne rapide mesurée dans le budget cumulé");

            sizes.Clear();
            engine = new InternetSpeedTest((phase, size, ct) => {
                sizes.Add(size);
                return TimeSpan.FromMilliseconds(1100 + size * 8.0 / 100000);
            });
            double highLatencyRate = engine.Measure(SpeedPhase.Download, token, 1100);
            Check(highLatencyRate > 70 && highLatencyRate <= 100 && sizes.Max() == 40000000,
                "Une liaison rapide à forte latence dépasse le petit transfert de calibration");

            sizes.Clear();
            engine = new InternetSpeedTest((phase, size, ct) => {
                sizes.Add(size);
                if (sizes.Count == 1) return TimeSpan.FromSeconds(2);
                return TimeSpan.FromMilliseconds(size < 8000000 ? 100 : 1500);
            });
            Check(engine.Measure(SpeedPhase.Upload, token) > 40 && sizes.Contains(8000000),
                "Un seul retard au premier échange ne fixe pas le débit d’envoi");

            using (var stop = new CancellationTokenSource())
            {
                stop.Cancel();
                int before = sizes.Count;
                bool cancelled = false;
                try { engine.Measure(SpeedPhase.Upload, stop.Token); }
                catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled && sizes.Count == before, "Annulation avant tout échange supplémentaire");
            }
            bool incomplete = false;
            try { InternetSpeedTest.ReadDownload(new MemoryStream(new byte[100]), 101, new byte[16], token); }
            catch (EndOfStreamException) { incomplete = true; }
            Check(incomplete, "Un téléchargement interrompu ne produit pas de débit");
        }

        static Device UsbDevice(string id, int code, string marker, string path, string product)
        {
            var evidence = new UsbEvidence(new[] { marker }, path, "Port_#0003.Hub_#0002", product);
            return new Device(id, "Appareil de test", code, "Appareil USB", evidence);
        }

        static void CheckUsb()
        {
            const string path = "PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(2)#USB(3)";
            var target = UsbDevice(@"USB\VID_0000&PID_0001\SERIE-PRIVEE", 43, @"USB\RESET_FAILURE", path, null);
            string otherPort = path.Replace("USB(3)", "USB(4)");
            var other = UsbDevice(@"USB\VID_1234&PID_0002\AUTRE", 0, "", otherPort, "Clavier");
            var known = UsbDevice(@"USB\VID_1234&PID_ABCD\PRIVE", 0, "", path, "Webcam HD");
            Check(Advice.For(target).Title.Contains("port USB"), "Échec de réinitialisation distingué du code 43 général");
            Check(Advice.For(new Device(@"PCI\VEN_ABCD\1", "Carte graphique", 43)).Title == Advice.For(43).Title, "Code 43 non USB préservé");
            Check(Advice.For(UsbDevice(target.Id, 43, @"USB\DEVICE_DESCRIPTOR_FAILURE", path, null)).Cause.Contains("description"), "Échec de lecture identifié");
            Check(UsbEvidence.ForDevice(new Device(@"USB\VID_0000&PID_0001\1", "Périphérique USB inconnu (échec de réinitialisation de port)", 43)) == UsbFailure.Reset, "Libellé français de la capture");
            Check(target.Usb.PortLabel.Contains("3") && target.Usb.PortLabel.Contains("2"), "Repères locaux du port et du concentrateur");
            Check(UsbEvidence.ProductIds(target.Id) == null && UsbEvidence.ProductIds(known.Id).Contains("ABCD"), "Identifiants de substitution exclus");
            var initial = new List<Device> { target, other };
            var oldHub = UsbDevice(@"USB\VID_1234&PID_0004\ANCIEN", 0, "", path.Replace("PCI(1400)", "PCI(1500)"), "Autre contrôleur");
            target.Usb.Remember(known);
            target.Usb.Remember(oldHub);
            target.Usb.Remember(target);
            target.Usb.Remember(known);
            Check(target.Usb.PriorModels.Count == 1 && target.Usb.PriorModels[0].Contains("Webcam HD"),
                "Historique dédoublonné uniquement sur le chemin complet, sans identifiant USB de substitution");
            var probe = new UsbProbe(target, initial);
            var unreadableId = new Device("", "Périphérique USB inconnu", 43, usb: target.Usb);
            var uncertainProbe = new UsbProbe(target, initial);
            uncertainProbe.Observe(new[] { other, unreadableId });
            Check(uncertainProbe.Stage == UsbStage.Unplug && uncertainProbe.Report.Contains("identif"),
                "Une entrée USB devenue impossible à identifier ne prouve pas un retrait");
            uncertainProbe.Observe(new[] { other });
            uncertainProbe.Observe(new[] { other, known, unreadableId });
            Check(uncertainProbe.Stage == UsbStage.Replug && !uncertainProbe.Report.Contains("ne signale plus"),
                "Un second appareil sans identité empêche de confirmer le retour sur la prise");
            probe.Observe(initial);
            Check(probe.Stage == UsbStage.Unplug, "Appareil toujours présent");
            probe.Observe(new List<Device> { other });
            Check(probe.Stage == UsbStage.Replug, "Disparition de la cible observée");
            probe.Observe(new List<Device> { other });
            Check(probe.Stage == UsbStage.Replug, "Une absence seule ne prouve pas une réparation");
            probe.Observe(new List<Device> { other, known });
            Check(probe.Stage == UsbStage.Done && probe.Detail.Contains("Webcam HD"), "Nouvelle identité sur le même port");
            Check(!probe.Report.Contains("PRIVE") && !probe.Report.Contains("Webcam HD"), "Observation partageable sans étiquette locale");
            var retry = new UsbProbe(target, initial);
            retry.Observe(new List<Device> { other });
            retry.Observe(initial);
            Check(retry.Stage == UsbStage.Done && retry.Detail.Contains("câble"), "Panne persistante avec test suivant");
            var concurrent = new UsbProbe(target, initial);
            concurrent.Observe(new List<Device>());
            Check(concurrent.Stage == UsbStage.Done && concurrent.Report.Contains("ambigu"), "Deux débranchements ne désignent pas une seule prise");
            var mismatch = new UsbProbe(target, initial);
            mismatch.Observe(new List<Device> { other });
            mismatch.Observe(new List<Device> { other, UsbDevice(known.Id, 0, "", otherPort, "Webcam HD") });
            Check(mismatch.Stage != UsbStage.Done && !mismatch.Detail.Contains("Webcam HD"), "Appareil sur un autre port jamais attribué");
            var noPort = new Device(target.Id, target.Name, target.Code, target.Model,
                new UsbEvidence(new[] { @"USB\RESET_FAILURE" }, null, null, null));
            var unlocated = new UsbProbe(noPort, new[] { noPort });
            unlocated.Observe(new Device[0]);
            unlocated.Observe(new[] { new Device(noPort.Id, "Appareil revenu", 0) });
            Check(!unlocated.Report.Contains("sur la prise suivie") && unlocated.Report.Contains("prise")
                && unlocated.Report.Contains("confirmer"), "Une identité seule ne confirme pas un retour sur la même prise");
            var report = DiagnosticReport.Format(26100, DateTime.Now, initial, null, retry.Report);
            Check(report.Contains("port USB") && report.Contains(retry.Report) && !report.Contains("SERIE-PRIVEE"), "Diagnostic USB et résultat de l’essai dans le résumé");
        }

        static void CheckTopology()
        {
            var controller = new Device(@"PCI\HCI\1", "Contrôleur de test", 0, null, null, null, UsbKind.Controller);
            var root = new Device(@"USB\ROOT_HUB30\1", "Hub racine", 0, null, null, controller.Id);
            var dock = new Device(@"USB\VID_1111&PID_0100\1", "Station A", 0, null, null, root.Id.ToLowerInvariant(), UsbKind.Hub);
            var nested = new Device(@"USB\VID_1111&PID_0200\1", "Station B", 0, null, null, dock.Id, UsbKind.Hub);
            var port2 = new UsbEvidence(null, null, "Port_#0002.Hub_#0004", "Webcam");
            var port10 = new UsbEvidence(null, "PCIROOT(0)#USBROOT(0)#USB(10)", null, null);
            var camera = new Device(@"USB\VID_2222&PID_3333\1", "Appareil composite", 43, null, port2, nested.Id);
            var disk = new Device(@"USB\VID_2222&PID_4444\1", "Disque", 0, null, port10, nested.Id);
            var emptyHub = new Device(@"USB\VID_1111&PID_0200\2", "Station B", 0, null, port2, dock.Id, UsbKind.Hub);
            var orphan = new Device(@"USB\VID_1111&PID_0200\3", "Raccordement inconnu", 0, null, port2, "ABSENT");
            var function = new Device(@"USB\VID_2222&PID_3333&MI_00\1", "Fonction vidéo", 0, null, null, camera.Id);
            var snapshot = new[] { orphan, disk, emptyHub, function, dock, nested, camera, controller, root };
            var map = UsbTopology.Build(snapshot);
            var host = map.Controllers.Single();
            var station = host.Children.Single().Children.Single();
            Check(station.Device == dock && station.Children.Count == 2, "Hub imbriqué et hub frère reliés au bon parent");
            var downstream = station.Children.Single(n => n.Device == nested);
            Check(downstream.Children[0].Device == camera && downstream.Children[1].Device == disk, "Tri numérique des ports");
            Check(downstream.Children[0].Title == "Webcam" && downstream.Children[0].Device.HasProblem, "Nom transmis et anomalie conservés");
            Check(downstream.Children[0].Children.Count == 0 && map.DeviceCount == 3, "Un appareil composite occupe une seule prise");
            Check(downstream.Children[0].Functions.SequenceEqual(new[] { "Fonction vidéo" }), "Fonction d’un appareil composite lisible sur sa fiche");
            Check(map.Unattached.Single().Device == orphan && map.HubCount == 4, "Hub vide reconnu et appareil non rattaché séparé");
            Check(port2.PortNumber == 2 && port10.PortNumber == 10, "Numéro de port relatif au hub");
            var host2 = new Device(@"PCI\HCI\2", "Contrôleur de test", 0, null, null, null, UsbKind.Controller);
            var root2 = new Device(@"USB\ROOT_HUB30\2", "Hub racine", 0, null, null, host2.Id);
            var twin = new Device(@"USB\VID_2222&PID_3333\2", "Appareil composite", 0, null, port2, root2.Id);
            var two = UsbTopology.Build(snapshot.Concat(new[] { twin, root2, host2 }));
            Check(two.Controllers.Single(n => n.Device == host2).Children.Single().Children.Single().Device == twin, "Ports identiques sur deux contrôleurs distincts");
            Check(two.Controllers[0].ControllerNumber == 1 && two.Controllers[1].ControllerNumber == 2, "Repères distincts pour deux contrôleurs de même nom");
            var loop = new Device(dock.Id, dock.Name, 0, null, null, nested.Id, UsbKind.Hub);
            Check(UsbTopology.Build(new[] { loop, nested }).Unattached.Count > 0, "Données cycliques consultables");
            Check(UsbTopology.Build(new[] { new Device(@"PCI\OTHER\1", "Carte réseau", 0) }).DeviceCount == 0, "Aucun branchement USB inventé");
        }

        static Device Adapter(string suffix, int port, bool unique, string com)
        {
            var evidence = new UsbEvidence(null, "PCIROOT(0)#USBROOT(0)#USB(" + port + ")",
                "Port_#000" + port + ".Hub_#0001", "Adaptateur", unique);
            return new Device(@"USB\VID_1234&PID_5678\" + suffix, "Adaptateur", 0, null, evidence, null, UsbKind.Device, com);
        }

        static void CheckPortChanges()
        {
            var first = Adapter("SERIAL", 2, true, "COM3");
            var moved = Adapter("SERIAL", 4, true, "COM5");
            var tracker = new UsbTracker();
            Check(tracker.Update(new[] { first }, new Device[0]).Count == 0, "Premier relevé de référence");
            tracker.Update(new Device[0], new Device[0]);
            var change = tracker.Update(new[] { moved }, new Device[0]).Single();
            Check(change.Recognized && !change.CanClean, "Même exemplaire sur une nouvelle prise, installation encore active");
            Check(change.Settings.Contains("COM3") && change.Settings.Contains("COM5"), "Ancien et nouveau réglage COM");
            Check(tracker.Update(new[] { moved }, new Device[0]).Single().Recognized, "Observation conservée après actualisation");
            var old = Adapter("6&A&0&2", 2, false, "COM3");
            var current = Adapter("6&A&0&4", 4, false, "COM5");
            var historic = new UsbTracker().Update(new[] { current }, new[] { old }).Single();
            Check(!historic.Recognized && historic.CanClean, "Ancienne entrée précise consultable et identité à confirmer");
            Check(new UsbTracker().Update(new[] { moved }, new[] { Adapter("AUTRE", 2, true, "COM3") }).Count == 0, "Deux identités uniques différentes");
            Check(new UsbTracker().Update(new[] { current }, new[] { old, Adapter("AUTRE", 3, false, null) }).All(c => !c.CanClean), "Plusieurs anciennes installations");
            Check(new UsbTracker().Update(new[] { current, Adapter("AUTRE", 3, false, null) }, new[] { old }).All(c => !c.CanClean), "Deux exemplaires actifs");
            Check(new UsbTracker().Update(new[] { current, old }, new[] { old }).Count == 0, "Ancienne entrée rebranchée");
            var observed = new UsbTracker();
            observed.Update(new[] { old, current }, new Device[0]);
            Check(observed.Update(new[] { current }, new[] { old }).Count == 0, "Deux appareils précédemment présents ensemble");
            var logical = new Device(@"FTDIBUS\PORT\1", "Port série", 0, null, null, current.Id, UsbKind.None, "COM7");
            var multi = new UsbTracker().Update(new[] { current, logical }, new[] { old }).Single();
            Check(multi.Settings.Contains("COM7") && multi.Settings.Contains("COM5"), "Ports série enfants rattachés à l’adaptateur");
            Check(!multi.Settings.Contains("sélectionnez COM5"), "Plusieurs ports, attribution indéterminée");
            Check(historic.Fingerprint != multi.Fingerprint, "Révalidation si la configuration change pendant la confirmation");
            var cleanId = historic.Fingerprint;
            Check(UsbCleanup.FindReviewed(cleanId, new[] { current }, new[] { old }) != null, "Ancienne installation retrouvée après confirmation");
            Check(UsbCleanup.FindReviewed("incorrect", new[] { current }, new[] { old }) == null, "Demande de retrait mal formée");
            Check(UsbCleanup.FindReviewed(cleanId, new[] { old, current }, new[] { old }) == null, "Retrait bloqué si l’ancien appareil revient");
            Check(UsbCleanup.FindReviewed(cleanId, new[] { current, logical }, new[] { old }) == null, "Retrait bloqué si une interface série change");
            var faulty = new Device(current.Id, current.Name, 43, null, current.Usb, null, UsbKind.Device, current.ComPort);
            Check(UsbCleanup.FindReviewed(cleanId, new[] { faulty }, new[] { old }) == null, "Aucun nettoyage si le périphérique actuel est en panne");
            var hub = new Device(old.Id, old.Name, 0, null, old.Usb, null, UsbKind.Hub, old.ComPort);
            Check(UsbCleanup.FindReviewed(cleanId, new[] { current }, new[] { hub }) == null, "Un ancien hub est exclu du retrait");
            var missingFlag = new UsbEvidence(null, old.Usb.PortPath, null, old.Name);
            var incomplete = new Device(old.Id, old.Name, 0, null, missingFlag, null, UsbKind.Device, old.ComPort);
            Check(UsbCleanup.FindReviewed(cleanId, new[] { current }, new[] { incomplete }) == null, "Capacité d’identification indisponible");
            var delayed = new UsbTracker();
            delayed.Update(new[] { first }, new Device[0]);
            delayed.Update(new[] { new Device(first.Id, first.Name, 0, null, new UsbEvidence(null, null, null, first.Name, true)) }, new Device[0]);
            Check(delayed.Update(new[] { moved }, new Device[0]).Single().Before.Com == "COM3", "Relevé intermédiaire incomplet pendant le branchement");
            Check(delayed.Update(new[] { Adapter("SERIAL", 4, true, "COM8") }, new Device[0]).Single().Title == "Affectation COM modifiée", "Numéro COM modifié sur la même prise");
            var reassigned = new UsbTracker();
            reassigned.Update(new[] { old }, new Device[0]);
            var localChange = reassigned.Update(new[] { Adapter("6&A&0&2", 2, false, "COM9") }, new Device[0]).SingleOrDefault();
            Check(localChange != null && !localChange.CanClean && !localChange.Recognized && localChange.Title.Contains("entrée Windows")
                && localChange.Settings.Contains("COM3") && localChange.Settings.Contains("COM9"),
                "Une réaffectation COM sur la même entrée Windows est visible même sans identifiant unique");
        }

        static void CheckReport()
        {
            const string id = @"USB\VID_0000&PID_0001\SERIE-0099";
            var date = new DateTime(2026, 11, 19, 14, 52, 0);
            var devices = new List<Device> {
                new Device(id, "Imprimante de Camille", 28, "Imprimante Laser"),
                new Device(id, id, Device.Unreadable),
                new Device(id, "Personnel", Device.UnspecifiedProblem, "Appareil SERIE-0099"),
                new Device(id, "Souris privée", 0, "Souris fonctionnelle")
            };
            string repair = DiagnosticReport.Reactivation(date, devices[0], "Réactivation annulée.");
            string report = DiagnosticReport.Format(26100, date, devices, repair);
            Check(report.Contains("Windows 11") && report.Contains("26100") && report.Contains("19/11/2026"), "Version et date du diagnostic");
            Check(report.Contains("Imprimante Laser") && report.Contains("28") && report.Contains(Advice.For(28).Title), "Modèle, code et explication");
            Check(report.Contains("Réactivation annulée.") && report.Contains("14:52"), "Résultat horodaté de la correction");
            Check(report.Contains(Advice.For(Device.Unreadable).Title) && !report.Contains("code -"), "État illisible et problème sans code");
            Check(!report.Contains("SERIE-0099") && !report.Contains("VID_0000") && !report.Contains("Camille") && !report.Contains("Souris"), "Données personnelles et appareils sains omis");
            var host = new Device(id, "PC", 22, "Ordinateur " + Environment.MachineName);
            Check(!DiagnosticReport.Reactivation(date, host, "Annulée").Contains(Environment.MachineName), "Nom du PC écarté de la correction");
            Check(DiagnosticReport.Format(19045, date, null, repair).Contains("incomplète"), "Échec de vérification partageable");
        }

        static void CheckInterface()
        {
            var app = new Application();
            System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
            var fixture = new List<Device> {
                new Device(@"USB\TEST\1", "Imprimante de test", 28),
                new Device(@"USB\TEST\2", "Souris de test", 0),
                new Device(@"USB\TEST\3", "Caméra de test", Device.Unreadable)
            };
            int scans = 0;
            bool failNext = false;
            string copied = null;
            bool clipboardBusy = true;
            var installed = new List<Device>();
            bool historyUnavailable = false;
            var shell = new Shell(delegate {
                scans++;
                if (scans == 2 || failNext) { failNext = false; throw new InvalidOperationException("Erreur de lecture simulée"); }
                return fixture;
            }, delegate(string value) {
                if (clipboardBusy) throw new System.Runtime.InteropServices.COMException();
                copied = value;
            }, delegate {
                if (historyUnavailable) throw new InvalidOperationException("Historique momentanément illisible");
                return installed;
            });
            var window = shell.Window;
            ShowNavigationWindow(window);
            Action<string> nav = name => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ClickVisible((Button)window.FindName("StartNav"));
            var startView = window.FindName("StartView") as StackPanel;
            Check(startView != null && startView.IsVisible && scans == 0,
                "Le dépannage guidé présente les symptômes avant toute analyse");
            Check(((TextBlock)window.FindName("AccessLevel")).Text.Length > 0, "Droits effectifs affichés dans le dépannage guidé");
            foreach (var route in new[] { new[] { "StartNetwork", "NetworkRepairView" },
                new[] { "StartSpeed", "SpeedPage" }, new[] { "StartPerformance", "PerformanceView" },
                new[] { "StartIntegrity", "IntegrityView" } })
            {
                ClickVisible((Button)window.FindName(route[0]));
                Check(((StackPanel)window.FindName(route[1])).Visibility == Visibility.Visible,
                    "Le symptôme ouvre sa vérification : " + route[0]);
                nav("StartNav");
            }
            ClickVisible((Button)window.FindName("StartReport"));
            var copyReport = (Button)window.FindName("CopyReport");
            Check(!copyReport.IsEnabled && ((StackPanel)window.FindName("ReportView")).Visibility == Visibility.Visible,
                "Rapport vide accessible depuis le dépannage guidé");
            nav("StartNav");
            nav("StartDevices");
            Check(((StackPanel)window.FindName("DiagnosticPage")).Visibility == Visibility.Visible && scans == 0,
                "Le symptôme matériel ouvre le diagnostic sans le lancer");
            var results = (StackPanel)window.FindName("Results");
            var scan = (Button)window.FindName("ScanButton");
            var copy = (Button)window.FindName("CopySummary");
            var share = (StackPanel)window.FindName("ShareBar");
            Action clickCopy = delegate { copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };
            Check(share.Visibility == Visibility.Collapsed, "Aucun résumé avant la première analyse");
            Check(results.Children.Count == 1 && scan.IsEnabled, "Écran initial prêt");
            Action<Button> clickAndWait = delegate(Button button) {
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var timeout = DateTime.UtcNow.AddSeconds(10);
                while (!scan.IsEnabled && DateTime.UtcNow < timeout)
                    window.Dispatcher.Invoke(DispatcherPriority.Background, new Action(delegate {}));
                Check(scan.IsEnabled, "Analyse terminée et bouton rétabli");
            };
            Action clickScan = delegate { clickAndWait(scan); };
            clickScan();
            nav("ReportNav");
            Check(copyReport.IsEnabled, "Le contrôle matériel alimente le rapport commun");
            nav("CopyReport");
            Check(copied == null && ((TextBlock)window.FindName("ReportFeedback")).Text.Contains("réessayez"),
                "Rapport conservé lorsque le presse-papiers est indisponible");
            clipboardBusy = false;
            nav("CopyReport");
            Check(copied.Contains("28") && copied.Contains("Rapport d’intervention") && !copied.Contains(@"USB\TEST"),
                "Rapport de la session copiable avec les codes utiles");
            copied = null;
            clipboardBusy = true;
            nav("DiagNav");
            Check(share.Visibility == Visibility.Visible && copied == null, "Copie proposée à la demande");
            clickCopy();
            Check(copied == null && ((TextBlock)window.FindName("CopyFeedback")).Text.Contains("réessayez"), "Presse-papiers occupé");
            clipboardBusy = false;
            clickCopy();
            Check(copied != null && copied.Contains("28"), "Bouton relié au résumé courant");
            Check(results.Children.OfType<Expander>().Any(), "Inventaire consultable");
            Check(((TextBlock)window.FindName("Stamp")).Text.Length > 0, "Heure de l’analyse");
            ((Button)window.FindName("HomeNav")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(((StackPanel)window.FindName("OtherPage")).Visibility == Visibility.Visible, "Catalogue des outils");
            ((Button)window.FindName("HelpNav")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(((TextBlock)window.FindName("PageTitle")).Text.Contains("Guide"), "Guide accessible");
            ((Button)window.FindName("DiagNav")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            clickScan();
            Check(results.Children.Count == 1 && ((TextBlock)window.FindName("Stamp")).Text == "", "Échec sans anciens résultats");
            clickCopy();
            Check(copied.Contains("incomplète") && !copied.Contains("pilote"), "Copie de l’échec sans ancien diagnostic");
            fixture.Clear();
            fixture.Add(new Device(@"USB\TEST\2", "Souris de test", 0));
            clickScan();
            Check(results.Children.OfType<Expander>().Any(), "Nouvel essai après échec");
            clickCopy();
            Check(copied.Contains("Aucun problème signalé") && !copied.Contains("incomplète"), "Copie du nouvel état sain");
            const string path = "PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(3)";
            var usb = UsbDevice(@"USB\VID_0000&PID_0001\TEST", 43, @"USB\RESET_FAILURE", path, null);
            fixture.Add(usb);
            clickScan();
            var locate = FindAction(results, "Retrouver cet appareil");
            Check(locate != null && FindAction(results, "Ouvrir Windows Update") == null, "Action contextualisée sur la fiche USB");
            failNext = true;
            clickAndWait(locate);
            nav("ReportNav");
            nav("CopyReport");
            Check(copied.Contains("Essai de branchement USB") && copied.Contains("inventaire de départ"),
                "Un essai USB interrompu dès la première lecture figure dans le rapport");
            nav("DiagNav");
            clickScan();
            clickAndWait(FindAction(results, "Retrouver cet appareil"));
            var guide = (Border)window.FindName("UsbGuide");
            var checkUsb = (Button)window.FindName("UsbCheck");
            Check(guide.Visibility == Visibility.Visible && checkUsb.Content.ToString().Contains("débranchement"), "Assistant ouvert sur la bonne entrée");
            fixture.Remove(usb);
            clickAndWait(checkUsb);
            Check(checkUsb.Content.ToString().Contains("rebranchement"), "Étape de retour disponible après le retrait");
            fixture.Add(UsbDevice(@"USB\VID_2345&PID_6789\SECRET", 0, "", path, "Webcam reconnue"));
            clickAndWait(checkUsb);
            Check(checkUsb.Visibility == Visibility.Collapsed && ((TextBlock)window.FindName("UsbInstruction")).Text.Contains("Webcam reconnue"), "Identité locale expliquée après le retour");
            clickCopy();
            Check(copied.Contains("Test USB") && !copied.Contains("SECRET"), "Compte rendu du test réel de l’interface");
            ((Button)window.FindName("UsbClose")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(results.Visibility == Visibility.Visible && guide.Visibility == Visibility.Collapsed, "Retour aux résultats actualisés");
            var host = new Device(@"PCI\HCI\1", "Contrôleur", 0, null, null, null, UsbKind.Controller);
            var root = new Device(@"USB\ROOT_HUB30\1", "Hub racine", 0, null, null, host.Id);
            var hub = new Device(@"USB\VID_1234&PID_2222\1", "Station", 0, null, null, root.Id, UsbKind.Hub);
            var faulty = new Device(usb.Id, usb.Name, usb.Code, usb.Model, usb.Usb, hub.Id);
            fixture.Clear();
            fixture.AddRange(new[] { faulty, root, hub, host });
            clickScan();
            var refresh = (Button)window.FindName("MapRefresh");
            var tree = (TreeView)window.FindName("MapTree");
            Action<Button> openMap = delegate(Button button) {
                Check(button != null && refresh != null && tree != null, "Entrée vers la carte disponible");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var timeout = DateTime.UtcNow.AddSeconds(10);
                while (!refresh.IsEnabled && DateTime.UtcNow < timeout)
                    window.Dispatcher.Invoke(DispatcherPriority.Background, new Action(delegate {}));
                Check(refresh.IsEnabled, "Lecture de la carte terminée");
            };
            openMap(FindAction(results, "Voir sur la carte USB"));
            var hostRow = (TreeViewItem)tree.Items[0];
            var rootRow = (TreeViewItem)hostRow.Items[0];
            var dockRow = (TreeViewItem)rootRow.Items[0];
            var deviceRow = (TreeViewItem)dockRow.Items[0];
            Check(((UsbNode)deviceRow.Tag).Device == faulty && deviceRow.IsSelected, "Accès direct à la bonne branche");
            Check(((TextBlock)window.FindName("MapPath")).Text.Contains("Station"), "Chemin lisible avec le hub intermédiaire");
            failNext = true;
            openMap(refresh);
            Check(tree.Items.Count == 0 && ((TextBlock)window.FindName("MapStatus")).Text.Contains("impossible"), "Erreur de lecture sans carte périmée");
            fixture.Remove(faulty);
            openMap(refresh);
            Check(tree.Items.Count == 1 && ((TextBlock)window.FindName("MapStatus")).Text.Contains("0 périphérique"), "Actualisation après débranchement");
            fixture.Clear();
            fixture.Add(new Device(@"PCI\OTHER\1", "Autre composant", 0));
            openMap((Button)window.FindName("MapNav"));
            Check(tree.Items.Count == 0 && ((TextBlock)window.FindName("MapStatus")).Text.Contains("Aucun"), "Carte vide distincte d’une erreur");
            var fixedPort = new Device(@"USB\VID_1111&PID_9999\PRIVE", "Station personnelle", 0, comPort: "COM27");
            fixture.Add(fixedPort);
            openMap(refresh);
            nav("ReportNav");
            nav("CopyReport");
            Check(copied.Contains("COM27") && !copied.Contains("Station personnelle") && !copied.Contains("PRIVE"),
                "Un port série stable est partageable dès le premier relevé, sans identifiant local");
            fixture.Remove(fixedPort);
            openMap((Button)window.FindName("MapNav"));
            var old = Adapter("6&A&0&2", 2, false, "COM3");
            installed.Add(old);
            fixture.Add(Adapter("6&A&0&4", 4, false, "COM5"));
            openMap(refresh);
            Check(((TextBlock)window.FindName("MapSerial")).Text.Contains("COM5")
                && !((TextBlock)window.FindName("MapSerial")).Text.Contains("COM3"),
                "La fiche série indique le port actuel et réserve l’ancien port à l’historique");
            var changes = (StackPanel)window.FindName("MapChanges");
            var labels = Descendants(changes).OfType<TextBlock>().Select(t => t.Text).ToList();
            Check(labels.Any(t => t.Contains("identité à confirmer")) && labels.Any(t => t.Contains("COM3") && t.Contains("sélectionnez COM5")), "Carte avec identité à confirmer et réglage conseillé");
            var remove = FindAction(changes, "Retirer l’ancienne installation…");
            Check(remove != null && !remove.IsEnabled, "Retrait inactif avant confirmation");
            nav("ReportNav");
            nav("CopyReport");
            string reportWithMove = copied;
            Check(copied.Contains("COM3") && copied.Contains("COM5") && copied.Contains("Essai de branchement USB")
                && !copied.Contains(@"USB\VID_") && !copied.Contains("SECRET"), "Suivi USB partageable après un changement de port");
            openMap((Button)window.FindName("MapNav"));
            nav("ReportNav");
            nav("CopyReport");
            Check(copied == reportWithMove, "La simple relecture des mêmes ports ne duplique pas le rapport");
            historyUnavailable = true;
            openMap((Button)window.FindName("MapNav"));
            historyUnavailable = false;
            openMap(refresh);
            nav("ReportNav");
            nav("CopyReport");
            Check(copied == reportWithMove, "Le retour de l’historique USB ne crée pas un deuxième déplacement");
            openMap((Button)window.FindName("MapNav"));
            remove = FindAction(changes, "Retirer l’ancienne installation…");
            var consent = Descendants(changes).OfType<CheckBox>().Single();
            consent.IsChecked = true;
            Check(remove.IsEnabled, "Retrait disponible après identification par l’utilisateur");
            consent.IsChecked = false;
            Check(!remove.IsEnabled, "Confirmation révocable");
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            int beforeNotification = scans;
            fixture.Add(old);
            SendMessage(hwnd, 0x219, new IntPtr(7), IntPtr.Zero);
            SendMessage(hwnd, 0x219, new IntPtr(7), IntPtr.Zero);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while ((scans == beforeNotification || !refresh.IsEnabled) && DateTime.UtcNow < deadline)
                window.Dispatcher.Invoke(DispatcherPriority.Background, new Action(delegate {}));
            Check(scans == beforeNotification + 1 && refresh.IsEnabled, "Notifications Windows regroupées en une actualisation");
            Check(FindAction(changes, "Retirer l’ancienne installation…") == null, "Le rebranchement fait disparaître le retrait");
            nav("ReportNav");
            int beforeBackground = scans;
            fixture.Remove(old);
            SendMessage(hwnd, 0x219, new IntPtr(7), IntPtr.Zero);
            deadline = DateTime.UtcNow.AddSeconds(4);
            while ((scans == beforeBackground || !refresh.IsEnabled) && DateTime.UtcNow < deadline)
                window.Dispatcher.Invoke(DispatcherPriority.Background, new Action(delegate {}));
            Check(scans > beforeBackground && ((StackPanel)window.FindName("ReportView")).Visibility == Visibility.Visible,
                "Le suivi USB observe une coupure pendant la consultation du rapport");
            nav("CopyReport");
            Check(copied.Contains("Activité USB") && !copied.Contains("6&A&0&2"), "Coupure USB consignée sans identifiant d’instance");
            openMap((Button)window.FindName("MapNav"));
            var activity = (ListBox)window.FindName("MonitorEvents");
            int eventsBeforePause = activity.Items.Count;
            nav("MonitorToggle");
            Check(((Button)window.FindName("MonitorToggle")).Content.ToString().Contains("Reprendre"), "Suivi suspendu sur demande");
            fixture.Add(old);
            openMap(refresh);
            Check(activity.Items.Count == eventsBeforePause, "Une lecture manuelle en pause ne consigne pas une coupure");
            openMap((Button)window.FindName("MonitorToggle"));
            Check(activity.Items.Count == eventsBeforePause, "La reprise établit une référence sans imputer de mouvement à la pause");
            fixture.Remove(old);
            openMap(refresh);
            fixture.Add(old);
            openMap(refresh);
            Check(activity.Items.Count == eventsBeforePause + 2, "La nouvelle séquence conserve une disparition et un retour");
            openMap(refresh);
            Check(activity.Items.Count == eventsBeforePause + 2, "Aucune observation en double quand les branchements sont stables");
            for (int i = 0; i < 65; i++)
                fixture.Add(new Device(@"USB\VID_1234&PID_5555\TEST" + i, "Accessoire " + i, 0));
            openMap(refresh);
            Check(activity.Items.Count == eventsBeforePause + 67, "Les anciens événements locaux restent consultables après 60 observations");
            CheckUsbConnectionMonitor();
            CheckUsbMapGuidance();
            CheckUsbSerialPorts();
            CheckSpeedPage();
            CheckNetworkRepairPage(window);
            CheckPerformanceFeature();
            CheckNetworkDetailsFeature();
            CheckIntegrityFeature();
            window.Close();
            app.Shutdown();
        }

        static void CheckSpeedPage()
        {
            string path = Path.Combine(Path.GetTempPath(), "AtelierPC-debit-" + Guid.NewGuid().ToString("N") + ".json");
            try { CheckSpeedPage(new SpeedHistory(path)); }
            finally { File.Delete(path); File.Delete(path + ".lock"); }
        }

        static void CheckSpeedPage(SpeedHistory history)
        {
            bool offline = true;
            bool failUpload = true;
            bool holdUpload = false;
            int blockedUploads = 0;
            int requests = 0;
            int identifications = 0;
            int uploadMilliseconds = 2000;
            string link = "Wi-Fi";
            string wifiName = "Maison été";
            var engine = new InternetSpeedTest((phase, size, ct) => {
                Interlocked.Increment(ref requests);
                if (offline) throw new System.Net.WebException("Serveur inaccessible", System.Net.WebExceptionStatus.ConnectFailure);
                if (phase == SpeedPhase.Upload && failUpload) throw new IOException("Coupure simulée");
                if (phase == SpeedPhase.Upload && holdUpload)
                {
                    Interlocked.Increment(ref blockedUploads);
                    ct.WaitHandle.WaitOne(10000);
                    ct.ThrowIfCancellationRequested();
                }
                if (phase == SpeedPhase.Latency) return TimeSpan.FromMilliseconds(27);
                return TimeSpan.FromMilliseconds(phase == SpeedPhase.Upload ? uploadMilliseconds : 2000);
            }, delegate {
                Interlocked.Increment(ref identifications);
                if (link == null) throw new System.Net.WebException("Métadonnées indisponibles");
                return new SpeedConnection { PublicIPv4 = "203.0.113.8", PublicIPv6 = "2001:db8::8",
                    Provider = "Opérateur témoin", Link = link, WifiName = link == "Wi-Fi" ? wifiName : null };
            });
            var window = new Shell(delegate { return new List<Device>(); }, null, null, engine, speedHistory: history).Window;
            Action<Button> click = b => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var home = (Button)window.FindName("HomeNav");
            var speedLink = window.FindName("SpeedNav") as Button;
            Check(speedLink != null, "Le menu latéral ouvre directement le test de débit");
            click(speedLink);
            var speedView = (StackPanel)window.FindName("SpeedPage");
            Check(window.FindName("SpeedHistory") is StackPanel && window.FindName("SpeedNetworkDetails") is TextBlock,
                "La page de débit présente l’historique et les détails de la connexion");
            Check(speedView.Visibility == Visibility.Visible && ((TextBlock)window.FindName("Crumb")).Text.Contains("débit")
                && requests == 0, "Accès direct à la page réseau sans démarrer la mesure");
            var networkGroup = window.FindName("MenuFamilyReseau") as ToggleButton;
            Check(networkGroup != null && networkGroup.IsChecked == true,
                "Le menu déplie la famille Réseau à l’ouverture du test de débit");
            Check(AutomationProperties.GetName(speedLink).Contains("débit") && speedLink.ToolTip != null,
                "Le raccourci réseau garde son nom accessible en mode compact");
            click(home);
            Action open = delegate {
                var entry = FindAction((StackPanel)window.FindName("OtherPage"), "Ouvrir le test de débit");
                Check(entry != null, "Test de débit accessible depuis le catalogue");
                click(entry);
            };
            open();
            Check(speedView.Visibility == Visibility.Visible && ((TextBlock)window.FindName("PageTitle")).Text == "Test de débit Internet"
                && AutomationProperties.GetItemStatus(speedLink) == "Page actuelle"
                && string.IsNullOrEmpty(AutomationProperties.GetItemStatus(home)),
                "Le catalogue sélectionne le même outil dans le rail");
            var start = (Button)window.FindName("SpeedStart");
            var cancel = (Button)window.FindName("SpeedCancel");
            var status = (TextBlock)window.FindName("SpeedStatus");
            var latency = (TextBlock)window.FindName("SpeedLatency");
            var down = (TextBlock)window.FindName("SpeedDownload");
            var up = (TextBlock)window.FindName("SpeedUpload");
            var downBar = (ProgressBar)window.FindName("SpeedDownloadBar");
            var upBar = (ProgressBar)window.FindName("SpeedUploadBar");
            var stagePanel = (DockPanel)window.FindName("SpeedStage");
            var stageLabel = (TextBlock)window.FindName("SpeedStageLabel");
            Check(requests == 0 && identifications == 0 && start.IsEnabled, "Ouverture sans trafic Internet");
            Action<Func<bool>> wait = delegate(Func<bool> done) {
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (!done() && DateTime.UtcNow < deadline)
                    window.Dispatcher.Invoke(DispatcherPriority.Background, new Action(delegate {}));
                Check(done(), "Transition du test de débit terminée");
            };
            click(start);
            wait(() => start.IsEnabled);
            Check(status.Text.Contains("Latence") && status.Text.Contains("impossible") &&
                latency.Text == "—" && down.Text == "—" && up.Text == "—", "Serveur inaccessible au démarrage sans résultat inventé");
            offline = false;
            click(start);
            wait(() => start.IsEnabled);
            Check(latency.Text == "27 ms" && down.Text.Contains("Mbit/s") && up.Text == "—" &&
                status.Text.ToLowerInvariant().Contains("envoi"), "Coupure en envoi avec conservation des mesures réussies");
            failUpload = false;
            holdUpload = true;
            click(start);
            Check(up.Text == "—" && down.Text == "—" && !start.IsEnabled, "Relance sans ancienne valeur ni double lancement");
            wait(() => Volatile.Read(ref blockedUploads) == 1);
            Check(stagePanel.Visibility == Visibility.Visible && stageLabel.Text == "Étape 4 sur 4" &&
                ((ProgressBar)window.FindName("SpeedProgress")).Value == 3 && down.Text.Contains("Mbit/s") && up.Text == "—",
                "Le suivi distingue l’envoi en cours des trois étapes terminées");
            click(cancel);
            wait(() => start.IsEnabled);
            Check(status.Text.Contains("annulé") && up.Text == "—", "Annulation du transfert dans la page");
            Check(stagePanel.Visibility == Visibility.Collapsed && ((TextBlock)window.FindName("SpeedUploadBytes")).Text == "Non mesuré",
                "L’annulation efface les indicateurs de mesure en cours");
            click(start);
            wait(() => Volatile.Read(ref blockedUploads) == 2);
            click(speedLink);
            Check(!start.IsEnabled && !status.Text.Contains("annulé"), "Rester sur le test de débit préserve la mesure en cours");
            click(home);
            wait(() => start.IsEnabled);
            open();
            Check(status.Text.Contains("annulé"), "Le départ de la page annule la mesure");
            Check(history.Load().Count == 0, "Les tests interrompus et en échec ne prennent pas de place dans l’historique");
            holdUpload = false;
            click(start);
            wait(() => start.IsEnabled);
            Check(status.Text.Contains("terminé") && up.Text == "0,26 Mbit/s" && down.Text == up.Text,
                "Relance complète avec résultats et unités en français");
            Check(downBar.Value == upBar.Value && upBar.Value > 0 && downBar.Maximum == upBar.Maximum,
                "Deux débits identiques donnent deux barres de même longueur");
            var previous = history.Load().Single();
            var historyPanel = (StackPanel)window.FindName("SpeedHistory");
            Check(((TextBlock)window.FindName("SpeedDownloadBytes")).Text == "0,03 Mo/s" &&
                ((TextBlock)window.FindName("SpeedUploadBytes")).Text == "0,03 Mo/s" &&
                ((TextBlock)window.FindName("SpeedJitter")).Text == "Gigue : 0 ms" &&
                ((TextBlock)window.FindName("SpeedNetworkDetails")).Text.Contains("IPv4 publique : 203.0.113.8") &&
                ((TextBlock)window.FindName("SpeedNetworkDetails")).Text.Contains("IPv6 publique : 2001:db8::8") &&
                previous.Connection.Provider == "Opérateur témoin" && previous.Connection.WifiName == "Maison été",
                "Unités, gigue et connexion du test affichées et conservées");
            wifiName = "Bureau";
            uploadMilliseconds = 4000;
            click(start);
            wait(() => start.IsEnabled);
            Check(Math.Abs(upBar.Value / downBar.Value - 0.5) < 0.0001 && downBar.Maximum == upBar.Maximum &&
                downBar.Maximum == downBar.Value, "Les barres de réception et d’envoi partagent une échelle proportionnelle");
            link = "Ethernet";
            click(start);
            wait(() => start.IsEnabled);
            Check(Descendants(historyPanel).OfType<TextBlock>().Any(t => t.Text.Contains("Ethernet")) &&
                Descendants(historyPanel).OfType<TextBlock>().Any(t => t.Text.Contains("Wi-Fi · Maison été")) &&
                Descendants(historyPanel).OfType<TextBlock>().Any(t => t.Text.Contains("Wi-Fi · Bureau")) &&
                history.Load()[0].Connection.WifiName == null && history.Load()[2].CompletedUtcTicks == previous.CompletedUtcTicks &&
                history.Load()[2].Connection.PublicIPv6 == "2001:db8::8" && history.Load()[2].Connection.PublicIPv4 == "203.0.113.8",
                "Deux SSID datés et les deux familles IP restent consultables après un passage en Ethernet");
            link = null;
            click(start);
            wait(() => start.IsEnabled);
            Check(status.Text.Contains("terminé") && history.Load().Count == 4 &&
                ((TextBlock)window.FindName("SpeedNetworkDetails")).Text.Contains("indisponible"),
                "Un échec d’identification préserve les mesures et ne reprend pas l’ancien FAI");
            holdUpload = true;
            click(start);
            wait(() => Volatile.Read(ref blockedUploads) == 3);
            Check(upBar.Value == 0 && downBar.Value == 0, "La relance efface la comparaison du test précédent");
            window.Close();
            wait(() => start.IsEnabled);
            Check(status.Text.Contains("annulé"), "La fermeture arrête le test");
        }

        static void CheckNetworkRepairPage(Window shellWindow)
        {
            Action<Button> click = b => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var direct = shellWindow.FindName("NetworkRepairNav") as Button;
            Check(direct != null, "Dépannage disponible depuis la rubrique Réseau");
            click(direct);
            Check(((StackPanel)shellWindow.FindName("NetworkRepairView")).Visibility == Visibility.Visible, "Page de dépannage ouverte");
            click((Button)shellWindow.FindName("HomeNav"));
            var entry = FindAction((StackPanel)shellWindow.FindName("OtherPage"), "Ouvrir le dépannage réseau");
            Check(entry != null, "Dépannage disponible depuis Tous les outils");
            click(entry);

            Window window;
            using (var xaml = typeof(Shell).Assembly.GetManifestResourceStream("AtelierPC.Fenetre.xaml"))
                window = (Window)System.Windows.Markup.XamlReader.Load(xaml);
            int checks = 0, repairs = 0;
            var card = new NetworkLink { Id = "a", Name = "Ethernet", HasIPv4 = true, HasGateway = true };
            var dns = NetworkDiagnosis.Assess(new[] { card }, WebProbe.DnsFailure);
            var healthy = NetworkDiagnosis.Assess(new[] { card }, WebProbe.Healthy);
            new NetworkRepairPage(window, delegate { return ++checks == 1 ? dns : healthy; },
                delegate(NetworkDiagnosis requested, IntPtr owner) { repairs++; return requested == dns ? "Cache vidé." : "Demande incorrecte."; });
            var start = (Button)window.FindName("NetworkCheck");
            var fix = (Button)window.FindName("NetworkFix");
            Action wait = delegate {
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (!start.IsEnabled && DateTime.UtcNow < deadline)
                    window.Dispatcher.Invoke(DispatcherPriority.Background, new Action(delegate {}));
                Check(start.IsEnabled, "Dépannage réseau disponible après le contrôle");
            };
            Check(checks == 0 && fix.Visibility == Visibility.Collapsed, "Ouverture du dépannage sans action système");
            click(start);
            wait();
            Check(checks == 1 && repairs == 0 && fix.Visibility == Visibility.Visible && fix.Content.ToString().Contains("DNS"),
                "Le diagnostic propose la correction et attend le choix de la personne");
            click(fix);
            wait();
            Check(repairs == 1 && checks == 2 && fix.Visibility == Visibility.Collapsed &&
                ((TextBlock)window.FindName("NetworkResultTitle")).Text.Contains("vérifié") &&
                ((TextBlock)window.FindName("NetworkFeedback")).Text.Contains("Cache vidé."),
                "La correction demandée est suivie d’une vérification indépendante");
            window.Close();
        }

        static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            yield return root;
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
                foreach (var entry in Descendants(child)) yield return entry;
        }

        static Button FindAction(DependencyObject root, string label)
        {
            return Descendants(root).OfType<Button>().FirstOrDefault(button =>
                string.Equals(button.Content as string, label) || AutomationProperties.GetName(button) == label);
        }
    }
}
