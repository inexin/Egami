using System.Numerics;

using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing;

/// <summary>
/// A surface to draw paths, shapes, strokes and images on, through a current transform and clip. The canvas itself is abstract:
/// <see cref="RasterCanvas{TPixel}"/> draws into pixels, and other backends (a PDF page, a recorder) keep the calls as vectors.
/// Every public call is checked and tracked here (the transform, the clip bounds, the save stack) and then ends in exactly one
/// protected <c>On</c> hook, which is all a backend overrides; hooks added in later versions get default bodies, so adding them
/// never breaks a backend.
/// <para>
/// Coordinates are float pixels, y down; the pixel at (0, 0) covers the square from 0 to 1. Calls draw when they are made. A canvas
/// is for one thread at a time; one large call may still spread its rows over several cores.
/// </para>
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="canvas"/>
/// </example>
public abstract partial class Canvas : IDisposable
{
    private readonly Stack<State> _saved = new();
    private State _state;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="Canvas"/> class.</summary>
    /// <param name="width">The drawing surface's width in device units.</param>
    /// <param name="height">The drawing surface's height in device units.</param>
    /// <exception cref="ArgumentOutOfRangeException">A size is not positive and finite.</exception>
    protected Canvas(float width, float height)
    {
        if (!(width > 0 && float.IsFinite(width)))
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "The width must be positive and finite.");
        }

        if (!(height > 0 && float.IsFinite(height)))
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, "The height must be positive and finite.");
        }

        Width = width;
        Height = height;
        _state = new State(Matrix3x2.Identity, new RectF(0, 0, width, height));
    }

    /// <summary>Initializes a new instance of the <see cref="Canvas"/> class whose clip starts as a rectangle that may not start at the origin, for a recorder.</summary>
    /// <param name="bounds">The starting clip, finite and not empty; its size is the canvas's.</param>
    private protected Canvas(RectF bounds)
        : this(bounds.Width, bounds.Height)
    {
        if (!bounds.IsFinite)
        {
            throw new ArgumentException("The bounds must be finite.", nameof(bounds));
        }

        _state = new State(Matrix3x2.Identity, bounds);
    }

    /// <summary>Gets the drawing surface's width in device units.</summary>
    public float Width { get; }

    /// <summary>Gets the drawing surface's height in device units.</summary>
    public float Height { get; }

    /// <summary>Gets the current transform from local coordinates to device coordinates.</summary>
    public Matrix3x2 Matrix => _state.Matrix;

    /// <summary>Gets the number of saved states plus one: 1 before any <see cref="Save"/>.</summary>
    public int SaveCount => _saved.Count + 1;

    /// <summary>
    /// Gets the bounds of the current clip in device coordinates. Every clip so far lies inside them; a backend may draw less (a
    /// clip by a round shape, or one that cuts a hole, keeps the bounds of what it could reach).
    /// </summary>
    public RectF DeviceClipBounds => _state.ClipBounds;

    /// <summary>Gets the bounds of the current clip in local coordinates: the device clip bounds mapped back through the transform.</summary>
    public RectF LocalClipBounds
    {
        get
        {
            if (_state.ClipBounds.IsEmpty || !Matrix3x2.Invert(_state.Matrix, out Matrix3x2 inverse))
            {
                return RectF.Empty;
            }

            return MapRect(_state.ClipBounds, inverse);
        }
    }

    /// <summary>Saves the transform and clip; the matching <see cref="Restore"/> brings them back.</summary>
    /// <returns>The save count before this call, to pass to <see cref="RestoreToCount"/>.</returns>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="canvas"/>
    /// </example>
    public int Save()
    {
        CheckLive();
        int count = SaveCount;
        _saved.Push(_state);
        _state = _state with { IsLayer = false };
        OnSave();
        return count;
    }

    /// <summary>
    /// Brings back the transform and clip of the matching <see cref="Save"/>, or ends the layer of the matching
    /// <see cref="SaveLayer(LayerOptions?)"/> by merging it into what lies below.
    /// </summary>
    /// <exception cref="InvalidOperationException">There is no saved state.</exception>
    public void Restore()
    {
        CheckLive();
        if (_saved.Count == 0)
        {
            throw new InvalidOperationException("Restore was called without a matching Save.");
        }

        bool layer = _state.IsLayer;
        _state = _saved.Pop();
        if (layer)
        {
            OnRestoreLayer();
        }
        else
        {
            OnRestore();
        }
    }

    /// <summary>Restores until <see cref="SaveCount"/> is <paramref name="count"/>; nothing when it already is or is lower.</summary>
    /// <param name="count">The save count to return to, at least 1.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is below 1.</exception>
    public void RestoreToCount(int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        CheckLive();
        while (SaveCount > count)
        {
            Restore();
        }
    }

    /// <summary>Moves the origin: later drawing is shifted by (<paramref name="dx"/>, <paramref name="dy"/>) local units.</summary>
    /// <param name="dx">The horizontal shift.</param>
    /// <param name="dy">The vertical shift.</param>
    public void Translate(float dx, float dy) => Concat(Matrix3x2.CreateTranslation(Finite(dx, nameof(dx)), Finite(dy, nameof(dy))));

    /// <summary>Scales later drawing about the local origin.</summary>
    /// <param name="sx">The horizontal factor.</param>
    /// <param name="sy">The vertical factor.</param>
    public void Scale(float sx, float sy) => Concat(Matrix3x2.CreateScale(Finite(sx, nameof(sx)), Finite(sy, nameof(sy))));

    /// <summary>Scales later drawing by the same factor on both axes about the local origin.</summary>
    /// <param name="scale">The factor.</param>
    public void Scale(float scale) => Scale(scale, scale);

    /// <summary>Turns later drawing clockwise (on a y-down screen) about the local origin.</summary>
    /// <param name="degrees">The angle.</param>
    public void Rotate(float degrees) => Concat(Matrix3x2.CreateRotation(Finite(degrees, nameof(degrees)) * MathF.PI / 180));

    /// <summary>Turns later drawing clockwise about a local point.</summary>
    /// <param name="degrees">The angle.</param>
    /// <param name="center">The point that stays put.</param>
    public void Rotate(float degrees, Vector2 center) => Concat(Matrix3x2.CreateRotation(Finite(degrees, nameof(degrees)) * MathF.PI / 180, FinitePoint(center, nameof(center))));

    /// <summary>Slants later drawing: x moves by y·tan(<paramref name="degreesX"/>) and y by x·tan(<paramref name="degreesY"/>).</summary>
    /// <param name="degreesX">The horizontal slant.</param>
    /// <param name="degreesY">The vertical slant.</param>
    public void Skew(float degreesX, float degreesY)
        => Concat(Matrix3x2.CreateSkew(Finite(degreesX, nameof(degreesX)) * MathF.PI / 180, Finite(degreesY, nameof(degreesY)) * MathF.PI / 180));

    /// <summary>Applies <paramref name="matrix"/> before the current transform: local points go through it first.</summary>
    /// <param name="matrix">The transform to add.</param>
    /// <exception cref="ArgumentException">An element is not finite.</exception>
    public void Concat(Matrix3x2 matrix) => SetMatrix(FiniteMatrix(matrix, nameof(matrix)) * _state.Matrix);

    /// <summary>Replaces the current transform.</summary>
    /// <param name="matrix">The new transform from local to device coordinates.</param>
    /// <exception cref="ArgumentException">An element is not finite.</exception>
    public void SetMatrix(Matrix3x2 matrix)
    {
        CheckLive();
        FiniteMatrix(matrix, nameof(matrix));
        _state = _state with { Matrix = matrix };
        OnSetMatrix(matrix);
    }

    /// <summary>Resets the transform to the identity: local coordinates are device coordinates.</summary>
    public void ResetMatrix() => SetMatrix(Matrix3x2.Identity);

    /// <summary>Clips later drawing to a rectangle, or to outside it, until the matching <see cref="Restore"/>.</summary>
    /// <param name="rect">The rectangle in local coordinates.</param>
    /// <param name="op">Whether to keep the inside or the outside.</param>
    /// <param name="antialias">Whether the clip's edges are antialiased.</param>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="clips"/>
    /// </example>
    public void ClipRect(RectF rect, ClipOp op = ClipOp.Intersect, bool antialias = true)
    {
        CheckLive();
        CheckRect(rect, nameof(rect));
        CheckOp(op);
        Narrow(rect, op);
        OnClipRect(rect, op, antialias);
    }

    /// <summary>Clips later drawing to a round rectangle, or to outside it.</summary>
    /// <param name="roundRect">The round rectangle in local coordinates.</param>
    /// <param name="op">Whether to keep the inside or the outside.</param>
    /// <param name="antialias">Whether the clip's edges are antialiased.</param>
    public void ClipRoundRect(RoundRect roundRect, ClipOp op = ClipOp.Intersect, bool antialias = true)
    {
        CheckLive();
        CheckOp(op);
        Narrow(roundRect.Bounds, op);
        OnClipRoundRect(roundRect, op, antialias);
    }

    /// <summary>Clips later drawing to the area a path covers by its fill rule, or to outside it.</summary>
    /// <param name="path">The path in local coordinates.</param>
    /// <param name="op">Whether to keep the inside or the outside.</param>
    /// <param name="antialias">Whether the clip's edges are antialiased.</param>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="clips"/>
    /// </example>
    public void ClipPath(Path path, ClipOp op = ClipOp.Intersect, bool antialias = true)
    {
        ArgumentNullException.ThrowIfNull(path);
        CheckLive();
        CheckOp(op);
        Narrow(path.IsEmpty ? RectF.Empty : path.TightBounds, op);
        OnClipPath(path, op, antialias);
    }

    /// <summary>Returns whether a local rectangle certainly lies outside the clip, so drawing it can be skipped.</summary>
    /// <param name="rect">The rectangle in local coordinates.</param>
    /// <returns><see langword="true"/> if nothing inside it can be drawn.</returns>
    public bool QuickReject(RectF rect)
    {
        if (rect.IsEmpty || !rect.IsFinite)
        {
            return true;
        }

        return !MapRect(rect, _state.Matrix).IntersectsWith(_state.ClipBounds);
    }

    /// <summary>Returns whether a path certainly lies outside the clip.</summary>
    /// <param name="path">The path in local coordinates.</param>
    /// <returns><see langword="true"/> if nothing of it can be drawn.</returns>
    public bool QuickReject(Path path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.IsEmpty || QuickReject(path.Bounds.Width == 0 || path.Bounds.Height == 0 ? path.Bounds.Inflate(0.5f, 0.5f) : path.Bounds);
    }

    /// <summary>Fills a rectangle.</summary>
    /// <param name="rect">The rectangle.</param>
    /// <param name="paint">The paint.</param>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="shapes"/>
    /// </example>
    public void FillRect(RectF rect, Paint paint)
    {
        Check(paint);
        CheckRect(rect, nameof(rect));
        if (!rect.IsEmpty)
        {
            OnDrawRect(rect, null, paint);
        }
    }

    /// <summary>Outlines a rectangle.</summary>
    /// <param name="rect">The rectangle.</param>
    /// <param name="stroke">The pen.</param>
    /// <param name="paint">The paint.</param>
    public void StrokeRect(RectF rect, Stroke stroke, Paint paint)
    {
        Check(paint, stroke);
        CheckRect(rect, nameof(rect));
        if (stroke.Effect is not null)
        {
            DrawWithEffect(Path.Rect(rect), stroke, paint);
            return;
        }

        OnDrawRect(rect, stroke, paint);
    }

    /// <summary>Fills a round rectangle.</summary>
    /// <param name="roundRect">The round rectangle.</param>
    /// <param name="paint">The paint.</param>
    public void FillRoundRect(RoundRect roundRect, Paint paint)
    {
        Check(paint);
        if (!roundRect.IsEmpty)
        {
            OnDrawRoundRect(roundRect, null, paint);
        }
    }

    /// <summary>Outlines a round rectangle.</summary>
    /// <param name="roundRect">The round rectangle.</param>
    /// <param name="stroke">The pen.</param>
    /// <param name="paint">The paint.</param>
    public void StrokeRoundRect(RoundRect roundRect, Stroke stroke, Paint paint)
    {
        Check(paint, stroke);
        if (stroke.Effect is not null)
        {
            DrawWithEffect(Path.RoundRect(roundRect), stroke, paint);
            return;
        }

        OnDrawRoundRect(roundRect, stroke, paint);
    }

    /// <summary>Fills the ellipse that fills a rectangle.</summary>
    /// <param name="oval">The rectangle.</param>
    /// <param name="paint">The paint.</param>
    public void FillOval(RectF oval, Paint paint)
    {
        Check(paint);
        CheckRect(oval, nameof(oval));
        if (!oval.IsEmpty)
        {
            OnDrawOval(oval, null, paint);
        }
    }

    /// <summary>Outlines the ellipse that fills a rectangle.</summary>
    /// <param name="oval">The rectangle.</param>
    /// <param name="stroke">The pen.</param>
    /// <param name="paint">The paint.</param>
    public void StrokeOval(RectF oval, Stroke stroke, Paint paint)
    {
        Check(paint, stroke);
        CheckRect(oval, nameof(oval));
        if (stroke.Effect is not null)
        {
            DrawWithEffect(Path.Oval(oval), stroke, paint);
            return;
        }

        OnDrawOval(oval, stroke, paint);
    }

    /// <summary>Fills a circle.</summary>
    /// <param name="center">The center.</param>
    /// <param name="radius">The radius; nothing is drawn when it is 0 or less.</param>
    /// <param name="paint">The paint.</param>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="shapes"/>
    /// </example>
    public void FillCircle(Vector2 center, float radius, Paint paint)
    {
        Check(paint);
        FinitePoint(center, nameof(center));
        Finite(radius, nameof(radius));
        if (radius > 0)
        {
            OnDrawOval(new RectF(center.X - radius, center.Y - radius, 2 * radius, 2 * radius), null, paint);
        }
    }

    /// <summary>Outlines a circle.</summary>
    /// <param name="center">The center.</param>
    /// <param name="radius">The radius.</param>
    /// <param name="stroke">The pen.</param>
    /// <param name="paint">The paint.</param>
    public void StrokeCircle(Vector2 center, float radius, Stroke stroke, Paint paint)
    {
        Check(paint, stroke);
        FinitePoint(center, nameof(center));
        Finite(radius, nameof(radius));
        if (radius >= 0)
        {
            RectF oval = new(center.X - radius, center.Y - radius, 2 * radius, 2 * radius);
            if (stroke.Effect is not null)
            {
                DrawWithEffect(Path.Oval(oval), stroke, paint);
                return;
            }

            OnDrawOval(oval, stroke, paint);
        }
    }

    /// <summary>Fills a pie slice of an ellipse: 0 degrees is the right end of its horizontal axis, and positive angles turn clockwise.</summary>
    /// <param name="oval">The rectangle the ellipse fills.</param>
    /// <param name="startDegrees">Where the slice starts.</param>
    /// <param name="sweepDegrees">How far it turns.</param>
    /// <param name="paint">The paint.</param>
    public void FillArc(RectF oval, float startDegrees, float sweepDegrees, Paint paint)
    {
        Check(paint);
        CheckRect(oval, nameof(oval));
        Finite(startDegrees, nameof(startDegrees));
        Finite(sweepDegrees, nameof(sweepDegrees));
        if (!oval.IsEmpty && sweepDegrees != 0)
        {
            OnDrawPath(Path.Pie(oval, startDegrees, sweepDegrees), null, paint);
        }
    }

    /// <summary>Strokes the open arc of an ellipse.</summary>
    /// <param name="oval">The rectangle the ellipse fills.</param>
    /// <param name="startDegrees">Where the arc starts.</param>
    /// <param name="sweepDegrees">How far it turns.</param>
    /// <param name="stroke">The pen.</param>
    /// <param name="paint">The paint.</param>
    public void StrokeArc(RectF oval, float startDegrees, float sweepDegrees, Stroke stroke, Paint paint)
    {
        Check(paint, stroke);
        CheckRect(oval, nameof(oval));
        Finite(startDegrees, nameof(startDegrees));
        Finite(sweepDegrees, nameof(sweepDegrees));
        Path arc = Path.Arc(oval, startDegrees, sweepDegrees);
        if (stroke.Effect is not null)
        {
            DrawWithEffect(arc, stroke, paint);
            return;
        }

        OnDrawPath(arc, stroke, paint);
    }

    /// <summary>Fills the ring between two round rectangles, as <see cref="Path.Ring"/> describes.</summary>
    /// <param name="outer">The outer edge.</param>
    /// <param name="inner">The inner edge: the hole.</param>
    /// <param name="paint">The paint.</param>
    public void FillRing(RoundRect outer, RoundRect inner, Paint paint)
    {
        Check(paint);
        if (!outer.IsEmpty)
        {
            OnDrawPath(Path.Ring(outer, inner), null, paint);
        }
    }

    /// <summary>Fills a path by its fill rule.</summary>
    /// <param name="path">The path.</param>
    /// <param name="paint">The paint.</param>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="paths"/>
    /// </example>
    public void FillPath(Path path, Paint paint)
    {
        ArgumentNullException.ThrowIfNull(path);
        Check(paint);
        if (!path.IsEmpty)
        {
            OnDrawPath(path, null, paint);
        }
    }

    /// <summary>Outlines a path with a pen.</summary>
    /// <param name="path">The path.</param>
    /// <param name="stroke">The pen.</param>
    /// <param name="paint">The paint.</param>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="strokes"/>
    /// </example>
    public void StrokePath(Path path, Stroke stroke, Paint paint)
    {
        ArgumentNullException.ThrowIfNull(path);
        Check(paint, stroke);
        if (stroke.Effect is not null)
        {
            DrawWithEffect(path, stroke, paint);
        }
        else if (path.Points.Length > 0)
        {
            OnDrawPath(path, stroke, paint);
        }
    }

    /// <summary>Strokes a straight line.</summary>
    /// <param name="start">Where it starts.</param>
    /// <param name="end">Where it ends.</param>
    /// <param name="stroke">The pen.</param>
    /// <param name="paint">The paint.</param>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="strokes"/>
    /// </example>
    public void DrawLine(Vector2 start, Vector2 end, Stroke stroke, Paint paint)
    {
        Check(paint, stroke);
        FinitePoint(start, nameof(start));
        FinitePoint(end, nameof(end));
        if (stroke.Effect is not null)
        {
            DrawWithEffect(Path.Line(start, end), stroke, paint);
            return;
        }

        OnDrawPoints(PointMode.Lines, [start, end], stroke, paint);
    }

    /// <summary>Draws points as dots, pairs as lines, or all of them as one polyline, with a pen.</summary>
    /// <param name="mode">How the points are read.</param>
    /// <param name="points">The points.</param>
    /// <param name="stroke">The pen: its width sizes the dots and lines.</param>
    /// <param name="paint">The paint.</param>
    public void DrawPoints(PointMode mode, ReadOnlySpan<Vector2> points, Stroke stroke, Paint paint)
    {
        Check(paint, stroke);
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
        }

        foreach (Vector2 p in points)
        {
            FinitePoint(p, nameof(points));
        }

        if (points.IsEmpty)
        {
            return;
        }

        if (stroke.Effect is not null)
        {
            // The same paths the default hook draws, each through the effect.
            switch (mode)
            {
                case PointMode.Points:
                    foreach (Vector2 p in points)
                    {
                        DrawWithEffect(new PathBuilder().MoveTo(p).LineTo(p).ToPath(), stroke.Cap == LineCap.Butt ? stroke with { Cap = LineCap.Square } : stroke, paint);
                    }

                    break;
                case PointMode.Lines:
                    for (int i = 0; i + 1 < points.Length; i += 2)
                    {
                        DrawWithEffect(Path.Line(points[i], points[i + 1]), stroke, paint);
                    }

                    break;
                default:
                    DrawWithEffect(Path.Polygon(points, close: false), stroke, paint);
                    break;
            }

            return;
        }

        OnDrawPoints(mode, points, stroke, paint);
    }

    /// <summary>Fills the whole clip with a paint.</summary>
    /// <param name="paint">The paint.</param>
    public void DrawPaint(Paint paint)
    {
        Check(paint);
        OnDrawPaint(paint);
    }

    /// <summary>Replaces every pixel inside the clip with a color: <see cref="DrawPaint"/> with <see cref="BlendMode.Source"/>.</summary>
    /// <param name="color">The color.</param>
    public void Clear(RgbaF32 color) => DrawPaint(new SolidPaint(color) { BlendMode = BlendMode.Source });

    /// <summary>Replaces every pixel inside the clip with a color.</summary>
    /// <param name="color">The color.</param>
    public void Clear(Rgba8 color) => Clear(RgbaF32.FromRgba8(color));

    /// <summary>Draws a whole image with its top-left corner at a local point, at its own size in local units.</summary>
    /// <param name="image">The image.</param>
    /// <param name="position">Where its top-left corner goes.</param>
    /// <param name="sampling">How the image is sampled when it is transformed; Egami's default sampling when <see langword="null"/>.</param>
    /// <param name="paint">The blend mode, opacity and antialiasing of the image's edges; its color is not used. <see langword="null"/> draws source-over.</param>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="images"/>
    /// </example>
    public void DrawImage(Raster image, Vector2 position, SamplingOptions? sampling = null, Paint? paint = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        FinitePoint(position, nameof(position));
        DrawImage(image, new RectF(0, 0, image.Width, image.Height), new RectF(position.X, position.Y, image.Width, image.Height), sampling, paint);
    }

    /// <summary>Draws a whole image scaled to fill a local rectangle.</summary>
    /// <param name="image">The image.</param>
    /// <param name="destination">Where it goes.</param>
    /// <param name="sampling">How the image is sampled; Egami's default sampling when <see langword="null"/>.</param>
    /// <param name="paint">The blend mode, opacity and antialiasing; its color is not used.</param>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="images"/>
    /// </example>
    public void DrawImage(Raster image, RectF destination, SamplingOptions? sampling = null, Paint? paint = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        DrawImage(image, new RectF(0, 0, image.Width, image.Height), destination, sampling, paint);
    }

    /// <summary>
    /// Draws part of an image scaled to fill a local rectangle, under the current transform and clip. Samples near the source
    /// rectangle's edges read only pixels inside it.
    /// </summary>
    /// <param name="image">The image.</param>
    /// <param name="source">The part of the image to draw, in its pixels; clipped to the image.</param>
    /// <param name="destination">Where it goes.</param>
    /// <param name="sampling">How the image is sampled; Egami's default sampling when <see langword="null"/>.</param>
    /// <param name="paint">The blend mode, opacity and antialiasing; its color is not used.</param>
    /// <exception cref="ArgumentException">A rectangle is not finite.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="images"/>
    /// </example>
    public void DrawImage(Raster image, RectF source, RectF destination, SamplingOptions? sampling = null, Paint? paint = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        CheckLive();
        CheckRect(source, nameof(source));
        CheckRect(destination, nameof(destination));

        RectF clipped = source.Intersect(new RectF(0, 0, image.Width, image.Height));
        if (clipped.IsEmpty || destination.IsEmpty)
        {
            return;
        }

        // Clipping the source shrinks the destination in proportion.
        if (clipped != source)
        {
            float sx = destination.Width / source.Width, sy = destination.Height / source.Height;
            destination = new RectF(
                destination.X + ((clipped.X - source.X) * sx),
                destination.Y + ((clipped.Y - source.Y) * sy),
                clipped.Width * sx,
                clipped.Height * sy);
        }

        OnDrawImage(image, clipped, destination, sampling ?? DefaultSampling, paint);
    }

    /// <summary>Restores every saved state and releases the backend's resources. Drawing afterwards throws.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        EndOpenMarkedContent();
        while (_saved.Count > 0)
        {
            Restore();
        }

        Dispose(true);
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases the backend's resources; called once by <see cref="Dispose()"/> after every state is restored.</summary>
    /// <param name="disposing">Always <see langword="true"/>: the canvas has no finalizer.</param>
    protected virtual void Dispose(bool disposing)
    {
    }

    /// <summary>Called by <see cref="Save"/> after the state is pushed.</summary>
    protected virtual void OnSave()
    {
    }

    /// <summary>Called by <see cref="Restore"/> after the state is popped; <see cref="Matrix"/> and <see cref="DeviceClipBounds"/> are already the restored ones.</summary>
    protected virtual void OnRestore()
    {
    }

    /// <summary>Called whenever the transform changes, with the new transform (also <see cref="Matrix"/>).</summary>
    /// <param name="matrix">The transform from local to device coordinates.</param>
    protected virtual void OnSetMatrix(Matrix3x2 matrix)
    {
    }

    /// <summary>Clips by a rectangle. By default it goes to <see cref="OnClipPath"/>.</summary>
    /// <param name="rect">The rectangle in local coordinates, under <see cref="Matrix"/>.</param>
    /// <param name="op">Whether to keep the inside or the outside.</param>
    /// <param name="antialias">Whether the edges are antialiased.</param>
    protected virtual void OnClipRect(RectF rect, ClipOp op, bool antialias) => OnClipPath(Path.Rect(rect), op, antialias);

    /// <summary>Clips by a round rectangle. By default it goes to <see cref="OnClipPath"/>.</summary>
    /// <param name="roundRect">The round rectangle in local coordinates.</param>
    /// <param name="op">Whether to keep the inside or the outside.</param>
    /// <param name="antialias">Whether the edges are antialiased.</param>
    protected virtual void OnClipRoundRect(RoundRect roundRect, ClipOp op, bool antialias) => OnClipPath(Path.RoundRect(roundRect), op, antialias);

    /// <summary>Clips by a path's fill.</summary>
    /// <param name="path">The path in local coordinates.</param>
    /// <param name="op">Whether to keep the inside or the outside.</param>
    /// <param name="antialias">Whether the edges are antialiased.</param>
    protected abstract void OnClipPath(Path path, ClipOp op, bool antialias);

    /// <summary>Fills the clip. By default it fills the device clip bounds mapped back to local coordinates.</summary>
    /// <param name="paint">The paint.</param>
    protected virtual void OnDrawPaint(Paint paint)
    {
        if (DeviceClipBounds.IsEmpty || !Matrix3x2.Invert(Matrix, out Matrix3x2 inverse))
        {
            return;
        }

        OnDrawPath(Path.Rect(DeviceClipBounds).Transform(inverse), null, paint);
    }

    /// <summary>Fills or strokes a rectangle. By default it goes to <see cref="OnDrawPath"/>.</summary>
    /// <param name="rect">The rectangle.</param>
    /// <param name="stroke">The pen, or <see langword="null"/> to fill.</param>
    /// <param name="paint">The paint.</param>
    protected virtual void OnDrawRect(RectF rect, Stroke? stroke, Paint paint) => OnDrawPath(Path.Rect(rect), stroke, paint);

    /// <summary>Fills or strokes a round rectangle. By default it goes to <see cref="OnDrawPath"/>.</summary>
    /// <param name="roundRect">The round rectangle.</param>
    /// <param name="stroke">The pen, or <see langword="null"/> to fill.</param>
    /// <param name="paint">The paint.</param>
    protected virtual void OnDrawRoundRect(RoundRect roundRect, Stroke? stroke, Paint paint) => OnDrawPath(Path.RoundRect(roundRect), stroke, paint);

    /// <summary>Fills or strokes an ellipse. By default it goes to <see cref="OnDrawPath"/>.</summary>
    /// <param name="oval">The rectangle the ellipse fills.</param>
    /// <param name="stroke">The pen, or <see langword="null"/> to fill.</param>
    /// <param name="paint">The paint.</param>
    protected virtual void OnDrawOval(RectF oval, Stroke? stroke, Paint paint) => OnDrawPath(Path.Oval(oval), stroke, paint);

    /// <summary>Fills a path by its fill rule, or strokes it.</summary>
    /// <param name="path">The path in local coordinates.</param>
    /// <param name="stroke">The pen, or <see langword="null"/> to fill.</param>
    /// <param name="paint">The paint.</param>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="backend"/>
    /// </example>
    protected abstract void OnDrawPath(Path path, Stroke? stroke, Paint paint);

    /// <summary>Draws points, lines or a polyline. By default each dot or line goes to <see cref="OnDrawPath"/>, the polyline as one path.</summary>
    /// <param name="mode">How the points are read.</param>
    /// <param name="points">The points.</param>
    /// <param name="stroke">The pen.</param>
    /// <param name="paint">The paint.</param>
    protected virtual void OnDrawPoints(PointMode mode, ReadOnlySpan<Vector2> points, Stroke stroke, Paint paint)
    {
        ArgumentNullException.ThrowIfNull(stroke);
        switch (mode)
        {
            case PointMode.Points:
                foreach (Vector2 p in points)
                {
                    OnDrawPath(new PathBuilder().MoveTo(p).LineTo(p).ToPath(), stroke.Cap == LineCap.Butt ? stroke with { Cap = LineCap.Square } : stroke, paint);
                }

                break;
            case PointMode.Lines:
                for (int i = 0; i + 1 < points.Length; i += 2)
                {
                    OnDrawPath(Path.Line(points[i], points[i + 1]), stroke, paint);
                }

                break;
            default:
                OnDrawPath(Path.Polygon(points, close: false), stroke, paint);
                break;
        }
    }

    /// <summary>Draws part of an image into a local rectangle.</summary>
    /// <param name="image">The image.</param>
    /// <param name="source">The part to draw, inside the image.</param>
    /// <param name="destination">Where it goes, in local coordinates.</param>
    /// <param name="sampling">How it is sampled.</param>
    /// <param name="paint">The blend mode, opacity and antialiasing, or <see langword="null"/> for source-over.</param>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="backend"/>
    /// </example>
    protected abstract void OnDrawImage(Raster image, RectF source, RectF destination, SamplingOptions sampling, Paint? paint);

    /// <summary>
    /// Draws one call into a temporary raster instead, for a backend that cannot express it (a blend mode or paint its format lacks):
    /// the result covers the device clip bounds at <paramref name="scale"/> device pixels per unit and holds what the call draws,
    /// clipped to those bounds, over transparency; the backend then draws it as an image. Skia's PDF backend falls back the same way.
    /// A paint's <see cref="Paint.Blur"/>, whose sigma is in device units, is scaled with the raster, so it keeps its size. A layer's
    /// filter and backdrop chains run on the raster's pixels as they are: a chain with sizes in pixels (a blur radius, an offset)
    /// should be built for the scale.
    /// </summary>
    /// <param name="draw">The call, made on a raster canvas set up with this canvas's transform.</param>
    /// <param name="scale">Raster pixels per device unit, such as 300 / 72 for a PDF page at 300 dpi.</param>
    /// <returns>The image and the device rectangle it covers, or <see langword="null"/> when the clip is empty.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="scale"/> is not positive and finite.</exception>
    protected FallbackImage? RasterizeFallback(Action<Canvas> draw, float scale = 1)
    {
        ArgumentNullException.ThrowIfNull(draw);
        if (!(scale > 0 && float.IsFinite(scale)))
        {
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "The scale must be positive and finite.");
        }

        RectF bounds = DeviceClipBounds;
        Rect pixels = new RectF(bounds.X * scale, bounds.Y * scale, bounds.Width * scale, bounds.Height * scale).RoundOut();
        if (pixels.IsEmpty)
        {
            return null;
        }

        Raster<RgbaF32> image = Raster.Create<RgbaF32>(pixels.Width, pixels.Height);
        using (RasterCanvas<RgbaF32> canvas = new(image) { BlurScale = scale })
        {
            canvas.SetMatrix(_state.Matrix * Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(-pixels.X, -pixels.Y));
            draw(canvas);
        }

        return new FallbackImage(image, new RectF(pixels.X / scale, pixels.Y / scale, pixels.Width / scale, pixels.Height / scale));
    }

    private static SamplingOptions DefaultSampling { get; } = new();

    private static RectF MapRect(RectF rect, Matrix3x2 matrix)
    {
        if (matrix.IsIdentity)
        {
            return rect;
        }

        return RectF.FromPoints(
        [
            Vector2.Transform(new Vector2(rect.Left, rect.Top), matrix),
            Vector2.Transform(new Vector2(rect.Right, rect.Top), matrix),
            Vector2.Transform(new Vector2(rect.Right, rect.Bottom), matrix),
            Vector2.Transform(new Vector2(rect.Left, rect.Bottom), matrix),
        ]);
    }

    private void Narrow(RectF localBounds, ClipOp op)
    {
        // A difference can only remove; the bounds keep what it might leave. An intersection keeps what both reach.
        if (op == ClipOp.Intersect)
        {
            _state = _state with { ClipBounds = localBounds.IsEmpty ? RectF.Empty : _state.ClipBounds.Intersect(MapRect(localBounds, _state.Matrix)) };
        }
    }

    /// <summary>Strokes the path a stroke's effect makes, with the effect taken off the stroke: backends see the result only.</summary>
    private void DrawWithEffect(Path path, Stroke stroke, Paint paint)
    {
        Path effected = path.Apply(stroke.Effect!);
        if (effected.Points.Length > 0)
        {
            OnDrawPath(effected, stroke with { Effect = null }, paint);
        }
    }

    private void Check(Paint paint)
    {
        CheckLive();
        ArgumentNullException.ThrowIfNull(paint);
    }

    private void Check(Paint paint, Stroke stroke)
    {
        Check(paint);
        ArgumentNullException.ThrowIfNull(stroke);
    }

    private void CheckLive() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static void CheckOp(ClipOp op)
    {
        if (!Enum.IsDefined(op))
        {
            throw new ArgumentOutOfRangeException(nameof(op), op, null);
        }
    }

    private static void CheckRect(RectF rect, string name)
    {
        if (!rect.IsFinite)
        {
            throw new ArgumentException("The rectangle must be finite.", name);
        }
    }

    private static float Finite(float value, string name) => float.IsFinite(value) ? value : throw new ArgumentException("The value must be finite.", name);

    private static Vector2 FinitePoint(Vector2 value, string name) => float.IsFinite(value.X) && float.IsFinite(value.Y) ? value : throw new ArgumentException("The point must be finite.", name);

    private static Matrix3x2 FiniteMatrix(Matrix3x2 m, string name)
    {
        if (!(float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M31) && float.IsFinite(m.M32)))
        {
            throw new ArgumentException("The matrix's elements must be finite.", name);
        }

        return m;
    }

    private readonly record struct State(Matrix3x2 Matrix, RectF ClipBounds, bool IsLayer = false);
}

/// <summary>A drawing call rendered into pixels by <c>Canvas.RasterizeFallback</c>, for a backend to place as an image.</summary>
/// <param name="Image">The pixels, straight-alpha float, over transparency.</param>
/// <param name="DeviceBounds">The device rectangle the image covers.</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="canvas"/>
/// </example>
public sealed record class FallbackImage(Raster<RgbaF32> Image, RectF DeviceBounds);
