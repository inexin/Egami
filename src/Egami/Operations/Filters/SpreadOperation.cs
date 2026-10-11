
using Egami.Operations.Geometry;
using Egami.Pixels;

namespace Egami.Operations.Filters;

/// <summary>
/// Spread, as ImageMagick's -spread: each pixel takes the stored value of a random neighbour within the radius (a whole-pixel
/// offset in the disc, edges repeating the edge pixel). The offset is a hash of (seed, x, y) by SplitMix64 (Steele, Lea and Flood,
/// 2014), drawn again while it falls outside the disc, so the result does not depend on threads, bands or scopes. Positions are the
/// whole image's, so inside a scope it runs on the full plane, reading a copy of the band around the area.
/// </summary>
internal sealed class SpreadOperation(float radius, ulong seed) : IRasterOperation, INeedsNewPlane, Scopes.IAreaStep
{
    /// <summary>The largest radius.</summary>
    internal const float MaxRadius = 1000;

    private const int Attempts = 64;

    public bool NeedsNewPlane(Size input) => true;

    public Size OutputSize(Size input) => input;

    /// <summary>Returns the offset pixel (<paramref name="x"/>, <paramref name="y"/>) takes its value from.</summary>
    internal static (int Dx, int Dy) Offset(ulong seed, int x, int y, float radius)
    {
        int reach = (int)MathF.Floor(radius), side = (2 * reach) + 1;
        float limit = radius * radius;
        ulong key = Mix(seed + Mix(((ulong)(uint)y << 32) | (uint)x));
        for (ulong k = 0; k < Attempts; k++)
        {
            ulong h = Mix(key + (k * 0x9E3779B97F4A7C15UL));
            int dx = (int)((uint)h % (uint)side) - reach, dy = (int)((uint)(h >> 32) % (uint)side) - reach;
            if ((dx * dx) + (dy * dy) <= limit)
            {
                return (dx, dy);
            }
        }

        return (0, 0);
    }

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Process(input, input.Source, new Point(0, 0), new Rect(0, 0, input.Destination.Width, input.Destination.Height));
    }

    public void RunArea<TPixel>(OperationInput<TPixel> input, Rect area)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        PixelPlane<TPixel> plane = input.Destination;
        area = area.Intersect(new Rect(0, 0, plane.Width, plane.Height));
        if (area.IsEmpty)
        {
            return;
        }

        // The pixels the area reads, copied before any of them change.
        int reach = (int)MathF.Floor(radius);
        Rect band = new Rect(area.X - reach, area.Y - reach, area.Width + (2 * reach), area.Height + (2 * reach)).Intersect(new Rect(0, 0, plane.Width, plane.Height));
        PixelPlane<TPixel> copy = Scopes.ScopeRunner.Scratch(input, band.Width, band.Height);
        try
        {
            Scopes.ScopeRunner.Copy(plane, band, copy, new Point(0, 0));
            Process(input, copy, new Point(band.X, band.Y), area);
        }
        finally
        {
            copy.Dispose();
        }
    }

    private static ulong Mix(ulong z)
    {
        z += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Writes <paramref name="area"/> of the destination from <paramref name="source"/>, whose pixel (0, 0) is the image's <paramref name="at"/>.</summary>
    private void Process<TPixel>(OperationInput<TPixel> input, PixelPlane<TPixel> source, Point at, Rect area)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        PixelPlane<TPixel> destination = input.Destination;
        int width = destination.Width, height = destination.Height;
        RowRunner.Run(area.Height, (long)area.Width * System.Runtime.CompilerServices.Unsafe.SizeOf<TPixel>(), input.Context.MaxParallelism, (start, end) =>
        {
            for (int j = start; j < end; j++)
            {
                int y = area.Y + j;
                Span<TPixel> output = destination.Row(y);
                for (int x = area.X; x < area.Right; x++)
                {
                    (int dx, int dy) = Offset(seed, x, y, radius);
                    int sx = Math.Clamp(x + dx, 0, width - 1) - at.X, sy = Math.Clamp(y + dy, 0, height - 1) - at.Y;
                    output[x] = source.Row(sy)[sx];
                }
            }
        }, input.CancellationToken);
    }
}
