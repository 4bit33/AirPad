using System.Buffers;

namespace AirPad.Core.Video;

/// <summary>
/// A decoded video frame ready for rendering (BGRA pixels = WPF <c>Bgr32</c>, 4 bytes per pixel).
/// Pixel buffers come from <see cref="ArrayPool{T}"/>: at 60 fps a fresh multi-MB array per frame means
/// a gen-2 GC every few frames. Whoever ends up with a frame should <see cref="Dispose"/> it;
/// forgetting is harmless (the GC just collects the buffer).
/// </summary>
public sealed class VideoFrame : IDisposable
{
    private int _disposed;

    public int Width { get; init; }
    public int Height { get; init; }
    public int Stride { get; init; }

    /// <summary>Pixel rows of <see cref="Stride"/> bytes; when <see cref="Pooled"/> the array may be longer than Stride × Height.</summary>
    public byte[] Pixels { get; init; } = Array.Empty<byte>();

    /// <summary>True when <see cref="Pixels"/> was rented from the shared array pool.</summary>
    public bool Pooled { get; init; }

    /// <summary>UTC time when the network packet arrived (for latency estimation).</summary>
    public DateTime NetworkReceivedUtc { get; init; } = DateTime.UtcNow;

    /// <summary>UTC time when decoding finished.</summary>
    public DateTime DecodedUtc { get; init; } = DateTime.UtcNow;

    /// <summary>True when decoded by the GPU (D3D11VA) path.</summary>
    public bool HardwareDecoded { get; init; }

    public void Dispose()
    {
        if (Pooled && Interlocked.Exchange(ref _disposed, 1) == 0)
            ArrayPool<byte>.Shared.Return(Pixels);
    }
}

/// <summary>
/// Rolling statistics for the streaming pipeline:
/// FPS, resolution, end-to-end latency estimate, counters.
/// </summary>
public sealed class VideoStats
{
    private readonly object _gate = new();
    private readonly Queue<DateTime> _frameTimes = new();
    private double _latencyMsEma;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public double Fps { get; private set; }
    public double LatencyMs => _latencyMsEma;
    public long FramesDecoded { get; private set; }
    public long FramesDropped { get; private set; }
    public bool HardwareDecoding { get; private set; }

    public event EventHandler? Changed;

    public void OnFrameDecoded(VideoFrame frame)
    {
        lock (_gate)
        {
            FramesDecoded++;
            Width = frame.Width;
            Height = frame.Height;
            HardwareDecoding = frame.HardwareDecoded;

            var now = DateTime.UtcNow;
            _frameTimes.Enqueue(now);
            while (_frameTimes.Count > 0 && (now - _frameTimes.Peek()).TotalSeconds > 1.0)
                _frameTimes.Dequeue();
            Fps = _frameTimes.Count;

            var sample = (now - frame.NetworkReceivedUtc).TotalMilliseconds;
            if (sample is >= 0 and < 5000)
                _latencyMsEma = _latencyMsEma == 0 ? sample : (_latencyMsEma * 0.85 + sample * 0.15);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void OnFrameDropped()
    {
        lock (_gate)
            FramesDropped++;
    }

    public void Reset()
    {
        lock (_gate)
        {
            _frameTimes.Clear();
            Fps = 0;
            _latencyMsEma = 0;
            FramesDecoded = 0;
            FramesDropped = 0;
            Width = 0;
            Height = 0;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
