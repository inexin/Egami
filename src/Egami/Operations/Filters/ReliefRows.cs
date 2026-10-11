using System.Runtime.InteropServices;

using Egami.Pixels;

namespace Egami.Operations.Filters;

/// <summary>Row helpers shared by the relief and lighting filters: straight float rows, and the height channel taken from them.</summary>
internal static class ReliefRows
{
    /// <summary>The Rec. 709 luminance weights, as ImageMagick's default pixel intensity has them.</summary>
    internal const double Red = 0.212656, Green = 0.715158, Blue = 0.072186;

    /// <summary>Converts a row to straight floats, four per pixel; CMYK types become RGB.</summary>
    internal static void LoadStraight<TPixel>(ReadOnlySpan<TPixel> row, Span<float> floats)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        PixelConvert.Convert(row, MemoryMarshal.Cast<float, RgbaF32>(floats)[..row.Length]);
    }

    /// <summary>Converts straight floats, four per pixel, back to a row.</summary>
    internal static void StoreStraight<TPixel>(ReadOnlySpan<float> floats, Span<TPixel> row)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        PixelConvert.Convert<RgbaF32, TPixel>(MemoryMarshal.Cast<float, RgbaF32>(floats)[..row.Length], row);
    }

    /// <summary>Writes each pixel's height (alpha, or the clamped Rec. 709 luminance of the straight color) from straight floats.</summary>
    internal static void Heights(ReadOnlySpan<float> straight, Span<float> heights, bool luminance)
    {
        for (int x = 0; x < heights.Length; x++)
        {
            int i = x * 4;
            heights[x] = luminance
                ? Math.Clamp(((float)Red * straight[i]) + ((float)Green * straight[i + 1]) + ((float)Blue * straight[i + 2]), 0f, 1f)
                : straight[i + 3];
        }
    }
}
