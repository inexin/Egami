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
    private readonly ushort[] _roughBlock = new ushort[32 * 32];

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
        // A unit that leaves no luma residual seldom gains from smaller transforms.
        if (_profile.TransformSearch && count > 1 && log2 > _sps.MinTbLog2Size && depth < maxDepth
            && !(_profile.EarlyStop && bestNode is { Split: false, CbfLuma: false }))
        {
            bool forced = log2 > _sps.MaxTbLog2Size || (cu.NxN && depth == 0);
            if (forced)
            {
                before.Restore();
                Array.Copy(before.Contexts, _bins.Contexts, HevcContexts.Count);
                LumaTree(cu, out bestNode, x, y, log2, depth, maxDepth, bestMode, qp, lambda, false);
                return bestNode;
            }

            // The unsplit tree is the one just coded: try the split from the same start, and keep the better.
            best.Capture(x, y, 1 << log2, false, false);
            Array.Copy(_bins.Contexts, best.Contexts, HevcContexts.Count);
            before.Restore();
            Array.Copy(before.Contexts, _bins.Contexts, HevcContexts.Count);
            double leafCost = bestCost - (lambda * ModeBits(bestMode, mpm) / Nal.CabacCost.Bit);
            double splitCost = SplitLumaTree(cu, out HevcTransformNode split, x, y, log2, depth, maxDepth, bestMode, qp, lambda, leafCost);
            if (splitCost < leafCost)
            {
                return split;
            }

            best.Restore();
            Array.Copy(best.Contexts, _bins.Contexts, HevcContexts.Count);
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
        if (keep >= 35)
        {
            for (int m = 0; m < 35; m++)
            {
                _candidates[m] = m;
            }

            return 35;
        }

        double sqrtLambda = Math.Sqrt(lambda);
        Span<bool> tried = stackalloc bool[35];
        tried.Clear();
        Span<int> modes = stackalloc int[35];
        _roughCosts.AsSpan().Fill(long.MaxValue);

        // A 64x64 block predicts its four 32x32 quarters (the largest transform), each from the source samples of the quarters
        // before it, which stand in for their reconstruction.
        if (log2 > 5)
        {
            CopyBlock(_source.Planes[0], _picture.Planes[0], (y * _picture.Widths[0]) + x, _picture.Widths[0], 64, 64);
        }

        // Coarse: planar, DC and every RoughStep-th angle; then around the two best angles at half the step, and so on to 1.
        int step = _profile.RoughStep;
        int count = 0;
        for (int m = 0; m < 35; m = m < 2 ? m + 1 : m + step)
        {
            modes[count++] = m;
        }

        RoughCosts(x, y, log2, modes[..count], mpm, sqrtLambda, tried);
        count = 0;
        for (int distance = step / 2; distance >= 1; distance /= 2)
        {
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
                for (int d = -distance; d <= distance; d += 2 * distance)
                {
                    int m = center + d;
                    if (center >= 2 && m >= 2 && m <= 34 && !tried[m] && !modes[..count].Contains(m))
                    {
                        modes[count++] = m;
                    }
                }
            }

            if (distance > 1 && count > 0)
            {
                RoughCosts(x, y, log2, modes[..count], mpm, sqrtLambda, tried);
                count = 0;
            }
        }

        if (_profile.MostProbableCandidates)
        {
            foreach (int m in mpm)
            {
                if (!tried[m] && !modes[..count].Contains(m))
                {
                    modes[count++] = m;
                }
            }
        }

        if (count > 0)
        {
            RoughCosts(x, y, log2, modes[..count], mpm, sqrtLambda, tried);
        }

        // The best keep, then the most probable modes not among them.
        int kept = 0;
        Span<bool> taken = stackalloc bool[35];
        taken.Clear();
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

            // A mode far behind the best by SATD seldom wins the full decision.
            if (best < 0 || (kept > 0 && _roughCosts[best] > _roughCosts[_candidates[0]] * (1 + _profile.CandidateMargin)))
            {
                break;
            }

            taken[best] = true;
            _candidates[kept++] = best;
        }

        if (_profile.MostProbableCandidates)
        {
            foreach (int m in mpm)
            {
                if (!taken[m])
                {
                    taken[m] = true;
                    _candidates[kept++] = m;
                }
            }
        }

        return kept;
    }

    /// <summary>
    /// The rough cost of each of <paramref name="modes"/>: SATD of its prediction plus sqrt(lambda) times its bits, the reference
    /// samples gathered once and every mode predicted into a scratch block.
    /// </summary>
    private void RoughCosts(int x, int y, int log2, ReadOnlySpan<int> modes, ReadOnlySpan<int> mpm, double sqrtLambda, Span<bool> tried)
    {
        int stride = _picture.Widths[0];
        ushort[] source = _source.Planes[0];
        int size = Math.Min(log2, 5), n = 1 << size;
        foreach (int m in modes)
        {
            int bits = mpm[0] == m ? 2 : mpm[1] == m || mpm[2] == m ? 3 : 6;
            _roughCosts[m] = (long)(sqrtLambda * bits);
            tried[m] = true;
        }

        for (int q = 0; q < (log2 > 5 ? 4 : 1); q++)
        {
            int xq = x + ((q & 1) << 5), yq = y + ((q >> 1) << 5);
            _intra.Gather(_picture, 0, xq, yq, size);
            foreach (int m in modes)
            {
                _intra.PredictGathered(_sps, m, false, _roughBlock, 0, n);
                _roughCosts[m] += HevcSatd.Compute(source, (yq * stride) + xq, stride, _roughBlock, 0, n, n);
            }
        }
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

        double splitCost = SplitLumaTree(cu, out HevcTransformNode split, x, y, log2, depth, maxDepth, mode, qp, lambda, leafCost);
        if (splitCost < leafCost)
        {
            node = split;
            return splitCost;
        }

        best.Restore();
        Array.Copy(best.Contexts, _bins.Contexts, HevcContexts.Count);
        return leafCost;
    }

    /// <summary>
    /// A luma transform tree split once at the top and searched below, from the current state; stops counting once its cost
    /// passes <paramref name="limit"/>.
    /// </summary>
    private double SplitLumaTree(HevcCodingUnit cu, out HevcTransformNode split, int x, int y, int log2, int depth, int maxDepth, int mode, int qp, double lambda,
        double limit)
    {
        long saved = _bins.Cost;
        _bins.Cost = 0;
        _bins.Encode(HevcContexts.SplitTransform + 5 - log2, 1);
        double cost = lambda * _bins.Cost / Nal.CabacCost.Bit;
        _bins.Cost = saved;
        split = new HevcTransformNode { Split = true, Children = new HevcTransformNode[4] };
        int half = 1 << (log2 - 1);
        for (int i = 0; i < 4 && cost < limit; i++)
        {
            cost += LumaTree(cu, out split.Children[i], x + ((i & 1) * half), y + ((i >> 1) * half), log2 - 1, depth + 1, maxDepth, mode, qp, lambda, false);
        }

        return cost;
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

        // Transform skip pays on sharp-edged luma (screen content); where the transform leaves nothing to code it rarely does.
        if (!tryTs || (_profile.EarlyStop && (c != 0 || result.Levels is null)))
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
        int scanEnd = count;
        long bits = -1;
        int nonZero = _profile.Rdoq
            ? Rdoq(_coefficients, levels, log2, c, scanIdx, qp, bitDepth, lambda / weight, out scanEnd, out bits)
            : HevcForward.Quantize(_coefficients, levels, log2, qp, bitDepth);
        if (nonZero != 0 && _pps.SignDataHiding)
        {
            HevcForward.HideSigns(_coefficients, levels, log2, qp, bitDepth, scanIdx, scanEnd >> 4);
        }

        if (nonZero == 0)
        {
            ClearLevels(levels, scanIdx, log2, scanEnd);
            return new BlockResult(null, false, weight * Sse(source, plane, at, stride, n, n), 0);
        }

        int lastRow = 0, lastColumn = 0;
        for (int i = 0; i < count; i++)
        {
            int level = levels[i];
            if (level != 0)
            {
                _scaled[i] = level;
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
        for (int r = 0; r <= lastRow; r++)
        {
            Array.Clear(_scaled, r * n, lastColumn + 1);
        }

        // The residual's rate: RDOQ's own count, or the bins counted (and the contexts moved on).
        if (bits < 0)
        {
            long saved = _bins.Cost;
            _bins.Cost = 0;
            _writer.Residual(levels, log2, c, mode, skip, false);
            bits = _bins.Cost;
            _bins.Cost = saved;
        }
        else if (_pps.TransformSkipEnabled && log2 <= _pps.Log2MaxTransformSkipSize)
        {
            bits += Nal.CabacCost.Of(_bins.Contexts[HevcContexts.TransformSkip + (c == 0 ? 0 : 1)], skip ? 1 : 0);
        }

        short[] kept = levels.AsSpan(0, count).ToArray();
        ClearLevels(levels, scanIdx, log2, scanEnd);
        return new BlockResult(kept, skip, weight * Sse(source, plane, at, stride, n, n), bits);
    }

    /// <summary>Clears the first <paramref name="scanEnd"/> positions in scan order of a level buffer, which is all zeros between blocks.</summary>
    private static void ClearLevels(short[] levels, int scanIdx, int log2, int scanEnd)
    {
        if (scanEnd >= 1 << (2 * log2))
        {
            Array.Clear(levels, 0, 1 << (2 * log2));
            return;
        }

        int[] rasters = ScanRasters[(scanIdx * 6) + log2];
        for (int s = 0; s < scanEnd; s++)
        {
            levels[rasters[s]] = 0;
        }
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
}
