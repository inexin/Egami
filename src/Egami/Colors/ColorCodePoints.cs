namespace Egami.Colors;

/// <summary>
/// The ITU-T H.273 code points that PNG's cICP chunk, AVIF and JPEG XL use to name a color encoding: colour primaries, transfer
/// characteristics, matrix coefficients and the full-range flag, kept exactly as stored so codes Egami does not know round-trip.
/// </summary>
/// <param name="Primaries">The colour primaries code: 1 for BT.709, 9 for BT.2020, 12 for Display P3.</param>
/// <param name="TransferCharacteristics">The transfer characteristics code: 1 for BT.709, 8 for linear, 13 for sRGB, 16 for PQ, 18 for HLG.</param>
/// <param name="MatrixCoefficients">The matrix coefficients code: 0 for RGB, which is what still images use.</param>
/// <param name="FullRange">Whether samples use the full range (true for RGB images) rather than the video range.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ColorManagement.cs" region="code-points"/>
/// </example>
public readonly record struct ColorCodePoints(byte Primaries, byte TransferCharacteristics, byte MatrixCoefficients, bool FullRange)
{
    /// <summary>Gets the code points of sRGB: BT.709 primaries, the sRGB transfer, RGB, full range.</summary>
    public static ColorCodePoints Srgb { get; } = new(1, 13, 0, true);

    /// <summary>Gets the code points of Rec. 2100 PQ: BT.2020 primaries, the PQ transfer, RGB, full range.</summary>
    public static ColorCodePoints Rec2100Pq { get; } = new(9, 16, 0, true);

    /// <summary>Gets the code points of Rec. 2100 HLG: BT.2020 primaries, the HLG transfer, RGB, full range.</summary>
    public static ColorCodePoints Rec2100Hlg { get; } = new(9, 18, 0, true);

    /// <summary>Returns the primaries the code names, or <see langword="null"/> for a code Egami cannot use (unspecified, reserved, or XYZ).</summary>
    /// <returns>The primaries, or <see langword="null"/>.</returns>
    public ColorPrimaries? GetPrimaries() => Primaries switch
    {
        1 => ColorPrimaries.Rec709,
        4 => new ColorPrimaries(new(0.67, 0.33), new(0.21, 0.71), new(0.14, 0.08), new(0.310, 0.316)),
        5 => new ColorPrimaries(new(0.64, 0.33), new(0.29, 0.60), new(0.15, 0.06), Chromaticity.D65),
        6 or 7 => new ColorPrimaries(new(0.630, 0.340), new(0.310, 0.595), new(0.155, 0.070), Chromaticity.D65),
        8 => new ColorPrimaries(new(0.681, 0.319), new(0.243, 0.692), new(0.145, 0.049), new(0.310, 0.316)),
        9 => ColorPrimaries.Rec2020,
        11 => new ColorPrimaries(new(0.680, 0.320), new(0.265, 0.690), new(0.150, 0.060), new(0.314, 0.351)),
        12 => ColorPrimaries.DisplayP3,
        22 => new ColorPrimaries(new(0.630, 0.340), new(0.295, 0.605), new(0.155, 0.077), Chromaticity.D65),
        _ => null,
    };

    /// <summary>Returns the transfer function the code names, or <see langword="null"/> for a code Egami does not implement.</summary>
    /// <returns>The transfer function, or <see langword="null"/>.</returns>
    public TransferFunction? GetTransferFunction() => TransferCharacteristics switch
    {
        1 or 6 or 14 or 15 => TransferFunction.Rec709,
        4 => TransferFunction.Gamma(2.2),
        5 => TransferFunction.Gamma(2.8),
        8 => TransferFunction.Linear,
        13 => TransferFunction.Srgb,
        16 => TransferFunction.Pq,
        18 => TransferFunction.Hlg,
        _ => null,
    };
}
