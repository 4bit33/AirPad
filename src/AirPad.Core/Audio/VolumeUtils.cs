namespace AirPad.Core.Audio;

/// <summary>
/// AirPlay volume is expressed in decibels (typically -30..0 dB in 1.875 dB
/// steps, down to -144 dB = mute), where 0 dB means full volume.
/// WASAPI and the UI slider use linear 0..1, hence these conversions.
/// Treating dB as linear (the original bug) clamps every negative value to 0
/// and mutes the output permanently.
/// </summary>
public static class VolumeUtils
{
    public const double MinDb = -144.0;

    /// <summary>Slider floor in dB (matches the iPad's own -30..0 dB range).</summary>
    public const double SliderMinDb = -30.0;

    public static double DbToLinear(double db)
    {
        if (double.IsNaN(db) || db <= MinDb)
            return 0.0;
        if (db >= 0)
            return 1.0;
        return Math.Clamp(Math.Pow(10.0, db / 20.0), 0.0, 1.0);
    }

    public static double LinearToDb(double linear)
    {
        if (double.IsNaN(linear) || linear <= 0)
            return MinDb;
        if (linear >= 1)
            return 0.0;
        return Math.Clamp(20.0 * Math.Log10(linear), MinDb, 0.0);
    }
}
