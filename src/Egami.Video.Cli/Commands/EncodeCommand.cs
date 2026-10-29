using System.CommandLine;
using System.Globalization;

using Egami.Cli.Host;
using Egami.Cli.Pipeline;
using Egami.Colors;
using Egami.Formats;
using Egami.Pixels;
using Egami.Video.Cli.Host;
using Egami.Video.Codecs;

namespace Egami.Video.Cli.Commands;

/// <summary>
/// <c>egami-video encode</c> (Build Plan tasks 10 and 11): makes a clip from images (a list, globs or an image sequence such
/// as <c>shots/{frame:0000}.png</c>) or from a video such as a Y4M stream on standard input, with the encoders Egami.Video
/// has (Motion JPEG, VP8 and uncompressed video), and an audio track copied from another file. Like
/// <c>ffmpeg -framerate 25 -i shots/%04d.png -c:v mjpeg clip.mkv</c>.
/// </summary>
internal static class EncodeCommand
{
    public static Command Create(VideoSession session)
    {
        Argument<string[]> inputs = new("inputs")
        {
            Description = "Images in order (files, or globs sorted by name), an image sequence such as shots/{frame:0000}.png or shots/%04d.png, a video such as a Y4M file, or - for Y4M or Matroska on standard input.",
            Arity = ArgumentArity.OneOrMore,
        };
        Option<string?> output = new("--output", "-o") { Description = "The clip to write, or - for standard output (with --format).", HelpName = "path" };
        Option<string?> format = new("--format", "-f") { Description = "The container by name or extension (mkv, avi, mp4, y4m), when -o's extension does not say; always for -o -.", HelpName = "container" };
        Option<string?> rate = new("--rate") { Description = "Frames per second, such as 25, 29.97 or 30000/1001; 25 for images, and a video's own rate for a video.", HelpName = "fps" };
        Option<string?> codec = new("--codec", "-c") { Description = "The video codec: mjpeg (the default), vp8 or vp9 (for webm, mkv and ivf), av1 (also for mp4 and obu), or raw (uncompressed, the default for y4m).", HelpName = "codec" };
        Option<int?> quality = new("--quality") { Description = "Quality from 0 to 100: 75 by default for Motion JPEG, 60 for VP8, VP9 and AV1.", HelpName = "0-100" };
        Option<int?> effort = new("--effort") { Description = "VP8, VP9 and AV1: how hard to work, 0 (fastest) to 10 (smallest); 4 by default.", HelpName = "0-10" };
        Option<string?> bitrate = new("--bitrate", "-b") { Description = "VP8, VP9 and AV1: aim at this average bitrate instead of a quality, such as 2000k or 1.5M.", HelpName = "bits/s" };
        Option<string?> size = new("--size") { Description = "Fit every frame into this size, keeping its shape, with black around it; images take the first image's size by default.", HelpName = "WxH" };
        Option<string?> chroma = new("--chroma") { Description = "Chroma subsampling for frames made from images: 420 (the default), 422 or 444.", HelpName = "420|422|444" };
        Option<string?> audio = new("--audio") { Description = "Add the first audio track of this file, copied without decoding, up to the end of the video.", HelpName = "file" };
        Command command = new("encode", "Make a clip from images, an image sequence or a Y4M stream, with Motion JPEG, VP8 or uncompressed video, and audio from another file.");
        command.Arguments.Add(inputs);
        command.Options.Add(output);
        command.Options.Add(format);
        command.Options.Add(rate);
        command.Options.Add(codec);
        command.Options.Add(quality);
        command.Options.Add(effort);
        command.Options.Add(bitrate);
        command.Options.Add(size);
        command.Options.Add(chroma);
        command.Options.Add(audio);
        command.SetAction(result =>
        {
            string text = result.GetValue(output)
                ?? throw new UsageException("No output: give -o with the clip to write, such as -o clip.mkv, or - with --format for standard output.");
            Settings settings = new()
            {
                Rate = result.GetValue(rate) is { } r ? ParseRate(r) : null,
                Quality = result.GetValue(quality) is { } q ? (q is >= 0 and <= 100 ? Math.Max(1, q) : throw new UsageException("--quality must be from 0 to 100.")) : null,
                Effort = result.GetValue(effort) is { } e ? (e is >= 0 and <= 10 ? e : throw new UsageException("--effort must be from 0 to 10.")) : null,
                Bitrate = result.GetValue(bitrate) is { } b ? ParseBitrate(b) : null,
                Size = result.GetValue(size) is { } s ? ParseSize(s) : null,
                Chroma = result.GetValue(chroma) is { } c ? ParseChroma(c) : null,
                Audio = result.GetValue(audio),
            };
            Source source = Source.Find(session, result.GetValue(inputs) ?? []);
            InputFile first = source.Display;
            MediaTarget target = MediaIO.Resolve(session, first, OutputTemplate.Parse(text), result.GetValue(format), "mkv");
            if (target.IsStdout && target.Container is AviFormat)
            {
                throw new UsageException("AVI needs a file it can seek in; write mkv or y4m to standard output.");
            }

            settings = settings with { Codec = FindCodec(session, result.GetValue(codec), target.Container) };
            if ((settings.Effort is not null || settings.Bitrate is not null) && settings.Codec != VideoCodecs.Vp8 && settings.Codec != VideoCodecs.Vp9 && settings.Codec != VideoCodecs.Av1)
            {
                throw new UsageException("--effort and --bitrate are for --codec vp8, vp9 and av1.");
            }

            if ((settings.Codec == VideoCodecs.Vp8 || settings.Codec == VideoCodecs.Vp9 || settings.Codec == VideoCodecs.Av1) && settings.Quality is not null)
            {
                settings = settings with { Quality = result.GetValue(quality) };
            }
            if (settings.Audio is not null && target.Container is Y4mFormat or IvfFormat)
            {
                throw new UsageException($"{target.Container.Name} holds video only; write mkv, avi or mp4 for --audio.");
            }

            foreach (string input in source.Paths)
            {
                if (Batch.SamePath(target.Path, input))
                {
                    throw new UsageException($"The output '{text}' is one of the inputs; give another -o.");
                }
            }

            using (source)
            {
                return Batch.Run(session, [first], BatchOptions.Default, _ =>
                {
                    Encode(session, source, target, settings);
                    return [target.Path];
                });
            }
        });
        return command;
    }

    private static void Encode(VideoSession session, Source source, MediaTarget target, Settings settings)
    {
        using IDemuxer? sound = settings.Audio is { } audioPath ? MediaFile.OpenDemuxer(session.ResolvePath(audioPath), session.Context) : null;
        AudioTrackInfo? audioTrack = null;
        if (sound is not null)
        {
            audioTrack = sound.Tracks.OfType<AudioTrackInfo>().FirstOrDefault()
                ?? throw new UsageException($"--audio {settings.Audio} has no audio track; it has {TrackSelection.Describe(sound.Tracks)}.");
        }

        using IEnumerator<VideoFrame> frames = source.Frames(session, settings).GetEnumerator();
        MediaIO.Write(session, target.Path, stream =>
        {
            CountingStream counted = new(stream);
            Rational frameRate = settings.Rate ?? source.Rate ?? new Rational(25, 1);
            VideoWriterOptions options = new()
            {
                Codec = settings.Codec!,
                FrameRate = frameRate,
                Quality = settings.Quality,
                ChromaFormat = settings.Chroma,
                Effort = settings.Effort,
                RateControl = settings.Bitrate is null ? RateControlMode.Quality : RateControlMode.Bitrate,
                Bitrate = settings.Bitrate,
                OtherTracks = audioTrack is null ? [] : [audioTrack with { Number = 1 }],
            };
            MediaTime? duration = source.Count is { } count ? new MediaTime(count, frameRate.Reciprocal()) : source.Duration;
            JobProgress progress = new(session.ProgressSink?.For(source.Display, 0, 1), duration, [], session.Cancellation, counted);
            using VideoWriter writer = VideoWriter.Create(counted, target.Container.Extensions[0].TrimStart('.'), options, session.Context, leaveOpen: true);
            EncodedPacket? waiting = null;
            MediaTime frameDuration = new(1, frameRate.Reciprocal());
            MediaTime end = MediaTime.Zero;
            try
            {
                long index = 0;
                while (frames.MoveNext())
                {
                    using VideoFrame frame = frames.Current;
                    MediaTime time = settings.Rate is null && source.KeepsTimes ? frame.Timestamp : new MediaTime(index, frameRate.Reciprocal());
                    try
                    {
                        if (frame is YuvVideoFrame yuv)
                        {
                            writer.WriteFrame(yuv.Raster, time);
                        }
                        else
                        {
                            writer.WriteFrame(((VideoFrame<Rgba8>)frame).Raster, time);
                        }
                    }
                    catch (NotSupportedException error) when (index == 0)
                    {
                        throw new UsageException($"{target.Container.Name} cannot hold this: {error.Message}");
                    }

                    index++;
                    end = time + frameDuration;
                    progress.Frame(time);
                    if (sound is not null)
                    {
                        waiting = CopyAudio(sound, audioTrack!.Number, writer, waiting, end);
                    }
                }

                if (index == 0)
                {
                    throw new UsageException($"{source.Display.Display}: no frames to encode.");
                }

                writer.Finish();
                progress.Finish();
                session.Info($"{index} frames at {frameRate} fps, {TimeValue.Format(end)}");
            }
            finally
            {
                waiting?.Dispose();
            }
        });
    }

    /// <summary>Copies the audio packets that start before <paramref name="until"/>; returns the first one that does not.</summary>
    private static EncodedPacket? CopyAudio(IDemuxer sound, int track, VideoWriter writer, EncodedPacket? waiting, MediaTime until)
    {
        while ((waiting ?? sound.ReadPacket()) is { } packet)
        {
            waiting = null;
            if (packet.TrackNumber != track)
            {
                packet.Dispose();
                continue;
            }

            if (packet.Timestamp >= until)
            {
                return packet;
            }

            using (packet)
            {
                packet.TrackNumber = 1;
                writer.WritePacket(packet);
            }
        }

        return null;
    }

    internal static VideoCodec FindCodec(VideoSession session, string? name, ContainerFormat container)
    {
        string key = (name ?? (container is Y4mFormat ? "raw" : "mjpeg")).ToLowerInvariant();
        VideoCodec codec = key switch
        {
            "mjpeg" or "mjpg" or "motion-jpeg" or "motionjpeg" => VideoCodecs.MotionJpeg,
            "raw" or "rawvideo" or "uncompressed" => RawVideoCodec.Instance,
            _ => session.Context.Codecs.FirstOrDefault(c => string.Equals(c.Name, key, StringComparison.OrdinalIgnoreCase) || string.Equals(CodecNames.Name(c.CodecId), key, StringComparison.OrdinalIgnoreCase))
                ?? throw new UsageException($"'{name}' is not a codec egami-video knows; it encodes mjpeg, vp8, vp9, av1 and raw."),
        };
        if (!codec.CanEncode)
        {
            throw new UsageException($"egami-video can decode {codec.Name} but not encode it; it encodes mjpeg, vp8, vp9, av1 and raw.");
        }

        return container is Y4mFormat && codec != RawVideoCodec.Instance
            ? throw new UsageException("Y4M holds uncompressed video only; give --codec raw, or write mkv or avi.")
            : codec;
    }

    /// <summary>Parses a bitrate in bits a second: 800000, 2000k or 1.5M.</summary>
    internal static long ParseBitrate(string text)
    {
        string t = text.Trim();
        double scale = t.EndsWith('k') || t.EndsWith('K') ? 1e3 : t.EndsWith('m') || t.EndsWith('M') ? 1e6 : 1;
        string number = scale == 1 ? t : t[..^1];
        return double.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double value) && value * scale >= 1000
            ? (long)Math.Round(value * scale)
            : throw new UsageException($"--bitrate '{text}' is not a bitrate; give bits a second such as 2000k or 1.5M.");
    }

    /// <summary>Parses a frame rate: 25, 29.97 (read as 30000/1001), or 30000/1001.</summary>
    internal static Rational ParseRate(string text, string option = "--rate")
    {
        Rational rate;
        try
        {
            if (text.Contains('/', StringComparison.Ordinal) || !text.Contains('.', StringComparison.Ordinal))
            {
                rate = Rational.Parse(text.Trim());
            }
            else
            {
                double value = double.Parse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
                long ntsc = (long)Math.Round(value * 1.001);
                rate = Math.Abs((ntsc * 1000 / 1001.0) - value) < 0.005 ? new Rational(ntsc * 1000, 1001) : new Rational((long)Math.Round(value * 1000), 1000);
            }
        }
        catch (FormatException)
        {
            throw BadRate(text, option);
        }
        catch (OverflowException)
        {
            throw BadRate(text, option);
        }

        return rate.Numerator > 0 && rate.ToDouble() <= 1000 ? rate : throw BadRate(text, option);

        static UsageException BadRate(string text, string option) => new($"{option} '{text}' is not a frame rate: give frames per second above 0 and at most 1000, such as 25, 29.97 or 30000/1001.");
    }

    internal static Size ParseSize(string text)
    {
        string[] parts = text.ToLowerInvariant().Split('x');
        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int width)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int height)
            && width is > 0 and <= 16384 && height is > 0 and <= 16384
            ? new Size(width, height)
            : throw new UsageException($"--size '{text}' is not a size: give WxH, such as 1280x720.");
    }

    private static ChromaFormat ParseChroma(string text) => text.Trim() switch
    {
        "420" or "4:2:0" => ChromaFormat.Yuv420,
        "422" or "4:2:2" => ChromaFormat.Yuv422,
        "444" or "4:4:4" => ChromaFormat.Yuv444,
        _ => throw new UsageException($"--chroma '{text}' is not a subsampling: give 420, 422 or 444."),
    };

    /// <summary>What the options ask for.</summary>
    private sealed record Settings
    {
        public Rational? Rate { get; init; }

        public int? Quality { get; init; }

        public int? Effort { get; init; }

        public long? Bitrate { get; init; }

        public Size? Size { get; init; }

        public ChromaFormat? Chroma { get; init; }

        public string? Audio { get; init; }

        public VideoCodec? Codec { get; init; }
    }

    /// <summary>Where the frames come from: image files, an image sequence, or a video (a file or standard input).</summary>
    private sealed class Source : IDisposable
    {
        private readonly List<InputFile> _images = [];
        private InputFile? _video;
        private FrameSource? _opened;

        /// <summary>Gets the input that names the job in messages and progress.</summary>
        public InputFile Display { get; private set; } = null!;

        /// <summary>Gets the paths of the input files, so the output never replaces one.</summary>
        public IEnumerable<string> Paths => _images.Select(i => i.Path!).Concat(_video?.Path is { } path ? [path] : []);

        /// <summary>Gets the number of frames when known before reading (images).</summary>
        public long? Count => _video is null ? _images.Count : null;

        /// <summary>Gets the video's frame rate, or <see langword="null"/> for images.</summary>
        public Rational? Rate { get; private set; }

        /// <summary>Gets the video's duration, when its headers say.</summary>
        public MediaTime? Duration { get; private set; }

        /// <summary>Gets a value indicating whether the frames carry their own times (a video).</summary>
        public bool KeepsTimes => _video is not null;

        public static Source Find(VideoSession session, string[] patterns)
        {
            Source source = new();
            if (patterns.Length == 1 && Sequence(patterns[0]) is { } name)
            {
                int start = File.Exists(session.ResolvePath(name(0))) ? 0 : 1;
                for (int i = start; File.Exists(session.ResolvePath(name(i))); i++)
                {
                    string path = session.ResolvePath(name(i));
                    source._images.Add(new InputFile(path, name(i), string.Empty, 0));
                }

                if (source._images.Count == 0)
                {
                    throw new UsageException($"No files for the image sequence '{patterns[0]}': looked for {name(0)} and {name(1)}.");
                }

                source.Display = source._images[0];
                return source;
            }

            bool video = patterns.Length == 1 && (patterns[0] == "-" || (!Globber.HasWildcard(patterns[0]) && IsVideo(session, patterns[0])));
            if (!video && patterns.Any(p => p == "-" || (!Globber.HasWildcard(p) && IsVideo(session, p))))
            {
                throw new UsageException("encode makes a clip from images, or from one video; give either.");
            }

            if (video)
            {
                source._video = Globber.Expand(session, patterns, recursive: false)[0];
                source.Display = source._video;
                return source;
            }

            source._images.AddRange(Globber.Expand(session.Images, patterns, recursive: false));
            source.Display = source._images[0];
            return source;
        }

        /// <summary>The frames, each to be disposed: RGBA for images and resized video, the codec's planes for a video otherwise.</summary>
        public IEnumerable<VideoFrame> Frames(VideoSession session, Settings settings)
        {
            if (_video is { } video)
            {
                _opened = FrameSource.Open(session, video, TrackSelection.All);
                Rate = _opened.Track.FrameRate is { Numerator: > 0 } rate ? rate : null;
                Duration = _opened.Info.Duration;
                return Video(session, _opened, settings);
            }

            return Images(session, settings);
        }

        public void Dispose() => _opened?.Dispose();

        private static IEnumerable<VideoFrame> Video(VideoSession session, FrameSource source, Settings settings)
        {
            bool jpeg = settings.Codec == VideoCodecs.MotionJpeg;
            foreach (YuvVideoFrame frame in source.Frames())
            {
                YuvRaster planes = frame.Raster;

                // Uncompressed video keeps the planes exactly; Motion JPEG takes 8-bit planes without alpha.
                bool convert = (settings.Size is { } size && (size.Width != frame.Width || size.Height != frame.Height))
                    || (settings.Chroma is { } chroma && chroma != planes.ChromaFormat)
                    || (jpeg && (planes.BitDepth > 8 || planes.HasAlpha));
                if (!convert)
                {
                    yield return frame;
                    continue;
                }

                Raster<Rgba8> image;
                MediaTime time = frame.Timestamp;
                using (frame)
                {
                    image = frame.Color?.TransferCharacteristics is 16 or 18 ? frame.ToRaster<Rgba8>(ColorProfile.Srgb) : FrameSource.ToImage(session, frame);
                }

                yield return new VideoFrame<Rgba8>(Fit(image, settings.Size ?? new Size(image.Width, image.Height)), time);
            }
        }

        private IEnumerable<VideoFrame> Images(VideoSession session, Settings settings)
        {
            Size? target = settings.Size;
            for (int i = 0; i < _images.Count; i++)
            {
                session.Cancellation.ThrowIfCancellationRequested();
                Raster<Rgba8> image;
                using (Raster loaded = new InputSource(_images[i]).Load(session.Images, new ReaderOptions { Frames = ..1 }))
                {
                    image = loaded is Raster<Rgba8> rgba ? rgba.Copy() : loaded.As<Rgba8>();
                }

                target ??= new Size(image.Width, image.Height);
                yield return new VideoFrame<Rgba8>(Fit(image, target.Value), MediaTime.Zero);
            }
        }

        /// <summary>The image fitted into <paramref name="size"/>, keeping its shape, with black around it; disposes the image if it makes a new one.</summary>
        private static Raster<Rgba8> Fit(Raster<Rgba8> image, Size size)
        {
            if (image.Width == size.Width && image.Height == size.Height)
            {
                return image;
            }

            using (image)
            {
                return image.Derive(x => x.Fit(size.Width, size.Height).Pad(size.Width, size.Height, new Rgba8(0, 0, 0, 255)));
            }
        }

        private static bool IsVideo(VideoSession session, string path)
        {
            string extension = Path.GetExtension(path).TrimStart('.');
            return extension.Length > 0 && session.Context.FindContainer(extension) is not null && Runner.FindFormat(session.Images, extension) is not { Reader: not null };
        }

        /// <summary>The file name of each frame of an image sequence (<c>{frame}</c> or a printf-style <c>%04d</c>), or <see langword="null"/>.</summary>
        private static Func<int, string>? Sequence(string text)
        {
            if (text.Contains("{frame", StringComparison.OrdinalIgnoreCase))
            {
                OutputTemplate template = OutputTemplate.Parse(text, "frame");
                InputFile none = new(null, text, string.Empty, 0);
                return i => template.Resolve(none, string.Empty, i);
            }

            int percent = text.IndexOf('%', StringComparison.Ordinal);
            if (percent < 0)
            {
                return null;
            }

            int end = percent + 1;
            while (end < text.Length && char.IsAsciiDigit(text[end]))
            {
                end++;
            }

            if (end >= text.Length || text[end] != 'd')
            {
                return null;
            }

            string digits = text[(percent + 1)..end];
            string pad = digits.Length > 0 && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int width) && width is > 0 and < 10 ? new string('0', width) : "0";
            string before = text[..percent], after = text[(end + 1)..];
            return i => before + i.ToString(pad, CultureInfo.InvariantCulture) + after;
        }
    }
}
