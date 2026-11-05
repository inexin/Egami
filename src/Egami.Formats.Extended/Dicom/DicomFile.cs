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
