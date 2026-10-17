namespace Egami.Colors;

/// <summary>Whether a color encoding has three RGB channels, one gray channel, four CMYK inks, or the inks of an n-colour press.</summary>
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

    /// <summary>
    /// Two to fifteen inks of an n-colour output profile (ICC's 2CLR to FCLR), such as a hexachrome or seven-colour press:
    /// <see cref="ColorProfile.ChannelCount"/> says how many, and <see cref="ColorProfile.ColorantNames"/> names them. No pixel
    /// type holds them: convert with <see cref="ColorTransform.Apply(ReadOnlySpan{float}, Span{float})"/> or draw into an
    /// <see cref="InkRaster"/>.
    /// </summary>
    NColor,
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
    private static readonly Lazy<ColorProfile> LazyFogra39 = new(() => FromIcc(EmbeddedProfile("Egami.Colors.FOGRA39L_coated.icc")));

    private ColorProfile(string name, ColorModel model, ColorPrimaries? primaries, Chromaticity white, TransferFunction? transfer, IccData? icc = null, IccModel? iccModel = null)
    {
        (Name, Model, Primaries, White, Transfer, Icc, IccModel) = (name, model, primaries, white, transfer, icc, iccModel);
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

    /// <summary>
    /// Gets FOGRA39L: offset printing on coated paper (ISO 12647-2, paper types 1 and 2), Europe's usual press condition, built for a
    /// 300% total ink limit. The profile is colord's, built by ArgyllCMS from Fogra's measurements, as the CTAN colorprofiles
    /// package ships it (ICC version 2.2, free of known copyright restrictions); <see cref="ToIcc"/> returns its bytes unchanged.
    /// It is read on first use. CMYK files without a profile still convert by the plain formula, not through this one.
    /// </summary>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/ColorManagement.cs" region="cmyk-ink"/>
    /// </example>
    public static ColorProfile Fogra39 => LazyFogra39.Value;

    /// <summary>Gets a short name for the encoding, such as "sRGB" or the description of an ICC profile.</summary>
    public string Name { get; }

    /// <summary>Gets the color model.</summary>
    public ColorModel Model { get; }

    /// <summary>Gets the number of channels: 3 for RGB, 1 for gray, 4 for CMYK, 2 to 15 for an n-colour profile.</summary>
    public int ChannelCount => Model switch
    {
        ColorModel.Rgb => 3,
        ColorModel.Gray => 1,
        ColorModel.Cmyk => 4,
        _ => IccModel!.Channels,
    };

    /// <summary>
    /// Gets the names of an output profile's inks, from its colorant table (<c>clrt</c>), in channel order; <see langword="null"/>
    /// when the profile has none.
    /// </summary>
    public IReadOnlyList<string>? ColorantNames => IccModel?.ColorantTable?.Select(c => c.Name).ToArray();

    /// <summary>
    /// Gets the primaries of an RGB encoding, or <see langword="null"/> for gray, CMYK, and ICC profiles that convert through
    /// tables rather than a matrix.
    /// </summary>
    public ColorPrimaries? Primaries { get; }

    /// <summary>Gets the white point.</summary>
    public Chromaticity White { get; }

    /// <summary>
    /// Gets the transfer function of an RGB or gray encoding, or <see langword="null"/> for CMYK, ICC profiles that convert
    /// through tables, and ICC profiles whose three curves differ.
    /// </summary>
    public TransferFunction? Transfer { get; }

    /// <summary>Gets the ICC profile this encoding was read from, or <see langword="null"/>.</summary>
    public IccData? Icc { get; }

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

    /// <summary>Reads an ICC profile: <see cref="FromIcc(IccData)"/> of <see cref="IccData.Parse"/>.</summary>
    /// <param name="profile">The profile's bytes.</param>
    /// <returns>The encoding.</returns>
    /// <exception cref="CorruptDataException">The profile is damaged where conversion needs it.</exception>
    /// <exception cref="UnsupportedFormatException">The profile is not an RGB, gray or CMYK input, display, output or color space profile.</exception>
    public static ColorProfile FromIcc(ReadOnlySpan<byte> profile) => FromIcc(IccData.Parse(profile));

    /// <summary>
    /// Returns the encoding an ICC profile describes. A matrix-shaper profile gets its primaries (un-adapted from D50 by its
    /// <c>chad</c> tag or its white point) and its curve, so <see cref="IsEquivalentTo"/> recognizes sRGB, Display P3 and the
    /// other built-ins written as ICC; a version 4.4 <c>cicp</c> tag naming a known encoding wins over the rest, as it does for
    /// HDR profiles. Profiles that convert through tables, and CMYK, keep only the model and white.
    /// </summary>
    /// <param name="icc">The parsed profile.</param>
    /// <returns>The encoding, with <see cref="Icc"/> set.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="icc"/> is <see langword="null"/>.</exception>
    /// <exception cref="CorruptDataException">The profile is damaged where conversion needs it.</exception>
    /// <exception cref="UnsupportedFormatException">The profile is not an RGB, gray or CMYK input, display, output or color space profile.</exception>
    public static ColorProfile FromIcc(IccData icc)
    {
        ArgumentNullException.ThrowIfNull(icc);
        IccModel model = IccModel.Read(icc);
        string name = string.IsNullOrWhiteSpace(icc.Description) ? "ICC profile" : icc.Description;
        if (model.Model == ColorModel.Rgb && icc.CodePoints is ColorCodePoints codePoints && FromCodePoints(codePoints) is ColorProfile coded)
        {
            return new ColorProfile(name, ColorModel.Rgb, coded.Primaries, coded.White, coded.Transfer, icc, model);
        }

        // The adopted white: chad maps it to D50; without chad, a version 2 profile's media white is it, and version 4 means D50.
        Vector3d whiteXyz = model.Adaptation is Matrix3d chad ? chad.Inverse() * PcsWhite
            : icc.Version.Major < 4 && model.MediaWhite.Y > 0 ? model.MediaWhite * (1 / model.MediaWhite.Y) : PcsWhite;
        Chromaticity white = Chromaticity.FromXyz(whiteXyz);
        if (!model.IsMatrixShaper)
        {
            return new ColorProfile(name, model.Model, null, white, null, icc, model);
        }

        IccCurve[] curves = model.Curves!;
        TransferFunction? transfer = curves.All(c => c.SameAs(curves[0])) ? curves[0].ToTransferFunction() : null;
        if (model.Model == ColorModel.Gray)
        {
            return new ColorProfile(name, ColorModel.Gray, null, white, transfer, icc, model);
        }

        // Undo the adaptation to D50 to get the colorants under the adopted white, then their chromaticities.
        Matrix3d toAdopted = model.Adaptation is Matrix3d a ? a.Inverse() : Matrix3d.Bradford(PcsWhite, whiteXyz);
        Matrix3d native = toAdopted * model.Colorants!.Value;
        ColorPrimaries primaries = new(Column(native, 0), Column(native, 1), Column(native, 2), white);
        if (!primaries.IsValid)
        {
            throw IccModel.Corrupt("rXYZ, gXYZ, bXYZ", "the colorants cannot form a color space");
        }

        return new ColorProfile(name, ColorModel.Rgb, primaries, white, transfer, icc, model);

        // Red has z = 0 in Display P3 and Rec. 2020, so rounding in the profile can leave a Z just below 0.
        static Chromaticity Column(Matrix3d m, int c) => Chromaticity.FromXyz(new Vector3d(m[0, c], m[1, c], Math.Max(m[2, c], 0)));
    }

    /// <summary>
    /// Returns an ICC profile for this encoding: the bytes it was read from, or a small version 4 profile written for it (4.3 with
    /// parametric curves for SDR encodings, 4.4 with a <c>cicp</c> tag for PQ and HLG), which Little CMS, browsers and image
    /// tools read.
    /// </summary>
    /// <returns>A new array holding the profile.</returns>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/ColorManagement.cs" region="to-icc"/>
    /// </example>
    public byte[] ToIcc() => Icc is IccData icc ? icc.ToBytes().ToArray() : IccWriter.Write(this);

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
    public bool IsEquivalentTo(ColorProfile other) => IsCloseTo(other, 1e-4);

    /// <summary>
    /// Returns whether two encodings mean the same colors within <paramref name="tolerance"/>. Conversion treats encodings within
    /// 2e-5, the rounding of an ICC profile's s15.16 numbers, as one, and converts anything further apart.
    /// </summary>
    internal bool IsCloseTo(ColorProfile other, double tolerance)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (Model != other.Model || Math.Abs(White.X - other.White.X) > tolerance || Math.Abs(White.Y - other.White.Y) > tolerance)
        {
            return false;
        }

        if (Transfer is null || other.Transfer is null || (Model == ColorModel.Rgb && (Primaries is null || other.Primaries is null)))
        {
            // Tables, CMYK, or curves that differ per channel: only the same profile bytes are known to mean the same colors.
            return Icc is not null && other.Icc is not null && Icc.ToBytes().Span.SequenceEqual(other.Icc.ToBytes().Span);
        }

        if (Model == ColorModel.Rgb && !Primaries!.IsCloseTo(other.Primaries!, tolerance))
        {
            return false;
        }

        return Transfer.IsCloseTo(other.Transfer, tolerance);
    }

    /// <inheritdoc/>
    public override string ToString() => Name;

    /// <summary>Gets the parsed tags of <see cref="Icc"/>, for conversion.</summary>
    internal IccModel? IccModel { get; }

    /// <summary>Gets the ICC connection space white, D50 as ICC headers give it.</summary>
    private static Vector3d PcsWhite => new(0.9642, 1.0, 0.8249);

    /// <summary>The matrix from this encoding's linear RGB to XYZ relative to its own white.</summary>
    internal Matrix3d ToXyz() => Primaries!.ToXyz();

    private static byte[] EmbeddedProfile(string name)
    {
        using Stream stream = typeof(ColorProfile).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"The resource {name} is missing.");
        byte[] bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }
}
