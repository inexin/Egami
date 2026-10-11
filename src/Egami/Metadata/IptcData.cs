using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace Egami.Metadata;

/// <summary>
/// Parsed IPTC-IIM datasets (IPTC-IIM 4.2), as news agencies, stock libraries and Lightroom write them next to XMP, with typed
/// getters for the common ones. Shaped like <see cref="ExifData"/>: an unedited block writes back byte for byte; after an edit
/// it is rebuilt in record and dataset order, record 1 first, with the coded character set (1:90) set to UTF-8 whenever a value
/// is not plain ASCII.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ApiExamples.cs" region="iptc-values"/>
/// </example>
public sealed class IptcData
{
    private const byte Marker = 0x1C;
    private static readonly byte[] Utf8Escape = [0x1B, 0x25, 0x47];

    private readonly List<IptcEntry> _entries = [];
    private List<string>? _warnings;
    private readonly byte[] _original = [];
    private bool _edited;

    /// <summary>Creates an empty block, to fill with <see cref="Set"/> and <see cref="Add"/>.</summary>
    public IptcData()
    {
        _edited = true;
    }

    private IptcData(byte[] original)
    {
        _original = original;
    }

    /// <summary>
    /// Gets whether the block was damaged when read: a dataset running past the end, bytes that are not datasets, or more data than
    /// allowed. What could be read is available; the original bytes are written back unless the block is edited.
    /// </summary>
    public bool IsDamaged { get; private set; }

    /// <summary>Gets what was wrong with the block when read (damage, values longer than the specification allows), as sentences.</summary>
    public IReadOnlyList<string> Warnings => _warnings ?? [];

    /// <summary>Gets every dataset in the order stored.</summary>
    public IReadOnlyList<IptcEntry> Entries => _entries;

    /// <summary>Gets whether text is UTF-8: the coded character set (1:90) is ESC % G.</summary>
    public bool IsUtf8 => _entries.Find(e => e.Key == IptcKey.CodedCharacterSet) is IptcEntry charset && charset.RawValue.Span.SequenceEqual(Utf8Escape);

    /// <summary>Gets the keywords (2:25), in order.</summary>
    public IReadOnlyList<string> Keywords => GetAll(IptcKey.Keywords);

    /// <summary>Gets the creators (2:80), in order.</summary>
    public IReadOnlyList<string> Bylines => GetAll(IptcKey.Byline);

    /// <summary>Gets the caption (2:120), or <see langword="null"/>.</summary>
    public string? Caption => Get(IptcKey.Caption);

    /// <summary>Gets the headline (2:105), or <see langword="null"/>.</summary>
    public string? Headline => Get(IptcKey.Headline);

    /// <summary>Gets the copyright notice (2:116), or <see langword="null"/>.</summary>
    public string? Copyright => Get(IptcKey.CopyrightNotice);

    /// <summary>Gets the credit (2:110), or <see langword="null"/>.</summary>
    public string? Credit => Get(IptcKey.Credit);

    /// <summary>
    /// Gets when the content was created, from the date (2:55) and time (2:60): midnight UTC when there is no valid time, and
    /// <see langword="null"/> when there is no valid date.
    /// </summary>
    public DateTimeOffset? DateCreated
    {
        get
        {
            if (Get(IptcKey.DateCreated) is not string date
                || !DateTime.TryParseExact(date, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime day))
            {
                return null;
            }

            if (Get(IptcKey.TimeCreated) is string time && time.Length >= 6
                && int.TryParse(time.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int h) && h < 24
                && int.TryParse(time.AsSpan(2, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int m) && m < 60
                && int.TryParse(time.AsSpan(4, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int s) && s < 60)
            {
                TimeSpan offset = TimeSpan.Zero;
                if (time.Length >= 11 && time[6] is '+' or '-'
                    && int.TryParse(time.AsSpan(7, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int oh) && oh <= 14
                    && int.TryParse(time.AsSpan(9, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int om) && om < 60)
                {
                    offset = new TimeSpan(oh, om, 0) * (time[6] == '-' ? -1 : 1);
                }

                return new DateTimeOffset(day.Year, day.Month, day.Day, h, m, s, offset);
            }

            return new DateTimeOffset(day, TimeSpan.Zero);
        }
    }

    /// <summary>
    /// Reads IPTC-IIM datasets: tag markers 0x1C, each with a record, a dataset, a length and the value. Never throws: a block that
    /// runs past its end or past <see cref="SafetyLimits.MaxMetadataBytes"/> keeps what was read and is marked damaged.
    /// </summary>
    /// <param name="iim">The datasets, without any Photoshop resource wrapper.</param>
    /// <param name="limits">The limits whose <see cref="SafetyLimits.MaxMetadataBytes"/> caps the values copied. Defaults to <see cref="SafetyLimits.Default"/>.</param>
    /// <returns>The parsed block; <see cref="IsDamaged"/> says whether it was all readable.</returns>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/ApiExamples.cs" region="iptc-parse"/>
    /// </example>
    public static IptcData Parse(ReadOnlySpan<byte> iim, SafetyLimits? limits = null) => Parse(iim, (limits ?? SafetyLimits.Default).MaxMetadataBytes);

    /// <summary>Returns the entry for the first dataset with <paramref name="key"/>, if the block has one.</summary>
    /// <param name="key">The record and dataset.</param>
    /// <param name="entry">The entry, or <see langword="null"/>.</param>
    /// <returns>Whether the block has the dataset.</returns>
    public bool TryGet(IptcKey key, [NotNullWhen(true)] out IptcEntry? entry)
    {
        entry = _entries.Find(e => e.Key == key);
        return entry is not null;
    }

    /// <summary>Returns the first value of <paramref name="key"/> as text, or <see langword="null"/>.</summary>
    /// <param name="key">The record and dataset.</param>
    /// <returns>The text.</returns>
    public string? Get(IptcKey key) => TryGet(key, out IptcEntry? entry) ? entry.AsString() : null;

    /// <summary>Returns every value of <paramref name="key"/> as text, in order.</summary>
    /// <param name="key">The record and dataset.</param>
    /// <returns>The values; empty when there are none.</returns>
    public IReadOnlyList<string> GetAll(IptcKey key)
    {
        List<string> values = [];
        foreach (IptcEntry entry in _entries)
        {
            if (entry.Key.Record == key.Record && entry.Key.Dataset == key.Dataset)
            {
                values.Add(entry.AsString());
            }
        }

        return values;
    }

    /// <summary>Replaces every value of <paramref name="key"/> with one value.</summary>
    /// <param name="key">The record and dataset.</param>
    /// <param name="value">The text, stored as UTF-8 (with 1:90 set to UTF-8 when it is not ASCII).</param>
    /// <exception cref="ArgumentException">The value is longer than <see cref="IptcKey.MaxLength"/> in UTF-8; long text belongs in XMP. Or the key is the coded character set, which Egami keeps.</exception>
    public void Set(IptcKey key, string value)
    {
        byte[] bytes = Encode(key, value);
        int at = _entries.FindIndex(e => e.Key == key);
        _entries.RemoveAll(e => e.Key == key);
        _entries.Insert(at < 0 ? _entries.Count : at, new IptcEntry(key, bytes, IsUtf8));
        Edited(value);
    }

    /// <summary>Adds one more value to a repeatable dataset, such as a keyword.</summary>
    /// <param name="key">The record and dataset.</param>
    /// <param name="value">The text, stored as UTF-8 (with 1:90 set to UTF-8 when it is not ASCII).</param>
    /// <exception cref="ArgumentException">The dataset is not repeatable (<see cref="IptcKey.IsRepeatable"/>), or the value is longer than <see cref="IptcKey.MaxLength"/> in UTF-8.</exception>
    public void Add(IptcKey key, string value)
    {
        if (!key.IsRepeatable)
        {
            throw new ArgumentException($"IPTC {key} holds one value; use Set.", nameof(key));
        }

        byte[] bytes = Encode(key, value);
        int last = _entries.FindLastIndex(e => e.Key == key);
        _entries.Insert(last < 0 ? _entries.Count : last + 1, new IptcEntry(key, bytes, IsUtf8));
        Edited(value);
    }

    /// <summary>Removes every value of <paramref name="key"/>.</summary>
    /// <param name="key">The record and dataset.</param>
    /// <returns>Whether there was any.</returns>
    public bool Remove(IptcKey key)
    {
        if (_entries.RemoveAll(e => e.Key == key) == 0)
        {
            return false;
        }

        _edited = true;
        return true;
    }

    /// <summary>Returns the datasets as stored in a file: the original bytes when unedited, otherwise rebuilt.</summary>
    /// <returns>The bytes.</returns>
    public ReadOnlyMemory<byte> ToBytes() => _edited ? Build() : _original;

    /// <summary>Returns an independent copy.</summary>
    /// <returns>The copy.</returns>
    public IptcData Copy()
    {
        IptcData copy = new(_original) { _edited = _edited, IsDamaged = IsDamaged };
        copy._entries.AddRange(_entries);
        copy._warnings = _warnings is null ? null : [.. _warnings];
        return copy;
    }

    /// <summary>Parses with a cap on the bytes of values read, from <see cref="SafetyLimits.MaxMetadataBytes"/>.</summary>
    internal static IptcData Parse(ReadOnlySpan<byte> iim, long maxBytes)
    {
        // Values are slices of the one copy kept for writing back; the charset applies once the whole block is read.
        byte[] original = iim.ToArray();
        IptcData data = new(original);
        data._entries.Capacity = Math.Min(iim.Length / 16, 1 << 16);
        bool utf8 = false;
        long total = 0;
        int i = 0;
        while (i < iim.Length)
        {
            if (iim[i] != Marker)
            {
                // Zero padding after the last dataset is common (Photoshop pads resources); anything else is damage.
                if (iim[i..].ContainsAnyExcept((byte)0))
                {
                    data.Damaged($"Bytes at offset {i} are not an IPTC dataset.");
                }

                break;
            }

            if (iim.Length - i < 5)
            {
                data.Damaged("The last IPTC dataset is cut short.");
                break;
            }

            IptcKey key = new(iim[i + 1], iim[i + 2]);
            int header = 5;
            long length = BinaryPrimitives.ReadUInt16BigEndian(iim[(i + 3)..]);
            if ((length & 0x8000) != 0)
            {
                // Extended dataset: the low 15 bits count the bytes of the length that follows.
                int lengthBytes = (int)(length & 0x7FFF);
                if (lengthBytes is 0 or > 4 || iim.Length - i - 5 < lengthBytes)
                {
                    data.Damaged($"IPTC {key} has an extended length that cannot be read.");
                    break;
                }

                length = 0;
                for (int b = 0; b < lengthBytes; b++)
                {
                    length = (length << 8) | iim[i + 5 + b];
                }

                header += lengthBytes;
            }

            if (length > iim.Length - i - header)
            {
                data.Damaged($"IPTC {key} runs past the end of the block.");
                break;
            }

            total += length;
            if (total > maxBytes)
            {
                data.Damaged("The IPTC block holds more data than the safety limits allow.");
                break;
            }

            if (key.MaxLength is int max && length > max)
            {
                (data._warnings ??= []).Add($"IPTC {key} is {length} bytes, longer than the {max} the IIM specification allows.");
            }

            utf8 |= key.Record == 1 && key.Dataset == 90 && iim.Slice(i + header, (int)length).SequenceEqual(Utf8Escape);
            data._entries.Add(new IptcEntry(key, new ReadOnlyMemory<byte>(original, i + header, (int)length), false));
            i += header + (int)length;
        }

        if (utf8)
        {
            foreach (IptcEntry entry in data._entries)
            {
                entry.MarkUtf8();
            }
        }

        return data;
    }

    private static bool IsText(IptcKey key) => key.Record == 2 && key.Dataset is > 0 and < 200;

    private static byte[] Encode(IptcKey key, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (key == IptcKey.CodedCharacterSet)
        {
            throw new ArgumentException("Egami sets the coded character set itself, to UTF-8 whenever a value needs it.", nameof(key));
        }

        byte[] bytes = Encoding.UTF8.GetBytes(value);
        if (key.MaxLength is int max && bytes.Length > max)
        {
            throw new ArgumentException($"IPTC {key} allows {max} bytes, and the value is {bytes.Length} in UTF-8; long text belongs in XMP.", nameof(value));
        }

        return bytes;
    }

    private void Edited(string value)
    {
        _edited = true;
        if (Ascii.IsValid(value) || IsUtf8)
        {
            return;
        }

        // The block becomes UTF-8: text read with the old rule is re-encoded so it reads the same afterwards.
        for (int i = 0; i < _entries.Count; i++)
        {
            IptcEntry e = _entries[i];
            _entries[i] = IsText(e.Key) ? new IptcEntry(e.Key, Encoding.UTF8.GetBytes(e.AsString()), true) : e.WithCharset(true);
        }

        _entries.RemoveAll(e => e.Key == IptcKey.CodedCharacterSet);
        _entries.Insert(0, new IptcEntry(IptcKey.CodedCharacterSet, Utf8Escape.ToArray(), true));
    }

    private byte[] Build()
    {
        List<IptcEntry> entries = [.. _entries];
        if (entries.Exists(e => e.Key == IptcKey.CodedCharacterSet) && !entries.Exists(e => e.Key == IptcKey.EnvelopeRecordVersion))
        {
            entries.Add(new IptcEntry(IptcKey.EnvelopeRecordVersion, new byte[] { 0, 4 }, IsUtf8));
        }

        if (entries.Exists(e => e.Key.Record == 2) && !entries.Exists(e => e.Key == IptcKey.RecordVersion))
        {
            entries.Add(new IptcEntry(IptcKey.RecordVersion, new byte[] { 0, 4 }, IsUtf8));
        }

        // Record and dataset order (stable, so repeats keep theirs), as IIM requires.
        IptcEntry[] ordered = [.. entries.OrderBy(e => e.Key.Record).ThenBy(e => e.Key.Dataset)];
        using MemoryStream stream = new();
        Span<byte> header = stackalloc byte[9];
        foreach (IptcEntry e in ordered)
        {
            ReadOnlySpan<byte> value = e.RawValue.Span;
            header[0] = Marker;
            header[1] = e.Key.Record;
            header[2] = e.Key.Dataset;
            if (value.Length < 0x8000)
            {
                BinaryPrimitives.WriteUInt16BigEndian(header[3..], (ushort)value.Length);
                stream.Write(header[..5]);
            }
            else
            {
                BinaryPrimitives.WriteUInt16BigEndian(header[3..], 0x8004);
                BinaryPrimitives.WriteUInt32BigEndian(header[5..], (uint)value.Length);
                stream.Write(header);
            }

            stream.Write(value);
        }

        return stream.ToArray();
    }

    private void Damaged(string warning)
    {
        IsDamaged = true;
        (_warnings ??= []).Add(warning);
    }
}
