namespace Egami.Video.Subtitles;

/// <summary>
/// The diagnostics of one read or write: <c>Subtitle.Repaired</c> for a fixed line (or an exception when reading strictly),
/// <c>Subtitle.Encoding</c> for a guessed encoding, and <c>Subtitle.Dropped</c> once per kind of lost feature, naming the first
/// cue that had it.
/// </summary>
internal sealed class SubtitleLog : IDisposable
{
    private readonly DiagnosticReporter _reporter;
    private readonly bool _strict;
    private HashSet<string>? _dropped;

    public SubtitleLog(MediaContext? context, string formatName, bool strict = false)
    {
        _reporter = (context ?? MediaContext.Default).Egami.StartDiagnostics();
        FormatName = formatName;
        _strict = strict;
    }

    public string FormatName { get; set; }

    /// <summary>Reports a repair at a 1-based line, or throws when reading strictly.</summary>
    public void Repaired(int line, string message)
    {
        if (_strict)
        {
            throw new CorruptDataException(FormatName, -1, $"Line {line}: {message}");
        }

        if (_reporter.IsEnabled(DiagnosticLevel.Warning))
        {
            _reporter.Report(DiagnosticLevel.Warning, "Subtitle.Repaired", $"{FormatName} line {line}: {message}");
        }
    }

    /// <summary>Throws for something even lenient reading cannot get past.</summary>
    public CorruptDataException Corrupt(int line, string message) => new(FormatName, -1, line > 0 ? $"Line {line}: {message}" : message);

    public void Encoding(string message)
    {
        if (_reporter.IsEnabled(DiagnosticLevel.Warning))
        {
            _reporter.Report(DiagnosticLevel.Warning, "Subtitle.Encoding", message);
        }
    }

    /// <summary>Reports, once per kind, that the target format has no way to show <paramref name="what"/>.</summary>
    /// <param name="what">The feature, as a plural noun phrase: "voices", "ASS override tags".</param>
    /// <param name="cue">The 1-based number of the first cue that had it, or 0 for the document.</param>
    public void Dropped(string what, int cue)
    {
        if ((_dropped ??= []).Add(what) && _reporter.IsEnabled(DiagnosticLevel.Warning))
        {
            string where = cue > 0 ? $", first in cue {cue}" : "";
            _reporter.Report(DiagnosticLevel.Warning, "Subtitle.Dropped", $"{FormatName} cannot store {what}{where}; they were left out.");
        }
    }

    public void Dispose() => _reporter.Dispose();
}
