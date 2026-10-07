using System.Collections;

using Egami.Metadata;
using Egami.Pixels;

namespace Egami;

/// <summary>One frame of a raster: a pixel plane the size of the raster plus per-frame metadata.</summary>
/// <typeparam name="TPixel">The pixel type.</typeparam>
public sealed class Frame<TPixel>
    where TPixel : unmanaged, IPixelType<TPixel>
{
    internal Frame(PixelPlane<TPixel> plane, FrameMetadata metadata)
    {
        Plane = plane;
        Metadata = metadata;
    }

    /// <summary>Gets the frame's pixels.</summary>
    public PixelPlane<TPixel> Plane { get; internal set; }

    /// <summary>Gets the frame's metadata.</summary>
    public FrameMetadata Metadata { get; }

    /// <summary>Returns one row of pixels. See <see cref="PixelPlane{TPixel}.Row"/>.</summary>
    /// <param name="y">The row index.</param>
    /// <returns>The row.</returns>
    public Span<TPixel> Row(int y)
    {
        return Plane.Row(y);
    }
}

/// <summary>The frames of a raster, all the same size and pixel type. There is always at least one.</summary>
/// <typeparam name="TPixel">The pixel type.</typeparam>
public sealed class FrameList<TPixel> : IReadOnlyList<Frame<TPixel>>
    where TPixel : unmanaged, IPixelType<TPixel>
{
    private readonly List<Frame<TPixel>> _frames;

    internal FrameList(List<Frame<TPixel>> frames)
    {
        _frames = frames;
    }

    /// <inheritdoc/>
    public int Count => _frames.Count;

    /// <inheritdoc/>
    public Frame<TPixel> this[int index] => _frames[index];

    /// <inheritdoc/>
    public IEnumerator<Frame<TPixel>> GetEnumerator()
    {
        return _frames.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}
