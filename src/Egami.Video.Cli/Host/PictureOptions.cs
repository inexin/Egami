using System.CommandLine;
using System.Globalization;

using Egami.Cli.Pipeline;
using Egami.Formats;

namespace Egami.Video.Cli.Host;

/// <summary>
/// The options of the commands that write pictures from a video (thumbnail, scenes, sheet, animate): inputs, an image output,
/// the image format and writer settings, the video track and the batch options.
/// </summary>
internal sealed class PictureOptions
{
    private readonly string _example;

    /// <summary>Creates the options; <paramref name="example"/> is an output template for messages, such as <c>thumbs/{name}.jpg</c>.</summary>
    public PictureOptions(string example)
    {
        _example = example;
    }

    /// <summary>Gets the inputs and batch options, shared with the other commands.</summary>
    public MediaOptions Media { get; } = new();

    public Option<string?> Output { get; } = new("--output", "-o")
    {
        Description = "Where to write: a file, a folder, a template with {name}, {ext}, {dir} and {index}, or - for standard output.",
        HelpName = "path",
    };

    public Option<string?> Format { get; } = new("--format", "-f")
    {
        Description = "The image format by name or extension (png, jpg, webp, gif …), when -o's extension does not say.",
        HelpName = "format",
    };

    public Option<string?> Track { get; } = new("--track", "-t")
    {
        Description = "Which video track: v:1 for the second, or a track number as info shows it; the first video track by default.",
        HelpName = "track",
    };

    public Option<int?> Quality { get; } = new("--quality") { Description = "Lossy quality from 0 to 100 for formats that have one.", HelpName = "0-100" };

    public Option<string[]> Set { get; } = new("--set") { Description = "Writer settings as fmt.key=value, as egami takes them.", HelpName = "fmt.key=value" };

    /// <summary>Adds the options to a command.</summary>
    public void AddTo(Command command)
    {
        command.Arguments.Add(Media.Inputs);
        command.Options.Add(Output);
        command.Options.Add(Format);
        command.Options.Add(Track);
        command.Options.Add(Quality);
        command.Options.Add(Set);
        command.Options.Add(Media.Recursive);
        command.Options.Add(Media.Jobs);
        command.Options.Add(Media.InPlace);
        command.Options.Add(Media.FailFast);
    }

    /// <summary>The video track <c>--track</c> picks.</summary>
    public TrackSelection Tracks(ParseResult result) => TrackSelection.Parse(result.GetValue(Track) is { } track ? [track] : null, false, false);

    /// <summary>The output template, checked against the number of inputs; <paramref name="part"/> names the part placeholder.</summary>
    public OutputTemplate OutputTemplate(VideoSession session, ParseResult result, IReadOnlyList<InputFile> inputs, string part = "frame")
    {
        string text = result.GetValue(Output)
            ?? throw new UsageException($"No output: give -o with a file, a folder or a template such as {_example}.");
        return MediaOptions.CheckTemplate(session, text, inputs, _example, part);
    }

    /// <summary>The writer settings the options ask for.</summary>
    public SaveSpec Save(ParseResult result, IReadOnlyDictionary<string, string>? more = null)
    {
        Dictionary<string, string> common = new(StringComparer.Ordinal);
        if (result.GetValue(Quality) is { } quality)
        {
            common["quality"] = quality.ToString(CultureInfo.InvariantCulture);
        }

        foreach ((string key, string value) in more ?? new Dictionary<string, string>())
        {
            common[key] = value;
        }

        return new SaveSpec(null, result.GetValue(Format), Egami.Cli.Host.ImageOptions.SetValues(result.GetValue(Set)), common);
    }
}

/// <summary>An image a picture command writes: its full path (or <c>-</c>) and its format.</summary>
/// <param name="Path">The full path, or <c>-</c> for standard output.</param>
/// <param name="Format">The image format to write.</param>
internal sealed record PictureTarget(string Path, FormatInfo Format)
{
    /// <summary>
    /// Where one input's picture goes: <paramref name="template"/> resolved for the input and part (a folder takes the input's
    /// name and <paramref name="defaultExtension"/>), the format from <c>--format</c> or the extension; standard output takes
    /// <c>--format</c> or the default format.
    /// </summary>
    public static PictureTarget Resolve(VideoSession session, InputFile input, OutputTemplate template, string? format, string defaultExtension, int part = 0)
    {
        FormatInfo? found = format is null ? null : Find(session, format);
        string path;
        if (template.IsStdout)
        {
            path = "-";
            found ??= Find(session, defaultExtension);
        }
        else
        {
            string text = template.Text;
            bool folder = text.EndsWith('/') || text.EndsWith('\\') || (!template.HasPlaceholders && Directory.Exists(session.ResolvePath(text)));
            string extension = found is { Extensions.Count: > 0 } ? found.Extensions[0] : defaultExtension;
            path = folder
                ? System.IO.Path.Combine(session.ResolvePath(text), $"{input.Name}{(part > 0 || template.NamesFrames ? $"-{part:000}" : string.Empty)}.{extension}")
                : session.ResolvePath(template.Resolve(input, extension, part));
            if (found is null)
            {
                string ext = System.IO.Path.GetExtension(path);
                found = ext.Length > 1 ? Find(session, ext) : throw new UsageException($"The output '{path}' has no extension; give --format.");
            }
        }

        if (!session.InPlace && input.Path is { } source && Batch.SamePath(path, source))
        {
            throw new UsageException($"{input.Display}: the output would replace the input; give another -o, or --in-place to replace it.");
        }

        return new PictureTarget(path, found);
    }

    /// <summary>Writes an image with the writer settings of <paramref name="save"/>, through a temporary file.</summary>
    public void Write(VideoSession session, Raster image, SaveSpec save)
    {
        WriterOptions options = Runner.Options(session.Images, save, Format);
        Egami.Cli.Hosting.Output.Write(session, Path, stream => image.Save(stream, Format, options));
    }

    private static FormatInfo Find(VideoSession session, string name)
    {
        FormatInfo found = Runner.FindFormat(session.Images, name)
            ?? throw new UsageException($"'{name}' is not an image format egami-video knows; 'egami formats' lists them.");
        return found.Writer is not null ? found : throw new UsageException($"egami-video can read {found.Name} but not write it.");
    }
}
