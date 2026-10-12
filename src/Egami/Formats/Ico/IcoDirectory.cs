using System.Buffers.Binary;

using Egami.Metadata;

namespace Egami.Formats.Ico;

/// <summary>
/// An icon or cursor file's directory, written from Microsoft's documentation of the ICONDIR and ICONDIRENTRY structures: a 6-byte
/// header (reserved 0, type 1 for icons or 2 for cursors, the entry count), then 16 bytes an entry. Each entry's own header, a PNG
/// IHDR or a BITMAPINFOHEADER, gives its real size and depth; the directory's values are hints.
/// </summary>
internal sealed class IcoDirectory
{
    internal const int IconType = 1;
    internal const int CursorType = 2;
    internal const int HeaderLength = 6;
    internal const int EntryLength = 16;
    internal const int SignatureLength = HeaderLength + EntryLength;

    private static ReadOnlySpan<byte> PngSignature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    private IcoDirectory(int type, int count, IReadOnlyList<Entry> entries, WarningList warnings)
    {
        Type = type;
        Count = count;
        Entries = entries;
        Warnings = warnings;
    }

    /// <summary>Gets 1 for an icon, 2 for a cursor.</summary>
    public int Type { get; }

    /// <summary>Gets the number of entries the directory lists.</summary>
    public int Count { get; }

    /// <summary>Gets the entries that could be read, in directory order.</summary>
    public IReadOnlyList<Entry> Entries { get; }

    /// <summary>Gets what was wrong with the directory, for the loaded raster's warnings.</summary>
    public WarningList Warnings { get; }

    /// <summary>
    /// Returns whether the data starts with an icon or cursor directory of <paramref name="type"/>: reserved 0, the type, at least
    /// one entry, and a first entry whose data lies after the directory and whose planes and bit count are plausible for an icon.
    /// </summary>
    internal static bool Matches(ReadOnlySpan<byte> header, int type)
    {
        if (header.Length < SignatureLength || BinaryPrimitives.ReadUInt16LittleEndian(header) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(header[2..]) != type)
        {
            return false;
        }

        int count = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
        ReadOnlySpan<byte> entry = header.Slice(HeaderLength, EntryLength);
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
        if (count == 0 || size < 12 || offset < HeaderLength + (EntryLength * (uint)count))
        {
            return false;
        }

        if (type == CursorType)
        {
            return true;
        }

        int planes = BinaryPrimitives.ReadUInt16LittleEndian(entry[4..]);
        int bits = BinaryPrimitives.ReadUInt16LittleEndian(entry[6..]);
        return planes <= 1 && bits is 0 or 1 or 2 or 4 or 8 or 16 or 24 or 32;
    }

    /// <summary>
    /// Parses the directory and every entry's header from the whole file. In strict mode any bad entry throws; in lenient mode bad
    /// entries are left out with a warning, and only a file with none left throws.
    /// </summary>
    internal static IcoDirectory Parse(EgamiInput input, ReadOnlySpan<byte> data, int expectedType, bool strict)
    {
        if (data.Length < HeaderLength || BinaryPrimitives.ReadUInt16LittleEndian(data) != 0)
        {
            throw input.Corrupt(0, "The icon directory header is missing or its reserved field is not 0.");
        }

        int type = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
        if (type != expectedType)
        {
            throw input.Corrupt(2, $"The directory says type {type}; {(expectedType == IconType ? "an icon is 1" : "a cursor is 2")}.");
        }

        int count = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        if (count == 0)
        {
            throw input.Corrupt(4, "The directory has no entries.");
        }

        input.CheckFrameCount(count);
        long directoryEnd = HeaderLength + ((long)EntryLength * count);
        if (directoryEnd > data.Length)
        {
            throw new CorruptDataException(input.FormatName, data.Length, $"The file ends inside the directory of {count} entries.") { IsTruncation = true };
        }

        WarningList warnings = new();
        List<Entry> entries = new(count);
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> raw = data.Slice(HeaderLength + (EntryLength * i), EntryLength);
            int at = HeaderLength + (EntryLength * i);
            string? problem = TryEntry(raw, data, i, type, directoryEnd, out Entry? entry);
            if (problem is null)
            {
                foreach (Entry other in entries)
                {
                    if (entry!.Offset < other.Offset + other.Size && other.Offset < entry.Offset + entry.Size)
                    {
                        problem = $"Entry {i}'s data overlaps entry {other.Index}'s.";
                        break;
                    }
                }
            }

            if (problem is not null)
            {
                if (strict)
                {
                    throw input.Corrupt(at, problem);
                }

                warnings.Add("Ico.EntrySkipped", problem + " The entry was skipped.", at);
                continue;
            }

            if (entry!.Hint is string hint)
            {
                warnings.Add("Ico.DirectoryMismatch", hint, at);
            }

            entries.Add(entry);
        }

        if (entries.Count == 0)
        {
            throw input.Corrupt(HeaderLength, "No entry of the icon could be read.");
        }

        return new IcoDirectory(type, count, entries, warnings);
    }

    /// <summary>Reads one directory entry and its data's header; returns what is wrong with it, or <see langword="null"/>.</summary>
    private static string? TryEntry(ReadOnlySpan<byte> raw, ReadOnlySpan<byte> data, int index, int type, long directoryEnd, out Entry? entry)
    {
        entry = null;
        int directoryWidth = raw[0] == 0 ? 256 : raw[0];
        int directoryHeight = raw[1] == 0 ? 256 : raw[1];
        int field1 = BinaryPrimitives.ReadUInt16LittleEndian(raw[4..]);
        int field2 = BinaryPrimitives.ReadUInt16LittleEndian(raw[6..]);
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(raw[8..]);
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(raw[12..]);
        if (offset < directoryEnd)
        {
            return $"Entry {index}'s data offset {offset} points into the directory.";
        }

        if (offset >= data.Length || size > data.Length - offset)
        {
            return $"Entry {index}'s {size} bytes at offset {offset} go past the end of the {data.Length}-byte file.";
        }

        if (size < 12)
        {
            return $"Entry {index} has only {size} bytes.";
        }

        ReadOnlySpan<byte> bytes = data.Slice((int)offset, (int)size);
        int width, height, bits;
        IcoEntryEncoding encoding;
        DibLayout? dib = null;
        if (bytes.StartsWith(PngSignature))
        {
            if (bytes.Length < 8 + 8 + 13 || !bytes.Slice(12, 4).SequenceEqual("IHDR"u8))
            {
                return $"Entry {index} starts as a PNG but has no IHDR chunk.";
            }

            uint w = BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]);
            uint h = BinaryPrimitives.ReadUInt32BigEndian(bytes[20..]);
            int depth = bytes[24];
            int channels = bytes[25] switch { 0 or 3 => 1, 4 => 2, 2 => 3, 6 => 4, _ => 0 };
            if (w == 0 || h == 0 || w > int.MaxValue || h > int.MaxValue || channels == 0)
            {
                return $"Entry {index}'s PNG header is invalid.";
            }

            (width, height, bits, encoding) = ((int)w, (int)h, depth * channels, IcoEntryEncoding.Png);
        }
        else
        {
            string? problem = DibLayout.TryRead(bytes, index, out DibLayout? layout);
            if (problem is not null)
            {
                return problem;
            }

            dib = layout!;
            (width, height, bits, encoding) = (dib.Width, dib.Height, dib.Bits, IcoEntryEncoding.Bmp);
        }

        string? hint = null;
        if ((directoryWidth, directoryHeight) != (width, height) && width <= 256 && height <= 256)
        {
            hint = $"Entry {index}'s directory says {directoryWidth}×{directoryHeight}; its {encoding.ToString().ToUpperInvariant()} header says {width}×{height}, which was used.";
        }

        Point? hotspot = type == CursorType ? new Point(field1, field2) : null;
        entry = new Entry(index, (int)offset, (int)size, width, height, bits, encoding, hotspot, dib, hint);
        return null;
    }

    /// <summary>One readable entry: where its data lies, and what its own header says.</summary>
    internal sealed record Entry(int Index, int Offset, int Size, int Width, int Height, int Bits, IcoEntryEncoding Encoding, Point? Hotspot, DibLayout? Dib, string? Hint)
    {
        /// <summary>Gets the depth the best-entry rule ranks by: a PNG counts as 32 bits.</summary>
        public int RankBits => Encoding == IcoEntryEncoding.Png ? 32 : Bits;

        public long Area => (long)Width * Height;

        public IcoEntry Public => new(Width, Height, Bits, Encoding, Hotspot);
    }

    /// <summary>
    /// Picks the entry Load returns: the one named, else with a minimum size the smallest at least that big, else the largest,
    /// then the deepest, then the first. Returns the candidates in the order they are tried, the chosen one first.
    /// </summary>
    internal IReadOnlyList<Entry> Candidates(ReaderOptions options)
    {
        if (options is IcoReaderOptions { Entry: int index })
        {
            Entry? named = Entries.FirstOrDefault(e => e.Index == index);
            if (named is null)
            {
                if (index < 0 || index >= Count)
                {
                    throw new ArgumentOutOfRangeException(nameof(options), index, $"The icon has {Count} entries; entry {index} does not exist.");
                }

                throw new CorruptDataException(IcoFormat.Info.Name, -1, $"Entry {index} of the icon could not be read; see the warnings of a load without IcoReaderOptions.Entry.");
            }

            return [named];
        }

        IEnumerable<Entry> best = Entries.OrderByDescending(e => e.Area).ThenByDescending(e => e.RankBits).ThenBy(e => e.Index);
        if (options.MinimumSize is Size minimum)
        {
            Rect? region = options.Region;
            List<Entry> enough = [.. Entries
                .Where(e => Fits(e, minimum, region))
                .OrderBy(e => e.Area).ThenByDescending(e => e.RankBits).ThenBy(e => e.Index)];
            if (enough.Count > 0)
            {
                return [.. enough, .. best.Where(e => !enough.Contains(e))];
            }
        }

        return [.. best];
    }

    /// <summary>Returns whether an entry is at least <paramref name="minimum"/> big, counting only <paramref name="region"/> when one is asked for.</summary>
    private static bool Fits(Entry entry, Size minimum, Rect? region)
    {
        int width = entry.Width;
        int height = entry.Height;
        if (region is Rect r)
        {
            width = Math.Max(0, Math.Min(entry.Width, r.X + r.Width) - Math.Max(0, r.X));
            height = Math.Max(0, Math.Min(entry.Height, r.Y + r.Height) - Math.Max(0, r.Y));
        }

        return width >= minimum.Width && height >= minimum.Height;
    }
}

/// <summary>The layout of a BMP entry: its info header, the color table and where the color (XOR) rows and the AND mask lie.</summary>
internal sealed class DibLayout
{
    public int HeaderSize { get; private init; }

    public int Width { get; private init; }

    /// <summary>Gets the image height: half the header's, which counts the mask's rows too.</summary>
    public int Height { get; private init; }

    public int Bits { get; private init; }

    public uint Compression { get; private init; }

    /// <summary>Gets where the color rows start, from the start of the entry.</summary>
    public int PixelOffset { get; private init; }

    /// <summary>Gets the bytes of one color row, padded to 4.</summary>
    public long ColorStride => (((long)Width * Bits) + 31) / 32 * 4;

    /// <summary>Gets the bytes of one mask row, padded to 4.</summary>
    public long MaskStride => ((long)Width + 31) / 32 * 4;

    /// <summary>Gets where the AND mask starts, from the start of the entry.</summary>
    public long MaskOffset => PixelOffset + (ColorStride * Height);

    internal static string? TryRead(ReadOnlySpan<byte> bytes, int index, out DibLayout? layout)
    {
        layout = null;
        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (headerSize is not (12 or 40 or 52 or 56 or 108 or 124) || headerSize > bytes.Length)
        {
            return $"Entry {index}'s data is neither a PNG nor a BMP info header.";
        }

        int width, fullHeight, bits;
        uint compression = 0, colorsUsed = 0;
        if (headerSize == 12)
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
            fullHeight = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]);
            bits = BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..]);
        }
        else
        {
            width = BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]);
            fullHeight = BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]);
            bits = BinaryPrimitives.ReadUInt16LittleEndian(bytes[14..]);
            compression = BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]);
            colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(bytes[32..]);
        }

        if (width < 1 || fullHeight < 2)
        {
            return $"Entry {index}'s BMP header gives the size {width}×{fullHeight}, which an icon entry cannot have.";
        }

        if (bits is not (1 or 4 or 8 or 16 or 24 or 32))
        {
            return $"Entry {index}'s BMP header says {bits} bits a pixel; icons use 1, 4, 8, 16, 24 or 32.";
        }

        if (compression is not (0 or 3) || (compression == 3 && bits is not (16 or 32)))
        {
            return $"Entry {index}'s BMP compression {compression} is not allowed in an icon, which stores uncompressed rows.";
        }

        int paletteEntries = bits <= 8 ? (colorsUsed == 0 || colorsUsed > 1u << bits ? 1 << bits : (int)colorsUsed) : (int)Math.Min(colorsUsed, 256);
        int masks = compression == 3 && headerSize == 40 ? 12 : 0;
        long pixels = headerSize + masks + ((long)paletteEntries * (headerSize == 12 ? 3 : 4));
        layout = new DibLayout
        {
            HeaderSize = (int)headerSize,
            Width = width,
            Height = fullHeight / 2,
            Bits = bits,
            Compression = compression,
            PixelOffset = (int)Math.Min(pixels, int.MaxValue),
        };
        if (layout.PixelOffset + layout.ColorStride * layout.Height > bytes.Length && layout.PixelOffset >= bytes.Length)
        {
            layout = null;
            return $"Entry {index} ends before its pixels.";
        }

        return null;
    }
}
