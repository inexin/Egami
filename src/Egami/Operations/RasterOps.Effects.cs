using System.Numerics;

using Egami.Operations.Filters;
using Egami.Pixels;

namespace Egami.Operations;

/// <summary>The effects of Phase 2.5.4 (vignette, glow, bloom, pixelate, oil paint, Kuwahara, spread and drop shadow) and 2.6.3's inner shadow.</summary>
public sealed partial class RasterOps
{
    /// <summary>
    /// Darkens (or tints) the image toward its corners, outside an ellipse fitted to the image, with a soft edge: the shape of
    /// ImageMagick's -vignette, which blurs a drawn ellipse. The mask is computed per pixel, so its cost does not depend on the
    /// softness. Pixels are mixed with the color source-over, on premultiplied stored values.
    /// </summary>
    /// <param name="radius">
    /// The ellipse's size as a fraction of the half-diagonal: at 1 the ellipse passes through the corners, at 0.7 it is about as
    /// wide as the image. The ellipse has the image's proportions.
    /// </param>
    /// <param name="softness">
    /// The share of the radius over which the edge fades, from 0 (hard) to 1: the fade is a Gaussian-blurred edge whose ±2σ spans
    /// softness × radius.
    /// </param>
    /// <param name="color">The color toward the corners, with its alpha as its strength; black and opaque when <see langword="null"/>.</param>
    /// <param name="center">The ellipse's center in the raster's pixel coordinates (0, 0 is the top-left corner of the image); the image's middle when <see langword="null"/>.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="radius"/> is outside 0..10, <paramref name="softness"/> is outside 0..1, or <paramref name="center"/> is not finite.
    /// </exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/FilterEffects.cs" region="vignette"/>
    /// </example>
    public RasterOps Vignette(float radius = 1, float softness = 0.5f, Rgba8? color = null, Vector2? center = null)
    {
        if (!(radius >= 0 && radius <= 10))
        {
            throw new ArgumentOutOfRangeException(nameof(radius), radius, "The radius must be from 0 to 10.");
        }

        if (!(softness >= 0 && softness <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(softness), softness, "The softness must be from 0 to 1.");
        }

        if (center is Vector2 c && !(float.IsFinite(c.X) && float.IsFinite(c.Y)))
        {
            throw new ArgumentOutOfRangeException(nameof(center), center, "The center must be finite.");
        }

        Rgba8 tint = color ?? new Rgba8(0, 0, 0, 255);
        return tint.A == 0 ? this : Add(new VignetteOperation(radius, softness, tint, center));
    }

    /// <summary>
    /// Adds a glow around the image's alpha shape, as an editor's outer glow layer style: the alpha, blurred with
    /// <see cref="BlurMethod.Fast"/>, spread, and filled with <paramref name="color"/>, drawn behind the image. With
    /// <paramref name="inner"/> the glow comes in from the shape's edges instead and is drawn on the image, inside the shape only.
    /// It is exactly the chain written by hand: a layer made with a <see cref="ColorMatrix(Operations.ColorMatrix)"/> (the color, and
    /// alpha or 1 − alpha), <see cref="Blur(float, ColorSpace, BlurMethod)"/>, <see cref="Arithmetic"/> multiplying alpha by
    /// 1 / (1 − spread) and <see cref="Opacity"/> for the color's alpha, then drawn with DrawImage (the image over the layer, or
    /// the layer over the image with <see cref="BlendMode.SourceAtop"/>). An opaque image has no edge to glow from.
    /// </summary>
    /// <param name="sigma">The glow's blur in pixels, from 0 to 1000.</param>
    /// <param name="color">The glow's color, with its alpha as the glow's opacity.</param>
    /// <param name="spread">How much the glow is hardened before it fades, from 0 (a plain blur) to just under 1 (a hard edge): the blurred alpha is divided by 1 − spread.</param>
    /// <param name="inner">Whether the glow is inside the shape instead of around it.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sigma"/> is outside 0..1000, or <paramref name="spread"/> is outside 0 to under 1.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/FilterEffects.cs" region="glow"/>
    /// </example>
    public RasterOps Glow(float sigma, Rgba8 color, float spread = 0, bool inner = false)
    {
        CheckSigma(sigma);
        if (!(spread >= 0 && spread < 1))
        {
            throw new ArgumentOutOfRangeException(nameof(spread), spread, "The spread must be from 0 to under 1.");
        }

        if (color.A == 0)
        {
            return this;
        }

        RasterOps layer = new();
        float r = color.R / 255f, g = color.G / 255f, b = color.B / 255f;
        layer.ColorMatrix(new ColorMatrix(inner
            ? [0, 0, 0, 0, r, 0, 0, 0, 0, g, 0, 0, 0, 0, b, 0, 0, 0, -1, 1]
            : [0, 0, 0, 0, r, 0, 0, 0, 0, g, 0, 0, 0, 0, b, 0, 0, 0, 1, 0]));
        layer.Blur(sigma, ColorSpace.Encoded, BlurMethod.Fast);
        if (spread > 0)
        {
            layer.Arithmetic(PixelMath.Multiply, 1 / (1 - spread), ColorChannels.Alpha);
        }

        if (color.A < 255)
        {
            layer.Opacity(color.A / 255f);
        }

        return inner
            ? Add(new LayerEffectOperation(layer.Steps, LayerPlacement.Over, BlendMode.SourceAtop, linear: false))
            : Add(new LayerEffectOperation(layer.Steps, LayerPlacement.Behind, BlendMode.SourceOver, linear: false));
    }

    /// <summary>
    /// Adds a shadow inside the image's alpha shape, as an editor's inner shadow layer style: everything outside the shape, moved
    /// by (<paramref name="dx"/>, <paramref name="dy"/>) rounded to whole pixels, blurred with <see cref="BlurMethod.Fast"/> and
    /// filled with <paramref name="color"/>, drawn on the image inside the shape only. Past the image's edges counts as outside, so
    /// a shape that touches an edge is shadowed from there too. It is exactly the chain written by hand: a layer made with a
    /// <see cref="ColorMatrix(Operations.ColorMatrix)"/> (the color, and 1 − alpha), <see cref="Offset"/> with the uncovered strips
    /// filled with the opaque color, <see cref="Blur(float, ColorSpace, BlurMethod)"/> and <see cref="Opacity"/> for the color's
    /// alpha, then drawn over the image with <see cref="BlendMode.SourceAtop"/>.
    /// </summary>
    /// <param name="dx">How far the shadow moves right, in pixels (negative moves it left), at most 10000 either way.</param>
    /// <param name="dy">How far the shadow moves down, in pixels, at most 10000 either way.</param>
    /// <param name="sigma">The shadow's blur in pixels, from 0 (a hard shadow) to 1000.</param>
    /// <param name="color">The shadow's color, with its alpha as the shadow's opacity.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An offset is not finite or beyond 10000 either way, or <paramref name="sigma"/> is outside 0..1000.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/FilterEffects.cs" region="inner-shadow"/>
    /// </example>
    public RasterOps InnerShadow(float dx, float dy, float sigma, Rgba8 color)
    {
        if (!(Math.Abs(dx) <= 10000))
        {
            throw new ArgumentOutOfRangeException(nameof(dx), dx, "The offset must be from -10000 to 10000.");
        }

        if (!(Math.Abs(dy) <= 10000))
        {
            throw new ArgumentOutOfRangeException(nameof(dy), dy, "The offset must be from -10000 to 10000.");
        }

        CheckSigma(sigma);
        if (color.A == 0)
        {
            return this;
        }

        RasterOps layer = new();
        float r = color.R / 255f, g = color.G / 255f, b = color.B / 255f;
        layer.ColorMatrix(new ColorMatrix([0, 0, 0, 0, r, 0, 0, 0, 0, g, 0, 0, 0, 0, b, 0, 0, 0, -1, 1]));
        layer.Offset((int)MathF.Round(dx), (int)MathF.Round(dy), EdgeMode.Transparent, new Rgba8(color.R, color.G, color.B, 255));
        layer.Blur(sigma, ColorSpace.Encoded, BlurMethod.Fast);
        if (color.A < 255)
        {
            layer.Opacity(color.A / 255f);
        }

        return Add(new LayerEffectOperation(layer.Steps, LayerPlacement.Over, BlendMode.SourceAtop, linear: false));
    }

    /// <summary>
    /// Makes bright areas glow, as a camera's bloom: the parts of each channel above <paramref name="threshold"/>, stretched to 0..1
    /// and scaled by <paramref name="intensity"/>, blurred in linear light with <see cref="BlurMethod.Fast"/> and screened onto the
    /// image in linear light. It is exactly the chain written by hand: a layer made with one
    /// <see cref="ColorMatrix(Operations.ColorMatrix)"/> (red, green and blue times k = intensity / (1 − threshold), minus
    /// threshold × k, clamped) and <see cref="Blur(float, ColorSpace, BlurMethod)"/> in linear light, then
    /// drawn with DrawImage, <see cref="BlendMode.Screen"/>, in linear light.
    /// </summary>
    /// <param name="threshold">The stored value above which a channel blooms, from 0 to under 1.</param>
    /// <param name="sigma">The bloom's blur in pixels, from 0 to 1000.</param>
    /// <param name="intensity">How strongly the bloom is added, from 0 (nothing) to 100; values are clamped to 1 before the blur.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="threshold"/> is outside 0 to under 1, <paramref name="sigma"/> is outside 0..1000, or <paramref name="intensity"/> is outside 0..100.
    /// </exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/FilterEffects.cs" region="bloom"/>
    /// </example>
    public RasterOps Bloom(float threshold = 0.8f, float sigma = 10, float intensity = 1)
    {
        if (!(threshold >= 0 && threshold < 1))
        {
            throw new ArgumentOutOfRangeException(nameof(threshold), threshold, "The threshold must be from 0 to under 1.");
        }

        CheckSigma(sigma);
        if (!(intensity >= 0 && intensity <= 100))
        {
            throw new ArgumentOutOfRangeException(nameof(intensity), intensity, "The intensity must be from 0 to 100.");
        }

        if (intensity == 0)
        {
            return this;
        }

        // One matrix, clamped once: (value − threshold) × intensity / (1 − threshold) for red, green and blue.
        RasterOps layer = new();
        float k = intensity / (1 - threshold), o = -threshold * k;
        layer.ColorMatrix(new ColorMatrix([k, 0, 0, 0, o, 0, k, 0, 0, o, 0, 0, k, 0, o, 0, 0, 0, 1, 0]));
        layer.Blur(sigma, ColorSpace.Linear, BlurMethod.Fast);
        return Add(new LayerEffectOperation(layer.Steps, LayerPlacement.Over, BlendMode.Screen, linear: true));
    }

    /// <summary>
    /// Pixelates: the image is cut into square cells, and every pixel of a cell becomes the cell's average, taken on premultiplied
    /// values so transparent pixels do not darken it. Cells start at <paramref name="origin"/> and repeat every
    /// <paramref name="cellSize"/> pixels both ways; cells cut by the image's edges average the pixels they hold. Inside
    /// <see cref="Within(Rect, Action{RasterOps})"/> cells stay on the image's grid and average whole cells, reading past the area.
    /// </summary>
    /// <param name="cellSize">The side of a cell in pixels, from 1 (changes nothing) to 4096.</param>
    /// <param name="origin">Where a cell's top-left corner falls, in the raster's coordinates; the grid repeats from there both ways.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cellSize"/> is outside 1..4096.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/FilterEffects.cs" region="pixelate"/>
    /// </example>
    public RasterOps Pixelate(int cellSize, Point origin = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(cellSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(cellSize, 4096);
        return cellSize == 1 ? this : Add(new PixelateOperation(cellSize, origin));
    }

    /// <summary>
    /// Makes the image look painted, as ImageMagick's -paint does: around each pixel, in the (2r + 1) square, pixels are sorted into
    /// <paramref name="levels"/> bins by intensity, and the pixel becomes a pixel of the most common bin, unchanged. With 256 levels
    /// on 8-bit images the result is ImageMagick's exactly; fewer levels make larger, flatter strokes. Works on stored values; the
    /// histogram slides along each row, so a larger radius costs little more per pixel than the window's size.
    /// </summary>
    /// <param name="radius">The brush radius in pixels, from 1 to 50.</param>
    /// <param name="levels">The number of intensity bins, from 2 to 256.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radius"/> is outside 1..50, or <paramref name="levels"/> is outside 2..256.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/FilterEffects.cs" region="oil-paint"/>
    /// </example>
    public RasterOps OilPaint(int radius = 3, int levels = 20)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(radius, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(radius, OilPaintOperation.MaxRadius);
        ArgumentOutOfRangeException.ThrowIfLessThan(levels, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(levels, 256);
        return Add(new OilPaintOperation(radius, levels));
    }

    /// <summary>
    /// Smooths while keeping edges, with the Kuwahara filter as ImageMagick's -kuwahara defines it: of the four (r + 1) squares that
    /// have the pixel as a corner, the one whose luma varies least gives the pixel its value, sampled at that square's middle. With
    /// <paramref name="sigma"/> above 0 the image is blurred first with ImageMagick's kernel for radius x sigma (a Gaussian of σ out to the radius). The
    /// variances come from summed-area tables, so the cost does not grow with the radius.
    /// </summary>
    /// <param name="radius">The radius in pixels, from 1 to 100.</param>
    /// <param name="sigma">The blur before, in pixels, from 0 (none) to 100.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radius"/> is outside 1..100, or <paramref name="sigma"/> is outside 0..100.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/FilterEffects.cs" region="kuwahara"/>
    /// </example>
    public RasterOps Kuwahara(int radius = 3, float sigma = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(radius, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(radius, KuwaharaOperation.MaxRadius);
        if (!(sigma >= 0 && sigma <= LargeSigma))
        {
            throw new ArgumentOutOfRangeException(nameof(sigma), sigma, "Sigma must be from 0 to 100.");
        }

        return Add(new KuwaharaOperation(radius, sigma));
    }

    /// <summary>
    /// Scatters pixels, as ImageMagick's -spread does: each pixel takes the value of a random pixel at most
    /// <paramref name="radius"/> away (a whole-pixel offset in the disc; edges repeat the edge pixel). The offsets come from a hash of
    /// the seed and the pixel's position, so the same seed gives the same pixels on any machine and any number of cores.
    /// </summary>
    /// <param name="radius">The farthest a value moves, in pixels, from 0 to 1000; below 1 nothing moves.</param>
    /// <param name="seed">Picks the pattern; the same seed gives the same result.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radius"/> is outside 0..1000.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/FilterEffects.cs" region="spread"/>
    /// </example>
    public RasterOps Spread(float radius, ulong seed = 0)
    {
        if (!(radius >= 0 && radius <= SpreadOperation.MaxRadius))
        {
            throw new ArgumentOutOfRangeException(nameof(radius), radius, "The radius must be from 0 to 1000.");
        }

        return radius < 1 ? this : Add(new SpreadOperation(radius, seed));
    }

    /// <summary>
    /// Adds a drop shadow, as Skia's DropShadow image filter: the image's alpha, moved by (<paramref name="dx"/>,
    /// <paramref name="dy"/>), blurred by a Gaussian of <paramref name="sigma"/> and filled with <paramref name="color"/>, with the
    /// image drawn over it source-over. By default the canvas grows by 3σ plus the offset on the sides the shadow reaches past the
    /// image, so it is never cut off, and the step then cannot run inside <see cref="Within(Rect, Action{RasterOps})"/>; set
    /// <see cref="DropShadowOptions.Expand"/> to <see langword="false"/> to keep the size. The shadow fades as if the image went on,
    /// transparent, past its edges.
    /// </summary>
    /// <param name="dx">How far the shadow moves right, in pixels (negative moves it left), at most 10000 either way.</param>
    /// <param name="dy">How far the shadow moves down, in pixels, at most 10000 either way.</param>
    /// <param name="sigma">The shadow's blur in pixels, from 0 (a hard shadow) to 1000.</param>
    /// <param name="color">The shadow's color, with its alpha as the shadow's opacity.</param>
    /// <param name="options">A separate vertical blur, whether the canvas grows, whether only the shadow is kept, and the blur method; the defaults when <see langword="null"/>.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An offset is not finite or beyond 10000 either way, or <paramref name="sigma"/> is outside 0..1000.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/FilterEffects.cs" region="drop-shadow"/>
    /// </example>
    public RasterOps DropShadow(float dx, float dy, float sigma, Rgba8 color, DropShadowOptions? options = null)
    {
        if (!(Math.Abs(dx) <= 10000))
        {
            throw new ArgumentOutOfRangeException(nameof(dx), dx, "The offset must be from -10000 to 10000.");
        }

        if (!(Math.Abs(dy) <= 10000))
        {
            throw new ArgumentOutOfRangeException(nameof(dy), dy, "The offset must be from -10000 to 10000.");
        }

        CheckSigma(sigma);
        options ??= new DropShadowOptions();
        return Add(new DropShadowOperation(dx, dy, sigma, options.SigmaY ?? sigma, color, options));
    }
}
