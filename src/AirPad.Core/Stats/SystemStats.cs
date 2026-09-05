using System.Diagnostics;

namespace AirPad.Core.Stats;

/// <summary>
/// Lightweight resource monitor: process CPU %, working set, and
/// decoder-mode GPU hint. Sampled on a timer; UI binds to
/// <see cref="Snapshot"/> values.
/// </summary>
public sealed class SystemStats : IDisposable
{
    private readonly Timer _timer;
    private TimeSpan _lastCpuTime;
    private DateTime _lastSample = DateTime.UtcNow;
    private bool _disposed;

    public double CpuPercent { get; private set; }
    public double MemoryMb { get; private set; }
    public string GpuStatus { get; private set; } = "Idle";

    public event EventHandler? Updated;

    public SystemStats()
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            _lastCpuTime = p.TotalProcessorTime;
        }
        catch { }
        _timer = new Timer(_ => Sample(), null, 1000, 1000);
    }

    public void SetGpuStatus(string status) => GpuStatus = status;

    private void Sample()
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            var now = DateTime.UtcNow;
            var cpu = p.TotalProcessorTime;
            double elapsedMs = (now - _lastSample).TotalMilliseconds;
            if (elapsedMs > 0)
            {
                double usedMs = (cpu - _lastCpuTime).TotalMilliseconds;
                CpuPercent = Math.Clamp(usedMs / elapsedMs / Environment.ProcessorCount * 100.0, 0, 100);
            }
            MemoryMb = p.WorkingSet64 / (1024.0 * 1024.0);
            _lastCpuTime = cpu;
            _lastSample = now;
        }
        catch { }
        try { Updated?.Invoke(this, EventArgs.Empty); } catch { }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try { _timer.Dispose(); } catch { }
    }
}
