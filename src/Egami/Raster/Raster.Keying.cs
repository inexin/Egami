using System.Buffers;

using Egami.Operations;
using Egami.Operations.Compose;
using Egami.Pixels;

namespace Egami;

/// <summary>Selections by color: masks for <see cref="RasterOps.Within(Mask, Action{RasterOps})"/>.</summary>
public abstract partial class Raster
{
    /// <summary>
    /// Returns a mask of the first frame: 255 where a pixel lies within <paramref name="tolerance"/> of <paramref name="color"/>, 0
    /// elsewhere, by ImageMagick's -fuzz distance. Use it to select by color, then edit only the selection.
    /// </summary>
    /// <param name="color">The color to select, with its alpha.</param>
    /// <param name="tolerance">The color distance, from 0 (the exact color) to 1 (every color).</param>
    /// <returns>The mask, the raster's size. The caller must dispose it.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tolerance"/> is outside 0..1.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Keying.cs" region="select-by-color"/>
    /// </example>
    public Raster<Gray8> ColorMask(Rgba8 color, float tolerance = 0)
    {
        RasterOps.CheckTolerance(tolerance, nameof(tolerance));
        FuzzColor target = new(RgbaF32.FromRgba8(color));
        double threshold = FuzzColor.Threshold(tolerance);
        Raster<Gray8> mask = Create<Gray8>(Width, Height, Context);
        RgbaF32[] buffer = ArrayPool<RgbaF32>.Shared.Rent(Width);
        try
        {
            Span<RgbaF32> row = buffer.AsSpan(0, Width);
            for (int y = 0; y < Height; y++)
            {
                ReadRow(y, 0, row);
                Span<Gray8> output = mask.Row(y);
                for (int x = 0; x < Width; x++)
                {
                    output[x] = new Gray8(target.Distance2(row[x]) <= threshold ? (byte)255 : (byte)0);
                }
            }
        }
        catch
        {
            mask.Dispose();
            throw;
        }
        finally
        {
            ArrayPool<RgbaF32>.Shared.Return(buffer);
        }

        return mask;
    }

    /// <summary>
    /// Returns a mask of the first frame's region connected to <paramref name="seed"/> whose colors lie within
    /// <paramref name="tolerance"/> of the seed's: the "magic wand". 255 inside the region, 0 elsewhere.
    /// </summary>
    /// <param name="seed">Where the region starts.</param>
    /// <param name="tolerance">The color distance, from 0 (the seed's exact color) to 1 (every color).</param>
    /// <param name="connectivity">Whether the region spreads to the four side neighbours or also the four diagonal ones.</param>
    /// <returns>The mask, the raster's size. The caller must dispose it.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The seed lies outside the raster, <paramref name="tolerance"/> is outside 0..1, or <paramref name="connectivity"/> is not defined.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Keying.cs" region="select-by-color"/>
    /// </example>
    public Raster<Gray8> FloodMask(Point seed, float tolerance = 0, Connectivity connectivity = Connectivity.Four)
    {
        RasterOps.CheckTolerance(tolerance, nameof(tolerance));
        RasterOps.CheckConnectivity(connectivity);
        if ((uint)seed.X >= (uint)Width || (uint)seed.Y >= (uint)Height)
        {
            throw new ArgumentOutOfRangeException(nameof(seed), seed, $"The seed lies outside the {Width}×{Height} raster.");
        }

        return Accept(new FloodVisitor(seed, tolerance, connectivity));
    }

    private sealed class FloodVisitor(Point seed, float tolerance, Connectivity connectivity) : IRasterVisitor<Raster<Gray8>>
    {
        public Raster<Gray8> Visit<TPixel>(Raster<TPixel> raster)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            using IMemoryOwner<byte> region = FloodFill.Region(raster.Frames[0].Plane, seed, tolerance, connectivity, raster.Context.Memory);
            ReadOnlySpan<byte> marked = region.Memory.Span;
            Raster<Gray8> mask = Create<Gray8>(raster.Width, raster.Height, raster.Context);
            for (int y = 0; y < raster.Height; y++)
            {
                Span<Gray8> row = mask.Row(y);
                long start = (long)y * raster.Width;
                for (int x = 0; x < row.Length; x++)
                {
                    row[x] = new Gray8(FloodFill.Marked(marked, start + x) ? (byte)255 : (byte)0);
                }
            }

            return mask;
        }
    }
}
