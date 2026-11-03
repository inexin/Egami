using System.Buffers.Binary;

namespace Egami.Formats.Extended.Textures.Blocks;

/// <summary>How an ASTC texture is decoded: its profile, which the Vulkan format names.</summary>
internal enum AstcProfile : byte
{
    /// <summary>LDR UNORM: 8-bit endpoints widened by replication; texels keep the top 8 bits of the 16-bit result.</summary>
    Ldr,

    /// <summary>LDR sRGB: colour endpoints widened as <c>e × 256 + 128</c>, alpha by replication; the top 8 bits are kept.</summary>
    Srgb,

    /// <summary>HDR (the SFLOAT formats): texels decode to half floats, HDR endpoints through the logarithmic encoding.</summary>
    Hdr,
}

/// <summary>
/// Decodes 2D ASTC blocks of one footprint, written from the Khronos Data Format Specification's ASTC chapter: the block
/// mode, partitions chosen by the hash function, every colour endpoint mode, integer sequence encoded endpoints and weights,
/// the bilinear weight infill, dual planes and void-extent blocks. LDR texels keep the top 8 bits of the interpolated 16-bit
/// value (the decode mode Vulkan calls <c>VK_FORMAT_R8G8B8A8_UNORM</c>, as astcenc's unorm8 decode does); HDR texels are half
/// floats. Blocks the specification calls illegal decode to the error colour: magenta in LDR, NaN in HDR. Instances are
/// shared per footprint and safe to use from several threads.
/// </summary>
internal sealed class Astc
{
    /// <summary>The 14 2D footprints, as width and height.</summary>
    public static ReadOnlySpan<byte> Footprints => [4, 4, 5, 4, 5, 5, 6, 5, 6, 6, 8, 5, 8, 6, 8, 8, 10, 5, 10, 6, 10, 8, 10, 10, 12, 10, 12, 12];

    private static readonly Astc?[] Cache = new Astc?[14];

    private readonly BlockMode[] _modes = new BlockMode[2048];
    private readonly byte[]?[] _partitions = new byte[3 * 1024][];
    private readonly Infill?[] _infills = new Infill[13 * 13];

    private Astc(int width, int height)
    {
        Width = width;
        Height = height;
        Texels = width * height;
        for (int m = 0; m < 2048; m++)
        {
            _modes[m] = DecodeMode(m, width, height);
        }
    }

    /// <summary>Gets the footprint's width in texels.</summary>
    public int Width { get; }

    /// <summary>Gets the footprint's height in texels.</summary>
    public int Height { get; }

    /// <summary>Gets the texels of one block.</summary>
    public int Texels { get; }

    /// <summary>Returns the decoder for a 2D footprint, or <see langword="null"/> if it is not one of the 14.</summary>
    public static Astc? For(int width, int height)
    {
        for (int i = 0; i < 14; i++)
        {
            if (Footprints[i * 2] == width && Footprints[(i * 2) + 1] == height)
            {
                return Cache[i] ??= new Astc(width, height);
            }
        }

        return null;
    }

    /// <summary>
    /// Decodes a block (16 bytes) of an LDR or sRGB texture into <see cref="Texels"/> RGBA8 pixels in row order.
    /// </summary>
    public void DecodeLdr(ReadOnlySpan<byte> block, Span<byte> rgba, bool srgb)
    {
        Span<ushort> values = stackalloc ushort[Texels * 4];
        Decode(block, srgb ? AstcProfile.Srgb : AstcProfile.Ldr, values);
        for (int i = 0; i < values.Length; i++)
        {
            rgba[i] = (byte)(values[i] >> 8);
        }
    }

    /// <summary>Decodes a block (16 bytes) of an HDR texture into <see cref="Texels"/> RGBA half-float pixels in row order.</summary>
    public void DecodeHdr(ReadOnlySpan<byte> block, Span<ushort> rgba) => Decode(block, AstcProfile.Hdr, rgba);

    /// <summary>Returns the block mode table entry for mode <paramref name="mode"/>, for the encoder.</summary>
    internal BlockMode Mode(int mode) => _modes[mode];

    /// <summary>
    /// Decodes a block into 16-bit values per channel: for LDR profiles the interpolated unorm16 value (its top 8 bits are the
    /// texel), for HDR the half float.
    /// </summary>
    internal void Decode(ReadOnlySpan<byte> block, AstcProfile profile, Span<ushort> output)
    {
        int texels = Texels;
        ulong lo = BinaryPrimitives.ReadUInt64LittleEndian(block);
        int modeBits = (int)(lo & 0x7FF);
        if ((modeBits & 0x1FF) == 0x1FC)
        {
            VoidExtent(block, lo, profile, output);
            return;
        }

        BlockMode mode = _modes[modeBits];
        int partitions = (int)((lo >> 11) & 3) + 1;
        if (!mode.Valid || (partitions == 4 && mode.DualPlane))
        {
            Error(profile, output);
            return;
        }

        int weightCount = mode.GridWidth * mode.GridHeight * (mode.DualPlane ? 2 : 1);
        int weightBits = AstcIse.BitCount(weightCount, mode.WeightRange);
        int belowWeights = 128 - weightBits;
        Span<byte> cems = stackalloc byte[4];
        int configBits;
        if (partitions == 1)
        {
            cems[0] = (byte)((lo >> 13) & 15);
            configBits = 17;
        }
        else
        {
            configBits = 29;
            int encoded = (int)((lo >> 23) & 63);
            if ((encoded & 3) == 0)
            {
                for (int p = 0; p < partitions; p++)
                {
                    cems[p] = (byte)((encoded >> 2) & 15);
                }
            }
            else
            {
                int extra = (3 * partitions) - 4;
                belowWeights -= extra;
                encoded |= AstcIse.Read(block, belowWeights, extra) << 6;
                int baseClass = (encoded & 3) - 1;
                int bit = 2;
                for (int p = 0; p < partitions; p++, bit++)
                {
                    cems[p] = (byte)((((encoded >> bit) & 1) + baseClass) << 2);
                }

                for (int p = 0; p < partitions; p++, bit += 2)
                {
                    cems[p] |= (byte)((encoded >> bit) & 3);
                }
            }
        }

        int plane2 = -1;
        if (mode.DualPlane)
        {
            belowWeights -= 2;
            plane2 = AstcIse.Read(block, belowWeights, 2);
        }

        int colorCount = 0;
        for (int p = 0; p < partitions; p++)
        {
            colorCount += ((cems[p] >> 2) + 1) * 2;
        }

        int colorBits = belowWeights - configBits;
        int colorRange = colorCount > 18 ? -1 : ColorRange(colorCount, colorBits);
        if (colorRange < AstcIse.Range6)
        {
            Error(profile, output);
            return;
        }

        Span<int> colorValues = stackalloc int[colorCount];
        AstcIse.Decode(colorRange, block, configBits, colorValues);
        byte[] unquantize = AstcIse.ColorUnquantized[colorRange];
        for (int i = 0; i < colorCount; i++)
        {
            colorValues[i] = unquantize[colorValues[i]];
        }

        // The weights run backwards from the top of the block: decode them from the block with its bits reversed.
        Span<byte> reversed = stackalloc byte[16];
        for (int i = 0; i < 16; i++)
        {
            reversed[i] = ReverseBits(block[15 - i]);
        }

        Span<int> weights = stackalloc int[weightCount];
        AstcIse.Decode(mode.WeightRange, reversed, 0, weights);
        byte[] weightUnquantize = AstcIse.WeightUnquantized[mode.WeightRange];
        for (int i = 0; i < weightCount; i++)
        {
            weights[i] = weightUnquantize[weights[i]];
        }

        Span<int> plane1 = stackalloc int[texels];
        Span<int> plane2Weights = stackalloc int[mode.DualPlane ? texels : 0];
        Infill infill = InfillFor(mode.GridWidth, mode.GridHeight);
        infill.Apply(weights, mode.DualPlane ? 2 : 1, 0, plane1);
        if (mode.DualPlane)
        {
            infill.Apply(weights, 2, 1, plane2Weights);
        }

        // The endpoints of each partition, as 16-bit values, and whether each channel is HDR (logarithmic).
        Span<int> endpoints = stackalloc int[partitions * 8];
        Span<bool> hdrRgb = stackalloc bool[partitions];
        Span<bool> hdrAlpha = stackalloc bool[partitions];
        int at = 0;
        for (int p = 0; p < partitions; p++)
        {
            int count = ((cems[p] >> 2) + 1) * 2;
            Endpoints(cems[p], colorValues.Slice(at, count), profile, endpoints.Slice(p * 8, 8), out hdrRgb[p], out hdrAlpha[p]);
            at += count;
        }

        byte[]? table = partitions > 1 ? PartitionTable(partitions, (int)((lo >> 13) & 1023)) : null;
        for (int t = 0; t < texels; t++)
        {
            int p = table is null ? 0 : table[t];
            ReadOnlySpan<int> e = endpoints.Slice(p * 8, 8);
            for (int c = 0; c < 4; c++)
            {
                int w = c == plane2 ? plane2Weights[t] : plane1[t];
                int value = ((e[c] * (64 - w)) + (e[4 + c] * w) + 32) >> 6;
                if (profile == AstcProfile.Hdr)
                {
                    value = (c == 3 ? hdrAlpha[p] : hdrRgb[p]) ? LnsToHalf(value) : Unorm16ToHalf(value);
                }

                output[(t * 4) + c] = (ushort)value;
            }
        }
    }

    /// <summary>Returns the highest colour range whose <paramref name="count"/> values fit in <paramref name="bits"/>, or -1.</summary>
    internal static int ColorRange(int count, int bits)
    {
        for (int r = 20; r >= 0; r--)
        {
            if (AstcIse.BitCount(count, r) <= bits)
            {
                return r;
            }
        }

        return -1;
    }

    /// <summary>Returns the partition of every texel for partition count <paramref name="count"/> and seed <paramref name="seed"/>.</summary>
    internal byte[] PartitionTable(int count, int seed)
    {
        int key = ((count - 2) * 1024) + seed;
        byte[]? table = _partitions[key];
        if (table is null)
        {
            table = new byte[Texels];
            bool small = Texels < 31;
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    table[(y * Width) + x] = (byte)SelectPartition(seed, x, y, 0, count, small);
                }
            }

            _partitions[key] = table;
        }

        return table;
    }

    /// <summary>Returns the weight infill of a <paramref name="gridWidth"/> × <paramref name="gridHeight"/> grid.</summary>
    internal Infill InfillFor(int gridWidth, int gridHeight) => _infills[(gridWidth * 13) + gridHeight] ??= new Infill(Width, Height, gridWidth, gridHeight);

    /// <summary>The partition hash function: which of <paramref name="count"/> partitions texel (x, y, z) is in.</summary>
    internal static int SelectPartition(int seed, int x, int y, int z, int count, bool small)
    {
        if (small)
        {
            x <<= 1;
            y <<= 1;
            z <<= 1;
        }

        seed += (count - 1) * 1024;
        uint rnum = Hash52((uint)seed);
        int s1 = (int)(rnum & 0xF), s2 = (int)((rnum >> 4) & 0xF), s3 = (int)((rnum >> 8) & 0xF), s4 = (int)((rnum >> 12) & 0xF);
        int s5 = (int)((rnum >> 16) & 0xF), s6 = (int)((rnum >> 20) & 0xF), s7 = (int)((rnum >> 24) & 0xF), s8 = (int)((rnum >> 28) & 0xF);
        int s9 = (int)((rnum >> 18) & 0xF), s10 = (int)((rnum >> 22) & 0xF), s11 = (int)((rnum >> 26) & 0xF);
        int s12 = (int)(((rnum >> 30) | (rnum << 2)) & 0xF);
        s1 *= s1;
        s2 *= s2;
        s3 *= s3;
        s4 *= s4;
        s5 *= s5;
        s6 *= s6;
        s7 *= s7;
        s8 *= s8;
        s9 *= s9;
        s10 *= s10;
        s11 *= s11;
        s12 *= s12;
        int sh1, sh2;
        if ((seed & 1) != 0)
        {
            sh1 = (seed & 2) != 0 ? 4 : 5;
            sh2 = count == 3 ? 6 : 5;
        }
        else
        {
            sh1 = count == 3 ? 6 : 5;
            sh2 = (seed & 2) != 0 ? 4 : 5;
        }

        int sh3 = (seed & 0x10) != 0 ? sh1 : sh2;
        s1 >>= sh1;
        s2 >>= sh2;
        s3 >>= sh1;
        s4 >>= sh2;
        s5 >>= sh1;
        s6 >>= sh2;
        s7 >>= sh1;
        s8 >>= sh2;
        s9 >>= sh3;
        s10 >>= sh3;
        s11 >>= sh3;
        s12 >>= sh3;
        int a = (int)(((s1 * x) + (s2 * y) + (s11 * z) + (rnum >> 14)) & 0x3F);
        int b = (int)(((s3 * x) + (s4 * y) + (s12 * z) + (rnum >> 10)) & 0x3F);
        int c = (int)(((s5 * x) + (s6 * y) + (s9 * z) + (rnum >> 6)) & 0x3F);
        int d = (int)(((s7 * x) + (s8 * y) + (s10 * z) + (rnum >> 2)) & 0x3F);
        if (count <= 3)
        {
            d = 0;
        }

        if (count <= 2)
        {
            c = 0;
        }

        if (a >= b && a >= c && a >= d)
        {
            return 0;
        }

        if (b >= c && b >= d)
        {
            return 1;
        }

        return c >= d ? 2 : 3;
    }

    /// <summary>Converts the specification's logarithmic 16-bit HDR value to a half float.</summary>
    internal static int LnsToHalf(int p)
    {
        int mc = p & 0x7FF;
        int ec = p >> 11;
        int mt = mc < 512 ? mc * 3 : mc < 1536 ? (mc * 4) - 512 : (mc * 5) - 2048;
        return Math.Min((ec << 10) | (mt >> 3), 0x7BFF);
    }

    /// <summary>Converts a 16-bit UNORM value to a half float, truncating the mantissa as the specification does.</summary>
    internal static int Unorm16ToHalf(int p)
    {
        if (p == 0xFFFF)
        {
            return 0x3C00;
        }

        if (p < 4)
        {
            return p << 8;
        }

        int lz = System.Numerics.BitOperations.LeadingZeroCount((uint)p) - 16;
        int m = (p << (lz + 1)) & 0xFFFF;
        return (m >> 6) | ((14 - lz) << 10);
    }

    private static uint Hash52(uint p)
    {
        p ^= p >> 15;
        p *= 0xEEDE0891;
        p ^= p >> 5;
        p += p << 16;
        p ^= p >> 7;
        p ^= p >> 3;
        p ^= p << 6;
        p ^= p >> 17;
        return p;
    }

    private static byte ReverseBits(byte b)
    {
        b = (byte)(((b & 0xF0) >> 4) | ((b & 0x0F) << 4));
        b = (byte)(((b & 0xCC) >> 2) | ((b & 0x33) << 2));
        return (byte)(((b & 0xAA) >> 1) | ((b & 0x55) << 1));
    }

    private static void Error(AstcProfile profile, Span<ushort> output)
    {
        for (int i = 0; i < output.Length; i += 4)
        {
            if (profile == AstcProfile.Hdr)
            {
                output[i] = output[i + 1] = output[i + 2] = output[i + 3] = 0x7E00;
            }
            else
            {
                output[i] = 0xFFFF;
                output[i + 1] = 0;
                output[i + 2] = 0xFFFF;
                output[i + 3] = 0xFFFF;
            }
        }
    }

    private void VoidExtent(ReadOnlySpan<byte> block, ulong lo, AstcProfile profile, Span<ushort> output)
    {
        bool fp16 = ((lo >> 9) & 1) != 0;
        int lowS = (int)((lo >> 12) & 0x1FFF), highS = (int)((lo >> 25) & 0x1FFF);
        int lowT = (int)((lo >> 38) & 0x1FFF), highT = (int)((lo >> 51) & 0x1FFF);
        bool allOnes = lowS == 0x1FFF && highS == 0x1FFF && lowT == 0x1FFF && highT == 0x1FFF;
        if (((lo >> 10) & 3) != 3 || ((lowS >= highS || lowT >= highT) && !allOnes) || (fp16 && profile != AstcProfile.Hdr))
        {
            Error(profile, output);
            return;
        }

        Span<ushort> color = stackalloc ushort[4];
        for (int c = 0; c < 4; c++)
        {
            int v = BinaryPrimitives.ReadUInt16LittleEndian(block[(8 + (c * 2))..]);
            color[c] = (ushort)(fp16 ? v : profile == AstcProfile.Hdr ? Unorm16ToHalf(v) : v);
        }

        for (int i = 0; i < output.Length; i += 4)
        {
            color.CopyTo(output[i..]);
        }
    }

    /// <summary>
    /// Decodes the endpoints of colour endpoint mode <paramref name="cem"/> from its unquantised values into eight 16-bit
    /// values: endpoint 0's RGBA, then endpoint 1's.
    /// </summary>
    private static void Endpoints(int cem, ReadOnlySpan<int> v, AstcProfile profile, Span<int> e, out bool hdrRgb, out bool hdrAlpha)
    {
        hdrRgb = false;
        hdrAlpha = false;
        Span<int> e0 = e[..4];
        Span<int> e1 = e[4..];
        switch (cem)
        {
            case 0:
                Set(e0, v[0], v[0], v[0], 255);
                Set(e1, v[1], v[1], v[1], 255);
                break;
            case 1:
                {
                    int l0 = (v[0] >> 2) | (v[1] & 0xC0);
                    int l1 = Math.Min(l0 + (v[1] & 0x3F), 255);
                    Set(e0, l0, l0, l0, 255);
                    Set(e1, l1, l1, l1, 255);
                    break;
                }

            case 2:
                {
                    int y0, y1;
                    if (v[1] >= v[0])
                    {
                        y0 = v[0] << 4;
                        y1 = v[1] << 4;
                    }
                    else
                    {
                        y0 = (v[1] << 4) + 8;
                        y1 = (v[0] << 4) - 8;
                    }

                    Set(e0, y0 << 4, y0 << 4, y0 << 4, 0x7800);
                    Set(e1, y1 << 4, y1 << 4, y1 << 4, 0x7800);
                    hdrRgb = hdrAlpha = true;
                    break;
                }

            case 3:
                {
                    int y0, d;
                    if ((v[0] & 0x80) != 0)
                    {
                        y0 = ((v[1] & 0xE0) << 4) | ((v[0] & 0x7F) << 2);
                        d = (v[1] & 0x1F) << 2;
                    }
                    else
                    {
                        y0 = ((v[1] & 0xF0) << 4) | ((v[0] & 0x7F) << 1);
                        d = (v[1] & 0x0F) << 1;
                    }

                    int y1 = Math.Min(y0 + d, 0xFFF);
                    Set(e0, y0 << 4, y0 << 4, y0 << 4, 0x7800);
                    Set(e1, y1 << 4, y1 << 4, y1 << 4, 0x7800);
                    hdrRgb = hdrAlpha = true;
                    break;
                }

            case 4:
                Set(e0, v[0], v[0], v[0], v[2]);
                Set(e1, v[1], v[1], v[1], v[3]);
                break;
            case 5:
                {
                    (int d0, int b0) = BitTransferSigned(v[1], v[0]);
                    (int d1, int b1) = BitTransferSigned(v[3], v[2]);
                    Set(e0, b0, b0, b0, b1);
                    Set(e1, Clamp8(b0 + d0), Clamp8(b0 + d0), Clamp8(b0 + d0), Clamp8(b1 + d1));
                    break;
                }

            case 6:
                Set(e0, (v[0] * v[3]) >> 8, (v[1] * v[3]) >> 8, (v[2] * v[3]) >> 8, 255);
                Set(e1, v[0], v[1], v[2], 255);
                break;
            case 7:
                HdrRgbScale(v, e0, e1);
                hdrRgb = hdrAlpha = true;
                break;
            case 8:
                Direct(v[0], v[2], v[4], 255, v[1], v[3], v[5], 255, e0, e1);
                break;
            case 9:
                Delta(v, false, e0, e1);
                break;
            case 10:
                Set(e0, (v[0] * v[3]) >> 8, (v[1] * v[3]) >> 8, (v[2] * v[3]) >> 8, v[4]);
                Set(e1, v[0], v[1], v[2], v[5]);
                break;
            case 11:
                HdrRgb(v, e0, e1);
                e0[3] = e1[3] = 0x7800;
                hdrRgb = hdrAlpha = true;
                break;
            case 12:
                Direct(v[0], v[2], v[4], v[6], v[1], v[3], v[5], v[7], e0, e1);
                break;
            case 13:
                Delta(v, true, e0, e1);
                break;
            case 14:
                HdrRgb(v, e0, e1);
                e0[3] = v[6];
                e1[3] = v[7];
                hdrRgb = true;
                break;
            default:
                HdrRgb(v, e0, e1);
                HdrAlpha(v[6], v[7], out e0[3], out e1[3]);
                hdrRgb = hdrAlpha = true;
                break;
        }

        switch (profile)
        {
            case AstcProfile.Ldr:
            case AstcProfile.Srgb:
                if (hdrRgb || hdrAlpha)
                {
                    Set(e0, 0xFF, 0, 0xFF, 0xFF);
                    Set(e1, 0xFF, 0, 0xFF, 0xFF);
                    hdrRgb = hdrAlpha = false;
                }

                for (int c = 0; c < 8; c++)
                {
                    e[c] = profile == AstcProfile.Srgb && (c & 3) != 3 ? (e[c] << 8) | 0x80 : e[c] * 257;
                }

                break;
            default:
                for (int c = 0; c < 8; c++)
                {
                    if (!((c & 3) == 3 ? hdrAlpha : hdrRgb))
                    {
                        e[c] *= 257;
                    }
                }

                break;
        }
    }

    private static void Direct(int r0, int g0, int b0, int a0, int r1, int g1, int b1, int a1, Span<int> e0, Span<int> e1)
    {
        if (r1 + g1 + b1 >= r0 + g0 + b0)
        {
            Set(e0, r0, g0, b0, a0);
            Set(e1, r1, g1, b1, a1);
        }
        else
        {
            BlueContract(r1, g1, b1, a1, e0);
            BlueContract(r0, g0, b0, a0, e1);
        }
    }

    private static void Delta(ReadOnlySpan<int> v, bool alpha, Span<int> e0, Span<int> e1)
    {
        (int dr, int r) = BitTransferSigned(v[1], v[0]);
        (int dg, int g) = BitTransferSigned(v[3], v[2]);
        (int db, int b) = BitTransferSigned(v[5], v[4]);
        int da = 0, a = 255;
        if (alpha)
        {
            (da, a) = BitTransferSigned(v[7], v[6]);
        }

        if (dr + dg + db >= 0)
        {
            Set(e0, r, g, b, a);
            Set(e1, r + dr, g + dg, b + db, a + da);
        }
        else
        {
            BlueContract(r + dr, g + dg, b + db, a + da, e0);
            BlueContract(r, g, b, a, e1);
        }

        for (int c = 0; c < 4; c++)
        {
            e0[c] = Clamp8(e0[c]);
            e1[c] = Clamp8(e1[c]);
        }
    }

    private static void BlueContract(int r, int g, int b, int a, Span<int> e) => Set(e, (r + b) >> 1, (g + b) >> 1, b, a);

    /// <summary>Moves the top bit of <paramref name="a"/> to <paramref name="b"/>; returns a as a signed 6-bit value and b.</summary>
    private static (int A, int B) BitTransferSigned(int a, int b)
    {
        b = (b >> 1) | (a & 0x80);
        a = (a >> 1) & 0x3F;
        if ((a & 0x20) != 0)
        {
            a -= 0x40;
        }

        return (a, b);
    }

    private static void HdrRgbScale(ReadOnlySpan<int> v, Span<int> e0, Span<int> e1)
    {
        int v0 = v[0], v1 = v[1], v2 = v[2], v3 = v[3];
        int modeValue = ((v0 & 0xC0) >> 6) | (((v1 & 0x80) >> 7) << 2) | (((v2 & 0x80) >> 7) << 3);
        int major, mode;
        if ((modeValue & 0xC) != 0xC)
        {
            major = modeValue >> 2;
            mode = modeValue & 3;
        }
        else if (modeValue != 0xF)
        {
            major = modeValue & 3;
            mode = 4;
        }
        else
        {
            major = 0;
            mode = 5;
        }

        int red = v0 & 0x3F, green = v1 & 0x1F, blue = v2 & 0x1F, scale = v3 & 0x1F;
        int bit0 = (v1 >> 6) & 1, bit1 = (v1 >> 5) & 1, bit2 = (v2 >> 6) & 1, bit3 = (v2 >> 5) & 1;
        int bit4 = (v3 >> 7) & 1, bit5 = (v3 >> 6) & 1, bit6 = (v3 >> 5) & 1;
        int one = 1 << mode;
        if ((one & 0x30) != 0)
        {
            green |= bit0 << 6;
        }

        if ((one & 0x3A) != 0)
        {
            green |= bit1 << 5;
        }

        if ((one & 0x30) != 0)
        {
            blue |= bit2 << 6;
        }

        if ((one & 0x3A) != 0)
        {
            blue |= bit3 << 5;
        }

        if ((one & 0x3D) != 0)
        {
            scale |= bit6 << 5;
        }

        if ((one & 0x2D) != 0)
        {
            scale |= bit5 << 6;
        }

        if ((one & 0x04) != 0)
        {
            scale |= bit4 << 7;
        }

        if ((one & 0x3B) != 0)
        {
            red |= bit4 << 6;
        }

        if ((one & 0x04) != 0)
        {
            red |= bit3 << 6;
        }

        if ((one & 0x10) != 0)
        {
            red |= bit5 << 7;
        }

        if ((one & 0x0F) != 0)
        {
            red |= bit2 << 7;
        }

        if ((one & 0x05) != 0)
        {
            red |= bit1 << 8;
        }

        if ((one & 0x0A) != 0)
        {
            red |= bit0 << 8;
        }

        if ((one & 0x05) != 0)
        {
            red |= bit0 << 9;
        }

        if ((one & 0x02) != 0)
        {
            red |= bit6 << 9;
        }

        if ((one & 0x01) != 0)
        {
            red |= bit3 << 10;
        }

        if ((one & 0x02) != 0)
        {
            red |= bit5 << 10;
        }

        int shift = mode switch { 0 or 1 => 1, 2 => 2, 3 => 3, 4 => 4, _ => 5 };
        red <<= shift;
        green <<= shift;
        blue <<= shift;
        scale <<= shift;
        if (mode != 5)
        {
            green = red - green;
            blue = red - blue;
        }

        if (major == 1)
        {
            (red, green) = (green, red);
        }
        else if (major == 2)
        {
            (red, blue) = (blue, red);
        }

        int red0 = Math.Clamp(red - scale, 0, 0xFFF), green0 = Math.Clamp(green - scale, 0, 0xFFF), blue0 = Math.Clamp(blue - scale, 0, 0xFFF);
        red = Math.Clamp(red, 0, 0xFFF);
        green = Math.Clamp(green, 0, 0xFFF);
        blue = Math.Clamp(blue, 0, 0xFFF);
        Set(e0, red0 << 4, green0 << 4, blue0 << 4, 0x7800);
        Set(e1, red << 4, green << 4, blue << 4, 0x7800);
    }

    private static void HdrRgb(ReadOnlySpan<int> v, Span<int> e0, Span<int> e1)
    {
        int v0 = v[0], v1 = v[1], v2 = v[2], v3 = v[3], v4 = v[4], v5 = v[5];
        int modeValue = ((v1 & 0x80) >> 7) | (((v2 & 0x80) >> 7) << 1) | (((v3 & 0x80) >> 7) << 2);
        int major = ((v4 & 0x80) >> 7) | (((v5 & 0x80) >> 7) << 1);
        if (major == 3)
        {
            Set(e0, v0 << 8, v2 << 8, (v4 & 0x7F) << 9, 0);
            Set(e1, v1 << 8, v3 << 8, (v5 & 0x7F) << 9, 0);
            return;
        }

        int a = v0 | ((v1 & 0x40) << 2);
        int b0 = v2 & 0x3F, b1 = v3 & 0x3F, c = v1 & 0x3F, d0 = v4 & 0x7F, d1 = v5 & 0x7F;
        int dbits = modeValue switch { 0 or 2 => 7, 4 or 6 => 5, _ => 6 };
        int bit0 = (v2 >> 6) & 1, bit1 = (v3 >> 6) & 1, bit2 = (v4 >> 6) & 1, bit3 = (v5 >> 6) & 1, bit4 = (v4 >> 5) & 1, bit5 = (v5 >> 5) & 1;
        int one = 1 << modeValue;
        if ((one & 0xA4) != 0)
        {
            a |= bit0 << 9;
        }

        if ((one & 0x8) != 0)
        {
            a |= bit2 << 9;
        }

        if ((one & 0x50) != 0)
        {
            a |= bit4 << 9;
            a |= bit5 << 10;
        }

        if ((one & 0xA0) != 0)
        {
            a |= bit1 << 10;
        }

        if ((one & 0xC0) != 0)
        {
            a |= bit2 << 11;
        }

        if ((one & 0x4) != 0)
        {
            c |= bit1 << 6;
        }

        if ((one & 0xE8) != 0)
        {
            c |= bit3 << 6;
        }

        if ((one & 0x20) != 0)
        {
            c |= bit2 << 7;
        }

        if ((one & 0x5B) != 0)
        {
            b0 |= bit0 << 6;
            b1 |= bit1 << 6;
        }

        if ((one & 0x12) != 0)
        {
            b0 |= bit2 << 7;
            b1 |= bit3 << 7;
        }

        if ((one & 0xAF) != 0)
        {
            d0 |= bit4 << 5;
            d1 |= bit5 << 5;
        }

        if ((one & 0x5) != 0)
        {
            d0 |= bit2 << 6;
            d1 |= bit3 << 6;
        }

        int sx = 32 - dbits;
        d0 = (d0 << sx) >> sx;
        d1 = (d1 << sx) >> sx;
        int shift = (modeValue >> 1) ^ 3;
        a <<= shift;
        b0 <<= shift;
        b1 <<= shift;
        c <<= shift;
        d0 <<= shift;
        d1 <<= shift;
        int red1 = a, green1 = a - b0, blue1 = a - b1;
        int red0 = a - c, green0 = a - b0 - c - d0, blue0 = a - b1 - c - d1;
        red0 = Math.Clamp(red0, 0, 0xFFF);
        green0 = Math.Clamp(green0, 0, 0xFFF);
        blue0 = Math.Clamp(blue0, 0, 0xFFF);
        red1 = Math.Clamp(red1, 0, 0xFFF);
        green1 = Math.Clamp(green1, 0, 0xFFF);
        blue1 = Math.Clamp(blue1, 0, 0xFFF);
        if (major == 1)
        {
            (red0, green0) = (green0, red0);
            (red1, green1) = (green1, red1);
        }
        else if (major == 2)
        {
            (red0, blue0) = (blue0, red0);
            (red1, blue1) = (blue1, red1);
        }

        Set(e0, red0 << 4, green0 << 4, blue0 << 4, 0);
        Set(e1, red1 << 4, green1 << 4, blue1 << 4, 0);
    }

    private static void HdrAlpha(int v6, int v7, out int a0, out int a1)
    {
        int selector = ((v6 >> 7) & 1) | ((v7 >> 6) & 2);
        v6 &= 0x7F;
        v7 &= 0x7F;
        if (selector == 3)
        {
            a0 = v6 << 5;
            a1 = v7 << 5;
        }
        else
        {
            v6 |= (v7 << (selector + 1)) & 0x780;
            v7 &= 0x3F >> selector;
            v7 ^= 32 >> selector;
            v7 -= 32 >> selector;
            v6 <<= 4 - selector;
            v7 <<= 4 - selector;
            v7 += v6;
            a0 = v6;
            a1 = Math.Clamp(v7, 0, 0xFFF);
        }

        a0 <<= 4;
        a1 <<= 4;
    }

    private static void Set(Span<int> e, int r, int g, int b, int a)
    {
        e[0] = r;
        e[1] = g;
        e[2] = b;
        e[3] = a;
    }

    private static int Clamp8(int v) => Math.Clamp(v, 0, 255);

    private static BlockMode DecodeMode(int mode, int blockWidth, int blockHeight)
    {
        int r0 = (mode >> 4) & 1;
        int h = (mode >> 9) & 1;
        int d = (mode >> 10) & 1;
        int a = (mode >> 5) & 3;
        int r, w, gh;
        if ((mode & 3) != 0)
        {
            r = r0 | ((mode & 3) << 1);
            int b = (mode >> 7) & 3;
            switch ((mode >> 2) & 3)
            {
                case 0:
                    w = b + 4;
                    gh = a + 2;
                    break;
                case 1:
                    w = b + 8;
                    gh = a + 2;
                    break;
                case 2:
                    w = a + 2;
                    gh = b + 8;
                    break;
                default:
                    b &= 1;
                    if ((mode & 0x100) != 0)
                    {
                        w = b + 2;
                        gh = a + 2;
                    }
                    else
                    {
                        w = a + 2;
                        gh = b + 6;
                    }

                    break;
            }
        }
        else
        {
            if ((mode & 15) == 0)
            {
                return default;
            }

            r = r0 | (((mode >> 2) & 3) << 1);
            switch ((mode >> 7) & 3)
            {
                case 0:
                    w = 12;
                    gh = a + 2;
                    break;
                case 1:
                    w = a + 2;
                    gh = 12;
                    break;
                case 2:
                    w = a + 6;
                    gh = ((mode >> 9) & 3) + 6;
                    d = 0;
                    h = 0;
                    break;
                default:
                    if (a == 0)
                    {
                        w = 6;
                        gh = 10;
                    }
                    else if (a == 1)
                    {
                        w = 10;
                        gh = 6;
                    }
                    else
                    {
                        return default;
                    }

                    break;
            }
        }

        int range = r - 2 + (h * 6);
        int count = w * gh * (d + 1);
        if (r < 2 || w > blockWidth || gh > blockHeight || count > 64)
        {
            return default;
        }

        int bits = AstcIse.BitCount(count, range);
        if (bits < 24 || bits > 96)
        {
            return default;
        }

        return new BlockMode(true, (byte)w, (byte)gh, d != 0, (byte)range);
    }

    /// <summary>A decoded block mode: the weight grid, whether there are two planes, and the weight range.</summary>
    internal readonly record struct BlockMode(bool Valid, byte GridWidth, byte GridHeight, bool DualPlane, byte WeightRange);

    /// <summary>The bilinear weight infill from a grid to the footprint's texels: four grid points and weights per texel.</summary>
    internal sealed class Infill
    {
        private readonly short[] _index;
        private readonly byte[] _weight;
        private readonly int _gridWidth;
        private readonly int _gridCount;

        public Infill(int blockWidth, int blockHeight, int gridWidth, int gridHeight)
        {
            int texels = blockWidth * blockHeight;
            _index = new short[texels * 4];
            _weight = new byte[texels * 4];
            _gridWidth = gridWidth;
            _gridCount = gridWidth * gridHeight;
            int ds = (1024 + (blockWidth / 2)) / (blockWidth - 1);
            int dt = (1024 + (blockHeight / 2)) / (blockHeight - 1);
            for (int t = 0; t < blockHeight; t++)
            {
                for (int s = 0; s < blockWidth; s++)
                {
                    int gs = ((ds * s * (gridWidth - 1)) + 32) >> 6;
                    int gt = ((dt * t * (gridHeight - 1)) + 32) >> 6;
                    int js = gs >> 4, fs = gs & 15, jt = gt >> 4, ft = gt & 15;
                    int w11 = ((fs * ft) + 8) >> 4;
                    int w10 = ft - w11;
                    int w01 = fs - w11;
                    int w00 = 16 - fs - ft + w11;
                    int v0 = js + (jt * gridWidth);
                    int i = ((t * blockWidth) + s) * 4;
                    _index[i] = (short)v0;
                    _index[i + 1] = (short)(v0 + 1);
                    _index[i + 2] = (short)(v0 + gridWidth);
                    _index[i + 3] = (short)(v0 + gridWidth + 1);
                    _weight[i] = (byte)w00;
                    _weight[i + 1] = (byte)w01;
                    _weight[i + 2] = (byte)w10;
                    _weight[i + 3] = (byte)w11;
                }
            }
        }

        /// <summary>Gets the texels' grid points, four per texel.</summary>
        public ReadOnlySpan<short> Indices => _index;

        /// <summary>Gets the texels' bilinear weights (sixteenths), four per texel.</summary>
        public ReadOnlySpan<byte> Weights => _weight;

        /// <summary>
        /// Infills the grid weights of one plane: <paramref name="grid"/> holds the planes interleaved, <paramref name="planes"/>
        /// per grid point, and <paramref name="plane"/> picks one.
        /// </summary>
        public void Apply(ReadOnlySpan<int> grid, int planes, int plane, Span<int> texels)
        {
            for (int t = 0; t < texels.Length; t++)
            {
                int i = t * 4;
                int sum = 8;
                for (int k = 0; k < 4; k++)
                {
                    int w = _weight[i + k];
                    if (w != 0)
                    {
                        sum += grid[(_index[i + k] * planes) + plane] * w;
                    }
                }

                texels[t] = sum >> 4;
            }
        }
    }
}
