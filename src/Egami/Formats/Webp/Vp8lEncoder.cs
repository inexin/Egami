using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Egami.Formats.Webp;

/// <summary>
/// Writes a VP8L (lossless WebP) bit stream (RFC 9649 section 3): the header; the subtract-green transform, then the predictor
/// transform with each 16 × 16 block's mode chosen by the smallest residuals; then the residuals as an entropy-coded image of
/// literals, color cache hits and backward references under one group of prefix codes. The color cache size is the one of 0,
/// 6 and 10 bits that gives the smallest stream.
/// </summary>
/// <remarks>
/// Ported from ClassicMac's lossless WebP encoder (Lars Dytterud, MIT, ClassicMac commit 1156fb4), reshaped to Egami's style:
/// pooled buffers, packed tokens, no LINQ or delegates.
/// </remarks>
internal static class Vp8lEncoder
{
    /// <summary>The VP8L signature byte.</summary>
    internal const byte Signature = 0x2F;

    /// <summary>The largest width or height VP8L can hold.</summary>
    internal const int MaxSide = 16384;

    private const int PredictorBits = 4;
    private const int MaxCopy = 4096;
    private const int MaxDistance = (1 << 20) - 120;
    private const int MinCopy = 3;
    private const int ChainDepth = 48;
    private const int HashBits = 16;

    /// <summary>The number of length prefix codes in the green alphabet.</summary>
    internal const int LengthCodes = 24;

    /// <summary>The number of distance prefix codes.</summary>
    internal const int DistanceCodes = 40;

    /// <summary>Writes the VP8L data for <paramref name="argb"/> (rows top-down, each pixel A &lt;&lt; 24 | R &lt;&lt; 16 | G &lt;&lt; 8 | B).</summary>
    internal static void Encode(ReadOnlySpan<uint> argb, int width, int height, Vp8lBitWriter output)
    {
        int count = width * height;
        uint[] pixels = ArrayPool<uint>.Shared.Rent(count);
        uint[] residuals = ArrayPool<uint>.Shared.Rent(count);
        int blocksX = Blocks(width);
        int blocksY = Blocks(height);
        uint[] modes = ArrayPool<uint>.Shared.Rent(blocksX * blocksY);
        try
        {
            // Subtract green (section 4.2): red and blue less green, on which the predictor works.
            bool alpha = false;
            for (int i = 0; i < count; i++)
            {
                uint p = argb[i];
                uint g = (p >> 8) & 0xFF;
                uint r = ((p >> 16) - g) & 0xFF;
                uint b = (p - g) & 0xFF;
                pixels[i] = (p & 0xFF00FF00) | (r << 16) | b;
                alpha |= p >> 24 != 0xFF;
            }

            Predict(width, height, pixels.AsSpan(0, count), modes, residuals.AsSpan(0, count));
            using Vp8lBitWriter trial = new(count);
            long best = long.MaxValue;
            foreach (int cacheBits in (ReadOnlySpan<int>)[0, 6, 10])
            {
                trial.Reset();
                trial.Write(Signature, 8);
                trial.Write((uint)(width - 1), 14);
                trial.Write((uint)(height - 1), 14);
                trial.Write(alpha ? 1u : 0u, 1);
                trial.Write(0, 3);

                // Subtract green, then the predictor with its block size and modes.
                trial.Write(1, 1);
                trial.Write(2, 2);
                trial.Write(1, 1);
                trial.Write(0, 2);
                trial.Write(PredictorBits - 2, 3);
                WriteImage(trial, modes.AsSpan(0, blocksX * blocksY), blocksX, cacheBits: 0, main: false);
                trial.Write(0, 1);
                WriteImage(trial, residuals.AsSpan(0, count), width, cacheBits, main: true);
                if (trial.BitCount < best)
                {
                    best = trial.BitCount;
                    output.Reset();
                    output.Write(trial.Finish());
                }
            }
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(pixels);
            ArrayPool<uint>.Shared.Return(residuals);
            ArrayPool<uint>.Shared.Return(modes);
        }
    }

    private static int Blocks(int size) => (size + (1 << PredictorBits) - 1) >> PredictorBits;

    /// <summary>
    /// The predictor transform (section 4.1): the mode of each block that gives the smallest residuals (each channel's distance
    /// from 0, wrapping), and the residual of every pixel against its prediction from its neighbours.
    /// </summary>
    private static void Predict(int width, int height, ReadOnlySpan<uint> pixels, Span<uint> modes, Span<uint> residuals)
    {
        const int size = 1 << PredictorBits;
        int blocksX = Blocks(width);
        int blocksY = Blocks(height);
        for (int by = 0; by < blocksY; by++)
        {
            int y1 = Math.Min(height, (by + 1) * size);
            for (int bx = 0; bx < blocksX; bx++)
            {
                int x1 = Math.Min(width, (bx + 1) * size);
                int bestMode = 0;
                long bestCost = long.MaxValue;
                for (int mode = 0; mode < 14; mode++)
                {
                    long cost = 0;
                    for (int y = by * size; y < y1 && cost < bestCost; y++)
                    {
                        for (int x = bx * size; x < x1; x++)
                        {
                            cost += Cost(Subtract(pixels[(y * width) + x], Prediction(pixels, width, x, y, mode)));
                        }
                    }

                    if (cost < bestCost)
                    {
                        (bestCost, bestMode) = (cost, mode);
                    }
                }

                modes[(by * blocksX) + bx] = 0xFF000000u | ((uint)bestMode << 8);
                for (int y = by * size; y < y1; y++)
                {
                    for (int x = bx * size; x < x1; x++)
                    {
                        residuals[(y * width) + x] = Subtract(pixels[(y * width) + x], Prediction(pixels, width, x, y, bestMode));
                    }
                }
            }
        }
    }

    private static long Cost(uint residual)
    {
        long cost = 0;
        for (int shift = 0; shift < 32; shift += 8)
        {
            int c = (int)((residual >> shift) & 0xFF);
            cost += Math.Min(c, 256 - c);
        }

        return cost;
    }

    /// <summary>
    /// The prediction for (x, y) (section 4.1): the top-left pixel predicts opaque black, the rest of the top row its left
    /// neighbour, the left column its top neighbour; elsewhere the mode's, the top-right of the last column being the first pixel
    /// of the row.
    /// </summary>
    internal static uint Prediction(ReadOnlySpan<uint> p, int width, int x, int y, int mode)
    {
        if (x == 0 && y == 0)
        {
            return 0xFF000000;
        }

        if (y == 0)
        {
            return p[x - 1];
        }

        if (x == 0)
        {
            return p[(y - 1) * width];
        }

        int row = y * width;
        uint l = p[row + x - 1];
        uint t = p[row - width + x];
        uint tl = p[row - width + x - 1];
        uint tr = p[row - width + x + 1];
        return Vp8lPredictors.Predict(mode, l, t, tl, tr);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint Subtract(uint a, uint b)
    {
        uint ag = (a | 0x00FF00FF) - (b & 0xFF00FF00);
        uint rb = (a | 0xFF00FF00) - (b & 0x00FF00FF);
        return (ag & 0xFF00FF00) | (rb & 0x00FF00FF);
    }

    /// <summary>
    /// Writes an entropy-coded image (section 5): its color cache flag and size, for the main image the meta prefix code flag
    /// (one group), the five prefix codes (green with the length and cache codes, red, blue, alpha, distance), then its tokens.
    /// </summary>
    private static void WriteImage(Vp8lBitWriter w, ReadOnlySpan<uint> pixels, int width, int cacheBits, bool main)
    {
        ulong[] tokenArray = ArrayPool<ulong>.Shared.Rent(pixels.Length);
        try
        {
            Span<ulong> tokens = tokenArray.AsSpan(0, Tokens(pixels, width, cacheBits, tokenArray));
            int cacheSize = cacheBits > 0 ? 1 << cacheBits : 0;
            int greenSize = 256 + LengthCodes + cacheSize;
            uint[] histogramArray = ArrayPool<uint>.Shared.Rent(greenSize + (256 * 3) + DistanceCodes);
            byte[] lengthArray = ArrayPool<byte>.Shared.Rent(greenSize + (256 * 3) + DistanceCodes);
            ushort[] codeArray = ArrayPool<ushort>.Shared.Rent(greenSize + (256 * 3) + DistanceCodes);
            try
            {
                Span<uint> histograms = histogramArray.AsSpan(0, greenSize + (256 * 3) + DistanceCodes);
                histograms.Clear();
                Span<uint> green = histograms[..greenSize];
                Span<uint> red = histograms.Slice(greenSize, 256);
                Span<uint> blue = histograms.Slice(greenSize + 256, 256);
                Span<uint> alpha = histograms.Slice(greenSize + 512, 256);
                Span<uint> distance = histograms.Slice(greenSize + 768, DistanceCodes);
                foreach (ulong t in tokens)
                {
                    Token.Read(t, out int kind, out uint value, out int length);
                    switch (kind)
                    {
                        case Token.Literal:
                            green[(int)((value >> 8) & 0xFF)]++;
                            red[(int)((value >> 16) & 0xFF)]++;
                            blue[(int)(value & 0xFF)]++;
                            alpha[(int)(value >> 24)]++;
                            break;
                        case Token.Cached:
                            green[256 + LengthCodes + (int)value]++;
                            break;
                        default:
                            green[256 + PrefixOf(length, out _, out _)]++;
                            distance[PrefixOf((int)value, out _, out _)]++;
                            break;
                    }
                }

                w.Write(cacheBits > 0 ? 1u : 0u, 1);
                if (cacheBits > 0)
                {
                    w.Write((uint)cacheBits, 4);
                }

                if (main)
                {
                    w.Write(0, 1);
                }

                Span<byte> lengths = lengthArray.AsSpan(0, histograms.Length);
                Span<ushort> codes = codeArray.AsSpan(0, histograms.Length);
                int start = 0;
                foreach (int size in (ReadOnlySpan<int>)[greenSize, 256, 256, 256, DistanceCodes])
                {
                    Vp8lPrefixCode.BuildLengths(histograms.Slice(start, size), lengths.Slice(start, size), Vp8lPrefixCode.MaxLength);
                    Vp8lPrefixCode.BuildCodes(lengths.Slice(start, size), codes.Slice(start, size));
                    Vp8lPrefixCode.Write(w, lengths.Slice(start, size));
                    if (Vp8lPrefixCode.IsSingle(lengths.Slice(start, size)))
                    {
                        // A code of one symbol takes no bits per symbol.
                        lengths.Slice(start, size).Clear();
                    }

                    start += size;
                }

                int redAt = greenSize;
                int blueAt = greenSize + 256;
                int alphaAt = greenSize + 512;
                int distanceAt = greenSize + 768;
                foreach (ulong t in tokens)
                {
                    Token.Read(t, out int kind, out uint value, out int length);
                    switch (kind)
                    {
                        case Token.Literal:
                            int g = (int)((value >> 8) & 0xFF);
                            int r = redAt + (int)((value >> 16) & 0xFF);
                            int b = blueAt + (int)(value & 0xFF);
                            int a = alphaAt + (int)(value >> 24);
                            w.Write(codes[g], lengths[g]);
                            w.Write(codes[r], lengths[r]);
                            w.Write(codes[b], lengths[b]);
                            w.Write(codes[a], lengths[a]);
                            break;
                        case Token.Cached:
                            int c = 256 + LengthCodes + (int)value;
                            w.Write(codes[c], lengths[c]);
                            break;
                        default:
                            int lengthCode = 256 + PrefixOf(length, out int extra, out int extraBits);
                            w.Write(codes[lengthCode], lengths[lengthCode]);
                            w.Write((uint)extra, extraBits);
                            int distanceCode = distanceAt + PrefixOf((int)value, out extra, out extraBits);
                            w.Write(codes[distanceCode], lengths[distanceCode]);
                            w.Write((uint)extra, extraBits);
                            break;
                    }
                }
            }
            finally
            {
                ArrayPool<uint>.Shared.Return(histogramArray);
                ArrayPool<byte>.Shared.Return(lengthArray);
                ArrayPool<ushort>.Shared.Return(codeArray);
            }
        }
        finally
        {
            ArrayPool<ulong>.Shared.Return(tokenArray);
        }
    }

    /// <summary>
    /// Returns a length or distance code's prefix and extra bits (section 5.2.2): values 1 to 4 have prefixes 0 to 3; above that,
    /// for v = value − 1, the prefix comes from v's two highest bits and the bits below are the extra bits.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int PrefixOf(int value, out int extra, out int extraBits)
    {
        int v = value - 1;
        if (v < 2)
        {
            extra = 0;
            extraBits = 0;
            return v;
        }

        int highest = 31 - BitOperations.LeadingZeroCount((uint)v);
        int second = (v >> (highest - 1)) & 1;
        extraBits = highest - 1;
        extra = v & ((1 << extraBits) - 1);
        return (2 * highest) + second;
    }

    /// <summary>Returns the color cache slot of a pixel (section 5.2.3).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int CacheKey(uint argb, int cacheBits) => (int)((argb * 0x1E35A7BDu) >> (32 - cacheBits));

    /// <summary>
    /// Writes the pixels as tokens: a backward reference to the longest earlier match (at least 3 pixels, found through chains of
    /// the positions of each pixel pair), else a color cache hit, else a literal. Every pixel goes into the cache as the decoder
    /// puts it there. A distance of one row, or of one pixel, takes its plane code.
    /// </summary>
    private static int Tokens(ReadOnlySpan<uint> pixels, int width, int cacheBits, Span<ulong> tokens)
    {
        int count = pixels.Length;
        int[] head = ArrayPool<int>.Shared.Rent(1 << HashBits);
        int[] chain = ArrayPool<int>.Shared.Rent(count);
        uint[]? cache = cacheBits > 0 ? ArrayPool<uint>.Shared.Rent(1 << cacheBits) : null;
        try
        {
            head.AsSpan(0, 1 << HashBits).Fill(-1);
            Span<uint> cacheSpan = cache is null ? default : cache.AsSpan(0, 1 << cacheBits);

            // Slots start empty; a pixel can only hit a slot it was stored in, so a sentinel no hash maps there is not needed:
            // a zero slot matches a zero pixel only if a zero pixel was stored, which the valid flags below track.
            Span<bool> valid = cacheBits > 0 ? new bool[1 << cacheBits] : default;
            int n = 0;
            for (int i = 0; i < count;)
            {
                int bestLength = 0;
                int bestFrom = 0;
                if (i + MinCopy <= count)
                {
                    int depth = 0;
                    int limit = Math.Min(MaxCopy, count - i);
                    for (int j = head[Hash(pixels, i)]; j >= 0 && depth < ChainDepth && i - j <= MaxDistance; j = chain[j], depth++)
                    {
                        int length = pixels.Slice(j, limit).CommonPrefixLength(pixels.Slice(i, limit));
                        if (length > bestLength)
                        {
                            (bestLength, bestFrom) = (length, j);
                            if (length == limit)
                            {
                                break;
                            }
                        }
                    }
                }

                if (bestLength >= MinCopy)
                {
                    int d = i - bestFrom;
                    int code = d == width ? 1 : d == 1 ? 2 : d + 120;
                    tokens[n++] = Token.Copy(bestLength, code);
                    for (int k = 0; k < bestLength; k++)
                    {
                        Insert(pixels, head, chain, i + k);
                        if (cache is not null)
                        {
                            int key = CacheKey(pixels[i + k], cacheBits);
                            cacheSpan[key] = pixels[i + k];
                            valid[key] = true;
                        }
                    }

                    i += bestLength;
                    continue;
                }

                uint p = pixels[i];
                if (cache is not null)
                {
                    int key = CacheKey(p, cacheBits);
                    tokens[n++] = valid[key] && cacheSpan[key] == p ? Token.CacheHit(key) : Token.Pixel(p);
                    cacheSpan[key] = p;
                    valid[key] = true;
                }
                else
                {
                    tokens[n++] = Token.Pixel(p);
                }

                Insert(pixels, head, chain, i);
                i++;
            }

            return n;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(head);
            ArrayPool<int>.Shared.Return(chain);
            if (cache is not null)
            {
                ArrayPool<uint>.Shared.Return(cache);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Hash(ReadOnlySpan<uint> pixels, int i) => (int)(((pixels[i] * 0x1E35A7BDu) ^ (pixels[i + 1] * 0x9E3779B1u)) >> (32 - HashBits));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Insert(ReadOnlySpan<uint> pixels, int[] head, int[] chain, int i)
    {
        if (i + 1 < pixels.Length)
        {
            int h = Hash(pixels, i);
            chain[i] = head[h];
            head[h] = i;
        }
    }

    /// <summary>A token packed into 64 bits: the kind in bits 62–63, a copy's length in bits 32–43, and the pixel, cache slot or distance code below.</summary>
    internal static class Token
    {
        internal const int Literal = 0;
        internal const int Cached = 1;
        internal const int Backward = 2;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ulong Pixel(uint argb) => argb;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ulong CacheHit(int key) => (1UL << 62) | (uint)key;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ulong Copy(int length, int distanceCode) => (2UL << 62) | ((ulong)length << 32) | (uint)distanceCode;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void Read(ulong token, out int kind, out uint value, out int length)
        {
            kind = (int)(token >> 62);
            value = (uint)token;
            length = (int)((token >> 32) & 0x3FFF_FFFF);
        }
    }
}

/// <summary>The 14 VP8L predictors (RFC 9649 section 4.1), shared by the reader and the writer.</summary>
internal static class Vp8lPredictors
{
    /// <summary>Returns predictor <paramref name="mode"/>'s value from the left, top, top-left and top-right pixels.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint Predict(int mode, uint l, uint t, uint tl, uint tr)
    {
        return mode switch
        {
            0 => 0xFF000000,
            1 => l,
            2 => t,
            3 => tr,
            4 => tl,
            5 => Average(Average(l, tr), t),
            6 => Average(l, tl),
            7 => Average(l, t),
            8 => Average(tl, t),
            9 => Average(t, tr),
            10 => Average(Average(l, tl), Average(t, tr)),
            11 => Select(l, t, tl),
            12 => ClampAddSubtractFull(l, t, tl),
            13 => ClampAddSubtractHalf(Average(l, t), tl),
            _ => 0xFF000000,
        };
    }

    /// <summary>Returns the per-channel average of two pixels, rounded down.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint Average(uint a, uint b) => (((a ^ b) & 0xFEFEFEFEu) >> 1) + (a & b);

    /// <summary>Returns the left or the top pixel, whichever is nearer (in summed channel distance) to left + top − top-left.</summary>
    internal static uint Select(uint l, uint t, uint tl)
    {
        int pl = 0;
        int pt = 0;
        for (int shift = 0; shift < 32; shift += 8)
        {
            int cl = (int)((l >> shift) & 0xFF);
            int ct = (int)((t >> shift) & 0xFF);
            int ctl = (int)((tl >> shift) & 0xFF);
            pl += Math.Abs(ct - ctl);
            pt += Math.Abs(cl - ctl);
        }

        return pl < pt ? l : t;
    }

    /// <summary>Returns a + b − c per channel, clamped to 0–255.</summary>
    internal static uint ClampAddSubtractFull(uint a, uint b, uint c)
    {
        uint result = 0;
        for (int shift = 0; shift < 32; shift += 8)
        {
            int value = (int)((a >> shift) & 0xFF) + (int)((b >> shift) & 0xFF) - (int)((c >> shift) & 0xFF);
            result |= (uint)Math.Clamp(value, 0, 255) << shift;
        }

        return result;
    }

    /// <summary>Returns a + (a − b) / 2 per channel, clamped to 0–255.</summary>
    internal static uint ClampAddSubtractHalf(uint a, uint b)
    {
        uint result = 0;
        for (int shift = 0; shift < 32; shift += 8)
        {
            int ca = (int)((a >> shift) & 0xFF);
            int value = ca + ((ca - (int)((b >> shift) & 0xFF)) / 2);
            result |= (uint)Math.Clamp(value, 0, 255) << shift;
        }

        return result;
    }
}
