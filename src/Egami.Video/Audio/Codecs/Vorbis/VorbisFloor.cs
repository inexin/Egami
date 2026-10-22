// Follows the Vorbis I specification (Xiph.Org): section 6 "Floor type 0 setup and decode" (header decode 6.2.1, packet
// decode 6.2.2, curve computation 6.2.3) and section 7 "Floor type 1 setup and decode" (header decode 7.2.2, packet decode
// 7.2.3, curve computation 7.2.4 with the helpers of 9.2.4 to 9.2.7 and the table of 10.1).
namespace Egami.Video.Audio.Codecs.Vorbis;

/// <summary>A floor configuration from the setup header.</summary>
internal abstract class VorbisFloor
{
    /// <summary>Gets the size of the per-channel decode state <see cref="Decode"/> fills.</summary>
    public abstract int StateSize { get; }

    public static VorbisFloor Read(ref VorbisBitReader r, VorbisCodebook[] books, int blockSize0, int blockSize1)
    {
        int type = (int)r.ReadBits(16);
        return type switch
        {
            0 => VorbisFloor0.Read(ref r, books, blockSize0, blockSize1),
            1 => VorbisFloor1.Read(ref r, books),
            _ => throw Vorbis.Corrupt($"A floor has the reserved type {type}."),
        };
    }

    /// <summary>Decodes a channel's floor from an audio packet into <paramref name="state"/>; false means 'unused'.</summary>
    public abstract bool Decode(ref VorbisBitReader r, Span<int> state, Span<float> scratch);

    /// <summary>Multiplies <paramref name="spectrum"/> (length n/2) by the floor curve the decoded state describes.</summary>
    public abstract void Apply(ReadOnlySpan<int> state, ReadOnlySpan<float> scratch, Span<float> spectrum, bool longBlock);
}

/// <summary>Floor type 1: a piecewise linear curve on a dB scale (section 7).</summary>
internal sealed class VorbisFloor1 : VorbisFloor
{
    private static readonly int[] Ranges = [256, 128, 86, 64];

    private readonly int[] _partitionClass;
    private readonly int[] _classDimensions;
    private readonly int[] _classSubclasses;
    private readonly VorbisCodebook?[] _classMasterbook;
    private readonly VorbisCodebook?[][] _subclassBooks;
    private readonly int _multiplier;
    private readonly int[] _x;
    private readonly int[] _low;
    private readonly int[] _high;
    private readonly int[] _sorted;
    private readonly int _range;
    private readonly int _yBits;

    private VorbisFloor1(int[] partitionClass, int[] classDimensions, int[] classSubclasses, VorbisCodebook?[] masterbooks,
        VorbisCodebook?[][] subclassBooks, int multiplier, int[] x)
    {
        _partitionClass = partitionClass;
        _classDimensions = classDimensions;
        _classSubclasses = classSubclasses;
        _classMasterbook = masterbooks;
        _subclassBooks = subclassBooks;
        _multiplier = multiplier;
        _x = x;
        _range = Ranges[multiplier - 1];
        _yBits = Vorbis.ILog(_range - 1);

        int values = x.Length;
        _low = new int[values];
        _high = new int[values];
        for (int i = 2; i < values; i++)
        {
            int low = 0;
            int high = 1;
            for (int j = 0; j < i; j++)
            {
                if (x[j] < x[i] && x[j] > x[low])
                {
                    low = j;
                }

                if (x[j] > x[i] && x[j] < x[high])
                {
                    high = j;
                }
            }

            _low[i] = low;
            _high[i] = high;
        }

        _sorted = [.. Enumerable.Range(0, values).OrderBy(i => x[i])];
    }

    // State: final Y values then step2 flags.
    public override int StateSize => 2 * _x.Length;

    public static VorbisFloor1 Read(ref VorbisBitReader r, VorbisCodebook[] books)
    {
        int partitions = (int)r.ReadBits(5);
        int[] partitionClass = new int[partitions];
        int maxClass = -1;
        for (int i = 0; i < partitions; i++)
        {
            partitionClass[i] = (int)r.ReadBits(4);
            maxClass = Math.Max(maxClass, partitionClass[i]);
        }

        int classes = maxClass + 1;
        int[] dimensions = new int[classes];
        int[] subclasses = new int[classes];
        VorbisCodebook?[] masterbooks = new VorbisCodebook?[classes];
        VorbisCodebook?[][] subclassBooks = new VorbisCodebook?[classes][];
        for (int c = 0; c < classes; c++)
        {
            dimensions[c] = (int)r.ReadBits(3) + 1;
            subclasses[c] = (int)r.ReadBits(2);
            if (subclasses[c] != 0)
            {
                masterbooks[c] = Book(books, (int)r.ReadBits(8));
            }

            subclassBooks[c] = new VorbisCodebook?[1 << subclasses[c]];
            for (int j = 0; j < subclassBooks[c].Length; j++)
            {
                int book = (int)r.ReadBits(8) - 1;
                subclassBooks[c][j] = book < 0 ? null : Book(books, book);
            }
        }

        int multiplier = (int)r.ReadBits(2) + 1;
        int rangeBits = (int)r.ReadBits(4);
        List<int> x = [0, 1 << rangeBits];
        foreach (int c in partitionClass)
        {
            for (int j = 0; j < dimensions[c]; j++)
            {
                x.Add((int)r.ReadBits(rangeBits));
                if (x.Count > 65)
                {
                    throw Vorbis.Corrupt("A floor 1 has more than 65 points.");
                }
            }
        }

        if (x.Distinct().Count() != x.Count)
        {
            throw Vorbis.Corrupt("A floor 1 has two points at the same position.");
        }

        return new VorbisFloor1(partitionClass, dimensions, subclasses, masterbooks, subclassBooks, multiplier, [.. x]);
    }

    private static VorbisCodebook Book(VorbisCodebook[] books, int index) =>
        index < books.Length ? books[index] : throw Vorbis.Corrupt("A floor names a codebook that does not exist.");

    public override bool Decode(ref VorbisBitReader r, Span<int> state, Span<float> scratch)
    {
        if (!r.ReadBit())
        {
            return false;
        }

        int values = _x.Length;
        Span<int> y = state[..values];
        Span<int> step2 = state.Slice(values, values);
        y[0] = (int)r.ReadBits(_yBits);
        y[1] = (int)r.ReadBits(_yBits);
        int offset = 2;
        foreach (int c in _partitionClass)
        {
            int cdim = _classDimensions[c];
            int cbits = _classSubclasses[c];
            int csub = (1 << cbits) - 1;
            int cval = 0;
            if (cbits > 0)
            {
                cval = _classMasterbook[c]!.DecodeScalar(ref r);
            }

            VorbisCodebook?[] subBooks = _subclassBooks[c];
            for (int j = 0; j < cdim; j++)
            {
                VorbisCodebook? book = subBooks[cval & csub];
                cval >>= cbits;
                y[offset + j] = book is null ? 0 : book.DecodeScalar(ref r);
            }

            offset += cdim;
        }

        if (r.Eop)
        {
            return false;
        }

        // Amplitude value synthesis (7.2.4 step 1).
        step2[0] = 1;
        step2[1] = 1;
        for (int i = 2; i < values; i++)
        {
            int low = _low[i];
            int high = _high[i];
            int predicted = RenderPoint(_x[low], y[low], _x[high], y[high], _x[i]);
            int val = y[i];
            int highRoom = _range - predicted;
            int lowRoom = predicted;
            int room = highRoom < lowRoom ? highRoom * 2 : lowRoom * 2;
            if (val != 0)
            {
                step2[low] = 1;
                step2[high] = 1;
                step2[i] = 1;
                if (val >= room)
                {
                    y[i] = highRoom > lowRoom ? val - lowRoom + predicted : predicted - val + highRoom - 1;
                }
                else
                {
                    y[i] = (val & 1) != 0 ? predicted - ((val + 1) / 2) : predicted + (val / 2);
                }
            }
            else
            {
                step2[i] = 0;
                y[i] = predicted;
            }
        }

        return true;
    }

    private static int RenderPoint(int x0, int y0, int x1, int y1, int x)
    {
        int dy = y1 - y0;
        int adx = x1 - x0;
        int err = Math.Abs(dy) * (x - x0);
        int off = err / adx;
        return dy < 0 ? y0 - off : y0 + off;
    }

    public override void Apply(ReadOnlySpan<int> state, ReadOnlySpan<float> scratch, Span<float> spectrum, bool longBlock)
    {
        // Curve synthesis (7.2.4 step 2), rendered straight through the inverse dB table and multiplied in.
        int values = _x.Length;
        ReadOnlySpan<int> y = state[..values];
        ReadOnlySpan<int> step2 = state.Slice(values, values);
        ReadOnlySpan<float> db = VorbisTables.InverseDb;
        int n = spectrum.Length;
        int lx = 0;
        int ly = Math.Clamp(y[_sorted[0]] * _multiplier, 0, 255);
        for (int k = 1; k < values; k++)
        {
            int i = _sorted[k];
            if (step2[i] == 0)
            {
                continue;
            }

            int hx = _x[i];
            int hy = Math.Clamp(y[i] * _multiplier, 0, 255);
            RenderLine(lx, ly, hx, hy, spectrum, db);
            lx = hx;
            ly = hy;
            if (lx >= n)
            {
                return;
            }
        }

        float last = db[ly];
        for (int x = lx; x < n; x++)
        {
            spectrum[x] *= last;
        }
    }

    private static void RenderLine(int x0, int y0, int x1, int y1, Span<float> v, ReadOnlySpan<float> db)
    {
        int dy = y1 - y0;
        int adx = x1 - x0;
        int ady = Math.Abs(dy);
        int b = dy / adx;
        int sy = dy < 0 ? b - 1 : b + 1;
        ady -= Math.Abs(b) * adx;
        int end = Math.Min(x1, v.Length);
        int y = y0;
        int err = 0;
        if (x0 < end)
        {
            v[x0] *= db[y];
        }

        for (int x = x0 + 1; x < end; x++)
        {
            err += ady;
            if (err >= adx)
            {
                err -= adx;
                y += sy;
            }
            else
            {
                y += b;
            }

            v[x] *= db[y];
        }
    }
}

/// <summary>Floor type 0: the frequency response of an LSP filter (section 6).</summary>
internal sealed class VorbisFloor0 : VorbisFloor
{
    private readonly int _order;
    private readonly int _barkMapSize;
    private readonly int _amplitudeBits;
    private readonly int _amplitudeOffset;
    private readonly VorbisCodebook[] _books;
    private readonly int _bookBits;
    private readonly int _maxDimensions;
    private readonly int[] _map0;
    private readonly int[] _map1;

    private VorbisFloor0(int order, int rate, int barkMapSize, int amplitudeBits, int amplitudeOffset, VorbisCodebook[] books,
        int blockSize0, int blockSize1)
    {
        _order = order;
        _barkMapSize = barkMapSize;
        _amplitudeBits = amplitudeBits;
        _amplitudeOffset = amplitudeOffset;
        _books = books;
        _bookBits = Vorbis.ILog(books.Length);
        _maxDimensions = books.Max(b => b.Dimensions);
        _map0 = BarkMap(rate, barkMapSize, blockSize0 / 2);
        _map1 = BarkMap(rate, barkMapSize, blockSize1 / 2);
    }

    // State: the amplitude; the coefficients go in the float scratch (the order plus one vector of overshoot).
    public override int StateSize => 1;

    public int ScratchSize => _order + _maxDimensions;

    private static double Bark(double x) => (13.1 * Math.Atan(.00074 * x)) + (2.24 * Math.Atan(.0000000185 * x * x)) + (.0001 * x);

    private static int[] BarkMap(int rate, int barkMapSize, int n)
    {
        int[] map = new int[n + 1];
        double scale = barkMapSize / Bark(.5 * rate);
        for (int i = 0; i < n; i++)
        {
            map[i] = Math.Min(barkMapSize - 1, (int)Math.Floor(Bark(rate * (double)i / (2.0 * n)) * scale));
        }

        map[n] = -1;
        return map;
    }

    public static new VorbisFloor0 Read(ref VorbisBitReader r, VorbisCodebook[] books, int blockSize0, int blockSize1)
    {
        int order = (int)r.ReadBits(8);
        int rate = (int)r.ReadBits(16);
        int barkMapSize = (int)r.ReadBits(16);
        int amplitudeBits = (int)r.ReadBits(6);
        int amplitudeOffset = (int)r.ReadBits(8);
        int count = (int)r.ReadBits(4) + 1;
        VorbisCodebook[] list = new VorbisCodebook[count];
        for (int i = 0; i < count; i++)
        {
            int book = (int)r.ReadBits(8);
            if (book >= books.Length)
            {
                throw Vorbis.Corrupt("A floor 0 names a codebook that does not exist.");
            }

            list[i] = books[book];
            if (!list[i].HasLookup)
            {
                throw Vorbis.Corrupt("A floor 0 codebook has no lookup table.");
            }
        }

        if (order == 0 || rate == 0 || barkMapSize == 0)
        {
            throw Vorbis.Corrupt("A floor 0 has a zero order, rate or Bark map size.");
        }

        return new VorbisFloor0(order, rate, barkMapSize, amplitudeBits, amplitudeOffset, list, blockSize0, blockSize1);
    }

    public override bool Decode(ref VorbisBitReader r, Span<int> state, Span<float> scratch)
    {
        int amplitude = (int)r.ReadBits(_amplitudeBits);
        if (amplitude == 0 || r.Eop)
        {
            return false;
        }

        int bookNumber = (int)r.ReadBits(_bookBits);
        if (r.Eop || bookNumber >= _books.Length)
        {
            return false;
        }

        VorbisCodebook book = _books[bookNumber];
        int dims = book.Dimensions;
        float[] values = book.Values!;
        float last = 0;
        int count = 0;
        while (count < _order)
        {
            int entry = book.DecodeScalar(ref r);
            if (entry < 0)
            {
                return false;
            }

            for (int j = 0; j < dims; j++)
            {
                scratch[count + j] = values[(entry * dims) + j] + last;
            }

            count += dims;
            last = scratch[count - 1];
        }

        state[0] = amplitude;
        return true;
    }

    public override void Apply(ReadOnlySpan<int> state, ReadOnlySpan<float> scratch, Span<float> spectrum, bool longBlock)
    {
        int[] map = longBlock ? _map1 : _map0;
        int n = spectrum.Length;
        int amplitude = state[0];
        Span<double> cosines = stackalloc double[_order];
        for (int j = 0; j < _order; j++)
        {
            cosines[j] = Math.Cos(scratch[j]);
        }

        double gain = amplitude * (double)_amplitudeOffset / ((1L << _amplitudeBits) - 1);
        int i = 0;
        while (i < n)
        {
            double omega = Math.PI * map[i] / _barkMapSize;
            double cw = Math.Cos(omega);
            double p;
            double q;
            if ((_order & 1) != 0)
            {
                p = 1 - (cw * cw);
                for (int j = 0; j < (_order - 1) / 2; j++)
                {
                    double d = cosines[(2 * j) + 1] - cw;
                    p *= 4 * d * d;
                }

                q = .25;
                for (int j = 0; j < (_order + 1) / 2; j++)
                {
                    double d = cosines[2 * j] - cw;
                    q *= 4 * d * d;
                }
            }
            else
            {
                p = (1 - cw) / 2;
                q = (1 + cw) / 2;
                for (int j = 0; j < _order / 2; j++)
                {
                    double dp = cosines[(2 * j) + 1] - cw;
                    double dq = cosines[2 * j] - cw;
                    p *= 4 * dp * dp;
                    q *= 4 * dq * dq;
                }
            }

            float value = (float)Math.Exp(.11512925 * ((gain / Math.Sqrt(p + q)) - _amplitudeOffset));
            int condition = map[i];
            do
            {
                spectrum[i] *= value;
                i++;
            }
            while (map[i] == condition);
        }
    }
}
