using System.Runtime.InteropServices;

using Egami.Formats;
using Egami.Formats.Tga;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Tests.Formats.Tga;

public class TgaDecodeTests(ITestOutputHelper output)
{
    private static readonly string[] Tools = ["expected", "magick", "pillow"];

    public static TheoryData<string> Files() => [.. SimpleCorpus.Files("tga")];

    /// <summary>
    /// Hand-made files follow the script's own pixels, made from the specification: Pillow ignores the 16-bit attribute bit and the
    /// 2.0 alpha type and replicates bits, ImageMagick misreads 16-bit gray with alpha, and neither divides out premultiplied
    /// alpha. Tool-made files follow ImageMagick, which Pillow matches on all of them.
    /// </summary>
    [Theory]
    [MemberData(nameof(Files))]
    public void DecodesLikeTheReference(string file)
    {
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(SimpleCorpus.Read(file));
        byte[] actual = Bytes(raster);
        foreach (string tool in Tools)
        {
            if (SimpleCorpus.Pixels(file, tool) is byte[] pixels)
            {
                output.WriteLine($"{tool}: largest difference {Largest(pixels, actual)}");
            }
        }

        string reference = Tools.First(t => SimpleCorpus.Pixels(file, t) is not null);
        Assert.Equal(0, Largest(SimpleCorpus.Pixels(file, reference)!, actual));
        Assert.Empty(raster.Metadata.Warnings);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void ReadsTheSameFromAStreamThatCannotSeek(string file)
    {
        byte[] data = SimpleCorpus.Read(file);
        using Raster<Rgba8> seekable = Raster.Load<Rgba8>(data);
        using Raster<Rgba8> streamed = Raster.Load<Rgba8>(new ForwardOnlyStream(data));
        Assert.Equal(Bytes(seekable), Bytes(streamed));
        Assert.Equal(seekable.Metadata.Text, streamed.Metadata.Text);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void ProbeAgreesWithLoad(string file)
    {
        byte[] data = SimpleCorpus.Read(file);
        RasterInfo info = Raster.Probe(data);
        using Raster raster = Raster.Load(data);
        Assert.Equal(TgaFormat.Info, info.Format);
        Assert.Equal((raster.Width, raster.Height, raster.Layout), (info.Width, info.Height, info.Layout));
    }

    [Theory]
    [InlineData("tga/rgb24.tga", "Bgr8")]
    [InlineData("tga/rgb16-no-alpha.tga", "Bgr8")]
    [InlineData("tga/argb1555.tga", "Bgra8")]
    [InlineData("tga/rgba32-rle.tga", "Bgra8")]
    [InlineData("tga/gray8.tga", "Gray8")]
    [InlineData("tga/gray-alpha16.tga", "GrayAlpha8")]
    [InlineData("tga/pal8.tga", "Bgr8")]
    public void NaturalTypes(string file, string type)
    {
        using Raster raster = Raster.Load(SimpleCorpus.Read(file));
        Assert.Equal(type, raster.GetType().GetGenericArguments()[0].Name);
    }

    [Fact]
    public void ExtensionAreaBecomesMetadata()
    {
        using Raster raster = Raster.Load(SimpleCorpus.Read("tga/footer-alpha-straight.tga"));
        Assert.Contains(new TextEntry("Author", "Egami tests"), raster.Metadata.Text);
        Assert.Contains(new TextEntry("Comment", "Made by tools/make-simple-references.py"), raster.Metadata.Text);
        TgaDetails details = (TgaDetails)raster.Metadata.FormatDetails!;
        Assert.Equal(TgaAlphaType.Straight, details.AlphaType);
        Assert.Equal(TgaOrigin.BottomLeft, details.Origin);
        Assert.Equal(8, details.AttributeBits);
    }

    [Fact]
    public void ImageIdIsKept()
    {
        byte[] data = SimpleCorpus.Read("tga/rgb24.tga");
        byte[] id = "hello"u8.ToArray();
        byte[] withId = [(byte)id.Length, .. data.AsSpan(1, 17), .. id, .. data.AsSpan(18)];
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(withId);
        using Raster<Rgba8> plain = Raster.Load<Rgba8>(data);
        Assert.Equal(id, ((TgaDetails)raster.Metadata.FormatDetails!).ImageId.ToArray());
        Assert.Equal(Bytes(plain), Bytes(raster));
    }

    /// <summary>No file of another format is taken for TGA, and no random 18-byte header passes the check.</summary>
    [Fact]
    public void OnlyRealTgaFilesAreDetected()
    {
        string images = SimpleCorpus.Images;
        int checkedFiles = 0;
        foreach (string path in Directory.EnumerateFiles(images, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(images, path).Replace('\\', '/');
            if (relative.StartsWith("tga/", StringComparison.Ordinal) || relative.StartsWith("fuzz/", StringComparison.Ordinal))
            {
                continue;
            }

            byte[] data = File.ReadAllBytes(path);
            if (data.Length >= 18 && TgaFormat.Info.Matches(data.AsSpan(0, 18)))
            {
                Assert.Fail($"{relative} looks like TGA.");
            }

            checkedFiles++;
        }

        Assert.True(checkedFiles > 200);
        Random random = new(18);
        byte[] header = new byte[18];
        int accepted = 0;
        for (int i = 0; i < 1_000_000; i++)
        {
            random.NextBytes(header);
            accepted += TgaFormat.Info.Matches(header) ? 1 : 0;
        }

        output.WriteLine($"{accepted} of 1,000,000 random headers accepted");
        Assert.Equal(0, accepted);
    }

    [Theory]
    [InlineData("tga/rgb24.tga")]
    [InlineData("tga/rgba32-rle.tga")]
    [InlineData("tga/pal8-rle.tga")]
    [InlineData("tga/gray8-rle.tga")]
    public void TruncatedFilesKeepRowsWithAWarning(string file)
    {
        byte[] data = SimpleCorpus.Read(file);
        ReadOnlyMemory<byte> cut = data.AsMemory(0, data.Length / 2);
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(cut, options: new ReaderOptions { ReadMetadata = false });
        Assert.NotEmpty(raster.Metadata.Warnings);
        using Raster<Rgba8> full = Raster.Load<Rgba8>(data);
        Assert.True(full.Row(full.Height - 1).SequenceEqual(raster.Row(raster.Height - 1)));
        Assert.Throws<CorruptDataException>(() => Raster.Load(cut, options: new ReaderOptions { Recovery = RecoveryMode.Strict }));
    }

    [Fact]
    public void RunPastTheEndOfTheImageIsAWarning()
    {
        // A 2×1 run-length image whose one packet repeats a pixel 4 times.
        byte[] data = [0, 0, 10, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 1, 0, 24, 0, 0x83, 1, 2, 3];
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(data);
        Assert.Single(raster.Metadata.Warnings);
        Assert.Equal(new Rgba8(3, 2, 1, 255), raster.Row(0)[1]);
        Assert.Throws<CorruptDataException>(() => Raster.Load(data, options: new ReaderOptions { Recovery = RecoveryMode.Strict }));
    }

    [Fact]
    public void HuffmanTypesAreUnsupported()
    {
        byte[] data = [.. SimpleCorpus.Read("tga/pal8.tga")];
        data[2] = 32;
        Assert.Throws<UnsupportedFormatException>(() => Raster.Load(data));
    }

    private static int Largest(byte[] expected, byte[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        int largest = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            largest = Math.Max(largest, Math.Abs(expected[i] - actual[i]));
        }

        return largest;
    }

    private static byte[] Bytes<TPixel>(Raster<TPixel> raster)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        List<byte> bytes = [];
        for (int y = 0; y < raster.Height; y++)
        {
            bytes.AddRange(MemoryMarshal.AsBytes(raster.Row(y)).ToArray());
        }

        return [.. bytes];
    }

    private sealed class ForwardOnlyStream(byte[] data) : MemoryStream(data, writable: false)
    {
        public override bool CanSeek => false;

        public override long Position { get => base.Position; set => throw new NotSupportedException(); }

        public override long Length => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();
    }
}
