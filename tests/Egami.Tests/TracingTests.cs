using System.Collections.Concurrent;
using System.Diagnostics;

using Egami.Formats;
using Egami.Formats.Png;
using Egami.Formats.Tiff;
using Egami.Pixels;

namespace Egami.Tests;

/// <summary>
/// Loads, probes, saves and transcodes start one activity each on <see cref="EgamiTracing.SourceName"/>, tagged with the format,
/// size and bytes, and a failing call marks its activity as an error. Each test runs under a parent activity of its own and keeps
/// only the activities of that trace, so tests running at the same time do not mix.
/// </summary>
public sealed class TracingTests : IDisposable
{
    private const string TestSourceName = "Egami.Tests.Tracing";

    // Matched by name: the listener's first check can run while this field is still being set (creating the source asks every
    // listener), when it would read null.
    private static readonly ActivitySource TestSource = new(TestSourceName);

    private readonly ConcurrentQueue<Activity> _stopped = new();
    private readonly ActivityListener _listener;
    private readonly Activity _parent;

    public TracingTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is EgamiTracing.SourceName or TestSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = Stopped,
        };
        ActivitySource.AddActivityListener(_listener);
        _parent = TestSource.StartActivity("Test")!;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _parent.Dispose();
        _listener.Dispose();
    }

    [Fact]
    public void LoadOfAPathIsOneActivityWithFormatSizeAndBytes()
    {
        byte[] png = Png(40, 30);
        string path = Path.Combine(Path.GetTempPath(), $"egami-tracing-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, png);
        try
        {
            using Raster raster = Raster.Load(path);
        }
        finally
        {
            File.Delete(path);
        }

        Activity load = Assert.Single(Ours());
        Assert.Equal("Egami.Load", load.OperationName);
        Assert.Equal(_parent.SpanId, load.ParentSpanId);
        Assert.Equal("PNG", load.GetTagItem("egami.format"));
        Assert.Equal(40, load.GetTagItem("egami.width"));
        Assert.Equal(30, load.GetTagItem("egami.height"));
        Assert.Equal(1, load.GetTagItem("egami.frames"));
        Assert.Equal((long)png.Length, load.GetTagItem("egami.bytes"));
        Assert.Equal(ActivityStatusCode.Unset, load.Status);
    }

    [Fact]
    public async Task LoadAsyncAndColorConversionStayOneActivity()
    {
        byte[] png = Png(8, 8);
        using Raster a = await Raster.LoadAsync(new MemoryStream(png), cancellationToken: Ct);
        using Raster<Rgba8> b = await Raster.LoadAsync<Rgba8>(new MemoryStream(png), cancellationToken: Ct);
        using Raster c = Raster.Load(png, options: new ReaderOptions { ConvertColorTo = global::Egami.Colors.ColorProfile.Srgb });

        List<Activity> loads = Ours();
        Assert.Equal(3, loads.Count);
        Assert.All(loads, x => Assert.Equal("Egami.Load", x.OperationName));
    }

    [Fact]
    public async Task ProbeTagsTheHeader()
    {
        byte[] png = Png(12, 7);
        _ = Raster.Probe(png);
        await Raster.ProbeAsync(new MemoryStream(png), cancellationToken: Ct);

        Assert.Equal(2, Ours().Count);
        Assert.All(Ours(), probe =>
        {
            Assert.Equal("Egami.Probe", probe.OperationName);
            Assert.Equal("PNG", probe.GetTagItem("egami.format"));
            Assert.Equal(12, probe.GetTagItem("egami.width"));
            Assert.Equal(7, probe.GetTagItem("egami.height"));
        });
    }

    [Fact]
    public async Task SavesTagFormatSizeAndBytesWritten()
    {
        using Raster<Rgba8> raster = Raster.Create<Rgba8>(20, 10);
        using MemoryStream sync = new();
        raster.Save(sync, PngFormat.Info);
        using MemoryStream async = new();
        await raster.SaveAsync(async, PngFormat.Info, cancellationToken: Ct);

        List<Activity> saves = Ours();
        Assert.Equal(2, saves.Count);
        Assert.All(saves, save =>
        {
            Assert.Equal("Egami.Save", save.OperationName);
            Assert.Equal("PNG", save.GetTagItem("egami.format"));
            Assert.Equal(20, save.GetTagItem("egami.width"));
            Assert.Equal(10, save.GetTagItem("egami.height"));
        });
        Assert.Equal(sync.Length, saves[0].GetTagItem("egami.bytes"));
        Assert.Equal(async.Length, saves[1].GetTagItem("egami.bytes"));
    }

    [Fact]
    public void SaveAllAndLoadAllCountTheImages()
    {
        using Raster<Rgba8> a = Raster.Create<Rgba8>(4, 4);
        using Raster<Rgba8> b = Raster.Create<Rgba8>(4, 4);
        using MemoryStream stream = new();
        Raster.SaveAll([a, b], stream, TiffFormat.Info);
        stream.Position = 0;
        using RasterList pages = Raster.LoadAll(stream);

        List<Activity> activities = Ours();
        Activity save = Assert.Single(activities, x => x.OperationName == "Egami.Save");
        Assert.Equal("TIFF", save.GetTagItem("egami.format"));
        Assert.Equal(2, save.GetTagItem("egami.images"));
        Activity loadAll = Assert.Single(activities, x => x.OperationName == "Egami.Load");
        Assert.Equal("TIFF", loadAll.GetTagItem("egami.format"));
        Assert.Equal(2, loadAll.GetTagItem("egami.images"));
    }

    [Fact]
    public void TranscodeIsOneActivity()
    {
        using MemoryStream output = new();
        Raster.Transcode(new MemoryStream(Png(16, 9)), output, PngFormat.Info, ops => ops.Resize(8, 4));

        Activity transcode = Assert.Single(Ours());
        Assert.Equal("Egami.Transcode", transcode.OperationName);
        Assert.Equal("PNG", transcode.GetTagItem("egami.format"));
        Assert.Equal(16, transcode.GetTagItem("egami.width"));
    }

    [Fact]
    public void FailureMarksTheActivity()
    {
        byte[] truncated = Png(8, 8)[..40];
        Exception e = Assert.ThrowsAny<Exception>(() => Raster.Load(truncated));

        Activity load = Assert.Single(Ours());
        Assert.Equal(ActivityStatusCode.Error, load.Status);
        Assert.Equal(e.GetType().FullName, load.GetTagItem("error.type"));
        Assert.Contains(load.Events, e => e.Name == "exception");
    }

    /// <summary>Encodes a test input outside the test's trace, so its save is not one of the activities checked.</summary>
    private static byte[] Png(int width, int height)
    {
        Activity? current = Activity.Current;
        Activity.Current = null;
        try
        {
            using Raster<Rgba8> raster = Raster.Create<Rgba8>(width, height);
            using MemoryStream stream = new();
            raster.Save(stream, PngFormat.Info);
            return stream.ToArray();
        }
        finally
        {
            Activity.Current = current;
        }
    }

    private void Stopped(Activity activity)
    {
        if (activity.Source.Name is EgamiTracing.SourceName)
        {
            _stopped.Enqueue(activity);
        }
    }

    /// <summary>The Egami activities of this test's trace, in the order they stopped.</summary>
    private List<Activity> Ours()
    {
        return [.. _stopped.Where(x => x.TraceId == _parent.TraceId)];
    }
}
