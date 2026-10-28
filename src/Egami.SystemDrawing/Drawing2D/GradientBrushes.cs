using System.Numerics;

using Egami.Drawing;
using Egami.GdiPlus;
using Egami.Pixels;
using Egami.SystemDrawing.Internal;

namespace Egami.SystemDrawing.Drawing2D;

using Point = System.Drawing.Point;

/// <summary>A brush of one of GDI+'s 53 hatch patterns: 8 by 8 tiles of a foreground color on a background, from the device origin.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="brushes"/>
/// </example>
public sealed class HatchBrush : Brush
{
    /// <summary>Initializes a new instance of the <see cref="HatchBrush"/> class on black.</summary>
    /// <param name="hatchstyle">The pattern.</param>
    /// <param name="foreColor">The pattern's color.</param>
    public HatchBrush(HatchStyle hatchstyle, Color foreColor)
        : this(hatchstyle, foreColor, Color.FromArgb(255, 0, 0, 0))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="HatchBrush"/> class.</summary>
    /// <param name="hatchstyle">The pattern.</param>
    /// <param name="foreColor">The pattern's color.</param>
    /// <param name="backColor">The color between.</param>
    /// <exception cref="ArgumentException">The style is not one of the 53.</exception>
    public HatchBrush(HatchStyle hatchstyle, Color foreColor, Color backColor)
    {
        if (hatchstyle is < HatchStyle.Horizontal or > HatchStyle.SolidDiamond)
        {
            throw GdiErrors.InvalidParameter();
        }

        HatchStyle = hatchstyle;
        ForegroundColor = Color.FromArgb(foreColor.ToArgb());
        BackgroundColor = Color.FromArgb(backColor.ToArgb());
    }

    /// <summary>Gets the pattern.</summary>
    public HatchStyle HatchStyle { get; }

    /// <summary>Gets the pattern's color.</summary>
    public Color ForegroundColor { get; }

    /// <summary>Gets the color between.</summary>
    public Color BackgroundColor { get; }

    /// <inheritdoc/>
    public override object Clone() => new HatchBrush(HatchStyle, ForegroundColor, BackgroundColor);

    internal override BrushFill Fill(Graphics graphics)
    {
        Picture tile = GdiPlusBrushes.HatchTile((int)HatchStyle, SolidBrush.Rgba(ForegroundColor), SolidBrush.Rgba(BackgroundColor), smoothDiagonals: true);
        Point origin = graphics.RenderingOrigin;
        return new(new PicturePaint(tile, TileMode.Repeat, TileMode.Repeat, new RectF(0, 0, 8, 8)) { LocalMatrix = Matrix3x2.CreateTranslation(origin.X, origin.Y) }, null);
    }
}

/// <summary>
/// A brush of colors along a line: across a rectangle at an angle (or between two points), the colors constant on lines
/// perpendicular to it, with a <see cref="Blend"/> or preset colors, repeating beyond the rectangle.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="brushes"/>
/// </example>
public sealed partial class LinearGradientBrush : Brush
{
    private GradientSettings _settings = new();
    private Color _start;
    private Color _end;
    private WrapMode _wrap = WrapMode.Tile;

    /// <summary>Initializes a new instance of the <see cref="LinearGradientBrush"/> class from one point's color to another's.</summary>
    /// <param name="point1">Where the first color is.</param>
    /// <param name="point2">Where the second color is.</param>
    /// <param name="color1">The first color.</param>
    /// <param name="color2">The second color.</param>
    /// <exception cref="System.Runtime.InteropServices.ExternalException">The points are the same, as in GDI+.</exception>
    public LinearGradientBrush(PointF point1, PointF point2, Color color1, Color color2)
    {
        if (point1 == point2)
        {
            throw GdiErrors.Generic();
        }

        float x = MathF.Min(point1.X, point2.X), y = MathF.Min(point1.Y, point2.Y);
        float w = MathF.Abs(point2.X - point1.X), h = MathF.Abs(point2.Y - point1.Y);
        // A horizontal or vertical pair gets a square, so the gradient has a width across it.
        if (w == 0)
        {
            w = h;
            x -= w / 2;
        }
        else if (h == 0)
        {
            h = w;
            y -= h / 2;
        }

        float angle = MathF.Atan2(point2.Y - point1.Y, point2.X - point1.X) * 180 / MathF.PI;
        Init(new RectangleF(x, y, w, h), color1, color2, angle, false);
    }

    /// <summary>Initializes a new instance of the <see cref="LinearGradientBrush"/> class from one point's color to another's.</summary>
    /// <param name="point1">Where the first color is.</param>
    /// <param name="point2">Where the second color is.</param>
    /// <param name="color1">The first color.</param>
    /// <param name="color2">The second color.</param>
    public LinearGradientBrush(Point point1, Point point2, Color color1, Color color2)
        : this((PointF)point1, (PointF)point2, color1, color2)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="LinearGradientBrush"/> class across a rectangle in a direction.</summary>
    /// <param name="rect">The rectangle.</param>
    /// <param name="color1">The start color.</param>
    /// <param name="color2">The end color.</param>
    /// <param name="linearGradientMode">The direction; the diagonals run corner to corner.</param>
    public LinearGradientBrush(RectangleF rect, Color color1, Color color2, LinearGradientMode linearGradientMode)
    {
        Check(rect);
        (float angle, bool scale) = linearGradientMode switch
        {
            LinearGradientMode.Horizontal => (0f, false),
            LinearGradientMode.Vertical => (90f, false),
            LinearGradientMode.ForwardDiagonal => (45f, true),
            LinearGradientMode.BackwardDiagonal => (135f, true),
            _ => throw new System.ComponentModel.InvalidEnumArgumentException(nameof(linearGradientMode), (int)linearGradientMode, typeof(LinearGradientMode)),
        };
        Init(rect, color1, color2, angle, scale);
    }

    /// <summary>Initializes a new instance of the <see cref="LinearGradientBrush"/> class across a rectangle in a direction.</summary>
    /// <param name="rect">The rectangle.</param>
    /// <param name="color1">The start color.</param>
    /// <param name="color2">The end color.</param>
    /// <param name="linearGradientMode">The direction.</param>
    public LinearGradientBrush(Rectangle rect, Color color1, Color color2, LinearGradientMode linearGradientMode)
        : this((RectangleF)rect, color1, color2, linearGradientMode)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="LinearGradientBrush"/> class across a rectangle at an angle.</summary>
    /// <param name="rect">The rectangle.</param>
    /// <param name="color1">The start color.</param>
    /// <param name="color2">The end color.</param>
    /// <param name="angle">The angle in degrees clockwise from the x axis.</param>
    public LinearGradientBrush(RectangleF rect, Color color1, Color color2, float angle)
        : this(rect, color1, color2, angle, false)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="LinearGradientBrush"/> class across a rectangle at an angle.</summary>
    /// <param name="rect">The rectangle.</param>
    /// <param name="color1">The start color.</param>
    /// <param name="color2">The end color.</param>
    /// <param name="angle">The angle in degrees clockwise from the x axis.</param>
    /// <param name="isAngleScaleable">Whether the angle is taken in the rectangle stretched to a square, as the diagonal modes are.</param>
    public LinearGradientBrush(RectangleF rect, Color color1, Color color2, float angle, bool isAngleScaleable)
    {
        Check(rect);
        Init(rect, color1, color2, angle, isAngleScaleable);
    }

    /// <summary>Initializes a new instance of the <see cref="LinearGradientBrush"/> class across a rectangle at an angle.</summary>
    /// <param name="rect">The rectangle.</param>
    /// <param name="color1">The start color.</param>
    /// <param name="color2">The end color.</param>
    /// <param name="angle">The angle in degrees clockwise from the x axis.</param>
    public LinearGradientBrush(Rectangle rect, Color color1, Color color2, float angle)
        : this((RectangleF)rect, color1, color2, angle, false)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="LinearGradientBrush"/> class across a rectangle at an angle.</summary>
    /// <param name="rect">The rectangle.</param>
    /// <param name="color1">The start color.</param>
    /// <param name="color2">The end color.</param>
    /// <param name="angle">The angle in degrees clockwise from the x axis.</param>
    /// <param name="isAngleScaleable">Whether the angle is taken in the rectangle stretched to a square.</param>
    public LinearGradientBrush(Rectangle rect, Color color1, Color color2, float angle, bool isAngleScaleable)
        : this((RectangleF)rect, color1, color2, angle, isAngleScaleable)
    {
    }

    private LinearGradientBrush()
    {
    }

    /// <summary>Gets the rectangle the gradient runs across, in brush space.</summary>
    public RectangleF Rectangle { get; private set; }

    /// <summary>Gets or sets the start and end colors.</summary>
    /// <exception cref="ArgumentException">Not exactly two colors.</exception>
#pragma warning disable CA1819 // System.Drawing's brush exposes arrays.
    public Color[] LinearColors
    {
        get => [_start, _end];
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Length < 2)
            {
                throw GdiErrors.InvalidParameter();
            }

            (_start, _end) = (Color.FromArgb(value[0].ToArgb()), Color.FromArgb(value[1].ToArgb()));
        }
    }
#pragma warning restore CA1819

    /// <summary>Gets or sets the falloff; null while preset colors are set, as in GDI+.</summary>
    public Blend? Blend
    {
        get => _settings.Blend;
        set => _settings.SetBlend(value!);
    }

    /// <summary>Gets or sets preset colors, which replace the two colors and the blend.</summary>
    public ColorBlend InterpolationColors
    {
        get => _settings.Colors;
        set => _settings.SetColors(value);
    }

    /// <summary>Gets or sets whether the colors mix in linear light (gamma 2.2).</summary>
    public bool GammaCorrection { get; set; }

    /// <summary>Gets or sets how the gradient repeats beyond the rectangle; it cannot be <see cref="WrapMode.Clamp"/>.</summary>
    /// <exception cref="ArgumentException">Clamp, as in GDI+.</exception>
    public WrapMode WrapMode
    {
        get => _wrap;
        set => _wrap = value is >= WrapMode.Tile and < WrapMode.Clamp ? value : throw GdiErrors.InvalidParameter();
    }

    /// <summary>Makes the blend a bell from the normal curve, peaking at a focus.</summary>
    /// <param name="focus">Where the end color is strongest, 0 to 1.</param>
    public void SetSigmaBellShape(float focus) => SetSigmaBellShape(focus, 1);

    /// <summary>Makes the blend a bell from the normal curve, peaking at a focus.</summary>
    /// <param name="focus">Where the end color is strongest, 0 to 1.</param>
    /// <param name="scale">How much of the end color there is at the focus, 0 to 1.</param>
    public void SetSigmaBellShape(float focus, float scale) => _settings.SigmaBell(focus, scale);

    /// <summary>Makes the blend a triangle, peaking at a focus.</summary>
    /// <param name="focus">Where the end color is strongest, 0 to 1.</param>
    public void SetBlendTriangularShape(float focus) => SetBlendTriangularShape(focus, 1);

    /// <summary>Makes the blend a triangle, peaking at a focus.</summary>
    /// <param name="focus">Where the end color is strongest, 0 to 1.</param>
    /// <param name="scale">How much of the end color there is at the focus, 0 to 1.</param>
    public void SetBlendTriangularShape(float focus, float scale) => _settings.Triangular(focus, scale);

    /// <inheritdoc/>
    public override object Clone() => new LinearGradientBrush
    {
        Rectangle = Rectangle,
        _start = _start,
        _end = _end,
        _settings = _settings.Copy(),
        _wrap = _wrap,
        GammaCorrection = GammaCorrection,
        Brushspace = Brushspace,
    };

    internal override BrushFill Fill(Graphics graphics)
    {
        RectangleF r = Rectangle;
        GradientStop[] stops = _settings.Stops(_start, _end, GammaCorrection);
        (TileMode tile, _) = GdiPlusBrushes.Wrap((int)_wrap);
        return new(new LinearGradient(new Vector2(r.X, r.Y), new Vector2(r.Right, r.Y), stops) { LocalMatrix = Brushspace * graphics.ShapeTransform, Tile = tile }, null);
    }

    private static void Check(RectangleF rect)
    {
        if (rect.Width == 0 || rect.Height == 0)
        {
            throw GdiErrors.InvalidParameter();
        }
    }

    /// <summary>
    /// GDI+'s brush transform for an angle: about the rectangle's center, the rectangle's width turned to the angle and stretched
    /// so its ends' perpendiculars pass through the far corners; with a scaleable angle, all of that in the rectangle stretched
    /// to a square.
    /// </summary>
    private void Init(RectangleF rect, Color color1, Color color2, float angle, bool scaleable)
    {
        Rectangle = rect;
        (_start, _end) = (Color.FromArgb(color1.ToArgb()), Color.FromArgb(color2.ToArgb()));
        double a = angle * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
        float w = rect.Width, h = rect.Height;
        Matrix3x2 linear;
        if (scaleable)
        {
            double l = Math.Abs(c) + Math.Abs(s);
            linear = new Matrix3x2((float)(l * c), (float)(l * s * h / w), (float)(-l * s * w / h), (float)(l * c), 0, 0);
        }
        else
        {
            double along = ((w * Math.Abs(c)) + (h * Math.Abs(s))) / w, across = ((w * Math.Abs(s)) + (h * Math.Abs(c))) / h;
            linear = new Matrix3x2((float)(along * c), (float)(along * s), (float)(-across * s), (float)(across * c), 0, 0);
        }

        Vector2 center = new(rect.X + (w / 2), rect.Y + (h / 2));
        Brushspace = Matrix3x2.CreateTranslation(-center) * linear * Matrix3x2.CreateTranslation(center);
    }
}

/// <summary>
/// A brush of colors from a center out to the edge of a polygon or path: the center color at the center point, the surrounding
/// colors at the boundary's points, with a <see cref="Blend"/> or preset colors between.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="brushes"/>
/// </example>
public sealed partial class PathGradientBrush : Brush
{
    private GradientSettings _settings = new();
    private PointF[] _boundary = [];
    private Color[] _surround = [Color.FromArgb(255, 255, 255, 255)];
    private WrapMode _wrap = WrapMode.Clamp;

    /// <summary>Initializes a new instance of the <see cref="PathGradientBrush"/> class over a polygon.</summary>
    /// <param name="points">The polygon.</param>
    public PathGradientBrush(params PointF[] points)
        : this(WrapMode.Clamp, (points ?? throw new ArgumentNullException(nameof(points))).AsSpan())
    {
    }

    /// <summary>Initializes a new instance of the <see cref="PathGradientBrush"/> class over a polygon.</summary>
    /// <param name="points">The polygon.</param>
    public PathGradientBrush(params ReadOnlySpan<PointF> points)
        : this(WrapMode.Clamp, points)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="PathGradientBrush"/> class over a polygon.</summary>
    /// <param name="points">The polygon.</param>
    /// <param name="wrapMode">How the gradient repeats.</param>
    public PathGradientBrush(PointF[] points, WrapMode wrapMode)
        : this(wrapMode, (points ?? throw new ArgumentNullException(nameof(points))).AsSpan())
    {
    }

    /// <summary>Initializes a new instance of the <see cref="PathGradientBrush"/> class over a polygon.</summary>
    /// <param name="wrapMode">How the gradient repeats.</param>
    /// <param name="points">The polygon.</param>
    /// <exception cref="OutOfMemoryException">Fewer than two points, as in GDI+.</exception>
    public PathGradientBrush(WrapMode wrapMode, params ReadOnlySpan<PointF> points)
    {
        if (points.Length < 2)
        {
            throw GdiErrors.OutOfMemory();
        }

        _boundary = points.ToArray();
        _wrap = wrapMode;
        CenterColor = Color.FromArgb(255, 0, 0, 0);
        CenterPoint = Average(_boundary);
    }

    /// <summary>Initializes a new instance of the <see cref="PathGradientBrush"/> class over a polygon.</summary>
    /// <param name="points">The polygon.</param>
    public PathGradientBrush(params Point[] points)
        : this(WrapMode.Clamp, ToF(points ?? throw new ArgumentNullException(nameof(points))))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="PathGradientBrush"/> class over a polygon.</summary>
    /// <param name="points">The polygon.</param>
    public PathGradientBrush(params ReadOnlySpan<Point> points)
        : this(WrapMode.Clamp, ToF(points))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="PathGradientBrush"/> class over a polygon.</summary>
    /// <param name="points">The polygon.</param>
    /// <param name="wrapMode">How the gradient repeats.</param>
    public PathGradientBrush(Point[] points, WrapMode wrapMode)
        : this(wrapMode, ToF(points ?? throw new ArgumentNullException(nameof(points))))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="PathGradientBrush"/> class over a polygon.</summary>
    /// <param name="wrapMode">How the gradient repeats.</param>
    /// <param name="points">The polygon.</param>
    public PathGradientBrush(WrapMode wrapMode, params ReadOnlySpan<Point> points)
        : this(wrapMode, ToF(points))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="PathGradientBrush"/> class over a path, its curves flattened.</summary>
    /// <param name="path">The path.</param>
    public PathGradientBrush(GraphicsPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.PointCount < 2)
        {
            throw GdiErrors.OutOfMemory();
        }

        // The center is the mean of the path's own points, control points included, as GDI+ takes it.
        CenterPoint = Average(path.PathPoints);
        using GraphicsPath flat = (GraphicsPath)path.Clone();
        flat.Flatten();
        _boundary = flat.PathPoints;
        CenterColor = Color.FromArgb(255, 255, 255, 255);
    }

    private PathGradientBrush()
    {
    }

    /// <summary>Gets or sets the center color.</summary>
    public Color CenterColor { get; set; }

    /// <summary>Gets or sets the center point, the mean of the boundary's points by default.</summary>
    public PointF CenterPoint { get; set; }

    /// <summary>Gets or sets the colors at the boundary's points; the last repeats for the rest.</summary>
    /// <exception cref="ArgumentException">More colors than points.</exception>
#pragma warning disable CA1819 // System.Drawing's brush exposes arrays.
    public Color[] SurroundColors
    {
        get => (Color[])_surround.Clone();
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Length == 0 || value.Length > _boundary.Length)
            {
                throw GdiErrors.InvalidParameter();
            }

            _surround = [.. value.Select(c => Color.FromArgb(c.ToArgb()))];
        }
    }
#pragma warning restore CA1819

    /// <summary>Gets or sets the scale of the center color's area; Egami keeps it but draws the plain gradient.</summary>
    public PointF FocusScales { get; set; }

    /// <summary>Gets the bounds of the boundary.</summary>
    public RectangleF Rectangle
    {
        get
        {
            float x0 = _boundary.Min(p => p.X), y0 = _boundary.Min(p => p.Y), x1 = _boundary.Max(p => p.X), y1 = _boundary.Max(p => p.Y);
            return RectangleF.FromLTRB(x0, y0, x1, y1);
        }
    }

    /// <summary>Gets or sets the falloff from the boundary (0) to the center (1); null while preset colors are set.</summary>
    public Blend? Blend
    {
        get => _settings.Blend;
        set => _settings.SetBlend(value!);
    }

    /// <summary>Gets or sets preset colors from the boundary (0) to the center (1).</summary>
    public ColorBlend InterpolationColors
    {
        get => _settings.Colors;
        set => _settings.SetColors(value);
    }

    /// <summary>Gets or sets how the gradient repeats beyond the boundary.</summary>
    public WrapMode WrapMode
    {
        get => _wrap;
        set => _wrap = value is >= WrapMode.Tile and <= WrapMode.Clamp ? value : throw new System.ComponentModel.InvalidEnumArgumentException(nameof(value), (int)value, typeof(WrapMode));
    }

    /// <summary>Makes the blend a bell from the normal curve.</summary>
    /// <param name="focus">Where the center color is strongest, 0 to 1.</param>
    public void SetSigmaBellShape(float focus) => SetSigmaBellShape(focus, 1);

    /// <summary>Makes the blend a bell from the normal curve.</summary>
    /// <param name="focus">Where the center color is strongest, 0 to 1.</param>
    /// <param name="scale">How much of the center color there is at the focus, 0 to 1.</param>
    public void SetSigmaBellShape(float focus, float scale) => _settings.SigmaBell(focus, scale);

    /// <summary>Makes the blend a triangle.</summary>
    /// <param name="focus">Where the center color is strongest, 0 to 1.</param>
    public void SetBlendTriangularShape(float focus) => SetBlendTriangularShape(focus, 1);

    /// <summary>Makes the blend a triangle.</summary>
    /// <param name="focus">Where the center color is strongest, 0 to 1.</param>
    /// <param name="scale">How much of the center color there is at the focus, 0 to 1.</param>
    public void SetBlendTriangularShape(float focus, float scale) => _settings.Triangular(focus, scale);

    /// <inheritdoc/>
    public override object Clone() => new PathGradientBrush
    {
        _settings = _settings.Copy(),
        _boundary = (PointF[])_boundary.Clone(),
        _surround = (Color[])_surround.Clone(),
        _wrap = _wrap,
        CenterColor = CenterColor,
        CenterPoint = CenterPoint,
        FocusScales = FocusScales,
        Brushspace = Brushspace,
    };

    internal override BrushFill Fill(Graphics graphics)
    {
        Vector2[] boundary = [.. _boundary.Select(p => new Vector2(p.X, p.Y))];
        Rgba8[] surround = [.. _surround.Select(SolidBrush.Rgba)];
        GradientStop[]? stops = null;
        bool preset = _settings.Preset is not null;
        bool plain = !preset && _settings.Factors is [1] && _settings.Positions is [0];
        if (!plain)
        {
            // Positions run from the boundary (0) to the center (1); a factor mixes from the surround (0) to the center color (1).
            stops = _settings.Stops(_surround[0], CenterColor, false);
        }

        Mesh? mesh = GdiPlusBrushes.PathGradient(boundary, new Vector2(CenterPoint.X, CenterPoint.Y), SolidBrush.Rgba(CenterColor), surround, stops, Brushspace * graphics.ShapeTransform, preset);
        return new(null, mesh);
    }

    private static PointF Average(PointF[] points) => new(points.Average(p => p.X), points.Average(p => p.Y));

    private static PointF[] ToF(ReadOnlySpan<Point> points)
    {
        PointF[] f = new PointF[points.Length];
        for (int i = 0; i < f.Length; i++)
        {
            f[i] = points[i];
        }

        return f;
    }
}
