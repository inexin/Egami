using System.CommandLine;
using System.Globalization;
using System.Text;

using Egami.Cli.Host;
using Egami.Cli.Pipeline;
using Egami.Fonts;

namespace Egami.Cli.Commands;

/// <summary>The font files <c>egami font</c> writes.</summary>
internal enum FontFileFormat
{
    /// <summary>WOFF 2.0.</summary>
    Woff2,

    /// <summary>WOFF 1.0.</summary>
    Woff,

    /// <summary>A TrueType font (.ttf).</summary>
    Ttf,

    /// <summary>An OpenType font (.otf).</summary>
    Otf,

    /// <summary>A TrueType or OpenType collection (.ttc).</summary>
    Ttc,
}

/// <summary>
/// <c>egami font convert|subset|info</c> (2.7.8): fonts written as WOFF2, WOFF or back to TrueType and OpenType losslessly with
/// <see cref="WebFontWriter"/> and <see cref="WebFontReader"/>, subset to the characters a page uses with <see cref="FontSubsetter"/>,
/// and described.
/// </summary>
internal static class FontCommands
{
    /// <summary>Tables a subset leaves out that a reader may miss, by what they hold.</summary>
    private static readonly (string What, string[] Tags)[] DroppedTables =
    [
        ("color glyphs", ["COLR", "CPAL", "SVG ", "CBDT", "CBLC", "sbix"]),
        ("embedded bitmaps", ["EBDT", "EBLC"]),
        ("vertical metrics", ["vhea", "vmtx", "VORG"]),
        ("legacy kerning", ["kern"]),
        ("math layout", ["MATH"]),
    ];

    public static Command Create(CliSession session)
    {
        Command font = new("font", "Convert fonts to WOFF2, WOFF or back to TTF/OTF, subset them to the characters a page uses, and describe them.");
        font.Subcommands.Add(Convert(session));
        font.Subcommands.Add(Subset(session));
        font.Subcommands.Add(FontInfo.Create(session));
        return font;
    }

    /// <summary>The inputs argument of the font commands.</summary>
    internal static Argument<string[]> Inputs() => new("inputs")
    {
        Description = "Font files (TTF, OTF, TTC, WOFF, WOFF2), globs (*, ?, **) expanded the same way in every shell, folders with --recursive, or - for standard input.",
        Arity = ArgumentArity.OneOrMore,
    };

    /// <summary>The inputs, expanded with folders taking font files.</summary>
    internal static List<InputFile> Expand(CliSession session, string[]? patterns, bool recursive)
    {
        session.FontInputs = true;
        return Globber.Expand(session, patterns ?? [], recursive);
    }

    private static Command Convert(CliSession session)
    {
        Argument<string[]> inputs = Inputs();
        Option<string?> output = OutputOption();
        Option<string?> format = FormatOption("woff2 (the default), woff, ttf, otf or ttc");
        Option<bool> noTransform = new("--no-transform") { Description = "Store WOFF2's glyf, loca and hmtx as they are, without the transforms that make TrueType outlines smaller." };
        Option<string?> metadata = new("--metadata") { Description = "An XML file to store as the WOFF or WOFF2 extended metadata block, replacing the source's.", HelpName = "file" };
        BatchFlags batch = new();
        Command command = new(
            "convert",
            "Convert fonts losslessly: to WOFF2 (the default) or WOFF for the web, or from WOFF and WOFF2 back to TTF, OTF or a TTC collection. "
            + "Collections convert to WOFF2 or TTC only.");
        command.Arguments.Add(inputs);
        command.Options.Add(output);
        command.Options.Add(format);
        command.Options.Add(noTransform);
        command.Options.Add(metadata);
        batch.AddTo(command);
        command.SetAction(result =>
        {
            List<InputFile> files = Expand(session, result.GetValue(inputs), result.GetValue(batch.Recursive));
            BatchOptions options = batch.Options(session, result);
            OutputTemplate? template = Template(session, result.GetValue(output), files.Count);
            FontFileFormat? requested = ParseFormat(result.GetValue(format));
            string? metadataText = result.GetValue(metadata) is { } metadataPath ? ReadText(session, metadataPath, "--metadata") : null;
            bool transform = !result.GetValue(noTransform);
            return Batch.Run(session, files, options with { Jobs = template?.IsStdout == true ? 1 : options.Jobs }, InputSource.Each(input =>
            {
                ReadOnlyMemory<byte> data = input.Bytes(session);
                FontProbe probe = Probe(input, data.Span);
                bool collection = IsCollection(probe);
                FontFileFormat target = FormatFor(session, template, input, requested);
                string path = Path(session, template, input, target);
                byte[] written;
                if (target is FontFileFormat.Woff or FontFileFormat.Woff2)
                {
                    if (target == FontFileFormat.Woff && collection)
                    {
                        throw new UsageException($"{input.File.Display} is a collection of {probe.FaceCount} fonts, which WOFF 1.0 cannot hold; write .woff2 or .ttc.");
                    }

                    written = WebFontWriter.Write(data, new WebFontOptions
                    {
                        Format = target == FontFileFormat.Woff ? WebFontFormat.Woff : WebFontFormat.Woff2,
                        TransformGlyf = transform,
                        TransformHmtx = transform,
                        Metadata = metadataText,
                        Context = session.Context,
                    });
                }
                else
                {
                    if (metadataText is not null)
                    {
                        throw new UsageException("--metadata goes in WOFF and WOFF2 files only; a TTF, OTF or TTC has no metadata block.");
                    }

                    if (target == FontFileFormat.Ttc && !collection)
                    {
                        throw new UsageException($"{input.File.Display} is a single font, not a collection; write .ttf or .otf.");
                    }

                    if (target != FontFileFormat.Ttc && collection)
                    {
                        throw new UsageException($"{input.File.Display} is a collection of {probe.FaceCount} fonts; write .ttc or .woff2.");
                    }

                    written = probe.Kind is FontFileKind.Woff or FontFileKind.Woff2 ? WebFontReader.ToSfnt(data, session.Context) : data.ToArray();
                }

                Output.Write(session, path, stream => stream.Write(written));
                session.Info($"{input.File.Display}: {data.Length:N0} -> {written.Length:N0} bytes ({Extension(target)})");
                return path == "-" ? [] : [path];
            }));
        });
        return command;
    }

    private static Command Subset(CliSession session)
    {
        Argument<string[]> inputs = Inputs();
        Option<string?> output = OutputOption();
        Option<string?> format = FormatOption("woff2 (the default), woff, ttf or otf");
        Option<string[]> text = new("--text") { Description = "Characters to keep. Repeat it, and combine it with --text-file and --unicodes; together they say what to keep.", HelpName = "text", AllowMultipleArgumentsPerToken = false };
        Option<string[]> textFile = new("--text-file") { Description = "Files or globs whose characters to keep, read as UTF-8 text (HTML and all: tags add only ASCII).", HelpName = "file", AllowMultipleArgumentsPerToken = false };
        Option<string[]> unicodes = new("--unicodes") { Description = "Code points to keep in CSS unicode-range syntax, such as U+0020-007E,U+00E5,U+4??.", HelpName = "ranges", AllowMultipleArgumentsPerToken = false };
        Option<bool> noLayout = new("--no-layout") { Description = "Keep only the glyphs the characters map to, without GSUB, GPOS and GDEF, renumbered densely: the smallest file, shaped without ligatures, kerning or contextual forms." };
        Option<bool> retainGids = new("--retain-gids") { Description = "Keep every glyph's id, with empty glyphs in the gaps (the default unless --no-layout)." };
        Option<string?> variation = new("--variation") { Description = "Where a variable font is instanced, such as wght=700,wdth=90; the subset is always a static font, by default at the font's default position.", HelpName = "axis=value,..." };
        Option<bool> noHinting = new("--no-hinting") { Description = "Leave out TrueType hinting (fpgm, prep, cvt and glyph instructions); CFF hints stay." };
        Option<string?> namePrefix = new("--name-prefix") { Description = "Put this before the PostScript and CFF font names, such as ABCDEF+.", HelpName = "prefix" };
        Option<int> face = new("--face") { Description = "Which font of a collection to subset, from 0.", HelpName = "index" };
        BatchFlags batch = new();
        Command command = new(
            "subset",
            "Subset fonts to the characters a page uses and write WOFF2 (the default), WOFF, TTF or OTF. By default the subset keeps the layout "
            + "tables and every glyph they can substitute, so it shapes the text as the full font does; prints the glyphs kept, the characters "
            + "the font lacks and the sizes.");
        command.Arguments.Add(inputs);
        foreach (Option option in (Option[])[output, format, text, textFile, unicodes, noLayout, retainGids, variation, noHinting, namePrefix, face])
        {
            command.Options.Add(option);
        }

        batch.AddTo(command);
        command.SetAction(result =>
        {
            Wanted wanted = Wanted.From(session, result.GetValue(text), result.GetValue(textFile), result.GetValue(unicodes));
            List<InputFile> files = Expand(session, result.GetValue(inputs), result.GetValue(batch.Recursive));
            BatchOptions options = batch.Options(session, result);
            OutputTemplate? template = Template(session, result.GetValue(output), files.Count);
            FontFileFormat? requested = ParseFormat(result.GetValue(format));
            if (requested == FontFileFormat.Ttc)
            {
                throw new UsageException("A subset is a single font; write woff2, woff, ttf or otf.");
            }

            List<FontVariation>? variations = ParseVariations(result.GetValue(variation));
            int faceIndex = result.GetValue(face);
            if (faceIndex < 0)
            {
                throw new UsageException("--face must be 0 or more.");
            }

            bool layout = !result.GetValue(noLayout);
            SubsetOptions subsetOptions = new()
            {
                LayoutClosure = layout,
                RetainGlyphIds = layout || result.GetValue(retainGids),
                DropHinting = result.GetValue(noHinting),
                FontNamePrefix = result.GetValue(namePrefix),
            };
            string[] reports = new string[files.Count];
            int code = Batch.Run(session, files, options with { Jobs = template?.IsStdout == true ? 1 : options.Jobs }, InputSource.Each(input =>
            {
                ReadOnlyMemory<byte> data = input.Bytes(session);
                FontProbe probe = Probe(input, data.Span);
                if (faceIndex >= probe.FaceCount)
                {
                    throw new UsageException($"--face {faceIndex}: {input.File.Display} has {probe.FaceCount} font{(probe.FaceCount == 1 ? string.Empty : "s")}.");
                }

                FontFileFormat target = FormatFor(session, template, input, requested);
                if (target == FontFileFormat.Ttc)
                {
                    throw new UsageException($"{input.File.Display}: a subset is a single font; write .woff2, .woff, .ttf or .otf.");
                }

                Typeface typeface = Typeface.FromMemory(data, faceIndex, new FontLoadOptions { Context = session.Context });
                if (typeface.Outlines == FontOutlines.None)
                {
                    throw new UnsupportedFormatException($"{input.File.Display} has no outlines to subset.");
                }

                string path = Path(session, template, input, target);
                (List<ushort> glyphs, List<int> missing, int fromRanges) = wanted.Map(typeface);
                SubsetOptions these = subsetOptions;
                string instance = string.Empty;
                if (typeface.IsVariable)
                {
                    these = these with { Variations = Checked(typeface, variations) };
                    IEnumerable<string> position = typeface.VariationAxes.Select(a =>
                        $"{a.Tag}={(variations?.LastOrDefault(v => v.Axis == a.Tag) is { Axis.Value: not 0 } v ? v.Value : a.Default).ToString("0.###", CultureInfo.InvariantCulture)}");
                    instance = $", static at {string.Join(" ", position)}";
                    if (variations is null)
                    {
                        session.Warn($"{input.File.Display} is a variable font; the subset drops its axes ({string.Join(", ", typeface.VariationAxes.Select(a => $"{a.Tag} {a.Min:0.###}-{a.Max:0.###}"))}) and is a static font at the default position; --variation picks another.");
                    }
                }
                else if (variations is not null)
                {
                    session.Warn($"{input.File.Display} is not a variable font; --variation is ignored.");
                }

                if (typeface.Embedding.HasFlag(FontEmbedding.NoSubsetting))
                {
                    session.Warn($"{input.File.Display}: the font's embedding flags (OS/2 fsType) say it may not be subset; check its license.");
                }

                FontSubset subset = FontSubsetter.Subset(typeface, glyphs, these);
                byte[] written = target is FontFileFormat.Woff or FontFileFormat.Woff2
                    ? WebFontWriter.Write(subset, new WebFontOptions { Format = target == FontFileFormat.Woff ? WebFontFormat.Woff : WebFontFormat.Woff2, Context = session.Context })
                    : subset.Data.ToArray();
                Output.Write(session, path, stream => stream.Write(written));

                Typeface back = subset.ToTypeface();
                foreach ((string what, string[] tags) in DroppedTables)
                {
                    string[] lost = [.. tags.Where(t => typeface.TryGetTable(new FontTag(t), out _) && !back.TryGetTable(new FontTag(t), out _)).Select(t => t.TrimEnd())];
                    if (lost.Length > 0)
                    {
                        session.Warn($"{input.File.Display}: the subset leaves out {string.Join(", ", lost)} ({what}).");
                    }
                }

                StringBuilder line = new();
                line.Append(CultureInfo.InvariantCulture, $"{input.File.Display}: {subset.GlyphMap.Count:N0} of {typeface.GlyphCount:N0} glyphs kept; ");
                if (wanted.Explicit.Count > 0)
                {
                    line.Append(missing.Count == 0
                        ? wanted.Explicit.Count == 1 ? "the 1 character found" : $"all {wanted.Explicit.Count:N0} characters found"
                        : $"{missing.Count:N0} of {Characters(wanted.Explicit.Count)} missing: {Describe(missing)}");
                }

                if (wanted.Ranges.Count > 0)
                {
                    line.Append(wanted.Explicit.Count > 0 ? ", " : string.Empty).Append(CultureInfo.InvariantCulture, $"{Characters(fromRanges)} in the ranges");
                }

                line.Append(CultureInfo.InvariantCulture, $"; {data.Length:N0} -> {written.Length:N0} bytes ({Extension(target)}{instance})");
                reports[input.File.Index] = line.ToString();
                return path == "-" ? [] : [path];
            }));

            TextWriter report = template?.IsStdout == true ? session.Console.Error : session.Console.Text;
            foreach (string? line in reports)
            {
                if (line is not null)
                {
                    report.WriteLine(line);
                }
            }

            return code;
        });
        return command;
    }

    /// <summary>The batch options the font commands share: --recursive, --jobs, --in-place and --fail-fast.</summary>
    private sealed class BatchFlags
    {
        public Option<bool> Recursive { get; } = new("--recursive", "-r") { Description = "Take every font in folders given as inputs, at any depth." };

        public Option<int> Jobs { get; } = new("--jobs", "-j") { Description = "How many files to work on at once; 0 (the default) for one per processor.", HelpName = "count" };

        public Option<bool> InPlace { get; } = new("--in-place") { Description = "Allow outputs to replace their inputs; without -o, each input is written over in its own format." };

        public Option<bool> FailFast { get; } = new("--fail-fast") { Description = "Stop at the first file that fails." };

        public void AddTo(Command command)
        {
            command.Options.Add(Recursive);
            command.Options.Add(Jobs);
            command.Options.Add(InPlace);
            command.Options.Add(FailFast);
        }

        public BatchOptions Options(CliSession session, ParseResult result)
        {
            bool inPlace = result.GetValue(InPlace);
            session.InPlace = inPlace;
            int jobs = result.GetValue(Jobs);
            if (jobs < 0)
            {
                throw new UsageException("--jobs must be 0 or more.");
            }

            return new BatchOptions(jobs, result.GetValue(FailFast), inPlace);
        }
    }

    private static Option<string?> OutputOption() => new("--output", "-o")
    {
        Description = "Where to write: a file, a folder, a template with {name}, {ext}, {dir}, {index} and {format}, or - for standard output. The extension picks the format.",
        HelpName = "path",
    };

    private static Option<string?> FormatOption(string formats) => new("--format", "-f")
    {
        Description = $"The output format, when -o's extension does not say: {formats}.",
        HelpName = "format",
    };

    /// <summary>The output template, checked against the number of inputs; <see langword="null"/> for --in-place without -o.</summary>
    private static OutputTemplate? Template(CliSession session, string? text, int count)
    {
        if (text is null)
        {
            if (!session.InPlace)
            {
                throw new UsageException("No output: give -o with a file, a folder, a template such as out/{name}.woff2, or - for standard output; or --in-place.");
            }

            return null;
        }

        OutputTemplate template = Hosting.OutputTemplate.Parse(text);
        TraceCommand.CheckMany(session, template, count, "-o", "out/{name}.woff2");
        return template;
    }

    /// <summary>The path to write one input to: -o's, or the input itself with --in-place and no -o.</summary>
    private static string Path(CliSession session, OutputTemplate? template, InputSource input, FontFileFormat format)
    {
        if (template is not null)
        {
            return TraceCommand.Target(session, template, input, Extension(format));
        }

        return input.File.Path ?? throw new UsageException("Standard input cannot be written in place; give -o.");
    }

    /// <summary>The format to write: --format, else -o's extension, else the input's own for --in-place, else WOFF2.</summary>
    private static FontFileFormat FormatFor(CliSession session, OutputTemplate? template, InputSource input, FontFileFormat? requested)
    {
        if (requested is { } format)
        {
            return format;
        }

        string? extension = template is null ? input.File.Extension
            : template.IsStdout || TraceCommand.Folder(session, template) ? null
            : System.IO.Path.GetExtension(template.Text).TrimStart('.');
        return extension is not null && ParseFormat(extension, quiet: true) is { } fromName ? fromName : FontFileFormat.Woff2;
    }

    /// <summary>Parses a format name or extension; <see langword="null"/> for none, or for an unknown name when <paramref name="quiet"/>.</summary>
    internal static FontFileFormat? ParseFormat(string? name, bool quiet = false)
    {
        if (name is null)
        {
            return null;
        }

        return name.Trim().TrimStart('.').ToLowerInvariant() switch
        {
            "woff2" => FontFileFormat.Woff2,
            "woff" => FontFileFormat.Woff,
            "ttf" or "truetype" => FontFileFormat.Ttf,
            "otf" or "opentype" => FontFileFormat.Otf,
            "ttc" or "otc" or "collection" => FontFileFormat.Ttc,
            _ when quiet => null,
            _ => throw new UsageException($"'{name}' is not a font format; use woff2, woff, ttf, otf or ttc."),
        };
    }

    internal static string Extension(FontFileFormat format) => format.ToString().ToLowerInvariant();

    /// <summary>Probes an input, refusing data that is no font the commands read.</summary>
    internal static FontProbe Probe(InputSource input, ReadOnlySpan<byte> data)
    {
        FontProbe probe = FontLoader.Probe(data);
        return probe.Kind switch
        {
            FontFileKind.Unknown => throw new UnsupportedFormatException($"{input.File.Display} is not a TrueType, OpenType, collection, WOFF or WOFF2 font."),
            FontFileKind.Cff => throw new UnsupportedFormatException($"{input.File.Display} is a bare CFF font, which has no sfnt tables; egami font reads TrueType, OpenType, collections, WOFF and WOFF2."),
            _ => probe,
        };
    }

    internal static bool IsCollection(FontProbe probe) => probe.Kind == FontFileKind.Collection || probe.FaceCount > 1;

    private static string ReadText(CliSession session, string path, string option)
    {
        string full = session.ResolvePath(path);
        session.Paths?.Check(full);
        if (!File.Exists(full))
        {
            throw new UsageException($"{option}: '{path}' does not exist.");
        }

        return File.ReadAllText(full, Encoding.UTF8);
    }

    /// <summary>Parses --variation: axis=value pairs separated by commas.</summary>
    private static List<FontVariation>? ParseVariations(string? text)
    {
        if (text is null)
        {
            return null;
        }

        List<FontVariation> list = [];
        foreach (string part in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = part.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0 || equals > 4 || !float.TryParse(part[(equals + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || !float.IsFinite(value))
            {
                throw new UsageException($"--variation '{part}' is not axis=value, such as wght=700.");
            }

            list.Add(new FontVariation(part[..equals].Trim(), value));
        }

        return list.Count > 0 ? list : throw new UsageException("--variation needs at least one axis=value.");
    }

    /// <summary>The variations, each checked against the face's axes.</summary>
    private static List<FontVariation>? Checked(Typeface face, List<FontVariation>? variations)
    {
        foreach (FontVariation variation in variations ?? [])
        {
            if (!face.VariationAxes.Any(a => a.Tag == variation.Axis))
            {
                throw new UsageException($"--variation: the font has no {variation.Axis} axis; it has {string.Join(", ", face.VariationAxes.Select(a => a.Tag.ToString()))}.");
            }
        }

        return variations;
    }

    private static string Characters(int count) => count == 1 ? "1 character" : $"{count:N0} characters";

    /// <summary>The first missing characters, as U+XXXX and the character where it prints.</summary>
    private static string Describe(List<int> missing)
    {
        const int Shown = 8;
        IEnumerable<string> shown = missing.Take(Shown).Select(c => $"U+{c:X4}" + (Rune.IsValid(c) && !Rune.IsControl(new Rune(c)) && !Rune.IsWhiteSpace(new Rune(c)) ? $" {char.ConvertFromUtf32(c)}" : string.Empty));
        return string.Join(", ", shown) + (missing.Count > Shown ? $" and {missing.Count - Shown:N0} more" : string.Empty);
    }

    /// <summary>
    /// The characters a subset keeps: those of --text and --text-file and single code points of --unicodes, each reported when the
    /// font lacks it, and the ranges of --unicodes, which keep whatever the font has in them.
    /// </summary>
    private sealed class Wanted
    {
        private Wanted(SortedSet<int> explicitCodes, List<(int First, int Last)> ranges)
        {
            Explicit = explicitCodes;
            Ranges = ranges;
        }

        public SortedSet<int> Explicit { get; }

        public List<(int First, int Last)> Ranges { get; }

        public static Wanted From(CliSession session, string[]? texts, string[]? textFiles, string[]? unicodes)
        {
            SortedSet<int> codes = [];
            List<(int, int)> ranges = [];
            foreach (string text in texts ?? [])
            {
                Add(codes, text);
            }

            if (textFiles is { Length: > 0 })
            {
                List<InputFile> files;
                try
                {
                    files = Globber.Expand(session, textFiles, recursive: false);
                }
                catch (FileNotFoundException missing)
                {
                    throw new UsageException($"--text-file: {missing.Message}");
                }

                foreach (InputFile file in files)
                {
                    Add(codes, file.Path is null ? ReadStdin(session) : File.ReadAllText(file.Path, Encoding.UTF8));
                }
            }

            foreach (string list in unicodes ?? [])
            {
                foreach ((int first, int last) in UnicodeRange.Parse(list, "--unicodes"))
                {
                    if (first == last)
                    {
                        codes.Add(first);
                    }
                    else
                    {
                        ranges.Add((first, last));
                    }
                }
            }

            // Line ends, tabs and other controls of a text file never need a glyph.
            codes.RemoveWhere(c => c < 0x20 || (c >= 0x7F && c < 0xA0));
            if (codes.Count == 0 && ranges.Count == 0)
            {
                throw new UsageException("Nothing to keep: give --text, --text-file or --unicodes.");
            }

            return new Wanted(codes, ranges);
        }

        /// <summary>The glyphs of the characters the face has, the asked characters it lacks, and how many characters the ranges added.</summary>
        public (List<ushort> Glyphs, List<int> Missing, int FromRanges) Map(Typeface face)
        {
            HashSet<ushort> glyphs = [];
            List<int> missing = [];
            foreach (int code in Explicit)
            {
                if (face.TryGetGlyph(code, out ushort glyph) && glyph != 0)
                {
                    glyphs.Add(glyph);
                }
                else
                {
                    missing.Add(code);
                }
            }

            int fromRanges = 0;
            if (Ranges.Count > 0)
            {
                foreach (int code in FontInfo.CodePoints(face))
                {
                    if (!Explicit.Contains(code) && Ranges.Any(r => code >= r.First && code <= r.Last) && face.TryGetGlyph(code, out ushort glyph) && glyph != 0)
                    {
                        glyphs.Add(glyph);
                        fromRanges++;
                    }
                }
            }

            return ([.. glyphs], missing, fromRanges);
        }

        private static void Add(SortedSet<int> codes, string text)
        {
            foreach (Rune rune in text.EnumerateRunes())
            {
                codes.Add(rune.Value);
            }
        }

        private static string ReadStdin(CliSession session)
        {
            using Stream input = session.Console.In();
            using StreamReader reader = new(input, Encoding.UTF8);
            return reader.ReadToEnd();
        }
    }
}

/// <summary>Parses and writes CSS unicode-range values (CSS Fonts 4, section 4.5): U+0020, U+0020-007E and U+4?? wildcards.</summary>
internal static class UnicodeRange
{
    /// <summary>Parses a comma- or space-separated list of ranges; throws <see cref="UsageException"/> naming <paramref name="option"/>.</summary>
    public static List<(int First, int Last)> Parse(string text, string option)
    {
        List<(int, int)> ranges = [];
        foreach (string token in text.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!token.StartsWith("U+", StringComparison.OrdinalIgnoreCase) || token.Length < 3)
            {
                throw Bad(token, option);
            }

            string body = token[2..];
            int dash = body.IndexOf('-', StringComparison.Ordinal);
            int first, last;
            if (dash >= 0)
            {
                first = Hex(body[..dash], token, option);
                last = Hex(body[(dash + 1)..], token, option);
            }
            else if (body.Contains('?', StringComparison.Ordinal))
            {
                int wild = body.Length - body.TrimEnd('?').Length;
                if (body.TrimEnd('?').Contains('?', StringComparison.Ordinal) || body.Length > 6)
                {
                    throw Bad(token, option);
                }

                string head = body[..^wild];
                first = Hex(head.Length == 0 ? "0" : head, token, option, allowEmpty: true) << (4 * wild);
                last = first | ((1 << (4 * wild)) - 1);
            }
            else
            {
                first = last = Hex(body, token, option);
            }

            if (first > last || last > 0x10FFFF)
            {
                throw new UsageException($"{option}: '{token}' is out of order or past U+10FFFF.");
            }

            ranges.Add((first, last));
        }

        return ranges.Count > 0 ? ranges : throw new UsageException($"{option} needs at least one range, such as U+0020-007E.");
    }

    /// <summary>Writes sorted code points as a unicode-range value, such as U+0020-007E, U+00A0-00FF.</summary>
    public static string Write(IReadOnlyList<int> sorted)
    {
        StringBuilder text = new();
        for (int i = 0; i < sorted.Count;)
        {
            int j = i;
            while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1)
            {
                j++;
            }

            text.Append(text.Length > 0 ? ", " : string.Empty).Append(CultureInfo.InvariantCulture, $"U+{sorted[i]:X4}");
            if (j > i)
            {
                text.Append(CultureInfo.InvariantCulture, $"-{sorted[j]:X4}");
            }

            i = j + 1;
        }

        return text.ToString();
    }

    private static int Hex(string digits, string token, string option, bool allowEmpty = false)
    {
        if ((digits.Length == 0 && !allowEmpty) || digits.Length > 6 || !int.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int value))
        {
            throw Bad(token, option);
        }

        return value;
    }

    private static UsageException Bad(string token, string option) => new($"{option}: '{token}' is not a unicode-range value such as U+0020, U+0020-007E or U+4??.");
}
