using System.Buffers.Binary;

namespace Egami.Formats.Extended.Dds.Blocks;

/// <summary>
/// Encodes BC6H blocks, unsigned (UF16) and signed (SF16), written from the Direct3D 11 BC6H format specification. Colours are
/// fitted in the decoder's unquantized domain, where the half-float bits interpolate linearly: endpoints along the principal axis,
/// quantized to each mode's precision (as deltas from the first endpoint in the transformed modes, which a block can use only
/// when its deltas fit), indices chosen with the decoder's exact arithmetic, then refitted by least squares. The effort picks the
/// modes (the one-region 10-bit mode at the lowest, the two-region modes with more partitions above) and the refits. The error
/// is the squared difference of the half-float bits as integers, which weighs light and dark alike, as HDR needs.
/// </summary>
internal static class Bc6hEncoder
{
    // Per mode index: the mode's code, regions, transformed, endpoint bits, delta bits of red, green and blue.
    private static ReadOnlySpan<byte> Modes =>
    [
        0x00, 2, 1, 10, 5, 5, 5,
        0x01, 2, 1, 7, 6, 6, 6,
        0x02, 2, 1, 11, 5, 4, 4,
        0x06, 2, 1, 11, 4, 5, 4,
        0x0A, 2, 1, 11, 4, 4, 5,
        0x0E, 2, 1, 9, 5, 5, 5,
        0x12, 2, 1, 8, 6, 5, 5,
        0x16, 2, 1, 8, 5, 6, 5,
        0x1A, 2, 1, 8, 5, 5, 6,
        0x1E, 2, 0, 6, 6, 6, 6,
        0x03, 1, 0, 10, 10, 10, 10,
        0x07, 1, 1, 11, 9, 9, 9,
        0x0B, 1, 1, 12, 8, 8, 8,
        0x0F, 1, 1, 16, 4, 4, 4,
    ];

    private static ReadOnlySpan<byte> Weights3 => [0, 9, 18, 27, 37, 46, 55, 64];

    private static ReadOnlySpan<byte> Weights4 => [0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64];

    /// <summary>Encodes 16 pixels of four half floats (raw bits, row order; alpha ignored) into a 16-byte block at effort 0 to 6.</summary>
    public static void Encode(ReadOnlySpan<ushort> rgba, Span<byte> block, bool signed, int effort)
    {
        // Each channel as the half's bits read as an integer, sign and magnitude for SF16; UF16 drops negatives and NaN.
        Span<int> h = stackalloc int[48];
        Span<int> u = stackalloc int[48];
        for (int i = 0; i < 16; i++)
        {
            for (int c = 0; c < 3; c++)
            {
                int bits = rgba[(i * 4) + c];
                int magnitude = Math.Min(bits & 0x7FFF, 0x7BFF);
                int value = (bits & 0x8000) != 0 ? (signed ? -magnitude : 0) : magnitude;
                h[(i * 3) + c] = value;

                // The decoder finishes an interpolated value v as v × 31 / 64 (31 / 32 when signed); invert that.
                u[(i * 3) + c] = signed ? (value * 32 / 31) : (value * 64 / 31);
            }
        }

        int maxPartitions = effort switch { <= 2 => 0, 3 => 4, 4 => 8, 5 => 16, _ => 32 };
        int refits = effort switch { <= 1 => 0, <= 4 => 1, _ => 2 };
        Candidate best = new() { Error = long.MaxValue };
        Candidate work = new();
        Span<int> ranked = stackalloc int[32];
        int partitions = maxPartitions == 0 ? 0 : Rank(u, maxPartitions, ranked);
        if (partitions > 2)
        {
            // A quick fit of each ranked partition in mode 10 (6-bit endpoints, which always fit) picks the two that every
            // two-region mode then tries.
            Span<long> errors = stackalloc long[32];
            for (int s = 0; s < partitions; s++)
            {
                work.Reset(9, ranked[s]);
                errors[s] = Fit(h, u, Modes.Slice(9 * 7, 7), signed, 0, work, long.MaxValue) ? work.Error : long.MaxValue;
            }

            for (int k = 0; k < 2; k++)
            {
                int pick = k;
                for (int s = k + 1; s < partitions; s++)
                {
                    if (errors[s] < errors[pick])
                    {
                        pick = s;
                    }
                }

                (ranked[k], ranked[pick]) = (ranked[pick], ranked[k]);
                (errors[k], errors[pick]) = (errors[pick], errors[k]);
            }

            partitions = 2;
        }

        for (int index = 0; index < 14; index++)
        {
            ReadOnlySpan<byte> m = Modes.Slice(index * 7, 7);
            if (m[1] == 1 ? index != 10 && effort == 0 : partitions == 0)
            {
                continue;
            }

            int shapes = m[1] == 1 ? 1 : partitions;
            for (int s = 0; s < shapes; s++)
            {
                work.Reset(index, m[1] == 1 ? 0 : ranked[s]);
                if (Fit(h, u, m, signed, refits, work, best.Error) && work.Error < best.Error)
                {
                    (best, work) = (work, best);
                }
            }
        }

        if (best.Error == long.MaxValue)
        {
            // Mode 11 always fits; this is never reached.
            block[..16].Clear();
            return;
        }

        Pack(best, block);
    }

    /// <summary>Ranks the 32 two-region partitions by the spread about each region's mean, smallest first.</summary>
    private static int Rank(ReadOnlySpan<int> u, int keep, Span<int> ranked)
    {
        Span<long> scores = stackalloc long[32];
        Span<long> sum = stackalloc long[3];
        for (int p = 0; p < 32; p++)
        {
            uint shape = BcTables.Partitions2[p];
            long score = 0;
            for (int region = 0; region < 2; region++)
            {
                long n = 0, square = 0;
                sum.Clear();
                for (int i = 0; i < 16; i++)
                {
                    if ((int)((shape >> (2 * i)) & 3) != region)
                    {
                        continue;
                    }

                    n++;
                    for (int c = 0; c < 3; c++)
                    {
                        long v = u[(i * 3) + c];
                        sum[c] += v;
                        square += v * v;
                    }
                }

                score += n == 0 ? 0 : square - (((sum[0] * sum[0]) + (sum[1] * sum[1]) + (sum[2] * sum[2])) / n);
            }

            scores[p] = score;
        }

        Span<bool> taken = stackalloc bool[32];
        for (int k = 0; k < keep; k++)
        {
            int pick = -1;
            for (int p = 0; p < 32; p++)
            {
                if (!taken[p] && (pick < 0 || scores[p] < scores[pick]))
                {
                    pick = p;
                }
            }

            taken[pick] = true;
            ranked[k] = pick;
        }

        return keep;
    }

    /// <summary>Fits every region in one mode and partition; returns false when the mode cannot code the block.</summary>
    private static bool Fit(ReadOnlySpan<int> h, ReadOnlySpan<int> u, ReadOnlySpan<byte> m, bool signed, int refits, Candidate work, long limit)
    {
        int regions = m[1];
        uint shape = regions == 2 ? BcTables.Partitions2[work.Partition] : 0;
        int bits = m[3];
        Span<float> lo = stackalloc float[6];
        Span<float> hi = stackalloc float[6];
        Span<int> members = stackalloc int[32];
        Span<int> counts = stackalloc int[2];
        for (int i = 0; i < 16; i++)
        {
            int r = (int)((shape >> (2 * i)) & 3);
            members[(r * 16) + counts[r]++] = i;
        }

        for (int r = 0; r < regions; r++)
        {
            LineFit(u, members.Slice(r * 16, counts[r]), lo.Slice(r * 3, 3), hi.Slice(r * 3, 3));
        }

        bool found = false;
        long bestError = long.MaxValue;
        Span<int> q = stackalloc int[12];
        Span<int> bestQ = stackalloc int[12];
        Span<byte> idx = stackalloc byte[16];
        Span<byte> bestIdx = stackalloc byte[16];
        for (int round = 0; round <= refits; round++)
        {
            for (int r = 0; r < regions; r++)
            {
                for (int c = 0; c < 3; c++)
                {
                    q[(r * 6) + c] = Quantize(lo[(r * 3) + c], bits, signed);
                    q[(r * 6) + 3 + c] = Quantize(hi[(r * 3) + c], bits, signed);
                }
            }

            long error = 0;
            for (int r = 0; r < regions && error < limit; r++)
            {
                error += Assign(h, members.Slice(r * 16, counts[r]), q.Slice(r * 6, 6), bits, signed, regions == 2 ? 3 : 4, idx);
            }

            if (error < bestError)
            {
                bestError = error;
                q.CopyTo(bestQ);
                idx.CopyTo(bestIdx);
                found = true;
            }

            if (round == refits || error >= limit)
            {
                break;
            }

            for (int r = 0; r < regions; r++)
            {
                Refit(u, members.Slice(r * 16, counts[r]), regions == 2 ? 3 : 4, idx, lo.Slice(r * 3, 3), hi.Slice(r * 3, 3));
            }
        }

        if (!found || bestError >= limit)
        {
            return false;
        }

        // Each region's anchor index must have its top bit clear: swap that region's endpoints and flip its indices if not.
        int indexBits = regions == 2 ? 3 : 4;
        int top = (1 << indexBits) - 1;
        for (int r = 0; r < regions; r++)
        {
            int anchor = r == 0 ? 0 : BcTables.Anchors2[work.Partition];
            if (bestIdx[anchor] > (top >> 1))
            {
                for (int c = 0; c < 3; c++)
                {
                    (bestQ[(r * 6) + c], bestQ[(r * 6) + 3 + c]) = (bestQ[(r * 6) + 3 + c], bestQ[(r * 6) + c]);
                }

                foreach (int i in members.Slice(r * 16, counts[r]))
                {
                    bestIdx[i] = (byte)(top - bestIdx[i]);
                }
            }
        }

        // The transformed modes store endpoints after the first as deltas, which must fit their bits.
        if (m[2] != 0)
        {
            for (int e = 1; e < regions * 2; e++)
            {
                for (int c = 0; c < 3; c++)
                {
                    int delta = bestQ[(e * 3) + c] - bestQ[c];
                    int half = 1 << (m[4 + c] - 1);
                    if (delta < -half || delta >= half)
                    {
                        return false;
                    }
                }
            }
        }

        bestQ.CopyTo(work.Endpoints);
        bestIdx.CopyTo(work.Indices);
        work.Error = bestError;
        return true;
    }

    private static void LineFit(ReadOnlySpan<int> u, ReadOnlySpan<int> members, Span<float> lo, Span<float> hi)
    {
        Span<float> mean = stackalloc float[3];
        foreach (int i in members)
        {
            for (int c = 0; c < 3; c++)
            {
                mean[c] += u[(i * 3) + c];
            }
        }

        for (int c = 0; c < 3; c++)
        {
            mean[c] /= Math.Max(1, members.Length);
        }

        float xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
        foreach (int i in members)
        {
            float dx = u[i * 3] - mean[0], dy = u[(i * 3) + 1] - mean[1], dz = u[(i * 3) + 2] - mean[2];
            xx += dx * dx;
            xy += dx * dy;
            xz += dx * dz;
            yy += dy * dy;
            yz += dy * dz;
            zz += dz * dz;
        }

        float ax = 1, ay = 1, az = 1;
        for (int it = 0; it < 8; it++)
        {
            float nx = (xx * ax) + (xy * ay) + (xz * az);
            float ny = (xy * ax) + (yy * ay) + (yz * az);
            float nz = (xz * ax) + (yz * ay) + (zz * az);
            float largest = Math.Max(Math.Abs(nx), Math.Max(Math.Abs(ny), Math.Abs(nz)));
            if (largest < 1e-6f)
            {
                break;
            }

            (ax, ay, az) = (nx / largest, ny / largest, nz / largest);
        }

        float length = (ax * ax) + (ay * ay) + (az * az);
        float tMin = float.MaxValue, tMax = float.MinValue;
        foreach (int i in members)
        {
            float t = (((u[i * 3] - mean[0]) * ax) + ((u[(i * 3) + 1] - mean[1]) * ay) + ((u[(i * 3) + 2] - mean[2]) * az)) / length;
            tMin = Math.Min(tMin, t);
            tMax = Math.Max(tMax, t);
        }

        if (members.Length == 0 || length < 1e-6f)
        {
            tMin = tMax = 0;
        }

        lo[0] = mean[0] + (tMin * ax);
        lo[1] = mean[1] + (tMin * ay);
        lo[2] = mean[2] + (tMin * az);
        hi[0] = mean[0] + (tMax * ax);
        hi[1] = mean[1] + (tMax * ay);
        hi[2] = mean[2] + (tMax * az);
    }

    private static void Refit(ReadOnlySpan<int> u, ReadOnlySpan<int> members, int indexBits, ReadOnlySpan<byte> idx, Span<float> lo, Span<float> hi)
    {
        ReadOnlySpan<byte> weights = indexBits == 3 ? Weights3 : Weights4;
        float aa = 0, ab = 0, bb = 0;
        Span<float> av = stackalloc float[3];
        Span<float> bv = stackalloc float[3];
        foreach (int i in members)
        {
            float w = weights[idx[i]] / 64f;
            float a = 1 - w;
            aa += a * a;
            ab += a * w;
            bb += w * w;
            for (int c = 0; c < 3; c++)
            {
                av[c] += a * u[(i * 3) + c];
                bv[c] += w * u[(i * 3) + c];
            }
        }

        float det = (aa * bb) - (ab * ab);
        if (Math.Abs(det) < 1e-3f)
        {
            return;
        }

        for (int c = 0; c < 3; c++)
        {
            lo[c] = ((bb * av[c]) - (ab * bv[c])) / det;
            hi[c] = ((aa * bv[c]) - (ab * av[c])) / det;
        }
    }

    /// <summary>Returns the endpoint level at <paramref name="bits"/> whose unquantized value is nearest <paramref name="value"/>.</summary>
    private static int Quantize(float value, int bits, bool signed)
    {
        int max = signed ? (1 << (bits - 1)) - 1 : (1 << bits) - 1;
        int min = signed ? -max : 0;
        float scale = signed ? 32768f / (1 << (bits - 1)) : 65536f / (1 << bits);
        int guess = Math.Clamp((int)MathF.Round(value / scale), min, max);
        int best = guess;
        float bestError = float.MaxValue;
        for (int q = Math.Max(min, guess - 1); q <= Math.Min(max, guess + 1); q++)
        {
            float d = Math.Abs(Unquantize(q, bits, signed) - value);
            if (d < bestError)
            {
                (best, bestError) = (q, d);
            }
        }

        return best;
    }

    /// <summary>Assigns each member its nearest interpolated colour, with the decoder's arithmetic; returns the squared half-bit error.</summary>
    private static long Assign(ReadOnlySpan<int> h, ReadOnlySpan<int> members, ReadOnlySpan<int> q, int bits, bool signed, int indexBits, Span<byte> idx)
    {
        ReadOnlySpan<byte> weights = indexBits == 3 ? Weights3 : Weights4;
        int entries = 1 << indexBits;
        Span<int> palette = stackalloc int[48];
        for (int c = 0; c < 3; c++)
        {
            int a = Unquantize(q[c], bits, signed);
            int b = Unquantize(q[3 + c], bits, signed);
            for (int k = 0; k < entries; k++)
            {
                int v = (((64 - weights[k]) * a) + (weights[k] * b) + 32) >> 6;
                palette[(k * 3) + c] = Finish(v, signed);
            }
        }

        // The palette runs along a line: project each pixel for the nearest index, then check the neighbours.
        long dd = 0;
        Span<long> dir = stackalloc long[3];
        for (int c = 0; c < 3; c++)
        {
            dir[c] = palette[((entries - 1) * 3) + c] - palette[c];
            dd += dir[c] * dir[c];
        }

        long total = 0;
        foreach (int i in members)
        {
            long bestError = long.MaxValue;
            int bestIndex = 0;
            int guess = 0;
            if (dd > 0)
            {
                long dot = 0;
                for (int c = 0; c < 3; c++)
                {
                    dot += (h[(i * 3) + c] - palette[c]) * dir[c];
                }

                guess = (int)Math.Clamp(Math.Round(dot * (entries - 1) / (double)dd), 0, entries - 1);
            }

            for (int k = Math.Max(0, guess - 1); k <= Math.Min(entries - 1, guess + 1); k++)
            {
                long e = 0;
                for (int c = 0; c < 3; c++)
                {
                    long d = palette[(k * 3) + c] - h[(i * 3) + c];
                    e += d * d;
                }

                if (e < bestError)
                {
                    (bestError, bestIndex) = (e, k);
                }
            }

            idx[i] = (byte)bestIndex;
            total += bestError;
        }

        return total;
    }

    /// <summary>The decoder's unquantization of an endpoint level.</summary>
    private static int Unquantize(int value, int bits, bool signed)
    {
        if (!signed)
        {
            if (bits >= 15)
            {
                return value;
            }

            return value == 0 ? 0 : value == (1 << bits) - 1 ? 0xFFFF : ((value << 16) + 0x8000) >> bits;
        }

        if (bits >= 16)
        {
            return value;
        }

        int magnitude = Math.Abs(value);
        int result = magnitude == 0 ? 0 : magnitude >= (1 << (bits - 1)) - 1 ? 0x7FFF : ((magnitude << 15) + 0x4000) >> (bits - 1);
        return value < 0 ? -result : result;
    }

    /// <summary>The decoder's last step, to the half's bits as a signed integer.</summary>
    private static int Finish(int value, bool signed)
    {
        if (!signed)
        {
            return (value * 31) >> 6;
        }

        return value < 0 ? -(((-value) * 31) >> 5) : (value * 31) >> 5;
    }

    /// <summary>Writes the header bits in the mode's layout, then the indices.</summary>
    private static void Pack(Candidate c, Span<byte> block)
    {
        ReadOnlySpan<byte> m = Modes.Slice(c.Mode * 7, 7);
        int regions = m[1];
        bool transformed = m[2] != 0;
        Span<int> fields = stackalloc int[12];
        for (int e = 0; e < regions * 2; e++)
        {
            for (int ch = 0; ch < 3; ch++)
            {
                int value = c.Endpoints[(e * 3) + ch];
                if (e > 0 && transformed)
                {
                    value -= c.Endpoints[ch];
                }

                fields[(ch * 4) + e] = value;
            }
        }

        UInt128 word = 0;
        byte[] header = BcTables.Bc6hHeaders[c.Mode];
        int headerBits = regions == 2 ? 82 : 65;
        for (int bit = 0; bit < headerBits; bit++)
        {
            int field = header[bit] >> 4;
            int which = header[bit] & 15;
            int value = field switch
            {
                1 => m[0],
                2 => c.Partition,
                >= 3 => fields[field - 3],
                _ => 0,
            };
            if (((value >> which) & 1) != 0)
            {
                word |= (UInt128)1 << bit;
            }
        }

        int at = headerBits;
        int indexBits = regions == 2 ? 3 : 4;
        int anchor = regions == 2 ? BcTables.Anchors2[c.Partition] : -1;
        for (int i = 0; i < 16; i++)
        {
            int count = i == 0 || i == anchor ? indexBits - 1 : indexBits;
            word |= (UInt128)(ulong)(c.Indices[i] & ((1 << count) - 1)) << at;
            at += count;
        }

        BinaryPrimitives.WriteUInt64LittleEndian(block, (ulong)word);
        BinaryPrimitives.WriteUInt64LittleEndian(block[8..], (ulong)(word >> 64));
    }

    /// <summary>One way to code the block.</summary>
    private sealed class Candidate
    {
        public int Mode { get; private set; }

        public int Partition { get; private set; }

        public long Error { get; set; }

        public int[] Endpoints { get; } = new int[12];

        public byte[] Indices { get; } = new byte[16];

        public void Reset(int mode, int partition)
        {
            (Mode, Partition, Error) = (mode, partition, long.MaxValue);
        }
    }
}
