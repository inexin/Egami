using System.Numerics;

using Egami.Drawing.Rasterization;
using Egami.Fonts;

namespace Egami.Drawing;

public sealed partial class RasterCanvas<TPixel>
{
    /// <summary>The largest device em size drawn through the glyph cache; bigger glyphs fill as paths.</summary>
    internal const float MaxCachedEm = 256;

    /// <summary>The flattening tolerance of glyph masks, finer than the canvas default: glyphs are mostly curves, and a mask is made once.</summary>
    internal const float GlyphTolerance = 0.01f;

    /// <inheritdoc/>
    /// <remarks>
    /// Under a scale-and-translate transform, glyphs up to 256 device pixels per em are drawn from <see cref="GlyphCache"/> masks
    /// with their origins snapped to whole device pixels, their curves flattened to 0.01 pixel (or the canvas tolerance when finer);
    /// any other glyph run fills as one path at the canvas tolerance.
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

        if (_target.CreateSink(paint, m, Options.ColorSpace, _clip.Mask, 255) is not ISpanSink sink)
        {
            return;
        }

        Typeface face = font.Typeface;
        bool antialias = paint.Antialias;
        float sx = emX / face.UnitsPerEm, sy = emY / face.UnitsPerEm;
        Rect clip = _clip.Bounds;
        byte[]? scratch = null;
        ReadOnlySpan<ushort> glyphs = run.Glyphs;
        ReadOnlySpan<Vector2> positions = run.Positions;
        for (int i = 0; i < glyphs.Length; i++)
        {
            Vector2 device = Vector2.Transform(origin + positions[i], m);
            if (!(MathF.Abs(device.X) < 1e9f && MathF.Abs(device.Y) < 1e9f))
            {
                continue;
            }

            int ox = (int)MathF.Floor(device.X + 0.5f), oy = (int)MathF.Floor(device.Y + 0.5f);
            ushort glyph = glyphs[i];
            GlyphMask mask = GlyphCache.GetOrAdd(new GlyphKey(face, glyph, emX, emY, antialias), () => RenderMask(face, glyph, sx, sy, antialias));
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

            scratch ??= new byte[Math.Max(visible.Width, 64)];
            if (scratch.Length < visible.Width)
            {
                scratch = new byte[visible.Width];
            }

            for (int y = visible.Y; y < visible.Bottom; y++)
            {
                Span<byte> row = scratch.AsSpan(0, visible.Width);
                mask.Coverage.AsSpan(((y - placed.Y) * placed.Width) + (visible.X - placed.X), visible.Width).CopyTo(row);
                if (!row.ContainsAnyExcept((byte)0))
                {
                    continue;
                }

                sink.Blend(y, visible.X, row);
            }
        }
    }

    // A glyph's coverage with its origin at a pixel corner, through the same flattener and rasterizer as paths.
    private GlyphMask RenderMask(Typeface face, ushort glyph, float sx, float sy, bool antialias)
    {
        var builder = new PathBuilder();
        face.GetGlyphOutline(glyph, new ScaledSink(builder, sx, sy));
        Path path = builder.ToPath();
        if (path.IsEmpty)
        {
            return GlyphMask.Empty;
        }

        var edges = new EdgeList();
        Flattener.Flatten(path, Matrix3x2.Identity, MathF.Min(Options.Tolerance, GlyphTolerance), edges);
        Rect bounds = edges.Bounds.RoundOut();
        if (bounds.IsEmpty)
        {
            return GlyphMask.Empty;
        }

        byte[] coverage = new byte[bounds.Width * bounds.Height];
        _rasterizer.Fill(edges, bounds, FillRule.NonZero, antialias, new BufferSink(coverage, bounds), 1);
        return new GlyphMask(bounds, coverage);
    }

    /// <summary>Font units to device pixels relative to the glyph origin: scaled per axis, y flipped.</summary>
    private sealed class ScaledSink(PathBuilder builder, float sx, float sy) : IGlyphOutlineSink
    {
        public void MoveTo(float x, float y) => builder.MoveTo(x * sx, -y * sy);

        public void LineTo(float x, float y) => builder.LineTo(x * sx, -y * sy);

        public void QuadTo(float cx, float cy, float x, float y) => builder.QuadTo(cx * sx, -cy * sy, x * sx, -y * sy);

        public void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y)
            => builder.CubicTo(c1x * sx, -c1y * sy, c2x * sx, -c2y * sy, x * sx, -y * sy);

        public void Close() => builder.Close();
    }
}
