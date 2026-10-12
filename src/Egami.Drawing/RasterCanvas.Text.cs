using System.Numerics;

using Egami.Drawing.Rasterization;
using Egami.Fonts;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing;

public sealed partial class RasterCanvas<TPixel>
{
    /// <summary>The largest device em size drawn through the glyph cache; bigger glyphs fill as paths.</summary>
    internal const float MaxCachedEm = 256;

    /// <summary>The largest device em size light hinting applies to.</summary>
    internal const float MaxHintedEm = 48;

    /// <summary>The flattening tolerance of glyph masks, finer than the canvas default: glyphs are mostly curves, and a mask is made once.</summary>
    internal const float GlyphTolerance = 0.01f;

    private bool _lcdFallbackReported;

    /// <inheritdoc/>
    /// <remarks>
    /// Under a scale-and-translate transform, glyphs up to 256 device pixels per em are drawn from <see cref="GlyphCache"/> masks:
    /// with <see cref="Font.Subpixel"/> their x origins round to quarter pixels (each phase its own mask) and y to whole pixels,
    /// else both to whole pixels; <see cref="Font.Hinting"/>, <see cref="Font.Edging"/> and synthetic bold and italic are applied
    /// when the mask is made, its curves flattened to 0.01 pixel (or the canvas tolerance when finer). Any other glyph run fills as
    /// one path at the canvas tolerance, with synthesis, aliased for <see cref="FontEdging.Alias"/>.
    /// </remarks>
    protected override void OnDrawGlyphRun(GlyphRun run, Vector2 origin, Paint paint)
    {
        if (_clip.Bounds.IsEmpty)
        {
            return;
        }

        // Blurred text fills as paths, which the blur needs whole.
        Matrix3x2 m = Matrix;
        Font font = run.Font;
        float emX = font.Size * m.M11, emY = font.Size * m.M22;
        if (m.M12 != 0 || m.M21 != 0 || emX == 0 || emY == 0 || MathF.Abs(emX) > MaxCachedEm || MathF.Abs(emY) > MaxCachedEm || paint.Blur is { Sigma: > 0 })
        {
            base.OnDrawGlyphRun(run, origin, paint);
            return;
        }

        FontEdging edging = paint.Antialias ? font.Edging : FontEdging.Alias;
        LcdPaint? lcd = null;
        if (edging == FontEdging.Lcd)
        {
            lcd = LcdPaintFor(paint);
            if (lcd is null)
            {
                edging = FontEdging.Antialias;
                ReportLcdFallback();
            }
        }

        ISpanSink? sink = null;
        if (lcd is null && (sink = _target.CreateSink(paint, m, Options.ColorSpace, _clip.Mask, 255)) is null)
        {
            return;
        }

        // Turned or scaled glyphs of a transformed run take the canvas route; the rest the mask route below.
        ReadOnlySpan<GlyphTransform> transforms = run.Transforms;
        if (!transforms.IsEmpty)
        {
            DrawTurnedGlyphs(run, origin, paint);
        }

        Typeface face = font.EffectiveTypeface;
        bool color = font.ColorGlyphs && (face.ColorFormats & ~ColorGlyphFormats.Svg) != 0;
        ColorKey? colorKey = color && paint is SolidPaint solid ? new ColorKey(font.Palette, solid.Color with { A = 1 }) : null;
        List<int>? uncachedColor = null;
        var style = new GlyphStyle(
            edging,
            font.Hinting == FontHinting.Light && MathF.Abs(emY) <= MaxHintedEm,
            font.AppliedSynthesis,
            edging == FontEdging.Lcd ? Options.PixelGeometry : PixelGeometry.Unknown);
        float sx = emX / face.UnitsPerEm, sy = emY / face.UnitsPerEm;
        Rect clip = _clip.Bounds;
        byte[]? scratch = null;
        ReadOnlySpan<ushort> glyphs = run.Glyphs;
        ReadOnlySpan<Vector2> positions = run.Positions;
        for (int i = 0; i < glyphs.Length; i++)
        {
            if (!transforms.IsEmpty && !transforms[i].IsTranslation)
            {
                continue;
            }

            Vector2 device = Vector2.Transform(origin + positions[i], m);
            if (!(MathF.Abs(device.X) < 1e9f && MathF.Abs(device.Y) < 1e9f))
            {
                continue;
            }

            (int ox, int phase) = Snap(device.X, font.Subpixel);
            int oy = (int)MathF.Floor(device.Y + 0.5f);
            ushort glyph = glyphs[i];
            if (color && face.HasColorGlyph(glyph))
            {
                // Color glyphs are cached as premultiplied RGBA for a solid paint; any other paint draws them through canvas calls.
                if (colorKey is null)
                {
                    (uncachedColor ??= []).Add(i);
                    continue;
                }

                var colorStyle = new GlyphStyle(FontEdging.Antialias, false, font.AppliedSynthesis & FontSynthesis.Italic, PixelGeometry.Unknown) { Color = colorKey };
                var colorKeyed = new GlyphKey(face, glyph, emX, emY, colorStyle, phase);
                if (!GlyphCache.TryGet(colorKeyed, out GlyphMask? image))
                {
                    image = RenderColorMask(font, face, glyph, emX, emY, phase, colorKey.Foreground);
                    GlyphCache.Add(colorKeyed, image);
                }

                if (image.Pixels is not null)
                {
                    DrawColorMask(image, ox, oy, paint);
                }
                else if (image.Coverage.Length == 0 && image.Bounds.IsEmpty && image.Channels == 0)
                {
                    // No color data after all, or it could not be read: the outline.
                    (uncachedColor ??= []).Add(i);
                }

                continue;
            }

            var key = new GlyphKey(face, glyph, emX, emY, style, phase);
            if (!GlyphCache.TryGet(key, out GlyphMask? mask))
            {
                mask = RenderMask(face, glyph, sx, sy, phase, style, emY);
                GlyphCache.Add(key, mask);
            }

            if (mask.Coverage.Length == 0)
            {
                continue;
            }

            Rect placed = new(mask.Bounds.X + ox, mask.Bounds.Y + oy, mask.Bounds.Width, mask.Bounds.Height);
            Rect visible = placed.Intersect(clip);
            if (visible.IsEmpty)
            {
                continue;
            }

            if (lcd is not null)
            {
                BlendLcd(mask, placed, visible, lcd);
            }
            else
            {
                BlendMask(mask, placed, visible, sink!, ref scratch);
            }
        }

        if (uncachedColor is not null)
        {
            base.OnDrawGlyphRun(Subrun(run, uncachedColor), origin, paint);
        }
    }

    // A coverage mask blended through the sink row by row; rows with no coverage are skipped.
    private static void BlendMask(GlyphMask mask, Rect placed, Rect visible, ISpanSink sink, ref byte[]? scratch)
    {
        if (scratch is null || scratch.Length < visible.Width)
        {
            scratch = new byte[Math.Max(visible.Width, 64)];
        }

        for (int y = visible.Y; y < visible.Bottom; y++)
        {
            Span<byte> row = scratch.AsSpan(0, visible.Width);
            mask.Coverage.AsSpan(((y - placed.Y) * placed.Width) + (visible.X - placed.X), visible.Width).CopyTo(row);
            if (row.ContainsAnyExcept((byte)0))
            {
                sink.Blend(y, visible.X, row);
            }
        }
    }

    private static readonly SamplingOptions NearestSampling = new() { Filter = ResampleFilter.Nearest };

    // A color glyph rendered once at its device size and phase into premultiplied RGBA, with an opaque foreground; the paint's
    // alpha and blend mode apply when it is drawn. A glyph that turns out to have no color data gives an empty mask of 0 channels.
    private GlyphMask RenderColorMask(Font font, Typeface face, ushort glyph, float emX, float emY, int phase, RgbaF32 foreground)
    {
        float ppem = MathF.Max(MathF.Abs(emX), MathF.Abs(emY));
        if (!face.TryGetColorGlyph(glyph, font.Palette, out ColorGlyph? color, ppem))
        {
            return new GlyphMask(default, [], 0);
        }

        GlyphExtents units = ColorGlyphDrawing.Bounds(face, color) ?? new GlyphExtents(face.Head.XMin, face.Head.YMin, face.Head.XMax, face.Head.YMax);
        if ((font.AppliedSynthesis & FontSynthesis.Italic) != 0)
        {
            float k = Synthesis.Skew;
            units = new GlyphExtents(units.XMin + (k * MathF.Min(0, units.YMin)), units.YMin, units.XMax + (k * MathF.Max(0, units.YMax)), units.YMax);
        }

        float sx = emX / face.UnitsPerEm, sy = emY / face.UnitsPerEm, dx = phase / 4f;
        float x0 = (units.XMin * sx) + dx, x1 = (units.XMax * sx) + dx, y0 = -units.YMax * sy, y1 = -units.YMin * sy;
        Rect bounds = RectF.FromEdges(MathF.Min(x0, x1), MathF.Min(y0, y1), MathF.Max(x0, x1), MathF.Max(y0, y1)).RoundOut();
        bounds = new Rect(bounds.X - 1, bounds.Y - 1, bounds.Width + 2, bounds.Height + 2);
        if (bounds.Width <= 0 || bounds.Height <= 0 || (long)bounds.Width * bounds.Height > 4 * MaxCachedEm * MaxCachedEm)
        {
            return GlyphMask.Empty;
        }

        var pixels = new PremulRgba8[bounds.Width * bounds.Height];
        using (Raster<PremulRgba8> image = Raster.Wrap<PremulRgba8>(pixels, bounds.Width, bounds.Height))
        using (RasterCanvas<PremulRgba8> canvas = image.CreateCanvas(Options))
        {
            canvas.Translate(dx - bounds.X, -bounds.Y);
            canvas.Scale(emX / font.Size, emY / font.Size);
            ColorGlyphDrawing.Draw(canvas, font, face, color, Vector2.Zero, new SolidPaint(foreground));
        }

        return new GlyphMask(bounds, [], 4) { Pixels = pixels };
    }

    // Draws a cached color glyph at whole device pixels, through the canvas's image route with the paint's alpha and blend mode.
    private void DrawColorMask(GlyphMask mask, int ox, int oy, Paint paint)
    {
        float alpha = paint is SolidPaint solid ? solid.Color.A * solid.Opacity : paint.Opacity;
        using Raster<PremulRgba8> image = Raster.Wrap<PremulRgba8>(mask.Pixels!, mask.Bounds.Width, mask.Bounds.Height);
        int count = Save();
        ResetMatrix();
        DrawImage(image, new RectF(mask.Bounds.X + ox, mask.Bounds.Y + oy, mask.Bounds.Width, mask.Bounds.Height), NearestSampling, new SolidPaint(new RgbaF32(0, 0, 0, 1)) { Opacity = Math.Clamp(alpha, 0, 1), BlendMode = paint.BlendMode });
        RestoreToCount(count);
    }

    // The run's turned or scaled glyphs through the canvas route: color glyphs as canvas calls, the rest as one path.
    private void DrawTurnedGlyphs(GlyphRun run, Vector2 origin, Paint paint)
    {
        var turned = new List<int>();
        ReadOnlySpan<GlyphTransform> transforms = run.Transforms;
        for (int i = 0; i < transforms.Length; i++)
        {
            if (!transforms[i].IsTranslation)
            {
                turned.Add(i);
            }
        }

        if (turned.Count > 0)
        {
            base.OnDrawGlyphRun(Subrun(run, turned), origin, paint);
        }
    }

    /// <summary>
    /// Rounds a device x to the glyph origin: with subpixel positioning to the nearest quarter pixel (a whole pixel and a phase of
    /// 0 to 3 quarters), else to the nearest whole pixel.
    /// </summary>
    internal static (int Origin, int Phase) Snap(float x, bool subpixel)
    {
        if (!subpixel)
        {
            return ((int)MathF.Floor(x + 0.5f), 0);
        }

        float quarters = MathF.Floor((x * 4) + 0.5f);
        int origin = (int)MathF.Floor(quarters / 4);
        return (origin, (int)(quarters - (origin * 4)));
    }

    // A glyph's coverage with its origin at a pixel corner plus its phase, through the same flattener and rasterizer as paths:
    // slanted for synthetic italic, its y snapped to the hint zones, outset for synthetic bold, and for LCD edging at three times
    // the resolution along the pixel geometry, filtered into one coverage per color channel.
    private GlyphMask RenderMask(Typeface face, ushort glyph, float sx, float sy, int phase, GlyphStyle style, float emY)
    {
        Path path = GlyphOutline(face, glyph, sx, sy, phase / 4f, style, emY);
        if (path.IsEmpty)
        {
            return GlyphMask.Empty;
        }

        float tolerance = MathF.Min(Options.Tolerance, GlyphTolerance);
        var edges = new EdgeList();
        if (style.Edging == FontEdging.Lcd)
        {
            bool vertical = style.Geometry is PixelGeometry.RgbVertical or PixelGeometry.BgrVertical;
            Flattener.Flatten(path, vertical ? Matrix3x2.CreateScale(1, 3) : Matrix3x2.CreateScale(3, 1), tolerance, edges);
            return Lcd.Render(edges, vertical, style.Geometry is PixelGeometry.BgrHorizontal or PixelGeometry.BgrVertical, _rasterizer);
        }

        Flattener.Flatten(path, Matrix3x2.Identity, tolerance, edges);
        Rect bounds = edges.Bounds.RoundOut();
        if (bounds.IsEmpty)
        {
            return GlyphMask.Empty;
        }

        byte[] coverage = new byte[bounds.Width * bounds.Height];
        _rasterizer.Fill(edges, bounds, FillRule.NonZero, style.Edging != FontEdging.Alias, new BufferSink(coverage, bounds), 1);
        return new GlyphMask(bounds, coverage, 1);
    }

    /// <summary>A glyph's device outline relative to its pixel origin: scaled, slanted, hinted and outset as the style asks.</summary>
    internal static Path GlyphOutline(Typeface face, ushort glyph, float sx, float sy, float dx, GlyphStyle style, float emY)
    {
        var builder = new PathBuilder();
        float skew = (style.Synthesis & FontSynthesis.Italic) != 0 ? Synthesis.Skew : 0;
        HintMap? hint = style.Hint ? HintMap.For(face, sy) : null;
        face.GetGlyphOutline(glyph, new DeviceSink(builder, sx, sy, dx, skew, hint));
        Path path = builder.ToPath();
        if ((style.Synthesis & FontSynthesis.Bold) != 0 && !path.IsEmpty)
        {
            // The outset merges the contours too.
            path = path.Offset(Synthesis.BoldOutset(emY));
        }
        else if (!path.IsEmpty && face.MayOverlap(glyph))
        {
            // The coverage rasterizer adds up overlapping contours, which darkens their edges where they cross inside a pixel;
            // merged first, the mask is the exact area, as for any other glyph. Masks are cached, so this is paid once.
            path = path.Simplify();
        }

        return path;
    }

    // The paint LCD edging can draw: a solid color drawn source-over onto an 8-bit target with red, green and blue, with a known
    // pixel geometry. Anything else falls back to grayscale.
    private LcdPaint? LcdPaintFor(Paint paint)
    {
        if (Options.PixelGeometry == PixelGeometry.Unknown || paint is not SolidPaint solid || solid.BlendMode != Operations.BlendMode.SourceOver)
        {
            return null;
        }

        PixelLayoutInfo layout = TPixel.Layout;
        bool rgb = layout.ChannelOrder is PixelChannelOrder.Rgba or PixelChannelOrder.Bgra or PixelChannelOrder.Rgb or PixelChannelOrder.Bgr
            or PixelChannelOrder.Argb or PixelChannelOrder.Abgr;
        if (!rgb || layout.ChannelType != PixelChannelType.Unorm8 || _layers.Count > 0)
        {
            return null;
        }

        Rgba8 color = solid.Color.ToRgba8();
        float alpha = solid.Color.A * solid.Opacity;
        return alpha <= 0 ? null : new LcdPaint(color, Math.Clamp(alpha, 0, 1), Options.ColorSpace == Operations.ColorSpace.Linear);
    }

    private void ReportLcdFallback()
    {
        if (_lcdFallbackReported)
        {
            return;
        }

        _lcdFallbackReported = true;
        if (_context.Diagnostics is null || _context.DiagnosticLevel > DiagnosticLevel.Warning)
        {
            return;
        }

        using DiagnosticReporter reporter = _context.StartDiagnostics();
        reporter.Report(DiagnosticLevel.Warning, "Text.LcdFallback", "LCD edging needs a known pixel geometry, an 8-bit RGB canvas outside a layer and a solid source-over paint; text is drawn with grayscale antialiasing.", null);
    }

    // Blends a three-channel mask onto the canvas, each channel by its own coverage; a pixel whose alpha is not opaque takes the
    // channels' mean as one coverage.
    private void BlendLcd(GlyphMask mask, Rect placed, Rect visible, LcdPaint paint)
    {
        Span<Rgba8> row = visible.Width <= 256 ? stackalloc Rgba8[visible.Width] : new Rgba8[visible.Width];
        ClipMask? clip = _clip.Mask;
        for (int y = visible.Y; y < visible.Bottom; y++)
        {
            ReadOnlySpan<byte> cov = mask.Coverage.AsSpan((((y - placed.Y) * placed.Width) + (visible.X - placed.X)) * 3, visible.Width * 3);
            if (!cov.ContainsAnyExcept((byte)0))
            {
                continue;
            }

            ReadOnlySpan<byte> clipRow = clip is null ? default : clip.Row(y).Slice(visible.X - clip.Bounds.X, visible.Width);
            _target.ReadRow(y, visible.X, row);
            for (int i = 0; i < row.Length; i++)
            {
                float k = clip is null ? paint.Alpha : paint.Alpha * clipRow[i] / 255f;
                float r = cov[i * 3] * k / 255f, g = cov[(i * 3) + 1] * k / 255f, b = cov[(i * 3) + 2] * k / 255f;
                row[i] = paint.Blend(row[i], r, g, b);
            }

            _target.WriteRow<Rgba8>(y, visible.X, row);
        }
    }

    /// <summary>Font units to device pixels relative to the glyph origin: scaled per axis, slanted, y hinted and flipped.</summary>
    private sealed class DeviceSink(PathBuilder builder, float sx, float sy, float dx, float skew, HintMap? hint) : IGlyphOutlineSink
    {
        public void MoveTo(float x, float y) => builder.MoveTo(X(x, y), Y(y));

        public void LineTo(float x, float y) => builder.LineTo(X(x, y), Y(y));

        public void QuadTo(float cx, float cy, float x, float y) => builder.QuadTo(X(cx, cy), Y(cy), X(x, y), Y(y));

        public void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y)
            => builder.CubicTo(X(c1x, c1y), Y(c1y), X(c2x, c2y), Y(c2y), X(x, y), Y(y));

        public void Close() => builder.Close();

        private float X(float x, float y) => ((x + (skew * y)) * sx) + dx;

        private float Y(float y) => -(hint?.Map(y) ?? y * sy);
    }
}

/// <summary>What a glyph mask is drawn with besides the face, glyph, size and phase; for a color glyph, its palette and foreground too.</summary>
internal readonly record struct GlyphStyle(FontEdging Edging, bool Hint, FontSynthesis Synthesis, PixelGeometry Geometry)
{
    public ColorKey? Color { get; init; }
}

/// <summary>What a cached color glyph depends on besides its shape: the palette and the opaque foreground color.</summary>
internal sealed record class ColorKey(FontPalette Palette, RgbaF32 Foreground);

/// <summary>A solid color and alpha that LCD edging blends per channel, in encoded or linear values.</summary>
internal sealed class LcdPaint(Rgba8 color, float alpha, bool linear)
{
    public float Alpha { get; } = alpha;

    /// <summary>Source-over of the color onto a pixel with a coverage per channel.</summary>
    public Rgba8 Blend(Rgba8 dst, float r, float g, float b)
    {
        if (dst.A != 255)
        {
            float k = (r + g + b) / 3;
            return new Rgba8(Mix(dst.R, color.R, k), Mix(dst.G, color.G, k), Mix(dst.B, color.B, k), (byte)MathF.Round(dst.A + ((255 - dst.A) * k)));
        }

        return new Rgba8(Mix(dst.R, color.R, r), Mix(dst.G, color.G, g), Mix(dst.B, color.B, b), 255);
    }

    private byte Mix(byte dst, byte src, float k)
    {
        if (k <= 0)
        {
            return dst;
        }

        if (!linear)
        {
            return (byte)MathF.Round(dst + ((src - dst) * k));
        }

        float d = Decode(dst), s = Decode(src);
        return Encode(d + ((s - d) * k));
    }

    private static float Decode(byte v)
    {
        float c = v / 255f;
        return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    }

    private static byte Encode(float c)
    {
        c = Math.Clamp(c, 0, 1);
        float v = c <= 0.0031308f ? c * 12.92f : (1.055f * MathF.Pow(c, 1 / 2.4f)) - 0.055f;
        return (byte)MathF.Round(v * 255);
    }
}

/// <summary>
/// Light hinting's y mapping at one scale: each zone's flat edge rounded to a whole pixel and its overshoot to whole pixels beyond
/// it, points between zones interpolated linearly and points beyond the outer zones moved with the nearest.
/// </summary>
internal sealed class HintMap
{
    private readonly float[] _from;
    private readonly float[] _to;
    private readonly float _scale;

    private HintMap(float[] from, float[] to, float scale)
    {
        _from = from;
        _to = to;
        _scale = scale;
    }

    public static HintMap? For(Typeface face, float sy)
    {
        HintZone[] zones = face.HintZones;
        if (zones.Length == 0)
        {
            return null;
        }

        var points = new List<(float From, float To)>();
        foreach (HintZone zone in zones)
        {
            float flat = MathF.Round(zone.Flat * sy);
            points.Add((zone.Flat, flat));
            if (zone.Overshoot != zone.Flat)
            {
                points.Add((zone.Overshoot, flat + MathF.Round((zone.Overshoot - zone.Flat) * sy)));
            }
        }

        points.Sort((a, b) => a.From.CompareTo(b.From));

        // Keep the mapping monotonic: a point that would land below its predecessor is dropped.
        var from = new List<float>();
        var to = new List<float>();
        foreach ((float f, float t) in points)
        {
            if (from.Count > 0 && (f == from[^1] || t < to[^1]))
            {
                continue;
            }

            from.Add(f);
            to.Add(t);
        }

        return new HintMap([.. from], [.. to], sy);
    }

    /// <summary>Maps a y in font units to device pixels above the baseline.</summary>
    public float Map(float y)
    {
        if (y <= _from[0])
        {
            return _to[0] + ((y - _from[0]) * _scale);
        }

        int last = _from.Length - 1;
        if (y >= _from[last])
        {
            return _to[last] + ((y - _from[last]) * _scale);
        }

        int i = 1;
        while (_from[i] < y)
        {
            i++;
        }

        float f0 = _from[i - 1], f1 = _from[i];
        return _to[i - 1] + ((_to[i] - _to[i - 1]) * (y - f0) / (f1 - f0));
    }
}

/// <summary>LCD edging: coverage at three times the resolution along one axis, filtered by [1, 4, 6, 4, 1] / 16 into three channels.</summary>
internal static class Lcd
{
    public static GlyphMask Render(EdgeList edges, bool vertical, bool bgr, CoverageRasterizer rasterizer)
    {
        Rect sub = edges.Bounds.RoundOut();
        if (sub.IsEmpty)
        {
            return GlyphMask.Empty;
        }

        // Pixel bounds along the tripled axis, wide enough for the filter's two subpixels each side.
        int lo = vertical ? sub.Y : sub.X, hi = vertical ? sub.Bottom : sub.Right;
        int p0 = FloorDiv(lo - 2, 3), p1 = -FloorDiv(-(hi + 2), 3);
        Rect hiRes = vertical ? new Rect(sub.X, p0 * 3, sub.Width, (p1 - p0) * 3) : new Rect(p0 * 3, sub.Y, (p1 - p0) * 3, sub.Height);
        byte[] coverage = new byte[hiRes.Width * hiRes.Height];
        rasterizer.Fill(edges, hiRes, FillRule.NonZero, true, new BufferSink(coverage, hiRes), 1);

        Rect bounds = vertical ? new Rect(sub.X, p0, sub.Width, p1 - p0) : new Rect(p0, sub.Y, p1 - p0, sub.Height);
        byte[] rgb = new byte[bounds.Width * bounds.Height * 3];
        int stride = hiRes.Width;
        for (int y = 0; y < bounds.Height; y++)
        {
            for (int x = 0; x < bounds.Width; x++)
            {
                for (int c = 0; c < 3; c++)
                {
                    int sum = 0;
                    for (int t = -2; t <= 2; t++)
                    {
                        int weight = t is -2 or 2 ? 1 : t is -1 or 1 ? 4 : 6;
                        int sx = vertical ? x : (x * 3) + c + t, sy = vertical ? (y * 3) + c + t : y;
                        if ((uint)sx < (uint)hiRes.Width && (uint)sy < (uint)hiRes.Height)
                        {
                            sum += weight * coverage[(sy * stride) + sx];
                        }
                    }

                    int channel = bgr ? 2 - c : c;
                    rgb[(((y * bounds.Width) + x) * 3) + channel] = (byte)((sum + 8) >> 4);
                }
            }
        }

        return new GlyphMask(bounds, rgb, 3);
    }

    private static int FloorDiv(int a, int b) => (int)Math.Floor(a / (double)b);
}
