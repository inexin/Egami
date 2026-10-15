using Egami.Formats;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Tests.Streaming;

/// <summary>Phase 2.2 task 7: PNG, APNG and GIF decode while their bytes arrive, to exactly what Load gives, whatever the splits.</summary>
public class IncrementalTests
{
    public static TheoryData<string, int> Cases()
    {
        TheoryData<string, int> data = [];
        foreach (string file in StreamingCorpus.Files)
        {
            foreach (int split in (int[])[1, 7, 65_536, -1])
            {
                data.Add(file, split);
            }
        }

        return data;
    }

    public static TheoryData<string> Others() => [.. StreamingCorpus.Files];

    [Theory]
    [MemberData(nameof(Cases))]
    public void FramesEqualLoadWhateverTheSplits(string file, int split)
    {
        byte[] data = StreamingCorpus.Read(file);
        using Raster<Rgba8> expected = Raster.Load<Rgba8>(data);
        Random random = new(data.Length);
        using RasterDecoder decoder = RasterDecoder.OpenIncremental();
        Raster<Rgba8>? target = null;
        List<Raster<Rgba8>> frames = [];
        try
        {
            int at = 0;
            while (at < data.Length)
            {
                int n = Math.Min(data.Length - at, split > 0 ? split : random.Next(1, 3000));
                decoder.Append(data.AsSpan(at, n));
                at += n;
                Pump(decoder, ref target, frames);
            }

            decoder.EndOfInput();
            Pump(decoder, ref target, frames);
            Assert.Equal(DecodeStatus.Completed, decoder.Status);
            Assert.Equal(expected.FrameCount, frames.Count);
            for (int f = 0; f < frames.Count; f++)
            {
                StreamingCorpus.Equal(expected, f, frames[f], $"{file} split {split}");
            }

            Assert.True(decoder.IsMetadataComplete);
            Assert.Equal(expected.Metadata.Warnings, decoder.Metadata.Warnings);
            Assert.Equal(expected.Metadata.Text, decoder.Metadata.Text);
            Assert.Equal(expected.Metadata.IccProfile.ToArray(), decoder.Metadata.IccProfile.ToArray());
        }
        finally
        {
            target?.Dispose();
            frames.ForEach(f => f.Dispose());
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void RowsComeOutBeforeTheLastByte(string file, int split)
    {
        if (split != 7)
        {
            return;
        }

        // Read in bands of 3 rows as bytes arrive; a non-interlaced PNG hands out rows long before its end.
        byte[] data = StreamingCorpus.Read(file);
        using Raster<Rgba8> expected = Raster.Load<Rgba8>(data);
        using RasterDecoder decoder = RasterDecoder.OpenIncremental();
        Raster<Rgba8>? band = null;
        int frame = 0;
        int y = 0;
        int rowsBeforeEnd = 0;
        try
        {
            for (int at = 0; at < data.Length + split; at = Math.Min(at + split, data.Length))
            {
                if (at < data.Length)
                {
                    decoder.Append(data.AsSpan(at, Math.Min(split, data.Length - at)));
                }
                else
                {
                    decoder.EndOfInput();
                }

                if (!decoder.HeaderRead)
                {
                    continue;
                }

                band ??= Raster.Create<Rgba8>(decoder.Info.Width, 3);
                while (true)
                {
                    int rows = decoder.ReadRows(band);
                    for (int i = 0; i < rows; i++)
                    {
                        Assert.True(expected.Frames[frame].Row(y + i).SequenceEqual(band.Row(i)), $"{file} frame {frame} row {y + i}");
                    }

                    y += rows;
                    rowsBeforeEnd += at < data.Length ? rows : 0;
                    if (decoder.Status == DecodeStatus.FrameEnded && decoder.NextFrame())
                    {
                        frame++;
                        y = 0;
                        continue;
                    }

                    if (rows == 0 || decoder.Status != DecodeStatus.RowsAvailable)
                    {
                        break;
                    }
                }

                if (at == data.Length)
                {
                    break;
                }
            }

            if (decoder.Status == DecodeStatus.FrameEnded)
            {
                Assert.False(decoder.NextFrame());
            }

            Assert.Equal(DecodeStatus.Completed, decoder.Status);
            Assert.Equal(expected.FrameCount - 1, frame);
            bool interlaced = expected.Metadata.FormatDetails is Egami.Formats.Png.PngDetails { Interlaced: true };
            if (file.EndsWith(".png", StringComparison.Ordinal) && expected.FrameCount == 1 && !interlaced && expected.Height > 3 && expected.Metadata.Warnings.Count == 0)
            {
                Assert.True(rowsBeforeEnd > 0, $"{file}: no row came out before the last byte.");
            }
        }
        finally
        {
            band?.Dispose();
        }
    }

    [Theory]
    [MemberData(nameof(Others))]
    public void LargeChunksDecodeAtTheEnd(string file)
    {
        byte[] data = StreamingCorpus.Read(file);
        using Raster<Rgba8> expected = Raster.Load<Rgba8>(data);
        using RasterDecoder decoder = RasterDecoder.OpenIncremental();
        for (int at = 0; at < data.Length; at += 997)
        {
            decoder.Append(data.AsSpan(at, Math.Min(997, data.Length - at)));
        }

        decoder.EndOfInput();
        Assert.True(decoder.HeaderRead);
        using Raster<Rgba8> target = Raster.Create<Rgba8>(decoder.Info.Width, decoder.Info.Height);
        for (int f = 0; f < expected.FrameCount; f++)
        {
            Assert.NotNull(decoder.ReadFrame(target));
            StreamingCorpus.Equal(expected, f, target, file);
        }
    }

    [Fact]
    public void InfoWaitsForTheHeader()
    {
        byte[] data = StreamingCorpus.Read("ops/egret.png");
        using RasterDecoder decoder = RasterDecoder.OpenIncremental();
        decoder.Append(data.AsSpan(0, 20));
        Assert.False(decoder.HeaderRead);
        Assert.Equal(DecodeStatus.NeedsInput, decoder.Status);
        Assert.Throws<InvalidOperationException>(() => decoder.Info);
        decoder.Append(data.AsSpan(20, 2000));
        Assert.True(decoder.HeaderRead);
        Assert.Equal((480, 640), (decoder.Info.Width, decoder.Info.Height));
    }

    private static void Pump(RasterDecoder decoder, ref Raster<Rgba8>? target, List<Raster<Rgba8>> frames)
    {
        if (!decoder.HeaderRead)
        {
            return;
        }

        target ??= Raster.Create<Rgba8>(decoder.Info.Width, decoder.Info.Height);
        while (decoder.ReadFrame(target) is FrameMetadata)
        {
            frames.Add(target.Copy());
        }
    }
}

/// <summary>RenderPreview for interlaced PNG and for GIF frames in progress: the last preview is the image itself.</summary>
public class PreviewTests
{
    public static TheoryData<string> Interlaced() =>
    [
        .. StreamingCorpus.Files.Where(f => f.EndsWith(".png", StringComparison.Ordinal)
            && Raster.Load(StreamingCorpus.Read(f)).Metadata.FormatDetails is Egami.Formats.Png.PngDetails { Interlaced: true }
            && Raster.Probe(StreamingCorpus.Read(f)).FrameCount == 1),
    ];

    public static TheoryData<string> Gifs() => [.. StreamingCorpus.Files.Where(f => f.EndsWith(".gif", StringComparison.Ordinal))];

    [Theory]
    [MemberData(nameof(Interlaced))]
    public void AnAdam7PreviewSharpensIntoTheImage(string file)
    {
        byte[] data = StreamingCorpus.Read(file);
        using Raster<Rgba8> expected = Raster.Load<Rgba8>(data);
        using RasterDecoder decoder = RasterDecoder.OpenIncremental();
        Raster<Rgba8>? preview = null;
        try
        {
            for (int at = 0; at < data.Length; at += 11)
            {
                decoder.Append(data.AsSpan(at, Math.Min(11, data.Length - at)));
                if (decoder.HeaderRead)
                {
                    preview ??= Raster.Create<Rgba8>(decoder.Info.Width, decoder.Info.Height);
                    Assert.True(decoder.RenderPreview(preview));
                }
            }

            decoder.EndOfInput();
            Assert.True(decoder.RenderPreview(preview!));
            StreamingCorpus.Equal(expected, 0, preview!, file);
        }
        finally
        {
            preview?.Dispose();
        }
    }

    public static TheoryData<string> Progressive() => [.. StreamingCorpus.Files.Where(f => f.Contains("progressive", StringComparison.Ordinal) && f.EndsWith(".jpg", StringComparison.Ordinal))];

    [Theory]
    [MemberData(nameof(Progressive))]
    public void AProgressiveJpegPreviewSharpensIntoTheImage(string file)
    {
        byte[] data = StreamingCorpus.Read(file);
        using Raster<Rgb8> expected = Raster.Load<Rgb8>(data);
        using RasterDecoder decoder = RasterDecoder.OpenIncremental();
        Raster<Rgb8>? preview = null;
        try
        {
            for (int at = 0; at < data.Length; at += 97)
            {
                decoder.Append(data.AsSpan(at, Math.Min(97, data.Length - at)));
                if (decoder.HeaderRead)
                {
                    preview ??= Raster.Create<Rgb8>(decoder.Info.Width, decoder.Info.Height);
                    Assert.True(decoder.RenderPreview(preview));
                }
            }

            decoder.EndOfInput();
            Assert.True(decoder.RenderPreview(preview!));
            StreamingCorpus.Equal(expected, 0, preview!, file);

            // Rows read after previews are the image too.
            using Raster<Rgb8> frame = Raster.Create<Rgb8>(decoder.Info.Width, decoder.Info.Height);
            Assert.NotNull(decoder.ReadFrame(frame));
            StreamingCorpus.Equal(expected, 0, frame, file);
        }
        finally
        {
            preview?.Dispose();
        }
    }

    [Fact]
    public void ABaselineJpegHasNoPreview()
    {
        using RasterDecoder decoder = RasterDecoder.Open(StreamingCorpus.Read("jpeg/duck-420.jpg"));
        using Raster<Rgb8> preview = Raster.Create<Rgb8>(decoder.Info.Width, decoder.Info.Height);
        Assert.False(decoder.RenderPreview(preview));
    }

    [Theory]
    [MemberData(nameof(Gifs))]
    public void AGifPreviewShowsTheFrameInProgress(string file)
    {
        byte[] data = StreamingCorpus.Read(file);
        using Raster<Rgba8> expected = Raster.Load<Rgba8>(data);
        using RasterDecoder decoder = RasterDecoder.OpenIncremental();
        decoder.Append(data);
        using Raster<Rgba8> preview = Raster.Create<Rgba8>(decoder.Info.Width, decoder.Info.Height);
        using Raster<Rgba8> canvas = Raster.Create<Rgba8>(decoder.Info.Width, decoder.Info.Height);
        for (int f = 0; f < expected.FrameCount; f++)
        {
            // Every byte is there: the preview of the next frame is that frame.
            Assert.True(decoder.RenderPreview(preview));
            StreamingCorpus.Equal(expected, f, preview, $"{file} preview of frame {f}");
            Assert.NotNull(decoder.ReadFrame(canvas));
        }

        // Partial data previews without throwing.
        using RasterDecoder partial = RasterDecoder.OpenIncremental();
        for (int at = 0; at < data.Length; at += 37)
        {
            partial.Append(data.AsSpan(at, Math.Min(37, data.Length - at)));
            if (partial.HeaderRead)
            {
                partial.RenderPreview(preview);
                partial.ReadFrame(canvas);
            }
        }
    }
}
