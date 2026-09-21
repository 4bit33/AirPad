using System.Buffers;
using System.Runtime.InteropServices;
using AirPad.Core.Logging;
using AirPad.Core.Video;
using FFmpeg.AutoGen;

namespace AirPad.Core.Media;

/// <summary>
/// Low-latency H.264 → BGRA decoder.
/// Tries D3D11VA hardware decoding when enabled and available,
/// otherwise falls back to multi-threaded software decoding.
/// Single ownership per instance; call <see cref="Feed"/> from the
/// mirror thread and render the returned frames immediately
/// (no queue inside the decoder).
/// </summary>
public sealed unsafe class H264Decoder : IDisposable
{
    private readonly AirPadLogger _log;
    private readonly bool _preferHardware;
    private readonly object _gate = new(); // Feed (mirror thread) vs Flush/Close (request/UI threads): a native context freed mid-call crashes the process

    private AVCodecContext* _ctx;
    private SwsContext* _sws;
    private AVFrame* _hwFrame;
    private int _swsSrcW;
    private int _swsSrcH;
    private AVPixelFormat _swsSrcFmt = AVPixelFormat.AV_PIX_FMT_NONE;
    private bool _hardwareActive;
    private bool _disposed;

    private static readonly AVHWDeviceType HwType = AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA;
    private AVBufferRef* _hwDeviceCtx;

    public bool HardwareActive => _hardwareActive;

    /// <summary>True when a D3D11VA device was created at Open() time.</summary>
    public bool HardwareAvailable { get; private set; }

    public bool IsOpen => _ctx != null;

    public H264Decoder(AirPadLogger log, bool preferHardware = true)
    {
        _log = log;
        _preferHardware = preferHardware && OperatingSystem.IsWindows();
    }

    public bool Open()
    {
        if (!FFmpegLoader.IsLoaded)
        {
            _log.Error("H.264 decoder: FFmpeg not loaded.");
            return false;
        }

        var codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
        if (codec == null)
        {
            _log.Error("H.264 decoder: avcodec_find_decoder(H264) returned null.");
            return false;
        }

        _ctx = ffmpeg.avcodec_alloc_context3(codec);
        if (_ctx == null)
        {
            _log.Error("H.264 decoder: avcodec_alloc_context3 failed.");
            return false;
        }

        _ctx->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
        _ctx->flags2 |= ffmpeg.AV_CODEC_FLAG2_FAST;
        _ctx->thread_count = Math.Clamp(Environment.ProcessorCount, 1, 4);

        if (_preferHardware && TryInitHardware())
        {
            HardwareAvailable = true;
            _log.Info("H.264 decoder: D3D11VA hardware acceleration enabled");
        }
        else
        {
            HardwareAvailable = false;
            _log.Info("H.264 decoder: software decoding");
        }

        int ret = ffmpeg.avcodec_open2(_ctx, codec, null);
        if (ret < 0)
        {
            _log.Error($"H.264 decoder: avcodec_open2 failed ({DescribeError(ret)}).");
            Close();
            return false;
        }

        _hwFrame = ffmpeg.av_frame_alloc();
        return true;
    }

    private bool TryInitHardware()
    {
        try
        {
            AVBufferRef* device = null;
            if (ffmpeg.av_hwdevice_ctx_create(&device, HwType, null, null, 0) < 0 || device == null)
                return false;

            _hwDeviceCtx = device;
            _ctx->hw_device_ctx = ffmpeg.av_buffer_ref(device);
            _ctx->get_format = (AVCodecContext_get_format_func)GetHwFormat;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static AVPixelFormat GetHwFormat(AVCodecContext* ctx, AVPixelFormat* pixFmts)
    {
        for (var p = pixFmts; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
        {
            if (*p == AVPixelFormat.AV_PIX_FMT_D3D11)
                return *p;
        }
        return AVPixelFormat.AV_PIX_FMT_NONE;
    }

    /// <summary>
    /// Feeds one Annex-B access unit. Returns zero or more decoded frames
    /// (normally 0 or 1 for mirror streams). With <paramref name="convert"/> false the
    /// frame is still decoded (later frames reference it) but the costly GPU readback and
    /// colour conversion are skipped and nothing is returned.
    /// </summary>
    public List<VideoFrame> Feed(byte[] annexb, DateTime receivedUtc, bool convert = true)
    {
        var frames = new List<VideoFrame>(2);
        if (annexb == null || annexb.Length == 0)
            return frames;

        lock (_gate)
        {
            if (_ctx == null || _disposed)
                return frames;

            // Every mirror payload is exactly one access unit, so it goes straight to the decoder.
            // (av_parser_parse2 would hold each frame back until the *next* one starts: a frame of extra
            // latency, and the last frame of a burst stays invisible until the screen changes again.)
            var packet = ffmpeg.av_packet_alloc();
            if (packet == null)
                return frames;
            try
            {
                // av_new_packet returns a padded, ref-counted buffer, which the bitstream readers expect.
                if (ffmpeg.av_new_packet(packet, annexb.Length) < 0)
                    return frames;
                Marshal.Copy(annexb, 0, (IntPtr)packet->data, annexb.Length);
                if (ffmpeg.avcodec_send_packet(_ctx, packet) < 0)
                    return frames;

                while (true)
                {
                    var frame = ffmpeg.av_frame_alloc();
                    if (frame == null)
                        break;
                    try
                    {
                        if (ffmpeg.avcodec_receive_frame(_ctx, frame) < 0)
                            break; // EAGAIN / EOF / error: nothing more for this packet
                        if (!convert)
                            continue;

                        var converted = ConvertFrame(frame, receivedUtc);
                        if (converted != null)
                            frames.Add(converted);
                    }
                    finally
                    {
                        ffmpeg.av_frame_free(&frame);
                    }
                }
            }
            finally
            {
                ffmpeg.av_packet_free(&packet);
            }
        }

        return frames;
    }

    private VideoFrame? ConvertFrame(AVFrame* frame, DateTime receivedUtc)
    {
        AVFrame* src = frame;
        AVFrame* transferred = null;
        try
        {
            if (frame->format == (int)AVPixelFormat.AV_PIX_FMT_D3D11)
            {
                transferred = ffmpeg.av_frame_alloc();
                if (transferred == null)
                    return null;
                if (ffmpeg.av_hwframe_transfer_data(transferred, frame, 0) < 0)
                    return null;
                src = transferred;
                _hardwareActive = true;
            }

            int width = src->width;
            int height = src->height;
            if (width <= 0 || height <= 0 || width > 8192 || height > 8192)
                return null;

            if (_sws == null || width != _swsSrcW || height != _swsSrcH || (AVPixelFormat)src->format != _swsSrcFmt)
            {
                if (_sws != null)
                    ffmpeg.sws_freeContext(_sws);
                // BGRA + no ACCURATE_RND: no scaling happens here, and this is ~8x cheaper than BGR24 with
                // ACCURATE_RND (1.7 ms → 0.2 ms per 720p frame) and is WPF's native upload format.
                _sws = ffmpeg.sws_getContext(width, height, (AVPixelFormat)src->format,
                    width, height, AVPixelFormat.AV_PIX_FMT_BGRA,
                    (int)SwsFlags.SWS_FAST_BILINEAR, null, null, null);
                if (_sws == null)
                    return null;
                _swsSrcW = width;
                _swsSrcH = height;
                _swsSrcFmt = (AVPixelFormat)src->format;
            }

            int stride = width * 4;
            var pixels = ArrayPool<byte>.Shared.Rent(stride * height);
            fixed (byte* dst = pixels)
            {
                byte*[] dstData = { dst };
                int[] dstLinesize = { stride };
                ffmpeg.sws_scale(_sws, src->data, src->linesize, 0, height, dstData, dstLinesize);
            }

            return new VideoFrame
            {
                Width = width,
                Height = height,
                Stride = stride,
                Pixels = pixels,
                Pooled = true,
                NetworkReceivedUtc = receivedUtc,
                DecodedUtc = DateTime.UtcNow,
                HardwareDecoded = _hardwareActive,
            };
        }
        catch
        {
            return null;
        }
        finally
        {
            if (transferred != null)
                ffmpeg.av_frame_free(&transferred);
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            if (_ctx != null)
                ffmpeg.avcodec_flush_buffers(_ctx);
        }
    }

    private void Close()
    {
        lock (_gate)
        {
            if (_sws != null)
            {
                ffmpeg.sws_freeContext(_sws);
                _sws = null;
            }
            if (_hwFrame != null)
            {
                var frame = _hwFrame;
                _hwFrame = null;
                ffmpeg.av_frame_free(&frame);
            }
            if (_ctx != null)
            {
                var ctx = _ctx;
                _ctx = null;
                ffmpeg.avcodec_free_context(&ctx);
            }
            if (_hwDeviceCtx != null)
            {
                var device = _hwDeviceCtx;
                _hwDeviceCtx = null;
                ffmpeg.av_buffer_unref(&device);
            }
        }
    }

    private static string DescribeError(int error)
    {
        const int size = 1024;
        var buffer = stackalloc byte[size];
        ffmpeg.av_strerror(error, buffer, (ulong)size);
        return System.Text.Encoding.ASCII.GetString(buffer, size).TrimEnd('\0');
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Close();
    }
}
