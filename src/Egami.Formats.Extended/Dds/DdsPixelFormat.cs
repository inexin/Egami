namespace Egami.Formats.Extended.Dds;

/// <summary>The pixel type a DDS format loads as when no other is asked for.</summary>
internal enum DdsNatural : byte
{
    Gray8,
    Gray16,
    GrayF32,
    GrayAlpha8,
    GrayAlpha16,
    Alpha8,
    Rgb8,
    Rgb16,
    RgbF32,
    Rgba8,
    Rgba16,
    RgbaF16,
    RgbaF32,
    Rgb10A2,
    Bgr565,
    Bgr555,
    Bgra5551,
    Bgra4444,
}

/// <summary>How the stored pixels of a format become the natural pixel type.</summary>
internal enum DdsCodec : byte
{
    /// <summary>The stored bytes are the natural pixel type's memory.</summary>
    Copy,
    Bgra8ToRgba8,
    Bgrx8ToRgb8,
    Rgbx8ToRgb8,
    Rg8ToRgb8,
    Rg16ToRgb16,
    Rg16FToRgbaF16,
    Rg32FToRgbF32,
    R16FToGrayF32,
    R11G11B10FToRgbaF16,
    R9G9B9E5ToRgbaF16,
    XrBiasToRgbaF16,
    Snorm8,
    Sint8,
    Snorm16,
    Sint16,
    Int32,
    UInt32,
    Rg8Snorm,
    Rg8Sint,
    Rg16Snorm,
    Rg16Sint,
    Rg32Int,
    Rg32UInt,
    Rgb32Int,
    Rgb32UInt,
    D32S8,
    Stencil32,
    D24S8,
    Stencil24,
    R1,
    Rgbg,
    Grgb,
    Yuy2,
    Uyvy,
    Y210,
    Y216,
    Ayuv,
    Y410,
    Y416,
    Nv12,
    P016,
    Nv11,
    P208,
    V208,
    V408,
    A4B4G4R4,
    X4R4G4B4,
    Masks,
    Palette8,
    Palette8Alpha,
    L6V5U5,
    X8L8V8U8,
    A2W10V10U10,
    Bc1,
    Bc2,
    Bc3,
    Bc4,
    Bc4Snorm,
    Bc5,
    Bc5Snorm,
    Bc6h,
    Bc6hSigned,
    Bc7,
}

/// <summary>
/// A DDS pixel format as the reader sees it: the DXGI format it is (or maps to, for a legacy header), how its bytes decode and
/// the pixel type it loads as. Legacy bit-mask formats that match no DXGI format keep their masks.
/// </summary>
internal sealed record DdsPixelFormat(int Dxgi, string Name, DdsNatural Natural, DdsCodec Codec, int UnitBytes)
{
    /// <summary>Gets whether the format stores sRGB-encoded values.</summary>
    public bool Srgb { get; init; }

    /// <summary>Gets whether colours are premultiplied by alpha (DXT2 and DXT4).</summary>
    public bool Premultiplied { get; init; }

    /// <summary>Gets the bit masks of a legacy mask format: red (or luminance), green, blue and alpha.</summary>
    public uint[] Masks { get; init; } = [];

    /// <summary>Gets whether data is in 4 × 4 blocks of <see cref="UnitBytes"/> bytes.</summary>
    public bool IsBlock => Codec >= DdsCodec.Bc1;

    /// <summary>Gets whether one unit of <see cref="UnitBytes"/> holds two pixels side by side.</summary>
    public bool IsPair => Codec is DdsCodec.Rgbg or DdsCodec.Grgb or DdsCodec.Yuy2 or DdsCodec.Uyvy or DdsCodec.Y210 or DdsCodec.Y216;

    /// <summary>Gets whether the luma and chroma are stored in separate planes.</summary>
    public bool IsPlanar => Codec is >= DdsCodec.Nv12 and <= DdsCodec.V408;

    /// <summary>Gets whether the format has only two channels (red and green), so <see cref="DdsReaderOptions.ReconstructNormalZ"/> applies.</summary>
    public bool IsTwoChannel => Codec is DdsCodec.Rg8ToRgb8 or DdsCodec.Rg16ToRgb16 or DdsCodec.Rg8Snorm or DdsCodec.Rg8Sint or DdsCodec.Rg16Snorm
        or DdsCodec.Rg16Sint or DdsCodec.Bc5 or DdsCodec.Bc5Snorm or DdsCodec.Rg16FToRgbaF16 or DdsCodec.Rg32FToRgbF32;

    /// <summary>Gets the bytes of one row of <paramref name="width"/> pixels, or of one row of blocks.</summary>
    public long RowBytes(long width)
    {
        if (IsBlock)
        {
            return Math.Max(1, (width + 3) / 4) * UnitBytes;
        }

        if (IsPair)
        {
            return ((width + 1) / 2) * UnitBytes;
        }

        return Codec switch
        {
            DdsCodec.R1 => (width + 7) / 8,
            DdsCodec.Nv12 or DdsCodec.Nv11 or DdsCodec.P208 or DdsCodec.V208 or DdsCodec.V408 => width,
            DdsCodec.P016 => ((width + 1) / 2) * 4,
            _ => width * UnitBytes,
        };
    }

    /// <summary>Gets the bytes of one surface of <paramref name="width"/> × <paramref name="height"/> pixels.</summary>
    public long SurfaceBytes(long width, long height)
    {
        long row = RowBytes(width);
        long half = (height + 1) / 2;
        return Codec switch
        {
            _ when IsBlock => row * Math.Max(1, (height + 3) / 4),
            DdsCodec.Nv12 or DdsCodec.P016 => row * (height + half),
            DdsCodec.Nv11 or DdsCodec.P208 => row * height * 2,
            DdsCodec.V208 => row * (height + (half * 2)),
            DdsCodec.V408 => row * (height + ((height / 2) * 4)),
            _ => row * height,
        };
    }

    /// <summary>Returns the format a DX10 header's DXGI format number names, or <see langword="null"/> if Egami cannot read it.</summary>
    public static DdsPixelFormat? FromDxgi(int dxgi)
    {
        return dxgi switch
        {
            1 => new(dxgi, "R32G32B32A32_TYPELESS", DdsNatural.RgbaF32, DdsCodec.Copy, 16),
            2 => new(dxgi, "R32G32B32A32_FLOAT", DdsNatural.RgbaF32, DdsCodec.Copy, 16),
            3 => new(dxgi, "R32G32B32A32_UINT", DdsNatural.RgbaF32, DdsCodec.UInt32, 16),
            4 => new(dxgi, "R32G32B32A32_SINT", DdsNatural.RgbaF32, DdsCodec.Int32, 16),
            5 => new(dxgi, "R32G32B32_TYPELESS", DdsNatural.RgbF32, DdsCodec.Copy, 12),
            6 => new(dxgi, "R32G32B32_FLOAT", DdsNatural.RgbF32, DdsCodec.Copy, 12),
            7 => new(dxgi, "R32G32B32_UINT", DdsNatural.RgbF32, DdsCodec.Rgb32UInt, 12),
            8 => new(dxgi, "R32G32B32_SINT", DdsNatural.RgbF32, DdsCodec.Rgb32Int, 12),
            9 => new(dxgi, "R16G16B16A16_TYPELESS", DdsNatural.Rgba16, DdsCodec.Copy, 8),
            10 => new(dxgi, "R16G16B16A16_FLOAT", DdsNatural.RgbaF16, DdsCodec.Copy, 8),
            11 => new(dxgi, "R16G16B16A16_UNORM", DdsNatural.Rgba16, DdsCodec.Copy, 8),
            12 => new(dxgi, "R16G16B16A16_UINT", DdsNatural.Rgba16, DdsCodec.Copy, 8),
            13 => new(dxgi, "R16G16B16A16_SNORM", DdsNatural.Rgba16, DdsCodec.Snorm16, 8),
            14 => new(dxgi, "R16G16B16A16_SINT", DdsNatural.Rgba16, DdsCodec.Sint16, 8),
            15 => new(dxgi, "R32G32_TYPELESS", DdsNatural.RgbF32, DdsCodec.Rg32FToRgbF32, 8),
            16 => new(dxgi, "R32G32_FLOAT", DdsNatural.RgbF32, DdsCodec.Rg32FToRgbF32, 8),
            17 => new(dxgi, "R32G32_UINT", DdsNatural.RgbF32, DdsCodec.Rg32UInt, 8),
            18 => new(dxgi, "R32G32_SINT", DdsNatural.RgbF32, DdsCodec.Rg32Int, 8),
            19 => new(dxgi, "R32G8X24_TYPELESS", DdsNatural.GrayF32, DdsCodec.D32S8, 8),
            20 => new(dxgi, "D32_FLOAT_S8X24_UINT", DdsNatural.GrayF32, DdsCodec.D32S8, 8),
            21 => new(dxgi, "R32_FLOAT_X8X24_TYPELESS", DdsNatural.GrayF32, DdsCodec.D32S8, 8),
            22 => new(dxgi, "X32_TYPELESS_G8X24_UINT", DdsNatural.Gray8, DdsCodec.Stencil32, 8),
            23 => new(dxgi, "R10G10B10A2_TYPELESS", DdsNatural.Rgb10A2, DdsCodec.Copy, 4),
            24 => new(dxgi, "R10G10B10A2_UNORM", DdsNatural.Rgb10A2, DdsCodec.Copy, 4),
            25 => new(dxgi, "R10G10B10A2_UINT", DdsNatural.Rgb10A2, DdsCodec.Copy, 4),
            26 => new(dxgi, "R11G11B10_FLOAT", DdsNatural.RgbaF16, DdsCodec.R11G11B10FToRgbaF16, 4),
            27 => new(dxgi, "R8G8B8A8_TYPELESS", DdsNatural.Rgba8, DdsCodec.Copy, 4),
            28 => new(dxgi, "R8G8B8A8_UNORM", DdsNatural.Rgba8, DdsCodec.Copy, 4),
            29 => new(dxgi, "R8G8B8A8_UNORM_SRGB", DdsNatural.Rgba8, DdsCodec.Copy, 4) { Srgb = true },
            30 => new(dxgi, "R8G8B8A8_UINT", DdsNatural.Rgba8, DdsCodec.Copy, 4),
            31 => new(dxgi, "R8G8B8A8_SNORM", DdsNatural.Rgba8, DdsCodec.Snorm8, 4),
            32 => new(dxgi, "R8G8B8A8_SINT", DdsNatural.Rgba8, DdsCodec.Sint8, 4),
            33 => new(dxgi, "R16G16_TYPELESS", DdsNatural.Rgb16, DdsCodec.Rg16ToRgb16, 4),
            34 => new(dxgi, "R16G16_FLOAT", DdsNatural.RgbaF16, DdsCodec.Rg16FToRgbaF16, 4),
            35 => new(dxgi, "R16G16_UNORM", DdsNatural.Rgb16, DdsCodec.Rg16ToRgb16, 4),
            36 => new(dxgi, "R16G16_UINT", DdsNatural.Rgb16, DdsCodec.Rg16ToRgb16, 4),
            37 => new(dxgi, "R16G16_SNORM", DdsNatural.Rgb16, DdsCodec.Rg16Snorm, 4),
            38 => new(dxgi, "R16G16_SINT", DdsNatural.Rgb16, DdsCodec.Rg16Sint, 4),
            39 => new(dxgi, "R32_TYPELESS", DdsNatural.GrayF32, DdsCodec.Copy, 4),
            40 => new(dxgi, "D32_FLOAT", DdsNatural.GrayF32, DdsCodec.Copy, 4),
            41 => new(dxgi, "R32_FLOAT", DdsNatural.GrayF32, DdsCodec.Copy, 4),
            42 => new(dxgi, "R32_UINT", DdsNatural.GrayF32, DdsCodec.UInt32, 4),
            43 => new(dxgi, "R32_SINT", DdsNatural.GrayF32, DdsCodec.Int32, 4),
            44 => new(dxgi, "R24G8_TYPELESS", DdsNatural.GrayF32, DdsCodec.D24S8, 4),
            45 => new(dxgi, "D24_UNORM_S8_UINT", DdsNatural.GrayF32, DdsCodec.D24S8, 4),
            46 => new(dxgi, "R24_UNORM_X8_TYPELESS", DdsNatural.GrayF32, DdsCodec.D24S8, 4),
            47 => new(dxgi, "X24_TYPELESS_G8_UINT", DdsNatural.Gray8, DdsCodec.Stencil24, 4),
            48 => new(dxgi, "R8G8_TYPELESS", DdsNatural.Rgb8, DdsCodec.Rg8ToRgb8, 2),
            49 => new(dxgi, "R8G8_UNORM", DdsNatural.Rgb8, DdsCodec.Rg8ToRgb8, 2),
            50 => new(dxgi, "R8G8_UINT", DdsNatural.Rgb8, DdsCodec.Rg8ToRgb8, 2),
            51 => new(dxgi, "R8G8_SNORM", DdsNatural.Rgb8, DdsCodec.Rg8Snorm, 2),
            52 => new(dxgi, "R8G8_SINT", DdsNatural.Rgb8, DdsCodec.Rg8Sint, 2),
            53 => new(dxgi, "R16_TYPELESS", DdsNatural.Gray16, DdsCodec.Copy, 2),
            54 => new(dxgi, "R16_FLOAT", DdsNatural.GrayF32, DdsCodec.R16FToGrayF32, 2),
            55 => new(dxgi, "D16_UNORM", DdsNatural.Gray16, DdsCodec.Copy, 2),
            56 => new(dxgi, "R16_UNORM", DdsNatural.Gray16, DdsCodec.Copy, 2),
            57 => new(dxgi, "R16_UINT", DdsNatural.Gray16, DdsCodec.Copy, 2),
            58 => new(dxgi, "R16_SNORM", DdsNatural.Gray16, DdsCodec.Snorm16, 2),
            59 => new(dxgi, "R16_SINT", DdsNatural.Gray16, DdsCodec.Sint16, 2),
            60 => new(dxgi, "R8_TYPELESS", DdsNatural.Gray8, DdsCodec.Copy, 1),
            61 => new(dxgi, "R8_UNORM", DdsNatural.Gray8, DdsCodec.Copy, 1),
            62 => new(dxgi, "R8_UINT", DdsNatural.Gray8, DdsCodec.Copy, 1),
            63 => new(dxgi, "R8_SNORM", DdsNatural.Gray8, DdsCodec.Snorm8, 1),
            64 => new(dxgi, "R8_SINT", DdsNatural.Gray8, DdsCodec.Sint8, 1),
            65 => new(dxgi, "A8_UNORM", DdsNatural.Alpha8, DdsCodec.Copy, 1),
            66 => new(dxgi, "R1_UNORM", DdsNatural.Gray8, DdsCodec.R1, 1),
            67 => new(dxgi, "R9G9B9E5_SHAREDEXP", DdsNatural.RgbaF16, DdsCodec.R9G9B9E5ToRgbaF16, 4),
            68 => new(dxgi, "R8G8_B8G8_UNORM", DdsNatural.Rgb8, DdsCodec.Rgbg, 4),
            69 => new(dxgi, "G8R8_G8B8_UNORM", DdsNatural.Rgb8, DdsCodec.Grgb, 4),
            70 => new(dxgi, "BC1_TYPELESS", DdsNatural.Rgba8, DdsCodec.Bc1, 8),
            71 => new(dxgi, "BC1_UNORM", DdsNatural.Rgba8, DdsCodec.Bc1, 8),
            72 => new(dxgi, "BC1_UNORM_SRGB", DdsNatural.Rgba8, DdsCodec.Bc1, 8) { Srgb = true },
            73 => new(dxgi, "BC2_TYPELESS", DdsNatural.Rgba8, DdsCodec.Bc2, 16),
            74 => new(dxgi, "BC2_UNORM", DdsNatural.Rgba8, DdsCodec.Bc2, 16),
            75 => new(dxgi, "BC2_UNORM_SRGB", DdsNatural.Rgba8, DdsCodec.Bc2, 16) { Srgb = true },
            76 => new(dxgi, "BC3_TYPELESS", DdsNatural.Rgba8, DdsCodec.Bc3, 16),
            77 => new(dxgi, "BC3_UNORM", DdsNatural.Rgba8, DdsCodec.Bc3, 16),
            78 => new(dxgi, "BC3_UNORM_SRGB", DdsNatural.Rgba8, DdsCodec.Bc3, 16) { Srgb = true },
            79 => new(dxgi, "BC4_TYPELESS", DdsNatural.Gray8, DdsCodec.Bc4, 8),
            80 => new(dxgi, "BC4_UNORM", DdsNatural.Gray8, DdsCodec.Bc4, 8),
            81 => new(dxgi, "BC4_SNORM", DdsNatural.Gray8, DdsCodec.Bc4Snorm, 8),
            82 => new(dxgi, "BC5_TYPELESS", DdsNatural.Rgb8, DdsCodec.Bc5, 16),
            83 => new(dxgi, "BC5_UNORM", DdsNatural.Rgb8, DdsCodec.Bc5, 16),
            84 => new(dxgi, "BC5_SNORM", DdsNatural.Rgb8, DdsCodec.Bc5Snorm, 16),
            85 => new(dxgi, "B5G6R5_UNORM", DdsNatural.Bgr565, DdsCodec.Copy, 2),
            86 => new(dxgi, "B5G5R5A1_UNORM", DdsNatural.Bgra5551, DdsCodec.Copy, 2),
            87 => new(dxgi, "B8G8R8A8_UNORM", DdsNatural.Rgba8, DdsCodec.Bgra8ToRgba8, 4),
            88 => new(dxgi, "B8G8R8X8_UNORM", DdsNatural.Rgb8, DdsCodec.Bgrx8ToRgb8, 4),
            89 => new(dxgi, "R10G10B10_XR_BIAS_A2_UNORM", DdsNatural.RgbaF16, DdsCodec.XrBiasToRgbaF16, 4),
            90 => new(dxgi, "B8G8R8A8_TYPELESS", DdsNatural.Rgba8, DdsCodec.Bgra8ToRgba8, 4),
            91 => new(dxgi, "B8G8R8A8_UNORM_SRGB", DdsNatural.Rgba8, DdsCodec.Bgra8ToRgba8, 4) { Srgb = true },
            92 => new(dxgi, "B8G8R8X8_TYPELESS", DdsNatural.Rgb8, DdsCodec.Bgrx8ToRgb8, 4),
            93 => new(dxgi, "B8G8R8X8_UNORM_SRGB", DdsNatural.Rgb8, DdsCodec.Bgrx8ToRgb8, 4) { Srgb = true },
            94 => new(dxgi, "BC6H_TYPELESS", DdsNatural.RgbaF16, DdsCodec.Bc6h, 16),
            95 => new(dxgi, "BC6H_UF16", DdsNatural.RgbaF16, DdsCodec.Bc6h, 16),
            96 => new(dxgi, "BC6H_SF16", DdsNatural.RgbaF16, DdsCodec.Bc6hSigned, 16),
            97 => new(dxgi, "BC7_TYPELESS", DdsNatural.Rgba8, DdsCodec.Bc7, 16),
            98 => new(dxgi, "BC7_UNORM", DdsNatural.Rgba8, DdsCodec.Bc7, 16),
            99 => new(dxgi, "BC7_UNORM_SRGB", DdsNatural.Rgba8, DdsCodec.Bc7, 16) { Srgb = true },
            100 => new(dxgi, "AYUV", DdsNatural.Rgba8, DdsCodec.Ayuv, 4),
            101 => new(dxgi, "Y410", DdsNatural.Rgb10A2, DdsCodec.Y410, 4),
            102 => new(dxgi, "Y416", DdsNatural.Rgba16, DdsCodec.Y416, 8),
            103 => new(dxgi, "NV12", DdsNatural.Rgb8, DdsCodec.Nv12, 1),
            104 => new(dxgi, "P010", DdsNatural.Rgb10A2, DdsCodec.P016, 2),
            105 => new(dxgi, "P016", DdsNatural.Rgb16, DdsCodec.P016, 2),
            106 => new(dxgi, "420_OPAQUE", DdsNatural.Rgb8, DdsCodec.Nv12, 1),
            107 => new(dxgi, "YUY2", DdsNatural.Rgb8, DdsCodec.Yuy2, 4),
            108 => new(dxgi, "Y210", DdsNatural.Rgb10A2, DdsCodec.Y210, 8),
            109 => new(dxgi, "Y216", DdsNatural.Rgb16, DdsCodec.Y216, 8),
            110 => new(dxgi, "NV11", DdsNatural.Rgb8, DdsCodec.Nv11, 1),
            115 => new(dxgi, "B4G4R4A4_UNORM", DdsNatural.Bgra4444, DdsCodec.Copy, 2),
            130 => new(dxgi, "P208", DdsNatural.Rgb8, DdsCodec.P208, 1),
            131 => new(dxgi, "V208", DdsNatural.Rgb8, DdsCodec.V208, 1),
            132 => new(dxgi, "V408", DdsNatural.Rgb8, DdsCodec.V408, 1),
            191 => new(dxgi, "A4B4G4R4_UNORM", DdsNatural.Bgra4444, DdsCodec.A4B4G4R4, 2),
            _ => null,
        };
    }

    /// <summary>Returns the sRGB counterpart of a UNORM format, for a legacy header NVTT marked sRGB.</summary>
    public DdsPixelFormat WithSrgb()
    {
        int srgb = Dxgi switch
        {
            28 => 29,
            71 => 72,
            74 => 75,
            77 => 78,
            87 => 91,
            88 => 93,
            98 => 99,
            _ => 0,
        };
        return srgb == 0 ? this : FromDxgi(srgb)! with { Premultiplied = Premultiplied, Codec = Codec, Natural = Natural, UnitBytes = UnitBytes };
    }
}
