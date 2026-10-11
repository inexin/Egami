using System.Numerics;
using System.Runtime.CompilerServices;

using Egami.Operations.Color;
using Egami.Operations.Tone;
using Egami.Pixels;

namespace Egami.Operations;

/// <summary>
/// A function of one pixel's color, as a value: build one from a matrix, curves, a 3D table or a tone step, chain filters with
/// <see cref="Then"/>, mix two with <see cref="Lerp"/>, run one with <see cref="RasterOps.ApplyFilter"/> or on your own pixels with
/// <see cref="Apply{TPixel}"/>, and save it as a .cube file through <see cref="ToLut3D"/>. Like Skia's SkColorFilter, a filter sees
/// only the pixel's own straight RGBA: no neighbours, position or image statistics, so any chain of filters runs as one pass.
/// </summary>
/// <remarks>
/// Filters work on the stored, gamma-encoded values, as photo editors and LUT files do, and clamp to 0..1 after each step, as the
/// color matrices do. Immutable and safe to share between threads.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Tone.cs" region="color-filter"/>
/// </example>
public sealed partial class ColorFilter
{
    private ColorFilter(FilterStage[] stages)
    {
        Stages = stages;
    }

    /// <summary>Gets the filter that changes nothing.</summary>
    public static ColorFilter Identity { get; } = new([]);

    /// <summary>Gets whether this filter can change alpha, which a 3D table cannot hold.</summary>
    public bool ChangesAlpha => Stages.Any(s => s.ChangesAlpha);

    /// <summary>Gets the stages, flattened and fused where that keeps the result.</summary>
    internal FilterStage[] Stages { get; }

    /// <summary>Gets whether the filter has no stage.</summary>
    internal bool IsIdentity => Stages.Length == 0;

    /// <summary>Returns a filter that applies a color matrix, as <see cref="RasterOps.ColorMatrix"/> does.</summary>
    /// <param name="matrix">The matrix.</param>
    /// <returns>The filter.</returns>
    public static ColorFilter Matrix(ColorMatrix matrix)
    {
        return matrix == Operations.ColorMatrix.Identity ? Identity : new([new MatrixStage(matrix)]);
    }

    /// <summary>Returns a filter that maps red, green and blue through one curve and keeps alpha.</summary>
    /// <param name="all">The curve for red, green and blue.</param>
    /// <returns>The filter.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="all"/> is <see langword="null"/>.</exception>
    public static ColorFilter Curves(ToneCurve all)
    {
        ArgumentNullException.ThrowIfNull(all);
        return Curves(all, all, all);
    }

    /// <summary>
    /// Returns a filter that maps each channel through its own curve, as ImageMagick's -clut and Skia's TableARGB do.
    /// </summary>
    /// <param name="red">The curve for red.</param>
    /// <param name="green">The curve for green.</param>
    /// <param name="blue">The curve for blue.</param>
    /// <param name="alpha">The curve for alpha, or <see langword="null"/> (the default) to keep alpha.</param>
    /// <returns>The filter.</returns>
    /// <exception cref="ArgumentNullException">A color curve is <see langword="null"/>.</exception>
    public static ColorFilter Curves(ToneCurve red, ToneCurve green, ToneCurve blue, ToneCurve? alpha = null)
    {
        ArgumentNullException.ThrowIfNull(red);
        ArgumentNullException.ThrowIfNull(green);
        ArgumentNullException.ThrowIfNull(blue);
        alpha ??= ToneCurve.Identity;
        return red.IsIdentity && green.IsIdentity && blue.IsIdentity && alpha.IsIdentity ? Identity : new([new CurveStage(red, green, blue, alpha)]);
    }

    /// <summary>Returns a filter that maps red, green and blue through a 3D table and keeps alpha.</summary>
    /// <param name="lut">The table.</param>
    /// <param name="interpolation">How the table is read between its grid points. Defaults to <see cref="LutInterpolation.Tetrahedral"/>.</param>
    /// <returns>The filter.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="lut"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="interpolation"/> is not defined.</exception>
    public static ColorFilter FromLut(Lut3D lut, LutInterpolation interpolation = LutInterpolation.Tetrahedral)
    {
        ArgumentNullException.ThrowIfNull(lut);
        if (!Enum.IsDefined(interpolation))
        {
            throw new ArgumentOutOfRangeException(nameof(interpolation), interpolation, "The interpolation is not defined.");
        }

        return new([new LutStage(lut, interpolation)]);
    }

    /// <summary>
    /// Returns a filter whose result is <paramref name="from"/>'s and <paramref name="to"/>'s mixed by <paramref name="amount"/>, as
    /// Skia's SkColorFilters::Lerp: <c>Lerp(Identity, look, 0.7f)</c> applies a look at 70%.
    /// </summary>
    /// <param name="from">The filter at amount 0.</param>
    /// <param name="to">The filter at amount 1.</param>
    /// <param name="amount">The mix, from 0 to 1.</param>
    /// <returns>The filter.</returns>
    /// <exception cref="ArgumentNullException">A filter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="amount"/> is outside 0..1.</exception>
    public static ColorFilter Lerp(ColorFilter from, ColorFilter to, float amount)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        CheckUnit(amount);
        return amount == 0 ? from : amount == 1 ? to : new([new LerpStage(from.Stages, to.Stages, amount)]);
    }

    /// <summary>Returns the filter that applies this one and then <paramref name="next"/>, clamping in between.</summary>
    /// <param name="next">The filter applied second.</param>
    /// <returns>The combined filter.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="next"/> is <see langword="null"/>.</exception>
    public ColorFilter Then(ColorFilter next)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (IsIdentity)
        {
            return next;
        }

        return next.IsIdentity ? this : new(Concat(Stages, next.Stages));
    }

    /// <summary>
    /// Applies the filter to <paramref name="pixels"/> in place, with the same arithmetic <see cref="RasterOps.ApplyFilter"/> uses
    /// for that pixel type.
    /// </summary>
    /// <typeparam name="TPixel">The pixel type.</typeparam>
    /// <param name="pixels">The pixels.</param>
    public void Apply<TPixel>(Span<TPixel> pixels)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (IsIdentity || pixels.IsEmpty)
        {
            return;
        }

        FilterProgram<TPixel> program = new(Stages);
        try
        {
            program.RunRow(pixels, pixels);
        }
        finally
        {
            program.Dispose();
        }
    }

    /// <summary>
    /// Samples the filter into a 3D table of <paramref name="size"/>³ points, for <see cref="CubeFile.Write(string, Lut3D, string?)"/>.
    /// A chain that ends in a 3D table and maps grid points to grid points comes back exactly; curves and matrices are sampled.
    /// </summary>
    /// <param name="size">The points per axis, from 2 to 256. Defaults to 33, the common .cube size.</param>
    /// <returns>The table.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="size"/> is outside 2 to 256.</exception>
    /// <exception cref="InvalidOperationException">The filter changes alpha, which a 3D table cannot hold.</exception>
    public Lut3D ToLut3D(int size = 33)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(size, Lut3D.MaxSize);
        if (ChangesAlpha)
        {
            throw new InvalidOperationException("The filter changes alpha, which a 3D table cannot hold.");
        }

        float[] values = new float[size * size * size * 3];
        Vector4[] row = new Vector4[size];
        using FilterContext context = new();
        float step = 1f / (size - 1);
        for (int b = 0; b < size; b++)
        {
            for (int g = 0; g < size; g++)
            {
                for (int r = 0; r < size; r++)
                {
                    row[r] = new Vector4(r * step, g * step, b * step, 1);
                }

                FilterStage.Run(Stages, row, context);
                int at = ((b * size) + g) * size * 3;
                for (int r = 0; r < size; r++)
                {
                    values[at + (r * 3)] = row[r].X;
                    values[at + (r * 3) + 1] = row[r].Y;
                    values[at + (r * 3) + 2] = row[r].Z;
                }
            }
        }

        return new Lut3D(size, values);
    }

    /// <summary>Returns the filter kept only in <paramref name="channels"/>, the others passing through.</summary>
    internal ColorFilter OnlyChannels(ColorChannels channels)
    {
        if ((channels & ColorChannels.All) == ColorChannels.All || IsIdentity)
        {
            return this;
        }

        if (channels == ColorChannels.None)
        {
            return Identity;
        }

        // Curves restrict exactly by leaving the other channels' curves out.
        if (Stages.Length == 1 && Stages[0] is CurveStage curves)
        {
            ReadOnlySpan<ToneCurve> c = curves.Curves;
            return new([new CurveStage(Pick(0, c[0]), Pick(1, c[1]), Pick(2, c[2]), Pick(3, c[3]))]);
        }

        return new([new SelectStage(Stages, channels)]);

        ToneCurve Pick(int channel, ToneCurve curve) => ((int)channels & (1 << channel)) != 0 ? curve : ToneCurve.Identity;
    }

    /// <summary>Wraps stages built by the factories.</summary>
    internal static ColorFilter FromStages(params FilterStage[] stages) => new(stages);

    /// <summary>Joins two stage lists, fusing at the join where the result is the same.</summary>
    internal static FilterStage[] Concat(FilterStage[] first, FilterStage[] second)
    {
        List<FilterStage> stages = [.. first];
        foreach (FilterStage stage in second)
        {
            FilterStage? last = stages.Count > 0 ? stages[^1] : null;
            if (last is MatrixStage a && stage is MatrixStage b && !ColorMatrixOperation.CanClip(a.Matrix))
            {
                stages[^1] = new MatrixStage(a.Matrix.Then(b.Matrix));
            }
            else if (last is CurveStage e && !e.ChangesAlpha && stage is LutStage lut)
            {
                // A curve before a 3D table folds into the table's shaper, as .cube's 1D-plus-3D files hold it.
                ReadOnlySpan<ToneCurve> curves = e.Curves;
                stages[^1] = new LutStage(lut.Lut.WithInputCurves(curves[0], curves[1], curves[2]), lut.Interpolation);
            }
            else
            {
                stages.Add(stage);
            }
        }

        return [.. stages];
    }

    internal static void CheckUnit(float amount, [CallerArgumentExpression(nameof(amount))] string? name = null)
    {
        if (!(amount >= 0 && amount <= 1))
        {
            throw new ArgumentOutOfRangeException(name, amount, "The value must be from 0 to 1.");
        }
    }
}
