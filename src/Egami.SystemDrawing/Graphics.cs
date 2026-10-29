using System.ComponentModel;
using System.Numerics;

using Egami.Drawing;
using Egami.GdiPlus;
using Egami.Operations;
using Egami.SystemDrawing.Drawing2D;
using Egami.SystemDrawing.Imaging;
using Egami.SystemDrawing.Internal;
using Egami.SystemDrawing.Text;

namespace Egami.SystemDrawing;

using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

/// <summary>
/// Draws on a bitmap, as System.Drawing's Graphics: shapes with pens and brushes, images, clipping, transforms and the page
/// unit, with GDI+'s defaults: aliased shapes (<see cref="SmoothingMode.None"/>), pixel centers on whole coordinates, bilinear
/// image sampling without prefiltering, transforms prepended. Get one with <see cref="FromImage(Image)"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="graphics"/>
/// </example>
public sealed partial class Graphics : MarshalByRefObject, IDisposable, IDeviceContext
{
    private readonly Bitmap _image;
    private readonly List<(int Id, Settings State, bool Container)> _stack = [];
    private Settings _state = new();
    private int _nextId = 1;
    private bool _disposed;
    private InPlaceCanvas? _inPlace;
    private ArgbDrawing _argbDrawing;

    private Graphics(Bitmap image)
    {
        _image = image;
        _argbDrawing = SystemDrawingOptions.ArgbDrawing;
    }

    /// <summary>
    /// Gets or sets how this Graphics draws on a 32-bit ARGB bitmap: in place (the default, from
    /// <see cref="SystemDrawingOptions.ArgbDrawing"/>), or through a premultiplied copy that rounds exactly as GDI+ does. Egami's
    /// own setting; System.Drawing has none.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not an <see cref="SystemDrawing.ArgbDrawing"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="graphics"/>
    /// </example>
    public ArgbDrawing ArgbDrawing
    {
        get => _argbDrawing;
        set => _argbDrawing = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, null);
    }

    /// <summary>Called before a DrawImage draws; returning <see langword="true"/> stops it with an ExternalException, as in GDI+.</summary>
    /// <param name="callbackdata">The data passed to DrawImage.</param>
    /// <returns><see langword="true"/> to stop.</returns>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="images"/>
    /// </example>
    public delegate bool DrawImageAbort(IntPtr callbackdata);

    /// <summary>Gets or sets the world transform, a copy.</summary>
    public Matrix Transform
    {
        get => new(State.World);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            TransformElements = value.MatrixElements;
        }
    }

    /// <summary>Gets or sets the world transform as a .NET matrix.</summary>
    public Matrix3x2 TransformElements
    {
        get => State.World;
        set => State.World = Matrix3x2.Invert(value, out _) ? value : throw GdiErrors.InvalidParameter();
    }

    /// <summary>Gets or sets the unit of page coordinates; <see cref="GraphicsUnit.World"/> cannot be set.</summary>
    /// <exception cref="ArgumentException">The unit is World or not a unit.</exception>
    public GraphicsUnit PageUnit
    {
        get => State.PageUnit;
        set
        {
            if (value is < GraphicsUnit.Display or > GraphicsUnit.Millimeter)
            {
                throw GdiErrors.InvalidParameter();
            }

            State.PageUnit = value;
        }
    }

    /// <summary>Gets or sets the scale of page coordinates; 1 by default.</summary>
    /// <exception cref="ArgumentException">The scale is zero, negative or too large.</exception>
    public float PageScale
    {
        get => State.PageScale;
        set
        {
            if (!(value > 0) || value > 1e9f)
            {
                throw GdiErrors.InvalidParameter();
            }

            State.PageScale = value;
        }
    }

    /// <summary>Gets the horizontal resolution of the bitmap drawn on.</summary>
    public float DpiX => Alive()._image.HorizontalResolution;

    /// <summary>Gets the vertical resolution of the bitmap drawn on.</summary>
    public float DpiY => Alive()._image.VerticalResolution;

    /// <summary>Gets or sets whether shapes are antialiased; <see cref="SmoothingMode.None"/> by default. As in GDI+, Default and HighSpeed read back as None, HighQuality as AntiAlias.</summary>
    public SmoothingMode SmoothingMode
    {
        get => State.Smoothing;
        set => State.Smoothing = value switch
        {
            SmoothingMode.Default or SmoothingMode.HighSpeed or SmoothingMode.None => SmoothingMode.None,
            SmoothingMode.HighQuality or SmoothingMode.AntiAlias => SmoothingMode.AntiAlias,
            _ => throw GdiErrors.InvalidParameter(),
        };
    }

    /// <summary>Gets or sets where pixel centers are.</summary>
    public PixelOffsetMode PixelOffsetMode
    {
        get => State.PixelOffset;
        set => State.PixelOffset = value is PixelOffsetMode.Invalid or < PixelOffsetMode.Invalid or > PixelOffsetMode.Half ? throw GdiErrors.InvalidParameter() : value;
    }

    /// <summary>Gets or sets whether drawing blends or replaces.</summary>
    public CompositingMode CompositingMode
    {
        get => State.Compositing;
        set => State.Compositing = value is < CompositingMode.SourceOver or > CompositingMode.SourceCopy ? throw GdiErrors.InvalidParameter() : value;
    }

    /// <summary>Gets or sets the blending quality; Egami blends the same way for every value.</summary>
    public CompositingQuality CompositingQuality
    {
        get => State.CompositingQuality;
        set => State.CompositingQuality = value is CompositingQuality.Invalid or < CompositingQuality.Invalid or > CompositingQuality.AssumeLinear ? throw GdiErrors.InvalidParameter() : value;
    }

    /// <summary>Gets or sets how images are sampled; <see cref="InterpolationMode.Bilinear"/> by default.</summary>
    public InterpolationMode InterpolationMode
    {
        get => State.Interpolation;
        set => State.Interpolation = value is InterpolationMode.Invalid or < InterpolationMode.Invalid or > InterpolationMode.HighQualityBicubic ? throw GdiErrors.InvalidParameter() : value switch
        {
            InterpolationMode.Default or InterpolationMode.Low => InterpolationMode.Bilinear,
            InterpolationMode.High => InterpolationMode.HighQualityBicubic,
            _ => value,
        };
    }

    /// <summary>Gets or sets how text is rendered.</summary>
    public TextRenderingHint TextRenderingHint
    {
        get => State.TextHint;
        set => State.TextHint = value is < TextRenderingHint.SystemDefault or > TextRenderingHint.ClearTypeGridFit ? throw GdiErrors.InvalidParameter() : value;
    }

    /// <summary>Gets or sets the text contrast, 0 to 12; 4 by default.</summary>
    public int TextContrast
    {
        get => State.TextContrast;
        set => State.TextContrast = value is < 0 or > 12 ? throw GdiErrors.InvalidParameter() : value;
    }

    /// <summary>Gets or sets the origin of hatch brushes and dithering, in device pixels.</summary>
    public Point RenderingOrigin
    {
        get => State.RenderingOrigin;
        set => State.RenderingOrigin = value;
    }

    /// <summary>Gets the state settings and transforms act on.</summary>
    private Settings State => Alive()._state;

    /// <summary>A graphics that draws on a bitmap.</summary>
    /// <param name="image">The bitmap.</param>
    /// <returns>The graphics.</returns>
    /// <exception cref="ArgumentException">The bitmap is indexed.</exception>
    /// <exception cref="System.Runtime.InteropServices.ExternalException">The bitmap is 16-bit gray or 16-bit ARGB 1555, which GDI+ cannot draw on either.</exception>
    public static Graphics FromImage(Image image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image is not Bitmap bitmap)
        {
            // GDI+ would record into a metafile; Egami does not record, so it fails as GDI+ does for a metafile it cannot record to.
            throw GdiErrors.Generic();
        }

        if (GdiPixels.IsIndexed(bitmap.PixelFormat))
        {
            throw new ArgumentException("A Graphics object cannot be created from an image that has an indexed pixel format.", nameof(image));
        }

        if (bitmap.PixelFormat is PixelFormat.Format16bppGrayScale or PixelFormat.Format16bppArgb1555)
        {
            throw GdiErrors.Generic();
        }

        return new Graphics(bitmap);
    }

    /// <summary>Not supported: Windows device contexts. Always throws.</summary>
    /// <param name="hdc">The device context.</param>
    /// <returns>Never returns.</returns>
    [Obsolete(Internal.Diagnostics.HandlesMessage, DiagnosticId = Internal.Diagnostics.Handles, UrlFormat = Internal.Diagnostics.Url)]
    [EditorBrowsable(EditorBrowsableState.Advanced)]
    public static Graphics FromHdc(IntPtr hdc) => throw GdiErrors.WindowsOnly("Graphics.FromHdc", "Draw on a Bitmap with Graphics.FromImage.");

    /// <summary>Not supported: Windows device contexts. Always throws.</summary>
    /// <param name="hdc">The device context.</param>
    /// <param name="hdevice">The device.</param>
    /// <returns>Never returns.</returns>
    [Obsolete(Internal.Diagnostics.HandlesMessage, DiagnosticId = Internal.Diagnostics.Handles, UrlFormat = Internal.Diagnostics.Url)]
    [EditorBrowsable(EditorBrowsableState.Advanced)]
    public static Graphics FromHdc(IntPtr hdc, IntPtr hdevice) => throw GdiErrors.WindowsOnly("Graphics.FromHdc", "Draw on a Bitmap with Graphics.FromImage.");

    /// <summary>Not supported: Windows windows. Always throws.</summary>
    /// <param name="hwnd">The window.</param>
    /// <returns>Never returns.</returns>
    [Obsolete(Internal.Diagnostics.HandlesMessage, DiagnosticId = Internal.Diagnostics.Handles, UrlFormat = Internal.Diagnostics.Url)]
    [EditorBrowsable(EditorBrowsableState.Advanced)]
    public static Graphics FromHwnd(IntPtr hwnd) => throw GdiErrors.WindowsOnly("Graphics.FromHwnd", "Draw on a Bitmap with Graphics.FromImage.");

    /// <summary>Not supported: Windows device contexts. Always throws.</summary>
    /// <param name="hdc">The device context.</param>
    /// <returns>Never returns.</returns>
    [Obsolete(Internal.Diagnostics.HandlesMessage, DiagnosticId = Internal.Diagnostics.Handles, UrlFormat = Internal.Diagnostics.Url)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static Graphics FromHdcInternal(IntPtr hdc) => throw GdiErrors.WindowsOnly("Graphics.FromHdcInternal", "Draw on a Bitmap with Graphics.FromImage.");

    /// <summary>Not supported: Windows windows. Always throws.</summary>
    /// <param name="hwnd">The window.</param>
    /// <returns>Never returns.</returns>
    [Obsolete(Internal.Diagnostics.HandlesMessage, DiagnosticId = Internal.Diagnostics.Handles, UrlFormat = Internal.Diagnostics.Url)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static Graphics FromHwndInternal(IntPtr hwnd) => throw GdiErrors.WindowsOnly("Graphics.FromHwndInternal", "Draw on a Bitmap with Graphics.FromImage.");

    /// <summary>Not supported: Windows device contexts. Always throws.</summary>
    /// <param name="hdc">The device context.</param>
    [Obsolete(Internal.Diagnostics.HandlesMessage, DiagnosticId = Internal.Diagnostics.Handles, UrlFormat = Internal.Diagnostics.Url)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void ReleaseHdcInternal(IntPtr hdc) => throw GdiErrors.WindowsOnly("Graphics.ReleaseHdcInternal", "Draw with the Graphics itself.");

    /// <summary>Not supported: Windows palettes. Always throws.</summary>
    /// <returns>Never returns.</returns>
    [Obsolete(Internal.Diagnostics.HandlesMessage, DiagnosticId = Internal.Diagnostics.Handles, UrlFormat = Internal.Diagnostics.Url)]
    public static IntPtr GetHalftonePalette() => throw GdiErrors.WindowsOnly("Graphics.GetHalftonePalette", "Use new ColorPalette(PaletteType.FixedHalftone256).");

    /// <summary>Not supported: Windows device contexts. Always throws.</summary>
    /// <returns>Never returns.</returns>
    [Obsolete(Internal.Diagnostics.HandlesMessage, DiagnosticId = Internal.Diagnostics.Handles, UrlFormat = Internal.Diagnostics.Url)]
    public IntPtr GetHdc() => throw GdiErrors.WindowsOnly("Graphics.GetHdc", "Draw with the Graphics itself.");

    /// <summary>Not supported: Windows device contexts. Always throws.</summary>
    /// <param name="hdc">The device context.</param>
    [Obsolete(Internal.Diagnostics.HandlesMessage, DiagnosticId = Internal.Diagnostics.Handles, UrlFormat = Internal.Diagnostics.Url)]
    [EditorBrowsable(EditorBrowsableState.Advanced)]
    public void ReleaseHdc(IntPtr hdc) => throw GdiErrors.WindowsOnly("Graphics.ReleaseHdc", "Draw with the Graphics itself.");

    /// <summary>Not supported: Windows device contexts. Always throws.</summary>
    [Obsolete(Internal.Diagnostics.HandlesMessage, DiagnosticId = Internal.Diagnostics.Handles, UrlFormat = Internal.Diagnostics.Url)]
    public void ReleaseHdc() => throw GdiErrors.WindowsOnly("Graphics.ReleaseHdc", "Draw with the Graphics itself.");

    /// <summary>Not supported: screen capture. Always throws.</summary>
    /// <param name="upperLeftSource">The screen point.</param>
    /// <param name="upperLeftDestination">The bitmap point.</param>
    /// <param name="blockRegionSize">The size.</param>
    [Obsolete(Internal.Diagnostics.ScreenMessage, DiagnosticId = Internal.Diagnostics.Screen, UrlFormat = Internal.Diagnostics.Url)]
    public void CopyFromScreen(Point upperLeftSource, Point upperLeftDestination, Size blockRegionSize) => throw GdiErrors.WindowsOnly("Graphics.CopyFromScreen", "There is no screen to copy from.");

    /// <summary>Not supported: screen capture. Always throws.</summary>
    /// <param name="upperLeftSource">The screen point.</param>
    /// <param name="upperLeftDestination">The bitmap point.</param>
    /// <param name="blockRegionSize">The size.</param>
    /// <param name="copyPixelOperation">The raster operation.</param>
    [Obsolete(Internal.Diagnostics.ScreenMessage, DiagnosticId = Internal.Diagnostics.Screen, UrlFormat = Internal.Diagnostics.Url)]
    public void CopyFromScreen(Point upperLeftSource, Point upperLeftDestination, Size blockRegionSize, CopyPixelOperation copyPixelOperation) => throw GdiErrors.WindowsOnly("Graphics.CopyFromScreen", "There is no screen to copy from.");

    /// <summary>Not supported: screen capture. Always throws.</summary>
    /// <param name="sourceX">The screen x.</param>
    /// <param name="sourceY">The screen y.</param>
    /// <param name="destinationX">The bitmap x.</param>
    /// <param name="destinationY">The bitmap y.</param>
    /// <param name="blockRegionSize">The size.</param>
    [Obsolete(Internal.Diagnostics.ScreenMessage, DiagnosticId = Internal.Diagnostics.Screen, UrlFormat = Internal.Diagnostics.Url)]
    public void CopyFromScreen(int sourceX, int sourceY, int destinationX, int destinationY, Size blockRegionSize) => throw GdiErrors.WindowsOnly("Graphics.CopyFromScreen", "There is no screen to copy from.");

    /// <summary>Not supported: screen capture. Always throws.</summary>
    /// <param name="sourceX">The screen x.</param>
    /// <param name="sourceY">The screen y.</param>
    /// <param name="destinationX">The bitmap x.</param>
    /// <param name="destinationY">The bitmap y.</param>
    /// <param name="blockRegionSize">The size.</param>
    /// <param name="copyPixelOperation">The raster operation.</param>
    [Obsolete(Internal.Diagnostics.ScreenMessage, DiagnosticId = Internal.Diagnostics.Screen, UrlFormat = Internal.Diagnostics.Url)]
    public void CopyFromScreen(int sourceX, int sourceY, int destinationX, int destinationY, Size blockRegionSize, CopyPixelOperation copyPixelOperation) => throw GdiErrors.WindowsOnly("Graphics.CopyFromScreen", "There is no screen to copy from.");

    /// <summary>Releases the graphics; the bitmap keeps what was drawn.</summary>
    public void Dispose()
    {
        _disposed = true;
        _canvas?.Dispose();
        _inPlace?.Dispose();
        _inPlace = null;
    }

    /// <summary>Does nothing: Egami draws each call at once.</summary>
    public void Flush()
    {
        Alive();
    }

    /// <summary>Does nothing: Egami draws each call at once.</summary>
    /// <param name="intention">Whether to wait; the same either way.</param>
    public void Flush(FlushIntention intention)
    {
        Alive();
    }

    /// <summary>Fills the area inside the clip with a color, replacing what is there.</summary>
    /// <param name="color">The color.</param>
    public void Clear(Color color)
    {
        Draw(null, canvas => canvas.DrawPaint(new SolidPaint(SolidBrush.Rgba(color)) { BlendMode = BlendMode.Source, Antialias = false }));
    }

    /// <summary>The nearest color GDI+ reports for the bitmap's pixel format: the 16-bit formats cut the low bits; the others keep the color.</summary>
    /// <param name="color">The color.</param>
    /// <returns>The nearest color.</returns>
    public Color GetNearestColor(Color color) => Alive()._image.PixelFormat switch
    {
        // GDI+ cuts the low bits and drops the alpha, without the bit replication the pixels themselves get.
        PixelFormat.Format16bppRgb555 => Color.FromArgb(255, color.R & 0xF8, color.G & 0xF8, color.B & 0xF8),
        PixelFormat.Format16bppRgb565 => Color.FromArgb(255, color.R & 0xF8, color.G & 0xFC, color.B & 0xF8),
        _ => color,
    };

    /// <summary>Multiplies the world transform by a matrix, applied before it.</summary>
    /// <param name="matrix">The matrix.</param>
    public void MultiplyTransform(Matrix matrix) => MultiplyTransform(matrix, MatrixOrder.Prepend);

    /// <summary>Multiplies the world transform by a matrix, applied before or after it.</summary>
    /// <param name="matrix">The matrix.</param>
    /// <param name="order">Whether it applies before or after.</param>
    public void MultiplyTransform(Matrix matrix, MatrixOrder order)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        Apply(matrix.MatrixElements, order);
    }

    /// <summary>Makes the world transform the identity.</summary>
    public void ResetTransform() => State.World = Matrix3x2.Identity;

    /// <summary>Translates the world transform, before it.</summary>
    /// <param name="dx">The horizontal distance.</param>
    /// <param name="dy">The vertical distance.</param>
    public void TranslateTransform(float dx, float dy) => TranslateTransform(dx, dy, MatrixOrder.Prepend);

    /// <summary>Translates the world transform, before or after it.</summary>
    /// <param name="dx">The horizontal distance.</param>
    /// <param name="dy">The vertical distance.</param>
    /// <param name="order">Whether it applies before or after.</param>
    public void TranslateTransform(float dx, float dy, MatrixOrder order) => Apply(Matrix3x2.CreateTranslation(dx, dy), order);

    /// <summary>Scales the world transform, before it.</summary>
    /// <param name="sx">The horizontal factor.</param>
    /// <param name="sy">The vertical factor.</param>
    public void ScaleTransform(float sx, float sy) => ScaleTransform(sx, sy, MatrixOrder.Prepend);

    /// <summary>Scales the world transform, before or after it.</summary>
    /// <param name="sx">The horizontal factor.</param>
    /// <param name="sy">The vertical factor.</param>
    /// <param name="order">Whether it applies before or after.</param>
    public void ScaleTransform(float sx, float sy, MatrixOrder order) => Apply(Matrix3x2.CreateScale(sx, sy), order);

    /// <summary>Rotates the world transform clockwise, before it.</summary>
    /// <param name="angle">The angle in degrees.</param>
    public void RotateTransform(float angle) => RotateTransform(angle, MatrixOrder.Prepend);

    /// <summary>Rotates the world transform clockwise, before or after it.</summary>
    /// <param name="angle">The angle in degrees.</param>
    /// <param name="order">Whether it applies before or after.</param>
    public void RotateTransform(float angle, MatrixOrder order) => Apply(Matrix.Rotation(angle), order);

    /// <summary>Transforms points from one coordinate space to another.</summary>
    /// <param name="destSpace">The space to transform to.</param>
    /// <param name="srcSpace">The space the points are in.</param>
    /// <param name="pts">The points, changed in place.</param>
    public void TransformPoints(CoordinateSpace destSpace, CoordinateSpace srcSpace, params PointF[] pts)
    {
        ArgumentNullException.ThrowIfNull(pts);
        TransformPoints(destSpace, srcSpace, pts.AsSpan());
    }

    /// <summary>Transforms points from one coordinate space to another.</summary>
    /// <param name="destSpace">The space to transform to.</param>
    /// <param name="srcSpace">The space the points are in.</param>
    /// <param name="pts">The points, changed in place.</param>
    public void TransformPoints(CoordinateSpace destSpace, CoordinateSpace srcSpace, params ReadOnlySpan<PointF> pts) => Matrix.Points(pts, SpaceTransform(destSpace, srcSpace));

    /// <summary>Transforms points from one coordinate space to another, rounding to whole coordinates.</summary>
    /// <param name="destSpace">The space to transform to.</param>
    /// <param name="srcSpace">The space the points are in.</param>
    /// <param name="pts">The points, changed in place.</param>
    public void TransformPoints(CoordinateSpace destSpace, CoordinateSpace srcSpace, params Point[] pts)
    {
        ArgumentNullException.ThrowIfNull(pts);
        TransformPoints(destSpace, srcSpace, pts.AsSpan());
    }

    /// <summary>Transforms points from one coordinate space to another, rounding to whole coordinates.</summary>
    /// <param name="destSpace">The space to transform to.</param>
    /// <param name="srcSpace">The space the points are in.</param>
    /// <param name="pts">The points, changed in place.</param>
    public void TransformPoints(CoordinateSpace destSpace, CoordinateSpace srcSpace, params ReadOnlySpan<Point> pts) => Matrix.Points(pts, SpaceTransform(destSpace, srcSpace));

    /// <summary>Saves the transform, page settings, clip and quality settings.</summary>
    /// <returns>The state, for <see cref="Restore(GraphicsState)"/>.</returns>
    public GraphicsState Save()
    {
        int id = _nextId++;
        _stack.Add((id, State.Copy(), false));
        return new GraphicsState(id);
    }

    /// <summary>Restores a saved state, dropping every state and container saved after it.</summary>
    /// <param name="gstate">The state.</param>
    public void Restore(GraphicsState gstate)
    {
        ArgumentNullException.ThrowIfNull(gstate);
        Pop(gstate.Id);
    }

    /// <summary>
    /// Starts a container: inside it the transform, page unit, clip and quality settings start again from their defaults, drawn
    /// through the transform at this point and kept inside the clip at this point.
    /// </summary>
    /// <returns>The container, for <see cref="EndContainer(GraphicsContainer)"/>.</returns>
    public GraphicsContainer BeginContainer() => Begin(Matrix3x2.Identity);

    /// <summary>Starts a container that maps a source rectangle onto a destination rectangle.</summary>
    /// <param name="dstrect">The destination, in the current world coordinates.</param>
    /// <param name="srcrect">The source, in the container's coordinates.</param>
    /// <param name="unit">The unit of the source.</param>
    /// <returns>The container.</returns>
    public GraphicsContainer BeginContainer(RectangleF dstrect, RectangleF srcrect, GraphicsUnit unit)
    {
        // The source is in the unit, and the container's own world units are pixels (its page unit is Display).
        float scale = UnitScale(unit);
        float sx = srcrect.Width == 0 ? 1 : dstrect.Width / (srcrect.Width * scale), sy = srcrect.Height == 0 ? 1 : dstrect.Height / (srcrect.Height * scale);
        Matrix3x2 map = Matrix3x2.CreateTranslation(-srcrect.X * scale, -srcrect.Y * scale) * Matrix3x2.CreateScale(sx, sy) * Matrix3x2.CreateTranslation(dstrect.X, dstrect.Y);
        return Begin(map);
    }

    /// <summary>Starts a container that maps a source rectangle onto a destination rectangle.</summary>
    /// <param name="dstrect">The destination, in the current world coordinates.</param>
    /// <param name="srcrect">The source, in the container's coordinates.</param>
    /// <param name="unit">The unit of the source.</param>
    /// <returns>The container.</returns>
    public GraphicsContainer BeginContainer(Rectangle dstrect, Rectangle srcrect, GraphicsUnit unit) => BeginContainer((RectangleF)dstrect, (RectangleF)srcrect, unit);

    /// <summary>Ends a container, restoring the state from before it.</summary>
    /// <param name="container">The container.</param>
    public void EndContainer(GraphicsContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);
        Pop(container.Id);
    }

    /// <summary>World coordinates to device pixels for shapes: container, world, page unit and scale, then GDI+'s pixel offset.</summary>
    internal Matrix3x2 ShapeTransform
    {
        get
        {
            Matrix3x2 m = ImageTransform;
            return Offset ? m : m * Matrix3x2.CreateTranslation(0.5f, 0.5f);
        }
    }

    /// <summary>Gets a value indicating whether pixel centers are half a pixel off whole coordinates, as Egami puts them.</summary>
    internal bool Offset => State.PixelOffset is PixelOffsetMode.Half or PixelOffsetMode.HighQuality;

    /// <summary>World coordinates to device pixels without the pixel offset, which GDI+ does not apply to images.</summary>
    internal Matrix3x2 ImageTransform => State.World * Page * State.Container;

    /// <summary>The clip drawing is kept in, in device pixels: the clip together with the clip of every container around it.</summary>
    internal Path? EffectiveClip => State.OuterClip is not Path outer ? State.Clip : State.Clip is not Path clip ? outer : PixelScans.Snap(GdiPlusRegions.Combine(outer, clip, GdiPlusRegions.Intersect)!);

    /// <summary>Gets a value indicating whether shapes are antialiased.</summary>
    internal bool Smooth => State.Smoothing is SmoothingMode.HighQuality or SmoothingMode.AntiAlias;

    /// <summary>Gets the blend mode for the compositing mode.</summary>
    internal BlendMode Blend => State.Compositing == CompositingMode.SourceCopy ? BlendMode.Source : BlendMode.SourceOver;

    /// <summary>The page transform: the page unit and scale, from page coordinates to device pixels.</summary>
    private Matrix3x2 Page => Matrix3x2.CreateScale(PageFactor);

    /// <summary>Device pixels per page unit; GDI+ ignores the page scale for Display units on a bitmap.</summary>
    internal float PageFactor => State.PageUnit == GraphicsUnit.Display ? 1 : UnitScale(State.PageUnit) * State.PageScale;

    /// <summary>Device pixels for one unit, at the bitmap's resolution; display units are pixels on a bitmap.</summary>
    internal float UnitScale(GraphicsUnit unit) => unit switch
    {
        GraphicsUnit.Point => DpiX / 72,
        GraphicsUnit.Inch => DpiX,
        GraphicsUnit.Document => DpiX / 300,
        GraphicsUnit.Millimeter => DpiX / 25.4f,
        _ => 1,
    };

    /// <summary>Runs a drawing call on the bitmap, clipped, with device coordinates.</summary>
    internal void Draw(RectF? bounds, Action<Canvas> draw)
    {
        Path? clip = EffectiveClip;
        if (clip is not null && clip.IsEmpty)
        {
            return;
        }

        RectF? area = bounds;
        if (clip is not null)
        {
            RectF c = clip.Bounds;
            area = area is RectF a ? Intersect(a, c) : c;
        }

        Surface.Draw(
            _image.Current,
            area,
            canvas =>
            {
                if (clip is not null)
                {
                    canvas.ClipPath(clip, ClipOp.Intersect, antialias: false);
                }

                draw(canvas);
            },
            ref _inPlace,
            State.Compositing == CompositingMode.SourceCopy || _argbDrawing == ArgbDrawing.Exact);
    }

    private static RectF Intersect(RectF a, RectF b)
    {
        float x0 = MathF.Max(a.X, b.X), y0 = MathF.Max(a.Y, b.Y), x1 = MathF.Min(a.Right, b.Right), y1 = MathF.Min(a.Bottom, b.Bottom);
        return new RectF(x0, y0, MathF.Max(0, x1 - x0), MathF.Max(0, y1 - y0));
    }

    private GraphicsContainer Begin(Matrix3x2 map)
    {
        int id = _nextId++;
        Matrix3x2 full = map * ImageTransform;
        Path? clip = EffectiveClip;
        _stack.Add((id, State.Copy(), true));
        _state = new Settings { Container = full, OuterClip = clip };
        return new GraphicsContainer(id);
    }

    private void Pop(int id)
    {
        int at = _stack.FindIndex(e => e.Id == id);
        if (at < 0)
        {
            return;
        }

        _state = _stack[at].State;
        _stack.RemoveRange(at, _stack.Count - at);
    }

    private void Apply(Matrix3x2 m, MatrixOrder order) => State.World = order == MatrixOrder.Append ? State.World * m : m * State.World;

    private Matrix3x2 SpaceTransform(CoordinateSpace to, CoordinateSpace from)
    {
        if (to is < CoordinateSpace.World or > CoordinateSpace.Device || from is < CoordinateSpace.World or > CoordinateSpace.Device)
        {
            throw GdiErrors.InvalidParameter();
        }

        Matrix3x2 ToDevice(CoordinateSpace space) => space switch
        {
            CoordinateSpace.World => ImageTransform,
            CoordinateSpace.Page => Page * State.Container,
            _ => Matrix3x2.Identity,
        };
        Matrix3x2 forward = ToDevice(from);
        if (!Matrix3x2.Invert(ToDevice(to), out Matrix3x2 back))
        {
            throw GdiErrors.InvalidParameter();
        }

        return forward * back;
    }

    private Graphics Alive()
    {
        if (_disposed)
        {
            throw GdiErrors.InvalidParameter();
        }

        _image.CheckAlive();
        return this;
    }

    /// <summary>What Save, Restore and containers keep.</summary>
    private sealed class Settings
    {
        public Matrix3x2 World { get; set; } = Matrix3x2.Identity;

        /// <summary>Gets the transform of the containers around this state, from their page space to device pixels.</summary>
        public Matrix3x2 Container { get; init; } = Matrix3x2.Identity;

        public GraphicsUnit PageUnit { get; set; } = GraphicsUnit.Display;

        public float PageScale { get; set; } = 1;

        /// <summary>Gets or sets the clip in device pixels, or null for none.</summary>
        public Path? Clip { get; set; }

        /// <summary>Gets the clip of the containers around this state, in device pixels, or null for none.</summary>
        public Path? OuterClip { get; init; }

        public SmoothingMode Smoothing { get; set; } = SmoothingMode.None;

        public PixelOffsetMode PixelOffset { get; set; } = PixelOffsetMode.Default;

        public CompositingMode Compositing { get; set; } = CompositingMode.SourceOver;

        public CompositingQuality CompositingQuality { get; set; } = CompositingQuality.Default;

        public InterpolationMode Interpolation { get; set; } = InterpolationMode.Bilinear;

        public TextRenderingHint TextHint { get; set; } = TextRenderingHint.SystemDefault;

        public int TextContrast { get; set; } = 4;

        public Point RenderingOrigin { get; set; }

        public Settings Copy() => (Settings)MemberwiseClone();
    }
}
