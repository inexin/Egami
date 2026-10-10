namespace Egami.Colors;

/// <summary>Whether a color encoding has three RGB channels, one gray channel, or four CMYK inks.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ColorManagement.cs" region="profiles"/>
/// </example>
public enum ColorModel
{
    /// <summary>Red, green and blue.</summary>
    Rgb,

    /// <summary>One gray channel.</summary>
    Gray,

    /// <summary>Cyan, magenta, yellow and black inks.</summary>
    Cmyk,
}

/// <summary>
/// What a raster's numbers mean: the color model, the primaries and white point, and the transfer function. Immutable; the
/// built-ins cover the common encodings, and <see cref="Create"/> makes any other RGB encoding.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ColorManagement.cs" region="profiles"/>
/// </example>
public sealed class ColorProfile
{
    private ColorProfile(string name, ColorModel model, ColorPrimaries? primaries, Chromaticity white, TransferFunction? transfer)
    {
        (Name, Model, Primaries, White, Transfer) = (name, model, primaries, white, transfer);
    }

    /// <summary>Gets sRGB (IEC 61966-2-1): BT.709 primaries, D65, the sRGB curve. What a file without color information means.</summary>
    public static ColorProfile Srgb { get; } = new("sRGB", ColorModel.Rgb, ColorPrimaries.Rec709, Chromaticity.D65, TransferFunction.Srgb);

    /// <summary>Gets linear sRGB: BT.709 primaries, D65, no curve.</summary>
    public static ColorProfile LinearSrgb { get; } = new("Linear sRGB", ColorModel.Rgb, ColorPrimaries.Rec709, Chromaticity.D65, TransferFunction.Linear);

    /// <summary>Gets Display P3: the P3 primaries, D65, the sRGB curve, as Apple devices and CSS Color 4 use it.</summary>
    public static ColorProfile DisplayP3 { get; } = new("Display P3", ColorModel.Rgb, ColorPrimaries.DisplayP3, Chromaticity.D65, TransferFunction.Srgb);

    /// <summary>Gets Adobe RGB (1998): its primaries, D65, a 563/256 (about 2.2) power curve.</summary>
    public static ColorProfile AdobeRgb { get; } = new("Adobe RGB (1998)", ColorModel.Rgb, ColorPrimaries.AdobeRgb, Chromaticity.D65, TransferFunction.Gamma(563.0 / 256));

    /// <summary>Gets ProPhoto RGB (ROMM RGB): its primaries, D50, a 1.8 power with a linear segment below 1/32.</summary>
    public static ColorProfile ProPhotoRgb { get; } = new("ProPhoto RGB", ColorModel.Rgb, ColorPrimaries.ProPhoto, Chromaticity.D50, TransferFunction.Parametric(1.8, 1, 0, 1.0 / 16, 1.0 / 32, 0, 0));

    /// <summary>Gets Rec. 2020: BT.2020 primaries, D65, the BT.709/BT.2020 curve.</summary>
    public static ColorProfile Rec2020 { get; } = new("Rec. 2020", ColorModel.Rgb, ColorPrimaries.Rec2020, Chromaticity.D65, TransferFunction.Rec709);

    /// <summary>Gets Rec. 2100 PQ: BT.2020 primaries, D65, the PQ curve (HDR).</summary>
    public static ColorProfile Rec2100Pq { get; } = new("Rec. 2100 PQ", ColorModel.Rgb, ColorPrimaries.Rec2020, Chromaticity.D65, TransferFunction.Pq);

    /// <summary>Gets Rec. 2100 HLG: BT.2020 primaries, D65, the HLG curve (HDR).</summary>
    public static ColorProfile Rec2100Hlg { get; } = new("Rec. 2100 HLG", ColorModel.Rgb, ColorPrimaries.Rec2020, Chromaticity.D65, TransferFunction.Hlg);

    /// <summary>Gets gray with the sRGB curve and D65 white: what a gray file without color information means.</summary>
    public static ColorProfile GraySrgb { get; } = new("Gray sRGB", ColorModel.Gray, null, Chromaticity.D65, TransferFunction.Srgb);

    /// <summary>Gets linear gray with D65 white.</summary>
    public static ColorProfile GrayLinear { get; } = new("Gray linear", ColorModel.Gray, null, Chromaticity.D65, TransferFunction.Linear);

    /// <summary>Gets a short name for the encoding, such as "sRGB" or the description of an ICC profile.</summary>
    public string Name { get; }

    /// <summary>Gets the color model.</summary>
    public ColorModel Model { get; }

    /// <summary>Gets the primaries of an RGB encoding, or <see langword="null"/> for gray and CMYK.</summary>
    public ColorPrimaries? Primaries { get; }

    /// <summary>Gets the white point.</summary>
    public Chromaticity White { get; }

    /// <summary>Gets the transfer function of an RGB or gray encoding, or <see langword="null"/> for CMYK.</summary>
    public TransferFunction? Transfer { get; }

    /// <summary>Gets a value indicating whether the encoding is HDR (a PQ or HLG curve).</summary>
    public bool IsHdr => Transfer?.IsHdr == true;

    /// <summary>Returns an RGB encoding from primaries and a transfer function.</summary>
    /// <param name="primaries">The primaries, with the white point.</param>
    /// <param name="transfer">The transfer function.</param>
    /// <param name="name">A name, or <see langword="null"/> for a generated one.</param>
    /// <returns>The encoding.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="primaries"/> or <paramref name="transfer"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The primaries cannot form a color space (<see cref="ColorPrimaries.IsValid"/> is false).</exception>
    public static ColorProfile Create(ColorPrimaries primaries, TransferFunction transfer, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(primaries);
        ArgumentNullException.ThrowIfNull(transfer);
        if (!primaries.IsValid)
        {
            throw new ArgumentException("The primaries cannot form a color space.", nameof(primaries));
        }

        return new ColorProfile(name ?? $"RGB, {transfer}", ColorModel.Rgb, primaries, primaries.White, transfer);
    }

    /// <summary>Returns a gray encoding from a transfer function and a white point.</summary>
    /// <param name="transfer">The transfer function.</param>
    /// <param name="white">The white point, or <see langword="null"/> for D65.</param>
    /// <param name="name">A name, or <see langword="null"/> for a generated one.</param>
    /// <returns>The encoding.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transfer"/> is <see langword="null"/>.</exception>
    public static ColorProfile CreateGray(TransferFunction transfer, Chromaticity? white = null, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        return new ColorProfile(name ?? $"Gray, {transfer}", ColorModel.Gray, null, white ?? Chromaticity.D65, transfer);
    }

    /// <summary>
    /// Returns the RGB encoding that H.273 code points name, or <see langword="null"/> when Egami does not implement the primaries
    /// or the transfer they name, or when the matrix code is not RGB (0).
    /// </summary>
    /// <param name="codePoints">The code points.</param>
    /// <returns>The encoding, a built-in one where it matches, or <see langword="null"/>.</returns>
    public static ColorProfile? FromCodePoints(ColorCodePoints codePoints)
    {
        if (codePoints.MatrixCoefficients != 0 || codePoints.GetPrimaries() is not ColorPrimaries primaries || codePoints.GetTransferFunction() is not TransferFunction transfer)
        {
            return null;
        }

        foreach (ColorProfile builtIn in (ReadOnlySpan<ColorProfile>)[Srgb, LinearSrgb, DisplayP3, Rec2020, Rec2100Pq, Rec2100Hlg])
        {
            if (builtIn.Primaries == primaries && builtIn.Transfer!.Equals(transfer))
            {
                return builtIn;
            }
        }

        return Create(primaries, transfer);
    }

    /// <summary>
    /// Returns the H.273 code points of this encoding, or <see langword="null"/> when its primaries or curve have none (Adobe RGB,
    /// ProPhoto, gray, CMYK, parametric and sampled curves).
    /// </summary>
    /// <returns>The code points, or <see langword="null"/>.</returns>
    public ColorCodePoints? ToCodePoints()
    {
        if (Model != ColorModel.Rgb || Primaries is null || Transfer is null)
        {
            return null;
        }

        byte? primaries = Primaries.IsCloseTo(ColorPrimaries.Rec709, 1e-6) ? 1 : Primaries.IsCloseTo(ColorPrimaries.Rec2020, 1e-6) ? 9 : Primaries.IsCloseTo(ColorPrimaries.DisplayP3, 1e-6) ? 12 : null;
        byte? transfer = Transfer.Shape switch
        {
            TransferFunction.Kind.Srgb => 13,
            TransferFunction.Kind.Linear => 8,
            TransferFunction.Kind.Rec709 => 1,
            TransferFunction.Kind.Pq => 16,
            TransferFunction.Kind.Hlg => 18,
            TransferFunction.Kind.Gamma when Transfer.Equals(TransferFunction.Gamma(2.2)) => 4,
            TransferFunction.Kind.Gamma when Transfer.Equals(TransferFunction.Gamma(2.8)) => 5,
            _ => null,
        };
        return primaries is byte p && transfer is byte t ? new ColorCodePoints(p, t, 0, true) : null;
    }

    /// <summary>
    /// Returns whether two encodings mean the same colors: the same model, primaries and white within 1e-4, and curves that agree
    /// within 1e-4 (so an ICC profile written for sRGB is equivalent to <see cref="Srgb"/>).
    /// </summary>
    /// <param name="other">The other encoding.</param>
    /// <returns><see langword="true"/> if they are equivalent.</returns>
    public bool IsEquivalentTo(ColorProfile other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (Model != other.Model || Math.Abs(White.X - other.White.X) > 1e-4 || Math.Abs(White.Y - other.White.Y) > 1e-4)
        {
            return false;
        }

        if (Model == ColorModel.Rgb && (Primaries is null || other.Primaries is null || !Primaries.IsCloseTo(other.Primaries)))
        {
            return false;
        }

        return Transfer is not null && other.Transfer is not null && Transfer.IsCloseTo(other.Transfer);
    }

    /// <inheritdoc/>
    public override string ToString() => Name;

    /// <summary>The matrix from this encoding's linear RGB to XYZ relative to its own white.</summary>
    internal Matrix3d ToXyz() => Primaries!.ToXyz();
}
