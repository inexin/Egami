using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

using Egami.Colors;
using Egami.Metadata;
using Egami.Pixels;
using Egami.Streaming;

namespace Egami.Formats.Psd;

/// <summary>Photoshop's Lab to sRGB: Lab relative to D50, adapted to sRGB's D65 by Bradford, as for TIFF's CIELab.</summary>
internal static class PsdLab
{
    private static readonly Vector3d White = Chromaticity.D50.ToXyz();
    private static readonly Matrix3d ToRgb = ColorValueMath.XyzToRgb * Matrix3d.Bradford(White, Chromaticity.D65.ToXyz());

    /// <summary>Returns the encoded sRGB of L* (0 to 100), a* and b*, each clamped to 0..1.</summary>
    public static (double R, double G, double B) ToSrgb(double l, double a, double b)
    {
        Vector3d linear = ToRgb * Cie.LabToXyz(new Vector3d(l, a, b), White);
        return (Encode(linear.X), Encode(linear.Y), Encode(linear.Z));

        static double Encode(double v) => ColorValueMath.Encode(Math.Clamp(v, 0, 1));
    }

    public static byte Unorm8(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255, MidpointRounding.AwayFromZero);

    public static ushort Unorm16(double v) => (ushort)Math.Round(Math.Clamp(v, 0, 1) * 65535, MidpointRounding.AwayFromZero);
}

/// <summary>
/// Reads PSD and PSB, written from Adobe's Photoshop File Format Specification: the flattened image stored after the layers, a
/// row at a time, each row gathered from the channel planes. Loads every color mode in the pixel type that fits it (see
/// <see cref="Natural"/>): CMYK inverted to ink amounts, Lab converted to sRGB, bitmap and duotone as gray, the first channel of a
/// multichannel file as gray. A flattened image with transparency is stored flattened onto white; the white is divided out.
/// </summary>
internal sealed class PsdReader : IFormatReader, IRowSourceReader
{
    internal static readonly PsdReader Instance = new();

    /// <summary>The pixel types a PSD loads as.</summary>
    internal enum Natural
    {
        Gray8,
        GrayAlpha8,
        Gray16,
        GrayAlpha16,
        GrayF32,
        Rgb8,
        Rgba8,
        Rgb16,
        Rgba16,
        RgbF32,
        RgbaF32,
        Cmyk8,
        Cmyk16,
    }

    public RasterInfo ReadInfo(EgamiInput input, ReaderOptions options)
    {
        PsdFile file = PsdFile.Read(input, options, wantsLayers: f => Flattens(f, options));
        bool flattens = file.Records.Count > 0 && Flattens(file, options);
        Natural natural = flattens ? NaturalOf(file, !OpaqueByRecords(file)) : NaturalOf(file);
        PixelLayoutInfo layout = LayoutOf(natural);
        ImageOrientation orientation = ImageOrientation.Normal;
        if (file.Resources.FirstOrDefault(r => r.Id == 1058).Data is byte[] exif)
        {
            orientation = Jpeg.JpegSegmentReader.ParseExif(exif, input.Limits.MaxMetadataBytes, new WarningList()).Orientation;
        }

        int channels = Needed(file).Count;
        return new RasterInfo
        {
            Format = PsdFormat.Info,
            Width = file.Width,
            Height = file.Height,
            Layout = layout,
            FrameCount = 1,
            ImageCount = 1,
            HasMetadata = file.Resources.Any(r => r.Id is 1039 or 1058 or 1060 or 1028 or 1005),
            EstimatedBytes = (long)file.Width * file.Height * layout.BytesPerPixel,
            Orientation = orientation,
            Access = flattens ? RasterAccess.Buffered : RasterAccess.Streamed,
            WorkingBytes = flattens ? PsdFlattener.WorkingBytes(file) + LargestLayer(file) : PsdPlanes.PlannedBytes(file, channels) + RowsWorkingBytes(file, layout),
        };
    }

    /// <summary>Returns whether a file is loaded by flattening its layers: when the options ask for it, or by default when it has no real composite.</summary>
    internal static bool Flattens(PsdFile file, ReaderOptions options)
    {
        PsdComposite composite = (options as PsdReaderOptions)?.Composite ?? PsdComposite.Auto;
        bool wanted = composite == PsdComposite.Flatten || (composite == PsdComposite.Auto && !file.HasRealComposite);
        return wanted && file.Mode is PsdColorMode.Rgb or PsdColorMode.Grayscale or PsdColorMode.Duotone or PsdColorMode.Cmyk;
    }

    /// <summary>
    /// Returns whether the records alone show the flattened image is opaque everywhere: a visible pixel layer at the top level
    /// with no transparency channel, no mask, Normal at full opacity and fill, covering the canvas. Everything drawn over it keeps
    /// it opaque. Load and Probe decide the type of a flattened image by this, before any pixels are read.
    /// </summary>
    internal static bool OpaqueByRecords(PsdFile file)
    {
        int level = 0;
        foreach (PsdLayerRecord record in file.Records)
        {
            PsdLayer info = record.Info;
            if (info.Kind == PsdLayerKind.GroupEnd)
            {
                level++;
                continue;
            }

            if (info.Kind != PsdLayerKind.Layer)
            {
                level = Math.Max(0, level - 1);
                continue;
            }

            if (level == 0 && info.IsVisible && !info.IsClipped && info.LayerType == PsdLayerType.Pixel && info.BlendModeKey == "norm"
                && info.Opacity >= 1 && info.FillOpacity >= 1 && record.Mask is null && !record.Has("vmsk") && !record.Has("vsms")
                && !Array.Exists(record.Channels, c => c.Id == -1) && Array.TrueForAll(record.BlendRanges, r => r == 0x0000FFFFu)
                && record.Left <= 0 && record.Top <= 0 && record.Right >= file.Width && record.Bottom >= file.Height)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns the bytes of the largest layer as ReadLayers holds it, which flattening holds one at a time.</summary>
    private static long LargestLayer(PsdFile file)
    {
        long largest = 0;
        foreach (PsdLayerRecord record in file.Records)
        {
            (int w, int h) = record.Size;
            largest = Math.Max(largest, (long)w * h * (file.Depth == 32 ? 16 : file.Depth == 16 ? 8 : 4) * 2);
        }

        return largest;
    }

    public Raster<TPixel> Read<TPixel>(EgamiInput input, ReaderOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using RowSource rows = OpenRows(input, options, ownsInput: false);
        return ReadAll<TPixel>(input, rows);
    }

    public Raster Read(EgamiInput input, ReaderOptions options)
    {
        using RowSource rows = OpenRows(input, options, ownsInput: false);
        return rows.WithNaturalType(new NaturalRead(input, rows));
    }

    private static Raster<TPixel> ReadAll<TPixel>(EgamiInput input, RowSource rows)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Raster<TPixel> raster = input.CreateRaster<TPixel>(rows.StoredWidth, rows.StoredHeight);
        try
        {
            rows.NextFrame();
            for (int y = 0; y < raster.Height; y++)
            {
                input.CancellationToken.ThrowIfCancellationRequested();
                rows.ReadRow(y, 0, raster.Row(y));
            }

            raster.Metadata.ReplaceWith(rows.Metadata);
            return raster;
        }
        catch
        {
            raster.Dispose();
            throw;
        }
    }

    public RowSource Open(EgamiInput input, ReaderOptions options) => OpenRows(input, options, ownsInput: true);

    private static RowSource OpenRows(EgamiInput input, ReaderOptions options, bool ownsInput)
    {
        PsdFlattener? flattener = null;
        PsdFile file = PsdFile.Read(input, options, channels: (f, i, end) =>
        {
            if (f.Records.Count == 0)
            {
                return;
            }

            // Each layer is decoded, flattened in and let go before the next.
            flattener = new PsdFlattener(f, PsdFlattenOptions.Default, i.Context, f.Warnings, index => f.Records[index].Info);
            Colors.InkSet? inks = f.Mode == PsdColorMode.Cmyk ? PsdLayerReader.InksOf(f) : null;
            PsdChannels.Read(i, f, end, options, (index, planes) =>
            {
                (Raster? pixels, InkRaster? ink, PsdMask? mask) = PsdLayerReader.Build(planes, f, i, inks, null);
                try
                {
                    flattener.Add(index, pixels, mask, ink);
                }
                finally
                {
                    pixels?.Dispose();
                    ink?.Dispose();
                    mask?.Pixels.Dispose();
                }
            });
        }, wantsLayers: f => Flattens(f, options));
        if (flattener is null)
        {
            RasterMetadata stored = Metadata(input, file, options);
            if ((options as PsdReaderOptions)?.Composite == PsdComposite.Flatten && file.Records.Count > 0)
            {
                stored.WarningList.Add("Psd.FlattenUnsupported", $"{file.Mode} files are not flattened; the composite Photoshop stored was loaded.");
            }

            return RowsOf(input, file, options, stored, ownsInput);
        }

        bool alpha = !OpaqueByRecords(file);
        Raster flat = flattener.Finish();
        try
        {
            Raster typed = Typed(flat, NaturalOf(file, alpha));
            if (!ReferenceEquals(typed, flat))
            {
                flat.Dispose();
            }

            flat = typed;
            flat.Metadata.ReplaceWith(Metadata(input, file, options, alpha, flattened: true));
            if (ownsInput)
            {
                input.ReturnUnreadBytes();
                input.Dispose();
            }

            return BufferedRowSource.From(flat);
        }
        catch
        {
            flat.Dispose();
            throw;
        }
    }

    /// <summary>Returns the raster in the natural type, converting it when it is another.</summary>
    private static Raster Typed(Raster raster, Natural natural)
    {
        return natural switch
        {
            Natural.Gray8 => raster as Raster<Gray8> ?? raster.As<Gray8>(),
            Natural.GrayAlpha8 => raster as Raster<GrayAlpha8> ?? raster.As<GrayAlpha8>(),
            Natural.Gray16 => raster as Raster<Gray16> ?? raster.As<Gray16>(),
            Natural.GrayAlpha16 => raster as Raster<GrayAlpha16> ?? raster.As<GrayAlpha16>(),
            Natural.GrayF32 => raster as Raster<GrayF32> ?? raster.As<GrayF32>(),
            Natural.Rgb8 => raster as Raster<Rgb8> ?? raster.As<Rgb8>(),
            Natural.Rgba8 => raster as Raster<Rgba8> ?? raster.As<Rgba8>(),
            Natural.Rgb16 => raster as Raster<Rgb16> ?? raster.As<Rgb16>(),
            Natural.Rgba16 => raster as Raster<Rgba16> ?? raster.As<Rgba16>(),
            Natural.RgbF32 => raster as Raster<RgbF32> ?? raster.As<RgbF32>(),
            Natural.RgbaF32 => raster as Raster<RgbaF32> ?? raster.As<RgbaF32>(),
            Natural.Cmyk8 => raster as Raster<Cmyk8> ?? raster.As<Cmyk8>(),
            _ => raster as Raster<Cmyk16> ?? raster.As<Cmyk16>(),
        };
    }

    /// <summary>Opens the stored composite's rows of a file already read up to them, in the natural type.</summary>
    internal static RowSource RowsOf(EgamiInput input, PsdFile file, ReaderOptions options, RasterMetadata metadata, bool ownsInput)
    {
        PsdPlanes planes = PsdPlanes.Open(input, file, Needed(file), options, metadata.WarningList);
        try
        {
            return NaturalOf(file) switch
            {
                Natural.Gray8 => new PsdRows<Gray8>(input, file, planes, metadata, ownsInput),
                Natural.GrayAlpha8 => new PsdRows<GrayAlpha8>(input, file, planes, metadata, ownsInput),
                Natural.Gray16 => new PsdRows<Gray16>(input, file, planes, metadata, ownsInput),
                Natural.GrayAlpha16 => new PsdRows<GrayAlpha16>(input, file, planes, metadata, ownsInput),
                Natural.GrayF32 => new PsdRows<GrayF32>(input, file, planes, metadata, ownsInput),
                Natural.Rgb8 => new PsdRows<Rgb8>(input, file, planes, metadata, ownsInput),
                Natural.Rgba8 => new PsdRows<Rgba8>(input, file, planes, metadata, ownsInput),
                Natural.Rgb16 => new PsdRows<Rgb16>(input, file, planes, metadata, ownsInput),
                Natural.Rgba16 => new PsdRows<Rgba16>(input, file, planes, metadata, ownsInput),
                Natural.RgbF32 => new PsdRows<RgbF32>(input, file, planes, metadata, ownsInput),
                Natural.RgbaF32 => new PsdRows<RgbaF32>(input, file, planes, metadata, ownsInput),
                Natural.Cmyk8 => new PsdRows<Cmyk8>(input, file, planes, metadata, ownsInput),
                _ => new PsdRows<Cmyk16>(input, file, planes, metadata, ownsInput),
            };
        }
        catch
        {
            planes.Dispose();
            throw;
        }
    }

    /// <summary>Returns the channels the pixels are made of: the color channels and, when there is one, the transparency.</summary>
    private static List<int> Needed(PsdFile file)
    {
        List<int> channels = [.. Enumerable.Range(0, file.ColorChannels)];
        if (file.MergedAlpha)
        {
            channels.Add(file.ColorChannels);
        }

        return channels;
    }

    internal static Natural NaturalOf(PsdFile file, bool? withAlpha = null)
    {
        bool alpha = withAlpha ?? file.MergedAlpha;
        return file.Mode switch
        {
            PsdColorMode.Bitmap => Natural.Gray8,
            PsdColorMode.Multichannel => file.Depth == 16 ? Natural.Gray16 : Natural.Gray8,
            PsdColorMode.Grayscale or PsdColorMode.Duotone => file.Depth switch
            {
                32 => alpha ? Natural.RgbaF32 : Natural.GrayF32,
                16 => alpha ? Natural.GrayAlpha16 : Natural.Gray16,
                _ => alpha ? Natural.GrayAlpha8 : Natural.Gray8,
            },
            PsdColorMode.Indexed => alpha || file.TransparentIndex is not null ? Natural.Rgba8 : Natural.Rgb8,
            PsdColorMode.Cmyk => file.Depth == 16 ? (alpha ? Natural.Rgba16 : Natural.Cmyk16) : alpha ? Natural.Rgba8 : Natural.Cmyk8,
            _ => file.Depth switch
            {
                32 => alpha ? Natural.RgbaF32 : Natural.RgbF32,
                16 => alpha ? Natural.Rgba16 : Natural.Rgb16,
                _ => alpha ? Natural.Rgba8 : Natural.Rgb8,
            },
        };
    }

    private static PixelLayoutInfo LayoutOf(Natural natural) => natural switch
    {
        Natural.Gray8 => Gray8.Layout,
        Natural.GrayAlpha8 => GrayAlpha8.Layout,
        Natural.Gray16 => Gray16.Layout,
        Natural.GrayAlpha16 => GrayAlpha16.Layout,
        Natural.GrayF32 => GrayF32.Layout,
        Natural.Rgb8 => Rgb8.Layout,
        Natural.Rgba8 => Rgba8.Layout,
        Natural.Rgb16 => Rgb16.Layout,
        Natural.Rgba16 => Rgba16.Layout,
        Natural.RgbF32 => RgbF32.Layout,
        Natural.RgbaF32 => RgbaF32.Layout,
        Natural.Cmyk8 => Cmyk8.Layout,
        _ => Cmyk16.Layout,
    };

    /// <summary>Returns what the rows hold besides the planes: one stored row a channel and one natural row.</summary>
    private static long RowsWorkingBytes(PsdFile file, PixelLayoutInfo layout)
    {
        long rowBytes = file.Depth == 1 ? (file.Width + 7) / 8 : (long)file.Width * file.Depth / 8;
        return (rowBytes * (file.ColorChannels + 1)) + ((long)file.Width * layout.BytesPerPixel);
    }

    /// <summary>Maps the image resources to metadata and the file to its details, and notes what the load leaves out.</summary>
    internal static RasterMetadata Metadata(EgamiInput input, PsdFile file, ReaderOptions options, bool? withAlpha = null, bool flattened = false)
    {
        bool alpha = withAlpha ?? file.MergedAlpha;
        RasterMetadata metadata = new() { FormatName = PsdFormat.Info.Name };
        PsdDetails details = new()
        {
            Version = file.Version,
            ColorMode = file.Mode,
            BitsPerChannel = file.Depth,
            ChannelCount = file.Channels,
            Compression = file.Compression,
            HasRealComposite = file.HasRealComposite,
            Layers = file.Layers,
            ColorModeData = file.Mode == PsdColorMode.Indexed ? default : file.ColorData,
        };
        int extraStart = file.ColorChannels + (file.MergedAlpha ? 1 : 0);
        int named = 0;
        for (int c = extraStart; c < file.Channels; c++)
        {
            details.ExtraChannels.Add(named < file.AlphaNames.Count ? file.AlphaNames[named] : $"Alpha {named + 1}");
            named++;
        }

        metadata.FormatDetails = details;
        metadata.WarningList.AddRange(file.Warnings);
        if (!file.HasRealComposite && !flattened)
        {
            metadata.WarningList.Add("Psd.NoComposite", "The file was saved without Maximize Compatibility, so it holds no real flattened image; the stored placeholder was loaded. PsdComposite.Auto or Flatten flattens the layers instead.");
        }

        if (file.Mode == PsdColorMode.Multichannel && file.Channels > 1)
        {
            metadata.WarningList.Add("Psd.ChannelsDropped", $"A multichannel file has no color model; its first channel was loaded as gray and the other {file.Channels - 1} were not loaded.");
        }

        bool converted = file.Mode == PsdColorMode.Lab || (file.Mode == PsdColorMode.Cmyk && alpha);
        if (file.Depth == 32)
        {
            metadata.ColorSpace = ColorSpaceHint.Linear;
        }

        foreach ((int id, string name, byte[] data) in file.Resources)
        {
            switch (id)
            {
                case 1039 when !converted:
                    metadata.IccProfile = data;
                    metadata.ColorSpace = ColorSpaceHint.EmbeddedIcc;
                    break;
                case 1005 when data.Length >= 16:
                    double x = BinaryPrimitives.ReadUInt32BigEndian(data) / 65536.0;
                    double y = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8)) / 65536.0;
                    if (x > 0 && y > 0)
                    {
                        metadata.Resolution = new Resolution(x, y, ResolutionUnit.PerInch);
                    }

                    break;
                case 1058:
                    metadata.Exif = Jpeg.JpegSegmentReader.ParseExif(data, input.Limits.MaxMetadataBytes, metadata.WarningList);
                    break;
                case 1060:
                    metadata.Xmp = data;
                    break;
                case 1028:
                    metadata.SetIptcSource(data, input.Limits.MaxMetadataBytes);
                    break;
                default:
                    details.OtherResources.Add(new PsdResource(id, name, data));
                    break;
            }
        }

        if (converted)
        {
            metadata.ColorSpace = ColorSpaceHint.Srgb;
        }

        return metadata;
    }

    /// <summary>Reads every row in the natural pixel type.</summary>
    private sealed class NaturalRead(EgamiInput input, RowSource rows) : INaturalTypeVisitor<Raster>
    {
        public Raster Visit<TPixel>()
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            return ReadAll<TPixel>(input, rows);
        }
    }

    /// <summary>The flattened image's rows in <typeparamref name="TNatural"/>, each gathered from the channel planes as it is asked for.</summary>
    private sealed class PsdRows<TNatural> : RowSource
        where TNatural : unmanaged, IPixelType<TNatural>
    {
        private readonly EgamiInput _input;
        private readonly PsdFile _file;
        private readonly PsdPlanes _planes;
        private readonly bool _ownsInput;
        private readonly byte[][] _channelRows;
        private readonly TNatural[] _natural;
        private readonly byte[] _palette = [];
        private int _row = -1;
        private bool _started;

        public PsdRows(EgamiInput input, PsdFile file, PsdPlanes planes, RasterMetadata metadata, bool ownsInput)
        {
            _input = input;
            _file = file;
            _planes = planes;
            _ownsInput = ownsInput;
            Metadata = metadata;
            List<int> needed = Needed(file);
            _channelRows = new byte[needed.Count][];
            for (int i = 0; i < needed.Count; i++)
            {
                _channelRows[i] = ArrayPool<byte>.Shared.Rent(planes.RowBytes);
            }

            _natural = ArrayPool<TNatural>.Shared.Rent(file.Width);
            if (file.Mode == PsdColorMode.Indexed)
            {
                _palette = file.ColorData;
            }
        }

        public override int StoredWidth => _file.Width;

        public override int StoredHeight => _file.Height;

        public override int NativeScale => 1;

        public override int MaxNativeScale => 1;

        public override PixelLayoutInfo Layout => TNatural.Layout;

        public override RasterMetadata Metadata { get; }

        public override int? FrameCount => 1;

        public override RasterAccess Access => RasterAccess.Streamed;

        public override long WorkingBytes => _planes.WorkingBytes + RowsWorkingBytes(_file, TNatural.Layout);

        public override TResult WithNaturalType<TResult>(INaturalTypeVisitor<TResult> visitor) => visitor.Visit<TNatural>();

        public override FrameMetadata? NextFrame()
        {
            if (_started)
            {
                return null;
            }

            _started = true;
            return new FrameMetadata();
        }

        public override void ReadRow<TPixel>(int y, int x, Span<TPixel> destination)
        {
            if (y != _row)
            {
                Gather(y);
                _row = y;
            }

            ReadOnlySpan<TNatural> natural = _natural.AsSpan(x, destination.Length);
            if (typeof(TPixel) == typeof(TNatural))
            {
                natural.CopyTo(MemoryMarshal.Cast<TPixel, TNatural>(destination));
            }
            else
            {
                PixelConvert.Convert(natural, destination);
            }
        }

        /// <summary>Reads row <paramref name="y"/> of every needed channel and converts it into the natural row.</summary>
        private void Gather(int y)
        {
            List<int> needed = Needed(_file);
            for (int i = 0; i < needed.Count; i++)
            {
                _planes.ReadRow(needed[i], y, _channelRows[i]);
            }

            Span<byte> natural = MemoryMarshal.AsBytes(_natural.AsSpan(0, _file.Width));
            switch (_file.Depth)
            {
                case 1:
                    Bitmap(natural);
                    break;
                case 8:
                    Convert8(natural);
                    break;
                case 16:
                    Convert16(MemoryMarshal.Cast<byte, ushort>(natural));
                    break;
                default:
                    Convert32(MemoryMarshal.Cast<byte, float>(natural));
                    break;
            }
        }

        private void Bitmap(Span<byte> gray)
        {
            ReadOnlySpan<byte> bits = _channelRows[0];
            for (int x = 0; x < gray.Length; x++)
            {
                gray[x] = (bits[x >> 3] & (0x80 >> (x & 7))) != 0 ? (byte)0 : (byte)255;
            }
        }

        private void Convert8(Span<byte> natural)
        {
            int width = _file.Width;
            byte[][] rows = _channelRows;
            bool alpha = _file.MergedAlpha;
            int colors = _file.ColorChannels;
            switch (_file.Mode)
            {
                case PsdColorMode.Indexed:
                    {
                        bool withAlpha = typeof(TNatural) == typeof(Rgba8);
                        int step = withAlpha ? 4 : 3;
                        for (int x = 0; x < width; x++)
                        {
                            int index = rows[0][x];
                            natural[x * step] = _palette[index];
                            natural[(x * step) + 1] = _palette[256 + index];
                            natural[(x * step) + 2] = _palette[512 + index];
                            if (withAlpha)
                            {
                                natural[(x * step) + 3] = rows.Length > 1 ? rows[1][x] : index == _file.TransparentIndex ? (byte)0 : (byte)255;
                            }
                        }

                        return;
                    }

                case PsdColorMode.Cmyk when !alpha:
                    for (int x = 0; x < width; x++)
                    {
                        natural[4 * x] = (byte)~rows[0][x];
                        natural[(4 * x) + 1] = (byte)~rows[1][x];
                        natural[(4 * x) + 2] = (byte)~rows[2][x];
                        natural[(4 * x) + 3] = (byte)~rows[3][x];
                    }

                    return;
                case PsdColorMode.Cmyk:
                    for (int x = 0; x < width; x++)
                    {
                        int a = rows[4][x];
                        int k = Unblend8(rows[3][x], a, 255);
                        natural[4 * x] = (byte)((Unblend8(rows[0][x], a, 255) * k + 127) / 255);
                        natural[(4 * x) + 1] = (byte)((Unblend8(rows[1][x], a, 255) * k + 127) / 255);
                        natural[(4 * x) + 2] = (byte)((Unblend8(rows[2][x], a, 255) * k + 127) / 255);
                        natural[(4 * x) + 3] = (byte)a;
                    }

                    return;
                case PsdColorMode.Lab:
                    {
                        int step = alpha ? 4 : 3;
                        for (int x = 0; x < width; x++)
                        {
                            int a = alpha ? rows[3][x] : 255;
                            double l = (alpha ? Unblend8(rows[0][x], a, 255) : rows[0][x]) * 100.0 / 255;
                            double ca = (alpha ? Unblend8(rows[1][x], a, 128) : rows[1][x]) - 128.0;
                            double cb = (alpha ? Unblend8(rows[2][x], a, 128) : rows[2][x]) - 128.0;
                            (double r, double g, double b) = Lab(l, ca, cb);
                            natural[x * step] = Unorm8(r);
                            natural[(x * step) + 1] = Unorm8(g);
                            natural[(x * step) + 2] = Unorm8(b);
                            if (alpha)
                            {
                                natural[(x * step) + 3] = (byte)a;
                            }
                        }

                        return;
                    }
            }

            if (!alpha)
            {
                if (colors == 1)
                {
                    rows[0].AsSpan(0, width).CopyTo(natural);
                    return;
                }

                for (int x = 0; x < width; x++)
                {
                    natural[3 * x] = rows[0][x];
                    natural[(3 * x) + 1] = rows[1][x];
                    natural[(3 * x) + 2] = rows[2][x];
                }

                return;
            }

            int pixel = colors + 1;
            byte[] alphaRow = rows[colors];
            for (int x = 0; x < width; x++)
            {
                int a = alphaRow[x];
                for (int c = 0; c < colors; c++)
                {
                    natural[(x * pixel) + c] = (byte)Unblend8(rows[c][x], a, 255);
                }

                natural[(x * pixel) + colors] = (byte)a;
            }
        }

        private void Convert16(Span<ushort> natural)
        {
            int width = _file.Width;
            byte[][] rows = _channelRows;
            bool alpha = _file.MergedAlpha;
            int colors = _file.ColorChannels;
            switch (_file.Mode)
            {
                case PsdColorMode.Cmyk when !alpha:
                    for (int x = 0; x < width; x++)
                    {
                        for (int c = 0; c < 4; c++)
                        {
                            natural[(4 * x) + c] = (ushort)~Sample16(rows[c], x);
                        }
                    }

                    return;
                case PsdColorMode.Cmyk:
                    for (int x = 0; x < width; x++)
                    {
                        int a = Sample16(rows[4], x);
                        long k = Unblend16(Sample16(rows[3], x), a, 65535);
                        for (int c = 0; c < 3; c++)
                        {
                            natural[(4 * x) + c] = (ushort)(((Unblend16(Sample16(rows[c], x), a, 65535) * k) + 32767) / 65535);
                        }

                        natural[(4 * x) + 3] = (ushort)a;
                    }

                    return;
                case PsdColorMode.Lab:
                    {
                        int step = alpha ? 4 : 3;
                        for (int x = 0; x < width; x++)
                        {
                            int a = alpha ? Sample16(rows[3], x) : 65535;
                            double l = (alpha ? Unblend16(Sample16(rows[0], x), a, 65535) : Sample16(rows[0], x)) * 100.0 / 65535;
                            double ca = ((alpha ? Unblend16(Sample16(rows[1], x), a, 32768) : Sample16(rows[1], x)) - 32768.0) / 256;
                            double cb = ((alpha ? Unblend16(Sample16(rows[2], x), a, 32768) : Sample16(rows[2], x)) - 32768.0) / 256;
                            (double r, double g, double b) = Lab(l, ca, cb);
                            natural[x * step] = Unorm16(r);
                            natural[(x * step) + 1] = Unorm16(g);
                            natural[(x * step) + 2] = Unorm16(b);
                            if (alpha)
                            {
                                natural[(x * step) + 3] = (ushort)a;
                            }
                        }

                        return;
                    }
            }

            int pixel = colors + (alpha ? 1 : 0);
            for (int x = 0; x < width; x++)
            {
                int a = alpha ? Sample16(rows[colors], x) : 65535;
                for (int c = 0; c < colors; c++)
                {
                    int s = Sample16(rows[c], x);
                    natural[(x * pixel) + c] = (ushort)(alpha ? Unblend16(s, a, 65535) : s);
                }

                if (alpha)
                {
                    natural[(x * pixel) + colors] = (ushort)a;
                }
            }
        }

        private void Convert32(Span<float> natural)
        {
            int width = _file.Width;
            byte[][] rows = _channelRows;
            bool alpha = _file.MergedAlpha;
            int colors = _file.ColorChannels;
            for (int x = 0; x < width; x++)
            {
                float a = alpha ? Sample32(rows[colors], x) : 1;
                if (alpha)
                {
                    // Gray with alpha loads as RGBA, the gray in all three.
                    for (int c = 0; c < 3; c++)
                    {
                        double s = Sample32(rows[colors == 1 ? 0 : c], x);
                        natural[(4 * x) + c] = a > 0 ? (float)((s - (1 - (double)a)) / a) : 0;
                    }

                    natural[(4 * x) + 3] = a;
                    continue;
                }

                for (int c = 0; c < colors; c++)
                {
                    natural[(x * colors) + c] = Sample32(rows[c], x);
                }
            }
        }

        private static int Sample16(byte[] row, int x) => BinaryPrimitives.ReadUInt16BigEndian(row.AsSpan(2 * x));

        private static float Sample32(byte[] row, int x) => BinaryPrimitives.ReadSingleBigEndian(row.AsSpan(4 * x));

        /// <summary>Divides the white Photoshop flattened a transparent pixel onto back out of an 8-bit sample: (s − (1 − a)·white) / a.</summary>
        private static int Unblend8(int stored, int alpha, int white)
        {
            if (alpha == 255)
            {
                return stored;
            }

            if (alpha == 0)
            {
                return 0;
            }

            // (255·s − (255 − a)·white) / a, rounded half up in exact integers.
            int numerator = (255 * stored) - ((255 - alpha) * white);
            return (int)Math.Clamp(FloorDivide((2L * numerator) + alpha, 2L * alpha), 0, 255);
        }

        private static long Unblend16(int stored, int alpha, int white)
        {
            if (alpha == 65535)
            {
                return stored;
            }

            if (alpha == 0)
            {
                return 0;
            }

            long numerator = (65535L * stored) - ((65535L - alpha) * white);
            return Math.Clamp(FloorDivide((2 * numerator) + alpha, 2L * alpha), 0, 65535);
        }

        private static long FloorDivide(long numerator, long denominator) => numerator >= 0 ? numerator / denominator : -((-numerator + denominator - 1) / denominator);

        private static (double R, double G, double B) Lab(double l, double a, double b) => PsdLab.ToSrgb(l, a, b);

        private static byte Unorm8(double v) => PsdLab.Unorm8(v);

        private static ushort Unorm16(double v) => PsdLab.Unorm16(v);

        protected override void Dispose(bool disposing)
        {
            foreach (byte[] row in _channelRows)
            {
                ArrayPool<byte>.Shared.Return(row);
            }

            ArrayPool<TNatural>.Shared.Return(_natural);
            _planes.Dispose();
            if (_ownsInput)
            {
                _input.ReturnUnreadBytes();
                _input.Dispose();
            }
        }
    }
}
