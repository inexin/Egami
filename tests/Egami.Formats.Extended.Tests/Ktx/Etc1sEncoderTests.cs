using Egami.Formats.Extended.Ktx;
using Egami.Formats.Extended.Textures.Blocks;
using Egami.Pixels;

namespace Egami.Formats.Extended.Tests.Ktx;

/// <summary>
/// The ETC1S encoder the KTX writer will use: it encodes a 120 × 120 crop of the duck photo, the KTX reader decodes the result,
/// and the PSNR must reach what the encoder reached when it was written (a little below, so harmless changes pass). Higher
/// quality must cost more bytes and look better, higher effort must not look worse, and the output must not depend on the
/// number of threads.
/// </summary>
public class Etc1sEncoderTests
{
    private const int Size = 120;

    private static readonly Lazy<byte[]> Duck = new(() =>
    {
        using Raster<Rgba8> photo = Raster.Load<Rgba8>(Path.Combine(AppContext.BaseDirectory, "images", "ops", "duck.png"));
        byte[] rgba = new byte[Size * Size * 4];
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                Rgba8 p = photo.Row(160 + y)[240 + x];
                int i = ((y * Size) + x) * 4;
                (rgba[i], rgba[i + 1], rgba[i + 2]) = (p.R, p.G, p.B);

                // Alpha: a ramp across, with a band of fully transparent pixels.
                rgba[i + 3] = (byte)(x is >= 50 and < 58 ? 0 : 40 + (x * 215 / (Size - 1)));
            }
        }

        return rgba;
    });

    public static TheoryData<int, int, bool, double> Cases => new()
    {
        // Quality, effort, alpha, minimum PSNR (RGB, or RGBA with alpha).
        { 1, 4, false, 20.9 },
        { 64, 0, false, 23.0 },
        { 64, 4, false, 24.4 },
        { 128, 0, false, 27.6 },
        { 128, 4, false, 28.4 },
        { 128, 6, false, 28.4 },
        { 255, 4, false, 29.9 },
        { 128, 4, true, 29.6 },
        { 255, 4, true, 31.1 },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void ReachesItsQuality(int quality, int effort, bool alpha, double minimum)
    {
        byte[] rgba = Duck.Value;
        Etc1sEncoded encoded = Etc1sEncoder.Encode([new Etc1sImage(rgba, Size, Size, 0)], alpha, quality, effort);
        double psnr = Psnr(rgba, Decode(encoded, Size, Size, alpha, 0), alpha);
        Assert.True(psnr >= minimum, $"{psnr:F2} dB");
    }

    [Fact]
    public void HigherQualityCostsMoreAndLooksBetter()
    {
        byte[] rgba = Duck.Value;
        double previousPsnr = 0;
        int previousBytes = 0;
        foreach (int quality in new[] { 16, 64, 128, 255 })
        {
            Etc1sEncoded encoded = Etc1sEncoder.Encode([new Etc1sImage(rgba, Size, Size, 0)], false, quality, 2);
            double psnr = Psnr(rgba, Decode(encoded, Size, Size, false, 0), false);
            int bytes = encoded.GlobalData.Length + encoded.Levels.Sum(l => l.Length);
            Assert.True(psnr > previousPsnr, $"quality {quality}: {psnr:F2} dB after {previousPsnr:F2} dB");
            Assert.True(bytes > previousBytes, $"quality {quality}: {bytes} bytes after {previousBytes}");
            (previousPsnr, previousBytes) = (psnr, bytes);
        }

        Assert.True(Etc1sEncoder.CodebookSize(1) == 16 && Etc1sEncoder.CodebookSize(255) == 16128);
    }

    [Fact]
    public void HigherEffortDoesNotLookWorse()
    {
        byte[] rgba = Duck.Value;
        double previous = 0;
        foreach (int effort in new[] { 0, 2, 4, 6 })
        {
            Etc1sEncoded encoded = Etc1sEncoder.Encode([new Etc1sImage(rgba, Size, Size, 0)], true, 128, effort);
            double psnr = Psnr(rgba, Decode(encoded, Size, Size, true, 0), true);
            Assert.True(psnr >= previous - 0.05, $"effort {effort}: {psnr:F2} dB after {previous:F2} dB");
            previous = psnr;
        }
    }

    [Fact]
    public void OutputDoesNotDependOnThreads()
    {
        byte[] rgba = Duck.Value;
        Etc1sEncoded one = Etc1sEncoder.Encode([new Etc1sImage(rgba, Size, Size, 0)], true, 128, 4, threads: 1);
        Etc1sEncoded four = Etc1sEncoder.Encode([new Etc1sImage(rgba, Size, Size, 0)], true, 128, 4, threads: 4);
        Assert.Equal(one.GlobalData, four.GlobalData);
        Assert.Equal(one.Levels.Length, four.Levels.Length);
        for (int i = 0; i < one.Levels.Length; i++)
        {
            Assert.Equal(one.Levels[i], four.Levels[i]);
        }
    }

    [Fact]
    public void EncodesMipLevelsThatShareTheCodebooks()
    {
        // Level 0 is the crop, level 1 every other pixel of it, level 2 an odd size that leaves partial blocks.
        byte[] rgba = Duck.Value;
        List<Etc1sImage> images = [new Etc1sImage(rgba, Size, Size, 0)];
        foreach ((int level, int size) in new[] { (1, Size / 2), (2, 30) })
        {
            int step = Size / size;
            byte[] smaller = new byte[size * size * 4];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    rgba.AsSpan(((y * step * Size) + (x * step)) * 4, 4).CopyTo(smaller.AsSpan(((y * size) + x) * 4));
                }
            }

            images.Add(new Etc1sImage(smaller, size, size, level));
        }

        Etc1sEncoded encoded = Etc1sEncoder.Encode(images, true, 128, 2);
        Assert.Equal(3, encoded.Levels.Length);
        for (int level = 0; level < 3; level++)
        {
            Etc1sImage image = images[level];
            double psnr = Psnr(image.Rgba, Decode(encoded, Size, Size, true, level), true);
            Assert.True(psnr >= 22, $"level {level}: {psnr:F2} dB");
        }
    }

    /// <summary>Wraps <paramref name="encoded"/> in a KTX 2 file and reads mip level <paramref name="mip"/> back as RGBA bytes.</summary>
    private static byte[] Decode(Etc1sEncoded encoded, int width, int height, bool alpha, int mip)
    {
        byte[] file = KtxFiles.Ktx2(0, width, height, encoded.Levels, scheme: 1, uncompressed: new long[encoded.Levels.Length], model: 163,
            transfer: 2, globalData: encoded.GlobalData, channels: alpha ? [0, 15] : [0]);
        using Raster<Rgba8> texture = Raster.Load<Rgba8>(new MemoryStream(file), TestImages.Context,
            new KtxReaderOptions { MipLevel = mip, Recovery = RecoveryMode.Strict });
        Assert.Empty(texture.Metadata.Warnings);
        byte[] rgba = new byte[texture.Width * texture.Height * 4];
        for (int y = 0; y < texture.Height; y++)
        {
            for (int x = 0; x < texture.Width; x++)
            {
                Rgba8 p = texture.Row(y)[x];
                int i = ((y * texture.Width) + x) * 4;
                (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = (p.R, p.G, p.B, p.A);
            }
        }

        return rgba;
    }

    private static double Psnr(byte[] expected, byte[] actual, bool alpha)
    {
        Assert.Equal(expected.Length, actual.Length);
        double sum = 0;
        int count = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            if (alpha || (i & 3) != 3)
            {
                double d = expected[i] - actual[i];
                sum += d * d;
                count++;
            }
        }

        return sum == 0 ? 99 : 10 * Math.Log10(255.0 * 255 * count / sum);
    }
}
