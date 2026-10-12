using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

using Egami.Pixels;

namespace Egami.Formats.Exr;

/// <summary>Composes decoded channel planes into raster rows: RGBA, gray, one channel, or luminance and chroma converted.</summary>
internal static class ExrCompose
{
    /// <summary>Fills <paramref name="raster"/>, which covers <paramref name="window"/>; returns the largest color value written.</summary>
    public static float Compose<TPixel>(ExrPlanes planes, ExrPartHeader part, Selection selection, Rect window, Raster<TPixel> raster, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (selection.Kind == SelectionKind.Channel)
        {
            ComposeChannel(planes, part, selection.Roles[0], raster, cancellationToken);
            return 0;
        }

        int width = window.Width;
        int x0 = Math.Max(window.X, part.MinX), x1 = Math.Min(window.X + width - 1, part.MaxX);
        RgbaF16[]? yca = selection.Kind == SelectionKind.Yca ? ExrYca.Convert(planes, part, selection) : null;
        bool half = selection.Layout.ChannelType == PixelChannelType.Float16;
        bool gray = selection.Layout.ChannelOrder == PixelChannelOrder.Gray;
        bool direct = (half && typeof(TPixel) == typeof(RgbaF16)) || (!half && !gray && typeof(TPixel) == typeof(RgbaF32)) || (gray && typeof(TPixel) == typeof(GrayF32));
        int pixelBytes = half ? 8 : gray ? 4 : 16;
        byte[] temp = direct ? [] : ArrayPool<byte>.Shared.Rent(width * pixelBytes);
        float[] scratch = ArrayPool<float>.Shared.Rent(width);
        ushort peakBits = 0;
        float peak = 0;
        try
        {
            for (int oy = 0; oy < raster.Height; oy++)
            {
                if ((oy & 63) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                Span<byte> row = direct ? MemoryMarshal.AsBytes(raster.Row(oy)) : temp.AsSpan(0, width * pixelBytes);
                int y = window.Y + oy;
                bool inside = y >= part.MinY && y <= part.MaxY && x0 <= x1;
                if (!inside || x0 > window.X || x1 < window.X + width - 1)
                {
                    row.Clear();
                }

                if (inside)
                {
                    int n = x1 - x0 + 1;
                    Span<byte> target = row.Slice((x0 - window.X) * pixelBytes, n * pixelBytes);
                    if (yca is not null)
                    {
                        Span<RgbaF16> source = yca.AsSpan(((y - part.MinY) * part.Width) + (x0 - part.MinX), n);
                        MemoryMarshal.AsBytes(source).CopyTo(target);
                        peakBits = Math.Max(peakBits, PeakBits(MemoryMarshal.Cast<RgbaF16, ulong>(source)));
                    }
                    else if (half)
                    {
                        peakBits = Math.Max(peakBits, FillHalf(planes, part, selection.Roles, y, x0, n, MemoryMarshal.Cast<byte, ulong>(target)));
                    }
                    else if (gray)
                    {
                        Span<float> values = MemoryMarshal.Cast<byte, float>(target);
                        ReadLine(planes, part, selection.Roles[0], y, x0, scratch.AsSpan(0, n));
                        scratch.AsSpan(0, n).CopyTo(values);
                        peak = MathF.Max(peak, Peak(values, 1));
                    }
                    else
                    {
                        Span<float> values = MemoryMarshal.Cast<byte, float>(target);
                        for (int k = 0; k < 4; k++)
                        {
                            int c = selection.Roles[k];
                            if (c < 0)
                            {
                                Fill(values, k, k == 3 ? 1 : 0);
                                continue;
                            }

                            Span<float> line = scratch.AsSpan(0, n);
                            ReadLine(planes, part, c, y, x0, line);
                            for (int i = 0; i < n; i++)
                            {
                                values[(i * 4) + k] = line[i];
                            }
                        }

                        peak = MathF.Max(peak, Peak(values, 4));
                    }
                }

                if (!direct)
                {
                    if (half)
                    {
                        PixelConvert.Convert<RgbaF16, TPixel>(MemoryMarshal.Cast<byte, RgbaF16>(row), raster.Row(oy));
                    }
                    else if (gray)
                    {
                        PixelConvert.Convert<GrayF32, TPixel>(MemoryMarshal.Cast<byte, GrayF32>(row), raster.Row(oy));
                    }
                    else
                    {
                        PixelConvert.Convert<RgbaF32, TPixel>(MemoryMarshal.Cast<byte, RgbaF32>(row), raster.Row(oy));
                    }
                }
            }

            return half ? (float)BitConverter.UInt16BitsToHalf(peakBits) : peak;
        }
        finally
        {
            if (temp.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(temp);
            }

            ArrayPool<float>.Shared.Return(scratch);
        }
    }

    /// <summary>Copies one channel's plane, at its stored resolution, into the raster as floats.</summary>
    private static void ComposeChannel<TPixel>(ExrPlanes planes, ExrPartHeader part, int c, Raster<TPixel> raster, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int width = planes.Widths[c];
        GrayF32[] row = ArrayPool<GrayF32>.Shared.Rent(width);
        try
        {
            for (int y = 0; y < planes.Heights[c]; y++)
            {
                if ((y & 63) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                Span<float> values = MemoryMarshal.Cast<GrayF32, float>(row.AsSpan(0, width));
                ConvertSamples(part.Channels[c].Type, PlaneRow(planes, part, c, y), values);
                PixelConvert.Convert<GrayF32, TPixel>(row.AsSpan(0, width), raster.Row(y));
            }
        }
        finally
        {
            ArrayPool<GrayF32>.Shared.Return(row);
        }
    }

    /// <summary>
    /// Interleaves full-resolution half planes into RGBA pixels; a missing color channel is 0 and a missing alpha 1. Returns the
    /// largest positive finite color sample, as half bits, which order as the values do.
    /// </summary>
    private static ushort FillHalf(ExrPlanes planes, ExrPartHeader part, int[] roles, int y, int x0, int n, Span<ulong> target)
    {
        int at = ((y - part.MinY) * part.Width) + (x0 - part.MinX);
        ReadOnlySpan<ushort> r = Line(planes, roles[0], at, n), g = Line(planes, roles[1], at, n), b = Line(planes, roles[2], at, n), a = Line(planes, roles[3], at, n);
        ulong fill = roles[3] < 0 ? 0x3C00UL << 48 : 0;
        ref ulong dst = ref MemoryMarshal.GetReference(target);
        uint peak = 0;
        int i = 0;
        if (Sse2.IsSupported && !r.IsEmpty && !g.IsEmpty && !b.IsEmpty)
        {
            // Eight pixels at a time: interleave 16-bit lanes, then 32-bit pairs; the peak from masked unsigned maxima.
            Vector128<ushort> alpha = Vector128.Create((ushort)0x3C00);
            Vector128<ushort> sign = Vector128.Create((ushort)0x8000), exponent = Vector128.Create((ushort)0x7C00);
            Vector128<ushort> best = Vector128<ushort>.Zero;
            ref ushort rr = ref MemoryMarshal.GetReference(r), gr = ref MemoryMarshal.GetReference(g), br = ref MemoryMarshal.GetReference(b);
            ref ushort ar = ref a.IsEmpty ? ref Unsafe.NullRef<ushort>() : ref MemoryMarshal.GetReference(a);
            for (; i + 8 <= n; i += 8)
            {
                Vector128<ushort> rv = Vector128.LoadUnsafe(ref rr, (nuint)i), gv = Vector128.LoadUnsafe(ref gr, (nuint)i), bv = Vector128.LoadUnsafe(ref br, (nuint)i);
                Vector128<ushort> av = a.IsEmpty ? alpha : Vector128.LoadUnsafe(ref ar, (nuint)i);
                Vector128<uint> rg0 = Sse2.UnpackLow(rv, gv).AsUInt32(), rg1 = Sse2.UnpackHigh(rv, gv).AsUInt32();
                Vector128<uint> ba0 = Sse2.UnpackLow(bv, av).AsUInt32(), ba1 = Sse2.UnpackHigh(bv, av).AsUInt32();
                ref ulong o = ref Unsafe.Add(ref dst, i);
                Sse2.UnpackLow(rg0, ba0).AsUInt64().StoreUnsafe(ref o);
                Sse2.UnpackHigh(rg0, ba0).AsUInt64().StoreUnsafe(ref o, 2);
                Sse2.UnpackLow(rg1, ba1).AsUInt64().StoreUnsafe(ref o, 4);
                Sse2.UnpackHigh(rg1, ba1).AsUInt64().StoreUnsafe(ref o, 6);
                best = Vector128.Max(best, Masked(rv, sign, exponent));
                best = Vector128.Max(best, Masked(gv, sign, exponent));
                best = Vector128.Max(best, Masked(bv, sign, exponent));
            }

            for (int k = 0; k < 8; k++)
            {
                peak = Math.Max(peak, best.GetElement(k));
            }
        }

        for (; i < n; i++)
        {
            uint rv = r.IsEmpty ? 0u : r[i], gv = g.IsEmpty ? 0u : g[i], bv = b.IsEmpty ? 0u : b[i];
            Unsafe.Add(ref dst, i) = rv | ((ulong)gv << 16) | ((ulong)bv << 32) | (a.IsEmpty ? fill : (ulong)a[i] << 48);
            peak = Math.Max(peak, Math.Max(Finite(rv), Math.Max(Finite(gv), Finite(bv))));
        }

        return (ushort)peak;

        static ReadOnlySpan<ushort> Line(ExrPlanes planes, int c, int at, int n) =>
            c < 0 ? [] : MemoryMarshal.Cast<byte, ushort>(planes.Plane(c)).Slice(at, n);
    }

    /// <summary>Keeps the lanes holding positive finite halves and zeroes the rest.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> Masked(Vector128<ushort> v, Vector128<ushort> sign, Vector128<ushort> exponent) =>
        v & ~(Vector128.Equals(v & sign, sign) | Vector128.Equals(v & exponent, exponent));

    /// <summary>Returns half bits when they are a positive finite value, else 0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Finite(uint bits) => (bits & 0x8000) == 0 && (bits & 0x7C00) != 0x7C00 ? bits : 0;

    private static ushort PeakBits(ReadOnlySpan<ulong> pixels)
    {
        uint peak = 0;
        foreach (ulong p in pixels)
        {
            peak = Math.Max(peak, Math.Max(Finite((uint)(p & 0xFFFF)), Math.Max(Finite((uint)((p >> 16) & 0xFFFF)), Finite((uint)((p >> 32) & 0xFFFF)))));
        }

        return (ushort)peak;
    }

    /// <summary>Reads channel <paramref name="c"/> on line <paramref name="y"/> from <paramref name="x0"/> as floats; a subsampled channel gives its nearest stored samples.</summary>
    private static void ReadLine(ExrPlanes planes, ExrPartHeader part, int c, int y, int x0, Span<float> destination)
    {
        ExrChannelInfo channel = part.Channels[c];
        int row = Math.Clamp(FloorDiv(y - planes.FirstY[c], channel.YSampling), 0, planes.Heights[c] - 1);
        ReadOnlySpan<byte> line = PlaneRow(planes, part, c, row);
        if (channel.XSampling == 1)
        {
            ConvertSamples(channel.Type, line.Slice((x0 - planes.FirstX[c]) * channel.Bytes, destination.Length * channel.Bytes), destination);
            return;
        }

        for (int i = 0; i < destination.Length; i++)
        {
            int col = Math.Clamp(FloorDiv(x0 + i - planes.FirstX[c], channel.XSampling), 0, planes.Widths[c] - 1);
            ConvertSamples(channel.Type, line.Slice(col * channel.Bytes, channel.Bytes), destination.Slice(i, 1));
        }
    }

    private static ReadOnlySpan<byte> PlaneRow(ExrPlanes planes, ExrPartHeader part, int c, int row)
    {
        int bytes = planes.Widths[c] * part.Channels[c].Bytes;
        return planes.Plane(c).Slice(row * bytes, bytes);
    }

    /// <summary>Converts stored samples to floats: half widened, float copied, uint converted.</summary>
    private static void ConvertSamples(ExrPixelType type, ReadOnlySpan<byte> source, Span<float> destination)
    {
        switch (type)
        {
            case ExrPixelType.Half:
                ReadOnlySpan<ushort> halves = MemoryMarshal.Cast<byte, ushort>(source);
                Span<uint> bits = MemoryMarshal.Cast<float, uint>(destination);
                int done = HalfKernels.ToSingle(halves, bits, 256);
                for (int i = done; i < halves.Length; i++)
                {
                    destination[i] = (float)BitConverter.UInt16BitsToHalf(halves[i]);
                }

                break;
            case ExrPixelType.Float:
                MemoryMarshal.Cast<byte, float>(source).CopyTo(destination);
                break;
            default:
                ReadOnlySpan<uint> words = MemoryMarshal.Cast<byte, uint>(source);
                for (int i = 0; i < words.Length; i++)
                {
                    destination[i] = BitConverter.IsLittleEndian ? words[i] : BinaryPrimitives.ReverseEndianness(words[i]);
                }

                break;
        }
    }

    private static void Fill(Span<float> values, int k, float value)
    {
        for (int i = k; i < values.Length; i += 4)
        {
            values[i] = value;
        }
    }

    /// <summary>Returns the largest finite color sample; with a stride of 4 the fourth (alpha) is skipped.</summary>
    private static float Peak(ReadOnlySpan<float> values, int stride)
    {
        float peak = 0;
        for (int i = 0; i < values.Length; i++)
        {
            float v = values[i];
            if ((stride == 1 || (i & 3) != 3) && v > peak && v <= float.MaxValue)
            {
                peak = v;
            }
        }

        return peak;
    }

    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);
}
