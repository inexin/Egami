using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Egami.Video.Containers.Mp3;

/// <summary>
/// ID3 tags (id3.org's ID3v2.2, 2.3 and 2.4 informal standards and the ID3v1 layout) and APEv2 tags: text frames become
/// Matroska tag names, pictures become attachments (the front cover named cover.*) and CHAP frames become chapters. Writes
/// ID3v2.4 in UTF-8.
/// </summary>
internal static class Id3Tag
{
    /// <summary>The ID3v2 header and footer size.</summary>
    public const int HeaderSize = 10;

    /// <summary>The ID3v1 tag size.</summary>
    public const int V1Size = 128;

    /// <summary>The APEv2 header and footer size.</summary>
    public const int ApeFooterSize = 32;

    /// <summary>ID3v2.3 and 2.4 frame ids and the Matroska tag names they map to; the reader also takes ID3v2.2's three-letter ids.</summary>
    private static readonly Dictionary<string, string> TextFrames = new(StringComparer.Ordinal)
    {
        ["TIT2"] = "TITLE",
        ["TPE1"] = "ARTIST",
        ["TALB"] = "ALBUM",
        ["TCON"] = "GENRE",
        ["TDRC"] = "DATE",
        ["TYER"] = "DATE",
        ["TRCK"] = "PART_NUMBER",
        ["TPOS"] = "DISC",
        ["TCOP"] = "COPYRIGHT",
        ["TSSE"] = "ENCODER",
        ["TENC"] = "ENCODER",
        ["TPE2"] = "ALBUM_ARTIST",
        ["TCOM"] = "COMPOSER",
        ["TBPM"] = "BPM",
        ["TIT1"] = "GROUPING",
        ["TIT3"] = "SUBTITLE",
        ["TPUB"] = "PUBLISHER",
        ["TSRC"] = "ISRC",
        ["TEXT"] = "LYRICIST",
        ["TPE3"] = "CONDUCTOR",
        ["TLAN"] = "LANGUAGE",
        ["TT2"] = "TITLE",
        ["TP1"] = "ARTIST",
        ["TAL"] = "ALBUM",
        ["TCO"] = "GENRE",
        ["TYE"] = "DATE",
        ["TRK"] = "PART_NUMBER",
        ["TPA"] = "DISC",
        ["TCR"] = "COPYRIGHT",
        ["TSS"] = "ENCODER",
        ["TEN"] = "ENCODER",
        ["TP2"] = "ALBUM_ARTIST",
        ["TCM"] = "COMPOSER",
        ["TBP"] = "BPM",
        ["TT1"] = "GROUPING",
        ["TT3"] = "SUBTITLE",
        ["TPB"] = "PUBLISHER",
        ["TRC"] = "ISRC",
        ["TXT"] = "LYRICIST",
        ["TP3"] = "CONDUCTOR",
        ["TLA"] = "LANGUAGE",
    };

    /// <summary>The tag names the writer stores in their own ID3v2.4 frames; others go in TXXX frames.</summary>
    private static readonly Dictionary<string, string> WriteFrames = new(StringComparer.Ordinal)
    {
        ["TITLE"] = "TIT2",
        ["ARTIST"] = "TPE1",
        ["ALBUM"] = "TALB",
        ["GENRE"] = "TCON",
        ["DATE"] = "TDRC",
        ["PART_NUMBER"] = "TRCK",
        ["TRACK"] = "TRCK",
        ["DISC"] = "TPOS",
        ["COPYRIGHT"] = "TCOP",
        ["ENCODER"] = "TSSE",
        ["ENCODED_BY"] = "TENC",
        ["ALBUM_ARTIST"] = "TPE2",
        ["COMPOSER"] = "TCOM",
        ["BPM"] = "TBPM",
        ["GROUPING"] = "TIT1",
        ["SUBTITLE"] = "TIT3",
        ["PUBLISHER"] = "TPUB",
        ["ISRC"] = "TSRC",
        ["LYRICIST"] = "TEXT",
        ["CONDUCTOR"] = "TPE3",
        ["LANGUAGE"] = "TLAN",
    };

    /// <summary>APEv2 item keys (compared without case) and their Matroska tag names; other text items keep their key in upper case.</summary>
    private static readonly Dictionary<string, string> ApeItems = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Title"] = "TITLE",
        ["Artist"] = "ARTIST",
        ["Album"] = "ALBUM",
        ["Album Artist"] = "ALBUM_ARTIST",
        ["Year"] = "DATE",
        ["Genre"] = "GENRE",
        ["Track"] = "PART_NUMBER",
        ["Disc"] = "DISC",
        ["Comment"] = "COMMENT",
        ["Composer"] = "COMPOSER",
        ["Copyright"] = "COPYRIGHT",
        ["Publisher"] = "PUBLISHER",
        ["ISRC"] = "ISRC",
        ["Language"] = "LANGUAGE",
        ["Subtitle"] = "SUBTITLE",
        ["Conductor"] = "CONDUCTOR",
    };

    /// <summary>The ID3v1 genres 0 to 79 and Winamp's additions 80 to 125, which TCON frames also refer to as "(n)".</summary>
    private static readonly string[] Genres =
    [
        "Blues", "Classic Rock", "Country", "Dance", "Disco", "Funk", "Grunge", "Hip-Hop", "Jazz", "Metal", "New Age", "Oldies",
        "Other", "Pop", "R&B", "Rap", "Reggae", "Rock", "Techno", "Industrial", "Alternative", "Ska", "Death Metal", "Pranks",
        "Soundtrack", "Euro-Techno", "Ambient", "Trip-Hop", "Vocal", "Jazz+Funk", "Fusion", "Trance", "Classical", "Instrumental",
        "Acid", "House", "Game", "Sound Clip", "Gospel", "Noise", "AlternRock", "Bass", "Soul", "Punk", "Space", "Meditative",
        "Instrumental Pop", "Instrumental Rock", "Ethnic", "Gothic", "Darkwave", "Techno-Industrial", "Electronic", "Pop-Folk",
        "Eurodance", "Dream", "Southern Rock", "Comedy", "Cult", "Gangsta", "Top 40", "Christian Rap", "Pop/Funk", "Jungle",
        "Native American", "Cabaret", "New Wave", "Psychadelic", "Rave", "Showtunes", "Trailer", "Lo-Fi", "Tribal", "Acid Punk",
        "Acid Jazz", "Polka", "Retro", "Musical", "Rock & Roll", "Hard Rock", "Folk", "Folk-Rock", "National Folk", "Swing",
        "Fast Fusion", "Bebob", "Latin", "Revival", "Celtic", "Bluegrass", "Avantgarde", "Gothic Rock", "Progressive Rock",
        "Psychedelic Rock", "Symphonic Rock", "Slow Rock", "Big Band", "Chorus", "Easy Listening", "Acoustic", "Humour", "Speech",
        "Chanson", "Opera", "Chamber Music", "Sonata", "Symphony", "Booty Bass", "Primus", "Porn Groove", "Satire", "Slow Jam",
        "Club", "Tango", "Samba", "Folklore", "Ballad", "Power Ballad", "Rhythmic Soul", "Freestyle", "Duet", "Punk Rock",
        "Drum Solo", "A capella", "Euro-House", "Dance Hall",
    ];

    /// <summary>Gets the whole size of the ID3v2 tag whose header starts <paramref name="header"/> (with its footer), or 0 when it is not one.</summary>
    public static int Size(ReadOnlySpan<byte> header)
    {
        if (header.Length < HeaderSize || !header.StartsWith("ID3"u8) || header[3] is < 2 or > 4 || header[3] == 0xFF
            || ((header[6] | header[7] | header[8] | header[9]) & 0x80) != 0)
        {
            return 0;
        }

        return HeaderSize + SyncSafe(header[6..]) + (header[3] == 4 && (header[5] & 0x10) != 0 ? HeaderSize : 0);
    }

    /// <summary>Gets the whole size of an ID3v2.4 tag that ends with the footer <paramref name="footer"/> ("3DI"), or 0.</summary>
    public static int FooterTagSize(ReadOnlySpan<byte> footer) =>
        footer.Length >= HeaderSize && footer.StartsWith("3DI"u8) && footer[3] == 4 && ((footer[6] | footer[7] | footer[8] | footer[9]) & 0x80) == 0
            ? (2 * HeaderSize) + SyncSafe(footer[6..])
            : 0;

    /// <summary>
    /// Reads an ID3v2.2, 2.3 or 2.4 tag: text frames into <paramref name="tags"/> (the first value of a name wins), pictures into
    /// <paramref name="attachments"/> and CHAP frames into <paramref name="chapters"/>, when those are given.
    /// </summary>
    public static void Read(ReadOnlySpan<byte> tag, Dictionary<string, string> tags, List<ChapterInfo>? chapters = null, List<AttachmentInfo>? attachments = null)
    {
        if (Size(tag) == 0)
        {
            return;
        }

        int version = tag[3];
        byte flags = tag[5];
        int size = Math.Min(SyncSafe(tag[6..]), tag.Length - HeaderSize);
        byte[] body = tag.Slice(HeaderSize, size).ToArray();
        if ((flags & 0x80) != 0 && version < 4)
        {
            // Unsynchronisation of the whole tag (version 2.4 marks it per frame).
            body = Resync(body);
        }

        if (version == 2 && (flags & 0x40) != 0)
        {
            // Version 2.2's compression bit: no scheme was ever defined, so the tag cannot be read.
            return;
        }

        int p = 0;
        if (version >= 3 && (flags & 0x40) != 0 && body.Length >= 4)
        {
            p = version == 4 ? SyncSafe(body) : 4 + (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(body), int.MaxValue - 4);
        }

        int pictures = attachments?.Count ?? 0;
        ReadFrames(body, p, version, (flags & 0x80) != 0, tags, chapters, attachments);
        chapters?.Sort((a, b) => a.Start.CompareTo(b.Start));
        if (attachments is not null && !attachments.Any(a => a.IsCoverArt)
            && attachments.FindIndex(pictures, a => a.Name.StartsWith($"picture-", StringComparison.Ordinal) && Path.GetFileNameWithoutExtension(a.Name).EndsWith("-0", StringComparison.Ordinal)) is >= 0 and int other)
        {
            // Writers such as FFmpeg mark an attached picture "other" (type 0); with no front cover it is the cover.
            attachments[other] = attachments[other] with { Name = "cover" + Path.GetExtension(attachments[other].Name) };
        }
    }

    /// <summary>Reads an ID3v1 or 1.1 tag (128 bytes starting "TAG"); names already present keep their value.</summary>
    public static void ReadV1(ReadOnlySpan<byte> tag, Dictionary<string, string> tags)
    {
        if (tag.Length < V1Size || !tag.StartsWith("TAG"u8))
        {
            return;
        }

        Add(tags, "TITLE", V1Text(tag.Slice(3, 30)));
        Add(tags, "ARTIST", V1Text(tag.Slice(33, 30)));
        Add(tags, "ALBUM", V1Text(tag.Slice(63, 30)));
        Add(tags, "DATE", V1Text(tag.Slice(93, 4)));
        bool v11 = tag[125] == 0 && tag[126] != 0;
        Add(tags, "COMMENT", V1Text(tag.Slice(97, v11 ? 28 : 30)));
        if (v11)
        {
            Add(tags, "PART_NUMBER", tag[126].ToString(CultureInfo.InvariantCulture));
        }

        if (tag[127] < Genres.Length)
        {
            Add(tags, "GENRE", Genres[tag[127]]);
        }
    }

    /// <summary>Gets the whole size of the APEv2 tag (with its header) whose footer is <paramref name="footer"/>, or 0.</summary>
    public static int ApeSize(ReadOnlySpan<byte> footer, out int items)
    {
        items = 0;
        if (footer.Length < ApeFooterSize || !footer.StartsWith("APETAGEX"u8))
        {
            return 0;
        }

        uint size = BinaryPrimitives.ReadUInt32LittleEndian(footer[12..]);
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(footer[20..]);
        items = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(footer[16..]), int.MaxValue);
        if (size < ApeFooterSize || size > int.MaxValue - ApeFooterSize)
        {
            return 0;
        }

        return (int)size + ((flags & 0x80000000) != 0 ? ApeFooterSize : 0);
    }

    /// <summary>Reads the items of an APEv2 tag (<paramref name="tag"/> is the whole tag, footer last): UTF-8 text items and cover art.</summary>
    public static void ReadApe(ReadOnlySpan<byte> tag, Dictionary<string, string> tags, List<AttachmentInfo>? attachments = null)
    {
        if (tag.Length < ApeFooterSize || ApeSize(tag[^ApeFooterSize..], out int count) == 0)
        {
            return;
        }

        uint footerSize = BinaryPrimitives.ReadUInt32LittleEndian(tag[(tag.Length - ApeFooterSize + 12)..]);
        int p = tag.Length - (int)Math.Min(footerSize, (uint)tag.Length);
        int end = tag.Length - ApeFooterSize;
        for (int i = 0; i < count && p + 8 < end; i++)
        {
            int length = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(tag[p..]), int.MaxValue);
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(tag[(p + 4)..]);
            int keyEnd = tag[(p + 8)..end].IndexOf((byte)0);
            if (keyEnd < 0 || length > end - (p + 8 + keyEnd + 1))
            {
                return;
            }

            string key = Encoding.ASCII.GetString(tag.Slice(p + 8, keyEnd));
            ReadOnlySpan<byte> value = tag.Slice(p + 8 + keyEnd + 1, length);
            p += 8 + keyEnd + 1 + length;
            int kind = (int)(flags >> 1) & 3;
            if (kind == 0)
            {
                // Several values are separated by NUL; keep the first.
                string text = Encoding.UTF8.GetString(value);
                int nul = text.IndexOf('\0', StringComparison.Ordinal);
                string name = ApeItems.TryGetValue(key, out string? mapped) ? mapped : key.ToUpperInvariant().Replace(' ', '_');
                Add(tags, name, (nul < 0 ? text : text[..nul]).Trim());
            }
            else if (kind == 1 && attachments is not null && key.StartsWith("Cover Art", StringComparison.OrdinalIgnoreCase))
            {
                // Binary cover art: a file name, NUL, then the image.
                int nameEnd = value.IndexOf((byte)0);
                ReadOnlySpan<byte> image = value[(nameEnd + 1)..];
                string? mime = ImageType(image);
                if (mime is not null)
                {
                    bool front = key.Equals("Cover Art (Front)", StringComparison.OrdinalIgnoreCase);
                    attachments.Add(Picture(attachments, front ? 3 : 4, mime, image.ToArray(), null));
                }
            }
        }
    }

    /// <summary>
    /// Writes an ID3v2.4 tag in UTF-8: the tags as text frames (names without a frame of their own as TXXX), image attachments
    /// as APIC frames (cover art as the front cover) and chapters as CHAP frames with a CTOC. Returns null when there is nothing to write.
    /// </summary>
    public static byte[]? Write(MediaMetadata metadata, MediaTime? duration)
    {
        using MemoryStream frames = new();
        HashSet<string> written = [];
        foreach ((string name, string value) in metadata.Tags)
        {
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            if (name == "COMMENT")
            {
                // Encoding, language "und", an empty description, then the text.
                WriteFrame(frames, "COMM", [3, .. "und"u8, 0, .. Encoding.UTF8.GetBytes(value)]);
            }
            else if (WriteFrames.TryGetValue(name, out string? id))
            {
                if (written.Add(id))
                {
                    WriteFrame(frames, id, [3, .. Encoding.UTF8.GetBytes(value)]);
                }
            }
            else
            {
                WriteFrame(frames, "TXXX", [3, .. Encoding.UTF8.GetBytes(name), 0, .. Encoding.UTF8.GetBytes(value)]);
            }
        }

        bool cover = false;
        foreach (AttachmentInfo attachment in metadata.Attachments)
        {
            if (!attachment.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Encoding, MIME type, picture type (3, front cover, once), description, then the image.
            byte type = attachment.IsCoverArt && !cover ? (byte)3 : (byte)0;
            cover |= type == 3;
            WriteFrame(frames, "APIC", [3, .. Encoding.ASCII.GetBytes(attachment.MediaType), 0, type, .. Encoding.UTF8.GetBytes(attachment.Description ?? ""), 0, .. attachment.Data.Span]);
        }

        IReadOnlyList<ChapterInfo> chapters = metadata.Chapters;
        if (chapters.Count > 0)
        {
            List<byte> toc = [.. "toc"u8, 0, 0x03, (byte)Math.Min(chapters.Count, 255)];
            Span<byte> times = stackalloc byte[16];
            for (int i = 0; i < chapters.Count && i < 255; i++)
            {
                ChapterInfo chapter = chapters[i];
                string element = "ch" + i.ToString(CultureInfo.InvariantCulture);
                MediaTime end = chapter.End ?? (i + 1 < chapters.Count ? chapters[i + 1].Start : duration ?? chapter.Start);
                List<byte> body = [.. Encoding.ASCII.GetBytes(element), 0];
                BinaryPrimitives.WriteUInt32BigEndian(times, Milliseconds(chapter.Start));
                BinaryPrimitives.WriteUInt32BigEndian(times[4..], Milliseconds(end));
                BinaryPrimitives.WriteUInt32BigEndian(times[8..], uint.MaxValue);
                BinaryPrimitives.WriteUInt32BigEndian(times[12..], uint.MaxValue);
                body.AddRange(times);
                if (chapter.Title is { Length: > 0 } title)
                {
                    using MemoryStream sub = new();
                    WriteFrame(sub, "TIT2", [3, .. Encoding.UTF8.GetBytes(title)]);
                    body.AddRange(sub.ToArray());
                }

                WriteFrame(frames, "CHAP", [.. body]);
                toc.AddRange(Encoding.ASCII.GetBytes(element));
                toc.Add(0);
            }

            WriteFrame(frames, "CTOC", [.. toc]);
        }

        if (frames.Length == 0)
        {
            return null;
        }

        byte[] tag = new byte[HeaderSize + frames.Length];
        "ID3"u8.CopyTo(tag);
        tag[3] = 4;
        WriteSyncSafe(tag.AsSpan(6), (int)frames.Length);
        frames.ToArray().CopyTo(tag, HeaderSize);
        return tag;
    }

    /// <summary>Undoes unsynchronisation: FF 00 stands for FF.</summary>
    private static byte[] Resync(ReadOnlySpan<byte> data)
    {
        byte[] clean = new byte[data.Length];
        int n = 0;
        for (int i = 0; i < data.Length; i++)
        {
            clean[n++] = data[i];
            if (data[i] == 0xFF && i + 1 < data.Length && data[i + 1] == 0)
            {
                i++;
            }
        }

        return clean.AsSpan(0, n).ToArray();
    }

    private static void ReadFrames(ReadOnlySpan<byte> body, int p, int version, bool unsynchronised, Dictionary<string, string> tags, List<ChapterInfo>? chapters, List<AttachmentInfo>? attachments)
    {
        int idLength = version == 2 ? 3 : 4;
        int headerLength = version == 2 ? 6 : 10;
        while (p >= 0 && p <= body.Length - headerLength && body[p] != 0)
        {
            string id = Encoding.ASCII.GetString(body.Slice(p, idLength));
            int frameSize = version switch
            {
                2 => (body[p + 3] << 16) | (body[p + 4] << 8) | body[p + 5],
                3 => (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(body[(p + 4)..]), int.MaxValue),
                _ => SyncSafe(body[(p + 4)..]),
            };
            if (frameSize < 0 || frameSize > body.Length - p - headerLength)
            {
                break;
            }

            ReadOnlySpan<byte> frame = body.Slice(p + headerLength, frameSize);
            p += headerLength + frameSize;
            byte[]? decoded = null;
            if (version >= 3)
            {
                byte format = body[p - frameSize - 1];
                if (!FrameData(ref frame, ref decoded, version, format, unsynchronised))
                {
                    continue;
                }
            }

            ReadFrame(id, frame, version, tags, chapters, attachments);
        }
    }

    /// <summary>Strips a version 2.3 or 2.4 frame's grouping byte, data length, unsynchronisation and zlib compression; false when it is encrypted or broken.</summary>
    private static bool FrameData(ref ReadOnlySpan<byte> frame, ref byte[]? decoded, int version, byte format, bool unsynchronised)
    {
        bool compressed = version == 3 ? (format & 0x80) != 0 : (format & 0x08) != 0;
        bool encrypted = version == 3 ? (format & 0x40) != 0 : (format & 0x04) != 0;
        bool grouped = version == 3 ? (format & 0x20) != 0 : (format & 0x40) != 0;
        bool lengthGiven = version == 3 ? compressed : (format & 0x01) != 0;
        if (encrypted)
        {
            return false;
        }

        int skip = (grouped ? 1 : 0) + (lengthGiven ? 4 : 0);
        if (frame.Length < skip)
        {
            return false;
        }

        frame = frame[skip..];
        if (version == 4 && (unsynchronised || (format & 0x02) != 0))
        {
            decoded = Resync(frame);
            frame = decoded;
        }

        if (compressed)
        {
            try
            {
                using ZLibStream zlib = new(new MemoryStream(frame.ToArray()), CompressionMode.Decompress);
                using MemoryStream inflated = new();
                zlib.CopyTo(inflated);
                decoded = inflated.ToArray();
                frame = decoded;
            }
            catch (Exception e) when (e is InvalidDataException or IOException)
            {
                // A damaged stream (ZLibException is an IOException).
                return false;
            }
        }

        return true;
    }

    private static void ReadFrame(string id, ReadOnlySpan<byte> frame, int version, Dictionary<string, string> tags, List<ChapterInfo>? chapters, List<AttachmentInfo>? attachments)
    {
        if (frame.Length < 1)
        {
            return;
        }

        switch (id)
        {
            case "COMM" or "COM":
                if (frame.Length >= 5)
                {
                    ReadOnlySpan<byte> rest = frame[4..];
                    int skip = Terminator(rest, frame[0]);
                    if (skip >= 0)
                    {
                        Add(tags, "COMMENT", Text(frame[0], rest[skip..]));
                    }
                }

                break;
            case "TXXX" or "TXX":
                {
                    int skip = Terminator(frame[1..], frame[0]);
                    if (skip > 0)
                    {
                        string name = Text(frame[0], frame.Slice(1, skip));
                        if (name.Length > 0)
                        {
                            Add(tags, name.ToUpperInvariant().Replace(' ', '_'), Text(frame[0], frame[(1 + skip)..]));
                        }
                    }

                    break;
                }

            case "APIC" or "PIC" when attachments is not null:
                ReadPicture(frame, id == "PIC", attachments);
                break;
            case "CHAP" when chapters is not null:
                ReadChapter(frame, version, chapters);
                break;
            default:
                if (TextFrames.TryGetValue(id, out string? tagName))
                {
                    string text = Text(frame[0], frame[1..]);
                    Add(tags, tagName, tagName == "GENRE" ? Genre(text) : text);
                }

                break;
        }
    }

    private static void ReadPicture(ReadOnlySpan<byte> frame, bool v22, List<AttachmentInfo> attachments)
    {
        byte encoding = frame[0];
        int p = 1;
        string mime;
        if (v22)
        {
            if (frame.Length < 5)
            {
                return;
            }

            mime = Encoding.ASCII.GetString(frame.Slice(1, 3)).ToUpperInvariant() switch
            {
                "PNG" => "image/png",
                "GIF" => "image/gif",
                _ => "image/jpeg",
            };
            p = 4;
        }
        else
        {
            int end = frame[1..].IndexOf((byte)0);
            if (end < 0)
            {
                return;
            }

            mime = Encoding.Latin1.GetString(frame.Slice(1, end)).Trim().ToLowerInvariant();
            p = 1 + end + 1;
            if (!mime.Contains('/', StringComparison.Ordinal))
            {
                mime = "image/" + (mime == "jpg" ? "jpeg" : mime.Length > 0 ? mime : "jpeg");
            }
        }

        if (p >= frame.Length)
        {
            return;
        }

        byte type = frame[p++];
        int skip = Terminator(frame[p..], encoding);
        if (skip < 0)
        {
            return;
        }

        string description = Text(encoding, frame.Slice(p, skip));
        byte[] data = frame[(p + skip)..].ToArray();
        if (mime == "image/jpg")
        {
            mime = "image/jpeg";
        }

        if (data.Length > 0 && mime != "-->")
        {
            attachments.Add(Picture(attachments, type, mime, data, description.Length > 0 ? description : null));
        }
    }

    /// <summary>Names a picture as FLAC does: the first front cover is cover.*, others picture-index-type.*.</summary>
    private static AttachmentInfo Picture(List<AttachmentInfo> attachments, int type, string mime, byte[] data, string? description)
    {
        string extension = mime switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/bmp" => ".bmp",
            _ => ".bin",
        };
        string name = type == 3 && !attachments.Any(a => a.IsCoverArt) ? "cover" + extension : $"picture-{attachments.Count}-{type}{extension}";
        return new AttachmentInfo(name, mime, data, description);
    }

    private static void ReadChapter(ReadOnlySpan<byte> frame, int version, List<ChapterInfo> chapters)
    {
        int end = frame.IndexOf((byte)0);
        if (end < 0 || frame.Length < end + 1 + 16)
        {
            return;
        }

        ReadOnlySpan<byte> times = frame.Slice(end + 1, 16);
        Rational ms = new(1, 1000);
        uint start = BinaryPrimitives.ReadUInt32BigEndian(times);
        uint stop = BinaryPrimitives.ReadUInt32BigEndian(times[4..]);
        Dictionary<string, string> sub = [];
        ReadFrames(frame, end + 1 + 16, version, false, sub, null, null);
        chapters.Add(new ChapterInfo(new MediaTime(start, ms), stop >= start ? new MediaTime(stop, ms) : null, sub.GetValueOrDefault("TITLE")));
    }

    /// <summary>Turns ID3v1 genre references ("(17)", "17", "(17)Rock") into names.</summary>
    private static string Genre(string text)
    {
        if (text.StartsWith('(') && text.IndexOf(')', StringComparison.Ordinal) is > 1 and int close
            && int.TryParse(text.AsSpan(1, close - 1), NumberStyles.None, CultureInfo.InvariantCulture, out int index))
        {
            string rest = text[(close + 1)..].Trim();
            return rest.Length > 0 ? rest : index < Genres.Length ? Genres[index] : text;
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n < Genres.Length ? Genres[n] : text;
    }

    private static string? ImageType(ReadOnlySpan<byte> data) =>
        data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8]) ? "image/jpeg"
        : data.Length >= 4 && data[0] == 0x89 && data[1..4].SequenceEqual("PNG"u8) ? "image/png"
        : data.StartsWith("GIF8"u8) ? "image/gif"
        : null;

    private static void Add(Dictionary<string, string> tags, string name, string text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            tags.TryAdd(name, text);
        }
    }

    private static string V1Text(ReadOnlySpan<byte> field)
    {
        int nul = field.IndexOf((byte)0);
        return Encoding.Latin1.GetString(nul < 0 ? field : field[..nul]).Trim();
    }

    private static int SyncSafe(ReadOnlySpan<byte> b) => (b[0] & 0x7F) << 21 | (b[1] & 0x7F) << 14 | (b[2] & 0x7F) << 7 | (b[3] & 0x7F);

    private static void WriteSyncSafe(Span<byte> b, int value)
    {
        b[0] = (byte)((value >> 21) & 0x7F);
        b[1] = (byte)((value >> 14) & 0x7F);
        b[2] = (byte)((value >> 7) & 0x7F);
        b[3] = (byte)(value & 0x7F);
    }

    private static void WriteFrame(Stream s, string id, ReadOnlySpan<byte> body)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        Encoding.ASCII.GetBytes(id, header);
        WriteSyncSafe(header[4..], body.Length);
        header[8] = header[9] = 0;
        s.Write(header);
        s.Write(body);
    }

    private static uint Milliseconds(MediaTime time) => (uint)Math.Clamp(time.Rescale(new Rational(1, 1000)).Ticks, 0, uint.MaxValue - 1);

    /// <summary>Returns the offset after a description's terminator, or -1.</summary>
    private static int Terminator(ReadOnlySpan<byte> data, byte encoding)
    {
        if (encoding is 1 or 2)
        {
            for (int i = 0; i + 1 < data.Length; i += 2)
            {
                if (data[i] == 0 && data[i + 1] == 0)
                {
                    return i + 2;
                }
            }

            return -1;
        }

        int end = data.IndexOf((byte)0);
        return end < 0 ? -1 : end + 1;
    }

    private static string Text(byte encoding, ReadOnlySpan<byte> data)
    {
        string text = encoding switch
        {
            1 => data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF ? Encoding.BigEndianUnicode.GetString(data[2..]) : Encoding.Unicode.GetString(data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE ? data[2..] : data),
            2 => Encoding.BigEndianUnicode.GetString(data),
            3 => Encoding.UTF8.GetString(data),
            _ => Encoding.Latin1.GetString(data),
        };

        // Version 2.4 separates several values with NUL; keep the first.
        int nul = text.IndexOf('\0', StringComparison.Ordinal);
        return (nul < 0 ? text : text[..nul]).Trim();
    }
}
