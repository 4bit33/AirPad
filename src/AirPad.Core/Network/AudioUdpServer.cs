using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using AirPad.Core.Logging;
using AirPad.Core.Media;
using AirPad.Core.Models.Enums;
using AirPad.Core.Protocol.Models;
using AirPad.Core.Utils;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace AirPad.Core.Network;

/// <summary>
/// Receives RAOP audio over UDP (control + data sockets), decrypts RTP
/// payloads (AES-CBC), reorders by sequence number with a small window,
/// decodes to PCM, and emits ordered packets for playback.
/// </summary>
public sealed class AudioUdpServer : IDisposable
{
    private const int MaxDatagram = 50000;
    private const int ReorderWindow = 256;

    private readonly AirPadLogger _log;
    private readonly int _controlPort;
    private readonly int _dataPort;

    private Socket? _controlSocket;
    private Socket? _dataSocket;
    private CancellationTokenSource? _cts;
    private Task? _controlTask;
    private Task? _dataTask;
    private Task? _playTask;
    private bool _disposed;

    private readonly object _sessionGate = new();
    private Session? _session;
    private AudioDecoder? _decoder;
    private IBufferedCipher? _cipher; // derived once per attached session; used by the playback thread only
    private bool _decoderReady;

    private readonly object _windowGate = new();
    private readonly SortedDictionary<ushort, PendingAudio> _window = new(new SeqComparer());
    private ushort _expected;
    private bool _haveExpected;
    private DateTime _lastPlayAdvance = DateTime.UtcNow;

    private EndPoint? _clientControlEndpoint;
    private ushort _resendSeq;
    private ulong _syncNtp;
    private uint _syncRtp;

    // Observability counters (Interlocked).
    private long _rtpReceived;
    private long _decodedOk;
    private long _decodedEmpty;
    private long _played;
    private long _stored;
    private long _firstDecodedLogged;
    private DateTime _lastSummaryUtc = DateTime.UtcNow;
    private DateTime _lastResendUtc = DateTime.MinValue;

    public event Action<PcmPacketShim>? PcmReady;

    public bool IsRunning => _cts != null && !_cts.IsCancellationRequested;

    public AudioUdpServer(AirPadLogger log, int controlPort, int dataPort)
    {
        _log = log;
        _controlPort = controlPort;
        _dataPort = dataPort;
    }

    public void AttachSession(Session session, AudioDecoder decoder)
    {
        lock (_sessionGate)
        {
            _session = session;
            _decoder = decoder;
            _cipher = null;
            _decoderReady = decoder.IsOpen;
        }
        Flush(-1);
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        if (IsRunning)
            return Task.CompletedTask;

        _controlSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
        _dataSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
        _controlSocket.Bind(new IPEndPoint(IPAddress.Any, _controlPort));
        _dataSocket.Bind(new IPEndPoint(IPAddress.Any, _dataPort));

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _controlTask = Task.Run(() => ControlLoopAsync(_cts.Token), _cts.Token);
        _dataTask = Task.Run(() => DataLoopAsync(_cts.Token), _cts.Token);
        _playTask = Task.Run(() => PlaybackLoopAsync(_cts.Token), _cts.Token);

        _log.Info($"Audio receiver listening (UDP {_controlPort} control / {_dataPort} data)");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        try { _cts?.Cancel(); } catch { }
        // ReceiveFrom ignores the token: closing the sockets is what unblocks the receive loops.
        try { _controlSocket?.Close(); } catch { }
        try { _dataSocket?.Close(); } catch { }
        foreach (var task in new[] { _controlTask, _dataTask, _playTask })
        {
            if (task != null)
            {
                try { await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
            }
        }
        _controlSocket = null;
        _dataSocket = null;
    }

    public void Flush(int nextSeq)
    {
        lock (_windowGate)
        {
            _window.Clear();
            if (nextSeq is >= 0 and <= 0xFFFF)
            {
                _expected = (ushort)nextSeq;
                _haveExpected = true;
            }
            else
            {
                _haveExpected = false;
            }
        }
        _log.Info($"Audio flush (next_seq={nextSeq})");
    }

    private async Task ControlLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[MaxDatagram];
        while (!ct.IsCancellationRequested && _controlSocket != null)
        {
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            int received;
            try
            {
                received = _controlSocket.ReceiveFrom(buffer, ref remote);
            }
            catch (SocketException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { continue; }

            if (received < 8)
                continue;

            _clientControlEndpoint = remote;
            int type = buffer[1] & ~0x80;

            if (type == 0x54 && received >= 20)
            {
                // Timing sync: NTP time + RTP timestamp of the next packet.
                _syncNtp = ((ulong)(uint)ReadI32(buffer, 8) << 32) | (uint)ReadI32(buffer, 12);
                _syncRtp = (uint)((buffer[16] << 24) | (buffer[17] << 16) | (buffer[18] << 8) | buffer[19]);
            }
            else if (type == 0x56 && received > 16)
            {
                // Retransmit response: a 4-byte header, then the original RTP packet.
                var payload = new byte[received - 4];
                Buffer.BlockCopy(buffer, 4, payload, 0, payload.Length);
                QueueDatagram(payload, payload.Length);
            }
            // 0x55 resend requests from the client are answered by the data loop.
        }
        await Task.CompletedTask;
    }

    private async Task DataLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[MaxDatagram];
        while (!ct.IsCancellationRequested && _dataSocket != null)
        {
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            int received;
            try
            {
                received = _dataSocket.ReceiveFrom(buffer, ref remote);
            }
            catch (SocketException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { continue; }

            if (received < 12)
                continue;

            var payload = new byte[received];
            Buffer.BlockCopy(buffer, 0, payload, 0, received);
            QueueDatagram(payload, received);
        }
        await Task.CompletedTask;
    }

    private void QueueDatagram(byte[] datagram, int length)
    {
        lock (_sessionGate)
        {
            if (_session?.DecryptedAesKey == null || _session.EcdhShared == null || _session.AesIv == null)
                return;
            if (_decoder == null || !_decoderReady)
                return;
        }

        if (length < 12 || length > MaxDatagram)
            return;

        ushort seq = (ushort)((datagram[2] << 8) | datagram[3]);

        if (Interlocked.Increment(ref _rtpReceived) == 1)
            _log.Info("First audio RTP packets arriving");
        MaybeLogSummary();

        // The 4-byte payload 00 68 34 00 is the silence frame the iPad sends while nothing plays. It carries no
        // audio but it owns a sequence number: dropping it left a hole the window could never cross.
        bool silence = length == 16 && datagram[12] == 0x00 && datagram[13] == 0x68 && datagram[14] == 0x34 && datagram[15] == 0x00;

        uint timestamp = (uint)((datagram[4] << 24) | (datagram[5] << 16) | (datagram[6] << 8) | datagram[7]);

        // Only buffer here: decrypt + decode happen after reordering, on the playback thread.
        lock (_windowGate)
        {
            if (!_haveExpected)
            {
                _expected = seq;
                _haveExpected = true;
            }

            int distance = SeqDistance(_expected, seq);
            if (distance >= ReorderWindow)
            {
                // Far ahead of everything buffered (outage, sender restart): resync here. Rejecting would repeat for
                // every later packet and leave the stream dead until the next FLUSH/SETUP.
                _window.Clear();
                _expected = seq;
                distance = 0;
            }
            else if (distance < 0)
                return; // late duplicate of a frame that was already played

            if (!_window.ContainsKey(seq))
            {
                _window[seq] = new PendingAudio(seq, timestamp, silence ? null : datagram);
                if (!silence)
                    Interlocked.Increment(ref _stored);
            }

            while (_window.Count > ReorderWindow)
                _window.Remove(_window.Keys.First());

            // Ask for resends when a gap opens (best effort, throttled:
            // an unthrottled storm makes the client flood us with duplicates).
            if (distance > 4 && (DateTime.UtcNow - _lastResendUtc).TotalMilliseconds > 200)
            {
                _lastResendUtc = DateTime.UtcNow;
                RequestResend(_expected, (ushort)(distance > 0 ? distance : 0));
            }
        }
    }

    private async Task PlaybackLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            PendingAudio? next = null;
            lock (_windowGate)
            {
                if (_haveExpected && _window.TryGetValue(_expected, out var pending))
                {
                    next = pending;
                    _window.Remove(_expected);
                    _expected++;
                    _lastPlayAdvance = DateTime.UtcNow;
                }
                else if (_haveExpected && (DateTime.UtcNow - _lastPlayAdvance).TotalMilliseconds > 250 && _window.Count > 0)
                {
                    // Stall: jump to the oldest buffered packet instead of gapless waiting.
                    var first = _window.Keys.First();
                    _expected = first;
                }
            }

            if (next != null)
            {
                foreach (var pcm in DecodeBuffered(next))
                {
                    Interlocked.Increment(ref _played);
                    PcmReady?.Invoke(new PcmPacketShim(pcm.Data, pcm.Timestamp));
                }
            }
            else
            {
                try { await Task.Delay(5, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>
    /// Decrypts (AES-CBC, 16-byte blocks; tail copied raw) and decodes one buffered datagram.
    /// Runs on the playback thread only, in sequence order: AAC-ELD is stateful, so decoding in arrival
    /// order (or from several threads) corrupts the stream.
    /// </summary>
    private List<AirPad.Core.Audio.PcmPacket> DecodeBuffered(PendingAudio audio)
    {
        var datagram = audio.Datagram;
        if (datagram == null)
            return []; // silence frame: only holds its place in the sequence

        AudioDecoder? decoder;
        IBufferedCipher? cipher = null;
        lock (_sessionGate)
        {
            decoder = _decoder;
            if (_session?.DecryptedAesKey != null && _session.EcdhShared != null && _session.AesIv != null)
                cipher = _cipher ??= CreateAudioCipher(_session.DecryptedAesKey, _session.EcdhShared, _session.AesIv);
        }
        if (decoder == null || cipher == null)
            return [];

        try
        {
            int payloadSize = datagram.Length - 12;
            int blocks = payloadSize / 16 * 16;
            cipher.Reset(); // every RTP packet restarts CBC at the session IV
            if (blocks > 0)
                cipher.ProcessBytes(datagram, 12, blocks, datagram, 12);
            var raw = new byte[payloadSize];
            Buffer.BlockCopy(datagram, 12, raw, 0, payloadSize);

            var decoded = decoder.Decode(raw, audio.Timestamp);
            if (decoded.Count == 0)
            {
                Interlocked.Increment(ref _decodedEmpty);
                return decoded;
            }
            Interlocked.Add(ref _decodedOk, decoded.Count);
            if (Interlocked.Increment(ref _firstDecodedLogged) == 1)
                _log.Info("Audio decoding works (compressed → PCM 44.1 kHz stereo)");
            return decoded;
        }
        catch
        {
            return [];
        }
    }

    private void MaybeLogSummary()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastSummaryUtc).TotalSeconds < 10)
            return;
        _lastSummaryUtc = now;
        int queued;
        lock (_windowGate) queued = _window.Count;
        _log.Debug($"Audio stats: RTP={Interlocked.Read(ref _rtpReceived)} decoded={Interlocked.Read(ref _decodedOk)} " +
                   $"undecodable={Interlocked.Read(ref _decodedEmpty)} stored={Interlocked.Read(ref _stored)} played={Interlocked.Read(ref _played)} queued={queued}");
    }

    private void RequestResend(ushort firstSeq, ushort count)
    {
        var socket = _controlSocket;
        var endpoint = _clientControlEndpoint;
        if (socket == null || endpoint == null || count == 0)
            return;
        try
        {
            ushort ours = _resendSeq++;
            var packet = new byte[8];
            packet[0] = 0x80;
            packet[1] = (byte)(0x55 | 0x80);
            packet[2] = (byte)(ours >> 8);
            packet[3] = (byte)ours;
            packet[4] = (byte)(firstSeq >> 8);
            packet[5] = (byte)firstSeq;
            packet[6] = (byte)(count >> 8);
            packet[7] = (byte)count;
            socket.SendTo(packet, endpoint);
        }
        catch { }
    }

    internal static IBufferedCipher CreateAudioCipher(byte[] decryptedAesKey, byte[] ecdhShared, byte[] aesIv)
    {
        using var sha = System.Security.Cryptography.SHA512.Create();
        byte[] hash = sha.ComputeHash(decryptedAesKey.Concat(ecdhShared).ToArray());
        byte[] key = Utilities.CopyOfRange(hash, 0, 16);

        var cipher = CipherUtilities.GetCipher("AES/CBC/NoPadding");
        cipher.Init(false, new ParametersWithIV(ParameterUtilities.CreateKeyParameter("AES", key), aesIv, 0, aesIv.Length));
        return cipher;
    }

    private static int ReadI32(byte[] buffer, int offset) =>
        (buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3];

    /// <summary>Signed forward distance from expected to seq in [-32768, 32767].</summary>
    private static int SeqDistance(ushort from, ushort to)
    {
        int d = (ushort)(to - from);
        return d < 0x8000 ? d : d - 0x10000;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try { _cts?.Cancel(); } catch { }
        try { _controlSocket?.Close(); } catch { }
        try { _dataSocket?.Close(); } catch { }
        try { _cts?.Dispose(); } catch { }
    }

    private sealed record PendingAudio(ushort Seq, uint Timestamp, byte[]? Datagram);

    private sealed class SeqComparer : IComparer<ushort>
    {
        public int Compare(ushort x, ushort y) => x.CompareTo(y);
    }

    public sealed record PcmPacketShim(byte[] Data, uint Timestamp);
}
