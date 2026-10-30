using System.Diagnostics;

namespace Egami.Formats;

/// <summary>
/// Starts the activities of <see cref="EgamiTracing.SourceName"/>. While nobody listens, <see cref="Start"/> checks one flag and
/// returns an empty span, so the I/O entry points pay nothing.
/// </summary>
internal static class Tracer
{
    public const string Load = "Egami.Load";
    public const string Probe = "Egami.Probe";
    public const string Save = "Egami.Save";
    public const string Transcode = "Egami.Transcode";

    internal static readonly ActivitySource Source = new(EgamiTracing.SourceName, typeof(Tracer).Assembly.GetName().Version?.ToString(3));

    /// <summary>
    /// Starts an activity, or returns an empty span when nobody listens or the same operation already runs on this flow (a
    /// Load of a path that loads its stream, or a load that converts its colors by loading again).
    /// </summary>
    /// <param name="name">The activity's name.</param>
    /// <param name="stream">The stream read or written, whose position counts <c>egami.bytes</c> when it can seek.</param>
    public static TraceSpan Start(string name, Stream? stream = null)
    {
        if (!Source.HasListeners() || (Activity.Current is { } current && current.Source == Source && current.OperationName == name))
        {
            return default;
        }

        return new TraceSpan(Source.StartActivity(name, ActivityKind.Internal), stream);
    }
}

/// <summary>One activity of <see cref="Tracer"/>, or none; every member does nothing on an empty span.</summary>
internal readonly struct TraceSpan : IDisposable
{
    private readonly Activity? _activity;
    private readonly Stream? _stream;
    private readonly long _start;

    public TraceSpan(Activity? activity, Stream? stream)
    {
        _activity = activity;
        if (activity is { IsAllDataRequested: true } && stream is { CanSeek: true })
        {
            _stream = stream;
            _start = stream.Position;
        }
    }

    private Activity? Recorded => _activity is { IsAllDataRequested: true } activity ? activity : null;

    /// <summary>Tags the format, size and frame count of a raster loaded or saved.</summary>
    public void Tag(Raster raster, string? format = null)
    {
        if (Recorded is Activity activity)
        {
            activity.SetTag("egami.format", format ?? raster.Metadata.FormatName);
            activity.SetTag("egami.width", raster.Width);
            activity.SetTag("egami.height", raster.Height);
            activity.SetTag("egami.frames", raster.FrameCount);
        }
    }

    /// <summary>Tags what a header says.</summary>
    public void Tag(RasterInfo info)
    {
        if (Recorded is Activity activity)
        {
            activity.SetTag("egami.format", info.Format.Name);
            activity.SetTag("egami.width", info.Width);
            activity.SetTag("egami.height", info.Height);
            if (info.FrameCount is int frames)
            {
                activity.SetTag("egami.frames", frames);
            }
        }
    }

    /// <summary>Tags the format and how many images a LoadAll or SaveAll handled.</summary>
    public void Tag(string? format, int images)
    {
        if (Recorded is Activity activity)
        {
            activity.SetTag("egami.format", format);
            activity.SetTag("egami.images", images);
        }
    }

    /// <summary>Marks the activity failed; returns <see langword="false"/>, for an exception filter that lets the exception pass.</summary>
    public bool Fail(Exception exception)
    {
        if (_activity is Activity activity)
        {
            activity.SetStatus(ActivityStatusCode.Error, exception.Message);
            if (activity.IsAllDataRequested)
            {
                activity.SetTag("error.type", exception.GetType().FullName);
                activity.AddException(exception);
            }
        }

        return false;
    }

    /// <summary>Tags the bytes read or written and stops the activity.</summary>
    public void Dispose()
    {
        if (_activity is null)
        {
            return;
        }

        // A stream the call disposed (or a file it closed) no longer seeks: its bytes are then not known.
        if (_stream is { CanSeek: true } stream)
        {
            _activity.SetTag("egami.bytes", Math.Abs(stream.Position - _start));
        }

        _activity.Dispose();
    }
}
