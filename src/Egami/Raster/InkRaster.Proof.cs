using Egami.Colors;
using Egami.Pixels;

namespace Egami;

/// <summary>How <see cref="InkRaster.Proof{TPixel}"/> shows the plates on screen.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ColorManagement.cs" region="ink-proof"/>
/// </example>
public sealed record class ProofOptions
{
    private readonly ColorProfile _target = ColorProfile.Srgb;

    /// <summary>Gets the default options: sRGB, paper shown white.</summary>
    public static ProofOptions Default { get; } = new();

    /// <summary>Gets the encoding of the proof's pixels. Defaults to <see cref="ColorProfile.Srgb"/>.</summary>
    /// <exception cref="ArgumentException">The encoding is neither RGB nor gray.</exception>
    public ColorProfile Target
    {
        get => _target;
        init => _target = value?.Model is ColorModel.Rgb or ColorModel.Gray ? value : throw new ArgumentException("A proof is RGB or gray.", nameof(value));
    }

    /// <summary>
    /// Gets a value indicating whether the paper's own colour shows (absolute colorimetric), as a print looks beside a white
    /// screen; otherwise paper is white and the rest scales to it (relative colorimetric, the default).
    /// </summary>
    public bool SimulatePaper { get; init; }

    /// <summary>Gets a value indicating whether the conversion uses black point compensation, without paper simulation. Defaults to <see langword="false"/>.</summary>
    public bool BlackPointCompensation { get; init; }
}

/// <summary>How much ink an <see cref="InkRaster"/> prints, from <see cref="InkRaster.MeasureCoverage"/>.</summary>
/// <param name="MaxTotal">The largest total of the printing inks on one pixel, as a fraction (3.2 is 320%).</param>
/// <param name="MaxAt">Where that total is (the first such pixel), or <see langword="null"/> for a raster with no ink.</param>
/// <param name="Limit">The total ink limit measured against, as a fraction.</param>
/// <param name="ShareOver">The share of pixels whose total exceeds <paramref name="Limit"/>, 0 to 1.</param>
/// <param name="PerPlate">Each plate's mean coverage, 0 to 1, by ink name; technical inks included.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ColorManagement.cs" region="ink-proof"/>
/// </example>
public sealed record InkCoverage(float MaxTotal, Point? MaxAt, float Limit, double ShareOver, IReadOnlyDictionary<string, double> PerPlate);

public sealed partial class InkRaster
{
    /// <summary>
    /// Returns how the plates print, on screen: each spot's appearance at full tint, as process tints scaled by the spot's tint,
    /// adds to the process inks (capped at full ink, as Ghostscript's and MuPDF's overprint simulation do), and the result goes
    /// through the ink set's profile to <see cref="ProofOptions.Target"/>. Technical inks and spots without an appearance do not show; unprinted paper is paper.
    /// The plates hold ink as printed, so the proof is opaque.
    /// </summary>
    /// <typeparam name="TPixel">The proof's pixel type.</typeparam>
    /// <param name="options">The target encoding and paper, or <see langword="null"/> for <see cref="ProofOptions.Default"/>.</param>
    /// <returns>The proof. The caller must dispose it.</returns>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/ColorManagement.cs" region="ink-proof"/>
    /// </example>
    public Raster<TPixel> Proof<TPixel>(ProofOptions? options = null)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        ThrowIfDisposed();
        options ??= ProofOptions.Default;
        ColorProfile profile = Inks.Profile;
        int process = Inks.ProcessCount;
        ColorTransformOptions transform = new()
        {
            Intent = options.SimulatePaper ? RenderingIntent.AbsoluteColorimetric : RenderingIntent.RelativeColorimetric,
            BlackPointCompensation = options.BlackPointCompensation && !options.SimulatePaper,
        };
        ColorTransform toTarget = ColorTransform.Create(profile, options.Target, transform, Context);

        // Each spot plate's appearance at full tint, as process tints.
        List<(int Plate, float[] Process)> spots = [];
        for (int i = process; i < Plates.Count; i++)
        {
            if (Plates[i].Colorant is { Kind: ColorantKind.Spot, Appearance: InkAppearance appearance })
            {
                spots.Add((i, AsProcess(appearance, profile)));
            }
        }

        int outputs = options.Target.ChannelCount;
        Raster<RgbaF32> proof = Raster.Create<RgbaF32>(Width, Height, Context);
        try
        {
            float[] tints = new float[Width * process];
            float[] colour = new float[Width * outputs];
            float[] spot = new float[Width];
            for (int y = 0; y < Height; y++)
            {
                for (int p = 0; p < process; p++)
                {
                    ReadPlate(Plates[p], y, spot);
                    for (int x = 0; x < Width; x++)
                    {
                        tints[(x * process) + p] = spot[x];
                    }
                }

                foreach ((int plate, float[] look) in spots)
                {
                    ReadPlate(Plates[plate], y, spot);
                    for (int x = 0; x < Width; x++)
                    {
                        for (int p = 0; p < process; p++)
                        {
                            int at = (x * process) + p;
                            tints[at] = Math.Min(1, tints[at] + (spot[x] * look[p]));
                        }
                    }
                }

                toTarget.Apply(tints, colour);
                Span<RgbaF32> row = proof.Row(y);
                for (int x = 0; x < Width; x++)
                {
                    row[x] = outputs == 1
                        ? new RgbaF32(colour[x], colour[x], colour[x], 1)
                        : new RgbaF32(colour[3 * x], colour[(3 * x) + 1], colour[(3 * x) + 2], 1);
                }
            }

            proof.Metadata.IccProfile = options.Target.ToIcc();
            if (typeof(TPixel) == typeof(RgbaF32))
            {
                return (Raster<TPixel>)(object)proof;
            }

            using (proof)
            {
                return proof.As<TPixel>();
            }
        }
        catch
        {
            proof.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Measures the ink: the largest total of the printing inks (process and spots; technical inks such as varnish are not
    /// counted) on one pixel, where it is, the share of pixels over <paramref name="limit"/>, and each plate's mean coverage.
    /// </summary>
    /// <param name="limit">The total ink limit, as a fraction: 3.0 for 300%, FOGRA39's.</param>
    /// <returns>The figures.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limit"/> is negative or not finite.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/ColorManagement.cs" region="ink-proof"/>
    /// </example>
    public InkCoverage MeasureCoverage(float limit = 3.0f)
    {
        ThrowIfDisposed();
        if (!(limit >= 0) || !float.IsFinite(limit))
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "The limit must be a finite fraction of 0 or more.");
        }

        double[] sums = new double[Plates.Count];
        float[] total = new float[Width], row = new float[Width];
        float max = 0;
        Point? at = null;
        long over = 0;
        for (int y = 0; y < Height; y++)
        {
            Array.Clear(total);
            for (int p = 0; p < Plates.Count; p++)
            {
                ReadPlate(Plates[p], y, row);
                bool printing = Plates[p].Colorant!.Kind != ColorantKind.Technical;
                for (int x = 0; x < Width; x++)
                {
                    sums[p] += row[x];
                    if (printing)
                    {
                        total[x] += row[x];
                    }
                }
            }

            for (int x = 0; x < Width; x++)
            {
                if (total[x] > max)
                {
                    (max, at) = (total[x], new Point(x, y));
                }

                over += total[x] > limit + 1e-6f ? 1 : 0;
            }
        }

        double pixels = (double)Width * Height;
        Dictionary<string, double> perPlate = new(StringComparer.Ordinal);
        for (int p = 0; p < Plates.Count; p++)
        {
            perPlate[Plates[p].Name] = sums[p] / pixels;
        }

        return new InkCoverage(max, at, limit, over / pixels, perPlate);
    }

    private static void ReadPlate(InkPlate plate, int y, Span<float> values)
    {
        if (plate.Depth == InkDepth.Unorm8)
        {
            ReadOnlySpan<byte> row = plate.Row8(y);
            for (int x = 0; x < values.Length; x++)
            {
                values[x] = row[x] / 255f;
            }
        }
        else
        {
            ReadOnlySpan<ushort> row = plate.Row16(y);
            for (int x = 0; x < values.Length; x++)
            {
                values[x] = row[x] / 65535f;
            }
        }
    }

    // An appearance as the process tints of the profile: CMYK as it is on a CMYK set, else through FOGRA39 (CMYK) or sRGB.
    private float[] AsProcess(InkAppearance appearance, ColorProfile profile)
    {
        float[] process = new float[Inks.ProcessCount];
        System.Numerics.Vector4 v = appearance.Values;
        ColorTransformOptions relative = new() { Intent = RenderingIntent.RelativeColorimetric, BlackPointCompensation = true };
        if (appearance.Kind == InkAppearanceKind.Cmyk)
        {
            if (profile.Model == ColorModel.Cmyk)
            {
                (process[0], process[1], process[2], process[3]) = (v.X, v.Y, v.Z, v.W);
            }
            else
            {
                ColorTransform.Create(ColorProfile.Fogra39, profile, relative, Context).Apply([v.X, v.Y, v.Z, v.W], process);
            }
        }
        else
        {
            RgbaF32 rgb = appearance.Kind == InkAppearanceKind.Rgb ? new RgbaF32(v.X, v.Y, v.Z, 1) : appearance.ToLab().ToRgbaF32();
            ColorTransform.Create(ColorProfile.Srgb, profile, relative, Context).Apply([rgb.R, rgb.G, rgb.B], process);
        }

        for (int i = 0; i < process.Length; i++)
        {
            process[i] = Math.Clamp(process[i], 0, 1);
        }

        return process;
    }
}
