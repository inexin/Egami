using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

using Egami.Nal;

namespace Egami.Heic.Hevc;

/// <summary>
/// The trial coding of an inter block (H.264 lessons in the HEVC encoder design: trials are priced statically, RDOQ and sign
/// hiding only on the kept coding): the residual transformed and quantized with the inter dead zone, its distortion worked out in
/// the transform domain (the quantization error of each level, scaled to samples as RDOQ scales it) instead of reconstructing it,
/// and its rate priced from the slice's current context states without moving them. The picture keeps the prediction; the
/// coding unit the search keeps is coded again in full.
/// </summary>
internal sealed partial class HevcCtuSearch
{
    /// <summary>Stands for a trial block's levels, which only say that the block has some: the kept coding replaces them.</summary>
    private static readonly short[] TrialLevels = new short[1];

    private readonly short[] _trialLevels = new short[32 * 32];
    private readonly int[] _trialRaster = new int[32 * 32];

    /// <summary>The diagonal scan position of each raster position, by log2 (the inverse of <see cref="ScanRasters"/>).</summary>
    /// <remarks>In a class of its own, made on first use, after <see cref="ScanRasters"/> (another file's static field).</remarks>
    private static class ScanPositions
    {
        private static readonly ushort[][] Tables = Make();

        internal static ushort[] Of(int log2) => Tables[log2];

        private static ushort[][] Make()
        {
            ushort[][] tables = new ushort[6][];
            for (int log2 = 2; log2 <= 5; log2++)
            {
                int[] rasters = ScanRasters[log2];
                tables[log2] = new ushort[rasters.Length];
                for (int s = 0; s < rasters.Length; s++)
                {
                    tables[log2][rasters[s]] = (ushort)s;
                }
            }

            return tables;
        }
    }

    /// <summary>A trial coding of one inter block over the prediction in the picture, which it leaves as it is.</summary>
    [SkipLocalsInit]
    private BlockResult TrialBlock(int c, int x, int y, int log2, int qp, double lambda, double weight)
    {
        int n = 1 << log2, count = n * n;
        ushort[] plane = _picture.Planes[c], source = _source.Planes[c];
        int stride = _picture.Widths[c];
        int at = (y * stride) + x;
        long predicted = Residual(source, plane, at, stride, n, _residual);
        if (predicted == 0)
        {
            return new BlockResult(null, false, 0, 0);
        }

        int bitDepth = c == 0 ? _sps.BitDepthLuma : _sps.BitDepthChroma;
        HevcForward.Transform(_residual, _coefficients, log2, bitDepth, false);

        // Quantization with the inter dead zone (1/6), and the distortion each level takes away from leaving the coefficient out.
        int qpc = Qp(c, qp);
        int scale = HevcForward.QuantScale[qpc % 6];
        int shift = HevcForward.QuantShift(qpc, log2, bitDepth);
        long offset = 85L << (shift - 9);
        double errScale = Math.ScaleB(1.0 / ((double)scale * scale), -2 * (15 - bitDepth - log2));
        int last = Avx2.IsSupported
            ? QuantizeAvx2(_coefficients, _trialRaster, count, log2, scale, (int)offset, shift, out double saved)
            : QuantizeScalar(_coefficients, _trialRaster, count, log2, scale, offset, shift, out saved);
        if (last < 0)
        {
            return new BlockResult(null, false, weight * predicted, 0);
        }

        // The levels to the last one's sub-block in scan order (inter blocks scan diagonally): each sub-block's 16 in a row.
        short[] levels = _trialLevels;
        int[] rasters = ScanRasters[log2];
        int end = ((last >> 4) + 1) << 4;
        for (int i = 0; i < end; i++)
        {
            levels[i] = (short)_trialRaster[rasters[i]];
        }

        long bits = TrialRate(levels, last, log2, c);
        double distortion = weight * Math.Max(predicted - (saved * errScale), 0);
        return distortion + (lambda * bits / CabacCost.Bit) < weight * predicted
            ? new BlockResult(TrialLevels, false, distortion, bits)
            : new BlockResult(null, false, weight * predicted, 0);
    }

    /// <summary>
    /// Quantizes coefficients (raster order) with a dead zone into absolute levels; gives the scan position of the last level that is
    /// not zero (or -1), and in <paramref name="saved"/> the squared error, in the quantizer's scale, its levels take away.
    /// </summary>
    private static int QuantizeScalar(int[] coefficients, int[] levels, int count, int log2, int scale, long offset, int shift, out double saved)
    {
        ushort[] positions = ScanPositions.Of(log2);
        saved = 0;
        int last = -1;
        for (int i = 0; i < count; i++)
        {
            long exact = Math.Abs((long)coefficients[i]) * scale;
            long level = Math.Min((exact + offset) >> shift, short.MaxValue);
            levels[i] = (int)level;
            if (level != 0)
            {
                last = Math.Max(last, positions[i]);
                double recon = level << shift;
                saved += recon * ((2.0 * exact) - recon);
            }
        }

        return last;
    }

    /// <inheritdoc cref="QuantizeScalar"/>
    /// <remarks>
    /// Eight coefficients a step: a coefficient's magnitude fits 16 bits and the scale 15, so |c| scale plus the offset fits an int.
    /// </remarks>
    private static int QuantizeAvx2(int[] coefficients, int[] levels, int count, int log2, int scale, int offset, int shift, out double saved)
    {
        ushort[] positions = ScanPositions.Of(log2);
        ref int c0 = ref MemoryMarshal.GetArrayDataReference(coefficients);
        ref int l0 = ref MemoryMarshal.GetArrayDataReference(levels);
        Vector256<int> scales = Vector256.Create(scale), offsets = Vector256.Create(offset), max = Vector256.Create((int)short.MaxValue);
        Vector256<double> sum = Vector256<double>.Zero;
        int last = -1;
        for (int i = 0; i < count; i += 8)
        {
            Vector256<int> exact = Vector256.Abs(Vector256.LoadUnsafe(ref c0, (nuint)i)) * scales;
            Vector256<int> level = Vector256.Min(Vector256.ShiftRightLogical(exact + offsets, shift), max);
            level.StoreUnsafe(ref l0, (nuint)i);
            uint mask = (~Vector256.Equals(level, Vector256<int>.Zero)).ExtractMostSignificantBits();
            if (mask == 0)
            {
                continue;
            }

            Vector256<int> recon = Vector256.ShiftLeft(level, shift);
            Vector256<double> rLo = Avx.ConvertToVector256Double(recon.GetLower()), rHi = Avx.ConvertToVector256Double(recon.GetUpper());
            Vector256<double> eLo = Avx.ConvertToVector256Double(exact.GetLower()), eHi = Avx.ConvertToVector256Double(exact.GetUpper());
            sum += (rLo * ((eLo + eLo) - rLo)) + (rHi * ((eHi + eHi) - rHi));
            do
            {
                last = Math.Max(last, positions[i + BitOperations.TrailingZeroCount(mask)]);
                mask &= mask - 1;
            }
            while (mask != 0);
        }

        saved = Vector256.Sum(sum);
        return last;
    }

    /// <summary>
    /// The residual (source less prediction) of an n by n block into <paramref name="residual"/>, and its sum of squares: the
    /// distortion of leaving it uncoded.
    /// </summary>
    private static long Residual(ushort[] source, ushort[] plane, int at, int stride, int n, int[] residual)
    {
        long sum = 0;
        ref ushort ps0 = ref MemoryMarshal.GetArrayDataReference(source);
        ref ushort pp0 = ref MemoryMarshal.GetArrayDataReference(plane);
        ref int pr0 = ref MemoryMarshal.GetArrayDataReference(residual);
        for (int r = 0; r < n; r++)
        {
            ref ushort ps = ref Unsafe.Add(ref ps0, at + (r * stride));
            ref ushort pp = ref Unsafe.Add(ref pp0, at + (r * stride));
            ref int pr = ref Unsafe.Add(ref pr0, r * n);
            int k = 0;
            if (n >= 8 && Vector256.IsHardwareAccelerated)
            {
                Vector256<int> acc = Vector256<int>.Zero;
                for (; k + 8 <= n; k += 8)
                {
                    Vector256<int> d = Vector256.WidenLower(Vector128.LoadUnsafe(ref ps, (nuint)k).ToVector256()).AsInt32()
                        - Vector256.WidenLower(Vector128.LoadUnsafe(ref pp, (nuint)k).ToVector256()).AsInt32();
                    d.StoreUnsafe(ref pr, (nuint)k);
                    acc += d * d;
                }

                sum += Vector256.Sum(acc);
            }

            for (; k < n; k++)
            {
                int d = Unsafe.Add(ref ps, k) - Unsafe.Add(ref pp, k);
                Unsafe.Add(ref pr, k) = d;
                sum += d * d;
            }
        }

        return sum;
    }

    // The significance flags' bin costs in the context states they were made from, by (chroma, log2 - 2, right/below pattern),
    // made on first use after the states change (they seldom do in a P picture's search: RDOQ and the trials leave them): the
    // cost of flag 0 at each scan position, what flag 1 adds to it, and the flag-0 costs summed from position 1 of each sub-block.
    private const int SigTables = 2 * 4 * 4;
    private readonly int[]?[] _sigZero = new int[SigTables][];
    private readonly int[]?[] _sigDelta = new int[SigTables][];
    private readonly int[]?[] _sigPrefix = new int[SigTables][];
    private readonly byte[] _sigStates = new byte[44];
    private uint _sigBuilt;

    /// <summary>The significance flag tables of a block kind, made when the slice's states differ from the tables'.</summary>
    private int SigTable(int c, int log2, int pattern)
    {
        int index = (((c == 0 ? 0 : 1) * 4) + log2 - 2) * 4 + pattern;
        if ((_sigBuilt & (1u << index)) != 0)
        {
            return index;
        }

        int n = 1 << log2, count = n * n;
        int[] zero = _sigZero[index] ??= new int[count], delta = _sigDelta[index] ??= new int[count], prefix = _sigPrefix[index] ??= new int[count];
        byte[] contexts = _bins.Contexts;
        byte[] sigTable = HevcSliceDecoder.SigContexts[((((c == 0 ? 0 : 1) * 4) + log2 - 2) * 3 * 4) + pattern];
        byte[] outer = HevcScan.Get(0, log2 - 2);
        byte[] offsets = HevcScan.Offsets(0, log2);
        for (int i = 0; i < count >> 4; i++)
        {
            int corner = (outer[(2 * i) + 1] << (2 + log2)) + (outer[2 * i] << 2);
            int sum = 0;
            for (int p = 0; p < 16; p++)
            {
                byte state = contexts[HevcContexts.SigCoeff + (p == 0 ? sigTable[corner] : sigTable[corner + offsets[p]])];
                int s = (i << 4) + p;
                zero[s] = CabacCost.Of(state, 0);
                delta[s] = CabacCost.Of(state, 1) - zero[s];
                sum += p == 0 ? 0 : zero[s];
                prefix[s] = sum;
            }
        }

        _sigBuilt |= 1u << index;
        return index;
    }

    /// <summary>
    /// The rate of residual_coding of an inter block's levels (absolute values in diagonal scan order, the last not zero at
    /// <paramref name="last"/>) in 1/32768 bits, every bin priced in the context state it starts the block in; signs hidden as
    /// sign data hiding would hide them. Clears the levels it reads.
    /// </summary>
    [SkipLocalsInit]
    private long TrialRate(short[] levels, int last, int log2, int c)
    {
        byte[] contexts = _bins.Contexts;
        ReadOnlySpan<byte> sigStates = contexts.AsSpan(HevcContexts.SigCoeff, 44);
        if (!sigStates.SequenceEqual(_sigStates))
        {
            sigStates.CopyTo(_sigStates);
            _sigBuilt = 0;
        }

        int n = 1 << log2;
        long bits = 0;
        if (_pps.TransformSkipEnabled && log2 <= _pps.Log2MaxTransformSkipSize)
        {
            bits += CabacCost.Of(contexts[HevcContexts.TransformSkip + (c == 0 ? 0 : 1)], 0);
        }

        int lastRaster = ScanRasters[log2][last];
        bits += LastPositionRate(lastRaster & (n - 1), lastRaster >> log2, log2, c);

        int log2Sb = log2 - 2, sbWidth = 1 << log2Sb;
        byte[] outer = HevcScan.Get(0, log2Sb);
        bool signHiding = _pps.SignDataHiding;
        int g1Offset = c > 0 ? 16 : 0, g2Offset = c > 0 ? 4 : 0;
        int codedFlag = HevcContexts.CodedSubBlock + (c == 0 ? 0 : 2);
        ulong codedSubBlock = 0;
        int greater1State = 1;
        int lastSubBlock = last >> 4;
        ref short l0 = ref MemoryMarshal.GetArrayDataReference(levels);
        for (int i = lastSubBlock; i >= 0; i--)
        {
            int xS = outer[2 * i], yS = outer[(2 * i) + 1];
            bool right = xS + 1 < sbWidth && (codedSubBlock & (1UL << ((yS * sbWidth) + xS + 1))) != 0;
            bool below = yS + 1 < sbWidth && (codedSubBlock & (1UL << (((yS + 1) * sbWidth) + xS))) != 0;
            int top = i == lastSubBlock ? last & 15 : 15;
            ref short block = ref Unsafe.Add(ref l0, i << 4);
            uint mask = (~Vector256.Equals(Vector256.LoadUnsafe(ref block), Vector256<short>.Zero)).ExtractMostSignificantBits() & ((2u << top) - 1);
            int count = BitOperations.PopCount(mask);
            bool inferDc = false;
            if (i < lastSubBlock && i > 0)
            {
                bits += CabacCost.Of(contexts[codedFlag + (right || below ? 1 : 0)], count > 0 ? 1 : 0);
                if (count == 0)
                {
                    continue;
                }

                inferDc = true;
            }

            codedSubBlock |= 1UL << ((yS * sbWidth) + xS);

            // significant_coeff_flag below the last position (or all 16), the DC one inferred when nothing above it is significant:
            // the flag-0 costs summed, and what each significant position adds.
            int table = SigTable(c, log2, (right ? 1 : 0) + (below ? 2 : 0));
            int[] delta = _sigDelta[table]!;
            int startPos = i == lastSubBlock ? top - 1 : 15;
            int at = i << 4;
            if (startPos > 0)
            {
                bits += _sigPrefix[table]![at + startPos];
                for (uint m = mask & ((2u << startPos) - 2); m != 0; m &= m - 1)
                {
                    bits += delta[at + BitOperations.TrailingZeroCount(m)];
                }
            }

            if (startPos >= 0 && !(inferDc && mask == 1))
            {
                bits += _sigZero[table]![at] + ((mask & 1) != 0 ? delta[at] : 0);
            }

            if (count == 0)
            {
                continue;
            }

            // The levels in coding order: greater1 flags (first 8), the greater2 flag, coeff_abs_level_remaining, signs.
            int ctxSet = (i == 0 || c > 0 ? 0 : 2) + (greater1State == 0 ? 1 : 0);
            int g1Base = HevcContexts.Greater1 + (ctxSet * 4) + g1Offset;
            int g2 = HevcContexts.Greater2 + ctxSet + g2Offset;
            greater1State = 1;
            int k = 0, rice = 0;
            bool firstCoeff2 = true, greater2Coded = false;
            int first = 31 - BitOperations.LeadingZeroCount(mask), lowest = BitOperations.TrailingZeroCount(mask);
            for (uint m = mask; m != 0; k++)
            {
                int p = 31 - BitOperations.LeadingZeroCount(m);
                m ^= 1u << p;
                int level = Unsafe.Add(ref block, p);
                int baseLevel = 1;
                if (k < 8)
                {
                    bits += CabacCost.Of(contexts[g1Base + greater1State], level > 1 ? 1 : 0);
                    if (level > 1)
                    {
                        greater1State = 0;
                        if (!greater2Coded)
                        {
                            bits += CabacCost.Of(contexts[g2], level > 2 ? 1 : 0);
                            greater2Coded = true;
                        }
                    }
                    else if (greater1State is > 0 and < 3)
                    {
                        greater1State++;
                    }

                    baseLevel = firstCoeff2 ? 3 : 2;
                }

                if (level >= baseLevel)
                {
                    bits += HevcSyntaxWriter.RemainingLength(level - baseLevel, rice) * (long)CabacCost.Bit;
                    if (level > 3 << rice)
                    {
                        rice = Math.Min(rice + 1, 4);
                    }
                }

                if (level >= 2)
                {
                    firstCoeff2 = false;
                }
            }

            bool hidden = signHiding && first - lowest > 3;
            bits += (long)CabacCost.Bit * (hidden ? count - 1 : count);
        }

        levels.AsSpan(0, (lastSubBlock + 1) << 4).Clear();
        return bits;
    }

    /// <summary>The rate of last_sig_coeff_x and _y (prefix bins in their context states, suffix bits).</summary>
    private long LastPositionRate(int lastX, int lastY, int log2, int c)
    {
        int ctxOffset = c == 0 ? (3 * (log2 - 2)) + ((log2 - 1) >> 2) : 15;
        int ctxShift = c == 0 ? (log2 + 1) >> 2 : log2 - 2;
        int maxPrefix = (log2 << 1) - 1;
        byte[] contexts = _bins.Contexts;
        long bits = 0;
        int prefixX = HevcSyntaxWriter.Prefix(lastX, out _, out int bitsX), prefixY = HevcSyntaxWriter.Prefix(lastY, out _, out int bitsY);
        for (int i = 0; i < prefixX; i++)
        {
            bits += CabacCost.Of(contexts[HevcContexts.LastXPrefix + ctxOffset + (i >> ctxShift)], 1);
        }

        if (prefixX < maxPrefix)
        {
            bits += CabacCost.Of(contexts[HevcContexts.LastXPrefix + ctxOffset + (prefixX >> ctxShift)], 0);
        }

        for (int i = 0; i < prefixY; i++)
        {
            bits += CabacCost.Of(contexts[HevcContexts.LastYPrefix + ctxOffset + (i >> ctxShift)], 1);
        }

        if (prefixY < maxPrefix)
        {
            bits += CabacCost.Of(contexts[HevcContexts.LastYPrefix + ctxOffset + (prefixY >> ctxShift)], 0);
        }

        return bits + ((long)(bitsX + bitsY) * CabacCost.Bit);
    }
}
