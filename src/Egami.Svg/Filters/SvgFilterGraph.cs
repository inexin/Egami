using System.Numerics;

using Egami.Drawing;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Svg.Filters;

/// <summary>Where a filter primitive's input comes from.</summary>
internal enum SvgFilterSource : byte
{
    SourceGraphic,
    SourceAlpha,
    Result,
}

/// <summary>A filter primitive's input: the filtered element's pixels or alpha, or an earlier primitive's result.</summary>
internal readonly record struct SvgFilterInput(SvgFilterSource Source, int Index = -1)
{
    public static SvgFilterInput Graphic { get; } = new(SvgFilterSource.SourceGraphic);
}

/// <summary>
/// One filter primitive, resolved: its subregion and every length in the filtered element's user units, its inputs, and whether
/// it works on linearRGB values (color-interpolation-filters).
/// </summary>
internal abstract record SvgPrimitive(RectF Region, bool Linear);

internal sealed record SvgBlur(RectF Region, bool Linear, SvgFilterInput In, float SigmaX, float SigmaY) : SvgPrimitive(Region, Linear);

internal sealed record SvgOffset(RectF Region, bool Linear, SvgFilterInput In, float Dx, float Dy) : SvgPrimitive(Region, Linear);

internal sealed record SvgDropShadow(RectF Region, bool Linear, SvgFilterInput In, float Dx, float Dy, float SigmaX, float SigmaY, RgbaF32 Color) : SvgPrimitive(Region, Linear);

internal sealed record SvgFlood(RectF Region, bool Linear, RgbaF32 Color) : SvgPrimitive(Region, Linear);

internal sealed record SvgMerge(RectF Region, bool Linear, IReadOnlyList<SvgFilterInput> Inputs) : SvgPrimitive(Region, Linear);

internal sealed record SvgBlend(RectF Region, bool Linear, SvgFilterInput In, SvgFilterInput In2, BlendMode Mode) : SvgPrimitive(Region, Linear);

/// <summary>feComposite's operators; Arithmetic uses K1 to K4.</summary>
internal enum SvgCompositeOperator : byte
{
    Over,
    In,
    Out,
    Atop,
    Xor,
    Lighter,
    Arithmetic,
}

internal sealed record SvgComposite(RectF Region, bool Linear, SvgFilterInput In, SvgFilterInput In2, SvgCompositeOperator Operator, Vector4 K) : SvgPrimitive(Region, Linear);

/// <summary>A 4 by 5 color matrix, row major, as feColorMatrix's values (R', G', B', A' rows; R, G, B, A, 1 columns).</summary>
internal sealed record SvgColorMatrix(RectF Region, bool Linear, SvgFilterInput In, float[] Matrix) : SvgPrimitive(Region, Linear);

/// <summary>One channel's feComponentTransfer function.</summary>
internal sealed record SvgTransferFunction(string Type, float[] Table, float Slope, float Intercept, float Amplitude, float Exponent, float Offset)
{
    public static SvgTransferFunction Identity { get; } = new("identity", [], 1, 0, 1, 1, 0);

    /// <summary>Applies the function to a channel value in 0 to 1.</summary>
    public float Apply(float c)
    {
        switch (Type)
        {
            case "table" when Table.Length > 0:
            {
                int n = Table.Length - 1;
                if (n == 0)
                {
                    return Table[0];
                }

                float x = c * n;
                int k = Math.Clamp((int)MathF.Floor(x), 0, n - 1);
                return Table[k] + ((x - k) * (Table[k + 1] - Table[k]));
            }

            case "discrete" when Table.Length > 0:
            {
                int n = Table.Length;
                int k = Math.Clamp((int)MathF.Floor(c * n), 0, n - 1);
                return Table[k];
            }

            case "linear":
                return (Slope * c) + Intercept;
            case "gamma":
                return (Amplitude * MathF.Pow(c, Exponent)) + Offset;
            default:
                return c;
        }
    }
}

internal sealed record SvgComponentTransfer(RectF Region, bool Linear, SvgFilterInput In, SvgTransferFunction R, SvgTransferFunction G, SvgTransferFunction B, SvgTransferFunction A) : SvgPrimitive(Region, Linear);

internal sealed record SvgConvolve(
    RectF Region, bool Linear, SvgFilterInput In, int OrderX, int OrderY, float[] Kernel, float Divisor, float Bias, int TargetX, int TargetY,
    EdgeMode Edge, bool PreserveAlpha) : SvgPrimitive(Region, Linear);

internal sealed record SvgMorphology(RectF Region, bool Linear, SvgFilterInput In, bool Dilate, float RadiusX, float RadiusY) : SvgPrimitive(Region, Linear);

/// <summary>A light source of feDiffuseLighting or feSpecularLighting, in user units.</summary>
internal abstract record SvgLight;

internal sealed record SvgDistantLight(float Azimuth, float Elevation) : SvgLight;

internal sealed record SvgPointLight(Vector3 Position) : SvgLight;

internal sealed record SvgSpotLight(Vector3 Position, Vector3 PointsAt, float Exponent, float? ConeAngle) : SvgLight;

internal sealed record SvgLighting(
    RectF Region, bool Linear, SvgFilterInput In, bool Specular, float SurfaceScale, float Constant, float Exponent, RgbaF32 Color, SvgLight Light) : SvgPrimitive(Region, Linear);

internal sealed record SvgDisplacement(RectF Region, bool Linear, SvgFilterInput In, SvgFilterInput In2, float Scale, ColorChannel X, ColorChannel Y) : SvgPrimitive(Region, Linear);

internal sealed record SvgTurbulence(RectF Region, bool Linear, float FrequencyX, float FrequencyY, int Octaves, int Seed, bool Stitch, bool Fractal) : SvgPrimitive(Region, Linear);

internal sealed record SvgTile(RectF Region, bool Linear, SvgFilterInput In) : SvgPrimitive(Region, Linear);

/// <summary>feImage: a resolved image or element drawn into the subregion, in user units.</summary>
internal sealed record SvgFeImage(RectF Region, bool Linear, SvgNode? Content) : SvgPrimitive(Region, Linear);

/// <summary>One filter of a filter list: its region and primitives, run in order; the last primitive's result is the filter's.</summary>
internal sealed record SvgFilterGraph(RectF Region, IReadOnlyList<SvgPrimitive> Primitives);
