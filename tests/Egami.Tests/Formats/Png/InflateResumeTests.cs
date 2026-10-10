using System.IO.Compression;

namespace Egami.Tests.Formats.Png;

/// <summary>
/// Phase 2.2 risk test: incremental PNG needs the BCL inflater to return 0 when its source runs dry and then carry on, with its
/// window and bit state intact, once more bytes arrive. The BCL does not document this, so these tests pin it down.
/// </summary>
public class InflateResumeTests
{
    public static TheoryData<int, CompressionLevel> Cases()
    {
        TheoryData<int, CompressionLevel> data = [];
        foreach (int split in new[] { 1, 7, 4096, 65_536 })
        {
            foreach (CompressionLevel level in new[] { CompressionLevel.NoCompression, CompressionLevel.Fastest, CompressionLevel.SmallestSize })
            {
                data.Add(split, level);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void DeflateResumesAfterSourceRunsDry(int split, CompressionLevel level)
    {
        byte[] original = Sample(300_000);
        byte[] compressed = Compress(original, level, zlib: false);
        GrowingStream source = new(compressed, split);
        using DeflateStream deflate = new(source, CompressionMode.Decompress, leaveOpen: true);
        Assert.Equal(original, ReadAll(deflate, source, out int dry));
        Assert.True(dry > 0 || compressed.Length <= split);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ZLibResumesAfterSourceRunsDry(int split, CompressionLevel level)
    {
        byte[] original = Sample(300_000);
        byte[] compressed = Compress(original, level, zlib: true);
        GrowingStream source = new(compressed, split);
        using ZLibStream zlib = new(source, CompressionMode.Decompress, leaveOpen: true);
        Assert.Equal(original, ReadAll(zlib, source, out int dry));
        Assert.True(dry > 0 || compressed.Length <= split);
    }

    [Fact]
    public void RandomSplitsResume()
    {
        Random random = new(22);
        byte[] original = Sample(200_000);
        byte[] compressed = Compress(original, CompressionLevel.Optimal, zlib: false);
        for (int run = 0; run < 20; run++)
        {
            GrowingStream source = new(compressed, () => random.Next(1, 3000));
            using DeflateStream deflate = new(source, CompressionMode.Decompress, leaveOpen: true);
            Assert.Equal(original, ReadAll(deflate, source, out _));
        }
    }

    [Fact]
    public void RepeatedDryReadsLeaveStateIntact()
    {
        // A caller polling with no new bytes must get 0 each time without the inflater giving up.
        byte[] original = Sample(50_000);
        byte[] compressed = Compress(original, CompressionLevel.Optimal, zlib: false);
        GrowingStream source = new(compressed, 100);
        using DeflateStream deflate = new(source, CompressionMode.Decompress, leaveOpen: true);
        MemoryStream output = new();
        byte[] buffer = new byte[1000];
        while (true)
        {
            int read = deflate.Read(buffer);
            if (read > 0)
            {
                output.Write(buffer, 0, read);
                continue;
            }

            if (source.AtEnd)
            {
                break;
            }

            for (int i = 0; i < 5; i++)
            {
                Assert.Equal(0, deflate.Read(buffer));
            }

            source.Release();
        }

        Assert.Equal(original, output.ToArray());
    }

    [Fact]
    public void ReadReturnsZeroAfterTheFinalBlock()
    {
        // After the final block, Read returns 0 even when the source has bytes left (the zlib trailer, later PNG chunks).
        byte[] original = Sample(10_000);
        byte[] compressed = [.. Compress(original, CompressionLevel.Optimal, zlib: false), 1, 2, 3, 4];
        using DeflateStream deflate = new(new MemoryStream(compressed), CompressionMode.Decompress);
        MemoryStream output = new();
        deflate.CopyTo(output);
        Assert.Equal(original, output.ToArray());
        Assert.Equal(0, deflate.Read(new byte[16]));
    }

    private static byte[] ReadAll(Stream inflater, GrowingStream source, out int dry)
    {
        MemoryStream output = new();
        byte[] buffer = new byte[1000];
        dry = 0;
        while (true)
        {
            int read = inflater.Read(buffer);
            if (read > 0)
            {
                output.Write(buffer, 0, read);
            }
            else if (source.AtEnd)
            {
                return output.ToArray();
            }
            else
            {
                dry++;
                source.Release();
            }
        }
    }

    /// <summary>Compressible but not trivial data: runs, repeats at long distances and noise, so the window matters.</summary>
    private static byte[] Sample(int length)
    {
        Random random = new(5);
        byte[] data = new byte[length];
        for (int i = 0; i < length; i++)
        {
            data[i] = (i % 1000) switch
            {
                < 300 => (byte)(i / 1000),
                < 600 => i >= 20_000 ? data[i - 20_000] : (byte)random.Next(256),
                _ => (byte)random.Next(16),
            };
        }

        return data;
    }

    private static byte[] Compress(byte[] data, CompressionLevel level, bool zlib)
    {
        MemoryStream output = new();
        using (Stream compressor = zlib ? new ZLibStream(output, level, leaveOpen: true) : new DeflateStream(output, level, leaveOpen: true))
        {
            compressor.Write(data);
        }

        return output.ToArray();
    }

    /// <summary>A source that hands out only the bytes released so far and returns 0 when it runs dry, like a download in progress.</summary>
    private sealed class GrowingStream(byte[] data, Func<int> nextChunk) : Stream
    {
        private int available = Math.Min(data.Length, nextChunk());
        private int position;

        public GrowingStream(byte[] data, int chunk)
            : this(data, () => chunk)
        {
        }

        public bool AtEnd => available == data.Length && position == data.Length;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void Release() => available = Math.Min(data.Length, available + nextChunk());

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int count = Math.Min(buffer.Length, available - position);
            data.AsSpan(position, count).CopyTo(buffer);
            position += count;
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
