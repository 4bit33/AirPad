using System.Net;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using AirPad.Core.Config;
using AirPad.Core.Logging;
using AirPad.Core.Protocol;
using Makaretu.Dns;

namespace AirPad.Core.Discovery;

/// <summary>
/// Advertises the receiver on the LAN via mDNS/DNS-SD (Bonjour-compatible,
/// no Apple software required) so the PC appears in the iPad's
/// Control Center → Screen Mirroring list. A registration keeps advertising the
/// addresses it was built with, so it registers again when the machine's
/// addresses change (hotspot toggled, VPN up, Wi-Fi roam).
/// </summary>
public sealed class MdnsAdvertiser : IDisposable
{
    public const string AirPlayService = "_airplay._tcp";
    public const string AirTunesService = "_raop._tcp";

    private sealed record Registration(string Instance, string DeviceId, ushort AirPlayPort, ushort AirTunesPort, string PublicKeyHex);

    private readonly AirPadLogger _log;
    private readonly object _gate = new();
    private MulticastService? _mdns;
    private ServiceDiscovery? _sd;
    private Registration? _registration; // set while the advertiser is meant to be running
    private string _registeredFor = "";  // address set the current profiles were built from
    private Timer? _timer;
    private bool _disposed;

    public bool IsAdvertising { get; private set; }

    /// <summary>
    /// Asked before registering again: that sends mDNS goodbyes, which must not happen under a connected device.
    /// </summary>
    public Func<bool> CanRefresh { get; set; } = () => true;

    // ServiceProfile bakes A/AAAA records in when it is built and a live advertiser answers from its own copy
    // (changing profile.Resources afterwards has no effect), so following address changes means registering again.
    internal Func<IPAddress[]> Addresses { get; set; } = () => MulticastService.GetLinkLocalAddresses().ToArray();
    internal TimeSpan Debounce { get; set; } = TimeSpan.FromSeconds(3);
    internal TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(15);

    public MdnsAdvertiser(AirPadLogger log)
    {
        _log = log;
    }

    /// <param name="publicKey">Receiver's Ed25519 public key (PairingService.ServerPublicKey): the advertised
    /// <c>pk</c> must be the key that actually signs pair-verify, not a copy-pasted constant.</param>
    public Task StartAsync(AirPadSettings settings, byte[] publicKey)
    {
        if (IsAdvertising)
            return Task.CompletedTask;

        var deviceId = (settings.DeviceId ?? "").Trim();
        if (!Regex.IsMatch(deviceId, "^(([0-9a-fA-F]{2}):){5}([0-9a-fA-F]{2})$"))
            throw new ArgumentException("DeviceId must be a MAC address like 11:22:33:44:55:66.", nameof(settings));

        var instance = string.IsNullOrWhiteSpace(settings.ReceiverName)
            ? AirPadSettings.DefaultReceiverName()
            : settings.ReceiverName.Trim();

        lock (_gate)
        {
            _registration = new Registration(instance, deviceId, (ushort)settings.AirPlayPort, (ushort)settings.AirTunesPort,
                Convert.ToHexString(publicKey).ToLowerInvariant());
            Register(_registration);
        }
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        return Task.CompletedTask;
    }

    private void Register(Registration r)
    {
        var addresses = Addresses();
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
        var deviceIdInstance = r.DeviceId.Replace(":", "", StringComparison.Ordinal).ToUpperInvariant();
        var airTunes = new ServiceProfile($"{deviceIdInstance}@{r.Instance}", AirTunesService, r.AirTunesPort, addresses);
        airTunes.AddProperty("ch", "2");
        airTunes.AddProperty("cn", "2,3");
        airTunes.AddProperty("et", "0,3,5");
        airTunes.AddProperty("md", "0,1,2");
        airTunes.AddProperty("sr", "44100");
        airTunes.AddProperty("ss", "16");
        airTunes.AddProperty("da", "true");
        airTunes.AddProperty("sv", "false");
        airTunes.AddProperty("ft", AirPlayFeatures.MdnsTxt);
        airTunes.AddProperty("am", "AppleTV5,3");
        airTunes.AddProperty("pk", r.PublicKeyHex);
        airTunes.AddProperty("sf", "0x4");
        airTunes.AddProperty("tp", "UDP");
        airTunes.AddProperty("vn", "65537");
        airTunes.AddProperty("vs", "220.68");
        airTunes.AddProperty("vv", "2");

        var airPlay = new ServiceProfile(r.Instance, AirPlayService, r.AirPlayPort, addresses);
        airPlay.AddProperty("deviceid", r.DeviceId);
        airPlay.AddProperty("features", AirPlayFeatures.MdnsTxt);
        airPlay.AddProperty("flags", "0x4");
        airPlay.AddProperty("model", "AppleTV5,3");
        airPlay.AddProperty("pk", r.PublicKeyHex);
        airPlay.AddProperty("pi", "aa072a95-0318-4ec3-b042-4992495877d3");
        airPlay.AddProperty("srcvers", "220.68");
        airPlay.AddProperty("vv", "2");

        _sd.Advertise(airTunes);
        _sd.Advertise(airPlay);
        _mdns.Start();

        _registeredFor = Fingerprint(addresses);
        IsAdvertising = true;
        _log.Info("mDNS service registered");
        _log.Info($"Receiver '{r.Instance}' is now visible to iPads on the local network (AirPlay TCP {r.AirPlayPort}, AirTunes TCP {r.AirTunesPort})");
    }

    /// <summary>Registers again if the machine's addresses moved on. Returns true when it did.</summary>
    internal bool Refresh()
    {
        lock (_gate)
        {
            var r = _registration;
            if (r == null || (IsAdvertising && Fingerprint(Addresses()) == _registeredFor))
                return false;

            if (!CanRefresh())
            {
                Arm(RetryDelay); // a device is connected: try again later instead of bouncing the registration under it
                return false;
            }

            _log.Info("Network addresses changed; re-registering the receiver on mDNS");
            Unregister();
            try
            {
                Register(r);
            }
            catch (Exception ex)
            {
                _log.Warn($"mDNS re-registration failed: {ex.Message}");
                Arm(RetryDelay);
                return false;
            }
            return true;
        }
    }

    internal void OnNetworkAddressChanged(object? sender, EventArgs e) => Arm(Debounce);

    private void Arm(TimeSpan delay)
    {
        lock (_gate)
        {
            if (_registration == null)
                return;
            // An adapter coming up raises a burst of events while DHCP settles: every event restarts the countdown.
            _timer?.Dispose();
            _timer = new Timer(_ => Tick(), null, delay, Timeout.InfiniteTimeSpan);
        }
    }

    private void Tick()
    {
        try { Refresh(); }
        catch (Exception ex) { _log.Warn($"mDNS refresh failed: {ex.Message}"); }
    }

    private static string Fingerprint(IEnumerable<IPAddress> addresses) =>
        string.Join(",", addresses.Select(a => a.ToString()).Order(StringComparer.Ordinal));

    private void Unregister()
    {
        if (_mdns == null && _sd == null)
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

    public void Stop()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        lock (_gate)
        {
            _registration = null;
            _timer?.Dispose();
            _timer = null;
            Unregister();
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
