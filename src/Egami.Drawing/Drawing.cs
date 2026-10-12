using System.Numerics;

using Egami.Drawing.Recording;
using Egami.Pixels;

namespace Egami.Drawing;

/// <summary>
/// Recorded canvas calls, from a <see cref="DrawingRecorder"/> or <see cref="Load"/>: replay them on any canvas with
/// <see cref="Canvas.DrawDrawing"/> or <see cref="Playback"/>, fill shapes with them through a <see cref="DrawingPaint"/>, render
/// them with <see cref="ToRaster{TPixel}"/>, or keep them in an .egdr file with <see cref="Save"/>. Replay is always vector, so a
/// drawing stays sharp at any scale. Immutable, and safe to replay on several threads and canvases at once.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Advanced.cs" region="recording"/>
/// </example>
public sealed partial class Drawing
{
    /// <summary>The most rendered images a drawing keeps for <see cref="Canvas.DrawDrawing"/>'s cacheAsImage and drawing patterns.</summary>
    private const int CacheEntries = 8;

    private readonly DrawingCall[] _calls;
    private readonly Dictionary<(RectF Area, int Width, int Height), Raster<RgbaF32>> _cache = [];
    private readonly Lock _cacheLock = new();

    internal Drawing(RectF bounds, DrawingCall[] calls)
    {
        Bounds = bounds;
        _calls = calls;
    }

    /// <summary>Gets the drawing's bounds in its own coordinates, as the recorder was given them.</summary>
    public RectF Bounds { get; }

    /// <summary>Gets the number of recorded calls, saves and transforms included; nested drawings count as one.</summary>
    public int CallCount => _calls.Length;

    internal ReadOnlySpan<DrawingCall> Calls => _calls;

    /// <summary>
    /// Replays the calls on a canvas through its public calls, between a <see cref="Canvas.Save"/> and a
    /// <see cref="Canvas.Restore"/>; recorded transforms apply after the canvas's transform at the start.
    /// </summary>
    /// <param name="canvas">The canvas.</param>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Advanced.cs" region="recording"/>
    /// </example>
    public void Playback(Canvas canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        Matrix3x2 under = canvas.Matrix;
        int count = canvas.Save();
        try
        {
            foreach (DrawingCall call in _calls)
            {
                call.Replay(canvas, under);
            }
        }
        finally
        {
            canvas.RestoreToCount(count);
        }
    }

    /// <summary>Renders the drawing's <see cref="Bounds"/> into a new raster, over transparency.</summary>
    /// <typeparam name="TPixel">The raster's pixel type.</typeparam>
    /// <param name="scale">Raster pixels per drawing unit.</param>
    /// <param name="options">The canvas settings, or <see langword="null"/> for the defaults.</param>
    /// <param name="context">The context the raster is made in, or <see langword="null"/> for the default.</param>
    /// <returns>A raster of the bounds' size times the scale, rounded up.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="scale"/> is not positive and finite, or the raster would be too large.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Advanced.cs" region="recording"/>
    /// </example>
    public Raster<TPixel> ToRaster<TPixel>(float scale = 1, CanvasOptions? options = null, EgamiContext? context = null)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (!(scale > 0 && float.IsFinite(scale)))
        {
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "The scale must be positive and finite.");
        }

        double w = Math.Ceiling(Bounds.Width * (double)scale), h = Math.Ceiling(Bounds.Height * (double)scale);
        if (w > int.MaxValue || h > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "The raster would be too large.");
        }

        return Render<TPixel>(Bounds, (int)w, (int)h, options, context);
    }

    /// <summary>The drawing rendered at a scale, kept for later calls at the same scale, and where it goes in drawing units.</summary>
    internal (Raster<RgbaF32> Image, RectF Destination) CachedImage(float scale)
    {
        int w = (int)Math.Clamp(Math.Ceiling(Bounds.Width * (double)scale), 1, 8192), h = (int)Math.Clamp(Math.Ceiling(Bounds.Height * (double)scale), 1, 8192);
        Raster<RgbaF32> image = Cached(Bounds, w, h);
        return (image, new RectF(Bounds.X, Bounds.Y, w / scale, h / scale));
    }

    /// <summary>A part of the drawing rendered to exactly <paramref name="width"/> by <paramref name="height"/> pixels, kept for reuse.</summary>
    internal Raster<RgbaF32> Cached(RectF area, int width, int height)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue((area, width, height), out Raster<RgbaF32>? image))
            {
                return image;
            }
        }

        Raster<RgbaF32> rendered = Render<RgbaF32>(area, width, height, null, null);
        lock (_cacheLock)
        {
            // The images are not disposed when dropped: a call on another thread may still be drawing one.
            if (_cache.Count >= CacheEntries)
            {
                _cache.Clear();
            }

            _cache[(area, width, height)] = rendered;
        }

        return rendered;
    }

    private Raster<TPixel> Render<TPixel>(RectF area, int width, int height, CanvasOptions? options, EgamiContext? context)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Raster<TPixel> raster = Raster.Create<TPixel>(Math.Max(width, 1), Math.Max(height, 1), context);
        try
        {
            using RasterCanvas<TPixel> canvas = new(raster, options);
            canvas.SetMatrix(Canvas.Compose(Matrix3x2.CreateTranslation(-area.X, -area.Y), Matrix3x2.CreateScale(width / area.Width, height / area.Height)));
            Playback(canvas);
        }
        catch
        {
            raster.Dispose();
            throw;
        }

        return raster;
    }
}
