using System.Numerics;

using Egami.Drawing;
using Egami.GdiPlus;
using Egami.SystemDrawing.Internal;

namespace Egami.SystemDrawing.Drawing2D;

/// <summary>
/// A line end drawn from a path: the path's x axis runs across the line and its y axis along it, outward from the end, in pen
/// widths. The stroke path is outlined; GDI+ refuses fill paths. Set it as a pen's <see cref="Pen.CustomStartCap"/> or
/// <see cref="Pen.CustomEndCap"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="pens"/>
/// </example>
public class CustomLineCap : MarshalByRefObject, ICloneable, IDisposable
{
    private readonly GraphicsPath? _stroke;
    private LineCap _startCap;
    private LineCap _endCap;

    /// <summary>Initializes a new instance of the <see cref="CustomLineCap"/> class.</summary>
    /// <param name="fillPath">The path to fill, or null.</param>
    /// <param name="strokePath">The path to outline, or null.</param>
    public CustomLineCap(GraphicsPath? fillPath, GraphicsPath? strokePath)
        : this(fillPath, strokePath, LineCap.Flat, 0)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="CustomLineCap"/> class.</summary>
    /// <param name="fillPath">The path to fill, or null.</param>
    /// <param name="strokePath">The path to outline, or null.</param>
    /// <param name="baseCap">The cap the line itself ends with.</param>
    public CustomLineCap(GraphicsPath? fillPath, GraphicsPath? strokePath, LineCap baseCap)
        : this(fillPath, strokePath, baseCap, 0)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="CustomLineCap"/> class.</summary>
    /// <param name="fillPath">The path to fill, or null.</param>
    /// <param name="strokePath">The path to outline, or null.</param>
    /// <param name="baseCap">The cap the line itself ends with.</param>
    /// <param name="baseInset">How far the line stops short of its end, in pen widths.</param>
    /// <exception cref="NotImplementedException">A fill path is given: GDI+ does not implement filled custom caps, and neither does Egami.</exception>
    public CustomLineCap(GraphicsPath? fillPath, GraphicsPath? strokePath, LineCap baseCap, float baseInset)
    {
        if (fillPath is not null)
        {
            throw new NotImplementedException("Not implemented.");
        }

        _stroke = (GraphicsPath?)strokePath?.Clone();
        BaseCap = baseCap;
        BaseInset = baseInset;
    }

    /// <summary>Initializes a new instance of the <see cref="CustomLineCap"/> class for an arrow cap.</summary>
    private protected CustomLineCap()
    {
    }

    /// <summary>Gets or sets the cap the line itself ends with.</summary>
    /// <exception cref="ArgumentException">An anchor or custom cap, which GDI+ refuses.</exception>
    public LineCap BaseCap
    {
        get;
        set => field = value is LineCap.Flat or LineCap.Square or LineCap.Round or LineCap.Triangle ? value : throw GdiErrors.InvalidParameter();
    }

    /// <summary>Gets or sets how far the line stops short of its end, in pen widths.</summary>
    public float BaseInset { get; set; }

    /// <summary>Gets or sets how a stroke path's corners join.</summary>
    public LineJoin StrokeJoin { get; set; }

    /// <summary>Gets or sets how much the pen width scales the cap; 1 by default.</summary>
    public float WidthScale { get; set; } = 1;

    /// <summary>Sets the caps of a stroke path's ends.</summary>
    /// <param name="startCap">The start cap.</param>
    /// <param name="endCap">The end cap.</param>
    public void SetStrokeCaps(LineCap startCap, LineCap endCap)
    {
        _startCap = startCap;
        _endCap = endCap;
    }

    /// <summary>Gets the caps of a stroke path's ends.</summary>
    /// <param name="startCap">The start cap.</param>
    /// <param name="endCap">The end cap.</param>
    public void GetStrokeCaps(out LineCap startCap, out LineCap endCap)
    {
        startCap = _startCap;
        endCap = _endCap;
    }

    /// <summary>A copy.</summary>
    /// <returns>The copy.</returns>
    public object Clone() => CloneCore();

    /// <summary>Releases the cap.</summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases the cap.</summary>
    /// <param name="disposing">Whether <see cref="Dispose()"/> was called.</param>
    protected virtual void Dispose(bool disposing)
    {
    }

    /// <summary>The cap for the shared GDI+ pen code.</summary>
    internal virtual GdiPlusCustomCap Geometry()
    {
        Path? shape = null;
        if (_stroke is not null)
        {
            // A stroke path is outlined one unit (one pen width) wide.
            Path outline = _stroke.ToPath();
            shape = new Stroke(1) { Join = StrokeJoin switch { LineJoin.Bevel => Egami.Drawing.LineJoin.Bevel, LineJoin.Round => Egami.Drawing.LineJoin.Round, _ => Egami.Drawing.LineJoin.Miter } }.ToPath(outline);
        }

        return new GdiPlusCustomCap { Shape = shape, BaseInset = BaseInset, DrawShape = true, WidthScale = WidthScale };
    }

    /// <summary>A copy of this cap's kind.</summary>
    private protected virtual CustomLineCap CloneCore()
    {
        CustomLineCap copy = new(null, _stroke, BaseCap, BaseInset) { StrokeJoin = StrokeJoin, WidthScale = WidthScale };
        copy.SetStrokeCaps(_startCap, _endCap);
        return copy;
    }
}

/// <summary>An arrowhead line end, its width and height in pen widths, filled or outlined, with an optional inset middle.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="pens"/>
/// </example>
public sealed class AdjustableArrowCap : CustomLineCap
{
    /// <summary>Initializes a new instance of the <see cref="AdjustableArrowCap"/> class, filled.</summary>
    /// <param name="width">The width, in pen widths.</param>
    /// <param name="height">The height, in pen widths.</param>
    public AdjustableArrowCap(float width, float height)
        : this(width, height, true)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AdjustableArrowCap"/> class.</summary>
    /// <param name="width">The width, in pen widths.</param>
    /// <param name="height">The height, in pen widths.</param>
    /// <param name="isFilled">Whether the arrow is filled.</param>
    public AdjustableArrowCap(float width, float height, bool isFilled)
    {
        Width = width;
        Height = height;
        Filled = isFilled;
    }

    /// <summary>Gets or sets whether the arrow is filled.</summary>
    public bool Filled { get; set; }

    /// <summary>Gets or sets the height, along the line, in pen widths.</summary>
    public float Height { get; set; }

    /// <summary>Gets or sets how far the middle of the base is pulled toward the tip, in pen widths.</summary>
    public float MiddleInset { get; set; }

    /// <summary>Gets or sets the width, across the line, in pen widths.</summary>
    public float Width { get; set; }

    internal override GdiPlusCustomCap Geometry() =>
        new() { Arrow = true, Width = Width, Height = Height, MiddleInset = MiddleInset, Filled = Filled, BaseInset = BaseInset, WidthScale = WidthScale };

    private protected override CustomLineCap CloneCore() =>
        new AdjustableArrowCap(Width, Height, Filled) { MiddleInset = MiddleInset, BaseCap = BaseCap, BaseInset = BaseInset, StrokeJoin = StrokeJoin, WidthScale = WidthScale };
}
