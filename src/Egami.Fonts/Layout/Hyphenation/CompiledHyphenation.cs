using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace Egami.Fonts.Layout.Hyphenation;

/// <summary>
/// Egami's compiled hyphenation form (<c>.ehyp</c>), little-endian throughout:
/// <code>
/// "EHYP" u16 version (1) u8 minBefore u8 minAfter
/// u16 metadata count, then per entry: string key, string value
/// i32 states, i32 edges, i32 pool bytes
/// i32[states + 1] edge starts, u16[edges] edge characters, i32[edges] edge targets
/// i32[states] value starts, u8[states] value lengths, u8[pool] values
/// i32 exception count, then per exception: string word, u16 point count, u16[count] points
/// u16 no-hyphen count, then per entry: string
/// </code>
/// A string is a u16 count of UTF-16 code units and the units.
/// </summary>
internal static class CompiledHyphenation
{
    private const ushort Version = 1;

    private static ReadOnlySpan<byte> Signature => "EHYP"u8;

    public static bool HasSignature(ReadOnlySpan<byte> data) => data.StartsWith(Signature);

    public static void Write(Hyphenator hyphenator, Stream output)
    {
        PatternTrie trie = hyphenator.Trie;
        using BinaryWriter w = new(output, Encoding.UTF8, leaveOpen: true);
        w.Write(Signature);
        w.Write(Version);
        w.Write((byte)hyphenator.MinBefore);
        w.Write((byte)hyphenator.MinAfter);
        w.Write(checked((ushort)hyphenator.Metadata.Count));
        foreach ((string key, string value) in hyphenator.Metadata)
        {
            WriteString(w, key);
            WriteString(w, value);
        }

        w.Write(trie.StateCount);
        w.Write(trie.EdgeChars.Length);
        w.Write(trie.ValuePool.Length);
        foreach (int v in trie.EdgeStart)
        {
            w.Write(v);
        }

        foreach (char c in trie.EdgeChars)
        {
            w.Write((ushort)c);
        }

        foreach (int v in trie.EdgeTargets)
        {
            w.Write(v);
        }

        foreach (int v in trie.ValueStart)
        {
            w.Write(v);
        }

        w.Write(trie.ValueLength);
        w.Write(trie.ValuePool);

        // Sorted, so the same hyphenator always writes the same bytes.
        w.Write(hyphenator.Exceptions.Count);
        foreach ((string word, int[] points) in hyphenator.Exceptions.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            WriteString(w, word);
            w.Write((ushort)points.Length);
            foreach (int p in points)
            {
                w.Write((ushort)p);
            }
        }

        w.Write(checked((ushort)hyphenator.NoHyphen.Count));
        foreach (string s in hyphenator.NoHyphen)
        {
            WriteString(w, s);
        }
    }

    private static void WriteString(BinaryWriter w, string s)
    {
        w.Write(checked((ushort)s.Length));
        foreach (char c in s)
        {
            w.Write((ushort)c);
        }
    }

    public static Hyphenator Read(byte[] data)
    {
        Reader r = new(data);
        if (!HasSignature(data))
        {
            throw new InvalidDataException("The data is not a compiled hyphenation file.");
        }

        r.Skip(4);
        ushort version = r.U16();
        if (version != Version)
        {
            throw new InvalidDataException($"Compiled hyphenation version {version} is not supported.");
        }

        int minBefore = r.U8();
        int minAfter = r.U8();
        if (minBefore == 0 || minAfter == 0)
        {
            throw new InvalidDataException("A hyphenation minimum is 0.");
        }

        int metadataCount = r.U16();
        List<KeyValuePair<string, string>> metadata = new(Math.Min(metadataCount, 64));
        for (int i = 0; i < metadataCount; i++)
        {
            metadata.Add(new(r.String(), r.String()));
        }

        int states = r.Count(9);
        int edges = r.Count(6);
        int pool = r.Count(1);
        int[] edgeStart = r.I32s(states + 1);
        char[] edgeChars = r.Chars(edges);

        int[] edgeTargets = r.I32s(edges);
        int[] valueStart = r.I32s(states);
        byte[] valueLength = r.Bytes(states);
        byte[] valuePool = r.Bytes(pool);
        PatternTrie trie = PatternTrie.Validated(edgeStart, edgeChars, edgeTargets, valueStart, valueLength, valuePool);

        int exceptionCount = r.Count(4);
        Dictionary<string, int[]> exceptions = new(exceptionCount, StringComparer.Ordinal);
        for (int i = 0; i < exceptionCount; i++)
        {
            string word = r.String();
            int[] points = new int[r.U16()];
            for (int k = 0; k < points.Length; k++)
            {
                points[k] = r.U16();
                if (points[k] <= (k == 0 ? 0 : points[k - 1]) || points[k] >= word.Length)
                {
                    throw new InvalidDataException("A hyphenation exception has a point out of order.");
                }
            }

            if (word.Length == 0 || !exceptions.TryAdd(word, points))
            {
                throw new InvalidDataException("A hyphenation exception is empty or repeated.");
            }
        }

        int noHyphenCount = r.U16();
        string[] noHyphen = new string[noHyphenCount];
        for (int i = 0; i < noHyphenCount; i++)
        {
            noHyphen[i] = r.String();
            if (noHyphen[i].Length == 0)
            {
                throw new InvalidDataException("A no-hyphen entry is empty.");
            }
        }

        return new Hyphenator(trie, exceptions, noHyphen, minBefore, minAfter, metadata);
    }

    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _at;

        private ReadOnlySpan<byte> Take(int length)
        {
            if (length < 0 || _data.Length - _at < length)
            {
                throw new InvalidDataException("The compiled hyphenation data is cut short.");
            }

            ReadOnlySpan<byte> span = _data.Slice(_at, length);
            _at += length;
            return span;
        }

        public void Skip(int length) => Take(length);

        public byte U8() => Take(1)[0];

        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

        public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

        // A count whose entries take at least bytesEach bytes, so a bad count fails before a large allocation.
        public int Count(int bytesEach)
        {
            int count = I32();
            if (count < 0 || count > (_data.Length - _at) / bytesEach)
            {
                throw new InvalidDataException("A count in the compiled hyphenation data is too large.");
            }

            return count;
        }

        public int[] I32s(int count)
        {
            int[] values = MemoryMarshal.Cast<byte, int>(Take(checked(count * 4))).ToArray();
            if (!BitConverter.IsLittleEndian)
            {
                BinaryPrimitives.ReverseEndianness(values, values);
            }

            return values;
        }

        public char[] Chars(int count)
        {
            char[] values = MemoryMarshal.Cast<byte, char>(Take(checked(count * 2))).ToArray();
            if (!BitConverter.IsLittleEndian)
            {
                BinaryPrimitives.ReverseEndianness(MemoryMarshal.Cast<char, ushort>(values.AsSpan()), MemoryMarshal.Cast<char, ushort>(values.AsSpan()));
            }

            return values;
        }

        public byte[] Bytes(int count) => Take(count).ToArray();

        public string String()
        {
            return new string(Chars(U16()));
        }
    }
}
