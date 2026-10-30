using System.Buffers.Binary;
using System.Text;

using Egami.Formats.Jpeg2000;
using Egami.Metadata;

namespace Egami.Formats.Icns;

/// <summary>
/// The elements of an icon family, written from Apple's description of the format (the Icon Services types in IconsCore.h and the
/// Icon Utilities documentation): an <c>icns</c> header with the file length, then elements, each a four-character type, a
/// big-endian length that counts its 8-byte header, and data. Image elements become entries; masks, the table of contents and
/// other elements are kept aside or skipped.
/// </summary>
internal sealed class IcnsDirectory
{
    private const int HeaderLength = 8;

    private static ReadOnlySpan<byte> PngSignature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>What each image type holds: its size and how its data is stored when not sniffed from its first bytes.</summary>
    private static readonly Dictionary<string, (int Width, int Height, Kind Kind, int Bits)> Types = new()
    {
        ["ICON"] = (32, 32, Kind.Mono, 1),
        ["ICN#"] = (32, 32, Kind.Mono, 1),
        ["icm#"] = (16, 12, Kind.Mono, 1),
        ["ics#"] = (16, 16, Kind.Mono, 1),
        ["ich#"] = (48, 48, Kind.Mono, 1),
        ["icl4"] = (32, 32, Kind.Indexed, 4),
        ["icm4"] = (16, 12, Kind.Indexed, 4),
        ["ics4"] = (16, 16, Kind.Indexed, 4),
        ["ich4"] = (48, 48, Kind.Indexed, 4),
        ["icl8"] = (32, 32, Kind.Indexed, 8),
        ["icm8"] = (16, 12, Kind.Indexed, 8),
        ["ics8"] = (16, 16, Kind.Indexed, 8),
        ["ich8"] = (48, 48, Kind.Indexed, 8),
        ["is32"] = (16, 16, Kind.Rgb, 24),
        ["il32"] = (32, 32, Kind.Rgb, 24),
        ["ih32"] = (48, 48, Kind.Rgb, 24),
        ["it32"] = (128, 128, Kind.Rgb, 24),
        ["icp4"] = (16, 16, Kind.Rgb, 24),
        ["icp5"] = (32, 32, Kind.Rgb, 24),
        ["icp6"] = (64, 64, Kind.Png, 32),
        ["ic04"] = (16, 16, Kind.Argb, 32),
        ["ic05"] = (32, 32, Kind.Argb, 32),
        ["icsb"] = (18, 18, Kind.Argb, 32),
        ["icsB"] = (36, 36, Kind.Png, 32),
        ["sb24"] = (24, 24, Kind.Png, 32),
        ["SB24"] = (48, 48, Kind.Png, 32),
        ["ic07"] = (128, 128, Kind.Png, 32),
        ["ic08"] = (256, 256, Kind.Png, 32),
        ["ic09"] = (512, 512, Kind.Png, 32),
        ["ic10"] = (1024, 1024, Kind.Png, 32),
        ["ic11"] = (32, 32, Kind.Png, 32),
        ["ic12"] = (64, 64, Kind.Png, 32),
        ["ic13"] = (256, 256, Kind.Png, 32),
        ["ic14"] = (512, 512, Kind.Png, 32),
    };

    /// <summary>The 8-bit mask of each RGB type.</summary>
    private static readonly Dictionary<string, string> AlphaMasks = new()
    {
        ["is32"] = "s8mk",
        ["il32"] = "l8mk",
        ["ih32"] = "h8mk",
        ["it32"] = "t8mk",
        ["icp4"] = "s8mk",
        ["icp5"] = "l8mk",
    };

    /// <summary>The 1-bit icon and mask of each size, keyed by the old types that take their mask from it.</summary>
    private static readonly Dictionary<string, string> BitMasks = new()
    {
        ["ICN#"] = "ICN#",
        ["icl4"] = "ICN#",
        ["icl8"] = "ICN#",
        ["il32"] = "ICN#",
        ["icm#"] = "icm#",
        ["icm4"] = "icm#",
        ["icm8"] = "icm#",
        ["ics#"] = "ics#",
        ["ics4"] = "ics#",
        ["ics8"] = "ics#",
        ["is32"] = "ics#",
        ["ich#"] = "ich#",
        ["ich4"] = "ich#",
        ["ich8"] = "ich#",
        ["ih32"] = "ich#",
    };

    private readonly Dictionary<string, (int Offset, int Length)> _elements = [];

    private IcnsDirectory()
    {
    }

    /// <summary>How an element's pixels are stored.</summary>
    internal enum Kind
    {
        Png,
        Jpeg2000,
        Argb,
        Rgb,
        Indexed,
        Mono,
    }

    /// <summary>Gets the image entries, in file order.</summary>
    public List<Entry> Entries { get; } = [];

    /// <summary>Gets the warnings parsing gave.</summary>
    public WarningList Warnings { get; } = new();

    internal static bool Matches(ReadOnlySpan<byte> header)
    {
        return header.Length >= HeaderLength && header.StartsWith("icns"u8) && BinaryPrimitives.ReadUInt32BigEndian(header[4..]) >= HeaderLength;
    }

    /// <summary>Lists the elements. A length past the end of the file stops the list there: an error when strict, else a warning.</summary>
    public static IcnsDirectory Parse(ReadOnlySpan<byte> data, bool strict)
    {
        if (!Matches(data))
        {
            throw new CorruptDataException(IcnsFormat.Info.Name, 0, "The ICNS signature is wrong.");
        }

        IcnsDirectory directory = new();
        long end = Math.Min(BinaryPrimitives.ReadUInt32BigEndian(data[4..]), data.Length);
        if (end < BinaryPrimitives.ReadUInt32BigEndian(data[4..]))
        {
            if (strict)
            {
                throw new CorruptDataException(IcnsFormat.Info.Name, 4, $"The header says the file is {BinaryPrimitives.ReadUInt32BigEndian(data[4..])} bytes; it has {data.Length}.") { IsTruncation = true };
            }

            directory.Warnings.Add("Icns.Truncated", $"The header says the file is {BinaryPrimitives.ReadUInt32BigEndian(data[4..])} bytes; it has {data.Length}.");
        }

        int offset = HeaderLength;
        while (offset + HeaderLength <= end)
        {
            string type = Encoding.Latin1.GetString(data.Slice(offset, 4));
            uint length = BinaryPrimitives.ReadUInt32BigEndian(data[(offset + 4)..]);
            if (length < HeaderLength || offset + length > end)
            {
                if (strict)
                {
                    throw new CorruptDataException(IcnsFormat.Info.Name, offset, $"The '{type}' element's length {length} does not fit the file.");
                }

                directory.Warnings.Add("Icns.ElementCut", $"The '{type}' element at byte {offset} has length {length}, which does not fit the file; the elements from it on were skipped.", offset);
                break;
            }

            directory._elements.TryAdd(type, (offset + HeaderLength, (int)length - HeaderLength));
            if (Types.TryGetValue(type, out (int Width, int Height, Kind Kind, int Bits) known))
            {
                ReadOnlySpan<byte> body = data.Slice(offset + HeaderLength, (int)length - HeaderLength);
                Kind kind = Sniff(body, known.Kind);
                if (kind == Kind.Png && body.Length >= 24)
                {
                    // A PNG entry's own size wins over its type's.
                    known.Width = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(body[16..]), int.MaxValue);
                    known.Height = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(body[20..]), int.MaxValue);
                }

                directory.Entries.Add(new Entry(type, known.Width, known.Height, kind, kind is Kind.Png or Kind.Jpeg2000 or Kind.Argb ? 32 : known.Bits, offset + HeaderLength, body.Length));
            }

            offset += (int)length;
        }

        if (directory.Entries.Count == 0)
        {
            throw new CorruptDataException(IcnsFormat.Info.Name, HeaderLength, "The icon family has no image the reader knows.");
        }

        foreach (Entry entry in directory.Entries)
        {
            entry.Index = directory.Entries.IndexOf(entry);
            if (entry.Kind == Kind.Rgb)
            {
                entry.AlphaMask = directory.Element(AlphaMasks[entry.Type]);
                if (entry.AlphaMask is null && BitMasks.TryGetValue(entry.Type, out string? mono))
                {
                    entry.BitMask = directory.Element(mono);
                }

                entry.Bits = entry.AlphaMask is null && entry.BitMask is null ? 24 : 32;
            }
            else if (entry.Kind is Kind.Indexed or Kind.Mono && BitMasks.TryGetValue(entry.Type, out string? mono))
            {
                entry.BitMask = directory.Element(mono);
            }
        }

        return directory;
    }

    /// <summary>Returns the entries in the order Load tries them: the one named, or the best first.</summary>
    public IReadOnlyList<Entry> Candidates(ReaderOptions options)
    {
        if (options is IcnsReaderOptions { Entry: int index })
        {
            if (index < 0 || index >= Entries.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(options), index, $"The icon family has {Entries.Count} entries; entry {index} does not exist.");
            }

            return [Entries[index]];
        }

        List<Entry> best = [.. Entries.OrderByDescending(e => (long)e.Width * e.Height).ThenByDescending(e => e.Bits).ThenBy(e => e.Index)];
        if (options.MinimumSize is Size minimum)
        {
            List<Entry> enough = [.. Entries
                .Where(e => e.Width >= minimum.Width && e.Height >= minimum.Height)
                .OrderBy(e => (long)e.Width * e.Height).ThenByDescending(e => e.Bits).ThenBy(e => e.Index)];
            if (enough.Count > 0)
            {
                return [.. enough, .. best.Where(e => !enough.Contains(e))];
            }
        }

        return best;
    }

    private (int Offset, int Length)? Element(string type) => _elements.TryGetValue(type, out (int, int) found) ? found : null;

    /// <summary>Tells PNG, JPEG 2000 and ARGB data apart from the type's usual storage by their first bytes.</summary>
    private static Kind Sniff(ReadOnlySpan<byte> body, Kind usual)
    {
        if (body.StartsWith(PngSignature))
        {
            return Kind.Png;
        }

        if (body.StartsWith(Jp2File.Signature) || J2kCodestream.Matches(body))
        {
            return Kind.Jpeg2000;
        }

        if (body.StartsWith("ARGB"u8))
        {
            return Kind.Argb;
        }

        // A type for PNG with other data is most likely ARGB without its tag; the decoder reports it if not.
        return usual == Kind.Png ? Kind.Argb : usual;
    }

    /// <summary>One image element and the masks that go with it.</summary>
    internal sealed class Entry(string type, int width, int height, Kind kind, int bits, int offset, int length)
    {
        public string Type => type;

        public int Width => width;

        public int Height => height;

        public Kind Kind => kind;

        public int Bits { get; set; } = bits;

        public int Offset => offset;

        public int Length => length;

        public int Index { get; set; }

        /// <summary>Gets or sets the 8-bit mask of an RGB entry.</summary>
        public (int Offset, int Length)? AlphaMask { get; set; }

        /// <summary>Gets or sets the 1-bit icon and mask element whose second half masks this entry.</summary>
        public (int Offset, int Length)? BitMask { get; set; }

        public IcnsEntryEncoding Encoding => kind switch
        {
            Kind.Png => IcnsEntryEncoding.Png,
            Kind.Jpeg2000 => IcnsEntryEncoding.Jpeg2000,
            Kind.Argb => IcnsEntryEncoding.Argb,
            Kind.Rgb => IcnsEntryEncoding.Rgb,
            _ => IcnsEntryEncoding.Indexed,
        };

        public IcnsEntry Public => new(type, width, height, Bits, Encoding);
    }
}
