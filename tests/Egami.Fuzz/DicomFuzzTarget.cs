using Egami.Formats;
using Egami.Formats.Extended;
using Egami.Formats.Extended.Dicom;

namespace Egami.Fuzz;

/// <summary>
/// The dicom fuzz target (DICOM Build Plan tasks 15 and 19): reads the bytes as a DICOM file leniently and strictly, walks every
/// element of every dataset and item and asks each for its value every way, probes them through the registered format, and
/// decodes the first frames' pixel data as shown, as modality values and as stored values, the last with an odd window. Only
/// EgamiExceptions and the documented NotSupportedException (a transfer syntax or layout not decoded) may escape the loads; a
/// getter may refuse a value of the wrong VR or a malformed number or date, as documented. Small images are also saved again
/// (tasks 21 and 22) as explicit VR, RLE and JPEG-LS: each file written must read strictly, with the stored values of its first
/// frame those of the file it came from.
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
                if (recovery == RecoveryMode.Lenient)
                {
                    Saves(file, context);
                }
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
            new() { Recovery = recovery, WindowCenter = -3.5, WindowWidth = 0.25, WindowFunction = DicomWindowFunction.Sigmoid, WindowIndex = 7, PreferWindow = true },
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

    /// <summary>Saves the file in three transfer syntaxes and checks that each reads back strictly with the same stored values.</summary>
    private static void Saves(DicomFile file, EgamiContext context)
    {
        double size = (file.Dataset.GetNumberSafe("Rows") ?? 0) * (file.Dataset.GetNumberSafe("Columns") ?? 0) * file.FrameCount;
        if (size > 1 << 16)
        {
            return;
        }

        int[]? before = Stored(file, context);
        foreach (DicomTransferSyntax syntax in (DicomTransferSyntax[])[DicomTransferSyntax.ExplicitVRLittleEndian, DicomTransferSyntax.RleLossless, DicomTransferSyntax.JpegLsLossless])
        {
            using MemoryStream output = new();
            try
            {
                file.Save(output, new DicomWriterOptions { TransferSyntax = syntax }, context);
            }
            catch (Exception e) when (e is EgamiException or NotSupportedException)
            {
                continue;
            }

            DicomFile again = DicomFile.Load(output.ToArray(), context, new ReaderOptions { Recovery = RecoveryMode.Strict });
            if (again.TransferSyntax.Uid != syntax.Uid)
            {
                throw new InvalidOperationException($"A file saved as {syntax} reads back as {again.TransferSyntax}.");
            }

            if (before is not null && (Stored(again, context) is not int[] after || !before.AsSpan().SequenceEqual(after)))
            {
                throw new InvalidOperationException($"A file saved as {syntax} does not keep the stored values of its first frame.");
            }
        }
    }

    /// <summary>Returns the stored values of the first frame, decoded strictly, or null when they cannot be.</summary>
    private static int[]? Stored(DicomFile file, EgamiContext context)
    {
        if (file.Dataset[DicomTag.PixelData] is null)
        {
            return null;
        }

        try
        {
            using EgamiInput input = new(Stream.Null, context, default);
            DicomImageInfo info = DicomImageInfo.Read(file);
            DicomPixelDecoder decoder = new(info, input, new ReaderOptions { Recovery = RecoveryMode.Strict }, static (_, _) => { });
            int[] samples = (int[])decoder.Decode(0).Samples.Clone();

            // A codestream may hold values wider than Bits Stored; only the stored bits are written.
            int bits = Math.Clamp(info.BitsStored, 1, 32);
            for (int i = 0; i < samples.Length && bits < 32; i++)
            {
                int low = samples[i] & ((1 << bits) - 1);
                samples[i] = info.IsSigned ? (low << (32 - bits)) >> (32 - bits) : low;
            }

            return samples;
        }
        catch (Exception e) when (e is EgamiException or NotSupportedException)
        {
            return null;
        }
    }

    private static double? GetNumberSafe(this DicomDataset dataset, string keyword)
    {
        try
        {
            return dataset.GetNumber(keyword);
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            return null;
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
