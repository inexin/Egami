using System.CommandLine;
using System.Globalization;

using Egami.Cli.Host;
using Egami.Pixels;
using Egami.Video.Analysis;
using Egami.Video.Audio;
using Egami.Video.Audio.Codecs;
using Egami.Video.Cli.Host;
using Egami.Video.Codecs;
using Egami.Video.Containers;

namespace Egami.Video.Cli.Commands;

/// <summary>
/// The <c>generate</c> command (V1.4 Build Plan task 18, Design Q10 and Q11): writes one of the <see cref="TestSources"/> to a
/// file, a picture source with an optional sound (<c>bars --tone</c>) or a sound on its own.
/// </summary>
internal static class GenerateCommand
{
    private static readonly string[] VideoSources = ["pattern", "bars", "solid", "gradient"];
    private static readonly string[] AudioSources = ["sine", "silence", "noise", "sweep"];

    public static Command Create(VideoSession session)
    {
        Argument<string> source = new("source") { Description = $"What to make: {string.Join(", ", VideoSources)} (pictures) or {string.Join(", ", AudioSources)} (sound)." };
        Argument<string?> file = new("file") { Description = "The file to write; or give -o.", Arity = ArgumentArity.ZeroOrOne };
        Option<string?> output = new("--output", "-o") { Description = "The file to write: the container comes from its extension or --format.", HelpName = "path" };
        Option<string?> format = new("--format", "-f") { Description = "The container by name or extension, when the file's extension does not say.", HelpName = "container" };
        Option<string> duration = new("--duration", "-t") { Description = "How long, in seconds or [HH:]MM:SS[.mmm]. Default 10.", HelpName = "time", DefaultValueFactory = _ => "10" };
        Option<string> size = new("--size", "-s") { Description = "The picture size. Default 1280x720.", HelpName = "WxH", DefaultValueFactory = _ => "1280x720" };
        Option<string> rate = new("--rate", "-r") { Description = "Frames per second, such as 25, 29.97 or 30000/1001. Default 30.", HelpName = "fps", DefaultValueFactory = _ => "30" };
        Option<string?> color = new("--color") { Description = "solid's color, and gradient's first, as #rrggbb. Default black for solid, black to white for gradient.", HelpName = "color" };
        Option<string?> toColor = new("--to-color") { Description = "gradient's last color as #rrggbb.", HelpName = "color" };
        Option<bool> vertical = new("--vertical") { Description = "Run gradient from top to bottom instead of left to right." };
        Option<string> barsStyle = new("--bars") { Description = "Which bars: auto (the default; SMPTE RP 219 for HD sizes, EG 1 for SD), hd or sd.", HelpName = "auto|hd|sd", DefaultValueFactory = _ => "auto" };
        Option<bool> tone = new("--tone") { Description = "Add a 1 kHz tone at -18 dBFS to a picture source: the same as --audio sine." };
        Option<string?> audio = new("--audio") { Description = "Add a sound to a picture source: sine, silence, noise or sweep.", HelpName = "source" };
        Option<double> frequency = new("--frequency") { Description = "sine's frequency in hertz. Default 1000.", HelpName = "Hz", DefaultValueFactory = _ => 1000 };
        Option<double> level = new("--level") { Description = "The level of sine (its peak), noise (its RMS) and sweep in dBFS. Default -18.", HelpName = "dB", DefaultValueFactory = _ => -18 };
        Option<string> noise = new("--noise") { Description = "noise's color: white (the default) or pink.", HelpName = "white|pink", DefaultValueFactory = _ => "white" };
        Option<double> from = new("--from") { Description = "sweep's start frequency in hertz. Default 20.", HelpName = "Hz", DefaultValueFactory = _ => 20 };
        Option<double?> to = new("--to") { Description = "sweep's end frequency in hertz. Default just below half the sample rate.", HelpName = "Hz" };
        Option<int> sampleRate = new("--sample-rate", "-ar") { Description = "The sample rate in hertz. Default 48000.", HelpName = "Hz", DefaultValueFactory = _ => 48000 };
        Option<string> channels = new("--channels", "-ac") { Description = "A channel count or a layout (mono, stereo, 5.1 …). Default stereo.", HelpName = "layout", DefaultValueFactory = _ => "stereo" };
        Option<string?> codec = new("--codec", "-c") { Description = "The video codec: mjpeg (the default), vp8, vp9 or raw (the default for y4m).", HelpName = "codec" };
        Option<string?> audioCodec = new("--audio-codec") { Description = "The audio codec: flac, pcm or opus; by default Opus in WebM and .opus, PCM in WAV and AVI, and FLAC otherwise.", HelpName = "codec" };
        Option<int?> quality = new("--quality") { Description = "The video quality from 0 to 100.", HelpName = "0-100" };
        Command command = new("generate", "Write a test clip or sound: moving pattern with frame number and timecode, color bars, a solid color or a gradient, with or without a tone; or a sine, silence, noise or sweep on its own.");
        command.Arguments.Add(source);
        command.Arguments.Add(file);
        foreach (Option option in new Option[] { output, format, duration, size, rate, color, toColor, vertical, barsStyle, tone, audio, frequency, level, noise, from, to, sampleRate, channels, codec, audioCodec, quality })
        {
            command.Options.Add(option);
        }

        command.SetAction(result =>
        {
            string kind = result.GetValue(source)!.ToLowerInvariant();
            bool picture = VideoSources.Contains(kind);
            if (!picture && !AudioSources.Contains(kind))
            {
                throw new UsageException($"'{kind}' is not a test source: give {string.Join(", ", VideoSources.Concat(AudioSources))}.");
            }

            string text = result.GetValue(output) ?? result.GetValue(file)
                ?? throw new UsageException($"No output: give the file to write, such as egami-video generate {kind} {(picture ? "bars.mkv" : "tone.wav")}.");
            if (result.GetValue(output) is not null && result.GetValue(file) is not null)
            {
                throw new UsageException("Give the output once: as the second argument or with -o, not both.");
            }

            TimeValue length = TimeValue.ParseDuration(result.GetValue(duration)!, "--duration", allowFrames: false);
            MediaTime time = MediaTime.FromSeconds(length.Seconds);
            string? sound = picture
                ? result.GetValue(audio)?.ToLowerInvariant() ?? (result.GetValue(tone) ? "sine" : null)
                : kind;
            if (sound is not null && !AudioSources.Contains(sound))
            {
                throw new UsageException($"--audio '{sound}' is not a sound source: give {string.Join(", ", AudioSources)}.");
            }

            if (!picture && (result.GetValue(tone) || result.GetValue(audio) is not null))
            {
                throw new UsageException($"{kind} is a sound already; --tone and --audio add a sound to a picture source.");
            }

            if (result.GetValue(sampleRate) is < 8000 or > 384000)
            {
                throw new UsageException("--sample-rate must be from 8000 to 384000.");
            }

            TestAudioFormat audioFormat = new()
            {
                SampleRate = result.GetValue(sampleRate),
                Layout = AudioCommand.ParseLayout(result.GetValue(channels)!),
                Context = session.Context,
            };
            Func<IEnumerable<AudioFrame>>? sounds = sound is null ? null : () => Sound(sound, audioFormat, time, result.GetValue(frequency), result.GetValue(level),
                result.GetValue(noise)!, result.GetValue(from), result.GetValue(to));
            InputFile name = new(null, kind, session.ResolvePath("."), 0);
            MediaTarget target = MediaIO.Resolve(session, name, OutputTemplate.Parse(text), result.GetValue(format), picture ? "mkv" : "wav");
            if (target.IsStdout)
            {
                throw new UsageException("generate writes a file; give a file name.");
            }

            string audioCodecName = AudioCodecName(result.GetValue(audioCodec), target);
            if (!picture)
            {
                return Batch.Run(session, [name], BatchOptions.Default, _ =>
                {
                    WriteSound(session, target, audioCodecName, audioFormat, sounds!());
                    return [target.Path];
                });
            }

            if (sound is not null && target.Container is Y4mFormat or IvfFormat)
            {
                throw new UsageException($"{target.Container.Name} holds video only; write mkv, webm, avi or mp4 for a sound.");
            }

            Size frameSize = EncodeCommand.ParseSize(result.GetValue(size)!);
            TestVideoFormat videoFormat = new()
            {
                Width = frameSize.Width,
                Height = frameSize.Height,
                FrameRate = EncodeCommand.ParseRate(result.GetValue(rate)!),
                Context = session.Context,
            };
            Rgba8 first = result.GetValue(color) is { } c ? Color(c, "--color") : new Rgba8(0, 0, 0, 255);
            Rgba8 last = result.GetValue(toColor) is { } t ? Color(t, "--to-color") : new Rgba8(255, 255, 255, 255);
            ColorBarsStyle style = result.GetValue(barsStyle)!.ToLowerInvariant() switch
            {
                "auto" => ColorBarsStyle.Auto,
                "hd" => ColorBarsStyle.Hd,
                "sd" => ColorBarsStyle.Sd,
                string other => throw new UsageException($"--bars '{other}' is not auto, hd or sd."),
            };
            IEnumerable<YuvVideoFrame> Frames() => kind switch
            {
                "pattern" => TestSources.Pattern(videoFormat, time),
                "bars" => TestSources.ColorBars(videoFormat, time, style),
                "solid" => TestSources.Solid(videoFormat, time, first),
                _ => TestSources.Gradient(videoFormat, time, first, last, result.GetValue(vertical)),
            };
            VideoCodec videoCodec = EncodeCommand.FindCodec(session, result.GetValue(codec), target.Container);
            int? q = result.GetValue(quality) is { } value ? (value is >= 0 and <= 100 ? Math.Max(1, value) : throw new UsageException("--quality must be from 0 to 100.")) : null;
            return Batch.Run(session, [name], BatchOptions.Default, _ =>
            {
                WriteClip(session, target, videoCodec, q, videoFormat, Frames(), sounds is null ? null : (AudioCodecFor(audioCodecName), audioFormat, sounds()), time);
                return [target.Path];
            });
        });
        return command;
    }

    private static IEnumerable<AudioFrame> Sound(string kind, TestAudioFormat format, MediaTime time, double frequency, double level, string noise, double from, double? to)
    {
        if (!(level <= 0))
        {
            throw new UsageException("--level must be 0 dBFS or less.");
        }

        return kind switch
        {
            "sine" => frequency > 0 && frequency < format.SampleRate / 2.0
                ? TestSources.Sine(format, time, frequency, level)
                : throw new UsageException($"--frequency must be above 0 and below half the sample rate ({format.SampleRate / 2} Hz)."),
            "silence" => TestSources.Silence(format, time),
            "noise" => TestSources.Noise(format, time, noise.ToLowerInvariant() switch
            {
                "white" => NoiseColor.White,
                "pink" => NoiseColor.Pink,
                string other => throw new UsageException($"--noise '{other}' is not white or pink."),
            }, level),
            _ => from > 0 && (to ?? from + 1) > from && (to ?? 0) < format.SampleRate / 2.0
                ? TestSources.Sweep(format, time, from, to, level)
                : throw new UsageException($"--from and --to must rise from above 0 to below half the sample rate ({format.SampleRate / 2} Hz)."),
        };
    }

    private static string AudioCodecName(string? asked, MediaTarget target)
    {
        ContainerFormat container = target.Container;
        bool opus = container == MatroskaFormat.WebM || string.Equals(Path.GetExtension(target.Path), ".opus", StringComparison.OrdinalIgnoreCase);
        string name = asked?.ToLowerInvariant() ?? (opus ? "opus" : container is WavFormat or AviFormat ? "pcm" : "flac");
        return name switch
        {
            "flac" when container is WavFormat => throw new UsageException($"{container.Name} holds PCM, not FLAC; drop --audio-codec flac or write .flac."),
            "pcm" when container is FlacFormat || container == OggFormat.Instance || container == MatroskaFormat.WebM =>
                throw new UsageException($"{container.Name} does not hold PCM; give --audio-codec flac or opus, or write .wav."),
            "opus" when container is WavFormat or FlacFormat => throw new UsageException($"{container.Name} cannot hold Opus; write .opus, .webm, .mka or .mp4."),
            "flac" or "pcm" or "opus" => name,
            _ => throw new UsageException($"--audio-codec '{asked}' is not flac, pcm or opus."),
        };
    }

    private static AudioCodec AudioCodecFor(string name) => name switch
    {
        "flac" => FlacCodec.Instance,
        "opus" => OpusCodec.Instance,
        _ => PcmCodec.Instance,
    };

    private static void WriteSound(VideoSession session, MediaTarget target, string codecName, TestAudioFormat format, IEnumerable<AudioFrame> frames)
    {
        MediaIO.Write(session, target.Path, stream =>
        {
            AudioWriterOptions options = new()
            {
                Codec = AudioCodecFor(codecName),
                SampleRate = format.SampleRate,
                Layout = format.Layout,
                BitsPerSample = codecName == "opus" ? 0 : 24,
            };
            using AudioWriter writer = AudioWriter.Create(stream, target.Container.Name, options, session.Context, leaveOpen: true);
            foreach (AudioFrame frame in frames)
            {
                using (frame)
                {
                    session.Cancellation.ThrowIfCancellationRequested();
                    writer.Write(frame);
                }
            }
        });
    }

    private static void WriteClip(VideoSession session, MediaTarget target, VideoCodec codec, int? quality, TestVideoFormat format, IEnumerable<YuvVideoFrame> frames,
        (AudioCodec Codec, TestAudioFormat Format, IEnumerable<AudioFrame> Frames)? sound, MediaTime duration)
    {
        MediaIO.Write(session, target.Path, stream =>
        {
            VideoWriterOptions options = new()
            {
                Codec = codec,
                FrameRate = format.FrameRate,
                Quality = quality,
                AudioCodec = sound?.Codec,
                AudioSampleRate = sound?.Format.SampleRate ?? 0,
                AudioLayout = sound?.Format.Layout,
                AudioBitsPerSample = sound is { Codec: var c } && c.CodecId != CodecIds.Opus ? 24 : 0,
            };
            JobProgress progress = new(session.ProgressSink?.For(new InputFile(null, "generate", ".", 0), 0, 1), duration, [], session.Cancellation);
            using VideoWriter writer = VideoWriter.Create(stream, target.Container.Extensions[0].TrimStart('.'), options, session.Context, leaveOpen: true);
            using IEnumerator<AudioFrame>? audio = sound?.Frames.GetEnumerator();
            MediaTime written = MediaTime.Zero;
            long index = 0;
            foreach (YuvVideoFrame frame in frames)
            {
                using (frame)
                {
                    MediaTime at = new(index++, format.FrameRate.Reciprocal());
                    progress.Frame(at);
                    writer.WriteFrame(frame.Raster, at);
                }

                // Keep the sound up to a frame ahead of the pictures, as the writer wants them roughly in step.
                MediaTime until = new(index, format.FrameRate.Reciprocal());
                while (audio is not null && written < until && audio.MoveNext())
                {
                    using AudioFrame block = audio.Current;
                    writer.WriteAudio(block);
                    written += block.Duration;
                }
            }

            while (audio is not null && audio.MoveNext())
            {
                using AudioFrame block = audio.Current;
                writer.WriteAudio(block);
            }

            progress.Finish();
        });
    }

    private static Rgba8 Color(string text, string option)
    {
        string hex = text.Trim().TrimStart('#');
        return hex.Length == 6 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value)
            ? new Rgba8((byte)(value >> 16), (byte)(value >> 8), (byte)value, 255)
            : throw new UsageException($"{option} '{text}' is not a color: give #rrggbb, such as #1e28dc.");
    }
}
