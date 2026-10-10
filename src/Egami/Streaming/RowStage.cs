using System.Numerics;

using Egami.Pixels;

namespace Egami.Streaming;

/// <summary>
/// Turns a source's rows into output rows: crops to the plan's region, averages Factor × Factor blocks, and converts to the
/// caller's pixel type. Holds one accumulator row when it reduces, nothing otherwise.
/// </summary>
internal sealed class RowStage(RowSource source, ReadPlan plan)
{
    private double[]? _sums;
    private RgbaF32[]? _line;

    /// <summary>Gets the next output row of the current frame.</summary>
    public int Row { get; private set; }

    /// <summary>Gets the output rows left in the current frame.</summary>
    public int Remaining => plan.Output.Height - Row;

    /// <summary>Gets the bytes the stage holds while reducing.</summary>
    public long WorkingBytes => plan.Factor == 1 ? 0 : (plan.Output.Width * 4L * sizeof(double)) + (plan.Region.Width * 16L);

    /// <summary>Starts the stage over for a new frame.</summary>
    public void Reset()
    {
        Row = 0;
    }

    /// <summary>Moves past output rows without producing them, decoding only where the source must.</summary>
    public void Skip(int count)
    {
        Row += Math.Min(count, Remaining);
    }

    /// <summary>Writes the next output row into <paramref name="destination"/>, which is the output width.</summary>
    public void Read<TPixel>(Span<TPixel> destination)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Rect region = plan.Region;
        int factor = plan.Factor;
        if (factor == 1)
        {
            source.ReadRow(region.Y + Row, region.X, destination);
            Row++;
            return;
        }

        int width = plan.Output.Width;
        double[] sums = _sums ??= new double[width * 4];
        RgbaF32[] line = _line ??= new RgbaF32[region.Width];
        Array.Clear(sums);
        int top = region.Y + (Row * factor);
        int rows = Math.Min(factor, region.Bottom - top);
        for (int y = top; y < top + rows; y++)
        {
            source.ReadRow(y, region.X, line.AsSpan());
            Accumulate(line, sums, factor);
        }

        for (int x = 0; x < width; x++)
        {
            int columns = Math.Min(factor, region.Width - (x * factor));
            destination[x] = TPixel.FromScaled(Average(sums.AsSpan(x * 4, 4), columns * rows));
        }

        Row++;
    }

    /// <summary>Adds a row to the block sums: color weighted by alpha, and alpha.</summary>
    internal static void Accumulate(ReadOnlySpan<RgbaF32> line, Span<double> sums, int factor)
    {
        for (int x = 0; x < line.Length; x++)
        {
            RgbaF32 p = line[x];
            Span<double> sum = sums.Slice(x / factor * 4, 4);
            double a = p.A;
            sum[0] += p.R * a;
            sum[1] += p.G * a;
            sum[2] += p.B * a;
            sum[3] += a;
        }
    }

    /// <summary>Returns the average of a block from its sums: alpha-weighted color, and mean alpha over <paramref name="count"/> pixels.</summary>
    internal static Vector4 Average(ReadOnlySpan<double> sum, int count)
    {
        double alpha = sum[3];
        if (alpha <= 0)
        {
            return Vector4.Zero;
        }

        return new Vector4((float)(sum[0] / alpha), (float)(sum[1] / alpha), (float)(sum[2] / alpha), (float)(alpha / count));
    }
}
