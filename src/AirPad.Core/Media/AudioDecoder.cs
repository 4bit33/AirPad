using AirPad.Core.Audio;
using AirPad.Core.Logging;
using AirPad.Core.Models.Enums;
using FFmpeg.AutoGen;

namespace AirPad.Core.Media;

/// <summary>
/// Decodes RAOP audio payloads (AAC-ELD mirror audio, AAC, ALAC music)
/// into 16-bit stereo PCM at 44100 Hz using FFmpeg.
/// </summary>
public sealed unsafe class AudioDecoder : IDisposable
{
    private readonly AirPadLogger _log;
    private AVCodecContext* _ctx;
    private SwrContext* _swr;
    private AudioFormat _format = AudioFormat.Unknown;
    private bool _disposed;

    public bool IsOpen => _ctx != null;
    public AudioFormat Format => _format;

    public AudioDecoder(AirPadLogger log)
    {
        _log = log;
    }

    public bool Open(AudioFormat format)
    {
        Close();

        if (!FFmpegLoader.IsLoaded)
        {
            _log.Error("Audio decoder: FFmpeg not loaded.");
            return false;
        }

        AVCodecID codecId = format switch
        {
            AudioFormat.AAC_ELD => AVCodecID.AV_CODEC_ID_AAC,
            AudioFormat.AAC => AVCodecID.AV_CODEC_ID_AAC,
            AudioFormat.ALAC => AVCodecID.AV_CODEC_ID_ALAC,
            _ => AVCodecID.AV_CODEC_ID_NONE,
        };
        if (codecId == AVCodecID.AV_CODEC_ID_NONE)
        {
            _log.Warn($"Audio decoder: unsupported format {format}");
            return false;
        }

        var codec = ffmpeg.avcodec_find_decoder(codecId);
        if (codec == null)
        {
            _log.Error($"Audio decoder: codec not found for {format}.");
            return false;
        }

        _ctx = ffmpeg.avcodec_alloc_context3(codec);
        if (_ctx == null)
            return false;

        _ctx->sample_rate = 44100;
        ffmpeg.av_channel_layout_default(&_ctx->ch_layout, 2);
        _ctx->request_sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_S16;

        var extradata = BuildExtradata(format);
        if (extradata is { Length: > 0 })
        {
            _ctx->extradata_size = extradata.Length;
            _ctx->extradata = (byte*)ffmpeg.av_malloc((ulong)(extradata.Length + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE));
            if (_ctx->extradata == null)
            {
                Close();
                return false;
            }
            fixed (byte* src = extradata)
                Buffer.MemoryCopy(src, _ctx->extradata, extradata.Length, extradata.Length);
        }

        int ret = ffmpeg.avcodec_open2(_ctx, codec, null);
        if (ret < 0)
        {
            _log.Error($"Audio decoder: avcodec_open2({format}) failed ({ret}). " +
                       (format == AudioFormat.ALAC ? "ALAC streaming audio will be skipped; mirroring video is unaffected." : "Mirror audio will be muted."));
            Close();
            return false;
        }

        _format = format;
        _log.Info($"Audio decoder opened: {format}");
        return true;
    }

    /// <summary>
    /// Decodes one decrypted RTP payload into PCM chunks.
    /// Returns an empty list when the payload cannot be decoded.
    /// </summary>
    public List<PcmPacket> Decode(byte[] payload, uint timestamp)
    {
        var output = new List<PcmPacket>(2);
        if (_ctx == null || _disposed || payload == null || payload.Length == 0)
            return output;

        // Some senders prefix AAC access units with a 2-byte AU header.
        // Try the raw payload first, then the payload without the header.
        if (TryDecodeOnce(payload, timestamp, output) || payload.Length <= 2)
            return output;

        var trimmed = new byte[payload.Length - 2];
        Buffer.BlockCopy(payload, 2, trimmed, 0, trimmed.Length);
        TryDecodeOnce(trimmed, timestamp, output);
        return output;
    }

    private bool TryDecodeOnce(byte[] data, uint timestamp, List<PcmPacket> output)
    {
        bool produced = false;
        fixed (byte* ptr = data)
        {
            var packet = ffmpeg.av_packet_alloc();
            if (packet == null)
                return false;
            try
            {
                packet->data = ptr;
                packet->size = data.Length;
                if (ffmpeg.avcodec_send_packet(_ctx, packet) < 0)
                    return false;

                while (true)
                {
                    var frame = ffmpeg.av_frame_alloc();
                    if (frame == null)
                        break;
                    try
                    {
                        int ret = ffmpeg.avcodec_receive_frame(_ctx, frame);
                        if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                            break;
                        if (ret < 0)
                            break;

                        var pcm = ResampleToS16Stereo(frame);
                        if (pcm is { Length: > 0 })
                        {
                            output.Add(new PcmPacket { Data = pcm, Format = _format, Timestamp = timestamp });
                            produced = true;
                        }
                    }
                    finally
                    {
                        ffmpeg.av_frame_free(&frame);
                    }
                }
            }
            finally
            {
                packet->data = null;
                packet->size = 0;
                ffmpeg.av_packet_free(&packet);
            }
        }
        return produced;
    }

    private byte[]? ResampleToS16Stereo(AVFrame* frame)
    {
        try
        {
            if (_swr == null)
            {
                _swr = ffmpeg.swr_alloc();
                if (_swr == null)
                    return null;
                ffmpeg.av_opt_set_chlayout(_swr, "in_chlayout", &frame->ch_layout, 0);
                ffmpeg.av_opt_set_int(_swr, "in_sample_rate", frame->sample_rate, 0);
                ffmpeg.av_opt_set_sample_fmt(_swr, "in_sample_fmt", (AVSampleFormat)frame->format, 0);
                AVChannelLayout stereo = default;
                ffmpeg.av_channel_layout_default(&stereo, 2);
                ffmpeg.av_opt_set_chlayout(_swr, "out_chlayout", &stereo, 0);
                ffmpeg.av_opt_set_int(_swr, "out_sample_rate", 44100, 0);
                ffmpeg.av_opt_set_sample_fmt(_swr, "out_sample_fmt", AVSampleFormat.AV_SAMPLE_FMT_S16, 0);
                if (ffmpeg.swr_init(_swr) < 0)
                    return null;
            }

            int outSamples = (int)ffmpeg.av_rescale_rnd(
                ffmpeg.swr_get_delay(_swr, frame->sample_rate) + frame->nb_samples, 44100, frame->sample_rate, AVRounding.AV_ROUND_UP);
            int outLinesize = 0;
            byte* outBuffer = null;
            if (ffmpeg.av_samples_alloc(&outBuffer, &outLinesize, 2, outSamples, AVSampleFormat.AV_SAMPLE_FMT_S16, 0) < 0)
                return null;
            try
            {
                int converted = ffmpeg.swr_convert(_swr, &outBuffer, outSamples, frame->extended_data, frame->nb_samples);
                if (converted <= 0)
                    return null;
                int bytes = converted * 2 * 2;
                var managed = new byte[bytes];
                fixed (byte* dst = managed)
                    Buffer.MemoryCopy(outBuffer, dst, bytes, bytes);
                return managed;
            }
            finally
            {
                ffmpeg.av_freep(&outBuffer);
            }
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? BuildExtradata(AudioFormat format) => format switch
    {
        // AudioSpecificConfig for AAC-ELD, 44.1 kHz stereo (as used by UxPlay-derived receivers).
        AudioFormat.AAC_ELD => new byte[] { 0xF8, 0xE8, 0x50, 0x00 },
        // AudioSpecificConfig for AAC-LC, 44.1 kHz stereo.
        AudioFormat.AAC => new byte[] { 0x12, 0x10 },
        // ALAC magic cookie, 352 frames/packet, 16-bit, 2ch, 44.1 kHz (best effort).
        AudioFormat.ALAC => new byte[]
        {
            0x00, 0x00, 0x00, 0x24, 0x61, 0x6C, 0x61, 0x63,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x60,
            0x00, 0x00, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0xAC, 0x44, 0x00,
            0x00, 0x02, 0xFF, 0x00,
        },
        _ => null,
    };

    private void Close()
    {
        if (_swr != null)
        {
            var swr = _swr;
            _swr = null;
            ffmpeg.swr_free(&swr);
        }
        if (_ctx != null)
        {
            var ctx = _ctx;
            _ctx = null;
            ffmpeg.avcodec_free_context(&ctx);
        }
        _format = AudioFormat.Unknown;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Close();
    }
}
