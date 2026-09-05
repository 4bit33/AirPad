namespace AirPad.Core.Logging;

public enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warning = 3,
    Error = 4,
}

public sealed record LogEntry(DateTime Timestamp, LogLevel Level, string Message, Exception? Exception = null)
{
    public override string ToString()
    {
        var tag = Level switch
        {
            LogLevel.Trace => "TRACE",
            LogLevel.Debug => "DEBUG",
            LogLevel.Info => "INFO",
            LogLevel.Warning => "WARN",
            LogLevel.Error => "ERROR",
            _ => "INFO",
        };
        var baseMsg = $"[{tag}] {Message}";
        return Exception == null ? baseMsg : $"{baseMsg} :: {Exception.GetType().Name}: {Exception.Message}";
    }
}

/// <summary>
/// Minimal thread-safe logger: keeps an in-memory ring buffer for the UI,
/// mirrors to a rolling log file, and raises <see cref="EntryAdded"/> for live UI updates.
/// </summary>
public sealed class AirPadLogger : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<LogEntry> _buffer = new();
    private readonly StreamWriter? _file;
    private bool _disposed;

    public const int MaxBufferEntries = 2000;

    private static long _instanceSequence;

    public LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    /// <summary>Full path of this launch's log file (null when file logging is unavailable).</summary>
    public string? LogFilePath { get; }

    public event EventHandler<LogEntry>? EntryAdded;

    public AirPadLogger(string? logDirectory = null, LogLevel minimumLevel = LogLevel.Info)
    {
        MinimumLevel = minimumLevel;
        try
        {
            var dir = logDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AirPad", "logs");
            Directory.CreateDirectory(dir);
            // A fresh file per app launch: airpad-20260905-164022-p1234-n1.log
            // (PID + sequence guard against two launches within the same second.)
            int pid;
            try { pid = Environment.ProcessId; } catch { pid = 0; }
            long seq = Interlocked.Increment(ref _instanceSequence);
            var path = Path.Combine(dir, $"airpad-{DateTime.Now:yyyyMMdd-HHmmss}-p{pid}-n{seq}.log");
            LogFilePath = path;
            _file = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            {
                AutoFlush = true,
            };
            PruneOldLogs(dir);
        }
        catch
        {
            _file = null;
        }
    }

    /// <summary>Keeps the log folder bounded: newest 30 files survive.</summary>
    private static void PruneOldLogs(string dir)
    {
        try
        {
            var files = new DirectoryInfo(dir).GetFiles("airpad-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(30)
                .ToList();
            foreach (var f in files)
            {
                try { f.Delete(); } catch { }
            }
        }
        catch { }
    }

    public void Log(LogLevel level, string message, Exception? ex = null)
    {
        if (level < MinimumLevel)
            return;
        var entry = new LogEntry(DateTime.Now, level, message, ex);
        lock (_gate)
        {
            if (_disposed)
                return;
            _buffer.Enqueue(entry);
            while (_buffer.Count > MaxBufferEntries)
                _buffer.Dequeue();
            try { _file?.WriteLine($"{entry.Timestamp:HH:mm:ss.fff} {entry}"); } catch { }
        }
        try { EntryAdded?.Invoke(this, entry); } catch { }
    }

    public void Trace(string m) => Log(LogLevel.Trace, m);
    public void Debug(string m) => Log(LogLevel.Debug, m);
    public void Info(string m) => Log(LogLevel.Info, m);
    public void Warn(string m, Exception? ex = null) => Log(LogLevel.Warning, m, ex);
    public void Error(string m, Exception? ex = null) => Log(LogLevel.Error, m, ex);

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_gate)
            return _buffer.ToList();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            try { _file?.Dispose(); } catch { }
        }
    }
}
