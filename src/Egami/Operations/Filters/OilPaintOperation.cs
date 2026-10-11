using System.Buffers;
using System.Runtime.InteropServices;

using Egami.Operations.Geometry;
using Egami.Operations.Resampling;
using Egami.Pixels;

namespace Egami.Operations.Filters;

/// <summary>
/// Oil paint, as ImageMagick's -paint defines it: over the (2r + 1)² square around each pixel (edges repeating the edge pixel),
/// every pixel falls in an intensity bin, and the output is a whole pixel of the most common bin, unchanged. Ties and the pixel
/// chosen follow ImageMagick's scan, rows top to bottom and each row left to right, keeping the first pixel at which a bin's
/// count goes past every count before it: so the winner is the bin whose last pixel in the scan comes first among the bins with
/// the highest count, and the output is that last pixel. Intensity is 0.212656 R + 0.715158 G + 0.072186 B on 16-bit values, cast
/// to float and scaled to 256 bins as ImageMagick's Q16 build does; with fewer levels, bins are merged evenly. The histogram slides
/// along each row, adding a column and dropping one, with a count of counts so the highest count is known at once.
/// </summary>
internal sealed class OilPaintOperation(int radius, int levels) : IRasterOperation, INeedsNewPlane, Scopes.INeighbourhoodStep
{
    /// <summary>The largest radius.</summary>
    internal const int MaxRadius = 50;

    public (int X, int Y)? Reach => (radius, radius);

    public bool NeedsNewPlane(Size input) => true;

    public Size OutputSize(Size input) => input;

    /// <summary>Returns ImageMagick's 256-level intensity bin for 16-bit red, green and blue.</summary>
    internal static int Bin256(float red, float green, float blue)
    {
        // GetPixelIntensity's Rec. 709 luma in double, ClampToQuantum to a float quantum, then ScaleQuantumToChar.
        float quantum = (float)((0.212656 * red) + (0.715158 * green) + (0.072186 * blue));
        if (!(quantum > 0f))
        {
            return 0;
        }

        if (quantum / 257.0f >= 255.0f)
        {
            return 255;
        }

        return (int)((quantum / 257.0f) + 0.5f);
    }

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        PixelPlane<TPixel> source = input.Source, destination = input.Destination;
        int r = radius, side = (2 * r) + 1, width = source.Width, height = source.Height, padded = width + (2 * r);
        int window = side * side;
        bool bytes = RowConverter<TPixel>.FourByte;
        int redByte = typeof(TPixel) == typeof(Bgra8) ? 2 : 0, blueByte = 2 - redByte;
        input.ForEachBand((start, end) =>
        {
            byte[] ring = ArrayPool<byte>.Shared.Rent(side * padded);
            float[] floats = bytes ? [] : ArrayPool<float>.Shared.Rent(width * 4);
            int[] histogram = ArrayPool<int>.Shared.Rent(levels);
            int[] counts = ArrayPool<int>.Shared.Rent(window + 1);
            int[] seen = ArrayPool<int>.Shared.Rent(levels);
            int[] offsets = ArrayPool<int>.Shared.Rent(side);
            try
            {
                Span<int> h = histogram.AsSpan(0, levels);
                Span<int> n = counts.AsSpan(0, window + 1);
                // A bin is marked when it holds this pixel's stamp, so marks never need clearing.
                Span<int> marked = seen.AsSpan(0, levels);
                marked.Clear();
                int stamp = 0;
                for (int v = start - r; v < start + r; v++)
                {
                    LoadBins(source, v, ring.AsSpan(Slot(v, side) * padded, padded), floats, bytes, redByte, blueByte);
                }

                for (int y = start; y < end; y++)
                {
                    LoadBins(source, y + r, ring.AsSpan(Slot(y + r, side) * padded, padded), floats, bytes, redByte, blueByte);
                    for (int v = 0; v < side; v++)
                    {
                        offsets[v] = Slot(y - r + v, side) * padded;
                    }

                    h.Clear();
                    n.Clear();
                    n[0] = levels;
                    int max = 0;
                    for (int v = 0; v < side; v++)
                    {
                        ReadOnlySpan<byte> bins = ring.AsSpan(offsets[v], padded);
                        for (int u = 0; u < side; u++)
                        {
                            Add(bins[u], h, n, ref max);
                        }
                    }

                    Span<TPixel> output = destination.Row(y);
                    for (int x = 0; x < width; x++)
                    {
                        // Back from the window's last pixel: each bin at the highest count is met first at its last pixel; the
                        // last of them to be met is the one ImageMagick keeps.
                        int needed = n[max], found = 0, du = 0, dv = 0;
                        stamp++;
                        for (int v = side - 1; v >= 0 && found < needed; v--)
                        {
                            ReadOnlySpan<byte> bins = ring.AsSpan(offsets[v] + x, side);
                            for (int u = side - 1; u >= 0; u--)
                            {
                                int b = bins[u];
                                if (h[b] == max && marked[b] != stamp)
                                {
                                    marked[b] = stamp;
                                    du = u;
                                    dv = v;
                                    if (++found == needed)
                                    {
                                        break;
                                    }
                                }
                            }
                        }

                        output[x] = source.Row(Math.Clamp(y - r + dv, 0, height - 1))[Math.Clamp(x - r + du, 0, width - 1)];
                        if (x + 1 < width)
                        {
                            for (int v = 0; v < side; v++)
                            {
                                ReadOnlySpan<byte> bins = ring.AsSpan(offsets[v], padded);
                                Remove(bins[x], h, n, ref max);
                                Add(bins[x + side], h, n, ref max);
                            }
                        }
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(ring);
                if (!bytes)
                {
                    ArrayPool<float>.Shared.Return(floats);
                }

                ArrayPool<int>.Shared.Return(histogram);
                ArrayPool<int>.Shared.Return(counts);
                ArrayPool<int>.Shared.Return(seen);
                ArrayPool<int>.Shared.Return(offsets);
            }
        });
    }

    private static int Slot(int v, int side) => ((v % side) + side) % side;

    private static void Add(int b, Span<int> h, Span<int> n, ref int max)
    {
        int c = h[b]++;
        n[c]--;
        n[c + 1]++;
        if (c + 1 > max)
        {
            max = c + 1;
        }
    }

    private static void Remove(int b, Span<int> h, Span<int> n, ref int max)
    {
        int c = h[b]--;
        n[c]--;
        n[c - 1]++;
        if (c == max && n[c] == 0)
        {
            max = c - 1;
        }
    }

    /// <summary>Loads virtual row <paramref name="v"/>'s bins, repeating the edge pixels past the image.</summary>
    private void LoadBins<TPixel>(PixelPlane<TPixel> source, int v, Span<byte> bins, float[] floats, bool bytes, int redByte, int blueByte)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int width = source.Width, r = radius;
        ReadOnlySpan<TPixel> row = source.Row(Math.Clamp(v, 0, source.Height - 1));
        Span<byte> middle = bins.Slice(r, width);
        if (bytes)
        {
            ReadOnlySpan<byte> p = MemoryMarshal.AsBytes(row);
            for (int x = 0; x < width; x++)
            {
                int i = x * 4;
                middle[x] = Level(Bin256(p[i + redByte] * 257f, p[i + 1] * 257f, p[i + blueByte] * 257f));
            }
        }
        else
        {
            Span<RgbaF32> straight = MemoryMarshal.Cast<float, RgbaF32>(floats.AsSpan(0, width * 4));
            PixelConvert.Convert(row, straight);
            for (int x = 0; x < width; x++)
            {
                RgbaF32 p = straight[x];
                middle[x] = Level(Bin256(Quantum(p.R), Quantum(p.G), Quantum(p.B)));
            }
        }

        bins[..r].Fill(middle[0]);
        bins.Slice(r + width, r).Fill(middle[width - 1]);
    }

    /// <summary>A 0..1 value as a 16-bit quantum: exact for 8 and 16-bit values.</summary>
    private static float Quantum(float value) => MathF.Round(Math.Clamp(value, 0, 1) * 65535f);

    private byte Level(int bin256) => (byte)(bin256 * levels / 256);
}
