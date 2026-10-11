using System.Buffers;
using System.Runtime.InteropServices;

using Egami.Metadata;
using Egami.Pixels;
using Egami.Streaming;

namespace Egami.Formats.Tiff;

/// <summary>
/// One TIFF page decoded a band at a time: a strip, or a row of tiles, decoded into natural pixels when a row in it is first asked
/// for, so only the strips and tiles a read touches are decoded. Tiles are decoded only for the columns asked for.
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
    private readonly IMemoryOwner<byte> _band;
    private readonly IMemoryOwner<byte>? _raw;
    private readonly byte[]? _chunky;
    private readonly bool[] _done;
    private readonly int _stride;
    private Raster<TNatural>? _whole;
    private int _bandIndex = -1;
    private int _frame = -1;
    private int _shortUnits;
    private bool _warned;
    private bool _disposed;

    public TiffRows(EgamiInput input, TiffImage image, TiffPage page, ReaderOptions options, bool ownsInput)
    {
        _input = input;
        _image = image;
        _page = page;
        _ownsInput = ownsInput;
        _strict = options.Recovery == RecoveryMode.Strict;
        Metadata = image.Metadata(page, options);
        _decoder = new TiffDecoder(page);
        _convert = new TiffConvert(page);
        _stride = page.Width * TNatural.Layout.BytesPerPixel;
        _done = new bool[page.Across];
        try
        {
            _band = input.RentScratch(checked(_stride * page.UnitHeight), ClearMode.Zero);
            if (!Encoded)
            {
                _raw = input.RentScratch(checked((int)(page.UnitBytes * page.Planes)));
                if (page.Planar)
                {
                    _chunky = new byte[(int)((((long)page.UnitWidth * page.Samples * page.Bits) + 7) / 8)];
                }
            }
        }
        catch
        {
            _band?.Dispose();
            _decoder.Dispose();
            throw;
        }
    }

    /// <summary>Gets whether units are whole JPEG or WebP streams, decoded by their own readers.</summary>
    private bool Encoded => _page.Compression is TiffCodes.Jpeg or TiffCodes.OldJpeg or TiffCodes.Webp;

    public override int StoredWidth => _page.Width;

    public override int StoredHeight => _page.Height;

    public override int NativeScale => 1;

    public override int MaxNativeScale => 1;

    public override PixelLayoutInfo Layout => TNatural.Layout;

    public override RasterMetadata Metadata { get; }

    public override int? FrameCount => 1;

    public override RasterAccess Access => _image.Source.Buffered ? RasterAccess.Buffered : RasterAccess.Streamed;

    public override long WorkingBytes => WorkingBytesOf(_image, _page);

    /// <summary>Returns what a streaming read of a page holds: a band of natural rows, the raw units of a band and the file copy, if any.</summary>
    internal static long WorkingBytesOf(TiffImage image, TiffPage page)
    {
        long band = (long)page.Width * page.Layout.BytesPerPixel * page.UnitHeight;
        long raw = page.Compression is TiffCodes.Jpeg or TiffCodes.OldJpeg or TiffCodes.Webp
            ? (long)page.UnitWidth * page.UnitHeight * page.Layout.BytesPerPixel
            : page.UnitBytes * page.Planes;
        return band + raw + image.Source.HeldBytes;
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
        int band = y / _page.UnitHeight;
        if (band != _bandIndex)
        {
            Load(band);
        }

        if (_page.Tiled && _whole is null)
        {
            int first = x / _page.UnitWidth;
            int last = (x + destination.Length - 1) / _page.UnitWidth;
            for (int col = first; col <= last; col++)
            {
                if (!_done[col])
                {
                    DecodeColumn(band, col);
                }
            }
        }

        ReadOnlySpan<byte> row = _band.Memory.Span.Slice((y - (band * _page.UnitHeight)) * _stride, _stride);
        PixelConvert.Convert(MemoryMarshal.Cast<byte, TNatural>(row).Slice(x, destination.Length), destination);
    }

    public override void EndFrame()
    {
        Warn();
    }

    /// <summary>Decodes the band of strips or tiles holding row band × UnitHeight; tiles wait until their columns are asked for.</summary>
    private void Load(int band)
    {
        Warn(final: false);
        _bandIndex = band;
        Array.Clear(_done);
        if (_whole is not null || TiffJpeg.Interchange(_page) is byte[] whole && LoadWhole(whole))
        {
            CopyWhole(band);
            return;
        }

        if (!_page.Tiled)
        {
            DecodeColumn(band, 0);
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
        Span<byte> target = _band.Memory.Span;
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

    /// <summary>Decodes one strip (column 0) or tile column of a band, every plane, into natural rows.</summary>
    private void DecodeColumn(int band, int col)
    {
        TiffPage page = _page;
        _done[col] = true;
        int rows = Math.Min(page.UnitHeight, page.Height - (band * page.UnitHeight));
        int x0 = col * page.UnitWidth;
        int columns = Math.Min(page.UnitWidth, page.Width - x0);
        long perPlane = (long)page.Across * page.Down;
        long unit = ((long)band * page.Across) + col;
        Span<byte> target = _band.Memory.Span;
        int pixelBytes = TNatural.Layout.BytesPerPixel;
        if (Encoded)
        {
            DecodeEncoded(unit, rows, x0, columns, target);
            return;
        }

        Span<byte> raw = _raw!.Memory.Span;
        int unitBytes = (int)page.UnitBytes;
        int unitRows = page.Tiled ? page.UnitHeight : rows;
        int expected = page.Subsampled ? unitBytes : (int)(page.UnitRowBytes * unitRows);
        for (int p = 0; p < page.Planes; p++)
        {
            Span<byte> plane = raw.Slice(p * unitBytes, unitBytes);
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
            _convert.Blocks(raw[..unitBytes], page.UnitWidth, rows, x0, page.Width, target, _stride);
            return;
        }

        int rowBytes = (int)page.UnitRowBytes;
        for (int r = 0; r < rows; r++)
        {
            Span<byte> natural = target.Slice((r * _stride) + (x0 * pixelBytes), columns * pixelBytes);
            if (page.Planar)
            {
                TiffConvert.Interleave(page, raw, unitBytes, r * rowBytes, page.UnitWidth, _chunky);
                _convert.Convert(_chunky, 0, columns, natural);
            }
            else
            {
                _convert.Convert(raw.Slice(r * rowBytes, rowBytes), 0, columns, natural);
            }
        }
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
    private void Warn(bool final = true)
    {
        if (final && _shortUnits > 0 && !_warned)
        {
            _warned = true;
            Metadata.WarningList.Add($"{_shortUnits} strip{(_shortUnits == 1 ? string.Empty : "s")} or tile{(_shortUnits == 1 ? string.Empty : "s")} of the page decode to fewer bytes than their rows, or not at all; what they lack is left black.");
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
        _band.Dispose();
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
