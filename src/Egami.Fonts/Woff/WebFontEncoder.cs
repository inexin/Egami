using System.Buffers.Binary;
using System.IO.Compression;

using Egami.Fonts.Parsing;
using Egami.Fonts.Subsetting;

namespace Egami.Fonts.Woff;

/// <summary>
/// Writes WOFF 1.0 (W3C: a header, a directory sorted by tag, each table compressed with zlib when that makes it smaller) and
/// WOFF 2.0 (W3C: a directory with known-tag flags and UIntBase128 lengths, a collection directory, one Brotli stream of every
/// table with the glyf, loca and hmtx transforms), each with its optional metadata and private blocks.
/// </summary>
internal static class WebFontEncoder
{
    private const int WoffHeaderSize = 44;
    private const int Woff2HeaderSize = 48;
    private const int BrotliQuality = 11;
    private const int BrotliWindow = 22;

    private static readonly Dictionary<uint, int> s_knownTagIndex = BuildKnownTags();

    public static byte[] Encode(WebFontSource source, WebFontOptions options, EgamiContext context)
    {
        byte[]? metadata = options.Metadata is null ? source.Metadata : options.Metadata.Length == 0 ? null : System.Text.Encoding.UTF8.GetBytes(options.Metadata);
        ReadOnlyMemory<byte> priv = options.PrivateData ?? source.PrivateData ?? ReadOnlyMemory<byte>.Empty;
        if (metadata is { Length: 0 })
        {
            metadata = null;
        }

        long limit = context.Limits.MaxTotalBytes;
        WebFontDecoder.CheckLimit((metadata?.Length ?? 0) + (long)priv.Length, limit);
        return options.Format switch
        {
            WebFontFormat.Woff => EncodeWoff(source, metadata, priv, limit),
            WebFontFormat.Woff2 => EncodeWoff2(source, options, metadata, priv, context),
            WebFontFormat.Eot => EncodeEot(source, options, limit),
            _ => throw new ArgumentOutOfRangeException(nameof(options), $"Unknown web font format {options.Format}."),
        };
    }

    internal static void Report(EgamiContext context, string id, string message, long offset)
    {
        if (context.Diagnostics is null || context.DiagnosticLevel > DiagnosticLevel.Warning)
        {
            return;
        }

        using DiagnosticReporter reporter = context.StartDiagnostics();
        reporter.Report(DiagnosticLevel.Warning, id, message, offset >= 0 ? offset : null);
    }

    private static byte[] EncodeEot(WebFontSource source, WebFontOptions options, long limit)
    {
        if (source.Faces.Count != 1)
        {
            throw new ArgumentException("EOT holds a single font; write a collection as WOFF2.");
        }

        var bodies = new byte[source.Tables.Count][];
        for (int i = 0; i < bodies.Length; i++)
        {
            bodies[i] = source.Tables[i].Data.ToArray();
        }

        byte[] sfnt = SfntAssembler.Build(source.Faces, bodies, collectionVersion: 0, limit);
        return Eot.Encode(sfnt, options.RootString, options.XorFontData, limit);
    }

    private static byte[] EncodeWoff(WebFontSource source, byte[]? metadata, ReadOnlyMemory<byte> priv, long limit)
    {
        if (source.Faces.Count != 1)
        {
            throw new ArgumentException("WOFF 1.0 holds a single font; write a collection as WOFF2.");
        }

        SfntFace face = source.Faces[0];
        var tables = face.Tables.OrderBy(t => t.Tag.Value).Select(t => source.Tables[t.Body]).ToArray();
        int count = tables.Length;
        var stored = new byte[count][];
        long totalSfntSize = 12 + (16L * count);
        long size = WoffHeaderSize + (20L * count);
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> body = tables[i].Data.Span;
            byte[] compressed = Deflate(body);
            stored[i] = compressed.Length < body.Length ? compressed : body.ToArray();
            totalSfntSize += WebFontDecoder.Pad4(body.Length);
            size += WebFontDecoder.Pad4(stored[i].Length);
            WebFontDecoder.CheckLimit(size, limit);
        }

        byte[]? packedMetadata = metadata is null ? null : Deflate(metadata);
        long metaOffset = 0, privOffset = 0;
        if (packedMetadata is not null)
        {
            metaOffset = size;
            size += packedMetadata.Length;
        }

        if (!priv.IsEmpty)
        {
            size = WebFontDecoder.Pad4(size);
            privOffset = size;
            size += priv.Length;
        }

        WebFontDecoder.CheckLimit(size, limit);
        CheckUInt(totalSfntSize);
        var w = new ByteWriter((int)size);
        w.U32(SfntDirectory.WoffTag);
        w.U32(face.Flavor);
        w.U32((uint)size);
        w.U16(count);
        w.U16(0);
        w.U32((uint)totalSfntSize);
        WriteVersion(w, source, face);
        w.U32((uint)metaOffset);
        w.U32((uint)(packedMetadata?.Length ?? 0));
        w.U32((uint)(metadata?.Length ?? 0));
        w.U32((uint)privOffset);
        w.U32((uint)priv.Length);
        long offset = WoffHeaderSize + (20L * count);
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> body = tables[i].Data.Span;
            w.U32(tables[i].Tag.Value);
            w.U32((uint)offset);
            w.U32((uint)stored[i].Length);
            w.U32((uint)body.Length);
            w.U32(Checksum(tables[i].Tag, body));
            offset += WebFontDecoder.Pad4(stored[i].Length);
        }

        foreach (byte[] table in stored)
        {
            w.Bytes(table);
            w.PadTo(4);
        }

        if (packedMetadata is not null)
        {
            w.Bytes(packedMetadata);
        }

        if (!priv.IsEmpty)
        {
            w.PadTo(4);
            w.Bytes(priv.Span);
        }

        return w.ToArray();
    }

    private static byte[] EncodeWoff2(WebFontSource source, WebFontOptions options, byte[]? metadata, ReadOnlyMemory<byte> priv, EgamiContext context)
    {
        long limit = context.Limits.MaxTotalBytes;
        int tableCount = source.Tables.Count;
        if (tableCount > 0xFFFF)
        {
            throw new ArgumentException($"The font has {tableCount} tables; WOFF2 holds at most 65535.");
        }

        // What each table becomes: its bytes in the stream, its transform version and its length once decoded.
        var entries = new Entry[tableCount];
        for (int t = 0; t < tableCount; t++)
        {
            SourceTable table = source.Tables[t];
            bool glyfOrLoca = table.Tag == Tags.Glyf || table.Tag == Tags.Loca;
            entries[t] = new Entry(table.Tag, table.Data, glyfOrLoca ? 3 : 0, (uint)table.Data.Length, Transformed: false);
        }

        var pairs = new Dictionary<int, int>();
        foreach (SfntFace face in source.Faces)
        {
            int glyf = Find(face, Tags.Glyf), loca = Find(face, Tags.Loca);
            if (glyf >= 0 && loca >= 0)
            {
                pairs.TryAdd(glyf, loca);
            }
        }

        if (options.TransformGlyf)
        {
            var xMins = new Dictionary<int, short[]>();
            foreach ((int glyf, int loca) in pairs)
            {
                if (TransformGlyf(source, glyf, loca, entries, context) is short[] xMin)
                {
                    xMins[glyf] = xMin;
                }
            }

            if (options.TransformHmtx)
            {
                TransformHmtx(source, entries, xMins);
            }
        }

        // Bit 11 of head.flags: the font has been through a lossless modifying transform; set in every WOFF2 file, as the
        // reference encoder sets it.
        foreach (SfntFace face in source.Faces)
        {
            int head = Find(face, Tags.Head);
            if (head >= 0 && entries[head].Data.Length >= 18 && (entries[head].Data.Span[16] & 0x08) == 0)
            {
                byte[] copy = entries[head].Data.ToArray();
                copy[16] |= 0x08;
                entries[head] = entries[head] with { Data = copy };
            }
        }

        // The directory: tables sorted by tag, each loca right after its glyf, as the reference encoder orders them.
        var order = new List<int>(tableCount);
        var pairedLoca = new HashSet<int>(pairs.Values);
        foreach (int t in Enumerable.Range(0, tableCount).OrderBy(t => entries[t].Tag.Value).ThenBy(t => t))
        {
            if (pairedLoca.Contains(t))
            {
                continue;
            }

            order.Add(t);
            if (pairs.TryGetValue(t, out int loca) && !order.Contains(loca))
            {
                order.Add(loca);
            }
        }

        foreach (int loca in pairedLoca)
        {
            if (!order.Contains(loca))
            {
                order.Add(loca);
            }
        }

        var position = new int[tableCount];
        for (int i = 0; i < order.Count; i++)
        {
            position[order[i]] = i;
        }

        var directory = new ByteWriter(order.Count * 10);
        long streamLength = 0;
        foreach (int t in order)
        {
            Entry e = entries[t];
            int flags = e.Version << 6;
            if (s_knownTagIndex.TryGetValue(e.Tag.Value, out int known))
            {
                directory.U8(flags | known);
            }
            else
            {
                directory.U8(flags | 63);
                directory.U32(e.Tag.Value);
            }

            WriteBase128(directory, e.OrigLength);
            if (e.Transformed)
            {
                WriteBase128(directory, (uint)e.Data.Length);
            }

            streamLength += e.Data.Length;
            WebFontDecoder.CheckLimit(streamLength, limit);
        }

        bool collection = source.CollectionVersion != 0;
        if (collection)
        {
            directory.U32(source.CollectionVersion);
            Woff2Glyf.Write255UInt16(directory, source.Faces.Count);
            foreach (SfntFace face in source.Faces)
            {
                Woff2Glyf.Write255UInt16(directory, face.Tables.Length);
                directory.U32(face.Flavor);
                foreach ((_, int body) in face.Tables)
                {
                    Woff2Glyf.Write255UInt16(directory, position[body]);
                }
            }
        }

        byte[] stream = new byte[streamLength];
        int at = 0;
        foreach (int t in order)
        {
            entries[t].Data.Span.CopyTo(stream.AsSpan(at));
            at += entries[t].Data.Length;
        }

        byte[] compressed = Brotli(stream);
        byte[]? packedMetadata = metadata is null ? null : Brotli(metadata);

        long size = Woff2HeaderSize + directory.Length + compressed.Length;
        size = WebFontDecoder.Pad4(size);
        long metaOffset = 0, privOffset = 0;
        if (packedMetadata is not null)
        {
            metaOffset = size;
            size = WebFontDecoder.Pad4(size + packedMetadata.Length);
        }

        if (!priv.IsEmpty)
        {
            privOffset = size;
            size = WebFontDecoder.Pad4(size + priv.Length);
        }

        WebFontDecoder.CheckLimit(size, limit);
        long totalSfntSize = TotalSfntSize(source, entries);
        CheckUInt(totalSfntSize);
        var w = new ByteWriter((int)size);
        w.U32(SfntDirectory.Woff2Tag);
        w.U32(collection ? SfntDirectory.CollectionTag : source.Faces[0].Flavor);
        w.U32((uint)size);
        w.U16(order.Count);
        w.U16(0);
        w.U32((uint)totalSfntSize);
        w.U32((uint)compressed.Length);
        WriteVersion(w, source, source.Faces[0]);
        w.U32((uint)metaOffset);
        w.U32((uint)(packedMetadata?.Length ?? 0));
        w.U32((uint)(metadata?.Length ?? 0));
        w.U32((uint)privOffset);
        w.U32((uint)priv.Length);
        w.Bytes(directory.Written);
        w.Bytes(compressed);
        w.PadTo(4);
        if (packedMetadata is not null)
        {
            w.Bytes(packedMetadata);
            w.PadTo(4);
        }

        if (!priv.IsEmpty)
        {
            w.Bytes(priv.Span);
            w.PadTo(4);
        }

        return w.ToArray();
    }

    // Transforms one glyf and its loca when every face that uses either uses both, with the same glyph count and loca format.
    private static short[]? TransformGlyf(WebFontSource source, int glyf, int loca, Entry[] entries, EgamiContext context)
    {
        int glyphCount = -1, indexFormat = -1;
        foreach (SfntFace face in source.Faces)
        {
            int g = Find(face, Tags.Glyf), l = Find(face, Tags.Loca);
            if (g != glyf && l != loca)
            {
                continue;
            }

            if (g != glyf || l != loca)
            {
                Report(context, "Font.TransformSkipped", "A collection's faces share a glyf or loca table but not both; they are stored untransformed.", -1);
                return null;
            }

            int head = Find(face, Tags.Head), maxp = Find(face, Tags.Maxp);
            ReadOnlySpan<byte> headData = head >= 0 ? source.Tables[head].Data.Span : default, maxpData = maxp >= 0 ? source.Tables[maxp].Data.Span : default;
            if (headData.Length < 54 || maxpData.Length < 6)
            {
                Report(context, "Font.TransformSkipped", "The font lacks a readable head or maxp table; glyf and loca are stored untransformed.", -1);
                return null;
            }

            int format = BinaryPrimitives.ReadInt16BigEndian(headData[50..]), count = BinaryPrimitives.ReadUInt16BigEndian(maxpData[4..]);
            if ((glyphCount >= 0 && count != glyphCount) || (indexFormat >= 0 && format != indexFormat))
            {
                Report(context, "Font.TransformSkipped", "A collection's faces share glyf but give different glyph counts or loca formats; it is stored untransformed.", -1);
                return null;
            }

            (glyphCount, indexFormat) = (count, format);
        }

        Woff2Glyf.Transformed? transformed;
        try
        {
            transformed = Woff2Glyf.Transform(source.Tables[glyf].Data.Span, source.Tables[loca].Data.Span, glyphCount, indexFormat);
        }
        catch (CorruptDataException e)
        {
            Report(context, "Font.TransformSkipped", $"The glyf table cannot be read ({e.Message}); glyf and loca are stored untransformed.", -1);
            return null;
        }

        if (transformed is null)
        {
            Report(context, "Font.TransformSkipped", "The glyf table would rebuild too long for the short loca format; glyf and loca are stored untransformed.", -1);
            return null;
        }

        entries[glyf] = entries[glyf] with { Data = transformed.Data, Version = 0, Transformed = true };
        entries[loca] = entries[loca] with { Data = ReadOnlyMemory<byte>.Empty, Version = 0, Transformed = true, OrigLength = (uint)((glyphCount + 1) * (indexFormat == 0 ? 2 : 4)) };
        return transformed.XMin;
    }

    // Transforms each hmtx whose faces all use the same transformed glyf and agree on the metric and glyph counts.
    private static void TransformHmtx(WebFontSource source, Entry[] entries, Dictionary<int, short[]> xMins)
    {
        for (int hmtx = 0; hmtx < entries.Length; hmtx++)
        {
            if (entries[hmtx].Tag != Tags.Hmtx)
            {
                continue;
            }

            int glyf = -2, metrics = -1, glyphs = -1;
            bool ok = true;
            foreach (SfntFace face in source.Faces)
            {
                if (Find(face, Tags.Hmtx) != hmtx)
                {
                    continue;
                }

                int g = Find(face, Tags.Glyf), hhea = Find(face, Tags.Hhea), maxp = Find(face, Tags.Maxp);
                if (g < 0 || (glyf != -2 && g != glyf) || !xMins.ContainsKey(g) || hhea < 0 || maxp < 0
                    || source.Tables[hhea].Data.Length < 36 || source.Tables[maxp].Data.Length < 6)
                {
                    ok = false;
                    break;
                }

                int m = BinaryPrimitives.ReadUInt16BigEndian(source.Tables[hhea].Data.Span[34..]), n = BinaryPrimitives.ReadUInt16BigEndian(source.Tables[maxp].Data.Span[4..]);
                if ((metrics >= 0 && m != metrics) || (glyphs >= 0 && n != glyphs))
                {
                    ok = false;
                    break;
                }

                (glyf, metrics, glyphs) = (g, m, n);
            }

            if (ok && glyf >= 0 && Woff2Glyf.TransformHmtx(source.Tables[hmtx].Data.Span, metrics, glyphs, xMins[glyf]) is byte[] data)
            {
                entries[hmtx] = entries[hmtx] with { Data = data, Version = 1, Transformed = true };
            }
        }
    }

    // The sfnt the file decodes to: the collection header, each face's directory and every table once, padded.
    private static long TotalSfntSize(WebFontSource source, Entry[] entries)
    {
        long size = source.CollectionVersion == 0 ? 0 : 12 + (4L * source.Faces.Count) + (source.CollectionVersion >= 0x00020000 ? 12 : 0);
        foreach (SfntFace face in source.Faces)
        {
            size += 12 + (16L * face.Tables.Length);
        }

        foreach (Entry e in entries)
        {
            size += WebFontDecoder.Pad4(e.OrigLength);
        }

        return size;
    }

    // The file's version: head's fontRevision, its two halves as the major and minor numbers.
    private static void WriteVersion(ByteWriter w, WebFontSource source, SfntFace face)
    {
        int head = Find(face, Tags.Head);
        ReadOnlySpan<byte> data = head >= 0 ? source.Tables[head].Data.Span : default;
        w.U32(data.Length >= 8 ? BinaryPrimitives.ReadUInt32BigEndian(data[4..]) : 0x00010000);
    }

    // The table's checksum, with head's checkSumAdjustment taken as zero, whatever the source's directory says.
    private static uint Checksum(FontTag tag, ReadOnlySpan<byte> body)
    {
        uint sum = SfntWriter.Checksum(body);
        return tag == Tags.Head && body.Length >= 12 ? sum - BinaryPrimitives.ReadUInt32BigEndian(body[8..]) : sum;
    }

    private static void WriteBase128(ByteWriter w, uint value)
    {
        int bytes = 1;
        while (bytes < 5 && (value >> (7 * bytes)) != 0)
        {
            bytes++;
        }

        for (int i = bytes - 1; i >= 0; i--)
        {
            int b = (int)((value >> (7 * i)) & 0x7F);
            w.U8(i > 0 ? b | 0x80 : b);
        }
    }

    // zlib at levels 9 and 8, each with the default and the filtered strategy, keeping the smallest: .NET's zlib-ng gives up to
    // 1 % more than classic zlib's level 9 on font tables with any one of them, and within 0.4 % with the best of the four.
    private static byte[] Deflate(ReadOnlySpan<byte> data)
    {
        byte[]? best = null;
        using var output = new MemoryStream((data.Length / 2) + 64);
        foreach (ZLibCompressionStrategy strategy in (ReadOnlySpan<ZLibCompressionStrategy>)[ZLibCompressionStrategy.Default, ZLibCompressionStrategy.Filtered])
        {
            for (int level = 9; level >= 8; level--)
            {
                output.SetLength(0);
                using (var zlib = new ZLibStream(output, new ZLibCompressionOptions { CompressionLevel = level, CompressionStrategy = strategy }, leaveOpen: true))
                {
                    zlib.Write(data);
                }

                if (best is null || output.Length < best.Length)
                {
                    best = output.ToArray();
                }
            }
        }

        return best!;
    }

    private static byte[] Brotli(ReadOnlySpan<byte> data)
    {
        byte[] buffer = new byte[BrotliEncoder.GetMaxCompressedLength(data.Length)];
        if (!BrotliEncoder.TryCompress(data, buffer, out int written, BrotliQuality, BrotliWindow))
        {
            throw new InvalidOperationException("Brotli could not compress the font data.");
        }

        return buffer.AsSpan(0, written).ToArray();
    }

    private static void CheckUInt(long size)
    {
        if (size > uint.MaxValue)
        {
            throw new LimitExceededException(nameof(SafetyLimits.MaxTotalBytes), uint.MaxValue, size);
        }
    }

    private static int Find(SfntFace face, FontTag tag)
    {
        foreach ((FontTag t, int body) in face.Tables)
        {
            if (t == tag)
            {
                return body;
            }
        }

        return -1;
    }

    private static Dictionary<uint, int> BuildKnownTags()
    {
        var map = new Dictionary<uint, int>(Woff2Decoder.KnownTags.Length);
        for (int i = 0; i < Woff2Decoder.KnownTags.Length; i++)
        {
            map[new FontTag(Woff2Decoder.KnownTags[i]).Value] = i;
        }

        return map;
    }

    private readonly record struct Entry(FontTag Tag, ReadOnlyMemory<byte> Data, int Version, uint OrigLength, bool Transformed);
}
