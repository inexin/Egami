using Egami.Formats.Jxl;
using Egami.Pixels;

namespace Egami.Tests.Formats.Jxl;

/// <summary>JPEG recompression: every JPEG JPEG XL can keep comes back byte for byte, smaller.</summary>
public class JxlRecompressTests
{
    public static TheoryData<string> Jpegs()
    {
        TheoryData<string> data = [];
        foreach (string f in Directory.EnumerateFiles(Path.Combine(SimpleCorpus.Images, "jpeg"), "*.jpg", SearchOption.AllDirectories).Order())
        {
            data.Add(Path.GetRelativePath(Path.Combine(SimpleCorpus.Images, "jpeg"), f));
        }

        return data;
    }

    /// <summary>Scans of some of the components (here Cb and Cr together) span the frame's MCU, not one of theirs.</summary>
    [Fact]
    public void PartiallyInterleavedScansRoundTrip()
    {
        byte[] jpeg = File.ReadAllBytes(Path.Combine(SimpleCorpus.Images, "jxl", "libjxl", "flower_small.q85_420_partially_interleaved.jpg"));
        Assert.Equal(jpeg, RoundTrip(jpeg));
    }

    /// <summary>
    /// Equal sampling factors other than 1×1 (all 1×2 here) are 4:4:4 to JPEG XL, with chroma from luma: the JPEG comes back,
    /// and the pixels are the JPEG's, near enough.
    /// </summary>
    [Fact]
    public void EqualSamplingFactorsRoundTrip()
    {
        byte[] jpeg = SampledJpeg(0x12, 40, 32);
        byte[] jxl = Recompress(jpeg);
        Assert.Equal(jpeg, Reconstruct(jxl));
        using Raster<Rgb8> fromJpeg = Raster.Load<Rgb8>(new MemoryStream(jpeg));
        using Raster<Rgb8> fromJxl = Raster.Load<Rgb8>(new MemoryStream(jxl));
        int max = 0;
        for (int y = 0; y < fromJpeg.Height; y++)
        {
            for (int x = 0; x < fromJpeg.Width; x++)
            {
                Rgb8 a = fromJpeg.Row(y)[x], b = fromJxl.Row(y)[x];
                max = Math.Max(max, Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B))));
            }
        }

        // Random high-frequency coefficients push integer and float IDCTs a few levels apart; without chroma from luma undone
        // the chroma would be far off.
        Assert.InRange(max, 0, 8);
    }

    /// <summary>
    /// Writes a baseline JPEG whose three components all have the sampling byte <paramref name="sampling"/>, with random
    /// coefficients (chroma partly following luma) and simple Huffman codes: every DC size 4 bits long, every AC symbol 6.
    /// </summary>
    private static byte[] SampledJpeg(byte sampling, int width, int height)
    {
        int h = sampling >> 4, v = sampling & 15;
        int cols = (width + (8 * h) - 1) / (8 * h);
        int rows = (height + (8 * v) - 1) / (8 * v);
        Random rng = new(5);
        List<int> acSymbols = [0x00, 0xF0];
        for (int run = 0; run < 16; run++)
        {
            for (int size = 1; size <= 3; size++)
            {
                acSymbols.Add((run << 4) | size);
            }
        }

        using MemoryStream o = new();
        o.Write([0xFF, 0xD8]);
        foreach ((int id, int q) in (ReadOnlySpan<(int, int)>)[(0, 2), (1, 3)])
        {
            o.Write([0xFF, 0xDB, 0, 67, (byte)id]);
            for (int i = 0; i < 64; i++)
            {
                o.WriteByte((byte)(q + (i / 8)));
            }
        }

        o.Write([0xFF, 0xC0, 0, 17, 8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 3, 1, sampling, 0, 2, sampling, 1, 3, sampling, 1]);
        byte[] dcCounts = new byte[16];
        dcCounts[3] = 12;
        o.Write([0xFF, 0xC4, 0, 31, 0x00, .. dcCounts, .. Enumerable.Range(0, 12).Select(i => (byte)i)]);
        byte[] acCounts = new byte[16];
        acCounts[5] = (byte)acSymbols.Count;
        o.Write([0xFF, 0xC4, 0, (byte)(19 + acSymbols.Count), 0x10, .. acCounts, .. acSymbols.Select(x => (byte)x)]);
        o.Write([0xFF, 0xDA, 0, 12, 3, 1, 0x00, 2, 0x00, 3, 0x00, 0, 63, 0]);
        ulong bits = 0;
        int count = 0;
        void Put(int value, int n)
        {
            bits = (bits << n) | (uint)(value & ((1 << n) - 1));
            count += n;
            while (count >= 8)
            {
                byte b = (byte)(bits >> (count - 8));
                o.WriteByte(b);
                if (b == 0xFF)
                {
                    o.WriteByte(0);
                }

                count -= 8;
            }
        }

        int Size(int x) => x == 0 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)Math.Abs(x));
        int[] lastDc = new int[3];
        int[] luma = new int[64];
        for (int m = 0; m < rows * cols; m++)
        {
            for (int c = 0; c < 3; c++)
            {
                for (int b = 0; b < h * v; b++)
                {
                    int[] block = new int[64];
                    block[0] = rng.Next(-60, 60);
                    for (int k = 1; k < 64; k++)
                    {
                        int value = rng.Next(100) < 20 - (k / 4) ? rng.Next(-7, 8) : 0;
                        block[k] = c == 0 ? value : Math.Clamp((luma[k] / 2) + (value / 3), -7, 7);
                    }

                    if (c == 0)
                    {
                        block.CopyTo(luma, 0);
                    }

                    int diff = block[0] - lastDc[c];
                    lastDc[c] = block[0];
                    int ds = Size(diff);
                    Put(ds, 4);
                    Put(diff < 0 ? diff - 1 : diff, ds);
                    int zeros = 0;
                    for (int k = 1; k < 64; k++)
                    {
                        if (block[k] == 0)
                        {
                            zeros++;
                            continue;
                        }

                        for (; zeros >= 16; zeros -= 16)
                        {
                            Put(acSymbols.IndexOf(0xF0), 6);
                        }

                        int size = Size(block[k]);
                        Put(acSymbols.IndexOf((zeros << 4) | size), 6);
                        Put(block[k] < 0 ? block[k] - 1 : block[k], size);
                        zeros = 0;
                    }

                    if (zeros > 0)
                    {
                        Put(0, 6);
                    }
                }
            }
        }

        if (count > 0)
        {
            Put(0x7F, 8 - count);
        }

        o.Write([0xFF, 0xD9]);
        return o.ToArray();
    }

    /// <summary>
    /// Fuzzer finds: padding bits that are not all ones, written back highest first; quantization tables whose DQT slots are
    /// not their list order, named in SOF by slot; both rebuild byte for byte, as djxl rebuilds them.
    /// </summary>
    [Theory]
    [InlineData("padding-bits.jpg")]
    [InlineData("quant-slots.jpg")]
    public void FuzzedJpegsRoundTrip(string name)
    {
        byte[] jpeg = File.ReadAllBytes(Path.Combine(SimpleCorpus.Images, "fuzz", "jxl-jpeg", name));
        Assert.Equal(jpeg, RoundTrip(jpeg));
    }

    /// <summary>A damaged stream whose coefficients re-encode with other padding than it had: libjxl writes a file it cannot rebuild; Egami refuses it.</summary>
    [Fact]
    public void UnrebuildableJpegsAreRefused()
    {
        byte[] jpeg = File.ReadAllBytes(Path.Combine(SimpleCorpus.Images, "fuzz", "jxl-jpeg", "unrebuildable.jpg"));
        Assert.Throws<UnsupportedFormatException>(() => Recompress(jpeg));
    }

    /// <summary>A fuzzer's 65329 × 248 frame: the limits stop it at the frame header, before its scan is decoded.</summary>
    [Fact]
    public void LimitsStopLargeFramesBeforeTheScan()
    {
        byte[] jpeg = File.ReadAllBytes(Path.Combine(SimpleCorpus.Images, "fuzz", "jxl-jpeg", "wide-frame.jpg"));
        EgamiContext context = EgamiContext.Default with { Limits = SafetyLimits.Default with { MaxPixels = 4_000_000 } };
        Assert.Throws<LimitExceededException>(() => JxlImage.RecompressJpeg(new MemoryStream(jpeg), Stream.Null, context));
    }

    [Theory]
    [MemberData(nameof(Jpegs))]
    public void RoundTrips(string name)
    {
        byte[] jpeg = File.ReadAllBytes(Path.Combine(SimpleCorpus.Images, "jpeg", name));
        using MemoryStream jxl = new();
        try
        {
            JxlImage.RecompressJpeg(new MemoryStream(jpeg), jxl);
        }
        catch (UnsupportedFormatException e)
        {
            Assert.Skip($"Not recompressible: {e.Message}");
        }

        jxl.Position = 0;
        using MemoryStream back = new();
        JxlImage.ReconstructJpeg(jxl, back);
        Assert.Equal(jpeg, back.ToArray());
        if (jpeg.Length > 20000)
        {
            // Small files are mostly headers, which the jbrd box repeats; progressive and optimized JPEGs gain least (cjxl too).
            Assert.True(jxl.Length < jpeg.Length * 0.95, $"{jxl.Length} bytes against the JPEG's {jpeg.Length}");
        }
    }

    private static byte[] Recompress(byte[] jpeg)
    {
        using MemoryStream jxl = new();
        JxlImage.RecompressJpeg(new MemoryStream(jpeg), jxl);
        return jxl.ToArray();
    }

    private static byte[] Reconstruct(byte[] jxl)
    {
        using MemoryStream back = new();
        JxlImage.ReconstructJpeg(new MemoryStream(jxl), back);
        return back.ToArray();
    }

    private static byte[] RoundTrip(byte[] jpeg) => Reconstruct(Recompress(jpeg));
}
