using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TauConfigManager.Core;

public static class NetworkAddressHelper
{
    public static IReadOnlyList<IPAddress> GetLocalIPv4Addresses()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
            .Select(a => a.Address)
            .Distinct()
            .ToList();
    }
}

