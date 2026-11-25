#:project ../../src/Egami.Fonts/Egami.Fonts.csproj
#:property Configuration=Release

// Compiles word lists to Egami's .edict form, for tools/make-word-dictionaries.py, which downloads and checks the sources and
// writes the cleaned lists. Usage: dotnet run tools/word-breaking/compile-edict.cs -- <job.json>
//
// The job is a JSON array of { "words" (a UTF-8 file, one word per line), "output" }. Prints each output's word count and size.
using System.Text.Json;
using Egami.Fonts.Layout;

using JsonDocument job = JsonDocument.Parse(File.ReadAllBytes(args[0]));
foreach (JsonElement entry in job.RootElement.EnumerateArray())
{
    string[] words = File.ReadAllLines(entry.GetProperty("words").GetString()!);
    WordDictionary dictionary = WordDictionary.FromWords(words);
    string output = entry.GetProperty("output").GetString()!;
    using (FileStream file = File.Create(output))
    {
        dictionary.Save(file);
    }

    // Check the file reads back to the same words.
    WordDictionary back;
    using (FileStream file = File.OpenRead(output))
    {
        back = WordDictionary.Load(file);
    }

    foreach (string word in words)
    {
        if (!back.Contains(word))
        {
            throw new InvalidOperationException($"{output} lost the word '{word}'.");
        }
    }

    if (back.Count != words.Length)
    {
        throw new InvalidOperationException($"{output} holds {back.Count} words, not {words.Length}.");
    }

    Console.WriteLine($"{output}: {back.Count} words, {new FileInfo(output).Length} bytes");
}
