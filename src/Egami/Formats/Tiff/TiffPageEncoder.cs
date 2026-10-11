using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;

using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Tiff;

/// <summary>
/// Writes one page: rows converted to the stored type as they arrive, gathered into a strip (or a band of tile rows), each strip or
/// tile run through the predictor and the compression and written out, then the page's IFD.
/// </summary>
internal sealed class TiffPageEncoder<TPixel> : RowEncoder<TPixel>
    where TPixel : unmanaged, IPixelType<TPixel>
{
    private readonly TiffOutput _file;
    private readonly bool _ownsFile;
    private readonly EncodeStart<TPixel> _start;
    private readonly TiffPlan _plan;
    private readonly TiffWriterOptions _options;
    private readonly int _pageIndex;
    private readonly int _pageCount;
    private readonly int _width;
    private readonly int _height;
    private readonly int _rowBytes;
    private readonly int _unitRows;
    private readonly int _across;
    private readonly int _bandRowBytes;
    private readonly byte[] _band;
    private readonly byte[] _unit;
    private readonly byte[] _packed;
    private readonly ulong[] _offsets;
    private readonly ulong[] _counts;
    private readonly MemoryStream? _deflate;
    private int _y;
    private int _bandStart;
    private int _unitIndex;
    private bool _completed;

    public TiffPageEncoder(TiffOutput file, EncodeStart<TPixel> start, int pageIndex, int pageCount, bool ownsFile)
    {
        _file = file;
        _ownsFile = ownsFile;
        _start = start;
        _pageIndex = pageIndex;
        _pageCount = pageCount;
        _width = start.Width;
        _height = start.Height;
        _options = start.Options as TiffWriterOptions ?? TiffWriterOptions.Default;
        _plan = TiffPlan.For<TPixel>(start.Metadata, start.Options);
        _rowBytes = checked(_width * _plan.PixelBytes);
        int tile = _plan.TileSize;
        if (tile > 0)
        {
            _unitRows = tile;
            _across = (_width + tile - 1) / tile;
            _bandRowBytes = checked(_across * tile * _plan.PixelBytes);
        }
        else
        {
            _unitRows = Math.Clamp(65536 / Math.Max(1, _rowBytes), 1, _height);
            _across = 1;
            _bandRowBytes = _rowBytes;
        }

        int down = (_height + _unitRows - 1) / _unitRows;
        _offsets = new ulong[_across * down];
        _counts = new ulong[_across * down];
        int unitBytes = checked((tile > 0 ? tile * tile * _plan.PixelBytes : _unitRows * _rowBytes));
        _band = ArrayPool<byte>.Shared.Rent(checked(_bandRowBytes * _unitRows));
        _unit = ArrayPool<byte>.Shared.Rent(unitBytes);
        _packed = ArrayPool<byte>.Shared.Rent(Math.Max(PackBits.MaxEncodedLength(unitBytes) + _unitRows, TiffLzwEncoder.MaxEncodedLength(unitBytes)));
        if (_plan.Compression == TiffCodes.Deflate)
        {
            _deflate = new MemoryStream(unitBytes / 2 + 64);
        }
    }

    public override void WriteRow(ReadOnlySpan<TPixel> row)
    {
        if (_y >= _height)
        {
            throw new InvalidOperationException("Every row of the page has been written.");
        }

        Span<byte> target = _band.AsSpan((_y - _bandStart) * _bandRowBytes, _bandRowBytes);
        Store(row, target[.._rowBytes]);
        if (_bandRowBytes > _rowBytes)
        {
            // Tiles past the right edge repeat the last pixel, so they compress well.
            ReadOnlySpan<byte> last = target.Slice(_rowBytes - _plan.PixelBytes, _plan.PixelBytes);
            for (int at = _rowBytes; at < _bandRowBytes; at += _plan.PixelBytes)
            {
                last.CopyTo(target[at..]);
            }
        }

        _y++;
        if (_y - _bandStart == _unitRows || _y == _height)
        {
            FlushBand();
        }
    }

    /// <summary>Converts a row to the stored type's bytes, premultiplying when straight alpha is written as associated.</summary>
    private void Store(ReadOnlySpan<TPixel> row, Span<byte> target)
    {
        if (typeof(TPixel) == typeof(Alpha8))
        {
            // Alpha8 is written as gray: its values as they are.
            MemoryMarshal.AsBytes(row).CopyTo(target);
            return;
        }

        switch (_plan.Store)
        {
            case TiffStore.Gray8: PixelConvert.Convert(row, MemoryMarshal.Cast<byte, Gray8>(target)); break;
            case TiffStore.GrayAlpha8: PixelConvert.Convert(row, MemoryMarshal.Cast<byte, GrayAlpha8>(target)); break;
            case TiffStore.Gray16: PixelConvert.Convert(row, MemoryMarshal.Cast<byte, Gray16>(target)); break;
            case TiffStore.GrayAlpha16: PixelConvert.Convert(row, MemoryMarshal.Cast<byte, GrayAlpha16>(target)); break;
            case TiffStore.GrayF32: PixelConvert.Convert(row, MemoryMarshal.Cast<byte, GrayF32>(target)); break;
            case TiffStore.Rgb8: PixelConvert.Convert(row, MemoryMarshal.Cast<byte, Rgb8>(target)); break;
            case TiffStore.Rgba8: PixelConvert.Convert(row, MemoryMarshal.Cast<byte, Rgba8>(target)); break;
            case TiffStore.Rgb16: PixelConvert.Convert(row, MemoryMarshal.Cast<byte, Rgb16>(target)); break;
            case TiffStore.Rgba16: PixelConvert.Convert(row, MemoryMarshal.Cast<byte, Rgba16>(target)); break;
            case TiffStore.RgbF32: PixelConvert.Convert(row, MemoryMarshal.Cast<byte, RgbF32>(target)); break;
            case TiffStore.RgbaF32: PixelConvert.Convert(row, MemoryMarshal.Cast<byte, RgbaF32>(target)); break;
            case TiffStore.RgbaF16: PixelConvert.Convert(row, MemoryMarshal.Cast<byte, RgbaF16>(target)); break;
            case TiffStore.Cmyk8: PixelConvert.Convert(row, MemoryMarshal.Cast<byte, Cmyk8>(target)); break;
            case TiffStore.Cmyk16: PixelConvert.Convert(row, MemoryMarshal.Cast<byte, Cmyk16>(target)); break;
            case TiffStore.PremulRgba8: PixelConvert.Convert(row, MemoryMarshal.Cast<byte, PremulRgba8>(target)); break;
            default: PixelConvert.Convert(row, MemoryMarshal.Cast<byte, PremulRgbaF32>(target)); break;
        }

        if (_plan.AssociatedFromStraight)
        {
            Premultiply(target);
        }
    }

    private void Premultiply(Span<byte> target)
    {
        switch (_plan.Store)
        {
            case TiffStore.GrayAlpha8:
                for (int i = 0; i + 1 < target.Length; i += 2)
                {
                    target[i] = Pixels.Premultiply.Times(target[i], target[i + 1]);
                }

                break;
            case TiffStore.GrayAlpha16:
                Span<ushort> ga = MemoryMarshal.Cast<byte, ushort>(target);
                for (int i = 0; i + 1 < ga.Length; i += 2)
                {
                    ga[i] = (ushort)(((ga[i] * (uint)ga[i + 1]) + 32767) / 65535);
                }

                break;
            case TiffStore.Rgba16:
                Span<ushort> rgba = MemoryMarshal.Cast<byte, ushort>(target);
                for (int i = 0; i + 3 < rgba.Length; i += 4)
                {
                    uint a = rgba[i + 3];
                    rgba[i] = (ushort)(((rgba[i] * a) + 32767) / 65535);
                    rgba[i + 1] = (ushort)(((rgba[i + 1] * a) + 32767) / 65535);
                    rgba[i + 2] = (ushort)(((rgba[i + 2] * a) + 32767) / 65535);
                }

                break;
            case TiffStore.RgbaF16:
                Span<Half> half = MemoryMarshal.Cast<byte, Half>(target);
                for (int i = 0; i + 3 < half.Length; i += 4)
                {
                    float a = (float)half[i + 3];
                    half[i] = (Half)((float)half[i] * a);
                    half[i + 1] = (Half)((float)half[i + 1] * a);
                    half[i + 2] = (Half)((float)half[i + 2] * a);
                }

                break;
        }
    }

    /// <summary>Writes the strip, or the band's tiles, gathered so far.</summary>
    private void FlushBand()
    {
        int rows = _y - _bandStart;
        if (_plan.TileSize == 0)
        {
            Span<byte> strip = _unit.AsSpan(0, rows * _rowBytes);
            _band.AsSpan(0, strip.Length).CopyTo(strip);
            EncodeUnit(strip, _rowBytes, rows);
        }
        else
        {
            int tile = _plan.TileSize;
            int tileRow = tile * _plan.PixelBytes;
            for (int col = 0; col < _across; col++)
            {
                Span<byte> unit = _unit.AsSpan(0, tile * tileRow);
                for (int r = 0; r < tile; r++)
                {
                    // Rows past the bottom edge repeat the last row.
                    int from = Math.Min(r, rows - 1);
                    _band.AsSpan((from * _bandRowBytes) + (col * tileRow), tileRow).CopyTo(unit[(r * tileRow)..]);
                }

                EncodeUnit(unit, tileRow, tile);
            }
        }

        _bandStart = _y;
    }

    /// <summary>Runs a strip or tile through the predictor, the byte order and the compression, and writes it.</summary>
    private void EncodeUnit(Span<byte> unit, int rowLength, int rows)
    {
        TiffPlan plan = _plan;
        int sampleBytes = plan.Bits / 8;
        if (plan.Predictor == 3)
        {
            for (int r = 0; r < rows; r++)
            {
                ApplyFloatPredictor(unit.Slice(r * rowLength, rowLength), plan.Samples, sampleBytes);
            }
        }
        else
        {
            if (plan.Predictor == 2)
            {
                for (int r = 0; r < rows; r++)
                {
                    ApplyHorizontal(unit.Slice(r * rowLength, rowLength), plan.Samples, plan.Bits);
                }
            }

            if (_file.BigEndian && sampleBytes > 1)
            {
                TiffFile.Swap(unit, sampleBytes);
            }
        }

        ReadOnlySpan<byte> packed;
        switch (plan.Compression)
        {
            case TiffCodes.Lzw:
                packed = _packed.AsSpan(0, TiffLzwEncoder.Encode(unit, _packed));
                break;
            case TiffCodes.PackBits:
                int n = 0;
                for (int r = 0; r < rows; r++)
                {
                    n += PackBits.Encode(unit.Slice(r * rowLength, rowLength), _packed.AsSpan(n));
                }

                packed = _packed.AsSpan(0, n);
                break;
            case TiffCodes.Deflate:
                MemoryStream deflate = _deflate!;
                deflate.SetLength(0);
                using (ZLibStream zlib = new(deflate, new ZLibCompressionOptions { CompressionLevel = _options.CompressionLevel }, leaveOpen: true))
                {
                    zlib.Write(unit);
                }

                packed = deflate.GetBuffer().AsSpan(0, (int)deflate.Length);
                break;
            default:
                packed = unit;
                break;
        }

        _offsets[_unitIndex] = (ulong)_file.Position;
        _counts[_unitIndex] = (ulong)packed.Length;
        _unitIndex++;
        _file.Write(packed);
    }

    /// <summary>The horizontal predictor's encoding: each sample but a row's first becomes the difference from the one a pixel before.</summary>
    private static void ApplyHorizontal(Span<byte> row, int samples, int bits)
    {
        switch (bits)
        {
            case 8:
                for (int i = row.Length - 1; i >= samples; i--)
                {
                    row[i] -= row[i - samples];
                }

                break;
            case 16:
                Span<ushort> shorts = MemoryMarshal.Cast<byte, ushort>(row);
                for (int i = shorts.Length - 1; i >= samples; i--)
                {
                    shorts[i] -= shorts[i - samples];
                }

                break;
            default:
                Span<uint> words = MemoryMarshal.Cast<byte, uint>(row);
                for (int i = words.Length - 1; i >= samples; i--)
                {
                    words[i] -= words[i - samples];
                }

                break;
        }
    }

    /// <summary>
    /// The floating-point predictor's encoding (Adobe Photoshop TIFF Technical Note 3): the row's samples split into byte planes,
    /// most significant first, then each byte the difference from the one a pixel (a sample count) before it.
    /// </summary>
    private static void ApplyFloatPredictor(Span<byte> row, int samples, int size)
    {
        int count = row.Length / size;
        byte[] rented = ArrayPool<byte>.Shared.Rent(row.Length);
        try
        {
            Span<byte> copy = rented.AsSpan(0, row.Length);
            row.CopyTo(copy);
            for (int c = 0; c < count; c++)
            {
                for (int k = 0; k < size; k++)
                {
                    row[((size - 1 - k) * count) + c] = copy[(c * size) + k];
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        for (int i = row.Length - 1; i >= samples; i--)
        {
            row[i] -= row[i - samples];
        }
    }

    public override void Complete()
    {
        if (_completed)
        {
            return;
        }

        if (_y < _height)
        {
            throw new InvalidOperationException($"The page has {_height} rows; {_y} were written.");
        }

        _completed = true;
        WriteIfd();
        if (_ownsFile)
        {
            _file.Finish();
        }
    }

    private void WriteIfd()
    {
        TiffPlan plan = _plan;
        TiffIfdWriter ifd = new(_file);
        if (_pageCount > 1)
        {
            ifd.Add(TiffTags.NewSubfileType, 4, 2);
        }

        ifd.Add(TiffTags.ImageWidth, 4, (ulong)_width);
        ifd.Add(TiffTags.ImageLength, 4, (ulong)_height);
        ifd.Add(TiffTags.BitsPerSample, 3, [.. Enumerable.Repeat((ulong)plan.Bits, plan.Samples)]);
        ifd.Add(TiffTags.Compression, 3, (ulong)plan.Compression);
        ifd.Add(TiffTags.Photometric, 3, (ulong)plan.Photometric);
        int offsetType = _file.Big ? 16 : 4;
        if (plan.TileSize == 0)
        {
            ifd.Add(TiffTags.StripOffsets, offsetType, _offsets);
        }

        ifd.Add(TiffTags.SamplesPerPixel, 3, (ulong)plan.Samples);
        if (plan.TileSize == 0)
        {
            ifd.Add(TiffTags.RowsPerStrip, 4, (ulong)_unitRows);
            ifd.Add(TiffTags.StripByteCounts, offsetType, _counts);
        }

        (ulong xn, ulong xd, ulong yn, ulong yd, int unit) = Resolution(_start.Metadata.Resolution);
        ifd.AddRational(TiffTags.XResolution, xn, xd);
        ifd.AddRational(TiffTags.YResolution, yn, yd);
        ifd.Add(TiffTags.PlanarConfiguration, 3, 1);
        ifd.Add(TiffTags.ResolutionUnit, 3, (ulong)unit);
        if (_pageCount > 1)
        {
            ifd.Add(TiffTags.PageNumber, 3, [(ulong)_pageIndex, (ulong)_pageCount]);
        }

        if (plan.Predictor != 1)
        {
            ifd.Add(TiffTags.Predictor, 3, (ulong)plan.Predictor);
        }

        if (plan.TileSize > 0)
        {
            ifd.Add(TiffTags.TileWidth, 4, (ulong)plan.TileSize);
            ifd.Add(TiffTags.TileLength, 4, (ulong)plan.TileSize);
            ifd.Add(TiffTags.TileOffsets, offsetType, _offsets);
            ifd.Add(TiffTags.TileByteCounts, offsetType, _counts);
        }

        if (plan.Photometric == 5)
        {
            ifd.Add(TiffTags.InkSet, 3, 1);
        }

        if (plan.Extra >= 0)
        {
            ifd.Add(TiffTags.ExtraSamples, 3, (ulong)plan.Extra);
        }

        if (plan.Format != 1)
        {
            ifd.Add(TiffTags.SampleFormat, 3, [.. Enumerable.Repeat((ulong)plan.Format, plan.Samples)]);
        }

        TiffMetadataWriter.Add(ifd, _start.Metadata, _start.Options);
        ifd.Write();
    }

    /// <summary>Returns the resolution as rationals and a ResolutionUnit: 72 per inch when the raster has none.</summary>
    private static (ulong Xn, ulong Xd, ulong Yn, ulong Yd, int Unit) Resolution(Resolution? resolution)
    {
        Resolution r = resolution ?? new Resolution(72, 72, ResolutionUnit.PerInch);
        if (r.Unit == ResolutionUnit.PerMeter)
        {
            r = new Resolution(r.X / 100, r.Y / 100, ResolutionUnit.PerCentimeter);
        }

        int unit = r.Unit switch
        {
            ResolutionUnit.None => 1,
            ResolutionUnit.PerCentimeter => 3,
            _ => 2,
        };
        (ulong xn, ulong xd) = Rational(r.X);
        (ulong yn, ulong yd) = Rational(r.Y);
        return (xn, xd, yn, yd, unit);
    }

    /// <summary>Returns a positive number as a fraction with a denominator up to 10,000, reduced.</summary>
    internal static (ulong Numerator, ulong Denominator) Rational(double value)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            return (72, 1);
        }

        ulong denominator = 10000;
        ulong numerator = (ulong)Math.Round(Math.Min(value, uint.MaxValue / 10000.0) * denominator);
        ulong gcd = Gcd(numerator, denominator);
        return gcd == 0 ? (72, 1) : (numerator / gcd, denominator / gcd);

        static ulong Gcd(ulong a, ulong b)
        {
            while (b != 0)
            {
                (a, b) = (b, a % b);
            }

            return a;
        }
    }

    protected override void Dispose(bool disposing)
    {
        ArrayPool<byte>.Shared.Return(_band);
        ArrayPool<byte>.Shared.Return(_unit);
        ArrayPool<byte>.Shared.Return(_packed);
        _deflate?.Dispose();
        if (_ownsFile)
        {
            _file.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>Collects a page's entries and writes its IFD: entries in ascending tag order, long values after it, aligned.</summary>
internal sealed class TiffIfdWriter(TiffOutput file)
{
    private readonly SortedDictionary<int, (int Type, long Count, byte[] Value)> _entries = [];

    /// <summary>Adds integer values of a type (SHORT 3, LONG 4, LONG8 16).</summary>
    public void Add(int tag, int type, params ulong[] values)
    {
        int size = TiffEntry.Size(type);
        byte[] bytes = new byte[values.Length * size];
        for (int i = 0; i < values.Length; i++)
        {
            Span<byte> at = bytes.AsSpan(i * size, size);
            switch (size)
            {
                case 1: at[0] = (byte)values[i]; break;
                case 2: file.Put16(at, checked((ushort)values[i])); break;
                case 4: file.Put32(at, checked((uint)values[i])); break;
                default: file.Put64(at, values[i]); break;
            }
        }

        _entries[tag] = (type, values.Length, bytes);
    }

    public void AddRational(int tag, ulong numerator, ulong denominator)
    {
        byte[] bytes = new byte[8];
        file.Put32(bytes, checked((uint)numerator));
        file.Put32(bytes.AsSpan(4), checked((uint)denominator));
        _entries[tag] = (5, 1, bytes);
    }

    /// <summary>Adds values already in the file's byte order.</summary>
    public void AddRaw(int tag, int type, long count, byte[] value)
    {
        _entries[tag] = (type, count, value);
    }

    public bool Has(int tag) => _entries.ContainsKey(tag);

    /// <summary>Gets whether the file is big-endian.</summary>
    public bool BigEndian => file.BigEndian;

    /// <summary>Starts a sub-IFD in the same file.</summary>
    public TiffIfdWriter Sub() => new(file);

    /// <summary>Gets whether the file is BigTIFF.</summary>
    public bool Big => file.Big;

    /// <summary>Writes the IFD at the next aligned offset, its long values after it, and links it into the chain unless it is a sub-IFD.</summary>
    public long Write(bool link = true)
    {
        bool big = file.Big;
        int entrySize = big ? 20 : 12;
        int inline = big ? 8 : 4;
        file.Align();
        long ifd = file.Position;
        long headBytes = (big ? 8 : 2) + ((long)_entries.Count * entrySize) + (big ? 8 : 4);
        long values = ifd + headBytes;
        using MemoryStream head = new((int)headBytes);
        using MemoryStream tail = new();
        Span<byte> buffer = stackalloc byte[20];
        if (big)
        {
            file.Put64(buffer, (ulong)_entries.Count);
            head.Write(buffer[..8]);
        }
        else
        {
            file.Put16(buffer, (ushort)_entries.Count);
            head.Write(buffer[..2]);
        }

        foreach ((int tag, (int type, long count, byte[] value)) in _entries)
        {
            buffer.Clear();
            file.Put16(buffer, (ushort)tag);
            file.Put16(buffer[2..], (ushort)type);
            if (big)
            {
                file.Put64(buffer[4..], (ulong)count);
            }
            else
            {
                file.Put32(buffer[4..], checked((uint)count));
            }

            Span<byte> field = buffer.Slice(big ? 12 : 8, inline);
            if (value.Length <= inline)
            {
                value.CopyTo(field);
            }
            else
            {
                long at = values + tail.Length;
                if (big)
                {
                    file.Put64(field, (ulong)at);
                }
                else
                {
                    file.Put32(field, checked((uint)at));
                }

                tail.Write(value);
                if (tail.Length % (big ? 8 : 2) != 0)
                {
                    tail.Write(new byte[(big ? 8 : 2) - (tail.Length % (big ? 8 : 2))]);
                }
            }

            head.Write(buffer[..entrySize]);
        }

        long next = ifd + headBytes - (big ? 8 : 4);
        head.Write(new byte[big ? 8 : 4]);
        file.Write(head.GetBuffer().AsSpan(0, (int)head.Length));
        file.Write(tail.GetBuffer().AsSpan(0, (int)tail.Length));
        if (link)
        {
            file.Link(ifd, next);
        }

        return ifd;
    }
}
