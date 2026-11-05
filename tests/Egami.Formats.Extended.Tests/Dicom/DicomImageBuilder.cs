using System.Buffers.Binary;

namespace Egami.Formats.Extended.Tests.Dicom;

/// <summary>
/// Writes DICOM image files for the tests from PS3.3 C.7.6.3 and PS3.5 by hand: the Image Pixel module and any other elements,
/// kept in tag order, and native or encapsulated pixel data. Independent of the reader.
/// </summary>
internal sealed class DicomImageBuilder
{
    private readonly List<(uint Tag, Action<DicomBuilder> Write)> _elements = [];

    public DicomImageBuilder(int rows, int columns, int bitsAllocated = 16, int bitsStored = -1, int highBit = -1, bool signed = false,
        int samples = 1, string photometric = "MONOCHROME2", int planar = 0, int frames = 1)
    {
        bitsStored = bitsStored < 0 ? bitsAllocated : bitsStored;
        highBit = highBit < 0 ? bitsStored - 1 : highBit;
        Rows = rows;
        Columns = columns;
        Add(0x0028, 0x0002, b => b.UInt16(0x0028, 0x0002, "US", (ushort)samples));
        Add(0x0028, 0x0004, b => b.Text(0x0028, 0x0004, "CS", photometric));
        if (samples > 1)
        {
            Add(0x0028, 0x0006, b => b.UInt16(0x0028, 0x0006, "US", (ushort)planar));
        }

        if (frames > 1)
        {
            Add(0x0028, 0x0008, b => b.Text(0x0028, 0x0008, "IS", frames.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        Add(0x0028, 0x0010, b => b.UInt16(0x0028, 0x0010, "US", (ushort)rows));
        Add(0x0028, 0x0011, b => b.UInt16(0x0028, 0x0011, "US", (ushort)columns));
        Add(0x0028, 0x0100, b => b.UInt16(0x0028, 0x0100, "US", (ushort)bitsAllocated));
        Add(0x0028, 0x0101, b => b.UInt16(0x0028, 0x0101, "US", (ushort)bitsStored));
        Add(0x0028, 0x0102, b => b.UInt16(0x0028, 0x0102, "US", (ushort)highBit));
        Add(0x0028, 0x0103, b => b.UInt16(0x0028, 0x0103, "US", signed ? (ushort)1 : (ushort)0));
    }

    public int Rows { get; }

    public int Columns { get; }

    /// <summary>Adds an element, written when the file is built, in tag order.</summary>
    public DicomImageBuilder Add(ushort group, ushort element, Action<DicomBuilder> write)
    {
        _elements.RemoveAll(e => e.Tag == (((uint)group << 16) | element));
        _elements.Add(((((uint)group) << 16) | element, write));
        return this;
    }

    public DicomImageBuilder Text(ushort group, ushort element, string vr, string value) => Add(group, element, b => b.Text(group, element, vr, value));

    /// <summary>Adds native pixel data as bytes, padded to even length.</summary>
    public DicomImageBuilder Native(byte[] data, string vr = "OW")
    {
        byte[] padded = data.Length % 2 == 0 ? data : [.. data, 0];
        return Add(0x7FE0, 0x0010, b => b.Bytes(0x7FE0, 0x0010, vr, padded));
    }

    /// <summary>Adds native 16-bit pixel data in the builder's byte order.</summary>
    public DicomImageBuilder Native16(params ushort[] values) => Add(0x7FE0, 0x0010, b => b.UInt16(0x7FE0, 0x0010, "OW", values));

    /// <summary>Adds encapsulated pixel data.</summary>
    public DicomImageBuilder Encapsulated(uint[] offsets, params byte[][] fragments)
    {
        byte[][] even = [.. fragments.Select(f => f.Length % 2 == 0 ? f : [.. f, 0])];
        return Add(0x7FE0, 0x0010, b => b.Encapsulated(offsets, even));
    }

    /// <summary>Returns the dataset's bytes.</summary>
    public byte[] Dataset(bool explicitVR = true, bool bigEndian = false)
    {
        DicomBuilder b = new(explicitVR, bigEndian);
        foreach ((uint _, Action<DicomBuilder> write) in _elements.OrderBy(e => e.Tag))
        {
            write(b);
        }

        return b.ToArray();
    }

    /// <summary>Returns a Part 10 file in a transfer syntax.</summary>
    public byte[] File(string transferSyntax)
    {
        bool explicitVR = transferSyntax != "1.2.840.10008.1.2";
        bool big = transferSyntax == "1.2.840.10008.1.2.2";
        return DicomBuilder.Part10(transferSyntax, Dataset(explicitVR, big));
    }

    /// <summary>Returns 16-bit words as little-endian bytes.</summary>
    public static byte[] Words(params ushort[] values)
    {
        byte[] bytes = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), values[i]);
        }

        return bytes;
    }

    /// <summary>
    /// Codes a frame as DICOM RLE (PS3.5 annex G) independently of the reader: one PackBits segment per byte of each sample, most
    /// significant first, runs of 3 or more as replicate runs and the rest as literal runs.
    /// </summary>
    public static byte[] Rle(uint[] samples, int components, int bytesPerSample)
    {
        int pixels = samples.Length / components;
        List<byte[]> segments = [];
        for (int c = 0; c < components; c++)
        {
            for (int b = 0; b < bytesPerSample; b++)
            {
                int shift = 8 * (bytesPerSample - 1 - b);
                byte[] plane = new byte[pixels];
                for (int p = 0; p < pixels; p++)
                {
                    plane[p] = (byte)(samples[(p * components) + c] >> shift);
                }

                segments.Add(PackBits(plane));
            }
        }

        byte[] header = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)segments.Count);
        uint at = 64;
        for (int i = 0; i < segments.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4 + (i * 4)), at);
            at += (uint)segments[i].Length;
        }

        return [.. header, .. segments.SelectMany(s => s)];
    }

    private static byte[] PackBits(byte[] data)
    {
        List<byte> output = [];
        int i = 0;
        while (i < data.Length)
        {
            int run = 1;
            while (i + run < data.Length && data[i + run] == data[i] && run < 128)
            {
                run++;
            }

            if (run >= 3)
            {
                output.Add((byte)(sbyte)(1 - run));
                output.Add(data[i]);
                i += run;
                continue;
            }

            int start = i;
            int length = 0;
            while (i < data.Length && length < 128)
            {
                if (i + 2 < data.Length && data[i] == data[i + 1] && data[i] == data[i + 2])
                {
                    break;
                }

                i++;
                length++;
            }

            output.Add((byte)(length - 1));
            output.AddRange(data.AsSpan(start, length).ToArray());
        }

        if (output.Count % 2 == 1)
        {
            output.Add(0x80);
        }

        return [.. output];
    }
}
