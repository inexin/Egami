using Egami.Formats;
using Egami.Formats.Psd;
using Egami.Pixels;

namespace Egami.Tests.Formats.Psd;

/// <summary>ReadLayers: layer pixels, masks, groups and damage.</summary>
public class PsdLayersTests
{
    private static string File(string name) => Path.Combine(PsdReaderTests.Images, name);

    [Fact]
    public void ReadsEveryLayerWithItsPixelsAndParent()
    {
        using PsdDocument document = PsdImage.ReadLayers(File("layers.psd"));
        Assert.Equal((64, 48, PsdColorMode.Rgb, 8), (document.Width, document.Height, document.ColorMode, document.BitsPerChannel));
        Assert.Equal(["Background", "Inner", "Dyp æøå 深", "Clipped", "Nested", "Group", "Top — ü"], document.Layers.Select(l => l.Info.Name));
        Assert.Equal([-1, 5, 4, 4, 5, -1, -1], document.Layers.Select(l => l.ParentIndex));
        Assert.Equal([0, 1, 2, 3, 4, 5, 6], document.Layers.Select(l => l.Index));
        PsdLayerImage inner = document.Layers[1];
        Raster<Rgba8> pixels = Assert.IsType<Raster<Rgba8>>(inner.Pixels);
        Assert.Equal((3, 2, 6, 4), (inner.X, inner.Y, pixels.Width, pixels.Height));
        Assert.All(Enumerable.Range(0, 4).SelectMany(y => pixels.Row(y).ToArray()), p => Assert.Equal(new Rgba8(20, 20, 20, 255), p));
        Assert.Null(document.Layers[4].Pixels);
        Assert.Null(inner.Mask);
    }

    [Fact]
    public void ConvertsToTheAskedType()
    {
        using PsdDocument document = PsdImage.ReadLayers<RgbaF32>(File("layers.psd"));
        Raster<RgbaF32> pixels = Assert.IsType<Raster<RgbaF32>>(document.Layers[0].Pixels);
        Assert.Equal(new RgbaF32(10 / 255f, 10 / 255f, 10 / 255f, 1), pixels.Row(0)[0]);
    }

    [Fact]
    public void ReadsFromAStreamThatCannotSeek()
    {
        using NonSeekableStream stream = new(System.IO.File.ReadAllBytes(File("many-layers.psd")));
        using PsdDocument document = PsdImage.ReadLayers(stream);
        Assert.Equal(300, document.Layers.Count);
        Assert.Equal(new Rgba8(7, 7, 7, 255), ((Raster<Rgba8>)document.Layers[7].Pixels!).Row(0)[0]);
    }

    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;

        public override long Position { get => base.Position; set => throw new NotSupportedException(); }

        public override long Length => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();
    }
}
