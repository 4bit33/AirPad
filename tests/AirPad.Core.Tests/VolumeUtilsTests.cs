using AirPad.Core.Audio;

namespace AirPad.Core.Tests;

public sealed class VolumeUtilsTests
{
    [Theory]
    [InlineData(0.0, 1.0)]
    [InlineData(-6.0206, 0.5)]
    [InlineData(-20.0, 0.1)]
    [InlineData(-30.0, 0.0316)]
    [InlineData(-144.0, 0.0)]
    [InlineData(-200.0, 0.0)]
    [InlineData(3.0, 1.0)] // clamped
    public void DbToLinear_ConvertsDecibels(double db, double expected)
    {
        Assert.Equal(expected, VolumeUtils.DbToLinear(db), precision: 3);
    }

    [Theory]
    [InlineData(1.0, 0.0)]
    [InlineData(0.5, -6.0206)]
    [InlineData(0.0, -144.0)]
    [InlineData(-1.0, -144.0)]
    [InlineData(2.0, 0.0)] // clamped
    public void LinearToDb_ConvertsBack(double linear, double expected)
    {
        Assert.Equal(expected, VolumeUtils.LinearToDb(linear), precision: 3);
    }

    [Fact]
    public void Roundtrip_PreservesAudibleRange()
    {
        foreach (var db in new[] { 0.0, -1.875, -15.0, -30.0 })
            Assert.Equal(db, VolumeUtils.LinearToDb(VolumeUtils.DbToLinear(db)), precision: 6);
    }
}
