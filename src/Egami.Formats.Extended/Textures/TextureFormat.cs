namespace Egami.Formats.Extended.Textures;

/// <summary>The pixel type a texture format loads as when no other is asked for.</summary>
internal enum TextureNatural : byte
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
internal enum TextureCodec : byte
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
    Bgr8ToRgb8,
    Rgb16FToRgbaF16,
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

    /// <summary>BC1 without alpha (Vulkan's BC1_RGB, OpenGL's DXT1 RGB): the punch-through texels read as opaque black.</summary>
    Bc1Opaque,

    /// <summary>ETC2 RGB8, and ETC1, which is a subset of it.</summary>
    Etc2Rgb,

    /// <summary>ETC2 RGB8 with punch-through alpha.</summary>
    Etc2RgbA1,

    /// <summary>ETC2 RGBA8: an EAC alpha block, then an ETC2 colour block.</summary>
    Etc2Rgba,
    EacR11,
    EacR11Snorm,
    EacRg11,
    EacRg11Snorm,

    /// <summary>ASTC, LDR UNORM; the footprint is the format's block size.</summary>
    AstcLdr,

    /// <summary>ASTC, LDR sRGB.</summary>
    AstcSrgb,

    /// <summary>ASTC, HDR (SFLOAT).</summary>
    AstcHdr,

    /// <summary>Basis Universal ETC1S, as the ETC1 blocks a BasisLZ slice decodes to.</summary>
    Etc1s,

    /// <summary>Basis Universal ETC1S with alpha: the ETC1 block of the colour slice, then that of the alpha slice (its green).</summary>
    Etc1sAlpha,

    /// <summary>Basis Universal UASTC LDR 4 × 4.</summary>
    Uastc,
}

/// <summary>
/// A texture format as the DDS and KTX readers see it: the DXGI format it is or maps to (0 for none), how its bytes decode and
/// the pixel type it loads as. <see cref="FromDxgi"/>, <see cref="FromVk"/> and <see cref="FromGl"/> map the three containers'
/// format numbers to one description. Legacy DDS bit-mask formats that match no DXGI format keep their masks.
/// </summary>
internal sealed record TextureFormat(int Dxgi, string Name, TextureNatural Natural, TextureCodec Codec, int UnitBytes)
{
    /// <summary>Gets whether the format stores sRGB-encoded values.</summary>
    public bool Srgb { get; init; }

    /// <summary>Gets the Vulkan format number the format is, for one <see cref="FromVk"/> or <see cref="FromGl"/> gave, else 0.</summary>
    public int Vk { get; init; }

    /// <summary>Gets whether colours are premultiplied by alpha (DXT2 and DXT4).</summary>
    public bool Premultiplied { get; init; }

    /// <summary>Gets the bit masks of a legacy mask format: red (or luminance), green, blue and alpha.</summary>
    public uint[] Masks { get; init; } = [];

    private readonly int _blockWidth;
    private readonly int _blockHeight;

    /// <summary>Gets whether data is in blocks of <see cref="BlockWidth"/> × <see cref="BlockHeight"/> texels and <see cref="UnitBytes"/> bytes.</summary>
    public bool IsBlock => Codec >= TextureCodec.Bc1;

    /// <summary>Gets whether the format is one of the ASTC formats.</summary>
    public bool IsAstc => Codec is TextureCodec.AstcLdr or TextureCodec.AstcSrgb or TextureCodec.AstcHdr;

    /// <summary>Gets whether one unit of <see cref="UnitBytes"/> holds two pixels side by side.</summary>
    public bool IsPair => Codec is TextureCodec.Rgbg or TextureCodec.Grgb or TextureCodec.Yuy2 or TextureCodec.Uyvy or TextureCodec.Y210 or TextureCodec.Y216;

    /// <summary>Gets whether the luma and chroma are stored in separate planes.</summary>
    public bool IsPlanar => Codec is >= TextureCodec.Nv12 and <= TextureCodec.V408;

    /// <summary>Gets whether the format has only two channels (red and green), so a reader's ReconstructNormalZ applies.</summary>
    public bool IsTwoChannel => Codec is TextureCodec.Rg8ToRgb8 or TextureCodec.Rg16ToRgb16 or TextureCodec.Rg8Snorm or TextureCodec.Rg8Sint or TextureCodec.Rg16Snorm
        or TextureCodec.Rg16Sint or TextureCodec.Bc5 or TextureCodec.Bc5Snorm or TextureCodec.Rg16FToRgbaF16 or TextureCodec.Rg32FToRgbF32
        or TextureCodec.EacRg11 or TextureCodec.EacRg11Snorm;

    /// <summary>Gets the width of one block of texels: 4 for most block formats (ASTC's footprint varies), 2 for the pair formats, else 1.</summary>
    public int BlockWidth
    {
        get => _blockWidth != 0 ? _blockWidth : IsBlock ? 4 : IsPair ? 2 : 1;
        init => _blockWidth = value;
    }

    /// <summary>Gets the height of one block of texels: 4 for most block formats (ASTC's footprint varies), else 1.</summary>
    public int BlockHeight
    {
        get => _blockHeight != 0 ? _blockHeight : IsBlock ? 4 : 1;
        init => _blockHeight = value;
    }

    /// <summary>Gets the bytes of one block of texels (of one texel, for the pixel formats).</summary>
    public int BytesPerBlock => UnitBytes;

    /// <summary>Gets the number of channels the format stores.</summary>
    public int Channels => Codec switch
    {
        _ when IsTwoChannel => 2,
        TextureCodec.Bc1Opaque => 3,
        TextureCodec.Bc4 or TextureCodec.Bc4Snorm or TextureCodec.EacR11 or TextureCodec.EacR11Snorm => 1,
        TextureCodec.Masks => Masks.Count(m => m != 0),
        _ => Natural switch
        {
            TextureNatural.Gray8 or TextureNatural.Gray16 or TextureNatural.GrayF32 or TextureNatural.Alpha8 => 1,
            TextureNatural.GrayAlpha8 or TextureNatural.GrayAlpha16 => 2,
            TextureNatural.Rgb8 or TextureNatural.Rgb16 or TextureNatural.RgbF32 or TextureNatural.Bgr565 or TextureNatural.Bgr555 => 3,
            _ => 4,
        },
    };

    /// <summary>Gets whether the stored values are signed (SNORM, SINT or signed float blocks); they load mapped to unsigned.</summary>
    public bool Signed => Codec is TextureCodec.Snorm8 or TextureCodec.Sint8 or TextureCodec.Snorm16 or TextureCodec.Sint16 or TextureCodec.Int32
        or TextureCodec.Rg8Snorm or TextureCodec.Rg8Sint or TextureCodec.Rg16Snorm or TextureCodec.Rg16Sint or TextureCodec.Rg32Int
        or TextureCodec.Rgb32Int or TextureCodec.Bc4Snorm or TextureCodec.Bc5Snorm or TextureCodec.Bc6hSigned or TextureCodec.EacR11Snorm
        or TextureCodec.EacRg11Snorm;

    /// <summary>Gets whether the format holds high dynamic range values, which load as floating point.</summary>
    public bool Hdr => Natural is TextureNatural.GrayF32 or TextureNatural.RgbF32 or TextureNatural.RgbaF16 or TextureNatural.RgbaF32;

    /// <summary>Gets the bytes of one row of <paramref name="width"/> pixels, or of one row of blocks.</summary>
    public long RowBytes(long width)
    {
        if (IsBlock)
        {
            return Math.Max(1, (width + BlockWidth - 1) / BlockWidth) * UnitBytes;
        }

        if (IsPair)
        {
            return ((width + 1) / 2) * UnitBytes;
        }

        return Codec switch
        {
            TextureCodec.R1 => (width + 7) / 8,
            TextureCodec.Nv12 or TextureCodec.Nv11 or TextureCodec.P208 or TextureCodec.V208 or TextureCodec.V408 => width,
            TextureCodec.P016 => ((width + 1) / 2) * 4,
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
            _ when IsBlock => row * Math.Max(1, (height + BlockHeight - 1) / BlockHeight),
            TextureCodec.Nv12 or TextureCodec.P016 => row * (height + half),
            TextureCodec.Nv11 or TextureCodec.P208 => row * height * 2,
            TextureCodec.V208 => row * (height + (half * 2)),
            TextureCodec.V408 => row * (height + ((height / 2) * 4)),
            _ => row * height,
        };
    }

    /// <summary>Returns the format a DX10 header's DXGI format number names, or <see langword="null"/> if Egami cannot read it.</summary>
    public static TextureFormat? FromDxgi(int dxgi)
    {
        return dxgi switch
        {
            1 => new(dxgi, "R32G32B32A32_TYPELESS", TextureNatural.RgbaF32, TextureCodec.Copy, 16),
            2 => new(dxgi, "R32G32B32A32_FLOAT", TextureNatural.RgbaF32, TextureCodec.Copy, 16),
            3 => new(dxgi, "R32G32B32A32_UINT", TextureNatural.RgbaF32, TextureCodec.UInt32, 16),
            4 => new(dxgi, "R32G32B32A32_SINT", TextureNatural.RgbaF32, TextureCodec.Int32, 16),
            5 => new(dxgi, "R32G32B32_TYPELESS", TextureNatural.RgbF32, TextureCodec.Copy, 12),
            6 => new(dxgi, "R32G32B32_FLOAT", TextureNatural.RgbF32, TextureCodec.Copy, 12),
            7 => new(dxgi, "R32G32B32_UINT", TextureNatural.RgbF32, TextureCodec.Rgb32UInt, 12),
            8 => new(dxgi, "R32G32B32_SINT", TextureNatural.RgbF32, TextureCodec.Rgb32Int, 12),
            9 => new(dxgi, "R16G16B16A16_TYPELESS", TextureNatural.Rgba16, TextureCodec.Copy, 8),
            10 => new(dxgi, "R16G16B16A16_FLOAT", TextureNatural.RgbaF16, TextureCodec.Copy, 8),
            11 => new(dxgi, "R16G16B16A16_UNORM", TextureNatural.Rgba16, TextureCodec.Copy, 8),
            12 => new(dxgi, "R16G16B16A16_UINT", TextureNatural.Rgba16, TextureCodec.Copy, 8),
            13 => new(dxgi, "R16G16B16A16_SNORM", TextureNatural.Rgba16, TextureCodec.Snorm16, 8),
            14 => new(dxgi, "R16G16B16A16_SINT", TextureNatural.Rgba16, TextureCodec.Sint16, 8),
            15 => new(dxgi, "R32G32_TYPELESS", TextureNatural.RgbF32, TextureCodec.Rg32FToRgbF32, 8),
            16 => new(dxgi, "R32G32_FLOAT", TextureNatural.RgbF32, TextureCodec.Rg32FToRgbF32, 8),
            17 => new(dxgi, "R32G32_UINT", TextureNatural.RgbF32, TextureCodec.Rg32UInt, 8),
            18 => new(dxgi, "R32G32_SINT", TextureNatural.RgbF32, TextureCodec.Rg32Int, 8),
            19 => new(dxgi, "R32G8X24_TYPELESS", TextureNatural.GrayF32, TextureCodec.D32S8, 8),
            20 => new(dxgi, "D32_FLOAT_S8X24_UINT", TextureNatural.GrayF32, TextureCodec.D32S8, 8),
            21 => new(dxgi, "R32_FLOAT_X8X24_TYPELESS", TextureNatural.GrayF32, TextureCodec.D32S8, 8),
            22 => new(dxgi, "X32_TYPELESS_G8X24_UINT", TextureNatural.Gray8, TextureCodec.Stencil32, 8),
            23 => new(dxgi, "R10G10B10A2_TYPELESS", TextureNatural.Rgb10A2, TextureCodec.Copy, 4),
            24 => new(dxgi, "R10G10B10A2_UNORM", TextureNatural.Rgb10A2, TextureCodec.Copy, 4),
            25 => new(dxgi, "R10G10B10A2_UINT", TextureNatural.Rgb10A2, TextureCodec.Copy, 4),
            26 => new(dxgi, "R11G11B10_FLOAT", TextureNatural.RgbaF16, TextureCodec.R11G11B10FToRgbaF16, 4),
            27 => new(dxgi, "R8G8B8A8_TYPELESS", TextureNatural.Rgba8, TextureCodec.Copy, 4),
            28 => new(dxgi, "R8G8B8A8_UNORM", TextureNatural.Rgba8, TextureCodec.Copy, 4),
            29 => new(dxgi, "R8G8B8A8_UNORM_SRGB", TextureNatural.Rgba8, TextureCodec.Copy, 4) { Srgb = true },
            30 => new(dxgi, "R8G8B8A8_UINT", TextureNatural.Rgba8, TextureCodec.Copy, 4),
            31 => new(dxgi, "R8G8B8A8_SNORM", TextureNatural.Rgba8, TextureCodec.Snorm8, 4),
            32 => new(dxgi, "R8G8B8A8_SINT", TextureNatural.Rgba8, TextureCodec.Sint8, 4),
            33 => new(dxgi, "R16G16_TYPELESS", TextureNatural.Rgb16, TextureCodec.Rg16ToRgb16, 4),
            34 => new(dxgi, "R16G16_FLOAT", TextureNatural.RgbaF16, TextureCodec.Rg16FToRgbaF16, 4),
            35 => new(dxgi, "R16G16_UNORM", TextureNatural.Rgb16, TextureCodec.Rg16ToRgb16, 4),
            36 => new(dxgi, "R16G16_UINT", TextureNatural.Rgb16, TextureCodec.Rg16ToRgb16, 4),
            37 => new(dxgi, "R16G16_SNORM", TextureNatural.Rgb16, TextureCodec.Rg16Snorm, 4),
            38 => new(dxgi, "R16G16_SINT", TextureNatural.Rgb16, TextureCodec.Rg16Sint, 4),
            39 => new(dxgi, "R32_TYPELESS", TextureNatural.GrayF32, TextureCodec.Copy, 4),
            40 => new(dxgi, "D32_FLOAT", TextureNatural.GrayF32, TextureCodec.Copy, 4),
            41 => new(dxgi, "R32_FLOAT", TextureNatural.GrayF32, TextureCodec.Copy, 4),
            42 => new(dxgi, "R32_UINT", TextureNatural.GrayF32, TextureCodec.UInt32, 4),
            43 => new(dxgi, "R32_SINT", TextureNatural.GrayF32, TextureCodec.Int32, 4),
            44 => new(dxgi, "R24G8_TYPELESS", TextureNatural.GrayF32, TextureCodec.D24S8, 4),
            45 => new(dxgi, "D24_UNORM_S8_UINT", TextureNatural.GrayF32, TextureCodec.D24S8, 4),
            46 => new(dxgi, "R24_UNORM_X8_TYPELESS", TextureNatural.GrayF32, TextureCodec.D24S8, 4),
            47 => new(dxgi, "X24_TYPELESS_G8_UINT", TextureNatural.Gray8, TextureCodec.Stencil24, 4),
            48 => new(dxgi, "R8G8_TYPELESS", TextureNatural.Rgb8, TextureCodec.Rg8ToRgb8, 2),
            49 => new(dxgi, "R8G8_UNORM", TextureNatural.Rgb8, TextureCodec.Rg8ToRgb8, 2),
            50 => new(dxgi, "R8G8_UINT", TextureNatural.Rgb8, TextureCodec.Rg8ToRgb8, 2),
            51 => new(dxgi, "R8G8_SNORM", TextureNatural.Rgb8, TextureCodec.Rg8Snorm, 2),
            52 => new(dxgi, "R8G8_SINT", TextureNatural.Rgb8, TextureCodec.Rg8Sint, 2),
            53 => new(dxgi, "R16_TYPELESS", TextureNatural.Gray16, TextureCodec.Copy, 2),
            54 => new(dxgi, "R16_FLOAT", TextureNatural.GrayF32, TextureCodec.R16FToGrayF32, 2),
            55 => new(dxgi, "D16_UNORM", TextureNatural.Gray16, TextureCodec.Copy, 2),
            56 => new(dxgi, "R16_UNORM", TextureNatural.Gray16, TextureCodec.Copy, 2),
            57 => new(dxgi, "R16_UINT", TextureNatural.Gray16, TextureCodec.Copy, 2),
            58 => new(dxgi, "R16_SNORM", TextureNatural.Gray16, TextureCodec.Snorm16, 2),
            59 => new(dxgi, "R16_SINT", TextureNatural.Gray16, TextureCodec.Sint16, 2),
            60 => new(dxgi, "R8_TYPELESS", TextureNatural.Gray8, TextureCodec.Copy, 1),
            61 => new(dxgi, "R8_UNORM", TextureNatural.Gray8, TextureCodec.Copy, 1),
            62 => new(dxgi, "R8_UINT", TextureNatural.Gray8, TextureCodec.Copy, 1),
            63 => new(dxgi, "R8_SNORM", TextureNatural.Gray8, TextureCodec.Snorm8, 1),
            64 => new(dxgi, "R8_SINT", TextureNatural.Gray8, TextureCodec.Sint8, 1),
            65 => new(dxgi, "A8_UNORM", TextureNatural.Alpha8, TextureCodec.Copy, 1),
            66 => new(dxgi, "R1_UNORM", TextureNatural.Gray8, TextureCodec.R1, 1),
            67 => new(dxgi, "R9G9B9E5_SHAREDEXP", TextureNatural.RgbaF16, TextureCodec.R9G9B9E5ToRgbaF16, 4),
            68 => new(dxgi, "R8G8_B8G8_UNORM", TextureNatural.Rgb8, TextureCodec.Rgbg, 4),
            69 => new(dxgi, "G8R8_G8B8_UNORM", TextureNatural.Rgb8, TextureCodec.Grgb, 4),
            70 => new(dxgi, "BC1_TYPELESS", TextureNatural.Rgba8, TextureCodec.Bc1, 8),
            71 => new(dxgi, "BC1_UNORM", TextureNatural.Rgba8, TextureCodec.Bc1, 8),
            72 => new(dxgi, "BC1_UNORM_SRGB", TextureNatural.Rgba8, TextureCodec.Bc1, 8) { Srgb = true },
            73 => new(dxgi, "BC2_TYPELESS", TextureNatural.Rgba8, TextureCodec.Bc2, 16),
            74 => new(dxgi, "BC2_UNORM", TextureNatural.Rgba8, TextureCodec.Bc2, 16),
            75 => new(dxgi, "BC2_UNORM_SRGB", TextureNatural.Rgba8, TextureCodec.Bc2, 16) { Srgb = true },
            76 => new(dxgi, "BC3_TYPELESS", TextureNatural.Rgba8, TextureCodec.Bc3, 16),
            77 => new(dxgi, "BC3_UNORM", TextureNatural.Rgba8, TextureCodec.Bc3, 16),
            78 => new(dxgi, "BC3_UNORM_SRGB", TextureNatural.Rgba8, TextureCodec.Bc3, 16) { Srgb = true },
            79 => new(dxgi, "BC4_TYPELESS", TextureNatural.Gray8, TextureCodec.Bc4, 8),
            80 => new(dxgi, "BC4_UNORM", TextureNatural.Gray8, TextureCodec.Bc4, 8),
            81 => new(dxgi, "BC4_SNORM", TextureNatural.Gray8, TextureCodec.Bc4Snorm, 8),
            82 => new(dxgi, "BC5_TYPELESS", TextureNatural.Rgb8, TextureCodec.Bc5, 16),
            83 => new(dxgi, "BC5_UNORM", TextureNatural.Rgb8, TextureCodec.Bc5, 16),
            84 => new(dxgi, "BC5_SNORM", TextureNatural.Rgb8, TextureCodec.Bc5Snorm, 16),
            85 => new(dxgi, "B5G6R5_UNORM", TextureNatural.Bgr565, TextureCodec.Copy, 2),
            86 => new(dxgi, "B5G5R5A1_UNORM", TextureNatural.Bgra5551, TextureCodec.Copy, 2),
            87 => new(dxgi, "B8G8R8A8_UNORM", TextureNatural.Rgba8, TextureCodec.Bgra8ToRgba8, 4),
            88 => new(dxgi, "B8G8R8X8_UNORM", TextureNatural.Rgb8, TextureCodec.Bgrx8ToRgb8, 4),
            89 => new(dxgi, "R10G10B10_XR_BIAS_A2_UNORM", TextureNatural.RgbaF16, TextureCodec.XrBiasToRgbaF16, 4),
            90 => new(dxgi, "B8G8R8A8_TYPELESS", TextureNatural.Rgba8, TextureCodec.Bgra8ToRgba8, 4),
            91 => new(dxgi, "B8G8R8A8_UNORM_SRGB", TextureNatural.Rgba8, TextureCodec.Bgra8ToRgba8, 4) { Srgb = true },
            92 => new(dxgi, "B8G8R8X8_TYPELESS", TextureNatural.Rgb8, TextureCodec.Bgrx8ToRgb8, 4),
            93 => new(dxgi, "B8G8R8X8_UNORM_SRGB", TextureNatural.Rgb8, TextureCodec.Bgrx8ToRgb8, 4) { Srgb = true },
            94 => new(dxgi, "BC6H_TYPELESS", TextureNatural.RgbaF16, TextureCodec.Bc6h, 16),
            95 => new(dxgi, "BC6H_UF16", TextureNatural.RgbaF16, TextureCodec.Bc6h, 16),
            96 => new(dxgi, "BC6H_SF16", TextureNatural.RgbaF16, TextureCodec.Bc6hSigned, 16),
            97 => new(dxgi, "BC7_TYPELESS", TextureNatural.Rgba8, TextureCodec.Bc7, 16),
            98 => new(dxgi, "BC7_UNORM", TextureNatural.Rgba8, TextureCodec.Bc7, 16),
            99 => new(dxgi, "BC7_UNORM_SRGB", TextureNatural.Rgba8, TextureCodec.Bc7, 16) { Srgb = true },
            100 => new(dxgi, "AYUV", TextureNatural.Rgba8, TextureCodec.Ayuv, 4),
            101 => new(dxgi, "Y410", TextureNatural.Rgb10A2, TextureCodec.Y410, 4),
            102 => new(dxgi, "Y416", TextureNatural.Rgba16, TextureCodec.Y416, 8),
            103 => new(dxgi, "NV12", TextureNatural.Rgb8, TextureCodec.Nv12, 1),
            104 => new(dxgi, "P010", TextureNatural.Rgb10A2, TextureCodec.P016, 2),
            105 => new(dxgi, "P016", TextureNatural.Rgb16, TextureCodec.P016, 2),
            106 => new(dxgi, "420_OPAQUE", TextureNatural.Rgb8, TextureCodec.Nv12, 1),
            107 => new(dxgi, "YUY2", TextureNatural.Rgb8, TextureCodec.Yuy2, 4),
            108 => new(dxgi, "Y210", TextureNatural.Rgb10A2, TextureCodec.Y210, 8),
            109 => new(dxgi, "Y216", TextureNatural.Rgb16, TextureCodec.Y216, 8),
            110 => new(dxgi, "NV11", TextureNatural.Rgb8, TextureCodec.Nv11, 1),
            115 => new(dxgi, "B4G4R4A4_UNORM", TextureNatural.Bgra4444, TextureCodec.Copy, 2),
            130 => new(dxgi, "P208", TextureNatural.Rgb8, TextureCodec.P208, 1),
            131 => new(dxgi, "V208", TextureNatural.Rgb8, TextureCodec.V208, 1),
            132 => new(dxgi, "V408", TextureNatural.Rgb8, TextureCodec.V408, 1),
            191 => new(dxgi, "A4B4G4R4_UNORM", TextureNatural.Bgra4444, TextureCodec.A4B4G4R4, 2),
            _ => null,
        };
    }

    /// <summary>
    /// Returns the format a Vulkan <c>VkFormat</c> number names, as KTX 2 stores it, or <see langword="null"/> if Egami cannot
    /// read it. Each takes its Vulkan name without the <c>VK_FORMAT_</c> prefix.
    /// </summary>
    public static TextureFormat? FromVk(int vk)
    {
        TextureFormat? format = vk switch
        {
            2 => Dxgi(191, "R4G4B4A4_UNORM_PACK16"),
            3 => Packed("B4G4R4A4_UNORM_PACK16", TextureNatural.Rgba8, 2, 0xF0, 0xF00, 0xF000, 0xF),
            4 => Dxgi(85, "R5G6B5_UNORM_PACK16"),
            5 => Packed("B5G6R5_UNORM_PACK16", TextureNatural.Rgb8, 2, 0x1F, 0x7E0, 0xF800, 0),
            6 => Packed("R5G5B5A1_UNORM_PACK16", TextureNatural.Rgba8, 2, 0xF800, 0x7C0, 0x3E, 0x1),
            7 => Packed("B5G5R5A1_UNORM_PACK16", TextureNatural.Rgba8, 2, 0x3E, 0x7C0, 0xF800, 0x1),
            8 => Dxgi(86, "A1R5G5B5_UNORM_PACK16"),
            9 => Dxgi(61, "R8_UNORM"),
            10 => Dxgi(63, "R8_SNORM"),
            13 => Dxgi(62, "R8_UINT"),
            14 => Dxgi(64, "R8_SINT"),
            15 => Dxgi(61, "R8_SRGB") with { Srgb = true },
            16 => Dxgi(49, "R8G8_UNORM"),
            17 => Dxgi(51, "R8G8_SNORM"),
            20 => Dxgi(50, "R8G8_UINT"),
            21 => Dxgi(52, "R8G8_SINT"),
            22 => Dxgi(49, "R8G8_SRGB") with { Srgb = true },
            23 => new(0, "R8G8B8_UNORM", TextureNatural.Rgb8, TextureCodec.Copy, 3),
            24 => new(0, "R8G8B8_SNORM", TextureNatural.Rgb8, TextureCodec.Snorm8, 3),
            27 => new(0, "R8G8B8_UINT", TextureNatural.Rgb8, TextureCodec.Copy, 3),
            28 => new(0, "R8G8B8_SINT", TextureNatural.Rgb8, TextureCodec.Sint8, 3),
            29 => new(0, "R8G8B8_SRGB", TextureNatural.Rgb8, TextureCodec.Copy, 3) { Srgb = true },
            30 => new(0, "B8G8R8_UNORM", TextureNatural.Rgb8, TextureCodec.Bgr8ToRgb8, 3),
            34 => new(0, "B8G8R8_UINT", TextureNatural.Rgb8, TextureCodec.Bgr8ToRgb8, 3),
            36 => new(0, "B8G8R8_SRGB", TextureNatural.Rgb8, TextureCodec.Bgr8ToRgb8, 3) { Srgb = true },
            37 => Dxgi(28, "R8G8B8A8_UNORM"),
            38 => Dxgi(31, "R8G8B8A8_SNORM"),
            41 => Dxgi(30, "R8G8B8A8_UINT"),
            42 => Dxgi(32, "R8G8B8A8_SINT"),
            43 => Dxgi(29, "R8G8B8A8_SRGB"),
            44 => Dxgi(87, "B8G8R8A8_UNORM"),
            48 => Dxgi(87, "B8G8R8A8_UINT"),
            50 => Dxgi(91, "B8G8R8A8_SRGB"),
            51 => Dxgi(28, "A8B8G8R8_UNORM_PACK32"),
            52 => Dxgi(31, "A8B8G8R8_SNORM_PACK32"),
            55 => Dxgi(30, "A8B8G8R8_UINT_PACK32"),
            56 => Dxgi(32, "A8B8G8R8_SINT_PACK32"),
            57 => Dxgi(29, "A8B8G8R8_SRGB_PACK32"),
            58 => Packed("A2R10G10B10_UNORM_PACK32", TextureNatural.Rgba16, 4, 0x3FF00000, 0xFFC00, 0x3FF, 0xC0000000),
            64 => Dxgi(24, "A2B10G10R10_UNORM_PACK32"),
            68 => Dxgi(25, "A2B10G10R10_UINT_PACK32"),
            70 => Dxgi(56, "R16_UNORM"),
            71 => Dxgi(58, "R16_SNORM"),
            74 => Dxgi(57, "R16_UINT"),
            75 => Dxgi(59, "R16_SINT"),
            76 => Dxgi(54, "R16_SFLOAT"),
            77 => Dxgi(35, "R16G16_UNORM"),
            78 => Dxgi(37, "R16G16_SNORM"),
            81 => Dxgi(36, "R16G16_UINT"),
            82 => Dxgi(38, "R16G16_SINT"),
            83 => Dxgi(34, "R16G16_SFLOAT"),
            84 => new(0, "R16G16B16_UNORM", TextureNatural.Rgb16, TextureCodec.Copy, 6),
            85 => new(0, "R16G16B16_SNORM", TextureNatural.Rgb16, TextureCodec.Snorm16, 6),
            88 => new(0, "R16G16B16_UINT", TextureNatural.Rgb16, TextureCodec.Copy, 6),
            89 => new(0, "R16G16B16_SINT", TextureNatural.Rgb16, TextureCodec.Sint16, 6),
            90 => new(0, "R16G16B16_SFLOAT", TextureNatural.RgbaF16, TextureCodec.Rgb16FToRgbaF16, 6),
            91 => Dxgi(11, "R16G16B16A16_UNORM"),
            92 => Dxgi(13, "R16G16B16A16_SNORM"),
            95 => Dxgi(12, "R16G16B16A16_UINT"),
            96 => Dxgi(14, "R16G16B16A16_SINT"),
            97 => Dxgi(10, "R16G16B16A16_SFLOAT"),
            98 => Dxgi(42, "R32_UINT"),
            99 => Dxgi(43, "R32_SINT"),
            100 => Dxgi(41, "R32_SFLOAT"),
            101 => Dxgi(17, "R32G32_UINT"),
            102 => Dxgi(18, "R32G32_SINT"),
            103 => Dxgi(16, "R32G32_SFLOAT"),
            104 => Dxgi(7, "R32G32B32_UINT"),
            105 => Dxgi(8, "R32G32B32_SINT"),
            106 => Dxgi(6, "R32G32B32_SFLOAT"),
            107 => Dxgi(3, "R32G32B32A32_UINT"),
            108 => Dxgi(4, "R32G32B32A32_SINT"),
            109 => Dxgi(2, "R32G32B32A32_SFLOAT"),
            122 => Dxgi(26, "B10G11R11_UFLOAT_PACK32"),
            123 => Dxgi(67, "E5B9G9R9_UFLOAT_PACK32"),
            124 => Dxgi(55, "D16_UNORM"),
            125 => Dxgi(46, "X8_D24_UNORM_PACK32"),
            126 => Dxgi(40, "D32_SFLOAT"),
            127 => new(0, "S8_UINT", TextureNatural.Gray8, TextureCodec.Copy, 1),
            131 => Dxgi(71, "BC1_RGB_UNORM_BLOCK") with { Codec = TextureCodec.Bc1Opaque },
            132 => Dxgi(72, "BC1_RGB_SRGB_BLOCK") with { Codec = TextureCodec.Bc1Opaque },
            133 => Dxgi(71, "BC1_RGBA_UNORM_BLOCK"),
            134 => Dxgi(72, "BC1_RGBA_SRGB_BLOCK"),
            135 => Dxgi(74, "BC2_UNORM_BLOCK"),
            136 => Dxgi(75, "BC2_SRGB_BLOCK"),
            137 => Dxgi(77, "BC3_UNORM_BLOCK"),
            138 => Dxgi(78, "BC3_SRGB_BLOCK"),
            139 => Dxgi(80, "BC4_UNORM_BLOCK"),
            140 => Dxgi(81, "BC4_SNORM_BLOCK"),
            141 => Dxgi(83, "BC5_UNORM_BLOCK"),
            142 => Dxgi(84, "BC5_SNORM_BLOCK"),
            143 => Dxgi(95, "BC6H_UFLOAT_BLOCK"),
            144 => Dxgi(96, "BC6H_SFLOAT_BLOCK"),
            145 => Dxgi(98, "BC7_UNORM_BLOCK"),
            146 => Dxgi(99, "BC7_SRGB_BLOCK"),
            147 => new(0, "ETC2_R8G8B8_UNORM_BLOCK", TextureNatural.Rgb8, TextureCodec.Etc2Rgb, 8),
            148 => new(0, "ETC2_R8G8B8_SRGB_BLOCK", TextureNatural.Rgb8, TextureCodec.Etc2Rgb, 8) { Srgb = true },
            149 => new(0, "ETC2_R8G8B8A1_UNORM_BLOCK", TextureNatural.Rgba8, TextureCodec.Etc2RgbA1, 8),
            150 => new(0, "ETC2_R8G8B8A1_SRGB_BLOCK", TextureNatural.Rgba8, TextureCodec.Etc2RgbA1, 8) { Srgb = true },
            151 => new(0, "ETC2_R8G8B8A8_UNORM_BLOCK", TextureNatural.Rgba8, TextureCodec.Etc2Rgba, 16),
            152 => new(0, "ETC2_R8G8B8A8_SRGB_BLOCK", TextureNatural.Rgba8, TextureCodec.Etc2Rgba, 16) { Srgb = true },
            153 => new(0, "EAC_R11_UNORM_BLOCK", TextureNatural.Gray16, TextureCodec.EacR11, 8),
            154 => new(0, "EAC_R11_SNORM_BLOCK", TextureNatural.Gray16, TextureCodec.EacR11Snorm, 8),
            155 => new(0, "EAC_R11G11_UNORM_BLOCK", TextureNatural.Rgb16, TextureCodec.EacRg11, 16),
            156 => new(0, "EAC_R11G11_SNORM_BLOCK", TextureNatural.Rgb16, TextureCodec.EacRg11Snorm, 16),
            >= 157 and <= 184 => Astc((vk - 157) / 2, (vk & 1) == 0 ? TextureCodec.AstcSrgb : TextureCodec.AstcLdr),
            >= 1000066000 and <= 1000066013 => Astc(vk - 1000066000, TextureCodec.AstcHdr),
            1000340000 => Dxgi(115, "A4R4G4B4_UNORM_PACK16"),
            1000340001 => Packed("A4B4G4R4_UNORM_PACK16", TextureNatural.Rgba8, 2, 0xF, 0xF0, 0xF00, 0xF000),
            _ => null,
        };
        return format is null ? null : format with { Vk = vk };

        static TextureFormat Dxgi(int dxgi, string name) => FromDxgi(dxgi)! with { Name = name };

        static TextureFormat Astc(int footprint, TextureCodec codec)
        {
            int w = Blocks.Astc.Footprints[footprint * 2];
            int h = Blocks.Astc.Footprints[(footprint * 2) + 1];
            string suffix = codec switch { TextureCodec.AstcSrgb => "SRGB", TextureCodec.AstcHdr => "SFLOAT", _ => "UNORM" };
            TextureNatural natural = codec == TextureCodec.AstcHdr ? TextureNatural.RgbaF16 : TextureNatural.Rgba8;
            return new(0, $"ASTC_{w}x{h}_{suffix}_BLOCK", natural, codec, 16)
            {
                Srgb = codec == TextureCodec.AstcSrgb,
                BlockWidth = w,
                BlockHeight = h,
            };
        }

        static TextureFormat Packed(string name, TextureNatural natural, int bytes, uint r, uint g, uint b, uint a)
        {
            return new TextureFormat(0, name, natural, TextureCodec.Masks, bytes) { Masks = [r, g, b, a] };
        }
    }

    /// <summary>
    /// Returns the format an OpenGL texture names, as KTX 1 stores it: its internal format, and for uncompressed data the
    /// format and type of its pixels, which decide the order of BGR and packed formats. Returns <see langword="null"/> if
    /// Egami cannot read it.
    /// </summary>
    public static TextureFormat? FromGl(int internalFormat, int format, int type)
    {
        const int Bgr = 0x80E0;
        const int Bgra = 0x80E1;
        int vk = internalFormat switch
        {
            0x83F0 => 131, // COMPRESSED_RGB_S3TC_DXT1
            0x83F1 => 133,
            0x83F2 => 135,
            0x83F3 => 137,
            0x8C4C => 132, // COMPRESSED_SRGB_S3TC_DXT1
            0x8C4D => 134,
            0x8C4E => 136,
            0x8C4F => 138,
            0x8DBB => 139, // COMPRESSED_RED_RGTC1
            0x8DBC => 140,
            0x8DBD => 141,
            0x8DBE => 142,
            0x8E8C => 145, // COMPRESSED_RGBA_BPTC_UNORM
            0x8E8D => 146,
            0x8E8E => 144, // COMPRESSED_RGB_BPTC_SIGNED_FLOAT
            0x8E8F => 143,
            0x8D64 => -2, // ETC1_RGB8_OES
            0x9274 => 147, // COMPRESSED_RGB8_ETC2
            0x9275 => 148,
            0x9276 => 149, // COMPRESSED_RGB8_PUNCHTHROUGH_ALPHA1_ETC2
            0x9277 => 150,
            0x9278 => 151, // COMPRESSED_RGBA8_ETC2_EAC
            0x9279 => 152,
            0x9270 => 153, // COMPRESSED_R11_EAC
            0x9271 => 154,
            0x9272 => 155,
            0x9273 => 156,
            >= 0x93B0 and <= 0x93BD => 157 + ((internalFormat - 0x93B0) * 2), // COMPRESSED_RGBA_ASTC_4x4_KHR and on
            >= 0x93D0 and <= 0x93DD => 158 + ((internalFormat - 0x93D0) * 2), // COMPRESSED_SRGB8_ALPHA8_ASTC_4x4_KHR and on
            0x8229 => 9, // R8
            0x8F94 => 10,
            0x8232 => 13,
            0x8231 => 14,
            0x8FBD => 15, // SR8_EXT
            0x822B => 16, // RG8
            0x8F95 => 17,
            0x8238 => 20,
            0x8237 => 21,
            0x8FBE => 22, // SRG8_EXT
            0x8051 => format == Bgr ? 30 : 23, // RGB8
            0x8F96 => 24,
            0x8D7D => format == Bgr ? 34 : 27,
            0x8D8F => 28,
            0x8C41 => format == Bgr ? 36 : 29, // SRGB8
            0x8058 => format == Bgra ? 44 : 37, // RGBA8
            0x8F97 => 38,
            0x8D7C => format == Bgra ? 48 : 41,
            0x8D8E => 42,
            0x8C43 => format == Bgra ? 50 : 43, // SRGB8_ALPHA8
            0x8059 => 64, // RGB10_A2
            0x906F => 68,
            0x822A => 70, // R16
            0x8F98 => 71,
            0x8234 => 74,
            0x8233 => 75,
            0x822D => 76,
            0x822C => 77, // RG16
            0x8F99 => 78,
            0x823A => 81,
            0x8239 => 82,
            0x822F => 83,
            0x8054 => 84, // RGB16
            0x8F9A => 85,
            0x8D77 => 88,
            0x8D89 => 89,
            0x881B => 90,
            0x805B => 91, // RGBA16
            0x8F9B => 92,
            0x8D76 => 95,
            0x8D88 => 96,
            0x881A => 97,
            0x8236 => 98, // R32UI
            0x8235 => 99,
            0x822E => 100,
            0x823C => 101,
            0x823B => 102,
            0x8230 => 103,
            0x8D71 => 104,
            0x8D83 => 105,
            0x8815 => 106,
            0x8D70 => 107,
            0x8D82 => 108,
            0x8814 => 109,
            0x8C3A => 122, // R11F_G11F_B10F
            0x8C3D => 123, // RGB9_E5
            0x8D62 => type == 0x8364 ? 5 : 4, // RGB565: UNSIGNED_SHORT_5_6_5_REV puts blue high
            0x8056 => type == 0x8365 ? (format == Bgra ? 1000340000 : 1000340001) : (format == Bgra ? 3 : 2), // RGBA4
            0x8057 => type == 0x8366 ? (format == Bgra ? 8 : -1) : (format == Bgra ? 7 : 6), // RGB5_A1
            0x81A5 => 124, // DEPTH_COMPONENT16
            0x8CAC => 126, // DEPTH_COMPONENT32F
            0x8D48 => 127, // STENCIL_INDEX8
            _ => Unsized(format, type),
        };
        if (vk > 0)
        {
            return FromVk(vk);
        }

        if (vk == -2)
        {
            return FromVk(147)! with { Name = "ETC1_RGB8_OES", Vk = 0 };
        }

        return (internalFormat, format, type) switch
        {
            (0x8040, _, _) or (0x1909, 0x1909, 0x1401) => new(0, "LUMINANCE8", TextureNatural.Gray8, TextureCodec.Copy, 1),
            (0x8042, _, _) or (0x1909, 0x1909, 0x1403) => new(0, "LUMINANCE16", TextureNatural.Gray16, TextureCodec.Copy, 2),
            (0x8045, _, _) or (0x190A, 0x190A, 0x1401) => new(0, "LUMINANCE8_ALPHA8", TextureNatural.GrayAlpha8, TextureCodec.Copy, 2),
            (0x8048, _, _) or (0x190A, 0x190A, 0x1403) => new(0, "LUMINANCE16_ALPHA16", TextureNatural.GrayAlpha16, TextureCodec.Copy, 4),
            (0x803C, _, _) or (0x1906, 0x1906, 0x1401) => FromDxgi(65)! with { Name = "ALPHA8" },
            _ => null,
        };

        // An unsized internal format (RED, RG, RGB, RGBA, BGR, BGRA) takes its size from the type.
        static int Unsized(int format, int type)
        {
            int channels = format switch
            {
                0x1903 => 1, // RED
                0x8227 => 2, // RG
                0x1907 or Bgr => 3,
                0x1908 or Bgra => 4,
                _ => 0,
            };
            return (channels, type) switch
            {
                (0, _) => -1,
                (_, 0x1401) when format == Bgr => 30,
                (_, 0x1401) when format == Bgra => 44,
                (1, 0x1401) => 9, // UNSIGNED_BYTE
                (2, 0x1401) => 16,
                (3, 0x1401) => 23,
                (4, 0x1401) => 37,
                (1, 0x1403) => 70, // UNSIGNED_SHORT
                (2, 0x1403) => 77,
                (3, 0x1403) => 84,
                (4, 0x1403) => 91,
                (1, 0x140B) => 76, // HALF_FLOAT
                (2, 0x140B) => 83,
                (3, 0x140B) => 90,
                (4, 0x140B) => 97,
                (1, 0x1406) => 100, // FLOAT
                (2, 0x1406) => 103,
                (3, 0x1406) => 106,
                (4, 0x1406) => 109,
                _ => -1,
            };
        }
    }

    /// <summary>
    /// Returns the family of a compressed Vulkan format Egami does not read (3D ASTC, PVRTC), for a clear message, or
    /// <see langword="null"/>.
    /// </summary>
    public static string? UnsupportedVkFamily(int vk) => vk switch
    {
        >= 1000288000 and <= 1000288029 => "3D ASTC",
        >= 1000054000 and <= 1000054007 => "PVRTC",
        _ => null,
    };

    /// <summary>
    /// Returns the format of a Basis Universal texture in KTX 2 (VkFormat 0): ETC1S, whose BasisLZ slices the reader decodes
    /// to ETC1 blocks first, or UASTC LDR 4 × 4. <paramref name="alpha"/> loads it as <c>Rgba8</c>, else <c>Rgb8</c>.
    /// </summary>
    public static TextureFormat Basis(bool uastc, bool alpha, bool srgb)
    {
        string name = (uastc ? "UASTC_4x4" : "ETC1S") + (alpha ? "_RGBA" : "_RGB") + (srgb ? "_SRGB" : "_UNORM");
        TextureNatural natural = alpha ? TextureNatural.Rgba8 : TextureNatural.Rgb8;
        TextureCodec codec = uastc ? TextureCodec.Uastc : alpha ? TextureCodec.Etc1sAlpha : TextureCodec.Etc1s;
        return new TextureFormat(0, name, natural, codec, uastc || alpha ? 16 : 8) { Srgb = srgb };
    }

    /// <summary>Returns the family of a compressed OpenGL internal format Egami does not read, or <see langword="null"/>.</summary>
    public static string? UnsupportedGlFamily(int internalFormat) => internalFormat switch
    {
        (>= 0x93C0 and <= 0x93C9) or (>= 0x93E0 and <= 0x93E9) => "3D ASTC",
        (>= 0x8C00 and <= 0x8C03) or (>= 0x8A54 and <= 0x8A57) or 0x9137 or 0x9138 => "PVRTC",
        _ => null,
    };

    /// <summary>Returns the sRGB counterpart of a UNORM format, for a legacy header NVTT marked sRGB.</summary>
    public TextureFormat WithSrgb()
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
