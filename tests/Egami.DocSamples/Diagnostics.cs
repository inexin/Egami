using System.Collections.Concurrent;
using System.Diagnostics;

using Egami.Formats;
using Egami.Formats.Png;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Diagnostics
{
    [Fact]
    public void Logger()
    {
        using SampleFiles files = new();
        FakeLogger logger = new();
        Action startup = () =>
        {
            #region logger
            // With Microsoft.Extensions.Logging: one line at startup sends every call's warnings to the app's log, with the id as
            // a property to filter and count on. Egami itself has no logging dependency.
            EgamiContext.SetDefault(new EgamiContext
            {
                Diagnostics = d => logger.Log(d.Level.ToLogLevel(), "{Source} {Id} at {Offset}: {Message}", d.Source, d.Id, d.Offset, d.Message),
            });
            #endregion
        };

        using Raster image = Raster.Load(SampleFiles.Damaged(), logger.Context);
        Assert.Contains(logger.Lines, line => line.Contains("Png.ChecksumMismatch", StringComparison.Ordinal));
    }

    [Fact]
    public void Serilog()
    {
        using SampleFiles files = new();
        FakeSerilog log = new();

        #region serilog
        // With Serilog's static Log: the same one line, with Serilog's level names.
        EgamiContext context = EgamiContext.Default with
        {
            Diagnostics = d => log.Write(d.Level == DiagnosticLevel.Warning ? "Warning" : d.Level == DiagnosticLevel.Info ? "Information" : "Debug", "{Id}: {Message}", d.Id, d.Message),
        };
        #endregion

        using Raster image = Raster.Load(SampleFiles.Damaged(), context);
        Assert.Single(log.Lines);
    }

    [Fact]
    public void PerFile()
    {
        using SampleFiles files = new();
        File.WriteAllBytes("damaged.png", SampleFiles.Damaged());
        List<string> log = [];

        #region per-file
        // Per file: the handler's closure carries the path, so no correlation id is needed. Contexts are cheap records.
        foreach (string path in Directory.EnumerateFiles(".", "*.png"))
        {
            EgamiContext perFile = EgamiContext.Default with { Diagnostics = d => log.Add($"{path}: {d.Message}") };
            using Raster image = Raster.Load(path, perFile);
        }
        #endregion

        Assert.Contains(log, line => line.Contains("damaged.png", StringComparison.Ordinal));
    }

    [Fact]
    public void WarningsAsErrors()
    {
        using SampleFiles files = new();

        #region errors
        // Warnings as errors: a handler that throws fails the call with its exception, and nothing the call allocated is leaked.
        EgamiContext strict = EgamiContext.Default with
        {
            Diagnostics = d => throw new InvalidDataException($"{d.Id}: {d.Message}"),
        };

        try
        {
            using Raster image = Raster.Load(SampleFiles.Damaged(), strict);
        }
        catch (InvalidDataException e)
        {
            Console.WriteLine($"Rejected: {e.Message}");
        }
        #endregion

        Assert.Throws<InvalidDataException>(() => Raster.Load(SampleFiles.Damaged(), strict));
    }

    [Fact]
    public void Level()
    {
        using SampleFiles files = new();
        List<EgamiDiagnostic> seen = [];

        #region level
        // Info and Debug events are off unless asked for; below the level they are never even formatted.
        EgamiContext verbose = EgamiContext.Default with
        {
            Diagnostics = d => seen.Add(d),
            DiagnosticLevel = DiagnosticLevel.Debug,
        };

        using Raster image = Raster.Load("duck.jpg", verbose, new ReaderOptions { MinimumSize = new Size(100, 100) });
        foreach (EgamiDiagnostic d in seen)
        {
            Console.WriteLine($"{d.Level,-7} {d.Id}: {d.Message}");
        }
        #endregion

        Assert.All(seen, d => Assert.NotEqual(DiagnosticLevel.Warning, d.Level));
    }

    [Fact]
    public void Warnings()
    {
        using SampleFiles files = new();

        #region warnings
        // Without a handler the same Warnings are in the metadata, each with its id.
        using Raster image = Raster.Load(SampleFiles.Damaged());
        foreach (EgamiDiagnostic warning in image.Metadata.Warnings)
        {
            Console.WriteLine($"{warning.Id} at byte {warning.Offset}: {warning.Message}");
        }

        bool checksumsOnly = image.Metadata.Warnings.All(w => w.Id == "Png.ChecksumMismatch");
        #endregion

        Assert.True(checksumsOnly);
    }

    [Fact]
    public void Plugin()
    {
        List<EgamiDiagnostic> seen = [];
        EgamiContext context = EgamiContext.Default with { Diagnostics = seen.Add };

        #region plugin
        // A decoder outside Egami (a video demuxer, a format plug-in's own entry point) reports through the same handler, with the
        // same level filter and the same cap of 10 per id per call. Dispose the reporter when the call ends.
        using (DiagnosticReporter diagnostics = context.StartDiagnostics())
        {
            if (diagnostics.IsEnabled(DiagnosticLevel.Warning))
            {
                diagnostics.Report(DiagnosticLevel.Warning, "Avi.IndexRebuilt", "The index was missing; it was rebuilt from the frames.", offset: 4096);
            }

            diagnostics.Report(DiagnosticLevel.Info, "Mjpeg.DhtInserted", "Frames have no Huffman tables; the standard ones were used.");
        }
        #endregion

        Assert.Equal("Avi.IndexRebuilt", Assert.Single(seen).Id);
    }

    [Fact]
    public void Tracing()
    {
        ConcurrentQueue<string> spans = new();

        #region tracing
        // With OpenTelemetry, one line at startup: builder.Services.AddOpenTelemetry().WithTracing(t => t.AddSource(EgamiTracing.SourceName)).
        // Without it, any ActivityListener hears the same activities. With neither, Egami starts none.
        using ActivityListener listener = new()
        {
            ShouldListenTo = source => source.Name == EgamiTracing.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => spans.Enqueue($"{activity.OperationName} {activity.GetTagItem("egami.format")} {activity.Duration.TotalMilliseconds:F1} ms"),
        };
        ActivitySource.AddActivityListener(listener);
        #endregion

        using Raster image = Raster.Load(SampleFiles.Damaged());
        Assert.Contains(spans, span => span.StartsWith("Egami.Load PNG", StringComparison.Ordinal));
    }

    // Stand-ins with the shape of Microsoft.Extensions.Logging and Serilog, which the samples do not reference.
    private sealed class FakeLogger
    {
        public List<string> Lines { get; } = [];

        public EgamiContext Context => EgamiContext.Default with
        {
            Diagnostics = d => Log(d.Level.ToLogLevel(), "{Source} {Id} at {Offset}: {Message}", d.Source, d.Id, d.Offset, d.Message),
        };

        public void Log(LogLevel level, string template, params object?[] args) => Lines.Add($"{level} {string.Join(' ', args)}");
    }

    private sealed class FakeSerilog
    {
        public List<string> Lines { get; } = [];

        public void Write(string level, string template, params object?[] args) => Lines.Add($"{level} {string.Join(' ', args)}");
    }
}

/// <summary>The levels of Microsoft.Extensions.Logging, for the sample.</summary>
public enum LogLevel
{
    Trace,
    Debug,
    Information,
    Warning,
    Error,
    Critical,
    None,
}

/// <summary>The one mapping an app writes.</summary>
public static class DiagnosticLevelExtensions
{
    public static LogLevel ToLogLevel(this DiagnosticLevel level) => level switch
    {
        DiagnosticLevel.Warning => LogLevel.Warning,
        DiagnosticLevel.Info => LogLevel.Information,
        _ => LogLevel.Debug,
    };
}
