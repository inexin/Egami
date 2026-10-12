using System.Buffers.Binary;
using System.IO.Compression;

using Egami.Fonts.Parsing;

namespace Egami.Fonts.Woff;

/// <summary>A WOFF or WOFF2 file unwrapped into an ordinary sfnt or collection, with the container's own blocks.</summary>
/// <param name="Data">The sfnt or collection bytes, which become the faces' memory.</param>
/// <param name="Container">Woff or Woff2.</param>
/// <param name="Metadata">The decompressed extended metadata block (UTF-8 XML), or <see langword="null"/>.</param>
/// <param name="PrivateData">The private data block, or <see langword="null"/>.</param>
/// <param name="FaceCount">The number of faces: 1, or the collection's count.</param>
internal sealed record DecodedFont(byte[] Data, FontFileKind Container, byte[]? Metadata, byte[]? PrivateData, int FaceCount);

/// <summary>
/// Unwraps WOFF 1.0 (W3C, zlib per table) and WOFF 2.0 (W3C, one Brotli stream, with the glyf, loca and hmtx transforms and
/// collections) into the sfnt they hold. Every length is checked against the context's <see cref="SafetyLimits.MaxTotalBytes"/>
/// before anything is allocated; a table that fails to inflate or a transform that reads past its stream throws
/// <see cref="CorruptDataException"/>.
/// </summary>
internal static class WebFontDecoder
{
    public static DecodedFont Decode(ReadOnlyMemory<byte> data, FontLoadOptions options)
    {
        uint magic = data.Length >= 4 ? BigEndian.UInt32(data.Span, 0) : 0;
        long limit = options.EffectiveContext.Limits.MaxTotalBytes;
        return magic switch
        {
            SfntDirectory.WoffTag => DecodeWoff(data, limit, options),
            SfntDirectory.Woff2Tag => Woff2Decoder.Decode(data, limit, options),
            _ => throw new UnsupportedFormatException("The data is not a WOFF or WOFF2 font."),
        };
    }

    private static DecodedFont DecodeWoff(ReadOnlyMemory<byte> memory, long limit, FontLoadOptions options)
    {
        ReadOnlySpan<byte> data = memory.Span;
        var r = new FontReader(data);
        r.Skip(4);
        uint flavor = r.ReadUInt32();
        uint length = r.ReadUInt32();
        int count = r.ReadUInt16();
        r.Skip(2);
        uint totalSfntSize = r.ReadUInt32();
        r.Skip(4);
        uint metaOffset = r.ReadUInt32(), metaLength = r.ReadUInt32(), metaOrigLength = r.ReadUInt32();
        uint privOffset = r.ReadUInt32(), privLength = r.ReadUInt32();
        if (length != data.Length)
        {
            Typeface.Report(options, "Font.BadContainer", $"The WOFF header gives a length of {length} bytes; the data has {data.Length}.", 8);
        }

        if (count == 0)
        {
            throw FontErrors.Corrupt(12, "The WOFF file has no tables.");
        }

        CheckLimit(totalSfntSize, limit);
        var tables = new SfntTable[count];
        long total = 12 + (16L * count);
        for (int i = 0; i < count; i++)
        {
            var tag = r.ReadTag();
            uint offset = r.ReadUInt32(), compLength = r.ReadUInt32(), origLength = r.ReadUInt32();
            r.Skip(4);
            if (offset > data.Length || compLength > data.Length - offset)
            {
                throw FontErrors.Corrupt(44 + (i * 20), $"The WOFF {tag} table lies outside the file.");
            }

            if (compLength > origLength)
            {
                throw FontErrors.Corrupt(44 + (i * 20), $"The WOFF {tag} table is longer compressed than its original length.");
            }

            total += Pad4(origLength);
            CheckLimit(total, limit);
            tables[i] = new SfntTable(tag, (int)offset, (int)compLength, (int)origLength);
        }

        byte[][] bodies = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            SfntTable t = tables[i];
            ReadOnlyMemory<byte> stored = memory.Slice(t.Offset, t.StoredLength);
            bodies[i] = t.StoredLength < t.Length ? Inflate(stored, t.Length, t.Offset, t.Tag) : stored.ToArray();
        }

        byte[]? metadata = null, priv = null;
        if (metaLength > 0)
        {
            if (metaOffset > data.Length || metaLength > data.Length - metaOffset)
            {
                Typeface.Report(options, "Font.BadContainer", "The WOFF metadata block lies outside the file; it is left out.", 24);
            }
            else
            {
                CheckLimit(metaOrigLength, limit);
                metadata = Inflate(memory.Slice((int)metaOffset, (int)metaLength), (int)metaOrigLength, (int)metaOffset, default);
            }
        }

        if (privLength > 0 && privOffset <= data.Length && privLength <= data.Length - privOffset)
        {
            priv = data.Slice((int)privOffset, (int)privLength).ToArray();
        }

        var face = new SfntFace(flavor, [.. Enumerable.Range(0, count).Select(i => (tables[i].Tag, i))]);
        byte[] sfnt = SfntAssembler.Build([face], bodies, collectionVersion: 0, limit);
        return new DecodedFont(sfnt, FontFileKind.Woff, metadata, priv, 1);
    }

    /// <summary>Inflates a zlib stream to exactly <paramref name="length"/> bytes.</summary>
    internal static byte[] Inflate(ReadOnlyMemory<byte> stored, int length, long offset, FontTag tag)
    {
        byte[] result = new byte[length];
        try
        {
            using var source = System.Runtime.InteropServices.MemoryMarshal.TryGetArray(stored, out ArraySegment<byte> segment)
                ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false)
                : new MemoryStream(stored.ToArray(), writable: false);
            using var zlib = new ZLibStream(source, CompressionMode.Decompress);
            int read = 0;
            while (read < length)
            {
                int n = zlib.Read(result, read, length - read);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }

            if (read != length || zlib.ReadByte() >= 0)
            {
                throw FontErrors.Corrupt(offset, $"The WOFF {Name(tag)} does not inflate to its original length.");
            }
        }
        catch (InvalidDataException e)
        {
            throw FontErrors.Corrupt(offset, $"The WOFF {Name(tag)} does not inflate ({e.Message}).");
        }

        return result;
    }

    internal static void CheckLimit(long length, long limit)
    {
        if (length > limit)
        {
            throw new LimitExceededException(nameof(SafetyLimits.MaxTotalBytes), limit, length);
        }
    }

    internal static long Pad4(long length) => (length + 3) & ~3L;

    private static string Name(FontTag tag) => tag == default ? "metadata block" : $"{tag} table";

    private readonly record struct SfntTable(FontTag Tag, int Offset, int StoredLength, int Length);
}

/// <summary>One face of an sfnt being assembled: its flavor (sfnt version) and its tables, by index into the shared bodies.</summary>
internal sealed record SfntFace(uint Flavor, (FontTag Tag, int Body)[] Tables);

/// <summary>Writes a single sfnt or a collection from table bodies: directories sorted by tag, tables 4-byte aligned, shared once.</summary>
internal static class SfntAssembler
{
    public static byte[] Build(IReadOnlyList<SfntFace> faces, IReadOnlyList<byte[]> bodies, uint collectionVersion, long limit)
    {
        bool collection = collectionVersion != 0;
        long size = collection ? 12 + (4L * faces.Count) + (collectionVersion >= 0x00020000 ? 12 : 0) : 0;
        var directoryAt = new long[faces.Count];
        for (int f = 0; f < faces.Count; f++)
        {
            directoryAt[f] = size;
            size += 12 + (16L * faces[f].Tables.Length);
        }

        var bodyAt = new long[bodies.Count];
        Array.Fill(bodyAt, -1);
        foreach (SfntFace face in faces)
        {
            foreach ((_, int body) in face.Tables)
            {
                if (bodyAt[body] < 0)
                {
                    bodyAt[body] = size;
                    size += WebFontDecoder.Pad4(bodies[body].Length);
                    WebFontDecoder.CheckLimit(size, limit);
                }
            }
        }

        byte[] font = new byte[size];
        if (collection)
        {
            BinaryPrimitives.WriteUInt32BigEndian(font, SfntDirectory.CollectionTag);
            BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(4), collectionVersion);
            BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(8), (uint)faces.Count);
            for (int f = 0; f < faces.Count; f++)
            {
                BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(12 + (4 * f)), (uint)directoryAt[f]);
            }
        }

        var checksums = new uint[bodies.Count];
        for (int b = 0; b < bodies.Count; b++)
        {
            if (bodyAt[b] >= 0)
            {
                bodies[b].CopyTo(font.AsSpan((int)bodyAt[b]));
                checksums[b] = Subsetting.SfntWriter.Checksum(bodies[b]);
            }
        }

        for (int f = 0; f < faces.Count; f++)
        {
            SfntFace face = faces[f];
            var sorted = face.Tables.OrderBy(t => t.Tag.Value).ToArray();
            int count = sorted.Length;
            int entrySelector = 0;
            while ((2 << entrySelector) <= count)
            {
                entrySelector++;
            }

            int searchRange = (1 << entrySelector) * 16;
            Span<byte> d = font.AsSpan((int)directoryAt[f]);
            BinaryPrimitives.WriteUInt32BigEndian(d, face.Flavor);
            BinaryPrimitives.WriteUInt16BigEndian(d[4..], (ushort)count);
            BinaryPrimitives.WriteUInt16BigEndian(d[6..], (ushort)searchRange);
            BinaryPrimitives.WriteUInt16BigEndian(d[8..], (ushort)entrySelector);
            BinaryPrimitives.WriteUInt16BigEndian(d[10..], (ushort)((count * 16) - searchRange));
            for (int i = 0; i < count; i++)
            {
                (FontTag tag, int body) = sorted[i];
                Span<byte> e = d[(12 + (16 * i))..];
                BinaryPrimitives.WriteUInt32BigEndian(e, tag.Value);
                BinaryPrimitives.WriteUInt32BigEndian(e[4..], checksums[body]);
                BinaryPrimitives.WriteUInt32BigEndian(e[8..], (uint)bodyAt[body]);
                BinaryPrimitives.WriteUInt32BigEndian(e[12..], (uint)bodies[body].Length);
            }
        }

        return font;
    }
}
