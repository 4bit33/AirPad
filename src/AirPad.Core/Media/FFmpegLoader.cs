using AirPad.Core.Logging;
using FFmpeg.AutoGen;

namespace AirPad.Core.Media;

/// <summary>
/// Locates and loads the native FFmpeg shared libraries (avcodec, avutil,
/// swscale, swresample). Video/audio decoding is unavailable until
/// <see cref="TryLoad"/> succeeds; callers must fall back gracefully
/// (ffplay named-pipe mode for video, mute for audio).
/// </summary>
public static class FFmpegLoader
{
    private static readonly object Gate = new();
    private static bool _loaded;
    private static string? _loadedFrom;

    public static bool IsLoaded
    {
        get { lock (Gate) return _loaded; }
    }

    public static string? LoadedFrom
    {
        get { lock (Gate) return _loadedFrom; }
    }

    public static string Version
    {
        get
        {
            lock (Gate)
            {
                if (!_loaded)
                    return "not loaded";
                try { return ffmpeg.av_version_info(); }
                catch { return "unknown"; }
            }
        }
    }

    /// <summary>
    /// Searches common locations for FFmpeg binaries and loads them.
    /// Order: explicit directory → AIRPAD_FFMPEG_DIR env var → app directory
    /// → ./ffmpeg → runtimes/win-x64/native → PATH probe.
    /// </summary>
    public static bool TryLoad(AirPadLogger log, string? explicitDirectory = null)
    {
        lock (Gate)
        {
            if (_loaded)
                return true;

            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(explicitDirectory))
                candidates.Add(explicitDirectory!);

            var envDir = Environment.GetEnvironmentVariable("AIRPAD_FFMPEG_DIR");
            if (!string.IsNullOrWhiteSpace(envDir))
                candidates.Add(envDir!);

            var appDir = AppContext.BaseDirectory;
            candidates.Add(appDir);
            candidates.Add(Path.Combine(appDir, "ffmpeg"));
            candidates.Add(Path.Combine(appDir, "runtimes", "win-x64", "native"));

            foreach (var dir in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (!Directory.Exists(dir))
                        continue;
                    if (!Directory.GetFiles(dir, "avcodec*.dll").Any())
                        continue;

                    ffmpeg.RootPath = dir;
                    var version = ffmpeg.av_version_info();
                    _loaded = true;
                    _loadedFrom = dir;
                    log.Info($"FFmpeg loaded: {version} ({dir})");
                    return true;
                }
                catch (Exception ex)
                {
                    log.Debug($"FFmpeg load failed in '{dir}': {ex.Message}");
                }
            }

            // Last resort: rely on PATH / system loader.
            try
            {
                ffmpeg.RootPath = "";
                var version = ffmpeg.av_version_info();
                _loaded = true;
                _loadedFrom = "(PATH)";
                log.Info($"FFmpeg loaded from PATH: {version}");
                return true;
            }
            catch (Exception ex)
            {
                log.Warn($"FFmpeg native libraries not found: {ex.Message}. " +
                         "Drop a BtbN/gyan FFmpeg shared build (avcodec/avformat/avutil/swscale/swresample DLLs) next to AirPad.exe. " +
                         "See docs/BUILD.md and scripts/get-ffmpeg.ps1.");
                return false;
            }
        }
    }
}
