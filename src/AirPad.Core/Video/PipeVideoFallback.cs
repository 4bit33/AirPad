using System.IO.Pipes;
using AirPad.Core.Logging;

namespace AirPad.Core.Video;

/// <summary>
/// Fallback video sink for PCs without FFmpeg DLLs: exposes the decrypted
/// Annex-B H.264 stream on a named pipe (<c>\\.\pipe\AirPadVideo</c>) that an
/// external player (ffplay, see scripts/run-ffplay-pipe.ps1) can display.
/// Used only when the built-in decoder cannot open.
/// </summary>
public sealed class PipeVideoFallback : IDisposable
{
    public const string PipeName = "AirPadVideo";

    private readonly AirPadLogger _log;
    private readonly object _gate = new();
    private NamedPipeServerStream? _pipe;
    private bool _disposed;

    public bool ClientConnected => _pipe?.IsConnected == true;

    public PipeVideoFallback(AirPadLogger log)
    {
        _log = log;
    }

    public void Write(byte[] annexb)
    {
        if (_disposed || annexb == null || annexb.Length == 0)
            return;

        lock (_gate)
        {
            try
            {
                _pipe ??= CreatePipe();
                if (!_pipe.IsConnected)
                    return; // no viewer attached; drop (low latency over completeness)

                _pipe.Write(annexb, 0, annexb.Length);
                _pipe.Flush();
            }
            catch (IOException)
            {
                Recreate();
            }
            catch (ObjectDisposedException)
            {
                Recreate();
            }
            catch
            {
                Recreate();
            }
        }
    }

    private NamedPipeServerStream CreatePipe()
    {
        var pipe = new NamedPipeServerStream(PipeName, PipeDirection.Out, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536);
        // Accept the viewer in the background; frames drop until it connects.
        _ = pipe.WaitForConnectionAsync().ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully)
                _log.Info("ffplay pipe viewer connected (fallback video output)");
        });
        return pipe;
    }

    private void Recreate()
    {
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            try { _pipe?.Dispose(); } catch { }
            _pipe = null;
        }
    }
}
