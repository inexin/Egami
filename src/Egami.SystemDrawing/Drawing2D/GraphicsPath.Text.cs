using System.Numerics;

using Egami.Drawing;
using Egami.SystemDrawing.Internal;

namespace Egami.SystemDrawing.Drawing2D;

using Point = System.Drawing.Point;

/// <summary>Text outlines, laid out as DrawString lays text out.</summary>
public sealed partial class GraphicsPath
{
    /// <summary>Adds the outlines of text laid out from a point: one closed figure per glyph contour, quadratic curves as Beziers.</summary>
    /// <param name="s">The text.</param>
    /// <param name="family">The family.</param>
    /// <param name="style">The <see cref="FontStyle"/> bits.</param>
    /// <param name="emSize">The em size, in the path's units.</param>
    /// <param name="origin">The layout origin.</param>
    /// <param name="format">The layout, or <see langword="null"/> for the default.</param>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="text"/>
    /// </example>
    public void AddString(string s, FontFamily family, int style, float emSize, PointF origin, StringFormat? format) =>
        AddStringCore(s, family, style, emSize, new RectangleF(origin.X, origin.Y, 0, 0), format);

    /// <summary>Adds the outlines of text laid out from a point.</summary>
    /// <param name="s">The text.</param>
    /// <param name="family">The family.</param>
    /// <param name="style">The <see cref="FontStyle"/> bits.</param>
    /// <param name="emSize">The em size, in the path's units.</param>
    /// <param name="origin">The layout origin.</param>
    /// <param name="format">The layout, or <see langword="null"/> for the default.</param>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="text"/>
    /// </example>
    public void AddString(string s, FontFamily family, int style, float emSize, Point origin, StringFormat? format) =>
        AddStringCore(s, family, style, emSize, new RectangleF(origin.X, origin.Y, 0, 0), format);

    /// <summary>Adds the outlines of text laid out in a rectangle, wrapped at its width; the rectangle does not clip.</summary>
    /// <param name="s">The text.</param>
    /// <param name="family">The family.</param>
    /// <param name="style">The <see cref="FontStyle"/> bits.</param>
    /// <param name="emSize">The em size, in the path's units.</param>
    /// <param name="layoutRect">The layout rectangle.</param>
    /// <param name="format">The layout, or <see langword="null"/> for the default.</param>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="text"/>
    /// </example>
    public void AddString(string s, FontFamily family, int style, float emSize, RectangleF layoutRect, StringFormat? format) =>
        AddStringCore(s, family, style, emSize, layoutRect, format);

    /// <summary>Adds the outlines of text laid out in a rectangle, wrapped at its width.</summary>
    /// <param name="s">The text.</param>
    /// <param name="family">The family.</param>
    /// <param name="style">The <see cref="FontStyle"/> bits.</param>
    /// <param name="emSize">The em size, in the path's units.</param>
    /// <param name="layoutRect">The layout rectangle.</param>
    /// <param name="format">The layout, or <see langword="null"/> for the default.</param>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="text"/>
    /// </example>
    public void AddString(string s, FontFamily family, int style, float emSize, Rectangle layoutRect, StringFormat? format) =>
        AddStringCore(s, family, style, emSize, layoutRect, format);

    private void AddStringCore(string s, FontFamily family, int style, float emSize, RectangleF layoutRect, StringFormat? format)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(family);
        if (s.Length == 0 || !(emSize > 0))
        {
            return;
        }

        Font font = new(family, emSize, (FontStyle)style, GraphicsUnit.World);
        GdiTextLayout layout = GdiTextLayout.Create(s, font, emSize, layoutRect.Size, format);
        Path outline = Graphics.TextOutline(layout, font, new Vector2(layoutRect.X, layoutRect.Y));
        ReadOnlySpan<PathVerb> verbs = outline.Verbs;
        ReadOnlySpan<Vector2> points = outline.Points;
        GdiShapes shapes = new();
        int p = 0;
        bool open = false;
        Vector2 current = default;
        foreach (PathVerb verb in verbs)
        {
            switch (verb)
            {
                case PathVerb.Move:
                    if (open)
                    {
                        shapes.Close();
                    }

                    current = points[p++];
                    shapes.Start(current);
                    open = true;
                    break;
                case PathVerb.Line:
                    current = points[p++];
                    shapes.Line(current);
                    break;
                case PathVerb.Quad:
                    Vector2 c = points[p], end = points[p + 1];
                    p += 2;
                    shapes.Bezier(current + ((c - current) * (2f / 3)), end + ((c - end) * (2f / 3)), end);
                    current = end;
                    break;
                case PathVerb.Cubic:
                    shapes.Bezier(points[p], points[p + 1], points[p + 2]);
                    current = points[p + 2];
                    p += 3;
                    break;
                case PathVerb.Close:
                    shapes.Close();
                    open = false;
                    break;
            }
        }

        if (open)
        {
            shapes.Close();
        }

        Append(shapes, closed: true);
    }
}
