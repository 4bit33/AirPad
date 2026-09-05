using System.Net.Sockets;
using System.Text;
using AirPad.Core.Logging;
using AirPad.Core.Protocol.Models;
using AirPad.Core.Utils;
using AirPad.Core.Video;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace AirPad.Core.Network;

/// <summary>
/// Consumes the raw AirPlay mirror TCP stream (128-byte frame headers +
/// AES-CTR-encrypted H.264 payloads), decrypts it, converts AVCC NALUs to
/// Annex-B, and raises <see cref="H264Packet"/> events on the fast path:
/// Network → Receive → Decrypt → Gate, with no extra buffering.
/// </summary>
public sealed class MirrorStreamHandler
{
    private const string StreamKeyPrefix = "AirPlayStreamKey";
    private const string StreamIvPrefix = "AirPlayStreamIV";

    private readonly AirPadLogger _log;
    private readonly global::AirPad.Core.OmgHax _omgHax = new();

    public event Action<H264Packet>? H264Received;
    public event Action<int, int>? ResolutionChanged;

    public MirrorStreamHandler(AirPadLogger log)
    {
        _log = log;
    }

    public async Task RunAsync(TcpClient client, Session session, byte[] prefix, CancellationToken ct)
    {
        // Resolve the FairPlay-bound AES key once per stream.
        if (session.DecryptedAesKey == null)
        {
            if (session.KeyMsg == null || session.AesKey == null)
            {
                _log.Error("Mirror stream rejected: missing FairPlay key material.");
                return;
            }
            session.DecryptedAesKey = new FairPlayKey().Decrypt(session.KeyMsg, session.AesKey);
        }

        var cipher = CreateStreamCipher(session.DecryptedAesKey, session.EcdhShared ?? Array.Empty<byte>(), session.StreamConnectionId ?? "0");
        _log.Info("Video stream started");
        _log.Info("Decoder initialized (H.264 Annex-B gate)");

        using (client)
        using (var stream = client.GetStream())
        {
            var stash = new List<byte>(prefix);
            try
            {
                while (!ct.IsCancellationRequested && client.Connected)
                {
                    // Frame header is exactly 128 bytes. Control requests
                    // (e.g. POST /feedback) may be interleaved: skip them.
                    var header = await ReadExactAsync(stream, stash, 128, ct).ConfigureAwait(false);
                    if (header == null)
                        break;

                    if (IsAsciiVerb(header))
                    {
                        await SkipControlRequestAsync(stream, stash, header, ct).ConfigureAwait(false);
                        continue;
                    }

                    var mirrorHeader = new AirPad.Core.Models.MirroringHeader(header);

                    if (mirrorHeader.PayloadSize <= 0 || mirrorHeader.PayloadSize > 8 * 1024 * 1024)
                    {
                        _log.Warn($"Mirror: implausible payload size {mirrorHeader.PayloadSize}; dropping connection.");
                        break;
                    }

                    var payload = await ReadExactAsync(stream, stash, mirrorHeader.PayloadSize, ct).ConfigureAwait(false);
                    if (payload == null)
                        break;

                    var receivedUtc = DateTime.UtcNow;

                    if (mirrorHeader.PayloadType == 1)
                    {
                        var spsPps = H264Gate.ParseAvccBox(payload);
                        if (spsPps != null)
                        {
                            session.SpsPps = spsPps;
                            var res = H264Gate.ParseSpsResolution(spsPps);
                            if (res != null)
                            {
                                session.WidthSource = res.Value.Width;
                                session.HeightSource = res.Value.Height;
                                ResolutionChanged?.Invoke(res.Value.Width, res.Value.Height);
                                _log.Info($"Video SPS: {res.Value.Width}x{res.Value.Height}");
                            }
                        }
                        continue;
                    }

                    if (mirrorHeader.PayloadType != 0)
                        continue; // unknown payload type: skip

                    if (!session.Pts.HasValue)
                        session.Pts = mirrorHeader.PayloadPts;

                    var decrypted = DecryptPayload(cipher, payload);
                    var packet = H264Gate.BuildDecoderPacket(decrypted, session.SpsPps, out bool isIdr);
                    if (packet == null)
                    {
                        // Wrong stream key (or corrupt data) shows up exactly
                        // like this: headers parse, payloads never gate.
                        // Never spam: one line per 200 consecutive drops.
                        if (++_gateDrops % 200 == 1)
                            _log.Debug($"Mirror: dropping invalid video payloads (x{_gateDrops}) — wrong stream key?");
                        continue;
                    }
                    _gateDrops = 0;

                    H264Received?.Invoke(new H264Packet
                    {
                        Data = packet,
                        WidthSource = session.WidthSource ?? 0,
                        HeightSource = session.HeightSource ?? 0,
                        Pts = mirrorHeader.PayloadPts,
                        IsIdr = isIdr,
                        ReceivedUtc = receivedUtc,
                    });
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (Exception ex)
            {
                _log.Error("Mirror stream error", ex);
            }
        }

        _log.Info("Video stream stopped");
    }

    private static bool IsAsciiVerb(byte[] header)
    {
        if (header.Length < 4)
            return false;
        return (header[0] == 'P' && header[1] == 'O' && header[2] == 'S' && header[3] == 'T') ||
               (header[0] == 'G' && header[1] == 'E' && header[2] == 'T' && header[3] == ' ');
    }

    private static async Task SkipControlRequestAsync(NetworkStream stream, List<byte> stash, byte[] first128, CancellationToken ct)
    {
        // Read until end of headers, parse Content-Length, discard the body.
        var buffer = new List<byte>(first128);
        while (IndexOfHeaderEnd(buffer) < 0 && buffer.Count < 65536)
        {
            var chunk = new byte[4096];
            int read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), ct).ConfigureAwait(false);
            if (read <= 0)
                return;
            buffer.AddRange(chunk.Take(read));
        }

        int headerEnd = IndexOfHeaderEnd(buffer);
        int contentLength = 0;
        if (headerEnd > 0)
        {
            var headerText = Encoding.ASCII.GetString(buffer.Take(headerEnd).ToArray());
            foreach (var line in headerText.Split(["\r\n"], StringSplitOptions.RemoveEmptyEntries).Skip(1))
            {
                int colon = line.IndexOf(':');
                if (colon > 0 && line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(line[(colon + 1)..].Trim(), out var len))
                {
                    contentLength = Math.Clamp(len, 0, 16 * 1024 * 1024);
                    break;
                }
            }
            int totalNeeded = headerEnd + contentLength;
            while (buffer.Count < totalNeeded)
            {
                var chunk = new byte[4096];
                int read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), ct).ConfigureAwait(false);
                if (read <= 0)
                    return;
                buffer.AddRange(chunk.Take(read));
            }
            // Keep any bytes after the request for the next frame.
            stash.Clear();
            if (buffer.Count > totalNeeded)
                stash.AddRange(buffer.Skip(totalNeeded));
        }
    }

    private static int IndexOfHeaderEnd(List<byte> buffer)
    {
        for (int i = 0; i + 3 < buffer.Count; i++)
        {
            if (buffer[i] == (byte)'\r' && buffer[i + 1] == (byte)'\n' && buffer[i + 2] == (byte)'\r' && buffer[i + 3] == (byte)'\n')
                return i + 4;
        }
        return -1;
    }

    private static async Task<byte[]?> ReadExactAsync(NetworkStream stream, List<byte> stash, int count, CancellationToken ct)
    {
        var result = new byte[count];
        int filled = 0;

        if (stash.Count > 0)
        {
            int take = Math.Min(stash.Count, count);
            stash.CopyTo(0, result, 0, take);
            stash.RemoveRange(0, take);
            filled = take;
        }

        while (filled < count)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(result.AsMemory(filled, count - filled), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return null; }
            catch (IOException) { return null; }
            if (read <= 0)
                return null;
            filled += read;
        }
        return result;
    }

    private byte[] _carry = new byte[16];
    private int _carryCount;
    private long _gateDrops;

    private byte[] DecryptPayload(IBufferedCipher cipher, byte[] payload)
    {
        // Streaming AES-CTR with 16-byte carry-over between payloads
        // (matches UxPlay/raop_rtp_mirror behaviour).
        if (_carryCount > 0)
        {
            for (int i = 0; i < _carryCount; i++)
                payload[i] = (byte)(payload[i] ^ _carry[(16 - _carryCount) + i]);
        }

        int blocks = ((payload.Length - _carryCount) / 16) * 16;
        if (blocks > 0)
            cipher.ProcessBytes(payload, _carryCount, blocks, payload, _carryCount);

        int rest = (payload.Length - _carryCount) % 16;
        _carryCount = 0;
        if (rest > 0)
        {
            Array.Clear(_carry, 0, _carry.Length);
            Buffer.BlockCopy(payload, payload.Length - rest, _carry, 0, rest);
            cipher.ProcessBytes(_carry, 0, 16, _carry, 0);
            Buffer.BlockCopy(_carry, 0, payload, payload.Length - rest, rest);
            _carryCount = 16 - rest;
        }

        return payload;
    }

    internal static IBufferedCipher CreateStreamCipher(byte[] decryptedAesKey, byte[] ecdhShared, string streamConnectionId)
    {
        using var sha = System.Security.Cryptography.SHA512.Create();
        byte[] combined = decryptedAesKey.Concat(ecdhShared).ToArray();
        byte[] hashed = sha.ComputeHash(combined);
        byte[] half = Utilities.CopyOfRange(hashed, 0, 16);

        byte[] skey = Encoding.UTF8.GetBytes(StreamKeyPrefix + streamConnectionId);
        byte[] siv = Encoding.UTF8.GetBytes(StreamIvPrefix + streamConnectionId);
        byte[] keyHash = sha.ComputeHash(skey.Concat(half).ToArray());
        byte[] ivHash = sha.ComputeHash(siv.Concat(half).ToArray());

        var cipher = CipherUtilities.GetCipher("AES/CTR/NoPadding");
        var keyParam = ParameterUtilities.CreateKeyParameter("AES", Utilities.CopyOfRange(keyHash, 0, 16));
        cipher.Init(false, new ParametersWithIV(keyParam, Utilities.CopyOfRange(ivHash, 0, 16)));
        return cipher;
    }

    private sealed class FairPlayKey
    {
        private readonly global::AirPad.Core.OmgHax _hax = new();
        public byte[] Decrypt(byte[] keyMsg, byte[] ekey)
        {
            var output = new byte[16];
            _hax.DecryptAesKey(keyMsg, ekey, output);
            return output;
        }
    }
}
