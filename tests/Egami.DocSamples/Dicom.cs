using Egami.Formats;
using Egami.Formats.Extended;
using Egami.Formats.Extended.Dicom;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Dicom
{
    [Fact]
    public void Load()
    {
        using SampleFiles files = new();

        #region dicom-load
        // The dataset of a DICOM file, every element kept; the preview API needs EGAMIX001 suppressed.
        DicomFile file = DicomFile.Load("scan.dcm");
        Console.WriteLine($"{file.TransferSyntax.Name}, {file.Dataset.Count} elements");

        // Elements by PS3.6 keyword or by tag, null when the file has none.
        string? modality = file.Dataset.GetString("Modality");
        double? rows = file.Dataset.GetNumber("Rows");
        DicomElement? name = file.Dataset[new DicomTag(0x0010, 0x0010)];
        foreach (EgamiDiagnostic warning in file.Warnings)
        {
            Console.WriteLine($"{warning.Id}: {warning.Message}");
        }
        #endregion

        Assert.Equal("CT", modality);
        Assert.Equal(4, rows);
        Assert.NotNull(name);
    }

    [Fact]
    public void Values()
    {
        using SampleFiles files = new();
        DicomFile file = DicomFile.Load("scan.dcm");

        #region dicom-values
        DicomDataset data = file.Dataset;
        DateOnly? studyDate = data["StudyDate"]?.GetDate();
        DicomPersonName? patient = data["PatientName"]?.GetPersonName();
        double[] window = data["WindowCenter"]?.GetNumbers() ?? [];

        // A sequence is a list of datasets; text decodes by the dataset's Specific Character Set.
        foreach (DicomDataset item in data["ContentSequence"]?.Items ?? [])
        {
            Console.WriteLine(item.GetString("ReferencedSOPClassUID"));
        }

        // Every element prints as a dump line, such as "(0028,0010) US Rows: 4".
        foreach (DicomElement element in data)
        {
            Console.WriteLine(element);
        }
        #endregion

        Assert.Equal(new DateOnly(2026, 11, 5), studyDate);
        Assert.Equal("Doe", patient!.FamilyName);
        Assert.Equal([40, 400], window);
    }

    [Fact]
    public void Tags()
    {
        #region dicom-tags
        DicomTag tag = DicomTag.FromKeyword("PixelSpacing");             // (0028,0030)
        string? keyword = new DicomTag(0x6002, 0x3000).Keyword;          // OverlayData: repeating groups match
        bool isPrivate = new DicomTag(0x0009, 0x1001).IsPrivate;
        Console.WriteLine($"{tag} {tag.Name}; dictionary {DicomFile.DictionaryEdition}");
        #endregion

        Assert.Equal(new DicomTag(0x0028, 0x0030), tag);
        Assert.Equal("OverlayData", keyword);
        Assert.True(isPrivate);
    }

    [Fact]
    public void Fragments()
    {
        using SampleFiles files = new();

        #region dicom-fragments
        // Compressed pixel data stays as the fragments the file stores, with its offset tables.
        DicomFile file = DicomFile.Load("compressed.dcm");
        DicomFragments? pixels = file.Dataset[DicomTag.PixelData]?.Fragments;
        if (pixels is not null)
        {
            Console.WriteLine($"{file.TransferSyntax.Name}: {pixels.Fragments.Count} fragments, {pixels.BasicOffsetTable.Count} frame offsets");
        }
        #endregion

        Assert.Equal(2, pixels!.Fragments.Count);
    }

    [Fact]
    public void Probe()
    {
        using SampleFiles files = new();

        #region dicom-probe
        // Registered, DICOM is detected and probed: the size, the layout a load gives and the number of frames.
        EgamiContext context = new EgamiContext().WithDicom();
        RasterInfo info = Raster.Probe("scan.dcm", context);
        Console.WriteLine($"{info.Format.Name} {info.Width}x{info.Height} {info.Layout}, {info.ImageCount} frames");
        #endregion

        Assert.Equal((4, 4), (info.Width, info.Height));
        Assert.Same(DicomFormat.Info, info.Format);
    }

    [Fact]
    public void Pixels()
    {
        using SampleFiles files = new();

        #region dicom-pixels
        // A load gives what a viewer shows: rescaled, windowed, inverted for MONOCHROME1, colour as RGB.
        EgamiContext context = new EgamiContext().WithDicom();
        using Raster image = Raster.Load("scan.dcm", context);
        DicomDetails details = (DicomDetails)image.Metadata.FormatDetails!;
        Console.WriteLine($"{image.Layout}: window {details.WindowCenter}/{details.WindowWidth}, slope {details.RescaleSlope}");
        image.Save("scan.png");
        #endregion

        Assert.Equal((4, 4), (image.Width, image.Height));
        Assert.True(details.UsedMinMax); // The sample has window centers but no widths.
    }

    [Fact]
    public void Window()
    {
        using SampleFiles files = new();
        EgamiContext context = new EgamiContext().WithDicom();

        #region dicom-window
        // A window of your own, in modality values (Hounsfield units for CT), or the file's second one.
        using Raster bone = Raster.Load("scan.dcm", context, new DicomReaderOptions { WindowCenter = 400, WindowWidth = 1800 });
        using Raster second = Raster.Load("scan.dcm", context, new DicomReaderOptions { VoiIndex = 1 });
        using Raster smooth = Raster.Load("scan.dcm", context,
            new DicomReaderOptions { WindowCenter = 40, WindowWidth = 400, WindowFunction = DicomVoiFunction.Sigmoid });
        #endregion

        Assert.Equal(400, ((DicomDetails)bone.Metadata.FormatDetails!).WindowCenter);
        Assert.Equal(1, ((DicomDetails)second.Metadata.FormatDetails!).FrameCount);
        Assert.Equal(DicomVoiFunction.Sigmoid, ((DicomDetails)smooth.Metadata.FormatDetails!).WindowFunction);
    }

    [Fact]
    public void ValuesOption()
    {
        using SampleFiles files = new();
        EgamiContext context = new EgamiContext().WithDicom();

        #region dicom-values-option
        // The numbers behind the picture, as GrayF32: modality values (such as Hounsfield units), or the stored values.
        using Raster<Egami.Pixels.GrayF32> hounsfield = Raster.Load<Egami.Pixels.GrayF32>("scan.dcm", context,
            new DicomReaderOptions { Values = DicomValues.Modality });
        float center = hounsfield.Row(2)[2].L;
        #endregion

        Assert.True(float.IsFinite(center));
    }

    [Fact]
    public void Frames()
    {
        using SampleFiles files = new();
        EgamiContext context = new EgamiContext().WithDicom();

        #region dicom-frames
        // Each frame of a multi-frame file is an image: LoadAll gives them all, DicomReaderOptions.Frame one of them.
        using RasterList frames = Raster.LoadAll("scan.dcm", context);

        // With the file read once, LoadFrame decodes frame after frame without reading it again.
        DicomFile file = DicomFile.Load("scan.dcm");
        for (int i = 0; i < file.FrameCount; i++)
        {
            using Raster frame = file.LoadFrame(i);
            Console.WriteLine($"frame {i}: {frame.Width}x{frame.Height}");
        }
        #endregion

        Assert.Equal(file.FrameCount, frames.Count);
    }
}
