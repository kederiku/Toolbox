using System.Linq;

namespace AtelierPC
{
    internal static partial class Tests
    {
        static void CheckRouterClientList()
        {
            const string first = "02:AA:00:00:00:01";
            const string second = "02:AA:00:00:00:02";
            foreach (var value in new[] { "02-aa-00-00-00-01", "02aa.0000.0001", "02aa00000001", first })
                Check(RouterClientList.NormalizeMac(value) == first, "Adresse matérielle unicast locale normalisée");
            foreach (var value in new[] { null, "", "00:00:00:00:00:00", "ff:ff:ff:ff:ff:ff", "01:00:5e:00:00:01",
                "a02aa00000001b", "02:aa:00:00:0:1", "2001:db8:02:aa:00:00:00:01", "02-aa:00-00-00-01" })
                Check(RouterClientList.NormalizeMac(value) == null, "Une valeur non unicast ou mal formée est écartée");

            var table = RouterClientList.Parse("Interface\tÉtat\tAdresse MAC\tNom de l’équipement\tAdresse IPv6\n" +
                "Wi-Fi\tConnecté\t02-aa-00-00-00-01\tConsole\t2001:0db8:0:0::2\n" +
                "Ethernet\tHors ligne\t" + second + "\tInconnu\t\n" +
                "Wi-Fi\tInconnu\t02:AA:00:00:00:03\t\t\n");
            Check(table.Count == 3 && table[0].Name == "Console" && table[0].Mac == first &&
                table[0].Address == "2001:db8::2" && table[0].Active == true, "Colonnes françaises réordonnées et IPv6");
            Check(table[1].Active == false && table[1].Name == "" && table[1].Address == "" &&
                table[2].Active == null, "État négatif distinct d’un état indéterminé");
            Check(table[0].StateLabel == "Connecté" && table[1].StateLabel == "Hors ligne" &&
                table[2].StateLabel == "Non précisé", "États lisibles de chaque entrée");

            var csv = RouterClientList.Parse("MAC Address,Device Name,IP Address,Online\r\n" +
                first + ",\"Télé, \"\"Salon\"\"\",192.168.1.12,no\r\n" +
                second + ",\"Tablette\r\nde Léa\",,yes");
            Check(csv.Count == 2 && csv[0].Name == "Télé, \"Salon\"" && csv[0].Active == false &&
                csv[1].Name == "Tablette de Léa" && csv[1].Active == true, "Guillemets CSV, virgule et retour dans le nom");
            var shifted = RouterClientList.Parse("Name;Address MAC;IP address;Status\n" +
                "Imprimante;" + first + ";192.168.1.16;en ligne\nDécalée;Erreur;" + second + ";10.0.0.2;online");
            Check(shifted.Count == 2 && shifted[0].Name == "Imprimante" && shifted[0].Address == "192.168.1.16" &&
                shifted[1].Name == "" && shifted[1].Active == null, "Ligne décalée sans association de colonnes incertaine");

            var blocks = RouterClientList.Parse("Nom de l’appareil : Station de travail\nMAC : " + first +
                "\nIP : 192.168.2.25\nStatut : Actif\n\nDevice Name\nunknown\nMAC Address\n" + second + "\nStatus\nDisconnected");
            Check(blocks.Count == 2 && blocks[0].Name == "Station de travail" && blocks[0].Active == true &&
                blocks[0].Address == "192.168.2.25" && blocks[1].Name == "" && blocks[1].Active == false,
                "Fiches avec libellés français et anglais, séparés ou sur la même ligne");
            var isolated = RouterClientList.Parse("Name: Alice\n\nMAC: " + first + "\n\nStatus: Online\n\n" + second);
            Check(isolated.Count == 2 && isolated.All(c => c.Name == "" && c.Active == null),
                "Les fiches isolées et les MAC seules ne s’échangent aucune information");
            var ambiguous = RouterClientList.Parse("MAC: " + first + "\nNom: Ambigu\nMAC: " + second);
            Check(ambiguous.Count == 2 && ambiguous.All(c => c.Name == ""), "Un nom entre deux MAC sans structure reste indéterminé");
            var repeated = RouterClientList.Parse("Nom: Télé\nMAC: " + first + "\nNom: Mobile\nMAC: " + second);
            Check(repeated.Count == 2 && repeated[0].Name == "Télé" && repeated[1].Name == "Mobile",
                "Les fiches consécutives de même structure gardent leurs noms");

            var loose = RouterClientList.Parse("Ethernet\t" + first + "\t192.168.3.13\n" +
                "unknown\t" + second + "\n\n02:aa:00:00:00:03\n\n2001:db8:02:aa:00:00:00:01");
            Check(loose.Count == 3 && loose.All(c => c.Name == "" && c.Active == null) &&
                loose[0].Address == "192.168.3.13", "MAC seules et tableau sans en-tête, sans nom déduit de la liaison");
            var conflict = RouterClientList.Parse("Name\tHostname\tIP\tIPv4\tMAC\nCaméra\tAutre\t192.168.1.2\t192.168.1.3\t" + first);
            Check(conflict.Count == 1 && conflict[0].Name == "" && conflict[0].Address == "",
                "Des valeurs contradictoires ne choisissent pas arbitrairement un nom ou une adresse");
            var misleading = RouterClientList.Parse("Wi-Fi name\tGateway IP\tMAC address\tLast online\n" +
                "Orange bureau\t192.168.1.1\t" + first + "\tOnline");
            Check(misleading.Count == 1 && misleading[0].Name == "" && misleading[0].Address == "" &&
                misleading[0].Active == null, "Seuls les intitulés exacts décrivent le client");
            Check(RouterClientList.Parse("Nom\tMAC\tActif\nEthernet\t" + first + "\tOui").Single().Active == true,
                "Un client actif sans nom ni IP est conservé");
            var addresses = RouterClientList.Parse("IP\tMAC\n192.168.1.20\t" + first + "\n\t" + first +
                "\n192.168.1.3\t" + first + "\n2001:db8::2\t" + first);
            Check(addresses.OrderBy(c => c.AddressSortKey).Select(c => c.Address).SequenceEqual(
                new[] { "192.168.1.3", "192.168.1.20", "2001:db8::2", "" }), "Tri numérique des adresses des deux familles");
            var noMac = RouterClientList.Parse("Device name,IP address,Status\nCaméra,192.168.1.21,Online\n,192.168.1.22,Online\n" +
                "\nNom;MAC;IPv6\nConsole;;2001:db8::4\nImprimante;01:00:5e:00:00:01;192.168.1.3\n\nTélé\t192.168.1.17");
            Check(noMac.Count == 2 && noMac[0].Name == "Caméra" && noMac[0].Mac == "" && noMac[0].Active == true &&
                noMac[1].Name == "Console" && noMac[1].Mac == "" && noMac[1].Address == "2001:db8::4",
                "Une table de clients identifiée conserve les IP nommées sans MAC disponible");
            var card = RouterClientList.Parse("Nom : Projecteur\tAdresse MAC : " + first +
                "\tÉtat : Connecté\tAdresse IP : 192.168.1.40");
            Check(card.Count == 1 && card[0].Name == "Projecteur" && card[0].Active == true && card[0].Address == "192.168.1.40",
                "Une carte rendue en ligne conserve les valeurs de ses libellés");
            var status = RouterClientList.Parse("MAC Address\n" + first + "\nStatus\nOnline\n\nOnline\t" + second);
            Check(status.Count == 2 && status.All(c => c.Active == true), "Un statut explicite reste lisible en fiche ou en ligne sans en-tête");
            var twoColumns = RouterClientList.Parse("Nom\tLampe\nMAC\t" + first + "\nStatut\tConnecté");
            Check(twoColumns.Count == 1 && twoColumns[0].Name == "Lampe" && twoColumns[0].Active == true,
                "Une fiche à deux colonnes accepte un état traduit");
            var extra = RouterClientList.Parse("Nom : Lampe\tFabricant : Hexa\tMAC : " + first +
                "\n\nBSSID : " + second);
            Check(extra.Count == 1 && extra[0].Name == "Lampe", "Les champs étrangers ne donnent ni un nom ni une MAC client");
            var badIp = RouterClientList.Parse("Client\tAdresse Internet\nBalance\t192.168.1.9\n" +
                "Proxy\t[2001:db8::4]:80\nAbsent\t0.0.0.0");
            Check(badIp.Count == 1 && badIp[0].Name == "Balance", "Une entrée IP seule exige une adresse de client exploitable");
            var copied = RouterClientList.Parse("Device name  IP address  MAC Address  Connection\n" +
                "Écran mural  192.168.1.30  " + first + "  Connected\n" +
                "Autre écran  192.168.1.31  " + second + "  Ethernet");
            Check(copied.Count == 2 && copied[0].Name == "Écran mural" && copied[0].Active == true &&
                copied[1].Active == null, "Un tableau en texte à colonnes espacées garde les noms composés et les états explicites");
            var broken = RouterClientList.Parse("Nom;MAC;IP\n\"non terminé;" + first +
                ";192.168.1.4\nConsole;" + second + ";192.168.1.8");
            Check(broken.Count == 1 && broken[0].Mac == second && broken[0].Name == "Console",
                "Une ligne CSV incomplète n’absorbe pas le client suivant");
            var large = RouterClientList.Parse(string.Join("\n", Enumerable.Repeat(first, 8192)));
            Check(large.Count > 0 && large.Count < 8192 && large.All(c => c.Mac == first),
                "Une liste très longue conserve un nombre borné de clients complets");
            var inline = RouterClientList.Parse("Nom : Vidéo, salon\nMAC: " + first +
                "\n\nNom: Lampe;MAC: " + second + ";Statut: Inactif\n\nNom:\tRadio\tMAC : " + first + "\tStatut:\tOnline");
            Check(inline.Count == 3 && inline[0].Name == "Vidéo, salon" && inline[1].Name == "Lampe" &&
                inline[1].Active == false && inline[2].Name == "Radio" && inline[2].Active == true,
                "Les libellés en ligne se distinguent d’un en-tête et d’un nom comportant une virgule");
        }
    }
}
