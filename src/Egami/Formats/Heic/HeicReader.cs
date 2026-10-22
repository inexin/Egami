using System.Buffers.Binary;

using Egami.Formats.Avif;
using Egami.Formats.Heif;
using Egami.Metadata;
using Egami.Pixels;
using Egami.Streaming;

namespace Egami.Formats.Heic;

/// <summary>
/// HEIC (HEIF with HEVC-coded images) as core Egami knows it: detected by its brands and probed from its container, so
/// <c>Raster.Probe</c> gives the size, depth and orientation, while loading needs the Egami.Heic
/// package, which holds the HEVC decoder and registers itself with <c>WithHeic()</c> (2.9.4 design, question 10). The core
/// package contains no HEVC code and never references Egami.Heic.
/// </summary>
internal static class HeicSupport
{
    /// <summary>The brands of HEVC-coded HEIF images and sequences (ISO/IEC 23008-12, Annex B).</summary>
    private static readonly uint[] HevcBrands = [.. new[] { "heic", "heix", "heim", "heis", "hevc", "hevx", "hevm", "hevs" }.Select(b => IsoTypes.Of(b))];

    /// <summary>Gets the message Load gives when the Egami.Heic package is not registered.</summary>
    internal const string PackageNeeded = "The data is HEIC, HEIF with HEVC-coded images. HEVC is covered by patents, so core Egami does not decode it: "
        + "add the Egami.Heic package and register it with WithHeic(), for example EgamiContext.SetDefault(new EgamiContext().WithHeic()) at startup.";

    /// <summary>Gets core Egami's HEIC registration: detection and Probe, with Load naming the package it needs.</summary>
    internal static FormatInfo Info { get; } = Create(() => Info!, PackageNeeded, null);

    /// <summary>
    /// Makes a HEIC registration. With <paramref name="codec"/> (Egami.Heic's HEVC decoder) it loads through the HEIF image layer;
    /// without, it probes and Load throws <paramref name="loadMessage"/>.
    /// </summary>
    /// <param name="self">Gets the registration, which Probe reports as the format.</param>
    /// <param name="loadMessage">What Load throws without a codec.</param>
    /// <param name="codec">Makes the HEVC codec for one read, or null.</param>
    internal static FormatInfo Create(Func<FormatInfo> self, string loadMessage, Func<ReaderOptions, IHeifCodec>? codec) => new()
    {
        Name = "HEIC",
        MimeTypes = ["image/heic", "image/heif", "image/heic-sequence", "image/heif-sequence"],
        Extensions = ["heic", "heif", "hif", "heics", "heifs"],
        SignatureLength = 64,
        Matches = Matches,
        Reader = new HeicReader(self, loadMessage, codec),
    };

    /// <summary>
    /// Gets whether the data starts with an ftyp box listing a HEVC brand (heic, heix, heim, heis, hevc, hevx, hevm or hevs). AVIF
    /// files, with avif or avis, are left to the AVIF reader. A file with only the general mif1 or msf1 brands is not taken: telling
    /// its codec needs the meta box, past the first bytes detection reads.
    /// </summary>
    internal static bool Matches(ReadOnlySpan<byte> header)
    {
        if (header.Length < 12 || BinaryPrimitives.ReadUInt32BigEndian(header[4..]) != IsoTypes.Ftyp)
        {
            return false;
        }

        uint size = BinaryPrimitives.ReadUInt32BigEndian(header);
        int end = (int)Math.Min(size < 16 ? 12 : size, (uint)header.Length);
        for (int at = 8; at + 4 <= end; at += 4)
        {
            // Byte 12 holds the minor version, not a brand.
            uint brand = BinaryPrimitives.ReadUInt32BigEndian(header[at..]);
            if (at == 12)
            {
                continue;
            }

            if (brand == AvifFile.Avif || brand == AvifFile.Avis)
            {
                return false;
            }

            if (Array.IndexOf(HevcBrands, brand) >= 0)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Reads HEIC through the HEIF image layer (<see cref="HeifImageReader"/>) with the HEVC codec Egami.Heic plugs in: grids (their
/// cells decoded in parallel), alpha, clap, irot and imir, Exif and XMP, Region and streaming rows, under AVIF's rules (2.9.4
/// design, question 12). Without a codec it reads the container for Probe and Load throws.
/// </summary>
/// <param name="format">Gets the registration Probe reports.</param>
/// <param name="loadMessage">What Load throws without a codec.</param>
/// <param name="codec">Makes the HEVC codec for one read, or null.</param>
internal sealed class HeicReader(Func<FormatInfo> format, string loadMessage, Func<ReaderOptions, IHeifCodec>? codec)
    : IFormatReader, IYuvFormatReader, IRowSourceReader
{
    /// <summary>The image layer; without a codec, making one throws why the image cannot load, after the container was read.</summary>
    private readonly HeifImageReader _layer = new(format, new HeifDiagnosticIds("Heic.MatrixApproximated", "Heic.MatrixUnknown", "Heic.ExifDamaged", "Heic.GainMapDropped",
        "Heic.SequenceFirstFrame", "Heic.FramesDropped", "Egami.Heic decodes intra pictures only"), codec ?? (_ => throw new UnsupportedFormatException(loadMessage)))
    {
        Converter = c => new HeicRgbConverter(c.Color, c.Alpha, c.CodePoints, c.Layer),
        Describe = (file, _, metadata) => metadata.FormatDetails = HeicDetails.From((HeicFile)file, metadata.CodePoints ?? file.CodePoints),
        TruncateFill = true,
    };

    public RasterInfo ReadInfo(EgamiInput input, ReaderOptions options)
    {
        HeifFile heif = HeifFile.Read(input);
        return HeicSequence.Find(heif) is HeicSequence sequence
            ? _layer.SequenceInfo(sequence, input)
            : _layer.Info(HeicFile.Open(heif), input, options);
    }

    public Raster Read(EgamiInput input, ReaderOptions options)
    {
        if (options.Hdr is HdrDecode hdr)
        {
            return HeifImageReader.ReadHdr(hdr, () => Read(input, options with { Hdr = null, ReadMetadata = true }));
        }

        HeifFile heif = HeifFile.Read(input);
        return HeicSequence.Find(heif) is HeicSequence sequence
            ? _layer.ReadSequence(input, sequence, options, _ => SequenceMetadata(sequence))
            : _layer.Read(input, HeicFile.Open(heif), options);
    }

    public Raster<TPixel> Read<TPixel>(EgamiInput input, ReaderOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (options.Hdr is not null)
        {
            Raster hdr = Read(input, options);
            if (hdr is Raster<TPixel> typed)
            {
                return typed;
            }

            using (hdr)
            {
                return hdr.As<TPixel>();
            }
        }

        HeifFile heif = HeifFile.Read(input);
        return HeicSequence.Find(heif) is HeicSequence sequence
            ? _layer.ReadSequence<TPixel>(input, sequence, options, _ => SequenceMetadata(sequence))
            : _layer.Read<TPixel>(input, HeicFile.Open(heif), options);
    }

    public RowSource Open(EgamiInput input, ReaderOptions options)
    {
        try
        {
            HeifFile heif = HeifFile.Read(input);
            if (HeicSequence.Find(heif) is HeicSequence sequence)
            {
                // A sequence is read whole, its frames served from the raster.
                return BufferedRowSource.From(_layer.ReadSequence(input, sequence, options, _ => SequenceMetadata(sequence)));
            }

            return _layer.Open(input, HeicFile.Open(heif), options);
        }
        catch
        {
            input.Dispose();
            throw;
        }
    }

    public YuvRaster ReadYuv(EgamiInput input, ReaderOptions options)
    {
        HeifFile heif = HeifFile.Read(input);
        return HeicSequence.Find(heif) is HeicSequence sequence
            ? _layer.SequenceYuv(input, sequence, options, _ => SequenceMetadata(sequence))
            : _layer.ReadYuv(input, HeicFile.Open(heif), options, "HEIC");
    }

    public (RasterInfo Info, ChromaFormat? Format, int BitDepth, YuvColor Color) ProbeYuv(EgamiInput input, ReaderOptions options)
    {
        HeifFile heif = HeifFile.Read(input);
        if (HeicSequence.Find(heif) is HeicSequence sequence)
        {
            RasterInfo first = _layer.SequenceInfo(sequence, input);
            return (first, sequence.Config.Chroma, sequence.BitDepth, _layer.PlaneColor(sequence.CodePoints, sequence.ChromaSiting, null));
        }

        return _layer.ProbeYuv(HeicFile.Open(heif));
    }

    /// <summary>Gathers a sequence's metadata: its ICC profile and color, the details, and the container's warnings.</summary>
    private static RasterMetadata SequenceMetadata(HeicSequence sequence)
    {
        RasterMetadata metadata = new()
        {
            FormatName = "HEIC",
            IccProfile = sequence.Icc,
            CodePoints = sequence.CodePoints,
            FormatDetails = sequence.Details(sequence.CodePoints),
        };
        foreach (HeifProblem problem in sequence.Problems)
        {
            metadata.WarningList.Add(problem.Id, problem.Message, problem.Offset);
        }

        return metadata;
    }

    /// <summary>
    /// Reads image item <paramref name="itemId"/> (see <see cref="HeicDetails.Items"/>): an hvc1 item or a grid, converted with its
    /// own color, its own clean aperture cropped, and its own rotation and mirroring as the orientation.
    /// </summary>
    internal Raster ReadItem(EgamiInput input, ReaderOptions options, uint itemId) =>
        _layer.ReadItem(input, HeicFile.Read(input), itemId, options, HeicFile.Hvc1, "hvc1");

}
