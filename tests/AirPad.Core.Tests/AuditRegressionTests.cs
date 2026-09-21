using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Serialization.Plists;
using System.Security.Cryptography;
using System.Text;
using AirPad.Core.Audio;
using AirPad.Core.Auth;
using AirPad.Core.Logging;
using AirPad.Core.Media;
using AirPad.Core.Models;
using AirPad.Core.Network;
using AirPad.Core.Protocol;
using AirPad.Core.Protocol.Models;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace AirPad.Core.Tests;

/// <summary>Control-plane framing: a request may arrive in any number of TCP reads.</summary>
public sealed class ControlFramingRegressionTests : IAsyncLifetime
{
    private TcpControlServer? _server;
    private int _port;

    public async Task InitializeAsync()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        _port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _server = new TcpControlServer(_port, new AirPadLogger(minimumLevel: LogLevel.Error), HandleAsync);
        await _server.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_server != null)
            await _server.StopAsync();
        _server?.Dispose();
    }

    private static async Task HandleAsync(Request request, Response response, CancellationToken ct)
    {
        response.Headers.Add("X-Echo-Length", (request.Body?.Length ?? 0).ToString());
        await response.WriteAsync(new byte[32], 0, 32);
    }

    [Fact]
    public async Task RequestSplitAcrossReads_IsAnswered()
    {
        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, _port);
        var stream = client.GetStream();

        var body = new byte[300];
        var head = Encoding.ASCII.GetBytes($"SETUP rtsp://127.0.0.1/1 RTSP/1.0\r\nCSeq: 7\r\nContent-Length: {body.Length}\r\n\r\n");
        foreach (var chunk in head.Concat(body).Chunk(97))
        {
            await stream.WriteAsync(chunk);
            await Task.Delay(40); // force separate reads on the server
        }

        var response = await ReadResponseAsync(stream);
        Assert.Contains("CSeq: 7", response, StringComparison.Ordinal);
        Assert.Contains("X-Echo-Length: 300", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CloseAllClients_HangsUpLiveConnections()
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("OPTIONS * RTSP/1.0\r\nCSeq: 1\r\n\r\n"));
        await ReadResponseAsync(stream); // the server has accepted and is serving us

        _server!.CloseAllClients();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Assert.Equal(0, await stream.ReadAsync(new byte[16], cts.Token)); // EOF = the iPad would see the hang-up
    }

    private static async Task<string> ReadResponseAsync(NetworkStream stream)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var buffer = new List<byte>();
        var tmp = new byte[4096];
        while (true)
        {
            int read = await stream.ReadAsync(tmp, cts.Token);
            if (read <= 0)
                break;
            buffer.AddRange(tmp.Take(read));
            var text = Encoding.ASCII.GetString(buffer.ToArray());
            int end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end < 0)
                continue;
            int len = 0;
            foreach (var line in text[..end].Split("\r\n"))
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    len = int.Parse(line[15..].Trim());
            if (buffer.Count >= end + 4 + len)
                return text;
        }
        return Encoding.ASCII.GetString(buffer.ToArray());
    }
}

/// <summary>The mirror handler is shared by every stream of a receiver run: no state may leak between streams.</summary>
public sealed class MirrorStreamRegressionTests
{
    private static readonly byte[] Key = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
    private static readonly byte[] Shared = Enumerable.Range(100, 32).Select(i => (byte)i).ToArray();

    [Fact]
    public async Task SecondStreamOnTheSameHandler_DecryptsFromAFreshKeystream()
    {
        var handler = new MirrorStreamHandler(new AirPadLogger(minimumLevel: LogLevel.Error));
        int packets = 0;
        handler.H264Received += _ => Interlocked.Increment(ref packets);

        // Total payload size is not a multiple of 16, so the CTR carry is non-zero when a stream ends.
        for (int run = 1; run <= 2; run++)
        {
            packets = 0;
            await RunStreamAsync(handler, [1003, 517, 1211]);
            Assert.True(packets == 3, $"stream #{run}: only {packets}/3 frames survived decryption");
        }
    }

    private static async Task RunStreamAsync(MirrorStreamHandler handler, int[] nalSizes)
    {
        var plain = nalSizes.Select(n =>
        {
            var p = new byte[4 + n]; // AVCC: 4-byte length + one non-IDR slice NAL
            BinaryPrimitives.WriteInt32BigEndian(p, n);
            p[4] = 0x41;
            return p;
        }).ToArray();

        var all = new byte[(plain.Sum(p => p.Length) + 15) / 16 * 16];
        int offset = 0;
        foreach (var p in plain) { p.CopyTo(all, offset); offset += p.Length; }
        var enc = new byte[all.Length];
        MirrorStreamHandler.CreateStreamCipher(Key, Shared, "42").ProcessBytes(all, 0, all.Length, enc, 0);

        var wire = new MemoryStream();
        offset = 0;
        foreach (var p in plain)
        {
            var header = new byte[128]; // payload size (LE) + type 0 = video
            BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)p.Length);
            wire.Write(header);
            wire.Write(enc, offset, p.Length);
            offset += p.Length;
        }

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var sender = Task.Run(async () =>
            {
                using var c = new TcpClient();
                await c.ConnectAsync(IPAddress.Loopback, port);
                await c.GetStream().WriteAsync(wire.ToArray());
            });
            using var server = await listener.AcceptTcpClientAsync();
            var session = new Session("mirror-regress") { DecryptedAesKey = Key, EcdhShared = Shared, StreamConnectionId = "42" };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await handler.RunAsync(server, session, [], cts.Token);
            await sender;
        }
        finally
        {
            listener.Stop();
        }
    }
}

public sealed class AudioUdpRegressionTests
{
    private static readonly AirPadLogger Log = new(minimumLevel: LogLevel.Error);

    [Fact]
    public async Task StopAsync_DoesNotWaitOutBlockedReceiveCalls()
    {
        var server = new AudioUdpServer(Log, FreeUdpPort(), FreeUdpPort());
        await server.StartAsync();
        await Task.Delay(200); // let the receive loops block in ReceiveFrom, as they do in a live session

        var sw = Stopwatch.StartNew();
        await server.StopAsync();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"StopAsync took {sw.Elapsed.TotalSeconds:0.0} s");
    }

    [Fact]
    public async Task Packets_AreDecodedInSequenceOrder_AndRetransmitResponsesAreUnwrapped()
    {
        int controlPort = FreeUdpPort(), dataPort = FreeUdpPort();
        var session = new Session("audio-regress")
        {
            DecryptedAesKey = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray(),
            EcdhShared = Enumerable.Range(50, 32).Select(i => (byte)i).ToArray(),
            AesIv = Enumerable.Range(200, 16).Select(i => (byte)i).ToArray(),
        };
        var decoder = new RecordingDecoder();
        var server = new AudioUdpServer(Log, controlPort, dataPort);
        await server.StartAsync();
        try
        {
            server.AttachSession(session, decoder);
            using var udp = new UdpClient();

            // 10, 12, 11 on the data port: arrival order != sequence order.
            foreach (ushort seq in new ushort[] { 10, 12, 11 })
            {
                var packet = Rtp(seq, session);
                await udp.SendAsync(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, dataPort));
                await Task.Delay(10);
            }

            // 13 arrives as a retransmit response on the control port: 4-byte header + the original RTP packet.
            var inner = Rtp(13, session);
            var wrapped = new byte[] { 0x80, 0xD6, 0x00, 0x01 }.Concat(inner).ToArray();
            await udp.SendAsync(wrapped, wrapped.Length, new IPEndPoint(IPAddress.Loopback, controlPort));

            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline && decoder.Count < 4)
                await Task.Delay(20);

            Assert.Equal(new byte[] { 10, 11, 12, 13 }, decoder.Snapshot());
        }
        finally
        {
            await server.StopAsync();
        }
    }

    // RTP header + AES-CBC payload whose first plaintext byte is the sequence number.
    private static byte[] Rtp(ushort seq, Session s)
    {
        var plain = new byte[48];
        plain[0] = (byte)seq;
        using var sha = SHA512.Create();
        var key = sha.ComputeHash(s.DecryptedAesKey!.Concat(s.EcdhShared!).ToArray()).Take(16).ToArray();
        var cipher = CipherUtilities.GetCipher("AES/CBC/NoPadding");
        cipher.Init(true, new ParametersWithIV(ParameterUtilities.CreateKeyParameter("AES", key), s.AesIv!, 0, 16));
        var enc = cipher.DoFinal(plain);

        var packet = new byte[12 + enc.Length];
        packet[0] = 0x80;
        packet[1] = 0x60;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), seq);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), seq * 352u);
        enc.CopyTo(packet, 12);
        return packet;
    }

    private static int FreeUdpPort()
    {
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
        s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)s.LocalEndPoint!).Port;
    }

    private sealed class RecordingDecoder : AudioDecoder
    {
        private readonly List<byte> _seen = [];

        public RecordingDecoder() : base(Log) { }

        public override bool IsOpen => true;

        public int Count { get { lock (_seen) return _seen.Count; } }

        public byte[] Snapshot() { lock (_seen) return _seen.ToArray(); }

        public override List<PcmPacket> Decode(byte[] payload, uint timestamp)
        {
            lock (_seen) _seen.Add(payload[0]);
            return [new PcmPacket { Data = new byte[4], Timestamp = timestamp }];
        }
    }
}

public sealed class VideoRegressionTests
{
    // Any Annex-B H.264 file works; this one ships with Windows.
    private const string SamplePath = @"C:\Windows\bcastdvr\broadcastpause720.h264";

    [SkippableFact]
    public void H264Decoder_ReturnsEveryAccessUnitImmediately()
    {
        Skip.IfNot(FFmpegLoader.TryLoad(new AirPadLogger(minimumLevel: LogLevel.Error)), "FFmpeg DLLs not present (set AIRPAD_FFMPEG_DIR).");
        Skip.IfNot(File.Exists(SamplePath), "Sample H.264 stream not present.");

        var units = SplitAccessUnits(File.ReadAllBytes(SamplePath));
        using var decoder = new H264Decoder(new AirPadLogger(minimumLevel: LogLevel.Error), preferHardware: false);
        Assert.True(decoder.Open());

        // With av_parser_parse2 in front, the first Feed returned nothing and the last frame never came out.
        int frames = 0;
        for (int i = 0; i < units.Count; i++)
        {
            var decoded = decoder.Feed(units[i], DateTime.UtcNow);
            Assert.True(decoded.Count == 1, $"access unit #{i} returned {decoded.Count} frames");
            frames += decoded.Count;
            foreach (var frame in decoded)
            {
                Assert.Equal(frame.Width * 4, frame.Stride);
                frame.Dispose();
            }
        }
        Assert.Equal(units.Count, frames);
    }

    [Fact]
    public void PooledFrame_DisposedTwice_ReturnsItsBufferOnce()
    {
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(4096);
        var frame = new Video.VideoFrame { Width = 32, Height = 32, Stride = 128, Pixels = buffer, Pooled = true };
        frame.Dispose();
        frame.Dispose();

        // A double return would hand the same array to two renters, which then paint over each other.
        var a = System.Buffers.ArrayPool<byte>.Shared.Rent(4096);
        var b = System.Buffers.ArrayPool<byte>.Shared.Rent(4096);
        Assert.NotSame(a, b);
    }

    // Splits an Annex-B stream into access units (a new one starts at AUD/SPS/PPS/SEI or a slice with first_mb == 0).
    private static List<byte[]> SplitAccessUnits(byte[] d)
    {
        var starts = new List<int>();
        for (int i = 0; i + 3 < d.Length; i++)
        {
            if (d[i] == 0 && d[i + 1] == 0 && d[i + 2] == 1)
            {
                starts.Add(i > 0 && d[i - 1] == 0 ? i - 1 : i);
                i += 2;
            }
        }

        var units = new List<byte[]>();
        var current = new MemoryStream();
        bool hasSlice = false;
        for (int k = 0; k < starts.Count; k++)
        {
            int s = starts[k], e = k + 1 < starts.Count ? starts[k + 1] : d.Length;
            int header = s + (d[s + 2] == 0 ? 4 : 3);
            int type = d[header] & 0x1F;
            bool slice = type is 1 or 5;
            if (hasSlice && (type is 6 or 7 or 8 or 9 || (slice && (d[header + 1] & 0x80) != 0)))
            {
                units.Add(current.ToArray());
                current.SetLength(0);
                hasSlice = false;
            }
            current.Write(d, s, e - s);
            hasSlice |= slice;
        }
        if (current.Length > 0)
            units.Add(current.ToArray());
        return units;
    }
}

public sealed class ShutdownRegressionTests
{
    // Like a WPF dispatcher whose thread is blocked in OnExit: anything posted to it is never run.
    private sealed class NeverPumpedContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) { }
    }

    [Fact]
    public async Task Dispose_WithAConnectedDevice_DoesNotDeadlockAUiThread()
    {
        var path = Path.Combine(Path.GetTempPath(), $"airpad-shutdown-{Guid.NewGuid():N}.json");
        var store = new Config.SettingsStore(path);
        store.Save(new Config.AirPadSettings
        {
            ReceiverName = "AirPad-Test-Shutdown",
            AirPlayPort = 17920,
            AirTunesPort = 15920,
            AudioControlPort = 17922,
            AudioDataPort = 17923,
            LogLevel = "Error",
        });
        var manager = new Connection.ConnectionManager(new AirPadLogger(minimumLevel: LogLevel.Error), store);
        try
        {
            await manager.StartAsync();
            using var device = new TcpClient(); // a live control connection: StopAsync has to wait for it
            await device.ConnectAsync(IPAddress.Loopback, 15920);
            await Task.Delay(100);

            using var done = new ManualResetEventSlim();
            var ui = new Thread(() =>
            {
                SynchronizationContext.SetSynchronizationContext(new NeverPumpedContext());
                manager.Dispose(); // what App.OnExit does
                done.Set();
            });
            ui.Start();

            Assert.True(done.Wait(TimeSpan.FromSeconds(15)), "ConnectionManager.Dispose deadlocked on a UI-style sync context");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}

public sealed class SessionRegressionTests
{
    [Fact]
    public async Task NewEkey_DropsTheAesKeyDerivedFromThePreviousHandshake()
    {
        var log = new AirPadLogger(minimumLevel: LogLevel.Error);
        var handler = new AirPlayRequestHandler(log, new PairingService(log), new FairPlayService(log),
            "Test", "11:22:33:44:55:66", 7000, 5000, 7002, 7003);

        // The iPad reuses Active-Remote across connections, so the same Session object is handed out again.
        var session = await SessionManager.Current.GetOrCreateAsync("regress-stale-key");
        session.KeyMsg = new byte[164];
        session.EcdhShared = new byte[32];
        session.PairVerified = true;
        session.DecryptedAesKey = new byte[16]; // left over from the previous connection

        var plist = new Dictionary<string, object>
        {
            ["ekey"] = new byte[72],
            ["eiv"] = new byte[16],
            ["isScreenMirroringSession"] = true,
            ["timingPort"] = 6001,
        };
        byte[] body;
        using (var ms = new MemoryStream())
        {
            new BinaryPlistWriter().WriteObject(ms, plist, false);
            body = ms.ToArray();
        }
        var head = $"SETUP rtsp://127.0.0.1/1 RTSP/1.0\r\nCSeq: 3\r\nActive-Remote: regress-stale-key\r\nContent-Length: {body.Length}\r\n\r\n";
        var request = new Request(Convert.ToHexString(Encoding.ASCII.GetBytes(head).Concat(body).ToArray()));

        await handler.HandleAsync(request, request.GetBaseResponse(), CancellationToken.None);

        Assert.Null(session.DecryptedAesKey);
    }
}
