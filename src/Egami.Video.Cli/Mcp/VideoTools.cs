using Egami.Cli.Hosting.Mcp;
using Egami.Pixels;
using Egami.Video.Cli.Host;

using ModelContextProtocol.Protocol;

namespace Egami.Video.Cli.Mcp;

/// <summary>
/// What <c>egami-video mcp</c> serves: the shared host's server over egami-video's command tree, with its read-only commands,
/// needed arguments, checks and a picture of the first file written (an image, or a video's first frame).
/// </summary>
internal static class VideoTools
{
    private static readonly Lazy<McpCatalog> All = new(() => McpCatalog.Build(Set));

    private static readonly Lazy<McpToolSet> SetValue = new(Create);

    /// <summary>Gets egami-video's tool set.</summary>
    public static McpToolSet Set => SetValue.Value;

    /// <summary>Gets every tool.</summary>
    public static McpCatalog Catalog => All.Value;

    private static McpToolSet Create()
    {
        return new McpToolSet
        {
            Name = "egami-video",
            Title = "Egami.Video",
            WebsiteUrl = "https://inexin.github.io/Egami/",
            Instructions = "egami-video inspects, remuxes, cuts, joins and converts video and audio files on this computer with Egami.Video, a fully managed .NET library; it also exports frames, makes thumbnails, contact sheets and animations, and checks clips. "
                + "Every tool is an egami-video command; paths are files, globs or folders inside the folders the user allowed, and outputs never replace inputs unless in_place is true. "
                + "Start with info, which answers in JSON; formats lists the containers and codecs, hwprobe the GPU video engines. Times are seconds, [HH:]MM:SS[.mmm] or #frame; tracks are v, a, s, a:1, s:eng or a track number. "
                + "Video jobs can take a while: they send progress notifications and stop when the call is cancelled, leaving no partial file. "
                + "Every result is an object: exitCode (0 success, 1 a file failed or compare found a difference, 2 a usage error, 3 a safety limit), written (the files written), output (the command's JSON or text) and messages (warnings and errors).",
            CreateRoot = () => VideoCli.CreateRoot(new VideoSession(new CliConsole(() => Stream.Null, () => Stream.Null, TextWriter.Null, TextWriter.Null), ".", _ => null)),
            Run = (args, console, workingDirectory, environment, setup) => VideoCli.Run(args, console, workingDirectory, environment, setup),
            Failure = VideoSession.FailureOf,
            Excluded = new HashSet<string>(McpToolSet.DefaultExcluded, StringComparer.Ordinal) { "--progress", "--progress-format" },
            NotTools = new HashSet<string>(StringComparer.Ordinal) { "mcp", "ffmpeg", "ffprobe" },
            ReadOnly = new HashSet<string>(StringComparer.Ordinal) { "info", "formats", "hwprobe", "hash", "compare", "verify", "scenes", "analyze" },
            Needed = new HashSet<(string, string)> { ("compare", "reference") },
            Checks = new HashSet<string>(StringComparer.Ordinal) { "compare", "verify" },
            Preview = Preview,
            PreviewDescription = "Also return the first file written as a picture (a video's first frame), fit to 1024 pixels.",
            Resources = Resources(),
            Prompts = Prompts(),
        };
    }

    /// <summary>The documents egami-video mcp serves: the schemas of its JSON, the formats and the agent guide.</summary>
    private static McpResourceEntry[] Resources()
    {
        System.Reflection.Assembly assembly = typeof(VideoTools).Assembly;
        return
        [
            new("egami-video://schema/info", "info-schema", "egami-video info schema", "The JSON schema of what the info tool returns.", "application/schema+json", McpResourceEntry.Embedded(assembly, "egami-video-info-1.json")),
            new("egami-video://schema/metadata", "metadata-schema", "egami-video metadata schema", "The JSON schema of what the metadata tool writes and its load option reads: tags, chapters, track names and languages.", "application/schema+json", McpResourceEntry.Embedded(assembly, "egami-video-metadata-1.json")),
            new("egami-video://formats", "formats", "Containers and codecs", "The containers and codecs egami-video reads and writes, as egami-video formats --json prints them.", "application/json", McpResourceEntry.Output((args, console, folder, environment, setup) => VideoCli.Run(args, console, folder, environment, setup), "formats", "--json")),
            new("egami-video://guide", "guide", "egami-video guide for agents", "How to call egami-video from scripts and through this server: commands, times and tracks, exit codes and common mistakes.", "text/markdown", McpResourceEntry.Embedded(assembly, "egami-video-guide.md")),
        ];
    }

    /// <summary>The tasks egami-video mcp offers as prompts.</summary>
    private static McpPromptEntry[] Prompts()
    {
        McpPromptArgument file = new("file", "The video file.");
        return
        [
            new(
                "make_clip",
                "Cut a clip",
                "Cut a clip out of a video without encoding it again.",
                [file, new("start", "Where the clip starts: seconds, [HH:]MM:SS[.mmm] or #frame."), new("end", "Where the clip ends, written as start is.")],
                ["info", "trim"],
                a => $"Cut the part of {a["file"]} from {a["start"]} to {a["end"]} into a clip of its own. "
                    + "Call info on the file to check the times fall inside it; then call trim with inputs "
                    + $"[\"{a["file"]}\"], from \"{a["start"]}\", to \"{a["end"]}\" and output \"{{dir}}/{{name}}-clip.{{ext}}\". "
                    + "That copies packets, so the clip starts at the keyframe at or before the start; if the user needs the exact frame, "
                    + "call it again with smart true (VP8, VP9 and AV1) or reencode true."),
            new(
                "make_gif",
                "Make a GIF",
                "Make an animated GIF from a stretch of a video.",
                [file, new("start", "Where the GIF starts: seconds, [HH:]MM:SS[.mmm] or #frame."), new("length", "How long the GIF lasts, in seconds; 3 by default.", Required: false)],
                ["info", "animate"],
                a => $"Make an animated GIF from {a["file"]}, starting at {a["start"]} and lasting {Or(a, "length", "3")} seconds. "
                    + "Call info on the file for its length and size; then call animate with inputs "
                    + $"[\"{a["file"]}\"], from \"{a["start"]}\", to the time {Or(a, "length", "3")} seconds after it, rate 10, size \"480x\" "
                    + "and output \"{dir}/{name}.gif\". A long or large GIF gets big: offer a .webp output for a smaller file."),
            new(
                "web_video",
                "Video for the web",
                "Make an MP4 a browser plays from a video file.",
                [file],
                ["info", "remux", "encode", "audio"],
                a => $"Make an MP4 that browsers play from {a["file"]}. Call info on the file first. "
                    + "If its video is H.264, HEVC or AV1 (egami-video copies H.264 and HEVC but does not encode them), "
                    + $"call remux with inputs [\"{a["file"]}\"] and output \"{{dir}}/{{name}}-web.mp4\", which copies the packets. "
                    + $"Otherwise call encode with inputs [\"{a["file"]}\"], codec \"av1\", audio \"{a["file"]}\" and output \"{{dir}}/{{name}}-web.mp4\". "
                    + "If the audio is not AAC or Opus, then call audio on that MP4 with codec \"aac\", which encodes the sound again and copies the video. "
                    + "Call info on the result to confirm its codecs."),
            new(
                "extract_audio",
                "Extract the audio",
                "Write a video's sound to an audio file.",
                [file, new("format", "The audio file to write, by extension: wav, flac, opus, m4a, aac, aiff or caf; flac by default.", Required: false)],
                ["info", "extract", "audio"],
                a => $"Write the sound of {a["file"]} to a {Or(a, "format", "flac")} file. Call info on the file to see its audio tracks and codec. "
                    + $"If the codec already suits .{Or(a, "format", "flac")}, call extract with inputs [\"{a["file"]}\"], track \"a\" and output "
                    + $"\"{{dir}}/{{name}}.{Or(a, "format", "flac")}\", which copies it without decoding; otherwise call audio with inputs [\"{a["file"]}\"], "
                    + $"no_video true and output \"{{dir}}/{{name}}.{Or(a, "format", "flac")}\"."),
        ];
    }

    private static string Or(IReadOnlyDictionary<string, string> arguments, string name, string fallback)
        => arguments.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;

    /// <summary>The first file written as a PNG fit to 1024 pixels: the image itself, or a media file's first video frame.</summary>
    private static ImageContentBlock Preview(string path)
    {
        const int Fit = 1024;
        Raster image;
        if (MediaFile.Probe(path) is { VideoTrack: not null } && TryFirstFrame(path) is { } frame)
        {
            image = frame;
        }
        else
        {
            image = Raster.Load(path, EgamiContext.Default, new Egami.Formats.ReaderOptions { Frames = ..1 });
        }

        using (image)
        {
            double scale = Math.Min(1, (double)Fit / Math.Max(image.Width, image.Height));
            int width = Math.Max(1, (int)Math.Round(image.Width * scale)), height = Math.Max(1, (int)Math.Round(image.Height * scale));
            Raster small = image.Width == width && image.Height == height ? image : image.Derive(ops => ops.Resize(width, height));
            try
            {
                using MemoryStream png = new();
                small.Save(png, "png");
                return ImageContentBlock.FromBytes(png.ToArray(), "image/png");
            }
            finally
            {
                if (!ReferenceEquals(small, image))
                {
                    small.Dispose();
                }
            }
        }
    }

    private static Raster<Rgba8>? TryFirstFrame(string path)
    {
        using VideoReader reader = VideoReader.Open(path);
        using YuvVideoFrame? frame = reader.ReadFrame();
        return frame?.ToRaster<Rgba8>(Colors.ColorProfile.Srgb);
    }
}
