using System.Globalization;
using System.Text;

namespace Egami.Video.Cli.Host;

/// <summary>
/// Where long jobs report how far they have come (V1.3 Progress design Q8): a live line on standard error when it is a terminal
/// (time, percent, frames, fps, size, speed and time left), and with <c>--progress</c> a file or standard output that gets
/// ffmpeg's <c>-progress</c> key=value blocks, or one JSON object per report with <c>--progress-format json</c>; under
/// <c>egami-video mcp</c>, the client's progress notifications.
/// </summary>
internal sealed class ProgressSink : IDisposable
{
    private readonly Lock _lock = new();
    private readonly TextWriter? _live;
    private readonly TextWriter? _report;
    private readonly bool _json;
    private readonly bool _ownsReport;
    private readonly Action<double, double, string>? _note;
    private int _lastLineLength;
    private long _filesDone;

    private ProgressSink(TextWriter? live, TextWriter? report, bool json, bool ownsReport, Action<double, double, string>? note = null)
    {
        _live = live;
        _report = report;
        _json = json;
        _ownsReport = ownsReport;
        _note = note;
    }

    /// <summary>
    /// The sink the options ask for, or <see langword="null"/> when nothing reports: no <c>--progress</c>, and standard error is
    /// not a terminal or <c>--quiet</c> is on.
    /// </summary>
    public static ProgressSink? Create(VideoSession session, string? target, string? format)
    {
        bool json = format?.ToLowerInvariant() switch
        {
            null or "keys" or "ffmpeg" => false,
            "json" => true,
            _ => throw new UsageException($"--progress-format '{format}' is not keys or json."),
        };
        if (format is not null && target is null)
        {
            throw new UsageException("--progress-format needs --progress <file|->.");
        }

        TextWriter? report = null;
        bool owns = false;
        if (target == "-")
        {
            report = session.Console.Text;
        }
        else if (target is not null)
        {
            string path = session.ResolvePath(target);
            report = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            owns = true;
        }

        bool terminal = ReferenceEquals(session.Console, CliConsole.System) && !Console.IsErrorRedirected && session.Verbosity != Verbosity.Quiet;
        TextWriter? live = terminal ? session.Console.Error : null;
        Action<double, double, string>? note = session.ProgressNote;
        return live is null && report is null && note is null ? null : new ProgressSink(live, report, json, owns, note);
    }

    /// <summary>A sink for tests and the MCP server: reports go to <paramref name="report"/> only.</summary>
    public static ProgressSink ToWriter(TextWriter report, bool json) => new(null, report, json, false);

    /// <summary>
    /// The handler for one input's job; <paramref name="index"/> and <paramref name="count"/> place it in a batch, whose live
    /// line names the file and whose report blocks carry <c>file=</c> and <c>files=</c>.
    /// </summary>
    public IProgress<MediaProgress> For(InputFile input, int index, int count) => new FileProgress(this, input, index, count);

    /// <summary>Ends the live line, so the next message starts on a line of its own.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_live is not null && _lastLineLength > 0)
            {
                _live.Write('\n');
                _lastLineLength = 0;
            }

            if (_ownsReport)
            {
                _report?.Dispose();
            }
        }
    }

    private void Report(InputFile input, int index, int count, MediaProgress progress)
    {
        lock (_lock)
        {
            if (progress.IsFinal)
            {
                _filesDone++;
            }

            if (_live is not null)
            {
                string line = LiveLine(input, index, count, progress);
                _live.Write('\r' + line + new string(' ', Math.Max(0, _lastLineLength - line.Length)));
                _lastLineLength = line.Length;
                if (progress.IsFinal && count > 1)
                {
                    _live.Write('\n');
                    _lastLineLength = 0;
                }
            }

            if (_report is not null)
            {
                _report.Write(_json ? Json(input, index, count, progress) : Keys(input, index, count, progress));
                _report.Flush();
            }

            // The MCP server's notifications: work done in files, so 1.5 of 3 is halfway through the second.
            _note?.Invoke(index + (progress.Fraction ?? 0), count, LiveLine(input, index, count, progress));
        }
    }

    /// <summary>The live line: <c>[2/5] clip.mkv time=0:00:12.500 42% frame=300 fps=240 size=3.2 MiB speed=9.6x eta=0:00:17</c>.</summary>
    internal static string LiveLine(InputFile input, int index, int count, MediaProgress progress)
    {
        StringBuilder line = new();
        if (count > 1)
        {
            line.Append(CultureInfo.InvariantCulture, $"[{index + 1}/{count}] {input.Display} ");
        }

        line.Append("time=").Append(TimeValue.Format(progress.Position));
        if (progress.Fraction is { } fraction)
        {
            line.Append(CultureInfo.InvariantCulture, $" {fraction * 100:0}%");
        }

        line.Append(CultureInfo.InvariantCulture, $" frame={progress.Frames}");
        if (Fps(progress) is { } fps)
        {
            line.Append(CultureInfo.InvariantCulture, $" fps={fps:0.#}");
        }

        line.Append(" size=").Append(Size(progress.BytesWritten));
        if (progress.Speed is { } speed)
        {
            line.Append(CultureInfo.InvariantCulture, $" speed={speed:0.##}x");
        }

        if (!progress.IsFinal && progress.Fraction is > 0 and < 1 && progress.Elapsed > TimeSpan.Zero)
        {
            double left = progress.Elapsed.TotalSeconds * (1 - progress.Fraction.Value) / progress.Fraction.Value;
            line.Append(" eta=").Append(TimeValue.Format(left)[..^4]);
        }

        return line.ToString();
    }

    /// <summary>One block in ffmpeg's <c>-progress</c> keys, ending with <c>progress=continue</c> or <c>progress=end</c>.</summary>
    internal static string Keys(InputFile input, int index, int count, MediaProgress progress)
    {
        long micros = (long)Math.Round(progress.Position.Seconds * 1_000_000);
        double seconds = progress.Position.Seconds;
        StringBuilder text = new();
        text.Append(CultureInfo.InvariantCulture, $"frame={progress.Frames}\n");
        text.Append(CultureInfo.InvariantCulture, $"fps={Fps(progress) ?? 0:0.00}\n");

        // The first output video stream's quantizer, as ffmpeg writes it; -1.0 when packets are copied.
        text.Append("stream_0_0_q=-1.0\n");
        text.Append(seconds > 0 ? string.Create(CultureInfo.InvariantCulture, $"bitrate={progress.BytesWritten * 8 / seconds / 1000:0.0}kbits/s\n") : "bitrate=N/A\n");
        text.Append(CultureInfo.InvariantCulture, $"total_size={progress.BytesWritten}\n");
        text.Append(CultureInfo.InvariantCulture, $"out_time_us={micros}\n");
        text.Append(CultureInfo.InvariantCulture, $"out_time_ms={micros}\n");
        text.Append(CultureInfo.InvariantCulture, $"out_time={OutTime(micros)}\n");
        text.Append("dup_frames=0\n");
        text.Append("drop_frames=0\n");
        text.Append(progress.Speed is { } speed ? string.Create(CultureInfo.InvariantCulture, $"speed={speed:0.###}x\n") : "speed=N/A\n");
        if (count > 1)
        {
            text.Append(CultureInfo.InvariantCulture, $"file={input.Display}\nfile_index={index}\nfiles={count}\n");
        }

        text.Append(progress.IsFinal ? "progress=end\n" : "progress=continue\n");
        return text.ToString();
    }

    /// <summary>One report as a JSON object on one line.</summary>
    internal static string Json(InputFile input, int index, int count, MediaProgress progress)
    {
        StringBuilder text = new("{");
        text.Append(CultureInfo.InvariantCulture, $"\"file\":\"{Escape(input.Display)}\",\"file_index\":{index},\"files\":{count},");
        text.Append(CultureInfo.InvariantCulture, $"\"time\":{progress.Position.Seconds:0.######},");
        text.Append("\"duration\":").Append(progress.Duration is { } d ? d.Seconds.ToString("0.######", CultureInfo.InvariantCulture) : "null").Append(',');
        text.Append("\"fraction\":").Append(progress.Fraction is { } f ? f.ToString("0.####", CultureInfo.InvariantCulture) : "null").Append(',');
        text.Append(CultureInfo.InvariantCulture, $"\"frames\":{progress.Frames},\"packets\":{progress.Packets},\"bytes_read\":{progress.BytesRead},\"bytes_written\":{progress.BytesWritten},");
        text.Append(CultureInfo.InvariantCulture, $"\"elapsed\":{progress.Elapsed.TotalSeconds:0.###},");
        text.Append("\"speed\":").Append(progress.Speed is { } s ? s.ToString("0.###", CultureInfo.InvariantCulture) : "null").Append(',');
        text.Append("\"progress\":").Append(progress.IsFinal ? "\"end\"" : "\"continue\"").Append("}\n");
        return text.ToString();
    }

    private static double? Fps(MediaProgress progress) => progress.Elapsed.TotalSeconds > 0 ? progress.Frames / progress.Elapsed.TotalSeconds : null;

    private static string OutTime(long micros)
    {
        long abs = Math.Abs(micros);
        return string.Create(CultureInfo.InvariantCulture, $"{(micros < 0 ? "-" : string.Empty)}{abs / 3_600_000_000:00}:{abs / 60_000_000 % 60:00}:{abs / 1_000_000 % 60:00}.{abs % 1_000_000:000000}");
    }

    private static string Size(long bytes)
    {
        return bytes >= 1 << 30 ? string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1 << 30):0.0} GiB")
            : bytes >= 1 << 20 ? string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1 << 20):0.0} MiB")
            : string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0} KiB");
    }

    private static string Escape(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private sealed class FileProgress(ProgressSink sink, InputFile input, int index, int count) : IProgress<MediaProgress>
    {
        public void Report(MediaProgress value) => sink.Report(input, index, count, value);
    }
}
