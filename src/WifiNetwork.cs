using System;
using System.Runtime.InteropServices;
using Windows.Networking.Connectivity;

namespace AtelierPC
{
    internal static class WifiNetwork
    {
        internal static string ConnectedName(Guid adapterId)
        {
            try
            {
                string name = null;
                foreach (var profile in NetworkInformation.GetConnectionProfiles())
                {
                    if (!profile.IsWlanConnectionProfile || profile.NetworkAdapter == null ||
                        profile.NetworkAdapter.NetworkAdapterId != adapterId ||
                        profile.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.None) continue;
                    string ssid = profile.WlanConnectionProfileDetails.GetConnectedSsid();
                    if (string.IsNullOrEmpty(ssid)) continue;
                    if (name != null && name != ssid) return null;
                    name = ssid;
                }
                return name;
            }
            catch (UnauthorizedAccessException) { }
            catch (COMException) { }
            catch (InvalidOperationException) { }
            catch (NotSupportedException) { }
            return null;
        }
    }
}
