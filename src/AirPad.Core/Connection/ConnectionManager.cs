using AirPad.Core.Audio;
using AirPad.Core.Auth;
using AirPad.Core.Config;
using AirPad.Core.Discovery;
using AirPad.Core.Logging;
using AirPad.Core.Media;
using AirPad.Core.Network;
using AirPad.Core.Protocol;
using AirPad.Core.Protocol.Models;
using AirPad.Core.Stats;
using AirPad.Core.Video;

namespace AirPad.Core.Connection;

public enum ReceiverState
{
    Ready,
    WaitingForDevice,
    Connected,
    Streaming,
}

/// <summary>
/// Owns the whole receive pipeline and its state machine:
/// Network → Receive → Decode → (events for) GPU Render.
/// UI subscribes to events only; no UI types are referenced here.
/// </summary>
public sealed class ConnectionManager : IDisposable
{
    private readonly AirPadLogger _log;
    private readonly SettingsStore _settingsStore;
    private readonly object _gate = new();
    private bool _disposed;

    private MdnsAdvertiser? _mdns;
    private TcpControlServer? _controlServer;
    private DualModeServer? _dualServer;
    private AirPlayRequestHandler? _handler;
    private MirrorStreamHandler? _mirror;
    private AudioUdpServer? _audioServer;
    private AudioDecoder? _audioDecoder;
    private H264Decoder? _videoDecoder;
    private WasapiPlayer? _audioPlayer;
    private PairingService? _pairing;
    private FairPlayService? _fairPlay;

    private Session? _pendingMirrorSession;
    private bool _mirrorActive;
    private Session? _audioSession;
    private PipeVideoFallback? _pipeFallback;
    private int _lastDimW;
    private int _lastDimH;

    // Black-frame guard: some GPU/driver combos hand back valid-sized but
    // black D3D11VA frames. Sample the first frames of each mirror session;
    // if they are (near-)black, fall back to software decoding automatically.
    private long _brightnessSum;
    private int _brightnessSamples;
    private bool _brightnessFallbackDone;

    /// <summary>
    /// Single funnel for dimension changes (SPS headers and/or decoded
    /// frames — the latter is ground truth and also covers H.264 profiles
    /// whose SPS we cannot parse). Deduplicated.
    /// </summary>
    private void ReportDimensions(int w, int h, string source)
    {
        if (w <= 0 || h <= 0)
            return;
        lock (_gate)
        {
            if (w == _lastDimW && h == _lastDimH)
                return;
            _lastDimW = w;
            _lastDimH = h;
        }
        OrientationChanged?.Invoke(this, (w, h));
        _log.Info($"{(w >= h ? "Orientation: landscape" : "Orientation: portrait")} ({w}x{h}, via {source})");
    }
    private string _deviceName = "";
    private ReceiverState _state = ReceiverState.Ready;
    private long _secondWindowStart = DateTime.UtcNow.Ticks;
    private int _secondWindowCount;
    private long _lastStreamLogMs;

    public AirPadSettings Settings { get; private set; }
    public AirPadLogger Log => _log;
    public VideoStats VideoStats { get; } = new();
    public SystemStats SystemStats { get; } = new();

    public ReceiverState State
    {
        get { lock (_gate) return _state; }
        private set
        {
            bool changed;
            lock (_gate)
            {
                changed = _state != value;
                _state = value;
            }
            if (changed)
            {
                _log.Info($"State → {value}");
                StateChanged?.Invoke(this, value);
            }
        }
    }

    public string DeviceName
    {
        get { lock (_gate) return _deviceName; }
        private set
        {
            lock (_gate) _deviceName = value;
            DeviceNameChanged?.Invoke(this, value);
        }
    }

    public bool IsRunning => _controlServer?.IsRunning == true;

    public event EventHandler<ReceiverState>? StateChanged;
    public event EventHandler<string>? DeviceNameChanged;
    public event EventHandler<VideoFrame>? FrameReady;
    public event EventHandler? VideoCleared;
    public event EventHandler<(int Width, int Height)>? OrientationChanged;
    public event EventHandler? FullscreenRequested;

    public ConnectionManager(AirPadLogger log, SettingsStore settingsStore)
    {
        _log = log;
        _settingsStore = settingsStore;
        Settings = settingsStore.Load();
        ApplyLogLevel();
    }

    public void ReloadSettings()
    {
        Settings = _settingsStore.Load();
        ApplyLogLevel();
        if (_audioPlayer != null)
        {
            _audioPlayer.Volume = Settings.Volume;
            _audioPlayer.Muted = Settings.Muted;
        }
    }

    public void SaveSettings()
    {
        _settingsStore.Save(Settings);
        ApplyLogLevel();
    }

    private DateTime _lastSettingsSaveUtc = DateTime.MinValue;

    private void SaveSettingsThrottled()
    {
        // Volume sliders/keys fire many updates per second; don't rewrite the
        // JSON file on every tick.
        var now = DateTime.UtcNow;
        if ((now - _lastSettingsSaveUtc).TotalSeconds < 1)
            return;
        _lastSettingsSaveUtc = now;
        SaveSettings();
    }

    /// <summary>
    /// Applies a linear 0..1 volume immediately (player + settings + save).
    /// Used both by the UI slider and by device (dB-converted) updates.
    /// </summary>
    public void SetVolume(double linear)
    {
        Settings.Volume = Math.Clamp(linear, 0.0, 1.0);
        if (_audioPlayer != null)
            _audioPlayer.Volume = Settings.Volume;
        SaveSettingsThrottled();
    }

    public void SetMuted(bool muted)
    {
        Settings.Muted = muted;
        if (_audioPlayer != null)
            _audioPlayer.Muted = muted;
        SaveSettingsThrottled();
    }

    private void ApplyLogLevel()
    {
        _log.MinimumLevel = Settings.LogLevel switch
        {
            "Trace" => LogLevel.Trace,
            "Debug" => LogLevel.Debug,
            "Warning" => LogLevel.Warning,
            "Error" => LogLevel.Error,
            _ => LogLevel.Info,
        };
    }

    public async Task StartAsync()
    {
        if (IsRunning)
            return;

        _log.Info("Receiver started");
        var warning = NetworkInfo.GetNetworkWarning();
        if (warning != null)
            _log.Warn(warning);

        var primary = NetworkInfo.GetPrimaryIPv4();
        _log.Info(primary != null ? $"Primary LAN address: {primary}" : "No LAN address detected");

        FFmpegLoader.TryLoad(_log);

        _pairing = new PairingService(_log);
        _fairPlay = new FairPlayService(_log);
        _mirror = new MirrorStreamHandler(_log);
        _mirror.H264Received += OnH264Packet;
        _mirror.ResolutionChanged += (w, h) => ReportDimensions(w, h, "SPS");

        _handler = new AirPlayRequestHandler(_log, _pairing, _fairPlay,
            Settings.ReceiverName, Settings.DeviceId,
            Settings.AirPlayPort, Settings.AirTunesPort,
            Settings.AudioControlPort, Settings.AudioDataPort);
        _handler.DeviceNameSeen += name =>
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                DeviceName = name;
                _log.Info($"Device connected: {name}");
                if (State == ReceiverState.WaitingForDevice)
                    State = ReceiverState.Connected;
            }
        };
        _handler.VolumeChanged += volume =>
        {
            SetVolume(Math.Clamp((double)volume, 0.0, 1.0));
            if (Settings.Volume <= 0.0001)
                _log.Warn("Output muted — raise the iPad volume or the AirPad slider");
        };
        _handler.GetVolumeLinear = () => Settings.Volume;
        _handler.MirrorSessionReady += session =>
        {
            lock (_gate)
            {
                _pendingMirrorSession = session;
                _mirrorActive = false;
            }
            _log.Info("Mirror session negotiated; waiting for video stream");
            if (State == ReceiverState.WaitingForDevice)
                State = ReceiverState.Connected;
            return Task.CompletedTask;
        };
        _handler.AudioSessionReady += async session =>
        {
            await EnsureAudioAsync(session).ConfigureAwait(false);
        };
        _handler.AudioFlushRequested += _ => _audioServer?.Flush(-1);
        _handler.SessionTeardown += async (session, kind) =>
        {
            await OnTeardownAsync(session, kind).ConfigureAwait(false);
        };

        _controlServer = new TcpControlServer(Settings.AirTunesPort, _log, _handler.HandleAsync);
        await _controlServer.StartAsync().ConfigureAwait(false);

        _dualServer = new DualModeServer(Settings.AirPlayPort, _log, _handler.HandleAsync, _mirror,
            claimMirrorSession: ClaimMirrorSession,
            mirrorConnected: _ => OnMirrorConnected(),
            mirrorDisconnected: _ => OnMirrorDisconnected());
        await _dualServer.StartAsync().ConfigureAwait(false);

        _mdns = new MdnsAdvertiser(_log);
        await _mdns.StartAsync(Settings, _pairing.ServerPublicKey).ConfigureAwait(false);

        State = ReceiverState.WaitingForDevice;
        _log.Info("Waiting for device (open Screen Mirroring on the iPad)");
    }

    public async Task StopAsync()
    {
        _log.Info("Receiver stopping");
        try { _mdns?.Stop(); } catch { }
        _mdns?.Dispose();
        _mdns = null;

        if (_controlServer != null)
            await _controlServer.StopAsync().ConfigureAwait(false);
        if (_dualServer != null)
            await _dualServer.StopAsync().ConfigureAwait(false);
        _controlServer?.Dispose();
        _dualServer?.Dispose();
        _controlServer = null;
        _dualServer = null;

        if (_audioServer != null)
            await _audioServer.StopAsync().ConfigureAwait(false);
        _audioServer?.Dispose();
        _audioServer = null;

        _audioDecoder?.Dispose();
        _audioDecoder = null;
        _videoDecoder?.Dispose();
        _videoDecoder = null;
        _audioPlayer?.Dispose();
        _audioPlayer = null;
        _pipeFallback?.Dispose();
        _pipeFallback = null;

        lock (_gate)
        {
            _pendingMirrorSession = null;
            _mirrorActive = false;
            _audioSession = null;
            _lastDimW = 0;
            _lastDimH = 0;
        }
        VideoStats.Reset();
        DeviceName = "";
        State = ReceiverState.Ready;
        _log.Info("Receiver stopped");
    }

    /// <summary>
    /// Drops the current stream/session but keeps the receiver advertising.
    /// </summary>
    public Task DisconnectAsync()
    {
        _log.Info("Device disconnected (by user)");
        lock (_gate)
        {
            _pendingMirrorSession = null;
            _mirrorActive = false;
            _audioSession = null;
            _lastDimW = 0;
            _lastDimH = 0;
        }
        _videoDecoder?.Flush();
        _audioServer?.Flush(-1);
        try { _audioPlayer?.Clear(); } catch { }
        VideoStats.Reset();
        VideoCleared?.Invoke(this, EventArgs.Empty);
        DeviceName = "";
        if (IsRunning)
            State = ReceiverState.WaitingForDevice;
        // Resetting our state alone leaves the iPad streaming into a void: closing the sockets is the disconnect.
        _controlServer?.CloseAllClients();
        _dualServer?.CloseAllClients();
        return Task.CompletedTask;
    }

    private Session? ClaimMirrorSession()
    {
        lock (_gate)
        {
            if (_pendingMirrorSession != null && !_mirrorActive)
            {
                _mirrorActive = true;
                return _pendingMirrorSession;
            }
            return null;
        }
    }

    private void OnMirrorConnected()
    {
        EnsureVideoDecoder();
        _brightnessSum = 0;
        _brightnessSamples = 0;
        _brightnessFallbackDone = false;
        _log.Info("Device connected (mirror stream live)");
        State = ReceiverState.Streaming;
        if (Settings.FullscreenOnConnect)
            FullscreenRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnMirrorDisconnected()
    {
        _log.Info("Device disconnected (mirror stream closed)");
        lock (_gate) _mirrorActive = false;
        VideoCleared?.Invoke(this, EventArgs.Empty);
        // Only step down from Streaming: after Disconnect/TEARDOWN(all) the state is already WaitingForDevice.
        if (IsRunning && State == ReceiverState.Streaming)
            State = ReceiverState.Connected;
    }

    private async Task OnTeardownAsync(Session session, string kind)
    {
        if (kind is "mirror" or "all")
        {
            lock (_gate)
            {
                if (_pendingMirrorSession?.SessionId == session.SessionId)
                {
                    _pendingMirrorSession = null;
                    _mirrorActive = false;
                }
            }
            _videoDecoder?.Flush();
            VideoStats.Reset();
            VideoCleared?.Invoke(this, EventArgs.Empty);
        }
        if (kind is "audio" or "all")
        {
            _audioServer?.Flush(-1);
            try { _audioPlayer?.Clear(); } catch { }
        }
        // An audio-only teardown (the iPad does it whenever sound stops) leaves the mirror stream, and Streaming, alone.
        if (IsRunning && kind != "audio")
            State = kind == "all" ? ReceiverState.WaitingForDevice : ReceiverState.Connected;
        await Task.CompletedTask;
    }

    /// <summary>
    /// Samples decoded brightness. Returns true when the caller should drop
    /// the current batch (fallback to software decoding was just triggered).
    /// </summary>
    private bool CheckFrameBrightness(VideoFrame frame)
    {
        if (_brightnessFallbackDone || !frame.HardwareDecoded)
            return false;

        // Sparse sample of the green channel: ~2000 probes per 1080p frame (pooled buffers are longer than the picture).
        var pixels = frame.Pixels;
        int length = frame.Stride * frame.Height;
        long sum = 0;
        int n = 0;
        for (int i = 1; i < length; i += 4 * 997)
        {
            sum += pixels[i];
            n++;
        }
        double avg = n == 0 ? 0 : (double)sum / n;
        if (_brightnessSamples < 3)
            _log.Debug($"First video frame brightness avg={avg:0} ({frame.Width}x{frame.Height}, hw={frame.HardwareDecoded})");

        _brightnessSum += (long)(avg * n);
        _brightnessSamples += n;

        // Decide after ~30 hardware frames (~0.5 s of video).
        if (Volatile.Read(ref _videoDecoder) is { } decoder && VideoStats.FramesDecoded >= 30)
        {
            _brightnessFallbackDone = true;
            double totalAvg = _brightnessSamples == 0 ? 0 : (double)_brightnessSum / _brightnessSamples;
            if (totalAvg < 4)
            {
                _log.Warn($"GPU decoder outputs black frames (avg brightness {totalAvg:0.0}); falling back to software decoding");
                try { decoder.Dispose(); } catch { }
                if (decoder == Volatile.Read(ref _videoDecoder))
                    _videoDecoder = null;
                var sw = new H264Decoder(_log, preferHardware: false);
                if (sw.Open())
                {
                    _videoDecoder = sw;
                    SystemStats.SetGpuStatus("Software decode (auto-fallback)");
                    _log.Info("Software decoder active after GPU fallback");
                }
                else
                {
                    sw.Dispose();
                    _log.Error("Software fallback decoder failed to open");
                }
                return true;
            }
            _log.Debug($"GPU frame brightness OK (avg {totalAvg:0.0}); staying on hardware decode");
        }
        return false;
    }

    private void EnsureVideoDecoder()
    {
        if (_videoDecoder?.IsOpen == true)
            return;
        _videoDecoder?.Dispose();
        _videoDecoder = new H264Decoder(_log, Settings.HardwareAcceleration);
        if (_videoDecoder.Open())
        {
            SystemStats.SetGpuStatus(_videoDecoder.HardwareAvailable ? "D3D11VA ready" : "Software decode");
            _log.Info($"Streaming {Settings.MaxFps} FPS max (decoder {(_videoDecoder.HardwareAvailable ? "GPU" : "CPU")})");
        }
        else
        {
            _log.Error("Video decoder failed to open. Install FFmpeg shared DLLs next to AirPad.exe (see docs/BUILD.md) " +
                       "or use the ffplay named-pipe fallback: scripts/run-ffplay-pipe.ps1.");
            _videoDecoder?.Dispose();
            _videoDecoder = null;
        }
    }

    private void OnH264Packet(H264Packet packet)
    {
        var decoder = Volatile.Read(ref _videoDecoder);
        if (decoder == null || !decoder.IsOpen)
        {
            // Built-in decoder unavailable: offer the raw stream on the
            // named pipe for an external viewer (scripts/run-ffplay-pipe.ps1).
            _pipeFallback ??= new PipeVideoFallback(_log);
            _pipeFallback.Write(packet.Data);
            return;
        }

        // Max-FPS cap: every frame is still decoded (P-frames reference each other, so skipping one smears
        // the picture until the next IDR), only the GPU readback + conversion + render of surplus frames is skipped.
        bool render = true;
        if (Settings.MaxFps > 0)
        {
            var now = DateTime.UtcNow.Ticks;
            var secondStart = Volatile.Read(ref _secondWindowStart);
            if (now - secondStart >= TimeSpan.TicksPerSecond)
            {
                Volatile.Write(ref _secondWindowStart, now);
                _secondWindowCount = 0;
            }
            render = ++_secondWindowCount <= Settings.MaxFps;
        }

        List<VideoFrame> frames;
        try
        {
            frames = decoder.Feed(packet.Data, packet.ReceivedUtc, render);
        }
        catch (Exception ex)
        {
            _log.Warn($"Decode failed: {ex.Message}");
            VideoStats.OnFrameDropped();
            return;
        }

        if (frames.Count == 0)
        {
            VideoStats.OnFrameDropped();
            return;
        }

        foreach (var frame in frames)
        {
            VideoStats.OnFrameDecoded(frame);
            ReportDimensions(frame.Width, frame.Height, "decoded");
            if (CheckFrameBrightness(frame))
            {
                frame.Dispose();
                continue; // fell back to software mid-batch; drop black frames
            }
            SystemStats.SetGpuStatus(frame.HardwareDecoded ? "D3D11VA active" : "Software decode");
            try { FrameReady?.Invoke(this, frame); } catch { }
        }
        // Once a second: a line per frame floods the file, the UI log and the dispatcher at Debug level.
        if (Environment.TickCount64 - _lastStreamLogMs >= 1000)
        {
            _lastStreamLogMs = Environment.TickCount64;
            _log.Debug($"Streaming {VideoStats.Fps:0} FPS {VideoStats.Width}x{VideoStats.Height} (~{VideoStats.LatencyMs:0} ms)");
        }
    }

    private async Task EnsureAudioAsync(Session session)
    {
        if (!Settings.AudioEnabled)
        {
            _log.Info("Audio session offered but audio is disabled in Settings");
            return;
        }

        lock (_gate) _audioSession = session;

        if (session.DecryptedAesKey == null && session.KeyMsg != null && session.AesKey != null)
        {
            session.DecryptedAesKey = new FairPlayService(_log).DecryptAesKey(session.KeyMsg, session.AesKey);
            await SessionManager.Current.SaveAsync(session.SessionId, session).ConfigureAwait(false);
        }

        if (_audioDecoder == null || !_audioDecoder.IsOpen || _audioDecoder.Format != session.AudioFormat)
        {
            _audioDecoder?.Dispose();
            _audioDecoder = new Media.AudioDecoder(_log);
            if (!_audioDecoder.Open(session.AudioFormat))
            {
                _log.Warn("Audio decoder unavailable; continuing video-only");
                return;
            }
        }

        if (_audioServer == null)
        {
            _audioServer = new AudioUdpServer(_log, Settings.AudioControlPort, Settings.AudioDataPort);
            _audioServer.PcmReady += shim =>
            {
                try { _audioPlayer?.Enqueue(shim.Data); } catch { }
            };
            await _audioServer.StartAsync().ConfigureAwait(false);
        }
        _audioServer.AttachSession(session, _audioDecoder);

        _audioPlayer ??= new WasapiPlayer(_log);
        _audioPlayer.Volume = Settings.Volume;
        _audioPlayer.Muted = Settings.Muted;
        _audioPlayer.Start();
        _log.Info("Audio stream started (mirror audio → default output device)");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        // On the pool: StopAsync's inner awaits capture the caller's context, and a UI thread blocked here would deadlock.
        try { Task.Run(StopAsync).GetAwaiter().GetResult(); } catch { }
        SystemStats.Dispose();
    }
}
