using System.Buffers.Binary;

namespace Egami.Video.Hardware.Encoding;

/// <summary>NAL unit framing for the bytes the driver writes, which are Annex B: each unit after a start code.</summary>
internal static class NalUnits
{
    /// <summary>Returns the ranges of the NAL units in an Annex B byte stream, without their start codes.</summary>
    internal static List<Range> SplitAnnexB(ReadOnlySpan<byte> stream)
    {
        List<Range> units = [];
        int start = -1;
        int i = 0;
        while (i + 2 < stream.Length)
        {
            if (stream[i] == 0 && stream[i + 1] == 0 && stream[i + 2] == 1)
            {
                if (start >= 0)
                {
                    units.Add(new Range(start, TrimZeros(stream, start, i)));
                }

                i += 3;
                start = i;
            }
            else
            {
                i++;
            }
        }

        if (start >= 0 && start < stream.Length)
        {
            units.Add(new Range(start, TrimZeros(stream, start, stream.Length)));
        }

        return units;
    }

    /// <summary>
    /// Rewrites an Annex B access unit with each NAL unit after a 4-byte big-endian size, as MP4 and Matroska store them.
    /// </summary>
    internal static byte[] ToLengthPrefixed(ReadOnlySpan<byte> annexB)
    {
        List<Range> units = SplitAnnexB(annexB);
        int size = 0;
        foreach (Range unit in units)
        {
            size += 4 + unit.GetOffsetAndLength(annexB.Length).Length;
        }

        byte[] result = new byte[size];
        int at = 0;
        foreach (Range unit in units)
        {
            ReadOnlySpan<byte> nal = annexB[unit];
            BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(at), nal.Length);
            nal.CopyTo(result.AsSpan(at + 4));
            at += 4 + nal.Length;
        }

        return result;
    }

    /// <summary>The AVCDecoderConfigurationRecord (ISO/IEC 14496-15) of one SPS and one PPS, NAL headers included.</summary>
    internal static byte[] AvcC(ReadOnlySpan<byte> sps, ReadOnlySpan<byte> pps)
    {
        List<byte> b = [1, sps[1], sps[2], sps[3], 0xFF, 0xE1, (byte)(sps.Length >> 8), (byte)sps.Length];
        b.AddRange(sps);
        b.Add(1);
        b.Add((byte)(pps.Length >> 8));
        b.Add((byte)pps.Length);
        b.AddRange(pps);
        if (sps[1] is 100 or 110 or 122 or 144)
        {
            b.Add(0xFC | 1); // chroma_format 4:2:0
            b.Add(0xF8); // bit_depth_luma_minus8 = 0
            b.Add(0xF8); // bit_depth_chroma_minus8 = 0
            b.Add(0); // numOfSequenceParameterSetExt
        }

        return [.. b];
    }

    /// <summary>The end of a unit without the zero bytes a following four-byte start code or trailing padding leaves.</summary>
    /// <summary>
    /// The HEVC decoder configuration record (hvcC, ISO/IEC 14496-15 8.3.3) of an 8-bit 4:2:0 stream with one temporal layer and
    /// 4-byte NAL unit lengths: the profile, tier and level copied from the SPS, then the VPS, SPS and PPS arrays.
    /// </summary>
    internal static byte[] HvcC(byte[] vps, byte[] sps, byte[] pps)
    {
        // The SPS RBSP after its 2-byte header: 4 bits VPS id, 3 bits sub-layers, 1 bit nesting, then 12 bytes of
        // profile_tier_level: profile space, tier and profile, 32 compatibility flags, 48 constraint flags, level.
        byte[] rbsp = Egami.Nal.NalFraming.Unescape(sps);
        List<byte> b = [1];
        b.AddRange(rbsp.AsSpan(3, 12));
        b.AddRange([0xF0, 0x00, 0xFC, 0xFC | 1, 0xF8, 0xF8, 0, 0, (1 << 3) | (1 << 2) | 3, 3]);
        foreach ((int type, byte[] nal) in new[] { (32, vps), (33, sps), (34, pps) })
        {
            b.Add((byte)(0x80 | type));
            b.Add(0);
            b.Add(1);
            b.Add((byte)(nal.Length >> 8));
            b.Add((byte)nal.Length);
            b.AddRange(nal);
        }

        return [.. b];
    }

    private static int TrimZeros(ReadOnlySpan<byte> stream, int start, int end)
    {
        while (end > start && stream[end - 1] == 0)
        {
            end--;
        }

        return end;
    }
}
