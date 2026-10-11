using System.Buffers.Binary;

using Egami.Formats;
using Egami.Formats.Jpeg;
using Egami.Pixels;
using Egami.Streaming;

namespace Egami.Tests.Formats.Jpeg;

/// <summary>
/// The Phase 2.4.3 writer modes: CMYK and YCCK output, 12-bit output and progressive scans. Egami's own reader checks the round
/// trips here; tools/check-jpeg-writer-interop.py checks that djpeg, ImageMagick and Pillow read the same files to the same pixels.
/// </summary>
public class JpegWriteDepthTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(JpegColorType.Cmyk, false)]
    [InlineData(JpegColorType.Cmyk, true)]
    [InlineData(JpegColorType.Ycck, false)]
    [InlineData(JpegColorType.Ycck, true)]
    public void CmykRoundTrips(JpegColorType type, bool sixteen)
    {
        using Raster<Cmyk8> source = Inks(61, 47);
        using Raster raster = sixteen ? source.As<Cmyk16>() : source.Copy();
        byte[] file = Encode(raster, new JpegWriterOptions { Quality = 98, ColorType = type, Subsampling = ChromaSubsampling.Subsample444 });

        // Adobe APP14 right after SOI, in place of JFIF, with transform 0 for CMYK and 2 for YCCK.
        Assert.Equal(0xEE, file[3]);
        Assert.Equal("Adobe"u8.ToArray(), file[6..11]);
        Assert.Equal(type == JpegColorType.Cmyk ? 0 : 2, file[17]);
        Assert.Equal(-1, IndexOf(file, "JFIF\0"u8));
        Assert.Equal(type == JpegColorType.Cmyk ? JpegColorSpace.Cmyk : JpegColorSpace.Ycck, Details(file).ColorSpace);

        using Raster<Cmyk8> back = Raster.Load<Cmyk8>(file);
        Assert.True(Psnr(Bytes(source), Bytes(back)) > 38, $"{type}: {Psnr(Bytes(source), Bytes(back)):F1} dB");
    }

    [Fact]
    public void YcckSubsamplesChromaButNotBlack()
    {
        using Raster<Cmyk8> source = Inks(40, 24);
        byte[] file = Encode(source, new JpegWriterOptions { ColorType = JpegColorType.Ycck });
        int sof = IndexOf(file, [0xFF, 0xC0]);
        Assert.Equal(4, file[sof + 9]);
        Assert.Equal([1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1, 4, 0x22, 0], file[(sof + 10)..(sof + 22)]);
        using Raster<Cmyk8> back = Raster.Load<Cmyk8>(file);
        Assert.True(Psnr(Bytes(source), Bytes(back)) > 30);
    }

    [Fact]
    public void AutoPicksCmykForCmykPixels()
    {
        using Raster<Cmyk8> cmyk = Inks(16, 16);
        using Raster<Cmyk16> cmyk16 = cmyk.As<Cmyk16>();
        using Raster<Rgb8> rgb = cmyk.As<Rgb8>();
        Assert.Equal(JpegColorSpace.Cmyk, Space(Encode(cmyk, JpegWriterOptions.Default)));
        Assert.Equal(JpegColorSpace.Cmyk, Space(Encode(cmyk16, JpegWriterOptions.Default)));
        Assert.Equal(JpegColorSpace.YCbCr, Space(Encode(rgb, JpegWriterOptions.Default)));

        // RGB written as CMYK goes through the plain formula, as Rgb8 to Cmyk8 conversion does.
        byte[] asCmyk = Encode(rgb, new JpegWriterOptions { ColorType = JpegColorType.Cmyk, Quality = 100, Subsampling = ChromaSubsampling.Subsample444 });
        Assert.Equal(JpegColorSpace.Cmyk, Space(asCmyk));
        using Raster<Cmyk8> converted = rgb.As<Cmyk8>();
        Assert.Equal(Encode(converted, new JpegWriterOptions { Quality = 100 }), asCmyk);
    }

    [Theory]
    [InlineData(PixelChannelOrder.Gray)]
    [InlineData(PixelChannelOrder.Rgb)]
    [InlineData(PixelChannelOrder.Cmyk)]
    public void TwelveBitKeepsDetail(PixelChannelOrder order)
    {
        using Raster<Rgb16> rgb = Gradient16(70, 45);
        using Raster raster = order switch
        {
            PixelChannelOrder.Gray => rgb.As<Gray16>(),
            PixelChannelOrder.Cmyk => rgb.As<Cmyk16>(),
            _ => rgb.Copy(),
        };
        JpegWriterOptions options = new() { Precision = 12, Quality = 100, Subsampling = ChromaSubsampling.Subsample444 };
        byte[] file = Encode(raster, options);
        Assert.True(IndexOf(file, [0xFF, 0xC1]) > 0, "SOF1");
        Assert.Equal(12, Details(file).Precision);

        // At quality 100 the error stays within 4 levels in 12 bits (64 in 16), where 8 bits would be off by up to 256.
        using Raster back = Raster.Load(file);
        Assert.Equal(raster.Layout, back.Layout);
        ushort[] a = Words(raster), b = Words(back);
        int worst = 0;
        for (int i = 0; i < a.Length; i++)
        {
            worst = Math.Max(worst, Math.Abs(a[i] - b[i]));
        }

        Assert.True(worst <= 64, $"{order}: off by {worst} in 16 bits");
    }

    [Fact]
    public void TwelveBitTakesFloatAnd8BitPixels()
    {
        using Raster<Rgb16> rgb = Gradient16(33, 20);
        using Raster<RgbF32> floats = rgb.As<RgbF32>();
        using Raster<Rgb8> bytes = rgb.As<Rgb8>();
        JpegWriterOptions options = new() { Precision = 12 };
        Assert.Equal(Encode(rgb, options), Encode(floats, options));
        using Raster<Rgb16> back = Raster.Load<Rgb16>(Encode(bytes, options));
        Assert.Equal((33, 20), (back.Width, back.Height));
    }

    [Fact]
    public void TwelveBitWritesSixteenBitTablesAtLowQuality()
    {
        using Raster<Rgb16> rgb = Gradient16(16, 16);
        using Raster<Gray16> gray = rgb.As<Gray16>();
        byte[] file = Encode(gray, new JpegWriterOptions { Precision = 12, Quality = 5 });
        int dqt = IndexOf(file, [0xFF, 0xDB]);
        Assert.Equal(0x10, file[dqt + 4]);
        Assert.Equal(2 + 1 + 128, BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(dqt + 2)));
        using Raster<Gray16> back = Raster.Load<Gray16>(file);
        Assert.Equal(16, back.Width);
    }

    public static TheoryData<string, ChromaSubsampling, int, int> Progressives()
    {
        TheoryData<string, ChromaSubsampling, int, int> data = [];
        foreach (string type in (string[])["rgb", "gray", "cmyk", "ycck"])
        {
            foreach (ChromaSubsampling sampling in Enum.GetValues<ChromaSubsampling>())
            {
                data.Add(type, sampling, 0, 8);
            }

            data.Add(type, ChromaSubsampling.Subsample420, 3, 8);
            data.Add(type, ChromaSubsampling.Subsample420, 0, 12);
            data.Add(type, ChromaSubsampling.Subsample422, 5, 12);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Progressives))]
    public void ProgressiveDecodesToBaselinePixels(string type, ChromaSubsampling sampling, int restart, int precision)
    {
        foreach ((int w, int h) in ((int, int)[])[(1, 1), (7, 3), (17, 9), (33, 65), (130, 75)])
        {
            using Raster<Rgb16> rgb = Gradient16(w, h);
            using Raster source = type switch
            {
                "gray" => rgb.As<Gray16>(),
                "cmyk" or "ycck" => rgb.As<Cmyk16>(),
                _ => rgb.Copy(),
            };
            JpegColorType color = type == "ycck" ? JpegColorType.Ycck : JpegColorType.Auto;
            byte[] plain = Encode(source, new JpegWriterOptions { Subsampling = sampling, RestartInterval = restart, Precision = precision, ColorType = color });
            byte[] file = Encode(source, new JpegWriterOptions { Subsampling = sampling, RestartInterval = restart, Precision = precision, ColorType = color, Progressive = true });
            Assert.True(IndexOf(file, [0xFF, 0xC2]) > 0, "SOF2");
            Assert.True(Details(file).Progressive);
            using Raster a = Raster.Load(plain);
            using Raster b = Raster.Load(file);
            Assert.Empty(b.Metadata.Warnings);
            Assert.True(Words(a).AsSpan().SequenceEqual(Words(b)), $"{type} {sampling} restart {restart} {precision}-bit {w}×{h}");
        }
    }

    [Fact]
    public void ProgressiveUsesLibjpegScanScripts()
    {
        using Raster<Rgb16> rgb = Gradient16(64, 48);
        Assert.Equal(10, Scans(Encode(rgb.As<Rgb8>(), new JpegWriterOptions { Progressive = true })));
        Assert.Equal(6, Scans(Encode(rgb.As<Gray8>(), new JpegWriterOptions { Progressive = true })));
        Assert.Equal(18, Scans(Encode(rgb.As<Cmyk8>(), new JpegWriterOptions { Progressive = true })));
    }

    [Fact]
    public void ProgressiveIsSmallerThanBaselineOnAPhoto()
    {
        using Raster<Rgb8> photo = Raster.Load<Rgb8>(Path.Combine(AppContext.BaseDirectory, "images", "ops", "duck.png"));
        int baseline = Encode(photo, new JpegWriterOptions { OptimizeHuffman = true }).Length;
        int progressive = Encode(photo, new JpegWriterOptions { Progressive = true }).Length;
        Assert.True(progressive < baseline, $"progressive {progressive:N0} bytes, optimized baseline {baseline:N0}");
    }

    [Theory]
    [InlineData(true, 8, JpegColorType.Auto)]
    [InlineData(false, 12, JpegColorType.Auto)]
    [InlineData(true, 12, JpegColorType.Ycck)]
    [InlineData(false, 8, JpegColorType.Cmyk)]
    public void StreamedEncodeWritesWhatSaveWrites(bool progressive, int precision, JpegColorType type)
    {
        using Raster<Rgb16> source = Gradient16(45, 38);
        JpegWriterOptions options = new() { Progressive = progressive, Precision = precision, ColorType = type, RestartInterval = 2 };
        byte[] saved = Encode(source, options);
        using MemoryStream streamed = new();
        using (RasterEncoder<Rgb16> encoder = RasterEncoder.Create<Rgb16>(streamed, JpegFormat.Info, source.Width, source.Height, options))
        {
            encoder.WriteRows(source);
            encoder.Complete();
        }

        Assert.Equal(saved, streamed.ToArray());
    }

    [Theory]
    [InlineData(true, 8, JpegColorType.Auto)]
    [InlineData(false, 12, JpegColorType.Auto)]
    [InlineData(true, 8, JpegColorType.Ycck)]
    public void SameBytesOnOneThreadAndMany(bool progressive, int precision, JpegColorType type)
    {
        using Raster<Rgb8> serial = Raster.Create<Rgb8>(1201, 999, EgamiContext.Default with { MaxParallelism = 1 });
        Random random = new(7);
        byte[] noise = new byte[serial.Width * serial.Height * 3];
        random.NextBytes(noise);
        serial.CopyPixelsFrom(noise);
        using Raster<Rgb8> parallel = Raster.Create<Rgb8>(1201, 999, EgamiContext.Default with { MaxParallelism = 8 });
        parallel.CopyPixelsFrom(noise);
        JpegWriterOptions options = new() { Progressive = progressive, Precision = precision, ColorType = type };
        Assert.Equal(Encode(serial, options), Encode(parallel, options));
    }

    [Fact]
    public void ProgressiveCountsItsCoefficientsAgainstTheMemoryLimit()
    {
        EgamiContext tight = EgamiContext.Default with { Limits = SafetyLimits.Default with { MaxTotalBytes = 100_000 } };
        using Raster<Rgb8> raster = Raster.Create<Rgb8>(256, 256, tight);
        Assert.Throws<LimitExceededException>(() => Encode(raster, new JpegWriterOptions { Progressive = true }));
        Assert.Throws<LimitExceededException>(() => Encode(raster, new JpegWriterOptions { Precision = 12 }));
        Assert.NotEmpty(Encode(raster, JpegWriterOptions.Default));
    }

    [Fact]
    public void RejectsOtherPrecisions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new JpegWriterOptions { Precision = 10 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new JpegWriterOptions { Precision = 16 });
    }

    private static byte[] Encode(Raster raster, WriterOptions options)
    {
        using MemoryStream stream = new();
        raster.Save(stream, JpegFormat.Info, options);
        return stream.ToArray();
    }

    private static JpegColorSpace Space(byte[] file) => Details(file).ColorSpace;

    private static JpegDetails Details(byte[] file)
    {
        using Raster raster = Raster.Load(file);
        return Assert.IsType<JpegDetails>(raster.Metadata.FormatDetails);
    }

    /// <summary>Counts the SOS markers outside entropy-coded data: every 0xFF 0xDA, since stuffing keeps 0xFF in data followed by 0.</summary>
    private static int Scans(byte[] file)
    {
        int count = 0;
        for (int i = 0; i + 1 < file.Length; i++)
        {
            if (file[i] == 0xFF && file[i + 1] == 0xDA)
            {
                count++;
            }
        }

        return count;
    }

    private static int IndexOf(byte[] file, ReadOnlySpan<byte> what) => file.AsSpan().IndexOf(what);

    /// <summary>Smooth inks with black in one corner, so CMYK keeps all four channels busy.</summary>
    private static Raster<Cmyk8> Inks(int width, int height)
    {
        Raster<Cmyk8> raster = Raster.Create<Cmyk8>(width, height);
        raster.ForEachRow((row, y) =>
        {
            for (int x = 0; x < row.Length; x++)
            {
                row[x] = new Cmyk8((byte)(x * 255 / width), (byte)(y * 255 / height), (byte)(128 + (x - y)), (byte)Math.Max(0, 200 - ((x + y) * 4)));
            }
        }, Ct);
        return raster;
    }

    private static Raster<Rgb16> Gradient16(int width, int height)
    {
        Raster<Rgb16> raster = Raster.Create<Rgb16>(width, height);
        raster.ForEachRow((row, y) =>
        {
            for (int x = 0; x < row.Length; x++)
            {
                row[x] = new Rgb16((ushort)(9000 + (x * 397)), (ushort)(14000 + (y * 503)), (ushort)(60000 - (x * 211) - (y * 157)));
            }
        }, Ct);
        return raster;
    }

    private static byte[] Bytes(Raster raster)
    {
        byte[] bytes = new byte[raster.Width * raster.Height * raster.Layout.BytesPerPixel];
        switch (raster)
        {
            case Raster<Gray8> r: r.CopyPixelsTo(bytes.AsSpan()); break;
            case Raster<Gray16> r: r.CopyPixelsTo(bytes.AsSpan()); break;
            case Raster<Rgb8> r: r.CopyPixelsTo(bytes.AsSpan()); break;
            case Raster<Rgb16> r: r.CopyPixelsTo(bytes.AsSpan()); break;
            case Raster<Cmyk8> r: r.CopyPixelsTo(bytes.AsSpan()); break;
            case Raster<Cmyk16> r: r.CopyPixelsTo(bytes.AsSpan()); break;
            default: throw new InvalidOperationException(raster.GetType().Name);
        }

        return bytes;
    }

    private static ushort[] Words(Raster raster)
    {
        byte[] bytes = Bytes(raster);
        if (raster.Layout.BytesPerPixel == raster.Layout.ChannelCount)
        {
            return [.. bytes.Select(b => (ushort)(b * 257))];
        }

        return [.. System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(bytes)];
    }

    private static double Psnr(byte[] a, byte[] b)
    {
        Assert.Equal(a.Length, b.Length);
        double sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            int d = a[i] - b[i];
            sum += d * d;
        }

        return sum == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255 / (sum / a.Length));
    }
}
