namespace Egami.Formats.Jbig2;

/// <summary>The parameters of the symbol dictionary decoding procedure (T.88 6.5.2, Table 13).</summary>
internal sealed class Jbig2SymbolParams
{
    internal bool Huffman { get; init; }

    internal bool RefAgg { get; init; }

    internal IReadOnlyList<Jbig2Bitmap> Input { get; init; } = [];

    internal int NewSymbols { get; init; }

    internal int Exported { get; init; }

    internal Jbig2HuffmanTable? HuffDh { get; init; }

    internal Jbig2HuffmanTable? HuffDw { get; init; }

    internal Jbig2HuffmanTable? HuffBmSize { get; init; }

    internal Jbig2HuffmanTable? HuffAggInst { get; init; }

    internal int Template { get; init; }

    internal int[] At { get; init; } = [];

    internal int RefinementTemplate { get; init; }

    internal int[] RefinementAt { get; init; } = [-1, -1, -1, -1];
}

/// <summary>The symbol dictionary (T.88 6.5) and pattern dictionary (6.7) decoding procedures.</summary>
internal static class Jbig2Dictionaries
{
    /// <summary>Decodes a symbol dictionary and returns its exported symbols (SDEXSYMS).</summary>
    internal static Jbig2Bitmap[] DecodeSymbols(Jbig2SymbolParams p, Jbig2ArithState arith, Jbig2BitReader? bits, Jbig2Budget budget)
    {
        long total = (long)p.Input.Count + p.NewSymbols;
        budget.Charge(total * 16);
        Jbig2Bitmap[] newSymbols = new Jbig2Bitmap[p.NewSymbols];
        int[]? widths = p.Huffman && !p.RefAgg ? new int[p.NewSymbols] : null;

        // 6.5.8.2.3: symbol IDs within a dictionary are coded with a fixed length for all symbols it could refer to.
        int codeLength = CeilLog2(total);
        if (!p.Huffman && p.RefAgg)
        {
            arith.Iaid = new Jbig2SymbolIdDecoder(codeLength);
        }

        long height = 0;
        int decoded = 0;
        while (decoded < p.NewSymbols)
        {
            arith.CheckExhausted(bits);
            height += p.Huffman ? p.HuffDh!.DecodeInt(bits!) : arith.Iadh.DecodeInt(arith.Mq);
            if (height < 0 || height > budget.Limits.MaxHeight)
            {
                throw new InvalidDataException("A symbol dictionary's height class has an impossible height.");
            }

            long width = 0;
            long totalWidth = 0;
            int first = decoded;
            while (true)
            {
                long dw = p.Huffman ? p.HuffDw!.Decode(bits!) : arith.Iadw.Decode(arith.Mq);
                if (dw == Jbig2HuffmanTable.Oob)
                {
                    break;
                }

                if (decoded >= p.NewSymbols)
                {
                    throw new InvalidDataException("A symbol dictionary holds more symbols than its header says.");
                }

                width += dw;
                totalWidth += width;
                if (width < 0 || width > budget.Limits.MaxWidth || totalWidth > int.MaxValue)
                {
                    throw new InvalidDataException("A symbol has an impossible width.");
                }

                if (widths is not null)
                {
                    widths[decoded] = (int)width;
                }
                else
                {
                    newSymbols[decoded] = p.RefAgg
                        ? RefAggSymbol(p, arith, bits, newSymbols, decoded, (int)width, (int)height, codeLength, budget)
                        : DirectSymbol(p, arith, (int)width, (int)height, budget);
                }

                decoded++;
                arith.CheckExhausted(bits);
            }

            if (widths is not null)
            {
                Collective(p, bits!, newSymbols, widths, first, decoded, (int)totalWidth, (int)height, budget);
            }
        }

        // 6.5.10: export flags as alternating runs over the input symbols and the new ones.
        Jbig2Bitmap[] exported = new Jbig2Bitmap[p.Exported];
        long index = 0;
        int flag = 0;
        int count = 0;
        while (index < total)
        {
            long run = p.Huffman ? Jbig2HuffmanTable.Standard(1).DecodeInt(bits!) : arith.Iaex.DecodeInt(arith.Mq);
            if (run < 0 || index + run > total)
            {
                throw new InvalidDataException("A symbol dictionary's export runs pass its symbols.");
            }

            if (flag != 0)
            {
                if (count + run > p.Exported)
                {
                    throw new InvalidDataException("A symbol dictionary exports more symbols than its header says.");
                }

                for (long i = index; i < index + run; i++)
                {
                    exported[count++] = i < p.Input.Count ? p.Input[(int)i] : newSymbols[i - p.Input.Count];
                }
            }

            index += run;
            flag ^= 1;
            arith.CheckExhausted(bits);
        }

        if (count != p.Exported)
        {
            throw new InvalidDataException("A symbol dictionary exports fewer symbols than its header says.");
        }

        return exported;
    }

    /// <summary>Returns ceil(log2(<paramref name="n"/>)), 0 for n of 1 or less.</summary>
    internal static int CeilLog2(long n)
    {
        int bits = 0;
        while ((1L << bits) < n)
        {
            bits++;
        }

        return bits;
    }

    private static Jbig2Bitmap DirectSymbol(Jbig2SymbolParams p, Jbig2ArithState arith, int width, int height, Jbig2Budget budget)
    {
        budget.Reserve(width, height);
        Jbig2GenericParams generic = new()
        {
            Width = width,
            Height = height,
            Template = p.Template,
            At = p.At,
        };
        return Jbig2Generic.DecodeArithmetic(generic, arith.Mq, arith.Gb);
    }

    private static Jbig2Bitmap RefAggSymbol(
        Jbig2SymbolParams p, Jbig2ArithState arith, Jbig2BitReader? bits, Jbig2Bitmap[] newSymbols, int decoded, int width, int height, int codeLength, Jbig2Budget budget)
    {
        int instances = p.Huffman ? p.HuffAggInst!.DecodeInt(bits!) : arith.Iaai.DecodeInt(arith.Mq);
        Jbig2Bitmap[] symbols = new Jbig2Bitmap[p.Input.Count + decoded];
        for (int i = 0; i < p.Input.Count; i++)
        {
            symbols[i] = p.Input[i];
        }

        Array.Copy(newSymbols, 0, symbols, p.Input.Count, decoded);
        if (instances > 1)
        {
            Jbig2TextParams text = new()
            {
                Huffman = p.Huffman,
                Refine = true,
                Width = width,
                Height = height,
                Instances = instances,
                Strips = 1,
                Symbols = symbols,
                SymbolCodeLength = Math.Max(codeLength, 1),
                Combine = Jbig2Combine.Or,
                RefCorner = 1,
                HuffFs = p.Huffman ? Jbig2HuffmanTable.Standard(6) : null,
                HuffDs = p.Huffman ? Jbig2HuffmanTable.Standard(8) : null,
                HuffDt = p.Huffman ? Jbig2HuffmanTable.Standard(11) : null,
                HuffRdw = p.Huffman ? Jbig2HuffmanTable.Standard(15) : null,
                HuffRdh = p.Huffman ? Jbig2HuffmanTable.Standard(15) : null,
                HuffRdx = p.Huffman ? Jbig2HuffmanTable.Standard(15) : null,
                HuffRdy = p.Huffman ? Jbig2HuffmanTable.Standard(15) : null,
                HuffRsize = p.Huffman ? Jbig2HuffmanTable.Standard(1) : null,
                RefinementTemplate = p.RefinementTemplate,
                RefinementAt = p.RefinementAt,
            };
            return Jbig2Text.Decode(text, arith, bits, budget);
        }

        if (instances != 1)
        {
            throw new InvalidDataException("An aggregate symbol has no instances.");
        }

        // 6.5.8.2.2: one instance, refined.
        int id = p.Huffman ? (int)bits!.ReadBits(Math.Max(codeLength, 1)) : arith.Iaid!.Decode(arith.Mq);
        if ((uint)id >= (uint)symbols.Length)
        {
            throw new InvalidDataException("A refined symbol names a symbol that is not defined yet.");
        }

        int rdx = p.Huffman ? Jbig2HuffmanTable.Standard(15).DecodeInt(bits!) : arith.Iardx.DecodeInt(arith.Mq);
        int rdy = p.Huffman ? Jbig2HuffmanTable.Standard(15).DecodeInt(bits!) : arith.Iardy.DecodeInt(arith.Mq);
        budget.Reserve(width, height);
        Jbig2RefinementParams refine = new()
        {
            Width = width,
            Height = height,
            Template = p.RefinementTemplate,
            Reference = symbols[id],
            ReferenceDx = rdx,
            ReferenceDy = rdy,
            At = p.RefinementAt,
        };
        if (!p.Huffman)
        {
            return Jbig2Refinement.Decode(refine, arith.Mq, arith.Gr);
        }

        long size = Jbig2HuffmanTable.Standard(1).Decode(bits!);
        bits!.Align();
        int start = bits.BytePosition;
        if (size < 0 || start + size > bits.End)
        {
            throw new InvalidDataException("A refined symbol's data runs past the segment.");
        }

        arith.Mq.Start(bits.Data, start, (int)size);
        Jbig2Bitmap refined = Jbig2Refinement.Decode(refine, arith.Mq, arith.Gr);
        bits.Seek(start + (int)size);
        return refined;
    }

    /// <summary>Decodes a height class collective bitmap (6.5.9) and splits it into the class's symbols.</summary>
    private static void Collective(Jbig2SymbolParams p, Jbig2BitReader bits, Jbig2Bitmap[] symbols, int[] widths, int first, int end, int totalWidth, int height, Jbig2Budget budget)
    {
        long size = p.HuffBmSize!.Decode(bits);
        bits.Align();
        int start = bits.BytePosition;
        int rowBytes = (totalWidth + 7) / 8;
        long length = size == 0 ? (long)rowBytes * height : size;
        if (size < 0 || start + length > bits.End)
        {
            throw new InvalidDataException("A height class collective bitmap runs past the segment.");
        }

        Jbig2Bitmap collective;
        budget.Reserve(totalWidth, height);
        if (size == 0)
        {
            collective = new Jbig2Bitmap(totalWidth, height);
            Jbig2Generic.FromBytes(collective, bits.Data.AsSpan(start, (int)length), rowBytes);
        }
        else
        {
            collective = Jbig2Generic.DecodeMmr(totalWidth, height, bits.Data.AsSpan(start, (int)length)).Bitmap;
        }

        bits.Seek(start + (int)length);
        int x = 0;
        for (int i = first; i < end; i++)
        {
            budget.Reserve(widths[i], height);
            symbols[i] = collective.Crop(x, 0, widths[i], height);
            x += widths[i];
        }
    }

    /// <summary>Decodes a pattern dictionary (6.7): GRAYMAX + 1 patterns cut from one collective bitmap.</summary>
    internal static Jbig2Bitmap[] DecodePatterns(bool mmr, int template, int width, int height, long grayMax, byte[] data, int start, int end, Jbig2ArithState arith, Jbig2Budget budget)
    {
        long count = grayMax + 1;
        long total = count * width;
        if (width == 0 || height == 0 || total > int.MaxValue)
        {
            throw new InvalidDataException("A pattern dictionary has an impossible size.");
        }

        budget.Reserve(total, height);
        budget.Charge(count * 16);
        Jbig2Bitmap collective;
        if (mmr)
        {
            collective = Jbig2Generic.DecodeMmr((int)total, height, data.AsSpan(start, end - start)).Bitmap;
        }
        else
        {
            int[] at = template == 0 ? [-width, 0, -3, -1, 2, -2, -2, -2] : [-width, 0];
            arith.Mq.Start(data, start, end - start);
            Jbig2GenericParams generic = new()
            {
                Width = (int)total,
                Height = height,
                Template = template,
                At = at,
            };
            collective = Jbig2Generic.DecodeArithmetic(generic, arith.Mq, arith.Gb);
        }

        Jbig2Bitmap[] patterns = new Jbig2Bitmap[count];
        for (int g = 0; g < count; g++)
        {
            budget.Reserve(width, height);
            patterns[g] = collective.Crop(g * width, 0, width, height);
        }

        return patterns;
    }
}
