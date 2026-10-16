namespace Egami.Metafiles.Reading;

/// <summary>Collects what a load or a playback skipped, as warnings with Metafile.* ids, and hands them to the context.</summary>
internal sealed class MetafileReport
{
    private readonly List<EgamiDiagnostic> _warnings = [];

    /// <summary>Gets the warnings so far.</summary>
    public IReadOnlyList<EgamiDiagnostic> Warnings => _warnings;

    /// <summary>Reports something skipped or assumed.</summary>
    /// <param name="id">The id, such as <c>Metafile.TruncatedRecord</c>.</param>
    /// <param name="message">What happened and what was done, as a sentence.</param>
    /// <param name="offset">The byte offset in the (inflated) file, if known.</param>
    public void Warn(string id, string message, long? offset = null) =>
        _warnings.Add(new EgamiDiagnostic(DiagnosticLevel.Warning, "Metafile", id, message, offset, null));

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
}
