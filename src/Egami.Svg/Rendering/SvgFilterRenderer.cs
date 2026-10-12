using System.Numerics;

using Egami.Drawing;
using Egami.Operations;
using Egami.Pixels;
using Egami.Svg.Filters;

namespace Egami.Svg.Rendering;

/// <summary>
/// Runs a filter (stage D): the filtered content is drawn into a raster covering the filter region at the canvas's scale (times
/// FilterScale on a vector backend, question 15), the primitives run on it with Egami's operations, and the result is drawn back
/// as an image through the canvas's transform, so rotation and skew apply to the result as SVG says. Pixels are straight RGBA
/// floats; each result keeps the color space it was made in (linearRGB or sRGB, color-interpolation-filters) and is converted
/// only when a primitive needs the other.
/// </summary>
internal static class SvgFilterRenderer
{
    /// <summary>The largest filter raster, in pixels; a larger region is drawn at a lower resolution.</summary>
    private const long MaxPixels = 4096L * 4096;

    public static void Draw(Canvas canvas, SvgFilter filter, Action<Canvas> content, SvgRenderOptions options, int depth)
    {
        Matrix3x2 m = canvas.Matrix;
        float sx = MathF.Sqrt((m.M11 * m.M11) + (m.M12 * m.M12));
        float sy = MathF.Sqrt((m.M21 * m.M21) + (m.M22 * m.M22));
        if (!(sx > 0 && sy > 0 && float.IsFinite(sx) && float.IsFinite(sy)))
        {
            return;
        }

        if (!IsRaster(canvas))
        {
            sx *= options.FilterScale;
            sy *= options.FilterScale;
        }

        RectF region = filter.Region;
        double area = region.Width * (double)sx * region.Height * sy;
        if (area > MaxPixels)
        {
            float down = (float)Math.Sqrt(MaxPixels / area);
            sx *= down;
            sy *= down;
        }

        Frame frame = Frame.Of(region, sx, sy);
        if (frame.Width <= 0 || frame.Height <= 0)
        {
            return;
        }

        Raster<RgbaF32> current = Raster.Create<RgbaF32>(frame.Width, frame.Height);
        try
        {
            using (RasterCanvas<RgbaF32> c = current.CreateCanvas())
            {
                c.Translate(-frame.X, -frame.Y);
                c.Scale(sx, sy);
                content(c);
            }

            foreach (SvgFilterGraph graph in filter.Graphs)
            {
                Raster<RgbaF32> next = new Graph(graph, frame, current, options, depth).Run();
                current.Dispose();
                current = next;
            }

            int count = canvas.Save();
            canvas.Scale(1 / sx, 1 / sy);
            canvas.DrawImage(current, new RectF(frame.X, frame.Y, frame.Width, frame.Height));
            canvas.RestoreToCount(count);
        }
        finally
        {
            current.Dispose();
        }
    }

    /// <summary>Runs operations on a raster wrapped around an array and copies the result back (an operation may replace the pixels).</summary>
    private static void Operate(Raster<RgbaF32> raster, RgbaF32[] pixels, Action<RasterOps> build)
    {
        raster.Apply(build);
        raster.CopyPixelsTo(pixels);
    }

    private static bool IsRaster(Canvas canvas)
    {
        Type type = canvas.GetType();
        return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(RasterCanvas<>);
    }

    /// <summary>The filter raster's place: its pixel origin and size, and the user-to-pixel scale.</summary>
    private readonly record struct Frame(int X, int Y, int Width, int Height, float Sx, float Sy)
    {
        public static Frame Of(RectF region, float sx, float sy)
        {
            int x0 = (int)MathF.Floor(region.Left * sx), y0 = (int)MathF.Floor(region.Top * sy);
            int x1 = (int)MathF.Ceiling(region.Right * sx), y1 = (int)MathF.Ceiling(region.Bottom * sy);
            return new Frame(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0), sx, sy);
        }

        /// <summary>A user-space rectangle as pixels of the raster, clamped to it.</summary>
        public Rect Pixels(RectF r)
        {
            int x0 = Math.Clamp((int)MathF.Round((r.Left * Sx) - X), 0, Width), y0 = Math.Clamp((int)MathF.Round((r.Top * Sy) - Y), 0, Height);
            int x1 = Math.Clamp((int)MathF.Round((r.Right * Sx) - X), 0, Width), y1 = Math.Clamp((int)MathF.Round((r.Bottom * Sy) - Y), 0, Height);
            return new Rect(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
        }

        /// <summary>A user-space point in raster pixels.</summary>
        public Vector2 Pixel(Vector2 p) => new((p.X * Sx) - X, (p.Y * Sy) - Y);
    }

    /// <summary>One result: its pixels (straight RGBA floats, frame-sized), color space and subregion.</summary>
    private sealed record Image(RgbaF32[] Pixels, bool Linear, Rect Region);

    /// <summary>One filter of the list, run over the previous filter's result.</summary>
    private sealed class Graph(SvgFilterGraph graph, Frame frame, Raster<RgbaF32> source, SvgRenderOptions options, int depth)
    {
        private readonly List<Image> _results = [];
        private readonly Dictionary<(SvgFilterSource, bool), Image> _sources = [];
        private readonly Rect _region = frame.Pixels(graph.Region);

        private int W => frame.Width;

        private int H => frame.Height;

        public Raster<RgbaF32> Run()
        {
            foreach (SvgPrimitive primitive in graph.Primitives)
            {
                Rect sub = Intersect(frame.Pixels(primitive.Region), _region);
                RgbaF32[] pixels = Primitive(primitive, sub);
                Clear(pixels, sub);
                _results.Add(new Image(pixels, primitive.Linear, sub));
            }

            Image last = _results.Count > 0 ? _results[^1] : Source(SvgFilterSource.SourceGraphic, false);
            RgbaF32[] result = last.Linear ? Convert(last.Pixels, toLinear: false) : (RgbaF32[])last.Pixels.Clone();
            Clear(result, _region);
            return Raster.Wrap<RgbaF32>(result.AsMemory(), W, H);
        }

        private static Rect Intersect(Rect a, Rect b)
        {
            int x0 = Math.Max(a.X, b.X), y0 = Math.Max(a.Y, b.Y), x1 = Math.Min(a.X + a.Width, b.X + b.Width), y1 = Math.Min(a.Y + a.Height, b.Y + b.Height);
            return new Rect(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
        }

        /// <summary>Zeroes everything outside a rectangle: a result is transparent outside its subregion.</summary>
        private void Clear(RgbaF32[] pixels, Rect keep)
        {
            for (int y = 0; y < H; y++)
            {
                Span<RgbaF32> row = pixels.AsSpan(y * W, W);
                if (y < keep.Y || y >= keep.Y + keep.Height)
                {
                    row.Clear();
                    continue;
                }

                row[..Math.Min(keep.X, W)].Clear();
                if (keep.X + keep.Width < W)
                {
                    row[(keep.X + keep.Width)..].Clear();
                }
            }
        }

        private Image Source(SvgFilterSource kind, bool linear)
        {
            if (_sources.TryGetValue((kind, linear), out Image? cached))
            {
                return cached;
            }

            RgbaF32[] pixels = new RgbaF32[W * H];
            source.CopyPixelsTo(pixels);
            Clear(pixels, _region);
            if (kind == SvgFilterSource.SourceAlpha)
            {
                for (int i = 0; i < pixels.Length; i++)
                {
                    pixels[i] = new RgbaF32(0, 0, 0, pixels[i].A);
                }
            }
            else if (linear)
            {
                pixels = Convert(pixels, toLinear: true);
            }

            Image image = new(pixels, linear, _region);
            _sources[(kind, linear)] = image;
            return image;
        }

        /// <summary>An input in the color space a primitive works in.</summary>
        private Image Input(SvgFilterInput input, bool linear)
        {
            if (input.Source != SvgFilterSource.Result || input.Index < 0 || input.Index >= _results.Count)
            {
                return Source(input.Source == SvgFilterSource.SourceAlpha ? SvgFilterSource.SourceAlpha : SvgFilterSource.SourceGraphic, linear);
            }

            Image image = _results[input.Index];
            return image.Linear == linear ? image : image with { Pixels = Convert(image.Pixels, linear), Linear = linear };
        }

        private RgbaF32[] Copy(SvgFilterInput input, bool linear) => (RgbaF32[])Input(input, linear).Pixels.Clone();

        private Raster<RgbaF32> Wrap(RgbaF32[] pixels) => Raster.Wrap<RgbaF32>(pixels.AsMemory(), W, H);

        private RgbaF32[] Primitive(SvgPrimitive primitive, Rect sub)
        {
            bool linear = primitive.Linear;
            switch (primitive)
            {
                case SvgBlur blur:
                    return Blur(Copy(blur.In, linear), blur.SigmaX * frame.Sx, blur.SigmaY * frame.Sy);
                case SvgOffset offset:
                    return Offset(Copy(offset.In, linear), offset.Dx * frame.Sx, offset.Dy * frame.Sy);
                case SvgDropShadow shadow:
                {
                    RgbaF32[] input = Copy(shadow.In, linear);
                    RgbaF32 color = linear ? ToLinear(shadow.Color) : shadow.Color;
                    RgbaF32[] shade = new RgbaF32[input.Length];
                    for (int i = 0; i < input.Length; i++)
                    {
                        shade[i] = new RgbaF32(color.R, color.G, color.B, color.A * input[i].A);
                    }

                    shade = Offset(Blur(shade, shadow.SigmaX * frame.Sx, shadow.SigmaY * frame.Sy), shadow.Dx * frame.Sx, shadow.Dy * frame.Sy);
                    return Over(input, shade);
                }

                case SvgFlood flood:
                {
                    RgbaF32 color = linear ? ToLinear(flood.Color) : flood.Color;
                    RgbaF32[] pixels = new RgbaF32[W * H];
                    for (int y = sub.Y; y < sub.Y + sub.Height; y++)
                    {
                        pixels.AsSpan((y * W) + sub.X, sub.Width).Fill(color);
                    }

                    return pixels;
                }

                case SvgMerge merge:
                {
                    RgbaF32[] pixels = new RgbaF32[W * H];
                    foreach (SvgFilterInput input in merge.Inputs)
                    {
                        pixels = Over(Input(input, linear).Pixels, pixels);
                    }

                    return pixels;
                }

                case SvgBlend blend:
                    return Draw(Copy(blend.In2, linear), Input(blend.In, linear).Pixels, blend.Mode);
                case SvgComposite composite:
                {
                    RgbaF32[] destination = Copy(composite.In2, linear);
                    RgbaF32[] top = Input(composite.In, linear).Pixels;
                    if (composite.Operator == SvgCompositeOperator.Arithmetic)
                    {
                        using Raster<RgbaF32> d = Wrap(destination);
                        using Raster<RgbaF32> s = Wrap(top);
                        Operate(d, destination, ops => ops.DrawArithmetic(s, new Point(0, 0), composite.K.X, composite.K.Y, composite.K.Z, composite.K.W, clamp: true));
                        return destination;
                    }

                    BlendMode mode = composite.Operator switch
                    {
                        SvgCompositeOperator.In => BlendMode.SourceIn,
                        SvgCompositeOperator.Out => BlendMode.SourceOut,
                        SvgCompositeOperator.Atop => BlendMode.SourceAtop,
                        SvgCompositeOperator.Xor => BlendMode.Xor,
                        SvgCompositeOperator.Lighter => BlendMode.Plus,
                        _ => BlendMode.SourceOver,
                    };
                    return Draw(destination, top, mode);
                }

                case SvgColorMatrix matrix:
                {
                    RgbaF32[] pixels = Copy(matrix.In, linear);
                    using Raster<RgbaF32> r = Wrap(pixels);
                    Operate(r, pixels, ops => ops.ColorMatrix(new ColorMatrix(matrix.Matrix)));
                    return pixels;
                }

                case SvgComponentTransfer transfer:
                {
                    RgbaF32[] pixels = Copy(transfer.In, linear);
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        RgbaF32 p = pixels[i];
                        pixels[i] = new RgbaF32(
                            Math.Clamp(transfer.R.Apply(p.R), 0, 1), Math.Clamp(transfer.G.Apply(p.G), 0, 1), Math.Clamp(transfer.B.Apply(p.B), 0, 1), Math.Clamp(transfer.A.Apply(p.A), 0, 1));
                    }

                    return pixels;
                }

                case SvgConvolve convolve:
                    return Convolve(Copy(convolve.In, linear), convolve);
                case SvgMorphology morphology:
                {
                    RgbaF32[] pixels = Copy(morphology.In, linear);
                    int rx = (int)MathF.Round(morphology.RadiusX * frame.Sx), ry = (int)MathF.Round(morphology.RadiusY * frame.Sy);
                    if (rx <= 0 && ry <= 0)
                    {
                        return pixels;
                    }

                    using Raster<RgbaF32> r = Wrap(pixels);
                    Operate(r, pixels, ops => _ = morphology.Dilate ? ops.Dilate(Math.Max(rx, 0), Math.Max(ry, 0)) : ops.Erode(Math.Max(rx, 0), Math.Max(ry, 0)));
                    return pixels;
                }

                case SvgLighting lighting:
                    return Lighting(Copy(lighting.In, linear), lighting, linear);
                case SvgDisplacement displacement:
                {
                    RgbaF32[] pixels = Copy(displacement.In, linear);
                    RgbaF32[] map = (RgbaF32[])Input(displacement.In2, linear).Pixels.Clone();
                    using Raster<RgbaF32> r = Wrap(pixels);
                    using Raster<RgbaF32> m = Wrap(map);
                    Operate(r, pixels, ops => ops.Displace(m, displacement.Scale * frame.Sx, displacement.Scale * frame.Sy, displacement.X, displacement.Y));
                    return pixels;
                }

                case SvgTurbulence turbulence:
                {
                    RgbaF32[] pixels = new RgbaF32[W * H];
                    if (turbulence.FrequencyX < 0 || turbulence.FrequencyY < 0)
                    {
                        return pixels;
                    }

                    Size? stitch = turbulence.Stitch ? new Size(Math.Max(1, (int)MathF.Round(primitive.Region.Width)), Math.Max(1, (int)MathF.Round(primitive.Region.Height))) : null;
                    NoisePaint noise = turbulence.Fractal
                        ? NoisePaint.Fractal(turbulence.FrequencyX, turbulence.FrequencyY, Math.Clamp(turbulence.Octaves, 0, NoisePaint.MaxOctaves), turbulence.Seed, stitch)
                        : NoisePaint.Turbulence(turbulence.FrequencyX, turbulence.FrequencyY, Math.Clamp(turbulence.Octaves, 0, NoisePaint.MaxOctaves), turbulence.Seed, stitch);
                    using Raster<RgbaF32> r = Wrap(pixels);
                    using RasterCanvas<RgbaF32> c = r.CreateCanvas();
                    c.Translate(-frame.X, -frame.Y);
                    c.Scale(frame.Sx, frame.Sy);
                    c.FillRect(primitive.Region, noise with { BlendMode = BlendMode.Source });
                    return pixels;
                }

                case SvgTile tile:
                {
                    Image input = Input(tile.In, linear);
                    Rect from = input.Region;
                    RgbaF32[] pixels = new RgbaF32[W * H];
                    if (from.Width <= 0 || from.Height <= 0)
                    {
                        return pixels;
                    }

                    for (int y = sub.Y; y < sub.Y + sub.Height; y++)
                    {
                        int sy = from.Y + Mod(y - from.Y, from.Height);
                        for (int x = sub.X; x < sub.X + sub.Width; x++)
                        {
                            pixels[(y * W) + x] = input.Pixels[(sy * W) + from.X + Mod(x - from.X, from.Width)];
                        }
                    }

                    return pixels;
                }

                case SvgFeImage image:
                {
                    RgbaF32[] pixels = new RgbaF32[W * H];
                    if (image.Content is SvgNode node)
                    {
                        using Raster<RgbaF32> r = Wrap(pixels);
                        using RasterCanvas<RgbaF32> c = r.CreateCanvas();
                        c.Translate(-frame.X, -frame.Y);
                        c.Scale(frame.Sx, frame.Sy);
                        SvgRenderer.Draw(c, node, options, depth + 1);
                    }

                    return linear ? Convert(pixels, toLinear: true) : pixels;
                }

                default:
                    return new RgbaF32[W * H];
            }
        }

        private static int Mod(int a, int n) => ((a % n) + n) % n;

        /// <summary>A Gaussian blur with separate deviations in pixels, over transparency beyond the raster's edge.</summary>
        private RgbaF32[] Blur(RgbaF32[] pixels, float sigmaX, float sigmaY)
        {
            if (!(sigmaX > 0) && !(sigmaY > 0))
            {
                return pixels;
            }

            int padX = (int)MathF.Ceiling(3 * Math.Max(0, sigmaX)) + 1, padY = (int)MathF.Ceiling(3 * Math.Max(0, sigmaY)) + 1;
            int pw = W + (2 * padX), ph = H + (2 * padY);
            RgbaF32[] padded = new RgbaF32[pw * ph];
            for (int y = 0; y < H; y++)
            {
                pixels.AsSpan(y * W, W).CopyTo(padded.AsSpan(((y + padY) * pw) + padX, W));
            }

            using (Raster<RgbaF32> r = Raster.Wrap<RgbaF32>(padded.AsMemory(), pw, ph))
            {
                if (sigmaX == sigmaY)
                {
                    Operate(r, padded, ops => ops.Blur(sigmaX));
                }
                else
                {
                    Operate(r, padded, ops =>
                    {
                        if (sigmaX > 0)
                        {
                            ops.MotionBlur(Math.Min(127, MathF.Ceiling(3 * sigmaX)), sigmaX, 0, centered: true);
                        }

                        if (sigmaY > 0)
                        {
                            ops.MotionBlur(Math.Min(127, MathF.Ceiling(3 * sigmaY)), sigmaY, 90, centered: true);
                        }
                    });
                }
            }

            RgbaF32[] result = new RgbaF32[W * H];
            for (int y = 0; y < H; y++)
            {
                padded.AsSpan(((y + padY) * pw) + padX, W).CopyTo(result.AsSpan(y * W, W));
            }

            return result;
        }

        /// <summary>Moves the pixels by whole pixels (rounded), transparent where nothing moves in.</summary>
        private RgbaF32[] Offset(RgbaF32[] pixels, float dx, float dy)
        {
            int ox = (int)MathF.Round(dx), oy = (int)MathF.Round(dy);
            if (ox == 0 && oy == 0)
            {
                return pixels;
            }

            RgbaF32[] result = new RgbaF32[W * H];
            for (int y = 0; y < H; y++)
            {
                int from = y - oy;
                if ((uint)from >= (uint)H)
                {
                    continue;
                }

                int x0 = Math.Max(0, ox), x1 = Math.Min(W, W + ox);
                if (x1 > x0)
                {
                    pixels.AsSpan((from * W) + x0 - ox, x1 - x0).CopyTo(result.AsSpan((y * W) + x0, x1 - x0));
                }
            }

            return result;
        }

        /// <summary>Source-over of <paramref name="top"/> on <paramref name="bottom"/>, into a new array.</summary>
        private RgbaF32[] Over(RgbaF32[] top, RgbaF32[] bottom) => Draw((RgbaF32[])bottom.Clone(), top, BlendMode.SourceOver);

        /// <summary>Draws <paramref name="top"/> onto <paramref name="destination"/> with a blend or compositing mode, in place.</summary>
        private RgbaF32[] Draw(RgbaF32[] destination, RgbaF32[] top, BlendMode mode)
        {
            using Raster<RgbaF32> d = Wrap(destination);
            using Raster<RgbaF32> s = Wrap((RgbaF32[])top.Clone());
            Operate(d, destination, ops => ops.DrawImage(s, new Point(0, 0), mode));
            return destination;
        }

        /// <summary>feConvolveMatrix: SVG's kernel is applied rotated half a turn, about the target pixel.</summary>
        private RgbaF32[] Convolve(RgbaF32[] pixels, SvgConvolve convolve)
        {
            int ox = convolve.OrderX, oy = convolve.OrderY;
            float[] flipped = new float[ox * oy];
            for (int j = 0; j < oy; j++)
            {
                for (int i = 0; i < ox; i++)
                {
                    flipped[(j * ox) + i] = convolve.Kernel[((oy - 1 - j) * ox) + (ox - 1 - i)];
                }
            }

            ConvolutionKernel kernel = new(ox, oy, flipped, convolve.Divisor, convolve.Bias);
            using (Raster<RgbaF32> r = Wrap(pixels))
            {
                Operate(r, pixels, ops => ops.Convolve(kernel, convolve.Edge, convolve.PreserveAlpha));
            }

            // The kernel runs about its center; a target elsewhere moves the result.
            int shiftX = (ox - 1 - convolve.TargetX) - (ox / 2), shiftY = (oy - 1 - convolve.TargetY) - (oy / 2);
            return shiftX == 0 && shiftY == 0 ? pixels : Offset(pixels, -shiftX, -shiftY);
        }

        private RgbaF32[] Lighting(RgbaF32[] pixels, SvgLighting lighting, bool linear)
        {
            RgbaF32 color = linear ? ToLinear(lighting.Color) : lighting.Color;
            Rgba8 color8 = new((byte)Math.Clamp((int)MathF.Round(color.R * 255), 0, 255), (byte)Math.Clamp((int)MathF.Round(color.G * 255), 0, 255), (byte)Math.Clamp((int)MathF.Round(color.B * 255), 0, 255), 255);
            float scale = (frame.Sx + frame.Sy) / 2;
            Light light = lighting.Light switch
            {
                SvgDistantLight d => new DistantLight(d.Azimuth, d.Elevation) { Color = color8 },
                SvgPointLight p => new PointLight(Position(p.Position, scale)) { Color = color8 },
                SvgSpotLight s => new SpotLight(Position(s.Position, scale), Position(s.PointsAt, scale), s.Exponent, s.ConeAngle ?? 90) { Color = color8 },
                _ => new DistantLight(0, 0) { Color = color8 },
            };
            LightingModel model = lighting.Specular ? LightingModel.Specular(lighting.Constant, lighting.Exponent) : LightingModel.Diffuse(lighting.Constant);
            using Raster<RgbaF32> r = Wrap(pixels);
            Operate(r, pixels, ops => ops.Lighting(light, model, new LightingOptions { SurfaceScale = lighting.SurfaceScale, Height = HeightSource.Alpha }));
            return pixels;
        }

        private Vector3 Position(Vector3 user, float scale)
        {
            Vector2 p = frame.Pixel(new Vector2(user.X, user.Y));
            return new Vector3(p.X, p.Y, user.Z * scale);
        }
    }

    private static RgbaF32 ToLinear(RgbaF32 c) => new(Linear(c.R), Linear(c.G), Linear(c.B), c.A);

    private static float Linear(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    private static float Encoded(float c) => c <= 0.0031308f ? c * 12.92f : (1.055f * MathF.Pow(c, 1 / 2.4f)) - 0.055f;

    /// <summary>Converts the color channels (straight alpha) between sRGB and linearRGB, into a new array.</summary>
    private static RgbaF32[] Convert(RgbaF32[] pixels, bool toLinear)
    {
        RgbaF32[] result = new RgbaF32[pixels.Length];
        for (int i = 0; i < pixels.Length; i++)
        {
            RgbaF32 p = pixels[i];
            result[i] = p.A <= 0 ? default : toLinear
                ? new RgbaF32(Linear(p.R), Linear(p.G), Linear(p.B), p.A)
                : new RgbaF32(Encoded(p.R), Encoded(p.G), Encoded(p.B), p.A);
        }

        return result;
    }
}
