using System.IO;
using System.Runtime.Serialization.Plists;
using System.Text;
using System.Text.RegularExpressions;
using AirPad.Core.Audio;
using AirPad.Core.Auth;
using AirPad.Core.Logging;using AirPad.Core.Models;
using AirPad.Core.Models.Enums;
using AirPad.Core.Protocol.Models;

namespace AirPad.Core.Protocol;

/// <summary>
/// Handles every RTSP/HTTP request of the AirPlay control plane:
/// /info, /pair-setup, /pair-verify, /fp-setup, SETUP, RECORD,
/// GET/SET_PARAMETER, OPTIONS, ANNOUNCE, FLUSH, TEARDOWN, /feedback.
/// Media callbacks are raised for the connection manager to route
/// into the video/audio pipelines.
/// </summary>
public sealed class AirPlayRequestHandler
{
    private readonly AirPadLogger _log;
    private readonly PairingService _pairing;
    private readonly FairPlayService _fairPlay;
    private readonly SessionManager _sessions = SessionManager.Current;

    private readonly int _airPlayPort;
    private readonly int _airTunesPort;
    private readonly int _audioControlPort;
    private readonly int _audioDataPort;
    private readonly string _receiverName;
    private readonly string _deviceId;

    public event Func<Session, Task>? MirrorSessionReady;
    public event Func<Session, Task>? AudioSessionReady;
    public event Func<Session, string, Task>? SessionTeardown;
    public event Action<decimal>? VolumeChanged;
    public event Action<string>? DeviceNameSeen;

    /// <summary>Current output volume (linear 0..1), for answering GET_PARAMETER.</summary>
    public Func<double>? GetVolumeLinear { get; set; }

    public AirPlayRequestHandler(
        AirPadLogger log,
        PairingService pairing,
        FairPlayService fairPlay,
        string receiverName,
        string deviceId,
        int airPlayPort,
        int airTunesPort,
        int audioControlPort,
        int audioDataPort)
    {
        _log = log;
        _pairing = pairing;
        _fairPlay = fairPlay;
        _receiverName = receiverName;
        _deviceId = deviceId;
        _airPlayPort = airPlayPort;
        _airTunesPort = airTunesPort;
        _audioControlPort = audioControlPort;
        _audioDataPort = audioDataPort;
    }

    public async Task HandleAsync(Request request, Response response, CancellationToken ct)
    {
        var sessionId = TryGetHeader(request, "Active-Remote") ?? "default";
        var session = await _sessions.GetOrCreateAsync(sessionId).ConfigureAwait(false);

        if (request.Type == RequestType.GET && "/info".Equals(request.Path, StringComparison.OrdinalIgnoreCase))
        {
            await HandleGetInfoAsync(response).ConfigureAwait(false);
            return;
        }

        if (request.Type == RequestType.POST && "/pair-setup".Equals(request.Path, StringComparison.OrdinalIgnoreCase))
        {
            response.Headers.Add("Content-Type", "application/octet-stream");
            await response.WriteAsync(_pairing.ServerPublicKey, 0, _pairing.ServerPublicKey.Length).ConfigureAwait(false);
            _log.Debug("pair-setup: returned server public key");
            return;
        }

        if (request.Type == RequestType.POST && "/pair-verify".Equals(request.Path, StringComparison.OrdinalIgnoreCase))
        {
            await HandlePairVerifyAsync(request, response, session, sessionId).ConfigureAwait(false);
            return;
        }

        if (request.Type == RequestType.POST && "/fp-setup".Equals(request.Path, StringComparison.OrdinalIgnoreCase))
        {
            await HandleFpSetupAsync(request, response, session, sessionId).ConfigureAwait(false);
            return;
        }

        if (request.Type == RequestType.SETUP)
        {
            await HandleSetupAsync(request, response, session, sessionId, ct).ConfigureAwait(false);
            return;
        }

        if (request.Type == RequestType.GET_PARAMETER)
        {
            var data = request.Body != null ? Encoding.ASCII.GetString(request.Body) : "";
            if (data.Contains("volume", StringComparison.OrdinalIgnoreCase))
            {
                double linear = GetVolumeLinear?.Invoke() ?? 1.0;
                double db = VolumeUtils.LinearToDb(linear);
                var output = Encoding.ASCII.GetBytes(
                    $"volume: {db.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture)}\r\n");
                response.Headers.Add("Content-Type", "text/parameters");
                await response.WriteAsync(output, 0, output.Length).ConfigureAwait(false);
            }
            return;
        }

        if (request.Type == RequestType.RECORD)
        {
            response.Headers.Add("Audio-Latency", "0");
            _log.Info("RECORD: streaming started by device");
            return;
        }

        if (request.Type == RequestType.SET_PARAMETER)
        {
            HandleSetParameter(request);
            return;
        }

        if (request.Type == RequestType.OPTIONS)
        {
            response.Headers.Add("Public", "SETUP, RECORD, PAUSE, FLUSH, TEARDOWN, OPTIONS, GET_PARAMETER, SET_PARAMETER, ANNOUNCE");
            return;
        }

        if (request.Type == RequestType.ANNOUNCE)
        {
            _log.Debug("ANNOUNCE received");
            return;
        }

        if (request.Type == RequestType.FLUSH)
        {
            int nextSeq = -1;
            var rtpInfo = TryGetHeader(request, "RTP-Info");
            if (!string.IsNullOrWhiteSpace(rtpInfo))
            {
                var m = Regex.Match(rtpInfo, @"seq\=([^;]*)");
                if (m.Success && int.TryParse(m.Groups[1].Value, out var seq))
                    nextSeq = seq;
            }
            _log.Info($"FLUSH received (next_seq={nextSeq})");
            AudioFlushRequested?.Invoke(nextSeq);
            return;
        }

        if (request.Type == RequestType.TEARDOWN)
        {
            await HandleTeardownAsync(request, session, sessionId).ConfigureAwait(false);
            return;
        }

        if (request.Type == RequestType.POST && "/play".Equals(request.Path, StringComparison.OrdinalIgnoreCase))
        {
            // Video apps (YouTube, Netflix, …) switch from screen mirroring to
            // direct URL playback (HLS). We don't implement that player yet, so
            // mirroring ends here by the iPad's own choice (TEARDOWN follows).
            _log.Info("AirPlay video handoff (/play) requested — direct URL playback (HLS) is not " +
                      "implemented, the iPad stops mirroring here. Play the video in Safari to keep mirroring.");
            LogPlayUrl(request.Body);
            return;
        }

        // /feedback, /command, /reverse, /server-info and anything else:
        // plain 200 OK keeps the session alive.
        _log.Debug($"{request.Type} {request.Path}: acknowledged");
    }

    private void LogPlayUrl(byte[]? body)
    {
        if (body is not { Length: > 0 })
            return;
        try
        {
            using var mem = new MemoryStream(body);
            var plist = (Dictionary<object, object>)new BinaryPlistReader().ReadObject(mem)!;
            foreach (var kv in plist)
            {
                if (kv.Key is string key && key.Equals("Content-Location", StringComparison.OrdinalIgnoreCase))
                {
                    var url = Convert.ToString(kv.Value) ?? "";
                    string host;
                    try { host = new Uri(url).Host; } catch { host = url.Length > 80 ? url[..80] + "…" : url; }
                    _log.Debug($"/play Content-Location host: {host}");
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"/play body not parsed: {ex.Message}");
        }
    }

    public event Action<int>? AudioFlushRequested;

    private static string? TryGetHeader(Request request, string name)
    {
        try
        {
            if (request.Headers.ContainsKey(name))
                return request.Headers[name];
        }
        catch { }
        return null;
    }

    private async Task HandleGetInfoAsync(Response response)
    {
        var dict = new Dictionary<string, object>
        {
            ["features"] = (long)AirPlayFeatures.Info,
            ["name"] = _receiverName,
            ["displays"] = new List<Dictionary<string, object>>
            {
                new()
                {
                    ["primaryInputDevice"] = 1,
                    ["rotation"] = true,
                    ["widthPhysical"] = 0,
                    ["edid"] = "AP///////wAGEBOuhXxiyAoaAQS1PCJ4IA8FrlJDsCYOT1QAAAABAQEBAQEBAQEBAQEBAQEBAAAAEAAAAAAAAAAAAAAAAAAAAAAAEAAAAAAAAAAAAAAAAAAAAAAA/ABpTWFjCiAgICAgICAgAAAAAAAAAAAAAAAAAAAAAAAAAqBwE3kDAAMAFIBuAYT/E58AL4AfAD8LUQACAAQAf4EY+hAAAQEAEnYx/Hj7/wIQiGLT+vj4/v//AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAADHkHATeQMAAwFQU+wABP8PnwAvAB8A/whBAAIABABM0AAE/w6fAC8AHwBvCD0AAgAEAMyRAAR/DJ8ALwAfAAcHMwACAAQAVV4ABP8JnwAvAB8AnwUoAAIABAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB+Q",
                    ["widthPixels"] = 1920.0,
                    ["uuid"] = "061013ae-7b0f-4305-984b-974f677a150b",
                    ["heightPhysical"] = 0,
                    ["features"] = 30,
                    ["heightPixels"] = 1080.0,
                    ["overscanned"] = false,
                },
            },
            ["audioFormats"] = new List<Dictionary<string, object>>
            {
                new() { ["type"] = 100, ["audioInputFormats"] = 67108860, ["audioOutputFormats"] = 67108860 },
                new() { ["type"] = 101, ["audioInputFormats"] = 67108860, ["audioOutputFormats"] = 67108860 },
            },
            ["vv"] = 2,
            ["statusFlags"] = 4,
            ["keepAliveLowPower"] = true,
            ["sourceVersion"] = "220.68",
            ["pk"] = Convert.ToHexString(_pairing.ServerPublicKey).ToLowerInvariant(),
            ["keepAliveSendStatsAsBody"] = true,
            ["deviceID"] = _deviceId,
            ["model"] = "AppleTV5,3",
            ["audioLatencies"] = new List<Dictionary<string, object>>
            {
                new() { ["outputLatencyMicros"] = 0, ["type"] = 100, ["audioType"] = "default", ["inputLatencyMicros"] = 0 },
                new() { ["outputLatencyMicros"] = 0, ["type"] = 101, ["audioType"] = "default", ["inputLatencyMicros"] = 0 },
            },
            ["macAddress"] = _deviceId,
        };

        byte[] output;
        using (var ms = new MemoryStream())
        {
            new BinaryPlistWriter().WriteObject(ms, dict, false);
            output = ms.ToArray();
        }

        response.Headers.Add("Content-Type", "application/x-apple-binary-plist");
        await response.WriteAsync(output, 0, output.Length).ConfigureAwait(false);
        _log.Debug("GET /info: returned receiver capabilities");
    }

    private async Task HandlePairVerifyAsync(Request request, Response response, Session session, string sessionId)
    {
        if (request.Body == null || request.Body.Length < 68)
        {
            _log.Warn($"pair-verify: malformed body ({request.Body?.Length ?? 0} bytes)");
            response.StatusCode = StatusCode.BADREQUEST;
            return;
        }

        using var mem = new MemoryStream(request.Body);
        using var reader = new BinaryReader(mem);
        byte flag = reader.ReadByte();
        reader.ReadBytes(3);

        if (flag > 0)
        {
            session.EcdhTheirs = reader.ReadBytes(32);
            session.EdTheirs = reader.ReadBytes(32);
            var (_, shared) = _pairing.HandlePairVerifyStep1(session.EcdhTheirs, session.EdTheirs, out var output);
            session.EcdhOurs = output.Take(32).ToArray();
            session.EcdhShared = shared;
            await _sessions.SaveAsync(sessionId, session).ConfigureAwait(false);

            response.Headers.Add("Content-Type", "application/octet-stream");
            await response.WriteAsync(output, 0, output.Length).ConfigureAwait(false);
            _log.Info("pair-verify step 1 completed");
        }
        else
        {
            var signature = reader.ReadBytes(64);
            bool ok = session.EcdhOurs != null && session.EcdhTheirs != null && session.EdTheirs != null && session.EcdhShared != null &&
                      _pairing.HandlePairVerifyStep2(signature, session.EcdhOurs, session.EcdhTheirs, session.EdTheirs, session.EcdhShared);
            session.PairVerified = ok;
            await _sessions.SaveAsync(sessionId, session).ConfigureAwait(false);
            _log.Info($"Device pairing {(ok ? "verified" : "FAILED")}");
            if (!ok)
                response.StatusCode = StatusCode.UNAUTHORIZED;
        }
    }

    private async Task HandleFpSetupAsync(Request request, Response response, Session session, string sessionId)
    {
        if (!session.PairCompleted)
        {
            _log.Warn("fp-setup rejected: pairing not completed");
            response.StatusCode = StatusCode.UNAUTHORIZED;
            return;
        }

        var reply = _fairPlay.Handle(request.Body ?? Array.Empty<byte>(), out var keyMsg);
        if (reply == null)
        {
            response.StatusCode = StatusCode.BADREQUEST;
            return;
        }

        if (keyMsg != null)
        {
            session.KeyMsg = keyMsg;
            await _sessions.SaveAsync(sessionId, session).ConfigureAwait(false);
            _log.Info("FairPlay key message stored");
        }

        response.Headers.Add("Content-Type", "application/octet-stream");
        await response.WriteAsync(reply, 0, reply.Length).ConfigureAwait(false);
    }

    private async Task HandleSetupAsync(Request request, Response response, Session session, string sessionId, CancellationToken ct)
    {
        if (!session.FairPlaySetupCompleted)
        {
            _log.Warn("SETUP rejected: FairPlay handshake not completed");
            response.StatusCode = StatusCode.BADREQUEST;
            return;
        }

        if (request.Body == null || request.Body.Length == 0)
        {
            response.StatusCode = StatusCode.BADREQUEST;
            return;
        }

        var plistReader = new BinaryPlistReader();
        Dictionary<object, object> plist;
        using (var mem = new MemoryStream(request.Body))
        {
            try
            {
                plist = (Dictionary<object, object>)plistReader.ReadObject(mem)!;
            }
            catch (Exception ex)
            {
                _log.Warn($"SETUP: invalid plist body: {ex.Message}");
                response.StatusCode = StatusCode.BADREQUEST;
                return;
            }
        }

        if (plist.ContainsKey("streams"))
        {
            if (plist["streams"] is not object[] streamsArr || streamsArr.Length == 0 ||
                streamsArr[0] is not Dictionary<object, object> stream)
            {
                _log.Warn("SETUP: malformed streams array");
                response.StatusCode = StatusCode.BADREQUEST;
                return;
            }
            short type = (short)PlistLong(GetDictValue(stream, "type"));

            if (type == 110) // screen mirroring
            {
                var scid = GetDictValue(stream, "streamConnectionID");
                session.StreamConnectionId = FormatStreamConnectionId(scid);
                await _sessions.SaveAsync(sessionId, session).ConfigureAwait(false);

                var streams = new Dictionary<string, List<Dictionary<string, int>>>
                {
                    ["streams"] = new() { new() { ["type"] = 110, ["dataPort"] = _airPlayPort } },
                };
                await WritePlistResponseAsync(response, streams).ConfigureAwait(false);
                _log.Info($"Mirror SETUP: stream connection {session.StreamConnectionId}, data port {_airPlayPort}");

                if (MirrorSessionReady != null)
                    await MirrorSessionReady.Invoke(session).ConfigureAwait(false);
                return;
            }

            if (type == 96) // audio
            {
                var af = GetDictValue(stream, "audioFormat");
                if (af != null)
                {
                    session.AudioFormat = (AudioFormat)PlistLong(af);
                    _log.Info($"Audio SETUP: format {DescribeAudioFormat(session.AudioFormat)}");
                }

                var streams = new Dictionary<string, List<Dictionary<string, int>>>
                {
                    ["streams"] = new() { new() { ["type"] = 96, ["controlPort"] = _audioControlPort, ["dataPort"] = _audioDataPort } },
                };
                await WritePlistResponseAsync(response, streams).ConfigureAwait(false);
                await _sessions.SaveAsync(sessionId, session).ConfigureAwait(false);

                if (AudioSessionReady != null)
                    await AudioSessionReady.Invoke(session).ConfigureAwait(false);
                return;
            }

            _log.Warn($"SETUP: unknown stream type {type}");
            response.StatusCode = StatusCode.BADREQUEST;
            return;
        }

        // Session-level SETUP: crypto material + timing.
        DumpPlist(plist);
        if (plist.ContainsKey("et"))
            _log.Debug($"SETUP session et={PlistLong(plist["et"])}");
        if (plist.ContainsKey("ekey") && plist["ekey"] is byte[] ekey)
        {
            session.AesKey = ekey;
            // The iPad reuses Active-Remote across connections, so this Session may still hold the AES key
            // derived from the previous handshake; the video/audio paths only derive it when it is null.
            session.DecryptedAesKey = null;
        }
        if (plist.ContainsKey("eiv") && plist["eiv"] is byte[] eiv)
            session.AesIv = eiv;
        if (plist.ContainsKey("isScreenMirroringSession"))
            session.MirroringSession = PlistBool(plist["isScreenMirroringSession"]);
        if (plist.ContainsKey("timingPort"))
        {
            long tp = PlistLong(plist["timingPort"]);
            // bplist 2-byte ints surface as signed Int16 (e.g. -2371 for port
            // 63165); reinterpret negative values as unsigned ports.
            if (tp < 0)
            {
                tp += 65536;
                _log.Debug($"SETUP: timingPort reinterpreted as unsigned ({tp})");
            }
            session.TimingPort = (int)Math.Clamp(tp, 0, 65535);
            if (tp is < 0 or > 65535)
                _log.Warn($"SETUP: timingPort {tp} out of range, clamped to {session.TimingPort}");
        }
        if (plist.ContainsKey("name"))
            DeviceNameSeen?.Invoke(Convert.ToString(plist["name"]) ?? "");

        await _sessions.SaveAsync(sessionId, session).ConfigureAwait(false);

        var timing = new Dictionary<string, int>
        {
            ["timingPort"] = _airTunesPort,
            ["eventPort"] = _airTunesPort,
        };
        await WritePlistResponseAsync(response, timing).ConfigureAwait(false);
        _log.Info("Session SETUP completed (timing + event ports exchanged)");
    }

    private void HandleSetParameter(Request request)
    {
        try
        {
            var contentType = request.Headers.ContainsKey("Content-Type") ? request.Headers["Content-Type"] : "";
            if (contentType.StartsWith("text/parameters", StringComparison.OrdinalIgnoreCase))
            {
                var body = request.Body != null ? Encoding.ASCII.GetString(request.Body) : "";
                var parts = body.Split(":", StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim(' ', '\r', '\n')).ToArray();
                if (parts.Length == 2)
                {
                    if (parts[0].Equals("volume", StringComparison.OrdinalIgnoreCase) && decimal.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var volDb))
                    {
                        // request.Body contains 'volume: N.NNNNNN' in dB (-30..0, down to -144 = mute).
                        double linear = VolumeUtils.DbToLinear((double)volDb);
                        _log.Info($"Volume set to {volDb:0.00} dB ({linear:P0}) by device");
                        VolumeChanged?.Invoke((decimal)linear);
                    }
                    else if (parts[0].Equals("progress", StringComparison.OrdinalIgnoreCase))
                    {
                        _log.Debug($"Progress: {parts[1]}");
                    }
                }
            }
            else if (contentType.StartsWith("application/x-dmap-tagged", StringComparison.OrdinalIgnoreCase) && request.Body != null)
            {
                try
                {
                    var dmap = new AirPad.Core.DmapTagged.DMapTagged();
                    dmap.Decode(request.Body);
                    _log.Debug("DMAP metadata received");
                }
                catch (Exception ex)
                {
                    _log.Warn($"DMAP decode failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"SET_PARAMETER handling failed: {ex.Message}");
        }
    }

    private async Task HandleTeardownAsync(Request request, Session session, string sessionId)
    {
        try
        {
            if (request.Body is { Length: > 0 })
            {
                var plistReader = new BinaryPlistReader();
                using var mem = new MemoryStream(request.Body);
                var plist = (Dictionary<object, object>)plistReader.ReadObject(mem)!;
                if (plist.ContainsKey("streams") && plist["streams"] is object[] streams && streams.Length > 0 &&
                    streams[^1] is Dictionary<object, object> last)
                {
                    short type = (short)PlistLong(GetDictValue(last, "type"));
                    _log.Info($"TEARDOWN stream type {type}");
                    if (SessionTeardown != null)
                        await SessionTeardown.Invoke(session, type == 110 ? "mirror" : "audio").ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"TEARDOWN parse issue: {ex.Message}");
        }

        _log.Info("TEARDOWN session");
        if (SessionTeardown != null)
            await SessionTeardown.Invoke(session, "all").ConfigureAwait(false);
    }

    private static object? GetDictValue(Dictionary<object, object> dict, string key)
    {
        foreach (var kv in dict)
        {
            if (kv.Key is string s && s.Equals(key, StringComparison.Ordinal))
                return kv.Value;
        }
        return dict.TryGetValue(key, out var value) ? value : null;
    }

    /// <summary>
    /// Best-effort plist integer coercion. Real iPadOS devices encode small
    /// integers in 1/2/4/8-byte forms (reader always yields Int64), but newer
    /// OS versions may widen fields or send numeric strings — never throw.
    /// </summary>
    internal static long PlistLong(object? value, long defaultValue = 0)
    {
        try
        {
            return value switch
            {
                null => defaultValue,
                long l => l,
                int i => i,
                short s => s,
                byte b => b,
                ulong ul => ul > long.MaxValue ? defaultValue : (long)ul,
                double d => double.IsNaN(d) || double.IsInfinity(d) ? defaultValue : (long)d,
                float f => float.IsNaN(f) || float.IsInfinity(f) ? defaultValue : (long)f,
                string str when long.TryParse(str, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
                bool bl => bl ? 1 : 0,
                _ => Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture),
            };
        }
        catch
        {
            return defaultValue;
        }
    }

    internal static bool PlistBool(object? value) => value switch
    {
        null => false,
        bool b => b,
        string s when bool.TryParse(s, out var parsed) => parsed,
        _ => PlistLong(value) != 0,
    };

    /// <summary>
    /// Formats the mirror streamConnectionID exactly as the iPad expects it
    /// in the AES key derivation ("AirPlayStreamKey" + id): the wire value is
    /// an unsigned 64-bit integer, but the plist codec surfaces it as signed
    /// Int64 (e.g. -1378097000387297060). Formatting the signed value would
    /// derive a wrong video key and silently drop every frame.
    /// </summary>
    internal static string FormatStreamConnectionId(object? value) => value switch
    {
        string s => s,
        null => "0",
        _ => ((ulong)PlistLong(value)).ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private void DumpPlist(Dictionary<object, object> plist)
    {
        if (_log.MinimumLevel > Logging.LogLevel.Debug)
            return;
        foreach (var kv in plist)
        {
            string val = kv.Value switch
            {
                byte[] b => $"<{b.Length} bytes>",
                object[] arr => $"[{arr.Length} items]",
                null => "null",
                _ => kv.Value.ToString() ?? "null",
            };
            _log.Debug($"SETUP plist: {kv.Key} = {val} ({kv.Value?.GetType().Name ?? "null"})");
        }
    }

    private static async Task WritePlistResponseAsync(Response response, System.Collections.IDictionary graph)
    {
        byte[] output;
        using (var ms = new MemoryStream())
        {
            new BinaryPlistWriter().WriteObject(ms, graph, false);
            output = ms.ToArray();
        }
        response.Headers.Add("Content-Type", "application/x-apple-binary-plist");
        await response.WriteAsync(output, 0, output.Length).ConfigureAwait(false);
    }

    private static string DescribeAudioFormat(AudioFormat format) => format switch
    {
        AudioFormat.ALAC => "Apple Lossless (ALAC)",
        AudioFormat.AAC => "AAC",
        AudioFormat.AAC_ELD => "AAC-ELD (low-delay mirroring audio)",
        AudioFormat.PCM => "PCM",
        _ => $"Unknown ({(int)format})",
    };
}
