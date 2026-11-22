using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

using Egami.Nal;

namespace Egami.Heic.Hevc;

/// <summary>Whether a CTB's SAO parameters are coded as a merge with the CTB to the left or above.</summary>
internal readonly record struct HevcSaoChoice(bool MergeLeft, bool MergeUp);

/// <summary>
/// The SAO search (in the manner of the JCT-VC encoder description): per CTB and component, statistics of the source minus the
/// deblocked samples for each edge offset class and category and each band, then the parameters of least J, the change in squared
/// error plus lambda times their bins: off, one of the four edge classes with an offset per category, or four bands from a band
/// position. Cb and Cr share the type and edge class. Last, a merge with the CTB to the left or above wins when its parameters
/// cost less here than new ones.
/// </summary>
internal static class HevcSaoSearch
{
    /// <summary>The neighbours of each edge offset class, (dx, dy) twice, as the loop filter takes them.</summary>
    private static readonly int[][] Directions = [[-1, 0, 1, 0], [0, -1, 0, 1], [-1, -1, 1, 1], [1, -1, -1, 1]];

    /// <summary>Chooses every CTB's SAO parameters into <see cref="HevcPicture.Sao"/>; gives the merge choices.</summary>
    internal static HevcSaoChoice[] Choose(HevcPicture picture, HevcSource source, HevcSliceHeader slice, int parallelism, CancellationToken cancellationToken)
    {
        HevcSps sps = picture.Sps;
        int ctbs = sps.WidthInCtbs * sps.HeightInCtbs;
        int components = picture.Planes.Length;
        Statistics[] stats = new Statistics[ctbs * components];
        HevcThreads.For(sps.HeightInCtbs, parallelism, row =>
        {
            for (int ctb = row * sps.WidthInCtbs; ctb < (row + 1) * sps.WidthInCtbs; ctb++)
            {
                for (int c = 0; c < components; c++)
                {
                    stats[(ctb * components) + c] = Gather(picture, source, c, ctb);
                }
            }
        }, cancellationToken);

        double lambda = 0.57 * Math.Pow(2, (slice.SliceQp + sps.QpBdOffsetLuma - 12) / 3.0);
        double[] weights = new double[components];
        weights[0] = 1;
        for (int c = 1; c < components; c++)
        {
            int offset = c == 1 ? picture.Pps.CbQpOffset + slice.CbQpOffset : picture.Pps.CrQpOffset + slice.CrQpOffset;
            int qpi = Math.Clamp(slice.SliceQp + offset, -sps.QpBdOffsetChroma, 57);
            int qpc = sps.ChromaArrayType == 1 ? HevcSliceDecoder.ChromaQp(qpi) : Math.Min(qpi, 51);
            weights[c] = Math.Pow(2, (slice.SliceQp - qpc) / 3.0);
        }

        HevcBins bins = new();
        HevcSyntaxWriter writer = new(picture, slice, bins);
        byte[] initial = new byte[HevcContexts.Count];
        HevcContexts.Initialize(initial, slice.SliceQp);
        HevcSaoChoice[] choices = new HevcSaoChoice[ctbs];
        for (int ctb = 0; ctb < ctbs; ctb++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadOnlySpan<Statistics> here = stats.AsSpan(ctb * components, components);
            HevcSaoParameters best = Best(sps, here, weights, lambda, ctb, writer, bins, initial, out double bestCost);
            bestCost += lambda * Bits(writer, bins, initial, ctb, best, false, false);
            HevcSaoChoice choice = default;
            int rx = ctb % sps.WidthInCtbs, ry = ctb / sps.WidthInCtbs;
            if (rx > 0)
            {
                HevcSaoParameters left = picture.Sao[ctb - 1];
                double cost = Change(sps, here, weights, left) + (lambda * Bits(writer, bins, initial, ctb, left, true, false));
                if (cost < bestCost)
                {
                    (bestCost, best, choice) = (cost, left, new HevcSaoChoice(true, false));
                }
            }

            if (ry > 0)
            {
                HevcSaoParameters up = picture.Sao[ctb - sps.WidthInCtbs];
                double cost = Change(sps, here, weights, up) + (lambda * Bits(writer, bins, initial, ctb, up, false, true));
                if (cost < bestCost)
                {
                    (best, choice) = (up, new HevcSaoChoice(false, true));
                }
            }

            picture.Sao[ctb] = best;
            choices[ctb] = choice;
        }

        return choices;
    }

    /// <summary>The bits of a CTB's SAO syntax, from fixed contexts, in bits.</summary>
    private static double Bits(HevcSyntaxWriter writer, HevcBins bins, byte[] initial, int ctb, in HevcSaoParameters sao, bool mergeLeft, bool mergeUp)
    {
        Array.Copy(initial, bins.Contexts, HevcContexts.Count);
        bins.Cost = 0;
        writer.Sao(ctb, sao, mergeLeft, mergeUp);
        return bins.Cost / (double)CabacCost.Bit;
    }

    /// <summary>The best new parameters of a CTB: luma, then Cb and Cr together; gives their change in weighted squared error.</summary>
    private static HevcSaoParameters Best(HevcSps sps, ReadOnlySpan<Statistics> stats, double[] weights, double lambda, int ctb, HevcSyntaxWriter writer,
        HevcBins bins, byte[] initial, out double distortion)
    {
        HevcSaoParameters sao = default;
        distortion = 0;
        int cMax = (1 << (Math.Min(sps.BitDepthLuma, 10) - 5)) - 1;

        // Luma: off, an edge class or bands.
        double bestLuma = 0, bestLumaJ = lambda * LumaBits(writer, bins, initial, ctb, default);
        for (int type = 1; type <= 2; type++)
        {
            for (int k = 0; k < (type == 2 ? 4 : 1); k++)
            {
                (HevcSaoParameters.OffsetSet offsets, int saoClass, double change) = Offsets(stats[0], type, k, cMax, lambda);
                HevcSaoParameters candidate = default;
                (candidate.TypeY, candidate.ClassY, candidate.OffsetsY) = (type, saoClass, offsets);
                double j = change + (lambda * LumaBits(writer, bins, initial, ctb, candidate));
                if (j < bestLumaJ)
                {
                    (bestLumaJ, bestLuma) = (j, change);
                    (sao.TypeY, sao.ClassY, sao.OffsetsY) = (type, saoClass, offsets);
                }
            }
        }

        distortion += bestLuma;
        if (stats.Length == 1)
        {
            return sao;
        }

        // Chroma: one type and edge class for both.
        int cMaxC = (1 << (Math.Min(sps.BitDepthChroma, 10) - 5)) - 1;
        HevcSaoParameters chroma = default;
        double bestChroma = 0, bestChromaJ = lambda * ChromaBits(writer, bins, initial, ctb, default);
        for (int type = 1; type <= 2; type++)
        {
            for (int k = 0; k < (type == 2 ? 4 : 1); k++)
            {
                (HevcSaoParameters.OffsetSet cb, int cbClass, double cbChange) = Offsets(stats[1], type, k, cMaxC, lambda / weights[1]);
                (HevcSaoParameters.OffsetSet cr, int crClass, double crChange) = Offsets(stats[2], type, k, cMaxC, lambda / weights[2]);
                HevcSaoParameters candidate = default;
                (candidate.TypeCb, candidate.ClassCb, candidate.OffsetsCb) = (type, cbClass, cb);
                (candidate.TypeCr, candidate.ClassCr, candidate.OffsetsCr) = (type, type == 2 ? cbClass : crClass, cr);
                double change = (weights[1] * cbChange) + (weights[2] * crChange);
                double j = change + (lambda * ChromaBits(writer, bins, initial, ctb, candidate));
                if (j < bestChromaJ)
                {
                    (bestChromaJ, bestChroma, chroma) = (j, change, candidate);
                }
            }
        }

        (sao.TypeCb, sao.ClassCb, sao.OffsetsCb) = (chroma.TypeCb, chroma.ClassCb, chroma.OffsetsCb);
        (sao.TypeCr, sao.ClassCr, sao.OffsetsCr) = (chroma.TypeCr, chroma.ClassCr, chroma.OffsetsCr);
        distortion += bestChroma;
        return sao;
    }

    private static double LumaBits(HevcSyntaxWriter writer, HevcBins bins, byte[] initial, int ctb, HevcSaoParameters luma) =>
        Bits(writer, bins, initial, ctb, luma, false, false);

    private static double ChromaBits(HevcSyntaxWriter writer, HevcBins bins, byte[] initial, int ctb, HevcSaoParameters chroma) =>
        Bits(writer, bins, initial, ctb, chroma, false, false);

    /// <summary>
    /// The offsets of least J for one type (1 bands, 2 edges) and edge class: each category's mean error rounded, then moved
    /// towards zero while that pays; for bands the best four in a row. Gives the offsets, the band position or class, and the change
    /// in squared error.
    /// </summary>
    private static (HevcSaoParameters.OffsetSet Offsets, int Class, double Change) Offsets(in Statistics stats, int type, int edgeClass, int cMax, double lambda)
    {
        HevcSaoParameters.OffsetSet offsets = default;
        if (type == 2)
        {
            double change = 0;
            for (int category = 0; category < 4; category++)
            {
                long count = stats.EdgeCount[(edgeClass * 4) + category];
                long sum = stats.EdgeSum[(edgeClass * 4) + category];

                // Valleys (categories 1 and 2) take offsets of 0 or more, peaks (3 and 4) 0 or less.
                int sign = category < 2 ? 1 : -1;
                (int offset, _) = BestOffset(count, sign * sum, cMax, lambda, false);
                offsets[category] = sign * offset;
                change += Delta(count, sum, sign * offset);
            }

            return (offsets, edgeClass, change);
        }

        Span<double> bandChange = stackalloc double[32];
        Span<int> bandOffset = stackalloc int[32];
        for (int band = 0; band < 32; band++)
        {
            (int offset, double delta) = BestOffset(stats.BandCount[band], Math.Abs(stats.BandSum[band]), cMax, lambda, true);
            bandOffset[band] = stats.BandSum[band] < 0 ? -offset : offset;
            bandChange[band] = delta;
        }

        double best = double.MaxValue;
        int position = 0;
        for (int start = 0; start < 32; start++)
        {
            double change = bandChange[start] + bandChange[(start + 1) & 31] + bandChange[(start + 2) & 31] + bandChange[(start + 3) & 31];
            if (change < best)
            {
                (best, position) = (change, start);
            }
        }

        for (int k = 0; k < 4; k++)
        {
            offsets[k] = bandOffset[(position + k) & 31];
        }

        // The J of the bands chose them; give back the change in error alone.
        double distortion = 0;
        for (int k = 0; k < 4; k++)
        {
            int band = (position + k) & 31;
            distortion += Delta(stats.BandCount[band], stats.BandSum[band], offsets[k]);
        }

        return (offsets, position, distortion);
    }

    /// <summary>
    /// The magnitude 0 to cMax of least J for a category of <paramref name="count"/> samples whose errors sum to
    /// <paramref name="sum"/> (towards the offset's sign), and that J less the rate of 0.
    /// </summary>
    private static (int Offset, double Cost) BestOffset(long count, long sum, int cMax, double lambda, bool signed)
    {
        if (count == 0 || sum <= 0)
        {
            return (0, 0);
        }

        int start = (int)Math.Min(cMax, (sum + (count / 2)) / count);
        double best = 0;
        int bestOffset = 0;
        for (int offset = start; offset > 0; offset--)
        {
            double bits = Math.Min(offset + 1, cMax) + (signed ? 1 : 0) - 1;
            double j = Delta(count, sum, offset) + (lambda * bits);
            if (j < best)
            {
                (best, bestOffset) = (j, offset);
            }
        }

        return (bestOffset, best);
    }

    /// <summary>The change in squared error when an offset is added to samples whose errors sum to <paramref name="sum"/>.</summary>
    private static double Delta(long count, long sum, int offset) => ((double)count * offset * offset) - (2.0 * offset * sum);

    /// <summary>The change in weighted squared error of giving a CTB the parameters <paramref name="sao"/> (as a merge does).</summary>
    private static double Change(HevcSps sps, ReadOnlySpan<Statistics> stats, double[] weights, in HevcSaoParameters sao)
    {
        double change = 0;
        for (int c = 0; c < stats.Length; c++)
        {
            int type = sao.Type(c);
            HevcSaoParameters.OffsetSet offsets = sao.Offsets(c);
            double delta = 0;
            if (type == 2)
            {
                for (int category = 0; category < 4; category++)
                {
                    int i = (sao.Class(c) * 4) + category;
                    delta += Delta(stats[c].EdgeCount[i], stats[c].EdgeSum[i], offsets[category]);
                }
            }
            else if (type == 1)
            {
                for (int k = 0; k < 4; k++)
                {
                    int band = (sao.Class(c) + k) & 31;
                    delta += Delta(stats[c].BandCount[band], stats[c].BandSum[band], offsets[k]);
                }
            }

            change += weights[c] * delta;
        }

        return change;
    }

    /// <summary>
    /// The statistics of one CTB and component: per edge class and category (1 to 4, at 0 to 3) and per band, how many deblocked
    /// samples fall in it and the sum of the source minus them.
    /// </summary>
    private sealed class Statistics
    {
        internal long[] EdgeCount { get; } = new long[16];

        internal long[] EdgeSum { get; } = new long[16];

        internal long[] BandCount { get; } = new long[32];

        internal long[] BandSum { get; } = new long[32];
    }

    private static Statistics Gather(HevcPicture picture, HevcSource source, int c, int ctb)
    {
        HevcSps sps = picture.Sps;
        int sx = c == 0 ? 1 : sps.SubWidthC, sy = c == 0 ? 1 : sps.SubHeightC;
        int ctbSize = 1 << sps.CtbLog2Size;
        int width = picture.Widths[c], height = picture.Heights[c];
        int x0 = ((ctb % sps.WidthInCtbs) * ctbSize) / sx, y0 = ((ctb / sps.WidthInCtbs) * ctbSize) / sy;
        int w = Math.Min(ctbSize / sx, width - x0), h = Math.Min(ctbSize / sy, height - y0);
        int bitDepth = c == 0 ? sps.BitDepthLuma : sps.BitDepthChroma;
        int shift = bitDepth - 5;
        ushort[] deblocked = picture.Planes[c], original = source.Planes[c];
        Statistics stats = new();
        Span<long> edgeCount = stackalloc long[16], edgeSum = stackalloc long[16];
        edgeCount.Clear();
        edgeSum.Clear();
        long[] bandCount = stats.BandCount, bandSum = stats.BandSum;
        for (int y = y0; y < y0 + h; y++)
        {
            int row = y * width;
            for (int x = x0; x < x0 + w; x++)
            {
                int value = deblocked[row + x];
                int band = value >> shift;
                bandCount[band]++;
                bandSum[band] += original[row + x] - value;
            }

            // Each class's neighbours, the samples whose neighbours are outside the picture left out.
            for (int k = 0; k < 4; k++)
            {
                int[] d = Directions[k];
                if (d[1] != 0 && (y == 0 || y == height - 1))
                {
                    continue;
                }

                int xs = x0, xe = x0 + w;
                if (d[0] != 0)
                {
                    xs = Math.Max(xs, 1);
                    xe = Math.Min(xe, width - 1);
                }

                EdgeRow(deblocked, original, row, (d[1] * width) + d[0], (d[3] * width) + d[2], xs, xe, bitDepth,
                    edgeCount.Slice(k * 4, 4), edgeSum.Slice(k * 4, 4));
            }
        }

        for (int i = 0; i < 16; i++)
        {
            stats.EdgeCount[i] = edgeCount[i];
            stats.EdgeSum[i] = edgeSum[i];
        }

        return stats;
    }

    /// <summary>
    /// One row's edge statistics of a class from <paramref name="xs"/> to <paramref name="xe"/>: the neighbours at offsets
    /// <paramref name="a"/> and <paramref name="b"/>; categories 1 to 4 (edge index 0, 1, 3, 4) counted at 0 to 3.
    /// </summary>
    private static void EdgeRow(ushort[] deblocked, ushort[] original, int row, int a, int b, int xs, int xe, int bitDepth, Span<long> count,
        Span<long> sum)
    {
        int x = xs;
        if (Vector256.IsHardwareAccelerated && bitDepth <= 12 && xe - xs >= 16)
        {
            // Sixteen samples at a time, the sums in 16-bit lanes for at most four steps (|error| < 4096), then widened.
            ref short deb = ref Unsafe.As<ushort, short>(ref MemoryMarshal.GetArrayDataReference(deblocked));
            ref short org = ref Unsafe.As<ushort, short>(ref MemoryMarshal.GetArrayDataReference(original));
            Vector256<short> two = Vector256.Create((short)2), one = Vector256<short>.One, three = Vector256.Create((short)3), four = Vector256.Create((short)4);
            Vector256<short> c0 = default, c1 = default, c2 = default, c3 = default;
            Vector256<int> s0 = default, s1 = default, s2 = default, s3 = default;
            while (x + 16 <= xe)
            {
                Vector256<short> s16_0 = default, s16_1 = default, s16_2 = default, s16_3 = default;
                for (int step = 0; step < 4 && x + 16 <= xe; step++, x += 16)
                {
                    int i = row + x;
                    Vector256<short> v = Vector256.LoadUnsafe(ref deb, (nuint)i);
                    Vector256<short> va = Vector256.LoadUnsafe(ref deb, (nuint)(i + a));
                    Vector256<short> vb = Vector256.LoadUnsafe(ref deb, (nuint)(i + b));
                    Vector256<short> error = Vector256.LoadUnsafe(ref org, (nuint)i) - v;

                    // 2 + sign(v - a) + sign(v - b), the comparisons' masks being -1.
                    Vector256<short> edge = two - Vector256.GreaterThan(v, va) + Vector256.LessThan(v, va) - Vector256.GreaterThan(v, vb) + Vector256.LessThan(v, vb);
                    Vector256<short> m0 = Vector256.Equals(edge, Vector256<short>.Zero), m1 = Vector256.Equals(edge, one);
                    Vector256<short> m3 = Vector256.Equals(edge, three), m4 = Vector256.Equals(edge, four);
                    c0 -= m0;
                    c1 -= m1;
                    c2 -= m3;
                    c3 -= m4;
                    s16_0 += error & m0;
                    s16_1 += error & m1;
                    s16_2 += error & m3;
                    s16_3 += error & m4;
                }

                s0 += Vector256.WidenLower(s16_0) + Vector256.WidenUpper(s16_0);
                s1 += Vector256.WidenLower(s16_1) + Vector256.WidenUpper(s16_1);
                s2 += Vector256.WidenLower(s16_2) + Vector256.WidenUpper(s16_2);
                s3 += Vector256.WidenLower(s16_3) + Vector256.WidenUpper(s16_3);
            }

            // A row has at most 64 / 16 steps, so the 16-bit counts cannot overflow.
            count[0] += Vector256.Sum(Vector256.WidenLower(c0) + Vector256.WidenUpper(c0));
            count[1] += Vector256.Sum(Vector256.WidenLower(c1) + Vector256.WidenUpper(c1));
            count[2] += Vector256.Sum(Vector256.WidenLower(c2) + Vector256.WidenUpper(c2));
            count[3] += Vector256.Sum(Vector256.WidenLower(c3) + Vector256.WidenUpper(c3));
            sum[0] += Vector256.Sum(s0);
            sum[1] += Vector256.Sum(s1);
            sum[2] += Vector256.Sum(s2);
            sum[3] += Vector256.Sum(s3);
        }

        for (; x < xe; x++)
        {
            int i = row + x;
            int value = deblocked[i];
            int edge = 2 + Math.Sign(value - deblocked[i + a]) + Math.Sign(value - deblocked[i + b]);
            if (edge != 2)
            {
                int at = edge < 2 ? edge : edge - 1;
                count[at]++;
                sum[at] += original[i] - value;
            }
        }
    }
}
