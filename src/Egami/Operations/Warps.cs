using System.Numerics;

namespace Egami.Operations;

/// <summary>
/// The built-in warps for <see cref="RasterOps.Warp"/>: lens correction, polar and depolar, arc, swirl, wave and implode with
/// ImageMagick's parameter meanings, so its documentation describes them and its results are their reference, and Skia's
/// magnifier. Positions are in pixels with pixel centers at +0.5.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Warps.cs" region="warps"/>
/// </example>
public static class Warps
{
    /// <summary>
    /// Corrects (or adds) barrel and pincushion lens distortion with the PanoTools model that ImageMagick's Barrel distort, Hugin
    /// and lens databases use: each output point at radius r from the center samples the source at radius
    /// r × (a·r³ + b·r² + c·r + d), with r normalized to half the image's smaller side. The size is kept.
    /// </summary>
    /// <param name="a">The cubic coefficient.</param>
    /// <param name="b">The quadratic coefficient.</param>
    /// <param name="c">The linear coefficient.</param>
    /// <param name="d">The constant term, or <see langword="null"/> (the default) for 1 − a − b − c, which keeps the scale at r = 1.</param>
    /// <param name="center">The center of the distortion, or <see langword="null"/> (the default) for the image's center.</param>
    /// <returns>The warp.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A coefficient or the center is not finite.</exception>
    public static IWarp Barrel(double a, double b, double c, double? d = null, Vector2? center = null)
    {
        return new BarrelWarp(Finite(a, nameof(a)), Finite(b, nameof(b)), Finite(c, nameof(c)), Finite(d ?? (1 - a - b - c), nameof(d)), Finite(center, nameof(center)), inverse: false);
    }

    /// <summary>
    /// The inverse lens model, ImageMagick's BarrelInverse distort: each output point at radius r samples the source at radius
    /// r / (a·r³ + b·r² + c·r + d), r normalized as for <see cref="Barrel"/>. The size is kept.
    /// </summary>
    /// <param name="a">The cubic coefficient.</param>
    /// <param name="b">The quadratic coefficient.</param>
    /// <param name="c">The linear coefficient.</param>
    /// <param name="d">The constant term, or <see langword="null"/> (the default) for 1 − a − b − c.</param>
    /// <param name="center">The center of the distortion, or <see langword="null"/> (the default) for the image's center.</param>
    /// <returns>The warp.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A coefficient or the center is not finite.</exception>
    public static IWarp BarrelInverse(double a, double b, double c, double? d = null, Vector2? center = null)
    {
        return new BarrelWarp(Finite(a, nameof(a)), Finite(b, nameof(b)), Finite(c, nameof(c)), Finite(d ?? (1 - a - b - c), nameof(d)), Finite(center, nameof(center)), inverse: true);
    }

    /// <summary>
    /// Wraps the image around a circle, ImageMagick's Polar distort: the source's columns become angles (0° pointing down, growing
    /// counter-clockwise on screen from <paramref name="fromDegrees"/> to <paramref name="toDegrees"/>) and its rows radii, the
    /// top row at <paramref name="minRadius"/> and the bottom at <paramref name="maxRadius"/>. The output is the square around the
    /// circle, as ImageMagick's +distort sizes it.
    /// </summary>
    /// <param name="maxRadius">The outer radius; 0 (the default) for the distance from the center to the nearest edge, −1 for the farthest corner.</param>
    /// <param name="minRadius">The inner radius, 0 or more and less than the outer.</param>
    /// <param name="center">The center, used for the special radii; <see langword="null"/> (the default) for the image's center.</param>
    /// <param name="fromDegrees">The angle of the source's left edge.</param>
    /// <param name="toDegrees">The angle of its right edge; <see langword="null"/> (the default) for a full circle.</param>
    /// <returns>The warp.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A radius or angle is not finite, or the radii are out of order.</exception>
    public static IWarp Polar(float maxRadius = 0, float minRadius = 0, Vector2? center = null, float fromDegrees = -180, float? toDegrees = null)
    {
        return new PolarWarp(PolarArgs.Check(maxRadius, minRadius, center, fromDegrees, toDegrees), depolar: false);
    }

    /// <summary>
    /// Unwraps a circle into a rectangle, ImageMagick's DePolar distort, the inverse of <see cref="Polar"/>: output columns are
    /// angles and rows radii from <paramref name="minRadius"/> to <paramref name="maxRadius"/> around the center. The output is
    /// as tall as the radii's difference and as wide as their mean circumference, rounded up.
    /// </summary>
    /// <param name="maxRadius">The outer radius; 0 (the default) for the distance from the center to the nearest edge, −1 for the farthest corner.</param>
    /// <param name="minRadius">The inner radius, 0 or more and less than the outer.</param>
    /// <param name="center">The center, or <see langword="null"/> (the default) for the image's center.</param>
    /// <param name="fromDegrees">The angle of the output's left edge.</param>
    /// <param name="toDegrees">The angle of its right edge; <see langword="null"/> (the default) for a full circle.</param>
    /// <returns>The warp.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A radius or angle is not finite, or the radii are out of order.</exception>
    public static IWarp DePolar(float maxRadius = 0, float minRadius = 0, Vector2? center = null, float fromDegrees = -180, float? toDegrees = null)
    {
        return new PolarWarp(PolarArgs.Check(maxRadius, minRadius, center, fromDegrees, toDegrees), depolar: true);
    }

    /// <summary>
    /// Bends the image into an arc, ImageMagick's Arc distort: the source's width spans <paramref name="degrees"/> of a circle
    /// centered above it, its top edge on the outer radius. The output is the arc's bounding box.
    /// </summary>
    /// <param name="degrees">The angle the width spans, more than 0; 360 makes a ring.</param>
    /// <param name="rotation">Turns the arc clockwise from the top, in degrees.</param>
    /// <param name="topRadius">The radius of the top edge, or <see langword="null"/> (the default) for the one that keeps the center line's length.</param>
    /// <param name="bottomRadius">The radius of the bottom edge, or <see langword="null"/> (the default) to scale the height with the top radius.</param>
    /// <returns>The warp.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The angle is not more than 0, or a value is not finite, or the top radius is not more than 0.</exception>
    public static IWarp Arc(float degrees, float rotation = 0, float? topRadius = null, float? bottomRadius = null)
    {
        if (!(degrees > 1e-6f) || !float.IsFinite(degrees))
        {
            throw new ArgumentOutOfRangeException(nameof(degrees), degrees, "The arc angle must be more than 0 and finite.");
        }

        Finite(rotation, nameof(rotation));
        if (topRadius is float top && (!(top > 1e-6f) || !float.IsFinite(top)))
        {
            throw new ArgumentOutOfRangeException(nameof(topRadius), top, "The top radius must be more than 0 and finite.");
        }

        if (bottomRadius is float bottom && !float.IsFinite(bottom))
        {
            throw new ArgumentOutOfRangeException(nameof(bottomRadius), bottom, "The bottom radius must be finite.");
        }

        if (bottomRadius is not null && topRadius is null)
        {
            throw new ArgumentOutOfRangeException(nameof(bottomRadius), bottomRadius, "A bottom radius needs a top radius.");
        }

        return new ArcWarp(degrees, rotation, topRadius, bottomRadius);
    }

    /// <summary>
    /// Swirls the image about its center, ImageMagick's -swirl: points turn by <paramref name="degrees"/> at the center, falling
    /// to 0 at the radius with the square of the distance. The size is kept; points outside the radius stay put.
    /// </summary>
    /// <param name="degrees">The turn at the center; positive turns clockwise.</param>
    /// <param name="center">The center, or <see langword="null"/> (the default) for ImageMagick's, the middle of the image.</param>
    /// <param name="radius">The radius, or <see langword="null"/> (the default) for half the image's larger side; the swirl is stretched to the image's aspect ratio.</param>
    /// <returns>The warp.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A value is not finite, or the radius is not more than 0.</exception>
    public static IWarp Swirl(float degrees, Vector2? center = null, float? radius = null)
    {
        return new RadialWarp(Finite(degrees, nameof(degrees)), Finite(center, nameof(center)), Radius(radius), implode: false);
    }

    /// <summary>
    /// Pulls the image toward its center, ImageMagick's -implode; a negative amount pushes it out (explodes). The size is kept.
    /// </summary>
    /// <param name="amount">The strength: 0 changes nothing, 1 is strong.</param>
    /// <returns>The warp.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="amount"/> is not finite.</exception>
    public static IWarp Implode(float amount)
    {
        return new RadialWarp(Finite(amount, nameof(amount)), null, null, implode: true);
    }

    /// <summary>
    /// Shifts each column up or down along a sine wave, ImageMagick's -wave: column x moves down by
    /// |amplitude| + amplitude × sin(2πx / wavelength). The image grows by twice the amplitude in height.
    /// </summary>
    /// <param name="amplitude">The wave's height, in pixels.</param>
    /// <param name="wavelength">The wave's length, in pixels, more than 0.</param>
    /// <returns>The warp.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A value is not finite, or the wavelength is not more than 0.</exception>
    public static IWarp Wave(float amplitude, float wavelength)
    {
        Finite(amplitude, nameof(amplitude));
        if (!(wavelength > 0) || !float.IsFinite(wavelength))
        {
            throw new ArgumentOutOfRangeException(nameof(wavelength), wavelength, "The wavelength must be more than 0 and finite.");
        }

        return new WaveWarp(amplitude, wavelength);
    }

    /// <summary>
    /// Magnifies the area <paramref name="lens"/> by <paramref name="zoom"/>, as Skia's magnifier filter does: the middle of the
    /// lens shows the zoomed image, and over the last <paramref name="inset"/> pixels to the lens's edge it blends back into the
    /// surroundings with rounded corners. Outside the lens nothing moves. The size is kept.
    /// </summary>
    /// <param name="lens">The lens, in pixels.</param>
    /// <param name="zoom">The magnification; 1 or less changes nothing, as in Skia.</param>
    /// <param name="inset">The width of the blending band, 0 or more; 0 gives a hard edge.</param>
    /// <returns>The warp.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The lens is empty, or the zoom or inset is out of range or not finite.</exception>
    public static IWarp Magnifier(Rect lens, float zoom, float inset)
    {
        if (lens.IsEmpty)
        {
            throw new ArgumentOutOfRangeException(nameof(lens), lens, "The lens is empty.");
        }

        if (!(zoom > 0) || !float.IsFinite(zoom))
        {
            throw new ArgumentOutOfRangeException(nameof(zoom), zoom, "The zoom must be more than 0 and finite.");
        }

        if (!(inset >= 0) || !float.IsFinite(inset))
        {
            throw new ArgumentOutOfRangeException(nameof(inset), inset, "The inset must be 0 or more and finite.");
        }

        return new MagnifierWarp(lens, zoom, inset);
    }

    private static double Finite(double value, string name)
    {
        return double.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(name, value, "The value must be finite.");
    }

    private static float Finite(float value, string name)
    {
        return float.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(name, value, "The value must be finite.");
    }

    private static Vector2? Finite(Vector2? value, string name)
    {
        return value is Vector2 v && (!float.IsFinite(v.X) || !float.IsFinite(v.Y)) ? throw new ArgumentOutOfRangeException(name, v, "The point must be finite.") : value;
    }

    private static float? Radius(float? radius)
    {
        return radius is float r && (!(r > 0) || !float.IsFinite(r)) ? throw new ArgumentOutOfRangeException(nameof(radius), r, "The radius must be more than 0 and finite.") : radius;
    }

    /// <summary>The checked arguments of Polar and DePolar.</summary>
    private readonly record struct PolarArgs(double MaxRadius, double MinRadius, Vector2? Center, double From, double To)
    {
        internal static PolarArgs Check(float maxRadius, float minRadius, Vector2? center, float fromDegrees, float? toDegrees)
        {
            Finite(maxRadius, nameof(maxRadius));
            Finite(minRadius, nameof(minRadius));
            Finite(center, nameof(center));
            Finite(fromDegrees, nameof(fromDegrees));
            if (toDegrees is float to)
            {
                Finite(to, nameof(toDegrees));
            }

            if (minRadius < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(minRadius), minRadius, "The inner radius must be 0 or more.");
            }

            if (maxRadius > 0 && maxRadius <= minRadius)
            {
                throw new ArgumentOutOfRangeException(nameof(maxRadius), maxRadius, "The outer radius must be more than the inner one.");
            }

            if (maxRadius < 0 && maxRadius != -1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxRadius), maxRadius, "The outer radius must be more than 0, or 0 or −1 for the special values.");
            }

            double from = fromDegrees * Math.PI / 180;
            double end = (toDegrees ?? fromDegrees) * Math.PI / 180;
            if (Math.Abs(end - from) < 1e-12)
            {
                end += 2 * Math.PI;
            }

            return new PolarArgs(maxRadius, minRadius, center, from, end);
        }

        /// <summary>Returns the center (the image's when none was given) and the outer radius with the special values resolved.</summary>
        internal (double X, double Y, double Radius) Resolve(Size input)
        {
            double cx = Center?.X ?? (input.Width / 2.0);
            double cy = Center?.Y ?? (input.Height / 2.0);
            double radius = MaxRadius;
            if (radius == 0)
            {
                radius = Math.Min(Math.Min(Math.Abs(cx), Math.Abs(cy)), Math.Min(Math.Abs(cx - input.Width), Math.Abs(cy - input.Height)));
            }
            else if (radius < 0)
            {
                double x0 = cx * cx, x1 = (cx - input.Width) * (cx - input.Width);
                double y0 = cy * cy, y1 = (cy - input.Height) * (cy - input.Height);
                radius = Math.Sqrt(Math.Max(x0, x1) + Math.Max(y0, y1));
            }

            if (!(radius - MinRadius > 1e-9))
            {
                throw new ArgumentException($"The outer radius {radius} is not more than the inner one, {MinRadius}, for a {input.Width}×{input.Height} image.");
            }

            return (cx, cy, radius);
        }
    }

    private sealed class BarrelWarp(double a, double b, double c, double d, Vector2? center, bool inverse) : IWarp
    {
        public Size OutputSize(Size input) => input;

        public void Map(int y, Size input, Span<Vector2> source)
        {
            // The coefficients work on radii normalized to half the smaller side; scaling them once works on pixels instead.
            double scale = 2.0 / Math.Min(input.Width, input.Height);
            double a3 = a * scale * scale * scale, b2 = b * scale * scale, c1 = c * scale;
            double cx = center?.X ?? (input.Width / 2.0), cy = center?.Y ?? (input.Height / 2.0);
            double dy = y + 0.5 - cy;
            for (int x = 0; x < source.Length; x++)
            {
                double dx = x + 0.5 - cx;
                double r = Math.Sqrt((dx * dx) + (dy * dy));
                double f = (((((a3 * r) + b2) * r) + c1) * r) + d;
                if (inverse)
                {
                    f = 1 / f;
                }

                source[x] = r > 1e-12 ? new Vector2((float)((dx * f) + cx), (float)((dy * f) + cy)) : new Vector2((float)(dx + cx), (float)(dy + cy));
            }
        }
    }

    private sealed class PolarWarp(PolarArgs args, bool depolar) : IWarp
    {
        public Size OutputSize(Size input)
        {
            (_, _, double radius) = args.Resolve(input);
            if (depolar)
            {
                return new Size(Dimension(Math.Ceiling((radius - args.MinRadius) * (args.To - args.From) * 0.5)), Dimension(Math.Ceiling(radius - args.MinRadius)));
            }

            return new Size(Dimension(Span(Origin(args.Center?.X), radius).Length), Dimension(Span(Origin(args.Center?.Y), radius).Length));
        }

        public void Map(int y, Size input, Span<Vector2> source)
        {
            (double cx, double cy, double radius) = args.Resolve(input);
            if (depolar)
            {
                Size output = OutputSize(input);
                double angleStep = (args.To - args.From) / output.Width;
                double radiusStep = (radius - args.MinRadius) / output.Height;
                double r = ((y + 0.5) * radiusStep) + args.MinRadius;
                for (int x = 0; x < source.Length; x++)
                {
                    double angle = ((x + 0.5) * angleStep) + args.From;
                    source[x] = new Vector2((float)((r * Math.Sin(angle)) + cx), (float)((r * Math.Cos(angle)) + cy));
                }

                return;
            }

            // The canvas is laid out around the center as ImageMagick's +distort lays it out; positions are relative to it.
            double ox = Origin(args.Center?.X), oy = Origin(args.Center?.Y);
            double left = Span(ox, radius).Start, top = Span(oy, radius).Start;
            double angleScale = input.Width / (args.To - args.From);
            double radiusScale = input.Height / (radius - args.MinRadius);
            double middle = (args.From + args.To) / 2;
            double dy = top + y + 0.5 - oy;
            for (int x = 0; x < source.Length; x++)
            {
                double dx = left + x + 0.5 - ox;
                double turn = (Math.Atan2(dx, dy) - middle) / (2 * Math.PI);
                turn -= Math.Floor(turn + 0.5);
                double sx = (turn * 2 * Math.PI * angleScale) + (input.Width / 2.0);
                double sy = (Math.Sqrt((dx * dx) + (dy * dy)) - args.MinRadius) * radiusScale;
                source[x] = new Vector2((float)sx, (float)sy);
            }
        }

        /// <summary>The coordinate the circle is laid out around: the given center, or 0 when none was given, as ImageMagick does.</summary>
        private static double Origin(float? center) => center ?? 0;

        /// <summary>The first canvas coordinate and the canvas length around <paramref name="center"/>, as ImageMagick's best fit rounds them.</summary>
        private static (double Start, double Length) Span(double center, double radius)
        {
            double start = Math.Floor(center - radius - 0.5);
            return (start, Math.Ceiling(center + radius - start + 0.5));
        }
    }

    private sealed class ArcWarp(float degrees, float rotation, float? topRadius, float? bottomRadius) : IWarp
    {
        public Size OutputSize(Size input)
        {
            Layout layout = For(input);
            return new Size(layout.Width, layout.Height);
        }

        public void Map(int y, Size input, Span<Vector2> source)
        {
            Layout l = For(input);
            double dy = l.Top + y + 0.5;
            for (int x = 0; x < source.Length; x++)
            {
                double dx = l.Left + x + 0.5;
                double turn = (Math.Atan2(dy, dx) - l.Center) / (2 * Math.PI);
                turn -= Math.Floor(turn + 0.5);
                double r = Math.Sqrt((dx * dx) + (dy * dy));
                source[x] = new Vector2((float)((turn * l.AngleScale) + l.Middle + 0.5), (float)((l.Outer - r) * l.RadiusScale));
            }
        }

        private Layout For(Size input)
        {
            double center = (-Math.PI / 2) + (rotation * Math.PI / 180);
            center /= 2 * Math.PI;
            center -= Math.Floor(center + 0.5);
            center *= 2 * Math.PI;
            double span = degrees * Math.PI / 180;
            double thickness = input.Height - 1;
            double outer = (input.Width / span) + (thickness / 2);
            if (topRadius is float top)
            {
                thickness = bottomRadius is float bottom ? top - bottom : thickness * top / outer;
                outer = top;
            }

            // The bounding box of the arc: its four corners and the points of its outer edge that point straight along an axis.
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            void Add(double x, double y)
            {
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }

            foreach (double a in (double[])[center - (span / 2), center + (span / 2)])
            {
                Add(outer * Math.Cos(a), outer * Math.Sin(a));
                Add((outer - thickness) * Math.Cos(a), (outer - thickness) * Math.Sin(a));
            }

            for (double a = Math.Ceiling((center - (span / 2)) / (Math.PI / 2)) * (Math.PI / 2); a < center + (span / 2); a += Math.PI / 2)
            {
                Add(outer * Math.Cos(a), outer * Math.Sin(a));
            }

            double left = Math.Floor(minX - 0.5), top0 = Math.Floor(minY - 0.5);
            int width = Dimension(Math.Ceiling(maxX - left + 0.5)), height = Dimension(Math.Ceiling(maxY - top0 + 0.5));
            return new Layout(left, top0, width, height, center, 2 * Math.PI * input.Width / span, outer, input.Height / thickness, (input.Width - 1.0) / 2);
        }

        private readonly record struct Layout(double Left, double Top, int Width, int Height, double Center, double AngleScale, double Outer, double RadiusScale, double Middle);
    }

    /// <summary>Swirl and implode: radial mappings inside an ellipse stretched to the image's aspect ratio, in pixel-index coordinates as ImageMagick computes them.</summary>
    private sealed class RadialWarp(float amount, Vector2? center, float? radius, bool implode) : IWarp
    {
        public Size OutputSize(Size input) => input;

        public void Map(int y, Size input, Span<Vector2> source)
        {
            // ImageMagick works on pixel indices, with the center at (width / 2, height / 2) in them.
            double cx = center is Vector2 c ? c.X - 0.5 : input.Width / 2.0;
            double cy = center is Vector2 c2 ? c2.Y - 0.5 : input.Height / 2.0;
            double r = radius ?? Math.Max(input.Width / 2.0, input.Height / 2.0);
            double sx = input.Width < input.Height ? input.Height / (double)input.Width : 1;
            double sy = input.Width > input.Height ? input.Width / (double)input.Height : 1;
            double turn = amount * Math.PI / 180;
            double dy = sy * (y - cy);
            for (int x = 0; x < source.Length; x++)
            {
                double dx = sx * (x - cx);
                double distance = (dx * dx) + (dy * dy);
                double u = x, v = y;
                if (distance < r * r)
                {
                    if (implode)
                    {
                        double f = distance > 0 ? Math.Pow(Math.Sin(Math.PI * Math.Sqrt(distance) / r / 2), -amount) : 1;
                        u = (f * dx / sx) + cx;
                        v = (f * dy / sy) + cy;
                    }
                    else
                    {
                        double factor = 1 - (Math.Sqrt(distance) / r);
                        double angle = turn * factor * factor;
                        double sin = Math.Sin(angle), cos = Math.Cos(angle);
                        u = (((cos * dx) - (sin * dy)) / sx) + cx;
                        v = (((sin * dx) + (cos * dy)) / sy) + cy;
                    }
                }

                source[x] = new Vector2((float)(u + 0.5), (float)(v + 0.5));
            }
        }
    }

    private sealed class WaveWarp(float amplitude, float wavelength) : IWarp
    {
        public Size OutputSize(Size input) => new(input.Width, Dimension(Math.Floor(input.Height + (2.0 * Math.Abs(amplitude)))));

        public void Map(int y, Size input, Span<Vector2> source)
        {
            for (int x = 0; x < source.Length; x++)
            {
                // ImageMagick keeps the shifts in single precision.
                float shift = (float)(Math.Abs(amplitude) + (amplitude * Math.Sin(2 * Math.PI * x / wavelength)));
                source[x] = new Vector2(x + 0.5f, (float)(y - (double)shift + 0.5));
            }
        }
    }

    private sealed class MagnifierWarp(Rect lens, float zoom, float inset) : IWarp
    {
        public Size OutputSize(Size input) => input;

        public void Map(int y, Size input, Span<Vector2> source)
        {
            double py = y + 0.5;
            if (zoom <= 1 || py < lens.Y || py > lens.Bottom)
            {
                Identity(y, source);
                return;
            }

            // The zoomed source rectangle shrinks the lens about its center (kept inside the image) by the zoom, which Skia caps so
            // that half a pixel at most fills the lens.
            double centerX = Math.Clamp(lens.X + (lens.Width / 2.0), 0, input.Width);
            double centerY = Math.Clamp(lens.Y + (lens.Height / 2.0), 0, input.Height);
            double invZoom = 1 / Math.Min(zoom, 2 * Math.Max(1, Math.Max(lens.Width, lens.Height)));
            double srcLeft = (lens.X * invZoom) + (centerX * (1 - invZoom));
            double srcTop = (lens.Y * invZoom) + (centerY * (1 - invZoom));
            double invInset = inset > 0 ? 1 / inset : double.PositiveInfinity;
            for (int x = 0; x < source.Length; x++)
            {
                double px = x + 0.5;
                if (px < lens.X || px > lens.Right)
                {
                    source[x] = new Vector2((float)px, (float)py);
                    continue;
                }

                double zx = srcLeft + ((px - lens.X) * invZoom);
                double zy = srcTop + ((py - lens.Y) * invZoom);
                double ex = Math.Min(px - lens.X, lens.Right - px) * invInset;
                double ey = Math.Min(py - lens.Y, lens.Bottom - py) * invInset;
                double weight = ex < 2 && ey < 2 ? 2 - Math.Sqrt(((2 - ex) * (2 - ex)) + ((2 - ey) * (2 - ey))) : Math.Min(ex, ey);
                weight = double.IsNaN(weight) ? 1 : Math.Clamp(weight, 0, 1);
                double t = weight * weight;
                source[x] = new Vector2((float)(px + ((zx - px) * t)), (float)(py + ((zy - py) * t)));
            }
        }

        private static void Identity(int y, Span<Vector2> source)
        {
            for (int x = 0; x < source.Length; x++)
            {
                source[x] = new Vector2(x + 0.5f, y + 0.5f);
            }
        }
    }

    private static int Dimension(double value)
    {
        if (!(value < int.MaxValue / 4))
        {
            throw new ArgumentException($"The warped image would be {value:0} pixels across.");
        }

        return Math.Max(1, (int)value);
    }
}
