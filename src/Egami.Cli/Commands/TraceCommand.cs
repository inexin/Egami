using System.CommandLine;
using System.Text;

using Egami.Cli.Host;
using Egami.Cli.Pipeline;
using Egami.Cli.Steps;
using Egami.Drawing;
using Egami.Drawing.Tracing;
using Egami.Pixels;
using Egami.Svg;

namespace Egami.Cli.Commands;

/// <summary>
/// <c>egami trace</c> (Bitmap Tracing Build Plan task 12): images traced into vector shapes with <see cref="RasterTraceExtensions"/>
/// and written as SVG, from a <see cref="TraceOptions"/> preset with any setting changed, and a PNG preview of the trace on request.
/// </summary>
internal static class TraceCommand
{
    /// <summary>The presets by their command-line names.</summary>
    private static readonly (string Name, TraceOptions Options)[] Presets =
    [
        ("default", TraceOptions.Default),
        ("black-and-white", TraceOptions.BlackAndWhite),
        ("logo", TraceOptions.Logo),
        ("poster", TraceOptions.Poster),
        ("photo", TraceOptions.Photo),
        ("line-art", TraceOptions.LineArt),
        ("sketch", TraceOptions.Sketch),
        ("pixel-art", TraceOptions.PixelArt),
    ];

    public static Command Create(CliSession session)
    {
        ImageOptions options = new();
        Option<string?> output = new("--output", "-o")
        {
            Description = "Where to write the SVG: a file, a folder, a template with {name}, {ext}, {dir} and {index}, or - (the default for one input) for standard output.",
            HelpName = "path",
        };
        Option<string?> preset = new("--preset", "-p")
        {
            Description = $"The starting settings: {string.Join(", ", Presets.Select(p => p.Name))}. default (black and white with a white background) by default; the options below change it.",
            HelpName = "preset",
        };
        Option<string?> mode = new("--mode", "-m") { Description = "How pixels become shapes: black-and-white, grayscale, color, logo, centerline (strokes along the middle of lines) or pixel-art (smooth shapes from sprites and other pixel art).", HelpName = "mode" };
        Option<int?> colors = new("--colors", "-c") { Description = "The most colors (gray levels in grayscale) to reduce the image to, 2 to 256 (grayscale, color, logo).", HelpName = "count" };
        Option<string?> palette = new("--palette") { Description = "Fixed colors instead, CSS colors separated by commas or spaces, such as \"black, #c00, white\" (grayscale, color, logo).", HelpName = "colors" };
        Option<string?> layering = new("--layering") { Description = "stacked (each color layer covers those above it, the default) or abutting (each color only its own area).", HelpName = "stacked|abutting" };
        Option<float?> threshold = new("--threshold", "-t") { Description = "The level that splits ink from paper, 0 (black) to 1 (white); found by Otsu's method by default (black-and-white, centerline).", HelpName = "0-1" };
        Option<float?> fidelity = new("--path-fidelity") { Description = "How closely curves follow the pixels, 0 (fewest curves) to 1 (tightest).", HelpName = "0-1" };
        Option<float?> corners = new("--corners") { Description = "How readily corners stay sharp, 0 (round every corner) to 1 (a polygon).", HelpName = "0-1" };
        Option<int?> noise = new("--noise") { Description = "The smallest region kept, in pixels; smaller ones merge into those around them.", HelpName = "pixels" };
        Option<bool?> ignoreWhite = new("--ignore-white") { Description = "Leave out white: no white background shape, and no white color layer." };
        Option<float?> smoothing = new("--smoothing") { Description = "Edge-keeping smoothing before the colors are found, 0 to 1, for photos (grayscale, color).", HelpName = "0-1" };
        Option<bool?> snapLines = new("--snap-lines") { Description = "Make straight runs lines (grayscale, color, logo; on by default)." };
        Option<bool?> snapArcs = new("--snap-arcs") { Description = "Make runs on a circle arcs, and round outlines circles (grayscale, color, logo; on by default)." };
        Option<float?> strokeWidth = new("--stroke-width") { Description = "One pen width in pixels for every stroke, instead of the width each stroke's ink covers (centerline).", HelpName = "pixels" };
        Option<bool?> exact = new("--exact") { Description = "Keep the pixel squares, one shape per color with straight edges, instead of smoothing them (pixel-art)." };
        Option<int?> precision = new("--precision") { Description = "Decimal places of the SVG's coordinates.", HelpName = "digits" };
        Option<string?> preview = new("--preview-png") { Description = "Also write the trace drawn as a PNG, the image's size: a file, a folder or a template with {name}.", HelpName = "path" };
        Command command = new("trace", "Trace images into vector shapes (logos, line art, scans, posters, photos, pixel art) and write them as SVG.");
        command.Arguments.Add(options.Inputs);
        foreach (Option option in (Option[])[output, preset, mode, colors, palette, layering, threshold, fidelity, corners, noise, ignoreWhite, smoothing, snapLines, snapArcs, strokeWidth, exact, precision, preview])
        {
            command.Options.Add(option);
        }

        command.Options.Add(options.Recursive);
        command.Options.Add(options.Jobs);
        command.Options.Add(options.InPlace);
        command.Options.Add(options.FailFast);
        command.SetAction(result =>
        {
            TraceOptions trace = Options(result, preset, mode, colors, palette, layering, threshold, fidelity, corners, noise, ignoreWhite, smoothing, snapLines, snapArcs, strokeWidth, exact);
            SvgWriteOptions svg = Checked(() => result.GetValue(precision) is { } digits ? new SvgWriteOptions { Precision = digits } : SvgWriteOptions.Default, "--precision");
            List<InputFile> inputs = options.ExpandInputs(session, result);
            OutputTemplate template = Hosting.OutputTemplate.Parse(result.GetValue(output) ?? "-");
            CheckMany(session, template, inputs.Count, "-o", "out/{name}.svg");
            OutputTemplate? previewTemplate = result.GetValue(preview) is { } previewText ? Hosting.OutputTemplate.Parse(previewText) : null;
            if (previewTemplate is not null)
            {
                if (previewTemplate.IsStdout && template.IsStdout)
                {
                    throw new UsageException("The SVG and the preview cannot both go to standard output; give -o or a --preview-png file.");
                }

                CheckMany(session, previewTemplate, inputs.Count, "--preview-png", "previews/{name}.png");
            }

            BatchOptions batch = options.Batch(session, result) with { Jobs = template.IsStdout || previewTemplate?.IsStdout == true ? 1 : result.GetValue(options.Jobs) };
            return Batch.Run(session, inputs, batch, InputSource.Each(input =>
            {
                using Raster image = input.Load(session, new Formats.ReaderOptions { Frames = ..1 });
                TraceResult traced = image.Trace(trace, session.Context);
                List<string> written = [];
                string path = Target(session, template, input, "svg");
                Output.Write(session, path, stream => traced.SaveSvg(stream, svg));
                if (path != "-")
                {
                    written.Add(path);
                }

                if (previewTemplate is not null)
                {
                    string previewPath = Target(session, previewTemplate, input, "png");
                    using Raster<Rgba8> drawn = Raster.Create(traced.Width, traced.Height, Rgba8.Transparent, session.Context);
                    using (RasterCanvas<Rgba8> canvas = drawn.CreateCanvas())
                    {
                        traced.Draw(canvas);
                    }

                    Output.Write(session, previewPath, stream => drawn.Save(stream, Formats.Png.PngFormat.Info));
                    if (previewPath != "-")
                    {
                        written.Add(previewPath);
                    }
                }

                session.Info($"{input.File.Display}: {traced.Shapes.Count} shapes in {traced.Palette.Count} colors");
                return written;
            }));
        });
        return command;
    }

    /// <summary>The preset with the given options changed; a value out of range is a usage error naming its option.</summary>
    private static TraceOptions Options(
        ParseResult result,
        Option<string?> preset,
        Option<string?> mode,
        Option<int?> colors,
        Option<string?> palette,
        Option<string?> layering,
        Option<float?> threshold,
        Option<float?> fidelity,
        Option<float?> corners,
        Option<int?> noise,
        Option<bool?> ignoreWhite,
        Option<float?> smoothing,
        Option<bool?> snapLines,
        Option<bool?> snapArcs,
        Option<float?> strokeWidth,
        Option<bool?> exact)
    {
        TraceOptions trace = TraceOptions.Default;
        if (result.GetValue(preset) is { } name)
        {
            string key = name.Trim().ToLowerInvariant().Replace('_', '-');
            trace = Presets.FirstOrDefault(p => p.Name == key || p.Name.Replace("-", string.Empty, StringComparison.Ordinal) == key).Options
                ?? throw new UsageException($"'{name}' is not a preset; use {string.Join(", ", Presets.Select(p => p.Name))}.");
        }

        if (result.GetValue(mode) is { } m)
        {
            trace = trace with { Mode = ValueParsers.Enum<TraceMode>(m) };
        }

        if (result.GetValue(layering) is { } l)
        {
            trace = trace with { Layering = ValueParsers.Enum<TraceLayering>(l) };
        }

        if (result.GetValue(palette) is { } p)
        {
            Rgba8[] list = [.. SplitColors(p).Select(ValueParsers.ParseColor)];
            trace = Checked(() => trace with { Palette = list }, "--palette");
        }

        trace = Set(trace, result.GetValue(colors), static (t, v) => t with { Colors = v }, "--colors");
        trace = Set(trace, result.GetValue(threshold), static (t, v) => t with { Threshold = v }, "--threshold");
        trace = Set(trace, result.GetValue(fidelity), static (t, v) => t with { PathFidelity = v }, "--path-fidelity");
        trace = Set(trace, result.GetValue(corners), static (t, v) => t with { Corners = v }, "--corners");
        trace = Set(trace, result.GetValue(noise), static (t, v) => t with { Noise = v }, "--noise");
        trace = Set(trace, result.GetValue(ignoreWhite), static (t, v) => t with { IgnoreWhite = v }, "--ignore-white");
        trace = Set(trace, result.GetValue(smoothing), static (t, v) => t with { Smoothing = v }, "--smoothing");
        trace = Set(trace, result.GetValue(snapLines), static (t, v) => t with { SnapToLines = v }, "--snap-lines");
        trace = Set(trace, result.GetValue(snapArcs), static (t, v) => t with { SnapToArcs = v }, "--snap-arcs");
        trace = Set(trace, result.GetValue(strokeWidth), static (t, v) => t with { StrokeWidth = v }, "--stroke-width");
        trace = Set(trace, result.GetValue(exact), static (t, v) => t with { Exact = v }, "--exact");
        return trace;
    }

    private static TraceOptions Set<T>(TraceOptions trace, T? value, Func<TraceOptions, T, TraceOptions> change, string option)
        where T : struct
    {
        return value is { } v ? Checked(() => change(trace, v), option) : trace;
    }

    private static T Checked<T>(Func<T> make, string option)
    {
        try
        {
            return make();
        }
        catch (ArgumentException error)
        {
            string message = error.Message;
            int cut = message.IndexOf(" (Parameter", StringComparison.Ordinal);
            throw new UsageException($"{option}: {(cut > 0 ? message[..cut] : message)}");
        }
    }

    /// <summary>Splits a list of CSS colors at commas and spaces outside parentheses, so rgb(0 0 0) and rgb(0,0,0) stay whole.</summary>
    private static List<string> SplitColors(string text)
    {
        List<string> colors = [];
        StringBuilder current = new();
        int depth = 0;
        foreach (char c in text)
        {
            depth += c == '(' ? 1 : c == ')' ? -1 : 0;
            if (depth == 0 && (c == ',' || char.IsWhiteSpace(c)))
            {
                if (current.Length > 0)
                {
                    colors.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            colors.Add(current.ToString());
        }

        return colors.Count > 0 ? colors : throw new UsageException("--palette needs at least one color.");
    }

    private static void CheckMany(CliSession session, OutputTemplate template, int count, string option, string example)
    {
        if (count <= 1)
        {
            return;
        }

        if (template.IsStdout)
        {
            throw new UsageException($"{count} inputs cannot all be written to standard output; give {option} a folder or a template such as {example}.");
        }

        if (!template.IsPerInput && !Folder(session, template))
        {
            throw new UsageException($"{count} inputs would all be written to '{template.Text}'; give {option} a folder or a template with {{name}}, such as {example}.");
        }
    }

    private static bool Folder(CliSession session, OutputTemplate template)
    {
        string text = template.Text;
        return text.EndsWith('/') || text.EndsWith('\\') || (!template.HasPlaceholders && Directory.Exists(session.ResolvePath(text)));
    }

    /// <summary>The path for one input: <c>-</c>, a file in a folder named after the input, or the template filled in.</summary>
    private static string Target(CliSession session, OutputTemplate template, InputSource input, string extension)
    {
        if (template.IsStdout)
        {
            return "-";
        }

        string path = Folder(session, template)
            ? System.IO.Path.Combine(session.ResolvePath(template.Text), $"{input.File.Name}.{extension}")
            : session.ResolvePath(template.Resolve(input.File, extension));
        if (!session.InPlace && input.File.Path is { } source && Batch.SamePath(path, source))
        {
            throw new UsageException($"{input.File.Display}: the output would replace the input; give another path, or --in-place to replace it.");
        }

        return path;
    }
}
