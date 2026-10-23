using System.Runtime.InteropServices;

using Egami.Formats;
using Egami.Formats.Ani;
using Egami.Formats.Ico;
using Egami.Metadata;
using Egami.Pixels;
using Egami.Streaming;

namespace Egami.Tests.Formats.Ani;

/// <summary>
/// Animated cursors built byte by byte (<see cref="AniBuilder"/>) against the cursors they hold: steps played out from the
/// <c>seq </c> list, delays from the rates, one size for the whole animation, hotspots, the details, raw-bitmap frames, and
/// damaged and hostile files in both recovery modes.
/// </summary>
public class AniReaderTests
{
    private static readonly ReaderOptions Strict = new() { Recovery = RecoveryMode.Strict };

    [Fact]
    public void StepsPlayTheSequenceWithTheirRates()
    {
        byte[][] cursors = [AniBuilder.Cursor(1, new Point(3, 4), 32), AniBuilder.Cursor(2, new Point(5, 6), 32), AniBuilder.Cursor(3, new Point(7, 8), 32)];
        AniBuilder builder = new() { Sequence = [0, 1, 2, 1, 0], Rates = [6, 12, 18, 3, 60], Title = "Busy", Artist = "Egami tests" };
        builder.Frames.AddRange(cursors);
        byte[] data = builder.Build();

        using Raster<Rgba8> ani = Raster.Load<Rgba8>(data, options: Strict);
        Assert.Equal(5, ani.FrameCount);
        Assert.Equal((32, 32), (ani.Width, ani.Height));
        Assert.Equal("ANI", ani.Metadata.FormatName);
        Assert.Equal(0, ani.Metadata.LoopCount);
        Assert.Empty(ani.Metadata.Warnings);
        int[] sequence = [0, 1, 2, 1, 0];
        int[] rates = [6, 12, 18, 3, 60];
        for (int step = 0; step < 5; step++)
        {
            using Raster<Rgba8> cursor = Raster.Load<Rgba8>(cursors[sequence[step]]);
            Frame<Rgba8> frame = ani.Frames[step];
            Assert.True(SamePixels(cursor.Frames[0], frame), $"Step {step} differs from frame {sequence[step]}.");
            Assert.Equal(TimeSpan.FromSeconds(rates[step] / 60.0).TotalMilliseconds, frame.Metadata.Delay.TotalMilliseconds, 0.001);
            Assert.Equal(FrameDisposal.ClearToBackground, frame.Metadata.Disposal);
            Assert.Equal(FrameBlend.Source, frame.Metadata.Blend);
            IcoDetails details = (IcoDetails)frame.Metadata.FormatDetails!;
            Assert.Equal(((IcoDetails)cursor.Metadata.FormatDetails!).Hotspot, details.Hotspot);
            Assert.NotNull(details.Hotspot);
        }

        AniDetails ani2 = (AniDetails)ani.Metadata.FormatDetails!;
        Assert.Equal("Busy", ani2.Title);
        Assert.Equal("Egami tests", ani2.Artist);
        Assert.Equal(3, ani2.StoredFrames);
        Assert.Equal(sequence, ani2.Steps);
        Assert.Equal([new Size(32, 32)], ani2.Sizes);
        Assert.False(ani2.RawBitmaps);
        Assert.Equal(TimeSpan.FromSeconds(10 / 60.0).TotalMilliseconds, ani2.DefaultDelay.TotalMilliseconds, 0.001);
    }

    [Fact]
    public void WithoutSequenceOrRatesFramesPlayInOrderAtTheDefaultRate()
    {
        AniBuilder builder = new() { DefaultRate = 4, InfoFirst = false };
        builder.Frames.Add(AniBuilder.Cursor(1, new Point(0, 0), 16));
        builder.Frames.Add(AniBuilder.Cursor(2, new Point(1, 1), 16));
        using Raster<Rgba8> ani = Raster.Load<Rgba8>(builder.Build(), options: Strict);
        Assert.Equal(2, ani.FrameCount);
        Assert.All(ani.Frames, f => Assert.Equal(TimeSpan.FromTicks((long)Math.Round(4 * TimeSpan.TicksPerSecond / 60.0)), f.Metadata.Delay));
        Assert.Equal([0, 1], ((AniDetails)ani.Metadata.FormatDetails!).Steps);
        Assert.Null(((AniDetails)ani.Metadata.FormatDetails!).Title);
    }

    [Fact]
    public void ProbeAndDetectionUseTheChunksWithoutPixels()
    {
        AniBuilder builder = new() { Sequence = [0, 0, 1] };
        builder.Frames.Add(AniBuilder.Cursor(1, new Point(2, 2), 16, 32, 48));
        builder.Frames.Add(AniBuilder.Cursor(2, new Point(2, 2), 16, 32, 48));
        byte[] data = builder.Build();
        RasterInfo info = Raster.Probe(data);
        Assert.Equal(AniFormat.Info, info.Format);
        Assert.Equal((48, 48), (info.Width, info.Height));
        Assert.Equal(3, info.FrameCount);
        Assert.Equal(3, info.ImageCount);
        Assert.Equal(0, info.LoopCount);
        Assert.True(AniFormat.Info.Matches(data));
        Assert.Contains(AniFormat.Info, EgamiContext.Default.Formats);
    }

    [Fact]
    public void WebpIsNotTakenForAnAnimatedCursor()
    {
        byte[] webp = [.. "RIFF"u8, 4, 0, 0, 0, .. "WEBP"u8];
        Assert.False(AniFormat.Info.Matches(webp));
    }

    [Fact]
    public void LoadPicksOneSizeAndLoadAllGivesEverySize()
    {
        AniBuilder builder = new();
        builder.Frames.Add(AniBuilder.Cursor(1, new Point(8, 8), 16, 32, 48));
        builder.Frames.Add(AniBuilder.Cursor(2, new Point(8, 8), 16, 32, 48));
        byte[] data = builder.Build();

        using Raster<Rgba8> largest = Raster.Load<Rgba8>(data, options: Strict);
        Assert.Equal((48, 48), (largest.Width, largest.Height));

        using Raster<Rgba8> small = Raster.Load<Rgba8>(data, options: new ReaderOptions { MinimumSize = new Size(20, 20) });
        Assert.Equal((32, 32), (small.Width, small.Height));
        Assert.Equal(2, small.FrameCount);

        using RasterList all = Raster.LoadAll<Rgba8>(data, options: Strict);
        Assert.Equal([48, 32, 16], all.Select(r => r.Width));
        Assert.All(all, r => Assert.Equal(2, r.FrameCount));
        using RasterList cursor = Raster.LoadAll<Rgba8>(builder.Frames[1]);
        for (int i = 0; i < 3; i++)
        {
            Raster<Rgba8> animation = (Raster<Rgba8>)all[i];
            Raster<Rgba8> entry = (Raster<Rgba8>)cursor.First(c => c.Width == animation.Width);
            Assert.True(SamePixels(entry.Frames[0], animation.Frames[1]));
            Assert.Equal(((IcoDetails)entry.Metadata.FormatDetails!).Hotspot, ((IcoDetails)animation.Frames[1].Metadata.FormatDetails!).Hotspot);
        }

        Assert.True(SamePixels(((Raster<Rgba8>)all[1]).Frames[0], small.Frames[0]));
    }

    [Fact]
    public void AFrameWithoutTheSizeIsResizedWithItsHotspot()
    {
        AniBuilder builder = new();
        builder.Frames.Add(AniBuilder.Cursor(1, new Point(10, 20), 32));
        builder.Frames.Add(AniBuilder.Cursor(2, new Point(10, 20), 64));
        byte[] data = builder.Build();
        using Raster<Rgba8> ani = Raster.Load<Rgba8>(data);
        Assert.Equal((64, 64), (ani.Width, ani.Height));
        Assert.Contains(ani.Metadata.Warnings, w => w.Id == "Ani.SizeMissing");
        using Raster<Rgba8> small = Raster.Load<Rgba8>(builder.Frames[0]);
        Point original = ((IcoDetails)small.Metadata.FormatDetails!).Hotspot!.Value;
        Assert.Equal(new Point(original.X * 2, original.Y * 2), ((IcoDetails)ani.Frames[0].Metadata.FormatDetails!).Hotspot);
    }

    [Fact]
    public void FrameRangesAndOtherPixelTypesWork()
    {
        AniBuilder builder = new() { Sequence = [2, 1, 0, 1] };
        for (int i = 0; i < 3; i++)
        {
            builder.Frames.Add(AniBuilder.Cursor(i, new Point(1, 1), 24));
        }

        byte[] data = builder.Build();
        using Raster<Rgba8> all = Raster.Load<Rgba8>(data);
        using Raster<Rgba8> middle = Raster.Load<Rgba8>(data, options: new ReaderOptions { Frames = 1..3 });
        Assert.Equal(2, middle.FrameCount);
        Assert.True(SamePixels(all.Frames[1], middle.Frames[0]));
        Assert.True(SamePixels(all.Frames[2], middle.Frames[1]));

        using Raster<Rgba16> wide = Raster.Load<Rgba16>(data);
        Assert.Equal(4, wide.FrameCount);
        Rgba8 expected = all.Frames[3].Row(5)[7];
        Rgba16 actual = wide.Frames[3].Row(5)[7];
        Assert.Equal(expected.R * 257, actual.R);
        Assert.Equal(expected.A * 257, actual.A);

        using RasterDecoder decoder = RasterDecoder.Open(data);
        using Raster rows = decoder.ReadAll();
        Assert.Equal(4, rows.FrameCount);
    }

    [Fact]
    public void RawBitmapFramesAreRead()
    {
        using Raster<Rgba8> pattern = AniBuilder.Pattern(8, 6, 3);

        // Bare 32-bit bottom-up rows at the header's size.
        byte[] rows = new byte[8 * 6 * 4];
        for (int y = 0; y < 6; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                Rgba8 p = pattern.Row(5 - y)[x];
                (rows[((y * 8) + x) * 4], rows[(((y * 8) + x) * 4) + 1], rows[(((y * 8) + x) * 4) + 2], rows[(((y * 8) + x) * 4) + 3]) = (p.B, p.G, p.R, p.A);
            }
        }

        // The same pixels as a whole BMP file, and as a DIB without its file header.
        using MemoryStream bmp = new();
        pattern.Save(bmp, Egami.Formats.Bmp.BmpFormat.Info);
        byte[] file = bmp.ToArray();
        byte[] dib = file[14..];

        AniBuilder builder = new() { Flags = 0, RawHeader = (8, 6, 32) };
        builder.Frames.Add(rows);
        builder.Frames.Add(file);
        builder.Frames.Add(dib);
        using Raster<Rgba8> ani = Raster.Load<Rgba8>(builder.Build(), options: Strict);
        Assert.Equal(3, ani.FrameCount);
        Assert.True(((AniDetails)ani.Metadata.FormatDetails!).RawBitmaps);
        for (int i = 0; i < 3; i++)
        {
            Assert.True(SamePixels(pattern.Frames[0], ani.Frames[i]), $"Raw frame {i} differs.");
            Assert.Null(((IcoDetails)ani.Frames[i].Metadata.FormatDetails!).Hotspot);
        }
    }

    [Fact]
    public void AnIconInARawFileIsReadAsAnIcon()
    {
        AniBuilder builder = new() { Flags = 0 };
        builder.Frames.Add(AniBuilder.Cursor(1, new Point(1, 1), 16));
        using Raster<Rgba8> ani = Raster.Load<Rgba8>(builder.Build());
        Assert.Contains(ani.Metadata.Warnings, w => w.Id == "Ani.IconInRawFile");
        Assert.NotNull(((IcoDetails)ani.Frames[0].Metadata.FormatDetails!).Hotspot);
    }

    [Fact]
    public void AnOversizedHeaderIsDamage()
    {
        AniBuilder builder = new() { HeaderChunkSize = 40 };
        builder.Frames.Add(AniBuilder.Cursor(1, new Point(1, 1), 16));
        byte[] data = builder.Build();
        using Raster ani = Raster.Load(data);
        Assert.Contains(ani.Metadata.Warnings, w => w.Id == "Ani.HeaderSize");
        Assert.Throws<CorruptDataException>(() => Raster.Load(data, options: Strict));

        builder.HeaderChunkSize = 20;
        Assert.Throws<CorruptDataException>(() => Raster.Load(builder.Build()));
    }

    [Fact]
    public void BadStepsAreLeftOut()
    {
        AniBuilder builder = new() { Sequence = [0, 5, 1], Rates = [6, 6, 0] };
        builder.Frames.Add(AniBuilder.Cursor(1, new Point(1, 1), 16));
        builder.Frames.Add(AniBuilder.Cursor(2, new Point(1, 1), 16));
        byte[] data = builder.Build();
        using Raster ani = Raster.Load(data);
        Assert.Equal(2, ani.FrameCount);
        Assert.Contains(ani.Metadata.Warnings, w => w.Id == "Ani.SequenceIndex");
        Assert.Contains(ani.Metadata.Warnings, w => w.Id == "Ani.ZeroRate");
        Assert.Equal(TimeSpan.FromTicks((long)Math.Round(10 * TimeSpan.TicksPerSecond / 60.0)), ((Raster<Rgba8>)ani).Frames[1].Metadata.Delay);
        Assert.Equal([0, 1], ((AniDetails)ani.Metadata.FormatDetails!).Steps);
        Assert.Throws<CorruptDataException>(() => Raster.Load(data, options: Strict));
    }

    [Fact]
    public void ADamagedFrameDropsItsSteps()
    {
        byte[] broken = AniBuilder.Cursor(2, new Point(1, 1), 16);
        broken[6 + 12] = 0xFF; // the entry's data offset now points past the end
        AniBuilder builder = new() { Sequence = [0, 1, 0] };
        builder.Frames.Add(AniBuilder.Cursor(1, new Point(1, 1), 16));
        builder.Frames.Add(broken);
        byte[] data = builder.Build();
        using Raster ani = Raster.Load(data);
        Assert.Equal(2, ani.FrameCount);
        Assert.Contains(ani.Metadata.Warnings, w => w.Id == "Ani.FrameDamaged");
        Assert.Throws<CorruptDataException>(() => Raster.Load(data, options: Strict));
    }

    [Fact]
    public void ATruncatedFileKeepsItsWholeFrames()
    {
        AniBuilder builder = new();
        builder.Frames.Add(AniBuilder.Cursor(1, new Point(1, 1), 16));
        builder.Frames.Add(AniBuilder.Cursor(2, new Point(1, 1), 16));
        byte[] whole = builder.Build();
        byte[] cut = whole[..(whole.Length - 30)];
        using Raster ani = Raster.Load(cut);
        Assert.Equal(1, ani.FrameCount);
        Assert.Contains(ani.Metadata.Warnings, w => w.Id == "Ani.RiffSize");
        Assert.Contains(ani.Metadata.Warnings, w => w.Id == "Ani.FrameDamaged");
        Assert.Throws<CorruptDataException>(() => Raster.Load(cut, options: Strict));
    }

    [Fact]
    public void FilesWithoutHeaderOrFramesThrow()
    {
        AniBuilder empty = new();
        Assert.Throws<CorruptDataException>(() => Raster.Load(empty.Build()));

        byte[] headerless = [.. "RIFF"u8, 4, 0, 0, 0, .. "ACON"u8];
        Assert.Throws<CorruptDataException>(() => Raster.Load(headerless));
    }

    [Fact]
    public void CountsAndFlagsThatDisagreeAreNoted()
    {
        AniBuilder builder = new() { DeclaredFrames = 7, DeclaredSteps = 9, Flags = 1 | 2 };
        builder.Frames.Add(AniBuilder.Cursor(1, new Point(1, 1), 16));
        byte[] data = builder.Build();
        using Raster ani = Raster.Load(data);
        Assert.Contains(ani.Metadata.Warnings, w => w.Id == "Ani.FrameCount");
        Assert.Contains(ani.Metadata.Warnings, w => w.Id == "Ani.StepCount");
        Assert.Contains(ani.Metadata.Warnings, w => w.Id == "Ani.SequenceFlag");
        Assert.Throws<CorruptDataException>(() => Raster.Load(data, options: Strict));
    }

    [Fact]
    public void UnpaddedOddChunksAreFollowed()
    {
        AniBuilder builder = new() { Pad = false, Title = "Odd", Artist = "Name" };
        builder.Frames.Add(AniBuilder.Cursor(1, new Point(1, 1), 16));
        using Raster ani = Raster.Load(builder.Build(), options: Strict);
        Assert.Equal("Odd", ((AniDetails)ani.Metadata.FormatDetails!).Title);
        Assert.Equal("Name", ((AniDetails)ani.Metadata.FormatDetails!).Artist);
    }

    [Fact]
    public void StepsCountAgainstTheFrameLimit()
    {
        AniBuilder builder = new() { Sequence = [.. Enumerable.Repeat(0, 50)] };
        builder.Frames.Add(AniBuilder.Cursor(1, new Point(1, 1), 16));
        EgamiContext few = EgamiContext.Default with { Limits = SafetyLimits.Default with { MaxFrames = 10 } };
        Assert.Throws<LimitExceededException>(() => Raster.Load(builder.Build(), few));
    }

    [Fact]
    public void ARateListShorterThanTheStepsUsesTheDefault()
    {
        AniBuilder builder = new() { Rates = [30], DefaultRate = 3 };
        builder.Frames.Add(AniBuilder.Cursor(1, new Point(1, 1), 16));
        builder.Frames.Add(AniBuilder.Cursor(2, new Point(1, 1), 16));
        using Raster<Rgba8> ani = Raster.Load<Rgba8>(builder.Build());
        Assert.Contains(ani.Metadata.Warnings, w => w.Id == "Ani.RateCount");
        Assert.Equal(500, ani.Frames[0].Metadata.Delay.TotalMilliseconds, 0.001);
        Assert.Equal(50, ani.Frames[1].Metadata.Delay.TotalMilliseconds, 0.001);
    }

    [Fact]
    public void DetailsCopyDeeply()
    {
        AniDetails details = new() { Title = "a", Steps = [1, 2], Sizes = [new Size(3, 3)], StoredFrames = 2, RawBitmaps = true, DefaultDelay = TimeSpan.FromSeconds(1) };
        AniDetails copy = (AniDetails)details.Copy();
        Assert.NotSame(details.Steps, copy.Steps);
        Assert.Equal(details.Steps, copy.Steps);
        Assert.Equal(details.Sizes, copy.Sizes);
        Assert.Equal((details.Title, details.StoredFrames, details.RawBitmaps, details.DefaultDelay), (copy.Title, copy.StoredFrames, copy.RawBitmaps, copy.DefaultDelay));
    }

    internal static bool SamePixels(Frame<Rgba8> expected, Frame<Rgba8> actual)
    {
        if (expected.Plane.Width != actual.Plane.Width || expected.Plane.Height != actual.Plane.Height)
        {
            return false;
        }

        for (int y = 0; y < expected.Plane.Height; y++)
        {
            if (!MemoryMarshal.AsBytes(expected.Row(y)).SequenceEqual(MemoryMarshal.AsBytes(actual.Row(y))))
            {
                return false;
            }
        }

        return true;
    }
}
