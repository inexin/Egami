using System.Buffers;
using System.Runtime.InteropServices;

using Egami.Pixels;

namespace Egami.Formats.Psd;

/// <summary>The flattener's mask feathering: a layer's user or vector mask blurred by its feather radius, in a pooled plane.</summary>
internal sealed partial class PsdFlattener
{
    /// <summary>Gets the Gaussian σ for a feather radius of one pixel, fitted to the user and vector masks of Photoshop-saved files.</summary>
    internal const double FeatherSigma = 1;

    private PsdMask? _featherFor;
    private float[]? _featherPlane;

    /// <summary>
    /// Returns the user mask's coverage over the canvas (its default color outside its bounds, thinned by its density) blurred
    /// by its feather, building it on the first row of a layer.
    /// </summary>
    private float[] Feathered(PsdMask mask)
    {
        if (ReferenceEquals(_featherFor, mask) && _featherPlane is float[] ready)
        {
            return ready;
        }

        ReleaseFeather();
        float[] plane = Allocate((long)_width * _height);
        float outside = 1 - (mask.Density * (1 - (mask.DefaultColor / 255f)));
        Rect bounds = mask.Bounds;
        int from = Math.Clamp(bounds.X, 0, _width), to = Math.Clamp(bounds.Right, 0, _width);
        Span<GrayF32> values = MemoryMarshal.Cast<float, GrayF32>((_maskRow ??= new float[_width]).AsSpan(0, Math.Max(0, to - from)));
        for (int y = 0; y < _height; y++)
        {
            Span<float> row = plane.AsSpan(y * _width, _width);
            row.Fill(outside);
            if (y < bounds.Y || y >= bounds.Bottom || from >= to)
            {
                continue;
            }

            mask.Pixels.ReadRow(y - bounds.Y, from - bounds.X, values);
            for (int i = 0; i < values.Length; i++)
            {
                row[from + i] = 1 - (mask.Density * (1 - Math.Clamp(values[i].L, 0, 1)));
            }
        }

        Blur(plane, _width, _height, mask.Feather * FeatherSigma);
        _featherFor = mask;
        _featherPlane = plane;
        return plane;
    }

    private void ReleaseFeather()
    {
        if (_featherPlane is not null)
        {
            Release(_featherPlane);
            _featherPlane = null;
        }

        _featherFor = null;
    }

    /// <summary>
    /// Blurs a plane of <paramref name="width"/> by <paramref name="height"/> values in place with a Gaussian of
    /// <paramref name="sigma"/>, the edge values repeated past the edges: exact weights for a small σ, three box passes of the
    /// widths that give the same variance otherwise.
    /// </summary>
    internal static void Blur(float[] plane, int width, int height, double sigma)
    {
        if (sigma <= 0 || width <= 0 || height <= 0)
        {
            return;
        }

        float[] line = ArrayPool<float>.Shared.Rent(Math.Max(width, height));
        float[] copy = ArrayPool<float>.Shared.Rent(Math.Max(width, height));
        try
        {
            if (sigma < 2)
            {
                float[] weights = Operations.Filters.ConvolutionOperation.GaussianWeights((float)sigma);
                Passes(plane, width, height, line, copy, (src, dst, n) => Convolve(src, dst, n, weights));
                return;
            }

            // Three boxes whose variances add up to σ²: m of width wl and the rest of width wl + 2.
            double ideal = Math.Sqrt((12 * sigma * sigma / 3) + 1);
            int wl = (int)Math.Floor(ideal);
            if (wl % 2 == 0)
            {
                wl--;
            }

            int m = (int)Math.Round(((12 * sigma * sigma) - (3 * wl * wl) - (12 * wl) - 9) / ((-4.0 * wl) - 4));
            for (int pass = 0; pass < 3; pass++)
            {
                int radius = ((pass < m ? wl : wl + 2) - 1) / 2;
                Passes(plane, width, height, line, copy, (src, dst, n) => Box(src, dst, n, radius));
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(line);
            ArrayPool<float>.Shared.Return(copy);
        }
    }

    /// <summary>Runs a one-dimensional filter over every row, then every column.</summary>
    private static void Passes(float[] plane, int width, int height, float[] line, float[] copy, Action<float[], float[], int> filter)
    {
        for (int y = 0; y < height; y++)
        {
            plane.AsSpan(y * width, width).CopyTo(copy);
            filter(copy, line, width);
            line.AsSpan(0, width).CopyTo(plane.AsSpan(y * width, width));
        }

        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                copy[y] = plane[(y * width) + x];
            }

            filter(copy, line, height);
            for (int y = 0; y < height; y++)
            {
                plane[(y * width) + x] = line[y];
            }
        }
    }

    private static void Convolve(float[] source, float[] target, int count, float[] weights)
    {
        int r = weights.Length / 2;
        for (int i = 0; i < count; i++)
        {
            float sum = 0;
            for (int k = -r; k <= r; k++)
            {
                sum += weights[k + r] * source[Math.Clamp(i + k, 0, count - 1)];
            }

            target[i] = sum;
        }
    }

    /// <summary>A box of 2 <paramref name="radius"/> + 1 values by a running sum, the edge values repeated.</summary>
    private static void Box(float[] source, float[] target, int count, int radius)
    {
        float scale = 1f / ((2 * radius) + 1);
        double sum = 0;
        for (int k = -radius; k <= radius; k++)
        {
            sum += source[Math.Clamp(k, 0, count - 1)];
        }

        for (int i = 0; i < count; i++)
        {
            target[i] = (float)(sum * scale);
            sum += source[Math.Min(i + radius + 1, count - 1)] - source[Math.Max(i - radius, 0)];
        }
    }
}
