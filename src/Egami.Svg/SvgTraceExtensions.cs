using System.Text;

using Egami.Drawing.Tracing;

namespace Egami.Svg;

/// <summary>Writes the shapes of a bitmap trace as SVG, through <see cref="SvgCanvas"/>.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Tracing.cs" region="svg"/>
/// </example>
public static class SvgTraceExtensions
{
    /// <summary>Returns the shapes as an SVG file of the traced image's size, one <c>path</c> element per shape, back to front.</summary>
    /// <param name="result">The trace.</param>
    /// <param name="options">How to write the file, or <see langword="null"/> for <see cref="SvgWriteOptions.Default"/>.</param>
    /// <returns>The SVG markup.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Tracing.cs" region="svg"/>
    /// </example>
    public static string ToSvg(this TraceResult result, SvgWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        using MemoryStream stream = new();
        SaveSvg(result, stream, options);
        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    /// <summary>Saves the shapes as an SVG file of the traced image's size, one <c>path</c> element per shape, back to front.</summary>
    /// <param name="result">The trace.</param>
    /// <param name="path">The file to write; it is replaced if it exists.</param>
    /// <param name="options">How to write the file, or <see langword="null"/> for <see cref="SvgWriteOptions.Default"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> or <paramref name="path"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Tracing.cs" region="svg"/>
    /// </example>
    public static void SaveSvg(this TraceResult result, string path, SvgWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(path);
        using FileStream file = File.Create(path);
        SaveSvg(result, file, options);
    }

    /// <summary>Writes the shapes as an SVG file of the traced image's size to a stream, from its current position; the stream is left open.</summary>
    /// <param name="result">The trace.</param>
    /// <param name="stream">Where the file goes.</param>
    /// <param name="options">How to write the file, or <see langword="null"/> for <see cref="SvgWriteOptions.Default"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> or <paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot be written.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Tracing.cs" region="svg"/>
    /// </example>
    public static void SaveSvg(this TraceResult result, Stream stream, SvgWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        using SvgCanvas canvas = new(stream, result.Width, result.Height, options);
        result.Draw(canvas);
    }
}
