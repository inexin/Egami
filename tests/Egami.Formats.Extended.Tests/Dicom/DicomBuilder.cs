using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Egami.Formats.Extended.Tests.Dicom;

/// <summary>
/// Writes DICOM bytes for the tests by hand from PS3.5 and PS3.10, independent of the reader: elements in explicit or implicit
/// VR, either byte order, sequences and items of defined or undefined length, encapsulated pixel data, and the Part 10 header.
/// </summary>
internal sealed class DicomBuilder(bool explicitVR = true, bool bigEndian = false)
{
    private static readonly string[] LongVRs = ["OB", "OD", "OF", "OL", "OV", "OW", "SQ", "SV", "UC", "UN", "UR", "UT", "UV"];

    private readonly ArrayBufferWriter<byte> _bytes = new();

    public bool ExplicitVR { get; } = explicitVR;

    public bool BigEndian { get; } = bigEndian;

    public byte[] ToArray() => _bytes.WrittenSpan.ToArray();

    /// <summary>Writes an element whose value is text, padded to even length (UI with NUL, others with a space).</summary>
    public DicomBuilder Text(ushort group, ushort element, string vr, string value, Encoding? encoding = null)
    {
        byte[] bytes = (encoding ?? Encoding.Latin1).GetBytes(value);
        return Bytes(group, element, vr, Pad(bytes, vr == "UI" ? (byte)0 : (byte)' '));
    }

    /// <summary>Writes an element whose value is text already encoded, padded with a space.</summary>
    public DicomBuilder Raw(ushort group, ushort element, string vr, byte[] value) => Bytes(group, element, vr, Pad(value, (byte)' '));

    public DicomBuilder UInt16(ushort group, ushort element, string vr, params ushort[] values)
    {
        byte[] bytes = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++)
        {
            if (BigEndian)
            {
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(i * 2), values[i]);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), values[i]);
            }
        }

        return Bytes(group, element, vr, bytes);
    }

    public DicomBuilder UInt32(ushort group, ushort element, string vr, params uint[] values)
    {
        byte[] bytes = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
        {
            if (BigEndian)
            {
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(i * 4), values[i]);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), values[i]);
            }
        }

        return Bytes(group, element, vr, bytes);
    }

    public DicomBuilder Double(ushort group, ushort element, params double[] values)
    {
        byte[] bytes = new byte[values.Length * 8];
        for (int i = 0; i < values.Length; i++)
        {
            if (BigEndian)
            {
                BinaryPrimitives.WriteDoubleBigEndian(bytes.AsSpan(i * 8), values[i]);
            }
            else
            {
                BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(i * 8), values[i]);
            }
        }

        return Bytes(group, element, "FD", bytes);
    }

    /// <summary>Writes an element header and value as given, with no padding.</summary>
    public DicomBuilder Bytes(ushort group, ushort element, string vr, byte[] value)
    {
        Header(group, element, vr, (uint)value.Length);
        _bytes.Write(value);
        return this;
    }

    /// <summary>Writes an element header with the length given, which may be 0xFFFFFFFF (undefined).</summary>
    public DicomBuilder Header(ushort group, ushort element, string vr, uint length)
    {
        Tag(group, element);
        if (ExplicitVR)
        {
            _bytes.Write(Encoding.ASCII.GetBytes(vr));
            if (LongVRs.Contains(vr))
            {
                _bytes.Write(new byte[2]);
                U32(length);
            }
            else
            {
                U16((ushort)length);
            }
        }
        else
        {
            U32(length);
        }

        return this;
    }

    /// <summary>Writes a sequence of the items given; each item is written by its own builder in this one's encoding.</summary>
    public DicomBuilder Sequence(ushort group, ushort element, bool undefinedSequence, bool undefinedItems, params Action<DicomBuilder>[] items)
    {
        DicomBuilder body = new(ExplicitVR, BigEndian);
        foreach (Action<DicomBuilder> item in items)
        {
            DicomBuilder content = new(ExplicitVR, BigEndian);
            item(content);
            byte[] bytes = content.ToArray();
            body.Tag(0xFFFE, 0xE000);
            body.U32(undefinedItems ? 0xFFFFFFFF : (uint)bytes.Length);
            body._bytes.Write(bytes);
            if (undefinedItems)
            {
                body.Tag(0xFFFE, 0xE00D);
                body.U32(0);
            }
        }

        if (undefinedSequence)
        {
            body.Tag(0xFFFE, 0xE0DD);
            body.U32(0);
        }

        byte[] all = body.ToArray();
        Header(group, element, "SQ", undefinedSequence ? 0xFFFFFFFF : (uint)all.Length);
        _bytes.Write(all);
        return this;
    }

    /// <summary>Writes encapsulated pixel data: the Basic Offset Table, the fragments and the sequence delimiter.</summary>
    public DicomBuilder Encapsulated(uint[] offsets, params byte[][] fragments)
    {
        Header(0x7FE0, 0x0010, "OB", 0xFFFFFFFF);
        Tag(0xFFFE, 0xE000);
        U32((uint)(offsets.Length * 4));
        foreach (uint offset in offsets)
        {
            U32(offset);
        }

        foreach (byte[] fragment in fragments)
        {
            Tag(0xFFFE, 0xE000);
            U32((uint)fragment.Length);
            _bytes.Write(fragment);
        }

        Tag(0xFFFE, 0xE0DD);
        U32(0);
        return this;
    }

    /// <summary>Appends bytes as they are.</summary>
    public DicomBuilder Append(byte[] bytes)
    {
        _bytes.Write(bytes);
        return this;
    }

    /// <summary>Returns a Part 10 file: preamble, DICM, the file meta information for the transfer syntax, then the dataset.</summary>
    public static byte[] Part10(string transferSyntax, byte[] dataset, bool deflate = false, bool zlib = false)
    {
        DicomBuilder meta = new(true, false);
        DicomBuilder body = new(true, false);
        body.Bytes(0x0002, 0x0001, "OB", [0, 1]);
        body.Text(0x0002, 0x0002, "UI", "1.2.840.10008.5.1.4.1.1.7");
        body.Text(0x0002, 0x0003, "UI", "1.2.3.4.5");
        body.Text(0x0002, 0x0010, "UI", transferSyntax);
        byte[] group = body.ToArray();
        meta.UInt32(0x0002, 0x0000, "UL", (uint)group.Length);
        meta.Append(group);
        if (deflate)
        {
            using MemoryStream packed = new();
            using (Stream deflater = zlib ? new ZLibStream(packed, CompressionLevel.Optimal, true) : new DeflateStream(packed, CompressionLevel.Optimal, true))
            {
                deflater.Write(dataset);
            }

            dataset = packed.ToArray();
        }

        return [.. new byte[128], .. "DICM"u8, .. meta.ToArray(), .. dataset];
    }

    private void Tag(ushort group, ushort element)
    {
        U16(group);
        U16(element);
    }

    private void U16(ushort value)
    {
        Span<byte> b = stackalloc byte[2];
        if (BigEndian)
        {
            BinaryPrimitives.WriteUInt16BigEndian(b, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(b, value);
        }

        _bytes.Write(b);
    }

    private void U32(uint value)
    {
        Span<byte> b = stackalloc byte[4];
        if (BigEndian)
        {
            BinaryPrimitives.WriteUInt32BigEndian(b, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        }

        _bytes.Write(b);
    }

    private static byte[] Pad(byte[] bytes, byte pad) => bytes.Length % 2 == 0 ? bytes : [.. bytes, pad];
}
