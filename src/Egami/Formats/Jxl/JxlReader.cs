using Egami.Metadata;
using Egami.Pixels;
using Egami.Streaming;

namespace Egami.Formats.Jxl;

/// <summary>
/// Reads JPEG XL (ISO/IEC 18181), written from the specification with libjxl (BSD-3-Clause) and jxl-oxide (MIT or Apache-2.0)
/// read for the header layouts (see THIRD-PARTY-NOTICES). This stage reads the container and the headers: Probe gives size,
/// sample format, channels, orientation, frame count and a memory estimate. Decoding pixels comes with the Modular and VarDCT
/// stages.
/// </summary>
internal sealed class JxlReader : IFormatReader, IRowSourceReader
{
    internal static readonly JxlReader Instance = new();

    public RasterInfo ReadInfo(EgamiInput input, ReaderOptions options)
    {
        JxlFile file = JxlFile.Read(input, allBoxes: true);
        if (input.Reports(DiagnosticLevel.Warning))
        {
            // A probe has no raster to list warnings on, so dropped boxes go to the handler directly.
            foreach (JxlProblem problem in file.Container.Problems)
            {
                input.Diagnostics.Report(DiagnosticLevel.Warning, problem.Id, problem.Message, problem.Offset);
            }
        }

        JxlImageHeader h = file.Header;
        PixelLayoutInfo layout = options.Hdr is not null && file.Container.GainMap is not null ? RgbaF16.Layout : NaturalLayout(h, options is JxlReaderOptions { LinearOutput: true });
        int? frames = file.DisplayedFrames;
        return new RasterInfo
        {
            Format = JxlFormat.Info,
            Width = h.Width,
            Height = h.Height,
            Layout = layout,
            FrameCount = frames,
            ImageCount = 1,
            HasMetadata = h.Color.WantIcc || file.Container.Exif is not null || file.Container.Xmp is not null || file.Container.Jumbf.Count > 0,
            EstimatedBytes = (long)h.Width * h.Height * layout.BytesPerPixel * (frames ?? 1),
            Orientation = (ImageOrientation)h.Orientation,
            LoopCount = h.Animation is JxlAnimation animation ? (int)Math.Min(animation.Loops, int.MaxValue) : null,
        };
    }

    public Raster Read(EgamiInput input, ReaderOptions options) => JxlImageDecoder.Decode(input, options);

    /// <summary>
    /// Decodes the image whole, on several threads, with only the groups a Region touches (design question 10), and serves its
    /// rows from the result; the session crops them.
    /// </summary>
    public RowSource Open(EgamiInput input, ReaderOptions options)
    {
        Raster raster = JxlImageDecoder.Decode(input, options);
        raster.Metadata.FormatName ??= JxlFormat.Info.Name;
        return BufferedRowSource.From(raster);
    }

    public Raster<TPixel> Read<TPixel>(EgamiInput input, ReaderOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        return JxlImageDecoder.Decode<TPixel>(input, options);
    }

    /// <summary>
    /// Gets the natural pixel layout (design question 5): 8-bit types up to 8 bits a sample, 16-bit ones up to 16, half float
    /// for 16-bit float samples and single float beyond; gray or color, with alpha when an alpha channel exists; CMYK when a
    /// black channel exists. Linear output is single float.
    /// </summary>
    internal static PixelLayoutInfo NaturalLayout(JxlImageHeader h, bool linear = false)
    {
        bool alpha = h.AlphaIndex >= 0;
        bool gray = h.ColorChannels == 1;
        JxlBitDepth depth = h.BitDepth;
        if (h.ExtraChannels.Any(c => c.Type == JxlExtraChannelType.Black))
        {
            return depth.BitsPerSample <= 8 && !depth.IsFloat ? Cmyk8.Layout : Cmyk16.Layout;
        }

        if (linear)
        {
            return gray ? GrayF32.Layout : alpha ? RgbaF32.Layout : RgbF32.Layout;
        }

        if (depth.IsFloat || depth.BitsPerSample > 16)
        {
            return depth.IsFloat && depth.BitsPerSample <= 16 && !gray ? RgbaF16.Layout
                : gray ? GrayF32.Layout
                : alpha ? RgbaF32.Layout : RgbF32.Layout;
        }

        return depth.BitsPerSample <= 8
            ? (gray ? (alpha ? GrayAlpha8.Layout : Gray8.Layout) : alpha ? Rgba8.Layout : Rgb8.Layout)
            : (gray ? (alpha ? GrayAlpha16.Layout : Gray16.Layout) : alpha ? Rgba16.Layout : Rgb16.Layout);
    }
}
