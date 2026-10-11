using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Egami.Formats.Webp;

/// <summary>How the lossless encoder works: effort 0 to 6 on libwebp's method scale, exact transparent colors, near-lossless level.</summary>
internal readonly record struct Vp8lEncoderOptions(int Effort = 4, bool KeepTransparentColors = true, int NearLossless = 100);

/// <summary>
/// Writes a VP8L (lossless WebP) bit stream (RFC 9649 section 3). Effort chooses the tools: images of 256 colors or fewer are
/// written as palette indices (bundled when 16 or fewer); others through subtract green, the predictor with each tile's mode
/// chosen by its cost, and the color transform. The coded pixels are backward references found through hash chains, a color
/// cache sized from symbol statistics, and, from effort 3, meta prefix codes that give similar tiles their own codes. At efforts 5
/// and 6 the encoder tries more than one plan and keeps the smallest.
/// </summary>
/// <remarks>
/// Grown from ClassicMac's lossless WebP encoder (Lars Dytterud, MIT, ClassicMac commit 1156fb4): its prefix-code building, bit
/// writer and predictor helpers remain.
/// </remarks>
internal static class Vp8lEncoder
{
    /// <summary>The VP8L signature byte.</summary>
    internal const byte Signature = 0x2F;

    /// <summary>The largest width or height VP8L can hold.</summary>
    internal const int MaxSide = 16384;

    /// <summary>The number of length prefix codes in the green alphabet.</summary>
    internal const int LengthCodes = 24;

    /// <summary>The number of distance prefix codes.</summary>
    internal const int DistanceCodes = 40;

    /// <summary>The pixel count up to which every plan is tried, whatever the effort.</summary>
    private const int SmallImage = 128 * 128;

    /// <summary>The most tiles the entropy image may have, as in libwebp.</summary>
    private const int MaxHistogramTiles = 2600;

    private static readonly int[] AllModes = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13];
    private static readonly int[] FewModes = [1, 2, 11, 12, 13, 7, 5];

    /// <summary>Writes the VP8L data for <paramref name="argb"/> (rows top-down, each pixel A &lt;&lt; 24 | R &lt;&lt; 16 | G &lt;&lt; 8 | B).</summary>
    internal static void Encode(ReadOnlySpan<uint> argb, int width, int height, Vp8lBitWriter output)
    {
        Encode(argb, width, height, new Vp8lEncoderOptions(), output);
    }

    /// <summary>Writes the VP8L data for <paramref name="argb"/> with the given options.</summary>
    internal static void Encode(ReadOnlySpan<uint> argb, int width, int height, Vp8lEncoderOptions options, Vp8lBitWriter output)
    {
        int count = width * height;
        uint[] source = ArrayPool<uint>.Shared.Rent(count);
        uint[] palette = new uint[256];
        try
        {
            Span<uint> pixels = source.AsSpan(0, count);
            argb.CopyTo(pixels);
            bool alpha = false;
            foreach (uint p in pixels)
            {
                if (p < 0xFF000000)
                {
                    alpha = true;
                    break;
                }
            }

            if (!options.KeepTransparentColors && alpha)
            {
                // Fully transparent pixels may take any color; zero joins them into runs and matches.
                foreach (ref uint p in pixels)
                {
                    if (p < 0x01000000)
                    {
                        p = 0;
                    }
                }
            }

            int colors = Vp8lTransforms.FindPalette(pixels, palette);
            int effort = options.Effort;
            using Vp8lBitWriter trial = new(count);
            long best = long.MaxValue;
            foreach (Plan plan in Plans(colors, effort, count <= SmallImage, Vp8lTransforms.RankSpatialModes(pixels, width)))
            {
                trial.Reset();
                WriteHeader(trial, width, height, alpha);
                if (plan.Palette)
                {
                    EncodePalette(trial, pixels, width, height, palette.AsSpan(0, colors), plan.NearestOrder, effort);
                }
                else
                {
                    EncodeSpatial(trial, pixels, width, height, plan, effort, options.NearLossless);
                }

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
            ArrayPool<uint>.Shared.Return(source);
        }
    }

    /// <summary>
    /// One way to encode the image: palette indices (in sorted or nearest-neighbour order), or some of subtract green, the predictor
    /// and the color transform.
    /// </summary>
    private readonly record struct Plan(bool Palette, bool NearestOrder, bool SubtractGreen, bool Predictor, bool CrossColor, int BitsAdjust);

    /// <summary>
    /// Returns the plans an effort tries: the palette when the image has 256 colors or fewer, else the spatial mode the estimate
    /// ranks first; at effort 6 also the runner-up and the other palette order; for a small image, all of them. The smallest
    /// file wins.
    /// </summary>
    private static IEnumerable<Plan> Plans(int colors, int effort, bool small, (Vp8lTransforms.SpatialMode First, Vp8lTransforms.SpatialMode Second) ranked)
    {
        if (small)
        {
            // Trying everything costs little on a small image, and estimates are least reliable there.
            if (colors > 0)
            {
                yield return new Plan(true, false, false, false, false, 0);
                yield return new Plan(true, true, false, false, false, 0);
            }

            for (int mode = 0; mode < 4; mode++)
            {
                Plan plan = Spatial((Vp8lTransforms.SpatialMode)mode, effort, 0);
                yield return plan;
                if (plan.CrossColor && effort >= 3)
                {
                    yield return plan with { CrossColor = false };
                }
            }

            yield break;
        }

        if (colors > 0)
        {
            yield return new Plan(true, false, false, false, false, 0);
            if (effort >= 6)
            {
                yield return new Plan(true, true, false, false, false, 0);
                if (colors > 16)
                {
                    yield return Spatial(ranked.First, effort, 0);
                }
            }

            yield break;
        }

        yield return Spatial(ranked.First, effort, 0);
        if (effort >= 6)
        {
            yield return Spatial(ranked.Second, effort, 0);
        }
    }

    private static Plan Spatial(Vp8lTransforms.SpatialMode mode, int effort, int bitsAdjust)
    {
        bool predictor = mode is Vp8lTransforms.SpatialMode.Spatial or Vp8lTransforms.SpatialMode.SpatialSubtractGreen;
        bool green = mode is Vp8lTransforms.SpatialMode.SubtractGreen or Vp8lTransforms.SpatialMode.SpatialSubtractGreen;
        return new Plan(false, false, green, predictor, predictor, bitsAdjust);
    }

    private static void WriteHeader(Vp8lBitWriter w, int width, int height, bool alpha)
    {
        w.Write(Signature, 8);
        w.Write((uint)(width - 1), 14);
        w.Write((uint)(height - 1), 14);
        w.Write(alpha ? 1u : 0u, 1);
        w.Write(0, 3);
    }

    /// <summary>Writes the color indexing transform (the palette, delta coded) and then the index image.</summary>
    private static void EncodePalette(Vp8lBitWriter w, ReadOnlySpan<uint> pixels, int width, int height, ReadOnlySpan<uint> sorted, bool nearest, int effort)
    {
        Span<uint> palette = stackalloc uint[sorted.Length];
        sorted.CopyTo(palette);
        if (nearest)
        {
            Vp8lTransforms.OrderByNearest(palette);
        }

        int colors = palette.Length;
        int bundleBits = Vp8lTransforms.BundleBits(colors);
        w.Write(1, 1);
        w.Write(3, 2);
        w.Write((uint)(colors - 1), 8);
        Span<uint> deltas = stackalloc uint[colors];
        deltas[0] = palette[0];
        for (int i = 1; i < colors; i++)
        {
            deltas[i] = Subtract(palette[i], palette[i - 1]);
        }

        WriteImage(w, deltas, colors, 1, effort, main: false);
        int packedWidth = Vp8lDecoder.SubSampleSize(width, bundleBits);
        uint[] indexArray = ArrayPool<uint>.Shared.Rent(packedWidth * height);
        try
        {
            Span<uint> indices = indexArray.AsSpan(0, packedWidth * height);
            Vp8lTransforms.ApplyPalette(pixels, width, height, palette, bundleBits, indices);
            w.Write(0, 1);
            WriteImage(w, indices, packedWidth, height, effort, main: true, usePalette: true);
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(indexArray);
        }
    }

    /// <summary>Writes the plan's subtract green, predictor and color transform, then the coded image.</summary>
    private static void EncodeSpatial(Vp8lBitWriter w, ReadOnlySpan<uint> source, int width, int height, Plan plan, int effort, int nearLossless)
    {
        int count = width * height;
        uint[] workArray = ArrayPool<uint>.Shared.Rent(count);
        uint[] residualArray = ArrayPool<uint>.Shared.Rent(count);
        try
        {
            Span<uint> pixels = workArray.AsSpan(0, count);
            source.CopyTo(pixels);
            if (nearLossless < 100)
            {
                Vp8lTransforms.NearLossless(pixels, width, height, nearLossless);
            }

            if (plan.SubtractGreen)
            {
                Vp8lTransforms.SubtractGreen(pixels);
                w.Write(1, 1);
                w.Write(2, 2);
            }

            if (!plan.Predictor)
            {
                w.Write(0, 1);
                WriteImage(w, pixels, width, height, effort, main: true);
                return;
            }

            int histoBits = HistogramBits(effort, false, width, height);
            int bits = Math.Clamp(TransformBits(effort, histoBits) + plan.BitsAdjust, 2, 9);
            int tiles = Vp8lDecoder.SubSampleSize(width, bits) * Vp8lDecoder.SubSampleSize(height, bits);
            uint[] tileArray = ArrayPool<uint>.Shared.Rent(tiles);
            try
            {
                Span<uint> tileData = tileArray.AsSpan(0, tiles);
                ReadOnlySpan<int> modes = effort == 0 ? FewModes : AllModes;
                Vp8lTransforms.ChooseModes(pixels, width, height, bits, modes, tileData);
                Span<uint> residuals = residualArray.AsSpan(0, count);
                Vp8lTransforms.ApplyPredictor(pixels, width, height, bits, tileData, residuals);
                w.Write(1, 1);
                w.Write(0, 2);
                w.Write((uint)(bits - 2), 3);
                WriteImage(w, tileData, Vp8lDecoder.SubSampleSize(width, bits), Vp8lDecoder.SubSampleSize(height, bits), effort, main: false);

                if (plan.CrossColor)
                {
                    Vp8lTransforms.ChooseCrossColor(residuals, width, height, bits, effort, tileData);
                    Vp8lTransforms.ApplyCrossColor(residuals, width, height, bits, tileData);
                    w.Write(1, 1);
                    w.Write(1, 2);
                    w.Write((uint)(bits - 2), 3);
                    WriteImage(w, tileData, Vp8lDecoder.SubSampleSize(width, bits), Vp8lDecoder.SubSampleSize(height, bits), effort, main: false);
                }

                w.Write(0, 1);
                WriteImage(w, residuals, width, height, effort, main: true);
            }
            finally
            {
                ArrayPool<uint>.Shared.Return(tileArray);
            }
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(workArray);
            ArrayPool<uint>.Shared.Return(residualArray);
        }
    }

    /// <summary>Returns the entropy image's tile bits, as libwebp chooses them: smaller tiles at higher effort, at most 2,600 tiles.</summary>
    internal static int HistogramBits(int effort, bool palette, int width, int height)
    {
        int bits = (palette ? 9 : 7) - effort;
        while (Vp8lDecoder.SubSampleSize(width, bits) * Vp8lDecoder.SubSampleSize(height, bits) > MaxHistogramTiles)
        {
            bits++;
        }

        return Math.Clamp(bits, 2, 9);
    }

    /// <summary>Returns the predictor and color transform tile bits: no finer than the entropy image, and at most 6, 5 or 4 by effort.</summary>
    internal static int TransformBits(int effort, int histoBits)
    {
        int max = effort < 4 ? 6 : effort > 4 ? 4 : 5;
        return Math.Min(histoBits, max);
    }

    /// <summary>
    /// Writes an entropy-coded image (section 5): backward references, the color cache size that its statistics favour, for the
    /// main image from effort 3 meta prefix codes when they pay for themselves, the prefix codes and the tokens.
    /// </summary>
    private static void WriteImage(Vp8lBitWriter w, ReadOnlySpan<uint> pixels, int width, int height, int effort, bool main, bool usePalette = false)
    {
        int count = pixels.Length;
        ulong[] tokenArray = ArrayPool<ulong>.Shared.Rent(count);
        try
        {
            Vp8lBackwardRefs.Settings settings = Vp8lBackwardRefs.ForEffort(main ? effort : Math.Min(effort, 2));
            int n = Vp8lBackwardRefs.Build(pixels, width, settings, main ? 10 : 0, tokenArray, out int cacheBits);
            Span<ulong> tokens = tokenArray.AsSpan(0, n);
            if (cacheBits > 0)
            {
                Vp8lCache.Apply(tokens, pixels, cacheBits);
            }

            w.Write(cacheBits > 0 ? 1u : 0u, 1);
            if (cacheBits > 0)
            {
                w.Write((uint)cacheBits, 4);
            }

            Vp8lGroups groups = main && effort >= 1
                ? Vp8lGroups.Cluster(tokens, width, height, HistogramBits(effort, usePalette, width, height), cacheBits, effort)
                : Vp8lGroups.Single(tokens, cacheBits);
            if (main)
            {
                if (groups.Count > 1)
                {
                    w.Write(1, 1);
                    w.Write((uint)(groups.Bits - 2), 3);
                    WriteImage(w, groups.Map, groups.MapWidth, groups.MapHeight, effort, main: false);
                }
                else
                {
                    w.Write(0, 1);
                }
            }

            groups.Write(w, tokens, width);
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

    /// <summary>Returns the distance code of a distance in pixels: the plane code when the offset is one of the 120 nearby ones, else the distance plus 120.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int DistanceToCode(int distance, int width)
    {
        int dy = distance / width;
        int dx = distance - (dy * width);
        if (dx <= 8 && dy < 8)
        {
            int code = PlaneCodes[(dy * 16) + 8 - dx];
            if (code > 0)
            {
                return code;
            }
        }
        else if (dx > width - 8 && dy < 7)
        {
            int code = PlaneCodes[((dy + 1) * 16) + 8 + (width - dx)];
            if (code > 0)
            {
                return code;
            }
        }

        return distance + 120;
    }

    /// <summary>Gets the plane code for each (dy, 8 − dx) cell, 0 where none: the inverse of <see cref="Vp8lDecoder.PlaneX"/> and PlaneY.</summary>
    private static ReadOnlySpan<byte> PlaneCodes => PlaneCodeTable;

    private static readonly byte[] PlaneCodeTable = BuildPlaneCodes();

    private static byte[] BuildPlaneCodes()
    {
        byte[] table = new byte[16 * 9];
        for (int code = 1; code <= 120; code++)
        {
            int dx = Vp8lDecoder.PlaneX[code - 1];
            int dy = Vp8lDecoder.PlaneY[code - 1];
            table[(dy * 16) + 8 - dx] = (byte)code;
        }

        return table;
    }

    /// <summary>Returns the color cache slot of a pixel (section 5.2.3).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int CacheKey(uint argb, int cacheBits) => (int)((argb * 0x1E35A7BDu) >> (32 - cacheBits));

    /// <summary>Subtracts two pixels channel by channel, each wrapping around.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint Subtract(uint a, uint b)
    {
        uint ag = (a | 0x00FF00FF) - (b & 0xFF00FF00);
        uint rb = (a | 0xFF00FF00) - (b & 0x00FF00FF);
        return (ag & 0xFF00FF00) | (rb & 0x00FF00FF);
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

        /// <summary>Returns how many pixels a token covers.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int Pixels(ulong token) => token >> 62 == Backward ? (int)((token >> 32) & 0x3FFF_FFFF) : 1;
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
