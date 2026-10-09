using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;

using Egami.Formats;
using Egami.Formats.Png;
using Egami.Metadata;
using Egami.Pixels;
using Egami.Tests.Formats.Gif;

namespace Egami.Tests.Formats.Png;

public class ApngDecodeTests(ITestOutputHelper output)
{
    public static TheoryData<string> Files() => [.. AnimCorpus.Files("apng")];

    /// <summary>
    /// Every frame matches Pillow's composite exactly, except pixels with partial alpha. Pillow blends Over frames with an alpha
    /// mask that ignores the canvas's own alpha, so a half-transparent pixel over a transparent one comes out darker and more
    /// transparent than source-over gives; <see cref="OverBlendsWithSourceOver"/> checks those by hand. Pillow also lists a default
    /// image that is not part of the animation as frame 0, which browsers and the specification skip.
    /// </summary>
    [Theory]
    [MemberData(nameof(Files))]
    public void EveryFrameMatchesPillow(string file)
    {
        byte[][] expected = PillowFrames(file);
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(AnimCorpus.Read(file));
        Assert.Equal(expected.Length, raster.FrameCount);
        for (int i = 0; i < expected.Length; i++)
        {
            byte[] actual = GifDecodeTests.Bytes(raster.Frames[i]);
            byte[] reference = expected[i];
            int worst = 0;
            int differing = 0;
            for (int p = 0; p < actual.Length; p += 4)
            {
                if ((reference[p + 3] == 0 && actual[p + 3] == 0) || reference[p + 3] is > 0 and < 255 || actual[p + 3] is > 0 and < 255)
                {
                    continue;
                }

                int difference = 0;
                for (int c = 0; c < 4; c++)
                {
                    difference = Math.Max(difference, Math.Abs(reference[p + c] - actual[p + c]));
                }

                Assert.True(difference == 0, $"frame {i} pixel {p / 4}: {difference} levels");
                differing += difference > 0 ? 1 : 0;
                worst = Math.Max(worst, difference);
            }

            output.WriteLine($"frame {i}: {differing} pixels within {worst} level");
        }

        Assert.Empty(raster.Metadata.Warnings);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void ProbeReadsFramesAndPlaysFromAcTL(string file)
    {
        JsonElement entry = AnimCorpus.Entry(file);
        RasterInfo info = Raster.Probe(AnimCorpus.Read(file));
        Assert.Equal(PngFormat.Info, info.Format);
        Assert.Equal(PillowFrames(file).Length, info.FrameCount);
        Assert.Equal(entry.GetProperty("loop").GetInt32(), info.LoopCount);
        Assert.Equal(Rgba8.Layout, info.Layout);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void DelaysAndPlaysMatchTheManifest(string file)
    {
        JsonElement entry = AnimCorpus.Entry(file);
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(AnimCorpus.Read(file));
        int[] delays = [.. entry.GetProperty("delays").EnumerateArray().Select(d => (int)d.GetDouble())];
        int skip = file.EndsWith("default-image.png", StringComparison.Ordinal) ? 1 : 0;
        Assert.Equal(delays[skip..], raster.Frames.Select(f => (int)Math.Round(f.Metadata.Delay.TotalMilliseconds)));
        Assert.Equal(entry.GetProperty("loop").GetInt32(), raster.Metadata.LoopCount);
        PngDetails details = Assert.IsType<PngDetails>(raster.Metadata.FormatDetails);
        Assert.True(details.Animated);
        Assert.Equal(skip == 0, details.DefaultImageIsFrame);
        Assert.DoesNotContain(details.OtherChunks, c => c.Type is "acTL" or "fcTL" or "fdAT");
    }

    [Fact]
    public void DefaultImageOutsideTheAnimationIsSkippedEvenForTheFirstFrame()
    {
        // Pillow lists the fallback image as a frame; browsers and the APNG specification do not.
        byte[] file = AnimCorpus.Read("apng/default-image.png");
        byte[][] pillow = AnimCorpus.Frames("apng/default-image.png", "pillow")!;
        using Raster<Rgba8> first = Raster.Load<Rgba8>(file, options: new ReaderOptions { Frames = ..1 });
        Assert.Equal(1, first.FrameCount);
        Assert.Equal(pillow[1], GifDecodeTests.Bytes(first.Frames[0]));
        Assert.NotEqual(new Rgba8(255, 0, 255, 255), first.GetPixel(0, 0));
    }

    [Fact]
    public void FrameRangesKeepOnlyTheChosenFrames()
    {
        byte[] file = AnimCorpus.Read("apng/sprite-over-previous.png");
        byte[][] expected = AnimCorpus.Frames("apng/sprite-source-previous.png", "pillow")!;
        file = AnimCorpus.Read("apng/sprite-source-previous.png");
        using Raster<Rgba8> middle = Raster.Load<Rgba8>(file, options: new ReaderOptions { Frames = 2..4 });
        Assert.Equal(2, middle.FrameCount);
        Assert.Equal(expected[2], GifDecodeTests.Bytes(middle.Frames[0]));
        using Raster<Rgba8> last = Raster.Load<Rgba8>(file, options: new ReaderOptions { Frames = ^1.. });
        Assert.Equal(expected[^1], GifDecodeTests.Bytes(last.Frames[0]));
    }

    [Fact]
    public void OverBlendsWithSourceOver()
    {
        // Frame 1 is half-transparent green over an opaque red pixel and over a transparent one.
        byte[] first = [255, 0, 0, 255, 0, 0, 0, 0];
        byte[] second = [0, 255, 0, 128, 0, 255, 0, 128];
        byte[] file = Apng.Build(2, 1, 8, 6, [(new Rect(0, 0, 2, 1), Apng.Rows8(first, 8, 1), 0, 0), (new Rect(0, 0, 2, 1), Apng.Rows8(second, 8, 1), 0, 1)]);
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(file);
        Assert.Equal(new Rgba8(127, 128, 0, 255), raster.Frames[1].Row(0)[0]);
        Assert.Equal(new Rgba8(0, 255, 0, 128), raster.Frames[1].Row(0)[1]);
    }

    [Fact]
    public void SixteenBitFramesLoadAsRgba16()
    {
        // Two 2 x 1 frames of 16-bit RGBA; the second is drawn Over the first at half alpha.
        ushort[] first = [1000, 2000, 3000, 65535, 4000, 5000, 6000, 65535];
        ushort[] second = [60000, 50000, 40000, 32768, 0, 0, 0, 0];
        byte[] file = Apng.Build(2, 1, 16, 6, [(new Rect(0, 0, 2, 1), Apng.Rows16(first, 2, 1), 0, 0), (new Rect(0, 0, 2, 1), Apng.Rows16(second, 2, 1), 0, 1)]);
        using Raster raster = Raster.Load(file);
        Raster<Rgba16> wide = Assert.IsType<Raster<Rgba16>>(raster);
        Assert.Equal(2, wide.FrameCount);
        Assert.Equal(new Rgba16(4000, 5000, 6000, 65535), wide.Frames[1].Row(0)[1]);
        Rgba16 blended = wide.Frames[1].Row(0)[0];
        Assert.Equal(65535, blended.A);
        Assert.InRange(blended.R, 30499, 30501);
        Assert.Equal(Rgba16.Layout, Raster.Probe(file).Layout);
    }

    [Fact]
    public void PaletteFramesShareThePalette()
    {
        byte[] file = Apng.Build(3, 2, 8, 3, [(new Rect(0, 0, 3, 2), Apng.Rows8([0, 1, 2, 2, 1, 0], 3, 2), 0, 0), (new Rect(1, 0, 2, 1), Apng.Rows8([2, 2], 2, 1), 0, 0)],
            palette: [255, 0, 0, 0, 255, 0, 0, 0, 255], transparency: [255, 128]);
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(file);
        Assert.Equal(new Rgba8(0, 255, 0, 128), raster.Frames[0].Row(0)[1]);
        Assert.Equal(new Rgba8(0, 0, 255, 255), raster.Frames[1].Row(0)[1]);
        Assert.Equal(new Rgba8(255, 0, 0, 255), raster.Frames[1].Row(0)[0]);
        Assert.Equal(new Rgba8(255, 0, 0, 255), raster.Frames[1].Row(1)[2]);
    }

    [Fact]
    public void SequenceGapsAreCorruptStrictlyAndEndTheAnimationLeniently()
    {
        byte[] file = Apng.Build(2, 2, 8, 6, [(new Rect(0, 0, 2, 2), Apng.Rows8(new byte[16], 8, 2), 0, 0), (new Rect(0, 0, 1, 1), Apng.Rows8(new byte[4], 4, 1), 0, 0), (new Rect(0, 0, 1, 1), Apng.Rows8([9, 9, 9, 255], 4, 1), 0, 0)], sequenceGapAt: 2);
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(file);
        Assert.Equal(2, raster.FrameCount);
        Assert.NotEmpty(raster.Metadata.Warnings);
        Assert.Throws<CorruptDataException>(() => Raster.Load(file, options: new ReaderOptions { Recovery = RecoveryMode.Strict }));
    }

    [Fact]
    public void FrameOutsideTheImageIsCorruptInBothModes()
    {
        byte[] file = Apng.Build(2, 2, 8, 6, [(new Rect(0, 0, 2, 2), Apng.Rows8(new byte[16], 8, 2), 0, 0), (new Rect(1, 1, 2, 1), Apng.Rows8(new byte[8], 8, 1), 0, 0)]);
        Assert.Throws<CorruptDataException>(() => Raster.Load(file));
    }

    [Fact]
    public void ZeroFramesMeansAStillPng()
    {
        byte[] file = Apng.Build(2, 2, 8, 6, [(new Rect(0, 0, 2, 2), Apng.Rows8(new byte[16], 8, 2), 0, 0)], frameCount: 0);
        using Raster raster = Raster.Load(file);
        Assert.Equal(1, raster.FrameCount);
        Assert.NotEmpty(raster.Metadata.Warnings);
    }

    [Fact]
    public void TooManyFramesThrowsLimitExceeded()
    {
        EgamiContext context = EgamiContext.Default with { Limits = SafetyLimits.Default with { MaxFrames = 3 } };
        Assert.Throws<LimitExceededException>(() => Raster.Load(AnimCorpus.Read("apng/pan-rgb.png"), context));
        Assert.Throws<LimitExceededException>(() => Raster.Probe(AnimCorpus.Read("apng/pan-rgb.png"), context));
    }

    private static byte[][] PillowFrames(string file)
    {
        byte[][] frames = AnimCorpus.Frames(file, "pillow")!;
        return file.EndsWith("default-image.png", StringComparison.Ordinal) ? frames[1..] : frames;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void DamagedCopiesFailOnlyWithEgamiExceptions(string file)
    {
        byte[] data = AnimCorpus.Read(file);
        foreach (int length in new[] { data.Length - 12, (data.Length * 3) / 4, data.Length / 2 })
        {
            using (Raster raster = Raster.Load(data.AsMemory(0, length)))
            {
                Assert.NotEmpty(raster.Metadata.Warnings);
            }

            Assert.Throws<CorruptDataException>(() => Raster.Load(data.AsMemory(0, length), options: new ReaderOptions { Recovery = RecoveryMode.Strict }));
        }

        Random random = new(file.Length);
        for (int round = 0; round < 30; round++)
        {
            byte[] copy = (byte[])data.Clone();
            copy[8 + random.Next(copy.Length - 8)] ^= (byte)(1 << random.Next(8));
            try
            {
                using Raster raster = Raster.Load(copy);
            }
            catch (EgamiException)
            {
            }
        }
    }
}

/// <summary>Writes small APNG files by hand, for cases Pillow cannot make.</summary>
internal static class Apng
{
    internal static byte[] Rows8(byte[] samples, int rowBytes, int rows)
    {
        using MemoryStream raw = new();
        for (int y = 0; y < rows; y++)
        {
            raw.WriteByte(0);
            raw.Write(samples, y * rowBytes, rowBytes);
        }

        return raw.ToArray();
    }

    internal static byte[] Rows16(ushort[] samples, int width, int rows)
    {
        byte[] bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(i * 2), samples[i]);
        }

        return Rows8(bytes, bytes.Length / rows, rows);
    }

    /// <summary>Builds an APNG whose first frame is the default image; each frame is (rectangle, filtered rows, disposal, blend).</summary>
    internal static byte[] Build(int width, int height, int depth, int colorType, (Rect Bounds, byte[] Rows, byte Disposal, byte Blend)[] frames,
        byte[]? palette = null, byte[]? transparency = null, int sequenceGapAt = -1, int? frameCount = null)
    {
        using MemoryStream file = new();
        file.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        byte[] ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = (byte)depth;
        ihdr[9] = (byte)colorType;
        Chunk(file, "IHDR", ihdr);
        byte[] actl = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(actl, (uint)(frameCount ?? frames.Length));
        Chunk(file, "acTL", actl);
        if (palette is not null)
        {
            Chunk(file, "PLTE", palette);
        }

        if (transparency is not null)
        {
            Chunk(file, "tRNS", transparency);
        }

        uint sequence = 0;
        for (int i = 0; i < frames.Length; i++)
        {
            (Rect bounds, byte[] rows, byte disposal, byte blend) = frames[i];
            if (i == sequenceGapAt)
            {
                sequence++;
            }

            byte[] fctl = new byte[26];
            BinaryPrimitives.WriteUInt32BigEndian(fctl, sequence++);
            BinaryPrimitives.WriteUInt32BigEndian(fctl.AsSpan(4), (uint)bounds.Width);
            BinaryPrimitives.WriteUInt32BigEndian(fctl.AsSpan(8), (uint)bounds.Height);
            BinaryPrimitives.WriteUInt32BigEndian(fctl.AsSpan(12), (uint)bounds.X);
            BinaryPrimitives.WriteUInt32BigEndian(fctl.AsSpan(16), (uint)bounds.Y);
            BinaryPrimitives.WriteUInt16BigEndian(fctl.AsSpan(20), 1);
            BinaryPrimitives.WriteUInt16BigEndian(fctl.AsSpan(22), 10);
            fctl[24] = disposal;
            fctl[25] = blend;
            Chunk(file, "fcTL", fctl);
            byte[] compressed = Compress(rows);
            if (i == 0)
            {
                Chunk(file, "IDAT", compressed);
            }
            else
            {
                byte[] fdat = new byte[4 + compressed.Length];
                BinaryPrimitives.WriteUInt32BigEndian(fdat, sequence++);
                compressed.CopyTo(fdat, 4);
                Chunk(file, "fdAT", fdat);
            }
        }

        Chunk(file, "IEND", []);
        return file.ToArray();
    }

    private static byte[] Compress(byte[] data)
    {
        using MemoryStream output = new();
        using (ZLibStream zlib = new(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return output.ToArray();
    }

    private static void Chunk(Stream file, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(number, (uint)data.Length);
        file.Write(number);
        byte[] typed = [.. System.Text.Encoding.ASCII.GetBytes(type), .. data];
        file.Write(typed);
        BinaryPrimitives.WriteUInt32BigEndian(number, Egami.Formats.Crc32.Update(0, typed));
        file.Write(number);
    }
}
