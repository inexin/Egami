using System.Buffers.Binary;
using System.Text;

namespace Egami.Formats.Extended.Raw;

/// <summary>
/// The TIFF structure RAW files are built on, read from TIFF 6.0 and TIFF/EP over the whole file in memory: the header, IFDs,
/// and typed values. Maker formats with their own magic number (ORF, RW2) read through it too. Every offset and count is checked
/// against the data before it is used; a value outside the data reads as missing.
/// </summary>
internal sealed class RawTiff
{
    private const int MaxEntries = 4096;

    public RawTiff(RawSource source, bool bigEndian, int origin = 0)
    {
        Source = source;
        BigEndian = bigEndian;
        Origin = origin;
    }

    /// <summary>Gets the file's bytes as far as they have been read.</summary>
    public RawSource Source { get; }

    /// <summary>Gets the bytes read so far; only the first <see cref="Length"/> are data.</summary>
    public byte[] Data => Source.Data;

    /// <summary>Gets how many bytes have been read so far.</summary>
    public int Length => Source.Length;

    /// <summary>Gets whether values are big-endian (<c>MM</c>).</summary>
    public bool BigEndian { get; }

    /// <summary>Gets the position offsets count from: 0 for a plain file, the start of an embedded TIFF otherwise.</summary>
    public int Origin { get; }

    /// <summary>Reads a TIFF header at the start of <paramref name="data"/>: the byte order, any magic number, and the first IFD offset.</summary>
    public static bool TryHeader(ReadOnlySpan<byte> data, out bool bigEndian, out ushort magic, out uint firstIfd)
    {
        bigEndian = false;
        magic = 0;
        firstIfd = 0;
        if (data.Length < 8 || data[0] != data[1] || (data[0] != 'I' && data[0] != 'M'))
        {
            return false;
        }

        bigEndian = data[0] == 'M';
        magic = bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(data[2..]) : BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
        firstIfd = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(data[4..]) : BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        return true;
    }

    /// <summary>Reads the IFD at <paramref name="offset"/> (counted from <see cref="Origin"/>), or returns <see langword="null"/> when it does not fit.</summary>
    public RawIfd? ReadIfd(long offset)
    {
        // An offset beyond what an array can hold is no IFD (and would overflow the arithmetic below).
        long at = Origin + offset;
        if (offset <= 0 || offset > int.MaxValue - 16L - Origin || !Source.Ensure(at + 2))
        {
            return null;
        }

        int count = U16((int)at);
        if (count == 0 || count > MaxEntries || !Source.Ensure(at + 2 + (count * 12L) + 4) && at + 2 + (count * 12L) > Length)
        {
            return null;
        }

        Dictionary<int, RawEntry> entries = new(count);
        for (int i = 0; i < count; i++)
        {
            int e = (int)at + 2 + (i * 12);
            int tag = U16(e);
            int type = U16(e + 2);
            uint n = U32(e + 4);
            int size = RawEntry.Size(type);
            // A count is checked against what the value needs, not against what is read so far: the file is read lazily, and a
            // large value (a maker note of a megabyte or more) lies beyond the first bytes.
            if (size == 0 || n == 0 || (long)n * size > int.MaxValue)
            {
                continue;
            }

            long bytes = (long)n * size;
            long valueAt = e + 8;
            if (bytes > 4)
            {
                long pointer = Origin + (long)U32(e + 8);
                valueAt = Source.Ensure(pointer + bytes) ? pointer : -1;
            }

            entries.TryAdd(tag, new RawEntry(tag, type, n, valueAt));
        }

        long nextAt = at + 2 + (count * 12L);
        long next = nextAt + 4 <= Length ? U32((int)nextAt) : 0;
        return new RawIfd(this, offset, entries, next);
    }

    /// <summary>Reads the IFD chain from <paramref name="first"/>, at most <paramref name="max"/> IFDs, stopping at a loop.</summary>
    public List<RawIfd> Chain(long first, int max)
    {
        List<RawIfd> list = [];
        HashSet<long> seen = [];
        long offset = first;
        while (offset > 0 && list.Count < max && seen.Add(offset) && ReadIfd(offset) is RawIfd ifd)
        {
            list.Add(ifd);
            offset = ifd.Next;
        }

        return list;
    }

    public ushort U16(int at) => BigEndian ? BinaryPrimitives.ReadUInt16BigEndian(Data.AsSpan(at)) : BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(at));

    public uint U32(int at) => BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(Data.AsSpan(at)) : BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(at));

    public ulong U64(int at) => BigEndian ? BinaryPrimitives.ReadUInt64BigEndian(Data.AsSpan(at)) : BinaryPrimitives.ReadUInt64LittleEndian(Data.AsSpan(at));

    /// <summary>Returns value <paramref name="index"/> of <paramref name="entry"/> as a number, whatever its type.</summary>
    public double Number(RawEntry entry, long index)
    {
        int size = entry.ValueSize;
        int at = (int)(entry.ValueAt + (index * size));
        return entry.Type switch
        {
            1 or 7 => Data[at],
            2 => Data[at],
            6 => (sbyte)Data[at],
            3 => U16(at),
            8 => (short)U16(at),
            4 or 13 => U32(at),
            9 => (int)U32(at),
            5 => U32(at + 4) == 0 ? 0 : U32(at) / (double)U32(at + 4),
            10 => U32(at + 4) == 0 ? 0 : (int)U32(at) / (double)(int)U32(at + 4),
            11 => BitConverter.Int32BitsToSingle((int)U32(at)),
            12 => BitConverter.Int64BitsToDouble((long)U64(at)),
            16 or 18 => U64(at),
            17 => (long)U64(at),
            _ => 0,
        };
    }
}

/// <summary>
/// A RAW file's bytes, read from the input as far as they are needed: Probe reads only up to the last IFD and tag value it
/// looks at, a load reads on to the sensor data. The whole file stays in one pooled array, within the load's memory budget, and
/// goes back to the pool on <see cref="Dispose"/>.
/// </summary>
internal sealed class RawSource : IDisposable
{
    private readonly EgamiInput? _input;
    private readonly long _max;
    private bool _ended;
    private bool _pooled;

    public RawSource(EgamiInput input)
    {
        _input = input;
        _max = Math.Min(input.Limits.MaxTotalBytes, Array.MaxLength);
        Data = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024);
        _pooled = true;
    }

    public RawSource(byte[] data, int length)
    {
        Data = data;
        Length = length;
        _ended = true;
    }

    public byte[] Data { get; private set; }

    public int Length { get; private set; }

    /// <summary>Reads on until the first <paramref name="end"/> bytes are in; returns whether they are (the file may be shorter).</summary>
    public bool Ensure(long end)
    {
        if (end <= Length)
        {
            return true;
        }

        if (_ended || _input is null || end > _max)
        {
            return false;
        }

        if (end > Data.Length)
        {
            long size = Math.Min(Math.Max(end, Math.Min((long)Data.Length * 2, _max)), _max);
            _input.CheckMemory(size);
            byte[] larger = System.Buffers.ArrayPool<byte>.Shared.Rent((int)size);
            Data.AsSpan(0, Length).CopyTo(larger);
            Return();
            Data = larger;
            _pooled = true;
        }

        while (Length < end)
        {
            int n = _input.Read(Data.AsSpan(Length, (int)Math.Min(Data.Length - Length, Math.Max(end - Length, 256 * 1024))));
            if (n == 0)
            {
                _ended = true;
                break;
            }

            Length += n;
        }

        return end <= Length;
    }

    public void Dispose()
    {
        Return();
        Data = [];
        Length = 0;
        _ended = true;
    }

    private void Return()
    {
        if (_pooled)
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(Data);
            _pooled = false;
        }
    }

    /// <summary>Reads the rest of the file.</summary>
    public void ReadAll()
    {
        while (!_ended && Ensure(Math.Min((long)Math.Max(Data.Length, Length + 1) * 2, _max)) && Length < _max)
        {
        }
    }
}

/// <summary>One IFD entry: its tag, type, value count and where the values lie, or −1 when they lie outside the data.</summary>
internal readonly record struct RawEntry(int Tag, int Type, uint Count, long ValueAt)
{
    public int ValueSize => Size(Type);

    public long ByteCount => Count * (long)ValueSize;

    public static int Size(int type) => type switch
    {
        1 or 2 or 6 or 7 => 1,
        3 or 8 => 2,
        4 or 9 or 11 or 13 => 4,
        5 or 10 or 12 or 16 or 17 or 18 => 8,
        _ => 0,
    };
}

/// <summary>One IFD of a <see cref="RawTiff"/>, with typed reads of its entries that return a fallback for a missing or damaged one.</summary>
internal sealed class RawIfd(RawTiff file, long offset, Dictionary<int, RawEntry> entries, long next)
{
    public RawTiff File { get; } = file;

    public long Offset { get; } = offset;

    public Dictionary<int, RawEntry> Entries { get; } = entries;

    public long Next { get; } = next;

    public bool Has(int tag) => Entries.TryGetValue(tag, out RawEntry e) && e.ValueAt >= 0;

    public bool TryGet(int tag, out RawEntry entry) => Entries.TryGetValue(tag, out entry) && entry.ValueAt >= 0;

    /// <summary>Returns the entry's first value as an integer, or <paramref name="fallback"/>.</summary>
    public long Int(int tag, long fallback = 0)
    {
        return TryGet(tag, out RawEntry e) ? (long)File.Number(e, 0) : fallback;
    }

    /// <summary>Returns the entry's first value as a number, or <paramref name="fallback"/>.</summary>
    public double Double(int tag, double fallback = 0)
    {
        return TryGet(tag, out RawEntry e) ? File.Number(e, 0) : fallback;
    }

    /// <summary>Returns every value of the entry as numbers, at most <paramref name="max"/>, or <see langword="null"/>.</summary>
    public double[]? Doubles(int tag, int max = 1 << 20)
    {
        if (!TryGet(tag, out RawEntry e) || e.Count > max)
        {
            return null;
        }

        double[] values = new double[e.Count];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = File.Number(e, i);
        }

        return values;
    }

    /// <summary>Returns every value of the entry as integers, at most <paramref name="max"/>, or <see langword="null"/>.</summary>
    public long[]? Ints(int tag, int max = 1 << 24)
    {
        if (!TryGet(tag, out RawEntry e) || e.Count > max)
        {
            return null;
        }

        long[] values = new long[e.Count];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = (long)File.Number(e, i);
        }

        return values;
    }

    /// <summary>Returns an ASCII entry cut at its first NUL and trimmed, or <see langword="null"/>.</summary>
    public string? Ascii(int tag)
    {
        if (!TryGet(tag, out RawEntry e))
        {
            return null;
        }

        ReadOnlySpan<byte> bytes = Bytes(e);
        int end = bytes.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? bytes : bytes[..end]).Trim();
    }

    /// <summary>Returns the entry's bytes as stored.</summary>
    public ReadOnlySpan<byte> Bytes(RawEntry entry) => File.Data.AsSpan((int)entry.ValueAt, (int)entry.ByteCount);

    /// <summary>Returns the bytes of the entry with tag <paramref name="tag"/>, or an empty span.</summary>
    public ReadOnlySpan<byte> Bytes(int tag) => TryGet(tag, out RawEntry e) ? Bytes(e) : [];

    /// <summary>Returns the IFD a pointer entry points to, or <see langword="null"/>.</summary>
    public RawIfd? Sub(int tag)
    {
        return TryGet(tag, out RawEntry e) && File.Number(e, 0) is double at && at > 0 && at != Offset ? File.ReadIfd((long)at) : null;
    }
}

/// <summary>The TIFF, TIFF/EP, EXIF and DNG tag numbers RAW reading uses.</summary>
internal static class RawTags
{
    public const int NewSubFileType = 254;
    public const int ImageWidth = 256;
    public const int ImageLength = 257;
    public const int BitsPerSample = 258;
    public const int Compression = 259;
    public const int Photometric = 262;
    public const int Make = 271;
    public const int Model = 272;
    public const int StripOffsets = 273;
    public const int Orientation = 274;
    public const int SamplesPerPixel = 277;
    public const int RowsPerStrip = 278;
    public const int StripByteCounts = 279;
    public const int PlanarConfiguration = 284;
    public const int Software = 305;
    public const int DateTime = 306;
    public const int Predictor = 317;
    public const int TileWidth = 322;
    public const int TileLength = 323;
    public const int TileOffsets = 324;
    public const int TileByteCounts = 325;
    public const int SubIfds = 330;
    public const int SampleFormat = 339;
    public const int JpegTables = 347;
    public const int JpegInterchangeFormat = 513;
    public const int JpegInterchangeFormatLength = 514;
    public const int YCbCrSubSampling = 530;
    public const int Xmp = 700;
    public const int CfaRepeatPatternDim = 33421;
    public const int CfaPattern = 33422;
    public const int ExposureTime = 33434;
    public const int FNumber = 33437;
    public const int ExifIfd = 34665;
    public const int IccProfile = 34675;
    public const int GpsIfd = 34853;
    public const int IsoSpeed = 34855;
    public const int DateTimeOriginal = 36867;
    public const int FocalLength = 37386;
    public const int MakerNote = 37500;
    public const int CfaPatternExif = 41730;
    public const int LensMake = 42035;
    public const int LensModel = 42036;
    public const int DngVersion = 50706;
    public const int DngBackwardVersion = 50707;
    public const int UniqueCameraModel = 50708;
    public const int CfaPlaneColor = 50710;
    public const int CfaLayout = 50711;
    public const int LinearizationTable = 50712;
    public const int BlackLevelRepeatDim = 50713;
    public const int BlackLevel = 50714;
    public const int BlackLevelDeltaH = 50715;
    public const int BlackLevelDeltaV = 50716;
    public const int WhiteLevel = 50717;
    public const int DefaultScale = 50718;
    public const int DefaultCropOrigin = 50719;
    public const int DefaultCropSize = 50720;
    public const int ColorMatrix1 = 50721;
    public const int ColorMatrix2 = 50722;
    public const int CameraCalibration1 = 50723;
    public const int CameraCalibration2 = 50724;
    public const int AnalogBalance = 50727;
    public const int AsShotNeutral = 50728;
    public const int AsShotWhiteXY = 50729;
    public const int BaselineExposure = 50730;
    public const int LensInfo = 50736;
    public const int DngPrivateData = 50740;
    public const int CalibrationIlluminant1 = 50778;
    public const int CalibrationIlluminant2 = 50779;
    public const int ActiveArea = 50829;
    public const int MaskedAreas = 50830;
    public const int ProfileName = 50936;
    public const int ProfileHueSatMapDims = 50937;
    public const int ProfileHueSatMapData1 = 50938;
    public const int ProfileHueSatMapData2 = 50939;
    public const int ProfileToneCurve = 50940;
    public const int ForwardMatrix1 = 50964;
    public const int ForwardMatrix2 = 50965;
    public const int ProfileLookTableDims = 50981;
    public const int ProfileLookTableData = 50982;
    public const int OpcodeList1 = 51008;
    public const int OpcodeList2 = 51009;
    public const int OpcodeList3 = 51022;
    public const int NoiseProfile = 51041;
    public const int ProfileHueSatMapEncoding = 51107;
    public const int ProfileLookTableEncoding = 51108;
    public const int BaselineExposureOffset = 51109;
}
