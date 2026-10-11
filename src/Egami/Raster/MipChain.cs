using System.Buffers;

using Egami.Operations;
using Egami.Operations.Geometry;
using Egami.Operations.Resampling;
using Egami.Pixels;

namespace Egami;

/// <summary>
/// A chain of ever-halved copies of an image, for making many smaller sizes fast: each resize starts from the smallest level that
/// is still at least twice the target, so a gallery of thumbnail sizes reads each source pixel about once instead of once per size.
/// Level 0 is the source itself; each next level halves the size, rounding up, by averaging 2 × 2 blocks of premultiplied pixels.
/// </summary>
/// <remarks>
/// The levels together take a third more memory than the source. Every frame of an animation gets its levels. The chain does
/// not copy or dispose the source, which must outlive it; disposing the chain frees the levels it made.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Crops.cs" region="mip-chain"/>
/// </example>
public sealed class MipChain : IDisposable
{
    private readonly List<Raster> _levels;
    private readonly ColorSpace _colorSpace;
    private bool _disposed;

    private MipChain(List<Raster> levels, ColorSpace colorSpace)
    {
        _levels = levels;
        _colorSpace = colorSpace;
    }

    /// <summary>Gets the levels, from the source (level 0) down to 1 × 1.</summary>
    /// <exception cref="ObjectDisposedException">The chain has been disposed.</exception>
    public IReadOnlyList<Raster> Levels
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _levels;
        }
    }

    /// <summary>Builds the chain for <paramref name="source"/>, down to a 1 × 1 level.</summary>
    /// <param name="source">The image; it becomes level 0 as it is, so it must outlive the chain.</param>
    /// <param name="colorSpace">Where the levels and the resizes from them blend: the stored values (the default) or linear light.</param>
    /// <returns>The chain. The caller must dispose it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="colorSpace"/> is not defined.</exception>
    /// <exception cref="LimitExceededException">The levels would break the context's <see cref="SafetyLimits.MaxTotalBytes"/>.</exception>
    public static MipChain Build(Raster source, ColorSpace colorSpace = ColorSpace.Encoded)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!Enum.IsDefined(colorSpace))
        {
            throw new ArgumentOutOfRangeException(nameof(colorSpace), colorSpace, "The color space is not defined.");
        }

        List<Raster> levels = [source];
        try
        {
            Colors.LinearCurve? curve = colorSpace == ColorSpace.Linear ? Colors.LinearCurve.For(source.Metadata.GetColorProfile().Transfer) : null;
            while (levels[^1].Width > 1 || levels[^1].Height > 1)
            {
                levels.Add(levels[^1].Accept(new Halve(curve)));
            }
        }
        catch
        {
            for (int i = 1; i < levels.Count; i++)
            {
                levels[i].Dispose();
            }

            throw;
        }

        return new MipChain(levels, colorSpace);
    }

    /// <summary>
    /// Returns the image resized to exactly <paramref name="width"/> × <paramref name="height"/>, from the smallest level at least
    /// twice that size in both directions (the source when there is none), as <see cref="RasterOps.Resize"/> would from the source.
    /// </summary>
    /// <param name="width">The width, at least 1.</param>
    /// <param name="height">The height, at least 1.</param>
    /// <param name="filter">The resampling kernel.</param>
    /// <returns>The new raster, with the source's pixel type and metadata. The caller must dispose it.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A size is less than 1, or the filter is not defined.</exception>
    /// <exception cref="ObjectDisposedException">The chain has been disposed.</exception>
    public Raster Resize(int width, int height, ResampleFilter filter = ResampleFilter.Bicubic)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (!Enum.IsDefined(filter))
        {
            throw new ArgumentOutOfRangeException(nameof(filter), filter, "The filter is not defined.");
        }

        Raster level = LevelFor(width, height);
        return level.Derive(ops => ops.Resize(width, height, filter, _colorSpace));
    }

    /// <summary>
    /// Returns the image fitted inside <paramref name="width"/> × <paramref name="height"/> with its aspect ratio kept, the same
    /// size <see cref="RasterOps.Fit"/> gives from the source, resized from the smallest level at least twice that size.
    /// </summary>
    /// <param name="width">The box width, at least 1.</param>
    /// <param name="height">The box height, at least 1.</param>
    /// <param name="filter">The resampling kernel.</param>
    /// <param name="enlarge">Whether an image smaller than the box is scaled up; <see langword="false"/> returns it at its own size.</param>
    /// <returns>The new raster. The caller must dispose it.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A size is less than 1, or the filter is not defined.</exception>
    /// <exception cref="ObjectDisposedException">The chain has been disposed.</exception>
    public Raster Fit(int width, int height, ResampleFilter filter = ResampleFilter.Bicubic, bool enlarge = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        Raster source = _levels[0];
        double fit = Math.Min(width / (double)source.Width, height / (double)source.Height);
        if (!enlarge && fit >= 1)
        {
            return Resize(source.Width, source.Height, filter);
        }

        return Resize(
            Math.Clamp((int)Math.Round(source.Width * fit), 1, width),
            Math.Clamp((int)Math.Round(source.Height * fit), 1, height),
            filter);
    }

    /// <summary>Frees the levels the chain made; the source is left alone.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        for (int i = 1; i < _levels.Count; i++)
        {
            _levels[i].Dispose();
        }
    }

    private Raster LevelFor(int width, int height)
    {
        Raster chosen = _levels[0];
        foreach (Raster level in _levels)
        {
            if (level.Width >= 2 * width && level.Height >= 2 * height)
            {
                chosen = level;
            }
        }

        return chosen;
    }

    /// <summary>Makes the next level: each pixel averages a 2 × 2 block (fewer at an odd edge), premultiplied.</summary>
    private sealed class Halve(Colors.LinearCurve? curve) : IRasterVisitor<Raster>
    {
        public Raster Visit<TPixel>(Raster<TPixel> source)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            int width = (source.Width + 1) / 2, height = (source.Height + 1) / 2;
            BudgetedMemorySource memory = new(source.Context.Memory, source.Context.Limits.MaxTotalBytes);
            List<Frame<TPixel>> frames = new(source.Frames.Count);
            try
            {
                for (int f = 0; f < source.Frames.Count; f++)
                {
                    PixelPlane<TPixel> plane = PixelPlane.Allocate<TPixel>(width, height, memory);
                    frames.Add(new Frame<TPixel>(plane, source.Frames[f].Metadata.Copy()));
                    Reduce(source.Frames[f].Plane, plane, source.Context.MaxParallelism);
                }
            }
            catch
            {
                foreach (Frame<TPixel> frame in frames)
                {
                    frame.Plane.Dispose();
                }

                throw;
            }

            Metadata.RasterMetadata metadata = source.Metadata.Copy();
            ExifUpkeep.Dimensions(metadata, new Size(width, height));
            ExifUpkeep.DropThumbnail(metadata);
            Raster<TPixel> level = new(frames, metadata, source.Context);
            return level;
        }

        private void Reduce<TPixel>(PixelPlane<TPixel> source, PixelPlane<TPixel> destination, int maxParallelism)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            RowConverter<TPixel> converter = new(curve);
            int sw = source.Width, sh = source.Height, width = destination.Width;
            RowRunner.Run(destination.Height, (long)width * 16, maxParallelism, (start, end) =>
            {
                float[] buffer = ArrayPool<float>.Shared.Rent((sw * 8) + (width * 4));
                try
                {
                    Span<float> top = buffer.AsSpan(0, sw * 4);
                    Span<float> bottom = buffer.AsSpan(sw * 4, sw * 4);
                    Span<float> output = buffer.AsSpan(sw * 8, width * 4);
                    for (int y = start; y < end; y++)
                    {
                        bool opaque = converter.Load(source.Row(2 * y), top);
                        bool two = (2 * y) + 1 < sh;
                        if (two)
                        {
                            opaque &= converter.Load(source.Row((2 * y) + 1), bottom);
                        }

                        for (int x = 0; x < width; x++)
                        {
                            int a = 8 * x;
                            bool pair = (2 * x) + 1 < sw;
                            float scale = 1f / ((pair ? 2 : 1) * (two ? 2 : 1));
                            for (int c = 0; c < 4; c++)
                            {
                                float sum = top[a + c] + (pair ? top[a + 4 + c] : 0);
                                if (two)
                                {
                                    sum += bottom[a + c] + (pair ? bottom[a + 4 + c] : 0);
                                }

                                output[(4 * x) + c] = sum * scale;
                            }
                        }

                        converter.Store(output, destination.Row(y), opaque);
                    }
                }
                finally
                {
                    ArrayPool<float>.Shared.Return(buffer);
                }
            }, CancellationToken.None);
        }
    }
}
