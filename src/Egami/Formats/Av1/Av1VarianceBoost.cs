namespace Egami.Formats.Av1;

/// <summary>
/// Tune IQ's Variance Boost (libaom's av1_get_sbq_variance_boost, allintra_vis.c, and av1_get_variance_boost_block_variance,
/// aq_variance.c; BSD 2-clause, see THIRD-PARTY-NOTICES): each 64x64 superblock's quantizer from the variance of its 8x8 luma
/// blocks around the fifth octile, so flat and finely graded areas, where banding and blocking show, get finer steps.
/// </summary>
internal static class Av1VarianceBoost
{
    private const int MaxDeltaQRange = 80;
    private const double MaxBoost = 8.0;

    /// <summary>Gets the delta_q_res, as a power of 2, the base quantizer codes its superblocks' deltas in.</summary>
    internal static int DeltaQResLog2(int baseQIndex) => baseQIndex >= 160 ? 3 : baseQIndex >= 120 ? 2 : baseQIndex >= 80 ? 1 : 0;

    /// <summary>Gets each 64x64 superblock's quantizer, in raster order, from the luma plane padded to whole superblocks.</summary>
    internal static int[] SuperblockQIndices(ushort[] luma, int stride, int sbCols, int sbRows, int baseQIndex, int bitDepth)
    {
        int[] qindices = new int[sbCols * sbRows];
        Span<int> variances = stackalloc int[64];
        double baseQ = Q(baseQIndex, bitDepth);
        for (int sbRow = 0; sbRow < sbRows; sbRow++)
        {
            for (int sbCol = 0; sbCol < sbCols; sbCol++)
            {
                for (int i = 0; i < 64; i++)
                {
                    variances[i] = Variance8x8(luma, (((sbRow * 64) + ((i >> 3) * 8)) * stride) + (sbCol * 64) + ((i & 7) * 8), stride, bitDepth) / 64;
                }

                // The fifth octile, with its neighbours weighed 1:2:1.
                variances.Sort();
                int variance = Math.Max(1, (variances[31] + (variances[39] * 2) + variances[47] + 2) / 4);
                double ratio = Math.Clamp((0.15 * 3.0 * (10.0 - Math.Log2(variance))) + 1.0, 1.0, MaxBoost);
                double targetQ = baseQ / ratio;
                int target = 0;
                while (target < 255 && Q(target, bitDepth) < targetQ)
                {
                    target++;
                }

                int boost = Math.Min(MaxDeltaQRange, (int)Math.Round((baseQIndex + 544.0) * (baseQIndex - target) / 1279.0, MidpointRounding.AwayFromZero));
                qindices[(sbRow * sbCols) + sbCol] = Math.Max(baseQIndex - boost, 1);
            }
        }

        return qindices;
    }

    /// <summary>
    /// Rounds a superblock's quantizer to a whole number of delta_q_res steps from the one before, with a dead zone of a quarter
    /// step (av1_adjust_q_from_delta_q_res, rd.c).
    /// </summary>
    internal static int Adjust(int resLog2, int previous, int qindex)
    {
        int res = 1 << resLog2;
        qindex = Math.Clamp(qindex, res, 256 - res);
        int sign = qindex - previous >= 0 ? 1 : -1;
        int abs = (Math.Abs(qindex - previous) + (res / 4)) & ~(res - 1);
        return Math.Max(previous + (sign * abs), 1);
    }

    /// <summary>Gets libaom's real quantizer of a qindex: the AC step, scaled to 8 bits (av1_convert_qindex_to_q).</summary>
    private static double Q(int qindex, int bitDepth) => bitDepth switch
    {
        10 => Av1Tables.AcQLookup10[qindex] / 16.0,
        12 => Av1Tables.AcQLookup12[qindex] / 64.0,
        _ => Av1Tables.AcQLookup8[qindex] / 4.0,
    };

    /// <summary>Gets an 8x8 block's variance times 64 at 8-bit scale, as aom_variance8x8 and its high bit depth forms compute it.</summary>
    private static int Variance8x8(ushort[] plane, int at, int stride, int bitDepth)
    {
        long sum = 0, squares = 0;
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                int v = plane[at + (y * stride) + x];
                sum += v;
                squares += v * v;
            }
        }

        if (bitDepth > 8)
        {
            int shift = bitDepth - 8;
            squares = (squares + (1L << ((2 * shift) - 1))) >> (2 * shift);
            sum = (sum + (1L << (shift - 1))) >> shift;
        }

        return (int)Math.Max(0, squares - ((sum * sum) >> 6));
    }
}
