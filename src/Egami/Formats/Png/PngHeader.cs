namespace Egami.Formats.Png;

/// <summary>A PNG color type (PNG specification, section 11.2.1).</summary>
public enum PngColorType : byte
{
    /// <summary>Grayscale.</summary>
    Gray = 0,

    /// <summary>Red, green, blue.</summary>
    Rgb = 2,

    /// <summary>Indices into a palette.</summary>
    Palette = 3,

    /// <summary>Grayscale with alpha.</summary>
    GrayAlpha = 4,

    /// <summary>Red, green, blue and alpha.</summary>
    Rgba = 6,
}

/// <summary>The contents of an IHDR chunk, validated against the specification.</summary>
internal readonly record struct PngHeader(int Width, int Height, int BitDepth, PngColorType ColorType, bool Interlaced)
{
    /// <summary>Gets the number of samples per pixel.</summary>
    public int Channels => ColorType switch
    {
        PngColorType.Gray or PngColorType.Palette => 1,
        PngColorType.GrayAlpha => 2,
        PngColorType.Rgb => 3,
        _ => 4,
    };

    /// <summary>Gets the number of bits per pixel.</summary>
    public int BitsPerPixel => Channels * BitDepth;

    /// <summary>Gets the filter distance: bytes per complete pixel, rounded up to 1 (section 9.2).</summary>
    public int FilterStride => Math.Max(1, BitsPerPixel / 8);

    /// <summary>Returns the bytes in one scanline of <paramref name="width"/> pixels, without the filter-type byte.</summary>
    public long RowBytes(long width)
    {
        return ((width * BitsPerPixel) + 7) / 8;
    }

    /// <summary>Returns whether <paramref name="bitDepth"/> is allowed for <paramref name="colorType"/> (section 11.2.1, table 11.1).</summary>
    public static bool IsValid(PngColorType colorType, int bitDepth)
    {
        return colorType switch
        {
            PngColorType.Gray => bitDepth is 1 or 2 or 4 or 8 or 16,
            PngColorType.Palette => bitDepth is 1 or 2 or 4 or 8,
            PngColorType.Rgb or PngColorType.GrayAlpha or PngColorType.Rgba => bitDepth is 8 or 16,
            _ => false,
        };
    }
}

/// <summary>The seven Adam7 passes (section 8.2): column and row start, then column and row step.</summary>
internal static class Adam7
{
    internal static ReadOnlySpan<byte> XStart => [0, 4, 0, 2, 0, 1, 0];

    internal static ReadOnlySpan<byte> YStart => [0, 0, 4, 0, 2, 0, 1];

    internal static ReadOnlySpan<byte> XStep => [8, 8, 4, 4, 2, 2, 1];

    internal static ReadOnlySpan<byte> YStep => [8, 8, 8, 4, 4, 2, 2];

    /// <summary>Returns the number of columns pass <paramref name="pass"/> covers in an image <paramref name="width"/> wide.</summary>
    internal static int Columns(int pass, int width)
    {
        return width > XStart[pass] ? (width - XStart[pass] + XStep[pass] - 1) / XStep[pass] : 0;
    }

    /// <summary>Returns the number of rows pass <paramref name="pass"/> covers in an image <paramref name="height"/> high.</summary>
    internal static int Rows(int pass, int height)
    {
        return height > YStart[pass] ? (height - YStart[pass] + YStep[pass] - 1) / YStep[pass] : 0;
    }
}
