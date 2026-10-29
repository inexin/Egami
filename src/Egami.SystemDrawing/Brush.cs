using Egami.Drawing;
using Egami.Pixels;
using Egami.SystemDrawing.Internal;

namespace Egami.SystemDrawing;

/// <summary>What fills shapes: the base of <see cref="SolidBrush"/> and the other brushes.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="shapes"/>
/// </example>
public abstract class Brush : MarshalByRefObject, ICloneable, IDisposable
{
    /// <summary>Initializes a new instance of the <see cref="Brush"/> class.</summary>
    protected Brush()
    {
    }

    /// <summary>A copy.</summary>
    /// <returns>The copy.</returns>
    public abstract object Clone();

    /// <summary>Releases the brush.</summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Not supported: native GDI+ brushes. Always throws.</summary>
    /// <param name="brush">The native brush.</param>
    protected internal void SetNativeBrush(IntPtr brush) => throw GdiErrors.WindowsOnly("Brush.SetNativeBrush", "Derive from one of the brush classes instead.");

    /// <summary>Releases the brush.</summary>
    /// <param name="disposing">Whether <see cref="Dispose()"/> was called.</param>
    protected virtual void Dispose(bool disposing)
    {
    }

    /// <summary>Finishes nothing: brushes hold no unmanaged memory.</summary>
    ~Brush()
    {
        Dispose(false);
    }

    /// <summary>What the brush paints with on a graphics: a paint for device space, or a mesh for path gradients.</summary>
    internal abstract BrushFill Fill(Graphics graphics);
}

/// <summary>A brush of one color.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="shapes"/>
/// </example>
public sealed class SolidBrush : Brush
{
    private readonly bool _immutable;
    private Color _color;

    /// <summary>Initializes a new instance of the <see cref="SolidBrush"/> class.</summary>
    /// <param name="color">The color.</param>
    public SolidBrush(Color color)
    {
        _color = color;
    }

    internal SolidBrush(Color color, bool immutable)
        : this(color)
    {
        _immutable = immutable;
    }

    /// <summary>Gets or sets the color.</summary>
    /// <exception cref="ArgumentException">The brush is one of the <c>Brushes</c>, which cannot change.</exception>
    public Color Color
    {
        get => _color;
        set
        {
            if (_immutable)
            {
                throw new ArgumentException("Changes cannot be made to Brush because permissions are not valid.");
            }

            _color = value;
        }
    }

    /// <summary>A copy, which can change even when this brush cannot.</summary>
    /// <returns>The copy.</returns>
    public override object Clone() => new SolidBrush(_color);

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (_immutable && disposing)
        {
            throw new ArgumentException("Changes cannot be made to Brush because permissions are not valid.");
        }

        base.Dispose(disposing);
    }

    internal override BrushFill Fill(Graphics graphics) => new(new SolidPaint(Rgba(_color)), null);

    internal static Rgba8 Rgba(Color c) => new(c.R, c.G, c.B, c.A);
}

/// <summary>What a brush paints with: a paint in device space, or a Gouraud mesh clipped to the shape.</summary>
internal readonly record struct BrushFill(Paint? Paint, Mesh? Mesh);
