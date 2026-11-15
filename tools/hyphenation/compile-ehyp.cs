#:project ../../src/Egami.Fonts/Egami.Fonts.csproj
#:property Configuration=Release

// Compiles hyph-utf8 TeX pattern files to Egami's .ehyp form, for tools/make-hyphenation-patterns.py, which writes the job
// file and checks the sources. Usage: dotnet run tools/hyphenation/compile-ehyp.cs -- <job.json>
//
// The job is a JSON array of { "tag", "patterns", "exceptions" (a file whose \hyphenation block is added, or null), "output",
// "metadata": { name: value } }.
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Egami.Fonts.Layout;

using JsonDocument job = JsonDocument.Parse(File.ReadAllBytes(args[0]));
foreach (JsonElement entry in job.RootElement.EnumerateArray())
{
    string tag = entry.GetProperty("tag").GetString()!;
    Hyphenator hyphenator;
    using (FileStream patterns = File.OpenRead(entry.GetProperty("patterns").GetString()!))
    {
        hyphenator = Hyphenator.Load(patterns, HyphenationFormat.TeX);
    }

    if (entry.GetProperty("exceptions").GetString() is { } exceptionsFile)
    {
        hyphenator = hyphenator.WithExceptions(ExceptionWords(File.ReadAllText(exceptionsFile)));
    }

    List<KeyValuePair<string, string>> metadata = [];
    foreach (JsonProperty p in entry.GetProperty("metadata").EnumerateObject())
    {
        metadata.Add(new(p.Name, p.Value.GetString()!));
    }

    // The metadata travels in the compiled form; setting it is internal to Egami.Fonts.
    MethodInfo withMetadata = typeof(Hyphenator).GetMethod("WithMetadata", BindingFlags.NonPublic | BindingFlags.Instance)!;
    hyphenator = (Hyphenator)withMetadata.Invoke(hyphenator, [metadata])!;

    string output = entry.GetProperty("output").GetString()!;
    using (FileStream file = File.Create(output))
    {
        hyphenator.Save(file);
    }

    Console.WriteLine($"{tag}: {new FileInfo(output).Length} bytes, minimums {hyphenator.MinBefore}/{hyphenator.MinAfter}");
}

// The words of a file's \hyphenation{...} block, with TeX comments left out.
static IEnumerable<string> ExceptionWords(string text)
{
    string body = Regex.Replace(text, "%.*", "");
    Match m = Regex.Match(body, @"\\hyphenation\s*\{([^}]*)\}");
    return m.Success ? m.Groups[1].Value.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries) : [];
}
