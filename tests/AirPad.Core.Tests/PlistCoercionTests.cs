using AirPad.Core.Protocol;

namespace AirPad.Core.Tests;

/// <summary>
/// The plist codec yields Int64 for integers, but real devices may widen
/// fields, send numeric strings, or add unexpected shapes. Parsing must
/// never throw (an exception aborts the whole SETUP and drops the iPad).
/// </summary>
public sealed class PlistCoercionTests
{
    [Theory]
    [InlineData((long)7000, 7000L)]
    [InlineData(110, 110L)]
    [InlineData((short)96, 96L)]
    [InlineData((byte)5, 5L)]
    [InlineData(63254.0, 63254L)]
    [InlineData("1932754847159437512", 1932754847159437512L)]
    [InlineData(true, 1L)]
    [InlineData(false, 0L)]
    [InlineData(null, 0L)]
    public void PlistLong_CoercesCommonShapes(object? value, long expected)
    {
        Assert.Equal(expected, AirPlayRequestHandler.PlistLong(value));
    }

    [Fact]
    public void PlistLong_NeverThrowsOnHostileInput()
    {
        Assert.Equal(0, AirPlayRequestHandler.PlistLong(new byte[] { 1, 2, 3 }));
        Assert.Equal(0, AirPlayRequestHandler.PlistLong(ulong.MaxValue));
        Assert.Equal(0, AirPlayRequestHandler.PlistLong(double.NaN));
        Assert.Equal(0, AirPlayRequestHandler.PlistLong("not-a-number"));
        Assert.Equal(7, AirPlayRequestHandler.PlistLong("garbage", 7));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(1L, true)]
    [InlineData(0L, false)]
    [InlineData(null, false)]
    public void PlistBool_CoercesCommonShapes(object? value, bool expected)
    {
        Assert.Equal(expected, AirPlayRequestHandler.PlistBool(value));
    }

    [Fact]
    public void FormatStreamConnectionId_UsesUnsignedForm()
    {
        // Real session value that previously derived a wrong video key:
        // -1378097000387297060 (Int64) == 17068647073322254556 (UInt64).
        Assert.Equal("17068647073322254556",
            AirPlayRequestHandler.FormatStreamConnectionId(-1378097000387297060L));
        Assert.Equal("6613281118396170940",
            AirPlayRequestHandler.FormatStreamConnectionId(6613281118396170940L));
        Assert.Equal("abc", AirPlayRequestHandler.FormatStreamConnectionId("abc"));
        Assert.Equal("0", AirPlayRequestHandler.FormatStreamConnectionId(null));
    }
}
