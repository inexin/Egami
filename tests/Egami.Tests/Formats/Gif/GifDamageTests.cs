using System.Buffers;

using Egami.Formats;
using Egami.Formats.Gif;
using Egami.Pixels;

namespace Egami.Tests.Formats.Gif;

/// <summary>Limits, damaged copies of every corpus file, and hand-made files for cases tools will not write.</summary>
public class GifDamageTests
{
    private static readonly EgamiContext Context = EgamiContext.Default with
    {
        MaxParallelism = 1,
        Limits = SafetyLimits.Default with { MaxPixels = 4_000_000, MaxTotalBytes = 64L * 1024 * 1024 },
    };

    private static readonly ReaderOptions Strict = new() { Recovery = RecoveryMode.Strict };

    public static TheoryData<string> Files() => [.. AnimCorpus.Files("gif")];

    [Theory]
    [MemberData(nameof(Files))]
    public void TruncatedCopiesKeepTheFramesSoFar(string file)
    {
        byte[] data = AnimCorpus.Read(file);
        foreach (int length in new[] { data.Length - 1, (data.Length * 3) / 4, data.Length / 2 })
        {
            byte[] cut = data[..length];
            using (Raster<Rgba8> raster = Raster.Load<Rgba8>(cut, Context))
            {
                Assert.NotEmpty(raster.Metadata.Warnings);
            }

            Assert.Throws<CorruptDataException>(() => Raster.Load<Rgba8>(cut, Context, Strict));
            Assert.True(Raster.Probe(cut, Context).FrameCount >= 1);
        }
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void BitFlippedCopiesFailOnlyWithEgamiExceptions(string file)
    {
        byte[] data = AnimCorpus.Read(file);
        Random random = new(file.Length);
        for (int round = 0; round < 40; round++)
        {
            byte[] copy = (byte[])data.Clone();
            for (int flips = 0; flips < 1 + (round % 4); flips++)
            {
                copy[6 + random.Next(copy.Length - 6)] ^= (byte)(1 << random.Next(8));
            }

            foreach (ReaderOptions options in new[] { ReaderOptions.Default, Strict })
            {
                try
                {
                    using Raster raster = Raster.Load(copy, Context, options);
                    Raster.Probe(copy, Context, options);
                }
                catch (EgamiException)
                {
                }
            }
        }
    }

    [Fact]
    public void TooManyFramesThrowsAsEachFrameStarts()
    {
        EgamiContext context = Context with { Limits = Context.Limits with { MaxFrames = 5 } };
        byte[] data = AnimCorpus.Read("gif/loop-3.gif");
        Assert.Throws<LimitExceededException>(() => Raster.Load(data, context));
        Assert.Throws<LimitExceededException>(() => Raster.Probe(data, context));
    }

    [Fact]
    public void HugeLogicalScreenThrowsBeforeAllocating()
    {
        byte[] data = Build(65535, 65535, [(0, 0, 1, 1, [0])]);
        Assert.Throws<LimitExceededException>(() => Raster.Load(data, Context));
    }

    [Fact]
    public void SmallFileExpandingPastMaxTotalBytesThrows()
    {
        // 1,000 one-pixel frames on a 2000 x 2000 screen: tiny on disk, 16 MB per decoded frame.
        byte[] data = Build(2000, 2000, [.. Enumerable.Repeat((0, 0, 1, 1, new byte[] { 1 }), 1000)]);
        Assert.True(data.Length < 40_000);
        Assert.Throws<LimitExceededException>(() => Raster.Load(data, Context));
        Assert.True(Raster.Probe(data, Context).EstimatedBytes > Context.Limits.MaxTotalBytes);
    }

    [Fact]
    public void FrameStickingOutIsClippedLenientlyAndCorruptStrictly()
    {
        byte[] data = Build(8, 8, [(0, 0, 8, 8, Fill(64, 0)), (6, 5, 4, 4, Fill(16, 1))]);
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(data, Context);
        Assert.Single(raster.Metadata.Warnings);
        Assert.Equal(new Rgba8(255, 255, 255, 255), raster.Frames[1].Row(7)[7]);
        Assert.Equal(new Rgba8(0, 0, 0, 255), raster.Frames[1].Row(4)[7]);
        Assert.Throws<CorruptDataException>(() => Raster.Load(data, Context, Strict));
    }

    [Fact]
    public void FrameWithoutAnyColorTableIsTransparent()
    {
        byte[] data = Build(4, 4, [(0, 0, 4, 4, Fill(16, 1))], globalPalette: false);
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(data, Context);
        Assert.NotEmpty(raster.Metadata.Warnings);
        Assert.Equal(default, raster.Row(2)[2]);
        Assert.Throws<CorruptDataException>(() => Raster.Load(data, Context, Strict));
    }

    [Fact]
    public void IndicesPastTheTableAreTransparent()
    {
        // A 2-color table with indices up to 3.
        byte[] data = Build(2, 2, [(0, 0, 2, 2, [0, 1, 2, 3])], minCodeSize: 2);
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(data, Context);
        Assert.Equal(new Rgba8(255, 255, 255, 255), raster.Row(0)[1]);
        Assert.Equal(default, raster.Row(1)[0]);
        Assert.NotEmpty(raster.Metadata.Warnings);
        Assert.Throws<CorruptDataException>(() => Raster.Load(data, Context, Strict));
    }

    [Fact]
    public void MinimumCodeSizeOneIsReadWithAWarning()
    {
        byte[] data = Build(3, 3, [(0, 0, 3, 3, [0, 1, 1, 0, 1, 0, 1, 1, 1])], minCodeSize: 1);
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(data, Context);
        Assert.Single(raster.Metadata.Warnings);
        Assert.Equal(new Rgba8(255, 255, 255, 255), raster.Row(2)[2]);
        Assert.Equal(new Rgba8(0, 0, 0, 255), raster.Row(0)[0]);
    }

    [Fact]
    public void BadLzwCodeIsCorruptInBothModes()
    {
        byte[] data = Build(2, 2, [(0, 0, 2, 2, [0, 1, 1, 0])]);
        int image = Array.LastIndexOf(data, (byte)0x2C);

        // The first code after the clear is replaced by 7, which cannot exist yet (codes are 3 bits: clear, then 7).
        data[image + 12] = 0x3C;
        Assert.Throws<CorruptDataException>(() => Raster.Load(data, Context));
    }

    [Fact]
    public void XmpIccAndOtherApplicationExtensionsAreRead()
    {
        byte[] image = Build(2, 2, [(0, 0, 2, 2, [0, 1, 1, 0])]);
        byte[] xmp = "<x:xmpmeta xmlns:x='adobe:ns:meta/'>Egami</x:xmpmeta>"u8.ToArray();
        ArrayBufferWriter<byte> extensions = new();
        extensions.Write<byte>([0x21, 0xFF, 11]);
        extensions.Write("XMP DataXMP"u8);
        extensions.Write(xmp);
        extensions.Write<byte>([0x01]);
        for (int i = 255; i >= 0; i--)
        {
            extensions.Write<byte>([(byte)i]);
        }

        extensions.Write<byte>([0]);
        extensions.Write<byte>([0x21, 0xFF, 11]);
        extensions.Write("ICCRGBG1012"u8);
        extensions.Write<byte>([4, 1, 2, 3, 4, 2, 5, 6, 0]);
        extensions.Write<byte>([0x21, 0xFF, 11]);
        extensions.Write("EGAMITST1.0"u8);
        extensions.Write<byte>([3, 7, 8, 9, 0]);

        // After the header and the 2-color table.
        byte[] data = [.. image[..19], .. extensions.WrittenSpan, .. image[19..]];
        using Raster raster = Raster.Load(data, Context, Strict);
        Assert.Equal(xmp, raster.Metadata.Xmp.ToArray());
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, raster.Metadata.IccProfile.ToArray());
        GifApplicationExtension other = Assert.Single(((GifDetails)raster.Metadata.FormatDetails!).OtherExtensions);
        Assert.Equal("EGAMITST1.0", other.Identifier);
        Assert.Equal(new byte[] { 7, 8, 9 }, other.Data.ToArray());
        Assert.True(Raster.Probe(data, Context).HasMetadata);

        using Raster bare = Raster.Load(data, Context, new ReaderOptions { ReadMetadata = false });
        Assert.True(bare.Metadata.Xmp.IsEmpty && bare.Metadata.IccProfile.IsEmpty);
    }

    private static byte[] Fill(int count, byte value) => [.. Enumerable.Repeat(value, count)];

    /// <summary>Writes a GIF89a with a black-and-white global table and the given frames (full indices, no compression tricks).</summary>
    internal static byte[] Build(int width, int height, (int Left, int Top, int Width, int Height, byte[] Indices)[] frames, bool globalPalette = true, int minCodeSize = 2)
    {
        ArrayBufferWriter<byte> output = new();
        output.Write("GIF89a"u8);
        output.Write<byte>([(byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8), (byte)(globalPalette ? 0x80 : 0), 0, 0]);
        if (globalPalette)
        {
            output.Write<byte>([0, 0, 0, 255, 255, 255]);
        }

        using LzwEncoder encoder = new();
        foreach ((int left, int top, int w, int h, byte[] indices) in frames)
        {
            output.Write<byte>([0x2C, (byte)left, (byte)(left >> 8), (byte)top, (byte)(top >> 8), (byte)w, (byte)(w >> 8), (byte)h, (byte)(h >> 8), 0]);
            encoder.Encode(minCodeSize, indices, output);
        }

        output.Write<byte>([0x3B]);
        return output.WrittenSpan.ToArray();
    }
}
