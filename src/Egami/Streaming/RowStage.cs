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
    private int _accumulated = -1;

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
        _accumulated = -1;
    }

    /// <summary>Moves past output rows without producing them, decoding only where the source must.</summary>
    public void Skip(int count)
    {
        Row += Math.Min(count, Remaining);
    }

    /// <summary>
    /// Gets or sets the conversion to <see cref="Formats.ReaderOptions.ConvertColorTo"/>, run on each row in the natural pixel
    /// type after cropping and reducing, as a load followed by ConvertColor does.
    /// </summary>
    public ColorRows? Color { get; set; }

    /// <summary>Writes the next output row into <paramref name="destination"/>, which is the output width.</summary>
    public void Read<TPixel>(Span<TPixel> destination)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (Color is ColorRows color)
        {
            color.Read(this, destination);
            return;
        }

        ReadStored(destination);
    }

    /// <summary>Writes the next output row as stored: cropped, reduced and converted to the pixel type, with no color conversion.</summary>
    internal void ReadStored<TPixel>(Span<TPixel> destination)
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
        if (_accumulated < 0)
        {
            Array.Clear(sums);
            _accumulated = 0;
        }

        // The sums so far are kept if the source runs out of bytes, so an incremental decode carries on from the next row.
        int top = region.Y + (Row * factor);
        int rows = Math.Min(factor, region.Bottom - top);
        for (; _accumulated < rows; _accumulated++)
        {
            source.ReadRow(top + _accumulated, region.X, line.AsSpan());
            Accumulate(line, sums, factor);
        }

        _accumulated = -1;

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

/// <summary>Converts rows to <see cref="Formats.ReaderOptions.ConvertColorTo"/>; made for the source's natural pixel type.</summary>
internal abstract class ColorRows
{
    /// <summary>Reads the stage's next row in the natural type and converts it into <paramref name="destination"/>.</summary>
    public abstract void Read<TPixel>(RowStage stage, Span<TPixel> destination)
        where TPixel : unmanaged, IPixelType<TPixel>;

    /// <summary>Makes the conversion for a source whose natural type is the visited one.</summary>
    internal sealed class Factory(Colors.ColorProfile source, Colors.HdrMetadata? hdr, Colors.ColorProfile target, EgamiContext context) : INaturalTypeVisitor<ColorRows>
    {
        public ColorRows Visit<TNatural>()
            where TNatural : unmanaged, IPixelType<TNatural>
        {
            return new ColorRows<TNatural>(source, hdr, target, context);
        }
    }
}

/// <summary>
/// The conversion for natural type <typeparamref name="TNatural"/>: the same transform Raster.ConvertColor builds, from the
/// source's profile to the target at the destination's color model, kept per destination type.
/// </summary>
internal sealed class ColorRows<TNatural>(Colors.ColorProfile source, Colors.HdrMetadata? hdr, Colors.ColorProfile target, EgamiContext context) : ColorRows
    where TNatural : unmanaged, IPixelType<TNatural>
{
    private TNatural[] _row = [];
    private Type? _type;
    private Colors.ColorTransform? _transform;

    public override void Read<TPixel>(RowStage stage, Span<TPixel> destination)
    {
        if (_row.Length < destination.Length)
        {
            _row = new TNatural[destination.Length];
        }

        Span<TNatural> natural = _row.AsSpan(0, destination.Length);
        stage.ReadStored(natural);
        if (_type != typeof(TPixel))
        {
            Colors.ColorProfile from = Operations.Color.ConvertColorOperation.ForPixels(source, Colors.ColorTransform.ModelOf<TNatural>() ?? Colors.ColorModel.Rgb);
            Colors.ColorProfile to = Operations.Color.ConvertColorOperation.ForPixels(target, Colors.ColorTransform.ModelOf<TPixel>() ?? Colors.ColorModel.Rgb);
            _transform = Colors.ColorTransform.CreateFor(from, to, Colors.ColorTransformOptions.Default, hdr, context);
            _type = typeof(TPixel);
        }

        _transform!.Apply<TNatural, TPixel>(natural, destination);
    }
}
