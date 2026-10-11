namespace Egami.Operations;

/// <summary>
/// How a surface reflects light in <see cref="RasterOps.Lighting"/>: <see cref="Diffuse"/> (SVG's feDiffuseLighting, Lambert's
/// N·L) or <see cref="Specular"/> (feSpecularLighting, Phong–Blinn's (N·H)^shininess), with SVG's and Skia's constants.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Lighting.cs" region="lights"/>
/// </example>
public readonly record struct LightingModel
{
    private LightingModel(bool specular, float constant, float shininess)
    {
        IsSpecular = specular;
        Constant = constant;
        Shininess = shininess;
    }

    /// <summary>Gets a value indicating whether the model is specular; otherwise it is diffuse.</summary>
    public bool IsSpecular { get; }

    /// <summary>Gets the reflectance: SVG's diffuseConstant (kd) or specularConstant (ks).</summary>
    public float Constant { get; }

    /// <summary>Gets the specular exponent (SVG's specularExponent, Skia's shininess); 0 for a diffuse model.</summary>
    public float Shininess { get; }

    /// <summary>
    /// A matte surface: each pixel is the light's color times <paramref name="kd"/> times N·L, the cosine between the surface
    /// normal and the direction to the light, clamped to 0..1. The light map is opaque.
    /// </summary>
    /// <param name="kd">The reflectance, SVG's diffuseConstant and Skia's kd; 0 or more.</param>
    /// <returns>The model.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kd"/> is negative or not finite.</exception>
    public static LightingModel Diffuse(float kd = 1)
    {
        CheckConstant(kd, nameof(kd));
        return new LightingModel(false, kd, 0);
    }

    /// <summary>
    /// A shiny surface: each pixel is the light's color times <paramref name="ks"/> times (N·H)^<paramref name="shininess"/>, where
    /// H is halfway between the direction to the light and the viewer straight above, clamped to 0..1. The light map's alpha is
    /// the largest of its color channels, so it is transparent where there is no highlight.
    /// </summary>
    /// <param name="ks">The reflectance, SVG's specularConstant and Skia's ks; 0 or more.</param>
    /// <param name="shininess">The exponent, SVG's specularExponent and Skia's shininess, from 1 to 128; larger is a smaller, sharper highlight.</param>
    /// <returns>The model.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ks"/> is negative or not finite, or <paramref name="shininess"/> is not from 1 to 128.</exception>
    public static LightingModel Specular(float ks = 1, float shininess = 1)
    {
        CheckConstant(ks, nameof(ks));
        if (!(shininess >= 1 && shininess <= 128))
        {
            throw new ArgumentOutOfRangeException(nameof(shininess), shininess, "The shininess must be from 1 to 128.");
        }

        return new LightingModel(true, ks, shininess);
    }

    private static void CheckConstant(float k, string name)
    {
        if (!(k >= 0) || !float.IsFinite(k))
        {
            throw new ArgumentOutOfRangeException(name, k, "The constant must be 0 or more and finite.");
        }
    }
}
