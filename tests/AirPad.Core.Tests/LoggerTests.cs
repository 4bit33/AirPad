using AirPad.Core.Logging;

namespace AirPad.Core.Tests;

public sealed class LoggerTests
{
    [Fact]
    public void EachInstance_WritesItsOwnFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"airpad-logtest-{Guid.NewGuid():N}");
        try
        {
            using var a = new AirPadLogger(dir, LogLevel.Debug);
            using var b = new AirPadLogger(dir, LogLevel.Debug);

            Assert.NotNull(a.LogFilePath);
            Assert.NotNull(b.LogFilePath);
            Assert.NotEqual(a.LogFilePath, b.LogFilePath);

            a.Info("hello-a");
            b.Info("hello-b");

            Assert.Contains("hello-a", ReadShared(a.LogFilePath!));
            Assert.Contains("hello-b", ReadShared(b.LogFilePath!));
            Assert.DoesNotContain("hello-b", ReadShared(a.LogFilePath!));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void PrunesOldFiles_KeepsThirty()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"airpad-logtest-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dir);
            // 35 stale files with distinct timestamps.
            for (int i = 0; i < 35; i++)
            {
                var path = Path.Combine(dir, $"airpad-20200101-00000{i:D2}-p1.log");
                File.WriteAllText(path, "stale");
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-i - 1));
            }

            using var logger = new AirPadLogger(dir, LogLevel.Error);

            var remaining = Directory.GetFiles(dir, "airpad-*.log");
            Assert.Equal(30, remaining.Length);
            Assert.Contains(logger.LogFilePath!, remaining);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>Reads a live log file that another handle keeps open for writing.</summary>
    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }
}
