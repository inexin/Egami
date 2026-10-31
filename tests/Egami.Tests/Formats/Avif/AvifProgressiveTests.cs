using Egami.Formats.Avif;
using Egami.Pixels;
using Egami.Streaming;

namespace Egami.Tests.Formats.Avif;

/// <summary>
/// Progressive (layered) AVIF while its bytes arrive (AVIF extras design, question 3): RenderPreview draws each layer at full size
/// as it arrives, the last preview is the image Load gives, and every other AVIF waits for the last byte.
/// </summary>
public class AvifProgressiveTests
{
    private static readonly string Corpus = Path.Combine(AppContext.BaseDirectory, "images", "avif");

    [Theory]
    [InlineData("duck-progressive.avif", 2)]
    [InlineData("duck-layered-3.avif", 3)]
    public void APreviewShowsEachLayerAsItArrives(string file, int layers)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Corpus, file));
        using Raster<Rgb8> expected = Raster.Load<Rgb8>(data);
        using RasterDecoder decoder = RasterDecoder.OpenIncremental();
        Raster<Rgb8>? preview = null;
        List<Raster<Rgb8>> distinct = [];
        try
        {
            for (int at = 0; at < data.Length; at += 211)
            {
                decoder.Append(data.AsSpan(at, Math.Min(211, data.Length - at)));
                if (decoder.HeaderRead)
                {
                    Assert.Equal((expected.Width, expected.Height), (decoder.Info.Width, decoder.Info.Height));
                    preview ??= Raster.Create<Rgb8>(decoder.Info.Width, decoder.Info.Height);
                    if (decoder.RenderPreview(preview) && (distinct.Count == 0 || !Same(distinct[^1], preview)))
                    {
                        distinct.Add(preview.Copy());
                    }
                }
            }

            // The header arrives before the first layer, and each layer shows on its own at full size.
            Assert.NotNull(preview);
            decoder.EndOfInput();
            Assert.True(decoder.RenderPreview(preview));
            if (!Same(distinct[^1], preview))
            {
                distinct.Add(preview.Copy());
            }

            Assert.Equal(layers, distinct.Count);
            for (int layer = 0; layer < layers; layer++)
            {
                using Raster<Rgb8> loaded = Raster.Load<Rgb8>(data, options: new AvifReaderOptions { Layer = layer });
                Assert.True(Same(loaded, distinct[layer]), $"layer {layer}");
            }

            // Rows read after previews are the image Load gives.
            using Raster<Rgb8> frame = Raster.Create<Rgb8>(decoder.Info.Width, decoder.Info.Height);
            Assert.NotNull(decoder.ReadFrame(frame));
            Assert.True(Same(expected, frame));
            Assert.Equal(layers, ((AvifDetails)decoder.Metadata.FormatDetails!).Layers.Count);
        }
        finally
        {
            preview?.Dispose();
            distinct.ForEach(r => r.Dispose());
        }
    }

    [Fact]
    public void AStillThatIsNotLayeredWaitsForTheEnd()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Corpus, "duck-420-q60.avif"));
        using RasterDecoder decoder = RasterDecoder.OpenIncremental();
        decoder.Append(data.AsSpan(0, data.Length - 1));
        Assert.False(decoder.HeaderRead);
        decoder.Append(data.AsSpan(data.Length - 1));
        decoder.EndOfInput();
        Assert.True(decoder.HeaderRead);
        using Raster<Rgb8> frame = Raster.Create<Rgb8>(decoder.Info.Width, decoder.Info.Height);
        Assert.False(decoder.RenderPreview(frame));
        Assert.NotNull(decoder.ReadFrame(frame));
        using Raster<Rgb8> expected = Raster.Load<Rgb8>(data);
        Assert.True(Same(expected, frame));
    }

    [Fact]
    public void ACutFileFailsAtTheEnd()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Corpus, "duck-layered-3.avif"));
        using RasterDecoder decoder = RasterDecoder.OpenIncremental();
        decoder.Append(data.AsSpan(0, data.Length - 100));
        Assert.True(decoder.HeaderRead);
        using Raster<Rgb8> frame = Raster.Create<Rgb8>(decoder.Info.Width, decoder.Info.Height);
        Assert.True(decoder.RenderPreview(frame));
        decoder.EndOfInput();
        Assert.ThrowsAny<EgamiException>(() => decoder.ReadFrame(frame));
    }

    internal static bool Same<TPixel>(Raster<TPixel> a, Raster<TPixel> b)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if ((a.Width, a.Height) != (b.Width, b.Height))
        {
            return false;
        }

        for (int y = 0; y < a.Height; y++)
        {
            if (!a.Row(y).SequenceEqual(b.Row(y)))
            {
                return false;
            }
        }

        return true;
    }
}
