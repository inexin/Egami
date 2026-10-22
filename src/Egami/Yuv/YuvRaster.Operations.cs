using Egami.Metadata;
using Egami.Operations;
using Egami.Pixels;

namespace Egami;

/// <content>Crop and resize on the planes.</content>
public abstract partial class YuvRaster
{
    /// <summary>
    /// Returns a new raster holding <paramref name="rect"/> of this one. The rectangle's corner must fall on a chroma sample: even
    /// x and y for 4:2:0, even x for 4:2:2, x a multiple of 4 for 4:1:1, and x and y multiples of 4 for 4:1:0. Chroma is copied
    /// exactly, with no filtering.
    /// </summary>
    /// <param name="rect">The area to keep, in luma pixels.</param>
    /// <returns>The new raster, with a copy of the metadata. The caller must dispose it.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The rectangle is empty or reaches outside the raster.</exception>
    /// <exception cref="ArgumentException">The rectangle's corner is not on a chroma sample.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/PlanarYuv.cs" region="operations"/>
    /// </example>
    public YuvRaster Crop(Rect rect) => CropCore(rect);

    /// <summary>
    /// Returns a new raster with every plane resized by <paramref name="filter"/>, as a <see cref="Gray8"/> or <see cref="Gray16"/>
    /// resize would, keeping the chroma format, siting and color. Samples are clamped to the bit depth, since some filters
    /// overshoot. Scaling planes before converting costs half as much at 4:2:0 as scaling RGB after.
    /// </summary>
    /// <param name="width">The new luma width, at least 1.</param>
    /// <param name="height">The new luma height, at least 1.</param>
    /// <param name="filter">The resampling kernel. Defaults to <see cref="ResampleFilter.Bicubic"/>.</param>
    /// <returns>The new raster, with a copy of the metadata. The caller must dispose it.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A size is less than 1.</exception>
    /// <exception cref="LimitExceededException">The new raster breaks the context's <see cref="SafetyLimits"/>.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/PlanarYuv.cs" region="operations"/>
    /// </example>
    public YuvRaster Resize(int width, int height, ResampleFilter filter = ResampleFilter.Bicubic) => ResizeCore(width, height, filter);

    /// <summary>
    /// Resizes every plane into an existing raster of the size wanted, as <see cref="Resize(int, int, ResampleFilter)"/> does, so a
    /// video can scale every frame into the same raster. The target must have the same sample type, chroma format, bit depth and
    /// color. An alpha plane is resized when both have one; a target alpha plane with no source alpha is made opaque. The target's
    /// metadata is left alone.
    /// </summary>
    /// <param name="target">The raster to write, of any size.</param>
    /// <param name="filter">The resampling kernel. Defaults to <see cref="ResampleFilter.Bicubic"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The target is this raster, or differs in sample type, chroma format, bit depth or color.</exception>
    /// <exception cref="ObjectDisposedException">Either raster has been disposed.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/PlanarYuv.cs" region="operations"/>
    /// </example>
    public void ResizeTo(YuvRaster target, ResampleFilter filter = ResampleFilter.Bicubic)
    {
        ArgumentNullException.ThrowIfNull(target);
        ThrowIfDisposed();
        target.ThrowIfDisposed();
        if (ReferenceEquals(target, this))
        {
            throw new ArgumentException("The target must be another raster.", nameof(target));
        }

        if (target.GetType() != GetType() || target.ChromaFormat != ChromaFormat || target.BitDepth != BitDepth || target.Color != Color)
        {
            throw new ArgumentException(
                $"The target is {target.ChromaFormat} at {target.BitDepth} bits, {target.Color}; this raster is {ChromaFormat} at {BitDepth} bits, {Color}.", nameof(target));
        }

        ResizeToCore(target, filter);
    }

    private protected abstract YuvRaster CropCore(Rect rect);

    private protected abstract YuvRaster ResizeCore(int width, int height, ResampleFilter filter);

    private protected abstract void ResizeToCore(YuvRaster target, ResampleFilter filter);
}

/// <content>Crop and resize on the planes.</content>
public sealed partial class YuvRaster<TSample>
{
    /// <summary>Returns a new raster holding <paramref name="rect"/> of this one. See <see cref="YuvRaster.Crop(Rect)"/>.</summary>
    /// <param name="rect">The area to keep, in luma pixels.</param>
    /// <returns>The new raster. The caller must dispose it.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The rectangle is empty or reaches outside the raster.</exception>
    /// <exception cref="ArgumentException">The rectangle's corner is not on a chroma sample.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    public new YuvRaster<TSample> Crop(Rect rect)
    {
        ThrowIfDisposed();
        if (rect.Width < 1 || rect.Height < 1 || rect.X < 0 || rect.Y < 0 || rect.X > Width - rect.Width || rect.Y > Height - rect.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(rect), rect, $"The rectangle must be inside the {Width}×{Height} raster.");
        }

        (int sx, int sy) = Subsampling(ChromaFormat);
        if (rect.X % sx != 0 || rect.Y % sy != 0)
        {
            throw new ArgumentException($"A {ChromaFormat} crop must start on a chroma sample: x a multiple of {sx} and y of {sy}.", nameof(rect));
        }

        YuvRaster<TSample> result = Allocate(rect.Width, rect.Height, ChromaFormat, BitDepth, Color, HasAlpha, Metadata.Copy(), Context, ClearMode.None);
        CopyArea(Y, result.Y, rect.X, rect.Y);
        CopyArea(A, result.A, rect.X, rect.Y);
        CopyArea(U, result.U, rect.X / sx, rect.Y / sy);
        CopyArea(V, result.V, rect.X / sx, rect.Y / sy);
        UpdateExif(result.Metadata, rect.Width, rect.Height);
        return result;

        static void CopyArea(YuvPlane<TSample>? from, YuvPlane<TSample>? to, int x, int y)
        {
            for (int row = 0; to is not null && row < to.Height; row++)
            {
                from!.Row(y + row).Slice(x, to.Width).CopyTo(to.Row(row));
            }
        }
    }

    /// <summary>Returns a new raster with every plane resized. See <see cref="YuvRaster.Resize(int, int, ResampleFilter)"/>.</summary>
    /// <param name="width">The new luma width, at least 1.</param>
    /// <param name="height">The new luma height, at least 1.</param>
    /// <param name="filter">The resampling kernel. Defaults to <see cref="ResampleFilter.Bicubic"/>.</param>
    /// <returns>The new raster. The caller must dispose it.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A size is less than 1.</exception>
    /// <exception cref="LimitExceededException">The new raster breaks the context's <see cref="SafetyLimits"/>.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    public new YuvRaster<TSample> Resize(int width, int height, ResampleFilter filter = ResampleFilter.Bicubic)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ThrowIfDisposed();
        YuvRaster<TSample> result = Allocate(width, height, ChromaFormat, BitDepth, Color, HasAlpha, Metadata.Copy(), Context, ClearMode.None);
        try
        {
            ResizePlanes(result, filter);
            UpdateExif(result.Metadata, width, height);
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    private protected override YuvRaster CropCore(Rect rect) => Crop(rect);

    /// <inheritdoc/>
    private protected override YuvRaster ResizeCore(int width, int height, ResampleFilter filter) => Resize(width, height, filter);

    /// <inheritdoc/>
    private protected override void ResizeToCore(YuvRaster target, ResampleFilter filter) => ResizePlanes((YuvRaster<TSample>)target, filter);

    /// <summary>Resizes every plane into <paramref name="target"/>; a target alpha plane with no source alpha is made opaque.</summary>
    private void ResizePlanes(YuvRaster<TSample> target, ResampleFilter filter)
    {
        ResizePlane(Y, target.Y, filter);
        ResizePlane(U, target.U, filter);
        ResizePlane(V, target.V, filter);
        if (A is not null)
        {
            ResizePlane(A, target.A, filter);
        }
        else if (target.A is YuvPlane<TSample> alpha)
        {
            for (int y = 0; y < alpha.Height; y++)
            {
                Span<TSample> row = alpha.Row(y);
                for (int x = 0; x < row.Length; x++)
                {
                    YuvConvert.SetSample(row, x, target.MaxSample);
                }
            }
        }
    }

    /// <summary>Keeps EXIF's stored size in step with a new size, as raster operations do.</summary>
    private static void UpdateExif(RasterMetadata metadata, int width, int height)
    {
        Operations.Geometry.ExifUpkeep.Dimensions(metadata, new Size(width, height));
        Operations.Geometry.ExifUpkeep.DropThumbnail(metadata);
    }

    private static (int X, int Y) Subsampling(ChromaFormat format) => format switch
    {
        ChromaFormat.Yuv420 => (2, 2),
        ChromaFormat.Yuv422 => (2, 1),
        ChromaFormat.Yuv411 => (4, 1),
        ChromaFormat.Yuv410 => (4, 4),
        _ => (1, 1),
    };

    /// <summary>Resizes one plane in one channel with the gray resize's filters and weights, clamped to the bit depth.</summary>
    private void ResizePlane(YuvPlane<TSample>? from, YuvPlane<TSample>? to, ResampleFilter filter)
    {
        if (from is not null && to is not null)
        {
            YuvPlaneResize.Resize(from, to, filter, MaxSample, Context.MaxParallelism);
        }
    }
}
