using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

using Egami.Operations.Geometry;
using Egami.Pixels;

namespace Egami.Operations.Color;

/// <summary>
/// Applies a 4 × 5 color matrix to straight RGBA and clamps to 0..1. The planner multiplies adjacent matrices into one, so a chain
/// of adjustments is one pass. Per-channel matrices on 8-bit byte-per-channel types run as lookup tables.
/// </summary>
internal sealed class ColorMatrixOperation(ColorMatrix matrix) : IRasterOperation
{
    /// <summary>Gets the matrix.</summary>
    internal ColorMatrix Matrix => matrix;

    public Size OutputSize(Size input)
    {
        return input;
    }

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (matrix == ColorMatrix.Identity)
        {
            OperationRows.CopyIfSeparate(input);
            return;
        }

        ReadOnlySpan<int> order = ByteChannelOrder<TPixel>();
        if (!order.IsEmpty)
        {
            int[] map = order.ToArray();
            Vector4[] columns = ColorKernels.MemoryOrderColumns(matrix, map);
            byte[]? tables = matrix.IsPerChannel ? ColorKernels.BuildTables(columns, map.Length) : null;
            input.ForEachBand((start, end) =>
            {
                for (int y = start; y < end; y++)
                {
                    ReadOnlySpan<byte> source = MemoryMarshal.AsBytes(input.Source.Row(y));
                    Span<byte> destination = MemoryMarshal.AsBytes(input.Destination.Row(y));
                    if (tables is not null)
                    {
                        ColorKernels.ApplyTables(source, destination, tables, map.Length);
                    }
                    else if (map.Length == 4)
                    {
                        ColorKernels.Apply4(source, destination, columns);
                    }
                    else
                    {
                        ColorKernels.Apply3Scalar(source, destination, columns);
                    }
                }
            });
            return;
        }

        Vector4[] rgbaColumns = ColorKernels.MemoryOrderColumns(matrix, [0, 1, 2, 3]);
        if (typeof(TPixel) == typeof(RgbaF32))
        {
            input.ForEachBand((start, end) =>
            {
                for (int y = start; y < end; y++)
                {
                    ColorKernels.ApplyFloat(
                        MemoryMarshal.Cast<TPixel, Vector4>(input.Source.Row(y)), MemoryMarshal.Cast<TPixel, Vector4>(input.Destination.Row(y)), rgbaColumns);
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
                    ColorKernels.ApplyFloat(pixels, pixels, rgbaColumns);
                    PixelConvert.Convert<RgbaF32, TPixel>(row, input.Destination.Row(y));
                }
            }
            finally
            {
                ArrayPool<RgbaF32>.Shared.Return(buffer);
            }
        });
    }

    /// <summary>
    /// Gets, for types that store one byte per channel, which RGBA channel each memory byte holds; empty for other types.
    /// </summary>
    private static ReadOnlySpan<int> ByteChannelOrder<TPixel>()
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (TPixel.Layout.ChannelType != PixelChannelType.Unorm8)
        {
            return [];
        }

        return TPixel.Layout.ChannelOrder switch
        {
            PixelChannelOrder.Rgba => [0, 1, 2, 3],
            PixelChannelOrder.Bgra => [2, 1, 0, 3],
            PixelChannelOrder.Rgb => [0, 1, 2],
            PixelChannelOrder.Bgr => [2, 1, 0],
            _ => [],
        };
    }
}

/// <summary>Converts toward gray in linear light: decode, take the luma, mix, encode.</summary>
internal sealed class LinearGrayscaleOperation(GrayscaleMode mode, float amount) : IRasterOperation
{
    public Size OutputSize(Size input)
    {
        return input;
    }

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        (float wr, float wg, float wb) = ColorMatrix.LumaWeights(mode);
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

/// <summary>The inner loops of the color matrix. Vector paths add in the same order as the scalar references.</summary>
internal static class ColorKernels
{
    /// <summary>
    /// Returns the matrix as five column vectors in memory channel order: columns 0..3 weigh memory channels 0..3, column 4 is the
    /// offset. <paramref name="map"/> gives the RGBA channel of each memory channel; with three channels alpha reads as 1, so its
    /// weight folds into the offset.
    /// </summary>
    internal static Vector4[] MemoryOrderColumns(ColorMatrix matrix, int[] map)
    {
        Vector4[] columns = new Vector4[5];
        Span<float> c = stackalloc float[4];
        for (int col = 0; col < 5; col++)
        {
            for (int row = 0; row < 4; row++)
            {
                int outChannel = row < map.Length ? map[row] : 3;
                int inChannel = col == 4 ? 4 : col < map.Length ? map[col] : -1;
                float offset = col == 4 && map.Length == 3 ? matrix[outChannel, 3] : 0f;
                c[row] = inChannel < 0 ? 0f : matrix[outChannel, inChannel] + offset;
            }

            columns[col] = new Vector4(c[0], c[1], c[2], c[3]);
        }

        return columns;
    }

    /// <summary>Builds one 256-entry table per memory channel for a per-channel matrix.</summary>
    internal static byte[] BuildTables(Vector4[] columns, int channels)
    {
        byte[] tables = new byte[channels * 256];
        for (int ch = 0; ch < channels; ch++)
        {
            for (int v = 0; v < 256; v++)
            {
                // The same arithmetic as the matrix path, with the zero weights left out.
                float x = v * (1f / 255f);
                tables[(ch * 256) + v] = ToByte((x * columns[ch][ch]) + columns[4][ch]);
            }
        }

        return tables;
    }

    /// <summary>Maps every byte through its channel's table.</summary>
    internal static void ApplyTables(ReadOnlySpan<byte> source, Span<byte> destination, ReadOnlySpan<byte> tables, int channels)
    {
        if (channels == 4)
        {
            ReadOnlySpan<byte> t0 = tables[..256];
            ReadOnlySpan<byte> t1 = tables.Slice(256, 256);
            ReadOnlySpan<byte> t2 = tables.Slice(512, 256);
            ReadOnlySpan<byte> t3 = tables.Slice(768, 256);
            for (int i = 0; i + 3 < source.Length; i += 4)
            {
                destination[i] = t0[source[i]];
                destination[i + 1] = t1[source[i + 1]];
                destination[i + 2] = t2[source[i + 2]];
                destination[i + 3] = t3[source[i + 3]];
            }

            return;
        }

        for (int i = 0; i < source.Length; i++)
        {
            destination[i] = tables[((i % channels) * 256) + source[i]];
        }
    }

    /// <summary>Applies the matrix to 4-byte pixels, one pixel per 128-bit vector.</summary>
    internal static void Apply4(ReadOnlySpan<byte> source, Span<byte> destination, Vector4[] columns)
    {
        if (!Vector128.IsHardwareAccelerated)
        {
            Apply4Scalar(source, destination, columns);
            return;
        }

        ReadOnlySpan<uint> src = MemoryMarshal.Cast<byte, uint>(source);
        Span<uint> dst = MemoryMarshal.Cast<byte, uint>(destination);
        Vector4 c0 = columns[0], c1 = columns[1], c2 = columns[2], c3 = columns[3], c4 = columns[4];
        int i = 0;
        if (Vector256.IsHardwareAccelerated && src.Length >= 4)
        {
            // Four pixels per step, two per 256-bit vector. Each channel is broadcast within its pixel's half, so every lane adds
            // its five terms in the same order as the scalar reference.
            Vector256<float> w0 = Both(c0), w1 = Both(c1), w2 = Both(c2), w3 = Both(c3), w4 = Both(c4);
            Vector256<float> scale = Vector256.Create(1f / 255f);
            Vector256<int> xs = Vector256.Create(0, 0, 0, 0, 4, 4, 4, 4);
            Vector256<int> ys = Vector256.Create(1, 1, 1, 1, 5, 5, 5, 5);
            Vector256<int> zs = Vector256.Create(2, 2, 2, 2, 6, 6, 6, 6);
            Vector256<int> ws = Vector256.Create(3, 3, 3, 3, 7, 7, 7, 7);
            ref byte s = ref MemoryMarshal.GetReference(source);
            ref byte d = ref MemoryMarshal.GetReference(destination);
            for (; i <= src.Length - 4; i += 4)
            {
                (Vector256<ushort> lo, _) = Vector256.Widen(Vector256.Create(Vector128.LoadUnsafe(ref s, (nuint)(i * 4)), Vector128<byte>.Zero));
                (Vector256<uint> a, Vector256<uint> b) = Vector256.Widen(lo);
                Vector256<int> ra = Vector256.ConvertToInt32(Transform(Vector256.ConvertToSingle(a.AsInt32()) * scale));
                Vector256<int> rb = Vector256.ConvertToInt32(Transform(Vector256.ConvertToSingle(b.AsInt32()) * scale));
                Vector256<ushort> words = Vector256.Narrow(ra.AsUInt32(), rb.AsUInt32());
                Vector256.Narrow(words, words).GetLower().StoreUnsafe(ref d, (nuint)(i * 4));
            }

            Vector256<float> Transform(Vector256<float> v)
            {
                Vector256<float> o = (w0 * Vector256.Shuffle(v, xs)) + (w1 * Vector256.Shuffle(v, ys)) + (w2 * Vector256.Shuffle(v, zs))
                    + (w3 * Vector256.Shuffle(v, ws)) + w4;
                return (Vector256.Min(Vector256.Max(o, Vector256<float>.Zero), Vector256<float>.One) * 255f) + Vector256.Create(0.5f);
            }
        }

        for (; i < src.Length; i++)
        {
            Vector128<uint> wide = Vector128.WidenLower(Vector128.WidenLower(Vector128.CreateScalarUnsafe(src[i]).AsByte()));
            Vector4 v = (Vector128.ConvertToSingle(wide.AsInt32()) * (1f / 255f)).AsVector4();
            Vector4 o = (c0 * v.X) + (c1 * v.Y) + (c2 * v.Z) + (c3 * v.W) + c4;
            Vector128<int> rounded = Vector128.ConvertToInt32((Clamp01(o) * 255f).AsVector128() + Vector128.Create(0.5f));
            Vector128<ushort> words = Vector128.Narrow(rounded.AsUInt32(), rounded.AsUInt32());
            dst[i] = Vector128.Narrow(words, words).AsUInt32().ToScalar();
        }
    }

    /// <summary>The reference for <see cref="Apply4"/>.</summary>
    internal static void Apply4Scalar(ReadOnlySpan<byte> source, Span<byte> destination, Vector4[] columns)
    {
        for (int i = 0; i + 3 < source.Length; i += 4)
        {
            float x = source[i] * (1f / 255f), y = source[i + 1] * (1f / 255f), z = source[i + 2] * (1f / 255f), w = source[i + 3] * (1f / 255f);
            for (int ch = 0; ch < 4; ch++)
            {
                destination[i + ch] = ToByte((columns[0][ch] * x) + (columns[1][ch] * y) + (columns[2][ch] * z) + (columns[3][ch] * w) + columns[4][ch]);
            }
        }
    }

    /// <summary>Applies the matrix to 3-byte pixels; alpha is already folded into the offset.</summary>
    internal static void Apply3Scalar(ReadOnlySpan<byte> source, Span<byte> destination, Vector4[] columns)
    {
        for (int i = 0; i + 2 < source.Length; i += 3)
        {
            float x = source[i] * (1f / 255f), y = source[i + 1] * (1f / 255f), z = source[i + 2] * (1f / 255f);
            for (int ch = 0; ch < 3; ch++)
            {
                destination[i + ch] = ToByte((columns[0][ch] * x) + (columns[1][ch] * y) + (columns[2][ch] * z) + columns[4][ch]);
            }
        }
    }

    /// <summary>Applies the matrix to float pixels in RGBA order, clamping to 0..1. Source and destination may be the same.</summary>
    internal static void ApplyFloat(ReadOnlySpan<Vector4> source, Span<Vector4> destination, Vector4[] columns)
    {
        Vector4 c0 = columns[0], c1 = columns[1], c2 = columns[2], c3 = columns[3], c4 = columns[4];
        for (int i = 0; i < source.Length; i++)
        {
            Vector4 v = source[i];
            destination[i] = Clamp01((c0 * v.X) + (c1 * v.Y) + (c2 * v.Z) + (c3 * v.W) + c4);
        }
    }

    private static Vector256<float> Both(Vector4 column)
    {
        Vector128<float> half = column.AsVector128();
        return Vector256.Create(half, half);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ToByte(float value)
    {
        float clamped = value > 0f ? (value < 1f ? value : 1f) : 0f;
        return (byte)(int)((clamped * 255f) + 0.5f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector4 Clamp01(Vector4 v)
    {
        return Vector4.Min(Vector4.Max(v, Vector4.Zero), Vector4.One);
    }
}
