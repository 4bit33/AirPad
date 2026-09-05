namespace AirPad.Core.Video;

/// <summary>
/// One decrypted, Annex-B H.264 access unit ready for decoding,
/// with capture metadata for stats and orientation handling.
/// </summary>
public sealed class H264Packet
{
    public required byte[] Data { get; init; }
    public int WidthSource { get; init; }
    public int HeightSource { get; init; }
    public long Pts { get; init; }
    public bool IsIdr { get; init; }
    public DateTime ReceivedUtc { get; init; } = DateTime.UtcNow;
}
