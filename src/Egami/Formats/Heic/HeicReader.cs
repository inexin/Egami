using System.Buffers.Binary;

using Egami.Formats.Avif;
using Egami.Formats.Heif;
using Egami.Pixels;

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
    internal static FormatInfo Info { get; } = Create(() => Info!, PackageNeeded);

    /// <summary>Makes a HEIC registration whose reader probes and, for now, refuses to load with <paramref name="loadMessage"/>.</summary>
    /// <param name="self">Gets the registration, which Probe reports as the format.</param>
    /// <param name="loadMessage">What Load throws.</param>
    internal static FormatInfo Create(Func<FormatInfo> self, string loadMessage) => new()
    {
        Name = "HEIC",
        MimeTypes = ["image/heic", "image/heif", "image/heic-sequence", "image/heif-sequence"],
        Extensions = ["heic", "heif", "hif", "heics", "heifs"],
        SignatureLength = 64,
        Matches = Matches,
        Reader = new HeicReader(self, loadMessage),
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

/// <summary>Reads a HEIC file's container for Probe; loading throws until a HEVC codec is plugged in.</summary>
/// <param name="format">Gets the registration Probe reports.</param>
/// <param name="loadMessage">What Load throws.</param>
internal sealed class HeicReader(Func<FormatInfo> format, string loadMessage) : IFormatReader
{
    public RasterInfo ReadInfo(EgamiInput input, ReaderOptions options)
    {
        HeicFile file = HeicFile.Read(input);
        if (input.Reports(DiagnosticLevel.Warning))
        {
            // A probe has no raster to list warnings on, so skipped entries go to the handler directly.
            foreach (HeifProblem problem in file.Heif.Problems)
            {
                input.Diagnostics.Report(DiagnosticLevel.Warning, problem.Id, problem.Message, problem.Offset);
            }
        }

        (int width, int height) = file.Crop is (_, _, int w, int h) ? (w, h) : (file.Width, file.Height);
        input.Limits.CheckFrameSize(width, height);
        HevcConfig config = file.Config;
        PixelLayoutInfo layout = AvifReader.NaturalLayout(Math.Max(config.LumaBitDepth, config.ChromaBitDepth), config.ChromaFormatIdc == 0, file.HasAlpha);
        return new RasterInfo
        {
            Format = format(),
            Width = width,
            Height = height,
            Layout = layout,
            FrameCount = 1,
            ImageCount = 1,
            HasMetadata = !file.Icc.IsEmpty || file.ExifItems.Count > 0 || file.XmpItems.Count > 0 || file.ContentLightLevel is not null
                || file.MasteringDisplay is not null,
            EstimatedBytes = (long)width * height * layout.BytesPerPixel,
            Orientation = file.Orientation,
            Access = RasterAccess.Streamed,
            WorkingBytes = WorkingBytes(file),
        };
    }

    public Raster Read(EgamiInput input, ReaderOptions options) => throw Unloadable(input);

    public Raster<TPixel> Read<TPixel>(EgamiInput input, ReaderOptions options)
        where TPixel : unmanaged, IPixelType<TPixel> => throw Unloadable(input);

    /// <summary>Reads the container, so damaged data is reported as such, then says why the image cannot load.</summary>
    private UnsupportedFormatException Unloadable(EgamiInput input)
    {
        HeicFile.Read(input);
        return new UnsupportedFormatException(loadMessage);
    }

    /// <summary>Gets the bytes a decode holds besides the raster: the planes of the image and of its alpha, at two bytes a sample.</summary>
    private static long WorkingBytes(HeicFile file)
    {
        HevcConfig config = file.Config;
        (int sx, int sy) = config.ChromaFormatIdc switch { 1 => (1, 1), 2 => (1, 0), _ => (0, 0) };
        long luma = (long)file.Width * file.Height;
        long chroma = config.ChromaFormatIdc == 0 ? 0 : 2L * ((file.Width + sx) >> sx) * ((file.Height + sy) >> sy);
        return 2 * (luma + chroma + (file.Alpha is null ? 0 : luma));
    }
}
