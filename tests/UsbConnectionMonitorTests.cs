using System;
using System.Linq;

namespace AtelierPC
{
    internal static partial class Tests
    {
        static void CheckUsbConnectionMonitor()
        {
            var time = new DateTime(2026, 11, 20, 14, 3, 1);
            const string privateId = @"USB\VID_1234&PID_5678\SERIE_PRIVEE";
            const string privatePath = "PCI_CONFIDENTIEL#USB(5)#USB(7)";
            var a = new Device(privateId, "Micro personnel", 0, null,
                new UsbEvidence(null, privatePath, null, "Micro personnel"));
            var same = new Device(privateId.ToLowerInvariant(), a.Name, 0, null, a.Usb);
            var b = new Device(@"USB\VID_1234&PID_5678\AUTRE", a.Name, 0, null, a.Usb);
            var empty = new Device[0];
            var monitor = new UsbConnectionMonitor();
            Check(monitor.Observe(new[] { a }, time).Count == 0, "Le premier relevé USB est silencieux.");
            Check(monitor.Observe(new[] { same }, time).Count == 0, "La casse d’une entrée ne signale aucune coupure.");
            var loss = monitor.Observe(empty, time.AddSeconds(1)).Single();
            Check(loss.LocalText.Contains("Micro personnel") && loss.LocalText.Contains("5 → 7"),
                "Le suivi local permet de retrouver le nom et la voie de l’appareil.");
            Check(loss.SharedText.Contains("14:03:02") && loss.SharedText.Contains("n°1") && loss.SharedText.Contains("7")
                && loss.SharedText.Contains("disparition"), "Le constat partageable situe et date la disparition.");
            Check(!new[] { "Micro personnel", "SERIE_PRIVEE", "VID_", "PCI_CONFIDENTIEL", "#USB", "5 → 7" }
                .Any(s => loss.SharedText.Contains(s)), "La version partageable écarte les identifiants et chemins.");
            Check(monitor.Observe(empty, time.AddSeconds(2)).Count == 0, "Une absence persistante ne compte qu’une fois.");
            var arrival = monitor.Observe(new[] { b }, time.AddSeconds(3)).Single();
            Check(arrival.SharedText.Contains("apparition") && arrival.SharedText.Contains("n°2")
                && arrival.SharedText.Contains("14:03:04") && !arrival.SharedText.Contains("retour"),
                "Un autre exemplaire sur la même prise apparaît sous un nouveau numéro.");
            var returned = monitor.Observe(new[] { b, same }, time.AddSeconds(4)).Single();
            Check(returned.SharedText.Contains("retour") && returned.SharedText.Contains("n°1")
                && returned.SharedText.Contains("14:03:05"), "Le même identifiant revient sous son numéro initial.");
            var again = monitor.Observe(new[] { b }, time.AddSeconds(5)).Single();
            Check(again.SharedText.Contains("2 disparitions"), "Le compteur porte sur les disparitions de chaque entrée.");
            Check(again.SharedText.Contains("câble") && again.SharedText.Contains("prise"),
                "Une coupure répétée propose un essai que le destinataire du rapport peut réaliser.");
            Check(monitor.Observe(new[] { b, a }, time.AddSeconds(6)).Single().SharedText.Contains("2 retours"),
                "Le deuxième cycle de coupure conserve le nombre de retours observés.");

            var iface = new Device(@"USB\VID_1234&PID_5678&MI_01\FONCTION", "Interface", 0, null, null,
                a.Id, UsbKind.Device);
            var root = new Device(@"USB\ROOT_HUB30\RACINE", "Hub racine", 0);
            monitor.ResetBaseline();
            monitor.Observe(new[] { a, iface, root }, time);
            Check(monitor.Observe(new[] { a }, time).Count == 0,
                "Les fonctions USB et hubs racines ne deviennent pas des débranchements.");
            monitor.Observe(empty, time);
            monitor.ResetBaseline();
            Check(monitor.Observe(empty, time).Count == 0 && monitor.Observe(new[] { a }, time).Single().SharedText.Contains("apparition"),
                "Après interruption, un branchement postérieur à la nouvelle référence est une apparition.");
            Check(monitor.Observe(new[] { a, same }, time).Count == 0 && monitor.Observe(new[] { same }, time).Count == 0,
                "Un identifiant ambigu interrompt le suivi individuel même si sa casse diffère.");
            var unknown = new Device("", "Entrée illisible", 0, null, null, null, UsbKind.Device);
            Check(monitor.Observe(new[] { unknown }, time).Count == 0 && monitor.Observe(new[] { a }, time).Count == 0,
                "Un identifiant USB illisible ne suffit pas pour déduire une disparition suivie d’un retour.");
            monitor = new UsbConnectionMonitor();
            monitor.Observe(new[] { a, same }, time);
            Check(monitor.Observe(new[] { a }, time).Count == 0, "Lever un doublon initial ne crée pas d’apparition.");
            monitor.Observe(new[] { unknown }, time);
            Check(monitor.Observe(new[] { b }, time).Count == 0, "Lire enfin un identifiant ne prouve pas une apparition.");

            var hub = new Device(@"USB\VID_1234&PID_1111\H", "Hub bureau", 0, null, null, root.Id, UsbKind.Hub);
            var child = new Device(privateId, a.Name, 0, null, a.Usb, hub.Id);
            monitor = new UsbConnectionMonitor();
            monitor.Observe(new[] { child, hub }, time);
            var group = monitor.Observe(empty, time).Single();
            Check(group.LocalText.Contains(hub.Name) && group.LocalText.Contains(child.Name)
                && !group.SharedText.Contains(child.Name) && !group.SharedText.Contains(hub.Name),
                "Un hub et son enfant disparus ensemble donnent un constat qui conserve les deux entrées.");
            Check(monitor.Observe(new[] { hub, child }, time).Count == 1, "Un retour groupé donne une seule notification.");
            var childOnly = monitor.Observe(new[] { hub }, time).Single();
            Check(childOnly.LocalText.Contains(child.Name) && !childOnly.LocalText.Contains(hub.Name),
                "Une disparition isolée sous un hub reste attribuée à l’enfant.");
        }
    }
}
