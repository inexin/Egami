using System.Buffers.Binary;
using System.Text;

using Egami.Formats.Iso;

namespace Egami.Video.Containers.Mp4;

/// <summary>
/// Reads MP4 and QuickTime metadata (V4.1 Design Q8): iTunes-style meta/ilst items, QuickTime meta keys and udta text boxes as
/// Matroska tag names, cover art (covr) as attachments, and Nero chapters (chpl).
/// </summary>
internal static class Mp4Metadata
{
    /// <summary>ilst item and QuickTime udta box types, and the Matroska tag names they map to.</summary>
    private static readonly Dictionary<uint, string> TagNames = new()
    {
        [Code("©nam")] = "TITLE",
        [Code("©ART")] = "ARTIST",
        [Code("aART")] = "ALBUM_ARTIST",
        [Code("©alb")] = "ALBUM",
        [Code("©day")] = "DATE",
        [Code("©cmt")] = "COMMENT",
        [Code("©too")] = "ENCODER",
        [Code("©gen")] = "GENRE",
        [Code("©wrt")] = "COMPOSER",
        [Code("©grp")] = "GROUPING",
        [Code("©lyr")] = "LYRICS",
        [Code("©xyz")] = "LOCATION",
        [Code("©des")] = "DESCRIPTION",
        [Code("©inf")] = "DESCRIPTION",
        [Code("©cpy")] = "COPYRIGHT",
        [Code("©swr")] = "ENCODER",
        [Code("©enc")] = "ENCODED_BY",
        [Code("cprt")] = "COPYRIGHT",
        [Code("desc")] = "DESCRIPTION",
        [Code("ldes")] = "SYNOPSIS",
        [Code("tvsh")] = "SHOW",
        [Code("tven")] = "EPISODE_ID",
        [Code("tvsn")] = "SEASON",
        [Code("tves")] = "PART_NUMBER",
        [Code("trkn")] = "TRACK",
        [Code("disk")] = "DISC",
    };

    /// <summary>QuickTime meta key names (com.apple.quicktime.*) and their Matroska tag names.</summary>
    private static readonly Dictionary<string, string> KeyNames = new(StringComparer.Ordinal)
    {
        ["com.apple.quicktime.title"] = "TITLE",
        ["com.apple.quicktime.artist"] = "ARTIST",
        ["com.apple.quicktime.album"] = "ALBUM",
        ["com.apple.quicktime.creationdate"] = "DATE",
        ["com.apple.quicktime.comment"] = "COMMENT",
        ["com.apple.quicktime.description"] = "DESCRIPTION",
        ["com.apple.quicktime.software"] = "ENCODER",
        ["com.apple.quicktime.location.ISO6709"] = "LOCATION",
        ["com.apple.quicktime.make"] = "MAKE",
        ["com.apple.quicktime.model"] = "MODEL",
        ["com.apple.quicktime.copyright"] = "COPYRIGHT",
        ["com.apple.quicktime.genre"] = "GENRE",
    };

    /// <summary>Gets the Matroska tag name an ilst item type maps to, for the muxer's reverse mapping.</summary>
    public static IReadOnlyDictionary<uint, string> ItemTags => TagNames;

    /// <summary>Reads a movie-level udta box into tags, chapters and cover art.</summary>
    public static void ReadMovie(IsoBoxReader parent, IsoBox udta, Dictionary<string, string> tags, List<ChapterInfo> chapters, List<AttachmentInfo> attachments)
    {
        IsoBoxReader children = UserData(ref parent, udta);
        while (children.Next(out IsoBox box))
        {
            ReadOnlySpan<byte> c = children.Content(box);
            string type = box.Type.ToString();
            if (type == "meta")
            {
                ReadMeta(children, box, tags, attachments);
            }
            else if (type == "chpl")
            {
                ReadChpl(c, chapters);
            }
            else if (box.Type.Value >> 24 == 0xA9 && TextBox(c) is { } text)
            {
                // A QuickTime text box: a 16-bit length and language, then the text.
                tags.TryAdd(TagNames.GetValueOrDefault(box.Type.Value, box.Type.ToString()), text);
            }
        }
    }

    /// <summary>
    /// Returns a reader over a udta box's items. A QuickTime user data list may end with a 32-bit zero after its last item
    /// (QuickTime File Format, User Data Atoms), as QuickTime 7 wrote it; that terminator is left out rather than read as a
    /// cut-off box.
    /// </summary>
    private static IsoBoxReader UserData(ref IsoBoxReader parent, in IsoBox udta)
    {
        ReadOnlySpan<byte> content = parent.Content(udta);
        int at = 0;
        while (content.Length - at >= 8)
        {
            uint size = BinaryPrimitives.ReadUInt32BigEndian(content[at..]);
            if (size < 8 || size > content.Length - at)
            {
                break;
            }

            at += (int)size;
        }

        int left = content.Length - at;
        return left is > 0 and < 8 && !content[at..].ContainsAnyExcept((byte)0)
            ? new IsoBoxReader(content[..at], "QuickTime", udta.ContentStart)
            : parent.Children(udta);
    }

    /// <summary>Reads a meta box (iTunes mdir with ilst, or QuickTime mdta with keys and ilst).</summary>
    public static void ReadMeta(IsoBoxReader parent, IsoBox meta, Dictionary<string, string> tags, List<AttachmentInfo> attachments)
    {
        // ISO meta is a full box; QuickTime's is not, which shows as an hdlr box straight after the header.
        ReadOnlySpan<byte> content = parent.Content(meta);
        int skip = content.Length >= 8 && content[4..8].SequenceEqual("hdlr"u8) ? 0 : 4;
        IsoBoxReader children = parent.Children(meta, Math.Min(skip, content.Length));
        List<string> keys = [];
        IsoBox? ilst = null;
        while (children.Next(out IsoBox box))
        {
            switch (box.Type.ToString())
            {
                case "keys":
                    ReadOnlySpan<byte> k = children.Content(box);
                    if (k.Length >= 8)
                    {
                        uint count = BinaryPrimitives.ReadUInt32BigEndian(k[4..]);
                        int at = 8;
                        for (uint i = 0; i < count && at + 8 <= k.Length; i++)
                        {
                            int size = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(k[at..]), int.MaxValue);
                            if (size < 8 || at + size > k.Length)
                            {
                                break;
                            }

                            keys.Add(Encoding.UTF8.GetString(k.Slice(at + 8, size - 8)));
                            at += size;
                        }
                    }

                    break;
                case "ilst":
                    ilst = box;
                    break;
            }
        }

        if (ilst is not IsoBox list)
        {
            return;
        }

        IsoBoxReader items = children.Children(list);
        while (items.Next(out IsoBox item))
        {
            string? name;
            if (keys.Count > 0 && item.Type.Value >= 1 && item.Type.Value <= keys.Count)
            {
                string key = keys[(int)item.Type.Value - 1];
                name = KeyNames.GetValueOrDefault(key, key);
            }
            else
            {
                name = TagNames.GetValueOrDefault(item.Type.Value);
            }

            string? freeform = null;
            IsoBoxReader parts = items.Children(item);
            while (parts.Next(out IsoBox part))
            {
                ReadOnlySpan<byte> p = parts.Content(part);
                switch (part.Type.ToString())
                {
                    case "name" when p.Length >= 4:
                        freeform = Encoding.UTF8.GetString(p[4..]);
                        break;
                    case "data" when p.Length >= 8:
                        uint dataType = BinaryPrimitives.ReadUInt32BigEndian(p) & 0xFFFFFF;
                        ReadOnlySpan<byte> value = p[8..];
                        string itemName = name ?? freeform ?? item.Type.ToString();
                        if (item.Type.ToString() == "covr" && dataType is 13 or 14 or 27 or 0)
                        {
                            bool png = dataType == 14 || value.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G']);
                            string fileName = attachments.Any(a => a.IsCoverArt) ? $"cover{attachments.Count + 1}" : "cover";
                            attachments.Add(new AttachmentInfo(fileName + (png ? ".png" : ".jpg"), png ? "image/png" : "image/jpeg", value.ToArray()));
                        }
                        else if (Value(item.Type.ToString(), dataType, value) is { } text)
                        {
                            tags.TryAdd(itemName, text);
                        }

                        break;
                }
            }
        }
    }

    /// <summary>Reads a track's udta for its name: a QuickTime 'name' box, or a ©nam text box.</summary>
    public static string? ReadTrackName(IsoBoxReader parent, IsoBox udta)
    {
        IsoBoxReader children = UserData(ref parent, udta);
        while (children.Next(out IsoBox box))
        {
            ReadOnlySpan<byte> c = children.Content(box);
            if (box.Type.ToString() == "name" && c.Length > 0)
            {
                string name = Encoding.UTF8.GetString(c).TrimEnd('\0');
                return name.Length > 0 ? name : null;
            }

            if (box.Type.Value == Code("©nam") && TextBox(c) is { } text)
            {
                return text;
            }
        }

        return null;
    }

    /// <summary>Reads Nero chapters: a full box, 4 reserved bytes in version 1, a count, then each start (100 ns units) and a Pascal title.</summary>
    public static void ReadChpl(ReadOnlySpan<byte> c, List<ChapterInfo> chapters)
    {
        if (c.Length < 5)
        {
            return;
        }

        int at = c[0] == 1 ? 8 : 4;
        if (at >= c.Length)
        {
            return;
        }

        int count = c[at++];
        List<(long Start, string Title)> list = [];
        for (int i = 0; i < count && at + 9 <= c.Length; i++)
        {
            long start = (long)Math.Min(BinaryPrimitives.ReadUInt64BigEndian(c[at..]), long.MaxValue);
            int length = c[at + 8];
            at += 9;
            if (at + length > c.Length)
            {
                break;
            }

            list.Add((start, Encoding.UTF8.GetString(c.Slice(at, length))));
            at += length;
        }

        // Starts beyond a century are broken; they are kept at one.
        const long Century = 100L * 365 * 24 * 3600 * 10_000_000;
        for (int i = 0; i < list.Count; i++)
        {
            list[i] = (Math.Min(list[i].Start, Century), list[i].Title);
        }

        Rational hundredNs = new(1, 10_000_000);
        for (int i = 0; i < list.Count; i++)
        {
            MediaTime? end = i + 1 < list.Count ? new MediaTime(list[i + 1].Start, hundredNs) : null;
            chapters.Add(new ChapterInfo(new MediaTime(list[i].Start, hundredNs), end, list[i].Title.Length > 0 ? list[i].Title : null));
        }
    }

    /// <summary>Reads a chapter text sample (a 16-bit length, then UTF-8 or UTF-16 with a byte order mark), or <see langword="null"/>.</summary>
    public static string? ChapterText(ReadOnlySpan<byte> sample)
    {
        if (sample.Length < 2)
        {
            return null;
        }

        int length = Math.Min(BinaryPrimitives.ReadUInt16BigEndian(sample), sample.Length - 2);
        ReadOnlySpan<byte> text = sample.Slice(2, length);
        return text.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]) ? Encoding.BigEndianUnicode.GetString(text[2..])
            : text.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) ? Encoding.Unicode.GetString(text[2..])
            : Encoding.UTF8.GetString(text);
    }

    private static string? TextBox(ReadOnlySpan<byte> c)
    {
        if (c.Length < 4)
        {
            return null;
        }

        int length = Math.Min(BinaryPrimitives.ReadUInt16BigEndian(c), c.Length - 4);
        string text = Encoding.UTF8.GetString(c.Slice(4, length)).TrimEnd('\0');
        return text.Length > 0 ? text : null;
    }

    /// <summary>Reads a data box's value as text: UTF-8 or UTF-16 strings, integers, and the track and disc number pairs.</summary>
    private static string? Value(string item, uint dataType, ReadOnlySpan<byte> value)
    {
        if (item is "trkn" or "disk" && value.Length >= 6)
        {
            int number = BinaryPrimitives.ReadUInt16BigEndian(value[2..]), total = BinaryPrimitives.ReadUInt16BigEndian(value[4..]);
            return total > 0 ? $"{number}/{total}" : number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return dataType switch
        {
            1 => Encoding.UTF8.GetString(value),
            2 => Encoding.BigEndianUnicode.GetString(value),
            21 or 22 => value.Length switch
            {
                1 => (dataType == 21 ? (sbyte)value[0] : (int)value[0]).ToString(System.Globalization.CultureInfo.InvariantCulture),
                2 => (dataType == 21 ? BinaryPrimitives.ReadInt16BigEndian(value) : (int)BinaryPrimitives.ReadUInt16BigEndian(value)).ToString(System.Globalization.CultureInfo.InvariantCulture),
                4 => (dataType == 21 ? BinaryPrimitives.ReadInt32BigEndian(value) : (long)BinaryPrimitives.ReadUInt32BigEndian(value)).ToString(System.Globalization.CultureInfo.InvariantCulture),
                8 => BinaryPrimitives.ReadInt64BigEndian(value).ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => null,
            },
            _ => null,
        };
    }

    private static uint Code(string text) => FourCC.Parse(text).Value;
}
