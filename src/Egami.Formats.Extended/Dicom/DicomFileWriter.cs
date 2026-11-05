using System.Runtime.InteropServices;
using System.Text;

using Egami.Pixels;

namespace Egami.Formats.Extended.Dicom;

/// <summary>
/// Writes DICOM files: an opened <see cref="DicomFile"/> again in another transfer syntax (every element kept, the pixel data
/// re-encoded), and Secondary Capture images (PS3.3 A.8.1) from rasters.
/// </summary>
internal static class DicomFileWriter
{
    private static readonly DicomTag SopClassUid = new(0x0008, 0x0016);
    private static readonly DicomTag SopInstanceUid = new(0x0008, 0x0018);
    private static readonly DicomTag LossyTag = new(0x0028, 0x2110);
    private static readonly DicomTag LossyRatioTag = new(0x0028, 0x2112);
    private static readonly DicomTag LossyMethodTag = new(0x0028, 0x2114);

    /// <summary>The Image Pixel elements that describe the pixel data, which the writer sets.</summary>
    private static readonly HashSet<uint> ImagePixel =
    [
        0x00280002, 0x00280004, 0x00280006, 0x00280008, 0x00280010, 0x00280011, 0x00280100, 0x00280101, 0x00280102, 0x00280103,
        0x00282110, 0x00282112, 0x00282114,
    ];

    /// <summary>Gets the SOP Class UID of Secondary Capture Image Storage.</summary>
    internal const string SecondaryCapture = "1.2.840.10008.5.1.4.1.1.7";

    /// <summary>
    /// Writes <paramref name="file"/> in the transfer syntax of <paramref name="options"/>: every element kept but group lengths;
    /// native pixel data of a little-endian file and encapsulated pixel data already in the target transfer syntax copied as they
    /// are, any other decoded and encoded again. A lossy coding records itself (Lossy Image Compression, its ratio and method) and
    /// gives the image a new SOP Instance UID, as PS3.3 C.7.6.1.1.5 says.
    /// </summary>
    public static void Transcode(DicomFile file, Stream output, DicomWriterOptions options, EgamiContext context)
    {
        DicomTransferSyntax target = options.TransferSyntax;
        Dictionary<uint, DicomElement> changed = [];
        HashSet<uint> removed = [];
        DicomDataset dataset = file.Dataset;
        if (dataset[DicomTag.PixelData] is DicomElement pixels && !Keeps(file, pixels, target))
        {
            using EgamiInput input = new(Stream.Null, context, default);
            DicomImageInfo info = DicomImageInfo.Read(file);
            DicomPixelDecoder decoder = new(info, input, new ReaderOptions { Recovery = RecoveryMode.Strict }, static (_, _) => { });
            decoder.CheckSupported();
            DicomSamples first = decoder.Decode(0);
            DicomPixelLayout layout = new(info.Rows, info.Columns, info.SamplesPerPixel, info.BitsAllocated, info.BitsStored, info.HighBit, info.IsSigned,
                Photometric(info, first), info.Frames);
            DicomEncodedPixels encoded = DicomPixelEncoder.Encode(layout, f => f == 0 ? first.Samples : decoder.Decode(f).Samples, target, options, context);
            removed.Add(0x7FE00001);
            removed.Add(0x7FE00002);
            foreach (DicomElement element in encoded.Elements)
            {
                changed[element.Tag.Value] = element;
            }

            if (encoded.IsLossy)
            {
                Lossy(dataset, encoded, changed);
                changed[SopInstanceUid.Value] = DicomValue.Text(SopInstanceUid, DicomVR.UI, DicomUid.Generate());
            }
        }

        List<DicomElement> elements = [];
        foreach (DicomElement element in dataset)
        {
            if (removed.Contains(element.Tag.Value) || changed.ContainsKey(element.Tag.Value))
            {
                continue;
            }

            elements.Add(element);
        }

        elements.AddRange(changed.Values);
        string sopClass = Uid(changed.GetValueOrDefault(SopClassUid.Value) ?? dataset[SopClassUid]);
        string sopInstance = Uid(changed.GetValueOrDefault(SopInstanceUid.Value) ?? dataset[SopInstanceUid]);
        DicomStreamWriter.WriteFile(output, Meta(sopClass, sopInstance, target, file.FileMetaInformation), elements, target);
    }

    /// <summary>Returns whether the pixel data goes into the target as it is: native little-endian into native, or already in its encapsulated transfer syntax.</summary>
    private static bool Keeps(DicomFile file, DicomElement pixels, DicomTransferSyntax target)
    {
        DicomPixelDecoder.DicomCodec from = DicomPixelDecoder.CodecFor(file.TransferSyntax.Uid);
        DicomPixelDecoder.DicomCodec to = DicomPixelDecoder.CodecFor(target.Uid);
        if (pixels.Fragments is null)
        {
            return from == DicomPixelDecoder.DicomCodec.Native && to == DicomPixelDecoder.DicomCodec.Native && !file.TransferSyntax.IsBigEndian;
        }

        return file.TransferSyntax.Uid == target.Uid;
    }

    /// <summary>
    /// Returns the photometric interpretation of decoded samples: RGB for those the codec gave as RGB (JPEG XL, a JPEG 2000
    /// component transform undone), YBR_FULL for 4:2:2 data whose chroma the decoder gave every pixel, otherwise the file's.
    /// </summary>
    private static string Photometric(DicomImageInfo info, DicomSamples samples)
    {
        if (info.SamplesPerPixel != 3)
        {
            return info.Photometric;
        }

        return samples.IsRgb || info.Photometric is "YBR_RCT" or "YBR_ICT" ? "RGB"
            : info.Photometric == "YBR_FULL_422" ? "YBR_FULL"
            : info.Photometric;
    }

    /// <summary>Records a lossy coding: Lossy Image Compression 01, and the ratio and method added to any earlier ones.</summary>
    private static void Lossy(DicomDataset dataset, DicomEncodedPixels encoded, Dictionary<uint, DicomElement> changed)
    {
        changed[LossyTag.Value] = DicomValue.Text(LossyTag, DicomVR.CS, "01");
        List<string> ratios = [.. Values(dataset[LossyRatioTag]), DicomPixelEncoder.Ratio(encoded.Ratio)];
        List<string> methods = [.. Values(dataset[LossyMethodTag]), encoded.Method ?? ""];
        changed[LossyRatioTag.Value] = DicomValue.Text(LossyRatioTag, DicomVR.DS, string.Join('\\', ratios));
        changed[LossyMethodTag.Value] = DicomValue.Text(LossyMethodTag, DicomVR.CS, string.Join('\\', methods));
    }

    private static IReadOnlyList<string> Values(DicomElement? element)
    {
        if (element is null || element.Fragments is not null || element.VR == DicomVR.SQ)
        {
            return [];
        }

        return element.LooseText() is string text && text.Length > 0 ? [.. text.Split('\\').Select(v => v.Trim())] : [];
    }

    private static string Uid(DicomElement? element) => element?.LooseText() ?? "";

    /// <summary>
    /// Returns the file meta information (PS3.10 7.1): version, SOP class and instance, transfer syntax and Egami's implementation
    /// UID and version, then any other elements the file had, such as the Source Application Entity Title.
    /// </summary>
    internal static List<DicomElement> Meta(string sopClass, string sopInstance, DicomTransferSyntax syntax, DicomDataset? kept)
    {
        List<DicomElement> meta =
        [
            DicomValue.Bytes(new DicomTag(0x0002, 0x0001), DicomVR.OB, new byte[] { 0, 1 }),
            DicomValue.Text(new DicomTag(0x0002, 0x0002), DicomVR.UI, sopClass),
            DicomValue.Text(new DicomTag(0x0002, 0x0003), DicomVR.UI, sopInstance),
            DicomValue.Text(DicomTag.TransferSyntaxUid, DicomVR.UI, syntax.Uid),
            DicomValue.Text(new DicomTag(0x0002, 0x0012), DicomVR.UI, DicomUid.ImplementationClassUid),
            DicomValue.Text(new DicomTag(0x0002, 0x0013), DicomVR.SH, DicomUid.ImplementationVersion),
        ];
        foreach (DicomElement element in kept ?? DicomDataset.Empty)
        {
            if (element.Tag.Group == 0x0002 && element.Tag.Element > 0x0013)
            {
                meta.Add(element);
            }
        }

        return meta;
    }

    /// <summary>
    /// Writes a Secondary Capture image of one frame of samples (gray as MONOCHROME2, colour as RGB, 8 or 16 bits): the Patient,
    /// General Study, General Series, SC Equipment, General Image, Image Pixel and SOP Common modules, their type 2 elements left
    /// empty unless <see cref="DicomWriterOptions.Attributes"/> gives them.
    /// </summary>
    public static void SecondaryCaptureImage(Stream output, int width, int height, int components, int bits, int[] samples, DicomWriterOptions options, EgamiContext context)
    {
        DicomPixelLayout layout = new(height, width, components, bits, bits, bits - 1, false, components == 1 ? "MONOCHROME2" : "RGB", 1);
        bool ascii = options.Attributes.Values.All(v => v is null || Ascii.IsValid(v));
        Encoding encoding = ascii ? Encoding.ASCII : Encoding.UTF8;
        Dictionary<uint, DicomElement> elements = [];
        void Set(ushort group, ushort element, DicomVR vr, string text) => elements[((uint)group << 16) | element] = DicomValue.Text(new DicomTag(group, element), vr, text);

        if (!ascii)
        {
            Set(0x0008, 0x0005, DicomVR.CS, "ISO_IR 192");
        }

        Set(0x0008, 0x0016, DicomVR.UI, SecondaryCapture);
        Set(0x0008, 0x0018, DicomVR.UI, DicomUid.Generate());
        Set(0x0008, 0x0020, DicomVR.DA, "");
        Set(0x0008, 0x0030, DicomVR.TM, "");
        Set(0x0008, 0x0050, DicomVR.SH, "");
        Set(0x0008, 0x0060, DicomVR.CS, "OT");
        Set(0x0008, 0x0064, DicomVR.CS, "WSD");
        Set(0x0008, 0x0090, DicomVR.PN, "");
        Set(0x0010, 0x0010, DicomVR.PN, "");
        Set(0x0010, 0x0020, DicomVR.LO, "");
        Set(0x0010, 0x0030, DicomVR.DA, "");
        Set(0x0010, 0x0040, DicomVR.CS, "");
        Set(0x0020, 0x000D, DicomVR.UI, DicomUid.Generate());
        Set(0x0020, 0x000E, DicomVR.UI, DicomUid.Generate());
        Set(0x0020, 0x0010, DicomVR.SH, "");
        Set(0x0020, 0x0011, DicomVR.IS, "");
        Set(0x0020, 0x0013, DicomVR.IS, "");
        Set(0x0020, 0x0020, DicomVR.CS, "");
        elements[0x00280002] = DicomValue.UShort(new DicomTag(0x0028, 0x0002), components);
        elements[0x00280010] = DicomValue.UShort(new DicomTag(0x0028, 0x0010), height);
        elements[0x00280011] = DicomValue.UShort(new DicomTag(0x0028, 0x0011), width);
        elements[0x00280100] = DicomValue.UShort(new DicomTag(0x0028, 0x0100), bits);
        elements[0x00280101] = DicomValue.UShort(new DicomTag(0x0028, 0x0101), bits);
        elements[0x00280102] = DicomValue.UShort(new DicomTag(0x0028, 0x0102), bits - 1);
        elements[0x00280103] = DicomValue.UShort(new DicomTag(0x0028, 0x0103), 0);

        foreach ((string keyword, string value) in options.Attributes)
        {
            if (!DicomTag.TryFromKeyword(keyword, out DicomTag tag))
            {
                throw new ArgumentException($"PS3.6 has no keyword '{keyword}'.", nameof(options));
            }

            if (tag.Group is 0x0002 or 0x7FE0 || ImagePixel.Contains(tag.Value) || tag == SopClassUid || tag == DicomTag.SpecificCharacterSet)
            {
                throw new ArgumentException($"{keyword} {tag} is set by the writer from the image; it cannot be given.", nameof(options));
            }

            DicomVR vr = DicomDictionary.Find(tag)?.VRs.FirstOrDefault() ?? DicomVR.UN;
            elements[tag.Value] = DicomValue.FromText(tag, vr, value ?? "", encoding);
        }

        DicomEncodedPixels encoded = DicomPixelEncoder.Encode(layout, _ => samples, options.TransferSyntax, options, context);
        foreach (DicomElement element in encoded.Elements)
        {
            elements[element.Tag.Value] = element;
        }

        if (encoded.IsLossy)
        {
            elements[LossyTag.Value] = DicomValue.Text(LossyTag, DicomVR.CS, "01");
            elements[LossyRatioTag.Value] = DicomValue.Text(LossyRatioTag, DicomVR.DS, DicomPixelEncoder.Ratio(encoded.Ratio));
            elements[LossyMethodTag.Value] = DicomValue.Text(LossyMethodTag, DicomVR.CS, encoded.Method ?? "");
        }

        string instance = Uid(elements[SopInstanceUid.Value]);
        DicomStreamWriter.WriteFile(output, Meta(SecondaryCapture, instance, options.TransferSyntax, null), [.. elements.Values], options.TransferSyntax);
    }
}

/// <summary>
/// Writes a raster as a DICOM Secondary Capture image: gray pixel types as MONOCHROME2, any other as RGB, 8 bits for 8-bit types
/// and 16 for wider and float ones; alpha is dropped.
/// </summary>
internal sealed class DicomWriter : ExtendedWriter
{
    protected override FormatInfo Format => DicomFormat.Info;

    public override RowEncoder<TPixel> Start<TPixel>(Stream output, EncodeStart<TPixel> start)
    {
        DicomWriterOptions options = start.Options as DicomWriterOptions ?? DicomWriterOptions.Default;
        PixelLayoutInfo layout = TPixel.Layout;
        bool wide = layout.BitsPerChannel > 8 || layout.ChannelType is PixelChannelType.Float16 or PixelChannelType.Float32;
        start.ReportLosses("DICOM", wide ? SaveKeeps.Wide : SaveKeeps.None);
        return new Encoder<TPixel>(output, start.Width, start.Height, IsGray<TPixel>() ? 1 : 3, wide ? 16 : 8, options, start.Context);
    }

    private sealed class Encoder<TPixel>(Stream output, int width, int height, int components, int bits, DicomWriterOptions options, EgamiContext context)
        : RowEncoder<TPixel>
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        private readonly int[] _samples = new int[checked(width * height * components)];
        private readonly byte[] _row = new byte[width * components * (bits / 8)];
        private int _y;

        public override void WriteRow(ReadOnlySpan<TPixel> row)
        {
            Span<int> target = _samples.AsSpan(_y++ * width * components, width * components);
            switch (components, bits)
            {
                case (1, 8):
                    PixelConvert.Convert(row, MemoryMarshal.Cast<byte, Gray8>(_row.AsSpan()));
                    Copy(_row, target);
                    break;
                case (1, _):
                    PixelConvert.Convert(row, MemoryMarshal.Cast<byte, Gray16>(_row.AsSpan()));
                    Copy(MemoryMarshal.Cast<byte, ushort>(_row.AsSpan()), target);
                    break;
                case (_, 8):
                    PixelConvert.Convert(row, MemoryMarshal.Cast<byte, Rgb8>(_row.AsSpan()));
                    Copy(_row, target);
                    break;
                default:
                    PixelConvert.Convert(row, MemoryMarshal.Cast<byte, Rgb16>(_row.AsSpan()));
                    Copy(MemoryMarshal.Cast<byte, ushort>(_row.AsSpan()), target);
                    break;
            }
        }

        public override void Complete()
        {
            DicomFileWriter.SecondaryCaptureImage(output, width, height, components, bits, _samples, options, context);
        }

        private static void Copy(ReadOnlySpan<byte> source, Span<int> target)
        {
            for (int i = 0; i < target.Length; i++)
            {
                target[i] = source[i];
            }
        }

        private static void Copy(ReadOnlySpan<ushort> source, Span<int> target)
        {
            for (int i = 0; i < target.Length; i++)
            {
                target[i] = source[i];
            }
        }
    }
}
