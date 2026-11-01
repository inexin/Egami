using System.Buffers;
using System.Runtime.InteropServices;

using Egami.Pixels;

namespace Egami.Formats.Jpeg;

/// <summary>
/// Writes lossless JPEG (ITU-T T.81 annex H, SOF3 or SOF11) from samples that are not pixels: TIFF compression 7 strips and
/// tiles, DNG's sensor tiles and any other format that stores lossless JPEG streams. The samples are coded exactly as saving the
/// same samples as pixels with <see cref="JpegLosslessOptions"/> codes them.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/AnimationAndMetadata.cs" region="jpeg-lossless-samples"/>
/// </example>
public static class JpegLosslessWriter
{
    /// <summary>
    /// Writes <paramref name="samples"/>, rows of <paramref name="width"/> pixels of <paramref name="components"/> samples each,
    /// interleaved by component, as a whole lossless JPEG stream: SOI, the frame, its Huffman tables (or none when arithmetic
    /// coded), the scan and EOI, with no APP segment. Every sample must be below 2^<paramref name="precision"/>. A
    /// <see langword="null"/> <paramref name="predictor"/> picks the one that codes smallest; <paramref name="restartInterval"/>
    /// is in samples per component, rounded up to whole rows. The component ids are 1 for one component, R, G, B for three, C,
    /// M, Y, K for four and 1, 2 for two.
    /// </summary>
    /// <param name="output">Where to write. It is left open.</param>
    /// <param name="samples">The samples, row after row, the components of each pixel next to each other.</param>
    /// <param name="components">The components of a pixel, 1 to 4.</param>
    /// <param name="width">The pixels in a row, 1 to 65535.</param>
    /// <param name="precision">The bits of a sample, 2 to 16.</param>
    /// <param name="predictor">The T.81 predictor, 1 to 7, or <see langword="null"/> for the one that codes smallest.</param>
    /// <param name="restartInterval">The restart interval in pixels, rounded up to whole rows, or 0 for none.</param>
    /// <param name="arithmetic">Whether to code with the arithmetic coder (SOF11) instead of Huffman tables (SOF3).</param>
    /// <exception cref="ArgumentOutOfRangeException">A count, the precision or the predictor is out of range.</exception>
    /// <exception cref="ArgumentException"><paramref name="samples"/> is not 1 to 65535 whole rows.</exception>
    public static void Write(Stream output, ReadOnlySpan<ushort> samples, int components, int width, int precision, int? predictor,
        int restartInterval = 0, bool arithmetic = false)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentOutOfRangeException.ThrowIfLessThan(components, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(components, 4);
        ArgumentOutOfRangeException.ThrowIfLessThan(precision, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(precision, 16);
        if (predictor is < 1 or > 7)
        {
            throw new ArgumentOutOfRangeException(nameof(predictor), predictor, "A lossless JPEG predictor is 1 to 7.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, ushort.MaxValue);
        int stride = width * components;
        if (samples.IsEmpty || samples.Length % stride != 0 || samples.Length / stride > ushort.MaxValue)
        {
            throw new ArgumentException($"JPEG: {samples.Length} samples are not 1 to 65535 whole rows of {stride}.", nameof(samples));
        }

        int height = samples.Length / stride;
        ReadOnlySpan<byte> ids = components switch { 1 => [1], 2 => [1, 2], 3 => "RGB"u8, _ => "CMYK"u8 };
        using JpegBitWriter writer = new(output);
        using JpegLosslessScan scan = new(writer, width, height, components, precision, 0, arithmetic, ids);
        writer.Marker(JpegMarker.Soi);
        scan.WriteFrame();
        for (int y = 0; y < height; y++)
        {
            ushort[] row = ArrayPool<ushort>.Shared.Rent(stride);
            scan.Rows[y] = row;
            samples.Slice(y * stride, stride).CopyTo(row);
        }

        scan.Complete(predictor, restartInterval);
    }

    /// <summary>
    /// Writes a colour filter array mosaic, rows of <paramref name="width"/> photosites, as DNG lays its lossless JPEG tiles out:
    /// each row coded as pairs of photosites, a two-component frame half as wide, so the two colours of a row each predict from
    /// their own kind. Predictor 1, as Adobe DNG Converter writes. The width must be even.
    /// </summary>
    /// <param name="output">Where to write. It is left open.</param>
    /// <param name="mosaic">The photosites, row after row.</param>
    /// <param name="width">The photosites in a row: even, 2 to 131070.</param>
    /// <param name="precision">The bits of a sample, 2 to 16.</param>
    /// <param name="predictor">The T.81 predictor, 1 to 7.</param>
    /// <exception cref="ArgumentException"><paramref name="width"/> is odd, or <paramref name="mosaic"/> is not whole rows.</exception>
    public static void WriteCfa(Stream output, ReadOnlySpan<ushort> mosaic, int width, int precision, int predictor = 1)
    {
        if (width < 2 || width % 2 != 0)
        {
            throw new ArgumentException($"JPEG: a CFA tile coded as pairs needs an even width, not {width}.", nameof(width));
        }

        Write(output, mosaic, 2, width / 2, precision, predictor);
    }
}

/// <summary>
/// Writes lossless JPEG (T.81 annex H): SOF3 with Huffman tables made for the image (annex K.2), or SOF11 with the arithmetic
/// coder and the two-dimensional contexts of H.1.2.3. The samples are the pixels' own, gray, RGB or CMYK (inverted, as Adobe
/// files hold it), at 2 to 16 bits with no subsampling, all interleaved in one scan. They are held until every row has come, so
/// the predictor can be chosen and the Huffman symbols counted, and then coded.
/// </summary>
internal sealed class JpegLosslessWriter<TPixel> : RowEncoder<TPixel>
    where TPixel : unmanaged, IPixelType<TPixel>
{
    private readonly JpegBitWriter _writer;
    private readonly JpegLosslessScan _scan;
    private readonly JpegWriterOptions _options;
    private readonly Kind _kind;
    private readonly int _width;
    private readonly int _height;
    private readonly int _count;
    private readonly int _precision;
    private readonly int _pointTransform;
    private readonly ushort[] _wide;
    private readonly Rgb16[]? _composited;
    private readonly byte[]? _narrow;
    private int _rowsIn;

    internal JpegLosslessWriter(Stream output, EncodeStart<TPixel> start, JpegWriterOptions jpeg)
    {
        JpegLosslessOptions lossless = jpeg.Lossless!;
        if (jpeg.Progressive)
        {
            throw new ArgumentException("JPEG: a lossless file has no progressive form (that would be hierarchical JPEG); leave Progressive off.", nameof(jpeg));
        }

        _kind = jpeg.ColorType switch
        {
            JpegColorType.Ycck => throw new ArgumentException("JPEG: a lossless file is written as gray, RGB or CMYK, never YCCK.", nameof(jpeg)),
            JpegColorType.Gray => Kind.Gray,
            JpegColorType.Cmyk => Kind.Cmyk,
            _ => TPixel.Layout.ChannelOrder switch
            {
                PixelChannelOrder.Gray or PixelChannelOrder.GrayAlpha => Kind.Gray,
                PixelChannelOrder.Cmyk => Kind.Cmyk,
                _ => Kind.Rgb,
            },
        };

        int source = TPixel.Layout.ChannelType == PixelChannelType.Unorm8 ? 8 : 16;
        _precision = lossless.Precision ?? source;
        _pointTransform = lossless.PointTransform;
        if (_pointTransform >= _precision)
        {
            throw new ArgumentException($"JPEG: the point transform ({_pointTransform}) must be below the precision ({_precision} bits).", nameof(jpeg));
        }

        _options = jpeg;
        _width = start.Width;
        _height = start.Height;
        _count = _kind switch { Kind.Gray => 1, Kind.Rgb => 3, _ => 4 };
        long held = (long)_width * _height * _count * sizeof(ushort);
        if (held > start.Context.Limits.MaxTotalBytes)
        {
            throw new LimitExceededException(nameof(SafetyLimits.MaxTotalBytes), start.Context.Limits.MaxTotalBytes, held);
        }

        SaveKeeps alpha = jpeg.Background is null ? SaveKeeps.None : SaveKeeps.Alpha;
        SaveKeeps cmyk = _kind == Kind.Cmyk ? SaveKeeps.Cmyk : SaveKeeps.None;
        SaveLosses.Report(start, "JPEG", alpha | cmyk | SaveKeeps.Wide | SaveKeeps.Exif | SaveKeeps.Xmp | SaveKeeps.Iptc | SaveKeeps.Icc | SaveKeeps.Comment | SaveKeeps.Resolution | SaveKeeps.GainMap);
        if (_precision < source)
        {
            SaveLosses.Report(start.Context, "Save.DepthReduced", $"Lossless JPEG was written at {_precision} bits; the {source}-bit samples were rounded to it.");
        }

        if (_pointTransform > 0)
        {
            SaveLosses.Report(start.Context, "Save.DepthReduced", $"Lossless JPEG with point transform {_pointTransform} drops the low {_pointTransform} bits of every sample; the file is not lossless.");
        }

        if (!jpeg.StripMetadata && jpeg.KeepGainMap && start.Metadata.GainMap is not null)
        {
            SaveLosses.Report(start.Context, "Save.GainMapDropped", "A lossless JPEG carries no gain map, since no HDR viewer decodes its base image; the image was written without it.");
        }

        _wide = ArrayPool<ushort>.Shared.Rent(_width * 4);
        _composited = jpeg.Background is null ? null : new Rgb16[_width];

        // 8-bit pixels written at 8 bits as their own components convert straight to bytes, which is the same as the 16-bit
        // path's widen and round back.
        Kind natural = TPixel.Layout.ChannelOrder switch
        {
            PixelChannelOrder.Gray or PixelChannelOrder.GrayAlpha => Kind.Gray,
            PixelChannelOrder.Cmyk => Kind.Cmyk,
            _ => Kind.Rgb,
        };
        _narrow = source == 8 && _precision == 8 && jpeg.Background is null && _kind == natural ? ArrayPool<byte>.Shared.Rent(_width * _count) : null;
        _writer = new JpegBitWriter(output);
        _writer.Marker(JpegMarker.Soi);
        if (_kind == Kind.Gray)
        {
            JpegWriter.WriteJfif(_writer, jpeg.StripMetadata ? null : start.Metadata.Resolution);
        }
        else
        {
            // Adobe transform 0: the components are RGB or CMYK as they are, not YCbCr or YCCK.
            JpegWriter.WriteAdobe(_writer, 0);
        }

        if (!jpeg.StripMetadata)
        {
            JpegWriter.WriteMetadata(_writer, start.Metadata, start.Options.ProfileToWrite(start.Metadata), start.Context);
        }

        _scan = new JpegLosslessScan(_writer, _width, _height, _count, _precision, _pointTransform, jpeg.Arithmetic, Ids);
        _scan.WriteFrame();
    }

    /// <summary>Which components the file holds.</summary>
    private enum Kind
    {
        Gray,
        Rgb,
        Cmyk,
    }

    /// <summary>The component ids: 1 for gray, R, G, B and C, M, Y, K as libjpeg names them.</summary>
    private ReadOnlySpan<byte> Ids => _kind switch { Kind.Gray => [1], Kind.Rgb => "RGB"u8, _ => "CMYK"u8 };

    public override void WriteRow(ReadOnlySpan<TPixel> row)
    {
        if (_rowsIn >= _height)
        {
            throw new InvalidOperationException("Every row of the image has been written.");
        }

        ushort[] samples = ArrayPool<ushort>.Shared.Rent(_width * _count);
        _scan.Rows[_rowsIn++] = samples;
        Samples(row, samples.AsSpan(0, _width * _count));
    }

    public override void Complete()
    {
        if (_rowsIn != _height)
        {
            throw new InvalidOperationException($"The image has {_height} rows; {_rowsIn} were written.");
        }

        _scan.Complete(_options.Lossless!.Predictor, _options.RestartInterval);
    }

    protected override void Dispose(bool disposing)
    {
        _scan.Dispose();
        ArrayPool<ushort>.Shared.Return(_wide);
        if (_narrow is not null)
        {
            ArrayPool<byte>.Shared.Return(_narrow);
        }
        _writer.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>
    /// Converts one row of pixels to the file's samples, interleaved: composited onto the background when there is one, narrowed
    /// to the precision with rounding, CMYK inverted, then shifted down by the point transform.
    /// </summary>
    private void Samples(ReadOnlySpan<TPixel> row, Span<ushort> samples)
    {
        if (_narrow is byte[] narrow)
        {
            Span<byte> bytes = narrow.AsSpan(0, samples.Length);
            switch (_kind)
            {
                case Kind.Gray:
                    PixelConvert.Convert(row, MemoryMarshal.Cast<byte, Gray8>(bytes));
                    break;
                case Kind.Rgb:
                    PixelConvert.Convert(row, MemoryMarshal.Cast<byte, Rgb8>(bytes));
                    break;
                default:
                    PixelConvert.Convert(row, MemoryMarshal.Cast<byte, Cmyk8>(bytes));
                    break;
            }

            int pt = _pointTransform;
            int flip = _kind == Kind.Cmyk ? 0xFF : 0;
            for (int i = 0; i < bytes.Length; i++)
            {
                samples[i] = (ushort)((bytes[i] ^ flip) >> pt);
            }

            return;
        }

        Span<ushort> wide = _wide.AsSpan(0, _width * _count);
        if (_options.Background is Rgba8 background)
        {
            Span<Rgba16> rgba = MemoryMarshal.Cast<ushort, Rgba16>(_wide.AsSpan(0, _width * 4));
            PixelConvert.Convert(row, rgba);
            int br = background.R * 257, bg = background.G * 257, bb = background.B * 257;
            Rgb16[] rgb = _composited!;
            for (int x = 0; x < _width; x++)
            {
                Rgba16 p = rgba[x];
                long a = p.A, rest = 65535 - p.A;
                rgb[x] = new Rgb16(
                    (ushort)(((p.R * a) + (br * rest) + 32767) / 65535),
                    (ushort)(((p.G * a) + (bg * rest) + 32767) / 65535),
                    (ushort)(((p.B * a) + (bb * rest) + 32767) / 65535));
            }

            Convert<Rgb16>(rgb, wide);
        }
        else
        {
            Convert(row, wide);
        }

        int max = (1 << _precision) - 1;
        int shift = _pointTransform;
        bool invert = _kind == Kind.Cmyk;
        for (int i = 0; i < samples.Length; i++)
        {
            int value = _precision == 16 ? wide[i] : (int)(((wide[i] * (long)max) + 32767) / 65535);
            samples[i] = (ushort)((invert ? max - value : value) >> shift);
        }
    }

    /// <summary>Converts pixels to the file's components at 16 bits.</summary>
    private void Convert<TFrom>(ReadOnlySpan<TFrom> row, Span<ushort> wide)
        where TFrom : unmanaged, IPixelType<TFrom>
    {
        switch (_kind)
        {
            case Kind.Gray:
                PixelConvert.Convert(row, MemoryMarshal.Cast<ushort, Gray16>(wide));
                break;
            case Kind.Rgb:
                PixelConvert.Convert(row, MemoryMarshal.Cast<ushort, Rgb16>(wide));
                break;
            default:
                PixelConvert.Convert(row, MemoryMarshal.Cast<ushort, Cmyk16>(wide));
                break;
        }
    }
}
