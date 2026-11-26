using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using AirPad.Core.Connection;
using AirPad.Core.Discovery;
using AirPad.Helpers;

namespace AirPad;

public partial class SettingsWindow : Window
{
    private readonly ConnectionManager _manager;
    private readonly SettingsEditor _editor;

    public SettingsWindow(ConnectionManager manager)
    {
        InitializeComponent();
        _manager = manager;
        _editor = new SettingsEditor(manager);
        DataContext = _editor;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!_editor.Apply())
            return;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnLink(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}

internal sealed class SettingsEditor : INotifyPropertyChanged
{
    private readonly ConnectionManager _manager;

    public event PropertyChangedEventHandler? PropertyChanged;

    public SettingsEditor(ConnectionManager manager)
    {
        _manager = manager;
        var s = manager.Settings;
        ReceiverName = s.ReceiverName;
        DeviceId = s.DeviceId;
        AirPlayPort = s.AirPlayPort.ToString();
        AirTunesPort = s.AirTunesPort.ToString();
        AudioControlPort = s.AudioControlPort.ToString();
        AudioDataPort = s.AudioDataPort.ToString();
        AutoStart = AutostartHelper.IsEnabled();
        FullscreenOnConnect = s.FullscreenOnConnect;
        HardwareAcceleration = s.HardwareAcceleration;
        AudioEnabled = s.AudioEnabled;
        MaxFps = s.MaxFps;
        VideoScaling = s.VideoScaling;
        AuthMode = s.AuthMode;
        LogLevel = s.LogLevel;

        FixFirewallCommand = new AsyncRelayCommand(FixFirewallAsync);
    }

    public string ReceiverName { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string AirPlayPort { get; set; } = "7000";
    public string AirTunesPort { get; set; } = "5000";
    public string AudioControlPort { get; set; } = "7002";
    public string AudioDataPort { get; set; } = "7003";
    public bool AutoStart { get; set; }
    public bool FullscreenOnConnect { get; set; }
    public bool HardwareAcceleration { get; set; }
    public bool AudioEnabled { get; set; }
    public int MaxFps { get; set; } = 60;
    public string VideoScaling { get; set; } = "Fit";
    public string AuthMode { get; set; } = "Open";
    public string LogLevel { get; set; } = "Info";

    public ICommand FixFirewallCommand { get; }

    private async Task FixFirewallAsync()
    {
        var s = _manager.Settings;
        await Task.Run(() =>
        {
            if (FirewallHelper.TryEnsureRules(_manager.Log, s.AirPlayPort, s.AirTunesPort, s.AudioControlPort, s.AudioDataPort, out var message))
                MessageBox.Show(message, "AirPad", MessageBoxButton.OK, MessageBoxImage.Information);
            else
                MessageBox.Show(message + "\n\nTip: restart AirPad as administrator and retry.",
                    "AirPad", MessageBoxButton.OK, MessageBoxImage.Warning);
        });
    }

    public bool Apply()
    {
        if (string.IsNullOrWhiteSpace(ReceiverName))
        {
            MessageBox.Show("Receiver name cannot be empty.", "AirPad", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (!System.Text.RegularExpressions.Regex.IsMatch(DeviceId.Trim(), "^(([0-9a-fA-F]{2}):){5}([0-9a-fA-F]{2})$"))
        {
            MessageBox.Show("Device ID must be a MAC address like 11:22:33:44:55:66.", "AirPad", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (!int.TryParse(AirPlayPort, out var ap) || !int.TryParse(AirTunesPort, out var at) ||
            !int.TryParse(AudioControlPort, out var ac) || !int.TryParse(AudioDataPort, out var ad))
        {
            MessageBox.Show("Ports must be numbers between 1024 and 65535.", "AirPad", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        bool portsChanged = ap != _manager.Settings.AirPlayPort || at != _manager.Settings.AirTunesPort ||
                            ac != _manager.Settings.AudioControlPort || ad != _manager.Settings.AudioDataPort;
        bool wasRunning = _manager.IsRunning;

        var s = _manager.Settings;
        s.ReceiverName = ReceiverName.Trim();
        s.DeviceId = DeviceId.Trim();
        s.AirPlayPort = Math.Clamp(ap, 1024, 65535);
        s.AirTunesPort = Math.Clamp(at, 1024, 65535);
        s.AudioControlPort = Math.Clamp(ac, 1024, 65535);
        s.AudioDataPort = Math.Clamp(ad, 1024, 65535);
        s.FullscreenOnConnect = FullscreenOnConnect;
        s.HardwareAcceleration = HardwareAcceleration;
        s.AudioEnabled = AudioEnabled;
        s.MaxFps = Math.Clamp(MaxFps, 15, 120);
        s.VideoScaling = VideoScaling;
        s.AuthMode = AuthMode;
        s.LogLevel = LogLevel;
        _manager.SaveSettings();
        AutostartHelper.SetEnabled(AutoStart);

        if (portsChanged && wasRunning)
        {
            MessageBox.Show("Ports changed. The receiver will restart to apply them.", "AirPad",
                MessageBoxButton.OK, MessageBoxImage.Information);
            _ = Task.Run(async () =>
            {
                try
                {
                    await _manager.StopAsync();
                    await _manager.StartAsync();
                }
                catch { }
            });
        }
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
