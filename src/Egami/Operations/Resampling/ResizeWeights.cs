namespace Egami.Operations.Resampling;

/// <summary>The filter kernels, from their textbook definitions. Each takes a distance in source pixels at scale 1.</summary>
internal static class ResampleKernels
{
    /// <summary>Returns how far from its center the kernel is non-zero, in source pixels at scale 1.</summary>
    internal static double Support(ResampleFilter filter)
    {
        return filter switch
        {
            ResampleFilter.Nearest => 0,
            ResampleFilter.Box => 0.5,
            ResampleFilter.Bilinear => 1,
            ResampleFilter.Bicubic or ResampleFilter.Mitchell => 2,
            ResampleFilter.Lanczos3 => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(filter), filter, null),
        };
    }

    /// <summary>Evaluates the kernel at distance <paramref name="x"/>.</summary>
    internal static double Evaluate(ResampleFilter filter, double x)
    {
        x = Math.Abs(x);
        switch (filter)
        {
            case ResampleFilter.Box:
                return x < 0.5 ? 1 : x == 0.5 ? 0.5 : 0;
            case ResampleFilter.Bilinear:
                return x < 1 ? 1 - x : 0;
            case ResampleFilter.Bicubic:
                return Cubic(x, b: 0, c: 0.5);
            case ResampleFilter.Mitchell:
                return Cubic(x, b: 1 / 3.0, c: 1 / 3.0);
            case ResampleFilter.Lanczos3:
                return x < 3 ? Sinc(x) * Sinc(x / 3) : 0;
            default:
                return x < 0.5 ? 1 : 0;
        }
    }

    /// <summary>The Mitchell–Netravali family of cubics; B = 0, C = 0.5 is Keys with a = −0.5 (Catmull-Rom).</summary>
    private static double Cubic(double x, double b, double c)
    {
        if (x < 1)
        {
            return (((12 - (9 * b) - (6 * c)) * x * x * x) + ((-18 + (12 * b) + (6 * c)) * x * x) + (6 - (2 * b))) / 6;
        }

        if (x < 2)
        {
            return (((-b - (6 * c)) * x * x * x) + (((6 * b) + (30 * c)) * x * x) + (((-12 * b) - (48 * c)) * x) + ((8 * b) + (24 * c))) / 6;
        }

        return 0;
    }

    private static double Sinc(double x)
    {
        if (x == 0)
        {
            return 1;
        }

        double px = Math.PI * x;
        return Math.Sin(px) / px;
    }
}

/// <summary>
/// The weights of one resize axis: for each output position, the first source index and a run of weights that sum to 1.
/// One table serves every frame and every band.
/// </summary>
internal sealed class ResizeWeights
{
    private ResizeWeights(int[] first, int[] count, float[] weights, int stride)
    {
        First = first;
        Count = count;
        Weights = weights;
        Stride = stride;
    }

    /// <summary>Gets the first source index for each output position. Never decreases.</summary>
    internal int[] First { get; }

    /// <summary>Gets the number of taps for each output position.</summary>
    internal int[] Count { get; }

    /// <summary>Gets the weights, <see cref="Stride"/> per output position, zero past each position's count.</summary>
    internal float[] Weights { get; }

    /// <summary>Gets the most taps any output position has.</summary>
    internal int Stride { get; }

    /// <summary>Gets the weights of output position <paramref name="i"/>.</summary>
    internal ReadOnlySpan<float> Row(int i)
    {
        return Weights.AsSpan(i * Stride, Count[i]);
    }

    /// <summary>
    /// Builds the weights for resizing the source span starting at <paramref name="offset"/> and <paramref name="extent"/> pixels long
    /// (both may be fractional) to <paramref name="size"/> output pixels. Taps outside <paramref name="low"/>..<paramref name="high"/>
    /// are folded onto the nearest edge pixel.
    /// </summary>
    internal static ResizeWeights Build(ResampleFilter filter, int size, double offset, double extent, int low, int high)
    {
        double scale = size / extent;
        int[] first = new int[size];
        int[] count = new int[size];
        if (filter == ResampleFilter.Nearest)
        {
            for (int i = 0; i < size; i++)
            {
                first[i] = Math.Clamp((int)Math.Floor(offset + ((i + 0.5) / scale)), low, high);
                count[i] = 1;
            }

            float[] ones = new float[size];
            ones.AsSpan().Fill(1f);
            return new ResizeWeights(first, count, ones, 1);
        }

        // Shrinking stretches the kernel so every source pixel contributes.
        double stretch = Math.Max(1.0, 1.0 / scale);
        double support = ResampleKernels.Support(filter) * stretch;
        int stride = Math.Min((int)Math.Ceiling(support * 2) + 1, high - low + 1);
        float[] weights = new float[size * stride];
        double[] buffer = new double[stride];
        for (int i = 0; i < size; i++)
        {
            double center = offset + ((i + 0.5) / scale) - 0.5;
            int left = (int)Math.Ceiling(center - support);
            int right = (int)Math.Floor(center + support);
            int start = Math.Clamp(left, low, high);
            int end = Math.Clamp(right, low, high);
            int n = end - start + 1;
            Array.Clear(buffer);
            double sum = 0;
            for (int j = left; j <= right; j++)
            {
                double w = ResampleKernels.Evaluate(filter, (j - center) / stretch);
                buffer[Math.Clamp(j, low, high) - start] += w;
                sum += w;
            }

            if (sum == 0)
            {
                // Only reachable for Box exactly between pixels at extreme ratios: take the nearest pixel.
                buffer[Math.Clamp((int)Math.Round(center), start, end) - start] = 1;
                sum = 1;
            }

            Span<float> row = weights.AsSpan(i * stride, n);
            float total = 0;
            for (int k = 0; k < n; k++)
            {
                row[k] = (float)(buffer[k] / sum);
                total += row[k];
            }

            // Push the float rounding error onto the largest weight so the row sums to 1 in float.
            int largest = 0;
            for (int k = 1; k < n; k++)
            {
                if (row[k] > row[largest])
                {
                    largest = k;
                }
            }

            row[largest] += 1f - total;
            first[i] = start;
            count[i] = n;
        }

        return new ResizeWeights(first, count, weights, stride);
    }
}
