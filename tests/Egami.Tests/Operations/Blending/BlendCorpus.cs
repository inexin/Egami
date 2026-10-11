using Egami.Pixels;

namespace Egami.Tests.Operations.Blending;

/// <summary>
/// The layer-pair corpus for the 2.5.1 blend, mask and keying gates, generated in code so nothing is committed as an image.
/// Every image is straight 8-bit RGBA, row by row. egami-compare links this file, so the libraries it compares see the same pixels.
/// </summary>
internal static class BlendCorpus
{
    /// <summary>The (source alpha, destination alpha) levels of the pair grid.</summary>
    internal static readonly (byte Source, byte Destination)[] AlphaLevels = [(255, 255), (160, 255), (255, 96), (160, 96)];

    /// <summary>The side of the pair grid: one pixel per 8-bit value pair.</summary>
    internal const int GridSide = 256;

    /// <summary>
    /// Returns a 256 × 256 source and destination where pixel (x, y) pairs source value x with destination value y in red, and
    /// other mixes of the same pair in green and blue, so every 8-bit pair is blended at least once per channel position. The
    /// alpha of each image is constant, one of <see cref="AlphaLevels"/>.
    /// </summary>
    internal static (Rgba8[] Source, Rgba8[] Destination) PairGrid(int alphaLevel)
    {
        (byte sa, byte da) = AlphaLevels[alphaLevel];
        Rgba8[] source = new Rgba8[GridSide * GridSide];
        Rgba8[] destination = new Rgba8[GridSide * GridSide];
        for (int y = 0; y < GridSide; y++)
        {
            for (int x = 0; x < GridSide; x++)
            {
                int i = (y * GridSide) + x;
                source[i] = new Rgba8((byte)x, (byte)(255 - x), (byte)(x ^ 0x55), sa);
                destination[i] = new Rgba8((byte)y, (byte)(y ^ 0xAA), (byte)(255 - y), da);
            }
        }

        return (source, destination);
    }

    /// <summary>
    /// Returns a layer pair of the given size: a destination with an opaque color gradient, a fully transparent block and a
    /// half-transparent band; a source with a soft-edged disc, a hard-edged bar, an alpha ramp and a fully transparent area.
    /// </summary>
    internal static (Rgba8[] Source, Rgba8[] Destination) Layers(int width, int height)
    {
        Rgba8[] source = new Rgba8[width * height];
        Rgba8[] destination = new Rgba8[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width) + x;
                destination[i] = Under(x, y, width, height);
                source[i] = Over(x, y, width, height);
            }
        }

        return (source, destination);
    }

    /// <summary>Returns a gray mask of the given size: a smooth horizontal ramp with a hard-edged block of full coverage and one of none.</summary>
    internal static byte[] Mask(int width, int height)
    {
        byte[] mask = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte value = (byte)((x * 255) / Math.Max(1, width - 1));
                if (y < height / 4 && x < width / 3)
                {
                    value = 255;
                }
                else if (y >= height * 3 / 4 && x >= width * 2 / 3)
                {
                    value = 0;
                }

                mask[(y * width) + x] = value;
            }
        }

        return mask;
    }

    private static Rgba8 Under(int x, int y, int width, int height)
    {
        // A fully transparent block in the top-right corner.
        if (x >= width * 3 / 4 && y < height / 4)
        {
            return new Rgba8(0, 0, 0, 0);
        }

        byte r = (byte)((x * 255) / Math.Max(1, width - 1));
        byte g = (byte)((y * 255) / Math.Max(1, height - 1));
        byte b = (byte)(255 - ((x + y) * 255 / Math.Max(1, width + height - 2)));

        // A half-transparent band across the middle.
        byte a = y >= height * 2 / 5 && y < height * 3 / 5 ? (byte)128 : (byte)255;
        return new Rgba8(r, g, b, a);
    }

    private static Rgba8 Over(int x, int y, int width, int height)
    {
        // A fully transparent strip down the left edge.
        if (x < width / 10)
        {
            return new Rgba8(0, 0, 0, 0);
        }

        // A disc with an 8-pixel soft edge.
        double cx = width * 0.6, cy = height * 0.4, radius = Math.Min(width, height) * 0.3;
        double d = Math.Sqrt(((x + 0.5 - cx) * (x + 0.5 - cx)) + ((y + 0.5 - cy) * (y + 0.5 - cy)));
        if (d < radius)
        {
            double edge = Math.Clamp((radius - d) / 8, 0, 1);
            return new Rgba8(230, 60, 40, (byte)Math.Round(255 * edge));
        }

        // A hard-edged opaque bar.
        if (x >= width / 5 && x < width * 2 / 5 && y >= height / 8 && y < height * 5 / 8)
        {
            return new Rgba8(40, 190, 90, 255);
        }

        // An alpha ramp along the bottom, with a hue sweep.
        if (y >= height * 3 / 4)
        {
            byte t = (byte)((x * 255) / Math.Max(1, width - 1));
            return new Rgba8((byte)(255 - t), 128, t, t);
        }

        return new Rgba8(200, 200, 60, 64);
    }
}
