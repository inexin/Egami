using Egami.Video.Containers;
using Egami.Video.Trimming;

namespace Egami.Video;

public static partial class MediaFile
{
    /// <summary>
    /// Cuts the part of a file from <paramref name="start"/> up to <paramref name="end"/> into a new file, with timestamps from 0.
    /// <see cref="TrimMode.Smart"/>, the default, is frame-exact and encodes again only the frames from the start up to the next
    /// keyframe; every other packet is copied byte for byte. Video ends after the last frame that starts before
    /// <paramref name="end"/>. Audio is cut at the sample where the format allows (PCM, MP4 edit lists, Matroska Opus with
    /// CodecDelay and DiscardPadding), else at the nearest packet; subtitle cues shown across the cut are clipped to it, and
    /// chapters too. Tags and attachments are kept. The output container comes from the extension.
    /// </summary>
    /// <param name="inputPath">The source file.</param>
    /// <param name="outputPath">The file to write; an existing file is replaced.</param>
    /// <param name="start">Where the cut starts: its first frame is the first at or after this time.</param>
    /// <param name="end">Where the cut ends: nothing that starts at or after it is kept; <see langword="null"/> for the end of the file.</param>
    /// <param name="mode">How a start between keyframes is handled. Default <see cref="TrimMode.Smart"/>.</param>
    /// <param name="tracks">Which tracks to keep, or <see langword="null"/> for all.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>. Its <see cref="MediaContext.Warnings"/> gets what <see cref="TrimResult.Warnings"/> lists.</param>
    /// <param name="progress">Receives <see cref="MediaProgress"/> reports on the job's thread, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Stops the job between packets and frames; the output file is then deleted.</param>
    /// <returns>Where the cut starts and what was copied and encoded again.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="start"/> is negative, or at or past the last frame.</exception>
    /// <exception cref="ArgumentException"><paramref name="end"/> is not after <paramref name="start"/>, or no track is kept.</exception>
    /// <exception cref="UnsupportedFormatException">The input is not a registered container, or no container writes the output's extension.</exception>
    /// <exception cref="NotSupportedException">The output container cannot hold one of the tracks, or <see cref="TrimMode.Reencode"/> was asked for a codec that cannot encode.</exception>
    /// <exception cref="OperationCanceledException">The job was cancelled.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/VideoTrim.cs" region="video-trim"/>
    /// </example>
    public static TrimResult Trim(string inputPath, string outputPath, MediaTime start, MediaTime? end, TrimMode mode = TrimMode.Smart, Func<TrackInfo, bool>? tracks = null,
        MediaContext? context = null, IProgress<MediaProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputPath);
        ArgumentNullException.ThrowIfNull(outputPath);
        CheckRange(start, end, mode);
        context ??= MediaContext.Default;
        ContainerFormat format = FormatForPath(outputPath, context);
        return RunTrim(OpenRead(inputPath), false, new JobOutput(outputPath, null, null, false), format, start, end, mode, tracks, context, progress, cancellationToken);
    }

    /// <summary>
    /// Cuts part of a stream into another, as
    /// <see cref="Trim(string, string, MediaTime, MediaTime?, TrimMode, Func{TrackInfo, bool}?, MediaContext?, IProgress{MediaProgress}?, CancellationToken)"/>
    /// does for files. The input may be non-seekable (it is then read from its start, without seeking near the cut); the
    /// output works as for <see cref="Remux(Stream, Stream, string, Func{TrackInfo, bool}?, MediaContext?, bool, IProgress{MediaProgress}?, CancellationToken)"/>.
    /// </summary>
    /// <param name="input">The source, read from its current position.</param>
    /// <param name="output">Where to write.</param>
    /// <param name="outputFormat">The output container: "Matroska", ".mkv", "webm" and so on.</param>
    /// <param name="start">Where the cut starts: its first frame is the first at or after this time.</param>
    /// <param name="end">Where the cut ends, or <see langword="null"/> for the end of the input.</param>
    /// <param name="mode">How a start between keyframes is handled. Default <see cref="TrimMode.Smart"/>.</param>
    /// <param name="tracks">Which tracks to keep, or <see langword="null"/> for all.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <param name="leaveOpen">Whether the job leaves both streams open when it ends.</param>
    /// <param name="progress">Receives <see cref="MediaProgress"/> reports on the job's thread, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Stops the job between packets and frames; what was written to <paramref name="output"/> stays.</param>
    /// <returns>Where the cut starts and what was copied and encoded again.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="start"/> is negative, or at or past the last frame.</exception>
    /// <exception cref="ArgumentException"><paramref name="end"/> is not after <paramref name="start"/>, or no track is kept.</exception>
    /// <exception cref="UnsupportedFormatException">The input is not a registered container, or no container writes <paramref name="outputFormat"/>.</exception>
    /// <exception cref="NotSupportedException">The output container cannot hold one of the tracks or needs a seekable output, or <see cref="TrimMode.Reencode"/> was asked for a codec that cannot encode.</exception>
    /// <exception cref="OperationCanceledException">The job was cancelled.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/VideoTrim.cs" region="video-trim-streams"/>
    /// </example>
    public static TrimResult Trim(Stream input, Stream output, string outputFormat, MediaTime start, MediaTime? end, TrimMode mode = TrimMode.Smart, Func<TrackInfo, bool>? tracks = null,
        MediaContext? context = null, bool leaveOpen = false, IProgress<MediaProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(outputFormat);
        CheckRange(start, end, mode);
        context ??= MediaContext.Default;
        ContainerFormat format = context.FindContainer(outputFormat) is { CanMux: true } found ? found : throw new UnsupportedFormatException($"No registered container writes '{outputFormat}'.");
        return RunTrim(input, leaveOpen, new JobOutput(null, output, outputFormat, leaveOpen), format, start, end, mode, tracks, context, progress, cancellationToken);
    }

    private static void CheckRange(MediaTime start, MediaTime? end, TrimMode mode)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(start, MediaTime.Zero);
        if (end is { } stop && stop <= start)
        {
            throw new ArgumentException($"The end ({stop.Seconds:0.###} s) must be after the start ({start.Seconds:0.###} s).", nameof(end));
        }

        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown trim mode.");
        }
    }

    /// <summary>The trim job: opens the input, runs <see cref="TrimJob"/> into the output, and handles progress and cancellation as <see cref="Mux"/> does.</summary>
    private static TrimResult RunTrim(Stream input, bool leaveInputOpen, JobOutput target, ContainerFormat format, MediaTime start, MediaTime? end, TrimMode mode,
        Func<TrackInfo, bool>? tracks, MediaContext context, IProgress<MediaProgress>? progress, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            long inputLength = progress is not null && input.CanSeek ? Math.Max(0, input.Length - input.Position) : 0;
            JobStream? counted = progress is null ? null : new JobStream(input);
            using IDemuxer demuxer = OpenDemuxer(counted ?? input, context, leaveOpen: true);
            TrackInfo[] kept = [.. demuxer.Tracks.Where(t => tracks?.Invoke(t) ?? true)];
            if (kept.Length == 0)
            {
                throw new ArgumentException("No track is left to write.", nameof(tracks));
            }

            FileStream? file = target.Path is null ? null : new FileStream(target.Path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16);
            Stream stream = file ?? target.Stream!;
            JobStream? sink = progress is not null || cancellationToken.CanBeCanceled ? new JobStream(stream) : null;
            MediaTime? length = end is { } stop ? stop - start : demuxer.Info.Duration is { } d && d > start ? d - start : null;
            ProgressReporter? reporter = progress is null ? null : new ProgressReporter(progress, length, kept, counted, inputLength, sink);
            TrimJob job = new(demuxer, kept, start, end, mode, format, context, (outputs, metadata) =>
            {
                IMuxer muxer = format.CreateMuxer(sink ?? stream, outputs, metadata, context, leaveOpen: true);
                reporter?.Start();
                return muxer;
            }, reporter is null ? null : reporter.Written, cancellationToken);
            TrimResult result;
            try
            {
                result = job.Run();
            }
            catch (Exception e)
            {
                // A failed or cancelled job leaves no file: the muxer's last writes fail at once, and a file the job created goes.
                job.Dispose();
                if (e is OperationCanceledException)
                {
                    sink?.Abandon();
                }

                try
                {
                    job.Muxer?.Dispose();
                }
                catch (Exception) when (e is OperationCanceledException or InvalidDataException or ArgumentException or NotSupportedException)
                {
                    // The job's own error is the one to report.
                }

                CloseOutput(target, file, deleteFile: true);
                throw;
            }

            job.Dispose();
            try
            {
                job.Muxer?.Dispose();
            }
            finally
            {
                CloseOutput(target, file, deleteFile: false);
            }

            reporter?.Finish();
            return result;
        }
        finally
        {
            if (!leaveInputOpen)
            {
                input.Dispose();
            }

            if (target.Stream is not null && !target.LeaveOpen)
            {
                target.Stream.Dispose();
            }
        }
    }
}
