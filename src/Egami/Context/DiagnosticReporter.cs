using System.Globalization;
using System.Runtime.CompilerServices;

namespace Egami;

/// <summary>
/// One call's link to <see cref="EgamiContext.Diagnostics"/>, with the same level filter and per-id cap Egami's own readers and
/// writers use. Get one from <see cref="EgamiContext.StartDiagnostics"/> at the start of a call (format plug-ins use
/// <see cref="Formats.EgamiInput.Diagnostics"/>) and dispose it when the call ends. When the context has no handler it is a shared
/// instance that does nothing, so reporting costs one check.
/// </summary>
/// <remarks>
/// Each id is delivered at most 10 times per reporter; disposing sends one more event per capped id that counts the rest.
/// <see cref="Report(DiagnosticLevel, string, string, long?, int?)"/> may be called from several threads at once.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Diagnostics.cs" region="plugin"/>
/// </example>
public sealed class DiagnosticReporter : IDisposable
{
    private static readonly DiagnosticReporter s_off = new(null, DiagnosticLevel.Warning);

    private readonly Action<EgamiDiagnostic>? _handler;
    private readonly DiagnosticLevel _level;
    private readonly Lock _lock = new();
    private List<Counter>? _counters;
    private Metadata.RasterMetadata? _tracked;

    private DiagnosticReporter(Action<EgamiDiagnostic>? handler, DiagnosticLevel level)
    {
        _handler = handler;
        _level = level;
    }

    /// <summary>Gets whether a handler is set, so that some events are delivered.</summary>
    public bool IsOn => _handler is not null;

    /// <summary>Gets whether events at <paramref name="level"/> are delivered. Check it before building an expensive message.</summary>
    /// <param name="level">The level of the event about to be reported.</param>
    /// <returns><see langword="true"/> if a handler is set and <paramref name="level"/> is at or above the context's level.</returns>
    public bool IsEnabled(DiagnosticLevel level) => _handler is not null && level >= _level;

    /// <summary>Reports an event, unless its level is below the context's or its id has already been delivered 10 times.</summary>
    /// <param name="level">How much the event matters.</param>
    /// <param name="id">A stable id of the form <c>Source.Name</c>, such as <c>"Video.FrameDropped"</c>.</param>
    /// <param name="message">What happened and what was done, as a sentence.</param>
    /// <param name="offset">The byte offset in the input the event is about, if known.</param>
    /// <param name="frame">The frame or page index the event is about, if known.</param>
    /// <exception cref="ArgumentException"><paramref name="id"/> is not of the form <c>Source.Name</c>, or <paramref name="message"/> is empty.</exception>
    public void Report(DiagnosticLevel level, string id, string message, long? offset = null, int? frame = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(message);
        if (id.IndexOf('.', StringComparison.Ordinal) < 1)
        {
            throw new ArgumentException($"The id '{id}' is not of the form Source.Name.", nameof(id));
        }

        Deliver(level, id, message, offset, frame);
    }

    /// <summary>Reports an event built elsewhere, unless its level is below the context's or its id is past the cap.</summary>
    /// <param name="diagnostic">The event.</param>
    /// <exception cref="ArgumentException">The event's id or message is empty.</exception>
    public void Report(EgamiDiagnostic diagnostic)
    {
        ArgumentException.ThrowIfNullOrEmpty(diagnostic.Id, nameof(diagnostic));
        ArgumentException.ThrowIfNullOrEmpty(diagnostic.Message, nameof(diagnostic));
        if (IsEnabled(diagnostic.Level) && Count(diagnostic.Id, diagnostic.Level))
        {
            _handler!(diagnostic);
        }
    }

    /// <summary>Ends the call: sends one event for each capped id, counting the events left out.</summary>
    public void Dispose()
    {
        Complete();
    }

    /// <summary>Starts a call's reporting: a new reporter, or the shared one that does nothing when the context has no handler.</summary>
    internal static DiagnosticReporter Start(EgamiContext context)
    {
        return context.Diagnostics is Action<EgamiDiagnostic> handler ? new DiagnosticReporter(handler, context.DiagnosticLevel) : s_off;
    }


    /// <summary>Delivers an event whose message is already built, counting it against its id's cap.</summary>
    internal void Deliver(DiagnosticLevel level, string id, string message, long? offset = null, int? frame = null)
    {
        if (IsEnabled(level) && Count(id, level))
        {
            _handler!(EgamiDiagnostic.Create(level, id, message, offset, frame));
        }
    }

    /// <summary>Has <see cref="Complete"/> send the Warnings of the metadata a load returns.</summary>
    internal void Track(Metadata.RasterMetadata metadata) => _tracked = metadata;

    /// <summary>
    /// Sends the tracked metadata's new Warnings, then one summary for each id that went past the cap since the last time. Safe to
    /// call more than once.
    /// </summary>
    internal void Complete()
    {
        if (_handler is null)
        {
            return;
        }

        _tracked?.WarningList.Flush();
        List<EgamiDiagnostic>? summaries = null;
        lock (_lock)
        {
            if (_counters is not null)
            {
                foreach (Counter counter in _counters)
                {
                    if (counter.More > counter.Reported)
                    {
                        (summaries ??= []).Add(WarningList.Summary(counter.Level, counter.Id, counter.More - counter.Reported));
                        counter.Reported = counter.More;
                    }
                }
            }
        }


        if (summaries is not null)
        {
            foreach (EgamiDiagnostic summary in summaries)
            {
                _handler(summary);
            }
        }
    }

    // Returns whether the event is within its id's cap; counts the rest for the summary.
    private bool Count(string id, DiagnosticLevel level)
    {
        lock (_lock)
        {
            _counters ??= [];
            foreach (Counter counter in _counters)
            {
                if (string.Equals(counter.Id, id, StringComparison.Ordinal))
                {
                    if (counter.Delivered < WarningList.RepeatCap)
                    {
                        counter.Delivered++;
                        return true;
                    }

                    counter.More++;
                    return false;
                }
            }

            _counters.Add(new Counter(id, level) { Delivered = 1 });
            return true;
        }
    }

    private sealed class Counter(string id, DiagnosticLevel level)
    {
        public string Id { get; } = id;

        public DiagnosticLevel Level { get; } = level;

        public int Delivered { get; set; }

        public int More { get; set; }

        public int Reported { get; set; }
    }
}

/// <summary>
/// Builds a diagnostic's message only when it will be delivered: the constructor sets <c>shouldAppend</c> to
/// <see langword="false"/> when there is no handler or the level is below the context's, so the compiler formats nothing.
/// </summary>
[InterpolatedStringHandler]
internal ref struct DiagnosticMessage
{
    private DefaultInterpolatedStringHandler _builder;

    /// <summary>Starts a message for <paramref name="reporter"/> at <paramref name="level"/>.</summary>
    public DiagnosticMessage(int literalLength, int formattedCount, DiagnosticReporter? reporter, DiagnosticLevel level, out bool shouldAppend)
    {
        shouldAppend = reporter is not null && reporter.IsEnabled(level);
        _builder = shouldAppend ? new DefaultInterpolatedStringHandler(literalLength, formattedCount, CultureInfo.InvariantCulture) : default;
    }

    /// <summary>Starts a message for a load's <paramref name="input"/> at <paramref name="level"/>.</summary>
    public DiagnosticMessage(int literalLength, int formattedCount, Formats.EgamiInput input, DiagnosticLevel level, out bool shouldAppend)
    {
        shouldAppend = input.Reports(level);
        _builder = shouldAppend ? new DefaultInterpolatedStringHandler(literalLength, formattedCount, CultureInfo.InvariantCulture) : default;
    }

    /// <summary>Starts a message for a call in <paramref name="context"/> at <paramref name="level"/>.</summary>
    public DiagnosticMessage(int literalLength, int formattedCount, EgamiContext context, DiagnosticLevel level, out bool shouldAppend)
    {
        shouldAppend = context.Diagnostics is not null && level >= context.DiagnosticLevel;
        _builder = shouldAppend ? new DefaultInterpolatedStringHandler(literalLength, formattedCount, CultureInfo.InvariantCulture) : default;
    }

    /// <summary>Appends literal text.</summary>
    public void AppendLiteral(string value) => _builder.AppendLiteral(value);

    /// <summary>Appends a value.</summary>
    public void AppendFormatted<T>(T value) => _builder.AppendFormatted(value);

    /// <summary>Appends a value with a format.</summary>
    public void AppendFormatted<T>(T value, string? format) => _builder.AppendFormatted(value, format);

    /// <summary>Appends text.</summary>
    public void AppendFormatted(ReadOnlySpan<char> value) => _builder.AppendFormatted(value);

    /// <summary>Appends text.</summary>
    public void AppendFormatted(string? value) => _builder.AppendFormatted(value);

    /// <summary>Returns the message built.</summary>
    internal string ToStringAndClear() => _builder.ToStringAndClear();
}

/// <summary>Egami's own reporting, which builds a message only when it will be delivered.</summary>
internal static class DiagnosticReporting
{
    /// <summary>
    /// Reports an event of a call in <paramref name="context"/>, formatting <paramref name="message"/> only when it is delivered and
    /// starting the call's <paramref name="reporter"/> only then, so a call that reports nothing allocates nothing.
    /// </summary>
    internal static void Emit(
        this EgamiContext context,
        ref DiagnosticReporter? reporter,
        DiagnosticLevel level,
        string id,
        [InterpolatedStringHandlerArgument("context", "level")] ref DiagnosticMessage message,
        long? offset = null,
        int? frame = null)
    {
        if (context.Diagnostics is not null && level >= context.DiagnosticLevel)
        {
            (reporter ??= DiagnosticReporter.Start(context)).Deliver(level, id, message.ToStringAndClear(), offset, frame);
        }
    }

    /// <summary>Reports an event of a load, formatting <paramref name="message"/> only when it is delivered.</summary>
    internal static void Emit(
        this Formats.EgamiInput input,
        DiagnosticLevel level,
        string id,
        [InterpolatedStringHandlerArgument("input", "level")] ref DiagnosticMessage message,
        long? offset = null,
        int? frame = null)
    {
        if (input.Reports(level))
        {
            input.Diagnostics.Deliver(level, id, message.ToStringAndClear(), offset, frame);
        }
    }

    /// <summary>Reports an event, formatting <paramref name="message"/> only when the reporter delivers <paramref name="level"/>.</summary>
    internal static void Emit(
        this DiagnosticReporter? reporter,
        DiagnosticLevel level,
        string id,
        [InterpolatedStringHandlerArgument("reporter", "level")] ref DiagnosticMessage message,
        long? offset = null,
        int? frame = null)
    {
        if (reporter is not null && reporter.IsEnabled(level))
        {
            reporter.Deliver(level, id, message.ToStringAndClear(), offset, frame);
        }
    }
}
