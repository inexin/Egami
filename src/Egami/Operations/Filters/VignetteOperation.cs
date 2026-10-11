using System.Buffers;
using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

using Egami.Operations.Resampling;
using Egami.Pixels;

namespace Egami.Operations.Filters;

/// <summary>
/// Vignette: a layer of one color whose alpha rises outside an ellipse fitted to the image, drawn source-over on premultiplied
/// encoded values. The mask is analytic: the signed distance to the ellipse in pixels (to first order, the ellipse function over
/// its gradient) through the profile of a Gaussian-blurred edge, the shape ImageMagick's -vignette makes by blurring a drawn ellipse.
/// Positions are the whole image's, so inside a scope it runs on the full plane and changes only the area.
/// </summary>
internal sealed class VignetteOperation : IRasterOperation, Scopes.IAreaStep
{
    /// <summary>The profile's table covers the distance from −<see cref="Span"/>σ to +<see cref="Span"/>σ.</summary>
    private const float Span = 4;

    private const int Steps = 1024;

    private const float Scale = Steps / (2 * Span);

    /// <summary>At the ellipse's center the gradient vanishes; there the mask is 1.</summary>
    private const float MinGradient = 1e-24f;

    /// <summary>1 − Φ(t) for t from −Span to Span: the coverage of a Gaussian-blurred edge, 1 inside and 0 outside.</summary>
    private static readonly float[] Profile = BuildProfile();

    private readonly float _radius;
    private readonly float _softness;
    private readonly Rgba8 _color;
    private readonly Vector2? _center;

    internal VignetteOperation(float radius, float softness, Rgba8 color, Vector2? center)
    {
        _radius = radius;
        _softness = softness;
        _color = color;
        _center = center;
    }

    public Size OutputSize(Size input) => input;

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Process(input, input.Source, new Rect(0, 0, input.Destination.Width, input.Destination.Height));
    }

    public void RunArea<TPixel>(OperationInput<TPixel> input, Rect area)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Process(input, input.Destination, area);
    }

    /// <summary>
    /// Returns the mask, 1 inside the ellipse and 0 far outside, for a pixel center (<paramref name="dx"/>, <paramref name="dy"/>) from
    /// the ellipse's center; <paramref name="invA2"/> and <paramref name="invB2"/> are 1/a² and 1/b², σ the edge's blur. The vector
    /// path in <see cref="Coverage"/> does the same operations in the same order, so both give the same bits.
    /// </summary>
    internal static float Mask(float dx, float dy, float invA2, float invB2, float sigma)
    {
        // f = sqrt((dx/a)² + (dy/b)²) is 1 on the ellipse; (f − 1)/|∇f| is the distance to it in pixels to first order.
        float u = dx * invA2, v = dy * invB2;
        float f2 = (dx * u) + (dy * v);
        float gradient2 = (u * u) + (v * v);
        if (!(gradient2 > MinGradient))
        {
            return 1;
        }

        float f = MathF.Sqrt(f2);
        float distance = (f - 1) * f / MathF.Sqrt(gradient2);
        float t = Math.Clamp(((distance / sigma) + Span) * Scale, 0, Steps);
        int i = (int)t;
        float w = t - i;
        return Profile[i] + (w * (Profile[i + 1] - Profile[i]));
    }

    /// <summary>
    /// Writes 1 − <see cref="Mask"/>, the color layer's coverage, for the pixels of a row from column <paramref name="x0"/>, whose
    /// centers are x + 0.5 − <paramref name="cx"/> across: whole numbers and halves are exact, so a pixel's value does not depend on
    /// where the row starts or which path computes it.
    /// </summary>
    internal static void Coverage(Span<float> coverage, int x0, float cx, float dy, float invA2, float invB2, float sigma)
    {
        int i = 0;
        if (Avx2.IsSupported && coverage.Length >= Vector256<float>.Count)
        {
            float v = dy * invB2, dyv = dy * v, vv = v * v;
            Vector256<float> lanes = Vector256.Create(0f, 1, 2, 3, 4, 5, 6, 7);
            Vector256<float> a2 = Vector256.Create(invA2), one = Vector256<float>.One, min = Vector256.Create(MinGradient);
            Vector256<float> sigmas = Vector256.Create(sigma), half = Vector256.Create(0.5f), center = Vector256.Create(cx), span = Vector256.Create(Span), scale = Vector256.Create(Scale);
            Vector256<float> top = Vector256.Create((float)Steps);
            unsafe
            {
                fixed (float* table = Profile)
                {
                    for (; i <= coverage.Length - Vector256<float>.Count; i += Vector256<float>.Count)
                    {
                        Vector256<float> dx = (Vector256.Create((float)(x0 + i)) + lanes + half) - center;
                        Vector256<float> u = dx * a2;
                        Vector256<float> f2 = (dx * u) + Vector256.Create(dyv);
                        Vector256<float> gradient2 = (u * u) + Vector256.Create(vv);
                        Vector256<float> f = Vector256.Sqrt(f2);
                        Vector256<float> distance = (f - one) * f / Vector256.Sqrt(gradient2);
                        Vector256<float> t = Vector256.Min(Vector256.Max(((distance / sigmas) + span) * scale, Vector256<float>.Zero), top);
                        Vector256<int> index = Vector256.ConvertToInt32(Vector256.Floor(t));
                        Vector256<float> w = t - Vector256.ConvertToSingle(index);
                        Vector256<float> p0 = Avx2.GatherVector256(table, index, 4);
                        Vector256<float> p1 = Avx2.GatherVector256(table + 1, index, 4);
                        Vector256<float> mask = p0 + (w * (p1 - p0));
                        mask = Vector256.ConditionalSelect(Vector256.GreaterThan(gradient2, min), mask, one);
                        (one - mask).StoreUnsafe(ref coverage[i]);
                    }
                }
            }
        }

        for (; i < coverage.Length; i++)
        {
            coverage[i] = 1 - Mask(((x0 + i) + 0.5f) - cx, dy, invA2, invB2, sigma);
        }
    }

    /// <summary>The ellipse's semi-axes and the edge's σ, in pixels, for an image of <paramref name="width"/> × <paramref name="height"/>.</summary>
    internal (float A, float B, float Sigma) Shape(int width, int height)
    {
        float a = _radius * width * 0.5f * MathF.Sqrt(2);
        float b = _radius * height * 0.5f * MathF.Sqrt(2);
        float halfDiagonal = 0.5f * MathF.Sqrt(((float)width * width) + ((float)height * height));

        // softness is the share of the radius the edge fades over, ±2σ of the blur.
        float sigma = Math.Max(_softness * _radius * halfDiagonal / 4, 0.3f);
        return (Math.Max(a, 1e-3f), Math.Max(b, 1e-3f), sigma);
    }

    private static float[] BuildProfile()
    {
        // One extra entry past the end, so the last step interpolates to itself.
        float[] table = new float[Steps + 2];
        for (int i = 0; i <= Steps + 1; i++)
        {
            double t = (Math.Min(i, Steps) * (2.0 * Span) / Steps) - Span;
            table[i] = (float)(0.5 * (1 - Erf(t / Math.Sqrt(2))));
        }

        return table;
    }

    /// <summary>The error function by its Taylor series, to double precision for |x| ≤ 3.5; past that it is ±1 to 1e-6.</summary>
    private static double Erf(double x)
    {
        if (Math.Abs(x) > 3.5)
        {
            return Math.Sign(x);
        }

        // erf(x) = 2/√π Σ (−1)ⁿ x^(2n+1) / (n! (2n+1)).
        double sum = 0, term = x;
        for (int n = 0; n < 200; n++)
        {
            double add = term / ((2 * n) + 1);
            sum += add;
            if (Math.Abs(add) < 1e-17)
            {
                break;
            }

            term *= -x * x / (n + 1);
        }

        return 2 / Math.Sqrt(Math.PI) * sum;
    }

    private void Process<TPixel>(OperationInput<TPixel> input, PixelPlane<TPixel> source, Rect area)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        PixelPlane<TPixel> destination = input.Destination;
        int width = destination.Width, height = destination.Height;
        (float a, float b, float sigma) = Shape(width, height);
        float invA2 = 1 / (a * a), invB2 = 1 / (b * b);
        Vector2 center = _center ?? new Vector2(width * 0.5f, height * 0.5f);
        float ca = _color.A / 255f;
        Vector4 color = new Vector4(_color.R / 255f, _color.G / 255f, _color.B / 255f, 1) * ca;
        if (typeof(TPixel) == typeof(PremulBgra8))
        {
            // Its rows load as they are stored, blue first.
            (color.X, color.Z) = (color.Z, color.X);
        }
        RowConverter<TPixel> converter = new(null, inksAsRgb: true);
        RowRunner.Run(area.Height, (long)area.Width * 16, input.Context.MaxParallelism, (start, end) =>
        {
            float[] buffer = ArrayPool<float>.Shared.Rent(area.Width * 5);
            try
            {
                Span<float> row = buffer.AsSpan(0, area.Width * 4), coverage = buffer.AsSpan(area.Width * 4, area.Width);
                for (int j = start; j < end; j++)
                {
                    int y = area.Y + j;
                    bool opaque = converter.Load(source.Row(y).Slice(area.X, area.Width), row);
                    float dy = y + 0.5f - center.Y;
                    Span<Vector4> pixels = System.Runtime.InteropServices.MemoryMarshal.Cast<float, Vector4>(row);
                    Coverage(coverage, area.X, center.X, dy, invA2, invB2, sigma);
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        // The color layer's alpha is ca·k; source-over puts it on top of the pixel.
                        float k = coverage[i];
                        if (k > 0)
                        {
                            pixels[i] = (color * k) + (pixels[i] * (1 - (ca * k)));
                        }
                    }

                    converter.Store(row, destination.Row(y).Slice(area.X, area.Width), opaque);
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(buffer);
            }
        }, input.CancellationToken);
    }
}
