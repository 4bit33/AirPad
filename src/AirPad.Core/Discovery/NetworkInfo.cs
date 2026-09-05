using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AirPad.Core.Discovery;

/// <summary>
/// Local network inspection: enumerates usable IPv4 addresses,
/// detects the primary LAN address, and warns when peers may be
/// on a different subnet (e.g. PC on Ethernet, iPad on another VLAN).
/// </summary>
public static class NetworkInfo
{
    public static IReadOnlyList<IPAddress> GetUsableIPv4Addresses()
    {
        var result = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                var props = nic.GetIPProperties();
                foreach (var unicast in props.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;
                    if (IPAddress.IsLoopback(unicast.Address))
                        continue;
                    if (unicast.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                        continue; // link-local, not usable for AirPlay
                    if (!result.Contains(unicast.Address))
                        result.Add(unicast.Address);
                }
            }
        }
        catch
        {
            // Best effort only.
        }

        // Fallback to DNS resolution.
        if (result.Count == 0)
        {
            try
            {
                foreach (var ip in Dns.GetHostAddresses(Dns.GetHostName()))
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                        result.Add(ip);
                }
            }
            catch { }
        }

        return result;
    }

    public static IPAddress? GetPrimaryIPv4()
    {
        var all = GetUsableIPv4Addresses();
        if (all.Count == 0)
            return null;

        // Prefer a private LAN address (192.168/10/172.16) over anything else.
        foreach (var ip in all)
        {
            if (IsPrivateAddress(ip))
                return ip;
        }
        return all[0];
    }

    public static bool IsPrivateAddress(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        if (b[0] == 10) return true;
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
        if (b[0] == 192 && b[1] == 168) return true;
        return false;
    }

    /// <summary>
    /// Returns a human-readable warning when the local configuration looks
    /// suspicious for AirPlay (no private LAN address, AP isolation risk, ...).
    /// Returns null when everything looks fine.
    /// </summary>
    public static string? GetNetworkWarning()
    {
        var all = GetUsableIPv4Addresses();
        if (all.Count == 0)
            return "No usable IPv4 address found. Connect this PC to your local Wi-Fi or Ethernet network.";

        if (!all.Any(IsPrivateAddress))
            return "No private LAN address (192.168.x.x / 10.x.x.x / 172.16.x.x) detected. The iPad must be on the same local network; VPNs or public networks will block discovery.";

        if (all.Count > 1)
        {
            // Multiple subnets (e.g. Ethernet + Wi-Fi + VM adapters) are the #1
            // cause of "iPad cannot see the PC". Surface it early.
            var prefixes = all.Select(a => string.Join(".", a.ToString().Split('.').Take(3))).Distinct().ToList();
            if (prefixes.Count > 1)
                return $"Multiple local subnets detected ({string.Join(", ", all)}). Make sure the iPad joins the same Wi-Fi network as one of these addresses.";
        }

        return null;
    }
}
