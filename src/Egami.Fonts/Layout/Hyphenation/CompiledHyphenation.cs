using System.Buffers.Binary;
using System.Text;

namespace Egami.Fonts.Layout.Hyphenation;

/// <summary>
/// Egami's compiled hyphenation form (<c>.ehyp</c>). After a fixed header, numbers are unsigned LEB128 varints (<c>v</c>):
/// <code>
/// "EHYP" u16 version (2, little-endian) u8 minBefore u8 minAfter
/// v metadata count, then per entry: string key, string value
/// v states, v alphabet size, then the alphabet's UTF-16 code units in ascending order: v the first, then v the gap from the
///     one before
/// v[states] child counts, in breadth-first state order
/// [states - 1] edge characters as alphabet indexes: u8 each when the alphabet has at most 256 entries, else u16 little-endian
/// v value strings, then per string: u8 length, u8[length] values
/// v[states] value string of each state, 1-based; 0 for none
/// v exception count, then per exception: string word, v point count, v[count] points as gaps from the one before
/// v no-hyphen count, then per entry: string
/// </code>
/// A string is a v count of UTF-8 bytes and the bytes. States are numbered breadth first, so edge e leads to state e + 1 (see
/// <see cref="PatternTrie"/>) and the file stores no edge targets.
/// </summary>
internal static class CompiledHyphenation
{
    private const ushort Version = 2;

    private static ReadOnlySpan<byte> Signature => "EHYP"u8;

    public static bool HasSignature(ReadOnlySpan<byte> data) => data.StartsWith(Signature);

    public static void Write(Hyphenator hyphenator, Stream output)
    {
        PatternTrie trie = hyphenator.Trie;
        Writer w = new();
        w.Bytes(Signature);
        w.U8((byte)Version);
        w.U8(Version >> 8);
        w.U8((byte)hyphenator.MinBefore);
        w.U8((byte)hyphenator.MinAfter);
        w.V(hyphenator.Metadata.Count);
        foreach ((string key, string value) in hyphenator.Metadata)
        {
            w.String(key);
            w.String(value);
        }

        int states = trie.StateCount;
        w.V(states);
        char[] alphabet = [.. trie.EdgeChars.Distinct().Order()];
        w.V(alphabet.Length);
        for (int i = 0; i < alphabet.Length; i++)
        {
            w.V(i == 0 ? alphabet[0] : alphabet[i] - alphabet[i - 1]);
        }

        for (int s = 0; s < states; s++)
        {
            w.V(trie.EdgeStart[s + 1] - trie.EdgeStart[s]);
        }

        bool wide = alphabet.Length > 256;
        foreach (char c in trie.EdgeChars)
        {
            int index = Array.BinarySearch(alphabet, c);
            w.U8((byte)index);
            if (wide)
            {
                w.U8((byte)(index >> 8));
            }
        }

        w.V(trie.ValueCount);
        for (int v = 1; v <= trie.ValueCount; v++)
        {
            int from = trie.ValueOffset[v - 1];
            w.U8((byte)(trie.ValueOffset[v] - from));
            w.Bytes(trie.ValuePool.AsSpan(from, trie.ValueOffset[v] - from));
        }

        foreach (ushort id in trie.ValueId)
        {
            w.V(id);
        }

        // Sorted, so the same hyphenator always writes the same bytes.
        w.V(hyphenator.Exceptions.Count);
        foreach ((string word, int[] points) in hyphenator.Exceptions.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            w.String(word);
            w.V(points.Length);
            for (int k = 0; k < points.Length; k++)
            {
                w.V(points[k] - (k == 0 ? 0 : points[k - 1]));
            }
        }

        w.V(hyphenator.NoHyphen.Count);
        foreach (string s in hyphenator.NoHyphen)
        {
            w.String(s);
        }

        output.Write(w.Written);
    }

    public static Hyphenator Read(byte[] data)
    {
        if (!HasSignature(data))
        {
            throw new InvalidDataException("The data is not a compiled hyphenation file.");
        }

        Reader r = new(data);
        r.Skip(4);
        int version = r.U8() | (r.U8() << 8);
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

        int metadataCount = r.Count();
        List<KeyValuePair<string, string>> metadata = new(Math.Min(metadataCount, 64));
        for (int i = 0; i < metadataCount; i++)
        {
            metadata.Add(new(r.String(), r.String()));
        }

        int states = r.Count();
        if (states == 0)
        {
            throw new InvalidDataException("The hyphenation trie has no states.");
        }

        int alphabetSize = r.Count();
        char[] alphabet = new char[alphabetSize];
        int unit = -1;
        for (int i = 0; i < alphabetSize; i++)
        {
            int gap = r.V();
            unit = i == 0 ? gap : unit + gap;
            if ((i > 0 && gap == 0) || unit > char.MaxValue)
            {
                throw new InvalidDataException("The hyphenation alphabet is out of order.");
            }

            alphabet[i] = (char)unit;
        }

        // Every state but the root has one edge into it, and a child count takes at least one byte.
        int[] edgeStart = new int[r.Fits(states) + 1];
        int edges = 0;
        for (int s = 0; s < states; s++)
        {
            edgeStart[s] = edges;
            edges += r.V();
            if (edges >= states)
            {
                throw new InvalidDataException("The hyphenation trie has more edges than states.");
            }
        }

        edgeStart[states] = edges;
        bool wide = alphabetSize > 256;
        ReadOnlySpan<byte> indexes = r.Take(checked(edges * (wide ? 2 : 1)));
        char[] edgeChars = new char[edges];
        for (int e = 0; e < edges; e++)
        {
            int index = wide ? BinaryPrimitives.ReadUInt16LittleEndian(indexes[(e * 2)..]) : indexes[e];
            if (index >= alphabetSize)
            {
                throw new InvalidDataException("A hyphenation trie edge is outside the alphabet.");
            }

            edgeChars[e] = alphabet[index];
        }

        int valueCount = r.Count();
        if (valueCount > ushort.MaxValue)
        {
            throw new InvalidDataException("The hyphenation trie has too many value strings.");
        }

        int[] valueOffset = new int[valueCount + 1];
        int poolStart = r.Position;
        for (int v = 0; v < valueCount; v++)
        {
            int length = r.U8();
            r.Skip(length);
            valueOffset[v + 1] = valueOffset[v] + length;
        }

        // The pool is the value strings without their length bytes.
        byte[] valuePool = new byte[valueOffset[valueCount]];
        ReadOnlySpan<byte> packed = data.AsSpan(poolStart, r.Position - poolStart);
        for (int v = 0, at = 0; v < valueCount; v++)
        {
            int length = packed[at];
            packed.Slice(at + 1, length).CopyTo(valuePool.AsSpan(valueOffset[v]));
            at += length + 1;
        }

        ushort[] valueId = new ushort[r.Fits(states)];
        for (int s = 0; s < states; s++)
        {
            int id = r.V();
            if (id > valueCount)
            {
                throw new InvalidDataException("A hyphenation trie state names a missing value string.");
            }

            valueId[s] = (ushort)id;
        }

        PatternTrie trie = PatternTrie.Validated(edgeStart, edgeChars, valueId, valueOffset, valuePool);

        int exceptionCount = r.Count();
        Dictionary<string, int[]> exceptions = new(exceptionCount, StringComparer.Ordinal);
        for (int i = 0; i < exceptionCount; i++)
        {
            string word = r.String();
            int[] points = new int[r.Count()];
            for (int k = 0; k < points.Length; k++)
            {
                int gap = r.V();
                points[k] = (k == 0 ? 0 : points[k - 1]) + gap;
                if (gap == 0 || points[k] >= word.Length)
                {
                    throw new InvalidDataException("A hyphenation exception has a point out of order.");
                }
            }

            if (word.Length == 0 || !exceptions.TryAdd(word, points))
            {
                throw new InvalidDataException("A hyphenation exception is empty or repeated.");
            }
        }

        int noHyphenCount = r.Count();
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

    private sealed class Writer
    {
        private byte[] _buffer = new byte[1 << 16];
        private int _length;

        public ReadOnlySpan<byte> Written => _buffer.AsSpan(0, _length);

        public void U8(int value)
        {
            Room(1);
            _buffer[_length++] = (byte)value;
        }

        public void Bytes(ReadOnlySpan<byte> bytes)
        {
            Room(bytes.Length);
            bytes.CopyTo(_buffer.AsSpan(_length));
            _length += bytes.Length;
        }

        public void V(int value)
        {
            uint v = checked((uint)value);
            while (v >= 0x80)
            {
                U8((int)(v | 0x80) & 0xFF);
                v >>= 7;
            }

            U8((int)v);
        }

        public void String(string s)
        {
            int count = Encoding.UTF8.GetByteCount(s);
            V(count);
            Room(count);
            _length += Encoding.UTF8.GetBytes(s, _buffer.AsSpan(_length));
        }

        private void Room(int count)
        {
            if (_buffer.Length - _length < count)
            {
                Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + count));
            }
        }
    }

    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _at;

        public readonly int Position => _at;

        public ReadOnlySpan<byte> Take(int length)
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

        // An unsigned LEB128 number of at most 31 bits.
        public int V()
        {
            int value = 0;
            for (int shift = 0; shift < 35; shift += 7)
            {
                byte b = U8();
                value |= (b & 0x7F) << shift;
                if (b < 0x80)
                {
                    if (shift == 28 && b > 7)
                    {
                        break;
                    }

                    return value;
                }
            }

            throw new InvalidDataException("A number in the compiled hyphenation data is too large.");
        }

        // A count of entries that take at least a byte each, so a bad count fails before a large allocation.
        public int Count() => Fits(V());

        public readonly int Fits(int count) =>
            count <= _data.Length - _at ? count : throw new InvalidDataException("A count in the compiled hyphenation data is too large.");

        public string String() => Encoding.UTF8.GetString(Take(V()));
    }
}
