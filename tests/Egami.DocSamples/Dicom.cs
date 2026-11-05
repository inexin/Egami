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
        // Registered, DICOM is detected and probed; decoding its pixel data comes in a later version.
        EgamiContext context = new EgamiContext().WithDicom();
        RasterInfo info = Raster.Probe("scan.dcm", context);
        Console.WriteLine($"{info.Format.Name} {info.Width}x{info.Height}, {info.FrameCount} frames");
        #endregion

        Assert.Equal((4, 4), (info.Width, info.Height));
        Assert.Same(DicomFormat.Info, info.Format);
    }
}
