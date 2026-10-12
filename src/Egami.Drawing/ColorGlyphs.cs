using System.Numerics;

using Egami.Fonts;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing;

/// <summary>
/// Draws color glyphs with ordinary canvas calls, so every backend gets them and vector ones stay vector: a COLR paint graph
/// becomes clips by glyph outlines, solid and gradient fills (2.6.3's linear, two-circle and sweep gradients mixing in encoded
/// sRGB), transforms and layers with blend modes; an sbix or CBDT image is decoded with Egami's own decoders and drawn scaled.
/// The whole glyph is one group whose opacity is the paint's alpha; the paint's color fills foreground entries.
/// </summary>
internal static class ColorGlyphDrawing
{
    // Bitmap glyphs shrink with a box filter, which averages the pixels each output pixel covers, and grow bilinearly.
    private static readonly SamplingOptions s_shrinkSampling = new() { Filter = ResampleFilter.Box };
    private static readonly SamplingOptions s_growSampling = new() { Filter = ResampleFilter.Bilinear };

    /// <summary>Gets whether a font draws a glyph in color.</summary>
    public static bool IsColor(Font font, Typeface face, ushort glyph)
        => font.ColorGlyphs && (face.ColorFormats & ~ColorGlyphFormats.Svg) != 0 && face.HasColorGlyph(glyph);

    /// <summary>Draws a color glyph with its origin at a local point, at the font's size.</summary>
    public static void Draw(Canvas canvas, Font font, Typeface face, ColorGlyph glyph, Vector2 origin, Paint paint, Matrix3x2? glyphTransform = null)
    {
        (RgbaF32 foreground, Paint? foregroundPaint, float alpha) = Foreground(paint);
        if (alpha <= 0)
        {
            return;
        }

        int count = canvas.Save();
        if (alpha < 1 || paint.BlendMode != BlendMode.SourceOver)
        {
            canvas.SaveLayer(new LayerOptions { Opacity = alpha, BlendMode = paint.BlendMode });
        }

        canvas.Translate(origin.X, origin.Y);
        if (glyphTransform is Matrix3x2 transform)
        {
            canvas.Concat(transform);
        }

        if ((font.AppliedSynthesis & FontSynthesis.Italic) != 0)
        {
            // y down: x moves right by the skew times the height above the baseline.
            canvas.Concat(new Matrix3x2(1, 0, -Synthesis.Skew, 1, 0, 0));
        }

        float scale = font.Scale;
        switch (glyph)
        {
            case PaintColorGlyph paintGlyph:
                canvas.Scale(scale, -scale);
                if (paintGlyph.ClipBox is GlyphExtents box && !box.IsEmpty)
                {
                    canvas.ClipRect(RectF.FromEdges(box.XMin, box.YMin, box.XMax, box.YMax));
                }

                paintGlyph.Paint.Accept(new Visitor(canvas, face, foreground, foregroundPaint));
                break;
            case BitmapColorGlyph bitmap:
                if (Decode(bitmap) is Raster<Rgba8> image)
                {
                    using (image)
                    {
                        GlyphExtents b = bitmap.Bounds;
                        var target = RectF.FromEdges(b.XMin * scale, -b.YMax * scale, b.XMax * scale, -b.YMin * scale);
                        canvas.DrawImage(image, target, target.Width < image.Width ? s_shrinkSampling : s_growSampling);
                    }
                }

                break;
        }

        canvas.RestoreToCount(count);
    }

    /// <summary>The box a color glyph can paint, in font units with y up, or <see langword="null"/> when it is unbounded.</summary>
    public static GlyphExtents? Bounds(Typeface face, ColorGlyph glyph)
    {
        switch (glyph)
        {
            case BitmapColorGlyph bitmap:
                return bitmap.Bounds;
            case PaintColorGlyph paint:
                if (paint.ClipBox is GlyphExtents box)
                {
                    return box;
                }

                var bounds = new BoundsVisitor(face);
                paint.Paint.Accept(bounds);
                return bounds.Unbounded ? null : bounds.Box;
            default:
                return null;
        }
    }

    /// <summary>Decodes a bitmap glyph's image, or returns <see langword="null"/> when it cannot be read.</summary>
    public static Raster<Rgba8>? Decode(BitmapColorGlyph bitmap)
    {
        try
        {
            if (bitmap.Encoding == BitmapGlyphEncoding.PremultipliedBgra)
            {
                // Premultiplied BGRA rows; straighten them into RGBA.
                var pixels = new Rgba8[bitmap.PixelWidth * bitmap.PixelHeight];
                ReadOnlySpan<byte> data = bitmap.Data.Span;
                for (int i = 0; i < pixels.Length; i++)
                {
                    byte b = data[i * 4], g = data[(i * 4) + 1], r = data[(i * 4) + 2], a = data[(i * 4) + 3];
                    pixels[i] = a == 0 ? default : new Rgba8(Straight(r, a), Straight(g, a), Straight(b, a), a);
                }

                return Raster.Wrap<Rgba8>(pixels, bitmap.PixelWidth, bitmap.PixelHeight);
            }

            return Raster.Load<Rgba8>(bitmap.Data);
        }
        catch (EgamiException)
        {
            return null;
        }

        static byte Straight(byte c, byte a) => (byte)Math.Min(255, ((c * 255) + (a / 2)) / a);
    }

    // The foreground: a solid paint's color, opaque, with its alpha left for the group; another paint fills foreground entries itself.
    private static (RgbaF32 Color, Paint? Paint, float Alpha) Foreground(Paint paint)
    {
        if (paint is SolidPaint solid)
        {
            RgbaF32 c = solid.Color;
            return (c with { A = 1 }, null, Math.Clamp(c.A * solid.Opacity, 0, 1));
        }

        return (new RgbaF32(0, 0, 0, 1), paint with { Opacity = 1, BlendMode = BlendMode.SourceOver }, Math.Clamp(paint.Opacity, 0, 1));
    }

    private static Path GlyphPath(Typeface face, ushort glyph)
    {
        var builder = new PathBuilder();
        face.GetGlyphOutline(glyph, new UnitSink(builder));
        return builder.ToPath();
    }

    private static TileMode Tile(ColorExtend extend) => extend switch
    {
        ColorExtend.Repeat => TileMode.Repeat,
        ColorExtend.Reflect => TileMode.Mirror,
        _ => TileMode.Clamp,
    };

    /// <summary>A color line's stops brought into 0 to 1, with the fraction of the original line they span.</summary>
    private static (GradientStop[] Stops, float Start, float End) Normalize(ColorLine line, RgbaF32 foreground)
    {
        if (line.Stops.Count == 0)
        {
            return ([new GradientStop(0, default(RgbaF32))], 0, 1);
        }

        // The extend mode applies outside the stops' own range, so a repeat's period is first stop to last, wherever they lie.
        float min = line.Stops[0].Offset, max = line.Stops[^1].Offset;
        if (max - min < 1e-6f)
        {
            max = min + 1e-6f;
        }

        var stops = new GradientStop[line.Stops.Count];
        for (int i = 0; i < stops.Length; i++)
        {
            ColorStop stop = line.Stops[i];
            float t = Math.Clamp((stop.Offset - min) / (max - min), 0, 1);
            stops[i] = new GradientStop(t, Clamp(stop.Color.Resolve(foreground)));
        }

        return (stops, min, max);
    }

    private static RgbaF32 Clamp(RgbaF32 c) => new(Math.Clamp(c.R, 0, 1), Math.Clamp(c.G, 0, 1), Math.Clamp(c.B, 0, 1), Math.Clamp(c.A, 0, 1));

    /// <summary>Walks a paint graph, drawing each node with canvas calls in font units, y up.</summary>
    private sealed class Visitor(Canvas canvas, Typeface face, RgbaF32 foreground, Paint? foregroundPaint) : IColorPaintVisitor
    {
        public void VisitLayers(ColorLayersPaint paint)
        {
            foreach (ColorPaint layer in paint.Layers)
            {
                layer.Accept(this);
            }
        }

        public void VisitSolid(ColorSolidPaint paint)
        {
            if (paint.Color.IsForeground && foregroundPaint is not null)
            {
                canvas.DrawPaint(foregroundPaint with { Opacity = Math.Clamp(paint.Color.Color.A, 0, 1) });
                return;
            }

            canvas.DrawPaint(new SolidPaint(Clamp(paint.Color.Resolve(foreground))));
        }

        public void VisitLinearGradient(ColorLinearGradientPaint paint)
        {
            // The color line runs from P0 to P1 projected onto the normal of P0–P2, so lines of equal color run parallel to P0–P2.
            Vector2 p0 = paint.P0, p1 = paint.P1;
            Vector2 d = paint.P2 - p0;
            if (d.LengthSquared() > 0)
            {
                Vector2 n = Vector2.Normalize(new Vector2(-d.Y, d.X));
                p1 = p0 + (n * Vector2.Dot(paint.P1 - p0, n));
            }

            (GradientStop[] stops, float start, float end) = Normalize(paint.Line, foreground);
            Vector2 a = Vector2.Lerp(p0, p1, start), b = Vector2.Lerp(p0, p1, end);
            if (a == b)
            {
                return;
            }

            canvas.DrawPaint(new LinearGradient(a, b, stops) { Tile = Tile(paint.Line.Extend) });
        }

        public void VisitRadialGradient(ColorRadialGradientPaint paint)
        {
            (GradientStop[] stops, float start, float end) = Normalize(paint.Line, foreground);
            Vector2 c0 = Vector2.Lerp(paint.Center0, paint.Center1, start), c1 = Vector2.Lerp(paint.Center0, paint.Center1, end);
            float r0 = paint.Radius0 + ((paint.Radius1 - paint.Radius0) * start), r1 = paint.Radius0 + ((paint.Radius1 - paint.Radius0) * end);
            canvas.DrawPaint(new ConicalGradient(c0, Math.Max(0, r0), c1, Math.Max(0, r1), stops) { Tile = Tile(paint.Line.Extend) });
        }

        public void VisitSweepGradient(ColorSweepGradientPaint paint)
        {
            (GradientStop[] stops, float start, float end) = Normalize(paint.Line, foreground);
            float a0 = paint.StartAngle + ((paint.EndAngle - paint.StartAngle) * start), a1 = paint.StartAngle + ((paint.EndAngle - paint.StartAngle) * end);
            if (a1 == a0)
            {
                return;
            }

            if (a1 < a0)
            {
                // Going clockwise: the same sweep counter-clockwise with the stops reversed.
                (a0, a1) = (a1, a0);
                stops = [.. stops.Reverse().Select(s => new GradientStop(1 - s.Offset, s.Color))];
            }

            canvas.DrawPaint(new SweepGradient(paint.Center, a0, a1, stops) { Tile = Tile(paint.Line.Extend) });
        }

        public void VisitGlyph(ColorGlyphPaint paint)
        {
            Path outline = GlyphPath(face, paint.Glyph);
            if (outline.IsEmpty)
            {
                return;
            }

            int count = canvas.Save();
            canvas.ClipPath(outline);
            paint.Paint.Accept(this);
            canvas.RestoreToCount(count);
        }

        public void VisitColorGlyph(ColorGlyphReferencePaint paint) => paint.Paint.Accept(this);

        public void VisitTransform(ColorTransformPaint paint)
        {
            int count = canvas.Save();
            canvas.Concat(paint.Matrix);
            paint.Paint.Accept(this);
            canvas.RestoreToCount(count);
        }

        public void VisitComposite(ColorCompositePaint paint)
        {
            int count = canvas.SaveLayer();
            paint.Backdrop.Accept(this);
            canvas.SaveLayer(new LayerOptions { BlendMode = paint.Mode });
            paint.Source.Accept(this);
            canvas.RestoreToCount(count);
        }
    }

    /// <summary>The box a paint graph can paint: the union of its glyph clips, transformed; unbounded when a fill has no clip.</summary>
    private sealed class BoundsVisitor(Typeface face) : IColorPaintVisitor
    {
        private Matrix3x2 _matrix = Matrix3x2.Identity;
        private bool _clipped;

        public GlyphExtents Box { get; private set; } = GlyphExtents.Empty;

        public bool Unbounded { get; private set; }

        public void VisitLayers(ColorLayersPaint paint)
        {
            foreach (ColorPaint layer in paint.Layers)
            {
                layer.Accept(this);
            }
        }

        public void VisitSolid(ColorSolidPaint paint) => Fill();

        public void VisitLinearGradient(ColorLinearGradientPaint paint) => Fill();

        public void VisitRadialGradient(ColorRadialGradientPaint paint) => Fill();

        public void VisitSweepGradient(ColorSweepGradientPaint paint) => Fill();

        public void VisitGlyph(ColorGlyphPaint paint)
        {
            GlyphExtents e = face.GetGlyphExtents(paint.Glyph);
            if (e.IsEmpty)
            {
                return;
            }

            Vector2 a = Vector2.Transform(new Vector2(e.XMin, e.YMin), _matrix), b = Vector2.Transform(new Vector2(e.XMax, e.YMin), _matrix);
            Vector2 c = Vector2.Transform(new Vector2(e.XMin, e.YMax), _matrix), d = Vector2.Transform(new Vector2(e.XMax, e.YMax), _matrix);
            Vector2 min = Vector2.Min(Vector2.Min(a, b), Vector2.Min(c, d)), max = Vector2.Max(Vector2.Max(a, b), Vector2.Max(c, d));
            var box = new GlyphExtents(min.X, min.Y, max.X, max.Y);
            Box = Box.IsEmpty ? box : new GlyphExtents(MathF.Min(Box.XMin, box.XMin), MathF.Min(Box.YMin, box.YMin), MathF.Max(Box.XMax, box.XMax), MathF.Max(Box.YMax, box.YMax));

            // The fill inside the clip is bounded by it, whatever it is.
            bool clipped = _clipped;
            _clipped = true;
            paint.Paint.Accept(this);
            _clipped = clipped;
        }

        public void VisitColorGlyph(ColorGlyphReferencePaint paint) => paint.Paint.Accept(this);

        public void VisitTransform(ColorTransformPaint paint)
        {
            Matrix3x2 saved = _matrix;
            _matrix = paint.Matrix * _matrix;
            paint.Paint.Accept(this);
            _matrix = saved;
        }

        public void VisitComposite(ColorCompositePaint paint)
        {
            paint.Backdrop.Accept(this);
            paint.Source.Accept(this);
        }

        private void Fill()
        {
            if (!_clipped)
            {
                Unbounded = true;
            }
        }
    }

    /// <summary>Font units, y up, as they are: the canvas's transform does the scaling.</summary>
    private sealed class UnitSink(PathBuilder builder) : IGlyphOutlineSink
    {
        public void MoveTo(float x, float y) => builder.MoveTo(x, y);

        public void LineTo(float x, float y) => builder.LineTo(x, y);

        public void QuadTo(float cx, float cy, float x, float y) => builder.QuadTo(cx, cy, x, y);

        public void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y) => builder.CubicTo(c1x, c1y, c2x, c2y, x, y);

        public void Close() => builder.Close();
    }
}
