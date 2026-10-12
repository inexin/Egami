using System.Numerics;
using System.Text;

using Egami.Drawing;
using Egami.Operations;
using Egami.Pixels;
using Egami.Svg.Output;

namespace Egami.Svg;

public sealed partial class SvgCanvas
{
    private void DrawShape(Path path, Stroke? stroke, Paint paint)
    {
        (bool skip, string? blend) = Blend(paint.BlendMode, paint);
        if (skip)
        {
            return;
        }

        RectF local = DrawnBounds(path, stroke);
        MaskBlur? blur = paint.Blur is MaskBlur b && b.Sigma > 0 ? b : null;
        string? reason = blur is MaskBlur mb && mb.Style != BlurStyle.Normal ? $"a {mb.Style} mask blur" : null;
        string? value = null;
        float opacity = 1;
        if (reason is null && !TryPaint(paint, local, out value, out opacity, out reason))
        {
            reason ??= paint.GetType().Name;
        }

        if (reason is not null)
        {
            Paint flat = blend is null ? paint : paint with { BlendMode = BlendMode.SourceOver };
            float outset = blur is MaskBlur blurred ? 3 * blurred.Sigma : 1;
            Fallback(local, outset, c =>
            {
                if (stroke is null)
                {
                    c.FillPath(path, flat);
                }
                else
                {
                    c.StrokePath(path, stroke, flat);
                }
            }, reason, blend, 1);
            return;
        }

        if (value is null)
        {
            return;
        }

        SvgAttrs a = new SvgAttrs().Add("d", path.ToSvg(_out.PathFormat)).Add("transform", Num.Transform(Matrix));
        if (stroke is null)
        {
            a.Add("fill", value).Add("fill-opacity", opacity < 1 ? SvgNumbers.Unit(opacity) : null)
                .Add("fill-rule", path.FillRule == FillRule.EvenOdd ? "evenodd" : null);
        }
        else
        {
            a.Add("fill", "none").Add("stroke", value).Add("stroke-opacity", opacity < 1 ? SvgNumbers.Unit(opacity) : null);
            AddStroke(a, stroke);
        }

        a.Add("shape-rendering", paint.Antialias ? null : "crispEdges");
        if (blur is MaskBlur sharp)
        {
            RectF device = MapRect(local, Matrix).Inflate(3 * sharp.Sigma + 1, 3 * sharp.Sigma + 1);
            string filter = _out.Define("f", "filter", new SvgAttrs()
                .Add("filterUnits", "userSpaceOnUse")
                .Add("x", Num.F(device.X)).Add("y", Num.F(device.Y)).Add("width", Num.F(device.Width)).Add("height", Num.F(device.Height)),
                Child(SvgOutput.DefDepth + 1, "feGaussianBlur", new SvgAttrs().Add("stdDeviation", Num.F(sharp.Sigma))));
            _body.Open("g", new SvgAttrs().Add("clip-path", ClipRef).Add("filter", $"url(#{filter})").Add("style", Style(BlendDeclaration(blend))));
            _body.Empty("path", a);
            _body.Close();
            return;
        }

        a.Add("clip-path", Matrix.IsIdentity ? ClipRef : null).Add("style", Style(BlendDeclaration(blend)));
        bool clipped = !Matrix.IsIdentity && OpenClip();
        _body.Empty("path", a);
        CloseClip(clipped);
    }

    private void AddStroke(SvgAttrs a, Stroke stroke)
    {
        if (stroke.IsHairline)
        {
            a.Add("stroke-width", "1").Add("vector-effect", "non-scaling-stroke");
        }
        else
        {
            a.Add("stroke-width", Num.F(stroke.Width));
        }

        a.Add("stroke-linecap", stroke.Cap switch { LineCap.Round => "round", LineCap.Square => "square", _ => null })
            .Add("stroke-linejoin", stroke.Join switch { LineJoin.Round => "round", LineJoin.Bevel => "bevel", _ => null })
            .Add("stroke-miterlimit", stroke.Join == LineJoin.Miter && stroke.MiterLimit != 4 ? Num.F(stroke.MiterLimit) : null);
        if (stroke.Dash is DashPattern dash && dash.Intervals.Count > 0)
        {
            a.Add("stroke-dasharray", string.Join(' ', dash.Intervals.Select(i => Num.F(i))))
                .Add("stroke-dashoffset", dash.Phase != 0 ? Num.F(dash.Phase) : null);
        }
    }

    /// <summary>The local bounds a fill or stroke of a path can reach.</summary>
    private RectF DrawnBounds(Path path, Stroke? stroke)
    {
        RectF bounds = path.Bounds;
        if (stroke is null)
        {
            return bounds;
        }

        float scale = MathF.Sqrt(MathF.Abs(Matrix.GetDeterminant()));
        float width = stroke.IsHairline ? (scale > 0 ? 1 / scale : 0) : stroke.Width;
        float reach = width / 2 * (stroke.Join == LineJoin.Miter ? MathF.Max(stroke.MiterLimit, 1.5f) : 1.5f);
        return bounds.Inflate(reach, reach);
    }

    /// <summary>
    /// The value of fill or stroke for a paint over an area (a color, or a url to a gradient or pattern in defs), and the
    /// opacity; <paramref name="value"/> is <see langword="null"/> when the paint draws nothing. Returns <see langword="false"/>
    /// with a reason when SVG cannot express the paint.
    /// </summary>
    private bool TryPaint(Paint paint, RectF localBounds, out string? value, out float opacity, out string? reason)
    {
        value = null;
        opacity = paint.Opacity;
        reason = null;
        if (paint is SolidPaint solid)
        {
            value = SvgNumbers.Color(solid.Color.R, solid.Color.G, solid.Color.B);
            opacity = Math.Clamp(solid.Color.A, 0, 1) * paint.Opacity;
            return true;
        }

        if (paint is SweepGradient)
        {
            reason = "a sweep gradient";
            return false;
        }

        if (paint is not (Gradient or ImagePaint or PicturePaint))
        {
            reason = paint switch
            {
                NoisePaint => "a noise paint",
                BlendPaint => "a blend paint",
                ColorMatrixPaint => "a color matrix paint",
                _ => "a custom paint",
            };
            return false;
        }

        if (!Matrix3x2.Invert(paint.LocalMatrix, out Matrix3x2 inverse))
        {
            return true;
        }

        return paint is Gradient gradient
            ? TryGradient(gradient, out value, ref opacity, out reason)
            : TryPattern(paint, MapRect(localBounds, inverse), out value, out reason);
    }

    private bool TryGradient(Gradient g, out string? value, ref float opacity, out string? reason)
    {
        value = null;
        reason = null;
        if (g.Interpolation.Space is not (InterpolationSpace.Encoded or InterpolationSpace.LinearSrgb))
        {
            reason = $"a gradient mixed in {g.Interpolation.Space}";
            return false;
        }

        if (g.Tile == TileMode.Decal || (g.Tile == TileMode.Clamp && !(g.ExtendStart && g.ExtendEnd)))
        {
            reason = "a gradient that is transparent past its ends";
            return false;
        }

        SvgAttrs a = new SvgAttrs().Add("gradientUnits", "userSpaceOnUse");
        string name;
        bool degenerate;
        switch (g)
        {
            case LinearGradient l:
                name = "linearGradient";
                degenerate = l.Start == l.End;
                a.Add("x1", Num.F(l.Start.X)).Add("y1", Num.F(l.Start.Y)).Add("x2", Num.F(l.End.X)).Add("y2", Num.F(l.End.Y));
                break;
            case RadialGradient r:
                name = "radialGradient";
                degenerate = r.Radius == 0;
                a.Add("cx", Num.F(r.Center.X)).Add("cy", Num.F(r.Center.Y)).Add("r", Num.F(r.Radius));
                break;
            case ConicalGradient c:
                name = "radialGradient";
                degenerate = false;
                float d = Vector2.Distance(c.Start, c.End);
                bool inside = c.EndRadius > 0 && d + c.StartRadius < c.EndRadius * 0.9999f;
                if (!inside || (c.StartRadius > 0 && c.Tile != TileMode.Clamp))
                {
                    reason = "a two-point conical gradient whose start circle is not inside its end circle";
                    return false;
                }

                a.Add("cx", Num.F(c.End.X)).Add("cy", Num.F(c.End.Y)).Add("r", Num.F(c.EndRadius));
                if (c.Start != c.End)
                {
                    a.Add("fx", Num.F(c.Start.X)).Add("fy", Num.F(c.Start.Y));
                }

                if (c.StartRadius > 0)
                {
                    a.Add("fr", Num.F(c.StartRadius));
                }

                break;
            default:
                reason = "a gradient";
                return false;
        }

        IReadOnlyList<GradientStop> stops = g.Stops;
        if (degenerate)
        {
            // As Egami draws a gradient with no extent: its last color when clamped.
            if (g.Tile != TileMode.Clamp)
            {
                reason = "a repeating gradient with no extent";
                return false;
            }

            RgbaF32 last = stops[^1].Color;
            value = SvgNumbers.Color(last.R, last.G, last.B);
            opacity *= Math.Clamp(last.A, 0, 1);
            return true;
        }

        a.Add("spreadMethod", g.Tile switch { TileMode.Repeat => "repeat", TileMode.Mirror => "reflect", _ => null })
            .Add("gradientTransform", Num.Transform(g.LocalMatrix));
        StringBuilder inner = new();
        foreach (GradientStop stop in Stops(stops, g.Interpolation.Space == InterpolationSpace.LinearSrgb, g.Premultiplied))
        {
            float offset = stop.Offset;
            inner.Append(Child(SvgOutput.DefDepth + 1, "stop", new SvgAttrs()
                .Add("offset", Num.F(offset))
                .Add("stop-color", SvgNumbers.Color(stop.Color.R, stop.Color.G, stop.Color.B))
                .Add("stop-opacity", stop.Color.A < 1 ? SvgNumbers.Unit(stop.Color.A) : null)));
        }

        value = $"url(#{_out.Define("g", name, a, inner.ToString())})";
        return true;
    }

    /// <summary>
    /// A gradient's stops as SVG mixes them the way Egami does: offsets clamped and in order, and between two stops whose mix SVG
    /// would make differently (in linear light, or with different alphas, where viewers disagree on premultiplying) seven more
    /// stops mixed as Egami mixes them, so every viewer draws the same gradient within a small error.
    /// </summary>
    private static List<GradientStop> Stops(IReadOnlyList<GradientStop> stops, bool linear, bool premultiplied)
    {
        const int Steps = 8;
        List<GradientStop> result = [];
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
                    result.Add(new GradientStop(previous + ((offset - previous) * t), Mix(stops[i - 1].Color, color, t, linear, premultiplied)));
                }
            }

            result.Add(new GradientStop(offset, color));
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

    /// <summary>An image or picture paint as a pattern: each axis repeats, mirrors, or (decal, or clamped inside) holds one copy.</summary>
    private bool TryPattern(Paint paint, RectF paintBounds, out string? value, out string? reason)
    {
        value = null;
        reason = null;
        Func<string> define;
        float w, h;
        TileMode tileX, tileY;
        bool nearest = false;
        if (paint is ImagePaint image)
        {
            define = () => ImageDefinition(image.Image);
            w = image.Image.Width;
            h = image.Image.Height;
            tileX = image.TileX;
            tileY = image.TileY;
            nearest = image.Sampling.Filter == ResampleFilter.Nearest;
        }
        else
        {
            PicturePaint picture = (PicturePaint)paint;
            RectF tile = picture.Tile;
            define = () =>
            {
                // The picture's tile at the origin, clipped to the tile (a nested svg would clip too, but not in every viewer).
                string pic = PictureDefinition(picture.Picture, MathF.Sqrt(MathF.Abs((picture.LocalMatrix * Matrix).GetDeterminant())));
                string crop = _out.Define("c", "clipPath", new SvgAttrs().Add("clipPathUnits", "userSpaceOnUse"), Child(SvgOutput.DefDepth + 1, "path", new SvgAttrs().Add("d", Path.Rect(tile).ToSvg(_out.PathFormat))));
                return _out.Define("t", "g", new SvgAttrs().Add("transform", Num.Transform(Matrix3x2.CreateTranslation(-tile.X, -tile.Y))), Child(SvgOutput.DefDepth + 1, "use", new SvgAttrs().Add("xlink:href", "#" + pic).Add("clip-path", $"url(#{crop})")));
            };
            w = tile.Width;
            h = tile.Height;
            tileX = picture.TileX;
            tileY = picture.TileY;
        }

        if (!Axis(tileX, w, paintBounds.Left, paintBounds.Right, out float x0, out float tw, out bool mirrorX)
            || !Axis(tileY, h, paintBounds.Top, paintBounds.Bottom, out float y0, out float th, out bool mirrorY))
        {
            reason = "an image or picture paint clamped past its edges";
            return false;
        }

        string content = define();
        StringBuilder inner = new();
        for (int ix = 0; ix < (mirrorX ? 2 : 1); ix++)
        {
            for (int iy = 0; iy < (mirrorY ? 2 : 1); iy++)
            {
                Matrix3x2 copy = new(ix == 1 ? -1 : 1, 0, 0, iy == 1 ? -1 : 1, (ix == 1 ? 2 * w : 0) - x0, (iy == 1 ? 2 * h : 0) - y0);
                inner.Append(Child(SvgOutput.DefDepth + 1, "use", new SvgAttrs().Add("xlink:href", "#" + content).Add("transform", Num.Transform(copy))));
            }
        }

        SvgAttrs a = new SvgAttrs()
            .Add("patternUnits", "userSpaceOnUse")
            .Add("width", Num.F(tw)).Add("height", Num.F(th))
            .Add("patternTransform", Num.Transform(Matrix3x2.CreateTranslation(x0, y0) * paint.LocalMatrix))
            .Add("image-rendering", nearest ? "optimizeSpeed" : null)
            .Add("style", nearest ? "image-rendering:pixelated" : null);
        value = $"url(#{_out.Define("p", "pattern", a, inner.ToString())})";
        return true;
    }

    /// <summary>One axis of a pattern tile: where it starts, how long it is, and whether it holds a mirrored second copy.</summary>
    private static bool Axis(TileMode mode, float size, float from, float to, out float start, out float length, out bool mirror)
    {
        start = 0;
        length = size;
        mirror = false;
        switch (mode)
        {
            case TileMode.Repeat:
                return true;
            case TileMode.Mirror:
                mirror = true;
                length = 2 * size;
                return true;
            case TileMode.Clamp when from < -0.001f || to > size + 0.001f:
                return false;
            default:
                // One copy in a tile larger than the area drawn, so it never repeats there.
                start = MathF.Floor(MathF.Min(0, from)) - 1;
                length = MathF.Ceiling(MathF.Max(size, to)) + 1 - start;
                return true;
        }
    }
}
