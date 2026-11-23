using System;
using System.Net;

namespace AtelierPC
{
    internal static partial class Tests
    {
        static void CheckRouterBrowser()
        {
            var gateway = IPAddress.Parse("192.168.19.1");
            var other = IPAddress.Parse("192.168.19.20");
            var outside = IPAddress.Parse("192.0.2.20");
            var network = new LocalNetwork("Test", 4, IPAddress.Parse("192.168.19.10"),
                IPAddress.Parse("255.255.255.0"), new[] { gateway });
            Uri uri;
            Check(RouterBrowser.TryWebAddress("https://192.168.19.1:8443/clients", out uri) &&
                RouterBrowser.IsLocalManagement(network, uri, host => new[] { outside }), "L’interface peut utiliser un port HTTPS personnalisé");
            Check(!RouterBrowser.TryWebAddress("file:///C:/windows", out uri) &&
                !RouterBrowser.TryWebAddress("https://user:pass@192.168.19.1", out uri) &&
                !RouterBrowser.TryWebAddress("javascript:alert(1)", out uri), "Seules les adresses web sans identifiants sont acceptées");
            Check(RouterBrowser.TryWebAddress("http://box.lan/clients", out uri) &&
                RouterBrowser.IsLocalManagement(network, uri, host => new[] { gateway }) &&
                !RouterBrowser.IsLocalManagement(network, uri, host => new[] { gateway, outside }) &&
                !RouterBrowser.IsLocalManagement(network, uri, host => new IPAddress[0]), "Toutes les réponses DNS du nom choisi doivent appartenir au réseau sélectionné");
            Check(RouterBrowser.TryWebAddress("http://192.168.19.20", out uri) &&
                RouterBrowser.IsLocalManagement(network, uri, host => new[] { outside }) &&
                RouterBrowser.TryWebAddress("https://borne.lan/", out uri) && RouterBrowser.IsLocalManagement(network, uri, host => new[] { other }),
                "Une borne mesh du sous-réseau peut être désignée par son adresse ou son nom");
            foreach (string denied in new[] { "http://192.168.19.10", "https://192.0.2.20", "http://127.0.0.1", "http://192.168.19.255" })
                Check(RouterBrowser.TryWebAddress(denied, out uri) && !RouterBrowser.IsLocalManagement(network, uri, host => new[] { gateway }),
                    "L’adresse n’est pas une interface d’administration sur ce réseau · " + denied);
            Check(RouterBrowser.DecodePage("\"Nom\\tIPv4\\nTéléphone\\t192.168.19.8\"") ==
                "Nom\tIPv4\nTéléphone\t192.168.19.8" && RouterBrowser.DecodePage("null") == null,
                "Le résultat JSON du navigateur conserve les colonnes, les lignes et les accents");
            string script = RouterBrowser.CaptureScript(new Uri("https://box.lan:444/clients?session=private"));
            Check(script.Contains("box.lan:444") && !script.Contains("session=private") && script.Contains("location.origin"),
                "La lecture de la page reçoit uniquement l’origine autorisée");
        }
    }
}
