namespace Egami.Quantization;

/// <summary>
/// A custom error-diffusion kernel for <see cref="PaletteOptions.Kernel"/>: how much of each pixel's error goes to which
/// neighbours. Row 0 of <see cref="Weights"/> is the current row, with the current pixel at column <see cref="Origin"/>; the
/// following rows are the rows below. Only entries to the right of the current pixel on row 0, and every entry below, may be
/// non-zero, since the rest have been mapped already.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Palettes.cs" region="kernel"/>
/// </example>
public sealed class ErrorDiffusionKernel
{
    private readonly int[,] _weights;

    /// <summary>Creates a kernel.</summary>
    /// <param name="weights">The weights, at most 8 rows and 16 columns; row 0 is the current row.</param>
    /// <param name="divisor">What the weights are divided by: their sum spreads the whole error, a larger divisor less of it.</param>
    /// <param name="origin">The column of <paramref name="weights"/> where the current pixel is.</param>
    /// <exception cref="ArgumentNullException"><paramref name="weights"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The weights are empty, too large or negative, put weight on the current pixel or before it, or sum to more than the divisor.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="divisor"/> is less than 1, or <paramref name="origin"/> is outside the columns.</exception>
    public ErrorDiffusionKernel(int[,] weights, int divisor, int origin)
    {
        ArgumentNullException.ThrowIfNull(weights);
        int rows = weights.GetLength(0), columns = weights.GetLength(1);
        if (rows is < 1 or > 8 || columns is < 1 or > 16)
        {
            throw new ArgumentException($"The weights must have 1 to 8 rows and 1 to 16 columns; they have {rows} and {columns}.", nameof(weights));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(divisor, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(origin);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(origin, columns);
        long sum = 0;
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < columns; c++)
            {
                int w = weights[r, c];
                if (w < 0)
                {
                    throw new ArgumentException($"Weight [{r}, {c}] is negative.", nameof(weights));
                }

                if (r == 0 && c <= origin && w != 0)
                {
                    throw new ArgumentException($"Weight [0, {c}] falls on the current pixel or one already mapped.", nameof(weights));
                }

                sum += w;
            }
        }

        if (sum > divisor)
        {
            throw new ArgumentException($"The weights sum to {sum}, more than the divisor {divisor}, so errors would grow.", nameof(weights));
        }

        _weights = (int[,])weights.Clone();
        Divisor = divisor;
        Origin = origin;
        Taps = Build(_weights, divisor, origin);
    }

    /// <summary>Gets a copy of the weights.</summary>
    public int[,] Weights => (int[,])_weights.Clone();

    /// <summary>Gets the divisor.</summary>
    public int Divisor { get; }

    /// <summary>Gets the column of the current pixel.</summary>
    public int Origin { get; }

    /// <summary>Gets the non-zero weights as offsets and fractions.</summary>
    internal (int Dx, int Dy, double Weight)[] Taps { get; }

    /// <summary>Gets how many rows below the current one the kernel reaches.</summary>
    internal int Depth => _weights.GetLength(0) - 1;

    /// <summary>Gets how far left and right the kernel reaches.</summary>
    internal int Reach => Math.Max(Origin, _weights.GetLength(1) - 1 - Origin);

    private static (int, int, double)[] Build(int[,] weights, int divisor, int origin)
    {
        List<(int, int, double)> taps = [];
        for (int r = 0; r < weights.GetLength(0); r++)
        {
            for (int c = 0; c < weights.GetLength(1); c++)
            {
                if (weights[r, c] != 0)
                {
                    taps.Add((c - origin, r, (double)weights[r, c] / divisor));
                }
            }
        }

        return [.. taps];
    }
}

/// <summary>The published error-diffusion kernels and ordered threshold maps behind <see cref="DitherMode"/>.</summary>
internal static class DitherKernels
{
    private static readonly Dictionary<DitherMode, ErrorDiffusionKernel> Diffusion = new()
    {
        // Floyd and Steinberg 1976.
        [DitherMode.FloydSteinberg] = new(new[,] { { 0, 0, 7 }, { 3, 5, 1 } }, 16, 1),

        // Jarvis, Judice and Ninke 1976.
        [DitherMode.JarvisJudiceNinke] = new(new[,] { { 0, 0, 0, 7, 5 }, { 3, 5, 7, 5, 3 }, { 1, 3, 5, 3, 1 } }, 48, 2),

        // Stucki 1981.
        [DitherMode.Stucki] = new(new[,] { { 0, 0, 0, 8, 4 }, { 2, 4, 8, 4, 2 }, { 1, 2, 4, 2, 1 } }, 42, 2),

        // Burkes 1988.
        [DitherMode.Burkes] = new(new[,] { { 0, 0, 0, 8, 4 }, { 2, 4, 8, 4, 2 } }, 32, 2),

        // Sierra 1989: three rows, two rows, and the "Filter Lite".
        [DitherMode.Sierra3] = new(new[,] { { 0, 0, 0, 5, 3 }, { 2, 4, 5, 4, 2 }, { 0, 2, 3, 2, 0 } }, 32, 2),
        [DitherMode.Sierra2] = new(new[,] { { 0, 0, 0, 4, 3 }, { 1, 2, 3, 2, 1 } }, 16, 2),
        [DitherMode.SierraLite] = new(new[,] { { 0, 0, 2 }, { 1, 1, 0 } }, 4, 1),

        // Atkinson (Apple, 1984): spreads only six eighths, so flat areas keep more contrast.
        [DitherMode.Atkinson] = new(new[,] { { 0, 0, 1, 1 }, { 1, 1, 1, 0 }, { 0, 1, 0, 0 } }, 8, 1),

        // Stevenson and Arce 1985, for hexagonal grids.
        [DitherMode.StevensonArce] = new(new[,]
        {
            { 0, 0, 0, 0, 0, 32, 0 },
            { 12, 0, 26, 0, 30, 0, 16 },
            { 0, 12, 0, 26, 0, 12, 0 },
            { 5, 0, 12, 0, 12, 0, 5 },
        }, 200, 3),
    };

    /// <summary>Returns the kernel of an error-diffusion mode, or null for the others.</summary>
    internal static ErrorDiffusionKernel? Kernel(DitherMode mode) => Diffusion.GetValueOrDefault(mode);

    /// <summary>Returns the threshold map of an ordered mode (values 0 to n² − 1) and its side n, or null for the others.</summary>
    internal static (byte[] Map, int Size)? Ordered(DitherMode mode) => mode switch
    {
        DitherMode.Bayer2x2 => (Bayer(2), 2),
        DitherMode.Bayer4x4 => (Bayer(4), 4),
        DitherMode.Ordered => (FrameMapper.Bayer.ToArray(), 8),
        DitherMode.Bayer16x16 => (Bayer(16), 16),
        DitherMode.Ordered3x3 => ([0, 7, 3, 6, 5, 2, 4, 1, 8], 3),
        _ => null,
    };

    /// <summary>The n × n Bayer matrix, built as M₂ₙ = [[4M, 4M + 2], [4M + 3, 4M + 1]] from M₁ = [0].</summary>
    internal static byte[] Bayer(int size)
    {
        int[] m = [0];
        for (int n = 1; n < size; n *= 2)
        {
            int[] next = new int[4 * n * n];
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    int v = 4 * m[(y * n) + x];
                    next[(y * 2 * n) + x] = v;
                    next[(y * 2 * n) + x + n] = v + 2;
                    next[((y + n) * 2 * n) + x] = v + 3;
                    next[((y + n) * 2 * n) + x + n] = v + 1;
                }
            }

            m = next;
        }

        return [.. m.Select(v => (byte)v)];
    }
}
