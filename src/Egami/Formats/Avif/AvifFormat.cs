using System.Buffers.Binary;

using Egami.Colors;
using Egami.Formats.Heif;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Avif;

/// <summary>The AVIF format (AV1 Image File Format), registered in <see cref="EgamiContext.Default"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Avif.cs" region="probe"/>
/// </example>
public static class AvifFormat
{
    /// <summary>Gets the format's registration: name, MIME type, extension, signature, reader and writer.</summary>
    public static FormatInfo Info { get; } = new()
    {
        Name = "AVIF",
        MimeTypes = ["image/avif"],
        Extensions = ["avif"],
        SignatureLength = 64,
        Matches = Matches,
        Reader = AvifReader.Instance,
        Writer = AvifWriter.Instance,
    };

    /// <summary>
    /// Gets whether the data starts with an ftyp box whose major or compatible brands include avif or avis, or, for a mini file (major
    /// brand mif3), whose minor version is avif, where such a file names its codec. A HEIF file without those brands, such as HEIC,
    /// is left to other readers.
    /// </summary>
    internal static bool Matches(ReadOnlySpan<byte> header)
    {
        if (header.Length < 12 || BinaryPrimitives.ReadUInt32BigEndian(header[4..]) != IsoTypes.Ftyp)
        {
            return false;
        }

        uint size = BinaryPrimitives.ReadUInt32BigEndian(header);
        int end = (int)Math.Min(size < 16 ? 12 : size, (uint)header.Length);
        bool mini = header.Length >= 16 && BinaryPrimitives.ReadUInt32BigEndian(header[8..]) == HeifMini.Mif3;
        for (int at = 8; at + 4 <= end; at += 4)
        {
            // Byte 12 holds the minor version, not a brand, except that a mini file names its codec there (AVIF extras question 6).
            uint brand = BinaryPrimitives.ReadUInt32BigEndian(header[at..]);
            if ((at != 12 || mini) && (brand == AvifFile.Avif || brand == AvifFile.Avis))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Details only AVIF has, kept in <see cref="RasterMetadata.FormatDetails"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Avif.cs" region="probe"/>
/// </example>
public sealed class AvifDetails : FormatDetails
{
    /// <summary>Gets or sets the ftyp box's major brand, such as avif or avis.</summary>
    public string MajorBrand { get; set; } = "avif";

    /// <summary>Gets or sets the ftyp box's compatible brands, such as mif1, miaf and MA1B.</summary>
    public IReadOnlyList<string> CompatibleBrands { get; set; } = [];

    /// <summary>Gets or sets the AV1 profile: 0 (Main), 1 (High, 4:4:4) or 2 (Professional, 4:2:2 and 12 bits).</summary>
    public int Profile { get; set; }

    /// <summary>Gets or sets the AV1 level, as seq_level_idx: 0 is level 2.0, 1 is 2.1, and so on; 31 means no level.</summary>
    public int Level { get; set; }

    /// <summary>Gets or sets the AV1 tier: 0 (Main) or 1 (High).</summary>
    public int Tier { get; set; }

    /// <summary>
    /// Gets or sets the bits a sample of the image Load gave has: 8, 10 or 12 as AV1 codes them, or the depth of a sample
    /// transform's output (16 in the files avifenc --depth 16 writes) when <see cref="HasSampleTransform"/> is true.
    /// </summary>
    public int BitDepth { get; set; } = 8;

    /// <summary>
    /// Gets or sets whether the image Load gave is the output of a sample transform derived image (a sato item, AVIF 1.2.0), which
    /// combines several AV1 images into one of more bits (design question 4); <see cref="AvifReaderOptions.SampleTransform"/> set
    /// to false loads the file's fallback image instead.
    /// </summary>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Avif.cs" region="sato"/>
    /// </example>
    public bool HasSampleTransform { get; set; }

    /// <summary>Gets or sets the chroma format: 4:0:0 (monochrome), 4:2:0, 4:2:2 or 4:4:4.</summary>
    public ChromaFormat Chroma { get; set; } = ChromaFormat.Yuv420;

    /// <summary>
    /// Gets or sets where 4:2:0 chroma sits, as the AV1 sequence header says: <see cref="ChromaSiting.Left"/> (vertical) or
    /// <see cref="ChromaSiting.TopLeft"/> (colocated); <see langword="null"/> when it is unknown or the image is not 4:2:0.
    /// </summary>
    public ChromaSiting? ChromaSiting { get; set; }

    /// <summary>
    /// Gets or sets the color code points (ITU-T H.273) and range: the nclx colr property's when there is one, otherwise the AV1
    /// sequence header's. With an ICC profile too, the matrix and range still come from here (design question 8).
    /// </summary>
    public ColorCodePoints CodePoints { get; set; } = new(2, 2, 2, false);

    /// <summary>Gets or sets whether the image has an alpha item.</summary>
    public bool HasAlpha { get; set; }

    /// <summary>
    /// Gets or sets whether the primary image's AV1 frame is coded losslessly (every segment at quantizer 0), so saving it again
    /// writes lossless unless <see cref="AvifWriterOptions.Lossless"/> says otherwise.
    /// </summary>
    public bool Lossless { get; set; }

    /// <summary>Gets or sets whether the color is premultiplied by the alpha (a prem item reference). Egami loads straight alpha.</summary>
    public bool AlphaPremultiplied { get; set; }

    /// <summary>Gets or sets the rows of a grid image, or 1.</summary>
    public int GridRows { get; set; } = 1;

    /// <summary>Gets or sets the columns of a grid image, or 1.</summary>
    public int GridColumns { get; set; } = 1;

    /// <summary>
    /// Gets or sets whether the image is layered for progressive display (a1lx without an lsel picking one layer). Load gives the
    /// best layer, or the one <see cref="AvifReaderOptions.Layer"/> picks, at the image's full size.
    /// </summary>
    public bool IsProgressive { get; set; }

    /// <summary>
    /// Gets or sets the size of each layer of a layered image, in the order they decode (the first is the lowest quality), as Load
    /// found them; empty when the image is not layered.
    /// </summary>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Avif.cs" region="layers"/>
    /// </example>
    public IReadOnlyList<Size> Layers { get; set; } = [];

    /// <summary>Gets or sets whether the file is an image sequence (the avis brand).</summary>
    public bool IsSequence { get; set; }

    /// <summary>Gets or sets whether the AV1 stream carries film grain parameters.</summary>
    public bool HasFilmGrain { get; set; }

    /// <summary>
    /// Gets or sets the primary image's film grain parameters as its AV1 frame header codes them, or <see langword="null"/> when it
    /// applies none. Load adds the grain unless <see cref="AvifReaderOptions.FilmGrain"/> is false (design question 6).
    /// </summary>
    public AvifFilmGrain? FilmGrain { get; set; }

    /// <summary>
    /// Gets or sets every item of the file with what it is to the primary image (design question 13); read an image item with
    /// <see cref="AvifImage.ReadItem(string, uint, ReaderOptions?, EgamiContext?)"/>. Empty for a sequence read from its tracks.
    /// </summary>
    public IReadOnlyList<AvifItem> Items { get; set; } = [];

    /// <inheritdoc/>
    public override FormatDetails Copy()
    {
        return new AvifDetails
        {
            MajorBrand = MajorBrand,
            CompatibleBrands = CompatibleBrands,
            Profile = Profile,
            Level = Level,
            Tier = Tier,
            BitDepth = BitDepth,
            HasSampleTransform = HasSampleTransform,
            Chroma = Chroma,
            ChromaSiting = ChromaSiting,
            CodePoints = CodePoints,
            HasAlpha = HasAlpha,
            Lossless = Lossless,
            AlphaPremultiplied = AlphaPremultiplied,
            GridRows = GridRows,
            GridColumns = GridColumns,
            IsProgressive = IsProgressive,
            Layers = Layers,
            IsSequence = IsSequence,
            HasFilmGrain = HasFilmGrain,
            FilmGrain = FilmGrain,
            Items = Items,
        };
    }

    /// <summary>Gets the details of a file whose structure has been read.</summary>
    internal static AvifDetails From(AvifFile file)
    {
        return new AvifDetails
        {
            MajorBrand = IsoTypes.ToString(file.Heif.MajorBrand),
            CompatibleBrands = [.. file.Heif.CompatibleBrands.Select(IsoTypes.ToString)],
            Profile = file.Config.Profile,
            Level = file.Config.Level,
            Tier = file.Config.Tier,
            BitDepth = file.BitDepth,
            HasSampleTransform = file.SampleTransform is not null,
            Chroma = file.Config.Chroma,
            ChromaSiting = file.Config.Chroma != ChromaFormat.Yuv420 ? null : file.Sequence.ChromaSamplePosition switch
            {
                1 => Pixels.ChromaSiting.Left,
                2 => Pixels.ChromaSiting.TopLeft,
                _ => null,
            },
            CodePoints = file.CodePoints,
            HasAlpha = file.Alpha is not null,
            AlphaPremultiplied = file.AlphaPremultiplied,
            GridRows = file.GridSize.Rows,
            GridColumns = file.GridSize.Columns,
            IsProgressive = file.Progressive,
            IsSequence = file.Heif.HasBrand(AvifFile.Avis),
            HasFilmGrain = file.Sequence.FilmGrainParamsPresent,
            Items = AvifImage.Items(file),
        };
    }
}

/// <summary>Options for reading AVIF.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Avif.cs" region="options"/>
/// </example>
public sealed record AvifReaderOptions : ReaderOptions
{
    private readonly int? _layer;

    /// <summary>
    /// Gets whether the film grain an AV1 image carries is added to its pixels, as the AV1 specification's output process does
    /// (the default, true), or left out, giving the image as coded; the parameters are in <see cref="AvifDetails.FilmGrain"/>
    /// either way.
    /// </summary>
    public bool FilmGrain { get; init; } = true;

    /// <summary>
    /// Gets the layer of a layered (progressive) image to load, counted from 0 for the first and lowest quality one in decoding
    /// order, or <see langword="null"/> (the default) for the layer the file picks with its lsel property, else the best. A layer
    /// past the last gives the last; a layer smaller than the image is scaled up to its full size. Images that are not layered
    /// ignore it; <see cref="AvifDetails.Layers"/> lists the layers.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Avif.cs" region="layers"/>
    /// </example>
    public int? Layer
    {
        get => _layer;
        init => _layer = value is null or >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The layer is 0 or more.");
    }

    /// <summary>
    /// Gets whether a sample transform derived image (a sato item, AVIF 1.2.0) that the file prefers to its primary image is applied,
    /// giving its output at its own depth, such as 16 bits from a 12-bit image and an 8-bit residual (the default, true), or left
    /// out, giving the primary image the file keeps as the fallback for readers without sample transforms.
    /// </summary>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Avif.cs" region="sato"/>
    /// </example>
    public bool SampleTransform { get; init; } = true;
}

/// <summary>
/// An AV1 frame's film grain parameters (AV1 specification 5.9.30 and 6.8.20): what the decoder's grain synthesis adds to the
/// output, kept so an image loaded without its grain can have it added again or described.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Avif.cs" region="options"/>
/// </example>
public sealed record AvifFilmGrain
{
    /// <summary>Gets the random seed.</summary>
    public int RandomSeed { get; init; }

    /// <summary>Gets the piecewise-linear scaling function for luma: (value, scaling) points with increasing values.</summary>
    public IReadOnlyList<(int Value, int Scaling)> ScalingPointsY { get; init; } = [];

    /// <summary>Gets the scaling function for Cb.</summary>
    public IReadOnlyList<(int Value, int Scaling)> ScalingPointsCb { get; init; } = [];

    /// <summary>Gets the scaling function for Cr.</summary>
    public IReadOnlyList<(int Value, int Scaling)> ScalingPointsCr { get; init; } = [];

    /// <summary>Gets whether chroma grain is scaled by the luma scaling function.</summary>
    public bool ChromaScalingFromLuma { get; init; }

    /// <summary>Gets the scaling shift, 8 to 11.</summary>
    public int ScalingShift { get; init; }

    /// <summary>Gets the autoregressive filter's lag, 0 to 3.</summary>
    public int ArCoeffLag { get; init; }

    /// <summary>Gets the luma autoregressive coefficients, from -128 to 127.</summary>
    public IReadOnlyList<int> ArCoeffsY { get; init; } = [];

    /// <summary>Gets the Cb autoregressive coefficients, the last weighting luma when there is luma grain.</summary>
    public IReadOnlyList<int> ArCoeffsCb { get; init; } = [];

    /// <summary>Gets the Cr autoregressive coefficients.</summary>
    public IReadOnlyList<int> ArCoeffsCr { get; init; } = [];

    /// <summary>Gets the autoregressive coefficients' shift, 6 to 9.</summary>
    public int ArCoeffShift { get; init; }

    /// <summary>Gets the shift that scales the Gaussian samples down, 0 to 3.</summary>
    public int GrainScaleShift { get; init; }

    /// <summary>Gets the Cb multipliers and offset as coded: cb_mult, cb_luma_mult and cb_offset.</summary>
    public (int Mult, int LumaMult, int Offset) Cb { get; init; }

    /// <summary>Gets the Cr multipliers and offset as coded: cr_mult, cr_luma_mult and cr_offset.</summary>
    public (int Mult, int LumaMult, int Offset) Cr { get; init; }

    /// <summary>Gets whether grain blocks are blended where they overlap.</summary>
    public bool Overlap { get; init; }

    /// <summary>Gets whether the output is clipped to the video range.</summary>
    public bool ClipToRestrictedRange { get; init; }
}

/// <summary>
/// Options for writing AVIF. Lossy images are coded on libavif's quality scale, so the same <see cref="Quality"/> gives about the
/// same file as avifenc -q; lossless images keep every sample of 4:4:4 RGB.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Avif.cs" region="write"/>
/// </example>
public sealed class AvifWriterOptions : WriterOptions
{
    private readonly int _quality = 60;
    private readonly int _effort = 4;
    private readonly int _alphaQuality = 100;
    private readonly int? _bitDepth;
    private readonly Size? _gridTileSize;
    private readonly IReadOnlyList<AvifLayer>? _layers;
    private readonly int? _targetSize;
    private readonly int? _gainMapQuality;
    private readonly int _filmGrainDenoise;

    /// <summary>Gets the default options.</summary>
    public static new AvifWriterOptions Default { get; } = new();

    /// <summary>
    /// Gets the lossy quality, 0 (smallest) to 100 (best), on avifenc's -q scale. Defaults to 60. Ignored for lossless images.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 100.</exception>
    public int Quality
    {
        get => _quality;
        init => _quality = InRange(value, 0, 100);
    }

    /// <summary>
    /// Gets how hard the encoder works, 0 (fastest) to 10 (smallest files): 10 minus avifenc's speed, so the default 4 matches
    /// avifenc's default speed 6.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 10.</exception>
    public int Effort
    {
        get => _effort;
        init => _effort = InRange(value, 0, 10);
    }

    /// <summary>
    /// Gets whether to write lossless (every sample of the RGB or gray image exact, 4:4:4 with the identity matrix) or lossy
    /// data, or <see langword="null"/> (the default) to keep the kind of the AVIF file the raster was read from, and write lossy
    /// otherwise.
    /// </summary>
    public bool? Lossless { get; init; }

    /// <summary>
    /// Gets the chroma format of lossy color, or <see langword="null"/> (the default) to choose: 4:0:0 for gray images, 4:4:4 from
    /// Quality 90 up, else 4:2:0. Lossless images are always 4:4:4 (or 4:0:0 when gray). AV1 has no 4:1:1 or 4:1:0, so those
    /// choose as <see langword="null"/> does.
    /// </summary>
    public ChromaFormat? Chroma { get; init; }

    /// <summary>
    /// Gets the bits a sample has, 8, 10, 12 or 16, or <see langword="null"/> (the default) for 8 with 8-bit pixels and 10 with
    /// deeper ones; a lossless file of deeper pixels takes the depth of the AVIF file they were read from, else 12. 16 bits, which
    /// AV1 cannot code, are written as a sample transform (AVIF 1.2.0): lossless as an 8-bit image of the high bytes and a hidden
    /// 8-bit image of the low bytes, lossy as a 12-bit image and a hidden 8-bit residual that overlaps it by 4 bits. Readers without
    /// sample transforms show the 8- or 12-bit image.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not 8, 10, 12 or 16.</exception>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Avif.cs" region="sato"/>
    /// </example>
    public int? BitDepth
    {
        get => _bitDepth;
        init => _bitDepth = value is null or 8 or 10 or 12 or 16 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The bit depth is 8, 10, 12 or 16.");
    }

    /// <summary>
    /// Gets the quality of the alpha plane, 0 to 100 on the same scale as <see cref="Quality"/>: 100 (the default) keeps alpha
    /// exactly. Alpha is written straight, not premultiplied.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 100.</exception>
    public int AlphaQuality
    {
        get => _alphaQuality;
        init => _alphaQuality = InRange(value, 0, 100);
    }

    /// <summary>
    /// Gets the size of the cells of a grid image, or <see langword="null"/> (the default) to write one frame up to 8192 x 4352
    /// and a grid of equal cells only for larger images.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A side is below 64 or above 8192.</exception>
    public Size? GridTileSize
    {
        get => _gridTileSize;
        init => _gridTileSize = value is not Size size || (size.Width is >= 64 and <= 8192 && size.Height is >= 64 and <= 8192)
            ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "Grid cells are 64 to 8192 samples a side.");
    }

    /// <summary>
    /// Gets whether to write a progressive image of two layers, as avifenc --progressive does: a small first layer at quality 2,
    /// which a viewer can show while the rest arrives, then the image at <see cref="Quality"/>. Defaults to false. Ignored when
    /// <see cref="Layers"/> is set, and below Quality 10, where a first layer saves nothing.
    /// </summary>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Avif.cs" region="layers"/>
    /// </example>
    public bool Progressive { get; init; }

    /// <summary>
    /// Gets the layers of a progressive image, 1 to 4, from the first and smallest to the last, each with its own quality and scale;
    /// <see langword="null"/> (the default) for one layer, or the two <see cref="Progressive"/> makes. The last layer is the
    /// image at full size; when the image is lossless it is coded lossless. Images larger than one AV1 frame (written as a grid)
    /// are written in one layer.
    /// </summary>
    /// <exception cref="ArgumentException">The list is empty, has more than 4 layers, or its last layer is not at scale 1.</exception>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Avif.cs" region="layers"/>
    /// </example>
    public IReadOnlyList<AvifLayer>? Layers
    {
        get => _layers;
        init => _layers = value is null ? null
            : value.Count is 0 or > 4 ? throw new ArgumentException("A layered image has 1 to 4 layers.", nameof(value))
            : value[^1].Scale != 1 ? throw new ArgumentException("The last layer is the image at full size, scale 1.", nameof(value))
            : [.. value];
    }

    /// <summary>
    /// Gets the largest size the file may have, in bytes, or <see langword="null"/> (the default) to code at <see cref="Quality"/>:
    /// the quality is searched, as avifenc --target-size searches it, for the largest file not above the size. When even quality 0
    /// is larger, that file is written and an Avif.TargetSizeExceeded warning says so. Ignored for lossless images.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is below 1.</exception>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Avif.cs" region="target-size"/>
    /// </example>
    public int? TargetSize
    {
        get => _targetSize;
        init => _targetSize = value is null or >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The target size is 1 byte or more.");
    }

    /// <summary>
    /// Gets the quality of a gain map, 0 to 100 on the same scale as <see cref="Quality"/>, as avifenc --qgain-map sets it, or
    /// <see langword="null"/> (the default) for the image's quality.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 100.</exception>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Avif.cs" region="write"/>
    /// </example>
    public int? GainMapQuality
    {
        get => _gainMapQuality;
        init => _gainMapQuality = value is int q ? InRange(q, 0, 100) : null;
    }

    /// <summary>
    /// Gets how strongly to take film grain out before coding and put it back on decoding, 0 (the default, off) to 50, as libaom's
    /// denoise-noise-level: the image is denoised, the grain measured and written as AV1 film grain parameters that decoders add
    /// back, so the grain costs few bits. A raster loaded without its grain (<see cref="AvifReaderOptions.FilmGrain"/> false)
    /// gets its <see cref="AvifDetails.FilmGrain"/> written back when this is 0. Ignored for lossless images.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 50.</exception>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Avif.cs" region="grain"/>
    /// </example>
    public int FilmGrainDenoise
    {
        get => _filmGrainDenoise;
        init => _filmGrainDenoise = InRange(value, 0, 50);
    }

    private static int InRange(int value, int min, int max)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, min);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, max);
        return value;
    }
}

/// <summary>One layer of a progressive AVIF image (<see cref="AvifWriterOptions.Layers"/>): its quality and its scale.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Avif.cs" region="layers"/>
/// </example>
public sealed record AvifLayer
{
    private readonly int _quality = 60;
    private readonly double _scale = 1;

    /// <summary>Gets the layer's quality, 0 to 100 on <see cref="AvifWriterOptions.Quality"/>'s scale. Defaults to 60.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 100.</exception>
    public int Quality
    {
        get => _quality;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 0);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 100);
            _quality = value;
        }
    }

    /// <summary>
    /// Gets the layer's size as a part of the image's, above 0 up to 1 (the default), as avifenc --scaling-mode sets it: a layer at
    /// 0.5 is coded at half the width and height and shown scaled up. A layer of another size than the one before it is coded on
    /// its own rather than from it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not above 0 and at most 1.</exception>
    public double Scale
    {
        get => _scale;
        init => _scale = value is > 0 and <= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The scale is above 0 and at most 1.");
    }
}
