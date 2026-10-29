using Egami.GdiPlus;
using Egami.SystemDrawing.Internal;

namespace Egami.SystemDrawing;

/// <summary>
/// What draws lines and outlines: a brush and a width in world units, at least one device pixel, as in GDI+.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="shapes"/>
/// </example>
public sealed partial class Pen : MarshalByRefObject, ICloneable, IDisposable
{
    private Brush _brush;
    private float _width;

    /// <summary>Initializes a new instance of the <see cref="Pen"/> class, 1 unit wide.</summary>
    /// <param name="color">The color.</param>
    public Pen(Color color)
        : this(color, 1)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Pen"/> class.</summary>
    /// <param name="color">The color.</param>
    /// <param name="width">The width in world units; 0 draws one device pixel wide.</param>
    public Pen(Color color, float width)
        : this(new SolidBrush(color), width)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Pen"/> class, 1 unit wide.</summary>
    /// <param name="brush">The brush the pen paints with.</param>
    public Pen(Brush brush)
        : this(brush, 1)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Pen"/> class.</summary>
    /// <param name="brush">The brush the pen paints with.</param>
    /// <param name="width">The width in world units; 0 draws one device pixel wide.</param>
    public Pen(Brush brush, float width)
    {
        ArgumentNullException.ThrowIfNull(brush);
        _brush = (Brush)brush.Clone();
        _width = width;
    }

    /// <summary>Gets or sets the color; setting it makes the brush a solid brush of that color.</summary>
    /// <exception cref="ArgumentException">The pen paints with a brush other than a solid one, as in GDI+.</exception>
    public Color Color
    {
        get => _brush is SolidBrush solid ? solid.Color : throw GdiErrors.InvalidParameter();
        set
        {
            CheckMutable();
            _brush = new SolidBrush(value);
        }
    }

    /// <summary>Gets or sets a copy of the brush the pen paints with.</summary>
    public Brush Brush
    {
        get => (Brush)_brush.Clone();
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            CheckMutable();
            _brush = (Brush)value.Clone();
        }
    }

    /// <summary>Gets or sets the width in world units.</summary>
    public float Width
    {
        get => _width;
        set
        {
            CheckMutable();
            _width = value;
        }
    }

    /// <summary>Gets a value indicating whether the pen is one of the <c>Pens</c>, which cannot change.</summary>
    internal bool Immutable { get; init; }

    /// <summary>Gets the brush without copying it.</summary>
    internal Brush PaintBrush => _brush;

    /// <summary>A copy, which can change even when this pen cannot.</summary>
    /// <returns>The copy.</returns>
    public object Clone()
    {
        Pen copy = new(_brush, _width);
        CopyTo(copy);
        return copy;
    }

    /// <summary>Releases the pen.</summary>
    /// <exception cref="ArgumentException">The pen is one of the <c>Pens</c>, which cannot be disposed.</exception>
    public void Dispose()
    {
        if (Immutable)
        {
            throw new ArgumentException("Changes cannot be made to Pen because permissions are not valid.");
        }
    }

    /// <summary>The pen's geometry for the shared GDI+ pen code.</summary>
    internal GdiPlusPen Geometry() => GeometryCore(new GdiPlusPen { Width = _width });

    private void CheckMutable()
    {
        if (Immutable)
        {
            throw new ArgumentException("Changes cannot be made to Pen because permissions are not valid.");
        }
    }

    /// <summary>Adds the settings beyond width and brush.</summary>
    private partial GdiPlusPen GeometryCore(GdiPlusPen pen);

    /// <summary>Copies the settings beyond width and brush.</summary>
    private partial void CopyTo(Pen copy);
}
