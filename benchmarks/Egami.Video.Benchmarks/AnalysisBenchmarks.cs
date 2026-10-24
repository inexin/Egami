using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;

using Egami.Video.Analysis;

namespace Egami.Video.Benchmarks;

/// <summary>
/// The V1.4 speed gate (Design Q14, Build Plan task 21): scenes, black and freeze together add at most 10 % to 1080p one-thread
/// decode time. The clip is 250 frames of 1080p Motion JPEG (the cheapest decode Egami.Video has, so the strictest test);
/// <see cref="Decode"/> reads every frame and <see cref="Analyze"/> runs <see cref="MediaAnalysis.Run"/> over the same file.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Monitoring, warmupCount: 2, iterationCount: 10)]
public class AnalysisBenchmarks
{
    private readonly MediaContext _context = MediaContext.Default with { Egami = EgamiContext.Default with { MaxParallelism = 1 } };
    private string _path = "";

    [GlobalSetup]
    public void Setup() => _path = Clips.MotionJpeg(1920, 1080, ".mkv", 250);

    [Benchmark(Baseline = true, Description = "Decode 1080p, one thread")]
    public long Decode()
    {
        long frames = 0;
        using VideoReader reader = VideoReader.Open(_path, null, _context);
        while (reader.ReadFrame() is { } frame)
        {
            frame.Dispose();
            frames++;
        }

        return frames;
    }

    [Benchmark(Description = "Decode and analyze: scenes, black, freeze")]
    public long Analyze()
    {
        using FreezeDetector freeze = new();
        MediaAnalysisOptions options = new() { VideoDetectors = [new BlackDetector(), freeze] };
        return MediaAnalysis.Run(_path, options, _context).VideoFrames;
    }
}

/// <summary>Each video detector's cost per 1080p 8-bit 4:2:0 frame, on decoded frames held in memory (no decode in the time).</summary>
[MemoryDiagnoser]
public class VideoDetectorBenchmarks
{
    private const int Frames = 30;
    private readonly List<YuvVideoFrame> _frames = [];

    [Params("scenes", "black", "freeze", "crop", "interlace", "duplicates", "signal")]
    public string Detector { get; set; } = "scenes";

    [GlobalSetup]
    public void Setup()
    {
        // A moving pattern, with every fifth frame repeated so freeze and duplicates see both kinds.
        TestVideoFormat format = new() { Width = 1920, Height = 1080, FrameRate = new Rational(25, 1) };
        foreach (YuvVideoFrame frame in TestSources.Pattern(format, new MediaTime(Frames, new Rational(1, 25))))
        {
            _frames.Add(frame);
        }
    }

    private IVideoDetector Create() => Detector switch
    {
        "scenes" => new SceneDetector(),
        "black" => new BlackDetector(),
        "freeze" => new FreezeDetector(),
        "crop" => new CropDetector(),
        "interlace" => new InterlaceDetector(),
        "duplicates" => new DuplicateDetector(),
        _ => new SignalMeter(),
    };

    /// <summary>A new detector takes the 30 frames, so the time includes creating it once per 30 frames.</summary>
    [Benchmark(OperationsPerInvoke = Frames, Description = "Add one 1080p frame")]
    public void Add()
    {
        IVideoDetector detector = Create();
        foreach (YuvVideoFrame frame in _frames)
        {
            detector.Add(frame);
        }

        detector.Complete(_frames[^1].Timestamp);
        (detector as IDisposable)?.Dispose();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (YuvVideoFrame frame in _frames)
        {
            frame.Dispose();
        }
    }
}
