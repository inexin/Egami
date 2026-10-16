using System.Buffers.Binary;
using System.Numerics;

using Egami.Drawing;
using Egami.Pixels;

namespace Egami.Metafiles.Playback;

/// <summary>
/// Hatch and pattern brushes (design question 11): the tile is sized in reference device pixels (8 for a hatch, the bitmap's
/// size for a pattern) and drawn as a vector picture or an image paint, so it scales with the picture and a hatch stays vector
/// in PDF. GDI's own tiles are in output pixels; at 1:1 the two agree. Tiles start at the brush origin.
/// </summary>
internal sealed partial class GdiPlayer
{
    /// <summary>GDI's six hatch tiles (HS_HORIZONTAL to HS_DIAGCROSS), one byte per row, the high bit leftmost, as GDI draws them.</summary>
    private static readonly byte[][] s_hatches =
    [
        [0x00, 0x00, 0x00, 0xFF, 0x00, 0x00, 0x00, 0x00],
        [0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08],
        [0x80, 0x40, 0x20, 0x10, 0x08, 0x04, 0x02, 0x01],
        [0x01, 0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80],
        [0x08, 0x08, 0x08, 0xFF, 0x08, 0x08, 0x08, 0x08],
        [0x81, 0x42, 0x24, 0x18, 0x18, 0x24, 0x42, 0x81],
    ];

    /// <summary>Tile space (reference pixels from the brush origin) to the canvas's device space.</summary>
    private Matrix3x2 TileMatrix
    {
        get
        {
            Matrix3x2 m = Matrix3x2.CreateTranslation(_dc.BrushOrg) * _device;
            if (_aliased)
            {
                m.M31 = MathF.Round(m.M31);
                m.M32 = MathF.Round(m.M32);
            }

            return m;
        }
    }

    /// <summary>The context bitmaps inside records decode with: the playback's limits, no diagnostics of their own.</summary>
    private EgamiContext DibContext => (_options.Context ?? EgamiContext.Default) with { Diagnostics = null };

    private Paint? PatternPaint(GdiBrush brush)
    {
        switch (brush.Style)
        {
            case GdiBrush.Hatched:
                {
                    byte[] rows = s_hatches[Math.Clamp(brush.Hatch, 0, 5)];
                    PictureRecorder tile = new(new RectF(0, 0, 8, 8));
                    if (_dc.BkMode == 2)
                    {
                        tile.FillRect(new RectF(0, 0, 8, 8), new SolidPaint(_dc.BkColor) { Antialias = false });
                    }

                    SolidPaint ink = new(brush.Color) { Antialias = false };
                    for (int y = 0; y < 8; y++)
                    {
                        for (int x = 0; x < 8; x++)
                        {
                            if ((rows[y] & (0x80 >> x)) != 0)
                            {
                                tile.FillRect(new RectF(x, y, 1, 1), ink);
                            }
                        }
                    }

                    return new PicturePaint(tile.Finish(), TileMode.Repeat, TileMode.Repeat, new RectF(0, 0, 8, 8)) { LocalMatrix = TileMatrix, Antialias = !_aliased };
                }

            case GdiBrush.Pattern or GdiBrush.DibPattern or GdiBrush.DibPatternPt when brush.Image is not null:
                {
                    Raster<Rgba8> image = brush.Image;
                    if (brush.Monochrome && brush.Bits is bool[] bits)
                    {
                        // A monochrome pattern takes the colors of the moment: 0 bits the text color, 1 bits the background.
                        image = Raster.Create<Rgba8>(brush.Image.Width, brush.Image.Height);
                        for (int y = 0; y < image.Height; y++)
                        {
                            Span<Rgba8> row = image.Row(y);
                            for (int x = 0; x < row.Length; x++)
                            {
                                row[x] = bits[(y * image.Width) + x] ? _dc.BkColor : _dc.TextColor;
                            }
                        }
                    }

                    return new ImagePaint(image, TileMode.Repeat, TileMode.Repeat, new Operations.SamplingOptions { Filter = Operations.ResampleFilter.Nearest }) { LocalMatrix = TileMatrix, Antialias = !_aliased };
                }

            default:
                return RopPaint(brush.Color);
        }
    }

    /// <summary>EMR_CREATEDIBPATTERNBRUSHPT and EMR_CREATEMONOBRUSH: the DIB at the record's offsets.</summary>
    private GdiBrush DibBrush(ReadOnlySpan<byte> r, int usage, int offBmi, int cbBmi, int offBits, int cbBits)
    {
        bool mono = Reading.MetafileReader.U32(r, 0) == 93;
        if (offBmi < 0 || cbBmi <= 0 || offBits < 0 || cbBits < 0 || offBmi + cbBmi > r.Length || offBits + cbBits > r.Length)
        {
            return new GdiBrush { Style = GdiBrush.Solid, Color = new Rgba8(0, 0, 0, 255) };
        }

        return PatternBrush(r.Slice(offBmi, cbBmi), r.Slice(offBits, cbBits), usage, mono);
    }

    /// <summary>META_DIBCREATEPATTERNBRUSH: Style, ColorUsage, then a packed DIB; BS_PATTERN means a monochrome pattern.</summary>
    private GdiBrush WmfDibBrush(ReadOnlySpan<byte> p)
    {
        if (p.Length < 4 + 40)
        {
            return new GdiBrush { Style = GdiBrush.Solid, Color = new Rgba8(0, 0, 0, 255) };
        }

        int style = BinaryPrimitives.ReadUInt16LittleEndian(p), usage = BinaryPrimitives.ReadUInt16LittleEndian(p[2..]);
        ReadOnlySpan<byte> dib = p[4..];
        int infoSize = PackedInfoSize(dib, usage);
        if (infoSize <= 0 || infoSize > dib.Length)
        {
            return new GdiBrush { Style = GdiBrush.Solid, Color = new Rgba8(0, 0, 0, 255) };
        }

        return PatternBrush(dib[..infoSize], dib[infoSize..], usage, mono: style == GdiBrush.Pattern);
    }

    private GdiBrush PatternBrush(ReadOnlySpan<byte> info, ReadOnlySpan<byte> bits, int usage, bool mono)
    {
        Raster<Rgba8>? image = DecodeDib(info, bits, usage);
        if (image is null)
        {
            WarnOnce("Metafile.BadBitmap", "A pattern brush's bitmap cannot be decoded; the brush draws black.");
            return new GdiBrush { Style = GdiBrush.Solid, Color = new Rgba8(0, 0, 0, 255) };
        }

        bool[]? set = null;
        if (mono && info.Length >= 48)
        {
            // Remember which pixels are the second palette color, to paint the text and background colors at draw time.
            Rgba8 second = new(info[46], info[45], info[44], 255);
            set = new bool[image.Width * image.Height];
            for (int y = 0; y < image.Height; y++)
            {
                ReadOnlySpan<Rgba8> row = image.Row(y);
                for (int x = 0; x < row.Length; x++)
                {
                    set[(y * image.Width) + x] = row[x].Equals(second);
                }
            }
        }

        return new GdiBrush { Style = GdiBrush.DibPatternPt, Image = image, Monochrome = set is not null, Bits = set };
    }

    /// <summary>The size of a packed DIB's BITMAPINFO: the header, any bit-field masks and the color table.</summary>
    private static int PackedInfoSize(ReadOnlySpan<byte> dib, int usage)
    {
        if (dib.Length < 12)
        {
            return -1;
        }

        int headerSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(dib);
        if (headerSize == 12)
        {
            int coreBits = BinaryPrimitives.ReadUInt16LittleEndian(dib[10..]);
            return 12 + (coreBits <= 8 ? (1 << coreBits) * (usage == 1 ? 2 : 3) : 0);
        }

        if (headerSize < 40 || dib.Length < 40)
        {
            return -1;
        }

        int bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
        uint compression = BinaryPrimitives.ReadUInt32LittleEndian(dib[16..]);
        uint used = BinaryPrimitives.ReadUInt32LittleEndian(dib[32..]);
        int colors = bitCount <= 8 ? (int)(used == 0 ? 1u << bitCount : Math.Min(used, 256u)) : (int)Math.Min(used, 256u);
        int masks = headerSize == 40 && compression is 3 or 6 ? (compression == 3 ? 12 : 16) : 0;
        return headerSize + masks + (colors * (usage == 1 ? 2 : 4));
    }

    /// <summary>
    /// Decodes a DIB (its BITMAPINFO and bits apart, as metafile records carry them) through Egami's BMP reader, behind a
    /// BITMAPFILEHEADER; DIB_PAL_COLORS tables become RGB from the selected palette. Null when it cannot be read.
    /// </summary>
    private Raster<Rgba8>? DecodeDib(ReadOnlySpan<byte> info, ReadOnlySpan<byte> bits, int usage)
    {
        if (info.Length < 12)
        {
            return null;
        }

        byte[] table = info.ToArray();
        if (usage == 1 && info.Length >= 40)
        {
            // Indices into the logical palette, two bytes each, become RGBQUADs.
            int headerSize = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(info), (uint)info.Length);
            int count = (info.Length - headerSize) / 2;
            table = new byte[headerSize + (count * 4)];
            info[..headerSize].CopyTo(table);
            Rgba8[] entries = _dc.Palette?.Entries ?? [];
            for (int i = 0; i < count; i++)
            {
                int index = BinaryPrimitives.ReadUInt16LittleEndian(info[(headerSize + (i * 2))..]);
                Rgba8 c = index < entries.Length ? entries[index] : new Rgba8(0, 0, 0, 255);
                table[headerSize + (i * 4)] = c.B;
                table[headerSize + (i * 4) + 1] = c.G;
                table[headerSize + (i * 4) + 2] = c.R;
            }
        }

        if (usage == 2 || (table.Length >= 40 && table.Length == BinaryPrimitives.ReadUInt32LittleEndian(table)))
        {
            // DIB_PAL_INDICES, or a header with no color table (as GDI records MaskBlt's mask): a gray ramp, black to white.
            int headerSize = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(table), (uint)table.Length);
            int bitCount = table.Length >= 16 ? BinaryPrimitives.ReadUInt16LittleEndian(table.AsSpan(14)) : 0;
            if (headerSize >= 40 && bitCount is 1 or 4 or 8)
            {
                int n = 1 << bitCount;
                byte[] withTable = new byte[headerSize + (n * 4)];
                table.AsSpan(0, headerSize).CopyTo(withTable);
                BinaryPrimitives.WriteUInt32LittleEndian(withTable.AsSpan(32), (uint)n);
                for (int i = 0; i < n; i++)
                {
                    byte v = (byte)(i * 255 / (n - 1));
                    withTable[headerSize + (i * 4)] = v;
                    withTable[headerSize + (i * 4) + 1] = v;
                    withTable[headerSize + (i * 4) + 2] = v;
                }

                table = withTable;
            }
        }

        byte[] file = new byte[14 + table.Length + bits.Length];
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(2), (uint)file.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(10), (uint)(14 + table.Length));
        table.CopyTo(file, 14);
        bits.CopyTo(file.AsSpan(14 + table.Length));
        try
        {
            return Raster.Load<Rgba8>(file, DibContext);
        }
        catch (Exception e) when (e is EgamiException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}
