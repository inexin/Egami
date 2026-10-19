using Egami.Formats.Av1;
using Egami.Formats.Heif;

namespace Egami.Formats.Avif;

/// <summary>
/// An AVIF file's still image: the HEIF image (<see cref="HeifImage"/>) with av01 items, and the AV1 configuration and sequence
/// header of its (first) coded item. Reading it decodes nothing. The AV1 decoder and the AVIF reader build on it.
/// </summary>
internal sealed class AvifFile : HeifImage
{
    internal static readonly uint Av01 = FourCC.Of("av01");
    internal static readonly uint Avif = FourCC.Of("avif");
    internal static readonly uint Avis = FourCC.Of("avis");

    private AvifFile(HeifFile heif, HeifItem primary)
        : base(heif, primary)
    {
    }

    /// <summary>Gets the AV1 configuration of the (first cell of the) primary image.</summary>
    internal Av1Config Config { get; private set; } = null!;

    /// <summary>Gets the AV1 sequence header of the (first cell of the) primary image.</summary>
    internal Av1SequenceHeader Sequence { get; private set; } = null!;

    /// <summary>Gets whether the image has progressive layers (a1lx on the av01 item and no lsel picking one).</summary>
    internal bool Progressive { get; private set; }

    internal override int BitDepth => Config.BitDepth;

    internal override bool Monochrome => Config.Monochrome;

    internal override int SubsamplingX => Config.SubsamplingX;

    internal override int SubsamplingY => Config.SubsamplingY;

    /// <summary>Gets the siting: centered unless 4:2:0, where the sequence header's chroma_sample_position 2 is top-left and others left.</summary>
    internal override Pixels.ChromaSiting ChromaSiting => Siting(Config, Sequence);

    private protected override uint CodedType => Av01;

    private protected override string DiagnosticPrefix => "Avif";

    private protected override Colors.ColorCodePoints StreamCodePoints =>
        new((byte)Sequence.ColorPrimaries, (byte)Sequence.TransferCharacteristics, (byte)Sequence.MatrixCoefficients, Sequence.FullRange);

    /// <summary>Gets the chroma siting an AV1 configuration and sequence header give.</summary>
    internal static Pixels.ChromaSiting Siting(Av1Config config, Av1SequenceHeader sequence) =>
        config.Chroma != Pixels.ChromaFormat.Yuv420 ? Pixels.ChromaSiting.Center
            : sequence.ChromaSamplePosition == 2 ? Pixels.ChromaSiting.TopLeft : Pixels.ChromaSiting.Left;

    /// <summary>Reads the input into memory and finds the still image.</summary>
    internal static AvifFile Read(EgamiInput input) => Open(HeifFile.Read(input));

    /// <summary>Finds the still image of a parsed container.</summary>
    internal static AvifFile Open(HeifFile heif)
    {
        AvifFile avif = new(heif, FindPrimary(heif, AvifProperties.Understood.Contains, Av01, Grid, Iovl));
        avif.Resolve();
        return avif;
    }

    private protected override void ReadCoding(HeifItem first)
    {
        HeifFile heif = Heif;
        if (heif.Property(first, AvifProperties.Av1C) is not HeifProperty av1C)
        {
            throw Corrupt(heif, $"{Capital(first)} has no av1C property.");
        }

        Config = AvifProperties.ReadAv1Config(heif, av1C);
        Sequence = Av1SequenceHeader.Find(Config.ConfigObus.Span, heif.FormatName)
            ?? Av1SequenceHeader.Find(heif.ItemData(first).Span, heif.FormatName)
            ?? throw Corrupt(heif, $"{Capital(first)} has no AV1 sequence header.");

        // Layered without lsel picking a layer (or with lsel's 0xFFFF, any layer).
        Progressive = heif.Property(first, AvifProperties.A1lx) is not null
            && (heif.Property(first, AvifProperties.Lsel) is not HeifProperty lsel || heif.Content(lsel) is [0xFF, 0xFF, ..]);
    }
}
