using System.Text;

using Egami.Fonts.Layout.WordBreaking;

namespace Egami.Fonts.Layout;

/// <summary>
/// One script's word list, for breaking text written without spaces between words (Thai, Lao, Khmer, Myanmar) into words.
/// </summary>
/// <remarks>
/// <para>The words are kept as a minimised word graph: Egami's own compiled form (<c>.edict</c>), which <see cref="Load"/>
/// reads back by inflating one byte array, with nothing to build. Words compare exactly, UTF-16 code unit by code unit: no case
/// folding or normalization. A dictionary holds words spelled with at most 255 distinct code units, which a script's block
/// and a few joiners fit.</para>
/// <para>A dictionary is immutable and safe to share between threads. <see cref="WordDictionaries.BuiltIn"/> holds the
/// dictionaries Egami.Fonts ships.</para>
/// </remarks>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="word-dictionaries"/>
/// </example>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "A dictionary of words, as in HyphenationDictionaries; the name was chosen in the 2.8.5 plan (Q5).")]
public sealed class WordDictionary
{
    private WordDictionary(WordGraph graph) => Graph = graph;

    /// <summary>Gets the number of words.</summary>
    public int Count => Graph.Count;

    /// <summary>Gets the graph the segmenter walks.</summary>
    internal WordGraph Graph { get; }

    /// <summary>Makes a dictionary of <paramref name="words"/>, in any order; duplicates count once.</summary>
    /// <param name="words">The words.</param>
    /// <returns>The dictionary.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="words"/> is null.</exception>
    /// <exception cref="ArgumentException">A word is null or empty, or the words use more than 255 distinct UTF-16 code
    /// units.</exception>
    public static WordDictionary FromWords(IEnumerable<string> words) => new(WordGraph.Build(words));

    /// <summary>
    /// Reads a dictionary from <paramref name="stream"/>: the compiled form <see cref="Save"/> writes, or a UTF-8 word list with
    /// one word per line, as ICU's dictionary sources are. In a word list, lines whose first non-space character is <c>#</c>
    /// are comments, blank lines are skipped, a byte order mark is ignored and each word is trimmed of white space.
    /// </summary>
    /// <param name="stream">The file, read to its end; the stream is left open.</param>
    /// <returns>The dictionary.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="InvalidDataException">The compiled form is damaged.</exception>
    /// <exception cref="ArgumentException">A word list uses more than 255 distinct UTF-16 code units.</exception>
    public static WordDictionary Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using MemoryStream copy = new();
        stream.CopyTo(copy);
        byte[] data = copy.GetBuffer();
        int length = (int)copy.Length;
        if (WordGraph.HasSignature(data.AsSpan(0, length)))
        {
            copy.Position = 0;
            return new WordDictionary(WordGraph.Read(copy, trusted: false));
        }

        string text = Encoding.UTF8.GetString(data, 0, length);
        return FromWords(ListWords(text));
    }

    /// <summary>Writes the dictionary in Egami's compiled form (<c>.edict</c>), which <see cref="Load"/> reads back.</summary>
    /// <param name="stream">The stream to write to; it is left open.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    public void Save(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Graph.Write(stream);
    }

    /// <summary>Gets whether <paramref name="word"/> is in the dictionary, exactly as spelled.</summary>
    /// <param name="word">The word.</param>
    /// <returns>Whether it is; never for an empty span.</returns>
    public bool Contains(ReadOnlySpan<char> word) => Graph.Contains(word);

    /// <summary>Returns a dictionary with this one's words and <paramref name="words"/>, such as names and technical terms a
    /// text uses.</summary>
    /// <param name="words">The words to add; ones already in the dictionary change nothing.</param>
    /// <returns>The new dictionary; this one is unchanged.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="words"/> is null.</exception>
    /// <exception cref="ArgumentException">A word is null or empty, or the words together use more than 255 distinct UTF-16
    /// code units.</exception>
    public WordDictionary WithWords(IEnumerable<string> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        return FromWords(Graph.Words().Concat(words));
    }

    /// <summary>Finds every word that <paramref name="text"/> starts with and writes their lengths, shortest first.</summary>
    /// <param name="text">The text from the position to look at.</param>
    /// <param name="lengths">Receives the lengths in UTF-16 code units; the search stops when it is full.</param>
    /// <returns>The number of lengths written.</returns>
    internal int MatchPrefixes(ReadOnlySpan<char> text, Span<int> lengths) => Graph.MatchPrefixes(text, lengths);

    /// <summary>Lists the words, in ordinal order.</summary>
    internal IEnumerable<string> Words() => Graph.Words();

    /// <summary>Reads a compiled dictionary shipped with Egami.Fonts, skipping the checks untrusted files get.</summary>
    internal static WordDictionary LoadTrusted(Stream stream) => new(WordGraph.Read(stream, trusted: true));

    private static IEnumerable<string> ListWords(string text)
    {
        foreach (string line in text.TrimStart('﻿').Split('\n'))
        {
            string word = line.Trim();
            if (word.Length > 0 && word[0] != '#')
            {
                yield return word;
            }
        }
    }
}
