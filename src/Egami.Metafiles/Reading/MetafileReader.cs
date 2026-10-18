using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace Egami.Metafiles.Reading;

/// <summary>
/// Reads the three containers from the Microsoft Open Specifications: [MS-WMF] (an optional placeable header, the META_HEADER,
/// then 16-bit records sized in words), [MS-EMF] (EMR_HEADER, then records with 32-bit types and sizes) and [MS-EMFPLUS] (records
/// inside EMR_COMMENT, objects split over several EmfPlusObject records). It checks every size against the file and never
/// follows a size past it: a record that does not fit ends the walk with a warning, and what was read before it stays.
/// </summary>
internal static class MetafileReader
{
    /// <summary>The placeable WMF key.</summary>
    internal const uint PlaceableKey = 0x9AC6CDD7;

    /// <summary>The EMF header's signature, " EMF".</summary>
    internal const uint EmfSignature = 0x464D4520;

    /// <summary>The EMF+ comment identifier, "EMF+".</summary>
    internal const uint EmfPlusIdentifier = 0x2B464D45;

    internal const int EmrHeader = 1;
    internal const int EmrEof = 14;
    internal const int EmrComment = 70;
    internal const int WmfEof = 0x0000;
    internal const int WmfSetWindowExt = 0x020C;
    internal const ushort PlusHeader = 0x4001;
    internal const ushort PlusObject = 0x4008;

    /// <summary>Returns whether the bytes start like a WMF (placeable or not) or EMF file.</summary>
    internal static bool LooksLikeMetafile(ReadOnlySpan<byte> data) =>
        (data.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(data) == PlaceableKey)
        || (data.Length >= 44 && BinaryPrimitives.ReadUInt32LittleEndian(data) == EmrHeader && BinaryPrimitives.ReadUInt32LittleEndian(data[40..]) == EmfSignature)
        || (data.Length >= 6 && BinaryPrimitives.ReadUInt16LittleEndian(data) is 1 or 2 && BinaryPrimitives.ReadUInt16LittleEndian(data[2..]) == 9
            && BinaryPrimitives.ReadUInt16LittleEndian(data[4..]) is 0x0100 or 0x0300);

    /// <summary>Returns whether the bytes are gzip data (EMZ, WMZ).</summary>
    internal static bool IsGzip(ReadOnlySpan<byte> data) => data.Length >= 3 && data[0] == 0x1F && data[1] == 0x8B && data[2] == 8;

    /// <summary>Reads a stream to its end, inflating gzip, within the context's total byte limit.</summary>
    internal static byte[] ReadAll(Stream stream, SafetyLimits limits)
    {
        byte[] data = ReadBounded(stream, limits.MaxTotalBytes);
        return IsGzip(data) ? Inflate(new MemoryStream(data, writable: false), limits) : data;
    }

    /// <summary>Copies the bytes, inflating gzip, within the context's total byte limit.</summary>
    internal static byte[] ReadAll(ReadOnlyMemory<byte> data, SafetyLimits limits)
    {
        if (data.Length > limits.MaxTotalBytes)
        {
            throw new LimitExceededException(nameof(SafetyLimits.MaxTotalBytes), limits.MaxTotalBytes, data.Length);
        }

        if (!IsGzip(data.Span))
        {
            return data.ToArray();
        }

        MemoryStream input = MemoryMarshal.TryGetArray(data, out ArraySegment<byte> segment)
            ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false)
            : new MemoryStream(data.ToArray(), writable: false);
        return Inflate(input, limits);
    }

    private static byte[] Inflate(MemoryStream input, SafetyLimits limits)
    {
        using GZipStream gzip = new(input, CompressionMode.Decompress);
        try
        {
            return ReadBounded(gzip, limits.MaxTotalBytes);
        }
        catch (InvalidDataException e)
        {
            throw new CorruptDataException("Metafile", 0, "The gzip data (EMZ or WMZ) cannot be inflated.", e);
        }
    }

    private static byte[] ReadBounded(Stream stream, long max)
    {
        using MemoryStream copy = new();
        byte[] buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (copy.Length + read > max)
            {
                throw new LimitExceededException(nameof(SafetyLimits.MaxTotalBytes), max, copy.Length + read);
            }

            copy.Write(buffer, 0, read);
        }

        return copy.ToArray();
    }

    /// <summary>Reads a WMF or EMF file's container.</summary>
    /// <exception cref="CorruptDataException">The data is no WMF or EMF file, or its header is broken.</exception>
    internal static MetafileContent Read(byte[] data, MetafileReport report)
    {
        ReadOnlySpan<byte> span = data;
        if (span.Length >= 44 && U32(span, 0) == EmrHeader && U32(span, 40) == EmfSignature)
        {
            return ReadEmf(data, report);
        }

        if (span.Length >= 4 && U32(span, 0) == PlaceableKey)
        {
            return ReadWmf(data, 22, report);
        }

        if (LooksLikeMetafile(span))
        {
            return ReadWmf(data, 0, report);
        }

        throw new CorruptDataException("Metafile", 0, "The data is not a WMF or EMF file.");
    }

    private static MetafileContent ReadWmf(byte[] data, int start, MetafileReport report)
    {
        ReadOnlySpan<byte> span = data;
        (short, short, short, short)? box = null;
        int inch = 0;
        if (start == 22)
        {
            if (span.Length < 22 + 18)
            {
                throw new CorruptDataException("WMF", 0, "The file ends inside its headers.");
            }

            box = (I16(span, 6), I16(span, 8), I16(span, 10), I16(span, 12));
            inch = U16(span, 14);
            ushort checksum = 0;
            for (int i = 0; i < 20; i += 2)
            {
                checksum ^= U16(span, i);
            }

            if (checksum != U16(span, 20))
            {
                report.Warn("Metafile.BadChecksum", "The placeable header's checksum does not match; the header is used anyway, as Windows does.", 20);
            }

            if (inch == 0)
            {
                report.Warn("Metafile.BadHeader", "The placeable header gives 0 units per inch; 1440 (twips) is assumed.", 14);
                inch = 1440;
            }
        }

        if (span.Length < start + 18)
        {
            throw new CorruptDataException("WMF", start, "The file ends inside its header.");
        }

        int type = U16(span, start), headerWords = U16(span, start + 2), version = U16(span, start + 4);
        if (type is not (1 or 2) || headerWords != 9 || version is not (0x0100 or 0x0300))
        {
            throw new CorruptDataException("WMF", start, "The META_HEADER is not valid (type, header size or version).");
        }

        int objects = U16(span, start + 10);
        List<MetaRecord> records = [new MetaRecord(-1, start, 18)];
        (short, short)? extent = null;
        int at = start + 18;
        while (true)
        {
            if (at + 6 > span.Length)
            {
                report.Warn("Metafile.NoEndRecord", "The file ends without a META_EOF record; the records before the end are used.", at);
                break;
            }

            long words = U32(span, at);
            int function = U16(span, at + 4);
            if (words < 3 || (words * 2) > span.Length - at)
            {
                report.Warn("Metafile.TruncatedRecord", $"A record (function 0x{function:X4}) claims {words} words, which do not fit the file; reading stops there.", at);
                break;
            }

            int size = (int)(words * 2);
            records.Add(new MetaRecord(function, at, size));
            if (function == WmfSetWindowExt && extent is null && size >= 10)
            {
                extent = (I16(span, at + 8), I16(span, at + 6)); // y comes first in the record
            }

            if (function == WmfEof)
            {
                break;
            }

            at += size;
        }

        return new MetafileContent
        {
            Data = data,
            Kind = MetafileKind.Wmf,
            Records = records,
            ObjectCount = objects,
            PlaceableBox = box,
            UnitsPerInch = inch,
            WindowExtent = extent,
        };
    }

    private static MetafileContent ReadEmf(byte[] data, MetafileReport report)
    {
        ReadOnlySpan<byte> span = data;
        int headerSize = (int)Math.Min(U32(span, 4), int.MaxValue);
        if (headerSize < 88 || headerSize > span.Length)
        {
            throw new CorruptDataException("EMF", 4, "The EMR_HEADER record's size is not valid.");
        }

        (int, int, int, int) bounds = (I32(span, 8), I32(span, 12), I32(span, 16), I32(span, 20));
        (int, int, int, int) frame = (I32(span, 24), I32(span, 28), I32(span, 32), I32(span, 36));
        int headerRecords = I32(span, 52);
        int handles = U16(span, 56);
        string? description = null;
        long chars = U32(span, 60), offset = U32(span, 64);
        if (chars > 0)
        {
            if (offset >= 88 && offset + (chars * 2) <= headerSize)
            {
                description = Encoding.Unicode.GetString(span.Slice((int)offset, (int)chars * 2)).TrimEnd('\0').Replace('\0', '\n');
            }
            else
            {
                report.Warn("Metafile.BadHeader", "The EMF description lies outside the header record; it is ignored.", 60);
            }
        }

        List<MetaRecord> records = [];
        List<PlusRecord> plus = [];
        PlusReader plusReader = new(report, data.Length);
        int at = 0;
        while (true)
        {
            if (at + 8 > span.Length)
            {
                report.Warn("Metafile.NoEndRecord", "The file ends without an EMR_EOF record; the records before the end are used.", at);
                break;
            }

            uint type = U32(span, at);
            long size = U32(span, at + 4);
            if (size < 8 || (size & 3) != 0 || size > span.Length - at)
            {
                report.Warn("Metafile.TruncatedRecord", $"A record (type {type}) claims {size} bytes, which do not fit the file; reading stops there.", at);
                break;
            }

            int index = records.Count;
            records.Add(new MetaRecord((int)Math.Min(type, int.MaxValue), at, (int)size));
            if (type == EmrComment && size >= 16)
            {
                long dataSize = U32(span, at + 8);
                if (dataSize >= 4 && dataSize <= size - 12 && U32(span, at + 12) == EmfPlusIdentifier)
                {
                    plusReader.Read(data, at + 16, (int)dataSize - 4, index, plus);
                }
            }

            if (type == EmrEof)
            {
                break;
            }

            at += (int)size;
        }

        MetafileKind kind = plusReader.HeaderFlags switch
        {
            null => MetafileKind.Emf,
            ushort flags when (flags & 1) != 0 => MetafileKind.EmfPlusDual,
            _ => MetafileKind.EmfPlusOnly,
        };
        return new MetafileContent
        {
            Data = data,
            Kind = kind,
            Records = records,
            PlusRecords = plus,
            ObjectCount = handles,
            Bounds = bounds,
            Frame = frame,
            DevicePixels = (I32(span, 72), I32(span, 76)),
            DeviceMillimeters = (I32(span, 80), I32(span, 84)),
            HeaderRecordCount = headerRecords,
            Description = description,
            PlusDpi = plusReader.Dpi,
        };
    }

    /// <summary>Walks the EMF+ records of the comments in order, joining objects that are split over several records.</summary>
    private sealed class PlusReader(MetafileReport report, long fileLength)
    {
        private byte[]? _pending;
        private int _pendingFilled;
        private ushort _pendingFlags;

        /// <summary>Gets the EmfPlusHeader record's flags (bit 0: dual), or null before one is read.</summary>
        public ushort? HeaderFlags { get; private set; }

        /// <summary>Gets the EmfPlusHeader's logical DPI.</summary>
        public (int X, int Y) Dpi { get; private set; }

        public void Read(byte[] data, int start, int length, int emfIndex, List<PlusRecord> records)
        {
            ReadOnlySpan<byte> span = data.AsSpan(start, length);
            int at = 0;
            while (at + 12 <= span.Length)
            {
                ushort type = U16(span, at), flags = U16(span, at + 2);
                long size = U32(span, at + 4), dataSize = U32(span, at + 8);
                if (size < 12 || dataSize > size - 12 || size > span.Length - at)
                {
                    report.Warn("Metafile.TruncatedRecord", $"An EMF+ record (type 0x{type:X4}) claims {size} bytes, which do not fit its comment; the rest of the comment is skipped.", start + at);
                    return;
                }

                ReadOnlyMemory<byte> body = data.AsMemory(start + at + 12, (int)dataSize);
                if (type == PlusHeader && HeaderFlags is null)
                {
                    HeaderFlags = flags;
                    if (dataSize >= 16)
                    {
                        Dpi = (I32(body.Span, 8), I32(body.Span, 12));
                    }
                }

                if (type == PlusObject && (flags & 0x8000) != 0)
                {
                    Continue(body.Span, flags, emfIndex, records, start + at);
                }
                else
                {
                    if (_pending is not null)
                    {
                        report.Warn("Metafile.BadObject", "An EMF+ object split over several records ends before its last piece; it is skipped.", start + at);
                        _pending = null;
                    }

                    records.Add(new PlusRecord(type, flags, body, emfIndex));
                }

                at += (int)size;
            }
        }

        /// <summary>Adds one piece of a continued object: TotalObjectSize, then the piece's bytes.</summary>
        private void Continue(ReadOnlySpan<byte> body, ushort flags, int emfIndex, List<PlusRecord> records, long offset)
        {
            if (body.Length < 4)
            {
                report.Warn("Metafile.BadObject", "A continued EMF+ object record has no TotalObjectSize; it is skipped.", offset);
                return;
            }

            long total = U32(body, 0);
            ReadOnlySpan<byte> piece = body[4..];
            if (_pending is null || (_pendingFlags & 0x7FFF) != (flags & 0x7FFF) || _pending.Length != total)
            {
                if (_pending is not null)
                {
                    report.Warn("Metafile.BadObject", "An EMF+ object split over several records was interrupted by another; the first is skipped.", offset);
                }

                // Pieces never add up to more than the file holds, so a larger total is a broken record, not a reason to allocate.
                if (total > fileLength)
                {
                    report.Warn("Metafile.BadObject", $"A continued EMF+ object claims {total} bytes, more than the file can hold; it is skipped.", offset);
                    _pending = null;
                    return;
                }

                _pending = new byte[total];
                _pendingFilled = 0;
                _pendingFlags = flags;
            }

            int take = (int)Math.Min(piece.Length, _pending.Length - _pendingFilled);
            piece[..take].CopyTo(_pending.AsSpan(_pendingFilled));
            _pendingFilled += take;
            if (_pendingFilled == _pending.Length)
            {
                records.Add(new PlusRecord(PlusObject, (ushort)(flags & 0x7FFF), _pending, emfIndex));
                _pending = null;
            }
        }
    }

    internal static ushort U16(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadUInt16LittleEndian(s[at..]);

    internal static short I16(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadInt16LittleEndian(s[at..]);

    internal static uint U32(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadUInt32LittleEndian(s[at..]);

    internal static int I32(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadInt32LittleEndian(s[at..]);

    internal static float F32(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadSingleLittleEndian(s[at..]);
}
