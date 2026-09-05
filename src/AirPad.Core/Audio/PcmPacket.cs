using AirPad.Core.Models.Enums;

namespace AirPad.Core.Audio;

/// <summary>
/// Decoded PCM audio chunk (stereo 16-bit, 44100 Hz) with its RTP timestamp.
/// </summary>
public sealed class PcmPacket
{
    public required byte[] Data { get; init; }
    public AudioFormat Format { get; init; } = AudioFormat.Unknown;
    public uint Timestamp { get; init; }
    public DateTime ReceivedUtc { get; init; } = DateTime.UtcNow;
}
