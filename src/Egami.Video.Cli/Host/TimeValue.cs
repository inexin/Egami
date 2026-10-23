using System.Globalization;

namespace Egami.Video.Cli.Host;

/// <summary>
/// A time on the command line (Video CLI design Q5): seconds (<c>90.5</c>, <c>90.5s</c>, <c>500ms</c>), <c>[HH:]MM:SS[.mmm]</c>
/// (<c>1:30</c>, <c>01:02:03.250</c>), or a frame number <c>#1234</c> counted from 0 in presentation order.
/// </summary>
internal readonly record struct TimeValue(double Seconds, long? Frame, string Text)
{
    /// <summary>Gets a value indicating whether the time is a frame number.</summary>
    public bool IsFrame => Frame is not null;

    /// <summary>Parses a time; <paramref name="option"/> names the option in the message when the text is wrong.</summary>
    public static TimeValue Parse(string text, string option)
    {
        ArgumentNullException.ThrowIfNull(text);
        string value = text.Trim();
        if (value.StartsWith('#'))
        {
            return long.TryParse(value.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out long frame)
                ? new TimeValue(0, frame, text)
                : throw Wrong(text, option);
        }

        if (value.Contains(':', StringComparison.Ordinal))
        {
            string[] parts = value.Split(':');
            if (parts.Length > 3 || parts.Any(p => p.Length == 0))
            {
                throw Wrong(text, option);
            }

            double seconds = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                bool last = i == parts.Length - 1;
                NumberStyles style = last ? NumberStyles.AllowDecimalPoint : NumberStyles.None;
                if (!double.TryParse(parts[i], style, CultureInfo.InvariantCulture, out double part) || (i > 0 && part >= 60))
                {
                    throw Wrong(text, option);
                }

                seconds = (seconds * 60) + part;
            }

            return new TimeValue(seconds, null, text);
        }

        (string number, double scale) = value.EndsWith("ms", StringComparison.OrdinalIgnoreCase) ? (value[..^2], 0.001)
            : value.EndsWith('s') || value.EndsWith('S') ? (value[..^1], 1.0)
            : value.EndsWith('m') || value.EndsWith('M') ? (value[..^1], 60.0)
            : value.EndsWith('h') || value.EndsWith('H') ? (value[..^1], 3600.0)
            : (value, 1.0);
        return double.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double plain) && double.IsFinite(plain)
            ? new TimeValue(plain * scale, null, text)
            : throw Wrong(text, option);
    }

    /// <summary>Parses a length of time, which must be more than zero and may not be a frame number unless allowed.</summary>
    public static TimeValue ParseDuration(string text, string option, bool allowFrames = true)
    {
        TimeValue value = Parse(text, option);
        if (value.IsFrame ? !allowFrames || value.Frame == 0 : value.Seconds <= 0)
        {
            throw new UsageException($"{option} '{text}' must be a length of time more than zero, such as 10, 1:30 or 500ms{(allowFrames ? ", or a frame count such as #25" : string.Empty)}.");
        }

        return value;
    }

    /// <summary>The time as a <see cref="MediaTime"/>, finding a frame number's time among <paramref name="frameTimes"/>.</summary>
    public MediaTime Resolve(Func<IReadOnlyList<MediaTime>> frameTimes, string display, string option)
    {
        if (Frame is not { } frame)
        {
            return MediaTime.FromSeconds(Seconds);
        }

        IReadOnlyList<MediaTime> times = frameTimes();
        return frame < times.Count
            ? times[(int)frame]
            : throw new UsageException($"{display}: {option} {Text} is past the last frame; the video has {times.Count} frames (#0 to #{times.Count - 1}).");
    }

    /// <summary>Writes a time as the tool prints it: <c>H:MM:SS.mmm</c>.</summary>
    public static string Format(MediaTime time) => Format(time.Seconds);

    /// <summary>Writes seconds as the tool prints them: <c>H:MM:SS.mmm</c>, with a minus sign before zero.</summary>
    public static string Format(double seconds)
    {
        string sign = seconds < 0 ? "-" : string.Empty;
        long millis = (long)Math.Round(Math.Abs(seconds) * 1000);
        return string.Create(CultureInfo.InvariantCulture, $"{sign}{millis / 3_600_000}:{millis / 60_000 % 60:00}:{millis / 1000 % 60:00}.{millis % 1000:000}");
    }

    private static UsageException Wrong(string text, string option) =>
        new($"{option} '{text}' is not a time: give seconds (90.5), [HH:]MM:SS[.mmm] (1:30, 01:02:03.250) or a frame number (#1234).");
}
