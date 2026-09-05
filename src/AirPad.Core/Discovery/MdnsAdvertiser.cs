using System.Text.RegularExpressions;
using AirPad.Core.Config;
using AirPad.Core.Logging;
using Makaretu.Dns;

namespace AirPad.Core.Discovery;

/// <summary>
/// Advertises the receiver on the LAN via mDNS/DNS-SD (Bonjour-compatible,
/// no Apple software required) so the PC appears in the iPad's
/// Control Center → Screen Mirroring list.
/// </summary>
public sealed class MdnsAdvertiser : IDisposable
{
    public const string AirPlayService = "_airplay._tcp";
    public const string AirTunesService = "_raop._tcp";

    private readonly AirPadLogger _log;
    private MulticastService? _mdns;
    private ServiceDiscovery? _sd;
    private bool _disposed;

    public bool IsAdvertising { get; private set; }

    public MdnsAdvertiser(AirPadLogger log)
    {
        _log = log;
    }

    public Task StartAsync(AirPadSettings settings)
    {
        if (IsAdvertising)
            return Task.CompletedTask;

        var deviceId = (settings.DeviceId ?? "").Trim();
        if (!Regex.IsMatch(deviceId, "^(([0-9a-fA-F]{2}):){5}([0-9a-fA-F]{2})$"))
            throw new ArgumentException("DeviceId must be a MAC address like 11:22:33:44:55:66.", nameof(settings));

        var instance = string.IsNullOrWhiteSpace(settings.ReceiverName)
            ? AirPadSettings.DefaultReceiverName()
            : settings.ReceiverName.Trim();
        var deviceIdInstance = deviceId.Replace(":", "", StringComparison.Ordinal).ToUpperInvariant();

        foreach (var ip in MulticastService.GetIPAddresses())
            _log.Info($"Local IP address: {ip}");

        _mdns = new MulticastService();
        _sd = new ServiceDiscovery(_mdns);

        _mdns.NetworkInterfaceDiscovered += (_, e) =>
        {
            foreach (var nic in e.NetworkInterfaces)
                _log.Info($"mDNS network interface: {nic.Name}");
        };

        // RAOP (audio + mirror control plane). Proven record set compatible
        // with iPadOS Screen Mirroring discovery.
        var airTunes = new ServiceProfile($"{deviceIdInstance}@{instance}", AirTunesService, (ushort)settings.AirTunesPort);
        airTunes.AddProperty("ch", "2");
        airTunes.AddProperty("cn", "2,3");
        airTunes.AddProperty("et", "0,3,5");
        airTunes.AddProperty("md", "0,1,2");
        airTunes.AddProperty("sr", "44100");
        airTunes.AddProperty("ss", "16");
        airTunes.AddProperty("da", "true");
        airTunes.AddProperty("sv", "false");
        airTunes.AddProperty("ft", "0x5A7FFFF7,0x1E");
        airTunes.AddProperty("am", "AppleTV5,3");
        airTunes.AddProperty("pk", "29fbb183a58b466e05b9ab667b3c429d18a6b785637333d3f0f3a34baa89f45e");
        airTunes.AddProperty("sf", "0x4");
        airTunes.AddProperty("tp", "UDP");
        airTunes.AddProperty("vn", "65537");
        airTunes.AddProperty("vs", "220.68");
        airTunes.AddProperty("vv", "2");

        var airPlay = new ServiceProfile(instance, AirPlayService, (ushort)settings.AirPlayPort);
        airPlay.AddProperty("deviceid", deviceId);
        airPlay.AddProperty("features", "0x5A7FFFF7,0x1E");
        airPlay.AddProperty("flags", "0x4");
        airPlay.AddProperty("model", "AppleTV5,3");
        airPlay.AddProperty("pk", "29fbb183a58b466e05b9ab667b3c429d18a6b785637333d3f0f3a34baa89f45e");
        airPlay.AddProperty("pi", "aa072a95-0318-4ec3-b042-4992495877d3");
        airPlay.AddProperty("srcvers", "220.68");
        airPlay.AddProperty("vv", "2");

        _sd.Advertise(airTunes);
        _sd.Advertise(airPlay);
        _mdns.Start();

        IsAdvertising = true;
        _log.Info("mDNS service registered");
        _log.Info($"Receiver '{instance}' is now visible to iPads on the local network (AirPlay TCP {settings.AirPlayPort}, AirTunes TCP {settings.AirTunesPort})");
        return Task.CompletedTask;
    }

    public void Stop()
    {
        if (!IsAdvertising)
            return;
        try
        {
            _sd?.Dispose();
            _mdns?.Stop();
            _mdns?.Dispose();
        }
        catch (Exception ex)
        {
            _log.Warn($"mDNS shutdown warning: {ex.Message}");
        }
        finally
        {
            _sd = null;
            _mdns = null;
            IsAdvertising = false;
            _log.Info("mDNS service unregistered");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
    }
}
