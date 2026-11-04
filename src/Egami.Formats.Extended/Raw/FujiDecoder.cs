using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Egami.Formats.Extended.Raw;

/// <summary>
/// Fujifilm's compressed RAF codec, lossless and lossy, X-Trans and Bayer, written from the clean-room specification a
/// study-only session wrote (sections 3 to 9): a 16-byte header, independent vertical strips of 768 columns, and per strip six
/// passes per 6-row line that decode colour lines with a gradient predictor, a 41-context adaptive Golomb-Rice code and, when
/// lossy, a per-line quantiser. Strips decode in parallel; each writes only its own columns.
/// </summary>
internal static class FujiDecoder
{
    private const int BlockWidth = 768;

    /// <summary>The X-Trans reference pattern the codec anchors at raw (0, 0): 0 red, 1 green, 2 blue (spec, section 8.3).</summary>
    internal static readonly byte[] XTrans =
    [
        1, 1, 0, 1, 1, 2,
        1, 1, 2, 1, 1, 0,
        2, 0, 1, 0, 2, 1,
        1, 1, 2, 1, 1, 0,
        1, 1, 0, 1, 1, 2,
        0, 2, 1, 2, 0, 1,
    ];

    /// <summary>The compressed stream's header (spec, section 3).</summary>
    internal sealed record Header(bool Lossless, bool XTransType, int Bits, int Height, int RoundedWidth, int Width, int Blocks, int Lines);

    /// <summary>Reads and checks the header; returns <see langword="null"/> when the 16 bytes are not one.</summary>
    public static Header? ReadHeader(ReadOnlySpan<byte> s)
    {
        if (s.Length < 16 || BinaryPrimitives.ReadUInt16BigEndian(s) != 0x4953)
        {
            return null;
        }

        int bits = s[4], height = BinaryPrimitives.ReadUInt16BigEndian(s[5..]), rounded = BinaryPrimitives.ReadUInt16BigEndian(s[7..]);
        int width = BinaryPrimitives.ReadUInt16BigEndian(s[9..]), blockWidth = BinaryPrimitives.ReadUInt16BigEndian(s[11..]);
        int blocks = s[13], lines = BinaryPrimitives.ReadUInt16BigEndian(s[14..]);
        bool xtrans = s[3] == 16;
        bool valid = s[2] <= 1 && s[3] is 16 or 0 && bits is 12 or 14 or 16
            && height is >= 6 and <= 0x4002 && height % 6 == 0
            && blockWidth == BlockWidth && rounded <= 0x4200 && rounded >= blockWidth && rounded % blockWidth == 0
            && width is >= 0x300 and <= 0x4200 && width % 24 == 0 && rounded - width < blockWidth
            && blocks is >= 1 and <= 16 && blocks == rounded / blockWidth && lines is >= 1 and <= 0xAAB && lines == height / 6;
        return valid ? new Header(s[2] == 1, xtrans, bits, height, rounded, width, blocks, lines) : null;
    }

    /// <summary>Decodes the strip at <paramref name="offset"/> into <paramref name="pixels"/> (raw width × raw height).</summary>
    public static void Decode(byte[] file, int offset, int count, RawPixels pixels, ParallelOptions parallel, Action<int, string> damaged)
    {
        ReadOnlySpan<byte> data = file.AsSpan(offset, count);
        Header h = ReadHeader(data) ?? throw new InvalidDataException("The RAF compressed header is not valid.");
        if (h.Width != pixels.Width || h.Height != pixels.Height)
        {
            throw new InvalidDataException("The RAF compressed header's size is not the raw image's.");
        }

        int table = 16;
        int qbases = table + ((4 * h.Blocks) + 15 & ~15);
        int l16 = (h.Lines + 15) & ~15;
        long at = qbases + (h.Lossless ? 0 : (long)h.Blocks * l16);
        if (at > data.Length)
        {
            throw new InvalidDataException("The RAF compressed data ends inside its strip table.");
        }

        long[] sizes = new long[h.Blocks];
        long[] starts = new long[h.Blocks];
        for (int b = 0; b < h.Blocks; b++)
        {
            sizes[b] = BinaryPrimitives.ReadUInt32BigEndian(data[(table + (4 * b))..]);
            starts[b] = at;
            at += sizes[b];
        }

        byte[] q = h.Lossless ? [] : data.Slice(qbases, h.Blocks * l16).ToArray();

        // The tables are the same for every strip: the main table of each q base the file uses, and the three small ones.
        int max = (1 << h.Bits) - 1;
        QTable[] mains = new QTable[256];
        mains[0] = new QTable(max, 0, 18, 67, 276, 9);
        foreach (byte b in q)
        {
            mains[b] ??= new QTable(max, b, (3 * b) + 18, (5 * b) + 67, (7 * b) + 276, 9);
        }

        QTable[] small = [new QTable(max, 0, 18, 67, 276, 3), new QTable(max, 1, 21, 72, 283, 3), new QTable(max, 2, 24, 77, 290, 3)];
        List<(int, string)> reports = [];
        RawParallel.For(0, h.Blocks, parallel, b =>
        {
            // A strip that starts past the end of the file (or of its declared data) is missing: it decodes from zeros.
            long start = Math.Min(offset + starts[b], file.Length);
            int length = (int)Math.Clamp(Math.Min(sizes[b], file.Length - start), 0, int.MaxValue);
            try
            {
                new Strip(h, b, file.AsSpan((int)start, length), h.Lossless ? default : q.AsSpan(b * l16, h.Lines), pixels, mains, small).Run();
            }
            catch (InvalidDataException e)
            {
                lock (reports)
                {
                    reports.Add((b, $"RAF strip {b} is damaged ({e.Message}); what did not decode reads as black."));
                }
            }
        });
        foreach ((int b, string m) in reports.OrderBy(r => r.Item1))
        {
            damaged(b, m);
        }
    }

    /// <summary>A quantisation table: thresholds, quantiser base and the derived step, size and raw width (spec, section 7.3).</summary>
    private sealed class QTable
    {
        private readonly int _max;
        private readonly int _t0;
        private readonly int _t1;
        private readonly int _t2;
        private readonly int _t3;

        public QTable(int max, int q, int t1, int t2, int t3, int mult)
        {
            _max = max;
            Q = q;
            Mult = mult;
            Step = (2 * q) + 1;
            Total = ((max + (2 * q)) / Step) + 1;
            RawBits = Math.Max(1, 32 - BitOperations.LeadingZeroCount((uint)(Total - 1)));
            int t0 = q;
            if (t1 > max || t1 < q + 1)
            {
                t1 = q + 1;
            }

            if (t2 < t1 || t2 > max)
            {
                t2 = t1;
            }

            if (t3 < t2 || t3 > max)
            {
                t3 = t2;
            }

            (_t0, _t1, _t2, _t3) = (t0, t1, t2, t3);
        }

        public int Q { get; }

        public int Mult { get; }

        public int Step { get; }

        public int Total { get; }

        public int RawBits { get; }

        /// <summary>
        /// Quantises a gradient: −4 at or below −T3, −3 at or below −T2, −2 at or below −T1, −1 below −T0, 0 to T0, 1 below T1, 2
        /// below T2, 3 below T3, else 4. As a sum of comparisons rather than a table, which for 16-bit data is 128 KB and missed
        /// the cache.
        /// </summary>
        public int Of(int v)
        {
            v = Math.Clamp(v, -_max, _max);
            return (v > _t0 ? 1 : 0) + (v >= _t1 ? 1 : 0) + (v >= _t2 ? 1 : 0) + (v >= _t3 ? 1 : 0)
                - (v < -_t0 ? 1 : 0) - (v <= -_t1 ? 1 : 0) - (v <= -_t2 ? 1 : 0) - (v <= -_t3 ? 1 : 0);
        }

        public int Initial => Math.Max(2, (Total + 32) >> 6);
    }

    /// <summary>One strip's decoder: its bit reader, colour lines and adaptive contexts (spec, sections 4 to 8).</summary>
    private ref struct Strip
    {
        // Colour lines: red 0 to 4, green 5 to 12, blue 13 to 17; each W cells plus a pad on either side.
        private const int R0 = 0, G0 = 5, B0 = 13;
        private static readonly (int A, int B)[] Passes = [(R0 + 2, G0 + 2), (G0 + 3, B0 + 2), (R0 + 3, G0 + 4), (G0 + 5, B0 + 3), (R0 + 4, G0 + 6), (G0 + 7, B0 + 4)];

        private readonly Header _h;
        private readonly int _strip;
        private readonly ReadOnlySpan<byte> _data;
        private readonly ReadOnlySpan<byte> _q;
        private readonly RawPixels _pixels;
        private readonly int _w;
        private readonly int _stride;
        private readonly int _max;
        private readonly int _maxBits;
        private readonly int[] _lines;
        private readonly int[] _a;
        private readonly int[] _n;
        private readonly int[] _sa;
        private readonly int[] _sn;
        private readonly QTable[] _small;
        private readonly QTable[] _mains;
        private QTable _main;
        private int _pos;
        private ulong _buffer;
        private int _count;

        public Strip(Header h, int strip, ReadOnlySpan<byte> data, ReadOnlySpan<byte> q, RawPixels pixels, QTable[] mains, QTable[] small)
        {
            _h = h;
            _strip = strip;
            _data = data;
            _q = q;
            _pixels = pixels;
            _w = h.XTransType ? BlockWidth * 2 / 3 : BlockWidth / 2;
            _stride = _w + 2;
            _max = (1 << h.Bits) - 1;
            _maxBits = 4 * (32 - BitOperations.LeadingZeroCount((uint)_max));
            _lines = new int[18 * (_w + 2)];
            _a = new int[6 * 41];
            _n = new int[6 * 41];
            _sa = new int[6 * 3 * 5];
            _sn = new int[6 * 3 * 5];
            _mains = mains;
            _main = mains[0];
            _small = small;
            for (int i = 0; i < _sa.Length; i++)
            {
                _sa[i] = _small[i / 5 % 3].Initial;
                _sn[i] = 1;
            }

            ResetMain();
        }

        private readonly void ResetMain()
        {
            for (int i = 0; i < _a.Length; i++)
            {
                _a[i] = _main.Initial;
                _n[i] = 1;
            }
        }

        // Lines are 0 to 17 and positions -1 to W, by construction, so the colour lines need no bounds checks.
        private readonly int Cell(int line, int p) => Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_lines), (line * _stride) + p + 1);

        private readonly void Set(int line, int p, int v) => Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_lines), (line * _stride) + p + 1) = v;

        public void Run()
        {
            int previousQ = -1;
            for (int line = 0; line < _h.Lines; line++)
            {
                if (!_h.Lossless)
                {
                    int q = _q[line];
                    if (q != previousQ)
                    {
                        _main = _mains[q];
                        ResetMain();
                        previousQ = q;
                    }
                }

                for (int k = 0; k < 6; k++)
                {
                    (int a, int b) = Passes[k];
                    Pads(a);
                    Pads(b);
                    for (int i = 0; i < (_w / 2) + 4; i++)
                    {
                        if (i < _w / 2)
                        {
                            Sample(a, 2 * i, k, true);
                            Sample(b, 2 * i, k, false);
                        }

                        if (i >= 4 && i - 4 < _w / 2)
                        {
                            Sample(a, (2 * (i - 4)) + 1, k, true);
                            Sample(b, (2 * (i - 4)) + 1, k, false);
                        }
                    }
                }

                Copy(line);

                // The last two lines of each colour carry into the next 6-row line.
                Move(R0 + 3, R0);
                Move(R0 + 4, R0 + 1);
                Move(G0 + 6, G0);
                Move(G0 + 7, G0 + 1);
                Move(B0 + 3, B0);
                Move(B0 + 4, B0 + 1);
            }
        }

        private readonly void Move(int from, int to) => _lines.AsSpan(from * (_w + 2), _w + 2).CopyTo(_lines.AsSpan(to * (_w + 2), _w + 2));

        /// <summary>The pad rule: a line's pads hold the first and last real samples of the line above (spec, section 6).</summary>
        private readonly void Pads(int line)
        {
            Set(line, -1, Cell(line - 1, 0));
            Set(line, _w, Cell(line - 1, _w - 1));
        }

        /// <summary>Gets whether an even cell of an X-Trans pass is predictor-only (spec, section 8.2).</summary>
        private readonly bool PredictorOnly(int k, bool first, int p)
        {
            if (!_h.XTransType)
            {
                return false;
            }

            return (k, first) switch
            {
                (0, true) => true,
                (1, false) => true,
                (2, true) => p % 4 == 0,
                (2, false) => true,
                (3, false) => p % 4 == 2,
                (4, true) => p % 4 == 2,
                (5, true) => true,
                (5, false) => p % 4 == 0,
                _ => false,
            };
        }

        private void Sample(int line, int p, int k, bool first)
        {
            int n = Cell(line - 1, p), nw = Cell(line - 1, p - 1), ne = Cell(line - 1, p + 1), nn = Cell(line - 2, p);
            int pred, v1, v2, m;
            bool even = (p & 1) == 0;
            if (even)
            {
                int dnw = Math.Abs(nw - n), dnn = Math.Abs(nn - n), dne = Math.Abs(ne - n);
                // The two neighbours beside the largest difference, without branches: they are hard to predict.
                bool westOut = (dnw > dnn) & (dnw > dne), eastOut = !westOut & (dne > dnw) & (dne > dnn);
                int left = westOut ? nn : nw, right = eastOut ? nn : ne;
                pred = (left + right + (2 * n)) >> 2;
                v1 = n - nn;
                v2 = nw - n;
                m = dnw + dnn;
                if (PredictorOnly(k, first, p))
                {
                    Set(line, p, pred);
                    return;
                }
            }
            else
            {
                int wc = Cell(line, p - 1), ec = Cell(line, p + 1);
                bool extreme = Math.Sign(n - nw) * Math.Sign(n - ne) > 0;
                int sum = wc + ec;
                pred = extreme ? (sum + (2 * n)) >> 2 : sum >> 1;
                v1 = n - nw;
                v2 = nw - wc;
                m = Math.Abs(n - nw) + Math.Abs(nw - wc);
            }

            // Table and context set (spec, sections 7.4 and 9.2).
            int set = ((even ? 0 : 3) + (k % 3));
            QTable t = _main;
            int[] a = _a, nArr = _n;
            int baseIndex = set * 41;
            if (!_h.Lossless)
            {
                for (int i = 1; i <= 3 && i <= _main.Q; i++)
                {
                    if (m <= 4 + i) // the selection limits 5, 6 and 7
                    {
                        t = _small[i - 1];
                        a = _sa;
                        nArr = _sn;
                        baseIndex = ((set * 3) + (i - 1)) * 5;
                        break;
                    }
                }
            }

            int g = (t.Mult * t.Of(v1)) + t.Of(v2);
            int ctx = baseIndex + Math.Abs(g);
            int z = Zeros();
            int code;
            if (z < _maxBits - t.RawBits - 1)
            {
                // The least kk (at most 15) with n << kk at least a: n's top bit shifted to a's, or one more.
                int count = nArr[ctx], sum = a[ctx], kk = 0;
                if (count < sum)
                {
                    kk = BitOperations.LeadingZeroCount((uint)count) - BitOperations.LeadingZeroCount((uint)sum);
                    kk = Math.Min(15, count << kk < sum ? kk + 1 : kk);
                }

                code = (z << kk) + (int)Read(kk);
            }
            else
            {
                code = (int)Read(t.RawBits) + 1;
            }

            if (code >= t.Total || _pos > _data.Length + 16)
            {
                throw new InvalidDataException($"a code outside the table at line {line}, position {p}");
            }

            // Even codes 2m are m, odd codes 2m + 1 are −(m + 1).
            int e = (code >> 1) ^ -(code & 1);
            a[ctx] += Math.Abs(e);
            if (nArr[ctx] == 64)
            {
                a[ctx] >>= 1;
                nArr[ctx] >>= 1;
            }

            nArr[ctx]++;
            int sign = g >> 31, step = e * t.Step;
            int value = pred + ((step ^ sign) - sign);
            if (value < -t.Q)
            {
                value += t.Total * t.Step;
            }
            else if (value > _max + t.Q)
            {
                value -= t.Total * t.Step;
            }

            Set(line, p, Math.Clamp(value, 0, _max));
        }

        /// <summary>Writes the 6 raw rows of a line from the colour lines (spec, section 8.3).</summary>
        private readonly void Copy(int line)
        {
            int x0 = _strip * BlockWidth;
            int width = Math.Min(BlockWidth, _h.Width - x0);
            Span<int> sources = stackalloc int[6];
            for (int r = 0; r < 6; r++)
            {
                Span<ushort> row = _pixels.U16Row((line * 6) + r).Slice(x0, width);
                if (_h.XTransType)
                {
                    // Each 6 columns hold cells 4c to 4c + 3: columns 0, 1 and 2 cells 4c, 4c + 1, 4c + 1, columns 3 to 5
                    // cells 4c + 2, 4c + 3, 4c + 3, each from its colour's line.
                    for (int i = 0; i < 6; i++)
                    {
                        sources[i] = Source(XTrans[(r * 6) + i], r);
                    }

                    for (int x = 0; x < width; x++)
                    {
                        int i = x % 6;
                        int idx = (2 * (x / 3)) + (x % 3 == 0 ? 0 : 1);
                        row[x] = (ushort)Cell(sources[i], idx);
                    }
                }
                else
                {
                    // Bayer: even rows red and green, odd rows green and blue; columns 2c and 2c + 1 both read cell c.
                    int even = Source((r & 1) == 0 ? 0 : 1, r), odd = Source((r & 1) == 0 ? 1 : 2, r);
                    int x = 0;
                    for (; x + 1 < width; x += 2)
                    {
                        row[x] = (ushort)Cell(even, x >> 1);
                        row[x + 1] = (ushort)Cell(odd, x >> 1);
                    }

                    if (x < width)
                    {
                        row[x] = (ushort)Cell(even, x >> 1);
                    }
                }
            }
        }

        /// <summary>The colour line a raw row's samples of a colour come from.</summary>
        private static int Source(int colour, int r) => colour switch { 0 => R0 + 2 + (r / 2), 1 => G0 + 2 + r, _ => B0 + 2 + (r / 2) };

        private int Zeros()
        {
            int zeros = 0;
            while (true)
            {
                Fill();
                int lead = BitOperations.LeadingZeroCount(_buffer);
                if (lead < 64 && lead < _count)
                {
                    zeros += lead;
                    Skip(lead + 1);
                    return zeros;
                }

                zeros += _count;
                Skip(_count);
                if (zeros > 64 || _pos > _data.Length + 16)
                {
                    throw new InvalidDataException("a run of zero bits too long to be data");
                }
            }
        }

        private uint Read(int n)
        {
            if (n == 0)
            {
                return 0;
            }

            Fill();
            uint v = (uint)(_buffer >> (64 - n));
            Skip(n);
            return v;
        }

        private void Skip(int n)
        {
            _buffer = n >= 64 ? 0 : _buffer << n;
            _count -= n;
        }

        private void Fill()
        {
            if (_count > 56)
            {
                return;
            }

            if (_pos + 8 <= _data.Length)
            {
                // As many whole bytes as fit, from one big-endian read: the same bits as a byte at a time.
                int bytes = (64 - _count) >> 3;
                ulong word = BinaryPrimitives.ReadUInt64BigEndian(_data[_pos..]) >> _count;
                int kept = _count + (bytes * 8);
                _buffer |= kept == 64 ? word : word & ~(ulong.MaxValue >> kept);
                _pos += bytes;
                _count = kept;
                return;
            }

            while (_count <= 56)
            {
                ulong b = _pos < _data.Length ? _data[_pos] : 0UL;
                _pos++;
                _buffer |= b << (56 - _count);
                _count += 8;
            }
        }
    }
}
