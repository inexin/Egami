using System.Numerics;

using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing.Rasterization;

/// <summary>
/// Lights a bevel in device pixels: a height field from the signed distance to the shape's edge (<see cref="EdgeDistance"/>, as
/// the PSD flattener's bevel and emboss) through the profile, its normals by central differences, Lambert's diffuse term and
/// Blinn-Phong's specular term against the flat face's, written as white (or a tint) where lighter and black where darker.
/// </summary>
internal static class BevelShading
{
    /// <summary>
    /// Turns <paramref name="image"/>, whose alpha is the shape's coverage, into the bevel's light and shade as straight-alpha
    /// colors over transparency.
    /// </summary>
    internal static void Shade(Raster<RgbaF32> image, Bevel bevel, float width, float height)
    {
        int w = image.Width, h = image.Height, n = w * h;
        float[] cover = new float[n], distance = new float[n], field = new float[n];
        for (int y = 0; y < h; y++)
        {
            Span<RgbaF32> row = image.Row(y);
            for (int x = 0; x < w; x++)
            {
                cover[(y * w) + x] = row[x].A;
            }
        }

        float[] scratch = new float[n];
        EdgeDistance.Signed(cover, w, h, distance, scratch, new float[n]);
        Func<float, float> profile = Profile(bevel.Profile);
        for (int i = 0; i < n; i++)
        {
            field[i] = height * profile(Math.Clamp(-distance[i] / width, 0, 1));
        }

        // The distance field's gradient is faceted (each pixel takes one nearest edge pixel), which shows as rays on a curved
        // rim; two passes of a [1 2 1] filter each way smooth the field by about a pixel.
        Smooth(field, w, h, scratch);
        Smooth(field, w, h, scratch);

        Model m = Model.Of(bevel.Material);
        BevelLight light = bevel.Light;
        float angle = light.Angle * MathF.PI / 180, elevation = light.Elevation * MathF.PI / 180;
        Vector3 toLight = new(MathF.Cos(elevation) * MathF.Cos(angle), -MathF.Cos(elevation) * MathF.Sin(angle), MathF.Sin(elevation));
        Vector3 half = Vector3.Normalize(toLight + Vector3.UnitZ);
        float flat = toLight.Z, flatSpecular = MathF.Pow(half.Z, m.Shininess);
        for (int y = 0; y < h; y++)
        {
            Span<RgbaF32> row = image.Row(y);
            for (int x = 0; x < w; x++)
            {
                int i = (y * w) + x;
                float t = Math.Clamp(-distance[i] / width, 0, 1);
                if (cover[i] <= 0 || t >= 1)
                {
                    row[x] = default;
                    continue;
                }

                float gx = (field[(y * w) + Math.Min(x + 1, w - 1)] - field[(y * w) + Math.Max(x - 1, 0)]) / 2;
                float gy = (field[(Math.Min(y + 1, h - 1) * w) + x] - field[(Math.Max(y - 1, 0) * w) + x]) / 2;
                Vector3 normal = Vector3.Normalize(new Vector3(-gx, -gy, 1));
                float diffuse = Vector3.Dot(normal, toLight) - flat;
                float lighter = diffuse > 0 ? diffuse / MathF.Max(1 - flat, 1e-3f) : 0;
                float darker = diffuse < 0 ? -diffuse / MathF.Max(flat, 1e-3f) : 0;
                float specular = m.Specular > 0 && flatSpecular < 1
                    ? MathF.Max(0, MathF.Pow(MathF.Max(0, Vector3.Dot(normal, half)), m.Shininess) - flatSpecular) / (1 - flatSpecular)
                    : 0;
                float net = (m.Diffuse * lighter) + (m.Specular * specular) - (m.Shadow * darker) - (m.EdgeDark * (1 - t) * (1 - t));
                net *= light.Intensity * (m.EdgeFade ? t : 1);
                float alpha = Math.Min(1, Math.Abs(net)) * cover[i];
                row[x] = net > 0 ? new RgbaF32(m.Tint.X, m.Tint.Y, m.Tint.Z, alpha) : new RgbaF32(0, 0, 0, alpha);
            }
        }
    }

    /// <summary>Filters the plane by [1 2 1] / 4 across and then down, edges repeated; <paramref name="scratch"/> is the same size.</summary>
    private static void Smooth(float[] plane, int w, int h, float[] scratch)
    {
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                scratch[row + x] = (plane[row + Math.Max(x - 1, 0)] + (2 * plane[row + x]) + plane[row + Math.Min(x + 1, w - 1)]) / 4;
            }
        }

        for (int y = 0; y < h; y++)
        {
            int up = Math.Max(y - 1, 0) * w, row = y * w, down = Math.Min(y + 1, h - 1) * w;
            for (int x = 0; x < w; x++)
            {
                plane[row + x] = (scratch[up + x] + (2 * scratch[row + x]) + scratch[down + x]) / 4;
            }
        }
    }

    /// <summary>Returns the profile's height, 0 at the outer edge to about 1, at a share of the way in from 0 to 1.</summary>
    internal static Func<float, float> Profile(BevelProfile profile) => profile switch
    {
        BevelProfile.RelaxedInset => t => t * t * (3 - (2 * t)),
        BevelProfile.Cross => t => t < 0.5f ? MathF.Sin(MathF.PI * t) : 0.75f + (0.25f * MathF.Cos(2 * MathF.PI * (t - 0.5f))),
        BevelProfile.CoolSlant => t => t < 0.2f ? 3 * t : 0.6f + (0.5f * (t - 0.2f)),
        BevelProfile.Angle => t => t,
        BevelProfile.SoftRound => t => MathF.Sin(MathF.PI * t / 2),
        BevelProfile.Convex => t => 1 - ((1 - t) * (1 - t)),
        BevelProfile.Slope => t => t * t,
        BevelProfile.Divot => t => t < 0.25f ? MathF.Sin(2 * MathF.PI * t) : t < 0.75f ? 1 - (0.6f * MathF.Sin(2 * MathF.PI * (t - 0.25f))) : 1,
        BevelProfile.Riblet => t => (0.8f * t) + (0.2f * MathF.Abs(MathF.Sin(3 * MathF.PI * t))),
        BevelProfile.HardEdge => t => Math.Min(1, 4 * t),
        BevelProfile.ArtDeco => ArtDeco,
        _ => t => MathF.Sqrt(1 - ((1 - t) * (1 - t))),
    };

    private static float ArtDeco(float t)
    {
        float u = 3 * t;
        int step = Math.Min(2, (int)u);
        float riser = Math.Min(1, (u - step) / 0.35f);
        return (step + (riser * riser * (3 - (2 * riser)))) / 3;
    }

    /// <summary>A material: the diffuse, specular and shadow weights, the shine's exponent, the highlight's tint and edge effects.</summary>
    private readonly record struct Model(float Diffuse, float Specular, float Shininess, float Shadow, Vector3 Tint, float EdgeDark = 0, bool EdgeFade = false)
    {
        private static readonly Vector3 White = Vector3.One;

        internal static Model Of(BevelMaterial material) => material switch
        {
            BevelMaterial.Matte => new(1, 0, 1, 1, White),
            BevelMaterial.WarmMatte => new(1, 0, 1, 1, new Vector3(1, 0.95f, 0.85f)),
            BevelMaterial.Plastic => new(0.8f, 0.6f, 40, 1, White),
            BevelMaterial.Metal => new(0.4f, 1, 20, 1.3f, White),
            BevelMaterial.SoftMetal => new(0.5f, 0.7f, 8, 1.1f, White),
            BevelMaterial.DarkEdge => new(1, 0, 1, 1, White, EdgeDark: 0.5f),
            BevelMaterial.SoftEdge => new(1, 0, 1, 1, White, EdgeFade: true),
            BevelMaterial.Clear => new(0.25f, 0.9f, 60, 0.4f, White),
            BevelMaterial.Powder => new(0.7f, 0, 1, 0.6f, White),
            BevelMaterial.TranslucentPowder => new(0.35f, 0, 1, 0.3f, White),
            _ => new(0, 0, 1, 0, White),
        };
    }
}
