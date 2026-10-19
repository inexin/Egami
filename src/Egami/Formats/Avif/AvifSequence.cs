using Egami.Colors;
using Egami.Formats.Av1;
using Egami.Formats.Heif;
using Egami.Metadata;

namespace Egami.Formats.Avif;

/// <summary>
/// An AVIF image sequence (the avis brand), read from its tracks as libavif reads them: the first AV1 track that describes no
/// other is the color, a track with an auxl reference to it the alpha; each track's first sample entry gives the av1C
/// configuration and colr. Until inter frames arrive (2.9.5, design question 4), every frame loads when every sample is a sync
/// sample (a key frame), and otherwise the first frame alone.
/// </summary>
internal sealed class AvifSequence : IHeifSequence
{
    private static readonly uint Vide = FourCC.Of("vide");
    private static readonly uint Pict = FourCC.Of("pict");
    private static readonly uint Auxl = FourCC.Of("auxl");
    private static readonly uint Prem = FourCC.Of("prem");

    private AvifSequence(HeifFile heif, HeifTrack color, HeifTrack? alpha)
    {
        Heif = heif;
        Color = color;
        Alpha = alpha;
        Config = AvifProperties.ReadAv1Config(heif, Property(color, AvifProperties.Av1C)
            ?? throw new CorruptDataException(heif.FormatName, -1, "The AVIF sequence's color track has no av1C box."));
        Sequence = Av1SequenceHeader.Find(Config.ConfigObus.Span, heif.FormatName)
            ?? (color.Samples.Count > 0 && Fits(heif, color.Samples[0])
                ? Av1SequenceHeader.Find(heif.Bytes.Span.Slice((int)color.Samples[0].Offset, color.Samples[0].Size), heif.FormatName) : null)
            ?? throw new CorruptDataException(heif.FormatName, -1, "The AVIF sequence has no AV1 sequence header.");
        if (Property(color, AvifProperties.Colr) is HeifProperty colr)
        {
            (Nclx, Icc) = AvifProperties.ReadColor(heif, colr);
        }

        AlphaPremultiplied = alpha is not null && color.References.TryGetValue(Prem, out uint[]? prem) && Array.IndexOf(prem, alpha.Id) >= 0;
        _colorPrefix = Prefix(heif, color, Config.ConfigObus);
    }

    private readonly byte[] _colorPrefix;

    /// <summary>Gets the container.</summary>
    internal HeifFile Heif { get; }

    /// <summary>Gets the color track.</summary>
    internal HeifTrack Color { get; }

    /// <summary>Gets the alpha track, or null.</summary>
    internal HeifTrack? Alpha { get; }

    /// <summary>Gets the color track's AV1 configuration.</summary>
    internal Av1Config Config { get; }

    /// <summary>Gets the AV1 sequence header.</summary>
    internal Av1SequenceHeader Sequence { get; }

    /// <summary>Gets the nclx colr box of the color track's sample entry, or null.</summary>
    internal NclxColor? Nclx { get; }

    /// <summary>Gets the ICC profile of the color track's sample entry, or an empty block.</summary>
    internal ReadOnlyMemory<byte> Icc { get; }

    /// <summary>Gets whether the color is premultiplied by the alpha (a prem track reference).</summary>
    internal bool AlphaPremultiplied { get; }

    /// <summary>Gets the image size: the AV1 sequence's maximum frame size.</summary>
    public (int Width, int Height) Size => (Sequence.MaxFrameWidth, Sequence.MaxFrameHeight);

    /// <summary>Gets whether every color sample is a sync sample, so every frame decodes without inter prediction.</summary>
    public bool AllKeyFrames => Color.Samples.TrueForAll(s => s.Sync) && (Alpha is null || Alpha.Samples.TrueForAll(s => s.Sync));

    /// <summary>Gets how many frames Load gives: all of them when every sample is a key frame, else the first.</summary>
    internal int FrameCount => ((IHeifSequence)this).FrameCount;

    public int BitDepth => Config.BitDepth;

    public bool Monochrome => Config.Monochrome;

    public bool HasAlpha => Alpha is not null;

    public Pixels.ChromaSiting ChromaSiting => AvifFile.Siting(Config, Sequence);

    public bool Repeats => Color.Repeats;

    public int SampleCount => Color.Samples.Count;

    public IEnumerable<HeifProblem> Problems => Heif.Problems;

    ReadOnlyMemory<byte> IHeifSequence.Icc => Icc;

    bool IHeifSequence.AlphaPremultiplied => AlphaPremultiplied;

    /// <summary>Gets the color code points: the nclx box's, else the sequence header's.</summary>
    public ColorCodePoints CodePoints => Nclx is NclxColor n
        ? new ColorCodePoints((byte)n.Primaries, (byte)n.Transfer, (byte)n.Matrix, n.FullRange)
        : new ColorCodePoints((byte)Sequence.ColorPrimaries, (byte)Sequence.TransferCharacteristics, (byte)Sequence.MatrixCoefficients, Sequence.FullRange);

    /// <summary>
    /// Gets the sequence when the file is read from its tracks, as libavif decides: the major brand avis, or, unless the major
    /// brand is avif, any track with no primary item to fall back on; otherwise null.
    /// </summary>
    internal static AvifSequence? Find(HeifFile heif)
    {
        bool tracksFirst = heif.MajorBrand == AvifFile.Avis || (heif.MajorBrand != AvifFile.Avif && !heif.HasMeta);
        if (!tracksFirst && heif.HasMeta)
        {
            return null;
        }

        List<HeifTrack> tracks = HeifTracks.Read(heif);
        HeifTrack? color = tracks.Find(t => t.Id != 0 && t.Samples.Count > 0 && t.SampleEntry == AvifFile.Av01 && !t.References.ContainsKey(Auxl)
            && (t.Handler == Pict || t.Handler == Vide));
        if (color is null)
        {
            return heif.HasMeta ? null : throw new CorruptDataException(heif.FormatName, -1, "The AVIF sequence has no AV1 color track.");
        }

        HeifTrack? alpha = tracks.Find(t => t != color && t.SampleEntry == AvifFile.Av01 && t.Samples.Count > 0
            && t.References.TryGetValue(Auxl, out uint[]? owners) && Array.IndexOf(owners, color.Id) >= 0 && IsAlpha(heif, t));
        return new AvifSequence(heif, color, alpha);
    }

    /// <summary>Gets whether an auxiliary track is alpha: its auxi box names the alpha URN (a depth track names another).</summary>
    private static bool IsAlpha(HeifFile heif, HeifTrack track)
    {
        foreach (IsoBox box in track.EntryBoxes)
        {
            if (box.Type == FourCC.Of("auxi"))
            {
                // A FullBox, then the type as a null-terminated string.
                ReadOnlySpan<byte> c = box.Content(heif.Bytes.Span);
                string type = c.Length > 4 ? System.Text.Encoding.UTF8.GetString(c[4..].TrimEnd((byte)0)) : string.Empty;
                return type is AvifProperties.AlphaUrn or AvifProperties.HevcAlphaUrn;
            }
        }

        return true;
    }

    /// <summary>Decodes frame <paramref name="index"/>'s color and alpha into planes.</summary>
    public (HeifPlanes Color, HeifPlanes? Alpha) Decode(int index, IHeifCodec codec, EgamiInput input) => Decode(index, (AvifCodec)codec, input);

    /// <inheritdoc cref="Decode(int, IHeifCodec, EgamiInput)"/>
    internal (HeifPlanes Color, HeifPlanes? Alpha) Decode(int index, AvifCodec codec, EgamiInput input)
    {
        HeifPlanes color = codec.Decode(Sample(Color, index, _colorPrefix), input);
        if (color.BitDepth != Config.BitDepth || color.Monochrome != Config.Monochrome)
        {
            // The layout Load gives comes from av1C, so every frame must match it.
            throw input.Corrupt(-1, $"Frame {index} of the AVIF sequence is {color.BitDepth}-bit{(color.Monochrome ? " monochrome" : "")}, but its av1C box says {Config.BitDepth}-bit{(Config.Monochrome ? " monochrome" : "")}.");
        }

        HeifPlanes? alpha = null;
        if (Alpha is HeifTrack track && index < track.Samples.Count)
        {
            Av1Config config = AvifProperties.ReadAv1Config(Heif, Property(track, AvifProperties.Av1C)
                ?? throw new CorruptDataException(Heif.FormatName, -1, "The AVIF sequence's alpha track has no av1C box."));
            alpha = codec.Decode(Sample(track, index, Prefix(Heif, track, config.ConfigObus)), input).LumaOnly();
            if (alpha.Width != color.Width || alpha.Height != color.Height)
            {
                throw input.Corrupt(-1, $"The alpha frame is {alpha.Width} x {alpha.Height}, but the color frame is {color.Width} x {color.Height}.");
            }
        }

        return (color, alpha);
    }

    /// <summary>Gets how long frame <paramref name="index"/> shows.</summary>
    public TimeSpan Delay(int index) => Color.Timescale == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(Color.Samples[index].Duration / (double)Color.Timescale);

    private static HeifProperty? Property(HeifTrack track, uint type)
    {
        foreach (IsoBox box in track.EntryBoxes)
        {
            if (box.Type == type)
            {
                return new HeifProperty(type, box);
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the OBUs to put before each of a track's samples: its configuration OBUs, and when they hold no sequence header, the
    /// first sample's, as a decoder keeps it from one temporal unit to the next.
    /// </summary>
    private static byte[] Prefix(HeifFile heif, HeifTrack track, ReadOnlyMemory<byte> configObus)
    {
        if (Av1SequenceHeader.Find(configObus.Span, heif.FormatName) is not null || track.Samples.Count == 0)
        {
            return configObus.ToArray();
        }

        HeifSample first = track.Samples[0];
        if (!Fits(heif, first))
        {
            return configObus.ToArray();
        }

        byte[] data = heif.Bytes.Span.Slice((int)first.Offset, first.Size).ToArray();
        foreach (Av1Obu obu in Av1Obu.Split(data, heif.FormatName))
        {
            if (obu.Type == Av1ObuType.SequenceHeader)
            {
                // The sequence header as an OBU of its own: its header byte with obu_has_size_field, the size, the payload.
                List<byte> header = [(byte)(((int)Av1ObuType.SequenceHeader << 3) | 2)];
                for (uint size = (uint)obu.PayloadLength; ; size >>= 7)
                {
                    header.Add((byte)((size & 0x7f) | (size >= 0x80 ? 0x80u : 0u)));
                    if (size < 0x80)
                    {
                        break;
                    }
                }

                return [.. configObus.Span, .. header, .. data.AsSpan(obu.PayloadStart, obu.PayloadLength)];
            }
        }

        return configObus.ToArray();
    }

    /// <summary>Gets whether a sample lies within the file.</summary>
    private static bool Fits(HeifFile heif, HeifSample sample) => sample.Offset >= 0 && sample.Size >= 0 && sample.Offset + sample.Size <= heif.Bytes.Length;

    /// <summary>A sample's bytes, after <paramref name="prefix"/> (the configuration OBUs and sequence header).</summary>
    private byte[] Sample(HeifTrack track, int index, byte[] prefix)
    {
        HeifSample sample = track.Samples[index];
        ReadOnlySpan<byte> obus = prefix;
        if (!Fits(Heif, sample))
        {
            throw new CorruptDataException(Heif.FormatName, sample.Offset, "A sample of the AVIF sequence lies past the end of the file.") { IsTruncation = true };
        }

        return [.. obus, .. Heif.Bytes.Span.Slice((int)sample.Offset, sample.Size)];
    }
}
