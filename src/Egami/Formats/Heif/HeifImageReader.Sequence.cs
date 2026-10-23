using Egami.Colors;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Heif;

/// <summary>
/// A HEIF image sequence read from its tracks (AVIF's avis, HEIC's hevc and hevx brands): a color track, maybe an alpha track,
/// and the color its sample entry gives. A sequence whose codec decodes inter frames (AVIF) loads every frame; otherwise every
/// frame loads when every sample is a sync sample, and the first frame alone when not.
/// </summary>
internal interface IHeifSequence
{
    /// <summary>Gets the frame size.</summary>
    (int Width, int Height) Size { get; }

    /// <summary>Gets the bits a sample has.</summary>
    int BitDepth { get; }

    /// <summary>Gets whether the frames are luma only.</summary>
    bool Monochrome { get; }

    /// <summary>Gets whether there is an alpha track.</summary>
    bool HasAlpha { get; }

    /// <summary>Gets whether the color is premultiplied by the alpha.</summary>
    bool AlphaPremultiplied { get; }

    /// <summary>Gets the ICC profile of the color track's sample entry, or an empty block.</summary>
    ReadOnlyMemory<byte> Icc { get; }

    /// <summary>Gets the color: the sample entry's nclx box, else the stream's.</summary>
    ColorCodePoints CodePoints { get; }

    /// <summary>Gets where chroma sits.</summary>
    ChromaSiting ChromaSiting { get; }

    /// <summary>Gets whether the sequence repeats (an edit list that loops).</summary>
    bool Repeats { get; }

    /// <summary>Gets how many times the sequence plays, 0 for forever (<see cref="HeifTrack.LoopCount"/>).</summary>
    int LoopCount { get; }

    /// <summary>Gets the color track's sample count.</summary>
    int SampleCount { get; }

    /// <summary>Gets whether every sample is a sync sample, so every frame decodes without inter prediction.</summary>
    bool AllKeyFrames { get; }

    /// <summary>Gets whether every frame decodes: the codec decodes inter frames, or every sample is a key frame.</summary>
    bool DecodesAllFrames => AllKeyFrames;

    /// <summary>Gets how many frames Load gives: all of them when every frame decodes, else the first.</summary>
    int FrameCount => DecodesAllFrames ? SampleCount : Math.Min(1, SampleCount);

    /// <summary>Gets the container's problems, given as warnings.</summary>
    IEnumerable<HeifProblem> Problems { get; }

    /// <summary>Gets how long frame <paramref name="index"/> shows.</summary>
    TimeSpan Delay(int index);

    /// <summary>Decodes frame <paramref name="index"/>'s color and alpha into planes with <paramref name="codec"/>.</summary>
    (HeifPlanes Color, HeifPlanes? Alpha) Decode(int index, IHeifCodec codec, EgamiInput input);
}

/// <content>Image sequences, shared by AVIF and HEIC.</content>
internal sealed partial class HeifImageReader
{
    /// <summary>Gets a sequence's RasterInfo: its frames, size and natural layout.</summary>
    internal RasterInfo SequenceInfo(IHeifSequence sequence, EgamiInput input)
    {
        (int width, int height) = sequence.Size;
        input.Limits.CheckFrameSize(width, height);
        PixelLayoutInfo layout = NaturalLayout(sequence.BitDepth, sequence.Monochrome, sequence.HasAlpha);
        int frames = sequence.FrameCount;
        return new RasterInfo
        {
            Format = format(),
            Width = width,
            Height = height,
            Layout = layout,
            FrameCount = frames,
            ImageCount = 1,
            HasMetadata = !sequence.Icc.IsEmpty,
            EstimatedBytes = (long)width * height * layout.BytesPerPixel * frames,
            LoopCount = sequence.LoopCount,
            Access = RasterAccess.Buffered,
        };
    }

    /// <summary>Reads the frames Load gives in the natural pixel type.</summary>
    /// <param name="input">The input.</param>
    /// <param name="sequence">The sequence.</param>
    /// <param name="options">The options; <see cref="ReaderOptions.Frames"/> picks frames.</param>
    /// <param name="metadata">Makes the metadata (format details, color, warnings) once the frames are decoded with the codec.</param>
    internal Raster ReadSequence(EgamiInput input, IHeifSequence sequence, ReaderOptions options, Func<IHeifCodec, RasterMetadata> metadata)
    {
        PixelLayoutInfo layout = NaturalLayout(sequence.BitDepth, sequence.Monochrome, sequence.HasAlpha);
        return layout == Gray8.Layout ? ReadSequence<Gray8>(input, sequence, options, metadata)
            : layout == GrayAlpha8.Layout ? ReadSequence<GrayAlpha8>(input, sequence, options, metadata)
            : layout == Rgb8.Layout ? ReadSequence<Rgb8>(input, sequence, options, metadata)
            : layout == Rgba8.Layout ? ReadSequence<Rgba8>(input, sequence, options, metadata)
            : layout == Gray16.Layout ? ReadSequence<Gray16>(input, sequence, options, metadata)
            : layout == GrayAlpha16.Layout ? ReadSequence<GrayAlpha16>(input, sequence, options, metadata)
            : layout == Rgb16.Layout ? ReadSequence<Rgb16>(input, sequence, options, metadata)
            : ReadSequence<Rgba16>(input, sequence, options, metadata);
    }

    /// <summary>Reads the frames in <see cref="ReaderOptions.Frames"/> (of those Load gives), each converted as a still is.</summary>
    internal Raster<TPixel> ReadSequence<TPixel>(EgamiInput input, IHeifSequence sequence, ReaderOptions options, Func<IHeifCodec, RasterMetadata> metadata)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        IHeifCodec codec = createCodec(options);
        (int first, int count) = AnimationCanvasRange.Clamp(options.Frames, sequence.FrameCount);
        if (count == 0)
        {
            throw input.Corrupt(-1, "No frame is in the requested range.");
        }

        PixelLayoutInfo natural = NaturalLayout(sequence.BitDepth, sequence.Monochrome, sequence.HasAlpha);
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
                    dropped = $"Frame {index} of the {format().Name} sequence could not be decoded ({e.Message}); the {index - first} frames before it were loaded.";
                    break;
                }

                IHeifRgbConverter converter = Converter(new HeifConversion(color, alpha, sequence.AlphaPremultiplied, sequence.CodePoints, false));
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

            RasterMetadata gathered = metadata(codec);
            gathered.CodePoints = RasterCodePoints(gathered.CodePoints, gathered.IccProfile);
            if (!sequence.DecodesAllFrames && sequence.SampleCount > 1)
            {
                gathered.WarningList.Add(ids.SequenceFirstFrame!, $"The {format().Name} sequence's {sequence.SampleCount} frames are not all key frames, and {ids.InterFrames}; only the first frame was loaded.");
            }

            if (dropped is not null)
            {
                gathered.WarningList.Add(ids.FramesDropped!, dropped);
            }

            if (unsupported)
            {
                gathered.WarningList.Add(ids.MatrixApproximated, $"Matrix coefficients {sequence.CodePoints.MatrixCoefficients} are not converted with this range and chroma; the nearest matrix was used.");
            }

            Adopt(raster!.Metadata, gathered);
            raster.Metadata.LoopCount = sequence.LoopCount;
            return raster;
        }
        catch
        {
            raster?.Dispose();
            throw;
        }
    }

    /// <summary>Gives the first frame's planes, as YuvRaster.Load gives a still's.</summary>
    internal YuvRaster SequenceYuv(EgamiInput input, IHeifSequence sequence, ReaderOptions options, Func<IHeifCodec, RasterMetadata> metadata)
    {
        IHeifCodec codec = createCodec(options);
        (HeifPlanes color, HeifPlanes? alpha) = sequence.Decode(0, codec, input);
        RasterMetadata gathered = metadata(codec);
        gathered.CodePoints = sequence.CodePoints;
        YuvColor yuvColor = PlaneColor(sequence.CodePoints, sequence.ChromaSiting, gathered);
        return color.BitDepth == 8
            ? Planes<byte>(color, alpha, yuvColor, gathered, input.Context)
            : Planes<ushort>(color, alpha, yuvColor, gathered, input.Context);
    }
}
