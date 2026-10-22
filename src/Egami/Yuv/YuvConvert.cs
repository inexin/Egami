using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Egami.Metadata;
using Egami.Operations;
using Egami.Pixels;

namespace Egami;

/// <summary>
/// Conversion between a <see cref="YuvRaster{TSample}"/> and a <see cref="Raster{TPixel}"/>, row by row, in bands across threads.
/// </summary>
/// <remarks>
/// <para>
/// To RGB, each output row first gets its chroma upsampled to luma width at 16 times the sample scale, so the bilinear weights
/// (quarters both ways at most) stay exact integers; then the matrix runs per pixel. From RGB, the matrix runs at full resolution
/// without rounding chroma, the downsampling filter sums with integer weights, and chroma is rounded once at the end.
/// </para>
/// <para>
/// 8-bit pictures to and from the four 8-bit RGB orders use fixed point (<see cref="YuvKernels8"/>); everything else uses single
/// floats (<see cref="YuvKernelsFloat"/>). In both, the SIMD kernels produce exactly the scalar path's samples.
/// </para>
/// </remarks>
internal static class YuvConvert
{
    /// <summary>Converts <paramref name="source"/> into <paramref name="target"/>, which has the same size.</summary>
    internal static void ToRgb<TSample, TPixel>(YuvRaster<TSample> source, Raster<TPixel> target, YuvConvertOptions options, CancellationToken cancellationToken)
        where TSample : unmanaged
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (typeof(TPixel) == typeof(Rgba8) || typeof(TPixel) == typeof(Bgra8) || typeof(TPixel) == typeof(Rgb8) || typeof(TPixel) == typeof(Bgr8)
            || typeof(TPixel) == typeof(Rgba16))
        {
            ToRgbDirect<TSample, TPixel>(source, target, options, cancellationToken);
        }
        else if (TPixel.Layout.BitsPerChannel <= 8)
        {
            ToRgbVia<TSample, TPixel, Rgba8>(source, target, options, cancellationToken);
        }
        else
        {
            ToRgbVia<TSample, TPixel, Rgba16>(source, target, options, cancellationToken);
        }
    }

    /// <summary>Converts <paramref name="source"/> into <paramref name="target"/>, which has the same size.</summary>
    internal static void FromRgb<TPixel, TSample>(Raster<TPixel> source, YuvRaster<TSample> target, YuvConvertOptions options, CancellationToken cancellationToken)
        where TSample : unmanaged
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (typeof(TPixel) == typeof(Rgba8) || typeof(TPixel) == typeof(Bgra8) || typeof(TPixel) == typeof(Rgb8) || typeof(TPixel) == typeof(Bgr8)
            || typeof(TPixel) == typeof(Rgba16))
        {
            FromRgbDirect<TPixel, TPixel, TSample>(source, target, options, cancellationToken);
        }
        else if (TPixel.Layout.BitsPerChannel <= 8)
        {
            FromRgbDirect<TPixel, Rgba8, TSample>(source, target, options, cancellationToken);
        }
        else
        {
            FromRgbDirect<TPixel, Rgba16, TSample>(source, target, options, cancellationToken);
        }
    }

    /// <summary>
    /// Updates a converted raster's metadata to say what is known: the samples are now RGB at full range. Transfer and primaries
    /// are unchanged, since conversion does not touch them.
    /// </summary>
    internal static void DescribeRgb(RasterMetadata metadata, YuvRaster source)
    {
        if (metadata.CodePoints is { } codes)
        {
            metadata.CodePoints = codes with { MatrixCoefficients = 0, FullRange = true };
        }
    }

    /// <summary>The byte or 16-bit word offsets of each channel in one of the direct pixel types, and its size; A is -1 without alpha.</summary>
    internal readonly record struct Order(int R, int G, int B, int A, int Size)
    {
        internal static Order Of<TPixel>() =>
            typeof(TPixel) == typeof(Rgba8) || typeof(TPixel) == typeof(Rgba16) ? new(0, 1, 2, 3, 4)
            : typeof(TPixel) == typeof(Bgra8) ? new(2, 1, 0, 3, 4)
            : typeof(TPixel) == typeof(Rgb8) ? new(0, 1, 2, -1, 3)
            : typeof(TPixel) == typeof(Bgr8) ? new(2, 1, 0, -1, 3)
            : throw new InvalidOperationException($"{typeof(TPixel).Name} is not a direct YUV target.");
    }

    private static void ToRgbDirect<TSample, TPixel>(YuvRaster<TSample> source, Raster<TPixel> target, YuvConvertOptions options, CancellationToken cancellationToken)
        where TSample : unmanaged
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Order order = Order.Of<TPixel>();
        int outBits = typeof(TPixel) == typeof(Rgba16) ? 16 : 8;
        long rowBytes = (long)source.Width * Unsafe.SizeOf<TPixel>();
        if (RowRunner.IsSequential(source.Height, rowBytes, source.Context.MaxParallelism))
        {
            // One thread: reuse the raster's kept buffers and make no delegate, so a repeated conversion allocates nothing.
            ToRgbRows<TSample> rows = source.TakeState() is ToRgbRows<TSample> kept && kept.Matches(options, order, outBits) ? kept : new(source, options, order, outBits);
            try
            {
                int band = RowRunner.BandRows(rowBytes);
                for (int y = 0; y < source.Height; y++)
                {
                    if (y % band == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    rows.Convert(y, MemoryMarshal.AsBytes(target.Row(y)));
                }
            }
            finally
            {
                source.KeepState(rows);
            }

            return;
        }

        ToRgbParallel(source, target, options, order, outBits, rowBytes, cancellationToken);
    }

    /// <summary>The parallel half of <see cref="ToRgbDirect"/>, apart so the sequential half creates no closure.</summary>
    private static void ToRgbParallel<TSample, TPixel>(YuvRaster<TSample> source, Raster<TPixel> target, YuvConvertOptions options, Order order, int outBits, long rowBytes, CancellationToken cancellationToken)
        where TSample : unmanaged
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        RowRunner.Run(source.Height, rowBytes, source.Context.MaxParallelism, (start, end) =>
        {
            using ToRgbRows<TSample> rows = new(source, options, order, outBits);
            for (int y = start; y < end; y++)
            {
                rows.Convert(y, MemoryMarshal.AsBytes(target.Row(y)));
            }
        }, cancellationToken);
    }

    private static void ToRgbVia<TSample, TPixel, TVia>(YuvRaster<TSample> source, Raster<TPixel> target, YuvConvertOptions options, CancellationToken cancellationToken)
        where TSample : unmanaged
        where TPixel : unmanaged, IPixelType<TPixel>
        where TVia : unmanaged, IPixelType<TVia>
    {
        Order order = Order.Of<TVia>();
        int outBits = typeof(TVia) == typeof(Rgba16) ? 16 : 8;
        long rowBytes = (long)source.Width * Unsafe.SizeOf<TPixel>();
        if (RowRunner.IsSequential(source.Height, rowBytes, source.Context.MaxParallelism))
        {
            ToRgbRows<TSample> rows = source.TakeState() is ToRgbRows<TSample> kept && kept.Matches(options, order, outBits) ? kept : new(source, options, order, outBits);
            try
            {
                ViaRows<TSample, TPixel, TVia>(rows, source.Width, target, 0, source.Height, RowRunner.BandRows(rowBytes), cancellationToken);
            }
            finally
            {
                source.KeepState(rows);
            }

            return;
        }

        ToRgbViaParallel<TSample, TPixel, TVia>(source, target, options, order, outBits, rowBytes, cancellationToken);
    }

    private static void ToRgbViaParallel<TSample, TPixel, TVia>(YuvRaster<TSample> source, Raster<TPixel> target, YuvConvertOptions options, Order order, int outBits, long rowBytes, CancellationToken cancellationToken)
        where TSample : unmanaged
        where TPixel : unmanaged, IPixelType<TPixel>
        where TVia : unmanaged, IPixelType<TVia>
    {
        RowRunner.Run(source.Height, rowBytes, source.Context.MaxParallelism, (start, end) =>
        {
            using ToRgbRows<TSample> rows = new(source, options, order, outBits);
            ViaRows<TSample, TPixel, TVia>(rows, source.Width, target, start, end, int.MaxValue, cancellationToken);
        }, cancellationToken);
    }

    private static void ViaRows<TSample, TPixel, TVia>(ToRgbRows<TSample> rows, int width, Raster<TPixel> target, int start, int end, int band, CancellationToken cancellationToken)
        where TSample : unmanaged
        where TPixel : unmanaged, IPixelType<TPixel>
        where TVia : unmanaged, IPixelType<TVia>
    {
        TVia[] via = ArrayPool<TVia>.Shared.Rent(width);
        try
        {
            Span<TVia> row = via.AsSpan(0, width);
            for (int y = start; y < end; y++)
            {
                if ((y - start) % band == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                rows.Convert(y, MemoryMarshal.AsBytes(row));
                PixelConvert.Convert<TVia, TPixel>(row, target.Row(y));
            }
        }
        finally
        {
            ArrayPool<TVia>.Shared.Return(via);
        }
    }

    private static void FromRgbDirect<TPixel, TVia, TSample>(Raster<TPixel> source, YuvRaster<TSample> target, YuvConvertOptions options, CancellationToken cancellationToken)
        where TSample : unmanaged
        where TPixel : unmanaged, IPixelType<TPixel>
        where TVia : unmanaged, IPixelType<TVia>
    {
        int groupRows = target.ChromaFormat switch { ChromaFormat.Yuv420 => 2, ChromaFormat.Yuv410 => 4, _ => 1 };
        int groups = (target.Height + groupRows - 1) / groupRows;
        long groupBytes = (long)groupRows * target.Width * Unsafe.SizeOf<TPixel>();
        if (RowRunner.IsSequential(groups, groupBytes, target.Context.MaxParallelism))
        {
            FromRgbRows<TPixel, TVia, TSample> rows = target.TakeState() is FromRgbRows<TPixel, TVia, TSample> kept && kept.Matches(options) ? kept : new(target, options);
            try
            {
                int band = RowRunner.BandRows(groupBytes);
                for (int g = 0; g < groups; g++)
                {
                    if (g % band == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    rows.Convert(source, g);
                }
            }
            finally
            {
                target.KeepState(rows);
            }

            return;
        }

        FromRgbParallel<TPixel, TVia, TSample>(source, target, options, groups, groupBytes, cancellationToken);
    }

    private static void FromRgbParallel<TPixel, TVia, TSample>(Raster<TPixel> source, YuvRaster<TSample> target, YuvConvertOptions options, int groups, long groupBytes, CancellationToken cancellationToken)
        where TSample : unmanaged
        where TPixel : unmanaged, IPixelType<TPixel>
        where TVia : unmanaged, IPixelType<TVia>
    {
        RowRunner.Run(groups, groupBytes, target.Context.MaxParallelism, (start, end) =>
        {
            using FromRgbRows<TPixel, TVia, TSample> rows = new(target, options);
            for (int g = start; g < end; g++)
            {
                rows.Convert(source, g);
            }
        }, cancellationToken);
    }

    /// <summary>Rounds a value to the nearest integer, halves up, as every Egami conversion rounds.</summary>
    internal static int RoundHalfUp(double value) => (int)Math.Floor(value + 0.5);

    /// <summary>Reads sample <paramref name="i"/> as an integer, clamped to <paramref name="max"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Sample<TSample>(ReadOnlySpan<TSample> row, int i, int max)
        where TSample : unmanaged
    {
        if (typeof(TSample) == typeof(byte))
        {
            return Unsafe.As<TSample, byte>(ref Unsafe.AsRef(in row[i]));
        }

        return Math.Min(Unsafe.As<TSample, ushort>(ref Unsafe.AsRef(in row[i])), max);
    }

    /// <summary>Writes an integer sample, already in range.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetSample<TSample>(Span<TSample> row, int i, int value)
        where TSample : unmanaged
    {
        if (typeof(TSample) == typeof(byte))
        {
            Unsafe.As<TSample, byte>(ref row[i]) = (byte)value;
        }
        else
        {
            Unsafe.As<TSample, ushort>(ref row[i]) = (ushort)value;
        }
    }

    /// <summary>
    /// Upsamples chroma to luma width at 16 times the sample scale, for each output row of a <see cref="YuvRaster"/>. Weights are
    /// the siting's bilinear weights in quarters each way, or the nearest sample.
    /// </summary>
    internal static class Upsample
    {
        /// <summary>
        /// Returns the chroma rows and their weights (summing to 4) for luma row <paramref name="y"/>: vertical interpolation for
        /// 4:2:0, the row itself otherwise.
        /// </summary>
        internal static (int Row0, int Weight0, int Row1, int Weight1) Vertical(ChromaFormat format, ChromaSiting siting, ChromaUpsampling mode, int y, int chromaHeight)
        {
            if (format != ChromaFormat.Yuv420)
            {
                return (y, 4, y, 0);
            }

            int j = y >> 1;
            if (mode == ChromaUpsampling.Nearest)
            {
                return (j, 4, j, 0);
            }

            if (siting == ChromaSiting.TopLeft)
            {
                // Co-sited rows: even luma rows sit on a chroma row, odd ones halfway to the next.
                return (y & 1) == 0 ? (j, 4, j, 0) : (j, 2, Math.Min(j + 1, chromaHeight - 1), 2);
            }

            // Centered between luma rows 2j and 2j + 1: 3/4 of the nearer chroma row, 1/4 of the farther.
            return (y & 1) == 0 ? (j, 3, Math.Max(j - 1, 0), 1) : (j, 3, Math.Min(j + 1, chromaHeight - 1), 1);
        }

        /// <summary>
        /// Upsamples one row of vertically weighted chroma (4 times the sample scale), <paramref name="chromaWidth"/> long, to
        /// <paramref name="target"/> (16 times the sample scale), luma width long.
        /// </summary>
        internal static void Horizontal(ReadOnlySpan<int> t, int chromaWidth, Span<int> target, ChromaFormat format, ChromaSiting siting, ChromaUpsampling mode)
        {
            int width = target.Length;
            if (format == ChromaFormat.Yuv444)
            {
                for (int x = 0; x < width; x++)
                {
                    target[x] = t[x] * 4;
                }

                return;
            }

            // The SIMD kernel does the chroma samples with both neighbors; the rule below does the first and the rest.
            int done = YuvResample.Horizontal(t[..chromaWidth], target, siting, mode);
            Scalar(t, chromaWidth, target, siting, mode, 0, Math.Min(2, width));
            Scalar(t, chromaWidth, target, siting, mode, Math.Min(2 * done, width), width);

            static void Scalar(ReadOnlySpan<int> t, int chromaWidth, Span<int> target, ChromaSiting siting, ChromaUpsampling mode, int from, int to)
            {
                int last = chromaWidth - 1;
                for (int x = from; x < to; x++)
                {
                    int i = x >> 1;
                    if (mode == ChromaUpsampling.Nearest)
                    {
                        target[x] = t[i] * 4;
                    }
                    else if (siting == ChromaSiting.Center)
                    {
                        int other = (x & 1) == 0 ? Math.Max(i - 1, 0) : Math.Min(i + 1, last);
                        target[x] = (3 * t[i]) + t[other];
                    }
                    else
                    {
                        target[x] = (x & 1) == 0 ? 4 * t[i] : (2 * t[i]) + (2 * t[Math.Min(i + 1, last)]);
                    }
                }
            }
        }

        /// <summary>
        /// Upsamples 4:1:1 or 4:1:0 chroma for luma row <paramref name="y"/> to <paramref name="target"/> at 64 times the sample
        /// scale, exact, or 16 times rounded once when <paramref name="shift"/> is 2. Weights are the siting's bilinear weights in
        /// eighths each way, or the nearest sample. <paramref name="t"/> holds one row of vertically weighted chroma.
        /// </summary>
        internal static void Quarter<TSample>(YuvPlane<TSample> plane, int y, Span<int> t, Span<int> target, ChromaFormat format, ChromaSiting siting, ChromaUpsampling mode, int max, int shift)
            where TSample : unmanaged
        {
            int half = (1 << shift) >> 1;
            // Vertical: the row itself for 4:1:1; for 4:1:0 co-sited on luma row 4j for TopLeft, else centered at 4j + 1.5.
            (int r0, int w0, int r1, int w1) = format == ChromaFormat.Yuv411 ? (y, 8, y, 0)
                : mode == ChromaUpsampling.Nearest ? (y >> 2, 8, y >> 2, 0)
                : Taps(siting == ChromaSiting.TopLeft ? 2 * y : (2 * y) - 3, plane.Height);
            ReadOnlySpan<TSample> a = plane.Row(r0), b = plane.Row(r1);
            for (int i = 0; i < t.Length; i++)
            {
                t[i] = (w0 * Sample(a, i, max)) + (w1 * Sample(b, i, max));
            }

            for (int x = 0; x < target.Length; x++)
            {
                if (mode == ChromaUpsampling.Nearest)
                {
                    target[x] = (t[x >> 2] * 8) >> shift;
                    continue;
                }

                // Horizontal: co-sited on luma column 4i for Left and TopLeft, centered at 4i + 1.5 for Center.
                (int c0, int k0, int c1, int k1) = Taps(siting == ChromaSiting.Center ? (2 * x) - 3 : 2 * x, t.Length);
                target[x] = ((k0 * t[c0]) + (k1 * t[c1]) + half) >> shift;
            }

            // Position p in eighths of a chroma sample: the two samples around it, clamped to the plane, and their weights.
            static (int, int, int, int) Taps(int p, int n)
            {
                int i = p >> 3, f = p & 7;
                return (Math.Clamp(i, 0, n - 1), 8 - f, Math.Clamp(i + 1, 0, n - 1), f);
            }
        }
    }

    /// <summary>The per-band state of a conversion to RGB: buffers and coefficients, reused for every row of the band.</summary>
    private sealed class ToRgbRows<TSample> : IDisposable
        where TSample : unmanaged
    {
        private readonly YuvRaster<TSample> _source;
        private readonly YuvConvertOptions _options;
        private readonly Order _order;
        private readonly int _outBits;
        private readonly int[] _t;
        private readonly int[] _cb;
        private readonly int[] _cr;
        private readonly short[] _t16;
        private readonly short[] _cb16;
        private readonly short[] _cr16;
        private readonly int _chromaWidth;
        private readonly int _chromaHeight;
        private readonly bool _quarter;
        private readonly int[] _quarterRow;
        private readonly YuvKernels8.ToRgbCoefficients _fixed;
        private readonly YuvKernelsFloat.ToRgbCoefficients _float;
        private readonly bool _useFixed;

        internal ToRgbRows(YuvRaster<TSample> source, YuvConvertOptions options, Order order, int outBits)
        {
            _source = source;
            _options = options;
            _order = order;
            _outBits = outBits;
            (_chromaWidth, _chromaHeight) = YuvPlaneSize.ChromaSize(source.ChromaFormat, source.Width, source.Height);
            _useFixed = source.BitDepth == 8 && outBits == 8;
            _quarter = source.ChromaFormat is ChromaFormat.Yuv411 or ChromaFormat.Yuv410;
            _quarterRow = _quarter && _useFixed ? ArrayPool<int>.Shared.Rent(source.Width) : [];
            if (_useFixed)
            {
                // 8-bit chroma at 16 times the scale fits 16-bit lanes; one spare element each side for the SIMD upsampler.
                _fixed = YuvKernels8.ToRgbCoefficients.For(source.Color);
                _t16 = ArrayPool<short>.Shared.Rent(_chromaWidth + 2);
                _t = _quarter ? ArrayPool<int>.Shared.Rent(_chromaWidth) : [];
                _cb16 = ArrayPool<short>.Shared.Rent(source.Width + 2);
                _cr16 = ArrayPool<short>.Shared.Rent(source.Width + 2);
                _cb = _cr = [];
            }
            else
            {
                _float = YuvKernelsFloat.ToRgbCoefficients.For(source.Color, source.BitDepth, (1 << outBits) - 1);
                if (_quarter)
                {
                    // 4:1:1 and 4:1:0 chroma comes at 64 times the sample scale, so the eighths of both ways stay exact.
                    _float = _float with { COff = _float.COff * 4, CScale = _float.CScale / 4 };
                }

                _t = ArrayPool<int>.Shared.Rent(Math.Max(_chromaWidth, 1));
                _cb = ArrayPool<int>.Shared.Rent(source.Width);
                _cr = ArrayPool<int>.Shared.Rent(source.Width);
                _t16 = _cb16 = _cr16 = [];
            }
        }

        /// <summary>Returns whether this state converts with these settings, so it can be reused.</summary>
        internal bool Matches(YuvConvertOptions options, Order order, int outBits) => _options == options && _order == order && _outBits == outBits;

        /// <summary>Converts luma row <paramref name="y"/> into one row of the target, as bytes.</summary>
        internal void Convert(int y, Span<byte> target)
        {
            YuvRaster<TSample> s = _source;
            int width = s.Width;
            int max = s.MaxSample;
            ReadOnlySpan<TSample> luma = s.Y.Row(y);
            ReadOnlySpan<TSample> alpha = s.A is null ? default : s.A.Row(y);
            if (_useFixed)
            {
                ReadOnlySpan<byte> luma8 = MemoryMarshal.Cast<TSample, byte>(luma);
                Span<short> cb16 = _cb16.AsSpan(0, width), cr16 = _cr16.AsSpan(0, width);
                if (s.ChromaFormat == ChromaFormat.Yuv400)
                {
                    if (s.Color.Matrix == YuvMatrix.Identity)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            cb16[x] = cr16[x] = (short)(luma8[x] * 16);
                        }
                    }
                    else
                    {
                        cb16.Fill(2048);
                        cr16.Fill(2048);
                    }
                }
                else
                {
                    ChromaRow8(s.U!, y, cb16);
                    ChromaRow8(s.V!, y, cr16);
                }

                YuvKernels8.ToRgb(luma8, cb16, cr16, MemoryMarshal.Cast<TSample, byte>(alpha), target, _order, in _fixed);
                return;
            }

            Span<int> cb = _cb.AsSpan(0, width), cr = _cr.AsSpan(0, width);
            if (s.ChromaFormat == ChromaFormat.Yuv400)
            {
                // No chroma: neutral, or for the identity matrix (G, B, R stored as Y, U, V) the luma itself, so gray stays gray.
                int neutral = 16 << (s.BitDepth - 1);
                for (int x = 0; x < width; x++)
                {
                    cb[x] = cr[x] = s.Color.Matrix == YuvMatrix.Identity ? Sample(luma, x, max) * 16 : neutral;
                }
            }
            else
            {
                ChromaRow(s.U!, y, cb, max);
                ChromaRow(s.V!, y, cr, max);
            }

            if (_outBits == 8)
            {
                YuvKernelsFloat.ToRgb(luma, cb, cr, alpha, max, target, _order, in _float);
            }
            else
            {
                YuvKernelsFloat.ToRgb(luma, cb, cr, alpha, max, MemoryMarshal.Cast<byte, ushort>(target), _order, in _float);
            }
        }

        public void Dispose()
        {
            if (_useFixed)
            {
                ArrayPool<short>.Shared.Return(_t16);
                ArrayPool<short>.Shared.Return(_cb16);
                ArrayPool<short>.Shared.Return(_cr16);
                if (_quarter)
                {
                    ArrayPool<int>.Shared.Return(_t);
                    ArrayPool<int>.Shared.Return(_quarterRow);
                }
            }
            else
            {
                ArrayPool<int>.Shared.Return(_t);
                ArrayPool<int>.Shared.Return(_cb);
                ArrayPool<int>.Shared.Return(_cr);
            }
        }

        private void ChromaRow8(YuvPlane<TSample> plane, int y, Span<short> target)
        {
            YuvRaster<TSample> s = _source;
            if (_quarter)
            {
                Span<int> row = _quarterRow.AsSpan(0, target.Length);
                Upsample.Quarter(plane, y, _t.AsSpan(0, _chromaWidth), row, s.ChromaFormat, s.Color.Siting, _options.Upsampling, 255, shift: 2);
                for (int x = 0; x < row.Length; x++)
                {
                    target[x] = (short)row[x];
                }

                return;
            }

            (int r0, int w0, int r1, int w1) = Upsample.Vertical(s.ChromaFormat, s.Color.Siting, _options.Upsampling, y, _chromaHeight);
            Span<short> t = _t16.AsSpan(0, _chromaWidth);
            ReadOnlySpan<byte> a = MemoryMarshal.Cast<TSample, byte>(plane.Row(r0));
            ReadOnlySpan<byte> b = w1 == 0 ? a : MemoryMarshal.Cast<TSample, byte>(plane.Row(r1));
            YuvKernels8.UpsampleVertical(a, b, w0, w1, t);
            YuvKernels8.UpsampleHorizontal(t, target, s.ChromaFormat, s.Color.Siting, _options.Upsampling);
        }

        private void ChromaRow(YuvPlane<TSample> plane, int y, Span<int> target, int max)
        {
            YuvRaster<TSample> s = _source;
            if (_quarter)
            {
                Upsample.Quarter(plane, y, _t.AsSpan(0, _chromaWidth), target, s.ChromaFormat, s.Color.Siting, _options.Upsampling, max, shift: 0);
                return;
            }

            (int r0, int w0, int r1, int w1) = Upsample.Vertical(s.ChromaFormat, s.Color.Siting, _options.Upsampling, y, _chromaHeight);
            Span<int> t = _t.AsSpan(0, _chromaWidth);
            ReadOnlySpan<TSample> a = plane.Row(r0);
            ReadOnlySpan<TSample> b = w1 == 0 ? a : plane.Row(r1);
            for (int i = YuvResample.Vertical(a, b, w0, w1, max, t); i < t.Length; i++)
            {
                t[i] = (w0 * Sample(a, i, max)) + (w1 * Sample(b, i, max));
            }

            Upsample.Horizontal(t, _chromaWidth, target, s.ChromaFormat, s.Color.Siting, _options.Upsampling);
        }
    }

    /// <summary>
    /// The per-band state of a conversion from RGB. Group <c>g</c> is chroma row <c>g</c> with the luma rows it covers: two for
    /// 4:2:0, four for 4:1:0, one otherwise.
    /// </summary>
    private sealed class FromRgbRows<TPixel, TVia, TSample> : IDisposable
        where TPixel : unmanaged, IPixelType<TPixel>
        where TVia : unmanaged, IPixelType<TVia>
        where TSample : unmanaged
    {
        private readonly YuvRaster<TSample> _target;
        private readonly YuvConvertOptions _options;
        private readonly Order _order;
        private readonly bool _useFixed;
        private readonly YuvKernels8.FromRgbCoefficients _fixed;
        private readonly YuvKernelsFloat.FromRgbCoefficients _float;
        private readonly TVia[]? _via;
        private readonly int[][] _fixedRows;
        private readonly float[][] _floatRows;
        private readonly int[] _fixedSum;
        private readonly float[] _floatSum;
        private readonly int _chromaWidth;
        private readonly int _inMax;
        private readonly bool _boxFromRgb;
        private readonly long[] _quarterSum;

        internal FromRgbRows(YuvRaster<TSample> target, YuvConvertOptions options)
        {
            _target = target;
            _options = options;
            _order = Order.Of<TVia>();
            bool wordSource = typeof(TVia) == typeof(Rgba16);
            _inMax = wordSource ? 65535 : 255;
            _useFixed = target.BitDepth == 8 && !wordSource;
            (_chromaWidth, _) = YuvPlaneSize.ChromaSize(target.ChromaFormat, target.Width, target.Height);
            int width = target.Width;
            bool quarter = target.ChromaFormat is ChromaFormat.Yuv411 or ChromaFormat.Yuv410;
            _quarterSum = quarter && _useFixed ? ArrayPool<long>.Shared.Rent(2 * _chromaWidth) : [];
            if (typeof(TVia) != typeof(TPixel))
            {
                _via = ArrayPool<TVia>.Shared.Rent(width);
            }

            // Up to three source rows of full-resolution chroma (cb, cr each), and the horizontally filtered sums.
            if (_useFixed)
            {
                _fixed = YuvKernels8.FromRgbCoefficients.For(target.Color);
                _boxFromRgb = _via is null && _order.Size == 4 && target.Color.Matrix != YuvMatrix.Identity;
                _fixedRows = [.. Enumerable.Range(0, 6).Select(_ => ArrayPool<int>.Shared.Rent(width))];
                _fixedSum = ArrayPool<int>.Shared.Rent(Math.Max(2 * _chromaWidth, 1));
                _floatRows = [];
                _floatSum = [];
            }
            else
            {
                _float = YuvKernelsFloat.FromRgbCoefficients.For(target.Color, target.BitDepth, _inMax);
                _floatRows = [.. Enumerable.Range(0, 6).Select(_ => ArrayPool<float>.Shared.Rent(width))];
                _floatSum = ArrayPool<float>.Shared.Rent(Math.Max(2 * _chromaWidth, 1));
                _fixedRows = [];
                _fixedSum = [];
            }
        }

        /// <summary>Returns whether this state converts with these settings, so it can be reused.</summary>
        internal bool Matches(YuvConvertOptions options) => _options == options;

        internal void Convert(Raster<TPixel> source, int group)
        {
            YuvRaster<TSample> t = _target;
            int height = t.Height;
            ChromaFormat format = t.ChromaFormat;
            bool fast = _options.Downsampling == ChromaDownsampling.Fast;
            ChromaSiting siting = t.Color.Siting;
            if (format is ChromaFormat.Yuv411 or ChromaFormat.Yuv410)
            {
                ConvertQuarter(source, group, format, siting, fast);
                return;
            }

            int first = format == ChromaFormat.Yuv420 ? 2 * group : group;

            // The source rows that feed this chroma row, with their vertical weights.
            Span<int> rows = stackalloc int[3];
            Span<int> weights = stackalloc int[3];
            int count;
            if (format != ChromaFormat.Yuv420 || fast)
            {
                (rows[0], weights[0], count) = (first, 1, 1);
            }
            else if (siting == ChromaSiting.TopLeft)
            {
                (rows[0], weights[0]) = (Math.Max(first - 1, 0), 1);
                (rows[1], weights[1]) = (first, 2);
                (rows[2], weights[2]) = (Math.Min(first + 1, height - 1), 1);
                count = 3;
            }
            else
            {
                (rows[0], weights[0]) = (first, 1);
                (rows[1], weights[1]) = (Math.Min(first + 1, height - 1), 1);
                count = 2;
            }

            int verticalWeight = 0;
            for (int i = 0; i < count; i++)
            {
                verticalWeight += weights[i];
            }

            if (_boxFromRgb && !fast && siting == ChromaSiting.Center && format is ChromaFormat.Yuv420 or ChromaFormat.Yuv422)
            {
                // The common case of centered chroma from four-byte pixels: luma per row, then chroma once per box of RGB sums,
                // which in exact integer arithmetic gives the same samples as averaging chroma.
                int owned = format == ChromaFormat.Yuv420 ? Math.Min(2, height - first) : 1;
                for (int y = first; y < first + owned; y++)
                {
                    ConvertRow(source, y, owned: true, slot: -1);
                }

                ReadOnlySpan<byte> top = MemoryMarshal.AsBytes(source.Row(first));
                ReadOnlySpan<byte> bottom = format == ChromaFormat.Yuv420 ? MemoryMarshal.AsBytes(source.Row(Math.Min(first + 1, height - 1))) : top;
                int chromaAt = format == ChromaFormat.Yuv420 ? group : first;
                YuvKernels8.BoxFromRgb(
                    top, bottom, format == ChromaFormat.Yuv420, _order, MemoryMarshal.Cast<TSample, byte>(t.U!.Row(chromaAt)), MemoryMarshal.Cast<TSample, byte>(t.V!.Row(chromaAt)), t.Width, in _fixed);
                return;
            }

            // Chroma for every row that feeds this chroma row, and luma (and alpha) for the rows this group owns. Row first - 1
            // for TopLeft belongs to the previous group, so only its chroma is computed here.
            int own = format == ChromaFormat.Yuv420 ? Math.Min(2, height - first) : 1;
            bool chroma = format != ChromaFormat.Yuv400;
            for (int i = 0; i < count; i++)
            {
                int y = rows[i];
                ConvertRow(source, y, y >= first && y < first + own, chroma ? i : -1);
            }

            for (int y = first; y < first + own; y++)
            {
                if (!rows[..count].Contains(y))
                {
                    ConvertRow(source, y, owned: true, slot: -1);
                }
            }

            if (format == ChromaFormat.Yuv400)
            {
                return;
            }

            int chromaRow = format == ChromaFormat.Yuv420 ? group : first;
            Span<TSample> u = t.U!.Row(chromaRow), v = t.V!.Row(chromaRow);
            if (_useFixed)
            {
                YuvKernels8.Downsample(_fixedRows, weights[..count], verticalWeight, _fixedSum, u, v, t.Width, format, siting, fast, in _fixed);
            }
            else
            {
                YuvKernelsFloat.Downsample(_floatRows, weights[..count], verticalWeight, _floatSum, u, v, t.Width, format, siting, fast, t.MaxSample, in _float);
            }
        }

        public void Dispose()
        {
            if (_via is not null)
            {
                ArrayPool<TVia>.Shared.Return(_via);
            }

            foreach (int[] row in _fixedRows)
            {
                ArrayPool<int>.Shared.Return(row);
            }

            foreach (float[] row in _floatRows)
            {
                ArrayPool<float>.Shared.Return(row);
            }

            if (_fixedSum.Length > 0)
            {
                ArrayPool<int>.Shared.Return(_fixedSum);
            }

            if (_floatSum.Length > 0)
            {
                ArrayPool<float>.Shared.Return(_floatSum);
            }

            if (_quarterSum.Length > 0)
            {
                ArrayPool<long>.Shared.Return(_quarterSum);
            }
        }

        /// <summary>
        /// Converts one group of 4:1:1 (one luma row) or 4:1:0 (four luma rows). Chroma is filtered over each chroma sample's
        /// columns (and for 4:1:0 rows): a box of four when centered, a 1-2-3-4-3-2-1 triangle when co-sited, the first sample when
        /// fast; the source rows are added one at a time, so any number fit the one slot.
        /// </summary>
        private void ConvertQuarter(Raster<TPixel> source, int group, ChromaFormat format, ChromaSiting siting, bool fast)
        {
            YuvRaster<TSample> t = _target;
            int height = t.Height, width = t.Width, cw = _chromaWidth;
            bool tall = format == ChromaFormat.Yuv410;
            int first = tall ? 4 * group : group;
            int own = tall ? Math.Min(4, height - first) : 1;
            bool rowTriangle = tall && !fast && siting == ChromaSiting.TopLeft;
            int from = !tall || fast ? first : rowTriangle ? first - 3 : first;
            int to = !tall || fast ? first : first + 3;
            bool columnTriangle = !fast && siting != ChromaSiting.Center;
            int weight = (fast ? 1 : columnTriangle ? 16 : 4) * (!tall || fast ? 1 : rowTriangle ? 16 : 4);
            Span<long> fixedSum = _useFixed ? _quarterSum.AsSpan(0, 2 * cw) : default;
            Span<float> floatSum = _useFixed ? default : _floatSum.AsSpan(0, 2 * cw);
            fixedSum.Clear();
            floatSum.Clear();
            for (int r = from; r <= to; r++)
            {
                int y = Math.Clamp(r, 0, height - 1);
                int rowWeight = rowTriangle ? 4 - Math.Abs(r - first) : 1;
                ConvertRow(source, y, y >= first && y < first + own, slot: 0);
                for (int i = 0; i < cw; i++)
                {
                    int x = 4 * i;
                    if (_useFixed)
                    {
                        fixedSum[i] += rowWeight * Filter(_fixedRows[0].AsSpan(0, width), x, fast, columnTriangle);
                        fixedSum[cw + i] += rowWeight * Filter(_fixedRows[1].AsSpan(0, width), x, fast, columnTriangle);
                    }
                    else
                    {
                        floatSum[i] += rowWeight * FilterFloat(_floatRows[0].AsSpan(0, width), x, fast, columnTriangle);
                        floatSum[cw + i] += rowWeight * FilterFloat(_floatRows[1].AsSpan(0, width), x, fast, columnTriangle);
                    }
                }
            }

            // Fast mode reads one row: the group's other rows still need luma.
            for (int y = Math.Max(to + 1, first); y < first + own; y++)
            {
                ConvertRow(source, y, owned: true, slot: -1);
            }

            int chromaRow = group;
            Span<TSample> u = t.U!.Row(chromaRow), v = t.V!.Row(chromaRow);
            if (_useFixed)
            {
                int shift = 15 + int.Log2(weight);
                long half = 1L << (shift - 1);
                for (int i = 0; i < cw; i++)
                {
                    SetSample(u, i, (int)Math.Clamp(((fixedSum[i] + half) >> shift) + _fixed.COff, 0, 255));
                    SetSample(v, i, (int)Math.Clamp(((fixedSum[cw + i] + half) >> shift) + _fixed.COff, 0, 255));
                }
            }
            else
            {
                float scale = 1f / weight;
                for (int i = 0; i < cw; i++)
                {
                    SetSample(u, i, YuvKernelsFloat.Round((floatSum[i] * scale) + _float.COff, t.MaxSample));
                    SetSample(v, i, YuvKernelsFloat.Round((floatSum[cw + i] * scale) + _float.COff, t.MaxSample));
                }
            }

            static long Filter(ReadOnlySpan<int> row, int x, bool fast, bool triangle)
            {
                int last = row.Length - 1;
                if (fast)
                {
                    return row[x];
                }

                long sum = 0;
                if (triangle)
                {
                    for (int k = -3; k <= 3; k++)
                    {
                        sum += (4 - Math.Abs(k)) * (long)row[Math.Clamp(x + k, 0, last)];
                    }
                }
                else
                {
                    for (int k = 0; k < 4; k++)
                    {
                        sum += row[Math.Min(x + k, last)];
                    }
                }

                return sum;
            }

            static float FilterFloat(ReadOnlySpan<float> row, int x, bool fast, bool triangle)
            {
                int last = row.Length - 1;
                if (fast)
                {
                    return row[x];
                }

                float sum = 0;
                if (triangle)
                {
                    for (int k = -3; k <= 3; k++)
                    {
                        sum += (4 - Math.Abs(k)) * row[Math.Clamp(x + k, 0, last)];
                    }
                }
                else
                {
                    for (int k = 0; k < 4; k++)
                    {
                        sum += row[Math.Min(x + k, last)];
                    }
                }

                return sum;
            }
        }

        /// <summary>
        /// Converts source row <paramref name="y"/>: luma and alpha into the target when <paramref name="owned"/>, and full-resolution
        /// chroma into slot <paramref name="slot"/>.
        /// </summary>
        private void ConvertRow(Raster<TPixel> source, int y, bool owned, int slot)
        {
            YuvRaster<TSample> t = _target;
            ReadOnlySpan<byte> rgb;
            if (_via is null)
            {
                rgb = MemoryMarshal.AsBytes(source.Row(y));
            }
            else
            {
                Span<TVia> via = _via.AsSpan(0, t.Width);
                PixelConvert.Convert<TPixel, TVia>(source.Row(y), via);
                rgb = MemoryMarshal.AsBytes(via);
            }

            Span<TSample> luma = owned ? t.Y.Row(y) : default;
            bool chroma = slot >= 0;
            if (_useFixed)
            {
                Span<int> cb = chroma ? _fixedRows[2 * slot].AsSpan(0, t.Width) : default, cr = chroma ? _fixedRows[(2 * slot) + 1].AsSpan(0, t.Width) : default;
                YuvKernels8.FromRgb(rgb, _order, MemoryMarshal.Cast<TSample, byte>(luma), cb, cr, t.Width, in _fixed);
            }
            else
            {
                Span<float> cb = chroma ? _floatRows[2 * slot].AsSpan(0, t.Width) : default, cr = chroma ? _floatRows[(2 * slot) + 1].AsSpan(0, t.Width) : default;
                if (_inMax == 255)
                {
                    YuvKernelsFloat.FromRgb(rgb, _order, luma, cb, cr, t.Width, t.MaxSample, in _float);
                }
                else
                {
                    YuvKernelsFloat.FromRgb(MemoryMarshal.Cast<byte, ushort>(rgb), _order, luma, cb, cr, t.Width, t.MaxSample, in _float);
                }
            }

            if (owned && t.A is not null)
            {
                Alpha(rgb, t.A.Row(y), t.Width, t.MaxSample);
            }
        }

        private void Alpha(ReadOnlySpan<byte> rgb, Span<TSample> alpha, int width, int max)
        {
            if (_order.A < 0)
            {
                for (int x = 0; x < width; x++)
                {
                    SetSample(alpha, x, max);
                }

                return;
            }

            long inMax = _inMax;
            if (_inMax == 255)
            {
                for (int x = 0; x < width; x++)
                {
                    int a = rgb[(x * _order.Size) + _order.A];
                    SetSample(alpha, x, max == 255 ? a : (int)(((2L * a * max) + inMax) / (2 * inMax)));
                }
            }
            else
            {
                ReadOnlySpan<ushort> words = MemoryMarshal.Cast<byte, ushort>(rgb);
                for (int x = 0; x < width; x++)
                {
                    int a = words[(x * _order.Size) + _order.A];
                    SetSample(alpha, x, (int)(((2L * a * max) + inMax) / (2 * inMax)));
                }
            }
        }
    }
}
