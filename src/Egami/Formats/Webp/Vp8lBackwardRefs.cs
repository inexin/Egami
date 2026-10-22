using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Egami.Formats.Webp;

/// <summary>
/// Turns pixels into VP8L tokens (literals and backward references): hash chains over pixel pairs find the longest earlier match
/// within reach, the nearby plane codes (the pixel to the left and the one above) are always tried, and the parse is greedy with
/// one step of lookahead, or, at high effort, chosen by a shortest-path search over a bit-cost model.
/// </summary>
internal static class Vp8lBackwardRefs
{
    internal const int MinLength = 3;
    internal const int MaxLength = 4096;
    private const int HashBits = 18;

    /// <summary>Settings that scale with effort: how many chain links to follow, how far back to look, and how to parse.</summary>
    internal readonly record struct Settings(int ChainDepth, int Window, bool Lazy, bool CostBased)
    {
        /// <summary>Gets how many threads the match search may use; the result does not depend on it.</summary>
        internal int Parallelism { get; init; } = 1;
    }

    /// <summary>Returns the settings for an effort level (0 to 6) on the main image.</summary>
    internal static Settings ForEffort(int effort) => effort switch
    {
        0 => new Settings(10, 1 << 17, true, false),
        1 => new Settings(24, Vp8lDecoder.MaxDistance, true, true),
        2 => new Settings(32, Vp8lDecoder.MaxDistance, true, true),
        3 => new Settings(48, Vp8lDecoder.MaxDistance, true, true),
        4 => new Settings(64, Vp8lDecoder.MaxDistance, true, true),
        5 => new Settings(128, Vp8lDecoder.MaxDistance, true, true),
        _ => new Settings(512, Vp8lDecoder.MaxDistance, true, true),
    };

    /// <summary>
    /// Writes the tokens for <paramref name="pixels"/> into <paramref name="tokens"/> and returns how many. The color cache size
    /// (up to <paramref name="maxCacheBits"/>) is chosen from a greedy parse; a cost-based parse then knows which literals the
    /// cache will hold, since the cache's contents at a position do not depend on how the pixels before it were coded. The tokens
    /// are literals and copies; the caller turns literals into cache hits with <see cref="Vp8lCache.Apply"/>.
    /// </summary>
    internal static int Build(ReadOnlySpan<uint> pixels, int width, Settings settings, int maxCacheBits, Span<ulong> tokens, out int cacheBits)
    {
        int count = pixels.Length;
        cacheBits = 0;
        if (count < 2)
        {
            for (int i = 0; i < count; i++)
            {
                tokens[i] = Vp8lEncoder.Token.Pixel(pixels[i]);
            }

            return count;
        }

        int[] matchArray = ArrayPool<int>.Shared.Rent(count * 3);
        try
        {
            Span<int> lengths = matchArray.AsSpan(0, count);
            Span<int> distances = matchArray.AsSpan(count, count);
            Span<int> near = matchArray.AsSpan(2 * count, count);
            FindMatches(pixels, width, settings, lengths, distances, near);
            int n = GreedyParse(pixels, width, settings.Lazy, LiteralBits(pixels), lengths, distances, tokens);
            cacheBits = Vp8lCache.ChooseBits(tokens[..n], pixels, maxCacheBits);
            if (!settings.CostBased)
            {
                return n;
            }

            // The cache size chosen from the greedy parse may not suit the cost-based one: choose again from its tokens, and keep
            // whichever of the two parses is estimated smaller.
            ulong[] firstArray = ArrayPool<ulong>.Shared.Rent(count);
            try
            {
                Span<ulong> greedy = firstArray.AsSpan(0, n);
                tokens[..n].CopyTo(greedy);
                int first = CostParse(pixels, width, lengths, distances, near, tokens, greedy, cacheBits);
                int again = Vp8lCache.ChooseBits(tokens[..first], pixels, maxCacheBits);
                if (again == cacheBits)
                {
                    return first;
                }

                Span<ulong> firstTokens = firstArray.AsSpan(0, Math.Max(first, n));
                tokens[..first].CopyTo(firstTokens);
                double firstBits = Estimate(firstTokens[..first], pixels, cacheBits);
                int second = CostParse(pixels, width, lengths, distances, near, tokens, firstTokens[..first], again);
                if (Estimate(tokens[..second], pixels, again) <= firstBits)
                {
                    cacheBits = again;
                    return second;
                }

                firstTokens[..first].CopyTo(tokens);
                return first;
            }
            finally
            {
                ArrayPool<ulong>.Shared.Return(firstArray);
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(matchArray);
        }
    }

    /// <summary>The pixels searched as one unit; segments of the image are searched independently, in parallel when allowed.</summary>
    private const int Segment = 1 << 16;

    /// <summary>
    /// Finds, for every position, the longest earlier match (length 0 when none of at least <see cref="MinLength"/>) and its
    /// distance. The hash chains are built first; then positions are searched from the end backwards, and a match found at one
    /// position is extended to the left for as long as the pixels before it match at the same distance, so those positions need
    /// no search of their own and every position knows the match that runs furthest from it. The left and upper pixels, whose
    /// codes are cheapest, are tried first, and a far match must be clearly longer to replace a near one. The best of those two
    /// nearby matches is kept apart in <paramref name="near"/> (positive for the left pixel, negative for the one above).
    /// </summary>
    private static unsafe void FindMatches(ReadOnlySpan<uint> pixels, int width, Settings settings, Span<int> lengths, Span<int> distances, Span<int> near)
    {
        int count = pixels.Length;
        int[] headArray = ArrayPool<int>.Shared.Rent(1 << HashBits);
        int[] chainArray = ArrayPool<int>.Shared.Rent(count);
        try
        {
            Span<int> head = headArray.AsSpan(0, 1 << HashBits);
            Span<int> chain = chainArray.AsSpan(0, count);
            head.Fill(-1);
            for (int i = 0; i + 1 < count; i++)
            {
                int h = Hash(pixels, i);
                chain[i] = head[h];
                head[h] = i;
            }

            lengths[count - 1] = 0;
            distances[count - 1] = 0;
            near[count - 1] = 0;

            // Fixed segments, whatever the parallelism, so the file is the same on any machine: a match found near a segment's
            // start does not extend left into the segment before.
            int segments = (count - 1 + Segment - 1) / Segment;
            if (settings.Parallelism <= 1 || segments == 1)
            {
                for (int k = 0; k < segments; k++)
                {
                    SearchSegment(pixels, width, settings, chain, lengths, distances, near, k * Segment, Math.Min((k + 1) * Segment, count - 1));
                }

                return;
            }

            fixed (uint* pp = pixels)
            fixed (int* pc = chain, pl = lengths, pd = distances, pn = near)
            {
                nint ap = (nint)pp, ac = (nint)pc, al = (nint)pl, ad = (nint)pd, an = (nint)pn;
                Parallel.For(0, segments, new ParallelOptions { MaxDegreeOfParallelism = settings.Parallelism }, k =>
                {
                    SearchSegment(
                        new ReadOnlySpan<uint>((void*)ap, count), width, settings, new ReadOnlySpan<int>((void*)ac, count),
                        new Span<int>((void*)al, count), new Span<int>((void*)ad, count), new Span<int>((void*)an, count),
                        k * Segment, Math.Min((k + 1) * Segment, count - 1));
                });
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(headArray);
            ArrayPool<int>.Shared.Return(chainArray);
        }
    }

    /// <summary>
    /// Finds the longest match at each position from <paramref name="end"/> − 1 down to <paramref name="start"/>: the pixel above
    /// and to the left first, then the hash chain; a match found is extended leftwards while the pixels before keep matching.
    /// </summary>
    private static void SearchSegment(
        ReadOnlySpan<uint> pixels, int width, Settings settings, ReadOnlySpan<int> chain, Span<int> lengths, Span<int> distances, Span<int> near, int start, int end)
    {
        int count = pixels.Length;
        int farPenalty = 16;
        for (int pos = end - 1; pos >= start;)
        {
            int limit = Math.Min(MaxLength, count - pos);
            int bestLength = 0;
            int bestDistance = 0;
            int nearLength = 0;
            // The left pixel wins a tie with the one above, so the one above is compared only when the left match stops short.
            if (pos >= 1)
            {
                (bestLength, bestDistance) = (pixels.Slice(pos - 1, limit).CommonPrefixLength(pixels.Slice(pos, limit)), 1);
            }

            if (pos >= width && bestLength < limit)
            {
                int above = pixels.Slice(pos - width, limit).CommonPrefixLength(pixels.Slice(pos, limit));
                if (above > bestLength)
                {
                    (bestLength, bestDistance) = (above, width);
                }
            }

            if (bestLength >= MinLength)
            {
                nearLength = bestDistance == 1 ? bestLength : -bestLength;
            }

            int depth = settings.ChainDepth;
            for (int j = chain[pos]; j >= 0 && depth-- > 0 && bestLength < limit; j = chain[j])
            {
                int distance = pos - j;
                if (distance > settings.Window)
                {
                    break;
                }

                // Only a candidate that could beat the best so far is compared in full.
                if (pixels[j + bestLength] != pixels[pos + bestLength])
                {
                    continue;
                }

                int length = pixels.Slice(j, limit).CommonPrefixLength(pixels.Slice(pos, limit));
                if (length > bestLength + (distance > width * 8 && bestLength >= MinLength ? farPenalty : 0))
                {
                    (bestLength, bestDistance) = (length, distance);
                }
            }

            if (bestLength < MinLength)
            {
                lengths[pos] = 0;
                distances[pos] = 0;
                near[pos] = 0;
                pos--;
                continue;
            }

            // Extend to the left while the pixels before keep matching at this distance: each step left is one pixel longer (up to
            // the longest copy), and the nearby match grows with it while it also keeps matching.
            int nearDistance = nearLength > 0 ? 1 : width;
            int nearRun = Math.Abs(nearLength);
            int steps = MatchesBefore(pixels, pos, bestDistance, Math.Min(pos - start, pos - bestDistance));
            int nearSteps = nearRun > 0 ? MatchesBefore(pixels, pos, nearDistance, Math.Min(steps, Math.Min(pos - nearDistance, MaxLength - nearRun))) : 0;
            distances.Slice(pos - steps, steps + 1).Fill(bestDistance);
            for (int i = 0; i <= steps; i++)
            {
                lengths[pos - i] = Math.Min(bestLength + i, MaxLength);
                int run = i <= nearSteps ? nearRun + i : 0;
                near[pos - i] = run >= MinLength ? (nearDistance == 1 ? run : -run) : 0;
            }

            pos -= steps + 1;
        }
    }

    /// <summary>
    /// Returns how many of the pixels before <paramref name="pos"/>, at most <paramref name="max"/>, match the pixels
    /// <paramref name="distance"/> before them without a break, comparing backwards a vector at a time.
    /// </summary>
    private static int MatchesBefore(ReadOnlySpan<uint> pixels, int pos, int distance, int max)
    {
        ref uint p0 = ref MemoryMarshal.GetReference(pixels);
        int k = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            while (k + 4 <= max)
            {
                int at = pos - k - 4;
                if (Vector128.LoadUnsafe(ref p0, (nuint)at) != Vector128.LoadUnsafe(ref p0, (nuint)(at - distance)))
                {
                    break;
                }

                k += 4;
            }
        }

        while (k < max && Unsafe.Add(ref p0, pos - k - 1) == Unsafe.Add(ref p0, pos - k - 1 - distance))
        {
            k++;
        }

        return k;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Hash(ReadOnlySpan<uint> pixels, int i)
    {
        ulong key = ((ulong)pixels[i + 1] << 32) | pixels[i];
        return (int)((key * 0x9E3779B97F4A7C15UL) >> (64 - HashBits));
    }

    /// <summary>Returns the estimated bits of a token stream with the color cache applied.</summary>
    private static double Estimate(ReadOnlySpan<ulong> tokens, ReadOnlySpan<uint> pixels, int cacheBits)
    {
        ulong[] copyArray = ArrayPool<ulong>.Shared.Rent(tokens.Length);
        try
        {
            Span<ulong> copy = copyArray.AsSpan(0, tokens.Length);
            tokens.CopyTo(copy);
            if (cacheBits > 0)
            {
                Vp8lCache.Apply(copy, pixels, cacheBits);
            }

            Vp8lHistogram histogram = new(cacheBits);
            foreach (ulong token in copy)
            {
                histogram.Add(token);
            }

            return histogram.Bits();
        }
        finally
        {
            ArrayPool<ulong>.Shared.Return(copyArray);
        }
    }

    /// <summary>Returns the estimated bits of a pixel coded as a literal, from the spread of each channel over the whole image.</summary>
    private static float LiteralBits(ReadOnlySpan<uint> pixels)
    {
        // Counted a run at a time, into two sets of counts in turn: a channel that keeps one value would otherwise make each
        // count wait on the one before.
        uint[] counts = new uint[2 * 4 * 256];
        int bank = 0;
        for (int i = 0; i < pixels.Length;)
        {
            uint p = pixels[i];
            int start = i;
            while (++i < pixels.Length && pixels[i] == p)
            {
            }

            uint run = (uint)(i - start);
            counts[bank + (int)(p >> 24)] += run;
            counts[bank + 256 + (int)((p >> 16) & 0xFF)] += run;
            counts[bank + 512 + (int)((p >> 8) & 0xFF)] += run;
            counts[bank + 768 + (int)(p & 0xFF)] += run;
            bank ^= 1024;
        }

        for (int k = 0; k < 1024; k++)
        {
            counts[k] += counts[1024 + k];
        }

        double bits = 0;
        for (int c = 0; c < 4; c++)
        {
            bits += Vp8lCost.Bits(counts.AsSpan(c * 256, 256));
        }

        return (float)(bits / pixels.Length);
    }

    /// <summary>Returns the rough bits of a copy: the length and distance prefixes at a few bits each, plus their extra bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float CopyBits(int length, int distanceCode)
    {
        Vp8lEncoder.PrefixOf(length, out _, out int lengthExtra);
        Vp8lEncoder.PrefixOf(distanceCode, out _, out int distanceExtra);
        return 3 + lengthExtra + (distanceCode <= 120 ? 3 : 6) + distanceExtra;
    }

    /// <summary>
    /// Greedy parse: the match found at each position when it is estimated cheaper than its pixels as literals, unless the next
    /// position matches longer (lazy matching).
    /// </summary>
    private static int GreedyParse(ReadOnlySpan<uint> pixels, int width, bool lazy, float literalBits, ReadOnlySpan<int> lengths, ReadOnlySpan<int> distances, Span<ulong> tokens)
    {
        int n = 0;
        for (int i = 0; i < pixels.Length;)
        {
            int length = lengths[i];
            if (length >= MinLength && length * literalBits > CopyBits(length, Vp8lEncoder.DistanceToCode(distances[i], width))
                && !(lazy && i + 1 < pixels.Length && lengths[i + 1] > length + 1))
            {
                tokens[n++] = Vp8lEncoder.Token.Copy(length, Vp8lEncoder.DistanceToCode(distances[i], width));
                i += length;
                continue;
            }

            tokens[n++] = Vp8lEncoder.Token.Pixel(pixels[i]);
            i++;
        }

        return n;
    }

    /// <summary>
    /// Shortest-path parse: each position's cheapest way to be reached, from a literal (or cache hit) at the position before or from
    /// a match that ends there, under bit costs from the statistics of an earlier parse (<paramref name="model0"/>). A match is tried at the longest and shortest
    /// length of each length prefix it covers, since lengths that share a prefix cost the same but for their extra bits.
    /// </summary>
    private static int CostParse(ReadOnlySpan<uint> pixels, int width, ReadOnlySpan<int> lengths, ReadOnlySpan<int> distances, ReadOnlySpan<int> near, Span<ulong> tokens, ReadOnlySpan<ulong> model0, int cacheBits)
    {
        int count = pixels.Length;
        Vp8lHistogram stats = new(cacheBits);
        ulong[] statTokens = ArrayPool<ulong>.Shared.Rent(model0.Length);
        float[] costArray = ArrayPool<float>.Shared.Rent(count + 1);
        int[] fromArray = ArrayPool<int>.Shared.Rent(count + 1);
        try
        {
            Span<ulong> greedyTokens = statTokens.AsSpan(0, model0.Length);
            model0.CopyTo(greedyTokens);
            if (cacheBits > 0)
            {
                Vp8lCache.Apply(greedyTokens, pixels, cacheBits);
            }

            foreach (ulong token in greedyTokens)
            {
                stats.Add(token);
            }

            CostModel model = new(stats);
            uint[]? cache = cacheBits > 0 ? new uint[1 << cacheBits] : null;
            Span<float> cost = costArray.AsSpan(0, count + 1);
            Span<int> from = fromArray.AsSpan(0, count + 1);
            int[] viaArray = ArrayPool<int>.Shared.Rent(count + 1);
            Span<int> via = viaArray.AsSpan(0, count + 1);
            cost.Fill(float.MaxValue);
            cost[0] = 0;
            for (int i = 0; i < count; i++)
            {
                float here = cost[i];
                uint p = pixels[i];
                float literal = model.Literal(p);
                if (cache is not null)
                {
                    int slot = Vp8lEncoder.CacheKey(p, cacheBits);
                    if (cache[slot] == p)
                    {
                        literal = model.Cached(slot);
                    }

                    cache[slot] = p;
                }

                if (here + literal < cost[i + 1])
                {
                    cost[i + 1] = here + literal;
                    from[i + 1] = -1;
                }

                if (lengths[i] >= MinLength)
                {
                    Relax(cost, from, via, model, i, lengths[i], distances[i], width, here);
                }

                int nearLength = Math.Abs(near[i]);
                int nearDistance = near[i] > 0 ? 1 : width;
                if (nearLength >= MinLength && nearDistance != distances[i])
                {
                    Relax(cost, from, via, model, i, nearLength, nearDistance, width, here);
                }
            }

            // Walk back from the end, then write the tokens in order.
            int n = 0;
            for (int i = count; i > 0;)
            {
                n++;
                i -= from[i] < 0 ? 1 : from[i];
            }

            int at = n;
            for (int i = count; i > 0;)
            {
                if (from[i] < 0)
                {
                    tokens[--at] = Vp8lEncoder.Token.Pixel(pixels[i - 1]);
                    i--;
                }
                else
                {
                    int length = from[i];
                    tokens[--at] = Vp8lEncoder.Token.Copy(length, Vp8lEncoder.DistanceToCode(via[i], width));
                    i -= length;
                }
            }

            ArrayPool<int>.Shared.Return(viaArray);
            return n;
        }
        finally
        {
            ArrayPool<ulong>.Shared.Return(statTokens);
            ArrayPool<float>.Shared.Return(costArray);
            ArrayPool<int>.Shared.Return(fromArray);
        }
    }

    /// <summary>Returns the longest value a length prefix codes: 1 to 4 for prefixes 0 to 3, then up to (2 + (p &amp; 1)) &lt;&lt; extra bits.</summary>
    private static int PrefixEnd(int prefix)
    {
        if (prefix < 4)
        {
            return prefix + 1;
        }

        int extraBits = (prefix - 2) >> 1;
        return ((2 + (prefix & 1)) << extraBits) + (1 << extraBits);
    }

    /// <summary>Offers a copy from position <paramref name="i"/> at each length prefix it covers, shortest and longest of each.</summary>
    private static void Relax(Span<float> cost, Span<int> from, Span<int> via, CostModel model, int i, int length, int distance, int width, float here)
    {
        float distanceCost = here + model.Distance(Vp8lEncoder.DistanceToCode(distance, width));
        for (int lo = MinLength; lo <= length;)
        {
            int prefix = Vp8lEncoder.PrefixOf(lo, out _, out int extraBits);
            int hi = Math.Min(length, PrefixEnd(prefix));
            float copy = distanceCost + model.LengthPrefix(prefix) + extraBits;
            TryCopy(cost, from, via, i, hi, distance, copy);
            if (lo != hi)
            {
                TryCopy(cost, from, via, i, lo, distance, copy);
            }

            lo = hi + 1;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void TryCopy(Span<float> cost, Span<int> from, Span<int> via, int i, int length, int distance, float total)
    {
        if (total < cost[i + length])
        {
            cost[i + length] = total;
            from[i + length] = length;
            via[i + length] = distance;
        }
    }

    /// <summary>Bits per symbol from a histogram, for the shortest-path parse.</summary>
    private sealed class CostModel
    {
        private readonly float[] _green;
        private readonly float[] _red = new float[256];
        private readonly float[] _blue = new float[256];
        private readonly float[] _alpha = new float[256];
        private readonly float[] _distance = new float[Vp8lEncoder.DistanceCodes];

        internal CostModel(Vp8lHistogram stats)
        {
            _green = new float[stats.Literal.Length];
            Fill(stats.Literal, _green);
            Fill(stats.Red, _red);
            Fill(stats.Blue, _blue);
            Fill(stats.Alpha, _alpha);
            Fill(stats.Distance, _distance);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal float Literal(uint argb) => _green[(argb >> 8) & 0xFF] + _red[(argb >> 16) & 0xFF] + _blue[argb & 0xFF] + _alpha[argb >> 24];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal float LengthPrefix(int prefix) => _green[256 + prefix];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal float Cached(int slot) => _green[256 + Vp8lEncoder.LengthCodes + slot];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal float Distance(int code)
        {
            int prefix = Vp8lEncoder.PrefixOf(code, out _, out int extraBits);
            return _distance[prefix] + extraBits;
        }

        private static void Fill(ReadOnlySpan<uint> counts, Span<float> bits)
        {
            uint total = 0;
            foreach (uint c in counts)
            {
                total += c;
            }

            // Unseen symbols cost a little more than the rarest seen one.
            float logTotal = Vp8lCost.Log2(total + 1);
            for (int i = 0; i < counts.Length; i++)
            {
                bits[i] = counts[i] == 0 ? logTotal + 2 : logTotal - Vp8lCost.Log2(counts[i]);
            }
        }
    }
}
