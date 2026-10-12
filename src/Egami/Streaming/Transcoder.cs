using Egami.Formats;
using Egami.Metadata;
using Egami.Operations;
using Egami.Operations.Color;
using Egami.Operations.Geometry;
using Egami.Operations.Resampling;
using Egami.Pixels;

namespace Egami.Streaming;

/// <summary>Where a transcode writes: makes the encoder once the output size and pixel type are known.</summary>
internal interface ITranscodeTarget
{
    RasterEncoder<TPixel> Create<TPixel>(int width, int height, RasterMetadata metadata, EgamiContext context)
        where TPixel : unmanaged, IPixelType<TPixel>;

    /// <summary>Writes a whole raster, for chains that cannot stream.</summary>
    Task Save(Raster raster, bool async, CancellationToken cancellationToken);
}

internal sealed class PathTarget(string path, WriterOptions? options) : ITranscodeTarget
{
    public RasterEncoder<TPixel> Create<TPixel>(int width, int height, RasterMetadata metadata, EgamiContext context)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        return RasterEncoder.Create<TPixel>(path, width, height, options, metadata, context);
    }

    public Task Save(Raster raster, bool async, CancellationToken cancellationToken)
    {
        if (async)
        {
            return raster.SaveAsync(path, options, cancellationToken);
        }

        raster.Save(path, options);
        return Task.CompletedTask;
    }
}

internal sealed class StreamTarget(Stream stream, FormatInfo format, WriterOptions? options) : ITranscodeTarget
{
    public RasterEncoder<TPixel> Create<TPixel>(int width, int height, RasterMetadata metadata, EgamiContext context)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        return RasterEncoder.Create<TPixel>(stream, format, width, height, options, metadata, context);
    }

    public Task Save(Raster raster, bool async, CancellationToken cancellationToken)
    {
        if (async)
        {
            return raster.SaveAsync(stream, format, options, cancellationToken);
        }

        raster.Save(stream, format, options);
        return Task.CompletedTask;
    }
}

/// <summary>
/// What a transcode decodes with and runs: the reader options (with a leading Crop made a Region, and a leading resize made a
/// MinimumSize) and the steps left for the rows.
/// </summary>
internal sealed record TranscodePlan(ReaderOptions Options, IReadOnlyList<IRasterOperation> Steps)
{
    /// <summary>
    /// Gets the chain to run on the fully decoded image instead of on rows, when it holds a measured step (Trim, EntropyCrop,
    /// Deskew), which needs the whole frame before anything after it can be sized.
    /// </summary>
    public Action<RasterOps>? WholeImage { get; init; }

    /// <summary>Plans the chain for an image the user's options decode to <paramref name="info"/>'s size and orientation.</summary>
    public static TranscodePlan Create(Action<RasterOps>? build, RasterInfo info, ReaderOptions options, Colors.ColorProfile? colorProfile = null, Colors.HdrMetadata? hdr = null)
    {
        if (build is null)
        {
            return new TranscodePlan(options, []);
        }

        Size size = new(info.Width, info.Height);
        // Linear-light steps and ConvertColor need the encoding the rows arrive in: the file's, or ConvertColorTo's.
        OperationPipeline.Plan plan = OperationPipeline.Build(build, size, info.Orientation, colorProfile is null ? null : () => colorProfile, hdr);
        // Measured steps need every frame first, and so, for now, do the animation steps.
        if (plan.HasMeasuredSteps || plan.HasFrameSteps)
        {
            return new TranscodePlan(options, []) { WholeImage = build };
        }

        List<IRasterOperation> steps = [.. plan.Steps];
        foreach (IRasterOperation step in steps)
        {
            Check(step);
        }

        // What the decoder can do itself, it does, unless the caller already chose a region or a size.
        if (steps.Count > 0 && options.Region is null && options.MinimumSize is null)
        {
            if (steps[0] is CropOperation crop)
            {
                options = options with { Region = crop.Rect };
                steps.RemoveAt(0);
            }
            else if (steps[0] is ResizeOperation resize)
            {
                Size target = plan.Sizes[0];
                if (resize.CropRect is Rect rect)
                {
                    options = options with { Region = rect };
                    steps[0] = resize.WithoutCrop();
                }

                options = options with { MinimumSize = target };
            }
        }

        return new TranscodePlan(options, steps);
    }

    /// <summary>Throws for an operation that cannot run on rows as they come, naming it.</summary>
    private static void Check(IRasterOperation step)
    {
        string? name = step switch
        {
            CropOperation or ResizeOperation or ColorMatrixOperation or LinearGrayscaleOperation => null,
            FlipOperation { Vertical: false } => null,
            FlipOperation { Horizontal: false } => "FlipVertical",
            FlipOperation => "Rotate180 (or AutoOrient of an image stored upside down)",
            RotateQuarterOperation => "a quarter turn (Rotate, or AutoOrient of a turned image)",
            RotateOperation => "Rotate",
            Operations.Filters.ConvolutionOperation => "Blur, BoxBlur, Sharpen or Convolve",
            Operations.Compose.DrawImageOperation => "DrawImage",
            PadOperation => "Pad",
            FillColorOperation => "FillColor",
            _ => step.GetType().Name,
        };
        if (name is not null)
        {
            throw new NotSupportedException($"Transcode cannot stream {name}, which needs rows out of order or the whole image. Load the image and use Apply instead.");
        }
    }

    /// <summary>Adds the row steps for an input of <paramref name="size"/> to <paramref name="steps"/>; returns the output size.</summary>
    public Size Build<TPixel>(Size size, EgamiContext context, List<RowStep<TPixel>> steps, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        for (int i = 0; i < Steps.Count; i++)
        {
            IRasterOperation step = Steps[i];
            Size next = step.OutputSize(size);
            steps.Add(step switch
            {
                CropOperation crop => new CropRows<TPixel>(crop.Rect),
                ResizeOperation resize => resize.Rows<TPixel>(size),
                FlipOperation => new FlipRows<TPixel>(size.Width),
                _ => new PixelRows<TPixel>(step, size.Width, context, cancellationToken),
            });
            size = next;
        }

        return size;
    }
}

/// <summary>Runs a transcode: decoder rows through the steps into the encoder.</summary>
internal static class Transcoder
{
    private const int BandRows = 32;

    public static void Run(Stream input, EgamiContext context, ReaderOptions options, Action<RasterOps>? build, ITranscodeTarget target, CancellationToken cancellationToken)
    {
        // The header is read once to plan, then again with the options the plan chose: a seekable input goes back, and the
        // bytes read from one that cannot seek are replayed.
        using ReplayStream? replay = input.CanSeek ? null : new(input);
        Stream source = replay ?? input;
        long start = input.CanSeek ? input.Position : 0;
        TranscodePlan plan;
        using (RasterDecoder probe = RasterDecoder.Open(source, context, options))
        {
            probe.Quiet = true;
            plan = TranscodePlan.Create(build, probe.Info, options, options.ConvertColorTo ?? probe.Metadata.GetColorProfile(), options.ConvertColorTo is null ? probe.Metadata.Hdr : null);
        }

        Restart(input, replay, start, context);
        if (plan.WholeImage is Action<RasterOps> whole)
        {
            using Raster raster = Raster.Load(source, context, plan.Options);
            raster.Apply(whole, cancellationToken);
            target.Save(raster, async: false, cancellationToken).GetAwaiter().GetResult();
            return;
        }

        using RasterDecoder decoder = RasterDecoder.Open(source, context, plan.Options);
        decoder.WithNaturalType(new Work(decoder, plan, target, context, cancellationToken));
    }

    public static async Task RunAsync(Stream input, EgamiContext context, ReaderOptions options, Action<RasterOps>? build, ITranscodeTarget target, CancellationToken cancellationToken)
    {
        using ReplayStream? replay = input.CanSeek ? null : new(input);
        Stream source = replay ?? input;
        long start = input.CanSeek ? input.Position : 0;
        TranscodePlan plan;
        using (RasterDecoder probe = await RasterDecoder.OpenAsync(source, context, options, cancellationToken).ConfigureAwait(false))
        {
            probe.Quiet = true;
            plan = TranscodePlan.Create(build, probe.Info, options, options.ConvertColorTo ?? probe.Metadata.GetColorProfile(), options.ConvertColorTo is null ? probe.Metadata.Hdr : null);
        }

        Restart(input, replay, start, context);
        if (plan.WholeImage is Action<RasterOps> whole)
        {
            using Raster raster = await Raster.LoadAsync(source, context, plan.Options, cancellationToken).ConfigureAwait(false);
            raster.Apply(whole, cancellationToken);
            await target.Save(raster, async: true, cancellationToken).ConfigureAwait(false);
            return;
        }

        using RasterDecoder decoder = await RasterDecoder.OpenAsync(source, context, plan.Options, cancellationToken).ConfigureAwait(false);
        await decoder.WithNaturalType(new AsyncWork(decoder, plan, target, context, cancellationToken)).ConfigureAwait(false);
    }

    private static void Restart(Stream input, ReplayStream? replay, long start, EgamiContext context)
    {
        if (replay is not null)
        {
            DiagnosticReporter? diagnostics = null;
            context.Emit(ref diagnostics, DiagnosticLevel.Debug, "Streaming.Replayed", $"The input cannot seek, so the {replay.Kept:N0} bytes read to plan the transcode were kept and read again.");
            replay.Rewind();
        }
        else
        {
            input.Position = start;
        }
    }

    /// <summary>The steps of one image or frame, ending in the band handed to <paramref name="write"/>.</summary>
    private static List<RowStep<TPixel>> Steps<TPixel>(TranscodePlan plan, Size size, EgamiContext context, Action<Raster<TPixel>, int> write, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        List<RowStep<TPixel>> steps = [];
        try
        {
            Size output = plan.Build(size, context, steps, cancellationToken);
            steps.Add(new BandRows<TPixel>(output.Width, context, write));
            for (int i = 0; i + 1 < steps.Count; i++)
            {
                steps[i].Next = steps[i + 1];
            }

            return steps;
        }
        catch
        {
            Dispose(steps);
            throw;
        }
    }

    private static void Dispose<TPixel>(List<RowStep<TPixel>> steps)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        foreach (RowStep<TPixel> step in steps)
        {
            step.Dispose();
        }
    }

    /// <summary>Returns the output size and the metadata to write, with the steps' changes (EXIF size, orientation) made.</summary>
    private static (Size Size, RasterMetadata Metadata) Output(RasterDecoder decoder, TranscodePlan plan)
    {
        Size size = new(decoder.Info.Width, decoder.Info.Height);
        Size[] sizes = new Size[plan.Steps.Count];
        Size step = size;
        for (int i = 0; i < sizes.Length; i++)
        {
            step = sizes[i] = plan.Steps[i].OutputSize(step);
        }

        RasterMetadata metadata = decoder.Metadata.Copy();
        new OperationPipeline.Plan(plan.Steps, sizes).UpdateMetadata(metadata, size);
        return (step, metadata);
    }

    /// <summary>
    /// Transcodes in the decoder's natural pixel type. An animation is written frame by frame when the output animates: each
    /// frame goes through the steps into one reused output frame. A first frame that turns out to be the only one is written as
    /// a still image.
    /// </summary>
    private sealed class Work(RasterDecoder decoder, TranscodePlan plan, ITranscodeTarget target, EgamiContext context, CancellationToken cancellationToken) : INaturalTypeVisitor<bool>
    {
        public bool Visit<TPixel>()
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            Size size = new(decoder.Info.Width, decoder.Info.Height);
            (Size output, RasterMetadata metadata) = Output(decoder, plan);
            using RasterEncoder<TPixel> encoder = target.Create<TPixel>(output.Width, output.Height, metadata, context);
            using Raster<TPixel> band = Raster.Create<TPixel>(size.Width, BandRows, context);
            if (decoder.Info.FrameCount == 1 || !encoder.Animates)
            {
                Pump(band, encoder.WriteRows);
                encoder.Complete();
                return true;
            }

            using Raster<TPixel> frame = Raster.Create<TPixel>(output.Width, output.Height, context);
            PumpFrame(band, frame);
            FrameMetadata first = decoder.CurrentFrame?.Copy() ?? new FrameMetadata();
            if (!decoder.NextFrame())
            {
                encoder.WriteRows(frame);
            }
            else
            {
                encoder.WriteFrame(frame, first);
                do
                {
                    PumpFrame(band, frame);
                    encoder.WriteFrame(frame, decoder.CurrentFrame);
                }
                while (decoder.NextFrame());
            }

            encoder.Complete();
            return true;
        }

        private void PumpFrame<TPixel>(Raster<TPixel> band, Raster<TPixel> frame)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            int filled = 0;
            Pump(band, (rows, count) =>
            {
                for (int y = 0; y < count; y++)
                {
                    rows.Row(y).CopyTo(frame.Row(filled++));
                }
            });
        }

        /// <summary>Reads the current frame's rows through the steps into <paramref name="write"/>.</summary>
        private void Pump<TPixel>(Raster<TPixel> band, Action<Raster<TPixel>, int> write)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            List<RowStep<TPixel>> steps = Steps(plan, new Size(band.Width, decoder.Info.Height), context, write, cancellationToken);
            try
            {
                int rows;
                while ((rows = decoder.ReadRows(band)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    for (int y = 0; y < rows; y++)
                    {
                        steps[0].Take(band.Row(y));
                    }
                }

                steps[0].End();
            }
            finally
            {
                Dispose(steps);
            }
        }
    }

    /// <summary>Transcodes as <see cref="Work"/> does, reading and writing asynchronously.</summary>
    private sealed class AsyncWork(RasterDecoder decoder, TranscodePlan plan, ITranscodeTarget target, EgamiContext context, CancellationToken cancellationToken) : INaturalTypeVisitor<Task>
    {
        public async Task Visit<TPixel>()
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            Size size = new(decoder.Info.Width, decoder.Info.Height);
            (Size output, RasterMetadata metadata) = Output(decoder, plan);
            using RasterEncoder<TPixel> encoder = target.Create<TPixel>(output.Width, output.Height, metadata, context);
            using Raster<TPixel> band = Raster.Create<TPixel>(size.Width, BandRows, context);
            encoder.Hold = true;
            if (decoder.Info.FrameCount == 1 || !encoder.Animates)
            {
                await Pump(band, encoder.WriteRows, encoder).ConfigureAwait(false);
                await encoder.CompleteAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            using Raster<TPixel> frame = Raster.Create<TPixel>(output.Width, output.Height, context);
            await PumpFrame(band, frame, encoder).ConfigureAwait(false);
            FrameMetadata first = decoder.CurrentFrame?.Copy() ?? new FrameMetadata();
            if (!await decoder.NextFrameAsync(cancellationToken).ConfigureAwait(false))
            {
                encoder.WriteRows(frame);
            }
            else
            {
                encoder.WriteFrame(frame, first);
                do
                {
                    await PumpFrame(band, frame, encoder).ConfigureAwait(false);
                    encoder.WriteFrame(frame, decoder.CurrentFrame);
                    await encoder.WriteOutAsync(cancellationToken).ConfigureAwait(false);
                }
                while (await decoder.NextFrameAsync(cancellationToken).ConfigureAwait(false));
            }

            await encoder.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }

        private Task PumpFrame<TPixel>(Raster<TPixel> band, Raster<TPixel> frame, RasterEncoder<TPixel> encoder)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            int filled = 0;
            return Pump(band, (rows, count) =>
            {
                for (int y = 0; y < count; y++)
                {
                    rows.Row(y).CopyTo(frame.Row(filled++));
                }
            }, encoder);
        }

        private async Task Pump<TPixel>(Raster<TPixel> band, Action<Raster<TPixel>, int> write, RasterEncoder<TPixel> encoder)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            List<RowStep<TPixel>> steps = Steps(plan, new Size(band.Width, decoder.Info.Height), context, write, cancellationToken);
            try
            {
                int rows;
                while ((rows = await decoder.ReadRowsAsync(band, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    for (int y = 0; y < rows; y++)
                    {
                        steps[0].Take(band.Row(y));
                    }

                    await encoder.WriteOutAsync(cancellationToken).ConfigureAwait(false);
                }

                steps[0].End();
            }
            finally
            {
                Dispose(steps);
            }
        }
    }
}

/// <summary>
/// Reads a stream once while keeping what was read, then after <see cref="Rewind"/> hands those bytes out again before going
/// on with the stream, so a header can be read twice without seeking. Only the bytes before the rewind are kept.
/// </summary>
internal sealed class ReplayStream(Stream inner) : Stream
{
    private byte[] _kept = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024);
    private int _count;
    private int _replayed = -1;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>Gets how many bytes are kept to hand out again.</summary>
    public int Kept => _count;

    /// <summary>Starts handing out the kept bytes again.</summary>
    public void Rewind()
    {
        _replayed = 0;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_replayed >= 0 && _replayed < _count)
        {
            return Replay(buffer);
        }

        int n = inner.Read(buffer);
        Keep(buffer[..n]);
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_replayed >= 0 && _replayed < _count)
        {
            return Replay(buffer.Span);
        }

        int n = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Keep(buffer.Span[..n]);
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        byte[]? kept = Interlocked.Exchange(ref _kept, null!);
        if (kept is not null)
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(kept);
        }

        base.Dispose(disposing);
    }

    private int Replay(Span<byte> buffer)
    {
        int n = Math.Min(buffer.Length, _count - _replayed);
        _kept.AsSpan(_replayed, n).CopyTo(buffer);
        _replayed += n;
        return n;
    }

    private void Keep(ReadOnlySpan<byte> read)
    {
        if (_replayed >= 0)
        {
            // Rewound already: nothing more is kept.
            return;
        }

        if (_count + read.Length > _kept.Length)
        {
            byte[] larger = System.Buffers.ArrayPool<byte>.Shared.Rent(Math.Max(_kept.Length * 2, _count + read.Length));
            _kept.AsSpan(0, _count).CopyTo(larger);
            System.Buffers.ArrayPool<byte>.Shared.Return(_kept);
            _kept = larger;
        }

        read.CopyTo(_kept.AsSpan(_count));
        _count += read.Length;
    }
}
