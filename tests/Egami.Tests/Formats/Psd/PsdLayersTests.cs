using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

using Egami.Formats;
using Egami.Formats.Psd;
using Egami.Pixels;

namespace Egami.Tests.Formats.Psd;

/// <summary>ReadLayers: layer pixels, masks, groups and damage.</summary>
public class PsdLayersTests(ITestOutputHelper output)
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

    public static TheoryData<string> FlattenedFiles() =>
        [.. Manifest().RootElement.EnumerateObject().Where(p => p.Value.TryGetProperty("flat", out JsonElement f) && f.GetString() is "psd-tools" or "own").Select(p => p.Name).Order(StringComparer.Ordinal)];

    /// <summary>
    /// The flattened layers against psd-tools' composite, or for the files psd-tools composites otherwise than Photoshop, against
    /// Egami's model written out in numpy: straight RGBA, alpha everywhere, color where alpha is not 0.
    /// </summary>
    [Theory]
    [MemberData(nameof(FlattenedFiles))]
    public void FlattenMatchesTheReference(string name)
    {
        float[] expected = MemoryMarshal.Cast<byte, float>(Gunzip(Path.Combine(PsdReaderTests.References, name + ".flat.f32.gz"))).ToArray();
        using PsdDocument document = PsdImage.ReadLayers(File(name));
        using Raster flat = document.Flatten();
        using Raster<RgbaF32> mine = flat.As<RgbaF32>();
        (float peak, int at, int over) = Peak(expected, mine, 1.5f / 255);
        output.WriteLine($"{name}: peak {peak * 255:F2} levels at pixel {at}, {over} pixels over 1.5 levels; warnings {string.Join(", ", flat.Metadata.Warnings.Select(w => w.Id))}");
        Assert.DoesNotContain(flat.Metadata.Warnings, w => w.Id != "Psd.NoComposite");
        Assert.True(
            over <= Outliers(name),
            $"{name}: {peak * 255:F2} levels from the reference at ({at % document.Width}, {at / document.Width}): {mine.Row(at / document.Width)[at % document.Width]} against ({expected[4 * at]}, {expected[(4 * at) + 1]}, {expected[(4 * at) + 2]}, {expected[(4 * at) + 3]}).");
    }

    public static TheoryData<string> InkFiles() =>
        [.. Manifest().RootElement.EnumerateObject().Where(p => p.Value.TryGetProperty("flat", out JsonElement f) && f.GetString() == "ink").Select(p => p.Name).Order(StringComparer.Ordinal)];

    /// <summary>CMYK layers flattened onto inks against the ink math written out: straight inks and alpha, within a level.</summary>
    [Theory]
    [MemberData(nameof(InkFiles))]
    public void FlattenInkMatchesTheInkMath(string name)
    {
        float[] expected = MemoryMarshal.Cast<byte, float>(Gunzip(Path.Combine(PsdReaderTests.References, name + ".flat.f32.gz"))).ToArray();
        using PsdDocument document = PsdImage.ReadLayers(File(name));
        using InkRaster ink = document.FlattenInk();
        float full = ink.Depth == InkDepth.Unorm8 ? 255 : 65535, peak = 0;
        for (int y = 0; y < ink.Height; y++)
        {
            for (int x = 0; x < ink.Width; x++)
            {
                int i = 5 * ((y * ink.Width) + x);
                float a = Value(ink.Alpha, x, y) / full;
                peak = Math.Max(peak, MathF.Abs(a - expected[i + 4]));
                for (int p = 0; p < 4; p++)
                {
                    peak = Math.Max(peak, MathF.Abs((Value(ink.Plates[p], x, y) / full) - (expected[i + p] * expected[i + 4])));
                }
            }
        }

        Assert.True(peak * 255 <= 1.01, $"{name}: {peak * 255:F2} levels from the ink math.");
        using Raster flat = document.Flatten();
        // The background is opaque, so Flatten gives the inks as Load gives CMYK.
        Assert.Equal(ink.Depth == InkDepth.Unorm8 ? Cmyk8.Layout : Cmyk16.Layout, flat.Layout);

        static int Value(InkPlate plate, int x, int y) => plate.Depth == InkDepth.Unorm8 ? plate.Row8(y)[x] : plate.Row16(y)[x];
    }

    /// <summary>Gets how many pixels may be more than 1.5 levels from psd-tools: none, but where psd-tools is known to differ.</summary>
    private static int Outliers(string name) => name switch
    {
        // psd-tools rounds Hard Mix's ties (Cb + Cs = 1) down; Photoshop gives 1 there, as Egami does.
        "layered-mode-hardmix.psd" => 12,

        // psd-tools (aggdraw) antialiases the vector mask's edge about half a pixel off Egami's, which puts 0, 0 at the
        // canvas's corner; inside and outside the shape they agree.
        "layered-fill.psd" => 200,
        _ => 0,
    };

    internal static (float Peak, int At, int Over) Peak(float[] expected, Raster<RgbaF32> mine, float tolerance)
    {
        float peak = 0;
        int where = -1, over = 0;
        for (int y = 0; y < mine.Height; y++)
        {
            Span<RgbaF32> row = mine.Row(y);
            for (int x = 0; x < mine.Width; x++)
            {
                int i = 4 * ((y * mine.Width) + x);
                RgbaF32 p = row[x];
                float a = MathF.Abs(p.A - expected[i + 3]);
                float c = Math.Min(p.A, expected[i + 3]) > 0.5f / 255
                    ? Math.Max(MathF.Abs(p.R - expected[i]), Math.Max(MathF.Abs(p.G - expected[i + 1]), MathF.Abs(p.B - expected[i + 2]))) * Math.Min(p.A, expected[i + 3])
                    : 0;
                over += Math.Max(a, c) > tolerance ? 1 : 0;
                if (Math.Max(a, c) > peak)
                {
                    peak = Math.Max(a, c);
                    where = (y * mine.Width) + x;
                }
            }
        }

        return (peak, where, over);
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

    /// <summary>Load flattens a file without a real composite by default, as ReadLayers and Flatten do, and Probe agrees.</summary>
    [Fact]
    public void LoadFlattensWhenThereIsNoRealComposite()
    {
        string path = File("layered-nocomposite.psd");
        using PsdDocument document = PsdImage.ReadLayers(path);
        using Raster flat = document.Flatten();
        using Raster loaded = Raster.Load(path);
        RasterInfo info = Raster.Probe(path);
        Assert.Equal(info.Layout, loaded.Layout);
        Assert.Equal(RasterAccess.Buffered, info.Access);
        Assert.DoesNotContain(loaded.Metadata.Warnings, w => w.Id == "Psd.NoComposite");
        using Raster<Rgba8> a = flat.As<Rgba8>(), b = loaded.As<Rgba8>();
        for (int y = 0; y < a.Height; y++)
        {
            Assert.True(a.Row(y).SequenceEqual(b.Row(y)), $"row {y}");
        }

        using NonSeekableStream stream = new(System.IO.File.ReadAllBytes(path));
        using Raster<Rgba8> streamed = Raster.Load<Rgba8>(stream);
        Assert.True(streamed.Row(20).SequenceEqual(b.Row(20)));

        using Raster stored = Raster.Load(path, EgamiContext.Default, new PsdReaderOptions { Composite = PsdComposite.Stored });
        Assert.Contains(stored.Metadata.Warnings, w => w.Id == "Psd.NoComposite");
        Assert.Equal(new Rgb8(255, 255, 255), ((Raster<Rgb8>)stored).Row(0)[0]);
    }

    /// <summary>PsdComposite.Flatten flattens a file with a real composite too; a Lab file gives its stored composite with a warning.</summary>
    [Fact]
    public void FlattenOnRequest()
    {
        PsdReaderOptions flatten = new() { Composite = PsdComposite.Flatten };
        using PsdDocument document = PsdImage.ReadLayers(File("layered-rgb16.psd"));
        using Raster mine = document.Flatten();
        using Raster loaded = Raster.Load(File("layered-rgb16.psd"), EgamiContext.Default, flatten);
        Assert.Equal(Raster.Probe(File("layered-rgb16.psd"), EgamiContext.Default, flatten).Layout, loaded.Layout);
        using Raster<Rgba16> a = mine.As<Rgba16>(), b = loaded.As<Rgba16>();
        Assert.True(a.Row(30).SequenceEqual(b.Row(30)));

        using Raster lab = Raster.Load(File("layered-lab8.psd"), EgamiContext.Default, flatten);
        Assert.Contains(lab.Metadata.Warnings, w => w.Id == "Psd.FlattenUnsupported");
        Assert.Throws<ArgumentOutOfRangeException>(() => new PsdReaderOptions { Composite = (PsdComposite)7 });
    }

    [Fact]
    public void FlattenUsesTheLayersAsChanged()
    {
        using PsdDocument document = PsdImage.ReadLayers(File("layered-rgb8.psd"));
        foreach (PsdLayerImage layer in document.Layers.Where(l => l.Info.Name != "Background"))
        {
            layer.Info = layer.Info with { IsVisible = false };
        }

        using Raster flat = document.Flatten();
        using Raster<Rgba8> only = flat.As<Rgba8>();
        using Raster<Rgba8> background = document.Layers[0].Pixels!.As<Rgba8>();
        for (int y = 0; y < only.Height; y++)
        {
            Assert.True(only.Row(y).SequenceEqual(background.Row(y)), $"row {y}");
        }

        Assert.Throws<ArgumentNullException>(() => document.Layers[0].Info = null!);
    }

    [Fact]
    public void FlattenRefusesLabAndADisposedDocument()
    {
        PsdDocument lab = PsdImage.ReadLayers(File("layered-lab8.psd"));
        Assert.Throws<UnsupportedFormatException>(() => lab.Flatten());
        lab.Dispose();
        Assert.Throws<ObjectDisposedException>(() => lab.Flatten());
    }

    /// <summary>A vector mask's coverage adds up to its shape's area, and the inside is full and the outside empty.</summary>
    [Fact]
    public void VectorMasksCoverTheirArea()
    {
        using PsdDocument document = PsdImage.ReadLayers(File("layered-fill.psd"));
        PsdLayerRecord shape = document.Layers.Single(l => l.Info.Name == "Shape").Record;
        float[] plane = PsdVectorMask.Rasterize(shape.Blocks["vmsk"], document.Width, document.Height)!;

        // The triangle (10, 6), (54, 14), (24, 42): half the cross product of two sides.
        double area = Math.Abs(((54 - 10) * (42 - 6)) - ((24 - 10) * (14 - 6))) / 2.0;
        Assert.Equal(area, plane.Take(64 * 48).Sum(v => (double)v), area * 0.005);
        Assert.Equal(1f, plane[(20 * 64) + 30]);
        Assert.Equal(0f, plane[(40 * 64) + 60]);
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
