using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;

namespace AtelierPC
{
    internal static partial class Tests
    {
        sealed class ObservedConnection : TcpConnectionInformation
        {
            readonly IPEndPoint local, remote;
            readonly TcpState state;

            internal ObservedConnection(IPAddress source, IPAddress device, int remotePort, TcpState state)
            {
                local = new IPEndPoint(source, 52310);
                remote = new IPEndPoint(device, remotePort);
                this.state = state;
            }

            public override IPEndPoint LocalEndPoint { get { return local; } }
            public override IPEndPoint RemoteEndPoint { get { return remote; } }
            public override TcpState State { get { return state; } }
        }

        static void CheckLocalDevicePorts()
        {
            var source = IPAddress.Parse("192.0.2.10");
            var device = IPAddress.Parse("192.0.2.11");
            var other = IPAddress.Parse("192.0.2.12");
            var listeners = new[] {
                new IPEndPoint(source, 443), new IPEndPoint(IPAddress.Any, 80),
                new IPEndPoint(IPAddress.Any, 443), new IPEndPoint(IPAddress.Loopback, 5000),
                new IPEndPoint(other, 8000), new IPEndPoint(IPAddress.IPv6Any, 3000)
            };
            Check(LocalDevicePorts.ListeningPorts(source, listeners).SequenceEqual(new[] { 80, 443 }),
                "Les écoutes IPv4 de la carte sélectionnée et de toutes les interfaces sont triées et dédoublonnées");
            var connections = new[] {
                new ObservedConnection(source, device, 443, TcpState.Established),
                new ObservedConnection(source, device, 80, TcpState.Established),
                new ObservedConnection(source, device, 443, TcpState.Established),
                new ObservedConnection(other, device, 445, TcpState.Established),
                new ObservedConnection(source, other, 8080, TcpState.Established),
                new ObservedConnection(source, device, 9000, TcpState.TimeWait)
            };
            Check(LocalDevicePorts.EstablishedPorts(source, device, connections).SequenceEqual(new[] { 80, 443 }),
                "Seuls les ports distants des connexions établies entre les deux adresses sont retenus");

            var listener = new TcpListener(IPAddress.Loopback, 0);
            try
            {
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var loopbackSource = IPAddress.Parse("127.0.0.2");
                string self = LocalDevicePorts.Describe(IPAddress.Loopback, IPAddress.Loopback);
                Check(self.Contains("écoute") && self.Contains(port.ToString()), "Une écoute TCP réelle du PC est affichée");
                using (var client = new TcpClient(new IPEndPoint(loopbackSource, 0)))
                {
                    client.Connect(IPAddress.Loopback, port);
                    using (var peer = listener.AcceptSocket())
                    {
                        string remote = LocalDevicePorts.Describe(loopbackSource, IPAddress.Loopback);
                        Check(remote.Contains("établies") && remote.Contains(port.ToString()),
                            "L’observation affiche le port distant d’une connexion établie entre deux adresses de boucle locale");
                    }
                }

                string connected = LocalDevicePorts.CheckAsync(loopbackSource, IPAddress.Loopback, port,
                    CancellationToken.None).GetAwaiter().GetResult();
                Check(connected.Contains("Port accessible") && connected.Contains(port.ToString()) && listener.Pending(),
                    "Le test TCP confirme le port de l’écoute locale");
                using (Socket accepted = listener.AcceptSocket())
                {
                    Check(((IPEndPoint)accepted.RemoteEndPoint).Address.Equals(loopbackSource),
                        "La connexion provient de l’adresse locale sélectionnée");
                    accepted.ReceiveTimeout = 2000;
                    Check(accepted.Receive(new byte[1]) == 0, "Le test ferme sa connexion sans envoyer de données");
                }
                bool cancelled = false;
                try { LocalDevicePorts.CheckAsync(IPAddress.Loopback, IPAddress.Loopback, port,
                    new CancellationToken(true)).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled && !listener.Pending(), "Un test déjà annulé ne contacte pas l’appareil");
            }
            finally { listener.Stop(); }
            using (var reserved = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                reserved.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                int port = ((IPEndPoint)reserved.LocalEndPoint).Port;
                string refused = LocalDevicePorts.CheckAsync(IPAddress.Loopback, IPAddress.Loopback, port,
                    CancellationToken.None).GetAwaiter().GetResult();
                Check(refused.Contains("refusée") && refused.Contains(port.ToString()), "Une connexion refusée est identifiée");
            }
        }
    }
}
