using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

using Egami.Operations.Geometry;
using Egami.Pixels;

namespace Egami.Operations.Color;

/// <summary>
/// Applies a chain of 4 × 5 color matrices to straight RGBA, clamping to 0..1 after each stage, in one pass. The planner multiplies
/// adjacent matrices into one stage unless the earlier one can push a value out of 0..1, where the chain clamps as running the
/// steps one by one would. Chains of per-channel stages on 8-bit byte-per-channel types run as lookup tables.
/// </summary>
internal sealed class ColorMatrixOperation : IRasterOperation
{
    private readonly ColorMatrix[] _stages;
    private readonly int _count;

    internal ColorMatrixOperation(ColorMatrix matrix)
    {
        _stages = [matrix];
        _count = 1;
    }

    private ColorMatrixOperation(ColorMatrix[] stages, int count)
    {
        _stages = stages;
        _count = count;
    }

    /// <summary>Gets the stages, each clamped after it runs.</summary>
    internal IReadOnlyList<ColorMatrix> Stages => new ArraySegment<ColorMatrix>(_stages, 0, _count);

    public Size OutputSize(Size input)
    {
        return input;
    }

    /// <summary>
    /// Returns whether a matrix can map a color inside 0..1 to one outside. A matrix is affine, so it maps the unit cube onto the
    /// convex hull of its 16 corners' images: checking the corners decides it.
    /// </summary>
    internal static bool CanClip(ColorMatrix matrix)
    {
        for (int corner = 0; corner < 16; corner++)
        {
            for (int row = 0; row < 4; row++)
            {
                float v = matrix[row, 4];
                for (int c = 0; c < 4; c++)
                {
                    v += ((corner >> c) & 1) * matrix[row, c];
                }

                if (v < -1e-6f || v > 1 + 1e-6f)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Returns this chain followed by <paramref name="next"/>, fusing across stages that cannot clip.</summary>
    internal ColorMatrixOperation Then(ColorMatrixOperation next)
    {
        // One array, sized for the worst case; merged stages leave its end unused rather than costing a second copy.
        ColorMatrix[] stages = new ColorMatrix[_count + next._count];
        Array.Copy(_stages, stages, _count);
        int count = _count;
        for (int i = 0; i < next._count; i++)
        {
            if (!CanClip(stages[count - 1]))
            {
                stages[count - 1] = stages[count - 1].Then(next._stages[i]);
            }
            else
            {
                stages[count++] = next._stages[i];
            }
        }

        return new ColorMatrixOperation(stages, count);
    }

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        bool identity = true;
        for (int i = 0; i < _count; i++)
        {
            identity &= _stages[i] == ColorMatrix.Identity;
        }

        if (identity)
        {
            OperationRows.CopyIfSeparate(input);
            return;
        }

        int[]? map = ByteChannelOrder<TPixel>();
        if (map is not null)
        {
            RunBytes(input, map);
            return;
        }

        Vector4[][] rgbaStages = Columns(RgbaOrder);
        if (typeof(TPixel) == typeof(RgbaF32))
        {
            input.ForEachBand((start, end) =>
            {
                for (int y = start; y < end; y++)
                {
                    ColorKernels.ApplyFloat(
                        MemoryMarshal.Cast<TPixel, Vector4>(input.Source.Row(y)), MemoryMarshal.Cast<TPixel, Vector4>(input.Destination.Row(y)), rgbaStages);
                }
            });
            return;
        }

        // Other types go through RgbaF32: 16-bit values convert exactly, and gray types convert back through their luma.
        input.ForEachBand((start, end) =>
        {
            RgbaF32[] buffer = ArrayPool<RgbaF32>.Shared.Rent(input.Source.Width);
            try
            {
                Span<RgbaF32> row = buffer.AsSpan(0, input.Source.Width);
                for (int y = start; y < end; y++)
                {
                    PixelConvert.Convert<TPixel, RgbaF32>(input.Source.Row(y), row);
                    Span<Vector4> pixels = MemoryMarshal.Cast<RgbaF32, Vector4>(row);
                    ColorKernels.ApplyFloat(pixels, pixels, rgbaStages);
                    PixelConvert.Convert<RgbaF32, TPixel>(row, input.Destination.Row(y));
                }
            }
            finally
            {
                ArrayPool<RgbaF32>.Shared.Return(buffer);
            }
        });
    }

    private void RunBytes<TPixel>(OperationInput<TPixel> input, int[] map)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int channels = map.Length;
        Vector4[][] stages = Columns(channels == 4 ? map : map == RgbOrder ? RgbaOrder : BgrWithAlpha);
        bool allPerChannel = true;
        int prefix = -1;
        for (int i = 0; i < _count; i++)
        {
            if (!_stages[i].IsPerChannel)
            {
                allPerChannel = false;
                prefix = prefix < 0 ? i : prefix;
            }
        }

        // A chain of per-channel stages is four byte tables. Otherwise its leading per-channel stages (Brightness, Contrast, Invert,
        // Opacity) become float tables that replace the byte-to-float conversion, and only the stages after them run as matrices.
        // Both come from the pool: they are the only sizable allocations of a call.
        byte[]? tables = allPerChannel ? ArrayPool<byte>.Shared.Rent(channels * 256) : null;
        float[]? prefixTables = !allPerChannel && prefix > 0 && channels == 4 ? ArrayPool<float>.Shared.Rent(4 * 256) : null;
        try
        {
            if (tables is not null)
            {
                ColorKernels.BuildTables(stages, channels, tables);
            }

            if (prefixTables is not null)
            {
                ColorKernels.BuildFloatTables(stages.AsSpan(0, prefix), prefixTables);
            }

            Vector4[][] all = stages;
            int skip = prefixTables is null ? 0 : prefix;
            input.ForEachBand((start, end) =>
            {
                for (int y = start; y < end; y++)
                {
                    ReadOnlySpan<byte> source = MemoryMarshal.AsBytes(input.Source.Row(y));
                    Span<byte> destination = MemoryMarshal.AsBytes(input.Destination.Row(y));
                    if (tables is not null)
                    {
                        ColorKernels.ApplyTables(source, destination, tables, channels);
                    }
                    else if (channels == 4)
                    {
                        ColorKernels.Apply4(source, destination, all.AsSpan(skip), prefixTables);
                    }
                    else
                    {
                        ColorKernels.Apply3Scalar(source, destination, stages);
                    }
                }
            });
        }
        finally
        {
            if (tables is not null)
            {
                ArrayPool<byte>.Shared.Return(tables);
            }

            if (prefixTables is not null)
            {
                ArrayPool<float>.Shared.Return(prefixTables);
            }
        }
    }

    private Vector4[][] Columns(int[] map)
    {
        Vector4[][] columns = new Vector4[_count][];
        for (int i = 0; i < columns.Length; i++)
        {
            columns[i] = ColorKernels.MemoryOrderColumns(_stages[i], map);
        }

        return columns;
    }

    private static readonly int[] RgbaOrder = [0, 1, 2, 3];
    private static readonly int[] BgraOrder = [2, 1, 0, 3];
    private static readonly int[] RgbOrder = [0, 1, 2];
    private static readonly int[] BgrOrder = [2, 1, 0];
    private static readonly int[] BgrWithAlpha = [2, 1, 0, 3];

    /// <summary>
    /// Gets, for types that store one byte per channel, which RGBA channel each memory byte holds; null for other types.
    /// </summary>
    private static int[]? ByteChannelOrder<TPixel>()
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        // Premultiplied bytes are not the straight values the color math expects, so they take the converting path.
        if (TPixel.Layout.ChannelType != PixelChannelType.Unorm8 || TPixel.Layout.Alpha == AlphaKind.Premultiplied)
        {
            return null;
        }

        return TPixel.Layout.ChannelOrder switch
        {
            PixelChannelOrder.Rgba => RgbaOrder,
            PixelChannelOrder.Bgra => BgraOrder,
            PixelChannelOrder.Rgb => RgbOrder,
            PixelChannelOrder.Bgr => BgrOrder,
            _ => null,
        };
    }
}

/// <summary>Converts toward gray in linear light: decode, take the luma, mix, encode.</summary>
internal sealed class LinearGrayscaleOperation(LumaWeights mode, float amount) : IRasterOperation
{
    public Size OutputSize(Size input)
    {
        return input;
    }

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        (float wr, float wg, float wb) = ColorMatrix.Weights(mode);
        bool eightBit = TPixel.Layout.ChannelType == PixelChannelType.Unorm8;
        float[] toLinear = Srgb.LinearFrom8;
        input.ForEachBand((start, end) =>
        {
            RgbaF32[] buffer = ArrayPool<RgbaF32>.Shared.Rent(input.Source.Width);
            try
            {
                Span<RgbaF32> row = buffer.AsSpan(0, input.Source.Width);
                for (int y = start; y < end; y++)
                {
                    PixelConvert.Convert<TPixel, RgbaF32>(input.Source.Row(y), row);
                    foreach (ref RgbaF32 p in row)
                    {
                        float r = eightBit ? toLinear[(int)((p.R * 255f) + 0.5f)] : (float)Srgb.ToLinear(Math.Clamp(p.R, 0f, 1f));
                        float g = eightBit ? toLinear[(int)((p.G * 255f) + 0.5f)] : (float)Srgb.ToLinear(Math.Clamp(p.G, 0f, 1f));
                        float b = eightBit ? toLinear[(int)((p.B * 255f) + 0.5f)] : (float)Srgb.ToLinear(Math.Clamp(p.B, 0f, 1f));
                        float luma = (wr * r) + (wg * g) + (wb * b);
                        p.R = Encode(r + ((luma - r) * amount), eightBit);
                        p.G = Encode(g + ((luma - g) * amount), eightBit);
                        p.B = Encode(b + ((luma - b) * amount), eightBit);
                    }

                    PixelConvert.Convert<RgbaF32, TPixel>(row, input.Destination.Row(y));
                }
            }
            finally
            {
                ArrayPool<RgbaF32>.Shared.Return(buffer);
            }
        });
    }

    private static float Encode(float linear, bool eightBit)
    {
        return eightBit ? Srgb.FromLinearTo8(linear) / 255f : (float)Srgb.FromLinear(Math.Clamp(linear, 0f, 1f));
    }
}

/// <summary>
/// The inner loops of the color matrix. Each stage is five column vectors; every stage's result is clamped to 0..1 before the next
/// one reads it. Vector paths add in the same order as the scalar references.
/// </summary>
internal static class ColorKernels
{
    /// <summary>
    /// Returns the matrix as five column vectors in memory channel order: columns 0..3 weigh memory channels 0..3, column 4 is the
    /// offset. <paramref name="map"/> gives the RGBA channel of each of the four lanes; three-byte types pass alpha as lane 3,
    /// which reads 1.
    /// </summary>
    internal static Vector4[] MemoryOrderColumns(ColorMatrix matrix, int[] map)
    {
        Vector4[] columns = new Vector4[5];
        Span<float> c = stackalloc float[4];
        for (int col = 0; col < 5; col++)
        {
            for (int row = 0; row < 4; row++)
            {
                c[row] = matrix[map[row], col == 4 ? 4 : map[col]];
            }

            columns[col] = new Vector4(c[0], c[1], c[2], c[3]);
        }

        return columns;
    }

    /// <summary>Builds one 256-entry table per memory channel for a chain of per-channel stages.</summary>
    internal static byte[] BuildTables(ReadOnlySpan<Vector4[]> stages, int channels, byte[]? tables = null)
    {
        tables ??= new byte[channels * 256];
        for (int ch = 0; ch < channels; ch++)
        {
            for (int v = 0; v < 256; v++)
            {
                // The same arithmetic as the matrix path, with the zero weights left out.
                float x = v * (1f / 255f);
                foreach (Vector4[] columns in stages)
                {
                    x = Clamp01((x * columns[ch][ch]) + columns[4][ch]);
                }

                tables[(ch * 256) + v] = (byte)(int)((x * 255f) + 0.5f);
            }
        }

        return tables;
    }

    /// <summary>Maps every byte through its channel's table.</summary>
    internal static void ApplyTables(ReadOnlySpan<byte> source, Span<byte> destination, ReadOnlySpan<byte> tables, int channels)
    {
        if (destination.Length < source.Length || tables.Length < channels * 256)
        {
            throw new ArgumentException("The destination or tables are too short.");
        }

        ref byte t = ref MemoryMarshal.GetReference(tables);
        if (channels == 4)
        {
            // A whole pixel per step: four lookups, one 32-bit store, no bounds checks (a byte index never leaves its table).
            ReadOnlySpan<uint> src = MemoryMarshal.Cast<byte, uint>(source);
            Span<uint> dst = MemoryMarshal.Cast<byte, uint>(destination);
            ref uint s = ref MemoryMarshal.GetReference(src);
            ref uint d = ref MemoryMarshal.GetReference(dst);
            for (int i = 0; i < src.Length; i++)
            {
                uint p = Unsafe.Add(ref s, i);
                Unsafe.Add(ref d, i) = Unsafe.Add(ref t, (int)(p & 0xFF))
                    | ((uint)Unsafe.Add(ref t, 256 + (int)((p >> 8) & 0xFF)) << 8)
                    | ((uint)Unsafe.Add(ref t, 512 + (int)((p >> 16) & 0xFF)) << 16)
                    | ((uint)Unsafe.Add(ref t, 768 + (int)(p >> 24)) << 24);
            }

            return;
        }

        for (int i = 0; i < source.Length; i++)
        {
            destination[i] = Unsafe.Add(ref t, ((i % channels) * 256) + source[i]);
        }
    }

    /// <summary>
    /// Builds 256 floats per memory channel: each byte value run through per-channel stages, clamped after each, with the same
    /// arithmetic as the matrix path.
    /// </summary>
    internal static float[] BuildFloatTables(ReadOnlySpan<Vector4[]> stages, float[]? tables = null)
    {
        tables ??= new float[4 * 256];
        for (int ch = 0; ch < 4; ch++)
        {
            for (int v = 0; v < 256; v++)
            {
                float x = v * (1f / 255f);
                foreach (Vector4[] columns in stages)
                {
                    x = Clamp01((x * columns[ch][ch]) + columns[4][ch]);
                }

                tables[(ch * 256) + v] = x;
            }
        }

        return tables;
    }

    /// <summary>
    /// Applies the stages to 4-byte pixels, four pixels per step in two 256-bit vectors. With <paramref name="prefixTables"/>,
    /// each byte first maps through its channel's float table (the chain's leading per-channel stages) instead of dividing by 255.
    /// </summary>
    internal static void Apply4(ReadOnlySpan<byte> source, Span<byte> destination, ReadOnlySpan<Vector4[]> stages, float[]? prefixTables = null)
    {
        int i = 0;
        if (Vector256.IsHardwareAccelerated && source.Length >= 16)
        {
            // Each channel is broadcast within its pixel's half, so every lane adds its five terms in the scalar order.
            Span<Vector256<float>> w = stackalloc Vector256<float>[stages.Length * 5];
            for (int s = 0; s < stages.Length; s++)
            {
                for (int k = 0; k < 5; k++)
                {
                    w[(s * 5) + k] = Both(stages[s][k]);
                }
            }

            Vector256<float> scale = Vector256.Create(1f / 255f);
            if (prefixTables is not null && prefixTables.Length < 4 * 256)
            {
                throw new ArgumentException("The prefix tables are too short.");
            }

            ref float t = ref prefixTables is null ? ref Unsafe.NullRef<float>() : ref MemoryMarshal.GetArrayDataReference(prefixTables);
            ref byte src = ref MemoryMarshal.GetReference(source);
            ref byte dst = ref MemoryMarshal.GetReference(destination);
            for (; i <= source.Length - 16; i += 16)
            {
                Vector256<float> fa, fb;
                if (prefixTables is null)
                {
                    (Vector256<ushort> lo, _) = Vector256.Widen(Vector256.Create(Vector128.LoadUnsafe(ref src, (nuint)i), Vector128<byte>.Zero));
                    (Vector256<uint> a, Vector256<uint> b) = Vector256.Widen(lo);
                    fa = Vector256.ConvertToSingle(a.AsInt32()) * scale;
                    fb = Vector256.ConvertToSingle(b.AsInt32()) * scale;
                }
                else
                {
                    fa = Lookup(ref t, ref Unsafe.Add(ref src, i));
                    fb = Lookup(ref t, ref Unsafe.Add(ref src, i + 8));
                }

                Vector256<int> ra = Vector256.ConvertToInt32(Transform(fa, w));
                Vector256<int> rb = Vector256.ConvertToInt32(Transform(fb, w));
                Vector256<ushort> words = Vector256.Narrow(ra.AsUInt32(), rb.AsUInt32());
                Vector256.Narrow(words, words).GetLower().StoreUnsafe(ref dst, (nuint)i);
            }
        }

        Apply4Scalar(source[i..], destination[i..], stages, prefixTables);
    }

    /// <summary>Reads two pixels' floats from the prefix tables: a byte index never leaves its 256-entry table.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> Lookup(ref float t, ref byte p)
    {
        return Vector256.Create(
            Unsafe.Add(ref t, p), Unsafe.Add(ref t, 256 + Unsafe.Add(ref p, 1)), Unsafe.Add(ref t, 512 + Unsafe.Add(ref p, 2)), Unsafe.Add(ref t, 768 + Unsafe.Add(ref p, 3)),
            Unsafe.Add(ref t, Unsafe.Add(ref p, 4)), Unsafe.Add(ref t, 256 + Unsafe.Add(ref p, 5)), Unsafe.Add(ref t, 512 + Unsafe.Add(ref p, 6)), Unsafe.Add(ref t, 768 + Unsafe.Add(ref p, 7)));
    }

    /// <summary>The reference for <see cref="Apply4"/>.</summary>
    internal static void Apply4Scalar(ReadOnlySpan<byte> source, Span<byte> destination, ReadOnlySpan<Vector4[]> stages, float[]? prefixTables = null)
    {
        Span<float> v = stackalloc float[4];
        Span<float> o = stackalloc float[4];
        for (int i = 0; i + 3 < source.Length; i += 4)
        {
            for (int ch = 0; ch < 4; ch++)
            {
                v[ch] = prefixTables is null ? source[i + ch] * (1f / 255f) : prefixTables[(ch * 256) + source[i + ch]];
            }

            Transform(v, o, stages);
            for (int ch = 0; ch < 4; ch++)
            {
                destination[i + ch] = (byte)(int)((v[ch] * 255f) + 0.5f);
            }
        }
    }

    /// <summary>Applies the stages to 3-byte pixels, with alpha reading 1.</summary>
    internal static void Apply3Scalar(ReadOnlySpan<byte> source, Span<byte> destination, ReadOnlySpan<Vector4[]> stages)
    {
        Span<float> v = stackalloc float[4];
        Span<float> o = stackalloc float[4];
        for (int i = 0; i + 2 < source.Length; i += 3)
        {
            for (int ch = 0; ch < 3; ch++)
            {
                v[ch] = source[i + ch] * (1f / 255f);
            }

            v[3] = 1f;
            Transform(v, o, stages);
            for (int ch = 0; ch < 3; ch++)
            {
                destination[i + ch] = (byte)(int)((v[ch] * 255f) + 0.5f);
            }
        }
    }

    /// <summary>Applies the stages to float pixels in RGBA order. Source and destination may be the same.</summary>
    internal static void ApplyFloat(ReadOnlySpan<Vector4> source, Span<Vector4> destination, Vector4[][] stages)
    {
        for (int i = 0; i < source.Length; i++)
        {
            Vector4 v = source[i];
            foreach (Vector4[] c in stages)
            {
                v = Vector4.Min(Vector4.Max((c[0] * v.X) + (c[1] * v.Y) + (c[2] * v.Z) + (c[3] * v.W) + c[4], Vector4.Zero), Vector4.One);
            }

            destination[i] = v;
        }
    }

    /// <summary>Runs one pixel through every stage in place in <paramref name="v"/>, clamping after each.</summary>
    private static void Transform(Span<float> v, Span<float> o, ReadOnlySpan<Vector4[]> stages)
    {
        foreach (Vector4[] c in stages)
        {
            for (int ch = 0; ch < 4; ch++)
            {
                o[ch] = Clamp01((c[0][ch] * v[0]) + (c[1][ch] * v[1]) + (c[2][ch] * v[2]) + (c[3][ch] * v[3]) + c[4][ch]);
            }

            o.CopyTo(v);
        }
    }

    /// <summary>Runs two pixels through every stage, clamping after each, and scales the result to 0..255 plus the rounding half.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> Transform(Vector256<float> v, ReadOnlySpan<Vector256<float>> w)
    {
        for (int k = 0; k + 4 < w.Length; k += 5)
        {
            Vector256<float> o = (w[k] * Broadcast(v, 0)) + (w[k + 1] * Broadcast(v, 1)) + (w[k + 2] * Broadcast(v, 2))
                + (w[k + 3] * Broadcast(v, 3)) + w[k + 4];
            // The native min and max (minps, maxps) equal the scalar clamp for every finite value; the IEEE-exact versions cost
            // several instructions each.
            v = Vector256.MinNative(Vector256.MaxNative(o, Vector256<float>.Zero), Vector256<float>.One);
        }

        return (v * 255f) + Vector256.Create(0.5f);
    }

    /// <summary>Copies channel <paramref name="channel"/> of each pixel to all four lanes of that pixel's half.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector256<float> Broadcast(Vector256<float> v, [ConstantExpected(Min = 0, Max = 3)] byte channel)
    {
        if (Avx.IsSupported)
        {
            // vpermilps with an immediate shuffles within each 128-bit half, which is exactly one pixel.
            return channel switch
            {
                0 => Avx.Permute(v, 0b00_00_00_00),
                1 => Avx.Permute(v, 0b01_01_01_01),
                2 => Avx.Permute(v, 0b10_10_10_10),
                _ => Avx.Permute(v, 0b11_11_11_11),
            };
        }

        int c = channel;
        return Vector256.Shuffle(v, Vector256.Create(c, c, c, c, c + 4, c + 4, c + 4, c + 4));
    }

    private static Vector256<float> Both(Vector4 column)
    {
        Vector128<float> half = column.AsVector128();
        return Vector256.Create(half, half);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Clamp01(float value)
    {
        return value > 0f ? (value < 1f ? value : 1f) : 0f;
    }
}
