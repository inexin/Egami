using Egami.Formats.Heif;
using Egami.Pixels;

namespace Egami.Formats.Avif;

/// <summary>
/// Reads AVIF (AV1 Image File Format 1.1 over HEIF), written from the specifications. This stage reads the container and the
/// headers: Probe gives the size after clap, the natural pixel type, irot and imir as the orientation, and a memory estimate.
/// Decoding pixels comes with the AV1 decoder.
/// </summary>
internal sealed class AvifReader : IFormatReader
{
    internal static readonly AvifReader Instance = new();

    public RasterInfo ReadInfo(EgamiInput input, ReaderOptions options)
    {
        AvifFile file = AvifFile.Read(input);
        if (input.Reports(DiagnosticLevel.Warning))
        {
            // A probe has no raster to list warnings on, so skipped entries go to the handler directly.
            foreach (HeifProblem problem in file.Heif.Problems)
            {
                input.Diagnostics.Report(DiagnosticLevel.Warning, problem.Id, problem.Message, problem.Offset);
            }
        }

        (int width, int height) = file.Crop is (_, _, int w, int h) ? (w, h) : (file.Width, file.Height);
        input.Limits.CheckFrameSize(width, height);
        PixelLayoutInfo layout = NaturalLayout(file.Config.BitDepth, file.Config.Monochrome, file.Alpha is not null);
        return new RasterInfo
        {
            Format = AvifFormat.Info,
            Width = width,
            Height = height,
            Layout = layout,
            FrameCount = 1,
            ImageCount = 1,
            HasMetadata = !file.Icc.IsEmpty || file.ExifItems.Count > 0 || file.XmpItems.Count > 0 || file.ContentLightLevel is not null
                || file.MasteringDisplay is not null,
            EstimatedBytes = (long)width * height * layout.BytesPerPixel,
            Orientation = file.Orientation,
        };
    }

    public Raster Read(EgamiInput input, ReaderOptions options) => throw NotYet(input);

    public Raster<TPixel> Read<TPixel>(EgamiInput input, ReaderOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        throw NotYet(input);
    }

    /// <summary>
    /// Gets the natural pixel layout (design question 7): 8-bit types for 8-bit files and 16-bit ones for 10 and 12 bits; gray for
    /// monochrome; with alpha when there is an alpha item.
    /// </summary>
    internal static PixelLayoutInfo NaturalLayout(int bitDepth, bool monochrome, bool alpha)
    {
        return bitDepth <= 8
            ? (monochrome ? (alpha ? GrayAlpha8.Layout : Gray8.Layout) : alpha ? Rgba8.Layout : Rgb8.Layout)
            : (monochrome ? (alpha ? GrayAlpha16.Layout : Gray16.Layout) : alpha ? Rgba16.Layout : Rgb16.Layout);
    }

    private static UnsupportedFormatException NotYet(EgamiInput input)
    {
        // The structure is read first, so data that is not AVIF, or is broken, fails as it would in a full decode.
        AvifFile.Read(input);
        return new UnsupportedFormatException("Egami reads AVIF headers (Raster.Probe) but does not decode AVIF pixels yet.");
    }
}
