using System.Buffers.Binary;
using System.CommandLine;
using System.Globalization;
using System.Text.Json;

using Egami.Cli.Host;
using Egami.Cli.Pipeline;
using Egami.Fonts;

namespace Egami.Cli.Commands;

/// <summary>
/// <c>egami font info</c> (2.7.8): each font's names, container, outlines, glyph count, units per em, variation axes, embedding
/// rights, tables and the Unicode blocks it covers, as text or JSON.
/// </summary>
internal static class FontInfo
{
    /// <summary>The name ids shown, with their JSON names.</summary>
    private static readonly (int Id, string Name)[] NameIds =
    [
        (0, "copyright"), (1, "family"), (2, "subfamily"), (4, "fullName"), (5, "version"), (6, "postScriptName"), (7, "trademark"),
        (8, "manufacturer"), (9, "designer"), (11, "vendorUrl"), (13, "license"), (14, "licenseUrl"), (16, "typographicFamily"),
        (17, "typographicSubfamily"),
    ];

    public static Command Create(CliSession session)
    {
        Argument<string[]> inputs = FontCommands.Inputs();
        Option<bool> json = new("--json") { Description = "Write JSON, with every name, table and block, and the coverage as a CSS unicode-range." };
        Option<bool> recursive = new("--recursive", "-r") { Description = "Take every font in folders given as inputs, at any depth." };
        Option<int> jobs = new("--jobs", "-j") { Description = "How many files to read at once; 0 (the default) for one per processor.", HelpName = "count" };
        Option<bool> failFast = new("--fail-fast") { Description = "Stop at the first file that fails." };
        Command command = new("info", "Describe fonts: names, container, outlines, glyphs, units per em, variation axes, embedding rights, tables and the Unicode blocks covered.");
        command.Arguments.Add(inputs);
        command.Options.Add(json);
        command.Options.Add(recursive);
        command.Options.Add(jobs);
        command.Options.Add(failFast);
        command.SetAction(result =>
        {
            List<InputFile> files = FontCommands.Expand(session, result.GetValue(inputs), result.GetValue(recursive));
            if (result.GetValue(jobs) < 0)
            {
                throw new UsageException("--jobs must be 0 or more.");
            }

            Report?[] reports = new Report?[files.Count];
            int code = Batch.Run(session, files, new BatchOptions(result.GetValue(jobs), result.GetValue(failFast), false), InputSource.Each(input =>
            {
                reports[input.File.Index] = Read(session, input);
                return [];
            }));
            if (result.GetValue(json))
            {
                JsonOut.Write(session.Console.Text, writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteStartArray("files");
                    foreach (Report? report in reports)
                    {
                        if (report is not null)
                        {
                            WriteJson(writer, report);
                        }
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                });
            }
            else
            {
                foreach (Report? report in reports)
                {
                    if (report is not null)
                    {
                        WriteText(session.Console.Text, report);
                    }
                }
            }

            return code;
        });
        return command;
    }

    /// <summary>The code points a face maps to a glyph through its Unicode character maps, sorted.</summary>
    public static List<int> CodePoints(Typeface face)
    {
        SortedSet<int> codes = [];
        foreach (CharacterMap map in face.CharacterMaps)
        {
            if (map.PlatformId == 0 || (map.PlatformId == 3 && map.EncodingId is 1 or 10))
            {
                foreach (CharacterMapping mapping in map.GetMappings())
                {
                    if (mapping.Glyph != 0 && mapping.Code <= 0x10FFFF)
                    {
                        codes.Add(mapping.Code);
                    }
                }
            }
        }

        // The face's own choice of map decides; a code another map has but the chosen one lacks is not covered.
        return [.. codes.Where(c => face.TryGetGlyph(c, out ushort glyph) && glyph != 0)];
    }

    private static Report Read(CliSession session, InputSource input)
    {
        ReadOnlyMemory<byte> data = input.Bytes(session);
        FontProbe probe = FontLoader.Probe(data.Span);
        if (probe.Kind == FontFileKind.Unknown)
        {
            throw new UnsupportedFormatException($"{input.File.Display} is not a TrueType, OpenType, collection, WOFF, WOFF2 or CFF font.");
        }

        FontLoadOptions options = new() { Context = session.Context };
        List<FaceReport> faces = [];
        if (probe.Kind == FontFileKind.Cff)
        {
            faces.Add(Describe(Typeface.FromCff(data), 0, []));
        }
        else
        {
            byte[] sfnt = probe.Kind is FontFileKind.Woff or FontFileKind.Woff2 ? WebFontReader.ToSfnt(data, session.Context) : data.ToArray();
            for (int i = 0; i < probe.FaceCount; i++)
            {
                faces.Add(Describe(Typeface.FromMemory(data, i, options), i, Tables(sfnt, i)));
            }
        }

        return new Report(input.File.Display, probe.Kind, data.Length, faces);
    }

    private static FaceReport Describe(Typeface face, int index, List<(string Tag, int Length)> tables)
    {
        List<(string, string)> names = [];
        IReadOnlyList<FontNameRecord> records = face.GetNames();
        foreach ((int id, string name) in NameIds)
        {
            if (English(records, id) is { } value)
            {
                names.Add((name, value));
            }
        }

        List<int> codes = CodePoints(face);
        List<(string Name, int First, int Last, int Covered)> blocks = [];
        (int First, int Last, string Name)[] all = UnicodeBlocks.All;
        int at = 0;
        foreach (int code in codes)
        {
            while (at < all.Length && all[at].Last < code)
            {
                at++;
            }

            bool inBlock = at < all.Length && all[at].First <= code;
            (string name, int first, int last) = inBlock ? (all[at].Name, all[at].First, all[at].Last) : ("No block", -1, -1);
            if (blocks.Count > 0 && blocks[^1].Name == name)
            {
                blocks[^1] = blocks[^1] with { Covered = blocks[^1].Covered + 1 };
            }
            else
            {
                blocks.Add((name, first, last, 1));
            }
        }

        string outlines = face.Outlines switch
        {
            FontOutlines.TrueType => "TrueType",
            FontOutlines.Cff when face.TryGetTable(new FontTag("CFF2"), out _) => "CFF2",
            FontOutlines.Cff => "CFF",
            _ => "none",
        };
        return new FaceReport(index, face, names, outlines, tables, codes, blocks);
    }

    /// <summary>A name in English (Windows en-US, else Macintosh English, else Unicode, else any), or <see langword="null"/>.</summary>
    private static string? English(IReadOnlyList<FontNameRecord> records, int id)
    {
        FontNameRecord? best = null;
        int bestRank = int.MaxValue;
        foreach (FontNameRecord record in records)
        {
            if (record.NameId != id || string.IsNullOrWhiteSpace(record.Value))
            {
                continue;
            }

            int rank = record.PlatformId == 3 && record.LanguageId == 0x409 ? 0
                : record.PlatformId == 1 && record.LanguageId == 0 ? 1
                : record.PlatformId == 0 ? 2
                : 3;
            if (rank < bestRank)
            {
                best = record;
                bestRank = rank;
            }
        }

        return best?.Value.Trim();
    }

    /// <summary>The table directory of one font of an sfnt or collection: each table's tag and length, in directory order.</summary>
    private static List<(string Tag, int Length)> Tables(byte[] sfnt, int face)
    {
        ReadOnlySpan<byte> data = sfnt;
        int offset = 0;
        if (data.Length >= 12 && BinaryPrimitives.ReadUInt32BigEndian(data) == 0x74746366)
        {
            int at = 12 + (4 * face);
            offset = at + 4 <= data.Length ? (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(data[at..]), int.MaxValue) : data.Length;
        }

        List<(string, int)> tables = [];
        if (offset + 12 > data.Length)
        {
            return tables;
        }

        int count = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 4)..]);
        for (int i = 0; i < count && offset + 12 + (16 * i) + 16 <= data.Length; i++)
        {
            ReadOnlySpan<byte> entry = data.Slice(offset + 12 + (16 * i), 16);
            tables.Add((new FontTag(BinaryPrimitives.ReadUInt32BigEndian(entry)).ToString(), (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(entry[12..]), int.MaxValue)));
        }

        return tables;
    }

    private static string Container(FontFileKind kind, IReadOnlyList<FaceReport> faces) => kind switch
    {
        FontFileKind.Woff => "WOFF",
        FontFileKind.Woff2 => faces.Count > 1 ? $"WOFF2 collection of {faces.Count} fonts" : "WOFF2",
        FontFileKind.Collection => $"collection of {faces.Count} fonts",
        FontFileKind.Cff => "bare CFF",
        _ => faces.Count > 0 && faces[0].Outlines == "TrueType" ? "TrueType font" : "OpenType font",
    };

    private static string Embedding(FontEmbedding embedding)
    {
        string usage = (embedding & (FontEmbedding.Restricted | FontEmbedding.PreviewAndPrint | FontEmbedding.Editable)) switch
        {
            0 => "installable",
            var e when e.HasFlag(FontEmbedding.Restricted) => "restricted",
            var e when e.HasFlag(FontEmbedding.Editable) => "editable",
            _ => "preview and print",
        };
        return usage + (embedding.HasFlag(FontEmbedding.NoSubsetting) ? ", no subsetting" : string.Empty) + (embedding.HasFlag(FontEmbedding.BitmapOnly) ? ", bitmap only" : string.Empty);
    }

    private static string Number(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static void WriteText(TextWriter output, Report report)
    {
        output.WriteLine($"{report.Path}: {Container(report.Kind, report.Faces)}, {report.Bytes:N0} bytes");
        foreach (FaceReport f in report.Faces)
        {
            Typeface face = f.Face;
            string indent = report.Faces.Count > 1 ? "    " : "  ";
            if (report.Faces.Count > 1)
            {
                output.WriteLine($"  font {f.Index}:");
            }

            output.WriteLine($"{indent}name: {face.FullName} ({face.PostScriptName})");
            output.WriteLine($"{indent}family: {face.FamilyName}, style: {face.SubfamilyName}");
            foreach ((string name, string value) in f.Names)
            {
                if (name is "version" or "copyright" or "designer" or "manufacturer" or "license" or "licenseUrl")
                {
                    string shown = value.ReplaceLineEndings(" ");
                    output.WriteLine($"{indent}{name}: {(shown.Length > 120 ? shown[..117] + "..." : shown)}");
                }
            }

            string color = face.ColorFormats == ColorGlyphFormats.None ? string.Empty : $", color {face.ColorFormats}";
            output.WriteLine($"{indent}outlines: {f.Outlines}{color}; {face.GlyphCount:N0} glyphs; {face.UnitsPerEm} units per em");
            if (face.IsVariable)
            {
                output.WriteLine($"{indent}axes: {string.Join(", ", face.VariationAxes.Select(a => $"{a.Tag} {Number(a.Min)}-{Number(a.Max)} (default {Number(a.Default)})"))}; {face.NamedInstances.Count} named instances");
            }

            output.WriteLine($"{indent}embedding: {Embedding(face.Embedding)}");
            if (f.Tables.Count > 0)
            {
                output.WriteLine($"{indent}tables ({f.Tables.Count}): {string.Join(" ", f.Tables.Select(t => t.Tag.TrimEnd()))}");
            }

            output.WriteLine($"{indent}characters: {f.Codes.Count:N0} in {f.Blocks.Count} blocks");
            foreach ((string name, int first, int last, int covered) in f.Blocks)
            {
                output.WriteLine(first < 0 ? $"{indent}  {name}: {covered:N0}" : $"{indent}  {name} (U+{first:X4}-{last:X4}): {covered:N0} of {last - first + 1:N0}");
            }
        }
    }

    private static void WriteJson(Utf8JsonWriter writer, Report report)
    {
        writer.WriteStartObject();
        writer.WriteString("path", report.Path);
        writer.WriteString("container", report.Kind.ToString().ToLowerInvariant());
        writer.WriteNumber("bytes", report.Bytes);
        writer.WriteStartArray("faces");
        foreach (FaceReport f in report.Faces)
        {
            Typeface face = f.Face;
            writer.WriteStartObject();
            writer.WriteNumber("index", f.Index);
            writer.WriteString("family", face.FamilyName);
            writer.WriteString("subfamily", face.SubfamilyName);
            writer.WriteString("fullName", face.FullName);
            writer.WriteString("postScriptName", face.PostScriptName);
            writer.WriteStartObject("names");
            foreach ((string name, string value) in f.Names)
            {
                writer.WriteString(name, value);
            }

            writer.WriteEndObject();
            writer.WriteString("outlines", f.Outlines);
            writer.WriteStartArray("color");
            foreach (ColorGlyphFormats format in Enum.GetValues<ColorGlyphFormats>())
            {
                if (format != ColorGlyphFormats.None && face.ColorFormats.HasFlag(format))
                {
                    writer.WriteStringValue(format.ToString());
                }
            }

            writer.WriteEndArray();
            writer.WriteNumber("glyphs", face.GlyphCount);
            writer.WriteNumber("unitsPerEm", face.UnitsPerEm);
            writer.WriteBoolean("variable", face.IsVariable);
            writer.WriteStartArray("axes");
            foreach (VariationAxis axis in face.VariationAxes)
            {
                writer.WriteStartObject();
                writer.WriteString("tag", axis.Tag.ToString());
                writer.WriteString("name", axis.Name);
                writer.WriteNumber("min", axis.Min);
                writer.WriteNumber("default", axis.Default);
                writer.WriteNumber("max", axis.Max);
                writer.WriteBoolean("hidden", axis.IsHidden);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("namedInstances");
            foreach (NamedInstance instance in face.NamedInstances)
            {
                writer.WriteStringValue(instance.Name);
            }

            writer.WriteEndArray();
            writer.WriteStartObject("embedding");
            writer.WriteString("usage", Embedding(face.Embedding).Split(',')[0]);
            writer.WriteNumber("fsType", (int)face.Embedding);
            writer.WriteBoolean("noSubsetting", face.Embedding.HasFlag(FontEmbedding.NoSubsetting));
            writer.WriteBoolean("bitmapOnly", face.Embedding.HasFlag(FontEmbedding.BitmapOnly));
            writer.WriteEndObject();
            writer.WriteStartArray("tables");
            foreach ((string tag, int length) in f.Tables)
            {
                writer.WriteStartObject();
                writer.WriteString("tag", tag);
                writer.WriteNumber("bytes", length);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteNumber("characters", f.Codes.Count);
            writer.WriteStartArray("blocks");
            foreach ((string name, int first, int last, int covered) in f.Blocks)
            {
                writer.WriteStartObject();
                writer.WriteString("name", name);
                if (first >= 0)
                {
                    writer.WriteString("first", $"U+{first:X4}");
                    writer.WriteString("last", $"U+{last:X4}");
                    writer.WriteNumber("size", last - first + 1);
                }

                writer.WriteNumber("covered", covered);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteString("unicodeRange", UnicodeRange.Write(f.Codes));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private sealed record Report(string Path, FontFileKind Kind, int Bytes, List<FaceReport> Faces);

    private sealed record FaceReport(
        int Index,
        Typeface Face,
        List<(string Name, string Value)> Names,
        string Outlines,
        List<(string Tag, int Length)> Tables,
        List<int> Codes,
        List<(string Name, int First, int Last, int Covered)> Blocks);
}
