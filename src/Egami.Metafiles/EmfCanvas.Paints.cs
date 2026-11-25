using System.Numerics;

using Egami.Drawing;
using Egami.Formats.Png;
using Egami.GdiPlus;
using Egami.Metafiles.Writing;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Metafiles;

public sealed partial class EmfCanvas
{
    /// <summary>The most periods of a repeating gradient unrolled into stops or triangles before it falls back to an image.</summary>
    private const int MaxPeriods = 256;

    private readonly Dictionary<(Raster Image, float Opacity), byte[]> _textures = [];

    /// <summary>
    /// A paint EMF+ draws with a brush object: the brush, the GDI+ interpolation mode it needs (textures), and the Gouraud
    /// triangles of GDI's EMR_GRADIENTFILL when GDI can draw it exactly too.
    /// </summary>
    private sealed record class BrushPlan(byte[] Brush, int Interpolation, GdiGradient? Gradient);

    /// <summary>Gouraud triangles in local coordinates with opaque vertex colors.</summary>
    private sealed record class GdiGradient(Vector2[] Points, Rgba8[] Colors, int[] Triangles);

    // ---- Solid colors ----

    /// <summary>
    /// The color a paint draws everywhere, before its own opacity, when it is built only of solid paints: a
    /// <see cref="SolidPaint"/>, or a <see cref="ColorMatrixPaint"/> or <see cref="BlendPaint"/> over solid paints, whose color
    /// Egami works out by drawing one pixel. Null for any other paint.
    /// </summary>
    private static RgbaF32? Constant(Paint paint)
    {
        if (paint is SolidPaint solid)
        {
            return solid.Color;
        }

        if (paint is not (ColorMatrixPaint or BlendPaint) || !Leaves(paint))
        {
            return null;
        }

        using Raster<RgbaF32> pixel = Raster.Create<RgbaF32>(1, 1);
        using (RasterCanvas<RgbaF32> canvas = pixel.CreateCanvas())
        {
            canvas.FillRect(new RectF(0, 0, 1, 1), paint with { Opacity = 1, BlendMode = BlendMode.SourceOver, Antialias = false, Blur = null, Dither = false });
        }

        return pixel.GetPixel(0, 0);

        static bool Leaves(Paint p) => p switch
        {
            SolidPaint => true,
            ColorMatrixPaint m => Leaves(m.Source),
            BlendPaint b => Leaves(b.Source) && Leaves(b.Destination),
            _ => false,
        };
    }

    // ---- Brushes ----

    /// <summary>
    /// Plans a paint that is not one color as an EMF+ brush over a fill's local bounds: a linear gradient as a
    /// LinearGradientBrush, a centered radial gradient as a PathGradientBrush on a circle, an image or picture paint as a
    /// TextureBrush. Returns false with a reason when EMF+ has no exact form for it; true with a null plan when it draws nothing.
    /// </summary>
    private bool TryBrush(Paint paint, RectF bounds, float opacity, out BrushPlan? plan, out string? reason)
    {
        plan = null;
        reason = null;
        if (paint is not (LinearGradient or RadialGradient or ImagePaint or PicturePaint))
        {
            reason = paint switch
            {
                SweepGradient => "a sweep gradient",
                ConicalGradient => "a two-point conical gradient",
                _ => $"a {paint.GetType().Name}",
            };
            return false;
        }

        if (!Matrix3x2.Invert(paint.LocalMatrix, out Matrix3x2 inverse))
        {
            return true; // Egami draws nothing with a paint it cannot map
        }

        Vector2[] corners = [.. Corners(bounds).Select(p => Vector2.Transform(p, inverse))];
        return paint switch
        {
            LinearGradient l => TryLinear(l, corners, opacity, out plan, out reason),
            RadialGradient r => TryRadial(r, corners, opacity, out plan, out reason),
            ImagePaint i => TryTexture(i.Image, i.TileX, i.TileY, i.LocalMatrix, Interpolation(i.Sampling.Filter), opacity, out plan, out reason),
            _ => TryPicture((PicturePaint)paint, opacity, out plan, out reason),
        };
    }

    /// <summary>
    /// A linear gradient as a LinearGradientBrush: a brush rectangle one unit high along the gradient's line, under a transform
    /// that maps it onto the line and through the paint's matrix, with the stops as preset colors (BlendColors). Clamped ends
    /// become a brush wide enough to cover the shape, so GDI+'s tiling never shows; repeat is WrapModeTile and mirror
    /// WrapModeTileFlipX.
    /// </summary>
    private static bool TryLinear(LinearGradient g, Vector2[] corners, float opacity, out BrushPlan? plan, out string? reason)
    {
        plan = null;
        if (!Mixable(g, out bool gamma, out reason))
        {
            return false;
        }

        Vector2 d = g.End - g.Start;
        float dd = d.LengthSquared();
        if (!(dd > 0) || !float.IsFinite(dd))
        {
            reason = "a gradient with no extent";
            return false;
        }

        Vector2 perp = new(-d.Y, d.X);
        float tmin = float.MaxValue, tmax = float.MinValue, umin = float.MaxValue, umax = float.MinValue;
        foreach (Vector2 p in corners)
        {
            Vector2 rel = p - g.Start;
            float t = Vector2.Dot(rel, d) / dd, u = Vector2.Dot(rel, perp) / dd;
            (tmin, tmax, umin, umax) = (MathF.Min(tmin, t), MathF.Max(tmax, t), MathF.Min(umin, u), MathF.Max(umax, u));
        }

        if (!float.IsFinite(tmin) || !float.IsFinite(tmax) || tmax - tmin > 1e6f)
        {
            reason = "a gradient far larger than its shape";
            return false;
        }

        List<(float Offset, Rgba8 Color)> stops = Stops(g, opacity);
        float lo = 0, hi = 1;
        int wrap;
        switch (g.Tile)
        {
            case TileMode.Clamp when g.ExtendStart && g.ExtendEnd:
                lo = MathF.Min(0, MathF.Floor(tmin));
                hi = MathF.Max(1, MathF.Ceiling(tmax));
                wrap = GdiPlusBrushes.WrapTile;
                break;
            case TileMode.Repeat:
                wrap = GdiPlusBrushes.WrapTile;
                break;
            case TileMode.Mirror:
                wrap = GdiPlusBrushes.WrapTileFlipX;
                break;
            default:
                reason = "a gradient that is transparent past its ends";
                return false;
        }

        List<(float Position, Rgba8 Color)> presets = Padded([.. stops.Select(s => ((s.Offset - lo) / (hi - lo), s.Color))]);
        RecordBuffer b = new(64 + (presets.Count * 8));
        b.U32(GdiPlusData.Version).I32(4).U32(0x02 | 0x04 | (gamma ? 0x80u : 0)).I32(wrap); // LinearGradient: transform, preset colors
        b.F32(lo).F32(0).F32(hi - lo).F32(1);
        b.U32(Argb(presets[0].Color)).U32(Argb(presets[^1].Color)).U32(0).U32(0);
        b.Matrix(new Matrix3x2(d.X, d.Y, perp.X, perp.Y, g.Start.X, g.Start.Y) * g.LocalMatrix);
        Blend(b, presets);

        // GDI draws an opaque gradient exactly as Gouraud triangles across the shape, one strip per pair of stops.
        GdiGradient? gdi = null;
        if (!gamma && stops.All(s => s.Color.A == 255))
        {
            gdi = Strips(g, d, perp, stops, tmin, tmax, umin - 0.01f, umax + 0.01f);
        }

        plan = new BrushPlan(b.ToArray(), 0, gdi);
        return true;
    }

    /// <summary>The strips of a linear gradient's GDI triangles over the shape's span of the line, or null when there are too many.</summary>
    private static GdiGradient? Strips(LinearGradient g, Vector2 d, Vector2 perp, List<(float Offset, Rgba8 Color)> stops, float tmin, float tmax, float umin, float umax)
    {
        List<(float T, Rgba8 Color)> line = [];
        if (g.Tile == TileMode.Clamp)
        {
            line.Add((MathF.Min(tmin, stops[0].Offset), stops[0].Color));
            line.AddRange(stops);
            line.Add((MathF.Max(tmax, stops[^1].Offset), stops[^1].Color));
        }
        else
        {
            List<(float Offset, Rgba8 Color)> period = Padded(stops);
            int first = (int)MathF.Floor(tmin), last = (int)MathF.Ceiling(tmax);
            if (last - first > MaxPeriods)
            {
                return null;
            }

            for (int k = first; k < last; k++)
            {
                bool flip = g.Tile == TileMode.Mirror && (k & 1) != 0;
                for (int i = 0; i < period.Count; i++)
                {
                    (float o, Rgba8 c) = period[flip ? period.Count - 1 - i : i];
                    line.Add((k + (flip ? 1 - o : o), c));
                }
            }
        }

        List<Vector2> points = [];
        List<Rgba8> colors = [];
        List<int> triangles = [];
        Vector2 Point(float t, float u) => Vector2.Transform(g.Start + (t * d) + (u * perp), g.LocalMatrix);
        for (int i = 0; i + 1 < line.Count; i++)
        {
            (float ta, Rgba8 ca) = line[i];
            (float tb, Rgba8 cb) = line[i + 1];
            if (!(tb > ta))
            {
                continue;
            }

            int at = points.Count;
            points.AddRange([Point(ta, umin), Point(tb, umin), Point(tb, umax), Point(ta, umax)]);
            colors.AddRange([ca, cb, cb, ca]);
            triangles.AddRange([at, at + 1, at + 2, at, at + 2, at + 3]);
        }

        return points.Count == 0 ? null : new GdiGradient([.. points], [.. colors], [.. triangles]);
    }

    /// <summary>
    /// A centered radial gradient as a PathGradientBrush: a circle k times the radius around the center, k large enough that the
    /// circle holds the shape (GDI+ draws nothing outside a path gradient's boundary), and the stops as preset colors from the
    /// boundary (0) to the center (1). Repeating and mirrored gradients are unrolled into stops.
    /// </summary>
    private static bool TryRadial(RadialGradient g, Vector2[] corners, float opacity, out BrushPlan? plan, out string? reason)
    {
        plan = null;
        if (!Mixable(g, out bool gamma, out reason))
        {
            return false;
        }

        if (gamma)
        {
            reason = "a radial gradient mixed with GDI+'s gamma";
            return false;
        }

        if (!(g.Radius > 0) || g.Tile == TileMode.Decal || (g.Tile == TileMode.Clamp && !g.ExtendEnd))
        {
            reason = g.Radius > 0 ? "a gradient that is transparent past its ends" : "a gradient with no extent";
            return false;
        }

        float reach = corners.Max(p => Vector2.Distance(p, g.Center)) / g.Radius;
        if (!(reach < MaxPeriods))
        {
            reason = "a gradient far smaller than its shape";
            return false;
        }

        int k = Math.Max(1, (int)MathF.Ceiling(reach * 1.01f));
        List<(float Offset, Rgba8 Color)> stops = Stops(g, opacity);
        List<(float Radius, Rgba8 Color)> rings = [];
        if (g.Tile == TileMode.Clamp)
        {
            rings.AddRange(Padded(stops));
            if (k > 1)
            {
                rings.Add((k, stops[^1].Color));
            }
        }
        else
        {
            List<(float Offset, Rgba8 Color)> period = Padded(stops);
            for (int j = 0; j < k; j++)
            {
                bool flip = g.Tile == TileMode.Mirror && (j & 1) != 0;
                for (int i = 0; i < period.Count; i++)
                {
                    (float o, Rgba8 c) = period[flip ? period.Count - 1 - i : i];
                    rings.Add((j + (flip ? 1 - o : o), c));
                }
            }
        }

        // Positions run from the boundary in; the boundary is the circle of radius k.
        List<(float Position, Rgba8 Color)> presets = [];
        for (int i = rings.Count - 1; i >= 0; i--)
        {
            (float r, Rgba8 c) = rings[i];
            float p = 1 - (r / k);
            if (presets.Count == 0 || presets[^1] != (p, c))
            {
                presets.Add((p, c));
            }
        }

        presets = Padded(presets);
        float radius = g.Radius * k;
        byte[] boundary = GdiPlusData.WritePath(Path.Oval(new RectF(g.Center.X - radius, g.Center.Y - radius, radius * 2, radius * 2)));
        RecordBuffer b = new(boundary.Length + 64 + (presets.Count * 8));
        b.U32(GdiPlusData.Version).I32(3).U32(0x01 | 0x02 | 0x04).I32(GdiPlusBrushes.WrapTile); // PathGradient: path, transform, preset colors
        b.U32(Argb(presets[^1].Color)).F32(g.Center.X).F32(g.Center.Y);
        b.I32(1).U32(Argb(presets[0].Color)); // one surrounding color
        b.I32(boundary.Length).Bytes(boundary);
        b.Matrix(g.LocalMatrix);
        Blend(b, presets);
        plan = new BrushPlan(b.ToArray(), 0, null);
        return true;
    }

    /// <summary>
    /// An image as a TextureBrush: the image (its alpha times the paint's opacity) under the paint's matrix, with GDI+'s wrap
    /// modes for repeat, mirror and decal. Clamped edges have no GDI+ form.
    /// </summary>
    private bool TryTexture(Raster image, TileMode x, TileMode y, Matrix3x2 transform, int interpolation, float opacity, out BrushPlan? plan, out string? reason)
    {
        plan = null;
        reason = null;
        int wrap = (x, y) switch
        {
            (TileMode.Repeat, TileMode.Repeat) => GdiPlusBrushes.WrapTile,
            (TileMode.Mirror, TileMode.Repeat) => GdiPlusBrushes.WrapTileFlipX,
            (TileMode.Repeat, TileMode.Mirror) => GdiPlusBrushes.WrapTileFlipY,
            (TileMode.Mirror, TileMode.Mirror) => GdiPlusBrushes.WrapTileFlipXY,
            (TileMode.Decal, TileMode.Decal) => GdiPlusBrushes.WrapClamp,
            _ => -1,
        };
        if (wrap < 0)
        {
            reason = "an image paint whose edges are clamped or mixed";
            return false;
        }

        byte[] bitmap = BitmapObject(image, opacity);
        RecordBuffer b = new(bitmap.Length + 40);
        b.U32(GdiPlusData.Version).I32(2).U32(0x02).I32(wrap).Matrix(transform).Bytes(bitmap); // TextureBrush with a transform
        plan = new BrushPlan(b.ToArray(), interpolation, null);
        return true;
    }

    /// <summary>
    /// A picture paint's tile drawn by Egami as a texture at the device scale and sampled bilinearly, as Egami's raster backend
    /// draws picture paints, so a hatch or pixel pattern looks the same in the file.
    /// </summary>
    private bool TryPicture(PicturePaint paint, float opacity, out BrushPlan? plan, out string? reason)
    {
        Matrix3x2 m = paint.LocalMatrix * Matrix;
        RectF tile = paint.Tile;
        float sx = new Vector2(m.M11, m.M12).Length(), sy = new Vector2(m.M21, m.M22).Length();
        int w = (int)Math.Clamp(Math.Ceiling(tile.Width * (double)sx), 1, 4096), h = (int)Math.Clamp(Math.Ceiling(tile.Height * (double)sy), 1, 4096);
        using Raster<Rgba8> image = Raster.Create<Rgba8>(w, h);
        using (RasterCanvas<Rgba8> canvas = image.CreateCanvas())
        {
            canvas.Scale(w / tile.Width, h / tile.Height);
            canvas.Translate(-tile.X, -tile.Y);
            paint.Picture.Playback(canvas);
        }

        Matrix3x2 transform = Matrix3x2.CreateScale(tile.Width / w, tile.Height / h) * Matrix3x2.CreateTranslation(tile.X, tile.Y) * paint.LocalMatrix;
        return TryTexture(image, paint.TileX, paint.TileY, transform, 3, opacity, out plan, out reason);
    }

    /// <summary>
    /// The EmfPlusImage of an image with its alpha times an opacity, written once per image and opacity: a PNG, as
    /// <see cref="ImageObject"/>.
    /// </summary>
    private byte[] BitmapObject(Raster image, float opacity)
    {
        if (!_textures.TryGetValue((image, opacity), out byte[]? bitmap))
        {
            using Raster<Rgba8> rgba = image.As<Rgba8>();
            if (opacity < 1)
            {
                for (int row = 0; row < rgba.Height; row++)
                {
                    foreach (ref Rgba8 p in rgba.Row(row))
                    {
                        p = new Rgba8(p.R, p.G, p.B, (byte)MathF.Round(p.A * opacity));
                    }
                }
            }

            bitmap = ImageObject(rgba);
            _textures[(image, opacity)] = bitmap;
        }

        return bitmap;
    }

    /// <summary>
    /// GDI+'s interpolation mode for an Egami filter: nearest neighbor (5), bilinear (3), GDI+'s own filters as themselves (bicubic
    /// 4, high-quality bilinear 6), else high-quality bicubic (7).
    /// </summary>
    private static int Interpolation(ResampleFilter filter) => filter switch
    {
        ResampleFilter.Nearest => 5,
        ResampleFilter.Bilinear or ResampleFilter.GdiPlusBilinear => 3,
        ResampleFilter.GdiPlusBicubic => 4,
        ResampleFilter.GdiPlusHighQualityBilinear => 6,
        _ => 7,
    };

    /// <summary>An EmfPlusImage of a bitmap: a PNG (compressed EmfPlusBitmap, PixelFormat32bppARGB).</summary>
    private static byte[] ImageObject(Raster<Rgba8> rgba)
    {
        using MemoryStream png = new();
        rgba.Save(png, PngFormat.Info, new PngWriterOptions { FirstFrameOnly = true });
        RecordBuffer o = new((int)png.Length + 32);
        o.U32(GdiPlusData.Version).U32(1); // EmfPlusImage: ImageDataTypeBitmap
        o.I32(rgba.Width).I32(rgba.Height).I32(0).U32(0x0026200A).U32(1); // EmfPlusBitmap: PixelFormat32bppARGB, compressed
        o.Bytes(png.GetBuffer().AsSpan(0, (int)png.Length)).Align();
        return o.ToArray();
    }

    // ---- Stops ----

    /// <summary>Whether EMF+ mixes a gradient's stops as Egami does: in encoded sRGB (linear light gets extra stops) or with GDI+'s gamma.</summary>
    private static bool Mixable(Gradient g, out bool gamma, out string? reason)
    {
        gamma = g.Interpolation.Space == InterpolationSpace.GdiPlusGamma;
        reason = g.Interpolation.Space is InterpolationSpace.Encoded or InterpolationSpace.LinearSrgb or InterpolationSpace.GdiPlusGamma
            ? null
            : $"a gradient mixed in {g.Interpolation.Space}";
        return reason is null;
    }

    /// <summary>
    /// A gradient's stops as 8-bit colors with the paint's opacity: offsets clamped to 0 to 1 and in order, and between two stops
    /// whose mix EMF+ would make differently (in linear light, or with different alphas, where players disagree on
    /// premultiplying) seven more stops mixed as Egami mixes them, as Egami.Svg writes gradients.
    /// </summary>
    private static List<(float Offset, Rgba8 Color)> Stops(Gradient g, float opacity)
    {
        const int Steps = 8;
        bool linear = g.Interpolation.Space == InterpolationSpace.LinearSrgb;
        IReadOnlyList<GradientStop> stops = g.Stops;
        List<(float, Rgba8)> result = [];
        float previous = 0;
        for (int i = 0; i < stops.Count; i++)
        {
            float offset = MathF.Max(previous, Math.Clamp(stops[i].Offset, 0, 1));
            RgbaF32 color = stops[i].Color;
            if (i > 0 && offset > previous && (linear || stops[i - 1].Color.A != color.A))
            {
                for (int k = 1; k < Steps; k++)
                {
                    float t = k / (float)Steps;
                    result.Add((previous + ((offset - previous) * t), Color8(Mix(stops[i - 1].Color, color, t, linear, g.Premultiplied), opacity)));
                }
            }

            result.Add((offset, Color8(color, opacity)));
            previous = offset;
        }

        return result;
    }

    private static RgbaF32 Mix(RgbaF32 a, RgbaF32 b, float t, bool linear, bool premultiplied)
    {
        float aa = Math.Clamp(a.A, 0, 1), ba = Math.Clamp(b.A, 0, 1);
        float alpha = aa + ((ba - aa) * t);
        float wa = premultiplied ? aa : 1, wb = premultiplied ? ba : 1;
        float Channel(float x, float y)
        {
            float lx = linear ? ToLinear(x) : x, ly = linear ? ToLinear(y) : y;
            float v = (lx * wa) + (((ly * wb) - (lx * wa)) * t);
            if (premultiplied)
            {
                v = alpha > 0 ? v / alpha : 0;
            }

            return linear ? ToEncoded(v) : v;
        }

        return new RgbaF32(Channel(a.R, b.R), Channel(a.G, b.G), Channel(a.B, b.B), alpha);
    }

    private static float ToLinear(float v)
    {
        v = Math.Clamp(v, 0, 1);
        return v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
    }

    private static float ToEncoded(float v)
    {
        v = Math.Clamp(v, 0, 1);
        return v <= 0.0031308f ? v * 12.92f : (1.055f * MathF.Pow(v, 1 / 2.4f)) - 0.055f;
    }

    /// <summary>Stops with the first color at 0 and the last at 1 when they do not reach the ends, as GDI+ requires of preset colors.</summary>
    private static List<(float, Rgba8)> Padded(List<(float Offset, Rgba8 Color)> stops)
    {
        List<(float, Rgba8)> result = [];
        if (stops[0].Offset > 0)
        {
            result.Add((0, stops[0].Color));
        }

        result.AddRange(stops);
        if (stops[^1].Offset < 1)
        {
            result.Add((1, stops[^1].Color));
        }

        return result;
    }

    /// <summary>EmfPlusBlendColors: a count, the positions, then the ARGB colors.</summary>
    private static void Blend(RecordBuffer b, List<(float Position, Rgba8 Color)> presets)
    {
        b.I32(presets.Count);
        foreach ((float p, _) in presets)
        {
            b.F32(Math.Clamp(p, 0, 1));
        }

        foreach ((_, Rgba8 c) in presets)
        {
            b.U32(Argb(c));
        }
    }

    private static Vector2[] Corners(RectF r) => [new(r.Left, r.Top), new(r.Right, r.Top), new(r.Right, r.Bottom), new(r.Left, r.Bottom)];

    // ---- GDI ----

    /// <summary>EMR_GRADIENTFILL of Gouraud triangles, clipped to a fill between EMR_SAVEDC and EMR_RESTOREDC.</summary>
    private void WriteGdiGradient(Path fill, Matrix3x2 m, GdiGradient gradient)
    {
        GdiSave();
        try
        {
            if (!WriteGdiPath(fill, m))
            {
                return;
            }

            SetFillMode(fill.FillRule);
            _w.Gdi(67, _w.Data().I32(1).Span); // EMR_SELECTCLIPPATH RGN_AND
            RecordBuffer b = new(32 + (gradient.Points.Length * 16) + (gradient.Triangles.Length * 4));
            b.Bytes(Bounds16(MapRect(fill.Bounds, m))).I32(gradient.Points.Length).I32(gradient.Triangles.Length / 3).U32(2); // GRADIENT_FILL_TRIANGLE
            for (int i = 0; i < gradient.Points.Length; i++)
            {
                Rgba8 c = gradient.Colors[i];
                b.I32(Fix(gradient.Points[i].X)).I32(Fix(gradient.Points[i].Y)).U16(c.R << 8).U16(c.G << 8).U16(c.B << 8).U16(0xFF00);
            }

            foreach (int index in gradient.Triangles)
            {
                b.I32(index);
            }

            _w.Gdi(118, b.Span); // EMR_GRADIENTFILL
        }
        finally
        {
            GdiRestore();
        }
    }
}
