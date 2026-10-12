using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Text;

namespace Egami.Formats.Exr;

/// <summary>
/// DWAA and DWAB, DreamWorks Animation's lossy compression: channels the block's rules call lossy are stored as 8 × 8 DCT
/// blocks (in a perceptual encoding, R, G and B of a layer as Y'CbCr), quantized to half, their AC terms run-length and
/// Huffman or deflate coded and their DC terms deflated; alpha-like channels are run-length coded and the rest deflated.
/// Translated from OpenEXR's internal_dwa_*.h (BSD-3-Clause; see THIRD-PARTY-NOTICES). The inverse DCT follows OpenEXR's AVX
/// form; on processors where OpenEXR runs its SSE2 or scalar form instead, values may differ by a half-float step.
/// </summary>
internal static class ExrDwa
{
    private const int Unknown = 0;
    private const int LossyDct = 1;
    private const int Rle = 2;

    private const int StaticHuffman = 0;
    private const int Deflate = 1;

    /// <summary>Position k of a block in row order holds coefficient Zig[k] of the zigzag order.</summary>
    private static readonly byte[] Zig =
    [
        0, 1, 5, 6, 14, 15, 27, 28, 2, 4, 7, 13, 16, 26, 29, 42, 3, 8, 12, 17, 25, 30, 41, 43, 9, 11, 18, 24, 31, 40, 44, 53,
        10, 19, 23, 32, 39, 45, 52, 54, 20, 22, 33, 38, 46, 51, 55, 60, 21, 34, 37, 47, 50, 56, 59, 61, 35, 36, 48, 49, 57, 58, 62, 63,
    ];

    /// <summary>Coefficient k of the zigzag order goes to position Unzig[k] of a block in row order.</summary>
    private static readonly byte[] Unzig = BuildUnzig();

    /// <summary>The rules of version 0 and 1 blocks, which carry none of their own.</summary>
    private static readonly Rule[] LegacyRules = BuildLegacyRules();

    /// <summary>The table from the perceptual encoding back to linear half, as OpenEXR's dwaToLinear.</summary>
    private static readonly Lazy<ushort[]> s_toLinear = new(BuildToLinear);

    public static bool Decompress(in ExrBlock block, ReadOnlySpan<byte> source, Span<byte> destination)
    {
        try
        {
            return Run(block, source, destination);
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool Run(in ExrBlock block, ReadOnlySpan<byte> source, Span<byte> destination)
    {
        const int Sizes = 11;
        if (source.Length < Sizes * 8)
        {
            return false;
        }

        Span<long> counters = stackalloc long[Sizes];
        for (int i = 0; i < Sizes; i++)
        {
            counters[i] = BinaryPrimitives.ReadInt64LittleEndian(source[(i * 8)..]);
            if (counters[i] < 0 || counters[i] > int.MaxValue)
            {
                return false;
            }
        }

        long version = counters[0], unknownUncompressed = counters[1], unknownCompressed = counters[2], acCompressed = counters[3];
        long dcCompressed = counters[4], rleCompressed = counters[5], rleUncompressed = counters[6], rleRaw = counters[7];
        long acCount = counters[8], dcCount = counters[9], acCompression = counters[10];
        if (version > 2)
        {
            return false;
        }

        int pos = Sizes * 8;
        Rule[] rules = LegacyRules;
        if (version >= 2)
        {
            if (pos + 2 > source.Length)
            {
                return false;
            }

            int ruleSize = BinaryPrimitives.ReadUInt16LittleEndian(source[pos..]);
            if (ruleSize < 2 || pos + ruleSize > source.Length)
            {
                return false;
            }

            rules = ReadRules(source.Slice(pos + 2, ruleSize - 2));
            pos += ruleSize;
        }

        long total = unknownCompressed + acCompressed + dcCompressed + rleCompressed;
        if (total > source.Length - pos)
        {
            return false;
        }

        ReadOnlySpan<byte> unknownData = source.Slice(pos, (int)unknownCompressed);
        ReadOnlySpan<byte> acData = source.Slice(pos + (int)unknownCompressed, (int)acCompressed);
        ReadOnlySpan<byte> dcData = source.Slice(pos + (int)(unknownCompressed + acCompressed), (int)dcCompressed);
        ReadOnlySpan<byte> rleData = source.Slice(pos + (int)(unknownCompressed + acCompressed + dcCompressed), (int)rleCompressed);

        // Classify the channels: the last matching rule gives the scheme; R, G and B of one prefix form a color set.
        ExrChannelInfo[] channels = block.Part.Channels;
        int n = channels.Length;
        int[] scheme = new int[n];
        List<(string Prefix, int[] Idx)> prefixes = [];
        for (int c = 0; c < n; c++)
        {
            string name = channels[c].Name;
            int dot = name.LastIndexOf('.');
            string suffix = dot >= 0 ? name[(dot + 1)..] : name;
            string prefix = dot >= 0 ? name[..(dot + 1)] : string.Empty;
            int mapIndex = prefixes.FindIndex(p => p.Prefix == prefix);
            if (mapIndex < 0)
            {
                prefixes.Add((prefix, [-1, -1, -1]));
                mapIndex = prefixes.Count - 1;
            }

            foreach (Rule rule in rules)
            {
                if (rule.Type == channels[c].Type && string.Equals(rule.Suffix, suffix, rule.CaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                {
                    scheme[c] = rule.Scheme;
                    if (rule.CscIndex >= 0)
                    {
                        prefixes[mapIndex].Idx[rule.CscIndex] = c;
                    }
                }
            }
        }

        List<int[]> colorSets = [];
        foreach ((string _, int[] idx) in prefixes)
        {
            if (idx[0] < 0 || idx[1] < 0 || idx[2] < 0)
            {
                continue;
            }

            ExrChannelInfo r = channels[idx[0]], g = channels[idx[1]], b = channels[idx[2]];
            if (r.XSampling == g.XSampling && r.XSampling == b.XSampling && r.YSampling == g.YSampling && r.YSampling == b.YSampling)
            {
                colorSets.Add(idx);
            }
        }

        // Each channel's lines in the output, which is cleared first.
        destination.Clear();
        int[] widths = new int[n], heights = new int[n];
        List<int>[] rows = new List<int>[n];
        for (int c = 0; c < n; c++)
        {
            widths[c] = ExrBlock.Sampled(block.X0, block.X1, channels[c].XSampling);
            heights[c] = ExrBlock.Sampled(block.Y0, block.Y1, channels[c].YSampling);
            rows[c] = [];
        }

        int outPos = 0;
        for (int y = block.Y0; y <= block.Y1; y++)
        {
            for (int c = 0; c < n; c++)
            {
                if (y % channels[c].YSampling != 0)
                {
                    continue;
                }

                rows[c].Add(outPos);
                outPos += widths[c] * channels[c].Bytes;
            }
        }

        if (outPos != destination.Length)
        {
            return false;
        }

        // The deflated leftovers, the run-length coded channels, and the AC and DC terms.
        long unknownNeeded = 0, rleNeeded = 0, blocks = 0;
        for (int c = 0; c < n; c++)
        {
            long size = (long)widths[c] * heights[c] * channels[c].Bytes;
            unknownNeeded += scheme[c] == Unknown ? size : 0;
            rleNeeded += scheme[c] == Rle ? size : 0;
            blocks += scheme[c] == LossyDct ? ((long)(widths[c] + 7) / 8) * ((heights[c] + 7) / 8) : 0;
        }

        if ((unknownNeeded > 0 && (unknownUncompressed < unknownNeeded || unknownCompressed == 0))
            || (unknownNeeded == 0 && (unknownUncompressed > 0 || unknownCompressed > 0))
            || (rleNeeded > 0 && (rleRaw < rleNeeded || rleCompressed == 0))
            || (rleNeeded == 0 && (rleRaw > 0 || rleCompressed > 0 || rleUncompressed > 0))
            || unknownUncompressed > destination.Length || rleRaw > destination.Length
            || rleUncompressed > (rleRaw * 2) + 1024 || acCount > blocks * 63 || dcCount > blocks)
        {
            return false;
        }

        byte[] unknownBuffer = new byte[unknownUncompressed];
        if (unknownUncompressed > 0 && ExrCodec.InflateUpTo(unknownData, unknownBuffer) != unknownUncompressed)
        {
            return false;
        }

        byte[] rleBuffer = new byte[rleRaw];
        if (rleRaw > 0)
        {
            byte[] packed = new byte[rleUncompressed];
            if (ExrCodec.InflateUpTo(rleData, packed) != rleUncompressed || !UnRle(packed, rleBuffer))
            {
                return false;
            }
        }

        ushort[] ac = new ushort[acCount];
        if (acCompressed > 0)
        {
            bool ok = acCompression switch
            {
                StaticHuffman => ExrHuffman.Decode(acData, ac),
                Deflate => ExrCodec.InflateUpTo(acData, MemoryMarshal.AsBytes(ac.AsSpan())) == acCount * 2,
                _ => false,
            };
            if (!ok)
            {
                return false;
            }
        }

        ushort[] dc = new ushort[dcCount];
        if (dcCompressed > 0)
        {
            byte[] raw = new byte[dcCount * 2];
            if (ExrCodec.InflateUpTo(dcData, raw) != raw.Length)
            {
                return false;
            }

            ExrCodec.Unpredict(raw, MemoryMarshal.AsBytes(dc.AsSpan()));
        }
        else if (dcCount != 0)
        {
            return false;
        }

        int acPos = 0, dcPos = 0;
        bool[] done = new bool[n];
        ushort[] toLinear = s_toLinear.Value;
        foreach (int[] set in colorSets)
        {
            if (scheme[set[0]] != LossyDct || scheme[set[1]] != LossyDct || scheme[set[2]] != LossyDct
                || !DecodeDct(destination, rows, set, channels, widths[set[0]], heights[set[0]], ac, ref acPos, dc, ref dcPos, toLinear))
            {
                return false;
            }

            done[set[0]] = done[set[1]] = done[set[2]] = true;
        }

        int unknownPos = 0, rlePos = 0;
        for (int c = 0; c < n; c++)
        {
            ExrChannelInfo channel = channels[c];
            if (scheme[c] == Rle)
            {
                // Each byte of a sample in its own plane, the channel's planes one after another.
                int count = widths[c] * heights[c];
                int bytes = channel.Bytes;
                if (rlePos + (count * bytes) > rleBuffer.Length)
                {
                    return false;
                }

                int i = 0;
                foreach (int row in rows[c])
                {
                    Span<byte> line = destination.Slice(row, widths[c] * bytes);
                    if (bytes == 2)
                    {
                        Interleave(rleBuffer.AsSpan(rlePos + i, widths[c]), rleBuffer.AsSpan(rlePos + count + i, widths[c]), line);
                        i += widths[c];
                        continue;
                    }

                    for (int x = 0; x < widths[c]; x++, i++)
                    {
                        for (int b = 0; b < bytes; b++)
                        {
                            line[(x * bytes) + b] = rleBuffer[rlePos + (b * count) + i];
                        }
                    }
                }

                rlePos += count * bytes;
            }
            else if (done[c])
            {
                continue;
            }
            else if (scheme[c] == LossyDct)
            {
                if (!DecodeDct(destination, rows, [c], channels, widths[c], heights[c], ac, ref acPos, dc, ref dcPos, channel.Linear ? null : toLinear))
                {
                    return false;
                }
            }
            else
            {
                int lineBytes = widths[c] * channel.Bytes;
                foreach (int row in rows[c])
                {
                    if (unknownPos + lineBytes > unknownBuffer.Length)
                    {
                        return false;
                    }

                    unknownBuffer.AsSpan(unknownPos, lineBytes).CopyTo(destination.Slice(row, lineBytes));
                    unknownPos += lineBytes;
                }
            }
        }

        return true;
    }

    /// <summary>Decodes the DCT blocks of one channel, or of a color set (Y'CbCr back to R'G'B'), into their lines.</summary>
    private static bool DecodeDct(Span<byte> destination, List<int>[] rows, int[] set, ExrChannelInfo[] channels, int width, int height, ushort[] ac, ref int acPos, ushort[] dc, ref int dcPos, ushort[]? toLinear)
    {
        if (width == 0 || height == 0)
        {
            return true;
        }

        int comps = set.Length;
        int blocksX = (width + 7) / 8, blocksY = (height + 7) / 8;
        int leftoverX = width - ((blocksX - 1) * 8), leftoverY = height - ((blocksY - 1) * 8);
        if (dc.Length - dcPos < comps * blocksX * blocksY)
        {
            return false;
        }

        ushort[] rowBlockArray = ArrayPool<ushort>.Shared.Rent(comps * blocksX * 64);
        try
        {
            Span<ushort> rowBlock = rowBlockArray.AsSpan(0, comps * blocksX * 64);
            Span<float> data = stackalloc float[comps * 64];
            Span<ushort> ordered = stackalloc ushort[64];
            ref byte unzig = ref MemoryMarshal.GetArrayDataReference(Unzig);
            int dcIndex = 0;
            for (int by = 0; by < blocksY; by++)
            {
                int maxY = by == blocksY - 1 ? leftoverY : 8;
                for (int bx = 0; bx < blocksX; bx++)
                {
                    bool constant = true;
                    for (int k = 0; k < comps; k++)
                    {
                        ordered.Clear();
                        ushort dcValue = dc[dcPos + (k * blocksX * blocksY) + dcIndex];
                        ordered[0] = dcValue;
                        int lastNonZero = 0;
                        int comp = 1;
                        while (comp < 64)
                        {
                            if (acPos >= ac.Length)
                            {
                                return false;
                            }

                            ushort value = ac[acPos++];
                            if ((value & 0xFF00) == 0xFF00)
                            {
                                int count = value & 0xFF;
                                comp += count == 0 ? 64 : count;
                            }
                            else
                            {
                                lastNonZero = comp;
                                ordered[Unsafe.Add(ref unzig, comp)] = value;
                                comp++;
                            }
                        }

                        Span<float> d = data.Slice(k * 64, 64);
                        if (lastNonZero == 0)
                        {
                            d.Fill((float)BitConverter.UInt16BitsToHalf(dcValue) * 3.535536e-01f * 3.535536e-01f);
                        }
                        else
                        {
                            constant = false;
                            ToSingle(ordered, d);
                            InverseDct(d);
                        }
                    }

                    dcIndex++;
                    if (comps == 3 && !constant && Vector256.IsHardwareAccelerated)
                    {
                        // The same operations in the same order on eight samples at a time, so the same bits.
                        ref float c0 = ref MemoryMarshal.GetReference(data);
                        for (int i = 0; i < 64; i += 8)
                        {
                            Vector256<float> s0 = Vector256.LoadUnsafe(ref c0, (nuint)i);
                            Vector256<float> s1 = Vector256.LoadUnsafe(ref c0, (nuint)(64 + i));
                            Vector256<float> s2 = Vector256.LoadUnsafe(ref c0, (nuint)(128 + i));
                            (s0 + (Vector256.Create(1.5747f) * s2)).StoreUnsafe(ref c0, (nuint)i);
                            (s0 - (Vector256.Create(0.1873f) * s1) - (Vector256.Create(0.4682f) * s2)).StoreUnsafe(ref c0, (nuint)(64 + i));
                            (s0 + (Vector256.Create(1.8556f) * s1)).StoreUnsafe(ref c0, (nuint)(128 + i));
                        }
                    }
                    else if (comps == 3)
                    {
                        Span<float> c0 = data[..64], c1 = data.Slice(64, 64), c2 = data.Slice(128, 64);
                        for (int i = 0, count = constant ? 1 : 64; i < count; i++)
                        {
                            float s0 = c0[i], s1 = c1[i], s2 = c2[i];
                            c0[i] = s0 + (1.5747f * s2);
                            c1[i] = s0 - (0.1873f * s1) - (0.4682f * s2);
                            c2[i] = s0 + (1.8556f * s1);
                        }
                    }

                    for (int k = 0; k < comps; k++)
                    {
                        Span<ushort> target = rowBlock.Slice(((k * blocksX) + bx) * 64, 64);
                        if (constant)
                        {
                            target.Fill(BitConverter.HalfToUInt16Bits((Half)data[k * 64]));
                        }
                        else
                        {
                            ToHalf(data.Slice(k * 64, 64), target);
                        }
                    }
                }

                for (int k = 0; k < comps; k++)
                {
                    int c = set[k];
                    ReadOnlySpan<int> lineStarts = CollectionsMarshal.AsSpan(rows[c]);
                    ref ushort blockRef = ref rowBlock[k * blocksX * 64];
                    for (int y = 8 * by; y < (8 * by) + maxY; y++)
                    {
                        ref ushort line = ref Unsafe.As<byte, ushort>(ref destination[lineStarts[y]]);
                        int inBlock = (y & 7) * 8;
                        for (int bx = 0; bx < blocksX; bx++)
                        {
                            int count = bx == blocksX - 1 ? leftoverX : 8;
                            ref ushort src = ref Unsafe.Add(ref blockRef, (bx * 64) + inBlock);
                            ref ushort dst = ref Unsafe.Add(ref line, bx * 8);
                            if (count == 8 && toLinear is not null && Avx2.IsSupported)
                            {
                                ToLinear8(toLinear, ref src, ref dst);
                            }
                            else if (count == 8 && toLinear is null)
                            {
                                Unsafe.WriteUnaligned(ref Unsafe.As<ushort, byte>(ref dst), Unsafe.ReadUnaligned<Vector128<ushort>>(ref Unsafe.As<ushort, byte>(ref src)));
                            }
                            else if (toLinear is null)
                            {
                                for (int x = 0; x < count; x++)
                                {
                                    Unsafe.Add(ref dst, x) = Unsafe.Add(ref src, x);
                                }
                            }
                            else
                            {
                                for (int x = 0; x < count; x++)
                                {
                                    Unsafe.Add(ref dst, x) = toLinear[Unsafe.Add(ref src, x)];
                                }
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            ArrayPool<ushort>.Shared.Return(rowBlockArray);
        }

        dcPos += comps * blocksX * blocksY;

        // Float channels were decoded as half; widen them in place, from the right.
        foreach (int c in set)
        {
            if (channels[c].Type != ExrPixelType.Float)
            {
                continue;
            }

            foreach (int row in rows[c])
            {
                Span<byte> line = destination.Slice(row, width * 4);
                for (int x = width - 1; x >= 0; x--)
                {
                    float f = (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(line[(x * 2)..]));
                    BinaryPrimitives.WriteSingleLittleEndian(line[(x * 4)..], f);
                }
            }
        }

        return true;
    }

    /// <summary>
    /// The 8 × 8 inverse DCT, in place, with the constants and the order of operations of OpenEXR's AVX form, which it runs on
    /// any x86-64 processor with AVX: rows as even and odd halves through 4 × 4 matrices, then columns.
    /// </summary>
    private static void InverseDct(Span<float> data)
    {
        const float A = 3.535536e-01f, B = 4.903927e-01f, C = 4.619398e-01f, D = 4.157349e-01f;
        const float E = 2.777855e-01f, F = 1.913422e-01f, G = 9.754573e-02f;
        if (Avx2.IsSupported)
        {
            InverseDctAvx(data);
            return;
        }

        for (int r = 0; r < 64; r += 8)
        {
            float x0 = data[r], x1 = data[r + 1], x2 = data[r + 2], x3 = data[r + 3];
            float x4 = data[r + 4], x5 = data[r + 5], x6 = data[r + 6], x7 = data[r + 7];
            float e0 = ((x0 * A) + (x2 * C)) + ((x4 * A) + (x6 * F));
            float e1 = ((x0 * A) + (x2 * F)) + ((x4 * -A) + (x6 * -C));
            float e2 = ((x0 * A) + (x2 * -F)) + ((x4 * -A) + (x6 * C));
            float e3 = ((x0 * A) + (x2 * -C)) + ((x4 * A) + (x6 * -F));
            float o0 = ((x1 * B) + (x3 * D)) + ((x5 * E) + (x7 * G));
            float o1 = ((x1 * D) + (x3 * -G)) + ((x5 * -B) + (x7 * -E));
            float o2 = ((x1 * E) + (x3 * -B)) + ((x5 * G) + (x7 * D));
            float o3 = ((x1 * G) + (x3 * -E)) + ((x5 * D) + (x7 * -B));
            data[r] = e0 + o0;
            data[r + 1] = e1 + o1;
            data[r + 2] = e2 + o2;
            data[r + 3] = e3 + o3;
            data[r + 4] = e3 - o3;
            data[r + 5] = e2 - o2;
            data[r + 6] = e1 - o1;
            data[r + 7] = e0 - o0;
        }

        for (int c = 0; c < 8; c++)
        {
            float r0 = data[c], r1 = data[8 + c], r2 = data[16 + c], r3 = data[24 + c];
            float r4 = data[32 + c], r5 = data[40 + c], r6 = data[48 + c], r7 = data[56 + c];
            float o0 = ((B * r1) + (D * r3)) + ((E * r5) + (G * r7));
            float o1 = ((D * r1) - ((G * r3) + (B * r5))) - (E * r7);
            float o2 = (((E * r1) - (B * r3)) + (G * r5)) + (D * r7);
            float o3 = ((G * r1) + (D * r5)) - ((E * r3) + (B * r7));
            float sum = (A * r0) + (A * r4), difference = (A * r0) - (A * r4);
            float even1 = (C * r2) + (F * r6), even2 = (F * r2) - (C * r6);
            float e0 = sum + even1, e3 = sum - even1, e1 = difference + even2, e2 = difference - even2;
            data[c] = e0 + o0;
            data[8 + c] = e1 + o1;
            data[16 + c] = e2 + o2;
            data[24 + c] = e3 + o3;
            data[32 + c] = e3 - o3;
            data[40 + c] = e2 - o2;
            data[48 + c] = e1 - o1;
            data[56 + c] = e0 - o0;
        }
    }

    /// <summary>Looks up eight halves in the nonlinear-to-linear table at once, gathering 32 bits and keeping the low 16.</summary>
    private static unsafe void ToLinear8(ushort[] table, ref ushort source, ref ushort destination)
    {
        Vector256<int> indices = Avx2.ConvertToVector256Int32(Unsafe.ReadUnaligned<Vector128<ushort>>(ref Unsafe.As<ushort, byte>(ref source)));
        fixed (ushort* t = table)
        {
            Vector256<int> looked = Avx2.GatherVector256((int*)t, indices, 2) & Vector256.Create(0xFFFF);
            Vector128<ushort> packed = Sse41.PackUnsignedSaturate(looked.GetLower(), looked.GetUpper());
            Unsafe.WriteUnaligned(ref Unsafe.As<ushort, byte>(ref destination), packed);
        }
    }

    /// <summary>Widens 64 halves to floats, as OpenEXR's F16C conversion does (exactly).</summary>
    private static void ToSingle(ReadOnlySpan<ushort> source, Span<float> destination)
    {
        int done = Pixels.HalfKernels.ToSingle(source, MemoryMarshal.Cast<float, uint>(destination), 256);
        for (int i = done; i < source.Length; i++)
        {
            destination[i] = (float)BitConverter.UInt16BitsToHalf(source[i]);
        }
    }

    /// <summary>Rounds 64 floats to halves, to nearest even.</summary>
    private static void ToHalf(ReadOnlySpan<float> source, Span<ushort> destination)
    {
        int done = Pixels.HalfKernels.ToHalf(MemoryMarshal.Cast<float, uint>(source), destination, 256);
        for (int i = done; i < source.Length; i++)
        {
            destination[i] = BitConverter.HalfToUInt16Bits((Half)source[i]);
        }
    }

    /// <summary>
    /// <see cref="InverseDct"/> on eight lanes at once, operation for operation, so the results are the same bits: the rows are
    /// transposed into vectors of one coefficient across eight rows, transformed, transposed back, then the columns transformed.
    /// </summary>
    private static void InverseDctAvx(Span<float> data)
    {
        const float A = 3.535536e-01f, B = 4.903927e-01f, C = 4.619398e-01f, D = 4.157349e-01f;
        const float E = 2.777855e-01f, F = 1.913422e-01f, G = 9.754573e-02f;
        ref float d = ref MemoryMarshal.GetReference(data);
        Vector256<int> x0 = Vector256.LoadUnsafe(ref d).AsInt32(), x1 = Vector256.LoadUnsafe(ref d, 8).AsInt32();
        Vector256<int> x2 = Vector256.LoadUnsafe(ref d, 16).AsInt32(), x3 = Vector256.LoadUnsafe(ref d, 24).AsInt32();
        Vector256<int> x4 = Vector256.LoadUnsafe(ref d, 32).AsInt32(), x5 = Vector256.LoadUnsafe(ref d, 40).AsInt32();
        Vector256<int> x6 = Vector256.LoadUnsafe(ref d, 48).AsInt32(), x7 = Vector256.LoadUnsafe(ref d, 56).AsInt32();
        Jpeg.JpegIdct.Transpose(ref x0, ref x1, ref x2, ref x3, ref x4, ref x5, ref x6, ref x7);

        // Rows: x0..x7 now hold coefficient k of every row.
        Vector256<float> a = Vector256.Create(A), b = Vector256.Create(B), c = Vector256.Create(C), dd = Vector256.Create(D);
        Vector256<float> e = Vector256.Create(E), f = Vector256.Create(F), g = Vector256.Create(G);
        Vector256<float> r0 = x0.AsSingle(), r1 = x1.AsSingle(), r2 = x2.AsSingle(), r3 = x3.AsSingle();
        Vector256<float> r4 = x4.AsSingle(), r5 = x5.AsSingle(), r6 = x6.AsSingle(), r7 = x7.AsSingle();
        Vector256<float> e0 = ((r0 * a) + (r2 * c)) + ((r4 * a) + (r6 * f));
        Vector256<float> e1 = ((r0 * a) + (r2 * f)) + ((r4 * -a) + (r6 * -c));
        Vector256<float> e2 = ((r0 * a) + (r2 * -f)) + ((r4 * -a) + (r6 * c));
        Vector256<float> e3 = ((r0 * a) + (r2 * -c)) + ((r4 * a) + (r6 * -f));
        Vector256<float> o0 = ((r1 * b) + (r3 * dd)) + ((r5 * e) + (r7 * g));
        Vector256<float> o1 = ((r1 * dd) + (r3 * -g)) + ((r5 * -b) + (r7 * -e));
        Vector256<float> o2 = ((r1 * e) + (r3 * -b)) + ((r5 * g) + (r7 * dd));
        Vector256<float> o3 = ((r1 * g) + (r3 * -e)) + ((r5 * dd) + (r7 * -b));
        x0 = (e0 + o0).AsInt32();
        x1 = (e1 + o1).AsInt32();
        x2 = (e2 + o2).AsInt32();
        x3 = (e3 + o3).AsInt32();
        x4 = (e3 - o3).AsInt32();
        x5 = (e2 - o2).AsInt32();
        x6 = (e1 - o1).AsInt32();
        x7 = (e0 - o0).AsInt32();
        Jpeg.JpegIdct.Transpose(ref x0, ref x1, ref x2, ref x3, ref x4, ref x5, ref x6, ref x7);

        // Columns: x0..x7 hold the rows again.
        r0 = x0.AsSingle();
        r1 = x1.AsSingle();
        r2 = x2.AsSingle();
        r3 = x3.AsSingle();
        r4 = x4.AsSingle();
        r5 = x5.AsSingle();
        r6 = x6.AsSingle();
        r7 = x7.AsSingle();
        o0 = ((b * r1) + (dd * r3)) + ((e * r5) + (g * r7));
        o1 = ((dd * r1) - ((g * r3) + (b * r5))) - (e * r7);
        o2 = (((e * r1) - (b * r3)) + (g * r5)) + (dd * r7);
        o3 = ((g * r1) + (dd * r5)) - ((e * r3) + (b * r7));
        Vector256<float> sum = (a * r0) + (a * r4), difference = (a * r0) - (a * r4);
        Vector256<float> even1 = (c * r2) + (f * r6), even2 = (f * r2) - (c * r6);
        e0 = sum + even1;
        e3 = sum - even1;
        e1 = difference + even2;
        e2 = difference - even2;
        (e0 + o0).StoreUnsafe(ref d);
        (e1 + o1).StoreUnsafe(ref d, 8);
        (e2 + o2).StoreUnsafe(ref d, 16);
        (e3 + o3).StoreUnsafe(ref d, 24);
        (e3 - o3).StoreUnsafe(ref d, 32);
        (e2 - o2).StoreUnsafe(ref d, 40);
        (e1 - o1).StoreUnsafe(ref d, 48);
        (e0 - o0).StoreUnsafe(ref d, 56);
    }

    /// <summary>Interleaves the low and high bytes of 16-bit samples, sixteen at a time where the processor can.</summary>
    private static void Interleave(ReadOnlySpan<byte> low, ReadOnlySpan<byte> high, Span<byte> destination)
    {
        int n = low.Length, x = 0;
        ref byte lo = ref MemoryMarshal.GetReference(low);
        ref byte hi = ref MemoryMarshal.GetReference(high);
        ref byte dst = ref MemoryMarshal.GetReference(destination);
        if (Sse2.IsSupported)
        {
            for (; x <= n - 16; x += 16)
            {
                Vector128<byte> l = Vector128.LoadUnsafe(ref lo, (nuint)x), h = Vector128.LoadUnsafe(ref hi, (nuint)x);
                Sse2.UnpackLow(l, h).StoreUnsafe(ref dst, (nuint)(2 * x));
                Sse2.UnpackHigh(l, h).StoreUnsafe(ref dst, (nuint)((2 * x) + 16));
            }
        }

        for (; x < n; x++)
        {
            Unsafe.Add(ref dst, 2 * x) = Unsafe.Add(ref lo, x);
            Unsafe.Add(ref dst, (2 * x) + 1) = Unsafe.Add(ref hi, x);
        }
    }

    /// <summary>OpenEXR's byte RLE without the predictor: a count n ≥ 0 repeats the next byte n + 1 times, n &lt; 0 copies −n bytes.</summary>
    private static bool UnRle(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int pos = 0, outPos = 0;
        while (pos < source.Length)
        {
            int count = (sbyte)source[pos++];
            if (count < 0)
            {
                count = -count;
                if (count > source.Length - pos || count > destination.Length - outPos)
                {
                    return false;
                }

                source.Slice(pos, count).CopyTo(destination[outPos..]);
                pos += count;
                outPos += count;
            }
            else
            {
                if (pos >= source.Length || count + 1 > destination.Length - outPos)
                {
                    return false;
                }

                destination.Slice(outPos, count + 1).Fill(source[pos++]);
                outPos += count + 1;
            }
        }

        return outPos == destination.Length;
    }

    private static Rule[] ReadRules(ReadOnlySpan<byte> data)
    {
        List<Rule> rules = [];
        int pos = 0;
        while (pos < data.Length)
        {
            int end = data[pos..].IndexOf((byte)0);
            if (end < 0 || end > 128 || pos + end + 3 > data.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(data));
            }

            string suffix = Encoding.ASCII.GetString(data.Slice(pos, end));
            byte value = data[pos + end + 1];
            byte type = data[pos + end + 2];
            pos += end + 3;
            int csc = (value >> 4) - 1;
            int scheme = (value >> 2) & 3;
            if (csc is < -1 or >= 3 || scheme >= 3 || type > 2)
            {
                throw new ArgumentOutOfRangeException(nameof(data));
            }

            rules.Add(new Rule(suffix, scheme, (ExrPixelType)type, csc, (value & 1) != 0));
        }

        return [.. rules];
    }

    private static Rule[] BuildLegacyRules()
    {
        List<Rule> rules = [];
        foreach ((string suffix, int scheme, int csc) in new[]
        {
            ("r", LossyDct, 0), ("red", LossyDct, 0), ("g", LossyDct, 1), ("grn", LossyDct, 1), ("green", LossyDct, 1),
            ("b", LossyDct, 2), ("blu", LossyDct, 2), ("blue", LossyDct, 2), ("y", LossyDct, -1), ("by", LossyDct, -1), ("ry", LossyDct, -1),
        })
        {
            rules.Add(new Rule(suffix, scheme, ExrPixelType.Half, csc, true));
            rules.Add(new Rule(suffix, scheme, ExrPixelType.Float, csc, true));
        }

        rules.Add(new Rule("a", Rle, ExrPixelType.UInt, -1, true));
        rules.Add(new Rule("a", Rle, ExrPixelType.Half, -1, true));
        rules.Add(new Rule("a", Rle, ExrPixelType.Float, -1, true));
        return [.. rules];
    }

    /// <summary>dwaToLinear: values up to 1 to the power 2.2, above 1 e^(2.2 (x − 1)), sign kept; infinities and NaN to 0.</summary>
    private static byte[] BuildUnzig()
    {
        byte[] unzig = new byte[64];
        for (int i = 0; i < 64; i++)
        {
            unzig[Zig[i]] = (byte)i;
        }

        return unzig;
    }

    private static ushort[] BuildToLinear()
    {
        // Two entries more than the halves, so a gather of 32 bits at the last one stays inside the table.
        ushort[] table = new ushort[65536 + 2];
        for (int i = 1; i < 65536; i++)
        {
            if ((i & 0x7C00) == 0x7C00)
            {
                continue;
            }

            float f = (float)BitConverter.UInt16BitsToHalf((ushort)i);
            float sign = f < 0 ? -1f : 1f;
            f = MathF.Abs(f);
            (float px, float py) = f <= 1.0f ? (f, 2.2f) : (9.02501329156f, f - 1.0f);
            float z = sign * (float)Math.Pow(px, py);
            table[i] = BitConverter.HalfToUInt16Bits((Half)z);
        }

        return table;
    }

    private sealed record Rule(string Suffix, int Scheme, ExrPixelType Type, int CscIndex, bool CaseInsensitive);
}
