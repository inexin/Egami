using System.IO.Compression;
using System.Runtime.CompilerServices;

namespace Egami.Fonts.Layout.WordBreaking;

/// <summary>
/// A word list as a minimised acyclic word graph (a DAFSA), in the compact form <see cref="WordDictionary"/> walks without
/// unpacking it, and Egami's file form of it (<c>.edict</c>).
/// </summary>
/// <remarks>
/// <para>Words are spelled in symbols: a dictionary has an alphabet of at most 255 UTF-16 code units, ascending, and symbol
/// <c>i + 1</c> is the code unit at index <c>i</c> (0 is no symbol). For Thai, Lao, Khmer and Myanmar the alphabet is the
/// script's block plus a few joiners.</para>
/// <para>The graph is a byte array of nodes, each a run of its outgoing edges in ascending symbol order. An edge is its symbol
/// (<c>u8</c>) and an unsigned LEB128 varint <c>t &lt;&lt; 2 | last &lt;&lt; 1 | final</c>: <c>final</c> when a word ends
/// after the edge, <c>last</c> on the node's last edge, and <c>t</c> 0 when the edge leads to a node without edges, else
/// 1 + the distance from the end of the edge to the target node. Every target lies after its edge, so a walk only moves
/// forward. The root node is at offset 0; an empty graph is an empty dictionary.</para>
/// <para>The file is <c>"EDCT"</c>, <c>u8</c> version (1), then a raw Deflate stream of varints: the word count, the longest
/// word's length in code units, the alphabet size and its code units (the first, then the gap from the one before), the
/// graph's length, and the graph.</para>
/// </remarks>
internal sealed class WordGraph
{
    private const byte Version = 1;
    private const int MaxGraphLength = 1 << 28;

    private readonly byte[] _graph;
    private readonly char[] _alphabet;
    private readonly char _low;
    private readonly byte[] _lowSymbols = new byte[256];

    private WordGraph(byte[] graph, char[] alphabet, int count, int maxLength)
    {
        _graph = graph;
        _alphabet = alphabet;
        Count = count;
        MaxLength = maxLength;

        // Most of a script's alphabet lies in one block: map the 256 code units from the start of the 128-unit block holding
        // the most of it through a table, and search the rest.
        int best = 0, bestCount = -1;
        for (int i = 0; i < alphabet.Length; i++)
        {
            int block = alphabet[i] & ~0x7F;
            int n = 0;
            foreach (char c in alphabet)
            {
                n += (uint)(c - block) < 256 ? 1 : 0;
            }

            if (n > bestCount)
            {
                (best, bestCount) = (block, n);
            }
        }

        _low = (char)best;
        for (int i = 0; i < alphabet.Length; i++)
        {
            uint d = (uint)(alphabet[i] - best);
            if (d < 256)
            {
                _lowSymbols[d] = (byte)(i + 1);
            }
        }
    }

    public static ReadOnlySpan<byte> Signature => "EDCT"u8;

    /// <summary>Gets the number of words.</summary>
    public int Count { get; }

    /// <summary>Gets the longest word's length in UTF-16 code units; 0 for an empty graph.</summary>
    public int MaxLength { get; }

    /// <summary>Gets the size of the graph in memory, in bytes, for tests and measurements.</summary>
    internal int GraphLength => _graph.Length;

    /// <summary>Builds the graph of <paramref name="words"/>: any order, duplicates allowed, none empty.</summary>
    /// <exception cref="ArgumentException">A word is empty or null, or the words use more than 255 distinct code units.</exception>
    public static WordGraph Build(IEnumerable<string> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        List<string> list = [];
        foreach (string word in words)
        {
            if (string.IsNullOrEmpty(word))
            {
                throw new ArgumentException("A word is null or empty.", nameof(words));
            }

            list.Add(word);
        }

        list.Sort(StringComparer.Ordinal);
        SortedSet<char> units = [];
        foreach (string word in list)
        {
            foreach (char c in word)
            {
                units.Add(c);
            }
        }

        if (units.Count > 255)
        {
            throw new ArgumentException($"The words use {units.Count} distinct UTF-16 code units; a dictionary holds at most 255.", nameof(words));
        }

        char[] alphabet = [.. units];
        WordGraphBuilder builder = new();
        string? previous = null;
        int count = 0, maxLength = 0;
        byte[] symbols = [];
        foreach (string word in list)
        {
            if (word == previous)
            {
                continue;
            }

            if (symbols.Length < word.Length)
            {
                symbols = new byte[word.Length * 2];
            }

            for (int i = 0; i < word.Length; i++)
            {
                symbols[i] = (byte)(Array.BinarySearch(alphabet, word[i]) + 1);
            }

            builder.Add(symbols.AsSpan(0, word.Length));
            previous = word;
            count++;
            maxLength = Math.Max(maxLength, word.Length);
        }

        return new WordGraph(builder.Finish(), alphabet, count, maxLength);
    }

    /// <summary>Gets whether <paramref name="data"/> starts like the file form.</summary>
    public static bool HasSignature(ReadOnlySpan<byte> data) => data.StartsWith(Signature);

    /// <summary>Reads the file form from <paramref name="stream"/>'s position.</summary>
    /// <param name="stream">The file; the stream is left open.</param>
    /// <param name="trusted">True for the built-in dictionaries, which skip the graph's checks.</param>
    /// <exception cref="InvalidDataException">The data is not a valid file.</exception>
    public static WordGraph Read(Stream stream, bool trusted)
    {
        Span<byte> head = stackalloc byte[5];
        if (stream.ReadAtLeast(head, 5, throwOnEndOfStream: false) < 5 || !head.StartsWith(Signature))
        {
            throw new InvalidDataException("Not an Egami word dictionary (.edict).");
        }

        if (head[4] != Version)
        {
            throw new InvalidDataException($"Word dictionary version {head[4]} is not supported.");
        }

        using DeflateStream inflate = new(stream, CompressionMode.Decompress, leaveOpen: true);
        try
        {
            return ReadBody(inflate, trusted);
        }
        catch (EndOfStreamException e)
        {
            throw new InvalidDataException("The word dictionary is cut short.", e);
        }
    }

    private static WordGraph ReadBody(Stream inflate, bool trusted)
    {
        int count = ReadVarint(inflate);
        int maxLength = ReadVarint(inflate);
        int alphabetSize = ReadVarint(inflate);
        if (alphabetSize > 255)
        {
            throw new InvalidDataException("The word dictionary's alphabet has more than 255 entries.");
        }

        char[] alphabet = new char[alphabetSize];
        int unit = -1;
        for (int i = 0; i < alphabetSize; i++)
        {
            int gap = ReadVarint(inflate);
            unit = i == 0 ? gap : unit + gap;
            if ((i > 0 && gap == 0) || unit > char.MaxValue)
            {
                throw new InvalidDataException("The word dictionary's alphabet is not ascending UTF-16 code units.");
            }

            alphabet[i] = (char)unit;
        }

        int length = ReadVarint(inflate);
        if (length > MaxGraphLength)
        {
            throw new InvalidDataException("The word dictionary's graph is too large.");
        }

        byte[] graph = GC.AllocateUninitializedArray<byte>(length);
        inflate.ReadExactly(graph);
        if (inflate.ReadByte() >= 0)
        {
            throw new InvalidDataException("The word dictionary has data after its graph.");
        }

        if (!trusted)
        {
            Validate(graph, alphabetSize, count, maxLength);
        }

        return new WordGraph(graph, alphabet, count, maxLength);
    }

    /// <summary>Writes the file form.</summary>
    public void Write(Stream output)
    {
        output.Write(Signature);
        output.WriteByte(Version);
        using DeflateStream deflate = new(output, CompressionLevel.SmallestSize, leaveOpen: true);
        WriteVarint(deflate, Count);
        WriteVarint(deflate, MaxLength);
        WriteVarint(deflate, _alphabet.Length);
        for (int i = 0; i < _alphabet.Length; i++)
        {
            WriteVarint(deflate, i == 0 ? _alphabet[0] : _alphabet[i] - _alphabet[i - 1]);
        }

        WriteVarint(deflate, _graph.Length);
        deflate.Write(_graph);
    }

    /// <summary>Gets whether <paramref name="word"/> is in the graph, exactly as spelled.</summary>
    public bool Contains(ReadOnlySpan<char> word)
    {
        if (word.IsEmpty || word.Length > MaxLength)
        {
            return false;
        }

        int node = 0;
        bool final = false;
        foreach (char c in word)
        {
            if (node < 0 || !Step(ref node, Symbol(c), out final))
            {
                return false;
            }
        }

        return final;
    }

    /// <summary>
    /// Finds every word that <paramref name="text"/> starts with: writes their lengths in UTF-16 code units to
    /// <paramref name="lengths"/>, shortest first, and stops when it is full.
    /// </summary>
    /// <returns>The number of lengths written.</returns>
    public int MatchPrefixes(ReadOnlySpan<char> text, Span<int> lengths)
    {
        int found = 0;
        int node = _graph.Length == 0 ? -1 : 0;
        int end = Math.Min(text.Length, MaxLength);
        for (int i = 0; i < end && node >= 0 && found < lengths.Length; i++)
        {
            if (!Step(ref node, Symbol(text[i]), out bool final))
            {
                break;
            }

            if (final)
            {
                lengths[found++] = i + 1;
            }
        }

        return found;
    }

    /// <summary>Lists every word, in ordinal order.</summary>
    public IEnumerable<string> Words()
    {
        if (_graph.Length == 0)
        {
            yield break;
        }

        // Depth first, keeping per depth the offset of the next edge to take (-1 once the node is done); the word so far is
        // as long as the stack is deep.
        char[] word = new char[MaxLength];
        Stack<int> next = new();
        next.Push(0);
        while (next.Count > 0)
        {
            int at = next.Pop();
            if (at < 0)
            {
                continue;
            }

            int depth = next.Count;
            int pos = at + 1;
            uint v = ReadVarint(_graph, ref pos);
            next.Push((v & 2) != 0 ? -1 : pos);
            word[depth] = _alphabet[_graph[at] - 1];
            if ((v & 1) != 0)
            {
                yield return new string(word, 0, depth + 1);
            }

            uint t = v >> 2;
            if (t != 0)
            {
                next.Push(pos + (int)t - 1);
            }
        }
    }

    /// <summary>The symbol of a code unit; 0 when it is not in the alphabet.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Symbol(char c)
    {
        uint d = (uint)(c - _low);
        if (d < 256)
        {
            return _lowSymbols[d];
        }

        int i = Array.BinarySearch(_alphabet, c);
        return i >= 0 ? i + 1 : 0;
    }

    /// <summary>Follows the edge of <paramref name="symbol"/> from <paramref name="node"/>: on success, <paramref name="node"/>
    /// becomes the target (-1 for one without edges).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Step(ref int node, int symbol, out bool final)
    {
        final = false;
        if (symbol == 0)
        {
            return false;
        }

        byte[] graph = _graph;
        int pos = node;
        while (true)
        {
            int label = graph[pos++];
            uint v = ReadVarint(graph, ref pos);
            if (label == symbol)
            {
                final = (v & 1) != 0;
                uint t = v >> 2;
                node = t == 0 ? -1 : pos + (int)t - 1;
                return true;
            }

            if (label > symbol || (v & 2) != 0)
            {
                return false;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ReadVarint(byte[] data, ref int pos)
    {
        uint b = data[pos++];
        if (b < 0x80)
        {
            return b;
        }

        uint value = b & 0x7F;
        int shift = 7;
        do
        {
            b = data[pos++];
            value |= (b & 0x7F) << shift;
            shift += 7;
        }
        while (b >= 0x80);
        return value;
    }

    /// <summary>Checks an untrusted graph: edges in bounds and ascending, every target a node after its edge, and the stored
    /// word count and longest length right.</summary>
    private static void Validate(byte[] graph, int alphabetSize, int count, int maxLength)
    {
        static InvalidDataException Bad() => new("The word dictionary's graph is damaged.");

        int nodes = 0;
        List<int> starts = [];
        bool atStart = true;
        int lastLabel = 0;
        int pos = 0;
        while (pos < graph.Length)
        {
            if (atStart)
            {
                starts.Add(pos);
                lastLabel = 0;
                atStart = false;
                nodes++;
            }

            int label = graph[pos++];
            if (label == 0 || label > alphabetSize || label <= lastLabel)
            {
                throw Bad();
            }

            lastLabel = label;
            ulong v = 0;
            for (int shift = 0; ; shift += 7)
            {
                if (pos >= graph.Length || shift > 28)
                {
                    throw Bad();
                }

                byte b = graph[pos++];
                v |= (ulong)(b & 0x7F) << shift;
                if (b < 0x80)
                {
                    break;
                }
            }

            ulong t = v >> 2;
            if ((t == 0 && (v & 1) == 0) || (t != 0 && (ulong)pos + t - 1 >= (ulong)graph.Length))
            {
                throw Bad();
            }

            atStart = (v & 2) != 0;
        }

        if (!atStart && graph.Length > 0)
        {
            throw Bad();
        }

        // Words and longest word from each node, from the last node back: every target lies after its edge.
        int[] startArray = [.. starts];
        long[] words = new long[nodes];
        int[] longest = new int[nodes];
        for (int n = nodes - 1; n >= 0; n--)
        {
            long w = 0;
            int l = 0;
            pos = startArray[n];
            while (true)
            {
                pos++;
                uint v = ReadVarint(graph, ref pos);
                uint t = v >> 2;
                long below = 0;
                int deeper = 0;
                if (t != 0)
                {
                    int target = Array.BinarySearch(startArray, pos + (int)t - 1);
                    if (target < 0)
                    {
                        throw Bad();
                    }

                    below = words[target];
                    deeper = longest[target];
                }

                w += below + (v & 1);
                l = Math.Max(l, deeper + 1);
                if (w > int.MaxValue)
                {
                    throw Bad();
                }

                if ((v & 2) != 0)
                {
                    break;
                }
            }

            words[n] = w;
            longest[n] = l;
        }

        if (count != (nodes == 0 ? 0 : words[0]) || maxLength != (nodes == 0 ? 0 : longest[0]))
        {
            throw Bad();
        }
    }

    private static int ReadVarint(Stream stream)
    {
        int value = 0;
        for (int shift = 0; shift < 35; shift += 7)
        {
            int b = stream.ReadByte();
            if (b < 0)
            {
                throw new EndOfStreamException();
            }

            value |= (b & 0x7F) << shift;
            if (b < 0x80)
            {
                if (value < 0)
                {
                    break;
                }

                return value;
            }
        }

        throw new InvalidDataException("The word dictionary has a bad number.");
    }

    private static void WriteVarint(Stream stream, int value)
    {
        uint v = (uint)value;
        while (v >= 0x80)
        {
            stream.WriteByte((byte)(v | 0x80));
            v >>= 7;
        }

        stream.WriteByte((byte)v);
    }
}
