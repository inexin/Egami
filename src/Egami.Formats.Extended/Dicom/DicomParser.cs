using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Egami.Formats.Extended.Dicom;

/// <summary>
/// Reads a DICOM file's bytes into datasets (PS3.10 section 7, PS3.5 sections 7 and A.4): the preamble and DICM prefix, the file
/// meta information in explicit VR little endian, then the dataset in its transfer syntax, with sequences, items and delimiters
/// of defined or undefined length, and encapsulated pixel data kept as fragments. Every length is checked against the data, the
/// nesting against <see cref="MaxDepth"/> and the element count against the load's memory budget. A lenient read keeps what it
/// read before damage and says why it stopped; a strict one throws.
/// </summary>
internal sealed class DicomParser
{
    /// <summary>The deepest nesting of sequences read.</summary>
    public const int MaxDepth = 64;

    private const uint Undefined = 0xFFFFFFFF;
    private const uint ItemTag = 0xFFFEE000;
    private const uint ItemDelimiter = 0xFFFEE00D;
    private const uint SequenceDelimiter = 0xFFFEE0DD;

    /// <summary>The memory an element takes beyond its bytes, counted against the load's budget.</summary>
    private const int ElementCost = 160;

    private readonly EgamiInput _input;
    private readonly bool _strict;
    private readonly List<EgamiDiagnostic> _warnings = [];
    private readonly HashSet<string> _warned = [];
    private long _elements;
    private long _budgetBase;
    private bool _stopped;

    private DicomParser(EgamiInput input, bool strict)
    {
        _input = input;
        _strict = strict;
    }

    /// <summary>A file read: what <see cref="DicomFile"/> holds.</summary>
    internal sealed record Result(bool HasPreamble, DicomDataset Meta, DicomDataset Dataset, DicomTransferSyntax TransferSyntax,
        IReadOnlyList<EgamiDiagnostic> Warnings);

    /// <summary>Returns whether data starts as a DICOM Part 10 file: 128 bytes, then DICM.</summary>
    public static bool IsPart10(ReadOnlySpan<byte> header)
    {
        return header.Length >= 132 && header.Slice(128, 4).SequenceEqual("DICM"u8);
    }

    /// <summary>Reads a whole file held in memory.</summary>
    public static Result Parse(ReadOnlyMemory<byte> data, EgamiInput input, ReaderOptions options)
    {
        DicomParser parser = new(input, options.Recovery == RecoveryMode.Strict) { _budgetBase = data.Length };
        return parser.ParseFile(data);
    }

    private Result ParseFile(ReadOnlyMemory<byte> data)
    {
        ReadOnlySpan<byte> span = data.Span;
        bool preamble = IsPart10(span);
        int pos;
        if (preamble)
        {
            pos = 132;
        }
        else if (span.StartsWith("DICM"u8))
        {
            Note("Dicom.NoPreamble", "The file starts with DICM, without the 128-byte preamble; it was read from there.", 0);
            pos = 4;
        }
        else
        {
            pos = 0;
            if (!LooksLikeDataset(span, out _, out _))
            {
                throw new CorruptDataException("DICOM", 0, "The data is not DICOM: it has no DICM prefix at byte 128 and does not start with a data element.");
            }

            Note("Dicom.NoPreamble", "The file has no preamble and DICM prefix; it was read as a bare dataset.", 0);
        }

        // The file meta information: group 0002, explicit VR little endian (PS3.10 7.1).
        DicomDataset meta = DicomDataset.Empty;
        if (span.Length - pos >= 8 && BinaryPrimitives.ReadUInt16LittleEndian(span[pos..]) == 0x0002)
        {
            bool explicitMeta = IsVR(span[(pos + 4)..]);
            if (!explicitMeta)
            {
                Note("Dicom.ImplicitMeta", "The file meta information is in implicit VR, against PS3.10; it was read as written.", pos);
            }

            meta = ReadDataset(data, ref pos, data.Length, new Mode(explicitMeta, false), DicomCharacterSet.Default, 0, item: false, metaOnly: true);
        }
        else if (preamble)
        {
            Damage("Dicom.NoMeta", "The file has no file meta information (group 0002) after DICM.", pos);
        }

        DicomTransferSyntax syntax;
        string? uid = meta[DicomTag.TransferSyntaxUid]?.LooseText();
        if (!string.IsNullOrEmpty(uid))
        {
            syntax = DicomTransferSyntax.FromUid(uid);
        }
        else
        {
            bool explicitVR = LooksLikeDataset(span[pos..], out bool bigEndian, out bool isExplicit) && isExplicit;
            syntax = !explicitVR ? DicomTransferSyntax.ImplicitVRLittleEndian
                : bigEndian ? DicomTransferSyntax.ExplicitVRBigEndian
                : DicomTransferSyntax.ExplicitVRLittleEndian;
            if (meta.Count > 0)
            {
                Note("Dicom.NoTransferSyntax", $"The file meta information has no Transfer Syntax UID; the dataset reads as {syntax.Name}.", pos);
            }
        }

        ReadOnlyMemory<byte> body = data[pos..];
        if (syntax.IsDeflated)
        {
            body = Inflate(body.Span, pos);
        }

        Mode mode = new(syntax.IsExplicitVR, syntax.IsBigEndian);
        if (body.Length >= 8 && !syntax.IsDeflated)
        {
            mode = CheckMode(body.Span, mode, syntax);
        }

        int at = 0;
        DicomDataset dataset = ReadDataset(body, ref at, body.Length, mode, DicomCharacterSet.Default, 0, item: false, metaOnly: false);
        return new Result(preamble, meta, dataset, syntax, _warnings);
    }

    /// <summary>Checks the first element against the transfer syntax, for files that state the wrong one.</summary>
    private Mode CheckMode(ReadOnlySpan<byte> body, Mode mode, DicomTransferSyntax syntax)
    {
        bool letters = IsVR(body[4..]);
        if (mode.Explicit && !letters)
        {
            Note("Dicom.TransferSyntaxMismatch", $"The transfer syntax is {syntax.Name}, but the dataset is in implicit VR; it was read as implicit.", 0);
            return mode with { Explicit = false };
        }

        if (!mode.Explicit && letters && BinaryPrimitives.ReadUInt32LittleEndian(body[4..]) > body.Length - 8)
        {
            Note("Dicom.TransferSyntaxMismatch", $"The transfer syntax is {syntax.Name}, but the dataset is in explicit VR; it was read as explicit.", 0);
            return mode with { Explicit = true };
        }

        return mode;
    }

    /// <summary>Reads elements until the end, an item delimiter (in an item), or the end of group 0002 (for the file meta information).</summary>
    private DicomDataset ReadDataset(ReadOnlyMemory<byte> data, ref int pos, int end, Mode mode, DicomCharacterSet characters, int depth,
        bool item, bool metaOnly)
    {
        List<DicomElement> elements = [];
        int pixelRepresentation = 0;
        uint previous = 0;
        while (pos < end && !_stopped)
        {
            ReadOnlySpan<byte> span = data.Span;
            if (end - pos < 8)
            {
                if (span[pos..end].ContainsAnyExcept((byte)0))
                {
                    Damage("Dicom.TrailingBytes", $"{end - pos} bytes after the last element are too few for another; they were ignored.", pos);
                }

                pos = end;
                break;
            }

            uint tag = ReadTag(span[pos..], mode.BigEndian);
            if (metaOnly && tag >> 16 != 0x0002)
            {
                break;
            }

            if (tag == ItemDelimiter)
            {
                if (item)
                {
                    pos += 8;
                    return new DicomDataset(elements);
                }

                Damage("Dicom.StrayDelimiter", "An item delimiter outside an item was skipped.", pos);
                pos += 8;
                continue;
            }

            if (tag == SequenceDelimiter || tag == ItemTag)
            {
                if (item)
                {
                    // The item's own delimiter is missing; leave this for the sequence to read.
                    Damage("Dicom.MissingDelimiter", "An item of undefined length ended without its delimiter.", pos);
                    return new DicomDataset(elements);
                }

                Damage("Dicom.StrayDelimiter", "A sequence delimiter or item outside a sequence was skipped.", pos);
                pos += 8;
                continue;
            }

            if (tag < previous)
            {
                Note("Dicom.TagOrder", "Elements are not in ascending tag order; they were kept in file order.", pos);
            }

            previous = tag;
            DicomTag dicomTag = new((ushort)(tag >> 16), (ushort)tag);
            int start = pos;
            DicomVR vr;
            uint length;
            int header;
            bool implicitSequence = false;
            if (mode.Explicit && IsVR(span[(pos + 4)..]))
            {
                vr = ParseVR(span[(pos + 4)..], out bool known);
                if (!known)
                {
                    Note("Dicom.UnknownVR", $"{dicomTag} has the VR '{Encoding.ASCII.GetString(span.Slice(pos + 4, 2))}', which this version does not know; it was read as UN.", pos);
                }

                if (!known || IsLong(vr))
                {
                    if (end - pos < 12)
                    {
                        Damage("Dicom.Truncated", $"The data ends inside the header of {dicomTag}.", pos);
                        pos = end;
                        break;
                    }

                    length = mode.BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(span[(pos + 8)..]) : BinaryPrimitives.ReadUInt32LittleEndian(span[(pos + 8)..]);
                    header = 12;
                }
                else
                {
                    length = mode.BigEndian ? BinaryPrimitives.ReadUInt16BigEndian(span[(pos + 6)..]) : BinaryPrimitives.ReadUInt16LittleEndian(span[(pos + 6)..]);
                    header = 8;
                }

                if (vr == DicomVR.UN && length == Undefined)
                {
                    // An unknown element of undefined length is a sequence in implicit VR little endian (PS3.5 6.2.2).
                    implicitSequence = true;
                }
            }
            else
            {
                if (mode.Explicit)
                {
                    Note("Dicom.ImplicitElement", $"{dicomTag} has no VR in an explicit VR dataset; it was read as implicit VR.", pos);
                }

                length = mode.BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(span[(pos + 4)..]) : BinaryPrimitives.ReadUInt32LittleEndian(span[(pos + 4)..]);
                header = 8;
                vr = ImplicitVR(dicomTag, pixelRepresentation, length);
            }

            pos += header;
            Count(start);
            DicomElement element;
            if (vr == DicomVR.SQ || implicitSequence)
            {
                if (depth >= MaxDepth)
                {
                    throw new CorruptDataException("DICOM", start, $"Sequences nest deeper than {MaxDepth} levels.");
                }

                Mode itemMode = implicitSequence ? new Mode(false, false) : mode;
                int sequenceEnd = end;
                if (length != Undefined)
                {
                    sequenceEnd = Bound(pos, length, end, dicomTag, start);
                }

                List<DicomDataset> items = ReadItems(data, ref pos, sequenceEnd, length == Undefined, itemMode, characters, depth + 1);
                if (length != Undefined)
                {
                    pos = sequenceEnd;
                }

                element = new DicomElement(dicomTag, DicomVR.SQ, ReadOnlyMemory<byte>.Empty, mode.BigEndian, characters, start, items);
            }
            else if (length == Undefined)
            {
                if (dicomTag != DicomTag.PixelData)
                {
                    Damage("Dicom.UndefinedLength", $"{dicomTag} ({vr}) has an undefined length, which only sequences and pixel data may have.", start);
                    _stopped = true;
                    break;
                }

                DicomFragments fragments = ReadFragments(data, ref pos, end, mode.BigEndian);
                element = new DicomElement(dicomTag, vr, ReadOnlyMemory<byte>.Empty, mode.BigEndian, characters, start, fragments: fragments);
            }
            else
            {
                int valueEnd = Bound(pos, length, end, dicomTag, start);
                if ((length & 1) != 0)
                {
                    Note("Dicom.OddLength", $"{dicomTag} has an odd length ({length}); values should be padded to even lengths.", start);
                }

                element = new DicomElement(dicomTag, vr, data[pos..valueEnd], mode.BigEndian, characters, start);
                pos = valueEnd;
                if (dicomTag == DicomTag.SpecificCharacterSet)
                {
                    string terms = element.LooseText() ?? "";
                    characters = DicomCharacterSet.From(terms.Length == 0 ? [] : [.. terms.Split('\\').Select(t => t.Trim(' ', '\0'))], out string? unknown);
                    if (unknown is not null)
                    {
                        Note("Dicom.CharacterSet", $"The character set '{unknown}' is not one DICOM defines; such text was read as Latin-1.", start);
                    }
                }
                else if (tag == 0x00280103 && element.RawValue.Length >= 2)
                {
                    pixelRepresentation = mode.BigEndian ? BinaryPrimitives.ReadUInt16BigEndian(element.RawValue.Span) : BinaryPrimitives.ReadUInt16LittleEndian(element.RawValue.Span);
                }
            }

            elements.Add(element);
        }

        if (item && !metaOnly && pos >= end && !_stopped && end == data.Length)
        {
            // An item of undefined length that runs to the end of the data.
            Damage("Dicom.MissingDelimiter", "An item of undefined length ended with the data, without its delimiter.", pos);
        }

        return new DicomDataset(elements);
    }

    /// <summary>Reads a sequence's items, to its end (defined length) or its delimiter.</summary>
    private List<DicomDataset> ReadItems(ReadOnlyMemory<byte> data, ref int pos, int end, bool undefined, Mode mode, DicomCharacterSet characters, int depth)
    {
        List<DicomDataset> items = [];
        while (!_stopped)
        {
            ReadOnlySpan<byte> span = data.Span;
            if (end - pos < 8)
            {
                if (undefined)
                {
                    Damage("Dicom.MissingDelimiter", "A sequence of undefined length ended with the data, without its delimiter.", pos);
                }
                else if (end > pos)
                {
                    Damage("Dicom.Truncated", "A sequence ends with bytes too few for another item; they were ignored.", pos);
                }

                pos = end;
                break;
            }

            uint tag = ReadTag(span[pos..], mode.BigEndian);
            uint length = mode.BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(span[(pos + 4)..]) : BinaryPrimitives.ReadUInt32LittleEndian(span[(pos + 4)..]);
            if (tag == SequenceDelimiter)
            {
                pos += 8;
                if (!undefined)
                {
                    Note("Dicom.StrayDelimiter", "A sequence of defined length had a sequence delimiter; it was skipped.", pos - 8);
                    continue;
                }

                break;
            }

            if (tag != ItemTag)
            {
                if (undefined)
                {
                    // The sequence delimiter is missing; the next element belongs to the dataset around the sequence.
                    Damage("Dicom.MissingDelimiter", "A sequence of undefined length ended without its delimiter.", pos);
                    break;
                }

                Damage("Dicom.BadItem", $"A sequence holds {new DicomTag((ushort)(tag >> 16), (ushort)tag)} where an item belongs; the rest of the sequence was skipped.", pos);
                pos = end;
                break;
            }

            int start = pos;
            pos += 8;
            Count(start);
            if (length == Undefined)
            {
                items.Add(ReadDataset(data, ref pos, end, mode, characters, depth, item: true, metaOnly: false));
            }
            else
            {
                int itemEnd = Bound(pos, length, end, new DicomTag(0xFFFE, 0xE000), start);
                items.Add(ReadDataset(data, ref pos, itemEnd, mode, characters, depth, item: false, metaOnly: false));
                pos = itemEnd;
            }
        }

        return items;
    }

    /// <summary>Reads encapsulated pixel data: items of defined length, the first the Basic Offset Table, up to the sequence delimiter.</summary>
    private DicomFragments ReadFragments(ReadOnlyMemory<byte> data, ref int pos, int end, bool bigEndian)
    {
        List<ReadOnlyMemory<byte>> fragments = [];
        uint[]? offsets = null;
        while (!_stopped)
        {
            ReadOnlySpan<byte> span = data.Span;
            if (end - pos < 8)
            {
                Damage("Dicom.MissingDelimiter", "Encapsulated pixel data ended with the data, without its sequence delimiter.", pos);
                pos = end;
                break;
            }

            uint tag = ReadTag(span[pos..], bigEndian);
            uint length = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(span[(pos + 4)..]) : BinaryPrimitives.ReadUInt32LittleEndian(span[(pos + 4)..]);
            if (tag == SequenceDelimiter)
            {
                pos += 8;
                break;
            }

            if (tag != ItemTag || length == Undefined)
            {
                Damage("Dicom.BadFragment", "Encapsulated pixel data holds something other than an item of defined length; the rest was skipped.", pos);
                _stopped = true;
                break;
            }

            int start = pos;
            pos += 8;
            Count(start);
            int valueEnd = Bound(pos, length, end, new DicomTag(0xFFFE, 0xE000), start);
            ReadOnlyMemory<byte> value = data[pos..valueEnd];
            pos = valueEnd;
            if (offsets is null)
            {
                ReadOnlySpan<byte> table = value.Span;
                offsets = new uint[table.Length / 4];
                for (int i = 0; i < offsets.Length; i++)
                {
                    offsets[i] = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(table[(i * 4)..]) : BinaryPrimitives.ReadUInt32LittleEndian(table[(i * 4)..]);
                }
            }
            else
            {
                fragments.Add(value);
            }
        }

        return new DicomFragments(offsets ?? [], [.. fragments]);
    }

    /// <summary>Returns where a value of <paramref name="length"/> bytes at <paramref name="pos"/> ends, cut to <paramref name="end"/> when the data is shorter.</summary>
    private int Bound(int pos, uint length, int end, DicomTag tag, int start)
    {
        if (length <= (uint)(end - pos))
        {
            return pos + (int)length;
        }

        Damage("Dicom.Truncated", $"{tag} says it has {length} bytes, but only {end - pos} follow; it was cut there.", start);
        _stopped = end == pos || _stopped;
        return end;
    }

    /// <summary>Counts an element against the load's memory budget, and checks for cancellation now and then.</summary>
    private void Count(int offset)
    {
        _elements++;
        if ((_elements & 1023) == 0)
        {
            _input.CancellationToken.ThrowIfCancellationRequested();
            _input.CheckMemory(_budgetBase + (_elements * ElementCost));
        }

        _ = offset;
    }

    private ReadOnlyMemory<byte> Inflate(ReadOnlySpan<byte> compressed, int offset)
    {
        byte[] copy = compressed.ToArray();
        bool zlib = copy.Length >= 2 && (copy[0] & 0x0F) == 8 && ((copy[0] << 8) | copy[1]) % 31 == 0;
        if (zlib)
        {
            Note("Dicom.ZlibHeader", "The deflated dataset has a zlib header, against PS3.5 A.5; it was read anyway.", offset);
        }

        using MemoryStream source = new(copy, false);
        using Stream inflater = zlib ? new ZLibStream(source, CompressionMode.Decompress) : new DeflateStream(source, CompressionMode.Decompress);
        byte[] buffer = new byte[Math.Max(4096, Math.Min(copy.Length * 4L, 1 << 20))];
        int length = 0;
        try
        {
            while (true)
            {
                if (length == buffer.Length)
                {
                    long grown = Math.Min((long)buffer.Length * 2, Array.MaxLength);
                    if (grown <= buffer.Length)
                    {
                        throw new LimitExceededException(nameof(SafetyLimits.MaxTotalBytes), Array.MaxLength, grown);
                    }

                    _input.CheckMemory(_budgetBase + grown);
                    Array.Resize(ref buffer, (int)grown);
                }

                int read = inflater.Read(buffer, length, buffer.Length - length);
                if (read == 0)
                {
                    break;
                }

                length += read;
            }
        }
        catch (InvalidDataException e)
        {
            Damage("Dicom.Deflate", $"The deflated dataset is damaged ({e.Message}); what inflated was read.", offset);
        }

        _budgetBase += length;
        return buffer.AsMemory(0, length);
    }

    /// <summary>Returns the VR an implicit VR element has: PS3.6's, with the choices it leaves resolved.</summary>
    private static DicomVR ImplicitVR(DicomTag tag, int pixelRepresentation, uint length)
    {
        if (tag.Element == 0x0000)
        {
            return DicomVR.UL;
        }

        if (tag.IsPrivate)
        {
            return tag.Element is >= 0x0010 and <= 0x00FF ? DicomVR.LO : DicomVR.UN;
        }

        DicomDictionaryEntry? entry = DicomDictionary.Find(tag);
        if (entry is null || entry.VRs.Length == 0)
        {
            return length == Undefined ? DicomVR.SQ : DicomVR.UN;
        }

        DicomVR[] vrs = entry.VRs;
        if (vrs.Length == 1)
        {
            return vrs[0];
        }

        if (vrs.Contains(DicomVR.OW))
        {
            return tag == DicomTag.PixelData || vrs.Contains(DicomVR.OB) || length > 6 ? DicomVR.OW : vrs[0];
        }

        return pixelRepresentation == 1 && vrs.Contains(DicomVR.SS) ? DicomVR.SS : vrs[0];
    }

    /// <summary>Returns whether data plausibly starts with a data element, and its encoding.</summary>
    private static bool LooksLikeDataset(ReadOnlySpan<byte> data, out bool bigEndian, out bool isExplicit)
    {
        bigEndian = false;
        isExplicit = false;
        if (data.Length < 8)
        {
            return false;
        }

        ushort little = BinaryPrimitives.ReadUInt16LittleEndian(data);
        ushort big = BinaryPrimitives.ReadUInt16BigEndian(data);
        isExplicit = IsVR(data[4..]) && ParseVR(data[4..], out bool known) is var _ && known;
        if (little is 0x0002 or 0x0008 || (little is > 0 and <= 0x0028 && (little & 1) == 0 && isExplicit))
        {
            if (!isExplicit)
            {
                // Implicit: the length must fit the data.
                return BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) <= data.Length - 8;
            }

            return true;
        }

        if (big is 0x0002 or 0x0008 && isExplicit)
        {
            bigEndian = true;
            return true;
        }

        return false;
    }

    private static uint ReadTag(ReadOnlySpan<byte> s, bool bigEndian)
    {
        return bigEndian
            ? ((uint)BinaryPrimitives.ReadUInt16BigEndian(s) << 16) | BinaryPrimitives.ReadUInt16BigEndian(s[2..])
            : ((uint)BinaryPrimitives.ReadUInt16LittleEndian(s) << 16) | BinaryPrimitives.ReadUInt16LittleEndian(s[2..]);
    }

    private static bool IsVR(ReadOnlySpan<byte> s) => s.Length >= 2 && char.IsAsciiLetterUpper((char)s[0]) && char.IsAsciiLetterUpper((char)s[1]);

    private static DicomVR ParseVR(ReadOnlySpan<byte> s, out bool known)
    {
        Span<char> code = [(char)s[0], (char)s[1]];
        known = Enum.TryParse(code, out DicomVR vr) && Enum.IsDefined(vr);
        return known ? vr : DicomVR.UN;
    }

    /// <summary>Returns whether an explicit VR has the 4-byte length form (PS3.5 table 7.1-1).</summary>
    private static bool IsLong(DicomVR vr)
    {
        return vr is DicomVR.OB or DicomVR.OD or DicomVR.OF or DicomVR.OL or DicomVR.OV or DicomVR.OW or DicomVR.SQ or DicomVR.SV
            or DicomVR.UC or DicomVR.UN or DicomVR.UR or DicomVR.UT or DicomVR.UV;
    }

    /// <summary>Notes something unusual that was read as written; never fatal.</summary>
    private void Note(string id, string message, long offset)
    {
        if (_warned.Add(id))
        {
            _warnings.Add(new EgamiDiagnostic(DiagnosticLevel.Warning, "Dicom", id, message, offset, null));
        }
    }

    /// <summary>Notes damage a lenient read recovered from; a strict read throws.</summary>
    private void Damage(string id, string message, long offset)
    {
        if (_strict)
        {
            throw new CorruptDataException("DICOM", offset, message);
        }

        _warnings.Add(new EgamiDiagnostic(DiagnosticLevel.Warning, "Dicom", id, message, offset, null));
    }

    private readonly record struct Mode(bool Explicit, bool BigEndian);
}
