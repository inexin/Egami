using Egami.Formats.Avif;
using Egami.Formats.Heif;

namespace Egami.Formats.Heic;

/// <summary>
/// A HEIC file's still image: the HEIF image (<see cref="HeifImage"/>) with hvc1 items, and the hvcC record of its (first) coded
/// item. Reading it parses the container only; decoding the HEVC items is Egami.Heic's (2.9.4 design, questions 7 and 10).
/// </summary>
internal sealed class HeicFile : HeifImage
{
    internal static readonly uint Hvc1 = FourCC.Of("hvc1");
    internal static readonly uint HvcC = FourCC.Of("hvcC");

    /// <summary>The property types the HEIC reader understands, so an essential one of them does not make an item unusable.</summary>
    internal static readonly HashSet<uint> Understood =
        [HvcC, AvifProperties.Ispe, AvifProperties.Pixi, AvifProperties.Colr, AvifProperties.AuxC, AvifProperties.Clli, AvifProperties.Mdcv,
         AvifProperties.Irot, AvifProperties.Imir, AvifProperties.Clap, AvifProperties.Pasp, AvifProperties.Cclv, AvifProperties.Amve];

    private HeicFile(HeifFile heif, HeifItem primary)
        : base(heif, primary)
    {
    }

    /// <summary>Gets the hvcC record of the (first cell of the) primary image.</summary>
    internal HevcConfig Config { get; private set; } = null!;

    /// <summary>Gets the larger of hvcC's luma and chroma bit depths.</summary>
    internal override int BitDepth => Math.Max(Config.LumaBitDepth, Config.ChromaBitDepth);

    internal override bool Monochrome => Config.ChromaFormatIdc == 0;

    internal override int SubsamplingX => Config.ChromaFormatIdc is 1 or 2 ? 1 : 0;

    internal override int SubsamplingY => Config.ChromaFormatIdc == 1 ? 1 : 0;

    /// <summary>Gets the siting: left for 4:2:0, HEVC's default chroma sample location (type 0), and centered otherwise.</summary>
    internal override Pixels.ChromaSiting ChromaSiting => Config.ChromaFormatIdc == 1 ? Pixels.ChromaSiting.Left : Pixels.ChromaSiting.Center;

    private protected override uint CodedType => Hvc1;

    private protected override string DiagnosticPrefix => "Heic";

    /// <summary>
    /// Gets the color when there is no nclx property: unspecified until the decoder reads the SPS's VUI, which a converter treats
    /// as BT.601 limited range, as HEVC's default.
    /// </summary>
    private protected override Colors.ColorCodePoints StreamCodePoints => new(2, 2, 2, false);

    /// <summary>Reads the input into memory and finds the still image.</summary>
    internal static HeicFile Read(EgamiInput input) => Open(HeifFile.Read(input));

    /// <summary>Finds the still image of a parsed container.</summary>
    internal static HeicFile Open(HeifFile heif)
    {
        HeicFile heic = new(heif, FindPrimary(heif, Understood.Contains, Hvc1, AvifFile.Grid, AvifFile.Iovl));
        heic.Resolve();
        return heic;
    }

    private protected override void ReadCoding(HeifItem first)
    {
        HeifFile heif = Heif;
        Config = heif.Property(first, HvcC) is HeifProperty hvcC
            ? HevcConfig.Read(heif, hvcC)
            : throw Corrupt(heif, $"{Capital(first)} has no hvcC property.");
    }
}
