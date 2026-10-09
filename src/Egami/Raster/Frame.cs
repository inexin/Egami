using System.Collections;

using Egami.Metadata;
using Egami.Pixels;

namespace Egami;

/// <summary>One frame of a raster: a pixel plane the size of the raster plus per-frame metadata.</summary>
/// <typeparam name="TPixel">The pixel type.</typeparam>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ApiExamples.cs" region="frame-list"/>
/// </example>
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

/// <summary>
/// The frames of a raster, all the same size and pixel type. There is always at least one. Frames added from anywhere are copied,
/// so a frame belongs to one raster only.
/// </summary>
/// <typeparam name="TPixel">The pixel type.</typeparam>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ApiExamples.cs" region="frame-list"/>
/// </example>
public sealed class FrameList<TPixel> : IReadOnlyList<Frame<TPixel>>
    where TPixel : unmanaged, IPixelType<TPixel>
{
    private readonly List<Frame<TPixel>> _frames;
    private readonly Raster<TPixel> _owner;

    internal FrameList(List<Frame<TPixel>> frames, Raster<TPixel> owner)
    {
        _frames = frames;
        _owner = owner;
    }

    /// <inheritdoc/>
    public int Count => _frames.Count;

    /// <inheritdoc/>
    public Frame<TPixel> this[int index] => _frames[index];

    /// <summary>Appends a copy of <paramref name="frame"/>'s pixels and metadata, from this raster or another.</summary>
    /// <param name="frame">The frame to copy. It must be the raster's size.</param>
    /// <returns>The new frame.</returns>
    /// <exception cref="ArgumentException">The frame is not the raster's size.</exception>
    /// <exception cref="LimitExceededException">All frames together would break <see cref="SafetyLimits.MaxTotalBytes"/>.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    public Frame<TPixel> Add(Frame<TPixel> frame)
    {
        return Insert(_frames.Count, frame);
    }

    /// <summary>Inserts a copy of <paramref name="frame"/>'s pixels and metadata at <paramref name="index"/>.</summary>
    /// <param name="index">Where the copy goes, 0 to <see cref="Count"/>.</param>
    /// <param name="frame">The frame to copy. It must be the raster's size.</param>
    /// <returns>The new frame.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside 0 to <see cref="Count"/>.</exception>
    /// <exception cref="ArgumentException">The frame is not the raster's size.</exception>
    /// <exception cref="LimitExceededException">All frames together would break <see cref="SafetyLimits.MaxTotalBytes"/>.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    public Frame<TPixel> Insert(int index, Frame<TPixel> frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        _owner.CheckNotDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, _frames.Count);
        if (frame.Plane.Width != _owner.Width || frame.Plane.Height != _owner.Height)
        {
            throw new ArgumentException(
                $"The frame is {frame.Plane.Width} × {frame.Plane.Height} but the raster is {_owner.Width} × {_owner.Height}; resize it first.",
                nameof(frame));
        }

        long frameBytes = (long)_owner.Width * _owner.Height * System.Runtime.CompilerServices.Unsafe.SizeOf<TPixel>();
        long total = frameBytes * (_frames.Count + 1);
        long max = _owner.Context.Limits.MaxTotalBytes;
        if (total > max)
        {
            throw new LimitExceededException(nameof(SafetyLimits.MaxTotalBytes), max, total);
        }

        PixelPlane<TPixel> plane = PixelPlane.Allocate<TPixel>(_owner.Width, _owner.Height, _owner.Context.Memory);
        for (int y = 0; y < _owner.Height; y++)
        {
            frame.Plane.Row(y).CopyTo(plane.Row(y));
        }

        Frame<TPixel> copy = new(plane, frame.Metadata.Copy());
        _frames.Insert(index, copy);
        return copy;
    }

    /// <summary>Appends a frame the list's raster now owns, without copying (readers building an animation).</summary>
    internal void AddOwned(Frame<TPixel> frame)
    {
        _frames.Add(frame);
    }

    /// <summary>Removes the frame at <paramref name="index"/> and returns its memory.</summary>
    /// <param name="index">The frame to remove.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not a frame.</exception>
    /// <exception cref="InvalidOperationException">It is the only frame; a raster always has one.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    public void RemoveAt(int index)
    {
        _owner.CheckNotDisposed();
        CheckIndex(index, nameof(index));
        if (_frames.Count == 1)
        {
            throw new InvalidOperationException("A raster always has at least one frame.");
        }

        Frame<TPixel> frame = _frames[index];
        _frames.RemoveAt(index);
        frame.Plane.Dispose();
    }

    /// <summary>Moves the frame at <paramref name="from"/> to <paramref name="to"/>, without copying pixels.</summary>
    /// <param name="from">The frame to move.</param>
    /// <param name="to">Its new position, counted after it is taken out.</param>
    /// <exception cref="ArgumentOutOfRangeException">An index is not a frame.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    public void Move(int from, int to)
    {
        _owner.CheckNotDisposed();
        CheckIndex(from, nameof(from));
        CheckIndex(to, nameof(to));
        Frame<TPixel> frame = _frames[from];
        _frames.RemoveAt(from);
        _frames.Insert(to, frame);
    }

    /// <inheritdoc/>
    public IEnumerator<Frame<TPixel>> GetEnumerator()
    {
        return _frames.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    private void CheckIndex(int index, string paramName)
    {
        if ((uint)index >= (uint)_frames.Count)
        {
            throw new ArgumentOutOfRangeException(paramName, index, $"The raster has {_frames.Count} frames.");
        }
    }
}
