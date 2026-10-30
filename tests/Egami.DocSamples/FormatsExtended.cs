using Egami.Formats;
using Egami.Formats.Extended;
using Egami.Formats.Extended.Dds;
using Egami.Formats.Extended.Pcx;
using Egami.Formats.Extended.Sgi;
using Egami.Formats.Extended.SunRaster;
using Egami.Formats.Extended.Xbm;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.DocSamples;

[Collection("Samples")]
public class FormatsExtended
{
    [Fact]
    public void Register()
    {
        using SampleFiles files = new();

        #region register
        // Every stable format of Egami.Formats.Extended, once at startup before any other Egami call:
        // EgamiContext.SetDefault(new EgamiContext().WithExtendedFormats());

        // ...or in a context of its own.
        EgamiContext context = EgamiContext.Default.WithExtendedFormats();
        using Raster image = Raster.Load("duck.png", context);

        // A raster saves to a path through the context it was loaded with, so the extension picks PCX.
        image.Save("duck.pcx");
        using Raster back = Raster.Load("duck.pcx", context);
        string format = back.Metadata.FormatName!;               // "PCX"
        #endregion

        Assert.Equal("PCX", format);
        Assert.Equal((image.Width, image.Height), (back.Width, back.Height));
    }

    [Fact]
    public void RegisterOne()
    {
        using SampleFiles files = new();
        using Raster image = Raster.Load("duck.png");

        #region register-one
        // One format at a time: each has its own method and its own FormatInfo.
        EgamiContext context = EgamiContext.Default.WithSgi().WithSunRaster();
        using (FileStream output = File.Create("duck.sgi"))
        {
            image.Save(output, SgiFormat.Info);
        }

        RasterInfo info = Raster.Probe("duck.sgi", context);
        Console.WriteLine($"{info.Format.Name} {info.Width}×{info.Height} {info.Layout.ChannelOrder}");
        #endregion

        Assert.Equal("SGI", info.Format.Name);
        Assert.Equal(SunRasterFormat.Info, context.Formats[^1]);
    }

    [Fact]
    public void Details()
    {
        using SampleFiles files = new();
        EgamiContext context = EgamiContext.Default.WithExtendedFormats();
        using (Raster<Gray8> cursor = Raster.Create<Gray8>(16, 16, context))
        {
            cursor.Metadata.FormatDetails = new XbmDetails { Name = "arrow", Hotspot = new Point(1, 1) };
            cursor.Save("arrow.xbm");
        }

        #region details
        // What the header said that the pixels do not: PcxDetails, SgiDetails, SunRasterDetails, XbmDetails and XpmDetails.
        using Raster image = Raster.Load("arrow.xbm", context);
        if (image.Metadata.FormatDetails is XbmDetails xbm)
        {
            Console.WriteLine($"{xbm.Name}, hotspot {xbm.Hotspot}");
        }

        // Writers take the name and hotspot back from the details.
        image.Metadata.FormatDetails = new XbmDetails { Name = "pointer", Hotspot = new Point(0, 0) };
        image.Save("pointer.xbm");
        #endregion

        Assert.StartsWith("#define pointer_width 16", File.ReadAllText("pointer.xbm"), StringComparison.Ordinal);
        Assert.IsType<PcxDetails>(Raster.Load(SavePcx(image), context).Metadata.FormatDetails);
    }

    [Fact]
    public void Options()
    {
        using SampleFiles files = new();
        using Raster image = Raster.Load("duck.png");

        #region options
        // SGI codes its rows by default; Sun raster writes them plain unless asked.
        using (FileStream sgi = File.Create("duck.rgb"))
        {
            image.Save(sgi, SgiFormat.Info, new SgiWriterOptions { Rle = false });
        }

        using (FileStream sun = File.Create("duck.ras"))
        {
            image.Save(sun, SunRasterFormat.Info, new SunRasterWriterOptions { Rle = true });
        }
        #endregion

        EgamiContext context = EgamiContext.Default.WithExtendedFormats();
        Assert.False(((SgiDetails)Raster.Load("duck.rgb", context).Metadata.FormatDetails!).Rle);
        Assert.True(((SunRasterDetails)Raster.Load("duck.ras", context).Metadata.FormatDetails!).Rle);
    }

    [Fact]
    public void DdsLoad()
    {
        using SampleFiles files = new();
        WriteTexture("rock.dds", faces: 1, size: 64);

        #region dds-load
        // Load gives mip 0 of the first surface, in the format's natural pixel type (Rgba8 for BC1 to BC3, BC7 and RGBA8).
        EgamiContext context = EgamiContext.Default.WithDds();
        using Raster texture = Raster.Load("rock.dds", context);
        texture.Save("rock.png");

        // Probe reads the header alone: the surfaces at mip 0 are its images.
        RasterInfo info = Raster.Probe("rock.dds", context);
        Console.WriteLine($"{info.Width}×{info.Height}, {info.ImageCount} surface(s)");
        #endregion

        Assert.Equal((64, 1), (texture.Width, info.ImageCount));
    }

    [Fact]
    public void DdsDetailsSample()
    {
        using SampleFiles files = new();
        WriteTexture("rock.dds", faces: 1, size: 64);
        EgamiContext context = EgamiContext.Default.WithDds();

        #region dds-details
        // What the header says: the DXGI format, the mip count, the dimension, cube and array layout and the alpha mode.
        using Raster texture = Raster.Load("rock.dds", context);
        if (texture.Metadata.FormatDetails is DdsDetails dds)
        {
            Console.WriteLine($"{dds.FormatName} (DXGI {dds.DxgiFormat}), {dds.MipLevels} mips, {dds.Dimension}, cube: {dds.IsCube}");
            Console.WriteLine($"alpha {dds.AlphaMode}, sRGB {dds.IsSrgb}, DX10 header {dds.Dx10Header}");
        }

        // An _SRGB format is tagged sRGB; any other loads its values as stored.
        bool srgb = texture.Metadata.ColorSpace == ColorSpaceHint.Srgb;
        #endregion

        DdsDetails details = Assert.IsType<DdsDetails>(texture.Metadata.FormatDetails);
        Assert.Equal((7, DdsDimension.Texture2D, DdsAlphaMode.Unknown), (details.MipLevels, details.Dimension, details.AlphaMode));
        Assert.False(srgb);
    }

    [Fact]
    public void DdsSurfaces()
    {
        using SampleFiles files = new();
        WriteTexture("sky.dds", faces: 6, size: 32);
        EgamiContext context = EgamiContext.Default.WithDds();

        #region dds-surfaces
        // One surface and mip: cube face -Y (faces go +X, -X, +Y, -Y, +Z, -Z), third mip.
        using Raster face = Raster.Load("sky.dds", context, new DdsReaderOptions { Surface = 3, MipLevel = 2 });

        // Every face at mip 0, or with every mip after its face.
        using RasterList faces = Raster.LoadAll("sky.dds", context);
        using RasterList all = Raster.LoadAll("sky.dds", context, new DdsReaderOptions { IncludeMipLevels = true });

        // A thumbnail reads the smallest mip that still covers the size, a fraction of the file.
        using Raster thumbnail = Raster.Load("sky.dds", context, new ReaderOptions { MinimumSize = new Size(8, 8) });

        // Two-channel normal maps (BC5, R8G8) load with blue 0, or with Z rebuilt.
        using Raster normals = Raster.Load("sky.dds", context, new DdsReaderOptions { ReconstructNormalZ = true });
        #endregion

        Assert.Equal((8, 6, 36, 8), (face.Width, faces.Count, all.Count, thumbnail.Width));
        Assert.Equal(3, ((DdsDetails)face.Metadata.FormatDetails!).Surface);
        Assert.Equal(32, normals.Width);
    }

    /// <summary>Writes an R8G8B8A8 DDS of <paramref name="faces"/> faces (6 makes a cube map) with a full mip chain.</summary>
    private static void WriteTexture(string path, int faces, int size)
    {
        int mips = (int)Math.Log2(size) + 1;
        using MemoryStream file = new();
        byte[] header = new byte[148];
        System.Text.Encoding.ASCII.GetBytes("DDS ").CopyTo(header, 0);
        BitConverter.GetBytes(124).CopyTo(header, 4);
        BitConverter.GetBytes(0x2100F).CopyTo(header, 8);
        BitConverter.GetBytes(size).CopyTo(header, 12);
        BitConverter.GetBytes(size).CopyTo(header, 16);
        BitConverter.GetBytes(mips).CopyTo(header, 28);
        BitConverter.GetBytes(32).CopyTo(header, 76);
        BitConverter.GetBytes(4).CopyTo(header, 80);
        System.Text.Encoding.ASCII.GetBytes("DX10").CopyTo(header, 84);
        BitConverter.GetBytes(28).CopyTo(header, 128);
        BitConverter.GetBytes(3).CopyTo(header, 132);
        BitConverter.GetBytes(faces == 6 ? 4 : 0).CopyTo(header, 136);
        BitConverter.GetBytes(faces == 6 ? 1 : faces).CopyTo(header, 140);
        file.Write(header);
        using Raster<Rgba8> duck = Raster.Load<Rgba8>("duck.png");
        for (int f = 0; f < faces; f++)
        {
            for (int m = 0; m < mips; m++)
            {
                int side = size >> m;
                using Raster<Rgba8> level = duck.Derive(x => x.Resize(side, side));
                for (int y = 0; y < side; y++)
                {
                    file.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(level.Row(y)));
                }
            }
        }

        File.WriteAllBytes(path, file.ToArray());
    }

    private static byte[] SavePcx(Raster image)
    {
        using MemoryStream output = new();
        image.Save(output, PcxFormat.Info);
        return output.ToArray();
    }
}
