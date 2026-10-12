namespace Egami.Svg.Reading;

/// <summary>
/// Collects what a load skipped. In strict mode the first problem throws instead. Warnings are delivered to the context's
/// diagnostics when the load ends, and only when it succeeds, as Egami's other readers do.
/// </summary>
internal sealed class SvgReport
{
    private readonly List<EgamiDiagnostic> _warnings = [];

    public SvgReport(bool strict) => Strict = strict;

    /// <summary>Gets a value indicating whether problems throw.</summary>
    public bool Strict { get; }

    /// <summary>Gets the warnings so far.</summary>
    public IReadOnlyList<EgamiDiagnostic> Warnings => _warnings;

    /// <summary>Reports something skipped; throws <see cref="SvgException"/> in strict mode.</summary>
    /// <param name="id">The id, such as <c>Svg.BadAttribute</c>.</param>
    /// <param name="message">What happened and what was done, as a sentence.</param>
    /// <param name="at">The item it is about, for the line and column, if known.</param>
    public void Warn(string id, string message, SvgContent? at = null)
    {
        int line = at?.Line ?? 0;
        int column = at?.Column ?? 0;
        if (Strict)
        {
            throw new SvgException(line, column, message);
        }

        Add(id, message, line, column);
    }

    /// <summary>Reports something that is worth knowing but not an error, even in strict mode (a default size, say).</summary>
    public void Note(string id, string message, SvgContent? at = null) => Add(id, message, at?.Line ?? 0, at?.Column ?? 0);

    /// <summary>Sends the warnings to the context's diagnostics handler.</summary>
    public void Deliver(EgamiContext context)
    {
        if (_warnings.Count == 0 || context.Diagnostics is null)
        {
            return;
        }

        using DiagnosticReporter reporter = context.StartDiagnostics();
        foreach (EgamiDiagnostic warning in _warnings)
        {
            reporter.Report(warning);
        }
    }

    private void Add(string id, string message, int line, int column)
    {
        string text = line > 0 ? $"Line {line}, column {column}: {message}" : message;
        _warnings.Add(new EgamiDiagnostic(DiagnosticLevel.Warning, "Svg", id, text, null, null));
    }
}
