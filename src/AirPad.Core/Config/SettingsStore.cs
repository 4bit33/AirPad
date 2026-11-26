using System.Text.Json;

namespace AirPad.Core.Config;

/// <summary>
/// Loads/saves <see cref="AirPadSettings"/> as JSON under %APPDATA%/AirPad.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public string FilePath { get; }

    public SettingsStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AirPad", "settings.json");
    }

    public AirPadSettings Load()
    {
        AirPadSettings? settings = null;
        bool corrupt = false;
        try
        {
            if (File.Exists(FilePath))
                settings = JsonSerializer.Deserialize<AirPadSettings>(File.ReadAllText(FilePath), JsonOptions);
        }
        catch
        {
            corrupt = true; // fall back to defaults, but leave the file alone so it can be fixed by hand
        }
        settings ??= new AirPadSettings();

        // Every install used to share one device ID, so two AirPad PCs on a network looked like one receiver to the
        // iPad. Give each install its own, once, and keep it: a new ID on every launch would look like a new device.
        if (settings.DeviceId == AirPadSettings.LegacyDeviceId)
        {
            settings.DeviceId = AirPadSettings.NewDeviceId();
            if (!corrupt)
            {
                try { Save(settings); } catch { /* read-only profile: the ID just isn't persisted */ }
            }
        }
        return Sanitize(settings);
    }

    public void Save(AirPadSettings settings)
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(Sanitize(settings), JsonOptions);
        File.WriteAllText(FilePath, json);
    }

    private static AirPadSettings Sanitize(AirPadSettings s)
    {
        if (string.IsNullOrWhiteSpace(s.ReceiverName))
            s.ReceiverName = AirPadSettings.DefaultReceiverName();
        s.AirPlayPort = Math.Clamp(s.AirPlayPort, 1024, 65535);
        s.AirTunesPort = Math.Clamp(s.AirTunesPort, 1024, 65535);
        s.AudioControlPort = Math.Clamp(s.AudioControlPort, 1024, 65535);
        s.AudioDataPort = Math.Clamp(s.AudioDataPort, 1024, 65535);
        s.MaxFps = Math.Clamp(s.MaxFps, 15, 120);
        s.Volume = Math.Clamp(s.Volume, 0.0, 1.0);
        if (s.VideoScaling is not ("Fit" or "Fill" or "Stretch"))
            s.VideoScaling = "Fit";
        if (s.AuthMode is not ("Open" or "Pin"))
            s.AuthMode = "Open";
        return s;
    }
}
