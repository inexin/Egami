using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

using Egami.Operations.Geometry;
using Egami.Operations.Resampling;
using Egami.Pixels;

namespace Egami.Operations.Filters;

/// <summary>
/// The convolution engine behind Blur, BoxBlur, Sharpen and Convolve. Rows are loaded into float, premultiplied, and padded on
/// both sides according to the edge mode, so the inner loops never check bounds. A separable kernel filters each row horizontally
/// into a ring of rows, then sums the ring vertically; a box filter does both with running sums; any other kernel keeps the padded
/// rows in the ring and sums every tap.
/// </summary>
internal sealed class ConvolutionOperation : IRasterOperation, INeedsNewPlane
{
    private readonly float[]? _row;
    private readonly float[]? _column;
    private readonly float[]? _full;
    private readonly int _rx;
    private readonly int _ry;
    private readonly bool _box;
    private readonly EdgeMode _edge;
    private readonly bool _preserveAlpha;
    private readonly float _offset;
    private readonly float _sharpen;
    private readonly bool _linear;

    private ConvolutionOperation(
        float[]? row, float[]? column, float[]? full, int rx, int ry, bool box, EdgeMode edge, bool preserveAlpha, float offset, float sharpen, bool linear)
    {
        _row = row;
        _column = column;
        _full = full;
        _rx = rx;
        _ry = ry;
        _box = box;
        _edge = edge;
        _preserveAlpha = preserveAlpha;
        _offset = offset;
        _sharpen = sharpen;
        _linear = linear;
    }

    /// <summary>A Gaussian blur: radius ceil(3σ), weights normalized to sum to 1.</summary>
    internal static ConvolutionOperation Gaussian(float sigma, bool linear, float sharpen = 0)
    {
        float[] weights = GaussianWeights(sigma);
        int r = weights.Length / 2;
        return new ConvolutionOperation(weights, weights, null, r, r, box: false, EdgeMode.Clamp, preserveAlpha: false, offset: 0, sharpen, linear);
    }

    /// <summary>The mean over a (2r + 1) square, as running sums.</summary>
    internal static ConvolutionOperation Box(int radius, bool linear)
    {
        return new ConvolutionOperation(null, null, null, radius, radius, box: true, EdgeMode.Clamp, preserveAlpha: false, offset: 0, sharpen: 0, linear);
    }

    /// <summary>A caller's kernel, with the divisor folded into the weights.</summary>
    internal static ConvolutionOperation FromKernel(ConvolutionKernel kernel, EdgeMode edge, bool preserveAlpha)
    {
        float scale = 1 / kernel.Divisor;
        if (kernel.IsSeparable)
        {
            float[] column = [.. kernel.Column!.Select(w => w * scale)];
            return new ConvolutionOperation([.. kernel.Row!], column, null, kernel.Width / 2, kernel.Height / 2, box: false, edge, preserveAlpha, kernel.Offset, sharpen: 0, linear: false);
        }

        float[] full = new float[kernel.Width * kernel.Height];
        for (int i = 0; i < full.Length; i++)
        {
            full[i] = kernel.Values[i] * scale;
        }

        return new ConvolutionOperation(null, null, full, kernel.Width / 2, kernel.Height / 2, box: false, edge, preserveAlpha, kernel.Offset, sharpen: 0, linear: false);
    }

    /// <summary>Returns the normalized Gaussian weights for <paramref name="sigma"/>, radius ceil(3σ).</summary>
    internal static float[] GaussianWeights(float sigma)
    {
        int r = Math.Max(1, (int)Math.Ceiling(3 * sigma));
        double[] w = new double[(2 * r) + 1];
        double sum = 0;
        for (int i = -r; i <= r; i++)
        {
            w[i + r] = Math.Exp(-(i * (double)i) / (2.0 * sigma * sigma));
            sum += w[i + r];
        }

        return [.. w.Select(v => (float)(v / sum))];
    }

    /// <summary>Maps a row or column index past the border to the one an edge mode reads, or −1 for transparent.</summary>
    internal static int MapIndex(int i, int size, EdgeMode edge)
    {
        if ((uint)i < (uint)size)
        {
            return i;
        }

        switch (edge)
        {
            case EdgeMode.Clamp:
                return Math.Clamp(i, 0, size - 1);
            case EdgeMode.Wrap:
                return ((i % size) + size) % size;
            case EdgeMode.Mirror:
                int period = 2 * size;
                int m = ((i % period) + period) % period;
                return m < size ? m : period - 1 - m;
            default:
                return -1;
        }
    }

    public bool NeedsNewPlane(Size input)
    {
        return true;
    }

    public Size OutputSize(Size input)
    {
        return input;
    }

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int height = input.Source.Height;

        // Each band re-reads 2ry rows above and below it, so bands should be tall; but every thread needs one. Bands are whole
        // groups of rows, at least 16 groups each (RowRunner's minimum): groups of up to ry rows, small enough that there is a band
        // per thread.
        int threads = Math.Max(1, input.Context.MaxParallelism);
        int group = Math.Clamp((height + (16 * threads) - 1) / (16 * threads), 1, Math.Max(1, _ry));
        long rowBytes = (long)input.Source.Width * TPixel.Layout.BytesPerPixel;
        RowRunner.Run((height + group - 1) / group, rowBytes * group, input.Context.MaxParallelism, (start, end) =>
        {
            new Band<TPixel>(this, input).Run(start * group, Math.Min(end * group, height));
        }, input.CancellationToken);
    }

    /// <summary>The buffers and state of one band of output rows.</summary>
    private sealed class Band<TPixel>
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        private readonly ConvolutionOperation _op;
        private readonly OperationInput<TPixel> _input;
        private readonly RowConverter<TPixel> _converter;
        private readonly int _width;
        private readonly int _height;
        private readonly int _ringRows;
        private readonly int _ringLength;
        private readonly int _block;

        internal Band(ConvolutionOperation op, OperationInput<TPixel> input)
        {
            _op = op;
            _input = input;
            _converter = new RowConverter<TPixel>(op._linear);
            _width = input.Source.Width;
            _height = input.Source.Height;

            // Separable and box rings hold filtered rows; the full-kernel ring holds padded source rows. Box keeps one extra row,
            // the one its running sum subtracts; separable keeps the rows for a block of outputs.
            _block = op._column is not null ? ConvolutionKernels.Block : 1;
            _ringRows = (2 * op._ry) + _block + (op._box ? 1 : 0);
            _ringLength = (op._full is null ? _width : _width + (2 * op._rx)) * 4;
        }

        internal void Run(int start, int end)
        {
            int padded = (_width + (2 * _op._rx)) * 4;
            int rowFloats = _width * 4;
            float[] ringBuffer = ArrayPool<float>.Shared.Rent(_ringRows * _ringLength);
            float[] lineBuffer = ArrayPool<float>.Shared.Rent(padded);
            float[] outBuffer = ArrayPool<float>.Shared.Rent(rowFloats * _block);
            float[] centerBuffer = ArrayPool<float>.Shared.Rent(rowFloats);
            float[] sumBuffer = ArrayPool<float>.Shared.Rent(_op._box ? rowFloats : 0);
            bool[] opaque = ArrayPool<bool>.Shared.Rent(_ringRows);
            try
            {
                Span<float> ring = ringBuffer.AsSpan(0, _ringRows * _ringLength);
                Span<float> line = lineBuffer.AsSpan(0, padded);
                Span<float> outputs = outBuffer.AsSpan(0, rowFloats * _block);
                Span<float> center = centerBuffer.AsSpan(0, rowFloats);
                Span<float> sums = sumBuffer.AsSpan(0, _op._box ? rowFloats : 0);
                int ry = _op._ry;
                int next = start - ry;
                for (int y0 = start; y0 < end; y0 += _block)
                {
                    int count = Math.Min(_block, end - y0);
                    for (; next <= y0 + count - 1 + ry; next++)
                    {
                        int slot = Slot(next);
                        opaque[slot] = LoadPadded(next, line);
                        Span<float> target = ring.Slice(slot * _ringLength, _ringLength);
                        if (_op._full is not null)
                        {
                            line.CopyTo(target);
                        }
                        else if (_op._box)
                        {
                            ConvolutionKernels.BoxRow(line, _op._rx, target);
                        }
                        else
                        {
                            ConvolutionKernels.Correlate(line, _op._row!, target);
                        }
                    }

                    if (_op._column is not null)
                    {
                        ConvolutionKernels.VerticalBlock(ring, rowFloats, _ringRows, Slot(y0 - ry), _op._column, outputs, count);
                    }

                    for (int j = 0; j < count; j++)
                    {
                        int y = y0 + j;
                        Span<float> output = outputs.Slice(j * rowFloats, rowFloats);
                        bool allOpaque = _op._edge != EdgeMode.Transparent && !_op._preserveAlpha;
                        for (int v = y - ry; v <= y + ry && allOpaque; v++)
                        {
                            allOpaque &= opaque[Slot(v)];
                        }

                        if (_op._box)
                        {
                            BoxColumn(ring, sums, y, y == start, output);
                        }
                        else if (_op._full is not null)
                        {
                            FullRow(ring, y, output);
                        }

                        Finish(y, output, center, ref allOpaque);
                        _converter.Store(output, _input.Destination.Row(y), allOpaque);
                    }
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(ringBuffer);
                ArrayPool<float>.Shared.Return(lineBuffer);
                ArrayPool<float>.Shared.Return(outBuffer);
                ArrayPool<float>.Shared.Return(centerBuffer);
                ArrayPool<float>.Shared.Return(sumBuffer);
                ArrayPool<bool>.Shared.Return(opaque);
            }
        }

        private int Slot(int virtualRow)
        {
            return ((virtualRow % _ringRows) + _ringRows) % _ringRows;
        }

        /// <summary>Loads virtual row <paramref name="v"/> into the middle of <paramref name="line"/> and fills the side padding.</summary>
        /// <returns><see langword="true"/> if every loaded pixel is opaque.</returns>
        private bool LoadPadded(int v, Span<float> line)
        {
            int rx = _op._rx;
            Span<float> middle = line.Slice(rx * 4, _width * 4);
            int row = MapIndex(v, _height, _op._edge);
            bool opaque;
            if (row < 0)
            {
                middle.Clear();
                opaque = false;
            }
            else
            {
                opaque = _converter.Load(_input.Source.Row(row), middle);
                if (_op._preserveAlpha)
                {
                    ResizeKernels.Unpremultiply(middle, opaque);
                }
            }

            for (int i = 1; i <= rx; i++)
            {
                Pad(line, rx - i, MapIndex(-i, _width, _op._edge));
                Pad(line, rx + _width - 1 + i, MapIndex(_width - 1 + i, _width, _op._edge));
            }

            return opaque && (rx == 0 || _op._edge != EdgeMode.Transparent);
        }

        private void Pad(Span<float> line, int at, int from)
        {
            Span<float> target = line.Slice(at * 4, 4);
            if (from < 0)
            {
                target.Clear();
            }
            else
            {
                line.Slice((_op._rx + from) * 4, 4).CopyTo(target);
            }
        }

        /// <summary>Sums the box column for row <paramref name="y"/>: from scratch on the band's first row, then by sliding.</summary>
        private void BoxColumn(Span<float> ring, Span<float> sums, int y, bool first, Span<float> output)
        {
            int r = _op._ry;
            Span<Vector4> s = MemoryMarshal.Cast<float, Vector4>(sums);
            if (first)
            {
                s.Clear();
                for (int v = y - r; v <= y + r; v++)
                {
                    ReadOnlySpan<Vector4> row = MemoryMarshal.Cast<float, Vector4>(ring.Slice(Slot(v) * _ringLength, _ringLength));
                    for (int x = 0; x < s.Length; x++)
                    {
                        s[x] += row[x];
                    }
                }
            }
            else
            {
                ReadOnlySpan<Vector4> added = MemoryMarshal.Cast<float, Vector4>(ring.Slice(Slot(y + r) * _ringLength, _ringLength));
                ReadOnlySpan<Vector4> removed = MemoryMarshal.Cast<float, Vector4>(ring.Slice(Slot(y - r - 1) * _ringLength, _ringLength));
                for (int x = 0; x < s.Length; x++)
                {
                    s[x] += added[x] - removed[x];
                }
            }

            float scale = 1f / ((2 * r) + 1);
            Span<Vector4> o = MemoryMarshal.Cast<float, Vector4>(output);
            for (int x = 0; x < o.Length; x++)
            {
                o[x] = s[x] * scale;
            }
        }

        /// <summary>Sums every tap of a full kernel from the padded rows in the ring.</summary>
        private void FullRow(Span<float> ring, int y, Span<float> output)
        {
            int kw = (2 * _op._rx) + 1;
            int kh = (2 * _op._ry) + 1;
            bool firstTap = true;
            for (int ky = 0; ky < kh; ky++)
            {
                ReadOnlySpan<float> row = ring.Slice(Slot(y - _op._ry + ky) * _ringLength, _ringLength);
                for (int kx = 0; kx < kw; kx++)
                {
                    float w = _op._full![(ky * kw) + kx];
                    ConvolutionKernels.Accumulate(row.Slice(kx * 4, output.Length), w, output, firstTap);
                    firstTap = false;
                }
            }
        }

        /// <summary>Applies the offset, the sharpen blend and the kept alpha, leaving premultiplied pixels for the converter.</summary>
        private void Finish(int y, Span<float> output, Span<float> center, ref bool opaque)
        {
            Span<Vector4> o = MemoryMarshal.Cast<float, Vector4>(output);
            if (_op._sharpen != 0)
            {
                // Unsharp mask in premultiplied form: out = src + amount × (src − blur).
                bool centerOpaque = _converter.Load(_input.Source.Row(y), center);
                opaque &= centerOpaque;
                ReadOnlySpan<Vector4> c = MemoryMarshal.Cast<float, Vector4>(center);
                float amount = _op._sharpen;
                for (int x = 0; x < o.Length; x++)
                {
                    o[x] = c[x] + ((c[x] - o[x]) * amount);
                }
            }

            if (_op._preserveAlpha)
            {
                // The color was convolved straight; take the source pixel's alpha and premultiply by it.
                bool centerOpaque = _converter.Load(_input.Source.Row(y), center);
                ResizeKernels.Unpremultiply(center, centerOpaque);
                ReadOnlySpan<Vector4> c = MemoryMarshal.Cast<float, Vector4>(center);
                for (int x = 0; x < o.Length; x++)
                {
                    float a = c[x].W;
                    Vector4 v = o[x] + new Vector4(_op._offset, _op._offset, _op._offset, 0);
                    o[x] = new Vector4(v.X * a, v.Y * a, v.Z * a, a);
                }

                return;
            }

            if (_op._offset != 0)
            {
                // The offset is a straight color change, so in premultiplied form it scales with alpha.
                for (int x = 0; x < o.Length; x++)
                {
                    float a = o[x].W;
                    o[x] += new Vector4(_op._offset * a, _op._offset * a, _op._offset * a, 0);
                }
            }
        }
    }
}

/// <summary>The inner loops of convolution. Vector paths add in the same order as the scalar references.</summary>
internal static class ConvolutionKernels
{
    /// <summary>The output rows the vertical pass makes per read of the ring: eight accumulators fit in the vector registers.</summary>
    internal const int Block = 8;

    /// <summary>
    /// The vertical pass for <paramref name="count"/> (at most <see cref="Block"/>) consecutive output rows. Output j sums ring rows
    /// <c>first + j + k</c> (mod <paramref name="ringRows"/>) times <c>weights[k]</c>, from k = 0 up, as
    /// <see cref="ResizeKernels.VerticalScalar"/> does; each ring row is read once for the whole block, which cuts memory traffic
    /// for tall kernels by the block size. <paramref name="outputs"/> holds the rows one after another.
    /// </summary>
    internal static void VerticalBlock(ReadOnlySpan<float> ring, int rowLength, int ringRows, int first, ReadOnlySpan<float> weights, Span<float> outputs, int count)
    {
        int taps = weights.Length;
        if (ring.Length < rowLength * ringRows || outputs.Length < rowLength * count || count < 1 || count > Block || taps + count - 1 > ringRows)
        {
            throw new ArgumentException("The ring or outputs are too short.");
        }

        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            ref float r = ref MemoryMarshal.GetReference(ring);
            ref float o = ref MemoryMarshal.GetReference(outputs);
            Span<nuint> rows = stackalloc nuint[taps + count - 1];
            for (int k = 0; k < rows.Length; k++)
            {
                rows[k] = (nuint)((first + k) % ringRows * rowLength);
            }

            for (; i <= rowLength - Vector256<float>.Count; i += Vector256<float>.Count)
            {
                // One accumulator per output row, all in registers; output j takes ring row k with weight k − j.
                Vector256<float> a0 = Vector256<float>.Zero, a1 = a0, a2 = a0, a3 = a0, a4 = a0, a5 = a0, a6 = a0, a7 = a0;
                for (int k = 0; k < rows.Length; k++)
                {
                    Vector256<float> v = Vector256.LoadUnsafe(ref r, rows[k] + (nuint)i);
                    AddTap(ref a0, v, weights, k, count, 0);
                    AddTap(ref a1, v, weights, k, count, 1);
                    AddTap(ref a2, v, weights, k, count, 2);
                    AddTap(ref a3, v, weights, k, count, 3);
                    AddTap(ref a4, v, weights, k, count, 4);
                    AddTap(ref a5, v, weights, k, count, 5);
                    AddTap(ref a6, v, weights, k, count, 6);
                    AddTap(ref a7, v, weights, k, count, 7);
                }

                Store(a0, ref o, rowLength, i, count, 0);
                Store(a1, ref o, rowLength, i, count, 1);
                Store(a2, ref o, rowLength, i, count, 2);
                Store(a3, ref o, rowLength, i, count, 3);
                Store(a4, ref o, rowLength, i, count, 4);
                Store(a5, ref o, rowLength, i, count, 5);
                Store(a6, ref o, rowLength, i, count, 6);
                Store(a7, ref o, rowLength, i, count, 7);
            }
        }

        for (int j = 0; j < count; j++)
        {
            ResizeKernels.VerticalScalar(ring, rowLength, ringRows, (first + j) % ringRows, weights, outputs.Slice(j * rowLength, rowLength), i);
        }
    }

    /// <summary>
    /// Correlates a padded row with 1D weights: <c>destination[i] = Σ weights[k] × source[i + 4k]</c>, summed from k = 0 up.
    /// </summary>
    internal static void Correlate(ReadOnlySpan<float> source, ReadOnlySpan<float> weights, Span<float> destination)
    {
        int i = 0;
        if (Vector256.IsHardwareAccelerated && source.Length >= destination.Length + ((weights.Length - 1) * 4))
        {
            // 64 outputs at a time in eight registers, every tap summed into them before they are stored: the row is written once
            // instead of once per tap, which matters for wide kernels.
            ref float s = ref MemoryMarshal.GetReference(source);
            ref float d = ref MemoryMarshal.GetReference(destination);
            for (; i <= destination.Length - 64; i += 64)
            {
                Vector256<float> a0 = Vector256<float>.Zero, a1 = a0, a2 = a0, a3 = a0, a4 = a0, a5 = a0, a6 = a0, a7 = a0;
                for (int k = 0; k < weights.Length; k++)
                {
                    nuint at = (nuint)(i + (k * 4));
                    float w = weights[k];
                    a0 += Vector256.LoadUnsafe(ref s, at) * w;
                    a1 += Vector256.LoadUnsafe(ref s, at + 8) * w;
                    a2 += Vector256.LoadUnsafe(ref s, at + 16) * w;
                    a3 += Vector256.LoadUnsafe(ref s, at + 24) * w;
                    a4 += Vector256.LoadUnsafe(ref s, at + 32) * w;
                    a5 += Vector256.LoadUnsafe(ref s, at + 40) * w;
                    a6 += Vector256.LoadUnsafe(ref s, at + 48) * w;
                    a7 += Vector256.LoadUnsafe(ref s, at + 56) * w;
                }

                a0.StoreUnsafe(ref d, (nuint)i);
                a1.StoreUnsafe(ref d, (nuint)(i + 8));
                a2.StoreUnsafe(ref d, (nuint)(i + 16));
                a3.StoreUnsafe(ref d, (nuint)(i + 24));
                a4.StoreUnsafe(ref d, (nuint)(i + 32));
                a5.StoreUnsafe(ref d, (nuint)(i + 40));
                a6.StoreUnsafe(ref d, (nuint)(i + 48));
                a7.StoreUnsafe(ref d, (nuint)(i + 56));
            }
        }

        // The rest, tap by tap, in the same order per element.
        Span<float> tail = destination[i..];
        for (int k = 0; k < weights.Length; k++)
        {
            Accumulate(source.Slice(i + (k * 4), tail.Length), weights[k], tail, k == 0);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddTap(ref Vector256<float> sum, Vector256<float> v, ReadOnlySpan<float> weights, int k, int count, int j)
    {
        if (j < count && (uint)(k - j) < (uint)weights.Length)
        {
            sum += v * weights[k - j];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store(Vector256<float> sum, ref float outputs, int rowLength, int i, int count, int j)
    {
        if (j < count)
        {
            sum.StoreUnsafe(ref outputs, (nuint)((j * rowLength) + i));
        }
    }

    /// <summary><c>destination[i] = (first ? 0 : destination[i]) + weight × source[i]</c>, with the first tap stored, not added.</summary>
    internal static void Accumulate(ReadOnlySpan<float> source, float weight, Span<float> destination, bool first)
    {
        int i = 0;
        if (Vector256.IsHardwareAccelerated && source.Length >= destination.Length)
        {
            ref float s = ref MemoryMarshal.GetReference(source);
            ref float d = ref MemoryMarshal.GetReference(destination);
            for (; i <= destination.Length - Vector256<float>.Count; i += Vector256<float>.Count)
            {
                Vector256<float> product = Vector256.LoadUnsafe(ref s, (nuint)i) * weight;
                (first ? product : Vector256.LoadUnsafe(ref d, (nuint)i) + product).StoreUnsafe(ref d, (nuint)i);
            }
        }

        AccumulateScalar(source, weight, destination, first, i);
    }

    /// <summary>The reference for <see cref="Accumulate"/>, from element <paramref name="from"/> on.</summary>
    internal static void AccumulateScalar(ReadOnlySpan<float> source, float weight, Span<float> destination, bool first, int from = 0)
    {
        for (int i = from; i < destination.Length; i++)
        {
            float product = source[i] * weight;
            destination[i] = first ? product : destination[i] + product;
        }
    }

    /// <summary>Box-filters a row padded by <paramref name="radius"/> pixels on each side with a running sum.</summary>
    internal static void BoxRow(ReadOnlySpan<float> source, int radius, Span<float> destination)
    {
        ReadOnlySpan<Vector4> s = MemoryMarshal.Cast<float, Vector4>(source);
        Span<Vector4> d = MemoryMarshal.Cast<float, Vector4>(destination);
        int window = (2 * radius) + 1;
        float scale = 1f / window;
        Vector4 sum = Vector4.Zero;
        for (int k = 0; k < window; k++)
        {
            sum += s[k];
        }

        for (int x = 0; x < d.Length; x++)
        {
            d[x] = sum * scale;
            if (x + window < s.Length)
            {
                sum += s[x + window] - s[x];
            }
        }
    }
}
