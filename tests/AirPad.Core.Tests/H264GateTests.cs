using AirPad.Core.Video;

namespace AirPad.Core.Tests;

public sealed class H264GateTests
{
    [Fact]
    public void AvccToAnnexB_RewritesLengthsToStartCodes()
    {
        // Two NALUs: len=3 "ABC", len=2 "DE".
        var payload = new byte[] { 0, 0, 0, 3, (byte)'A', (byte)'B', (byte)'C', 0, 0, 0, 2, (byte)'D', (byte)'E' };
        Assert.True(H264Gate.AvccToAnnexB(payload));
        Assert.Equal(new byte[] { 0, 0, 0, 1, (byte)'A', (byte)'B', (byte)'C', 0, 0, 0, 1, (byte)'D', (byte)'E' }, payload);
    }

    [Fact]
    public void AvccToAnnexB_RejectsTruncatedPayload()
    {
        var payload = new byte[] { 0, 0, 0, 10, 1, 2, 3 };
        Assert.False(H264Gate.AvccToAnnexB(payload));
    }

    [Fact]
    public void ParseAvccBox_RejectsMalformedInput()
    {
        Assert.Null(H264Gate.ParseAvccBox(null!));
        Assert.Null(H264Gate.ParseAvccBox(Array.Empty<byte>()));
        Assert.Null(H264Gate.ParseAvccBox(new byte[11]));
    }

    [Fact]
    public void ParseAvccBox_BuildsAnnexBSpsPps()
    {
        // avcC: version/profile/compat/level/reserved..., spsLen=4 "SPS!", ppsCount=1, ppsLen=3 "PPS".
        var payload = new byte[]
        {
            1, 100, 0, 31, 0xFF, 0xE1, 0x00, 0x04,
            (byte)'S', (byte)'P', (byte)'S', (byte)'!',
            0x01, 0x00, 0x03, (byte)'P', (byte)'P', (byte)'S',
        };
        var spsPps = H264Gate.ParseAvccBox(payload);
        Assert.NotNull(spsPps);
        Assert.Equal(new byte[]
        {
            0, 0, 0, 1, (byte)'S', (byte)'P', (byte)'S', (byte)'!',
            0, 0, 0, 1, (byte)'P', (byte)'P', (byte)'S',
        }, spsPps);
    }

    [Fact]
    public void BuildDecoderPacket_PrependsSpsPpsToIdr()
    {
        var spsPps = new byte[] { 0, 0, 0, 1, 9 };
        // NAL header 0x65 = forbidden(0) + nal_ref_idc(3) + type(5=IDR).
        var payload = new byte[] { 0, 0, 0, 2, 0x65, 0xAA };
        var packet = H264Gate.BuildDecoderPacket(payload, spsPps, out bool isIdr);
        Assert.NotNull(packet);
        Assert.True(isIdr);
        Assert.Equal(new byte[] { 0, 0, 0, 1, 9, 0, 0, 0, 1, 0x65, 0xAA }, packet);
    }

    [Fact]
    public void BuildDecoderPacket_LeavesNonIdrAlone()
    {
        // NAL type 1 (non-IDR slice).
        var payload = new byte[] { 0, 0, 0, 2, 0x41, 0xBB };
        var packet = H264Gate.BuildDecoderPacket(payload, new byte[] { 1, 2, 3 }, out bool isIdr);
        Assert.NotNull(packet);
        Assert.False(isIdr);
        Assert.Equal(6, packet.Length);
    }
}
