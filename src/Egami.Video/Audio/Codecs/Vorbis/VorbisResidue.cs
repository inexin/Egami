// Follows the Vorbis I specification (Xiph.Org), section 8 "Residue setup and decode": header decode (8.6.1), packet decode
// (8.6.2) and the residue formats 0, 1 and 2 (8.6.3 to 8.6.5).
using System.Runtime.CompilerServices;

namespace Egami.Video.Audio.Codecs.Vorbis;

/// <summary>A residue configuration from the setup header.</summary>
internal sealed class VorbisResidue
{
    private readonly int _begin;
    private readonly int _end;
    private readonly int _partitionSize;
    private readonly int _classifications;
    private readonly VorbisCodebook _classbook;

    // Books per classification and pass (8 passes), null for none.
    private readonly VorbisCodebook?[] _books;

    // The one vector type 2 decodes as type 1.
    private readonly float[][] _single = new float[1][];

    private static readonly bool[] Decode1 = [false];

    private VorbisResidue(int type, int begin, int end, int partitionSize, int classifications, VorbisCodebook classbook,
        VorbisCodebook?[] books)
    {
        Type = type;
        _begin = begin;
        _end = end;
        _partitionSize = partitionSize;
        _classifications = classifications;
        _classbook = classbook;
        _books = books;
    }

    public int Type { get; }

    public static VorbisResidue Read(ref VorbisBitReader r, VorbisCodebook[] books)
    {
        int type = (int)r.ReadBits(16);
        if (type > 2)
        {
            throw Vorbis.Corrupt($"A residue has the reserved type {type}.");
        }

        int begin = (int)r.ReadBits(24);
        int end = (int)r.ReadBits(24);
        int partitionSize = (int)r.ReadBits(24) + 1;
        int classifications = (int)r.ReadBits(6) + 1;
        int classbook = (int)r.ReadBits(8);
        if (classbook >= books.Length)
        {
            throw Vorbis.Corrupt("A residue names a codebook that does not exist.");
        }

        Span<int> cascade = stackalloc int[classifications];
        for (int i = 0; i < classifications; i++)
        {
            int low = (int)r.ReadBits(3);
            int high = r.ReadBit() ? (int)r.ReadBits(5) : 0;
            cascade[i] = (high << 3) | low;
        }

        VorbisCodebook?[] list = new VorbisCodebook?[classifications * 8];
        for (int i = 0; i < classifications; i++)
        {
            for (int pass = 0; pass < 8; pass++)
            {
                if ((cascade[i] & (1 << pass)) == 0)
                {
                    continue;
                }

                int book = (int)r.ReadBits(8);
                if (book >= books.Length)
                {
                    throw Vorbis.Corrupt("A residue names a codebook that does not exist.");
                }

                if (!books[book].HasLookup)
                {
                    throw Vorbis.Corrupt("A residue codebook has no lookup table.");
                }

                list[(i * 8) + pass] = books[book];
            }
        }

        if (books[classbook].Dimensions == 0)
        {
            throw Vorbis.Corrupt("A residue's classification codebook has no dimensions.");
        }

        return new VorbisResidue(type, begin, end, partitionSize, classifications, books[classbook], list);
    }

    /// <summary>Gets the classification scratch size, per vector, for vectors of <paramref name="size"/> values.</summary>
    public int ClassificationsSize(int size) => (size / _partitionSize) + _classbook.Dimensions;

    /// <summary>
    /// Decodes <paramref name="count"/> vectors of length <paramref name="n"/> into <paramref name="vectors"/> (which the caller
    /// cleared). For type 2, <paramref name="interleaved"/> (count * n values, cleared here) holds the single interleaved vector.
    /// </summary>
    public void Decode(ref VorbisBitReader r, float[][] vectors, ReadOnlySpan<bool> doNotDecode, int count, int n,
        float[] interleaved, int[][] classes)
    {
        if (Type == 2)
        {
            bool any = false;
            for (int j = 0; j < count; j++)
            {
                any |= !doNotDecode[j];
            }

            if (!any)
            {
                return;
            }

            if (count == 1)
            {
                DecodeVectors(ref r, vectors, Decode1, 1, n, 1, classes);
                return;
            }

            Span<float> v = interleaved.AsSpan(0, count * n);
            v.Clear();
            _single[0] = interleaved;
            DecodeVectors(ref r, _single, Decode1, 1, count * n, 1, classes);
            Deinterleave(v, vectors, count, n);
            return;
        }

        DecodeVectors(ref r, vectors, doNotDecode, count, n, Type, classes);
    }

    private static void Deinterleave(ReadOnlySpan<float> v, float[][] vectors, int count, int n)
    {
        if (count == 2)
        {
            Span<float> a = vectors[0].AsSpan(0, n);
            Span<float> b = vectors[1].AsSpan(0, n);
            for (int i = 0; i < n; i++)
            {
                a[i] = v[2 * i];
                b[i] = v[(2 * i) + 1];
            }

            return;
        }

        for (int j = 0; j < count; j++)
        {
            Span<float> a = vectors[j].AsSpan(0, n);
            for (int i = 0, p = j; i < n; i++, p += count)
            {
                a[i] = v[p];
            }
        }
    }

    private void DecodeVectors(ref VorbisBitReader r, float[][] vectors, ReadOnlySpan<bool> doNotDecode, int count, int size,
        int format, int[][] classes)
    {
        int begin = Math.Min(_begin, size);
        int end = Math.Min(_end, size);
        if (end <= begin)
        {
            return;
        }

        int partitionsToRead = (end - begin) / _partitionSize;
        int classwords = _classbook.Dimensions;
        int classifications = _classifications;
        for (int pass = 0; pass < 8; pass++)
        {
            int partition = 0;
            while (partition < partitionsToRead)
            {
                if (pass == 0)
                {
                    for (int j = 0; j < count; j++)
                    {
                        if (doNotDecode[j])
                        {
                            continue;
                        }

                        int temp = _classbook.DecodeScalar(ref r);
                        if (temp < 0)
                        {
                            return;
                        }

                        int[] cls = classes[j];
                        for (int i = classwords - 1; i >= 0; i--)
                        {
                            cls[i + partition] = temp % classifications;
                            temp /= classifications;
                        }
                    }
                }

                for (int i = 0; i < classwords && partition < partitionsToRead; i++, partition++)
                {
                    for (int j = 0; j < count; j++)
                    {
                        if (doNotDecode[j])
                        {
                            continue;
                        }

                        VorbisCodebook? book = _books[(classes[j][partition] * 8) + pass];
                        if (book is null)
                        {
                            continue;
                        }

                        Span<float> v = vectors[j].AsSpan(begin + (partition * _partitionSize), _partitionSize);
                        bool ok = format == 0 ? DecodeFormat0(ref r, book, v) : DecodeFormat1(ref r, book, v);
                        if (!ok)
                        {
                            return;
                        }
                    }
                }
            }
        }
    }

    /// <summary>Format 0 (8.6.3): vector values interleaved by step across the partition.</summary>
    private static bool DecodeFormat0(ref VorbisBitReader r, VorbisCodebook book, Span<float> v)
    {
        int dims = book.Dimensions;
        int step = v.Length / dims;
        float[] values = book.Values!;
        for (int i = 0; i < step; i++)
        {
            int entry = book.DecodeScalar(ref r);
            if (entry < 0)
            {
                return false;
            }

            int t = entry * dims;
            for (int j = 0; j < dims; j++)
            {
                v[i + (j * step)] += values[t + j];
            }
        }

        return true;
    }

    /// <summary>Format 1 (8.6.4): vector values in order; a last vector that overruns the partition is cut off.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool DecodeFormat1(ref VorbisBitReader r, VorbisCodebook book, Span<float> v)
    {
        int dims = book.Dimensions;
        float[] values = book.Values!;
        int i = 0;
        if (dims == 2)
        {
            while (i + 2 <= v.Length)
            {
                int entry = book.DecodeScalar(ref r);
                if (entry < 0)
                {
                    return false;
                }

                v[i] += values[2 * entry];
                v[i + 1] += values[(2 * entry) + 1];
                i += 2;
            }
        }

        while (i < v.Length)
        {
            int entry = book.DecodeScalar(ref r);
            if (entry < 0)
            {
                return false;
            }

            int t = entry * dims;
            for (int j = 0; j < dims && i < v.Length; j++)
            {
                v[i++] += values[t + j];
            }
        }

        return true;
    }
}
