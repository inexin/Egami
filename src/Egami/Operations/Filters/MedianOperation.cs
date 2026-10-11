using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

using Egami.Operations.Geometry;
using Egami.Pixels;

namespace Egami.Operations.Filters;

/// <summary>
/// The median of each channel over a (2r + 1)² square, on stored values, edges repeating the edge pixel. Types whose channels are
/// straight bytes take the byte paths: a selection network on 32 bytes at a time up to radius 2, and from radius 3 the
/// constant-time running histograms of Perreault and Hébert (2007). Other types load straight floats and select per pixel.
/// </summary>
internal sealed class MedianOperation : IRasterOperation, INeedsNewPlane, Scopes.INeighbourhoodStep
{
    /// <summary>The largest radius: window counts must fit the histograms' 16-bit bins.</summary>
    internal const int MaxRadius = 100;

    /// <summary>The largest radius the selection networks take; above it the histograms are faster.</summary>
    internal const int NetworkRadius = 2;

    private readonly int _r;
    private readonly bool _includeAlpha;

    internal MedianOperation(int radius, bool includeAlpha)
    {
        _r = radius;
        _includeAlpha = includeAlpha;
    }

    public (int X, int Y)? Reach => (_r, _r);

    public bool NeedsNewPlane(Size input) => true;

    public Size OutputSize(Size input) => input;

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (StoredRows<TPixel>.Bytes && _r <= NetworkRadius)
        {
            NetworkBands(input);
        }
        else if (StoredRows<TPixel>.Bytes)
        {
            HistogramTiles(input);
        }
        else
        {
            SelectBands(input);
        }

        if (!_includeAlpha && TPixel.Layout.HasAlpha && TPixel.Layout.ChannelOrder != PixelChannelOrder.Cmyk)
        {
            KeepAlpha(input);
        }
    }

    /// <summary>The window's lanes kept: every byte lane, or all but alpha.</summary>
    private int[] Lanes<TPixel>()
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int bpp = Unsafe.SizeOf<TPixel>();
        int alpha = _includeAlpha ? -1 : StoredRows<TPixel>.AlphaByte;
        return [.. Enumerable.Range(0, bpp).Where(b => b != alpha)];
    }

    /// <summary>Puts the source's alpha back, for types whose alpha the median does not touch.</summary>
    private static void KeepAlpha<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        PixelPlane<TPixel> source = input.Source, destination = input.Destination;
        if (StoredRows<TPixel>.Bytes)
        {
            int bpp = Unsafe.SizeOf<TPixel>(), alpha = StoredRows<TPixel>.AlphaByte;
            input.ForEachBand((start, end) =>
            {
                for (int y = start; y < end; y++)
                {
                    ReadOnlySpan<byte> s = MemoryMarshal.AsBytes(source.Row(y));
                    Span<byte> d = MemoryMarshal.AsBytes(destination.Row(y));
                    for (int i = alpha; i < s.Length; i += bpp)
                    {
                        d[i] = s[i];
                    }
                }
            });
        }

        // The float path keeps alpha itself.
    }

    /// <summary>Loads virtual row <paramref name="v"/>'s bytes into <paramref name="padded"/>, repeating the edge pixels <paramref name="r"/> deep on each side.</summary>
    private static void LoadBytes<TPixel>(PixelPlane<TPixel> source, int v, int r, Span<byte> padded)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int bpp = Unsafe.SizeOf<TPixel>();
        ReadOnlySpan<byte> row = MemoryMarshal.AsBytes(source.Row(Math.Clamp(v, 0, source.Height - 1)));
        row.CopyTo(padded[(r * bpp)..]);
        ReadOnlySpan<byte> first = row[..bpp], last = row[^bpp..];
        for (int i = 0; i < r; i++)
        {
            first.CopyTo(padded[(i * bpp)..]);
            last.CopyTo(padded[((r + source.Width + i) * bpp)..]);
        }
    }

    /// <summary>Radius 1 and 2 on byte types: each window offset loads 32 bytes, and the network sorts them lane by lane.</summary>
    private void NetworkBands<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        PixelPlane<TPixel> source = input.Source, destination = input.Destination;
        int r = _r, side = (2 * r) + 1, bpp = Unsafe.SizeOf<TPixel>();
        int rowBytes = source.Width * bpp, padded = (source.Width + (2 * r)) * bpp;
        SelectionNetwork network = SelectionNetwork.Median(side * side);
        input.ForEachBand((start, end) =>
        {
            byte[] ring = ArrayPool<byte>.Shared.Rent(side * padded);
            byte[] scalars = ArrayPool<byte>.Shared.Rent(side * side);
            try
            {
                Span<Vector256<byte>> values = stackalloc Vector256<byte>[side * side];
                for (int v = start - r; v < start + r; v++)
                {
                    LoadBytes(source, v, r, ring.AsSpan(Slot(v, side) * padded, padded));
                }

                for (int y = start; y < end; y++)
                {
                    LoadBytes(source, y + r, r, ring.AsSpan(Slot(y + r, side) * padded, padded));
                    Span<byte> output = MemoryMarshal.AsBytes(destination.Row(y));
                    int o = 0;
                    if (Vector256.IsHardwareAccelerated && rowBytes >= Vector256<byte>.Count)
                    {
                        for (; ; o += Vector256<byte>.Count)
                        {
                            // The last block overlaps the one before it rather than running past the row.
                            o = Math.Min(o, rowBytes - Vector256<byte>.Count);
                            int k = 0;
                            for (int dy = -r; dy <= r; dy++)
                            {
                                ref byte row = ref ring[Slot(y + dy, side) * padded];
                                for (int dx = 0; dx < side; dx++)
                                {
                                    values[k++] = Vector256.LoadUnsafe(ref row, (nuint)(o + (dx * bpp)));
                                }
                            }

                            network.Run(values);
                            values[side * side / 2].StoreUnsafe(ref MemoryMarshal.GetReference(output), (nuint)o);
                            if (o == rowBytes - Vector256<byte>.Count)
                            {
                                break;
                            }
                        }
                    }
                    else
                    {
                        for (; o < rowBytes; o++)
                        {
                            int k = 0;
                            for (int dy = -r; dy <= r; dy++)
                            {
                                int row = Slot(y + dy, side) * padded;
                                for (int dx = 0; dx < side; dx++)
                                {
                                    scalars[k++] = ring[row + o + (dx * bpp)];
                                }
                            }

                            network.Run(scalars.AsSpan(0, side * side));
                            output[o] = scalars[side * side / 2];
                        }
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(ring);
                ArrayPool<byte>.Shared.Return(scalars);
            }
        });
    }

    private static int Slot(int v, int rows) => ((v % rows) + rows) % rows;

    /// <summary>
    /// Radius 3 and up on byte types. Tiles of columns keep a 256-bin histogram per column and lane, slid down one row at a time;
    /// the window's histogram slides across by adding one column and taking one away, 16 bins to a vector, and a 16-bin coarse
    /// histogram finds the median's block before its 16 fine bins are searched.
    /// </summary>
    private void HistogramTiles<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        PixelPlane<TPixel> source = input.Source;
        int width = source.Width, height = source.Height;
        int[] lanes = Lanes<TPixel>();
        if (lanes.Length == 0)
        {
            Copy(input);
            return;
        }

        const int TileWidth = 128;
        int strips = (width + TileWidth - 1) / TileWidth;
        int threads = Math.Max(1, input.Context.MaxParallelism);
        int bands = Math.Max(1, Math.Min((2 * threads + strips - 1) / strips, height / Math.Max(32, 2 * _r)));
        int bandHeight = (height + bands - 1) / bands;
        int tiles = strips * ((height + bandHeight - 1) / bandHeight);
        Chunks.Run(tiles, (long)width * height * Unsafe.SizeOf<TPixel>() * _r, input.Context.MaxParallelism, (start, end) =>
        {
            using HistogramTile<TPixel> tile = new(input, _r, lanes, TileWidth);
            for (int t = start; t < end; t++)
            {
                input.CancellationToken.ThrowIfCancellationRequested();
                int x0 = t % strips * TileWidth, y0 = t / strips * bandHeight;
                tile.Run(x0, Math.Min(x0 + TileWidth, width), y0, Math.Min(y0 + bandHeight, height));
            }
        }, input.CancellationToken);
    }

    private static void Copy<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        for (int y = 0; y < input.Source.Height; y++)
        {
            input.Source.Row(y).CopyTo(input.Destination.Row(y));
        }
    }

    /// <summary>One thread's histograms for <see cref="HistogramTiles"/>.</summary>
    private sealed class HistogramTile<TPixel> : IDisposable
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        private readonly OperationInput<TPixel> _input;
        private readonly int _r;
        private readonly int[] _lanes;
        private readonly int _bpp;
        private readonly ushort[] _columns;
        private readonly ushort[] _coarseColumns;
        private readonly ushort[] _window;
        private readonly ushort[] _coarse;

        internal HistogramTile(OperationInput<TPixel> input, int r, int[] lanes, int tileWidth)
        {
            _input = input;
            _r = r;
            _lanes = lanes;
            _bpp = Unsafe.SizeOf<TPixel>();
            int columns = (tileWidth + (2 * r)) * lanes.Length;
            _columns = ArrayPool<ushort>.Shared.Rent(columns * 256);
            _coarseColumns = ArrayPool<ushort>.Shared.Rent(columns * 16);
            _window = ArrayPool<ushort>.Shared.Rent(lanes.Length * 256);
            _coarse = ArrayPool<ushort>.Shared.Rent(lanes.Length * 16);
        }

        internal void Run(int x0, int x1, int y0, int y1)
        {
            PixelPlane<TPixel> source = _input.Source;
            int r = _r, height = source.Height, lanes = _lanes.Length;
            int columns = (x1 - x0 + (2 * r)) * lanes;
            Span<ushort> fine = _columns.AsSpan(0, columns * 256), coarse = _coarseColumns.AsSpan(0, columns * 16);
            fine.Clear();
            coarse.Clear();
            for (int v = y0 - r; v <= y0 + r; v++)
            {
                Column(MemoryMarshal.AsBytes(source.Row(Math.Clamp(v, 0, height - 1))), x0, x1, 1);
            }

            int n = ((2 * r) + 1) * ((2 * r) + 1), target = n / 2;
            for (int y = y0; y < y1; y++)
            {
                if (y > y0)
                {
                    Column(MemoryMarshal.AsBytes(source.Row(Math.Clamp(y - r - 1, 0, height - 1))), x0, x1, -1);
                    Column(MemoryMarshal.AsBytes(source.Row(Math.Clamp(y + r, 0, height - 1))), x0, x1, 1);
                }

                Span<byte> output = MemoryMarshal.AsBytes(_input.Destination.Row(y));
                Span<ushort> window = _window.AsSpan(0, lanes * 256), windowCoarse = _coarse.AsSpan(0, lanes * 16);
                window.Clear();
                windowCoarse.Clear();
                for (int c = 0; c <= 2 * r; c++)
                {
                    for (int l = 0; l < lanes; l++)
                    {
                        Add(fine.Slice(((c * lanes) + l) * 256, 256), window.Slice(l * 256, 256));
                        Add(coarse.Slice(((c * lanes) + l) * 16, 16), windowCoarse.Slice(l * 16, 16));
                    }
                }

                for (int x = x0; x < x1; x++)
                {
                    int c = x - x0;
                    if (x > x0)
                    {
                        for (int l = 0; l < lanes; l++)
                        {
                            int added = ((c + (2 * r)) * lanes) + l, removed = ((c - 1) * lanes) + l;
                            Slide(fine.Slice(added * 256, 256), fine.Slice(removed * 256, 256), window.Slice(l * 256, 256));
                            Slide(coarse.Slice(added * 16, 16), coarse.Slice(removed * 16, 16), windowCoarse.Slice(l * 16, 16));
                        }
                    }

                    for (int l = 0; l < lanes; l++)
                    {
                        output[(x * _bpp) + _lanes[l]] = Find(window.Slice(l * 256, 256), windowCoarse.Slice(l * 16, 16), target);
                    }
                }
            }
        }

        /// <summary>
        /// Adds (<paramref name="sign"/> 1) or removes (−1) one row's bytes to every column histogram of the tile; columns past the
        /// image's sides read the edge pixel.
        /// </summary>
        private void Column(ReadOnlySpan<byte> row, int x0, int x1, int sign)
        {
            int r = _r, lanes = _lanes.Length, width = row.Length / _bpp;
            ushort step = (ushort)sign;
            for (int c = 0, x = x0 - r; x < x1 + r; c++, x++)
            {
                int at = Math.Clamp(x, 0, width - 1) * _bpp;
                for (int l = 0; l < lanes; l++)
                {
                    byte value = row[at + _lanes[l]];
                    int column = (c * lanes) + l;
                    _columns[(column * 256) + value] += step;
                    _coarseColumns[(column * 16) + (value >> 4)] += step;
                }
            }
        }

        private static byte Find(ReadOnlySpan<ushort> window, ReadOnlySpan<ushort> coarse, int target)
        {
            int below = 0, block = 0;
            while (below + coarse[block] <= target)
            {
                below += coarse[block];
                block++;
            }

            int bin = block * 16;
            while (below + window[bin] <= target)
            {
                below += window[bin];
                bin++;
            }

            return (byte)bin;
        }

        private static void Add(ReadOnlySpan<ushort> column, Span<ushort> window)
        {
            int i = 0;
            if (Vector256.IsHardwareAccelerated)
            {
                for (; i < window.Length; i += Vector256<ushort>.Count)
                {
                    (Vector256.Create(window[i..]) + Vector256.Create(column[i..])).CopyTo(window[i..]);
                }
            }

            for (; i < window.Length; i++)
            {
                window[i] += column[i];
            }
        }

        private static void Slide(ReadOnlySpan<ushort> added, ReadOnlySpan<ushort> removed, Span<ushort> window)
        {
            ref ushort w = ref MemoryMarshal.GetReference(window);
            ref ushort a = ref MemoryMarshal.GetReference(added);
            ref ushort d = ref MemoryMarshal.GetReference(removed);
            int i = 0;
            if (Vector256.IsHardwareAccelerated)
            {
                for (; i < window.Length; i += Vector256<ushort>.Count)
                {
                    (Vector256.LoadUnsafe(ref w, (nuint)i) + (Vector256.LoadUnsafe(ref a, (nuint)i) - Vector256.LoadUnsafe(ref d, (nuint)i))).StoreUnsafe(ref w, (nuint)i);
                }
            }

            for (; i < window.Length; i++)
            {
                window[i] += (ushort)(added[i] - removed[i]);
            }
        }

        public void Dispose()
        {
            ArrayPool<ushort>.Shared.Return(_columns);
            ArrayPool<ushort>.Shared.Return(_coarseColumns);
            ArrayPool<ushort>.Shared.Return(_window);
            ArrayPool<ushort>.Shared.Return(_coarse);
        }
    }

    /// <summary>Any other type: straight floats, and a selection per pixel and channel.</summary>
    private void SelectBands<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        PixelPlane<TPixel> source = input.Source, destination = input.Destination;
        int r = _r, side = (2 * r) + 1, width = source.Width;
        int padded = (width + (2 * r)) * 4, n = side * side;
        int channels = _includeAlpha || !TPixel.Layout.HasAlpha || TPixel.Layout.ChannelOrder == PixelChannelOrder.Cmyk ? 4 : 3;
        input.ForEachBand((start, end) =>
        {
            float[] ring = ArrayPool<float>.Shared.Rent(side * padded);
            float[] window = ArrayPool<float>.Shared.Rent(n);
            float[] output = ArrayPool<float>.Shared.Rent(width * 4);
            try
            {
                for (int v = start - r; v < start + r; v++)
                {
                    LoadFloats(source, v, r, ring.AsSpan(Slot(v, side) * padded, padded));
                }

                for (int y = start; y < end; y++)
                {
                    LoadFloats(source, y + r, r, ring.AsSpan(Slot(y + r, side) * padded, padded));
                    ReadOnlySpan<float> center = ring.AsSpan((Slot(y, side) * padded) + (r * 4), width * 4);
                    center.CopyTo(output);
                    for (int x = 0; x < width; x++)
                    {
                        for (int c = 0; c < channels; c++)
                        {
                            int k = 0;
                            for (int dy = -r; dy <= r; dy++)
                            {
                                int row = (Slot(y + dy, side) * padded) + (x * 4) + c;
                                for (int dx = 0; dx < side; dx++)
                                {
                                    window[k++] = ring[row + (dx * 4)];
                                }
                            }

                            output[(x * 4) + c] = Select(window.AsSpan(0, n), n / 2);
                        }
                    }

                    StoredRows<TPixel>.Store(output.AsSpan(0, width * 4), destination.Row(y));
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(ring);
                ArrayPool<float>.Shared.Return(window);
                ArrayPool<float>.Shared.Return(output);
            }
        });
    }

    private static void LoadFloats<TPixel>(PixelPlane<TPixel> source, int v, int r, Span<float> padded)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int width = source.Width;
        StoredRows<TPixel>.Load(source.Row(Math.Clamp(v, 0, source.Height - 1)), padded.Slice(r * 4, width * 4));
        ReadOnlySpan<float> first = padded.Slice(r * 4, 4), last = padded.Slice((r + width - 1) * 4, 4);
        for (int i = 0; i < r; i++)
        {
            first.CopyTo(padded[(i * 4)..]);
            last.CopyTo(padded[((r + width + i) * 4)..]);
        }
    }

    /// <summary>Returns the <paramref name="k"/>-th smallest value (from 0), reordering <paramref name="values"/>.</summary>
    internal static float Select(Span<float> values, int k)
    {
        int low = 0, high = values.Length - 1;
        while (low < high)
        {
            float pivot = values[(low + high) >>> 1];
            int i = low, j = high;
            while (i <= j)
            {
                while (values[i] < pivot)
                {
                    i++;
                }

                while (values[j] > pivot)
                {
                    j--;
                }

                if (i <= j)
                {
                    (values[i], values[j]) = (values[j], values[i]);
                    i++;
                    j--;
                }
            }

            if (k <= j)
            {
                high = j;
            }
            else if (k >= i)
            {
                low = i;
            }
            else
            {
                break;
            }
        }

        return values[k];
    }
}
