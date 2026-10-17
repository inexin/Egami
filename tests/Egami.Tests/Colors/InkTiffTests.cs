using Egami.Colors;
using Egami.Formats.Tiff;
using Egami.Pixels;

namespace Egami.Tests.Colors;

/// <summary>Phase 2.6.5 task 5: ink rasters saved and loaded as separated TIFF. libtiff's reading is checked in egami-compare.</summary>
public class InkTiffTests
{
    private static readonly InkSet Spots = InkSet.FromProfile(ColorProfile.Fogra39)
        .WithSpot("PANTONE 186 C", InkAppearance.Cmyk(0, 1, 0.81f, 0.04f))
        .WithSpot("Varnish", null, ColorantKind.Technical);

    public static TheoryData<InkDepth, TiffCompression, bool> Settings()
    {
        TheoryData<InkDepth, TiffCompression, bool> data = [];
        foreach (InkDepth depth in Enum.GetValues<InkDepth>())
        {
            foreach (TiffCompression compression in (TiffCompression[])[TiffCompression.Deflate, TiffCompression.Lzw, TiffCompression.PackBits, TiffCompression.None])
            {
                data.Add(depth, compression, true);
            }

            data.Add(depth, TiffCompression.Deflate, false);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Settings))]
    public void RoundTripsAreIdentical(InkDepth depth, TiffCompression compression, bool alpha)
    {
        using InkRaster raster = Pattern(Spots, depth, opaque: !alpha);
        using MemoryStream file = new();
        raster.Save(file, new InkTiffOptions { Compression = compression, IncludeAlpha = alpha });
        file.Position = 0;
        using InkRaster back = InkRaster.Load(file);
        Assert.Equal(raster.Plates.Select(p => p.Name), back.Plates.Select(p => p.Name));
        Assert.Equal((raster.Width, raster.Height, raster.Depth), (back.Width, back.Height, back.Depth));
        AssertSamePlanes(raster, back);

        // The spots come back as spots without appearances: TIFF does not keep them.
        Assert.Equal(ColorantKind.Spot, back.Inks[5].Kind);
        Assert.Null(back.Inks[4].Appearance);
    }

    [Fact]
    public void PlainCmykIsInkSetOneAndNColourKeepsItsProfile()
    {
        using InkRaster cmyk = Pattern(InkSet.FromProfile(ColorProfile.Fogra39), InkDepth.Unorm8, opaque: true);
        using MemoryStream file = new();
        cmyk.Save(file, new InkTiffOptions { IncludeAlpha = false });

        // An ordinary TIFF reader sees a CMYK image.
        file.Position = 0;
        using (Raster loaded = Raster.Load(file))
        {
            Assert.Equal(PixelChannelOrder.Cmyk, loaded.Layout.ChannelOrder);
            Assert.Equal("FOGRA39L Coated", loaded.Metadata.GetColorProfile().Name);
        }

        ColorProfile seven = ColorProfile.FromIcc(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "images", "icc", "ncolor", "7clr-mab-v4.icc")));
        using InkRaster raster = Pattern(InkSet.FromProfile(seven).WithSpot("Gold", null), InkDepth.Unorm16, opaque: true);
        using MemoryStream nfile = new();
        raster.Save(nfile);
        nfile.Position = 0;
        using InkRaster back = InkRaster.Load(nfile);
        Assert.Equal(ColorModel.NColor, back.Inks.Profile.Model);
        Assert.Equal(7, back.Inks.ProcessCount);
        Assert.Equal(["Cyan", "Magenta", "Yellow", "Black", "Orange", "Green", "Violet", "Gold"], back.Plates.Select(p => p.Name));
        AssertSamePlanes(raster, back);
    }

    [Fact]
    public void AnOrdinaryCmykTiffLoadsAsCoveredPlates()
    {
        using Raster<Cmyk8> image = Raster.Create<Cmyk8>(3, 2, new Cmyk8(10, 20, 30, 40));
        using MemoryStream file = new();
        image.Save(file, TiffFormat.Info);
        file.Position = 0;
        using InkRaster plates = InkRaster.Load(file);
        Assert.Equal(["Cyan", "Magenta", "Yellow", "Black"], plates.Plates.Select(p => p.Name));
        Assert.Equal(30, plates.Plate("Yellow")!.Row8(1)[2]);
        Assert.Equal(255, plates.Alpha.Row8(0)[0]);
    }

    [Fact]
    public void OtherFilesAreRefused()
    {
        using Raster<Rgb8> image = Raster.Create<Rgb8>(3, 2);
        using MemoryStream file = new();
        image.Save(file, TiffFormat.Info);
        file.Position = 0;
        Assert.Throws<UnsupportedFormatException>(() => InkRaster.Load(file));
        Assert.Throws<ArgumentOutOfRangeException>(() => new InkTiffOptions { Compression = TiffCompression.Jpeg });
    }

    [Fact]
    public void FuzzFindingsFailCleanly()
    {
        // Task 16's inktiff findings: more inks than a raster holds, and no samples per pixel, once threw ArgumentException.
        foreach (string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "images", "fuzz", "inktiff-regressions")))
        {
            try
            {
                using InkRaster loaded = InkRaster.Load(new MemoryStream(File.ReadAllBytes(file)));
            }
            catch (EgamiException)
            {
            }
        }
    }

    // Every value the depth holds somewhere, alpha varying (or full), each plane different, and premultiplied as stored.
    private static InkRaster Pattern(InkSet inks, InkDepth depth, bool opaque)
    {
        InkRaster raster = new(inks, 67, 41, depth);
        for (int y = 0; y < raster.Height; y++)
        {
            for (int p = 0; p <= raster.Plates.Count; p++)
            {
                InkPlate plate = p < raster.Plates.Count ? raster.Plates[p] : raster.Alpha;
                for (int x = 0; x < raster.Width; x++)
                {
                    int v = p == raster.Plates.Count && opaque ? 65535 : (((x * 977) + (y * 131) + (p * 7919)) * 37) & 0xFFFF;
                    if (depth == InkDepth.Unorm8)
                    {
                        plate.Row8(y)[x] = (byte)(v >> 8);
                    }
                    else
                    {
                        plate.Row16(y)[x] = (ushort)v;
                    }
                }
            }
        }

        return raster;
    }

    private static void AssertSamePlanes(InkRaster a, InkRaster b)
    {
        for (int p = 0; p <= a.Plates.Count; p++)
        {
            InkPlate x = p < a.Plates.Count ? a.Plates[p] : a.Alpha, z = p < b.Plates.Count ? b.Plates[p] : b.Alpha;
            for (int y = 0; y < a.Height; y++)
            {
                bool same = a.Depth == InkDepth.Unorm8 ? x.Row8(y).SequenceEqual(z.Row8(y)) : x.Row16(y).SequenceEqual(z.Row16(y));
                Assert.True(same, $"{x.Name}, row {y}");
            }
        }
    }
}
