using Egami.Formats;
using Egami.Formats.Extended;
using Egami.Formats.Extended.Dicom;

namespace Egami.Fuzz;

/// <summary>
/// The dicom fuzz target (DICOM Build Plan tasks 15 and 19): reads the bytes as a DICOM file leniently and strictly, walks every
/// element of every dataset and item and asks each for its value every way, probes them through the registered format, and
/// decodes the first frames' pixel data as shown, as modality values and as stored values, the last with an odd window. Only
/// EgamiExceptions and the documented NotSupportedException (a transfer syntax or layout not decoded) may escape the loads; a
/// getter may refuse a value of the wrong VR or a malformed number or date, as documented.
/// </summary>
internal static class DicomFuzzTarget
{
    public static void Load(byte[] data, EgamiContext context)
    {
        foreach (RecoveryMode recovery in (RecoveryMode[])[RecoveryMode.Lenient, RecoveryMode.Strict])
        {
            try
            {
                DicomFile file = DicomFile.Load(data, context, new ReaderOptions { Recovery = recovery });
                Walk(file.FileMetaInformation, 0);
                Walk(file.Dataset, 0);
                _ = file.TransferSyntax.Name;
                Pixels(file, context, recovery);
            }
            catch (EgamiException)
            {
            }
        }

        try
        {
            _ = Raster.Probe(data, DicomFormat.Info, context.WithDicom());
        }
        catch (EgamiException)
        {
        }
    }

    /// <summary>Decodes up to three frames every way a load can give them.</summary>
    private static void Pixels(DicomFile file, EgamiContext context, RecoveryMode recovery)
    {
        DicomReaderOptions[] ways =
        [
            new() { Recovery = recovery },
            new() { Recovery = recovery, Values = DicomValues.Modality },
            new() { Recovery = recovery, Values = DicomValues.Stored },
            new() { Recovery = recovery, WindowCenter = -3.5, WindowWidth = 0.25, WindowFunction = DicomVoiFunction.Sigmoid, VoiIndex = 7, PreferWindow = true },
        ];
        for (int frame = 0; frame < Math.Min(file.FrameCount, 3); frame++)
        {
            foreach (DicomReaderOptions way in ways)
            {
                try
                {
                    using Raster raster = file.LoadFrame(frame, way, context);
                    _ = raster.Metadata.FormatDetails?.Copy();
                }
                catch (Exception e) when (e is EgamiException or NotSupportedException)
                {
                }
            }
        }
    }

    private static void Walk(DicomDataset dataset, int depth)
    {
        if (depth > DicomParser.MaxDepth + 1)
        {
            throw new InvalidOperationException("The parser let sequences nest deeper than its limit.");
        }

        foreach (DicomElement element in dataset)
        {
            _ = element.ToStringSafe();
            Try(() => element.GetString());
            Try(() => element.GetStrings());
            Try(() => element.GetNumbers());
            Try(() => element.GetIntegers());
            Try(() => element.GetTags());
            Try(() => element.GetDate());
            Try(() => element.GetTime());
            Try(() => element.GetDateTime());
            Try(() => element.GetPersonName());
            _ = element.Tag.Keyword;
            if (element.Fragments is DicomFragments fragments)
            {
                _ = fragments.Fragments.Sum(f => f.Length) + fragments.BasicOffsetTable.Count + fragments.ExtendedOffsetTable.Count;
            }

            foreach (DicomDataset item in element.Items)
            {
                Walk(item, depth + 1);
            }
        }
    }

    private static string ToStringSafe(this DicomElement element)
    {
        try
        {
            return element.ToString();
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            return "";
        }
    }

    private static void Try(Func<object?> get)
    {
        try
        {
            _ = get();
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
        }
    }
}
