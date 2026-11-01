using System.Buffers;
using System.Runtime.InteropServices;

using Egami.Formats.Jpeg;
using Egami.Pixels;

namespace Egami.Formats.Tiff;

/// <summary>
/// JPEG (7), old-style JPEG (6), WebP (50001) and JPEG XL (50002, and DNG 1.7's 52546) strips and tiles, each a whole stream for
/// Egami's own decoders: JPEG through the 1.3 decoder with the page's JPEGTables (lossless JPEG to its raw samples, T.81 annex H),
/// WebP through the 2.3.1 reader, JPEG XL through the 2.9.3 one.
/// </summary>
internal static class TiffJpeg
{
    /// <summary>Decodes one unit's stream into a raster of the page's natural type; the caller copies what it needs and disposes it.</summary>
    public static Raster<TNatural> Decode<TNatural>(TiffPage page, ReadOnlySpan<byte> unit, int width, int height, EgamiContext context, bool strict)
        where TNatural : unmanaged, IPixelType<TNatural>
    {
        byte[] stream = page.Compression switch
        {
            TiffCodes.Jpeg => JpegStream(page, unit),
            TiffCodes.OldJpeg => OldJpegStream(page, unit, width, height),
            _ => unit.ToArray(),
        };
        using MemoryStream memory = new(stream, 0, stream.Length, writable: false, publiclyVisible: true);
        EgamiInput input = new(memory, context, CancellationToken.None);
        try
        {
            bool raw = RawYCbCr(page);
            ReaderOptions options = new JpegReaderOptions
            {
                ReadMetadata = false,
                Recovery = strict ? RecoveryMode.Strict : RecoveryMode.Lenient,
                FastUpsampling = raw,
            };
            if (page.Compression == TiffCodes.Webp)
            {
                input.SetFormat(Webp.WebpFormat.Info);
                return Webp.WebpFormat.Info.Reader!.Read<TNatural>(input, options);
            }

            if (page.Compression is TiffCodes.JpegXl or TiffCodes.DngJpegXl)
            {
                input.SetFormat(Jxl.JxlFormat.Info);
                return Jxl.JxlFormat.Info.Reader!.Read<TNatural>(input, ReaderOptions.Default with { ReadMetadata = false, Recovery = options.Recovery });
            }

            input.SetFormat(JpegFormat.Info);
            JpegHeader header = JpegSegmentReader.ReadHeader(input, options);
            if (page.Photometric == 2 && header.Components.Length == 3)
            {
                // RGB kept as RGB (libtiff's JPEGCOLORMODE raw): no Adobe segment says so, so say it here.
                header.AdobeTransform = 0;
            }

            return JpegReader.Decode<TNatural>(input, header, options);
        }
        finally
        {
            input.Dispose();
        }
    }

    /// <summary>
    /// Returns whether a JPEG page's first strip or tile that has data is lossless JPEG (SOF3 or SOF11), by the frame marker of
    /// its first megabyte.
    /// </summary>
    public static bool IsLossless(TiffPage page)
    {
        for (long unit = 0; unit < page.UnitCount; unit++)
        {
            (long offset, long count) = page.Unit(unit);
            if (count <= 0)
            {
                continue;
            }

            int length = (int)Math.Min(count, 1 << 20);
            byte[] head = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                int n = page.File.Source.Read(offset, head.AsSpan(0, length));
                return FrameMarker(head.AsSpan(0, n)) is JpegMarker.Sof3 or JpegMarker.Sof11;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(head);
            }
        }

        return false;
    }

    /// <summary>Returns a JPEG stream's frame marker (SOF0 to SOF15), or -1 when none comes before the scan or the data ends.</summary>
    private static int FrameMarker(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
        {
            return -1;
        }

        int at = 2;
        while (at + 4 <= jpeg.Length && jpeg[at] == 0xFF)
        {
            byte marker = jpeg[at + 1];
            if (marker == 0xFF)
            {
                at++;
                continue;
            }

            if (marker is >= 0xC0 and <= 0xCF and not (JpegMarker.Dht or 0xC8 or 0xCC))
            {
                return marker;
            }

            if (marker is JpegMarker.Sos or JpegMarker.Eoi)
            {
                return -1;
            }

            at += 2 + ((jpeg[at + 2] << 8) | jpeg[at + 3]);
        }

        return -1;
    }

    /// <summary>
    /// Decodes a lossless JPEG unit to the page's raw samples: <paramref name="rows"/> rows of the unit, 8-bit samples as bytes,
    /// 16-bit ones in the host's order, other depths packed from the high bit and each row padded to a byte, as TIFF stores them.
    /// The frame may lay a row out as any number of components that fill it, as DNG's two-component tiles do. Returns the bytes
    /// filled: none for a unit that does not decode or does not fit the page, and one short of the whole for a damaged one, so
    /// the caller reports both and keeps what decoded.
    /// </summary>
    public static int DecodeLossless(TiffPage page, ReadOnlySpan<byte> unit, int rows, Span<byte> target)
    {
        ushort[] samples;
        int width, components;
        bool damaged;
        try
        {
            samples = JpegEmbedded.DecodeLosslessSamples(page.JpegTables ?? default(ReadOnlyMemory<byte>), unit.ToArray(), page.File.Input.Context, out _,
                out width, out components, out damaged);
        }
        catch (Exception e) when (e is CorruptDataException or InvalidDataException or UnsupportedFormatException or EndOfStreamException)
        {
            return 0;
        }

        int rowSamples = page.UnitWidth * page.UnitSamples;
        int rowBytes = (int)page.UnitRowBytes;
        if (width * components != rowSamples || rowBytes == 0)
        {
            return 0;
        }

        int whole = Math.Min(Math.Min(rows, samples.Length / rowSamples), target.Length / rowBytes);
        int bits = page.Bits;
        int mask = (1 << bits) - 1;
        for (int r = 0; r < whole; r++)
        {
            ReadOnlySpan<ushort> from = samples.AsSpan(r * rowSamples, rowSamples);
            Span<byte> to = target.Slice(r * rowBytes, rowBytes);
            if (bits == 8)
            {
                for (int i = 0; i < from.Length; i++)
                {
                    to[i] = (byte)from[i];
                }
            }
            else if (bits == 16)
            {
                from.CopyTo(MemoryMarshal.Cast<byte, ushort>(to));
            }
            else
            {
                ulong buffer = 0;
                int count = 0, at = 0;
                foreach (ushort sample in from)
                {
                    buffer = (buffer << bits) | (uint)(sample & mask);
                    count += bits;
                    while (count >= 8)
                    {
                        count -= 8;
                        to[at++] = (byte)(buffer >> count);
                    }
                }

                if (count > 0)
                {
                    to[at] = (byte)(buffer << (8 - count));
                }
            }
        }

        return (whole * rowBytes) - (damaged && whole > 0 ? 1 : 0);
    }

    /// <summary>
    /// Gets whether a page's JPEG is converted with the page's own ReferenceBlackWhite and coefficients: old-style JPEG, which
    /// libtiff hands out as YCbCr and converts that way, replicating chroma.
    /// </summary>
    public static bool RawYCbCr(TiffPage page) => page.Compression == TiffCodes.OldJpeg && page.Photometric == 6;

    /// <summary>A strip or tile with the JPEGTables in front: the tables without their EOI, then the unit without its SOI.</summary>
    private static byte[] JpegStream(TiffPage page, ReadOnlySpan<byte> unit)
    {
        if (page.JpegTables is not { Length: >= 4 } tables || unit.Length < 2 || unit[0] != 0xFF || unit[1] != 0xD8)
        {
            return unit.ToArray();
        }

        int head = tables[^2] == 0xFF && tables[^1] == 0xD9 ? tables.Length - 2 : tables.Length;
        byte[] stream = new byte[head + unit.Length - 2];
        tables.AsSpan(0, head).CopyTo(stream);
        unit[2..].CopyTo(stream.AsSpan(head));
        return stream;
    }

    /// <summary>
    /// An old-style JPEG unit (TIFF 6.0 section 22), best effort: a unit that is already a JPEG stream is used as it is; otherwise
    /// the tables are rebuilt from JPEGQTables, JPEGDCTables and JPEGACTables, with a frame and scan header for the unit, its
    /// entropy-coded data and an EOI. Moved from Lars's ClassicMac (TiffFile.cs, MIT).
    /// </summary>
    private static byte[] OldJpegStream(TiffPage page, ReadOnlySpan<byte> unit, int width, int height)
    {
        if (unit.Length >= 2 && unit[0] == 0xFF && unit[1] == 0xD8)
        {
            return unit.ToArray();
        }

        byte[] tables = OldTables(page);
        int components = page.Samples;
        List<byte> stream = new(tables.Length + unit.Length + 64) { 0xFF, 0xD8 };
        stream.AddRange(tables);
        // SOF0: baseline, 8-bit, the unit's size; Y takes the subsampling, the others 1 × 1; component i uses table i.
        stream.AddRange([0xFF, 0xC0, 0, (byte)(8 + (3 * components)), 8, (byte)(height >> 8), (byte)height,
            (byte)(width >> 8), (byte)width, (byte)components]);
        for (int c = 0; c < components; c++)
        {
            byte sampling = c == 0 && page.Photometric == 6 ? (byte)((page.SubH << 4) | page.SubV) : (byte)0x11;
            stream.AddRange([(byte)(c + 1), sampling, (byte)c]);
        }

        // SOS: every component, its DC and AC tables, the whole spectrum.
        stream.AddRange([0xFF, 0xDA, 0, (byte)(6 + (2 * components)), (byte)components]);
        for (int c = 0; c < components; c++)
        {
            stream.AddRange([(byte)(c + 1), (byte)((c << 4) | c)]);
        }

        stream.AddRange([0, 63, 0]);
        stream.AddRange(unit.ToArray());
        stream.AddRange([0xFF, 0xD9]);
        return [.. stream];
    }

    /// <summary>Rebuilds DQT, DHT and DRI segments from an old-style JPEG page's table tags (one offset per component each).</summary>
    private static byte[] OldTables(TiffPage page)
    {
        TiffFile file = page.File;
        int components = page.Samples;
        ulong[] Offsets(int tag, string name) =>
            page.Ifd.TryGet(tag, out TiffEntry entry) && entry.Count >= components ? file.Integers(entry, 16)
                : throw new NotSupportedException($"TIFF: an old-style JPEG page without JPEGInterchangeFormat needs {name} for each component.");

        ulong[] q = Offsets(TiffTags.JpegQTables, "JPEGQTables");
        ulong[] dc = Offsets(TiffTags.JpegDcTables, "JPEGDCTables");
        ulong[] ac = Offsets(TiffTags.JpegAcTables, "JPEGACTables");
        List<byte> tables = [];
        Span<byte> buffer = stackalloc byte[16 + 256];
        for (int c = 0; c < components; c++)
        {
            if (file.Source.Read((long)q[c], buffer[..64]) < 64)
            {
                throw file.Input.Corrupt((long)q[c], $"JPEGQTables[{c}] lies outside the file.");
            }

            tables.AddRange([0xFF, 0xDB, 0, 67, (byte)c]);
            tables.AddRange(buffer[..64].ToArray());
        }

        void Huffman(ulong[] offsets, int tableClass, Span<byte> buffer)
        {
            for (int c = 0; c < components; c++)
            {
                if (file.Source.Read((long)offsets[c], buffer[..16]) < 16)
                {
                    throw file.Input.Corrupt((long)offsets[c], "A JPEG Huffman table lies outside the file.");
                }

                int values = 0;
                foreach (byte n in buffer[..16])
                {
                    values += n;
                }

                if (values > 256 || file.Source.Read((long)offsets[c] + 16, buffer.Slice(16, values)) < values)
                {
                    throw file.Input.Corrupt((long)offsets[c], "A JPEG Huffman table lies outside the file.");
                }

                int length = 2 + 1 + 16 + values;
                tables.AddRange([0xFF, 0xC4, (byte)(length >> 8), (byte)length, (byte)((tableClass << 4) | c)]);
                tables.AddRange(buffer[..(16 + values)].ToArray());
            }
        }

        Huffman(dc, 0, buffer);
        Huffman(ac, 1, buffer);
        ulong restart = file.Integer(page.Ifd, TiffTags.JpegRestartInterval, 0);
        if (restart is > 0 and <= 65535)
        {
            tables.AddRange([0xFF, 0xDD, 0, 4, (byte)(restart >> 8), (byte)restart]);
        }

        return [.. tables];
    }

    /// <summary>
    /// Returns an old-style JPEG page's whole stream when JPEGInterchangeFormat points at one, or <see langword="null"/>: such pages
    /// decode as one picture.
    /// </summary>
    public static byte[]? Interchange(TiffPage page)
    {
        TiffFile file = page.File;
        if (page.Compression != TiffCodes.OldJpeg || !page.Ifd.TryGet(TiffTags.JpegInterchangeFormat, out TiffEntry at) || at.ValueAt < 0)
        {
            return null;
        }

        long offset = (long)file.Integer(at, 0);
        long length = (long)file.Integer(page.Ifd, TiffTags.JpegInterchangeFormatLength, 0);
        if (offset <= 0 || offset >= file.Source.Length)
        {
            return null;
        }

        if (length <= 0 || length > file.Source.Length - offset)
        {
            length = file.Source.Length - offset;
        }

        if (length > int.MaxValue)
        {
            return null;
        }

        byte[] bytes = ArrayPool<byte>.Shared.Rent((int)length);
        try
        {
            int n = file.Source.Read(offset, bytes.AsSpan(0, (int)length));
            return bytes.AsSpan(0, n).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }
}
