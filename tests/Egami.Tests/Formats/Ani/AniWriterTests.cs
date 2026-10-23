using System.Buffers.Binary;

using Egami.Formats;
using Egami.Formats.Ani;
using Egami.Formats.Ico;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Tests.Formats.Ani;

/// <summary>
/// Writing animated cursors: frames stored once with a <c>seq </c> chunk when they repeat, delays as jiffies with a <c>rate</c>
/// chunk only when they differ, hotspots, sizes, title and artist, round trips and the same bytes for the same input.
/// </summary>
public class AniWriterTests
{
    private static readonly ReaderOptions Strict = new() { Recovery = RecoveryMode.Strict };

    [Fact]
    public void RepeatedFramesAreStoredOnceAndPlayedBackInOrder()
    {
        using Raster<Rgba8> animation = Animation(32, [1, 2, 3, 2], [100, 200, 100, 100], [new(1, 2), new(3, 4), new(5, 6), new(3, 4)]);
        byte[] data = Save(animation, new AniWriterOptions { Title = "Spinner", Artist = "Egami tests" });

        Assert.Equal(AniFormat.Info, Raster.Probe(data).Format);
        Dictionary<string, byte[]> chunks = Chunks(data);
        Assert.Equal([0, 1, 2, 1], Words(chunks["seq "]));
        Assert.Equal([6, 12, 6, 6], Words(chunks["rate"]));
        ReadOnlySpan<byte> header = chunks["anih"];
        Assert.Equal(36u, BinaryPrimitives.ReadUInt32LittleEndian(header));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(header[4..]));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(header[8..]));
        Assert.Equal(6u, BinaryPrimitives.ReadUInt32LittleEndian(header[28..]));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(header[32..]));

        using Raster<Rgba8> loaded = Raster.Load<Rgba8>(data, options: Strict);
        Assert.Empty(loaded.Metadata.Warnings);
        Assert.Equal(4, loaded.FrameCount);
        for (int i = 0; i < 4; i++)
        {
            Assert.True(AniReaderTests.SamePixels(animation.Frames[i], loaded.Frames[i]), $"Frame {i} differs.");
            Assert.Equal(animation.Frames[i].Metadata.Delay.TotalMilliseconds, loaded.Frames[i].Metadata.Delay.TotalMilliseconds, 1.0);
            Assert.Equal(((IcoDetails)animation.Frames[i].Metadata.FormatDetails!).Hotspot, ((IcoDetails)loaded.Frames[i].Metadata.FormatDetails!).Hotspot);
        }

        AniDetails details = (AniDetails)loaded.Metadata.FormatDetails!;
        Assert.Equal(("Spinner", "Egami tests"), (details.Title, details.Artist));
        Assert.Equal(3, details.StoredFrames);
        Assert.Equal([0, 1, 2, 1], details.Steps);
    }

    [Fact]
    public void SamePixelsWithAnotherHotspotAreAnotherFrame()
    {
        using Raster<Rgba8> animation = Animation(16, [1, 1], [100, 100], [new(0, 0), new(8, 8)]);
        Dictionary<string, byte[]> chunks = Chunks(Save(animation, new AniWriterOptions()));
        Assert.False(chunks.ContainsKey("seq "));
        Assert.False(chunks.ContainsKey("rate"));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(chunks["anih"].AsSpan(4)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(chunks["anih"].AsSpan(32)));
    }

    [Fact]
    public void EqualDelaysNeedNoRateChunkAndTheSameInputGivesTheSameBytes()
    {
        using Raster<Rgba8> animation = Animation(24, [1, 2, 3], [50, 50, 50], [new(1, 1), new(1, 1), new(1, 1)]);
        byte[] first = Save(animation, new AniWriterOptions());
        Assert.Equal(first, Save(animation, new AniWriterOptions()));
        Dictionary<string, byte[]> chunks = Chunks(first);
        Assert.False(chunks.ContainsKey("rate"));
        Assert.False(chunks.ContainsKey("seq "));
        Assert.False(chunks.ContainsKey("LIST INFO"));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(chunks["anih"].AsSpan(28)));
        using Raster<Rgba8> loaded = Raster.Load<Rgba8>(first, options: Strict);
        Assert.All(loaded.Frames, f => Assert.Equal(50, f.Metadata.Delay.TotalMilliseconds, 0.001));
    }

    [Fact]
    public void SizesGiveEveryFrameEachSizeWithTheHotspotScaled()
    {
        using Raster<Rgba8> animation = Animation(64, [1, 2], [100, 200], [new(10, 20), new(10, 20)]);
        byte[] data = Save(animation, new AniWriterOptions { Sizes = AniWriterOptions.CursorSizes, Hotspot = new Point(32, 16) });
        RasterInfo info = Raster.Probe(data);
        Assert.Equal(5, info.ImageCount);
        Assert.Equal((128, 128), (info.Width, info.Height));
        using RasterList all = Raster.LoadAll(data);
        Assert.Equal([128, 96, 64, 48, 32], all.Select(r => r.Width));
        Raster<Rgba8> small = (Raster<Rgba8>)Raster.Load<Rgba8>(data, options: new ReaderOptions { MinimumSize = new Size(32, 32) });
        using (small)
        {
            Assert.Equal(32, small.Width);
            Assert.All(small.Frames, f => Assert.Equal(new Point(16, 8), ((IcoDetails)f.Metadata.FormatDetails!).Hotspot));
        }
    }

    [Fact]
    public void CursorSizesAreAllPngSinceBmpEntriesWouldStartPastWhatWindowsReads()
    {
        // Windows' LoadCursorFromFile rejects an animated cursor when an entry of a frame starts past 65535; as BMP the 128 entry
        // of the cursor sizes would start at 68982.
        using Raster<Rgba8> animation = Animation(64, [1, 2], [100, 200], [new(10, 20), new(10, 20)]);
        foreach (List<(int Size, bool Png, int Start)> frame in Entries(Save(animation, new AniWriterOptions { Sizes = AniWriterOptions.CursorSizes })))
        {
            Assert.Equal([32, 48, 64, 96, 128], frame.Select(e => e.Size));
            Assert.All(frame, e => Assert.True(e.Png));
            Assert.All(frame, e => Assert.InRange(e.Start, 0, ushort.MaxValue));
        }
    }

    [Fact]
    public void SizesThatFitAreAllBmpEvenWhereAFrameWasPng()
    {
        // The frames' own IcoDetails say PNG; with BMP and PNG entries in one frame Windows may draw another size's BMP entry.
        using Raster<Rgba8> animation = Animation(48, [1, 2], [100, 200], [new(1, 2), new(1, 2)]);
        Assert.Equal(IcoEntryEncoding.Png, ((IcoDetails)animation.Frames[0].Metadata.FormatDetails!).Encoding);
        List<List<(int Size, bool Png, int Start)>> frames = Entries(Save(animation, new AniWriterOptions { Sizes = [16, 32, 48, 64] }));
        Assert.All(frames, frame => Assert.All(frame, e => Assert.False(e.Png)));

        // One size keeps the frame's own encoding, as before.
        Assert.All(Entries(Save(animation, new AniWriterOptions())), frame => Assert.True(Assert.Single(frame).Png));
    }

    [Fact]
    public void BmpEntriesTooLargeForWindowsAreWrittenWithAWarning()
    {
        List<EgamiDiagnostic> warnings = [];
        EgamiContext context = EgamiContext.Default with { Diagnostics = warnings.Add };
        using Raster<Rgba8> animation = Animation(32, [1], [100], [default], context);
        byte[] data = Save(animation, new AniWriterOptions { Sizes = AniWriterOptions.CursorSizes, Encoding = IcoEntryEncoding.Bmp });
        Assert.Single(warnings, w => w.Id == "Save.AniTooLarge");
        Assert.All(Entries(data)[0], e => Assert.False(e.Png));

        warnings.Clear();
        Save(animation, new AniWriterOptions { Sizes = [32, 48, 64], Encoding = IcoEntryEncoding.Bmp });
        Assert.DoesNotContain(warnings, w => w.Id == "Save.AniTooLarge");
    }

    [Fact]
    public void ShortDelaysBecome100MillisecondsAndALoopCountWarns()
    {
        List<EgamiDiagnostic> warnings = [];
        EgamiContext context = EgamiContext.Default with { Diagnostics = warnings.Add };
        using Raster<Rgba8> animation = Animation(16, [1, 2], [0, 10], [default, default], context);
        animation.Metadata.LoopCount = 3;
        byte[] data = Save(animation, new AniWriterOptions());
        Assert.Contains(warnings, w => w.Id == "Save.AniDelayRaised");
        Assert.Contains(warnings, w => w.Id == "Save.AniLoops");
        Assert.Equal(6u, BinaryPrimitives.ReadUInt32LittleEndian(Chunks(data)["anih"].AsSpan(28)));
    }

    [Fact]
    public void ALargeAnimationIsFittedTo256OnceWithOneWarning()
    {
        List<EgamiDiagnostic> warnings = [];
        EgamiContext context = EgamiContext.Default with { Diagnostics = warnings.Add };
        using Raster<Rgba8> animation = Animation(300, [1, 2, 1], [100, 100, 100], [new(150, 299), new(150, 299), new(150, 299)], context);
        byte[] data = Save(animation, new AniWriterOptions());
        Assert.Single(warnings, w => w.Id == "Save.IcoFitted");
        using Raster<Rgba8> loaded = Raster.Load<Rgba8>(data, options: Strict);
        Assert.Equal((256, 256), (loaded.Width, loaded.Height));
        Assert.Equal(3, loaded.FrameCount);
        Assert.Equal(new Point(128, 255), ((IcoDetails)loaded.Frames[0].Metadata.FormatDetails!).Hotspot);
        Assert.Equal(IcoEntryEncoding.Png, ((IcoDetails)loaded.Frames[0].Metadata.FormatDetails!).Encoding);
    }

    [Fact]
    public void LoadSaveLoadKeepsFramesHotspotsTitleAndArtist()
    {
        byte[][] cursors = [AniBuilder.Cursor(1, new Point(3, 4), 32), AniBuilder.Cursor(2, new Point(5, 6), 32)];
        AniBuilder builder = new() { Sequence = [0, 1, 1, 0], Rates = [6, 12, 18, 3], Title = "Busy", Artist = "Someone" };
        builder.Frames.AddRange(cursors);
        using Raster<Rgba8> first = Raster.Load<Rgba8>(builder.Build(), options: Strict);
        byte[] saved = Save(first, AniWriterOptions.Default);
        using Raster<Rgba8> second = Raster.Load<Rgba8>(saved, options: Strict);
        Assert.Equal(first.FrameCount, second.FrameCount);
        for (int i = 0; i < first.FrameCount; i++)
        {
            Assert.True(AniReaderTests.SamePixels(first.Frames[i], second.Frames[i]));
            Assert.Equal(first.Frames[i].Metadata.Delay, second.Frames[i].Metadata.Delay);
            Assert.Equal(((IcoDetails)first.Frames[i].Metadata.FormatDetails!).Hotspot, ((IcoDetails)second.Frames[i].Metadata.FormatDetails!).Hotspot);
        }

        AniDetails details = (AniDetails)second.Metadata.FormatDetails!;
        Assert.Equal(("Busy", "Someone"), (details.Title, details.Artist));
        Assert.Equal([0, 1, 1, 0], details.Steps);
        Assert.Equal(saved, Save(second, AniWriterOptions.Default));
        Dictionary<string, byte[]> stripped = Chunks(Save(first, new AniWriterOptions { StripMetadata = true }));
        Assert.False(stripped.ContainsKey("LIST INFO"));
    }

    [Fact]
    public void TitleAndArtistTravelAsTextEntries()
    {
        List<EgamiDiagnostic> warnings = [];
        EgamiContext context = EgamiContext.Default with { Diagnostics = warnings.Add };
        using Raster<Rgba8> animation = Animation(16, [1, 2], [100, 100], [default, default], context);
        animation.Metadata.Text.Add(new TextEntry("Title", "From PNG"));
        animation.Metadata.Text.Add(new TextEntry("Author", "Someone"));
        byte[] data = Save(animation, new AniWriterOptions());
        Assert.DoesNotContain(warnings, w => w.Id == "Save.MetadataDropped");

        using Raster<Rgba8> loaded = Raster.Load<Rgba8>(data, options: Strict);
        Assert.Equal(("From PNG", "Someone"), (((AniDetails)loaded.Metadata.FormatDetails!).Title, ((AniDetails)loaded.Metadata.FormatDetails!).Artist));
        Assert.Equal(["Title=From PNG", "Author=Someone"], loaded.Metadata.Text.Select(t => $"{t.Key}={t.Value}"));
        Assert.True(Raster.Probe(data).HasMetadata);

        // The options come first, then the details, then the text entries.
        using Raster<Rgba8> again = Raster.Load<Rgba8>(Save(loaded, new AniWriterOptions { Artist = "Options" }), options: Strict);
        Assert.Equal(("From PNG", "Options"), (((AniDetails)again.Metadata.FormatDetails!).Title, ((AniDetails)again.Metadata.FormatDetails!).Artist));
    }

    [Fact]
    public void AnyPixelTypeAndAOneFrameRasterSave()
    {
        using Raster<Rgba16> deep = Raster.Create<Rgba16>(8, 8);
        using MemoryStream stream = new();
        deep.Save(stream, AniFormat.Info);
        using Raster<Rgba8> loaded = Raster.Load<Rgba8>(stream.ToArray(), options: Strict);
        Assert.Equal(1, loaded.FrameCount);
        Assert.Equal((8, 8), (loaded.Width, loaded.Height));
    }

    [Fact]
    public void BadSizesThrow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AniWriterOptions { Sizes = [16, 257] });
        Assert.Throws<ArgumentException>(() => new AniWriterOptions { Sizes = [32, 32] });
        Assert.Throws<ArgumentException>(() => new AniWriterOptions { Sizes = [] });
        Assert.Equal([32, 48, 64, 96, 128], AniWriterOptions.CursorSizes);
    }

    /// <summary>Builds an animation of <see cref="AniBuilder.Pattern"/> frames with the given seeds, delays (ms) and hotspots.</summary>
    internal static Raster<Rgba8> Animation(int size, int[] seeds, int[] delays, Point[] hotspots, EgamiContext? context = null)
    {
        Raster<Rgba8> animation = Raster.Create<Rgba8>(size, size, context);
        for (int i = 0; i < seeds.Length; i++)
        {
            using Raster<Rgba8> pattern = AniBuilder.Pattern(size, size, seeds[i]);
            Frame<Rgba8> frame = i == 0 ? animation.Frames[0] : animation.Frames.Add(pattern.Frames[0]);
            for (int y = 0; i == 0 && y < size; y++)
            {
                pattern.Row(y).CopyTo(frame.Row(y));
            }

            frame.Metadata.Delay = TimeSpan.FromMilliseconds(delays[i]);
            frame.Metadata.FormatDetails = new IcoDetails { Hotspot = hotspots[i] };
        }

        return animation;
    }

    private static byte[] Save(Raster raster, AniWriterOptions options)
    {
        using MemoryStream stream = new();
        raster.Save(stream, AniFormat.Info, options);
        return stream.ToArray();
    }

    /// <summary>Lists the file's top-level chunks by id, a LIST by "LIST" and its form type.</summary>
    private static Dictionary<string, byte[]> Chunks(byte[] data)
    {
        Dictionary<string, byte[]> chunks = [];
        int at = 12;
        while (at + 8 <= data.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(data, at, 4);
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 4));
            byte[] body = data.AsSpan(at + 8, length).ToArray();
            chunks[id == "LIST" ? "LIST " + System.Text.Encoding.ASCII.GetString(body, 0, 4) : id] = body;
            at += 8 + length + (length & 1);
        }

        return chunks;
    }

    /// <summary>Each stored frame's directory: every entry's size, whether it is PNG and where in the frame its data starts.</summary>
    private static List<List<(int Size, bool Png, int Start)>> Entries(byte[] data)
    {
        byte[] list = Chunks(data)["LIST fram"];
        List<List<(int, bool, int)>> frames = [];
        for (int at = 4; at + 8 <= list.Length;)
        {
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(list.AsSpan(at + 4));
            ReadOnlySpan<byte> icon = list.AsSpan(at + 8, length);
            List<(int, bool, int)> entries = [];
            for (int i = 0; i < BinaryPrimitives.ReadUInt16LittleEndian(icon[4..]); i++)
            {
                ReadOnlySpan<byte> entry = icon.Slice(6 + (16 * i), 16);
                int start = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
                entries.Add((entry[0] == 0 ? 256 : entry[0], icon[start] == 0x89, start));
            }

            frames.Add(entries);
            at += 8 + length + (length & 1);
        }

        return frames;
    }

    private static int[] Words(byte[] data)
    {
        int[] values = new int[data.Length / 4];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i * 4));
        }

        return values;
    }
}
