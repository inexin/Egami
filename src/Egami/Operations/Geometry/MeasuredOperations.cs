using System.Buffers;

using Egami.Operations.Compose;
using Egami.Pixels;

namespace Egami.Operations.Geometry;

/// <summary>A step that keeps every pixel: what a measured step becomes when it finds nothing to change.</summary>
internal sealed class KeepOperation : IRasterOperation
{
    internal static KeepOperation Instance { get; } = new();

    public Size OutputSize(Size input) => input;

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        OperationRows.CopyIfSeparate(input);
    }
}

/// <summary>
/// Trim: removes border rows and columns whose pixels all lie within the tolerance of the border color, by 2.5.1's -fuzz
/// distance, as ImageMagick's -trim does. Frames are cropped to the union of their rectangles.
/// </summary>
internal sealed class TrimOperation(float tolerance, Rgba8? color) : IMeasuredStep
{
    public Size OutputSize(Size input) => throw new InvalidOperationException("Trim is measured, so it is never sized up front.");

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        throw new InvalidOperationException("Trim is measured, so it never runs itself.");
    }

    public IRasterOperation Measure<TPixel>(IReadOnlyList<PixelPlane<TPixel>> frames, EgamiContext context, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Rect union = default;
        foreach (PixelPlane<TPixel> frame in frames)
        {
            union = Bounds.Union(union, Bounds.Trim(frame, tolerance, color, context.MaxParallelism, cancellationToken));
        }

        return Bounds.CropOrKeep(union, new Size(frames[0].Width, frames[0].Height));
    }
}

/// <summary>EntropyCrop: crops to the bounding box of the 16 × 16 tiles whose luminance entropy reaches the threshold.</summary>
internal sealed class EntropyCropOperation(float threshold) : IMeasuredStep
{
    public Size OutputSize(Size input) => throw new InvalidOperationException("EntropyCrop is measured, so it is never sized up front.");

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        throw new InvalidOperationException("EntropyCrop is measured, so it never runs itself.");
    }

    public IRasterOperation Measure<TPixel>(IReadOnlyList<PixelPlane<TPixel>> frames, EgamiContext context, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Rect union = default;
        foreach (PixelPlane<TPixel> frame in frames)
        {
            union = Bounds.Union(union, Bounds.Entropy(frame, threshold, context.MaxParallelism, cancellationToken));
        }

        return Bounds.CropOrKeep(union, new Size(frames[0].Width, frames[0].Height));
    }
}

/// <summary>The content-measuring rectangles behind Trim, EntropyCrop and their Raster helpers.</summary>
internal static class Bounds
{
    /// <summary>The side of an entropy tile, in pixels.</summary>
    internal const int EntropyTile = 16;

    /// <summary>Returns the smallest rectangle holding both; an empty rectangle counts as nothing.</summary>
    internal static Rect Union(Rect a, Rect b)
    {
        if (a.IsEmpty)
        {
            return b;
        }

        if (b.IsEmpty)
        {
            return a;
        }

        int left = Math.Min(a.X, b.X), top = Math.Min(a.Y, b.Y);
        return new Rect(left, top, Math.Max(a.Right, b.Right) - left, Math.Max(a.Bottom, b.Bottom) - top);
    }

    /// <summary>A crop to <paramref name="rect"/>, or a step that keeps everything when it is empty or the whole image.</summary>
    internal static IRasterOperation CropOrKeep(Rect rect, Size size)
    {
        return rect.IsEmpty || rect == new Rect(0, 0, size.Width, size.Height) ? KeepOperation.Instance : new CropOperation(rect);
    }

    /// <summary>
    /// Returns the rectangle of pixels farther than <paramref name="tolerance"/> from the border color (the given color, or the
    /// top-left pixel's), or an empty rectangle when every pixel is border.
    /// </summary>
    internal static Rect Trim<TPixel>(PixelPlane<TPixel> plane, float tolerance, Rgba8? color, int maxParallelism, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int width = plane.Width;
        RgbaF32 reference;
        if (color is Rgba8 given)
        {
            reference = RgbaF32.FromRgba8(given);
        }
        else
        {
            Span<RgbaF32> first = stackalloc RgbaF32[1];
            PixelConvert.Convert<TPixel, RgbaF32>(plane.Row(0)[..1], first);
            reference = first[0];
        }

        FuzzColor target = new(reference);
        double threshold = FuzzColor.Threshold(tolerance);
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        object gate = new();
        RowRunner.Run(plane.Height, (long)width * 16, maxParallelism, (start, end) =>
        {
            int bandLeft = int.MaxValue, bandTop = int.MaxValue, bandRight = -1, bandBottom = -1;
            RgbaF32[] buffer = ArrayPool<RgbaF32>.Shared.Rent(width);
            try
            {
                Span<RgbaF32> row = buffer.AsSpan(0, width);
                for (int y = start; y < end; y++)
                {
                    PixelConvert.Convert<TPixel, RgbaF32>(plane.Row(y), row);
                    int x0 = 0;
                    while (x0 < width && target.Distance2(row[x0]) <= threshold)
                    {
                        x0++;
                    }

                    if (x0 == width)
                    {
                        continue;
                    }

                    int x1 = width - 1;
                    while (x1 > x0 && target.Distance2(row[x1]) <= threshold)
                    {
                        x1--;
                    }

                    bandLeft = Math.Min(bandLeft, x0);
                    bandRight = Math.Max(bandRight, x1);
                    bandTop = Math.Min(bandTop, y);
                    bandBottom = Math.Max(bandBottom, y);
                }
            }
            finally
            {
                ArrayPool<RgbaF32>.Shared.Return(buffer);
            }

            lock (gate)
            {
                left = Math.Min(left, bandLeft);
                right = Math.Max(right, bandRight);
                top = Math.Min(top, bandTop);
                bottom = Math.Max(bottom, bandBottom);
            }
        }, cancellationToken);

        return right < 0 ? default : new Rect(left, top, right - left + 1, bottom - top + 1);
    }

    /// <summary>
    /// Splits the luminance into 16 × 16 tiles, scores each by the Shannon entropy of its 256-bin histogram over 8 bits, and
    /// returns the bounding box of the tiles at or above <paramref name="threshold"/>, clipped to the image; empty when no tile is.
    /// </summary>
    internal static Rect Entropy<TPixel>(PixelPlane<TPixel> plane, float threshold, int maxParallelism, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int width = plane.Width, height = plane.Height;
        int tilesX = (width + EntropyTile - 1) / EntropyTile;
        int tilesY = (height + EntropyTile - 1) / EntropyTile;
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        object gate = new();

        // One band is one row of tiles; RowRunner bands rows, so it runs over tile rows with each tile row's bytes as its weight.
        RowRunner.Run(tilesY, (long)width * EntropyTile * 4, maxParallelism, (start, end) =>
        {
            int[] histograms = ArrayPool<int>.Shared.Rent(tilesX * 256);
            Rgba8[] buffer = ArrayPool<Rgba8>.Shared.Rent(width);
            int bandLeft = int.MaxValue, bandTop = int.MaxValue, bandRight = -1, bandBottom = -1;
            try
            {
                Span<Rgba8> row = buffer.AsSpan(0, width);
                for (int ty = start; ty < end; ty++)
                {
                    Span<int> counts = histograms.AsSpan(0, tilesX * 256);
                    counts.Clear();
                    int y0 = ty * EntropyTile, y1 = Math.Min(height, y0 + EntropyTile);
                    for (int y = y0; y < y1; y++)
                    {
                        PixelConvert.Convert<TPixel, Rgba8>(plane.Row(y), row);
                        for (int x = 0; x < width; x++)
                        {
                            Rgba8 p = row[x];
                            counts[((x / EntropyTile) * 256) + Luma(p)]++;
                        }
                    }

                    for (int tx = 0; tx < tilesX; tx++)
                    {
                        int pixels = (Math.Min(width, (tx + 1) * EntropyTile) - (tx * EntropyTile)) * (y1 - y0);
                        if (Score(counts.Slice(tx * 256, 256), pixels) >= threshold)
                        {
                            bandLeft = Math.Min(bandLeft, tx);
                            bandRight = Math.Max(bandRight, tx);
                            bandTop = Math.Min(bandTop, ty);
                            bandBottom = Math.Max(bandBottom, ty);
                        }
                    }
                }
            }
            finally
            {
                ArrayPool<int>.Shared.Return(histograms);
                ArrayPool<Rgba8>.Shared.Return(buffer);
            }

            lock (gate)
            {
                left = Math.Min(left, bandLeft);
                right = Math.Max(right, bandRight);
                top = Math.Min(top, bandTop);
                bottom = Math.Max(bottom, bandBottom);
            }
        }, cancellationToken);

        if (right < 0)
        {
            return default;
        }

        int x = left * EntropyTile, y = top * EntropyTile;
        return new Rect(x, y, Math.Min(width, (right + 1) * EntropyTile) - x, Math.Min(height, (bottom + 1) * EntropyTile) - y);
    }

    /// <summary>The Shannon entropy of a histogram, in bits, divided by the 8 bits a 256-bin histogram can reach.</summary>
    internal static double Score(ReadOnlySpan<int> histogram, int pixels)
    {
        double entropy = 0;
        foreach (int count in histogram)
        {
            if (count > 0)
            {
                double p = count / (double)pixels;
                entropy -= p * Math.Log2(p);
            }
        }

        return entropy / 8;
    }

    /// <summary>Rec. 709 luma of the encoded values, 0..255, with weights 54, 183 and 19 out of 256.</summary>
    private static int Luma(Rgba8 p) => ((54 * p.R) + (183 * p.G) + (19 * p.B) + 128) >> 8;
}
