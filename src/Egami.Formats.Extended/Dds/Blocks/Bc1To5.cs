using System.Buffers.Binary;

namespace Egami.Formats.Extended.Dds.Blocks;

/// <summary>
/// Decodes the S3TC and RGTC blocks, BC1 to BC5, written from the Direct3D 11 block compression specification and the Khronos
/// Data Format Specification. Each block is 4 × 4 pixels, decoded into 16 values in row order. Colours interpolate the stored
/// 5- and 6-bit endpoints exactly and round once, as Direct3D's reference decoder does in floating point.
/// </summary>
internal static class Bc1To5
{
    /// <summary>
    /// Decodes a BC1 colour block (8 bytes) into 16 RGBA pixels, 4 bytes each. With <paramref name="fourColors"/> (the colour
    /// half of BC2 and BC3) the block always interpolates two colours; otherwise endpoint 0 at most endpoint 1 selects the
    /// three-colour mode, whose fourth index is transparent black.
    /// </summary>
    public static void DecodeBc1(ReadOnlySpan<byte> block, Span<byte> rgba, bool fourColors)
    {
        int c0 = BinaryPrimitives.ReadUInt16LittleEndian(block);
        int c1 = BinaryPrimitives.ReadUInt16LittleEndian(block[2..]);
        uint indices = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);
        Span<uint> palette = stackalloc uint[4];
        int r0 = c0 >> 11, g0 = (c0 >> 5) & 63, b0 = c0 & 31;
        int r1 = c1 >> 11, g1 = (c1 >> 5) & 63, b1 = c1 & 31;
        palette[0] = Pack(Scale(r0, 1, 31), Scale(g0, 1, 63), Scale(b0, 1, 31), 255);
        palette[1] = Pack(Scale(r1, 1, 31), Scale(g1, 1, 63), Scale(b1, 1, 31), 255);
        if (fourColors || c0 > c1)
        {
            palette[2] = Pack(Scale((2 * r0) + r1, 3, 31), Scale((2 * g0) + g1, 3, 63), Scale((2 * b0) + b1, 3, 31), 255);
            palette[3] = Pack(Scale(r0 + (2 * r1), 3, 31), Scale(g0 + (2 * g1), 3, 63), Scale(b0 + (2 * b1), 3, 31), 255);
        }
        else
        {
            palette[2] = Pack(Scale(r0 + r1, 2, 31), Scale(g0 + g1, 2, 63), Scale(b0 + b1, 2, 31), 255);
            palette[3] = 0;
        }

        for (int i = 0; i < 16; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(rgba[(i * 4)..], palette[(int)((indices >> (2 * i)) & 3)]);
        }
    }

    /// <summary>Decodes a BC2 block (16 bytes): explicit 4-bit alpha, then a BC1 colour block.</summary>
    public static void DecodeBc2(ReadOnlySpan<byte> block, Span<byte> rgba)
    {
        DecodeBc1(block[8..], rgba, fourColors: true);
        ulong alpha = BinaryPrimitives.ReadUInt64LittleEndian(block);
        for (int i = 0; i < 16; i++)
        {
            rgba[(i * 4) + 3] = (byte)(((alpha >> (4 * i)) & 15) * 17);
        }
    }

    /// <summary>Decodes a BC3 block (16 bytes): an interpolated alpha block, then a BC1 colour block.</summary>
    public static void DecodeBc3(ReadOnlySpan<byte> block, Span<byte> rgba)
    {
        DecodeBc1(block[8..], rgba, fourColors: true);
        DecodeUnorm(block, rgba[3..], 4);
    }

    /// <summary>
    /// Decodes an unsigned BC4 block (8 bytes, also BC3's alpha and each half of BC5) into 16 bytes, every
    /// <paramref name="stride"/>th byte of <paramref name="values"/>.
    /// </summary>
    public static void DecodeUnorm(ReadOnlySpan<byte> block, Span<byte> values, int stride)
    {
        int a0 = block[0];
        int a1 = block[1];
        Span<byte> palette = stackalloc byte[8];
        palette[0] = (byte)a0;
        palette[1] = (byte)a1;
        if (a0 > a1)
        {
            for (int i = 1; i < 7; i++)
            {
                palette[i + 1] = (byte)(((((7 - i) * a0) + (i * a1)) * 2 + 7) / 14);
            }
        }
        else
        {
            for (int i = 1; i < 5; i++)
            {
                palette[i + 1] = (byte)(((((5 - i) * a0) + (i * a1)) * 2 + 5) / 10);
            }

            palette[6] = 0;
            palette[7] = 255;
        }

        ulong indices = ReadIndices(block);
        for (int i = 0; i < 16; i++)
        {
            values[i * stride] = palette[(int)((indices >> (3 * i)) & 7)];
        }
    }

    /// <summary>
    /// Decodes a signed BC4 block (8 bytes) into 16 bytes, every <paramref name="stride"/>th byte of <paramref name="values"/>,
    /// with −1 to 1 mapped to 0 to 255. The endpoint −128 reads as −127, as the specification requires.
    /// </summary>
    public static void DecodeSnorm(ReadOnlySpan<byte> block, Span<byte> values, int stride)
    {
        int a0 = Math.Max((int)(sbyte)block[0], -127);
        int a1 = Math.Max((int)(sbyte)block[1], -127);
        Span<byte> palette = stackalloc byte[8];
        palette[0] = SnormToByte(a0, 1);
        palette[1] = SnormToByte(a1, 1);
        if (a0 > a1)
        {
            for (int i = 1; i < 7; i++)
            {
                palette[i + 1] = SnormToByte(((7 - i) * a0) + (i * a1), 7);
            }
        }
        else
        {
            for (int i = 1; i < 5; i++)
            {
                palette[i + 1] = SnormToByte(((5 - i) * a0) + (i * a1), 5);
            }

            palette[6] = 0;
            palette[7] = 255;
        }

        ulong indices = ReadIndices(block);
        for (int i = 0; i < 16; i++)
        {
            values[i * stride] = palette[(int)((indices >> (3 * i)) & 7)];
        }
    }

    /// <summary>Maps the signed level <paramref name="n"/> / <paramref name="d"/> (−127 to 127) to 0 to 255, rounding once.</summary>
    internal static byte SnormToByte(int n, int d)
    {
        return (byte)((((n + (127 * d)) * 255 * 2) + (254 * d)) / (508 * d));
    }

    /// <summary>Returns round(<paramref name="n"/> × 255 / (<paramref name="d"/> × <paramref name="max"/>)).</summary>
    private static int Scale(int n, int d, int max)
    {
        int den = d * max;
        return ((n * 255 * 2) + den) / (2 * den);
    }

    private static uint Pack(int r, int g, int b, int a) => (uint)(r | (g << 8) | (b << 16) | (a << 24));

    private static ulong ReadIndices(ReadOnlySpan<byte> block)
    {
        return block[2] | ((ulong)block[3] << 8) | ((ulong)block[4] << 16) | ((ulong)block[5] << 24) | ((ulong)block[6] << 32) | ((ulong)block[7] << 40);
    }
}
