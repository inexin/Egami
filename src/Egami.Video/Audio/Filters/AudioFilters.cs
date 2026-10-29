using Egami.Video.Containers;

namespace Egami.Video.Audio.Filters;

/// <summary>What <see cref="AudioFilters.NormalizeLoudnessAsync"/> did to one audio track.</summary>
/// <param name="TrackNumber">The track's number.</param>
/// <param name="Measured">The track's loudness before, from the first pass.</param>
/// <param name="Gain">The gain applied in decibels.</param>
/// <param name="Limited">Whether the true-peak limiter ran, because the gain would have taken the peaks over the ceiling.</param>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/AudioFilters.cs" region="audio-loudness"/>
/// </example>
public sealed record LoudnessNormalization(int TrackNumber, LoudnessReport Measured, double Gain, bool Limited);

/// <summary>
/// Whole-file jobs with audio filters: the audio tracks are decoded, filtered and encoded again while every other track is
/// copied as it is, the way <see cref="MediaFile.Transcode(string, string, AudioCodec?, int?, Func{TrackInfo, bool}?, MediaContext?, IProgress{MediaProgress}?, CancellationToken)"/>
/// copies them. Filtered tracks are encoded with the codec given; else, in Matroska and MP4, with their own codec when
/// Egami.Video encodes it and FLAC when not, and in the other containers with the container's usual codec (FLAC in .flac,
/// Opus in WebM and Ogg, PCM in WAV and AIFF) unless the track's own is of the same kind.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/AudioFilters.cs" region="audio-loudness"/>
/// </example>
public static class AudioFilters
{
    /// <summary>
    /// Brings every audio track of a file to a loudness target in two passes: the first measures each track with
    /// <see cref="LoudnessMeter"/>, the second applies a linear <see cref="LoudnessNormalizer"/> as the file is copied, so the
    /// dynamics stay as they are and a true-peak limiter runs only where it has to.
    /// </summary>
    /// <param name="inputPath">The source file.</param>
    /// <param name="outputPath">The file to write; an existing file is replaced.</param>
    /// <param name="target">The target, or <see langword="null"/> for <see cref="LoudnessTarget.EbuR128"/>.</param>
    /// <param name="audio">The audio codec to encode with, or <see langword="null"/> for each track's own (see <see cref="AudioFilters"/>).</param>
    /// <param name="audioLevel">The audio codec's compression level, or <see langword="null"/> for its default.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <param name="progress">Receives <see cref="MediaProgress"/> reports for the second pass, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Stops the job; the output file is then deleted.</param>
    /// <returns>What was done to each audio track, in track order.</returns>
    /// <exception cref="UnsupportedFormatException">The input is not a registered container, or no container writes the output's extension.</exception>
    /// <exception cref="NotSupportedException">The file has no audio track Egami.Video decodes, or the codec cannot encode.</exception>
    /// <exception cref="OperationCanceledException">The job was cancelled.</exception>
    public static Task<IReadOnlyList<LoudnessNormalization>> NormalizeLoudnessAsync(string inputPath, string outputPath, LoudnessTarget? target = null,
        AudioCodec? audio = null, int? audioLevel = null, MediaContext? context = null, IProgress<MediaProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputPath);
        ArgumentNullException.ThrowIfNull(outputPath);
        target ??= LoudnessTarget.EbuR128;
        context ??= MediaContext.Default;
        return Task.Run<IReadOnlyList<LoudnessNormalization>>(
            () =>
            {
                Dictionary<int, LoudnessReport> reports = Measure(inputPath, context, cancellationToken);
                List<LoudnessNormalization> done = [];
                MediaFile.Filter(
                    inputPath,
                    outputPath,
                    track =>
                    {
                        if (!reports.TryGetValue(track.Number, out LoudnessReport? report))
                        {
                            return null;
                        }

                        LoudnessNormalizer normalizer = new(target, report);
                        done.Add(new LoudnessNormalization(track.Number, report, normalizer.Gain, normalizer.Limits));
                        return normalizer;
                    },
                    audio,
                    audioLevel,
                    null,
                    context,
                    progress,
                    cancellationToken);
                return done;
            },
            cancellationToken);
    }

    /// <summary>
    /// Copies a file, running each audio track through the filter <paramref name="filters"/> makes for it. A filter that
    /// changes the length, such as <see cref="Tempo"/> or <see cref="SilenceRemover"/>, takes the sound out of step with the
    /// video, so use one only on a file without video or when that is what you want.
    /// </summary>
    /// <param name="inputPath">The source file.</param>
    /// <param name="outputPath">The file to write; an existing file is replaced.</param>
    /// <param name="filters">Makes the filter for an audio track, or returns <see langword="null"/> to copy the track as it is. The job disposes the filters.</param>
    /// <param name="audio">The audio codec to encode with, or <see langword="null"/> for each track's own (see <see cref="AudioFilters"/>).</param>
    /// <param name="audioLevel">The audio codec's compression level, or <see langword="null"/> for its default.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <param name="progress">Receives <see cref="MediaProgress"/> reports, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Stops the job; the output file is then deleted.</param>
    /// <returns>How many packets were written.</returns>
    /// <exception cref="UnsupportedFormatException">The input is not a registered container, or no container writes the output's extension.</exception>
    /// <exception cref="NotSupportedException">The output container cannot hold one of the tracks, or the codec cannot encode.</exception>
    /// <exception cref="OperationCanceledException">The job was cancelled.</exception>
    public static Task<long> ApplyAsync(string inputPath, string outputPath, Func<AudioTrackInfo, IAudioFilter?> filters, AudioCodec? audio = null,
        int? audioLevel = null, MediaContext? context = null, IProgress<MediaProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputPath);
        ArgumentNullException.ThrowIfNull(outputPath);
        ArgumentNullException.ThrowIfNull(filters);
        context ??= MediaContext.Default;
        return Task.Run(() => MediaFile.Filter(inputPath, outputPath, filters, audio, audioLevel, null, context, progress, cancellationToken), cancellationToken);
    }

    /// <summary>Measures every audio track Egami.Video decodes, one pass per track.</summary>
    private static Dictionary<int, LoudnessReport> Measure(string path, MediaContext context, CancellationToken cancellationToken)
    {
        MediaInfo info = MediaFile.Probe(path, context);
        Dictionary<int, LoudnessReport> reports = [];
        foreach (AudioTrackInfo track in info.Tracks.OfType<AudioTrackInfo>())
        {
            if (context.FindAudioCodec(track) is null)
            {
                continue;
            }

            using AudioReader reader = AudioReader.Open(path, new AudioReaderOptions { TrackNumber = track.Number }, context);
            LoudnessMeter meter = new();
            foreach (AudioFrame frame in reader.ReadFrames())
            {
                using (frame)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    meter.Add(frame);
                }
            }

            meter.Complete(meter.Duration);
            reports[track.Number] = meter.Report();
        }

        if (reports.Count == 0)
        {
            throw new NotSupportedException($"'{path}' has no audio track Egami.Video decodes.");
        }

        return reports;
    }
}
