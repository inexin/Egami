using System.Buffers.Binary;

namespace Egami.Formats.Heif;

/// <summary>One sample of a track: where its bytes are in the file, how long it shows (in the track's timescale), and whether it is a sync sample.</summary>
/// <param name="Offset">The sample's offset in the file.</param>
/// <param name="Size">The sample's size in bytes.</param>
/// <param name="Duration">How long the sample shows, in timescale units.</param>
/// <param name="Sync">Whether the sample is a sync sample (a key frame).</param>
internal readonly record struct HeifSample(long Offset, int Size, long Duration, bool Sync);

/// <summary>
/// A track of an image sequence (ISO/IEC 14496-12, as HEIF 23008-12 clause 7 uses it): its handler, size, timescale, the first
/// sample entry's coding name and child boxes (av1C or hvcC, colr, auxi), the track it describes (an auxl reference, as an alpha
/// track gives), and its samples. Codec-neutral, so AVIF and HEIC sequences share it.
/// </summary>
internal sealed class HeifTrack
{
    /// <summary>Gets the track_ID.</summary>
    internal uint Id { get; init; }

    /// <summary>Gets the handler type: pict, vide or auxv.</summary>
    internal uint Handler { get; init; }

    /// <summary>Gets the width the track header gives.</summary>
    internal int Width { get; init; }

    /// <summary>Gets the height the track header gives.</summary>
    internal int Height { get; init; }

    /// <summary>Gets the media timescale: units a second.</summary>
    internal uint Timescale { get; init; }

    /// <summary>Gets the first sample entry's type, such as av01.</summary>
    internal uint SampleEntry { get; set; }

    /// <summary>Gets the boxes inside the first sample entry (av1C, colr, pasp, auxi, ...).</summary>
    internal List<IsoBox> EntryBoxes { get; } = [];

    /// <summary>Gets the tracks this one references by type (auxl for an alpha track, prem, ...).</summary>
    internal Dictionary<uint, uint[]> References { get; } = [];

    /// <summary>Gets the samples in decoding order.</summary>
    internal List<HeifSample> Samples { get; } = [];

    /// <summary>Gets whether the edit list repeats the track (an infinite loop).</summary>
    internal bool Repeats { get; init; }
}

/// <summary>Reads the tracks of a moov box.</summary>
internal static class HeifTracks
{
    private static readonly uint Trak = FourCC.Of("trak");
    private static readonly uint Tkhd = FourCC.Of("tkhd");
    private static readonly uint Tref = FourCC.Of("tref");
    private static readonly uint Edts = FourCC.Of("edts");
    private static readonly uint Elst = FourCC.Of("elst");
    private static readonly uint Mdia = FourCC.Of("mdia");
    private static readonly uint Mdhd = FourCC.Of("mdhd");
    private static readonly uint Minf = FourCC.Of("minf");
    private static readonly uint Stbl = FourCC.Of("stbl");
    private static readonly uint Stsd = FourCC.Of("stsd");
    private static readonly uint Stts = FourCC.Of("stts");
    private static readonly uint Stss = FourCC.Of("stss");
    private static readonly uint Stsc = FourCC.Of("stsc");
    private static readonly uint Stsz = FourCC.Of("stsz");
    private static readonly uint Stco = FourCC.Of("stco");
    private static readonly uint Co64 = FourCC.Of("co64");
    private static readonly uint HdlrType = FourCC.Of("hdlr");

    /// <summary>Reads every track of the file's moov box, or none when it has no moov box.</summary>
    internal static List<HeifTrack> Read(HeifFile file)
    {
        List<HeifTrack> tracks = [];
        ReadOnlySpan<byte> bytes = file.Bytes.Span;
        foreach (IsoBox moov in file.Boxes)
        {
            if (moov.Type != FourCC.Of("moov"))
            {
                continue;
            }

            IsoBoxReader reader = new(bytes, moov.ContentStart, moov.End, file.FormatName);
            while (reader.Next(out IsoBox trak))
            {
                if (trak.Type == Trak)
                {
                    tracks.Add(ReadTrack(file, trak));
                }
            }

            break;
        }

        return tracks;
    }

    private static HeifTrack ReadTrack(HeifFile file, IsoBox trak)
    {
        ReadOnlySpan<byte> bytes = file.Bytes.Span;
        uint id = 0, handler = 0, timescale = 0;
        int width = 0, height = 0;
        bool repeats = false;
        IsoBox? stbl = null;
        Dictionary<uint, uint[]> references = [];
        IsoBoxReader children = new(bytes, trak.ContentStart, trak.End, file.FormatName);
        while (children.Next(out IsoBox box))
        {
            ReadOnlySpan<byte> c = box.Content(bytes);
            if (box.Type == Tkhd)
            {
                // version and flags, then times (32 or 64 bits) before track_ID; width and height (16.16) are the last 8 bytes.
                int at = c.Length > 0 && c[0] == 1 ? 4 + 16 : 4 + 8;
                Need(file, box, c, at + 4);
                id = BinaryPrimitives.ReadUInt32BigEndian(c[at..]);
                if (c.Length >= 8)
                {
                    width = (int)(BinaryPrimitives.ReadUInt32BigEndian(c[^8..]) >> 16);
                    height = (int)(BinaryPrimitives.ReadUInt32BigEndian(c[^4..]) >> 16);
                }
            }
            else if (box.Type == Tref)
            {
                IsoBoxReader types = new(bytes, box.ContentStart, box.End, file.FormatName);
                while (types.Next(out IsoBox reference))
                {
                    ReadOnlySpan<byte> r = reference.Content(bytes);
                    uint[] ids = new uint[r.Length / 4];
                    for (int i = 0; i < ids.Length; i++)
                    {
                        ids[i] = BinaryPrimitives.ReadUInt32BigEndian(r[(4 * i)..]);
                    }

                    references[reference.Type] = ids;
                }
            }
            else if (box.Type == Edts)
            {
                IsoBoxReader edits = new(bytes, box.ContentStart, box.End, file.FormatName);
                while (edits.Next(out IsoBox elst))
                {
                    ReadOnlySpan<byte> e = elst.Content(bytes);
                    // Bit 0 of the edit list's flags repeats it.
                    repeats |= elst.Type == Elst && e.Length >= 4 && (e[3] & 1) != 0;
                }
            }
            else if (box.Type == Mdia)
            {
                IsoBoxReader media = new(bytes, box.ContentStart, box.End, file.FormatName);
                while (media.Next(out IsoBox m))
                {
                    ReadOnlySpan<byte> mc = m.Content(bytes);
                    if (m.Type == Mdhd)
                    {
                        int at = mc.Length > 0 && mc[0] == 1 ? 4 + 16 : 4 + 8;
                        Need(file, m, mc, at + 4);
                        timescale = BinaryPrimitives.ReadUInt32BigEndian(mc[at..]);
                    }
                    else if (m.Type == HdlrType)
                    {
                        Need(file, m, mc, 12);
                        handler = BinaryPrimitives.ReadUInt32BigEndian(mc[8..]);
                    }
                    else if (m.Type == Minf)
                    {
                        IsoBoxReader info = new(bytes, m.ContentStart, m.End, file.FormatName);
                        while (info.Next(out IsoBox s))
                        {
                            stbl = s.Type == Stbl ? s : stbl;
                        }
                    }
                }
            }
        }

        HeifTrack track = new() { Id = id, Handler = handler, Width = width, Height = height, Timescale = timescale, Repeats = repeats };
        foreach ((uint type, uint[] ids) in references)
        {
            track.References[type] = ids;
        }

        if (stbl is IsoBox table)
        {
            track.SampleEntry = ReadSampleTable(file, table, track);
        }

        return track;
    }

    /// <summary>Reads stsd's first entry and the sample tables into <paramref name="track"/>; returns the entry's type.</summary>
    private static uint ReadSampleTable(HeifFile file, IsoBox stbl, HeifTrack track)
    {
        ReadOnlySpan<byte> bytes = file.Bytes.Span;
        uint entryType = 0;
        List<(long Count, long Delta)> times = [];
        HashSet<long>? sync = null;
        List<(long FirstChunk, long Samples)> chunks = [];
        List<long> offsets = [];
        List<int> sizes = [];
        IsoBoxReader children = new(bytes, stbl.ContentStart, stbl.End, file.FormatName);
        while (children.Next(out IsoBox box))
        {
            ReadOnlySpan<byte> c = box.Content(bytes);
            if (box.Type == Stsd)
            {
                Need(file, box, c, 8);
                if (BinaryPrimitives.ReadUInt32BigEndian(c[4..]) == 0)
                {
                    continue;
                }

                // The first sample entry: a visual sample entry's 78 bytes of fields, then its boxes.
                IsoBoxReader entries = new(bytes, box.ContentStart + 8, box.End, file.FormatName);
                if (entries.Next(out IsoBox sampleEntry))
                {
                    entryType = sampleEntry.Type;
                    if (sampleEntry.ContentStart + 78 <= sampleEntry.End)
                    {
                        IsoBoxReader inner = new(bytes, sampleEntry.ContentStart + 78, sampleEntry.End, file.FormatName);
                        while (inner.Next(out IsoBox child))
                        {
                            track.EntryBoxes.Add(child);
                        }
                    }
                }
            }
            else if (box.Type == Stts)
            {
                int count = Count(file, box, c, 8);
                for (int i = 0; i < count; i++)
                {
                    times.Add((BinaryPrimitives.ReadUInt32BigEndian(c[(8 + (8 * i))..]), BinaryPrimitives.ReadUInt32BigEndian(c[(12 + (8 * i))..])));
                }
            }
            else if (box.Type == Stss)
            {
                int count = Count(file, box, c, 4);
                sync = [];
                for (int i = 0; i < count; i++)
                {
                    sync.Add(BinaryPrimitives.ReadUInt32BigEndian(c[(8 + (4 * i))..]));
                }
            }
            else if (box.Type == Stsc)
            {
                int count = Count(file, box, c, 12);
                for (int i = 0; i < count; i++)
                {
                    chunks.Add((BinaryPrimitives.ReadUInt32BigEndian(c[(8 + (12 * i))..]), BinaryPrimitives.ReadUInt32BigEndian(c[(12 + (12 * i))..])));
                }
            }
            else if (box.Type == Stsz)
            {
                Need(file, box, c, 12);
                uint size = BinaryPrimitives.ReadUInt32BigEndian(c[4..]);
                int count = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(c[8..]), int.MaxValue);
                if (size == 0)
                {
                    Need(file, box, c, 12 + (4L * count));
                }
                else
                {
                    // Samples of one size lie end to end in the file, so no more of them fit than its length allows.
                    count = (int)Math.Min(count, (file.Bytes.Length / size) + 1);
                }

                for (int i = 0; i < count; i++)
                {
                    long s = size != 0 ? size : BinaryPrimitives.ReadUInt32BigEndian(c[(12 + (4 * i))..]);
                    sizes.Add((int)Math.Min(s, int.MaxValue));
                }
            }
            else if (box.Type == Stco || box.Type == Co64)
            {
                int wide = box.Type == Co64 ? 8 : 4;
                int count = Count(file, box, c, wide);
                for (int i = 0; i < count; i++)
                {
                    offsets.Add(wide == 8 ? (long)BinaryPrimitives.ReadUInt64BigEndian(c[(8 + (8 * i))..]) : BinaryPrimitives.ReadUInt32BigEndian(c[(8 + (4 * i))..]));
                }
            }
        }

        // Samples in chunk order: stsc's runs give how many samples each chunk holds, stco where the chunk starts.
        int sample = 0, run = 0, timeRun = 0;
        long timeLeft = times.Count > 0 ? times[0].Count : 0;
        for (int chunk = 0; chunk < offsets.Count && sample < sizes.Count; chunk++)
        {
            while (run + 1 < chunks.Count && chunks[run + 1].FirstChunk <= chunk + 1)
            {
                run++;
            }

            long perChunk = chunks.Count > 0 ? chunks[run].Samples : 0;
            long at = offsets[chunk];
            for (long i = 0; i < perChunk && sample < sizes.Count; i++, sample++)
            {
                while (timeLeft == 0 && timeRun + 1 < times.Count)
                {
                    timeRun++;
                    timeLeft = times[timeRun].Count;
                }

                long duration = times.Count > 0 ? times[timeRun].Delta : 0;
                timeLeft--;
                track.Samples.Add(new HeifSample(at, sizes[sample], duration, sync is null || sync.Contains(sample + 1)));
                at += sizes[sample];
            }
        }

        return entryType;
    }

    private static int Count(HeifFile file, IsoBox box, ReadOnlySpan<byte> c, int entrySize)
    {
        Need(file, box, c, 8);
        long count = BinaryPrimitives.ReadUInt32BigEndian(c[4..]);
        Need(file, box, c, 8 + (count * entrySize));
        return (int)count;
    }

    private static void Need(HeifFile file, IsoBox box, ReadOnlySpan<byte> c, long length)
    {
        if (c.Length < length)
        {
            throw new CorruptDataException(file.FormatName, box.Start, $"A \"{FourCC.ToString(box.Type)}\" box is cut off.");
        }
    }
}
