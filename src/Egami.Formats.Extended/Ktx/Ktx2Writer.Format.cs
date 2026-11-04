using System.Buffers.Binary;

using Egami.Formats.Extended.Textures;
using Egami.Metadata;

namespace Egami.Formats.Extended.Ktx;

internal sealed partial class Ktx2Writer
{
    private const uint Max = uint.MaxValue;

    private static readonly uint One = BitConverter.SingleToUInt32Bits(1f);
    private static readonly uint MinusOne = BitConverter.SingleToUInt32Bits(-1f);

    /// <summary>
    /// Builds the data format descriptor: one basic block whose colour model, texel block size, plane bytes and samples are the
    /// ones the Khronos Data Format Specification gives the format, as libktx writes them; the primaries and transfer function
    /// come from the raster's colour tag (an _SRGB format always takes the sRGB transfer function).
    /// </summary>
    internal static byte[] Dfd(Target target, bool srgb, bool alpha, bool premultiplied, RasterMetadata metadata)
    {
        Sample[] samples = Samples(target, srgb, alpha);
        byte[] dfd = new byte[4 + 24 + (16 * samples.Length)];
        Span<byte> d = dfd;
        BinaryPrimitives.WriteUInt32LittleEndian(d, (uint)dfd.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(d[8..], 2);
        BinaryPrimitives.WriteUInt16LittleEndian(d[10..], (ushort)(24 + (16 * samples.Length)));
        d[12] = target.Model;
        d[13] = Primaries(metadata);
        d[14] = srgb ? (byte)2 : Transfer(metadata);
        d[15] = premultiplied ? (byte)1 : (byte)0;
        if (target.IsBlock)
        {
            d[16] = (byte)(target.BlockWidth - 1);
            d[17] = (byte)(target.BlockHeight - 1);
        }

        if (target.Format == KtxTextureFormat.Etc1s)
        {
            d[20] = 8;
            d[21] = alpha ? (byte)8 : (byte)0;
        }
        else
        {
            d[20] = (byte)(target.IsBlock ? target.BlockBytes : target.PixelBytes);
        }

        for (int i = 0; i < samples.Length; i++)
        {
            Span<byte> s = d[(28 + (16 * i))..];
            Sample sample = samples[i];
            BinaryPrimitives.WriteUInt16LittleEndian(s, (ushort)sample.Offset);
            s[2] = (byte)(sample.Bits - 1);
            s[3] = sample.Channel;
            BinaryPrimitives.WriteUInt32LittleEndian(s[8..], sample.Lower);
            BinaryPrimitives.WriteUInt32LittleEndian(s[12..], sample.Upper);
        }

        return dfd;
    }

    /// <summary>The KHR_DF primaries of the raster's colour tag: H.273 code points mapped back, else BT.709.</summary>
    private static byte Primaries(RasterMetadata metadata) => metadata.CodePoints?.Primaries switch
    {
        5 => 2, // BT601_EBU
        6 => 3, // BT601_SMPTE
        9 => 4, // BT2020
        4 => 8, // NTSC1953
        12 => 10, // DISPLAYP3
        _ => 1, // BT709
    };

    /// <summary>The KHR_DF transfer function of a raster that is not sRGB: H.273 code points mapped back, else linear.</summary>
    private static byte Transfer(RasterMetadata metadata) => metadata.CodePoints?.TransferCharacteristics switch
    {
        1 => 3, // ITU
        6 => 4, // NTSC
        18 => 8, // HLG
        16 => 10, // PQ
        17 => 12, // DCIP3
        7 => 15, // ST240
        _ => 1, // LINEAR
    };

    private static Sample[] Samples(Target target, bool srgb, bool alpha)
    {
        // Alpha in an sRGB format is linear, and says so.
        byte a = srgb ? (byte)0x1F : (byte)0x0F;
        const byte Signed = 0x40, Float = 0x80;
        return target.Format switch
        {
            KtxTextureFormat.Rgba8 => Unorm((0, 8), (1, 8), (2, 8), (a, 8)),
            KtxTextureFormat.Bgra8 => Unorm((2, 8), (1, 8), (0, 8), (a, 8)),
            KtxTextureFormat.Rgb8 => Unorm((0, 8), (1, 8), (2, 8)),
            KtxTextureFormat.R8 => Unorm((0, 8)),
            KtxTextureFormat.Rg8 => Unorm((0, 8), (1, 8)),
            KtxTextureFormat.R16 => Unorm((0, 16)),
            KtxTextureFormat.Rg16 => Unorm((0, 16), (1, 16)),
            KtxTextureFormat.Rgb16 => Unorm((0, 16), (1, 16), (2, 16)),
            KtxTextureFormat.Rgba16 => Unorm((0, 16), (1, 16), (2, 16), (0xF, 16)),
            KtxTextureFormat.RgbaF16 => Floats(16, 0, 1, 2, 0xF),
            KtxTextureFormat.R32F => Floats(32, 0),
            KtxTextureFormat.RgbF32 => Floats(32, 0, 1, 2),
            KtxTextureFormat.RgbaF32 => Floats(32, 0, 1, 2, 0xF),
            KtxTextureFormat.Rgb10A2 => Unorm((0, 10), (1, 10), (2, 10), (0xF, 2)),
            KtxTextureFormat.R11G11B10F => [new(0, 11, Float, 0, One), new(11, 11, Float | 1, 0, One), new(22, 10, Float | 2, 0, One)],
            KtxTextureFormat.Rgb9E5 =>
            [
                new(0, 9, 0, 0, 0x2100), new(27, 5, 0x20, 15, 31),
                new(9, 9, 1, 0, 0x2100), new(27, 5, 0x21, 15, 31),
                new(18, 9, 2, 0, 0x2100), new(27, 5, 0x22, 15, 31),
            ],
            KtxTextureFormat.Bgr565 => Unorm((2, 5), (1, 6), (0, 5)),
            KtxTextureFormat.Bgra5551 => Unorm((2, 5), (1, 5), (0, 5), (0xF, 1)),
            KtxTextureFormat.Bgra4444 => Unorm((2, 4), (1, 4), (0, 4), (0xF, 4)),
            KtxTextureFormat.Bc1 => [new(0, 64, 1, 0, Max)],
            KtxTextureFormat.Bc2 or KtxTextureFormat.Bc3 => [new(0, 64, a, 0, Max), new(64, 64, 0, 0, Max)],
            KtxTextureFormat.Bc4 => [new(0, 64, 0, 0, Max)],
            KtxTextureFormat.Bc5 => [new(0, 64, 0, 0, Max), new(64, 64, 1, 0, Max)],
            KtxTextureFormat.Bc6h => [new(0, 128, Float, 0, One)],
            KtxTextureFormat.Bc6hSigned => [new(0, 128, Float | Signed, MinusOne, One)],
            KtxTextureFormat.Etc1 or KtxTextureFormat.Etc2Rgb => [new(0, 64, 2, 0, Max)],
            KtxTextureFormat.Etc2RgbA1 => [new(0, 64, 2, 0, Max), new(0, 64, a, 0, Max)],
            KtxTextureFormat.Etc2Rgba => [new(0, 64, a, 0, Max), new(64, 64, 2, 0, Max)],
            KtxTextureFormat.EacR11 => [new(0, 64, 0, 0, Max)],
            KtxTextureFormat.EacR11Signed => [new(0, 64, Signed, 0x80000000, 0x7FFFFFFF)],
            KtxTextureFormat.EacRg11 => [new(0, 64, 0, 0, Max), new(64, 64, 1, 0, Max)],
            KtxTextureFormat.EacRg11Signed => [new(0, 64, Signed, 0x80000000, 0x7FFFFFFF), new(64, 64, Signed | 1, 0x80000000, 0x7FFFFFFF)],
            KtxTextureFormat.Etc1s => alpha ? [new(0, 64, 0, 0, Max), new(64, 64, 0xF, 0, Max)] : [new(0, 64, 0, 0, Max)],
            KtxTextureFormat.Uastc => [new(0, 128, alpha ? (byte)3 : (byte)0, 0, Max)],
            _ => [new(0, 128, 0, 0, Max)], // BC7 and ASTC
        };

        static Sample[] Unorm(params ReadOnlySpan<(int Channel, int Bits)> channels)
        {
            Sample[] samples = new Sample[channels.Length];
            int offset = 0;
            for (int i = 0; i < channels.Length; i++)
            {
                samples[i] = new(offset, channels[i].Bits, (byte)channels[i].Channel, 0, (uint)((1L << channels[i].Bits) - 1));
                offset += channels[i].Bits;
            }

            return samples;
        }

        static Sample[] Floats(int bits, params ReadOnlySpan<byte> channels)
        {
            Sample[] samples = new Sample[channels.Length];
            for (int i = 0; i < channels.Length; i++)
            {
                samples[i] = new(i * bits, bits, (byte)(channels[i] | Float | Signed), MinusOne, One);
            }

            return samples;
        }
    }

    /// <summary>One sample of the descriptor: where its bits lie, its channel and qualifiers, and the values its range maps to.</summary>
    private readonly record struct Sample(int Offset, int Bits, byte Channel, uint Lower, uint Upper);

    /// <summary>What a writable format is: its Vulkan numbers, how it is made and how its descriptor describes it.</summary>
    internal sealed record Target(KtxTextureFormat Format, int Vk, int SrgbVk, TextureNatural Work, int PixelBytes, int BlockWidth, int BlockHeight, int BlockBytes, int TypeSize, byte Model)
    {
        public bool IsBlock => BlockBytes > 0;

        public bool HasSrgb => SrgbVk != 0 || Format is KtxTextureFormat.Etc1s or KtxTextureFormat.Uastc;

        public bool IsAstc => Format is >= KtxTextureFormat.Astc4x4 and <= KtxTextureFormat.Astc12x12;

        public int VkFor(bool srgb) => srgb && SrgbVk != 0 ? SrgbVk : Vk;

        public static Target For(KtxTextureFormat format)
        {
            if (format is >= KtxTextureFormat.Astc4x4 and <= KtxTextureFormat.Astc12x12)
            {
                int i = format - KtxTextureFormat.Astc4x4;
                return new(format, 157 + (2 * i), 158 + (2 * i), TextureNatural.Rgba8, 0, Textures.Blocks.Astc.Footprints[i * 2], Textures.Blocks.Astc.Footprints[(i * 2) + 1], 16, 1, 162);
            }

            return format switch
            {
                KtxTextureFormat.Rgba8 => Plain(format, 37, 43, TextureNatural.Rgba8, 4, 1),
                KtxTextureFormat.Bgra8 => Plain(format, 44, 50, TextureNatural.Rgba8, 4, 1),
                KtxTextureFormat.Rgb8 => Plain(format, 23, 29, TextureNatural.Rgb8, 3, 1),
                KtxTextureFormat.R8 => Plain(format, 9, 15, TextureNatural.Gray8, 1, 1),
                KtxTextureFormat.Rg8 => Plain(format, 16, 22, TextureNatural.Rgb8, 2, 1),
                KtxTextureFormat.R16 => Plain(format, 70, 0, TextureNatural.Gray16, 2, 2),
                KtxTextureFormat.Rg16 => Plain(format, 77, 0, TextureNatural.Rgb16, 4, 2),
                KtxTextureFormat.Rgb16 => Plain(format, 84, 0, TextureNatural.Rgb16, 6, 2),
                KtxTextureFormat.Rgba16 => Plain(format, 91, 0, TextureNatural.Rgba16, 8, 2),
                KtxTextureFormat.RgbaF16 => Plain(format, 97, 0, TextureNatural.RgbaF16, 8, 2),
                KtxTextureFormat.R32F => Plain(format, 100, 0, TextureNatural.GrayF32, 4, 4),
                KtxTextureFormat.RgbF32 => Plain(format, 106, 0, TextureNatural.RgbF32, 12, 4),
                KtxTextureFormat.RgbaF32 => Plain(format, 109, 0, TextureNatural.RgbaF32, 16, 4),
                KtxTextureFormat.Rgb10A2 => Plain(format, 64, 0, TextureNatural.Rgb10A2, 4, 4),
                KtxTextureFormat.R11G11B10F => Plain(format, 122, 0, TextureNatural.RgbaF32, 4, 4),
                KtxTextureFormat.Rgb9E5 => Plain(format, 123, 0, TextureNatural.RgbaF32, 4, 4),
                KtxTextureFormat.Bgr565 => Plain(format, 4, 0, TextureNatural.Bgr565, 2, 2),
                KtxTextureFormat.Bgra5551 => Plain(format, 8, 0, TextureNatural.Bgra5551, 2, 2),
                KtxTextureFormat.Bgra4444 => Plain(format, 1000340000, 0, TextureNatural.Bgra4444, 2, 2),
                KtxTextureFormat.Bc1 => Block(format, 133, 134, TextureNatural.Rgba8, 8, 128),
                KtxTextureFormat.Bc2 => Block(format, 135, 136, TextureNatural.Rgba8, 16, 129),
                KtxTextureFormat.Bc3 => Block(format, 137, 138, TextureNatural.Rgba8, 16, 130),
                KtxTextureFormat.Bc4 => Block(format, 139, 0, TextureNatural.Gray8, 8, 131),
                KtxTextureFormat.Bc5 => Block(format, 141, 0, TextureNatural.Rgb8, 16, 132),
                KtxTextureFormat.Bc6h => Block(format, 143, 0, TextureNatural.RgbaF16, 16, 133),
                KtxTextureFormat.Bc6hSigned => Block(format, 144, 0, TextureNatural.RgbaF16, 16, 133),
                KtxTextureFormat.Bc7 => Block(format, 145, 146, TextureNatural.Rgba8, 16, 134),
                KtxTextureFormat.Etc1 or KtxTextureFormat.Etc2Rgb => Block(format, 147, 148, TextureNatural.Rgba8, 8, 161),
                KtxTextureFormat.Etc2RgbA1 => Block(format, 149, 150, TextureNatural.Rgba8, 8, 161),
                KtxTextureFormat.Etc2Rgba => Block(format, 151, 152, TextureNatural.Rgba8, 16, 161),
                KtxTextureFormat.EacR11 => Block(format, 153, 0, TextureNatural.Gray16, 8, 161),
                KtxTextureFormat.EacR11Signed => Block(format, 154, 0, TextureNatural.Gray16, 8, 161),
                KtxTextureFormat.EacRg11 => Block(format, 155, 0, TextureNatural.Rgb16, 16, 161),
                KtxTextureFormat.EacRg11Signed => Block(format, 156, 0, TextureNatural.Rgb16, 16, 161),
                KtxTextureFormat.Etc1s => Block(format, 0, 0, TextureNatural.Rgba8, 8, 163),
                KtxTextureFormat.Uastc => Block(format, 0, 0, TextureNatural.Rgba8, 16, 166),
                _ => throw new ArgumentOutOfRangeException(nameof(format), format, "The KTX 2 format is not defined."),
            };

            static Target Plain(KtxTextureFormat format, int vk, int srgbVk, TextureNatural work, int pixelBytes, int typeSize) =>
                new(format, vk, srgbVk, work, pixelBytes, 1, 1, 0, typeSize, 1);

            static Target Block(KtxTextureFormat format, int vk, int srgbVk, TextureNatural work, int blockBytes, byte model) =>
                new(format, vk, srgbVk, work, 0, 4, 4, blockBytes, 1, model);
        }
    }
}
