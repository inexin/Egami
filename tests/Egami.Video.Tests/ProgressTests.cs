using System.Diagnostics;

using Egami.Video.Audio.Codecs;
using Egami.Video.Containers;
using Egami.Video.Tests.Containers;

namespace Egami.Video.Tests;

/// <summary>
/// Progress reports, cancellation and the stream overloads of <see cref="MediaFile.Remux(string, string, Func{TrackInfo, bool}?, MediaContext?, IProgress{MediaProgress}?, CancellationToken)"/> and Transcode.
/// Runs with no other test beside it: <see cref="NoHandlerAllocatesNothingPerPacket"/> counts the bytes the test thread
/// allocates to within 8 a packet, which tests running alongside can push over.
/// </summary>
[Collection(nameof(MemoryTests))]
public class ProgressTests
{
    private static readonly string[] MediaExtensions = [".avi", ".mkv", ".webm", ".mka", ".ivf", ".y4m", ".mp4", ".mov", ".m4a", ".obu", ".flac", ".wav", ".w64", ".ogg", ".opus"];

    [Fact]
    public void ReportsFirstThenAtMostEvery250MsThenFinal()
    {
        ManualClock clock = new();
        Reports reports = new();
        VideoTrackInfo video = Video(1);
        ProgressReporter reporter = new(reports, MediaTime.FromSeconds(10), [video], null, 0, null, clock);
        reporter.Start();
        for (int i = 0; i < 20; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            reporter.Written(new EncodedPacket(1, new byte[1], new MediaTime(i, new Rational(1, 2))));
        }

        reporter.Finish();

        // 2 s of packets, 100 ms apart: the first report, then one at 300, 600, ... 1800 ms (every third packet), then the final.
        Assert.Equal(1 + 6 + 1, reports.Items.Count);
        Assert.Equal(TimeSpan.Zero, reports.Items[0].Elapsed);
        Assert.Equal(0, reports.Items[0].Packets);
        Assert.Equal([300, 600, 900, 1200, 1500, 1800], reports.Items.Skip(1).SkipLast(1).Select(r => (int)r.Elapsed.TotalMilliseconds));
        MediaProgress last = reports.Items[^1];
        Assert.True(last.IsFinal);
        Assert.Equal(1, last.Fraction);
        Assert.Equal(20, last.Packets);
        Assert.Equal(20, last.Frames);
        Assert.Equal(9.5, last.Position.Seconds);
        Assert.Single(reports.Items, r => r.IsFinal);
    }

    [Fact]
    public void FractionComesFromTimeThenBytesThenNothing()
    {
        ManualClock clock = new();
        VideoTrackInfo video = Video(1);

        Reports timed = new();
        ProgressReporter reporter = new(timed, MediaTime.FromSeconds(4), [video], null, 0, null, clock);
        reporter.Start();
        clock.Advance(TimeSpan.FromSeconds(1));
        reporter.Written(new EncodedPacket(1, new byte[1], MediaTime.FromSeconds(1)));
        Assert.Equal(0.25, timed.Items[^1].Fraction);
        Assert.Equal(1.0, timed.Items[^1].Speed);

        using JobStream input = new(new MemoryStream(new byte[1000]));
        input.ReadExactly(new byte[100]);
        Reports counted = new();
        reporter = new ProgressReporter(counted, null, [video], input, 1000, null, clock);
        reporter.Start();
        Assert.Equal(0.1, counted.Items[^1].Fraction);
        Assert.Equal(100, counted.Items[^1].BytesRead);

        Reports unknown = new();
        reporter = new ProgressReporter(unknown, null, [video], input, 0, null, clock);
        reporter.Start();
        Assert.Null(unknown.Items[^1].Fraction);
        Assert.Null(unknown.Items[^1].Speed);
        reporter.Finish();
        Assert.Equal(1, unknown.Items[^1].Fraction);
    }

    [Fact]
    public void FractionAndPositionNeverGoBackwards()
    {
        ManualClock clock = new();
        Reports reports = new();
        AudioTrackInfo audio = new() { Number = 2, Kind = TrackKind.Audio, CodecId = CodecIds.PcmLittleEndian, TimeBase = new Rational(1, 1000), SampleRate = 48000, Channels = 1, BitsPerSample = 16 };
        ProgressReporter reporter = new(reports, MediaTime.FromSeconds(10), [Video(1), audio], null, 0, null, clock);
        reporter.Start();
        int[] seconds = [2, 5, 3, 1, 6, 4];
        foreach (int s in seconds)
        {
            clock.Advance(TimeSpan.FromMilliseconds(300));
            reporter.Written(new EncodedPacket(s % 2 + 1, new byte[1], MediaTime.FromSeconds(s)));
        }

        Assert.Equal([0, 2, 5, 5, 5, 6, 6], reports.Items.Select(r => r.Position.Seconds));
        Assert.Equal(3, reports.Items[^1].Frames);
    }

    [Fact]
    public void NoHandlerAllocatesNothingPerPacket()
    {
        string input = LongClip(600);
        string output = AviWriterTests.TempFile($"alloc-{Guid.NewGuid():N}.mkv");
        try
        {
            // Without a token: the plain path, as callers who pass neither run it.
#pragma warning disable xUnit1051
            MediaFile.Remux(input, output);
#pragma warning restore xUnit1051
            long small = Allocated(() => MediaFile.Remux(LongClip(100), output));
            long large = Allocated(() => MediaFile.Remux(input, output));
            long manual = Allocated(() => ManualRemux(input, output));
            long withHandler = Allocated(() => MediaFile.Remux(input, output, progress: new Reports(), cancellationToken: new CancellationTokenSource().Token));

            // Six times the packets cost what the packets themselves cost, as in a hand-written demux-mux loop.
            long perPacket = (large - small) / 500;
            long manualSmall = Allocated(() => ManualRemux(LongClip(100), output));
            Assert.True(perPacket <= ((manual - manualSmall) / 500) + 8, $"{perPacket} bytes a packet, a plain loop {(manual - manualSmall) / 500}");
            Assert.True(large <= manual + 2048, $"Remux allocated {large} bytes, a plain loop {manual}");

            // A handler adds the reporter, its wrappers and a few reports, not something per packet.
            Assert.True(withHandler - large < 8192, $"a handler added {withHandler - large} bytes");
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public void CancelMidFileStopsQuicklyAndDeletesTheOutput()
    {
        string input = LongClip(3000);
        string output = AviWriterTests.TempFile($"cancel-{Guid.NewGuid():N}.mp4");
        using CancellationTokenSource cts = new();
        long cancelledAt = 0;
        using FileStream file = File.OpenRead(input);
        TriggerStream trigger = new(file, file.Length / 2, () =>
        {
            cancelledAt = Stopwatch.GetTimestamp();
            cts.Cancel();
        });

        Assert.Throws<OperationCanceledException>(() => MediaFile.Remux(trigger, File.Create(output), ".mp4", cancellationToken: cts.Token));
        TimeSpan stopped = Stopwatch.GetElapsedTime(cancelledAt);
        Assert.True(stopped < TimeSpan.FromMilliseconds(100), $"stopped {stopped.TotalMilliseconds} ms after the cancel");

        // The same through the path overload: the file is gone.
        using CancellationTokenSource again = new();
        int reports = 0;
        Assert.Throws<OperationCanceledException>(() => MediaFile.Remux(input, output, progress: new Reports(_ =>
        {
            if (++reports == 1)
            {
                again.Cancel();
            }
        }), cancellationToken: again.Token));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void ACancelBeforeTheStartLeavesAnExistingOutputAlone()
    {
        string output = AviWriterTests.TempFile($"existing-{Guid.NewGuid():N}.mkv");
        File.WriteAllBytes(output, [1, 2, 3]);
        try
        {
            Assert.Throws<OperationCanceledException>(() => MediaFile.Remux(Corpus.PathOf("mkv-mjpeg-pcm.mkv"), output, cancellationToken: new CancellationToken(canceled: true)));
            Assert.Equal([1, 2, 3], File.ReadAllBytes(output));
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public void AHandlerThatThrowsStopsTheJob()
    {
        string output = AviWriterTests.TempFile($"throws-{Guid.NewGuid():N}.mkv");
        try
        {
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => MediaFile.Remux(Corpus.PathOf("mkv-mjpeg-pcm.mkv"), output, progress: new Reports(_ => throw new InvalidOperationException("handler")), cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal("handler", error.Message);
        }
        finally
        {
            File.Delete(output);
        }
    }

    public static TheoryData<string> CorpusFiles()
    {
        TheoryData<string> data = [];
        foreach (string file in Directory.EnumerateFiles(Corpus.Directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (MediaExtensions.Contains(Path.GetExtension(file)))
            {
                data.Add(Path.GetRelativePath(Corpus.Directory, file));
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void ReportsHoldTheirPromisesOnTheCorpus(string name)
    {
        string input = Corpus.PathOf(name);
        string? output = null;
        Reports reports = new();
        long packets = -1;
        foreach (string extension in new[] { Path.GetExtension(name), ".mkv" }.Distinct())
        {
            output = AviWriterTests.TempFile($"corpus-{Guid.NewGuid():N}{extension}");
            reports = new Reports();
            try
            {
                packets = MediaFile.Remux(input, output, progress: reports, cancellationToken: TestContext.Current.CancellationToken);
                break;
            }
            catch (Exception e) when (e is NotSupportedException or UnsupportedFormatException or CorruptDataException)
            {
                File.Delete(output);
            }
        }

        if (packets < 0)
        {
            return;
        }

        try
        {
            Assert.True(reports.Items.Count >= 2);
            Assert.Equal(0, reports.Items[0].Packets);
            MediaProgress final = reports.Items[^1];
            Assert.True(final.IsFinal);
            Assert.Equal(1, final.Fraction);
            Assert.Equal(packets, final.Packets);
            Assert.Equal(new FileInfo(output!).Length, final.BytesWritten);
            Assert.Single(reports.Items, r => r.IsFinal);
            for (int i = 1; i < reports.Items.Count; i++)
            {
                Assert.True(reports.Items[i].Position.Seconds >= reports.Items[i - 1].Position.Seconds);
                Assert.True((reports.Items[i].Fraction ?? 0) >= (reports.Items[i - 1].Fraction ?? 0));
                Assert.True(reports.Items[i].Packets >= reports.Items[i - 1].Packets);
            }

            // At most five a second: the first and the final, plus one per 250 ms.
            Assert.True(reports.Items.Count - 2 <= (int)(final.Elapsed.TotalMilliseconds / 250) + 1, $"{reports.Items.Count} reports in {final.Elapsed}");
        }
        finally
        {
            File.Delete(output!);
        }
    }

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void CancelAtARandomPointLeavesNoFile(string name)
    {
        string input = Corpus.PathOf(name);
        string extension = Path.GetExtension(name) is ".avi" or ".mkv" or ".webm" or ".ivf" or ".y4m" or ".mp4" ? Path.GetExtension(name) : ".mkv";
        string output = AviWriterTests.TempFile($"random-{Guid.NewGuid():N}{extension}");
        long length = new FileInfo(input).Length;
        Random random = new(name.GetHashCode(StringComparison.Ordinal));
        using CancellationTokenSource cts = new();
        using FileStream file = File.OpenRead(input);
        TriggerStream trigger = new(file, random.NextInt64(1, length), cts.Cancel);
        try
        {
            MediaFile.Remux(trigger, File.Create(output), extension, cancellationToken: cts.Token);

            // The cancel came after the last packet was read, or the input cannot go into the container: the job finished.
        }
        catch (OperationCanceledException)
        {
            Assert.True(cts.IsCancellationRequested);
        }
        catch (Exception e) when (e is NotSupportedException or UnsupportedFormatException or CorruptDataException)
        {
        }
        finally
        {
            File.Delete(output);
        }

        // The path overload deletes its file.
        using CancellationTokenSource first = new();
        int count = 0;
        try
        {
            MediaFile.Remux(input, output, progress: new Reports(_ =>
            {
                if (++count == 1)
                {
                    first.Cancel();
                }
            }), cancellationToken: first.Token);
            Assert.Fail("The job finished after a cancel on its first report.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (e is NotSupportedException or UnsupportedFormatException or CorruptDataException)
        {
        }

        Assert.False(File.Exists(output));
    }

    [Theory]
    [InlineData("mkv-mjpeg-pcm.mkv", ".mkv")]
    [InlineData("webm-vp8-opus.webm", ".webm")]
    [InlineData("vp8/v0-altref.ivf", ".ivf")]
    [InlineData("y4m-422.y4m", ".y4m")]
    [InlineData("mkv-mjpeg-pcm.mkv", ".mp4")]
    public void StreamOverloadsMatchThePathOverloadsOnNonSeekableStreams(string name, string extension)
    {
        string input = Corpus.PathOf(name);
        string expected = AviWriterTests.TempFile($"path-{Guid.NewGuid():N}{extension}");
        try
        {
            long count = MediaFile.Remux(input, expected, cancellationToken: TestContext.Current.CancellationToken);
            using MemoryStream written = new();
            using NonSeekableStream reader = new(File.OpenRead(input));
            using NonSeekableStream writer = new(written);
            Reports reports = new();
            Assert.Equal(count, MediaFile.Remux(reader, writer, extension, leaveOpen: true, progress: reports, cancellationToken: TestContext.Current.CancellationToken));
            Assert.False(reader.Disposed);
            Assert.False(writer.Disposed);
            Assert.Equal(written.Length, reports.Items[^1].BytesWritten);
            // A forward-only stream gets fragmented MP4, which keeps each fragment's tracks apart, so compare track by track.
            Assert.Equal(PacketsOf(File.ReadAllBytes(expected)).OrderBy(p => p.Item1), PacketsOf(written.ToArray()).OrderBy(p => p.Item1));

            // Without leaveOpen the job disposes both streams.
            using NonSeekableStream ownedReader = new(File.OpenRead(input));
            NonSeekableStream ownedWriter = new(new MemoryStream());
            MediaFile.Remux(ownedReader, ownedWriter, extension, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(ownedReader.Disposed);
            Assert.True(ownedWriter.Disposed);
        }
        finally
        {
            File.Delete(expected);
        }
    }

    [Fact]
    public void TranscodeStreamOverloadMatchesThePathOverload()
    {
        string input = Corpus.PathOf("mkv-mjpeg-pcm.mkv");
        string expected = AviWriterTests.TempFile($"transcode-{Guid.NewGuid():N}.mkv");
        try
        {
            long count = MediaFile.Transcode(input, expected, audio: FlacCodec.Instance, cancellationToken: TestContext.Current.CancellationToken);
            using MemoryStream written = new();
            Reports reports = new();
            Assert.Equal(count, MediaFile.Transcode(new NonSeekableStream(File.OpenRead(input)), new NonSeekableStream(written), "mkv", audio: FlacCodec.Instance, leaveOpen: true, progress: reports, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(count, reports.Items[^1].Packets);
            Assert.Equal(PacketsOf(File.ReadAllBytes(expected)), PacketsOf(written.ToArray()));
        }
        finally
        {
            File.Delete(expected);
        }
    }

    [Fact]
    public void AviRefusesANonSeekableOutput()
    {
        using NonSeekableStream output = new(new MemoryStream());
        NotSupportedException error = Assert.Throws<NotSupportedException>(() => MediaFile.Remux(File.OpenRead(Corpus.PathOf("avi-mjpeg-420-pcm.avi")), output, ".avi", leaveOpen: false, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("seekable", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACancelledStreamJobLeavesTheCallersStream()
    {
        string input = LongClip(3000);
        using MemoryStream written = new();
        using CancellationTokenSource cts = new();
        int count = 0;
        Assert.Throws<OperationCanceledException>(() => MediaFile.Remux(File.OpenRead(input), written, ".mkv", leaveOpen: true, progress: new Reports(_ =>
        {
            if (++count == 1)
            {
                cts.Cancel();
            }
        }), cancellationToken: cts.Token));
        Assert.True(written.CanWrite);
        written.WriteByte(0);
    }

    private static VideoTrackInfo Video(int number) => new() { Number = number, Kind = TrackKind.Video, CodecId = CodecIds.MotionJpeg, TimeBase = new Rational(1, 2), Width = 16, Height = 16 };

    private static List<(int Track, long Ticks, string Data)> PacketsOf(byte[] file)
    {
        List<(int, long, string)> packets = [];
        using IDemuxer demuxer = MediaFile.OpenDemuxer(new MemoryStream(file));
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                packets.Add((packet.TrackNumber, packet.Timestamp.Rescale(Rational.Nanosecond).Ticks, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(packet.Data.Span))));
            }
        }

        return packets;
    }

    private static long Allocated(Action action)
    {
        action();
        long before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static long ManualRemux(string input, string output)
    {
        using IDemuxer demuxer = MediaFile.OpenDemuxer(input);
        using IMuxer muxer = MediaFile.CreateMuxer(output, demuxer.Tracks, metadata: demuxer.Info.Metadata);
        long count = 0;
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                muxer.WritePacket(packet);
                count++;
            }
        }

        muxer.Finish();
        return count;
    }

    /// <summary>A Motion JPEG clip of <paramref name="frames"/> frames at 25 fps, made from the corpus clip's frames.</summary>
    private static string LongClip(int frames)
    {
        string path = AviWriterTests.TempFile($"progress-{frames}.mkv");
        lock (MediaExtensions)
        {
            if (File.Exists(path))
            {
                return path;
            }

            List<byte[]> packets = [.. Codecs.MotionJpegDecoderTests.Packets("avi-mjpeg-420-pcm.avi")];
            VideoTrackInfo track = new() { Number = 1, Kind = TrackKind.Video, CodecId = CodecIds.MotionJpeg, TimeBase = new Rational(1, 25), Width = 160, Height = 120, FrameRate = new Rational(25, 1) };
            string partial = path + ".partial";
            using (IMuxer muxer = MediaFile.CreateMuxer(partial + ".mkv", [track]))
            {
                for (int i = 0; i < frames; i++)
                {
                    using EncodedPacket packet = new(1, packets[i % packets.Count], new MediaTime(i, new Rational(1, 25))) { IsKeyframe = true };
                    muxer.WritePacket(packet);
                }
            }

            File.Move(partial + ".mkv", path, overwrite: true);
            return path;
        }
    }

    /// <summary>Collects reports synchronously, unlike <see cref="Progress{T}"/>, which posts them to the thread pool.</summary>
    private sealed class Reports(Action<MediaProgress>? then = null) : IProgress<MediaProgress>
    {
        public List<MediaProgress> Items { get; } = [];

        public void Report(MediaProgress value)
        {
            Items.Add(value);
            then?.Invoke(value);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan time) => _ticks += time.Ticks;
    }

    /// <summary>A forward-only view of a stream that runs an action once a given number of bytes has been read.</summary>
    private sealed class TriggerStream(Stream inner, long after, Action trigger) : NonSeekableStream(inner)
    {
        private long _read;

        public override int Read(Span<byte> buffer)
        {
            int read = base.Read(buffer);
            long before = _read;
            _read += read;
            if (before < after && _read >= after)
            {
                trigger();
            }

            return read;
        }
    }

    private class NonSeekableStream(Stream inner) : Stream
    {
        public bool Disposed { get; private set; }

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => inner.CanWrite;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            if (disposing && inner is not MemoryStream)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
