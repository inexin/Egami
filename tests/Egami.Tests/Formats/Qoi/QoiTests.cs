using System.Buffers.Binary;
using System.Runtime.InteropServices;

using Egami.Formats;
using Egami.Formats.Qoi;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Tests.Formats.Qoi;

public class QoiTests
{
    public static TheoryData<string> Files() => [.. SimpleCorpus.Files("qoi")];

    /// <summary>Pillow, ImageMagick, qoiconv (when built) and the script's own pixels all agree on every file.</summary>
    [Theory]
    [MemberData(nameof(Files))]
    public void DecodesLikeEveryReference(string file)
    {
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(SimpleCorpus.Read(file));
        byte[] actual = Bytes(raster);
        foreach (string tool in new[] { "expected", "pillow", "magick", "qoiconv" })
        {
            if (SimpleCorpus.Pixels(file, tool) is byte[] reference)
            {
                Assert.True(reference.AsSpan().SequenceEqual(actual), $"{tool} differs");
            }
        }

        Assert.Empty(raster.Metadata.Warnings);
    }

    /// <summary>
    /// Every corpus file was written by an encoder making the reference encoder's choices (ImageMagick's, qoiconv, or the script's
    /// copy of the specification's algorithm), so decoding and writing again gives the same bytes.
    /// </summary>
    [Theory]
    [MemberData(nameof(Files))]
    public void WritesTheSameBytesAsTheReferenceEncoder(string file)
    {
        byte[] original = SimpleCorpus.Read(file);
        using Raster raster = Raster.Load(original);
        using MemoryStream output = new();
        raster.Save(output, QoiFormat.Info, new QoiWriterOptions { IncludeAlpha = original[12] == 4 });
        Assert.Equal(original, output.ToArray());
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void ProbeReadsTheHeader(string file)
    {
        byte[] data = SimpleCorpus.Read(file);
        RasterInfo info = Raster.Probe(data);
        Assert.Equal(QoiFormat.Info, info.Format);
        Assert.Equal((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4)), info.Width);
        Assert.Equal((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8)), info.Height);
        Assert.Equal(data[12] == 4 ? Rgba8.Layout : Rgb8.Layout, info.Layout);
        Assert.Equal(1, info.FrameCount);
    }

    [Fact]
    public void NaturalTypeFollowsTheChannelCount()
    {
        using Raster rgb = Raster.Load(SimpleCorpus.Read("qoi/rgb-magick.qoi"));
        using Raster rgba = Raster.Load(SimpleCorpus.Read("qoi/rgba-magick.qoi"));
        Assert.Equal(Rgb8.Layout, rgb.Layout);
        Assert.Equal(Rgba8.Layout, rgba.Layout);
        Assert.Equal("QOI", rgb.Metadata.FormatName);
        Assert.Equal(new QoiDetails { Channels = 3 }.Channels, ((QoiDetails)rgb.Metadata.FormatDetails!).Channels);
    }

    [Fact]
    public void LinearColorspaceRoundTrips()
    {
        byte[] original = SimpleCorpus.Read("qoi/linear-rgba.qoi");
        using Raster raster = Raster.Load(original);
        Assert.Equal(ColorSpaceHint.Linear, raster.Metadata.ColorSpace);
        Assert.True(((QoiDetails)raster.Metadata.FormatDetails!).Linear);
        using MemoryStream output = new();
        raster.Save(output, QoiFormat.Info);
        Assert.Equal(1, output.ToArray()[13]);
    }

    [Fact]
    public void AutoWritesThreeChannelsOnlyWhenOpaque()
    {
        using Raster<Rgba8> opaque = Raster.Load<Rgba8>(SimpleCorpus.Read("qoi/rgb-magick.qoi"));
        using Raster<Rgba8> translucent = Raster.Load<Rgba8>(SimpleCorpus.Read("qoi/rgba-magick.qoi"));
        Assert.Equal(3, Save(opaque)[12]);
        Assert.Equal(4, Save(translucent)[12]);
        Assert.Equal(3, Save(translucent, new QoiWriterOptions { IncludeAlpha = false })[12]);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(7, 3)]
    [InlineData(100, 1)]
    [InlineData(1, 100)]
    public void RoundTripsEveryPixelType(int width, int height)
    {
        Random random = new(width * 1000 + height);
        using Raster<Rgba8> source = Raster.Create<Rgba8>(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<Rgba8> row = source.Row(y);
            for (int x = 0; x < width; x++)
            {
                // Mostly small steps, some repeats and some jumps, so every chunk type appears.
                int kind = random.Next(4);
                row[x] = kind == 0 && x > 0 ? row[x - 1] : new Rgba8((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)(kind == 1 ? random.Next(256) : 255));
            }
        }

        byte[] data = Save(source);
        using Raster<Rgba8> back = Raster.Load<Rgba8>(data);
        Assert.Equal(Bytes(source), Bytes(back));
        using Raster<Bgra8> bgra = Raster.Load<Bgra8>(data);
        using Raster<Rgba8> again = bgra.As<Rgba8>();
        Assert.Equal(Bytes(source), Bytes(again));
    }

    [Fact]
    public void LongRunsCrossRowsAndTheSixtyTwoLimit()
    {
        using Raster<Rgba8> source = Raster.Create<Rgba8>(13, 11);
        foreach (int y in Enumerable.Range(0, 11))
        {
            source.Row(y).Fill(new Rgba8(1, 2, 3, 255));
        }

        source.Row(10)[12] = new Rgba8(9, 9, 9, 255);
        byte[] data = Save(source);
        using Raster<Rgba8> back = Raster.Load<Rgba8>(data);
        Assert.Equal(Bytes(source), Bytes(back));
    }

    [Fact]
    public void TruncatedFileKeepsRowsWithAWarning()
    {
        byte[] data = SimpleCorpus.Read("qoi/rgba-magick.qoi");
        byte[] cut = data[..(data.Length / 2)];
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(cut);
        Assert.Single(raster.Metadata.Warnings);
        using Raster<Rgba8> full = Raster.Load<Rgba8>(data);
        Assert.True(full.Row(0).SequenceEqual(raster.Row(0)));
        Assert.True(raster.Row(raster.Height - 1).ToArray().All(p => p.Equals(default(Rgba8))));
        Assert.Throws<CorruptDataException>(() => Raster.Load<Rgba8>(cut, options: new ReaderOptions { Recovery = RecoveryMode.Strict }));
    }

    [Fact]
    public void MissingEndMarkerIsAWarning()
    {
        byte[] data = SimpleCorpus.Read("qoi/rgb-magick.qoi");
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(data.AsMemory(..^8));
        Assert.Single(raster.Metadata.Warnings);
        Assert.Throws<CorruptDataException>(() => Raster.Load<Rgba8>(data.AsMemory(..^8), options: new ReaderOptions { Recovery = RecoveryMode.Strict }));
    }

    [Theory]
    [InlineData(12, 5)]
    [InlineData(13, 2)]
    [InlineData(4, 0)]
    public void BadHeadersThrow(int offset, byte value)
    {
        byte[] data = SimpleCorpus.Read("qoi/rgb-magick.qoi");
        if (offset == 4)
        {
            data.AsSpan(4, 4).Clear();
        }
        else
        {
            data[offset] = value;
        }

        Assert.Throws<CorruptDataException>(() => Raster.Load(data));
    }

    [Fact]
    public void HugeSizeIsRejectedBeforeAllocating()
    {
        byte[] data = [.. "qoif"u8, 0x7F, 0xFF, 0xFF, 0xFF, 0x7F, 0xFF, 0xFF, 0xFF, 4, 0];
        Assert.Throws<LimitExceededException>(() => Raster.Load(data));
    }

    private static byte[] Save<TPixel>(Raster<TPixel> raster, QoiWriterOptions? options = null)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using MemoryStream output = new();
        raster.Save(output, QoiFormat.Info, options ?? QoiWriterOptions.Default);
        return output.ToArray();
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
}
