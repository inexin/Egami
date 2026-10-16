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
    private static RasterInfo SequenceInfo(AvifSequence sequence, EgamiInput input, ReaderOptions options)
    {
        (int width, int height) = sequence.Size;
        input.Limits.CheckFrameSize(width, height);
        PixelLayoutInfo layout = SequenceLayout(sequence);
        int frames = sequence.FrameCount;
        return new RasterInfo
        {
            Format = AvifFormat.Info,
            Width = width,
            Height = height,
            Layout = layout,
            FrameCount = frames,
            ImageCount = 1,
            HasMetadata = !sequence.Icc.IsEmpty,
            EstimatedBytes = (long)width * height * layout.BytesPerPixel * frames,
            LoopCount = sequence.Color.Repeats ? 0 : 1,
            Access = RasterAccess.Buffered,
        };
    }

    private static PixelLayoutInfo SequenceLayout(AvifSequence sequence) =>
        NaturalLayout(sequence.Config.BitDepth, sequence.Config.Monochrome, sequence.Alpha is not null);

    /// <summary>Reads the frames Load gives in the natural pixel type.</summary>
    private static Raster ReadSequence(EgamiInput input, AvifSequence sequence, ReaderOptions options)
    {
        PixelLayoutInfo layout = SequenceLayout(sequence);
        return layout == Gray8.Layout ? ReadSequence<Gray8>(input, sequence, options)
            : layout == GrayAlpha8.Layout ? ReadSequence<GrayAlpha8>(input, sequence, options)
            : layout == Rgb8.Layout ? ReadSequence<Rgb8>(input, sequence, options)
            : layout == Rgba8.Layout ? ReadSequence<Rgba8>(input, sequence, options)
            : layout == Gray16.Layout ? ReadSequence<Gray16>(input, sequence, options)
            : layout == GrayAlpha16.Layout ? ReadSequence<GrayAlpha16>(input, sequence, options)
            : layout == Rgb16.Layout ? ReadSequence<Rgb16>(input, sequence, options)
            : ReadSequence<Rgba16>(input, sequence, options);
    }

    /// <summary>Reads the frames in <see cref="ReaderOptions.Frames"/> (of those Load gives), each converted as a still is.</summary>
    private static Raster<TPixel> ReadSequence<TPixel>(EgamiInput input, AvifSequence sequence, ReaderOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        AvifCodec codec = new(options is not AvifReaderOptions { FilmGrain: false });
        (int first, int count) = AnimationCanvasRange.Clamp(options.Frames, sequence.FrameCount);
        if (count == 0)
        {
            throw input.Corrupt(-1, "No frame is in the requested range.");
        }

        PixelLayoutInfo natural = SequenceLayout(sequence);
        bool unsupported = false;
        string? dropped = null;
        Raster<TPixel>? raster = null;
        try
        {
            for (int index = first; index < first + count; index++)
            {
                input.CancellationToken.ThrowIfCancellationRequested();
                HeifPlanes color;
                HeifPlanes? alpha;
                try
                {
                    (color, alpha) = sequence.Decode(index, codec, input);
                }
                catch (CorruptDataException e) when (raster is not null && options.Recovery != RecoveryMode.Strict)
                {
                    // A damaged frame after the first ends a lenient load with the frames before it.
                    dropped = $"Frame {index} of the AVIF sequence could not be decoded ({e.Message}); the {index - first} frames before it were loaded.";
                    break;
                }

                HeifRgbConverter converter = new(color, alpha, sequence.AlphaPremultiplied, sequence.CodePoints);
                unsupported |= !converter.Supported;
                Frame<TPixel>? frame = null;
                if (raster is null)
                {
                    input.Limits.CheckFrameSize(color.Width, color.Height);
                    raster = input.CreateRaster<TPixel>(color.Width, color.Height);
                }
                else if (color.Width != raster.Width || color.Height != raster.Height)
                {
                    throw input.Corrupt(-1, $"Frame {index} of the sequence is {color.Width} x {color.Height}, not {raster.Width} x {raster.Height}.");
                }
                else
                {
                    frame = input.AddFrame(raster);
                }

                PixelRows rows = new(converter.Channels, converter.OutputDepth, (row, column, samples) => converter.Row(row, column, samples.Length / converter.Channels, samples))
                {
                    Write = (row, column, width, target) => converter.Write(row, column, width, target),
                };
                Raster<TPixel> target = raster;
                if (frame is null)
                {
                    Fill(raster.Width, raster.Height, y => target.Row(y), rows, natural, input);
                    raster.Frames[0].Metadata.Delay = sequence.Delay(index);
                }
                else
                {
                    Frame<TPixel> current = frame;
                    Fill(raster.Width, raster.Height, y => current.Row(y), rows, natural, input);
                    current.Metadata.Delay = sequence.Delay(index);
                }
            }

            RasterMetadata metadata = SequenceMetadata(sequence, codec);
            metadata.CodePoints = RasterCodePoints(metadata.CodePoints, metadata.IccProfile);
            if (!sequence.AllKeyFrames && sequence.Color.Samples.Count > 1)
            {
                metadata.WarningList.Add("Avif.SequenceFirstFrame", $"The AVIF sequence's {sequence.Color.Samples.Count} frames are not all key frames, and Egami decodes inter frames from 2.9.5; only the first frame was loaded.");
            }

            if (dropped is not null)
            {
                metadata.WarningList.Add("Avif.FramesDropped", dropped);
            }

            if (unsupported)
            {
                metadata.WarningList.Add("Avif.MatrixApproximated", $"libavif does not convert matrix coefficients {sequence.CodePoints.MatrixCoefficients} with this range and chroma; the nearest matrix it has was used.");
            }

            Adopt(raster!.Metadata, metadata);
            raster.Metadata.LoopCount = sequence.Color.Repeats ? 0 : 1;
            return raster;
        }
        catch
        {
            raster?.Dispose();
            throw;
        }
    }

    /// <summary>Gives the first frame's planes, as YuvRaster.Load gives a still's.</summary>
    private static YuvRaster SequenceYuv(AvifSequence sequence, EgamiInput input, ReaderOptions options)
    {
        AvifCodec codec = new(options is not AvifReaderOptions { FilmGrain: false });
        (HeifPlanes color, HeifPlanes? alpha) = sequence.Decode(0, codec, input);
        RasterMetadata metadata = SequenceMetadata(sequence, codec);
        metadata.CodePoints = sequence.CodePoints;
        YuvColor yuvColor = PlaneColor(sequence.CodePoints, sequence.Config, sequence.Sequence, metadata);
        return color.BitDepth == 8
            ? Planes<byte>(color, alpha, yuvColor, metadata, input.Context)
            : Planes<ushort>(color, alpha, yuvColor, metadata, input.Context);
    }

    private static RasterMetadata SequenceMetadata(AvifSequence sequence, AvifCodec codec)
    {
        HeifFile heif = sequence.Heif;
        RasterMetadata metadata = new() { FormatName = AvifFormat.Info.Name, IccProfile = sequence.Icc, CodePoints = sequence.CodePoints };
        Av1Config config = sequence.Config;
        metadata.FormatDetails = new AvifDetails
        {
            MajorBrand = FourCC.ToString(heif.MajorBrand),
            CompatibleBrands = [.. heif.CompatibleBrands.Select(FourCC.ToString)],
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
