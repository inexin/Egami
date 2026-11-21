namespace Egami.Drawing.Tracing;

/// <summary>How the color layers of a grayscale or color trace fit together.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Tracing.cs" region="color"/>
/// </example>
public enum TraceLayering
{
    /// <summary>
    /// Each layer also covers the layers above it, largest color first, so no gap shows between colors when the shapes are
    /// drawn antialiased. The default.
    /// </summary>
    Stacked,

    /// <summary>
    /// Each shape covers only its own color's pixels, and neighbouring shapes share one fitted edge, so the shapes never overlap
    /// and are easy to edit one by one.
    /// </summary>
    Abutting,
}
