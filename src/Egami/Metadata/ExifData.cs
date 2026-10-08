using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace Egami.Metadata;

/// <summary>
/// A parsed EXIF block (a TIFF structure, CIPA DC-008): the tags of IFD0, the EXIF, GPS and interoperability sub-IFDs and IFD1
/// with its thumbnail, with typed getters for the common ones. An unedited block writes back byte for byte; after an edit it is
/// laid out again, keeping the maker note at its old offset when it fits, since maker notes often hold offsets of their own.
/// </summary>
public sealed class ExifData
{
    private const ushort ExifPointer = 0x8769;
    private const ushort GpsPointer = 0x8825;
    private const ushort InteropPointer = 0xA005;
    private const ushort ThumbnailOffset = 0x0201;
    private const ushort ThumbnailLength = 0x0202;

    private readonly List<ExifEntry>[] _ifds = [[], [], [], [], []];
    private readonly List<string> _warnings = [];
    private byte[] _original = [];
    private bool _edited;

    /// <summary>Creates an empty block, little-endian, to fill with <see cref="Set(ExifKey, string)"/> and the other setters.</summary>
    public ExifData()
    {
        _edited = true;
    }

    private ExifData(bool bigEndian)
    {
        IsBigEndian = bigEndian;
    }

    /// <summary>Gets whether values are stored big-endian (Motorola order) rather than little-endian (Intel order).</summary>
    public bool IsBigEndian { get; }

    /// <summary>
    /// Gets whether the block was damaged when read: an offset outside it, a loop between IFDs, or more data than allowed. What
    /// could be read is available; the original bytes are kept and written back unless the block is edited.
    /// </summary>
    public bool IsDamaged { get; private set; }

    /// <summary>Gets what was wrong with a damaged block, and what changed when an edited block was written, as sentences.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Gets every entry, IFD by IFD (IFD0, EXIF, GPS, interoperability, IFD1), in the order stored.</summary>
    public IReadOnlyList<ExifEntry> Entries => [.. _ifds.SelectMany(ifd => ifd)];

    /// <summary>Gets the embedded thumbnail (usually a small JPEG), or empty.</summary>
    public ReadOnlyMemory<byte> Thumbnail { get; private set; }

    /// <summary>Gets or sets the orientation (IFD0 0x0112). Missing or out-of-range values read as <see cref="ImageOrientation.Normal"/>.</summary>
    public ImageOrientation Orientation
    {
        get => TryGet(ExifKey.Orientation, out ExifEntry? entry) && entry.Type is ExifType.Short or ExifType.Long && entry.Count > 0
            && entry.AsInteger() is >= 1 and <= 8 and long value ? (ImageOrientation)value : ImageOrientation.Normal;
        set => Set(ExifKey.Orientation, (ushort)value);
    }

    /// <summary>Gets when the photo was taken: <see cref="ExifKey.DateTimeOriginal"/>, else <see cref="ExifKey.DateTime"/>, or <see langword="null"/>.</summary>
    public DateTime? DateTaken => Date(ExifKey.DateTimeOriginal) ?? Date(ExifKey.DateTime);

    /// <summary>Gets the camera maker, or <see langword="null"/>.</summary>
    public string? CameraMake => Text(ExifKey.Make);

    /// <summary>Gets the camera model, or <see langword="null"/>.</summary>
    public string? CameraModel => Text(ExifKey.Model);

    /// <summary>Gets the GPS position, or <see langword="null"/> when the block has no complete latitude and longitude.</summary>
    public GeoPosition? Location
    {
        get
        {
            if (Degrees(ExifKey.GpsLatitude) is not double latitude || Degrees(ExifKey.GpsLongitude) is not double longitude)
            {
                return null;
            }

            latitude = Text(ExifKey.GpsLatitudeRef) == "S" ? -latitude : latitude;
            longitude = Text(ExifKey.GpsLongitudeRef) == "W" ? -longitude : longitude;
            double? altitude = null;
            if (TryGet(ExifKey.GpsAltitude, out ExifEntry? entry) && entry.Count > 0 && entry.Type != ExifType.Ascii)
            {
                double meters = entry.AsDouble();
                bool below = TryGet(ExifKey.GpsAltitudeRef, out ExifEntry? reference) && reference.Count > 0 && reference.Type != ExifType.Ascii && reference.AsInteger() == 1;
                altitude = below ? -meters : meters;
            }

            return new GeoPosition(latitude, longitude, altitude);
        }
    }

    /// <summary>Reads an EXIF block: a TIFF header and its IFDs, without the "Exif\0\0" prefix JPEG puts before it. Never throws.</summary>
    /// <param name="tiff">The block.</param>
    /// <returns>The parsed block; <see cref="IsDamaged"/> says whether it was all readable.</returns>
    public static ExifData Parse(ReadOnlySpan<byte> tiff) => Parse(tiff, int.MaxValue);

    /// <summary>Returns the entry for <paramref name="key"/>, if the block has it.</summary>
    /// <param name="key">The tag and IFD.</param>
    /// <param name="entry">The entry, or <see langword="null"/>.</param>
    /// <returns>Whether the block has the tag.</returns>
    public bool TryGet(ExifKey key, [NotNullWhen(true)] out ExifEntry? entry)
    {
        entry = _ifds[(int)key.Ifd].Find(e => e.Key == key);
        return entry is not null;
    }

    /// <summary>Sets a text value (ASCII type; other characters are stored as UTF-8, as many programs do).</summary>
    /// <param name="key">The tag and IFD.</param>
    /// <param name="value">The text.</param>
    public void Set(ExifKey key, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        byte[] bytes = [.. Encoding.UTF8.GetBytes(value), 0];
        Put(key, ExifType.Ascii, bytes.Length, bytes);
    }

    /// <summary>Sets a 16-bit unsigned value (SHORT type).</summary>
    /// <param name="key">The tag and IFD.</param>
    /// <param name="value">The value.</param>
    public void Set(ExifKey key, ushort value)
    {
        byte[] bytes = new byte[2];
        if (IsBigEndian)
        {
            BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        }

        Put(key, ExifType.Short, 1, bytes);
    }

    /// <summary>Sets a 32-bit unsigned value (LONG type).</summary>
    /// <param name="key">The tag and IFD.</param>
    /// <param name="value">The value.</param>
    public void Set(ExifKey key, uint value)
    {
        byte[] bytes = new byte[4];
        WriteUInt32(bytes, value);
        Put(key, ExifType.Long, 1, bytes);
    }

    /// <summary>Sets one or more fractions: RATIONAL type, or SRATIONAL when any part is negative.</summary>
    /// <param name="key">The tag and IFD.</param>
    /// <param name="values">The fractions; each part must fit 32 bits.</param>
    /// <exception cref="ArgumentException"><paramref name="values"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A numerator or denominator does not fit 32 bits.</exception>
    public void Set(ExifKey key, params ReadOnlySpan<ExifFraction> values)
    {
        if (values.IsEmpty)
        {
            throw new ArgumentException("At least one value is needed.", nameof(values));
        }

        bool signed = false;
        foreach (ExifFraction f in values)
        {
            signed |= f.Numerator < 0 || f.Denominator < 0;
        }

        byte[] bytes = new byte[values.Length * 8];
        for (int i = 0; i < values.Length; i++)
        {
            WriteUInt32(bytes.AsSpan(i * 8), Part(values[i].Numerator, signed));
            WriteUInt32(bytes.AsSpan((i * 8) + 4), Part(values[i].Denominator, signed));
        }

        Put(key, signed ? ExifType.SRational : ExifType.Rational, values.Length, bytes);
    }

    /// <summary>Sets a value of any type from its raw bytes, in this block's byte order.</summary>
    /// <param name="key">The tag and IFD.</param>
    /// <param name="type">The TIFF type.</param>
    /// <param name="value">The bytes: a whole number of values of <paramref name="type"/>.</param>
    /// <exception cref="ArgumentException">The type is unknown or the bytes are not a whole number of values.</exception>
    public void Set(ExifKey key, ExifType type, ReadOnlySpan<byte> value)
    {
        int size = ExifEntry.Size(type);
        if (size == 0 || value.Length % size != 0)
        {
            throw new ArgumentException($"{value.Length} bytes are not a whole number of {type} values.", nameof(value));
        }

        Put(key, type, value.Length / size, value.ToArray());
    }

    /// <summary>
    /// Removes a tag. Removing <see cref="ExifKey.GpsInfo"/> or <see cref="ExifKey.ExifInfo"/> removes that whole sub-IFD.
    /// </summary>
    /// <param name="key">The tag and IFD.</param>
    /// <returns>Whether anything was removed.</returns>
    public bool Remove(ExifKey key)
    {
        bool removed;
        if (key == ExifKey.GpsInfo)
        {
            removed = _ifds[(int)ExifIfd.Gps].Count > 0;
            _ifds[(int)ExifIfd.Gps].Clear();
        }
        else if (key == ExifKey.ExifInfo)
        {
            removed = _ifds[(int)ExifIfd.Exif].Count + _ifds[(int)ExifIfd.Interoperability].Count > 0;
            _ifds[(int)ExifIfd.Exif].Clear();
            _ifds[(int)ExifIfd.Interoperability].Clear();
        }
        else
        {
            removed = _ifds[(int)key.Ifd].RemoveAll(e => e.Key == key) > 0;
        }

        _edited |= removed;
        return removed;
    }

    /// <summary>Removes the thumbnail and the rest of IFD1, as operations do once the image no longer matches it.</summary>
    /// <returns>Whether there was one.</returns>
    public bool RemoveThumbnail()
    {
        bool removed = !Thumbnail.IsEmpty || _ifds[(int)ExifIfd.Thumbnail].Count > 0;
        Thumbnail = default;
        _ifds[(int)ExifIfd.Thumbnail].Clear();
        _edited |= removed;
        return removed;
    }

    /// <summary>
    /// Returns the block as bytes: the bytes it was read from if nothing was edited, else the block laid out again. A maker note
    /// that cannot stay at its old offset adds a warning to <see cref="Warnings"/>.
    /// </summary>
    /// <returns>The TIFF bytes.</returns>
    public ReadOnlyMemory<byte> ToBytes() => _edited ? Build() : _original;

    /// <summary>Returns an independent copy.</summary>
    /// <returns>The copy.</returns>
    public ExifData Copy()
    {
        ExifData copy = new(IsBigEndian)
        {
            _original = _original,
            _edited = _edited,
            IsDamaged = IsDamaged,
            Thumbnail = Thumbnail,
        };
        for (int i = 0; i < _ifds.Length; i++)
        {
            copy._ifds[i].AddRange(_ifds[i]);
        }

        copy._warnings.AddRange(_warnings);
        return copy;
    }

    /// <summary>Parses with a cap on the bytes of values read, from <see cref="SafetyLimits.MaxMetadataBytes"/>.</summary>
    internal static ExifData Parse(ReadOnlySpan<byte> tiff, long maxBytes)
    {
        bool bigEndian = tiff.Length >= 2 && tiff[0] == (byte)'M' && tiff[1] == (byte)'M';
        ExifData data = new(bigEndian) { _original = tiff.ToArray() };
        if (tiff.Length < 8 || !(bigEndian || (tiff[0] == (byte)'I' && tiff[1] == (byte)'I')) || data.ReadUInt16(tiff, 2) != 42)
        {
            data.Damaged("The EXIF block does not start with a TIFF header.");
            return data;
        }

        Reader reader = new(data, tiff, maxBytes);
        uint next = reader.ReadIfd(data.ReadUInt32(tiff, 4), ExifIfd.Primary);
        if (next != 0)
        {
            reader.ReadIfd(next, ExifIfd.Thumbnail);
        }

        return data;
    }

    private static uint Part(long value, bool signed)
    {
        if (signed ? value is < int.MinValue or > int.MaxValue : value is < 0 or > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Rational parts must fit 32 bits.");
        }

        return (uint)value;
    }

    private void Put(ExifKey key, ExifType type, int count, byte[] bytes)
    {
        if (key == ExifKey.GpsInfo || key == ExifKey.ExifInfo || (key.Ifd == ExifIfd.Exif && key.Tag == InteropPointer)
            || (key.Ifd == ExifIfd.Thumbnail && key.Tag is ThumbnailOffset or ThumbnailLength))
        {
            throw new ArgumentException($"{key} is laid out by Egami and cannot be set.", nameof(key));
        }

        List<ExifEntry> ifd = _ifds[(int)key.Ifd];
        ExifEntry entry = new(key, type, count, bytes, IsBigEndian);
        int at = ifd.FindIndex(e => e.Key == key);
        if (at >= 0)
        {
            ifd[at] = entry;
        }
        else
        {
            ifd.Add(entry);
        }

        _edited = true;
    }

    private void Damaged(string problem)
    {
        if (!IsDamaged)
        {
            IsDamaged = true;
            _warnings.Add(problem);
        }
    }

    private string? Text(ExifKey key) => TryGet(key, out ExifEntry? entry) && entry.Type == ExifType.Ascii ? entry.AsString().Trim() : null;

    private DateTime? Date(ExifKey key)
    {
        return Text(key) is string text && DateTime.TryParseExact(text, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date)
            ? date
            : null;
    }

    private double? Degrees(ExifKey key)
    {
        if (!TryGet(key, out ExifEntry? entry) || entry.Type is not (ExifType.Rational or ExifType.SRational) || entry.Count < 1)
        {
            return null;
        }

        double degrees = 0;
        double scale = 1;
        for (int i = 0; i < Math.Min(3, entry.Count); i++, scale *= 60)
        {
            double part = entry.AsFraction(i).ToDouble();
            if (double.IsNaN(part))
            {
                return null;
            }

            degrees += part / scale;
        }

        return degrees;
    }

    private ushort ReadUInt16(ReadOnlySpan<byte> data, int at)
    {
        return IsBigEndian ? BinaryPrimitives.ReadUInt16BigEndian(data[at..]) : BinaryPrimitives.ReadUInt16LittleEndian(data[at..]);
    }

    private uint ReadUInt32(ReadOnlySpan<byte> data, int at)
    {
        return IsBigEndian ? BinaryPrimitives.ReadUInt32BigEndian(data[at..]) : BinaryPrimitives.ReadUInt32LittleEndian(data[at..]);
    }

    private void WriteUInt16(Span<byte> data, ushort value)
    {
        if (IsBigEndian)
        {
            BinaryPrimitives.WriteUInt16BigEndian(data, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(data, value);
        }
    }

    private void WriteUInt32(Span<byte> data, uint value)
    {
        if (IsBigEndian)
        {
            BinaryPrimitives.WriteUInt32BigEndian(data, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data, value);
        }
    }

    /// <summary>
    /// Lays the block out again: the header, then each IFD followed by its values that do not fit in an entry, in the order IFD0,
    /// EXIF, interoperability, GPS, IFD1, then the thumbnail. The maker note goes last, at its old offset when everything before
    /// it fits below that, since its own internal offsets may count from the start of the block.
    /// </summary>
    private byte[] Build()
    {
        ExifIfd[] order = [ExifIfd.Primary, ExifIfd.Exif, ExifIfd.Interoperability, ExifIfd.Gps, ExifIfd.Thumbnail];
        bool hasInterop = _ifds[(int)ExifIfd.Interoperability].Count > 0;
        bool hasExif = _ifds[(int)ExifIfd.Exif].Count > 0 || hasInterop;
        bool hasGps = _ifds[(int)ExifIfd.Gps].Count > 0;
        bool hasThumbnail = _ifds[(int)ExifIfd.Thumbnail].Count > 0 || !Thumbnail.IsEmpty;

        // Each IFD's entries, structural pointers included, sorted by tag as TIFF requires.
        Dictionary<ExifIfd, List<(ushort Tag, ExifEntry? Entry)>> tables = [];
        foreach (ExifIfd kind in order)
        {
            List<(ushort, ExifEntry?)> table = [.. _ifds[(int)kind].Where(e => e.Key.Tag != ExifKey.MakerNote.Tag || kind != ExifIfd.Exif).Select(e => (e.Key.Tag, (ExifEntry?)e))];
            if (kind == ExifIfd.Exif && TryGet(ExifKey.MakerNote, out ExifEntry? makerNote))
            {
                table.Add((makerNote.Key.Tag, makerNote));
            }

            if (kind == ExifIfd.Primary && hasExif)
            {
                table.Add((ExifPointer, null));
            }

            if (kind == ExifIfd.Primary && hasGps)
            {
                table.Add((GpsPointer, null));
            }

            if (kind == ExifIfd.Exif && hasInterop)
            {
                table.Add((InteropPointer, null));
            }

            if (kind == ExifIfd.Thumbnail && !Thumbnail.IsEmpty)
            {
                table.Add((ThumbnailOffset, null));
                table.Add((ThumbnailLength, null));
            }

            table.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            tables[kind] = table;
        }

        bool Present(ExifIfd kind) => kind switch
        {
            ExifIfd.Primary => true,
            ExifIfd.Exif => hasExif,
            ExifIfd.Interoperability => hasInterop,
            ExifIfd.Gps => hasGps,
            _ => hasThumbnail,
        };

        // Pass 1: offsets of each IFD, each out-of-line value and the thumbnail.
        Dictionary<ExifIfd, int> ifdOffset = [];
        Dictionary<ExifEntry, int> valueOffset = [];
        ExifEntry? note = null;
        int position = 8;
        foreach (ExifIfd kind in order.Where(Present))
        {
            ifdOffset[kind] = position;
            position += 2 + (12 * tables[kind].Count) + 4;
            foreach ((_, ExifEntry? entry) in tables[kind])
            {
                if (entry is null || entry.RawValue.Length <= 4)
                {
                    continue;
                }

                if (entry.Key == ExifKey.MakerNote)
                {
                    note = entry;
                    continue;
                }

                valueOffset[entry] = position;
                position += entry.RawValue.Length + (entry.RawValue.Length & 1);
            }
        }

        int thumbnailAt = position;
        position += Thumbnail.Length + (Thumbnail.Length & 1);
        if (note is not null)
        {
            if (note.Offset >= position)
            {
                position = note.Offset;
            }
            else if (note.Offset >= 0)
            {
                _warnings.Add("The maker note had to move when the EXIF block was rebuilt; offsets inside it may no longer be right.");
            }

            valueOffset[note] = position;
            position += note.RawValue.Length;
        }

        // Pass 2: write.
        byte[] block = new byte[position];
        block[0] = block[1] = IsBigEndian ? (byte)'M' : (byte)'I';
        WriteUInt16(block.AsSpan(2), 42);
        WriteUInt32(block.AsSpan(4), 8);
        ExifIfd[] present = [.. order.Where(Present)];
        foreach (ExifIfd kind in present)
        {
            int at = ifdOffset[kind];
            List<(ushort Tag, ExifEntry? Entry)> table = tables[kind];
            WriteUInt16(block.AsSpan(at), (ushort)table.Count);
            at += 2;
            foreach ((ushort tag, ExifEntry? entry) in table)
            {
                Span<byte> slot = block.AsSpan(at, 12);
                WriteUInt16(slot, tag);
                if (entry is null)
                {
                    // A pointer or the thumbnail's offset and length, all LONG.
                    WriteUInt16(slot[2..], (ushort)ExifType.Long);
                    WriteUInt32(slot[4..], 1);
                    uint value = tag switch
                    {
                        ExifPointer => (uint)ifdOffset[ExifIfd.Exif],
                        GpsPointer => (uint)ifdOffset[ExifIfd.Gps],
                        InteropPointer => (uint)ifdOffset[ExifIfd.Interoperability],
                        ThumbnailOffset => (uint)thumbnailAt,
                        _ => (uint)Thumbnail.Length,
                    };
                    WriteUInt32(slot[8..], value);
                }
                else
                {
                    WriteUInt16(slot[2..], (ushort)entry.Type);
                    WriteUInt32(slot[4..], (uint)entry.Count);
                    ReadOnlySpan<byte> value = entry.RawValue.Span;
                    if (value.Length <= 4)
                    {
                        value.CopyTo(slot[8..]);
                    }
                    else
                    {
                        WriteUInt32(slot[8..], (uint)valueOffset[entry]);
                        value.CopyTo(block.AsSpan(valueOffset[entry]));
                    }
                }

                at += 12;
            }

            // IFD0 links to IFD1; the others end the chain.
            WriteUInt32(block.AsSpan(at), kind == ExifIfd.Primary && hasThumbnail ? (uint)ifdOffset[ExifIfd.Thumbnail] : 0);
        }

        Thumbnail.Span.CopyTo(block.AsSpan(thumbnailAt));
        _original = block;
        _edited = false;
        return block;
    }

    /// <summary>Walks IFDs with bounds checks, a limit on value bytes and protection against IFDs that point back at each other.</summary>
    private ref struct Reader(ExifData data, ReadOnlySpan<byte> tiff, long maxBytes)
    {
        private readonly ExifData _data = data;
        private readonly ReadOnlySpan<byte> _tiff = tiff;
        private readonly HashSet<uint> _visited = [];
        private long _bytes;

        /// <summary>Reads the IFD at <paramref name="offset"/> into <paramref name="kind"/> and its sub-IFDs; returns the next IFD's offset.</summary>
        internal uint ReadIfd(uint offset, ExifIfd kind)
        {
            if (offset < 8 || offset > _tiff.Length - 2)
            {
                _data.Damaged($"An EXIF IFD offset ({offset}) is outside the block.");
                return 0;
            }

            if (!_visited.Add(offset))
            {
                _data.Damaged("EXIF IFDs point back at each other; the loop was cut.");
                return 0;
            }

            int count = _data.ReadUInt16(_tiff, (int)offset);
            int at = (int)offset + 2;
            if (at + (12L * count) > _tiff.Length)
            {
                _data.Damaged("An EXIF IFD runs past the end of the block.");
                count = (_tiff.Length - at) / 12;
            }

            uint thumbnailOffset = 0;
            uint thumbnailLength = 0;
            for (int i = 0; i < count; i++, at += 12)
            {
                ReadOnlySpan<byte> slot = _tiff.Slice(at, 12);
                ushort tag = _data.ReadUInt16(slot, 0);
                ExifType type = (ExifType)_data.ReadUInt16(slot, 2);
                uint n = _data.ReadUInt32(slot, 4);
                uint value = _data.ReadUInt32(slot, 8);

                // Sub-IFDs and the thumbnail are structure, laid out again when the block is written.
                if (kind == ExifIfd.Primary && tag is ExifPointer or GpsPointer)
                {
                    ReadIfd(value, tag == ExifPointer ? ExifIfd.Exif : ExifIfd.Gps);
                    continue;
                }

                if (kind == ExifIfd.Exif && tag == InteropPointer)
                {
                    ReadIfd(value, ExifIfd.Interoperability);
                    continue;
                }

                if (kind == ExifIfd.Thumbnail && tag is ThumbnailOffset or ThumbnailLength)
                {
                    (thumbnailOffset, thumbnailLength) = tag == ThumbnailOffset ? (value, thumbnailLength) : (thumbnailOffset, value);
                    continue;
                }

                int size = ExifEntry.Size(type);
                long length = (long)size * n;
                if (size == 0)
                {
                    // An unknown type has no known size; such entries are left out.
                    continue;
                }

                _bytes += length;
                if (_bytes > maxBytes || length > _tiff.Length)
                {
                    _data.Damaged("The EXIF block holds more data than it can or than the metadata limit allows.");
                    return 0;
                }

                int start = length <= 4 ? at + 8 : (int)Math.Min(value, int.MaxValue);
                if (length > 4 && (value < 8 || value + length > _tiff.Length))
                {
                    _data.Damaged($"The value of EXIF tag 0x{tag:X4} is outside the block.");
                    continue;
                }

                _data._ifds[(int)kind].Add(new ExifEntry(new ExifKey(tag, kind), type, (int)n, _tiff.Slice(start, (int)length).ToArray(), _data.IsBigEndian, length > 4 ? start : -1));
            }

            if (kind == ExifIfd.Thumbnail && thumbnailLength > 0)
            {
                if (thumbnailOffset >= 8 && thumbnailOffset + (long)thumbnailLength <= _tiff.Length)
                {
                    _data.Thumbnail = _tiff.Slice((int)thumbnailOffset, (int)thumbnailLength).ToArray();
                }
                else
                {
                    _data.Damaged("The EXIF thumbnail is outside the block.");
                }
            }

            return at + 4 <= _tiff.Length ? _data.ReadUInt32(_tiff, at) : 0;
        }
    }
}
