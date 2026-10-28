using System.Buffers.Binary;
using System.Runtime.InteropServices;

using Egami.SystemDrawing.Imaging;

namespace Egami.SystemDrawing.Internal;

/// <summary>
/// GDI+'s pixel formats and how it converts between them, measured bit for bit in egami-compare (sysdrawing BitmapProbes and
/// ConversionProbes): 5 and 6-bit channels narrow by truncation and widen by repeating their high bits; premultiplied 8-bit
/// color is round(c × a / 255) and is divided out through the reciprocal 0xFF0000 / a; the 48 and 64-bit formats hold linear
/// light (the sRGB curve) from 0 to 8192, with alpha scaled down and up by truncation.
/// </summary>
internal static class GdiPixels
{
    /// <summary>The 14 formats a bitmap can have.</summary>
    public static readonly PixelFormat[] All =
    [
        PixelFormat.Format1bppIndexed, PixelFormat.Format4bppIndexed, PixelFormat.Format8bppIndexed, PixelFormat.Format16bppGrayScale,
        PixelFormat.Format16bppRgb555, PixelFormat.Format16bppRgb565, PixelFormat.Format16bppArgb1555, PixelFormat.Format24bppRgb,
        PixelFormat.Format32bppRgb, PixelFormat.Format32bppArgb, PixelFormat.Format32bppPArgb, PixelFormat.Format48bppRgb,
        PixelFormat.Format64bppArgb, PixelFormat.Format64bppPArgb,
    ];

    /// <summary>sRGB 8-bit levels as GDI+'s 16-bit linear values, round(EOTF(v / 255) × 8192).</summary>
    private static readonly ushort[] ToLinearTable = BuildToLinear();

    /// <summary>16-bit linear values 0 to 8192 as sRGB 8-bit levels, round(OETF(v / 8192) × 255).</summary>
    private static readonly byte[] ToSrgbTable = BuildToSrgb();

    public static int Bits(PixelFormat format) => ((int)format >> 8) & 0xFF;

    public static bool IsValid(PixelFormat format) => Array.IndexOf(All, format) >= 0;

    public static bool IsIndexed(PixelFormat format) => ((int)format & (int)PixelFormat.Indexed) != 0;

    public static bool IsExtended(PixelFormat format) => ((int)format & (int)PixelFormat.Extended) != 0;

    public static bool HasAlpha(PixelFormat format) => ((int)format & ((int)PixelFormat.Alpha | (int)PixelFormat.PAlpha)) != 0;

    /// <summary>The bytes from one row to the next: the row's bits rounded up to a multiple of 32.</summary>
    public static int Stride(int width, PixelFormat format) => checked((int)((((long)width * Bits(format)) + 31) / 32 * 4));

    /// <summary>The bytes the pixels of a row take, without the padding.</summary>
    public static int RowBytes(int width, PixelFormat format) => checked((int)((((long)width * Bits(format)) + 7) / 8));

    /// <summary>An 8-bit sRGB level as a 16-bit linear value.</summary>
    public static ushort ToLinear(byte v) => ToLinearTable[v];

    /// <summary>A stored 16-bit linear value as an 8-bit sRGB level: negative values (read as signed) are 0, values past 8192 are 255.</summary>
    public static byte ToSrgb(short v) => v <= 0 ? (byte)0 : v >= 8192 ? (byte)255 : ToSrgbTable[v];

    /// <summary>An 8-bit alpha as 16-bit alpha, truncating.</summary>
    public static ushort Alpha16(byte a) => (ushort)(a * 8192 / 255);

    /// <summary>A stored 16-bit alpha as 8-bit alpha, truncating; negative values are 0.</summary>
    public static byte Alpha8(short a) => a <= 0 ? (byte)0 : a >= 8192 ? (byte)255 : (byte)(a * 255 / 8192);

    /// <summary>8-bit premultiplication, round(c × a / 255).</summary>
    public static byte Premultiply(byte c, byte a) => (byte)(((c * a) + 127) / 255);

    /// <summary>8-bit unpremultiplication through GDI+'s 16-bit reciprocal of alpha.</summary>
    public static byte Unpremultiply(byte c, byte a) => (byte)Math.Min(255, (c * (0xFF0000 / a)) >> 16);

    /// <summary>The palette index of pixel <paramref name="x"/> in a row of an indexed format, high bits first.</summary>
    public static int Index(PixelFormat format, ReadOnlySpan<byte> row, int x) => Bits(format) switch
    {
        1 => (row[x >> 3] >> (7 - (x & 7))) & 1,
        4 => (row[x >> 1] >> ((1 - (x & 1)) * 4)) & 0xF,
        _ => row[x],
    };

    /// <summary>Sets the palette index of pixel <paramref name="x"/> in a row of an indexed format.</summary>
    public static void SetIndex(PixelFormat format, Span<byte> row, int x, int index)
    {
        switch (Bits(format))
        {
            case 1:
                int bit = 7 - (x & 7);
                row[x >> 3] = (byte)((row[x >> 3] & ~(1 << bit)) | ((index & 1) << bit));
                break;
            case 4:
                int shift = (1 - (x & 1)) * 4;
                row[x >> 1] = (byte)((row[x >> 1] & ~(0xF << shift)) | ((index & 0xF) << shift));
                break;
            default:
                row[x] = (byte)index;
                break;
        }
    }

    /// <summary>
    /// A pixel as 32-bit straight ARGB, as GetPixel reads it. Indexed pixels look up the palette (black past its end); 16-bit
    /// gray reads its high byte.
    /// </summary>
    public static uint Read(PixelFormat format, ReadOnlySpan<byte> row, int x, ReadOnlySpan<uint> palette)
    {
        switch (format)
        {
            case PixelFormat.Format1bppIndexed or PixelFormat.Format4bppIndexed or PixelFormat.Format8bppIndexed:
                int index = Index(format, row, x);
                return index < palette.Length ? palette[index] : 0xFF000000;
            case PixelFormat.Format16bppGrayScale:
                byte gray = row[(x * 2) + 1];
                return 0xFF000000 | ((uint)gray << 16) | ((uint)gray << 8) | gray;
            case PixelFormat.Format16bppRgb555:
                {
                    int v = BinaryPrimitives.ReadUInt16LittleEndian(row[(x * 2)..]);
                    return 0xFF000000 | Wide5(v >> 10) << 16 | Wide5(v >> 5) << 8 | Wide5(v);
                }

            case PixelFormat.Format16bppRgb565:
                {
                    int v = BinaryPrimitives.ReadUInt16LittleEndian(row[(x * 2)..]);
                    return 0xFF000000 | Wide5(v >> 11) << 16 | Wide6(v >> 5) << 8 | Wide5(v);
                }

            case PixelFormat.Format16bppArgb1555:
                {
                    int v = BinaryPrimitives.ReadUInt16LittleEndian(row[(x * 2)..]);
                    return ((v & 0x8000) != 0 ? 0xFF000000 : 0) | Wide5(v >> 10) << 16 | Wide5(v >> 5) << 8 | Wide5(v);
                }

            case PixelFormat.Format24bppRgb:
                return 0xFF000000 | ((uint)row[(x * 3) + 2] << 16) | ((uint)row[(x * 3) + 1] << 8) | row[x * 3];
            case PixelFormat.Format32bppRgb:
                return 0xFF000000 | (BinaryPrimitives.ReadUInt32LittleEndian(row[(x * 4)..]) & 0xFFFFFF);
            case PixelFormat.Format32bppArgb:
                return BinaryPrimitives.ReadUInt32LittleEndian(row[(x * 4)..]);
            case PixelFormat.Format32bppPArgb:
                {
                    byte a = row[(x * 4) + 3];
                    if (a == 0)
                    {
                        return 0;
                    }

                    return ((uint)a << 24) | ((uint)Unpremultiply(row[(x * 4) + 2], a) << 16) | ((uint)Unpremultiply(row[(x * 4) + 1], a) << 8) | Unpremultiply(row[x * 4], a);
                }

            default:
                {
                    Linear p = ReadLinear(format, row, x);
                    return ((uint)Alpha8(p.A) << 24) | ((uint)ToSrgb(p.R) << 16) | ((uint)ToSrgb(p.G) << 8) | ToSrgb(p.B);
                }
        }
    }

    /// <summary>Writes a 32-bit straight ARGB color as SetPixel does. Not for indexed formats or 16-bit gray.</summary>
    public static void Write(PixelFormat format, Span<byte> row, int x, uint argb)
    {
        byte a = (byte)(argb >> 24), r = (byte)(argb >> 16), g = (byte)(argb >> 8), b = (byte)argb;
        switch (format)
        {
            case PixelFormat.Format16bppRgb555:
                BinaryPrimitives.WriteUInt16LittleEndian(row[(x * 2)..], (ushort)(((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3)));
                break;
            case PixelFormat.Format16bppRgb565:
                BinaryPrimitives.WriteUInt16LittleEndian(row[(x * 2)..], (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3)));
                break;
            case PixelFormat.Format16bppArgb1555:
                BinaryPrimitives.WriteUInt16LittleEndian(row[(x * 2)..], (ushort)((a >= 128 ? 0x8000 : 0) | ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3)));
                break;
            case PixelFormat.Format24bppRgb:
                row[x * 3] = b;
                row[(x * 3) + 1] = g;
                row[(x * 3) + 2] = r;
                break;
            case PixelFormat.Format32bppRgb or PixelFormat.Format32bppArgb:
                BinaryPrimitives.WriteUInt32LittleEndian(row[(x * 4)..], argb);
                break;
            case PixelFormat.Format32bppPArgb:
                row[x * 4] = Premultiply(b, a);
                row[(x * 4) + 1] = Premultiply(g, a);
                row[(x * 4) + 2] = Premultiply(r, a);
                row[(x * 4) + 3] = a;
                break;
            case PixelFormat.Format48bppRgb or PixelFormat.Format64bppArgb or PixelFormat.Format64bppPArgb:
                WriteLinear(format, row, x, new Linear((short)ToLinear(r), (short)ToLinear(g), (short)ToLinear(b), (short)Alpha16(a)));
                break;
            default:
                throw new InvalidOperationException($"{format} pixels are not written as colors.");
        }
    }

    /// <summary>A pixel of a 48 or 64-bit format as straight 16-bit linear values (premultiplied ones divided out, truncating).</summary>
    public static Linear ReadLinear(PixelFormat format, ReadOnlySpan<byte> row, int x)
    {
        ReadOnlySpan<short> v = MemoryMarshal.Cast<byte, short>(row);
        switch (format)
        {
            case PixelFormat.Format48bppRgb:
                return new Linear(v[(x * 3) + 2], v[(x * 3) + 1], v[x * 3], 8192);
            case PixelFormat.Format64bppArgb:
                return new Linear(v[(x * 4) + 2], v[(x * 4) + 1], v[x * 4], v[(x * 4) + 3]);
            default:
                {
                    short a = v[(x * 4) + 3];
                    if (a <= 0)
                    {
                        return default;
                    }

                    short Divide(short c) => (short)Math.Min(8192, c * 8192 / a);
                    return new Linear(Divide(v[(x * 4) + 2]), Divide(v[(x * 4) + 1]), Divide(v[x * 4]), a);
                }
        }
    }

    /// <summary>Writes straight 16-bit linear values to a 48 or 64-bit format (premultiplying for 64bppPArgb, truncating).</summary>
    public static void WriteLinear(PixelFormat format, Span<byte> row, int x, Linear p)
    {
        Span<short> v = MemoryMarshal.Cast<byte, short>(row);
        switch (format)
        {
            case PixelFormat.Format48bppRgb:
                v[x * 3] = p.B;
                v[(x * 3) + 1] = p.G;
                v[(x * 3) + 2] = p.R;
                break;
            case PixelFormat.Format64bppArgb:
                v[x * 4] = p.B;
                v[(x * 4) + 1] = p.G;
                v[(x * 4) + 2] = p.R;
                v[(x * 4) + 3] = p.A;
                break;
            default:
                short Times(short c) => (short)(c * p.A / 8192);
                v[x * 4] = Times(p.B);
                v[(x * 4) + 1] = Times(p.G);
                v[(x * 4) + 2] = Times(p.R);
                v[(x * 4) + 3] = p.A;
                break;
        }
    }

    /// <summary>
    /// Converts a row from one format to another, as LockBits and Clone do: the same format copies; between indexed formats the
    /// indices are kept; into indexed formats, colors go through the palette's <see cref="GdiPalettes.InverseTable"/>
    /// (<paramref name="toIndex"/>); between 48 and 64-bit formats the values stay linear; out of 32bppPArgb into a format
    /// without alpha, the premultiplied color is kept as it is; otherwise as GetPixel reads and SetPixel writes.
    /// </summary>
    public static void ConvertRow(PixelFormat from, ReadOnlySpan<byte> source, ReadOnlySpan<uint> fromPalette, PixelFormat to, Span<byte> target, byte[]? toIndex, int width)
    {
        if (from == to)
        {
            source[..RowBytes(width, from)].CopyTo(target);
            return;
        }

        bool extendedFrom = IsExtended(from), extendedTo = IsExtended(to);
        int mask = (1 << Bits(to)) - 1;
        for (int x = 0; x < width; x++)
        {
            if (IsIndexed(to) && IsIndexed(from))
            {
                SetIndex(to, target, x, Index(from, source, x) & mask);
            }
            else if (IsIndexed(to))
            {
                SetIndex(to, target, x, GdiPalettes.Lookup(toIndex!, Read(from, source, x, fromPalette)));
            }
            else if (extendedFrom && extendedTo)
            {
                WriteLinear(to, target, x, ReadLinear(from, source, x));
            }
            else if (from == PixelFormat.Format32bppPArgb && !HasAlpha(to))
            {
                Write(to, target, x, 0xFF000000 | (BinaryPrimitives.ReadUInt32LittleEndian(source[(x * 4)..]) & 0xFFFFFF));
            }
            else
            {
                Write(to, target, x, Read(from, source, x, fromPalette));
            }
        }
    }

    private static uint Wide5(int v) => (uint)(((v & 31) << 3) | ((v & 31) >> 2));

    private static uint Wide6(int v) => (uint)(((v & 63) << 2) | ((v & 63) >> 4));

    private static double SrgbToLinear(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    private static ushort[] BuildToLinear()
    {
        ushort[] table = new ushort[256];
        for (int v = 0; v < 256; v++)
        {
            table[v] = (ushort)(int)((SrgbToLinear(v / 255.0) * 8192) + 0.5);
        }

        return table;
    }

    private static byte[] BuildToSrgb()
    {
        byte[] table = new byte[8193];
        for (int v = 0; v <= 8192; v++)
        {
            double l = v / 8192.0;
            double c = l <= 0.0031308 ? l * 12.92 : (1.055 * Math.Pow(l, 1 / 2.4)) - 0.055;
            table[v] = (byte)Math.Min(255, (int)((c * 255) + 0.5));
        }

        return table;
    }
}

/// <summary>A pixel as straight 16-bit linear values, 0 to 8192.</summary>
internal readonly record struct Linear(short R, short G, short B, short A);
