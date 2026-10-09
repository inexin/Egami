using System.Runtime.InteropServices;
using System.Text.Json;

using Egami.Formats;
using Egami.Formats.Gif;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Tests.Formats.Gif;

public class GifDecodeTests(ITestOutputHelper output)
{
    public static TheoryData<string> Files() => [.. AnimCorpus.Files("gif")];

    /// <summary>
    /// Pillow and ImageMagick agree on every frame of every file except where they differ on disposal; there the GIF89a
    /// specification and browsers decide: "restore to background" clears to transparent (ImageMagick; Pillow fills with the
    /// background color), and "restore to previous" puts back what was there before the frame (ImageMagick).
    /// </summary>
    [Theory]
    [MemberData(nameof(Files))]
    public void EveryFrameMatchesTheReference(string file)
    {
        byte[][] expected = AnimCorpus.Frames(file, "magick")!;
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(AnimCorpus.Read(file));
        Assert.Equal(expected.Length, raster.FrameCount);
        for (int i = 0; i < expected.Length; i++)
        {
            byte[] actual = Bytes(raster.Frames[i]);
            if (file == "gif/sprite-dispose3.gif" && i > 0)
            {
                // Every frame here restores to previous, the first included. Both tools then keep the first frame under the
                // second; the specification ("what was there prior to rendering") and Chrome restore the empty canvas. So each
                // later frame shows only its own opaque pixels, which match the reference, on transparency.
                expected[i] = OnlyOpaque(expected[i], actual);
            }

            int differing = Differing(expected[i], actual);
            output.WriteLine($"frame {i}: {differing} pixels differ");
            Assert.Equal(0, differing);
        }

        Assert.Empty(raster.Metadata.Warnings);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void ProbeCountsEveryFrame(string file)
    {
        JsonElement entry = AnimCorpus.Entry(file);
        RasterInfo info = Raster.Probe(AnimCorpus.Read(file));
        Assert.Equal(GifFormat.Info, info.Format);
        Assert.Equal((entry.GetProperty("width").GetInt32(), entry.GetProperty("height").GetInt32()), (info.Width, info.Height));
        Assert.Equal(entry.GetProperty("frames").GetInt32(), info.FrameCount);
        Assert.Equal(Rgba8.Layout, info.Layout);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void DelaysAndLoopCountMatchTheManifest(string file)
    {
        JsonElement entry = AnimCorpus.Entry(file);
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(AnimCorpus.Read(file));
        int[] delays = [.. entry.GetProperty("delays").EnumerateArray().Select(d => (int)d.GetDouble())];
        Assert.Equal(delays, raster.Frames.Select(f => (int)f.Metadata.Delay.TotalMilliseconds));

        // Pillow reports the NETSCAPE value (repeats after the first play); LoopCount is total plays, 0 for forever.
        JsonElement loop = entry.GetProperty("loop");
        int expected = loop.ValueKind == JsonValueKind.Null ? 1 : loop.GetInt32() == 0 ? 0 : loop.GetInt32() + 1;
        Assert.Equal(expected, raster.Metadata.LoopCount);
        Assert.Equal(loop.ValueKind == JsonValueKind.Null ? null : loop.GetInt32(), ((GifDetails)raster.Metadata.FormatDetails!).RepeatCount);
    }

    [Fact]
    public void NaturalTypeIsRgba8()
    {
        using Raster raster = Raster.Load(AnimCorpus.Read("gif/pan-local.gif"));
        Assert.Equal(Rgba8.Layout, raster.Layout);
        Assert.Equal("GIF", raster.Metadata.FormatName);
    }

    [Fact]
    public void FrameRangeKeepsOnlyTheChosenFramesButDrawsTheEarlierOnes()
    {
        byte[] file = AnimCorpus.Read("gif/pan-optimized.gif");
        byte[][] expected = AnimCorpus.Frames("gif/pan-optimized.gif", "magick")!;
        using Raster<Rgba8> first = Raster.Load<Rgba8>(file, options: new ReaderOptions { Frames = ..1 });
        Assert.Equal(1, first.FrameCount);
        Assert.Equal(expected[0], Bytes(first.Frames[0]));

        using Raster<Rgba8> middle = Raster.Load<Rgba8>(file, options: new ReaderOptions { Frames = 3..5 });
        Assert.Equal(2, middle.FrameCount);
        Assert.Equal(expected[3], Bytes(middle.Frames[0]));
        Assert.Equal(expected[4], Bytes(middle.Frames[1]));

        using Raster<Rgba8> last = Raster.Load<Rgba8>(file, options: new ReaderOptions { Frames = ^2.. });
        Assert.Equal(2, last.FrameCount);
        Assert.Equal(expected[^1], Bytes(last.Frames[1]));
    }

    [Fact]
    public void FrameMetadataDescribesTheStoredFrame()
    {
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(AnimCorpus.Read("gif/page-dispose-previous.gif"));
        Frame<Rgba8> frame = raster.Frames[1];
        Assert.Equal(new Rect(4, 6, 16, 12), frame.Metadata.Bounds);
        Assert.Equal(FrameDisposal.RestorePrevious, frame.Metadata.Disposal);
        GifFrameDetails details = Assert.IsType<GifFrameDetails>(frame.Metadata.FormatDetails);
        Assert.False(details.IsInterlaced);

        using Raster<Rgba8> interlaced = Raster.Load<Rgba8>(AnimCorpus.Read("gif/interlaced.gif"));
        Assert.True(Assert.IsType<GifFrameDetails>(interlaced.Frames[0].Metadata.FormatDetails).IsInterlaced);
    }

    [Fact]
    public void CommentsBecomeTextEntries()
    {
        using Raster raster = Raster.Load(AnimCorpus.Read("gif/comment.gif"));
        Assert.Equal(new TextEntry("Comment", "Made for Egami's tests"), Assert.Single(raster.Metadata.Text));
        Assert.True(Raster.Probe(AnimCorpus.Read("gif/comment.gif")).HasMetadata);
    }

    [Fact]
    public void Gif87aKeepsItsVersion()
    {
        using Raster raster = Raster.Load(AnimCorpus.Read("gif/gif87a.gif"));
        Assert.Equal("87a", ((GifDetails)raster.Metadata.FormatDetails!).Version);
    }

    internal static byte[] Bytes(Frame<Rgba8> frame)
    {
        byte[] bytes = new byte[frame.Plane.Width * frame.Plane.Height * 4];
        for (int y = 0; y < frame.Plane.Height; y++)
        {
            MemoryMarshal.AsBytes(frame.Row(y)).CopyTo(bytes.AsSpan(y * frame.Plane.Width * 4));
        }

        return bytes;
    }

    private static byte[] OnlyOpaque(byte[] reference, byte[] actual)
    {
        byte[] result = new byte[reference.Length];
        for (int i = 0; i < reference.Length; i += 4)
        {
            if (actual[i + 3] != 0)
            {
                reference.AsSpan(i, 4).CopyTo(result.AsSpan(i));
            }
        }

        return result;
    }

    private static int Differing(byte[] expected, byte[] actual)
    {
        int count = 0;
        for (int i = 0; i < expected.Length; i += 4)
        {
            // Fully transparent pixels are equal whatever their color.
            if (expected[i + 3] == 0 && actual[i + 3] == 0)
            {
                continue;
            }

            if (!expected.AsSpan(i, 4).SequenceEqual(actual.AsSpan(i, 4)))
            {
                count++;
            }
        }

        return count;
    }
}
