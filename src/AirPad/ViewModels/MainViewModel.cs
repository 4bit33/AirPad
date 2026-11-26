using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AirPad.Core.Audio;
using AirPad.Core.Connection;
using AirPad.Core.Discovery;
using AirPad.Core.Logging;
using AirPad.Core.Video;
using AirPad.Helpers;

namespace AirPad.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly AirPadLogger _log;
    private readonly ConnectionManager _manager;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _statsTimer;
    private bool _disposed;

    private WriteableBitmap? _videoBitmap;
    private VideoFrame? _pendingFrame;
    private int _lastVideoW;
    private int _lastVideoH;
    private string _orientation = "—";
    private DispatcherTimer? _testPatternTimer;
    private int _testPatternFrame;
    private bool _testPatternActive;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? FullscreenRequested;

    public ObservableCollection<string> LogLines { get; } = new();

    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand ToggleFullscreenCommand { get; }
    public ICommand ToggleChromeCommand { get; }
    public ICommand ToggleMuteCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand TestPatternCommand { get; }

    public MainViewModel(AirPadLogger log, ConnectionManager manager)
    {
        _log = log;
        _manager = manager;
        _dispatcher = Application.Current.Dispatcher;

        foreach (var entry in _log.Snapshot().TakeLast(200))
            AppendLog(entry);
        _log.EntryAdded += OnLogEntry;

        _manager.StateChanged += (_, _) => RefreshState();
        _manager.DeviceNameChanged += (_, _) => OnPropertyChanged(nameof(DeviceText));
        _manager.FrameReady += OnFrameReady;
        _manager.VideoCleared += (_, _) => _ = _dispatcher.BeginInvoke(new Action(ClearVideo));
        _manager.OrientationChanged += (_, wh) => _ = _dispatcher.BeginInvoke(() =>
        {
            _orientation = wh.Width >= wh.Height ? "Landscape" : "Portrait";
            OnPropertyChanged(nameof(OrientationText));
            OnPropertyChanged(nameof(ResolutionText));
        });
        _manager.FullscreenRequested += (_, _) => FullscreenRequested?.Invoke(this, EventArgs.Empty);
        _manager.VideoStats.Changed += (_, _) => { };
        _manager.SystemStats.Updated += (_, _) => _ = _dispatcher.BeginInvoke(RefreshStats);

        StartCommand = new AsyncRelayCommand(StartAsync, () => !_manager.IsRunning);
        StopCommand = new AsyncRelayCommand(StopAsync, () => _manager.IsRunning);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, () => _manager.IsRunning);
        ToggleFullscreenCommand = new RelayCommand(() => FullscreenToggleRequested?.Invoke(this, EventArgs.Empty));
        ToggleChromeCommand = new RelayCommand(() => ChromeToggleRequested?.Invoke(this, EventArgs.Empty));
        ToggleMuteCommand = new RelayCommand(() =>
        {
            _manager.SetMuted(!_manager.Settings.Muted);
            OnPropertyChanged(nameof(Muted));
            OnPropertyChanged(nameof(MuteText));
        });
        OpenSettingsCommand = new RelayCommand(() => SettingsRequested?.Invoke(this, EventArgs.Empty));
        TestPatternCommand = new RelayCommand(ToggleTestPattern);

        _statsTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => RefreshStats(), _dispatcher);
        _statsTimer.Start();

        RefreshState();
        RefreshStats();
        IpText = NetworkInfo.GetPrimaryIPv4()?.ToString() ?? "—";
        NetworkWarning = NetworkInfo.GetNetworkWarning() ?? "";
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e) =>
        _ = Task.Run(() =>
        {
            // Enumerating adapters takes ~15 ms: never on the UI thread that also paints the video.
            var ip = NetworkInfo.GetPrimaryIPv4()?.ToString() ?? "—";
            var warning = NetworkInfo.GetNetworkWarning() ?? "";
            _dispatcher.BeginInvoke(() =>
            {
                IpText = ip;
                NetworkWarning = warning;
                OnPropertyChanged(nameof(IpText));
                OnPropertyChanged(nameof(NetworkWarning));
                OnPropertyChanged(nameof(HasNetworkWarning));
            });
        });

    public event EventHandler? FullscreenToggleRequested;
    public event EventHandler? ChromeToggleRequested;
    public event EventHandler? SettingsRequested;

    public string StateText => _manager.State switch
    {
        ReceiverState.Ready => "Ready",
        ReceiverState.WaitingForDevice => "Waiting for device",
        ReceiverState.Connected => "Connected",
        ReceiverState.Streaming => "Streaming",
        _ => _manager.State.ToString(),
    };

    public Brush StateBrush => _manager.State switch
    {
        ReceiverState.Streaming => new SolidColorBrush((Color)Application.Current.Resources["GreenColor"]),
        ReceiverState.Connected => new SolidColorBrush((Color)Application.Current.Resources["AccentColor"]),
        ReceiverState.WaitingForDevice => new SolidColorBrush((Color)Application.Current.Resources["AmberColor"]),
        _ => new SolidColorBrush((Color)Application.Current.Resources["DimColor"]),
    };

    public string ReceiverNameText => _manager.Settings.ReceiverName;
    public string IpText { get; private set; } = "—";
    public string NetworkWarning { get; private set; } = "";
    public bool HasNetworkWarning => !string.IsNullOrEmpty(NetworkWarning);
    public string DeviceText => string.IsNullOrEmpty(_manager.DeviceName) ? "No device" : _manager.DeviceName;
    public bool IsStreaming => _manager.State == ReceiverState.Streaming;
    public bool IsRunning => _manager.IsRunning;

    public string FpsText { get; private set; } = "—";
    public string ResolutionText { get; private set; } = "—";
    public string LatencyText { get; private set; } = "—";
    public string CpuText { get; private set; } = "—";
    public string GpuText { get; private set; } = "Idle";
    public string OrientationText => _orientation;

    public bool Muted => _manager.Settings.Muted;
    public string MuteText => Muted ? "Unmute" : "Mute";

    public double Volume
    {
        get => _manager.Settings.Volume;
        set
        {
            _manager.SetVolume(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(VolumeDb));
        }
    }

    /// <summary>
    /// Slider position in dB (-30..0), mirroring the iPad's own volume steps
    /// so both move in sync. Linear 0..1 would bunch all travel at one end.
    /// </summary>
    public double VolumeDb
    {
        get => Math.Clamp(VolumeUtils.LinearToDb(_manager.Settings.Volume),
            VolumeUtils.SliderMinDb, 0.0);
        set
        {
            _manager.SetVolume(VolumeUtils.DbToLinear(Math.Clamp(value, VolumeUtils.SliderMinDb, 0.0)));
            OnPropertyChanged();
            OnPropertyChanged(nameof(Volume));
        }
    }

    public WriteableBitmap? VideoBitmap
    {
        get => _videoBitmap;
        private set
        {
            _videoBitmap = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(VideoVisible));
        }
    }

    public bool VideoVisible => VideoBitmap != null && (IsStreaming || _testPatternActive);
    public Stretch VideoStretch => _manager.Settings.VideoScaling switch
    {
        "Fill" => Stretch.UniformToFill,
        "Stretch" => Stretch.Fill,
        _ => Stretch.Uniform,
    };

    private void OnLogEntry(object? sender, LogEntry entry) =>
        _ = _dispatcher.BeginInvoke(() => AppendLog(entry));

    public string TestPatternText => _testPatternActive ? "Stop test" : "Test pattern";

    /// <summary>
    /// Renders synthetic color bars through the exact production path
    /// (VideoFrame → dispatcher → WriteableBitmap → Image). If bars show but
    /// the iPad doesn't, the problem is in the stream/decoder, not the UI.
    /// </summary>
    private void ToggleTestPattern()
    {
        if (_testPatternActive)
        {
            _testPatternActive = false;
            if (_testPatternTimer != null)
            {
                _testPatternTimer.Stop();
                _testPatternTimer = null;
            }
            ClearVideo();
        }
        else
        {
            _testPatternActive = true;
            _testPatternFrame = 0;
            _testPatternTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render,
                (_, _) => RenderFrame(MakeTestFrame()), _dispatcher);
            _testPatternTimer.Start();
        }
        OnPropertyChanged(nameof(TestPatternText));
        OnPropertyChanged(nameof(VideoVisible));
    }

    private VideoFrame MakeTestFrame()
    {
        const int width = 640;
        const int height = 480;
        int stride = width * 4;
        var pixels = new byte[stride * height];
        int shift = _testPatternFrame++;
        byte[,] bars = { { 192, 0, 0 }, { 192, 192, 0 }, { 0, 192, 0 }, { 0, 192, 192 }, { 0, 0, 192 }, { 192, 0, 192 } };
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int bar = ((x / 80) + shift / 10) % 8;
                int o = y * stride + x * 4;
                if (bar < 6)
                {
                    pixels[o] = bars[bar, 0];
                    pixels[o + 1] = bars[bar, 1];
                    pixels[o + 2] = bars[bar, 2];
                }
                else
                {
                    byte v = (byte)((x + shift) % 256);
                    pixels[o] = v;
                    pixels[o + 1] = v;
                    pixels[o + 2] = v;
                }
                pixels[o + 3] = 255;
            }
        }
        return new VideoFrame
        {
            Width = width,
            Height = height,
            Stride = stride,
            Pixels = pixels,
            NetworkReceivedUtc = DateTime.UtcNow,
            DecodedUtc = DateTime.UtcNow,
        };
    }

    private void AppendLog(LogEntry entry)
    {
        LogLines.Add($"{entry.Timestamp:HH:mm:ss} {entry}");
        while (LogLines.Count > 300)
            LogLines.RemoveAt(0);
    }

    private void RefreshState()
    {
        _ = _dispatcher.BeginInvoke(() =>
        {
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(StateBrush));
            OnPropertyChanged(nameof(DeviceText));
            OnPropertyChanged(nameof(IsStreaming));
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(VideoVisible));
            OnPropertyChanged(nameof(ReceiverNameText));
            CommandManager.InvalidateRequerySuggested();
        });
    }

    private void RefreshStats()
    {
        var stats = _manager.VideoStats;
        var sys = _manager.SystemStats;
        FpsText = stats.FramesDecoded == 0 && !IsStreaming ? "—" : $"{stats.Fps:0} FPS";
        ResolutionText = stats.Width > 0 ? $"{stats.Width}×{stats.Height}" : "—";
        LatencyText = stats.FramesDecoded == 0 ? "—" : $"~{stats.LatencyMs:0} ms";
        CpuText = $"CPU {sys.CpuPercent:0}% · {sys.MemoryMb:0} MB";
        GpuText = stats.FramesDecoded == 0 && !IsStreaming ? "Idle" : sys.GpuStatus + (stats.HardwareDecoding ? "" : " (CPU decode)");
        OnPropertyChanged(nameof(FpsText));
        OnPropertyChanged(nameof(ResolutionText));
        OnPropertyChanged(nameof(LatencyText));
        OnPropertyChanged(nameof(CpuText));
        OnPropertyChanged(nameof(GpuText));
        // Device-driven changes (volume/mute from the iPad) must reach the slider.
        OnPropertyChanged(nameof(Volume));
        OnPropertyChanged(nameof(VolumeDb));
        OnPropertyChanged(nameof(Muted));
        OnPropertyChanged(nameof(MuteText));
    }

    private async Task StartAsync()
    {
        try
        {
            _manager.ReloadSettings();
            await _manager.StartAsync();
            OnPropertyChanged(nameof(VideoStretch));
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to start receiver: {ex.Message}", ex);
            MessageBox.Show($"Failed to start receiver:\n{ex.Message}", "AirPad",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        RefreshState();
    }

    private async Task StopAsync()
    {
        try { await _manager.StopAsync(); } catch { }
        _ = _dispatcher.BeginInvoke(new Action(ClearVideo));
        RefreshState();
    }

    private async Task DisconnectAsync()
    {
        try { await _manager.DisconnectAsync(); } catch { }
        _ = _dispatcher.BeginInvoke(new Action(ClearVideo));
        RefreshState();
    }

    private void OnFrameReady(object? sender, VideoFrame frame)
    {
        // Coalesce: never queue more than one pending render (lowest latency), and the newest frame wins.
        // (Keeping the first one would leave a stale picture on screen once the source goes idle.)
        var superseded = Interlocked.Exchange(ref _pendingFrame, frame);
        if (superseded != null)
        {
            superseded.Dispose();
            return; // a render is already scheduled and will pick up `frame`
        }
        _ = _dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            var latest = Interlocked.Exchange(ref _pendingFrame, null);
            if (latest == null)
                return;
            try { RenderFrame(latest); }
            finally { latest.Dispose(); }
        }));
    }

    private void RenderFrame(VideoFrame frame)
    {
        try
        {
            if (VideoBitmap == null || _lastVideoW != frame.Width || _lastVideoH != frame.Height)
            {
                VideoBitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr32, null);
                _lastVideoW = frame.Width;
                _lastVideoH = frame.Height;
                _log.Info($"Video rendering at {frame.Width}x{frame.Height} (hw={frame.HardwareDecoded})");
            }
            var rect = new Int32Rect(0, 0, frame.Width, frame.Height);
            VideoBitmap.Lock();
            try
            {
                if (frame.Stride == VideoBitmap.BackBufferStride)
                {
                    System.Runtime.InteropServices.Marshal.Copy(frame.Pixels, 0, VideoBitmap.BackBuffer, frame.Stride * frame.Height);
                }
                else
                {
                    // Stride mismatch: copy row by row.
                    for (int y = 0; y < frame.Height; y++)
                    {
                        System.Runtime.InteropServices.Marshal.Copy(frame.Pixels, y * frame.Stride,
                            VideoBitmap.BackBuffer + y * VideoBitmap.BackBufferStride, frame.Width * 4);
                    }
                }
                VideoBitmap.AddDirtyRect(rect);
            }
            finally
            {
                VideoBitmap.Unlock();
            }
            if (!VideoVisible)
            {
                OnPropertyChanged(nameof(VideoVisible));
                RefreshStats();
            }
        }
        catch { }
    }

    private void ClearVideo()
    {
        VideoBitmap = null;
        _lastVideoW = 0;
        _lastVideoH = 0;
        _orientation = "—";
        OnPropertyChanged(nameof(OrientationText));
        OnPropertyChanged(nameof(ResolutionText));
    }

    public void OnSettingsSaved()
    {
        OnPropertyChanged(nameof(ReceiverNameText));
        OnPropertyChanged(nameof(VideoStretch));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _statsTimer.Stop();
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        _log.EntryAdded -= OnLogEntry;
    }
}
