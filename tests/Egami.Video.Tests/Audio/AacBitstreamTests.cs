using Egami.Video.Audio.Codecs.Aac;

namespace Egami.Video.Tests.Audio;

/// <summary>The AAC decoder's bit reader and Huffman lookup, against hand-made codes.</summary>
public class AacBitstreamTests
{
    [Fact]
    public void ReadsBitsMostSignificantFirst()
    {
        byte[] data = [0b1010_0000, 0xFF, 0x01, 0x80, 0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC];
        AacBitReader reader = new(data);
        Assert.Equal(1u, reader.Read(1));
        Assert.Equal(0b01u, reader.Read(2));
        Assert.Equal(0u, reader.Read(5));
        Assert.Equal(0xFF01u, reader.Read(16));
        Assert.Equal(1u, reader.Read(1));
        Assert.Equal(0u, reader.Read(7));
        Assert.Equal(0x12345678u, reader.Read(32));
        Assert.False(reader.Overrun);
    }

    [Fact]
    public void PeeksAndSkips()
    {
        byte[] data = [0x12, 0x34, 0x56, 0x78, 0x9A];
        AacBitReader reader = new(data);
        reader.Skip(4);
        Assert.Equal(0x23456789u, reader.Peek32());
        reader.Skip(32);
        Assert.Equal(0xA0000000u, reader.Peek32());
        Assert.Equal(4, reader.Remaining);
        reader.Skip(8);
        Assert.True(reader.Overrun);
    }

    [Fact]
    public void AlignsToBytes()
    {
        AacBitReader reader = new([0xFF, 0x80]);
        reader.Read(3);
        reader.Align();
        Assert.Equal(8, reader.Position);
        Assert.Equal(1u, reader.Read(1));
    }

    [Fact]
    public void DecodesShortAndLongCodes()
    {
        // A complete prefix code with lengths 1 to 14: symbol i < 13 is i ones then a zero, the last two are 13 ones then 0 or 1.
        const int Symbols = 15;
        uint[] codes = new uint[Symbols];
        byte[] lengths = new byte[Symbols];
        for (int i = 0; i < 13; i++)
        {
            lengths[i] = (byte)(i + 1);
            codes[i] = ((1u << i) - 1) << 1;
        }

        lengths[13] = 14;
        codes[13] = ((1u << 13) - 1) << 1;
        lengths[14] = 14;
        codes[14] = (1u << 14) - 1;
        AacHuffman huffman = new(codes, lengths);
        Assert.Equal(14, huffman.MaxLength);

        // Write every symbol once, then decode them back.
        List<bool> bits = [];
        for (int i = 0; i < Symbols; i++)
        {
            for (int b = lengths[i] - 1; b >= 0; b--)
            {
                bits.Add(((codes[i] >> b) & 1) != 0);
            }
        }

        byte[] data = new byte[(bits.Count + 7) / 8];
        for (int i = 0; i < bits.Count; i++)
        {
            if (bits[i])
            {
                data[i / 8] |= (byte)(0x80 >> (i % 8));
            }
        }

        AacBitReader reader = new(data);
        for (int i = 0; i < Symbols; i++)
        {
            Assert.Equal(i, huffman.Decode(ref reader));
        }

        Assert.Equal(bits.Count, reader.Position);
    }

    [Fact]
    public void RefusesBitsThatStartNoCode()
    {
        // Only "0" and "10" are codes, so "11..." starts none.
        AacHuffman huffman = new([0b0, 0b10], [1, 2]);
        AacBitReader reader = new([0xC0]);
        Assert.Equal(-1, huffman.Decode(ref reader));
    }
}
