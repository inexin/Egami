using Egami.Fonts.Parsing;

namespace Egami.Fonts.Variations;

/// <summary>
/// Glyph variations ('gvar', OpenType 1.9.1): per glyph, tuple variations with embedded or shared peaks, intermediate regions,
/// shared and private packed point numbers and packed deltas, with the deltas of points a tuple leaves out inferred by IUP.
/// </summary>
internal sealed class GvarTable
{
    private const int SharedPointNumbers = 0x8000;
    private const int CountMask = 0x0FFF;
    private const int EmbeddedPeak = 0x8000;
    private const int IntermediateRegion = 0x4000;
    private const int PrivatePointNumbers = 0x2000;
    private const int TupleIndexMask = 0x0FFF;

    private readonly ReadOnlyMemory<byte> _table;
    private readonly long _offset;
    private readonly int _axisCount;
    private readonly float[] _sharedTuples;
    private readonly uint[] _offsets;
    private readonly int _dataStart;

    private GvarTable(ReadOnlyMemory<byte> table, long offset, int axisCount, float[] sharedTuples, uint[] offsets, int dataStart)
    {
        _table = table;
        _offset = offset;
        _axisCount = axisCount;
        _sharedTuples = sharedTuples;
        _offsets = offsets;
        _dataStart = dataStart;
    }

    public static GvarTable Read(ReadOnlyMemory<byte> table, long offset, int axisCount, int glyphCount)
    {
        ReadOnlySpan<byte> data = table.Span;
        var r = new FontReader(data, offset);
        int major = r.ReadUInt16();
        r.Skip(2);
        if (major != 1)
        {
            throw FontErrors.Corrupt(offset, $"The gvar table has version {major}.");
        }

        int axes = r.ReadUInt16();
        if (axes != axisCount)
        {
            throw FontErrors.Corrupt(offset, $"The gvar table has {axes} axes; fvar has {axisCount}.");
        }

        int sharedCount = r.ReadUInt16();
        int sharedOffset = (int)r.ReadUInt32();
        int glyphs = r.ReadUInt16();
        int flags = r.ReadUInt16();
        int dataStart = (int)r.ReadUInt32();
        int count = Math.Min(glyphs, glyphCount);
        var offsets = new uint[count + 1];
        bool longOffsets = (flags & 1) != 0;
        for (int i = 0; i <= count; i++)
        {
            offsets[i] = longOffsets ? r.ReadUInt32() : (uint)r.ReadUInt16() * 2;
        }

        var shared = new float[sharedCount * axisCount];
        var s = new FontReader(BigEndian.Slice(data, sharedOffset, (long)shared.Length * 2), offset + sharedOffset);
        for (int i = 0; i < shared.Length; i++)
        {
            shared[i] = s.ReadF2Dot14();
        }

        if (dataStart < 0 || dataStart > data.Length)
        {
            throw FontErrors.Corrupt(offset, "The gvar glyph variation data starts past the end of the table.");
        }

        return new GvarTable(table, offset, axisCount, shared, offsets, dataStart);
    }

    /// <summary>
    /// Adds a glyph's deltas at normalized <paramref name="coords"/> to <paramref name="dx"/> and <paramref name="dy"/>, one per point
    /// (the glyph's points then its four phantom points). <paramref name="x"/> and <paramref name="y"/> are the default positions
    /// IUP interpolates between; <paramref name="ends"/> are the contour ends, or empty for a composite glyph, whose points are
    /// component offsets and get no inferred deltas.
    /// </summary>
    /// <returns><see langword="true"/> if any tuple applied.</returns>
    public bool AddDeltas(int glyph, ReadOnlySpan<float> coords, ReadOnlySpan<float> x, ReadOnlySpan<float> y, IReadOnlyList<int> ends, Span<float> dx, Span<float> dy)
    {
        if ((uint)glyph + 1 >= (uint)_offsets.Length)
        {
            return false;
        }

        uint start = _offsets[glyph], end = _offsets[glyph + 1];
        if (end <= start)
        {
            return false;
        }

        ReadOnlySpan<byte> table = _table.Span;
        long at = _dataStart + (long)start;
        ReadOnlySpan<byte> data = BigEndian.Slice(table, at, end - start);
        long fileOffset = _offset + at;
        var header = new FontReader(data, fileOffset);
        int tupleCount = header.ReadUInt16();
        int serializedAt = header.ReadUInt16();
        int pointCount = x.Length;
        var serialized = new FontReader(BigEndian.Slice(data, serializedAt, data.Length - (long)serializedAt), fileOffset + serializedAt);
        int[]? sharedPoints = (tupleCount & SharedPointNumbers) != 0 ? ReadPoints(ref serialized, pointCount) : null;
        Span<float> peak = stackalloc float[_axisCount];
        Span<float> lower = stackalloc float[_axisCount];
        Span<float> upper = stackalloc float[_axisCount];
        float[]? tx = null, ty = null;
        bool[]? touchedPoints = null;
        bool applied = false;
        for (int t = 0; t < (tupleCount & CountMask); t++)
        {
            int size = header.ReadUInt16();
            int index = header.ReadUInt16();
            if ((index & EmbeddedPeak) != 0)
            {
                for (int a = 0; a < _axisCount; a++)
                {
                    peak[a] = header.ReadF2Dot14();
                }
            }
            else
            {
                int shared = index & TupleIndexMask;
                if ((shared + 1) * _axisCount > _sharedTuples.Length)
                {
                    throw FontErrors.Corrupt(fileOffset, $"A gvar tuple names shared tuple {shared} of {_sharedTuples.Length / Math.Max(1, _axisCount)}.");
                }

                _sharedTuples.AsSpan(shared * _axisCount, _axisCount).CopyTo(peak);
            }

            bool intermediate = (index & IntermediateRegion) != 0;
            if (intermediate)
            {
                for (int a = 0; a < _axisCount; a++)
                {
                    lower[a] = header.ReadF2Dot14();
                }

                for (int a = 0; a < _axisCount; a++)
                {
                    upper[a] = header.ReadF2Dot14();
                }
            }

            int tupleStart = serialized.Position;
            if (size > serialized.Remaining)
            {
                throw FontErrors.Corrupt(fileOffset, "A gvar tuple's data runs past the glyph's variation data.");
            }

            serialized.Skip(size);
            float scalar = TupleScalar(coords, peak, lower, upper, intermediate);
            if (scalar == 0)
            {
                continue;
            }

            var tuple = new FontReader(serialized.Data.Slice(tupleStart, size), fileOffset + serializedAt + tupleStart);
            int[]? points = (index & PrivatePointNumbers) != 0 ? ReadPoints(ref tuple, pointCount) : sharedPoints;
            int n = points?.Length ?? pointCount;
            tx ??= new float[pointCount];
            ty ??= new float[pointCount];
            Span<float> xs = tx, ys = ty;
            int[] buffer = new int[n * 2];
            ReadDeltas(ref tuple, buffer.AsSpan(0, n));
            ReadDeltas(ref tuple, buffer.AsSpan(n, n));
            if (points is null)
            {
                for (int i = 0; i < pointCount; i++)
                {
                    dx[i] += scalar * buffer[i];
                    dy[i] += scalar * buffer[n + i];
                }
            }
            else
            {
                xs.Clear();
                ys.Clear();
                touchedPoints ??= new bool[pointCount];
                Span<bool> touched = touchedPoints;
                touched.Clear();
                for (int i = 0; i < n; i++)
                {
                    int p = points[i];
                    if ((uint)p < (uint)pointCount)
                    {
                        xs[p] = buffer[i];
                        ys[p] = buffer[n + i];
                        touched[p] = true;
                    }
                }

                InferDeltas(x, xs, touched, ends);
                InferDeltas(y, ys, touched, ends);
                for (int i = 0; i < pointCount; i++)
                {
                    dx[i] += scalar * xs[i];
                    dy[i] += scalar * ys[i];
                }
            }

            applied = true;
        }

        return applied;
    }

    /// <summary>A tuple's scalar: the product over axes of the tent its peak (and intermediate start and end) describe.</summary>
    internal static float TupleScalar(ReadOnlySpan<float> coords, ReadOnlySpan<float> peak, ReadOnlySpan<float> lower, ReadOnlySpan<float> upper, bool intermediate)
    {
        float scalar = 1;
        for (int a = 0; a < peak.Length; a++)
        {
            float p = peak[a];
            float s = intermediate ? lower[a] : MathF.Min(p, 0);
            float e = intermediate ? upper[a] : MathF.Max(p, 0);

            // An axis with no peak, a malformed region or one crossing zero does not limit the tuple.
            if (p == 0 || s > p || p > e || (s < 0 && e > 0))
            {
                continue;
            }

            float c = a < coords.Length ? coords[a] : 0;
            if (c == p)
            {
                continue;
            }

            if (c <= s || c >= e)
            {
                return 0;
            }

            scalar *= c < p ? (c - s) / (p - s) : (e - c) / (e - p);
        }

        return scalar;
    }

    /// <summary>Reads packed point numbers: a count (0 for all points), then runs of byte or word increments.</summary>
    internal static int[]? ReadPoints(ref FontReader r, int pointCount)
    {
        int count = r.ReadByte();
        if (count == 0)
        {
            return null;
        }

        if ((count & 0x80) != 0)
        {
            count = ((count & 0x7F) << 8) | r.ReadByte();
        }

        if (count > Math.Max(pointCount, 0xFFFF))
        {
            throw FontErrors.Corrupt(-1, "A gvar tuple lists more points than the glyph has.");
        }

        var points = new int[count];
        int point = 0, i = 0;
        while (i < count)
        {
            int control = r.ReadByte();
            int run = (control & 0x7F) + 1;
            bool words = (control & 0x80) != 0;
            for (int k = 0; k < run && i < count; k++)
            {
                point += words ? r.ReadUInt16() : r.ReadByte();
                points[i++] = point;
            }
        }

        return points;
    }

    /// <summary>Reads packed deltas: runs of zeros, bytes, words or longs.</summary>
    internal static void ReadDeltas(ref FontReader r, Span<int> deltas)
    {
        int i = 0;
        while (i < deltas.Length)
        {
            int control = r.ReadByte();
            int run = (control & 0x3F) + 1;
            int kind = control & 0xC0;
            for (int k = 0; k < run && i < deltas.Length; k++)
            {
                deltas[i++] = kind switch
                {
                    0x80 => 0,
                    0x40 => r.ReadInt16(),
                    0xC0 => r.ReadInt32(),
                    _ => r.ReadSByte(),
                };
            }
        }
    }

    /// <summary>
    /// Infers the deltas of untouched points contour by contour, as OpenType's IUP defines: a contour with one touched point moves
    /// whole; between two touched points a coordinate interpolates, or takes the nearer point's delta outside their span.
    /// Points past the last contour (phantom points) keep what they have.
    /// </summary>
    internal static void InferDeltas(ReadOnlySpan<float> orig, Span<float> deltas, ReadOnlySpan<bool> touched, IReadOnlyList<int> ends)
    {
        int start = 0;
        foreach (int end in ends)
        {
            int last = Math.Min(end, orig.Length - 1);
            InferContour(orig, deltas, touched, start, last);
            start = end + 1;
        }
    }

    private static void InferContour(ReadOnlySpan<float> orig, Span<float> deltas, ReadOnlySpan<bool> touched, int start, int end)
    {
        int first = -1;
        for (int i = start; i <= end; i++)
        {
            if (touched[i])
            {
                first = i;
                break;
            }
        }

        if (first < 0)
        {
            return;
        }

        // Walk from each touched point to the next one, cyclically, filling the untouched points between them.
        int current = first;
        do
        {
            int next = current;
            do
            {
                next = next == end ? start : next + 1;
            }
            while (!touched[next]);

            for (int p = current == end ? start : current + 1; p != next; p = p == end ? start : p + 1)
            {
                deltas[p] = Interpolate(orig[p], orig[current], orig[next], deltas[current], deltas[next]);
            }

            current = next;
        }
        while (current != first);
    }

    private static float Interpolate(float c, float c1, float c2, float d1, float d2)
    {
        if (c1 == c2)
        {
            return d1 == d2 ? d1 : 0;
        }

        if (c1 > c2)
        {
            (c1, c2) = (c2, c1);
            (d1, d2) = (d2, d1);
        }

        if (c <= c1)
        {
            return d1;
        }

        if (c >= c2)
        {
            return d2;
        }

        return d1 + ((c - c1) * (d2 - d1) / (c2 - c1));
    }
}
