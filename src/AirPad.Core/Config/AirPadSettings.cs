namespace AirPad.Core.Config;

/// <summary>
/// Persisted user settings (JSON in %APPDATA%/AirPad/settings.json).
/// No passwords or secret keys are ever stored here.
/// </summary>
public sealed class AirPadSettings
{
    public string ReceiverName { get; set; } = DefaultReceiverName();
    public int AirPlayPort { get; set; } = 7000;
    public int AirTunesPort { get; set; } = 5000;
    public int AudioControlPort { get; set; } = 7002;
    public int AudioDataPort { get; set; } = 7003;
    public string DeviceId { get; set; } = "11:22:33:44:55:66";

    public bool AutoStartWithWindows { get; set; } = false;
    public bool FullscreenOnConnect { get; set; } = false;
    public bool HardwareAcceleration { get; set; } = true;
    public int MaxFps { get; set; } = 60;
    public string VideoScaling { get; set; } = "Fit";
    public bool AudioEnabled { get; set; } = true;
    public string AuthMode { get; set; } = "Open";
    public string LogLevel { get; set; } = "Info";
    public double Volume { get; set; } = 1.0;
    public bool Muted { get; set; } = false;

    public static string DefaultReceiverName()
    {
        try { return Environment.MachineName + "-AirPad"; }
        catch { return "AirPad"; }
    }
}
