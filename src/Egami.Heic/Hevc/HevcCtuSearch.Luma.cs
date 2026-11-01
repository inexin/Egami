using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Egami.Heic.Hevc;

/// <summary>
/// Luma: the intra mode of a prediction unit, by a rough decision on the Hadamard cost (SATD) of each mode's prediction and a full
/// rate-distortion decision among the best few, then its transform tree.
/// </summary>
internal sealed partial class HevcCtuSearch
{
    private readonly long[] _roughCosts = new long[35];
    private readonly int[] _candidates = new int[35];

    /// <summary>Decides a luma prediction block's mode and transform tree; reconstructs it and gives the tree.</summary>
    private HevcTransformNode LumaPredictionUnit(HevcCodingUnit cu, int part, int x, int y, int log2, int depth, int qp, double lambda)
    {
        Span<int> mpm = stackalloc int[3];
        _writer.MostProbableModes(x, y, cu.LumaModes.AsSpan(0, part), cu, mpm);
        int count = cu.Bypass && _profile.SmallCandidates == 1 ? 1 : Candidates(x, y, log2, mpm, lambda, log2 <= 3 ? _profile.SmallCandidates : _profile.LargeCandidates);

        HevcSnapshot before = _tuBefore[log2], best = _tuBest[log2];
        int maxDepth = _sps.MaxTransformHierarchyDepthIntra + (cu.NxN ? 1 : 0);
        HevcTransformNode? bestNode = null;
        double bestCost = double.MaxValue;
        int bestMode = 0;
        if (count > 1)
        {
            before.Capture(x, y, 1 << log2, false, false);
            Array.Copy(_bins.Contexts, before.Contexts, HevcContexts.Count);
        }

        for (int i = 0; i < count; i++)
        {
            int mode = _candidates[i];
            if (i > 0)
            {
                before.Restore();
                Array.Copy(before.Contexts, _bins.Contexts, HevcContexts.Count);
            }

            cu.LumaModes[part] = mode;
            long bits = ModeBits(mode, mpm);
            double cost = LumaTree(cu, out HevcTransformNode node, x, y, log2, depth, maxDepth, mode, qp, lambda, !_profile.TransformSearch || count > 1);
            cost += lambda * bits / Nal.CabacCost.Bit;
            if (cost < bestCost)
            {
                (bestCost, bestNode, bestMode) = (cost, node, mode);
                if (i + 1 < count)
                {
                    best.Capture(x, y, 1 << log2, false, false);
                    Array.Copy(_bins.Contexts, best.Contexts, HevcContexts.Count);
                }
            }
        }

        if (count > 1 && bestNode is not null && cu.LumaModes[part] != bestMode)
        {
            best.Restore();
            Array.Copy(best.Contexts, _bins.Contexts, HevcContexts.Count);
        }

        cu.LumaModes[part] = bestMode;

        // The transform tree of the mode chosen, now with its splits searched.
        if (_profile.TransformSearch && count > 1 && log2 > _sps.MinTbLog2Size && depth < maxDepth)
        {
            if (count > 1)
            {
                before.Restore();
                Array.Copy(before.Contexts, _bins.Contexts, HevcContexts.Count);
            }

            LumaTree(cu, out bestNode, x, y, log2, depth, maxDepth, bestMode, qp, lambda, false);
        }

        return bestNode!;
    }

    /// <summary>The cost of a luma mode's syntax: prev_intra_luma_pred_flag and mpm_idx or rem_intra_luma_pred_mode.</summary>
    private long ModeBits(int mode, ReadOnlySpan<int> mpm)
    {
        long saved = _bins.Cost;
        _bins.Cost = 0;
        byte context = _bins.Contexts[HevcContexts.PrevIntraLumaPred];
        _bins.Encode(HevcContexts.PrevIntraLumaPred, mpm.Contains(mode) ? 1 : 0);
        _writer.LumaModeIndex(mode, mpm);
        _bins.Contexts[HevcContexts.PrevIntraLumaPred] = context;
        long bits = _bins.Cost;
        _bins.Cost = saved;
        return bits;
    }

    /// <summary>
    /// The rough decision: ranks the modes by SATD plus sqrt(lambda) times their mode bits, and puts the best
    /// <paramref name="keep"/> (and the most probable modes) into <see cref="_candidates"/>; returns how many there are.
    /// </summary>
    private int Candidates(int x, int y, int log2, ReadOnlySpan<int> mpm, double lambda, int keep)
    {
        double sqrtLambda = Math.Sqrt(lambda);
        Span<bool> tried = stackalloc bool[35];
        if (keep >= 35)
        {
            for (int m = 0; m < 35; m++)
            {
                _candidates[m] = m;
            }

            return 35;
        }

        // Coarse: planar, DC and every other angle (or every fourth at low effort); then the neighbours of the best angles.
        int step = _profile.AllRoughModes ? 1 : 2;
        for (int m = 0; m < 35; m++)
        {
            _roughCosts[m] = long.MaxValue;
        }

        for (int m = 0; m < 35; m = m < 2 ? m + 1 : m + step)
        {
            _roughCosts[m] = RoughCost(x, y, log2, m, mpm, sqrtLambda);
            tried[m] = true;
        }

        if (step > 1)
        {
            // Refine around the two best angles.
            int first = -1, second = -1;
            for (int m = 2; m < 35; m++)
            {
                if (!tried[m])
                {
                    continue;
                }

                if (first < 0 || _roughCosts[m] < _roughCosts[first])
                {
                    (second, first) = (first, m);
                }
                else if (second < 0 || _roughCosts[m] < _roughCosts[second])
                {
                    second = m;
                }
            }

            foreach (int center in (ReadOnlySpan<int>)[first, second])
            {
                for (int d = -1; d <= 1; d += 2)
                {
                    int m = center + d;
                    if (center >= 2 && m >= 2 && m <= 34 && !tried[m])
                    {
                        _roughCosts[m] = RoughCost(x, y, log2, m, mpm, sqrtLambda);
                        tried[m] = true;
                    }
                }
            }
        }

        if (_profile.MostProbableCandidates)
        {
            foreach (int m in mpm)
            {
                if (!tried[m])
                {
                    _roughCosts[m] = RoughCost(x, y, log2, m, mpm, sqrtLambda);
                    tried[m] = true;
                }
            }
        }

        // The best keep, then the most probable modes not among them.
        int count = 0;
        Span<bool> taken = stackalloc bool[35];
        for (int k = 0; k < keep; k++)
        {
            int best = -1;
            for (int m = 0; m < 35; m++)
            {
                if (!taken[m] && _roughCosts[m] != long.MaxValue && (best < 0 || _roughCosts[m] < _roughCosts[best]))
                {
                    best = m;
                }
            }

            if (best < 0)
            {
                break;
            }

            taken[best] = true;
            _candidates[count++] = best;
        }

        if (_profile.MostProbableCandidates)
        {
            foreach (int m in mpm)
            {
                if (!taken[m])
                {
                    taken[m] = true;
                    _candidates[count++] = m;
                }
            }
        }

        return count;
    }

    /// <summary>
    /// SATD of the prediction of a mode plus sqrt(lambda) times its bits. A 64x64 block predicts its four 32x32 quarters (the
    /// largest transform), each from the source samples of the quarters before it.
    /// </summary>
    private long RoughCost(int x, int y, int log2, int mode, ReadOnlySpan<int> mpm, double sqrtLambda)
    {
        long satd = 0;
        int stride = _picture.Widths[0];
        ushort[] plane = _picture.Planes[0], source = _source.Planes[0];
        if (log2 > 5)
        {
            for (int q = 0; q < 4; q++)
            {
                int xq = x + ((q & 1) << 5), yq = y + ((q >> 1) << 5);
                _intra.Predict(_picture, 0, xq, yq, 5, mode, false);
                satd += Satd(source, plane, (yq * stride) + xq, stride, 32);
                CopyBlock(source, plane, (yq * stride) + xq, stride, 32, 32);
            }
        }
        else
        {
            _intra.Predict(_picture, 0, x, y, log2, mode, false);
            satd = Satd(source, plane, (y * stride) + x, stride, 1 << log2);
        }

        int bits = mpm[0] == mode ? 2 : mpm[1] == mode || mpm[2] == mode ? 3 : 6;
        return satd + (long)(sqrtLambda * bits);
    }

    /// <summary>
    /// Codes a luma transform tree for a mode: forced splits, and with <paramref name="leafOnly"/> false the searched ones, each
    /// leaf predicted, transformed and reconstructed. Gives J of its luma: distortion plus lambda times the rate of its split and
    /// cbf_luma flags and residuals.
    /// </summary>
    private double LumaTree(HevcCodingUnit cu, out HevcTransformNode node, int x, int y, int log2, int depth, int maxDepth, int mode, int qp, double lambda,
        bool leafOnly)
    {
        bool forced = log2 > _sps.MaxTbLog2Size || (cu.NxN && depth == 0);
        bool flagCoded = !forced && log2 <= _sps.MaxTbLog2Size && log2 > _sps.MinTbLog2Size && depth < maxDepth;
        bool trySplit = flagCoded && !leafOnly && log2 > 2;
        int half = 1 << (log2 - 1);
        if (forced)
        {
            node = new HevcTransformNode { Split = true, Children = new HevcTransformNode[4] };
            double total = 0;
            for (int i = 0; i < 4; i++)
            {
                total += LumaTree(cu, out node.Children[i], x + ((i & 1) * half), y + ((i >> 1) * half), log2 - 1, depth + 1, maxDepth, mode, qp, lambda, leafOnly);
            }

            return total;
        }

        HevcSnapshot before = _tuBefore[log2], best = _tuBest[log2];
        if (trySplit)
        {
            before.Capture(x, y, 1 << log2, false, false);
            Array.Copy(_bins.Contexts, before.Contexts, HevcContexts.Count);
        }

        // A transform unit.
        long saved = _bins.Cost;
        _bins.Cost = 0;
        if (flagCoded)
        {
            _bins.Encode(HevcContexts.SplitTransform + 5 - log2, 0);
        }

        BlockResult leaf = CodeBlock(0, x, y, log2, mode, qp, cu.Bypass, lambda, 1);
        _bins.Encode(HevcContexts.CbfLuma + (depth == 0 ? 1 : 0), leaf.Levels is null ? 0 : 1);
        double leafCost = leaf.Distortion + (lambda * (_bins.Cost + leaf.Bits) / Nal.CabacCost.Bit);
        _bins.Cost = saved;
        node = new HevcTransformNode { CbfLuma = leaf.Levels is not null };
        node.Levels[0] = leaf.Levels;
        node.TransformSkip[0] = leaf.TransformSkip;
        if (!trySplit)
        {
            return leafCost;
        }

        best.Capture(x, y, 1 << log2, false, false);
        Array.Copy(_bins.Contexts, best.Contexts, HevcContexts.Count);
        before.Restore();
        Array.Copy(before.Contexts, _bins.Contexts, HevcContexts.Count);

        _bins.Cost = 0;
        _bins.Encode(HevcContexts.SplitTransform + 5 - log2, 1);
        double splitCost = lambda * _bins.Cost / Nal.CabacCost.Bit;
        _bins.Cost = saved;
        HevcTransformNode split = new() { Split = true, Children = new HevcTransformNode[4] };
        for (int i = 0; i < 4 && splitCost < leafCost; i++)
        {
            splitCost += LumaTree(cu, out split.Children[i], x + ((i & 1) * half), y + ((i >> 1) * half), log2 - 1, depth + 1, maxDepth, mode, qp, lambda, false);
        }

        if (splitCost < leafCost)
        {
            node = split;
            return splitCost;
        }

        best.Restore();
        Array.Copy(best.Contexts, _bins.Contexts, HevcContexts.Count);
        return leafCost;
    }

    // ---------------------------------------------------------------- one block

    /// <summary>A coded block: its levels (null for none), transform skip, distortion and residual rate in 1/32768 bits.</summary>
    private readonly record struct BlockResult(short[]? Levels, bool TransformSkip, double Distortion, long Bits);

    /// <summary>
    /// Predicts, transforms, quantizes and reconstructs one block of component <paramref name="c"/> at (x, y) in its samples, as
    /// the decoder will: transform skip tried too for 4x4 blocks when the profile allows it. The rate of the residual (with
    /// transform_skip_flag) is counted, and the contexts move on with it.
    /// </summary>
    private BlockResult CodeBlock(int c, int x, int y, int log2, int mode, int qp, bool bypass, double lambda, double weight)
    {
        int n = 1 << log2, count = n * n;
        ushort[] plane = _picture.Planes[c], source = _source.Planes[c];
        int stride = _picture.Widths[c];
        int at = (y * stride) + x;
        _intra.Predict(_picture, c, x, y, log2, mode, false);
        for (int r = 0; r < n; r++)
        {
            for (int k = 0; k < n; k++)
            {
                int i = (r * n) + k;
                int p = plane[at + (r * stride) + k];
                _prediction[i] = (ushort)p;
                _residual[i] = source[at + (r * stride) + k] - p;
            }
        }

        if (bypass)
        {
            short[]? levels = null;
            for (int i = 0; i < count; i++)
            {
                if (_residual[i] != 0)
                {
                    levels ??= new short[count];
                    levels[i] = (short)_residual[i];
                }
            }

            CopyBlock(source, plane, at, stride, n, n);
            long bits = 0;
            if (levels is not null)
            {
                long saved = _bins.Cost;
                _bins.Cost = 0;
                _writer.Residual(levels, log2, c, mode, false, true);
                bits = _bins.Cost;
                _bins.Cost = saved;
            }

            return new BlockResult(levels, false, 0, bits);
        }

        int bitDepth = c == 0 ? _sps.BitDepthLuma : _sps.BitDepthChroma;
        int qpc = Qp(c, qp);
        bool tryTs = log2 == 2 && _pps.TransformSkipEnabled && _profile.TransformSkip;
        if (tryTs)
        {
            Array.Copy(_bins.Contexts, _contextsSaved, HevcContexts.Count);
        }

        BlockResult result = Quantized(c, plane, source, at, stride, log2, mode, qpc, bitDepth, false, lambda, weight, _levels);
        if (!tryTs)
        {
            return result;
        }

        // Transform skip on the same prediction: keep the transformed result and its contexts, try the other.
        Span<ushort> reconstruction = stackalloc ushort[16];
        for (int r = 0; r < 4; r++)
        {
            plane.AsSpan(at + (r * stride), 4).CopyTo(reconstruction[(r * 4)..]);
            _prediction.AsSpan(r * 4, 4).CopyTo(plane.AsSpan(at + (r * stride)));
        }

        Span<byte> contexts = stackalloc byte[HevcContexts.Count];
        _bins.Contexts.CopyTo(contexts);
        _contextsSaved.CopyTo(_bins.Contexts, 0);
        BlockResult skipped = Quantized(c, plane, source, at, stride, log2, mode, qpc, bitDepth, true, lambda, weight, _levelsTs);
        double transformed = result.Distortion + (lambda * result.Bits / Nal.CabacCost.Bit);
        double skip = skipped.Distortion + (lambda * skipped.Bits / Nal.CabacCost.Bit);
        if (skip < transformed)
        {
            return skipped;
        }

        for (int r = 0; r < 4; r++)
        {
            reconstruction.Slice(r * 4, 4).CopyTo(plane.AsSpan(at + (r * stride)));
        }

        contexts.CopyTo(_bins.Contexts);
        return result;
    }

    /// <summary>Transforms (or skips the transform), quantizes, reconstructs and counts a predicted block's residual.</summary>
    private BlockResult Quantized(int c, ushort[] plane, ushort[] source, int at, int stride, int log2, int mode, int qp, int bitDepth, bool skip, double lambda,
        double weight, short[] levels)
    {
        int n = 1 << log2, count = n * n;
        bool dst = c == 0 && log2 == 2;
        if (skip)
        {
            HevcForward.Skip(_residual, _coefficients, log2, bitDepth);
        }
        else
        {
            HevcForward.Transform(_residual, _coefficients, log2, bitDepth, dst);
        }

        int scanIdx = HevcSyntaxWriter.ScanIndex(log2, c, mode, _chroma);
        int nonZero = _profile.Rdoq
            ? Rdoq(_coefficients, levels, log2, c, scanIdx, qp, bitDepth, lambda / weight, skip)
            : HevcForward.Quantize(_coefficients, levels, log2, qp, bitDepth);
        if (nonZero != 0 && _pps.SignDataHiding)
        {
            HevcForward.HideSigns(_coefficients, levels, log2, qp, bitDepth, scanIdx);
        }

        if (nonZero == 0)
        {
            return new BlockResult(null, false, weight * Sse(source, plane, at, stride, n, n), 0);
        }

        int lastRow = 0, lastColumn = 0;
        for (int i = 0; i < count; i++)
        {
            int level = levels[i];
            _scaled[i] = level;
            if (level != 0)
            {
                lastRow = Math.Max(lastRow, i >> log2);
                lastColumn = Math.Max(lastColumn, i & (n - 1));
            }
        }

        HevcTransform.Scale(_scaled, log2, qp, bitDepth, null, lastRow, lastColumn);
        if (skip)
        {
            HevcTransform.Skip(_scaled, _inverse, log2, bitDepth);
        }
        else
        {
            HevcTransform.Inverse(_scaled, _inverse, _scratch, log2, bitDepth, dst, lastRow, lastColumn);
        }

        HevcTransform.Add(plane, at, stride, _inverse, log2, bitDepth);
        Array.Clear(_scaled, 0, count);

        long saved = _bins.Cost;
        _bins.Cost = 0;
        _writer.Residual(levels, log2, c, mode, skip, false);
        long bits = _bins.Cost;
        _bins.Cost = saved;
        return new BlockResult(levels.AsSpan(0, count).ToArray(), skip, weight * Sse(source, plane, at, stride, n, n), bits);
    }

    // ---------------------------------------------------------------- sample helpers

    private static void CopyBlock(ushort[] from, ushort[] to, int at, int stride, int width, int height)
    {
        for (int r = 0; r < height; r++)
        {
            from.AsSpan(at + (r * stride), width).CopyTo(to.AsSpan(at + (r * stride)));
        }
    }

    /// <summary>The sum of squared differences of two blocks at the same place in planes of one stride.</summary>
    internal static long Sse(ushort[] a, ushort[] b, int at, int stride, int width, int height)
    {
        long sum = 0;
        for (int r = 0; r < height; r++)
        {
            ref ushort pa = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(a), at + (r * stride));
            ref ushort pb = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(b), at + (r * stride));
            int k = 0;
            if (Vector256.IsHardwareAccelerated)
            {
                Vector256<int> acc = Vector256<int>.Zero;
                for (; k + 16 <= width; k += 16)
                {
                    Vector256<short> d = Vector256.LoadUnsafe(ref pa, (nuint)k).AsInt16() - Vector256.LoadUnsafe(ref pb, (nuint)k).AsInt16();
                    (Vector256<int> lo, Vector256<int> hi) = Vector256.Widen(d);
                    acc += (lo * lo) + (hi * hi);
                }

                sum += Vector256.Sum(acc.AsUInt32());
            }

            for (; k < width; k++)
            {
                int d = Unsafe.Add(ref pa, k) - Unsafe.Add(ref pb, k);
                sum += d * d;
            }
        }

        return sum;
    }

    /// <summary>The sum of absolute Hadamard-transformed differences of a square block: 8x8 transforms, or 4x4 for a 4x4 block.</summary>
    internal static long Satd(ushort[] a, ushort[] b, int at, int stride, int size)
    {
        long sum = 0;
        Span<int> d = stackalloc int[64];
        int t = size == 4 ? 4 : 8;
        for (int by = 0; by < size; by += t)
        {
            for (int bx = 0; bx < size; bx += t)
            {
                int origin = at + (by * stride) + bx;
                for (int r = 0; r < t; r++)
                {
                    for (int k = 0; k < t; k++)
                    {
                        d[(r * t) + k] = a[origin + (r * stride) + k] - b[origin + (r * stride) + k];
                    }
                }

                sum += t == 4 ? (Hadamard4(d) + 1) >> 1 : (Hadamard8(d) + 2) >> 2;
            }
        }

        return sum;
    }

    private static long Hadamard4(Span<int> d)
    {
        Span<int> m = stackalloc int[16];
        for (int r = 0; r < 4; r++)
        {
            int a0 = d[(r * 4) + 0] + d[(r * 4) + 1], a1 = d[(r * 4) + 0] - d[(r * 4) + 1];
            int a2 = d[(r * 4) + 2] + d[(r * 4) + 3], a3 = d[(r * 4) + 2] - d[(r * 4) + 3];
            (m[(r * 4) + 0], m[(r * 4) + 1], m[(r * 4) + 2], m[(r * 4) + 3]) = (a0 + a2, a1 + a3, a0 - a2, a1 - a3);
        }

        long sum = 0;
        for (int k = 0; k < 4; k++)
        {
            int a0 = m[k] + m[4 + k], a1 = m[k] - m[4 + k], a2 = m[8 + k] + m[12 + k], a3 = m[8 + k] - m[12 + k];
            sum += Math.Abs(a0 + a2) + Math.Abs(a1 + a3) + Math.Abs(a0 - a2) + Math.Abs(a1 - a3);
        }

        return sum;
    }

    private static long Hadamard8(Span<int> d)
    {
        Span<int> m = stackalloc int[64];
        for (int r = 0; r < 8; r++)
        {
            Butterfly8(d.Slice(r * 8, 8), m.Slice(r * 8, 8));
        }

        long sum = 0;
        Span<int> column = stackalloc int[8];
        Span<int> output = stackalloc int[8];
        for (int k = 0; k < 8; k++)
        {
            for (int r = 0; r < 8; r++)
            {
                column[r] = m[(r * 8) + k];
            }

            Butterfly8(column, output);
            for (int r = 0; r < 8; r++)
            {
                sum += Math.Abs(output[r]);
            }
        }

        return sum;
    }

    private static void Butterfly8(ReadOnlySpan<int> v, Span<int> o)
    {
        int a0 = v[0] + v[1], a1 = v[0] - v[1], a2 = v[2] + v[3], a3 = v[2] - v[3];
        int a4 = v[4] + v[5], a5 = v[4] - v[5], a6 = v[6] + v[7], a7 = v[6] - v[7];
        int b0 = a0 + a2, b1 = a1 + a3, b2 = a0 - a2, b3 = a1 - a3, b4 = a4 + a6, b5 = a5 + a7, b6 = a4 - a6, b7 = a5 - a7;
        (o[0], o[1], o[2], o[3], o[4], o[5], o[6], o[7]) = (b0 + b4, b1 + b5, b2 + b6, b3 + b7, b0 - b4, b1 - b5, b2 - b6, b3 - b7);
    }
}
