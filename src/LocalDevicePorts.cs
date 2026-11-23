using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security;
using System.Threading;
using System.Threading.Tasks;

namespace AtelierPC
{
    internal static class LocalDevicePorts
    {
        internal static IEnumerable<int> ListeningPorts(IPAddress source, IEnumerable<IPEndPoint> listeners)
        {
            return listeners.Where(endpoint => endpoint.Address.Equals(source) || endpoint.Address.Equals(IPAddress.Any))
                .Select(endpoint => endpoint.Port).Distinct().OrderBy(port => port);
        }

        internal static IEnumerable<int> EstablishedPorts(IPAddress source, IPAddress device, IEnumerable<TcpConnectionInformation> connections)
        {
            return connections.Where(connection => connection.State == TcpState.Established &&
                connection.LocalEndPoint.Address.Equals(source) && connection.RemoteEndPoint.Address.Equals(device))
                .Select(connection => connection.RemoteEndPoint.Port).Distinct().OrderBy(port => port);
        }

        internal static string Describe(IPAddress source, IPAddress device)
        {
            try
            {
                var properties = IPGlobalProperties.GetIPGlobalProperties();
                if (source.Equals(device))
                {
                    string ports = string.Join(", ", ListeningPorts(source, properties.GetActiveTcpListeners()));
                    return ports.Length == 0 ? "Aucun port TCP en écoute sur " + source + " pour le moment." :
                        "Ports TCP en écoute sur " + source + " (y compris les écoutes sur toutes les interfaces IPv4) · " + ports;
                }
                string remotePorts = string.Join(", ", EstablishedPorts(source, device, properties.GetActiveTcpConnections()));
                return remotePorts.Length == 0 ? "Aucune connexion TCP établie entre " + source + " et " + device +
                    ". Windows n’observe actuellement aucun port distant pour cet appareil." :
                    "Ports TCP distants des connexions établies entre " + source + " et " + device + " · " + remotePorts;
            }
            catch (NetworkInformationException) { }
            catch (NotSupportedException) { }
            catch (UnauthorizedAccessException) { }
            catch (SecurityException) { }
            return "Windows ne peut pas lire les informations sur les ports TCP de ce PC.";
        }

        internal static async Task<string> CheckAsync(IPAddress source, IPAddress device, int port, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (port < 1 || port > IPEndPoint.MaxPort) throw new ArgumentOutOfRangeException("port");
            if (source == null || source.AddressFamily != AddressFamily.InterNetwork || source.Equals(IPAddress.Any))
                throw new ArgumentException("Une adresse IPv4 locale est nécessaire.", "source");
            if (device == null || device.AddressFamily != AddressFamily.InterNetwork || device.Equals(IPAddress.Any))
                throw new ArgumentException("Une adresse IPv4 d’appareil est nécessaire.", "device");

            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                SocketError outcome = SocketError.Success;
                try
                {
                    using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                    using (deadline.Token.Register(socket.Close))
                    {
                        socket.Bind(new IPEndPoint(source, 0));
                        deadline.CancelAfter(3000);
                        await Task.Factory.FromAsync<EndPoint>(socket.BeginConnect, socket.EndConnect,
                            new IPEndPoint(device, port), null).ConfigureAwait(false);
                    }
                }
                catch (SocketException ex) { outcome = ex.SocketErrorCode; }
                catch (ObjectDisposedException) { if (!deadline.IsCancellationRequested) throw; }
                token.ThrowIfCancellationRequested();
                if (deadline.IsCancellationRequested) outcome = SocketError.TimedOut;

                string endpoint = device + " (TCP " + port + ")";
                switch (outcome)
                {
                    case SocketError.Success: return endpoint + " · Port accessible depuis ce PC.";
                    case SocketError.ConnectionRefused: return endpoint + " · Connexion refusée.";
                    case SocketError.TimedOut: return endpoint + " · Délai dépassé, aucune réponse à temps.";
                    case SocketError.NetworkDown:
                    case SocketError.NetworkUnreachable:
                    case SocketError.HostDown:
                    case SocketError.HostUnreachable: return endpoint + " · Appareil ou réseau inaccessible.";
                    case SocketError.AddressNotAvailable: return "L’adresse locale " + source + " est indisponible.";
                    default: return endpoint + " · Test impossible (erreur réseau " + (int)outcome + ").";
                }
            }
        }
    }
}
