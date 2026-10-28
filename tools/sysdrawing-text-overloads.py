"""Writes src/Egami.SystemDrawing/Graphics.TextOverloads.cs: every DrawString, MeasureString and MeasureCharacterRanges overload.

Usage: python tools/sysdrawing-text-overloads.py src/Egami.SystemDrawing/Graphics.TextOverloads.cs
"""
import sys

out = []
def emit(doc, params_doc, sig, body, ret=None, extra=""):
    out.append(f"    /// <summary>{doc}</summary>")
    for n, d in params_doc:
        out.append(f"    /// <param name=\"{n}\">{d}</param>")
    if ret:
        out.append(f"    /// <returns>{ret}</returns>")
    if extra:
        out.append(extra)
    out.append("    /// <example>")
    out.append("    /// <code language=\"csharp\" source=\"../../tests/Egami.DocSamples/SystemDrawingPackage.cs\" region=\"text\"/>")
    out.append("    /// </example>")
    out.append(f"    public {sig} => {body};")
    out.append("")

S = [("string?", "s", ".AsSpan()"), ("ReadOnlySpan<char>", "s", "")]
P = {"s": ("s", "The text."), "font": ("font", "The font."), "brush": ("brush", "The brush."),
     "x": ("x", "The left of the layout origin."), "y": ("y", "The top of the layout origin."),
     "point": ("point", "The layout origin: the text starts 1/6 em right of it with the default format."),
     "layoutRectangle": ("layoutRectangle", "The rectangle the text is laid out, wrapped and clipped in."),
     "format": ("format", "The layout, or <see langword=\"null\"/> for the default.")}
def pd(*names): return [P[n] for n in names]

# DrawString
for t, n, conv in S:
    sp = f"s{conv}" if conv else "s"
    emit("Draws text from a point.", pd("s","font","brush","x","y"), f"void DrawString({t} s, Font font, Brush brush, float x, float y)", f"DrawStringCore({sp}, font, brush, new RectangleF(x, y, 0, 0), null)")
    emit("Draws text from a point.", pd("s","font","brush","point"), f"void DrawString({t} s, Font font, Brush brush, PointF point)", f"DrawStringCore({sp}, font, brush, new RectangleF(point.X, point.Y, 0, 0), null)")
    emit("Draws text from a point with a format.", pd("s","font","brush","x","y","format"), f"void DrawString({t} s, Font font, Brush brush, float x, float y, StringFormat? format)", f"DrawStringCore({sp}, font, brush, new RectangleF(x, y, 0, 0), format)")
    emit("Draws text from a point with a format.", pd("s","font","brush","point","format"), f"void DrawString({t} s, Font font, Brush brush, PointF point, StringFormat? format)", f"DrawStringCore({sp}, font, brush, new RectangleF(point.X, point.Y, 0, 0), format)")
    emit("Draws text in a rectangle, wrapped at its width.", pd("s","font","brush","layoutRectangle"), f"void DrawString({t} s, Font font, Brush brush, RectangleF layoutRectangle)", f"DrawStringCore({sp}, font, brush, layoutRectangle, null)")
    emit("Draws text in a rectangle with a format.", pd("s","font","brush","layoutRectangle","format"), f"void DrawString({t} s, Font font, Brush brush, RectangleF layoutRectangle, StringFormat? format)", f"DrawStringCore({sp}, font, brush, layoutRectangle, format)")

RET = "The size: the widest line and the padding by the lines' height and an eighth em, in world units."
for t, n, conv in S:
    sp = f"text{conv}" if conv else "text"
    def m(doc, params, sig, body):
        emit(doc, params, sig, body, RET)
    m("Measures text on one line per line break.", [("text","The text."),("font","The font.")], f"SizeF MeasureString({t} text, Font font)", f"MeasureStringCore({sp}, font, SizeF.Empty, null, out _, out _)")
    m("Measures text wrapped at a width.", [("text","The text."),("font","The font."),("width","The width to wrap at.")], f"SizeF MeasureString({t} text, Font font, int width)", f"MeasureStringCore({sp}, font, new SizeF(width, 0), null, out _, out _)")
    m("Measures text wrapped at a width with a format.", [("text","The text."),("font","The font."),("width","The width to wrap at."),("format","The layout, or <see langword=\"null\"/> for the default.")], f"SizeF MeasureString({t} text, Font font, int width, StringFormat? format)", f"MeasureStringCore({sp}, font, new SizeF(width, 0), format, out _, out _)")
    m("Measures text laid out in an area.", [("text","The text."),("font","The font."),("layoutArea","The area; zero for an unbounded side.")], f"SizeF MeasureString({t} text, Font font, SizeF layoutArea)", f"MeasureStringCore({sp}, font, layoutArea, null, out _, out _)")
    m("Measures text laid out in an area with a format.", [("text","The text."),("font","The font."),("layoutArea","The area; zero for an unbounded side."),("stringFormat","The layout, or <see langword=\"null\"/> for the default.")], f"SizeF MeasureString({t} text, Font font, SizeF layoutArea, StringFormat? stringFormat)", f"MeasureStringCore({sp}, font, layoutArea, stringFormat, out _, out _)")
    m("Measures text laid out from a point with a format.", [("text","The text."),("font","The font."),("origin","The layout origin; it does not change the size."),("stringFormat","The layout, or <see langword=\"null\"/> for the default.")], f"SizeF MeasureString({t} text, Font font, PointF origin, StringFormat? stringFormat)", f"MeasureStringCore({sp}, font, SizeF.Empty, stringFormat, out _, out _)")
    m("Measures text laid out in an area, with how much of it fits.", [("text","The text."),("font","The font."),("layoutArea","The area; zero for an unbounded side."),("stringFormat","The layout, or <see langword=\"null\"/> for the default."),("charactersFitted","The number of characters on the lines that fit."),("linesFilled","The number of lines that fit.")], f"SizeF MeasureString({t} text, Font font, SizeF layoutArea, StringFormat? stringFormat, out int charactersFitted, out int linesFilled)", f"MeasureStringCore({sp}, font, layoutArea, stringFormat, out charactersFitted, out linesFilled)")

emit("Measures text laid out in a rectangle, with how much of it fits.", [("text","The text."),("font","The font."),("layoutArea","The rectangle; only its size counts."),("stringFormat","The layout, or <see langword=\"null\"/> for the default."),("charactersFitted","The number of characters on the lines that fit."),("linesFilled","The number of lines that fit.")],
     "SizeF MeasureStringInternal(ReadOnlySpan<char> text, Font font, RectangleF layoutArea, StringFormat? stringFormat, out int charactersFitted, out int linesFilled)", "MeasureStringCore(text, font, layoutArea.Size, stringFormat, out charactersFitted, out linesFilled)", RET)

for t, n, conv in S:
    sp = f"text{conv}" if conv else "text"
    emit("Measures where the format's character ranges fall: one region per range, in whole device pixels.", [("text","The text."),("font","The font."),("layoutRect","The rectangle the text is laid out in."),("stringFormat","The layout, with the ranges from <see cref=\"StringFormat.SetMeasurableCharacterRanges\"/>.")],
         f"Region[] MeasureCharacterRanges({t} text, Font font, RectangleF layoutRect, StringFormat? stringFormat)", f"MeasureCharacterRangesCore({sp}, font, layoutRect, stringFormat)", "The regions, in world coordinates.")

head = """// Written by tools/sysdrawing-text-overloads.py; each overload forwards to a core in Graphics.Text.cs.
namespace Egami.SystemDrawing;

/// <summary>The text overloads, each a call to the layout core in Graphics.Text.cs.</summary>
public sealed partial class Graphics
{
"""
open(sys.argv[1], "w", encoding="utf-8", newline="\n").write(head + "\n".join(out).rstrip() + "\n}\n")
