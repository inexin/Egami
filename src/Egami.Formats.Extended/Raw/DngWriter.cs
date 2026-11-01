using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;

using Egami.Colors;
using Egami.Formats.Jpeg;
using Egami.Formats.Jxl;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Extended.Raw;

/// <summary>What a DNG is written from: the raw data with the frame that describes it, and whatever came with it.</summary>
internal sealed class DngContent : IDisposable
{
    public required RawFrame Frame { get; init; }

    /// <summary>Gets the raw data, the whole sensor: 1 sample a pixel for a colour filter array, 3 for LinearRaw.</summary>
    public required RawPixels Pixels { get; init; }

    /// <summary>Gets a DNG source's IFD0, whose tags the output keeps, or <see langword="null"/>.</summary>
    public RawIfd? SourceIfd0 { get; init; }

    /// <summary>Gets a DNG source's raw IFD, whose tags the output keeps, or <see langword="null"/>.</summary>
    public RawIfd? SourceRaw { get; init; }

    public ExifData? Exif { get; init; }

    public byte[]? Xmp { get; init; }

    /// <summary>Gets the byte order the maker note was written in, for DNGPrivateData.</summary>
    public bool MakerNoteBigEndian { get; init; }

    /// <summary>Gets where the maker note was in its file, for DNGPrivateData.</summary>
    public long MakerNoteOffset { get; init; }

    /// <summary>Gets the image the previews show, when it is not a develop of the raw data (a raster saved as LinearRaw).</summary>
    public Raster? Rendered { get; init; }

    public void Dispose() => Pixels.Dispose();
}

/// <summary>
/// Writes DNG files, from the Adobe DNG Specification 1.7.1, laid out as Adobe DNG Converter does: IFD0 holds a 256-pixel
/// thumbnail and the camera tags; SubIFDs hold the raw data in tiles (CFA or LinearRaw) and a JPEG preview. A DNG source keeps
/// every tag it had; the rest come from the frame, which the parser filled from the file and the camera table.
/// </summary>
internal static class DngWriter
{
    private const int PhotometricCfa = 32803;
    private const int PhotometricLinearRaw = 34892;
    private const int CompressionJpeg = 7;
    private const int CompressionDeflate = 8;
    private const int CompressionJpegXl = 52546;
    private const int OpcodeList1 = 51008;
    private const int OpcodeList2 = 51009;
    private const int OpcodeList3 = 51022;
    private const int NoiseProfile = 51041;
    private const int PreviewColorSpace = 50970;

    /// <summary>The tags that describe an IFD's own image layout or point at other data, which a copied IFD never keeps.</summary>
    private static readonly HashSet<int> Structural =
    [
        RawTags.NewSubFileType, RawTags.ImageWidth, RawTags.ImageLength, RawTags.BitsPerSample, RawTags.Compression, RawTags.Photometric,
        RawTags.StripOffsets, RawTags.SamplesPerPixel, RawTags.RowsPerStrip, RawTags.StripByteCounts, RawTags.PlanarConfiguration,
        RawTags.Predictor, RawTags.TileWidth, RawTags.TileLength, RawTags.TileOffsets, RawTags.TileByteCounts, RawTags.SubIfds,
        RawTags.SampleFormat, RawTags.JpegTables, RawTags.JpegInterchangeFormat, RawTags.JpegInterchangeFormatLength,
        RawTags.YCbCrSubSampling, RawTags.Xmp, RawTags.ExifIfd, RawTags.GpsIfd, RawTags.DngVersion, RawTags.DngBackwardVersion,
        RawTags.Software, 50966, 50967, 50968, 50969, PreviewColorSpace, 50971, 0x0201, 0x0202, 0x0212, 0x0213, 0x0214,
    ];

    public static void Write(Stream output, DngContent content, DngWriterOptions options, EgamiContext context, RawWarnings warnings)
    {
        RawFrame frame = content.Frame;
        RawPixels pixels = content.Pixels;
        DngCompression compression = options.Compression ?? (pixels.IsFloat ? DngCompression.Deflate : DngCompression.LosslessJpeg);
        if (compression == DngCompression.LosslessJpeg && pixels.IsFloat)
        {
            throw new ArgumentException("DNG: lossless JPEG takes integer raw data; choose Deflate, None or JpegXl for floating-point data.", nameof(options));
        }

        DngIfd raw = RawIfd(content, compression, options, context);

        // The previews come after the tiles: a develop may change the raw data in place.
        DngIfd ifd0;
        if (options.Preview == DngPreview.None)
        {
            ifd0 = raw;
        }
        else
        {
            ifd0 = new DngIfd();
            ifd0.Long(RawTags.NewSubFileType, 1);
            using Raster<Rgb8>? rendered = Render(content, half: options.Preview != DngPreview.Full, context, warnings);
            if (rendered is not null)
            {
                using Raster<Rgb8> thumbnail = rendered.Derive(ops => ops.Fit(256, 256, enlarge: false));
                Thumbnail(ifd0, thumbnail);
            }

            ifd0.SubIfds.Add(raw);
            if (rendered is not null && options.Preview is DngPreview.Medium or DngPreview.Full)
            {
                using Raster<Rgb8> preview = options.Preview == DngPreview.Full ? rendered.Copy() : rendered.Derive(ops => ops.Fit(1024, 1024, enlarge: false));
                ifd0.SubIfds.Add(Preview(preview));
            }
        }

        Camera(ifd0, content, compression);
        Metadata(ifd0, content);
        DngTiff.Write(output, ifd0);
    }

    /// <summary>Builds the raw IFD: the source's own tags when it is a DNG, then the frame's, then the tiles.</summary>
    private static DngIfd RawIfd(DngContent content, DngCompression compression, DngWriterOptions options, EgamiContext context)
    {
        RawFrame frame = content.Frame;
        RawPixels pixels = content.Pixels;
        DngIfd ifd = new();
        if (content.SourceRaw is RawIfd sourceRaw && sourceRaw != content.SourceIfd0)
        {
            Copy(ifd, sourceRaw);
        }

        int samples = pixels.Samples;
        int tile = options.TileSize;
        int bits = pixels.IsFloat ? 32 : 16;
        int precision = 16;
        if (compression == DngCompression.LosslessJpeg)
        {
            precision = Math.Clamp(Math.Max(frame.Sensor.Bits, BitsFor(Max(pixels))), 2, 16);
            bits = precision;
        }

        ifd.Long(RawTags.NewSubFileType, 0);
        ifd.Long(RawTags.ImageWidth, pixels.Width);
        ifd.Long(RawTags.ImageLength, pixels.Height);
        ifd.Short(RawTags.BitsPerSample, [.. Enumerable.Repeat(bits, samples)]);
        ifd.Short(RawTags.Compression, compression switch
        {
            DngCompression.LosslessJpeg => CompressionJpeg,
            DngCompression.Deflate => CompressionDeflate,
            DngCompression.JpegXl => CompressionJpegXl,
            _ => 1,
        });
        ifd.Short(RawTags.Photometric, frame.Cfa is null ? PhotometricLinearRaw : PhotometricCfa);
        ifd.Short(RawTags.SamplesPerPixel, samples);
        ifd.Short(RawTags.PlanarConfiguration, 1);
        ifd.Long(RawTags.TileWidth, tile);
        ifd.Long(RawTags.TileLength, tile);
        if (compression == DngCompression.Deflate)
        {
            ifd.Short(RawTags.Predictor, pixels.IsFloat ? 3 : 2);
        }

        if (pixels.IsFloat)
        {
            ifd.Short(RawTags.SampleFormat, [.. Enumerable.Repeat(3, samples)]);
        }

        if (frame.Cfa is RawCfa cfa)
        {
            ifd.Short(RawTags.CfaRepeatPatternDim, cfa.Rows, cfa.Columns);
            ifd.Bytes(RawTags.CfaPattern, cfa.Colors);
            ifd.Bytes(RawTags.CfaPlaneColor, [.. Enumerable.Range(0, frame.Planes).Select(i => (byte)i)]);
            ifd.Short(RawTags.CfaLayout, 1);
        }

        if (frame.Linearization is ushort[] table)
        {
            ifd.Short(RawTags.LinearizationTable, [.. table.Select(v => (int)v)]);
        }

        ifd.Short(RawTags.BlackLevelRepeatDim, frame.BlackRows, frame.BlackColumns);
        ifd.Rational(RawTags.BlackLevel, frame.Black);
        if (frame.BlackDeltaH is double[] dh)
        {
            ifd.SRational(RawTags.BlackLevelDeltaH, dh);
        }

        if (frame.BlackDeltaV is double[] dv)
        {
            ifd.SRational(RawTags.BlackLevelDeltaV, dv);
        }

        if (!pixels.IsFloat)
        {
            ifd.Integer(RawTags.WhiteLevel, [.. frame.White.Select(w => (long)Math.Round(w))]);
        }
        else
        {
            ifd.Remove(RawTags.WhiteLevel);
        }

        Rect active = frame.Active;
        (double cx, double cy, double cw, double ch) = frame.Crop;
        ifd.Rational(RawTags.DefaultScale, 1, 1);
        ifd.Rational(RawTags.DefaultCropOrigin, cx, cy);
        ifd.Rational(RawTags.DefaultCropSize, cw, ch);
        ifd.Long(RawTags.ActiveArea, active.Y, active.X, active.Bottom, active.Right);
        foreach ((int tag, byte[]? list) in (ReadOnlySpan<(int, byte[]?)>)[(OpcodeList1, frame.Opcodes1), (OpcodeList2, frame.Opcodes2), (OpcodeList3, frame.Opcodes3)])
        {
            if (list is not null)
            {
                ifd.Undefined(tag, list);
            }
        }

        if (frame.NoiseProfile is double[] noise)
        {
            ifd.Double(NoiseProfile, noise);
        }

        ifd.OffsetsTag = RawTags.TileOffsets;
        ifd.CountsTag = RawTags.TileByteCounts;
        ifd.Units.AddRange(Tiles(content, compression, tile, precision, options.ParallelCompression ? context.MaxParallelism : 1));
        return ifd;
    }

    /// <summary>Codes the raw data in tiles, row by row of tiles; those past the edges repeat the last whole pattern.</summary>
    private static byte[][] Tiles(DngContent content, DngCompression compression, int tile, int precision, int parallelism)
    {
        RawPixels pixels = content.Pixels;
        int across = (pixels.Width + tile - 1) / tile;
        int down = (pixels.Height + tile - 1) / tile;
        int periodX = content.Frame.Cfa?.Columns ?? 1;
        int periodY = content.Frame.Cfa?.Rows ?? 1;
        bool pairs = content.Frame.Cfa is not null && pixels.Samples == 1;
        byte[][] tiles = new byte[across * down][];
        Parallel.For(0, tiles.Length, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism) }, i =>
        {
            int x0 = i % across * tile, y0 = i / across * tile;
            tiles[i] = pixels.IsFloat
                ? FloatTile(pixels, x0, y0, tile, periodX, periodY, compression)
                : IntTile(pixels, x0, y0, tile, periodX, periodY, compression, precision, pairs);
        });
        return tiles;
    }

    /// <summary>Returns the column (or row) to read for <paramref name="x"/>, which past the edge repeats the last whole period.</summary>
    private static int Edge(int x, int size, int period)
    {
        if (x < size)
        {
            return x;
        }

        int last = size - period;
        return last < 0 ? size - 1 : last + ((x - size) % period);
    }

    private static byte[] IntTile(RawPixels pixels, int x0, int y0, int tile, int periodX, int periodY, DngCompression compression, int precision, bool pairs)
    {
        int s = pixels.Samples;
        int stride = tile * s;
        ushort[] samples = new ushort[tile * stride];
        for (int y = 0; y < tile; y++)
        {
            ReadOnlySpan<ushort> row = pixels.U16Row(Edge(y0 + y, pixels.Height, periodY));
            Span<ushort> to = samples.AsSpan(y * stride, stride);
            int whole = Math.Clamp(pixels.Width - x0, 0, tile);
            row.Slice(x0 * s, whole * s).CopyTo(to);
            for (int x = whole; x < tile; x++)
            {
                row.Slice(Edge(x0 + x, pixels.Width, periodX) * s, s).CopyTo(to[(x * s)..]);
            }
        }

        switch (compression)
        {
            case DngCompression.LosslessJpeg:
                {
                    using MemoryStream stream = new();
                    if (pairs)
                    {
                        JpegLosslessWriter.WriteCfa(stream, samples, tile, precision);
                    }
                    else
                    {
                        JpegLosslessWriter.Write(stream, samples, s, tile, precision, 1);
                    }

                    return stream.ToArray();
                }

            case DngCompression.JpegXl:
                return s == 1 ? Jxl<Gray16>(MemoryMarshal.AsBytes(samples.AsSpan()), tile) : Jxl<Rgb16>(MemoryMarshal.AsBytes(samples.AsSpan()), tile);
            case DngCompression.Deflate:
                for (int y = 0; y < tile; y++)
                {
                    // TIFF's horizontal predictor: each sample less the one a pixel before.
                    Span<ushort> row = samples.AsSpan(y * stride, stride);
                    for (int i = row.Length - 1; i >= s; i--)
                    {
                        row[i] -= row[i - s];
                    }
                }

                return Deflate(LittleEndian(samples));
            default:
                return LittleEndian(samples);
        }
    }

    private static byte[] FloatTile(RawPixels pixels, int x0, int y0, int tile, int periodX, int periodY, DngCompression compression)
    {
        int s = pixels.Samples;
        int stride = tile * s;
        float[] samples = new float[tile * stride];
        for (int y = 0; y < tile; y++)
        {
            ReadOnlySpan<float> row = pixels.F32Row(Edge(y0 + y, pixels.Height, periodY));
            Span<float> to = samples.AsSpan(y * stride, stride);
            for (int x = 0; x < tile; x++)
            {
                row.Slice(Edge(x0 + x, pixels.Width, periodX) * s, s).CopyTo(to[(x * s)..]);
            }
        }

        if (compression == DngCompression.JpegXl)
        {
            return s == 1 ? Jxl<GrayF32>(MemoryMarshal.AsBytes(samples.AsSpan()), tile) : Jxl<RgbF32>(MemoryMarshal.AsBytes(samples.AsSpan()), tile);
        }

        byte[] bytes = new byte[samples.Length * 4];
        for (int i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), samples[i]);
        }

        if (compression != DngCompression.Deflate)
        {
            return bytes;
        }

        // TIFF's floating-point predictor (Adobe's TIFF Technical Note 3): each row's bytes by significance, the most
        // significant of every sample first, then each byte less the one a pixel before.
        byte[] copy = new byte[stride * 4];
        for (int y = 0; y < tile; y++)
        {
            Span<byte> row = bytes.AsSpan(y * stride * 4, stride * 4);
            row.CopyTo(copy);
            for (int c = 0; c < stride; c++)
            {
                for (int k = 0; k < 4; k++)
                {
                    row[((3 - k) * stride) + c] = copy[(c * 4) + k];
                }
            }

            for (int i = row.Length - 1; i >= s; i--)
            {
                row[i] -= row[i - s];
            }
        }

        return Deflate(bytes);
    }

    private static byte[] Jxl<TPixel>(ReadOnlySpan<byte> pixels, int tile)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using Raster<TPixel> raster = Raster.Create<TPixel>(tile, tile);
        raster.CopyPixelsFrom(pixels);
        raster.Metadata.ColorSpace = ColorSpaceHint.Linear;
        using MemoryStream stream = new();
        raster.Save(stream, JxlFormat.Info, new JxlWriterOptions { Lossless = true, StripMetadata = true });
        return stream.ToArray();
    }

    private static byte[] LittleEndian(ushort[] samples)
    {
        byte[] bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), samples[i]);
        }

        return bytes;
    }

    private static byte[] Deflate(byte[] bytes)
    {
        using MemoryStream stream = new();
        using (ZLibStream zlib = new(stream, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(bytes);
        }

        return stream.ToArray();
    }

    private static int Max(RawPixels pixels)
    {
        int max = 0;
        foreach (ushort v in pixels.U16)
        {
            max = Math.Max(max, v);
        }

        return max;
    }

    private static int BitsFor(int max) => 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)Math.Max(max, 1));

    /// <summary>Renders the image the previews show: a develop of the raw data, half size unless a full preview is asked for.</summary>
    private static Raster<Rgb8>? Render(DngContent content, bool half, EgamiContext context, RawWarnings warnings)
    {
        if (content.Rendered is Raster rendered)
        {
            return rendered.ConvertColor<Rgb8>(ColorProfile.Srgb);
        }

        RawFrame frame = content.Frame;
        if (frame.Planes != 3 || (frame.Cfa is null && content.Pixels.Samples != 3))
        {
            warnings.Add("DngNoPreview", $"Egami develops raw data with three colours; this has {frame.Planes}, so the DNG has no previews.");
            return null;
        }

        RawDeveloper developer = new(frame, new RawReaderOptions(), half, ColorProfile.Srgb, warnings);
        using EgamiInput input = new(Stream.Null, context, CancellationToken.None);
        Raster<Rgb8> raster = Raster.Create<Rgb8>(developer.Output.Width, developer.Output.Height, context);
        try
        {
            developer.Run(content.Pixels, input, (y, row) => PixelConvert.Convert(row, raster.Row(y)));
            return raster;
        }
        catch
        {
            raster.Dispose();
            throw;
        }
    }

    /// <summary>Makes IFD0 the thumbnail: uncompressed 8-bit RGB in one strip.</summary>
    private static void Thumbnail(DngIfd ifd, Raster<Rgb8> thumbnail)
    {
        ifd.Long(RawTags.ImageWidth, thumbnail.Width);
        ifd.Long(RawTags.ImageLength, thumbnail.Height);
        ifd.Short(RawTags.BitsPerSample, 8, 8, 8);
        ifd.Short(RawTags.Compression, 1);
        ifd.Short(RawTags.Photometric, 2);
        ifd.Short(RawTags.SamplesPerPixel, 3);
        ifd.Long(RawTags.RowsPerStrip, thumbnail.Height);
        ifd.Short(RawTags.PlanarConfiguration, 1);
        ifd.Short(PreviewColorSpace, 2);
        byte[] strip = new byte[thumbnail.Width * thumbnail.Height * 3];
        thumbnail.CopyPixelsTo(strip);
        ifd.Units.Add(strip);
    }

    /// <summary>A preview IFD: a baseline JPEG in one strip, in sRGB.</summary>
    private static DngIfd Preview(Raster<Rgb8> preview)
    {
        using MemoryStream jpeg = new();
        preview.Save(jpeg, JpegFormat.Info, new JpegWriterOptions { Quality = 90, StripMetadata = true });
        DngIfd ifd = new();
        ifd.Long(RawTags.NewSubFileType, 1);
        ifd.Long(RawTags.ImageWidth, preview.Width);
        ifd.Long(RawTags.ImageLength, preview.Height);
        ifd.Short(RawTags.BitsPerSample, 8, 8, 8);
        ifd.Short(RawTags.Compression, CompressionJpeg);
        ifd.Short(RawTags.Photometric, 6);
        ifd.Short(RawTags.SamplesPerPixel, 3);
        ifd.Long(RawTags.RowsPerStrip, preview.Height);
        ifd.Short(RawTags.PlanarConfiguration, 1);
        ifd.Short(RawTags.YCbCrSubSampling, 2, 2);
        ifd.Short(PreviewColorSpace, 2);
        ifd.Units.Add(jpeg.ToArray());
        return ifd;
    }

    /// <summary>The camera tags DNG keeps in IFD0: the source DNG's own, then the frame's version, names and colour profile.</summary>
    private static void Camera(DngIfd ifd0, DngContent content, DngCompression compression)
    {
        RawFrame frame = content.Frame;
        if (content.SourceIfd0 is RawIfd source)
        {
            Copy(ifd0, source);
        }

        bool jxl = compression == DngCompression.JpegXl;
        byte[] version = jxl ? [1, 7, 0, 0] : [1, 4, 0, 0];
        byte[] backward = jxl ? [1, 7, 0, 0] : [1, 1, 0, 0];
        if (frame.DngVersion is { Length: 4 } own && Newer(own, version))
        {
            version = own;
        }

        if (content.SourceIfd0 is RawIfd sourceIfd0 && sourceIfd0.Bytes(RawTags.DngBackwardVersion).ToArray() is { Length: 4 } ownBack && Newer(ownBack, backward))
        {
            backward = ownBack;
        }

        ifd0.Bytes(RawTags.DngVersion, version);
        ifd0.Bytes(RawTags.DngBackwardVersion, backward);
        ifd0.Ascii(RawTags.Software, "Egami");
        if (frame.Make.Length > 0)
        {
            ifd0.Ascii(RawTags.Make, frame.Make);
        }

        if (frame.Model.Length > 0)
        {
            ifd0.Ascii(RawTags.Model, frame.Model);
        }

        if (frame.Orientation is >= 1 and <= 8)
        {
            ifd0.Short(RawTags.Orientation, frame.Orientation);
        }

        string unique = frame.UniqueCameraModel ?? frame.Profile.Name ?? $"{frame.Make} {frame.Model}".Trim();
        ifd0.Ascii(RawTags.UniqueCameraModel, unique.Length > 0 ? unique : "Egami");
        RawProfile p = frame.Profile;
        Matrix(ifd0, RawTags.ColorMatrix1, p.ColorMatrix1);
        Matrix(ifd0, RawTags.ColorMatrix2, p.ColorMatrix2);
        Matrix(ifd0, RawTags.CameraCalibration1, p.CameraCalibration1);
        Matrix(ifd0, RawTags.CameraCalibration2, p.CameraCalibration2);
        Matrix(ifd0, RawTags.ForwardMatrix1, p.ForwardMatrix1);
        Matrix(ifd0, RawTags.ForwardMatrix2, p.ForwardMatrix2);
        if (p.Illuminant1 > 0)
        {
            ifd0.Short(RawTags.CalibrationIlluminant1, p.Illuminant1);
        }

        if (p.Illuminant2 > 0 && p.ColorMatrix2 is not null)
        {
            ifd0.Short(RawTags.CalibrationIlluminant2, p.Illuminant2);
        }

        if (frame.AnalogBalance is double[] analog)
        {
            ifd0.Rational(RawTags.AnalogBalance, analog);
        }

        if (frame.AsShotNeutral is double[] neutral)
        {
            ifd0.Rational(RawTags.AsShotNeutral, neutral);
        }
        else if (frame.AsShotWhite is (double wx, double wy))
        {
            ifd0.Rational(RawTags.AsShotWhiteXY, wx, wy);
        }

        double baseline = frame.BaselineExposure != 0 ? frame.BaselineExposure : p.BaselineExposure;
        ifd0.SRational(RawTags.BaselineExposure, baseline);
        if (!ifd0.Has(RawTags.DngPrivateData) && frame.MakerNote is { Length: > 0 } note)
        {
            ifd0.Bytes(RawTags.DngPrivateData, MakN(note, content.MakerNoteBigEndian, content.MakerNoteOffset));
        }
    }

    private static bool Newer(byte[] a, byte[] b)
    {
        for (int i = 0; i < 4; i++)
        {
            if (a[i] != b[i])
            {
                return a[i] > b[i];
            }
        }

        return false;
    }

    private static void Matrix(DngIfd ifd, int tag, double[]? values)
    {
        if (values is { Length: > 0 })
        {
            ifd.SRational(tag, values);
        }
    }

    /// <summary>
    /// Adobe's DNGPrivateData form of a maker note: "Adobe", a zero byte, "MakN", a big-endian count of what follows, the byte
    /// order mark and offset the note had in its file, then the note.
    /// </summary>
    internal static byte[] MakN(ReadOnlySpan<byte> note, bool bigEndian, long offset)
    {
        byte[] b = new byte[20 + note.Length];
        "Adobe\0MakN"u8.CopyTo(b);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(10), (uint)(6 + note.Length));
        (bigEndian ? "MM"u8 : "II"u8).CopyTo(b.AsSpan(14));
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16), (uint)Math.Clamp(offset, 0, uint.MaxValue));
        note.CopyTo(b.AsSpan(20));
        return b;
    }

    /// <summary>EXIF (primary tags in IFD0, the EXIF and GPS IFDs) and XMP. The maker note lives in DNGPrivateData instead.</summary>
    private static void Metadata(DngIfd ifd0, DngContent content)
    {
        if (content.Exif is ExifData exif)
        {
            DngIfd exifIfd = new();
            DngIfd gps = new();
            foreach (ExifEntry entry in exif.Entries)
            {
                int tag = entry.Key.Tag;
                switch (entry.Key.Ifd)
                {
                    case ExifIfd.Primary when !Structural.Contains(tag) && tag is not (RawTags.Make or RawTags.Model or RawTags.Orientation) && !ifd0.Has(tag):
                        ifd0.Raw(tag, (int)entry.Type, entry.Count, LittleEndian(entry));
                        break;
                    case ExifIfd.Exif when tag is not (RawTags.MakerNote or 0xA005):
                        exifIfd.Raw(tag, (int)entry.Type, entry.Count, LittleEndian(entry));
                        break;
                    case ExifIfd.Gps:
                        gps.Raw(tag, (int)entry.Type, entry.Count, LittleEndian(entry));
                        break;
                }
            }

            ifd0.Exif = exifIfd.Count > 0 ? exifIfd : null;
            ifd0.Gps = gps.Count > 0 ? gps : null;
        }

        if (content.Xmp is { Length: > 0 } xmp)
        {
            ifd0.Bytes(RawTags.Xmp, xmp);
        }
    }

    private static byte[] LittleEndian(ExifEntry entry)
    {
        byte[] bytes = entry.RawValue.ToArray();
        if (entry.IsBigEndian)
        {
            Reverse(bytes, (int)entry.Type);
        }

        return bytes;
    }

    /// <summary>Puts each value of a TIFF type in the other byte order; rationals' two halves each.</summary>
    private static void Reverse(byte[] bytes, int type)
    {
        int size = type switch
        {
            3 or 8 => 2,
            4 or 9 or 11 or 13 or 5 or 10 => 4,
            12 or 16 or 17 or 18 => 8,
            _ => 1,
        };
        for (int i = 0; size > 1 && i + size <= bytes.Length; i += size)
        {
            Array.Reverse(bytes, i, size);
        }
    }

    /// <summary>Copies a source IFD's tags that carry values, not layout or pointers, into <paramref name="to"/>, little-endian.</summary>
    private static void Copy(DngIfd to, RawIfd from)
    {
        foreach (RawEntry entry in from.Entries.Values)
        {
            if (entry.ValueAt < 0 || entry.Type is < 1 or > 12 || Structural.Contains(entry.Tag) || entry.ByteCount > int.MaxValue)
            {
                continue;
            }

            byte[] bytes = from.Bytes(entry).ToArray();
            if (from.File.BigEndian)
            {
                Reverse(bytes, entry.Type);
            }

            to.Raw(entry.Tag, entry.Type, (int)entry.Count, bytes);
        }
    }
}
