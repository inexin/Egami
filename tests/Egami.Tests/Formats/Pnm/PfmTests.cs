using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

using Egami.Formats;
using Egami.Formats.Pnm;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Tests.Formats.Pnm;

/// <summary>The PFM float map (Phase 2.1.1 task 10). Netpbm and ImageMagick references follow in egami-compare.</summary>
public class PfmTests
{
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "images", "pfm");

    [Theory]
    [InlineData("rgb-little.pfm", true, true, 1.0)]
    [InlineData("gray-little.pfm", false, true, 1.0)]
    [InlineData("rgb-big.pfm", true, false, 2.5)]
    [InlineData("gray-big-hdr.pfm", false, false, 1.0)]
    public void ReadsBothByteOrdersBottomRowFirst(string name, bool color, bool little, double scale)
    {
        string path = Path.Combine(Folder, name);
        byte[] file = File.ReadAllBytes(path);
        using Raster raster = Raster.Load(path);
        Assert.Equal(color ? typeof(Raster<RgbF32>) : typeof(Raster<GrayF32>), raster.GetType());
        Assert.Equal(ColorSpaceHint.Linear, raster.Metadata.ColorSpace);
        PnmDetails details = (PnmDetails)raster.Metadata.FormatDetails!;
        Assert.Equal((PnmKind.FloatMap, little, scale), (details.Kind, details.LittleEndian!.Value, details.Scale!.Value));

        // The first float after the header is the bottom row's first sample.
        int headerLength = HeaderLength(file);
        float first = little ? BinaryPrimitives.ReadSingleLittleEndian(file.AsSpan(headerLength)) : BinaryPrimitives.ReadSingleBigEndian(file.AsSpan(headerLength));
        float loaded = color ? ((Raster<RgbF32>)raster).Row(raster.Height - 1)[0].R : ((Raster<GrayF32>)raster).Row(raster.Height - 1)[0].L;
        Assert.Equal(first, loaded);

        RasterInfo info = Raster.Probe(path);
        Assert.Equal((PnmFormat.Info, raster.Width, raster.Height, raster.Layout), (info.Format, info.Width, info.Height, info.Layout));
    }

    [Fact]
    public void FloatsRoundTripBitForBit()
    {
        using Raster<RgbF32> color = Raster.Create<RgbF32>(13, 6);
        using Raster<GrayF32> gray = Raster.Create<GrayF32>(13, 6);
        Random random = new(12);
        for (int y = 0; y < 6; y++)
        {
            Span<float> c = MemoryMarshal.Cast<RgbF32, float>(color.Row(y));
            for (int i = 0; i < c.Length; i++)
            {
                c[i] = (float)((random.NextDouble() * 40) - 10);
            }

            Span<float> g = MemoryMarshal.Cast<GrayF32, float>(gray.Row(y));
            for (int i = 0; i < g.Length; i++)
            {
                g[i] = (float)((random.NextDouble() * 4) - 1);
            }
        }

        string path = Path.Combine(Path.GetTempPath(), $"egami-{Guid.NewGuid():N}.pfm");
        try
        {
            color.Save(path);
            Assert.StartsWith("PF\n13 6\n-1.0\n", Encoding.ASCII.GetString(File.ReadAllBytes(path), 0, 13), StringComparison.Ordinal);
            using (Raster<RgbF32> back = Raster.Load<RgbF32>(path))
            {
                for (int y = 0; y < 6; y++)
                {
                    Assert.True(MemoryMarshal.AsBytes(color.Row(y)).SequenceEqual(MemoryMarshal.AsBytes(back.Row(y))));
                }
            }

            gray.Save(path);
            Assert.StartsWith("Pf\n", Encoding.ASCII.GetString(File.ReadAllBytes(path), 0, 3), StringComparison.Ordinal);
            using Raster<GrayF32> grayBack = Raster.Load<GrayF32>(path);
            for (int y = 0; y < 6; y++)
            {
                Assert.True(MemoryMarshal.AsBytes(gray.Row(y)).SequenceEqual(MemoryMarshal.AsBytes(grayBack.Row(y))));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void OtherTypesConvertBothWays()
    {
        using Raster<Rgba8> image = Raster.Create<Rgba8>(4, 4, new Rgba8(255, 128, 0, 77));
        using MemoryStream stream = new();
        image.Save(stream, PnmFormat.Info, new PnmWriterOptions { Kind = PnmKind.FloatMap });
        stream.Position = 0;
        using Raster<Rgba8> back = Raster.Load<Rgba8>(stream);
        Assert.Equal(new Rgba8(255, 128, 0, 255), back.Row(3)[3]);

        // Auto picks PFM for float types without alpha only.
        using Raster<RgbF32> floats = Raster.Create<RgbF32>(2, 2, new RgbF32(2f, 0.5f, -1f));
        using MemoryStream auto = new();
        floats.Save(auto, PnmFormat.Info);
        Assert.Equal((byte)'F', auto.ToArray()[1]);
    }

    [Fact]
    public void ATruncatedFileLeavesTheTopRowsBlack()
    {
        byte[] file = File.ReadAllBytes(Path.Combine(Folder, "rgb-little.pfm"));
        byte[] cut = file[..(HeaderLength(file) + (7 * 3 * 4 * 2) + 5)];
        using Raster<RgbF32> raster = Raster.Load<RgbF32>(cut);
        Assert.Contains(raster.Metadata.Warnings, w => w.Message.Contains("top 3 rows", StringComparison.Ordinal));
        Assert.Equal(default, raster.Row(0)[0]);
        Assert.NotEqual(default, raster.Row(4)[0]);
        Assert.Throws<CorruptDataException>(() => Raster.Load(cut, options: new ReaderOptions { Recovery = RecoveryMode.Strict }).Dispose());
    }

    [Theory]
    [InlineData("PF\n2 2\n0\n")]
    [InlineData("PF\n2 2\nscale\n")]
    [InlineData("Pf\n0 2\n-1\n")]
    [InlineData("PF\n2 2\n-1")]
    public void BadHeadersAreCorrupt(string header)
    {
        byte[] data = [.. Encoding.ASCII.GetBytes(header), .. new byte[64]];
        Assert.Throws<CorruptDataException>(() => Raster.Load(data).Dispose());
    }

    private static int HeaderLength(byte[] file)
    {
        int lines = 0;
        for (int i = 0; i < file.Length; i++)
        {
            if (file[i] == '\n' && ++lines == 3)
            {
                return i + 1;
            }
        }

        throw new InvalidDataException();
    }
}
