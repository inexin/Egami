using Egami.Operations.Filters;

namespace Egami.Operations;

/// <summary>Lighting and relief (Phase 2.5.4 stage C): the SVG lighting filters, emboss and shade.</summary>
public sealed partial class RasterOps
{
    /// <summary>
    /// Lights the image as a height field with the SVG 1.1 lighting filters (feDiffuseLighting and feSpecularLighting), with
    /// Skia's parameters: the height channel (alpha by default) times <see cref="LightingOptions.SurfaceScale"/> is a surface, its
    /// normal comes from SVG's Sobel stencils (with SVG's own stencils on the edges and corners), and the light's color is scaled
    /// by N·L (<see cref="LightingModel.Diffuse"/>) or (N·H)^shininess (<see cref="LightingModel.Specular"/>). The result is the
    /// light map (opaque for diffuse; for specular, alpha is the largest color channel), or, with
    /// <see cref="LightingOptions.Blend"/>, the light map blended onto the image, which keeps its alpha. Light is computed on
    /// stored values, as Skia does in its working space; SVG's default of linear light is not applied.
    /// </summary>
    /// <param name="light">The light: <see cref="DistantLight"/>, <see cref="PointLight"/> or <see cref="SpotLight"/>, in pixel coordinates.</param>
    /// <param name="model">How the surface reflects: <see cref="LightingModel.Diffuse"/> or <see cref="LightingModel.Specular"/>; the default value is a diffuse model with kd 0, which is black.</param>
    /// <param name="options">The surface scale, height channel and blend; <see langword="null"/> for the defaults.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="light"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Lighting.cs" region="lighting"/>
    /// </example>
    public RasterOps Lighting(Light light, LightingModel model, LightingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(light);
        return Add(new LightingOperation(light, model, options ?? new LightingOptions()));
    }

    /// <summary>
    /// Embosses: blurs with a Gaussian of <paramref name="sigma"/>, takes the slope toward the light at <paramref name="angle"/>
    /// with a 3 × 3 Sobel stencil, scales it by <paramref name="depth"/> and adds mid gray, so flat areas become 0.5 and slopes
    /// facing the light are lighter. This is the classic directional emboss; ImageMagick's -emboss is a different filter (a
    /// diagonal sharpening kernel followed by histogram equalization). Works on stored values; alpha is kept; edges repeat the
    /// edge pixel.
    /// </summary>
    /// <param name="angle">Where the light comes from, in degrees counterclockwise from the right with y up, as -shade's azimuth; 135 is the top left.</param>
    /// <param name="depth">The strength of the relief, from 0 (flat gray) to 100; 1 maps a slope of one level per pixel to one level.</param>
    /// <param name="sigma">The blur before the slope is taken, in pixels, from 0 (none) to 100.</param>
    /// <param name="grayscale">Whether the height is the Rec. 709 luminance and the result gray; otherwise each color channel is embossed on its own.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="angle"/> is not finite, or <paramref name="depth"/> or <paramref name="sigma"/> is out of range.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Lighting.cs" region="emboss"/>
    /// </example>
    public RasterOps Emboss(float angle = 135, float depth = 1, float sigma = 1, bool grayscale = true)
    {
        if (!float.IsFinite(angle))
        {
            throw new ArgumentOutOfRangeException(nameof(angle), angle, "The angle must be finite.");
        }

        if (!(depth >= 0 && depth <= 100))
        {
            throw new ArgumentOutOfRangeException(nameof(depth), depth, "The depth must be from 0 to 100.");
        }

        if (!(sigma >= 0 && sigma <= 100))
        {
            throw new ArgumentOutOfRangeException(nameof(sigma), sigma, "Sigma must be from 0 to 100.");
        }

        return Add(new EmbossOperation(angle, depth, sigma, grayscale));
    }

    /// <summary>
    /// Shades the image as ImageMagick's -shade does: the luminance is a height field, and each pixel becomes the Lambertian
    /// shading of its surface normal by a distant light at <paramref name="azimuth"/> and <paramref name="elevation"/>, a gray
    /// relief; with <paramref name="colored"/> the shading multiplies the image's own colors instead, as +shade does. Identical to
    /// ImageMagick at 8 bits; alpha is kept; edges repeat the edge pixel.
    /// </summary>
    /// <param name="azimuth">The light's direction in the image plane, in degrees counterclockwise from the right with y up, as ImageMagick's.</param>
    /// <param name="elevation">The light's angle above the image plane, in degrees, from 0 to 90; flat areas get sin(elevation).</param>
    /// <param name="colored">Whether the shading multiplies the colors (+shade) rather than replacing them with gray (-shade).</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="azimuth"/> is not finite, or <paramref name="elevation"/> is not from 0 to 90.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Lighting.cs" region="shade"/>
    /// </example>
    public RasterOps Shade(float azimuth = 30, float elevation = 30, bool colored = false)
    {
        if (!float.IsFinite(azimuth))
        {
            throw new ArgumentOutOfRangeException(nameof(azimuth), azimuth, "The azimuth must be finite.");
        }

        if (!(elevation >= 0 && elevation <= 90))
        {
            throw new ArgumentOutOfRangeException(nameof(elevation), elevation, "The elevation must be from 0 to 90 degrees.");
        }

        return Add(new ShadeOperation(azimuth, elevation, colored));
    }
}
