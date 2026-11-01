using System.Runtime.InteropServices;

using Egami.Formats.Extended.Raw;
using Egami.Formats.Tiff;
using Egami.Pixels;

namespace Egami.Formats.Extended.Tests.Raw;

/// <summary>
/// DNG writing: RAW files converted with their sensor data kept bit for bit, rasters saved as CFA or LinearRaw DNGs, every
/// compression and preview setting, the tags DNG readers need, and Transcode's route to the conversion.
/// </summary>
public class DngWriterTests
{
    private static readonly RawReaderOptions Mosaic = new() { Output = RawOutput.Mosaic };

    private static EgamiContext Context => TestImages.Context;

    /// <summary>A 64 × 48 Bayer DNG with an active area, a crop, a 2 × 2 black pattern, a white level and an opcode list.</summary>
    private static DngFile Source(int bits = 14)
    {
        Random random = new(bits);
        int max = (1 << bits) - 1;
        DngFile dng = new DngFile
        {
            Width = 64,
            Height = 48,
            Bits = 16,
            Black = [512, 514, 516, 518],
            BlackRepeat = [2, 2],
            White = max,
            ActiveArea = [2, 4, 46, 60],
            Crop = [1, 1, 52, 40],
            Neutral = [0.5, 1, 0.7],
            BaselineExposure = 0.35,
        }.Fill((x, y, _) => Math.Clamp(600 + (x * 50) + (y * 30) + ((x & 1) * 300) + random.Next(-40, 41), 0, max));
        dng.Exif.Add(TiffTag.Rational(33434, 1.0 / 125));
        dng.Exif.Add(TiffTag.Undefined(37500, "Egami maker note"u8.ToArray()));
        return dng;
    }

    private static byte[] Convert(byte[] raw, DngWriterOptions? options = null)
    {
        using MemoryStream input = new(raw);
        using MemoryStream output = new();
        RawFormat.ConvertToDng(input, output, options, Context);
        return output.ToArray();
    }

    private static void SameMosaic(byte[] a, byte[] b)
    {
        using Raster<Gray16> first = Raster.Load<Gray16>(a, Context, Mosaic);
        using Raster<Gray16> second = Raster.Load<Gray16>(b, Context, Mosaic);
        Assert.Equal((first.Width, first.Height), (second.Width, second.Height));
        for (int y = 0; y < first.Height; y++)
        {
            Assert.True(first.Row(y).SequenceEqual(second.Row(y)), $"Row {y} differs.");
        }

        RawDetails da = (RawDetails)first.Metadata.FormatDetails!, db = (RawDetails)second.Metadata.FormatDetails!;
        Assert.Equal((da.ActiveArea, da.CropArea, da.CfaPattern), (db.ActiveArea, db.CropArea, db.CfaPattern));
        Assert.Equal(da.BlackLevels, db.BlackLevels);
        Assert.Equal(da.WhiteLevels, db.WhiteLevels);
        Assert.Equal(da.AsShotNeutral.Select(v => Math.Round(v, 6)), db.AsShotNeutral.Select(v => Math.Round(v, 6)));
        Assert.Equal(da.Opcodes, db.Opcodes);
        Assert.Equal(da.Make, db.Make);
        Assert.Equal(da.Model, db.Model);
    }

    [Theory]
    [InlineData(DngCompression.LosslessJpeg)]
    [InlineData(DngCompression.None)]
    [InlineData(DngCompression.Deflate)]
    [InlineData(DngCompression.JpegXl)]
    public void ConversionKeepsTheMosaicBitForBit(DngCompression compression)
    {
        DngFile dng = Source();
        dng.Opcodes1 = new OpcodeList().Add(8, p => p.Area(0, 0, 16, 16).U32(1).F64(0, 3)).Build();
        byte[] raw = dng.Build();
        byte[] converted = Convert(raw, new DngWriterOptions { Compression = compression, TileSize = 32 });
        SameMosaic(raw, converted);
        RawDetails details = Details(converted);
        int expected = compression switch
        {
            DngCompression.None => 1,
            DngCompression.Deflate => 8,
            DngCompression.JpegXl => 52546,
            _ => 7,
        };
        Assert.Equal(expected, details.Compression);
        Assert.Equal(compression == DngCompression.JpegXl ? new Version(1, 7, 0, 0) : new Version(1, 4, 0, 0), details.DngVersion);
    }

    private static RawDetails Details(byte[] dng)
    {
        using Raster<Gray16> mosaic = Raster.Load<Gray16>(dng, Context, Mosaic);
        return (RawDetails)mosaic.Metadata.FormatDetails!;
    }

    [Fact]
    public void ConversionKeepsTheColourDataBaselineAndMakerNote()
    {
        byte[] raw = Source().Build();
        byte[] converted = Convert(raw);
        RawDetails a = Details(raw), b = Details(converted);
        Assert.Equal(a.BaselineExposure, b.BaselineExposure, 4);
        Assert.Equal(a.UniqueCameraModel ?? "Egami Test Camera", b.UniqueCameraModel);
        Assert.Equal(a.MakerNote.ToArray(), b.MakerNote.ToArray());
        Assert.Equal(1.0 / 125, b.ExposureTime!.Value, 6);

        // The develop sees the same camera: the same colours within a level or two.
        using Raster<Rgb16> da = Raster.Load<Rgb16>(raw, Context);
        using Raster<Rgb16> db = Raster.Load<Rgb16>(converted, Context);
        Assert.Equal((da.Width, da.Height), (db.Width, db.Height));
        Assert.True(Raster.Compare(da, db).Overall.Psnr > 60, $"PSNR {Raster.Compare(da, db).Overall.Psnr:F1} dB");
    }

    [Theory]
    [InlineData(DngPreview.None, 0)]
    [InlineData(DngPreview.Thumbnail, 1)]
    [InlineData(DngPreview.Medium, 2)]
    [InlineData(DngPreview.Full, 2)]
    public void PreviewsComeAsAsked(DngPreview preview, int count)
    {
        byte[] converted = Convert(Source().Build(), new DngWriterOptions { Preview = preview });
        RawDetails details = Details(converted);
        Assert.Equal(count, details.Previews.Count);
        if (preview == DngPreview.Full)
        {
            RawPreview jpeg = details.Previews.Single(p => p.Kind == RawPreviewKind.Jpeg);
            Assert.Equal((52, 40), (jpeg.Width, jpeg.Height));
        }

        if (count > 0)
        {
            using Raster<Rgb8> shown = Raster.Load<Rgb8>(converted, Context, new RawReaderOptions { Output = RawOutput.Preview });
            Assert.True(shown.Width > 0);

            // The first image is the thumbnail, which a plain TIFF reader opens.
            using Raster page = Raster.Load(converted, EgamiContext.Default);
            Assert.True(page.Width <= 256);
        }
    }

    [Fact]
    public void TranscodeToDngKeepsTheMosaic()
    {
        byte[] raw = Source().Build();
        using MemoryStream input = new(raw);
        using MemoryStream output = new();
        Raster.Transcode(input, output, RawFormat.Info, context: Context);
        SameMosaic(raw, output.ToArray());
    }

    [Fact]
    public void TranscodeWithOperationsWritesTheDevelopAsLinearRaw()
    {
        byte[] raw = Source().Build();
        using MemoryStream input = new(raw);
        using MemoryStream output = new();
        Raster.Transcode(input, output, RawFormat.Info, ops => ops.Fit(26, 20), context: Context);
        RawDetails details = (RawDetails)Raster.Load<Rgb16>(output.ToArray(), Context, Mosaic).Metadata.FormatDetails!;
        Assert.Null(details.CfaPattern);
        Assert.Equal((26, 20), (details.SensorWidth, details.SensorHeight));
    }

    [Fact]
    public void AMosaicRasterSavesAsACfaDng()
    {
        byte[] raw = Source().Build();
        using Raster<Gray16> mosaic = Raster.Load<Gray16>(raw, Context, Mosaic);
        using MemoryStream saved = new();
        mosaic.Save(saved, RawFormat.Info);
        SameMosaic(raw, saved.ToArray());
    }

    [Fact]
    public void AnRgbRasterSavesAsLinearRaw()
    {
        using Raster<Rgb16> photo = Raster.Create<Rgb16>(40, 30);
        for (int y = 0; y < 30; y++)
        {
            Span<Rgb16> row = photo.Row(y);
            for (int x = 0; x < 40; x++)
            {
                row[x] = new Rgb16((ushort)(x * 1600), (ushort)(y * 2100), (ushort)(30000 + (x * y * 10)));
            }
        }

        using MemoryStream saved = new();
        photo.Save(saved, RawFormat.Info, new DngWriterOptions { Compression = DngCompression.Deflate });
        byte[] dng = saved.ToArray();
        using Raster<Rgb16> linear = Raster.Load<Rgb16>(dng, Context, Mosaic);
        RawDetails details = (RawDetails)linear.Metadata.FormatDetails!;
        Assert.Null(details.CfaPattern);
        Assert.Equal((40, 30, 8), (linear.Width, linear.Height, details.Compression));

        // The stored samples are the image in linear light.
        using Raster<Rgb16> expected = photo.ConvertColor<Rgb16>(Colors.ColorProfile.LinearSrgb);
        Assert.Equal(MemoryMarshal.AsBytes(expected.Row(17)).ToArray(), MemoryMarshal.AsBytes(linear.Row(17)).ToArray());
        Assert.Equal(2, details.Previews.Count);
    }

    [Fact]
    public void FloatRastersUseDeflate()
    {
        using Raster<RgbF32> hdr = Raster.Create<RgbF32>(20, 16);
        hdr.Row(5)[3] = new RgbF32(2.5f, 0.25f, 0.125f);
        using MemoryStream saved = new();
        hdr.Save(saved, RawFormat.Info);
        using Raster<RgbF32> back = Raster.Load<RgbF32>(saved.ToArray(), Context, Mosaic);
        Assert.Equal(8, ((RawDetails)back.Metadata.FormatDetails!).Compression);
        Assert.Equal(hdr.Row(5)[3], back.Row(5)[3]);
        using MemoryStream none = new();
        hdr.Save(none, RawFormat.Info, new DngWriterOptions { Compression = DngCompression.None });
        Assert.Equal(1, ((RawDetails)Raster.Load<RgbF32>(none.ToArray(), Context, Mosaic).Metadata.FormatDetails!).Compression);
    }

    [Fact]
    public void ParallelCompressionWritesTheSameFile()
    {
        byte[] raw = Source().Build();
        byte[] one = Convert(raw, new DngWriterOptions { ParallelCompression = false, TileSize = 16 });
        byte[] many = Convert(raw, new DngWriterOptions { ParallelCompression = true, TileSize = 16 });
        Assert.Equal(one, many);
    }

    [Fact]
    public void TileSizeIsChecked()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DngWriterOptions { TileSize = 100 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new DngWriterOptions { TileSize = 0 });
        Assert.Null(new DngWriterOptions { TileSize = null }.TileSize);
        Assert.Equal((256, 240), (DngWriter.TileFor(4096), DngWriter.TileFor(2400)));
    }

    [Fact]
    public void TheMakerNoteIsInDngPrivateDataAsMakN()
    {
        byte[] converted = Convert(Source().Build());
        // Once, 20 bytes after the MakN header; EXIF no longer holds it.
        int makN = converted.AsSpan().IndexOf("Adobe\0MakN"u8);
        int note = converted.AsSpan().IndexOf("Egami maker note"u8);
        Assert.True(makN > 0);
        Assert.Equal(makN + 20, note);
        Assert.Equal(-1, converted.AsSpan(note + 1).IndexOf("Egami maker note"u8));
    }

    /// <summary>
    /// Every file of a RAW corpus converts and loads back with its mosaic bit for bit. Runs only when EGAMI_RAW_CORPUS names a
    /// folder of RAW files.
    /// </summary>
    [Fact]
    public void EveryCorpusFileConvertsWithItsMosaicKept()
    {
        string? folder = Environment.GetEnvironmentVariable("EGAMI_RAW_CORPUS");
        Assert.SkipWhen(folder is null || !Directory.Exists(folder), "Set EGAMI_RAW_CORPUS to a folder of RAW files.");
        List<string> failures = [];
        foreach (string path in Directory.EnumerateFiles(folder!).Order())
        {
            byte[] raw = File.ReadAllBytes(path);
            Raster<Gray16> before;
            try
            {
                if (Raster.Probe(raw, Context).Format != RawFormat.Info)
                {
                    // A TIFF that Egami reads as an ordinary TIFF, not as camera RAW (the Canon 1Ds's .tif).
                    TestContext.Current.TestOutputHelper?.WriteLine($"{Path.GetFileName(path)}: not a RAW file to Egami");
                    continue;
                }

                before = Raster.Load<Gray16>(raw, Context, Mosaic);
            }
            catch (Exception e) when (e is EgamiException or NotSupportedException or InvalidCastException)
            {
                // What Egami cannot read as a mosaic (CR3's CRX data, a few old layouts, LinearRaw as Gray16) is not converted here.
                TestContext.Current.TestOutputHelper?.WriteLine($"{Path.GetFileName(path)}: not read as a mosaic, {e.Message}");
                continue;
            }

            using (before)
                try
                {
                    byte[] dng = Convert(raw, new DngWriterOptions { Preview = DngPreview.None });
                    using Raster<Gray16> after = Raster.Load<Gray16>(dng, Context, Mosaic);
                    bool same = before.Width == after.Width && before.Height == after.Height
                        && Enumerable.Range(0, before.Height).All(y => before.Row(y).SequenceEqual(after.Row(y)));
                    TestContext.Current.TestOutputHelper?.WriteLine($"{Path.GetFileName(path)}: {raw.Length:N0} → {dng.Length:N0} B, {(same ? "same" : "DIFFERENT")}");
                    if (!same)
                    {
                        failures.Add(Path.GetFileName(path));
                    }
                }
                catch (NotSupportedException e)
                {
                    TestContext.Current.TestOutputHelper?.WriteLine($"{Path.GetFileName(path)}: not supported, {e.Message}");
                }
                catch (InvalidCastException)
                {
                    // LinearRaw and float files load as other pixel types; the RGB tests cover those.
                }
                catch (EgamiException e)
                {
                    TestContext.Current.TestOutputHelper?.WriteLine($"{Path.GetFileName(path)}: FAILED, {e.Message}");
                    failures.Add(Path.GetFileName(path));
                }
        }

        Assert.Empty(failures);
    }
}
