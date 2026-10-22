using Egami.Formats.Heif;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Avif;

/// <content>
/// Image sequences (the avis brand, design question 4): every frame as an animation when every sample is a key frame, else the
/// first frame with an Avif.SequenceFirstFrame warning until inter frames arrive in 2.9.5.
/// </content>
internal sealed partial class AvifReader
{
    private static RasterInfo SequenceInfo(AvifSequence sequence, EgamiInput input, ReaderOptions options) => Still.SequenceInfo(sequence, input);

    /// <summary>Reads the frames Load gives in the natural pixel type.</summary>
    private static Raster ReadSequence(EgamiInput input, AvifSequence sequence, ReaderOptions options) =>
        Still.ReadSequence(input, sequence, options, codec => SequenceMetadata(sequence, (AvifCodec)codec));

    /// <summary>Reads the frames in <see cref="ReaderOptions.Frames"/> (of those Load gives), each converted as a still is.</summary>
    private static Raster<TPixel> ReadSequence<TPixel>(EgamiInput input, AvifSequence sequence, ReaderOptions options)
        where TPixel : unmanaged, IPixelType<TPixel> =>
        Still.ReadSequence<TPixel>(input, sequence, options, codec => SequenceMetadata(sequence, (AvifCodec)codec));

    /// <summary>Gives the first frame's planes, as YuvRaster.Load gives a still's.</summary>
    private static YuvRaster SequenceYuv(AvifSequence sequence, EgamiInput input, ReaderOptions options) =>
        Still.SequenceYuv(input, sequence, options, codec => SequenceMetadata(sequence, (AvifCodec)codec));

    private static RasterMetadata SequenceMetadata(AvifSequence sequence, AvifCodec codec)
    {
        HeifFile heif = sequence.Heif;
        RasterMetadata metadata = new() { FormatName = AvifFormat.Info.Name, IccProfile = sequence.Icc, CodePoints = sequence.CodePoints };
        Av1Config config = sequence.Config;
        metadata.FormatDetails = new AvifDetails
        {
            MajorBrand = IsoTypes.ToString(heif.MajorBrand),
            CompatibleBrands = [.. heif.CompatibleBrands.Select(IsoTypes.ToString)],
            Profile = config.Profile,
            Level = config.Level,
            Tier = config.Tier,
            BitDepth = config.BitDepth,
            Chroma = config.Chroma,
            CodePoints = sequence.CodePoints,
            HasAlpha = sequence.Alpha is not null,
            AlphaPremultiplied = sequence.AlphaPremultiplied,
            IsSequence = true,
            HasFilmGrain = sequence.Sequence.FilmGrainParamsPresent,
            FilmGrain = codec.FilmGrain,
        };
        foreach (HeifProblem problem in heif.Problems)
        {
            metadata.WarningList.Add(problem.Id, problem.Message, problem.Offset);
        }

        return metadata;
    }
}
