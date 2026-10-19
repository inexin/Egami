using System.Buffers.Binary;

using Egami.Formats.Avif;
using Egami.Formats.Heif;
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
    private static readonly uint[] HevcBrands = [.. new[] { "heic", "heix", "heim", "heis", "hevc", "hevx", "hevm", "hevs" }.Select(b => FourCC.Of(b))];

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
        if (header.Length < 12 || BinaryPrimitives.ReadUInt32BigEndian(header[4..]) != FourCC.Ftyp)
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
    private readonly HeifImageReader _layer = new(format, new HeifDiagnosticIds("Heic.MatrixApproximated", "Heic.MatrixUnknown", "Heic.ExifDamaged", "Heic.GainMapDropped"), codec ?? (_ => throw new UnsupportedFormatException(loadMessage)))
    {
        Converter = c => new HeicRgbConverter(c.Color, c.Alpha, c.CodePoints, c.Layer),
        TruncateFill = true,
    };

    public RasterInfo ReadInfo(EgamiInput input, ReaderOptions options) => _layer.Info(HeicFile.Read(input), input, options);

    public Raster Read(EgamiInput input, ReaderOptions options)
    {
        if (options.Hdr is HdrDecode hdr)
        {
            return HeifImageReader.ReadHdr(hdr, () => Read(input, options with { Hdr = null, ReadMetadata = true }));
        }

        HeicFile file = HeicFile.Read(input);
        return _layer.Read(input, file, options);
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

        HeicFile file = HeicFile.Read(input);
        return _layer.Read<TPixel>(input, file, options);
    }

    public RowSource Open(EgamiInput input, ReaderOptions options)
    {
        try
        {
            HeicFile file = HeicFile.Read(input);
            return _layer.Open(input, file, options);
        }
        catch
        {
            input.Dispose();
            throw;
        }
    }

    public YuvRaster ReadYuv(EgamiInput input, ReaderOptions options)
    {
        HeicFile file = HeicFile.Read(input);
        return _layer.ReadYuv(input, file, options, "HEIC");
    }

    public (RasterInfo Info, ChromaFormat? Format, int BitDepth, YuvColor Color) ProbeYuv(EgamiInput input, ReaderOptions options)
    {
        return _layer.ProbeYuv(HeicFile.Read(input));
    }
}
