using Egami.Nal;

namespace Egami.Video.Codecs.H264;

/// <summary>
/// The twelve scaling lists of an SPS or PPS (H.264 7.3.2.1.1.1, 7.4.2.1.1.1): six 4x4 lists (intra Y, Cb, Cr, inter Y, Cb, Cr)
/// and up to six 8x8 lists (intra Y, inter Y, then Cb and Cr for 4:4:4), in raster order.
/// </summary>
internal sealed class H264ScalingLists
{
    /// <summary>The 4x4 zigzag scan: raster positions in scan order.</summary>
    internal static readonly byte[] Zigzag4x4 = [0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15];

    /// <summary>The 8x8 zigzag scan: raster positions in scan order.</summary>
    internal static readonly byte[] Zigzag8x8 =
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
    ];

    // Table 7-3 and 7-4, in raster order.
    private static readonly byte[] Default4x4Intra = [6, 13, 20, 28, 13, 20, 28, 32, 20, 28, 32, 37, 28, 32, 37, 42];
    private static readonly byte[] Default4x4Inter = [10, 14, 20, 24, 14, 20, 24, 27, 20, 24, 27, 30, 24, 27, 30, 34];

    private static readonly byte[] Default8x8Intra =
    [
        6, 10, 13, 16, 18, 23, 25, 27, 10, 11, 16, 18, 23, 25, 27, 29, 13, 16, 18, 23, 25, 27, 29, 31, 16, 18, 23, 25, 27, 29, 31, 33,
        18, 23, 25, 27, 29, 31, 33, 36, 23, 25, 27, 29, 31, 33, 36, 38, 25, 27, 29, 31, 33, 36, 38, 40, 27, 29, 31, 33, 36, 38, 40, 42,
    ];

    private static readonly byte[] Default8x8Inter =
    [
        9, 13, 15, 17, 19, 21, 22, 24, 13, 13, 17, 19, 21, 22, 24, 25, 15, 17, 19, 21, 22, 24, 25, 27, 17, 19, 21, 22, 24, 25, 27, 28,
        19, 21, 22, 24, 25, 27, 28, 30, 21, 22, 24, 25, 27, 28, 30, 32, 22, 24, 25, 27, 28, 30, 32, 33, 24, 25, 27, 28, 30, 32, 33, 35,
    ];

    private static readonly byte[] Flat4x4 = [.. Enumerable.Repeat((byte)16, 16)];
    private static readonly byte[] Flat8x8 = [.. Enumerable.Repeat((byte)16, 64)];

    /// <summary>The flat lists of a stream with no scaling matrix.</summary>
    internal static readonly H264ScalingLists Flat = new([Flat4x4, Flat4x4, Flat4x4, Flat4x4, Flat4x4, Flat4x4], [Flat8x8, Flat8x8, Flat8x8, Flat8x8, Flat8x8, Flat8x8]);

    private H264ScalingLists(byte[][] list4x4, byte[][] list8x8)
    {
        List4x4 = list4x4;
        List8x8 = list8x8;
    }

    /// <summary>Gets the 4x4 lists, in raster order.</summary>
    internal byte[][] List4x4 { get; }

    /// <summary>Gets the 8x8 lists, in raster order.</summary>
    internal byte[][] List8x8 { get; }

    /// <summary>
    /// Reads count lists. Absent lists fall back by rule A (an SPS: the defaults) or rule B (a PPS: the SPS's lists,
    /// <paramref name="sequence"/>, or flat when the SPS has none).
    /// </summary>
    internal static H264ScalingLists Read(NalBitReader r, int count, H264ScalingLists? sequence, bool ruleA)
    {
        byte[][] l4 = new byte[6][];
        byte[][] l8 = new byte[6][];
        sequence ??= Flat;
        for (int i = 0; i < 12; i++)
        {
            bool present = i < count && r.Flag("scaling_list_present_flag");
            bool small = i < 6;
            byte[]? list = null;
            if (present)
            {
                list = ReadList(r, small ? 16 : 64, out bool useDefault);
                if (useDefault)
                {
                    list = small ? (i < 3 ? Default4x4Intra : Default4x4Inter) : ((i - 6) % 2 == 0 ? Default8x8Intra : Default8x8Inter);
                }
            }
            else
            {
                // Fall-back rules A and B (Table 7-2): the first list of each kind falls back to the default or sequence-level list,
                // the others to the previous list of the same kind.
                list = i switch
                {
                    0 => ruleA ? Default4x4Intra : sequence.List4x4[0],
                    3 => ruleA ? Default4x4Inter : sequence.List4x4[3],
                    1 or 2 or 4 or 5 => l4[i - 1],
                    6 => ruleA ? Default8x8Intra : sequence.List8x8[0],
                    7 => ruleA ? Default8x8Inter : sequence.List8x8[1],
                    _ => l8[i - 8],
                };
            }

            if (small)
            {
                l4[i] = list!;
            }
            else
            {
                l8[i - 6] = list!;
            }
        }

        return new H264ScalingLists(l4, l8);
    }

    private static byte[] ReadList(NalBitReader r, int size, out bool useDefault)
    {
        byte[] list = new byte[size];
        byte[] scan = size == 16 ? Zigzag4x4 : Zigzag8x8;
        int last = 8, next = 8;
        useDefault = false;
        for (int j = 0; j < size; j++)
        {
            if (next != 0)
            {
                int delta = r.Se("delta_scale", -128, 127);
                next = (last + delta + 256) % 256;
                useDefault = j == 0 && next == 0;
            }

            list[scan[j]] = (byte)(next == 0 ? last : next);
            last = list[scan[j]];
        }

        return list;
    }
}
