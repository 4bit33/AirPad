using AirPad.Core.Models.Enums;

namespace AirPad.Core.Protocol.Models;

/// <summary>
/// AirPlay session state negotiated via RTSP/HTTP handshake
/// (pair-verify → fp-setup → SETUP → RECORD).
/// </summary>
public sealed class Session
{
    public Session(string sessionId)
    {
        SessionId = sessionId;
    }

    public string SessionId { get; }

    // Pair-verify (ECDH Curve25519 + Ed25519)
    public byte[]? EcdhOurs { get; set; }
    public byte[]? EcdhTheirs { get; set; }
    public byte[]? EdTheirs { get; set; }
    public byte[]? EcdhShared { get; set; }
    public bool? PairVerified { get; set; }

    // FairPlay
    public byte[]? KeyMsg { get; set; }

    // Mirror session keys
    public byte[]? AesKey { get; set; }
    public byte[]? AesIv { get; set; }
    public string? StreamConnectionId { get; set; }
    public AudioFormat AudioFormat { get; set; } = AudioFormat.Unknown;

    // Media state
    public byte[]? DecryptedAesKey { get; set; }
    public byte[]? SpsPps { get; set; }
    public long? Pts { get; set; }
    public int? WidthSource { get; set; }
    public int? HeightSource { get; set; }
    public bool? MirroringSession { get; set; }
    public int TimingPort { get; set; }

    public bool PairCompleted => EcdhShared != null && (PairVerified ?? false);
    public bool FairPlaySetupCompleted => KeyMsg != null && EcdhShared != null && (PairVerified ?? false);
    public bool FairPlayReady => KeyMsg != null && EcdhShared != null && AesKey != null && AesIv != null;
    public bool MirroringSessionReady => StreamConnectionId != null && (MirroringSession ?? false);
    public bool AudioSessionReady => AudioFormat != AudioFormat.Unknown;
}
