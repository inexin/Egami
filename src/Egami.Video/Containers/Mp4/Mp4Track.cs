using System.Buffers.Binary;
using System.Text;

using Egami.Formats.Iso;

namespace Egami.Video.Containers.Mp4;

/// <summary>An edit list entry (elst): how long the segment shows, in the movie timescale, and where it starts in the media (-1 for an empty edit).</summary>
/// <param name="Duration">The segment's duration in the movie timescale.</param>
/// <param name="MediaTime">Where it starts in the media timescale, or -1 for an empty edit.</param>
/// <param name="Rate">The playback rate, 16.16 fixed point.</param>
internal readonly record struct Mp4Edit(long Duration, long MediaTime, int Rate);

/// <summary>A sample description (an stsd entry): its type and the whole box as the file stores it.</summary>
/// <param name="Type">The coding name, such as av01 or mp4a.</param>
/// <param name="Box">The entry, header included.</param>
internal sealed record Mp4SampleEntry(FourCC Type, byte[] Box);

/// <summary>
/// A track read from a trak box (ISO/IEC 14496-12 8.3 to 8.7, and the QuickTime File Format): the header fields the demuxer and
/// track info need, the sample descriptions, the sample tables, the edit list and the track references.
/// </summary>
internal sealed class Mp4Track
{
    public static readonly FourCC Vide = FourCC.Parse("vide");
    public static readonly FourCC Soun = FourCC.Parse("soun");
    public static readonly FourCC Subt = FourCC.Parse("subt");
    public static readonly FourCC Text = FourCC.Parse("text");
    public static readonly FourCC Sbtl = FourCC.Parse("sbtl");
    public static readonly FourCC Tmcd = FourCC.Parse("tmcd");

    public uint Id { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the tkhd matrix: a, b, u, c, d, v, x, y, w (a to d and x, y as 16.16; u, v, w as 2.30).</summary>
    public int[] Matrix { get; set; } = [0x10000, 0, 0, 0, 0x10000, 0, 0, 0, 0x40000000];

    /// <summary>Gets or sets the presentation size from tkhd, 16.16.</summary>
    public (uint Width, uint Height) Size { get; set; }

    public short AlternateGroup { get; set; }

    public FourCC Handler { get; set; }

    public string? HandlerName { get; set; }

    public uint Timescale { get; set; }

    public long MediaDuration { get; set; }

    public string? Language { get; set; }

    /// <summary>Gets or sets the track's name from its udta (name box or ©nam), if any.</summary>
    public string? Name { get; set; }

    public List<Mp4SampleEntry> Entries { get; } = [];

    public Mp4SampleTable Table { get; } = new();

    public List<Mp4Edit> Edits { get; } = [];

    public Dictionary<FourCC, uint[]> References { get; } = [];

    /// <summary>Gets or sets the trex defaults for fragments: description, duration, size and flags.</summary>
    public (uint Description, uint Duration, uint Size, uint Flags) FragmentDefaults { get; set; } = (1, 0, 0, 0);

    /// <summary>Reads a trak box.</summary>
    public static Mp4Track Read(IsoBoxReader parent, IsoBox trak, bool quickTime, MediaLimits limits)
    {
        Mp4Track track = new();
        IsoBoxReader children = parent.Children(trak);
        while (children.Next(out IsoBox box))
        {
            switch (box.Type.ToString())
            {
                case "tkhd":
                    track.ReadTkhd(new BoxFields(children.Content(box), box));
                    break;
                case "mdia":
                    track.ReadMdia(children, box, quickTime, limits);
                    break;
                case "edts":
                    IsoBoxReader edts = children.Children(box);
                    while (edts.Next(out IsoBox elst))
                    {
                        if (elst.Type.ToString() == "elst")
                        {
                            track.ReadElst(new BoxFields(edts.Content(elst), elst), limits);
                        }
                    }

                    break;
                case "tref":
                    IsoBoxReader tref = children.Children(box);
                    while (tref.Next(out IsoBox reference))
                    {
                        ReadOnlySpan<byte> r = tref.Content(reference);
                        uint[] ids = new uint[r.Length / 4];
                        for (int i = 0; i < ids.Length; i++)
                        {
                            ids[i] = BinaryPrimitives.ReadUInt32BigEndian(r[(4 * i)..]);
                        }

                        track.References[reference.Type] = ids;
                    }

                    break;
                case "udta":
                    track.Name = Mp4Metadata.ReadTrackName(children, box) ?? track.Name;
                    break;
            }
        }

        return track;
    }

    private void ReadTkhd(BoxFields f)
    {
        (int version, uint flags) = f.FullBox();
        Enabled = (flags & 1) != 0;
        f.UVar(version == 1); // creation time
        f.UVar(version == 1); // modification time
        Id = f.U32();
        f.Skip(4);
        f.UVar(version == 1); // duration
        f.Skip(8);
        f.Skip(2); // layer
        AlternateGroup = f.I16();
        f.Skip(4); // volume, reserved
        int[] matrix = new int[9];
        for (int i = 0; i < 9; i++)
        {
            matrix[i] = f.I32();
        }

        Matrix = matrix;
        Size = (f.U32(), f.U32());
    }

    private void ReadMdia(IsoBoxReader parent, IsoBox mdia, bool quickTime, MediaLimits limits)
    {
        IsoBoxReader children = parent.Children(mdia);
        while (children.Next(out IsoBox box))
        {
            switch (box.Type.ToString())
            {
                case "mdhd":
                    BoxFields f = new(children.Content(box), box);
                    (int version, _) = f.FullBox();
                    f.UVar(version == 1);
                    f.UVar(version == 1);
                    Timescale = f.U32();
                    MediaDuration = f.UVar(version == 1);
                    Language = LanguageOf(f.U16());
                    break;
                case "hdlr":
                    ReadHdlr(new BoxFields(children.Content(box), box), quickTime);
                    break;
                case "minf":
                    IsoBoxReader minf = children.Children(box);
                    while (minf.Next(out IsoBox stbl))
                    {
                        if (stbl.Type.ToString() == "stbl")
                        {
                            ReadStbl(minf, stbl, limits);
                        }
                    }

                    break;
            }
        }
    }

    private void ReadHdlr(BoxFields f, bool quickTime)
    {
        f.FullBox();
        uint componentType = f.U32();
        FourCC handler = new(f.U32());

        // QuickTime has a 'mhlr' media handler and a 'dhlr' data handler in different boxes; only the media one counts.
        if (componentType == FourCC.Parse("dhlr").Value)
        {
            return;
        }

        Handler = handler;
        f.Skip(Math.Min(12, f.Remaining));
        if (f.Remaining > 0)
        {
            // ISO writes a NUL-terminated UTF-8 name; QuickTime a Pascal string (a length byte first), which some MP4 writers copy.
            ReadOnlySpan<byte> rest = f.Rest;
            string name = quickTime || (rest[0] == rest.Length - 1 && rest[0] > 0)
                ? Encoding.UTF8.GetString(rest.Slice(1, Math.Min(rest[0], rest.Length - 1)))
                : f.CString();
            name = name.TrimEnd('\0');
            HandlerName = name.Length > 0 ? name : null;
        }
    }

    private void ReadElst(BoxFields f, MediaLimits limits)
    {
        (int version, _) = f.FullBox();
        uint count = f.U32();
        f.Need(count, version == 1 ? 20 : 12);
        if (count > limits.MaxIndexEntries)
        {
            throw new LimitExceededException("MaxIndexEntries", limits.MaxIndexEntries, count);
        }

        for (uint i = 0; i < count; i++)
        {
            long duration = f.UVar(version == 1);
            long mediaTime = version == 1 ? (long)f.U64() : f.I32();
            Edits.Add(new Mp4Edit(duration, mediaTime, f.I32()));
        }
    }

    private void ReadStbl(IsoBoxReader parent, IsoBox stbl, MediaLimits limits)
    {
        Mp4SampleTable t = Table;
        IsoBoxReader children = parent.Children(stbl);
        long sizeCount = -1;
        while (children.Next(out IsoBox box))
        {
            BoxFields f = new(children.Content(box), box);
            switch (box.Type.ToString())
            {
                case "stsd":
                    f.FullBox();
                    uint entries = f.U32();
                    IsoBoxReader list = children.Children(box, 8);
                    for (uint i = 0; i < entries && list.Next(out IsoBox entry); i++)
                    {
                        byte[] bytes = new byte[entry.Size];
                        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)Math.Min(entry.Size, uint.MaxValue));
                        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), entry.Type.Value);
                        int header = (int)(entry.ContentStart - entry.Start);
                        if (header != 8)
                        {
                            // A large size or uuid entry is stored with a plain 8-byte header and its extended type kept.
                            bytes = new byte[8 + entry.ContentLength];
                            BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)bytes.Length);
                            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), entry.Type.Value);
                        }

                        list.Content(entry).CopyTo(bytes.AsSpan(8));
                        Entries.Add(new Mp4SampleEntry(entry.Type, bytes));
                    }

                    break;
                case "stts":
                    f.FullBox();
                    t.Times = new (uint, uint)[CountOf(ref f, 8, limits)];
                    for (int i = 0; i < t.Times.Length; i++)
                    {
                        t.Times[i] = (f.U32(), f.U32());
                    }

                    break;
                case "ctts":
                    f.FullBox();
                    t.Compositions = new (uint, int)[CountOf(ref f, 8, limits)];
                    for (int i = 0; i < t.Compositions.Length; i++)
                    {
                        // Version 0 offsets are unsigned, but writers store negative ones there too; both are read as signed, as players do.
                        t.Compositions[i] = (f.U32(), f.I32());
                    }

                    break;
                case "stss":
                    f.FullBox();
                    uint[] sync = new uint[CountOf(ref f, 4, limits)];
                    for (int i = 0; i < sync.Length; i++)
                    {
                        sync[i] = f.U32();
                    }

                    // Sorted for binary search; writers store them ascending already.
                    Array.Sort(sync);
                    t.SyncSamples = sync;
                    break;
                case "stsc":
                    f.FullBox();
                    t.Chunks = new (uint, uint, uint)[CountOf(ref f, 12, limits)];
                    for (int i = 0; i < t.Chunks.Length; i++)
                    {
                        t.Chunks[i] = (f.U32(), f.U32(), f.U32());
                    }

                    break;
                case "stsz":
                    f.FullBox();
                    uint size = f.U32();
                    uint samples = f.U32();
                    Limit(samples, limits);
                    sizeCount = samples;
                    t.ConstantSize = (int)Math.Min(size, int.MaxValue);
                    if (size == 0)
                    {
                        f.Need(samples, 4);
                        t.Sizes = new int[samples];
                        for (int i = 0; i < t.Sizes.Length; i++)
                        {
                            t.Sizes[i] = (int)Math.Min(f.U32(), int.MaxValue);
                        }
                    }

                    break;
                case "stz2":
                    f.FullBox();
                    f.Skip(3);
                    int fieldSize = f.U8();
                    uint compact = f.U32();
                    Limit(compact, limits);
                    if (fieldSize is not (4 or 8 or 16))
                    {
                        throw new CorruptDataException(Mp4Format.Instance.Name, box.Start, $"The stz2 field size is {fieldSize}; it must be 4, 8 or 16.");
                    }

                    f.Need(((compact * fieldSize) + 7) / 8, 1);
                    sizeCount = compact;
                    t.Sizes = new int[compact];
                    for (int i = 0; i < t.Sizes.Length; i++)
                    {
                        t.Sizes[i] = fieldSize switch
                        {
                            16 => f.U16(),
                            8 => f.U8(),
                            _ => (i & 1) == 0 ? f.Rest[0] >> 4 : f.U8() & 15,
                        };
                    }

                    break;
                case "stco":
                case "co64":
                    f.FullBox();
                    bool wide = box.Type.ToString() == "co64";
                    t.ChunkOffsets = new long[CountOf(ref f, wide ? 8 : 4, limits)];
                    for (int i = 0; i < t.ChunkOffsets.Length; i++)
                    {
                        t.ChunkOffsets[i] = wide ? (long)Math.Min(f.U64(), long.MaxValue) : f.U32();
                    }

                    break;
                case "sdtp":
                    f.FullBox();
                    t.Dependencies = f.Rest.ToArray();
                    break;
            }
        }

        t.Count = Math.Max(0, sizeCount);
    }

    private static int CountOf(ref BoxFields f, int size, MediaLimits limits)
    {
        uint count = f.U32();
        Limit(count, limits);
        f.Need(count, size);
        return (int)count;
    }

    private static void Limit(long count, MediaLimits limits)
    {
        if (count > limits.MaxIndexEntries || count > int.MaxValue)
        {
            throw new LimitExceededException("MaxIndexEntries", Math.Min(limits.MaxIndexEntries, int.MaxValue), count);
        }
    }

    /// <summary>
    /// Reads mdhd's language: three letters of ISO 639-2/T packed five bits each (MP4), or below 0x400 a Macintosh language code
    /// (QuickTime), of which 0 is English. Returns <see langword="null"/> for "und".
    /// </summary>
    internal static string? LanguageOf(ushort code)
    {
        if (code < 0x400)
        {
            return code == 0 ? "eng" : null;
        }

        Span<char> letters = [(char)(((code >> 10) & 31) + 0x60), (char)(((code >> 5) & 31) + 0x60), (char)((code & 31) + 0x60)];
        foreach (char c in letters)
        {
            if (c is < 'a' or > 'z')
            {
                return null;
            }
        }

        string language = new(letters);
        return language == "und" ? null : language;
    }
}
