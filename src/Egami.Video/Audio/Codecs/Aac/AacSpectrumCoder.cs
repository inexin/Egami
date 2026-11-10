// Translated to C# from oxideav-aac (MIT), modified: the codebook cost and section search of its src/encoder.rs (band_bits,
// section_header_bits, optimize_group_sections) and the spectral writer of src/spectral_data.rs, rewritten over int buffers
// with prefix sums and with all-zero bands allowed inside coded sections. Checked against ISO/IEC 14496-3 4.6.3.

namespace Egami.Video.Audio.Codecs.Aac;

/// <summary>
/// The Huffman side of the AAC LC encoder (ISO/IEC 14496-3 4.6.3): the bit cost of a band of quantized lines in each spectral
/// codebook, the section search that picks a codebook per band and merges runs of bands into sections at the least total cost,
/// and the writer for section data and spectral data.
/// </summary>
internal static class AacSpectrumCoder
{
    /// <summary>The codebook of all-zero bands.</summary>
    public const int ZeroBook = 0;

    /// <summary>The codebook of the escape-coded pairs.</summary>
    public const int EscapeBook = 11;

    /// <summary>The noise substitution codebook.</summary>
    public const int NoiseBook = 13;

    /// <summary>The out-of-phase intensity codebook.</summary>
    public const int IntensityBook2 = 14;

    /// <summary>The in-phase intensity codebook.</summary>
    public const int IntensityBook = 15;

    private static readonly uint[][] s_codes = new uint[12][];
    private static readonly byte[][] s_lengths = new byte[12][];

    static AacSpectrumCoder()
    {
        for (int cb = 1; cb <= 11; cb++)
        {
            ReadOnlySpan<ushort> codes = cb switch
            {
                1 => AacTables.Codes1,
                2 => AacTables.Codes2,
                3 => AacTables.Codes3,
                4 => AacTables.Codes4,
                5 => AacTables.Codes5,
                6 => AacTables.Codes6,
                7 => AacTables.Codes7,
                8 => AacTables.Codes8,
                9 => AacTables.Codes9,
                10 => AacTables.Codes10,
                _ => AacTables.Codes11,
            };
            ReadOnlySpan<byte> lengths = cb switch
            {
                1 => AacTables.Lengths1,
                2 => AacTables.Lengths2,
                3 => AacTables.Lengths3,
                4 => AacTables.Lengths4,
                5 => AacTables.Lengths5,
                6 => AacTables.Lengths6,
                7 => AacTables.Lengths7,
                8 => AacTables.Lengths8,
                9 => AacTables.Lengths9,
                10 => AacTables.Lengths10,
                _ => AacTables.Lengths11,
            };
            uint[] wide = new uint[codes.Length];
            for (int i = 0; i < codes.Length; i++)
            {
                wide[i] = codes[i];
            }

            s_codes[cb] = wide;
            s_lengths[cb] = lengths.ToArray();
        }
    }

    /// <summary>Gets the largest magnitude each codebook codes without an escape (Table 4.95); 16 and above escape in codebook 11.</summary>
    public static ReadOnlySpan<short> Largest => [0, 1, 1, 2, 2, 4, 4, 7, 7, 12, 12, 8191];

    /// <summary>Gets the first codebook that codes a band whose largest magnitude is <paramref name="peak"/>, or 0 for an all-zero band.</summary>
    /// <param name="peak">The band's largest magnitude.</param>
    /// <returns>The codebook.</returns>
    public static int FirstBook(int peak) => peak switch
    {
        0 => 0,
        1 => 1,
        2 => 3,
        <= 4 => 5,
        <= 7 => 7,
        <= 12 => 9,
        _ => 11,
    };

    /// <summary>Gets the bits that code <paramref name="lines"/> in codebook <paramref name="cb"/>, 1 to 11, with signs and escapes.</summary>
    /// <param name="cb">The codebook; it must code every magnitude in the band.</param>
    /// <param name="lines">The quantized lines, a multiple of four long.</param>
    /// <returns>The bit count.</returns>
    public static int Bits(int cb, ReadOnlySpan<int> lines)
    {
        byte[] lengths = s_lengths[cb];
        int bits = 0;
        bool unsigned = IsUnsigned(cb);
        if (cb <= 4)
        {
            const int modulus = 3;
            int offset = unsigned ? 0 : 1;
            for (int k = 0; k < lines.Length; k += 4)
            {
                int a = lines[k], b = lines[k + 1], c = lines[k + 2], d = lines[k + 3];
                if (unsigned)
                {
                    bits += (a != 0 ? 1 : 0) + (b != 0 ? 1 : 0) + (c != 0 ? 1 : 0) + (d != 0 ? 1 : 0);
                    a = Math.Abs(a);
                    b = Math.Abs(b);
                    c = Math.Abs(c);
                    d = Math.Abs(d);
                }

                bits += lengths[((((((a + offset) * modulus) + b + offset) * modulus) + c + offset) * modulus) + d + offset];
            }

            return bits;
        }

        int lav = Largest[cb];
        if (cb == EscapeBook)
        {
            for (int k = 0; k < lines.Length; k += 2)
            {
                int a = Math.Abs(lines[k]), b = Math.Abs(lines[k + 1]);
                bits += (a != 0 ? 1 : 0) + (b != 0 ? 1 : 0) + EscapeBits(a) + EscapeBits(b);
                bits += lengths[(Math.Min(a, 16) * 17) + Math.Min(b, 16)];
            }

            return bits;
        }

        if (unsigned)
        {
            int modulus = lav + 1;
            for (int k = 0; k < lines.Length; k += 2)
            {
                int a = lines[k], b = lines[k + 1];
                bits += (a != 0 ? 1 : 0) + (b != 0 ? 1 : 0);
                bits += lengths[(Math.Abs(a) * modulus) + Math.Abs(b)];
            }
        }
        else
        {
            int modulus = (2 * lav) + 1;
            for (int k = 0; k < lines.Length; k += 2)
            {
                bits += lengths[((lines[k] + lav) * modulus) + lines[k + 1] + lav];
            }
        }

        return bits;
    }

    /// <summary>Writes the lines of one band in codebook <paramref name="cb"/> (4.6.3.3): codeword, sign bits, then escapes.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="cb">The codebook, 1 to 11.</param>
    /// <param name="lines">The quantized lines.</param>
    public static void Write(AacBitWriter writer, int cb, ReadOnlySpan<int> lines)
    {
        uint[] codes = s_codes[cb];
        byte[] lengths = s_lengths[cb];
        bool unsigned = IsUnsigned(cb);
        if (cb <= 4)
        {
            int offset = unsigned ? 0 : 1;
            for (int k = 0; k < lines.Length; k += 4)
            {
                int a = lines[k], b = lines[k + 1], c = lines[k + 2], d = lines[k + 3];
                int index = unsigned
                    ? (((((Math.Abs(a) * 3) + Math.Abs(b)) * 3) + Math.Abs(c)) * 3) + Math.Abs(d)
                    : (((((((a + offset) * 3) + b + offset) * 3) + c + offset) * 3) + d + offset);
                writer.Write(codes[index], lengths[index]);
                if (unsigned)
                {
                    WriteSign(writer, a);
                    WriteSign(writer, b);
                    WriteSign(writer, c);
                    WriteSign(writer, d);
                }
            }

            return;
        }

        int lav = Largest[cb];
        for (int k = 0; k < lines.Length; k += 2)
        {
            int a = lines[k], b = lines[k + 1];
            int index = cb == EscapeBook
                ? (Math.Min(Math.Abs(a), 16) * 17) + Math.Min(Math.Abs(b), 16)
                : unsigned ? (Math.Abs(a) * (lav + 1)) + Math.Abs(b) : ((a + lav) * ((2 * lav) + 1)) + b + lav;
            writer.Write(codes[index], lengths[index]);
            if (unsigned)
            {
                WriteSign(writer, a);
                WriteSign(writer, b);
            }

            if (cb == EscapeBook)
            {
                WriteEscape(writer, Math.Abs(a));
                WriteEscape(writer, Math.Abs(b));
            }
        }
    }

    /// <summary>Gets the bits of the section_data entry for a section of <paramref name="bands"/> bands (Table 4.52).</summary>
    /// <param name="bands">The section length in bands.</param>
    /// <param name="shortWindows">Whether the block has eight short windows (3-bit lengths) rather than one long one (5-bit).</param>
    /// <returns>The bit count.</returns>
    public static int SectionBits(int bands, bool shortWindows) => shortWindows ? 4 + (3 * ((bands / 7) + 1)) : 4 + (5 * ((bands / 31) + 1));

    /// <summary>Writes the section data of one window group from its per-band codebooks.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="books">The codebook of each band up to max_sfb.</param>
    /// <param name="shortWindows">Whether the block has eight short windows.</param>
    public static void WriteSections(AacBitWriter writer, ReadOnlySpan<byte> books, bool shortWindows)
    {
        int bits = shortWindows ? 3 : 5;
        int escape = (1 << bits) - 1;
        int k = 0;
        while (k < books.Length)
        {
            int cb = books[k];
            int end = k + 1;
            while (end < books.Length && books[end] == cb)
            {
                end++;
            }

            writer.Write((uint)cb, 4);
            int length = end - k;
            while (length >= escape)
            {
                writer.Write((uint)escape, bits);
                length -= escape;
            }

            writer.Write((uint)length, bits);
            k = end;
        }
    }

    /// <summary>
    /// Picks each band's codebook and the sections at the least cost (section headers plus spectral bits), by dynamic programming
    /// over where sections end. A band whose <paramref name="books"/> entry is 13 to 15 keeps it, in a section of its own kind; a
    /// band with codebook 0 may join a neighbouring coded section when coding its zeros costs less than a section break, in
    /// which case it takes that codebook and needs a scalefactor.
    /// </summary>
    /// <param name="lines">The group's quantized lines in coding order.</param>
    /// <param name="starts">The start of each band in <paramref name="lines"/>, with the end of the last one after it.</param>
    /// <param name="books">In: 0 for an all-zero band, 13 to 15 for the special codebooks, else any codebook that codes the band. Out: the chosen codebooks.</param>
    /// <param name="shortWindows">Whether the block has eight short windows.</param>
    /// <param name="scratch">Work space of at least <c>13 * (bands + 1)</c> ints.</param>
    /// <returns>The bits of the section data and spectral data.</returns>
    public static int ChooseSections(ReadOnlySpan<int> lines, ReadOnlySpan<int> starts, Span<byte> books, bool shortWindows, Span<int> scratch)
    {
        int n = books.Length;
        if (n == 0)
        {
            return 0;
        }

        // Prefix sums of each codebook's cost per band; a band a codebook cannot code costs Never, so any sum that holds one
        // is at least Never (64 bands of it stay far below int.MaxValue).
        const int Never = 1 << 24;
        Span<int> prefix = scratch[..(12 * (n + 1))];
        Span<int> best = scratch.Slice(12 * (n + 1), n + 1);
        Span<int> from = stackalloc int[n + 1];
        Span<byte> pick = stackalloc byte[n + 1];
        for (int cb = 0; cb < 12; cb++)
        {
            prefix[cb * (n + 1)] = 0;
        }

        for (int b = 0; b < n; b++)
        {
            int cb0 = books[b];
            ReadOnlySpan<int> band = lines[starts[b]..starts[b + 1]];
            int first = cb0 == 0 ? 1 : cb0 >= NoiseBook ? 12 : FirstBook(Peak(band));
            int last = cb0 >= NoiseBook ? 0 : Math.Min(first + 5, 11);
            for (int cb = 1; cb < 12; cb++)
            {
                int cost = Never;
                if (cb >= first && (cb <= last || cb == EscapeBook) && cb0 < NoiseBook)
                {
                    cost = cb0 == 0 ? Bits(cb, band) + 1 : Bits(cb, band);
                }

                int at = (cb * (n + 1)) + b;
                prefix[at + 1] = prefix[at] + cost;
            }
        }

        best[0] = 0;
        for (int b = 1; b <= n; b++)
        {
            best[b] = int.MaxValue;
            int kind = Kind(books[b - 1]);
            for (int a = b - 1; a >= 0; a--)
            {
                int header = SectionBits(b - a, shortWindows);
                int ka = Kind(books[a]);

                // A run of zero bands, or of one special codebook.
                if (kind == ka && kind != 1 && Same(books, a, b) && best[a] + header < best[b])
                {
                    best[b] = best[a] + header;
                    from[b] = a;
                    pick[b] = books[b - 1];
                }

                if (ka == 2 || kind == 2)
                {
                    if (ka != kind)
                    {
                        break;
                    }

                    continue;
                }

                // A coded section; zero bands may join it.
                for (int cb = 1; cb < 12; cb++)
                {
                    int sum = prefix[(cb * (n + 1)) + b] - prefix[(cb * (n + 1)) + a];
                    if (sum >= Never)
                    {
                        continue;
                    }

                    int total = best[a] + header + sum;
                    if (total < best[b])
                    {
                        best[b] = total;
                        from[b] = a;
                        pick[b] = (byte)cb;
                    }
                }
            }
        }

        for (int b = n; b > 0; b = from[b])
        {
            books[from[b]..b].Fill(pick[b]);
        }

        return best[n];
    }

    /// <summary>Gets the largest magnitude in a band.</summary>
    /// <param name="band">The quantized lines.</param>
    /// <returns>The largest magnitude.</returns>
    public static int Peak(ReadOnlySpan<int> band)
    {
        int peak = 0;
        foreach (int v in band)
        {
            peak = Math.Max(peak, Math.Abs(v));
        }

        return peak;
    }

    private static bool IsUnsigned(int cb) => cb is 3 or 4 or 7 or 8 or 9 or 10 or 11;

    /// <summary>0 for an all-zero band, 1 for a coded one, 2 for noise and intensity bands.</summary>
    private static int Kind(int cb) => cb == 0 ? 0 : cb >= NoiseBook ? 2 : 1;

    private static bool Same(ReadOnlySpan<byte> books, int a, int b)
    {
        for (int i = a + 1; i < b; i++)
        {
            if (books[i] != books[a])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The bits of an escape sequence after the codeword (4.6.3.3): none below 16, else 2N - 3 for 2^N up to 2^(N+1) - 1.</summary>
    private static int EscapeBits(int magnitude) => magnitude < 16 ? 0 : (2 * (31 - int.LeadingZeroCount(magnitude))) - 3;

    private static void WriteSign(AacBitWriter writer, int v)
    {
        if (v != 0)
        {
            writer.WriteBit(v < 0);
        }
    }

    private static void WriteEscape(AacBitWriter writer, int magnitude)
    {
        if (magnitude < 16)
        {
            return;
        }

        int n = 31 - int.LeadingZeroCount(magnitude);
        int prefix = n - 4;

        // prefix ones, a zero, then the n low bits of the magnitude below its leading one.
        writer.Write((uint)(((1 << prefix) - 1) << 1), prefix + 1);
        writer.Write((uint)magnitude & ((1u << n) - 1), n);
    }
}
