using System.CommandLine;
using System.Numerics;

using Egami.Cli.Host;
using Egami.Cli.Pipeline;
using Egami.Cli.Steps;
using Egami.Drawing;
using Egami.Fonts;
using Egami.Fonts.Layout;
using Egami.Fonts.Substitutes;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Cli.Commands;

/// <summary><c>egami text</c>, <c>egami overlay</c> and <c>egami montage</c> (stage C, CLI design Q9).</summary>
internal static class DrawingCommands
{
    /// <summary>The family the tool uses when no --font is given: bundled, so text looks the same on every machine.</summary>
    public const string DefaultFamily = "Liberation Sans";

    public static Command Text(CliSession session)
    {
        ImageOptions options = new();
        Option<string?> text = new("--text", "-t") { Description = "The text; the last input may give it instead: egami text in.png \"(c) 2026\" -o out.png.", HelpName = "text" };
        Option<string?> font = new("--font") { Description = $"A font file, an installed family name, or sans-serif, serif or monospace. {DefaultFamily} (bundled) by default.", HelpName = "font" };
        Option<float> size = new("--size", "-s") { Description = "The font size in pixels.", HelpName = "pixels", DefaultValueFactory = _ => 24 };
        Option<string?> color = new("--color") { Description = "The text color, a CSS color (white by default).", HelpName = "color" };
        Option<string?> anchor = new("--anchor") { Description = "Where the text sits: bottom-right (the default), bottom, center, top-left, ...", HelpName = "anchor" };
        Option<int> margin = new("--margin") { Description = "The distance from the image's edges in pixels.", HelpName = "pixels", DefaultValueFactory = _ => 16 };
        Option<string?> offset = new("--offset") { Description = "A further shift of the text in pixels, x,y.", HelpName = "x,y" };
        Option<float> rotate = new("--rotate") { Description = "Degrees clockwise to turn the text around its center.", HelpName = "degrees" };
        Option<float> opacity = new("--opacity") { Description = "From 0 (invisible) to 1 (the default).", HelpName = "0-1", DefaultValueFactory = _ => 1 };
        Option<int?> width = new("--width") { Description = "The width to wrap lines at, in pixels; the image's width less the margins by default.", HelpName = "pixels" };
        Option<string?> shadow = new("--shadow") { Description = "A drop shadow in this color, offset by a tenth of the size.", HelpName = "color" };
        Command command = new("text", "Draw a caption or watermark on images, with any font and script, wrapped to fit (2.8.2 layout).");
        options.AddTo(command);
        foreach (Option option in (Option[])[text, font, size, color, anchor, margin, offset, rotate, opacity, width, shadow])
        {
            command.Options.Add(option);
        }

        command.SetAction(result =>
        {
            string caption = result.GetValue(text) ?? TakeLast(result, options)
                ?? throw new UsageException("Give the text: egami text in.png \"(c) 2026\" -o out.png, or --text.");
            return ImageCommand.Run(session, result, options, () =>
            {
                FontFamily family = ResolveFont(session, result.GetValue(font));
                TextOptions draw = new(
                    caption,
                    family,
                    result.GetValue(size) is > 0 and <= 4096 and var px ? px : throw new UsageException("--size must be above 0 and at most 4096."),
                    result.GetValue(color) is { } c ? ValueParsers.ParseColor(c) : Rgba8.White,
                    result.GetValue(anchor) is { } a ? ValueParsers.Enum<Anchor>(a) : Anchor.BottomRight,
                    result.GetValue(margin),
                    result.GetValue(offset) is { } o ? (Point)ValueParsers.Point(o) : default,
                    result.GetValue(rotate),
                    Opacity(result.GetValue(opacity)),
                    result.GetValue(width),
                    result.GetValue(shadow) is { } sh ? ValueParsers.ParseColor(sh) : null);
                StepInfo step = new("text", "DrawImage", "Draws text.", [new OverloadInfo([])], static (_, _, _) => { })
                {
                    Sized = (ops, _, imageSize) => ops.DrawImage(RenderText(draw, imageSize), default(Point), draw.Opacity),
                    KeepsSize = true,
                };
                return new Plan(
                    [.. ImageCommand.Leading(result, options), new StepItem(new BoundStep(step, 0, [], [new("text", caption)])), new SaveItem(options.Save(result))],
                    options.Read(result));
            });
        });
        return command;
    }

    public static Command Overlay(CliSession session)
    {
        ImageOptions options = new();
        Option<string?> image = new("--image", "-i") { Description = "The image to lay on top (SVG too); the last input may give it instead: egami overlay in.png logo.svg -o out.png.", HelpName = "image" };
        Option<string?> anchor = new("--anchor") { Description = "Where it sits: bottom-right (the default), center, top-left, ...", HelpName = "anchor" };
        Option<int> margin = new("--margin") { Description = "The distance from the image's edges in pixels.", HelpName = "pixels" };
        Option<string?> offset = new("--offset") { Description = "A further shift in pixels, x,y.", HelpName = "x,y" };
        Option<float> opacity = new("--opacity") { Description = "From 0 (invisible) to 1 (the default).", HelpName = "0-1", DefaultValueFactory = _ => 1 };
        Option<string?> blend = new("--blend") { Description = "The blend mode: normal (the default), multiply, screen, overlay, darken, lighten, ...", HelpName = "mode" };
        Option<string?> size = new("--size", "-s") { Description = "Resize the overlay first, in the size syntax (W, xH, WxH, P%).", HelpName = "size" };
        Command command = new("overlay", "Lay an image (a logo, a watermark, an SVG) on images, placed by anchor, with opacity and a blend mode.");
        options.AddTo(command);
        foreach (Option option in (Option[])[image, anchor, margin, offset, opacity, blend, size])
        {
            command.Options.Add(option);
        }

        command.SetAction(result =>
        {
            string top = result.GetValue(image) ?? TakeLast(result, options)
                ?? throw new UsageException("Give the overlay: egami overlay in.png logo.svg -o out.png, or --image.");
            return ImageCommand.Run(session, result, options, () =>
            {
                Raster picture = session.LoadImage(top);
                if (result.GetValue(size) is { } sizeText)
                {
                    SizeSpec spec = SizeSpec.Parse(sizeText);
                    Raster resized = picture.Derive(ops => BuiltinSteps.AddResize(ops, spec, FitMode.Contain, Anchor.Center, ResampleFilter.Bicubic, false, default, new Size(picture.Width, picture.Height)));
                    picture.Dispose();
                    picture = resized;
                }

                Anchor where = result.GetValue(anchor) is { } a ? ValueParsers.Enum<Anchor>(a) : Anchor.BottomRight;
                int edge = result.GetValue(margin);
                Point shift = result.GetValue(offset) is { } o ? (Point)ValueParsers.Point(o) : default;
                float alpha = Opacity(result.GetValue(opacity));
                BlendMode mode = result.GetValue(blend) is { } b && b != "normal" ? ValueParsers.Enum<BlendMode>(b) : BlendMode.SourceOver;
                StepInfo step = new("overlay", "DrawImage", "Draws an image.", [new OverloadInfo([])], static (_, _, _) => { })
                {
                    Sized = (ops, _, imageSize) =>
                    {
                        (float x, float y) = Place(where, imageSize, picture.Width, picture.Height, edge);
                        ops.DrawImage(picture, new Point((int)MathF.Round(x) + shift.X, (int)MathF.Round(y) + shift.Y), mode, alpha);
                    },
                    KeepsSize = true,
                };
                return new Plan(
                    [.. ImageCommand.Leading(result, options), new StepItem(new BoundStep(step, 0, [], [new("image", top)])), new SaveItem(options.Save(result))],
                    options.Read(result));
            });
        });
        return command;
    }

    public static Command Montage(CliSession session)
    {
        Argument<string[]> inputs = new("inputs") { Description = "The images, in order: files or globs (sorted by name).", Arity = ArgumentArity.OneOrMore };
        Option<string> output = new("--output", "-o") { Description = "The contact sheet to write.", HelpName = "path", Required = true };
        Option<string?> format = new("--format", "-f") { Description = "The output format, when the extension does not say.", HelpName = "format" };
        Option<int?> columns = new("--columns", "-c") { Description = "Images per row; enough for a square sheet by default.", HelpName = "count" };
        Option<string> tile = new("--tile") { Description = "Each image's box; images are shrunk to fit it, never enlarged.", HelpName = "WxH", DefaultValueFactory = _ => "200x200" };
        Option<int> spacing = new("--spacing") { Description = "The gap between tiles, in pixels.", HelpName = "pixels", DefaultValueFactory = _ => 8 };
        Option<string?> background = new("--background") { Description = "The sheet's color (white by default).", HelpName = "color" };
        Option<bool> labels = new("--labels") { Description = "Write each file's name under its image." };
        Option<string?> font = new("--font") { Description = "The label font: a file, an installed family, or sans-serif, serif or monospace.", HelpName = "font" };
        Option<float> labelSize = new("--label-size") { Description = "The label size in pixels.", HelpName = "pixels", DefaultValueFactory = _ => 13 };
        Option<string[]> set = new("--set") { Description = "Writer settings as fmt.key=value.", HelpName = "fmt.key=value" };
        Option<int?> quality = new("--quality") { Description = "Lossy quality from 0 to 100.", HelpName = "0-100" };
        Command command = new("montage", "Lay images out on a contact sheet: a grid of tiles, optionally labeled with their file names.");
        command.Arguments.Add(inputs);
        foreach (Option option in (Option[])[output, format, columns, tile, spacing, background, labels, font, labelSize, set, quality])
        {
            command.Options.Add(option);
        }

        command.SetAction(result =>
        {
            List<InputFile> files = Globber.Expand(session, result.GetValue(inputs) ?? [], recursive: false);
            Size cell = (Size)ValueParsers.Size(result.GetValue(tile)!);
            if (cell.Width < 1 || cell.Height < 1 || cell.Width > 8192 || cell.Height > 8192)
            {
                throw new UsageException("--tile must be from 1x1 to 8192x8192.");
            }

            int gap = Math.Clamp(result.GetValue(spacing), 0, 1000);
            int count = result.GetValue(columns) ?? (int)Math.Ceiling(Math.Sqrt(files.Count));
            if (count < 1)
            {
                throw new UsageException("--columns must be at least 1.");
            }

            Rgba8 paper = result.GetValue(background) is { } bg ? ValueParsers.ParseColor(bg) : Rgba8.White;
            bool labeled = result.GetValue(labels);
            FontFamily? family = labeled ? ResolveFont(session, result.GetValue(font)) : null;
            float px = Math.Clamp(result.GetValue(labelSize), 4, 200);
            int labelHeight = labeled ? (int)MathF.Ceiling(px * 1.6f) : 0;
            List<Raster> tiles = [];
            try
            {
                foreach (InputFile file in files)
                {
                    using Raster image = new InputSource(file).Load(session, new Formats.ReaderOptions { Frames = ..1 });
                    using Raster fitted = image.Derive(ops => ops.Fit(cell.Width, cell.Height, enlarge: false));
                    Raster<Rgba8> tileImage = Raster.Create(cell.Width, cell.Height + labelHeight, paper, session.Context);
                    tileImage.Apply(ops => ops.DrawImage(fitted, new Point((cell.Width - fitted.Width) / 2, (cell.Height - fitted.Height) / 2)));
                    if (family is not null)
                    {
                        using RasterCanvas<Rgba8> canvas = tileImage.CreateCanvas();
                        ParagraphStyle style = new()
                        {
                            DefaultStyle = new TextStyle { FontFamilies = [family], Size = px, Color = Contrast(paper) },
                            Align = TextAlign.Center,
                            MaxLines = 1,
                            Ellipsis = "...",
                        };
                        canvas.DrawText(Fit(System.IO.Path.GetFileName(file.Path ?? file.Display), style, cell.Width - 4), new RectF(2, cell.Height, cell.Width - 4, labelHeight), style, VerticalAlign.Center);
                    }

                    tiles.Add(tileImage);
                }

                using Raster sheet = Raster.Grid(tiles, Math.Min(count, tiles.Count), null, gap, paper);
                Dictionary<string, string> common = [];
                if (result.GetValue(quality) is { } q)
                {
                    common["quality"] = q.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                SaveSpec save = new(result.GetValue(output), result.GetValue(format), ImageOptions.SetValues(result.GetValue(set)), common);
                (_, string path, Formats.FormatInfo outputFormat) = Runner.Resolve(session, new InputSource(files[0] with { Path = null }), save, null, Formats.Png.PngFormat.Info);
                Formats.WriterOptions options = Runner.Options(session, save, outputFormat);
                Output.Write(session, path, stream => sheet.Save(stream, outputFormat, options));
                session.Info($"{tiles.Count} images on a {sheet.Width}x{sheet.Height} sheet");
                return ExitCodes.Success;
            }
            finally
            {
                foreach (Raster t in tiles)
                {
                    t.Dispose();
                }
            }
        });
        return command;
    }

    /// <summary>Finds a font: a file, an installed family, a generic name, or a bundled substitute.</summary>
    public static FontFamily ResolveFont(CliSession session, string? font)
    {
        if (font is null)
        {
            return SubstituteFonts.Collection.TryGetFamily(DefaultFamily, out FontFamily bundled) ? bundled : SubstituteFonts.Collection.Families[0];
        }

        string path = session.ResolvePath(font);
        if (File.Exists(path))
        {
            FontCollection own = new();
            own.AddFile(path);
            return own.Families.Count > 0 ? own.Families[0] : throw new UsageException($"'{font}' holds no font egami can read.");
        }

        GenericFamily? generic = font.ToLowerInvariant() switch
        {
            "sans-serif" or "sans" => GenericFamily.SansSerif,
            "serif" => GenericFamily.Serif,
            "monospace" or "mono" => GenericFamily.Monospace,
            _ => null,
        };
        if (generic is { } g && FontCollection.System.Match(g, FontStyle.Normal) is { } match)
        {
            FontCollection one = new();
            one.Add(match.Typeface);
            return one.Families[0];
        }

        if (FontCollection.System.TryGetFamily(font, out FontFamily installed) || SubstituteFonts.Collection.TryGetFamily(font, out installed))
        {
            return installed;
        }

        throw new UsageException($"No font file or installed family is called '{font}'.");
    }

    /// <summary>Renders the text onto a transparent layer the size of the image.</summary>
    private static Raster<Rgba8> RenderText(TextOptions options, Size image)
    {
        float wrap = options.Width ?? Math.Max(1, image.Width - (2 * options.Margin));
        TextAlign align = options.Anchor switch
        {
            Anchor.Left or Anchor.TopLeft or Anchor.BottomLeft => TextAlign.Left,
            Anchor.Right or Anchor.TopRight or Anchor.BottomRight => TextAlign.Right,
            _ => TextAlign.Center,
        };
        TextStyle text = new()
        {
            FontFamilies = [options.Family],
            Size = options.Size,
            Color = options.Color,
            Shadows = options.Shadow is { } shadow ? [new TextShadow(new Vector2(options.Size / 10, options.Size / 10), options.Size / 12, shadow)] : [],
        };
        TextLayout layout = Paragraph.Create(options.Text, new ParagraphStyle { DefaultStyle = text, Align = align }).Layout(wrap);
        (float x, float y) = Place(options.Anchor, image, wrap, layout.Height, options.Margin);
        x += options.Offset.X;
        y += options.Offset.Y;
        Raster<Rgba8> layer = Raster.Create<Rgba8>(image.Width, image.Height);
        using RasterCanvas<Rgba8> canvas = layer.CreateCanvas();
        if (options.Rotate != 0)
        {
            canvas.Rotate(options.Rotate, new Vector2(x + (wrap / 2), y + (layout.Height / 2)));
        }

        canvas.DrawTextLayout(layout, new Vector2(x, y));
        return layer;
    }

    /// <summary>The top-left corner of a box of the given size placed by anchor inside the image, a margin from its edges.</summary>
    private static (float X, float Y) Place(Anchor anchor, Size image, float width, float height, int margin)
    {
        float left = margin, centerX = (image.Width - width) / 2, right = image.Width - margin - width;
        float top = margin, centerY = (image.Height - height) / 2, bottom = image.Height - margin - height;
        return anchor switch
        {
            Anchor.TopLeft => (left, top),
            Anchor.Top => (centerX, top),
            Anchor.TopRight => (right, top),
            Anchor.Left => (left, centerY),
            Anchor.Right => (right, centerY),
            Anchor.BottomLeft => (left, bottom),
            Anchor.Bottom => (centerX, bottom),
            Anchor.BottomRight => (right, bottom),
            _ => (centerX, centerY),
        };
    }

    /// <summary>A label shortened in the middle (<c>holi...0031.jpg</c>) until it fits the width.</summary>
    private static string Fit(string label, ParagraphStyle style, float width)
    {
        string text = label;
        int keep = label.Length;
        while (keep > 4 && Paragraph.Create(text, style).Layout(float.PositiveInfinity).LongestLine > width)
        {
            keep--;
            int head = (keep + 1) / 2;
            text = string.Concat(label.AsSpan(0, head), "...", label.AsSpan(label.Length - (keep - head)));
        }

        return text;
    }

    private static float Opacity(float value)
    {
        return value is >= 0 and <= 1 ? value : throw new UsageException("--opacity must be from 0 to 1.");
    }

    private static Rgba8 Contrast(Rgba8 background)
    {
        int luma = (background.R * 2126 + background.G * 7152 + background.B * 722) / 10000;
        return luma > 128 ? new Rgba8(30, 30, 30, 255) : Rgba8.White;
    }

    /// <summary>Takes the last input as the command's value when there are at least two.</summary>
    private static string? TakeLast(ParseResult result, ImageOptions options)
    {
        string[] inputs = result.GetValue(options.Inputs) ?? [];
        if (inputs.Length < 2)
        {
            return null;
        }

        options.InputsOverride = inputs[..^1];
        return inputs[^1];
    }

    private sealed record TextOptions(string Text, FontFamily Family, float Size, Rgba8 Color, Anchor Anchor, int Margin, Point Offset, float Rotate, float Opacity, int? Width, Rgba8? Shadow);
}
