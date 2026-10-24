using Egami.Video.Audio;
using Egami.Video.Audio.Codecs;
using Egami.Video.Containers;
using Egami.Video.Containers.Flac;

namespace Egami.Video;

/// <summary>
/// Entry points for media files: probe what a file holds, open a demuxer to read its packets, or create a muxer to write
/// one. The container is detected from the file's first bytes when reading, and chosen by extension when writing.
/// </summary>
/// <example>
/// <code language="csharp">
/// MediaInfo info = MediaFile.Probe("in.avi");
/// using IDemuxer demuxer = MediaFile.OpenDemuxer("in.avi");
/// using IMuxer muxer = MediaFile.CreateMuxer("out.mkv", demuxer.Tracks);
/// while (demuxer.ReadPacket() is { } packet)
/// {
///     using (packet)
///         muxer.WritePacket(packet);
/// }
/// </code>
/// </example>
public static partial class MediaFile
{
    /// <summary>Reads a file's headers: format, duration, tracks and tags. No frame is decoded.</summary>
    /// <param name="path">The file path.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <returns>What the file holds.</returns>
    /// <exception cref="UnsupportedFormatException">No registered container matches the file.</exception>
    /// <exception cref="CorruptDataException">The headers break the format's rules.</exception>
    /// <exception cref="LimitExceededException">The file breaks a limit.</exception>
    public static MediaInfo Probe(string path, MediaContext? context = null)
    {
        using IDemuxer demuxer = OpenDemuxer(path, context);
        return demuxer.Info;
    }

    /// <summary>Reads a stream's headers. See <see cref="Probe(string, MediaContext?)"/>.</summary>
    /// <param name="stream">The file, read from its current position and left open.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <returns>What the file holds.</returns>
    /// <exception cref="UnsupportedFormatException">No registered container matches the file.</exception>
    /// <exception cref="CorruptDataException">The headers break the format's rules.</exception>
    /// <exception cref="LimitExceededException">The file breaks a limit.</exception>
    public static MediaInfo Probe(Stream stream, MediaContext? context = null)
    {
        using IDemuxer demuxer = OpenDemuxer(stream, context, leaveOpen: true);
        return demuxer.Info;
    }

    /// <summary>Reads a file's headers on the thread pool. See <see cref="Probe(string, MediaContext?)"/>.</summary>
    /// <param name="path">The file path.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <param name="cancellationToken">Cancels before the read starts.</param>
    /// <returns>What the file holds.</returns>
    public static Task<MediaInfo> ProbeAsync(string path, MediaContext? context = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Task.Run(() => Probe(path, context), cancellationToken);
    }

    /// <summary>
    /// Reads a stream's headers. A stream that is not a <see cref="FileStream"/> or <see cref="MemoryStream"/> is first copied
    /// into memory asynchronously, as Egami's <c>LoadAsync</c> does, so no thread blocks on network or pipe reads.
    /// </summary>
    /// <param name="stream">The file, read from its current position and left open.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <param name="cancellationToken">Cancels the copy.</param>
    /// <returns>What the file holds.</returns>
    public static async Task<MediaInfo> ProbeAsync(Stream stream, MediaContext? context = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream is FileStream or MemoryStream)
        {
            return await Task.Run(() => Probe(stream, context), cancellationToken).ConfigureAwait(false);
        }

        using MemoryStream buffer = new();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        buffer.Position = 0;
        return Probe(buffer, context);
    }

    /// <summary>Opens a file for reading packets.</summary>
    /// <param name="path">The file path.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <returns>The demuxer, which owns the file. The caller must dispose it.</returns>
    /// <exception cref="UnsupportedFormatException">No registered container matches the file.</exception>
    /// <exception cref="CorruptDataException">The headers break the format's rules.</exception>
    /// <exception cref="LimitExceededException">The file breaks a limit.</exception>
    public static IDemuxer OpenDemuxer(string path, MediaContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        try
        {
            return OpenDemuxer(stream, context, leaveOpen: false);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens a stream for reading packets, from its current position. A non-seekable stream works too: its first bytes are
    /// buffered for detection and replayed, and the demuxer cannot seek.
    /// </summary>
    /// <param name="stream">The file.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <param name="leaveOpen">Whether disposing the demuxer leaves the stream open.</param>
    /// <returns>The demuxer. The caller must dispose it.</returns>
    /// <exception cref="UnsupportedFormatException">No registered container matches the file.</exception>
    /// <exception cref="CorruptDataException">The headers break the format's rules.</exception>
    /// <exception cref="LimitExceededException">The file breaks a limit.</exception>
    public static IDemuxer OpenDemuxer(Stream stream, MediaContext? context = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        context ??= MediaContext.Default;
        (ContainerFormat format, Stream input) = Detect(stream, context);
        return format.OpenDemuxer(input, context, leaveOpen);
    }

    /// <summary>Opens a file for reading packets on the thread pool. See <see cref="OpenDemuxer(string, MediaContext?)"/>.</summary>
    /// <param name="path">The file path.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <param name="cancellationToken">Cancels before the open starts.</param>
    /// <returns>The demuxer. The caller must dispose it.</returns>
    public static Task<IDemuxer> OpenDemuxerAsync(string path, MediaContext? context = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Task.Run(() => OpenDemuxer(path, context), cancellationToken);
    }

    /// <summary>Creates a file and a muxer for it, choosing the container by the file's extension. An existing file is replaced.</summary>
    /// <param name="path">The file path.</param>
    /// <param name="tracks">The tracks to write, numbered as the packets will be.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <param name="metadata">Tags, chapters and attachments to write, or <see langword="null"/> for none.</param>
    /// <returns>The muxer, which owns the file. The caller must dispose it.</returns>
    /// <exception cref="UnsupportedFormatException">No registered container writes the extension.</exception>
    /// <exception cref="NotSupportedException">The container cannot hold one of the tracks.</exception>
    public static IMuxer CreateMuxer(string path, IReadOnlyList<TrackInfo> tracks, MediaContext? context = null, MediaMetadata? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        context ??= MediaContext.Default;
        ContainerFormat format = FormatForPath(path, context);
        FileStream stream = new(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16);
        try
        {
            return format.CreateMuxer(stream, tracks, metadata ?? MediaMetadata.Empty, context, leaveOpen: false);
        }
        catch
        {
            stream.Dispose();
            File.Delete(path);
            throw;
        }
    }

    /// <summary>Creates a muxer that writes to a stream in the named container.</summary>
    /// <param name="stream">Where to write. A non-seekable stream gets the container's streaming layout, when it has one.</param>
    /// <param name="formatNameOrExtension">"AVI", "Matroska", ".mkv", "webm" and so on.</param>
    /// <param name="tracks">The tracks to write, numbered as the packets will be.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <param name="leaveOpen">Whether disposing the muxer leaves the stream open.</param>
    /// <param name="metadata">Tags, chapters and attachments to write, or <see langword="null"/> for none.</param>
    /// <returns>The muxer. The caller must dispose it.</returns>
    /// <exception cref="UnsupportedFormatException">No registered container has that name or extension, or it cannot write.</exception>
    /// <exception cref="NotSupportedException">The container cannot hold one of the tracks.</exception>
    public static IMuxer CreateMuxer(Stream stream, string formatNameOrExtension, IReadOnlyList<TrackInfo> tracks, MediaContext? context = null, bool leaveOpen = false, MediaMetadata? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(formatNameOrExtension);
        context ??= MediaContext.Default;
        ContainerFormat format = context.FindContainer(formatNameOrExtension) is { CanMux: true } found
            ? found
            : throw new UnsupportedFormatException($"No registered container writes '{formatNameOrExtension}'.");
        return format.CreateMuxer(stream, tracks, metadata ?? MediaMetadata.Empty, context, leaveOpen);
    }

    /// <summary>
    /// Copies the packets of one file into another container without decoding, for example AVI to Matroska keeping the
    /// sound, with its tags, chapters and attachments where the output container can hold them. The output container comes
    /// from the extension.
    /// </summary>
    /// <param name="inputPath">The source file.</param>
    /// <param name="outputPath">The file to write; an existing file is replaced.</param>
    /// <param name="tracks">Which tracks to keep, or <see langword="null"/> for all.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <param name="progress">Receives <see cref="MediaProgress"/> reports on the job's thread, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Stops the job between packets; the output file is then deleted.</param>
    /// <returns>How many packets were copied.</returns>
    /// <exception cref="UnsupportedFormatException">The input is not a registered container, or no container writes the output's extension.</exception>
    /// <exception cref="NotSupportedException">The output container cannot hold one of the tracks.</exception>
    /// <exception cref="OperationCanceledException">The job was cancelled.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Video.cs" region="video-progress"/>
    /// </example>
    public static long Remux(string inputPath, string outputPath, Func<TrackInfo, bool>? tracks = null, MediaContext? context = null, IProgress<MediaProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputPath);
        ArgumentNullException.ThrowIfNull(outputPath);
        return RunJob(OpenRead(inputPath), false, new JobOutput(outputPath, null, null, false), null, null, tracks, context ?? MediaContext.Default, progress, cancellationToken);
    }

    /// <summary>
    /// Copies the packets of one stream into another container without decoding, as
    /// <see cref="Remux(string, string, Func{TrackInfo, bool}?, MediaContext?, IProgress{MediaProgress}?, CancellationToken)"/>
    /// does for files: for example from one cloud blob to another without a local copy. The input may be non-seekable, except
    /// for MP4 files with moov after the media. Matroska, WebM, IVF and YUV4MPEG2 write to a non-seekable stream as they go;
    /// so does MP4, written fragmented there with an mp4-fragmented warning (see <see cref="Mp4Format.NonSeekable"/>); AVI
    /// needs a seekable stream.
    /// </summary>
    /// <param name="input">The source, read from its current position.</param>
    /// <param name="output">Where to write.</param>
    /// <param name="outputFormat">The output container: "Matroska", ".mkv", "webm" and so on.</param>
    /// <param name="tracks">Which tracks to keep, or <see langword="null"/> for all.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <param name="leaveOpen">Whether the job leaves both streams open when it ends.</param>
    /// <param name="progress">Receives <see cref="MediaProgress"/> reports on the job's thread, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Stops the job between packets; what was written to <paramref name="output"/> stays.</param>
    /// <returns>How many packets were copied.</returns>
    /// <exception cref="UnsupportedFormatException">The input is not a registered container, or no container writes <paramref name="outputFormat"/>.</exception>
    /// <exception cref="NotSupportedException">The output container cannot hold one of the tracks, or needs a seekable output.</exception>
    /// <exception cref="OperationCanceledException">The job was cancelled.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Video.cs" region="video-streams"/>
    /// </example>
    public static long Remux(Stream input, Stream output, string outputFormat, Func<TrackInfo, bool>? tracks = null, MediaContext? context = null, bool leaveOpen = false, IProgress<MediaProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(outputFormat);
        return RunJob(input, leaveOpen, new JobOutput(null, output, outputFormat, leaveOpen), null, null, tracks, context ?? MediaContext.Default, progress, cancellationToken);
    }

    /// <summary>
    /// Copies a file into another container like <see cref="Remux(string, string, Func{TrackInfo, bool}?, MediaContext?, IProgress{MediaProgress}?, CancellationToken)"/>,
    /// but decodes its audio tracks and encodes them again with <paramref name="audio"/>, at the same rate, layout and bit depth:
    /// for example keeping a WebM's video and turning its audio into FLAC in Matroska. Audio no registered codec decodes is
    /// copied as it is. The output container comes from the extension. Without a codec, a WebM output gets its audio in Opus:
    /// Opus and Vorbis tracks are copied and the others encoded, so a Matroska or MP4 source turns into a valid WebM.
    /// </summary>
    /// <param name="inputPath">The source file.</param>
    /// <param name="outputPath">The file to write; an existing file is replaced.</param>
    /// <param name="audio">The audio codec to encode with, or <see langword="null"/> to copy audio too (to WebM: to encode it in Opus unless it is Opus or Vorbis).</param>
    /// <param name="audioLevel">The audio codec's compression level, or <see langword="null"/> for its default.</param>
    /// <param name="tracks">Which tracks to keep, or <see langword="null"/> for all.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <param name="progress">Receives <see cref="MediaProgress"/> reports on the job's thread, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Stops the job between packets; the output file is then deleted.</param>
    /// <returns>How many packets were written.</returns>
    /// <exception cref="UnsupportedFormatException">The input is not a registered container, or no container writes the output's extension.</exception>
    /// <exception cref="NotSupportedException">The output container cannot hold one of the tracks, or the codec cannot encode.</exception>
    /// <exception cref="OperationCanceledException">The job was cancelled.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Audio.cs" region="audio-transcode"/>
    /// </example>
    public static long Transcode(string inputPath, string outputPath, AudioCodec? audio = null, int? audioLevel = null, Func<TrackInfo, bool>? tracks = null, MediaContext? context = null, IProgress<MediaProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputPath);
        ArgumentNullException.ThrowIfNull(outputPath);
        ThrowIfCannotEncode(audio);
        context ??= MediaContext.Default;
        bool webM = audio is null && FormatForPath(outputPath, context) == MatroskaFormat.WebM;
        return RunJob(OpenRead(inputPath), false, new JobOutput(outputPath, null, null, false), webM ? OpusCodec.Instance : audio, audioLevel, tracks, context, progress, cancellationToken, webM);
    }

    /// <summary>
    /// Copies a stream into another container, encoding its audio again, as
    /// <see cref="Transcode(string, string, AudioCodec?, int?, Func{TrackInfo, bool}?, MediaContext?, IProgress{MediaProgress}?, CancellationToken)"/>
    /// does for files. The streams work as for <see cref="Remux(Stream, Stream, string, Func{TrackInfo, bool}?, MediaContext?, bool, IProgress{MediaProgress}?, CancellationToken)"/>.
    /// </summary>
    /// <param name="input">The source, read from its current position.</param>
    /// <param name="output">Where to write.</param>
    /// <param name="outputFormat">The output container: "Matroska", ".mkv", "webm" and so on.</param>
    /// <param name="audio">The audio codec to encode with, or <see langword="null"/> to copy audio too (to WebM: to encode it in Opus unless it is Opus or Vorbis).</param>
    /// <param name="audioLevel">The audio codec's compression level, or <see langword="null"/> for its default.</param>
    /// <param name="tracks">Which tracks to keep, or <see langword="null"/> for all.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <param name="leaveOpen">Whether the job leaves both streams open when it ends.</param>
    /// <param name="progress">Receives <see cref="MediaProgress"/> reports on the job's thread, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Stops the job between packets; what was written to <paramref name="output"/> stays.</param>
    /// <returns>How many packets were written.</returns>
    /// <exception cref="UnsupportedFormatException">The input is not a registered container, or no container writes <paramref name="outputFormat"/>.</exception>
    /// <exception cref="NotSupportedException">The output container cannot hold one of the tracks or needs a seekable output, or the codec cannot encode.</exception>
    /// <exception cref="OperationCanceledException">The job was cancelled.</exception>
    public static long Transcode(Stream input, Stream output, string outputFormat, AudioCodec? audio = null, int? audioLevel = null, Func<TrackInfo, bool>? tracks = null, MediaContext? context = null, bool leaveOpen = false, IProgress<MediaProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(outputFormat);
        ThrowIfCannotEncode(audio);
        context ??= MediaContext.Default;
        bool webM = audio is null && context.FindContainer(outputFormat) == MatroskaFormat.WebM;
        return RunJob(input, leaveOpen, new JobOutput(null, output, outputFormat, leaveOpen), webM ? OpusCodec.Instance : audio, audioLevel, tracks, context, progress, cancellationToken, webM);
    }

    private static void ThrowIfCannotEncode(AudioCodec? audio)
    {
        if (audio is { CanEncode: false })
        {
            throw new NotSupportedException($"{audio.Name} cannot encode.");
        }
    }

    private static readonly Dictionary<int, (IAudioDecoder Decoder, IAudioEncoder Encoder)> Empty = [];

    /// <summary>The rate Opus always decodes at, whatever rate its header says the source had.</summary>
    private const int OpusDecodeRate = 48000;

    private static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);

    /// <summary>
    /// The remux and transcode loop. Owns <paramref name="input"/> unless <paramref name="leaveInputOpen"/>. Without a handler
    /// and with a token that cannot be cancelled it runs exactly as a plain demux-mux loop: no wrapper, no clock. With
    /// <paramref name="keepWebMAudio"/> Vorbis tracks are copied too, as WebM holds them.
    /// </summary>
    private static long RunJob(Stream input, bool leaveInputOpen, JobOutput target, AudioCodec? audio, int? audioLevel, Func<TrackInfo, bool>? tracks, MediaContext context, IProgress<MediaProgress>? progress, CancellationToken cancellationToken, bool keepWebMAudio = false)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            long inputLength = progress is not null && input.CanSeek ? Math.Max(0, input.Length - input.Position) : 0;
            JobStream? counted = progress is null ? null : new JobStream(input);
            using IDemuxer demuxer = OpenDemuxer(counted ?? input, context, leaveOpen: true);
            TrackInfo[] kept = [.. demuxer.Tracks.Where(t => tracks?.Invoke(t) ?? true)];
            Dictionary<int, (IAudioDecoder Decoder, IAudioEncoder Encoder)>? recoded = audio is null ? null : [];
            try
            {
                for (int i = 0; i < kept.Length && recoded is not null; i++)
                {
                    if (kept[i] is not AudioTrackInfo track || track.CodecId == audio!.CodecId || (keepWebMAudio && track.CodecId == CodecIds.Vorbis) || context.FindAudioCodec(track) is not { } codec)
                    {
                        continue;
                    }

                    IAudioDecoder decoder = codec.CreateDecoder(track, new AudioDecoderOptions { Errors = DecodeErrorMode.Conceal }, context);
                    IAudioEncoder encoder;
                    try
                    {
                        encoder = audio.CreateEncoder(
                            new AudioEncoderOptions
                            {
                                SampleRate = track.CodecId == CodecIds.Opus ? OpusDecodeRate : track.SampleRate,
                                Layout = track.EffectiveLayout,
                                BitsPerSample = audio.CodecId == CodecIds.PcmFloat ? 32 : track.BitsPerSample is > 0 and <= 32 && track.CodecId != CodecIds.PcmFloat ? track.BitsPerSample : audio.CodecId == CodecIds.Flac ? 24 : 16,
                                Level = audioLevel,
                            },
                            context);
                    }
                    catch
                    {
                        decoder.Dispose();
                        throw;
                    }

                    recoded[track.Number] = (decoder, encoder);
                    kept[i] = encoder.Track with { Number = track.Number, Language = track.Language, Name = track.Name, IsDefault = track.IsDefault, Tags = track.Tags };
                }

                return Mux(demuxer, kept, recoded, target, context, progress, counted, inputLength, cancellationToken);
            }
            finally
            {
                if (recoded is not null)
                {
                    foreach ((IAudioDecoder decoder, IAudioEncoder encoder) in recoded.Values)
                    {
                        decoder.Dispose();
                        encoder.Dispose();
                    }
                }
            }
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

    private static long Mux(IDemuxer demuxer, TrackInfo[] kept, Dictionary<int, (IAudioDecoder Decoder, IAudioEncoder Encoder)>? recoded, JobOutput target, MediaContext context, IProgress<MediaProgress>? progress, JobStream? input, long inputLength, CancellationToken cancellationToken)
    {
        HashSet<int> numbers = [.. kept.Select(t => t.Number)];
        ContainerFormat format = target.Path is { } path
            ? FormatForPath(path, context)
            : context.FindContainer(target.Format!) is { CanMux: true } found ? found : throw new UnsupportedFormatException($"No registered container writes '{target.Format}'.");
        FileStream? file = target.Path is null ? null : new FileStream(target.Path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16);
        Stream stream = file ?? target.Stream!;
        JobStream? sink = progress is not null || cancellationToken.CanBeCanceled ? new JobStream(stream) : null;
        IMuxer? muxer = null;
        try
        {
            muxer = format.CreateMuxer(sink ?? stream, kept, demuxer.Info.Metadata, context, leaveOpen: true);
        }
        catch
        {
            CloseOutput(target, file, deleteFile: true);
            throw;
        }

        ProgressReporter? reporter = progress is null ? null : new ProgressReporter(progress, demuxer.Info.Duration, kept, input, inputLength, sink);
        long count = 0;
        void Write(IMuxer muxer, EncodedPacket packet)
        {
            muxer.WritePacket(packet);
            count++;
            reporter?.Written(packet);
        }

        void Drain(IMuxer muxer, int number, IAudioDecoder decoder, IAudioEncoder encoder)
        {
            while (decoder.TryReceive(out AudioFrame? frame))
            {
                using (frame)
                {
                    encoder.Send(frame);
                }
            }

            while (encoder.TryReceive(out EncodedPacket? packet))
            {
                using (packet)
                {
                    packet.TrackNumber = number;
                    Write(muxer, packet);
                }
            }
        }

        try
        {
            reporter?.Start();
            while (demuxer.ReadPacket() is { } packet)
            {
                using (packet)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!numbers.Contains(packet.TrackNumber))
                    {
                        continue;
                    }

                    if (recoded is not null && recoded.TryGetValue(packet.TrackNumber, out var pair))
                    {
                        pair.Decoder.Send(packet);
                        Drain(muxer, packet.TrackNumber, pair.Decoder, pair.Encoder);
                    }
                    else
                    {
                        Write(muxer, packet);
                    }
                }
            }

            foreach ((int number, (IAudioDecoder decoder, IAudioEncoder encoder)) in recoded ?? Empty)
            {
                cancellationToken.ThrowIfCancellationRequested();
                decoder.Flush();
                Drain(muxer, number, decoder, encoder);
                encoder.Flush();
                Drain(muxer, number, decoder, encoder);
                if (muxer is ITrackUpdatable updatable)
                {
                    updatable.UpdateTrack(encoder.Track);
                }
            }

            muxer.Finish();
        }
        catch (OperationCanceledException)
        {
            // Stop without finishing the file: the muxer's last writes fail at once, and a file the job created goes.
            sink?.Abandon();
            try
            {
                muxer.Dispose();
            }
            catch (OperationCanceledException)
            {
            }

            muxer = null;
            CloseOutput(target, file, deleteFile: true);
            throw;
        }
        finally
        {
            if (muxer is not null)
            {
                try
                {
                    muxer.Dispose();
                }
                finally
                {
                    CloseOutput(target, file, deleteFile: false);
                }
            }
        }

        reporter?.Finish();
        return count;
    }

    private static void CloseOutput(JobOutput target, FileStream? file, bool deleteFile)
    {
        if (file is not null)
        {
            file.Dispose();
            if (deleteFile)
            {
                File.Delete(target.Path!);
            }
        }
    }

    /// <summary>Where a job writes: a file it creates, or a caller's stream in a named container.</summary>
    private readonly record struct JobOutput(string? Path, Stream? Stream, string? Format, bool LeaveOpen);

    internal static ContainerFormat FormatForPath(string path, MediaContext context)
    {
        string extension = Path.GetExtension(path);
        return extension.Length > 1 && context.FindContainer(extension) is { CanMux: true } format
            ? format
            : throw new UnsupportedFormatException($"No registered container writes '{extension}' files.");
    }

    private static (ContainerFormat Format, Stream Input) Detect(Stream stream, MediaContext context)
    {
        int needed = context.Containers.Count == 0 ? 0 : context.Containers.Max(f => f.SignatureLength);
        byte[] header = new byte[needed];
        Stream input = stream;
        int read;
        if (stream.CanSeek)
        {
            long start = stream.Position;
            read = stream.ReadAtLeast(header, needed, throwOnEndOfStream: false);
            stream.Position = start;
        }
        else
        {
            read = stream.ReadAtLeast(header, needed, throwOnEndOfStream: false);
            input = new ReplayStream(header.AsMemory(0, read), stream);
        }

        foreach (ContainerFormat format in context.Containers)
        {
            if (format.IsMatch(header.AsSpan(0, Math.Min(read, format.SignatureLength))))
            {
                return (format, input);
            }
        }

        throw new UnsupportedFormatException("No registered container matches the data.");
    }
}
