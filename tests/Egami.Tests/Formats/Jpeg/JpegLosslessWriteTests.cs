using Egami.Colors;
using Egami.Formats;
using Egami.Formats.Jpeg;
using Egami.Pixels;
using Egami.Streaming;

namespace Egami.Tests.Formats.Jpeg;

/// <summary>
/// Lossless JPEG writing (SOF3 and SOF11): every predictor, point transform, precision, size and restart interval loads back to
/// exactly the samples written. tools/check-jpeg-writer-interop.py checks the SOF3 files with djpeg and their size against
/// cjpeg -lossless.
/// </summary>
public class JpegLosslessWriteTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<int, bool> Predictors()
    {
        TheoryData<int, bool> data = [];
        for (int p = 1; p <= 7; p++)
        {
            data.Add(p, false);
            data.Add(p, true);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Predictors))]
    public void EveryPredictorRoundTrips(int predictor, bool arithmetic)
    {
        using Raster<Rgb8> rgb = Noisy(37, 23, predictor);
        AssertExact(rgb, new JpegLosslessOptions { Predictor = predictor }, arithmetic, restart: 0);
        using Raster<Gray16> gray = rgb.As<Gray16>();
        AssertExact(gray, new JpegLosslessOptions { Predictor = predictor }, arithmetic, restart: 0);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 9)]
    [InlineData(9, 1)]
    [InlineData(37, 23)]
    [InlineData(64, 3)]
    public void OddSizesRoundTrip(int width, int height)
    {
        using Raster<Rgb8> rgb = Noisy(width, height, 3);
        foreach (bool arithmetic in (bool[])[false, true])
        {
            AssertExact(rgb, new JpegLosslessOptions(), arithmetic, restart: 0);
            AssertExact(rgb, new JpegLosslessOptions { Predictor = 4 }, arithmetic, restart: 2);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(37)]
    [InlineData(100)]
    public void RestartIntervalsRoundTrip(int restart)
    {
        using Raster<Rgb8> rgb = Noisy(37, 23, 4);
        using Raster<Cmyk16> cmyk = rgb.As<Cmyk16>();
        foreach (bool arithmetic in (bool[])[false, true])
        {
            foreach (int predictor in (int[])[1, 4, 7])
            {
                AssertExact(rgb, new JpegLosslessOptions { Predictor = predictor }, arithmetic, restart);
                AssertExact(cmyk, new JpegLosslessOptions { Predictor = predictor }, arithmetic, restart);
            }
        }
    }

    [Theory]
    [InlineData(1, 37, 37)]
    [InlineData(37, 37, 37)]
    [InlineData(38, 37, 74)]
    [InlineData(100, 64, 128)]
    [InlineData(65535, 300, 65400)]
    public void RestartIntervalIsWholeRows(int asked, int width, int written)
    {
        // T.81 asks a lossless restart interval to be a whole number of MCU rows; libjpeg-turbo refuses any other.
        Assert.Equal(written, JpegLosslessScan.RestartInterval(asked, width));
        using Raster<Rgb8> source = Noisy(width, 3, 15);
        byte[] file = Encode(source, new JpegWriterOptions { Lossless = new JpegLosslessOptions(), RestartInterval = asked });
        int dri = file.AsSpan().IndexOf((ReadOnlySpan<byte>)[0xFF, JpegMarker.Dri]);
        Assert.Equal(written, (file[dri + 4] << 8) | file[dri + 5]);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(15)]
    [InlineData(16)]
    public void EveryPrecisionKeepsItsSamples(int precision)
    {
        using Raster<Rgb8> color = Noisy(29, 17, 5);
        using Raster<Gray16> gray = color.As<Gray16>();
        foreach (bool arithmetic in (bool[])[false, true])
        {
            foreach (int pt in (int[])[0, 1, 3])
            {
                if (pt < precision)
                {
                    AssertExact(gray, new JpegLosslessOptions { Precision = precision, PointTransform = pt, Predictor = 1 + (pt % 7) }, arithmetic, restart: 0);
                }
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PixelTypesRoundTripExactly(bool arithmetic)
    {
        using Raster<Rgb8> rgb = Noisy(41, 27, 6);
        using Raster<Gray8> gray8 = rgb.As<Gray8>();
        using Raster<Cmyk8> cmyk8 = rgb.As<Cmyk8>();
        using Raster<Rgb16> rgb16 = Wide(41, 27);
        using Raster<Gray16> gray16 = rgb16.As<Gray16>();
        using Raster<Cmyk16> cmyk16 = rgb16.As<Cmyk16>();

        Assert.Equal(Bytes(rgb), Bytes(Load<Rgb8>(Encode(rgb, arithmetic))));
        Assert.Equal(Bytes(gray8), Bytes(Load<Gray8>(Encode(gray8, arithmetic))));
        Assert.Equal(Bytes(cmyk8), Bytes(Load<Cmyk8>(Encode(cmyk8, arithmetic))));
        Assert.Equal(Bytes(rgb16), Bytes(Load<Rgb16>(Encode(rgb16, arithmetic))));
        Assert.Equal(Bytes(gray16), Bytes(Load<Gray16>(Encode(gray16, arithmetic))));
        Assert.Equal(Bytes(cmyk16), Bytes(Load<Cmyk16>(Encode(cmyk16, arithmetic))));

        // Loaded in their natural types: 8-bit files as 8-bit, 16-bit ones as 16-bit, RGB as RGB and CMYK as CMYK.
        using Raster natural8 = Raster.Load(Encode(rgb, arithmetic));
        using Raster natural16 = Raster.Load(Encode(cmyk16, arithmetic));
        Assert.IsType<Raster<Rgb8>>(natural8);
        Assert.IsType<Raster<Cmyk16>>(natural16);
        JpegDetails details = Assert.IsType<JpegDetails>(natural16.Metadata.FormatDetails);
        Assert.True(details.Lossless);
        Assert.Equal(16, details.Precision);
        Assert.Equal(arithmetic, details.Arithmetic);
        Assert.Equal(JpegColorSpace.Cmyk, details.ColorSpace);
        Assert.Equal(JpegColorSpace.Rgb, Assert.IsType<JpegDetails>(natural8.Metadata.FormatDetails).ColorSpace);
    }

    [Fact]
    public void WritesTheMarkersT81Asks()
    {
        using Raster<Rgb8> rgb = Noisy(16, 16, 7);
        byte[] huffman = Encode(rgb, new JpegWriterOptions { Lossless = new JpegLosslessOptions { Predictor = 6 } });
        byte[] arithmetic = Encode(rgb, new JpegWriterOptions { Lossless = new JpegLosslessOptions { Predictor = 6 }, Arithmetic = true });
        Assert.True(Has(huffman, JpegMarker.Sof3) && Has(huffman, JpegMarker.Dht));
        Assert.True(Has(arithmetic, JpegMarker.Sof11) && !Has(arithmetic, JpegMarker.Dht));
        Assert.False(Has(huffman, JpegMarker.Dqt) || Has(arithmetic, JpegMarker.Dqt));

        // Adobe transform 0 for RGB, and no JFIF segment, so readers take the components as RGB.
        Assert.Equal(0xEE, huffman[3]);
        Assert.Equal(-1, huffman.AsSpan().IndexOf("JFIF\0"u8));

        // The predictor goes in Ss and the point transform in Al.
        int sos = huffman.AsSpan().IndexOf((ReadOnlySpan<byte>)[0xFF, JpegMarker.Sos]);
        int ns = huffman[sos + 4];
        Assert.Equal(6, huffman[sos + 5 + (2 * ns)]);
        Assert.Equal(0, huffman[sos + 7 + (2 * ns)]);
    }

    [Fact]
    public void AutomaticPredictorIsNoWorseThanTheFixedOnesByMuch()
    {
        using Raster<Rgb8> source = Noisy(160, 120, 8);
        int auto = Encode(source, new JpegWriterOptions { Lossless = new JpegLosslessOptions() }).Length;
        int best = Enumerable.Range(1, 7).Min(p => Encode(source, new JpegWriterOptions { Lossless = new JpegLosslessOptions { Predictor = p } }).Length);
        Assert.True(auto <= best * 1.02, $"auto {auto} bytes, best fixed {best}");
    }

    [Fact]
    public void StreamedEncodeMatchesWhole()
    {
        using Raster<Rgb8> source = Noisy(53, 31, 9);
        foreach (bool arithmetic in (bool[])[false, true])
        {
            JpegWriterOptions options = new() { Lossless = new JpegLosslessOptions(), Arithmetic = arithmetic, RestartInterval = 7 };
            byte[] whole = Encode(source, options);
            using MemoryStream memory = new();
            using (RasterEncoder<Rgb8> encoder = RasterEncoder.Create<Rgb8>(memory, JpegFormat.Info, source.Width, source.Height, options))
            {
                using Raster<Rgb8> band = Raster.Create<Rgb8>(source.Width, 4);
                for (int y = 0; y < source.Height; y += 4)
                {
                    int rows = Math.Min(4, source.Height - y);
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
    }

    [Fact]
    public void GrayColorTypeWritesLuma()
    {
        using Raster<Rgb8> source = Noisy(20, 12, 10);
        using Raster<Gray8> luma = source.As<Gray8>();
        byte[] file = Encode(source, new JpegWriterOptions { Lossless = new JpegLosslessOptions(), ColorType = JpegColorType.Gray });
        Assert.Equal(Bytes(luma), Bytes(Load<Gray8>(file)));
    }

    [Fact]
    public void LossyChoicesAreReported()
    {
        List<EgamiDiagnostic> seen = [];
        EgamiContext context = new() { Diagnostics = seen.Add };
        using Raster<Rgb16> wide = Wide(16, 16);
        using Raster<Rgb16> source = InContext(wide, context);
        using MemoryStream stream = new();
        source.Save(stream, JpegFormat.Info, new JpegWriterOptions { Lossless = new JpegLosslessOptions { PointTransform = 2, Precision = 12 } });
        Assert.Equal(2, seen.Count(d => d.Id == "Save.DepthReduced"));

        seen.Clear();
        using Raster<Rgb8> noisy = Noisy(16, 16, 11);
        using Raster<Rgb8> exact = InContext(noisy, context);
        exact.Save(new MemoryStream(), JpegFormat.Info, new JpegWriterOptions { Lossless = new JpegLosslessOptions() });
        Assert.Empty(seen);
    }

    [Fact]
    public void InvalidCombinationsThrow()
    {
        using Raster<Rgb8> source = Noisy(8, 8, 12);
        JpegLosslessOptions lossless = new();
        Assert.Throws<ArgumentException>(() => Encode(source, new JpegWriterOptions { Lossless = lossless, Progressive = true }));
        Assert.Throws<ArgumentException>(() => Encode(source, new JpegWriterOptions { Lossless = lossless, ColorType = JpegColorType.Ycck }));
        Assert.Throws<ArgumentException>(() => Encode(source, new JpegWriterOptions { Lossless = new JpegLosslessOptions { Precision = 4, PointTransform = 4 } }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new JpegLosslessOptions { Predictor = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new JpegLosslessOptions { Predictor = 8 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new JpegLosslessOptions { Precision = 1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new JpegLosslessOptions { Precision = 17 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new JpegLosslessOptions { PointTransform = 16 });

        using Raster<RgbaF32> hdr = Raster.Create<RgbaF32>(8, 8);
        Assert.Throws<NotSupportedException>(() => Encode(hdr, new JpegWriterOptions { Lossless = lossless, GainMap = new GainMapOptions() }));
    }

    [Fact]
    public void HeldSamplesCountAgainstTheMemoryLimit()
    {
        using Raster<Rgb8> noisy = Noisy(64, 64, 13);
        using Raster<Rgb8> source = InContext(noisy, new EgamiContext { Limits = new SafetyLimits { MaxTotalBytes = 64 * 64 * 3 } });
        using MemoryStream stream = new();
        Assert.Throws<LimitExceededException>(() => source.Save(stream, JpegFormat.Info, new JpegWriterOptions { Lossless = new JpegLosslessOptions() }));
    }

    [Fact]
    public void AGainMapIsLeftOutWithAWarning()
    {
        List<EgamiDiagnostic> seen = [];
        EgamiContext context = new() { Diagnostics = seen.Add };
        using Raster sdr = Raster.Load(Path.Combine(AppContext.BaseDirectory, "images", "gainmap", "uhdr-default.jpg"), context);
        byte[] file = Encode(sdr, new JpegWriterOptions { Lossless = new JpegLosslessOptions() });
        Assert.Contains(seen, d => d.Id == "Save.GainMapDropped");
        using Raster back = Raster.Load(file);
        Assert.Null(back.Metadata.GainMap);
    }

    /// <summary>
    /// Writes <paramref name="raster"/> lossless and checks the decoded samples are exactly the written ones: at the pixel type's
    /// own precision the pixels themselves, else each sample narrowed to the precision with rounding, its low point-transform bits
    /// cleared, and widened back as the reader widens.
    /// </summary>
    private static void AssertExact<TPixel>(Raster<TPixel> raster, JpegLosslessOptions lossless, bool arithmetic, int restart)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        byte[] file = Encode(raster, new JpegWriterOptions { Lossless = lossless, Arithmetic = arithmetic, RestartInterval = restart });
        Assert.True(Has(file, arithmetic ? JpegMarker.Sof11 : JpegMarker.Sof3));
        using Raster<TPixel> back = Raster.Load<TPixel>(file);
        int source = TPixel.Layout.ChannelType == PixelChannelType.Unorm8 ? 8 : 16;
        int precision = lossless.Precision ?? source;
        if (precision == source && lossless.PointTransform == 0)
        {
            Assert.Equal(Bytes(raster), Bytes(back));
            return;
        }

        Assert.Equal(16, source);
        ushort[] written = Words(raster);
        ushort[] read = Words(back);
        int max = (1 << precision) - 1;
        for (int i = 0; i < written.Length; i++)
        {
            int sample = (int)(((written[i] * (long)max) + 32767) / 65535);
            sample = sample >> lossless.PointTransform << lossless.PointTransform;
            int expected = (int)(((sample * 65535L) + (max >> 1)) / max);
            Assert.True(expected == read[i], $"sample {i}: {written[i]} written, {read[i]} read, {expected} expected at {precision} bits, Pt {lossless.PointTransform}");
        }
    }

    private static Raster<TPixel> InContext<TPixel>(Raster<TPixel> raster, EgamiContext context)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Raster<TPixel> copy = Raster.Create<TPixel>(raster.Width, raster.Height, context);
        for (int y = 0; y < raster.Height; y++)
        {
            raster.Row(y).CopyTo(copy.Row(y));
        }

        return copy;
    }

    private static byte[] Encode(Raster raster, bool arithmetic) => Encode(raster, new JpegWriterOptions { Lossless = new JpegLosslessOptions(), Arithmetic = arithmetic });

    private static byte[] Encode(Raster raster, WriterOptions options)
    {
        using MemoryStream stream = new();
        raster.Save(stream, JpegFormat.Info, options);
        return stream.ToArray();
    }

    private static Raster<TPixel> Load<TPixel>(byte[] file)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        return Raster.Load<TPixel>(file);
    }

    private static byte[] Bytes<TPixel>(Raster<TPixel> raster)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        TPixel[] pixels = new TPixel[raster.Width * raster.Height];
        raster.CopyPixelsTo(pixels.AsSpan());
        return System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels.AsSpan()).ToArray();
    }

    private static ushort[] Words<TPixel>(Raster<TPixel> raster)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        return System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(Bytes(raster)).ToArray();
    }

    /// <summary>Returns whether a marker comes before the entropy-coded data ends (FF then the marker, outside stuffed bytes).</summary>
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
                return false;
            }

            i += 2 + ((file[i + 2] << 8) | file[i + 3]);
        }

        return false;
    }

    /// <summary>A gradient with noise and hard edges, so every predictor sees both signs and large differences.</summary>
    internal static Raster<Rgb8> Noisy(int width, int height, int seed)
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
                int edge = ((x / 5) + (y / 3)) % 3 == 0 ? 200 : 0;
                row[x] = new Rgb8(
                    (byte)Math.Clamp((x * 255 / Math.Max(1, width)) + (noise[n] / 16) - (edge / 2), 0, 255),
                    (byte)((y * 255 / Math.Max(1, height)) ^ (noise[n + 1] & 7)),
                    (byte)(edge + (noise[n + 2] / 8)));
            }
        }, Ct);
        return raster;
    }

    /// <summary>A 16-bit image whose low bits are busy, so a 16-bit round trip is not an 8-bit one widened.</summary>
    private static Raster<Rgb16> Wide(int width, int height)
    {
        Random random = new(14);
        Raster<Rgb16> raster = Raster.Create<Rgb16>(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<Rgb16> row = raster.Row(y);
            for (int x = 0; x < width; x++)
            {
                row[x] = new Rgb16((ushort)((x * 1500) + random.Next(300)), (ushort)((y * 2300) + random.Next(4096)), (ushort)random.Next(65536));
            }
        }

        return raster;
    }
}
