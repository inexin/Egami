using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;

using Egami.Formats.Webp;
using Egami.Pixels;

namespace Egami.Tests.Formats.Webp;

/// <summary>The public VP8 stream decoder: its frames, hidden frames, broken data with and without concealment, memory and limits.</summary>
public class Vp8StreamDecoderTests
{
    private static List<byte[]> Packets(string clip) => Vp8VideoCorpus.ReadIvf(Path.Combine(Vp8VideoCorpus.Clips, clip + ".ivf"));

    private static string Md5(YuvRaster<byte> raster)
    {
        using MemoryStream packed = new();
        foreach (YuvPlane<byte> plane in new[] { raster.Y, raster.U!, raster.V! })
        {
            for (int y = 0; y < plane.Height; y++)
            {
                packed.Write(plane.Row(y));
            }
        }

#pragma warning disable CA5351 // vpxdec's frame hashes are MD5; nothing here is security
        return Convert.ToHexStringLower(MD5.HashData(packed.ToArray()));
#pragma warning restore CA5351
    }

    [Theory]
    [InlineData("v0-altref")]
    [InlineData("v0-odd-97x61")]
    [InlineData("v3-pan")]
    public void CopiedFramesMatchLibvpx(string clip)
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Vp8VideoCorpus.ClipReferences, clip + ".json")));
        string[] md5 = [.. json.RootElement.GetProperty("md5").EnumerateArray().Select(e => e.GetString()!)];
        using Vp8StreamDecoder decoder = new();
        YuvRaster<byte>? frame = null;
        int shown = 0, hidden = 0;
        foreach (byte[] packet in Packets(clip))
        {
            Vp8FrameInfo info = decoder.Decode(packet);
            Assert.False(info.IsConcealed);
            if (!info.IsShown)
            {
                hidden++;
                Assert.Throws<InvalidOperationException>(() => decoder.CopyFrameTo(frame!));
                continue;
            }

            frame ??= YuvRaster.Create(info.Width, info.Height, ChromaFormat.Yuv420, YuvColor.Bt601Limited);
            decoder.CopyFrameTo(frame);
            Assert.Equal(md5[shown++], Md5(frame));
        }

        Assert.Equal(md5.Length, shown);
        Assert.Equal(clip != "v3-pan", hidden > 0);
        frame?.Dispose();
    }

    [Fact]
    public void TheDestinationMustBe420AtTheFrameSize()
    {
        using Vp8StreamDecoder decoder = new();
        Vp8FrameInfo info = decoder.Decode(Packets("v0-pan")[0]);
        using YuvRaster<byte> small = YuvRaster.Create(info.Width - 1, info.Height, ChromaFormat.Yuv420, YuvColor.Bt601Limited);
        using YuvRaster<byte> full = YuvRaster.Create(info.Width, info.Height, ChromaFormat.Yuv444, YuvColor.Bt601Limited);
        Assert.Throws<ArgumentException>(() => decoder.CopyFrameTo(small));
        Assert.Throws<ArgumentException>(() => decoder.CopyFrameTo(full));
    }

    [Fact]
    public void AStreamStartsAtAKeyFrame()
    {
        List<byte[]> packets = Packets("v0-pan");
        using Vp8StreamDecoder decoder = new();
        Assert.Throws<CorruptDataException>(() => decoder.Decode(packets[1]));
        Assert.True(decoder.Decode(packets[0]).IsKeyFrame);
        Assert.False(decoder.Decode(packets[1]).IsKeyFrame);

        // After a reset, an inter frame has nothing to predict from again.
        decoder.Reset();
        Assert.Throws<CorruptDataException>(() => decoder.Decode(packets[2]));
    }

    [Fact]
    public void TruncatedFramesThrowOrAreConcealed()
    {
        List<byte[]> packets = Packets("v0-pan");
        byte[] cut = packets[3][..(packets[3].Length / 2)];
        using Vp8StreamDecoder strict = new();
        strict.Decode(packets[0]);
        strict.Decode(packets[1]);
        strict.Decode(packets[2]);
        Assert.Throws<CorruptDataException>(() => strict.Decode(cut));

        using Vp8StreamDecoder lenient = new() { ConcealErrors = true };
        lenient.Decode(packets[0]);
        lenient.Decode(packets[1]);
        lenient.Decode(packets[2]);
        Vp8FrameInfo info = lenient.Decode(cut);
        Assert.True(info.IsConcealed);
        Assert.True(info.IsShown);
        using YuvRaster<byte> frame = YuvRaster.Create(info.Width, info.Height, ChromaFormat.Yuv420, YuvColor.Bt601Limited);
        lenient.CopyFrameTo(frame);

        // Unreadable data shows the previous frame again; the stream goes on.
        Assert.True(lenient.Decode([0x01, 0x02]).IsConcealed);
        Assert.False(lenient.Decode(packets[4]).IsConcealed);
    }

    [Fact]
    public void ConcealmentStartsAnInterFrameFromGray()
    {
        List<byte[]> packets = Packets("v0-pan");
        using Vp8StreamDecoder decoder = new() { ConcealErrors = true };

        // No key frame has given the size yet: nothing to show.
        Vp8FrameInfo first = decoder.Decode(packets[1]);
        Assert.True(first.IsConcealed);
        Assert.False(first.IsShown);

        decoder.Decode(packets[0]);
        decoder.Reset();
        Vp8FrameInfo info = decoder.Decode(packets[1]);
        Assert.True(info.IsConcealed);
        Assert.True(info.IsShown);
    }

    [Fact]
    public void FrameBuffersComeFromTheContextsMemoryAndGoBack()
    {
        CountingSource memory = new();
        EgamiContext context = EgamiContext.Default with { Memory = memory };
        using (Vp8StreamDecoder decoder = new(context))
        {
            foreach (byte[] packet in Packets("v0-altref"))
            {
                decoder.Decode(packet);
            }

            // Four frame buffers of three planes each, however long the stream.
            Assert.Equal(12, memory.Live);
        }

        Assert.Equal(0, memory.Live);
    }

    [Fact]
    public void TheFrameSizeIsChecked()
    {
        EgamiContext context = EgamiContext.Default with { Limits = SafetyLimits.Default with { MaxPixels = 1000 } };
        using Vp8StreamDecoder decoder = new(context);
        Assert.Throws<LimitExceededException>(() => decoder.Decode(Packets("v0-pan")[0]));
    }

    private sealed class CountingSource : IPixelMemorySource
    {
        private int _live;

        public int Live => _live;

        public int MaxBlockBytes => int.MaxValue;

        public IMemoryOwner<byte> Allocate(int byteLength, ClearMode clear)
        {
            Interlocked.Increment(ref _live);
            return new Block(this, byteLength);
        }

        private sealed class Block(CountingSource source, int length) : IMemoryOwner<byte>
        {
            private byte[]? _array = new byte[length];

            public Memory<byte> Memory => _array ?? throw new ObjectDisposedException(nameof(Block));

            public void Dispose()
            {
                if (_array is not null)
                {
                    _array = null;
                    Interlocked.Decrement(ref source._live);
                }
            }
        }
    }
}
