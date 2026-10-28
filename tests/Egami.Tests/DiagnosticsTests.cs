using Egami.Formats;
using Egami.Formats.Png;
using Egami.Pixels;
using Egami.Streaming;
using Egami.Tests.Formats.Png;

namespace Egami.Tests;

public class DiagnosticsTests
{
    /// <summary>A small PNG with <paramref name="badChunks"/> tEXt chunks whose CRC is wrong, each a Png.ChecksumMismatch warning.</summary>
    internal static byte[] DamagedPng(int badChunks)
    {
        PngBuilder builder = PngReaderTests.Build(PngColorType.Rgb, 8, false, false, 8, 8, seed: 3);
        for (int i = 0; i < badChunks; i++)
        {
            builder.ChunksBeforeImageData.Add(("tEXt", PngBuilder.Latin1($"Key{i}", "x")));
        }

        builder.BadCrcChunk = "tEXt";
        return builder.Build();
    }

    [Fact]
    public void WarningsCarryIdsWithNoHandler()
    {
        using Raster raster = Raster.Load(DamagedPng(1));
        EgamiDiagnostic warning = Assert.Single(raster.Metadata.Warnings);
        Assert.Equal(DiagnosticLevel.Warning, warning.Level);
        Assert.Equal("Png", warning.Source);
        Assert.Equal("Png.ChecksumMismatch", warning.Id);
        Assert.StartsWith("The tEXt chunk CRC is", warning.Message, StringComparison.Ordinal);
        Assert.NotNull(warning.Offset);
        Assert.Equal($"Png.ChecksumMismatch: {warning.Message}", warning.ToString());
    }

    [Fact]
    public void TheHandlerGetsWhatMetadataWarningsHolds()
    {
        List<EgamiDiagnostic> seen = [];
        EgamiContext context = new() { Diagnostics = seen.Add };
        using Raster raster = Raster.Load(DamagedPng(3), context);
        Assert.Equal(3, raster.Metadata.Warnings.Count);
        Assert.Equal(raster.Metadata.Warnings, seen);
    }

    [Fact]
    public void EveryLoadPathDeliversEachWarningOnce()
    {
        byte[] file = DamagedPng(2);
        void Check(Action<EgamiContext> load)
        {
            List<EgamiDiagnostic> seen = [];
            load(new EgamiContext { Diagnostics = d => seen.Add(d) });
            Assert.Equal(["Png.ChecksumMismatch", "Png.ChecksumMismatch"], seen.Select(d => d.Id));
        }

        Check(c => Raster.Load(file, c).Dispose());
        Check(c => Raster.Load<Rgba8>(file, c).Dispose());
        Check(c => Raster.Load(file, c, new ReaderOptions { MinimumSize = new Size(4, 4) }).Dispose());
        Check(c => Raster.Load(file, c, new ReaderOptions { ConvertColorTo = Egami.Colors.ColorProfile.DisplayP3 }).Dispose());
        Check(c => Raster.LoadAsync(new NonSeekableStream(file), c).GetAwaiter().GetResult().Dispose());
        Check(c => Raster.LoadAsync<Rgba8>(new MemoryStream(file), c).GetAwaiter().GetResult().Dispose());
        Check(c =>
        {
            using RasterDecoder decoder = RasterDecoder.Open(file, c);
            using Raster<Rgba8> band = Raster.Create<Rgba8>(decoder.Info.Width, 3);
            while (decoder.ReadRows(band) > 0)
            {
            }
        });
        Check(c =>
        {
            using RasterDecoder decoder = RasterDecoder.OpenIncremental(c);
            decoder.Append(file);
            decoder.EndOfInput();
            decoder.ReadFrame<Rgba8>()!.Dispose();
        });
        Check(c => Raster.Transcode(new MemoryStream(file), new MemoryStream(), PngFormat.Info, context: c));
    }

    [Fact]
    public void RepeatsAreCappedInBothPlaces()
    {
        List<EgamiDiagnostic> seen = [];
        using Raster raster = Raster.Load(DamagedPng(15), new EgamiContext { Diagnostics = seen.Add });
        Assert.Equal(11, raster.Metadata.Warnings.Count);
        Assert.All(raster.Metadata.Warnings, w => Assert.Equal("Png.ChecksumMismatch", w.Id));
        Assert.Equal("The same problem came up 5 more times; only the first 10 are listed.", raster.Metadata.Warnings[10].Message);
        Assert.Equal(raster.Metadata.Warnings, seen);
    }

    [Fact]
    public void AThrowingHandlerFailsTheCallAndLeaksNothing()
    {
        PooledPixelMemorySource memory = new();
        EgamiContext context = new() { Memory = memory, Diagnostics = d => throw new InvalidDataException(d.Message) };
        Assert.Throws<InvalidDataException>(() => Raster.Load(DamagedPng(1), context));
        Assert.Throws<InvalidDataException>(() =>
        {
            using RasterDecoder decoder = RasterDecoder.Open(DamagedPng(1), context);
            decoder.ReadFrame<Rgba8>()!.Dispose();
        });
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Assert.Equal(0, memory.Usage.LiveBytes);

        // A clean file never calls the handler.
        Raster.Load(DamagedPng(0), context).Dispose();
    }

    [Fact]
    public void LevelIsValidatedAndDefaultsToWarning()
    {
        Assert.Equal(DiagnosticLevel.Warning, new EgamiContext().DiagnosticLevel);
        Assert.Null(new EgamiContext().Diagnostics);
        Assert.Throws<ArgumentOutOfRangeException>(() => new EgamiContext { DiagnosticLevel = (DiagnosticLevel)7 });
    }

    [Fact]
    public void InfoMessagesAreNotFormattedBelowTheLevel()
    {
        Counting value = new();
        int delivered = 0;
        DiagnosticReporter warningOnly = new EgamiContext { Diagnostics = _ => delivered++ }.StartDiagnostics();
        warningOnly.Emit(DiagnosticLevel.Info, "Test.Info", $"value {value}");
        DiagnosticReporter off = new EgamiContext().StartDiagnostics();
        off.Emit(DiagnosticLevel.Warning, "Test.Warning", $"value {value}");
        Assert.Equal(0, value.Formatted);
        Assert.Equal(0, delivered);

        DiagnosticReporter debug = new EgamiContext { Diagnostics = _ => delivered++, DiagnosticLevel = DiagnosticLevel.Debug }.StartDiagnostics();
        debug.Emit(DiagnosticLevel.Info, "Test.Info", $"value {value}");
        Assert.Equal(1, value.Formatted);
        Assert.Equal(1, delivered);
    }

    [Fact]
    public void ThePublicReporterFiltersCapsAndSummarizes()
    {
        List<EgamiDiagnostic> seen = [];
        EgamiContext context = new() { Diagnostics = seen.Add, DiagnosticLevel = DiagnosticLevel.Info };
        using (DiagnosticReporter reporter = context.StartDiagnostics())
        {
            Assert.True(reporter.IsEnabled(DiagnosticLevel.Warning));
            Assert.False(reporter.IsEnabled(DiagnosticLevel.Debug));
            Assert.True(reporter.IsEnabled(DiagnosticLevel.Info));
            reporter.Report(DiagnosticLevel.Debug, "Avi.IndexRead", "Not delivered.");
            for (int i = 0; i < 13; i++)
            {
                reporter.Report(DiagnosticLevel.Warning, "Avi.IndexRebuilt", $"Rebuilt {i}.", offset: i, frame: i);
            }

            reporter.Report(new EgamiDiagnostic(DiagnosticLevel.Info, "Mkv", "Mkv.UnknownSizeCluster", "A cluster has no size.", null, 2));
            Assert.Equal(11, seen.Count);
        }

        Assert.Equal(12, seen.Count);
        Assert.Equal(new EgamiDiagnostic(DiagnosticLevel.Warning, "Avi", "Avi.IndexRebuilt", "Rebuilt 0.", 0, 0), seen[0]);
        Assert.Equal("Mkv.UnknownSizeCluster", seen[10].Id);
        Assert.Equal(new EgamiDiagnostic(DiagnosticLevel.Warning, "Avi", "Avi.IndexRebuilt", "The same problem came up 3 more times; only the first 10 are listed.", null, null), seen[11]);

        Assert.Throws<ArgumentException>(() => context.StartDiagnostics().Report(DiagnosticLevel.Info, "NoDot", "x"));
        Assert.Throws<ArgumentException>(() => context.StartDiagnostics().Report(DiagnosticLevel.Info, "A.B", string.Empty));

        DiagnosticReporter off = new EgamiContext().StartDiagnostics();
        Assert.False(off.IsEnabled(DiagnosticLevel.Warning));
        Assert.Same(off, EgamiContext.Default.StartDiagnostics());
        off.Report(DiagnosticLevel.Warning, "Avi.IndexRebuilt", "Goes nowhere.");
        off.Dispose();
    }

    [Fact]
    public void WarningsATakeBackRemovesAreNeverDelivered()
    {
        List<EgamiDiagnostic> seen = [];
        WarningList list = new();
        list.Bind(seen.Add);
        list.Add("Test.Kept", "Kept.");
        list.Add("Test.Taken", "Taken back.");
        list.RemoveRange(1, 1);
        list.Flush();
        list.Add("Test.Later", "Later.");
        list.Insert(0, EgamiDiagnostic.Create(DiagnosticLevel.Warning, "Test.First", "First."));
        list.Flush();
        list.Flush();
        Assert.Equal(["Test.Kept", "Test.First", "Test.Later"], seen.Select(d => d.Id));
        Assert.Equal(["Test.First", "Test.Kept", "Test.Later"], list.Select(d => d.Id));
    }

    [Fact]
    public void CopiesDoNotDeliverAgain()
    {
        List<EgamiDiagnostic> seen = [];
        using Raster raster = Raster.Load(DamagedPng(1), new EgamiContext { Diagnostics = seen.Add });
        Egami.Metadata.RasterMetadata copy = raster.Metadata.Copy();
        copy.WarningList.Flush();
        Assert.Single(copy.Warnings);
        Assert.Single(seen);
    }

    [Fact]
    public void PluginWarningsNeedAnId()
    {
        EgamiInput input = new(new MemoryStream([1, 2, 3]), EgamiContext.Default, default);
        using Raster<Rgba8> raster = Raster.Create<Rgba8>(1, 1);
        Assert.Throws<ArgumentException>(() => input.Warn(raster, "NoDot", "Problem."));
        Assert.Throws<ArgumentException>(() => input.Warn(raster, ".Lead", "Problem."));
        input.Warn(raster, "Pcx.PaletteMissing", "The palette is missing.", 12);
        Assert.Equal(new EgamiDiagnostic(DiagnosticLevel.Warning, "Pcx", "Pcx.PaletteMissing", "The palette is missing.", 12, null), Assert.Single(raster.Metadata.Warnings));

        // An open image has metadata but no raster.
        Egami.Metadata.RasterMetadata open = new();
        Assert.Throws<ArgumentException>(() => input.Warn(open, "NoDot", "Problem."));
        input.Warn(open, "Sgi.RowsMissing", "Rows are missing.");
        Assert.Equal("Sgi.RowsMissing", Assert.Single(open.Warnings).Id);
        input.Dispose();
    }

    [Fact]
    public void PluginsCheckFrameSizes()
    {
        EgamiInput input = new(new MemoryStream([1, 2, 3]), new EgamiContext { Limits = new SafetyLimits { MaxWidth = 100, MaxPixels = 1000 } }, default);
        input.CheckFrameSize(100, 10);
        Assert.Equal("MaxWidth", Assert.Throws<LimitExceededException>(() => input.CheckFrameSize(101, 1)).LimitName);
        Assert.Equal("MaxPixels", Assert.Throws<LimitExceededException>(() => input.CheckFrameSize(100, 11)).LimitName);
        input.Dispose();
    }

    [Fact]
    public void ParallelDecodesLoseNoEvents()
    {
        byte[] file = DamagedPng(4);
        int count = 0;
        EgamiContext context = new() { Diagnostics = _ => Interlocked.Increment(ref count) };
        Parallel.For(0, 200, _ => Raster.Load(file, context).Dispose());
        Assert.Equal(800, count);
    }

    [Fact]
    public void DamagedMetadataReportsOnceAtLoadOrFirstUse()
    {
        using Raster<Rgba8> image = Raster.Create<Rgba8>(4, 4);
        byte[] exif = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "images", "jpeg", "exif-tags.jpg"));
        int exifAt = exif.AsSpan().IndexOf("Exif\0\0"u8) + 6;
        exif[exifAt + 4] = 0xFF;
        image.Metadata.Exif = Egami.Metadata.ExifData.Parse(exif.AsSpan(exifAt, 512));
        image.Metadata.Xmp = "<x:xmpmeta"u8.ToArray();
        image.Metadata.SetIptcSource(new byte[] { 0x1C, 2, 25, 0, 200, 1, 2 }, long.MaxValue);
        image.Metadata.IccProfile = new byte[200];
        image.Metadata.WarningList.RemoveRange(0, image.Metadata.WarningList.Count);
        MemoryStream file = new();
        image.Save(file, PngFormat.Info);

        List<EgamiDiagnostic> seen = [];
        using Raster raster = Raster.Load(file.ToArray(), new EgamiContext { Diagnostics = seen.Add });
        Assert.Equal(["Exif.Damaged"], seen.Select(d => d.Id).Distinct());
        Assert.Null(raster.Metadata.XmpData);
        Assert.Null(raster.Metadata.XmpData);
        Assert.Equal("Xmp.Damaged", seen[^1].Id);
        int count = seen.Count;
        _ = raster.Metadata.Iptc;
        _ = raster.Metadata.Iptc;
        Assert.Equal("Iptc.Damaged", seen[^1].Id);
        Assert.Equal(count + 1, seen.Count);
        Assert.Equal(Egami.Colors.ColorProfile.Srgb, raster.Metadata.GetColorProfile());
        Assert.Equal(Egami.Colors.ColorProfile.Srgb, raster.Metadata.GetColorProfile());
        Assert.Equal("Icc.Unusable", seen[^1].Id);
        Assert.Equal(count + 2, seen.Count);
        Assert.Equal(raster.Metadata.Warnings, seen);
    }

    [Fact]
    public void LoadsReportTheirDecisionsAtInfoAndDebug()
    {
        List<EgamiDiagnostic> seen = [];
        EgamiContext context = new() { Diagnostics = seen.Add, DiagnosticLevel = DiagnosticLevel.Debug };
        Raster.Load(DamagedPng(0), context).Dispose();
        EgamiDiagnostic detected = Assert.Single(seen);
        Assert.Equal((DiagnosticLevel.Info, "Detect", "Detect.Format"), (detected.Level, detected.Source, detected.Id));
        Assert.Equal("The data is PNG, found by its signature.", detected.Message);

        seen.Clear();
        EgamiContext limited = context with { Limits = SafetyLimits.Default with { MaxPixels = 10 } };
        Assert.Throws<LimitExceededException>(() => Raster.Load(DamagedPng(0), limited));
        Assert.Equal(["Detect.Format", "Limits.Exceeded"], seen.Select(d => d.Id));
        Assert.Contains("MaxPixels is 10", seen[1].Message, StringComparison.Ordinal);

        seen.Clear();
        byte[] gif = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "images", "gif", "pan-local.gif"));
        using Raster all = Raster.Load(gif);
        Raster.Load(gif, context, new ReaderOptions { Frames = 1.. }).Dispose();
        Assert.Equal($"ReaderOptions.Frames is 1..^0: {all.FrameCount - 1} frames were read and the rest skipped.", seen.Single(d => d.Id == "Decode.FramesSelected").Message);

        seen.Clear();
        Raster.LoadAll(new NonSeekableStream(DamagedPng(0)), context).Dispose();
        Assert.Contains(seen, d => d.Id == "Streaming.ReadWhole");

        seen.Clear();
        byte[] jpeg = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "images", "jpeg", "duck-q100.jpg"));
        Raster.Load(jpeg, context, new ReaderOptions { MinimumSize = new Size(40, 40), ConvertColorTo = Egami.Colors.ColorProfile.DisplayP3 }).Dispose();
        Assert.Contains(seen, d => d.Id == "Decode.ScaleChosen" && d.Message.Contains("decodes at 1/", StringComparison.Ordinal));
        Assert.Contains(seen, d => d.Id == "Color.ConvertedOnLoad" && d.Message.Contains("to Display P3", StringComparison.Ordinal));

        seen.Clear();
        byte[] progressive = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "images", "jpeg", "duck-progressive.jpg"));
        EgamiInput input = new(new MemoryStream(progressive), context with { MaxParallelism = 4 }, default);
        Egami.Formats.Jpeg.JpegHeader header = Egami.Formats.Jpeg.JpegSegmentReader.ReadHeader(input, ReaderOptions.Default);
        Egami.Formats.Jpeg.JpegReader.Decode<Rgba8>(input, header, ReaderOptions.Default, forcePipeline: true).Dispose();
        input.Dispose();
        Assert.Contains(seen, d => d.Id == "Parallel.Bands");

        seen.Clear();
        using (RasterDecoder incremental = RasterDecoder.OpenIncremental(context))
        {
            incremental.Append(jpeg.AsSpan(0, 100));
            incremental.Append(jpeg.AsSpan(100));
        }

        Assert.Contains(seen, d => d.Id == "Streaming.BufferGrown");

        seen.Clear();
        Raster.Transcode(new NonSeekableStream(DamagedPng(0)), new MemoryStream(), PngFormat.Info, ops => ops.Grayscale(), context: context);
        Assert.Contains(seen, d => d.Id == "Streaming.Replayed");

        seen.Clear();
        using (Raster<Rgba8> image = Raster.Create<Rgba8>(32, 32, context))
        {
            image.Apply(ops => ops.Crop(new Rect(0, 0, 16, 16)).Resize(8, 8).Grayscale().Invert(), TestContext.Current.CancellationToken);
        }

        Assert.Contains("The chain of 4 steps runs as 2:", Assert.Single(seen).Message, StringComparison.Ordinal);

        // At the default level none of them is built.
        seen.Clear();
        Raster.Load(DamagedPng(0), context with { DiagnosticLevel = DiagnosticLevel.Warning }).Dispose();
        Assert.Empty(seen);
    }

    [Fact]
    public void SavesReportWhatTheyLose()
    {
        List<EgamiDiagnostic> seen = [];
        EgamiContext context = new() { Diagnostics = seen.Add };
        List<string> Save(Raster raster, Egami.Formats.FormatInfo format, Egami.Formats.WriterOptions? options = null)
        {
            seen.Clear();
            raster.Save(new MemoryStream(), format, options);
            return [.. seen.Select(d => d.Id)];
        }

        using Raster<Rgba16> translucent = Raster.Create<Rgba16>(8, 8, context);
        translucent.Row(0)[0] = new Rgba16(1000, 2000, 3000, 4000);
        translucent.Metadata.Text.Add(new Egami.Metadata.TextEntry("Title", "x"));
        translucent.Metadata.Xmp = "<x:xmpmeta xmlns:x='adobe:ns:meta/'/>"u8.ToArray();
        Assert.Equal(["Save.AlphaDropped", "Save.DepthReduced", "Save.MetadataDropped"], Save(translucent, Egami.Formats.Jpeg.JpegFormat.Info));
        Assert.Contains("JPEG cannot hold text other than Comment", seen[2].Message, StringComparison.Ordinal);
        Assert.Equal(["Save.MetadataDropped"], Save(translucent, Egami.Formats.Qoi.QoiFormat.Info, new Egami.Formats.Qoi.QoiWriterOptions()).Where(id => id != "Save.DepthReduced"));
        Assert.Contains("XMP or text entries", seen.Single(d => d.Id == "Save.MetadataDropped").Message, StringComparison.Ordinal);

        // A plugin writer reports through EncodeStart.ReportLosses, as core writers do.
        Assert.Equal(["Save.AlphaDropped", "Save.DepthReduced", "Save.MetadataDropped"], Save(translucent, LossyPlugin.Info));
        Assert.StartsWith("PLUG ", seen[0].Message, StringComparison.Ordinal);

        // Lossless saves report nothing; asking for the loss is not a loss either.
        Assert.Empty(Save(translucent, PngFormat.Info));
        Assert.Equal(["Save.MetadataDropped"], Save(translucent, Egami.Formats.Tiff.TiffFormat.Info));
        Assert.Equal("TIFF cannot hold text entries; it was not written.", seen[0].Message);
        Assert.Empty(Save(translucent, Egami.Formats.Jpeg.JpegFormat.Info, new Egami.Formats.Jpeg.JpegWriterOptions { Background = new Rgba8(0, 0, 0, 255), StripMetadata = true, Precision = 12 }));

        using Raster<Rgba8> frames = Raster.Load<Rgba8>(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "images", "gif", "pan-local.gif")), context);
        Assert.Contains("Save.FramesDropped", Save(frames, Egami.Formats.Bmp.BmpFormat.Info));
        Assert.DoesNotContain("Save.FramesDropped", Save(frames, PngFormat.Info));

        using Raster<Rgba8> noisy = Raster.Create<Rgba8>(64, 64, context);
        for (int y = 0; y < 64; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                noisy.Row(y)[x] = new Rgba8((byte)(x * 4), (byte)(y * 4), (byte)(x ^ y), 255);
            }
        }

        Assert.Equal(["Save.ColorsQuantized"], Save(noisy, Egami.Formats.Gif.GifFormat.Info));
        Assert.Contains("Save.ColorsQuantized", Save(noisy, PngFormat.Info, new PngWriterOptions { ColorType = PngColorType.Palette }));

        // Writers' own choices are Info, off by default.
        EgamiContext info = context with { DiagnosticLevel = DiagnosticLevel.Info };
        using Raster<Rgb8> plain = Raster.Create<Rgb8>(8, 8, info);
        Assert.Equal(["Png.ColorTypeChosen"], Save(plain, PngFormat.Info));
        Assert.Equal(["Jpeg.EncodingChosen"], Save(plain, Egami.Formats.Jpeg.JpegFormat.Info));
        Assert.Contains("YCbCr 4:2:0 at quality", seen[0].Message, StringComparison.Ordinal);
        Assert.Equal(["Webp.EncodingChosen"], Save(plain, Egami.Formats.Webp.WebpFormat.Info));
    }

    private sealed class Counting : ISpanFormattable
    {
        public int Formatted { get; private set; }

        public string ToString(string? format, IFormatProvider? formatProvider)
        {
            Formatted++;
            return "counted";
        }

        public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
        {
            Formatted++;
            charsWritten = 0;
            return "counted".AsSpan().TryCopyTo(destination) && (charsWritten = 7) > 0;
        }
    }

    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}

/// <summary>A writer that keeps 8-bit RGB only, for the plugin loss report.</summary>
internal static class LossyPlugin
{
    public static Egami.Formats.FormatInfo Info { get; } = new()
    {
        Name = "PLUG",
        SignatureLength = 0,
        Matches = _ => false,
        Writer = new Writer(),
    };

    private sealed class Writer : Egami.Formats.IFormatWriter, Egami.Formats.IRowFormatWriter
    {
        public void Write<TPixel>(Raster<TPixel> raster, Stream output, Egami.Formats.WriterOptions options)
            where TPixel : unmanaged, IPixelType<TPixel> => raster.Save(output, Info, options);

        public Egami.Formats.RowEncoder<TPixel> Start<TPixel>(Stream output, Egami.Formats.EncodeStart<TPixel> start)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            start.ReportLosses("PLUG", Egami.Formats.SaveKeeps.None);
            return new Encoder<TPixel>();
        }
    }

    private sealed class Encoder<TPixel> : Egami.Formats.RowEncoder<TPixel>
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        public override void WriteRow(ReadOnlySpan<TPixel> row)
        {
        }

        public override void Complete()
        {
        }
    }
}
