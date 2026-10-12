using Egami.Fonts;

namespace Egami.Svg;

/// <summary>
/// Where a document's text finds its fonts (question 13): the caller's <see cref="Collection"/> first, then the fonts installed
/// on the machine unless <see cref="UseSystemFonts"/> is off, then per-character fallback through the same fonts. A server
/// that renders untrusted files should pass its own collection and turn system fonts off, so output never depends on the
/// machine.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Svg.cs" region="fonts"/>
/// </example>
public sealed record class SvgFontOptions
{
    /// <summary>Gets the defaults: no collection of the caller's, the installed fonts, Times New Roman when no family is named.</summary>
    public static SvgFontOptions Default { get; } = new();

    /// <summary>Gets the caller's fonts, searched before any other, or <see langword="null"/> for none.</summary>
    public FontCollection? Collection { get; init; }

    /// <summary>Gets a value indicating whether the fonts installed on the machine are searched after <see cref="Collection"/>. Defaults to true.</summary>
    public bool UseSystemFonts { get; init; } = true;

    /// <summary>
    /// Gets the family used when text names none, as a browser's default font; <c>Times New Roman</c> by default. When no font
    /// has it, the serif family stands in.
    /// </summary>
    public string DefaultFamily { get; init; } = "Times New Roman";

    /// <summary>
    /// Gets the family each generic name (serif, sans-serif, monospace, cursive, fantasy) stands for, or <see langword="null"/>
    /// to use the usual families of the operating system, as <see cref="FontCollection.Match(GenericFamily, FontStyle)"/> picks them.
    /// </summary>
    public IReadOnlyDictionary<GenericFamily, string>? GenericFamilies { get; init; }
}
