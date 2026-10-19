using Egami.Barcodes.ErrorCorrection;

namespace Egami.Barcodes.Matrix;

/// <summary>
/// Data Matrix ECC 200 (ISO/IEC 16022, DMRE per ISO/IEC 21471). The encodation is a shortest-path search over the six modes
/// (ASCII, C40, Text, X12, EDIFACT, Base 256) with the end-of-data rules of section 5.2, translated from zint's minimal
/// encodation (backend/dmatrix.c, which ports Alex Geller's algorithm; BSD-3, see THIRD-PARTY-NOTICES.md) so that symbols
/// equal zint's. Error correction blocks are interleaved, and 144 x 144 uses the de facto placement readers expect.
/// </summary>
internal static class DataMatrixEncoder
{
    private const int Ascii = 1, C40 = 2, Text = 3, X12 = 4, Edifact = 5, Base256 = 6, ModeCount = 6;

    /// <summary>Which sizes a symbol may take.</summary>
    public readonly record struct Shapes(bool Square, bool Rectangle, bool Dmre);

    /// <summary>The data to encode: bytes, an ECI, GS1 (FNC1 first and for each GS separator).</summary>
    public readonly record struct Input(byte[] Bytes, int Eci, bool Gs1);

    /// <summary>Encodes the input as the smallest allowed symbol, or returns <see langword="null"/> when it does not fit.</summary>
    public static (DataMatrixTables.Size Size, BarcodeModules Modules)? Encode(Input input, Shapes shapes)
    {
        var encoder = new Encoding(input, shapes);
        List<byte>? codewords = encoder.Run();
        if (codewords is null || SizeIndex(shapes, codewords.Count) is not int index)
        {
            return null;
        }

        DataMatrixTables.Size size = DataMatrixTables.Sizes[index];
        byte[] data = new byte[size.Data + (size.CheckPerBlock * ((size.Data + 2) / size.DataPerBlock))];
        codewords.CopyTo(data);
        AddPadding(data, codewords.Count, size.Data);
        AddCheckCodewords(data, size, skew: index == DataMatrixTables.Sizes.Length - 1);
        return (size, Place(data, size));
    }

    /// <summary>Returns the index of the smallest allowed size with at least this many data codewords, or <see langword="null"/>.</summary>
    internal static int? SizeIndex(Shapes shapes, int minimum)
    {
        for (int i = 0; i < DataMatrixTables.Sizes.Length; i++)
        {
            DataMatrixTables.Size size = DataMatrixTables.Sizes[i];
            bool allowed = size.IsSquare ? shapes.Square : size.Dmre ? shapes.Rectangle && shapes.Dmre : shapes.Rectangle;
            if (allowed && size.Data >= minimum)
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>
    /// The codewords left after <paramref name="written"/> in the smallest allowed size for those and <paramref name="pending"/>
    /// more (negative when none is large enough).
    /// </summary>
    private static int CodewordsRemaining(Shapes shapes, int written, int pending = 0) =>
        (SizeIndex(shapes, written + pending) is int i ? DataMatrixTables.Sizes[i].Data : DataMatrixTables.Sizes[^1].Data) - written;

    /// <summary>Pads with 129 and then the 253-state randomized pad (Annex B.1.1).</summary>
    private static void AddPadding(byte[] data, int used, int capacity)
    {
        if (used == capacity)
        {
            return;
        }

        data[used] = 129;
        for (int tp = used + 1; tp < capacity; tp++)
        {
            int prn = ((149 * (tp + 1)) % 253) + 1, value = 129 + prn;
            data[tp] = (byte)(value <= 254 ? value : value - 254);
        }
    }

    /// <summary>Adds the Reed-Solomon blocks, data interleaved across them; 144 x 144 rotates its blocks as zint and other writers do.</summary>
    private static void AddCheckCodewords(byte[] data, DataMatrixTables.Size size, bool skew)
    {
        int bytes = size.Data, blocks = (bytes + 2) / size.DataPerBlock, checks = size.CheckPerBlock * blocks;
        int[] check = new int[size.CheckPerBlock];
        Span<int> block = stackalloc int[(bytes / blocks) + 1];
        for (int b = 0; b < blocks; b++)
        {
            int count = 0;
            for (int n = b; n < bytes; n += blocks)
            {
                block[count++] = data[n];
            }

            ReedSolomon.Encode(GaloisField.DataMatrix, block[..count], check);
            for (int n = b, p = 0; n < checks; n += blocks, p++)
            {
                data[bytes + (skew ? (b < 8 ? n + 2 : n - 8) : n)] = (byte)check[p];
            }
        }
    }

    /// <summary>Lays out the finder (solid left and bottom, alternating top and right) per data region and the codewords (Annex F).</summary>
    private static BarcodeModules Place(byte[] data, DataMatrixTables.Size size)
    {
        int h = size.Rows, w = size.Columns, fh = size.RegionRows, fw = size.RegionColumns;
        int nc = w - (2 * (w / fw)), nr = h - (2 * (h / fh));
        int[] places = Places(nr, nc);
        bool[] dark = new bool[h * w];
        void Set(int row, int column) => dark[(row * w) + column] = true;

        for (int y = 0; y < h; y += fh)
        {
            for (int x = 0; x < w; x++)
            {
                Set(h - y - 1, x);
            }

            for (int x = 0; x < w; x += 2)
            {
                Set(y, x);
            }
        }

        for (int x = 0; x < w; x += fw)
        {
            for (int y = 0; y < h; y++)
            {
                Set(h - y - 1, x);
            }

            for (int y = 0; y < h; y += 2)
            {
                Set(h - y - 1, x + fw - 1);
            }
        }

        for (int y = 0; y < nr; y++)
        {
            for (int x = 0; x < nc; x++)
            {
                int v = places[((nr - y - 1) * nc) + x];
                if (v == 1 || (v > 7 && (data[(v >> 3) - 1] & (1 << (v & 7))) != 0))
                {
                    Set(h - (1 + y + (2 * (y / (fh - 2)))) - 1, 1 + x + (2 * (x / (fw - 2))));
                }
            }
        }

        return BarcodeModules.Wrap(w, h, dark);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), int[]> s_places = new();

    /// <summary>The placement of a mapping matrix size, computed once per size; callers only read it.</summary>
    internal static int[] Places(int nr, int nc) => s_places.GetOrAdd((nr, nc), static key =>
    {
        int[] places = new int[key.Item1 * key.Item2];
        Placement(places, key.Item1, key.Item2);
        return places;
    });

    // Annex F: which codeword and bit goes in each module of the mapping matrix, as (codeword << 3) + bit.
    internal static void Placement(int[] array, int nr, int nc)
    {
        void Bit(int r, int c, int p, int b)
        {
            if (r < 0)
            {
                r += nr;
                c += 4 - ((nr + 4) % 8);
            }

            if (c < 0)
            {
                c += nc;
                r += 4 - ((nc + 4) % 8);
            }

            if (r >= nr)
            {
                r -= nr;
            }

            array[(r * nc) + c] = (p << 3) + b;
        }

        void Block(int r, int c, int p)
        {
            Bit(r - 2, c - 2, p, 7);
            Bit(r - 2, c - 1, p, 6);
            Bit(r - 1, c - 2, p, 5);
            Bit(r - 1, c - 1, p, 4);
            Bit(r - 1, c, p, 3);
            Bit(r, c - 2, p, 2);
            Bit(r, c - 1, p, 1);
            Bit(r, c, p, 0);
        }

        void Corner(ReadOnlySpan<(int R, int C)> cells, int p)
        {
            for (int i = 0; i < 8; i++)
            {
                Bit(cells[i].R, cells[i].C, p, 7 - i);
            }
        }

        int p = 1, row = 4, col = 0;
        do
        {
            if (row == nr && col == 0)
            {
                Corner([(nr - 1, 0), (nr - 1, 1), (nr - 1, 2), (0, nc - 2), (0, nc - 1), (1, nc - 1), (2, nc - 1), (3, nc - 1)], p++);
            }

            if (row == nr - 2 && col == 0 && nc % 4 != 0)
            {
                Corner([(nr - 3, 0), (nr - 2, 0), (nr - 1, 0), (0, nc - 4), (0, nc - 3), (0, nc - 2), (0, nc - 1), (1, nc - 1)], p++);
            }

            if (row == nr - 2 && col == 0 && nc % 8 == 4)
            {
                Corner([(nr - 3, 0), (nr - 2, 0), (nr - 1, 0), (0, nc - 2), (0, nc - 1), (1, nc - 1), (2, nc - 1), (3, nc - 1)], p++);
            }

            if (row == nr + 4 && col == 2 && nc % 8 == 0)
            {
                Corner([(nr - 1, 0), (nr - 1, nc - 1), (0, nc - 3), (0, nc - 2), (0, nc - 1), (1, nc - 3), (1, nc - 2), (1, nc - 1)], p++);
            }

            do
            {
                if (row < nr && col >= 0 && array[(row * nc) + col] == 0)
                {
                    Block(row, col, p++);
                }

                row -= 2;
                col += 2;
            }
            while (row >= 0 && col < nc);
            row++;
            col += 3;
            do
            {
                if (row >= 0 && col < nc && array[(row * nc) + col] == 0)
                {
                    Block(row, col, p++);
                }

                row += 2;
                col -= 2;
            }
            while (row < nr && col >= 0);
            row += 3;
            col++;
        }
        while (row < nr || col < nc);

        if (array[(nr * nc) - 1] == 0)
        {
            array[(nr * nc) - 1] = array[(nr * nc) - nc - 2] = 1;
        }
    }

    /// <summary>One encodation run: the shortest-path search for the modes, then the codewords.</summary>
    private sealed class Encoding(Input input, Shapes shapes)
    {
        private readonly byte[] _source = input.Bytes;
        private readonly bool _gs1 = input.Gs1;

        private int Length => _source.Length;

        public List<byte>? Run()
        {
            var target = new List<byte>();
            if (_gs1)
            {
                target.Add(232);
            }

            int eci = input.Eci;
            if (eci > 0)
            {
                target.Add(241);
                if (eci <= 126)
                {
                    target.Add((byte)(eci + 1));
                }
                else if (eci <= 16382)
                {
                    target.Add((byte)(((eci - 127) / 254) + 128));
                    target.Add((byte)(((eci - 127) % 254) + 1));
                }
                else
                {
                    target.Add((byte)(((eci - 16383) / 64516) + 192));
                    target.Add((byte)((((eci - 16383) / 254) % 254) + 1));
                    target.Add((byte)(((eci - 16383) % 254) + 1));
                }
            }

            return Length == 0 ? target : Encode(target);
        }

        private List<byte>? Encode(List<byte> target)
        {
            byte[] modes = DefineModes();
            int[] buffer = new int[8];
            int bufferCount = 0, b256Start = 0, sp = 0, mode = Ascii;
            while (sp < Length)
            {
                if (modes[sp] != mode)
                {
                    switch (mode)
                    {
                        case C40 or Text or X12:
                            bufferCount = 0;
                            target.Add(254);
                            break;
                        case Edifact:
                            int lastAscii = LastAscii(sp);
                            if (lastAscii == 0 || CodewordsRemaining(shapes, target.Count + lastAscii, bufferCount) > 2 - lastAscii)
                            {
                                buffer[bufferCount++] = 31;
                            }

                            bufferCount = FlushEdifact(buffer, bufferCount, target, empty: true);
                            break;
                        case Base256:
                            FinishBase256(target, b256Start);
                            break;
                    }

                    mode = modes[sp];
                    switch (mode)
                    {
                        case C40:
                            target.Add(230);
                            break;
                        case Text:
                            target.Add(239);
                            break;
                        case X12:
                            target.Add(238);
                            break;
                        case Edifact:
                            target.Add(240);
                            break;
                        case Base256:
                            target.Add(231);
                            b256Start = target.Count;
                            target.Add(0);
                            break;
                    }
                }

                byte c = _source[sp];
                switch (mode)
                {
                    case Ascii:
                        if (IsTwoDigits(sp))
                        {
                            target.Add((byte)((10 * (c - '0')) + (_source[sp + 1] - '0') + 130));
                            sp += 2;
                        }
                        else
                        {
                            AddAscii(target, c);
                            sp++;
                        }

                        break;
                    case C40 or Text:
                        ReadOnlySpan<byte> shiftTable = mode == C40 ? DataMatrixTables.C40Shift : DataMatrixTables.TextShift;
                        ReadOnlySpan<byte> valueTable = mode == C40 ? DataMatrixTables.C40Value : DataMatrixTables.TextValue;
                        int shift, value;
                        if (c >= 128)
                        {
                            buffer[bufferCount++] = 1;
                            buffer[bufferCount++] = 30;
                            (shift, value) = (shiftTable[c - 128], valueTable[c - 128]);
                        }
                        else if (_gs1 && c == 0x1D)
                        {
                            (shift, value) = (2, 27);
                        }
                        else
                        {
                            (shift, value) = (shiftTable[c], valueTable[c]);
                        }

                        if (shift != 0)
                        {
                            buffer[bufferCount++] = shift - 1;
                        }

                        buffer[bufferCount++] = value;
                        if (bufferCount >= 3)
                        {
                            bufferCount = FlushTriplets(buffer, bufferCount, target);
                        }

                        sp++;
                        break;
                    case X12:
                        buffer[bufferCount++] = c switch
                        {
                            >= (byte)'0' and <= (byte)'9' => c - '0' + 4,
                            >= (byte)'A' and <= (byte)'Z' => c - 'A' + 14,
                            13 => 0,
                            (byte)'*' => 1,
                            (byte)'>' => 2,
                            _ => 3,
                        };
                        if (bufferCount >= 3)
                        {
                            bufferCount = FlushTriplets(buffer, bufferCount, target);
                        }

                        sp++;
                        break;
                    case Edifact:
                        buffer[bufferCount++] = c >= 64 ? c - 64 : c;
                        sp++;
                        if (bufferCount >= 4)
                        {
                            bufferCount = FlushEdifact(buffer, bufferCount, target, empty: false);
                        }

                        break;
                    default:
                        target.Add(c);
                        sp++;
                        break;
                }

                if (target.Count > 1558)
                {
                    return null;
                }
            }

            return Finish(target, mode, buffer, bufferCount, sp, b256Start);
        }

        /// <summary>Ends the last mode by the rules of section 5.2: unlatches, partial triplets and quadruplets, Base 256 length.</summary>
        private List<byte>? Finish(List<byte> target, int mode, int[] buffer, int bufferCount, int sp, int b256Start)
        {
            int left = CodewordsRemaining(shapes, target.Count, bufferCount);
            switch (mode)
            {
                case C40 or Text:
                    if (bufferCount == 0)
                    {
                        if (left > 0)
                        {
                            target.Add(254);
                        }
                    }
                    else if (bufferCount == 2 && left == 2)
                    {
                        buffer[bufferCount++] = 0;
                        FlushTriplets(buffer, bufferCount, target);
                    }
                    else if (bufferCount == 1 && left <= 2 && IsC40Text(mode, _source[Length - 1]))
                    {
                        if (left > 1)
                        {
                            target.Add(254);
                        }

                        target.Add((byte)(_source[Length - 1] + 1));
                    }
                    else
                    {
                        // Back up to the last full triplet and finish in ASCII.
                        int total = 0;
                        while (sp > 0 && bufferCount % 3 != 0)
                        {
                            sp--;
                            int count = C40TextCount(mode, _source[sp]);
                            total += count;
                            bufferCount -= count;
                        }

                        target.RemoveRange(target.Count - ((total / 3) * 2), (total / 3) * 2);
                        target.Add(254);
                        for (; sp < Length; sp++)
                        {
                            if (IsTwoDigits(sp))
                            {
                                target.Add((byte)((10 * (_source[sp] - '0')) + (_source[sp + 1] - '0') + 130));
                                sp++;
                            }
                            else
                            {
                                AddAscii(target, _source[sp]);
                            }
                        }
                    }

                    break;
                case X12:
                    if (left == 1 && bufferCount == 1)
                    {
                        target.Add((byte)(_source[Length - 1] + 1));
                    }
                    else
                    {
                        if (left > 0)
                        {
                            target.Add(254);
                        }

                        if (bufferCount == 2)
                        {
                            target.Add((byte)(_source[Length - 2] + 1));
                        }

                        if (bufferCount >= 1)
                        {
                            target.Add((byte)(_source[Length - 1] + 1));
                        }
                    }

                    break;
                case Edifact:
                    if (left <= 2 && bufferCount <= left)
                    {
                        if (bufferCount == 2)
                        {
                            target.Add((byte)(_source[Length - 2] + 1));
                        }

                        if (bufferCount >= 1)
                        {
                            target.Add((byte)(_source[Length - 1] + 1));
                        }
                    }
                    else
                    {
                        if (bufferCount <= 3)
                        {
                            buffer[bufferCount++] = 31;
                        }

                        FlushEdifact(buffer, bufferCount, target, empty: true);
                    }

                    break;
                case Base256:
                    if (left > 0)
                    {
                        UpdateBase256Length(target, b256Start);
                    }

                    Randomize255(target, b256Start);
                    break;
            }

            return target.Count > 1558 ? null : target;
        }

        private void AddAscii(List<byte> target, byte c)
        {
            if (c >= 128)
            {
                target.Add(235);
                target.Add((byte)(c - 128 + 1));
            }
            else if (_gs1 && c == 0x1D)
            {
                target.Add(232);
            }
            else
            {
                target.Add((byte)(c + 1));
            }
        }

        private static void FinishBase256(List<byte> target, int start)
        {
            UpdateBase256Length(target, start);
            Randomize255(target, start);
        }

        private static void UpdateBase256Length(List<byte> target, int start)
        {
            int count = target.Count - (start + 1);
            if (count <= 249)
            {
                target[start] = (byte)count;
            }
            else
            {
                target[start] = (byte)(249 + (count / 250));
                target.Insert(start + 1, (byte)(count % 250));
            }
        }

        /// <summary>The 255-state randomizing of Base 256 codewords (Annex B.2.1).</summary>
        private static void Randomize255(List<byte> target, int start)
        {
            for (int i = start; i < target.Count; i++)
            {
                int prn = ((149 * (i + 1)) % 255) + 1;
                target[i] = (byte)((target[i] + prn) & 0xFF);
            }
        }

        private static int FlushTriplets(int[] buffer, int count, List<byte> target)
        {
            int full = count / 3 * 3;
            for (int i = 0; i < full; i += 3)
            {
                int value = (1600 * buffer[i]) + (40 * buffer[i + 1]) + buffer[i + 2] + 1;
                target.Add((byte)(value >> 8));
                target.Add((byte)(value & 0xFF));
            }

            Array.Copy(buffer, full, buffer, 0, count - full);
            return count - full;
        }

        private static int FlushEdifact(int[] buffer, int count, List<byte> target, bool empty)
        {
            int full = count / 4 * 4;
            for (int i = 0; i < full; i += 4)
            {
                target.Add((byte)((buffer[i] << 2) | ((buffer[i + 1] & 0x30) >> 4)));
                target.Add((byte)(((buffer[i + 1] & 0x0F) << 4) | ((buffer[i + 2] & 0x3C) >> 2)));
                target.Add((byte)(((buffer[i + 2] & 0x03) << 6) | buffer[i + 3]));
            }

            int rest = count - full;
            Array.Copy(buffer, full, buffer, 0, rest);
            if (empty && rest > 0)
            {
                target.Add((byte)((buffer[0] << 2) | (rest > 1 ? (buffer[1] & 0x30) >> 4 : 0)));
                if (rest > 1)
                {
                    target.Add((byte)(((buffer[1] & 0x0F) << 4) | (rest > 2 ? (buffer[2] & 0x3C) >> 2 : 0)));
                }

                if (rest > 2)
                {
                    target.Add((byte)((buffer[2] & 0x03) << 6));
                }

                rest = 0;
            }

            return rest;
        }

        private bool IsTwoDigits(int i) => i + 1 < Length && char.IsAsciiDigit((char)_source[i]) && char.IsAsciiDigit((char)_source[i + 1]);

        private static bool IsC40(byte c) => c <= '9' ? c >= '0' || c == ' ' : c is >= (byte)'A' and <= (byte)'Z';

        private static bool IsText(byte c) => c <= '9' ? c >= '0' || c == ' ' : c is >= (byte)'a' and <= (byte)'z';

        private static bool IsC40Text(int mode, byte c) => mode == C40 ? IsC40(c) : IsText(c);

        private static bool IsX12(byte c) => IsC40(c) || c is 13 or (byte)'*' or (byte)'>';

        private static bool IsEdifact(byte c) => c is >= (byte)' ' and <= (byte)'^';

        private int C40TextCount(int mode, byte c)
        {
            if (_gs1 && c == 0x1D)
            {
                return 2;
            }

            int count = 1;
            if (c >= 128)
            {
                count += 2;
                c -= 128;
            }

            if ((mode == C40 && DataMatrixTables.C40Shift[c] != 0) || (mode == Text && DataMatrixTables.TextShift[c] != 0))
            {
                count++;
            }

            return count;
        }

        /// <summary>Whether the 1 to 4 characters from a position are the end and fit in 1 or 2 ASCII codewords: 1 or 2, else 0.</summary>
        private int LastAscii(int from)
        {
            int rest = Length - from;
            if (rest > 4 || rest <= 0)
            {
                return 0;
            }

            bool Digit(int i) => char.IsAsciiDigit((char)_source[i]);
            bool High(int i) => _source[i] >= 128;
            return rest switch
            {
                1 => High(from) ? 0 : 1,
                2 => High(from) || High(from + 1) ? 0 : Digit(from) && Digit(from + 1) ? 1 : 2,
                3 => (Digit(from) && Digit(from + 1) && !High(from + 2)) || (Digit(from + 1) && Digit(from + 2) && !High(from)) ? 2 : 0,
                _ => Digit(from) && Digit(from + 1) && Digit(from + 2) && Digit(from + 3) ? 2 : 0,
            };
        }

        private struct Edge
        {
            public int Mode;
            public int EndMode;
            public int From;
            public int Len;
            public int Size;
            public int Bytes;
            public int Previous;
        }

        /// <summary>The shortest path over (position, mode) vertices; returns the mode for each input byte.</summary>
        private byte[] DefineModes()
        {
            var edges = new Edge[(Length + 1) * ModeCount];
            AddEdges(edges, 0, -1);
            for (int i = 1; i < Length; i++)
            {
                for (int j = 0; j < ModeCount; j++)
                {
                    if (edges[(i * ModeCount) + j].Mode != 0)
                    {
                        AddEdges(edges, i, (i * ModeCount) + j);
                    }
                }
            }

            int best = -1, bestSize = int.MaxValue;
            for (int j = 0; j < ModeCount; j++)
            {
                ref Edge edge = ref edges[(Length * ModeCount) + j];
                if (edge.Mode != 0 && edge.Size < bestSize)
                {
                    (bestSize, best) = (edge.Size, (Length * ModeCount) + j);
                }
            }

            byte[] modes = new byte[Length];
            for (int e = best; e >= 0; e = edges[e].Previous)
            {
                modes.AsSpan(edges[e].From, edges[e].Len).Fill((byte)edges[e].Mode);
            }

            return modes;
        }

        private void AddEdges(Edge[] edges, int from, int previous)
        {
            if (previous < 0 || edges[previous].EndMode != Edifact)
            {
                if (IsTwoDigits(from))
                {
                    AddEdge(edges, Ascii, from, 2, previous, 0);
                    if (previous >= 0 && edges[previous].Mode == Ascii)
                    {
                        return;
                    }
                }
                else
                {
                    AddEdge(edges, Ascii, from, 1, previous, 0);
                }

                foreach (int mode in (ReadOnlySpan<int>)[C40, Text])
                {
                    int words = C40Words(from, mode, out int len);
                    if (words != 0)
                    {
                        AddEdge(edges, mode, from, len, previous, words);
                    }
                }

                if (from + 2 < Length && IsX12(_source[from]) && IsX12(_source[from + 1]) && IsX12(_source[from + 2]))
                {
                    AddEdge(edges, X12, from, 3, previous, 0);
                }

                if (!_gs1 || _source[from] != 0x1D)
                {
                    AddEdge(edges, Base256, from, 1, previous, 0);
                }
            }

            if (IsEdifact(_source[from]))
            {
                for (int i = 1, pos = from + 1; i < 4 && pos < Length && IsEdifact(_source[pos]); i++, pos++)
                {
                    AddEdge(edges, Edifact, from, i + 1, previous, 0);
                }
            }
        }

        /// <summary>The C40 or Text codewords for whole triplets (or fewer at the end) from a position, and the characters they take.</summary>
        private int C40Words(int from, int mode, out int len)
        {
            int thirds = 0;
            for (int i = from; i < Length; i++)
            {
                byte c = _source[i];
                thirds += IsC40Text(mode, c) ? 1 : c < 128 ? 2 : IsC40Text(mode, (byte)(c & 0x7F)) ? 3 : 4;
                int remainder = thirds % 3;
                if (remainder == 0 || (remainder == 2 && i + 1 == Length))
                {
                    len = i - from + 1;
                    return (thirds + 2) / 3 * 2;
                }
            }

            len = 0;
            return 0;
        }

        private void AddEdge(Edge[] edges, int mode, int from, int len, int previous, int words)
        {
            Edge edge = NewEdge(edges, mode, from, len, previous, words);
            int index = ((from + len) * ModeCount) + edge.EndMode - 1;
            if (edges[index].Mode == 0 || edges[index].Size > edge.Size)
            {
                edges[index] = edge;
            }
        }

        private Edge NewEdge(Edge[] edges, int mode, int from, int len, int previous, int words)
        {
            var edge = new Edge { Mode = mode, EndMode = mode, From = from, Len = len, Previous = previous };
            int previousMode = previous >= 0 ? edges[previous].EndMode : Ascii;
            int size = previous >= 0 ? edges[previous].Size : 0;
            bool fromAsciiOrBase256 = previousMode is Ascii or Base256;
            switch (mode)
            {
                case Ascii:
                    size += _source[from] >= 128 ? 2 : 1;
                    size += fromAsciiOrBase256 ? 0 : 1;
                    break;
                case Base256:
                    size++;
                    if (previousMode != Base256)
                    {
                        size += 2 + (previousMode != Ascii ? 1 : 0);
                        edge.Bytes = 1;
                    }
                    else
                    {
                        edge.Bytes = 1 + edges[previous].Bytes;
                        if (edge.Bytes == 250)
                        {
                            size++;
                        }
                    }

                    break;
                case C40 or Text:
                    size += words;
                    if (previousMode != mode)
                    {
                        size += 1 + (fromAsciiOrBase256 ? 0 : 1);
                    }

                    if (from + len + 2 >= Length)
                    {
                        int lastAscii = LastAscii(from + len);
                        if (CodewordsRemaining(shapes, size + lastAscii) > 0)
                        {
                            size++;
                        }
                    }

                    break;
                case X12:
                    size += 2;
                    if (previousMode != X12)
                    {
                        size += 1 + (fromAsciiOrBase256 ? 0 : 1);
                    }

                    if (from + len + 2 >= Length)
                    {
                        int lastAscii = LastAscii(from + len);
                        if (lastAscii == 2 || CodewordsRemaining(shapes, size + lastAscii) > 0)
                        {
                            size++;
                        }
                    }

                    break;
                default:
                    size += 3;
                    if (previousMode != Edifact)
                    {
                        size += 1 + (fromAsciiOrBase256 ? 0 : 1);
                    }

                    // A partial EDIFACT group, or one at the end where 1 or 2 ASCII codewords fit, ends in ASCII.
                    if (len < 4)
                    {
                        edge.EndMode = Ascii;
                    }
                    else
                    {
                        int lastAscii = LastAscii(from + len);
                        if (lastAscii != 0 && CodewordsRemaining(shapes, size + lastAscii) <= 2 - lastAscii)
                        {
                            edge.EndMode = Ascii;
                        }
                    }

                    break;
            }

            edge.Size = size;
            return edge;
        }
    }
}
