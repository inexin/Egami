using Egami.Formats.Webp;
using Egami.Metadata;
using Egami.Pixels;
using Egami.Streaming;
using Egami.Tests.Formats.Gif;

namespace Egami.Tests.Formats.Webp;

/// <summary>
/// Animated WebP writing: GIF and APNG animations round-trip exactly as lossless WebP and closely as lossy, keep their delays and
/// loop counts, store only changed rectangles at even offsets, and write the same bytes streamed or saved.
/// </summary>
public class WebpAnimationWriteTests
{
    public static TheoryData<string> Files() => [.. AnimCorpus.Files("apng").Concat(AnimCorpus.Files("gif")).Where(f => !f.Contains("16", StringComparison.Ordinal))];

    [Theory]
    [MemberData(nameof(Files))]
    public void LosslessRoundTripsAreExact(string file)
    {
        using Raster<Rgba8> source = Raster.Load<Rgba8>(AnimCorpus.Read(file));
        foreach (int effort in new[] { 0, 4 })
        {
            byte[] written = Save(source, new WebpWriterOptions { Lossless = true, Effort = effort });
            using Raster<Rgba8> again = Raster.Load<Rgba8>(written);
            Assert.Equal(source.FrameCount, again.FrameCount);
            Assert.Empty(again.Metadata.Warnings);
            for (int i = 0; i < source.FrameCount; i++)
            {
                for (int y = 0; y < source.Height; y++)
                {
                    Assert.True(source.Frames[i].Row(y).SequenceEqual(again.Frames[i].Row(y)), $"{file} effort {effort}: frame {i} row {y}");
                }

                Assert.Equal(Math.Round(source.Frames[i].Metadata.Delay.TotalMilliseconds), again.Frames[i].Metadata.Delay.TotalMilliseconds);
            }

            if (source.FrameCount > 1)
            {
                Assert.Equal(Math.Min(source.Metadata.LoopCount, ushort.MaxValue), again.Metadata.LoopCount);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void LossyRoundTripsAreClose(string file)
    {
        using Raster<Rgba8> source = Raster.Load<Rgba8>(AnimCorpus.Read(file));
        foreach (WebpWriterOptions options in new[] { new WebpWriterOptions { Lossless = false, Effort = 4 }, new WebpWriterOptions { Lossless = false, Effort = 2 }, new WebpWriterOptions { Lossless = false, MixedFrames = true } })
        {
            using Raster<Rgba8> again = Raster.Load<Rgba8>(Save(source, options));
            Assert.Equal(source.FrameCount, again.FrameCount);
            for (int i = 0; i < source.FrameCount; i++)
            {
                double se = 0;
                long visible = 0;
                for (int y = 0; y < source.Height; y++)
                {
                    ReadOnlySpan<Rgba8> a = source.Frames[i].Row(y), b = again.Frames[i].Row(y);
                    for (int x = 0; x < a.Length; x++)
                    {
                        Assert.True(a[x].A == b[x].A, $"{file}: frame {i} alpha differs at ({x}, {y})");
                        if (a[x].A == 255)
                        {
                            double d = (0.299 * (a[x].R - b[x].R)) + (0.587 * (a[x].G - b[x].G)) + (0.114 * (a[x].B - b[x].B));
                            se += d * d;
                            visible++;
                        }
                    }
                }

                double psnr = visible == 0 || se == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 * visible / se);
                Assert.True(psnr >= 26, $"{file}: frame {i} at {psnr:F1} dB");
            }
        }
    }

    [Fact]
    public void LaterFramesStoreOnlyWhatChangedAtEvenOffsets()
    {
        using Raster<Rgba8> anim = Raster.Create(40, 30, Rgba8.Blue);
        Frame<Rgba8> second = anim.Frames.Add(anim.Frames[0]);
        second.Row(11).Slice(5, 3).Fill(Rgba8.Red);
        Frame<Rgba8> third = anim.Frames.Add(second);
        third.Row(21)[31] = new Rgba8(1, 2, 3, 128);
        using Raster<Rgba8> again = Raster.Load<Rgba8>(Save(anim, new WebpWriterOptions { Lossless = true }));
        Assert.Equal(new Rect(4, 10, 4, 2), again.Frames[1].Metadata.Bounds);
        Assert.Equal(FrameBlend.Over, again.Frames[1].Metadata.Blend);
        Assert.Equal(new Rect(30, 20, 2, 2), again.Frames[2].Metadata.Bounds);
        Assert.Equal(FrameBlend.Source, again.Frames[2].Metadata.Blend);
        Assert.Equal(new Rgba8(1, 2, 3, 128), again.Frames[2].Row(21)[31]);
        Assert.Equal(Rgba8.Red, again.Frames[2].Row(11)[6]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void StreamingWritesTheSameBytesAsSave(int which)
    {
        // A one-frame GIF and an animated one: a lone frame becomes a still either way.
        string file = AnimCorpus.Files("gif").Select(f => (f, Raster.Probe(AnimCorpus.Read(f)).FrameCount)).First(p => which == 0 ? p.FrameCount == 1 : p.FrameCount > 2).f;
        using Raster<Rgba8> source = Raster.Load<Rgba8>(AnimCorpus.Read(file));
        WebpWriterOptions options = new() { Lossless = true };
        byte[] saved = Save(source, options);
        foreach (bool seekable in new[] { true, false })
        {
            MemoryStream memory = new();
            Stream target = seekable ? memory : new ForwardOnly(memory);
            using (RasterEncoder<Rgba8> encoder = RasterEncoder.Create<Rgba8>(target, WebpFormat.Info, source.Width, source.Height, options, source.Metadata))
            {
                foreach (Frame<Rgba8> frame in source.Frames)
                {
                    using Raster<Rgba8> one = Raster.Create<Rgba8>(source.Width, source.Height);
                    for (int y = 0; y < source.Height; y++)
                    {
                        frame.Row(y).CopyTo(one.Row(y));
                    }

                    encoder.WriteFrame(one, frame.Metadata);
                }

                encoder.Complete();
            }

            byte[] streamed = memory.ToArray();
            int first = 0;
            while (first < Math.Min(saved.Length, streamed.Length) && saved[first] == streamed[first])
            {
                first++;
            }

            Assert.True(saved.AsSpan().SequenceEqual(streamed), $"seekable {seekable}: {streamed.Length} bytes against {saved.Length}, first difference at {first}");
        }
    }

    [Fact]
    public void DelaysAreRoundedAndCapped()
    {
        using Raster<Rgba8> anim = Raster.Create(8, 8, Rgba8.Blue);
        anim.Frames[0].Metadata.Delay = TimeSpan.FromMilliseconds(12.6);
        Frame<Rgba8> second = anim.Frames.Add(anim.Frames[0]);
        second.Row(1)[1] = Rgba8.Red;
        second.Metadata.Delay = TimeSpan.FromHours(10);
        using Raster<Rgba8> again = Raster.Load<Rgba8>(Save(anim, new WebpWriterOptions { Lossless = true }));
        Assert.Equal(TimeSpan.FromMilliseconds(13), again.Frames[0].Metadata.Delay);
        Assert.Equal(TimeSpan.FromMilliseconds(WebpWriter.MaxDelay), again.Frames[1].Metadata.Delay);
    }

    [Fact]
    public void FirstFrameOnlyWritesAStill()
    {
        using Raster<Rgba8> source = Raster.Load<Rgba8>(AnimCorpus.Read(AnimCorpus.Files("gif").First()));
        using Raster again = Raster.Load(Save(source, new WebpWriterOptions { FirstFrameOnly = true, Lossless = true }));
        Assert.Equal(1, again.FrameCount);
        Assert.False(Assert.IsType<WebpDetails>(again.Metadata.FormatDetails).Extended && again.Metadata.LoopCount != 0);
    }

    [Fact]
    public void MixedFramesAreNoLargerThanEitherKind()
    {
        using Raster<Rgba8> source = Raster.Load<Rgba8>(AnimCorpus.Read(AnimCorpus.Files("gif").First()));
        int lossless = Save(source, new WebpWriterOptions { Lossless = true }).Length;
        int lossy = Save(source, new WebpWriterOptions { Lossless = false }).Length;
        int mixed = Save(source, new WebpWriterOptions { Lossless = false, MixedFrames = true }).Length;
        Assert.True(mixed <= Math.Min(lossless, lossy) + 64, $"mixed {mixed}, lossless {lossless}, lossy {lossy}");
    }

    private static byte[] Save(Raster raster, WebpWriterOptions options)
    {
        using MemoryStream file = new();
        raster.Save(file, WebpFormat.Info, options);
        return file.ToArray();
    }

    /// <summary>A stream that cannot seek, over a memory stream.</summary>
    private sealed class ForwardOnly(MemoryStream inner) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }
}
