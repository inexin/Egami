using System.Buffers.Binary;

using Egami.Formats;
using Egami.Formats.Webp;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Tests.Formats.Webp;

/// <summary>The RIFF container: probing, metadata chunks both ways, kept chunks, resolution through EXIF, and damaged containers.</summary>
public class WebpContainerTests
{
    private static readonly string IccPath = Path.Combine(AppContext.BaseDirectory, "images", "icc", "compact", "DisplayP3-v4.icc");

    [Fact]
    public void MetadataChunksAreRead()
    {
        using Raster raster = Raster.Load(WebpCorpus.Read("meta-all.webp"));
        Assert.Equal(File.ReadAllBytes(IccPath), raster.Metadata.IccProfile.ToArray());
        Assert.NotNull(raster.Metadata.Exif);
        Assert.Contains("Egami", System.Text.Encoding.UTF8.GetString(raster.Metadata.Xmp.Span));
        WebpDetails details = Assert.IsType<WebpDetails>(raster.Metadata.FormatDetails);
        Assert.True(details.Extended);
        Assert.True(details.Lossless);
        Assert.Empty(raster.Metadata.Warnings);
    }

    [Fact]
    public void ExifWithTheExifPrefixIsAccepted()
    {
        using Raster plain = Raster.Load(WebpCorpus.Read("meta-exif.webp"));
        Assert.NotNull(plain.Metadata.Exif);
        Assert.False(plain.Metadata.Exif!.IsDamaged);
        RasterInfo prefixed = Raster.Probe(WebpCorpus.Read("meta-exif-prefixed.webp"));
        Assert.True(prefixed.HasMetadata);
    }

    [Theory]
    [InlineData("meta-icc-lossless.webp")]
    [InlineData("meta-exif.webp")]
    [InlineData("meta-all.webp")]
    public void MetadataRoundTripsByteForByte(string file)
    {
        using Raster raster = Raster.Load(WebpCorpus.Read(file));
        using MemoryStream saved = new();
        raster.Save(saved, WebpFormat.Info, WebpWriterOptions.Default);
        using Raster back = Raster.Load(saved.ToArray());
        Assert.Equal(raster.Metadata.IccProfile.ToArray(), back.Metadata.IccProfile.ToArray());
        Assert.Equal(raster.Metadata.Exif?.ToBytes().ToArray(), back.Metadata.Exif?.ToBytes().ToArray());
        Assert.Equal(raster.Metadata.Xmp.ToArray(), back.Metadata.Xmp.ToArray());
        Assert.True(Assert.IsType<WebpDetails>(back.Metadata.FormatDetails).Lossless);
    }

    [Fact]
    public void StrippedFilesUseTheSimpleContainer()
    {
        using Raster raster = Raster.Load(WebpCorpus.Read("meta-all.webp"));
        using MemoryStream saved = new();
        raster.Save(saved, WebpFormat.Info, new WebpWriterOptions { StripMetadata = true });
        byte[] file = saved.ToArray();
        Assert.Equal("VP8L", System.Text.Encoding.ASCII.GetString(file, 12, 4));
        Assert.Equal(file.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(4)));
    }

    [Fact]
    public void ResolutionGoesIntoExifAndComesBack()
    {
        using Raster<Rgba8> raster = Raster.Create(4, 3, Rgba8.Red);
        raster.Metadata.Resolution = new Resolution(300, 150, ResolutionUnit.PerInch);
        byte[] file = Save(raster);
        Assert.Equal("VP8X", System.Text.Encoding.ASCII.GetString(file, 12, 4));
        using Raster back = Raster.Load(file);
        Assert.Equal(new Resolution(300, 150, ResolutionUnit.PerInch), back.Metadata.Resolution);
        Assert.NotNull(back.Metadata.Exif);

        raster.Metadata.Resolution = new Resolution(118.11, 118.11, ResolutionUnit.PerCentimeter);
        using Raster cm = Raster.Load(Save(raster));
        Assert.Equal(ResolutionUnit.PerCentimeter, cm.Metadata.Resolution!.Value.Unit);
        Assert.Equal(118.11, cm.Metadata.Resolution!.Value.X, 3);
    }

    [Fact]
    public void OddSizedChunksArePadded()
    {
        using Raster<Rgba8> raster = Raster.Create(5, 5, Rgba8.Blue);
        raster.Metadata.Xmp = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>"u8.ToArray();
        Assert.Equal(1, raster.Metadata.Xmp.Length % 2);
        byte[] file = Save(raster);
        Assert.Equal(0, file.Length % 2);
        Assert.Equal(file.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(4)));
        WalkChunks(file, (type, size, at) => Assert.True(at + 8 + size + (size & 1) <= file.Length, $"{type} overruns the file."));
        using Raster back = Raster.Load(file);
        Assert.Equal(raster.Metadata.Xmp.ToArray(), back.Metadata.Xmp.ToArray());
    }

    [Fact]
    public void UnknownChunksAreKeptInPlace()
    {
        byte[] original = Save(Solid(6, 4));
        byte[] file = InsertChunk(MakeExtended(original), "ZZZZ"u8, [1, 2, 3], afterImage: false);
        file = InsertChunk(file, "YYYY"u8, [9, 8], afterImage: true);
        using Raster raster = Raster.Load(file);
        WebpDetails details = Assert.IsType<WebpDetails>(raster.Metadata.FormatDetails);
        Assert.Equal(2, details.OtherChunks.Count);
        Assert.Equal(("ZZZZ", WebpChunkPosition.BeforeImageData), (details.OtherChunks[0].Type, details.OtherChunks[0].Position));
        Assert.Equal(("YYYY", WebpChunkPosition.AfterImageData), (details.OtherChunks[1].Type, details.OtherChunks[1].Position));

        byte[] saved = Save(raster);
        List<string> order = [];
        WalkChunks(saved, (type, _, _) => order.Add(type));
        Assert.Equal(["VP8X", "ZZZZ", "VP8L", "YYYY"], order);
    }

    [Fact]
    public void ANonWebpRiffIsNotRecognized()
    {
        byte[] wave = [.. "RIFF"u8, 4, 0, 0, 0, .. "WAVE"u8];
        Assert.Throws<UnsupportedFormatException>(() => Raster.Probe(wave));
    }

    [Fact]
    public void AFirstChunkThatIsNoImageThrows()
    {
        byte[] file = Save(Solid(3, 3));
        "JUNK"u8.CopyTo(file.AsSpan(12));
        CorruptDataException e = Assert.Throws<CorruptDataException>(() => Raster.Load(file));
        Assert.Contains("JUNK", e.Message);
    }

    [Fact]
    public void ACanvasThatDisagreesWithTheImageThrows()
    {
        byte[] file = MakeExtended(Save(Solid(6, 4)));
        file[24] = 9;
        Assert.Throws<CorruptDataException>(() => Raster.Load(file));
    }

    [Fact]
    public void ARiffSizePastTheEndWarnsOrThrows()
    {
        byte[] file = Save(Solid(8, 8));
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(4), file.Length + 100);
        using (Raster lenient = Raster.Load(file))
        {
            Assert.NotEmpty(lenient.Metadata.Warnings);
        }

        Assert.Throws<CorruptDataException>(() => Raster.Load(file, options: new ReaderOptions { Recovery = RecoveryMode.Strict }));
    }

    [Fact]
    public void TruncatedLosslessDataKeepsTheRowsDecoded()
    {
        byte[] whole = WebpCorpus.Read("lossless-z6.webp");
        byte[] file = whole[..(whole.Length * 2 / 3)];
        using Raster<Rgba8> full = Raster.Load<Rgba8>(whole);
        using Raster<Rgba8> partial = Raster.Load<Rgba8>(file);
        Assert.NotEmpty(partial.Metadata.Warnings);
        Assert.True(full.Row(0).SequenceEqual(partial.Row(0)));
        Assert.Equal(default, partial.Row(partial.Height - 1)[0]);
        CorruptDataException e = Assert.Throws<CorruptDataException>(() => Raster.Load(file, options: new ReaderOptions { Recovery = RecoveryMode.Strict }));
        Assert.True(e.IsTruncation);
    }

    [Fact]
    public void AChunkSizePastTheRiffDataIsCaught()
    {
        byte[] file = Save(Solid(8, 8));
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(16), int.MaxValue);
        Assert.Throws<CorruptDataException>(() => Raster.Load(file, options: new ReaderOptions { Recovery = RecoveryMode.Strict }));
        using Raster lenient = Raster.Load(file);
        Assert.NotEmpty(lenient.Metadata.Warnings);
    }

    [Fact]
    public void LimitsAreCheckedBeforePixels()
    {
        byte[] file = Save(Solid(64, 64));
        EgamiContext small = EgamiContext.Default with { Limits = SafetyLimits.Default with { MaxWidth = 32 } };
        Assert.Throws<LimitExceededException>(() => Raster.Load(file, small));
    }

    [Fact]
    public void OversizedLossyRastersAreRefused()
    {
        using Raster<Gray8> raster = Raster.Create<Gray8>(16384, 1);
        using MemoryStream output = new();
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Save(output, WebpFormat.Info, new WebpWriterOptions { Lossless = false }));
    }

    [Fact]
    public void OversizedRastersAreRefusedBeforeAnyWork()
    {
        using Raster<Gray8> raster = Raster.Create<Gray8>(16385, 1);
        using MemoryStream output = new();
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Save(output, WebpFormat.Info, new WebpWriterOptions { Lossless = true }));
        Assert.Equal(0, output.Length);
    }

    private static Raster<Rgba8> Solid(int width, int height) => WebpTestImages.Make(width, height, (x, y) => new Rgba8((byte)(x * 20), (byte)(y * 30), 90, 255));

    private static byte[] Save(Raster raster)
    {
        using MemoryStream output = new();
        raster.Save(output, WebpFormat.Info, new WebpWriterOptions { Lossless = true });
        return output.ToArray();
    }

    /// <summary>Turns a simple-format VP8L file into an extended one by inserting a VP8X chunk with the image's size.</summary>
    private static byte[] MakeExtended(byte[] simple)
    {
        using Raster raster = Raster.Load(simple);
        byte[] vp8x = new byte[18];
        "VP8X"u8.CopyTo(vp8x);
        vp8x[4] = 10;
        vp8x[12] = (byte)(raster.Width - 1);
        vp8x[13] = (byte)((raster.Width - 1) >> 8);
        vp8x[15] = (byte)(raster.Height - 1);
        vp8x[16] = (byte)((raster.Height - 1) >> 8);
        byte[] file = [.. simple[..12], .. vp8x, .. simple[12..]];
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(4), file.Length - 8);
        return file;
    }

    private static byte[] InsertChunk(byte[] file, ReadOnlySpan<byte> type, byte[] data, bool afterImage)
    {
        int at = 12;
        while (at < file.Length)
        {
            string t = System.Text.Encoding.ASCII.GetString(file, at, 4);
            int size = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(at + 4));
            if (t is "VP8L" or "VP8 ")
            {
                if (afterImage)
                {
                    at += 8 + size + (size & 1);
                }

                break;
            }

            at += 8 + size + (size & 1);
        }

        byte[] chunk = [.. type, (byte)data.Length, 0, 0, 0, .. data, .. (data.Length % 2 == 1 ? new byte[1] : [])];
        byte[] result = [.. file[..at], .. chunk, .. file[at..]];
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), result.Length - 8);
        return result;
    }

    private static void WalkChunks(byte[] file, Action<string, int, int> visit)
    {
        for (int at = 12; at + 8 <= file.Length;)
        {
            int size = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(at + 4));
            visit(System.Text.Encoding.ASCII.GetString(file, at, 4), size, at);
            at += 8 + size + (size & 1);
        }
    }
}
