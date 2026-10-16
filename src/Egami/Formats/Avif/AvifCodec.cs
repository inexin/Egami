using Egami.Formats.Av1;
using Egami.Formats.Heif;

namespace Egami.Formats.Avif;

/// <summary>
/// AVIF's codec behind the HEIF image layer: an av01 item's AV1 frame, filtered and, unless turned off, with its film grain, as
/// planes. One instance serves one read and keeps the first frame's grain parameters.
/// </summary>
/// <param name="filmGrain">Whether grain is added (design question 6).</param>
internal sealed class AvifCodec(bool filmGrain) : IHeifCodec
{
    /// <summary>Gets the film grain parameters of the first frame decoded that applies grain, or null.</summary>
    internal AvifFilmGrain? FilmGrain { get; private set; }

    /// <summary>Gets the color code points and range the last decoded frame's sequence header gives.</summary>
    internal Colors.ColorCodePoints CodePoints { get; private set; }

    public HeifPlanes Decode(byte[] data, EgamiInput input)
    {
        Av1Frame frame = Av1Decoder.DecodeFirstFrame(data, input.FormatName, parallelism: input.Context.MaxParallelism,
            cancellationToken: input.CancellationToken, limits: input.Limits);
        if (!frame.Header.ShowFrame && !frame.Header.ShowableFrame)
        {
            throw new CorruptDataException(input.FormatName, -1, "The AV1 item's first frame is never shown.");
        }

        Av1SequenceHeader sequence = frame.Sequence;
        CodePoints = new Colors.ColorCodePoints((byte)sequence.ColorPrimaries, (byte)sequence.TransferCharacteristics, (byte)sequence.MatrixCoefficients, sequence.FullRange);
        Av1FilmGrain grain = frame.Header.FilmGrain;
        if (grain.ApplyGrain)
        {
            FilmGrain ??= Describe(grain);
        }

        ushort[][] planes = filmGrain ? Av1FilmGrainSynthesis.Apply(frame) : frame.Planes!;
        int count = sequence.Monochrome ? 1 : 3;
        return new HeifPlanes(frame.Header.UpscaledWidth, frame.Header.FrameHeight, sequence.BitDepth, sequence.SubsamplingX,
            sequence.SubsamplingY, sequence.Monochrome, planes[..count], frame.Strides[..count]);
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
