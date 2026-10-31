using System.Buffers.Binary;

using Egami.Formats.Av1;
using Egami.Formats.Heif;

namespace Egami.Formats.Avif;

/// <summary>
/// AVIF's codec behind the HEIF image layer: an av01 item's AV1 frame, filtered and, unless turned off, with its film grain, as
/// planes. A layered item (a1lx) decodes every layer and gives the one the options or lsel pick, else the best, at the item's
/// full size (AVIF extras design, question 2). One instance serves one read and keeps the first frame's grain parameters.
/// </summary>
/// <param name="filmGrain">Whether grain is added (design question 6).</param>
/// <param name="layer">The layer of a layered item to give, or null for the best (AVIF extras design, question 2).</param>
internal sealed class AvifCodec(bool filmGrain, int? layer = null) : IHeifCodec
{
    private bool _decoded;

    /// <summary>Gets the film grain parameters of the first frame decoded that applies grain, or null.</summary>
    internal AvifFilmGrain? FilmGrain { get; private set; }

    /// <summary>Gets whether the first frame decoded is coded lossless.</summary>
    internal bool Lossless { get; private set; }

    /// <summary>Gets the size of each layer of the first layered item decoded, in decoding order, or an empty list.</summary>
    internal IReadOnlyList<Size> Layers { get; private set; } = [];

    /// <summary>Gets the color code points and range the last decoded frame's sequence header gives.</summary>
    public Colors.ColorCodePoints CodePoints { get; private set; }

    /// <summary>Gets false: the codec keeps the first frame's grain parameters, so items decode one after another.</summary>
    public bool Concurrent => false;

    public HeifPlanes Decode(HeifImage file, HeifItem item, EgamiInput input, int parallelism)
    {
        byte[] data = file.Heif.ItemData(item, input.Limits).ToArray();
        return file.Heif.Property(item, AvifProperties.A1lx) is HeifProperty a1lx
            ? DecodeLayered(file, item, a1lx, data, input, parallelism)
            : Decode(data, input, parallelism);
    }

    /// <summary>Decodes an AV1 item's or sample's data.</summary>
    internal HeifPlanes Decode(byte[] data, EgamiInput input) => Decode(data, input, input.Context.MaxParallelism);

    /// <summary>
    /// Splits a layered item's data at the layer sizes a1lx gives (AV1-ISOBMFF 2.3.4: a size of 0 is unknown, and the last layer
    /// takes the rest), or gives it whole when a1lx gives none.
    /// </summary>
    internal static List<(int Offset, int Length)> LayerRanges(HeifFile heif, HeifProperty a1lx, int length)
    {
        ReadOnlySpan<byte> c = heif.Content(a1lx);
        if (c.Length < 1)
        {
            throw new CorruptDataException(heif.FormatName, a1lx.Box.StartIndex, "The a1lx property is empty.");
        }

        bool large = (c[0] & 1) != 0;
        int field = large ? 4 : 2;
        if (c.Length < 1 + (3 * field))
        {
            throw new CorruptDataException(heif.FormatName, a1lx.Box.StartIndex, "The a1lx property is cut off.");
        }

        List<(int Offset, int Length)> ranges = [];
        int at = 0;
        for (int i = 0; i < 3; i++)
        {
            long size = large ? BinaryPrimitives.ReadUInt32BigEndian(c[(1 + (4 * i))..]) : BinaryPrimitives.ReadUInt16BigEndian(c[(1 + (2 * i))..]);
            if (size == 0)
            {
                break;
            }

            if (size > length - at)
            {
                throw new CorruptDataException(heif.FormatName, a1lx.Box.StartIndex, $"The a1lx property's layer sizes pass the item's {length} bytes.");
            }

            ranges.Add((at, (int)size));
            at += (int)size;
        }

        if (at < length)
        {
            ranges.Add((at, length - at));
        }

        return ranges;
    }

    /// <summary>
    /// Decodes a layered item's layers in order: the shown frames of each a1lx range at operating point a1op's (else 0), every
    /// spatial layer output, stopping after <paramref name="ranges"/> ranges when given (the layers an incremental read has).
    /// </summary>
    internal static List<Av1Frame> DecodeLayers(HeifFile heif, HeifItem item, HeifProperty a1lx, byte[] data, EgamiInput input, int parallelism, int? ranges)
    {
        int point = heif.Property(item, AvifProperties.A1op) is HeifProperty a1op && heif.Content(a1op) is [byte op, ..] ? op : 0;
        Av1Stream stream = new(input.FormatName)
        {
            OperatingPoint = Math.Min(point, 31),
            OutputAllLayers = true,
            Parallelism = Math.Max(1, parallelism),
            Limits = input.Limits,
        };
        List<Av1Frame> frames = [];
        List<(int Offset, int Length)> parts = LayerRanges(heif, a1lx, data.Length);
        for (int i = 0; i < parts.Count && i < (ranges ?? int.MaxValue); i++)
        {
            input.CancellationToken.ThrowIfCancellationRequested();
            foreach ((Av1Frame frame, _) in stream.DecodeUnit(data, parts[i].Offset, parts[i].Length, input.CancellationToken))
            {
                frames.Add(frame);
            }
        }

        return frames;
    }

    /// <summary>Gives a decoded frame's planes, with its film grain unless that is turned off, and keeps its details.</summary>
    internal HeifPlanes Planes(Av1Frame frame)
    {
        Av1SequenceHeader sequence = frame.Sequence;
        if (!_decoded)
        {
            Lossless = frame.Header.CodedLossless;
            _decoded = true;
        }

        CodePoints = new Colors.ColorCodePoints((byte)sequence.ColorPrimaries, (byte)sequence.TransferCharacteristics, (byte)sequence.MatrixCoefficients, sequence.FullRange);
        Av1FilmGrain grain = frame.Header.FilmGrain;
        if (grain.ApplyGrain)
        {
            FilmGrain ??= Describe(grain);
        }

        ushort[][] planes = Av1Samples.Widen(filmGrain ? Av1FilmGrainSynthesis.Apply(frame) : frame.Planes!);
        int count = sequence.Monochrome ? 1 : 3;
        return new HeifPlanes(frame.Header.UpscaledWidth, frame.Header.FrameHeight, sequence.BitDepth, sequence.SubsamplingX,
            sequence.SubsamplingY, sequence.Monochrome, planes[..count], frame.Strides[..count]);
    }

    /// <summary>Scales planes to another size, each plane bilinearly with sample centers aligned, as a lower layer is shown at full size.</summary>
    internal static HeifPlanes Scale(HeifPlanes planes, int width, int height, EgamiInput input)
    {
        input.Limits.CheckFrameSize(width, height);
        int count = planes.Samples.Length;
        ushort[][] samples = new ushort[count][];
        int[] strides = new int[count];
        for (int p = 0; p < count; p++)
        {
            int sx = p == 0 ? 0 : planes.SubsamplingX, sy = p == 0 ? 0 : planes.SubsamplingY;
            int fw = (planes.Width + sx) >> sx, fh = (planes.Height + sy) >> sy, tw = (width + sx) >> sx, th = (height + sy) >> sy;
            strides[p] = tw;
            samples[p] = new ushort[tw * th];
            ushort[] from = planes.Samples[p], to = samples[p];
            int stride = planes.Strides[p];
            for (int y = 0; y < th; y++)
            {
                // The source position in 1/65536 samples: (y + 0.5) * fh / th - 0.5, held at the edges.
                long fy = Math.Max(0, ((((2L * y) + 1) * fh * 65536) / (2L * th)) - 32768);
                int y0 = Math.Min((int)(fy >> 16), fh - 1), y1 = Math.Min(y0 + 1, fh - 1);
                long wy = fy & 0xFFFF;
                for (int x = 0; x < tw; x++)
                {
                    long fx = Math.Max(0, ((((2L * x) + 1) * fw * 65536) / (2L * tw)) - 32768);
                    int x0 = Math.Min((int)(fx >> 16), fw - 1), x1 = Math.Min(x0 + 1, fw - 1);
                    long wx = fx & 0xFFFF;
                    long top = (from[(y0 * stride) + x0] * (65536 - wx)) + (from[(y0 * stride) + x1] * wx);
                    long bottom = (from[(y1 * stride) + x0] * (65536 - wx)) + (from[(y1 * stride) + x1] * wx);
                    to[(y * tw) + x] = (ushort)(((top * (65536 - wy)) + (bottom * wy) + (1L << 31)) >> 32);
                }
            }
        }

        return planes with { Width = width, Height = height, Samples = samples, Strides = strides };
    }

    private HeifPlanes Decode(byte[] data, EgamiInput input, int parallelism)
    {
        Av1Frame frame = Av1Decoder.DecodeFirstFrame(data, input.FormatName, parallelism: parallelism,
            cancellationToken: input.CancellationToken, limits: input.Limits);
        if (!frame.Header.ShowFrame && !frame.Header.ShowableFrame)
        {
            throw new CorruptDataException(input.FormatName, -1, "The AV1 item's first frame is never shown.");
        }

        return Planes(frame);
    }

    /// <summary>
    /// Decodes every layer of a layered item and gives the layer <c>layer</c> picks by its index (past the last gives the last),
    /// else the one lsel picks by its spatial id, else the last and best; a layer of another size is scaled to the item's.
    /// </summary>
    private HeifPlanes DecodeLayered(HeifImage file, HeifItem item, HeifProperty a1lx, byte[] data, EgamiInput input, int parallelism)
    {
        HeifFile heif = file.Heif;
        List<Av1Frame> frames = DecodeLayers(heif, item, a1lx, data, input, parallelism, null);
        if (frames.Count == 0)
        {
            throw new CorruptDataException(input.FormatName, -1, "The layered AV1 item shows no frame.");
        }

        if (Layers.Count == 0)
        {
            Layers = [.. frames.Select(f => new Size(f.Header.UpscaledWidth, f.Header.FrameHeight))];
        }

        Av1Frame chosen = frames[^1];
        if (layer is int index)
        {
            chosen = frames[Math.Min(index, frames.Count - 1)];
        }
        else if (heif.Property(item, AvifProperties.Lsel) is HeifProperty lsel && heif.Content(lsel) is [byte high, byte low, ..]
            && ((high << 8) | low) is int id and not 0xFFFF)
        {
            chosen = frames.LastOrDefault(f => f.SpatialId == id)
                ?? throw new CorruptDataException(input.FormatName, lsel.Box.StartIndex, $"The lsel property picks layer {id}, which the item does not have.");
        }

        HeifPlanes planes = Planes(chosen);
        (int width, int height) = file.Extents(item);
        return (planes.Width, planes.Height) == (width, height) || width <= 0 || height <= 0 ? planes : Scale(planes, width, height, input);
    }

    private static AvifFilmGrain Describe(Av1FilmGrain g) => new()
    {
        RandomSeed = g.GrainSeed,
        ScalingPointsY = g.PointsY,
        ScalingPointsCb = g.PointsCb,
        ScalingPointsCr = g.PointsCr,
        ChromaScalingFromLuma = g.ChromaScalingFromLuma,
        ScalingShift = g.GrainScalingMinus8 + 8,
        ArCoeffLag = g.ArCoeffLag,
        ArCoeffsY = [.. g.ArCoeffsYPlus128.Select(c => c - 128)],
        ArCoeffsCb = [.. g.ArCoeffsCbPlus128.Select(c => c - 128)],
        ArCoeffsCr = [.. g.ArCoeffsCrPlus128.Select(c => c - 128)],
        ArCoeffShift = g.ArCoeffShiftMinus6 + 6,
        GrainScaleShift = g.GrainScaleShift,
        Cb = (g.CbMult, g.CbLumaMult, g.CbOffset),
        Cr = (g.CrMult, g.CrLumaMult, g.CrOffset),
        Overlap = g.OverlapFlag,
        ClipToRestrictedRange = g.ClipToRestrictedRange,
    };
}
