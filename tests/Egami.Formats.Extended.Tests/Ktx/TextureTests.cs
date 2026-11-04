using Egami.Formats.Extended.Dds;
using Egami.Formats.Extended.Ktx;
using Egami.Formats.Extended.Textures;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Formats.Extended.Tests.Ktx;

/// <summary>The texture object model: loading every image of DDS, KTX and KTX 2, editing them, and saving with their own mips.</summary>
public class TextureTests
{
    private static EgamiContext Context => TestImages.Context;

    [Theory]
    [InlineData("rgba8-cube.ktx2", 1, 6, 1)]
    [InlineData("rgba8-array.ktx2", 3, 1, 1)]
    [InlineData("rgba8-srgb-mips.ktx2", 1, 1, 1)]
    [InlineData("rgba8-volume.ktx2", 1, 1, 3)]
    [InlineData("cube.ktx", 1, 6, 1)]
    [InlineData("array.ktx", 3, 1, 1)]
    public void LoadsEveryImageAndSavesThemBack(string name, int layers, int faces, int depth)
    {
        using Texture texture = Texture.Load(KtxFiles.Corpus(name), Context);
        Assert.Equal((layers, faces, depth), (texture.Layers, texture.Faces, texture.Depth));
        Assert.Equal(faces == 6, texture.IsCube);

        // Every image LoadAll gives is somewhere in the texture, at its mip's size.
        using RasterList all = Raster.LoadAll(KtxFiles.Corpus(name), Context, new KtxReaderOptions { IncludeMipLevels = true });
        Assert.Equal(all.Count, Enumerable.Range(0, texture.MipLevels).Sum(m => texture.Layers * texture.Faces * texture.MipDepth(m)));

        // KTX 2 keeps every layout; DDS every one but an array of volumes.
        foreach (FormatInfo format in (FormatInfo[])[Ktx2Format.Info, DdsFormat.Info])
        {
            using MemoryStream saved = new();
            texture.Save(saved, format, format == DdsFormat.Info ? new DdsWriterOptions { Format = DdsTextureFormat.Rgba8 } : new Ktx2WriterOptions { Format = KtxTextureFormat.Rgba8 });
            using Texture back = Texture.Load(saved.ToArray(), Context);
            Assert.Equal((texture.Layers, texture.Faces, texture.Depth, texture.MipLevels), (back.Layers, back.Faces, back.Depth, back.MipLevels));
            for (int m = 0; m < texture.MipLevels; m++)
            {
                for (int layer = 0; layer < texture.Layers; layer++)
                {
                    for (int face = 0; face < texture.Faces; face++)
                    {
                        for (int slice = 0; slice < texture.MipDepth(m); slice++)
                        {
                            using Raster<Rgba8> a = texture.GetImage(m, layer, face, slice).As<Rgba8>();
                            using Raster<Rgba8> b = back.GetImage(m, layer, face, slice).As<Rgba8>();
                            TestImages.AssertEqual(a, b);
                        }
                    }
                }
            }
        }
    }

    [Fact]
    public void LoadsDdsCubeMapsWithMips()
    {
        Raster<Rgba8>[] faces = [.. Enumerable.Range(0, 6).Select(i => Raster.Create<Rgba8>(8, 8, new Rgba8((byte)(i * 40), 0, 0, 255)))];
        using MemoryStream cube = new();
        Raster.SaveAll(faces, cube, DdsFormat.Info, new DdsWriterOptions { Cube = true, Format = DdsTextureFormat.Bc1 });
        foreach (Raster<Rgba8> face in faces)
        {
            face.Dispose();
        }

        using Texture texture = Texture.Load(new MemoryStream(cube.ToArray()), Context);
        Assert.Equal((true, 1, 4), (texture.IsCube, texture.Layers, texture.MipLevels));
        for (int face = 0; face < 6; face++)
        {
            using Raster<Rgba8> smallest = texture.GetImage(3, 0, face).As<Rgba8>();
            Assert.Equal((1, 1), (smallest.Width, smallest.Height));
            Assert.InRange(smallest.Row(0)[0].R, (face * 40) - 4, (face * 40) + 4);
        }
    }

    [Fact]
    public void SavesItsOwnMips()
    {
        // A mip edited by hand is written as it is, not made again from mip 0.
        using Texture texture = Texture.FromSurfaces([Raster.Create<Rgba8>(8, 8, new Rgba8(200, 200, 200, 255))]);
        texture.GenerateMips();
        Assert.Equal(4, texture.MipLevels);
        texture.SetImage(Raster.Create<Rgba8>(4, 4, new Rgba8(10, 20, 30, 255)), mip: 1);
        foreach (FormatInfo format in (FormatInfo[])[Ktx2Format.Info, DdsFormat.Info])
        {
            using MemoryStream saved = new();
            texture.Save(saved, format);
            using Texture back = Texture.Load(saved.ToArray(), Context);
            Assert.Equal(4, back.MipLevels);
            using Raster<Rgba8> mip1 = back.GetImage(1).As<Rgba8>();
            Assert.Equal(new Rgba8(10, 20, 30, 255), mip1.Row(2)[2]);
            using Raster<Rgba8> mip2 = back.GetImage(2).As<Rgba8>();
            Assert.Equal(new Rgba8(200, 200, 200, 255), mip2.Row(1)[1]);
        }

        // The writer's mip count keeps the first so many.
        using MemoryStream two = new();
        texture.Save(two, Ktx2Format.Info, new Ktx2WriterOptions { MipLevels = 2 });
        using Texture trimmed = Texture.Load(two.ToArray(), Context);
        Assert.Equal(2, trimmed.MipLevels);
    }

    [Fact]
    public void MakesAndTrimsMips()
    {
        using Texture texture = Texture.FromSurfaces([.. Enumerable.Range(0, 6).Select(i => (Raster)Raster.Create<Rgba8>(13, 13, new Rgba8(0, (byte)(i * 30), 0, 255)))], cube: true);
        texture.GenerateMips(colorSpace: ColorSpace.Linear);
        Assert.Equal(4, texture.MipLevels);
        Assert.Equal([13, 6, 3, 1], Enumerable.Range(0, 4).Select(texture.MipWidth));
        using Raster<Rgba8> smallest = texture.GetImage(3, 0, 5).As<Rgba8>();
        Assert.Equal(150, smallest.Row(0)[0].G);
        texture.GenerateMips(2);
        Assert.Equal(2, texture.MipLevels);
        texture.TrimMips(1);
        Assert.Equal(1, texture.MipLevels);

        Assert.Throws<ArgumentException>(() => texture.SetImage(Raster.Create<Rgba8>(4, 4)));
        Assert.Throws<ArgumentOutOfRangeException>(() => texture.GetImage(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => texture.GetImage(0, 0, 6));
        Assert.Throws<ArgumentOutOfRangeException>(() => texture.GetImage(1));
        Assert.Throws<ArgumentException>(() => Texture.FromSurfaces([Raster.Create<Rgba8>(4, 4)], cube: true));
        Assert.Throws<ArgumentException>(() => Texture.FromSurfaces([]));
    }

    [Fact]
    public void WritesVolumes()
    {
        using Texture volume = Texture.FromSlices([.. Enumerable.Range(0, 4).Select(i => (Raster)Raster.Create<Rgba8>(4, 4, new Rgba8((byte)(i * 60), 0, 0, 255)))]);
        Assert.Throws<NotSupportedException>(() => volume.GenerateMips());
        foreach (FormatInfo format in (FormatInfo[])[Ktx2Format.Info, DdsFormat.Info])
        {
            using MemoryStream saved = new();
            volume.Save(saved, format);
            using Texture back = Texture.Load(saved.ToArray(), Context);
            Assert.Equal((true, 4, 1), (back.IsVolume, back.Depth, back.MipLevels));
            using Raster<Rgba8> slice = back.GetImage(0, slice: 3).As<Rgba8>();
            Assert.Equal(180, slice.Row(0)[0].R);
        }

        Assert.Throws<NotSupportedException>(() => volume.Save(new MemoryStream(), TestImages.Format("PCX")));
        Assert.Throws<NotSupportedException>(() => volume.Save("volume.png"));
        Assert.Throws<UnsupportedFormatException>(() => Texture.Load(new byte[64], Context));
    }
}
