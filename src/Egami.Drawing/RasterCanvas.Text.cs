using System.Numerics;
using System.Runtime.CompilerServices;

using Egami.Drawing.Rasterization;
using Egami.Fonts;
using Egami.Fonts.Hinting;
using Egami.Fonts.Parsing;
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
    private LcdLook? _lcdLook;
    private bool _lcdLookMade;

    // The canvas's LCD filter, sampling and level as one cache-key part, or null for the defaults.
    private LcdLook? LcdLook
    {
        get
        {
            if (!_lcdLookMade)
            {
                CanvasOptions o = Options;
                _lcdLook = o.LcdFilter.Equals(LcdFilter.Default) && o.LcdSampling == LcdSampling.Smooth && o.ClearTypeLevel == 1
                    ? null
                    : new LcdLook(o.LcdFilter, o.LcdSampling, o.ClearTypeLevel);
                _lcdLookMade = true;
            }

            return _lcdLook;
        }
    }

    /// <inheritdoc/>
    protected override IColorGlyphRenderer? ColorGlyphRenderer => Options.ColorGlyphRenderer;

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

        run = DrawRendererGlyphs(run, origin, paint);
        if (run.Count == 0)
        {
            return;
        }

        // Blurred text fills as paths, which the blur needs whole. Only unhinted text moves with PixelOffset.None, as GDI+'s
        // AntiAlias text does and its grid-fitted text does not.
        Font font = run.Font;
        Matrix3x2 m = font.Hinting == FontHinting.None ? GeometryMatrix : Matrix;
        float emX = font.Size * m.M11, emY = font.Size * m.M22;
        if (m.M12 != 0 || m.M21 != 0 || emX == 0 || emY == 0 || MathF.Abs(emX) > MaxCachedEm || MathF.Abs(emY) > MaxCachedEm || paint.Blur is { Sigma: > 0 })
        {
            base.OnDrawGlyphRun(run, origin, paint);
            return;
        }

        // Text gamma needs the destination, so gamma text is drawn as LCD text is, by reading and writing pixels; contrast alone
        // only changes coverage and also goes through the sink.
        FontEdging edging = paint.Antialias ? font.Edging : FontEdging.Alias;
        TextBlend? blend = TextBlend.For(Options);
        SolidTextPaint? solidText = null;
        if (edging == FontEdging.Lcd)
        {
            solidText = Options.PixelGeometry == PixelGeometry.Unknown ? null : SolidTextPaintFor(paint, blend);
            if (solidText is null)
            {
                edging = FontEdging.Antialias;
                ReportLcdFallback();
            }
        }

        if (solidText is null && blend is { HasGamma: true })
        {
            solidText = SolidTextPaintFor(paint, blend);
        }

        ISpanSink? sink = null;
        if (solidText is null && (sink = _target.CreateSink(paint, m, Options.ColorSpace, _clip.Mask, 255)) is null)
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
        bool lcdEdging = edging == FontEdging.Lcd;
        bool full = font.Hinting == FontHinting.Full && MathF.Abs(emX) == MathF.Abs(emY) && face.CanRunInstructions;
        var style = new GlyphStyle(
            edging,
            font.Hinting is FontHinting.Light or FontHinting.Full && MathF.Abs(emY) <= MaxHintedEm,
            font.AppliedSynthesis,
            lcdEdging ? Options.PixelGeometry : PixelGeometry.Unknown)
        {
            Outset = font.Outset(emY, true),
            Look = lcdEdging ? LcdLook : null,
            Full = full ? font.HintingTarget : null,
        };
        bool subpixel = font.Subpixel && font.Metrics == FontMetrics.Design;

        // Embedded bitmaps apply only upright at a whole strike size; GetEmbeddedBitmap answers null at any other size.
        bool bitmaps = font.EmbeddedBitmaps && emX == emY && emY > 0;
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

            (int ox, int phase) = Snap(device.X, subpixel);
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

            GlyphMask? mask = bitmaps ? BitmapMask(face, glyph, emY) : null;
            if (mask is not null)
            {
                (ox, _) = Snap(device.X, false);
            }
            else
            {
                var key = new GlyphKey(face, glyph, emX, emY, style, phase);
                if (!GlyphCache.TryGet(key, out mask))
                {
                    mask = RenderMask(face, glyph, sx, sy, phase, style);
                    GlyphCache.Add(key, mask);
                }
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

            if (solidText is not null)
            {
                BlendSolid(mask, placed, visible, solidText.Value, blend);
            }
            else
            {
                BlendMask(mask, placed, visible, sink!, blend, ref scratch);
            }
        }

        if (uncachedColor is not null)
        {
            base.OnDrawGlyphRun(Subrun(run, uncachedColor), origin, paint);
        }
    }

    // A coverage mask blended through the sink row by row, after the text contrast boost; rows with no coverage are skipped.
    private static void BlendMask(GlyphMask mask, Rect placed, Rect visible, ISpanSink sink, TextBlend? blend, ref byte[]? scratch)
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
                blend?.Boost(row);
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

    // A glyph's embedded bitmap as a cached grayscale mask, or null when no strike of exactly this size holds it. Kept out of the
    // glyph loop so text without embedded bitmaps runs the same code as before.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static GlyphMask? BitmapMask(Typeface face, ushort glyph, float em)
    {
        if (face.GetEmbeddedBitmap(glyph, em) is not Fonts.Color.EmbeddedBitmap bitmap)
        {
            return null;
        }

        var key = new GlyphKey(face, glyph, em, em, new GlyphStyle(FontEdging.Alias, false, FontSynthesis.None, PixelGeometry.Unknown) { Bitmap = true }, 0);
        if (!GlyphCache.TryGet(key, out GlyphMask? mask))
        {
            mask = bitmap.Width == 0 ? GlyphMask.Empty : new GlyphMask(new Rect(bitmap.BearingX, -bitmap.BearingY, bitmap.Width, bitmap.Height), bitmap.Coverage, 1);
            GlyphCache.Add(key, mask);
        }

        return mask;
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
    // slanted for synthetic italic, its y snapped to the hint zones, outset for synthetic bold and embolden, and for LCD edging at
    // three times the resolution along the pixel geometry, filtered into one coverage per color channel.
    private GlyphMask RenderMask(Typeface face, ushort glyph, float sx, float sy, int phase, GlyphStyle style)
    {
        Path path = GlyphOutline(face, glyph, sx, sy, phase / 4f, style);
        if (path.IsEmpty)
        {
            return GlyphMask.Empty;
        }

        float tolerance = MathF.Min(Options.Tolerance, GlyphTolerance);
        if (style.Edging == FontEdging.Lcd)
        {
            return Lcd.Render(path, tolerance, style, _rasterizer);
        }

        var edges = new EdgeList();
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

    [ThreadStatic]
    private static OutlineRecorder? t_lightOutline;

    /// <summary>A glyph's device outline relative to its pixel origin: scaled, slanted, hinted and outset as the style asks.</summary>
    internal static Path GlyphOutline(Typeface face, ushort glyph, float sx, float sy, float dx, GlyphStyle style)
    {
        var builder = new PathBuilder();
        float skew = (style.Synthesis & FontSynthesis.Italic) != 0 ? Synthesis.Skew : 0;
        if (style.Full is TrueTypeTarget target && face.GetHintedGlyph(glyph, sy * face.UnitsPerEm, target) is HintedGlyph hinted)
        {
            // The font's instructions placed the points in pixels at the whole ppem; synthetic italic slants them after.
            hinted.Emit(new PixelSink(builder, MathF.Sign(sx), MathF.Sign(sy), dx, skew));
        }
        else
        {
            // Light hinting, also where full hinting does not apply (CFF, unhinted, variable, gasp).
            if (style.Hint)
            {
                // The outline is decoded once, into the thread's own recorder: light hinting reads its horizontal stems, then it
                // is drawn through the map. (The shared recorder stays free for the face's own decoding.)
                OutlineRecorder recorder = t_lightOutline ??= new OutlineRecorder();
                recorder.Clear();
                face.GetGlyphOutline(glyph, recorder);
                HintMap? hint = HintMap.For(face, sy, recorder);
                recorder.ReplayTo(new DeviceSink(builder, sx, sy, dx, skew, hint));
            }
            else
            {
                face.GetGlyphOutline(glyph, new DeviceSink(builder, sx, sy, dx, skew, null));
            }
        }

        Path path = builder.ToPath();
        if (style.Outset > 0 && !path.IsEmpty)
        {
            // The outset merges the contours too.
            path = path.Inflate(style.Outset);
        }
        else if (!path.IsEmpty && face.MayOverlap(glyph))
        {
            // The coverage rasterizer adds up overlapping contours, which darkens their edges where they cross inside a pixel;
            // merged first, the mask is the exact area, as for any other glyph. Masks are cached, so this is paid once.
            path = path.Simplify();
        }

        return path;
    }

    // The paint LCD edging and text gamma can draw: a solid color drawn source-over onto an 8-bit target with red, green and blue.
    // Anything else goes through the sink: in grayscale, without the gamma.
    private SolidTextPaint? SolidTextPaintFor(Paint paint, TextBlend? blend)
    {
        if (paint is not SolidPaint solid || solid.BlendMode != Operations.BlendMode.SourceOver)
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
        return alpha <= 0 ? null : new SolidTextPaint(color, Math.Clamp(alpha, 0, 1), Options.ColorSpace == Operations.ColorSpace.Linear, blend is { HasGamma: true } ? blend : null);
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

    // Blends a mask onto the canvas by reading and writing pixels: a three-channel mask each channel by its own coverage, a
    // grayscale one every channel by the same; a pixel whose alpha is not opaque takes the channels' mean as one coverage. The
    // text contrast boosts the coverage first.
    private void BlendSolid(GlyphMask mask, Rect placed, Rect visible, in SolidTextPaint paint, TextBlend? blend)
    {
        Span<Rgba8> row = visible.Width <= 256 ? stackalloc Rgba8[visible.Width] : new Rgba8[visible.Width];
        ClipMask? clip = _clip.Mask;
        int n = mask.Channels;
        Span<byte> boosted = blend is not { HasBoost: true } ? default : visible.Width * n <= 768 ? stackalloc byte[visible.Width * n] : new byte[visible.Width * n];
        for (int y = visible.Y; y < visible.Bottom; y++)
        {
            ReadOnlySpan<byte> mapped = mask.Coverage.AsSpan((((y - placed.Y) * placed.Width) + (visible.X - placed.X)) * n, visible.Width * n);
            if (!mapped.ContainsAnyExcept((byte)0))
            {
                continue;
            }

            if (!boosted.IsEmpty)
            {
                mapped.CopyTo(boosted);
                blend!.Boost(boosted);
            }

            ReadOnlySpan<byte> cov = boosted.IsEmpty ? mapped : boosted;

            ReadOnlySpan<byte> clipRow = clip is null ? default : clip.Row(y).Slice(visible.X - clip.Bounds.X, visible.Width);
            _target.ReadRow(y, visible.X, row);
            if (n == 1)
            {
                for (int i = 0; i < row.Length; i++)
                {
                    float k = clip is null ? paint.Alpha : paint.Alpha * clipRow[i] / 255f;
                    float a = cov[i] * k / 255f;
                    row[i] = paint.Blend(row[i], a, a, a);
                }
            }
            else
            {
                for (int i = 0; i < row.Length; i++)
                {
                    float k = clip is null ? paint.Alpha : paint.Alpha * clipRow[i] / 255f;
                    float r = cov[i * 3] * k / 255f, g = cov[(i * 3) + 1] * k / 255f, b = cov[(i * 3) + 2] * k / 255f;
                    row[i] = paint.Blend(row[i], r, g, b);
                }
            }

            _target.WriteRow<Rgba8>(y, visible.X, row);
        }
    }

    /// <summary>Hinted pixels (y up) to device pixels relative to the glyph origin: mirrored with the scale's signs, slanted and flipped.</summary>
    private sealed class PixelSink(PathBuilder builder, float signX, float signY, float dx, float skew) : IGlyphOutlineSink
    {
        public void MoveTo(float x, float y) => builder.MoveTo(X(x, y), Y(y));

        public void LineTo(float x, float y) => builder.LineTo(X(x, y), Y(y));

        public void QuadTo(float cx, float cy, float x, float y) => builder.QuadTo(X(cx, cy), Y(cy), X(x, y), Y(y));

        public void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y)
            => builder.CubicTo(X(c1x, c1y), Y(c1y), X(c2x, c2y), Y(c2y), X(x, y), Y(y));

        public void Close() => builder.Close();

        private float X(float x, float y) => ((x + (skew * y)) * signX) + dx;

        private float Y(float y) => -y * signY;
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
/// <remarks>Packed into 24 bytes, as many as before 2.7.5, since every cached glyph keeps one.</remarks>
internal readonly record struct GlyphStyle
{
    private readonly byte _edging;
    private readonly byte _synthesis;
    private readonly byte _geometry;
    // Bit 0: light hinting; bits 1 to 3: full hinting's target plus one; bit 7: an embedded bitmap.
    private readonly byte _hint;

    public GlyphStyle(FontEdging edging, bool hint, FontSynthesis synthesis, PixelGeometry geometry)
    {
        _edging = (byte)edging;
        _hint = hint ? (byte)1 : (byte)0;
        _synthesis = (byte)synthesis;
        _geometry = (byte)geometry;
    }

    public FontEdging Edging => (FontEdging)_edging;

    public bool Hint => (_hint & 1) != 0;

    /// <summary>Gets the target the font's instructions run for, or null when they do not.</summary>
    public TrueTypeTarget? Full
    {
        get => ((_hint >> 1) & 7) is int t and > 0 ? (TrueTypeTarget)(t - 1) : null;
        init => _hint = (byte)((_hint & 0x81) | (value is TrueTypeTarget t ? ((int)t + 1) << 1 : 0));
    }

    public FontSynthesis Synthesis => (FontSynthesis)_synthesis;

    public PixelGeometry Geometry => (PixelGeometry)_geometry;

    public ColorKey? Color { get; init; }

    /// <summary>Gets how far the outline grows, in device pixels: synthetic bold, embolden and stem darkening together.</summary>
    public float Outset { get; init; }

    /// <summary>Gets an LCD mask's filter, sampling and level, or null for the defaults (and for masks that are not LCD).</summary>
    public LcdLook? Look { get; init; }

    /// <summary>Gets a value indicating whether the mask is the glyph's embedded bitmap (EBDT) rather than its outline.</summary>
    public bool Bitmap
    {
        get => (_hint & 0x80) != 0;
        init => _hint = (byte)(value ? _hint | 0x80 : _hint & 0x7F);
    }
}

/// <summary>The LCD settings of a canvas that change its masks.</summary>
internal sealed record class LcdLook(LcdFilter Filter, LcdSampling Sampling, float Level);

/// <summary>What a cached color glyph depends on besides its shape: the palette and the opaque foreground color.</summary>
internal sealed record class ColorKey(FontPalette Palette, RgbaF32 Foreground);

/// <summary>A solid color and alpha that LCD edging and text gamma blend per channel: in encoded values, linear ones or through the gamma.</summary>
internal readonly struct SolidTextPaint(Rgba8 color, float alpha, bool linear, TextBlend? gamma)
{
    public float Alpha { get; } = alpha;

    /// <summary>Source-over of the color onto a pixel with a coverage per channel.</summary>
    public Rgba8 Blend(Rgba8 dst, float r, float g, float b)
    {
        if (gamma is not null)
        {
            return BlendGamma(dst, r, g, b);
        }

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

    // The same through the text gamma, which is only set for encoded blending.
    private Rgba8 BlendGamma(Rgba8 dst, float r, float g, float b)
    {
        if (dst.A != 255)
        {
            float k = (r + g + b) / 3;
            return new Rgba8(gamma!.Mix(dst.R, color.R, k), gamma.Mix(dst.G, color.G, k), gamma.Mix(dst.B, color.B, k), (byte)MathF.Round(dst.A + ((255 - dst.A) * k)));
        }

        return new Rgba8(gamma!.Mix(dst.R, color.R, r), gamma.Mix(dst.G, color.G, g), gamma.Mix(dst.B, color.B, b), 255);
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
/// Light hinting's y mapping at one scale, after the light autohinter the 2.7.5 study describes: the scale is first nudged so the
/// x-height lands on a whole pixel (when that moves the tallest zone by under 2 pixels); each zone shorter than 3/4 pixel has its
/// flat edge rounded to a whole pixel and its overshoot to whole pixels beyond it, taller zones are left alone; the glyph's own
/// horizontal stems between zones keep their width with their lower edge on a whole pixel; points in between are interpolated
/// linearly and points beyond the outer control points move with the nearest.
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

    /// <summary>The map for one glyph, its outline decoded here (for tests; drawing passes the outline it decoded already).</summary>
    public static HintMap? ForGlyph(Typeface face, float sy, ushort glyph)
    {
        var recorder = new OutlineRecorder();
        face.GetGlyphOutline(glyph, recorder);
        return For(face, sy, recorder);
    }

    public static HintMap? For(Typeface face, float sy, OutlineRecorder? outline = null)
    {
        HintZone[] zones = face.HintZones;
        if (zones.Length == 0)
        {
            return null;
        }

        sy = XHeightScale(face, zones, sy);
        var points = new List<(float From, float To)>();
        foreach (HintZone zone in zones)
        {
            // A zone 3/4 pixel tall or more is not snapped: at that size its overshoot is drawn as it is.
            if (MathF.Abs((zone.Overshoot - zone.Flat) * sy) >= 0.75f)
            {
                continue;
            }

            float flat = MathF.Round(zone.Flat * sy);
            points.Add((zone.Flat, flat));
            if (zone.Overshoot != zone.Flat)
            {
                points.Add((zone.Overshoot, flat + MathF.Round((zone.Overshoot - zone.Flat) * sy)));
            }
        }

        points.Sort((a, b) => a.From.CompareTo(b.From));
        if (points.Count == 0)
        {
            return new HintMap([0], [0], sy);
        }

        if (outline is not null)
        {
            // The glyph's horizontal stems (crossbars) between zones: where the zone mapping would put the lower edge, rounded to a
            // pixel, and the upper edge the stem's own height above it.
            int zonePoints = points.Count;
            float tolerance = face.UnitsPerEm / 60f;
            foreach ((float low, float high) in Stems(face, outline))
            {
                if (NearZone(zones, low, tolerance) || NearZone(zones, high, tolerance))
                {
                    continue;
                }

                float bottom = MathF.Round(Interpolate(points, zonePoints, low, sy));
                points.Add((low, bottom));
                points.Add((high, bottom + ((high - low) * sy)));
            }

            points.Sort((a, b) => a.From.CompareTo(b.From));
        }

        return Build(points, sy);
    }

    // The scale nudged so the x-height (OS/2 sxHeight, else the top of 'x') lands on a whole pixel: up when its fraction is 40/64
    // or more, else down, unless that moves the tallest zone by 2 pixels or more.
    private static float XHeightScale(Typeface face, HintZone[] zones, float sy)
    {
        float xHeight = face.XHeightForHinting;
        float scaled = MathF.Abs(xHeight * sy);
        if (xHeight <= 0 || scaled < 1)
        {
            return sy;
        }

        float fraction = scaled - MathF.Floor(scaled);
        float target = fraction >= 40 / 64f ? MathF.Ceiling(scaled) : MathF.Floor(scaled);
        float tweaked = sy * target / scaled;
        float tallest = 0;
        foreach (HintZone z in zones)
        {
            tallest = MathF.Max(tallest, MathF.Abs(z.Flat));
        }

        return MathF.Abs(tallest * (tweaked - sy)) < 2 ? tweaked : sy;
    }

    private static bool NearZone(HintZone[] zones, float y, float tolerance)
    {
        foreach (HintZone z in zones)
        {
            if (MathF.Abs(z.Flat - y) < tolerance || MathF.Abs(z.Overshoot - y) < tolerance)
            {
                return true;
            }
        }

        return false;
    }

    // The zone mapping of y through the first `count` control points (sorted by font units), as Map does, without building a map.
    private static float Interpolate(List<(float From, float To)> points, int count, float y, float sy)
    {
        if (y <= points[0].From)
        {
            return points[0].To + ((y - points[0].From) * sy);
        }

        for (int i = 1; i < count; i++)
        {
            (float f0, float t0) = points[i - 1];
            (float f1, float t1) = points[i];
            if (y <= f1)
            {
                return f1 == f0 ? t1 : t0 + ((t1 - t0) * (y - f0) / (f1 - f0));
            }
        }

        return points[count - 1].To + ((y - points[count - 1].From) * sy);
    }

    [ThreadStatic]
    private static SegmentSink? t_segments;

    [ThreadStatic]
    private static List<(float Low, float High)>? t_stems;

    // Horizontal stems of a glyph in font units: pairs of horizontal outline segments that overlap in x by half the shorter one,
    // between 1/60 and 1/6 em apart; each segment pairs with the nearest one above it. The list is the thread's own, reused.
    private static List<(float Low, float High)> Stems(Typeface face, OutlineRecorder outline)
    {
        SegmentSink sink = t_segments ??= new SegmentSink();
        sink.Reset(face.UnitsPerEm);
        outline.ReplayTo(sink);
        float min = face.UnitsPerEm / 60f, max = face.UnitsPerEm / 6f;
        List<(float Low, float High)> stems = t_stems ??= [];
        stems.Clear();
        foreach ((float y, float x0, float x1) in sink.Horizontals)
        {
            float best = float.MaxValue;
            foreach ((float y2, float a0, float a1) in sink.Horizontals)
            {
                float d = y2 - y, overlap = MathF.Min(x1, a1) - MathF.Max(x0, a0);
                if (d >= min && d <= max && d < best && overlap >= 0.5f * MathF.Min(x1 - x0, a1 - a0))
                {
                    best = d;
                }
            }

            if (best != float.MaxValue && !stems.Contains((y, y + best)))
            {
                stems.Add((y, y + best));
            }
        }

        return stems;
    }

    private static HintMap Build(List<(float From, float To)> points, float sy)
    {
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

/// <summary>Records an outline's horizontal straight segments in font units: y, and the x range, longer than 1/40 em.</summary>
internal sealed class SegmentSink : IGlyphOutlineSink
{
    private int _unitsPerEm = 1000;
    private float _x;
    private float _y;
    private float _startX;
    private float _startY;

    public List<(float Y, float X0, float X1)> Horizontals { get; } = [];

    /// <summary>Clears the segments for an outline of a face with this many units per em.</summary>
    public void Reset(int unitsPerEm)
    {
        _unitsPerEm = unitsPerEm;
        Horizontals.Clear();
    }

    public void MoveTo(float x, float y) => (_x, _y, _startX, _startY) = (x, y, x, y);

    public void LineTo(float x, float y) => Segment(x, y);

    public void QuadTo(float cx, float cy, float x, float y) => (_x, _y) = (x, y);

    public void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y) => (_x, _y) = (x, y);

    public void Close() => Segment(_startX, _startY);

    private void Segment(float x, float y)
    {
        if (MathF.Abs(y - _y) <= _unitsPerEm / 200f && MathF.Abs(x - _x) >= _unitsPerEm / 40f)
        {
            Horizontals.Add(((y + _y) / 2, MathF.Min(x, _x), MathF.Max(x, _x)));
        }

        (_x, _y) = (x, y);
    }
}

/// <summary>
/// LCD edging: coverage at three times the resolution along one axis, spread by the canvas's <see cref="LcdFilter"/> (five taps
/// adding up to 256) into three channels and mixed with the pixel's grayscale coverage by the ClearType level. Smooth sampling
/// takes each subpixel's exact area; Gdi sampling, as GDI+ ClearType does, takes two on-or-off samples per subpixel on each
/// pixel's centre line, so a channel of the <see cref="LcdFilter.Gdi"/> filter counts 0 to 6 sixths.
/// </summary>
internal static class Lcd
{
    // Gdi sampling stretches the axis along the stripes by this much and keeps the middle row of each pixel's rows.
    private const int Strip = 64;

    // Gdi sampling's on-or-off samples per pixel across the stripes.
    private const int Samples = 6;

    public static GlyphMask Render(Path path, float tolerance, GlyphStyle style, CoverageRasterizer rasterizer)
    {
        bool vertical = style.Geometry is PixelGeometry.RgbVertical or PixelGeometry.BgrVertical;
        bool bgr = style.Geometry is PixelGeometry.BgrHorizontal or PixelGeometry.BgrVertical;
        if (style.Look?.Sampling == LcdSampling.Gdi)
        {
            return RenderGdi(path, tolerance, style, rasterizer, vertical, bgr);
        }

        var edges = new EdgeList();
        Flattener.Flatten(path, vertical ? Matrix3x2.CreateScale(1, 3) : Matrix3x2.CreateScale(3, 1), tolerance, edges);
        Rect sub = edges.Bounds.RoundOut();
        if (sub.IsEmpty)
        {
            return GlyphMask.Empty;
        }

        // Pixel bounds along the tripled axis, wide enough for the filter's two subpixels each side.
        int lo = vertical ? sub.Y : sub.X, hi = vertical ? sub.Bottom : sub.Right;
        int p0 = FloorDiv(lo - 2, 3);
        int pixels = -FloorDiv(-(hi + 2), 3) - p0;
        int l0 = vertical ? sub.X : sub.Y;
        int lines = vertical ? sub.Width : sub.Height;
        Rect hiRes = vertical ? new Rect(sub.X, p0 * 3, sub.Width, pixels * 3) : new Rect(p0 * 3, sub.Y, pixels * 3, sub.Height);
        byte[] samples = new byte[hiRes.Width * hiRes.Height];
        rasterizer.Fill(edges, hiRes, FillRule.NonZero, true, new BufferSink(samples, hiRes), 1);
        (int lineStride, int subStride) = vertical ? (1, hiRes.Width) : (hiRes.Width, 1);

        LcdFilter filter = style.Look?.Filter ?? LcdFilter.Default;
        int level = style.Look is LcdLook look ? (int)MathF.Round(Math.Clamp(look.Level, 0, 1) * 256) : 256;
        int count = pixels * 3;
        byte[] rgb = new byte[lines * pixels * 3];
        for (int l = 0; l < lines; l++)
        {
            int line = l * lineStride;
            for (int p = 0; p < pixels; p++)
            {
                int gray = 0;
                if (level < 256)
                {
                    int s0 = line + (p * 3 * subStride);
                    gray = samples[s0] + samples[s0 + subStride] + samples[s0 + (2 * subStride)];
                }

                int at = (vertical ? (p * lines) + l : (l * pixels) + p) * 3;
                for (int c = 0; c < 3; c++)
                {
                    int sum = 0;
                    for (int t = -2; t <= 2; t++)
                    {
                        int s = (p * 3) + c + t;
                        if ((uint)s < (uint)count)
                        {
                            sum += filter[t + 2] * samples[line + (s * subStride)];
                        }
                    }

                    int value = (sum + 128) >> 8;
                    if (level < 256)
                    {
                        // Both in 1/768ths: the filtered value times 3 against the three samples' sum.
                        value = (((value * 3 * level) + (gray * (256 - level))) + 384) / 768;
                    }

                    rgb[at + (bgr ? 2 - c : c)] = (byte)value;
                }
            }
        }

        Rect bounds = vertical ? new Rect(l0, p0, lines, pixels) : new Rect(p0, l0, pixels, lines);
        return new GlyphMask(bounds, rgb, 3);
    }

    // Gdi sampling: six samples per pixel across the stripes and the pixels' centre lines as rows (a vertical stripe swaps the axes
    // first); a sample is on when the outline covers at least half of its thin cell, which is where the outline crosses the cell's
    // centre. Each subpixel then holds 0 to 2 samples, and the filter spreads those counts over three channels: with the Gdi
    // filter a channel is the samples of its subpixel and its two neighbours, k of 6, stored as k × 255 / 6 to within a level.
    private static GlyphMask RenderGdi(Path path, float tolerance, GlyphStyle style, CoverageRasterizer rasterizer, bool vertical, bool bgr)
    {
        var edges = new EdgeList();
        Matrix3x2 m = vertical ? new Matrix3x2(0, Strip, Samples, 0, 0, 0) : Matrix3x2.CreateScale(Samples, Strip);
        Flattener.Flatten(path, m, tolerance, edges);
        Rect sub = edges.Bounds.RoundOut();
        if (sub.IsEmpty)
        {
            return GlyphMask.Empty;
        }

        // Pixels wide enough for the filter's two subpixels (four samples) each side.
        int l0 = FloorDiv(sub.Y, Strip);
        int lines = -FloorDiv(-sub.Bottom, Strip) - l0;
        int p0 = FloorDiv(sub.X - 4, Samples);
        int pixels = -FloorDiv(-(sub.Right + 4), Samples) - p0;
        int width = pixels * Samples, count = pixels * 3;
        byte[] samples = new byte[width];
        byte[] counts = new byte[count];
        byte[] rgb = new byte[lines * count];
        LcdFilter filter = style.Look?.Filter ?? LcdFilter.Gdi;
        int level = style.Look is LcdLook look ? (int)MathF.Round(Math.Clamp(look.Level, 0, 1) * 256) : 256;
        for (int l = 0; l < lines; l++)
        {
            var row = new Rect(p0 * Samples, ((l0 + l) * Strip) + (Strip / 2), width, 1);
            Array.Clear(samples);
            rasterizer.Fill(edges, row, FillRule.NonZero, true, new BufferSink(samples, row), 1);
            for (int s = 0; s < count; s++)
            {
                counts[s] = (byte)((samples[2 * s] >= 128 ? 1 : 0) + (samples[(2 * s) + 1] >= 128 ? 1 : 0));
            }

            for (int p = 0; p < pixels; p++)
            {
                int at = (vertical ? (p * lines) + l : (l * pixels) + p) * 3;

                // In 1/768ths: the pixel's six samples at 128 each.
                int gray = (counts[p * 3] + counts[(p * 3) + 1] + counts[(p * 3) + 2]) * 128;
                for (int c = 0; c < 3; c++)
                {
                    int sum = 0;
                    for (int t = -2; t <= 2; t++)
                    {
                        int s = (p * 3) + c + t;
                        if ((uint)s < (uint)count)
                        {
                            sum += filter[t + 2] * counts[s];
                        }
                    }

                    // The counts are halves of a subpixel: the weights' 256 and the 2 together are 512.
                    int value = ((sum * 255) + 256) / 512;
                    if (level < 256)
                    {
                        value = (((value * 3 * level) + (gray * (256 - level))) + 384) / 768;
                    }

                    rgb[at + (bgr ? 2 - c : c)] = (byte)value;
                }
            }
        }

        Rect bounds = vertical ? new Rect(l0, p0, lines, pixels) : new Rect(p0, l0, pixels, lines);
        return new GlyphMask(bounds, rgb, 3);
    }

    private static int FloorDiv(int a, int b) => (int)Math.Floor(a / (double)b);
}
