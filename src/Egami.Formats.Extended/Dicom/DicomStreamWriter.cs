using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Egami.Formats.Extended.Dicom;

/// <summary>
/// Writes datasets (PS3.5 chapter 7) and Part 10 files (PS3.10 section 7.1): the preamble, DICM, the file meta information in
/// explicit VR little endian with its group length, then the dataset in explicit or implicit VR little endian, deflated for
/// Deflated Explicit VR Little Endian. Sequences and their items are written with undefined lengths and delimiters, encapsulated
/// pixel data as its offset table item and fragments. Values read from a big-endian file are swapped by their VR; odd lengths
/// are padded as PS3.5 section 6.2 says.
/// </summary>
internal sealed class DicomStreamWriter
{
    private const uint Undefined = 0xFFFFFFFF;

    private readonly Stream _output;
    private readonly bool _explicit;
    private readonly byte[] _header = new byte[12];

    private DicomStreamWriter(Stream output, bool isExplicit)
    {
        _output = output;
        _explicit = isExplicit;
    }

    /// <summary>
    /// Writes a Part 10 file: the 128-byte preamble, DICM, the file meta information (its group length worked out here; the
    /// other elements as given) and the dataset in <paramref name="syntax"/>.
    /// </summary>
    public static void WriteFile(Stream output, IReadOnlyList<DicomElement> meta, IReadOnlyList<DicomElement> dataset, DicomTransferSyntax syntax)
    {
        if (syntax.IsBigEndian)
        {
            throw new NotSupportedException($"{syntax} is retired and is not written; write Explicit VR Little Endian instead.");
        }

        using MemoryStream group = new();
        DicomStreamWriter metaWriter = new(group, isExplicit: true);
        foreach (DicomElement element in Sorted(meta))
        {
            if (element.Tag != new DicomTag(0x0002, 0x0000))
            {
                metaWriter.Write(element);
            }
        }

        Span<byte> start = stackalloc byte[132 + 12];
        start.Clear();
        "DICM"u8.CopyTo(start[128..]);
        Span<byte> length = start[132..];
        BinaryPrimitives.WriteUInt16LittleEndian(length, 0x0002);
        BinaryPrimitives.WriteUInt16LittleEndian(length[2..], 0x0000);
        length[4] = (byte)'U';
        length[5] = (byte)'L';
        BinaryPrimitives.WriteUInt16LittleEndian(length[6..], 4);
        BinaryPrimitives.WriteUInt32LittleEndian(length[8..], (uint)group.Length);
        output.Write(start);
        group.Position = 0;
        group.CopyTo(output);

        if (syntax.IsDeflated)
        {
            using DeflateStream deflate = new(output, CompressionLevel.Optimal, leaveOpen: true);
            WriteDataset(deflate, dataset, syntax.IsExplicitVR);
        }
        else
        {
            WriteDataset(output, dataset, syntax.IsExplicitVR);
        }

        output.Flush();
    }

    /// <summary>Writes a dataset's elements in ascending tag order, without a group length or preamble.</summary>
    public static void WriteDataset(Stream output, IEnumerable<DicomElement> elements, bool isExplicit)
    {
        DicomStreamWriter writer = new(output, isExplicit);
        foreach (DicomElement element in Sorted(elements))
        {
            writer.Write(element);
        }
    }

    /// <summary>Returns elements in ascending tag order, file order kept among equal tags.</summary>
    internal static IEnumerable<DicomElement> Sorted(IEnumerable<DicomElement> elements) => elements.OrderBy(e => e.Tag.Value);

    /// <summary>Returns whether an explicit VR has the 4-byte length form (PS3.5 table 7.1-1).</summary>
    internal static bool IsLong(DicomVR vr)
    {
        return vr is DicomVR.OB or DicomVR.OD or DicomVR.OF or DicomVR.OL or DicomVR.OV or DicomVR.OW or DicomVR.SQ or DicomVR.SV
            or DicomVR.UC or DicomVR.UN or DicomVR.UR or DicomVR.UT or DicomVR.UV;
    }

    /// <summary>Returns the bytes of a value in little-endian order: a big-endian value's words swapped by its VR.</summary>
    internal static ReadOnlyMemory<byte> LittleEndian(DicomElement element)
    {
        ReadOnlyMemory<byte> value = element.RawValue;
        int size = element.VR switch
        {
            DicomVR.US or DicomVR.SS or DicomVR.OW or DicomVR.AT => 2,
            DicomVR.UL or DicomVR.SL or DicomVR.FL or DicomVR.OL or DicomVR.OF => 4,
            DicomVR.FD or DicomVR.OD or DicomVR.UV or DicomVR.SV or DicomVR.OV => 8,
            _ => 1,
        };
        if (!element.IsBigEndian || size == 1 || value.IsEmpty)
        {
            return value;
        }

        byte[] swapped = value.ToArray();
        int whole = swapped.Length - (swapped.Length % size);
        for (int i = 0; i < whole; i += size)
        {
            swapped.AsSpan(i, size).Reverse();
        }

        return swapped;
    }

    private void Write(DicomElement element)
    {
        DicomTag tag = element.Tag;
        if (tag.Element == 0x0000 && tag.Group != 0x0002)
        {
            // Group lengths (retired outside group 0002) would no longer be right.
            return;
        }

        if (element.VR == DicomVR.SQ)
        {
            Header(tag, DicomVR.SQ, Undefined);
            foreach (DicomDataset item in element.Items)
            {
                Marker(0xE000, Undefined);
                foreach (DicomElement nested in Sorted(item))
                {
                    Write(nested);
                }

                Marker(0xE00D, 0);
            }

            Marker(0xE0DD, 0);
            return;
        }

        if (element.Fragments is DicomFragments fragments)
        {
            Header(tag, DicomVR.OB, Undefined);
            IReadOnlyList<uint> table = fragments.BasicOffsetTable;
            Marker(0xE000, (uint)(table.Count * 4));
            Span<byte> word = stackalloc byte[4];
            foreach (uint offset in table)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(word, offset);
                _output.Write(word);
            }

            foreach (ReadOnlyMemory<byte> fragment in fragments.Fragments)
            {
                bool odd = (fragment.Length & 1) != 0;
                Marker(0xE000, (uint)(fragment.Length + (odd ? 1 : 0)));
                _output.Write(fragment.Span);
                if (odd)
                {
                    _output.WriteByte(0);
                }
            }

            Marker(0xE0DD, 0);
            return;
        }

        ReadOnlyMemory<byte> value = LittleEndian(element);
        DicomVR vr = element.VR;
        long padded = value.Length + (value.Length & 1);
        if (_explicit && !IsLong(vr) && padded > 0xFFFE)
        {
            // Too long for a 16-bit length: PS3.5 6.2.2 lets such a value be written as UN.
            vr = DicomVR.UN;
        }

        if (padded > uint.MaxValue - 1)
        {
            throw new NotSupportedException($"{tag} holds {value.Length} bytes, more than a DICOM element can.");
        }

        Header(tag, vr, (uint)padded);
        _output.Write(value.Span);
        if ((value.Length & 1) != 0)
        {
            _output.WriteByte(Padding(element.VR));
        }
    }

    /// <summary>Returns the byte that pads a value of a VR to an even length: NUL for UI and binary VRs, a space for text.</summary>
    internal static byte Padding(DicomVR vr) => vr != DicomVR.UI && DicomElement.IsText(vr) ? (byte)' ' : (byte)0;

    private void Header(DicomTag tag, DicomVR vr, uint length)
    {
        Span<byte> h = _header;
        BinaryPrimitives.WriteUInt16LittleEndian(h, tag.Group);
        BinaryPrimitives.WriteUInt16LittleEndian(h[2..], tag.Element);
        if (!_explicit)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(h[4..], length);
            _output.Write(h[..8]);
            return;
        }

        string code = vr.ToString();
        h[4] = (byte)code[0];
        h[5] = (byte)code[1];
        if (IsLong(vr))
        {
            h[6] = 0;
            h[7] = 0;
            BinaryPrimitives.WriteUInt32LittleEndian(h[8..], length);
            _output.Write(h);
            return;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(h[6..], (ushort)length);
        _output.Write(h[..8]);
    }

    /// <summary>Writes an item, item delimiter or sequence delimiter tag (group FFFE) and its length, which have no VR.</summary>
    private void Marker(ushort element, uint length)
    {
        Span<byte> h = _header;
        BinaryPrimitives.WriteUInt16LittleEndian(h, 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(h[2..], element);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], length);
        _output.Write(h[..8]);
    }
}

/// <summary>Makes data elements to write: text, numbers and bytes as little-endian values.</summary>
internal static class DicomValue
{
    /// <summary>Returns an element holding bytes already in little-endian order.</summary>
    public static DicomElement Bytes(DicomTag tag, DicomVR vr, ReadOnlyMemory<byte> value)
    {
        return new DicomElement(tag, vr, value, false, DicomCharacterSet.Default, -1);
    }

    /// <summary>Returns an element of the PS3.6 VR of <paramref name="tag"/>, its value parsed from text (see <see cref="Parse"/>).</summary>
    public static DicomElement FromText(DicomTag tag, DicomVR vr, string text, Encoding encoding) => Bytes(tag, vr, Parse(vr, text, encoding));

    /// <summary>Returns a text element, written in ASCII.</summary>
    public static DicomElement Text(DicomTag tag, DicomVR vr, string text) => Bytes(tag, vr, Encoding.ASCII.GetBytes(text));

    /// <summary>Returns an element of unsigned 16-bit integers.</summary>
    public static DicomElement UShort(DicomTag tag, params ReadOnlySpan<int> values)
    {
        byte[] bytes = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), (ushort)values[i]);
        }

        return Bytes(tag, DicomVR.US, bytes);
    }

    /// <summary>
    /// Encodes the text of a value by its VR: text VRs in <paramref name="encoding"/>, numbers and tags of binary VRs parsed from
    /// their <c>\</c>-separated text.
    /// </summary>
    /// <exception cref="ArgumentException">The VR holds bytes or items, or a number does not parse.</exception>
    public static byte[] Parse(DicomVR vr, string text, Encoding encoding)
    {
        if (DicomElement.IsText(vr))
        {
            return encoding.GetBytes(text);
        }

        string[] parts = text.Length == 0 ? [] : text.Split('\\');
        int size = vr switch
        {
            DicomVR.US or DicomVR.SS => 2,
            DicomVR.UL or DicomVR.SL or DicomVR.FL or DicomVR.AT => 4,
            DicomVR.FD or DicomVR.UV or DicomVR.SV => 8,
            _ => throw new ArgumentException($"A {vr} value cannot be given as text.", nameof(text)),
        };
        byte[] bytes = new byte[parts.Length * size];
        System.Globalization.CultureInfo c = System.Globalization.CultureInfo.InvariantCulture;
        for (int i = 0; i < parts.Length; i++)
        {
            Span<byte> at = bytes.AsSpan(i * size);
            string p = parts[i].Trim();
            try
            {
                switch (vr)
                {
                    case DicomVR.US: BinaryPrimitives.WriteUInt16LittleEndian(at, ushort.Parse(p, c)); break;
                    case DicomVR.SS: BinaryPrimitives.WriteInt16LittleEndian(at, short.Parse(p, c)); break;
                    case DicomVR.UL: BinaryPrimitives.WriteUInt32LittleEndian(at, uint.Parse(p, c)); break;
                    case DicomVR.SL: BinaryPrimitives.WriteInt32LittleEndian(at, int.Parse(p, c)); break;
                    case DicomVR.FL: BinaryPrimitives.WriteSingleLittleEndian(at, float.Parse(p, c)); break;
                    case DicomVR.FD: BinaryPrimitives.WriteDoubleLittleEndian(at, double.Parse(p, c)); break;
                    case DicomVR.UV: BinaryPrimitives.WriteUInt64LittleEndian(at, ulong.Parse(p, c)); break;
                    case DicomVR.SV: BinaryPrimitives.WriteInt64LittleEndian(at, long.Parse(p, c)); break;
                    default:
                        string hex = p.Trim('(', ')').Replace(",", "", StringComparison.Ordinal);
                        uint t = uint.Parse(hex, System.Globalization.NumberStyles.HexNumber, c);
                        BinaryPrimitives.WriteUInt16LittleEndian(at, (ushort)(t >> 16));
                        BinaryPrimitives.WriteUInt16LittleEndian(at[2..], (ushort)t);
                        break;
                }
            }
            catch (Exception e) when (e is FormatException or OverflowException)
            {
                throw new ArgumentException($"'{p}' is not a {vr} value.", nameof(text), e);
            }
        }

        return bytes;
    }
}
