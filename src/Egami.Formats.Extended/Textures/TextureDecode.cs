using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

using Egami.Formats.Extended.Textures.Blocks;

namespace Egami.Formats.Extended.Textures;

/// <summary>
/// Turns a texture format's stored bytes into its natural pixel type's memory: a row of pixels, a row of 4 × 4 blocks, or a row of
/// a planar video surface. YUV converts with the BT.601 studio-range integer formulas of Microsoft's "Recommended 8-Bit YUV
/// Formats for Video Rendering" and its 10- and 16-bit counterparts, as DirectXTex converts them.
/// </summary>
internal static class TextureDecode
{
    /// <summary>Returns the bytes of one natural pixel.</summary>
    public static int NaturalBytes(TextureNatural natural) => natural switch
    {
        TextureNatural.Gray8 or TextureNatural.Alpha8 => 1,
        TextureNatural.Gray16 or TextureNatural.GrayAlpha8 or TextureNatural.Bgr565 or TextureNatural.Bgr555 or TextureNatural.Bgra5551 or TextureNatural.Bgra4444 => 2,
        TextureNatural.Rgb8 => 3,
        TextureNatural.GrayF32 or TextureNatural.GrayAlpha16 or TextureNatural.Rgba8 or TextureNatural.Rgb10A2 => 4,
        TextureNatural.Rgb16 => 6,
        TextureNatural.Rgba16 or TextureNatural.RgbaF16 => 8,
        TextureNatural.RgbF32 => 12,
        _ => 16,
    };

    /// <summary>
    /// Decodes one stored row of a pixel or pair format into <paramref name="width"/> natural pixels.
    /// <paramref name="palette"/> is the palette of a P8 or A8P8 file.
    /// </summary>
    public static void Row(TextureFormat f, ReadOnlySpan<byte> src, Span<byte> dst, int width, uint[] palette)
    {
        switch (f.Codec)
        {
            case TextureCodec.Copy:
                src[..(width * f.UnitBytes)].CopyTo(dst);
                break;
            case TextureCodec.Bgra8ToRgba8:
                for (int x = 0; x < width; x++)
                {
                    int s = x * 4;
                    dst[s] = src[s + 2];
                    dst[s + 1] = src[s + 1];
                    dst[s + 2] = src[s];
                    dst[s + 3] = src[s + 3];
                }

                break;
            case TextureCodec.Bgrx8ToRgb8:
                for (int x = 0; x < width; x++)
                {
                    dst[x * 3] = src[(x * 4) + 2];
                    dst[(x * 3) + 1] = src[(x * 4) + 1];
                    dst[(x * 3) + 2] = src[x * 4];
                }

                break;
            case TextureCodec.Bgr8ToRgb8:
                for (int x = 0; x < width; x++)
                {
                    dst[x * 3] = src[(x * 3) + 2];
                    dst[(x * 3) + 1] = src[(x * 3) + 1];
                    dst[(x * 3) + 2] = src[x * 3];
                }

                break;
            case TextureCodec.Rgbx8ToRgb8:
                for (int x = 0; x < width; x++)
                {
                    dst[x * 3] = src[x * 4];
                    dst[(x * 3) + 1] = src[(x * 4) + 1];
                    dst[(x * 3) + 2] = src[(x * 4) + 2];
                }

                break;
            case TextureCodec.Rg8ToRgb8:
                for (int x = 0; x < width; x++)
                {
                    dst[x * 3] = src[x * 2];
                    dst[(x * 3) + 1] = src[(x * 2) + 1];
                    dst[(x * 3) + 2] = 0;
                }

                break;
            case TextureCodec.Rg8Snorm:
            case TextureCodec.Rg8Sint:
                for (int x = 0; x < width; x++)
                {
                    dst[x * 3] = Signed8(src[x * 2], f.Codec == TextureCodec.Rg8Snorm);
                    dst[(x * 3) + 1] = Signed8(src[(x * 2) + 1], f.Codec == TextureCodec.Rg8Snorm);
                    dst[(x * 3) + 2] = 0;
                }

                break;
            case TextureCodec.Snorm8:
            case TextureCodec.Sint8:
                for (int i = 0; i < width * f.UnitBytes; i++)
                {
                    dst[i] = Signed8(src[i], f.Codec == TextureCodec.Snorm8);
                }

                break;
            case TextureCodec.Rg16ToRgb16:
            case TextureCodec.Rg16Snorm:
            case TextureCodec.Rg16Sint:
                {
                    Span<ushort> d = MemoryMarshal.Cast<byte, ushort>(dst);
                    for (int x = 0; x < width; x++)
                    {
                        d[x * 3] = Sample16(f.Codec, src[(x * 4)..]);
                        d[(x * 3) + 1] = Sample16(f.Codec, src[((x * 4) + 2)..]);
                        d[(x * 3) + 2] = 0;
                    }

                    break;
                }

            case TextureCodec.Snorm16:
            case TextureCodec.Sint16:
                {
                    Span<ushort> d = MemoryMarshal.Cast<byte, ushort>(dst);
                    for (int i = 0; i < width * f.UnitBytes / 2; i++)
                    {
                        d[i] = Sample16(f.Codec, src[(i * 2)..]);
                    }

                    break;
                }

            case TextureCodec.Rg16FToRgbaF16:
                {
                    Span<ushort> d = MemoryMarshal.Cast<byte, ushort>(dst);
                    for (int x = 0; x < width; x++)
                    {
                        d[x * 4] = BinaryPrimitives.ReadUInt16LittleEndian(src[(x * 4)..]);
                        d[(x * 4) + 1] = BinaryPrimitives.ReadUInt16LittleEndian(src[((x * 4) + 2)..]);
                        d[(x * 4) + 2] = 0;
                        d[(x * 4) + 3] = HalfOne;
                    }

                    break;
                }

            case TextureCodec.Rgb16FToRgbaF16:
                {
                    Span<ushort> d = MemoryMarshal.Cast<byte, ushort>(dst);
                    for (int x = 0; x < width; x++)
                    {
                        d[x * 4] = BinaryPrimitives.ReadUInt16LittleEndian(src[(x * 6)..]);
                        d[(x * 4) + 1] = BinaryPrimitives.ReadUInt16LittleEndian(src[((x * 6) + 2)..]);
                        d[(x * 4) + 2] = BinaryPrimitives.ReadUInt16LittleEndian(src[((x * 6) + 4)..]);
                        d[(x * 4) + 3] = HalfOne;
                    }

                    break;
                }

            case TextureCodec.R16FToGrayF32:
                {
                    Span<float> d = MemoryMarshal.Cast<byte, float>(dst);
                    for (int x = 0; x < width; x++)
                    {
                        d[x] = (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(src[(x * 2)..]));
                    }

                    break;
                }

            case TextureCodec.Rg32FToRgbF32:
                {
                    Span<float> d = MemoryMarshal.Cast<byte, float>(dst);
                    for (int x = 0; x < width; x++)
                    {
                        d[x * 3] = BinaryPrimitives.ReadSingleLittleEndian(src[(x * 8)..]);
                        d[(x * 3) + 1] = BinaryPrimitives.ReadSingleLittleEndian(src[((x * 8) + 4)..]);
                        d[(x * 3) + 2] = 0;
                    }

                    break;
                }

            case TextureCodec.Int32:
            case TextureCodec.UInt32:
            case TextureCodec.Rgb32Int:
            case TextureCodec.Rgb32UInt:
                {
                    bool signed = f.Codec is TextureCodec.Int32 or TextureCodec.Rgb32Int;
                    Span<float> d = MemoryMarshal.Cast<byte, float>(dst);
                    for (int i = 0; i < width * f.UnitBytes / 4; i++)
                    {
                        d[i] = signed ? BinaryPrimitives.ReadInt32LittleEndian(src[(i * 4)..]) : BinaryPrimitives.ReadUInt32LittleEndian(src[(i * 4)..]);
                    }

                    break;
                }

            case TextureCodec.Rg32Int:
            case TextureCodec.Rg32UInt:
                {
                    Span<float> d = MemoryMarshal.Cast<byte, float>(dst);
                    for (int x = 0; x < width; x++)
                    {
                        for (int c = 0; c < 2; c++)
                        {
                            ReadOnlySpan<byte> s = src[((x * 8) + (c * 4))..];
                            d[(x * 3) + c] = f.Codec == TextureCodec.Rg32Int ? BinaryPrimitives.ReadInt32LittleEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s);
                        }

                        d[(x * 3) + 2] = 0;
                    }

                    break;
                }

            case TextureCodec.R11G11B10FToRgbaF16:
                {
                    Span<ushort> d = MemoryMarshal.Cast<byte, ushort>(dst);
                    for (int x = 0; x < width; x++)
                    {
                        uint v = BinaryPrimitives.ReadUInt32LittleEndian(src[(x * 4)..]);

                        // 11- and 10-bit floats have half's exponent; only the mantissa is shorter.
                        d[x * 4] = (ushort)((v & 0x7FF) << 4);
                        d[(x * 4) + 1] = (ushort)(((v >> 11) & 0x7FF) << 4);
                        d[(x * 4) + 2] = (ushort)(((v >> 22) & 0x3FF) << 5);
                        d[(x * 4) + 3] = HalfOne;
                    }

                    break;
                }

            case TextureCodec.R9G9B9E5ToRgbaF16:
                {
                    Span<Half> d = MemoryMarshal.Cast<byte, Half>(dst);
                    for (int x = 0; x < width; x++)
                    {
                        uint v = BinaryPrimitives.ReadUInt32LittleEndian(src[(x * 4)..]);
                        int exponent = (int)(v >> 27) - 24;
                        d[x * 4] = (Half)MathF.ScaleB(v & 0x1FF, exponent);
                        d[(x * 4) + 1] = (Half)MathF.ScaleB((v >> 9) & 0x1FF, exponent);
                        d[(x * 4) + 2] = (Half)MathF.ScaleB((v >> 18) & 0x1FF, exponent);
                        d[(x * 4) + 3] = Half.One;
                    }

                    break;
                }

            case TextureCodec.XrBiasToRgbaF16:
                {
                    Span<Half> d = MemoryMarshal.Cast<byte, Half>(dst);
                    for (int x = 0; x < width; x++)
                    {
                        uint v = BinaryPrimitives.ReadUInt32LittleEndian(src[(x * 4)..]);
                        d[x * 4] = (Half)(((int)(v & 0x3FF) - 384) / 510f);
                        d[(x * 4) + 1] = (Half)(((int)((v >> 10) & 0x3FF) - 384) / 510f);
                        d[(x * 4) + 2] = (Half)(((int)((v >> 20) & 0x3FF) - 384) / 510f);
                        d[(x * 4) + 3] = (Half)((v >> 30) / 3f);
                    }

                    break;
                }

            case TextureCodec.D32S8:
                for (int x = 0; x < width; x++)
                {
                    src.Slice(x * 8, 4).CopyTo(dst[(x * 4)..]);
                }

                break;
            case TextureCodec.Stencil32:
                for (int x = 0; x < width; x++)
                {
                    dst[x] = src[(x * 8) + 4];
                }

                break;
            case TextureCodec.D24S8:
                {
                    Span<float> d = MemoryMarshal.Cast<byte, float>(dst);
                    for (int x = 0; x < width; x++)
                    {
                        d[x] = (BinaryPrimitives.ReadUInt32LittleEndian(src[(x * 4)..]) & 0xFFFFFF) / 16777215f;
                    }

                    break;
                }

            case TextureCodec.Stencil24:
                for (int x = 0; x < width; x++)
                {
                    dst[x] = src[(x * 4) + 3];
                }

                break;
            case TextureCodec.R1:
                for (int x = 0; x < width; x++)
                {
                    dst[x] = (byte)((src[x >> 3] >> (7 - (x & 7))) & 1) == 0 ? (byte)0 : (byte)255;
                }

                break;
            case TextureCodec.Rgbg:
            case TextureCodec.Grgb:
                for (int x = 0; x < width; x++)
                {
                    ReadOnlySpan<byte> s = src[((x / 2) * 4)..];
                    bool rgbg = f.Codec == TextureCodec.Rgbg;
                    dst[x * 3] = rgbg ? s[0] : s[1];
                    dst[(x * 3) + 1] = rgbg ? s[(x & 1) == 0 ? 1 : 3] : s[(x & 1) == 0 ? 0 : 2];
                    dst[(x * 3) + 2] = rgbg ? s[2] : s[3];
                }

                break;
            case TextureCodec.Yuy2:
            case TextureCodec.Uyvy:
                for (int x = 0; x < width; x++)
                {
                    ReadOnlySpan<byte> s = src[((x / 2) * 4)..];
                    bool yuy2 = f.Codec == TextureCodec.Yuy2;
                    int y = yuy2 ? s[(x & 1) * 2] : s[((x & 1) * 2) + 1];
                    int u = yuy2 ? s[1] : s[0];
                    int v = yuy2 ? s[3] : s[2];
                    Yuv8(y, u, v, dst[(x * 3)..]);
                }

                break;
            case TextureCodec.Y210:
            case TextureCodec.Y216:
                for (int x = 0; x < width; x++)
                {
                    ReadOnlySpan<byte> s = src[((x / 2) * 8)..];
                    int y = BinaryPrimitives.ReadUInt16LittleEndian(s[((x & 1) * 4)..]);
                    int u = BinaryPrimitives.ReadUInt16LittleEndian(s[2..]);
                    int v = BinaryPrimitives.ReadUInt16LittleEndian(s[6..]);
                    if (f.Codec == TextureCodec.Y210)
                    {
                        Yuv10(y >> 6, u >> 6, v >> 6, 3, dst[(x * 4)..]);
                    }
                    else
                    {
                        Yuv16(y, u, v, dst[(x * 6)..]);
                    }
                }

                break;
            case TextureCodec.Ayuv:
                for (int x = 0; x < width; x++)
                {
                    ReadOnlySpan<byte> s = src[(x * 4)..];
                    Yuv8(s[2], s[1], s[0], dst[(x * 4)..]);
                    dst[(x * 4) + 3] = s[3];
                }

                break;
            case TextureCodec.Y410:
                for (int x = 0; x < width; x++)
                {
                    uint v = BinaryPrimitives.ReadUInt32LittleEndian(src[(x * 4)..]);
                    Yuv10((int)((v >> 10) & 0x3FF), (int)(v & 0x3FF), (int)((v >> 20) & 0x3FF), (int)(v >> 30), dst[(x * 4)..]);
                }

                break;
            case TextureCodec.Y416:
                for (int x = 0; x < width; x++)
                {
                    ReadOnlySpan<byte> s = src[(x * 8)..];
                    Yuv16(BinaryPrimitives.ReadUInt16LittleEndian(s[2..]), BinaryPrimitives.ReadUInt16LittleEndian(s), BinaryPrimitives.ReadUInt16LittleEndian(s[4..]), dst[(x * 8)..]);
                    BinaryPrimitives.WriteUInt16LittleEndian(dst[((x * 8) + 6)..], BinaryPrimitives.ReadUInt16LittleEndian(s[6..]));
                }

                break;
            case TextureCodec.A4B4G4R4:
                {
                    Span<ushort> d = MemoryMarshal.Cast<byte, ushort>(dst);
                    for (int x = 0; x < width; x++)
                    {
                        int v = BinaryPrimitives.ReadUInt16LittleEndian(src[(x * 2)..]);
                        d[x] = (ushort)(((v >> 4) & 15) | (((v >> 8) & 15) << 4) | ((v >> 12) << 8) | ((v & 15) << 12));
                    }

                    break;
                }

            case TextureCodec.X4R4G4B4:
                {
                    Span<ushort> d = MemoryMarshal.Cast<byte, ushort>(dst);
                    for (int x = 0; x < width; x++)
                    {
                        d[x] = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(src[(x * 2)..]) | 0xF000);
                    }

                    break;
                }

            case TextureCodec.Masks:
                Masks(f, src, dst, width);
                break;
            case TextureCodec.Palette8:
                for (int x = 0; x < width; x++)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(dst[(x * 4)..], palette[src[x]]);
                }

                break;
            case TextureCodec.Palette8Alpha:
                for (int x = 0; x < width; x++)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(dst[(x * 4)..], (palette[src[x * 2]] & 0xFFFFFF) | ((uint)src[(x * 2) + 1] << 24));
                }

                break;
            case TextureCodec.L6V5U5:
                for (int x = 0; x < width; x++)
                {
                    int t = BinaryPrimitives.ReadUInt16LittleEndian(src[(x * 2)..]);
                    int v = ((((t >> 5) & 0x1F) ^ 16) - 16) + 16;
                    int u = (((t & 0x1F) ^ 16) - 16) + 16;
                    dst[x * 4] = (byte)(((t & 0xFC00) >> 8) | ((t & 0xC000) >> 14));
                    dst[(x * 4) + 1] = (byte)((u << 3) | (u >> 2));
                    dst[(x * 4) + 2] = (byte)((v << 3) | (v >> 2));
                    dst[(x * 4) + 3] = 255;
                }

                break;
            case TextureCodec.X8L8V8U8:
                for (int x = 0; x < width; x++)
                {
                    ReadOnlySpan<byte> s = src[(x * 4)..];
                    dst[x * 4] = s[2];
                    dst[(x * 4) + 1] = (byte)(s[0] ^ 0x80);
                    dst[(x * 4) + 2] = (byte)(s[1] ^ 0x80);
                    dst[(x * 4) + 3] = 255;
                }

                break;
            case TextureCodec.A2W10V10U10:
                for (int x = 0; x < width; x++)
                {
                    uint t = BinaryPrimitives.ReadUInt32LittleEndian(src[(x * 4)..]);
                    BinaryPrimitives.WriteUInt32LittleEndian(dst[(x * 4)..], (t & 0xC0000000) | ((t & 0x3FFFFFFF) ^ 0x20080200));
                }

                break;
            default:
                throw new InvalidOperationException($"{f.Codec} is not a row codec.");
        }
    }

    /// <summary>
    /// Decodes one row of 4 × 4 blocks into <paramref name="rows"/> rows of <paramref name="width"/> natural pixels, the
    /// rows <paramref name="stride"/> bytes apart.
    /// </summary>
    public static void Blocks(TextureFormat f, ReadOnlySpan<byte> src, Span<byte> dst, int width, int rows, int stride)
    {
        int pixel = NaturalBytes(f.Natural);
        Span<byte> block = stackalloc byte[16 * 8];
        int blocks = (width + 3) / 4;
        for (int bx = 0; bx < blocks; bx++)
        {
            ReadOnlySpan<byte> b = src.Slice(bx * f.UnitBytes, f.UnitBytes);
            switch (f.Codec)
            {
                case TextureCodec.Bc1:
                    Bc1To5.DecodeBc1(b, block, fourColors: false);
                    break;
                case TextureCodec.Bc1Opaque:
                    Bc1To5.DecodeBc1(b, block, fourColors: false);
                    for (int i = 3; i < 64; i += 4)
                    {
                        block[i] = 255;
                    }

                    break;
                case TextureCodec.Bc2:
                    Bc1To5.DecodeBc2(b, block);
                    break;
                case TextureCodec.Bc3:
                    Bc1To5.DecodeBc3(b, block);
                    break;
                case TextureCodec.Bc4:
                    Bc1To5.DecodeUnorm(b, block, 1);
                    break;
                case TextureCodec.Bc4Snorm:
                    Bc1To5.DecodeSnorm(b, block, 1);
                    break;
                case TextureCodec.Bc5:
                    Bc1To5.DecodeUnorm(b, block, 3);
                    Bc1To5.DecodeUnorm(b[8..], block[1..], 3);
                    ClearBlue(block);
                    break;
                case TextureCodec.Bc5Snorm:
                    Bc1To5.DecodeSnorm(b, block, 3);
                    Bc1To5.DecodeSnorm(b[8..], block[1..], 3);
                    ClearBlue(block);
                    break;
                case TextureCodec.Bc6h:
                case TextureCodec.Bc6hSigned:
                    Bc6h.Decode(b, MemoryMarshal.Cast<byte, ushort>(block), f.Codec == TextureCodec.Bc6hSigned);
                    break;
                default:
                    Bc7.Decode(b, block);
                    break;
            }

            int x0 = bx * 4;
            int columns = Math.Min(4, width - x0);
            for (int y = 0; y < rows; y++)
            {
                block.Slice(y * 4 * pixel, columns * pixel).CopyTo(dst.Slice((y * stride) + (x0 * pixel), columns * pixel));
            }
        }
    }

    /// <summary>
    /// Decodes row <paramref name="y"/> of a planar video surface held whole in <paramref name="surface"/> (as many bytes as
    /// were read; missing chroma reads as neutral).
    /// </summary>
    public static void Planar(TextureFormat f, ReadOnlySpan<byte> surface, int y, Span<byte> dst, int width, int height)
    {
        long pitch = f.RowBytes(width);
        long luma = pitch * height;
        for (int x = 0; x < width; x++)
        {
            switch (f.Codec)
            {
                case TextureCodec.Nv12:
                case TextureCodec.P208:
                    {
                        long chromaRow = f.Codec == TextureCodec.Nv12 ? y / 2 : y;
                        long at = luma + (chromaRow * pitch) + ((x / 2) * 2);
                        Yuv8(Byte(surface, (y * pitch) + x, 16), Byte(surface, at, 128), Byte(surface, at + 1, 128), dst[(x * 3)..]);
                        break;
                    }

                case TextureCodec.Nv11:
                    {
                        long at = luma + (y * (pitch / 2)) + ((x / 4) * 2);
                        Yuv8(Byte(surface, (y * pitch) + x, 16), Byte(surface, at, 128), Byte(surface, at + 1, 128), dst[(x * 3)..]);
                        break;
                    }

                case TextureCodec.V208:
                    {
                        long half = (height + 1) / 2;
                        long u = luma + ((y / 2) * pitch) + x;
                        long v = luma + (half * pitch) + ((y / 2) * pitch) + x;
                        Yuv8(Byte(surface, (y * pitch) + x, 16), Byte(surface, u, 128), Byte(surface, v, 128), dst[(x * 3)..]);
                        break;
                    }

                case TextureCodec.V408:
                    {
                        long u = luma + (y * pitch) + x;
                        long v = luma + (height * pitch) + (y * pitch) + x;
                        Yuv8(Byte(surface, (y * pitch) + x, 16), Byte(surface, u, 128), Byte(surface, v, 128), dst[(x * 3)..]);
                        break;
                    }

                default:
                    {
                        // P010 and P016: 16-bit samples, P010's 10 bits in the high bits.
                        long chroma = luma + ((y / 2) * pitch) + ((x / 2) * 4);
                        int yy = Word(surface, (y * pitch) + (x * 2), 4096);
                        int u = Word(surface, chroma, 32768);
                        int v = Word(surface, chroma + 2, 32768);
                        if (f.Natural == TextureNatural.Rgb10A2)
                        {
                            Yuv10(yy >> 6, u >> 6, v >> 6, 3, dst[(x * 4)..]);
                        }
                        else
                        {
                            Yuv16(yy, u, v, dst[(x * 6)..]);
                        }

                        break;
                    }
            }
        }
    }

    /// <summary>Divides the colour of premultiplied natural pixels by their alpha.</summary>
    public static void Unpremultiply(TextureNatural natural, Span<byte> pixels)
    {
        switch (natural)
        {
            case TextureNatural.Rgba8:
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    int a = pixels[i + 3];
                    for (int c = 0; c < 3; c++)
                    {
                        pixels[i + c] = a == 0 ? (byte)0 : (byte)Math.Min(255, ((pixels[i + c] * 255) + (a / 2)) / a);
                    }
                }

                break;
            case TextureNatural.Rgba16:
                {
                    Span<ushort> p = MemoryMarshal.Cast<byte, ushort>(pixels);
                    for (int i = 0; i < p.Length; i += 4)
                    {
                        int a = p[i + 3];
                        for (int c = 0; c < 3; c++)
                        {
                            p[i + c] = a == 0 ? (ushort)0 : (ushort)Math.Min(65535, ((p[i + c] * 65535L) + (a / 2)) / a);
                        }
                    }

                    break;
                }

            case TextureNatural.RgbaF16:
                {
                    Span<Half> p = MemoryMarshal.Cast<byte, Half>(pixels);
                    for (int i = 0; i < p.Length; i += 4)
                    {
                        float a = (float)p[i + 3];
                        for (int c = 0; c < 3; c++)
                        {
                            p[i + c] = a == 0 ? Half.Zero : (Half)((float)p[i + c] / a);
                        }
                    }

                    break;
                }

            case TextureNatural.RgbaF32:
                {
                    Span<float> p = MemoryMarshal.Cast<byte, float>(pixels);
                    for (int i = 0; i < p.Length; i += 4)
                    {
                        float a = p[i + 3];
                        for (int c = 0; c < 3; c++)
                        {
                            p[i + c] = a == 0 ? 0 : p[i + c] / a;
                        }
                    }

                    break;
                }
        }
    }

    /// <summary>Rebuilds the blue channel of a two-channel normal map's natural pixels as the normal's Z.</summary>
    public static void ReconstructZ(TextureNatural natural, Span<byte> pixels)
    {
        switch (natural)
        {
            case TextureNatural.Rgb8:
                for (int i = 0; i < pixels.Length; i += 3)
                {
                    pixels[i + 2] = (byte)Math.Round(Z((pixels[i] / 127.5f) - 1, (pixels[i + 1] / 127.5f) - 1) * 127.5f + 127.5f);
                }

                break;
            case TextureNatural.Rgb16:
                {
                    Span<ushort> p = MemoryMarshal.Cast<byte, ushort>(pixels);
                    for (int i = 0; i < p.Length; i += 3)
                    {
                        p[i + 2] = (ushort)Math.Round(Z((p[i] / 32767.5f) - 1, (p[i + 1] / 32767.5f) - 1) * 32767.5f + 32767.5f);
                    }

                    break;
                }

            case TextureNatural.RgbaF16:
                {
                    Span<Half> p = MemoryMarshal.Cast<byte, Half>(pixels);
                    for (int i = 0; i < p.Length; i += 4)
                    {
                        p[i + 2] = (Half)Z((float)p[i], (float)p[i + 1]);
                    }

                    break;
                }

            case TextureNatural.RgbF32:
                {
                    Span<float> p = MemoryMarshal.Cast<byte, float>(pixels);
                    for (int i = 0; i < p.Length; i += 3)
                    {
                        p[i + 2] = Z(p[i], p[i + 1]);
                    }

                    break;
                }
        }
    }

    private static float Z(float x, float y) => MathF.Sqrt(MathF.Max(0, 1 - (x * x) - (y * y)));

    private const ushort HalfOne = 0x3C00;

    private static byte Byte(ReadOnlySpan<byte> surface, long at, byte missing) => at < surface.Length ? surface[(int)at] : missing;

    private static int Word(ReadOnlySpan<byte> surface, long at, int missing)
    {
        return at + 1 < surface.Length ? BinaryPrimitives.ReadUInt16LittleEndian(surface[(int)at..]) : missing;
    }

    private static void ClearBlue(Span<byte> rgb)
    {
        for (int i = 0; i < 16; i++)
        {
            rgb[(i * 3) + 2] = 0;
        }
    }

    private static byte Signed8(byte value, bool snorm)
    {
        return snorm ? Bc1To5.SnormToByte(Math.Max((int)(sbyte)value, -127), 1) : (byte)(value ^ 0x80);
    }

    private static ushort Sample16(TextureCodec codec, ReadOnlySpan<byte> src)
    {
        ushort v = BinaryPrimitives.ReadUInt16LittleEndian(src);
        return codec switch
        {
            TextureCodec.Rg16Snorm or TextureCodec.Snorm16 => (ushort)(((((long)Math.Max((short)v, (short)-32767) + 32767) * 65535 * 2) + 65534) / 131068),
            TextureCodec.Rg16Sint or TextureCodec.Sint16 => (ushort)(v ^ 0x8000),
            _ => v,
        };
    }

    private static void Yuv8(int y, int u, int v, Span<byte> rgb)
    {
        y -= 16;
        u -= 128;
        v -= 128;
        rgb[0] = (byte)Math.Clamp(((298 * y) + (409 * v) + 128) >> 8, 0, 255);
        rgb[1] = (byte)Math.Clamp(((298 * y) - (100 * u) - (208 * v) + 128) >> 8, 0, 255);
        rgb[2] = (byte)Math.Clamp(((298 * y) + (516 * u) + 128) >> 8, 0, 255);
    }

    private static void Yuv10(int y, int u, int v, int alpha, Span<byte> rgb10a2)
    {
        long yy = y - 64;
        long uu = u - 512;
        long vv = v - 512;
        uint r = (uint)Math.Clamp(((76533 * yy) + (104905 * vv) + 32768) >> 16, 0, 1023);
        uint g = (uint)Math.Clamp(((76533 * yy) - (25747 * uu) - (53425 * vv) + 32768) >> 16, 0, 1023);
        uint b = (uint)Math.Clamp(((76533 * yy) + (132590 * uu) + 32768) >> 16, 0, 1023);
        BinaryPrimitives.WriteUInt32LittleEndian(rgb10a2, r | (g << 10) | (b << 20) | ((uint)alpha << 30));
    }

    private static void Yuv16(int y, int u, int v, Span<byte> rgb16)
    {
        long yy = y - 4096;
        long uu = u - 32768;
        long vv = v - 32768;
        Span<ushort> d = MemoryMarshal.Cast<byte, ushort>(rgb16[..6]);
        d[0] = (ushort)Math.Clamp(((76607 * yy) + (105006 * vv) + 32768) >> 16, 0, 65535);
        d[1] = (ushort)Math.Clamp(((76607 * yy) - (25772 * uu) - (53477 * vv) + 32768) >> 16, 0, 65535);
        d[2] = (ushort)Math.Clamp(((76607 * yy) + (132718 * uu) + 32768) >> 16, 0, 65535);
    }

    /// <summary>Decodes a legacy bit-mask format: each mask's field scaled to 8 or 16 bits, rounding to nearest.</summary>
    private static void Masks(TextureFormat f, ReadOnlySpan<byte> src, Span<byte> dst, int width)
    {
        uint[] masks = f.Masks;
        bool wide = f.Natural is TextureNatural.Gray16 or TextureNatural.GrayAlpha16 or TextureNatural.Rgb16 or TextureNatural.Rgba16;
        int[] channels = f.Natural switch
        {
            TextureNatural.Gray8 or TextureNatural.Gray16 => [0],
            TextureNatural.GrayAlpha8 or TextureNatural.GrayAlpha16 => [0, 3],
            TextureNatural.Alpha8 => [3],
            TextureNatural.Rgb8 or TextureNatural.Rgb16 => [0, 1, 2],
            _ => [0, 1, 2, 3],
        };
        int max = wide ? 65535 : 255;
        for (int x = 0; x < width; x++)
        {
            uint value = 0;
            for (int i = 0; i < f.UnitBytes; i++)
            {
                value |= (uint)src[(x * f.UnitBytes) + i] << (8 * i);
            }

            for (int c = 0; c < channels.Length; c++)
            {
                uint mask = masks[channels[c]];
                int level = max;
                if (mask != 0)
                {
                    int shift = BitOperations.TrailingZeroCount(mask);
                    ulong top = (mask >> shift) + 1UL - 1UL;
                    ulong field = (value & mask) >> shift;
                    level = (int)(((field * (ulong)max * 2) + top) / (2 * top));
                }

                int at = (x * channels.Length) + c;
                if (wide)
                {
                    MemoryMarshal.Cast<byte, ushort>(dst)[at] = (ushort)level;
                }
                else
                {
                    dst[at] = (byte)level;
                }
            }
        }
    }
}
