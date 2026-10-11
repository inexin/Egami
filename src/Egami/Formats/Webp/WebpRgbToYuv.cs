using System.Runtime.InteropServices;

using Egami.Pixels;

namespace Egami.Formats.Webp;

/// <summary>
/// Converts ARGB to the VP8 encoder's padded 4:2:0 planes with 2.1.4's BT.601 limited-range kernel. Chroma is the average of
/// each 2 × 2 box; where the box is partly transparent the average is weighted by alpha, so invisible pixels do not bleed their
/// color into visible neighbours.
/// </summary>
internal static class WebpRgbToYuv
{
    internal static Vp8Planes Convert(ReadOnlySpan<uint> argb, int width, int height)
    {
        Vp8Planes planes = new(width, height);
        YuvKernels8.FromRgbCoefficients k = YuvKernels8.FromRgbCoefficients.For(WebpReader.Vp8Color);
        YuvConvert.Order order = YuvConvert.Order.Of<Bgra8>();
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(argb);
        int rowBytes = width * 4;
        for (int y = 0; y < height; y++)
        {
            YuvKernels8.FromRgb(bytes.Slice(y * rowBytes, rowBytes), order, planes.Y.AsSpan(y * planes.Stride, width), default, default, width, in k);
        }

        int chromaWidth = (width + 1) >> 1;
        for (int cy = 0; cy < (height + 1) >> 1; cy++)
        {
            int y0 = 2 * cy;
            bool twoRows = y0 + 1 < height;
            ReadOnlySpan<uint> top = argb.Slice(y0 * width, width);
            ReadOnlySpan<uint> bottom = twoRows ? argb.Slice((y0 + 1) * width, width) : top;
            Span<byte> u = planes.U.AsSpan(cy * planes.ChromaStride, chromaWidth);
            Span<byte> v = planes.V.AsSpan(cy * planes.ChromaStride, chromaWidth);
            if (Opaque(top) && Opaque(bottom))
            {
                YuvKernels8.BoxFromRgb(MemoryMarshal.AsBytes(top), MemoryMarshal.AsBytes(bottom), twoRows, order, u, v, width, in k);
            }
            else
            {
                Weighted(top, bottom, u, v, width, in k);
            }
        }

        planes.Pad();
        return planes;
    }

    private static bool Opaque(ReadOnlySpan<uint> row)
    {
        foreach (uint p in row)
        {
            if (p < 0xFF000000)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>One row of chroma boxes, each from four pixels (a lone last column or row counts twice), weighted by alpha.</summary>
    private static void Weighted(ReadOnlySpan<uint> top, ReadOnlySpan<uint> bottom, Span<byte> u, Span<byte> v, int width, in YuvKernels8.FromRgbCoefficients k)
    {
        for (int i = 0; i < u.Length; i++)
        {
            int a = 2 * i, b = Math.Min(a + 1, width - 1);
            uint p0 = top[a], p1 = top[b], p2 = bottom[a], p3 = bottom[b];
            int a0 = (int)(p0 >> 24), a1 = (int)(p1 >> 24), a2 = (int)(p2 >> 24), a3 = (int)(p3 >> 24);
            int total = a0 + a1 + a2 + a3;
            long r, g, bl;
            if (total == 0 || total == 4 * 255)
            {
                r = R(p0) + R(p1) + R(p2) + R(p3);
                g = G(p0) + G(p1) + G(p2) + G(p3);
                bl = B(p0) + B(p1) + B(p2) + B(p3);
            }
            else
            {
                // Four times the alpha-weighted mean, rounded, so the sum keeps the scale of an unweighted box.
                r = ((4L * ((a0 * R(p0)) + (a1 * R(p1)) + (a2 * R(p2)) + (a3 * R(p3)))) + (total / 2)) / total;
                g = ((4L * ((a0 * G(p0)) + (a1 * G(p1)) + (a2 * G(p2)) + (a3 * G(p3)))) + (total / 2)) / total;
                bl = ((4L * ((a0 * B(p0)) + (a1 * B(p1)) + (a2 * B(p2)) + (a3 * B(p3)))) + (total / 2)) / total;
            }

            const int Shift = 17, Half = 1 << (Shift - 1);
            u[i] = (byte)Math.Clamp((int)(((k.BR * r) + (k.BG * g) + (k.BB * bl) + Half) >> Shift) + k.COff, 0, 255);
            v[i] = (byte)Math.Clamp((int)(((k.RR * r) + (k.RG * g) + (k.RB * bl) + Half) >> Shift) + k.COff, 0, 255);
        }

        static int R(uint p) => (int)((p >> 16) & 0xFF);

        static int G(uint p) => (int)((p >> 8) & 0xFF);

        static int B(uint p) => (int)(p & 0xFF);
    }
}
