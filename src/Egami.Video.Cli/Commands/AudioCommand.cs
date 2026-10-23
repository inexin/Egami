using System.CommandLine;
using System.Globalization;

using Egami.Video.Audio;
using Egami.Video.Audio.Codecs;
using Egami.Video.Cli.Host;
using Egami.Video.Containers;
using Egami.Video.Containers.Flac;

namespace Egami.Video.Cli.Commands;

/// <summary>
/// <c>egami-video audio</c> (Video CLI design Q8, Build Plan task 18): decodes an audio track and writes it to an audio file
/// (WAV, Wave64, FLAC, Ogg FLAC, Opus), or copies a video file's other tracks unchanged and encodes its audio tracks again
/// (Matroska, WebM, MP4, AVI), resampling, remixing and changing the sample format on the way. Like <c>ffmpeg -i in.mkv -vn out.flac</c>,
/// <c>ffmpeg -i in.webm -c:v copy -c:a flac out.mkv</c> and <c>ffmpeg -i in.flac -c:a libopus -b:a 96k out.opus</c>.
/// </summary>
internal static class AudioCommand
{
    private static readonly string[] SampleFormatNames = ["u8", "s16", "s24", "s32", "f32", "f64"];

    private static readonly Dictionary<string, ChannelLayout> LayoutNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mono"] = ChannelLayout.Mono,
        ["stereo"] = ChannelLayout.Stereo,
        ["2.1"] = ChannelLayout.Surround21,
        ["3.0"] = ChannelLayout.Surround30,
        ["quad"] = ChannelLayout.Quad,
        ["5.0"] = ChannelLayout.Surround50,
        ["5.1"] = ChannelLayout.Surround51,
        ["5.1(side)"] = ChannelLayout.Surround51Side,
        ["6.1"] = ChannelLayout.Surround61,
        ["7.1"] = ChannelLayout.Surround71,
        ["7.1.4"] = ChannelLayout.Surround714,
    };

    public static Command Create(VideoSession session)
    {
        MediaOptions options = new();
        Option<string?> codec = new("--codec", "-c") { Description = "The audio codec to write: flac, pcm or opus; by default Opus in .opus and WebM, PCM in WAV, Wave64 and AVI, and FLAC otherwise.", HelpName = "flac|pcm|opus" };
        Option<string?> bitrate = new("--bitrate", "-b") { Description = "Opus's bitrate for all channels together, such as 96k; 64k a channel by default.", HelpName = "bits/s" };
        Option<int?> rate = new("--rate", "-ar") { Description = "Resample to this many samples per second.", HelpName = "Hz" };
        Option<string?> channels = new("--channels", "-ac") { Description = "Mix to this many channels or this layout: mono, stereo, 2.1, 3.0, quad, 5.0, 5.1, 5.1(side), 6.1, 7.1 or 7.1.4.", HelpName = "count|layout" };
        Option<string?> sampleFormat = new("--sample-format") { Description = "The samples to store: u8, s16, s24, s32, f32 or f64 (float only with pcm); by default the source's depth.", HelpName = "format" };
        Option<int?> level = new("--level") { Description = "FLAC's compression level, from 0 (fastest) to 8 (smallest); 5 by default.", HelpName = "0-8" };
        Option<bool> noVideo = new("--no-video", "-vn") { Description = "Leave out video tracks, keeping only audio (and subtitles) in a Matroska or MP4 output." };
        Command command = new("audio", "Decode audio to WAV, FLAC, Ogg FLAC or Opus, or encode a video file's audio again while copying its video unchanged: resample, remix and change the sample format.");
        options.AddTo(command);
        command.Options.Add(codec);
        command.Options.Add(rate);
        command.Options.Add(channels);
        command.Options.Add(sampleFormat);
        command.Options.Add(level);
        command.Options.Add(bitrate);
        command.Options.Add(noVideo);
        command.SetAction(result =>
        {
            AudioRequest request = new(
                result.GetValue(codec)?.ToLowerInvariant() is { } name
                    ? name is "flac" or "pcm" or "opus" ? name : throw new UsageException($"--codec '{name}' is not flac, pcm or opus; egami-video encodes FLAC, PCM and Opus audio.")
                    : null,
                result.GetValue(rate) is { } hz ? hz is > 0 and <= 768000 ? hz : throw new UsageException($"--rate {hz} is not a sample rate; give a number of Hz from 1 to 768000, such as 48000.") : null,
                result.GetValue(channels) is { } layout ? ParseLayout(layout) : null,
                result.GetValue(sampleFormat) is { } format ? ParseSampleFormat(format) : null,
                result.GetValue(level) is { } l ? l is >= 0 and <= 8 ? l : throw new UsageException($"--level {l} is not 0 to 8.") : null,
                result.GetValue(bitrate) is { } b ? ParseBitrate(b) : null,
                TrackSelection.Parse(result.GetValue(options.Track), result.GetValue(options.NoAudio), result.GetValue(options.NoSubtitles), result.GetValue(noVideo)),
                result.GetValue(options.Format));
            if (result.GetValue(options.NoAudio))
            {
                throw new UsageException("--no-audio leaves nothing for audio to do; use remux to drop audio tracks.");
            }

            BatchOptions batch = options.Batch(session, result);
            List<InputFile> inputs = options.ExpandInputs(session, result);
            OutputTemplate output = options.OutputTemplate(session, result, inputs, "out/{name}.flac");
            string? container = result.GetValue(options.Format);
            return Batch.Run(session, inputs, batch, input =>
            {
                MediaTarget target = MediaIO.Resolve(session, input, output, container, "flac");
                Run(session, input, target, request, inputs.Count);
                return [target.Path];
            });
        });
        return command;
    }

    /// <summary>What to write: the codec, rate, layout, sample format, level and bitrate, the tracks, and the <c>--format</c> given.</summary>
    internal sealed record AudioRequest(string? Codec, int? Rate, ChannelLayout? Layout, (SampleFormat Format, int Bits, bool Float)? SampleFormat, int? Level, int? Bitrate, TrackSelection Tracks, string? Format = null);

    /// <summary>Parses <c>--bitrate</c>: bits a second, such as 96000, 96k or 0.5M, within what Opus codes.</summary>
    internal static int ParseBitrate(string text)
    {
        long bits = EncodeCommand.ParseBitrate(text);
        return bits is >= 500 and <= 2_400_000 ? (int)bits : throw new UsageException($"--bitrate {text} is not one Opus codes; give at most 2400k bits a second, such as 96k.");
    }

    /// <summary>Parses a sample format name: the frame format to convert to, the bits stored, and whether it is float.</summary>
    internal static (SampleFormat Format, int Bits, bool Float) ParseSampleFormat(string text) => text.ToLowerInvariant() switch
    {
        "u8" => (SampleFormat.U8, 8, false),
        "s16" => (SampleFormat.S16, 16, false),
        "s24" => (SampleFormat.S32, 24, false),
        "s32" => (SampleFormat.S32, 32, false),
        "f32" => (SampleFormat.F32, 32, true),
        "f64" => (SampleFormat.F64, 64, true),
        _ => throw new UsageException($"--sample-format '{text}' is not {string.Join(", ", SampleFormatNames[..^1])} or {SampleFormatNames[^1]}."),
    };

    /// <summary>Parses <c>--channels</c>: a count from 1 to 255, or a layout name.</summary>
    internal static ChannelLayout ParseLayout(string text)
    {
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int count))
        {
            return count is >= 1 and <= ChannelLayout.MaxChannels
                ? ChannelLayout.ForChannelCount(count)
                : throw new UsageException($"--channels {count} is not 1 to {ChannelLayout.MaxChannels}.");
        }

        return LayoutNames.TryGetValue(text, out ChannelLayout layout)
            ? layout
            : throw new UsageException($"--channels '{text}' is not a channel count or a layout: {string.Join(", ", LayoutNames.Keys)}.");
    }

    /// <summary>Writes one input's audio to <paramref name="target"/>.</summary>
    internal static void Run(VideoSession session, InputFile input, MediaTarget target, AudioRequest request, int count)
    {
        ContainerFormat container = target.Container;
        bool audioFile = container is WavFormat or FlacFormat || container == OggFormat.Instance;
        bool opusFile = container == MatroskaFormat.WebM || IsOpusFile(target, request.Format);
        string codecName = request.Codec ?? (opusFile ? "opus" : container is WavFormat or AviFormat ? "pcm" : "flac");
        if (container == MatroskaFormat.WebM && codecName != "opus")
        {
            throw new UsageException($"WebM holds Opus and Vorbis audio, not {codecName.ToUpperInvariant()}; drop --codec {codecName} or write Matroska (.mkv or .mka).");
        }

        if (codecName == "opus" && container is WavFormat or FlacFormat)
        {
            throw new UsageException($"{container.Name} cannot hold Opus; write .opus, .webm, .mka or .mp4.");
        }

        if (codecName == "pcm" && container is FlacFormat)
        {
            throw new UsageException($"{container.Name} holds FLAC, not PCM; drop --codec pcm or write .wav.");
        }

        if (codecName == "pcm" && container == OggFormat.Instance)
        {
            throw new UsageException($"{container.Name} holds FLAC and Opus, not PCM; give --codec flac or opus, or write .wav.");
        }

        if (codecName == "flac" && container is WavFormat)
        {
            throw new UsageException($"{container.Name} holds PCM, not FLAC; drop --codec flac or write .flac.");
        }

        if (codecName == "flac" && request.SampleFormat is { Float: true })
        {
            throw new UsageException("FLAC stores integer samples; give --sample-format s16, s24 or s32, or --codec pcm for float.");
        }

        if (request.Level is not null && codecName == "pcm")
        {
            throw new UsageException("--level sets FLAC's compression; PCM has none.");
        }

        if (request.Level is not null && codecName == "opus")
        {
            throw new UsageException("--level sets FLAC's compression; give Opus a --bitrate instead.");
        }

        if (request.SampleFormat is not null && codecName == "opus")
        {
            throw new UsageException("Opus codes its own samples; drop --sample-format.");
        }

        if (request.Bitrate is not null && codecName != "opus")
        {
            throw new UsageException($"--bitrate sets Opus's bitrate; {codecName.ToUpperInvariant()} is lossless. Give --codec opus.");
        }

        if (audioFile)
        {
            WriteAudioFile(session, input, target, request, codecName, count);
        }
        else
        {
            Transcode(session, input, target, request, codecName, count);
        }
    }

    /// <summary>Whether the output is a .opus file, by its extension or by <c>--format opus</c>: Ogg that gets Opus by default.</summary>
    private static bool IsOpusFile(MediaTarget target, string? format) =>
        target.IsStdout
            ? string.Equals(format?.TrimStart('.'), "opus", StringComparison.OrdinalIgnoreCase)
            : string.Equals(Path.GetExtension(target.Path), ".opus", StringComparison.OrdinalIgnoreCase);

    /// <summary>The codec to encode with: FLAC, Opus, or integer or float PCM.</summary>
    private static AudioCodec Codec(string name, AudioRequest request, AudioTrackInfo source)
    {
        if (name == "flac")
        {
            return FlacCodec.Instance;
        }

        if (name == "opus")
        {
            return OpusCodec.Instance;
        }

        bool asFloat = request.SampleFormat is { } format ? format.Float : source.CodecId == CodecIds.PcmFloat;
        return asFloat ? PcmCodec.FloatingPoint : PcmCodec.Instance;
    }

    /// <summary>The bits per sample to store: the option's, else the source's depth for lossless sources, else 24 for FLAC and 16 for PCM.</summary>
    private static int Bits(AudioCodec codec, AudioRequest request, AudioTrackInfo source)
    {
        if (request.SampleFormat is { } format)
        {
            return format.Bits;
        }

        bool lossless = source.CodecId is CodecIds.Flac or CodecIds.PcmLittleEndian or CodecIds.PcmBigEndian;
        if (codec.CodecId == CodecIds.Opus)
        {
            return 16;
        }

        if (codec.CodecId == CodecIds.PcmFloat)
        {
            return source.CodecId == CodecIds.PcmFloat && source.BitsPerSample == 64 ? 64 : 32;
        }

        if (lossless && source.BitsPerSample is > 0 and <= 32)
        {
            int bits = source.BitsPerSample;
            return codec.CodecId == CodecIds.Flac ? Math.Max(bits, 4) : bits <= 8 ? 8 : bits <= 16 ? 16 : bits <= 24 ? 24 : 32;
        }

        return codec.CodecId == CodecIds.Flac ? 24 : 16;
    }

    /// <summary>The Opus settings: the bitrate, if given.</summary>
    private static OpusEncoderOptions? OpusOptions(string codecName, AudioRequest request) =>
        codecName == "opus" ? new OpusEncoderOptions { Bitrate = request.Bitrate } : null;

    /// <summary>The rate a track decodes at: 48 kHz for Opus, whose header names the original recording's rate.</summary>
    private static int DecodedRate(AudioTrackInfo track) => track.CodecId == CodecIds.Opus ? 48000 : track.SampleRate;

    private static ChannelLayout LayoutOf(AudioTrackInfo track) =>
        track.Layout is { ChannelCount: > 0 } layout && layout.ChannelCount == track.Channels ? layout : ChannelLayout.ForChannelCount(track.Channels);

    private static IMuxer CreateMuxer(VideoSession session, Stream stream, ContainerFormat container, IReadOnlyList<TrackInfo> tracks, MediaMetadata metadata, string codec)
    {
        try
        {
            return MediaIO.CreateMuxer(session, stream, container, tracks, metadata);
        }
        catch (NotSupportedException error)
        {
            throw Unsupported(container, error, codec);
        }
    }

    private static UsageException Unsupported(ContainerFormat container, NotSupportedException error, string codec) =>
        new(codec == "opus"
            ? $"{container.Name} cannot hold this ({error.Message.TrimEnd('.')}); try another container."
            : $"{container.Name} cannot hold this audio ({error.Message.TrimEnd('.')}); try --codec {(codec == "flac" ? "pcm" : "flac")}, another --sample-format or another container.");

    /// <summary>Decodes one audio track to an audio file (WAV, Wave64, FLAC or Ogg), through the reader's trimming and conversion.</summary>
    private static void WriteAudioFile(VideoSession session, InputFile input, MediaTarget target, AudioRequest request, string codecName, int count)
    {
        byte[]? stdin = null;
        if (input.IsStdin)
        {
            using Stream pipe = session.Console.In();
            using MemoryStream buffer = new();
            pipe.CopyTo(buffer);
            stdin = buffer.ToArray();
        }

        Stream Open() => stdin is not null ? new MemoryStream(stdin, writable: false) : MediaIO.OpenStream(session, input);
        AudioTrackInfo track;
        using (IDemuxer probe = MediaFile.OpenDemuxer(Open(), session.Context))
        {
            track = (AudioTrackInfo)request.Tracks.SelectOne(probe.Tracks, TrackKind.Audio, input.Display);
            if (request.Tracks.Select(probe.Tracks, input.Display).Count(t => t.Kind == TrackKind.Audio) > 1)
            {
                session.Warn($"{input.Display}: {target.Container.Name} holds one audio track; writing track {track.Number}. Pick another with --track a:N.");
            }
        }

        if (session.Context.FindAudioCodec(track) is null)
        {
            throw new UsageException($"{input.Display}: egami-video cannot decode {CodecNames.Name(track.CodecId)} audio.");
        }

        AudioReaderOptions readerOptions = new()
        {
            TrackNumber = track.Number,
            SampleFormat = null,
            SampleRate = request.Rate,
            Layout = request.Layout,
        };
        using AudioReader reader = AudioReader.Open(Open(), readerOptions, session.Context);
        AudioWriterOptions writerOptions = new()
        {
            Codec = Codec(codecName, request, track),
            BitsPerSample = Bits(Codec(codecName, request, track), request, track),
            Level = request.Level,
            Opus = OpusOptions(codecName, request),
            Metadata = reader.Info.Metadata,
        };
        MediaIO.Write(session, target.Path, output =>
        {
            CountingStream counted = new(output);
            JobProgress progress = new(session.ProgressSink?.For(input, input.Index, count), reader.Info.Duration, [], session.Cancellation, counted);
            using AudioWriter writer = AudioWriter.Create(counted, target.Container.Name, writerOptions, session.Context, leaveOpen: true);
            try
            {
                while (reader.TryRead(out AudioFrame? frame))
                {
                    using (frame)
                    {
                        progress.Packet(track.Number, frame.Timestamp);
                        writer.Write(frame);
                    }
                }

                writer.Finish();
            }
            catch (NotSupportedException error)
            {
                throw Unsupported(target.Container, error, codecName);
            }

            progress.Finish();
        });
    }

    /// <summary>Copies an input's video and other tracks unchanged and encodes its audio tracks again.</summary>
    private static void Transcode(VideoSession session, InputFile input, MediaTarget target, AudioRequest request, string codecName, int count)
    {
        using IDemuxer demuxer = MediaIO.OpenDemuxer(session, input);
        TrackInfo[] kept = [.. request.Tracks.Select(demuxer.Tracks, input.Display)];
        if (!kept.Any(t => t.Kind == TrackKind.Audio))
        {
            throw new UsageException($"{input.Display}: no audio track{(request.Tracks.IsExplicit ? " among those --track picks" : string.Empty)}; it has {TrackSelection.Describe(demuxer.Tracks)}.");
        }

        // Without a rate, layout or sample format to change, Egami.Video's own transcode job does it all. Standard input is read
        // once, so it goes on through the loop below, as does Opus, which the job would encode at the rate of the original
        // recording that OpusHead names rather than the 48 kHz Opus decodes to, and encoding to Opus, whose bitrate the job does
        // not take.
        if (request.Rate is null && request.Layout is null && request.SampleFormat is null && !input.IsStdin && codecName != "opus" && !kept.Any(t => t.CodecId == CodecIds.Opus))
        {
            HashSet<int> numbers = [.. kept.Select(t => t.Number)];
            foreach (AudioTrackInfo track in kept.OfType<AudioTrackInfo>().Where(t => session.Context.FindAudioCodec(t) is null))
            {
                session.Warn($"{input.Display}: egami-video cannot decode track {track.Number} ({CodecNames.Name(track.CodecId)}); it is copied as it is.");
            }

            AudioCodec audio = codecName == "flac" ? FlacCodec.Instance : PcmCodec.Instance;
            IProgress<MediaProgress>? report = session.ProgressSink?.For(input, input.Index, count);
            MediaIO.Write(session, target.Path, output =>
            {
                using Stream source = MediaIO.OpenStream(session, input);
                try
                {
                    MediaFile.Transcode(source, output, target.Container.Name, audio, request.Level, t => numbers.Contains(t.Number), session.Context, leaveOpen: true, report, session.Cancellation);
                }
                catch (NotSupportedException error)
                {
                    throw Unsupported(target.Container, error, codecName);
                }
            });
            return;
        }

        Dictionary<int, AudioRecoder> recoders = [];
        try
        {
            for (int i = 0; i < kept.Length; i++)
            {
                if (kept[i] is not AudioTrackInfo track)
                {
                    continue;
                }

                if (session.Context.FindAudioCodec(track) is not { } decoderCodec)
                {
                    session.Warn($"{input.Display}: egami-video cannot decode track {track.Number} ({CodecNames.Name(track.CodecId)}); it is copied as it is.");
                    continue;
                }

                AudioCodec codec = Codec(codecName, request, track);
                AudioRecoder recoder = new(track, decoderCodec, codec, request, Bits(codec, request, track), OpusOptions(codecName, request), session.Context);
                recoders[track.Number] = recoder;
                kept[i] = recoder.Encoder.Track with { Number = track.Number, Language = track.Language, Name = track.Name, IsDefault = track.IsDefault, Tags = track.Tags };
            }

            HashSet<int> numbers = [.. kept.Select(t => t.Number)];
            MediaIO.Write(session, target.Path, output =>
            {
                CountingStream counted = new(output);
                using IMuxer muxer = CreateMuxer(session, counted, target.Container, kept, demuxer.Info.Metadata, codecName);
                JobProgress progress = new(session.ProgressSink?.For(input, input.Index, count), demuxer.Info.Duration, kept, session.Cancellation, counted);
                while (demuxer.ReadPacket() is { } packet)
                {
                    using (packet)
                    {
                        if (!numbers.Contains(packet.TrackNumber))
                        {
                            continue;
                        }

                        progress.Packet(packet.TrackNumber, packet.Timestamp);
                        if (recoders.TryGetValue(packet.TrackNumber, out AudioRecoder? recoder))
                        {
                            recoder.Send(packet, muxer.WritePacket);
                        }
                        else
                        {
                            muxer.WritePacket(packet);
                        }
                    }
                }

                foreach (AudioRecoder recoder in recoders.Values)
                {
                    session.Cancellation.ThrowIfCancellationRequested();
                    recoder.Finish(muxer.WritePacket);
                }

                muxer.Finish();
                progress.Finish();
            });
        }
        finally
        {
            foreach (AudioRecoder recoder in recoders.Values)
            {
                recoder.Dispose();
            }
        }
    }

    /// <summary>
    /// Decodes one audio track's packets and encodes them again: drops the codec's priming samples (before time zero), resamples
    /// with <see cref="Resampler"/> and mixes with <see cref="ChannelMixer"/>'s default matrix when asked.
    /// </summary>
    private sealed class AudioRecoder : IDisposable
    {
        private readonly IAudioDecoder _decoder;
        private readonly MediaContext _context;
        private readonly ChannelLayout _layout;
        private readonly bool _remix;
        private readonly int _rate;
        private Resampler? _resampler;

        public AudioRecoder(AudioTrackInfo track, AudioCodec decoder, AudioCodec encoder, AudioRequest request, int bits, OpusEncoderOptions? opus, MediaContext context)
        {
            Number = track.Number;
            _context = context;
            _rate = request.Rate ?? DecodedRate(track);
            _layout = request.Layout ?? LayoutOf(track);
            _remix = request.Layout is not null;
            _decoder = decoder.CreateDecoder(track, new AudioDecoderOptions { Errors = DecodeErrorMode.Conceal }, context);
            try
            {
                Encoder = encoder.CreateEncoder(new AudioEncoderOptions { SampleRate = _rate, Layout = _layout, BitsPerSample = bits, Level = request.Level, Opus = opus }, context);
            }
            catch
            {
                _decoder.Dispose();
                throw;
            }
        }

        public int Number { get; }

        public IAudioEncoder Encoder { get; }

        public void Send(EncodedPacket packet, Action<EncodedPacket> write)
        {
            _decoder.Send(packet);
            Drain(write);
        }

        public void Finish(Action<EncodedPacket> write)
        {
            _decoder.Flush();
            Drain(write);
            if (_resampler is not null)
            {
                _resampler.Flush();
                while (_resampler.TryReceive(out AudioFrame? resampled))
                {
                    using (resampled)
                    {
                        Encode(resampled, write);
                    }
                }
            }

            Encoder.Flush();
            Write(write);
        }

        public void Dispose()
        {
            _decoder.Dispose();
            Encoder.Dispose();
            _resampler?.Dispose();
        }

        private void Drain(Action<EncodedPacket> write)
        {
            while (_decoder.TryReceive(out AudioFrame? decoded))
            {
                using (decoded)
                {
                    AudioFrame? frame = Trim(decoded);
                    if (frame is null)
                    {
                        continue;
                    }

                    try
                    {
                        Convert(frame, write);
                    }
                    finally
                    {
                        if (!ReferenceEquals(frame, decoded))
                        {
                            frame.Dispose();
                        }
                    }
                }
            }
        }

        /// <summary>Drops the samples before time zero, the codec's priming delay.</summary>
        private AudioFrame? Trim(AudioFrame frame)
        {
            if (frame.Timestamp >= MediaTime.Zero)
            {
                return frame;
            }

            long skip = -frame.Timestamp.Rescale(new Rational(1, frame.SampleRate)).Ticks;
            return skip >= frame.SampleCount ? null : frame.Slice((int)skip, frame.SampleCount - (int)skip, _context);
        }

        private void Convert(AudioFrame frame, Action<EncodedPacket> write)
        {
            // The rate the encoder takes, which for Opus is not always the 48 kHz its track runs at.
            int rate = _rate;
            if (frame.SampleRate != rate)
            {
                _resampler ??= new Resampler(frame.SampleRate, rate, frame.Layout, ResamplerQuality.Good, _layout, SampleFormat.F32Planar, _context);
                _resampler.Send(frame);
                while (_resampler.TryReceive(out AudioFrame? resampled))
                {
                    using (resampled)
                    {
                        Encode(resampled, write);
                    }
                }
            }
            else if (frame.Layout != _layout && (_remix || frame.ChannelCount != _layout.ChannelCount))
            {
                using AudioFrame mixed = frame.Convert(SampleFormat.F32Planar, _layout, null, _context);
                Encode(mixed, write);
            }
            else
            {
                Encode(frame, write);
            }
        }

        private void Encode(AudioFrame frame, Action<EncodedPacket> write)
        {
            Encoder.Send(frame);
            Write(write);
        }

        private void Write(Action<EncodedPacket> write)
        {
            while (Encoder.TryReceive(out EncodedPacket? packet))
            {
                using (packet)
                {
                    packet.TrackNumber = Number;
                    write(packet);
                }
            }
        }
    }
}
