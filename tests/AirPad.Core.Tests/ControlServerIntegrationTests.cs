using System.Net.Sockets;
using System.Text;
using AirPad.Core.Logging;
using AirPad.Core.Models;
using AirPad.Core.Network;

namespace AirPad.Core.Tests;

/// <summary>
/// End-to-end framing tests against a real loopback control server:
/// persistent connection, pipelining, and Content-Length bodies.
/// </summary>
public sealed class ControlServerIntegrationTests : IAsyncLifetime
{
    private TcpControlServer? _server;
    private int _port;

    public async Task InitializeAsync()
    {
        _port = FindFreePort();
        _server = new TcpControlServer(_port, new AirPadLogger(minimumLevel: LogLevel.Error), HandleAsync);
        await _server.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_server != null)
            await _server.StopAsync();
        _server?.Dispose();
    }

    private static Task HandleAsync(Request request, Response response, CancellationToken ct)
    {
        if (request.Type == AirPad.Core.Models.Enums.RequestType.OPTIONS)
            response.Headers.Add("Public", "SETUP, RECORD, OPTIONS");
        if (request.Body is { Length: > 0 })
            response.Headers.Add("X-Echo-Length", request.Body.Length.ToString());
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Options_Returns200WithPublicHeader()
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", _port);
        using var stream = client.GetStream();

        var req = "OPTIONS * RTSP/1.0\r\nCSeq: 1\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(req));
        var response = await ReadResponseAsync(stream);

        Assert.Contains("200", response, StringComparison.Ordinal);
        Assert.Contains("CSeq: 1", response, StringComparison.Ordinal);
        Assert.Contains("Public:", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PipelinedRequests_GetSeparateResponses()
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", _port);
        using var stream = client.GetStream();

        var body = new byte[] { 1, 2, 3, 4, 5 };
        var r1 = "OPTIONS * RTSP/1.0\r\nCSeq: 10\r\n\r\n";
        var r2head = $"SETUP rtsp://127.0.0.1/1 RTSP/1.0\r\nCSeq: 11\r\nContent-Length: {body.Length}\r\n\r\n";
        var payload = Encoding.ASCII.GetBytes(r1)
            .Concat(Encoding.ASCII.GetBytes(r2head))
            .Concat(body).ToArray();
        await stream.WriteAsync(payload);

        var first = await ReadResponseAsync(stream);
        var second = await ReadResponseAsync(stream);
        Assert.Contains("CSeq: 10", first, StringComparison.Ordinal);
        Assert.Contains("CSeq: 11", second, StringComparison.Ordinal);
        Assert.Contains("X-Echo-Length: 5", second, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonLocalConnections_AreRejected()
    {
        // Loopback is always accepted; this test documents the filter contract
        // via the public helper instead of spoofing packets.
        Assert.True(TcpControlServer.IsLocalNetworkAddress(System.Net.IPAddress.Loopback));
        Assert.True(TcpControlServer.IsLocalNetworkAddress(System.Net.IPAddress.Parse("192.168.0.5")));
        Assert.False(TcpControlServer.IsLocalNetworkAddress(System.Net.IPAddress.Parse("8.8.8.8")));
        await Task.CompletedTask;
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<string> ReadResponseAsync(NetworkStream stream)
    {
        var buffer = new List<byte>();
        var tmp = new byte[4096];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        int headerEnd = -1;
        int contentLength = 0;
        while (true)
        {
            int read = await stream.ReadAsync(tmp.AsMemory(0, tmp.Length), cts.Token);
            if (read <= 0)
                break;
            buffer.AddRange(tmp.Take(read));

            if (headerEnd < 0)
            {
                for (int i = 0; i + 3 < buffer.Count; i++)
                {
                    if (buffer[i] == (byte)'\r' && buffer[i + 1] == (byte)'\n' && buffer[i + 2] == (byte)'\r' && buffer[i + 3] == (byte)'\n')
                    {
                        headerEnd = i + 4;
                        var head = Encoding.ASCII.GetString(buffer.Take(headerEnd).ToArray());
                        foreach (var line in head.Split(["\r\n"], StringSplitOptions.RemoveEmptyEntries).Skip(1))
                        {
                            int colon = line.IndexOf(':');
                            if (colon > 0 && line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
                                int.TryParse(line[(colon + 1)..].Trim(), out var len))
                                contentLength = len;
                        }
                        break;
                    }
                }
            }

            if (headerEnd >= 0 && buffer.Count >= headerEnd + contentLength)
                break;
        }

        return Encoding.ASCII.GetString(buffer.ToArray());
    }
}
