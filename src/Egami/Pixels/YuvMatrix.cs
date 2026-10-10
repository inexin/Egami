namespace Egami.Pixels;

/// <summary>
/// The matrix between RGB and luma-chroma (Y'CbCr) values, as ITU-T H.273 names them. Shared by
/// <see cref="Colors.YCbCrColor"/> and planar YUV images, so their coefficients never disagree.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ColorManagement.cs" region="color-values"/>
/// </example>
public enum YuvMatrix
{
    /// <summary>ITU-R BT.601: Kr 0.299, Kb 0.114. JPEG, and standard-definition video.</summary>
    Bt601,

    /// <summary>ITU-R BT.709: Kr 0.2126, Kb 0.0722. High-definition video.</summary>
    Bt709,

    /// <summary>ITU-R BT.2020, non-constant luminance: Kr 0.2627, Kb 0.0593. UHD and HDR video.</summary>
    Bt2020Ncl,

    /// <summary>No matrix: Y is green, Cb blue and Cr red, as AV1 stores lossless RGB.</summary>
    Identity,
}

/// <summary>Whether luma-chroma values use the whole range or the video range.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ColorManagement.cs" region="color-values"/>
/// </example>
public enum YuvRange
{
    /// <summary>Every value from 0 to the maximum, as JPEG and image formats use.</summary>
    Full,

    /// <summary>The video range: luma 16 to 235 and chroma 16 to 240 at 8 bits, scaled for other depths.</summary>
    Limited,
}

/// <summary>The shared coefficient table: Kr and Kb for each <see cref="YuvMatrix"/>, from ITU-T H.273.</summary>
internal static class YuvCoefficients
{
    public static (double Kr, double Kb) Of(YuvMatrix matrix) => matrix switch
    {
        YuvMatrix.Bt601 => (0.299, 0.114),
        YuvMatrix.Bt709 => (0.2126, 0.0722),
        YuvMatrix.Bt2020Ncl => (0.2627, 0.0593),
        YuvMatrix.Identity => (0, 0),
        _ => throw new ArgumentOutOfRangeException(nameof(matrix), matrix, "Not a YuvMatrix."),
    };
}
