using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace AtelierPC
{
    internal static partial class Tests
    {
        static Window NetworkTestWindow()
        {
            using (var xaml = typeof(NetworkRepairPage).Assembly.GetManifestResourceStream("AtelierPC.Fenetre.xaml"))
                return (Window)System.Windows.Markup.XamlReader.Load(xaml);
        }

        static void RunNetworkButton(Window window, string name)
        {
            ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var start = (Button)window.FindName("NetworkCheck");
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!start.IsEnabled && DateTime.UtcNow < deadline)
                window.Dispatcher.Invoke(DispatcherPriority.Background, new Action(delegate {}));
            Check(start.IsEnabled, "Fin du contrôle réseau simulé");
        }

        static void CheckNetworkDetailsFeature()
        {
            var window = NetworkTestWindow();
            new NetworkRepairPage(window, () => NetworkDiagnosis.Assess(new NetworkLink[0], WebProbe.Unavailable),
                delegate { throw new Exception("Aucune commande prévue"); });
            RunNetworkButton(window, "NetworkCheck");
            var resultPanel = (StackPanel)((Border)window.FindName("NetworkResult")).Child;
            var details = resultPanel.Children.OfType<Expander>().SingleOrDefault();
            Check(details != null, "Les observations du diagnostic réseau sont consultables");
            var observation = ((TextBlock)details.Content).Text;
            Check(observation.Contains("Aucune carte active") && observation.Contains("Résolution DNS — non testée") &&
                observation.Contains("Serveur de contrôle — non testé"), "Sans liaison, les contrôles distants sont omis");
            window.Close();

            var link = new NetworkLink { Id = "id-personnel", Name = "Carte de Lea 192.168.1.33",
                HasIPv4 = true, HasGateway = true, GatewayAddress = System.Net.IPAddress.Parse("192.168.1.1") };
            var intercepted = NetworkTroubleshooter.Diagnose(new[] { link }, true, host => true,
                host => WebProbe.Intercepted, gateway => GatewayProbe.NoReply);
            Check(intercepted.Observations.Contains("redirection") && intercepted.Observations.Contains("nom de contrôle résolu") &&
                intercepted.Observations.Contains("Passerelle configurée — présente") && intercepted.Observations.Contains("aucune réponse au ping"),
                "Une interception, une résolution et un silence ICMP sont des observations distinctes");
            int requests = 0;
            var offline = NetworkTroubleshooter.Diagnose(new NetworkLink[0], true, host => { requests++; return false; },
                host => { requests++; return WebProbe.Unavailable; }, gateway => { requests++; return GatewayProbe.NoReply; });
            Check(requests == 0 && offline.Observations.Contains("non testé"), "Aucune sollicitation distante sans liaison");
            var partial = NetworkTroubleshooter.Diagnose(new[] { new NetworkLink { AddressUnknown = true, GatewayUnknown = true, Dhcp = true } },
                false, host => null, host => WebProbe.Unavailable, gateway => GatewayProbe.NoReply);
            Check(partial.Fix == NetworkFix.None && partial.Observations.Contains("Adressage — indéterminé") &&
                partial.Observations.Contains("Passerelle configurée — indéterminée") && partial.Observations.Contains("Résolution DNS — indéterminée"),
                "Des paramètres illisibles restent inconnus et ne justifient aucune commande");
            var mixedDns = NetworkTroubleshooter.Diagnose(new[] { link }, true, host => host == "www.microsoft.com",
                host => WebProbe.DnsFailure, gateway => GatewayProbe.Responded);
            Check(mixedDns.Fix == NetworkFix.None && mixedDns.Observations.Contains("second nom résolu"), "Un échec de nom isolé ne déclenche pas de purge DNS");
            var dns = NetworkTroubleshooter.Diagnose(new[] { link }, true, host => false,
                host => WebProbe.DnsFailure, gateway => GatewayProbe.Responded);
            Check(dns.Fix == NetworkFix.FlushDns && dns.Observations.Contains("échec pour deux noms"), "Deux noms en échec justifient la correction DNS ciblée");
            var endpointError = NetworkTroubleshooter.Diagnose(new[] { link }, true, host => true,
                host => { throw new System.Net.WebException(); }, gateway => { throw new InvalidOperationException(); });
            Check(endpointError.Observations.Contains("nom de contrôle résolu") && endpointError.Observations.Contains("IPv4 utilisable") &&
                endpointError.Observations.Contains("Réponse de la passerelle — indéterminée") &&
                endpointError.Observations.Contains("Serveur de contrôle — résultat indéterminé"),
                "Une mesure indisponible préserve les observations des autres contrôles");

            var broken = NetworkDiagnosis.Assess(new[] { new NetworkLink { Id = link.Id, Name = link.Name, Dhcp = true } }, WebProbe.Unavailable);
            foreach (bool cancelled in new[] { true, false })
            {
                window = NetworkTestWindow();
                var entries = new List<string>();
                int checks = 0;
                new NetworkRepairPage(window, delegate {
                    if (++checks == 1) return broken;
                    if (cancelled) throw new InvalidOperationException();
                    return NetworkDiagnosis.Assess(new[] { link }, WebProbe.Healthy);
                }, delegate {
                    if (cancelled) return "Réparation annulée lors de la demande d’autorisation Windows.";
                    throw new InvalidOperationException(link.Name);
                }, (title, summary) => entries.Add(title + Environment.NewLine + summary));
                RunNetworkButton(window, "NetworkCheck");
                RunNetworkButton(window, "NetworkFix");
                Check(checks == 2 && entries.Count == 2, "La tentative est suivie d’un contrôle même après un échec ou un refus");
                var attempt = entries.Last();
                Check(attempt.Contains("Avant") && attempt.Contains("Adresse IP à renouveler") && attempt.Contains("Après") &&
                    attempt.Contains(cancelled ? "annulée" : "confirmée") && attempt.Contains(cancelled ? "indéterminé" : "Accès Internet vérifié"),
                    "Le rapport conserve le diagnostic initial, la commande et la vérification finale");
                Check(entries.All(s => !s.Contains("Lea") && !s.Contains(link.Id) && !s.Contains("192.168.")),
                    "La copie réseau ne révèle aucun nom de carte, identifiant ou adresse");
                var comparison = ((TextBlock)((StackPanel)((Border)window.FindName("NetworkResult")).Child).Children.OfType<Expander>().Single().Content).Text;
                Check(comparison.Contains("Avant") && comparison.Contains("Après") && comparison.Contains("Adressage"),
                    "La comparaison reste consultable sur la page");
                window.Close();
            }
        }
    }
}
