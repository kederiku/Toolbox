using System;
using System.Linq;

namespace AtelierPC
{
    internal enum NetworkFix { None, FlushDns, RenewAddress }
    internal enum WebProbe { Healthy, Unavailable, Intercepted, DnsFailure, Omitted, Unknown }
    internal enum DnsProbe { Omitted, Resolved, TwoNamesFailed, SecondNameResolved, Unknown }
    internal enum GatewayProbe { Omitted, Responded, NoReply, Unknown }

    internal sealed class NetworkLink
    {
        public string Id, Name;
        public bool HasIPv4, HasIPv6, HasGateway, Dhcp;
        public bool AddressUnknown { get; set; }
        public bool GatewayUnknown { get; set; }
        public System.Net.IPAddress GatewayAddress { get; set; }

        public bool CanRenew
        {
            get
            {
                return Dhcp && !AddressUnknown && !HasIPv4 && !HasIPv6 && !string.IsNullOrEmpty(Id) &&
                    !string.IsNullOrWhiteSpace(Name) && Name == Name.Trim() && Name[0] != '-' &&
                    Name.IndexOfAny(new[] { '"', '*', '?', '/', '\\' }) < 0 && !Name.Any(char.IsControl);
            }
        }
    }

    internal sealed class NetworkDiagnosis
    {
        public readonly string Title, Detail;
        public readonly NetworkFix Fix;
        public readonly NetworkLink Target;
        public string Observations { get; private set; }

        public string SharedSummary { get { return Title + Environment.NewLine + Observations; } }

        NetworkDiagnosis(string title, string detail, NetworkFix fix = NetworkFix.None, NetworkLink target = null)
        {
            Title = title; Detail = detail; Fix = fix; Target = target;
        }

        public static NetworkDiagnosis Assess(NetworkLink[] links, WebProbe probe)
        {
            bool offline = links.Length == 0 && probe == WebProbe.Unavailable;
            return Assess(links, offline ? WebProbe.Omitted : probe,
                offline ? DnsProbe.Omitted : probe == WebProbe.DnsFailure ? DnsProbe.TwoNamesFailed : DnsProbe.Omitted,
                GatewayProbe.Omitted, true);
        }

        public static NetworkDiagnosis Assess(NetworkLink[] links, WebProbe probe, DnsProbe dns,
            GatewayProbe gateway, bool inventoryComplete)
        {
            var diagnosis = Recommend(links, probe, dns, inventoryComplete);
            string linkText = links.Length == 0 ? (inventoryComplete ? "Aucune carte active" : "indéterminée")
                : links.Length + (links.Length == 1 ? " carte active" : " cartes actives");
            if (!inventoryComplete && links.Length > 0) linkText += " (inventaire partiel)";

            bool gatewayUnknown = !inventoryComplete || links.Any(l => l.GatewayUnknown);
            bool offline = inventoryComplete && links.Length == 0;
            string configured = links.Any(l => l.HasGateway) ? "présente" : gatewayUnknown ? "indéterminée" :
                offline ? "non testée" : "absente";

            diagnosis.Observations = string.Join(Environment.NewLine, new[] {
                "Liaison — " + linkText,
                "Adressage — " + DescribeAddresses(links, inventoryComplete),
                "Passerelle configurée — " + configured,
                "Réponse de la passerelle — " + DescribeGateway(gateway),
                "Résolution DNS — " + DescribeDns(dns),
                "Serveur de contrôle — " + DescribeServer(probe)
            });
            return diagnosis;
        }

        static string DescribeAddresses(NetworkLink[] links, bool inventoryComplete)
        {
            bool ipv4 = links.Any(l => l.HasIPv4), ipv6 = links.Any(l => l.HasIPv6);
            bool partial = !inventoryComplete || links.Any(l => l.AddressUnknown);
            if (!ipv4 && !ipv6)
            {
                if (partial) return "indéterminé";
                return links.Length == 0 ? "non testé" : "aucune adresse utilisable";
            }
            string families = ipv4 && ipv6 ? "IPv4 et IPv6 utilisables" : ipv4 ? "IPv4 utilisable" : "IPv6 utilisable";
            return partial ? families + " (lecture partielle)" : families;
        }

        static string DescribeGateway(GatewayProbe gateway)
        {
            switch (gateway)
            {
                case GatewayProbe.Responded: return "une passerelle a répondu au ping";
                case GatewayProbe.NoReply: return "aucune réponse au ping de la passerelle testée (le réseau peut filtrer ce test)";
                case GatewayProbe.Omitted: return "non testée";
                default: return "indéterminée";
            }
        }

        static string DescribeDns(DnsProbe dns)
        {
            switch (dns)
            {
                case DnsProbe.Resolved: return "nom de contrôle résolu";
                case DnsProbe.TwoNamesFailed: return "échec pour deux noms de contrôle";
                case DnsProbe.SecondNameResolved: return "premier nom non confirmé, second nom résolu";
                case DnsProbe.Omitted: return "non testée";
                default: return "indéterminée";
            }
        }

        static string DescribeServer(WebProbe probe)
        {
            switch (probe)
            {
                case WebProbe.Healthy: return "réponse Microsoft attendue reçue";
                case WebProbe.Intercepted: return "redirection ou contenu remplacé";
                case WebProbe.DnsFailure: return "injoignable, nom non résolu";
                case WebProbe.Unavailable: return "réponse attendue non obtenue";
                case WebProbe.Omitted: return "non testé";
                default: return "résultat indéterminé";
            }
        }

        static NetworkDiagnosis Recommend(NetworkLink[] links, WebProbe probe, DnsProbe dns, bool inventoryComplete)
        {
            if (probe == WebProbe.Healthy)
                return new NetworkDiagnosis("Accès Internet vérifié", "Le serveur de test Microsoft a répondu. Si un seul site ou logiciel reste inaccessible, vérifiez ses réglages ou réessayez plus tard.");
            if (links.Length == 0 && inventoryComplete)
                return new NetworkDiagnosis("Aucune connexion réseau active", "Connectez le PC au Wi-Fi ou branchez le câble Ethernet, puis relancez le diagnostic.");
            if (probe == WebProbe.Intercepted)
                return new NetworkDiagnosis("Connexion à valider", "Le réseau a redirigé le test ou remplacé sa réponse. Sur un Wi-Fi public, utilisez la notification de connexion de Windows pour vous identifier. Sur un réseau d’entreprise, vérifiez le proxy.");

            if (!links.Any(link => (link.HasIPv4 || link.HasIPv6) && link.HasGateway))
            {
                if (!inventoryComplete || links.Any(l => l.AddressUnknown || l.GatewayUnknown))
                    return new NetworkDiagnosis("Diagnostic réseau incomplet", "Relancez le diagnostic pour vérifier les cartes et leurs paramètres réseau.");
                var candidates = links.Where(link => link.CanRenew).ToArray();
                if (candidates.Length == 1)
                {
                    var link = candidates[0];
                    return new NetworkDiagnosis("Adresse IP à renouveler",
                        "La carte « " + link.Name + " » attend une adresse automatique. Windows peut en demander une nouvelle à votre box. La connexion de cette carte peut être brièvement interrompue.",
                        NetworkFix.RenewAddress, link);
                }
                if (!links.Any(link => link.HasIPv4 || link.HasIPv6))
                    return new NetworkDiagnosis("Adresse réseau à vérifier", "Les cartes actives disposent uniquement d’adresses locales au lien ou attendent une adresse. Vérifiez la connexion à la box et les réglages IP de la carte utilisée.");
                return new NetworkDiagnosis("Connexion à la box à vérifier", "Une carte dispose d’une adresse IP, mais aucune passerelle n’a été détectée sur une carte avec une adresse utilisable. Vérifiez le Wi-Fi ou le câble Ethernet et les paramètres de la passerelle.");
            }
            if (dns == DnsProbe.TwoNamesFailed)
                return new NetworkDiagnosis("Les noms Internet ne sont pas résolus", "Windows n’a pas pu retrouver les adresses de deux noms de test. Vider le cache DNS lui permet de redemander les adresses. Si le problème persiste, vérifiez le serveur DNS ou la box.",
                    NetworkFix.FlushDns);
            return new NetworkDiagnosis("Accès Internet non confirmé", "Le serveur de test Microsoft n’a pas fourni la réponse attendue. Essayez un autre site. Si plusieurs services échouent, vérifiez la box, la passerelle ou le proxy dans les paramètres réseau.");
        }
    }
}
