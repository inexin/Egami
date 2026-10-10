namespace Egami.Streaming;

/// <summary>
/// What a decode with <see cref="Formats.ReaderOptions.Region"/> and <see cref="Formats.ReaderOptions.MinimumSize"/> produces:
/// the reduction the reader applies itself (JPEG's IDCT scaling), the region in the reader's scaled pixels, and the box factor
/// the row stage averages by.
/// </summary>
/// <param name="NativeScale">The reduction the reader applies while decoding: 1, or 2, 4 or 8 for JPEG.</param>
/// <param name="ScaledSize">The size the reader produces at <paramref name="NativeScale"/>.</param>
/// <param name="Region">The rectangle kept, in the reader's scaled pixels.</param>
/// <param name="Factor">The box factor: each output pixel averages a Factor × Factor block of the region.</param>
internal sealed record ReadPlan(int NativeScale, Size ScaledSize, Rect Region, int Factor)
{
    /// <summary>Gets the output size: the region divided by the factor, rounded up.</summary>
    public Size Output => new(Ceiling(Region.Width, Factor), Ceiling(Region.Height, Factor));

    /// <summary>Gets whether the output differs from the reader's whole image, so metadata about the stored layout no longer holds.</summary>
    public bool ChangesGeometry => Factor > 1 || Region != new Rect(0, 0, ScaledSize.Width, ScaledSize.Height);

    /// <summary>Plans a decode of a <paramref name="width"/> by <paramref name="height"/> image.</summary>
    /// <param name="width">The stored width.</param>
    /// <param name="height">The stored height.</param>
    /// <param name="region">The requested rectangle in stored pixels, or <see langword="null"/> for the whole image.</param>
    /// <param name="minimum">The smallest acceptable output, or <see langword="null"/> for no reduction.</param>
    /// <param name="maxNativeScale">The largest power-of-two reduction the reader can apply itself; 1 if none.</param>
    /// <exception cref="ArgumentOutOfRangeException">The region has no area or misses the image.</exception>
    public static ReadPlan Create(int width, int height, Rect? region, Size? minimum, int maxNativeScale)
    {
        Rect stored = Clip(width, height, region);
        int factor = FactorFor(stored.Size, minimum);
        int scale = 1;
        while (scale * 2 <= Math.Min(factor, maxNativeScale))
        {
            scale *= 2;
        }

        if (scale == 1)
        {
            return new ReadPlan(1, new Size(width, height), stored, factor);
        }

        // The scaled region covers every scaled pixel the stored region touches, which is how a scaled Load cropped to the
        // scaled rectangle sees it. Only at the reader's largest scale does a box reduction follow.
        Size scaled = new(Ceiling(width, scale), Ceiling(height, scale));
        int left = stored.X / scale;
        int top = stored.Y / scale;
        Rect inScaled = new(left, top, Math.Min(scaled.Width, Ceiling(stored.Right, scale)) - left, Math.Min(scaled.Height, Ceiling(stored.Bottom, scale)) - top);
        return new ReadPlan(scale, scaled, inScaled, scale == maxNativeScale ? FactorFor(inScaled.Size, minimum) : 1);
    }

    /// <summary>Returns the largest whole factor N that keeps ceil(size / N) at least <paramref name="minimum"/> in both directions.</summary>
    internal static int FactorFor(Size size, Size? minimum)
    {
        if (minimum is not Size m || m.Width < 1 || m.Height < 1)
        {
            return 1;
        }

        // Beyond the longer side every factor gives a single pixel, so the factor stops there.
        int factor = Math.Min(FactorFor(size.Width, m.Width), FactorFor(size.Height, m.Height));
        return Math.Clamp(factor, 1, Math.Max(size.Width, size.Height));
    }

    private static int FactorFor(int length, int minimum)
    {
        // ceil(length / N) >= minimum  <=>  length > (minimum - 1) * N  <=>  N <= (length - 1) / (minimum - 1).
        return minimum <= 1 ? int.MaxValue : (length - 1) / (minimum - 1);
    }

    private static Rect Clip(int width, int height, Rect? region)
    {
        if (region is not Rect r)
        {
            return new Rect(0, 0, width, height);
        }

        if (r.IsEmpty)
        {
            throw new ArgumentOutOfRangeException(nameof(region), r, "The region has no area.");
        }

        int left = Math.Max(0, r.X);
        int top = Math.Max(0, r.Y);
        long right = Math.Min(width, (long)r.X + r.Width);
        long bottom = Math.Min(height, (long)r.Y + r.Height);
        if (right <= left || bottom <= top)
        {
            throw new ArgumentOutOfRangeException(nameof(region), r, $"The region misses the {width}×{height} image.");
        }

        return new Rect(left, top, (int)right - left, (int)bottom - top);
    }

    private static int Ceiling(int value, int divisor)
    {
        return (int)(((long)value + divisor - 1) / divisor);
    }
}
