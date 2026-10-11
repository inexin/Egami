using System.IO.Compression;

using Egami.Formats;
using Egami.Formats.Png;
using Egami.Pixels;

namespace Egami.Tests.Formats.Png;

/// <summary>
/// Phase 2.4.1 tasks 2 and 3: the banded zlib stream decodes to its input, and PngWriterOptions.ParallelCompression writes the
/// same bytes at every thread count, round trips exactly, and changes nothing below its threshold or when off.
/// </summary>
public class PngParallelTests
{
    private static readonly ReaderOptions Strict = new() { Recovery = RecoveryMode.Strict };

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void BandsJoinIntoOneZlibStream(int threads)
    {
        Random random = new(threads);
        byte[] data = new byte[300_000];
        random.NextBytes(data.AsSpan(0, 100_000));
        data.AsSpan(200_000, 50_000).Fill(7);
        foreach (int level in new[] { 0, 1, 5, 9 })
        {
            using MemoryStream stream = new();
            using (ParallelZLibWriter writer = new(stream, new ZLibCompressionOptions { CompressionLevel = level }, threads))
            {
                for (int at = 0; at < data.Length; at += 40_000)
                {
                    int start = at;
                    int length = Math.Min(40_000, data.Length - at);
                    writer.Add(length, buffer =>
                    {
                        data.AsSpan(start, length).CopyTo(buffer);
                        return length;
                    });
                }

                writer.Finish();
            }

            byte[] compressed = stream.ToArray();
            Assert.Equal(ParallelZLibWriter.Header(new ZLibCompressionOptions { CompressionLevel = level }), compressed[..2]);
            using ZLibStream inflate = new(new MemoryStream(compressed), CompressionMode.Decompress);
            using MemoryStream inflated = new();
            inflate.CopyTo(inflated);
            Assert.Equal(data, inflated.ToArray());
        }
    }

    [Fact]
    public void Adler32CombinesPieces()
    {
        Random random = new(9);
        byte[] data = new byte[70_000];
        random.NextBytes(data);
        uint whole = Adler32.Update(1, data);
        foreach (int split in new[] { 0, 1, 5551, 5552, 65521, 65522, 69_999, 70_000 })
        {
            uint first = Adler32.Update(1, data.AsSpan(0, split));
            uint second = Adler32.Update(1, data.AsSpan(split));
            Assert.Equal(whole, Adler32.Combine(first, second, data.Length - split));
        }

        byte[] ones = new byte[200_000];
        Array.Fill(ones, (byte)0xFF);
        Assert.Equal(Adler32.Update(1, ones), Adler32.Combine(Adler32.Update(1, ones.AsSpan(0, 131_072)), Adler32.Update(1, ones.AsSpan(131_072)), 200_000 - 131_072));
    }

    /// <summary>The same file at 1, 2, 4 and all cores, for plain and interlaced images, and it reads back exactly.</summary>
    [Theory]
    [InlineData(false, PngFilterMode.Adaptive)]
    [InlineData(false, PngFilterMode.Paeth)]
    [InlineData(true, PngFilterMode.Adaptive)]
    public void SameBytesAtEveryThreadCount(bool interlace, PngFilterMode filter)
    {
        byte[]? first = null;
        foreach (int threads in new[] { 1, 2, 4, Environment.ProcessorCount })
        {
            using Raster<Rgba8> raster = Photo(150, 97, threads);
            byte[] file = PngWriterTests.Save(raster, Banded(interlace, filter));
            first ??= file;
            Assert.Equal(first, file);
        }

        using Raster<Rgba8> source = Photo(150, 97, 1);
        using Raster<Rgba8> loaded = Raster.Load<Rgba8>(first!, options: Strict);
        for (int y = 0; y < source.Height; y++)
        {
            Assert.True(source.Row(y).SequenceEqual(loaded.Row(y)), $"row {y}");
        }

        byte[] single = PngWriterTests.Save(source, new PngWriterOptions { Interlace = interlace, Filter = filter });
        Assert.NotEqual(single, first);
    }

    [Theory]
    [MemberData(nameof(PngWriterTests.AllForms), MemberType = typeof(PngWriterTests))]
    public void EveryColorTypeAndBitDepthRoundTrips(PngColorType colorType, int depth)
    {
        PngBuilder source = PngReaderTests.Build(colorType, depth, false, colorType == PngColorType.Palette, 61, 37, seed: depth);
        using Raster<Rgba16> original = Raster.Load<Rgba16>(source.Build());
        foreach (bool interlace in new[] { false, true })
        {
            PngWriterOptions options = new()
            {
                ColorType = colorType,
                BitDepth = depth,
                Interlace = interlace,
                ParallelCompression = true,
                ParallelThreshold = 0,
                ParallelBandSize = 50,
            };
            using Raster<Rgba16> loaded = Raster.Load<Rgba16>(PngWriterTests.Save(original, options), options: Strict);
            for (int y = 0; y < original.Height; y++)
            {
                Assert.True(original.Row(y).SequenceEqual(loaded.Row(y)), $"{colorType} {depth} interlace {interlace} row {y}");
            }
        }
    }

    [Fact]
    public void ApngFramesCompressInBands()
    {
        using Raster<Rgba8> anim = Photo(80, 60, 4);
        Frame<Rgba8> second = anim.Frames.Add(anim.Frames[0]);
        second.Row(10).Slice(5, 50).Fill(Rgba8.Red);
        byte[] banded = PngWriterTests.Save(anim, Banded(false, PngFilterMode.Adaptive));
        Assert.NotEqual(PngWriterTests.Save(anim), banded);
        using Raster<Rgba8> again = Raster.Load<Rgba8>(banded, options: Strict);
        Assert.Equal(2, again.FrameCount);
        for (int i = 0; i < 2; i++)
        {
            for (int y = 0; y < anim.Height; y++)
            {
                Assert.True(anim.Frames[i].Row(y).SequenceEqual(again.Frames[i].Row(y)), $"frame {i} row {y}");
            }
        }
    }

    /// <summary>Below the threshold, and when off, the file is byte for byte what 1.x writes.</summary>
    [Fact]
    public void NothingChangesBelowTheThresholdOrWhenOff()
    {
        using Raster<Rgba8> raster = Photo(200, 150, 4);
        byte[] plain = PngWriterTests.Save(raster);
        Assert.Equal(plain, PngWriterTests.Save(raster, new PngWriterOptions { ParallelCompression = true }));
        Assert.Equal(plain, PngWriterTests.Save(raster, new PngWriterOptions { ParallelCompression = false, ParallelThreshold = 0, ParallelBandSize = 100 }));
    }

    [Fact]
    public void StreamedEncodeWritesWhatSaveWrites()
    {
        using Raster<Rgba8> original = Photo(120, 90, 4);
        PngWriterOptions options = Banded(false, PngFilterMode.Adaptive);
        byte[] saved = PngWriterTests.Save(original, options);
        using MemoryStream streamed = new();
        using (RasterEncoder<Rgba8> encoder = RasterEncoder.Create<Rgba8>(streamed, PngFormat.Info, original.Width, original.Height, options, context: original.Context))
        {
            encoder.WriteRows(original);
            encoder.Complete();
        }

        Assert.Equal(saved, streamed.ToArray());
    }

    /// <summary>Options that split even a small image into many bands of about 4 KB.</summary>
    private static PngWriterOptions Banded(bool interlace, PngFilterMode filter)
    {
        return new PngWriterOptions { Interlace = interlace, Filter = filter, ParallelCompression = true, ParallelThreshold = 0, ParallelBandSize = 4096 };
    }

    /// <summary>A smooth image with noise, so filters and matches both matter, on a context with the given thread count.</summary>
    private static Raster<Rgba8> Photo(int width, int height, int threads)
    {
        Random random = new(42);
        Raster<Rgba8> raster = Raster.Create<Rgba8>(width, height, EgamiContext.Default with { MaxParallelism = threads });
        for (int y = 0; y < height; y++)
        {
            Span<Rgba8> row = raster.Row(y);
            for (int x = 0; x < width; x++)
            {
                row[x] = new Rgba8((byte)(x + random.Next(4)), (byte)(y * 2), (byte)((x + y) / 2), (byte)(255 - random.Next(2)));
            }
        }

        return raster;
    }
}
