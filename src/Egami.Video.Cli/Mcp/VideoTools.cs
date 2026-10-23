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
                + "Start with info, which answers in JSON; formats lists the containers and codecs. Times are seconds, [HH:]MM:SS[.mmm] or #frame; tracks are v, a, s, a:1, s:eng or a track number. "
                + "Video jobs can take a while: they send progress notifications and stop when the call is cancelled, leaving no partial file. "
                + "Every result is an object: exitCode (0 success, 1 a file failed or compare found a difference, 2 a usage error, 3 a safety limit), written (the files written), output (the command's JSON or text) and messages (warnings and errors).",
            CreateRoot = () => VideoCli.CreateRoot(new VideoSession(new CliConsole(() => Stream.Null, () => Stream.Null, TextWriter.Null, TextWriter.Null), ".", _ => null)),
            Run = (args, console, workingDirectory, environment, setup) => VideoCli.Run(args, console, workingDirectory, environment, setup),
            Failure = VideoSession.FailureOf,
            Excluded = new HashSet<string>(McpToolSet.DefaultExcluded, StringComparer.Ordinal) { "--progress", "--progress-format" },
            NotTools = new HashSet<string>(StringComparer.Ordinal) { "mcp", "ffmpeg", "ffprobe" },
            ReadOnly = new HashSet<string>(StringComparer.Ordinal) { "info", "formats", "hash", "compare", "scenes", "analyze" },
            Needed = new HashSet<(string, string)> { ("compare", "reference") },
            Checks = new HashSet<string>(StringComparer.Ordinal) { "compare" },
            Preview = Preview,
            PreviewDescription = "Also return the first file written as a picture (a video's first frame), fit to 1024 pixels.",
        };
    }

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
