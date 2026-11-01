using System.Buffers.Binary;

namespace Egami.Formats.Extended.Raw;

/// <summary>
/// One IFD of a DNG being written: its entries, the IFDs it points to (SubIFDs, EXIF, GPS) and the image data its offsets tags
/// point at. Values are little-endian, as the file is.
/// </summary>
internal sealed class DngIfd
{
    private readonly SortedDictionary<int, (int Type, int Count, byte[] Value)> _entries = [];
    private readonly HashSet<int> _kept = [];

    /// <summary>Gets the IFDs listed in SubIFDs (tag 330), in order.</summary>
    public List<DngIfd> SubIfds { get; } = [];

    /// <summary>Gets or sets the EXIF IFD (tag 34665), or <see langword="null"/>.</summary>
    public DngIfd? Exif { get; set; }

    /// <summary>Gets or sets the GPS IFD (tag 34853), or <see langword="null"/>.</summary>
    public DngIfd? Gps { get; set; }

    /// <summary>Gets the strips or tiles, written after the IFDs; their offsets and byte counts go in <see cref="OffsetsTag"/> and <see cref="CountsTag"/>.</summary>
    public List<byte[]> Units { get; } = [];

    public int OffsetsTag { get; set; } = RawTags.StripOffsets;

    public int CountsTag { get; set; } = RawTags.StripByteCounts;

    public int Count => _entries.Count;

    public bool Has(int tag) => _entries.ContainsKey(tag);

    public void Remove(int tag)
    {
        _entries.Remove(tag);
        _kept.Remove(tag);
    }

    /// <summary>Sets an entry from its little-endian value bytes, unless it was kept from a source with <see cref="Keep"/>.</summary>
    public void Raw(int tag, int type, int count, byte[] value)
    {
        if (!_kept.Contains(tag))
        {
            _entries[tag] = (type, count, value);
        }
    }

    /// <summary>Sets an entry copied from a source DNG, which later sets leave as it is, so its exact values stay.</summary>
    public void Keep(int tag, int type, int count, byte[] value)
    {
        _entries[tag] = (type, count, value);
        _kept.Add(tag);
    }

    public void Short(int tag, params ReadOnlySpan<int> values)
    {
        byte[] b = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(i * 2), (ushort)values[i]);
        }

        Raw(tag, 3, values.Length, b);
    }

    public void Long(int tag, params ReadOnlySpan<long> values)
    {
        byte[] b = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 4), (uint)values[i]);
        }

        Raw(tag, 4, values.Length, b);
    }

    /// <summary>Sets SHORT values when they all fit 16 bits, else LONG ones.</summary>
    public void Integer(int tag, params ReadOnlySpan<long> values)
    {
        foreach (long v in values)
        {
            if (v is < 0 or > ushort.MaxValue)
            {
                Long(tag, values);
                return;
            }
        }

        int[] shorts = new int[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            shorts[i] = (int)values[i];
        }

        Short(tag, shorts);
    }

    public void Bytes(int tag, params ReadOnlySpan<byte> values) => Raw(tag, 1, values.Length, values.ToArray());

    public void Undefined(int tag, ReadOnlySpan<byte> values) => Raw(tag, 7, values.Length, values.ToArray());

    public void Ascii(int tag, string text)
    {
        byte[] b = [.. System.Text.Encoding.ASCII.GetBytes(text), 0];
        Raw(tag, 2, b.Length, b);
    }

    /// <summary>
    /// Sets RATIONAL values: whole numbers over 1, others over the largest denominator up to 10^9 that keeps the numerator in 32
    /// bits, so a measured black level keeps about seven decimals.
    /// </summary>
    public void Rational(int tag, params ReadOnlySpan<double> values)
    {
        byte[] b = new byte[values.Length * 8];
        for (int i = 0; i < values.Length; i++)
        {
            double v = Math.Max(0, values[i]);
            uint d = v == Math.Floor(v) ? 1 : (uint)Math.Clamp(Math.Floor(uint.MaxValue / Math.Max(v, 1)), 1, 1_000_000_000);

            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 8), (uint)Math.Round(v * d));
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan((i * 8) + 4), d);
        }

        Raw(tag, 5, values.Length, b);
    }

    /// <summary>Sets SRATIONAL values, each over 10,000 (matrices) or a denominator that keeps their precision.</summary>
    public void SRational(int tag, params ReadOnlySpan<double> values)
    {
        byte[] b = new byte[values.Length * 8];
        for (int i = 0; i < values.Length; i++)
        {
            double v = values[i];
            int d = 10000;
            while (Math.Abs(v * d) > int.MaxValue && d > 1)
            {
                d /= 10;
            }

            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(i * 8), (int)Math.Round(v * d));
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan((i * 8) + 4), d);
        }

        Raw(tag, 10, values.Length, b);
    }

    public void Double(int tag, params ReadOnlySpan<double> values)
    {
        byte[] b = new byte[values.Length * 8];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(b.AsSpan(i * 8), values[i]);
        }

        Raw(tag, 12, values.Length, b);
    }

    public IEnumerable<KeyValuePair<int, (int Type, int Count, byte[] Value)>> Entries => _entries;

    /// <summary>Gets the IFDs this one points to, SubIFDs first.</summary>
    public IEnumerable<DngIfd> Children
    {
        get
        {
            foreach (DngIfd sub in SubIfds)
            {
                yield return sub;
            }

            if (Exif is not null)
            {
                yield return Exif;
            }

            if (Gps is not null)
            {
                yield return Gps;
            }
        }
    }
}

/// <summary>
/// Writes a DNG's TIFF structure (TIFF 6.0, BigTIFF past 4 GB) from a tree of <see cref="DngIfd"/>s: the header, every IFD with
/// the values that do not fit in their entries, then the strips and tiles. Every offset is known before the first byte is
/// written, so the output need not seek.
/// </summary>
internal static class DngTiff
{
    public static void Write(Stream output, DngIfd ifd0)
    {
        List<DngIfd> ifds = [];
        Collect(ifd0, ifds);
        // Placeholders, so the entry counts and sizes are final; the values are filled once the layout is.
        Placeholders(ifds, false);
        bool big = Size(ifds, false) > uint.MaxValue;
        if (big)
        {
            Placeholders(ifds, true);
        }

        long[] ifdAt = Layout(ifds, big, out long dataAt);
        Dictionary<DngIfd, long> where = [];
        for (int i = 0; i < ifds.Count; i++)
        {
            where[ifds[i]] = ifdAt[i];
        }

        // Fill the offsets now the layout is known.
        long at = dataAt;
        foreach (DngIfd ifd in ifds)
        {
            if (ifd.SubIfds.Count > 0)
            {
                ifd.Offsets(RawTags.SubIfds, [.. ifd.SubIfds.Select(s => where[s])], big, 13);
            }

            if (ifd.Exif is not null)
            {
                ifd.Offsets(RawTags.ExifIfd, [where[ifd.Exif]], big, 13);
            }

            if (ifd.Gps is not null)
            {
                ifd.Offsets(RawTags.GpsIfd, [where[ifd.Gps]], big, 13);
            }

            if (ifd.Units.Count > 0)
            {
                long[] offsets = new long[ifd.Units.Count];
                long[] counts = new long[ifd.Units.Count];
                for (int u = 0; u < ifd.Units.Count; u++)
                {
                    offsets[u] = at;
                    counts[u] = ifd.Units[u].Length;
                    at += Pad(ifd.Units[u].Length);
                }

                ifd.Offsets(ifd.OffsetsTag, offsets, big, 16);
                ifd.Offsets(ifd.CountsTag, counts, big, 16);
            }
        }

        BufferedStream o = new(output, 1 << 16);
        Span<byte> header = stackalloc byte[16];
        if (big)
        {
            "II"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt16LittleEndian(header[2..], 43);
            BinaryPrimitives.WriteUInt16LittleEndian(header[4..], 8);
            BinaryPrimitives.WriteUInt16LittleEndian(header[6..], 0);
            BinaryPrimitives.WriteUInt64LittleEndian(header[8..], (ulong)ifdAt[0]);
            o.Write(header);
        }
        else
        {
            "II"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt16LittleEndian(header[2..], 42);
            BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)ifdAt[0]);
            o.Write(header[..8]);
        }

        for (int i = 0; i < ifds.Count; i++)
        {
            WriteIfd(o, ifds[i], ifdAt[i], big);
        }

        foreach (DngIfd ifd in ifds)
        {
            foreach (byte[] unit in ifd.Units)
            {
                o.Write(unit);
                if (unit.Length % 2 == 1)
                {
                    o.WriteByte(0);
                }
            }
        }

        o.Flush();
    }

    private static void Placeholders(List<DngIfd> ifds, bool big)
    {
        foreach (DngIfd ifd in ifds)
        {
            if (ifd.SubIfds.Count > 0)
            {
                ifd.Offsets(RawTags.SubIfds, new long[ifd.SubIfds.Count], big, 13);
            }

            if (ifd.Exif is not null)
            {
                ifd.Offsets(RawTags.ExifIfd, [0], big, 13);
            }

            if (ifd.Gps is not null)
            {
                ifd.Offsets(RawTags.GpsIfd, [0], big, 13);
            }

            if (ifd.Units.Count > 0)
            {
                ifd.Offsets(ifd.OffsetsTag, new long[ifd.Units.Count], big, 16);
                ifd.Offsets(ifd.CountsTag, new long[ifd.Units.Count], big, 16);
            }
        }
    }

    private static void Collect(DngIfd ifd, List<DngIfd> all)
    {
        all.Add(ifd);
        foreach (DngIfd child in ifd.Children)
        {
            Collect(child, all);
        }
    }

    private static long Pad(long length) => length + (length & 1);

    /// <summary>Returns the bytes one IFD takes with its outside values.</summary>
    private static long IfdSize(DngIfd ifd, bool big)
    {
        int entry = big ? 20 : 12;
        int inline = big ? 8 : 4;
        long size = (big ? 8 : 2) + ((long)ifd.Count * entry) + (big ? 8 : 4);
        foreach (KeyValuePair<int, (int Type, int Count, byte[] Value)> e in ifd.Entries)
        {
            int length = e.Value.Value.Length;
            if (length > inline)
            {
                size += Pad(length);
            }
        }

        return size;
    }

    private static long Size(List<DngIfd> ifds, bool big)
    {
        long size = big ? 16 : 8;
        foreach (DngIfd ifd in ifds)
        {
            size += IfdSize(ifd, big);
            foreach (byte[] unit in ifd.Units)
            {
                size += Pad(unit.Length);
            }
        }

        return size;
    }

    private static long[] Layout(List<DngIfd> ifds, bool big, out long dataAt)
    {
        long[] ifdAt = new long[ifds.Count];
        long at = big ? 16 : 8;
        for (int i = 0; i < ifds.Count; i++)
        {
            ifdAt[i] = at;
            at += IfdSize(ifds[i], big);
        }

        dataAt = at;
        return ifdAt;
    }

    /// <summary>Sets an offsets or counts entry: LONG (or IFD) in classic TIFF, LONG8 (or IFD8) in BigTIFF.</summary>
    private static void Offsets(this DngIfd ifd, int tag, long[] values, bool big, int bigType)
    {
        if (big)
        {
            byte[] b = new byte[values.Length * 8];
            for (int i = 0; i < values.Length; i++)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(i * 8), (ulong)values[i]);
            }

            // BigTIFF's types: 16 LONG8, 18 IFD8.
            ifd.Raw(tag, bigType == 13 ? 18 : 16, values.Length, b);
        }
        else
        {
            byte[] b = new byte[values.Length * 4];
            for (int i = 0; i < values.Length; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 4), (uint)values[i]);
            }

            ifd.Raw(tag, bigType == 13 ? 13 : 4, values.Length, b);
        }
    }

    /// <summary>Writes one IFD at <paramref name="at"/>, its entries then its outside values.</summary>
    private static void WriteIfd(Stream o, DngIfd ifd, long at, bool big)
    {
        int entrySize = big ? 20 : 12;
        int inline = big ? 8 : 4;
        long outside = at + (big ? 8 : 2) + ((long)ifd.Count * entrySize) + (big ? 8 : 4);
        using MemoryStream values = new();
        byte[] head = new byte[(big ? 8 : 2) + ((long)ifd.Count * entrySize) + (big ? 8 : 4)];
        Span<byte> h = head;
        if (big)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(h, (ulong)ifd.Count);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(h, (ushort)ifd.Count);
        }

        int p = big ? 8 : 2;
        foreach (KeyValuePair<int, (int Type, int Count, byte[] Value)> e in ifd.Entries)
        {
            (int type, int count, byte[] value) = e.Value;
            BinaryPrimitives.WriteUInt16LittleEndian(h[p..], (ushort)e.Key);
            BinaryPrimitives.WriteUInt16LittleEndian(h[(p + 2)..], (ushort)type);
            if (big)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(h[(p + 4)..], (ulong)count);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(h[(p + 4)..], (uint)count);
            }

            int valueAt = p + (big ? 12 : 8);
            if (value.Length <= inline)
            {
                value.CopyTo(h[valueAt..]);
            }
            else
            {
                long offset = outside + values.Length;
                if (big)
                {
                    BinaryPrimitives.WriteUInt64LittleEndian(h[valueAt..], (ulong)offset);
                }
                else
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(h[valueAt..], (uint)offset);
                }

                values.Write(value);
                if (value.Length % 2 == 1)
                {
                    values.WriteByte(0);
                }
            }

            p += entrySize;
        }

        // The next-IFD link: none; the IFDs are reached through IFD0's SubIFDs, EXIF and GPS pointers.
        o.Write(head);
        values.Position = 0;
        values.CopyTo(o);
    }
}
