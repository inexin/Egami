using Egami.Barcodes.ErrorCorrection;

namespace Egami.Barcodes.Matrix;

/// <summary>
/// Aztec Code (ISO/IEC 24778): the bytes are written in the Upper, Lower, Mixed, Punctuation and Digit modes and Binary Shift,
/// found by a search over mode states for the fewest bits; the bits are stuffed into codewords of the layer count's word size,
/// Reed-Solomon words are added to reach the error correction share, and the words are laid out in layers around the
/// bullseye and mode message, compact (1 to 4 layers) or full-range (4 to 32 layers) with its reference grid.
/// </summary>
internal static class AztecEncoder
{
    private const int Upper = 0, Lower = 1, Mixed = 2, Punct = 3, Digit = 4;

    /// <summary>The symbol: compact or not, layers, data words, modules.</summary>
    public readonly record struct Result(bool Compact, int Layers, int DataWords, BarcodeModules Modules);

    // The code of each byte in each mode, or -1. Punctuation also has two-character codes 2 to 5.
    private static readonly int[][] s_codes = BuildCodes();

    /// <summary>Encodes the bytes with an optional ECI; returns <see langword="null"/> when they do not fit.</summary>
    public static Result? Encode(byte[] data, int eci, int percent, bool? compact, int? layers)
    {
        BitBuffer bits = HighLevel(data, eci);
        for (int i = 0; i < 4 + 29; i++)
        {
            bool isCompact = i < 4;
            int layerCount = isCompact ? i + 1 : i;
            if ((compact is bool c && c != isCompact) || (layers is int l && l != layerCount))
            {
                continue;
            }

            int wordSize = WordSize(layerCount, isCompact);
            int totalBits = TotalBits(layerCount, isCompact), totalWords = totalBits / wordSize;
            int[] words = Stuff(bits, wordSize);
            int minimumCheck = ((totalWords * percent) + 99) / 100 + 3;
            if (words.Length + minimumCheck > totalWords || (isCompact && words.Length > 64) || words.Length > 2048)
            {
                continue;
            }

            return new Result(isCompact, layerCount, words.Length, Build(words, wordSize, totalWords, totalBits, layerCount, isCompact));
        }

        // A full-range symbol was asked for with fewer than 4 layers.
        if (compact == false && layers is int asked and < 4)
        {
            int wordSize = WordSize(asked, false), totalBits = TotalBits(asked, false), totalWords = totalBits / wordSize;
            int[] words = Stuff(bits, wordSize);
            if (words.Length + (((totalWords * percent) + 99) / 100) + 3 <= totalWords)
            {
                return new Result(false, asked, words.Length, Build(words, wordSize, totalWords, totalBits, asked, false));
            }
        }

        return null;
    }

    /// <summary>Makes an Aztec Rune: a compact core of 11 x 11 modules holding a value 0 to 255 in its mode message.</summary>
    public static BarcodeModules Rune(int value)
    {
        int[] message = [value >> 4, value & 0xF, 0, 0, 0, 0, 0];
        ReedSolomon.Encode(GaloisField.Aztec4, message.AsSpan(0, 2), message.AsSpan(2));
        var bits = new BitBuffer();
        foreach (int word in message)
        {
            bits.Append(word ^ 0b1010, 4);
        }

        bool[] dark = new bool[11 * 11];
        DrawCore(dark, 11, 5, compact: true, bits);
        return BarcodeModules.Wrap(11, 11, dark);
    }

    private static int WordSize(int layers, bool compact) => layers <= 2 ? 6 : layers <= 8 || compact ? 8 : layers <= 22 ? 10 : 12;

    private static int TotalBits(int layers, bool compact) => ((compact ? 88 : 112) + (16 * layers)) * layers;

    private static GaloisField Field(int wordSize) => wordSize switch
    {
        6 => GaloisField.Aztec6,
        8 => GaloisField.Aztec8,
        10 => GaloisField.Aztec10,
        _ => GaloisField.Aztec12,
    };

    /// <summary>
    /// Cuts the bits into words, padding the last with 1s; a word whose first <c>size - 1</c> bits are all 0 or all 1 takes
    /// only those bits and gets the opposite bit last, so no word is all 0s or all 1s.
    /// </summary>
    internal static int[] Stuff(BitBuffer bits, int size)
    {
        var words = new List<int>();
        int mask = (1 << size) - 2;
        for (int i = 0; i < bits.Count; i += size)
        {
            int word = 0;
            for (int j = 0; j < size; j++)
            {
                if (i + j >= bits.Count || bits[i + j])
                {
                    word |= 1 << (size - 1 - j);
                }
            }

            if ((word & mask) == mask)
            {
                words.Add(word & mask);
                i--;
            }
            else if ((word & mask) == 0)
            {
                words.Add(word | 1);
                i--;
            }
            else
            {
                words.Add(word);
            }
        }

        return [.. words];
    }

    private static BarcodeModules Build(int[] data, int wordSize, int totalWords, int totalBits, int layers, bool compact)
    {
        int[] words = new int[totalWords];
        data.CopyTo(words, 0);
        ReedSolomon.Encode(Field(wordSize), data, words.AsSpan(data.Length));

        // The unused bits come first, then the words, from the outermost layer inwards.
        var message = new BitBuffer();
        message.Append(0, totalBits % wordSize);
        foreach (int word in words)
        {
            message.Append(word, wordSize);
        }

        // Without the reference grid, then with it: every 16th row and column from the center in full-range symbols.
        int baseSize = (compact ? 11 : 14) + (layers * 4);
        int[] map = new int[baseSize];
        int size;
        if (compact)
        {
            size = baseSize;
            for (int i = 0; i < baseSize; i++)
            {
                map[i] = i;
            }
        }
        else
        {
            size = baseSize + 1 + (2 * (((baseSize / 2) - 1) / 15));
            int originalCenter = baseSize / 2, newCenter = size / 2;
            for (int i = 0; i < originalCenter; i++)
            {
                int offset = i + (i / 15);
                map[originalCenter - i - 1] = newCenter - offset - 1;
                map[originalCenter + i] = newCenter + offset + 1;
            }
        }

        bool[] dark = new bool[size * size];
        void Set(int x, int y) => dark[(y * size) + x] = true;

        for (int i = 0, rowOffset = 0; i < layers; i++)
        {
            int rowSize = ((layers - i) * 4) + (compact ? 9 : 12);
            for (int j = 0; j < rowSize; j++)
            {
                int columnOffset = j * 2;
                for (int k = 0; k < 2; k++)
                {
                    if (message[rowOffset + columnOffset + k])
                    {
                        Set(map[(i * 2) + k], map[(i * 2) + j]);
                    }

                    if (message[rowOffset + (rowSize * 2) + columnOffset + k])
                    {
                        Set(map[(i * 2) + j], map[baseSize - 1 - (i * 2) - k]);
                    }

                    if (message[rowOffset + (rowSize * 4) + columnOffset + k])
                    {
                        Set(map[baseSize - 1 - (i * 2) - k], map[baseSize - 1 - (i * 2) - j]);
                    }

                    if (message[rowOffset + (rowSize * 6) + columnOffset + k])
                    {
                        Set(map[baseSize - 1 - (i * 2) - j], map[(i * 2) + k]);
                    }
                }
            }

            rowOffset += rowSize * 8;
        }

        int center = size / 2;
        if (!compact)
        {
            for (int i = 0, j = 0; i < (baseSize / 2) - 1; i += 15, j += 16)
            {
                for (int k = center & 1; k < size; k += 2)
                {
                    Set(center - j, k);
                    Set(center + j, k);
                    Set(k, center - j);
                    Set(k, center + j);
                }
            }
        }

        DrawCore(dark, size, center, compact, ModeMessage(compact, layers, data.Length));
        return BarcodeModules.Wrap(size, size, dark);
    }

    /// <summary>The mode message: layers and data words, with Reed-Solomon over GF(16): 7 words compact, 10 full-range.</summary>
    private static BitBuffer ModeMessage(bool compact, int layers, int dataWords)
    {
        var raw = new BitBuffer();
        int[] message;
        if (compact)
        {
            raw.Append(layers - 1, 2);
            raw.Append(dataWords - 1, 6);
            message = new int[7];
        }
        else
        {
            raw.Append(layers - 1, 5);
            raw.Append(dataWords - 1, 11);
            message = new int[10];
        }

        int dataCount = compact ? 2 : 4;
        for (int i = 0; i < dataCount; i++)
        {
            int word = 0;
            for (int b = 0; b < 4; b++)
            {
                word = (word << 1) | (raw[(i * 4) + b] ? 1 : 0);
            }

            message[i] = word;
        }

        ReedSolomon.Encode(GaloisField.Aztec4, message.AsSpan(0, dataCount), message.AsSpan(dataCount));
        var bits = new BitBuffer();
        foreach (int word in message)
        {
            bits.Append(word, 4);
        }

        return bits;
    }

    /// <summary>Draws the bullseye, the orientation marks and the mode message around it.</summary>
    private static void DrawCore(bool[] dark, int size, int center, bool compact, BitBuffer mode)
    {
        void Set(int x, int y) => dark[(y * size) + x] = true;
        int radius = compact ? 5 : 7;
        for (int i = 0; i < radius; i += 2)
        {
            for (int j = center - i; j <= center + i; j++)
            {
                Set(j, center - i);
                Set(j, center + i);
                Set(center - i, j);
                Set(center + i, j);
            }
        }

        Set(center - radius, center - radius);
        Set(center - radius + 1, center - radius);
        Set(center - radius, center - radius + 1);
        Set(center + radius, center - radius);
        Set(center + radius, center - radius + 1);
        Set(center + radius, center + radius - 1);

        if (compact)
        {
            for (int i = 0; i < 7; i++)
            {
                int offset = center - 3 + i;
                if (mode[i])
                {
                    Set(offset, center - 5);
                }

                if (mode[i + 7])
                {
                    Set(center + 5, offset);
                }

                if (mode[20 - i])
                {
                    Set(offset, center + 5);
                }

                if (mode[27 - i])
                {
                    Set(center - 5, offset);
                }
            }
        }
        else
        {
            for (int i = 0; i < 10; i++)
            {
                int offset = center - 5 + i + (i / 5);
                if (mode[i])
                {
                    Set(offset, center - 7);
                }

                if (mode[i + 10])
                {
                    Set(center + 7, offset);
                }

                if (mode[29 - i])
                {
                    Set(offset, center + 7);
                }

                if (mode[39 - i])
                {
                    Set(center - 7, offset);
                }
            }
        }
    }

    // ---- High-level encoding ----

    /// <summary>A step in the encoding: a code of some bits, or a binary run of bytes.</summary>
    private sealed record class Token(Token? Previous, int Value, int Bits, int BinaryStart = -1, int BinaryCount = 0);

    /// <summary>A search state: the mode, an open binary run, the bits so far (with the open run counted) and the steps.</summary>
    private readonly record struct State(int Mode, int BinaryStart, int BinaryCount, int Cost, Token? Tokens);

    private static int BinaryCost(int count) => count == 0 ? 0 : (count <= 31 ? 10 : 21) + (8 * count);

    /// <summary>Finds the fewest bits for the bytes over the modes and Binary Shift, an ECI first as FLG(n).</summary>
    internal static BitBuffer HighLevel(byte[] data, int eci)
    {
        Token? start = null;
        int startCost = 0;
        if (eci > 0)
        {
            // P/S, FLG(n), n as 3 bits, then the ECI's digits in Digit codes.
            string digits = eci.ToString(System.Globalization.CultureInfo.InvariantCulture);
            start = new Token(start, 0, 5);
            start = new Token(start, 0, 5);
            start = new Token(start, digits.Length, 3);
            foreach (char d in digits)
            {
                start = new Token(start, d - '0' + 2, 4);
            }

            startCost = 13 + (4 * digits.Length);
        }

        int n = data.Length;
        var states = new Dictionary<(int, int), State>[n + 1];
        for (int i = 0; i <= n; i++)
        {
            states[i] = [];
        }

        states[0][(Upper, 0)] = new State(Upper, -1, 0, startCost, start);
        for (int i = 0; i < n; i++)
        {
            foreach (State state in states[i].Values)
            {
                Advance(data, i, state, states);
            }
        }

        State best = default;
        best = best with { Cost = int.MaxValue };
        foreach (State state in states[n].Values)
        {
            if (state.Cost < best.Cost)
            {
                best = state;
            }
        }

        Token? tokens = Close(best).Tokens;
        var list = new List<Token>();
        for (Token? t = tokens; t is not null; t = t.Previous)
        {
            list.Add(t);
        }

        list.Reverse();
        var bits = new BitBuffer();
        foreach (Token token in list)
        {
            if (token.BinaryCount == 0)
            {
                bits.Append(token.Value, token.Bits);
                continue;
            }

            bits.Append(31, 5);
            if (token.BinaryCount <= 31)
            {
                bits.Append(token.BinaryCount, 5);
            }
            else
            {
                bits.Append(0, 5);
                bits.Append(token.BinaryCount - 31, 11);
            }

            for (int k = 0; k < token.BinaryCount; k++)
            {
                bits.Append(data[token.BinaryStart + k], 8);
            }
        }

        return bits;
    }

    private static void Offer(Dictionary<(int, int), State>[] states, int position, State state)
    {
        // Open binary runs of up to 31 bytes and longer ones are kept apart: a long run costs more now but less later.
        var key = (state.Mode, state.BinaryCount == 0 ? 0 : state.BinaryCount <= 31 ? 1 : 2);
        if (!states[position].TryGetValue(key, out State existing) || state.Cost < existing.Cost)
        {
            states[position][key] = state;
        }
    }

    /// <summary>Ends an open binary run, turning it into a token.</summary>
    private static State Close(State state) => state.BinaryCount == 0
        ? state
        : state with { BinaryStart = -1, BinaryCount = 0, Tokens = new Token(state.Tokens, 0, 0, state.BinaryStart, state.BinaryCount) };

    private static void Advance(byte[] data, int i, State state, Dictionary<(int, int), State>[] states)
    {
        byte c = data[i];

        // Extend an open binary run, or start one (Binary Shift works from Upper, Lower and Mixed).
        if (state.BinaryCount > 0)
        {
            if (state.BinaryCount < 2078)
            {
                Offer(states, i + 1, state with { BinaryCount = state.BinaryCount + 1, Cost = state.Cost - BinaryCost(state.BinaryCount) + BinaryCost(state.BinaryCount + 1) });
            }

            state = Close(state);
        }

        {
            State origin = state;
            if (origin.Mode is Punct or Digit)
            {
                origin = Latch(origin, Upper);
            }

            Offer(states, i + 1, origin with { BinaryStart = i, BinaryCount = 1, Cost = origin.Cost + BinaryCost(1) });
        }

        // Two-character punctuation: CR LF, ". ", ", ", ": ".
        if (i + 1 < data.Length)
        {
            int pair = (c, data[i + 1]) switch
            {
                (13, 10) => 2,
                ((byte)'.', (byte)' ') => 3,
                ((byte)',', (byte)' ') => 4,
                ((byte)':', (byte)' ') => 5,
                _ => 0,
            };
            if (pair != 0)
            {
                Emit(states, i + 2, state, Punct, pair);
            }
        }

        for (int mode = Upper; mode <= Digit; mode++)
        {
            int code = s_codes[mode][c];
            if (code >= 0)
            {
                Emit(states, i + 1, state, mode, code);
            }
        }
    }

    /// <summary>Offers the code in a mode: in the current mode, after a latch, or after a shift where one exists.</summary>
    private static void Emit(Dictionary<(int, int), State>[] states, int position, State state, int mode, int code)
    {
        int bits = mode == Digit ? 4 : 5;
        if (state.Mode == mode)
        {
            Offer(states, position, Append(state, code, bits));
            return;
        }

        Offer(states, position, Append(Latch(state, mode), code, bits));
        int shiftBits = state.Mode == Digit ? 4 : 5;
        if (mode == Punct)
        {
            // P/S is code 0 in every mode but Punctuation.
            Offer(states, position, Append(Append(state, 0, shiftBits), code, 5));
        }
        else if (mode == Upper && state.Mode is Lower or Digit)
        {
            // U/S: 28 in Lower, 15 in Digit.
            Offer(states, position, Append(Append(state, state.Mode == Lower ? 28 : 15, shiftBits), code, 5));
        }
    }

    private static State Append(State state, int value, int bits) =>
        state with { Cost = state.Cost + bits, Tokens = new Token(state.Tokens, value, bits) };

    /// <summary>Latches to a mode with the shortest sequence of latch codes.</summary>
    private static State Latch(State state, int target)
    {
        ReadOnlySpan<(int Mode, int Code)> steps = (state.Mode, target) switch
        {
            (Upper, Lower) => [(Upper, 28)],
            (Upper, Mixed) => [(Upper, 29)],
            (Upper, Digit) => [(Upper, 30)],
            (Upper, Punct) => [(Upper, 29), (Mixed, 30)],
            (Lower, Upper) => [(Lower, 30), (Digit, 14)],
            (Lower, Mixed) => [(Lower, 29)],
            (Lower, Digit) => [(Lower, 30)],
            (Lower, Punct) => [(Lower, 29), (Mixed, 30)],
            (Mixed, Upper) => [(Mixed, 29)],
            (Mixed, Lower) => [(Mixed, 28)],
            (Mixed, Digit) => [(Mixed, 29), (Upper, 30)],
            (Mixed, Punct) => [(Mixed, 30)],
            (Punct, Upper) => [(Punct, 31)],
            (Punct, Lower) => [(Punct, 31), (Upper, 28)],
            (Punct, Mixed) => [(Punct, 31), (Upper, 29)],
            (Punct, Digit) => [(Punct, 31), (Upper, 30)],
            (Digit, Upper) => [(Digit, 14)],
            (Digit, Lower) => [(Digit, 14), (Upper, 28)],
            (Digit, Mixed) => [(Digit, 14), (Upper, 29)],
            (Digit, Punct) => [(Digit, 14), (Upper, 29), (Mixed, 30)],
            _ => [],
        };
        foreach ((int mode, int code) in steps)
        {
            state = Append(state, code, mode == Digit ? 4 : 5);
        }

        return state with { Mode = target };
    }

    private static int[][] BuildCodes()
    {
        int[][] codes = new int[5][];
        for (int m = 0; m < 5; m++)
        {
            codes[m] = new int[256];
            Array.Fill(codes[m], -1);
        }

        codes[Upper][' '] = codes[Lower][' '] = codes[Mixed][' '] = codes[Digit][' '] = 1;
        for (int i = 0; i < 26; i++)
        {
            codes[Upper]['A' + i] = 2 + i;
            codes[Lower]['a' + i] = 2 + i;
        }

        for (int i = 0; i < 10; i++)
        {
            codes[Digit]['0' + i] = 2 + i;
        }

        codes[Digit][','] = 12;
        codes[Digit]['.'] = 13;

        // Mixed: control characters 1 to 13 and 27 to 31, then @ \ ^ _ ` | ~ and DEL.
        for (int i = 1; i <= 13; i++)
        {
            codes[Mixed][i] = i + 1;
        }

        for (int i = 27; i <= 31; i++)
        {
            codes[Mixed][i] = i - 12;
        }

        const string mixed = "@\\^_`|~\x7F";
        for (int i = 0; i < mixed.Length; i++)
        {
            codes[Mixed][mixed[i]] = 20 + i;
        }

        // Punctuation from code 6; codes 1 to 5 are CR and the two-character codes.
        codes[Punct][13] = 1;
        const string punctuation = "!\"#$%&'()*+,-./:;<=>?[]{}";
        for (int i = 0; i < punctuation.Length; i++)
        {
            codes[Punct][punctuation[i]] = 6 + i;
        }

        return codes;
    }
}
