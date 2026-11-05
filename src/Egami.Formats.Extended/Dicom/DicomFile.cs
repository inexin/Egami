using System.Diagnostics.CodeAnalysis;

namespace Egami.Formats.Extended.Dicom;

/// <summary>
/// A DICOM file (PS3.10) read into its datasets: the file meta information, and the dataset in the file's transfer syntax,
/// every element kept, sequences as nested datasets, encapsulated pixel data as raw fragments. Implicit and explicit VR little
/// endian, explicit VR big endian and deflated files are read, with or without the preamble. In preview: the API may change
/// before it is stable.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Dicom.cs" region="dicom-load"/>
/// </example>
[Experimental(DiagnosticId, UrlFormat = DiagnosticUrl)]
public sealed class DicomFile
{
    /// <summary>The diagnostic id of the DICOM preview API.</summary>
    internal const string DiagnosticId = "EGAMIX001";

    /// <summary>Where the diagnostic points.</summary>
    internal const string DiagnosticUrl = "https://github.com/inexin/Egami/blob/main/docs/articles/formats/dicom.md";

    private DicomFile(DicomParser.Result result)
    {
        HasPreamble = result.HasPreamble;
        FileMetaInformation = result.Meta;
        Dataset = result.Dataset;
        TransferSyntax = result.TransferSyntax;
        Warnings = result.Warnings;
    }

    /// <summary>Gets whether the file starts with the 128-byte preamble and DICM, as PS3.10 requires.</summary>
    public bool HasPreamble { get; }

    /// <summary>Gets the file meta information (group 0002); empty for a bare dataset.</summary>
    public DicomDataset FileMetaInformation { get; }

    /// <summary>Gets the dataset: every element after the file meta information.</summary>
    public DicomDataset Dataset { get; }

    /// <summary>Gets the transfer syntax the dataset was read in: the file meta information's, or the one a bare dataset looked like.</summary>
    public DicomTransferSyntax TransferSyntax { get; }

    /// <summary>Gets the problems a lenient read recovered from and the irregularities it read past, such as <c>Dicom.Truncated</c>.</summary>
    public IReadOnlyList<EgamiDiagnostic> Warnings { get; }

    /// <summary>Gets the number of frames: Number of Frames (0028,0008), 1 when the dataset has none.</summary>
    public int FrameCount => Math.Max(DicomImageInfo.Integer(Dataset, 0x0028, 0x0008, 1), 1);

    /// <summary>Gets the PS3.6 edition whose data dictionary names tags, such as <c>DICOM PS3.6 2026d</c>.</summary>
    public static string DictionaryEdition => DicomDictionary.Edition;

    /// <summary>Reads a DICOM file from a path.</summary>
    /// <param name="path">The file.</param>
    /// <param name="context">The context whose safety limits apply, or <see langword="null"/> for the default.</param>
    /// <param name="options">How to read: <see cref="ReaderOptions.Recovery"/> chooses between keeping what was read before damage and throwing.</param>
    /// <returns>The file.</returns>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Dicom.cs" region="dicom-load"/>
    /// </example>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="CorruptDataException">The data is not DICOM, or is damaged and the read is strict.</exception>
    /// <exception cref="LimitExceededException">The file breaks a safety limit.</exception>
    public static DicomFile Load(string path, EgamiContext? context = null, ReaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        using FileStream stream = File.OpenRead(path);
        return Load(stream, context, options);
    }

    /// <summary>Reads a DICOM file from a stream, to its end.</summary>
    /// <param name="stream">The stream. It is left open.</param>
    /// <param name="context">The context whose safety limits apply, or <see langword="null"/> for the default.</param>
    /// <param name="options">How to read: <see cref="ReaderOptions.Recovery"/> chooses between keeping what was read before damage and throwing.</param>
    /// <returns>The file.</returns>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Dicom.cs" region="dicom-load"/>
    /// </example>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="CorruptDataException">The data is not DICOM, or is damaged and the read is strict.</exception>
    /// <exception cref="LimitExceededException">The file breaks a safety limit.</exception>
    public static DicomFile Load(Stream stream, EgamiContext? context = null, ReaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using EgamiInput input = new(stream, context ?? EgamiContext.Default, default);
        return Read(input, options ?? ReaderOptions.Default);
    }

    /// <summary>Reads a DICOM file held in memory. The file keeps referring to <paramref name="data"/> instead of copying it.</summary>
    /// <param name="data">The file's bytes.</param>
    /// <param name="context">The context whose safety limits apply, or <see langword="null"/> for the default.</param>
    /// <param name="options">How to read: <see cref="ReaderOptions.Recovery"/> chooses between keeping what was read before damage and throwing.</param>
    /// <returns>The file.</returns>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Dicom.cs" region="dicom-load"/>
    /// </example>
    /// <exception cref="CorruptDataException">The data is not DICOM, or is damaged and the read is strict.</exception>
    /// <exception cref="LimitExceededException">The file breaks a safety limit.</exception>
    public static DicomFile Load(ReadOnlyMemory<byte> data, EgamiContext? context = null, ReaderOptions? options = null)
    {
        using EgamiInput input = new(Stream.Null, context ?? EgamiContext.Default, default);
        input.CheckMemory(data.Length);
        return new DicomFile(DicomParser.Parse(data, input, options ?? ReaderOptions.Default)).Link();
    }

    /// <summary>
    /// Decodes one frame of the file's pixel data, as <see cref="Raster.Load(Stream, EgamiContext?, ReaderOptions?)"/> of the
    /// file would, without reading the file again.
    /// </summary>
    /// <param name="frame">The frame, from 0; it replaces <see cref="DicomReaderOptions.Frame"/>.</param>
    /// <param name="options">What to give and how, or <see langword="null"/> for what a viewer shows.</param>
    /// <param name="context">The context whose safety limits apply, or <see langword="null"/> for the default.</param>
    /// <returns>The frame, in the pixel type that fits it (see <see cref="DicomValues"/>).</returns>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Dicom.cs" region="dicom-frames"/>
    /// </example>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="frame"/> is negative or not below <see cref="FrameCount"/>.</exception>
    /// <exception cref="UnsupportedFormatException">The dataset has no image or no pixel data.</exception>
    /// <exception cref="NotSupportedException">The pixel data is in a transfer syntax or layout that is not decoded, such as MPEG.</exception>
    /// <exception cref="CorruptDataException">The pixel data is damaged and the read is strict, or cannot be read at all.</exception>
    /// <exception cref="LimitExceededException">The frame breaks a safety limit.</exception>
    public Raster LoadFrame(int frame = 0, DicomReaderOptions? options = null, EgamiContext? context = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frame);
        DicomReaderOptions read = (options ?? new DicomReaderOptions()) with { Frame = frame };
        using EgamiInput input = new(Stream.Null, context ?? EgamiContext.Default, default);
        using RowReader rows = DicomReader.Open(input, this, read);
        return ((IWholeRead)rows).ReadNatural(input);
    }

    /// <summary>
    /// Writes the file again in the transfer syntax of <paramref name="options"/>, keeping every element (group lengths, which
    /// would no longer be right, are left out). Pixel data already in that transfer syntax, or native in a little-endian file
    /// written native, is copied as it is; any other is decoded and encoded again, with the photometric interpretation and bit
    /// depth it then has. A lossy transfer syntax records itself in Lossy Image Compression, its ratio and method, and gives the
    /// image a new SOP Instance UID (PS3.3 C.7.6.1.1.5). The file meta information is made anew, keeping its other elements.
    /// </summary>
    /// <param name="stream">Where to write. It is left open.</param>
    /// <param name="options">The transfer syntax and its settings, or <see langword="null"/> for Explicit VR Little Endian.</param>
    /// <param name="context">The context whose safety limits apply while the pixel data is decoded, or <see langword="null"/> for the default.</param>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Dicom.cs" region="dicom-transcode"/>
    /// </example>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">
    /// The pixel data cannot be decoded, or the transfer syntax cannot hold it, such as signed values in JPEG Baseline or more than
    /// 8 bits in lossy JPEG XL.
    /// </exception>
    /// <exception cref="CorruptDataException">The pixel data is damaged.</exception>
    public void Save(Stream stream, DicomWriterOptions? options = null, EgamiContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        DicomFileWriter.Transcode(this, stream, options ?? DicomWriterOptions.Default, context ?? EgamiContext.Default);
    }

    /// <summary>Writes the file again to a path, as <see cref="Save(Stream, DicomWriterOptions?, EgamiContext?)"/> does.</summary>
    /// <param name="path">The file to write.</param>
    /// <param name="options">The transfer syntax and its settings, or <see langword="null"/> for Explicit VR Little Endian.</param>
    /// <param name="context">The context whose safety limits apply while the pixel data is decoded, or <see langword="null"/> for the default.</param>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Dicom.cs" region="dicom-transcode"/>
    /// </example>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">The pixel data cannot be decoded, or the transfer syntax cannot hold it.</exception>
    /// <exception cref="CorruptDataException">The pixel data is damaged.</exception>
    public void Save(string path, DicomWriterOptions? options = null, EgamiContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        using MemoryStream buffer = new();
        Save(buffer, options, context);
        using FileStream file = File.Create(path);
        buffer.Position = 0;
        buffer.CopyTo(file);
    }

    /// <summary>Reads the rest of an input as a DICOM file.</summary>
    internal static DicomFile Read(EgamiInput input, ReaderOptions options)
    {
        byte[] data = InputData.ReadToEnd(input, Array.MaxLength, out int length);
        return new DicomFile(DicomParser.Parse(data.AsMemory(0, length), input, options)).Link();
    }

    /// <summary>Gives the pixel data's fragments the Extended Offset Table of the dataset they are in.</summary>
    private DicomFile Link()
    {
        if (Dataset[DicomTag.PixelData]?.Fragments is DicomFragments fragments)
        {
            if (Dataset[new DicomTag(0x7FE0, 0x0001)] is { VR: DicomVR.OV or DicomVR.UN or DicomVR.OB } offsets)
            {
                fragments.ExtendedOffsetTable = U64(offsets);
            }

            if (Dataset[new DicomTag(0x7FE0, 0x0002)] is { VR: DicomVR.OV or DicomVR.UN or DicomVR.OB } lengths)
            {
                fragments.ExtendedOffsetTableLengths = U64(lengths);
            }
        }

        return this;
    }

    /// <summary>Reads 64-bit little-endian words (encapsulated data is always little endian).</summary>
    private static ulong[] U64(DicomElement element)
    {
        ReadOnlySpan<byte> bytes = element.RawValue.Span;
        ulong[] values = new ulong[bytes.Length / 8];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes[(i * 8)..]);
        }

        return values;
    }
}
