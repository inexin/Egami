using Egami.Colors;
using Egami.Formats.Jpeg;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Extended.Raw;

/// <summary>
/// Reads camera RAW files. Every output needs the whole file in memory (sensor data is read out of order and demosaicing needs
/// neighbouring rows), so the image is produced whole and a streaming decode hands out its rows.
/// </summary>
internal sealed class RawReader : IFormatReader, IRowFormatReader, IMultiImageReader
{
    public RasterInfo ReadInfo(EgamiInput input, ReaderOptions options)
    {
        using RawFile file = RawFile.Open(input);
        Plan plan = Plan.Choose(file, options, input);
        int orientation = file.Frame.Orientation;
        return new RasterInfo
        {
            Format = RawFormat.Info,
            Width = plan.Width,
            Height = plan.Height,
            Layout = plan.Layout,
            FrameCount = 1,
            ImageCount = 1 + file.Previews.Count,
            EstimatedBytes = (long)plan.Width * plan.Height * plan.Layout.BytesPerPixel,
            Access = RasterAccess.Buffered,
            WorkingBytes = plan.WorkingBytes,
            HasMetadata = true,
            Orientation = orientation is >= 1 and <= 8 ? (ImageOrientation)orientation : ImageOrientation.Normal,
        };
    }

    public Raster<TPixel> Read<TPixel>(EgamiInput input, ReaderOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using RawFile file = RawFile.Open(input);
        return Load<TPixel>(file, Plan.Choose(file, options, input), options, input);
    }

    public Raster Read(EgamiInput input, ReaderOptions options)
    {
        using RawFile file = RawFile.Open(input);
        Plan plan = Plan.Choose(file, options, input);
        if (plan.Output != RawOutput.Mosaic && options.ConvertColorTo is { Model: not ColorModel.Rgb } other)
        {
            // Converted to another model: its type at the depth the image has, as Egami's own conversion on load picks.
            bool wide = plan.Natural != Natural.Rgb8 && plan.Natural != Natural.Gray8;
            return (other.Model, wide) switch
            {
                (ColorModel.Gray, true) => Load<Gray16>(file, plan, options, input),
                (ColorModel.Gray, false) => Load<Gray8>(file, plan, options, input),
                (ColorModel.Cmyk, true) => Load<Cmyk16>(file, plan, options, input),
                _ => Load<Cmyk8>(file, plan, options, input),
            };
        }

        return plan.Natural switch
        {
            Natural.Gray16 => Load<Gray16>(file, plan, options, input),
            Natural.GrayF32 => Load<GrayF32>(file, plan, options, input),
            Natural.RgbF32 => Load<RgbF32>(file, plan, options, input),
            Natural.Rgb8 => Load<Rgb8>(file, plan, options, input),
            Natural.Gray8 => Load<Gray8>(file, plan, options, input),
            _ => Load<Rgb16>(file, plan, options, input),
        };
    }

    public RowReader Open(EgamiInput input, ReaderOptions options)
    {
        RawFile file = RawFile.Open(input);
        try
        {
            // The rows own the file from here, and give it back when they are disposed.
            Plan plan = Plan.Choose(file, options, input);
            return plan.Natural switch
            {
                Natural.Gray16 => new Rows<Gray16>(file, plan, options, input),
                Natural.GrayF32 => new Rows<GrayF32>(file, plan, options, input),
                Natural.RgbF32 => new Rows<RgbF32>(file, plan, options, input),
                Natural.Rgb8 => new Rows<Rgb8>(file, plan, options, input),
                Natural.Gray8 => new Rows<Gray8>(file, plan, options, input),
                _ => new Rows<Rgb16>(file, plan, options, input),
            };
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    public IReadOnlyList<ReaderOptions> Images(EgamiInput input, ReaderOptions options)
    {
        using RawFile file = RawFile.Open(input);
        RawReaderOptions raw = Raw(options);
        List<ReaderOptions> list = [raw with { Output = raw.Output == RawOutput.Preview ? RawOutput.Developed : raw.Output, PreviewIndex = null }];
        for (int i = 0; i < file.Previews.Count; i++)
        {
            list.Add(raw with { Output = RawOutput.Preview, PreviewIndex = i });
        }

        return list;
    }

    /// <summary>Returns the options as RAW options, the general ones copied over.</summary>
    internal static RawReaderOptions Raw(ReaderOptions options)
    {
        return options as RawReaderOptions ?? new RawReaderOptions
        {
            Recovery = options.Recovery,
            ReadMetadata = options.ReadMetadata,
            ConvertColorTo = options.ConvertColorTo,
            Frames = options.Frames,
            MinimumSize = options.MinimumSize,
            Region = options.Region,
        };
    }

    /// <summary>
    /// Loads the plan's output. <paramref name="convert"/> converts to a <see cref="ReaderOptions.ConvertColorTo"/> the develop
    /// cannot render into; a streaming decode leaves that to the decoder, which converts from what the rows' metadata says.
    /// </summary>
    private static Raster<TPixel> Load<TPixel>(RawFile file, Plan plan, ReaderOptions options, EgamiInput input, bool convert = true)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        try
        {
            return LoadCore<TPixel>(file, plan, options, input, convert);
        }
        catch (InvalidDataException e)
        {
            throw input.Corrupt(0, e.Message);
        }
    }

    private static Raster<TPixel> LoadCore<TPixel>(RawFile file, Plan plan, ReaderOptions options, EgamiInput input, bool convert)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        RawFrame frame = file.Frame;
        bool strict = options.Recovery == RecoveryMode.Strict;
        RawWarnings warnings = new();
        foreach ((string id, string message) in frame.Repairs)
        {
            warnings.Add(id, message);
        }

        Raster<TPixel> raster;
        RawCamera? camera = null;
        string? lensProfile = null;
        switch (plan.Output)
        {
            case RawOutput.Preview:
                raster = Preview<TPixel>(file, plan.Preview!, input, strict, warnings);
                break;
            case RawOutput.Mosaic:
                raster = Mosaic<TPixel>(file, input, strict, warnings);
                break;
            default:
                {
                    using RawPixels pixels = Pixels(file, frame.Sensor, input, strict, warnings);
                    MeasureBlack(frame, pixels);
                    ColorProfile space = RenderSpace(options) ?? ColorProfile.Srgb;
                    RawDeveloper developer = new(frame, Raw(options), plan.Output == RawOutput.HalfSize, space, warnings);
                    raster = input.CreateRaster<TPixel>(plan.Width, plan.Height);
                    try
                    {
                        Raster<TPixel> target = raster;
                        developer.Run(pixels, input, (y, row) => PixelConvert.Convert(row, target.Row(y)));
                        camera = developer.Camera;
                        lensProfile = developer.LensProfile;
                        Describe(raster.Metadata, space);
                    }
                    catch
                    {
                        raster.Dispose();
                        throw;
                    }

                    break;
                }
        }

        // A colour space the develop cannot render into (CMYK, gray, HDR) is converted to here, as Egami would after any load.
        if (convert && plan.Output != RawOutput.Mosaic && options.ConvertColorTo is ColorProfile convertTo && (plan.Output == RawOutput.Preview || RenderSpace(options) is null))
        {
            Raster<TPixel> stored = raster;
            using (stored)
            {
                raster = stored.ConvertColor<TPixel>(convertTo);
            }
        }

        RasterMetadata metadata = raster.Metadata;
        RawDetails details = Details(file, plan, camera);
        details.LensProfile = lensProfile;
        metadata.FormatDetails = details;
        if (options.ReadMetadata)
        {
            file.ReadMetadata(metadata, input);
        }

        foreach ((string id, string message) in warnings.List)
        {
            input.Warn(raster, $"Raw.{id}", message);
        }

        return raster;
    }

    /// <summary>Returns the RGB colour space the develop renders into for the options, or <see langword="null"/> for sRGB.</summary>
    internal static ColorProfile? RenderSpace(ReaderOptions options)
    {
        return options.ConvertColorTo is { Model: ColorModel.Rgb, Primaries: not null, Transfer.IsHdr: false } target ? target : null;
    }

    /// <summary>Records the colour space a develop rendered into.</summary>
    private static void Describe(RasterMetadata metadata, ColorProfile space)
    {
        if (space.IsEquivalentTo(ColorProfile.Srgb))
        {
            metadata.ColorSpace = ColorSpaceHint.Srgb;
            return;
        }

        metadata.IccProfile = space.ToIcc();
        metadata.ColorSpace = ColorSpaceHint.EmbeddedIcc;
    }

    /// <summary>
    /// Measures the black level from masked sensor pixels, per position of the 2 × 2 pattern, when the file and the camera
    /// table give none (some converters' DNGs, which the table comes from, hold black already subtracted).
    /// </summary>
    private static void MeasureBlack(RawFrame frame, RawPixels pixels)
    {
        if (frame.MaskedArea is not Rect area || frame.HasOwnLevels || frame.Black.Average() > 1 || pixels.IsFloat || area.Width < 4 || area.Height < 4)
        {
            return;
        }

        double[] sum = new double[4];
        long[] count = new long[4];
        for (int y = area.Y; y < Math.Min(area.Bottom, pixels.Height); y++)
        {
            ReadOnlySpan<ushort> row = pixels.U16Row(y);
            for (int x = area.X; x < Math.Min(area.Right, pixels.Width); x++)
            {
                int p = ((y & 1) << 1) | (x & 1);
                sum[p] += row[x];
                count[p]++;
            }
        }

        if (count.All(c => c > 0))
        {
            // The pattern of the measurement is anchored at the sensor's origin; the black pattern at the active area's.
            int ox = frame.Active.X & 1, oy = frame.Active.Y & 1;
            frame.BlackRows = frame.BlackColumns = 2;
            frame.Black = [.. Enumerable.Range(0, 4).Select(i => sum[(((i >> 1) ^ oy) << 1) | ((i & 1) ^ ox)] / count[(((i >> 1) ^ oy) << 1) | ((i & 1) ^ ox)])];
        }
    }

    /// <summary>Reads and decodes the sensor data; damage throws when strict, else is noted.</summary>
    private static RawPixels Pixels(RawFile file, RawSensor sensor, EgamiInput input, bool strict, RawWarnings warnings)
    {
        long end = 0;
        int units = Math.Min(sensor.Offsets.Length, sensor.Counts.Length);
        for (int u = 0; u < units; u++)
        {
            end = Math.Max(end, file.Tiff.Origin + sensor.Offsets[u] + sensor.Counts[u]);
        }

        if (sensor.Counts.Length == 0 || sensor.Counts.Any(c => c <= 0))
        {
            file.Source.ReadAll();
        }
        else
        {
            file.Source.Ensure(end);
        }
        return RawDecoder.Decode(sensor, file.Tiff, input, (unit, message) =>
        {
            if (strict)
            {
                throw input.Corrupt(unit >= 0 && unit < units ? sensor.Offsets[unit] : 0, message);
            }

            warnings.Add("DataDamaged", message);
        });
    }

    private static Raster<TPixel> Mosaic<TPixel>(RawFile file, EgamiInput input, bool strict, RawWarnings warnings)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        RawSensor sensor = file.Frame.Sensor;
        using RawPixels pixels = Pixels(file, sensor, input, strict, warnings);
        Raster<TPixel> raster = input.CreateRaster<TPixel>(sensor.Width, sensor.Height);
        try
        {
            int samples = pixels.Samples;
            for (int y = 0; y < sensor.Height; y++)
            {
                Span<TPixel> target = raster.Row(y);
                if (pixels.IsFloat)
                {
                    Span<float> row = pixels.F32Row(y);
                    if (samples == 1)
                    {
                        PixelConvert.Convert<GrayF32, TPixel>(System.Runtime.InteropServices.MemoryMarshal.Cast<float, GrayF32>(row), target);
                    }
                    else
                    {
                        PixelConvert.Convert<RgbF32, TPixel>(System.Runtime.InteropServices.MemoryMarshal.Cast<float, RgbF32>(row), target);
                    }
                }
                else if (samples == 1)
                {
                    PixelConvert.Convert<Gray16, TPixel>(System.Runtime.InteropServices.MemoryMarshal.Cast<ushort, Gray16>(pixels.U16Row(y)), target);
                }
                else
                {
                    PixelConvert.Convert<Rgb16, TPixel>(System.Runtime.InteropServices.MemoryMarshal.Cast<ushort, Rgb16>(pixels.U16Row(y)), target);
                }
            }

            raster.Metadata.ColorSpace = ColorSpaceHint.Linear;
            return raster;
        }
        catch
        {
            raster.Dispose();
            throw;
        }
    }

    private static Raster<TPixel> Preview<TPixel>(RawFile file, RawPreviewSource preview, EgamiInput input, bool strict, RawWarnings warnings)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (preview.Jpeg)
        {
            if (!file.Source.Ensure(preview.Offset + preview.Length))
            {
                throw input.Corrupt(preview.Offset, "The preview runs past the end of the file.");
            }

            ReadOnlyMemory<byte> bytes = file.Source.Data.AsMemory((int)preview.Offset, (int)preview.Length);
            EgamiContext context = input.Context with { Formats = [JpegFormat.Info] };
            Raster<TPixel> jpeg = Raster.Load<TPixel>(bytes, JpegFormat.Info, context, new ReaderOptions { Recovery = strict ? RecoveryMode.Strict : RecoveryMode.Lenient });
            jpeg.Metadata.Exif = null;
            return jpeg;
        }

        RawSensor sensor = DngParser.Sensor(preview.Ifd!);
        using RawPixels pixels = Pixels(file, sensor, input, strict, warnings);
        Raster<TPixel> raster = input.CreateRaster<TPixel>(sensor.Width, sensor.Height);
        Span<Rgb8> rgb = new Rgb8[sensor.Width];
        Span<Gray8> gray = new Gray8[sensor.Width];
        for (int y = 0; y < sensor.Height; y++)
        {
            Span<ushort> row = pixels.U16Row(y);
            if (sensor.Samples == 3)
            {
                for (int x = 0; x < sensor.Width; x++)
                {
                    rgb[x] = new Rgb8((byte)row[x * 3], (byte)row[(x * 3) + 1], (byte)row[(x * 3) + 2]);
                }

                PixelConvert.Convert<Rgb8, TPixel>(rgb, raster.Row(y));
            }
            else
            {
                for (int x = 0; x < sensor.Width; x++)
                {
                    gray[x] = new Gray8((byte)row[x]);
                }

                PixelConvert.Convert<Gray8, TPixel>(gray, raster.Row(y));
            }
        }

        raster.Metadata.ColorSpace = ColorSpaceHint.Srgb;
        return raster;
    }

    private static RawDetails Details(RawFile file, Plan plan, RawCamera? camera)
    {
        RawFrame f = file.Frame;
        RawSensor s = f.Sensor;
        (double cx, double cy, double cw, double ch) = f.Crop;
        List<(int, int)> opcodes = [];
        foreach ((int list, byte[]? bytes) in (ReadOnlySpan<(int, byte[]?)>)[(1, f.Opcodes1), (2, f.Opcodes2), (3, f.Opcodes3)])
        {
            if (bytes is not null)
            {
                opcodes.AddRange(DngOpcodes.Ids(bytes).Select(id => (list, id)));
            }
        }

        RawDetails details = new()
        {
            Output = plan.Output,
            PreviewIndex = plan.Output == RawOutput.Preview ? plan.PreviewIndex : null,
            Make = f.Make,
            Model = f.Model,
            UniqueCameraModel = f.UniqueCameraModel,
            DngVersion = f.DngVersion is { } v ? new Version(v[0], v[1], v[2], v[3]) : null,
            SensorWidth = s.Width,
            SensorHeight = s.Height,
            ActiveArea = f.Active,
            CropArea = new Rect(f.Active.X + (int)Math.Round(cx), f.Active.Y + (int)Math.Round(cy), (int)Math.Round(cw), (int)Math.Round(ch)),
            CfaPattern = f.Cfa?.Name,
            CfaWidth = f.Cfa?.Columns ?? 0,
            CfaHeight = f.Cfa?.Rows ?? 0,
            BitsPerSample = s.Bits,
            Compression = s.Compression,
            BlackLevels = f.Black,
            WhiteLevels = f.White,
            AsShotNeutral = f.AsShotNeutral ?? [],
            ProfileName = f.Profile.Name,
            BaselineExposure = f.BaselineExposure,
            Opcodes = opcodes,
            Previews = [.. file.Previews.Select(p => new RawPreview(p.Width, p.Height, p.Jpeg ? RawPreviewKind.Jpeg : RawPreviewKind.Uncompressed))],
            Iso = f.Iso,
            ExposureTime = f.ExposureTime,
            FNumber = f.FNumber,
            FocalLength = f.FocalLength,
            LensModel = f.LensModel,
            MakerNote = f.MakerNote,
        };
        if (camera is not null)
        {
            details.WhiteBalance = camera.Multipliers;
            details.CameraToXyz = camera.CameraToXyz;
            (details.Temperature, details.Tint) = RawColor.Temperature(camera.White);
        }

        return details;
    }

    /// <summary>The natural pixel types a RAW load gives.</summary>
    private enum Natural
    {
        Rgb16,
        Gray16,
        GrayF32,
        RgbF32,
        Rgb8,
        Gray8,
    }

    /// <summary>What a load produces: the output, its size and natural type, and the preview it is, if any.</summary>
    private sealed record Plan(RawOutput Output, int Width, int Height, Natural Natural, RawPreviewSource? Preview, int PreviewIndex, long WorkingBytes)
    {
        public PixelLayoutInfo Layout => Natural switch
        {
            Natural.Gray16 => Gray16.Layout,
            Natural.GrayF32 => GrayF32.Layout,
            Natural.RgbF32 => RgbF32.Layout,
            Natural.Rgb8 => Rgb8.Layout,
            Natural.Gray8 => Gray8.Layout,
            _ => Rgb16.Layout,
        };

        public static Plan Choose(RawFile file, ReaderOptions options, EgamiInput input)
        {
            RawReaderOptions raw = Raw(options);
            RawFrame frame = file.Frame;
            RawSensor sensor = frame.Sensor;
            List<RawPreviewSource> previews = file.Previews;
            long sensorBytes = (long)sensor.Width * sensor.Height * sensor.Samples * (sensor.Float ? 4 : 2);
            RawOutput output = raw.Output;
            int previewIndex = raw.PreviewIndex ?? 0;
            if (output == RawOutput.Developed && options.MinimumSize is Size minimum && options.Region is null)
            {
                for (int i = previews.Count - 1; i >= 0; i--)
                {
                    if (previews[i].Width >= minimum.Width && previews[i].Height >= minimum.Height)
                    {
                        output = RawOutput.Preview;
                        previewIndex = i;
                        break;
                    }
                }

                if (output == RawOutput.Developed && frame.Cfa is not null)
                {
                    RawDeveloper half = new(frame, raw, true, ColorProfile.Srgb, null);
                    if (half.Output.Width >= minimum.Width && half.Output.Height >= minimum.Height)
                    {
                        output = RawOutput.HalfSize;
                    }
                }
            }

            if (output == RawOutput.Preview)
            {
                if (previewIndex >= previews.Count)
                {
                    if (raw.PreviewIndex is not null)
                    {
                        throw new ArgumentOutOfRangeException(nameof(options), $"Preview {previewIndex} was asked for; the file has {previews.Count}.");
                    }

                    output = RawOutput.HalfSize;
                }
                else
                {
                    RawPreviewSource p = previews[previewIndex];
                    int width = p.Width, height = p.Height;
                    Natural natural = Natural.Rgb8;
                    if (!p.Jpeg && p.Ifd!.Int(RawTags.SamplesPerPixel, 1) == 1)
                    {
                        natural = Natural.Gray8;
                    }
                    else if (p.Jpeg && file.Source.Ensure(p.Offset + p.Length))
                    {
                        ReadOnlyMemory<byte> bytes = file.Source.Data.AsMemory((int)p.Offset, (int)Math.Min(p.Length, 64 * 1024));
                        try
                        {
                            // The JPEG's own size and layout are what a load gives, whatever the IFD says.
                            RasterInfo info = Raster.Probe(bytes, JpegFormat.Info, input.Context with { Formats = [JpegFormat.Info] });
                            natural = info.Layout.ChannelCount == 1 ? Natural.Gray8 : Natural.Rgb8;
                            (width, height) = (info.Width, info.Height);
                        }
                        catch (EgamiException)
                        {
                        }
                    }

                    input.CheckFrameSize(width, height);
                    return new Plan(output, width, height, natural, p, previewIndex, p.Length + ((long)width * height * 3));
                }
            }

            if (output == RawOutput.Mosaic)
            {
                input.CheckFrameSize(sensor.Width, sensor.Height);
                Natural natural = (sensor.Float, sensor.Samples) switch
                {
                    (true, 1) => Natural.GrayF32,
                    (true, _) => Natural.RgbF32,
                    (false, 1) => Natural.Gray16,
                    _ => Natural.Rgb16,
                };
                if (sensor.Samples is not (1 or 3))
                {
                    throw new NotSupportedException($"Egami reads raw data with 1 or 3 samples per pixel, not {sensor.Samples}.");
                }

                return new Plan(output, sensor.Width, sensor.Height, natural, null, 0, sensorBytes);
            }

            if (frame.Planes != 3 || (frame.Cfa is null && sensor.Samples != 3))
            {
                throw new NotSupportedException($"Egami develops raw data with three colours; this file has {frame.Planes}.");
            }

            RawDeveloper developer = new(frame, raw, output == RawOutput.HalfSize, ColorProfile.Srgb, null);
            input.CheckFrameSize(developer.Output.Width, developer.Output.Height);
            long active = (long)frame.Active.Width * frame.Active.Height;
            return new Plan(output, developer.Output.Width, developer.Output.Height, Natural.Rgb16, null, 0, sensorBytes + (active * 4 * sensor.Samples) + (active * 12));
        }
    }

    /// <summary>The rows of a RAW image for a streaming decode: the image is loaded whole on the first row asked for.</summary>
    private sealed class Rows<TNatural> : RowReader<TNatural>
        where TNatural : unmanaged, IPixelType<TNatural>
    {
        private readonly RawFile _file;
        private readonly Plan _plan;
        private readonly ReaderOptions _options;
        private readonly EgamiInput _input;
        private Raster<TNatural>? _raster;
        private bool _started;

        public Rows(RawFile file, Plan plan, ReaderOptions options, EgamiInput input)
        {
            _file = file;
            _plan = plan;
            _options = options;
            _input = input;
            Metadata = new RasterMetadata { FormatName = RawFormat.Info.Name, FormatDetails = Details(file, plan, null) };
            if (plan.Output is RawOutput.Developed or RawOutput.HalfSize)
            {
                // Known before the develop runs, so a streaming decode converts from what the rows will be: nothing when the
                // develop renders the asked-for space, else from sRGB.
                Describe(Metadata, RenderSpace(options) ?? ColorProfile.Srgb);
            }
            else if (plan.Output == RawOutput.Preview)
            {
                Metadata.ColorSpace = ColorSpaceHint.Srgb;
            }
        }

        public override int Width => _plan.Width;

        public override int Height => _plan.Height;

        public override RasterMetadata Metadata { get; }

        public override RasterAccess Access => RasterAccess.Buffered;

        public override long WorkingBytes => _plan.WorkingBytes;

        public override bool IsMetadataComplete => _raster is not null;

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
            if (_raster is null)
            {
                _raster = Load<TNatural>(_file, _plan, _options, _input, convert: false);
                RasterMetadata loaded = _raster.Metadata;
                Metadata.FormatDetails = loaded.FormatDetails?.Copy();
                Metadata.ColorSpace = loaded.ColorSpace;
                Metadata.Exif = loaded.Exif;
                Metadata.Xmp = loaded.Xmp;
                Metadata.IccProfile = loaded.IccProfile;
                foreach (EgamiDiagnostic warning in loaded.Warnings)
                {
                    _input.Warn(Metadata, warning.Id, warning.Message, warning.Offset);
                }
            }

            PixelConvert.Convert<TNatural, TPixel>(_raster.Row(y).Slice(x, destination.Length), destination);
        }

        protected override void Dispose(bool disposing)
        {
            _raster?.Dispose();
            _raster = null;
            _file.Dispose();
            base.Dispose(disposing);
        }
    }
}
