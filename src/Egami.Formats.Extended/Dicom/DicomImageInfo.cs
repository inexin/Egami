namespace Egami.Formats.Extended.Dicom;

/// <summary>
/// The Image Pixel module of a dataset (PS3.3 C.7.6.3): the size, the samples and how they are stored, read leniently so a
/// missing or malformed attribute takes its usual value.
/// </summary>
internal sealed class DicomImageInfo
{
    private DicomImageInfo(DicomFile file)
    {
        DicomDataset data = file.Dataset;
        File = file;
        Rows = Integer(data, 0x0028, 0x0010, 0);
        Columns = Integer(data, 0x0028, 0x0011, 0);
        SamplesPerPixel = Math.Max(Integer(data, 0x0028, 0x0002, 1), 1);
        BitsAllocated = Integer(data, 0x0028, 0x0100, 8);
        BitsStored = Integer(data, 0x0028, 0x0101, BitsAllocated);
        HighBit = Integer(data, 0x0028, 0x0102, BitsStored - 1);
        IsSigned = Integer(data, 0x0028, 0x0103, 0) == 1;
        PlanarConfiguration = Integer(data, 0x0028, 0x0006, 0);
        Frames = Math.Max(Integer(data, 0x0028, 0x0008, 1), 1);
        Photometric = Text(data, 0x0028, 0x0004)?.ToUpperInvariant() ?? (SamplesPerPixel >= 3 ? "RGB" : "MONOCHROME2");
        if (BitsStored <= 0 || BitsStored > BitsAllocated)
        {
            BitsStored = BitsAllocated;
        }

        if (HighBit < BitsStored - 1 || HighBit >= BitsAllocated)
        {
            HighBit = BitsStored - 1;
        }
    }

    public DicomFile File { get; }

    public int Rows { get; }

    public int Columns { get; }

    public int SamplesPerPixel { get; }

    public int BitsAllocated { get; }

    public int BitsStored { get; }

    public int HighBit { get; }

    public bool IsSigned { get; }

    public int PlanarConfiguration { get; }

    public int Frames { get; }

    public string Photometric { get; }

    /// <summary>Gets whether the pixels index the palette's colour tables.</summary>
    public bool IsPalette => SamplesPerPixel == 1 && Photometric == "PALETTE COLOR";

    /// <summary>Gets whether one sample is one gray value.</summary>
    public bool IsMonochrome => SamplesPerPixel == 1 && !IsPalette;

    /// <summary>Gets whether native data stores two luma samples then the shared chroma pair (PS3.3 C.7.6.3.1.2).</summary>
    public bool IsHorizontal422 => SamplesPerPixel == 3 && Photometric is "YBR_FULL_422" or "YBR_PARTIAL_422";

    /// <summary>Reads the module, or throws when the dataset has no image.</summary>
    public static DicomImageInfo Read(DicomFile file)
    {
        DicomImageInfo info = new(file);
        if (info.Rows <= 0 || info.Columns <= 0)
        {
            throw new UnsupportedFormatException("This DICOM file has no image: its dataset has no Rows and Columns.");
        }

        return info;
    }

    /// <summary>Throws <see cref="NotSupportedException"/> for a layout this reader does not decode.</summary>
    public void CheckSupported()
    {
        if (BitsAllocated is not (1 or 8 or 16 or 32))
        {
            throw new NotSupportedException($"Bits Allocated {BitsAllocated} is not decoded; DICOM pixel data is read at 1, 8, 16 and 32 bits.");
        }

        if (SamplesPerPixel is not (1 or 3))
        {
            throw new NotSupportedException($"Samples per Pixel {SamplesPerPixel} ({Photometric}) is not decoded; gray, palette and three-sample colour images are.");
        }

        if (SamplesPerPixel == 3 && Photometric is not ("RGB" or "YBR_FULL" or "YBR_FULL_422" or "YBR_PARTIAL_422" or "YBR_PARTIAL_420" or "YBR_ICT" or "YBR_RCT"))
        {
            throw new NotSupportedException($"The photometric interpretation {Photometric} is not decoded.");
        }
    }

    /// <summary>Returns the first integer of an element, or <paramref name="fallback"/> when it is missing or malformed.</summary>
    internal static int Integer(DicomDataset data, ushort group, ushort element, int fallback)
    {
        double? value = Number(data, group, element);
        return value is double v && v >= int.MinValue && v <= int.MaxValue ? (int)v : fallback;
    }

    /// <summary>Returns the first number of an element, or <see langword="null"/> when it is missing or malformed.</summary>
    internal static double? Number(DicomDataset data, ushort group, ushort element)
    {
        double[]? values = Numbers(data, group, element);
        return values is { Length: > 0 } && double.IsFinite(values[0]) ? values[0] : null;
    }

    /// <summary>Returns the numbers of an element, or <see langword="null"/> when it is missing or malformed.</summary>
    internal static double[]? Numbers(DicomDataset data, ushort group, ushort element)
    {
        DicomElement? found = data[new DicomTag(group, element)];
        if (found is null)
        {
            return null;
        }

        try
        {
            return found.VR is DicomVR.OB or DicomVR.UN && found.RawValue.Length is 2 or 4
                ? [found.RawValue.Length == 2 ? BitConverter.ToUInt16(found.RawValue.Span) : BitConverter.ToUInt32(found.RawValue.Span)]
                : found.GetNumbers();
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>Returns the trimmed text of an element, or <see langword="null"/> when it is missing, empty or not text.</summary>
    internal static string? Text(DicomDataset data, ushort group, ushort element)
    {
        DicomElement? found = data[new DicomTag(group, element)];
        if (found is null)
        {
            return null;
        }

        try
        {
            string text = found.GetString().Trim();
            return text.Length == 0 ? null : text;
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
