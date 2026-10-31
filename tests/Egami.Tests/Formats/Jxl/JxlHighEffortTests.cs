using Egami.Formats.Jxl;
using Egami.Pixels;

namespace Egami.Tests.Formats.Jxl;

/// <summary>JPEG XL extras PR B: lossless efforts 8 to 10, which must give every sample back, with LZ77 and per-group trees.</summary>
public class JxlHighEffortTests
{
    private static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images");

    public static TheoryData<int, string> Cases => new()
    {
        { 8, "photo" }, { 9, "photo" }, { 10, "photo" },
        { 8, "text" }, { 9, "text" }, { 10, "text" },
        { 8, "wide" }, { 9, "wide" }, { 10, "wide" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void LosslessIsExact(int effort, string kind)
    {
        using Raster<Rgba16> image = kind switch
        {
            "photo" => Raster.Load<Rgba16>(Path.Combine(Images, "ops", "egret.png")),
            "text" => Text(1100, 700),
            _ => Wide(),
        };
        using MemoryStream file = new();
        image.Save(file, JxlFormat.Info, new JxlWriterOptions { Lossless = true, Effort = effort });
        file.Position = 0;
        using Raster<Rgba16> back = Raster.Load<Rgba16>(file);
        for (int y = 0; y < image.Height; y++)
        {
            Assert.True(image.Row(y).SequenceEqual(back.Row(y)), $"Row {y} differs.");
        }
    }

    [Fact]
    public void EffortAboveTenIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new JxlWriterOptions { Effort = 11 });
        Assert.Equal(10, new JxlWriterOptions { Effort = 10 }.Effort);
    }

    /// <summary>Lines of repeated glyph-like shapes over 18 groups, which effort 8 and 9 code group by group, with LZ77 copies.</summary>
    private static Raster<Rgba16> Text(int width, int height)
    {
        Raster<Rgba16> r = Raster.Create<Rgba16>(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int glyph = (x / 9) % 7, gx = x % 9, gy = y % 14;
                bool ink = gy is > 2 and < 11 && gx is > 1 and < 7 && ((gx + gy + glyph) % 3 == 0 || gx == 2 + (glyph % 3));
                ushort v = ink ? (ushort)(4000 + (y / 140 * 1000)) : (ushort)60000;
                r.Row(y)[x] = new Rgba16(v, v, (ushort)(v ^ 0x40), 65535);
            }
        }

        return r;
    }

    /// <summary>A smooth 16-bit picture with translucent edges.</summary>
    private static Raster<Rgba16> Wide()
    {
        Raster<Rgba16> r = Raster.Create<Rgba16>(300, 200);
        for (int y = 0; y < r.Height; y++)
        {
            for (int x = 0; x < r.Width; x++)
            {
                r.Row(y)[x] = new Rgba16((ushort)(x * 211), (ushort)(y * 317), (ushort)((x * y) & 0xFFFF), (ushort)(x < 20 ? x * 3000 : 65535));
            }
        }

        return r;
    }
}
