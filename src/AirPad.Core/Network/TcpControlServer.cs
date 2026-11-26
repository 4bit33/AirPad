using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AirPad.Core.Logging;
using AirPad.Core.Models;
using AirPad.Core.Models.Enums;

namespace AirPad.Core.Network;

/// <summary>
/// Async TCP server for the AirPlay control plane: a single port serves
/// both HTTP and RTSP requests ("XTXP" style, persistent keep-alive
/// connections, pipelining-safe via Content-Length framing).
/// Only accepts connections from the local network.
/// </summary>
public class TcpControlServer : IDisposable
{
    private static readonly string[] Verbs =
    {
        "GET ", "POST ", "SETUP ", "RECORD ", "PAUSE ", "FLUSH ",
        "TEARDOWN ", "OPTIONS ", "ANNOUNCE ", "SET_PARAMETER ", "GET_PARAMETER ",
    };

    private readonly int _port;
    private readonly AirPadLogger _log;
    private readonly Func<Request, Response, CancellationToken, Task> _onRequest;
    private readonly ConcurrentDictionary<string, Task> _clients = new();
    private readonly ConcurrentDictionary<TcpClient, byte> _open = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public int Port => _port;
    public bool IsRunning => _listener != null && _cts != null && !_cts.IsCancellationRequested;

    public TcpControlServer(int port, AirPadLogger log, Func<Request, Response, CancellationToken, Task> onRequest)
    {
        _port = port;
        _log = log;
        _onRequest = onRequest;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning)
            return Task.CompletedTask;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();
        _log.Info($"TCP server listening on port {_port}");
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;

        try
        {
            await Task.WhenAll(_clients.Values).WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch { }
        _clients.Clear();
    }

    /// <summary>Hangs up every live connection: the iPad sees the socket close and stops mirroring.</summary>
    public void CloseAllClients()
    {
        foreach (var client in _open.Keys)
        {
            try { client.Close(); } catch { }
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.Warn($"Accept failed on port {_port}: {ex.Message}");
                await Task.Delay(250, ct).ContinueWith(_ => { });
                continue;
            }

            string? remote = null;
            try
            {
                remote = client.Client.RemoteEndPoint?.ToString();
                var ip = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
                if (ip != null && !IsLocalNetworkAddress(ip))
                {
                    _log.Warn($"Rejected non-local connection from {remote}");
                    client.Close();
                    continue;
                }
                _log.Info($"Device connected from {remote}");
            }
            catch { }

            TuneSocket(client);
            _open[client] = 0;
            var id = remote ?? Guid.NewGuid().ToString("N");
            var task = HandleClientAsync(client, id, ct);
            _clients[id] = task;
        }
    }

    protected virtual async Task HandleClientAsync(TcpClient client, string id, CancellationToken ct)
    {
        try
        {
            using (client)
            using (var stream = client.GetStream())
            {
                await HandleFormattedConnectionAsync(client, stream, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"Client {id} error: {ex.Message}");
        }
        finally
        {
            _open.TryRemove(client, out _);
            _clients.TryRemove(id, out _);
            _log.Info($"Device disconnected ({id})");
        }
    }

    /// <summary>
    /// Reads complete requests (headers + Content-Length body) and dispatches
    /// them one by one on the same persistent connection.
    /// </summary>
    protected async Task HandleFormattedConnectionAsync(TcpClient client, NetworkStream stream, CancellationToken ct, byte[]? prefix = null)
    {
        var pending = new List<byte>(8192);
        if (prefix is { Length: > 0 })
            pending.AddRange(prefix);

        var readBuffer = new byte[65536];

        while (!ct.IsCancellationRequested && client.Connected)
        {
            // TryExtractRequest consumes on success, so it must be called exactly once per iteration.
            if (!TryExtractRequest(pending, out var rawRequest))
            {
                if (pending.Count > 1024 * 1024)
                {
                    _log.Warn("Oversized pending buffer; resetting connection.");
                    break;
                }

                int read;
                try
                {
                    read = await stream.ReadAsync(readBuffer.AsMemory(0, readBuffer.Length), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (IOException) { break; }
                if (read <= 0)
                    break;
                pending.AddRange(readBuffer.Take(read));
                continue;
            }

            var hex = BitConverter.ToString(rawRequest).Replace("-", "", StringComparison.Ordinal);
            Request request;
            try
            {
                request = new Request(hex);
            }
            catch (Exception ex)
            {
                _log.Warn($"Malformed request ({rawRequest.Length} bytes): {ex.Message}");
                continue;
            }

            if (!request.IsValid)
            {
                _log.Warn("Received invalid request; ignoring.");
                continue;
            }

            var response = request.GetBaseResponse();
            try
            {
                await _onRequest(request, response, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error($"Request handler failed for {request.Type} {request.Path}: {ex.GetType().Name}: {ex.Message}", ex);
                _log.Debug(ex.ToString());
                response.StatusCode = StatusCode.INTERNALSERVERERROR;
            }

            await SendResponseAsync(stream, response, ct).ConfigureAwait(false);
        }
    }

    private static bool TryExtractRequest(List<byte> pending, out byte[] request)
    {
        request = Array.Empty<byte>();
        if (pending.Count < 16)
            return false;

        // Find end of headers.
        int headerEnd = -1;
        for (int i = 0; i + 3 < pending.Count; i++)
        {
            if (pending[i] == (byte)'\r' && pending[i + 1] == (byte)'\n' && pending[i + 2] == (byte)'\r' && pending[i + 3] == (byte)'\n')
            {
                headerEnd = i + 4;
                break;
            }
        }
        if (headerEnd < 0)
            return false; // incomplete headers; caller reads more (with overflow guard)

        int contentLength = 0;
        var headerText = Encoding.ASCII.GetString(pending.Take(headerEnd).ToArray());
        foreach (var line in headerText.Split(["\r\n"], StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            if (line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(line[(colon + 1)..].Trim(), out var len) && len > 0)
            {
                contentLength = len;
                break;
            }
        }

        if (contentLength < 0 || contentLength > 16 * 1024 * 1024)
            return false;

        if (pending.Count < headerEnd + contentLength)
            return false;

        request = pending.Take(headerEnd + contentLength).ToArray();
        pending.RemoveRange(0, headerEnd + contentLength);
        return true;
    }

    protected static async Task SendResponseAsync(NetworkStream stream, Response response, CancellationToken ct)
    {
        var protocol = response.GetProtocol() ?? "RTSP/1.0";
        var sb = new StringBuilder();
        sb.Append(protocol).Append(' ').Append((int)response.StatusCode).Append(' ').Append(response.StatusCode.ToString().ToUpperInvariant()).Append("\r\n");
        foreach (var header in response.Headers)
            sb.Append(header.Name).Append(": ").Append(string.Join(",", header.Values)).Append("\r\n");
        sb.Append("\r\n");

        var headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
        var body = await response.ReadAsync().ConfigureAwait(false) ?? Array.Empty<byte>();

        try
        {
            await stream.WriteAsync(headerBytes.AsMemory(0, headerBytes.Length), ct).ConfigureAwait(false);
            if (body.Length > 0)
                await stream.WriteAsync(body.AsMemory(0, body.Length), ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (IOException) { }
        catch (OperationCanceledException) { }
    }

    /// <summary>Options every accepted AirPlay socket wants (best effort).</summary>
    internal static void TuneSocket(TcpClient client)
    {
        try
        {
            // Handshake responses are written as header + body: without NoDelay the body waits for the
            // peer's delayed ACK (tens to hundreds of ms per request).
            client.NoDelay = true;

            // A vanished iPad (Wi-Fi off, sleep) sends no FIN. Keepalive turns that into a read error
            // in ~20 s instead of a zombie session that blocks the next connection.
            var socket = client.Client;
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 10);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 3);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
        }
        catch { }
    }

    internal static bool LooksLikeControlRequest(byte[] data, int length)
    {
        if (length < 8)
            return false;
        var start = Encoding.ASCII.GetString(data, 0, Math.Min(length, 32));
        foreach (var verb in Verbs)
        {
            if (start.StartsWith(verb, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    public static bool IsLocalNetworkAddress(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
            return true;
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return false;
        var b = ip.GetAddressBytes();
        if (b[0] == 10) return true;
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
        if (b[0] == 192 && b[1] == 168) return true;
        if (b[0] == 169 && b[1] == 254) return true;
        return false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _cts?.Dispose(); } catch { }
    }
}
