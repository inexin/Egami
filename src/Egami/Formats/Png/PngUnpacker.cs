using System.Buffers.Binary;
using System.Runtime.CompilerServices;
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

    /// <summary>Gets the palette as 256 RGB entries, out-of-range indices black, or null for non-palette images.</summary>
    private readonly Rgb8[]? _paletteRgb;

    /// <summary>The palette as 256 RGBA entries, out-of-range indices opaque black, or null for non-palette images.</summary>
    private readonly Rgba8[]? _paletteRgba;

    /// <summary>Scratch space for palette indices expanded from 1, 2 and 4-bit rows.</summary>
    private byte[] _indices = [];

    /// <summary>The palette converted to the pixel type being decoded into, built on first use.</summary>
    private Array? _typedPalette;

    /// <summary>Creates an unpacker for one image header.</summary>
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
        if (header.ColorType == PngColorType.Palette)
        {
            _paletteRgba = new Rgba8[256];
            _paletteRgb = new Rgb8[256];
            for (int i = 0; i < 256; i++)
            {
                Rgba8 c = i < palette.Length ? palette[i] : Rgba8.Black;
                _paletteRgba[i] = c;
                _paletteRgb[i] = new Rgb8(c.R, c.G, c.B);
            }
        }
    }

    /// <summary>Gets the natural pixel type.</summary>
    internal PngNaturalType Natural { get; }

    /// <summary>
    /// Gets a value indicating whether a reconstructed scanline's bytes are already the natural pixels: 8-bit gray, gray-alpha, RGB
    /// or RGBA with no transparency key. Such rows need no unpacking.
    /// </summary>
    internal bool ScanlineIsNatural => _header.BitDepth == 8 && !_hasKey
        && _header.ColorType is PngColorType.Gray or PngColorType.GrayAlpha or PngColorType.Rgb or PngColorType.Rgba;

    /// <summary>Gets the number of palette indices seen that were outside the palette.</summary>
    internal int BadIndices { get; set; }

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

        Expand(scanline, MemoryMarshal.AsBytes(destination), depth, scaled: true);
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
        BinaryPrimitives.ReverseEndianness(MemoryMarshal.Cast<byte, ushort>(scanline[..(destination.Length * 2)]), MemoryMarshal.Cast<Gray16, ushort>(destination));
    }

    /// <summary>Unpacks 16-bit RGB samples straight into <see cref="Rgb16"/> (a tRNS key needs alpha, so it is not used here).</summary>
    internal static void Unpack(ReadOnlySpan<byte> scanline, Span<Rgb16> destination)
    {
        BinaryPrimitives.ReverseEndianness(MemoryMarshal.Cast<byte, ushort>(scanline[..(destination.Length * 6)]), MemoryMarshal.Cast<Rgb16, ushort>(destination));
    }

    /// <summary>Unpacks 16-bit gray and alpha samples straight into <see cref="GrayAlpha16"/>.</summary>
    internal static void Unpack(ReadOnlySpan<byte> scanline, Span<GrayAlpha16> destination)
    {
        BinaryPrimitives.ReverseEndianness(MemoryMarshal.Cast<byte, ushort>(scanline[..(destination.Length * 4)]), MemoryMarshal.Cast<GrayAlpha16, ushort>(destination));
    }

    internal void Unpack(ReadOnlySpan<byte> scanline, Span<Rgb8> destination)
    {
        if (_header.ColorType == PngColorType.Rgb)
        {
            scanline[..(destination.Length * 3)].CopyTo(MemoryMarshal.AsBytes(destination));
            return;
        }

        ReadOnlySpan<byte> indices = Indices(scanline, destination.Length);
        Rgb8[] palette = _paletteRgb!;
        for (int x = 0; x < indices.Length; x++)
        {
            destination[x] = palette[indices[x]];
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
                ReadOnlySpan<byte> indices = Indices(scanline, destination.Length);
                Rgba8[] palette = _paletteRgba!;
                for (int x = 0; x < indices.Length; x++)
                {
                    destination[x] = palette[indices[x]];
                }

                break;
        }
    }

    internal void Unpack(ReadOnlySpan<byte> scanline, Span<Rgba16> destination)
    {
        switch (_header.ColorType)
        {
            case PngColorType.Rgba:
                BinaryPrimitives.ReverseEndianness(MemoryMarshal.Cast<byte, ushort>(scanline[..(destination.Length * 8)]), MemoryMarshal.Cast<Rgba16, ushort>(destination));
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

    /// <summary>
    /// For palette images, writes each pixel straight from a 256-entry palette already converted to <typeparamref name="TPixel"/>,
    /// so decoding to any pixel type costs one table lookup per pixel, with no intermediate row or conversion pass.
    /// </summary>
    /// <returns><see langword="false"/> if the image has no palette.</returns>
    internal bool TryUnpackPalette<TPixel>(ReadOnlySpan<byte> scanline, Span<TPixel> destination)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (_paletteRgba is null)
        {
            return false;
        }

        if (_typedPalette is not TPixel[] table)
        {
            table = new TPixel[256];
            PixelConvert.Convert<Rgba8, TPixel>(_paletteRgba, table);
            _typedPalette = table;
        }

        ReadOnlySpan<byte> indices = Indices(scanline, destination.Length);
        ref TPixel entries = ref MemoryMarshal.GetArrayDataReference(table);
        for (int x = 0; x < indices.Length; x++)
        {
            destination[x] = Unsafe.Add(ref entries, indices[x]);
        }

        return true;
    }

    /// <summary>Returns the palette indices of a row, expanded to one byte each, and counts any outside the palette.</summary>
    private ReadOnlySpan<byte> Indices(ReadOnlySpan<byte> scanline, int width)
    {
        ReadOnlySpan<byte> indices;
        if (_header.BitDepth == 8)
        {
            indices = scanline[..width];
        }
        else
        {
            if (_indices.Length < width)
            {
                _indices = new byte[width];
            }

            Expand(scanline, _indices.AsSpan(0, width), _header.BitDepth, scaled: false);
            indices = _indices.AsSpan(0, width);
        }

        // Indices outside the palette decode as black and are reported.
        if (_palette.Length < 1 << _header.BitDepth && indices.IndexOfAnyExceptInRange((byte)0, (byte)(_palette.Length - 1)) >= 0)
        {
            BadIndices += indices.Length - CountInRange(indices, _palette.Length);
        }

        return indices;
    }

    private static int CountInRange(ReadOnlySpan<byte> indices, int length)
    {
        int count = 0;
        foreach (byte index in indices)
        {
            if (index < length)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Expands samples packed at 1, 2 or 4 bits into one byte each, a whole source byte at a time through a lookup table.
    /// With <paramref name="scaled"/>, values are scaled to 0..255 (× 255, 85 or 17); otherwise they are raw indices.
    /// </summary>
    private static void Expand(ReadOnlySpan<byte> scanline, Span<byte> destination, int depth, bool scaled)
    {
        int perByte = 8 / depth;
        ulong[] table = ExpandTables.Get(depth, scaled);
        int full = destination.Length / perByte;
        ref byte d = ref MemoryMarshal.GetReference(destination);
        for (int i = 0; i < full; i++)
        {
            ulong expanded = table[scanline[i]];
            switch (perByte)
            {
                case 8:
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, i * 8), expanded);
                    break;
                case 4:
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, i * 4), (uint)expanded);
                    break;
                default:
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, i * 2), (ushort)expanded);
                    break;
            }
        }

        int scale = scaled ? 255 / ((1 << depth) - 1) : 1;
        for (int x = full * perByte; x < destination.Length; x++)
        {
            destination[x] = (byte)(Sample(scanline, x, depth) * scale);
        }
    }

    /// <summary>For each depth, 256 entries giving the samples of one packed byte as consecutive bytes, first sample lowest.</summary>
    private static class ExpandTables
    {
        private static readonly ulong[][] Raw = [Build(1, false), Build(2, false), Build(4, false)];
        private static readonly ulong[][] Scaled = [Build(1, true), Build(2, true), Build(4, true)];

        internal static ulong[] Get(int depth, bool scaled)
        {
            int index = depth == 1 ? 0 : depth == 2 ? 1 : 2;
            return scaled ? Scaled[index] : Raw[index];
        }

        private static ulong[] Build(int depth, bool scaled)
        {
            int perByte = 8 / depth;
            int max = (1 << depth) - 1;
            ulong[] table = new ulong[256];
            for (int b = 0; b < 256; b++)
            {
                ulong value = 0;
                for (int k = 0; k < perByte; k++)
                {
                    int sample = (b >> (8 - depth - (k * depth))) & max;
                    value |= (ulong)(scaled ? sample * (255 / max) : sample) << (8 * k);
                }

                table[b] = value;
            }

            return table;
        }
    }
}
