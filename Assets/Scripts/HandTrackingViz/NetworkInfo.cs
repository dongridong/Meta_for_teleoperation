using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace HandTrackingViz
{
    // ====================================================================
    // NetworkInfo
    // --------------------------------------------------------------------
    // Finds the headset's own LAN IPv4 address — the address a PC has to
    // connect to for the TCP stream (tcp://<this>:5555). Shown on the control
    // panel's IP toggle so nobody has to dig through the Quest's Wi-Fi menu.
    //
    // Two methods, in order:
    //
    //   1. Route probe — "connect" a UDP socket to a public address and read
    //      back which local address the OS picked. UDP connect sends nothing;
    //      it only resolves the route, so this is free and needs no internet.
    //      It does need a default route, which an isolated robot LAN may not
    //      have — hence:
    //   2. Interface scan — the first usable IPv4 on an interface that is up,
    //      preferring Wi-Fi (wlan*). Android often reports OperationalStatus
    //      as Unknown rather than Up, so only Down interfaces are skipped.
    //
    // Returns null when neither finds an address (Wi-Fi off, not associated).
    // ====================================================================
    public static class NetworkInfo
    {
        // Any routable address works; nothing is ever sent to it.
        private static readonly IPEndPoint RouteProbe = new IPEndPoint(IPAddress.Parse("8.8.8.8"), 53);

        public static string GetLocalIPv4()
        {
            string viaRoute = ProbeDefaultRoute();
            return viaRoute ?? ScanInterfaces();
        }

        private static string ProbeDefaultRoute()
        {
            try
            {
                using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    socket.Connect(RouteProbe);
                    if (socket.LocalEndPoint is IPEndPoint local && IsUsable(local.Address))
                        return local.Address.ToString();
                }
            }
            catch (SocketException)
            {
                // No default route (isolated LAN) or no network at all.
            }
            return null;
        }

        private static string ScanInterfaces()
        {
            string fallback = null;
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus == OperationalStatus.Down) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (!IsUsable(ua.Address)) continue;
                        string ip = ua.Address.ToString();
                        if (ni.Name.StartsWith("wlan", StringComparison.OrdinalIgnoreCase))
                            return ip;
                        if (fallback == null)
                            fallback = ip;
                    }
                }
            }
            catch (Exception)
            {
                // Interface enumeration is best-effort on Android; a failure
                // just means "unknown", which the panel shows as such.
            }
            return fallback;
        }

        private static bool IsUsable(IPAddress address)
        {
            if (address == null || address.AddressFamily != AddressFamily.InterNetwork) return false;
            if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any)) return false;
            byte[] b = address.GetAddressBytes();
            return !(b[0] == 169 && b[1] == 254);   // link-local: no DHCP lease
        }
    }
}
