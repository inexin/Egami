namespace Egami.Operations;

/// <summary>How a convolution reads taps that fall past the image border.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Effects.cs" region="filters"/>
/// </example>
public enum EdgeMode
{
    /// <summary>Repeats the edge pixel. The default.</summary>
    Clamp,

    /// <summary>Reflects the image at its border, repeating the edge pixel: <c>c b a | a b c | c b a</c>.</summary>
    Mirror,

    /// <summary>Continues from the opposite edge, as if the image were tiled.</summary>
    Wrap,

    /// <summary>Reads fully transparent black, so edges fade out.</summary>
    Transparent,
}

/// <summary>
/// A convolution kernel: an odd width and height of weights, applied as laid out (the weight at column 0, row 0 multiplies the
/// pixel up and to the left of the center). The sum is divided by <see cref="Divisor"/> and <see cref="Offset"/> is added to the
/// color channels.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Effects.cs" region="filters"/>
/// </example>
public sealed class ConvolutionKernel
{
    private readonly float[] _values;

    /// <summary>Creates a kernel from its weights, row by row.</summary>
    /// <param name="width">The width, an odd number from 1 to 255.</param>
    /// <param name="height">The height, an odd number from 1 to 255.</param>
    /// <param name="values">The weights, <paramref name="width"/> × <paramref name="height"/> of them, row by row.</param>
    /// <param name="divisor">What the weighted sum is divided by. Defaults to 1.</param>
    /// <param name="offset">What is added to red, green and blue afterwards, in 0..1 units. Defaults to 0.</param>
    /// <exception cref="ArgumentException">A size is even or out of range, the weight count is wrong, or a value is not finite.</exception>
    public ConvolutionKernel(int width, int height, ReadOnlySpan<float> values, float divisor = 1, float offset = 0)
        : this(width, height, values.ToArray(), null, null, divisor, offset)
    {
    }

    private ConvolutionKernel(int width, int height, float[] values, float[]? row, float[]? column, float divisor, float offset)
    {
        CheckSize(width, nameof(width));
        CheckSize(height, nameof(height));
        if (values.Length != width * height)
        {
            throw new ArgumentException($"A {width}×{height} kernel needs {width * height} weights, not {values.Length}.", nameof(values));
        }

        foreach (float v in values)
        {
            if (!float.IsFinite(v))
            {
                throw new ArgumentException("Every weight must be finite.", nameof(values));
            }
        }

        if (!float.IsFinite(divisor) || divisor == 0)
        {
            throw new ArgumentException("The divisor must be finite and not 0.", nameof(divisor));
        }

        if (!float.IsFinite(offset))
        {
            throw new ArgumentException("The offset must be finite.", nameof(offset));
        }

        Width = width;
        Height = height;
        _values = values;
        Row = row;
        Column = column;
        Divisor = divisor;
        Offset = offset;
    }

    /// <summary>Gets the width, an odd number.</summary>
    public int Width { get; }

    /// <summary>Gets the height, an odd number.</summary>
    public int Height { get; }

    /// <summary>Gets what the weighted sum is divided by.</summary>
    public float Divisor { get; }

    /// <summary>Gets what is added to red, green and blue after dividing, in 0..1 units.</summary>
    public float Offset { get; }

    /// <summary>Gets whether the kernel was built from a row and a column, so it runs as two 1D passes.</summary>
    internal bool IsSeparable => Row is not null;

    /// <summary>Gets the row weights of a separable kernel.</summary>
    internal float[]? Row { get; }

    /// <summary>Gets the column weights of a separable kernel.</summary>
    internal float[]? Column { get; }

    /// <summary>Gets one weight.</summary>
    /// <param name="x">The column, 0 to <see cref="Width"/> − 1.</param>
    /// <param name="y">The row, 0 to <see cref="Height"/> − 1.</param>
    /// <returns>The weight.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="x"/> or <paramref name="y"/> is out of range.</exception>
    public float this[int x, int y]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)x, (uint)Width, nameof(x));
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)y, (uint)Height, nameof(y));
            return _values[(y * Width) + x];
        }
    }

    /// <summary>
    /// Creates a separable kernel: weight (x, y) is <c>row[x] × column[y]</c>, and it runs as a horizontal pass and then a vertical
    /// one, costing <c>width + height</c> taps per pixel instead of <c>width × height</c>.
    /// </summary>
    /// <param name="row">The horizontal weights, an odd count.</param>
    /// <param name="column">The vertical weights, an odd count.</param>
    /// <param name="divisor">What the weighted sum is divided by. Defaults to 1.</param>
    /// <param name="offset">What is added to red, green and blue afterwards, in 0..1 units. Defaults to 0.</param>
    /// <returns>The kernel.</returns>
    /// <exception cref="ArgumentException">A count is even or out of range, or a value is not finite.</exception>
    public static ConvolutionKernel Separable(ReadOnlySpan<float> row, ReadOnlySpan<float> column, float divisor = 1, float offset = 0)
    {
        float[] values = new float[row.Length * column.Length];
        for (int y = 0; y < column.Length; y++)
        {
            for (int x = 0; x < row.Length; x++)
            {
                values[(y * row.Length) + x] = row[x] * column[y];
            }
        }

        return new ConvolutionKernel(row.Length, column.Length, values, row.ToArray(), column.ToArray(), divisor, offset);
    }

    /// <summary>Gets the weights, row by row.</summary>
    internal ReadOnlySpan<float> Values => _values;

    private static void CheckSize(int size, string name)
    {
        if (size < 1 || size > 255 || size % 2 == 0)
        {
            throw new ArgumentException($"Kernel sizes must be odd, from 1 to 255, not {size}.", name);
        }
    }
}
