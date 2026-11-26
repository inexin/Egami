using Egami.Fonts.Parsing;

namespace Egami.Fonts.Woff;

/// <summary>One table of a font being written as a web font: its tag, its bytes and the checksum its directory gives.</summary>
internal sealed record SourceTable(FontTag Tag, ReadOnlyMemory<byte> Data, uint Checksum);

/// <summary>
/// The fonts <see cref="WebFontWriter"/> writes: one face, or a collection's faces, each a flavor and its tables by index into
/// <see cref="Tables"/>, where a table the collection shares appears once.
/// </summary>
internal sealed class WebFontSource
{
    private WebFontSource(List<SourceTable> tables, List<SfntFace> faces, uint collectionVersion)
    {
        Tables = tables;
        Faces = faces;
        CollectionVersion = collectionVersion;
    }

    public List<SourceTable> Tables { get; }

    public List<SfntFace> Faces { get; }

    /// <summary>Gets the TTC header version, or 0 for a single font.</summary>
    public uint CollectionVersion { get; }

    /// <summary>Gets the metadata block of the web font the data came from (UTF-8 XML), or null.</summary>
    public byte[]? Metadata { get; private init; }

    /// <summary>Gets the private data block of the web font the data came from, or null.</summary>
    public ReadOnlyMemory<byte>? PrivateData { get; private init; }

    public static WebFontSource FromFace(Typeface face, long limit)
    {
        var tables = new List<SourceTable>(face.Tables.Count);
        var list = new (FontTag, int)[face.Tables.Count];
        long total = 12 + (16L * face.Tables.Count);
        int i = 0;
        foreach (TableRecord record in face.Tables)
        {
            total += WebFontDecoder.Pad4(record.Length);
            list[i] = (record.Tag, i);
            tables.Add(new SourceTable(record.Tag, face.FileData.Slice(record.Offset, record.Length), record.Checksum));
            i++;
        }

        WebFontDecoder.CheckLimit(total, limit);
        string? metadata = face.GetWoffMetadata();
        ReadOnlyMemory<byte> priv = face.GetWoffPrivateData();
        return new WebFontSource(tables, [new SfntFace(face.SfntVersion, list)], 0)
        {
            Metadata = metadata is null ? null : System.Text.Encoding.UTF8.GetBytes(metadata),
            PrivateData = priv.IsEmpty ? null : priv,
        };
    }

    public static WebFontSource FromData(ReadOnlyMemory<byte> data, EgamiContext context)
    {
        long limit = context.Limits.MaxTotalBytes;
        WebFontDecoder.CheckLimit(data.Length, limit);
        FontProbe probe = SfntDirectory.Probe(data.Span);
        switch (probe.Kind)
        {
            case FontFileKind.Woff:
            case FontFileKind.Woff2:
            case FontFileKind.Eot:
                DecodedFont decoded = WebFontDecoder.Decode(data, new FontLoadOptions { Context = context });
                WebFontSource inner = FromSfnt(decoded.Data, context, limit);
                return new WebFontSource(inner.Tables, inner.Faces, inner.CollectionVersion)
                {
                    Metadata = decoded.Metadata,
                    PrivateData = decoded.PrivateData,
                };
            case FontFileKind.Sfnt:
            case FontFileKind.Collection:
                return FromSfnt(data, context, limit);
            case FontFileKind.Cff:
                throw new ArgumentException("A bare CFF stream has no sfnt tables to write as a web font.", nameof(data));
            default:
                throw new ArgumentException("The data is not a TrueType or OpenType font, collection or web font.", nameof(data));
        }
    }

    private static WebFontSource FromSfnt(ReadOnlyMemory<byte> data, EgamiContext context, long limit)
    {
        ReadOnlySpan<byte> span = data.Span;
        bool collection = BigEndian.UInt32(span, 0) == SfntDirectory.CollectionTag;
        uint collectionVersion = 0;
        if (collection)
        {
            if (span.Length < 12)
            {
                throw FontErrors.Corrupt(0, "The collection header is cut short.");
            }

            collectionVersion = BigEndian.UInt32(span, 4);
            collectionVersion = collectionVersion >= 0x00020000 ? 0x00020000u : 0x00010000u;
        }

        SfntDirectory.FaceOffset(span, 0, out int faceCount);
        if (faceCount == 0)
        {
            throw FontErrors.Corrupt(8, "The collection has no fonts.");
        }

        if (faceCount > 0xFFFF)
        {
            throw FontErrors.Corrupt(8, $"The collection lists {faceCount} fonts; WOFF2 holds at most 65535.");
        }

        var tables = new List<SourceTable>();
        var shared = new Dictionary<(uint Tag, int Offset, int Length), int>();
        var faces = new List<SfntFace>(faceCount);
        long total = 0;
        for (int f = 0; f < faceCount; f++)
        {
            int faceOffset = SfntDirectory.FaceOffset(span, f, out _);
            TableRecord[] records = SfntDirectory.Read(span, faceOffset, out uint version, problem => WebFontEncoder.Report(context, "Font.BadDirectory", problem, faceOffset));
            var list = new List<(FontTag, int)>(records.Length);
            var seen = new HashSet<uint>();
            total += 12 + (16L * records.Length);
            foreach (TableRecord record in records)
            {
                if (!seen.Add(record.Tag.Value))
                {
                    WebFontEncoder.Report(context, "Font.BadDirectory", $"The table directory lists {record.Tag} twice; the second is left out.", faceOffset);
                    continue;
                }

                var key = (record.Tag.Value, record.Offset, record.Length);
                if (!shared.TryGetValue(key, out int index))
                {
                    index = tables.Count;
                    shared.Add(key, index);
                    tables.Add(new SourceTable(record.Tag, data.Slice(record.Offset, record.Length), record.Checksum));
                    total += WebFontDecoder.Pad4(record.Length);
                    WebFontDecoder.CheckLimit(total, limit);
                }

                list.Add((record.Tag, index));
            }

            faces.Add(new SfntFace(version, [.. list]));
        }

        return new WebFontSource(tables, faces, collectionVersion);
    }
}
