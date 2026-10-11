using System.Buffers;
using System.Numerics;

using Egami.Operations.Resampling;
using Egami.Pixels;

namespace Egami.Operations.Geometry;

/// <summary>
/// Runs an <see cref="IWarp"/>: maps each output row to source points and samples them with the footprint sampler. A pixel's
/// footprint comes from the points its neighbours map to, in a rolling window of three mapped rows; of the two neighbours on
/// each axis the closer mapped one is used, so a seam where the mapping tears (Polar's) does not blur.
/// </summary>
internal sealed class WarpOperation(IWarp warp, SamplingOptions options, Colors.LinearCurve? curve = null)
    : IRasterOperation, INeedsNewPlane, IUpdatesMetadata, ILinearLight
{
    /// <summary>Gets the warp.</summary>
    internal IWarp Warp => warp;

    public bool UsesLinearLight => options.ColorSpace == ColorSpace.Linear;

    public IRasterOperation WithCurve(Colors.LinearCurve linear) => new WarpOperation(warp, options, linear);

    public Size OutputSize(Size input) => warp.OutputSize(input);

    public bool NeedsNewPlane(Size input) => true;

    public void UpdateMetadata(Metadata.RasterMetadata metadata, Size input, Size output)
    {
        if (output != input)
        {
            ExifUpkeep.Dimensions(metadata, output);
            ExifUpkeep.DropThumbnail(metadata);
        }

        // A warp can move the subject anywhere, so its area is dropped.
        ExifUpkeep.Subject(metadata, null);
    }

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Colors.LinearCurve? linear = options.ColorSpace == ColorSpace.Linear ? curve ?? Colors.LinearCurve.Srgb : null;
        SamplerSettings settings = new(options.Filter, options.Edges, options.Background, linear);
        PixelPlane<TPixel> source = input.Source;
        PixelPlane<TPixel> destination = input.Destination;
        Size size = new(source.Width, source.Height);
        RowConverter<TPixel> converter = new(linear);
        int width = destination.Width, height = destination.Height;
        input.ForEachBand((start, end) =>
        {
            float[] rowBuffer = ArrayPool<float>.Shared.Rent(width * 4);
            Vector2[] mapped = ArrayPool<Vector2>.Shared.Rent(width * 3);
            try
            {
                FootprintSampler<TPixel> sampler = new(source, settings);
                Span<float> row = rowBuffer.AsSpan(0, width * 4);

                // Rows y − 1, y and y + 1 rotate through three slots, each remembering which row it holds.
                Span<int> slotRow = stackalloc int[] { -1, -1, -1 };
                for (int y = start; y < end; y++)
                {
                    Span<Vector2> current = Slot(warp, mapped, slotRow, y, width, size);
                    ReadOnlySpan<Vector2> above = y > 0 ? Slot(warp, mapped, slotRow, y - 1, width, size) : default;
                    ReadOnlySpan<Vector2> below = y + 1 < height ? Slot(warp, mapped, slotRow, y + 1, width, size) : default;
                    for (int x = 0; x < width; x++)
                    {
                        Vector2 p = current[x];
                        Vector4 sum;
                        if (!float.IsFinite(p.X) || !float.IsFinite(p.Y))
                        {
                            sum = sampler.Background;
                        }
                        else if (settings.Taps == 1)
                        {
                            sum = sampler.Nearest(p.X - 0.5, p.Y - 0.5);
                        }
                        else
                        {
                            Vector2 dx = Closer(p, x > 0 ? current[x - 1] : Missing, x + 1 < width ? current[x + 1] : Missing, Vector2.UnitX);
                            Vector2 dy = Closer(p, above.IsEmpty ? Missing : above[x], below.IsEmpty ? Missing : below[x], Vector2.UnitY);
                            Footprint footprint = settings.FootprintOf(dx.X, dy.X, dx.Y, dy.Y);
                            sum = sampler.Sample(p.X - 0.5, p.Y - 0.5, footprint);
                        }

                        sum.CopyTo(row[(x * 4)..]);
                    }

                    converter.Store(row, destination.Row(y), opaque: false);
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(rowBuffer);
                ArrayPool<Vector2>.Shared.Return(mapped);
            }
        });
    }

    /// <summary>Returns row <paramref name="y"/>'s mapped points, mapping the row into its slot unless the slot holds it already.</summary>
    private static Span<Vector2> Slot(IWarp warp, Vector2[] mapped, Span<int> slotRow, int y, int width, Size size)
    {
        int slot = y % 3;
        Span<Vector2> points = mapped.AsSpan(slot * width, width);
        if (slotRow[slot] != y)
        {
            warp.Map(y, size, points);
            slotRow[slot] = y;
        }

        return points;
    }

    private static Vector2 Missing => new(float.NaN, float.NaN);

    /// <summary>
    /// Returns how far the source point moves for one output pixel along an axis: the difference to the closer of the two mapped
    /// neighbours (negated for the one before), or <paramref name="fallback"/> when neither maps to a point.
    /// </summary>
    private static Vector2 Closer(Vector2 p, Vector2 before, Vector2 after, Vector2 fallback)
    {
        bool hasBefore = float.IsFinite(before.X) && float.IsFinite(before.Y);
        bool hasAfter = float.IsFinite(after.X) && float.IsFinite(after.Y);
        Vector2 back = p - before;
        Vector2 forward = after - p;
        if (hasBefore && hasAfter)
        {
            return back.LengthSquared() <= forward.LengthSquared() ? back : forward;
        }

        return hasAfter ? forward : hasBefore ? back : fallback;
    }
}

/// <summary>Each pixel samples from its own position moved by a displacement map's channels, as Skia's DisplacementMap does.</summary>
internal sealed class DisplaceWarp(Raster map, float scaleX, float scaleY, ColorChannel xChannel, ColorChannel yChannel) : IWarp
{
    public Size OutputSize(Size input)
    {
        if (map.Width != input.Width || map.Height != input.Height)
        {
            throw new ArgumentException($"The displacement map is {map.Width}×{map.Height} but the image is {input.Width}×{input.Height}; resize the map to the image's size first.");
        }

        return input;
    }

    public void Map(int y, Size input, Span<Vector2> source)
    {
        RgbaF32[] buffer = ArrayPool<RgbaF32>.Shared.Rent(source.Length);
        try
        {
            Span<RgbaF32> row = buffer.AsSpan(0, source.Length);
            map.ReadRow(y, 0, row);
            float cy = y + 0.5f;
            for (int x = 0; x < source.Length; x++)
            {
                RgbaF32 m = row[x];
                source[x] = new Vector2(x + 0.5f + (scaleX * (Channel(m, xChannel) - 0.5f)), cy + (scaleY * (Channel(m, yChannel) - 0.5f)));
            }
        }
        finally
        {
            ArrayPool<RgbaF32>.Shared.Return(buffer);
        }
    }

    private static float Channel(RgbaF32 p, ColorChannel channel) => channel switch
    {
        ColorChannel.Red => p.R,
        ColorChannel.Green => p.G,
        ColorChannel.Blue => p.B,
        ColorChannel.Alpha => p.A,
        _ => (0.2126f * p.R) + (0.7152f * p.G) + (0.0722f * p.B),
    };
}

/// <summary>Moves pixels by whole pixels with no resampling; what leaves one side comes in by the edge mode.</summary>
internal sealed class OffsetOperation(int dx, int dy, EdgeMode edges, Rgba8 background) : IRasterOperation, INeedsNewPlane, IUpdatesMetadata
{
    public Size OutputSize(Size input) => input;

    public bool NeedsNewPlane(Size input) => true;

    public void UpdateMetadata(Metadata.RasterMetadata metadata, Size input, Size output)
    {
        ExifUpkeep.Subject(metadata, null);
    }

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        PixelPlane<TPixel> source = input.Source;
        PixelPlane<TPixel> destination = input.Destination;
        int width = source.Width, height = source.Height;
        TPixel fill = TPixel.FromRgba8(background);
        input.ForEachBand((start, end) =>
        {
            for (int y = start; y < end; y++)
            {
                Span<TPixel> target = destination.Row(y);
                int sy = Fold(y - dy, height);
                if (sy < 0)
                {
                    target.Fill(fill);
                    continue;
                }

                ReadOnlySpan<TPixel> row = source.Row(sy);
                for (int x = 0; x < width; x++)
                {
                    int sx = Fold(x - dx, width);
                    target[x] = sx < 0 ? fill : row[sx];
                }
            }
        });
    }

    /// <summary>Folds an index into 0..size − 1 by the edge mode; −1 for Transparent outside.</summary>
    private int Fold(int index, int size)
    {
        if ((uint)index < (uint)size)
        {
            return index;
        }

        switch (edges)
        {
            case EdgeMode.Wrap:
                int wrapped = index % size;
                return wrapped < 0 ? wrapped + size : wrapped;
            case EdgeMode.Mirror:
                int period = 2 * size;
                int m = index % period;
                m = m < 0 ? m + period : m;
                return m < size ? m : period - 1 - m;
            case EdgeMode.Clamp:
                return Math.Clamp(index, 0, size - 1);
            default:
                return -1;
        }
    }
}

/// <summary>A new canvas filled by repeating the image, or a rectangle of it, with a copy's top-left corner at the origin.</summary>
internal sealed class TileOperation(int width, int height, Point origin, Rect? area) : IRasterOperation, INeedsNewPlane, IUpdatesMetadata
{
    public Size OutputSize(Size input)
    {
        Rect source = area ?? new Rect(0, 0, input.Width, input.Height);
        if (!new Rect(0, 0, input.Width, input.Height).Contains(source))
        {
            throw new ArgumentOutOfRangeException($"The tile's source rectangle {source} leaves the {input.Width}×{input.Height} image.", innerException: null);
        }

        return new Size(width, height);
    }

    public bool NeedsNewPlane(Size input) => true;

    public void UpdateMetadata(Metadata.RasterMetadata metadata, Size input, Size output)
    {
        ExifUpkeep.Dimensions(metadata, output);
        ExifUpkeep.DropThumbnail(metadata);
        ExifUpkeep.Subject(metadata, null);
    }

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        PixelPlane<TPixel> source = input.Source;
        PixelPlane<TPixel> destination = input.Destination;
        Rect r = area ?? new Rect(0, 0, source.Width, source.Height);
        input.ForEachBand((start, end) =>
        {
            for (int y = start; y < end; y++)
            {
                int ty = Mod(y - origin.Y, r.Height);
                ReadOnlySpan<TPixel> row = source.Row(r.Y + ty).Slice(r.X, r.Width);
                Span<TPixel> target = destination.Row(y);

                // The first, partial copy, then whole copies, then the last partial one.
                int x = 0;
                int offset = Mod(-origin.X, r.Width);
                while (x < target.Length)
                {
                    int count = Math.Min(r.Width - offset, target.Length - x);
                    row.Slice(offset, count).CopyTo(target[x..]);
                    x += count;
                    offset = 0;
                }
            }
        });
    }

    private static int Mod(int value, int size)
    {
        int m = value % size;
        return m < 0 ? m + size : m;
    }
}
