using System.Net;
using System.Text;
using AirPad.Core.Config;
using AirPad.Core.Discovery;
using AirPad.Core.Models;
using AirPad.Core.Models.Enums;

namespace AirPad.Core.Tests;

public sealed class RequestParsingTests
{
    private static Request Parse(string ascii)
    {
        var bytes = Encoding.ASCII.GetBytes(ascii);
        var hex = BitConverter.ToString(bytes).Replace("-", "", StringComparison.Ordinal);
        return new Request(hex);
    }

    [Fact]
    public void ParsesOptionsRequest()
    {
        var req = Parse("OPTIONS * RTSP/1.0\r\nCSeq: 3\r\nApple-Challenge: abcdef\r\n\r\n");
        Assert.True(req.IsValid);
        Assert.Equal(RequestType.OPTIONS, req.Type);
        Assert.Equal("*", req.Path);
        Assert.Equal(ProtocolType.RTSP10, req.Protocol);
        Assert.Equal("3", req.Headers["CSeq"]);
    }

    [Fact]
    public void ParsesHttpGetInfo()
    {
        var req = Parse("GET /info HTTP/1.1\r\nContent-Length: 0\r\n\r\n");
        Assert.True(req.IsValid);
        Assert.Equal(RequestType.GET, req.Type);
        Assert.Equal("/info", req.Path);
        Assert.Equal(ProtocolType.HTTP11, req.Protocol);
    }

    [Fact]
    public void PreservesBinaryBodyWithEmbeddedCrLf()
    {
        var body = new byte[] { 0x62, 0x70, 0x6C, 0x69, 0x73, 0x74, 0x0D, 0x0A, 0x30, 0x30, 0xFF, 0x00 };
        var head = Encoding.ASCII.GetBytes($"SETUP rtsp://192.168.1.2/1234 RTSP/1.0\r\nCSeq: 5\r\nContent-Length: {body.Length}\r\n\r\n");
        var raw = head.Concat(body).ToArray();
        var hex = BitConverter.ToString(raw).Replace("-", "", StringComparison.Ordinal);
        var req = new Request(hex);
        Assert.True(req.IsValid);
        Assert.Equal(RequestType.SETUP, req.Type);
        Assert.Equal(body, req.Body);
    }

    [Fact]
    public void BaseResponseEchoesCSeq()
    {
        var req = Parse("RECORD rtsp://192.168.1.2/1234 RTSP/1.0\r\nCSeq: 9\r\n\r\n");
        var res = req.GetBaseResponse();
        Assert.Equal(StatusCode.OK, res.StatusCode);
        Assert.Equal("9", res.Headers["CSeq"]);
    }
}

public sealed class SettingsStoreTests
{
    [Fact]
    public void Roundtrip_PreservesValues()
    {
        var path = Path.Combine(Path.GetTempPath(), $"airpad-test-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(path);
            var settings = new AirPadSettings
            {
                ReceiverName = "Living-Room",
                AirPlayPort = 7001,
                MaxFps = 30,
                Volume = 0.5,
            };
            store.Save(settings);
            var loaded = new SettingsStore(path).Load();
            Assert.Equal("Living-Room", loaded.ReceiverName);
            Assert.Equal(7001, loaded.AirPlayPort);
            Assert.Equal(30, loaded.MaxFps);
            Assert.Equal(0.5, loaded.Volume);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void Sanitize_ClampsOutOfRange()
    {
        var path = Path.Combine(Path.GetTempPath(), $"airpad-test-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(path);
            store.Save(new AirPadSettings { AirPlayPort = 80, MaxFps = 1000, Volume = 5.0, VideoScaling = "nope" });
            var loaded = new SettingsStore(path).Load();
            Assert.InRange(loaded.AirPlayPort, 1024, 65535);
            Assert.InRange(loaded.MaxFps, 15, 120);
            Assert.InRange(loaded.Volume, 0.0, 1.0);
            Assert.Equal("Fit", loaded.VideoScaling);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void MissingFile_ReturnsDefaults()
    {
        var loaded = new SettingsStore(Path.Combine(Path.GetTempPath(), $"airpad-missing-{Guid.NewGuid():N}.json")).Load();
        Assert.Equal(7000, loaded.AirPlayPort);
        Assert.Equal(60, loaded.MaxFps);
    }
}

public sealed class NetworkInfoTests
{
    [Fact]
    public void EnumeratesWithoutThrowing()
    {
        var all = NetworkInfo.GetUsableIPv4Addresses();
        Assert.NotNull(all);
    }

    [Theory]
    [InlineData("192.168.1.10", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.16.4.4", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("8.8.8.8", false)]
    public void ClassifiesPrivateAddresses(string ip, bool expected)
    {
        Assert.Equal(expected, NetworkInfo.IsPrivateAddress(IPAddress.Parse(ip)));
    }
}
