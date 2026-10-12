using Egami.Colors;
using Egami.Metadata;

namespace Egami.Operations.Geometry;

/// <summary>
/// Keeps a raster's gain map in step with a geometry step (HDR Design question 12): crops, pads, flips, turns, rotations,
/// resizes and affine transforms move the map with the picture, sampled at the map's own scale; a step whose effect is unknown
/// (a warp, a perspective transform, a custom step) drops it. The map follows Apply's convention: map pixel n covers base pixel
/// n × (base size / map size) onward.
/// </summary>
internal static class GainMapUpkeep
{
    /// <summary>Moves <see cref="RasterMetadata.GainMap"/> by <paramref name="map"/>, or drops it when the step is not affine.</summary>
    /// <param name="metadata">The metadata, whose gain map is replaced.</param>
    /// <param name="map">Where the step moves input points, or null when that is unknown.</param>
    /// <param name="input">The size of the picture the step read.</param>
    public static void Apply(RasterMetadata metadata, SubjectMap? map, Size input)
    {
        if (metadata.GainMap is GainMap gainMap)
        {
            metadata.GainMap = map is null || input.Width <= 0 || input.Height <= 0 ? null : Move(gainMap, map, input);
        }
    }

    /// <summary>Returns the map moved as <paramref name="map"/> moves the picture, or null when the step is not affine.</summary>
    internal static GainMap? Move(GainMap gainMap, SubjectMap map, Size input)
    {
        // The step as an affine map of pixel positions: out = origin + (a b; c d) · in.
        (double ox, double oy) = map.Point(0, 0);
        (double ex, double ey) = map.Point(1, 0);
        (double fx, double fy) = map.Point(0, 1);
        double a = ex - ox, c = ey - oy, b = fx - ox, d = fy - oy;
        (double px, double py) = map.Point(input.Width, input.Height);
        double tolerance = 1e-6 * (input.Width + input.Height + Math.Abs(ox) + Math.Abs(oy));
        double det = (a * d) - (b * c);
        if (Math.Abs(ox + (a * input.Width) + (b * input.Height) - px) > tolerance || Math.Abs(oy + (c * input.Width) + (d * input.Height) - py) > tolerance || Math.Abs(det) < 1e-12)
        {
            return null;
        }

        Size output = map.Bounds;
        (double cx, double cy) = map.Point(-0.5, -0.5);
        if (b == 0 && c == 0 && a > 0 && d > 0 && Math.Abs(cx + 0.5) <= tolerance && Math.Abs(cy + 0.5) <= tolerance
            && Math.Abs((a * input.Width) - output.Width) <= tolerance && Math.Abs((d * input.Height) - output.Height) <= tolerance)
        {
            // The whole picture scaled (or kept): the map covers it as it is.
            return gainMap;
        }

        double sx = gainMap.Width / (double)input.Width, sy = gainMap.Height / (double)input.Height;
        bool swapped = Math.Abs(b) > Math.Abs(a);
        int width = Math.Max(1, (int)Math.Round(output.Width * (swapped ? sy : sx), MidpointRounding.AwayFromZero));
        int height = Math.Max(1, (int)Math.Round(output.Height * (swapped ? sx : sy), MidpointRounding.AwayFromZero));
        int channels = gainMap.Channels;
        byte[] source = gainMap.Pixels;
        byte[] pixels = new byte[width * height * channels];
        Span<byte> neutral = stackalloc byte[3];
        for (int ch = 0; ch < 3; ch++)
        {
            neutral[ch] = Neutral(gainMap.Info, ch);
        }

        for (int j = 0; j < height; j++)
        {
            double y = (j * (double)output.Height / height) - oy;
            for (int i = 0; i < width; i++)
            {
                // The output pixel this map pixel starts at, back in the input, then in the source map.
                double x = (i * (double)output.Width / width) - ox;
                double inX = ((d * x) - (b * y)) / det, inY = ((a * y) - (c * x)) / det;
                Span<byte> target = pixels.AsSpan(((j * width) + i) * channels, channels);
                if (inX < -0.5 || inY < -0.5 || inX > input.Width - 0.5 || inY > input.Height - 0.5)
                {
                    neutral[..channels].CopyTo(target);
                    continue;
                }

                Sample(source, gainMap.Width, gainMap.Height, channels, inX * sx, inY * sy, target);
            }
        }

        return new GainMap(gainMap.Info, width, height, channels, pixels);
    }

    /// <summary>The map value of channel <paramref name="channel"/> that leaves the picture as it is (a boost of 1).</summary>
    internal static byte Neutral(GainMapInfo info, int channel)
    {
        double min = info.GainMapMin[channel], max = info.GainMapMax[channel];
        double g = max > min ? Math.Clamp(-min / (max - min), 0, 1) : 0;
        return (byte)Math.Round(Math.Pow(g, info.Gamma[channel]) * 255, MidpointRounding.AwayFromZero);
    }

    /// <summary>Samples the map bilinearly at (<paramref name="x"/>, <paramref name="y"/>), clamped at the edges.</summary>
    private static void Sample(byte[] source, int width, int height, int channels, double x, double y, Span<byte> target)
    {
        x = Math.Clamp(x, 0, width - 1);
        y = Math.Clamp(y, 0, height - 1);
        int x0 = (int)x, y0 = (int)y, x1 = Math.Min(x0 + 1, width - 1), y1 = Math.Min(y0 + 1, height - 1);
        double wx = x - x0, wy = y - y0;
        for (int ch = 0; ch < channels; ch++)
        {
            double top = (source[(((y0 * width) + x0) * channels) + ch] * (1 - wx)) + (source[(((y0 * width) + x1) * channels) + ch] * wx);
            double bottom = (source[(((y1 * width) + x0) * channels) + ch] * (1 - wx)) + (source[(((y1 * width) + x1) * channels) + ch] * wx);
            target[ch] = (byte)Math.Round((top * (1 - wy)) + (bottom * wy), MidpointRounding.AwayFromZero);
        }
    }
}
