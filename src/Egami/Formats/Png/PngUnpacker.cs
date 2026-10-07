using System.Buffers.Binary;
using System.Runtime.InteropServices;

using Egami.Pixels;

namespace Egami.Formats.Png;

/// <summary>The Egami pixel type a PNG decodes to without conversion (PNG design, "Unpacking and pixel types").</summary>
internal enum PngNaturalType
{
    Gray8,
    GrayAlpha8,
    Gray16,
    Rgb8,
    Rgba8,
    Rgba16,
}

/// <summary>Turns reconstructed scanline bytes into pixels of the natural type, applying bit depth scaling, palette and tRNS.</summary>
internal sealed class PngUnpacker
{
    private readonly PngHeader _header;
    private readonly Rgba8[] _palette;
    private readonly bool _hasKey;
    private readonly ushort _keyR;
    private readonly ushort _keyG;
    private readonly ushort _keyB;

    /// <param name="header">The image header.</param>
    /// <param name="palette">The palette with tRNS alpha applied, or empty.</param>
    /// <param name="transparency">The raw tRNS data for gray or RGB images, or empty.</param>
    internal PngUnpacker(PngHeader header, Rgba8[] palette, ReadOnlySpan<byte> transparency)
    {
        _header = header;
        _palette = palette;
        if (header.ColorType == PngColorType.Gray && transparency.Length >= 2)
        {
            _hasKey = true;
            _keyR = BinaryPrimitives.ReadUInt16BigEndian(transparency);
        }
        else if (header.ColorType == PngColorType.Rgb && transparency.Length >= 6)
        {
            _hasKey = true;
            _keyR = BinaryPrimitives.ReadUInt16BigEndian(transparency);
            _keyG = BinaryPrimitives.ReadUInt16BigEndian(transparency[2..]);
            _keyB = BinaryPrimitives.ReadUInt16BigEndian(transparency[4..]);
        }

        Natural = NaturalType(header, hasTransparency: _hasKey || (palette.Length > 0 && Array.Exists(palette, p => p.A != 255)));
    }

    /// <summary>Gets the natural pixel type.</summary>
    internal PngNaturalType Natural { get; }

    /// <summary>Gets the number of palette indices seen that were outside the palette.</summary>
    internal int BadIndices { get; private set; }

    /// <summary>Reports palette indices that were outside the palette: an error in strict mode, a warning otherwise.</summary>
    internal void ThrowIfBadIndices(PngChunkReader chunks, long offset)
    {
        if (BadIndices > 0)
        {
            chunks.Recoverable(offset, $"{BadIndices} pixels use palette indices outside the {_palette.Length}-entry palette. They were made black.");
        }
    }

    /// <summary>Returns the natural pixel type for a header, given whether tRNS makes pixels transparent.</summary>
    internal static PngNaturalType NaturalType(PngHeader header, bool hasTransparency)
    {
        return header.ColorType switch
        {
            PngColorType.Gray when header.BitDepth == 16 => hasTransparency ? PngNaturalType.Rgba16 : PngNaturalType.Gray16,
            PngColorType.Gray => hasTransparency ? PngNaturalType.GrayAlpha8 : PngNaturalType.Gray8,
            PngColorType.Rgb when header.BitDepth == 16 => PngNaturalType.Rgba16,
            PngColorType.Rgb => hasTransparency ? PngNaturalType.Rgba8 : PngNaturalType.Rgb8,
            PngColorType.Palette => hasTransparency ? PngNaturalType.Rgba8 : PngNaturalType.Rgb8,
            PngColorType.GrayAlpha when header.BitDepth == 16 => PngNaturalType.Rgba16,
            PngColorType.GrayAlpha => PngNaturalType.GrayAlpha8,
            PngColorType.Rgba when header.BitDepth == 16 => PngNaturalType.Rgba16,
            _ => PngNaturalType.Rgba8,
        };
    }

    /// <summary>Returns the layout of a natural type.</summary>
    internal static PixelLayoutInfo Layout(PngNaturalType type)
    {
        return type switch
        {
            PngNaturalType.Gray8 => Gray8.Layout,
            PngNaturalType.GrayAlpha8 => GrayAlpha8.Layout,
            PngNaturalType.Gray16 => Gray16.Layout,
            PngNaturalType.Rgb8 => Rgb8.Layout,
            PngNaturalType.Rgba8 => Rgba8.Layout,
            _ => Rgba16.Layout,
        };
    }

    /// <summary>Unpacks the first <c>destination.Length</c> pixels of a reconstructed scanline.</summary>
    internal void Unpack(ReadOnlySpan<byte> scanline, Span<Gray8> destination)
    {
        int depth = _header.BitDepth;
        if (depth == 8)
        {
            scanline[..destination.Length].CopyTo(MemoryMarshal.AsBytes(destination));
            return;
        }

        int scale = 255 / ((1 << depth) - 1);
        for (int x = 0; x < destination.Length; x++)
        {
            destination[x] = new Gray8((byte)(Sample(scanline, x, depth) * scale));
        }
    }

    internal void Unpack(ReadOnlySpan<byte> scanline, Span<GrayAlpha8> destination)
    {
        int depth = _header.BitDepth;
        if (_header.ColorType == PngColorType.GrayAlpha)
        {
            scanline[..(destination.Length * 2)].CopyTo(MemoryMarshal.AsBytes(destination));
            return;
        }

        // Gray with a tRNS key: the key is compared with the raw sample before scaling.
        int scale = 255 / ((1 << depth) - 1);
        for (int x = 0; x < destination.Length; x++)
        {
            int sample = Sample(scanline, x, depth);
            destination[x] = new GrayAlpha8((byte)(sample * scale), _hasKey && sample == _keyR ? (byte)0 : (byte)255);
        }
    }

    internal static void Unpack(ReadOnlySpan<byte> scanline, Span<Gray16> destination)
    {
        for (int x = 0; x < destination.Length; x++)
        {
            destination[x] = new Gray16(BinaryPrimitives.ReadUInt16BigEndian(scanline[(x * 2)..]));
        }
    }

    internal void Unpack(ReadOnlySpan<byte> scanline, Span<Rgb8> destination)
    {
        if (_header.ColorType == PngColorType.Rgb)
        {
            scanline[..(destination.Length * 3)].CopyTo(MemoryMarshal.AsBytes(destination));
            return;
        }

        for (int x = 0; x < destination.Length; x++)
        {
            Rgba8 c = PaletteEntry(Sample(scanline, x, _header.BitDepth));
            destination[x] = new Rgb8(c.R, c.G, c.B);
        }
    }

    internal void Unpack(ReadOnlySpan<byte> scanline, Span<Rgba8> destination)
    {
        switch (_header.ColorType)
        {
            case PngColorType.Rgba:
                scanline[..(destination.Length * 4)].CopyTo(MemoryMarshal.AsBytes(destination));
                break;
            case PngColorType.Rgb:
                for (int x = 0; x < destination.Length; x++)
                {
                    byte r = scanline[x * 3];
                    byte g = scanline[(x * 3) + 1];
                    byte b = scanline[(x * 3) + 2];
                    bool key = _hasKey && r == _keyR && g == _keyG && b == _keyB;
                    destination[x] = new Rgba8(r, g, b, key ? (byte)0 : (byte)255);
                }

                break;
            default:
                for (int x = 0; x < destination.Length; x++)
                {
                    destination[x] = PaletteEntry(Sample(scanline, x, _header.BitDepth));
                }

                break;
        }
    }

    internal void Unpack(ReadOnlySpan<byte> scanline, Span<Rgba16> destination)
    {
        switch (_header.ColorType)
        {
            case PngColorType.Rgba:
                for (int x = 0; x < destination.Length; x++)
                {
                    ReadOnlySpan<byte> p = scanline[(x * 8)..];
                    destination[x] = new Rgba16(
                        BinaryPrimitives.ReadUInt16BigEndian(p),
                        BinaryPrimitives.ReadUInt16BigEndian(p[2..]),
                        BinaryPrimitives.ReadUInt16BigEndian(p[4..]),
                        BinaryPrimitives.ReadUInt16BigEndian(p[6..]));
                }

                break;
            case PngColorType.Rgb:
                for (int x = 0; x < destination.Length; x++)
                {
                    ReadOnlySpan<byte> p = scanline[(x * 6)..];
                    ushort r = BinaryPrimitives.ReadUInt16BigEndian(p);
                    ushort g = BinaryPrimitives.ReadUInt16BigEndian(p[2..]);
                    ushort b = BinaryPrimitives.ReadUInt16BigEndian(p[4..]);
                    bool key = _hasKey && r == _keyR && g == _keyG && b == _keyB;
                    destination[x] = new Rgba16(r, g, b, key ? (ushort)0 : ushort.MaxValue);
                }

                break;
            case PngColorType.GrayAlpha:
                for (int x = 0; x < destination.Length; x++)
                {
                    ReadOnlySpan<byte> p = scanline[(x * 4)..];
                    ushort l = BinaryPrimitives.ReadUInt16BigEndian(p);
                    destination[x] = new Rgba16(l, l, l, BinaryPrimitives.ReadUInt16BigEndian(p[2..]));
                }

                break;
            default:
                // 16-bit gray with a tRNS key.
                for (int x = 0; x < destination.Length; x++)
                {
                    ushort l = BinaryPrimitives.ReadUInt16BigEndian(scanline[(x * 2)..]);
                    destination[x] = new Rgba16(l, l, l, _hasKey && l == _keyR ? (ushort)0 : ushort.MaxValue);
                }

                break;
        }
    }

    /// <summary>Reads sample <paramref name="index"/> of a row packed at <paramref name="depth"/> bits, most significant bits first.</summary>
    private static int Sample(ReadOnlySpan<byte> scanline, int index, int depth)
    {
        if (depth == 8)
        {
            return scanline[index];
        }

        int bit = index * depth;
        int shift = 8 - depth - (bit & 7);
        return (scanline[bit >> 3] >> shift) & ((1 << depth) - 1);
    }

    private Rgba8 PaletteEntry(int index)
    {
        if ((uint)index < (uint)_palette.Length)
        {
            return _palette[index];
        }

        BadIndices++;
        return Rgba8.Black;
    }
}
