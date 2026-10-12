using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;

using Egami.Pixels;

namespace Egami.Svg.Rendering;

/// <summary>
/// feGaussianBlur as SVG 1.1 section 15.17 describes it, on premultiplied colors with transparent black outside the raster: for a
/// deviation of 2 pixels or more, three box blurs of width d = ⌊s·3·√(2π)/4 + 0.5⌋ (an even d shifts the first two half a pixel
/// each way and widens the third), as browsers and resvg blur; below that, the Gaussian kernel itself. Each axis is blurred
/// separately, in place.
/// </summary>
internal static class SvgGaussianBlur
{
    /// <summary>Blurs straight-alpha pixels (width × height, row by row) by the deviations along x and y, in pixels.</summary>
    public static void Blur(RgbaF32[] pixels, int width, int height, float sigmaX, float sigmaY)
    {
        Span<Vector4> data = MemoryMarshal.Cast<RgbaF32, Vector4>(pixels.AsSpan(0, width * height));
        for (int i = 0; i < data.Length; i++)
        {
            Vector4 p = data[i];
            data[i] = new Vector4(p.X * p.W, p.Y * p.W, p.Z * p.W, p.W);
        }

        int longest = Math.Max(width, height);
        Vector4[] line = ArrayPool<Vector4>.Shared.Rent(longest);
        Vector4[] scratch = ArrayPool<Vector4>.Shared.Rent(longest);
        try
        {
            if (sigmaX > 0)
            {
                for (int y = 0; y < height; y++)
                {
                    Span<Vector4> row = data.Slice(y * width, width);
                    row.CopyTo(line);
                    BlurLine(line.AsSpan(0, width), scratch.AsSpan(0, width), sigmaX);
                    line.AsSpan(0, width).CopyTo(row);
                }
            }

            if (sigmaY > 0)
            {
                for (int x = 0; x < width; x++)
                {
                    for (int y = 0; y < height; y++)
                    {
                        line[y] = data[(y * width) + x];
                    }

                    BlurLine(line.AsSpan(0, height), scratch.AsSpan(0, height), sigmaY);
                    for (int y = 0; y < height; y++)
                    {
                        data[(y * width) + x] = line[y];
                    }
                }
            }
        }
        finally
        {
            ArrayPool<Vector4>.Shared.Return(line);
            ArrayPool<Vector4>.Shared.Return(scratch);
        }

        for (int i = 0; i < data.Length; i++)
        {
            Vector4 p = data[i];
            float a = Math.Clamp(p.W, 0, 1);
            data[i] = a > 0 ? new Vector4(Math.Clamp(p.X / a, 0, 1), Math.Clamp(p.Y / a, 0, 1), Math.Clamp(p.Z / a, 0, 1), a) : Vector4.Zero;
        }
    }

    private static void BlurLine(Span<Vector4> line, Span<Vector4> scratch, float sigma)
    {
        if (sigma >= 2)
        {
            int d = (int)Math.Floor((sigma * 3 * Math.Sqrt(2 * Math.PI) / 4) + 0.5);
            if (d % 2 == 1)
            {
                Box(line, scratch, (d - 1) / 2, (d - 1) / 2);
                Box(scratch, line, (d - 1) / 2, (d - 1) / 2);
                Box(line, scratch, (d - 1) / 2, (d - 1) / 2);
            }
            else
            {
                Box(line, scratch, d / 2, (d / 2) - 1);
                Box(scratch, line, (d / 2) - 1, d / 2);
                Box(line, scratch, d / 2, d / 2);
            }

            scratch.CopyTo(line);
            return;
        }

        Gaussian(line, scratch, sigma);
        scratch.CopyTo(line);
    }

    /// <summary>The mean of the window from <paramref name="left"/> before each pixel to <paramref name="right"/> after it.</summary>
    private static void Box(ReadOnlySpan<Vector4> source, Span<Vector4> destination, int left, int right)
    {
        int n = source.Length;
        float scale = 1f / (left + right + 1);
        Vector4 sum = Vector4.Zero;
        for (int i = 0; i <= Math.Min(right, n - 1); i++)
        {
            sum += source[i];
        }

        for (int i = 0; i < n; i++)
        {
            destination[i] = sum * scale;
            int add = i + right + 1, remove = i - left;
            if (add < n)
            {
                sum += source[add];
            }

            if (remove >= 0)
            {
                sum -= source[remove];
            }
        }
    }

    private static void Gaussian(ReadOnlySpan<Vector4> source, Span<Vector4> destination, float sigma)
    {
        int radius = (int)MathF.Ceiling(3 * sigma);
        Span<float> kernel = stackalloc float[(2 * radius) + 1];
        float total = 0;
        for (int k = -radius; k <= radius; k++)
        {
            total += kernel[k + radius] = MathF.Exp(-(k * k) / (2 * sigma * sigma));
        }

        for (int k = 0; k < kernel.Length; k++)
        {
            kernel[k] /= total;
        }

        int n = source.Length;
        for (int i = 0; i < n; i++)
        {
            Vector4 sum = Vector4.Zero;
            int from = Math.Max(-radius, -i), to = Math.Min(radius, n - 1 - i);
            for (int k = from; k <= to; k++)
            {
                sum += source[i + k] * kernel[k + radius];
            }

            destination[i] = sum;
        }
    }
}
