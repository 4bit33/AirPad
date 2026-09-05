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
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var settings = JsonSerializer.Deserialize<AirPadSettings>(json, JsonOptions);
                if (settings != null)
                    return Sanitize(settings);
            }
        }
        catch
        {
            // Corrupt settings file: fall back to defaults.
        }
        return new AirPadSettings();
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
