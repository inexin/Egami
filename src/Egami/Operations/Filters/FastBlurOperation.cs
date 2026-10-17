using System.Buffers;
using System.Runtime.InteropServices;

using Egami.Operations.Geometry;
using Egami.Operations.Resampling;
using Egami.Pixels;

namespace Egami.Operations.Filters;

/// <summary>
/// Box passes per axis on premultiplied float rows, with edges repeating the edge pixel: <see cref="BlurMethod.Fast"/>'s three
/// extended boxes, and the plain boxes of BoxBlur and of Blur above σ 100. Each strip of columns runs from the top of the image to
/// the bottom, so the result is the same at any thread count.
/// </summary>
internal sealed class FastBlurOperation : IRasterOperation, INeedsNewPlane, ILinearLight, Scopes.INeighbourhoodStep
{
    private readonly BoxCascade _x;
    private readonly BoxCascade _y;
    private readonly bool _linear;
    private readonly Colors.LinearCurve? _curve;
    private readonly int _strip;

    internal FastBlurOperation(float sigmaX, float sigmaY, bool linear)
        : this(BoxCascade.For(sigmaX), BoxCascade.For(sigmaY), linear, FastBlur.Strip)
    {
    }

    /// <summary>Runs <paramref name="x"/> across and <paramref name="y"/> down, in strips of <paramref name="strip"/> columns.</summary>
    internal FastBlurOperation(BoxCascade x, BoxCascade y, bool linear, int strip, Colors.LinearCurve? curve = null)
    {
        _x = x;
        _y = y;
        _linear = linear;
        _strip = strip;
        _curve = curve;
    }

    /// <summary>Plain box passes of <paramref name="radii"/> on both axes, for BoxBlur and the large Blur.</summary>
    internal static FastBlurOperation Boxes(int[] radii, bool linear)
    {
        BoxCascade boxes = BoxCascade.Boxes(radii);
        return new FastBlurOperation(boxes, boxes, linear, FastBlur.BoxStrip);
    }

    public bool UsesLinearLight => _linear;

    /// <summary>Gets <see langword="null"/>: running sums round by where they start, so a scope runs the blur on the whole image.</summary>
    public (int X, int Y)? Reach => null;

    /// <summary>Gets how far the box passes read past a pixel, for <see cref="RasterOps.Reach"/>.</summary>
    internal (int X, int Y) ReadReach => (_x.Reach, _y.Reach);

    public IRasterOperation WithCurve(Colors.LinearCurve curve) => new FastBlurOperation(_x, _y, _linear, _strip, curve);

    public bool NeedsNewPlane(Size input) => true;

    public Size OutputSize(Size input) => input;

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        // Byte types take the byte path for the three-pass cascades; one plain box costs less than the byte path's two sweeps.
        if (ByteBlur.Supports<TPixel>() && (_x.Radii.Length > 1 || _y.Radii.Length > 1))
        {
            IPixelMemorySource memory = (IPixelMemorySource?)input.Memory ?? input.Context.Memory;
            Colors.LinearCurve? curve = _linear ? _curve ?? Colors.LinearCurve.Srgb : null;
            ByteBlur.Run(input.Source, input.Destination, _x, _y, curve, memory, input.Context.MaxParallelism, input.CancellationToken);
            return;
        }

        // Strips of columns, each from the top of the image to the bottom: every row of a strip is loaded with the columns the
        // horizontal passes reach on either side, filtered across, then pulled through the vertical passes and stored. Strips have
        // a fixed width and every running sum starts at the same place, so the result does not depend on the thread count.
        RowConverter<TPixel> converter = new(_linear ? _curve ?? Colors.LinearCurve.Srgb : null);
        int width = input.Source.Width;
        int stripWidth = _strip;
        int strips = (width + stripWidth - 1) / stripWidth;
        Chunks.Run(strips, (long)width * input.Source.Height * 16, input.Context.MaxParallelism, (start, end) =>
        {
            using Strips<TPixel> strip = new(input.Source, input.Destination, converter, _x, _y, stripWidth);
            for (int s = start; s < end; s++)
            {
                input.CancellationToken.ThrowIfCancellationRequested();
                strip.Blur(s * stripWidth, Math.Min(stripWidth, width - (s * stripWidth)));
            }
        }, input.CancellationToken);
    }

    /// <summary>One thread's strips: loads and filters rows across, runs the vertical passes, and stores.</summary>
    private sealed class Strips<TPixel> : BoxCascade.Vertical
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        private readonly PixelPlane<TPixel> _source;
        private readonly PixelPlane<TPixel> _destination;
        private readonly RowConverter<TPixel> _converter;
        private readonly BoxCascade _x;
        private readonly int _reachY;
        private readonly float[] _lines;
        private readonly int _lineLength;
        private int _x0;
        private int _count;
        private int _cached;
        private int _lastTranslucent;

        internal Strips(PixelPlane<TPixel> source, PixelPlane<TPixel> destination, RowConverter<TPixel> converter, BoxCascade x, BoxCascade y, int stripWidth)
            : base(y, stripWidth * 4)
        {
            _source = source;
            _destination = destination;
            _converter = converter;
            _x = x;
            _reachY = y.Reach;
            _lineLength = (stripWidth + (2 * x.Reach)) * 4;
            _lines = ArrayPool<float>.Shared.Rent(2 * _lineLength);
        }

        internal void Blur(int x0, int count)
        {
            _x0 = x0;
            _count = count;
            _cached = int.MinValue;
            _lastTranslucent = int.MinValue;
            Run(0, _source.Height, count * 4);
        }

        protected override void Load(int v, Span<float> target)
        {
            // Rows come in order, so two are filtered at once: their running sums do not wait on each other.
            Span<float> a = _lines.AsSpan(0, _lineLength), b = _lines.AsSpan(_lineLength, _lineLength);
            if (v != _cached + 1)
            {
                LoadRow(v, a);
                LoadRow(v + 1, b);
                _x.Horizontal(a, b, _count);
                _cached = v;
                a[..(_count * 4)].CopyTo(target);
                return;
            }

            b[..(_count * 4)].CopyTo(target);
        }

        protected override void Store(int y, Span<float> row)
        {
            _converter.Store(row, _destination.Row(y).Slice(_x0, _count), _lastTranslucent < y - _reachY);
        }

        protected override void Dispose(bool disposing)
        {
            ArrayPool<float>.Shared.Return(_lines);
            base.Dispose(disposing);
        }

        /// <summary>Loads the strip's columns of row <paramref name="v"/> with the horizontal reach on each side, repeating the edge pixels past the image.</summary>
        private void LoadRow(int v, Span<float> line)
        {
            int width = _source.Width, reach = _x.Reach;
            int y = Math.Clamp(v, 0, _source.Height - 1);
            int first = Math.Max(_x0 - reach, 0), last = Math.Min(_x0 + _count + reach, width);
            int offset = first - (_x0 - reach);
            if (!_converter.Load(_source.Row(y)[first..last], line[(offset * 4)..]))
            {
                _lastTranslucent = Math.Max(_lastTranslucent, v);
            }

            Span<float> left = line.Slice(offset * 4, 4);
            for (int i = 0; i < offset; i++)
            {
                left.CopyTo(line[(i * 4)..]);
            }

            int end = offset + (last - first), total = _count + (2 * reach);
            Span<float> right = line.Slice((end - 1) * 4, 4);
            for (int i = end; i < total; i++)
            {
                right.CopyTo(line[(i * 4)..]);
            }
        }
    }
}

/// <summary>The box passes' strip widths, and padding a row for them (Canny's smoothing shares it).</summary>
internal static class FastBlur
{
    /// <summary>The pixels in a strip of the vertical pass.</summary>
    internal const int Strip = 128;

    /// <summary>
    /// The pixels in a strip of plain box passes. Their radii start at 9 (BoxBlur) and run past 100 (Blur above σ 100), so each
    /// row of a strip loads twice the reach on top of the strip; wider strips spend less of the time on that.
    /// </summary>
    internal const int BoxStrip = 512;

    /// <summary>Copies a row of <paramref name="width"/> pixels into the middle of <paramref name="padded"/> and repeats its end pixels into the <paramref name="pad"/> on each side.</summary>
    internal static void LoadPadded(ReadOnlySpan<float> row, Span<float> padded, int width, int channels, int pad)
    {
        row[..(width * channels)].CopyTo(padded.Slice(pad * channels));
        Span<float> first = padded.Slice(pad * channels, channels), last = padded.Slice((pad + width - 1) * channels, channels);
        for (int i = 0; i < pad; i++)
        {
            first.CopyTo(padded.Slice(i * channels));
            last.CopyTo(padded.Slice((pad + width + i) * channels));
        }
    }
}

/// <summary>Runs work items in about one contiguous chunk per thread, for work that sets up per-thread state once.</summary>
internal static class Chunks
{
    /// <summary>Runs items 0 to <paramref name="count"/> − 1 in chunks, in parallel when <paramref name="totalBytes"/> is large enough to be worth it.</summary>
    internal static void Run(int count, long totalBytes, int maxParallelism, RowBandAction action, CancellationToken cancellationToken)
    {
        int threads = Math.Min(Math.Max(1, maxParallelism), count);
        if (threads <= 1 || totalBytes < RowRunner.ParallelThresholdBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            action(0, count);
            return;
        }

        ParallelOptions options = new() { MaxDegreeOfParallelism = threads, CancellationToken = cancellationToken };
        Parallel.For(0, threads, options, chunk =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            action((int)((long)count * chunk / threads), (int)((long)count * (chunk + 1) / threads));
        });
    }
}
