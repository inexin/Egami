using Egami.Colors;
using Egami.Formats.Avif;
using Egami.Formats.Heif;
using Egami.Formats.Iso;
using Egami.Metadata;

namespace Egami.Formats.Heic;

/// <summary>
/// The codec seam for HEIC image sequences: decodes one sample (an access unit of length-prefixed NAL units) with the track's
/// hvcC configuration, whose parameter sets come first.
/// </summary>
internal interface IHevcSampleCodec
{
    /// <summary>Decodes a sample's picture into planes.</summary>
    HeifPlanes Decode(HevcConfig config, ReadOnlyMemory<byte> sample, EgamiInput input, int parallelism);
}

/// <summary>
/// A HEIC image sequence (the hevc and hevx brands), read from its tracks: the first hvc1 track that describes no other is the
/// color, a track with an auxl reference to it naming the alpha URN the alpha; each track's first sample entry gives its hvcC
/// configuration and colr. Egami.Heic decodes intra pictures only, so every frame loads when every sample is a sync sample, and
/// otherwise the first frame alone with a Heic.SequenceFirstFrame warning (2.9.4 design, question 5).
/// </summary>
internal sealed class HeicSequence : IHeifSequence
{
    private static readonly uint Vide = IsoTypes.Of("vide");
    private static readonly uint Pict = IsoTypes.Of("pict");
    private static readonly uint Auxl = IsoTypes.Of("auxl");
    private static readonly uint Prem = IsoTypes.Of("prem");
    private static readonly uint Hevc = IsoTypes.Of("hevc");
    private static readonly uint Hevx = IsoTypes.Of("hevx");

    private HeicSequence(HeifFile heif, HeifTrack color, HeifTrack? alpha)
    {
        Heif = heif;
        Color = color;
        AlphaTrack = alpha;
        Config = HevcConfig.Read(heif, Property(color, HeicFile.HvcC)
            ?? throw new CorruptDataException(heif.FormatName, -1, "The HEIC sequence's color track has no hvcC box."));
        AlphaConfig = alpha is null ? null : HevcConfig.Read(heif, Property(alpha, HeicFile.HvcC)
            ?? throw new CorruptDataException(heif.FormatName, -1, "The HEIC sequence's alpha track has no hvcC box."));
        if (Property(color, AvifProperties.Colr) is HeifProperty colr)
        {
            (Nclx, Icc) = AvifProperties.ReadColor(heif, colr);
        }

        AlphaPremultiplied = alpha is not null && color.References.TryGetValue(Prem, out uint[]? prem) && Array.IndexOf(prem, alpha.Id) >= 0;
    }

    /// <summary>Gets the container.</summary>
    internal HeifFile Heif { get; }

    /// <summary>Gets the color track.</summary>
    internal HeifTrack Color { get; }

    /// <summary>Gets the alpha track, or null.</summary>
    internal HeifTrack? AlphaTrack { get; }

    /// <summary>Gets the color track's hvcC configuration.</summary>
    internal HevcConfig Config { get; }

    /// <summary>Gets the alpha track's hvcC configuration, or null.</summary>
    internal HevcConfig? AlphaConfig { get; }

    /// <summary>Gets the nclx colr box of the color track's sample entry, or null.</summary>
    internal NclxColor? Nclx { get; }

    /// <summary>Gets the ICC profile of the color track's sample entry, or an empty block.</summary>
    public ReadOnlyMemory<byte> Icc { get; }

    /// <summary>Gets whether the color is premultiplied by the alpha (a prem track reference).</summary>
    public bool AlphaPremultiplied { get; }

    /// <summary>Gets the frame size: the color track's.</summary>
    public (int Width, int Height) Size => (Color.Width, Color.Height);

    public int BitDepth => Math.Max(Config.LumaBitDepth, Config.ChromaBitDepth);

    public bool Monochrome => Config.ChromaFormatIdc == 0;

    public bool HasAlpha => AlphaTrack is not null;

    /// <summary>Gets left for 4:2:0, HEVC's default chroma sample location, and centered otherwise.</summary>
    public Pixels.ChromaSiting ChromaSiting => Config.ChromaFormatIdc == 1 ? Pixels.ChromaSiting.Left : Pixels.ChromaSiting.Center;

    public bool Repeats => Color.Repeats;

    public int LoopCount => Color.LoopCount;

    public int SampleCount => Color.Samples.Count;

    /// <summary>Gets whether every sample is a sync sample (an IRAP picture, intra coded).</summary>
    public bool AllKeyFrames => Color.Samples.TrueForAll(s => s.Sync) && (AlphaTrack is null || AlphaTrack.Samples.TrueForAll(s => s.Sync));

    /// <summary>Gets the color: the nclx box's, else unspecified until the decoder reads the VUI.</summary>
    public ColorCodePoints CodePoints => Nclx is NclxColor n
        ? new ColorCodePoints((byte)n.Primaries, (byte)n.Transfer, (byte)n.Matrix, n.FullRange)
        : _streamCodePoints;

    public IEnumerable<HeifProblem> Problems => Heif.Problems;

    private ColorCodePoints _streamCodePoints = new(2, 2, 2, false);

    /// <summary>
    /// Gets the sequence when the file is read from its tracks: when it has no meta box, or when its major brand is hevc or hevx
    /// (image sequences) and every frame is intra, so Load gives them all. A sequence with inter frames and a primary item gives
    /// that still image instead, as heif-dec does; otherwise null.
    /// </summary>
    internal static HeicSequence? Find(HeifFile heif)
    {
        if (heif.HasMeta && heif.MajorBrand != Hevc && heif.MajorBrand != Hevx)
        {
            return null;
        }

        List<HeifTrack> tracks = HeifTracks.Read(heif);
        HeifTrack? color = tracks.Find(t => t.Id != 0 && t.Samples.Count > 0 && t.SampleEntry == HeicFile.Hvc1 && !t.References.ContainsKey(Auxl)
            && (t.Handler == Pict || t.Handler == Vide));
        if (color is null)
        {
            return heif.HasMeta ? null : throw new CorruptDataException(heif.FormatName, -1, "The HEIC sequence has no HEVC color track.");
        }

        HeifTrack? alpha = tracks.Find(t => t != color && t.SampleEntry == HeicFile.Hvc1 && t.Samples.Count > 0
            && t.References.TryGetValue(Auxl, out uint[]? owners) && Array.IndexOf(owners, color.Id) >= 0 && IsAlpha(heif, t));
        HeicSequence sequence = new(heif, color, alpha);
        return heif.HasMeta && !sequence.AllKeyFrames ? null : sequence;
    }

    public TimeSpan Delay(int index) => Color.Timescale == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(Color.Samples[index].Duration / (double)Color.Timescale);

    public (HeifPlanes Color, HeifPlanes? Alpha) Decode(int index, IHeifCodec codec, EgamiInput input)
    {
        IHevcSampleCodec samples = (IHevcSampleCodec)codec;
        HeifPlanes color = samples.Decode(Config, Sample(Color, index), input, input.Context.MaxParallelism);
        if (color.BitDepth != BitDepth || color.Monochrome != Monochrome)
        {
            throw input.Corrupt(-1, $"Frame {index} of the HEIC sequence is {color.BitDepth}-bit{(color.Monochrome ? " monochrome" : "")}, but its hvcC box says {BitDepth}-bit{(Monochrome ? " monochrome" : "")}.");
        }

        if (index == 0 && Nclx is null)
        {
            _streamCodePoints = codec.CodePoints;
        }

        HeifPlanes? alpha = null;
        if (AlphaTrack is HeifTrack track && index < track.Samples.Count)
        {
            alpha = samples.Decode(AlphaConfig!, Sample(track, index), input, input.Context.MaxParallelism).LumaOnly();
            if (alpha.Width != color.Width || alpha.Height != color.Height)
            {
                throw input.Corrupt(-1, $"The alpha frame is {alpha.Width} x {alpha.Height}, but the color frame is {color.Width} x {color.Height}.");
            }
        }

        return (color, alpha);
    }

    /// <summary>Gets the details Load reports for the sequence.</summary>
    internal HeicDetails Details(ColorCodePoints codePoints) => new()
    {
        MajorBrand = IsoTypes.ToString(Heif.MajorBrand),
        CompatibleBrands = [.. Heif.CompatibleBrands.Select(IsoTypes.ToString)],
        Profile = Config.Profile,
        Level = Config.Level,
        Tier = Config.Tier,
        BitDepth = BitDepth,
        Chroma = Config.Chroma,
        CodePoints = codePoints,
        HasAlpha = HasAlpha,
        AlphaPremultiplied = AlphaPremultiplied,
        IsSequence = true,
    };

    /// <summary>Gets whether an auxiliary track is alpha: its auxi box names the alpha URN.</summary>
    private static bool IsAlpha(HeifFile heif, HeifTrack track)
    {
        foreach (IsoBox box in track.EntryBoxes)
        {
            if (box.Type == IsoTypes.Of("auxi"))
            {
                ReadOnlySpan<byte> c = box.Content(heif.Bytes.Span);
                string type = c.Length > 4 ? System.Text.Encoding.UTF8.GetString(c[4..].TrimEnd((byte)0)) : string.Empty;
                return type is AvifProperties.AlphaUrn or AvifProperties.HevcAlphaUrn;
            }
        }

        return true;
    }

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

    /// <summary>A sample's bytes.</summary>
    private ReadOnlyMemory<byte> Sample(HeifTrack track, int index)
    {
        HeifSample sample = track.Samples[index];
        if (sample.Offset < 0 || sample.Size < 0 || sample.Offset + sample.Size > Heif.Bytes.Length)
        {
            throw new CorruptDataException(Heif.FormatName, sample.Offset, "A sample of the HEIC sequence lies past the end of the file.") { IsTruncation = true };
        }

        return Heif.Bytes.Slice((int)sample.Offset, sample.Size);
    }
}
