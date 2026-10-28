namespace Egami.SystemDrawing.Imaging;

/// <summary>How <see cref="Bitmap.LockBits(System.Drawing.Rectangle, ImageLockMode, PixelFormat)"/> may use the locked pixels.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="lockbits"/>
/// </example>
[Flags]
public enum ImageLockMode
{
    /// <summary>The pixels are read; changes are not written back.</summary>
    ReadOnly = 1,

    /// <summary>The pixels are written; they start out undefined and are written back on unlock.</summary>
    WriteOnly = 2,

    /// <summary>The pixels are read and written back on unlock.</summary>
    ReadWrite = 3,

    /// <summary>The caller's <see cref="BitmapData"/> holds the buffer the pixels go to.</summary>
    UserInputBuffer = 4,
}

/// <summary>What an image's <see cref="Image.Flags"/> say about it.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="codecs"/>
/// </example>
[Flags]
public enum ImageFlags
{
    /// <summary>No flags.</summary>
    None = 0,

    /// <summary>The image can be scaled, as a metafile.</summary>
    Scalable = 0x0001,

    /// <summary>The pixels hold alpha.</summary>
    HasAlpha = 0x0002,

    /// <summary>The pixels hold alpha between 0 and 255.</summary>
    HasTranslucent = 0x0004,

    /// <summary>The image can be scaled within limits.</summary>
    PartiallyScalable = 0x0008,

    /// <summary>The pixels are RGB.</summary>
    ColorSpaceRgb = 0x0010,

    /// <summary>The pixels are CMYK.</summary>
    ColorSpaceCmyk = 0x0020,

    /// <summary>The pixels are gray.</summary>
    ColorSpaceGray = 0x0040,

    /// <summary>The file stores YCbCr.</summary>
    ColorSpaceYcbcr = 0x0080,

    /// <summary>The file stores YCCK.</summary>
    ColorSpaceYcck = 0x0100,

    /// <summary>The file states its resolution.</summary>
    HasRealDpi = 0x1000,

    /// <summary>The file states its size in pixels.</summary>
    HasRealPixelSize = 0x2000,

    /// <summary>The pixels came from a file and are read-only to the codec.</summary>
    ReadOnly = 0x00010000,

    /// <summary>The image is cached.</summary>
    Caching = 0x00020000,
}

/// <summary>What an <see cref="ImageCodecInfo"/> can do.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="codecs"/>
/// </example>
[Flags]
public enum ImageCodecFlags
{
    /// <summary>It writes images.</summary>
    Encoder = 0x00000001,

    /// <summary>It reads images.</summary>
    Decoder = 0x00000002,

    /// <summary>It handles bitmaps.</summary>
    SupportBitmap = 0x00000004,

    /// <summary>It handles vector images.</summary>
    SupportVector = 0x00000008,

    /// <summary>It writes to seekable streams.</summary>
    SeekableEncode = 0x00000010,

    /// <summary>It reads in one blocking call.</summary>
    BlockingDecode = 0x00000020,

    /// <summary>It is built in.</summary>
    Builtin = 0x00010000,

    /// <summary>It belongs to the system.</summary>
    System = 0x00020000,

    /// <summary>A user installed it.</summary>
    User = 0x00040000,
}

/// <summary>What a <see cref="ColorPalette"/>'s <see cref="ColorPalette.Flags"/> say about its colors.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="palettes"/>
/// </example>
[Flags]
public enum PaletteFlags
{
    /// <summary>Some colors have alpha.</summary>
    HasAlpha = 0x0001,

    /// <summary>The colors are grays.</summary>
    GrayScale = 0x0002,

    /// <summary>The colors are a halftone palette.</summary>
    Halftone = 0x0004,
}

/// <summary>The fixed palettes a <see cref="ColorPalette"/> can be built from.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="palettes"/>
/// </example>
public enum PaletteType
{
    /// <summary>A palette of the caller's colors.</summary>
    Custom = 0,

    /// <summary>Black and white.</summary>
    FixedBlackAndWhite = 2,

    /// <summary>8 colors: every mix of full red, green and blue.</summary>
    FixedHalftone8 = 3,

    /// <summary>27 colors: 3 levels of each channel.</summary>
    FixedHalftone27 = 4,

    /// <summary>64 colors: 4 levels of each channel.</summary>
    FixedHalftone64 = 5,

    /// <summary>125 colors: 5 levels of each channel.</summary>
    FixedHalftone125 = 6,

    /// <summary>216 colors: 6 levels of each channel.</summary>
    FixedHalftone216 = 7,

    /// <summary>252 colors: 6 levels of red and blue and 7 of green.</summary>
    FixedHalftone252 = 8,

    /// <summary>256 colors: 8 levels of red and green and 4 of blue.</summary>
    FixedHalftone256 = 9,
}

/// <summary>How <see cref="Bitmap.ConvertFormat(PixelFormat, DitherType, PaletteType, ColorPalette?, float)"/> dithers to fewer colors.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="palettes"/>
/// </example>
public enum DitherType
{
    /// <summary>No dithering: each pixel takes the nearest color.</summary>
    None = 0,

    /// <summary>The nearest color; the same as <see cref="None"/> here.</summary>
    Solid = 1,

    /// <summary>A 4 by 4 ordered dither.</summary>
    Ordered4x4 = 2,

    /// <summary>An 8 by 8 ordered dither.</summary>
    Ordered8x8 = 3,

    /// <summary>A 16 by 16 ordered dither.</summary>
    Ordered16x16 = 4,

    /// <summary>A 4 by 4 spiral dither.</summary>
    Spiral4x4 = 5,

    /// <summary>An 8 by 8 spiral dither.</summary>
    Spiral8x8 = 6,

    /// <summary>A 4 by 4 dual spiral dither.</summary>
    DualSpiral4x4 = 7,

    /// <summary>An 8 by 8 dual spiral dither.</summary>
    DualSpiral8x8 = 8,

    /// <summary>Error diffusion.</summary>
    ErrorDiffusion = 9,
}

/// <summary>The type of an <see cref="EncoderParameter"/>'s values.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="codecs"/>
/// </example>
public enum EncoderParameterValueType
{
    /// <summary>8-bit unsigned integers.</summary>
    ValueTypeByte = 1,

    /// <summary>A null-terminated ASCII string.</summary>
    ValueTypeAscii = 2,

    /// <summary>16-bit unsigned integers.</summary>
    ValueTypeShort = 3,

    /// <summary>32-bit unsigned integers.</summary>
    ValueTypeLong = 4,

    /// <summary>Pairs of 32-bit integers: a numerator and a denominator.</summary>
    ValueTypeRational = 5,

    /// <summary>Pairs of 32-bit integers: a range.</summary>
    ValueTypeLongRange = 6,

    /// <summary>Bytes of any meaning.</summary>
    ValueTypeUndefined = 7,

    /// <summary>Pairs of rationals: a range.</summary>
    ValueTypeRationalRange = 8,

    /// <summary>A pointer to a block of values.</summary>
    ValueTypePointer = 9,
}

/// <summary>The values of the <see cref="Encoder"/> parameters that take a choice.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="codecs"/>
/// </example>
public enum EncoderValue
{
    /// <summary>CMYK color.</summary>
    ColorTypeCMYK = 0,

    /// <summary>YCCK color.</summary>
    ColorTypeYCCK = 1,

    /// <summary>TIFF LZW compression.</summary>
    CompressionLZW = 2,

    /// <summary>TIFF CCITT Group 3 compression, for 1-bit images.</summary>
    CompressionCCITT3 = 3,

    /// <summary>TIFF CCITT Group 4 compression, for 1-bit images.</summary>
    CompressionCCITT4 = 4,

    /// <summary>TIFF run-length (PackBits) compression.</summary>
    CompressionRle = 5,

    /// <summary>No compression.</summary>
    CompressionNone = 6,

    /// <summary>Interlaced rows.</summary>
    ScanMethodInterlaced = 7,

    /// <summary>Rows in order.</summary>
    ScanMethodNonInterlaced = 8,

    /// <summary>GIF 87a.</summary>
    VersionGif87 = 9,

    /// <summary>GIF 89a.</summary>
    VersionGif89 = 10,

    /// <summary>A progressive JPEG.</summary>
    RenderProgressive = 11,

    /// <summary>A baseline JPEG.</summary>
    RenderNonProgressive = 12,

    /// <summary>Rotate 90 degrees clockwise.</summary>
    TransformRotate90 = 13,

    /// <summary>Rotate 180 degrees.</summary>
    TransformRotate180 = 14,

    /// <summary>Rotate 270 degrees clockwise.</summary>
    TransformRotate270 = 15,

    /// <summary>Flip left to right.</summary>
    TransformFlipHorizontal = 16,

    /// <summary>Flip top to bottom.</summary>
    TransformFlipVertical = 17,

    /// <summary>Start a multi-frame file: frames follow with SaveAdd.</summary>
    MultiFrame = 18,

    /// <summary>The last frame of a multi-frame file.</summary>
    LastFrame = 19,

    /// <summary>Close a multi-frame file.</summary>
    Flush = 20,

    /// <summary>Frames in time, as an animation.</summary>
    FrameDimensionTime = 21,

    /// <summary>Frames as resolutions.</summary>
    FrameDimensionResolution = 22,

    /// <summary>Frames as pages.</summary>
    FrameDimensionPage = 23,
}
