namespace AirPad.Core.Video;

/// <summary>
/// Converts the AirPlay H.264 wire format into Annex-B byte streams
/// that a stock H.264 decoder accepts.
/// <para/>
/// On the wire, video payloads use AVCC length-prefixed NALUs
/// (4-byte big-endian length + NALU). SPS/PPS arrive separately
/// (payload type 1, avcC box) and must be prepended to IDR frames.
/// </summary>
public static class H264Gate
{
    private static readonly byte[] StartCode = { 0, 0, 0, 1 };

    /// <summary>
    /// Parses an avcC box (payload type 1) into Annex-B SPS+PPS.
    /// Returns null when the payload is malformed.
    /// </summary>
    public static byte[]? ParseAvccBox(byte[] payload)
    {
        try
        {
            if (payload == null || payload.Length < 11)
                return null;

            int spsLen = (payload[6] << 8) | payload[7];
            if (8 + spsLen + 3 > payload.Length)
                return null;

            var sps = new byte[spsLen];
            Buffer.BlockCopy(payload, 8, sps, 0, spsLen);

            int ppsCountPos = 8 + spsLen;
            int ppsLenPos = ppsCountPos + 1;
            if (ppsLenPos + 2 > payload.Length)
                return null;

            int ppsLen = (payload[ppsLenPos] << 8) | payload[ppsLenPos + 1];
            if (ppsLen <= 0 || ppsLenPos + 2 + ppsLen > payload.Length)
                return null;

            var pps = new byte[ppsLen];
            Buffer.BlockCopy(payload, ppsLenPos + 2, pps, 0, ppsLen);

            if (spsLen + ppsLen > 100 * 1024)
                return null;

            var output = new byte[4 + spsLen + 4 + ppsLen];
            Buffer.BlockCopy(StartCode, 0, output, 0, 4);
            Buffer.BlockCopy(sps, 0, output, 4, spsLen);
            Buffer.BlockCopy(StartCode, 0, output, 4 + spsLen, 4);
            Buffer.BlockCopy(pps, 0, output, 8 + spsLen, ppsLen);
            return output;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Rewrites length-prefixed NALUs to Annex-B in place.
    /// Returns false when the payload is malformed (caller must drop it).
    /// </summary>
    public static bool AvccToAnnexB(byte[] payload)
    {
        try
        {
            int pos = 0;
            while (pos + 4 <= payload.Length)
            {
                int naluLen = (payload[pos] << 24) | (payload[pos + 1] << 16) | (payload[pos + 2] << 8) | payload[pos + 3];
                if (naluLen <= 0 || pos + 4 + naluLen > payload.Length)
                    return false;

                payload[pos] = 0;
                payload[pos + 1] = 0;
                payload[pos + 2] = 0;
                payload[pos + 3] = 1;
                pos += 4 + naluLen;

                if (pos == payload.Length)
                    return true;
            }
            return pos == payload.Length;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Builds a decoder-ready packet: converts to Annex-B and, for IDR
    /// frames (NAL type 5), prepends the cached SPS/PPS.
    /// </summary>
    public static byte[]? BuildDecoderPacket(byte[] payload, byte[]? spsPps, out bool isIdr)
    {
        isIdr = false;
        if (payload == null || payload.Length < 5)
            return null;

        if (!AvccToAnnexB(payload))
            return null;

        int nalType = payload[4] & 0x1F;
        isIdr = nalType == 5;

        if (isIdr && spsPps != null && spsPps.Length > 0)
        {
            var combined = new byte[spsPps.Length + payload.Length];
            Buffer.BlockCopy(spsPps, 0, combined, 0, spsPps.Length);
            Buffer.BlockCopy(payload, 0, combined, spsPps.Length, payload.Length);
            return combined;
        }

        return payload;
    }

    /// <summary>
    /// Parses width/height from an SPS NALU (Annex-B, NAL type 7).
    /// Used to detect portrait/landscape switches. Best effort: returns null on failure.
    /// </summary>
    public static (int Width, int Height)? ParseSpsResolution(byte[] spsPps)
    {
        try
        {
            // Find first SPS NAL (type 7) after a start code.
            for (int i = 0; i + 4 < spsPps.Length; i++)
            {
                if (spsPps[i] == 0 && spsPps[i + 1] == 0 && spsPps[i + 2] == 0 && spsPps[i + 3] == 1)
                {
                    int nalStart = i + 4;
                    if (nalStart < spsPps.Length && (spsPps[nalStart] & 0x1F) == 7)
                    {
                        int len = 0;
                        for (int j = nalStart; j + 4 < spsPps.Length; j++)
                        {
                            if (spsPps[j] == 0 && spsPps[j + 1] == 0 && spsPps[j + 2] == 0 && spsPps[j + 3] == 1)
                            {
                                len = j - nalStart;
                                break;
                            }
                        }
                        if (len == 0)
                            len = spsPps.Length - nalStart;
                        var rbsp = NalToRbsp(spsPps, nalStart, len);
                        return ParseSpsRbsp(rbsp);
                    }
                }
            }
        }
        catch { }
        return null;
    }

    private static byte[] NalToRbsp(byte[] data, int offset, int length)
    {
        var output = new List<byte>(length);
        for (int i = 0; i < length; i++)
        {
            if (i + 2 < length && data[offset + i] == 0 && data[offset + i + 1] == 0 && data[offset + i + 2] == 3)
            {
                output.Add(0);
                output.Add(0);
                i += 2;
                continue;
            }
            output.Add(data[offset + i]);
        }
        return output.ToArray();
    }

    private static (int Width, int Height)? ParseSpsRbsp(byte[] rbsp)
    {
        try
        {
            var br = new BitReader(rbsp);
            br.SkipBits(8); // NAL header
            _ = br.ReadBits(8); // profile_idc
            br.SkipBits(8); // constraint flags + reserved
            _ = br.ReadBits(8); // level_idc
            _ = br.ReadUe(); // seq_parameter_set_id
            // Only Baseline/Main profiles reach here in practice for AirPlay.
            _ = br.ReadUe(); // log2_max_frame_num_minus4
            int picOrderCntType = br.ReadUe();
            if (picOrderCntType == 0)
                _ = br.ReadUe(); // log2_max_pic_order_cnt_lsb_minus4
            else if (picOrderCntType == 1)
            {
                br.SkipBits(1); // delta_pic_order_always_zero_flag
                _ = br.ReadSe();
                _ = br.ReadSe();
                int n = br.ReadUe();
                for (int i = 0; i < n; i++) _ = br.ReadSe();
            }
            _ = br.ReadUe(); // num_ref_frames
            br.SkipBits(1); // gaps_in_frame_num_value_allowed_flag
            int picWidthInMbs = br.ReadUe() + 1;
            int picHeightInMapUnits = br.ReadUe() + 1;
            int frameMbsOnly = br.ReadBits(1);
            if (frameMbsOnly == 0)
                br.SkipBits(1); // mb_adaptive_frame_field_flag
            br.SkipBits(1); // direct_8x8_inference_flag
            int frameCropping = br.ReadBits(1);
            int cropLeft = 0, cropRight = 0, cropTop = 0, cropBottom = 0;
            if (frameCropping != 0)
            {
                cropLeft = br.ReadUe();
                cropRight = br.ReadUe();
                cropTop = br.ReadUe();
                cropBottom = br.ReadUe();
            }
            int width = picWidthInMbs * 16 - (cropLeft + cropRight) * 2;
            int height = (2 - frameMbsOnly) * picHeightInMapUnits * 16 - (cropTop + cropBottom) * 2;
            if (width > 0 && height > 0 && width <= 8192 && height <= 8192)
                return (width, height);
        }
        catch { }
        return null;
    }

    private sealed class BitReader
    {
        private readonly byte[] _data;
        private int _bytePos;
        private int _bitPos;

        public BitReader(byte[] data) { _data = data; }

        public int ReadBits(int n)
        {
            int value = 0;
            for (int i = 0; i < n; i++)
            {
                if (_bytePos >= _data.Length)
                    throw new InvalidOperationException("SPS overrun");
                value <<= 1;
                value |= (_data[_bytePos] >> (7 - _bitPos)) & 1;
                _bitPos++;
                if (_bitPos == 8)
                {
                    _bitPos = 0;
                    _bytePos++;
                }
            }
            return value;
        }

        public void SkipBits(int n)
        {
            for (int i = 0; i < n; i++)
                ReadBits(1);
        }

        public int ReadUe()
        {
            int zeros = 0;
            while (ReadBits(1) == 0)
            {
                zeros++;
                if (zeros > 32)
                    throw new InvalidOperationException("Invalid UE code");
            }
            int value = 1;
            for (int i = 0; i < zeros; i++)
                value = (value << 1) | ReadBits(1);
            return value - 1;
        }

        public int ReadSe()
        {
            int ue = ReadUe();
            int sign = ((ue & 1) == 0) ? -1 : 1;
            return sign * ((ue + 1) >> 1);
        }
    }
}
