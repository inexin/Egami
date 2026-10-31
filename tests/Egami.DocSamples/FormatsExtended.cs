using Egami.Formats;
using Egami.Formats.Extended;
using Egami.Formats.Extended.Dds;
using Egami.Formats.Extended.Pcx;
using Egami.Formats.Extended.Raw;
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

    [Fact]
    public void DdsSave()
    {
        using SampleFiles files = new();
        WriteTexture("sky.dds", faces: 6, size: 32);
        EgamiContext context = EgamiContext.Default.WithDds();
        using Raster image = Raster.Load("duck.png");

        #region dds-save
        // A BC3 texture with its full mip chain: the format, mip count, effort (0 to 6) and header are options.
        using (FileStream output = File.Create("duck.dds"))
        {
            image.Save(output, DdsFormat.Info, new DdsWriterOptions { Format = DdsTextureFormat.Bc3, Effort = 5 });
        }

        // An sRGB colour texture without mips, in the DX10 header.
        using (FileStream output = File.Create("duck-srgb.dds"))
        {
            image.Save(output, DdsFormat.Info, new DdsWriterOptions { Format = DdsTextureFormat.Bc1, Srgb = true, MipLevels = 1 });
        }

        // Six faces back into a cube map; SaveAll without Cube writes a texture array.
        using RasterList faces = Raster.LoadAll("sky.dds", context);
        using (FileStream output = File.Create("sky-bc1.dds"))
        {
            Raster.SaveAll(faces, output, DdsFormat.Info, new DdsWriterOptions { Cube = true, Format = DdsTextureFormat.Bc1 });
        }
        #endregion

        Assert.True(((DdsDetails)Raster.Load("sky-bc1.dds", context).Metadata.FormatDetails!).IsCube);
        Assert.True(((DdsDetails)Raster.Load("duck.dds", context).Metadata.FormatDetails!).MipLevels > 1);
        Assert.True(((DdsDetails)Raster.Load("duck-srgb.dds", context).Metadata.FormatDetails!).IsSrgb);
    }

    [Fact]
    public void DdsNormalMap()
    {
        using SampleFiles files = new();
        using Raster<Rgb8> normals = Raster.Create<Rgb8>(64, 64, new Rgb8(128, 128, 255));

        #region dds-normal-map
        // BC5 keeps red and green; blue (the normal's Z) is rebuilt by the shader, or on load.
        using (FileStream output = File.Create("normals.dds"))
        {
            normals.Save(output, DdsFormat.Info, new DdsWriterOptions { Format = DdsTextureFormat.Bc5 });
        }

        EgamiContext context = EgamiContext.Default.WithDds();
        using Raster<Rgb8> back = Raster.Load<Rgb8>("normals.dds", context, new DdsReaderOptions { ReconstructNormalZ = true });
        #endregion

        Assert.Equal(new Rgb8(128, 128, 255), back.Row(10)[10]);
    }

    [Fact]
    public void DdsHdr()
    {
        using SampleFiles files = new();
        using Raster<RgbaF16> sky = Raster.Create<RgbaF16>(64, 32, new RgbaF16((Half)4.5f, (Half)3f, (Half)1.25f, Half.One));

        #region dds-hdr
        // BC6H keeps half-float colour at 8 bits a pixel; light values above 1 survive.
        using (FileStream output = File.Create("sky-hdr.dds"))
        {
            sky.Save(output, DdsFormat.Info, new DdsWriterOptions { Format = DdsTextureFormat.Bc6h });
        }

        using Raster<RgbaF16> back = Raster.Load<RgbaF16>("sky-hdr.dds", EgamiContext.Default.WithDds());
        #endregion

        Assert.InRange((float)back.Row(5)[5].R, 4.4f, 4.6f);
    }

    /// <summary>Writes an R8G8B8A8 DDS of <paramref name="faces"/> faces (6 makes a cube map) with a full mip chain.</summary>
    [Fact]
    public void RawLoad()
    {
        using SampleFiles files = new();
        WriteDng("photo.dng");

        #region raw-load
        // RAW goes ahead of TIFF, since a DNG is a TIFF file; WithExtendedFormats() registers it too.
        EgamiContext context = EgamiContext.Default.WithRaw();

        // Load develops the sensor data: the camera's white balance and colour, AHD demosaicing, sRGB in Rgb16.
        using Raster photo = Raster.Load("photo.dng", context);
        photo.Save("photo.png");

        // Probe reads the structure alone: the developed size, the camera and the embedded previews.
        RasterInfo info = Raster.Probe("photo.dng", context);
        Console.WriteLine($"{info.Width}×{info.Height}, {info.ImageCount} image(s)");
        #endregion

        Assert.Equal((photo.Width, photo.Height), (info.Width, info.Height));
        Assert.Equal(Rgb16.Layout, photo.Layout);
    }

    [Fact]
    public void RawOutputs()
    {
        using SampleFiles files = new();
        WriteDng("photo.dng");
        EgamiContext context = EgamiContext.Default.WithRaw();

        #region raw-outputs
        // The untouched sensor data for a pipeline of your own: Gray16, one value per photosite.
        using Raster mosaic = Raster.Load("photo.dng", context, new RawReaderOptions { Output = RawOutput.Mosaic });
        RawDetails raw = (RawDetails)mosaic.Metadata.FormatDetails!;
        Console.WriteLine($"{raw.CfaPattern}, black {raw.BlackLevels[0]}, white {raw.WhiteLevels[0]}");

        // Half the size and about four times faster: each 2 × 2 block of photosites is one pixel.
        using Raster<Rgba8> half = Raster.Load<Rgba8>("photo.dng", context, new RawReaderOptions { Output = RawOutput.HalfSize });

        // A thumbnail: MinimumSize takes the smallest embedded preview or half-size develop that covers it.
        using Raster thumb = Raster.Load("photo.dng", context, new ReaderOptions { MinimumSize = new Size(32, 32) });
        #endregion

        Assert.Equal("RGGB", raw.CfaPattern);
        Assert.Equal(Gray16.Layout, mosaic.Layout);
        Assert.True(half.Width * 2 <= mosaic.Width);
        Assert.True(thumb.Width >= 32);
    }

    [Fact]
    public void RawDevelop()
    {
        using SampleFiles files = new();
        WriteDng("photo.dng");
        EgamiContext context = EgamiContext.Default.WithRaw();

        #region raw-develop
        RawReaderOptions develop = new()
        {
            WhiteBalance = RawWhiteBalance.Custom,
            Temperature = 4800,                    // kelvin
            Tint = 5,                              // toward magenta
            Exposure = -0.5,                       // stops
            Highlights = RawHighlightMode.Rebuild, // recover clipped colour from what was not clipped
            Demosaic = RawDemosaicMethod.Malvar,   // faster than the default AHD
            LuminanceNoiseReduction = 20,          // 0 to 100; off by default
            ColorNoiseReduction = 40,              // 0 to 100; 25 by default
            AutoChromaticAberration = true,        // fix colour fringes when no lens profile covers the file
            DefaultToneCurve = true,               // a contrast curve like Adobe Camera Raw's default; off by default
            ConvertColorTo = Colors.ColorProfile.DisplayP3, // rendered straight into P3, the profile attached
        };
        using Raster photo = Raster.Load("photo.dng", context, develop);
        photo.Save("photo-p3.png");
        #endregion

        Assert.True(photo.Metadata.GetColorProfile().IsEquivalentTo(Colors.ColorProfile.DisplayP3));
    }

    [Fact]
    public void RawDetailsSample()
    {
        using SampleFiles files = new();
        WriteDng("photo.dng");
        EgamiContext context = EgamiContext.Default.WithRaw();

        #region raw-details
        using Raster photo = Raster.Load("photo.dng", context);
        RawDetails details = (RawDetails)photo.Metadata.FormatDetails!;
        Console.WriteLine($"{details.Make} {details.Model}, DNG {details.DngVersion}");
        Console.WriteLine($"Sensor {details.SensorWidth}×{details.SensorHeight}, crop {details.CropArea}");
        Console.WriteLine($"White balance {details.Temperature:F0} K, tint {details.Tint:F0}, multipliers {string.Join(", ", details.WhiteBalance)}");
        foreach (RawPreview preview in details.Previews)
        {
            Console.WriteLine($"Preview {preview.Width}×{preview.Height} {preview.Kind}");
        }
        #endregion

        Assert.Equal("Egami", details.Make);
        Assert.Single(details.Previews);
    }

    /// <summary>
    /// Writes a small DNG made from duck.png, from the DNG specification: the duck's pixels sampled through an RGGB colour filter
    /// as 14-bit values, a camera whose RGB is linear sRGB (D65), and a JPEG preview.
    /// </summary>
    private static void WriteDng(string path)
    {
        using Raster<RgbF32> duck = Raster.Load<RgbF32>("duck.png");
        int w = duck.Width & ~1, h = duck.Height & ~1;
        byte[] data = new byte[w * h * 2];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                RgbF32 p = duck.Row(y)[x];
                float encoded = (((y & 1) << 1) | (x & 1)) switch { 0 => p.R, 3 => p.B, _ => p.G };
                double linear = encoded <= 0.04045 ? encoded / 12.92 : Math.Pow((encoded + 0.055) / 1.055, 2.4);
                BitConverter.TryWriteBytes(data.AsSpan(((y * w) + x) * 2), (ushort)(512 + (linear * 15000)));
            }
        }

        using MemoryStream preview = new();
        using (Raster<Rgb8> small = Raster.Load<Rgb8>("duck.png").Derive(r => r.Resize(w / 4, h / 4)))
        {
            small.Save(preview, Egami.Formats.Jpeg.JpegFormat.Info);
        }

        List<(int Tag, int Type, byte[] Value)> ifd0 =
        [
            (254, 4, U32(1)), (256, 4, U32((uint)(w / 4))), (257, 4, U32((uint)(h / 4))), (258, 3, [8, 0, 8, 0, 8, 0]), (259, 3, U16(7)),
            (262, 3, U16(6)), (271, 2, [.. "Egami"u8, 0]), (272, 2, [.. "Sample"u8, 0]), (273, 4, U32(0)), (277, 3, U16(3)),
            (279, 4, U32((uint)preview.Length)), (330, 4, U32(0)), (50706, 1, [1, 4, 0, 0]),
            (50721, 10, SRationals(3.2404542, -1.5371385, -0.4985314, -0.9692660, 1.8760108, 0.0415560, 0.0556434, -0.2040259, 1.0572252)),
            (50728, 5, Rationals(1, 1, 1)), (50778, 3, U16(21)),
        ];
        List<(int Tag, int Type, byte[] Value)> raw =
        [
            (254, 4, U32(0)), (256, 4, U32((uint)w)), (257, 4, U32((uint)h)), (258, 3, U16(16)), (259, 3, U16(1)), (262, 3, U16(32803)),
            (273, 4, U32(0)), (277, 3, U16(1)), (278, 4, U32((uint)h)), (279, 4, U32((uint)data.Length)), (33421, 3, [2, 0, 2, 0]),
            (33422, 1, [0, 1, 1, 2]), (50714, 3, U16(512)), (50717, 3, U16(15512)),
        ];

        // Header, IFD0, the raw IFD, the preview, then the sensor data; the offsets are known once the sizes are.
        int ifd0At = 8, rawAt = ifd0At + IfdSize(ifd0), previewAt = rawAt + IfdSize(raw), dataAt = previewAt + (int)preview.Length;
        Patch(ifd0, 273, U32((uint)previewAt));
        Patch(ifd0, 330, U32((uint)rawAt));
        Patch(raw, 273, U32((uint)dataAt));
        using FileStream file = File.Create(path);
        file.Write([(byte)'I', (byte)'I', 42, 0]);
        file.Write(U32((uint)ifd0At));
        WriteIfd(file, ifd0, ifd0At);
        WriteIfd(file, raw, rawAt);
        file.Write(preview.ToArray());
        file.Write(data);
    }

    private static byte[] U16(ushort v) => BitConverter.GetBytes(v);

    private static byte[] U32(uint v) => BitConverter.GetBytes(v);

    private static byte[] Rationals(params double[] values) => [.. values.SelectMany(v => U32((uint)Math.Round(v * 10000)).Concat(U32(10000)))];

    private static byte[] SRationals(params double[] values) => [.. values.SelectMany(v => BitConverter.GetBytes((int)Math.Round(v * 1000000)).Concat(U32(1000000)))];

    private static void Patch(List<(int Tag, int Type, byte[] Value)> ifd, int tag, byte[] value)
    {
        int i = ifd.FindIndex(e => e.Tag == tag);
        ifd[i] = (tag, ifd[i].Type, value);
    }

    /// <summary>Returns the bytes an IFD takes: its entries, the next-IFD link, and the values that do not fit in an entry.</summary>
    private static int IfdSize(List<(int Tag, int Type, byte[] Value)> ifd) => 2 + (ifd.Count * 12) + 4 + ifd.Sum(e => e.Value.Length > 4 ? (e.Value.Length + 1) & ~1 : 0);

    private static void WriteIfd(Stream file, List<(int Tag, int Type, byte[] Value)> ifd, int at)
    {
        int values = at + 2 + (ifd.Count * 12) + 4;
        file.Write(U16((ushort)ifd.Count));
        List<byte> outside = [];
        foreach ((int tag, int type, byte[] value) in ifd)
        {
            file.Write(U16((ushort)tag));
            file.Write(U16((ushort)type));
            file.Write(U32((uint)(value.Length / TypeSize(type))));
            if (value.Length <= 4)
            {
                byte[] field = new byte[4];
                value.CopyTo(field, 0);
                file.Write(field);
            }
            else
            {
                file.Write(U32((uint)(values + outside.Count)));
                outside.AddRange(value);
                if ((outside.Count & 1) != 0)
                {
                    outside.Add(0);
                }
            }
        }

        file.Write(U32(0));
        file.Write(outside.ToArray());
    }

    private static int TypeSize(int type) => type switch { 3 => 2, 4 => 4, 5 or 10 => 8, _ => 1 };

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
