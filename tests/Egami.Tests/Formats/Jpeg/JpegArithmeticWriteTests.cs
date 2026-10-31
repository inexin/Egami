using Egami.Colors;
using Egami.Formats;
using Egami.Formats.Jpeg;
using Egami.Pixels;
using Egami.Streaming;

namespace Egami.Tests.Formats.Jpeg;

/// <summary>
/// Arithmetic-coded JPEG writing (SOF9 and SOF10): each file decodes to the same pixels as the Huffman file written with the same
/// options, since only the entropy coding differs, and arithmetic JpegTransform output repacks a Huffman file with no change.
/// tools/check-jpeg-writer-interop.py checks the same files with djpeg.
/// </summary>
public class JpegArithmeticWriteTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<int, int, ChromaSubsampling, bool, int> Shapes => new()
    {
        { 1, 1, ChromaSubsampling.Subsample420, false, 0 },
        { 37, 23, ChromaSubsampling.Subsample420, false, 0 },
        { 37, 23, ChromaSubsampling.Subsample422, true, 0 },
        { 64, 48, ChromaSubsampling.Subsample444, false, 3 },
        { 97, 61, ChromaSubsampling.Subsample420, true, 1 },
        { 97, 61, ChromaSubsampling.Subsample420, false, 5 },
        { 200, 130, ChromaSubsampling.Subsample422, true, 7 },
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void ColorMatchesHuffman(int width, int height, ChromaSubsampling sampling, bool progressive, int restart)
    {
        using Raster<Rgb8> source = Noisy(width, height, 1);
        foreach (int quality in (int[])[30, 75, 100])
        {
            JpegWriterOptions options = new() { Quality = quality, Subsampling = sampling, Progressive = progressive, RestartInterval = restart };
            AssertSamePixels<Rgb8>(source, options);
        }
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 3)]
    public void GrayMatchesHuffman(bool progressive, int restart)
    {
        using Raster<Rgb8> color = Noisy(53, 41, 2);
        using Raster<Gray8> source = color.As<Gray8>();
        AssertSamePixels<Gray8>(source, new JpegWriterOptions { Progressive = progressive, RestartInterval = restart, Quality = 90 });
    }

    [Theory]
    [InlineData(JpegColorType.Cmyk, false)]
    [InlineData(JpegColorType.Cmyk, true)]
    [InlineData(JpegColorType.Ycck, false)]
    [InlineData(JpegColorType.Ycck, true)]
    public void CmykMatchesHuffman(JpegColorType type, bool progressive)
    {
        using Raster<Rgb8> color = Noisy(45, 33, 3);
        using Raster<Cmyk8> source = color.As<Cmyk8>();
        AssertSamePixels<Cmyk8>(source, new JpegWriterOptions { ColorType = type, Progressive = progressive, RestartInterval = 2 });
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 4)]
    [InlineData(true, 0)]
    [InlineData(true, 5)]
    public void TwelveBitMatchesHuffman(bool progressive, int restart)
    {
        using Raster<Rgb8> color = Noisy(71, 39, 4);
        using Raster<Rgb16> source = color.As<Rgb16>();
        foreach (int quality in (int[])[10, 95])
        {
            AssertSamePixels<Rgb16>(source, new JpegWriterOptions { Precision = 12, Quality = quality, Progressive = progressive, RestartInterval = restart });
        }
    }

    [Fact]
    public void WritesSof9WithNoHuffmanTables()
    {
        using Raster<Rgb8> source = Noisy(40, 40, 5);
        byte[] sequential = Encode(source, new JpegWriterOptions { Arithmetic = true });
        byte[] progressive = Encode(source, new JpegWriterOptions { Arithmetic = true, Progressive = true });
        Assert.True(Has(sequential, JpegMarker.Sof9));
        Assert.True(Has(progressive, JpegMarker.Sof10));
        Assert.False(Has(sequential, JpegMarker.Dht) || Has(progressive, JpegMarker.Dht));
        Assert.False(Has(sequential, JpegMarker.Dac) || Has(progressive, JpegMarker.Dac));

        using Raster loaded = Raster.Load(sequential);
        JpegDetails details = Assert.IsType<JpegDetails>(loaded.Metadata.FormatDetails);
        Assert.True(details.Arithmetic);
    }

    [Fact]
    public void ArithmeticIsSmallerThanOptimizedHuffman()
    {
        using Raster<Rgb8> source = Noisy(256, 192, 6);
        int huffman = Encode(source, new JpegWriterOptions { OptimizeHuffman = true }).Length;
        int arithmetic = Encode(source, new JpegWriterOptions { Arithmetic = true }).Length;
        Assert.True(arithmetic < huffman, $"arithmetic {arithmetic} bytes, optimized Huffman {huffman}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StreamedEncodeMatchesWhole(bool progressive)
    {
        using Raster<Rgb8> source = Noisy(83, 45, 7);
        JpegWriterOptions options = new() { Arithmetic = true, Progressive = progressive, RestartInterval = 3 };
        byte[] whole = Encode(source, options);
        using MemoryStream memory = new();
        using (RasterEncoder<Rgb8> encoder = RasterEncoder.Create<Rgb8>(memory, JpegFormat.Info, source.Width, source.Height, options))
        {
            using Raster<Rgb8> band = Raster.Create<Rgb8>(source.Width, 5);
            for (int y = 0; y < source.Height; y += 5)
            {
                int rows = Math.Min(5, source.Height - y);
                for (int r = 0; r < rows; r++)
                {
                    source.Row(y + r).CopyTo(band.Row(r));
                }

                encoder.WriteRows(band, rows);
            }

            encoder.Complete();
        }

        Assert.Equal(whole, memory.ToArray());
    }

    [Fact]
    public void ReSavingWritesHuffman()
    {
        using Raster<Rgb8> source = Noisy(32, 32, 8);
        using Raster loaded = Raster.Load(Encode(source, new JpegWriterOptions { Arithmetic = true }));
        byte[] again = Encode(loaded, JpegWriterOptions.Default);
        Assert.True(Has(again, JpegMarker.Sof0));
        Assert.False(Has(again, JpegMarker.Sof9));
    }

    [Fact]
    public void GainMapOptionThrows()
    {
        using Raster<RgbaF32> hdr = Raster.Create<RgbaF32>(16, 16);
        using MemoryStream stream = new();
        Assert.Throws<NotSupportedException>(() => hdr.Save(stream, JpegFormat.Info, new JpegWriterOptions { Arithmetic = true, GainMap = new GainMapOptions() }));
    }

    [Fact]
    public void AGainMapIsLeftOutWithAWarning()
    {
        List<EgamiDiagnostic> seen = [];
        EgamiContext context = new() { Diagnostics = seen.Add };
        using Raster sdr = Raster.Load(Path.Combine(AppContext.BaseDirectory, "images", "gainmap", "uhdr-default.jpg"), context);
        Assert.NotNull(sdr.Metadata.GainMap);
        byte[] file = Encode(sdr, new JpegWriterOptions { Arithmetic = true });
        Assert.Contains(seen, d => d.Id == "Save.GainMapDropped");
        using Raster back = Raster.Load(file);
        Assert.Null(back.Metadata.GainMap);
        Assert.True(Has(file, JpegMarker.Sof9));
    }

    [Theory]
    [InlineData(false, JpegTransformOperation.None)]
    [InlineData(false, JpegTransformOperation.Rotate90)]
    [InlineData(true, JpegTransformOperation.None)]
    [InlineData(true, JpegTransformOperation.FlipHorizontal)]
    public void TransformRepacksWithArithmeticCoding(bool progressive, JpegTransformOperation operation)
    {
        using Raster<Rgb8> source = Noisy(96, 64, 9);
        byte[] original = Encode(source, new JpegWriterOptions { Quality = 85 });
        byte[] huffman = Transform(original, new JpegTransformOptions { Operation = operation, Progressive = progressive });
        byte[] arithmetic = Transform(original, new JpegTransformOptions { Operation = operation, Progressive = progressive, Arithmetic = true });
        Assert.True(Has(arithmetic, progressive ? JpegMarker.Sof10 : JpegMarker.Sof9));
        Assert.False(Has(arithmetic, JpegMarker.Dht));
        Assert.True(arithmetic.Length < huffman.Length, $"arithmetic {arithmetic.Length} bytes, Huffman {huffman.Length}");
        Assert.Equal(Pixels<Rgb8>(huffman), Pixels<Rgb8>(arithmetic));

        // Back to Huffman, the default: the same pixels again.
        byte[] back = Transform(arithmetic, new JpegTransformOptions());
        Assert.True(Has(back, JpegMarker.Sof0));
        Assert.Equal(Pixels<Rgb8>(huffman), Pixels<Rgb8>(back));
    }

    private static void AssertSamePixels<TPixel>(Raster source, JpegWriterOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        byte[] huffman = Encode(source, options);
        byte[] arithmetic = Encode(source, new JpegWriterOptions
        {
            Quality = options.Quality,
            Subsampling = options.Subsampling,
            Progressive = options.Progressive,
            RestartInterval = options.RestartInterval,
            Precision = options.Precision,
            ColorType = options.ColorType,
            Arithmetic = true,
        });
        Assert.True(Has(arithmetic, options.Progressive ? JpegMarker.Sof10 : JpegMarker.Sof9));
        Assert.Equal(Pixels<TPixel>(huffman), Pixels<TPixel>(arithmetic));
    }

    private static byte[] Pixels<TPixel>(byte[] file)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using Raster<TPixel> raster = Raster.Load<TPixel>(file);
        TPixel[] pixels = new TPixel[raster.Width * raster.Height];
        raster.CopyPixelsTo(pixels.AsSpan());
        return System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels.AsSpan()).ToArray();
    }

    private static byte[] Encode(Raster raster, WriterOptions options)
    {
        using MemoryStream stream = new();
        raster.Save(stream, JpegFormat.Info, options);
        return stream.ToArray();
    }

    private static byte[] Transform(byte[] file, JpegTransformOptions options)
    {
        using MemoryStream source = new(file);
        using MemoryStream destination = new();
        JpegTransform.Apply(source, destination, options);
        return destination.ToArray();
    }

    /// <summary>Returns whether a marker segment comes before the first scan.</summary>
    private static bool Has(byte[] file, byte marker)
    {
        int i = 2;
        while (i + 3 < file.Length && file[i] == 0xFF)
        {
            if (file[i + 1] == marker)
            {
                return true;
            }

            if (file[i + 1] == JpegMarker.Sos)
            {
                // A progressive file has more segments between its scans; look for the marker anywhere outside the data.
                return file.AsSpan(i).IndexOf((ReadOnlySpan<byte>)[0xFF, marker]) >= 0;
            }

            i += 2 + ((file[i + 2] << 8) | file[i + 3]);
        }

        return false;
    }

    /// <summary>A gradient with noise and a few hard edges, so every band of coefficients and both signs are busy.</summary>
    private static Raster<Rgb8> Noisy(int width, int height, int seed)
    {
        Random random = new(seed);
        byte[] noise = new byte[width * height * 3];
        random.NextBytes(noise);
        Raster<Rgb8> raster = Raster.Create<Rgb8>(width, height);
        raster.ForEachRow((row, y) =>
        {
            for (int x = 0; x < row.Length; x++)
            {
                int n = ((y * width) + x) * 3;
                int edge = ((x / 9) + (y / 7)) % 3 == 0 ? 90 : 0;
                row[x] = new Rgb8(
                    (byte)Math.Clamp((x * 255 / Math.Max(1, width)) + (noise[n] / 8) - edge, 0, 255),
                    (byte)Math.Clamp((y * 255 / Math.Max(1, height)) + (noise[n + 1] / 6), 0, 255),
                    (byte)Math.Clamp(128 + edge + (noise[n + 2] / 4) - 32, 0, 255));
            }
        }, Ct);
        return raster;
    }
}
