using System.Buffers.Binary;

namespace Egami.Formats.Extended.Tests.Dds;

/// <summary>Builds DDS files byte by byte, from Microsoft's DDS programming guide, for the reader tests.</summary>
internal static class DdsFiles
{
    public const uint Rgb = 0x40;
    public const uint AlphaPixels = 0x1;
    public const uint Alpha = 0x2;
    public const uint FourCC = 0x4;
    public const uint Pal8 = 0x20;
    public const uint Luminance = 0x20000;
    public const uint BumpDuDv = 0x80000;

    /// <summary>A file with a legacy pixel format given by bit masks.</summary>
    public static byte[] Masks(int width, int height, uint flags, int bits, uint r, uint g, uint b, uint a, ReadOnlySpan<byte> data, int mips = 1, uint caps2 = 0, int depth = 0, byte[]? palette = null)
    {
        byte[] header = Header(width, height, mips, caps2, depth, flags, 0, bits, r, g, b, a);
        return [.. header, .. palette ?? [], .. data];
    }

    /// <summary>A file with a legacy FourCC.</summary>
    public static byte[] FourCCFile(int width, int height, string code, ReadOnlySpan<byte> data, int mips = 1, uint caps2 = 0)
    {
        uint fourCC = code.Length == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(System.Text.Encoding.ASCII.GetBytes(code)) : uint.Parse(code, System.Globalization.CultureInfo.InvariantCulture);
        return [.. Header(width, height, mips, caps2, 0, FourCC, fourCC, 0, 0, 0, 0, 0), .. data];
    }

    /// <summary>A file with the DX10 header.</summary>
    public static byte[] Dx10(int width, int height, int dxgi, ReadOnlySpan<byte> data, int mips = 1, int arraySize = 1, bool cube = false, int dimension = 3, int depth = 1, uint alphaMode = 0)
    {
        byte[] header = Header(width, height, mips, 0, dimension == 4 ? depth : 0, FourCC, 0x30315844, 0, 0, 0, 0, 0);
        byte[] dx10 = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(dx10, (uint)dxgi);
        BinaryPrimitives.WriteUInt32LittleEndian(dx10.AsSpan(4), (uint)dimension);
        BinaryPrimitives.WriteUInt32LittleEndian(dx10.AsSpan(8), cube ? 4u : 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(dx10.AsSpan(12), (uint)arraySize);
        BinaryPrimitives.WriteUInt32LittleEndian(dx10.AsSpan(16), alphaMode);
        return [.. header, .. dx10, .. data];
    }

    private static byte[] Header(int width, int height, int mips, uint caps2, int depth, uint pfFlags, uint fourCC, int bits, uint r, uint g, uint b, uint a)
    {
        byte[] h = new byte[128];
        Span<byte> s = h;
        BinaryPrimitives.WriteUInt32LittleEndian(s, 0x20534444);
        BinaryPrimitives.WriteUInt32LittleEndian(s[4..], 124);
        BinaryPrimitives.WriteUInt32LittleEndian(s[8..], 0x1007 | (mips > 1 ? 0x20000u : 0) | (depth > 0 ? 0x800000u : 0));
        BinaryPrimitives.WriteUInt32LittleEndian(s[12..], (uint)height);
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(s[24..], (uint)depth);
        BinaryPrimitives.WriteUInt32LittleEndian(s[28..], (uint)mips);
        BinaryPrimitives.WriteUInt32LittleEndian(s[76..], 32);
        BinaryPrimitives.WriteUInt32LittleEndian(s[80..], pfFlags);
        BinaryPrimitives.WriteUInt32LittleEndian(s[84..], fourCC);
        BinaryPrimitives.WriteUInt32LittleEndian(s[88..], (uint)bits);
        BinaryPrimitives.WriteUInt32LittleEndian(s[92..], r);
        BinaryPrimitives.WriteUInt32LittleEndian(s[96..], g);
        BinaryPrimitives.WriteUInt32LittleEndian(s[100..], b);
        BinaryPrimitives.WriteUInt32LittleEndian(s[104..], a);
        BinaryPrimitives.WriteUInt32LittleEndian(s[108..], 0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(s[112..], caps2);
        return h;
    }

    /// <summary>Writes the fields of a 128-bit BC6H or BC7 block from bit 0 upwards.</summary>
    public sealed class BlockBits
    {
        private UInt128 _bits;
        private int _position;

        public BlockBits Put(int value, int count)
        {
            _bits |= (UInt128)(ulong)(value & ((1L << count) - 1)) << _position;
            _position += count;
            return this;
        }

        public byte[] ToArray()
        {
            byte[] block = new byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(block, (ulong)_bits);
            BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(8), (ulong)(_bits >> 64));
            return block;
        }
    }
}
