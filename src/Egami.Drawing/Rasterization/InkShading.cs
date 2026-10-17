using System.Buffers;
using System.Numerics;

using Egami.Colors;
using Egami.Drawing.Shading;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing.Rasterization;

/// <summary>
/// A paint that draws an <see cref="InkRaster"/>'s plates, mapped by name onto an ink canvas's planes; its <see cref="Paint.LocalMatrix"/>
/// takes image pixels to local coordinates. Only an ink canvas draws it.
/// </summary>
internal sealed record class InkImagePaint : Paint
{
    internal InkImagePaint(InkRaster image, SamplingOptions sampling)
    {
        Image = image;
        Sampling = sampling;
    }

    internal InkRaster Image { get; }

    internal SamplingOptions Sampling { get; }

    internal override Shader? CreateShader(Matrix3x2 toDevice, ColorSpace colorSpace) => null;

    /// <summary>Gets the device-to-image transform, or false when the mapping cannot be inverted.</summary>
    internal bool TryDeviceToImage(Matrix3x2 toDevice, out Matrix3x2 inverse) => TryInverse(toDevice, out inverse);
}

/// <summary>A gradient whose stops carry inks: t from the gradient's geometry, tints mixed linearly between stops per plane.</summary>
internal sealed class InkGradientSink : RasterSink
{
    private readonly InkTarget _target;
    private readonly GradientGeometry _geometry;
    private readonly GradientTiling _tiling;
    private readonly float[] _offsets;
    private readonly float[] _tints;     // per stop, every plane
    private readonly float[] _alphas;
    private readonly Paint _paint;
    private readonly bool[] _painted;

    internal InkGradientSink(InkTarget target, Gradient gradient, GradientGeometry geometry, Paint paint, ClipMask? mask, byte scale)
        : base(mask, scale)
    {
        _target = target;
        _geometry = geometry;
        _tiling = new GradientTiling(gradient.Tile, gradient.ExtendStart, gradient.ExtendEnd);
        _paint = paint;
        _painted = target.Resolver.Painted(paint);
        IReadOnlyList<GradientStop> stops = gradient.Stops;
        int planes = target.Inks;
        _offsets = new float[stops.Count];
        _tints = new float[stops.Count * planes];
        _alphas = new float[stops.Count];
        for (int i = 0; i < stops.Count; i++)
        {
            _offsets[i] = stops[i].Offset;
            _alphas[i] = target.Resolver.Resolve(stops[i].Ink is InkColor ink ? new SolidPaint(ink) : new SolidPaint(stops[i].Color), _tints.AsSpan(i * planes, planes));
        }
    }

    protected override void Write(int y, int x, ReadOnlySpan<byte> coverage)
    {
        int n = coverage.Length, planes = _target.Inks;
        Span<float> t = stackalloc float[n];
        Span<float> alpha = stackalloc float[n];
        float[] tints = new float[n * planes];
        _geometry.Parameters(y, x, t);
        for (int i = 0; i < n; i++)
        {
            float tiled = _tiling.Apply(t[i]);
            if (float.IsNaN(tiled))
            {
                alpha[i] = 0;
                continue;
            }

            // The last stop at or before t, and the next; equal offsets make a hard edge.
            int k = 0;
            while (k + 1 < _offsets.Length && _offsets[k + 1] <= tiled)
            {
                k++;
            }

            int next = Math.Min(k + 1, _offsets.Length - 1);
            float span = _offsets[next] - _offsets[k];
            float f = tiled <= _offsets[k] || span <= 0 ? 0 : Math.Min((tiled - _offsets[k]) / span, 1);
            for (int p = 0; p < planes; p++)
            {
                float a = _tints[(k * planes) + p], b = _tints[(next * planes) + p];
                tints[(i * planes) + p] = a + ((b - a) * f);
            }

            alpha[i] = _alphas[k] + ((_alphas[next] - _alphas[k]) * f);
        }

        InkCompositor.Composite(_target, y, x, n, tints, planes, alpha, coverage, _paint.Opacity, _paint.BlendMode, _painted, _target.KnockoutBackdrop as InkTarget);
    }
}

/// <summary>A mesh with vertex inks: each pixel's tints are its triangle's vertex tints weighted at the pixel's center.</summary>
internal sealed class InkMeshSink : RasterSink
{
    private readonly InkTarget _target;
    private readonly MeshShader _shader;
    private readonly float[] _tints;   // per vertex, every plane
    private readonly Paint _paint;
    private readonly bool[] _painted;

    internal InkMeshSink(InkTarget target, MeshShader shader, InkColor[] inks, Paint paint, ClipMask? mask, byte scale)
        : base(mask, scale)
    {
        _target = target;
        _shader = shader;
        _paint = paint;
        _painted = target.Resolver.Painted(paint);
        int planes = target.Inks;
        _tints = new float[inks.Length * planes];
        for (int v = 0; v < inks.Length; v++)
        {
            target.Resolver.Resolve(inks[v], _tints.AsSpan(v * planes, planes));
        }
    }

    protected override void Write(int y, int x, ReadOnlySpan<byte> coverage)
    {
        int n = coverage.Length, planes = _target.Inks;
        Span<float> alpha = stackalloc float[n];
        float[] tints = new float[n * planes];
        for (int i = 0; i < n; i++)
        {
            if (!_shader.TryWeights(y, x + i, out (int A, int B, int C) v, out (float W0, float W1, float W2) w))
            {
                alpha[i] = 0;
                continue;
            }

            for (int p = 0; p < planes; p++)
            {
                tints[(i * planes) + p] = (w.W0 * _tints[(v.A * planes) + p]) + (w.W1 * _tints[(v.B * planes) + p]) + (w.W2 * _tints[(v.C * planes) + p]);
            }

            alpha[i] = _shader.VertexAlpha;
        }

        InkCompositor.Composite(_target, y, x, n, tints, planes, alpha, coverage, _paint.Opacity, _paint.BlendMode, _painted, _target.KnockoutBackdrop as InkTarget);
    }
}

/// <summary>
/// An ink image's plates sampled at each pixel's center (nearest, or bilinear on premultiplied values with edges clamped), mapped
/// by name onto the target's planes; plates the target lacks print through their colorant's appearance into the process inks.
/// </summary>
internal sealed class InkImageSink : RasterSink
{
    private readonly InkTarget _target;
    private readonly InkRaster _image;
    private readonly Matrix3x2 _inverse;
    private readonly bool _nearest;
    private readonly int[] _planeOf;          // source plate to target plane, −1 for none
    private readonly float[][] _missing;      // for a plate with no plane: its full-tint process tints, else empty
    private readonly Paint _paint;
    private readonly bool[] _painted;

    internal InkImageSink(InkTarget target, InkImagePaint paint, Matrix3x2 inverse, ClipMask? mask, byte scale)
        : base(mask, scale)
    {
        _target = target;
        _image = paint.Image;
        _inverse = inverse;
        _nearest = paint.Sampling.Filter == ResampleFilter.Nearest;
        _paint = paint;
        _painted = target.Resolver.Painted(paint);
        int plates = _image.Plates.Count;
        _planeOf = new int[plates];
        _missing = new float[plates][];
        for (int i = 0; i < plates; i++)
        {
            Colorant colorant = _image.Plates[i].Colorant!;
            _planeOf[i] = target.Resolver.PlaneOf(colorant.Name);
            if (_planeOf[i] < 0)
            {
                _missing[i] = new float[target.Inks];
                target.Resolver.Resolve(InkColor.Spot(colorant, 1), _missing[i]);
            }
            else
            {
                _missing[i] = [];
            }
        }

        // Source-over of 8-bit plates that all have a plane, every plane painted, outside a knockout group: sampled plate by plate
        // and composited premultiplied, plane by plane.
        _fast = paint.BlendMode == BlendMode.SourceOver && !target.Wide && _image.Depth == InkDepth.Unorm8 && target.KnockoutBackdrop is null
            && Array.TrueForAll(_planeOf, p => p >= 0) && _planeOf.Distinct().Count() == plates && Array.TrueForAll(_painted, p => p);
    }

    private readonly bool _fast;

    protected override void Write(int y, int x, ReadOnlySpan<byte> coverage)
    {
        if (_fast)
        {
            FastWrite(y, x, coverage);
            return;
        }

        int n = coverage.Length, planes = _target.Inks, plates = _planeOf.Length;
        Span<float> alpha = stackalloc float[n];
        float[] tints = new float[n * planes];
        Span<float> sample = stackalloc float[plates + 1];
        for (int i = 0; i < n; i++)
        {
            Vector2 at = Vector2.Transform(new Vector2(x + i + 0.5f, y + 0.5f), _inverse);
            Sample(at, sample);
            float a = sample[plates];
            alpha[i] = a;
            if (a <= 0)
            {
                continue;
            }

            Span<float> to = tints.AsSpan(i * planes, planes);
            for (int s = 0; s < plates; s++)
            {
                float tint = Math.Clamp(sample[s] / a, 0, 1);
                if (_planeOf[s] >= 0)
                {
                    to[_planeOf[s]] = tint;
                }
            }

            // Plates the target lacks: their appearance, scaled by tint, over the process inks in complement space.
            for (int s = 0; s < plates; s++)
            {
                if (_planeOf[s] < 0)
                {
                    float tint = Math.Clamp(sample[s] / a, 0, 1);
                    float[] process = _missing[s];
                    for (int p = 0; p < process.Length; p++)
                    {
                        to[p] = 1 - ((1 - to[p]) * (1 - (tint * process[p])));
                    }
                }
            }
        }

        InkCompositor.Composite(_target, y, x, n, tints, planes, alpha, coverage, _paint.Opacity, _paint.BlendMode, _painted, _target.KnockoutBackdrop as InkTarget);
    }

    private void FastWrite(int y, int x, ReadOnlySpan<byte> coverage)
    {
        int n = coverage.Length, w = _image.Width, h = _image.Height;
        int[] index = ArrayPool<int>.Shared.Rent(n * 4);
        float[] weights = ArrayPool<float>.Shared.Rent(n * 3);
        try
        {
            // Where each pixel samples: columns xa and xb of rows ya and yb (all equal for nearest), and the two fractions.
            for (int i = 0; i < n; i++)
            {
                Vector2 at = Vector2.Transform(new Vector2(x + i + 0.5f, y + 0.5f), _inverse);
                if (_nearest)
                {
                    int sx = Math.Clamp((int)MathF.Floor(at.X), 0, w - 1), sy = Math.Clamp((int)MathF.Floor(at.Y), 0, h - 1);
                    (index[4 * i], index[(4 * i) + 1], index[(4 * i) + 2], index[(4 * i) + 3]) = (sx, sx, sy, sy);
                    (weights[3 * i], weights[(3 * i) + 1]) = (0, 0);
                }
                else
                {
                    float fx = at.X - 0.5f, fy = at.Y - 0.5f;
                    int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
                    (index[4 * i], index[(4 * i) + 1]) = (Math.Clamp(x0, 0, w - 1), Math.Clamp(x0 + 1, 0, w - 1));
                    (index[(4 * i) + 2], index[(4 * i) + 3]) = (Math.Clamp(y0, 0, h - 1), Math.Clamp(y0 + 1, 0, h - 1));
                    (weights[3 * i], weights[(3 * i) + 1]) = (fx - x0, fy - y0);
                }
            }

            // The sampled alpha, then each plate's straight tint, composited with exactly InkCompositor's source-over:
            // s = alpha · opacity · coverage, tint · s + d · (1 − s).
            int plates = _planeOf.Length;
            float[] rented = ArrayPool<float>.Shared.Rent(n * 2);
            try
            {
                Span<float> alpha = rented.AsSpan(0, n), sample = rented.AsSpan(n, n);
                Sample(_image.Alpha, index, weights, n, alpha);
                float opacity = _paint.Opacity;
                for (int i = 0; i < n; i++)
                {
                    weights[(3 * i) + 2] = alpha[i] * opacity * (coverage[i] / 255f);
                }

                for (int s = 0; s <= plates; s++)
                {
                    if (s < plates)
                    {
                        Sample(_image.Plates[s], index, weights, n, sample);
                    }

                    Span<byte> row = _target.Row8(s < plates ? _planeOf[s] : _target.Inks, y, x, n);
                    for (int i = 0; i < n; i++)
                    {
                        float k = weights[(3 * i) + 2];
                        float tint = s == plates ? 1 : alpha[i] > 0 ? Math.Clamp(sample[i] / alpha[i], 0, 1) : 0;
                        row[i] = InkTarget.To8((tint * k) + (row[i] / 255f * (1 - k)));
                    }
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(rented);
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(index);
            ArrayPool<float>.Shared.Return(weights);
        }
    }

    // A plate's bilinear samples, 0 to 1, as Sample computes them; a row is fetched only when the pixel's rows change.
    private static void Sample(InkPlate plate, int[] index, float[] weights, int n, Span<float> values)
    {
        int rowA = -1, rowB = -1;
        ReadOnlySpan<byte> a = default, b = default;
        for (int i = 0; i < n; i++)
        {
            int ya = index[(4 * i) + 2], yb = index[(4 * i) + 3];
            if (ya != rowA)
            {
                rowA = ya;
                a = plate.Row8(ya);
            }

            if (yb != rowB)
            {
                rowB = yb;
                b = plate.Row8(yb);
            }

            float tx = weights[3 * i], ty = weights[(3 * i) + 1];
            int xa = index[4 * i], xb = index[(4 * i) + 1];
            float aa = a[xa] / 255f, ab = a[xb] / 255f, ba = b[xa] / 255f, bb = b[xb] / 255f;
            float top = aa + ((ab - aa) * tx), bottom = ba + ((bb - ba) * tx);
            values[i] = top + ((bottom - top) * ty);
        }
    }

    // Premultiplied plates and alpha at an image position, in pixel units.
    private void Sample(Vector2 at, Span<float> values)
    {
        int w = _image.Width, h = _image.Height;
        if (_nearest)
        {
            int sx = Math.Clamp((int)MathF.Floor(at.X), 0, w - 1), sy = Math.Clamp((int)MathF.Floor(at.Y), 0, h - 1);
            for (int p = 0; p < values.Length; p++)
            {
                values[p] = Read(p, sx, sy);
            }

            return;
        }

        float fx = at.X - 0.5f, fy = at.Y - 0.5f;
        int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
        float tx = fx - x0, ty = fy - y0;
        int xa = Math.Clamp(x0, 0, w - 1), xb = Math.Clamp(x0 + 1, 0, w - 1), ya = Math.Clamp(y0, 0, h - 1), yb = Math.Clamp(y0 + 1, 0, h - 1);
        for (int p = 0; p < values.Length; p++)
        {
            float top = Read(p, xa, ya) + ((Read(p, xb, ya) - Read(p, xa, ya)) * tx);
            float bottom = Read(p, xa, yb) + ((Read(p, xb, yb) - Read(p, xa, yb)) * tx);
            values[p] = top + ((bottom - top) * ty);
        }
    }

    private float Read(int plane, int x, int y)
    {
        InkPlate plate = plane < _image.Plates.Count ? _image.Plates[plane] : _image.Alpha;
        return _image.Depth == InkDepth.Unorm8 ? plate.Row8(y)[x] / 255f : plate.Row16(y)[x] / 65535f;
    }
}

/// <summary>Images as plates: what <see cref="InkTarget.DrawImage"/> draws.</summary>
internal static class InkImages
{
    /// <summary>
    /// Converts a raster image to plates of the canvas's process inks. CMYK without a profile, or with the canvas's own, passes
    /// through unchanged; other CMYK, RGB and gray convert through their profile (untagged RGB as the canvas's RGB encoding).
    /// </summary>
    internal static InkRaster ToPlates(Raster image, InkResolver resolver, InkDepth depth, EgamiContext context)
    {
        InkSet set = InkSet.FromProfile(resolver.Profile);
        InkRaster plates = new(set, image.Width, image.Height, depth, context);
        try
        {
            PixelChannelOrder order = image.Layout.ChannelOrder;
            int process = set.ProcessCount;
            float[] values = new float[image.Width * process];
            if (order == PixelChannelOrder.Cmyk)
            {
                ColorProfile? tagged = image.Metadata.IccProfile.IsEmpty ? null : Tagged(image, ColorModel.Cmyk);
                bool passThrough = resolver.Profile.Model == ColorModel.Cmyk && (tagged is null || SameProfile(tagged, resolver.Profile));
                ColorTransform? convert = passThrough ? null : ColorTransform.Create(tagged ?? ColorProfile.Fogra39, resolver.Profile, resolver.Options);
                using Raster<Cmyk16> cmyk = image.As<Cmyk16>();
                float[] row = new float[image.Width * 4];
                for (int y = 0; y < image.Height; y++)
                {
                    ReadOnlySpan<Cmyk16> pixels = cmyk.Row(y);
                    for (int x = 0; x < pixels.Length; x++)
                    {
                        (row[4 * x], row[(4 * x) + 1], row[(4 * x) + 2], row[(4 * x) + 3]) = (pixels[x].C / 65535f, pixels[x].M / 65535f, pixels[x].Y / 65535f, pixels[x].K / 65535f);
                    }

                    if (convert is null)
                    {
                        row.CopyTo(values, 0);
                    }
                    else
                    {
                        convert.Apply(row, values);
                    }

                    Write(plates, y, values, process, null);
                }

                return plates;
            }

            bool gray = order is PixelChannelOrder.Gray or PixelChannelOrder.GrayAlpha;
            ColorProfile source = gray
                ? Tagged(image, ColorModel.Gray) ?? ColorProfile.GraySrgb
                : image.Metadata.IccProfile.IsEmpty && image.Metadata.CodePoints is null ? resolver.RgbSource : image.Metadata.GetColorProfile();
            ColorTransform transform = ColorTransform.Create(source, resolver.Profile, resolver.Options);
            using Raster<RgbaF32> rgba = image.As<RgbaF32>();
            int channels = gray ? 1 : 3;
            float[] input = new float[image.Width * channels];
            float[] alpha = new float[image.Width];
            for (int y = 0; y < image.Height; y++)
            {
                ReadOnlySpan<RgbaF32> pixels = rgba.Row(y);
                for (int x = 0; x < pixels.Length; x++)
                {
                    if (gray)
                    {
                        input[x] = pixels[x].R;
                    }
                    else
                    {
                        (input[3 * x], input[(3 * x) + 1], input[(3 * x) + 2]) = (pixels[x].R, pixels[x].G, pixels[x].B);
                    }

                    alpha[x] = Math.Clamp(pixels[x].A, 0, 1);
                }

                transform.Apply(input, values);

                Write(plates, y, values, process, alpha);
            }

            return plates;
        }
        catch
        {
            plates.Dispose();
            throw;
        }
    }

    // Straight tints and an alpha into premultiplied planes.
    private static void Write(InkRaster plates, int y, float[] values, int process, float[]? alpha)
    {
        int w = plates.Width;
        for (int p = 0; p <= process; p++)
        {
            InkPlate plate = p < process ? plates.Plates[p] : plates.Alpha;
            for (int x = 0; x < w; x++)
            {
                float a = alpha?[x] ?? 1;
                float v = p < process ? Math.Clamp(values[(x * process) + p], 0, 1) * a : a;
                if (plates.Depth == InkDepth.Unorm8)
                {
                    plate.Row8(y)[x] = InkTarget.To8(v);
                }
                else
                {
                    plate.Row16(y)[x] = InkTarget.To16(v);
                }
            }
        }
    }

    private static ColorProfile? Tagged(Raster image, ColorModel model)
    {
        if (image.Metadata.IccProfile.IsEmpty)
        {
            return null;
        }

        try
        {
            ColorProfile profile = ColorProfile.FromIcc(image.Metadata.IccProfile.Span);
            return profile.Model == model ? profile : null;
        }
        catch (EgamiException)
        {
            return null;
        }
    }

    private static bool SameProfile(ColorProfile a, ColorProfile b)
        => ReferenceEquals(a, b) || a.ToIcc().AsSpan().SequenceEqual(b.ToIcc());
}
