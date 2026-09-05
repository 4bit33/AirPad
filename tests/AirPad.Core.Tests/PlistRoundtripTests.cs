using System.Runtime.Serialization.Plists;

namespace AirPad.Core.Tests;

public sealed class PlistRoundtripTests
{
    [Fact]
    public void WriterReader_Roundtrip_SessionSetupShapes()
    {
        var timing = new Dictionary<string, int>
        {
            ["timingPort"] = 5000,
            ["eventPort"] = 5000,
        };
        var bytes = Write(timing);
        var back = Read(bytes);
        Assert.Equal(5000, Convert.ToInt32(back["timingPort"]));
        Assert.Equal(5000, Convert.ToInt32(back["eventPort"]));
    }

    [Fact]
    public void WriterReader_Roundtrip_MirrorSetupResponse()
    {
        var streams = new Dictionary<string, List<Dictionary<string, int>>>
        {
            ["streams"] = new() { new() { ["type"] = 110, ["dataPort"] = 7000 } },
        };
        var bytes = Write(streams);
        var back = Read(bytes);

        var arr = (object[])back["streams"]!;
        Assert.Single(arr);
        var first = (Dictionary<object, object>)arr[0];
        Assert.Equal(110, Convert.ToInt32(first["type"]));
        Assert.Equal(7000, Convert.ToInt32(first["dataPort"]));
    }

    [Fact]
    public void WriterReader_Roundtrip_InfoTypes()
    {
        var dict = new Dictionary<string, object>
        {
            ["features"] = 61379444727L,
            ["name"] = "AirPad-Test",
            ["vv"] = 2,
            ["rotation"] = true,
            ["widthPixels"] = 1920.0,
            ["pk"] = "29fbb183a58b466e05b9ab667b3c429d18a6b785637333d3f0f3a34baa89f45e",
            ["blob"] = new byte[] { 1, 2, 3, 4 },
        };
        var back = Read(Write(dict));
        Assert.Equal(61379444727L, Convert.ToInt64(back["features"]));
        Assert.Equal("AirPad-Test", back["name"]);
        Assert.Equal(2, Convert.ToInt32(back["vv"]));
        Assert.True(Convert.ToBoolean(back["rotation"]));
        Assert.Equal(1920.0, Convert.ToDouble(back["widthPixels"]));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, (byte[])back["blob"]!);
    }

    private static byte[] Write(System.Collections.IDictionary graph)
    {
        using var ms = new MemoryStream();
        new BinaryPlistWriter().WriteObject(ms, graph, false);
        return ms.ToArray();
    }

    private static Dictionary<object, object> Read(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        return (Dictionary<object, object>)new BinaryPlistReader().ReadObject(ms)!;
    }
}
