using Egami.Drawing.Recording;

namespace Egami.Drawing;

public sealed partial class Drawing
{
    /// <summary>
    /// Writes the drawing as an Egami drawing file (.egdr), Egami's own documented binary format: a signature and version, then
    /// chunks that a reader skips when it does not know them. Images are kept as PNG (so a float image is kept at 16 bits), paths
    /// and numbers as 32-bit floats, nested drawings once however often they are used, and text as its outlines, since a file holds
    /// no fonts. A drawing that loads back replays the same calls and draws the same pixels, except that text comes back as
    /// outline fills, which the raster backend draws as paths rather than from its glyph cache.
    /// </summary>
    /// <param name="stream">The stream to write to.</param>
    /// <exception cref="NotSupportedException">
    /// A call holds C# code a file cannot: a <see cref="CustomPaint"/>, or a layer's filter, backdrop or soft mask. The message
    /// gives the call's index. Nothing is written in that case.
    /// </exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Advanced.cs" region="egdr"/>
    /// </example>
    public void Save(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using MemoryStream buffer = new();
        EgdrWriter.Write(this, buffer);
        buffer.Position = 0;
        buffer.CopyTo(stream);
    }

    /// <summary>
    /// Reads an Egami drawing file. The file is untrusted input: its images go through the PNG reader with the context's
    /// <see cref="SafetyLimits"/>, what it decodes counts toward <see cref="SafetyLimits.MaxTotalBytes"/>, and drawings may nest 64
    /// deep and replay at most 50 million calls with nested drawings expanded.
    /// </summary>
    /// <param name="stream">The stream to read from, to its end.</param>
    /// <param name="context">The context whose limits apply, or <see langword="null"/> for the default.</param>
    /// <returns>The drawing.</returns>
    /// <exception cref="UnsupportedFormatException">The data is not an .egdr file, or is a major version this reader does not know.</exception>
    /// <exception cref="CorruptDataException">The file is damaged.</exception>
    /// <exception cref="LimitExceededException">The file is larger, or nests deeper, than the limits allow.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Advanced.cs" region="egdr"/>
    /// </example>
    public static Drawing Load(Stream stream, EgamiContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return EgdrReader.Read(stream, context ?? EgamiContext.Default);
    }
}
