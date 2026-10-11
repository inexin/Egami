using System.Buffers;
using System.Runtime.InteropServices;

using Egami.Metadata;
using Egami.Pixels;
using Egami.Streaming;

namespace Egami.Formats.Tiff;

/// <summary>
/// A reduced-resolution page standing in for a full page at an exact power-of-two reduction: the full page's size, the reduction
/// read, and the largest the file's pyramid offers.
/// </summary>
internal sealed record Pyramid(int Width, int Height, int Scale, int MaxScale, TiffPage Page);

/// <summary>
/// One TIFF page decoded a band at a time: a strip, or a row of tiles, decompressed when a row in it is first asked for, so only
/// the strips and tiles a read touches are decoded (tiles only for the columns asked for). Rows are converted from the band's raw
/// samples as they are asked for, so a load holds the raw band and the raster, nothing more. JPEG and WebP units, and subsampled
/// YCbCr, are converted into a band of natural pixels instead.
/// </summary>
internal sealed class TiffRows<TNatural> : RowSource
    where TNatural : unmanaged, IPixelType<TNatural>
{
    private readonly EgamiInput _input;
    private readonly TiffImage _image;
    private readonly TiffPage _page;
    private readonly bool _ownsInput;
    private readonly bool _strict;
    private readonly TiffDecoder _decoder;
    private readonly TiffConvert _convert;
    private readonly IMemoryOwner<byte>? _band;
    private readonly IMemoryOwner<byte>? _raw;
    private readonly byte[]? _chunky;
    private readonly TNatural[]? _row;
    private byte[]? _chunky2;
    private readonly bool[] _done;
    private readonly int _stride;
    private readonly int _unitBytes;
    private readonly Pyramid? _pyramid;
    private readonly bool _inPlace;
    private readonly long[]? _offsets;
    private Raster<TNatural>? _whole;
    private int _bandIndex = -1;
    private int _frame = -1;
    private int _shortUnits;
    private bool _warned;
    private bool _disposed;

    public TiffRows(EgamiInput input, TiffImage image, TiffPage page, ReaderOptions options, bool ownsInput, Pyramid? pyramid = null)
    {
        _pyramid = pyramid;
        _input = input;
        _image = image;
        _page = page;
        _ownsInput = ownsInput;
        _strict = options.Recovery == RecoveryMode.Strict;
        Metadata = image.Metadata(pyramid?.Page ?? page, options);
        _decoder = new TiffDecoder(page);
        _convert = new TiffConvert(page);
        _stride = page.Width * TNatural.Layout.BytesPerPixel;
        _done = new bool[page.Across];
        _unitBytes = Encoded ? 0 : checked((int)page.UnitBytes);
        _inPlace = InPlace(image, page);
        try
        {
            if (Banded(page))
            {
                _band = input.RentScratch(checked(_stride * page.UnitHeight), ClearMode.Zero);
            }

            if (_inPlace)
            {
                _offsets = new long[page.Planes * page.Across];
            }
            else if (!Encoded)
            {
                _raw = input.RentScratch(checked(_unitBytes * page.Planes * (page.Subsampled ? 1 : page.Across)));
            }

            if (!Encoded)
            {
                if (page.Planar)
                {
                    _chunky = new byte[(int)((((long)page.UnitWidth * page.Samples * page.Bits) + 7) / 8)];
                }

                if (!page.Subsampled && !_convert.IsDirect)
                {
                    _row = new TNatural[page.UnitWidth];
                }
            }
        }
        catch
        {
            _band?.Dispose();
            _raw?.Dispose();
            _decoder.Dispose();
            throw;
        }
    }

    /// <summary>Gets whether units are whole JPEG or WebP streams, decoded by their own readers.</summary>
    private bool Encoded => _page.Compression is TiffCodes.Jpeg or TiffCodes.OldJpeg or TiffCodes.Webp;

    /// <summary>
    /// Returns whether a page's rows are read where they lie in the file's memory, with no copy: uncompressed, chunky, its bits in
    /// order and its samples in the order they convert from.
    /// </summary>
    private static bool InPlace(TiffImage image, TiffPage page) => image.Source.InMemory && page.Compression == TiffCodes.None
        && !page.Planar && !page.Reversed && !page.Subsampled && page.Predictor == 1 && (!image.File.BigEndian || page.Bits % 8 != 0 || page.Bits == 8);

    /// <summary>Returns whether a page is converted into a band of natural pixels rather than row by row from its raw samples.</summary>
    private static bool Banded(TiffPage page) => page.Compression is TiffCodes.Jpeg or TiffCodes.OldJpeg or TiffCodes.Webp || page.Subsampled;

    public override int StoredWidth => _pyramid?.Width ?? _page.Width;

    public override int StoredHeight => _pyramid?.Height ?? _page.Height;

    public override int NativeScale => _pyramid?.Scale ?? 1;

    public override int MaxNativeScale => _pyramid?.MaxScale ?? 1;

    public override PixelLayoutInfo Layout => TNatural.Layout;

    public override RasterMetadata Metadata { get; }

    public override int? FrameCount => 1;

    public override RasterAccess Access => _image.Source.Buffered ? RasterAccess.Buffered : RasterAccess.Streamed;

    public override long WorkingBytes => WorkingBytesOf(_image, _page);

    /// <summary>Returns what a streaming read of a page holds: a band's raw units (or its natural pixels), and the file's copy if any.</summary>
    internal static long WorkingBytesOf(TiffImage image, TiffPage page)
    {
        long natural = (long)page.Width * page.Layout.BytesPerPixel * page.UnitHeight;
        long held = page.Compression is TiffCodes.Jpeg or TiffCodes.OldJpeg or TiffCodes.Webp
            ? natural + ((long)page.UnitWidth * page.UnitHeight * page.Layout.BytesPerPixel)
            : page.Subsampled ? natural + page.UnitBytes
            : InPlace(image, page) ? 0
            : page.UnitBytes * page.Planes * page.Across;
        return held + image.Source.HeldBytes;
    }

    public override TResult WithNaturalType<TResult>(INaturalTypeVisitor<TResult> visitor) => visitor.Visit<TNatural>();

    public override FrameMetadata? NextFrame()
    {
        _frame++;
        return _frame == 0 ? new FrameMetadata() : null;
    }

    public override void ReadRow<TPixel>(int y, int x, Span<TPixel> destination)
    {
        _input.CancellationToken.ThrowIfCancellationRequested();
        TiffPage page = _page;
        int band = y / page.UnitHeight;
        if (band != _bandIndex)
        {
            Load(band);
        }

        int first = x / page.UnitWidth;
        int last = (x + destination.Length - 1) / page.UnitWidth;
        if (_whole is null)
        {
            for (int col = first; col <= last; col++)
            {
                if (!_done[col])
                {
                    DecodeColumn(band, col);
                }
            }
        }

        int r = y - (band * page.UnitHeight);
        if (_band is not null)
        {
            ReadOnlySpan<byte> row = _band.Memory.Span.Slice(r * _stride, _stride);
            PixelConvert.Convert(MemoryMarshal.Cast<byte, TNatural>(row).Slice(x, destination.Length), destination);
            return;
        }

        // Convert straight from the raw samples, a tile column at a time.
        Span<byte> raw = _raw is null ? default : _raw.Memory.Span;
        int rowBytes = (int)page.UnitRowBytes;
        for (int col = first; col <= last; col++)
        {
            int x0 = col * page.UnitWidth;
            int from = Math.Max(x, x0);
            int to = Math.Min(x + destination.Length, Math.Min(x0 + page.UnitWidth, page.Width));
            ReadOnlySpan<byte> source;
            if (page.Planar)
            {
                TiffConvert.Interleave(page, raw[(col * _unitBytes)..], (long)_unitBytes * page.Across, r * rowBytes, page.UnitWidth, _chunky);
                source = _chunky;
            }
            else if (_inPlace)
            {
                source = InPlaceRow(col, r, rowBytes);
            }
            else
            {
                source = raw.Slice((col * _unitBytes) + (r * rowBytes), rowBytes);
            }

            Span<TPixel> target = destination.Slice(from - x, to - from);
            if (_convert.IsDirect)
            {
                PixelConvert.Convert(MemoryMarshal.Cast<byte, TNatural>(source).Slice(from - x0, to - from), target);
            }
            else
            {
                Span<TNatural> natural = _row.AsSpan(0, to - from);
                _convert.Convert(source, from - x0, to - from, MemoryMarshal.AsBytes(natural));
                PixelConvert.Convert<TNatural, TPixel>(natural, target);
            }
        }

    }

    public override void EndFrame()
    {
        Warn();
    }

    /// <summary>Starts the band of strips or tiles holding rows band × UnitHeight on; units wait until their columns are asked for.</summary>
    private void Load(int band)
    {
        _bandIndex = band;
        Array.Clear(_done);
        if (_whole is not null || (_page.Compression == TiffCodes.OldJpeg && TiffJpeg.Interchange(_page) is byte[] whole && LoadWhole(whole)))
        {
            CopyWhole(band);
        }
    }

    /// <summary>Decodes an old-style JPEG page whose JPEGInterchangeFormat holds one stream for the whole page.</summary>
    private bool LoadWhole(byte[] stream)
    {
        try
        {
            _whole = TiffJpeg.Decode<TNatural>(_page, stream, _page.Width, _page.Height, _input.Context, _strict);
            if (TiffJpeg.RawYCbCr(_page))
            {
                for (int y = 0; y < _whole.Height; y++)
                {
                    _convert.YCbCrInPlace(MemoryMarshal.AsBytes(_whole.Row(y)));
                }
            }

            return true;
        }
        catch (Exception e) when (!_strict && e is EgamiException or InvalidDataException)
        {
            return false;
        }
    }

    private void CopyWhole(int band)
    {
        Span<byte> target = _band!.Memory.Span;
        target.Clear();
        int rows = Math.Min(_page.UnitHeight, _page.Height - (band * _page.UnitHeight));
        int width = Math.Min(_whole!.Width, _page.Width);
        for (int r = 0; r < rows; r++)
        {
            int y = (band * _page.UnitHeight) + r;
            if (y >= _whole.Height)
            {
                break;
            }

            MemoryMarshal.AsBytes(_whole.Row(y)[..width]).CopyTo(target.Slice(r * _stride));
        }
    }

    /// <summary>Decodes one strip (column 0) or tile column of a band, every plane: raw samples, or natural pixels into the band.</summary>
    private void DecodeColumn(int band, int col)
    {
        TiffPage page = _page;
        _done[col] = true;
        int rows = Math.Min(page.UnitHeight, page.Height - (band * page.UnitHeight));
        int x0 = col * page.UnitWidth;
        int columns = Math.Min(page.UnitWidth, page.Width - x0);
        long perPlane = (long)page.Across * page.Down;
        long unit = ((long)band * page.Across) + col;
        if (Encoded)
        {
            DecodeEncoded(unit, rows, x0, columns, _band!.Memory.Span);
            return;
        }

        if (_inPlace)
        {
            (long offset, long count) = page.Unit(unit);
            _offsets![col] = count > 0 ? offset : -1;
            if (count < page.UnitRowBytes * (page.Tiled ? page.UnitHeight : rows))
            {
                Short(unit);
            }

            return;
        }

        Span<byte> raw = _raw!.Memory.Span;
        int unitRows = page.Tiled ? page.UnitHeight : rows;
        int expected = page.Subsampled ? _unitBytes : (int)(page.UnitRowBytes * unitRows);
        int slots = page.Subsampled ? 1 : page.Across;
        for (int p = 0; p < page.Planes; p++)
        {
            Span<byte> plane = raw.Slice(((p * slots) + (page.Subsampled ? 0 : col)) * _unitBytes, _unitBytes);
            int written = _decoder.Decode(unit + (p * perPlane), unitRows, plane);
            if (written < expected)
            {
                Short(unit + (p * perPlane));
            }

            if (page.Photometric == 32845)
            {
                TiffConvert.XyzToLinearSrgb(MemoryMarshal.Cast<byte, float>(plane));
            }
        }

        if (page.Subsampled)
        {
            _convert.Blocks(raw[.._unitBytes], page.UnitWidth, rows, x0, page.Width, _band!.Memory.Span, _stride);
        }
    }

    /// <summary>Returns row <paramref name="r"/> of an uncompressed unit where it lies in the file, padded with zeros past the file's end.</summary>
    private ReadOnlySpan<byte> InPlaceRow(int col, int r, int rowBytes)
    {
        long offset = _offsets![col];
        long count = offset < 0 ? 0 : _page.Unit(((long)_bandIndex * _page.Across) + col).Count;
        long at = (long)r * rowBytes;
        if (offset >= 0 && at + rowBytes <= count)
        {
            return _page.File.Source.Slice(offset + at, rowBytes, default);
        }

        _chunky2 ??= new byte[rowBytes];
        Array.Clear(_chunky2);
        if (offset >= 0 && at < count)
        {
            _page.File.Source.Slice(offset + at, (int)(count - at), default).CopyTo(_chunky2);
        }

        return _chunky2;
    }

    /// <summary>Decodes a JPEG or WebP strip or tile through its own reader and copies its pixels into the band.</summary>
    private void DecodeEncoded(long unit, int rows, int x0, int columns, Span<byte> target)
    {
        TiffPage page = _page;
        (long offset, long count) = page.Unit(unit);
        if (count <= 0 || count > int.MaxValue)
        {
            Short(unit);
            return;
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent((int)count);
        try
        {
            int n = page.File.Source.Read(offset, rented.AsSpan(0, (int)count));
            int unitHeight = page.Tiled ? page.UnitHeight : rows;
            using Raster<TNatural> decoded = TiffJpeg.Decode<TNatural>(page, rented.AsSpan(0, n), page.UnitWidth, unitHeight, _input.Context, _strict);
            if (decoded.Metadata.Warnings.Count > 0 || decoded.Width < columns || decoded.Height < rows)
            {
                Short(unit);
            }

            int pixelBytes = TNatural.Layout.BytesPerPixel;
            int width = Math.Min(columns, decoded.Width);
            bool invert = page.Photometric == 5 && page.Compression != TiffCodes.Webp;
            bool ycbcr = TiffJpeg.RawYCbCr(page);
            for (int r = 0; r < rows && r < decoded.Height; r++)
            {
                Span<byte> to = target.Slice((r * _stride) + (x0 * pixelBytes), width * pixelBytes);
                MemoryMarshal.AsBytes(decoded.Row(r)[..width]).CopyTo(to);
                if (ycbcr)
                {
                    _convert.YCbCrInPlace(to);
                }

                if (invert)
                {
                    // Egami's JPEG decoder reads CMYK as Adobe stores it, inverted; TIFF stores plain ink amounts.
                    foreach (ref byte b in to)
                    {
                        b = (byte)~b;
                    }
                }
            }
        }
        catch (Exception e) when (!_strict && e is CorruptDataException or InvalidDataException)
        {
            Short(unit);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private void Short(long unit)
    {
        if (_strict)
        {
            throw new CorruptDataException(TiffFormat.Info.Name, (long)Math.Min(unit < _page.UnitCount ? _page.Offsets[unit] : 0, long.MaxValue),
                $"Strip or tile {unit} of the page decodes to fewer bytes than its rows.");
        }

        _shortUnits++;
    }

    /// <summary>Notes the short strips or tiles once per page, in lenient mode.</summary>
    private void Warn()
    {
        if (_shortUnits > 0 && !_warned)
        {
            _warned = true;
            Metadata.WarningList.Add($"{_shortUnits} strip{(_shortUnits == 1 ? string.Empty : "s")} or tile{(_shortUnits == 1 ? string.Empty : "s")} of the page decode to fewer bytes than their rows, or not at all; what they lack is read as zero samples.");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Warn();
        _decoder.Dispose();
        _band?.Dispose();
        _raw?.Dispose();
        _whole?.Dispose();
        if (_ownsInput)
        {
            _image.Dispose();
            _input.ReturnUnreadBytes();
            _input.Dispose();
        }
    }
}
