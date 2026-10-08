using System.Buffers;
using System.Buffers.Binary;
using System.Text;

using Egami.Metadata;

namespace Egami.Formats.Jpeg;

/// <summary>
/// Reads the segments of a JPEG up to its first scan (ITU-T T.81 annex B): the frame header, tables and restart interval, and the
/// JFIF, EXIF, XMP, ICC, Adobe and comment segments. Modes outside baseline and progressive Huffman coding at 8 bits are rejected.
/// </summary>
internal static class JpegSegmentReader
{
    private static ReadOnlySpan<byte> JfifId => "JFIF\0"u8;

    private static ReadOnlySpan<byte> ExifId => "Exif\0\0"u8;

    private static ReadOnlySpan<byte> XmpId => "http://ns.adobe.com/xap/1.0/\0"u8;

    private static ReadOnlySpan<byte> ExtendedXmpId => "http://ns.adobe.com/xmp/extension/\0"u8;

    private static ReadOnlySpan<byte> IccId => "ICC_PROFILE\0"u8;

    private static ReadOnlySpan<byte> AdobeId => "Adobe"u8;

    /// <summary>Reads from the SOI marker up to and including the first SOS header.</summary>
    internal static JpegHeader ReadHeader(EgamiInput input, ReaderOptions options)
    {
        if (input.ReadByte() != 0xFF || input.ReadByte() != JpegMarker.Soi)
        {
            throw input.Corrupt(0, "The file does not start with a JPEG start-of-image marker.");
        }

        JpegHeader header = new();
        bool strict = options.Recovery == RecoveryMode.Strict;
        bool readMetadata = options.ReadMetadata;
        long kept = 0;
        Dictionary<int, byte[]> iccChunks = [];
        int iccCount = 0;
        ExtendedXmpParts? extended = null;
        while (true)
        {
            long at = input.Position;
            byte marker = NextMarker(input, strict);
            if (JpegMarker.IsStandalone(marker))
            {
                if (marker == JpegMarker.Eoi)
                {
                    throw input.Corrupt(at, "The file ends before any image data (SOS).");
                }

                continue;
            }

            int length = input.ReadUInt16BigEndian() - 2;
            if (length < 0)
            {
                throw input.Corrupt(at, $"Segment FF{marker:X2} has an invalid length.");
            }

            if (marker == JpegMarker.Sos)
            {
                if (header.Components.Length == 0)
                {
                    throw input.Corrupt(at, "A scan (SOS) comes before the frame header (SOF).");
                }

                header.FirstScan = ReadScan(input, header, length, at);
                break;
            }

            if (IsMetadata(marker))
            {
                if (!readMetadata)
                {
                    input.Skip(length);
                    continue;
                }

                kept += length;
                input.Limits.CheckMetadataBytes(kept);
            }

            byte[] data = ArrayPool<byte>.Shared.Rent(Math.Max(length, 1));
            try
            {
                Span<byte> payload = data.AsSpan(0, length);
                input.ReadExactly(payload);
                ReadSegment(input, header, marker, payload, at, iccChunks, ref iccCount, ref extended);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(data);
            }
        }

        FinishMetadata(header, iccChunks, iccCount, extended);
        return header;
    }

    /// <summary>Returns the frame's natural layout: Gray8 for one component, Rgb8 otherwise (CMYK and YCCK convert to RGB).</summary>
    internal static Pixels.PixelLayoutInfo Layout(JpegHeader header)
    {
        return header.Components.Length == 1 ? Pixels.Gray8.Layout : Pixels.Rgb8.Layout;
    }

    /// <summary>
    /// Returns the bytes a full load needs: the output raster, plus for a progressive file every coefficient (2 bytes each, over the
    /// blocks of whole MCUs), which a progressive decode holds until its last scan.
    /// </summary>
    internal static long EstimateBytes(JpegHeader header, int scale)
    {
        long width = (header.Width + scale - 1) / scale;
        long height = (header.Height + scale - 1) / scale;
        long bytes = width * height * Layout(header).BytesPerPixel;
        if (header.IsProgressive)
        {
            long mcusX = (header.Width + (8 * header.MaxH) - 1) / (8 * header.MaxH);
            long mcusY = (header.Height + (8 * header.MaxV) - 1) / (8 * header.MaxV);
            foreach (JpegFrameComponent c in header.Components)
            {
                bytes += mcusX * c.H * mcusY * c.V * 64 * 2;
            }
        }

        return bytes;
    }

    /// <summary>
    /// Returns the smallest decode scale, 1, 2, 4 or 8, whose output still covers <paramref name="target"/> in both directions, or 1
    /// without a target.
    /// </summary>
    internal static int ScaleFor(JpegHeader header, Size? target)
    {
        if (target is not Size t || t.Width < 1 || t.Height < 1)
        {
            return 1;
        }

        for (int scale = 8; scale > 1; scale /= 2)
        {
            if ((header.Width + scale - 1) / scale >= t.Width && (header.Height + scale - 1) / scale >= t.Height)
            {
                return scale;
            }
        }

        return 1;
    }

    /// <summary>
    /// Reads the next marker. Fill bytes (extra 0xFF) are skipped; anything else before a marker is skipped too in a lenient read,
    /// as libjpeg does, and is an error in a strict one.
    /// </summary>
    private static byte NextMarker(EgamiInput input, bool strict)
    {
        long at = input.Position;
        byte b = input.ReadByte();
        while (true)
        {
            if (b != 0xFF)
            {
                if (strict)
                {
                    throw input.Corrupt(at, "Expected a marker (0xFF).");
                }

                b = input.ReadByte();
                continue;
            }

            byte marker = input.ReadByte();
            while (marker == 0xFF)
            {
                marker = input.ReadByte();
            }

            if (marker == 0x00)
            {
                // A stuffed byte where a marker should be: keep looking.
                b = input.ReadByte();
                continue;
            }

            return marker;
        }
    }

    private static bool IsMetadata(byte marker)
    {
        return marker is (>= JpegMarker.App0 and <= JpegMarker.App15) or JpegMarker.Com;
    }

    private static void ReadSegment(
        EgamiInput input, JpegHeader header, byte marker, ReadOnlySpan<byte> payload, long at, Dictionary<int, byte[]> iccChunks, ref int iccCount, ref ExtendedXmpParts? extended)
    {
        switch (marker)
        {
            case JpegMarker.Sof0 or JpegMarker.Sof1 or JpegMarker.Sof2:
                ReadFrame(input, header, marker, payload, at);
                break;
            case JpegMarker.Sof3:
                throw new UnsupportedFormatException("Lossless JPEG (SOF3) is not supported.");
            case > JpegMarker.Sof3 and <= JpegMarker.Sof15 when marker is not (JpegMarker.Dht or JpegMarker.Dac or 0xC8):
                throw new UnsupportedFormatException(marker is >= 0xC9 ? "Arithmetic-coded JPEG is not supported." : "Hierarchical JPEG is not supported.");
            case JpegMarker.Dac:
                throw new UnsupportedFormatException("Arithmetic-coded JPEG is not supported.");
            case JpegMarker.Dhp or JpegMarker.Exp:
                throw new UnsupportedFormatException("Hierarchical JPEG is not supported.");
            case JpegMarker.Dht:
                ReadHuffmanTables(input, header, payload, at);
                break;
            case JpegMarker.Dqt:
                ReadQuantTables(input, header, payload, at);
                break;
            case JpegMarker.Dri:
                header.RestartInterval = payload.Length >= 2 ? BinaryPrimitives.ReadUInt16BigEndian(payload) : throw input.Corrupt(at, "The restart interval segment is too short.");
                break;
            case JpegMarker.Dnl:
                throw new UnsupportedFormatException("JPEG files that give their height after the image data (DNL) are not supported.");
            case JpegMarker.App0:
                ReadJfif(header, payload);
                break;
            case JpegMarker.App1:
                ReadApp1(header, payload, input.Limits.MaxMetadataBytes, ref extended);
                break;
            case JpegMarker.App2:
                ReadIcc(header, payload, iccChunks, ref iccCount);
                break;
            case JpegMarker.App14:
                if (payload.Length >= 12 && payload.StartsWith(AdobeId))
                {
                    header.AdobeTransform = payload[11];
                }

                break;
            case JpegMarker.Com:
                if (payload.Length > 0)
                {
                    header.Metadata.Text.Add(new TextEntry("Comment", DecodeText(payload)));
                    header.HasMetadata = true;
                }

                break;
        }
    }

    private static void ReadFrame(EgamiInput input, JpegHeader header, byte marker, ReadOnlySpan<byte> payload, long at)
    {
        if (header.Components.Length != 0)
        {
            throw input.Corrupt(at, "The file has more than one frame header (SOF).");
        }

        if (payload.Length < 6)
        {
            throw input.Corrupt(at, "The frame header is too short.");
        }

        int precision = payload[0];
        if (precision != 8)
        {
            throw new UnsupportedFormatException($"{precision}-bit JPEG is not supported; only 8-bit samples are.");
        }

        int height = BinaryPrimitives.ReadUInt16BigEndian(payload[1..]);
        int width = BinaryPrimitives.ReadUInt16BigEndian(payload[3..]);
        int count = payload[5];
        if (height == 0)
        {
            throw new UnsupportedFormatException("JPEG files that give their height after the image data (DNL) are not supported.");
        }

        if (width == 0)
        {
            throw input.Corrupt(at, "The frame width is 0.");
        }

        if (count is not (1 or 3 or 4))
        {
            throw new UnsupportedFormatException($"JPEG with {count} components is not supported; 1, 3 and 4 are.");
        }

        if (payload.Length < 6 + (3 * count))
        {
            throw input.Corrupt(at, "The frame header is too short for its components.");
        }

        JpegFrameComponent[] components = new JpegFrameComponent[count];
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> c = payload.Slice(6 + (3 * i), 3);
            int h = c[1] >> 4;
            int v = c[1] & 15;
            if (h is < 1 or > 4 || v is < 1 or > 4 || c[2] > 3)
            {
                throw input.Corrupt(at, $"Component {i} has invalid sampling factors or quantization table.");
            }

            for (int j = 0; j < i; j++)
            {
                if (components[j].Id == c[0])
                {
                    throw input.Corrupt(at, $"Two components share the identifier {c[0]}.");
                }
            }

            components[i] = new JpegFrameComponent(c[0], h, v, c[2]);
        }

        header.FrameMarker = marker;
        header.Width = width;
        header.Height = height;
        header.Components = components;
        header.Details.IsProgressive = marker == JpegMarker.Sof2;
        foreach (JpegFrameComponent component in components)
        {
            header.Details.Components.Add(new JpegComponent(component.Id, component.H, component.V));
        }
    }

    private static JpegScan ReadScan(EgamiInput input, JpegHeader header, int length, long at)
    {
        byte[] data = ArrayPool<byte>.Shared.Rent(Math.Max(length, 1));
        try
        {
            Span<byte> payload = data.AsSpan(0, length);
            input.ReadExactly(payload);
            return ParseScan(input, header, payload, at);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(data);
        }
    }

    /// <summary>Parses a scan header against the frame. Also used by the decoder for later scans.</summary>
    internal static JpegScan ParseScan(EgamiInput input, JpegHeader header, ReadOnlySpan<byte> payload, long at)
    {
        if (payload.Length < 1)
        {
            throw input.Corrupt(at, "The scan header is too short.");
        }

        int count = payload[0];
        if (count is < 1 or > 4 || payload.Length < 1 + (2 * count) + 3)
        {
            throw input.Corrupt(at, "The scan header is invalid.");
        }

        JpegScanComponent[] components = new JpegScanComponent[count];
        for (int i = 0; i < count; i++)
        {
            int id = payload[1 + (2 * i)];
            int tables = payload[2 + (2 * i)];
            int index = Array.FindIndex(header.Components, c => c.Id == id);
            if (index < 0 || (tables >> 4) > 3 || (tables & 15) > 3)
            {
                throw input.Corrupt(at, $"The scan names an unknown component ({id}) or table.");
            }

            components[i] = new JpegScanComponent(index, tables >> 4, tables & 15);
        }

        ReadOnlySpan<byte> tail = payload.Slice(1 + (2 * count), 3);
        JpegScan scan = new(components, tail[0], tail[1], tail[2] >> 4, tail[2] & 15);
        if (!header.IsProgressive && (scan.SpectralStart != 0 || scan.SpectralEnd != 63 || scan.ApproxHigh != 0 || scan.ApproxLow != 0))
        {
            // Sequential files must code all 64 coefficients; libjpeg only warns, so a lenient read would accept it.
            scan = scan with { SpectralStart = 0, SpectralEnd = 63, ApproxHigh = 0, ApproxLow = 0 };
        }

        if (scan.SpectralStart > 63 || scan.SpectralEnd > 63 || scan.SpectralStart > scan.SpectralEnd || scan.ApproxLow > 13 || scan.ApproxHigh > 13)
        {
            throw input.Corrupt(at, "The scan's spectral selection or successive approximation is invalid.");
        }

        return scan;
    }

    /// <summary>Parses DHT tables into the header. Also used by the decoder for tables between scans.</summary>
    internal static void ReadHuffmanTables(EgamiInput input, JpegHeader header, ReadOnlySpan<byte> payload, long at)
    {
        while (payload.Length > 0)
        {
            if (payload.Length < 17)
            {
                throw input.Corrupt(at, "A Huffman table is too short.");
            }

            int tc = payload[0] >> 4;
            int th = payload[0] & 15;
            if (tc > 1 || th > 3)
            {
                throw input.Corrupt(at, "A Huffman table has an invalid class or slot.");
            }

            byte[] counts = payload.Slice(1, 16).ToArray();
            int total = 0;
            foreach (byte n in counts)
            {
                total += n;
            }

            if (total > 256 || payload.Length < 17 + total)
            {
                throw input.Corrupt(at, "A Huffman table has more symbols than fit.");
            }

            JpegHuffmanSpec spec = new(counts, payload.Slice(17, total).ToArray());
            (tc == 0 ? header.DcTables : header.AcTables)[th] = spec;
            payload = payload[(17 + total)..];
        }
    }

    /// <summary>Parses DQT tables into the header. Also used by the decoder for tables between scans.</summary>
    internal static void ReadQuantTables(EgamiInput input, JpegHeader header, ReadOnlySpan<byte> payload, long at)
    {
        while (payload.Length > 0)
        {
            int pq = payload[0] >> 4;
            int tq = payload[0] & 15;
            int size = pq == 0 ? 64 : 128;
            if (pq > 1 || tq > 3 || payload.Length < 1 + size)
            {
                throw input.Corrupt(at, "A quantization table is invalid.");
            }

            ushort[] table = new ushort[64];
            for (int i = 0; i < 64; i++)
            {
                table[i] = pq == 0 ? payload[1 + i] : BinaryPrimitives.ReadUInt16BigEndian(payload[(1 + (2 * i))..]);
            }

            header.QuantTables[tq] = table;
            payload = payload[(1 + size)..];
        }
    }

    private static void ReadJfif(JpegHeader header, ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 14 || !payload.StartsWith(JfifId))
        {
            return;
        }

        header.HasJfif = true;
        int units = payload[7];
        int x = BinaryPrimitives.ReadUInt16BigEndian(payload[8..]);
        int y = BinaryPrimitives.ReadUInt16BigEndian(payload[10..]);
        if (x > 0 && y > 0 && units <= 2)
        {
            ResolutionUnit unit = units switch { 1 => ResolutionUnit.PerInch, 2 => ResolutionUnit.PerCentimeter, _ => ResolutionUnit.None };

            // 1:1 with no unit is what most encoders write when they know nothing; it says nothing either.
            if (!(unit == ResolutionUnit.None && x == 1 && y == 1))
            {
                header.Metadata.Resolution = new Resolution(x, y, unit);
                header.HasMetadata = true;
            }
        }
    }

    private static void ReadApp1(JpegHeader header, ReadOnlySpan<byte> payload, long maxBytes, ref ExtendedXmpParts? extended)
    {
        if (payload.StartsWith(ExifId))
        {
            // The first EXIF segment wins; a second one is usually a broken copy.
            if (header.Metadata.Exif.IsEmpty)
            {
                header.Metadata.Exif = payload[ExifId.Length..].ToArray();
                header.HasMetadata = true;
            }
        }
        else if (payload.StartsWith(XmpId))
        {
            header.Metadata.Xmp = payload[XmpId.Length..].ToArray();
            header.HasMetadata = true;
        }
        else if (payload.StartsWith(ExtendedXmpId) && payload.Length >= ExtendedXmpId.Length + 40)
        {
            ReadOnlySpan<byte> part = payload[ExtendedXmpId.Length..];
            string guid = Encoding.ASCII.GetString(part[..32]);
            uint total = BinaryPrimitives.ReadUInt32BigEndian(part[32..]);
            uint offset = BinaryPrimitives.ReadUInt32BigEndian(part[36..]);
            if (extended is null)
            {
                // The packet's stated size is allocated up front, so it must fit the metadata budget first.
                if (total > maxBytes)
                {
                    header.Metadata.Warnings.Add($"The extended XMP packet says it is {total:N0} bytes, over the metadata limit. It was dropped.");
                    return;
                }

                extended = new ExtendedXmpParts(guid, total);
            }

            if (extended.Guid == guid && extended.Total == total)
            {
                extended.Add(offset, part[40..]);
                header.HasMetadata = true;
            }
        }
    }

    private static void ReadIcc(JpegHeader header, ReadOnlySpan<byte> payload, Dictionary<int, byte[]> chunks, ref int count)
    {
        if (payload.Length < IccId.Length + 2 || !payload.StartsWith(IccId))
        {
            return;
        }

        int sequence = payload[IccId.Length];
        int of = payload[IccId.Length + 1];
        if (sequence < 1 || of < 1 || sequence > of)
        {
            return;
        }

        count = Math.Max(count, of);
        chunks.TryAdd(sequence, payload[(IccId.Length + 2)..].ToArray());
        header.HasMetadata = true;
    }

    private static void FinishMetadata(JpegHeader header, Dictionary<int, byte[]> iccChunks, int iccCount, ExtendedXmpParts? extended)
    {
        header.Details.ColorSpace = header.ColorSpace;
        header.Details.RestartInterval = header.RestartInterval;
        header.Details.EstimatedQuality = EstimateQuality(header);
        if (iccCount > 0)
        {
            if (iccChunks.Count == iccCount)
            {
                using MemoryStream profile = new();
                for (int i = 1; i <= iccCount; i++)
                {
                    profile.Write(iccChunks[i]);
                }

                header.Metadata.IccProfile = profile.ToArray();
                header.Metadata.ColorSpace = ColorSpaceHint.EmbeddedIcc;
            }
            else
            {
                header.Metadata.Warnings.Add($"The ICC profile has {iccChunks.Count} of its {iccCount} parts. It was dropped.");
            }
        }

        if (extended is not null)
        {
            if (extended.IsComplete)
            {
                header.Details.ExtendedXmp = extended.Data;
            }
            else
            {
                header.Metadata.Warnings.Add("The extended XMP packet is missing parts. It was dropped.");
            }
        }

        header.Metadata.FormatDetails = header.Details;
    }

    /// <summary>
    /// Estimates the IJG quality from the luma quantization table: the quality whose scaled Annex K table (T.81 table K.1) is
    /// closest. Only meaningful for files made with the IJG formula, which most are.
    /// </summary>
    private static int? EstimateQuality(JpegHeader header)
    {
        if (header.Components.Length == 0 || header.QuantTables[header.Components[0].QuantTable] is not ushort[] table)
        {
            return null;
        }

        int best = 0;
        long bestError = long.MaxValue;
        for (int quality = 1; quality <= 100; quality++)
        {
            int factor = quality < 50 ? 5000 / quality : 200 - (2 * quality);
            long error = 0;
            for (int k = 0; k < 64; k++)
            {
                int expected = Math.Clamp(((JpegTables.LumaQuant[JpegTables.ZigZag[k]] * factor) + 50) / 100, 1, 255);
                error += Math.Abs(table[k] - expected);
            }

            if (error < bestError)
            {
                bestError = error;
                best = quality;
            }
        }

        return best;
    }

    /// <summary>Decodes a comment as UTF-8 when it is valid, else as Latin-1, which older encoders wrote.</summary>
    private static string DecodeText(ReadOnlySpan<byte> bytes)
    {
        int end = bytes.IndexOf((byte)0);
        if (end >= 0)
        {
            bytes = bytes[..end];
        }

        return System.Text.Unicode.Utf8.IsValid(bytes) ? Encoding.UTF8.GetString(bytes) : Encoding.Latin1.GetString(bytes);
    }

    /// <summary>The parts of an extended XMP packet, each at its offset in the whole.</summary>
    private sealed class ExtendedXmpParts(string guid, uint total)
    {
        private readonly byte[] _data = new byte[total];
        private long _filled;

        internal string Guid => guid;

        internal uint Total => total;

        internal bool IsComplete => _filled >= _data.Length;

        internal byte[] Data => _data;

        internal void Add(uint offset, ReadOnlySpan<byte> part)
        {
            if (offset < _data.Length && part.Length <= _data.Length - offset)
            {
                part.CopyTo(_data.AsSpan((int)offset));
                _filled += part.Length;
            }
        }
    }
}
