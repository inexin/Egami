namespace Egami.Formats.Tiff;

/// <summary>The TIFF 6.0, supplement and EXIF tag numbers the reader and writer use.</summary>
internal static class TiffTags
{
    internal const int NewSubfileType = 254;
    internal const int SubfileType = 255;
    internal const int ImageWidth = 256;
    internal const int ImageLength = 257;
    internal const int BitsPerSample = 258;
    internal const int Compression = 259;
    internal const int Photometric = 262;
    internal const int Threshholding = 263;
    internal const int FillOrder = 266;
    internal const int DocumentName = 269;
    internal const int ImageDescription = 270;
    internal const int Make = 271;
    internal const int Model = 272;
    internal const int StripOffsets = 273;
    internal const int Orientation = 274;
    internal const int SamplesPerPixel = 277;
    internal const int RowsPerStrip = 278;
    internal const int StripByteCounts = 279;
    internal const int MinSampleValue = 280;
    internal const int MaxSampleValue = 281;
    internal const int XResolution = 282;
    internal const int YResolution = 283;
    internal const int PlanarConfiguration = 284;
    internal const int PageName = 285;
    internal const int FreeOffsets = 288;
    internal const int FreeByteCounts = 289;
    internal const int T4Options = 292;
    internal const int T6Options = 293;
    internal const int ResolutionUnit = 296;
    internal const int PageNumber = 297;
    internal const int TransferFunction = 301;
    internal const int Software = 305;
    internal const int DateTime = 306;
    internal const int Artist = 315;
    internal const int HostComputer = 316;
    internal const int Predictor = 317;
    internal const int WhitePoint = 318;
    internal const int PrimaryChromaticities = 319;
    internal const int ColorMap = 320;
    internal const int TileWidth = 322;
    internal const int TileLength = 323;
    internal const int TileOffsets = 324;
    internal const int TileByteCounts = 325;
    internal const int SubIfds = 330;
    internal const int InkSet = 332;
    internal const int InkNames = 333;
    internal const int NumberOfInks = 334;
    internal const int ExtraSamples = 338;
    internal const int SampleFormat = 339;
    internal const int SMinSampleValue = 340;
    internal const int SMaxSampleValue = 341;
    internal const int JpegTables = 347;
    internal const int JpegProc = 512;
    internal const int JpegInterchangeFormat = 513;
    internal const int JpegInterchangeFormatLength = 514;
    internal const int JpegRestartInterval = 515;
    internal const int JpegLosslessPredictors = 517;
    internal const int JpegPointTransforms = 518;
    internal const int JpegQTables = 519;
    internal const int JpegDcTables = 520;
    internal const int JpegAcTables = 521;
    internal const int YCbCrCoefficients = 529;
    internal const int YCbCrSubSampling = 530;
    internal const int YCbCrPositioning = 531;
    internal const int ReferenceBlackWhite = 532;
    internal const int Xmp = 700;
    internal const int Copyright = 33432;
    internal const int Iptc = 33723;
    internal const int Photoshop = 34377;
    internal const int ExifIfd = 34665;
    internal const int IccProfile = 34675;
    internal const int GpsIfd = 34853;
    internal const int InteroperabilityIfd = 40965;

    /// <summary>Returns whether a tag describes the pixel layout, so it always comes from the writer, never from metadata.</summary>
    internal static bool IsLayout(int tag) => tag is NewSubfileType or SubfileType or ImageWidth or ImageLength or BitsPerSample or Compression
        or Photometric or Threshholding or FillOrder or StripOffsets or SamplesPerPixel or RowsPerStrip or StripByteCounts
        or MinSampleValue or MaxSampleValue or PlanarConfiguration or FreeOffsets or FreeByteCounts or T4Options or T6Options
        or PageNumber or Predictor or ColorMap or TileWidth or TileLength or TileOffsets or TileByteCounts or SubIfds or InkSet
        or InkNames or NumberOfInks or ExtraSamples or SampleFormat or SMinSampleValue or SMaxSampleValue or JpegTables
        or (>= JpegProc and <= JpegAcTables) or YCbCrCoefficients or YCbCrSubSampling or YCbCrPositioning or ReferenceBlackWhite
        or XResolution or YResolution or ResolutionUnit or Orientation or Xmp or Iptc or Photoshop or ExifIfd or IccProfile or GpsIfd
        or InteroperabilityIfd or WhitePoint or PrimaryChromaticities or TransferFunction;
}

/// <summary>The TIFF compression codes, including ones the reader only names in its errors.</summary>
internal static class TiffCodes
{
    internal const int None = 1;
    internal const int CcittRle = 2;
    internal const int CcittGroup3 = 3;
    internal const int CcittGroup4 = 4;
    internal const int Lzw = 5;
    internal const int OldJpeg = 6;
    internal const int Jpeg = 7;
    internal const int Deflate = 8;
    internal const int Next = 32766;
    internal const int CcittRleWord = 32771;
    internal const int PackBits = 32773;
    internal const int ThunderScan = 32809;
    internal const int AdobeDeflate = 32946;
    internal const int Jbig = 34661;
    internal const int SgiLog = 34676;
    internal const int SgiLog24 = 34677;
    internal const int Jpeg2000 = 34712;
    internal const int Lerc = 34887;
    internal const int Lzma = 34925;
    internal const int Zstd = 50000;
    internal const int Webp = 50001;
    internal const int JpegXl = 50002;

    /// <summary>Returns a compression code's name for messages.</summary>
    internal static string Name(int code) => code switch
    {
        None => "None",
        CcittRle => "CCITT RLE",
        CcittGroup3 => "CCITT Group 3",
        CcittGroup4 => "CCITT Group 4",
        Lzw => "LZW",
        OldJpeg => "old-style JPEG",
        Jpeg => "JPEG",
        Deflate or AdobeDeflate => "Deflate",
        Next => "NeXT 2-bit",
        CcittRleWord => "CCITT RLEW",
        PackBits => "PackBits",
        ThunderScan => "ThunderScan",
        Jbig => "JBIG",
        SgiLog or SgiLog24 => "SGILog",
        Jpeg2000 => "JPEG 2000",
        Lerc => "LERC",
        Lzma => "LZMA",
        Zstd => "ZSTD",
        Webp => "WebP",
        JpegXl => "JPEG XL",
        _ => "an unknown scheme",
    };
}
