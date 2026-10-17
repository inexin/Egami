using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;

using Egami.Colors;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Jxl;

/// <summary>
/// Writes lossless JPEG XL (Phase 2.9.3): the raster's samples as they are (8 or 16 bit, half or single float; gray or RGB;
/// alpha when it is not opaque everywhere), coded by <see cref="JxlLosslessEncoder"/> at the effort the options ask for. The
/// color space is an enumerated encoding when it is sRGB, linear sRGB, PQ or HLG, else the ICC profile; EXIF and XMP go in
/// boxes of the container, which a file without them does without, and the EXIF orientation also into the codestream header.
/// Every pixel is needed before the first byte, so rows are gathered and encoded at <see cref="RowEncoder{TPixel}.Complete"/>.
/// </summary>
internal sealed class JxlWriter : IFormatWriter, IRowFormatWriter
{
    internal static readonly JxlWriter Instance = new();

    public void Write<TPixel>(Raster<TPixel> raster, Stream output, WriterOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Streaming.RowEncoding.Save(this, raster, output, options);
    }

    public RowEncoder<TPixel> Start<TPixel>(Stream output, EncodeStart<TPixel> start)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        JxlWriterOptions jxl = start.Options as JxlWriterOptions ?? JxlWriterOptions.Default;
        if (!jxl.WritesLossless(start.Metadata))
        {
            start.Refuse("JPEG XL", "Lossy encoding", "Save from a whole raster.");
            SaveLosses.Report(start, "JPEG XL", SaveKeeps.Alpha | SaveKeeps.Exif | SaveKeeps.Xmp | SaveKeeps.Icc);
            return new LossyEncoder<TPixel>(output, start, jxl);
        }

        start.Refuse("JPEG XL", "Lossless encoding", "Save from a whole raster.");
        SaveKeeps keeps = SaveKeeps.Alpha | SaveKeeps.Wide | SaveKeeps.Float | SaveKeeps.Exif | SaveKeeps.Xmp | SaveKeeps.Icc;
        SaveLosses.Report(start, "JPEG XL", keeps);
        return new Encoder<TPixel>(output, start, jxl);
    }

    /// <summary>Gets the raster's samples as the encoder takes them, in the raster's own precision.</summary>
    internal static JxlEncodeInput ToInput(Raster raster, int effort)
    {
        Planes planes = new(raster.Layout, raster.Width, raster.Height);
        switch (planes.Kind)
        {
            case PixelChannelType.Unorm8:
                {
                    using Raster<Rgba8> r = raster.As<Rgba8>();
                    for (int y = 0; y < raster.Height; y++)
                    {
                        planes.Add8(r.Row(y), y);
                    }

                    break;
                }

            case PixelChannelType.Float32:
                {
                    using Raster<RgbaF32> r = raster.As<RgbaF32>();
                    for (int y = 0; y < raster.Height; y++)
                    {
                        planes.AddF32(r.Row(y), y);
                    }

                    break;
                }

            case PixelChannelType.Float16:
                {
                    using Raster<RgbaF16> r = raster.As<RgbaF16>();
                    for (int y = 0; y < raster.Height; y++)
                    {
                        planes.AddF16(r.Row(y), y);
                    }

                    break;
                }

            default:
                {
                    using Raster<Rgba16> r = raster.As<Rgba16>();
                    for (int y = 0; y < raster.Height; y++)
                    {
                        planes.Add16(r.Row(y), y);
                    }

                    break;
                }
        }

        return planes.ToInput(effort);
    }

    /// <summary>Gets the color encoding to write: enumerated when the space is one JPEG XL names, else the ICC profile.</summary>
    internal static (JxlColorEncoding Encoding, byte[]? Icc) ColorOf(RasterMetadata metadata, WriterOptions options, bool gray)
    {
        if (metadata.CodePoints is ColorCodePoints codes && ColorProfile.FromCodePoints(codes) is ColorProfile coded)
        {
            if (coded.IsEquivalentTo(ColorProfile.Rec2100Pq))
            {
                return (new JxlColorEncoding { Primaries = 9, TransferFunction = 16 }, null);
            }

            if (coded.IsEquivalentTo(ColorProfile.Rec2100Hlg))
            {
                return (new JxlColorEncoding { Primaries = 9, TransferFunction = 18 }, null);
            }
        }

        if (metadata.ColorSpace == ColorSpaceHint.Linear)
        {
            return (new JxlColorEncoding { TransferFunction = 8 }, null);
        }

        ReadOnlyMemory<byte> icc = options.ProfileToWrite(metadata);
        if (!icc.IsEmpty && !options.StripMetadata)
        {
            ColorProfile? profile = null;
            try
            {
                profile = ColorProfile.FromIcc(icc.Span);
            }
            catch (CorruptDataException)
            {
            }

            if (profile is not null && !gray && profile.IsEquivalentTo(ColorProfile.Srgb))
            {
                return (new JxlColorEncoding(), null);
            }

            if (profile is not null && !gray && profile.IsEquivalentTo(ColorProfile.LinearSrgb))
            {
                return (new JxlColorEncoding { TransferFunction = 8 }, null);
            }

            return (new JxlColorEncoding(), icc.ToArray());
        }

        return (new JxlColorEncoding(), null);
    }

    /// <summary>Wraps the codestream in the container (ISO/IEC 18181-2) with jbrd, Exif and xml boxes, or returns it bare when there are none.</summary>
    internal static void WriteFile(Stream output, byte[] codestream, byte[]? exif, byte[]? xmp, byte[]? jbrd = null, bool compressBoxes = false)
    {
        if (exif is null && xmp is null && jbrd is null)
        {
            output.Write(codestream);
            return;
        }

        Box(output, "JXL ", [0x0D, 0x0A, 0x87, 0x0A]);
        Box(output, "ftyp", [.. "jxl "u8, 0, 0, 0, 0, .. "jxl "u8]);
        if (jbrd is not null)
        {
            Box(output, "jbrd", jbrd);
        }

        if (exif is not null)
        {
            // The box starts with the offset of the TIFF header within it: none here.
            MetadataBox(output, "Exif", [0, 0, 0, 0, .. exif], compressBoxes);
        }

        if (xmp is not null)
        {
            MetadataBox(output, "xml ", xmp, compressBoxes);
        }

        Box(output, "jxlc", codestream);
    }

    /// <summary>Writes a metadata box, or a brob box holding it Brotli compressed at quality 4, as cjxl does for recompressed JPEGs.</summary>
    private static void MetadataBox(Stream output, string type, byte[] payload, bool compress)
    {
        if (!compress)
        {
            Box(output, type, payload);
            return;
        }

        byte[] packed = new byte[4 + BrotliEncoder.GetMaxCompressedLength(payload.Length)];
        System.Text.Encoding.Latin1.GetBytes(type, packed);
        if (!BrotliEncoder.TryCompress(payload, packed.AsSpan(4), out int written, 4, 22))
        {
            throw new InvalidOperationException("Brotli compression failed.");
        }

        Box(output, "brob", packed.AsSpan(0, 4 + written));
    }

    private static void Box(Stream output, string type, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)(payload.Length + 8));
        System.Text.Encoding.Latin1.GetBytes(type, header[4..]);
        output.Write(header);
        output.Write(payload);
    }

    /// <summary>Gathers rows into planes and encodes them at the end.</summary>
    private sealed class Encoder<TPixel>(Stream output, EncodeStart<TPixel> start, JxlWriterOptions options) : RowEncoder<TPixel>
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        private readonly Planes _planes = new(TPixel.Layout, start.Width, start.Height);
        private readonly IntermediateRow _row = new(start.Width);
        private int _rows;

        public override void WriteRow(ReadOnlySpan<TPixel> row)
        {
            if (_rows == start.Height)
            {
                throw new InvalidOperationException($"The image has {start.Height} rows; no more can be written.");
            }

            switch (_planes.Kind)
            {
                case PixelChannelType.Unorm8:
                    PixelConvert.Convert(row, _row.Rgba8);
                    _planes.Add8(_row.Rgba8, _rows);
                    break;
                case PixelChannelType.Float32:
                    PixelConvert.Convert(row, _row.RgbaF32);
                    _planes.AddF32(_row.RgbaF32, _rows);
                    break;
                case PixelChannelType.Float16:
                    PixelConvert.Convert(row, _row.RgbaF16);
                    _planes.AddF16(_row.RgbaF16, _rows);
                    break;
                default:
                    PixelConvert.Convert(row, _row.Rgba16);
                    _planes.Add16(_row.Rgba16, _rows);
                    break;
            }

            _rows++;
        }

        public override void Complete()
        {
            if (_rows != start.Height)
            {
                throw new InvalidOperationException($"The image has {start.Height} rows, but {_rows} were written.");
            }

            RasterMetadata metadata = start.Metadata;
            bool strip = options.StripMetadata;
            JxlEncodeInput input = _planes.ToInput(options.Effort) with { Parallelism = start.Context.MaxParallelism };
            (JxlColorEncoding color, byte[]? icc) = ColorOf(metadata, options, input.Color.Count == 1);
            int orientation = metadata.Exif?.Orientation is ImageOrientation o && (int)o is >= 1 and <= 8 ? (int)o : 1;
            input = input with { ColorEncoding = color, Icc = icc, Orientation = orientation };
            byte[] codestream = JxlLosslessEncoder.Encode(input);
            byte[]? exif = strip || metadata.Exif is null ? null : metadata.Exif.ToBytes().ToArray();
            byte[]? xmp = strip || metadata.Xmp.IsEmpty ? null : metadata.Xmp.ToArray();
            WriteFile(output, codestream, exif, xmp);
        }
    }

    /// <summary>Converts rows to linear light as they come and encodes them lossy at the end (Phase 2.9.7).</summary>
    private sealed class LossyEncoder<TPixel>(Stream output, EncodeStart<TPixel> start, JxlWriterOptions options) : RowEncoder<TPixel>
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        private readonly JxlLinearRows _rows = new(start.Width, start.Height, start.Metadata.GetColorProfile(), TPixel.Layout);
        private readonly int _alphaBits = TPixel.Layout.ChannelType == PixelChannelType.Unorm8 ? 8 : 16;
        private readonly int[]? _alpha = TPixel.Layout.Alpha != AlphaKind.None ? new int[start.Width * start.Height] : null;
        private Cmyk16[]? _inks;
        private Rgba8[]? _rgba8;
        private Rgba16[]? _rgba16;
        private int _written;

        public override void WriteRow(ReadOnlySpan<TPixel> row)
        {
            if (_written == start.Height)
            {
                throw new InvalidOperationException($"The image has {start.Height} rows; no more can be written.");
            }

            if (_rows.HasBlack)
            {
                _inks ??= new Cmyk16[start.Width];
                PixelConvert.Convert(row, _inks.AsSpan());
                _rows.Add<Cmyk16>(_inks, _written);
            }
            else
            {
                _rows.Add(row, _written);
            }

            if (_alpha is not null)
            {
                // Straight alpha: premultiplied color was made straight by the color conversion.
                int at = _written * start.Width;
                if (_alphaBits == 8)
                {
                    _rgba8 ??= new Rgba8[start.Width];
                    PixelConvert.Convert(row, _rgba8.AsSpan());
                    for (int x = 0; x < start.Width; x++)
                    {
                        _alpha[at + x] = _rgba8[x].A;
                    }
                }
                else
                {
                    _rgba16 ??= new Rgba16[start.Width];
                    PixelConvert.Convert(row, _rgba16.AsSpan());
                    for (int x = 0; x < start.Width; x++)
                    {
                        _alpha[at + x] = _rgba16[x].A;
                    }
                }
            }

            _written++;
        }

        public override void Complete()
        {
            if (_written != start.Height)
            {
                throw new InvalidOperationException($"The image has {start.Height} rows, but {_written} were written.");
            }

            RasterMetadata metadata = start.Metadata;
            bool strip = options.StripMetadata;
            JxlLinearImage image = _rows.Image;
            (JxlColorEncoding color, byte[]? icc) = ColorOf(metadata, options, image.IsGray);
            int orientation = metadata.Exif?.Orientation is ImageOrientation o && (int)o is >= 1 and <= 8 ? (int)o : 1;
            int opaque = (1 << _alphaBits) - 1;
            int[]? alpha = _alpha is not null && !Array.TrueForAll(_alpha, v => v == opaque) ? _alpha : null;
            JxlEncodeInput header = new()
            {
                Width = start.Width,
                Height = start.Height,
                Color = image.IsGray ? [[]] : [[], [], []],
                ColorEncoding = color,
                Icc = icc,
                Orientation = orientation,
                BitDepth = TPixel.Layout.ChannelType switch
                {
                    PixelChannelType.Unorm8 => new JxlBitDepth(8, 0),
                    PixelChannelType.Float16 => new JxlBitDepth(16, 5),
                    PixelChannelType.Float32 => new JxlBitDepth(32, 8),
                    _ => new JxlBitDepth(16, 0),
                },
                Alpha = alpha,
                AlphaBitDepth = new JxlBitDepth(_alphaBits, 0),
                HasBlack = image.Black is not null,
            };
            JxlLossySettings settings = new()
            {
                Distance = options.EffectiveDistance,
                Effort = options.Effort,
                Parallelism = start.Context.MaxParallelism,
                PhotonNoiseIso = options.PhotonNoiseIso ?? 0,
                SimplifyInvisible = !options.KeepTransparentColors,
                AlphaDistance = options.AlphaDistance,
            };
            byte[] codestream = JxlVarDctEncoder.Encode(image, header, settings);
            byte[]? exif = strip || metadata.Exif is null ? null : metadata.Exif.ToBytes().ToArray();
            byte[]? xmp = strip || metadata.Xmp.IsEmpty ? null : metadata.Xmp.ToArray();
            WriteFile(output, codestream, exif, xmp);
        }
    }

    /// <summary>One row converted to the intermediate type the planes are filled from.</summary>
    private sealed class IntermediateRow(int width)
    {
        private Rgba8[]? _rgba8;
        private Rgba16[]? _rgba16;
        private RgbaF32[]? _rgbaF32;
        private RgbaF16[]? _rgbaF16;

        internal Span<Rgba8> Rgba8 => _rgba8 ??= new Rgba8[width];

        internal Span<Rgba16> Rgba16 => _rgba16 ??= new Rgba16[width];

        internal Span<RgbaF32> RgbaF32 => _rgbaF32 ??= new RgbaF32[width];

        internal Span<RgbaF16> RgbaF16 => _rgbaF16 ??= new RgbaF16[width];
    }

    /// <summary>The planes the encoder takes, filled row by row: one or three color planes and alpha when the layout has it.</summary>
    private sealed class Planes
    {
        private readonly int _width;
        private readonly int _height;
        private readonly int[][] _color;
        private readonly int[]? _alpha;
        private readonly bool _premultiplied;

        internal Planes(PixelLayoutInfo layout, int width, int height)
        {
            _width = width;
            _height = height;
            bool gray = layout.ChannelOrder is PixelChannelOrder.Gray or PixelChannelOrder.GrayAlpha;
            _color = [.. Enumerable.Range(0, gray ? 1 : 3).Select(_ => new int[width * height])];
            _alpha = layout.Alpha != AlphaKind.None ? new int[width * height] : null;
            _premultiplied = layout.Alpha == AlphaKind.Premultiplied;
            Kind = layout.ChannelType switch
            {
                PixelChannelType.Unorm8 or PixelChannelType.Float32 or PixelChannelType.Float16 => layout.ChannelType,
                _ => PixelChannelType.Unorm16,
            };
        }

        internal PixelChannelType Kind { get; }

        internal void Add8(ReadOnlySpan<Rgba8> row, int y) => Add(MemoryMarshal.AsBytes(row), y, static (b, i) => b[i]);

        internal void Add16(ReadOnlySpan<Rgba16> row, int y) => Add(MemoryMarshal.Cast<Rgba16, ushort>(row), y, static (s, i) => s[i]);

        internal void AddF32(ReadOnlySpan<RgbaF32> row, int y) => Add(MemoryMarshal.Cast<RgbaF32, int>(row), y, static (s, i) => s[i]);

        internal void AddF16(ReadOnlySpan<RgbaF16> row, int y) => Add(MemoryMarshal.Cast<RgbaF16, ushort>(row), y, static (s, i) => s[i]);

        /// <summary>Gets the encoder's input; alpha that is opaque everywhere is left out.</summary>
        internal JxlEncodeInput ToInput(int effort)
        {
            JxlBitDepth depth = Kind switch
            {
                PixelChannelType.Unorm8 => new JxlBitDepth(8, 0),
                PixelChannelType.Float32 => new JxlBitDepth(32, 8),
                PixelChannelType.Float16 => new JxlBitDepth(16, 5),
                _ => new JxlBitDepth(16, 0),
            };
            int[]? alpha = _alpha;
            if (alpha is not null)
            {
                int opaque = depth.IsFloat ? (depth.BitsPerSample == 32 ? BitConverter.SingleToInt32Bits(1f) : BitConverter.HalfToUInt16Bits((Half)1)) : (1 << depth.BitsPerSample) - 1;
                if (Array.TrueForAll(alpha, v => v == opaque))
                {
                    alpha = null;
                }
            }

            return new JxlEncodeInput
            {
                Width = _width,
                Height = _height,
                Color = _color,
                Alpha = alpha,
                BitDepth = depth,
                AlphaBitDepth = depth,
                AlphaPremultiplied = _premultiplied,
                Effort = effort,
            };
        }

        private void Add<T>(ReadOnlySpan<T> samples, int y, Func<ReadOnlySpan<T>, int, int> get)
        {
            int at = y * _width;
            for (int x = 0; x < _width; x++)
            {
                int s = x * 4;
                _color[0][at + x] = get(samples, s);
                if (_color.Length == 3)
                {
                    _color[1][at + x] = get(samples, s + 1);
                    _color[2][at + x] = get(samples, s + 2);
                }

                if (_alpha is not null)
                {
                    _alpha[at + x] = get(samples, s + 3);
                }
            }
        }
    }
}

/// <summary>Options for writing JPEG XL.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Jxl.cs" region="save"/>
/// </example>
public sealed class JxlWriterOptions : WriterOptions
{
    private readonly int _effort = 5;
    private readonly int _quality = 90;
    private readonly float? _distance;
    private readonly float? _photonNoiseIso;
    private readonly float _alphaDistance;

    /// <summary>Gets the default options.</summary>
    public static new JxlWriterOptions Default { get; } = new();

    /// <summary>
    /// Gets how hard the encoder works, 1 (fastest) to 7 (smallest files), on libjxl's effort scale for both kinds, so the same
    /// number gives about the same file as cjxl -e. Lossless: 1 is libjxl's fast lossless path, 2 and 3 use fixed trees, 4 and
    /// up learn the tree, and 5 and up also try local palettes and color transforms. Lossy: 1 to 3 use 8×8 DCTs and one
    /// quantization value, 4 adds finer LF and context modeling, 5 adaptive quantization, chroma from luma, Gaborish and simple
    /// block sizes, 6 the full block-size search, and 7 patches for repeated shapes such as text. Defaults to 5.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 1 to 7.</exception>
    public int Effort
    {
        get => _effort;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 7);
            _effort = value;
        }
    }

    /// <summary>
    /// Gets whether to write lossless (Modular, every sample exact) or lossy (VarDCT in XYB) data, or <see langword="null"/>
    /// (the default) to keep the kind of the JPEG XL file the raster was read from (lossless for a Modular file without XYB),
    /// and write lossy otherwise, as cjxl does for PNG input. <see cref="Quality"/> 100 with no <see cref="Distance"/> also
    /// writes lossless unless this is <see langword="false"/>.
    /// </summary>
    public bool? Lossless { get; init; }

    /// <summary>
    /// Gets the lossy quality, 0 (smallest) to 100 (best), on cjxl's -q scale, so the same number gives about the same file:
    /// 90 is distance 1.0 (visually lossless), each step below 100 adds 0.09 down to 30, and the distance grows faster below.
    /// 100 is lossless. <see cref="Distance"/>, when set, takes its place. Defaults to 90.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 100.</exception>
    public int Quality
    {
        get => _quality;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 0);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 100);
            _quality = value;
        }
    }

    /// <summary>
    /// Gets the Butteraugli-style distance lossy files are written at, as cjxl -d takes it: 1.0 is visually lossless, larger
    /// values give smaller files with more visible loss, 0.1 to 25. Defaults to <see langword="null"/>, which takes the distance
    /// from <see cref="Quality"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0.1 to 25.</exception>
    public float? Distance
    {
        get => _distance;
        init
        {
            if (value is float d && !(d >= 0.1f && d <= 25f))
            {
                throw new ArgumentOutOfRangeException(nameof(value), d, "The distance must be from 0.1 to 25.");
            }

            _distance = value;
        }
    }

    /// <summary>
    /// Gets the ISO speed of film-like photon noise the decoder adds to lossy images, as cjxl --photon_noise_iso takes it (for
    /// example 3200 for a grainy look); the noise depends on the image size like a 35 mm sensor's. Defaults to
    /// <see langword="null"/>, no noise. Lossless files ignore it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a positive number up to 10⁷.</exception>
    public float? PhotonNoiseIso
    {
        get => _photonNoiseIso;
        init
        {
            if (value is float iso && !(iso > 0 && iso <= 1e7f))
            {
                throw new ArgumentOutOfRangeException(nameof(value), iso, "The photon noise ISO must be above 0 and at most 10⁷.");
            }

            _photonNoiseIso = value;
        }
    }

    /// <summary>
    /// Gets whether lossy files keep the color of fully transparent pixels. <see langword="false"/> lets the encoder replace it
    /// by that of the visible neighbours, which makes files smaller (libjxl does that by default); <see langword="true"/>, the
    /// default as for WebP, keeps it as well as lossy coding does. Lossless files always keep it.
    /// </summary>
    public bool KeepTransparentColors { get; init; } = true;

    /// <summary>
    /// Gets the distance alpha is written at in lossy files, on <see cref="Distance"/>'s scale: 0 (the default) keeps alpha
    /// exact, larger values (up to 25) code it lossy through Modular's squeeze, smaller but with softer edges. Lossless files
    /// keep alpha exact.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 25.</exception>
    public float AlphaDistance
    {
        get => _alphaDistance;
        init
        {
            if (!(value >= 0 && value <= 25))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The alpha distance must be from 0 to 25.");
            }

            _alphaDistance = value;
        }
    }

    /// <summary>Gets the distance lossy data is written at: <see cref="Distance"/>, or <see cref="Quality"/> on cjxl's mapping, at least 0.1.</summary>
    internal float EffectiveDistance => _distance ?? MathF.Max(0.1f, DistanceFromQuality(_quality));

    /// <summary>libjxl's JxlEncoderDistanceFromQuality: 0.1 + (100 − q) × 0.09 from 30 up, a parabola below, 0 at 100.</summary>
    internal static float DistanceFromQuality(float quality)
    {
        return quality >= 100 ? 0
            : quality >= 30 ? 0.1f + ((100 - quality) * 0.09f)
            : (53.0f / 3000.0f * quality * quality) - (23.0f / 20.0f * quality) + 25.0f;
    }

    /// <summary>Gets whether a raster with this metadata is written lossless (see <see cref="Lossless"/>).</summary>
    internal bool WritesLossless(RasterMetadata metadata)
    {
        if (Lossless is bool lossless)
        {
            return lossless;
        }

        if (_distance is null && _quality == 100)
        {
            return true;
        }

        return metadata.FormatDetails is JxlDetails jxl && !jxl.IsXybEncoded && !jxl.HasJpegReconstruction;
    }
}
