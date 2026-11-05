using System.Buffers.Binary;

namespace Egami.Formats.Extended.Dicom;

/// <summary>
/// A lookup table of PS3.3 C.11 (Modality LUT, VOI LUT) or C.7.6.3.1.5 (palette colour): entries for the inputs from
/// <see cref="First"/> on, each <see cref="Bits"/> wide; inputs below the first take the first entry, inputs past the last
/// the last.
/// </summary>
internal sealed class DicomLut(int first, int bits, int[] entries)
{
    public int First { get; } = first;

    public int Bits { get; } = bits;

    public int[] Entries { get; } = entries;

    /// <summary>Gets the largest value an entry can have: 2^Bits − 1.</summary>
    public double Max => Math.Pow(2, Bits) - 1;

    public int Lookup(long input)
    {
        long at = input - First;
        return Entries[at < 0 ? 0 : at >= Entries.Length ? Entries.Length - 1 : (int)at];
    }

    /// <summary>
    /// Reads a LUT from its descriptor (number of entries, 0 meaning 65536; first input, signed when the pixels are; bits) and
    /// its data, or <see langword="null"/> when either is missing or malformed. 8-bit data stored as bytes, not words, is read
    /// as bytes.
    /// </summary>
    public static DicomLut? Read(DicomElement? descriptor, DicomElement? data, bool signedFirst, bool bigEndian)
    {
        if (descriptor is null || data is null)
        {
            return null;
        }

        int[]? d = Words(descriptor, bigEndian);
        if (d is not { Length: >= 3 })
        {
            return null;
        }

        int count = d[0] == 0 ? 65536 : d[0];
        int first = signedFirst ? (short)d[1] : d[1];
        int bits = Math.Clamp(d[2], 1, 16);
        ReadOnlySpan<byte> raw = data.RawValue.Span;
        int[] entries;
        if (bits <= 8 && raw.Length >= count && raw.Length < count * 2)
        {
            entries = new int[count];
            for (int i = 0; i < count; i++)
            {
                entries[i] = raw[i];
            }
        }
        else
        {
            int words = Math.Min(raw.Length / 2, count);
            if (words == 0)
            {
                return null;
            }

            entries = new int[words];
            for (int i = 0; i < words; i++)
            {
                entries[i] = bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(raw[(i * 2)..]) : BinaryPrimitives.ReadUInt16LittleEndian(raw[(i * 2)..]);
            }
        }

        return new DicomLut(first, bits, entries);
    }

    /// <summary>Reads a segmented palette colour LUT (PS3.3 C.7.9.2) whose descriptor is <paramref name="descriptor"/>.</summary>
    public static DicomLut? ReadSegmented(DicomElement? descriptor, DicomElement? data, bool signedFirst, bool bigEndian)
    {
        if (descriptor is null || data is null)
        {
            return null;
        }

        int[]? d = Words(descriptor, bigEndian);
        int[]? words = Words(data, bigEndian);
        if (d is not { Length: >= 3 } || words is null)
        {
            return null;
        }

        int count = d[0] == 0 ? 65536 : d[0];
        List<int> table = new(count);
        Expand(words, 0, words.Length, table, count, 0);
        if (table.Count == 0)
        {
            return null;
        }

        return new DicomLut(signedFirst ? (short)d[1] : d[1], Math.Clamp(d[2], 1, 16), [.. table]);
    }

    /// <summary>Runs the segments of words[start..end) into <paramref name="table"/>, at most <paramref name="count"/> entries.</summary>
    private static void Expand(int[] words, int start, int end, List<int> table, int count, int depth)
    {
        int at = start;
        while (at + 1 < end && table.Count < count)
        {
            int opcode = words[at];
            int length = words[at + 1];
            switch (opcode)
            {
                case 0:
                    for (int i = 0; i < length && at + 2 + i < end && table.Count < count; i++)
                    {
                        table.Add(words[at + 2 + i]);
                    }

                    at += 2 + length;
                    break;
                case 1:
                    if (at + 2 >= end)
                    {
                        return;
                    }

                    double from = table.Count > 0 ? table[^1] : 0;
                    double to = words[at + 2];
                    for (int i = 1; i <= length && table.Count < count; i++)
                    {
                        table.Add((int)Math.Round(from + ((to - from) * i / length)));
                    }

                    at += 3;
                    break;
                case 2:
                    if (at + 3 >= end || depth > 0)
                    {
                        return;
                    }

                    // The offset is the word index of a segment in the data, its low word first.
                    int offset = words[at + 2] | (words[at + 3] << 16);
                    int segment = 0;
                    int position = 0;
                    while (position < words.Length && segment < offset)
                    {
                        position = Next(words, position);
                        segment++;
                    }

                    for (int i = 0; i < length && position < words.Length; i++)
                    {
                        int next = Next(words, position);
                        Expand(words, position, next, table, count, depth + 1);
                        position = next;
                    }

                    at += 4;
                    break;
                default:
                    return;
            }
        }
    }

    /// <summary>Returns where the segment after the one at <paramref name="at"/> starts.</summary>
    private static int Next(int[] words, int at)
    {
        if (at + 1 >= words.Length)
        {
            return words.Length;
        }

        return words[at] switch
        {
            0 => at + 2 + words[at + 1],
            1 => at + 3,
            2 => at + 4,
            _ => words.Length,
        };
    }

    /// <summary>Returns the 16-bit words of an element (US, SS, OW, or bytes of OB and UN).</summary>
    private static int[]? Words(DicomElement element, bool bigEndian)
    {
        ReadOnlySpan<byte> raw = element.RawValue.Span;
        if (raw.Length < 2)
        {
            return null;
        }

        int[] words = new int[raw.Length / 2];
        for (int i = 0; i < words.Length; i++)
        {
            words[i] = bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(raw[(i * 2)..]) : BinaryPrimitives.ReadUInt16LittleEndian(raw[(i * 2)..]);
        }

        return words;
    }
}

/// <summary>
/// The transforms one frame's values go through (PS3.3 C.11), from the frame's Per-Frame Functional Groups, then the Shared
/// Functional Groups, then the dataset itself (C.7.6.16), and its cine timing.
/// </summary>
internal sealed class DicomFrameParameters
{
    public double Slope { get; private set; } = 1;

    public double Intercept { get; private set; }

    public DicomLut? ModalityLut { get; private set; }

    public double[] Centers { get; private set; } = [];

    public double[] Widths { get; private set; } = [];

    public DicomVoiFunction Function { get; private set; }

    public List<DicomLut> VoiLuts { get; } = [];

    public string? PresentationShape { get; private set; }

    public TimeSpan? FrameTime { get; private set; }

    public double? FrameRate { get; private set; }

    public static DicomFrameParameters Read(DicomImageInfo info, int frame)
    {
        DicomDataset data = info.File.Dataset;
        bool big = info.File.TransferSyntax.IsBigEndian;
        DicomFrameParameters p = new();
        DicomDataset? perFrame = Item(data, 0x5200, 0x9230, frame);
        DicomDataset? shared = Item(data, 0x5200, 0x9229, 0);

        // Pixel Value Transformation (0028,9145) or the Modality LUT module.
        DicomDataset rescale = Macro(perFrame, shared, 0x0028, 0x9145) ?? data;
        if (DicomImageInfo.Number(rescale, 0x0028, 0x1053) is double slope && slope != 0)
        {
            p.Slope = slope;
        }

        p.Intercept = DicomImageInfo.Number(rescale, 0x0028, 0x1052) ?? 0;
        if (Item(data, 0x0028, 0x3000, 0) is DicomDataset modality)
        {
            p.ModalityLut = DicomLut.Read(modality[new DicomTag(0x0028, 0x3002)], modality[new DicomTag(0x0028, 0x3006)], info.IsSigned, big);
        }

        // Frame VOI LUT (0028,9132) or the VOI LUT module.
        DicomDataset voi = Macro(perFrame, shared, 0x0028, 0x9132) ?? data;
        double[] centers = DicomImageInfo.Numbers(voi, 0x0028, 0x1050) ?? [];
        double[] widths = DicomImageInfo.Numbers(voi, 0x0028, 0x1051) ?? [];
        int windows = Math.Min(centers.Length, widths.Length);
        p.Centers = centers[..windows];
        p.Widths = widths[..windows];
        p.Function = (DicomImageInfo.Text(voi, 0x0028, 0x1056) ?? DicomImageInfo.Text(data, 0x0028, 0x1056))?.ToUpperInvariant() switch
        {
            "LINEAR_EXACT" => DicomVoiFunction.LinearExact,
            "SIGMOID" => DicomVoiFunction.Sigmoid,
            _ => DicomVoiFunction.Linear,
        };
        if (data[new DicomTag(0x0028, 0x3010)] is DicomElement luts)
        {
            foreach (DicomDataset item in luts.Items)
            {
                // The VOI LUT's first input is a modality value, signed when the modality values can be.
                if (DicomLut.Read(item[new DicomTag(0x0028, 0x3002)], item[new DicomTag(0x0028, 0x3006)], info.IsSigned || p.Intercept < 0, big) is DicomLut lut)
                {
                    p.VoiLuts.Add(lut);
                }
            }
        }

        p.PresentationShape = DicomImageInfo.Text(data, 0x2050, 0x0020)?.ToUpperInvariant();

        // Cine: Frame Time (ms), or this frame's Frame Time Vector entry (the time since the frame before; the first is 0).
        if (DicomImageInfo.Number(data, 0x0018, 0x1063) is double ms && ms > 0)
        {
            p.FrameTime = TimeSpan.FromMilliseconds(ms);
        }
        else if (DicomImageInfo.Numbers(data, 0x0018, 0x1065) is { } vector && frame + 1 < vector.Length && vector[frame + 1] >= 0)
        {
            p.FrameTime = TimeSpan.FromMilliseconds(vector[frame + 1]);
        }

        p.FrameRate = DicomImageInfo.Number(data, 0x0008, 0x2144) ?? DicomImageInfo.Number(data, 0x0018, 0x0040);
        return p;
    }

    /// <summary>Returns item <paramref name="index"/> of a sequence, or <see langword="null"/>.</summary>
    private static DicomDataset? Item(DicomDataset? data, ushort group, ushort element, int index)
    {
        return data?[new DicomTag(group, element)] is { } sequence && index < sequence.Items.Count ? sequence.Items[index] : null;
    }

    /// <summary>Returns the first item of a functional group macro's sequence, the frame's own before the shared one.</summary>
    private static DicomDataset? Macro(DicomDataset? perFrame, DicomDataset? shared, ushort group, ushort element)
    {
        return Item(perFrame, group, element, 0) ?? Item(shared, group, element, 0);
    }
}
