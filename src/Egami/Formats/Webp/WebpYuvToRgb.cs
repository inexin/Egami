using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

using Egami.Pixels;

namespace Egami.Formats.Webp;

/// <summary>
/// Converts VP8's 4:2:0 planes to RGB a row at a time. Chroma is first brought to full resolution as libwebp's default ("fancy")
/// upsampler does it, the 9:3:3:1 interpolation of centre-sited samples with libwebp's own rounding, so the chroma of every pixel
/// equals libwebp's; then 2.1.4's BT.601 kernel converts. With fast upsampling each chroma sample is repeated instead.
/// </summary>
/// <remarks>
/// Phase 2.3.1 found that 2.1.4's bilinear kernel, which interpolates at full precision, lands up to 2 levels from libwebp; the
/// conversion itself is within 1 level, so only the upsampling follows libwebp.
/// </remarks>
internal sealed class WebpYuvToRgb : IDisposable
{
    private readonly int _width;
    private readonly int _chromaHeight;
    private readonly bool _fast;
    private readonly short[] _cb;
    private readonly short[] _cr;
    private readonly YuvKernels8.ToRgbCoefficients _coefficients = YuvKernels8.ToRgbCoefficients.For(WebpReader.Vp8Color);
    private bool _disposed;

    internal WebpYuvToRgb(int width, int height, bool fast)
    {
        _width = width;
        _chromaHeight = (height + 1) >> 1;
        _fast = fast;
        _cb = ArrayPool<short>.Shared.Rent(width + 2);
        _cr = ArrayPool<short>.Shared.Rent(width + 2);
    }

    /// <summary>
    /// Returns the chroma rows luma row <paramref name="y"/> is made from, as libwebp pairs them: the row above and the current
    /// one, and whether <paramref name="y"/> is the upper row of its pair (nearer the row above).
    /// </summary>
    internal (int Above, int Current, bool Upper) ChromaRows(int y)
    {
        if (_fast)
        {
            return (y >> 1, y >> 1, true);
        }

        if (y == 0)
        {
            return (0, 0, true);
        }

        if ((y & 1) != 0)
        {
            return ((y - 1) >> 1, Math.Min((y + 1) >> 1, _chromaHeight - 1), true);
        }

        return ((y >> 1) - 1, y >> 1, false);
    }

    /// <summary>
    /// Converts luma row <paramref name="y"/> into <paramref name="target"/> in <paramref name="order"/>, with the chroma rows
    /// <see cref="ChromaRows"/> named and the alpha row (empty when opaque).
    /// </summary>
    internal void Convert(int y, ReadOnlySpan<byte> luma, ReadOnlySpan<byte> uAbove, ReadOnlySpan<byte> uCurrent, ReadOnlySpan<byte> vAbove, ReadOnlySpan<byte> vCurrent, ReadOnlySpan<byte> alpha, Span<byte> target, YuvConvert.Order order)
    {
        (_, _, bool upper) = ChromaRows(y);
        Span<short> cb = _cb.AsSpan(0, _width);
        Span<short> cr = _cr.AsSpan(0, _width);
        if (_fast)
        {
            Replicate(uCurrent, cb);
            Replicate(vCurrent, cr);
        }
        else
        {
            Upsample(uAbove, uCurrent, upper, cb);
            Upsample(vAbove, vCurrent, upper, cr);
        }

        YuvKernels8.ToRgb(luma[.._width], cb, cr, alpha, target, order, in _coefficients);
    }

    /// <summary>Converts whole planes into <paramref name="raster"/>: straight into Rgba8, Rgb8, Bgra8 and Bgr8, through Rgba8 otherwise.</summary>
    internal static void ToRaster<TPixel>(YuvRaster<byte> planes, Raster<TPixel> raster, bool fast, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        bool direct = typeof(TPixel) == typeof(Rgba8) || typeof(TPixel) == typeof(Rgb8) || typeof(TPixel) == typeof(Bgra8) || typeof(TPixel) == typeof(Bgr8);
        YuvConvert.Order order = direct ? YuvConvert.Order.Of<TPixel>() : YuvConvert.Order.Of<Rgba8>();
        using WebpYuvToRgb converter = new(planes.Width, planes.Height, fast);
        Rgba8[]? row = direct ? null : ArrayPool<Rgba8>.Shared.Rent(planes.Width);
        try
        {
            for (int y = 0; y < planes.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                (int above, int current, _) = converter.ChromaRows(y);
                Span<byte> target = direct ? System.Runtime.InteropServices.MemoryMarshal.AsBytes(raster.Row(y)) : System.Runtime.InteropServices.MemoryMarshal.AsBytes(row.AsSpan(0, planes.Width));
                ReadOnlySpan<byte> alpha = order.A >= 0 && planes.A is not null ? planes.A.Row(y) : default;
                converter.Convert(y, planes.Y.Row(y), planes.U!.Row(above), planes.U.Row(current), planes.V!.Row(above), planes.V.Row(current), alpha, target, order);
                if (!direct)
                {
                    PixelConvert.Convert<Rgba8, TPixel>(row.AsSpan(0, planes.Width), raster.Row(y));
                }
            }
        }
        finally
        {
            if (row is not null)
            {
                ArrayPool<Rgba8>.Shared.Return(row);
            }
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            ArrayPool<short>.Shared.Return(_cb);
            ArrayPool<short>.Shared.Return(_cr);
        }
    }

    /// <summary>Repeats each chroma sample over the two pixels it covers, at 16 times the sample scale the kernel takes.</summary>
    private static void Replicate(ReadOnlySpan<byte> chroma, Span<short> target)
    {
        for (int x = 0; x < target.Length; x++)
        {
            target[x] = (short)(chroma[x >> 1] << 4);
        }
    }

    /// <summary>
    /// One row of libwebp's fancy upsampling, at 16 times the sample scale: from the chroma row above (<paramref name="above"/>)
    /// and the current one, the upper row of a pair weighs the row above 3:1 and the lower row the current one; between samples the
    /// four nearest are blended through the two diagonals, with libwebp's rounding.
    /// </summary>
    internal static void Upsample(ReadOnlySpan<byte> above, ReadOnlySpan<byte> current, bool upper, Span<short> target)
    {
        int width = target.Length;
        int pairs = (width - 1) >> 1;
        target[0] = (short)((upper ? ((3 * above[0]) + current[0] + 2) >> 2 : ((3 * current[0]) + above[0] + 2) >> 2) << 4);
        int x = 1;
        if (Sse2.IsSupported)
        {
            x = UpsampleSse2(above, current, upper, target, pairs);
        }

        for (; x <= pairs; x++)
        {
            int tl = above[x - 1], t = above[x], l = current[x - 1], c = current[x];
            int avg = tl + t + l + c + 8;
            int diag12 = (avg + (2 * (t + l))) >> 3;
            int diag03 = (avg + (2 * (tl + c))) >> 3;
            target[(2 * x) - 1] = (short)((upper ? (diag12 + tl) >> 1 : (diag03 + l) >> 1) << 4);
            target[2 * x] = (short)((upper ? (diag03 + t) >> 1 : (diag12 + c) >> 1) << 4);
        }

        if ((width & 1) == 0)
        {
            int tl = above[pairs], l = current[pairs];
            target[width - 1] = (short)((upper ? ((3 * tl) + l + 2) >> 2 : ((3 * l) + tl + 2) >> 2) << 4);
        }
    }

    /// <summary>The pairs of <see cref="Upsample"/> eight at a time; returns the first pair left for the scalar loop.</summary>
    private static int UpsampleSse2(ReadOnlySpan<byte> above, ReadOnlySpan<byte> current, bool upper, Span<short> target, int pairs)
    {
        ref byte a = ref MemoryMarshal.GetReference(above);
        ref byte b = ref MemoryMarshal.GetReference(current);
        ref short o = ref MemoryMarshal.GetReference(target);
        Vector128<short> eight = Vector128.Create((short)8);
        int x = 1;
        for (; x + 7 <= pairs; x += 8)
        {
            Vector128<short> tl = Widen(ref Unsafe.Add(ref a, x - 1));
            Vector128<short> t = Widen(ref Unsafe.Add(ref a, x));
            Vector128<short> l = Widen(ref Unsafe.Add(ref b, x - 1));
            Vector128<short> c = Widen(ref Unsafe.Add(ref b, x));
            Vector128<short> avg = tl + t + l + c + eight;
            Vector128<short> diag12 = Vector128.ShiftRightArithmetic(avg + ((t + l) << 1), 3);
            Vector128<short> diag03 = Vector128.ShiftRightArithmetic(avg + ((tl + c) << 1), 3);
            Vector128<short> odd = upper ? Vector128.ShiftRightArithmetic(diag12 + tl, 1) : Vector128.ShiftRightArithmetic(diag03 + l, 1);
            Vector128<short> even = upper ? Vector128.ShiftRightArithmetic(diag03 + t, 1) : Vector128.ShiftRightArithmetic(diag12 + c, 1);
            ref short at = ref Unsafe.Add(ref o, (2 * x) - 1);
            (Sse2.UnpackLow(odd, even) << 4).StoreUnsafe(ref at);
            (Sse2.UnpackHigh(odd, even) << 4).StoreUnsafe(ref Unsafe.Add(ref at, 8));
        }

        return x;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Widen(ref byte source) => Vector128.WidenLower(Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref source)).AsByte()).AsInt16();
}
