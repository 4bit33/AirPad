using AirPad.Core.Logging;
using AirPad.Core.Media;
using FFmpeg.AutoGen;

namespace AirPad.Core.Tests;

/// <summary>
/// Verifies the managed FFmpeg.AutoGen bindings against the real native
/// DLLs (run scripts/get-ffmpeg.ps1 first, or set AIRPAD_FFMPEG_DIR).
/// Skipped when the DLLs are absent so plain CI stays green.
/// </summary>
public sealed unsafe class NativeFFmpegSmokeTests
{
    private static bool TryEnsureLoaded()
    {
        var log = new AirPadLogger(minimumLevel: LogLevel.Error);
        return FFmpegLoader.TryLoad(log);
    }

    [SkippableFact]
    public void LoadsNativeLibraries()
    {
        Skip.IfNot(TryEnsureLoaded(), "Native FFmpeg DLLs not present (run scripts/get-ffmpeg.ps1 or set AIRPAD_FFMPEG_DIR).");

        Assert.Contains("8.1", FFmpegLoader.Version, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void FindsRequiredDecoders()
    {
        Skip.IfNot(TryEnsureLoaded(), "Native FFmpeg DLLs not present (run scripts/get-ffmpeg.ps1 or set AIRPAD_FFMPEG_DIR).");

        Assert.NotEqual(IntPtr.Zero, (IntPtr)ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264));
        Assert.NotEqual(IntPtr.Zero, (IntPtr)ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_AAC));
        Assert.NotEqual(IntPtr.Zero, (IntPtr)ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_ALAC));
    }

    [SkippableFact]
    public void OpensH264DecoderContext()
    {
        Skip.IfNot(TryEnsureLoaded(), "Native FFmpeg DLLs not present (run scripts/get-ffmpeg.ps1 or set AIRPAD_FFMPEG_DIR).");

        var codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
        Assert.NotEqual(IntPtr.Zero, (IntPtr)codec);

        var ctx = ffmpeg.avcodec_alloc_context3(codec);
        Assert.NotEqual(IntPtr.Zero, (IntPtr)ctx);
        try
        {
            ctx->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
            Assert.Equal(0, ffmpeg.avcodec_open2(ctx, codec, null));
        }
        finally
        {
            var c = ctx;
            ffmpeg.avcodec_free_context(&c);
        }
    }
}
