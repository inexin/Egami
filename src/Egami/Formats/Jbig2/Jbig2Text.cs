namespace Egami.Formats.Jbig2;

/// <summary>The parameters of the text region decoding procedure (T.88 6.4.2, Table 9).</summary>
internal sealed class Jbig2TextParams
{
    internal bool Huffman { get; init; }

    internal bool Refine { get; init; }

    internal int Width { get; init; }

    internal int Height { get; init; }

    internal long Instances { get; init; }

    internal int Strips { get; init; } = 1;

    internal IReadOnlyList<Jbig2Bitmap> Symbols { get; init; } = [];

    /// <summary>Gets the symbol ID Huffman table (SBSYMCODES), or <see langword="null"/> for fixed-length IDs of <see cref="SymbolCodeLength"/> bits.</summary>
    internal Jbig2HuffmanTable? SymbolCodes { get; init; }

    internal int SymbolCodeLength { get; init; }

    internal int DefaultPixel { get; init; }

    internal Jbig2Combine Combine { get; init; }

    internal bool Transposed { get; init; }

    /// <summary>Gets the reference corner: 0 bottom left, 1 top left, 2 bottom right, 3 top right.</summary>
    internal int RefCorner { get; init; }

    internal int DsOffset { get; init; }

    internal Jbig2HuffmanTable? HuffFs { get; init; }

    internal Jbig2HuffmanTable? HuffDs { get; init; }

    internal Jbig2HuffmanTable? HuffDt { get; init; }

    internal Jbig2HuffmanTable? HuffRdw { get; init; }

    internal Jbig2HuffmanTable? HuffRdh { get; init; }

    internal Jbig2HuffmanTable? HuffRdx { get; init; }

    internal Jbig2HuffmanTable? HuffRdy { get; init; }

    internal Jbig2HuffmanTable? HuffRsize { get; init; }

    internal int RefinementTemplate { get; init; }

    internal int[] RefinementAt { get; init; } = [-1, -1, -1, -1];
}

/// <summary>The text region decoding procedure (T.88 6.4): symbol instances placed by strips, each optionally refined.</summary>
internal static class Jbig2Text
{
    private const int BottomLeft = 0;
    private const int TopLeft = 1;
    private const int BottomRight = 2;
    private const int TopRight = 3;

    /// <summary>
    /// Decodes a text region with arithmetic coding (<paramref name="arith"/>) or Huffman coding (<paramref name="bits"/>; the
    /// arithmetic state then serves only refinements).
    /// </summary>
    internal static Jbig2Bitmap Decode(Jbig2TextParams p, Jbig2ArithState arith, Jbig2BitReader? bits, Jbig2Budget budget)
    {
        Jbig2Bitmap region = budget.Bitmap(p.Width, p.Height);
        if (p.DefaultPixel != 0)
        {
            region.Fill(1);
        }

        bool huffman = p.Huffman;
        int logStrips = p.Strips switch
        {
            1 => 0,
            2 => 1,
            4 => 2,
            _ => 3,
        };
        long stripT = -(Read(huffman ? p.HuffDt : null, arith.Iadt, arith, bits) * (long)p.Strips);
        long firstS = 0;
        long instances = 0;
        while (instances < p.Instances)
        {
            long dt = Read(huffman ? p.HuffDt : null, arith.Iadt, arith, bits) * (long)p.Strips;
            stripT += dt;
            long curS = 0;
            bool first = true;
            while (true)
            {
                if (first)
                {
                    firstS += Read(huffman ? p.HuffFs : null, arith.Iafs, arith, bits);
                    curS = firstS;
                    first = false;
                }
                else
                {
                    long ids = huffman ? p.HuffDs!.Decode(bits!) : arith.Iads.Decode(arith.Mq);
                    if (ids == Jbig2IntegerDecoder.Oob)
                    {
                        break;
                    }

                    curS += ids + p.DsOffset;
                }

                if (instances >= p.Instances)
                {
                    // More instances than the header says would draw outside the decode's bounds.
                    throw new InvalidDataException("A text region holds more symbol instances than its header says.");
                }

                long curT = p.Strips == 1 ? 0 : huffman ? bits!.ReadBits(logStrips) : arith.Iait.DecodeInt(arith.Mq);
                long ti = stripT + curT;
                int id = huffman
                    ? (p.SymbolCodes is null ? (int)bits!.ReadBits(p.SymbolCodeLength) : p.SymbolCodes.DecodeInt(bits!))
                    : arith.Iaid!.Decode(arith.Mq);
                if ((uint)id >= (uint)p.Symbols.Count)
                {
                    throw new InvalidDataException("A symbol instance names a symbol that is not in the dictionaries.");
                }

                Jbig2Bitmap symbol = InstanceBitmap(p, arith, bits, p.Symbols[id], budget);
                int w = symbol.Width;
                int h = symbol.Height;
                if (!p.Transposed && p.RefCorner is TopRight or BottomRight)
                {
                    curS += w - 1;
                }
                else if (p.Transposed && p.RefCorner is BottomLeft or BottomRight)
                {
                    curS += h - 1;
                }

                long si = curS;
                (long x, long y) = p.Transposed ? (ti, si) : (si, ti);
                if (p.RefCorner is TopRight or BottomRight)
                {
                    x -= w - 1;
                }

                if (p.RefCorner is BottomLeft or BottomRight)
                {
                    y -= h - 1;
                }

                if (x > -w && x < p.Width && y > -h && y < p.Height)
                {
                    region.Compose(symbol, (int)x, (int)y, p.Combine);
                }

                if (!p.Transposed && p.RefCorner is TopLeft or BottomLeft)
                {
                    curS += w - 1;
                }
                else if (p.Transposed && p.RefCorner is TopLeft or TopRight)
                {
                    curS += h - 1;
                }

                instances++;
                if (Math.Abs(curS) > int.MaxValue || Math.Abs(stripT) > int.MaxValue)
                {
                    throw new InvalidDataException("A text region's coordinates run out of range.");
                }
            }
        }

        return region;
    }

    /// <summary>Decodes the symbol instance bitmap (6.4.11): the symbol itself, or a refinement of it.</summary>
    private static Jbig2Bitmap InstanceBitmap(Jbig2TextParams p, Jbig2ArithState arith, Jbig2BitReader? bits, Jbig2Bitmap symbol, Jbig2Budget budget)
    {
        if (!p.Refine)
        {
            return symbol;
        }

        int ri = p.Huffman ? bits!.ReadBit() : arith.Iari.DecodeInt(arith.Mq);
        if (ri == 0)
        {
            return symbol;
        }

        long rdw;
        long rdh;
        long rdx;
        long rdy;
        if (p.Huffman)
        {
            rdw = p.HuffRdw!.DecodeInt(bits!);
            rdh = p.HuffRdh!.DecodeInt(bits!);
            rdx = p.HuffRdx!.DecodeInt(bits!);
            rdy = p.HuffRdy!.DecodeInt(bits!);
        }
        else
        {
            rdw = arith.Iardw.DecodeInt(arith.Mq);
            rdh = arith.Iardh.DecodeInt(arith.Mq);
            rdx = arith.Iardx.DecodeInt(arith.Mq);
            rdy = arith.Iardy.DecodeInt(arith.Mq);
        }

        long width = symbol.Width + rdw;
        long height = symbol.Height + rdh;
        if (width < 0 || height < 0)
        {
            throw new InvalidDataException("A symbol instance refinement has a negative size.");
        }

        budget.Reserve(width, height);
        Jbig2RefinementParams refine = new()
        {
            Width = (int)width,
            Height = (int)height,
            Template = p.RefinementTemplate,
            Reference = symbol,
            ReferenceDx = (int)((rdw >> 1) + rdx),
            ReferenceDy = (int)((rdh >> 1) + rdy),
            At = p.RefinementAt,
        };
        if (!p.Huffman)
        {
            return Jbig2Refinement.Decode(refine, arith.Mq, arith.Gr);
        }

        long size = p.HuffRsize!.Decode(bits!);
        bits!.Align();
        int start = bits.BytePosition;
        if (size < 0 || start + size > bits.End)
        {
            throw new InvalidDataException("A symbol instance refinement's data runs past the segment.");
        }

        arith.Mq.Start(bits.Data, start, (int)size);
        Jbig2Bitmap refined = Jbig2Refinement.Decode(refine, arith.Mq, arith.Gr);
        bits.Seek(start + (int)size);
        return refined;
    }

    private static long Read(Jbig2HuffmanTable? table, Jbig2IntegerDecoder decoder, Jbig2ArithState arith, Jbig2BitReader? bits)
    {
        return table is not null ? table.DecodeInt(bits!) : decoder.DecodeInt(arith.Mq);
    }

    /// <summary>
    /// Reads the symbol ID Huffman table of a text region (7.4.3.1.7): run-length coded code lengths for
    /// <paramref name="symbols"/> symbols, then a byte boundary.
    /// </summary>
    internal static Jbig2HuffmanTable ReadSymbolCodes(Jbig2BitReader bits, int symbols)
    {
        (long, int, int)[] runLines = new (long, int, int)[35];
        for (int i = 0; i < 35; i++)
        {
            runLines[i] = (i, (int)bits.ReadBits(4), 0);
        }

        Jbig2HuffmanTable runCodes = Jbig2HuffmanTable.Create(runLines, 0, (0, 35), -1);
        (long, int, int)[] lines = new (long, int, int)[symbols];
        int at = 0;
        while (at < symbols)
        {
            int code = runCodes.DecodeInt(bits);
            int repeat;
            int length;
            if (code < 32)
            {
                repeat = 1;
                length = code;
            }
            else if (code == 32)
            {
                if (at == 0)
                {
                    throw new InvalidDataException("A symbol ID code length run copies a length before the first.");
                }

                repeat = (int)bits.ReadBits(2) + 3;
                length = lines[at - 1].Item2;
            }
            else
            {
                repeat = code == 33 ? (int)bits.ReadBits(3) + 3 : (int)bits.ReadBits(7) + 11;
                length = 0;
            }

            if (at + repeat > symbols)
            {
                throw new InvalidDataException("The symbol ID code lengths run past the number of symbols.");
            }

            for (int i = 0; i < repeat; i++)
            {
                lines[at] = (at, length, 0);
                at++;
            }

            if (bits.Overrun)
            {
                throw new InvalidDataException("The symbol ID Huffman table runs past the segment.");
            }
        }

        bits.Align();
        return Jbig2HuffmanTable.Create(lines, 0, (0, symbols), -1);
    }
}
