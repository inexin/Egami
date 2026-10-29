// Written by tools/sysdrawing-text-overloads.py; each overload forwards to a core in Graphics.Text.cs.
namespace Egami.SystemDrawing;

/// <summary>The text overloads, each a call to the layout core in Graphics.Text.cs.</summary>
public sealed partial class Graphics
{
    /// <summary>Draws text from a point.</summary>
    /// <param name="s">The text.</param>
    /// <param name="font">The font.</param>
    /// <param name="brush">The brush.</param>
    /// <param name="x">The left of the layout origin.</param>
    /// <param name="y">The top of the layout origin.</param>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="text"/>
    /// </example>
    public void DrawString(string? s, Font font, Brush brush, float x, float y) => DrawStringCore(s.AsSpan(), font, brush, new RectangleF(x, y, 0, 0), null);

    /// <summary>Draws text from a point.</summary>
    /// <param name="s">The text.</param>
    /// <param name="font">The font.</param>
    /// <param name="brush">The brush.</param>
    /// <param name="point">The layout origin: the text starts 1/6 em right of it with the default format.</param>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="text"/>
    /// </example>
    public void DrawString(string? s, Font font, Brush brush, PointF point) => DrawStringCore(s.AsSpan(), font, brush, new RectangleF(point.X, point.Y, 0, 0), null);

    /// <summary>Draws text from a point with a format.</summary>
    /// <param name="s">The text.</param>
    /// <param name="font">The font.</param>
    /// <param name="brush">The brush.</param>
    /// <param name="x">The left of the layout origin.</param>
    /// <param name="y">The top of the layout origin.</param>
    /// <param name="format">The layout, or <see langword="null"/> for the default.</param>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="text"/>
    /// </example>
    public void DrawString(string? s, Font font, Brush brush, float x, float y, StringFormat? format) => DrawStringCore(s.AsSpan(), font, brush, new RectangleF(x, y, 0, 0), format);

    /// <summary>Draws text from a point with a format.</summary>
    /// <param name="s">The text.</param>
    /// <param name="font">The font.</param>
    /// <param name="brush">The brush.</param>
    /// <param name="point">The layout origin: the text starts 1/6 em right of it with the default format.</param>
    /// <param name="format">The layout, or <see langword="null"/> for the default.</param>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="text"/>
    /// </example>
    public void DrawString(string? s, Font font, Brush brush, PointF point, StringFormat? format) => DrawStringCore(s.AsSpan(), font, brush, new RectangleF(point.X, point.Y, 0, 0), format);

    /// <summary>Draws text in a rectangle, wrapped at its width.</summary>
    /// <param name="s">The text.</param>
    /// <param name="font">The font.</param>
    /// <param name="brush">The brush.</param>
    /// <param name="layoutRectangle">The rectangle the text is laid out, wrapped and clipped in.</param>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="text"/>
    /// </example>
    public void DrawString(string? s, Font font, Brush brush, RectangleF layoutRectangle) => DrawStringCore(s.AsSpan(), font, brush, layoutRectangle, null);

    /// <summary>Draws text in a rectangle with a format.</summary>
    /// <param name="s">The text.</param>
    /// <param name="font">The font.</param>
    /// <param name="brush">The brush.</param>
    /// <param name="layoutRectangle">The rectangle the text is laid out, wrapped and clipped in.</param>
    /// <param name="format">The layout, or <see langword="null"/> for the default.</param>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="text"/>
    /// </example>
    public void DrawString(string? s, Font font, Brush brush, RectangleF layoutRectangle, StringFormat? format) => DrawStringCore(s.AsSpan(), font, brush, layoutRectangle, format);

    /// <summary>Draws text from a point.</summary>
    /// <param name="s">The text.</param>
    /// <param name="font">The font.</param>
    /// <param name="brush">The brush.</param>
    /// <param name="x">The left of the layout origin.</param>
    /// <param name="y">The top of the layout origin.</param>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="text"/>
    /// </example>
    public void DrawString(ReadOnlySpan<char> s, Font font, Brush brush, float x, float y) => DrawStringCore(s, font, brush, new RectangleF(x, y, 0, 0), null);

    /// <summary>Draws text from a point.</summary>
    /// <param name="s">The text.</param>
    /// <param name="font">The font.</param>
    /// <param name="brush">The brush.</param>
    /// <param name="point">The layout origin: the text starts 1/6 em right of it with the default format.</param>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="text"/>
    /// </example>
    public void DrawString(ReadOnlySpan<char> s, Font font, Brush brush, PointF point) => DrawStringCore(s, font, brush, new RectangleF(point.X, point.Y, 0, 0), null);

    /// <summary>Draws text from a point with a format.</summary>
    /// <param name="s">The text.</param>
    /// <param name="font">The font.</param>
    /// <param name="brush">The brush.</param>
    /// <param name="x">The left of the layout origin.</param>
    /// <param name="y">The top of the layout origin.</param>
    /// <param name="format">The layout, or <see langword="null"/> for the default.</param>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="text"/>
    /// </example>
    public void DrawString(ReadOnlySpan<char> s, Font font, Brush brush, float x, float y, StringFormat? format) => DrawStringCore(s, font, brush, new RectangleF(x, y, 0, 0), format);

    /// <summary>Draws text from a point with a format.</summary>
    /// <param name="s">The text.</param>
    /// <param name="font">The font.</param>
    /// <param name="brush">The brush.</param>
    /// <param name="point">The layout origin: the text starts 1/6 em right of it with the default format.</param>
    /// <param name="format">The layout, or <see langword="null"/> for the default.</param>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="text"/>
    /// </example>
    public void DrawString(ReadOnlySpan<char> s, Font font, Brush brush, PointF point, StringFormat? format) => DrawStringCore(s, font, brush, new RectangleF(point.X, point.Y, 0, 0), format);

    /// <summary>Draws text in a rectangle, wrapped at its width.</summary>
    /// <param name="s">The text.</param>
    /// <param name="font">The font.</param>
    /// <param name="brush">The brush.</param>
    /// <param name="layoutRectangle">The rectangle the text is laid out, wrapped and clipped in.</param>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="text"/>
    /// </example>
    public void DrawString(ReadOnlySpan<char> s, Font font, Brush brush, RectangleF layoutRectangle) => DrawStringCore(s, font, brush, layoutRectangle, null);

    /// <summary>Draws text in a rectangle with a format.</summary>
    /// <param name="s">The text.</param>
    /// <param name="font">The font.</param>
    /// <param name="brush">The brush.</param>
    /// <param name="layoutRectangle">The rectangle the text is laid out, wrapped and clipped in.</param>
    /// <param name="format">The layout, or <see langword="null"/> for the default.</param>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="text"/>
    /// </example>
    public void DrawString(ReadOnlySpan<char> s, Font font, Brush brush, RectangleF layoutRectangle, StringFormat? format) => DrawStringCore(s, font, brush, layoutRectangle, format);

    /// <summary>Measures text on one line per line break.</summary>
    /// <param name="text">The text.</param>
    /// <param name="font">The font.</param>
    /// <returns>The size: the widest line and the padding by the lines' height and an eighth em, in world units.</returns>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="text"/>
    /// </example>
    public SizeF MeasureString(string? text, Font font) => MeasureStringCore(text.AsSpan(), font, SizeF.Empty, null, out _, out _);

    /// <summary>Measures text wrapped at a width.</summary>
    /// <param name="text">The text.</param>
    /// <param name="font">The font.</param>
    /// <param name="width">The width to wrap at.</param>
    /// <returns>The size: the widest line and the padding by the lines' height and an eighth em, in world units.</returns>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="text"/>
    /// </example>
    public SizeF MeasureString(string? text, Font font, int width) => MeasureStringCore(text.AsSpan(), font, new SizeF(width, 0), null, out _, out _);

    /// <summary>Measures text wrapped at a width with a format.</summary>
    /// <param name="text">The text.</param>
    /// <param name="font">The font.</param>
    /// <param name="width">The width to wrap at.</param>
    /// <param name="format">The layout, or <see langword="null"/> for the default.</param>
    /// <returns>The size: the widest line and the padding by the lines' height and an eighth em, in world units.</returns>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="text"/>
    /// </example>
    public SizeF MeasureString(string? text, Font font, int width, StringFormat? format) => MeasureStringCore(text.AsSpan(), font, new SizeF(width, 0), format, out _, out _);

    /// <summary>Measures text laid out in an area.</summary>
    /// <param name="text">The text.</param>
    /// <param name="font">The font.</param>
    /// <param name="layoutArea">The area; zero for an unbounded side.</param>
    /// <returns>The size: the widest line and the padding by the lines' height and an eighth em, in world units.</returns>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="text"/>
    /// </example>
    public SizeF MeasureString(string? text, Font font, SizeF layoutArea) => MeasureStringCore(text.AsSpan(), font, layoutArea, null, out _, out _);

    /// <summary>Measures text laid out in an area with a format.</summary>
    /// <param name="text">The text.</param>
    /// <param name="font">The font.</param>
    /// <param name="layoutArea">The area; zero for an unbounded side.</param>
    /// <param name="stringFormat">The layout, or <see langword="null"/> for the default.</param>
    /// <returns>The size: the widest line and the padding by the lines' height and an eighth em, in world units.</returns>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="text"/>
    /// </example>
    public SizeF MeasureString(string? text, Font font, SizeF layoutArea, StringFormat? stringFormat) => MeasureStringCore(text.AsSpan(), font, layoutArea, stringFormat, out _, out _);

    /// <summary>Measures text laid out from a point with a format.</summary>
    /// <param name="text">The text.</param>
    /// <param name="font">The font.</param>
    /// <param name="origin">The layout origin; it does not change the size.</param>
    /// <param name="stringFormat">The layout, or <see langword="null"/> for the default.</param>
    /// <returns>The size: the widest line and the padding by the lines' height and an eighth em, in world units.</returns>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="text"/>
    /// </example>
    public SizeF MeasureString(string? text, Font font, PointF origin, StringFormat? stringFormat) => MeasureStringCore(text.AsSpan(), font, SizeF.Empty, stringFormat, out _, out _);

    /// <summary>Measures text laid out in an area, with how much of it fits.</summary>
    /// <param name="text">The text.</param>
    /// <param name="font">The font.</param>
    /// <param name="layoutArea">The area; zero for an unbounded side.</param>
    /// <param name="stringFormat">The layout, or <see langword="null"/> for the default.</param>
    /// <param name="charactersFitted">The number of characters on the lines that fit.</param>
    /// <param name="linesFilled">The number of lines that fit.</param>
    /// <returns>The size: the widest line and the padding by the lines' height and an eighth em, in world units.</returns>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="text"/>
    /// </example>
    public SizeF MeasureString(string? text, Font font, SizeF layoutArea, StringFormat? stringFormat, out int charactersFitted, out int linesFilled) => MeasureStringCore(text.AsSpan(), font, layoutArea, stringFormat, out charactersFitted, out linesFilled);

    /// <summary>Measures text on one line per line break.</summary>
    /// <param name="text">The text.</param>
    /// <param name="font">The font.</param>
    /// <returns>The size: the widest line and the padding by the lines' height and an eighth em, in world units.</returns>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="text"/>
    /// </example>
    public SizeF MeasureString(ReadOnlySpan<char> text, Font font) => MeasureStringCore(text, font, SizeF.Empty, null, out _, out _);

    /// <summary>Measures text wrapped at a width.</summary>
    /// <param name="text">The text.</param>
    /// <param name="font">The font.</param>
    /// <param name="width">The width to wrap at.</param>
    /// <returns>The size: the widest line and the padding by the lines' height and an eighth em, in world units.</returns>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="text"/>
    /// </example>
    public SizeF MeasureString(ReadOnlySpan<char> text, Font font, int width) => MeasureStringCore(text, font, new SizeF(width, 0), null, out _, out _);

    /// <summary>Measures text wrapped at a width with a format.</summary>
    /// <param name="text">The text.</param>
    /// <param name="font">The font.</param>
    /// <param name="width">The width to wrap at.</param>
    /// <param name="format">The layout, or <see langword="null"/> for the default.</param>
    /// <returns>The size: the widest line and the padding by the lines' height and an eighth em, in world units.</returns>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="text"/>
    /// </example>
    public SizeF MeasureString(ReadOnlySpan<char> text, Font font, int width, StringFormat? format) => MeasureStringCore(text, font, new SizeF(width, 0), format, out _, out _);

    /// <summary>Measures text laid out in an area.</summary>
    /// <param name="text">The text.</param>
    /// <param name="font">The font.</param>
    /// <param name="layoutArea">The area; zero for an unbounded side.</param>
    /// <returns>The size: the widest line and the padding by the lines' height and an eighth em, in world units.</returns>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="text"/>
    /// </example>
    public SizeF MeasureString(ReadOnlySpan<char> text, Font font, SizeF layoutArea) => MeasureStringCore(text, font, layoutArea, null, out _, out _);

    /// <summary>Measures text laid out in an area with a format.</summary>
    /// <param name="text">The text.</param>
    /// <param name="font">The font.</param>
    /// <param name="layoutArea">The area; zero for an unbounded side.</param>
    /// <param name="stringFormat">The layout, or <see langword="null"/> for the default.</param>
    /// <returns>The size: the widest line and the padding by the lines' height and an eighth em, in world units.</returns>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="text"/>
    /// </example>
    public SizeF MeasureString(ReadOnlySpan<char> text, Font font, SizeF layoutArea, StringFormat? stringFormat) => MeasureStringCore(text, font, layoutArea, stringFormat, out _, out _);

    /// <summary>Measures text laid out from a point with a format.</summary>
    /// <param name="text">The text.</param>
    /// <param name="font">The font.</param>
    /// <param name="origin">The layout origin; it does not change the size.</param>
    /// <param name="stringFormat">The layout, or <see langword="null"/> for the default.</param>
    /// <returns>The size: the widest line and the padding by the lines' height and an eighth em, in world units.</returns>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="text"/>
    /// </example>
    public SizeF MeasureString(ReadOnlySpan<char> text, Font font, PointF origin, StringFormat? stringFormat) => MeasureStringCore(text, font, SizeF.Empty, stringFormat, out _, out _);

    /// <summary>Measures text laid out in an area, with how much of it fits.</summary>
    /// <param name="text">The text.</param>
    /// <param name="font">The font.</param>
    /// <param name="layoutArea">The area; zero for an unbounded side.</param>
    /// <param name="stringFormat">The layout, or <see langword="null"/> for the default.</param>
    /// <param name="charactersFitted">The number of characters on the lines that fit.</param>
    /// <param name="linesFilled">The number of lines that fit.</param>
    /// <returns>The size: the widest line and the padding by the lines' height and an eighth em, in world units.</returns>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="text"/>
    /// </example>
    public SizeF MeasureString(ReadOnlySpan<char> text, Font font, SizeF layoutArea, StringFormat? stringFormat, out int charactersFitted, out int linesFilled) => MeasureStringCore(text, font, layoutArea, stringFormat, out charactersFitted, out linesFilled);

    /// <summary>Measures text laid out in a rectangle, with how much of it fits.</summary>
    /// <param name="text">The text.</param>
    /// <param name="font">The font.</param>
    /// <param name="layoutArea">The rectangle; only its size counts.</param>
    /// <param name="stringFormat">The layout, or <see langword="null"/> for the default.</param>
    /// <param name="charactersFitted">The number of characters on the lines that fit.</param>
    /// <param name="linesFilled">The number of lines that fit.</param>
    /// <returns>The size: the widest line and the padding by the lines' height and an eighth em, in world units.</returns>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="text"/>
    /// </example>
    public SizeF MeasureStringInternal(ReadOnlySpan<char> text, Font font, RectangleF layoutArea, StringFormat? stringFormat, out int charactersFitted, out int linesFilled) => MeasureStringCore(text, font, layoutArea.Size, stringFormat, out charactersFitted, out linesFilled);

    /// <summary>Measures where the format's character ranges fall: one region per range, in whole device pixels.</summary>
    /// <param name="text">The text.</param>
    /// <param name="font">The font.</param>
    /// <param name="layoutRect">The rectangle the text is laid out in.</param>
    /// <param name="stringFormat">The layout, with the ranges from <see cref="StringFormat.SetMeasurableCharacterRanges"/>.</param>
    /// <returns>The regions, in world coordinates.</returns>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="text"/>
    /// </example>
    public Region[] MeasureCharacterRanges(string? text, Font font, RectangleF layoutRect, StringFormat? stringFormat) => MeasureCharacterRangesCore(text.AsSpan(), font, layoutRect, stringFormat);

    /// <summary>Measures where the format's character ranges fall: one region per range, in whole device pixels.</summary>
    /// <param name="text">The text.</param>
    /// <param name="font">The font.</param>
    /// <param name="layoutRect">The rectangle the text is laid out in.</param>
    /// <param name="stringFormat">The layout, with the ranges from <see cref="StringFormat.SetMeasurableCharacterRanges"/>.</param>
    /// <returns>The regions, in world coordinates.</returns>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="text"/>
    /// </example>
    public Region[] MeasureCharacterRanges(ReadOnlySpan<char> text, Font font, RectangleF layoutRect, StringFormat? stringFormat) => MeasureCharacterRangesCore(text, font, layoutRect, stringFormat);
}
