using System.CommandLine;
using System.Text;

using Egami.Cli.Host;
using Egami.Cli.Pipeline;
using Egami.Metadata;
using Egami.Ocr;
using Egami.Pixels;

namespace Egami.Cli.Commands;

/// <summary>
/// <c>egami ocr</c> (2.12, design Q10): the text of images through an OCR program the user names, such as Tesseract, written
/// as plain text, hOCR or ALTO. Egami ships no engine; the program runs once per image, as <see cref="CommandOcrEngine"/>.
/// </summary>
internal static class OcrCommand
{
    public static Command Create(CliSession session)
    {
        ImageOptions options = new();
        Option<string> engine = new("--engine-command")
        {
            Description = "The OCR program and its arguments, run once per image: {image} is the image (PNG), {output} the result file without extension, {langs} the languages, {dpi} the resolution. It must write hOCR or ALTO, such as \"tesseract {image} {output} -l {langs} --dpi {dpi} hocr\".",
            HelpName = "command",
            Required = true,
        };
        Option<string?> output = new("--output", "-o")
        {
            Description = "Where to write: a file, a folder, a template with {name}, {ext}, {dir} and {index}, or - (the default) for standard output.",
            HelpName = "path",
        };
        Option<string?> asFormat = new("--as") { Description = "text, hocr or alto; by default from -o's extension (.txt, .hocr or .html, .xml), else text.", HelpName = "text|hocr|alto" };
        Option<string?> languages = new("--lang", "-l") { Description = "The languages to read, as the program names them, joined by + or commas, such as eng+nor.", HelpName = "languages" };
        Option<double?> dpi = new("--dpi") { Description = "The image's resolution, which programs use to judge text size; by default the image's own, else 300.", HelpName = "dpi" };
        Option<int?> timeout = new("--engine-timeout") { Description = "Seconds one run of the program may take; 300 by default.", HelpName = "seconds" };
        Command command = new("ocr", "Read the text of images with an OCR program (Tesseract or any that writes hOCR or ALTO), as plain text, hOCR or ALTO.");
        command.Arguments.Add(options.Inputs);
        command.Options.Add(engine);
        command.Options.Add(output);
        command.Options.Add(asFormat);
        command.Options.Add(languages);
        command.Options.Add(dpi);
        command.Options.Add(timeout);
        command.Options.Add(options.Recursive);
        command.Options.Add(options.Jobs);
        command.Options.Add(options.FailFast);
        command.SetAction(result =>
        {
            string engineCommand = result.GetValue(engine) is { Length: > 0 } text
                ? text
                : throw new UsageException("--engine-command names the OCR program, such as \"tesseract {image} {output} -l {langs} --dpi {dpi} hocr\".");
            string[] langs = (result.GetValue(languages) ?? string.Empty).Split(['+', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            int seconds = result.GetValue(timeout) ?? 300;
            if (seconds <= 0)
            {
                throw new UsageException("--engine-timeout must be more than 0 seconds.");
            }

            if (result.GetValue(dpi) is double given && !(given > 0 && double.IsFinite(given)))
            {
                throw new UsageException("--dpi must be more than 0.");
            }

            List<InputFile> inputs = options.ExpandInputs(session, result);
            OutputTemplate template = OutputTemplate.Parse(result.GetValue(output) ?? "-");
            if (inputs.Count > 1 && !template.IsStdout && !template.IsPerInput && !Directory.Exists(session.ResolvePath(template.Text)) && !template.Text.EndsWith('/') && !template.Text.EndsWith('\\'))
            {
                throw new UsageException($"{inputs.Count} inputs would all be written to '{template.Text}'; give a folder or a template with {{name}}, such as text/{{name}}.txt.");
            }

            string kind = result.GetValue(asFormat)?.ToLowerInvariant() ?? (template.IsStdout ? "text" : Path.GetExtension(template.Text).ToLowerInvariant() switch
            {
                ".hocr" or ".html" or ".htm" => "hocr",
                ".xml" or ".alto" => "alto",
                _ => "text",
            });
            if (kind is not ("text" or "hocr" or "alto"))
            {
                throw new UsageException($"--as '{kind}' is not text, hocr or alto.");
            }

            CommandOcrEngine ocr = new(engineCommand, new CommandOcrOptions { Languages = langs, Timeout = TimeSpan.FromSeconds(seconds) });
            string extension = kind switch { "hocr" => "hocr", "alto" => "xml", _ => "txt" };
            BatchOptions batch = options.Batch(session, result) with { Jobs = template.IsStdout ? 1 : result.GetValue(options.Jobs) };
            return Batch.Run(session, inputs, batch, InputSource.Each(input =>
            {
                using Raster image = input.Load(session);
                using Raster<Rgba8> pixels = image.As<Rgba8>();
                double resolution = result.GetValue(dpi) ?? Dpi(image.Metadata.Resolution) ?? 300;
                OcrRequest request = new(pixels) { Dpi = resolution, Languages = langs };
                OcrResult read = ocr.RecognizeAsync(request, session.Cancellation).GetAwaiter().GetResult();
                if (read.ImageSize.Width == 0)
                {
                    read = read with { ImageSize = new Size(pixels.Width, pixels.Height) };
                }

                void Write(Stream stream)
                {
                    switch (kind)
                    {
                        case "hocr":
                            Hocr.Write(stream, [read]);
                            break;
                        case "alto":
                            Alto.Write(stream, [read]);
                            break;
                        default:
                            byte[] bytes = Encoding.UTF8.GetBytes(read.Text + "\n");
                            stream.Write(bytes);
                            break;
                    }
                }

                if (template.IsStdout)
                {
                    if (inputs.Count > 1 && kind == "text")
                    {
                        session.Console.Text.WriteLine($"== {input.File.Display} ==");
                    }

                    using MemoryStream buffer = new();
                    Write(buffer);
                    session.Console.Text.Write(Encoding.UTF8.GetString(buffer.ToArray()));
                    session.Console.Text.Flush();
                    return [];
                }

                string path = session.ResolvePath(template.Resolve(input.File, extension));
                Output.Write(session, path, Write);
                session.Info($"{input.File.Display}: {read.Words.Count()} words");
                return [path];
            }));
        });
        return command;
    }

    /// <summary>The image's resolution in pixels per inch, or <see langword="null"/> when it does not say.</summary>
    private static double? Dpi(Resolution? resolution)
    {
        return resolution switch
        {
            { Unit: ResolutionUnit.PerInch, X: > 0 } r => r.X,
            { Unit: ResolutionUnit.PerCentimeter, X: > 0 } r => r.X * 2.54,
            { Unit: ResolutionUnit.PerMeter, X: > 0 } r => r.X * 0.0254,
            _ => null,
        };
    }
}
