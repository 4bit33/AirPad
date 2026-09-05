using AirPad.Core.Logging;
using NAudio.Wave;

namespace AirPad.Core.Audio;

/// <summary>
/// Plays decoded PCM (16-bit stereo 44100 Hz) through the default Windows
/// audio device (WASAPI shared). Small jitter buffer keeps latency low;
/// overflow drops oldest data instead of growing delay.
/// </summary>
public sealed class WasapiPlayer : IDisposable
{
    private readonly AirPadLogger _log;
    private readonly object _gate = new();
    private WasapiOut? _output;
    private BufferedWaveProvider? _provider;
    private bool _disposed;

    private double _volume = 1.0;
    private bool _muted;
    private long _firstSamplesLogged;

    public bool IsRunning => _output?.PlaybackState == PlaybackState.Playing;

    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0.0, 1.0);
            ApplyVolume();
        }
    }

    public bool Muted
    {
        get => _muted;
        set
        {
            _muted = value;
            ApplyVolume();
        }
    }

    public WasapiPlayer(AirPadLogger log)
    {
        _log = log;
    }

    public bool Start()
    {
        lock (_gate)
        {
            if (_disposed || _output != null)
                return _output != null;
            try
            {
                var format = new WaveFormat(44100, 16, 2);
                _provider = new BufferedWaveProvider(format)
                {
                    BufferDuration = TimeSpan.FromMilliseconds(400),
                    DiscardOnBufferOverflow = true,
                    ReadFully = false,
                };
                _output = new WasapiOut();
                _output.Init(_provider);
                ApplyVolume();
                _output.Play();
                _log.Info("Audio output started (WASAPI, 44.1 kHz stereo)");
                return true;
            }
            catch (Exception ex)
            {
                _log.Error("Audio output failed to start; mirroring continues muted", ex);
                try { _output?.Dispose(); } catch { }
                _output = null;
                _provider = null;
                return false;
            }
        }
    }

    public void Enqueue(byte[] pcm)
    {
        var provider = Volatile.Read(ref _provider);
        if (provider == null || pcm == null || pcm.Length == 0)
            return;
        try
        {
            provider.AddSamples(pcm, 0, pcm.Length);
            if (Interlocked.Increment(ref _firstSamplesLogged) == 1)
                _log.Info("Audio output: first PCM samples queued to speakers");
        }
        catch { }
    }

    public void Clear()
    {
        try { Volatile.Read(ref _provider)?.ClearBuffer(); } catch { }
    }

    public void Stop()
    {
        lock (_gate)
        {
            try { _output?.Stop(); } catch { }
            try { _output?.Dispose(); } catch { }
            _output = null;
            _provider = null;
            _log.Info("Audio output stopped");
        }
    }

    private void ApplyVolume()
    {
        try
        {
            if (_output != null)
                _output.Volume = _muted ? 0f : (float)_volume;
        }
        catch { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            try { _output?.Dispose(); } catch { }
            _output = null;
            _provider = null;
        }
    }
}
