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

    /// <summary>Starts decoding a track's samples in decoding order, inter pictures included.</summary>
    IHevcTrackDecoder OpenTrack(HevcConfig config, EgamiInput input, int parallelism);
}

/// <summary>A track's decoder: samples go in in decoding order, pictures come out in output order.</summary>
internal interface IHevcTrackDecoder
{
    /// <summary>Decodes a sample; the pictures it lets out wait for <see cref="TryTake"/>.</summary>
    void Send(ReadOnlyMemory<byte> sample);

    /// <summary>Lets the pictures still waiting out, at the end of the track.</summary>
    void Flush();

    /// <summary>Takes the next picture in output order, cropped to its conformance window.</summary>
    bool TryTake(out HeifPlanes planes);
}

/// <summary>
/// A HEIC image sequence (the hevc and hevx brands), read from its tracks: the first hvc1 track that describes no other is the
/// color, a track with an auxl reference to it naming the alpha URN the alpha; each track's first sample entry gives its hvcC
/// configuration and colr. Every frame loads, inter pictures too (2.9.10 design, question 20): the frames are the pictures in
/// output order, the track's samples decoded one after another from the first.
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

    /// <summary>Gets whether every frame decodes: HEVC's inter pictures do.</summary>
    public bool DecodesAllFrames => true;

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
    /// (image sequences), so Load gives every frame, inter ones too (2.9.10); otherwise null.
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
        return new HeicSequence(heif, color, alpha);
    }

    public TimeSpan Delay(int index) => Color.Timescale == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(Color.Samples[index].Duration / (double)Color.Timescale);

    public (HeifPlanes Color, HeifPlanes? Alpha) Decode(int index, IHeifCodec codec, EgamiInput input)
    {
        IHevcSampleCodec samples = (IHevcSampleCodec)codec;
        if (_color is null || index < _color.Next)
        {
            // From the first sample: the frames are pictures in output order, which only decoding from the start gives.
            _color = new TrackFrames(samples.OpenTrack(Config, input, input.Context.MaxParallelism));
            _alpha = AlphaTrack is null ? null : new TrackFrames(samples.OpenTrack(AlphaConfig!, input, input.Context.MaxParallelism));
        }

        HeifPlanes color = _color.Frame(index, Color, this, input);
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
            alpha = _alpha!.Frame(index, track, this, input).LumaOnly();
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

    private TrackFrames? _color;
    private TrackFrames? _alpha;

    /// <summary>A track's pictures in output order, decoded as far as the frames asked for.</summary>
    private sealed class TrackFrames(IHevcTrackDecoder decoder)
    {
        private int _sample;
        private bool _flushed;

        /// <summary>Gets the index of the next frame <see cref="Frame"/> gives without starting again.</summary>
        internal int Next { get; private set; }

        internal HeifPlanes Frame(int index, HeifTrack track, HeicSequence sequence, EgamiInput input)
        {
            while (true)
            {
                if (decoder.TryTake(out HeifPlanes planes))
                {
                    if (Next++ == index)
                    {
                        return planes;
                    }

                    continue;
                }

                if (_sample < track.Samples.Count)
                {
                    input.CancellationToken.ThrowIfCancellationRequested();
                    decoder.Send(sequence.Sample(track, _sample++));
                }
                else if (!_flushed)
                {
                    decoder.Flush();
                    _flushed = true;
                }
                else
                {
                    throw input.Corrupt(-1, $"The HEIC sequence's track has {track.Samples.Count} samples, but frame {index} was not among its pictures.");
                }
            }
        }
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
