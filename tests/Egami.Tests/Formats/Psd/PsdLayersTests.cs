using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

using Egami.Formats;
using Egami.Formats.Psd;
using Egami.Pixels;

namespace Egami.Tests.Formats.Psd;

/// <summary>ReadLayers: layer pixels, masks, groups and damage.</summary>
public class PsdLayersTests
{
    private static string File(string name) => Path.Combine(PsdReaderTests.Images, name);

    public static TheoryData<string> LayeredFiles() => [.. Manifest().RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)];

    internal static JsonDocument Manifest() => JsonDocument.Parse(System.IO.File.ReadAllBytes(Path.Combine(PsdReaderTests.References, "layered.json")));

    internal static byte[] Gunzip(string path)
    {
        using GZipStream zip = new(System.IO.File.OpenRead(path), CompressionMode.Decompress);
        using MemoryStream bytes = new();
        zip.CopyTo(bytes);
        return bytes.ToArray();
    }

    [Theory]
    [MemberData(nameof(LayeredFiles))]
    public void LayerPixelsAreTheWrittenOnes(string name)
    {
        using JsonDocument manifest = Manifest();
        JsonElement expected = manifest.RootElement.GetProperty(name).GetProperty("layers");
        byte[] truth = Gunzip(Path.Combine(PsdReaderTests.References, name + ".layerpixels.raw.gz"));
        using PsdDocument document = PsdImage.ReadLayers(File(name));
        Assert.DoesNotContain(document.Metadata.Warnings, w => w.Id != "Psd.NoComposite");
        int at = 0;
        foreach (JsonElement entry in expected.EnumerateArray())
        {
            PsdLayerImage layer = document.Layers.Single(l => l.Info.Name == entry.GetProperty("name").GetString());
            Assert.Equal((entry.GetProperty("x").GetInt32(), entry.GetProperty("y").GetInt32()), (layer.X, layer.Y));
            byte[] actual = Bytes(layer);
            Assert.Equal(entry.GetProperty("layout").GetString(), Layout(layer));
            Assert.True(truth.AsSpan(at, actual.Length).SequenceEqual(actual), $"{name}: layer {layer.Info.Name} differs.");
            at += actual.Length;
        }

        Assert.Equal(truth.Length, at);
    }

    private static string Layout(PsdLayerImage layer) => layer.Ink is InkRaster ink
        ? (ink.Depth == InkDepth.Unorm8 ? "Ink8" : "Ink16")
        : layer.Pixels!.GetType().GetGenericArguments()[0].Name;

    /// <summary>Returns a layer's pixels as little-endian bytes, an ink raster's planes one after another, alpha last.</summary>
    private static byte[] Bytes(PsdLayerImage layer)
    {
        List<byte> bytes = [];
        if (layer.Ink is InkRaster ink)
        {
            foreach (InkPlate plate in ink.Plates.Append(ink.Alpha))
            {
                for (int y = 0; y < ink.Height; y++)
                {
                    bytes.AddRange(ink.Depth == InkDepth.Unorm8 ? plate.Row8(y).ToArray() : MemoryMarshal.AsBytes(plate.Row16(y)).ToArray());
                }
            }

            return [.. bytes];
        }

        return layer.Pixels switch
        {
            Raster<Rgba8> r => Of(r),
            Raster<Rgba16> r => Of(r),
            Raster<RgbaF32> r => Of(r),
            Raster<GrayAlpha8> r => Of(r),
            Raster<GrayAlpha16> r => Of(r),
            _ => throw new InvalidOperationException(),
        };

        static byte[] Of<TPixel>(Raster<TPixel> raster)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            List<byte> rows = [];
            for (int y = 0; y < raster.Height; y++)
            {
                rows.AddRange(MemoryMarshal.AsBytes(raster.Row(y)).ToArray());
            }

            return [.. rows];
        }
    }

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
