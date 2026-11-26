using AirPad.Core.Config;
using AirPad.Core.Connection;
using AirPad.Core.Logging;

namespace AirPad.Core.Tests;

/// <summary>
/// Boots the full receiver (TCP control + dual-mode + mDNS) on
/// non-default ports and shuts it down cleanly.
/// </summary>
public sealed class ReceiverLifecycleTests : IAsyncLifetime
{
    private ConnectionManager? _manager;
    private string? _settingsPath;

    public Task InitializeAsync()
    {
        _settingsPath = Path.Combine(Path.GetTempPath(), $"airpad-lifecycle-{Guid.NewGuid():N}.json");
        var store = new SettingsStore(_settingsPath);
        store.Save(new AirPadSettings
        {
            ReceiverName = "AirPad-Test-Lifecycle",
            AirPlayPort = 17900,
            AirTunesPort = 15900,
            AudioControlPort = 17902,
            AudioDataPort = 17903,
            LogLevel = "Error",
        });

        _manager = new ConnectionManager(new AirPadLogger(minimumLevel: LogLevel.Error), store);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_manager != null)
        {
            try { await _manager.StopAsync(); } catch { }
            _manager.Dispose();
        }
        try { if (_settingsPath != null) File.Delete(_settingsPath); } catch { }
    }

    [Fact]
    public async Task StartStop_CyclesStateMachine()
    {
        var manager = _manager!;
        Assert.Equal(ReceiverState.Ready, manager.State);

        await manager.StartAsync();
        Assert.True(manager.IsRunning);
        Assert.Equal(ReceiverState.WaitingForDevice, manager.State);

        // Second start is a no-op.
        await manager.StartAsync();
        Assert.True(manager.IsRunning);

        await manager.DisconnectAsync();
        Assert.Equal(ReceiverState.WaitingForDevice, manager.State);

        await manager.StopAsync();
        Assert.False(manager.IsRunning);
        Assert.Equal(ReceiverState.Ready, manager.State);
    }

    [Fact]
    public async Task Start_WhenAirPlayPortTaken_FailsWithoutLeavingAHalfStartedReceiver()
    {
        var manager = _manager!;
        var squatter = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, 17900);
        squatter.Start();
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(manager.StartAsync);
            Assert.False(manager.IsRunning);
            Assert.Equal(ReceiverState.Ready, manager.State);
        }
        finally
        {
            squatter.Stop();
        }

        await manager.StartAsync(); // the control port was released, so a retry works
        Assert.True(manager.IsRunning);
    }

    [Fact]
    public async Task ControlPort_AcceptsRtspOptions()
    {
        var manager = _manager!;
        await manager.StartAsync();

        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync("127.0.0.1", 15900);
        using var stream = client.GetStream();
        var req = System.Text.Encoding.ASCII.GetBytes("OPTIONS * RTSP/1.0\r\nCSeq: 1\r\n\r\n");
        await stream.WriteAsync(req);

        var tmp = new byte[4096];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        int read = await stream.ReadAsync(tmp.AsMemory(0, tmp.Length), cts.Token);
        var response = System.Text.Encoding.ASCII.GetString(tmp, 0, read);
        Assert.Contains("200", response, StringComparison.Ordinal);
        Assert.Contains("SETUP", response, StringComparison.Ordinal);
    }
}
