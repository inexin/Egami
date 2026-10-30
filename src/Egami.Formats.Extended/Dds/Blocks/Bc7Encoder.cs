using System.Buffers.Binary;

namespace Egami.Formats.Extended.Dds.Blocks;

/// <summary>
/// Encodes BC7 blocks, written from the Direct3D 11 BC7 format specification, with a search shaped after bc7enc_rdo's: rank the
/// partitions by how well each subset's colours lie on a line, fit each subset's endpoints along its principal axis, try the
/// p-bit choices, assign indices with the decoder's exact interpolation, then refit by least squares. The effort picks the modes
/// (6 and 1 at the lowest, every mode at the highest), how many partitions are tried and how many refits run. Every candidate is
/// scored by its squared RGBA error as decoded, and a block's bytes depend only on its pixels.
/// </summary>
internal static class Bc7Encoder
{
    // Per mode: subsets, partition bits, rotation bits, index selection bits, colour bits, alpha bits, p-bits (0 none,
    // 1 one an endpoint, 2 one a subset), index bits, secondary index bits.
    private static ReadOnlySpan<byte> Modes =>
    [
        3, 4, 0, 0, 4, 0, 1, 3, 0,
        2, 6, 0, 0, 6, 0, 2, 3, 0,
        3, 6, 0, 0, 5, 0, 0, 2, 0,
        2, 6, 0, 0, 7, 0, 1, 2, 0,
        1, 0, 2, 1, 5, 6, 0, 2, 3,
        1, 0, 2, 0, 7, 8, 0, 2, 2,
        1, 0, 0, 0, 7, 7, 1, 4, 0,
        2, 6, 0, 0, 5, 5, 1, 2, 0,
    ];

    private static ReadOnlySpan<byte> Weights2 => [0, 21, 43, 64];

    private static ReadOnlySpan<byte> Weights3 => [0, 9, 18, 27, 37, 46, 55, 64];

    private static ReadOnlySpan<byte> Weights4 => [0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64];

    /// <summary>Encodes 16 RGBA pixels (4 bytes each, row order) into a 16-byte block at effort 0 to 6.</summary>
    public static void Encode(ReadOnlySpan<byte> rgba, Span<byte> block, int effort)
    {
        Span<int> px = stackalloc int[64];
        bool opaque = true;
        for (int i = 0; i < 64; i++)
        {
            px[i] = rgba[i];
            opaque &= (i & 3) != 3 || rgba[i] == 255;
        }

        ReadOnlySpan<int> modes = (opaque, effort) switch
        {
            (true, 0) => [6],
            (true, 1) => [6, 1],
            (true, 2) => [6, 1, 3],
            (true, 3) => [6, 1, 3, 0, 2],
            (true, _) => [6, 1, 3, 0, 2, 5, 4],
            (false, 0) => [6],
            (false, 1) => [6, 5],
            (false, 2) => [6, 5, 7],
            (false, _) => [6, 5, 7, 4],
        };
        int partitions = effort switch { <= 1 => 1, 2 => 2, 3 => 4, 4 => 8, 5 => 16, _ => 64 };
        int refits = effort switch { 0 => 0, <= 2 => 1, <= 4 => 2, _ => 3 };

        // Three encodings a thread, reused block after block: the best so far, the one being tried, and modes 4 and 5's alpha.
        Encoding[] scratch = t_scratch ??= [new(), new(), new()];
        Encoding best = scratch[0];
        Encoding work = scratch[1];
        best.Reset(6, 0, 0, 0);
        best.Error = long.MaxValue;
        Span<int> ranked = stackalloc int[64];
        Span<int> finalists = stackalloc int[2];
        Span<long> finalistErrors = stackalloc long[2];
        foreach (int mode in modes)
        {
            ReadOnlySpan<byte> m = Modes.Slice(mode * 9, 9);
            int subsets = m[0];
            if (subsets == 1)
            {
                int rotations = m[2] != 0 ? (effort >= 4 ? 4 : effort >= 2 ? 2 : 1) : 1;
                int selections = m[3] != 0 && effort >= 3 ? 2 : 1;
                for (int rotation = 0; rotation < rotations; rotation++)
                {
                    for (int selection = 0; selection < selections; selection++)
                    {
                        work.Reset(mode, 0, rotation, selection);
                        FitSingle(px, m, work, refits, scratch[2]);
                        if (work.Error < best.Error)
                        {
                            (best, work) = (work, best);
                        }
                    }
                }

                continue;
            }

            // A quick fit of every ranked partition, then the full search on the two best.
            int count = RankPartitions(px, subsets, 1 << m[1], Math.Min(partitions, 1 << m[1]), ranked, opaque);
            finalistErrors.Fill(long.MaxValue);
            for (int c = 0; c < count && count > 2; c++)
            {
                work.Reset(mode, ranked[c], 0, 0);
                FitPartitioned(px, m, work, 0, finalistErrors[1]);
                if (work.Error < finalistErrors[1])
                {
                    int slot = work.Error < finalistErrors[0] ? 0 : 1;
                    if (slot == 0)
                    {
                        (finalists[1], finalistErrors[1]) = (finalists[0], finalistErrors[0]);
                    }

                    (finalists[slot], finalistErrors[slot]) = (ranked[c], work.Error);
                }
            }

            if (count <= 2)
            {
                ranked[..count].CopyTo(finalists);
            }

            for (int c = 0; c < Math.Min(count, 2); c++)
            {
                if (count > 2 && finalistErrors[c] == long.MaxValue)
                {
                    continue;
                }

                work.Reset(mode, finalists[c], 0, 0);
                FitPartitioned(px, m, work, refits, best.Error);
                if (work.Error < best.Error)
                {
                    (best, work) = (work, best);
                }
            }
        }

        Pack(best, block);
    }

    [ThreadStatic]
    private static Encoding[]? t_scratch;

    /// <summary>Ranks the partitions by the colours' spread about each subset's mean, smallest first, and returns how many it kept.</summary>
    private static int RankPartitions(ReadOnlySpan<int> px, int subsets, int available, int keep, Span<int> ranked, bool opaque)
    {
        Span<long> scores = stackalloc long[64];
        int channels = opaque ? 3 : 4;
        Span<int> sum = stackalloc int[12];
        Span<long> square = stackalloc long[3];
        Span<int> n = stackalloc int[3];
        for (int p = 0; p < available; p++)
        {
            uint shape = subsets == 2 ? BcTables.Partitions2[p] : BcTables.Partitions3[p];
            sum.Clear();
            square.Clear();
            n.Clear();
            for (int i = 0; i < 16; i++)
            {
                int s = (int)((shape >> (2 * i)) & 3);
                n[s]++;
                for (int c = 0; c < channels; c++)
                {
                    int v = px[(i * 4) + c];
                    sum[(s * 4) + c] += v;
                    square[s] += v * v;
                }
            }

            long score = 0;
            for (int s = 0; s < subsets; s++)
            {
                long mean = 0;
                for (int c = 0; c < channels; c++)
                {
                    mean += (long)sum[(s * 4) + c] * sum[(s * 4) + c];
                }

                score += n[s] == 0 ? 0 : square[s] - (mean / n[s]);
            }

            scores[p] = score;
        }

        // A stable selection of the smallest scores, so equal scores keep the partition order.
        int kept = 0;
        Span<bool> taken = stackalloc bool[64];
        for (; kept < keep; kept++)
        {
            int pick = -1;
            for (int p = 0; p < available; p++)
            {
                if (!taken[p] && (pick < 0 || scores[p] < scores[pick]))
                {
                    pick = p;
                }
            }

            taken[pick] = true;
            ranked[kept] = pick;
        }

        return kept;
    }

    /// <summary>Fits a one-subset mode: 6 (RGBA together), or 4 and 5 (colour and alpha apart, after the rotation).</summary>
    private static void FitSingle(ReadOnlySpan<int> source, ReadOnlySpan<byte> m, Encoding work, int refits, Encoding alpha)
    {
        Span<int> px = stackalloc int[64];
        source.CopyTo(px);
        if (work.Rotation != 0)
        {
            int swap = work.Rotation - 1;
            for (int i = 0; i < 16; i++)
            {
                (px[(i * 4) + swap], px[(i * 4) + 3]) = (px[(i * 4) + 3], px[(i * 4) + swap]);
            }
        }

        Span<int> all = stackalloc int[16];
        for (int i = 0; i < 16; i++)
        {
            all[i] = i;
        }

        if (m[5] == 0 || m[8] == 0)
        {
            // Mode 6: RGBA on one line, unique p-bits.
            work.Error = Fit(px, all, 0, 4, m[4], m[5], m[6], m[7], refits, work, primary: true);
            return;
        }

        // Modes 4 and 5: colour on its indices, alpha on the other set.
        int colorBits = work.Selection == 0 ? m[7] : m[8];
        int alphaBits = work.Selection == 0 ? m[8] : m[7];
        work.Error = Fit(px, all, 0, 3, m[4], 0, 0, colorBits, refits, work, primary: work.Selection == 0)
            + FitAlpha(px, m[5], alphaBits, refits, work, alpha, primary: work.Selection != 0);
    }

    /// <summary>Fits every subset of a partitioned mode, stopping once the error passes <paramref name="limit"/>.</summary>
    private static void FitPartitioned(ReadOnlySpan<int> px, ReadOnlySpan<byte> m, Encoding work, int refits, long limit)
    {
        uint shape = m[0] == 2 ? BcTables.Partitions2[work.Partition] : BcTables.Partitions3[work.Partition];
        Span<int> members = stackalloc int[16];
        int channels = m[5] > 0 ? 4 : 3;
        long error = 0;
        for (int s = 0; s < m[0] && error < limit; s++)
        {
            int count = 0;
            for (int i = 0; i < 16; i++)
            {
                if ((int)((shape >> (2 * i)) & 3) == s)
                {
                    members[count++] = i;
                }
            }

            error += Fit(px, members[..count], s, channels, m[4], m[5], m[6], m[7], refits, work, primary: true);
            if (channels == 3)
            {
                // Modes 0 to 3 decode alpha as 255.
                foreach (int i in members[..count])
                {
                    int d = 255 - px[(i * 4) + 3];
                    error += d * d;
                }
            }
        }

        work.Error = error;
    }

    /// <summary>
    /// Fits one subset's endpoints in <paramref name="channels"/> channels (3: RGB, 4: RGBA): along the principal axis, then by
    /// least squares on the chosen indices, trying each p-bit choice. Writes endpoints, p-bits and indices into
    /// <paramref name="work"/> and returns the squared error.
    /// </summary>
    private static long Fit(ReadOnlySpan<int> px, ReadOnlySpan<int> members, int subset, int channels, int colorBits, int alphaBits, int pbitMode, int indexBits, int refits, Encoding work, bool primary)
    {
        Span<float> lo = stackalloc float[4];
        Span<float> hi = stackalloc float[4];
        LineFit(px, members, channels, lo, hi);
        long best = long.MaxValue;
        Span<int> q0 = stackalloc int[4];
        Span<int> q1 = stackalloc int[4];
        Span<int> p = stackalloc int[2];
        Span<byte> idx = stackalloc byte[16];
        Span<int> bestQ0 = stackalloc int[4];
        Span<int> bestQ1 = stackalloc int[4];
        Span<int> bestP = stackalloc int[2];
        Span<byte> bestIdx = stackalloc byte[16];
        for (int round = 0; round <= refits; round++)
        {
            // Unique p-bits: every pair in the last round; before it, each endpoint's p-bit that quantizes it best.
            bool guess = pbitMode == 1 && round < refits;
            int options = guess ? 1 : pbitMode == 1 ? 4 : pbitMode == 2 ? 2 : 1;
            long roundBest = long.MaxValue;
            for (int option = 0; option < options; option++)
            {
                p[0] = guess ? BestPBit(lo, channels, colorBits, alphaBits) : pbitMode == 1 ? option & 1 : pbitMode == 2 ? option : 0;
                p[1] = guess ? BestPBit(hi, channels, colorBits, alphaBits) : pbitMode == 1 ? option >> 1 : p[0];
                for (int c = 0; c < channels; c++)
                {
                    int bits = c == 3 ? alphaBits : colorBits;
                    q0[c] = Quantize(lo[c], bits, pbitMode != 0, p[0]);
                    q1[c] = Quantize(hi[c], bits, pbitMode != 0, p[1]);
                }

                long error = Assign(px, members, channels, q0, q1, p, colorBits, alphaBits, pbitMode != 0, indexBits, idx);
                if (error < roundBest)
                {
                    roundBest = error;
                }

                if (error < best)
                {
                    best = error;
                    q0.CopyTo(bestQ0);
                    q1.CopyTo(bestQ1);
                    p.CopyTo(bestP);
                    idx.CopyTo(bestIdx);
                }
            }

            if (round == refits || !Refit(px, members, channels, indexBits, bestIdx, lo, hi))
            {
                break;
            }
        }

        // The anchor index's top bit must be 0: swap the endpoints and flip the indices if it is not.
        int anchor = AnchorOf(work, subset);
        int top = (1 << indexBits) - 1;
        if (bestIdx[anchor] > (top >> 1))
        {
            for (int c = 0; c < 4; c++)
            {
                (bestQ0[c], bestQ1[c]) = (bestQ1[c], bestQ0[c]);
            }

            (bestP[0], bestP[1]) = (bestP[1], bestP[0]);
            foreach (int i in members)
            {
                bestIdx[i] = (byte)(top - bestIdx[i]);
            }
        }

        for (int c = 0; c < channels; c++)
        {
            work.Endpoints[(subset * 8) + c] = bestQ0[c];
            work.Endpoints[(subset * 8) + 4 + c] = bestQ1[c];
        }

        work.PBits[subset * 2] = bestP[0];
        work.PBits[(subset * 2) + 1] = bestP[1];
        foreach (int i in members)
        {
            (primary ? work.Indices : work.Secondary)[i] = bestIdx[i];
        }

        return best;
    }

    /// <summary>Fits modes 4 and 5's alpha: its own endpoints and indices, no p-bits.</summary>
    private static long FitAlpha(ReadOnlySpan<int> px, int alphaBits, int indexBits, int refits, Encoding work, Encoding single, bool primary)
    {
        Span<int> alpha = stackalloc int[64];
        Span<int> all = stackalloc int[16];
        for (int i = 0; i < 16; i++)
        {
            alpha[i * 4] = px[(i * 4) + 3];
            all[i] = i;
        }

        single.Reset(work.Mode, 0, 0, 0);
        long error = Fit(alpha, all, 0, 1, alphaBits, 0, 0, indexBits, refits, single, primary: true);
        work.Endpoints[3] = single.Endpoints[0];
        work.Endpoints[7] = single.Endpoints[4];
        for (int i = 0; i < 16; i++)
        {
            (primary ? work.Indices : work.Secondary)[i] = single.Indices[i];
        }

        return error;
    }

    private static int AnchorOf(Encoding work, int subset)
    {
        if (subset == 0)
        {
            return 0;
        }

        int subsets = Modes[work.Mode * 9];
        return subsets == 2 ? BcTables.Anchors2[work.Partition] : subset == 1 ? BcTables.Anchors3Second[work.Partition] : BcTables.Anchors3Third[work.Partition];
    }

    /// <summary>Finds the line through the members' colours: the mean and the principal axis, cut at the outermost projections.</summary>
    private static void LineFit(ReadOnlySpan<int> px, ReadOnlySpan<int> members, int channels, Span<float> lo, Span<float> hi)
    {
        Span<float> mean = stackalloc float[4];
        foreach (int i in members)
        {
            for (int c = 0; c < channels; c++)
            {
                mean[c] += px[(i * 4) + c];
            }
        }

        for (int c = 0; c < channels; c++)
        {
            mean[c] /= members.Length;
        }

        Span<float> cov = stackalloc float[16];
        foreach (int i in members)
        {
            for (int a = 0; a < channels; a++)
            {
                float da = px[(i * 4) + a] - mean[a];
                for (int b = a; b < channels; b++)
                {
                    cov[(a * 4) + b] += da * (px[(i * 4) + b] - mean[b]);
                }
            }
        }

        for (int a = 0; a < channels; a++)
        {
            for (int b = 0; b < a; b++)
            {
                cov[(a * 4) + b] = cov[(b * 4) + a];
            }
        }

        Span<float> axis = stackalloc float[4];
        Span<float> next = stackalloc float[4];
        for (int c = 0; c < channels; c++)
        {
            axis[c] = 1;
        }

        for (int it = 0; it < 8; it++)
        {
            float largest = 0;
            for (int a = 0; a < channels; a++)
            {
                float v = 0;
                for (int b = 0; b < channels; b++)
                {
                    v += cov[(a * 4) + b] * axis[b];
                }

                next[a] = v;
                largest = Math.Max(largest, Math.Abs(v));
            }

            if (largest < 1e-6f)
            {
                break;
            }

            for (int c = 0; c < channels; c++)
            {
                axis[c] = next[c] / largest;
            }
        }

        float length = 0;
        for (int c = 0; c < channels; c++)
        {
            length += axis[c] * axis[c];
        }

        float tMin = 0, tMax = 0;
        if (length > 1e-6f)
        {
            tMin = float.MaxValue;
            tMax = float.MinValue;
            foreach (int i in members)
            {
                float t = 0;
                for (int c = 0; c < channels; c++)
                {
                    t += (px[(i * 4) + c] - mean[c]) * axis[c];
                }

                t /= length;
                tMin = Math.Min(tMin, t);
                tMax = Math.Max(tMax, t);
            }
        }

        for (int c = 0; c < channels; c++)
        {
            lo[c] = Math.Clamp(mean[c] + (tMin * axis[c]), 0, 255);
            hi[c] = Math.Clamp(mean[c] + (tMax * axis[c]), 0, 255);
        }
    }

    /// <summary>Solves for the endpoints that best fit the members' index weights; returns false when they are degenerate.</summary>
    private static bool Refit(ReadOnlySpan<int> px, ReadOnlySpan<int> members, int channels, int indexBits, ReadOnlySpan<byte> idx, Span<float> lo, Span<float> hi)
    {
        ReadOnlySpan<byte> weights = WeightsFor(indexBits);
        float aa = 0, ab = 0, bb = 0;
        Span<float> av = stackalloc float[4];
        Span<float> bv = stackalloc float[4];
        foreach (int i in members)
        {
            float w = weights[idx[i]] / 64f;
            float a = 1 - w;
            aa += a * a;
            ab += a * w;
            bb += w * w;
            for (int c = 0; c < channels; c++)
            {
                av[c] += a * px[(i * 4) + c];
                bv[c] += w * px[(i * 4) + c];
            }
        }

        float det = (aa * bb) - (ab * ab);
        if (Math.Abs(det) < 1e-6f)
        {
            return false;
        }

        for (int c = 0; c < channels; c++)
        {
            lo[c] = Math.Clamp(((bb * av[c]) - (ab * bv[c])) / det, 0, 255);
            hi[c] = Math.Clamp(((aa * bv[c]) - (ab * av[c])) / det, 0, 255);
        }

        return true;
    }

    /// <summary>Picks each member's nearest palette entry, with the decoder's endpoint expansion and interpolation; returns the squared error.</summary>
    private static long Assign(ReadOnlySpan<int> px, ReadOnlySpan<int> members, int channels, ReadOnlySpan<int> q0, ReadOnlySpan<int> q1, ReadOnlySpan<int> p, int colorBits, int alphaBits, bool pbits, int indexBits, Span<byte> idx)
    {
        Span<int> e0 = stackalloc int[4];
        Span<int> e1 = stackalloc int[4];
        for (int c = 0; c < channels; c++)
        {
            int bits = (c == 3 ? alphaBits : colorBits) + (pbits ? 1 : 0);
            e0[c] = Expand(pbits ? (q0[c] << 1) | p[0] : q0[c], bits);
            e1[c] = Expand(pbits ? (q1[c] << 1) | p[1] : q1[c], bits);
        }

        ReadOnlySpan<byte> weights = WeightsFor(indexBits);
        int entries = 1 << indexBits;
        Span<int> palette = stackalloc int[16 * 4];
        for (int k = 0; k < entries; k++)
        {
            for (int c = 0; c < channels; c++)
            {
                palette[(k * 4) + c] = (((64 - weights[k]) * e0[c]) + (weights[k] * e1[c]) + 32) >> 6;
            }
        }

        // The palette lies on the line between the endpoints: project each pixel onto it for the nearest index, then
        // check the neighbours, which covers the interpolation's rounding.
        int dd = 0;
        Span<int> dir = stackalloc int[4];
        for (int c = 0; c < channels; c++)
        {
            dir[c] = e1[c] - e0[c];
            dd += dir[c] * dir[c];
        }

        long total = 0;
        foreach (int i in members)
        {
            int bestIndex = 0;
            int bestError = int.MaxValue;
            int guess = 0;
            if (dd > 0)
            {
                int dot = 0;
                for (int c = 0; c < channels; c++)
                {
                    dot += (px[(i * 4) + c] - e0[c]) * dir[c];
                }

                guess = Math.Clamp((int)MathF.Round(dot * (entries - 1) / (float)dd), 0, entries - 1);
            }

            for (int k = Math.Max(0, guess - 1); k <= Math.Min(entries - 1, guess + 1); k++)
            {
                int error = 0;
                for (int c = 0; c < channels; c++)
                {
                    int d = palette[(k * 4) + c] - px[(i * 4) + c];
                    error += d * d;
                }

                if (error < bestError)
                {
                    (bestIndex, bestError) = (k, error);
                }
            }

            idx[i] = (byte)bestIndex;
            total += bestError;
        }

        return total;
    }

    /// <summary>Returns the p-bit with which an endpoint's channels quantize closest.</summary>
    private static int BestPBit(ReadOnlySpan<float> endpoint, int channels, int colorBits, int alphaBits)
    {
        Span<float> error = stackalloc float[2];
        for (int p = 0; p < 2; p++)
        {
            for (int c = 0; c < channels; c++)
            {
                int bits = c == 3 ? alphaBits : colorBits;
                float d = Expand((Quantize(endpoint[c], bits, true, p) << 1) | p, bits + 1) - endpoint[c];
                error[p] += d * d;
            }
        }

        return error[1] < error[0] ? 1 : 0;
    }

    private static int Quantize(float value, int bits, bool pbit, int p)
    {
        if (!pbit)
        {
            return Math.Clamp((int)MathF.Round(value * ((1 << bits) - 1) / 255f), 0, (1 << bits) - 1);
        }

        // With a p-bit the stored level is (q << 1) | p at bits + 1.
        float full = value * ((1 << (bits + 1)) - 1) / 255f;
        return Math.Clamp((int)MathF.Round((full - p) / 2), 0, (1 << bits) - 1);
    }

    private static int Expand(int value, int bits)
    {
        value <<= 8 - bits;
        return value | (value >> bits);
    }

    private static ReadOnlySpan<byte> WeightsFor(int bits) => bits switch
    {
        2 => Weights2,
        3 => Weights3,
        _ => Weights4,
    };

    /// <summary>Writes the block's fields in the specification's order.</summary>
    private static void Pack(Encoding e, Span<byte> block)
    {
        ReadOnlySpan<byte> m = Modes.Slice(e.Mode * 9, 9);
        int subsets = m[0];
        UInt128 bits = 0;
        int at = 0;
        void Put(int value, int count)
        {
            bits |= (UInt128)(ulong)(value & ((1 << count) - 1)) << at;
            at += count;
        }

        Put(1 << e.Mode, e.Mode + 1);
        Put(e.Partition, m[1]);
        Put(e.Rotation, m[2]);
        Put(e.Selection, m[3]);
        for (int c = 0; c < 3; c++)
        {
            for (int s = 0; s < subsets; s++)
            {
                Put(e.Endpoints[(s * 8) + c], m[4]);
                Put(e.Endpoints[(s * 8) + 4 + c], m[4]);
            }
        }

        if (m[5] > 0)
        {
            for (int s = 0; s < subsets; s++)
            {
                Put(e.Endpoints[(s * 8) + 3], m[5]);
                Put(e.Endpoints[(s * 8) + 7], m[5]);
            }
        }

        if (m[6] == 1)
        {
            for (int s = 0; s < subsets; s++)
            {
                Put(e.PBits[s * 2], 1);
                Put(e.PBits[(s * 2) + 1], 1);
            }
        }
        else if (m[6] == 2)
        {
            for (int s = 0; s < subsets; s++)
            {
                Put(e.PBits[s * 2], 1);
            }
        }

        int anchor1 = subsets == 2 ? BcTables.Anchors2[e.Partition] : subsets == 3 ? BcTables.Anchors3Second[e.Partition] : -1;
        int anchor2 = subsets == 3 ? BcTables.Anchors3Third[e.Partition] : -1;
        for (int i = 0; i < 16; i++)
        {
            bool anchor = i == 0 || i == anchor1 || i == anchor2;
            Put(e.Indices[i], anchor ? m[7] - 1 : m[7]);
        }

        for (int i = 0; m[8] > 0 && i < 16; i++)
        {
            Put(e.Secondary[i], i == 0 ? m[8] - 1 : m[8]);
        }

        BinaryPrimitives.WriteUInt64LittleEndian(block, (ulong)bits);
        BinaryPrimitives.WriteUInt64LittleEndian(block[8..], (ulong)(bits >> 64));
    }

    /// <summary>One way to code the block: the mode's choices, quantized endpoints (subset × 8: end 0 RGBA, end 1 RGBA), p-bits and indices.</summary>
    private sealed class Encoding
    {
        public int Mode { get; private set; }

        public int Partition { get; private set; }

        public int Rotation { get; private set; }

        public int Selection { get; private set; }

        public long Error { get; set; }

        public int[] Endpoints { get; private init; } = new int[24];

        public int[] PBits { get; private init; } = new int[6];

        public byte[] Indices { get; private init; } = new byte[16];

        public byte[] Secondary { get; private init; } = new byte[16];

        public void Reset(int mode, int partition, int rotation, int selection)
        {
            (Mode, Partition, Rotation, Selection, Error) = (mode, partition, rotation, selection, 0);
            Array.Clear(Endpoints);
            Array.Clear(PBits);
            Array.Clear(Indices);
            Array.Clear(Secondary);
        }
    }
}
