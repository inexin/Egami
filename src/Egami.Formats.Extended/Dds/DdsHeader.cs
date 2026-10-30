using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Egami.Formats.Extended.Dds;

/// <summary>
/// The two DDS headers read into one description, written from Microsoft's DDS programming guide: the 124-byte DDS_HEADER and,
/// when its FourCC is <c>DX10</c>, the 20-byte DDS_HEADER_DXT10. A legacy header's FourCC or bit masks map to a DXGI format
/// first, as DirectXTex maps them, so the rest of the reader sees one list of formats.
/// </summary>
internal sealed class DdsHeader
{
    internal const int Length = 128;
    internal const int Dx10Length = 20;
    internal const uint Magic = 0x20534444; // "DDS "

    private const uint FlagDepth = 0x800000;
    private const uint Caps2Cubemap = 0x200;
    private const uint Caps2Volume = 0x200000;
    private const uint PfAlphaPixels = 0x1;
    private const uint PfAlpha = 0x2;
    private const uint PfFourCC = 0x4;
    private const uint PfPal8 = 0x20;
    private const uint PfRgb = 0x40;
    private const uint PfLuminance = 0x20000;
    private const uint PfBumpLuminance = 0x40000;
    private const uint PfBumpDuDv = 0x80000;
    private const uint PfSrgbNvtt = 0x40000000;

    private DdsHeader()
    {
    }

    /// <summary>Gets the width of mip 0.</summary>
    public int Width { get; private init; }

    /// <summary>Gets the height of mip 0.</summary>
    public int Height { get; private init; }

    /// <summary>Gets the depth of mip 0 of a volume texture, else 1.</summary>
    public int Depth { get; private init; }

    /// <summary>Gets the number of mip levels, at least 1.</summary>
    public int MipLevels { get; private init; }

    /// <summary>Gets the number of array elements (cubes, for a cube map).</summary>
    public int ArraySize { get; private init; }

    /// <summary>Gets the number of faces in each array element: up to 6 for a cube map, else 1.</summary>
    public int Faces { get; private init; }

    /// <summary>Gets the dimension.</summary>
    public DdsDimension Dimension { get; private init; }

    /// <summary>Gets whether the texture is a cube map.</summary>
    public bool IsCube { get; private init; }

    /// <summary>Gets the DX10 header's alpha mode, or <see cref="DdsAlphaMode.Premultiplied"/> for DXT2 and DXT4.</summary>
    public DdsAlphaMode AlphaMode { get; private init; }

    /// <summary>Gets the pixel format.</summary>
    public required DdsPixelFormat Format { get; init; }

    /// <summary>Gets whether the file has the DX10 header.</summary>
    public bool Dx10 { get; private init; }

    /// <summary>Gets the legacy header's FourCC, or <see langword="null"/>.</summary>
    public string? FourCC { get; private init; }

    /// <summary>Gets the palette of a P8 or A8P8 file: 256 RGBA entries, else empty.</summary>
    public uint[] Palette { get; private set; } = [];

    /// <summary>Gets the number of header bytes before the first surface, the magic number included.</summary>
    public int DataOffset { get; private set; }

    /// <summary>Gets what a lenient read changed to make sense of the header, for the warnings.</summary>
    public List<(string Id, string Message)> Repairs { get; } = [];

    /// <summary>Gets the number of surfaces at mip 0: array slices times faces, or depth slices of a volume.</summary>
    public int Surfaces => Dimension == DdsDimension.Texture3D ? Depth : ArraySize * Faces;

    /// <summary>Returns whether the data starts with the DDS magic number and a 124-byte header.</summary>
    public static bool Matches(ReadOnlySpan<byte> header)
    {
        return header.Length >= 8 && BinaryPrimitives.ReadUInt32LittleEndian(header) == Magic && BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) == 124;
    }

    /// <summary>Reads both headers (and a palette) from the start of the input.</summary>
    public static DdsHeader Read(EgamiInput input, bool strict)
    {
        Span<byte> h = stackalloc byte[Length];
        input.ReadExactly(h);
        if (!Matches(h))
        {
            throw input.Corrupt(0, "The DDS signature is wrong.");
        }

        uint flags = U32(h, 8);
        int height = (int)Math.Min(U32(h, 12), int.MaxValue);
        int width = (int)Math.Min(U32(h, 16), int.MaxValue);
        int depth = (int)Math.Min(U32(h, 24), int.MaxValue);
        int mips = (int)Math.Min(U32(h, 28), int.MaxValue);
        uint pfFlags = U32(h, 80);
        uint fourCC = U32(h, 84);
        uint caps2 = U32(h, 112);
        bool nvttSrgb = U32(h, 68) == 0x5454564E && (pfFlags & PfSrgbNvtt) != 0; // "NVTT"
        if (width == 0 || height == 0)
        {
            throw input.Corrupt(12, $"The texture size {width}×{height} is invalid.");
        }

        DdsHeader result;
        if ((pfFlags & PfFourCC) != 0 && fourCC == 0x30315844) // "DX10"
        {
            Span<byte> x = stackalloc byte[Dx10Length];
            input.ReadExactly(x);
            int dxgi = (int)Math.Min(U32(x, 0), int.MaxValue);
            uint dimension = U32(x, 4);
            uint misc = U32(x, 8);
            int arraySize = (int)Math.Min(Math.Max(U32(x, 12), 1u), int.MaxValue);
            uint misc2 = U32(x, 16);
            DdsPixelFormat format = DdsPixelFormat.FromDxgi(dxgi)
                ?? throw new UnsupportedFormatException($"The DDS file's DXGI format {dxgi} cannot be read.");
            bool cube = (misc & 0x4) != 0;
            DdsDimension dim = dimension switch
            {
                2 => DdsDimension.Texture1D,
                3 => DdsDimension.Texture2D,
                4 => DdsDimension.Texture3D,
                _ => strict ? throw input.Corrupt(Length + 4, $"The resource dimension {dimension} is not defined.") : DdsDimension.Texture2D,
            };
            if (dim == DdsDimension.Texture3D && arraySize > 1)
            {
                throw new UnsupportedFormatException("A DDS volume texture cannot be an array.");
            }

            DdsAlphaMode alpha = (misc2 & 7) switch
            {
                1 => DdsAlphaMode.Straight,
                2 => DdsAlphaMode.Premultiplied,
                3 => DdsAlphaMode.Opaque,
                4 => DdsAlphaMode.Custom,
                _ => DdsAlphaMode.Unknown,
            };
            result = new DdsHeader
            {
                Width = width,
                Height = dim == DdsDimension.Texture1D ? 1 : height,
                Depth = dim == DdsDimension.Texture3D ? Math.Max(depth, 1) : 1,
                MipLevels = Math.Max(mips, 1),
                ArraySize = arraySize,
                Faces = cube && dim == DdsDimension.Texture2D ? 6 : 1,
                IsCube = cube && dim == DdsDimension.Texture2D,
                Dimension = dim,
                AlphaMode = alpha,
                Format = format,
                Dx10 = true,
                DataOffset = Length + Dx10Length,
            };
        }
        else
        {
            DdsPixelFormat format = Legacy(h, pfFlags, fourCC)
                ?? throw new UnsupportedFormatException($"The DDS file's pixel format ({Describe(pfFlags, fourCC, h)}) cannot be read.");
            if (nvttSrgb)
            {
                format = format.WithSrgb();
            }

            bool volume = (flags & FlagDepth) != 0 || (caps2 & Caps2Volume) != 0;
            bool cube = !volume && (caps2 & Caps2Cubemap) != 0;
            int faces = cube ? BitOperations.PopCount(caps2 & 0xFC00) : 1;
            result = new DdsHeader
            {
                Width = width,
                Height = height,
                Depth = volume ? Math.Max(depth, 1) : 1,
                MipLevels = Math.Max(mips, 1),
                ArraySize = 1,
                Faces = Math.Max(faces, 1),
                IsCube = cube,
                Dimension = volume ? DdsDimension.Texture3D : DdsDimension.Texture2D,
                AlphaMode = format.Premultiplied ? DdsAlphaMode.Premultiplied : DdsAlphaMode.Unknown,
                Format = format,
                FourCC = (pfFlags & PfFourCC) != 0 ? FourCCText(fourCC) : null,
                DataOffset = Length,
            };
            if (cube && faces < 6)
            {
                result.Repairs.Add(("FacesMissing", $"The cube map stores {faces} of its 6 faces; only those are read."));
            }

            if (format.Codec is DdsCodec.Palette8 or DdsCodec.Palette8Alpha)
            {
                byte[] palette = new byte[1024];
                input.ReadExactly(palette);
                result.Palette = new uint[256];
                for (int i = 0; i < 256; i++)
                {
                    result.Palette[i] = BinaryPrimitives.ReadUInt32LittleEndian(palette.AsSpan(i * 4));
                }

                result.DataOffset += 1024;
            }
        }

        int maxMips = 1 + BitOperations.Log2((uint)Math.Max(Math.Max(result.Width, result.Height), result.Depth));
        if (result.MipLevels > maxMips)
        {
            if (strict)
            {
                throw input.Corrupt(32, $"The file claims {result.MipLevels} mip levels; a {result.Width}×{result.Height} texture has at most {maxMips}.");
            }

            result = result.WithMips(maxMips);
            result.Repairs.Add(("MipCountRepaired", $"The file claims more mip levels than its size allows; {maxMips} were read."));
        }

        return result;
    }

    /// <summary>Returns the width of mip <paramref name="mip"/>.</summary>
    public int MipWidth(int mip) => Math.Max(1, Width >> mip);

    /// <summary>Returns the height of mip <paramref name="mip"/>.</summary>
    public int MipHeight(int mip) => Math.Max(1, Height >> mip);

    /// <summary>Returns the depth of mip <paramref name="mip"/> of a volume texture, else 1.</summary>
    public int MipDepth(int mip) => Math.Max(1, Depth >> mip);

    /// <summary>Returns the bytes of one surface at mip <paramref name="mip"/>.</summary>
    public long MipBytes(int mip) => Format.SurfaceBytes(MipWidth(mip), MipHeight(mip));

    /// <summary>
    /// Returns where surface <paramref name="surface"/> at mip <paramref name="mip"/> starts, from the start of the file. For a
    /// volume texture, the surface is depth slice <paramref name="surface"/> of that mip.
    /// </summary>
    public long Offset(int surface, int mip)
    {
        long offset = DataOffset;
        if (Dimension == DdsDimension.Texture3D)
        {
            for (int m = 0; m < mip; m++)
            {
                offset = checked(offset + (MipDepth(m) * MipBytes(m)));
            }

            return checked(offset + (surface * MipBytes(mip)));
        }

        long element = 0;
        for (int m = 0; m < MipLevels; m++)
        {
            element = checked(element + MipBytes(m));
        }

        offset = checked(offset + (surface * element));
        for (int m = 0; m < mip; m++)
        {
            offset = checked(offset + MipBytes(m));
        }

        return offset;
    }

    /// <summary>Returns how many surfaces mip <paramref name="mip"/> has: its depth slices for a volume, else all surfaces.</summary>
    public int SurfacesAt(int mip) => Dimension == DdsDimension.Texture3D ? MipDepth(mip) : Surfaces;

    private DdsHeader WithMips(int mips)
    {
        DdsHeader copy = new()
        {
            Width = Width,
            Height = Height,
            Depth = Depth,
            MipLevels = mips,
            ArraySize = ArraySize,
            Faces = Faces,
            IsCube = IsCube,
            Dimension = Dimension,
            AlphaMode = AlphaMode,
            Format = Format,
            Dx10 = Dx10,
            FourCC = FourCC,
            Palette = Palette,
            DataOffset = DataOffset,
        };
        copy.Repairs.AddRange(Repairs);
        return copy;
    }

    private static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);

    /// <summary>Maps a legacy pixel format to the format it is, or <see langword="null"/> if it is none Egami reads.</summary>
    private static DdsPixelFormat? Legacy(ReadOnlySpan<byte> h, uint flags, uint fourCC)
    {
        if ((flags & PfFourCC) != 0)
        {
            return FromFourCC(fourCC);
        }

        int bits = (int)U32(h, 88);
        uint r = U32(h, 92);
        uint g = U32(h, 96);
        uint b = U32(h, 100);
        uint a = (flags & (PfAlphaPixels | PfAlpha)) != 0 ? U32(h, 104) : 0;
        if (bits is not (8 or 16 or 24 or 32))
        {
            return null;
        }

        int bytes = bits / 8;
        if ((flags & PfPal8) != 0)
        {
            return bits switch
            {
                8 => new DdsPixelFormat(0, "P8", DdsNatural.Rgba8, DdsCodec.Palette8, 1),
                16 => new DdsPixelFormat(0, "A8P8", DdsNatural.Rgba8, DdsCodec.Palette8Alpha, 2),
                _ => null,
            };
        }

        if ((flags & PfBumpDuDv) != 0)
        {
            return (bits, r, g, b, a) switch
            {
                (16, 0xFF, 0xFF00, 0, 0) => DdsPixelFormat.FromDxgi(51), // V8U8
                (32, 0xFF, 0xFF00, 0xFF0000, 0xFF000000) => DdsPixelFormat.FromDxgi(31), // Q8W8V8U8
                (32, 0xFFFF, 0xFFFF0000, 0, 0) => DdsPixelFormat.FromDxgi(37), // V16U16
                (32, 0x3FF00000, 0xFFC00, 0x3FF, 0xC0000000) => new DdsPixelFormat(0, "A2W10V10U10", DdsNatural.Rgb10A2, DdsCodec.A2W10V10U10, 4),
                _ => null,
            };
        }

        if ((flags & PfBumpLuminance) != 0)
        {
            return (bits, r, g, b) switch
            {
                (16, 0x1F, 0x3E0, 0xFC00) => new DdsPixelFormat(0, "L6V5U5", DdsNatural.Rgba8, DdsCodec.L6V5U5, 2),
                (32, 0xFF, 0xFF00, 0xFF0000) => new DdsPixelFormat(0, "X8L8V8U8", DdsNatural.Rgba8, DdsCodec.X8L8V8U8, 4),
                _ => null,
            };
        }

        bool luminance = (flags & PfLuminance) != 0 || ((flags & PfRgb) != 0 && r != 0 && g == 0 && b == 0);
        if (luminance)
        {
            return (bits, r, a) switch
            {
                (8, 0xFF, 0) => DdsPixelFormat.FromDxgi(61)! with { Name = "L8" },
                (16, 0xFFFF, 0) => DdsPixelFormat.FromDxgi(56)! with { Name = "L16" },
                (16, 0xFF, 0xFF00) => new DdsPixelFormat(49, "A8L8", DdsNatural.GrayAlpha8, DdsCodec.Copy, 2),
                (32, 0xFFFFFFFF, 0) when (flags & PfRgb) != 0 => DdsPixelFormat.FromDxgi(41),
                _ => Masks(bytes, a == 0 ? "L" : "AL", Wide(r, a) ? (a == 0 ? DdsNatural.Gray16 : DdsNatural.GrayAlpha16) : (a == 0 ? DdsNatural.Gray8 : DdsNatural.GrayAlpha8), r, 0, 0, a),
            };
        }

        if ((flags & PfRgb) == 0)
        {
            return (flags & PfAlpha) != 0 && a != 0 ? (bits == 8 && a == 0xFF ? DdsPixelFormat.FromDxgi(65) : Masks(bytes, "A", DdsNatural.Alpha8, 0, 0, 0, a)) : null;
        }

        if (bits == 32 && BitOperations.PopCount(r) == 10 && BitOperations.PopCount(g) == 10 && BitOperations.PopCount(b) == 10 && BitOperations.PopCount(a) == 2)
        {
            // The 10:10:10:2 masks: D3DX wrote them with red and blue swapped, so they are read swapped, as DirectXTex does.
            (r, b) = (b, r);
            if (r == 0x3FF && g == 0xFFC00 && b == 0x3FF00000 && a == 0xC0000000)
            {
                return DdsPixelFormat.FromDxgi(24);
            }
        }

        return (bits, r, g, b, a) switch
        {
            (32, 0xFF0000, 0xFF00, 0xFF, 0xFF000000) => DdsPixelFormat.FromDxgi(87),
            (32, 0xFF0000, 0xFF00, 0xFF, 0) => DdsPixelFormat.FromDxgi(88),
            (32, 0xFF, 0xFF00, 0xFF0000, 0xFF000000) => DdsPixelFormat.FromDxgi(28),
            (32, 0xFF, 0xFF00, 0xFF0000, 0) => new DdsPixelFormat(28, "X8B8G8R8", DdsNatural.Rgb8, DdsCodec.Rgbx8ToRgb8, 4),
            (32, 0xFFFF, 0xFFFF0000, 0, 0) => DdsPixelFormat.FromDxgi(35),
            (16, 0xF800, 0x7E0, 0x1F, 0) => DdsPixelFormat.FromDxgi(85),
            (16, 0x7C00, 0x3E0, 0x1F, 0x8000) => DdsPixelFormat.FromDxgi(86),
            (16, 0x7C00, 0x3E0, 0x1F, 0) => new DdsPixelFormat(86, "X1R5G5B5", DdsNatural.Bgr555, DdsCodec.Copy, 2),
            (16, 0xF00, 0xF0, 0xF, 0xF000) => DdsPixelFormat.FromDxgi(115),
            (16, 0xF00, 0xF0, 0xF, 0) => new DdsPixelFormat(115, "X4R4G4B4", DdsNatural.Bgra4444, DdsCodec.X4R4G4B4, 2),
            _ => Masks(bytes, a == 0 ? "RGB" : "RGBA", Wide(r, g, b, a) ? (a == 0 ? DdsNatural.Rgb16 : DdsNatural.Rgba16) : (a == 0 ? DdsNatural.Rgb8 : DdsNatural.Rgba8), r, g, b, a),
        };
    }

    private static DdsPixelFormat? FromFourCC(uint fourCC)
    {
        static DdsPixelFormat? Dxgi(int value) => DdsPixelFormat.FromDxgi(value);
        return FourCCText(fourCC) switch
        {
            "DXT1" => Dxgi(71),
            "DXT2" => Dxgi(74)! with { Premultiplied = true },
            "DXT3" => Dxgi(74),
            "DXT4" => Dxgi(77)! with { Premultiplied = true },
            "DXT5" or "A2D5" or "xGBR" or "RxBG" or "RBxG" or "xRBG" or "RGxB" or "xGxR" or "GXRB" or "GRXB" or "RXGB" or "BRGX" => Dxgi(77),
            "ATI1" or "BC4U" => Dxgi(80),
            "BC4S" => Dxgi(81),
            "ATI2" or "BC5U" or "A2XY" => Dxgi(83),
            "BC5S" => Dxgi(84),
            "BC6H" => Dxgi(95),
            "BC7L" or "BC7\0" => Dxgi(98),
            "RGBG" => Dxgi(68),
            "GRGB" => Dxgi(69),
            "YUY2" => Dxgi(107),
            "UYVY" => Dxgi(107)! with { Name = "UYVY", Codec = DdsCodec.Uyvy },
            _ => fourCC switch
            {
                36 => Dxgi(11),
                110 => Dxgi(13),
                111 => Dxgi(54),
                112 => Dxgi(34),
                113 => Dxgi(10),
                114 => Dxgi(41),
                115 => Dxgi(16),
                116 => Dxgi(2),
                _ => null,
            },
        };
    }

    private static DdsPixelFormat Masks(int bytes, string channels, DdsNatural natural, uint r, uint g, uint b, uint a)
    {
        return new DdsPixelFormat(0, $"{channels} masks, {bytes * 8} bits", natural, DdsCodec.Masks, bytes) { Masks = [r, g, b, a] };
    }

    private static bool Wide(params ReadOnlySpan<uint> masks)
    {
        foreach (uint mask in masks)
        {
            if (BitOperations.PopCount(mask) > 8)
            {
                return true;
            }
        }

        return false;
    }

    private static string FourCCText(uint fourCC)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, fourCC);
        foreach (byte c in bytes)
        {
            if (c is not (0 or (>= 0x20 and < 0x7F)))
            {
                return fourCC.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return Encoding.ASCII.GetString(bytes);
    }

    private static string Describe(uint flags, uint fourCC, ReadOnlySpan<byte> h)
    {
        return (flags & PfFourCC) != 0
            ? $"FourCC {FourCCText(fourCC)}"
            : $"flags 0x{flags:X}, {U32(h, 88)} bits, masks 0x{U32(h, 92):X} 0x{U32(h, 96):X} 0x{U32(h, 100):X} 0x{U32(h, 104):X}";
    }
}
