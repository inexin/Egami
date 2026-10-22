namespace Egami.Pixels;

/// <summary>How a <see cref="YuvRaster"/> stores chroma: at what resolution against luma, or not at all.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/PlanarYuv.cs" region="create"/>
/// </example>
public enum ChromaFormat
{
    /// <summary>Chroma at half the width and half the height of luma, as most video, lossy WebP and most JPEG files store it.</summary>
    Yuv420,

    /// <summary>Chroma at half the width and the full height of luma.</summary>
    Yuv422,

    /// <summary>Chroma at the full size of luma.</summary>
    Yuv444,

    /// <summary>Luma only, with no chroma planes: a gray picture, such as a gray JPEG or monochrome AV1.</summary>
    Yuv400,

    /// <summary>Chroma at a quarter of the width and the full height of luma, as NTSC DV and some capture cards store it.</summary>
    Yuv411,

    /// <summary>Chroma at a quarter of the width and a quarter of the height of luma, as old codecs such as Indeo (YVU9) store it.</summary>
    Yuv410,
}

/// <summary>
/// Where each chroma sample of a subsampled picture (4:2:0, 4:2:2, 4:1:1 or 4:1:0) sits against the luma samples it covers.
/// Conversions filter chroma to match, so colors line up with edges. Ignored for 4:4:4 and 4:0:0.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/PlanarYuv.cs" region="color"/>
/// </example>
public enum ChromaSiting
{
    /// <summary>Centered between the luma samples it covers, both ways: JPEG, VP8 and lossy WebP.</summary>
    Center,

    /// <summary>
    /// On the left luma column of the columns it covers and centered vertically: BT.601 and BT.709 video, MPEG-2 and later, and
    /// AV1's "vertical" position.
    /// </summary>
    Left,

    /// <summary>On the top-left luma sample of the block it covers: BT.2020 and AV1's "colocated" position.</summary>
    TopLeft,
}

/// <summary>
/// How the samples of a <see cref="YuvRaster"/> turn into RGB: the matrix, the range and where chroma sits. Every load, wrap and
/// conversion states one, so a picture is never read with a guessed matrix.
/// </summary>
/// <param name="Matrix">The matrix between RGB and luma-chroma.</param>
/// <param name="Range">Whether samples use the full range or the video range.</param>
/// <param name="Siting">Where subsampled chroma sits against luma.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/PlanarYuv.cs" region="color"/>
/// </example>
public readonly record struct YuvColor(YuvMatrix Matrix, YuvRange Range, ChromaSiting Siting)
{
    /// <summary>Gets JPEG's encoding (JFIF): BT.601, full range, chroma centered.</summary>
    public static YuvColor Jpeg => new(YuvMatrix.Bt601, YuvRange.Full, ChromaSiting.Center);

    /// <summary>Gets standard-definition video: BT.601, limited range, chroma on the left column.</summary>
    public static YuvColor Bt601Limited => new(YuvMatrix.Bt601, YuvRange.Limited, ChromaSiting.Left);

    /// <summary>Gets high-definition video: BT.709, limited range, chroma on the left column.</summary>
    public static YuvColor Bt709Limited => new(YuvMatrix.Bt709, YuvRange.Limited, ChromaSiting.Left);

    /// <summary>Gets UHD and HDR video: BT.2020 non-constant luminance, limited range, chroma on the top-left sample.</summary>
    public static YuvColor Bt2020Limited => new(YuvMatrix.Bt2020Ncl, YuvRange.Limited, ChromaSiting.TopLeft);

    /// <summary>
    /// Gets the color that ITU-T H.273's <c>matrix_coefficients</c> code and full-range flag name, as AV1, VP9, HEVC and CICP boxes
    /// carry them: 0 is identity (GBR), 1 BT.709, 5 and 6 BT.601, 9 BT.2020 non-constant luminance.
    /// </summary>
    /// <param name="matrixCoefficients">The H.273 code.</param>
    /// <param name="fullRange">The full-range flag.</param>
    /// <param name="siting">Where chroma sits, which H.273 does not carry.</param>
    /// <param name="color">The color, or the default when the code is not one Egami knows.</param>
    /// <returns><see langword="true"/> if the code names a matrix Egami knows; unknown codes return false rather than a guess.</returns>
    public static bool TryFromH273(int matrixCoefficients, bool fullRange, ChromaSiting siting, out YuvColor color)
    {
        if (!YuvMatrixCodes.TryFromH273(matrixCoefficients, out YuvMatrix matrix))
        {
            color = default;
            return false;
        }

        color = new YuvColor(matrix, fullRange ? YuvRange.Full : YuvRange.Limited, siting);
        return true;
    }

    /// <summary>Returns the ITU-T H.273 <c>matrix_coefficients</c> code of <see cref="Matrix"/>: 0, 1, 6 or 9.</summary>
    /// <returns>The code.</returns>
    public int ToH273() => YuvMatrixCodes.ToH273(Matrix);
}

/// <summary>The ITU-T H.273 <c>matrix_coefficients</c> codes of <see cref="YuvMatrix"/>.</summary>
internal static class YuvMatrixCodes
{
    internal static bool TryFromH273(int code, out YuvMatrix matrix)
    {
        (bool known, matrix) = code switch
        {
            0 => (true, YuvMatrix.Identity),
            1 => (true, YuvMatrix.Bt709),
            5 or 6 => (true, YuvMatrix.Bt601),
            9 => (true, YuvMatrix.Bt2020Ncl),
            _ => (false, default),
        };
        return known;
    }

    internal static int ToH273(YuvMatrix matrix) => matrix switch
    {
        YuvMatrix.Identity => 0,
        YuvMatrix.Bt709 => 1,
        YuvMatrix.Bt601 => 6,
        YuvMatrix.Bt2020Ncl => 9,
        _ => throw new ArgumentOutOfRangeException(nameof(matrix), matrix, "Not a YuvMatrix."),
    };
}
