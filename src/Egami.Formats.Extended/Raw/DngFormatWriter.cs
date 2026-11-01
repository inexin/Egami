using Egami.Pixels;

namespace Egami.Formats.Extended.Raw;

/// <summary>
/// The DNG writer registered on <see cref="RawFormat.Info"/>: a raster saves as a CFA DNG when it is a RAW mosaic with its
/// details, else as LinearRaw; a RAW file transcoded to DNG with no operations is converted with its sensor data kept.
/// </summary>
internal sealed class DngFormatWriter : IFormatWriter, IFormatConverter
{
    public void Write<TPixel>(Raster<TPixel> raster, Stream output, WriterOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        DngWriterOptions dng = options as DngWriterOptions ?? DngWriterOptions.Default;
        EgamiContext context = EgamiContext.Default;
        RawWarnings warnings = new();
        using EgamiInput input = new(Stream.Null, context, CancellationToken.None);
        using (DngContent content = DngSources.FromRaster(raster, input, warnings))
        {
            DngWriter.Write(output, content, dng, context, warnings);
        }

        Report(context, warnings);
    }

    public bool TryConvert(Stream input, FormatInfo inputFormat, Stream output, WriterOptions options, EgamiContext context)
    {
        if (inputFormat != RawFormat.Info)
        {
            return false;
        }

        Convert(input, output, options as DngWriterOptions ?? DngWriterOptions.Default, context);
        return true;
    }

    /// <summary>Reads a RAW file and writes it as DNG, its sensor data as decoded.</summary>
    internal static void Convert(Stream input, Stream output, DngWriterOptions options, EgamiContext context)
    {
        RawWarnings warnings = new();
        using EgamiInput egami = new(input, context, CancellationToken.None);
        using RawFile file = RawFile.Open(egami);
        using (DngContent content = DngSources.FromFile(file, egami, warnings))
        {
            DngWriter.Write(output, content, options, context, warnings);
        }

        Report(context, warnings);
    }

    private static void Report(EgamiContext context, RawWarnings warnings)
    {
        using DiagnosticReporter reporter = context.StartDiagnostics();
        foreach ((string id, string message) in warnings.List)
        {
            reporter.Report(DiagnosticLevel.Warning, $"Raw.{id}", message);
        }
    }
}
