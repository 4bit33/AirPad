using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AirPad.Core.Logging;
using AirPad.Core.Models;
using AirPad.Core.Models.Enums;
using AirPad.Core.Protocol.Models;

namespace AirPad.Core.Network;

/// <summary>
/// Dual-mode listener for the AirPlay port (default 7000).
/// The first bytes of each new connection decide its fate:
/// an RTSP/HTTP request line → control plane (same handler as the
/// AirTunes port); anything else → raw mirror stream for the session
/// that most recently completed a mirror SETUP.
/// </summary>
public sealed class DualModeServer : IDisposable
{
    private readonly int _port;
    private readonly AirPadLogger _log;
    private readonly Func<Request, Response, CancellationToken, Task> _onControlRequest;
    private readonly MirrorStreamHandler _mirror;
    private readonly Func<Session?> _claimMirrorSession;
    private readonly Action<Session>? _mirrorConnected;
    private readonly Action<Session>? _mirrorDisconnected;

    private readonly ConcurrentDictionary<TcpClient, byte> _open = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public DualModeServer(
        int port,
        AirPadLogger log,
        Func<Request, Response, CancellationToken, Task> onControlRequest,
        MirrorStreamHandler mirror,
        Func<Session?> claimMirrorSession,
        Action<Session>? mirrorConnected = null,
        Action<Session>? mirrorDisconnected = null)
    {
        _port = port;
        _log = log;
        _onControlRequest = onControlRequest;
        _mirror = mirror;
        _claimMirrorSession = claimMirrorSession;
        _mirrorConnected = mirrorConnected;
        _mirrorDisconnected = mirrorDisconnected;
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();
        _log.Info($"TCP server listening on port {_port} (dual-mode: control + mirror)");
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
        return Task.CompletedTask;
    }

    /// <summary>Hangs up every live connection (control and mirror): the iPad sees the socket close and stops mirroring.</summary>
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

            try
            {
                var ip = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
                if (ip != null && !TcpControlServer.IsLocalNetworkAddress(ip))
                {
                    _log.Warn($"Rejected non-local connection from {client.Client.RemoteEndPoint}");
                    client.Close();
                    continue;
                }
            }
            catch { }

            TcpControlServer.TuneSocket(client);
            _ = Task.Run(() => RouteClientAsync(client, ct), ct);
        }
    }

    private async Task RouteClientAsync(TcpClient client, CancellationToken ct)
    {
        string remote = "?";
        try { remote = client.Client.RemoteEndPoint?.ToString() ?? "?"; } catch { }
        _open[client] = 0;

        try
        {
            using (client)
            {
                client.ReceiveTimeout = 8000;
                client.SendTimeout = 8000;
                var stream = client.GetStream();

                // Peek the first line (up to 512 bytes) to classify the connection.
                var peek = new List<byte>(512);
                var tmp = new byte[512];
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
                bool decided = false;
                bool isControl = false;
                while (!decided)
                {
                    int read;
                    try
                    {
                        read = await stream.ReadAsync(tmp.AsMemory(0, tmp.Length), linked.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        _log.Debug($"Connection from {remote} closed before classification.");
                        return;
                    }
                    catch (IOException)
                    {
                        return;
                    }
                    if (read <= 0)
                        return;
                    peek.AddRange(tmp.Take(read));

                    var text = Encoding.ASCII.GetString(peek.Take(Math.Min(peek.Count, 64)).ToArray());
                    if (StartsWithVerb(text))
                    {
                        isControl = true;
                        decided = true;
                    }
                    else if (peek.Count >= 8 && !CouldBecomeVerb(text, peek.Count))
                    {
                        isControl = false;
                        decided = true;
                    }
                    else if (peek.Count >= 512)
                    {
                        isControl = false;
                        decided = true;
                    }
                }

                client.ReceiveTimeout = 0;
                client.SendTimeout = 0;

                if (isControl)
                {
                    _log.Debug($"Control connection from {remote} on port {_port}");
                    var control = new InlineControlConnection(_onControlRequest, _log);
                    await control.RunAsync(client, stream, peek.ToArray(), ct).ConfigureAwait(false);
                }
                else
                {
                    var session = _claimMirrorSession();
                    if (session == null)
                    {
                        _log.Warn($"Mirror stream from {remote} with no pending mirror session; closing.");
                        return;
                    }
                    _log.Info($"Mirror stream connected from {remote} (session {session.SessionId})");
                    try { _mirrorConnected?.Invoke(session); } catch { }
                    try
                    {
                        await _mirror.RunAsync(client, session, peek.ToArray(), ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        try { _mirrorDisconnected?.Invoke(session); } catch { }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"Connection {remote} on port {_port} ended: {ex.Message}");
        }
        finally
        {
            _open.TryRemove(client, out _);
        }
    }

    private static readonly string[] Verbs = { "GET ", "POST ", "SETUP ", "RECORD ", "PAUSE ", "FLUSH ", "TEARDOWN ", "OPTIONS ", "ANNOUNCE ", "SET_PARAMETER ", "GET_PARAMETER " };

    private static bool StartsWithVerb(string text)
    {
        foreach (var v in Verbs)
        {
            if (text.StartsWith(v, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static bool CouldBecomeVerb(string text, int totalBytes)
    {
        // Could the buffered bytes still grow into a verb? Only if the text
        // is a prefix of some verb and we have very few bytes.
        if (totalBytes > 16)
            return false;
        foreach (var v in Verbs)
        {
            if (v.StartsWith(text, StringComparison.Ordinal))
                return true;
        }
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

    /// <summary>
    /// Minimal inline formatted-connection loop (shares framing rules with
    /// <see cref="TcpControlServer"/> without needing a second listener).
    /// </summary>
    private sealed class InlineControlConnection
    {
        private readonly Func<Request, Response, CancellationToken, Task> _onRequest;
        private readonly AirPadLogger _log;

        public InlineControlConnection(Func<Request, Response, CancellationToken, Task> onRequest, AirPadLogger log)
        {
            _onRequest = onRequest;
            _log = log;
        }

        public async Task RunAsync(TcpClient client, NetworkStream stream, byte[] prefix, CancellationToken ct)
        {
            var pending = new List<byte>(prefix);
            var readBuffer = new byte[65536];

            while (!ct.IsCancellationRequested && client.Connected)
            {
                if (!TryExtract(pending, out var raw))
                {
                    if (pending.Count > 1024 * 1024)
                        break;
                    int read;
                    try
                    {
                        read = await stream.ReadAsync(readBuffer.AsMemory(0, readBuffer.Length), ct).ConfigureAwait(false);
                    }
                    catch { break; }
                    if (read <= 0)
                        break;
                    pending.AddRange(readBuffer.Take(read));
                    continue;
                }

                var hex = BitConverter.ToString(raw).Replace("-", "", StringComparison.Ordinal);
                Request request;
                try { request = new Request(hex); }
                catch (Exception ex)
                {
                    _log.Warn($"Malformed request on port 7000: {ex.Message}");
                    continue;
                }
                if (!request.IsValid)
                    continue;

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
                await SendAsync(stream, response, ct).ConfigureAwait(false);
            }
        }

        private static bool TryExtract(List<byte> pending, out byte[] request)
        {
            request = Array.Empty<byte>();
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
                return false;

            int contentLength = 0;
            var headerText = Encoding.ASCII.GetString(pending.Take(headerEnd).ToArray());
            foreach (var line in headerText.Split(["\r\n"], StringSplitOptions.RemoveEmptyEntries).Skip(1))
            {
                int colon = line.IndexOf(':');
                if (colon > 0 && line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
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

        private static async Task SendAsync(NetworkStream stream, Response response, CancellationToken ct)
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
            catch { }
        }
    }
}
