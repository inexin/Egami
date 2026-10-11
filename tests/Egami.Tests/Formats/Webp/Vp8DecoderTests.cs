using System.Buffers.Binary;

using Egami.Formats.Webp;

namespace Egami.Tests.Formats.Webp;

/// <summary>Phase 2.3.1 task 7: the VP8 decoder's planes equal libwebp's (WebPDecodeYUV) sample for sample on every lossy file.</summary>
public class Vp8DecoderTests
{
    public static TheoryData<string> Lossy() => [.. WebpCorpus.Lossy];

    [Theory]
    [MemberData(nameof(Lossy))]
    public void PlanesEqualLibwebp(string file)
    {
        byte[] data = WebpCorpus.Read(file);
        (int offset, int length) = FindChunk(data, "VP8 ");
        (int width, int height) = WebpCorpus.Size(file);
        byte[] expected = WebpCorpus.Yuv(file);
        int chromaWidth = (width + 1) / 2;
        int chromaHeight = (height + 1) / 2;
        byte[] actual = new byte[expected.Length];
        using Vp8Decoder decoder = Vp8Decoder.Create(data, offset, length);
        Assert.Equal((width, height), (decoder.Width, decoder.Height));
        while (decoder.DecodeRow((d, from, to, chromaFrom, chromaTo) =>
        {
            for (int y = from; y < to; y++)
            {
                d.RowY(y).CopyTo(actual.AsSpan(y * width, width));
            }

            for (int y = chromaFrom; y < chromaTo; y++)
            {
                d.RowU(y).CopyTo(actual.AsSpan((width * height) + (y * chromaWidth), chromaWidth));
                d.RowV(y).CopyTo(actual.AsSpan((width * height) + (chromaWidth * chromaHeight) + (y * chromaWidth), chromaWidth));
            }
        }))
        {
        }

        int first = actual.AsSpan().CommonPrefixLength(expected);
        if (first < expected.Length)
        {
            string plane = first < width * height ? $"Y ({first % width}, {first / width})" : first < (width * height) + (chromaWidth * chromaHeight)
                ? $"U ({(first - (width * height)) % chromaWidth}, {(first - (width * height)) / chromaWidth})"
                : $"V ({(first - (width * height) - (chromaWidth * chromaHeight)) % chromaWidth}, {(first - (width * height) - (chromaWidth * chromaHeight)) / chromaWidth})";
            Assert.Fail($"{file}: {plane} is {actual[first]}, libwebp gives {expected[first]}.");
        }

        Assert.False(decoder.PastEnd);
    }

    /// <summary>Returns the payload of the first chunk of a type, walking the RIFF chunks and the frames of an animation.</summary>
    internal static (int Offset, int Length) FindChunk(byte[] file, string type)
    {
        for (int at = 12; at + 8 <= file.Length;)
        {
            string t = System.Text.Encoding.ASCII.GetString(file, at, 4);
            int size = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(at + 4));
            if (t == type)
            {
                return (at + 8, size);
            }

            at += 8 + size + (size & 1);
        }

        throw new InvalidOperationException($"No {type} chunk.");
    }
}
