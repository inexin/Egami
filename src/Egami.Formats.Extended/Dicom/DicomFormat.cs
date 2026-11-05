using System.Diagnostics.CodeAnalysis;

using Egami.Pixels;

namespace Egami.Formats.Extended.Dicom;

/// <summary>
/// DICOM (PS3.10 files, .dcm): medical images with their dataset. Registered with
/// <see cref="ExtendedContextExtensions.WithDicom"/>; in preview. This version detects DICOM files and probes their size, frame
/// count and dataset; <see cref="DicomFile"/> reads every element. Decoding the pixel data comes in a later version, and until
/// then a load throws <see cref="NotSupportedException"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Dicom.cs" region="dicom-probe"/>
/// </example>
[Experimental(DicomFile.DiagnosticId, UrlFormat = DicomFile.DiagnosticUrl)]
public static class DicomFormat
{
    /// <summary>Gets the format's registration: name, MIME type, extensions, signature and reader.</summary>
    public static FormatInfo Info { get; } = new()
    {
        Name = "DICOM",
        MimeTypes = ["application/dicom"],
        Extensions = ["dcm", "dicom", "dic"],
        SignatureLength = 132,
        Matches = DicomParser.IsPart10,
        Reader = new DicomReader(),
    };
}

/// <summary>Reads the dataset to say what a file holds; pixel data decoding comes in a later version.</summary>
internal sealed class DicomReader : IFormatReader
{
    public RasterInfo ReadInfo(EgamiInput input, ReaderOptions options)
    {
        DicomFile file = DicomFile.Read(input, options);
        DicomDataset data = file.Dataset;
        int width = (int)(Number(data, "Columns") ?? 0);
        int height = (int)(Number(data, "Rows") ?? 0);
        int samples = (int)(Number(data, "SamplesPerPixel") ?? 1);
        int bits = (int)(Number(data, "BitsAllocated") ?? 8);
        int frames = (int)(Number(data, "NumberOfFrames") ?? 1);
        if (width <= 0 || height <= 0)
        {
            throw new UnsupportedFormatException("This DICOM file has no image: its dataset has no Rows and Columns.");
        }

        PixelLayoutInfo layout = samples >= 3 ? (bits > 8 ? Rgb16.Layout : Rgb8.Layout) : Gray16.Layout;
        return new RasterInfo
        {
            Format = DicomFormat.Info,
            Width = width,
            Height = height,
            Layout = layout,
            FrameCount = Math.Max(frames, 1),
            HasMetadata = true,
            EstimatedBytes = (long)width * height * layout.BytesPerPixel,
        };
    }

    public Raster<TPixel> Read<TPixel>(EgamiInput input, ReaderOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        throw NotYet();
    }

    public Raster Read(EgamiInput input, ReaderOptions options)
    {
        throw NotYet();
    }

    private static NotSupportedException NotYet()
    {
        return new NotSupportedException("This version reads DICOM datasets (DicomFile.Load) but does not decode their pixel data yet.");
    }

    private static double? Number(DicomDataset data, string keyword)
    {
        try
        {
            return data.GetNumber(keyword);
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
