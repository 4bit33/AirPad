namespace AirPad.Core.Protocol;

/// <summary>
/// AirPlay capability bits advertised over mDNS (<c>features</c> / <c>ft</c>) and in GET /info.
/// <para/>
/// Bits 0 (Video), 4 (VideoHTTPLiveStreams), 33 (SupportsAirPlayVideoPlayQueue) and 34 (SupportsAirPlayFromCloud)
/// tell iOS that the receiver can play a URL. AirPad cannot (no reverse channel, no /play, no HLS player), so when they
/// were set, starting a YouTube video made iOS hand playback off (GET /server-info, POST /reverse, POST /play), the
/// mirror session was torn down and the iPad hung. Without them iOS keeps mirroring the app's video, which is what UxPlay
/// does unless started with -hls. Bit numbers: openairplay.github.io/airplay-spec/features.html.
/// </summary>
public static class AirPlayFeatures
{
    private const ulong Video = 1UL << 0 | 1UL << 4 | 1UL << 33 | 1UL << 34;

    /// <summary>Value for the mDNS TXT records.</summary>
    public const ulong Mdns = 0x1E5A7FFFF7 & ~Video;

    /// <summary>Value for GET /info.</summary>
    public const ulong Info = 61379444727 & ~Video;

    /// <summary>The mDNS TXT form: <c>0x&lt;low 32 bits&gt;,0x&lt;high 32 bits&gt;</c>.</summary>
    public static string MdnsTxt => $"0x{Mdns & 0xFFFFFFFF:X},0x{Mdns >> 32:X}";
}
