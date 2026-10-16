using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Egami.Formats.Jxl;

/// <summary>
/// Effort 1: libjxl's fast lossless encoder (enc_fast_lossless.cc), which cjxl -e 1 runs. Every channel is predicted with the
/// clamped gradient and coded with its own prefix code, built from a few sampled rows, with runs of zero residuals as LZ77
/// copies at distance 1; RGB goes through YCoCg, and 8-bit images of at most 512 colors through a palette. The tree, the
/// context map and the codes' descriptions are libjxl's fixed bits. Translated from libjxl 0.12.0 (see THIRD-PARTY-NOTICES).
/// </summary>
internal static class JxlFastLossless
{
    private const int NumRawSymbols = 19;
    private const int NumLz77 = 33;
    private const int Lz77CacheSize = 32;
    private const int Lz77MinLength = 7;
    private const int ChunkSize = 16;
    private const int GroupDim = 256;
    private const int HashExp = 16;
    private const uint HashMultiplier = 2654435761;
    private const int MaxColors = 512;

    /// <summary>
    /// Encodes the channels (gray, gray and alpha, RGB or RGBA, at most 16 bits) and gives the frame's sections in TOC order; the
    /// frame header must say group size shift 1.
    /// </summary>
    internal static List<byte[]> Encode(int[][] channels, int width, int height, int bitDepth, int parallelism)
    {
        int nbChans = channels.Length;
        int groupsX = (width + GroupDim - 1) / GroupDim;
        int groupsY = (height + GroupDim - 1) / GroupDim;
        int groups = groupsX * groupsY;
        bool oneGroup = groups == 1;
        int dcGroups = ((width + 2047) / 2048) * ((height + 2047) / 2048);
        Depth depth = Depth.For(bitDepth);

        // The palette (effort 2 in libjxl's terms, as cjxl -e 1 asks): 8-bit images whose colors hash without collisions.
        uint[] palette = new uint[1 << HashExp];
        int[] lookup = new int[1 << HashExp];
        int pcolors = 0;
        bool collided = bitDepth != 8 || DetectPalette(channels, width, height, palette);
        int entries = 0;
        if (!collided)
        {
            pcolors = 1; // all zero is always a color
            bool haveColor = false;
            int minG = 255;
            int maxG = 0;
            for (int k = 0; k < palette.Length; k++)
            {
                if (palette[k] == 0)
                {
                    continue;
                }

                uint p = palette[k];
                int p0 = (int)(p & 0xFF);
                int p1 = (int)((p >> 8) & 0xFF);
                int p2 = (int)((p >> 16) & 0xFF);
                palette[entries] = p;
                haveColor |= p0 != p1 || p0 != p2;
                minG = Math.Min(minG, p1);
                maxG = Math.Max(maxG, p1);
                entries++;
                if (entries + pcolors > MaxColors)
                {
                    collided = true;
                    break;
                }
            }

            // Gray without many holes gains nothing from a palette.
            if (!collided && !haveColor && maxG - minG < entries * 1.4f)
            {
                collided = true;
            }
        }

        uint[] sorted = [];
        if (!collided)
        {
            sorted = [.. palette.Take(entries).Where(p => p != 0).OrderBy(p => Luma(p, nbChans))];
            Array.Clear(lookup);
            foreach (uint p in sorted)
            {
                lookup[Hash(p)] = pcolors++;
            }
        }

        // Token counts from a few rows of every group, scaled and added to libjxl's base counts.
        ulong[][] raw = [.. Enumerable.Range(0, 4).Select(_ => new ulong[NumRawSymbols])];
        ulong[][] lz = [.. Enumerable.Range(0, 4).Select(_ => new ulong[NumLz77])];
        for (int g = 0; g < groups; g++)
        {
            int x0 = g % groupsX * GroupDim;
            int y0 = g / groupsX * GroupDim;
            int xs = Math.Min(width - x0, GroupDim);
            int ys = Math.Min(height - y0, GroupDim);
            int rows = 2 * 2 * ys / 256;
            int yBegin = Math.Max(0, ys - rows) / 2;
            int yCount = Math.Max(0, Math.Min(rows, ys - yBegin - 1));
            int xMax = xs / ChunkSize * ChunkSize;
            Group group = new(channels, width, x0, y0 + yBegin, xMax, 1 + yCount, nbChans, collided ? null : lookup);
            Collector[] collectors = new Collector[group.Channels];
            for (int c = 0; c < group.Channels; c++)
            {
                int which = collided ? c : oneGroup ? 1 : 0;
                collectors[c] = new Collector(raw[which], lz[which]);
            }

            group.Process(yskip: 1, collectors);
        }

        ulong[] baseRaw = [3843, 852, 1270, 1214, 1014, 727, 481, 300, 159, 51, 5, 1, 1, 1, 1, 1, 1, 1, 1];
        bool ycocg = nbChans > 2 && collided;
        bool largePalette = !collided || pcolors >= 256;
        for (int i = depth.NumSymbols(ycocg || largePalette); i < NumRawSymbols; i++)
        {
            baseRaw[i] = 0;
        }

        for (int c = 0; c < 4; c++)
        {
            for (int i = 0; i < NumRawSymbols; i++)
            {
                raw[c][i] = (raw[c][i] << 8) + baseRaw[i];
            }
        }

        if (!collided)
        {
            (int token, _, _) = Uint000(PackSigned(pcolors - 1));
            for (int i = 0; i < token + 1; i++)
            {
                raw[0][i] = Math.Max(raw[0][i], 1);
            }

            // These tokens only code the palette itself, so they may get a bad code.
            for (int i = token + 1; i < 10; i++)
            {
                raw[0][i] = 1;
            }
        }

        ulong[] baseLz = [29, 27, 25, 23, 21, 21, 19, 18, 21, 17, 16, 15, 15, 14, 13, 13, 137, 98, 61, 34, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0];
        for (int c = 0; c < 4; c++)
        {
            for (int i = 0; i < NumLz77; i++)
            {
                lz[c][i] = (lz[c][i] << 8) + baseLz[i];
            }
        }

        PrefixCode[] codes = [.. Enumerable.Range(0, 4).Select(c => new PrefixCode(depth, raw[c], lz[c]))];

        // The DC global section; with one group the pixel data follows it in the same section.
        FastBitWriter global = new();
        WriteGlobalCommon(global, codes);
        if (collided)
        {
            if (nbChans > 2)
            {
                global.Write(2, 0b01); // one transform
                global.Write(2, 0b00); // RCT
                global.Write(5, 0b00000); // from channel 0
                global.Write(2, 0b00); // YCoCg
            }
            else
            {
                global.Write(2, 0b00); // no transforms
            }
        }
        else
        {
            WritePalette(global, codes, sorted, pcolors, nbChans);
        }

        if (!oneGroup)
        {
            global.ZeroPadToByte();
        }

        FastBitWriter[][] groupData = new FastBitWriter[groups][];
        Parallel.For(0, groups, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism) }, g =>
        {
            int x0 = g % groupsX * GroupDim;
            int y0 = g / groupsX * GroupDim;
            int xs = Math.Min(width - x0, GroupDim);
            int ys = Math.Min(height - y0, GroupDim);
            Group group = new(channels, width, x0, y0, xs, ys, nbChans, collided ? null : lookup);
            FastBitWriter[] outputs = new FastBitWriter[group.Channels];
            int capacity = (xs * ys * depth.MaxEncodedBitsPerSample / 8) + 64;
            Encoder[] encoders = new Encoder[group.Channels];
            for (int c = 0; c < group.Channels; c++)
            {
                outputs[c] = oneGroup && c == 0 ? global : new FastBitWriter(capacity);
                int which = collided ? c : oneGroup ? 1 : 0;
                encoders[c] = new Encoder(codes[which], outputs[c]);
            }

            if (!oneGroup)
            {
                // The group's header: global tree, default WP, no transforms.
                outputs[0].Write(1, 1);
                outputs[0].Write(1, 1);
                outputs[0].Write(2, 0b00);
            }

            group.Process(yskip: 0, encoders);
            groupData[g] = outputs;
        });

        if (oneGroup)
        {
            for (int c = 1; c < groupData[0].Length; c++)
            {
                global.Append(groupData[0][c]);
                groupData[0][c].Release();
            }

            global.ZeroPadToByte();
            byte[] single = global.ToArray();
            global.Release();
            return [single];
        }

        List<byte[]> sections = [global.ToArray()];
        global.Release();
        for (int i = 0; i < dcGroups + 1; i++)
        {
            sections.Add([]);
        }

        foreach (FastBitWriter[] outputs in groupData)
        {
            FastBitWriter section = outputs[0];
            for (int c = 1; c < outputs.Length; c++)
            {
                section.Append(outputs[c]);
                outputs[c].Release();
            }

            section.ZeroPadToByte();
            sections.Add(section.ToArray());
            section.Release();
        }

        return sections;
    }

    private static int Hash(uint p) => (int)((p * HashMultiplier) >> (32 - HashExp));

    private static uint PackSigned(int value) => unchecked(((uint)value << 1) ^ (((uint)~value >> 31) - 1));

    /// <summary>The hybrid integer 0, 0, 0: token 0 for 0, else one more than the value's top bit's place, the rest raw.</summary>
    private static (int Token, int NBits, uint Bits) Uint000(uint value)
    {
        if (value == 0)
        {
            return (0, 0, 0);
        }

        int n = BitOperations.Log2(value);
        return (n + 1, n, value - (1u << n));
    }

    /// <summary>The hybrid integer 4, 0, 0 of the LZ77 lengths.</summary>
    private static (int Token, int NBits, uint Bits) Uint400(uint value)
    {
        if (value < 16)
        {
            return ((int)value, 0, 0);
        }

        int n = BitOperations.Log2(value);
        return (16 + n - 4, n, value - (1u << n));
    }

    private static float Luma(uint p, int nbChans)
    {
        float a0 = p & 0xFF;
        float a1 = (p >> 8) & 0xFF;
        float a2 = (p >> 16) & 0xFF;
        float a3 = (p >> 24) & 0xFF;
        float y = (0.299f * a0) + (0.587f * a1) + (0.114f * a2) + 0.01f;
        return nbChans == 4 ? y * a3 : y;
    }

    /// <summary>Hashes every pixel's color into the table; returns whether two colors collided (libjxl's detect_palette).</summary>
    private static bool DetectPalette(int[][] channels, int width, int height, uint[] palette)
    {
        int nb = channels.Length;
        ulong mask = (1UL << (8 * nb)) - 1;
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                uint p = 0;
                for (int c = 0; c < nb; c++)
                {
                    p |= (uint)channels[c][row + x] << (8 * c);
                }

                p = (uint)(p & mask);
                int index = Hash(p);
                if (palette[index] != 0 && p != palette[index])
                {
                    return true;
                }

                palette[index] = p;
            }
        }

        return false;
    }

    /// <summary>Writes what every fast lossless file's DC global section starts with: the fixed tree, the context map and the codes.</summary>
    private static void WriteGlobalCommon(FastBitWriter w, PrefixCode[] codes)
    {
        // No patches, splines or noise.
        w.Write(1, 1); // default DC dequantization
        w.Write(1, 1); // global tree
        w.Write(1, 0); // no LZ77 for the tree
        w.Write(1, 1); // simple context map for the tree
        w.Write(2, 0); // all contexts in one cluster
        w.Write(1, 1); // prefix code for the tree
        w.Write(4, 0); // 000 hybrid integer
        w.Write(6, 0b100011); // alphabet of 4
        w.Write(2, 1); // simple prefix code
        w.Write(2, 3); // of 4 symbols
        w.Write(2, 0);
        w.Write(2, 1);
        w.Write(2, 2);
        w.Write(2, 3);
        w.Write(1, 0); // first tree shape

        // A leaf per channel, each with the gradient predictor.
        ReadOnlySpan<byte> symbolBits = [0b00, 0b10, 0b001, 0b101, 0b0011, 0b0111];
        ReadOnlySpan<byte> symbolNbits = [2, 2, 3, 3, 4, 4];
        foreach (int v in (ReadOnlySpan<int>)[1, 2, 1, 4, 1, 0, 0, 5, 0, 0, 0, 0, 5, 0, 0, 0, 0, 5, 0, 0, 0, 0, 5, 0, 0, 0])
        {
            w.Write(symbolNbits[v], symbolBits[v]);
        }

        w.Write(1, 1); // LZ77 for the main stream
        w.Write(2, 0b00); // offset 224
        w.Write(4, 0b1010); // minimum length 7
        w.Write(4, 4); // 400 hybrid integer for lengths
        w.Write(3, 0);
        w.Write(3, 0);

        w.Write(1, 1); // simple context map
        w.Write(2, 3); // 3 bits an entry
        w.Write(3, 4); // channel 3
        w.Write(3, 3); // channel 2
        w.Write(3, 2); // channel 1
        w.Write(3, 1); // channel 0
        w.Write(3, 0); // distances first

        w.Write(1, 1); // prefix codes
        w.Write(4, 0); // 000 hybrid integer for distances
        for (int i = 0; i < 4; i++)
        {
            w.Write(4, 0); // 000 hybrid integer for symbols
        }

        w.Write(5, 0b00001); // distance alphabet of 2 (only distance 1)
        for (int i = 0; i < 4; i++)
        {
            w.Write(1, 1);
            w.Write(4, 8);
            w.Write(8, 256);
        }

        w.Write(2, 1); // distances: simple prefix code
        w.Write(2, 0); // of one symbol
        w.Write(1, 1);
        foreach (PrefixCode code in codes)
        {
            code.WriteTo(w);
        }

        // The global stream's header.
        w.Write(1, 1); // global tree
        w.Write(1, 1); // default WP
    }

    /// <summary>Writes the palette transform and the palette itself (PrepareDCGlobalPalette).</summary>
    private static void WritePalette(FastBitWriter w, PrefixCode[] codes, uint[] sorted, int pcolors, int nbChans)
    {
        w.Write(2, 0b01); // one transform
        w.Write(2, 0b01); // palette
        w.Write(5, 0b00000); // from channel 0
        switch (nbChans)
        {
            case 1:
                w.Write(2, 0b00);
                break;
            case 3:
                w.Write(2, 0b01);
                break;
            case 4:
                w.Write(2, 0b10);
                break;
            default:
                w.Write(2, 0b11);
                w.Write(13, (ulong)(nbChans - 1));
                break;
        }

        if (pcolors < 256)
        {
            w.Write(2, 0b00);
            w.Write(8, (ulong)pcolors);
        }
        else
        {
            w.Write(2, 0b01);
            w.Write(10, (ulong)(pcolors - 256));
        }

        w.Write(2, 0b00); // no deltas
        w.Write(4, 0); // zero predictor for deltas

        // The palette is one row a channel, the all-zero color first.
        Encoder encoder = new(codes[0], w);
        RowProcessor processor = new(encoder);
        int[][] p = [.. Enumerable.Range(0, 4).Select(_ => new int[32 + 1024])];
        for (int i = 0; i < pcolors - 1; i++)
        {
            for (int c = 0; c < 4; c++)
            {
                p[c][16 + i + 1] = (int)((sorted[i] >> (8 * c)) & 0xFF);
            }
        }

        p[0][15] = 0;
        processor.ProcessRow(p[0], 16, p[0], 15, p[0], 15, p[0], 15, pcolors);
        p[1][15] = p[0][16];
        p[0][15] = p[0][16];
        if (nbChans > 1)
        {
            processor.ProcessRow(p[1], 16, p[1], 15, p[0], 16, p[0], 15, pcolors);
        }

        p[2][15] = p[1][16];
        p[1][15] = p[1][16];
        if (nbChans > 2)
        {
            processor.ProcessRow(p[2], 16, p[2], 15, p[1], 16, p[1], 15, pcolors);
        }

        p[3][15] = p[2][16];
        p[2][15] = p[2][16];
        if (nbChans > 3)
        {
            processor.ProcessRow(p[3], 16, p[3], 15, p[2], 16, p[2], 15, pcolors);
        }

        processor.Finish();
    }

    /// <summary>The prefix code lengths each bit depth allows (libjxl keeps them so its SIMD merging works).</summary>
    private sealed class Depth
    {
        private Depth(int bitDepth, byte[] min, byte[] max, int symbols)
        {
            BitDepth = bitDepth;
            MinLength = min;
            MaxLength = max;
            FixedSymbols = symbols;
        }

        internal int BitDepth { get; }

        /// <summary>Gets the most bits a sample can take coded, for sizing buffers.</summary>
        internal int MaxEncodedBitsPerSample => BitDepth <= 8 ? 16 : BitDepth <= 13 ? 22 : BitDepth == 14 ? 23 : 25;

        internal byte[] MinLength { get; }

        internal byte[] MaxLength { get; }

        private int FixedSymbols { get; }

        internal static Depth For(int bitDepth) => bitDepth switch
        {
            <= 8 => new(bitDepth, new byte[12], [7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 10], 0),
            <= 13 => new(bitDepth, new byte[17], [8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 10], 0),
            14 => new(14, [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 8, 8, 7], [7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 8, 8, 10], 17),
            _ => new(bitDepth, [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 8, 8, 8, 8, 8, 8, 7], [7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 8, 8, 8, 8, 8, 8, 10], 19),
        };

        /// <summary>Gets the raw symbols the residuals can need: one more than their bits (YCoCg and prediction add one each).</summary>
        internal int NumSymbols(bool ycocgOrLargePalette) => FixedSymbols != 0 ? FixedSymbols : BitDepth + (ycocgOrLargePalette ? 3 : 2);
    }

    /// <summary>A prefix code for the raw symbols and the LZ77 lengths, which share the last level-one symbol (libjxl's PrefixCode).</summary>
    private sealed class PrefixCode
    {
        private const int MaxSymbols = NumLz77;

        private readonly byte[] _rawNbits = new byte[NumRawSymbols];
        private readonly byte[] _rawBits = new byte[NumRawSymbols];
        private readonly byte[] _lz77Nbits = new byte[NumLz77];
        private readonly ushort[] _lz77Bits = new ushort[NumLz77];
        private readonly ulong[] _cacheBits = new ulong[Lz77CacheSize];
        private readonly byte[] _cacheNbits = new byte[Lz77CacheSize];

        internal PrefixCode(Depth depth, ulong[] rawCounts, ulong[] lz77Counts)
        {
            // The LZ77 lengths count as one more raw symbol on the first level.
            ulong[] level1 = new ulong[NumRawSymbols + 1];
            Array.Copy(rawCounts, level1, NumRawSymbols);
            int numraw = NumRawSymbols;
            while (numraw > 0 && level1[numraw - 1] == 0)
            {
                numraw--;
            }

            level1[numraw] = 0;
            foreach (ulong c in lz77Counts)
            {
                level1[numraw] += c;
            }

            byte[] level1Nbits = new byte[NumRawSymbols + 1];
            ComputeCodeLengths(level1, numraw + 1, depth.MinLength, depth.MaxLength, level1Nbits);
            byte[] level2Nbits = new byte[NumLz77];
            byte[] minLengths = new byte[NumLz77];
            byte[] maxLengths = new byte[NumLz77];
            Array.Fill(maxLengths, (byte)(15 - level1Nbits[numraw]));
            int numLz77 = NumLz77;
            while (numLz77 > 0 && lz77Counts[numLz77 - 1] == 0)
            {
                numLz77--;
            }

            ComputeCodeLengths(lz77Counts, numLz77, minLengths, maxLengths, level2Nbits);
            for (int i = 0; i < numraw; i++)
            {
                _rawNbits[i] = level1Nbits[i];
            }

            for (int i = 0; i < numLz77; i++)
            {
                _lz77Nbits[i] = level2Nbits[i] != 0 ? (byte)(level1Nbits[numraw] + level2Nbits[i]) : (byte)0;
            }

            ComputeCanonicalCode(_rawNbits, _rawBits, numraw, _lz77Nbits, _lz77Bits, NumLz77);
            for (int count = 0; count < Lz77CacheSize; count++)
            {
                (int token, int nbits, uint bits) = Uint400((uint)count);
                _cacheNbits[count] = (byte)(_lz77Nbits[token] + nbits + _rawNbits[0]);
                _cacheBits[count] = (((((ulong)bits) << _lz77Nbits[token]) | _lz77Bits[token]) << _rawNbits[0]) | _rawBits[0];
            }
        }

        internal byte[] RawNbitsTable => _rawNbits;

        internal byte[] RawBitsTable => _rawBits;

        /// <summary>Writes a run of zero residuals: a zero, then a copy of the rest at distance 1.</summary>
        internal void WriteRun(FastBitWriter w, int count)
        {
            if (count == 0)
            {
                return;
            }

            count -= Lz77MinLength + 1;
            if (count < Lz77CacheSize)
            {
                w.Write(_cacheNbits[count], _cacheBits[count]);
                return;
            }

            (int token, int nbits, uint bits) = Uint400((uint)count);
            ulong wbits = bits;
            wbits = (wbits << _lz77Nbits[token]) | _lz77Bits[token];
            wbits = (wbits << _rawNbits[0]) | _rawBits[0];
            w.Write(_lz77Nbits[token] + nbits + _rawNbits[0], wbits);
        }

        /// <summary>Writes the code as a complex Brotli prefix code over 224 raw symbols and the LZ77 symbols (libjxl's WriteTo).</summary>
        internal void WriteTo(FastBitWriter w)
        {
            ulong[] lengthCounts = new ulong[18];
            lengthCounts[17] = 3 + (2 * (NumLz77 - 1));
            foreach (byte n in _rawNbits)
            {
                lengthCounts[n]++;
            }

            foreach (byte n in _lz77Nbits)
            {
                lengthCounts[n]++;
            }

            byte[] lengthNbits = new byte[18];
            byte[] lengthMin = new byte[18];
            byte[] lengthMax = [5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5];
            ComputeCodeLengths(lengthCounts, 18, lengthMin, lengthMax, lengthNbits);
            w.Write(2, 0b00); // HSKIP 0

            ReadOnlySpan<byte> order = [1, 2, 3, 4, 0, 5, 17, 6, 16, 7, 8, 9, 10, 11, 12, 13, 14, 15];
            ReadOnlySpan<byte> lengthLengthNbits = [2, 4, 3, 2, 2, 4];
            ReadOnlySpan<byte> lengthLengthBits = [0, 7, 3, 2, 1, 15];
            int numCodeLengths = 18;
            while (lengthNbits[order[numCodeLengths - 1]] == 0)
            {
                numCodeLengths--;
            }

            for (int i = 0; i < numCodeLengths; i++)
            {
                int symbol = lengthNbits[order[i]];
                w.Write(lengthLengthNbits[symbol], lengthLengthBits[symbol]);
            }

            ushort[] lengthBits = new ushort[18];
            ComputeCanonicalCode([], [], 0, lengthNbits, lengthBits, 18);
            foreach (byte n in _rawNbits)
            {
                w.Write(lengthNbits[n], lengthBits[n]);
            }

            int numLz77 = NumLz77;
            while (_lz77Nbits[numLz77 - 1] == 0)
            {
                numLz77--;
            }

            // Zeros up to symbol 224, where the LZ77 symbols start: 224 − 19 = 205, as three repeat codes.
            w.Write(lengthNbits[17], lengthBits[17]);
            w.Write(3, 0b010);
            w.Write(lengthNbits[17], lengthBits[17]);
            w.Write(3, 0b000);
            w.Write(lengthNbits[17], lengthBits[17]);
            w.Write(3, 0b010);
            for (int i = 0; i < numLz77; i++)
            {
                w.Write(lengthNbits[_lz77Nbits[i]], lengthBits[_lz77Nbits[i]]);
            }
        }

        private static ushort BitReverse(int nbits, ushort bits)
        {
            ReadOnlySpan<ushort> nibble = [0b0000, 0b1000, 0b0100, 0b1100, 0b0010, 0b1010, 0b0110, 0b1110, 0b0001, 0b1001, 0b0101, 0b1101, 0b0011, 0b1011, 0b0111, 0b1111];
            ushort rev16 = (ushort)((nibble[bits & 0xF] << 12) | (nibble[(bits >> 4) & 0xF] << 8) | (nibble[(bits >> 8) & 0xF] << 4) | nibble[bits >> 12]);
            return (ushort)(rev16 >> (16 - nbits));
        }

        /// <summary>Makes the canonical codes from the lengths, the two chunks numbered as one.</summary>
        private static void ComputeCanonicalCode(byte[] firstNbits, byte[] firstBits, int firstSize, byte[] secondNbits, ushort[] secondBits, int secondSize)
        {
            const int MaxLength = 15;
            Span<int> lengthCounts = stackalloc int[MaxLength + 1];
            lengthCounts.Clear();
            for (int i = 0; i < firstSize; i++)
            {
                lengthCounts[firstNbits[i]]++;
            }

            for (int i = 0; i < secondSize; i++)
            {
                lengthCounts[secondNbits[i]]++;
            }

            Span<ushort> next = stackalloc ushort[MaxLength + 1];
            next.Clear();
            int code = 0;
            for (int i = 1; i <= MaxLength; i++)
            {
                code = (code + lengthCounts[i - 1]) << 1;
                next[i] = (ushort)code;
            }

            for (int i = 0; i < firstSize; i++)
            {
                firstBits[i] = (byte)BitReverse(firstNbits[i], next[firstNbits[i]]++);
            }

            for (int i = 0; i < secondSize; i++)
            {
                secondBits[i] = BitReverse(secondNbits[i], next[secondNbits[i]]++);
            }
        }

        /// <summary>
        /// Gets code lengths within the limits, with the Kraft sum exactly 1, that minimize the coded size: a dynamic program
        /// over symbols and the code space used (libjxl's ComputeCodeLengthsNonZero); symbols that never occur get length 0.
        /// </summary>
        private static void ComputeCodeLengths(ulong[] freqs, int n, byte[] minIn, byte[] maxIn, byte[] nbits)
        {
            ulong[] compact = new ulong[MaxSymbols + 1];
            byte[] min = new byte[MaxSymbols + 1];
            byte[] max = new byte[MaxSymbols + 1];
            int ni = 0;
            for (int i = 0; i < n; i++)
            {
                if (freqs[i] != 0)
                {
                    compact[ni] = freqs[i];
                    min[ni] = minIn[Math.Min(i, minIn.Length - 1)];
                    max[ni] = maxIn[Math.Min(i, maxIn.Length - 1)];
                    ni++;
                }
            }

            byte[] bits = new byte[MaxSymbols + 1];
            NonZero(compact, ni, min, max, bits);
            ni = 0;
            for (int i = 0; i < n; i++)
            {
                nbits[i] = freqs[i] != 0 ? bits[ni++] : (byte)0;
            }
        }

        private static void NonZero(ulong[] freqs, int n, byte[] min, byte[] max, byte[] nbits)
        {
            int precision = 0;
            int shortest = 255;
            for (int i = 0; i < n; i++)
            {
                if (min[i] < 1)
                {
                    min[i] = 1;
                }

                precision = Math.Max(max[i], precision);
                shortest = Math.Min(min[i], shortest);
            }

            // When every length is at least 2, act as if the shortest were 1. Costs fit in 32 bits for all but huge images.
            precision -= shortest - 1;
            ulong sum = 0;
            for (int i = 0; i < n; i++)
            {
                sum += freqs[i];
            }

            ulong infinity = sum * (ulong)precision;
            if (infinity < uint.MaxValue / 2)
            {
                Dynamic<uint>(freqs, n, min, max, nbits, precision, (uint)infinity);
            }
            else
            {
                Dynamic<ulong>(freqs, n, min, max, nbits, precision, infinity);
            }
        }

        /// <summary>The dynamic program: d[sym, used] is the least cost of the first symbols using that much code space.</summary>
        private static void Dynamic<T>(ulong[] freqs, int n, byte[] min, byte[] max, byte[] nbits, int precision, T infinity)
            where T : unmanaged, IBinaryInteger<T>
        {
            int span = (1 << precision) + 1;
            T[] d = ArrayPool<T>.Shared.Rent(span * (n + 1));
            Array.Fill(d, infinity, 0, span * (n + 1));
            d[0] = T.Zero;
            int full = 1 << precision;
            for (int sym = 0; sym < n; sym++)
            {
                Span<T> from = d.AsSpan(sym * span, span);
                Span<T> to = d.AsSpan((sym + 1) * span, span);
                for (int b = min[sym]; b <= max[sym]; b++)
                {
                    int delta = 1 << (precision - b);
                    T add = T.CreateTruncating(freqs[sym] * (ulong)b);
                    int count = full - delta + 1;
                    Span<T> src = from[..count];
                    Span<T> dst = to.Slice(delta, count);
                    int off = 0;
                    if (Vector256.IsHardwareAccelerated && Vector256<T>.IsSupported)
                    {
                        Vector256<T> vAdd = Vector256.Create(add);
                        for (; off + Vector256<T>.Count <= count; off += Vector256<T>.Count)
                        {
                            Vector256<T> v = Vector256.Min(Vector256.Create<T>(src[off..]) + vAdd, Vector256.Create<T>(dst[off..]));
                            v.CopyTo(dst[off..]);
                        }
                    }

                    for (; off < count; off++)
                    {
                        T cost = src[off] + add;
                        if (cost < dst[off])
                        {
                            dst[off] = cost;
                        }
                    }
                }
            }

            int s = n;
            int o = full;
            while (s-- > 0)
            {
                for (int b = min[s]; b <= max[s]; b++)
                {
                    int delta = 1 << (precision - b);
                    if (delta <= o && d[((s + 1) * span) + o] == d[(s * span) + o - delta] + T.CreateTruncating(freqs[s] * (ulong)b))
                    {
                        o -= delta;
                        nbits[s] = (byte)b;
                        break;
                    }
                }
            }

            ArrayPool<T>.Shared.Return(d);
        }
    }

    /// <summary>What a row processor hands each chunk to: a token counter or the encoder.</summary>
    private abstract class ChunkSink
    {
        internal abstract void Chunk(int run, uint[] residuals, int skip, int n);

        internal abstract void Finish(int run);
    }

    /// <summary>Counts tokens of sampled rows (ChunkSampleCollector); the last run is not counted, its length unknown.</summary>
    private sealed class Collector(ulong[] raw, ulong[] lz) : ChunkSink
    {
        internal override void Chunk(int run, uint[] residuals, int skip, int n)
        {
            if (run != 0)
            {
                raw[0]++;
                lz[Uint400((uint)(run - Lz77MinLength - 1)).Token]++;
            }

            for (int i = skip; i < n; i++)
            {
                raw[Uint000(residuals[i]).Token]++;
            }
        }

        internal override void Finish(int run)
        {
        }
    }

    /// <summary>Writes chunks with the channel's prefix code (ChunkEncoder).</summary>
    private sealed class Encoder(PrefixCode code, FastBitWriter output) : ChunkSink
    {
        private readonly byte[] _nbits = code.RawNbitsTable;
        private readonly byte[] _bits = code.RawBitsTable;
        private readonly CodeTables _tables = new(code.RawNbitsTable, code.RawBitsTable);

        internal override void Chunk(int run, uint[] residuals, int skip, int n)
        {
            code.WriteRun(output, run);
            if (skip == 0 && n == ChunkSize && Avx2.IsSupported)
            {
                output.WriteChunk(residuals, _tables);
                return;
            }

            output.WriteResiduals(residuals, skip, n, _nbits, _bits);
        }

        internal override void Finish(int run) => code.WriteRun(output, run);
    }

    /// <summary>A prefix code's lengths and codes for tokens 0 to 23, eight to a vector, for lookups by permutation.</summary>
    internal sealed class CodeTables
    {
        internal CodeTables(byte[] nbits, byte[] codes)
        {
            int[] n = new int[24];
            int[] c = new int[24];
            for (int i = 0; i < Math.Min(24, nbits.Length); i++)
            {
                n[i] = nbits[i];
                c[i] = codes[i];
            }

            N0 = Vector256.Create(n.AsSpan(0, 8));
            N1 = Vector256.Create(n.AsSpan(8, 8));
            N2 = Vector256.Create(n.AsSpan(16, 8));
            C0 = Vector256.Create(c.AsSpan(0, 8));
            C1 = Vector256.Create(c.AsSpan(8, 8));
            C2 = Vector256.Create(c.AsSpan(16, 8));
        }

        internal Vector256<int> N0 { get; }

        internal Vector256<int> N1 { get; }

        internal Vector256<int> N2 { get; }

        internal Vector256<int> C0 { get; }

        internal Vector256<int> C1 { get; }

        internal Vector256<int> C2 { get; }

        /// <summary>Looks each token up in a table of 24 held as three vectors.</summary>
        internal static Vector256<int> Lookup(Vector256<int> token, Vector256<int> t0, Vector256<int> t1, Vector256<int> t2)
        {
            Vector256<int> r0 = Avx2.PermuteVar8x32(t0, token);
            Vector256<int> r1 = Avx2.PermuteVar8x32(t1, token);
            Vector256<int> r2 = Avx2.PermuteVar8x32(t2, token);
            Vector256<int> high = Vector256.GreaterThan(token, Vector256.Create(15));
            Vector256<int> mid = Vector256.GreaterThan(token, Vector256.Create(7));
            return Vector256.ConditionalSelect(high, r2, Vector256.ConditionalSelect(mid, r1, r0));
        }
    }

    /// <summary>Predicts a row in chunks of 16 and passes them on, carrying runs of zero residuals across chunks (ChannelRowProcessor).</summary>
    private sealed class RowProcessor(ChunkSink sink)
    {
        private readonly uint[] _residuals = new uint[ChunkSize];
        private int _run;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void ProcessRow(int[] row, int r, int[] left, int l, int[] top, int t, int[] topleft, int tl, int xs)
        {
            for (int x = 0; x < xs; x += ChunkSize)
            {
                ProcessChunk(row, r + x, left, l + x, top, t + x, topleft, tl + x, Math.Min(ChunkSize, xs - x));
            }
        }

        internal void Finish() => sink.Finish(_run);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ProcessChunk(int[] row, int r, int[] left, int l, int[] top, int t, int[] topleft, int tl, int n)
        {
            int prefix;
            if (Vector256.IsHardwareAccelerated)
            {
                // The clamped gradient as the median of left, top and their gradient; the residual packed by sign.
                ref int rr = ref MemoryMarshal.GetArrayDataReference(row);
                ref int rl = ref MemoryMarshal.GetArrayDataReference(left);
                ref int rt = ref MemoryMarshal.GetArrayDataReference(top);
                ref int rtl = ref MemoryMarshal.GetArrayDataReference(topleft);
                ref uint res = ref MemoryMarshal.GetArrayDataReference(_residuals);
                uint zeros = 0;
                for (int i = 0; i < ChunkSize; i += 8)
                {
                    Vector256<int> px = Vector256.LoadUnsafe(ref rr, (nuint)(r + i));
                    Vector256<int> a = Vector256.LoadUnsafe(ref rl, (nuint)(l + i));
                    Vector256<int> b = Vector256.LoadUnsafe(ref rt, (nuint)(t + i));
                    Vector256<int> c = Vector256.LoadUnsafe(ref rtl, (nuint)(tl + i));
                    Vector256<int> grad = a + b - c;
                    Vector256<int> pred = Vector256.Min(Vector256.Max(grad, Vector256.Min(a, b)), Vector256.Max(a, b));
                    Vector256<int> d = px - pred;
                    Vector256<uint> packed = ((d << 1) ^ Vector256.ShiftRightArithmetic(d, 31)).AsUInt32();
                    packed.StoreUnsafe(ref res, (nuint)i);
                    zeros |= Vector256.Equals(packed, Vector256<uint>.Zero).ExtractMostSignificantBits() << i;
                }

                prefix = BitOperations.TrailingZeroCount(~zeros);
            }
            else
            {
                prefix = 0;
                bool leading = true;
                for (int i = 0; i < ChunkSize; i++)
                {
                    uint v = PackSigned(row[r + i] - ClampedGradient(left[l + i], top[t + i], topleft[tl + i]));
                    _residuals[i] = v;
                    if (leading && v == 0)
                    {
                        prefix++;
                    }
                    else
                    {
                        leading = false;
                    }
                }
            }

            prefix = Math.Min(n, prefix);
            if (prefix == n && (_run > 0 || prefix > Lz77MinLength))
            {
                _run += prefix;
            }
            else if (prefix + _run > Lz77MinLength)
            {
                sink.Chunk(_run + prefix, _residuals, prefix, n);
                _run = 0;
            }
            else
            {
                sink.Chunk(0, _residuals, 0, n);
            }
        }

        /// <summary>libjxl's branch-free clamped gradient.</summary>
        private static int ClampedGradient(int left, int top, int topleft)
        {
            int ac = left - topleft;
            int ab = left - top;
            int bc = top - topleft;
            int grad = ac + top;
            int d = ab ^ bc;
            int clamp = d < 0 ? top : left;
            int s = ac ^ bc;
            return s < 0 ? grad : clamp;
        }
    }

    /// <summary>A rectangle of the image, its channels converted row by row (YCoCg, or palette indices) and predicted (ProcessImageArea).</summary>
    private sealed class Group(int[][] channels, int width, int x0, int y0, int xs, int ys, int nbChans, int[]? lookup)
    {
        private const int Padding = 32;

        internal int Channels => lookup is null ? nbChans : 1;

        internal void Process(int yskip, ChunkSink[] sinks)
        {
            int n = Channels;
            int[][][] rows = [.. Enumerable.Range(0, n).Select(_ => new[] { new int[GroupDim + (2 * Padding)], new int[GroupDim + (2 * Padding)] })];
            RowProcessor[] processors = [.. sinks.Select(s => new RowProcessor(s))];
            for (int y = 0; y < ys; y++)
            {
                int src = ((y0 + y) * width) + x0;
                FillRow(rows, y & 1, src);

                for (int c = 0; c < n; c++)
                {
                    int[] cur = rows[c][y & 1];
                    int[] prev = rows[c][(y - 1) & 1];

                    // x = 0: the left is the top, or 0 on the first row; the top-left likewise.
                    cur[Padding - 1] = y > 0 ? prev[Padding] : 0;
                    prev[Padding - 1] = y > 0 ? prev[Padding] : 0;
                }

                if (y < yskip)
                {
                    continue;
                }

                for (int c = 0; c < n; c++)
                {
                    int[] cur = rows[c][y & 1];
                    int[] prev = rows[c][(y - 1) & 1];
                    if (y == 0)
                    {
                        processors[c].ProcessRow(cur, Padding, cur, Padding - 1, cur, Padding - 1, cur, Padding - 1, xs);
                    }
                    else
                    {
                        processors[c].ProcessRow(cur, Padding, cur, Padding - 1, prev, Padding, prev, Padding - 1, xs);
                    }
                }
            }

            foreach (RowProcessor p in processors)
            {
                p.Finish();
            }
        }

        /// <summary>Converts one row of every channel: palette indices, the samples as they are, or YCoCg with alpha as it is.</summary>
        private void FillRow(int[][][] rows, int which, int src)
        {
            if (lookup is not null)
            {
                int[] dst = rows[0][which];
                for (int x = 0; x < xs; x++)
                {
                    uint p = 0;
                    for (int k = 0; k < nbChans; k++)
                    {
                        p |= (uint)channels[k][src + x] << (8 * k);
                    }

                    dst[Padding + x] = lookup[Hash(p)];
                }

                return;
            }

            if (nbChans <= 2)
            {
                for (int c = 0; c < nbChans; c++)
                {
                    Array.Copy(channels[c], src, rows[c][which], Padding, xs);
                }

                return;
            }

            if (nbChans == 4)
            {
                Array.Copy(channels[3], src, rows[3][which], Padding, xs);
            }

            int[] r = channels[0];
            int[] g = channels[1];
            int[] b = channels[2];
            int[] dy = rows[0][which];
            int[] dco = rows[1][which];
            int[] dcg = rows[2][which];
            int x0 = 0;
            if (Vector256.IsHardwareAccelerated)
            {
                ref int rr = ref MemoryMarshal.GetArrayDataReference(r);
                ref int rg = ref MemoryMarshal.GetArrayDataReference(g);
                ref int rb = ref MemoryMarshal.GetArrayDataReference(b);
                ref int ry = ref MemoryMarshal.GetArrayDataReference(dy);
                ref int rco = ref MemoryMarshal.GetArrayDataReference(dco);
                ref int rcg = ref MemoryMarshal.GetArrayDataReference(dcg);
                for (; x0 + 8 <= xs; x0 += 8)
                {
                    Vector256<int> vr = Vector256.LoadUnsafe(ref rr, (nuint)(src + x0));
                    Vector256<int> vg = Vector256.LoadUnsafe(ref rg, (nuint)(src + x0));
                    Vector256<int> vb = Vector256.LoadUnsafe(ref rb, (nuint)(src + x0));
                    Vector256<int> co = vr - vb;
                    Vector256<int> tmp = vb + Vector256.ShiftRightArithmetic(co, 1);
                    Vector256<int> cg = vg - tmp;
                    (tmp + Vector256.ShiftRightArithmetic(cg, 1)).StoreUnsafe(ref ry, (nuint)(Padding + x0));
                    co.StoreUnsafe(ref rco, (nuint)(Padding + x0));
                    cg.StoreUnsafe(ref rcg, (nuint)(Padding + x0));
                }
            }

            for (int x = x0; x < xs; x++)
            {
                int co = r[src + x] - b[src + x];
                int tmp = b[src + x] + (co >> 1);
                int cg = g[src + x] - tmp;
                dy[Padding + x] = tmp + (cg >> 1);
                dco[Padding + x] = co;
                dcg[Padding + x] = cg;
            }
        }
    }

    /// <summary>A bit writer with a 64-bit buffer, stored whole at each write (libjxl's AddBits), that can append another writer at any bit offset.</summary>
    internal sealed class FastBitWriter(int capacity = 4096)
    {
        private byte[] _data = ArrayPool<byte>.Shared.Rent(Math.Max(capacity, 64));
        private int _bytes;
        private ulong _buffer;
        private int _bits;

        /// <summary>Writes up to 56 bits; <paramref name="bits"/> has nothing above <paramref name="count"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Write(int count, ulong bits)
        {
            if (count > 56)
            {
                Write(32, bits & 0xFFFF_FFFF);
                Write(count - 32, bits >> 32);
                return;
            }

            if (_bytes + 8 > _data.Length)
            {
                Grow(_data.Length * 2);
            }

            _buffer |= bits << _bits;
            _bits += count;
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_data), _bytes), BitConverter.IsLittleEndian ? _buffer : BinaryPrimitives.ReverseEndianness(_buffer));
            int n = _bits >> 3;
            _bytes += n;
            _buffer = n == 8 ? 0 : _buffer >> (n * 8);
            _bits &= 7;
        }

        internal void ZeroPadToByte()
        {
            if (_bits > 0)
            {
                Write(8 - _bits, 0);
            }
        }

        /// <summary>Writes residuals [skip, n) with a prefix code over their 0, 0, 0 hybrid integer tokens, the buffer kept in registers.</summary>
        internal void WriteResiduals(uint[] residuals, int skip, int n, byte[] nbits, byte[] codes)
        {
            // At most 16 residuals of 15 + 31 bits each.
            if (_bytes + (16 * 8) + 8 > _data.Length)
            {
                Grow(Math.Max(_data.Length * 2, _bytes + 256));
            }

            ref byte data = ref MemoryMarshal.GetArrayDataReference(_data);
            ulong buffer = _buffer;
            int bits = _bits;
            int bytes = _bytes;
            for (int i = skip; i < n; i++)
            {
                // Without branches: the token is the value's bit length, the raw bits all but its top bit (none for 0).
                uint v = residuals[i];
                int token = 32 - BitOperations.LeadingZeroCount(v);
                int raw = Math.Max(token - 1, 0);
                int rn = nbits[token];
                int count = rn + raw;
                ulong value = codes[token] | ((ulong)(v & ((1u << raw) - 1)) << rn);
                buffer |= value << bits;
                bits += count;
                if (bits >= 32)
                {
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref data, bytes), BitConverter.IsLittleEndian ? (uint)buffer : BinaryPrimitives.ReverseEndianness((uint)buffer));
                    bytes += 4;
                    buffer >>= 32;
                    bits -= 32;
                }
            }

            _buffer = buffer;
            _bits = bits;
            _bytes = bytes;

            // Keep the writer's invariant: fewer than 8 bits held.
            while (_bits >= 8)
            {
                _data[_bytes++] = (byte)_buffer;
                _buffer >>= 8;
                _bits -= 8;
            }
        }

        /// <summary>
        /// Writes a whole chunk of 16 residuals: each one's token, bit count and bits worked out eight at a time (the token from
        /// the exponent of the value as a float, exact for the 24-bit residuals of at most 16-bit samples), then merged in order.
        /// </summary>
        internal void WriteChunk(uint[] residuals, CodeTables tables)
        {
            if (_bytes + (16 * 8) + 8 > _data.Length)
            {
                Grow(Math.Max(_data.Length * 2, _bytes + 256));
            }

            Span<uint> counts = stackalloc uint[ChunkSize];
            Span<uint> values = stackalloc uint[ChunkSize];
            ref uint res = ref MemoryMarshal.GetArrayDataReference(residuals);
            for (int h = 0; h < ChunkSize; h += 8)
            {
                Vector256<uint> v = Vector256.LoadUnsafe(ref res, (nuint)h);
                Vector256<int> exponent = Avx2.ShiftRightLogical(Avx.ConvertToVector256Single(v.AsInt32()).AsInt32(), 23);
                Vector256<int> token = Vector256.Max(exponent - Vector256.Create(126), Vector256<int>.Zero);
                Vector256<int> raw = Vector256.Max(token - Vector256<int>.One, Vector256<int>.Zero);
                Vector256<int> rn = CodeTables.Lookup(token, tables.N0, tables.N1, tables.N2);
                Vector256<int> code = CodeTables.Lookup(token, tables.C0, tables.C1, tables.C2);
                Vector256<uint> mask = Avx2.ShiftLeftLogicalVariable(Vector256<uint>.One, raw.AsUInt32()) - Vector256<uint>.One;
                Vector256<uint> value = code.AsUInt32() | Avx2.ShiftLeftLogicalVariable(v & mask, rn.AsUInt32());
                (rn + raw).AsUInt32().StoreUnsafe(ref MemoryMarshal.GetReference(counts), (nuint)h);
                value.StoreUnsafe(ref MemoryMarshal.GetReference(values), (nuint)h);
            }

            ref byte data = ref MemoryMarshal.GetArrayDataReference(_data);
            ulong buffer = _buffer;
            int bits = _bits;
            int bytes = _bytes;
            for (int i = 0; i < ChunkSize; i++)
            {
                buffer |= (ulong)values[i] << bits;
                bits += (int)counts[i];
                if (bits >= 32)
                {
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref data, bytes), BitConverter.IsLittleEndian ? (uint)buffer : BinaryPrimitives.ReverseEndianness((uint)buffer));
                    bytes += 4;
                    buffer >>= 32;
                    bits -= 32;
                }
            }

            _buffer = buffer;
            _bits = bits;
            _bytes = bytes;
            while (_bits >= 8)
            {
                _data[_bytes++] = (byte)_buffer;
                _buffer >>= 8;
                _bits -= 8;
            }
        }

        /// <summary>Appends every bit of <paramref name="other"/>, seven bytes at a time.</summary>
        internal void Append(FastBitWriter other)
        {
            int i = 0;
            for (; i + 7 <= other._bytes; i += 7)
            {
                Write(56, BinaryPrimitives.ReadUInt64LittleEndian(other._data.AsSpan(i)) & 0xFF_FFFF_FFFF_FFFF);
            }

            for (; i < other._bytes; i++)
            {
                Write(8, other._data[i]);
            }

            Write(other._bits, other._buffer);
        }

        internal byte[] ToArray()
        {
            ZeroPadToByte();
            return _data.AsSpan(0, _bytes).ToArray();
        }

        /// <summary>Gives the buffer back to the pool; the writer is not used after.</summary>
        internal void Release()
        {
            ArrayPool<byte>.Shared.Return(_data);
            _data = [];
        }

        private void Grow(int size)
        {
            byte[] bigger = ArrayPool<byte>.Shared.Rent(size);
            _data.AsSpan(0, _bytes).CopyTo(bigger);
            ArrayPool<byte>.Shared.Return(_data);
            _data = bigger;
        }
    }
}
