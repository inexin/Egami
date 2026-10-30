namespace Egami;

/// <summary>
/// Names the <see cref="System.Diagnostics.ActivitySource"/> through which Egami times its loads, probes, saves and transcodes,
/// for OpenTelemetry or any other <see cref="System.Diagnostics.ActivityListener"/>. Nothing is started, timed or allocated while
/// nobody listens to it.
/// </summary>
/// <remarks>
/// The activities are <c>Egami.Load</c>, <c>Egami.Probe</c>, <c>Egami.Save</c> and <c>Egami.Transcode</c>, with the tags
/// <c>egami.format</c>, <c>egami.width</c>, <c>egami.height</c>, <c>egami.frames</c>, <c>egami.images</c> and <c>egami.bytes</c>
/// where they are known. A call that fails sets the activity's status to <see cref="System.Diagnostics.ActivityStatusCode.Error"/>
/// and its <c>error.type</c> tag to the exception's type. The names and tags are never renamed once shipped, like the public API.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Diagnostics.cs" region="tracing"/>
/// </example>
public static class EgamiTracing
{
    /// <summary>The name of Egami's activity source, "Egami": pass it to OpenTelemetry's <c>AddSource</c>.</summary>
    public const string SourceName = "Egami";
}
