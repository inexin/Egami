using Egami.Pixels;

namespace Egami.Video.Codecs;

/// <summary>
/// Reads the bit depth and chroma subsampling from the H.264 (avcC) and HEVC (hvcC) decoder configuration records that MP4 and
/// Matroska store as a track's codec private data (ISO/IEC 14496-15 sections 5.3.2.1 and 8.3.2.1), so a probe reports them
/// without a decoder.
/// </summary>
internal static class DecoderConfigRecords
{
    /// <summary>Reads the decoded format a track's configuration record states.</summary>
    /// <param name="codecId">The track's codec.</param>
    /// <param name="record">The track's codec private data.</param>
    /// <returns>The bits per sample (0 when unknown) and the chroma subsampling (null when unknown).</returns>
    public static (int BitDepth, ChromaFormat? Chroma) Read(string codecId, ReadOnlySpan<byte> record) => codecId switch
    {
        CodecIds.Hevc => Hevc(record),
        CodecIds.Avc => Avc(record),
        _ => (0, null),
    };

    private static (int BitDepth, ChromaFormat? Chroma) Hevc(ReadOnlySpan<byte> c)
    {
        // configurationVersion 1; then profile, 32 compatibility flags, 48 constraint flags, level, segmentation, parallelism
        // (bytes 1 to 15); byte 16 ends in chroma_format_idc, bytes 17 and 18 in bit_depth_luma_minus8 and bit_depth_chroma_minus8.
        if (c.Length < 23 || c[0] != 1)
        {
            return (0, null);
        }

        int luma = (c[17] & 7) + 8, chroma = (c[18] & 7) + 8;
        return (Math.Max(luma, chroma), Chroma(c[16] & 3));
    }

    private static (int BitDepth, ChromaFormat? Chroma) Avc(ReadOnlySpan<byte> c)
    {
        if (c.Length < 7 || c[0] != 1)
        {
            return (0, null);
        }

        int profile = c[1];

        // Baseline, Main, Extended and High allow only 8-bit 4:2:0 (and High monochrome, which a record cannot tell apart
        // without the sequence parameter set).
        if (profile is 66 or 77 or 88)
        {
            return (8, ChromaFormat.Yuv420);
        }

        // The other High profiles append chroma_format and the bit depths after the parameter sets, when the writer kept them.
        int at = 5, count = c[at++] & 0x1F;
        for (int i = 0; i < count; i++)
        {
            if (!Skip(c, ref at))
            {
                return Fallback(profile);
            }
        }

        if (at >= c.Length)
        {
            return Fallback(profile);
        }

        count = c[at++];
        for (int i = 0; i < count; i++)
        {
            if (!Skip(c, ref at))
            {
                return Fallback(profile);
            }
        }

        if (profile is 100 or 110 or 122 or 144 && at + 3 <= c.Length)
        {
            int luma = (c[at + 1] & 7) + 8, chroma = (c[at + 2] & 7) + 8;
            return (Math.Max(luma, chroma), Chroma(c[at] & 3));
        }

        return Fallback(profile);
    }

    private static (int BitDepth, ChromaFormat? Chroma) Fallback(int profile) => profile == 100 ? (8, ChromaFormat.Yuv420) : (0, null);

    private static bool Skip(ReadOnlySpan<byte> c, ref int at)
    {
        if (at + 2 > c.Length)
        {
            return false;
        }

        int length = (c[at] << 8) | c[at + 1];
        at += 2 + length;
        return at <= c.Length;
    }

    private static ChromaFormat Chroma(int idc) => idc switch
    {
        0 => ChromaFormat.Yuv400,
        1 => ChromaFormat.Yuv420,
        2 => ChromaFormat.Yuv422,
        _ => ChromaFormat.Yuv444,
    };
}
